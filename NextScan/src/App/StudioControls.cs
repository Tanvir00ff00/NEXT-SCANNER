// =============================================================================
// NextScan Studio - Control library (ground-up rebuild)
// Plan ref: MASTER_PLAN section 13.
//
// Every control is owner-drawn against Theme and animated through the shared
// Animator. Nothing here uses a stock WinForms visual: a native ComboBox paints
// a white system dropdown and a native CheckBox a light system glyph, and
// neither honours BackColor on a dark shell.
//
// Layout convention: controls take their size from the caller. They must render
// correctly at any size, including sizes too small for their content.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>Shared base: double buffered, background-safe, hover animated.</summary>
    public class NsBase : Control
    {
        protected readonly Anim HotAnim = new Anim(0);
        protected bool Pressed;

        public NsBase()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
            Font = Theme.Ui(9f);
            Animator.Attach(this, HotAnim);
        }

        protected void SetHot(bool on)
        {
            HotAnim.Target = on ? 1 : 0;
            Animator.Kick();
        }

        protected override void OnMouseEnter(EventArgs e) { SetHot(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { SetHot(false); Pressed = false; base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (CanFocus) Focus(); Pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { Pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if ((this is NsSegment || this is NsDropdown) &&
                (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Enabled && (this is NsPill || this is NsToggle || this is NsNavButton || this is NsIconButton) &&
                (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter))
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            base.OnKeyDown(e);
        }

        protected void PaintKeyboardFocus(Graphics graphics)
        {
            if (!Focused || !ShowFocusCues || Width < 8 || Height < 8) return;
            using (GraphicsPath path = Theme.Round(new Rectangle(3, 3, Width - 7, Height - 7), 5))
            using (Pen pen = new Pen(Theme.Accent, 1.5f))
            {
                pen.DashStyle = DashStyle.Dot;
                graphics.DrawPath(pen, path);
            }
        }

        /// <summary>Clears with the nearest opaque ancestor colour. Prevents text ghosting.</summary>
        protected void ClearBack(Graphics g)
        {
            g.Clear(Theme.BackOf(this));
        }

        /// <summary>
        /// Fades a colour towards the background when the control is disabled.
        ///
        /// Every control here paints from the palette rather than from system
        /// colours, so nothing dims on its own: a disabled slider looked exactly
        /// like a live one, which is worse than not disabling it at all.
        /// </summary>
        protected Color Live(Color c)
        {
            return Enabled ? c : Theme.Mix(Theme.BackOf(this), c, 0.34);
        }
    }

    /// <summary>
    /// How much of the eye a button is entitled to. Normal is an outlined
    /// button, Primary is the one action the panel is for, Quiet carries no
    /// plate until hovered, Danger is destructive.
    /// </summary>
    public enum PillKind { Normal, Primary, Quiet, Danger }

    public class NsPill : NsBase
    {
        public PillKind Kind = PillKind.Normal;
        public string Glyph = "";
        public int Radius = 8;

        public NsPill()
        {
            AccessibleRole = AccessibleRole.PushButton;
            Size = new Size(120, 36);
            Cursor = Cursors.Hand;
            Font = Theme.UiSemi(9f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 1 || r.Height <= 1) return;

            double hot = Enabled ? HotAnim.Eased : 0;
            Color fill, border, text;

            if (!Enabled)
            {
                fill = Theme.Surface; border = Theme.LineSoft; text = Theme.TextFaint;
            }
            else
            {
                switch (Kind)
                {
                    case PillKind.Primary:
                        fill = Theme.Mix(Theme.Accent, Theme.AccentSoft, hot);
                        border = Color.FromArgb(70, 255, 255, 255);
                        text = Theme.OnAccent;
                        break;
                    case PillKind.Danger:
                        fill = Theme.Mix(Theme.Field, Theme.Danger, hot * 0.85);
                        border = Theme.Mix(Theme.Line, Theme.Danger, hot);
                        text = Theme.Mix(Theme.Danger, Color.White, hot);
                        break;
                    case PillKind.Quiet:
                        fill = Theme.Mix(Color.Transparent, Theme.Field, hot);
                        border = Color.Transparent;
                        text = Theme.Mix(Theme.TextDim, Theme.Text, hot);
                        break;
                    default:
                        fill = Theme.Mix(Theme.Field, Theme.Hover, hot);
                        border = Theme.Mix(Theme.Line, Theme.Accent, hot * 0.6);
                        text = Theme.Text;
                        break;
                }
            }

            if (Pressed) fill = Theme.Mix(fill, Color.Black, 0.14);

            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                if (fill.A > 0) using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (border.A > 0) using (Pen p = new Pen(border, 1f)) g.DrawPath(p, path);
            }

            string label = (Glyph.Length > 0) ? Glyph + "   " + Text : Text;
            TextRenderer.DrawText(g, label, Font, r, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            PaintKeyboardFocus(g);
        }
    }

    // =========================================================================
    // Dropdown
    // =========================================================================
    /// <summary>
    /// Fully owner-drawn dropdown built on a Panel plus a borderless popup, not on
    /// ComboBox. The system draws the closed ComboBox box inside its own WM_PAINT
    /// and repaints the list with system colours, so theming it means fighting it
    /// on every message; owning the popup is simpler and matches at any DPI.
    /// </summary>
    public class NsDropdown : NsBase
    {
        readonly List<string> _items = new List<string>();
        int _index = -1;
        Form _popup;

        public event EventHandler SelectedIndexChanged;

        public NsDropdown()
        {
            Size = new Size(200, 30);
            Cursor = Cursors.Hand;
            Font = Theme.Ui(9f);
        }

        public IList<string> Items { get { return _items; } }

        public void SetItems(IEnumerable<string> items, int selected)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            _index = (_items.Count == 0) ? -1 : Math.Max(0, Math.Min(_items.Count - 1, selected));
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int clamped = (_items.Count == 0) ? -1 : Math.Max(0, Math.Min(_items.Count - 1, value));
                if (clamped == _index) return;
                _index = clamped;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedText
        {
            get { return (_index >= 0 && _index < _items.Count) ? _items[_index] : ""; }
        }

        /// <summary>Selects by text; returns false when the value is not present.</summary>
        public bool SelectText(string text)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (string.Equals(_items[i], text, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedIndex = i;
                    return true;
                }
            }
            return false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            TogglePopup();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                TogglePopup();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                SelectedIndex += e.KeyCode == Keys.Up ? -1 : 1;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        void TogglePopup()
        {
            if (_popup != null) { ClosePopup(); return; }
            if (_items.Count == 0) return;

            int rowHeight = 26;
            int visible = Math.Min(_items.Count, 12);
            int height = visible * rowHeight + 8;

            Point pt = PointToScreen(new Point(0, Height + 2));
            Screen scr = Screen.FromControl(this);
            if (pt.Y + height > scr.WorkingArea.Bottom && pt.Y - height - Height > scr.WorkingArea.Top)
            {
                pt.Y = PointToScreen(new Point(0, -height - 2)).Y;
            }

            _popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Theme.Raised,
                Size = new Size(Width, height),
                Location = pt
            };

            ListPanel list = new ListPanel(this, rowHeight) { Dock = DockStyle.Fill };
            _popup.Controls.Add(list);
            _popup.Deactivate += delegate { ClosePopup(); };
            _popup.Show(FindForm());
            _popup.BringToFront();
            list.Focus();
        }

        internal void ClosePopup()
        {
            if (_popup == null) return;
            Form p = _popup;
            _popup = null;
            try { p.Close(); p.Dispose(); } catch { }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 1 || r.Height <= 1) return;

            double hot = HotAnim.Eased;
            bool open = _popup != null;

            using (GraphicsPath path = Theme.Round(r, 7))
            {
                using (SolidBrush b = new SolidBrush(Live(Theme.Mix(Theme.Field, Theme.Hover, hot))))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Live(open ? Theme.Accent : Theme.Mix(Theme.Line, Theme.Accent, hot * 0.7)), 1f))
                    g.DrawPath(p, path);
            }

            TextRenderer.DrawText(g, SelectedText, Font,
                new Rectangle(10, 0, Width - 32, Height),
                Enabled ? Theme.Text : Theme.Mix(Theme.BackOf(this), Theme.Text, 0.34),
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Point c = new Point(Width - 15, Height / 2);
            using (Pen p = new Pen(Live(Theme.Mix(Theme.TextDim, Theme.Accent, hot)), 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                if (open)
                    g.DrawLines(p, new Point[] { new Point(c.X - 4, c.Y + 2), new Point(c.X, c.Y - 2), new Point(c.X + 4, c.Y + 2) });
                else
                    g.DrawLines(p, new Point[] { new Point(c.X - 4, c.Y - 2), new Point(c.X, c.Y + 2), new Point(c.X + 4, c.Y - 2) });
            }
        }

        /// <summary>The popup body. Kept private to the dropdown that owns it.</summary>
        class ListPanel : Control
        {
            readonly NsDropdown _owner;
            readonly int _rowHeight;
            int _hotRow = -1;

            public ListPanel(NsDropdown owner, int rowHeight)
            {
                _owner = owner;
                _rowHeight = rowHeight;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                BackColor = Theme.Raised;
                Cursor = Cursors.Hand;
            }

            int RowAt(int y)
            {
                int i = (y - 4) / _rowHeight;
                return (i < 0 || i >= _owner._items.Count) ? -1 : i;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int r = RowAt(e.Y);
                if (r != _hotRow) { _hotRow = r; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                if (_hotRow != -1) { _hotRow = -1; Invalidate(); }
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                int r = RowAt(e.Y);
                if (r >= 0) _owner.SelectedIndex = r;
                _owner.ClosePopup();
                base.OnMouseDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Raised);

                Rectangle border = new Rectangle(0, 0, Width - 1, Height - 1);
                using (Pen p = new Pen(Theme.Mix(Theme.Line, Theme.Accent, 0.4), 1f))
                    g.DrawRectangle(p, border);

                for (int i = 0; i < _owner._items.Count; i++)
                {
                    Rectangle row = new Rectangle(4, 4 + i * _rowHeight, Width - 8, _rowHeight);
                    if (row.Bottom > Height) break;

                    bool selected = (i == _owner._index);
                    bool hot = (i == _hotRow);

                    if (selected && hot)
                    {
                        using (GraphicsPath path = Theme.Round(row, 5))
                        using (SolidBrush b = new SolidBrush(Theme.Accent))
                            g.FillPath(b, path);
                    }
                    else if (selected)
                    {
                        using (GraphicsPath path = Theme.Round(row, 5))
                        using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Raised, Theme.Accent, 0.15)))
                            g.FillPath(b, path);
                    }
                    else if (hot)
                    {
                        using (GraphicsPath path = Theme.Round(row, 5))
                        using (SolidBrush b = new SolidBrush(Theme.Hover))
                            g.FillPath(b, path);
                    }

                    Color textColor;
                    if (selected && hot) textColor = Theme.OnAccent;
                    else if (hot) textColor = Theme.Text;
                    else if (selected) textColor = Theme.Accent;
                    else textColor = Theme.Text;

                    TextRenderer.DrawText(g, _owner._items[i], _owner.Font,
                        new Rectangle(row.X + 10, row.Y, row.Width - 32, row.Height),
                        textColor,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                    if (selected)
                    {
                        TextRenderer.DrawText(g, "✓", Theme.UiSemi(9.5f),
                            new Rectangle(row.Right - 22, row.Y, 18, row.Height),
                            (selected && hot) ? Theme.OnAccent : Theme.Accent,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                    }
                }
            }
        }
    }

    // =========================================================================
    // Text field
    // =========================================================================
    /// <summary>
    /// A themed text field. The frame is drawn to match NsDropdown, but the text
    /// itself is a hosted TextBox rather than something owner-drawn: caret
    /// placement, selection, the clipboard, undo and - the reason that settles
    /// it - IME composition for Bangla input are not worth reimplementing, and
    /// reimplementing them badly is worse than a borderless standard control.
    /// </summary>
    public class NsTextBox : NsBase
    {
        readonly TextBox _box;

        public event EventHandler TextCommitted;

        public NsTextBox()
        {
            Size = new Size(200, 30);
            Font = Theme.Ui(9f);

            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Font = Font,
                ForeColor = Theme.Text,
                BackColor = Theme.Field
            };
            _box.GotFocus += delegate { Invalidate(); };

            // Leaving a settings field means the value is settled, so it
            // commits. Leaving a message box does not mean send -- clicking
            // anything at all would post a half-written question.
            _box.LostFocus += delegate { Invalidate(); if (!_multiline) Commit(); };

            _box.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                if (_multiline && e.Shift) return;   // Shift+Enter is the new line
                // Otherwise the form beeps: a single-line TextBox has nothing to
                // do with Enter and passes it on as an unhandled input key.
                e.Handled = true;
                e.SuppressKeyPress = true;
                Commit();
            };
            Controls.Add(_box);
            Layout_();
        }

        void Commit()
        {
            if (TextCommitted != null) TextCommitted(this, EventArgs.Empty);
        }

        public override string Text
        {
            get { return _box == null ? "" : _box.Text; }
            set { if (_box != null) _box.Text = value ?? ""; }
        }

        bool _multiline;

        /// <summary>
        /// Several lines, and Enter sends rather than inserting one.
        ///
        /// That is a chat convention rather than a text-box one, and it lives
        /// here rather than in the caller because otherwise every caller
        /// decides separately what Enter does in a box that is plainly a
        /// message. Shift+Enter is the new line.
        /// </summary>
        public bool Multiline
        {
            get { return _multiline; }
            set
            {
                if (_multiline == value || _box == null) return;
                _multiline = value;
                _box.Multiline = value;
                _box.WordWrap = value;
                _box.AcceptsReturn = value;
                _box.ScrollBars = (value && _showScroll) ? ScrollBars.Vertical : ScrollBars.None;
                Layout_();
            }
        }

        bool _showScroll = true;

        /// <summary>
        /// Whether a multi-line box shows a scrollbar.
        ///
        /// WinForms shows it always or never, not when it is needed, so on a
        /// short composer it is a permanent grey stripe on a box that is
        /// usually empty. Off, the box still scrolls to keep the caret in view,
        /// which is all a three-line field has to do.
        /// </summary>
        public bool ShowScroll
        {
            get { return _showScroll; }
            set
            {
                if (_showScroll == value || _box == null) return;
                _showScroll = value;
                _box.ScrollBars = (_multiline && value) ? ScrollBars.Vertical : ScrollBars.None;
            }
        }

        /// <summary>
        /// Dots instead of characters, for an API key being typed.
        ///
        /// The system password character rather than one of ours, because a
        /// screen reader and a password manager both recognise that and neither
        /// recognises a box we have merely drawn asterisks into.
        /// </summary>
        public bool Secret
        {
            get { return _box != null && _box.UseSystemPasswordChar; }
            set { if (_box != null) _box.UseSystemPasswordChar = value; }
        }

        /// <summary>Puts the caret at the end, for a box that was just filled.</summary>
        public void ToEnd()
        {
            if (_box == null) return;
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }

        public void TakeFocus() { if (_box != null) _box.Focus(); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Layout_();
        }

        void Layout_()
        {
            if (_box == null) return;

            if (_multiline)
            {
                // A multi-line box fills the height it was given. Centring one
                // line inside it would leave the first line floating in the
                // middle and the rest running off the bottom.
                _box.SetBounds(10, 7, Math.Max(10, Width - 20), Math.Max(12, Height - 14));
                return;
            }

            // Centred vertically by height rather than by anchoring: the TextBox
            // sizes itself to its font and ignores a height we set.
            _box.SetBounds(10, Math.Max(2, (Height - _box.PreferredHeight) / 2 + 1),
                           Math.Max(10, Width - 20), _box.PreferredHeight);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_box != null) { _box.Font = Font; Layout_(); }
        }

        /// <summary>Re-reads the palette after a light/dark switch.</summary>
        public void ApplyTheme()
        {
            _box.ForeColor = Theme.Text;
            _box.BackColor = Theme.Field;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 1 || r.Height <= 1) return;

            bool focused = _box.Focused;
            double hot = HotAnim.Eased;

            using (GraphicsPath path = Theme.Round(r, 7))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field))
                    g.FillPath(b, path);
                using (Pen p = new Pen(focused ? Theme.Accent
                                               : Theme.Mix(Theme.Line, Theme.Accent, hot * 0.7), 1f))
                    g.DrawPath(p, path);
            }
        }
    }

    // =========================================================================
    // Toggle switch
    // =========================================================================
    /// <summary>iOS/Fluent style switch. Replaces the checkbox entirely.</summary>
    public class NsToggle : NsBase
    {
        readonly Anim _on = new Anim(0);
        bool _checked;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _on.Target = value ? 1 : 0;
                Animator.Kick();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public NsToggle()
        {
            AccessibleRole = AccessibleRole.CheckButton;
            Size = new Size(260, 26);
            Cursor = Cursors.Hand;
            Font = Theme.Ui(9f);
            Animator.Attach(this, _on);
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            const int tw = 34, th = 19;
            int ty = Math.Max(0, (Height - th) / 2);
            Rectangle track = new Rectangle(Width - tw - 1, ty, tw, th);

            double on = _on.Eased;
            using (GraphicsPath path = Theme.Round(track, th / 2))
            {
                using (SolidBrush b = new SolidBrush(Live(Theme.Mix(Theme.Field, Theme.Accent, on))))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Live(Theme.Mix(Theme.Line, Theme.Accent, on)), 1f))
                    g.DrawPath(p, path);
            }

            int knob = th - 5;
            int kx = track.X + 3 + (int)Math.Round((tw - knob - 6) * on);
            using (SolidBrush b = new SolidBrush(Live(Theme.Mix(Theme.TextDim, Color.White, on))))
                g.FillEllipse(b, kx, ty + 2, knob, knob);

            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(0, 0, Math.Max(0, Width - tw - 12), Height),
                Enabled ? Theme.Text : Theme.TextFaint,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            PaintKeyboardFocus(g);
        }
    }

    // =========================================================================
    // Segmented
    // =========================================================================
    public class NsSegment : NsBase
    {
        readonly Anim _slide = new Anim(0);
        string[] _items = new string[0];
        int _index;
        int _hotIndex = -1;

        public event EventHandler SelectedIndexChanged;

        public NsSegment()
        {
            Size = new Size(240, 30);
            Cursor = Cursors.Hand;
            Font = Theme.UiSemi(8.5f);
            Animator.Attach(this, _slide);
        }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; if (_index >= _items.Length) _index = 0; _slide.Snap(_index); Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                int c = (_items.Length == 0) ? 0 : Math.Max(0, Math.Min(_items.Length - 1, value));
                if (c == _index) return;
                _index = c;
                _slide.Target = c;
                Animator.Kick();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            {
                SelectedIndex += e.KeyCode == Keys.Left ? -1 : 1;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        int IndexAt(int x)
        {
            if (_items.Length == 0 || Width <= 0) return -1;
            return Math.Max(0, Math.Min(_items.Length - 1, x * _items.Length / Width));
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
            if (e.Button != MouseButtons.Left) { base.OnMouseDown(e); return; }
            int i = IndexAt(e.X);
            if (i >= 0) SelectedIndex = i;
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);
            if (_items.Length == 0 || Width < 6 || Height < 6) return;

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.Round(track, 7))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawPath(p, path);
            }

            // The selected chip slides between segments rather than jumping.
            float segW = (float)Width / _items.Length;
            float x = (float)(_slide.Value * segW);
            RectangleF chip = new RectangleF(x + 3, 3, segW - 6, Height - 7);
            if (chip.Width > 0)
            {
                using (GraphicsPath path = Theme.Round(Rectangle.Round(chip), 5))
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillPath(b, path);
            }

            for (int i = 0; i < _items.Length; i++)
            {
                Rectangle seg = new Rectangle((int)(i * segW), 0, (int)segW, Height);
                double nearness = 1.0 - Math.Min(1.0, Math.Abs(_slide.Value - i));
                Color col = Theme.Mix(i == _hotIndex ? Theme.Text : Theme.TextDim,
                                      Theme.OnAccent, nearness);
                TextRenderer.DrawText(g, _items[i], Font, seg, col,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // Slider with inline value
    // =========================================================================
    public class NsSlider : NsBase
    {
        int _min, _max = 100, _value = 50;
        bool _drag;

        public string Label = "";
        public string Suffix = "";
        public int DefaultValue = 0;
        public event EventHandler ValueChanged;

        public int Minimum { get { return _min; } set { _min = value; Invalidate(); } }
        public int Maximum { get { return _max; } set { _max = Math.Max(value, _min + 1); Invalidate(); } }

        public bool IsBipolar { get { return _min < 0 && _max > 0; } }

        public int Value
        {
            get { return _value; }
            set
            {
                int c = Math.Max(_min, Math.Min(_max, value));
                if (c == _value) return;
                _value = c;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }

        public NsSlider()
        {
            Size = new Size(260, 44);
            Cursor = Cursors.Hand;
            Font = Theme.Ui(8.75f);
        }

        const int TrackInset = 8;
        const int TrackHeight = 4;

        Rectangle TrackRect
        {
            get
            {
                return new Rectangle(TrackInset, Height - 14, Math.Max(1, Width - TrackInset * 2), TrackHeight);
            }
        }

        void SetFromX(int x)
        {
            Rectangle t = TrackRect;
            double f = Math.Max(0.0, Math.Min(1.0, (x - t.X) / (double)Math.Max(1, t.Width)));
            Value = _min + (int)Math.Round(f * (_max - _min));
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            Value = DefaultValue;
            base.OnDoubleClick(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _drag = true;
                SetFromX(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _drag = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // Guard against a lost mouse-up: without it the knob keeps tracking
            // the pointer after the button is released.
            if (_drag && (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left) _drag = false;
            if (_drag) SetFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);
            if (Width < 16) return;

            // 1. Label on left
            TextRenderer.DrawText(g, Label, Font, new Rectangle(0, 0, Width - 64, 20),
                Live(Theme.Text), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // 2. Value on right
            string valStr;
            if (IsBipolar && _value > 0) valStr = "+" + _value.ToString(System.Globalization.CultureInfo.InvariantCulture) + Suffix;
            else valStr = _value.ToString(System.Globalization.CultureInfo.InvariantCulture) + Suffix;

            Color valColor = Live((_value != DefaultValue) ? Theme.Accent : Theme.TextDim);
            TextRenderer.DrawText(g, valStr, Theme.UiSemi(8.5f), new Rectangle(Width - 64, 0, 64, 20),
                valColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Rectangle track = TrackRect;

            // 3. Track Background (soft pill)
            using (GraphicsPath trackPath = Theme.Round(track, 2))
            using (SolidBrush b = new SolidBrush(Live(Theme.Field)))
                g.FillPath(b, trackPath);

            double t = (_max == _min) ? 0 : (_value - _min) / (double)(_max - _min);
            int kx = track.X + (int)Math.Round(t * track.Width);

            // 4. Fill Track
            if (IsBipolar)
            {
                int zeroX = track.X + (int)Math.Round((0 - _min) / (double)(_max - _min) * track.Width);

                // Draw subtle center tick mark
                using (Pen tickPen = new Pen(Theme.Line, 1.2f))
                    g.DrawLine(tickPen, zeroX, track.Y - 2, zeroX, track.Bottom + 2);

                if (kx > zeroX)
                {
                    Rectangle fillRect = new Rectangle(zeroX, track.Y, kx - zeroX, track.Height);
                    using (GraphicsPath path = Theme.Round(fillRect, 2))
                    using (SolidBrush b = new SolidBrush(Live(Theme.Accent)))
                        g.FillPath(b, path);
                }
                else if (kx < zeroX)
                {
                    Rectangle fillRect = new Rectangle(kx, track.Y, zeroX - kx, track.Height);
                    using (GraphicsPath path = Theme.Round(fillRect, 2))
                    using (SolidBrush b = new SolidBrush(Live(Theme.Accent)))
                        g.FillPath(b, path);
                }
            }
            else
            {
                if (kx > track.X)
                {
                    Rectangle done = new Rectangle(track.X, track.Y, kx - track.X, track.Height);
                    using (GraphicsPath path = Theme.Round(done, 2))
                    using (SolidBrush b = new SolidBrush(Live(Theme.Accent)))
                        g.FillPath(b, path);
                }
            }

            // 5. Thumb Knob
            bool hot = _drag || HotAnim.Value > 0.2;
            int kr = hot ? 7 : 6;
            int cy = track.Y + track.Height / 2;

            // Subtle drop shadow under knob
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(35, Color.Black)))
                g.FillEllipse(shadow, kx - kr, cy - kr + 1, kr * 2, kr * 2);

            // Halo glow when hot
            if (hot)
            {
                using (SolidBrush halo = new SolidBrush(Color.FromArgb((int)(45 * HotAnim.Value), Theme.Accent)))
                    g.FillEllipse(halo, kx - kr - 3, cy - kr - 3, (kr + 3) * 2, (kr + 3) * 2);
            }

            // White knob body
            using (SolidBrush b = new SolidBrush(Live(Color.White)))
                g.FillEllipse(b, kx - kr, cy - kr, kr * 2, kr * 2);

            // Accent border ring
            using (Pen p = new Pen(Live(Theme.Accent), 2f))
                g.DrawEllipse(p, kx - kr, cy - kr, kr * 2, kr * 2);
        }
    }

    // =========================================================================
    // Section header + card
    // =========================================================================
    public class NsSection : NsBase
    {
        public NsSection()
        {
            Size = new Size(280, 26);
            TabStop = false;
            Font = Theme.Micro();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

        }
    }

    /// <summary>Rounded container. Children go into <see cref="Body"/>.</summary>
    public class NsCard : Panel
    {
        public Panel Body { get; private set; }
        public int Radius = 10;

        public NsCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Raised;

            Body = new Panel { BackColor = Color.Transparent, Location = new Point(12, 10) };
            Controls.Add(Body);
            Resize += delegate { Body.Size = new Size(Math.Max(1, Width - 24), Math.Max(1, Height - 20)); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            g.Clear(Theme.BackOf(this));

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 1 || r.Height <= 1) return;

            using (GraphicsPath path = Theme.Round(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(Theme.Raised)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawPath(p, path);
            }
        }
    }

    // =========================================================================
    // Status chip
    // =========================================================================
    public class NsChip : NsBase
    {
        public Color Dot = Theme.Good;
        public string Sub = "";
        readonly Anim _pulse = new Anim(0);
        bool _busy;

        public bool Busy
        {
            get { return _busy; }
            set
            {
                _busy = value;
                _pulse.Rate = 0.07;
                _pulse.Target = value ? 1 : 0;
                Animator.Kick();
                Invalidate();
            }
        }

        public NsChip()
        {
            Size = new Size(300, 30);
            Font = Theme.UiSemi(9f);
            Animator.Attach(this, _pulse);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 1 || r.Height <= 1) return;

            using (GraphicsPath path = Theme.Round(r, Height / 2))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }

            const int d = 8;
            int cy = (Height - d) / 2;
            double glow = _busy ? (0.35 + 0.65 * _pulse.Eased) : 1.0;

            using (SolidBrush halo = new SolidBrush(Color.FromArgb((int)(70 * glow), Dot)))
                g.FillEllipse(halo, 11, cy - 3, d + 6, d + 6);
            using (SolidBrush b = new SolidBrush(Dot))
                g.FillEllipse(b, 14, cy, d, d);

            int x = 30;
            Size nameSize = TextRenderer.MeasureText(g, Text, Font);
            int nameW = Math.Min(nameSize.Width, Math.Max(0, Width - x - 12));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, nameW, Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            x += nameW + 10;
            if (Sub.Length > 0 && x < Width - 24)
            {
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawLine(p, x - 5, 8, x - 5, Height - 8);
                TextRenderer.DrawText(g, Sub, Theme.Ui(8.25f), new Rectangle(x, 0, Width - x - 10, Height),
                    Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // Chat bubble
    // =========================================================================
    /// <summary>
    /// One turn in the assistant transcript.
    ///
    /// Owner-drawn like everything else here, and for a reason beyond
    /// consistency: a Label with AutoSize and a MaximumSize wraps correctly but
    /// cannot be given a rounded plate, and a RichTextBox per turn is a window
    /// handle per turn in a conversation that may run all day.
    ///
    /// The cost of drawing it is that the text cannot be selected with the
    /// mouse, so every reply carries a copy button instead. For this panel that
    /// is the better trade anyway: what an operator wants from a transcription
    /// is all of it, not a dragged-out part of it.
    /// </summary>
    public class NsBubble : NsBase
    {
        const int PadX = 13;
        const int PadY = 10;

        /// <summary>The operator's own turn. Tinted and inset; the reply is not.</summary>
        public bool Mine;

        /// <summary>Waiting for the first token. Paints moving dots instead of text.</summary>
        public bool Pending;

        /// <summary>Something went wrong in this turn, so it is not a reply to be read.</summary>
        public bool Trouble;

        /// <summary>Fired when the copy mark in the corner is clicked.</summary>
        public event EventHandler CopyWanted;

        bool _overCopy;

        public NsBubble()
        {
            Font = Theme.Ui(9f);
            Cursor = Cursors.Default;

            // A bubble is read, not operated. Without this the base class takes
            // focus on every click, which would pull the caret out of the box
            // the operator is typing their next question into.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        /// <summary>
        /// The height this bubble needs at a given width. The transcript asks
        /// before placing it, because a wrapped paragraph has no other way to
        /// say how tall it is.
        /// </summary>
        public int MeasureHeight(int width)
        {
            if (Pending) return 38;
            int inner = Math.Max(20, width - Indent() - PadX * 2);
            Size size = TextRenderer.MeasureText(Text ?? "", Font,
                new Size(inner, int.MaxValue), Flags);
            return Math.Max(34, size.Height + PadY * 2);
        }

        /// <summary>
        /// How far the operator's own turns are pulled in from the left.
        ///
        /// A reply gets the full column. A question is short and is the thing
        /// being answered, so the inset is what separates the two without a
        /// name label on every single turn.
        /// </summary>
        int Indent() { return Mine ? Math.Min(46, Width / 5) : 0; }

        static TextFormatFlags Flags
        {
            get { return TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding; }
        }

        Rectangle CopyMark()
        {
            return new Rectangle(Width - 26, 6, 18, 18);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool over = !Mine && !Pending && CopyWanted != null && CopyMark().Contains(e.Location);
            if (over != _overCopy) { _overCopy = over; Cursor = over ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_overCopy) { _overCopy = false; Cursor = Cursors.Default; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_overCopy && CopyWanted != null) CopyWanted(this, EventArgs.Empty);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            int indent = Indent();
            Rectangle r = new Rectangle(indent, 0, Math.Max(1, Width - indent - 1), Math.Max(1, Height - 1));
            if (r.Width <= 2 || r.Height <= 2) return;

            Color fill = Mine ? Theme.Mix(Theme.Surface, Theme.Accent, Theme.IsLight ? 0.14 : 0.24)
                             : Theme.Mix(Theme.Surface, Theme.Ground, 0.5);
            Color edge = Trouble ? Theme.Danger
                       : Mine ? Theme.Mix(Theme.Line, Theme.Accent, 0.45) : Theme.LineSoft;

            using (GraphicsPath path = Theme.Round(r, 10))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
            }

            if (Pending) { PaintDots(g, r); return; }

            Rectangle text = new Rectangle(r.X + PadX, r.Y + PadY,
                                           Math.Max(10, r.Width - PadX * 2), Math.Max(10, r.Height - PadY * 2));
            TextRenderer.DrawText(g, Text ?? "", Font, text,
                Trouble ? Theme.Danger : Theme.Text, Flags);

            // The copy mark sits over the reply's own top-right corner rather
            // than in a toolbar: it belongs to this turn, and a transcript that
            // has scrolled has no toolbar next to the turn being read.
            if (_overCopy)
                NsIcon.Draw(g, NsIcon.Copy, CopyMark(), Theme.Mix(Theme.TextFaint, Theme.Accent, 0.8));
        }

        /// <summary>
        /// Three dots rising in turn while the reply is on its way.
        ///
        /// Driven from the clock rather than from the shared Animator, which
        /// eases once between two values and stops. This has to keep going for
        /// as long as the model takes, and the panel's own timer repaints it.
        /// </summary>
        void PaintDots(Graphics g, Rectangle r)
        {
            int cx = r.X + PadX + 4;
            int cy = r.Y + r.Height / 2;
            double phase = (Environment.TickCount % 1200) / 1200.0;

            for (int i = 0; i < 3; i++)
            {
                double at = phase - i * 0.16;
                if (at < 0) at += 1.0;
                double lift = Math.Max(0, Math.Sin(at * Math.PI * 2)) ;
                int alpha = 90 + (int)(120 * lift);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, Theme.TextDim)))
                    g.FillEllipse(b, cx + i * 11, cy - 3 - (float)(lift * 2.5), 6, 6);
            }
        }
    }
}
