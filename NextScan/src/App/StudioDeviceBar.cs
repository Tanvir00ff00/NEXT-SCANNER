// =============================================================================
// NextScan Studio - Popup menu + the device bar that lives in the title bar
// Plan ref: MASTER_PLAN section 13.1.
//
// WHY THE DEVICE PAGE WENT AWAY
// The rail used to open a Device page holding the scanner list, the transport
// list, a re-probe button and the paper source. But "which scanner, reached how,
// from which tray" is a property of the whole session, not a step inside it: the
// user wants it always visible and always one click away, not behind a drawer
// they must open and then close again to get the canvas width back.
//
// So it moved into the chrome as one strip, and the rail lost a page.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>Lightweight borderless popup list for chrome-level choosers.</summary>
    public static class NsMenu
    {
        public static void Show(Control owner, Point screenPoint, IList<string> items,
                                int selected, int minWidth, Action<int> onPick)
        {
            if (owner == null || items == null || items.Count == 0) return;

            const int rowHeight = 28;
            int width = minWidth;
            using (Graphics g = owner.CreateGraphics())
            using (Font f = Theme.Ui(9f))
            {
                foreach (string s in items)
                    width = Math.Max(width, TextRenderer.MeasureText(g, s, f).Width + 34);
            }

            Form popup = new Form();
            popup.FormBorderStyle = FormBorderStyle.None;
            popup.ShowInTaskbar = false;
            popup.StartPosition = FormStartPosition.Manual;
            popup.BackColor = Theme.Raised;
            popup.Size = new Size(width, items.Count * rowHeight + 8);
            popup.Location = screenPoint;

            // Keep the menu on screen when opened near an edge.
            Rectangle wa = Screen.FromPoint(screenPoint).WorkingArea;
            if (popup.Right > wa.Right) popup.Left = Math.Max(wa.Left, wa.Right - popup.Width - 4);
            if (popup.Bottom > wa.Bottom) popup.Top = Math.Max(wa.Top, screenPoint.Y - popup.Height - 34);

            MenuList list = new MenuList(items, selected, rowHeight);
            list.Dock = DockStyle.Fill;
            list.Picked += delegate (object s, MenuList.PickEventArgs e)
            {
                try { popup.Close(); popup.Dispose(); }
                catch { }
                if (onPick != null) onPick(e.Index);
            };

            popup.Controls.Add(list);

            // Only dismiss once it has actually been active. Show() does not
            // always activate an owned form, and a Deactivate that arrives
            // before the first Activated would close the menu instantly.
            bool wasActive = false;
            popup.Activated += delegate { wasActive = true; };
            popup.Deactivate += delegate
            {
                if (!wasActive) return;
                try { popup.Close(); popup.Dispose(); } catch { }
            };

            popup.Show(owner.FindForm());
            popup.BringToFront();
            popup.Activate();
            list.Focus();
        }

        class MenuList : Control
        {
            readonly IList<string> _items;
            readonly int _selected;
            readonly int _rowHeight;
            int _hot = -1;

            public class PickEventArgs : EventArgs
            {
                public readonly int Index;
                public PickEventArgs(int i) { Index = i; }
            }

            public event EventHandler<PickEventArgs> Picked;

            public MenuList(IList<string> items, int selected, int rowHeight)
            {
                _items = items;
                _selected = selected;
                _rowHeight = rowHeight;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
                BackColor = Theme.Raised;
                Cursor = Cursors.Hand;
            }

            int RowAt(int y)
            {
                int i = (y - 4) / _rowHeight;
                return (i < 0 || i >= _items.Count) ? -1 : i;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int r = RowAt(e.Y);
                if (r != _hot) { _hot = r; Invalidate(); }
                base.OnMouseMove(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                int r = RowAt(e.Y);
                if (r >= 0 && Picked != null) Picked(this, new PickEventArgs(r));
                base.OnMouseDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Raised);

                using (Pen p = new Pen(Theme.Line, 1f))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);

                using (Font f = Theme.Ui(9f))
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        Rectangle row = new Rectangle(4, 4 + i * _rowHeight, Width - 8, _rowHeight);
                        if (row.Bottom > Height) break;

                        bool sel = (i == _selected);
                        if (sel || i == _hot)
                        {
                            using (GraphicsPath path = Theme.Round(row, 6))
                            using (SolidBrush b = new SolidBrush(sel ? Theme.Accent : Theme.Field))
                                g.FillPath(b, path);
                        }

                        Color col = sel ? Theme.OnAccent : Theme.Text;
                        TextRenderer.DrawText(g, _items[i], f,
                            new Rectangle(row.X + 10, row.Y, row.Width - 14, row.Height), col,
                            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Status dot, device name (click to change), transport (click to change),
    /// re-probe, and the paper source as icons - all in one title-bar strip.
    /// </summary>
    public class NsDeviceBar : NsBase
    {
        public string DeviceName = "No scanner";
        public string TransportText = "";
        public bool Connected;
        public bool Busy;

        /// <summary>0 flatbed, 1 feeder, 2 feeder duplex, 3 film.</summary>
        public int SourceIndex;
        public bool[] SourceEnabled = new bool[] { true, true, true, true };

        public event EventHandler DeviceClicked;
        public event EventHandler TransportClicked;
        public event EventHandler RefreshClicked;
        public event EventHandler SourceChanged;

        /// <summary>Raised while the bar is dragged by its empty area.</summary>
        public event EventHandler<int> Nudged;

        readonly Anim _pulse = new Anim(0);
        int _hotZone = -1;

        const int DotW = 26;
        const int RefreshW = 30;
        const int IconW = 28;

        public NsDeviceBar()
        {
            Size = new Size(560, 30);
            Font = Theme.UiSemi(9f);
            Cursor = Cursors.Hand;
            Animator.Attach(this, _pulse);
        }

        public void SetBusy(bool busy)
        {
            Busy = busy;
            _pulse.Rate = 0.06;
            _pulse.Target = busy ? 1 : 0;
            Animator.Kick();
            Invalidate();
        }

        int MeasureName()
        {
            using (Graphics g = CreateGraphics())
                return Math.Min(340, TextRenderer.MeasureText(g, DeviceName, Font).Width + 30);
        }

        int MeasureTransport()
        {
            if (string.IsNullOrEmpty(TransportText)) return 0;
            using (Graphics g = CreateGraphics())
            using (Font f = Theme.Ui(8.25f))
                return TextRenderer.MeasureText(g, TransportText, f).Width + 28;
        }

        Rectangle NameRect { get { return new Rectangle(DotW, 0, MeasureName(), Height); } }
        Rectangle TransportRect { get { Rectangle n = NameRect; return new Rectangle(n.Right, 0, MeasureTransport(), Height); } }
        Rectangle RefreshRect { get { Rectangle t = TransportRect; return new Rectangle(t.Right + 4, 3, RefreshW, Height - 6); } }
        Rectangle SourceRect(int i) { Rectangle r = RefreshRect; return new Rectangle(r.Right + 10 + i * IconW, 3, IconW, Height - 6); }

        /// <summary>Total width the bar needs for its current content.</summary>
        public int DesiredWidth { get { return SourceRect(3).Right + 10; } }

        int ZoneAt(Point p)
        {
            if (NameRect.Contains(p)) return 0;
            Rectangle t = TransportRect;
            if (t.Width > 0 && t.Contains(p)) return 1;
            if (RefreshRect.Contains(p)) return 2;
            for (int i = 0; i < 4; i++) if (SourceRect(i).Contains(p)) return 3 + i;
            return -1;
        }

        bool _dragging;
        int _dragLastX;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging && (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
            {
                _dragging = false;
                Capture = false;
            }

            if (_dragging)
            {
                int now = Cursor.Position.X;
                int dx = now - _dragLastX;
                if (dx != 0)
                {
                    _dragLastX = now;
                    if (Nudged != null) Nudged(this, dx);
                }
                base.OnMouseMove(e);
                return;
            }

            int z = ZoneAt(e.Location);
            if (z != _hotZone)
            {
                _hotZone = z;
                // Empty area is the drag handle for repositioning the whole bar.
                Cursor = (z < 0) ? Cursors.SizeAll : Cursors.Hand;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hotZone = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int z = ZoneAt(e.Location);
            if (z < 0)
            {
                // Dragging the strip itself repositions it in the title bar.
                _dragging = true;
                _dragLastX = Cursor.Position.X;
                Capture = true;
                base.OnMouseDown(e);
                return;
            }
            // Base FIRST. NsBase.OnMouseDown takes focus, and taking focus after
            // a menu has opened deactivates that menu - which closes it on the
            // same click that asked for it. That is why the device and transport
            // menus appeared to do nothing at all.
            base.OnMouseDown(e);

            if (z == 0 && DeviceClicked != null) DeviceClicked(this, EventArgs.Empty);
            else if (z == 1 && TransportClicked != null) TransportClicked(this, EventArgs.Empty);
            else if (z == 2 && RefreshClicked != null) RefreshClicked(this, EventArgs.Empty);
            else if (z >= 3 && z <= 6)
            {
                int idx = z - 3;
                if (SourceEnabled[idx] && idx != SourceIndex)
                {
                    SourceIndex = idx;
                    Invalidate();
                    if (SourceChanged != null) SourceChanged(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>Screen point to drop a menu under zone 0 (device) or 1 (transport).</summary>
        public Point MenuPointFor(int zone)
        {
            Rectangle r = (zone == 1) ? TransportRect : NameRect;
            return PointToScreen(new Point(r.X, Height + 4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Rectangle full = new Rectangle(0, 0, Width - 1, Height - 1);
            if (full.Width <= 2 || full.Height <= 2) return;

            using (GraphicsPath path = Theme.Round(full, Height / 2))
            {
                using (SolidBrush b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }

            Color dot = Busy ? Theme.Warn : (Connected ? Theme.Good : Theme.Danger);
            double glow = Busy ? (0.3 + 0.7 * _pulse.Eased) : 1.0;
            const int d = 8;
            int cy = (Height - d) / 2;
            using (SolidBrush halo = new SolidBrush(Color.FromArgb((int)(75 * glow), dot)))
                g.FillEllipse(halo, 8, cy - 3, d + 6, d + 6);
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, 11, cy, d, d);

            Rectangle nameRect = NameRect;
            DrawZone(g, nameRect, _hotZone == 0);
            TextRenderer.DrawText(g, DeviceName, Font,
                new Rectangle(nameRect.X + 2, 0, Math.Max(0, nameRect.Width - 18), Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            DrawCaret(g, new Point(nameRect.Right - 11, Height / 2), _hotZone == 0 ? Theme.Accent : Theme.TextFaint);

            Rectangle tr = TransportRect;
            if (tr.Width > 0)
            {
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawLine(p, tr.X, 7, tr.X, Height - 7);
                DrawZone(g, tr, _hotZone == 1);
                using (Font f = Theme.Ui(8.25f))
                    TextRenderer.DrawText(g, TransportText, f,
                        new Rectangle(tr.X + 8, 0, Math.Max(0, tr.Width - 22), Height), Theme.TextDim,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                DrawCaret(g, new Point(tr.Right - 11, Height / 2), _hotZone == 1 ? Theme.Accent : Theme.TextFaint);
            }

            DrawRefresh(g, RefreshRect, _hotZone == 2);

            for (int i = 0; i < 4; i++)
            {
                Rectangle r = SourceRect(i);
                if (r.Right > Width) break;
                DrawSourceIcon(g, r, i, i == SourceIndex, _hotZone == 3 + i, SourceEnabled[i]);
            }
        }

        static void DrawZone(Graphics g, Rectangle r, bool hot)
        {
            if (!hot || r.Width <= 0) return;
            Rectangle rr = new Rectangle(r.X, 3, Math.Max(1, r.Width - 2), Math.Max(1, r.Height - 6));
            using (GraphicsPath path = Theme.Round(rr, 6))
            using (SolidBrush b = new SolidBrush(Theme.Hover))
                g.FillPath(b, path);
        }

        static void DrawCaret(Graphics g, Point c, Color colour)
        {
            using (Pen p = new Pen(colour, 1.4f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawLines(p, new Point[]
                {
                    new Point(c.X - 3, c.Y - 1), new Point(c.X, c.Y + 2), new Point(c.X + 3, c.Y - 1)
                });
            }
        }

        static void DrawRefresh(Graphics g, Rectangle r, bool hot)
        {
            if (hot)
            {
                using (GraphicsPath path = Theme.Round(r, 6))
                using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            }

            Color col = hot ? Theme.Accent : Theme.TextDim;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            using (Pen p = new Pen(col, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawArc(p, cx - 6, cy - 6, 12, 12, 40, 280);
                g.DrawLines(p, new Point[]
                {
                    new Point(cx + 2, cy - 8), new Point(cx + 7, cy - 5), new Point(cx + 2, cy - 2)
                });
            }
        }

        /// <summary>
        /// Paper-source icons are drawn, not glyphed: the nearest Unicode shapes
        /// are inconsistent between fonts and read as mojibake at this size.
        /// </summary>
        static void DrawSourceIcon(Graphics g, Rectangle r, int kind, bool selected, bool hot, bool enabled)
        {
            if (selected)
            {
                using (GraphicsPath path = Theme.Round(r, 6))
                using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillPath(b, path);
            }
            else if (hot && enabled)
            {
                using (GraphicsPath path = Theme.Round(r, 6))
                using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, path);
            }

            Color col = !enabled ? Theme.TextFaint
                      : selected ? Theme.OnAccent
                      : (hot ? Theme.Text : Theme.TextDim);

            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;

            using (Pen p = new Pen(col, 1.5f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;

                if (kind == 0)
                {
                    // Flatbed: platen with a lid seam.
                    g.DrawRectangle(p, cx - 7, cy - 5, 14, 10);
                    g.DrawLine(p, cx - 4, cy + 1, cx + 4, cy + 1);
                }
                else if (kind == 1)
                {
                    // Feeder: a sheet above the feed slot.
                    g.DrawRectangle(p, cx - 6, cy - 8, 12, 6);
                    g.DrawLine(p, cx - 8, cy + 2, cx + 8, cy + 2);
                    g.DrawLines(p, new Point[] { new Point(cx - 2, cy + 5), new Point(cx, cy + 7), new Point(cx + 2, cy + 5) });
                }
                else if (kind == 2)
                {
                    // Duplex: two opposed arrows.
                    g.DrawLine(p, cx - 4, cy - 6, cx - 4, cy + 6);
                    g.DrawLines(p, new Point[] { new Point(cx - 7, cy + 3), new Point(cx - 4, cy + 6), new Point(cx - 1, cy + 3) });
                    g.DrawLine(p, cx + 4, cy + 6, cx + 4, cy - 6);
                    g.DrawLines(p, new Point[] { new Point(cx + 1, cy - 3), new Point(cx + 4, cy - 6), new Point(cx + 7, cy - 3) });
                }
                else
                {
                    // Film: a strip with sprocket holes.
                    g.DrawRectangle(p, cx - 7, cy - 6, 14, 12);
                    using (SolidBrush b = new SolidBrush(col))
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            g.FillRectangle(b, cx - 6 + i * 4, cy - 5, 2, 2);
                            g.FillRectangle(b, cx - 6 + i * 4, cy + 3, 2, 2);
                        }
                    }
                }
            }
        }
    }
}
