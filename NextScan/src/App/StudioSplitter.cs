// =============================================================================
// NextScan Studio - Splitters, drag grips, and the window-edge resize helper
// Plan ref: MASTER_PLAN section 13.1.
//
// THE EDGE-RESIZE BUG THIS FILE EXISTS TO FIX
// The shell handled WM_NCHITTEST on the Form and returned HTLEFT/HTBOTTOM/etc,
// which looked correct and did nothing. Windows sends WM_NCHITTEST to the window
// under the cursor, and the rail / drawer / canvas / filmstrip covered the whole
// client area - so the child received it and the form never saw the edge.
//
// The fix is for children to answer HTTRANSPARENT near the window border, which
// hands the hit test up to the form. ResizeAware does that for our own controls;
// for stock Panels the shell insets them instead.
// =============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>Shared hit-test constants for the borderless window frame.</summary>
    public static class Frame
    {
        public const int WM_NCHITTEST = 0x0084;
        public const int HTTRANSPARENT = -1;
        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                         HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>How close to the window edge counts as a resize grab.</summary>
        public const int Grip = 6;

        /// <summary>
        /// True when a point in a child control is within the grip of the form
        /// edge, meaning the child should decline the hit so the form can resize.
        /// </summary>
        public static bool NearFormEdge(Control child, Point screenPoint)
        {
            Form f = child.FindForm();
            if (f == null || f.WindowState == FormWindowState.Maximized) return false;

            Point p = f.PointToClient(screenPoint);
            return p.X <= Grip || p.Y <= Grip ||
                   p.X >= f.ClientSize.Width - Grip ||
                   p.Y >= f.ClientSize.Height - Grip;
        }
    }

    /// <summary>
    /// A Panel that gets out of the way of the window resize border.
    /// Used for the full-bleed shell panels.
    /// </summary>
    public class ResizeAwarePanel : Panel
    {
        public ResizeAwarePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Frame.WM_NCHITTEST)
            {
                Point screen = new Point(m.LParam.ToInt32());
                if (Frame.NearFormEdge(this, screen))
                {
                    m.Result = (IntPtr)Frame.HTTRANSPARENT;
                    return;
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Draggable divider between two docked panels.
    ///
    /// Reports a delta rather than resizing anything itself, because the shell
    /// owns the layout maths and has to clamp against the canvas minimum width.
    /// </summary>
    public class NsSplitter : NsBase
    {
        public bool Vertical = true;

        /// <summary>Positive delta means the pointer moved right / down.</summary>
        public event EventHandler<DeltaEventArgs> Dragged;

        /// <summary>Raised once the drag ends, so the caller can re-flow content.</summary>
        public event EventHandler DragFinished;

        public class DeltaEventArgs : EventArgs
        {
            public readonly int Delta;
            public DeltaEventArgs(int d) { Delta = d; }
        }

        bool _dragging;
        int _last;

        public NsSplitter()
        {
            Width = 6;
            Cursor = Cursors.SizeWE;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _dragging = true;
            _last = Vertical ? Cursor.Position.X : Cursor.Position.Y;
            Capture = true;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // Capture is released on mouse-up, but a mouse-up can be lost - the
            // window loses focus mid-drag, another app steals capture, or the
            // click was synthesised. Without this check the splitter stays glued
            // to the pointer and the panel collapses to its minimum the next time
            // the mouse moves anywhere.
            if (_dragging && (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
            {
                _dragging = false;
                Capture = false;
                if (DragFinished != null) DragFinished(this, EventArgs.Empty);
            }

            if (_dragging)
            {
                // Screen coordinates: the splitter itself moves during the drag,
                // so client-relative deltas would feed back on themselves.
                int now = Vertical ? Cursor.Position.X : Cursor.Position.Y;
                int d = now - _last;
                if (d != 0)
                {
                    _last = now;
                    if (Dragged != null) Dragged(this, new DeltaEventArgs(d));
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool was = _dragging;
            _dragging = false;
            Capture = false;
            if (was && DragFinished != null) DragFinished(this, EventArgs.Empty);
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            double hot = Math.Max(HotAnim.Eased, _dragging ? 1 : 0);

            // Hairline at rest; a soft amber bar while hovered, so the divider is
            // discoverable without drawing a permanent heavy line.
            using (Pen p = new Pen(Theme.Mix(Theme.LineSoft, Theme.Accent, hot), 1f))
            {
                if (Vertical) g.DrawLine(p, Width / 2, 0, Width / 2, Height);
                else g.DrawLine(p, 0, Height / 2, Width, Height / 2);
            }

            if (hot > 0.05)
            {
                int a = (int)(70 * hot);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(a, Theme.Accent)))
                {
                    if (Vertical)
                    {
                        int h = Math.Min(Height - 40, 46);
                        if (h > 6) g.FillRectangle(b, Width / 2 - 1, (Height - h) / 2, 3, h);
                    }
                    else
                    {
                        int w = Math.Min(Width - 40, 46);
                        if (w > 6) g.FillRectangle(b, (Width - w) / 2, Height / 2 - 1, w, 3);
                    }
                }
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Frame.WM_NCHITTEST)
            {
                Point screen = new Point(m.LParam.ToInt32());
                if (Frame.NearFormEdge(this, screen)) { m.Result = (IntPtr)Frame.HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// The six-dot handle on a floating bar. Dragging it moves the bar; dragging
    /// with the right button, or with Shift held, resizes it instead.
    ///
    /// A dedicated grip rather than "drag anywhere on the bar" because the bars
    /// are made of buttons - dragging from a button would either move the bar or
    /// press the button, and users cannot predict which.
    /// </summary>
    public class NsGrip : NsBase
    {
        public event EventHandler<MoveEventArgs> Moved;
        public event EventHandler<MoveEventArgs> Resized;

        public class MoveEventArgs : EventArgs
        {
            public readonly int Dx, Dy;
            public MoveEventArgs(int dx, int dy) { Dx = dx; Dy = dy; }
        }

        bool _dragging;
        bool _resizing;
        Point _last;

        public NsGrip()
        {
            Size = new Size(16, 30);
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _dragging = true;
            _resizing = (e.Button == MouseButtons.Right) ||
                        ((Control.ModifierKeys & Keys.Shift) == Keys.Shift);
            _last = Cursor.Position;
            Capture = true;
            Cursor = _resizing ? Cursors.SizeNWSE : Cursors.SizeAll;
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            // Same lost-mouse-up guard as NsSplitter.
            if (_dragging && (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) == MouseButtons.None)
            {
                _dragging = false;
                _resizing = false;
                Capture = false;
                Cursor = Cursors.SizeAll;
            }

            if (_dragging)
            {
                Point now = Cursor.Position;
                int dx = now.X - _last.X;
                int dy = now.Y - _last.Y;
                if (dx != 0 || dy != 0)
                {
                    _last = now;
                    if (_resizing) { if (Resized != null) Resized(this, new MoveEventArgs(dx, dy)); }
                    else { if (Moved != null) Moved(this, new MoveEventArgs(dx, dy)); }
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            _resizing = false;
            Capture = false;
            Cursor = Cursors.SizeAll;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            Color col = Theme.Mix(Theme.TextFaint, Theme.Accent, Math.Max(HotAnim.Eased, _dragging ? 1 : 0));
            int cx = Width / 2, cy = Height / 2;

            using (SolidBrush b = new SolidBrush(col))
            {
                for (int row = -1; row <= 1; row++)
                {
                    g.FillEllipse(b, cx - 3, cy + row * 5 - 1, 2, 2);
                    g.FillEllipse(b, cx + 1, cy + row * 5 - 1, 2, 2);
                }
            }
        }
    }
}
