// =============================================================================
// NextScan Studio - what is this page?
// Plan ref: docs/AI_LAYER.md
//
// Every page that is scanned, and every picture attached to a message, is looked
// at once by a vision model and described in a few typed fields: what kind of
// document it is, in what language, whether any of it is handwritten, whether it
// has pictures on it (a logo, a photograph, a stamp), whether it is the right way
// up and how good the scan is. That is shown on the page's card in the strip,
// told to the assistant with every turn, and used to choose who answers:
//
//   * a page that is mostly handwriting is read by Gemini, which reads it better
//     than the others (and the operator's own choice of model is kept for
//     everything else);
//   * a printed page that has some handwriting on it (a filled-in form) stays with
//     the model the operator chose, which is given a tool, transcribe_scan, that
//     has Gemini read the handwritten parts.
//
// The look itself is the cheap, fast tier of whichever provider can see (Gemini
// first), because telling a bill from a letter does not need the largest model.
// What it says is facts about the page, never what is written on it: no names,
// numbers or addresses leave this class except in the picture sent to the model
// the operator has already chosen to trust with their scans.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NextScan.Ai;
using NextScan.Core;

namespace NextScan.App
{
    /// <summary>What was seen on a page.</summary>
    public class ScanInfo
    {
        /// <summary>bill, receipt, bank_slip, form, id_card, passport, certificate, letter, prescription, table, handwritten_note, book_page, photo, drawing or other.</summary>
        public string Kind = "other";

        /// <summary>The kind in plain words, as the model put it: "Bank deposit slip".</summary>
        public string Name = "";

        /// <summary>The document's own name, as printed on it or as it would be filed: "Islami Bank - SMS banking and conditions".</summary>
        public string Title = "";

        public List<string> Languages = new List<string>();

        /// <summary>none, some (a printed page with handwriting on it) or mostly (what is to be read is handwritten).</summary>
        public string Handwriting = "none";

        public bool Printed = true;

        /// <summary>A logo, photograph, stamp or signature is on it.</summary>
        public bool Pictures;

        /// <summary>upright, rotated_90_cw, rotated_180 or rotated_90_ccw.</summary>
        public string Orientation = "upright";

        /// <summary>What is wrong with the scan, if anything: "blurred", "cut off at the right", "skewed".</summary>
        public string Quality = "";

        /// <summary>One line on what this page is, with no names or numbers.</summary>
        public string Summary = "";

        public double Confidence;

        /// <summary>The model that looked.</summary>
        public string Via = "";

        public bool Ok;
        public string Error = "";

        public bool MostlyHandwriting { get { return Ok && Handwriting == "mostly"; } }
        public bool SomeHandwriting { get { return Ok && Handwriting == "some"; } }

        /// <summary>The label for the page's card and for the status line: "Bank deposit slip · handwriting".</summary>
        public string Label
        {
            get
            {
                if (!Ok) return "";
                string name = Name.Length > 0 ? Name : Pretty(Kind);
                if (Handwriting == "mostly") return name + " · handwritten";
                if (Handwriting == "some") return name + " · some handwriting";
                return name;
            }
        }

        /// <summary>For the assistant's context: the facts, in a line.</summary>
        public string ForAssistant()
        {
            if (!Ok) return "";
            var s = new StringBuilder();
            s.Append(Name.Length > 0 ? Name : Pretty(Kind)).Append(" (").Append(Kind).Append(')');
            if (Languages.Count > 0) s.Append("; language: ").Append(string.Join(", ", Languages.ToArray()));
            s.Append("; handwriting: ").Append(Handwriting);
            if (Pictures) s.Append("; has pictures (logo, photograph, stamp or signature)");
            if (Orientation != "upright") s.Append("; the scan is turned (").Append(Orientation).Append(')');
            if (Quality.Length > 0) s.Append("; scan quality: ").Append(Quality);
            return s.ToString();
        }

        public static string Pretty(string kind)
        {
            switch ((kind ?? "").ToLowerInvariant())
            {
                case "bank_slip": return "Bank slip";
                case "id_card": return "ID card";
                case "handwritten_note": return "Handwritten note";
                case "book_page": return "Book page";
                case "": return "Page";
                default: return char.ToUpperInvariant(kind[0]) + kind.Substring(1).Replace('_', ' ');
            }
        }
    }

    public class ScanKinds
    {
        readonly Func<StudioSettings> _settings;
        readonly Func<string> _panelProvider;
        readonly Func<string> _panelModel;
        readonly SynchronizationContext _ui;
        readonly SemaphoreSlim _gate = new SemaphoreSlim(4);

        class Entry { public Task<ScanInfo> Task; public DateTime At = DateTime.Now; }
        readonly ConditionalWeakTable<RawImage, Entry> _pages = new ConditionalWeakTable<RawImage, Entry>();
        readonly Dictionary<string, Entry> _pictures = new Dictionary<string, Entry>();

        /// <summary>A page's description has arrived (or failed), on the UI thread. The key is the RawImage or the picture's hash.</summary>
        public event Action<object, ScanInfo> Changed;

        public ScanKinds(Func<StudioSettings> settings, Func<string> panelProvider, Func<string> panelModel = null)
        {
            _settings = settings;
            _panelProvider = panelProvider;
            _panelModel = panelModel;
            _ui = SynchronizationContext.Current;
        }

        public bool Enabled { get { return _settings().ScanRecognise; } }

        /// <summary>Handwriting goes to Gemini when the operator allows it and a Gemini key is set.</summary>
        public bool HandwritingToGemini { get { return _settings().HandwritingGemini && Gemini() != null; } }

        public IAiProvider Gemini()
        {
            IAiProvider g = AiProviders.ById("gemini");
            return g != null && g.Ready ? g : null;
        }

        // =====================================================================
        // Looking
        // =====================================================================

        /// <summary>What is known of a scanned page now, or null: not asked, or still being looked at.</summary>
        public ScanInfo Peek(RawImage page)
        {
            Entry e;
            if (page == null || !_pages.TryGetValue(page, out e) || !e.Task.IsCompleted || e.Task.IsFaulted) return null;
            ScanInfo info = e.Task.Result;
            return info != null && info.Ok ? info : null;
        }

        /// <summary>Why a page could not be recognised, or "" when it has been, is being looked at, or was not asked.</summary>
        public string Failure(RawImage page)
        {
            Entry e;
            if (page == null || !_pages.TryGetValue(page, out e) || !e.Task.IsCompleted) return "";
            if (e.Task.IsFaulted || e.Task.IsCanceled) return "it could not be looked at";
            ScanInfo info = e.Task.Result;
            return info != null && !info.Ok ? (info.Error.Length > 0 ? info.Error : "it could not be looked at") : "";
        }

        /// <summary>Whether a page is being looked at now.</summary>
        public bool Pending(RawImage page)
        {
            Entry e;
            return page != null && _pages.TryGetValue(page, out e) && !e.Task.IsCompleted;
        }

        /// <summary>Looks again at a page that could not be recognised.</summary>
        public void Again(RawImage page)
        {
            if (page == null) return;
            lock (_pages) { _pages.Remove(page); }
            Request(page);
        }

        /// <summary>Starts looking at a scanned page, if it is not being looked at already.</summary>
        public void Request(RawImage page)
        {
            if (Enabled && page != null && page.IsValid) { var ignored = InfoAsync(page); }
        }

        public Task<ScanInfo> InfoAsync(RawImage page)
        {
            if (page == null || !page.IsValid) return Task.FromResult(new ScanInfo { Error = "No page." });
            Entry e;
            if (_pages.TryGetValue(page, out e) && !(e.Task.IsCompleted && Failed(e.Task) && (DateTime.Now - e.At).TotalSeconds > 30)) return e.Task;

            var entry = new Entry();
            entry.Task = Task.Run(async () =>
            {
                byte[] jpeg;
                using (Bitmap bmp = page.ToBitmap()) jpeg = AttachReader.Jpeg(bmp, 800);
                ScanInfo info = await Look(jpeg).ConfigureAwait(false);
                Raise(page, info);
                return info;
            });
            lock (_pages) { _pages.Remove(page); _pages.Add(page, entry); }
            return entry.Task;
        }

        /// <summary>The same for a picture that is not a scanned page: one attached to a message.</summary>
        public Task<ScanInfo> InfoAsync(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return Task.FromResult(new ScanInfo { Error = "No picture." });
            string key = Hash(jpeg);
            lock (_pictures)
            {
                Entry e;
                if (_pictures.TryGetValue(key, out e) && !(e.Task.IsCompleted && Failed(e.Task) && (DateTime.Now - e.At).TotalSeconds > 30)) return e.Task;
                var entry = new Entry();
                entry.Task = Task.Run(async () =>
                {
                    ScanInfo info = await Look(jpeg).ConfigureAwait(false);
                    Raise(key, info);
                    return info;
                });
                _pictures[key] = entry;
                return entry.Task;
            }
        }

        static bool Failed(Task<ScanInfo> t) { return t.IsFaulted || t.IsCanceled || (t.Result != null && !t.Result.Ok); }

        static string Hash(byte[] bytes)
        {
            using (var sha = SHA1.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }

        void Raise(object key, ScanInfo info)
        {
            Action<object, ScanInfo> handler = Changed;
            if (handler == null) return;
            if (_ui != null) _ui.Post(delegate { handler(key, info); }, null);
            else handler(key, info);
        }

        const string Question =
            "This is one scanned page or photograph of a document, from a print and copy shop in Bangladesh. It may be a preview " +
            "of the whole scanner glass with the document lying on part of it: describe the document, never the empty glass. Say what it is. " +
            "Answer with JSON only, no other text, exactly these fields:\n" +
            "{\"kind\": one of \"bill\", \"receipt\", \"bank_slip\", \"form\", \"id_card\", \"passport\", \"certificate\", \"letter\", " +
            "\"prescription\", \"table\", \"handwritten_note\", \"book_page\", \"photo\", \"drawing\", \"other\",\n" +
            " \"name\": the kind in two to four plain words, e.g. \"Bank deposit slip\" or \"Handwritten letter\",\n" +
            " \"title\": what to call this document, two to six words, in the language it is mostly in: the heading printed on it if it has one (the bank's or office's name and what the paper is), otherwise a short description. No personal names or numbers,\n" +
            " \"languages\": [\"bengali\", \"english\", ...] -- the languages of the text on it, most used first,\n" +
            " \"handwriting\": \"none\", \"some\" or \"mostly\",\n" +
            " \"pictures\": true if there is a logo, photograph, stamp, seal or signature,\n" +
            " \"orientation\": \"upright\", \"rotated_90_cw\", \"rotated_180\" or \"rotated_90_ccw\" -- how the page is turned now,\n" +
            " \"quality\": \"\" if the scan is good, otherwise what is wrong in a few words (blurred, cut off, skewed, shadow, faint)}\n\n" +
            "Handwriting: \"mostly\" means what a reader would need to read is handwritten (a handwritten letter, notes, an exam " +
            "script, a prescription written by hand); \"some\" means a printed page with handwriting on it (a printed form filled in " +
            "by hand, a printed page with notes in the margin); \"none\" means no handwriting. A signature on its own is not " +
            "handwriting; say pictures: true for it. Look at the letters themselves, in whatever script.";

        // A provider that has just failed (no quota, an empty answer, too slow) is left alone for a while, so that
        // every page does not wait to be refused again.
        static readonly Dictionary<string, DateTime> Resting = new Dictionary<string, DateTime>();

        static bool IsResting(string providerId) { lock (Resting) { DateTime until; return Resting.TryGetValue(providerId, out until) && until > DateTime.Now; } }

        static void Rest(string providerId, TimeSpan how) { lock (Resting) Resting[providerId] = DateTime.Now + how; }

        /// <summary>
        /// Looks at the picture with the providers that can see, never waiting long on one: the first (Gemini's fast
        /// model, as AI crop does) is asked at once; if it has not answered in five seconds the next is asked as well,
        /// and so on, and the first usable answer wins. A provider that fails -- no quota left, an empty answer, too
        /// slow -- is left alone for a while. So a page is recognised in about the time the quickest of them takes, and
        /// one key running out costs nothing but its own turn.
        /// </summary>
        async Task<ScanInfo> Look(byte[] jpeg)
        {
            var info = new ScanInfo();
            StudioSettings settings = _settings();
            var overall = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                var order = new List<IAiProvider>();
                IAiProvider first = AiCrop.Looker(settings.ScanRecogniseVia, _panelProvider());
                if (first != null) order.Add(first);
                IAiProvider panel = AiProviders.ById(_panelProvider() ?? "");
                if (panel != null && panel.Ready && !order.Contains(panel)) order.Add(panel);
                foreach (string id in new[] { "gemini", "claude", "openai" })
                {
                    IAiProvider p = AiProviders.ById(id);
                    if (p != null && p.Ready && !order.Contains(p)) order.Add(p);
                }
                if (order.Count == 0) { info.Error = "No AI provider that can see pictures is set up."; return info; }

                // Those that are resting go to the back, not out: when nothing else is left they are tried.
                var rested = order.FindAll(p => IsResting(p.Info.Id));
                order.RemoveAll(p => rested.Contains(p));
                order.AddRange(rested);

                var errors = new List<string>();
                var pending = new List<Task<ScanInfo>>();
                int started = 0;
                Func<bool> startNext = delegate
                {
                    if (started >= order.Count) return false;
                    pending.Add(Attempt(order[started++], first, panel, jpeg, overall.Token));
                    return true;
                };
                startNext();

                while (pending.Count > 0)
                {
                    Task hedge = started < order.Count ? Task.Delay(5000, overall.Token) : null;
                    var waiting = new List<Task>(pending.ToArray());
                    if (hedge != null) waiting.Add(hedge);
                    Task finished = await Task.WhenAny(waiting).ConfigureAwait(false);
                    if (finished == hedge) { startNext(); continue; }

                    var done = (Task<ScanInfo>)finished;
                    pending.Remove(done);
                    ScanInfo got = done.IsFaulted || done.IsCanceled ? new ScanInfo { Error = "no answer" } : done.Result;
                    if (got.Ok) { overall.Cancel(); return got; }
                    errors.Add(got.Error);
                    if (pending.Count == 0) startNext();
                }
                info.Error = errors.Count > 0 ? errors[errors.Count - 1] : "The page could not be looked at.";
            }
            catch (OperationCanceledException) { info.Error = "Looking at the page took too long."; }
            catch (Exception ex) { info.Error = ex.GetBaseException().Message; }
            return info;
        }

        /// <summary>One provider's try: its fast model, one request, no retries (the next provider is the retry), a time limit.</summary>
        async Task<ScanInfo> Attempt(IAiProvider looker, IAiProvider first, IAiProvider panel, byte[] jpeg, CancellationToken overall)
        {
            var info = new ScanInfo();
            var own = CancellationTokenSource.CreateLinkedTokenSource(overall);
            own.CancelAfter(TimeSpan.FromSeconds(28));
            bool gate = false;
            try
            {
                StudioSettings settings = _settings();
                string model;
                if (looker == panel && looker != first)
                {
                    // The operator's own model: used if it is not known to be blind.
                    model = _panelModel == null ? "" : _panelModel();
                    if (string.IsNullOrEmpty(model)) model = AiModels.Default(looker, AiModels.Cached(looker));
                    IList<AiModel> listed = AiModels.Cached(looker);
                    if (listed != null) foreach (AiModel m in listed) if (m.Id == model && m.Vision == false) model = "";
                }
                else model = await AiCrop.Model(looker, looker == first ? settings.ScanRecogniseVia : "", "", "", own.Token).ConfigureAwait(false);
                if (string.IsNullOrEmpty(model)) { info.Error = looker.Info.Name + " has no model to look with"; return info; }

                await _gate.WaitAsync(own.Token).ConfigureAwait(false);
                gate = true;
                var request = new AiRequest { Model = model, Thinking = ThinkingLevel.Low, MaxOutputTokens = 1500, Instruction = "You describe scanned documents and answer with JSON only." };
                AiMessage turn = AiMessage.FromUser(Question);
                turn.Image = jpeg;
                turn.ImageMediaType = "image/jpeg";
                request.Messages.Add(turn);
                AiReply reply = await looker.Ask(request, null, own.Token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(reply.Trouble)) { info.Error = reply.Trouble; Rest(looker.Info.Id, TimeSpan.FromMinutes(2)); return info; }
                if (string.IsNullOrWhiteSpace(reply.Text)) { info.Error = looker.Info.Name + " gave no answer"; Rest(looker.Info.Id, TimeSpan.FromMinutes(2)); return info; }
                Parse(reply.Text, info);
                if (info.Ok) info.Via = looker.Info.Name + " " + model; else Rest(looker.Info.Id, TimeSpan.FromMinutes(1));
            }
            catch (OperationCanceledException)
            {
                if (!overall.IsCancellationRequested) { info.Error = looker.Info.Name + " was too slow"; Rest(looker.Info.Id, TimeSpan.FromMinutes(2)); }
                else info.Error = "stopped";
            }
            catch (Exception ex)
            {
                info.Error = ex.GetBaseException().Message ?? "failed";
                Rest(looker.Info.Id, Permanent(ex) ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1));
            }
            finally { if (gate) _gate.Release(); own.Dispose(); }
            return info;
        }

        static async Task<AiReply> Retry(IAiProvider provider, AiRequest request, CancellationToken cancel)
        {
            int[] waits = { 1500, 5000 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    AiReply reply = await provider.Ask(request, null, cancel).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(reply.Text) || !string.IsNullOrEmpty(reply.Trouble) || attempt >= waits.Length) return reply;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { if (attempt >= waits.Length || cancel.IsCancellationRequested || Permanent(ex)) throw; }
                await Task.Delay(waits[attempt], cancel).ConfigureAwait(false);
            }
        }

        /// <summary>A refusal that trying again will not change: no quota for this model, it is gone, the key is not allowed it.</summary>
        static bool Permanent(Exception ex)
        {
            string m = (ex.GetBaseException().Message ?? "").ToLowerInvariant();
            return m.Contains("limit: 0") || m.Contains("exceeded your current quota") || m.Contains("no longer available") ||
                   m.Contains("not found") || m.Contains("permission") || m.Contains("api key") || m.Contains("not supported");
        }

        /// <summary>Reads the model's JSON into the fields, forgivingly about fences, a sentence before it and missing fields.</summary>
        public static void Parse(string text, ScanInfo info)
        {
            string s = (text ?? "").Trim();
            Match fenced = Regex.Match(s, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline);
            if (fenced.Success) s = fenced.Groups[1].Value.Trim();
            int start = s.IndexOf('{'), end = s.LastIndexOf('}');
            if (start < 0 || end < start) { info.Error = "The model did not describe the page."; return; }

            Dictionary<string, object> d;
            try { d = new JavaScriptSerializer().DeserializeObject(s.Substring(start, end - start + 1)) as Dictionary<string, object>; }
            catch { info.Error = "The model's description could not be read."; return; }
            if (d == null) { info.Error = "The model's description could not be read."; return; }

            info.Kind = Text(d, "kind", "other").ToLowerInvariant().Replace(' ', '_');
            info.Name = Text(d, "name", "");
            info.Title = Text(d, "title", "");
            info.Summary = Text(d, "summary", "");
            info.Quality = Text(d, "quality", "");
            string hw = Text(d, "handwriting", "none").ToLowerInvariant();
            info.Handwriting = hw.StartsWith("most", StringComparison.Ordinal) || hw == "all" || hw == "yes" ? "mostly" : hw.StartsWith("some", StringComparison.Ordinal) ? "some" : "none";
            string orient = Text(d, "orientation", "upright").ToLowerInvariant();
            info.Orientation = orient.Contains("180") ? "rotated_180" : orient.Contains("ccw") || orient.Contains("270") ? "rotated_90_ccw" : orient.Contains("90") ? "rotated_90_cw" : "upright";
            info.Printed = Flag(d, "printed", true);
            info.Pictures = Flag(d, "pictures", false);
            object conf;
            if (d.TryGetValue("confidence", out conf) && conf != null) { double c; if (double.TryParse(Convert.ToString(conf, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out c)) info.Confidence = Math.Max(0, Math.Min(1, c)); }
            object langs;
            if (d.TryGetValue("languages", out langs) && langs is System.Collections.IEnumerable && !(langs is string))
                foreach (object l in (System.Collections.IEnumerable)langs) { string name = Convert.ToString(l, CultureInfo.InvariantCulture).Trim(); if (name.Length > 0 && info.Languages.Count < 4) info.Languages.Add(name.ToLowerInvariant()); }
            info.Ok = true;
        }

        static string Text(Dictionary<string, object> d, string key, string fallback)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture).Trim() : fallback;
        }

        static bool Flag(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant();
            return s == "true" || s == "yes" || s == "1";
        }

        // =====================================================================
        // Reading handwriting
        // =====================================================================

        // Models that failed for this key (a free key has no quota for the largest ones: "limit: 0"; a model can also
        // be withdrawn): not tried again this session, so the second page does not pay for the first one's discovery.
        static readonly HashSet<string> Failed_ = new HashSet<string>();

        public static void MarkFailed(string model) { if (!string.IsNullOrEmpty(model)) lock (Failed_) Failed_.Add(model); }

        static bool IsFailed(string model) { lock (Failed_) return Failed_.Contains(model); }

        /// <summary>
        /// The Gemini models to read handwriting with, best first: the operator's choice, then the strongest on the
        /// account, then its fast one -- which reads handwriting well and is the one a free key can use.
        /// Those that have failed this session are left out, unless nothing else is left.
        /// </summary>
        public async Task<List<string>> HandwritingCandidates(IAiProvider gemini, CancellationToken cancel)
        {
            var all = new List<string>();
            string chosen = _settings().HandwritingModel;
            if (!string.IsNullOrEmpty(chosen)) all.Add(chosen);
            try
            {
                IList<AiModel> list = await AiModels.Fetch(gemini, false, cancel).ConfigureAwait(false);
                string strong = AiModels.Default(gemini, list);
                if (!string.IsNullOrEmpty(strong) && !all.Contains(strong)) all.Add(strong);
                string fast = await JevSorter.ReaderModel(gemini, "", cancel).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(fast) && !all.Contains(fast)) all.Add(fast);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            var usable = all.FindAll(m => !IsFailed(m));
            return usable.Count > 0 ? usable : all;
        }

        /// <summary>The Gemini model that reads handwriting now.</summary>
        public async Task<string> HandwritingModel(IAiProvider gemini, CancellationToken cancel)
        {
            List<string> all = await HandwritingCandidates(gemini, cancel).ConfigureAwait(false);
            return all.Count > 0 ? all[0] : "";
        }

        /// <summary>That model failed: the next one to try, or null when there is none.</summary>
        public async Task<string> NextHandwritingModel(string failed, CancellationToken cancel)
        {
            MarkFailed(failed);
            IAiProvider gemini = Gemini();
            if (gemini == null) return null;
            List<string> all = await HandwritingCandidates(gemini, cancel).ConfigureAwait(false);
            foreach (string m in all) if (m != failed && !IsFailed(m)) return m;
            return null;
        }

        /// <summary>A note that starts with this asks for all the text on the picture, not only the handwriting.</summary>
        public const string AllText = "\u0001ALL\u0001";

        /// <summary>
        /// Has Gemini read the handwriting on a picture: the words as written, line by line, with what it
        /// cannot read marked rather than guessed. <paramref name="note"/> may say where to look or what language.
        /// </summary>
        public async Task<string> Transcribe(byte[] jpeg, string note, CancellationToken cancel)
        {
            IAiProvider gemini = Gemini();
            if (gemini == null) throw new InvalidOperationException("Gemini has no key on this computer; it is what reads handwriting.");
            List<string> models = await HandwritingCandidates(gemini, cancel).ConfigureAwait(false);
            if (models.Count == 0) throw new InvalidOperationException("Gemini has no model to read with.");

            var request = new AiRequest
            {
                Model = models[0],
                Thinking = ThinkingLevel.Medium,
                MaxOutputTokens = 8000,
                Instruction = "You read handwriting, in Bengali and English and mixtures of them, more carefully than anyone, and write down exactly what is there.",
            };
            bool all = (note ?? "").StartsWith(AllText, StringComparison.Ordinal);
            if (all) note = note.Substring(AllText.Length);
            string ask = all
              ? "Transcribe ALL the text in this picture, exactly.\n" +
                "- Every word, number and digit as written or printed, in the script it is in (Bengali stays Bengali, English stays English); do not translate, correct, complete or summarise anything.\n" +
                "- In reading order, with the lines and paragraphs as they are on the page. A table: one line per row, the cells separated by ' | '. A form field: its label, then its value.\n" +
                "- Punctuation, brackets and the numbering of lists exactly as they are (1. 2. 3. or a) b) c)).\n" +
                "- Handwriting among the print: write it in {curly braces}. Where something cannot be read with certainty write your best reading followed by [?]; where nothing can be read write [illegible]. Never fill a gap from what such a page usually says.\n" +
                "- Leave out logos and pictures. Do not describe the page.\n" +
                "Answer with the transcription only."
              :
                "Transcribe the handwriting in this picture.\n" +
                "- Write the words exactly as written, in the script they are written in (Bengali stays Bengali, English stays English); do not translate, correct or complete them.\n" +
                "- Keep the lines and paragraphs as they are on the page, in reading order.\n" +
                "- Where a word or number cannot be read with certainty write your best reading followed by [?]; where nothing can be read write [illegible]. Never fill a gap from what such a page would usually say.\n" +
                "- Digits: copy them exactly; Bengali digits stay Bengali digits.\n" +
                "- Printed text on the page (headings, labels, form fields) is not handwriting: leave it out, except as a short label in square brackets where it tells which field a handwritten value belongs to, e.g. [Name:] Rahim.\n" +
                "- Crossed-out words: write them in ~~double tildes~~. Underlined words are plain.\n" +
                "Answer with the transcription only.";
            if (!string.IsNullOrWhiteSpace(note)) ask += "\n\nThe operator says: " + note.Trim();
            AiMessage turn = AiMessage.FromUser(ask);
            turn.Image = jpeg;
            turn.ImageMediaType = "image/jpeg";
            request.Messages.Add(turn);

            // Each model in turn: one that is refused (no quota, withdrawn) is remembered and the next is tried.
            Exception last = null;
            foreach (string model in models)
            {
                request.Model = model;
                try
                {
                    AiReply reply = await Retry(gemini, request, cancel).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(reply.Trouble)) throw new InvalidOperationException(reply.Trouble);
                    if (!string.IsNullOrWhiteSpace(reply.Text)) return reply.Text.Trim();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; MarkFailed(model); }
            }
            if (last != null) throw last;
            return "";
        }
    }
}
