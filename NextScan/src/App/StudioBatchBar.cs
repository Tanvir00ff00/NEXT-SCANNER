// =============================================================================
// NextScan Studio - the batch strip on the scanning page
// Plan ref: MASTER_PLAN section 12 (jobs & batch), 13 (studio UI).
//
// Batch scanning used to live in a settings drawer: choose a source, open the
// File panel, set a split rule, come back, press Scan. That is a configuration
// screen wearing a feature's name. An operator with a stack of paper in their
// hand wants to look at one line, agree with it, and press one button.
//
// So this is a single strip, on the page they are already looking at, that is
// the same object through the whole job:
//
//   armed     Feeder  |  Until empty  |  Split: none  |  PDF  |  [Start batch]
//   running   Page 7  |  3 documents  |  0:42  |  9.9 ppm     |  [Stop]
//   finished  24 pages, 3 documents                           |  [Save now]
//
// Every field in the armed state is a control, not a label: click it and a small
// menu opens on the spot. Nothing here navigates anywhere, because a batch is
// decided in the five seconds between putting paper in the tray and starting,
// and any navigation in that window is a reason to give up and scan one page at
// a time instead.
//
// Owner-drawn as one control with hit-tested zones, the same way the device bar
// in the title bar is built - chips as separate child controls would each need
// their own focus, hover and theme handling for no gain.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public enum BatchBarState
    {
        Armed = 0,
        Running = 1,
        Finished = 2,
    }

    public class NsBatchBar : NsBase
    {
        // ---------------------------------------------------------------- model
        public StudioSettings Settings;

        public BatchBarState State = BatchBarState.Armed;

        /// <summary>Pages delivered so far in the running job.</summary>
        public int PagesDone;
        public int DocumentsDone;
        public DateTime StartedAt = DateTime.MinValue;

        public string FinishedNote = "";

        public event EventHandler StartRequested;
        public event EventHandler StopRequested;
        public event EventHandler SaveRequested;
        public event EventHandler SettingsChanged;

        // ---------------------------------------------------------------- layout
        class Zone
        {
            public Rectangle Rect;
            public string Label = "";
            public string Value = "";
            public Action<Point> OnClick;
            public bool IsButton;
            public bool Primary;
            public bool Danger;
            public bool Enabled = true;
        }

        readonly List<Zone> _zones = new List<Zone>();
        int _hot = -1;

        const int Pad = 12;
        const int Gap = 8;
        const int BarHeight = 52;

        public NsBatchBar()
        {
            Height = BarHeight;
            Font = Theme.Ui(9f);
            SetStyle(ControlStyles.Selectable, false);
        }

        public static int PreferredHeight { get { return BarHeight; } }

        // =====================================================================
        // Building the strip
        // =====================================================================
        void Rebuild()
        {
            _zones.Clear();
            if (Settings == null) return;

            int x = Pad;
            switch (State)
            {
                case BatchBarState.Armed: x = BuildArmed(x); break;
                case BatchBarState.Running: x = BuildRunning(x); break;
                default: x = BuildFinished(x); break;
            }
        }

        int BuildArmed(int x)
        {
            bool feeder = Settings.Source == PaperSource.Feeder ||
                          Settings.Source == PaperSource.FeederDuplex;

            x = AddField(x, "Source", SourceName(Settings.Source), delegate (Point p)
            {
                string[] items = { "Flatbed", "Feeder", "Feeder, both sides" };
                int sel = Settings.Source == PaperSource.Feeder ? 1
                        : Settings.Source == PaperSource.FeederDuplex ? 2 : 0;
                NsMenu.Show(this, p, items, sel, 180, delegate (int i)
                {
                    Settings.Source = i == 1 ? PaperSource.Feeder
                                    : i == 2 ? PaperSource.FeederDuplex : PaperSource.Flatbed;
                    Changed();
                });
            });

            // How many sheets. Meaningless on a flatbed, which holds one.
            x = AddField(x, "Pages", feeder ? PagesName() : "One sheet", delegate (Point p)
            {
                if (!feeder) return;
                string[] items = { "Until the tray is empty", "5", "10", "20", "50", "100" };
                int sel = Settings.BatchUntilEmpty ? 0 : IndexOfCount(Settings.PageCount);
                NsMenu.Show(this, p, items, sel, 200, delegate (int i)
                {
                    if (i == 0) Settings.BatchUntilEmpty = true;
                    else
                    {
                        Settings.BatchUntilEmpty = false;
                        Settings.PageCount = int.Parse(items[i], CultureInfo.InvariantCulture);
                    }
                    Changed();
                });
            }, feeder);

            x = AddField(x, "Split", SplitName(), delegate (Point p)
            {
                string[] items = { "One document", "Every 2 pages", "Every 4 pages", "On a blank sheet" };
                int sel = Settings.BatchSeparation == SeparationRule.BlankPage ? 3
                        : Settings.BatchSeparation == SeparationRule.FixedPageCount
                            ? (Settings.PagesPerDocument == 4 ? 2 : 1)
                            : 0;
                NsMenu.Show(this, p, items, sel, 190, delegate (int i)
                {
                    if (i == 0) Settings.BatchSeparation = SeparationRule.None;
                    else if (i == 3) Settings.BatchSeparation = SeparationRule.BlankPage;
                    else
                    {
                        Settings.BatchSeparation = SeparationRule.FixedPageCount;
                        Settings.PagesPerDocument = i == 2 ? 4 : 2;
                    }
                    Changed();
                });
            });

            x = AddField(x, "Save as", FormatName(), delegate (Point p)
            {
                string[] items = { "PDF, one file", "PDF, one per page", "TIFF, one file", "JPEG", "PNG" };
                int sel = FormatIndex();
                NsMenu.Show(this, p, items, sel, 190, delegate (int i)
                {
                    switch (i)
                    {
                        case 0: Settings.OutputFormat = "pdf"; Settings.MultiPageFile = true; break;
                        case 1: Settings.OutputFormat = "pdf"; Settings.MultiPageFile = false; break;
                        case 2: Settings.OutputFormat = "tif"; Settings.MultiPageFile = true; break;
                        case 3: Settings.OutputFormat = "jpg"; break;
                        default: Settings.OutputFormat = "png"; break;
                    }
                    Changed();
                });
            });

            x = AddField(x, "Blanks", Settings.DropBlankPages ? "Skipped" : "Kept", delegate (Point p)
            {
                string[] items = { "Keep blank pages", "Skip blank pages" };
                NsMenu.Show(this, p, items, Settings.DropBlankPages ? 1 : 0, 190, delegate (int i)
                {
                    Settings.DropBlankPages = (i == 1);
                    Changed();
                });
            });

            AddButton("Start batch", true, false, delegate (Point p)
            {
                if (StartRequested != null) StartRequested(this, EventArgs.Empty);
            });
            return x;
        }

        int BuildRunning(int x)
        {
            x = AddField(x, "Received", PagesDone + (PagesDone == 1 ? " page" : " pages"), null, false);
            if (DocumentsDone > 0)
                x = AddField(x, "Documents", DocumentsDone.ToString(CultureInfo.InvariantCulture), null, false);
            x = AddField(x, "Elapsed", Elapsed(), null, false);
            x = AddField(x, "Rate", Rate(), null, false);

            AddButton("Stop", false, true, delegate (Point p)
            {
                if (StopRequested != null) StopRequested(this, EventArgs.Empty);
            });
            return x;
        }

        int BuildFinished(int x)
        {
            x = AddField(x, "Batch", FinishedNote, null, false);
            AddButton("Save now", true, false, delegate (Point p)
            {
                if (SaveRequested != null) SaveRequested(this, EventArgs.Empty);
            });
            return x;
        }

        void Changed()
        {
            Rebuild();
            Invalidate();
            if (SettingsChanged != null) SettingsChanged(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------- zones
        int AddField(int x, string label, string value, Action<Point> onClick, bool enabled = true)
        {
            int w = MeasureField(label, value);
            Zone z = new Zone
            {
                Rect = new Rectangle(x, 8, w, Height - 16),
                Label = label,
                Value = value,
                OnClick = onClick,
                Enabled = enabled && onClick != null
            };
            _zones.Add(z);
            return x + w + Gap;
        }

        void AddButton(string text, bool primary, bool danger, Action<Point> onClick)
        {
            int w;
            using (Graphics g = CreateGraphics())
            using (Font f = Theme.UiSemi(9.5f))
                w = Math.Max(112, TextRenderer.MeasureText(g, text, f).Width + 34);

            // Anchored to the right edge: the action is always in the same place
            // whatever the fields in front of it happen to say.
            _zones.Add(new Zone
            {
                Rect = new Rectangle(Math.Max(Pad, Width - Pad - w), 8, w, Height - 16),
                Label = "",
                Value = text,
                OnClick = onClick,
                IsButton = true,
                Primary = primary,
                Danger = danger
            });
        }

        int MeasureField(string label, string value)
        {
            using (Graphics g = CreateGraphics())
            using (Font lf = Theme.Ui(7.5f))
            using (Font vf = Theme.UiSemi(9f))
            {
                int a = TextRenderer.MeasureText(g, label, lf).Width;
                int b = TextRenderer.MeasureText(g, value, vf).Width;
                return Math.Max(64, Math.Max(a, b) + 26);
            }
        }

        // ---------------------------------------------------------------- naming
        static string SourceName(PaperSource s)
        {
            if (s == PaperSource.Feeder) return "Feeder";
            if (s == PaperSource.FeederDuplex) return "Feeder, 2 sides";
            if (s == PaperSource.Film) return "Film";
            return "Flatbed";
        }

        string PagesName()
        {
            return Settings.BatchUntilEmpty
                ? "Until empty"
                : Math.Max(1, Settings.PageCount).ToString(CultureInfo.InvariantCulture);
        }

        static int IndexOfCount(int n)
        {
            int[] known = { 0, 5, 10, 20, 50, 100 };
            for (int i = 1; i < known.Length; i++) if (known[i] == n) return i;
            return 1;
        }

        string SplitName()
        {
            if (Settings.BatchSeparation == SeparationRule.BlankPage) return "Blank sheet";
            if (Settings.BatchSeparation == SeparationRule.FixedPageCount)
                return "Every " + Math.Max(1, Settings.PagesPerDocument);
            return "One document";
        }

        string FormatName()
        {
            string f = (Settings.OutputFormat ?? "jpg").ToLowerInvariant();
            if (f == "pdf") return Settings.MultiPageFile ? "PDF" : "PDF pages";
            if (f == "tif" || f == "tiff") return Settings.MultiPageFile ? "TIFF" : "TIFF pages";
            return f.ToUpperInvariant();
        }

        int FormatIndex()
        {
            string f = (Settings.OutputFormat ?? "jpg").ToLowerInvariant();
            if (f == "pdf") return Settings.MultiPageFile ? 0 : 1;
            if (f == "tif" || f == "tiff") return 2;
            if (f == "png") return 4;
            return 3;
        }

        string Elapsed()
        {
            if (StartedAt == DateTime.MinValue) return "0:00";
            TimeSpan t = DateTime.Now - StartedAt;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        string Rate()
        {
            if (StartedAt == DateTime.MinValue || PagesDone == 0) return "—";
            double minutes = (DateTime.Now - StartedAt).TotalMinutes;
            if (minutes < 0.02) return "—";
            return string.Format(CultureInfo.InvariantCulture, "{0:N1} ppm", PagesDone / minutes);
        }

        // =====================================================================
        // Interaction
        // =====================================================================
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Rebuild();
            Invalidate();
        }

        /// <summary>Call after changing state or counters.</summary>
        public void Refresh_()
        {
            Rebuild();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = ZoneAt(e.Location);
            if (hit == _hot) return;
            _hot = hit;
            Cursor = (hit >= 0 && _zones[hit].Enabled) ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int hit = ZoneAt(e.Location);
            if (hit < 0) return;

            Zone z = _zones[hit];
            if (!z.Enabled || z.OnClick == null) return;

            // Menus open under the field they belong to, so the eye does not
            // have to find them.
            Point screen = PointToScreen(new Point(z.Rect.Left, z.Rect.Bottom + 6));
            z.OnClick(screen);
        }

        int ZoneAt(Point p)
        {
            for (int i = 0; i < _zones.Count; i++) if (_zones[i].Rect.Contains(p)) return i;
            return -1;
        }

        // =====================================================================
        // Painting
        // =====================================================================
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            if (_zones.Count == 0) Rebuild();

            Rectangle body = new Rectangle(0, 0, Width - 1, Height - 1);
            if (body.Width <= 2 || body.Height <= 2) return;

            using (GraphicsPath path = Theme.Round(body, 12))
            {
                using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillPath(b, path);
                using (Pen p = new Pen(State == BatchBarState.Running
                                        ? Theme.Mix(Theme.Line, Theme.Accent, 0.55)
                                        : Theme.Line, 1f))
                    g.DrawPath(p, path);
            }

            for (int i = 0; i < _zones.Count; i++) PaintZone(g, _zones[i], i == _hot);

            if (State == BatchBarState.Running) PaintProgress(g, body);
        }

        void PaintZone(Graphics g, Zone z, bool hot)
        {
            if (z.IsButton)
            {
                Color fill = z.Danger ? Theme.Danger : (z.Primary ? Theme.Accent : Theme.Field);
                if (hot) fill = Theme.Mix(fill, Color.White, 0.12);

                using (GraphicsPath path = Theme.Round(z.Rect, 9))
                using (SolidBrush b = new SolidBrush(fill))
                    g.FillPath(b, path);

                TextRenderer.DrawText(g, z.Value, Theme.UiSemi(9.5f), z.Rect,
                    (z.Primary || z.Danger) ? Theme.OnAccent : Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }

            if (hot && z.Enabled)
            {
                using (GraphicsPath path = Theme.Round(z.Rect, 8))
                using (SolidBrush b = new SolidBrush(Theme.Hover))
                    g.FillPath(b, path);
            }

            // Label above, value below: the value is what is read at a glance and
            // the label is only needed the first few times.
            Rectangle labelRect = new Rectangle(z.Rect.X + 10, z.Rect.Y + 3, z.Rect.Width - 20, 13);
            Rectangle valueRect = new Rectangle(z.Rect.X + 10, z.Rect.Y + 15, z.Rect.Width - 20, z.Rect.Height - 17);

            TextRenderer.DrawText(g, z.Label.ToUpperInvariant(), Theme.Ui(6.75f), labelRect,
                Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, z.Value, Theme.UiSemi(9f), valueRect,
                z.Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            // A small caret marks the fields that open something, so the ones
            // that are only readouts do not look broken when clicked.
            if (z.Enabled && z.OnClick != null)
            {
                int cx = z.Rect.Right - 11, cy = z.Rect.Bottom - 13;
                using (Pen p = new Pen(hot ? Theme.Accent : Theme.TextFaint, 1.4f))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    g.DrawLine(p, cx - 4, cy - 2, cx, cy + 2);
                    g.DrawLine(p, cx, cy + 2, cx + 4, cy - 2);
                }
            }
        }

        /// <summary>
        /// A hairline along the bottom edge. Determinate when a page count was
        /// asked for; otherwise a slow sweep, because a feeder run until empty
        /// has no total to be a fraction of and a bar pretending otherwise is a
        /// lie the operator will notice at page 51 of "50".
        /// </summary>
        void PaintProgress(Graphics g, Rectangle body)
        {
            int h = 3;
            Rectangle track = new Rectangle(1, body.Bottom - h, body.Width - 2, h);

            bool determinate = Settings != null && !Settings.BatchUntilEmpty && Settings.PageCount > 1;
            if (determinate)
            {
                double f = Math.Max(0.0, Math.Min(1.0, PagesDone / (double)Settings.PageCount));
                Rectangle fill = new Rectangle(track.X, track.Y, (int)(track.Width * f), track.Height);
                if (fill.Width > 0)
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, fill);
                return;
            }

            double phase = (DateTime.Now.Ticks / 10000.0 % 1800.0) / 1800.0;
            int barWidth = Math.Max(60, track.Width / 5);
            int x = (int)(phase * (track.Width + barWidth)) - barWidth;
            Rectangle sweep = Rectangle.Intersect(track,
                new Rectangle(track.X + x, track.Y, barWidth, track.Height));
            if (sweep.Width > 0)
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, sweep);
        }
    }
}
