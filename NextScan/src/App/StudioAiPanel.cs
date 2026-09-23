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

        Panel _bar;                 // the thin strip at the top: history, new conversation
        NsIconButton _fresh;
        NsIconButton _past;
        Panel _scroll;
        Panel _column;
        Panel _welcome;             // the empty state, shown until the first turn
        NsComposer _composer;
        NsPill _modelButton;
        NsPill _effortButton;
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
        /// Which page this conversation is about, as the operator would name it,
        /// or empty while none has been sent. Kept so the panel can say so when
        /// the page underneath changes.
        /// </summary>
        string _pageFor = "";

        /// <summary>
        /// Whether the scan has already gone. A page is attached to the first
        /// turn where one exists -- which is usually the first turn, but need
        /// not be: asking a question, then scanning, then asking about the scan
        /// is an ordinary way to use a scanner, and refusing the first question
        /// because nothing was on the glass yet was not.
        /// </summary>
        bool _pageSent;

        /// <summary>
        /// The file this conversation is written to, made at the first turn.
        /// Empty until then, because an empty conversation is not one.
        /// </summary>
        string _chatId = "";

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

            /// <summary>
            /// All of these are about a page. They stay pressable without one
            /// anyway: a greyed button is a button that looks broken, and the
            /// press is answered with the reason instead of with nothing.
            /// </summary>
            public bool NeedsPage = true;
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
            "You are the assistant inside NextScan Studio, a scanner application used in a print " +
            "and copy shop. The operator scans identity papers, bills, forms and certificates, in " +
            "Bengali, English, or both on one page.\n\n" +
            "When a scan is attached, answer about that page. If something is not on it, say so " +
            "rather than filling it in from what documents of that kind usually say -- an " +
            "invented field on an identity paper is worse than a missing one.\n\n" +
            "When no scan is attached, simply answer the question.\n\n" +
            "Reply in the language the operator writes in, and keep names, numbers and dates " +
            "exactly as they are written. Write plainly: no headings unless the page has them, " +
            "and no preamble about what you are about to do.";

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
            _tips.SetToolTip(_fresh, "New conversation");
            _bar.Controls.Add(_fresh);

            _past = new NsIconButton { Icon = NsIcon.History };
            _past.Click += delegate { OpenHistory(); };
            _tips.SetToolTip(_past, "Earlier conversations");
            _bar.Controls.Add(_past);
        }

        void BuildTranscript()
        {
            _scroll = new Panel { BackColor = Theme.Surface, AutoScroll = true, Visible = false };
            Controls.Add(_scroll);

            // The bubbles go in a column inside the scrolling panel rather than
            // in the panel itself, so a re-layout -- which is every token that
            // arrives -- moves one control instead of all of them. The column
            // is placed at the scrolled origin, never at (0, 0); see
            // LayoutTranscript.
            _column = new Panel { BackColor = Theme.Surface, Location = new Point(0, 0) };
            _scroll.Controls.Add(_column);

            // A wheel over the gaps needs nothing: the column does not handle
            // it, so it climbs to the scrolling panel, which does. Only the text
            // of a turn eats it, and the bubble hands that on (OnTranscriptWheel).
            // Handling it here as well scrolled twice for every notch.
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

                bool page = HasPage();
                using (Font f = Theme.UiSemi(12f))
                    TextRenderer.DrawText(g, page ? "About this page" : "Ask anything", f,
                        new Rectangle(12, top + 40, Math.Max(1, _welcome.Width - 24), 26), Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

                if (page) return;

                // The five below are all about a page, and there is not one. The
                // box still works, so the panel says which half is available
                // rather than greying the whole thing out.
                using (Font f = Theme.Ui(8.25f))
                    TextRenderer.DrawText(g, "Scan a page to ask about it", f,
                        new Rectangle(12, top + 62, Math.Max(1, _welcome.Width - 24), 20), Theme.TextFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            };
            Controls.Add(_welcome);

            foreach (Suggestion suggestion in Suggestions)
            {
                Suggestion which = suggestion;
                NsPill chip = new NsPill { Text = suggestion.Title, Kind = PillKind.Normal, Radius = 17 };
                chip.Font = Theme.Ui(8.75f);
                chip.Click += delegate { Send(which.Title, which.Ask, which.NeedsPage); };
                _tips.SetToolTip(chip, "Needs a scanned page");
                _welcome.Controls.Add(chip);
                _chips.Add(chip);
            }
        }

        int WelcomeTop()
        {
            int block = 74 + _chips.Count * 40;
            return Math.Max(14, (_welcome.Height - block) / 2 - 10);
        }

        /// <summary>
        /// Is there a page to talk about.
        ///
        /// One place, because there were two and they disagreed: the empty state
        /// asked whether the source returned anything and said "About this
        /// page", while the box asked whether the note was non-empty and said
        /// "Ask anything", in the same panel at the same moment.
        /// </summary>
        bool HasPage()
        {
            RawImage page = PageSource == null ? null : PageSource();
            return page != null && page.IsValid;
        }

        void BuildComposer()
        {
            _composer = new NsComposer { Placeholder = "Ask anything" };
            _composer.Submit += delegate { Send(_composer.Text, null, false); };
            _composer.Typed += delegate { UpdateSend(); };
            _composer.HeightWanted += delegate { LayoutAll(); };
            _composer.FootChanged += delegate { LayoutComposer(); };
            Controls.Add(_composer);

            // Model and effort, beside the send button. This is the one place
            // every real chat interface agrees on. They are two buttons with a
            // mark each rather than one: they are two different questions, and
            // a single menu carrying both made the answer to either of them
            // three clicks deep.
            _modelButton = new NsPill { Kind = PillKind.Quiet, Radius = 11, Icon = NsIcon.Model };
            _modelButton.Font = Theme.Ui(8f);
            _modelButton.Click += delegate { OpenModelMenu(); };
            _composer.Controls.Add(_modelButton);

            _effortButton = new NsPill { Kind = PillKind.Quiet, Radius = 11, Icon = NsIcon.Effort };
            _effortButton.Font = Theme.Ui(8f);
            _effortButton.Click += delegate { OpenEffortMenu(); };
            _composer.Controls.Add(_effortButton);

            _send = new NsIconButton { Icon = NsIcon.Send, Circle = true, Raised = true };
            _send.Click += delegate
            {
                if (_busy) Stop();
                else Send(_composer.Text, null, false);
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
        /// Which models the panel may offer, as chosen in Settings. Empty until
        /// the operator has said, and then it is exactly what they said.
        /// </summary>
        public AiAllowed Allowed = AiAllowed.Read("");

        /// <summary>
        /// The models, by provider.
        ///
        /// The list is the provider's own, fetched with the operator's key and
        /// never written down here: what an account can reach depends on the
        /// account, and a list in our source would be wrong by the next release
        /// with nothing to say so. The first time this opens for a key it has
        /// nothing to show, so it says it is asking and has them by the time it
        /// is opened again.
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

                if (AiModels.Cached(provider) == null)
                {
                    menu.Note("asking " + provider.Info.Name + "...");
                    anyMissing = true;
                    BeginFetch(provider);
                    continue;
                }

                IList<AiModel> models = AiModels.Offered(provider, Allowed);
                if (models.Count == 0) { menu.Note("none chosen in Settings"); continue; }

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

            menu.Picked += delegate (object tag) { Took(tag); };
            menu.Show(_modelButton, Math.Max(210, Width - 28));

            if (anyMissing) Say("Asking the provider which models this key can reach");
        }

        /// <summary>
        /// How hard it thinks. Its own button and its own menu, because it is
        /// its own question: the model is chosen once for a way of working, the
        /// effort changes with what is being asked.
        /// </summary>
        void OpenEffortMenu()
        {
            NsChoiceMenu menu = new NsChoiceMenu();
            IAiProvider now = Provider();

            menu.Header("Effort", now.Info.Name);
            foreach (ThinkingLevel level in new[] { ThinkingLevel.Low, ThinkingLevel.Medium,
                                                    ThinkingLevel.High, ThinkingLevel.Max })
            {
                ThinkingLevel which = level;
                string note = level > now.Info.Ceiling
                    ? "uses " + now.Info.Ceiling.ToString().ToLowerInvariant()
                    : "";
                menu.Item(which.ToString(), note, which == _thinking, new Chosen { Effort = which });
            }

            menu.Picked += delegate (object tag) { Took(tag); };
            menu.Show(_effortButton, 190);
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
                _modelButton.Text = "Add a key";
                _tips.SetToolTip(_modelButton, "Settings, under Assistant, takes the key");
            }
            else if (string.IsNullOrEmpty(_model))
            {
                _modelButton.Text = provider.Info.Name;
                _tips.SetToolTip(_modelButton, "Choose a model");
            }
            else
            {
                _modelButton.Text = Short(AiModels.NameOf(provider, _model));
                _tips.SetToolTip(_modelButton, provider.Info.Name + " — " +
                                               AiModels.NameOf(provider, _model));
            }

            _effortButton.Text = _thinking.ToString().ToLowerInvariant();
            string note = provider.Info.Describe(_thinking);
            _tips.SetToolTip(_effortButton, note.Length > 0 ? note : "How hard it thinks");
            _effortButton.ForeColor = note.Length > 0 ? Theme.Warn : Theme.Text;

            LayoutComposer();
            UpdateChip();
        }

        /// <summary>
        /// A model name that fits on a button in a panel this narrow.
        ///
        /// The provider's display names run to "Gemini 3.1 Pro Preview", and the
        /// first two words are the ones that tell them apart.
        /// </summary>
        static string Short(string name)
        {
            if (name.Length <= 16) return name;

            string[] words = name.Split(' ');
            if (words.Length <= 2) return name;

            // Drop the maker's name from the front when there is one: the
            // provider is already said by the menu this came from.
            int from = (words[0].Length > 3 && char.IsLetter(words[0][0])) ? 1 : 0;

            // Three words, not two: two made "3.1 Flash" of both Flash and
            // Flash Lite, and the button could not say which one was chosen.
            // "Preview" goes first, since the menu says it in full.
            var kept = new List<string>();
            for (int i = from; i < words.Length && kept.Count < 3; i++)
                if (!words[i].Equals("Preview", StringComparison.OrdinalIgnoreCase)) kept.Add(words[i]);
            return kept.Count == 0 ? name : string.Join(" ", kept.ToArray());
        }

        // =====================================================================
        // Sending
        // =====================================================================

        /// <summary>
        /// One turn. <paramref name="ask"/> is the full question when it came
        /// from a suggestion, and null when the operator typed it, in which case
        /// <paramref name="shown"/> is both.
        /// </summary>
        void Send(string shown, string ask) { Send(shown, ask, false); }

        void Send(string shown, string ask, bool needsPage)
        {
            if (_busy) return;

            string text = (ask ?? shown ?? "").Trim();
            if (text.Length == 0) return;

            // Asked about a page, with no page. Say so rather than paying for an
            // answer that can only be "there is nothing here".
            if (needsPage && !_pageSent && !HasPage())
            {
                AddNote("That one is about a scanned page, and there is none yet. " +
                        "Preview or scan something first.", true);
                return;
            }

            IAiProvider provider = Provider();
            if (!provider.Ready)
            {
                AddNote("No key has been set for " + provider.Info.Name +
                        ". Settings, under Assistant, takes one.", true);
                return;
            }

            RawImage page = PageSource == null ? null : PageSource();
            byte[] image = null;

            // The page rides on one turn and is never sent again: it is the
            // largest cost in this panel, and the model already has it in the
            // conversation afterwards.
            if (!_pageSent && page != null)
            {
                image = Encode(page);
                if (image == null)
                {
                    AddNote("That page could not be prepared for sending.", true);
                    return;
                }
                _pageSent = true;
                _pageFor = PageNote == null ? "" : PageNote();
            }

            _composer.Text = "";
            UpdateSend();
            AddBubble(shown ?? text, true, false);

            AiMessage turn = AiMessage.FromUser(text);
            if (image != null)
            {
                turn.Image = image;
                turn.ImageMediaType = "image/jpeg";
            }
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
                // Not cleared: the status bar says what went wrong, in one line.
                int cut = trouble.IndexOf('\n');
                Say(cut > 0 ? trouble.Substring(0, cut) : trouble);
                return;
            }

            AiReply reply = done.Result;
            string text = reply == null ? "" : (reply.Text ?? "");
            if (text.Trim().Length == 0) text = "(the model returned nothing)";

            if (_live != null) { _live.Pending = false; _live.Text = text; }
            _history.Add(AiMessage.FromAssistant(text));
            _live = null;

            Count(reply);
            Keep();
            LayoutTranscript(true);
            Say("");
        }

        /// <summary>
        /// Writes the conversation out, after every completed turn.
        ///
        /// Not when it ends: a conversation does not end, the window closes, and
        /// a history written at the end is a history that is never written.
        /// </summary>
        void Keep()
        {
            if (_chatId.Length == 0) _chatId = AiHistory.NewId();
            AiHistory.Save(_chatId, _history, Provider().Info.Name,
                           AiModels.NameOf(Provider(), _model), _pageFor);
        }

        /// <summary>
        /// Earlier conversations, newest first.
        ///
        /// What is reopened is what was said, not what was looked at: the page
        /// is not kept, because a folder of scanned identity papers left on a
        /// shop machine is a different thing from a folder of text about them.
        /// The panel says so when one is opened.
        /// </summary>
        void OpenHistory()
        {
            IList<AiChat> recent = AiHistory.Recent(30);
            NsChoiceMenu menu = new NsChoiceMenu();

            if (recent.Count == 0) menu.Note("Nothing yet.");
            else
            {
                menu.Header("Earlier", recent.Count + "");
                foreach (AiChat chat in recent)
                {
                    AiChat which = chat;
                    menu.Item(chat.Title, When(chat.When), chat.Id == _chatId, which.Id);
                }
                menu.Rule();
                menu.Item("Forget all of them", "", false, ForgetAll);
            }

            menu.Picked += delegate (object tag) { Reopen(tag); };
            menu.Show(_past, Math.Max(240, Width - 28));
        }

        static readonly object ForgetAll = new object();

        static string When(DateTime when)
        {
            if (when == default(DateTime)) return "";
            TimeSpan ago = DateTime.Now - when;
            if (ago.TotalHours < 1) return Math.Max(1, (int)ago.TotalMinutes) + " min";
            if (ago.TotalHours < 24) return (int)ago.TotalHours + " h";
            if (ago.TotalDays < 7) return (int)ago.TotalDays + " d";
            return when.ToString("d MMM");
        }

        void Reopen(object tag)
        {
            if (tag == ForgetAll)
            {
                int gone = AiHistory.ForgetAll();
                _chatId = "";
                Say(gone + (gone == 1 ? " conversation forgotten" : " conversations forgotten"));
                return;
            }

            string id = tag as string;
            if (id == null) return;

            AiChat chat = AiHistory.Load(id);
            if (chat == null) { Say("That conversation could not be read."); return; }

            Reset();
            _chatId = chat.Id;
            _pageFor = chat.Page;

            // Marked as sent so the page now on the canvas is not quietly
            // attached to a conversation that was about a different one.
            _pageSent = chat.Page.Length > 0;

            foreach (AiMessage message in chat.Messages)
            {
                _history.Add(message);
                AddBubble(message.Text, message.Role == AiRole.User, false);
            }

            AddNote("Reopened" + (chat.Page.Length > 0 ? " — this was about " + chat.Page + ". " : ". ") +
                    "The page itself is not kept, so anything asked now is answered from what is " +
                    "written above.", false);

            UpdateChip();
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
            _modelButton.Enabled = !on;
            _effortButton.Enabled = !on;
            UpdateChip();
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
            if (known != null) return known.Message;

            string said = inner.Message ?? "";
            string hint = Hint(said);
            if (said.Length > 400) said = said.Substring(0, 400).TrimEnd() + "…";
            return hint.Length > 0 ? hint + "\n\n" + said : inner.GetType().Name + ": " + said;
        }

        /// <summary>
        /// What to do about the two failures that are about the model rather
        /// than the question. The newest model is not always one a key may use:
        /// Gemini 3.1 Pro answered a free key with a quota of zero, in a page of
        /// JSON that did not say the other models would have worked.
        /// </summary>
        static string Hint(string said)
        {
            string s = said.ToLowerInvariant();
            if (s.Contains("quota") || s.Contains("resource_exhausted") || s.Contains("rate limit") ||
                s.Contains("rate_limit") || s.Contains("429") || s.Contains("too many requests"))
                return "This model has no quota left on this key, or was asked too often. " +
                       "Choose another from the model button, or try again in a minute.";
            if (s.Contains("not_found") || s.Contains("404") || s.Contains("is not found") ||
                s.Contains("not supported") || s.Contains("does not exist") || s.Contains("no longer available"))
                return "This model is not available to this key. Choose another from the model button.";
            return "";
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
            bubble.Wheeled += OnTranscriptWheel;
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

        void OnTranscriptWheel(object sender, MouseEventArgs e)
        {
            // As far as the panel itself moves for a notch over a gap, so the
            // transcript does not change speed as the pointer crosses a turn.
            Scroll_(-e.Delta);
        }

        void Scroll_(int by)
        {
            if (_scroll == null || _column == null) return;

            int most = Math.Max(0, _column.Height - _scroll.ClientSize.Height);
            if (most <= 0) return;

            // AutoScrollPosition reads back negative and is set positive. It is
            // the one WinForms property that does not round-trip, and reading it
            // as given is how a transcript scrolls the wrong way.
            int at = Math.Max(0, Math.Min(most, -_scroll.AutoScrollPosition.Y + by));
            _scroll.AutoScrollPosition = new Point(0, at);
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
            _pageSent = false;
            _chatId = "";
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

        /// <summary>
        /// Opening the panel is the operator asking for it, so this is the
        /// moment to find out what their key can reach.
        ///
        /// It is a list, not a question: no provider bills for it, and nothing
        /// is generated. The rule that nothing is sent unasked is about spending
        /// the shop's money, and this spends none -- while not doing it leaves
        /// the button saying "Gemini" when it could say which Gemini.
        /// </summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) return;

            IAiProvider provider = Provider();
            if (provider.Ready && AiModels.Cached(provider) == null) BeginFetch(provider);
        }

        const int Pad = 14;
        const int BarHeight = 32;
        const int MeterHeight = 16;

        void LayoutAll()
        {
            if (_bar == null || Width <= 0 || Height <= 0) return;

            _bar.SetBounds(0, 0, Width, BarHeight);
            _fresh.SetBounds(Width - Pad - 26, 4, 26, 24);
            _past.SetBounds(Width - Pad - 58, 4, 26, 24);

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
            right -= round + 4;

            int effortWidth = Math.Min(TextWidth(_effortButton) + 34, Math.Max(44, right - 60));
            _effortButton.SetBounds(right - effortWidth, foot - 28, effortWidth, 26);
            right -= effortWidth + 2;

            int modelWidth = Math.Min(TextWidth(_modelButton) + 34, Math.Max(52, right - 12));
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

            // Once the scan has gone, the conversation is about that page even
            // if a different one is now on the canvas. Saying so is the
            // difference between a stale answer and a wrong one.
            if (_pageSent)
                _composer.FootNote = _pageFor.Length > 0 && _pageFor != page
                    ? "about " + _pageFor
                    : _pageFor;
            else
                _composer.FootNote = page.Length > 0 ? page : "";

            bool has = _pageSent || HasPage();
            foreach (NsPill chip in _chips) chip.Enabled = !_busy;

            // The box says what it will do with what you type, and that changes
            // when there is a page to attach to it.
            _composer.Placeholder = has ? "Ask about this page" : "Ask anything";
            if (_welcome != null) _welcome.Invalidate();
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

            // The scroll bar's width is kept back whether or not it is showing.
            // Sized to the client area instead, the column was as wide as the
            // panel until the bar appeared, then wider than what was left, which
            // brought a horizontal bar, which took height, which moved the
            // vertical one again.
            int width = Math.Max(100, _scroll.Width - SystemInformation.VerticalScrollBarWidth);
            int inner = Math.Max(80, width - Pad * 2);

            int y = 10;
            _column.SuspendLayout();
            foreach (NsBubble bubble in _bubbles)
            {
                bubble.Width = inner;                       // measure at the width it will be drawn at
                int h = bubble.MeasureHeight(inner);
                bubble.SetBounds(Pad, y, inner, h);
                y += h + 8;
            }

            // A child of a scrolled panel is placed in what is on screen, not in
            // the whole of it. Put back at (0, 0) after the operator had scrolled,
            // the column moved down by however far that was, and a reopened
            // conversation was drawn below an empty screen of its own height.
            Point shown = _scroll.AutoScrollPosition;
            _column.SetBounds(shown.X, shown.Y, width, y + 4);
            _column.ResumeLayout();

            if (toEnd && _bubbles.Count > 0)
                _scroll.AutoScrollPosition = new Point(0, Math.Max(0, _column.Height - _scroll.ClientSize.Height));
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
