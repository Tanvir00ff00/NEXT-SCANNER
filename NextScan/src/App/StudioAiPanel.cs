// =============================================================================
// NextScan Studio - the assistant panel
// Plan ref: docs/AI_LAYER.md.
//
// The right-hand panel, when the rail is on Assist: a transcript, a row of
// one-press actions above it, and a box to type in below. It is the only file
// in the application that names NextScan.Ai, which is what keeps the provider
// SDKs out of the shell.
//
// Two decisions are worth stating because they are not obvious from the code.
//
// The model is sent the WHOLE page, never a crop. That was the owner's
// correction to an earlier design and it was right: a model handed a rectangle
// cut out of a form does not know it is looking at a form, and answers about
// the rectangle. Measurements are a different job and are not asked of a model
// at all.
//
// And nothing is sent until the operator presses something. There is no pass
// over the page when the panel opens, no background call while they are looking
// at it. Every call on this panel costs the shop money, and a cost they did not
// ask for is one they cannot plan for.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using NextScan.Ai;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioAiPanel : Panel
    {
        // ---- what the shell hands over -------------------------------------

        /// <summary>The page being talked about, or null when there is none.</summary>
        public Func<RawImage> PageSource;

        /// <summary>Where that page came from, for the line under the transcript.</summary>
        public Func<string> PageNote;

        /// <summary>The shell's status line.</summary>
        public Action<string> Status;

        /// <summary>Raised when the provider, model or thinking level changes here.</summary>
        public event EventHandler ChoiceChanged;

        // ---- state ---------------------------------------------------------

        readonly List<AiMessage> _history = new List<AiMessage>();
        readonly List<NsBubble> _bubbles = new List<NsBubble>();

        Panel _actions;
        Panel _scroll;
        Panel _column;
        NsTextBox _composer;
        NsIconButton _send;
        Label _meter;
        Label _hint;
        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();
        readonly ToolTip _tips = new ToolTip();
        readonly Dictionary<string, NsIconButton> _actionButtons = new Dictionary<string, NsIconButton>();

        CancellationTokenSource _cancel;
        NsBubble _live;
        readonly StringBuilder _streamed = new StringBuilder();
        bool _busy;

        string _providerId = "claude";
        string _model = "";
        ThinkingLevel _thinking = ThinkingLevel.Medium;

        /// <summary>
        /// Which page this conversation is about, as the operator would name
        /// it. Kept so the panel can say so when the page underneath changes.
        /// </summary>
        string _pageFor = "";

        // =====================================================================
        // The one-press actions
        //
        // Data, not code: adding one is a line here. Each carries the whole
        // question, because a prompt assembled from fragments at the call site
        // is a prompt nobody can read in one place.
        // =====================================================================
        class QuickAction
        {
            public string Id = "";
            public string Icon = "";
            public string Title = "";
            public string Ask = "";
        }

        static readonly QuickAction[] Actions =
        {
            new QuickAction
            {
                Id = "identify", Icon = NsIcon.Identify, Title = "What is this?",
                Ask = "What document is this? Name the kind of document, who issued it if that is " +
                      "shown, and what it is used for. A few lines is enough."
            },
            new QuickAction
            {
                Id = "read", Icon = NsIcon.ReadText, Title = "Read the text",
                Ask = "Transcribe everything on this page, in the order it appears, keeping the " +
                      "layout: headings as headings, a table as rows, a form field as its label " +
                      "and its value. Do not translate it, do not correct it and do not summarise " +
                      "it -- transcribe it. Put [?] where you cannot read something rather than " +
                      "guessing at it."
            },
            new QuickAction
            {
                Id = "translate", Icon = NsIcon.Translate, Title = "Translate",
                Ask = "Translate the text on this page. If it is in Bengali, translate it into " +
                      "English; otherwise translate it into Bengali. Keep the layout, and where a " +
                      "field has a label keep the original beside the translation. Leave names, " +
                      "numbers and dates as they are written."
            },
            new QuickAction
            {
                Id = "check", Icon = NsIcon.CheckScan, Title = "Check the scan",
                Ask = "Look at this as a scan rather than as a document. Is any part cut off, out " +
                      "of focus, crooked, or too dark or too light to read? Say what to change on " +
                      "the scanner -- the crop, the resolution, straightening, the exposure. If " +
                      "there is nothing wrong with it, say so in one line."
            },
            new QuickAction
            {
                Id = "summarise", Icon = NsIcon.Summarise, Title = "Summarise",
                Ask = "Summarise what this document says: who it concerns, what it is for, and any " +
                      "dates and amounts on it. A few lines."
            },
        };

        /// <summary>
        /// The standing instruction, and the first thing in the cached prefix.
        ///
        /// Nothing in here may change between turns. A date, a page number or a
        /// session id in this string means the cache never hits, and nothing
        /// anywhere says so -- the only sign is a token count that stays high.
        /// </summary>
        const string Instruction =
            "You are the assistant inside NextScan Studio, a scanner application. The operator " +
            "has scanned a page and you are looking at that scan.\n\n" +
            "Answer about the page in front of you. If something is not on the page, say that " +
            "rather than filling it in from what documents of that kind usually say -- this is " +
            "used on identity papers, bills and forms where an invented field is worse than a " +
            "missing one.\n\n" +
            "Reply in the language the operator writes in. Many of these documents are Bengali, " +
            "English, or both on one page; keep names, numbers and dates exactly as written.\n\n" +
            "Write plainly. No headings unless the page has them, no preamble about what you are " +
            "about to do.";

        // =====================================================================
        // Build
        // =====================================================================
        public StudioAiPanel()
        {
            BackColor = Theme.Surface;
            DoubleBuffered = true;

            BuildActions();

            _scroll = new Panel { BackColor = Theme.Surface, AutoScroll = true };
            Controls.Add(_scroll);

            // The bubbles are placed in a column inside the scrolling panel
            // rather than in the panel itself. Setting a child's bounds
            // directly on an AutoScroll panel places it relative to the
            // scrolled origin, so every re-layout while scrolled -- which is
            // every token that arrives -- would walk the transcript up the
            // window. The column is what moves; its contents never do.
            _column = new Panel { BackColor = Theme.Surface, Location = new Point(0, 0) };
            _scroll.Controls.Add(_column);

            _hint = new Label
            {
                BackColor = Color.Transparent,
                ForeColor = Theme.TextFaint,
                Font = Theme.Ui(7.5f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_hint);

            _composer = new NsTextBox { Multiline = true, ShowScroll = false };
            _composer.TextCommitted += delegate { Send(_composer.Text, null); };
            Controls.Add(_composer);

            _send = new NsIconButton { Icon = NsIcon.Send, Raised = true };
            _send.Click += delegate
            {
                // One button, two jobs, the same way the Scan pill works: a stop
                // button that is dead almost all the time is worse than the
                // action changing meaning while it is actually running.
                if (_busy) Stop();
                else Send(_composer.Text, null);
            };
            Controls.Add(_send);

            _meter = new Label
            {
                BackColor = Color.Transparent,
                ForeColor = Theme.TextFaint,
                Font = Theme.Ui(7.5f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_meter);

            // Only for the waiting dots, and only while something is waiting.
            _tick.Interval = 60;
            _tick.Tick += delegate { if (_live != null && _live.Pending) _live.Invalidate(); };

            ShowWelcome();
            UpdateMeter();
        }

        void BuildActions()
        {
            _actions = new Panel { BackColor = Theme.Surface };
            _actions.Paint += delegate (object sender, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Surface);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    e.Graphics.DrawLine(pen, 0, _actions.Height - 1, _actions.Width, _actions.Height - 1);
            };
            Controls.Add(_actions);

            foreach (QuickAction action in Actions)
            {
                QuickAction which = action;
                NsIconButton button = new NsIconButton { Icon = action.Icon, Raised = true };
                button.Click += delegate { Send(which.Title, which.Ask); };
                _tips.SetToolTip(button, action.Title);
                _actions.Controls.Add(button);
                _actionButtons[action.Id] = button;
            }

            NsIconButton suggest = new NsIconButton { Icon = NsIcon.Suggest, Raised = true };
            suggest.Click += delegate { Suggest(); };
            _tips.SetToolTip(suggest, "Ask which of these would help with this page");
            _actions.Controls.Add(suggest);
            _actionButtons["suggest"] = suggest;

            NsIconButton clear = new NsIconButton { Icon = NsIcon.Clear, Raised = true };
            clear.Click += delegate { Reset(); };
            _tips.SetToolTip(clear, "Start again");
            _actions.Controls.Add(clear);
            _actionButtons["clear"] = clear;
        }

        // =====================================================================
        // What the shell sets
        // =====================================================================
        public string ProviderId
        {
            get { return _providerId; }
            set
            {
                string id = string.IsNullOrEmpty(value) ? "claude" : value;
                if (id == _providerId) return;
                _providerId = id;
                _model = "";                 // a model name belongs to one provider
                UpdateMeter();
                Raise();
            }
        }

        public string Model
        {
            get { return string.IsNullOrEmpty(_model) ? DefaultModel() : _model; }
            set { _model = value ?? ""; UpdateMeter(); Raise(); }
        }

        public ThinkingLevel Thinking
        {
            get { return _thinking; }
            set { _thinking = value; UpdateMeter(); Raise(); }
        }

        void Raise() { if (ChoiceChanged != null) ChoiceChanged(this, EventArgs.Empty); }

        IAiProvider Provider()
        {
            IAiProvider found = AiProviders.ById(_providerId);
            return found ?? AiProviders.All()[0];
        }

        string DefaultModel()
        {
            string[] models = Provider().Info.Models;
            return models.Length > 0 ? models[0] : "";
        }

        /// <summary>Re-reads what Settings may have changed while the panel was hidden.</summary>
        public void Rebind()
        {
            UpdateMeter();
            LayoutAll();
        }

        // =====================================================================
        // Sending
        // =====================================================================

        /// <summary>
        /// One turn. <paramref name="ask"/> is the full question when it came
        /// from an action button, and null when the operator typed it, in which
        /// case <paramref name="shown"/> is both.
        /// </summary>
        void Send(string shown, string ask)
        {
            if (_busy) return;

            string text = (ask ?? shown ?? "").Trim();
            if (text.Length == 0) return;

            IAiProvider provider = Provider();
            if (!provider.Ready)
            {
                AddNote("No key has been set for " + provider.Info.Name +
                        ". Settings, under Assistant, takes one.", true);
                return;
            }

            RawImage page = PageSource == null ? null : PageSource();
            byte[] image = null;

            // The page rides on the first turn only. Sending it again on every
            // turn would be the single largest cost in this panel, and the
            // model already has it in the conversation.
            if (_history.Count == 0)
            {
                if (page == null)
                {
                    AddNote("There is no page to look at yet. Preview or scan something first.", true);
                    return;
                }
                image = Encode(page);
                if (image == null)
                {
                    AddNote("That page could not be prepared for sending.", true);
                    return;
                }
                _pageFor = PageNote == null ? "" : PageNote();
            }

            _composer.Text = "";
            AddBubble(shown ?? text, true, false);

            AiMessage turn = AiMessage.FromUser(text);
            turn.Image = image;
            turn.ImageMediaType = "image/jpeg";
            _history.Add(turn);

            Ask(provider);
        }

        /// <summary>
        /// Asks which actions fit this page, and marks those that do.
        ///
        /// A separate, cheap call rather than a pass that runs on its own when
        /// the panel opens: the operator presses it, so they know they are
        /// spending, and it is at the lowest thinking level because it answers
        /// with a list of five words.
        /// </summary>
        void Suggest()
        {
            if (_busy) return;

            IAiProvider provider = Provider();
            if (!provider.Ready)
            {
                AddNote("No key has been set for " + provider.Info.Name +
                        ". Settings, under Assistant, takes one.", true);
                return;
            }

            RawImage page = PageSource == null ? null : PageSource();
            if (page == null)
            {
                AddNote("There is no page to look at yet. Preview or scan something first.", true);
                return;
            }

            var ids = new List<string>();
            foreach (QuickAction a in Actions) ids.Add(a.Id);

            var request = new AiRequest
            {
                Model = Model,
                Thinking = ThinkingLevel.Low,
                MaxOutputTokens = 64,
                Instruction = Instruction,
            };
            AiMessage turn = AiMessage.FromUser(
                "Which of these would be useful on this page? Answer with ids only, separated by " +
                "commas, and nothing else. ids: " + string.Join(", ", ids.ToArray()));
            turn.Image = Encode(page);
            turn.ImageMediaType = "image/jpeg";
            if (turn.Image == null) { AddNote("That page could not be prepared for sending.", true); return; }
            request.Messages.Add(turn);

            Busy(true);
            Say("Asking " + provider.Info.Name + " which actions fit this page");

            _cancel = new CancellationTokenSource();
            CancellationToken token = _cancel.Token;

            Task.Run(delegate { return provider.Ask(request, null, token); })
                .ContinueWith(delegate (Task<AiReply> done)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate { SuggestionsArrived(done); });
                });
        }

        void SuggestionsArrived(Task<AiReply> done)
        {
            Busy(false);

            if (done.IsCanceled) { Say("Stopped"); return; }
            if (done.IsFaulted)
            {
                AddNote(Plain(done.Exception), true);
                Say("");
                return;
            }

            string answer = done.Result == null ? "" : (done.Result.Text ?? "");
            int marked = 0;
            foreach (QuickAction a in Actions)
            {
                bool fits = answer.IndexOf(a.Id, StringComparison.OrdinalIgnoreCase) >= 0;
                NsIconButton button;
                if (_actionButtons.TryGetValue(a.Id, out button))
                {
                    button.Checked = fits;
                    if (fits) marked++;
                }
            }

            Count(done.Result);
            Say(marked == 0
                ? "Nothing here stood out as useful on this page"
                : marked + (marked == 1 ? " action is" : " actions are") + " marked for this page");
        }

        void Ask(IAiProvider provider)
        {
            var request = new AiRequest
            {
                Model = Model,
                Thinking = _thinking,
                MaxOutputTokens = 4096,
                Instruction = Instruction,
            };
            request.Messages.AddRange(_history);

            _streamed.Length = 0;
            _live = AddBubble("", false, false);
            _live.Pending = true;
            _tick.Start();

            Busy(true);
            string note = provider.Info.Describe(_thinking);
            Say("Asking " + provider.Info.Name + (note.Length > 0 ? " — " + note : ""));

            _cancel = new CancellationTokenSource();
            CancellationToken token = _cancel.Token;

            AiTextArrived onText = delegate (string piece)
            {
                // On a worker thread. The panel marshals; the provider has no
                // business knowing there is a UI thread at all.
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((MethodInvoker)delegate { Grew(piece); }); }
                catch (InvalidOperationException) { }
            };

            Task.Run(delegate { return provider.Ask(request, onText, token); })
                .ContinueWith(delegate (Task<AiReply> done)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate { ReplyArrived(done); });
                });
        }

        void Grew(string piece)
        {
            if (_live == null) return;
            _streamed.Append(piece);
            _live.Pending = false;
            _live.Text = _streamed.ToString();
            LayoutTranscript(true);
        }

        void ReplyArrived(Task<AiReply> done)
        {
            _tick.Stop();
            Busy(false);

            // A cancelled task is not a faulted one, and Result throws on
            // either. Stopping is something the operator did, so it reads as a
            // note on the turn rather than as a failure.
            if (done.IsCanceled || done.IsFaulted)
            {
                string trouble = done.IsCanceled ? "Stopped." : Plain(done.Exception);
                if (_live != null) { _live.Pending = false; _live.Trouble = true; _live.Text = trouble; }
                else AddNote(trouble, true);

                // The turn did not happen, so it does not belong in the history
                // that the next one is built from.
                if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
                _live = null;
                LayoutTranscript(true);
                Say("");
                return;
            }

            AiReply reply = done.Result;
            string text = reply == null ? "" : (reply.Text ?? "");
            if (text.Trim().Length == 0) text = "(the model returned nothing)";

            if (_live != null)
            {
                _live.Pending = false;
                _live.Text = text;
            }
            _history.Add(AiMessage.FromAssistant(text));
            _live = null;

            Count(reply);
            LayoutTranscript(true);
            Say("");
        }

        void Stop()
        {
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            Say("Stopping");
        }

        void Busy(bool on)
        {
            _busy = on;
            _send.Icon = on ? NsIcon.Stop : NsIcon.Send;
            _tips.SetToolTip(_send, on ? "Stop" : "Send  (Enter)");
            foreach (KeyValuePair<string, NsIconButton> pair in _actionButtons)
                if (pair.Key != "clear") pair.Value.Enabled = !on;
            _send.Invalidate();
        }

        void Say(string what) { if (Status != null) Status(what); }

        /// <summary>
        /// An exception the operator can read.
        ///
        /// AiTrouble is already written for them; anything else is the SDK's
        /// own message, which is usually the HTTP status and is more use than a
        /// sentence of ours pretending to know what went wrong.
        /// </summary>
        static string Plain(AggregateException ex)
        {
            Exception inner = ex == null ? null : ex.GetBaseException();
            if (inner == null) return "That did not work.";
            if (inner is OperationCanceledException) return "Stopped.";

            AiTrouble known = inner as AiTrouble;
            return known != null ? known.Message : inner.GetType().Name + ": " + inner.Message;
        }

        // =====================================================================
        // Usage
        // =====================================================================
        long _inTotal, _outTotal, _cachedTotal;

        void Count(AiReply reply)
        {
            if (reply == null) return;
            _inTotal += reply.Usage.InputTokens;
            _outTotal += reply.Usage.OutputTokens;
            _cachedTotal += reply.Usage.CachedInputTokens;
            UpdateMeter();
        }

        /// <summary>
        /// The line under the box: which model, how hard it is thinking, and
        /// what has been spent.
        ///
        /// The counts come from the provider's own reply and are never computed
        /// here. Tokenizers differ between providers and between generations of
        /// one provider, so a number produced locally is a number for the wrong
        /// model -- worse than none, because it looks authoritative.
        /// </summary>
        void UpdateMeter()
        {
            if (_meter == null) return;

            IAiProvider provider = Provider();
            var line = new StringBuilder();
            line.Append(provider.Info.Name).Append("  ").Append(Model);
            line.Append("   ").Append(_thinking.ToString().ToLowerInvariant());

            string note = provider.Info.Describe(_thinking);
            if (note.Length > 0) line.Append(" (").Append(provider.Info.Ceiling.ToString().ToLowerInvariant()).Append(")");

            if (_inTotal + _outTotal > 0)
            {
                line.Append("   ").Append(Tokens(_inTotal)).Append(" in / ").Append(Tokens(_outTotal)).Append(" out");
                if (_cachedTotal > 0) line.Append(", ").Append(Tokens(_cachedTotal)).Append(" cached");
            }

            if (!provider.Ready) line.Append("   ·  no key set");

            _meter.Text = line.ToString();
            _tips.SetToolTip(_meter, provider.Ready
                ? "Counted by " + provider.Info.Name + " for " + Model + ", not estimated here"
                : "Settings, under Assistant, takes the key");
        }

        static string Tokens(long n)
        {
            if (n < 1000) return n.ToString(CultureInfo.InvariantCulture);
            return (n / 1000.0).ToString(n < 10000 ? "0.0" : "0", CultureInfo.InvariantCulture) + "k";
        }

        // =====================================================================
        // The transcript
        // =====================================================================
        NsBubble AddBubble(string text, bool mine, bool trouble, bool copyable = true)
        {
            NsBubble bubble = new NsBubble { Text = text, Mine = mine, Trouble = trouble };
            if (!mine && copyable)
            {
                NsBubble which = bubble;
                bubble.CopyWanted += delegate { CopyOut(which.Text); };
            }
            _bubbles.Add(bubble);
            _column.Controls.Add(bubble);
            LayoutTranscript(true);
            return bubble;
        }

        void AddNote(string text, bool trouble)
        {
            AddBubble(text, false, trouble, false);
            Say(trouble ? text : "");
        }

        void CopyOut(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); Say("Copied"); }
            catch (Exception ex) { Say("Could not copy: " + ex.Message); }
        }

        void ShowWelcome()
        {
            AddBubble(
                "This looks at the page you have scanned.\n\n" +
                "Press one of the marks above for the usual jobs, or ask about the page in your " +
                "own words — in Bengali or in English. The whole page is sent, so it is read in " +
                "context rather than a piece at a time.\n\n" +
                "Enter sends. Shift+Enter starts a new line.",
                false, false, false);
        }

        /// <summary>Starts a new conversation. The page is sent again with the first turn.</summary>
        public void Reset()
        {
            if (_busy) Stop();

            _history.Clear();
            _pageFor = "";
            _live = null;
            _streamed.Length = 0;
            _inTotal = _outTotal = _cachedTotal = 0;

            foreach (NsBubble bubble in _bubbles) { _column.Controls.Remove(bubble); bubble.Dispose(); }
            _bubbles.Clear();

            foreach (KeyValuePair<string, NsIconButton> pair in _actionButtons) pair.Value.Checked = false;

            ShowWelcome();
            UpdateMeter();
            Say("");
        }

        // =====================================================================
        // The page, as the model will see it
        // =====================================================================

        /// <summary>
        /// The longest edge a page is sent at.
        ///
        /// All three providers resize anything larger before they look at it, so
        /// sending more than this is paying to upload pixels that are thrown
        /// away. A 300 dpi A4 page is 3500 px down to this, which still reads
        /// 9 pt type.
        /// </summary>
        const int LongestEdge = 1568;

        static byte[] Encode(RawImage page)
        {
            if (page == null || !page.IsValid) return null;

            try
            {
                using (Bitmap full = page.ToBitmap())
                {
                    if (full == null) return null;

                    int w = full.Width, h = full.Height;
                    double shrink = Math.Min(1.0, (double)LongestEdge / Math.Max(w, h));
                    int tw = Math.Max(1, (int)Math.Round(w * shrink));
                    int th = Math.Max(1, (int)Math.Round(h * shrink));

                    using (Bitmap small = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(full, new Rectangle(0, 0, tw, th));
                        }

                        // JPEG at 92 rather than PNG: a 1568 px scan of a page
                        // is about ten times smaller this way, and at 92 the
                        // artefacts are below the size of the type. PNG would be
                        // exact and would cost ten times as much on every page.
                        using (var stream = new MemoryStream())
                        {
                            ImageCodecInfo jpeg = Codec("image/jpeg");
                            if (jpeg == null) { small.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }

                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                                small.Save(stream, jpeg, parameters);
                            }
                            return stream.ToArray();
                        }
                    }
                }
            }
            catch { return null; }
        }

        static ImageCodecInfo Codec(string mime)
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
                if (string.Equals(codec.MimeType, mime, StringComparison.OrdinalIgnoreCase)) return codec;
            return null;
        }

        // =====================================================================
        // Layout
        // =====================================================================
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutAll();
        }

        const int Pad = 14;
        const int ActionsHeight = 50;
        const int ComposerHeight = 66;
        const int MeterHeight = 18;
        const int HintHeight = 16;

        void LayoutAll()
        {
            if (_actions == null || Width <= 0 || Height <= 0) return;

            int inner = Math.Max(60, Width - Pad * 2);

            _actions.SetBounds(0, 0, Width, ActionsHeight);
            LayoutActionRow();

            int bottom = Height;
            _meter.SetBounds(Pad, bottom - MeterHeight - 6, inner, MeterHeight);
            bottom -= MeterHeight + 8;

            _send.SetBounds(Width - Pad - 42, bottom - ComposerHeight + (ComposerHeight - 42) / 2, 42, 42);
            _composer.SetBounds(Pad, bottom - ComposerHeight, Math.Max(40, inner - 50), ComposerHeight);
            bottom -= ComposerHeight + 4;

            _hint.SetBounds(Pad, bottom - HintHeight, inner, HintHeight);
            bottom -= HintHeight;

            _scroll.SetBounds(0, ActionsHeight, Width, Math.Max(40, bottom - ActionsHeight));
            UpdateHint();
            LayoutTranscript(false);
        }

        void LayoutActionRow()
        {
            int count = _actions.Controls.Count;
            if (count == 0) return;

            const int square = 34;
            int gap = Math.Max(4, Math.Min(10, (Width - Pad * 2 - square * count) / Math.Max(1, count - 1)));
            int total = square * count + gap * (count - 1);
            int x = Math.Max(Pad, (Width - total) / 2);
            int y = (ActionsHeight - square) / 2;

            for (int i = 0; i < count; i++)
            {
                _actions.Controls[i].SetBounds(x, y, square, square);
                x += square + gap;
            }
        }

        /// <summary>
        /// Places the bubbles down the column, measuring each at the width it
        /// will actually be drawn at.
        /// </summary>
        void LayoutTranscript(bool toEnd)
        {
            if (_scroll == null) return;

            // The width the bubbles get is the panel's, less the scrollbar if
            // one is showing. Measuring against the full width and then having
            // the bar appear is what makes a transcript reflow as it grows.
            int inner = Math.Max(80, _scroll.ClientSize.Width - Pad * 2);

            int y = 10;
            _column.SuspendLayout();
            foreach (NsBubble bubble in _bubbles)
            {
                bubble.Width = inner;                       // measure at the width it will be drawn at
                int h = bubble.MeasureHeight(inner);
                bubble.SetBounds(Pad, y, inner, h);
                y += h + 8;
            }
            _column.SetBounds(0, 0, _scroll.ClientSize.Width, y + 4);
            _column.ResumeLayout();

            if (toEnd && _bubbles.Count > 0)
                _scroll.ScrollControlIntoView(_bubbles[_bubbles.Count - 1]);
        }

        void UpdateHint()
        {
            if (_hint == null) return;

            string page = PageNote == null ? "" : (PageNote() ?? "");
            if (_history.Count > 0 && _pageFor.Length > 0 && _pageFor != page)
            {
                // The conversation is about the page it started on. Saying so is
                // the difference between a stale answer and a wrong one.
                _hint.ForeColor = Theme.Warn;
                _hint.Text = "Talking about " + _pageFor + " — start again to use " + page;
                return;
            }

            _hint.ForeColor = Theme.TextFaint;
            _hint.Text = page.Length > 0 ? page : "Nothing scanned yet";
        }

        /// <summary>Re-reads the palette after a light/dark switch.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.Surface;
            if (_scroll != null) _scroll.BackColor = Theme.Surface;
            if (_column != null) _column.BackColor = Theme.Surface;
            if (_actions != null) _actions.BackColor = Theme.Surface;
            if (_composer != null) _composer.ApplyTheme();
            if (_meter != null) _meter.ForeColor = Theme.TextFaint;
            UpdateHint();
            Invalidate(true);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
                _tick.Dispose();
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
