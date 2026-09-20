// =============================================================================
// NextScan Studio - Interactive 16-bit curve editor control
// Plan ref: MASTER_PLAN section 9.2, 13.1.
//
// Features Fritsch-Carlson monotone cubic spline knots, channel selection
// (RGB Master, Red, Green, Blue), live histogram display, and instant LUT updates.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public enum CurveChannel { Rgb = 0, Red = 1, Green = 2, Blue = 3 }

    public class StudioCurveEditor : Control
    {
        public event EventHandler CurveChanged;

        Dictionary<CurveChannel, List<PointF>> _channelKnots = new Dictionary<CurveChannel, List<PointF>>();
        CurveChannel _activeChannel = CurveChannel.Rgb;
        int _dragKnotIndex = -1;
        int[] _histogram = new int[256];
        int _maxHistValue = 1;

        public CurveChannel ActiveChannel
        {
            get { return _activeChannel; }
            set { if (_activeChannel != value) { _activeChannel = value; Invalidate(); } }
        }

        public StudioCurveEditor()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            Size = new Size(240, 220);
            BackColor = Color.FromArgb(24, 28, 36);
            Cursor = Cursors.Cross;

            ResetAllCurves();
        }

        public void ResetAllCurves()
        {
            _channelKnots[CurveChannel.Rgb] = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
            _channelKnots[CurveChannel.Red] = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
            _channelKnots[CurveChannel.Green] = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
            _channelKnots[CurveChannel.Blue] = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
            Invalidate();
            if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
        }

        public void ResetActiveCurve()
        {
            _channelKnots[_activeChannel] = new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
            Invalidate();
            if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
        }

        public List<PointF> GetKnots(CurveChannel ch)
        {
            List<PointF> list;
            if (_channelKnots.TryGetValue(ch, out list)) return new List<PointF>(list);
            return new List<PointF> { new PointF(0, 0), new PointF(255, 255) };
        }

        public void SetKnots(CurveChannel ch, List<PointF> knots)
        {
            if (knots != null && knots.Count >= 2)
            {
                _channelKnots[ch] = new List<PointF>(knots);
                Invalidate();
                if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
            }
        }

        public ushort[] BuildLut16(CurveChannel ch)
        {
            List<PointF> pts = GetKnots(ch);
            // Scale points to [0, 65535]
            List<PointF> scaled = new List<PointF>();
            foreach (PointF p in pts)
            {
                scaled.Add(new PointF(p.X * 65535f / 255f, p.Y * 65535f / 255f));
            }
            return Curves.BuildLut16(scaled);
        }

        public byte[] BuildLut8(CurveChannel ch)
        {
            return Curves.BuildLut8(GetKnots(ch));
        }

        public void SetHistogram(int[] hist)
        {
            if (hist == null || hist.Length != 256) return;
            Array.Copy(hist, _histogram, 256);
            _maxHistValue = 1;
            for (int i = 1; i < 255; i++)
            {
                if (_histogram[i] > _maxHistValue) _maxHistValue = _histogram[i];
            }
            Invalidate();
        }

        Rectangle PlotRect
        {
            get { return new Rectangle(16, 12, Width - 32, Height - 28); }
        }

        PointF ValueToPoint(PointF val, Rectangle plot)
        {
            float px = plot.Left + (val.X / 255f) * plot.Width;
            float py = plot.Bottom - (val.Y / 255f) * plot.Height;
            return new PointF(px, py);
        }

        PointF PointToValue(Point p, Rectangle plot)
        {
            float vx = ((float)(p.X - plot.Left) / plot.Width) * 255f;
            float vy = ((float)(plot.Bottom - p.Y) / plot.Height) * 255f;
            vx = Math.Max(0f, Math.Min(255f, vx));
            vy = Math.Max(0f, Math.Min(255f, vy));
            return new PointF(vx, vy);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle plot = PlotRect;

            // Background & border
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(20, 24, 32)))
            {
                g.FillRectangle(bg, plot);
            }

            // Draw histogram
            if (_maxHistValue > 1)
            {
                using (SolidBrush hb = new SolidBrush(Color.FromArgb(40, 70, 90, 110)))
                {
                    for (int i = 0; i < 256; i++)
                    {
                        float hx = plot.Left + (i / 255f) * plot.Width;
                        float hh = ((float)_histogram[i] / _maxHistValue) * (plot.Height - 4);
                        if (hh > 1)
                        {
                            g.FillRectangle(hb, hx, plot.Bottom - hh, Math.Max(1f, plot.Width / 256f), hh);
                        }
                    }
                }
            }

            // Grid lines (25%, 50%, 75%)
            using (Pen gridPen = new Pen(Color.FromArgb(45, 52, 65), 1f))
            {
                gridPen.DashStyle = DashStyle.Dash;
                for (int i = 1; i <= 3; i++)
                {
                    float gx = plot.Left + (i / 4f) * plot.Width;
                    float gy = plot.Top + (i / 4f) * plot.Height;
                    g.DrawLine(gridPen, gx, plot.Top, gx, plot.Bottom);
                    g.DrawLine(gridPen, plot.Left, gy, plot.Right, gy);
                }
            }

            // Diagonal baseline
            using (Pen diagPen = new Pen(Color.FromArgb(55, 65, 80), 1f))
            {
                g.DrawLine(diagPen, plot.Left, plot.Bottom, plot.Right, plot.Top);
            }

            // Outer border
            using (Pen borderPen = new Pen(Color.FromArgb(70, 80, 100), 1f))
            {
                g.DrawRectangle(borderPen, plot);
            }

            // Draw inactive curves lightly
            foreach (CurveChannel ch in Enum.GetValues(typeof(CurveChannel)))
            {
                if (ch == _activeChannel) continue;
                DrawChannelCurve(g, ch, plot, true);
            }

            // Draw active curve
            DrawChannelCurve(g, _activeChannel, plot, false);

            // Draw knots for active curve
            List<PointF> knots = _channelKnots[_activeChannel];
            Color knotColor = GetChannelColor(_activeChannel);
            for (int i = 0; i < knots.Count; i++)
            {
                PointF pt = ValueToPoint(knots[i], plot);
                RectangleF knotRect = new RectangleF(pt.X - 4f, pt.Y - 4f, 8f, 8f);
                using (SolidBrush kb = new SolidBrush(i == _dragKnotIndex ? Color.White : knotColor))
                using (Pen kp = new Pen(Color.FromArgb(16, 20, 26), 1.5f))
                {
                    g.FillEllipse(kb, knotRect);
                    g.DrawEllipse(kp, knotRect);
                }
            }
        }

        void DrawChannelCurve(Graphics g, CurveChannel ch, Rectangle plot, bool isGhost)
        {
            byte[] lut = Curves.BuildLut8(_channelKnots[ch]);
            Color col = GetChannelColor(ch);
            Color drawCol = isGhost ? Color.FromArgb(60, col.R, col.G, col.B) : col;
            float penWidth = isGhost ? 1f : 2f;

            using (Pen cp = new Pen(drawCol, penWidth))
            {
                PointF prev = ValueToPoint(new PointF(0, lut[0]), plot);
                for (int x = 1; x <= 255; x++)
                {
                    PointF cur = ValueToPoint(new PointF(x, lut[x]), plot);
                    g.DrawLine(cp, prev, cur);
                    prev = cur;
                }
            }
        }

        Color GetChannelColor(CurveChannel ch)
        {
            switch (ch)
            {
                case CurveChannel.Red: return Color.FromArgb(240, 75, 75);
                case CurveChannel.Green: return Color.FromArgb(75, 215, 100);
                case CurveChannel.Blue: return Color.FromArgb(70, 150, 255);
                default: return Color.FromArgb(230, 235, 245);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Rectangle plot = PlotRect;
            List<PointF> knots = _channelKnots[_activeChannel];

            // Right click: remove knot
            if (e.Button == MouseButtons.Right)
            {
                for (int i = 1; i < knots.Count - 1; i++)
                {
                    PointF pt = ValueToPoint(knots[i], plot);
                    if (Math.Abs(pt.X - e.X) <= 6 && Math.Abs(pt.Y - e.Y) <= 6)
                    {
                        knots.RemoveAt(i);
                        Invalidate();
                        if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
                        return;
                    }
                }
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            // Check if clicking existing knot
            for (int i = 0; i < knots.Count; i++)
            {
                PointF pt = ValueToPoint(knots[i], plot);
                if (Math.Abs(pt.X - e.X) <= 6 && Math.Abs(pt.Y - e.Y) <= 6)
                {
                    _dragKnotIndex = i;
                    return;
                }
            }

            // Clicked empty area: add new knot
            if (plot.Contains(e.Location) && knots.Count < 16)
            {
                PointF val = PointToValue(e.Location, plot);
                knots.Add(val);
                knots.Sort((a, b) => a.X.CompareTo(b.X));
                _dragKnotIndex = knots.FindIndex(k => Math.Abs(k.X - val.X) < 0.1f && Math.Abs(k.Y - val.Y) < 0.1f);
                Invalidate();
                if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragKnotIndex >= 0 && _dragKnotIndex < _channelKnots[_activeChannel].Count)
            {
                Rectangle plot = PlotRect;
                PointF val = PointToValue(e.Location, plot);
                List<PointF> knots = _channelKnots[_activeChannel];

                // First and last knots have locked X coordinates
                if (_dragKnotIndex == 0) val.X = 0f;
                else if (_dragKnotIndex == knots.Count - 1) val.X = 255f;
                else
                {
                    // Clamp X between neighbors
                    float minX = knots[_dragKnotIndex - 1].X + 2f;
                    float maxX = knots[_dragKnotIndex + 1].X - 2f;
                    val.X = Math.Max(minX, Math.Min(maxX, val.X));
                }

                knots[_dragKnotIndex] = val;
                Invalidate();
                if (CurveChanged != null) CurveChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _dragKnotIndex = -1;
            Invalidate();
        }
    }
}
