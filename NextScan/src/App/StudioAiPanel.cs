// =============================================================================
// NextScan Studio - the assistant panel
// Plan ref: docs/AI_LAYER.md.
//
// The right-hand panel, when the rail is on Assist. It is the only file in the
// application that names NextScan.Ai, which is what keeps the provider SDKs out
// of the shell.
//
// The layout is not ours. The first version of this panel was invented: a row
// of icons across the top, a text box with a button beside it, and the model
// and thinking level on the settings page. Claude, ChatGPT, Gemini and Copilot
// were then looked at side by side, and all four agree with each other and none
// of them agree with that:
//
//   * the input and its controls are ONE rounded plate, and the model and the
//     effort sit along its bottom edge beside the send button;
//   * they are chosen per question, so they are never on a settings page --
//     Settings holds the key, which is the only part that belongs to the
//     account rather than to the turn;
//   * the empty state offers named suggestions, not a toolbar of icons that
//     have to be learned before they mean anything.
//
// Two decisions of our own are worth stating.
//
// The model is sent the WHOLE page, never a crop. A model handed a rectangle
// cut out of a form does not know it is looking at a form, and answers about
// the rectangle. Measurements are a different job and are not asked of a model.
//
// And nothing is sent until the operator presses something. There is no pass
// over the page when the panel opens and no background call while they look at
// it. Every call here costs the shop money, and a cost they did not ask for is
// one they cannot plan for.
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

        /// <summary>Where that page came from, for the chip on the composer.</summary>
        public Func<string> PageNote;

        /// <summary>The shell's status line.</summary>
        public Action<string> Status;

        /// <summary>Raised when the provider, model or thinking level changes here.</summary>
        public event EventHandler ChoiceChanged;

        // ---- state ---------------------------------------------------------

        readonly List<AiMessage> _history = new List<AiMessage>();
        readonly List<NsBubble> _bubbles = new List<NsBubble>();
        readonly List<NsPill> _chips = new List<NsPill>();

        Panel _bar;                 // the thin strip at the top: new conversation
        NsIconButton _fresh;
        Panel _scroll;
        Panel _column;
        Panel _welcome;             // the empty state, shown until the first turn
        NsComposer _composer;
        NsPill _modelButton;
        NsIconButton _send;
        Label _meter;

        readonly System.Windows.Forms.Timer _tick = new System.Windows.Forms.Timer();
        readonly ToolTip _tips = new ToolTip();

        CancellationTokenSource _cancel;
        NsBubble _live;
        readonly StringBuilder _streamed = new StringBuilder();
        bool _busy;

        string _providerId = "claude";
        string _model = "";
        ThinkingLevel _thinking = ThinkingLevel.Medium;

        /// <summary>
        /// Which page this conversation is about, as the operator would name it.
        /// Kept so the panel can say so when the page underneath changes.
        /// </summary>
        string _pageFor = "";

        // =====================================================================
        // The suggestions
        //
        // Data, not code: adding one is a line here. Each carries the whole
        // question, because a prompt assembled from fragments at the call site
        // is a prompt nobody can read in one place.
        // =====================================================================
        class Suggestion
        {
            public string Id = "";
            public string Title = "";
            public string Ask = "";
        }

        static readonly Suggestion[] Suggestions =
        {
            new Suggestion
            {
                Id = "identify", Title = "What is this document?",
                Ask = "What document is this? Name the kind of document, who issued it if that is " +
                      "shown, and what it is used for. A few lines is enough."
            },
            new Suggestion
            {
                Id = "read", Title = "Read the text",
                Ask = "Transcribe everything on this page, in the order it appears, keeping the " +
                      "layout: headings as headings, a table as rows, a form field as its label " +
                      "and its value. Do not translate it, do not correct it and do not summarise " +
                      "it -- transcribe it. Put [?] where you cannot read something rather than " +
                      "guessing at it."
            },
            new Suggestion
            {
                Id = "translate", Title = "Translate it",
                Ask = "Translate the text on this page. If it is in Bengali, translate it into " +
                      "English; otherwise translate it into Bengali. Keep the layout, and where a " +
                      "field has a label keep the original beside the translation. Leave names, " +
                      "numbers and dates as they are written."
            },
            new Suggestion
            {
                Id = "check", Title = "Check the scan",
                Ask = "Look at this as a scan rather than as a document. Is any part cut off, out " +
                      "of focus, crooked, or too dark or too light to read? Say what to change on " +
                      "the scanner -- the crop, the resolution, straightening, the exposure. If " +
                      "there is nothing wrong with it, say so in one line."
            },
            new Suggestion
            {
                Id = "summarise", Title = "Summarise it",
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

            BuildBar();
            BuildTranscript();
            BuildWelcome();
            BuildComposer();

            _tick.Interval = 60;
            _tick.Tick += delegate { if (_live != null && _live.Pending) _live.Invalidate(); };

            UpdateModelButton();
        }

        void BuildBar()
        {
            _bar = new Panel { BackColor = Theme.Surface };
            Controls.Add(_bar);

            _fresh = new NsIconButton { Icon = NsIcon.NewChat };
            _fresh.Click += delegate { Reset(); };
            _tips.SetToolTip(_fresh, "Start again");
            _bar.Controls.Add(_fresh);
        }

        void BuildTranscript()
        {
            _scroll = new Panel { BackColor = Theme.Surface, AutoScroll = true, Visible = false };
            Controls.Add(_scroll);

            // The bubbles go in a column inside the scrolling panel rather than
            // in the panel itself. Setting a child's bounds directly on an
            // AutoScroll panel places it relative to the scrolled origin, so
            // every re-layout while scrolled -- which is every token that
            // arrives -- would walk the transcript up the window.
            _column = new Panel { BackColor = Theme.Surface, Location = new Point(0, 0) };
            _scroll.Controls.Add(_column);
        }

        /// <summary>
        /// The empty state: what this is for, and the questions worth asking
        /// about a scanned page, by name.
        /// </summary>
        void BuildWelcome()
        {
            _welcome = new Panel { BackColor = Theme.Surface };
            _welcome.Paint += delegate (object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Surface);
                Theme.Smooth(g);

                int top = WelcomeTop();
                NsIcon.Draw(g, NsIcon.Assist, new RectangleF(_welcome.Width / 2f - 16, top, 32, 32),
                            Theme.Mix(Theme.TextFaint, Theme.Accent, 0.7));

                using (Font f = Theme.UiSemi(12f))
                    TextRenderer.DrawText(g, "About this page", f,
                        new Rectangle(12, top + 40, Math.Max(1, _welcome.Width - 24), 26), Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            };
            Controls.Add(_welcome);

            foreach (Suggestion suggestion in Suggestions)
            {
                Suggestion which = suggestion;
                NsPill chip = new NsPill { Text = suggestion.Title, Kind = PillKind.Normal, Radius = 17 };
                chip.Font = Theme.Ui(8.75f);
                chip.Click += delegate { Send(which.Title, which.Ask); };
                _welcome.Controls.Add(chip);
                _chips.Add(chip);
            }
        }

        int WelcomeTop()
        {
            int block = 74 + _chips.Count * 40;
            return Math.Max(14, (_welcome.Height - block) / 2 - 10);
        }

        void BuildComposer()
        {
            _composer = new NsComposer { Placeholder = "Ask about this page" };
            _composer.Submit += delegate { Send(_composer.Text, null); };
            _composer.Typed += delegate { UpdateSend(); };
            _composer.HeightWanted += delegate { LayoutAll(); };
            _composer.FootChanged += delegate { LayoutComposer(); };
            Controls.Add(_composer);

            // Model and effort, beside the send button. This is the one control
            // every real chat interface agrees on the position of.
            _modelButton = new NsPill { Kind = PillKind.Quiet, Radius = 11 };
            _modelButton.Font = Theme.Ui(8f);
            _modelButton.Click += delegate { OpenModelMenu(); };
            _composer.Controls.Add(_modelButton);

            _send = new NsIconButton { Icon = NsIcon.Send, Circle = true, Raised = true };
            _send.Click += delegate
            {
                if (_busy) Stop();
                else Send(_composer.Text, null);
            };
            _tips.SetToolTip(_send, "Send  (Enter)");
            _composer.Controls.Add(_send);

            _meter = new Label
            {
                BackColor = Color.Transparent,
                ForeColor = Theme.TextFaint,
                Font = Theme.Ui(7.5f),
                AutoSize = false,
                Visible = false,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_meter);
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
                UpdateModelButton();
                Raise();
            }
        }

        public string Model
        {
            get { return _model; }
            set { _model = value ?? ""; UpdateModelButton(); Raise(); }
        }

        public ThinkingLevel Thinking
        {
            get { return _thinking; }
            set { _thinking = value; UpdateModelButton(); Raise(); }
        }

        void Raise() { if (ChoiceChanged != null) ChoiceChanged(this, EventArgs.Empty); }

        IAiProvider Provider()
        {
            IAiProvider found = AiProviders.ById(_providerId);
            return found ?? AiProviders.All()[0];
        }

        /// <summary>Re-reads what Settings may have changed while the panel was hidden.</summary>
        public void Rebind()
        {
            UpdateModelButton();
            LayoutAll();
        }

        // =====================================================================
        // The model menu
        // =====================================================================

        /// <summary>
        /// Providers, their models, and the effort, in one menu.
        ///
        /// The model list is the provider's own, fetched with the operator's key
        /// and never written down here: what an account can reach depends on the
        /// account, and a list in our source would be wrong by the next release
        /// with nothing to say so. The first time this opens for a key it has
        /// nothing to show, so it says it is asking and has them by the time the
        /// menu is opened again.
        /// </summary>
        void OpenModelMenu()
        {
            NsChoiceMenu menu = new NsChoiceMenu();
            bool anyKey = false;
            bool anyMissing = false;

            foreach (IAiProvider provider in AiProviders.All())
            {
                if (!provider.Ready)
                {
                    menu.Header(provider.Info.Name, "no key");
                    continue;
                }

                anyKey = true;
                menu.Header(provider.Info.Name, "");

                IList<AiModel> models = AiModels.Cached(provider);
                if (models == null)
                {
                    menu.Note("asking " + provider.Info.Name + "...");
                    anyMissing = true;
                    BeginFetch(provider);
                    continue;
                }
                if (models.Count == 0) { menu.Note("no models on this key"); continue; }

                foreach (AiModel model in models)
                {
                    AiModel which = model;
                    IAiProvider owner = provider;
                    menu.Item(model.ToString(), "",
                              owner.Info.Id == _providerId && which.Id == _model,
                              new Chosen { Provider = owner.Info.Id, Model = which.Id });
                }
            }

            if (!anyKey)
            {
                menu.Note("No key has been set.");
                menu.Note("Settings, under Assistant.");
            }
            else
            {
                menu.Rule();
                menu.Header("Effort", "");

                IAiProvider now = Provider();
                foreach (ThinkingLevel level in new[] { ThinkingLevel.Low, ThinkingLevel.Medium,
                                                        ThinkingLevel.High, ThinkingLevel.Max })
                {
                    ThinkingLevel which = level;
                    string note = level > now.Info.Ceiling
                        ? "uses " + now.Info.Ceiling.ToString().ToLowerInvariant()
                        : "";
                    menu.Item(which.ToString(), note, which == _thinking, new Chosen { Effort = which });
                }
            }

            menu.Picked += delegate (object tag) { Took(tag); };
            menu.Show(_modelButton, Math.Max(210, Width - 28));

            if (anyMissing) Say("Asking the provider which models this key can reach");
        }

        class Chosen
        {
            public string Provider;
            public string Model;
            public ThinkingLevel? Effort;
        }

        void Took(object tag)
        {
            Chosen chosen = tag as Chosen;
            if (chosen == null) return;

            if (chosen.Effort.HasValue) { Thinking = chosen.Effort.Value; return; }

            _providerId = chosen.Provider;
            _model = chosen.Model;
            UpdateModelButton();
            Raise();
        }

        // ---- fetching the list ----------------------------------------------

        readonly List<string> _asking = new List<string>();

        /// <summary>
        /// Asks a provider for its models, once per key.
        ///
        /// Fire and forget: the menu that triggered it has already been drawn
        /// without them, and the point is that the next time it opens they are
        /// there. If it fails while nobody is looking at the menu, the status
        /// line is where that is said.
        /// </summary>
        void BeginFetch(IAiProvider provider)
        {
            string id = provider.Info.Id;
            if (_asking.Contains(id)) return;
            _asking.Add(id);

            CancellationToken token = new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
            Task.Run(delegate { return AiModels.Fetch(provider, false, token); })
                .ContinueWith(delegate (Task<IList<AiModel>> done)
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    try { BeginInvoke((MethodInvoker)delegate { Fetched(provider, done); }); }
                    catch (InvalidOperationException) { }
                });
        }

        void Fetched(IAiProvider provider, Task<IList<AiModel>> done)
        {
            _asking.Remove(provider.Info.Id);

            if (done.IsFaulted || done.IsCanceled)
            {
                Say("Could not reach " + provider.Info.Name + ": " + Plain(done.Exception));
                return;
            }

            // Nothing was chosen yet, or what was chosen is not on this key.
            if (provider.Info.Id == _providerId && !Has(done.Result, _model))
            {
                _model = AiModels.Default(provider, done.Result);
                Raise();
            }

            UpdateModelButton();
            Say(provider.Info.Name + ": " + done.Result.Count +
                (done.Result.Count == 1 ? " model" : " models"));
        }

        static bool Has(IList<AiModel> list, string id)
        {
            if (list == null || string.IsNullOrEmpty(id)) return false;
            foreach (AiModel model in list) if (model.Id == id) return true;
            return false;
        }

        void UpdateModelButton()
        {
            if (_modelButton == null) return;

            IAiProvider provider = Provider();

            if (!provider.Ready)
            {
                _modelButton.Text = "Add a key  ⌄";
                _tips.SetToolTip(_modelButton, "Settings, under Assistant, takes the key");
            }
            else if (string.IsNullOrEmpty(_model))
            {
                _modelButton.Text = provider.Info.Name + "  ⌄";
                _tips.SetToolTip(_modelButton, "Choose a model");
            }
            else
            {
                _modelButton.Text = AiModels.NameOf(provider, _model) + "   " +
                                    _thinking.ToString().ToLowerInvariant() + "  ⌄";
                string note = provider.Info.Describe(_thinking);
                _tips.SetToolTip(_modelButton, note.Length > 0
                    ? provider.Info.Name + " — " + note
                    : provider.Info.Name + " — model and effort");
            }

            LayoutComposer();
            UpdateChip();
        }

        // =====================================================================
        // Sending
        // =====================================================================

        /// <summary>
        /// One turn. <paramref name="ask"/> is the full question when it came
        /// from a suggestion, and null when the operator typed it, in which case
        /// <paramref name="shown"/> is both.
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

            // The page rides on the first turn only. Sending it again every turn
            // would be the single largest cost in this panel, and the model
            // already has it in the conversation.
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
            UpdateSend();
            AddBubble(shown ?? text, true, false);

            AiMessage turn = AiMessage.FromUser(text);
            turn.Image = image;
            turn.ImageMediaType = "image/jpeg";
            _history.Add(turn);

            Ask(provider);
        }

        void Ask(IAiProvider provider)
        {
            string model = string.IsNullOrEmpty(_model)
                ? AiModels.Default(provider, AiModels.Cached(provider))
                : _model;

            if (string.IsNullOrEmpty(model))
            {
                // No list yet for this key, so there is nothing to send to.
                // Fetch it and say so rather than guessing at a model name.
                BeginFetch(provider);
                AddNote("Asking " + provider.Info.Name + " which models this key can reach. " +
                        "Try again in a moment.", true);
                _history.RemoveAt(_history.Count - 1);
                return;
            }

            var request = new AiRequest
            {
                Model = model,
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
                    try { BeginInvoke((MethodInvoker)delegate { ReplyArrived(done); }); }
                    catch (InvalidOperationException) { }
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
                // the next one is built from.
                if (_history.Count > 0) _history.RemoveAt(_history.Count - 1);
                _live = null;
                LayoutTranscript(true);
                Say("");
                return;
            }

            AiReply reply = done.Result;
            string text = reply == null ? "" : (reply.Text ?? "");
            if (text.Trim().Length == 0) text = "(the model returned nothing)";

            if (_live != null) { _live.Pending = false; _live.Text = text; }
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
            foreach (NsPill chip in _chips) chip.Enabled = !on;
            _modelButton.Enabled = !on;
            UpdateSend();
        }

        void UpdateSend()
        {
            if (_send == null) return;
            // Filled while there is something to send, and while one is running,
            // which is the state the button acts on. Every one of these does it.
            _send.Checked = _busy || _composer.Text.Trim().Length > 0;
        }

        void Say(string what) { if (Status != null) Status(what); }

        /// <summary>
        /// An exception the operator can read.
        ///
        /// AiTrouble is already written for them; anything else is the SDK's own
        /// message, which is usually the HTTP status and is more use than a
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
        /// What this conversation has cost, under the box. Hidden until there is
        /// something to report -- which is why none of the interfaces this panel
        /// follows shows one: their users are not billed per page scanned.
        ///
        /// The counts come from the provider's own reply and are never computed
        /// here. Tokenizers differ between providers and between generations of
        /// one provider, so a number produced locally is a number for the wrong
        /// model -- worse than none, because it looks authoritative.
        /// </summary>
        void UpdateMeter()
        {
            if (_meter == null) return;

            bool any = _inTotal + _outTotal > 0;
            if (_meter.Visible != any) { _meter.Visible = any; LayoutAll(); }
            if (!any) return;

            var line = new StringBuilder();
            line.Append(Tokens(_inTotal)).Append(" in / ").Append(Tokens(_outTotal)).Append(" out");
            if (_cachedTotal > 0) line.Append(", ").Append(Tokens(_cachedTotal)).Append(" cached");
            line.Append("   counted by ").Append(Provider().Info.Name);

            _meter.Text = line.ToString();
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

            // The suggestions are the empty state, and this is no longer empty.
            if (_bubbles.Count == 1) ShowTranscript(true);

            LayoutTranscript(true);
            return bubble;
        }

        void AddNote(string text, bool trouble)
        {
            AddBubble(text, false, trouble, false);
            if (trouble) Say(text);
        }

        void CopyOut(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); Say("Copied"); }
            catch (Exception ex) { Say("Could not copy: " + ex.Message); }
        }

        void ShowTranscript(bool on)
        {
            _scroll.Visible = on;
            _welcome.Visible = !on;
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

            ShowTranscript(false);
            UpdateMeter();
            UpdateChip();
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

                        // JPEG at 92 rather than PNG: a 1568 px scan of a page is
                        // about ten times smaller this way, and at 92 the
                        // artefacts are below the size of the type. PNG would be
                        // exact and would cost ten times as much on every page.
                        using (var stream = new MemoryStream())
                        {
                            ImageCodecInfo jpeg = Codec("image/jpeg");
                            if (jpeg == null) { small.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }

                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] =
                                    new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
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
        const int BarHeight = 32;
        const int MeterHeight = 16;

        void LayoutAll()
        {
            if (_bar == null || Width <= 0 || Height <= 0) return;

            _bar.SetBounds(0, 0, Width, BarHeight);
            _fresh.SetBounds(Width - Pad - 26, 4, 26, 24);

            int bottom = Height - 8;

            if (_meter.Visible)
            {
                _meter.SetBounds(Pad + 4, bottom - MeterHeight, Math.Max(40, Width - Pad * 2 - 8), MeterHeight);
                bottom -= MeterHeight + 2;
            }

            int composerHeight = Math.Max(72, Math.Min(Height / 2, _composer.PreferredHeight));
            _composer.SetBounds(Pad, bottom - composerHeight, Math.Max(80, Width - Pad * 2), composerHeight);
            LayoutComposer();
            bottom -= composerHeight + 8;

            int top = BarHeight;
            int middle = Math.Max(40, bottom - top);
            _scroll.SetBounds(0, top, Width, middle);
            _welcome.SetBounds(0, top, Width, middle);

            LayoutWelcome();
            LayoutTranscript(false);
        }

        void LayoutComposer()
        {
            if (_composer == null || _send == null) return;

            int foot = _composer.FootBottom;
            int right = _composer.Width - 8;

            const int round = 30;
            _send.SetBounds(right - round, foot - round, round, round);
            right -= round + 6;

            int modelWidth = Math.Min(Math.Max(90, TextWidth(_modelButton) + 18), Math.Max(60, right - 14));
            _modelButton.SetBounds(right - modelWidth, foot - 28, modelWidth, 26);
        }

        static int TextWidth(NsPill pill)
        {
            return TextRenderer.MeasureText(pill.Text, pill.Font).Width;
        }

        /// <summary>What the question is about, at the left of the composer's strip.</summary>
        void UpdateChip()
        {
            if (_composer == null) return;

            string page = PageNote == null ? "" : (PageNote() ?? "");

            // The conversation is about the page it started on. Saying so is the
            // difference between a stale answer and a wrong one.
            bool moved = _history.Count > 0 && _pageFor.Length > 0 && _pageFor != page;

            _composer.FootNote = moved ? "about " + _pageFor
                               : page.Length > 0 ? page
                               : "nothing scanned yet";
        }

        void LayoutWelcome()
        {
            if (_welcome == null || _chips.Count == 0) return;

            int inner = Math.Max(120, Math.Min(300, _welcome.Width - Pad * 2));
            int x = (_welcome.Width - inner) / 2;
            int y = WelcomeTop() + 78;

            foreach (NsPill chip in _chips)
            {
                chip.SetBounds(x, y, inner, 34);
                y += 40;
            }
        }

        /// <summary>
        /// Places the bubbles down the column, measuring each at the width it
        /// will actually be drawn at.
        /// </summary>
        void LayoutTranscript(bool toEnd)
        {
            if (_scroll == null) return;

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

        /// <summary>Re-reads the palette after a light/dark switch.</summary>
        public void ApplyTheme()
        {
            BackColor = Theme.Surface;
            if (_scroll != null) _scroll.BackColor = Theme.Surface;
            if (_column != null) _column.BackColor = Theme.Surface;
            if (_welcome != null) _welcome.BackColor = Theme.Surface;
            if (_bar != null) _bar.BackColor = Theme.Surface;
            if (_composer != null) _composer.ApplyTheme();
            if (_meter != null) _meter.ForeColor = Theme.TextFaint;
            UpdateChip();
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
