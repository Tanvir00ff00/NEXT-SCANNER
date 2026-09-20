// =============================================================================
// NextScan Studio - Interactive high-DPI canvas
// Plan ref: MASTER_PLAN section 13.1, 13.3.
//
// GPU-accelerated double-buffered preview canvas with 9-handle crop rectangle,
// auto-detected document overlays, deskew badges, zoom/pan, and scan sweep line.
// =============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public enum DragHandle { None, Body, Nw, N, Ne, E, Se, S, Sw, W, DrawNew }

    public class StudioCanvas : Control
    {
        public event EventHandler CropChanged;
        public event EventHandler ZoomChanged;

        Bitmap _image;
        Bitmap _processedImage;
        RectangleF _cropNorm = new RectangleF(0f, 0f, 1f, 1f);
        RotatedBox _detectedBox;
        DeskewResult _deskewResult;

        // Viewport transform
        float _zoom = 1.0f; // 1.0 = fit
        PointF _panOffset = PointF.Empty;
        bool _autoFit = true;

        // Interactive dragging
        DragHandle _activeHandle = DragHandle.None;
        Point _dragStartMouse = Point.Empty;
        Rectangle _dragStartCrop = Rectangle.Empty;
        bool _isPanning = false;
        Point _panStartMouse = Point.Empty;
        PointF _panStartOffset = PointF.Empty;

        // Live scan sweep animation
        bool _isScanning = false;
        float _sweepProgress = 0f; // 0..1
        Timer _sweepTimer;

        // Before/after comparison
        bool _splitView = false;
        float _splitPosition = 0.5f;

        const int HandleSize = 9;

        public StudioCanvas()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(14, 17, 24);
            Cursor = Cursors.Cross;

            _sweepTimer = new Timer();
            _sweepTimer.Interval = 33; // ~30 FPS
            _sweepTimer.Tick += delegate
            {
                if (_isScanning)
                {
                    Invalidate();
                }
            };
        }

        public Bitmap Image
        {
            get { return _image; }
            set
            {
                if (_image != value)
                {
                    _image = value;
                    if (_autoFit) ResetZoom();
                    Invalidate();
                }
            }
        }

        public Bitmap ProcessedImage
        {
            get { return _processedImage; }
            set
            {
                _processedImage = value;
                Invalidate();
            }
        }

        public RectangleF CropNorm
        {
            get { return _cropNorm; }
            set
            {
                _cropNorm = StudioSettings.ClampCrop(value);
                Invalidate();
                if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            }
        }

        public RotatedBox DetectedBox
        {
            get { return _detectedBox; }
            set { _detectedBox = value; Invalidate(); }
        }

        public DeskewResult DeskewResult
        {
            get { return _deskewResult; }
            set { _deskewResult = value; Invalidate(); }
        }

        public float Zoom
        {
            get { return _zoom; }
            set
            {
                _zoom = Math.Max(0.1f, Math.Min(8.0f, value));
                _autoFit = false;
                Invalidate();
                if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
            }
        }

        public bool SplitView
        {
            get { return _splitView; }
            set { _splitView = value; Invalidate(); }
        }

        public void SetScanning(bool scanning, float progress = 0f)
        {
            _isScanning = scanning;
            _sweepProgress = progress;
            if (scanning) _sweepTimer.Start();
            else _sweepTimer.Stop();
            Invalidate();
        }

        public void ResetZoom()
        {
            _autoFit = true;
            _zoom = 1.0f;
            _panOffset = PointF.Empty;
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        public Rectangle GetImageDisplayRect()
        {
            Bitmap img = _processedImage ?? _image;
            if (img == null) return ClientRectangle;

            float imgAspect = (float)img.Width / img.Height;
            float clientAspect = (float)Width / Height;

            int baseW, baseH;
            if (clientAspect > imgAspect)
            {
                baseH = Height - 20;
                baseW = (int)(baseH * imgAspect);
            }
            else
            {
                baseW = Width - 20;
                baseH = (int)(baseW / imgAspect);
            }

            int w = (int)(baseW * _zoom);
            int h = (int)(baseH * _zoom);
            int x = (Width - w) / 2 + (int)_panOffset.X;
            int y = (Height - h) / 2 + (int)_panOffset.Y;

            return new Rectangle(x, y, Math.Max(10, w), Math.Max(10, h));
        }

        Rectangle CropToPixelRect(Rectangle imgRect)
        {
            int cx = imgRect.Left + (int)(_cropNorm.X * imgRect.Width);
            int cy = imgRect.Top + (int)(_cropNorm.Y * imgRect.Height);
            int cw = (int)(_cropNorm.Width * imgRect.Width);
            int ch = (int)(_cropNorm.Height * imgRect.Height);
            return new Rectangle(cx, cy, Math.Max(1, cw), Math.Max(1, ch));
        }

        RectangleF PixelToCropNorm(Rectangle cropPx, Rectangle imgRect)
        {
            float nx = (float)(cropPx.X - imgRect.Left) / imgRect.Width;
            float ny = (float)(cropPx.Y - imgRect.Top) / imgRect.Height;
            float nw = (float)cropPx.Width / imgRect.Width;
            float nh = (float)cropPx.Height / imgRect.Height;
            return StudioSettings.ClampCrop(new RectangleF(nx, ny, nw, nh));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            Bitmap displayBmp = _processedImage ?? _image;
            if (displayBmp == null)
            {
                // Draw helpful empty state
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(130, 145, 165)))
                using (Font f1 = new Font("Segoe UI", 12f, FontStyle.Bold))
                using (Font f2 = new Font("Segoe UI", 9f))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.DrawString("NextScan Studio Ready", f1, tb, new RectangleF(0, Height / 2 - 30, Width, 30), sf);
                    g.DrawString("Press F5 for Preview scan or F7 for Final scan", f2, tb, new RectangleF(0, Height / 2, Width, 25), sf);
                }
                return;
            }

            Rectangle imgRect = GetImageDisplayRect();

            // 1. Draw Image
            if (_splitView && _image != null && _processedImage != null)
            {
                // Before / After Split
                int splitX = imgRect.Left + (int)(imgRect.Width * _splitPosition);

                // Left side: original
                Rectangle leftDst = new Rectangle(imgRect.Left, imgRect.Top, splitX - imgRect.Left, imgRect.Height);
                Rectangle leftSrc = new Rectangle(0, 0, (int)(_image.Width * _splitPosition), _image.Height);
                if (leftDst.Width > 0 && leftSrc.Width > 0)
                {
                    g.DrawImage(_image, leftDst, leftSrc, GraphicsUnit.Pixel);
                }

                // Right side: adjusted
                Rectangle rightDst = new Rectangle(splitX, imgRect.Top, imgRect.Right - splitX, imgRect.Height);
                Rectangle rightSrc = new Rectangle((int)(_processedImage.Width * _splitPosition), 0,
                                                   _processedImage.Width - (int)(_processedImage.Width * _splitPosition), _processedImage.Height);
                if (rightDst.Width > 0 && rightSrc.Width > 0)
                {
                    g.DrawImage(_processedImage, rightDst, rightSrc, GraphicsUnit.Pixel);
                }

                // Split divider line
                using (Pen sp = new Pen(Color.FromArgb(0, 195, 255), 2f))
                {
                    g.DrawLine(sp, splitX, imgRect.Top, splitX, imgRect.Bottom);
                }
            }
            else
            {
                g.DrawImage(displayBmp, imgRect);
            }

            // Outer border around glass/page
            using (Pen bedPen = new Pen(Color.FromArgb(65, 75, 95), 1.5f))
            {
                g.DrawRectangle(bedPen, imgRect);
            }

            // 2. Draw Detected Document Polygon Overlay
            if (_detectedBox != null && _detectedBox.IsValid && _detectedBox.Corners != null)
            {
                PointF[] pts = new PointF[4];
                float scaleX = (float)imgRect.Width / displayBmp.Width;
                float scaleY = (float)imgRect.Height / displayBmp.Height;
                for (int i = 0; i < 4; i++)
                {
                    pts[i] = new PointF(imgRect.Left + _detectedBox.Corners[i].X * scaleX,
                                       imgRect.Top + _detectedBox.Corners[i].Y * scaleY);
                }

                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddPolygon(pts);
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(35, 0, 180, 255)))
                    using (Pen dp = new Pen(Color.FromArgb(200, 0, 200, 255), 2f))
                    {
                        dp.DashStyle = DashStyle.Dash;
                        g.FillPath(fill, path);
                        g.DrawPath(dp, path);
                    }
                }
            }

            // 3. Draw Crop Rectangle & Darkened Surround
            Rectangle cropPx = CropToPixelRect(imgRect);

            // Shaded backdrop outside crop
            using (Region shroud = new Region(imgRect))
            {
                shroud.Exclude(cropPx);
                using (SolidBrush shroudBrush = new SolidBrush(Color.FromArgb(120, 10, 14, 20)))
                {
                    g.FillRegion(shroudBrush, shroud);
                }
            }

            // Crop boundary line
            using (Pen cropPen = new Pen(Color.FromArgb(255, 255, 255), 1.5f))
            {
                g.DrawRectangle(cropPen, cropPx);
            }

            // Rule of thirds lines inside crop
            if (_activeHandle != DragHandle.None && cropPx.Width > 30 && cropPx.Height > 30)
            {
                using (Pen guidePen = new Pen(Color.FromArgb(80, 255, 255, 255), 1f))
                {
                    guidePen.DashStyle = DashStyle.Dot;
                    float thirdW = cropPx.Width / 3f;
                    float thirdH = cropPx.Height / 3f;
                    g.DrawLine(guidePen, cropPx.Left + thirdW, cropPx.Top, cropPx.Left + thirdW, cropPx.Bottom);
                    g.DrawLine(guidePen, cropPx.Left + thirdW * 2, cropPx.Top, cropPx.Left + thirdW * 2, cropPx.Bottom);
                    g.DrawLine(guidePen, cropPx.Left, cropPx.Top + thirdH, cropPx.Right, cropPx.Top + thirdH);
                    g.DrawLine(guidePen, cropPx.Left, cropPx.Top + thirdH * 2, cropPx.Right, cropPx.Top + thirdH * 2);
                }
            }

            // 9 Handles
            DrawHandle(g, cropPx.Left, cropPx.Top);                             // NW
            DrawHandle(g, cropPx.Left + cropPx.Width / 2, cropPx.Top);          // N
            DrawHandle(g, cropPx.Right, cropPx.Top);                            // NE
            DrawHandle(g, cropPx.Right, cropPx.Top + cropPx.Height / 2);        // E
            DrawHandle(g, cropPx.Right, cropPx.Bottom);                         // SE
            DrawHandle(g, cropPx.Left + cropPx.Width / 2, cropPx.Bottom);       // S
            DrawHandle(g, cropPx.Left, cropPx.Bottom);                          // SW
            DrawHandle(g, cropPx.Left, cropPx.Top + cropPx.Height / 2);         // W

            // 4. Deskew Angle & Confidence Badge
            if (_deskewResult != null && Math.Abs(_deskewResult.Skew) > 0.05f)
            {
                string badge = string.Format(CultureInfo.InvariantCulture, "Skew: {0:0.1}\u00b0 ({1:P0})",
                                             _deskewResult.Skew, _deskewResult.Confidence);
                using (Font bf = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                {
                    SizeF bSize = g.MeasureString(badge, bf);
                    RectangleF bRect = new RectangleF(cropPx.Left + 6, cropPx.Top + 6, bSize.Width + 12, bSize.Height + 6);
                    using (SolidBrush bBg = new SolidBrush(Color.FromArgb(210, 20, 24, 32)))
                    using (Pen bPen = new Pen(Color.FromArgb(0, 180, 255), 1.2f))
                    using (SolidBrush bText = new SolidBrush(Color.FromArgb(0, 210, 255)))
                    {
                        g.FillRectangle(bBg, bRect);
                        g.DrawRectangle(bPen, bRect.X, bRect.Y, bRect.Width, bRect.Height);
                        g.DrawString(badge, bf, bText, bRect.X + 6, bRect.Y + 3);
                    }
                }
            }

            // 5. Scan Sweep Glow Line
            if (_isScanning)
            {
                float sweepY = imgRect.Top + imgRect.Height * _sweepProgress;
                using (Pen laserPen = new Pen(Color.FromArgb(230, 0, 235, 255), 3f))
                using (LinearGradientBrush glowBrush = new LinearGradientBrush(
                    new PointF(imgRect.Left, sweepY - 12), new PointF(imgRect.Left, sweepY + 12),
                    Color.FromArgb(0, 0, 200, 255), Color.FromArgb(70, 0, 220, 255)))
                {
                    g.FillRectangle(glowBrush, imgRect.Left, sweepY - 12, imgRect.Width, 24);
                    g.DrawLine(laserPen, imgRect.Left, sweepY, imgRect.Right, sweepY);
                }
            }
        }

        void DrawHandle(Graphics g, int x, int y)
        {
            Rectangle r = new Rectangle(x - HandleSize / 2, y - HandleSize / 2, HandleSize, HandleSize);
            using (SolidBrush b = new SolidBrush(Color.White))
            using (Pen p = new Pen(Color.FromArgb(20, 115, 230), 2f))
            {
                g.FillRectangle(b, r);
                g.DrawRectangle(p, r);
            }
        }

        DragHandle HitTest(Point pt)
        {
            Rectangle imgRect = GetImageDisplayRect();
            Rectangle cropPx = CropToPixelRect(imgRect);

            int tol = HandleSize + 4;
            bool hitX(int x) => Math.Abs(pt.X - x) <= tol;
            bool hitY(int y) => Math.Abs(pt.Y - y) <= tol;

            int midX = cropPx.Left + cropPx.Width / 2;
            int midY = cropPx.Top + cropPx.Height / 2;

            if (hitX(cropPx.Left) && hitY(cropPx.Top)) return DragHandle.Nw;
            if (hitX(cropPx.Right) && hitY(cropPx.Top)) return DragHandle.Ne;
            if (hitX(cropPx.Right) && hitY(cropPx.Bottom)) return DragHandle.Se;
            if (hitX(cropPx.Left) && hitY(cropPx.Bottom)) return DragHandle.Sw;

            if (hitX(midX) && hitY(cropPx.Top)) return DragHandle.N;
            if (hitX(cropPx.Right) && hitY(midY)) return DragHandle.E;
            if (hitX(midX) && hitY(cropPx.Bottom)) return DragHandle.S;
            if (hitX(cropPx.Left) && hitY(midY)) return DragHandle.W;

            if (cropPx.Contains(pt)) return DragHandle.Body;
            if (imgRect.Contains(pt)) return DragHandle.DrawNew;

            return DragHandle.None;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();

            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Right && _zoom > 1.05f))
            {
                _isPanning = true;
                _panStartMouse = e.Location;
                _panStartOffset = _panOffset;
                Cursor = Cursors.SizeAll;
                return;
            }

            if (e.Button != MouseButtons.Left) return;

            Rectangle imgRect = GetImageDisplayRect();
            _activeHandle = HitTest(e.Location);
            _dragStartMouse = e.Location;
            _dragStartCrop = CropToPixelRect(imgRect);

            if (_activeHandle == DragHandle.DrawNew)
            {
                // Start drawing a new crop box
                _cropNorm = PixelToCropNorm(new Rectangle(e.X, e.Y, 4, 4), imgRect);
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_isPanning)
            {
                _panOffset = new PointF(_panStartOffset.X + (e.X - _panStartMouse.X),
                                       _panStartOffset.Y + (e.Y - _panStartMouse.Y));
                Invalidate();
                return;
            }

            if (_activeHandle == DragHandle.None)
            {
                // Update cursor on hover
                DragHandle h = HitTest(e.Location);
                switch (h)
                {
                    case DragHandle.Nw: case DragHandle.Se: Cursor = Cursors.SizeNWSE; break;
                    case DragHandle.Ne: case DragHandle.Sw: Cursor = Cursors.SizeNESW; break;
                    case DragHandle.N: case DragHandle.S: Cursor = Cursors.SizeNS; break;
                    case DragHandle.E: case DragHandle.W: Cursor = Cursors.SizeWE; break;
                    case DragHandle.Body: Cursor = Cursors.SizeAll; break;
                    default: Cursor = Cursors.Cross; break;
                }
                return;
            }

            Rectangle imgRect = GetImageDisplayRect();
            int dx = e.X - _dragStartMouse.X;
            int dy = e.Y - _dragStartMouse.Y;

            Rectangle newPx = _dragStartCrop;

            switch (_activeHandle)
            {
                case DragHandle.Body:
                    newPx.X += dx;
                    newPx.Y += dy;
                    break;
                case DragHandle.Nw:
                    newPx.X += dx; newPx.Width -= dx;
                    newPx.Y += dy; newPx.Height -= dy;
                    break;
                case DragHandle.N:
                    newPx.Y += dy; newPx.Height -= dy;
                    break;
                case DragHandle.Ne:
                    newPx.Width += dx;
                    newPx.Y += dy; newPx.Height -= dy;
                    break;
                case DragHandle.E:
                    newPx.Width += dx;
                    break;
                case DragHandle.Se:
                    newPx.Width += dx;
                    newPx.Height += dy;
                    break;
                case DragHandle.S:
                    newPx.Height += dy;
                    break;
                case DragHandle.Sw:
                    newPx.X += dx; newPx.Width -= dx;
                    newPx.Height += dy;
                    break;
                case DragHandle.W:
                    newPx.X += dx; newPx.Width -= dx;
                    break;
                case DragHandle.DrawNew:
                    int x0 = Math.Min(_dragStartMouse.X, e.X);
                    int y0 = Math.Min(_dragStartMouse.Y, e.Y);
                    int w0 = Math.Abs(e.X - _dragStartMouse.X);
                    int h0 = Math.Abs(e.Y - _dragStartMouse.Y);
                    newPx = new Rectangle(x0, y0, w0, h0);
                    break;
            }

            // Normalise positive dimensions
            if (newPx.Width < 10) newPx.Width = 10;
            if (newPx.Height < 10) newPx.Height = 10;

            CropNorm = PixelToCropNorm(newPx, imgRect);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _activeHandle = DragHandle.None;
            _isPanning = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.15f : 0.87f;
            Zoom = _zoom * factor;
        }
    }
}
