// =============================================================================
// NextScan Studio - Custom-drawn control library
// Plan ref: MASTER_PLAN section 13 (UI specification), section 13.5 (visual design).
//
// WHY THIS FILE EXISTS
// The studio window was assembled from stock WinForms controls. Functionally that
// worked, but a native ComboBox paints a white system dropdown and a native
// CheckBox paints a grey system glyph, neither of which honours BackColor on a
// dark theme - so the app read as a 2005 utility no matter what colours the
// surrounding panels used. Screenshotting the running app made that obvious.
//
// Everything here is owner-drawn against one palette (Theme), so the whole shell
// can be restyled from a single place instead of the ~40 scattered
// Color.FromArgb literals the App folder had accumulated.
//
// House rules for this file:
//   - every control is double-buffered; flicker on a dark theme is very visible
//   - nothing here talks to the scanner, the broker, or the imaging core
//   - controls degrade gracefully when given a size smaller than their content
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>
    /// The single source of truth for studio colours and fonts.
    /// Obsidian ground, electric blue primary, cyan section accents.
    /// </summary>
    public static class Theme
    {
        // Surfaces, darkest to lightest.
        public static readonly Color Obsidian = Color.FromArgb(14, 17, 24);   // canvas ground
        public static readonly Color Shell = Color.FromArgb(20, 24, 32);   // window chrome
        public static readonly Color Panel = Color.FromArgb(26, 31, 41);   // inspector ground
        public static readonly Color Card = Color.FromArgb(32, 38, 50);   // raised card
        public static readonly Color Field = Color.FromArgb(38, 45, 59);   // input background
        public static readonly Color FieldHover = Color.FromArgb(46, 55, 71);
        public static readonly Color Line = Color.FromArgb(52, 62, 80);   // hairline border

        // Text.
        public static readonly Color TextPrimary = Color.FromArgb(238, 243, 252);
        public static readonly Color TextMuted = Color.FromArgb(160, 172, 194);
        public static readonly Color TextFaint = Color.FromArgb(110, 122, 145);

        // Accents.
        public static readonly Color Accent = Color.FromArgb(0, 138, 255);   // electric blue, primary action
        public static readonly Color AccentHot = Color.FromArgb(48, 168, 255);
        public static readonly Color Cyan = Color.FromArgb(0, 200, 255);   // section headers, overlays
        public static readonly Color Good = Color.FromArgb(64, 208, 128);   // connected, success
        public static readonly Color Warn = Color.FromArgb(240, 176, 64);
        public static readonly Color Danger = Color.FromArgb(226, 74, 88);   // cancel, destructive

        public static Font Ui(float size)
        {
            return new Font("Segoe UI", size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font UiBold(float size)
        {
            return new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Point);
        }

        /// <summary>Rounded rectangle path. Radius is clamped so it can never invert.</summary>
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

            int d = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Turns on the quality settings every control here wants.</summary>
        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
    }

    /// <summary>Base for every owner-drawn control here: double buffered, transparent-friendly.</summary>
    public class NsControl : Control
    {
        protected bool Hot;
        protected bool Pressed;

        public NsControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = Theme.Ui(9f);
            ForeColor = Theme.TextPrimary;
        }

        protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hot = false; Pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }
    }

    // =========================================================================
    // Button
    // =========================================================================
    public enum NsButtonKind
    {
        /// <summary>Filled electric blue. One per screen - the thing you came to do.</summary>
        Primary,
        /// <summary>Translucent fill with a hairline border. The default.</summary>
        Ghost,
        /// <summary>Crimson. Destructive or abort.</summary>
        Danger,
        /// <summary>No fill until hovered. For dense toolbars.</summary>
        Quiet
    }

    public class NsButton : NsControl
    {
        public NsButtonKind Kind = NsButtonKind.Ghost;
        public int Radius = 6;

        /// <summary>Draws a small status dot on the left. Used by the Scan button.</summary>
        public bool ShowStatusDot;
        public Color StatusDotColor = Theme.Good;

        public NsButton()
        {
            Size = new Size(110, 32);
            Cursor = Cursors.Hand;
            Font = Theme.UiBold(9f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            Color fill, border, text;
            ResolveColours(out fill, out border, out text);

            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                if (fill.A > 0)
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                }
                if (border.A > 0)
                {
                    using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
                }
            }

            Rectangle textRect = r;
            if (ShowStatusDot)
            {
                int d = 8;
                int cy = r.Y + (r.Height - d) / 2;
                using (SolidBrush b = new SolidBrush(Enabled ? StatusDotColor : Theme.TextFaint))
                    g.FillEllipse(b, r.X + 12, cy, d, d);
                textRect = new Rectangle(r.X + 24, r.Y, r.Width - 24, r.Height);
            }

            TextRenderer.DrawText(g, Text, Font, textRect, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void ResolveColours(out Color fill, out Color border, out Color text)
        {
            if (!Enabled)
            {
                fill = Color.FromArgb(28, 34, 44);
                border = Color.FromArgb(40, 48, 62);
                text = Theme.TextFaint;
                return;
            }

            switch (Kind)
            {
                case NsButtonKind.Primary:
                    fill = Pressed ? Theme.Accent : (Hot ? Theme.AccentHot : Theme.Accent);
                    border = Color.FromArgb(120, 255, 255, 255);
                    text = Color.White;
                    break;

                case NsButtonKind.Danger:
                    fill = Hot ? Color.FromArgb(60, Theme.Danger) : Color.FromArgb(34, Theme.Danger);
                    border = Color.FromArgb(Hot ? 220 : 150, Theme.Danger);
                    text = Hot ? Color.White : Theme.Danger;
                    break;

                case NsButtonKind.Quiet:
                    fill = Hot ? Theme.FieldHover : Color.Transparent;
                    border = Color.Transparent;
                    text = Hot ? Theme.TextPrimary : Theme.TextMuted;
                    break;

                default: // Ghost - translucent white over whatever is behind it
                    fill = Color.FromArgb(Pressed ? 30 : (Hot ? 22 : 12), 255, 255, 255);
                    border = Color.FromArgb(Hot ? 90 : 55, 255, 255, 255);
                    text = Theme.TextPrimary;
                    break;
            }
        }
    }

    // =========================================================================
    // ComboBox
    // =========================================================================
    /// <summary>
    /// Dark dropdown.
    ///
    /// Two halves have to be handled separately, which is why this is not just a
    /// BackColor assignment:
    ///   - the OPEN list is themed by owner-drawing items (DrawMode.OwnerDrawFixed)
    ///   - the CLOSED box is drawn by the system regardless of BackColor, so we let
    ///     the system paint and then paint over it after WM_PAINT
    /// DropDownList style is mandatory here; DropDown keeps a native white edit
    /// field that cannot be themed at all.
    /// </summary>
    public class NsComboBox : ComboBox
    {
        const int WM_PAINT = 0x000F;
        bool _hot;

        public NsComboBox()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Field;
            ForeColor = Theme.TextPrimary;
            Font = Theme.Ui(9f);
            ItemHeight = 20;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            Graphics g = e.Graphics;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            using (SolidBrush b = new SolidBrush(selected ? Theme.Accent : Theme.Field))
                g.FillRectangle(b, e.Bounds);

            string text = Items[e.Index] != null ? Items[e.Index].ToString() : "";
            Rectangle textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(g, text, Font, textRect,
                selected ? Color.White : Theme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // Paint our chrome on top of whatever the system just drew. Doing this
            // in OnPaint alone is not enough: the system repaints the closed box
            // from inside its own WM_PAINT handling.
            if (m.Msg != WM_PAINT) return;

            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                Theme.Smooth(g);
                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                if (r.Width <= 0 || r.Height <= 0) return;

                using (GraphicsPath path = Theme.Round(r, 5))
                {
                    using (SolidBrush b = new SolidBrush(_hot ? Theme.FieldHover : Theme.Field))
                        g.FillPath(b, path);
                    using (Pen p = new Pen(_hot ? Theme.Accent : Theme.Line, 1f))
                        g.DrawPath(p, path);
                }

                string text = (SelectedIndex >= 0 && SelectedItem != null) ? SelectedItem.ToString() : Text;
                Rectangle textRect = new Rectangle(r.X + 8, r.Y, r.Width - 30, r.Height);
                TextRenderer.DrawText(g, text, Font, textRect,
                    Enabled ? Theme.TextPrimary : Theme.TextFaint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

                DrawChevron(g, new Point(r.Right - 15, r.Y + r.Height / 2), _hot ? Theme.Cyan : Theme.TextMuted);
            }
        }

        internal static void DrawChevron(Graphics g, Point centre, Color colour)
        {
            using (Pen p = new Pen(colour, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(centre.X - 4, centre.Y - 2),
                    new Point(centre.X,     centre.Y + 2),
                    new Point(centre.X + 4, centre.Y - 2)
                });
            }
        }
    }

    // =========================================================================
    // CheckBox
    // =========================================================================
    /// <summary>
    /// Owner-drawn checkbox.
    ///
    /// Deliberately derives from CheckBox rather than from NsControl: every call
    /// site already uses .Checked and .CheckedChanged, and inheriting keeps those
    /// working so swapping the factory is a one-line change. Only the painting is
    /// replaced - the native glyph ignores BackColor and stays light on a dark
    /// panel, which is the whole reason this exists.
    /// </summary>
    public class NsCheckBox : CheckBox
    {
        bool _hot;

        public NsCheckBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AutoSize = false;
            Size = new Size(200, 22);
            Cursor = Cursors.Hand;
            Font = Theme.Ui(8.75f);
            ForeColor = Theme.TextMuted;
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            const int box = 15;
            int top = Math.Max(0, (Height - box) / 2);
            Rectangle r = new Rectangle(0, top, box, box);

            using (GraphicsPath path = Theme.Round(r, 4))
            {
                using (SolidBrush b = new SolidBrush(Checked ? Theme.Accent : (_hot ? Theme.FieldHover : Theme.Field)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Checked ? Theme.AccentHot : (_hot ? Theme.Accent : Theme.Line), 1f))
                    g.DrawPath(p, path);
            }

            if (Checked)
            {
                using (Pen p = new Pen(Color.White, 2f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    g.DrawLines(p, new Point[]
                    {
                        new Point(r.X + 3, r.Y + 7),
                        new Point(r.X + 6, r.Y + 10),
                        new Point(r.X + 12, r.Y + 4)
                    });
                }
            }

            Rectangle textRect = new Rectangle(box + 8, 0, Math.Max(0, Width - box - 8), Height);
            TextRenderer.DrawText(g, Text, Font, textRect,
                Enabled ? (_hot ? Theme.TextPrimary : ForeColor) : Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================
    // Segmented control
    // =========================================================================
    /// <summary>
    /// A row of mutually exclusive pills. Replaces radio-button clusters, which on
    /// a dark theme paint a native light glyph that cannot be restyled.
    /// </summary>
    public class NsSegmented : NsControl
    {
        string[] _items = new string[0];
        int _selected;
        int _hotIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; if (_selected >= _items.Length) _selected = 0; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int clamped = (_items.Length == 0) ? 0 : Math.Max(0, Math.Min(_items.Length - 1, value));
                if (clamped == _selected) return;
                _selected = clamped;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public NsSegmented()
        {
            Size = new Size(240, 28);
            Cursor = Cursors.Hand;
            Font = Theme.UiBold(8.5f);
        }

        int IndexAt(int x)
        {
            if (_items.Length == 0 || Width <= 0) return -1;
            int i = x * _items.Length / Width;
            return Math.Max(0, Math.Min(_items.Length - 1, i));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i != _hotIndex) { _hotIndex = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.X);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            if (_items.Length == 0 || Width < 4 || Height < 4) return;

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(track, 6))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }

            float segW = (float)Width / _items.Length;
            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle seg = new Rectangle(
                    (int)Math.Round(i * segW) + 2, 2,
                    (int)Math.Round(segW) - 4, Height - 5);
                if (seg.Width <= 0) continue;

                if (i == _selected)
                {
                    using (GraphicsPath path = Theme.Round(seg, 5))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, path);
                }
                else if (i == _hotIndex)
                {
                    using (GraphicsPath path = Theme.Round(seg, 5))
                    using (SolidBrush b = new SolidBrush(Theme.FieldHover))
                        g.FillPath(b, path);
                }

                TextRenderer.DrawText(g, _items[i], Font, seg,
                    i == _selected ? Color.White : Theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // Slider
    // =========================================================================
    public class NsSlider : NsControl
    {
        int _min, _max = 100, _value = 50;
        bool _dragging;

        public event EventHandler ValueChanged;

        public int Minimum { get { return _min; } set { _min = value; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = Math.Max(value, _min + 1); Invalidate(); } }

        public int Value
        {
            get { return _value; }
            set
            {
                int clamped = Math.Max(_min, Math.Min(_max, value));
                if (clamped == _value) return;
                _value = clamped;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public NsSlider()
        {
            Size = new Size(240, 24);
            Cursor = Cursors.Hand;
        }

        void SetFromX(int x)
        {
            int usable = Math.Max(1, Width - 12);
            double t = Math.Max(0.0, Math.Min(1.0, (x - 6) / (double)usable));
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        protected override void OnMouseDown(MouseEventArgs e) { _dragging = true; SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) SetFromX(e.X); base.OnMouseMove(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            if (Width < 14 || Height < 6) return;

            int cy = Height / 2;
            Rectangle track = new Rectangle(6, cy - 2, Width - 12, 4);
            using (GraphicsPath path = Theme.Round(track, 2))
            using (SolidBrush b = new SolidBrush(Theme.Field))
                g.FillPath(b, path);

            double t = (_max == _min) ? 0 : (_value - _min) / (double)(_max - _min);
            int knobX = 6 + (int)Math.Round(t * (Width - 12));

            Rectangle filled = new Rectangle(6, cy - 2, Math.Max(0, knobX - 6), 4);
            if (filled.Width > 0)
            {
                using (GraphicsPath path = Theme.Round(filled, 2))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, path);
            }

            int kr = Hot || _dragging ? 7 : 6;
            using (SolidBrush b = new SolidBrush(Color.White))
                g.FillEllipse(b, knobX - kr, cy - kr, kr * 2, kr * 2);
            using (Pen p = new Pen(Theme.Accent, 2f))
                g.DrawEllipse(p, knobX - kr, cy - kr, kr * 2, kr * 2);
        }
    }

    // =========================================================================
    // Card
    // =========================================================================
    /// <summary>
    /// A titled section container for the inspector. Children are added to
    /// <see cref="Body"/>, which is inset below the header.
    /// </summary>
    public class NsCard : Panel
    {
        public string Title = "";
        public Panel Body { get; private set; }

        const int HeaderHeight = 30;

        public NsCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            BackColor = Theme.Panel;

            Body = new Panel
            {
                BackColor = Color.Transparent,
                Location = new Point(10, HeaderHeight),
                Size = new Size(Math.Max(1, Width - 20), Math.Max(1, Height - HeaderHeight - 8))
            };
            Controls.Add(Body);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Body != null)
                Body.Size = new Size(Math.Max(1, Width - 20), Math.Max(1, Height - HeaderHeight - 8));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            using (GraphicsPath path = Theme.Round(r, 8))
            {
                using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }

            // Accent tick to the left of the title, so sections are scannable
            // without relying on colour alone for the text.
            using (SolidBrush b = new SolidBrush(Theme.Cyan))
                g.FillRectangle(b, 10, 11, 3, 11);

            TextRenderer.DrawText(g, Title.ToUpperInvariant(), Theme.UiBold(8.25f),
                new Rectangle(19, 8, Width - 26, 18), Theme.Cyan,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================
    // Device badge
    // =========================================================================
    /// <summary>
    /// Live connection badge for the command bar, e.g.
    /// "[* CanoScan LiDE 400 | TWAIN 32-bit]". The dot colour is the only
    /// at-a-glance signal that the app still has a device, so it is deliberately
    /// the most saturated thing in the bar.
    /// </summary>
    public class NsDeviceBadge : NsControl
    {
        public string DeviceName = "No scanner";
        public string TransportText = "";
        public bool Connected;
        public bool Busy;

        public NsDeviceBadge()
        {
            Size = new Size(280, 28);
            Font = Theme.Ui(8.75f);
        }

        public void SetDevice(string name, string transport, bool connected)
        {
            DeviceName = string.IsNullOrEmpty(name) ? "No scanner" : name;
            TransportText = transport ?? "";
            Connected = connected;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            using (GraphicsPath path = Theme.Round(r, 14))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(45, 255, 255, 255), 1f)) g.DrawPath(p, path);
            }

            Color dot = Busy ? Theme.Warn : (Connected ? Theme.Good : Theme.Danger);
            const int d = 8;
            int cy = (Height - d) / 2;

            // Soft halo so the state reads at a glance on a dark bar.
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(70, dot)))
                g.FillEllipse(halo, 9, cy - 3, d + 6, d + 6);
            using (SolidBrush b = new SolidBrush(dot))
                g.FillEllipse(b, 12, cy, d, d);

            int x = 26;
            Size nameSize = TextRenderer.MeasureText(g, DeviceName, Theme.UiBold(8.75f));
            TextRenderer.DrawText(g, DeviceName, Theme.UiBold(8.75f),
                new Rectangle(x, 0, Math.Min(nameSize.Width, Width - x - 8), Height),
                Theme.TextPrimary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            x += Math.Min(nameSize.Width, Width - x - 8) + 8;
            if (TransportText.Length > 0 && x < Width - 20)
            {
                using (Pen p = new Pen(Color.FromArgb(60, 255, 255, 255), 1f))
                    g.DrawLine(p, x - 4, 7, x - 4, Height - 7);

                TextRenderer.DrawText(g, TransportText, Theme.Ui(8.25f),
                    new Rectangle(x, 0, Width - x - 8, Height),
                    Theme.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // Floating glass HUD
    // =========================================================================
    /// <summary>
    /// Translucent toolbar that floats over the canvas (plan section 13.3).
    /// It paints its own translucent ground, so it must be added ON TOP of the
    /// canvas in z-order and given a transparent BackColor.
    /// </summary>
    public class NsGlassHud : NsControl
    {
        public class HudItem
        {
            public string Text = "";
            public string Tooltip = "";
            public bool Toggle;
            public bool On;
            public object Tag;
        }

        readonly List<HudItem> _items = new List<HudItem>();
        readonly List<Rectangle> _bounds = new List<Rectangle>();
        int _hotIndex = -1;

        /// <summary>Raised with the clicked item. Toggles have already flipped.</summary>
        public event EventHandler<HudItemEventArgs> ItemClicked;

        public class HudItemEventArgs : EventArgs
        {
            public HudItem Item;
            public HudItemEventArgs(HudItem item) { Item = item; }
        }

        public NsGlassHud()
        {
            Height = 34;
            Font = Theme.UiBold(8.5f);
            Cursor = Cursors.Hand;
        }

        public HudItem Add(string text, string tooltip, bool toggle, object tag)
        {
            HudItem item = new HudItem { Text = text, Tooltip = tooltip, Toggle = toggle, Tag = tag };
            _items.Add(item);
            Relayout();
            return item;
        }

        public void Relayout()
        {
            _bounds.Clear();
            int x = 6;
            using (Graphics g = CreateGraphics())
            {
                foreach (HudItem it in _items)
                {
                    int w = TextRenderer.MeasureText(g, it.Text, Font).Width + 18;
                    _bounds.Add(new Rectangle(x, 5, w, Height - 10));
                    x += w + 4;
                }
            }
            Width = x + 2;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int found = -1;
            for (int i = 0; i < _bounds.Count; i++)
                if (_bounds[i].Contains(e.Location)) { found = i; break; }

            if (found != _hotIndex) { _hotIndex = found; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotIndex = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            for (int i = 0; i < _bounds.Count && i < _items.Count; i++)
            {
                if (!_bounds[i].Contains(e.Location)) continue;
                HudItem it = _items[i];
                if (it.Toggle) it.On = !it.On;
                Invalidate();
                if (ItemClicked != null) ItemClicked(this, new HudItemEventArgs(it));
                break;
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 0 || r.Height <= 0) return;

            // Acrylic-ish: a dark translucent slab with a light top edge. Real
            // blur is not available to GDI+, so the illusion comes from the
            // highlight and the low-alpha fill.
            using (GraphicsPath path = Theme.Round(r, 8))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(196, 22, 27, 36))) g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(38, 255, 255, 255), 1f)) g.DrawPath(p, path);
            }
            using (Pen p = new Pen(Color.FromArgb(26, 255, 255, 255), 1f))
                g.DrawLine(p, r.X + 8, r.Y + 1, r.Right - 8, r.Y + 1);

            for (int i = 0; i < _items.Count && i < _bounds.Count; i++)
            {
                HudItem it = _items[i];
                Rectangle b = _bounds[i];

                if (it.Toggle && it.On)
                {
                    using (GraphicsPath path = Theme.Round(b, 5))
                    using (SolidBrush br = new SolidBrush(Theme.Accent))
                        g.FillPath(br, path);
                }
                else if (i == _hotIndex)
                {
                    using (GraphicsPath path = Theme.Round(b, 5))
                    using (SolidBrush br = new SolidBrush(Color.FromArgb(30, 255, 255, 255)))
                        g.FillPath(br, path);
                }

                Color text = (it.Toggle && it.On) ? Color.White : (i == _hotIndex ? Theme.TextPrimary : Theme.TextMuted);
                TextRenderer.DrawText(g, it.Text, Font, b, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }
}
