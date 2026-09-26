// =============================================================================
// NextScan Studio - the two controls a chat panel needs
// Plan ref: docs/AI_LAYER.md
//
// Both of these are here rather than invented in the panel because both were
// got wrong first. The panel had a toolbar of icons across the top, a text box
// with a separate button beside it, and the model and thinking level buried in
// Settings. Looking at what Claude, ChatGPT, Gemini and Copilot actually do,
// they all agree and none of them do that:
//
//   * the input and its controls are ONE rounded container, not a box with
//     things next to it;
//   * the model and the effort sit in that container's bottom row, beside the
//     send button -- never on a settings page, because they are chosen per
//     question, not per installation;
//   * the empty state offers named suggestions, not a row of icons.
//
// NsComposer is the container. NsChoiceMenu is what the model button opens.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextScan.App
{
    // =========================================================================
    // Composer
    // =========================================================================
    /// <summary>
    /// The input box: one rounded plate holding the text area and, along its
    /// bottom edge, whatever controls the caller puts there.
    ///
    /// The caller adds those controls as children of this one and places them
    /// inside <see cref="FootTop"/>. That is deliberately not a nested Panel:
    /// a transparent panel over an owner-drawn parent is a well-known source of
    /// ghosting in WinForms, and there is nothing a child panel would buy.
    /// </summary>
    public class NsComposer : NsBase
    {
        readonly TextBox _box;
        bool _ghost;

        string _placeholder = "";

        /// <summary>
        /// Grey text shown while the box is empty and unfocused.
        ///
        /// A property rather than a field, and that is the whole of it: as a
        /// field it is assigned by the object initialiser, which runs after the
        /// constructor, so the constructor's attempt to show it saw an empty
        /// string and the box came up blank.
        /// </summary>
        public string Placeholder
        {
            get { return _placeholder; }
            set { _placeholder = value ?? ""; ShowGhost(); }
        }

        /// <summary>Height of the control strip along the bottom edge.</summary>
        public int FootHeight = 34;

        string _footNote = "";

        /// <summary>
        /// A quiet line at the left of the strip, for what the question is
        /// about.
        ///
        /// Painted rather than being a disabled button, which is what it was
        /// first and which went wrong in a way worth recording: as a child
        /// control it kept bounds from before the plate had been given its real
        /// height, and sat outside the plate it belonged to while the two real
        /// buttons beside it were placed correctly. It is not interactive, so it
        /// has no business being a control at all.
        /// </summary>
        public string FootNote
        {
            get { return _footNote; }
            set { _footNote = value ?? ""; Invalidate(); }
        }

        /// <summary>Enter, without Shift.</summary>
        public event EventHandler Submit;

        /// <summary>The text changed, so the caller can light its send button.</summary>
        public event EventHandler Typed;

        /// <summary>The box wants to be this tall now. Raised as the text grows.</summary>
        public event EventHandler HeightWanted;

        public NsComposer()
        {
            Size = new Size(260, 96);
            Font = Theme.Ui(9f);

            // Opaque, and the field colour, for the sake of the buttons that sit
            // inside. Theme.BackOf walks up to the nearest opaque ancestor to
            // decide what a transparent control should clear with, and with this
            // left transparent it walked past the plate to the panel behind: the
            // model button and the send button each drew themselves a white
            // rectangle on top of the plate they are supposed to be part of.
            //
            // This control's own clear is unaffected, because BackOf starts at
            // the PARENT -- so the corners outside the rounded plate still take
            // the panel's colour rather than being squared off in field grey.
            BackColor = Theme.Field;

            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Multiline = true,
                WordWrap = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.None,
                Font = Font,
                ForeColor = Theme.Text,
                BackColor = Theme.Field
            };
            _box.GotFocus += delegate { DropGhost(); Invalidate(); };
            _box.LostFocus += delegate { ShowGhost(); Invalidate(); };
            _box.TextChanged += delegate
            {
                if (_ghost) return;
                if (Typed != null) Typed(this, EventArgs.Empty);
                if (HeightWanted != null) HeightWanted(this, EventArgs.Empty);
            };
            _box.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter || e.Shift) return;
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (Submit != null) Submit(this, EventArgs.Empty);
            };
            Controls.Add(_box);

            ShowGhost();
            Layout_();
        }

        // ---- the placeholder ------------------------------------------------
        //
        // Written into the box in a faint colour and taken out again on focus.
        // The tidy way would be the system cue banner, and it does not work:
        // Windows does not draw one on a multi-line edit control. So the text is
        // real, and Text lies about it rather than letting a caller send it.

        void ShowGhost()
        {
            if (_box == null || _ghost || _box.Text.Length > 0 || _placeholder.Length == 0) return;
            _ghost = true;
            _box.ForeColor = Theme.TextFaint;
            _box.Text = _placeholder;
        }

        void DropGhost()
        {
            if (!_ghost) return;
            _ghost = false;
            _box.Text = "";
            _box.ForeColor = Theme.Text;
        }

        public override string Text
        {
            get { return _ghost ? "" : _box.Text; }
            set
            {
                _ghost = false;
                _box.ForeColor = Theme.Text;
                _box.Text = value ?? "";
                if (!_box.Focused) ShowGhost();
            }
        }

        public void TakeFocus() { _box.Focus(); }

        /// <summary>
        /// The height this wants, for the text it holds. Grows to five lines and
        /// then stops and scrolls, the way every chat box does: past that the
        /// transcript above is being eaten by the thing being typed into.
        /// </summary>
        public int PreferredHeight
        {
            get
            {
                int line = _box.Font.Height;
                int lines = _ghost ? 1 : Math.Max(1, Math.Min(5, CountLines()));
                return lines * line + FootHeight + 20;
            }
        }

        int CountLines()
        {
            // GetLineFromCharIndex on the last character counts wrapped lines
            // as well as typed ones, which is what the height has to allow for.
            if (_box.TextLength == 0) return 1;
            return _box.GetLineFromCharIndex(_box.TextLength) + 1;
        }

        /// <summary>The top of the control strip, in this control's coordinates.</summary>
        public int FootTop { get { return Math.Max(0, FootBottom - FootHeight); } }

        /// <summary>
        /// The bottom of the control strip. Everything in it is placed from
        /// here rather than from the top: the strip is against the bottom edge,
        /// and measuring from the other end means a stale height leaves the
        /// buttons hanging off the plate instead of merely misaligned.
        /// </summary>
        public int FootBottom { get { return Math.Max(0, Height - 7); } }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Layout_();
            // The caller owns the strip's contents, so it has to be told. The
            // alternative is the caller remembering to re-place them after every
            // resize, and it did not.
            if (FootChanged != null) FootChanged(this, EventArgs.Empty);
        }

        /// <summary>The strip moved. Re-place whatever was put in it.</summary>
        public event EventHandler FootChanged;

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_box != null) { _box.Font = Font; Layout_(); }
        }

        void Layout_()
        {
            if (_box == null) return;
            int h = Math.Max(_box.Font.Height, FootTop - 16);
            _box.SetBounds(13, 11, Math.Max(20, Width - 26), h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            // Clicking the plate anywhere that is not a button is clicking the
            // box. Every one of these behaves that way and it is invisible
            // until it is missing.
            if (e.Y < FootTop) _box.Focus();
            base.OnMouseDown(e);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.Field;
            _box.BackColor = Theme.Field;
            _box.ForeColor = _ghost ? Theme.TextFaint : Theme.Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 2 || r.Height <= 2) return;

            using (GraphicsPath path = Theme.Round(r, 12))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(_box.Focused ? Theme.Accent : Theme.Line, 1f)) g.DrawPath(p, path);
            }

            if (_footNote.Length == 0) return;

            // Up to whatever the caller put in the strip, and no further.
            int until = Width - 10;
            foreach (Control child in Controls)
            {
                if (child == _box || !child.Visible) continue;
                if (child.Left < until) until = child.Left;
            }

            int room = until - 22;
            if (room < 74) return;

            using (Font f = Theme.Ui(7.75f))
                TextRenderer.DrawText(g, _footNote, f,
                    new Rectangle(13, FootTop, room, FootHeight), Theme.TextFaint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================
    // Popup menu
    // =========================================================================
    /// <summary>
    /// What the model button opens: headed sections of rows, one of them
    /// ticked.
    ///
    /// Not a ContextMenuStrip. Every other control in this application is drawn
    /// from the palette, and a system menu in the middle of them is the one
    /// piece of the window that belongs to a different program. It follows the
    /// same borderless-Form pattern the dropdown already uses.
    ///
    /// Separate from NsMenu, which is the flat list of strings the title bar
    /// uses. This one has headed sections, ticks and unselectable rows, and
    /// bending the other into that shape would have made both harder to read
    /// than having two.
    /// </summary>
    public class NsChoiceMenu
    {
        class Row
        {
            public string Text = "";
            public string Note = "";
            public bool Header;
            public bool Rule;
            public bool Ticked;
            public bool Dim;
            public object Tag;
            public int Height { get { return Rule ? 9 : (Header ? 24 : 28); } }
        }

        readonly List<Row> _rows = new List<Row>();
        Form _popup;

        /// <summary>The row's tag. Fired after the popup has closed.</summary>
        public event Action<object> Picked;

        public void Header(string text, string note)
        {
            _rows.Add(new Row { Text = text, Note = note ?? "", Header = true });
        }

        public void Item(string text, string note, bool ticked, object tag)
        {
            _rows.Add(new Row { Text = text, Note = note ?? "", Ticked = ticked, Tag = tag });
        }

        /// <summary>A row that says something and cannot be chosen.</summary>
        public void Note(string text)
        {
            _rows.Add(new Row { Text = text, Dim = true });
        }

        public void Rule() { _rows.Add(new Row { Rule = true }); }

        public int Count { get { return _rows.Count; } }

        public void Show(Control anchor, int width)
        {
            if (_rows.Count == 0 || anchor == null) return;

            int wanted = 10;
            foreach (Row row in _rows) wanted += row.Height;

            Screen screen = Screen.FromControl(anchor);
            int height = Math.Min(wanted, Math.Max(120, screen.WorkingArea.Height - 120));

            // On whichever side of the button has the room. The composer's
            // buttons sit at the bottom of the panel and open upwards; the
            // history button sits at the top and opens downwards. Always above,
            // the history menu was pushed against the top of the screen and
            // covered the panel's own title.
            Point top = anchor.PointToScreen(Point.Empty);
            int below = screen.WorkingArea.Bottom - (top.Y + anchor.Height);
            int above = top.Y - screen.WorkingArea.Top;
            Point at = below >= height + 6 || below > above
                ? anchor.PointToScreen(new Point(0, anchor.Height + 6))
                : anchor.PointToScreen(new Point(0, -height - 6));
            if (at.Y < screen.WorkingArea.Top) at.Y = screen.WorkingArea.Top;
            if (at.Y + height > screen.WorkingArea.Bottom) at.Y = screen.WorkingArea.Bottom - height;

            int right = at.X + width;
            if (right > screen.WorkingArea.Right - 8) at.X -= right - (screen.WorkingArea.Right - 8);
            if (at.X < screen.WorkingArea.Left + 8) at.X = screen.WorkingArea.Left + 8;

            _popup = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                BackColor = Theme.Raised,
                Size = new Size(width, height),
                Location = at
            };

            Sheet sheet = new Sheet(this, wanted > height) { Dock = DockStyle.Fill };
            _popup.Controls.Add(sheet);

            // Only dismiss once it has actually been active. Show() does not
            // always activate an owned form, and a Deactivate arriving before
            // the first Activated closes the menu the instant it opens. The
            // title bar's menu learned this already.
            bool wasActive = false;
            _popup.Activated += delegate { wasActive = true; };
            _popup.Deactivate += delegate { if (wasActive) Close(); };

            _popup.Show(anchor.FindForm());
            _popup.BringToFront();
            _popup.Activate();
            sheet.Focus();
        }

        void Close()
        {
            if (_popup == null) return;
            Form going = _popup;
            _popup = null;
            try { going.Close(); going.Dispose(); } catch { }
        }

        void Chose(Row row)
        {
            object tag = row.Tag;
            Close();
            if (Picked != null) Picked(tag);
        }

        /// <summary>The painted surface inside the popup.</summary>
        class Sheet : Panel
        {
            readonly NsChoiceMenu _menu;
            readonly bool _scrolls;
            int _hot = -1;
            int _scroll;

            public Sheet(NsChoiceMenu menu, bool scrolls)
            {
                _menu = menu;
                _scrolls = scrolls;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Raised;
            }

            int Total()
            {
                int n = 10;
                foreach (Row row in _menu._rows) n += row.Height;
                return n;
            }

            int RowAt(int y)
            {
                int at = 5 - _scroll;
                for (int i = 0; i < _menu._rows.Count; i++)
                {
                    Row row = _menu._rows[i];
                    if (y >= at && y < at + row.Height)
                        return (row.Header || row.Rule || row.Dim) ? -1 : i;
                    at += row.Height;
                }
                return -1;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int i = RowAt(e.Y);
                if (i != _hot) { _hot = i; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                int i = RowAt(e.Y);
                if (i >= 0) _menu.Chose(_menu._rows[i]);
                base.OnMouseDown(e);
            }

            protected override void WndProc(ref Message m)
            {
                // The wheel belongs to the open menu, and stops there: a menu
                // that does not scroll passes the message on, and the page
                // behind it moved instead. A Sheet is a Panel, not an NsBase,
                // so there is no EatWheel to borrow -- the message is simply
                // not handed on, which is the whole effect.
                if (m.Msg == 0x020A)
                {
                    if (!_scrolls) return;
                    int delta = unchecked((short)((long)m.WParam >> 16));
                    int most = Math.Max(0, Total() - Height);
                    _scroll = Math.Max(0, Math.Min(most, _scroll + (delta > 0 ? -40 : 40)));
                    Invalidate();
                    return;
                }
                base.WndProc(ref m);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Raised);

                using (Pen edge = new Pen(Theme.Line, 1f))
                    g.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);

                int y = 5 - _scroll;
                for (int i = 0; i < _menu._rows.Count; i++)
                {
                    Row row = _menu._rows[i];
                    int h = row.Height;
                    if (y + h > 0 && y < Height) PaintRow(g, row, i, y, h);
                    y += h;
                }
            }

            void PaintRow(Graphics g, Row row, int index, int y, int h)
            {
                if (row.Rule)
                {
                    using (Pen p = new Pen(Theme.LineSoft, 1f))
                        g.DrawLine(p, 10, y + h / 2, Width - 10, y + h / 2);
                    return;
                }

                if (row.Header)
                {
                    using (Font f = Theme.UiSemi(7.25f))
                        TextRenderer.DrawText(g, row.Text.ToUpperInvariant(), f,
                            new Rectangle(13, y, Width - 26, h), Theme.TextFaint,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    if (row.Note.Length > 0)
                        using (Font f = Theme.Ui(7.25f))
                            TextRenderer.DrawText(g, row.Note, f, new Rectangle(13, y, Width - 26, h),
                                Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                                TextFormatFlags.NoPrefix);
                    return;
                }

                if (index == _hot && !row.Dim)
                {
                    using (GraphicsPath path = Theme.Round(new Rectangle(5, y + 1, Width - 10, h - 2), 6))
                    using (SolidBrush b = new SolidBrush(Theme.Hover))
                        g.FillPath(b, path);
                }

                Color colour = row.Dim ? Theme.TextFaint : Theme.Text;

                if (row.Ticked)
                    using (Pen p = new Pen(Theme.Accent, 1.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new PointF[] { new PointF(15, y + h / 2f),
                                                      new PointF(18.5f, y + h / 2f + 3.5f),
                                                      new PointF(24, y + h / 2f - 4f) });

                // The note is measured first and the text given what is left,
                // or a long title runs underneath it and both are unreadable.
                int noteWidth = 0;
                if (row.Note.Length > 0)
                    using (Font f = Theme.Ui(7.5f))
                    {
                        noteWidth = TextRenderer.MeasureText(g, row.Note, f, Size.Empty,
                                                             TextFormatFlags.NoPrefix).Width + 10;
                        TextRenderer.DrawText(g, row.Note, f, new Rectangle(31, y, Width - 44, h),
                            Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                            TextFormatFlags.NoPrefix);
                    }

                using (Font f = row.Ticked ? Theme.UiSemi(8.75f) : Theme.Ui(8.75f))
                    TextRenderer.DrawText(g, row.Text, f, new Rectangle(31, y, Math.Max(20, Width - 44 - noteWidth), h),
                        row.Ticked ? Theme.Accent : colour,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // =========================================================================
    // Check list
    // =========================================================================
    /// <summary>
    /// A fixed-height scrolling list of rows that can be ticked, with headings.
    ///
    /// For choosing which of a provider's models the panel may offer. A real
    /// account lists dozens, so a toggle per row would be a settings page
    /// several screens tall; this is one box the height of a paragraph that
    /// scrolls, which is how every "pick several of these" control works.
    /// </summary>
    public class NsCheckList : NsBase
    {
        public class Row
        {
            public string Text = "";
            public string Note = "";
            public bool Header;
            public bool Ticked;
            public bool Faint;
            public object Tag;
            public int Height { get { return Header ? 24 : 26; } }
        }

        readonly List<Row> _rows = new List<Row>();
        int _hot = -1;
        int _scroll;

        /// <summary>A row was ticked or unticked. The row carries its own state.</summary>
        public event EventHandler<EventArgs> Changed;

        public NsCheckList()
        {
            Size = new Size(260, 180);
            Font = Theme.Ui(8.5f);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            // WndProc catches the wheel and hands it here; OnMouseWheel only
            // sees synthetic ones.
            WheelDelta += delegate (int delta) { ScrollBy(delta); };
        }

        public IList<Row> Rows { get { return _rows; } }

        public void Clear() { _rows.Clear(); _scroll = 0; Invalidate(); }

        public void AddHeader(string text, string note)
        {
            _rows.Add(new Row { Text = text, Note = note ?? "", Header = true });
            Invalidate();
        }

        public void Add(string text, string note, bool ticked, bool faint, object tag)
        {
            _rows.Add(new Row { Text = text, Note = note ?? "", Ticked = ticked, Faint = faint, Tag = tag });
            Invalidate();
        }

        int Total()
        {
            int n = 8;
            foreach (Row row in _rows) n += row.Height;
            return n;
        }

        int RowAt(int y)
        {
            int at = 4 - _scroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (y >= at && y < at + _rows[i].Height) return _rows[i].Header ? -1 : i;
                at += _rows[i].Height;
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i != _hot) { _hot = i; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i >= 0)
            {
                _rows[i].Ticked = !_rows[i].Ticked;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // Reached only from a synthetic wheel (automation, or another
            // control raising it): the real one is taken in WndProc, so that
            // the settings page cannot scroll away underneath.
            ScrollBy(e.Delta);
        }

        protected override void WndProc(ref Message m)
        {
            // WM_MOUSEWHEEL, before base: this is what actually keeps the wheel
            // here. Left to WinForms, a wheel over this list was handed to the
            // AutoScroll panel behind it and scrolled the whole settings page,
            // and the list itself -- the one control the operator reaches for
            // because the list is long -- did not move.
            if (m.Msg == 0x020A)
            {
                int delta = unchecked((short)((long)m.WParam >> 16));
                if (EatWheel(delta)) return;
            }
            base.WndProc(ref m);
        }

        void ScrollBy(int delta)
        {
            int most = Math.Max(0, Total() - Height);
            if (most <= 0) return;

            // One notch is one line, not a fixed 40 pixels: a trackpad sends a
            // stream of small deltas, and a fixed step per notch made a gentle
            // two-finger flick jump a dozen rows at a time.
            int step = Math.Max(18, _rows.Count > 0 ? _rows[0].Height : 24);
            _scroll = Math.Max(0, Math.Min(most, _scroll + (delta > 0 ? -step : step)));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (r.Width <= 2 || r.Height <= 2) return;

            using (GraphicsPath path = Theme.Round(r, 8))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }

            Region clip = g.Clip;
            g.SetClip(new Rectangle(1, 1, Width - 2, Height - 2), CombineMode.Intersect);

            int y = 4 - _scroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (y + row.Height > 0 && y < Height) PaintRow(g, row, i, y);
                y += row.Height;
            }

            g.Clip = clip;

            // The same hairline the inspector uses, for the same reason: it is
            // never dragged, it only has to say how much is below.
            int total = Total();
            if (total <= Height) return;
            int thumb = Math.Max(24, Height * Height / total);
            int travel = Math.Max(1, total - Height);
            int top = (Height - thumb) * _scroll / travel;
            using (SolidBrush b = new SolidBrush(Theme.Line))
                g.FillRectangle(b, Width - 6, top + 2, 3, thumb - 4);
        }

        void PaintRow(Graphics g, Row row, int index, int y)
        {
            if (row.Header)
            {
                using (Font f = Theme.UiSemi(7.25f))
                    TextRenderer.DrawText(g, row.Text.ToUpperInvariant(), f,
                        new Rectangle(10, y, Width - 20, row.Height), Theme.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (row.Note.Length > 0)
                    using (Font f = Theme.Ui(7.25f))
                        TextRenderer.DrawText(g, row.Note, f, new Rectangle(10, y, Width - 22, row.Height),
                            Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                            TextFormatFlags.NoPrefix);
                return;
            }

            if (index == _hot)
                using (GraphicsPath path = Theme.Round(new Rectangle(4, y + 1, Width - 12, row.Height - 2), 5))
                using (SolidBrush b = new SolidBrush(Theme.Hover))
                    g.FillPath(b, path);

            Rectangle box = new Rectangle(10, y + (row.Height - 14) / 2, 14, 14);
            using (GraphicsPath path = Theme.Round(box, 4))
            {
                if (row.Ticked)
                {
                    using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
                    using (Pen p = new Pen(Theme.OnAccent, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(p, new PointF[] { new PointF(box.X + 3.2f, box.Y + 7f),
                                                      new PointF(box.X + 6f, box.Y + 9.8f),
                                                      new PointF(box.X + 10.8f, box.Y + 4.2f) });
                }
                else using (Pen p = new Pen(Theme.Line, 1.2f)) g.DrawPath(p, path);
            }

            using (Font f = Font)
                TextRenderer.DrawText(g, row.Text, f, new Rectangle(31, y, Width - 44, row.Height),
                    row.Faint ? Theme.TextFaint : Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
