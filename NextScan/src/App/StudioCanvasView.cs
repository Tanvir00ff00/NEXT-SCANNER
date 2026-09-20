// =============================================================================
// NextScan Studio - Canvas and page filmstrip (ground-up rebuild)
// Plan ref: MASTER_PLAN section 13.3 (canvas requirements).
//
// Layout break from the previous shell: the filmstrip is a VERTICAL strip on the
// right rather than a horizontal bar along the bottom. Scanned pages are portrait
// and a vertical strip shows five of them in the space a horizontal one used for
// two, and it leaves the full window height for the page being worked on.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    /// <summary>Which crop handle the pointer is over.</summary>
    public enum CropGrip { None, Move, NW, N, NE, E, SE, S, SW, W, New }

    public class StudioCanvasView : Control
    {
        // ---- content ---------------------------------------------------------
        RawImage _image;
        Bitmap _display;
        Bitmap _original;          // pre-adjustment copy, for the before/after split

        // ---- view state ------------------------------------------------------
        double _zoom = 1.0;
        bool _fitToWindow = true;
        PointF _pan = PointF.Empty;

        // ---- crop ------------------------------------------------------------
        RectangleF _cropNorm = new RectangleF(0f, 0f, 1f, 1f);
        CropGrip _grip = CropGrip.None;
        CropGrip _hotGrip = CropGrip.None;
        Point _dragStart;
        RectangleF _dragOriginCrop;
        bool _dragging;

        // ---- panning ---------------------------------------------------------
        bool _panning;
        bool _spaceHeld;
        Point _panStart;
        PointF _panOrigin;

        // ---- overlays --------------------------------------------------------
        readonly Anim _sweep = new Anim(0);
        readonly Anim _fade = new Anim(0);
        bool _scanning;

        public bool ShowThirds;
        public bool ShowSplit;
        public double SplitAt = 0.5;

        /// <summary>
        /// Vertical space at the top and bottom that the fit calculation must
        /// leave clear.
        ///
        /// The floating bars are SIBLINGS of the canvas, not children, so their
        /// background is the form ground rather than the scanned image - a bar
        /// sitting over the page paints a hard dark rectangle across it. Reserving
        /// the bands keeps the page out from under them, which fixes the artefact
        /// at its source instead of trying to fake transparency GDI+ cannot do.
        /// </summary>
        public int ReservedTop;
        public int ReservedBottom;

        /// <summary>
        /// True when the page on screen is a blank stand-in for the platen rather
        /// than something the scanner produced.
        ///
        /// It exists so the crop tools are usable the moment the app opens: without
        /// it every job started with a throwaway preview scan just to have
        /// something to draw a rectangle on.
        /// </summary>
        public bool IsPlaceholder { get; private set; }

        /// <summary>
        /// True when the canvas is showing the whole platen with the last scan
        /// composited into the place it came from, rather than the scan alone.
        ///
        /// Keeps the surrounding glass visible so the selection can be enlarged and
        /// re-scanned without going back for another preview. Export must NOT use
        /// this image - it is a viewing aid at display resolution.
        /// </summary>
        public bool IsBedView { get; private set; }

        public PointF[] DetectedPolygon;
        public double DetectedAngle;
        public double DetectedConfidence = -1;

        public event EventHandler CropChanged;

        /// <summary>Raised when the operator reshapes one detected region.</summary>
        public event EventHandler<RegionEventArgs> RegionEdited;

        /// <summary>Raised on a right click over a detected region.</summary>
        public event EventHandler<RegionEventArgs> RegionMenu;

        /// <summary>Raised when the selected region changes, including to none.</summary>
        public event EventHandler SelectionChanged;

        /// <summary>
        /// Raised by a plain click on the page away from every region. The
        /// operator may be pointing at a document the detector missed, so this
        /// carries where they pointed rather than simply meaning "nothing".
        /// </summary>
        public event EventHandler<RegionEventArgs> EmptyClicked;

        int _selected = -1;
        CropGrip _regionGrip = CropGrip.None;
        PointF[] _regionOrigin;
        bool _movedWhileDown;

        /// <summary>Which detected region is being worked on, or -1 for none.</summary>
        public int SelectedRegion
        {
            get { return _selected; }
            set
            {
                int clamped = (DetectedRegions == null || value < 0 || value >= DetectedRegions.Count) ? -1 : value;
                if (clamped == _selected) return;
                _selected = clamped;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        public StudioCanvasView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Ground;
            // Needed for the space-bar pan modifier to reach us at all.
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            Animator.Attach(this, _sweep, _fade);
        }

        // ---------------------------------------------------------------- content
        public RawImage Image { get { return _image; } }

        /// <summary>Shows a blank sheet. Cleared by the next real page.</summary>
        public void SetPlaceholderImage(RawImage img)
        {
            SetImage(img);
            IsPlaceholder = true;
            Invalidate();
        }

        /// <summary>Shows the platen with the last scan composited into it.</summary>
        public void SetBedViewImage(RawImage img)
        {
            SetImage(img);
            IsBedView = true;
            Invalidate();
        }

        public void SetImage(RawImage img)
        {
            IsPlaceholder = false;
            IsBedView = false;
            if (_display != null) { _display.Dispose(); _display = null; }
            if (_original != null) { _original.Dispose(); _original = null; }

            _image = img;
            if (img != null && img.IsValid)
            {
                _display = img.ToBitmap();
                _original = (Bitmap)_display.Clone();
            }

            _fitToWindow = true;
            _pan = PointF.Empty;
            _fade.Snap(0);
            _fade.Target = 1;
            Animator.Kick();
            Invalidate();
        }

        /// <summary>Swaps only the adjusted bitmap, keeping the "before" copy intact.</summary>
        public void SetAdjusted(Bitmap adjusted)
        {
            if (adjusted == null) return;
            if (_display != null && !ReferenceEquals(_display, _original)) _display.Dispose();
            _display = adjusted;
            Invalidate();
        }

        public Bitmap OriginalBitmap { get { return _original; } }
        public Bitmap DisplayBitmap { get { return _display; } }

        public bool Scanning
        {
            get { return _scanning; }
            set
            {
                _scanning = value;
                _sweep.Rate = 0.035;
                _sweep.Snap(0);
                _sweep.Target = value ? 1 : 0;
                Animator.Kick();
                Invalidate();
            }
        }

        public string ActivePaperLabel;

        public RectangleF CropNorm
        {
            get { return _cropNorm; }
            set { _cropNorm = StudioSettings.ClampCrop(value); Invalidate(); }
        }

        /// <summary>Rotates the active crop rectangle to align with 90/180/270 deg image rotations.</summary>
        public void RotateCrop(RotateFlipType type)
        {
            RectangleF old = _cropNorm;
            bool full = old.X <= 0.002f && old.Y <= 0.002f && old.Width >= 0.998f && old.Height >= 0.998f;
            if (full) return;

            RectangleF rotated = old;
            switch (type)
            {
                case RotateFlipType.Rotate90FlipNone:
                    rotated = new RectangleF(1f - old.Bottom, old.X, old.Height, old.Width);
                    break;
                case RotateFlipType.Rotate270FlipNone:
                    rotated = new RectangleF(old.Y, 1f - old.Right, old.Height, old.Width);
                    break;
                case RotateFlipType.Rotate180FlipNone:
                    rotated = new RectangleF(1f - old.Right, 1f - old.Bottom, old.Width, old.Height);
                    break;
            }
            _cropNorm = StudioSettings.ClampCrop(rotated);
            Invalidate();
        }

        // ---------------------------------------------------------------- view maths
        /// <summary>Rectangle the page occupies on screen, in control coordinates.</summary>
        public Rectangle PageRect
        {
            get
            {
                if (_display == null) return Rectangle.Empty;

                const int margin = 8;

                int usableTop = margin + ReservedTop;
                int usableBottom = margin + ReservedBottom;
                int usableH = Math.Max(40, Height - usableTop - usableBottom);
                int usableW = Math.Max(40, Width - margin * 2);

                double scale;
                if (_fitToWindow)
                {
                    double sx = usableW / (double)_display.Width;
                    double sy = usableH / (double)_display.Height;
                    scale = Math.Max(0.02, Math.Min(sx, sy));
                }
                else scale = _zoom;

                int w = Math.Max(1, (int)Math.Round(_display.Width * scale));
                int h = Math.Max(1, (int)Math.Round(_display.Height * scale));
                int x = (Width - w) / 2 + (int)_pan.X;
                // Centre inside the usable band, but never above it: when the page
                // is taller than the band the centring term goes negative and the
                // top of the page would slide under the floating HUD.
                int y = usableTop + Math.Max(0, (usableH - h) / 2) + (int)_pan.Y;
                return new Rectangle(x, y, w, h);
            }
        }

        public double EffectiveZoom
        {
            get
            {
                if (_display == null || _display.Width == 0) return 1;
                return PageRect.Width / (double)_display.Width;
            }
        }

        public void ZoomFit() { _fitToWindow = true; _pan = PointF.Empty; Invalidate(); }

        public void ZoomTo(double z)
        {
            _fitToWindow = false;
            _zoom = Math.Max(0.1, Math.Min(8.0, z));
            ClampPan();
            Invalidate();
        }

        public void ZoomBy(double factor)
        {
            double current = EffectiveZoom;
            ZoomTo(current * factor);
        }

        RectangleF CropPixels
        {
            get
            {
                Rectangle p = PageRect;
                return new RectangleF(
                    p.X + _cropNorm.X * p.Width,
                    p.Y + _cropNorm.Y * p.Height,
                    _cropNorm.Width * p.Width,
                    _cropNorm.Height * p.Height);
            }
        }

        // ---------------------------------------------------------------- hit testing
        /// <summary>The topmost detected region under a point, or -1.</summary>
        public int RegionAt(Point pt)
        {
            if (DetectedRegions == null) return -1;
            Rectangle page = PageRect;
            if (page.Width <= 0 || page.Height <= 0) return -1;

            // Last drawn is topmost, so search backwards: where two regions
            // overlap the one the operator can see is the one they mean.
            for (int i = DetectedRegions.Count - 1; i >= 0; i--)
            {
                PointF[] norm = DetectedRegions[i];
                if (norm == null || norm.Length < 3) continue;
                if (Contains(norm, page, pt)) return i;
            }
            return -1;
        }

        static bool Contains(PointF[] norm, Rectangle page, Point pt)
        {
            bool inside = false;
            for (int i = 0, j = norm.Length - 1; i < norm.Length; j = i++)
            {
                float xi = page.X + norm[i].X * page.Width, yi = page.Y + norm[i].Y * page.Height;
                float xj = page.X + norm[j].X * page.Width, yj = page.Y + norm[j].Y * page.Height;
                if ((yi > pt.Y) != (yj > pt.Y) &&
                    pt.X < (xj - xi) * (pt.Y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>The upright bounds of a region, in control pixels.</summary>
        RectangleF RegionBounds(int index)
        {
            PointF[] norm = DetectedRegions[index];
            Rectangle page = PageRect;
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            foreach (PointF n in norm)
            {
                float x = page.X + n.X * page.Width, y = page.Y + n.Y * page.Height;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        /// <summary>
        /// The eight points that shape a four-cornered region: its own corners,
        /// and the middle of each of its own sides.
        ///
        /// Its own, not those of an upright box round it. A card lying at twenty
        /// degrees has corners twenty degrees over, and a handle that sits
        /// anywhere else is a handle for something the operator cannot see.
        /// </summary>
        PointF[] RegionPoints(int index)
        {
            PointF[] norm = DetectedRegions[index];
            if (norm == null || norm.Length != 4) return null;

            Rectangle page = PageRect;
            PointF[] corner = new PointF[4];
            for (int i = 0; i < 4; i++)
                corner[i] = new PointF(page.X + norm[i].X * page.Width, page.Y + norm[i].Y * page.Height);

            return new PointF[]
            {
                corner[0], Middle(corner[0], corner[1]),
                corner[1], Middle(corner[1], corner[2]),
                corner[2], Middle(corner[2], corner[3]),
                corner[3], Middle(corner[3], corner[0])
            };
        }

        static PointF Middle(PointF a, PointF b)
        {
            return new PointF((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        }

        /// <summary>Which handle of the selected region is under a point.</summary>
        CropGrip RegionGripAt(Point pt)
        {
            if (_selected < 0 || DetectedRegions == null || _selected >= DetectedRegions.Count) return CropGrip.None;

            const float Reach = 7f;

            // A four-cornered region is dragged corner by corner, so the handles
            // are its corners. The order here is the order the grips are named
            // in: NW is the first vertex, N the side leaving it, and so on round.
            PointF[] handles = RegionPoints(_selected);
            if (handles != null)
            {
                CropGrip[] order =
                {
                    CropGrip.NW, CropGrip.N, CropGrip.NE, CropGrip.E,
                    CropGrip.SE, CropGrip.S, CropGrip.SW, CropGrip.W
                };
                for (int i = 0; i < handles.Length; i++)
                    if (Math.Abs(pt.X - handles[i].X) <= Reach && Math.Abs(pt.Y - handles[i].Y) <= Reach)
                        return order[i];
                return CropGrip.None;
            }

            RectangleF b = RegionBounds(_selected);
            bool left = Math.Abs(pt.X - b.Left) <= Reach, right = Math.Abs(pt.X - b.Right) <= Reach;
            bool top = Math.Abs(pt.Y - b.Top) <= Reach, bottom = Math.Abs(pt.Y - b.Bottom) <= Reach;
            bool inX = pt.X > b.Left - Reach && pt.X < b.Right + Reach;
            bool inY = pt.Y > b.Top - Reach && pt.Y < b.Bottom + Reach;

            if (left && top) return CropGrip.NW;
            if (right && top) return CropGrip.NE;
            if (left && bottom) return CropGrip.SW;
            if (right && bottom) return CropGrip.SE;
            if (top && inX) return CropGrip.N;
            if (bottom && inX) return CropGrip.S;
            if (left && inY) return CropGrip.W;
            if (right && inY) return CropGrip.E;
            return CropGrip.None;
        }

        CropGrip GripAt(Point pt)
        {
            if (_display == null) return CropGrip.None;

            RectangleF c = CropPixels;
            const int t = 9;

            bool left = Math.Abs(pt.X - c.Left) <= t;
            bool right = Math.Abs(pt.X - c.Right) <= t;
            bool top = Math.Abs(pt.Y - c.Top) <= t;
            bool bottom = Math.Abs(pt.Y - c.Bottom) <= t;
            bool inX = pt.X >= c.Left - t && pt.X <= c.Right + t;
            bool inY = pt.Y >= c.Top - t && pt.Y <= c.Bottom + t;

            if (left && top) return CropGrip.NW;
            if (right && top) return CropGrip.NE;
            if (left && bottom) return CropGrip.SW;
            if (right && bottom) return CropGrip.SE;
            if (top && inX) return CropGrip.N;
            if (bottom && inX) return CropGrip.S;
            if (left && inY) return CropGrip.W;
            if (right && inY) return CropGrip.E;
            if (c.Contains(pt)) return CropGrip.Move;
            return CropGrip.New;
        }

        static Cursor CursorFor(CropGrip g)
        {
            switch (g)
            {
                case CropGrip.NW:
                case CropGrip.SE: return Cursors.SizeNWSE;
                case CropGrip.NE:
                case CropGrip.SW: return Cursors.SizeNESW;
                case CropGrip.N:
                case CropGrip.S: return Cursors.SizeNS;
                case CropGrip.E:
                case CropGrip.W: return Cursors.SizeWE;
                case CropGrip.Move: return Cursors.SizeAll;
                default: return Cursors.Cross;
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (keyData == Keys.Space) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                Cursor = Cursors.Hand;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                _spaceHeld = false;
                if (!_panning) Cursor = Cursors.Default;
            }
            base.OnKeyUp(e);
        }

        void BeginPan(Point at)
        {
            _panning = true;
            _panStart = at;
            _panOrigin = _pan;
            // Leaving fit mode is what makes panning meaningful: a fitted page has
            // nowhere to go.
            if (_fitToWindow) { _zoom = EffectiveZoom; _fitToWindow = false; }
            Cursor = Cursors.Hand;
            Capture = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (_display == null) return;

            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spaceHeld))
            {
                BeginPan(e.Location);
                Focus();
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                int hit = RegionAt(e.Location);
                if (hit >= 0)
                {
                    SelectedRegion = hit;
                    Focus();
                    if (RegionMenu != null)
                        RegionMenu(this, new RegionEventArgs(hit, e.Location));
                }
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                _movedWhileDown = false;
                _dragStart = e.Location;
                Focus();

                // A handle of the region already chosen comes first: its corners
                // sit on top of whatever is underneath them.
                CropGrip onRegion = RegionGripAt(e.Location);
                if (onRegion != CropGrip.None)
                {
                    _regionGrip = onRegion;
                    _regionOrigin = (PointF[])DetectedRegions[_selected].Clone();
                    return;
                }

                int hit = RegionAt(e.Location);
                if (hit >= 0)
                {
                    SelectedRegion = hit;
                    _regionGrip = CropGrip.Move;
                    _regionOrigin = (PointF[])DetectedRegions[hit].Clone();
                    return;
                }

                _grip = GripAt(e.Location);
                _dragOriginCrop = _cropNorm;
                _dragging = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_display == null) return;

            // Same lost-button guard the splitters needed.
            if (_panning && Control.MouseButtons == MouseButtons.None)
            {
                _panning = false;
                Capture = false;
                Cursor = _spaceHeld ? Cursors.Hand : Cursors.Default;
            }

            if (_panning)
            {
                _pan = new PointF(_panOrigin.X + (e.X - _panStart.X),
                                  _panOrigin.Y + (e.Y - _panStart.Y));
                ClampPan();
                Invalidate();
                return;
            }

            if (_regionGrip != CropGrip.None)
            {
                if ((Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
                { EndRegionDrag(); return; }
                DragRegion(e.Location);
                return;
            }

            if (_dragging && (Control.MouseButtons & MouseButtons.Left) != MouseButtons.Left)
            {
                _dragging = false;
                _grip = CropGrip.None;
                if (CropChanged != null) CropChanged(this, EventArgs.Empty);
            }

            if (!_dragging)
            {
                CropGrip onRegion = RegionGripAt(e.Location);
                if (onRegion != CropGrip.None)
                {
                    if (onRegion != _hotGrip) { _hotGrip = onRegion; Cursor = CursorFor(onRegion); Invalidate(); }
                    return;
                }
                if (RegionAt(e.Location) >= 0)
                {
                    if (_hotGrip != CropGrip.Move) { _hotGrip = CropGrip.Move; Cursor = Cursors.SizeAll; Invalidate(); }
                    return;
                }

                CropGrip g = GripAt(e.Location);
                if (g != _hotGrip) { _hotGrip = g; Cursor = CursorFor(g); Invalidate(); }
                return;
            }

            if (Math.Abs(e.X - _dragStart.X) > 2 || Math.Abs(e.Y - _dragStart.Y) > 2) _movedWhileDown = true;

            Rectangle p = PageRect;
            if (p.Width <= 0 || p.Height <= 0) return;

            float dx = (e.X - _dragStart.X) / (float)p.Width;
            float dy = (e.Y - _dragStart.Y) / (float)p.Height;

            RectangleF r = _dragOriginCrop;
            switch (_grip)
            {
                case CropGrip.Move: r.X += dx; r.Y += dy; break;
                case CropGrip.NW: r.X += dx; r.Y += dy; r.Width -= dx; r.Height -= dy; break;
                case CropGrip.NE: r.Y += dy; r.Width += dx; r.Height -= dy; break;
                case CropGrip.SW: r.X += dx; r.Width -= dx; r.Height += dy; break;
                case CropGrip.SE: r.Width += dx; r.Height += dy; break;
                case CropGrip.N: r.Y += dy; r.Height -= dy; break;
                case CropGrip.S: r.Height += dy; break;
                case CropGrip.W: r.X += dx; r.Width -= dx; break;
                case CropGrip.E: r.Width += dx; break;

                case CropGrip.New:
                {
                    // Rubber-band a fresh selection from the press point.
                    float x0 = (_dragStart.X - p.X) / (float)p.Width;
                    float y0 = (_dragStart.Y - p.Y) / (float)p.Height;
                    float x1 = (e.X - p.X) / (float)p.Width;
                    float y1 = (e.Y - p.Y) / (float)p.Height;
                    r = new RectangleF(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));
                    break;
                }
            }

            _cropNorm = StudioSettings.ClampCrop(r);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);

            if (_panning)
            {
                _panning = false;
                Capture = false;
                Cursor = _spaceHeld ? Cursors.Hand : Cursors.Default;
                return;
            }

            if (_regionGrip != CropGrip.None) { EndRegionDrag(); return; }

            if (!_dragging) return;
            _dragging = false;
            _grip = CropGrip.None;

            // A click is not a drag. Pressing on bare glass used to rubber-band
            // a new selection of zero size, which counted as the operator
            // choosing a crop by hand and threw away every detected region --
            // so a stray click on the background wiped the whole preview.
            if (!_movedWhileDown)
            {
                _cropNorm = _dragOriginCrop;
                SelectedRegion = -1;
                Invalidate();

                Rectangle page = PageRect;
                if (EmptyClicked != null && page.Width > 0 && page.Height > 0 && page.Contains(e.Location))
                    EmptyClicked(this, new RegionEventArgs(-1, e.Location,
                        new PointF((e.X - page.X) / (float)page.Width,
                                   (e.Y - page.Y) / (float)page.Height)));
                return;
            }

            if (CropChanged != null) CropChanged(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reshapes the chosen region while a handle is held.
        ///
        /// The whole outline is moved or scaled together, so a region measured
        /// at an angle keeps its angle: the operator is adjusting where the item
        /// is, not redrawing it square to the screen.
        /// </summary>
        void DragRegion(Point at)
        {
            if (_selected < 0 || _regionOrigin == null) return;
            Rectangle page = PageRect;
            if (page.Width <= 0 || page.Height <= 0) return;

            float dx = (at.X - _dragStart.X) / (float)page.Width;
            float dy = (at.Y - _dragStart.Y) / (float)page.Height;
            if (Math.Abs(at.X - _dragStart.X) > 2 || Math.Abs(at.Y - _dragStart.Y) > 2) _movedWhileDown = true;

            // Four corners are moved one at a time, which lets the operator pull
            // the shape into a quadrilateral rather than only a rectangle. A
            // document photographed or laid at an angle is not a rectangle on
            // the glass, and a crop that can only be a rectangle either loses a
            // corner of it or takes in the platen beside it.
            if (_regionOrigin.Length == 4 && _regionGrip != CropGrip.Move)
            {
                int first = -1, second = -1;
                switch (_regionGrip)
                {
                    case CropGrip.NW: first = 0; break;
                    case CropGrip.NE: first = 1; break;
                    case CropGrip.SE: first = 2; break;
                    case CropGrip.SW: first = 3; break;
                    case CropGrip.N: first = 0; second = 1; break;
                    case CropGrip.E: first = 1; second = 2; break;
                    case CropGrip.S: first = 2; second = 3; break;
                    case CropGrip.W: first = 3; second = 0; break;
                }
                if (first < 0) return;

                PointF[] quad = new PointF[4];
                for (int i = 0; i < 4; i++)
                {
                    float x = _regionOrigin[i].X, y = _regionOrigin[i].Y;
                    if (i == first || i == second) { x += dx; y += dy; }
                    quad[i] = new PointF(Math.Max(0f, Math.Min(1f, x)), Math.Max(0f, Math.Min(1f, y)));
                }
                DetectedRegions[_selected] = quad;
                Invalidate();
                return;
            }

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            foreach (PointF n in _regionOrigin)
            {
                left = Math.Min(left, n.X); right = Math.Max(right, n.X);
                top = Math.Min(top, n.Y); bottom = Math.Max(bottom, n.Y);
            }
            float width = right - left, height = bottom - top;
            if (width <= 0.0005f || height <= 0.0005f) return;

            float moveX = 0, moveY = 0, scaleX = 1, scaleY = 1, anchorX = left, anchorY = top;
            switch (_regionGrip)
            {
                case CropGrip.Move: moveX = dx; moveY = dy; break;
                case CropGrip.W: anchorX = right; scaleX = (width - dx) / width; break;
                case CropGrip.E: anchorX = left; scaleX = (width + dx) / width; break;
                case CropGrip.N: anchorY = bottom; scaleY = (height - dy) / height; break;
                case CropGrip.S: anchorY = top; scaleY = (height + dy) / height; break;
                case CropGrip.NW: anchorX = right; anchorY = bottom; scaleX = (width - dx) / width; scaleY = (height - dy) / height; break;
                case CropGrip.NE: anchorX = left; anchorY = bottom; scaleX = (width + dx) / width; scaleY = (height - dy) / height; break;
                case CropGrip.SW: anchorX = right; anchorY = top; scaleX = (width - dx) / width; scaleY = (height + dy) / height; break;
                case CropGrip.SE: anchorX = left; anchorY = top; scaleX = (width + dx) / width; scaleY = (height + dy) / height; break;
                default: return;
            }

            // Never let a region be turned inside out by dragging a side past
            // its opposite: the outline would fold and the crop with it.
            if (scaleX < 0.05f) scaleX = 0.05f;
            if (scaleY < 0.05f) scaleY = 0.05f;

            PointF[] shaped = new PointF[_regionOrigin.Length];
            for (int i = 0; i < shaped.Length; i++)
            {
                float x = anchorX + (_regionOrigin[i].X - anchorX) * scaleX + moveX;
                float y = anchorY + (_regionOrigin[i].Y - anchorY) * scaleY + moveY;
                shaped[i] = new PointF(Math.Max(0f, Math.Min(1f, x)), Math.Max(0f, Math.Min(1f, y)));
            }
            DetectedRegions[_selected] = shaped;
            Invalidate();
        }

        void EndRegionDrag()
        {
            bool changed = _movedWhileDown;
            int index = _selected;
            _regionGrip = CropGrip.None;
            _regionOrigin = null;
            _movedWhileDown = false;
            Capture = false;
            if (changed && index >= 0 && RegionEdited != null)
                RegionEdited(this, new RegionEventArgs(index, Point.Empty));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_display == null) return;

            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
            {
                ZoomBy(e.Delta > 0 ? 1.12 : 1 / 1.12);
                return;
            }

            // Plain wheel scrolls the page, which is what people expect once an
            // image is larger than the viewport. Ctrl is the zoom modifier.
            if (_fitToWindow) { _zoom = EffectiveZoom; _fitToWindow = false; }

            int step = e.Delta / 2;
            if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                _pan = new PointF(_pan.X + step, _pan.Y);
            else
                _pan = new PointF(_pan.X, _pan.Y + step);

            ClampPan();
            Invalidate();
        }

        /// <summary>
        /// Keeps at least a corner of the page on screen. Without this a firm
        /// drag throws the image out of the viewport and there is no way back
        /// except Fit.
        /// </summary>
        void ClampPan()
        {
            if (_display == null) return;

            double scale = _fitToWindow ? EffectiveZoom : _zoom;
            int w = Math.Max(1, (int)Math.Round(_display.Width * scale));
            int h = Math.Max(1, (int)Math.Round(_display.Height * scale));

            float maxX = Math.Max(0, (w - Width) / 2f) + 60;
            float maxY = Math.Max(0, (h - Height) / 2f) + 60;

            _pan = new PointF(
                Math.Max(-maxX, Math.Min(maxX, _pan.X)),
                Math.Max(-maxY, Math.Min(maxY, _pan.Y)));
        }


        protected override void WndProc(ref Message m)
        {
            // Let the form resize from its border even though this control covers
            // the client area - see StudioSplitter.cs for the full explanation.
            if (m.Msg == Frame.WM_NCHITTEST)
            {
                Point screen = new Point(m.LParam.ToInt32());
                if (Frame.NearFormEdge(this, screen)) { m.Result = (IntPtr)Frame.HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- painting
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Ground);

            if (_display == null) { PaintEmpty(g); return; }

            Theme.Smooth(g);
            Rectangle page = PageRect;

            // Elevated sheet: soft shadow under the page so it reads as paper
            // sitting on the 18% grey proofing ground rather than a pasted rect.
            Theme.Shadow(g, page, 2, 5, 52);

            g.InterpolationMode = (EffectiveZoom >= 1.0)
                ? InterpolationMode.NearestNeighbor   // honest pixels when inspecting
                : InterpolationMode.HighQualityBicubic;

            int alpha = (int)Math.Round(255 * Math.Max(0.0, Math.Min(1.0, _fade.Eased)));
            if (IsPlaceholder)
            {
                using (SolidBrush paper = new SolidBrush(Color.White)) g.FillRectangle(paper, page);
            }
            else if (alpha >= 250)
            {
                g.DrawImage(_display, page);
            }
            else
            {
                using (ImageAttributes ia = new ImageAttributes())
                {
                    ColorMatrix cm = new ColorMatrix();
                    cm.Matrix33 = alpha / 255f;
                    ia.SetColorMatrix(cm);
                    g.DrawImage(_display, page, 0, 0, _display.Width, _display.Height, GraphicsUnit.Pixel, ia);
                }
            }

            if (ShowSplit && _original != null && !IsPlaceholder) PaintSplit(g, page);

            if (IsPlaceholder) PaintPlaceholderHint(g, page);

            PaintCrop(g, page);
            PaintDetectedRegions(g, page);
            if (DetectedPolygon != null && DetectedPolygon.Length >= 3) PaintDetection(g, page);
            if (_scanning) PaintSweep(g, page);
        }

        /// <summary>
        /// Drawn as an overlay rather than baked into the bitmap, so the sheet
        /// stays clean white if it is ever exported and the text stays crisp at
        /// any zoom.
        /// </summary>
        void PaintPlaceholderHint(Graphics g, Rectangle page)
        {
            using (Pen p = new Pen(Theme.Mix(Theme.Line, Theme.Accent, 0.35), 1.4f))
            {
                p.DashStyle = DashStyle.Dash;
                p.DashPattern = new float[] { 6f, 5f };
                g.DrawRectangle(p, page.X, page.Y, page.Width - 1, page.Height - 1);
            }

            if (page.Height < 120 || page.Width < 160) return;

            int centreX = page.X + page.Width / 2;
            int centreY = page.Y + page.Height / 2;
            // Preview guidance lives inside the placeholder only. It never
            // overlays an acquired image or becomes part of exported pixels.
            using (Pen pen = new Pen(Theme.Accent, 2f))
            {
                Rectangle icon = new Rectangle(centreX - 18, centreY - 72, 36, 44);
                using (GraphicsPath path = Theme.Round(icon, 5)) g.DrawPath(pen, path);
                g.DrawLine(pen, centreX - 9, centreY - 57, centreX + 9, centreY - 57);
                g.DrawLine(pen, centreX - 9, centreY - 49, centreX + 5, centreY - 49);
            }
            using (Font title = Theme.UiSemi(12f))
                TextRenderer.DrawText(g, "Ready to preview", title,
                    new Rectangle(page.X + 14, centreY - 10, page.Width - 28, 30),
                    Color.FromArgb(40, 40, 40), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            using (Font hint = Theme.Ui(9f))
                TextRenderer.DrawText(g, "Place originals on the glass.\nPreview to check the crop.", hint,
                    new Rectangle(page.X + 20, centreY + 28, Math.Max(1, page.Width - 40), 60),
                    Color.FromArgb(92, 92, 92), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }

        void PaintEmpty(Graphics g)
        {
            Theme.Smooth(g);

            string title = "No page yet";
            string hint = "Press  F5  to preview     •     F7  to scan";

            using (Font f1 = Theme.UiSemi(13f))
                TextRenderer.DrawText(g, title, f1, new Rectangle(0, Height / 2 - 30, Width, 28),
                    Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            using (Font f2 = Theme.Ui(9f))
                TextRenderer.DrawText(g, hint, f2, new Rectangle(0, Height / 2 + 2, Width, 22),
                    Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void PaintSplit(Graphics g, Rectangle page)
        {
            int splitX = page.X + (int)Math.Round(page.Width * SplitAt);
            Rectangle leftHalf = new Rectangle(page.X, page.Y, Math.Max(0, splitX - page.X), page.Height);
            if (leftHalf.Width <= 0) return;

            // "Before" is the unadjusted copy, drawn over the left half only.
            g.SetClip(leftHalf);
            g.DrawImage(_original, page);
            g.ResetClip();

            using (Pen p = new Pen(Theme.Accent, 1.5f)) g.DrawLine(p, splitX, page.Top, splitX, page.Bottom);

            DrawTag(g, new Point(leftHalf.X + 8, page.Y + 8), "BEFORE", Theme.TextDim);
            DrawTag(g, new Point(splitX + 8, page.Y + 8), "AFTER", Theme.Accent);
        }

        /// <summary>
        /// Auto crop results to draw over the page, as normalised outlines in the
        /// range 0..1. The measured vertices preserve skew, curves and notches;
        /// an upright bounding rectangle would hide the boundary the operator
        /// needs to approve before it becomes a scan crop.
        /// </summary>
        List<PointF[]> _regions;

        /// <summary>
        /// Auto crop results to draw, as normalised outlines. Replacing the list
        /// drops any selection with it: the numbers the operator was working on
        /// no longer describe the same items.
        /// </summary>
        public List<PointF[]> DetectedRegions
        {
            get { return _regions; }
            set { _regions = value; if (_selected >= 0) { _selected = -1; } }
        }

        public List<CropConfidence> DetectedRegionConfidence;

        void PaintDetectedRegions(Graphics g, Rectangle page)
        {
            if (DetectedRegions == null || DetectedRegions.Count == 0) return;

            for (int i = 0; i < DetectedRegions.Count; i++)
            {
                PointF[] norm = DetectedRegions[i];
                if (norm == null || norm.Length < 3) continue;

                PointF[] pts = new PointF[norm.Length];
                for (int k = 0; k < norm.Length; k++)
                    pts[k] = new PointF(page.X + norm[k].X * page.Width,
                                        page.Y + norm[k].Y * page.Height);

                bool review = DetectedRegionConfidence != null && i < DetectedRegionConfidence.Count &&
                    DetectedRegionConfidence[i] < CropConfidence.High;
                Color colour = review ? Theme.Warn : Theme.Accent;
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(38, colour)))
                    g.FillPolygon(fill, pts);
                using (Pen edge = new Pen(colour, 2f))
                {
                    edge.LineJoin = LineJoin.Round;
                    if (review) edge.DashStyle = DashStyle.Dash;
                    g.DrawPolygon(edge, pts);
                }
                if (pts.Length != 4)
                    using (SolidBrush vertexBrush = new SolidBrush(colour))
                        foreach (PointF vertex in pts) g.FillEllipse(vertexBrush, vertex.X - 1.5f, vertex.Y - 1.5f, 3, 3);

                if (i == _selected) DrawRegionHandles(g, pts);

                // Numbered, because with several items the order decides which
                // page is which in the filmstrip and in the saved files.
                if (DetectedRegions.Count > 1) DrawRegionBadge(g, pts, i + 1);
            }
        }

        /// <summary>
        /// The eight handles of the chosen region, on its upright bounds.
        ///
        /// Upright even when the region is not, because these scale the outline
        /// rather than redraw it: a handle that followed a tilted edge would
        /// suggest the operator can rotate the item, which they cannot -- the
        /// angle came from the item itself.
        /// </summary>
        void DrawRegionHandles(Graphics g, PointF[] pts)
        {
            PointF[] handles;

            if (pts.Length == 4)
            {
                handles = new PointF[]
                {
                    pts[0], Middle(pts[0], pts[1]), pts[1], Middle(pts[1], pts[2]),
                    pts[2], Middle(pts[2], pts[3]), pts[3], Middle(pts[3], pts[0])
                };
                using (Pen frame = new Pen(Color.FromArgb(150, Theme.Accent), 1f))
                {
                    frame.DashStyle = DashStyle.Dot;
                    g.DrawPolygon(frame, pts);
                }
            }
            else
            {
                float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
                foreach (PointF pt in pts)
                {
                    left = Math.Min(left, pt.X); right = Math.Max(right, pt.X);
                    top = Math.Min(top, pt.Y); bottom = Math.Max(bottom, pt.Y);
                }
                float midX = (left + right) / 2, midY = (top + bottom) / 2;

                handles = new PointF[]
                {
                    new PointF(left, top), new PointF(midX, top), new PointF(right, top),
                    new PointF(right, midY), new PointF(right, bottom), new PointF(midX, bottom),
                    new PointF(left, bottom), new PointF(left, midY)
                };
                using (Pen frame = new Pen(Color.FromArgb(150, Theme.Accent), 1f))
                {
                    frame.DashStyle = DashStyle.Dot;
                    g.DrawRectangle(frame, left, top, right - left, bottom - top);
                }
            }
            using (SolidBrush fill = new SolidBrush(Color.White))
            using (Pen edge = new Pen(Theme.Accent, 1.5f))
                foreach (PointF h in handles)
                {
                    g.FillRectangle(fill, h.X - 4, h.Y - 4, 8, 8);
                    g.DrawRectangle(edge, h.X - 4, h.Y - 4, 8, 8);
                }
        }

        void DrawRegionBadge(Graphics g, PointF[] pts, int number)
        {
            float cx = 0, cy = 0;
            foreach (PointF pt in pts) { cx += pt.X; cy += pt.Y; }
            cx /= pts.Length; cy /= pts.Length;

            const int r = 13;
            RectangleF disc = new RectangleF(cx - r, cy - r, r * 2, r * 2);
            using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, disc);
            TextRenderer.DrawText(g, number.ToString(CultureInfo.InvariantCulture), Theme.UiSemi(9.5f),
                Rectangle.Round(disc), Theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        void PaintCrop(Graphics g, Rectangle page)
        {
            RectangleF c = CropPixels;
            bool full = _cropNorm.X <= 0.002f && _cropNorm.Y <= 0.002f &&
                        _cropNorm.Width >= 0.998f && _cropNorm.Height >= 0.998f;

            if (!full)
            {
                // Dim everything outside the selection.
                using (Region outside = new Region(page))
                {
                    outside.Exclude(Rectangle.Round(c));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(Theme.IsLight ? 64 : 140, 0, 0, 0)))
                        g.FillRegion(b, outside);
                }
            }

            using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(230, 20, 20, 20) : Color.FromArgb(230, 255, 255, 255), 1f))
                g.DrawRectangle(p, c.X, c.Y, c.Width, c.Height);

            // Thirds guides appear only while actively adjusting, so they do not
            // sit on top of the image during evaluation.
            if (ShowThirds || _dragging)
            {
                using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(80, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255), 1f))
                {
                    for (int i = 1; i <= 2; i++)
                    {
                        float x = c.X + c.Width * i / 3f;
                        float y = c.Y + c.Height * i / 3f;
                        g.DrawLine(p, x, c.Y, x, c.Bottom);
                        g.DrawLine(p, c.X, y, c.Right, y);
                    }
                }
            }

            DrawCornerBrackets(g, c);

            if (_image != null)
            {
                int pxW = (int)Math.Round(_cropNorm.Width * _image.Width);
                int pxH = (int)Math.Round(_cropNorm.Height * _image.Height);
                double inW = (_image.XDpi > 1) ? pxW / _image.XDpi : 0;
                double inH = (_image.YDpi > 1) ? pxH / _image.YDpi : 0;

                string dims = string.Format(CultureInfo.InvariantCulture,
                    "{0:0.00} × {1:0.00} in     {2} × {3} px  @ {4:0} DPI",
                    inW, inH, pxW, pxH, _image.XDpi);
                if (!string.IsNullOrEmpty(ActivePaperLabel) && !full)
                    dims = ActivePaperLabel + "  •  " + dims;

                // Prefer just under the crop, but fall back to just inside it when
                // that would land in the reserved band or off the canvas.
                PointF at = new PointF(c.X, c.Bottom + 10);
                int limit = Height - ReservedBottom - 30;
                if (at.Y > limit) at.Y = Math.Max(c.Y + 6, c.Bottom - 32);
                if (at.X < 6) at.X = 6;
                DrawPill(g, at, dims);
            }

            if (DetectedConfidence >= 0 && Math.Abs(DetectedAngle) > 0.001)
            {
                string tag = string.Format(CultureInfo.InvariantCulture,
                    "∠ {0:0.0}° skew   ({1:0}% confidence)", DetectedAngle, DetectedConfidence * 100);
                float ty = Math.Max(ReservedTop + 6, c.Y - 34);
                DrawPill(g, new PointF(Math.Max(6, c.X), ty), tag);
            }
        }

        static void DrawCornerBrackets(Graphics g, RectangleF c)
        {
            const int len = 16;
            using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(30, 30, 30) : Color.White, 2.4f))
            {
                p.StartCap = LineCap.Flat;
                p.EndCap = LineCap.Flat;

                g.DrawLines(p, new PointF[] { new PointF(c.X, c.Y + len), new PointF(c.X, c.Y), new PointF(c.X + len, c.Y) });
                g.DrawLines(p, new PointF[] { new PointF(c.Right - len, c.Y), new PointF(c.Right, c.Y), new PointF(c.Right, c.Y + len) });
                g.DrawLines(p, new PointF[] { new PointF(c.Right, c.Bottom - len), new PointF(c.Right, c.Bottom), new PointF(c.Right - len, c.Bottom) });
                g.DrawLines(p, new PointF[] { new PointF(c.X + len, c.Bottom), new PointF(c.X, c.Bottom), new PointF(c.X, c.Bottom - len) });
            }

            // Edge midpoint ticks - the affordance for single-axis resize.
            using (Pen p = new Pen(Theme.IsLight ? Color.FromArgb(200, 30, 30, 30) : Color.FromArgb(200, 255, 255, 255), 2f))
            {
                float mx = c.X + c.Width / 2, my = c.Y + c.Height / 2;
                g.DrawLine(p, mx - 8, c.Y, mx + 8, c.Y);
                g.DrawLine(p, mx - 8, c.Bottom, mx + 8, c.Bottom);
                g.DrawLine(p, c.X, my - 8, c.X, my + 8);
                g.DrawLine(p, c.Right, my - 8, c.Right, my + 8);
            }
        }

        void PaintDetection(Graphics g, Rectangle page)
        {
            PointF[] pts = new PointF[DetectedPolygon.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new PointF(
                    page.X + DetectedPolygon[i].X * page.Width,
                    page.Y + DetectedPolygon[i].Y * page.Height);
            }

            using (Pen p = new Pen(Theme.Info, 1.6f))
            {
                p.DashStyle = DashStyle.Dash;
                p.DashPattern = new float[] { 5f, 4f };
                g.DrawPolygon(p, pts);
            }
        }

        void PaintSweep(Graphics g, Rectangle page)
        {
            int y = page.Y + (int)Math.Round(page.Height * _sweep.Value);

            using (LinearGradientBrush b = new LinearGradientBrush(
                new Rectangle(page.X, Math.Max(page.Y, y - 60), Math.Max(1, page.Width), 60),
                Color.FromArgb(0, Theme.Accent), Color.FromArgb(48, Theme.Accent), LinearGradientMode.Vertical))
            {
                g.FillRectangle(b, page.X, Math.Max(page.Y, y - 60), page.Width, Math.Min(60, y - page.Y));
            }

            using (Pen p = new Pen(Color.FromArgb(210, Theme.Accent), 1.6f))
                g.DrawLine(p, page.X, y, page.Right, y);

            // Loop while the scan is still running.
            if (_sweep.Settled && _scanning) { _sweep.Snap(0); _sweep.Target = 1; Animator.Kick(); }
        }

        static void DrawPill(Graphics g, PointF at, string text)
        {
            using (Font f = Theme.UiSemi(8.25f))
            {
                Size sz = TextRenderer.MeasureText(g, text, f);
                Rectangle r = new Rectangle((int)at.X, (int)at.Y, sz.Width + 18, 24);

                using (GraphicsPath path = Theme.Round(r, 12))
                {
                    using (SolidBrush b = new SolidBrush(Theme.Glass)) g.FillPath(b, path);
                    using (Pen p = new Pen(Theme.GlassLine, 1f)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, text, f, r, Theme.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        static void DrawTag(Graphics g, Point at, string text, Color colour)
        {
            using (Font f = Theme.UiSemi(7.5f))
            {
                Size sz = TextRenderer.MeasureText(g, text, f);
                Rectangle r = new Rectangle(at.X, at.Y, sz.Width + 14, 20);
                using (GraphicsPath path = Theme.Round(r, 10))
                using (SolidBrush b = new SolidBrush(Theme.Glass))
                    g.FillPath(b, path);
                TextRenderer.DrawText(g, text, f, r, colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // =========================================================================
    // Vertical filmstrip
    // =========================================================================
    public class StudioFilmstripView : Control
    {
        public class Page
        {
            public RawImage Image;
            public Bitmap Thumb;
            public string Caption = "";
        }

        readonly List<Page> _pages = new List<Page>();
        int _selected = -1;
        int _hot = -1;
        int _scroll;

        const int CardHeight = 118;
        const int Pad = 8;

        public event EventHandler SelectionChanged;
        public event EventHandler<PageEventArgs> PageDeleted;

        public class PageEventArgs : EventArgs
        {
            public readonly int Index;
            public PageEventArgs(int i) { Index = i; }
        }

        public StudioFilmstripView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Theme.Surface;
            Width = Theme.FilmstripWidth;
        }

        public int Count { get { return _pages.Count; } }
        public int SelectedIndex { get { return _selected; } }

        public RawImage SelectedImage
        {
            get { return (_selected >= 0 && _selected < _pages.Count) ? _pages[_selected].Image : null; }
        }

        public void AddPage(RawImage img)
        {
            if (img == null || !img.IsValid) return;

            Page p = new Page { Image = img };
            try
            {
                using (Bitmap full = img.ToBitmap())
                    p.Thumb = MakeThumb(full, Theme.FilmstripWidth - 26, CardHeight - 30);
            }
            catch { }

            p.Caption = img.Width + "×" + img.Height;
            _pages.Add(p);
            _selected = _pages.Count - 1;

            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void ReplaceSelectedImage(RawImage img)
        {
            if (_selected < 0 || _selected >= _pages.Count || img == null || !img.IsValid) return;
            Page p = _pages[_selected];
            p.Image = img;
            if (p.Thumb != null) { p.Thumb.Dispose(); p.Thumb = null; }
            try
            {
                using (Bitmap full = img.ToBitmap())
                    p.Thumb = MakeThumb(full, Theme.FilmstripWidth - 26, CardHeight - 30);
            }
            catch { }
            p.Caption = img.Width + "×" + img.Height;
            Invalidate();
        }

        public void Clear()
        {
            foreach (Page p in _pages) if (p.Thumb != null) p.Thumb.Dispose();
            _pages.Clear();
            _selected = -1;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public List<RawImage> AllImages()
        {
            List<RawImage> list = new List<RawImage>();
            foreach (Page p in _pages) list.Add(p.Image);
            return list;
        }

        static Bitmap MakeThumb(Bitmap src, int maxW, int maxH)
        {
            double s = Math.Min(maxW / (double)src.Width, maxH / (double)src.Height);
            int w = Math.Max(1, (int)(src.Width * s));
            int h = Math.Max(1, (int)(src.Height * s));

            Bitmap bmp = new Bitmap(w, h);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return bmp;
        }

        Rectangle CardRect(int i)
        {
            return new Rectangle(Pad, Pad + i * (CardHeight + Pad) - _scroll, Width - Pad * 2, CardHeight);
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < _pages.Count; i++) if (CardRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != _hot) { _hot = i; Cursor = (i >= 0) ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i < 0) { base.OnMouseDown(e); return; }

            Rectangle card = CardRect(i);
            Rectangle del = new Rectangle(card.Right - 22, card.Y + 4, 18, 18);

            if (del.Contains(e.Location))
            {
                if (_pages[i].Thumb != null) _pages[i].Thumb.Dispose();
                _pages.RemoveAt(i);
                if (_selected >= _pages.Count) _selected = _pages.Count - 1;
                Invalidate();
                if (PageDeleted != null) PageDeleted(this, new PageEventArgs(i));
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
            else if (i != _selected)
            {
                _selected = i;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int total = _pages.Count * (CardHeight + Pad) + Pad;
            int max = Math.Max(0, total - Height);
            _scroll = Math.Max(0, Math.Min(max, _scroll - e.Delta / 2));
            Invalidate();
            base.OnMouseWheel(e);
        }


        protected override void WndProc(ref Message m)
        {
            // Let the form resize from its border even though this control covers
            // the client area - see StudioSplitter.cs for the full explanation.
            if (m.Msg == Frame.WM_NCHITTEST)
            {
                Point screen = new Point(m.LParam.ToInt32());
                if (Frame.NearFormEdge(this, screen)) { m.Result = (IntPtr)Frame.HTTRANSPARENT; return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);

            using (Pen p = new Pen(Theme.LineSoft, 1f)) g.DrawLine(p, 0, 0, 0, Height);

            if (_pages.Count == 0)
            {
                using (Font f = Theme.Micro())
                    TextRenderer.DrawText(g, "PAGES", f, new Rectangle(0, 14, Width, 16),
                        Theme.TextFaint, TextFormatFlags.HorizontalCenter);

                // A dashed placeholder card, so the strip reads as an empty
                // container rather than as dead space.
                Rectangle ph = new Rectangle(Pad, 40, Width - Pad * 2, CardHeight);
                using (GraphicsPath path = Theme.Round(ph, 8))
                using (Pen dp = new Pen(Theme.Line, 1f))
                {
                    dp.DashStyle = DashStyle.Dash;
                    dp.DashPattern = new float[] { 4f, 4f };
                    g.DrawPath(dp, path);
                }
                using (Font f = Theme.Ui(7.5f))
                    TextRenderer.DrawText(g, "Your scans\nwill appear\nhere", f,
                        new Rectangle(6, 40, Width - 12, CardHeight), Theme.TextFaint,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            for (int i = 0; i < _pages.Count; i++)
            {
                Rectangle card = CardRect(i);
                if (card.Bottom < 0 || card.Y > Height) continue;

                bool sel = (i == _selected);

                using (GraphicsPath path = Theme.Round(card, 8))
                {
                    using (SolidBrush b = new SolidBrush(sel ? Theme.Raised : (i == _hot ? Theme.Field : Theme.Raised)))
                        g.FillPath(b, path);
                    using (Pen p = new Pen(sel ? Theme.Accent : Theme.LineSoft, sel ? 1.6f : 1f))
                        g.DrawPath(p, path);
                }

                Bitmap thumb = _pages[i].Thumb;
                if (thumb != null)
                {
                    int tw = Math.Min(thumb.Width, card.Width - 12);
                    int th = Math.Min(thumb.Height, card.Height - 26);
                    Rectangle dest = new Rectangle(card.X + (card.Width - tw) / 2, card.Y + 6, tw, th);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(thumb, dest);
                }

                using (Font f = Theme.UiSemi(7.5f))
                    TextRenderer.DrawText(g, (i + 1).ToString("00", CultureInfo.InvariantCulture), f,
                        new Rectangle(card.X + 5, card.Y + 3, 22, 16),
                        sel ? Theme.Accent : Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

                if (i == _hot)
                {
                    Rectangle del = new Rectangle(card.Right - 22, card.Y + 4, 18, 18);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(190, 0, 0, 0))) g.FillEllipse(b, del);
                    using (Pen p = new Pen(Theme.Danger, 1.6f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLine(p, del.X + 6, del.Y + 6, del.Right - 6, del.Bottom - 6);
                        g.DrawLine(p, del.Right - 6, del.Y + 6, del.X + 6, del.Bottom - 6);
                    }
                }

                using (Font f = Theme.Ui(7f))
                    TextRenderer.DrawText(g, _pages[i].Caption, f,
                        new Rectangle(card.X, card.Bottom - 18, card.Width, 16),
                        Theme.TextFaint, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}

namespace NextScan.App
{
    /// <summary>Which detected region an event is about, and where on screen.</summary>
    public sealed class RegionEventArgs : System.EventArgs
    {
        public readonly int Index;
        public readonly System.Drawing.Point At;

        /// <summary>Where on the page, as a fraction of it, when that is known.</summary>
        public readonly System.Drawing.PointF Norm;

        public RegionEventArgs(int index, System.Drawing.Point at)
            : this(index, at, System.Drawing.PointF.Empty) { }

        public RegionEventArgs(int index, System.Drawing.Point at, System.Drawing.PointF norm)
        { Index = index; At = at; Norm = norm; }
    }
}
