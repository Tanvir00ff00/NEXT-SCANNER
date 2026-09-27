// =============================================================================
// NextScan Studio - the settings page's own controls
// Plan ref: MASTER_PLAN section 13 (UI)
//
// The page was a grey list and a grey column of controls stretched across the
// window. The owner asked for it to be colourful, animated, and a page per
// icon with nothing folded away. So:
//
//   * every page has a colour of its own, carried by its tile in the list, the
//     banner across the top of the page, and the headings of its cards, so
//     where you are is seen before it is read;
//   * the list is one control that draws every entry, so the highlight can
//     slide from one entry to the next instead of jumping;
//   * a page's settings sit in cards of a readable width rather than across
//     the whole window, one card per group, nothing collapsible;
//   * a page arrives by sliding up and fading in over the one it replaces.
//
// Colour is for finding your way. The controls inside the cards keep the
// neutral palette, because a settings page is also where the scan colours are
// judged from.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>What a settings page is called, what it is for, and its colours.</summary>
    public class SettingsPageInfo
    {
        public string Title = "";
        public string Sub = "";
        public string Icon = "";
        public Color From;
        public Color To;

        public SettingsPageInfo(string title, string sub, string icon, string from, string to)
        {
            Title = title;
            Sub = sub;
            Icon = icon;
            From = Theme.Parse(from);
            To = Theme.Parse(to);
        }
    }

    static class Paint2
    {
        /// <summary>A diagonal gradient filling a rounded rectangle.</summary>
        public static void Gradient(Graphics g, Rectangle r, int radius, Color from, Color to, float angle)
        {
            if (r.Width <= 1 || r.Height <= 1) return;
            using (GraphicsPath path = Theme.Round(r, radius))
            using (var brush = new LinearGradientBrush(new Rectangle(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2), from, to, angle))
                g.FillPath(brush, path);
        }

        /// <summary>The page colours, a little deeper on the dark palette so white text still reads.</summary>
        public static Color Deep(Color c) { return Theme.IsLight ? c : Theme.Mix(c, Color.Black, 0.18); }

        public static Color Alpha(int a, Color c) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        /// <summary>Tokens as a person reads them: 1M, 200K, 8K.</summary>
        public static string Tokens(long n)
        {
            if (n <= 0) return "";
            if (n >= 1000000)
            {
                double m = n / 1000000.0;
                return (m >= 10 || Math.Abs(m - Math.Round(m)) < 0.05 ? Math.Round(m).ToString(CultureInfo.InvariantCulture)
                                                                     : m.ToString("0.#", CultureInfo.InvariantCulture)) + "M";
            }
            if (n >= 1000) return Math.Round(n / 1000.0).ToString(CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }

    // =========================================================================
    // The list of pages
    // =========================================================================
    /// <summary>
    /// The pages down the left: a coloured tile with the page's icon, its name,
    /// and a line saying what is in it. One control draws them all, so the
    /// highlight can glide to the entry that was clicked.
    /// </summary>
    public class NsSettingsNav : NsBase
    {
        readonly List<SettingsPageInfo> _pages = new List<SettingsPageInfo>();
        readonly List<Anim> _hover = new List<Anim>();
        readonly Anim _at = new Anim(0) { Rate = 0.24 };
        readonly Anim _pop = new Anim(1) { Rate = 0.16 };
        int _selected;
        int _hot = -1;

        public const int Top0 = 16;
        public const int Row = 58;

        /// <summary>Raised with the index of the page the operator chose.</summary>
        public event Action<int> Picked;

        public NsSettingsNav()
        {
            Font = Theme.UiSemi(9.75f);
            Cursor = Cursors.Hand;
            TabStop = true;
            Animator.Attach(this, _at, _pop);
        }

        public void SetPages(IList<SettingsPageInfo> pages, int selected)
        {
            _pages.Clear();
            _pages.AddRange(pages);
            _hover.Clear();
            foreach (SettingsPageInfo unused in pages)
            {
                var a = new Anim(0) { Rate = 0.25 };
                _hover.Add(a);
            }
            Animator.Attach(this, _hover.ToArray());
            _selected = Math.Max(0, Math.Min(pages.Count - 1, selected));
            _at.Snap(_selected);
            Invalidate();
        }

        public int Selected
        {
            get { return _selected; }
            set
            {
                int v = Math.Max(0, Math.Min(_pages.Count - 1, value));
                if (v == _selected) return;
                _selected = v;
                _at.Target = v;
                _pop.Snap(0);
                _pop.Target = 1;
                Animator.Kick();
                Invalidate();
            }
        }

        int IndexAt(Point p)
        {
            if (p.Y < Top0) return -1;
            int i = (p.Y - Top0) / Row;
            return i >= 0 && i < _pages.Count ? i : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i == _hot) return;
            if (_hot >= 0 && _hot < _hover.Count) _hover[_hot].Target = 0;
            _hot = i;
            if (_hot >= 0) _hover[_hot].Target = 1;
            Animator.Kick();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot >= 0 && _hot < _hover.Count) _hover[_hot].Target = 0;
            _hot = -1;
            Animator.Kick();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexAt(e.Location);
            if (i >= 0) Choose(i);
        }

        void Choose(int i)
        {
            if (i == _selected) return;
            Selected = i;
            if (Picked != null) Picked(i);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                Choose(Math.Max(0, Math.Min(_pages.Count - 1, _selected + (e.KeyCode == Keys.Down ? 1 : -1))));
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);
            if (_pages.Count == 0) return;

            // The highlight, where the animation has got to, in a colour
            // between the two pages' colours.
            double at = _at.Value;
            int lo = Math.Max(0, Math.Min(_pages.Count - 1, (int)Math.Floor(at)));
            int hi = Math.Max(0, Math.Min(_pages.Count - 1, lo + 1));
            double frac = at - lo;
            Color tint = Theme.Mix(_pages[lo].From, _pages[hi].From, frac);

            var plate = new Rectangle(10, Top0 + (int)Math.Round(at * Row), Width - 20, Row - 6);
            using (GraphicsPath path = Theme.Round(plate, 12))
            using (SolidBrush b = new SolidBrush(Paint2.Alpha(Theme.IsLight ? 34 : 52, tint)))
                g.FillPath(b, path);
            using (GraphicsPath path = Theme.Round(new Rectangle(plate.X, plate.Y + 14, 4, plate.Height - 28), 2))
            using (SolidBrush b = new SolidBrush(tint))
                g.FillPath(b, path);

            using (Font sub = Theme.Ui(8.25f))
            {
                for (int i = 0; i < _pages.Count; i++)
                {
                    SettingsPageInfo page = _pages[i];
                    int y = Top0 + i * Row;
                    double hot = _hover[i].Eased;
                    bool on = i == _selected;

                    if (!on && hot > 0.01)
                    {
                        using (GraphicsPath path = Theme.Round(new Rectangle(10, y, Width - 20, Row - 6), 12))
                        using (SolidBrush b = new SolidBrush(Paint2.Alpha((int)(22 * hot), Theme.Text)))
                            g.FillPath(b, path);
                    }

                    // The tile: the page's own gradient, always in colour. It
                    // lifts a little under the pointer and pops when chosen.
                    double lift = hot * 1.5;
                    double pop = on ? 1 + 0.10 * Math.Sin(Math.PI * _pop.Value) : 1;
                    int size = (int)Math.Round(36 * pop);
                    var tile = new Rectangle(22 - (size - 36) / 2, y + (Row - 6 - size) / 2 - (int)lift, size, size);
                    if (on || hot > 0.05)
                        Theme.Shadow(g, tile, 10, 4, (int)(on ? 60 : 40 * hot));
                    Paint2.Gradient(g, tile, 10, Paint2.Deep(page.From), Paint2.Deep(page.To), 45f);
                    using (GraphicsPath path = Theme.Round(new Rectangle(tile.X, tile.Y, tile.Width, tile.Height / 2), 10))
                    using (SolidBrush shine = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                        g.FillPath(shine, path);
                    NsIcon.Draw(g, page.Icon, new RectangleF(tile.X + 8, tile.Y + 8, tile.Width - 16, tile.Height - 16), Color.White);

                    int tx = 72;
                    Color title = on ? Theme.Text : Theme.Mix(Theme.TextDim, Theme.Text, hot);
                    TextRenderer.DrawText(g, page.Title, Font, new Rectangle(tx, y + 9, Width - tx - 14, 20), title,
                        TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, page.Sub, sub, new Rectangle(tx, y + 28, Width - tx - 14, 18), Theme.TextFaint,
                        TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }

            PaintKeyboardFocus(g);
        }
    }

    // =========================================================================
    // The banner across the top of a page
    // =========================================================================
    /// <summary>
    /// The page's name on its own colour, with its icon large, and what the
    /// page is for underneath. It plays in when the page arrives.
    /// </summary>
    public class NsHero : NsBase
    {
        SettingsPageInfo _page;
        readonly Anim _in = new Anim(1) { Rate = 0.12 };

        public NsHero()
        {
            Size = new Size(600, 128);
            TabStop = false;
            Animator.Attach(this, _in);
        }

        public void Show(SettingsPageInfo page, bool animate)
        {
            _page = page;
            if (animate) { _in.Snap(0); _in.Target = 1; Animator.Kick(); }
            else _in.Snap(1);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);
            if (_page == null) return;

            double t = _in.Eased;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color a = Paint2.Deep(_page.From), b = Paint2.Deep(_page.To);
            Theme.Shadow(g, r, 18, 6, 40);
            Paint2.Gradient(g, r, 18, a, b, 20f);

            // Soft rings drifting in on the right: decoration, and the part of
            // the banner that moves the most, so the arrival is felt.
            using (GraphicsPath clip = Theme.Round(r, 18))
            {
                Region keep = g.Clip;
                g.SetClip(clip);
                int drift = (int)((1 - t) * 40);
                using (SolidBrush ring = new SolidBrush(Color.FromArgb(26, 255, 255, 255)))
                {
                    g.FillEllipse(ring, Width - 190 + drift, -70, 220, 220);
                    g.FillEllipse(ring, Width - 90 + drift / 2, 40, 150, 150);
                }
                using (Pen line = new Pen(Color.FromArgb(40, 255, 255, 255), 1.5f))
                    g.DrawEllipse(line, Width - 260 + drift, 30, 120, 120);
                g.Clip = keep;
            }

            // The icon in a glass tile.
            int size = 64;
            var tile = new Rectangle(26, (Height - size) / 2, size, size);
            float grow = (float)(0.85 + 0.15 * t);
            var scaled = new RectangleF(tile.X + tile.Width * (1 - grow) / 2, tile.Y + tile.Height * (1 - grow) / 2,
                                        tile.Width * grow, tile.Height * grow);
            using (GraphicsPath path = Theme.Round(Rectangle.Round(scaled), 18))
            {
                using (SolidBrush glass = new SolidBrush(Color.FromArgb(46, 255, 255, 255))) g.FillPath(glass, path);
                using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1f)) g.DrawPath(edge, path);
            }
            NsIcon.Draw(g, _page.Icon, new RectangleF(scaled.X + 15 * grow, scaled.Y + 15 * grow,
                                                      scaled.Width - 30 * grow, scaled.Height - 30 * grow), Color.White);

            // The words, sliding in behind the icon.
            int tx = 112 + (int)((1 - t) * 18);
            int alpha = (int)(255 * t);
            using (Font big = Theme.UiSemi(17f))
            using (Font small = Theme.Ui(9.75f))
            {
                TextRenderer.DrawText(g, _page.Title, big, new Rectangle(tx, Height / 2 - 34, Width - tx - 30, 36),
                    Color.FromArgb(alpha, 255, 255, 255), TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, _page.Sub, small, new Rectangle(tx, Height / 2 + 4, Width - tx - 30, 40),
                    Color.FromArgb((int)(225 * t), 255, 255, 255),
                    TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // A card of settings
    // =========================================================================
    /// <summary>
    /// One group of settings on a raised plate, headed by its name and a stroke
    /// of the page's colour. Its children are ordinary controls: they clear to
    /// the card's colour because it is their nearest opaque parent.
    /// </summary>
    public class NsSettingsCard : Panel
    {
        public string Title = "";
        public string Note = "";
        public string Icon = "";
        public Color Accent = Theme.Accent;

        public const int Radius = 14;

        public NsSettingsCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Raised;
        }

        /// <summary>Where the first control goes: under the heading, or at the top of an untitled card.</summary>
        public int ContentTop { get { return Title.Length > 0 ? 50 : 18; } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            g.Clear(Parent != null ? Parent.BackColor : Theme.Ground);

            var r = new Rectangle(4, 2, Width - 9, Height - 9);
            if (r.Width <= 2 || r.Height <= 2) return;
            Theme.Shadow(g, r, Radius, 4, Theme.IsLight ? 38 : 70);
            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(Theme.Raised)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawPath(p, path);
            }

            if (Title.Length == 0) return;

            int x = 22;
            if (Icon.Length > 0)
            {
                var tile = new Rectangle(x, 16, 24, 24);
                using (GraphicsPath path = Theme.Round(tile, 7))
                using (SolidBrush b = new SolidBrush(Paint2.Alpha(Theme.IsLight ? 36 : 60, Accent)))
                    g.FillPath(b, path);
                NsIcon.Draw(g, Icon, new RectangleF(tile.X + 4, tile.Y + 4, 16, 16), Accent);
                x += 32;
            }
            else
            {
                using (GraphicsPath path = Theme.Round(new Rectangle(x, 20, 4, 16), 2))
                using (SolidBrush b = new SolidBrush(Accent))
                    g.FillPath(b, path);
                x += 12;
            }

            using (Font f = Theme.UiSemi(10.5f))
                TextRenderer.DrawText(g, Title, f, new Rectangle(x, 14, Width - x - 30, 28), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Note.Length > 0)
                using (Font f = Theme.Ui(8.25f))
                    TextRenderer.DrawText(g, Note, f, new Rectangle(22, 14, Width - 48, 28), Theme.TextFaint,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void OnPaint(PaintEventArgs e) { }
    }

    // =========================================================================
    // Arriving on a page
    // =========================================================================
    /// <summary>
    /// A picture of the old page fading out while a picture of the new one
    /// rises into place, laid over the real page for a fifth of a second.
    ///
    /// Moving the real controls would repaint every one of them each frame and
    /// tear; a picture moves in one blit. It removes itself when it is done,
    /// and it never takes a click that was meant for the page.
    /// </summary>
    public class NsPageTransition : Control
    {
        Bitmap _from, _to;
        readonly Timer _clock = new Timer { Interval = 15 };
        readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>How long the change takes. Long enough to be seen, short enough never to be waited for.</summary>
        public const int Milliseconds = 260;

        public NsPageTransition(Bitmap from, Bitmap to)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.Opaque, true);
            _from = from;
            _to = to;

            // Driven by the clock, not by paint: a window that is covered or
            // minimised is never painted, and a cover that only removed
            // itself when painted would still be there when it came back.
            _clock.Tick += delegate
            {
                if (_since.ElapsedMilliseconds >= Milliseconds) { _clock.Stop(); Dispose(); return; }
                Invalidate();
            };
            _clock.Start();
        }

        double Progress
        {
            get
            {
                double t = Math.Min(1.0, _since.ElapsedMilliseconds / (double)Milliseconds);
                return 1 - Math.Pow(1 - t, 3);      // ease out: quick to start, soft to land
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x0084, HTTRANSPARENT = -1;
            if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)HTTRANSPARENT; return; }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Ground);
            double t = Progress;

            if (_from != null && t < 1)
                Blend(g, _from, 1 - t, (int)(-10 * t));
            if (_to != null)
                Blend(g, _to, t, (int)((1 - t) * 26));
        }

        static void Blend(Graphics g, Bitmap image, double alpha, int dy)
        {
            if (alpha <= 0.01) return;
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)alpha };
                attributes.SetColorMatrix(matrix);
                g.DrawImage(image, new Rectangle(0, dy, image.Width, image.Height),
                            0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _clock.Stop();
                _clock.Dispose();
                if (_from != null) { _from.Dispose(); _from = null; }
                if (_to != null) { _to.Dispose(); _to = null; }
            }
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    // A status pill
    // =========================================================================
    public enum PillState { Idle, Busy, Good, Warn, Bad }

    /// <summary>"Connected", "No key", "Checking…": a coloured pill with a dot that breathes while busy.</summary>
    public class NsStatusPill : NsBase
    {
        PillState _state = PillState.Idle;
        readonly Anim _pulse = new Anim(0) { Rate = 0.06 };

        public NsStatusPill()
        {
            Size = new Size(120, 24);
            Font = Theme.UiSemi(8.25f);
            TabStop = false;
            Animator.Attach(this, _pulse);
        }

        public void Set(PillState state, string text)
        {
            _state = state;
            Text = text ?? "";
            Width = Math.Max(60, TextRenderer.MeasureText(Text, Font).Width + 34);
            if (state == PillState.Busy) Breathe();
            Invalidate();
        }

        void Breathe()
        {
            _pulse.Target = _pulse.Target > 0.5 ? 0 : 1;
            Animator.Kick();
        }

        Color Colour()
        {
            switch (_state)
            {
                case PillState.Good: return Theme.Good;
                case PillState.Warn: return Theme.Warn;
                case PillState.Bad: return Theme.Danger;
                case PillState.Busy: return Theme.Info;
                default: return Theme.TextFaint;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);
            if (_state == PillState.Busy && _pulse.Settled) Breathe();

            Color c = Colour();
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(r, Height / 2))
            using (SolidBrush b = new SolidBrush(Paint2.Alpha(Theme.IsLight ? 30 : 48, c)))
                g.FillPath(b, path);

            double glow = _state == PillState.Busy ? 0.3 + 0.7 * _pulse.Eased : 1;
            int d = 7, cy = (Height - d) / 2;
            using (SolidBrush halo = new SolidBrush(Paint2.Alpha((int)(80 * glow), c))) g.FillEllipse(halo, 8, cy - 3, d + 6, d + 6);
            using (SolidBrush dot = new SolidBrush(c)) g.FillEllipse(dot, 11, cy, d, d);

            Color ink = Theme.IsLight ? Theme.Mix(c, Color.Black, 0.25) : Theme.Mix(c, Color.White, 0.2);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(24, 0, Width - 28, Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================
    // A small icon button
    // =========================================================================
    /// <summary>A square button with an icon and a tooltip: reveal, remove, open a link.</summary>
    public class NsGlyphButton : NsBase
    {
        public string Icon = "";
        public Color? Tint;

        public NsGlyphButton()
        {
            Size = new Size(32, 32);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);
            double hot = HotAnim.Eased;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(r, 8))
            {
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Field, Theme.Hover, hot))) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Mix(Theme.Line, Tint ?? Theme.Accent, hot * 0.6), 1f)) g.DrawPath(p, path);
            }
            Color ink = Live(Tint.HasValue ? Theme.Mix(Theme.TextDim, Tint.Value, 0.4 + 0.6 * hot) : Theme.Mix(Theme.TextDim, Theme.Text, hot));
            int s = Math.Min(Width, Height) - 14;
            NsIcon.Draw(g, Icon, new RectangleF((Width - s) / 2f, (Height - s) / 2f, s, s), ink);
            PaintKeyboardFocus(g);
        }
    }

    // =========================================================================
    // Light or graphite, shown rather than named
    // =========================================================================
    /// <summary>
    /// The two palettes as two small pictures of the window, so the choice is
    /// made by looking at it. The chosen one is ringed and ticked.
    /// </summary>
    public class NsThemeChoice : NsBase
    {
        int _selected;
        int _hot = -1;
        readonly Anim[] _lift = { new Anim(0) { Rate = 0.25 }, new Anim(0) { Rate = 0.25 } };
        static readonly string[] Names = { "Light", "Graphite" };

        public event EventHandler SelectedIndexChanged;

        public NsThemeChoice()
        {
            Size = new Size(520, 170);
            Cursor = Cursors.Hand;
            TabStop = true;
            Animator.Attach(this, _lift);
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set { _selected = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        Rectangle Tile(int i)
        {
            int w = (Width - 18) / 2;
            return new Rectangle(i * (w + 18), 6, w, Height - 12);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hot = Tile(0).Contains(e.Location) ? 0 : Tile(1).Contains(e.Location) ? 1 : -1;
            if (hot == _hot) return;
            _hot = hot;
            for (int i = 0; i < 2; i++) _lift[i].Target = i == hot ? 1 : 0;
            Animator.Kick();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            _lift[0].Target = _lift[1].Target = 0;
            Animator.Kick();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < 2; i++) if (Tile(i).Contains(e.Location)) Pick(i);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Left || k == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { Pick(0); e.Handled = true; return; }
            if (e.KeyCode == Keys.Right) { Pick(1); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        void Pick(int i)
        {
            if (i == _selected) return;
            _selected = i;
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            for (int i = 0; i < 2; i++)
            {
                bool light = i == 0;
                bool on = i == _selected;
                Rectangle t = Tile(i);
                t.Offset(0, -(int)Math.Round(3 * _lift[i].Eased));
                Theme.Shadow(g, t, 12, 5, (int)(30 + 40 * _lift[i].Eased));

                Color ground = light ? Color.FromArgb(0xE8, 0xE8, 0xE8) : Color.FromArgb(0x16, 0x16, 0x16);
                Color surface = light ? Color.FromArgb(0xFA, 0xFA, 0xFA) : Color.FromArgb(0x20, 0x20, 0x20);
                Color raised = light ? Color.White : Color.FromArgb(0x29, 0x29, 0x29);
                Color ink = light ? Color.FromArgb(0x1B, 0x1B, 0x1B) : Color.FromArgb(0xED, 0xED, 0xED);
                Color accent = light ? Color.FromArgb(36, 88, 211) : Color.FromArgb(132, 174, 255);

                using (GraphicsPath path = Theme.Round(t, 12))
                using (SolidBrush b = new SolidBrush(ground)) g.FillPath(b, path);

                // A window in miniature: title bar, rail, page, panel.
                var win = new Rectangle(t.X + 12, t.Y + 12, t.Width - 24, t.Height - 52);
                using (GraphicsPath path = Theme.Round(win, 7))
                {
                    Region keep = g.Clip;
                    g.SetClip(path);
                    using (SolidBrush b = new SolidBrush(ground)) g.FillRectangle(b, win);
                    using (SolidBrush b = new SolidBrush(surface))
                    {
                        g.FillRectangle(b, win.X, win.Y, win.Width, 14);
                        g.FillRectangle(b, win.X, win.Y + 14, 16, win.Height);
                        g.FillRectangle(b, win.Right - 48, win.Y + 14, 48, win.Height);
                    }
                    using (SolidBrush b = new SolidBrush(accent))
                    {
                        g.FillEllipse(b, win.X + 5, win.Y + 20, 6, 6);
                        g.FillRectangle(b, win.Right - 42, win.Bottom - 14, 36, 8);
                    }
                    using (SolidBrush b = new SolidBrush(raised))
                        g.FillRectangle(b, win.X + 34, win.Y + 24, win.Width - 100, win.Height - 34);
                    using (SolidBrush b = new SolidBrush(Paint2.Alpha(60, ink)))
                    {
                        for (int k = 0; k < 4; k++) g.FillRectangle(b, win.Right - 42, win.Y + 22 + k * 9, 30 - k * 4, 4);
                        g.FillRectangle(b, win.X + 44, win.Y + 34, 60, 5);
                        g.FillRectangle(b, win.X + 44, win.Y + 44, 90, 4);
                    }
                    g.Clip = keep;
                    using (Pen p = new Pen(Paint2.Alpha(40, ink), 1f)) g.DrawPath(p, path);
                }

                using (Font f = Theme.UiSemi(9.5f))
                    TextRenderer.DrawText(g, Names[i], f, new Rectangle(t.X + 14, t.Bottom - 36, t.Width - 60, 26), ink,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                using (GraphicsPath path = Theme.Round(t, 12))
                using (Pen p = new Pen(on ? Theme.Accent : Paint2.Alpha(50, Theme.Text), on ? 2.5f : 1f))
                    g.DrawPath(p, path);

                if (on)
                {
                    var badge = new Rectangle(t.Right - 34, t.Bottom - 34, 22, 22);
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, badge);
                    NsIcon.Draw(g, NsIcon.Check, new RectangleF(badge.X + 3, badge.Y + 3, 16, 16), Theme.OnAccent);
                }
            }
            PaintKeyboardFocus(g);
        }
    }

    // =========================================================================
    // The models
    // =========================================================================
    /// <summary>
    /// Every model the connected providers returned, as rows the operator can
    /// tick, star and read: its name, its id, and what the provider itself
    /// says it can do. Owner-drawn and virtual, because a key can reach three
    /// hundred models and three hundred check boxes is three hundred windows.
    /// </summary>
    public class NsModelList : NsBase
    {
        public class Item
        {
            public bool Header;
            public string ProviderId = "";
            public string ProviderName = "";
            public Color Brand;
            public NextScan.Ai.AiModel Model;
            public bool On;
            public bool Default;
            public string Note = "";
            public int Count;
            public int CountOn;
        }

        readonly List<Item> _items = new List<Item>();
        readonly Anim _scroll = new Anim(0) { Rate = 0.3 };
        readonly Anim _hotRow = new Anim(0) { Rate = 0.3 };
        double _target;
        int _hot = -1;
        int _hotPart;       // 0 row, 1 star, 2 "all", 3 "none"
        bool _dragging;
        int _dragFrom;
        double _dragScroll;

        public const int RowH = 60;
        public const int HeadH = 44;

        public string Empty = "";

        public event Action<Item> Toggled;
        public event Action<Item> Starred;
        public event Action<string, bool> SetAll;

        public NsModelList()
        {
            Font = Theme.UiSemi(9.25f);
            TabStop = true;
            Animator.Attach(this, _scroll, _hotRow);
            WheelDelta += delegate (int delta) { ScrollTo(_target - delta / 120.0 * RowH * 1.5); };
        }

        public IList<Item> Items { get { return _items; } }

        public void SetItems(IEnumerable<Item> items, bool keepScroll)
        {
            _items.Clear();
            _items.AddRange(items);
            if (!keepScroll) { _target = 0; _scroll.Snap(0); }
            else ScrollTo(_target);
            _hot = -1;
            Invalidate();
        }

        int Total()
        {
            int h = 0;
            foreach (Item it in _items) h += it.Header ? HeadH : RowH;
            return h + 8;
        }

        int Most { get { return Math.Max(0, Total() - Height); } }

        void ScrollTo(double y)
        {
            _target = Math.Max(0, Math.Min(Most, y));
            _scroll.Target = _target;
            Animator.Kick();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A)
            {
                int delta = unchecked((short)((long)m.WParam >> 16));
                if (EatWheel(delta)) return;
            }
            base.WndProc(ref m);
        }

        int Hit(Point p, out int part)
        {
            part = 0;
            int y = -(int)Math.Round(_scroll.Value);
            for (int i = 0; i < _items.Count; i++)
            {
                Item it = _items[i];
                int h = it.Header ? HeadH : RowH;
                if (p.Y >= y && p.Y < y + h)
                {
                    if (it.Header)
                    {
                        int right = Width - 16;
                        if (p.X >= right - 44 && p.X < right) part = 3;
                        else if (p.X >= right - 90 && p.X < right - 50) part = 2;
                        else part = 0;
                    }
                    else if (p.X >= Width - 52 && p.X < Width - 14) part = 1;
                    return i;
                }
                y += h;
            }
            return -1;
        }

        Rectangle Thumb()
        {
            int total = Total();
            if (total <= Height) return Rectangle.Empty;
            int h = Math.Max(30, Height * Height / total);
            int y = (int)((Height - h) * (_scroll.Value / Math.Max(1, Most)));
            return new Rectangle(Width - 8, y, 5, h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging)
            {
                int total = Total();
                double per = (double)total / Math.Max(1, Height);
                _target = Math.Max(0, Math.Min(Most, _dragScroll + (e.Y - _dragFrom) * per));
                _scroll.Snap(_target);
                Invalidate();
                return;
            }
            int part;
            int i = Hit(e.Location, out part);
            if (i != _hot || part != _hotPart)
            {
                _hot = i;
                _hotPart = part;
                _hotRow.Snap(0);
                _hotRow.Target = 1;
                Animator.Kick();
                Cursor = i >= 0 && (!_items[i].Header || part >= 2) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty && e.X >= Width - 14)
            {
                _dragging = true;
                _dragFrom = e.Y;
                _dragScroll = _scroll.Value;
                if (!thumb.Contains(new Point(thumb.X, e.Y)))
                {
                    ScrollTo(_target + (e.Y < thumb.Y ? -Height : Height) * 0.8);
                    _dragging = false;
                }
                return;
            }
            int part;
            int i = Hit(e.Location, out part);
            if (i < 0) return;
            Item it = _items[i];
            if (it.Header)
            {
                if (part == 2 && SetAll != null) SetAll(it.ProviderId, true);
                if (part == 3 && SetAll != null) SetAll(it.ProviderId, false);
                return;
            }
            if (part == 1) { if (Starred != null) Starred(it); }
            else if (Toggled != null) Toggled(it);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragging = false;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            return k == Keys.Up || k == Keys.Down || k == Keys.PageUp || k == Keys.PageDown || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Down: ScrollTo(_target + RowH); e.Handled = true; return;
                case Keys.Up: ScrollTo(_target - RowH); e.Handled = true; return;
                case Keys.PageDown: ScrollTo(_target + Height * 0.9); e.Handled = true; return;
                case Keys.PageUp: ScrollTo(_target - Height * 0.9); e.Handled = true; return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ScrollTo(_target);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color back = Theme.BackOf(this);
            g.Clear(back);
            Theme.Smooth(g);

            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(frame, 12))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                g.SetClip(path);
            }

            if (_items.Count == 0)
            {
                using (Font f = Theme.Ui(9.5f))
                    TextRenderer.DrawText(g, Empty, f, new Rectangle(30, 0, Width - 60, Height), Theme.TextDim,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                g.ResetClip();
                using (GraphicsPath path = Theme.Round(frame, 12)) using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawPath(p, path);
                return;
            }

            using (Font nameFont = Theme.UiSemi(9.25f))
            using (Font small = Theme.Ui(8f))
            using (Font mono = new Font("Consolas", 8.25f))
            using (Font badge = Theme.UiSemi(7.5f))
            using (Font head = Theme.UiSemi(9f))
            {
                int y = -(int)Math.Round(_scroll.Value);
                for (int i = 0; i < _items.Count; i++)
                {
                    Item it = _items[i];
                    int h = it.Header ? HeadH : RowH;
                    if (y + h < 0) { y += h; continue; }
                    if (y > Height) break;

                    if (it.Header) PaintHeader(g, it, i, y, head, small);
                    else PaintRow(g, it, i, y, nameFont, small, mono, badge);
                    y += h;
                }
            }

            Rectangle thumb = Thumb();
            if (!thumb.IsEmpty)
                using (GraphicsPath path = Theme.Round(thumb, 3))
                using (SolidBrush b = new SolidBrush(Paint2.Alpha(_dragging ? 150 : 90, Theme.TextFaint)))
                    g.FillPath(b, path);

            g.ResetClip();
            using (GraphicsPath path = Theme.Round(frame, 12))
            using (Pen p = new Pen(Focused ? Theme.Accent : Theme.LineSoft, 1f))
                g.DrawPath(p, path);
        }

        void PaintHeader(Graphics g, Item it, int index, int y, Font head, Font small)
        {
            var band = new Rectangle(1, y, Width - 2, HeadH);
            using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Field, it.Brand, Theme.IsLight ? 0.08 : 0.14)))
                g.FillRectangle(b, band);

            var dot = new Rectangle(16, y + (HeadH - 22) / 2, 22, 22);
            Paint2.Gradient(g, dot, 7, it.Brand, Theme.Mix(it.Brand, Color.White, 0.25), 45f);
            TextRenderer.DrawText(g, it.ProviderName.Substring(0, 1), Theme.UiSemi(8.25f), dot, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            string counts = it.Note.Length > 0 ? it.Note : it.CountOn + " of " + it.Count + " on offer";
            TextRenderer.DrawText(g, it.ProviderName, head, new Rectangle(46, y, 240, HeadH), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            int nameW = TextRenderer.MeasureText(it.ProviderName, head).Width;
            TextRenderer.DrawText(g, counts, small, new Rectangle(46 + nameW + 6, y, 260, HeadH), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            if (it.Count > 0)
            {
                int right = Width - 16;
                bool hotAll = _hot == index && _hotPart == 2, hotNone = _hot == index && _hotPart == 3;
                TextRenderer.DrawText(g, "All", head, new Rectangle(right - 90, y, 40, HeadH),
                    hotAll ? it.Brand : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "None", head, new Rectangle(right - 44, y, 44, HeadH),
                    hotNone ? it.Brand : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawLine(p, 1, y + HeadH - 1, Width - 2, y + HeadH - 1);
        }

        void PaintRow(Graphics g, Item it, int index, int y, Font nameFont, Font small, Font mono, Font badge)
        {
            NextScan.Ai.AiModel m = it.Model;
            bool hot = _hot == index;
            if (it.Default || hot)
            {
                Color fill = it.Default ? Paint2.Alpha(Theme.IsLight ? 22 : 34, it.Brand)
                                        : Paint2.Alpha((int)(14 * (0.4 + 0.6 * _hotRow.Eased)), Theme.Text);
                using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, 1, y, Width - 2, RowH);
            }

            // The tick.
            var box = new Rectangle(18, y + (RowH - 20) / 2, 20, 20);
            using (GraphicsPath path = Theme.Round(box, 6))
            {
                if (it.On)
                {
                    Paint2.Gradient(g, box, 6, it.Brand, Theme.Mix(it.Brand, Color.White, 0.2), 45f);
                    NsIcon.Draw(g, NsIcon.Check, new RectangleF(box.X + 2, box.Y + 2, 16, 16), Color.White);
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(Theme.Raised)) g.FillPath(b, path);
                    using (Pen p = new Pen(hot ? it.Brand : Theme.Line, 1.4f)) g.DrawPath(p, path);
                }
            }

            int x = 52;
            int starX = Width - 46;

            // Badges from the right, before the name, so the name knows its room.
            var badges = new List<KeyValuePair<string, Color>>();
            if (m.ContextTokens > 0) badges.Add(new KeyValuePair<string, Color>(Paint2.Tokens(m.ContextTokens) + " context", Theme.Info));
            if (m.OutputTokens > 0) badges.Add(new KeyValuePair<string, Color>(Paint2.Tokens(m.OutputTokens) + " out", Theme.Mix(Theme.Info, it.Brand, 0.5)));
            if (m.Vision == true) badges.Add(new KeyValuePair<string, Color>("Images", Theme.Good));
            if (m.Pdf == true) badges.Add(new KeyValuePair<string, Color>("PDF", Theme.Parse("#E0584F")));
            if (m.Thinking == true) badges.Add(new KeyValuePair<string, Color>("Thinks", Theme.Parse("#9B6BF2")));
            if (!m.Likely) badges.Add(new KeyValuePair<string, Color>("not for chat", Theme.Warn));

            int bx = starX - 8;
            int by = y + 12;
            for (int b = badges.Count - 1; b >= 0; b--)
            {
                string text = badges[b].Key;
                int w = TextRenderer.MeasureText(text, badge).Width + 12;
                if (bx - w < x + 180) break;
                bx -= w;
                var pill = new Rectangle(bx, by, w, 18);
                using (GraphicsPath path = Theme.Round(pill, 9))
                using (SolidBrush fill = new SolidBrush(Paint2.Alpha(Theme.IsLight ? 30 : 50, badges[b].Value)))
                    g.FillPath(fill, path);
                Color ink = Theme.IsLight ? Theme.Mix(badges[b].Value, Color.Black, 0.3) : Theme.Mix(badges[b].Value, Color.White, 0.25);
                TextRenderer.DrawText(g, text, badge, pill, ink,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                bx -= 6;
            }

            // The date, under the badges.
            if (m.Released > new DateTime(2000, 1, 1))
                TextRenderer.DrawText(g, m.Released.ToString("MMM yyyy", CultureInfo.InvariantCulture), small,
                    new Rectangle(starX - 140, y + 34, 132, 16), Theme.TextFaint,
                    TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            int nameRoom = Math.Max(80, bx - x - 10);
            TextRenderer.DrawText(g, m.ToString(), nameFont, new Rectangle(x, y + 11, nameRoom, 20),
                m.Likely ? Theme.Text : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            string second = m.Id;
            if (m.Description.Length > 0) second += "   ·   " + m.Description;
            else if (m.Owner.Length > 0 && m.Owner != "system") second += "   ·   " + m.Owner;
            TextRenderer.DrawText(g, second, m.Description.Length > 0 ? small : mono,
                new Rectangle(x, y + 33, Math.Max(80, starX - 150 - x), 16), Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            // The star: the model the assistant opens on.
            bool starHot = hot && _hotPart == 1;
            if (it.Default || starHot || hot)
            {
                Color gold = Theme.Parse("#F2B01E");
                NsIcon.Draw(g, it.Default ? NsIcon.StarFill : NsIcon.Star,
                    new RectangleF(starX + 6, y + (RowH - 22) / 2f, 22, 22),
                    it.Default ? gold : starHot ? gold : Theme.TextFaint);
            }

            using (Pen p = new Pen(Paint2.Alpha(Theme.IsLight ? 90 : 60, Theme.LineSoft), 1f))
                g.DrawLine(p, x, y + RowH - 1, Width - 16, y + RowH - 1);
        }
    }
}
