// =============================================================================
// NextScan Studio - Application shell (ground-up rebuild)
// Plan ref: MASTER_PLAN section 13.1 (shell layout), 13.4 (keyboard).
//
// A fixed workflow navigation, settings inspector, preview toolbar and bottom
// command area give each action a stable home. The preview is never obscured by
// a floating toolbar. Compact windows wrap tools while retaining every action.
// Acquisition, crop planning and output retain their existing event handlers.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NextScan.Core;
using ColorMode = NextScan.Core.ColorMode;

// Aliased rather than imported. The shell reaches into the AI layer in four
// places and nowhere else, and naming it at each of them is what keeps that
// true -- a plain using would make it invisible when a fifth appeared.
using Ai = NextScan.Ai;

namespace NextScan.App
{
    public class StudioShell : Form
    {
        // ---- engine ----------------------------------------------------------
        readonly DeviceBroker _broker = new DeviceBroker();
        readonly StudioSettings _settings;
        List<ScannerEntry> _scanners = new List<ScannerEntry>();
        DeviceDescriptor _device;
        volatile bool _busy;

        // ---- chrome ----------------------------------------------------------
        Panel _titleBar;
        NsDeviceBar _deviceBar;
        // The workflow rail on the left picks a section; the inspector on the
        // right shows it. Settings are not a rail section - they open as a full
        // page from the title bar, because they are set once and left alone.
        Panel _rail;
        readonly List<NsNavButton> _railButtons = new List<NsNavButton>();

        Panel _inspector;
        Panel _inspHead;
        Panel _inspScroll;
        Panel _inspFoot;
        Panel _pagesPanel;
        StudioAiPanel _aiPanel;
        NsPill _savePill;

        /// <summary>The container the section builders fill.</summary>
        Panel _drawerHost;

        Label _planLabel;
        NsIconButton _toPsPill;

        // ---- the full-page settings view --------------------------------------
        Panel _settingsPage;
        Panel _settingsHead;
        Panel _settingsBody;
        NsIconButton _settingsButton;
        bool _settingsOpen;

        StudioCanvasView _canvas;
        StudioFilmstripView _film;
        NsBatchBar _batchBar;
        NsIconButton _batchPill;
        bool _batchRunning;
        Panel _status;
        Label _statusText;

        /// <summary>Zoom, rotate and overlay controls, docked in the status bar.</summary>
        readonly List<NsIconButton> _viewButtons = new List<NsIconButton>();
        NsIconButton _tbGrid, _tbCompare;
        Label _zoomText;

        readonly StudioLayout _layout = StudioLayout.Load();
        NsSplitter _splitDrawer;
        

        // ---- drawer widgets we read back -------------------------------------
        NsDropdown _ddPaperSize, _ddDpi, _ddMode, _ddFormat, _ddTonePreset;
        NsSegment _segOrientation;
        NsToggle _tgVendorUi, _tgAutoCrop, _tgMultiCrop, _tgAutoDeskew, _tgOpenPs, _tgKeep;
        NsSlider _slWhiten, _slBright, _slContrast, _slBwThreshold, _slQuality;
        Label _qualityNote;

        NsToggle _tgAdaptive;
        NsSegment _segSource;
        NsPill _findAgainPill;
        NsReadout _previewCost;

        /// <summary>What the connected scanner can actually feed from.</summary>
        bool _hasFeeder, _hasDuplex;
        NsReadout _roEstimate;
        Bitmap _processedCache;

        public class PaperSizeDef
        {
            public string Key;
            public string Label;
            public double WidthIn;
            public double HeightIn;

            public PaperSizeDef(string key, string label, double w, double h)
            {
                Key = key;
                Label = label;
                WidthIn = w;
                HeightIn = h;
            }
        }

        static readonly PaperSizeDef[] AllPaperSizes = new PaperSizeDef[]
        {
            new PaperSizeDef("Maximum", "Maximum (Full Bed)", 0, 0),
            new PaperSizeDef("A3", "A3  (297 × 420 mm)", 11.69, 16.54),
            new PaperSizeDef("Legal", "Legal  (8.5 × 14 in)", 8.50, 14.00),
            new PaperSizeDef("A4", "A4  (210 × 297 mm)", 8.27, 11.69),
            new PaperSizeDef("Letter", "Letter  (8.5 × 11 in)", 8.50, 11.00),
            new PaperSizeDef("A5", "A5  (148 × 210 mm)", 5.83, 8.27),
            new PaperSizeDef("B5", "B5  (182 × 257 mm)", 7.17, 10.12),
            new PaperSizeDef("Photo5x7", "5 × 7\" Photo  (13 × 18 cm)", 5.00, 7.00),
            new PaperSizeDef("Photo4x6", "4 × 6\" Photo  (10 × 15 cm)", 4.00, 6.00),
            new PaperSizeDef("Card", "Business Card  (2 × 3.5 in)", 2.00, 3.50),
        };

        readonly List<PaperSizeDef> _supportedPaperEntries = new List<PaperSizeDef>
        {
            AllPaperSizes[0], AllPaperSizes[3], AllPaperSizes[4], AllPaperSizes[5], AllPaperSizes[6], AllPaperSizes[7], AllPaperSizes[8], AllPaperSizes[9]
        };
        readonly List<string> _supportedPaperLabels = new List<string>
        {
            "Maximum (Full Bed)", "A4  (210 × 297 mm)", "Letter  (8.5 × 11 in)", "A5  (148 × 210 mm)", "B5  (182 × 257 mm)", "4 × 6\" Photo  (10 × 15 cm)", "5 × 7\" Photo  (13 × 18 cm)", "Business Card  (2 × 3.5 in)"
        };
        int _paperSizeIndex = 0;
        /// <summary>
        /// The area of the platen, in inches, that the page currently on the
        /// canvas came from.
        ///
        /// This is what makes a hand-drawn crop mean something physical: the crop
        /// is a fraction of the DISPLAYED page, and only this rectangle says where
        /// that page sat on the glass. A preview covers the whole bed; a scan made
        /// from a crop covers just that crop, so cropping again composes correctly
        /// instead of re-measuring from the bed origin.
        /// </summary>
        RectangleF _pageBedRect = RectangleF.Empty;

        /// <summary>True once the user has dragged the crop themselves.</summary>
        bool _manualCrop;

        /// <summary>
        /// True only when the operator dragged the selection themselves.
        ///
        /// This is not the same as _manualCrop, and conflating them broke auto
        /// crop completely: a paper size preset also sets a box, and A4 on an
        /// 8.5 in wide platen is 0.97 of the glass, which is "not the full bed"
        /// and so looked exactly like a hand-drawn selection. Auto crop stood
        /// aside for it on every scan.
        /// </summary>
        bool _cropDrawnByUser;



        /// <summary>Set once a real preview or scan has landed on the canvas.</summary>
        bool _hasRealPage;

        /// <summary>The Scan pill; it becomes the Cancel button while busy.</summary>
        /// <summary>
        /// Spools every page to disk as it arrives, so an interrupted batch is
        /// recoverable. One journal covers the whole session, including pages
        /// added by a second scan, and is only discarded once they are exported.
        /// </summary>
        ScanJournal _journal;
        NsPill _recoverPill;
        JournalSession _recoverable;

        HotFolder _hot;
        NsPill _hotPill;
        Label _hotStatus;
        Label _hotPathLabel;

        NsToggle _tgMultiPage;
        NsToggle _tgUntilEmpty;
        readonly List<Control> _batchFeedControls = new List<Control>();
        NsToggle _tgDropBlank;
        EventHandler _namePreview;
        readonly List<NsTextBox> _themedFields = new List<NsTextBox>();

        NsPill _scanPill;
        NsPill _previewPill;
        NsDropdown _presetPicker;
        NsTextBox _presetName;
        NsIconButton _fullBedPill;
        bool _cancelling;

        /// <summary>Region asked for by the scan in flight, for OnScanDone.</summary>
        RectangleF _lastRequestedRegion = RectangleF.Empty;

        /// <summary>
        /// The page the canvas is showing, at the resolution it was captured at,
        /// and what that page covers on the glass.
        ///
        /// Held separately because the canvas may not be showing it. Previewing
        /// a selection composites the result onto a picture of the whole bed
        /// built at BedViewDpi, and that composite is what <c>_canvas.Image</c>
        /// returns from then on.
        /// </summary>
        RawImage _capturePage;
        RectangleF _captureBedRect;

        /// <summary>
        /// What the preview image covers on the glass, in inches.
        ///
        /// Not the same thing as <c>_pageBedRect</c>, which is what the canvas
        /// is currently showing. They agree while a preview is the whole platen,
        /// which is why one field did for both until previews could be of a
        /// selection: after that, detections measured against the wrong one
        /// land somewhere else entirely on the bed.
        /// </summary>
        RectangleF _previewBedRect = RectangleF.Empty;
        bool _lastScanUsedManualCrop;

        /// <summary>
        /// Set when auto crop actually changed a page during the current scan.
        /// The bed view places a scanned page back inside the region that was
        /// requested from the scanner; once auto crop has cut the page down,
        /// that mapping no longer describes it and the composite would put the
        /// page in the wrong place on the glass.
        /// </summary>
        bool _autoCropChangedPages;

        /// <summary>
        /// What auto crop did to the last scan, for the status line. Without it
        /// the feature is silent: a page that came back whole looks the same
        /// whether the detector declined, was switched off, or was never asked.
        /// </summary>
        string _autoCropNote = "";

        /// <summary>
        /// The pages the most recent scan produced, after auto crop. Kept so the
        /// Photoshop handoff can send all of them: separating four cards into
        /// four pages and then handing over only the one the canvas happens to
        /// be showing defeats the point of separating them.
        /// </summary>
        readonly List<RawImage> _lastScanPages = new List<RawImage>();

        /// <summary>
        /// What the last preview found, in inches on the glass. Scanning cuts
        /// these out of the page and nothing else does.
        ///
        /// Auto crop deliberately never runs during a scan on its own. Cropping
        /// something the operator has not seen first is how a scan comes back
        /// trimmed in a way nobody asked for; the preview is where the decision
        /// is shown and agreed to. A scan pressed without a preview returns the
        /// whole selected area, untouched.
        /// </summary>
        /// <summary>
        /// The last preview of the whole glass.
        ///
        /// Kept so finding can be run again without asking for another pass of
        /// the lamp. Re-arming auto crop after a hand-drawn selection used to
        /// need a whole new preview, which made the switch look broken.
        /// </summary>
        RawImage _previewPage;
        bool _probing;
        bool _thoroughDetect;

        /// <summary>
        /// Every item of the last scan, shown together as one picture.
        ///
        /// Built at display resolution and never exported: the pages it is made
        /// from are kept whole in the filmstrip, and everything that writes a
        /// file or hands one to Photoshop reads those. Holding it here rather
        /// than only in the canvas is what lets the view be switched back and
        /// forth without scanning again.
        /// </summary>
        RawImage _sheet;
        RawImage _beforeSheet;
        bool _showingSheet;
        NsIconButton _tbSheet;

        readonly List<RectangleF> _cropPlan = new List<RectangleF>();
        readonly List<PointF[]> _cropPlanCorners = new List<PointF[]>();
        readonly List<PointF[]> _cropPlanOutlines = new List<PointF[]>();

        /// <summary>
        /// Runs the finder again over the preview already on screen.
        ///
        /// Every switch on the Detect panel changes what finding would produce,
        /// so each one re-runs it rather than throwing the plan away and waiting
        /// for the operator to work out that another preview is needed.
        /// </summary>
        void RedetectFromPreview(string why)
        {
            if (!_settings.AutoCrop || _previewPage == null)
            {
                ClearCropPlan(why);
                return;
            }

            // The plan is held in inches on the GLASS, so it can only be cut out
            // of a page that covers the glass. A hand-drawn selection would make
            // the scan cover less than that, so finding again hands the whole
            // bed back before it looks.
            if (_cropDrawnByUser)
            {
                _cropDrawnByUser = false;
                _manualCrop = false;
                if (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                    ApplyPaperSizeEntry(_supportedPaperEntries[_paperSizeIndex]);
            }

            PreviewAutoCrop(_previewPage);
            UpdateStatus();
        }

        void ClearCropPlan(string why)
        {
            if (_cropPlan.Count == 0) return;
            _cropPlan.Clear();
            _cropPlanCorners.Clear();
            _cropPlanOutlines.Clear();
            if (_canvas != null)
            {
                _canvas.DetectedRegions = null;
                _canvas.DetectedRegionConfidence = null;
                _canvas.Invalidate();
            }
            UpdateGroupSummaries();
            UpdatePlan();
            Log("crop plan cleared: " + why);
        }

        double _activeBedW = 8.5;
        double _activeBedH = 11.7;

        void ApplyPaperSizeEntry(PaperSizeDef def)
        {
            if (def == null) return;
            _settings.PaperSize = def.Key;
            _settings.RegionLeftIn = 0;
            _settings.RegionTopIn = 0;

            double baseW = (def.WidthIn > 0) ? def.WidthIn : _activeBedW;
            double baseH = (def.HeightIn > 0) ? def.HeightIn : _activeBedH;

            // Strict clamp to current active scanner platen limits
            baseW = Math.Min(baseW, _activeBedW);
            baseH = Math.Min(baseH, _activeBedH);

            double targetW = _settings.PaperLandscape ? Math.Max(baseW, baseH) : Math.Min(baseW, baseH);
            double targetH = _settings.PaperLandscape ? Math.Min(baseW, baseH) : Math.Max(baseW, baseH);

            _settings.RegionWidthIn = targetW;
            _settings.RegionHeightIn = targetH;

            ApplyPaperSizeToCanvasCrop(def);
        }

        void ApplyPaperSizeToCanvasCrop(PaperSizeDef def)
        {
            // Choosing a paper size replaces any selection the operator drew.
            _cropDrawnByUser = false;
            ClearCropPlan("the paper size changed");

            if (_canvas == null) return;
            if (def == null || def.Key == "Maximum")
            {
                _canvas.ActivePaperLabel = null;
                _canvas.CropNorm = new RectangleF(0f, 0f, 1f, 1f);
                _settings.CropNorm = _canvas.CropNorm;
                _canvas.Invalidate();
                return;
            }

            if (_canvas.Image != null && _canvas.Image.IsValid && def.WidthIn > 0 && def.HeightIn > 0)
            {
                double targetW = _settings.PaperLandscape ? Math.Max(def.WidthIn, def.HeightIn) : Math.Min(def.WidthIn, def.HeightIn);
                double targetH = _settings.PaperLandscape ? Math.Min(def.WidthIn, def.HeightIn) : Math.Max(def.WidthIn, def.HeightIn);

                double xdpi = _canvas.Image.XDpi > 1 ? _canvas.Image.XDpi : 300.0;
                double ydpi = _canvas.Image.YDpi > 1 ? _canvas.Image.YDpi : 300.0;

                double imgW = _canvas.Image.Width / xdpi;
                double imgH = _canvas.Image.Height / ydpi;

                float normW = Math.Min(1.0f, (float)(targetW / imgW));
                float normH = Math.Min(1.0f, (float)(targetH / imgH));

                // Position crop box centered on current image
                float normX = Math.Max(0f, (1.0f - normW) / 2f);
                float normY = Math.Max(0f, (1.0f - normH) / 2f);

                _canvas.ActivePaperLabel = def.Key + (_settings.PaperLandscape ? "  Landscape" : "  Portrait");
                _canvas.CropNorm = new RectangleF(normX, normY, normW, normH);
                _settings.CropNorm = _canvas.CropNorm;
                _canvas.Invalidate();
            }
        }

        void RotateActiveImage(RotateFlipType type)
        {
            if (_canvas == null || _canvas.Image == null || !_canvas.Image.IsValid) return;

            RawImage current = _canvas.Image;
            RawImage rotated = current.Rotate(type);
            if (rotated == null) return;

            _canvas.RotateCrop(type);
            _settings.CropNorm = _canvas.CropNorm;

            _canvas.SetImage(rotated);
            if (_film != null) _film.ReplaceSelectedImage(rotated);

            UpdateProcessedPreview();
            UpdateStatus();
        }

        readonly List<int> _supportedDpiValues = new List<int> { 75, 100, 150, 200, 300, 400, 600, 1200, 2400 };
        readonly List<string> _supportedDpiLabels = new List<string> { "75 dpi", "100 dpi", "150 dpi", "200 dpi", "300 dpi", "400 dpi", "600 dpi", "1200 dpi", "2400 dpi" };
        readonly Dictionary<string, DeviceCapabilities> _capsCache = new Dictionary<string, DeviceCapabilities>();

        // The workflow, in the order it happens. Settings are deliberately NOT
        // in this list: they are set once and then left alone, so they live
        // behind the title-bar button instead of taking a permanent rail slot.
        // Detect used to be a page of its own. It is not a separate job: what
        // the detector finds is decided by the same press of Preview that
        // captures the page, and the operator was being made to cross the rail
        // to reach settings that belong beside the button they just used.
        // Output is not here. Format, naming, the colour profile and where files
        // land are settings, not a step: they are chosen once for a way of
        // working and then left alone, while these four are what an operator
        // touches between one scan and the next. It lives under Settings.
        // Assist is last because it is the only one that is not part of getting
        // the page: the four before it are the scan, and it is what you do with
        // the scan once it exists.
        static readonly string[] SectionNames = { "Capture", "Look", "Batch", "Pages", "Assist" };

        // One icon per name, in the same order. Output's icon was left in this
        // list when Output stopped being a rail section, which shifted every
        // icon after it by one: Batch was wearing the download mark and Pages
        // was wearing Batch's.
        static readonly string[] SectionIcons =
        {
            NsIcon.Capture, NsIcon.Look, NsIcon.Batch, NsIcon.Pages, NsIcon.Assist
        };

        public StudioShell(StudioSettings settings)
        {
            _settings = settings ?? new StudioSettings();

            // Palette first: every control reads Theme in its constructor.
            Theme.Apply(_layout.LightTheme);

            Text = "NextScan Studio";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;

            // Size from the WORKING AREA, never a fixed constant. The previous
            // 1440x900 default was larger than this machine 1366x720 work area,
            // so the window opened clipped and Restore appeared to do nothing:
            // the restored size was still bigger than the screen.
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            MinimumSize = new Size(820, 560);

            int defW = Math.Max(MinimumSize.Width, Math.Min(1240, (int)(wa.Width * 0.90)));
            int defH = Math.Max(MinimumSize.Height, Math.Min(820, (int)(wa.Height * 0.92)));

            if (_layout.RememberWindowSize && _layout.WindowWidth > 0 && _layout.WindowHeight > 0)
            {
                // Never restore a size larger than the current screen: that is
                // exactly how the shell ended up permanently clipped before.
                defW = Math.Min(_layout.WindowWidth, wa.Width - 20);
                defH = Math.Min(_layout.WindowHeight, wa.Height - 20);
            }
            Size = new Size(Math.Max(MinimumSize.Width, defW), Math.Max(MinimumSize.Height, defH));
            BackColor = Theme.Ground;
            KeyPreview = true;
            DoubleBuffered = true;

            InitAppIcon();
            BuildChrome();
            BuildCanvasArea();
            BuildRail();
            BuildInspector();
            BuildSettingsPage();
            BuildStatus();

            Load += delegate { LayoutAll(); BeginProbe(); };
            Resize += delegate { LayoutAll(); };

            // The controls in a section are sized from the panel width, so a
            // finished resize has to rebuild them, not just move them.
            ResizeEnd += delegate { ShowSection(_activeSection); LayoutAll(); };
            KeyDown += OnShellKeyDown;
        }

        void InitAppIcon()
        {
            try
            {
                using (Bitmap bmp = new Bitmap(48, 48))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        Theme.Smooth(g);
                        DrawAppLogoBadge(g, 0, 0, 48);
                    }
                    IntPtr hIcon = bmp.GetHicon();
                    Icon = Icon.FromHandle(hIcon);
                }
            }
            catch { }
        }

        void DrawAppLogoBadge(Graphics g, int x, int y, int size)
        {
            float s = size / 24f;
            Rectangle r = new Rectangle(x, y, size, size);
            int radius = Math.Max(3, (int)Math.Round(size * 0.22));

            using (GraphicsPath path = Theme.Round(r, radius))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0x18, 0x1A, 0x22)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(0x35, 0x3A, 0x48), 1f))
                    g.DrawPath(p, path);
            }

            float nLeft = x + 5.5f * s;
            float nRight = x + size - 5.5f * s;
            float nTop = y + 5.0f * s;
            float nBottom = y + size - 5.0f * s;
            float midY = y + size / 2.0f;

            using (Pen pN = new Pen(Theme.Accent, Math.Max(1.2f, 1.6f * s)))
            {
                pN.StartCap = LineCap.Round;
                pN.EndCap = LineCap.Round;
                pN.LineJoin = LineJoin.Round;

                // Left vertical stem
                g.DrawLine(pN, nLeft, nTop, nLeft, nBottom);
                // Diagonal
                g.DrawLine(pN, nLeft, nTop, nRight, nBottom);
                // Right vertical stem
                g.DrawLine(pN, nRight, nTop, nRight, nBottom);
            }

            // Laser scanner beam
            using (Pen pGlow = new Pen(Color.FromArgb(90, Theme.Accent), Math.Max(1.5f, 3.2f * s)))
            {
                pGlow.StartCap = LineCap.Round;
                pGlow.EndCap = LineCap.Round;
                g.DrawLine(pGlow, x + 3f * s, midY, x + size - 3f * s, midY);
            }
            using (Pen pCore = new Pen(Color.FromArgb(0xFF, 0xFE, 0xE8), Math.Max(1f, 1.2f * s)))
            {
                pCore.StartCap = LineCap.Round;
                pCore.EndCap = LineCap.Round;
                g.DrawLine(pCore, x + 3.5f * s, midY, x + size - 3.5f * s, midY);
            }
        }

        // =====================================================================
        // Chrome
        // =====================================================================
        void BuildChrome()
        {
            _titleBar = new Panel { Height = Theme.TitleBarHeight, BackColor = Theme.Surface, Dock = DockStyle.Top };
            _titleBar.Paint += delegate (object s, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Surface);

                // One line, not two. The old stacked "STUDIO / CAPTURE
                // WORKSPACE" strapline said nothing an operator needed and cost
                // the height that now belongs to the preview.
                int badgeSize = 26;
                int badgeY = (_titleBar.Height - badgeSize) / 2;
                DrawAppLogoBadge(g, 14, badgeY, badgeSize);

                int wordX = 48;
                using (Font font = Theme.UiSemi(11.5f))
                {
                    Rectangle box = new Rectangle(wordX, 0, 120, _titleBar.Height);
                    TextRenderer.DrawText(g, "NextScan", font, box, Theme.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    wordX += TextRenderer.MeasureText(g, "NextScan", font, new Size(200, 30),
                                                      TextFormatFlags.NoPadding).Width + 6;
                }
                using (Font font = Theme.Ui(9f))
                    TextRenderer.DrawText(g, "Studio", font,
                        new Rectangle(wordX, 0, 90, _titleBar.Height), Theme.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                using (Pen p = new Pen(Theme.LineSoft, 1f))
                    g.DrawLine(p, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
            };
            _titleBar.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                // Standard borderless drag: hand the gesture back to the window
                // manager so snap layouts and multi-monitor behave normally.
                ReleaseCapture();
                SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
            };
            _titleBar.DoubleClick += delegate { ToggleMaximise(); };
            Controls.Add(_titleBar);

            _deviceBar = new NsDeviceBar();
            _deviceBar.DeviceName = "Searching…";
            _deviceBar.TransportText = "";
            _deviceBar.Size = new Size(560, 30);
            _deviceBar.SetBusy(true);
            _deviceBar.DeviceClicked += delegate { ShowDeviceMenu(); };
            _deviceBar.TransportClicked += delegate { ShowTransportMenu(); };
            _deviceBar.RefreshClicked += delegate { BeginProbe(); };
            _deviceBar.Nudged += delegate (object o, int dx)
            {
                _layout.DeviceBarX = Math.Max(150, Math.Min(ClientSize.Width - 200, _layout.DeviceBarX + dx));
                LayoutAll();
            };
            _deviceBar.SourceChanged += delegate
            {
                _settings.Source = IndexToSource(_deviceBar.SourceIndex);
                UpdateBatchFeedEnabled();
                if (_namePreview != null) _namePreview(null, EventArgs.Empty);
                SetStatus("Paper source: " + SourceLabel(_deviceBar.SourceIndex));
            };
            _titleBar.Controls.Add(_deviceBar);

            // Settings open as a page rather than a rail section, so their way
            // in belongs with the other window-level controls.
            _settingsButton = new NsIconButton
            {
                Icon = NsIcon.Settings,
                Size = new Size(34, 28),
                Toggle = false
            };
            _settingsButton.AccessibleName = "Settings";
            _tips.SetToolTip(_settingsButton, "Settings");
            _settingsButton.Click += delegate { OpenSettings(!_settingsOpen); };
            _titleBar.Controls.Add(_settingsButton);

            AddWindowButton("✕", delegate { Close(); }, Theme.Danger);
            AddWindowButton("▢", delegate { ToggleMaximise(); }, Theme.TextDim);
            AddWindowButton("─", delegate { WindowState = FormWindowState.Minimized; }, Theme.TextDim);
        }

        void AddWindowButton(string glyph, EventHandler onClick, Color hot)
        {
            NsPill b = new NsPill
            {
                Text = glyph,
                Kind = PillKind.Quiet,
                Size = new Size(44, Theme.TitleBarHeight - 12),
                Radius = 6,
                Font = Theme.Ui(9f),
                Tag = hot
            };
            b.Click += onClick;
            _titleBar.Controls.Add(b);
        }

        void ToggleMaximise()
        {
            WindowState = (WindowState == FormWindowState.Maximized)
                ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        // =====================================================================
        // Workflow rail and inspector
        //
        // A narrow icon rail on the left picks one section; the panel on the
        // right shows that section and nothing else. There is no accordion:
        // groups that open and shut mean the panel's height changes under the
        // pointer and the operator has to manage the panel as well as the scan.
        // One rail click, one panel, always the same size.
        // =====================================================================
        int _drawerContentHeight = 400;
        int _drawerScrollY = 0;
        int _activeSection;

        delegate void SectionBuilder(ref int y);

        /// <summary>Height of the pinned action footer.</summary>
        const int FooterHeight = 136;

        public const int RailWidth = 56;

        void BuildRail()
        {
            _rail = new Panel { BackColor = Theme.Surface };
            _rail.Paint += delegate (object sender, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Surface);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    e.Graphics.DrawLine(pen, _rail.Width - 1, 0, _rail.Width - 1, _rail.Height);
            };
            Controls.Add(_rail);

            for (int i = 0; i < SectionNames.Length; i++)
            {
                NsNavButton button = new NsNavButton
                {
                    Icon = SectionIcons[i],
                    Caption = SectionNames[i].ToUpperInvariant(),
                    AccessibleName = SectionNames[i],
                    Selected = (i == 0),
                    Tag = i
                };
                button.Click += OnRailClick;
                _rail.Controls.Add(button);
                _railButtons.Add(button);
            }
        }

        void OnRailClick(object sender, EventArgs e)
        {
            NsNavButton button = sender as NsNavButton;
            if (button == null) return;
            ShowSection((int)button.Tag);
        }

        void BuildInspector()
        {
            _inspector = new Panel { BackColor = Theme.Surface };
            _inspector.Paint += delegate (object sender, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Surface);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    e.Graphics.DrawLine(pen, 0, 0, 0, _inspector.Height);
            };
            Controls.Add(_inspector);

            _inspHead = new Panel { BackColor = Theme.Surface, Location = new Point(0, 0) };
            _inspHead.Paint += delegate (object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Surface);
                using (Font f = Theme.UiSemi(10.5f))
                    TextRenderer.DrawText(g, SectionNames[_activeSection], f,
                        new Rectangle(Dx, 0, Math.Max(1, _inspHead.Width - Dx - 60), _inspHead.Height),
                        Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                using (Font f = Theme.Ui(8f))
                    TextRenderer.DrawText(g, SectionHint(_activeSection), f,
                        new Rectangle(Dx, 0, Math.Max(1, _inspHead.Width - Dx * 2), _inspHead.Height),
                        Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                        TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    g.DrawLine(pen, 0, _inspHead.Height - 1, _inspHead.Width, _inspHead.Height - 1);
            };
            _inspector.Controls.Add(_inspHead);

            _inspScroll = new Panel { BackColor = Theme.Surface };
            _inspScroll.Paint += delegate (object sender, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Surface);
                if (_drawerContentHeight <= _inspScroll.Height) return;

                // A hairline thumb rather than a system scrollbar: it is never
                // dragged, it only has to say how much is below.
                int thumbHeight = Math.Max(28, _inspScroll.Height * _inspScroll.Height / _drawerContentHeight);
                int travel = Math.Max(1, _drawerContentHeight - _inspScroll.Height);
                int thumbTop = (_inspScroll.Height - thumbHeight) * _drawerScrollY / travel;
                using (SolidBrush brush = new SolidBrush(Theme.Line))
                    e.Graphics.FillRectangle(brush, _inspScroll.Width - 5, thumbTop, 3, thumbHeight);
            };
            _inspScroll.MouseWheel += OnDrawerMouseWheel;
            _inspector.Controls.Add(_inspScroll);

            _drawerHost = new Panel { Location = new Point(0, 0), BackColor = Theme.Surface };
            _drawerHost.MouseWheel += OnDrawerMouseWheel;
            _inspScroll.Controls.Add(_drawerHost);

            // The Pages panel is built once and kept: ShowSection disposes
            // everything it finds in the section host, and the session's
            // thumbnails have to outlive a rail click.
            _pagesPanel = new Panel { BackColor = Theme.Surface, Visible = false };
            _inspector.Controls.Add(_pagesPanel);
            if (_film != null) _pagesPanel.Controls.Add(_film);

            _recoverPill = new NsPill
            {
                Text = "Recover unsaved pages",
                Kind = PillKind.Normal,
                Radius = 7,
                Visible = false
            };
            _recoverPill.Click += delegate { RecoverSession(); };
            _pagesPanel.Controls.Add(_recoverPill);

            _savePill = new NsPill { Text = "Save session pages", Kind = PillKind.Primary, Radius = 7 };
            _savePill.Click += delegate { SaveSession(); };
            _pagesPanel.Controls.Add(_savePill);

            // Built once and kept, for the same reason the Pages panel is:
            // ShowSection disposes everything in the section host, and a
            // conversation has to survive a rail click.
            BuildAiPanel();

            _inspFoot = new Panel { BackColor = Theme.Surface };
            _inspFoot.Paint += delegate (object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(Theme.Mix(Theme.Surface, Theme.Ground, 0.35));
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    g.DrawLine(pen, 0, 0, _inspFoot.Width, 0);
            };
            _inspector.Controls.Add(_inspFoot);

            BuildActionFooter();
            ShowSection(0);
        }

        /// <summary>
        /// The assistant panel.
        ///
        /// Wrapped, because it is the only part of the shell that depends on an
        /// assembly loaded from a subfolder at run time. Every other failure
        /// here would be a bug; this one can be an install that lost a file, and
        /// it must cost the operator the Assist section rather than the window.
        /// </summary>
        void BuildAiPanel()
        {
            try
            {
                _aiPanel = new StudioAiPanel { Visible = false };
                _aiPanel.PageSource = delegate { return PageForAssistant(); };
                _aiPanel.PageNote = delegate { return AssistantPageNote(); };
                _aiPanel.Status = delegate (string text) { if (text.Length > 0) SetStatus(text); };
                _aiPanel.ChoiceChanged += delegate
                {
                    _settings.AiProvider = _aiPanel.ProviderId;
                    _settings.AiModel = _aiPanel.Model;
                    _settings.AiThinking = _aiPanel.Thinking.ToString();
                };

                _aiPanel.ProviderId = _settings.AiProvider;
                _aiPanel.Model = _settings.AiModel;
                _aiPanel.Thinking = ThinkingFromSettings();

                _inspector.Controls.Add(_aiPanel);
            }
            catch (Exception ex)
            {
                _aiPanel = null;
                Log("the assistant could not be loaded: " + ex.Message);
            }
        }

        Ai.ThinkingLevel ThinkingFromSettings()
        {
            try
            {
                return (Ai.ThinkingLevel)Enum.Parse(
                    typeof(Ai.ThinkingLevel), _settings.AiThinking, true);
            }
            catch { return Ai.ThinkingLevel.Medium; }
        }

        /// <summary>
        /// The page the assistant talks about.
        ///
        /// A saved session page wins over whatever is on the canvas: a page in
        /// the filmstrip is a finished scan the operator chose, and the preview
        /// underneath it is scaffolding. Falls back to the preview, because
        /// asking about what is on the glass before saving it is the normal
        /// case, not an edge one.
        /// </summary>
        RawImage PageForAssistant()
        {
            if (_film != null && _film.SelectedImage != null) return _film.SelectedImage;
            if (_capturePage != null && _capturePage.IsValid) return _capturePage;

            // Deliberately not PageToCutFrom. Before the first preview the
            // canvas holds a blank sheet, which is a perfectly valid RawImage of
            // nothing: the panel offered it as "Preview 851 x 1169" and pressing
            // any action would have sent the model a white page and paid for the
            // answer.
            if (_canvas == null || _canvas.IsPlaceholder) return null;
            return _canvas.Image;
        }

        string AssistantPageNote()
        {
            RawImage page = PageForAssistant();
            if (page == null || !page.IsValid) return "";

            bool saved = _film != null && _film.SelectedImage == page;
            return (saved ? "Page " + (_film.SelectedIndex + 1) : "Preview") + "  " +
                   page.Width + " x " + page.Height + "  " +
                   Math.Round(page.XDpi) + " dpi";
        }

        /// <summary>The right-hand note on the panel header: state, not a label.</summary>
        string SectionHint(int index)
        {
            switch (index)
            {
                case 0: return CaptureSummary() + " · " + DetectSummary();
                case 1: return LookSummary();
                case 2: return BatchSummary();
                case 3: return _film == null ? "" : _film.Count + (_film.Count == 1 ? " page" : " pages");
                default: return AssistantSummary();
            }
        }

        /// <summary>
        /// Preview, Scan, and the two follow-on actions.
        ///
        /// Four pills of equal weight said all four were equally likely, which
        /// is untrue: Scan is what the window is for. It gets the full width and
        /// the accent; Preview is the rehearsal above it; the two follow-ons are
        /// icons, because they are recognised rather than read.
        /// </summary>
        void BuildActionFooter()
        {
            _planLabel = new Label
            {
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoSize = false,
                Text = ""
            };
            _inspFoot.Controls.Add(_planLabel);

            _previewPill = new NsPill { Text = "Preview", Kind = PillKind.Normal, Radius = 7 };
            _previewPill.Click += delegate { StartScan(true, false); };
            _inspFoot.Controls.Add(_previewPill);

            // Preview follows the selection once there is one, which is what
            // the operator means by it. This is the way back out to the whole
            // glass without having to clear the selection first, and it is a
            // button rather than a setting because it is a thing you do, not a
            // mode you are in.
            _fullBedPill = new NsIconButton { Icon = NsIcon.WholeBed, Toggle = false, Raised = true };
            _fullBedPill.Click += delegate { StartScan(true, true); };
            _tips.SetToolTip(_fullBedPill, "Preview the whole glass");
            _inspFoot.Controls.Add(_fullBedPill);

            _batchPill = new NsIconButton { Icon = NsIcon.Stack, Toggle = false, Raised = true };
            _batchPill.Click += delegate { ToggleBatchBar(); };
            _tips.SetToolTip(_batchPill, "Batch and hot folder");
            _inspFoot.Controls.Add(_batchPill);

            // Photoshop wears its own icon here when it is installed, because
            // this button names Photoshop rather than an action of ours.
            _toPsPill = new NsIconButton
            {
                Icon = NsIcon.Photoshop,
                Picture = AppIcon.Photoshop,
                Raised = true
            };
            _toPsPill.Click += delegate { SendToPhotoshop(); };
            _tips.SetToolTip(_toPsPill, AppIcon.Photoshop != null
                ? "Send to Photoshop"
                : "Send to Photoshop (not installed)");
            _inspFoot.Controls.Add(_toPsPill);

            _scanPill = new NsPill { Text = "Scan", Kind = PillKind.Primary, Radius = 8 };
            _scanPill.Click += delegate
            {
                // One button, two jobs: a second control that is dead 99% of the
                // time is worse than the primary action changing meaning while a
                // scan is actually running.
                if (_busy) RequestCancel();
                else StartScan(false);
            };
            _inspFoot.Controls.Add(_scanPill);
        }

        /// <summary>Rebuilds the panel for one rail section.</summary>
        void ShowSection(int index)
        {
            if (_drawerHost == null) return;

            // Asking for a section is asking to leave the settings page. The
            // rail is behind it, so only the keyboard can get here while it is
            // open, and silently changing a panel nobody can see is worse than
            // closing the page they clearly wanted out of.
            if (_settingsOpen) OpenSettings(false);

            _activeSection = Math.Max(0, Math.Min(SectionNames.Length - 1, index));
            for (int i = 0; i < _railButtons.Count; i++)
                _railButtons[i].Selected = (i == _activeSection);

            _drawerHost.SuspendLayout();
            while (_drawerHost.Controls.Count > 0)
            {
                Control previous = _drawerHost.Controls[0];
                _drawerHost.Controls.Remove(previous);
                previous.Dispose();
            }
            _themedFields.Clear();

            // Everything in the host was just disposed; these are rebuilt by the
            // section that owns them, and must not be written to before then.
            _roEstimate = null;
            _segSource = null;
            _slQuality = null;
            _qualityNote = null;
            _tgMultiPage = null;
            _findAgainPill = null;
            _presetPicker = null;
            _presetName = null;

            int y = 14;
            switch (_activeSection)
            {
                case 0: BuildPresetGroup(ref y); BuildCaptureGroup(ref y); BuildDetectGroup(ref y); break;
                case 1: BuildLookGroup(ref y); break;
                case 2: BuildBatchGroup(ref y); break;
                default: break;   // Pages and Assist are their own panels, built once
            }

            _drawerHost.ResumeLayout();
            _drawerHost.PerformLayout();

            _drawerContentHeight = y + 16;
            _drawerScrollY = 0;

            // The filmstrip is a long-lived control, so it is parented to the
            // inspector rather than rebuilt: ShowSection disposes everything it
            // finds in the host, and the session's thumbnails must outlive that.
            LayoutInspectorBody();
            if (_inspHead != null) _inspHead.Invalidate();
        }

        const int PagesSection = 3;
        const int AiSection = 4;

        void OnDrawerMouseWheel(object sender, MouseEventArgs e)
        {
            int maxScroll = Math.Max(0, _drawerContentHeight - _inspScroll.Height);
            if (maxScroll <= 0) return;
            int delta = (e.Delta > 0) ? -48 : 48;
            _drawerScrollY = Math.Max(0, Math.Min(maxScroll, _drawerScrollY + delta));
            UpdateDrawerScroll();
        }

        void UpdateDrawerScroll()
        {
            if (_drawerHost == null || _inspScroll == null) return;
            int maxScroll = Math.Max(0, _drawerContentHeight - _inspScroll.Height);
            _drawerScrollY = Math.Max(0, Math.Min(maxScroll, _drawerScrollY));
            _inspScroll.Invalidate();
            _drawerHost.SetBounds(0, -_drawerScrollY, Math.Max(1, _inspScroll.Width - 7),
                                  Math.Max(_inspScroll.Height, _drawerContentHeight));
        }

        // ---------------------------------------------------------- summaries
        string CaptureSummary()
        {
            string paper = (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                ? _supportedPaperEntries[_paperSizeIndex].Key : "Bed";
            return paper + " · " + _settings.Dpi + " dpi · " + ShortMode(_settings.Mode);
        }

        static string ShortMode(ColorMode mode)
        {
            if (mode == ColorMode.Gray8) return "Grey 8";
            if (mode == ColorMode.Gray16) return "Grey 16";
            if (mode == ColorMode.BlackWhite1) return "B&W";
            if (mode == ColorMode.Color48) return "Colour 48";
            return "Colour 24";
        }

        string DetectSummary()
        {
            if (!_settings.AutoCrop) return _settings.AutoDeskew ? "off · straighten" : "off";
            if (_cropPlan.Count > 0)
                return _cropPlan.Count + (_cropPlan.Count == 1 ? " item found" : " items found");
            return _settings.MultiRegionCrop ? "on · several items" : "on · one item";
        }

        string LookSummary()
        {
            string name = ToneEngine.Info(_settings.Tone).Name;
            if (_settings.Brightness == 0 && _settings.Contrast == 0 && _settings.Polish == 0) return name;
            return name + " · " + _settings.Polish + "/" + _settings.Brightness + "/" + _settings.Contrast;
        }

        /// <summary>
        /// OutputSummary used to be here. It was the header note for the Output
        /// rail section, which became a settings group in 1.3, and the only
        /// thing still calling it was a switch arm that was off by one.
        /// </summary>
        string AssistantSummary()
        {
            if (_aiPanel == null) return "not available";

            Ai.IAiProvider provider = Ai.AiProviders.ById(_aiPanel.ProviderId);
            if (provider == null) return "";
            if (!provider.Ready) return provider.Info.Name + " · no key";

            string model = Ai.AiModels.NameOf(provider, _aiPanel.Model);
            return provider.Info.Name + (model.Length > 0 ? " · " + model : "");
        }

        string BatchSummary()
        {
            if (_settings.BatchUntilEmpty) return "until the tray is empty";
            int pages = Math.Max(1, _settings.PageCount);
            string basis = pages == 1 ? "one sheet" : pages + " pages";
            if (_settings.BatchSeparation == SeparationRule.BlankPage) return basis + " · blank splits";
            if (_settings.BatchSeparation == SeparationRule.FixedPageCount)
                return basis + " · every " + Math.Max(1, _settings.PagesPerDocument);
            return basis;
        }

        /// <summary>
        /// The sentence above the Scan button that says what pressing it will
        /// actually do. The preview-first rule is a product promise, so it is
        /// stated where the decision is made rather than in a note further up.
        /// </summary>
        void UpdatePlan()
        {
            if (_planLabel == null) return;

            string text;
            if (!_settings.AutoCrop)
            {
                text = "Scan keeps the whole selected area at " + _settings.Dpi + " dpi.";
            }
            else if (_cropPlan.Count > 1)
            {
                text = "Scan cuts the " + _cropPlan.Count + " outlined items into separate pages.";
            }
            else if (_cropPlan.Count == 1)
            {
                text = "Scan cuts the one outlined item out of the page.";
            }
            else
            {
                text = "No preview yet — Scan keeps the whole area. Preview first to crop.";
            }

            if (_settings.OpenInPhotoshop) text += " Each page opens in Photoshop.";
            _planLabel.Text = text;

            UpdateEstimate();
            UpdateDetectReadout();

            if (_scanPill != null && !_busy)
                _scanPill.Text = _cropPlan.Count > 1 ? "Scan " + _cropPlan.Count + " items" : "Scan";

            if (_inspHead != null) _inspHead.Invalidate();
        }

        /// <summary>Kept so callers that refreshed group headers still compile.</summary>
        void UpdateGroupSummaries()
        {
            if (_inspHead != null) _inspHead.Invalidate();
        }

        /// <summary>
        /// Narrowest inspector that still shows its longest label in full.
        /// Below this every row ellipsises and the panel reads as broken rather
        /// than as compact, so the splitter stops here.
        /// </summary>
        const int MinDrawerWidth = 300;

        const int Dx = 16;

        /// <summary>Inspector width actually in use, after every clamp.</summary>
        int InspectorWidth
        {
            get
            {
                int wanted = Math.Max(MinDrawerWidth, _layout.DrawerWidth);
                int ceiling = Math.Max(MinDrawerWidth, Math.Min(520, ClientSize.Width / 3));
                int width = Math.Min(wanted, ceiling);

                // The preview is the point of the window. If the chrome would
                // squeeze it below this, the inspector gives way first.
                int strip = StripWidth;
                if (ClientSize.Width - strip - width < 360)
                    width = Math.Max(MinDrawerWidth, ClientSize.Width - strip - 360);
                return Math.Max(220, Math.Min(width, Math.Max(220, ClientSize.Width - 200)));
            }
        }

        int StripWidth
        {
            get
            {
                if (!_layout.ShowFilmstrip) return 0;
                return Math.Max(88, Math.Min(220, _layout.FilmstripWidth));
            }
        }

        /// <summary>Width available to a group, inside the scroll thumb gutter.</summary>
        int InspectorContentWidth { get { return Math.Max(160, InspectorWidth - 8); } }

        /// <summary>
        /// Content width for one control, derived from the LIVE inspector width.
        /// Using a constant meant controls kept their original width when the
        /// splitter narrowed the panel, so labels clipped.
        /// </summary>
        int Dw { get { return Math.Max(120, InspectorContentWidth - Dx * 2); } }

        /// <summary>
        /// Named jobs, at the top of the first section because recalling one is
        /// the first thing done and not the last.
        /// </summary>
        void BuildPresetGroup(ref int y)
        {
            AddSectionLabel("Presets", ref y);

            List<string> saved = StudioPresets.Names();

            // The first row is a prompt rather than a preset, so that opening
            // the list does not apply whatever happens to be at the top of it.
            List<string> shown = new List<string>();
            shown.Add(saved.Count == 0 ? "Nothing saved yet" : "Choose a preset");
            shown.AddRange(saved);

            _presetPicker = new NsDropdown { Location = new Point(Dx, y), Size = new Size(Dw, 32) };
            _presetPicker.SetItems(shown, 0);
            _presetPicker.SelectedIndexChanged += delegate
            {
                if (_presetPicker == null || _presetPicker.SelectedIndex <= 0) return;
                ApplyPreset(_presetPicker.SelectedText);
            };
            _drawerHost.Controls.Add(_presetPicker);
            y += 38;

            _presetName = new NsTextBox { Location = new Point(Dx, y), Size = new Size(Dw - 152, 32) };
            _drawerHost.Controls.Add(_presetName);
            _themedFields.Add(_presetName);

            NsPill save = new NsPill
            {
                Text = "Save",
                Kind = PillKind.Normal,
                Radius = 7,
                Location = new Point(Dx + Dw - 146, y),
                Size = new Size(70, 32)
            };
            save.Click += delegate { SavePreset(); };
            _drawerHost.Controls.Add(save);

            NsPill remove = new NsPill
            {
                Text = "Delete",
                Kind = PillKind.Quiet,
                Radius = 7,
                Location = new Point(Dx + Dw - 70, y),
                Size = new Size(70, 32)
            };
            remove.Click += delegate { DeletePreset(); };
            _drawerHost.Controls.Add(remove);
            y += 38;

            Label hint = new Label
            {
                Text = "Not the scanner, not the watched folder.",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 16),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(hint);
            y += 26;
        }

        void SavePreset()
        {
            string name = (_presetName == null ? "" : _presetName.Text).Trim();
            if (!StudioPresets.IsUsableName(name))
            {
                SetStatus(name.Length == 0
                    ? "Type a name for this preset first."
                    : "A preset name can hold letters, digits, spaces, - _ ( ) and +.");
                return;
            }

            bool replacing = StudioPresets.Exists(name);
            if (!StudioPresets.Save(name, _settings))
            {
                SetStatus("Could not write the preset.");
                return;
            }

            SetStatus(replacing ? "Preset \"" + name + "\" updated." : "Preset \"" + name + "\" saved.");
            ShowSection(_activeSection);
        }

        void DeletePreset()
        {
            // The name in the box wins over the one in the list: it is the one
            // being looked at, and the list resets to its prompt after every
            // apply.
            string name = (_presetName == null ? "" : _presetName.Text).Trim();
            if (name.Length == 0 && _presetPicker != null && _presetPicker.SelectedIndex > 0)
                name = _presetPicker.SelectedText;

            if (!StudioPresets.Exists(name)) { SetStatus("No preset by that name."); return; }

            StudioPresets.Delete(name);
            SetStatus("Preset \"" + name + "\" deleted.");
            ShowSection(_activeSection);
        }

        void ApplyPreset(string name)
        {
            if (!StudioPresets.Apply(name, _settings)) { SetStatus("Could not read that preset."); return; }

            _settings.Save();

            // The crop is part of the job, so it comes back with it, and the
            // canvas has to be told: it holds its own copy for drawing.
            _canvas.CropNorm = _settings.CropNorm;
            _manualCrop = _settings.CropNorm.Width < 0.999f || _settings.CropNorm.Height < 0.999f;
            _cropDrawnByUser = _manualCrop;

            SetStatus("Preset \"" + name + "\" applied.");

            // Rebuilt rather than individually updated: every control in this
            // section reads a field that may have just changed, and a panel that
            // shows the old numbers is worse than one that flickers.
            ShowSection(_activeSection);

            if (_presetName != null) _presetName.Text = name;

            UpdateProcessedPreview();
            UpdatePreviewCost();
            UpdateStatus();
        }

        void AddSectionLabel(string text, ref int y)
        {
            NsSection s = new NsSection { Text = text, Location = new Point(Dx, y), Size = new Size(Dw, 20) };
            _drawerHost.Controls.Add(s);
            y += 26;
        }

        Label AddFieldLabel(string text, ref int y)
        {
            Label l = new Label
            {
                Text = text,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 15),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                UseMnemonic = false,
                Font = Theme.Ui(8.25f)
            };
            _drawerHost.Controls.Add(l);
            y += 17;
            return l;
        }

        NsDropdown AddDropdown(IEnumerable<string> items, int selected, ref int y)
        {
            NsDropdown d = new NsDropdown { Location = new Point(Dx, y), Size = new Size(Dw, 34) };
            d.SetItems(items, selected);
            _drawerHost.Controls.Add(d);
            y += 42;
            return d;
        }

        NsToggle AddToggle(string text, bool on, ref int y)
        {
            NsToggle t = new NsToggle { Text = text, Checked = on, Location = new Point(Dx, y), Size = new Size(Dw, 30) };
            _drawerHost.Controls.Add(t);
            y += 34;
            return t;
        }

        NsSegment AddSegment(string[] items, int selected, ref int y)
        {
            NsSegment s = new NsSegment { Location = new Point(Dx, y), Size = new Size(Dw, 30) };
            s.Items = items;
            s.SelectedIndex = selected;
            _drawerHost.Controls.Add(s);
            y += 38;
            return s;
        }

        void BuildCaptureGroup(ref int y)
        {
            // Paper source belongs with the rest of what a scan is, not only on
            // the batch strip: choosing the feeder changes what half the other
            // settings on this panel even mean. It is only shown when the
            // scanner has somewhere else to feed from - a flatbed-only device
            // does not need a control with one usable option.
            if (_hasFeeder)
            {
                AddFieldLabel("Source", ref y);
                List<string> sources = new List<string> { "Flatbed", "Feeder" };
                if (_hasDuplex) sources.Add("2-sided");

                int chosen = Math.Min(sources.Count - 1, SourceToIndex(_settings.Source));
                _segSource = AddSegment(sources.ToArray(), chosen, ref y);
                _segSource.SelectedIndexChanged += delegate
                {
                    _settings.Source = IndexToSource(_segSource.SelectedIndex);
                    if (_deviceBar != null) _deviceBar.SourceIndex = SourceToIndex(_settings.Source);
                    UpdateBatchFeedEnabled();
                    UpdateStatus();
                };
            }

            AddFieldLabel("Paper size", ref y);
            _ddPaperSize = AddDropdown(_supportedPaperLabels, _paperSizeIndex, ref y);
            _ddPaperSize.SelectedIndexChanged += delegate
            {
                int i = _ddPaperSize.SelectedIndex;
                if (i >= 0 && i < _supportedPaperEntries.Count)
                {
                    _paperSizeIndex = i;
                    ApplyPaperSizeEntry(_supportedPaperEntries[i]);
                }
            };

            _segOrientation = AddSegment(new string[] { "Portrait", "Landscape" }, _settings.PaperLandscape ? 1 : 0, ref y);
            _segOrientation.SelectedIndexChanged += delegate
            {
                _settings.PaperLandscape = (_segOrientation.SelectedIndex == 1);
                if (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                {
                    ApplyPaperSizeEntry(_supportedPaperEntries[_paperSizeIndex]);
                }
            };

            AddFieldLabel("Resolution", ref y);
            int dpiIndex = _supportedDpiValues.IndexOf(_settings.Dpi);
            if (dpiIndex < 0)
            {
                dpiIndex = _supportedDpiValues.IndexOf(300);
                if (dpiIndex < 0 && _supportedDpiValues.Count > 0)
                    dpiIndex = _supportedDpiValues.Count - 1;
                if (dpiIndex >= 0) _settings.Dpi = _supportedDpiValues[dpiIndex];
            }

            _ddDpi = AddDropdown(_supportedDpiLabels, dpiIndex, ref y);
            _ddDpi.SelectedIndexChanged += delegate
            {
                int i = _ddDpi.SelectedIndex;
                if (i >= 0 && i < _supportedDpiValues.Count) _settings.Dpi = _supportedDpiValues[i];
                if (_canvas != null) { _canvas.PlaceholderDpi = _settings.Dpi; _canvas.Invalidate(); }
            };

            AddFieldLabel("Colour", ref y);
            _ddMode = AddDropdown(new string[] { "Colour 24-bit", "Colour 48-bit", "Grey 8-bit", "Grey 16-bit", "Black & white" },
                                  ModeToIndex(_settings.Mode), ref y);
            _ddMode.SelectedIndexChanged += delegate
            {
                _settings.Mode = IndexToMode(_ddMode.SelectedIndex);
                UpdateProcessedPreview();
            };

            _tgVendorUi = AddToggle("Use the scanner's own dialog", _settings.ShowVendorUi, ref y);
            _tgVendorUi.CheckedChanged += delegate { _settings.ShowVendorUi = _tgVendorUi.Checked; };

            // What these four settings actually cost. Resolution is the one
            // people over-set, and a number here is worth more than any warning
            // after the fact.
            y += 6;
            _roEstimate = new NsReadout
            {
                Caption = "uncompressed page",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30)
            };
            _drawerHost.Controls.Add(_roEstimate);
            y += 36;
            UpdateEstimate();


        }

        /// <summary>
        /// Finding the edges of what is on the glass, straightening it, and
        /// where it goes afterwards. Kept together because they are one
        /// decision: what comes out of this scan and in what shape.
        /// </summary>
        void BuildDetectGroup(ref int y)
        {
            _tgAutoCrop = AddToggle("Find items on the glass", _settings.AutoCrop, ref y);
            _tgAutoCrop.CheckedChanged += delegate
            {
                _settings.AutoCrop = _tgAutoCrop.Checked;
                UpdateCropTogglesEnabled();

                // Switching it back on looks again straight away. Leaving the
                // operator to discover that another preview was needed is what
                // made this switch feel like it did nothing.
                RedetectFromPreview("auto crop was switched off");
            };

            // Only meaningful once the edges are being found at all, so it is
            // greyed rather than hidden when they are not.
            _tgMultiCrop = AddToggle("Separate several items", _settings.MultiRegionCrop, ref y);
            _tgMultiCrop.CheckedChanged += delegate
            {
                _settings.MultiRegionCrop = _tgMultiCrop.Checked;
                RedetectFromPreview("the separation setting changed");
            };

            UpdateCropTogglesEnabled();

            _tgAutoDeskew = AddToggle("Straighten automatically", _settings.AutoDeskew, ref y);
            _tgAutoDeskew.CheckedChanged += delegate
            {
                _settings.AutoDeskew = _tgAutoDeskew.Checked;
                RedetectFromPreview("straightening changed");
            };

            // Handing the page to Photoshop is part of deciding what this scan
            // is for, not part of configuring where files are filed - and it is
            // switched far more often than anything on the Output page. It
            // belongs where the operator already is when they press Scan.
            AddSectionLabel("After scanning", ref y);
            _tgOpenPs = AddToggle("Open in Photoshop", _settings.OpenInPhotoshop, ref y);
            _tgOpenPs.CheckedChanged += delegate { _settings.OpenInPhotoshop = _tgOpenPs.Checked; };

            // An explicit way back. A hand-drawn selection clears the plan on
            // purpose - it is a deliberate override - but there was no way to
            // undo that short of previewing the whole glass again.
            y += 6;
            _findAgainPill = new NsPill
            {
                Text = "Find items again",
                Glyph = "\u21ba",
                Kind = PillKind.Normal,
                Radius = 7,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 32)
            };
            _findAgainPill.Click += delegate { RedetectFromPreview("asked to look again"); };
            _drawerHost.Controls.Add(_findAgainPill);
            y += 40;

            // A standing notice about when cropping happens used to sit here. It
            // said the same thing as the line above the Preview button, which is
            // written from the current state and changes as the state does, so
            // the fixed copy was one more thing to read past every time the
            // panel opened without ever telling the operator anything new.

            UpdateDetectReadout();
        }

        void UpdateEstimate()
        {
            if (_roEstimate == null) return;

            double wide = _settings.RegionWidthIn > 0.05 ? _settings.RegionWidthIn : _activeBedW;
            double high = _settings.RegionHeightIn > 0.05 ? _settings.RegionHeightIn : _activeBedH;
            double pixels = wide * _settings.Dpi * high * _settings.Dpi;

            double bytesPerPixel;
            switch (_settings.Mode)
            {
                case ColorMode.Color48: bytesPerPixel = 6; break;
                case ColorMode.Gray8: bytesPerPixel = 1; break;
                case ColorMode.Gray16: bytesPerPixel = 2; break;
                case ColorMode.BlackWhite1: bytesPerPixel = 0.125; break;
                default: bytesPerPixel = 3; break;
            }

            double megabytes = pixels * bytesPerPixel / (1024.0 * 1024.0);
            string size = megabytes >= 1024
                ? (megabytes / 1024.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
                : megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB";

            _roEstimate.Value = size;

            // Anything that will not fit in a 32-bit process comfortably is
            // worth flagging before the scan rather than after it fails.
            _roEstimate.ValueColor = megabytes > 900 ? Theme.Warn : Color.Empty;
            _roEstimate.Invalidate();
        }

        /// <summary>
        /// Keeps "find items again" from offering to do something it cannot.
        ///
        /// This used to drive a readout beside it as well, saying how the last
        /// preview went. The line above the Preview button says that from the
        /// same facts and updates on the same events, so the readout was a
        /// second copy that could only ever agree with the first.
        /// </summary>
        void UpdateDetectReadout()
        {
            if (_findAgainPill != null)
                _findAgainPill.Enabled = _settings.AutoCrop && _previewPage != null;
        }

        void BuildLookGroup(ref int y)
        {
            AddSectionLabel("Paper", ref y);

            _slWhiten = new NsSlider
            {
                Label = "Paper polish",
                Suffix = "%",
                Minimum = 0,
                Maximum = 100,
                DefaultValue = 0,
                Value = _settings.Polish,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _slWhiten.ValueChanged += delegate
            {
                _settings.Polish = _slWhiten.Value;
                UpdateProcessedPreview();
            };
            _drawerHost.Controls.Add(_slWhiten);
            y += 52;

            AddSectionLabel("Tone", ref y);

            AddFieldLabel("Preset", ref y);

            List<string> presetNames = new List<string>();
            int presetIndex = 0;
            for (int i = 0; i < ToneEngine.Presets.Length; i++)
            {
                presetNames.Add(ToneEngine.Presets[i].Name);
                if (ToneEngine.Presets[i].Preset == _settings.Tone) presetIndex = i;
            }

            _ddTonePreset = AddDropdown(presetNames, presetIndex, ref y);

            // Preset names alone do not tell an operator which one to pick, and
            // the difference between "Document colour" and "Greyscale" matters
            // more than either name suggests.
            Label presetNote = new Label
            {
                Text = ToneEngine.Info(_settings.Tone).Description,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(7.75f)
            };
            _drawerHost.Controls.Add(presetNote);
            y += 34;

            _ddTonePreset.SelectedIndexChanged += delegate
            {
                int i = _ddTonePreset.SelectedIndex;
                if (i < 0 || i >= ToneEngine.Presets.Length) return;

                TonePresetInfo info = ToneEngine.Presets[i];
                _settings.Tone = info.Preset;

                // Choosing a preset resets the polish to what that preset is for.
                // Carrying a document-strength lift into Photo would undo the
                // point of picking Photo.
                _settings.Polish = info.Polish;
                if (_slWhiten != null) _slWhiten.Value = info.Polish;

                presetNote.Text = info.Description;
                UpdateThresholdEnabled();
                UpdateProcessedPreview();
            };

            _slBright = new NsSlider
            {
                Label = "Brightness",
                Suffix = "%",
                Minimum = -100,
                Maximum = 100,
                DefaultValue = 0,
                Value = _settings.Brightness,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _slBright.ValueChanged += delegate
            {
                _settings.Brightness = _slBright.Value;
                UpdateProcessedPreview();
            };
            _drawerHost.Controls.Add(_slBright);
            y += 52;

            _slContrast = new NsSlider
            {
                Label = "Contrast",
                Suffix = "%",
                Minimum = -100,
                Maximum = 100,
                DefaultValue = 0,
                Value = _settings.Contrast,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _slContrast.ValueChanged += delegate
            {
                _settings.Contrast = _slContrast.Value;
                UpdateProcessedPreview();
            };
            _drawerHost.Controls.Add(_slContrast);
            y += 52;

            _tgAdaptive = AddToggle("Threshold per area", _settings.AdaptiveThreshold, ref y);
            _tgAdaptive.CheckedChanged += delegate
            {
                _settings.AdaptiveThreshold = _tgAdaptive.Checked;
                UpdateThresholdEnabled();
                UpdateProcessedPreview();
            };

            _slBwThreshold = new NsSlider
            {
                Label = "B&W Threshold",
                Suffix = "",
                Minimum = 10,
                Maximum = 245,
                DefaultValue = 128,
                Value = _settings.BwThreshold,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _slBwThreshold.ValueChanged += delegate
            {
                _settings.BwThreshold = _slBwThreshold.Value;
                UpdateProcessedPreview();
            };
            _drawerHost.Controls.Add(_slBwThreshold);
            UpdateThresholdEnabled();
            y += 54;

            // ---------------------------------------------------------------
            // Colour
            // ---------------------------------------------------------------
            AddSectionLabel("Colour", ref y);

            // The one control here that measures the page instead of taking a
            // number, so it goes first and everything under it is a correction
            // applied on top of what it decided.
            AddFieldLabel("Automatic", ref y);
            string[] autoNames = { "Off", "Contrast only", "Remove colour cast", "Cast and midtones" };
            int autoIndex = (int)_settings.AutoTone;
            NsDropdown ddAuto = AddDropdown(autoNames, autoIndex, ref y);

            Label autoNote = new Label
            {
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f)
            };
            _drawerHost.Controls.Add(autoNote);
            y += 34;

            EventHandler sayWhatAutoDoes = delegate
            {
                switch (_settings.AutoTone)
                {
                    case AutoTone.Contrast:
                        autoNote.Text = "Fills the tonal range. Colour is left exactly as found."; break;
                    case AutoTone.Colour:
                        autoNote.Text = "Stretches each channel, which takes out a lamp cast."; break;
                    case AutoTone.Full:
                        autoNote.Text = "Also pulls midtones neutral. Can flatten a genuinely one-colour page."; break;
                    default:
                        autoNote.Text = "Nothing is measured; only the settings below apply."; break;
                }
            };
            sayWhatAutoDoes(null, EventArgs.Empty);

            ddAuto.SelectedIndexChanged += delegate
            {
                _settings.AutoTone = (AutoTone)ddAuto.SelectedIndex;
                sayWhatAutoDoes(null, EventArgs.Empty);
                UpdateProcessedPreview();
            };

            NsSlider slVibrance = AddToneSlider("Vibrance", _settings.Vibrance, ref y);
            slVibrance.ValueChanged += delegate
            { _settings.Vibrance = slVibrance.Value; UpdateProcessedPreview(); };

            NsSlider slSaturation = AddToneSlider("Saturation", _settings.Saturation, ref y);
            slSaturation.ValueChanged += delegate
            { _settings.Saturation = slSaturation.Value; UpdateProcessedPreview(); };

            NsSlider slTemperature = AddToneSlider("Warmth", _settings.Temperature, ref y);
            slTemperature.ValueChanged += delegate
            { _settings.Temperature = slTemperature.Value; UpdateProcessedPreview(); };

            NsSlider slTint = AddToneSlider("Tint", _settings.Tint, ref y);
            slTint.ValueChanged += delegate
            { _settings.Tint = slTint.Value; UpdateProcessedPreview(); };

            // Vibrance above saturation, and the recovery pair below, because
            // that is the order they are reached for: add colour, then rescue
            // the ends if adding it cost anything.
            AddSectionLabel("Recovery", ref y);

            NsSlider slHighlights = AddToneSlider("Highlights", _settings.Highlights, ref y);
            slHighlights.ValueChanged += delegate
            { _settings.Highlights = slHighlights.Value; UpdateProcessedPreview(); };

            NsSlider slShadows = AddToneSlider("Shadows", _settings.Shadows, ref y);
            slShadows.ValueChanged += delegate
            { _settings.Shadows = slShadows.Value; UpdateProcessedPreview(); };

            // ---------------------------------------------------------------
            // Clean-up
            // ---------------------------------------------------------------
            AddSectionLabel("Clean-up", ref y);

            // Screen ruling, not a strength. A printed dot is a fixed size on
            // paper, so what the filter needs to know is what was printed, and
            // it works out the rest from the scan resolution.
            AddFieldLabel("Printed original", ref y);
            string[] screenNames = { "No (continuous tone)", "Newspaper  85 lpi",
                                     "Magazine  133 lpi", "Fine printing  175 lpi" };
            int[] screenLpi = { 0, 85, 133, 175 };
            int screenIndex = 0;
            for (int i = 0; i < screenLpi.Length; i++)
                if (screenLpi[i] == _settings.DescreenLpi) screenIndex = i;

            NsDropdown ddScreen = AddDropdown(screenNames, screenIndex, ref y);
            ddScreen.SelectedIndexChanged += delegate
            {
                int i = Math.Max(0, Math.Min(screenLpi.Length - 1, ddScreen.SelectedIndex));
                _settings.DescreenLpi = screenLpi[i];
                UpdateProcessedPreview();
            };

            Label screenNote = new Label
            {
                Text = "Removes the dot grid from something that was printed.",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f)
            };
            _drawerHost.Controls.Add(screenNote);
            y += 34;

            NsSlider slBackground = AddZeroSlider("Flatten paper", _settings.BackgroundClean, ref y);
            slBackground.ValueChanged += delegate
            { _settings.BackgroundClean = slBackground.Value; UpdateProcessedPreview(); };

            NsSlider slDespeckle = AddZeroSlider("Despeckle", _settings.Despeckle, ref y);
            slDespeckle.ValueChanged += delegate
            { _settings.Despeckle = slDespeckle.Value; UpdateProcessedPreview(); };

            // Last in the panel because it is last in the pipeline, and the
            // reason is worth seeing: sharpening a halftone before the descreen
            // has removed it sharpens the dots.
            NsSlider slSharpen = AddZeroSlider("Sharpen", _settings.Sharpen, ref y);
            slSharpen.ValueChanged += delegate
            { _settings.Sharpen = slSharpen.Value; UpdateProcessedPreview(); };

            NsPill btnReset = new NsPill
            {
                Text = "Reset Adjustments",
                Glyph = "↺",
                Kind = PillKind.Normal,
                Radius = 6,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 34)
            };
            btnReset.Click += delegate
            {
                _settings.Polish = ToneEngine.Info(_settings.Tone).Polish;
                if (_slWhiten != null) _slWhiten.Value = 0;
                _settings.Brightness = 0;
                if (_slBright != null) _slBright.Value = 0;
                _settings.Contrast = 0;
                if (_slContrast != null) _slContrast.Value = 0;
                _settings.BwThreshold = 128;
                if (_slBwThreshold != null) _slBwThreshold.Value = 128;

                _settings.AutoTone = AutoTone.Off;
                ddAuto.SelectedIndex = 0;
                _settings.Saturation = 0; slSaturation.Value = 0;
                _settings.Vibrance = 0; slVibrance.Value = 0;
                _settings.Temperature = 0; slTemperature.Value = 0;
                _settings.Tint = 0; slTint.Value = 0;
                _settings.Highlights = 0; slHighlights.Value = 0;
                _settings.Shadows = 0; slShadows.Value = 0;

                _settings.DescreenLpi = 0; ddScreen.SelectedIndex = 0;
                _settings.BackgroundClean = 0; slBackground.Value = 0;
                _settings.Despeckle = 0; slDespeckle.Value = 0;
                _settings.Sharpen = 0; slSharpen.Value = 0;

                    _settings.Tone = TonePreset.OriginalColour;
                if (_ddTonePreset != null) _ddTonePreset.SelectedIndex = 0;
                UpdateProcessedPreview();
            };
            _drawerHost.Controls.Add(btnReset);
            y += 46;
        }

        /// <summary>
        /// A -100..100 adjustment slider, centred on zero because every one of
        /// these does nothing there and can go either way from it.
        /// </summary>
        NsSlider AddToneSlider(string label, int value, ref int y)
        {
            NsSlider slider = new NsSlider
            {
                Label = label,
                Suffix = "",
                Minimum = -100,
                Maximum = 100,
                DefaultValue = 0,
                Value = value,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _drawerHost.Controls.Add(slider);
            y += 54;
            return slider;
        }

        /// <summary>A 0..100 slider, for the things that only go one way.</summary>
        NsSlider AddZeroSlider(string label, int value, ref int y)
        {
            NsSlider slider = new NsSlider
            {
                Label = label,
                Suffix = "%",
                Minimum = 0,
                Maximum = 100,
                DefaultValue = 0,
                Value = value,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _drawerHost.Controls.Add(slider);
            y += 54;
            return slider;
        }

        void BuildOutputGroup(ref int y)
        {
            // It had no heading of its own while it was a rail section, because
            // the rail was the heading. Inside Settings every other group names
            // itself, and a nameless one reads as part of whatever is above it.
            AddSectionLabel("Output", ref y);

            AddFieldLabel("Format", ref y);
            string[] formats = { "JPEG", "PNG", "TIFF", "PDF" };
            int fi = 0;
            string f = (_settings.OutputFormat ?? "jpg").ToLowerInvariant();
            if (f.StartsWith("png")) fi = 1;
            else if (f.StartsWith("tif")) fi = 2;
            else if (f.StartsWith("pdf")) fi = 3;

            _ddFormat = AddDropdown(formats, fi, ref y);
            _ddFormat.SelectedIndexChanged += delegate
            {
                switch (_ddFormat.SelectedIndex)
                {
                    case 1: _settings.OutputFormat = "png"; break;
                    case 2: _settings.OutputFormat = "tif"; break;
                    case 3: _settings.OutputFormat = "pdf"; break;
                    default: _settings.OutputFormat = "jpg"; break;
                }
                if (_namePreview != null) _namePreview(null, EventArgs.Empty);
                if (_tgMultiPage != null)
                    _tgMultiPage.Enabled = PageWriter.SupportsMultiPage(_settings.OutputFormat);
                UpdateQualityEnabled();
                UpdateStatus();
            };

            // Whether several pages land in one file is a property of the
            // container, so it belongs beside the format that decides whether
            // that is even possible - not two panels away under Batch.
            _tgMultiPage = AddToggle("One file for the whole job", _settings.MultiPageFile, ref y);
            _tgMultiPage.Enabled = PageWriter.SupportsMultiPage(_settings.OutputFormat);
            _tgMultiPage.CheckedChanged += delegate { _settings.MultiPageFile = _tgMultiPage.Checked; };

            AddSectionLabel("Colour profile", ref y);

            Label profileNow = new Label
            {
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f)
            };
            _drawerHost.Controls.Add(profileNow);
            y += 34;

            EventHandler sayWhichProfile = delegate
            {
                string chosen = _settings.ColorProfilePath;
                profileNow.Text = string.IsNullOrEmpty(chosen)
                    ? "The scanner's own, if it names one. Otherwise sRGB."
                    : "Using " + Path.GetFileName(chosen);
            };
            sayWhichProfile(null, EventArgs.Empty);

            NsPill pickProfile = new NsPill
            {
                Text = "Choose a profile",
                Kind = PillKind.Normal,
                Radius = 7,
                Location = new Point(Dx, y),
                Size = new Size(Dw - 84, 32)
            };
            pickProfile.Click += delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog())
                {
                    dialog.Title = "Choose a colour profile for this scanner";
                    dialog.Filter = "ICC profiles (*.icc;*.icm)|*.icc;*.icm|All files (*.*)|*.*";
                    try
                    {
                        dialog.InitialDirectory = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System),
                            @"spool\drivers\color");
                    }
                    catch { }

                    if (dialog.ShowDialog(this) != DialogResult.OK) return;

                    // Checked before it is kept, not when a scan is delivered.
                    // Finding out then means finding out from a page that went
                    // out claiming to know what its colours mean.
                    if (IccProfile.Load(dialog.FileName) == null)
                    {
                        SetStatus("That file is not a colour profile.");
                        return;
                    }

                    _settings.ColorProfilePath = dialog.FileName;
                    sayWhichProfile(null, EventArgs.Empty);
                    SetStatus("Colour profile set to " + Path.GetFileName(dialog.FileName) + ".");
                }
            };
            _drawerHost.Controls.Add(pickProfile);

            NsPill clearProfile = new NsPill
            {
                Text = "Clear",
                Kind = PillKind.Quiet,
                Radius = 7,
                Location = new Point(Dx + Dw - 78, y),
                Size = new Size(78, 32)
            };
            clearProfile.Click += delegate
            {
                _settings.ColorProfilePath = "";
                sayWhichProfile(null, EventArgs.Empty);
                SetStatus("Back to the scanner's own profile, or sRGB.");
            };
            _drawerHost.Controls.Add(clearProfile);
            y += 44;

            AddFieldLabel("Name pattern", ref y);
            NsTextBox pattern = new NsTextBox
            {
                Text = _settings.OutputNamePattern,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30)
            };
            Label preview = null;
            EventHandler refreshPreview = delegate
            {
                if (preview == null) return;
                string counterFormat;
                string shown = NameTemplate.Expand(pattern.Text, ExportPlanFromSettings().Context,
                                                   out counterFormat);
                if (counterFormat != null)
                    shown = shown.Replace("{#}", new string('0', counterFormat.Length - 1) + "1");
                preview.Text = shown + "." + (_settings.OutputFormat ?? "jpg").ToLowerInvariant();
            };
            pattern.TextCommitted += delegate
            {
                string v = (pattern.Text ?? "").Trim();
                // An empty pattern would resolve to a bare extension. Rather than
                // rejecting it with a dialog, fall back to the default and show
                // that in the field, so the state on screen is the state in use.
                if (v.Length == 0) { v = "scan_{date}_{nnn}"; pattern.Text = v; }
                _settings.OutputNamePattern = v;
                refreshPreview(null, EventArgs.Empty);
            };
            _drawerHost.Controls.Add(pattern);
            _themedFields.Add(pattern);
            y += 34;

            // The tokens are unguessable, and a pattern is the kind of setting
            // people abandon rather than look up. Showing the resulting filename
            // as they type is cheaper than any amount of help text.
            preview = new Label
            {
                Location = new Point(Dx, y),
                Size = new Size(Dw, 18),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(preview);
            _namePreview = refreshPreview;
            refreshPreview(null, EventArgs.Empty);
            y += 22;

            Label tokens = new Label
            {
                Text = @"{date} {time} {nnn} {device} {dpi} {mode} {doc}   \ makes subfolders",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 16),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(7.5f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(tokens);
            y += 22;

            AddFieldLabel("Folder", ref y);
            Label dir = new Label
            {
                Text = _settings.OutputDirectory,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 20),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8.5f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(dir);
            y += 26;

            NsPill browse = new NsPill
            {
                Text = "Choose folder…",
                Kind = PillKind.Normal,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 32)
            };
            browse.Click += delegate
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.SelectedPath = _settings.OutputDirectory;
                    if (fb.ShowDialog(this) == DialogResult.OK)
                    {
                        _settings.OutputDirectory = fb.SelectedPath;
                        dir.Text = fb.SelectedPath;
                    }
                }
            };
            _drawerHost.Controls.Add(browse);
            y += 46;

            // The setting was already read, persisted and passed all the way to
            // the encoder; it simply had nowhere to be set from. 92 is a good
            // default, but a 600 dpi archive scan and a photograph of a receipt
            // do not want the same number.
            AddSectionLabel("Compression", ref y);
            _slQuality = new NsSlider
            {
                Label = "JPEG quality",
                Suffix = "%",
                Minimum = 40,
                Maximum = 100,
                DefaultValue = 92,
                Value = Math.Max(40, Math.Min(100, _settings.JpegQuality)),
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            _slQuality.ValueChanged += delegate { _settings.JpegQuality = _slQuality.Value; };
            _drawerHost.Controls.Add(_slQuality);
            y += 50;

            _qualityNote = new Label
            {
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(7.75f)
            };
            _drawerHost.Controls.Add(_qualityNote);
            y += 34;

            UpdateQualityEnabled();
        }

        /// <summary>
        /// JPEG quality only reaches the file for the two formats that carry a
        /// JPEG stream. Greying it under PNG and TIFF is more honest than
        /// leaving a live control that changes nothing.
        /// </summary>
        void UpdateQualityEnabled()
        {
            if (_slQuality == null) return;

            string format = (_settings.OutputFormat ?? "jpg").ToLowerInvariant();
            bool applies = format.StartsWith("jpg") || format.StartsWith("jpeg") || format.StartsWith("pdf");

            _slQuality.Enabled = applies;
            if (_qualityNote != null)
            {
                _qualityNote.Text = applies
                    ? "Applies to JPEG files and to the images inside a PDF."
                    : format.ToUpperInvariant() + " is lossless, so quality does not apply.";
                _qualityNote.Invalidate();
            }
        }

        /// <summary>How many sheets this job is, and how they become documents.</summary>
        void BuildBatchGroup(ref int y)
        {
            _tgUntilEmpty = AddToggle("Feed until the tray is empty", _settings.BatchUntilEmpty, ref y);
            _tgUntilEmpty.CheckedChanged += delegate
            {
                _settings.BatchUntilEmpty = _tgUntilEmpty.Checked;
                UpdateBatchFeedEnabled();
            };

            NsSlider slPages = new NsSlider
            {
                Label = "Pages to scan",
                Minimum = 1,
                Maximum = 200,
                DefaultValue = 1,
                Value = Math.Max(1, _settings.PageCount),
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            slPages.ValueChanged += delegate { _settings.PageCount = slPages.Value; };
            _drawerHost.Controls.Add(slPages);
            y += 52;

            // Both only apply to a feeder. Rather than hiding them - which makes
            // the panel jump around as the source changes - they are disabled,
            // so the operator can still see what a feeder would offer.
            _batchFeedControls.Add(_tgUntilEmpty);
            _batchFeedControls.Add(slPages);
            UpdateBatchFeedEnabled();

            _tgDropBlank = AddToggle("Skip blank pages", _settings.DropBlankPages, ref y);
            _tgDropBlank.CheckedChanged += delegate { _settings.DropBlankPages = _tgDropBlank.Checked; };

            AddFieldLabel("Split into documents", ref y);
            string[] rules = { "No - one document", "Every N pages", "On a blank sheet" };
            int ri = _settings.BatchSeparation == SeparationRule.FixedPageCount ? 1
                   : _settings.BatchSeparation == SeparationRule.BlankPage ? 2 : 0;

            NsDropdown ddSplit = AddDropdown(rules, ri, ref y);
            NsSlider slPerDoc = null;

            EventHandler applySplit = delegate
            {
                switch (ddSplit.SelectedIndex)
                {
                    case 1: _settings.BatchSeparation = SeparationRule.FixedPageCount; break;
                    case 2: _settings.BatchSeparation = SeparationRule.BlankPage; break;
                    default: _settings.BatchSeparation = SeparationRule.None; break;
                }
                // The page count only means anything for the fixed-count rule;
                // leaving it live under the other two invites the operator to set
                // a value that is then quietly ignored.
                if (slPerDoc != null)
                    slPerDoc.Enabled = _settings.BatchSeparation == SeparationRule.FixedPageCount;
            };
            ddSplit.SelectedIndexChanged += applySplit;

            slPerDoc = new NsSlider
            {
                Label = "Pages per document",
                Minimum = 1,
                Maximum = 50,
                DefaultValue = 1,
                Value = Math.Max(1, _settings.PagesPerDocument),
                Location = new Point(Dx, y),
                Size = new Size(Dw, 44)
            };
            slPerDoc.ValueChanged += delegate { _settings.PagesPerDocument = slPerDoc.Value; };
            _drawerHost.Controls.Add(slPerDoc);
            y += 52;

            applySplit(null, EventArgs.Empty);

        }

        /// <summary>A folder NextScan watches and processes on its own.</summary>
        /// <summary>
        /// What this build is and where it writes when something goes wrong.
        ///
        /// Every support conversation starts with these three facts, and asking
        /// someone to find a log folder they have never heard of is how those
        /// conversations stall.
        /// </summary>
        void BuildAboutSettings(ref int y)
        {
            AddSectionLabel("About", ref y);

            AddReadout("Version", AppInfo.Product + " " + AppInfo.Version, ref y);

            // The build time as well as the version. Two builds can carry the
            // same version while one of them is the one with the fix in it, and
            // during development that is most of them.
            string built = "unknown";
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    built = File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
            }
            catch { }
            AddReadout("Build", built, ref y);

            AddReadout(".NET Framework", Environment.Version.ToString(), ref y);

            // Whether the model layer is actually there. It is two files and a
            // native DLL that are not in the repository, and when they are
            // missing nothing breaks -- detection is simply worse, quietly. The
            // one place that can say so plainly is here.
            AddReadout("Detection model", ModelStatus(), ref y);

            // Not a readout: a caption on the left and a value on the right
            // needs the value to be short, and a repository address is not.
            // Given its own line it simply fits.
            AddFieldLabel("Source", ref y);
            Label source = new Label
            {
                Text = AppInfo.Repository,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 18),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(source);
            y += 26;

            AddFieldLabel("Diagnostics are written to", ref y);
            Label folder = new Label
            {
                Text = DiagnosticsFolder(),
                Location = new Point(Dx, y),
                Size = new Size(Dw, 18),
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(folder);
            y += 24;

            NsPill open = new NsPill
            {
                Text = "Open that folder",
                Kind = PillKind.Normal,
                Radius = 7,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 32)
            };
            open.Click += delegate
            {
                try
                {
                    string path = DiagnosticsFolder();
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
                catch { SetStatus("Could not open the diagnostics folder."); }
            };
            _drawerHost.Controls.Add(open);
            y += 40;
        }

        /// <summary>
        /// The shortcuts, read off the same list the keyboard uses.
        /// </summary>
        void BuildShortcutSettings(ref int y)
        {
            AddSectionLabel("Keyboard", ref y);

            string group = "";
            foreach (Shortcut shortcut in Shortcuts())
            {
                if (shortcut.Group != group)
                {
                    group = shortcut.Group;
                    AddFieldLabel(group, ref y);
                }
                AddReadout(shortcut.Does, shortcut.Keys_, ref y);
            }
            y += 4;
        }

        void AddReadout(string caption, string value, ref int y)
        {
            NsReadout readout = new NsReadout
            {
                Caption = caption,
                Value = value,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30)
            };
            _drawerHost.Controls.Add(readout);
            y += 36;
        }

        /// <summary>
        /// Says which half is missing when the model layer is off, because the
        /// two have different answers: a missing runtime is a broken install, a
        /// missing model file is a payload that was never fetched.
        /// </summary>
        string ModelStatus()
        {
            try
            {
                string encoder, decoder;
                SamProposals.Locate(out encoder, out decoder);

                if (!File.Exists(encoder) || !File.Exists(decoder)) return "not installed";
                if (!Ort.Available) return Ort.Availability;
                return _settings.UseModel ? "ready" : "installed, switched off";
            }
            catch { return "unknown"; }
        }

        static string DiagnosticsFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NextScan", "diagnostics");
        }

        void BuildWatchGroup(ref int y)
        {
            AddSectionLabel("Watched folder", ref y);

            _hotPathLabel = new Label
            {
                Text = HotPathText(),
                Location = new Point(Dx, y),
                Size = new Size(Dw, 20),
                ForeColor = Theme.Text,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8.5f),
                AutoEllipsis = true
            };
            _drawerHost.Controls.Add(_hotPathLabel);
            y += 26;

            NsPill pickHot = new NsPill
            {
                Text = "Choose folder to watch\u2026",
                Kind = PillKind.Normal,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 32)
            };
            pickHot.Click += delegate
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    if (Directory.Exists(_settings.HotFolderPath)) fb.SelectedPath = _settings.HotFolderPath;
                    if (fb.ShowDialog(this) != DialogResult.OK) return;

                    // Changing the folder while one is running would leave the
                    // old watcher on the old path with no way to reach it.
                    StopHotFolder();
                    _settings.HotFolderPath = fb.SelectedPath;
                    _hotPathLabel.Text = HotPathText();
                    UpdateHotFolderUi();
                }
            };
            _drawerHost.Controls.Add(pickHot);
            y += 40;

            NsToggle tgHotGroup = AddToggle("One document per drop",
                                            _settings.HotFolderGroupPerBatch, ref y);
            tgHotGroup.CheckedChanged += delegate
            {
                _settings.HotFolderGroupPerBatch = tgHotGroup.Checked;
            };

            AddFieldLabel("Once processed, the original is", ref y);
            string[] dispositions = { "Moved to _processed", "Deleted", "Left where it is" };
            int di = _settings.HotFolderDisposition == SourceDisposition.Delete ? 1
                   : _settings.HotFolderDisposition == SourceDisposition.LeaveInPlace ? 2 : 0;
            NsDropdown ddHotSource = AddDropdown(dispositions, di, ref y);
            ddHotSource.SelectedIndexChanged += delegate
            {
                switch (ddHotSource.SelectedIndex)
                {
                    case 1: _settings.HotFolderDisposition = SourceDisposition.Delete; break;
                    case 2: _settings.HotFolderDisposition = SourceDisposition.LeaveInPlace; break;
                    default: _settings.HotFolderDisposition = SourceDisposition.MoveToSubfolder; break;
                }
            };

            _hotPill = new NsPill
            {
                Text = "Start watching",
                Kind = PillKind.Primary,
                Location = new Point(Dx, y),
                Size = new Size(Dw, 36)
            };
            _hotPill.Click += delegate
            {
                if (_hot != null) StopHotFolder();
                else StartHotFolder();
            };
            _drawerHost.Controls.Add(_hotPill);
            y += 42;

            _hotStatus = new Label
            {
                Text = "",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 32),
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f)
            };
            _drawerHost.Controls.Add(_hotStatus);
            y += 38;

            UpdateHotFolderUi();

            // Recovering and saving the session moved to the Pages panel, where
            // the pages themselves are. Keeping the file on disk is a standing
            // preference, so it stays here.
            AddSectionLabel("Files", ref y);
            _tgKeep = AddToggle("Keep the file on disk", _settings.KeepFiles, ref y);
            _tgKeep.CheckedChanged += delegate { _settings.KeepFiles = _tgKeep.Checked; };
            y += 8;
        }

        /// <summary>
        /// What a preview costs and what it is worth.
        ///
        /// A preview is a different job from a scan: it is looked at and thrown
        /// away, and it is what auto crop measures. Too coarse and small items
        /// are missed; too fine and every press of Preview spends seconds of
        /// lamp time on detail nobody will see. The two are worth separating,
        /// and the operator is the one who knows which way their bed leans.
        /// </summary>
        void BuildPreviewSettings(ref int y)
        {
            AddSectionLabel("Preview", ref y);

            NsToggle matchScan = AddToggle("Use the scan resolution", _settings.PreviewMatchesScan, ref y);

            AddFieldLabel("Resolution", ref y);
            int chosen = _supportedDpiValues.IndexOf(_settings.PreviewDpi);
            if (chosen < 0)
            {
                chosen = _supportedDpiValues.IndexOf(100);
                if (chosen < 0 && _supportedDpiValues.Count > 0) chosen = 0;
            }
            NsDropdown previewDpi = AddDropdown(_supportedDpiLabels, chosen, ref y);
            previewDpi.SelectedIndexChanged += delegate
            {
                int index = previewDpi.SelectedIndex;
                if (index >= 0 && index < _supportedDpiValues.Count)
                    _settings.PreviewDpi = _supportedDpiValues[index];
                UpdatePreviewCost();
            };

            AddFieldLabel("Colour", ref y);
            NsDropdown previewMode = AddDropdown(new string[] { "Colour", "Greyscale" },
                                                 _settings.PreviewMode == ColorMode.Gray8 ? 1 : 0, ref y);
            previewMode.SelectedIndexChanged += delegate
            {
                _settings.PreviewMode = previewMode.SelectedIndex == 1 ? ColorMode.Gray8 : ColorMode.Color24;
                UpdatePreviewCost();
            };

            matchScan.CheckedChanged += delegate
            {
                _settings.PreviewMatchesScan = matchScan.Checked;
                previewDpi.Enabled = !matchScan.Checked;
                previewMode.Enabled = !matchScan.Checked;
                UpdatePreviewCost();
            };
            previewDpi.Enabled = !matchScan.Checked;
            previewMode.Enabled = !matchScan.Checked;

            y += 4;
            _previewCost = new NsReadout
            {
                Caption = "one preview",
                Location = new Point(Dx, y),
                Size = new Size(Dw, 30)
            };
            _drawerHost.Controls.Add(_previewCost);
            y += 36;

            const string note = "Auto crop measures the preview, not the scan. Finer previews "
                + "find smaller items and measure edges more closely; coarser ones are quicker.";
            NsNote previewNote = new NsNote
            {
                Text = note,
                Location = new Point(Dx, y),
                Size = new Size(Dw, NsNote.Measure(note, Dw))
            };
            _drawerHost.Controls.Add(previewNote);
            y += previewNote.Height + 10;

            UpdatePreviewCost();
        }

        /// <summary>
        /// What the chosen preview settings cost, in megabytes and seconds.
        ///
        /// Resolution is the setting people over-set, and a number beside the
        /// dropdown is worth more than any warning afterwards.
        /// </summary>
        void UpdatePreviewCost()
        {
            if (_previewCost == null) return;

            int dpi = PreviewDpi();
            bool grey = !_settings.PreviewMatchesScan && _settings.PreviewMode == ColorMode.Gray8;
            double pixels = _activeBedW * dpi * _activeBedH * dpi;
            double megabytes = pixels * (grey ? 1 : 3) / (1024.0 * 1024.0);

            // The lamp travels the bed once whatever the resolution; what grows
            // is the time per line. Measured on the LiDE 400: about 4 s at
            // 100 dpi and about 12 s at 300.
            double seconds = 2.0 + 0.033 * dpi;

            _previewCost.Value = megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB · about "
                + seconds.ToString("0", CultureInfo.InvariantCulture) + " s";
            _previewCost.ValueColor = megabytes > 150 ? Theme.Warn : Color.Empty;
            _previewCost.Invalidate();
        }

        /// <summary>
        /// The keys, and only the keys.
        ///
        /// Which model and how hard it thinks used to be here, and that was
        /// wrong: they are chosen per question, and Claude, ChatGPT, Gemini and
        /// Copilot all keep them on the composer beside the send button. What is
        /// left is the one part that belongs to the account rather than to the
        /// turn.
        ///
        /// All three are shown at once rather than behind a picker. The question
        /// this page answers is "which of these can I use", and a control that
        /// shows one at a time cannot answer it.
        ///
        /// Each box is write-only. What is already stored is never put back on
        /// screen -- the row says the last four characters, which is enough to
        /// tell whether the right key is in there and no use to anybody reading
        /// over a shoulder.
        /// </summary>
        void BuildAssistantSettings(ref int y)
        {
            AddSectionLabel("Assistant", ref y);

            if (_aiPanel == null)
            {
                Label gone = AddFieldLabel("The assistant did not load on this machine.", ref y);
                gone.ForeColor = Theme.Warn;
                y += 10;
                return;
            }

            foreach (Ai.IAiProvider provider in Ai.AiProviders.All())
            {
                Ai.IAiProvider which = provider;
                AddFieldLabel(provider.Info.Name, ref y);

                NsTextBox key = new NsTextBox
                {
                    Secret = true,
                    Location = new Point(Dx, y),
                    Size = new Size(Math.Max(60, Dw - 82), 32)
                };
                _themedFields.Add(key);
                _drawerHost.Controls.Add(key);

                NsPill keep = new NsPill
                {
                    Text = "Save",
                    Kind = PillKind.Normal,
                    Radius = 7,
                    Location = new Point(Dx + Math.Max(60, Dw - 78), y),
                    Size = new Size(78, 32)
                };
                _drawerHost.Controls.Add(keep);
                y += 36;

                Label state = AddFieldLabel("", ref y);
                y += 6;

                Action show = delegate
                {
                    string tail = Ai.AiKeys.Tail(which.Info.Id);
                    state.Text = tail.Length > 0
                        ? "Saved   " + tail
                        : "No key. It looks like " + which.Info.KeyHint;
                    state.ForeColor = tail.Length > 0 ? Theme.Good : Theme.TextDim;
                };
                show();

                keep.Click += delegate
                {
                    string typed = key.Text.Trim();
                    try
                    {
                        // An empty box means remove, not store nothing. Clearing
                        // it is the only way to take a key off a shared machine.
                        Ai.AiKeys.Set(which.Info.Id, typed);

                        // The list of models belongs to the key that fetched it.
                        // A new key is a different account and may not reach the
                        // same models at all.
                        Ai.AiModels.Forget(which);

                        SetStatus(typed.Length == 0
                            ? "The " + which.Info.Name + " key has been removed."
                            : which.Info.Name + " key saved.");
                    }
                    catch (Exception ex) { SetStatus("Could not store the key: " + ex.Message); }

                    key.Text = "";
                    show();
                    _aiPanel.Rebind();
                    if (_inspHead != null) _inspHead.Invalidate();
                };
            }

            Label where = AddFieldLabel(
                "Encrypted under this Windows account. Never written to the settings file " +
                "or to a preset.", ref y);
            where.ForeColor = Theme.TextFaint;
            where.Size = new Size(Dw, 30);
            y += 24;

            Label pick = AddFieldLabel(
                "The model and the effort are on the box in the Assist panel.", ref y);
            pick.ForeColor = Theme.TextFaint;
            pick.Size = new Size(Dw, 30);
            y += 22;
        }

        void BuildAppearanceSettings(ref int y)
        {
            AddSectionLabel("Appearance", ref y);

            NsSegment theme = new NsSegment();
            theme.Items = new string[] { "Light", "Graphite" };
            theme.SelectedIndex = _layout.LightTheme ? 0 : 1;
            theme.Location = new Point(Dx, y);
            theme.Size = new Size(Dw, 30);
            theme.SelectedIndexChanged += delegate
            {
                _layout.LightTheme = (theme.SelectedIndex == 0);
                ApplyTheme();
            };
            _drawerHost.Controls.Add(theme);
            y += 40;

            AddSectionLabel("Window", ref y);

            NsToggle tRemember = AddToggle("Remember window size", _layout.RememberWindowSize, ref y);
            tRemember.CheckedChanged += delegate { _layout.RememberWindowSize = tRemember.Checked; };

            y += 6;
            AddSectionLabel("Save & Reset", ref y);

            NsPill save = new NsPill();
            save.Text = "Save layout as default";
            save.Kind = PillKind.Primary;
            save.Location = new Point(Dx, y);
            save.Size = new Size(Dw, 34);
            save.Click += delegate
            {
                _layout.WindowWidth = Width;
                _layout.WindowHeight = Height;
                if (_layout.Save()) SetStatus("Layout saved. It will be used the next time NextScan starts.");
                else SetStatus("Could not write the layout file.");
            };
            _drawerHost.Controls.Add(save);
            y += 40;

            NsPill reset = new NsPill();
            reset.Text = "Reset to default layout";
            reset.Kind = PillKind.Normal;
            reset.Location = new Point(Dx, y);
            reset.Size = new Size(Dw, 32);
            reset.Click += delegate
            {
                _layout.ResetToDefaults();
                ApplyTheme();
                LayoutAll();
                SetStatus("Default workspace restored. Save to use it next time.");
            };
            _drawerHost.Controls.Add(reset);
            y += 38;
        }

        /// <summary>
        /// Repaints the whole shell in the current palette.
        ///
        /// Panels hold their BackColor as state rather than reading Theme each
        /// paint, so those have to be reassigned explicitly; everything
        /// owner-drawn just needs invalidating.
        /// </summary>
        /// <summary>Greys out the feeder-only batch controls on a flatbed.</summary>
        void UpdateBatchFeedEnabled()
        {
            bool feeder = _settings.Source == PaperSource.Feeder ||
                          _settings.Source == PaperSource.FeederDuplex;
            foreach (Control c in _batchFeedControls)
            {
                if (c == null) continue;
                // The page count is also meaningless while the job is set to run
                // until the tray empties.
                bool untilEmpty = _tgUntilEmpty != null && _tgUntilEmpty.Checked;
                c.Enabled = feeder && (c == _tgUntilEmpty || !untilEmpty);
            }
        }

        void ApplyTheme()
        {
            Theme.Apply(_layout.LightTheme);

            BackColor = Theme.Ground;
            if (_titleBar != null) _titleBar.BackColor = Theme.Surface;
            if (_inspector != null) _inspector.BackColor = Theme.Surface;
            if (_inspHead != null) _inspHead.BackColor = Theme.Surface;
            if (_inspScroll != null) _inspScroll.BackColor = Theme.Surface;
            if (_inspFoot != null) _inspFoot.BackColor = Theme.Surface;
            if (_pagesPanel != null) _pagesPanel.BackColor = Theme.Surface;
            if (_aiPanel != null) _aiPanel.ApplyTheme();
            if (_rail != null) _rail.BackColor = Theme.Surface;
            if (_settingsPage != null) _settingsPage.BackColor = Theme.Ground;
            if (_settingsHead != null) _settingsHead.BackColor = Theme.Surface;
            if (_settingsBody != null) _settingsBody.BackColor = Theme.Ground;
            if (_zoomText != null) _zoomText.ForeColor = Theme.TextFaint;
            if (_planLabel != null) _planLabel.ForeColor = Theme.TextDim;
            if (_status != null) _status.BackColor = Theme.Surface;
            if (_canvas != null) _canvas.BackColor = Theme.Ground;
            if (_film != null) _film.BackColor = Theme.Surface;
            if (_statusText != null) _statusText.ForeColor = Theme.TextDim;

            // Label colours are set at construction, not at paint time, and a
            // section holds dozens of them. Rebuilding is both shorter and the
            // only version that cannot leave one row in the old palette.
            ShowSection(_activeSection);
            if (_settingsOpen) FillSettings();

            // The text fields host a real TextBox, whose colours are properties
            // rather than something repainted from the palette on the next frame.
            foreach (NsTextBox t in _themedFields) t.ApplyTheme();

            try
            {
                int dark = _layout.LightTheme ? 0 : 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }

            Invalidate(true);
            SetStatus(_layout.LightTheme ? "Light theme." : "Dark theme.");
        }

        // =====================================================================
        // Canvas, HUD, dock, filmstrip
        // =====================================================================
        void BuildCanvasArea()
        {
            _canvas = new StudioCanvasView();
            Controls.Add(_canvas);

            _splitDrawer = new NsSplitter();
            _splitDrawer.Dragged += delegate (object o, NsSplitter.DeltaEventArgs e)
            {
                // The inspector is on the RIGHT now, so dragging left widens it.
                _layout.DrawerWidth = Math.Max(MinDrawerWidth, Math.Min(520, _layout.DrawerWidth - e.Delta));
                LayoutAll();
            };
            // Rebuild once at the end rather than on every mouse move: laying out
            // a dozen controls per pixel of drag is visibly janky.
            _splitDrawer.DragFinished += delegate { ShowSection(_activeSection); LayoutAll(); };
            Controls.Add(_splitDrawer);

            _canvas.CropChanged += delegate
            {
                _settings.CropNorm = _canvas.CropNorm;
                _manualCrop = !IsFullCrop(_canvas.CropNorm);
                ClearCropPlan("the selection was moved by hand");
                // Only a real drag reaches here: the CropNorm setter does not
                // raise this event, so every programmatic change is silent.
                _cropDrawnByUser = _manualCrop;
                ShowCropRegionHint();
            };

            // Reshaping a detected region changes the plan for that one item and
            // nothing else. It must not be mistaken for the operator drawing a
            // crop by hand, which throws the whole plan away.
            _canvas.RegionEdited += delegate(object sender, RegionEventArgs e)
            {
                RebuildPlanEntry(e.Index);
                PinStatus("Item " + (e.Index + 1) + " adjusted by hand.");
            };

            _canvas.RegionMenu += delegate(object sender, RegionEventArgs e)
            {
                ShowRegionMenu(e.Index, e.At);
            };

            _canvas.EmptyClicked += delegate(object sender, RegionEventArgs e)
            {
                ProbeForItem(e.Norm);
            };

            _film = new StudioFilmstripView();
            _film.SelectionChanged += delegate
            {
                RawImage img = _film.SelectedImage;
                if (img != null)
                {
                    _canvas.SetImage(img);
                    if (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                        ApplyPaperSizeToCanvasCrop(_supportedPaperEntries[_paperSizeIndex]);
                    UpdateProcessedPreview();
                }
                else _canvas.SetImage(null);
            };

            // Deliberately NOT added to the form: the filmstrip is parented to
            // the Pages panel inside the inspector, which is built next.

            _batchBar = new NsBatchBar
            {
                Settings = _settings,
                Visible = false,
                Height = NsBatchBar.PreferredHeight
            };
            _batchBar.StartRequested += delegate { StartBatch(); };
            _batchBar.StopRequested += delegate { RequestCancel(); };
            _batchBar.SaveRequested += delegate { SaveSession(); HideBatchBarIfIdle(); };
            _batchBar.SettingsChanged += delegate { OnBatchSettingsChanged(); };
            Controls.Add(_batchBar);

            // The batch strip sits over the canvas, so it has to be in front of
            // it; controls added later are further back in WinForms z-order.
            _batchBar.BringToFront();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.R))
            {
                RotateActiveImage(RotateFlipType.Rotate90FlipNone);
                return true;
            }
            if (keyData == (Keys.Control | Keys.Shift | Keys.R))
            {
                RotateActiveImage(RotateFlipType.Rotate270FlipNone);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// The status bar carries the view controls.
        ///
        /// They used to live in a floating bar over the bottom of the preview,
        /// which cost a 44 px strip of image plus its margins on every window.
        /// The status bar is already there and already half empty, so putting
        /// them here costs nothing at all.
        /// </summary>
        void BuildStatus()
        {
            _status = new Panel { Height = Theme.StatusBarHeight, BackColor = Theme.Surface, Dock = DockStyle.Bottom };
            _status.Paint += delegate (object sender, PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Surface);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    e.Graphics.DrawLine(pen, 0, 0, _status.Width, 0);
            };
            Controls.Add(_status);

            _statusText = new Label
            {
                Text = "Ready",
                ForeColor = Theme.TextDim,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8.25f),
                Location = new Point(16, 5),
                AutoSize = true
            };
            _status.Controls.Add(_statusText);

            // The shortcut is named on the button, because there are no menus
            // here to find it in and an unadvertised shortcut may as well not
            // exist. These are Photoshop's, deliberately: anyone running both
            // already has the habit.
            AddViewButton(NsIcon.Fit, "Fit the page in the window  (Ctrl+0)", delegate { _canvas.ZoomFit(); });
            AddViewButton(NsIcon.Actual, "Actual size  (Ctrl+1)", delegate { _canvas.ZoomTo(1.0); });
            AddViewButton(NsIcon.ZoomOut, "Zoom out  (Ctrl+-)", delegate { _canvas.ZoomBy(1 / 1.25); });
            AddViewButton(NsIcon.ZoomIn, "Zoom in  (Ctrl++)", delegate { _canvas.ZoomBy(1.25); });
            AddViewButton(NsIcon.RotateLeft, "Rotate left",
                          delegate { RotateActiveImage(RotateFlipType.Rotate270FlipNone); });
            AddViewButton(NsIcon.RotateRight, "Rotate right",
                          delegate { RotateActiveImage(RotateFlipType.Rotate90FlipNone); });

            _tbGrid = AddViewButton(NsIcon.Grid, "Thirds guides", null);
            _tbGrid.Toggle = true;
            _tbGrid.Click += delegate { _canvas.ShowThirds = _tbGrid.Checked; _canvas.Invalidate(); };

            _tbCompare = AddViewButton(NsIcon.Compare, "Before and after", null);
            _tbCompare.Toggle = true;
            _tbCompare.Click += delegate { _canvas.ShowSplit = _tbCompare.Checked; _canvas.Invalidate(); };

            // Only useful once a scan has cut several items, so it is hidden
            // until there is something to show rather than sitting there greyed.
            _tbSheet = AddViewButton(NsIcon.Pages, "Every item from the last scan", null);
            _tbSheet.Toggle = true;
            _tbSheet.Visible = false;
            _tbSheet.Click += delegate { ShowSheet(_tbSheet.Checked); };

            _zoomText = new Label
            {
                ForeColor = Theme.TextFaint,
                BackColor = Color.Transparent,
                Font = Theme.Ui(8f),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleRight,
                Text = ""
            };
            _status.Controls.Add(_zoomText);
        }

        NsIconButton AddViewButton(string icon, string tip, EventHandler onClick)
        {
            NsIconButton button = new NsIconButton { Icon = icon, Size = new Size(26, 22) };
            button.AccessibleName = tip;
            _tips.SetToolTip(button, tip);
            if (onClick != null) button.Click += onClick;
            _status.Controls.Add(button);
            _viewButtons.Add(button);
            return button;
        }

        readonly ToolTip _tips = new ToolTip { InitialDelay = 500, ReshowDelay = 200 };

        // =====================================================================
        // Settings, as a full page
        //
        // Everything here is decided once and then left alone, so it does not
        // deserve a permanent column beside the preview. It opens over the
        // workspace, uses the whole width in columns rather than one narrow
        // strip, and closes back to exactly where the operator was.
        // =====================================================================
        void BuildSettingsPage()
        {
            _settingsPage = new Panel { BackColor = Theme.Ground, Visible = false };
            Controls.Add(_settingsPage);

            _settingsHead = new Panel { BackColor = Theme.Surface, Location = new Point(0, 0) };
            _settingsHead.Paint += delegate (object sender, PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                Theme.Smooth(g);
                g.Clear(Theme.Surface);
                using (Font f = Theme.UiSemi(13f))
                    TextRenderer.DrawText(g, "Settings", f, new Rectangle(28, 0, 400, _settingsHead.Height),
                        Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                using (Pen pen = new Pen(Theme.LineSoft, 1f))
                    g.DrawLine(pen, 0, _settingsHead.Height - 1, _settingsHead.Width, _settingsHead.Height - 1);
            };
            _settingsPage.Controls.Add(_settingsHead);

            NsPill done = new NsPill { Text = "Done", Kind = PillKind.Primary, Radius = 7, Size = new Size(96, 32) };
            done.Click += delegate { OpenSettings(false); };
            done.Name = "settingsDone";
            _settingsHead.Controls.Add(done);

            // AutoScroll, because the page does not fit. It never did on a
            // window that is not maximised, and there was no way to reach what
            // was below the fold: the columns simply ran off the bottom.
            _settingsBody = new Panel { BackColor = Theme.Ground, AutoScroll = true };
            _settingsBody.MouseWheel += OnSettingsWheel;
            _settingsPage.Controls.Add(_settingsBody);
        }

        void OpenSettings(bool open)
        {
            if (_settingsOpen == open) return;
            _settingsOpen = open;

            if (open) FillSettings();
            else
            {
                // Closing the page is the moment the operator considers the
                // change made. Settings were only written when the window itself
                // closed, so a crash - or a machine turned off at the wall -
                // lost every choice made since it started.
                try { _settings.Save(); } catch (Exception ex) { Log("could not save settings: " + ex.Message); }
            }

            _settingsPage.Visible = open;
            if (open) _settingsPage.BringToFront();
            if (_settingsButton != null) _settingsButton.Checked = open;

            LayoutAll();
            SetStatus(open ? "Settings. Changes apply immediately." : "Ready.");
        }

        /// <summary>
        /// Lays the settings out in columns across the page.
        ///
        /// One 300 px column on a 1300 px page would be nine tenths empty, which
        /// is exactly the kind of hollow screen a full page has to justify. The
        /// column count follows the width.
        /// </summary>
        void FillSettings()
        {
            _settingsBody.SuspendLayout();
            while (_settingsBody.Controls.Count > 0)
            {
                Control previous = _settingsBody.Controls[0];
                _settingsBody.Controls.Remove(previous);
                previous.Dispose();
            }
            _previewCost = null;

            Panel host = _drawerHost;
            int columnWidth = SettingsColumnWidth;
            int count = SettingsColumns;

            // Centred rather than flush left. There is genuinely not a screen's
            // worth of settings here, and a left-packed pair of columns on a
            // wide monitor reads as a page that failed to load; a centred band
            // reads as a sheet, which is what it is.
            const int gutter = 44;
            int band = count * columnWidth + (count - 1) * gutter;
            int originX = Math.Max(28, (_settingsBody.ClientSize.Width - band) / 2);

            Panel[] columns = new Panel[count];
            for (int i = 0; i < columns.Length; i++)
            {
                columns[i] = new Panel
                {
                    BackColor = Color.Transparent,
                    Location = new Point(originX + i * (columnWidth + gutter), 4),
                    Size = new Size(columnWidth, Math.Max(100, _settingsBody.Height - 8))
                };
                // A wheel turned over a column has to reach the panel that
                // scrolls. Unhandled wheel messages climb to the parent, but
                // only once something has focus, and nothing has focus on a page
                // that has just opened.
                columns[i].MouseWheel += OnSettingsWheel;
                _settingsBody.Controls.Add(columns[i]);
            }

            SectionBuilder[] parts = { BuildOutputGroup, BuildPreviewSettings, BuildShortcutSettings,
                                       BuildAssistantSettings, BuildWatchGroup,
                                       BuildAppearanceSettings, BuildAboutSettings };
            int[] into = { 0, 0, 0, 1, 1, 1, 1 };
            int tallest = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                _drawerHost = columns[Math.Min(into[i], columns.Length - 1)];
                int y = _drawerHost.Tag == null ? 8 : (int)_drawerHost.Tag;
                parts[i](ref y);
                y += 20;
                _drawerHost.Tag = y;
                tallest = Math.Max(tallest, y);
            }
            _drawerHost = host;

            for (int i = 0; i < columns.Length; i++)
            {
                columns[i].Height = Math.Max(tallest, _settingsBody.Height);
                columns[i].Tag = null;
            }

            _settingsBody.ResumeLayout();
            _settingsBody.PerformLayout();

            // Back to the top. Filling the page gives something focus, and
            // WinForms scrolls whatever has focus into view, so the page opened
            // at its own end with the first setting above the fold.
            _settingsBody.AutoScrollPosition = new Point(0, 0);
        }

        void OnSettingsWheel(object sender, MouseEventArgs e)
        {
            if (_settingsBody == null) return;

            int room = _settingsBody.DisplayRectangle.Height - _settingsBody.ClientSize.Height;
            if (room <= 0) return;

            int at = -_settingsBody.AutoScrollPosition.Y + (e.Delta > 0 ? -72 : 72);
            _settingsBody.AutoScrollPosition = new Point(0, Math.Max(0, Math.Min(room, at)));
        }

        int SettingsColumns
        {
            get
            {
                int usable = Math.Max(320, ClientSize.Width - 56);
                return Math.Max(1, Math.Min(2, usable / 380));
            }
        }

        int SettingsColumnWidth
        {
            get
            {
                int columns = SettingsColumns;
                int usable = Math.Max(320, ClientSize.Width - 56 - (columns - 1) * 34);
                return Math.Max(280, Math.Min(420, usable / columns));
            }
        }

        // =====================================================================
        // Layout
        //
        //   title bar                                    [settings] [_ □ x]
        //   [rail][ ................ preview ............ ][ inspector ]
        //   status .................................. [ view controls ]
        //
        // Nothing floats, nothing is draggable except the inspector edge, and
        // the only thing ever drawn over the preview is the batch strip while a
        // batch is actually running. The view controls live in the status bar,
        // which already existed, so they cost the image nothing.
        // =====================================================================
        void LayoutAll()
        {
            if (_titleBar == null || _inspector == null || _canvas == null) return;

            int top = Theme.TitleBarHeight;
            int bodyHeight = Math.Max(1, ClientSize.Height - top - Theme.StatusBarHeight);

            // ---- title bar ---------------------------------------------------
            _titleBar.Width = ClientSize.Width;
            int buttonX = ClientSize.Width - 6;
            foreach (Control control in _titleBar.Controls)
            {
                NsPill button = control as NsPill;
                if (button == null) continue;
                buttonX -= button.Width;
                button.Location = new Point(buttonX, (Theme.TitleBarHeight - button.Height) / 2);
            }
            if (_settingsButton != null)
            {
                buttonX -= _settingsButton.Width + 12;
                _settingsButton.Location = new Point(buttonX,
                                                     (Theme.TitleBarHeight - _settingsButton.Height) / 2);
            }
            if (_deviceBar != null)
            {
                int available = Math.Max(160, buttonX - DeviceBarLeft - 16);
                _deviceBar.Width = Math.Min(_deviceBar.DesiredWidth, available);
                _deviceBar.Location = new Point(DeviceBarLeft,
                                                (Theme.TitleBarHeight - _deviceBar.Height) / 2);
            }

            // ---- settings, when it is open -----------------------------------
            _settingsPage.SetBounds(0, top, ClientSize.Width, bodyHeight);
            if (_settingsOpen)
            {
                const int settingsHeadHeight = 62;
                _settingsHead.SetBounds(0, 0, ClientSize.Width, settingsHeadHeight);
                foreach (Control control in _settingsHead.Controls)
                {
                    NsPill button = control as NsPill;
                    if (button == null) continue;
                    button.Location = new Point(Math.Max(28, ClientSize.Width - button.Width - 28),
                                                (settingsHeadHeight - button.Height) / 2);
                }
                _settingsBody.SetBounds(0, settingsHeadHeight, ClientSize.Width,
                                        Math.Max(1, bodyHeight - settingsHeadHeight));
            }

            // ---- columns -----------------------------------------------------
            int railWidth = RailWidth;
            int inspWidth = InspectorWidth;
            int stageWidth = Math.Max(1, ClientSize.Width - railWidth - inspWidth);

            _rail.SetBounds(0, top, railWidth, bodyHeight);
            for (int i = 0; i < _railButtons.Count; i++)
                _railButtons[i].SetBounds(0, 8 + i * (RailItemHeight + 2), railWidth, RailItemHeight);

            _canvas.SetBounds(railWidth, top, stageWidth, bodyHeight);

            _inspector.SetBounds(ClientSize.Width - inspWidth, top, inspWidth, bodyHeight);
            _splitDrawer.SetBounds(ClientSize.Width - inspWidth - 3, top, 6, bodyHeight);
            _splitDrawer.BringToFront();

            LayoutInspectorBody();

            // ---- the batch strip, over the top of the preview ----------------
            int batchHeight = 0;
            if (_batchBar.Visible)
            {
                batchHeight = NsBatchBar.PreferredHeight;
                _batchBar.SetBounds(railWidth + 12, top + 12, Math.Max(1, stageWidth - 24), batchHeight);
                _batchBar.BringToFront();
            }
            _canvas.ReservedTop = 12 + (batchHeight > 0 ? batchHeight + 10 : 0);
            _canvas.ReservedBottom = 12;

            // Last, and after the splitter and the batch strip have raised
            // themselves: the settings page covers the whole workspace or it
            // is not a page.
            if (_settingsOpen) _settingsPage.BringToFront();

            LayoutStatusBar();
        }

        /// <summary>
        /// The resolution a preview is taken at.
        ///
        /// Clamped to what the scanner will actually do: a saved preference of
        /// 300 dpi on a device that only offers 75, 150 and 600 has to resolve
        /// to one of those, and the nearest at or above the request keeps the
        /// detection quality the operator asked for rather than silently
        /// dropping below it.
        /// </summary>
        int PreviewDpi()
        {
            int wanted = _settings.PreviewMatchesScan ? _settings.Dpi : _settings.PreviewDpi;
            if (_supportedDpiValues.Count == 0) return Math.Max(50, wanted);
            if (_supportedDpiValues.Contains(wanted)) return wanted;

            int best = -1;
            foreach (int value in _supportedDpiValues)
                if (value >= wanted && (best < 0 || value < best)) best = value;
            if (best > 0) return best;

            best = _supportedDpiValues[0];
            foreach (int value in _supportedDpiValues) if (value > best) best = value;
            return best;
        }

        /// <summary>Left edge of the device chip: clear of the wordmark.</summary>
        const int DeviceBarLeft = 174;

        const int InspectorHeadHeight = 42;
        const int RailItemHeight = 50;

        void LayoutStatusBar()
        {
            if (_statusText == null) return;

            int height = Theme.StatusBarHeight;
            int x = ClientSize.Width - 10;

            for (int i = _viewButtons.Count - 1; i >= 0; i--)
            {
                NsIconButton button = _viewButtons[i];

                // A wider gap in front of the two overlay toggles: they change
                // what is drawn, the six before them only change the view.
                if (i == _viewButtons.Count - 2) x -= 8;
                x -= button.Width;
                button.Location = new Point(x, (height - button.Height) / 2);
            }

            _zoomText.SetBounds(Math.Max(0, x - 84), 0, 76, height);
            _statusText.AutoSize = false;
            _statusText.TextAlign = ContentAlignment.MiddleLeft;
            _statusText.AutoEllipsis = true;
            _statusText.SetBounds(16, 0, Math.Max(1, _zoomText.Left - 26), height);
        }

        void LayoutInspectorBody()
        {
            if (_inspHead == null) return;

            int width = _inspector.Width;
            int bodyHeight = _inspector.Height;
            int inner = Math.Max(1, width - 1);

            bool pages = (_activeSection == PagesSection);
            bool assist = (_activeSection == AiSection) && _aiPanel != null;

            // A chat is its box pinned to the bottom, and the action footer
            // would take 136 px from the one panel that needs every one of
            // them. So on Assist it goes -- with one exception. A scan that is
            // running must always have a visible way to stop it, so the footer
            // comes back at the height of one button, carrying nothing but the
            // button that stops the scan.
            int footHeight = assist
                ? (_busy ? CompactFooterHeight : 0)
                : Math.Min(FooterHeight, Math.Max(92, bodyHeight - 120));
            int middle = Math.Max(1, bodyHeight - InspectorHeadHeight - footHeight);

            _inspHead.SetBounds(1, 0, inner, InspectorHeadHeight);
            _inspFoot.SetBounds(1, Math.Max(0, bodyHeight - footHeight), inner, footHeight);
            _inspFoot.Visible = footHeight > 0;

            _inspScroll.SetBounds(1, InspectorHeadHeight, inner, middle);
            _pagesPanel.SetBounds(1, InspectorHeadHeight, inner, middle);
            _inspScroll.Visible = !pages && !assist;
            _pagesPanel.Visible = pages;
            if (_aiPanel != null)
            {
                _aiPanel.SetBounds(1, InspectorHeadHeight, inner, middle);
                _aiPanel.Visible = assist;
            }
            if (pages) LayoutPagesPanel(inner, middle);

            if (footHeight > 0) LayoutFooter(inner, assist);
            UpdateDrawerScroll();
        }

        void LayoutPagesPanel(int width, int height)
        {
            int pad = Dx;
            int inner = Math.Max(60, width - pad * 2);

            bool recover = _recoverPill != null && _recoverPill.Visible;
            int actions = 12 + (recover ? 34 + 8 : 0) + 36 + 12;

            if (_film != null) _film.SetBounds(0, 0, width, Math.Max(1, height - actions));

            int y = Math.Max(0, height - actions) + 12;
            if (recover)
            {
                _recoverPill.SetBounds(pad, y, inner, 34);
                y += 42;
            }
            if (_savePill != null) _savePill.SetBounds(pad, y, inner, 36);
        }

        /// <summary>The pinned action footer. Compact carries the stop button alone.</summary>
        const int CompactFooterHeight = 56;

        void LayoutFooter(int width, bool compact)
        {
            if (_planLabel == null) return;

            int inner = Math.Max(60, width - Dx * 2);

            if (compact)
            {
                _planLabel.Visible = _previewPill.Visible = _fullBedPill.Visible = false;
                _batchPill.Visible = _toPsPill.Visible = false;
                _scanPill.Visible = true;
                _scanPill.SetBounds(Dx, 9, inner, 38);
                return;
            }

            _planLabel.Visible = _previewPill.Visible = _fullBedPill.Visible = true;
            _batchPill.Visible = _toPsPill.Visible = _scanPill.Visible = true;
            _planLabel.SetBounds(Dx, 10, inner, 30);

            const int gap = 8;
            const int square = 38;

            // Preview, then the two follow-on actions as icons. They are
            // recognised rather than read, and giving them full-width labels
            // made four buttons of equal weight where only one matters.
            int row = 46;
            int previewWidth = Math.Max(60, inner - (square + gap) * 3);
            int at = Dx + previewWidth + gap;
            _previewPill.SetBounds(Dx, row, previewWidth, square);
            _fullBedPill.SetBounds(at, row, square, square);
            _batchPill.SetBounds(at + square + gap, row, square, square);
            _toPsPill.SetBounds(at + (square + gap) * 2, row, square, square);

            _scanPill.SetBounds(Dx, row + square + gap, inner, 40);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Ground);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyModernChrome();

            // Deferred to the message loop so the window is on screen before the
            // status line mentions a batch to recover; announcing it during
            // construction would land before there is anything to read it on.
            BeginInvoke((MethodInvoker)delegate { CheckForRecoverableSession(); });
        }

        /// <summary>
        /// Windows 11 rounds every window corner through DWM. A borderless form
        /// does not get that automatically, which is why the shell looked like a
        /// hard rectangle next to every other app. DwmSetWindowAttribute is the
        /// supported way to ask for it; on Windows 10 the call simply fails and
        /// we keep square corners rather than faking them with a Region (a region
        /// clips the drop shadow and aliases the edge).
        /// </summary>
        void ApplyModernChrome()
        {
            try
            {
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch { }

            try
            {
                int dark = _layout.LightTheme ? 0 : 1;
                DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            }
            catch { }
        }

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // =====================================================================
        // Device probing
        // =====================================================================
        void BeginProbe()
        {
            if (_busy) return;

            _deviceBar.DeviceName = "Searching…";
            _deviceBar.TransportText = "";
            _deviceBar.SetBusy(true);
            SetStatus("Looking for scanners on TWAIN, WIA and the network…");

            Thread t = new Thread(delegate ()
            {
                List<ScannerEntry> found;
                try { found = _broker.Probe(); }
                catch (Exception ex) { found = new List<ScannerEntry>(); Log("probe failed: " + ex.Message); }

                try
                {
                    BeginInvoke((MethodInvoker)delegate { OnProbeDone(found); });
                }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void OnProbeDone(List<ScannerEntry> found)
        {
            _scanners = found ?? new List<ScannerEntry>();
            _deviceBar.SetBusy(false);

            if (_scanners.Count == 0)
            {
                _device = null;
                _deviceBar.DeviceName = "No scanner";
                _deviceBar.TransportText = "check power and cable";
                _deviceBar.Connected = false;
                _deviceBar.Invalidate();
                SetStatus("No scanner found. Check it is switched on, then press Search again.");
            }
            else
            {
                // Prefer the device named in settings, else the first one.
                int index = 0;
                for (int i = 0; i < _scanners.Count; i++)
                {
                    if (string.Equals(_scanners[i].DisplayName, _settings.DeviceName, StringComparison.OrdinalIgnoreCase))
                    { index = i; break; }
                }

                ApplyScanner(index);
                SetStatus("Found " + _scanners.Count + " scanner" + (_scanners.Count == 1 ? "" : "s") + ".");
            }

        }

        void ApplyScanner(int index)
        {
            if (index < 0 || index >= _scanners.Count) return;

            ScannerEntry entry = _scanners[index];
            _device = entry.Preferred;
            _settings.DeviceName = entry.DisplayName;

            UpdateDeviceChip();
            FetchDeviceCapabilities(_device);
        }

        void UpdateDeviceChip()
        {
            if (_device == null || _deviceBar == null) return;

            _deviceBar.DeviceName = _device.FriendlyName;
            _deviceBar.TransportText = _device.Transport.ToString().ToUpperInvariant() + " · " + _device.HostBitness + "-bit";
            _deviceBar.Connected = true;
            _deviceBar.SourceIndex = SourceToIndex(_settings.Source);
            _deviceBar.Invalidate();
            LayoutAll();
        }

        void FetchDeviceCapabilities(DeviceDescriptor dev)
        {
            if (dev == null) return;

            string key = dev.NativeId + ":" + dev.Transport + ":" + dev.HostBitness;
            lock (_capsCache)
            {
                if (_capsCache.ContainsKey(key))
                {
                    ApplyCapabilities(_capsCache[key]);
                    return;
                }
            }

            Thread t = new Thread(delegate ()
            {
                DeviceCapabilities caps;
                NsResult r = _broker.GetCapabilities(dev, out caps);
                if (r.Ok && caps != null)
                {
                    lock (_capsCache) { _capsCache[key] = caps; }
                    try
                    {
                        BeginInvoke((Action)delegate
                        {
                            if (ReferenceEquals(_device, dev))
                            {
                                ApplyCapabilities(caps);
                            }
                        });
                    }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void ApplyCapabilities(DeviceCapabilities caps)
        {
            if (caps == null) return;

            List<int> list = new List<int>();
            int maxCap = caps.MaxResolution > 0 ? caps.MaxResolution : int.MaxValue;

            if (caps.Resolutions != null && caps.Resolutions.Count > 0)
            {
                if (caps.ResolutionIsRange)
                {
                    // Clean steps within [MinResolution, MaxResolution], capped at 4800
                    int[] stops = { 75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400, 4800 };
                    foreach (int v in stops)
                    {
                        if (v >= caps.MinResolution && v <= maxCap)
                            list.Add(v);
                    }
                    if (maxCap <= 4800 && !list.Contains(maxCap) && maxCap >= caps.MinResolution)
                        list.Add(maxCap);
                }
                else
                {
                    // Discrete list: strictly and only what the driver explicitly supports up to MaxResolution
                    foreach (int v in caps.Resolutions)
                    {
                        if (v > 0 && v <= maxCap && !list.Contains(v))
                            list.Add(v);
                    }
                }
            }

            if (list.Count == 0)
            {
                int max = caps.MaxResolution > 0 ? caps.MaxResolution : 300;
                int[] ladder = { 75, 100, 150, 200, 300, 400, 600, 1200, 2400, 4800 };
                foreach (int r in ladder)
                    if (r <= max) list.Add(r);
                if (list.Count == 0) list.Add(Math.Min(300, max));
            }

            list.Sort();

            _supportedDpiValues.Clear();
            _supportedDpiValues.AddRange(list);

            _supportedDpiLabels.Clear();
            foreach (int v in _supportedDpiValues)
                _supportedDpiLabels.Add(v + " dpi");

            // Clamp or update selected DPI to supported range
            if (!_supportedDpiValues.Contains(_settings.Dpi))
            {
                int best = _supportedDpiValues[_supportedDpiValues.Count - 1];
                for (int i = _supportedDpiValues.Count - 1; i >= 0; i--)
                {
                    if (_supportedDpiValues[i] <= _settings.Dpi)
                    {
                        best = _supportedDpiValues[i];
                        break;
                    }
                }
                _settings.Dpi = best;
            }

            int selIndex = _supportedDpiValues.IndexOf(_settings.Dpi);
            if (selIndex < 0 && _supportedDpiValues.Count > 0) selIndex = _supportedDpiValues.Count - 1;

            if (_ddDpi != null)
            {
                _ddDpi.SetItems(_supportedDpiLabels, selIndex);
            }

            // 2. Evaluate supported paper sizes based on scanner physical bed dimensions
            _activeBedW = (caps.PhysicalWidthIn > 1.0) ? caps.PhysicalWidthIn : 8.5;
            _activeBedH = (caps.PhysicalHeightIn > 1.0) ? caps.PhysicalHeightIn : 11.7;

            _supportedPaperEntries.Clear();
            _supportedPaperLabels.Clear();

            // Determine friendly maximum name based on detected bed size
            string maxName = "Maximum";
            if (_activeBedW >= 11.0 && _activeBedH >= 16.0)
                maxName = "Maximum / A3";
            else if (_activeBedH >= 13.5 && _activeBedW < 10.0)
                maxName = "Maximum / Legal";
            else if (Math.Abs(_activeBedW - 8.5) < 0.2 && Math.Abs(_activeBedH - 11.0) < 0.2)
                maxName = "Maximum / Letter";
            else if (Math.Abs(_activeBedW - 8.27) < 0.2 && Math.Abs(_activeBedH - 11.69) < 0.2)
                maxName = "Maximum / A4";
            else
                maxName = "Maximum";

            string maxLabel = string.Format(CultureInfo.InvariantCulture, "{0} ({1:0.#} × {2:0.#}\")", maxName, _activeBedW, _activeBedH);
            _supportedPaperEntries.Add(new PaperSizeDef("Maximum", maxLabel, _activeBedW, _activeBedH));
            _supportedPaperLabels.Add(maxLabel);

            // Filter standard sizes: only allow sizes that physically fit on the scanner's bed
            for (int i = 1; i < AllPaperSizes.Length; i++)
            {
                PaperSizeDef def = AllPaperSizes[i];
                if ((def.WidthIn <= _activeBedW + 0.1 && def.HeightIn <= _activeBedH + 0.1) ||
                    (def.HeightIn <= _activeBedW + 0.1 && def.WidthIn <= _activeBedH + 0.1))
                {
                    _supportedPaperEntries.Add(def);
                    _supportedPaperLabels.Add(def.Label);
                }
            }

            // Keep user's chosen size if it is supported on this scanner; otherwise default to Maximum (index 0)
            _paperSizeIndex = 0;
            if (!string.IsNullOrEmpty(_settings.PaperSize) && _settings.PaperSize != "Maximum")
            {
                for (int i = 0; i < _supportedPaperEntries.Count; i++)
                {
                    if (string.Equals(_supportedPaperEntries[i].Key, _settings.PaperSize, StringComparison.OrdinalIgnoreCase))
                    {
                        _paperSizeIndex = i;
                        break;
                    }
                }
            }

            ApplyPaperSizeEntry(_supportedPaperEntries[_paperSizeIndex]);

            if (_ddPaperSize != null)
            {
                _ddPaperSize.SetItems(_supportedPaperLabels, _paperSizeIndex);
            }
            if (_segOrientation != null)
            {
                _segOrientation.SelectedIndex = _settings.PaperLandscape ? 1 : 0;
            }

            // Which sources exist is a property of the scanner, and the Capture
            // panel hides the source control on a flatbed-only device. Rebuild
            // it now that the answer is known. Always on the UI thread: this
            // method is reached either from the cache on the UI thread or
            // through BeginInvoke.
            bool hadFeeder = _hasFeeder, hadDuplex = _hasDuplex;
            _hasFeeder = caps.SupportsFeeder;
            _hasDuplex = caps.SupportsDuplex;
            if ((hadFeeder != _hasFeeder || hadDuplex != _hasDuplex) && _drawerHost != null)
                ShowSection(_activeSection);

            // The platen size is only known now, so this is the first point at
            // which a correctly sized blank sheet can be drawn.
            ShowBlankSheet();
        }

        /// <summary>Device list, dropped under the name zone of the title bar.</summary>
        void ShowDeviceMenu()
        {
            if (_scanners.Count == 0) { BeginProbe(); return; }

            List<string> names = new List<string>();
            int current = 0;
            for (int i = 0; i < _scanners.Count; i++)
            {
                names.Add(_scanners[i].DisplayName);
                if (_device != null && _scanners[i].Connections.Contains(_device)) current = i;
            }

            NsMenu.Show(_deviceBar, _deviceBar.MenuPointFor(0), names, current, 220,
                delegate (int picked) { ApplyScanner(picked); });
        }

        /// <summary>Transports for the selected device, under the transport zone.</summary>
        void ShowTransportMenu()
        {
            ScannerEntry entry = CurrentEntry();
            if (entry == null) return;

            List<string> labels = new List<string>();
            int current = 0;
            for (int i = 0; i < entry.Connections.Count; i++)
            {
                DeviceDescriptor d = entry.Connections[i];
                labels.Add(d.Transport.ToString().ToUpperInvariant() + "   ·   " + d.HostBitness + "-bit" +
                           (ReferenceEquals(d, entry.Preferred) ? "   (recommended)" : ""));
                if (ReferenceEquals(d, _device)) current = i;
            }

            NsMenu.Show(_deviceBar, _deviceBar.MenuPointFor(1), labels, current, 200,
                delegate (int picked)
                {
                    if (picked < 0 || picked >= entry.Connections.Count) return;
                    _device = entry.Connections[picked];
                    _settings.Transport = _device.Transport;
                    _settings.HostBitness = _device.HostBitness;
                    UpdateDeviceChip();
                    FetchDeviceCapabilities(_device);
                    SetStatus("Using " + _device.Transport + " on the " + _device.HostBitness + "-bit host.");
                });
        }

        ScannerEntry CurrentEntry()
        {
            foreach (ScannerEntry e in _scanners)
                if (_device != null && e.Connections.Contains(_device)) return e;
            return (_scanners.Count > 0) ? _scanners[0] : null;
        }

        static string SourceLabel(int i)
        {
            if (i == 1) return "feeder";
            if (i == 2) return "feeder, duplex";
            if (i == 3) return "film";
            return "flatbed";
        }



        // =====================================================================
        // Scanning
        // =====================================================================
        /// <summary>
        /// Remembers where on the platen the delivered page came from.
        ///
        /// The image's own pixel size and dpi are used in preference to the
        /// requested rectangle, because that is what the scanner actually
        /// produced - drivers clamp and round the frame they were given.
        /// </summary>
        void RecordPageBedRect(RawImage img, RectangleF requested)
        {
            float x = requested.Width > 0 ? requested.X : 0f;
            float y = requested.Height > 0 ? requested.Y : 0f;

            double w = (img != null && img.XDpi > 1) ? img.Width / img.XDpi
                     : (requested.Width > 0 ? requested.Width : _activeBedW);
            double h = (img != null && img.YDpi > 1) ? img.Height / img.YDpi
                     : (requested.Height > 0 ? requested.Height : _activeBedH);

            _pageBedRect = new RectangleF(x, y, (float)w, (float)h);
        }

        /// <summary>
        /// A white sheet the size of the selected scanner's platen, shown before
        /// anything has been scanned.
        ///
        /// 100 dpi is deliberate, and it is no longer the preview's resolution:
        /// this sheet is a white rectangle to drag a selection on, so its only
        /// requirements are that an A4 bed stays around 3 MB and that it is
        /// large enough to draw on. The crop maths does not care either way --
        /// a selection is held in inches on the glass, not in pixels of
        /// whatever happens to be displayed underneath it.
        /// </summary>
        static RawImage MakeBlankSheet(double widthIn, double heightIn, int dpi)
        {
            int w = Math.Max(16, (int)Math.Round(widthIn * dpi));
            int h = Math.Max(16, (int)Math.Round(heightIn * dpi));

            RawImage img = new RawImage();
            img.Width = w;
            img.Height = h;
            img.Channels = 3;
            img.BitsPerChannel = 8;
            img.Stride = w * 3;
            img.XDpi = dpi;
            img.YDpi = dpi;

            long len = (long)h * img.Stride;
            if (len > int.MaxValue) return null;

            img.Pixels = new byte[len];
            for (long i = 0; i < len; i++) img.Pixels[i] = 0xFF;
            return img;
        }

        /// <summary>
        /// Puts the platen on the canvas so the crop tools work immediately.
        /// Does nothing once a real page exists - a scan must never be replaced by
        /// a blank sheet.
        /// </summary>
        void ShowBlankSheet()
        {
            if (_hasRealPage || _canvas == null) return;

            RawImage sheet = MakeBlankSheet(_activeBedW, _activeBedH, 100);
            if (sheet == null) return;

            _canvas.PlaceholderDpi = _settings.Dpi;
            _canvas.SetPlaceholderImage(sheet);
            _pageBedRect = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);
            _manualCrop = false;

            // The crop is computed here from the platen rather than delegated to
            // ApplyPaperSizeToCanvasCrop, which starts from _settings.CropNorm and
            // would therefore re-apply a crop persisted from an earlier session to
            // a sheet it was never measured against.
            PaperSizeDef def = (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                             ? _supportedPaperEntries[_paperSizeIndex] : null;

            if (def == null || def.Key == "Maximum" || def.WidthIn <= 0 || def.HeightIn <= 0)
            {
                _canvas.ActivePaperLabel = null;
                _canvas.CropNorm = new RectangleF(0f, 0f, 1f, 1f);
            }
            else
            {
                double targetW = _settings.PaperLandscape ? Math.Max(def.WidthIn, def.HeightIn)
                                                          : Math.Min(def.WidthIn, def.HeightIn);
                double targetH = _settings.PaperLandscape ? Math.Min(def.WidthIn, def.HeightIn)
                                                          : Math.Max(def.WidthIn, def.HeightIn);

                float normW = (float)Math.Min(1.0, targetW / _activeBedW);
                float normH = (float)Math.Min(1.0, targetH / _activeBedH);

                // Anchored at the origin corner, not centred: that is where the
                // guides on a flatbed put the sheet.
                _canvas.ActivePaperLabel = def.Key + (_settings.PaperLandscape ? "  Landscape" : "  Portrait");
                _canvas.CropNorm = new RectangleF(0f, 0f, normW, normH);
            }

            _settings.CropNorm = _canvas.CropNorm;
            _canvas.Invalidate();
            UpdateStatus();
        }

        /// <summary>Display resolution for the platen view. See MakeBedComposite.</summary>
        const int BedViewDpi = 150;

        /// <summary>
        /// Paints a scanned region back onto a white sheet the size of the platen,
        /// so the canvas keeps showing the whole glass after a partial scan.
        ///
        /// Built at BedViewDpi rather than the scan's own resolution: a full A4 bed
        /// at 600 dpi would be over 100 MB, and this image is only ever looked at.
        /// Everything that leaves the application - the filmstrip, saving, the
        /// Photoshop handoff - uses the original full-resolution page instead.
        /// </summary>
        RawImage MakeBedComposite(RawImage page, RectangleF regionIn)
        {
            if (page == null || !page.IsValid) return null;
            if (_activeBedW < 1.0 || _activeBedH < 1.0) return null;

            int bedW = (int)Math.Round(_activeBedW * BedViewDpi);
            int bedH = (int)Math.Round(_activeBedH * BedViewDpi);
            if (bedW < 16 || bedH < 16) return null;

            Bitmap bed = null;
            Bitmap src = null;
            try
            {
                bed = new Bitmap(bedW, bedH, PixelFormat.Format24bppRgb);
                bed.SetResolution(BedViewDpi, BedViewDpi);

                using (Graphics g = Graphics.FromImage(bed))
                {
                    g.Clear(Color.White);

                    src = page.ToBitmap();
                    if (src != null)
                    {
                        int dx = (int)Math.Round(regionIn.X * BedViewDpi);
                        int dy = (int)Math.Round(regionIn.Y * BedViewDpi);
                        int dw = (int)Math.Round(regionIn.Width * BedViewDpi);
                        int dh = (int)Math.Round(regionIn.Height * BedViewDpi);

                        if (dw > 0 && dh > 0)
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(dx, dy, dw, dh));
                        }
                    }
                }

                return RawImage.FromBitmap(bed);
            }
            catch { return null; }
            finally
            {
                if (src != null) src.Dispose();
                if (bed != null) bed.Dispose();
            }
        }

        static bool IsFullCrop(RectangleF c)
        {
            return c.X <= 0.004f && c.Y <= 0.004f && c.Width >= 0.996f && c.Height >= 0.996f;
        }

        /// <summary>Maps the canvas crop onto the platen. Empty when it cannot.</summary>
        RectangleF CropToBedRegion()
        {
            if (_pageBedRect.Width < 0.05f || _pageBedRect.Height < 0.05f) return RectangleF.Empty;

            RectangleF c = _canvas.CropNorm;
            if (IsFullCrop(c)) return RectangleF.Empty;

            double left = _pageBedRect.X + c.X * _pageBedRect.Width;
            double top = _pageBedRect.Y + c.Y * _pageBedRect.Height;
            double w = c.Width * _pageBedRect.Width;
            double h = c.Height * _pageBedRect.Height;

            // Clamp to the platen: a crop dragged to the very edge of a preview can
            // round a hair past the bed, and drivers reject an out-of-range frame
            // outright rather than clipping it.
            if (left < 0) { w += left; left = 0; }
            if (top < 0) { h += top; top = 0; }
            if (left + w > _activeBedW) w = _activeBedW - left;
            if (top + h > _activeBedH) h = _activeBedH - top;

            // Below this the scan is not worth making and some drivers fail.
            if (w < 0.2 || h < 0.2) return RectangleF.Empty;

            return new RectangleF((float)left, (float)top, (float)w, (float)h);
        }

        void ShowCropRegionHint()
        {
            RectangleF r = CropToBedRegion();
            if (r.Width <= 0)
            {
                SetStatus("Scan area: the whole page.");
                return;
            }
            SetStatus(string.Format(CultureInfo.InvariantCulture,
                "Scan area: {0:0.00} × {1:0.00} in, {2:0.00} in from the left and {3:0.00} in from the top.",
                r.Width, r.Height, r.X, r.Y));
        }

        // =====================================================================
        // Batch strip
        // =====================================================================
        void ToggleBatchBar()
        {
            if (_batchBar == null) return;

            if (_batchBar.Visible && !_batchRunning)
            {
                _batchBar.Visible = false;
                if (_batchPill != null) _batchPill.Invalidate();
                LayoutAll();
                return;
            }

            _batchBar.Settings = _settings;
            _batchBar.State = _batchRunning ? BatchBarState.Running : BatchBarState.Armed;
            _batchBar.Visible = true;
            _batchBar.Refresh_();

            // The pill deliberately stays a secondary control while the strip is
            // open. The strip carries its own primary action, and a page with
            // Scan, Batch and Start batch all filled in the accent colour tells
            // the operator nothing about which one to press.
            if (_batchPill != null) _batchPill.Invalidate();

            LayoutAll();
            SetStatus("Batch: set the job on the strip, then press Start batch.");
        }

        void HideBatchBarIfIdle()
        {
            if (_batchBar == null || _batchRunning) return;
            _batchBar.State = BatchBarState.Armed;
            _batchBar.Refresh_();
        }

        /// <summary>
        /// The strip edits the same settings object the drawer does, so the two
        /// can never disagree - but the drawer controls were built from those
        /// values and have to be told.
        /// </summary>
        void OnBatchSettingsChanged()
        {
            try
            {
                if (_deviceBar != null)
                {
                    _deviceBar.SourceIndex = SourceToIndex(_settings.Source);
                    _deviceBar.Invalidate();
                }
                UpdateBatchFeedEnabled();
                if (_namePreview != null) _namePreview(null, EventArgs.Empty);
                if (_tgMultiPage != null)
                {
                    _tgMultiPage.Enabled = PageWriter.SupportsMultiPage(_settings.OutputFormat);
                    _tgMultiPage.Checked = _settings.MultiPageFile;
                }
                if (_tgDropBlank != null) _tgDropBlank.Checked = _settings.DropBlankPages;
                if (_tgUntilEmpty != null) _tgUntilEmpty.Checked = _settings.BatchUntilEmpty;
                if (_ddFormat != null)
                {
                    string f = (_settings.OutputFormat ?? "jpg").ToLowerInvariant();
                    _ddFormat.SelectedIndex = f.StartsWith("png") ? 1
                                            : f.StartsWith("tif") ? 2
                                            : f.StartsWith("pdf") ? 3 : 0;
                }
            }
            catch (Exception ex) { Log("could not mirror the batch settings: " + ex.Message); }
        }

        void StartBatch()
        {
            if (_busy) { SetStatus("A scan is already running."); return; }
            if (_device == null) { SetStatus("No scanner selected."); return; }

            bool feeder = _settings.Source == PaperSource.Feeder ||
                          _settings.Source == PaperSource.FeederDuplex;
            if (!feeder && _settings.BatchUntilEmpty)
            {
                // A flatbed cannot empty a tray. Saying so is better than
                // scanning the same sheet until the operator notices.
                SetStatus("A flatbed holds one sheet. Choose Feeder to scan a stack.");
                return;
            }

            _batchRunning = true;
            _batchBar.State = BatchBarState.Running;
            _batchBar.PagesDone = 0;
            _batchBar.DocumentsDone = 0;
            _batchBar.StartedAt = DateTime.Now;
            _batchBar.Refresh_();
            StartBatchTicker();

            StartScan(false);
        }

        /// <summary>
        /// Keeps the elapsed time and the sweeping progress line moving. One
        /// timer only while a batch is on screen: an always-running timer to animate a
        /// hidden bar is how an idle scanner ends up using a core.
        /// </summary>
        System.Windows.Forms.Timer _batchTicker;   // System.Threading has one too

        void StartBatchTicker()
        {
            if (_batchTicker == null)
            {
                _batchTicker = new System.Windows.Forms.Timer { Interval = 200 };
                _batchTicker.Tick += delegate
                {
                    if (_batchBar == null || !_batchBar.Visible ||
                        _batchBar.State != BatchBarState.Running)
                    {
                        _batchTicker.Stop();
                        return;
                    }
                    _batchBar.Refresh_();
                };
            }
            _batchTicker.Start();
        }

        void BatchPageArrived(int pageNumber)
        {
            if (_batchBar == null || !_batchRunning) return;
            _batchBar.PagesDone = pageNumber;
            _batchBar.Refresh_();
        }

        void EndBatch(int pages, bool cancelled)
        {
            if (!_batchRunning) return;
            _batchRunning = false;
            if (_batchTicker != null) _batchTicker.Stop();
            if (_batchBar == null) return;

            int documents = 1;
            try
            {
                List<RawImage> all = _film.AllImages();
                documents = Math.Max(1, BatchSplitter.Split(all, BatchOptionsFromSettings()).Count);
            }
            catch { }

            _batchBar.DocumentsDone = documents;
            _batchBar.State = BatchBarState.Finished;
            _batchBar.FinishedNote = (cancelled ? "Stopped after " : "") +
                pages + (pages == 1 ? " page" : " pages") +
                (documents > 1 ? ", " + documents + " documents" : "") +
                " — not saved yet";
            _batchBar.Refresh_();
        }

        /// <summary>Asks the running scan to stop. Safe to call when idle.</summary>
        void RequestCancel()
        {
            if (!_busy) return;
            if (_cancelling) { SetStatus("Still stopping the scanner…"); return; }

            _cancelling = true;
            SetScanButtonMode();
            SetStatus("Stopping the scan…");

            // Off the UI thread: CancelScan waits for the host to close its data
            // source, and blocking the message loop here would freeze the window
            // for as long as that takes.
            Thread t = new Thread(delegate ()
            {
                bool had = false;
                try { had = _broker.CancelScan(); }
                catch (Exception ex) { Log("cancel failed: " + ex.Message); }

                if (!had)
                {
                    try { BeginInvoke((MethodInvoker)delegate { _cancelling = false; SetScanButtonMode(); }); }
                    catch { }
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Keeps the dock in step with whether a scan is running.</summary>
        void SetScanButtonMode()
        {
            if (_scanPill == null) return;

            if (_busy)
            {
                _scanPill.Text = _cancelling ? "Stopping…" : "Cancel";
                _scanPill.Kind = PillKind.Danger;
                _scanPill.Enabled = !_cancelling;
            }
            else
            {
                _scanPill.Text = "Scan";
                _scanPill.Kind = PillKind.Primary;
                _scanPill.Enabled = true;
            }

            if (_previewPill != null) _previewPill.Enabled = !_busy;
            if (_fullBedPill != null) _fullBedPill.Enabled = !_busy;

            // On Assist the footer is not there at all unless a scan is
            // running, so the panel has to be re-measured when that changes.
            if (_activeSection == AiSection) LayoutInspectorBody();
            _scanPill.Invalidate();
        }

        void StartScan(bool preview) { StartScan(preview, false); }

        void StartScan(bool preview, bool wholeBed)
        {
            if (_busy) { SetStatus("A scan is already running."); return; }
            if (_device == null) { SetStatus("No scanner selected."); return; }

            _busy = true;
            _cancelling = false;
            _autoCropChangedPages = false;
            _autoCropNote = "";
            _lastScanPages.Clear();

            if (_canvas != null) { _canvas.SelectedRegion = -1; _canvas.DetectedRegions = null; }

            // Last scan's sheet describes last scan. Leaving it reachable while
            // a new page arrives would let the operator switch to a view of
            // work that is no longer on screen.
            DropSheet();
            if (_tbSheet != null) { _tbSheet.Checked = false; _tbSheet.Visible = false; }

            // One journal per session rather than per scan, so pages added by a
            // later "scan more" land in the same recoverable batch.
            if (!preview && _journal == null)
            {
                try
                {
                    _journal = ScanJournal.Begin(
                        (_settings.DeviceName ?? "scanner") + " at " + _settings.Dpi + " dpi");
                    _journal.Log = delegate (string m) { Log("journal: " + m); };
                }
                catch (Exception ex) { Log("could not start the journal: " + ex.Message); }
            }

            SetScanButtonMode();
            _canvas.Scanning = true;
            _deviceBar.SetBusy(true);
            string sizeDesc = (_settings.PaperSize == "Maximum" || string.IsNullOrEmpty(_settings.PaperSize)) ? "" : (_settings.PaperSize + " ");
            SetStatus(preview ? "Previewing…" : ("Scanning " + sizeDesc + "at " + _settings.Dpi + " dpi…"));

            ScanSettings s = new ScanSettings();
            s.Dpi = preview ? PreviewDpi() : _settings.Dpi;
            s.Mode = preview ? (_settings.PreviewMatchesScan ? _settings.Mode : _settings.PreviewMode) : _settings.Mode;
            s.Source = _settings.Source;

            // A flatbed holds one sheet, so asking for more would just scan the
            // same glass repeatedly. Only a feeder can deliver a batch, and there
            // "until empty" is expressed as a page count of zero.
            bool feeder = _settings.Source == PaperSource.Feeder ||
                          _settings.Source == PaperSource.FeederDuplex;
            s.PageCount = (preview || !feeder)
                ? 1
                : (_settings.BatchUntilEmpty ? 0 : Math.Max(1, _settings.PageCount));

            s.IsPreview = preview;
            s.ShowVendorUi = !preview && _settings.ShowVendorUi;
            if (preview)
            {
                // A preview takes the whole glass when nothing is selected,
                // because that is how the operator finds out what is on the bed.
                // Once they have drawn a selection, previewing the whole bed
                // again throws away the thing they are working on: they want to
                // see that area closer, not the platen once more. The whole bed
                // button is the way back, and it is the only thing that forces
                // it -- this used to be a setting whose default quietly meant
                // the selection was never honoured at all.
                RectangleF previewRegion = (!wholeBed && _manualCrop)
                    ? CropToBedRegion() : RectangleF.Empty;

                if (previewRegion.Width > 0.05f && previewRegion.Height > 0.05f)
                {
                    s.RegionLeftIn = previewRegion.X;
                    s.RegionTopIn = previewRegion.Y;
                    s.RegionWidthIn = previewRegion.Width;
                    s.RegionHeightIn = previewRegion.Height;
                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "Previewing the selected area, {0:0.00} × {1:0.00} in…",
                        previewRegion.Width, previewRegion.Height));
                }
                else
                {
                    s.RegionLeftIn = 0;
                    s.RegionTopIn = 0;
                    s.RegionWidthIn = _activeBedW;
                    s.RegionHeightIn = _activeBedH;
                }
            }
            else
            {
                // A crop the user drew themselves wins over the paper-size preset:
                // it is the more specific instruction, and it is the one they are
                // looking at.
                RectangleF cropRegion = _manualCrop ? CropToBedRegion() : RectangleF.Empty;

                if (cropRegion.Width > 0)
                {
                    s.RegionLeftIn = cropRegion.X;
                    s.RegionTopIn = cropRegion.Y;
                    s.RegionWidthIn = cropRegion.Width;
                    s.RegionHeightIn = cropRegion.Height;
                    _lastScanUsedManualCrop = true;

                    SetStatus(string.Format(CultureInfo.InvariantCulture,
                        "Scanning the selected area, {0:0.00} × {1:0.00} in at {2} dpi…",
                        cropRegion.Width, cropRegion.Height, s.Dpi));
                }
                else
                {
                    s.RegionLeftIn = _settings.RegionLeftIn;
                    s.RegionTopIn = _settings.RegionTopIn;
                    s.RegionWidthIn = Math.Min(_settings.RegionWidthIn, _activeBedW);
                    s.RegionHeightIn = Math.Min(_settings.RegionHeightIn, _activeBedH);
                    _lastScanUsedManualCrop = false;
                }
            }

            _lastRequestedRegion = new RectangleF(
                (float)s.RegionLeftIn, (float)s.RegionTopIn,
                (float)s.RegionWidthIn, (float)s.RegionHeightIn);

            DeviceDescriptor dev = _device;
            List<RawImage> got = new List<RawImage>();

            Thread t = new Thread(delegate ()
            {
                NsResult r;
                try
                {
                    r = _broker.Scan(dev, s,
                                     delegate (RawImage img)
                                     {
                                         if (preview) { got.Add(img); return true; }

                                         // One page in, one or many out. Everything
                                         // downstream - filmstrip, journal, export,
                                         // the Photoshop handoff - then treats each
                                         // item as a page in its own right.
                                         foreach (RawImage piece in ApplyAutoCrop(img))
                                         {
                                             got.Add(piece);
                                             lock (_lastScanPages) _lastScanPages.Add(piece);
                                             OnPageArrived(piece, got.Count);
                                         }
                                         return true;
                                     },
                                     delegate (string msg, int pct)
                                     {
                                         try { BeginInvoke((MethodInvoker)delegate { SetStatus(msg); }); }
                                         catch { }
                                     });
                }
                catch (Exception ex)
                {
                    r = NsResult.Fail(NsError.Unknown, ex.Message, "");
                }

                try { BeginInvoke((MethodInvoker)delegate { OnScanDone(r, got, preview); }); }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// <summary>
        /// Runs auto crop over a preview and draws the result on the canvas.
        ///
        /// One region also moves the scan box onto it, so pressing Scan captures
        /// that document and nothing else. Several regions leave the box alone -
        /// the whole platen has to be scanned for the items to be cut out of it
        /// afterwards - and are drawn numbered, because that order decides which
        /// page is which once they are saved.
        /// </summary>
        /// <summary>
        /// Looks again at the spot the operator pointed to.
        ///
        /// A click on bare glass means one of two things and only the glass can
        /// say which. Either there is a document there that detection missed --
        /// a pale card reads the same as the platen, which is the whole reason
        /// this engine has been hard -- or there is nothing there and the
        /// operator is dismissing what is on screen. So it asks first, and
        /// clears only when the answer is no.
        ///
        /// The reading of the page is the one already taken for the preview, so
        /// this costs a prompt rather than another pass of the encoder.
        /// </summary>
        void ProbeForItem(PointF norm)
        {
            if (_probing) return;
            if (_previewPage == null || norm.X <= 0 && norm.Y <= 0) { ClearDetections(); return; }

            _probing = true;
            try
            {
                // A fifth of the glass is already a generous document -- A5 on
                // an A4 bed is a quarter. Anything larger that a click produces
                // is the sheet everything is resting on, or the platen itself,
                // and answering a click on blank glass with "the whole page" is
                // worse than answering nothing.
                const double LargestAnItemCanBe = 0.20;

                RotatedBox found = SamProposals.ProbeAt(_previewPage, norm.X, norm.Y, LargestAnItemCanBe);
                if (found == null || !found.IsValid) { ClearDetections(); return; }

                // Something already covers it: the operator clicked a gap inside
                // an item rather than a new one, and there is nothing to add.
                if (AlreadyCovered(found) || Swallows(found)) { ClearDetections(); return; }

                AddRegionFromBox(found);
                _canvas.SelectedRegion = _canvas.DetectedRegions.Count - 1;
                PinStatus(string.Format(CultureInfo.InvariantCulture,
                    "Found one more item there, {0:0.00} × {1:0.00} in. {2} in all.",
                    found.Width / Dpi(_previewPage.XDpi), found.Height / Dpi(_previewPage.YDpi),
                    _cropPlan.Count));
            }
            catch (Exception ex) { Log("probe failed: " + ex.Message); ClearDetections(); }
            finally { _probing = false; }
        }

        static double Dpi(double value)
        {
            return value >= 1 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 300.0;
        }

        /// <summary>
        /// Whether a proposed region contains items already found.
        ///
        /// A click between two cards can come back as both cards and the gap.
        /// That is one more answer that is honest and useless: it is not a new
        /// item, it is the ones already on screen with a box drawn round them.
        /// </summary>
        bool Swallows(RotatedBox box)
        {
            if (_canvas.DetectedRegions == null || _previewPage == null) return false;

            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            foreach (PointF corner in box.Corners)
            {
                left = Math.Min(left, corner.X / _previewPage.Width);
                right = Math.Max(right, corner.X / _previewPage.Width);
                top = Math.Min(top, corner.Y / _previewPage.Height);
                bottom = Math.Max(bottom, corner.Y / _previewPage.Height);
            }

            foreach (PointF[] outline in _canvas.DetectedRegions)
            {
                if (outline == null || outline.Length < 3) continue;
                float cx = 0, cy = 0;
                foreach (PointF pt in outline) { cx += pt.X; cy += pt.Y; }
                cx /= outline.Length; cy /= outline.Length;
                if (cx > left && cx < right && cy > top && cy < bottom) return true;
            }
            return false;
        }

        bool AlreadyCovered(RotatedBox box)
        {
            if (_canvas.DetectedRegions == null || _previewPage == null) return false;
            PointF centre = box.Center;
            float nx = centre.X / _previewPage.Width, ny = centre.Y / _previewPage.Height;

            foreach (PointF[] outline in _canvas.DetectedRegions)
            {
                if (outline == null || outline.Length < 3) continue;
                bool inside = false;
                for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
                {
                    if ((outline[i].Y > ny) != (outline[j].Y > ny) &&
                        nx < (outline[j].X - outline[i].X) * (ny - outline[i].Y) /
                             (outline[j].Y - outline[i].Y) + outline[i].X) inside = !inside;
                }
                if (inside) return true;
            }
            return false;
        }

        /// <summary>
        /// Takes away everything on the page. Only reached when the operator
        /// clicked somewhere there is genuinely nothing.
        /// </summary>
        void ClearDetections()
        {
            if (_canvas == null) return;
            bool had = _canvas.DetectedRegions != null && _canvas.DetectedRegions.Count > 0;

            _canvas.SelectedRegion = -1;
            _canvas.DetectedRegions = null;
            _canvas.DetectedRegionConfidence = null;
            _cropPlan.Clear();
            _cropPlanCorners.Clear();
            _cropPlanOutlines.Clear();
            _canvas.Invalidate();

            if (had) PinStatus("Nothing there. The whole area will be scanned.");
        }

        /// <summary>
        /// Adds one more item to everything that describes the items, keeping
        /// the four lists the same length and in the same order.
        /// </summary>
        void AddRegionFromBox(RotatedBox box)
        {
            if (_previewPage == null || box == null || box.Corners == null) return;

            PointF[] norm = new PointF[box.Corners.Length];
            PointF[] bed = new PointF[box.Corners.Length];
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            for (int i = 0; i < norm.Length; i++)
            {
                norm[i] = new PointF(box.Corners[i].X / _previewPage.Width,
                                     box.Corners[i].Y / _previewPage.Height);
                bed[i] = new PointF((float)(norm[i].X * _activeBedW), (float)(norm[i].Y * _activeBedH));
                left = Math.Min(left, bed[i].X); right = Math.Max(right, bed[i].X);
                top = Math.Min(top, bed[i].Y); bottom = Math.Max(bottom, bed[i].Y);
            }

            if (_canvas.DetectedRegions == null)
            {
                _canvas.DetectedRegions = new List<PointF[]>();
                _canvas.DetectedRegionConfidence = new List<CropConfidence>();
            }
            if (_canvas.DetectedRegionConfidence == null)
                _canvas.DetectedRegionConfidence = new List<CropConfidence>();

            _canvas.DetectedRegions.Add(norm);
            _canvas.DetectedRegionConfidence.Add(CropConfidence.Good);
            _cropPlan.Add(RectangleF.FromLTRB(left, top, right, bottom));
            _cropPlanCorners.Add(bed.Length == 4 ? bed : new PointF[]
            {
                new PointF(left, top), new PointF(right, top),
                new PointF(right, bottom), new PointF(left, bottom)
            });
            _cropPlanOutlines.Add(null);
            _canvas.Invalidate();
        }

        /// <summary>
        /// Rewrites the plan for one item after the operator has reshaped it.
        ///
        /// The plan is held in inches on the glass, and the outline the operator
        /// dragged is a fraction of the preview, so the three descriptions of
        /// that item -- its envelope, its oriented corners and its free-form
        /// outline -- are all rebuilt from the shape now on screen. Rebuilding
        /// only the one entry matters: the others were measured and are still
        /// right, and a preview is not cheap enough to take again for a nudge.
        /// </summary>
        void RebuildPlanEntry(int index)
        {
            if (_canvas == null || _canvas.DetectedRegions == null) return;
            if (index < 0 || index >= _canvas.DetectedRegions.Count) return;
            if (index >= _cropPlan.Count || index >= _cropPlanCorners.Count) return;

            PointF[] norm = _canvas.DetectedRegions[index];
            if (norm == null || norm.Length < 3) return;

            PointF[] bed = new PointF[norm.Length];
            float left = float.MaxValue, top = float.MaxValue, right = float.MinValue, bottom = float.MinValue;
            for (int i = 0; i < norm.Length; i++)
            {
                bed[i] = new PointF((float)(norm[i].X * _activeBedW), (float)(norm[i].Y * _activeBedH));
                left = Math.Min(left, bed[i].X); right = Math.Max(right, bed[i].X);
                top = Math.Min(top, bed[i].Y); bottom = Math.Max(bottom, bed[i].Y);
            }

            _cropPlan[index] = RectangleF.FromLTRB(left, top, right, bottom);

            // Four points are the item's own corners, at its own angle. More
            // than four is free-form stock, whose corners are its envelope.
            if (bed.Length == 4) _cropPlanCorners[index] = bed;
            else
                _cropPlanCorners[index] = new PointF[]
                {
                    new PointF(left, top), new PointF(right, top),
                    new PointF(right, bottom), new PointF(left, bottom)
                };

            // Always the outline now, not only when it was free-form to begin
            // with. Once a corner has been pulled on its own the shape is a
            // quadrilateral and no longer describable by a box and an angle, so
            // the cut has to follow the outline or it will not follow the edit.
            if (index < _cropPlanOutlines.Count) _cropPlanOutlines[index] = bed;

            _canvas.Invalidate();
        }

        /// <summary>
        /// What can be done with one detected item, on a right click over it.
        /// </summary>
        void ShowRegionMenu(int index, Point at)
        {
            if (_canvas == null || _canvas.DetectedRegions == null) return;
            if (index < 0 || index >= _canvas.DetectedRegions.Count) return;

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.BackColor = Theme.Raised;
            menu.ForeColor = Theme.Text;
            menu.ShowImageMargin = false;

            int number = index + 1;
            ToolStripMenuItem toPhotoshop = new ToolStripMenuItem("Send item " + number + " to Photoshop");
            toPhotoshop.Click += delegate { SendRegionToPhotoshop(index); };
            menu.Items.Add(toPhotoshop);

            ToolStripMenuItem saveOne = new ToolStripMenuItem("Save item " + number + "\u2026");
            saveOne.Click += delegate { SaveRegion(index); };
            menu.Items.Add(saveOne);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem drop = new ToolStripMenuItem("Remove item " + number);
            drop.Click += delegate { RemoveRegion(index); };
            menu.Items.Add(drop);

            ToolStripMenuItem again = new ToolStripMenuItem("Look again, more closely");
            again.Enabled = _previewPage != null;
            again.Click += delegate { DetectAgain(); };
            menu.Items.Add(again);

            menu.Show(_canvas, at);
        }

        /// <summary>The one item, cut from what is on screen.</summary>
        RawImage CutRegion(int index)
        {
            if (_canvas == null || _canvas.Image == null || !_canvas.Image.IsValid) return null;

            // The contact sheet is a picture of pages, not a page. Cutting a
            // region out of it would hand Photoshop a screenshot.
            if (_showingSheet) return null;

            RectangleF covers;
            RawImage source = PageToCutFrom(out covers);
            if (source == null || !source.IsValid) return null;

            List<RawImage> pieces = ApplyAutoCrop(source, covers);
            if (pieces == null || index < 0 || index >= pieces.Count) return null;
            if (ReferenceEquals(pieces[index], source)) return null;   // nothing was cut
            return pieces[index];
        }

        /// <summary>
        /// The page an item should be cut from, and what it covers on the glass.
        ///
        /// Deliberately not <c>_canvas.Image</c>. Previewing a selection
        /// composites the result onto a picture of the whole bed built at
        /// BedViewDpi, so the displayed image is 150 dpi whatever the capture
        /// was. Cutting from that handed Photoshop a downscaled picture of the
        /// bed instead of the page, and the documents arrived at 150 ppi from a
        /// 300 dpi scan.
        ///
        /// The guard against cutting up the contact sheet was already here for
        /// the same reason -- it is a picture of pages, not a page -- and the
        /// bed view is the same mistake wearing different clothes.
        /// </summary>
        RawImage PageToCutFrom(out RectangleF covers)
        {
            if (_capturePage != null && _capturePage.IsValid)
            {
                covers = _captureBedRect;
                return _capturePage;
            }

            covers = _pageBedRect;
            return _canvas != null ? _canvas.Image : null;
        }

        void SendRegionToPhotoshop(int index)
        {
            RawImage piece = CutRegion(index);
            if (piece == null) { SetStatus("That item could not be cut from the preview."); return; }
            SendScanPagesToPhotoshop(new List<RawImage> { piece }, piece.XDpi);
        }

        /// <summary>
        /// Writes one item to the output folder, through the same batch writer
        /// every other save uses, so the naming and the file format are the ones
        /// the operator configured rather than a second set invented here.
        /// </summary>
        void SaveRegion(int index)
        {
            RawImage piece = CutRegion(index);
            if (piece == null) { SetStatus("That item could not be cut from the preview."); return; }

            try
            {
                Directory.CreateDirectory(_settings.OutputDirectory);
                int documents;
                List<string> written = BatchSplitter.WriteBatch(new List<RawImage> { piece },
                    BatchOptionsFromSettings(), ExportPlanFromSettings(), out documents);
                SetStatus(written.Count > 0
                    ? "Saved  " + Path.GetFileName(written[0])
                    : "That item could not be written.");
            }
            catch (Exception ex) { SetStatus("Could not save that item: " + ex.Message); }
        }

        /// <summary>
        /// Drops one item from the plan. Every list that describes the items is
        /// indexed the same way, so all of them lose the same entry or the
        /// numbering on screen stops matching the numbering in the files.
        /// </summary>
        void RemoveRegion(int index)
        {
            if (_canvas == null || _canvas.DetectedRegions == null) return;
            if (index < 0 || index >= _canvas.DetectedRegions.Count) return;

            _canvas.SelectedRegion = -1;
            _canvas.DetectedRegions.RemoveAt(index);
            if (_canvas.DetectedRegionConfidence != null && index < _canvas.DetectedRegionConfidence.Count)
                _canvas.DetectedRegionConfidence.RemoveAt(index);
            if (index < _cropPlan.Count) _cropPlan.RemoveAt(index);
            if (index < _cropPlanCorners.Count) _cropPlanCorners.RemoveAt(index);
            if (index < _cropPlanOutlines.Count) _cropPlanOutlines.RemoveAt(index);

            _canvas.Invalidate();
            PinStatus(_cropPlan.Count == 0
                ? "No items left. Scan will keep the whole area."
                : _cropPlan.Count + " item(s) will be cut from the scan.");
        }

        /// <summary>
        /// Runs detection again, asking a harder question than last time.
        ///
        /// Repeating the same question is pointless: detection is deterministic
        /// and gives the same answer on the same capture, which is exactly what
        /// the operator complained about after a miss. This sweeps more finely
        /// and accepts less certainty, so it can find what the first pass walked
        /// past -- and it takes longer, which is the trade being made.
        /// </summary>
        void DetectAgain()
        {
            if (_previewPage == null) { SetStatus("There is no preview to look at."); return; }
            SetStatus("Looking again, more closely…");
            _thoroughDetect = true;
            try { PreviewAutoCrop(_previewPage); }
            finally { _thoroughDetect = false; }
        }

        void PreviewAutoCrop(RawImage preview)
        {
            _canvas.SelectedRegion = -1;
            _canvas.DetectedRegions = null;
            _canvas.DetectedRegionConfidence = null;
            _cropPlan.Clear();
            _cropPlanCorners.Clear();
            _cropPlanOutlines.Clear();

            if (preview == null || !_settings.AutoCrop)
            {
                PinStatus("Preview ready — drag on the page to set the scan area.");
                _canvas.Invalidate();
                return;
            }

            List<CropRegion> items;
            Stopwatch cropWatch = Stopwatch.StartNew();
            PlatenDetectionReport detectionReport = new PlatenDetectionReport();
            try { items = PlatenDetector.Detect(preview, AutoCropOptionsFromSettings(), detectionReport); }
            catch (Exception ex)
            {
                Log("auto crop on the preview failed: " + ex.Message);
                PinStatus("Preview ready — drag on the page to set the scan area.");
                return;
            }
            cropWatch.Stop();

            // Kept on every preview, not only on failure. "It cropped, but a
            // few millimetres out" is a report that cannot be acted on without
            // the page it happened to, and that was the case for most of the
            // time this feature took to get working.
            SaveCropDiagnostic(preview, items, cropWatch.ElapsedMilliseconds, detectionReport);

            if (items == null || items.Count == 0)
            {
                PinStatus(detectionReport.UnresolvedCandidates > 0
                    ? "No reliable crop. Unresolved boundaries are recorded in diagnostics; review the preview and crop manually."
                    : "Preview ready. Nothing found to crop — drag to set the area yourself.");
                _canvas.Invalidate();
                return;
            }

            if (!_settings.MultiRegionCrop && items.Count > 1)
            {
                // Only the strongest item, since separating them was not asked for.
                items.Sort(delegate (CropRegion a, CropRegion b) { return b.Score.CompareTo(a.Score); });
                items.RemoveRange(1, items.Count - 1);
            }

            // The plan is held in inches on the glass, not as a fraction of the
            // preview. A scan may cover a different area - a paper size, or a
            // selection - and inches are the one description both agree on.
            //
            // Two rectangles, because two different things are being converted
            // between and they stopped being the same rectangle once a preview
            // could be of a selection:
            //
            //   covers  what the preview image is of, so preview pixels become
            //           a place on the glass
            //   shown   what the canvas is displaying, so a place on the glass
            //           becomes somewhere to draw
            //
            // Previewing a selection and then compositing it back onto a picture
            // of the whole bed makes these differ by the ratio between them. Read
            // the wrong one and every region is stretched by exactly that ratio,
            // which is how this was found.
            RectangleF whole = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);
            RectangleF covers = (_previewBedRect.Width > 0.05f && _previewBedRect.Height > 0.05f)
                                ? _previewBedRect : whole;
            RectangleF shown = (_pageBedRect.Width > 0.05f && _pageBedRect.Height > 0.05f)
                               ? _pageBedRect : whole;

            List<PointF[]> outlines = new List<PointF[]>();
            List<CropConfidence> confidences = new List<CropConfidence>();
            int reviewCount = 0;
            foreach (CropRegion item in items)
            {
                confidences.Add(item.Confidence);
                if (item.Confidence < CropConfidence.High) reviewCount++;
                _cropPlan.Add(new RectangleF(
                    covers.X + item.NormRect.X * covers.Width,
                    covers.Y + item.NormRect.Y * covers.Height,
                    item.NormRect.Width * covers.Width,
                    item.NormRect.Height * covers.Height));

                PointF[] corners = item.Outline ?? ((item.Box != null) ? item.Box.Corners : null);
                if (corners != null && corners.Length >= 3)
                {
                    PointF[] q = new PointF[corners.Length];
                    for (int i = 0; i < corners.Length; i++)
                    {
                        float bedX = covers.X + corners[i].X / preview.Width * covers.Width;
                        float bedY = covers.Y + corners[i].Y / preview.Height * covers.Height;
                        q[i] = new PointF((bedX - shown.X) / shown.Width,
                                          (bedY - shown.Y) / shown.Height);
                    }
                    outlines.Add(q);
                }
                else
                {
                    RectangleF n = item.NormRect;
                    float left = (covers.X + n.Left * covers.Width - shown.X) / shown.Width;
                    float top = (covers.Y + n.Top * covers.Height - shown.Y) / shown.Height;
                    float right = (covers.X + n.Right * covers.Width - shown.X) / shown.Width;
                    float bottom = (covers.Y + n.Bottom * covers.Height - shown.Y) / shown.Height;
                    outlines.Add(new PointF[]
                    {
                        new PointF(left, top), new PointF(right, top),
                        new PointF(right, bottom), new PointF(left, bottom)
                    });
                }
                PointF[] bedCorners = new PointF[4];
                PointF[] orientation = item.Box.Corners;
                for (int corner = 0; corner < 4; corner++)
                    bedCorners[corner] = new PointF(
                        covers.X + orientation[corner].X / preview.Width * covers.Width,
                        covers.Y + orientation[corner].Y / preview.Height * covers.Height);
                _cropPlanCorners.Add(bedCorners);
                PointF[] bedOutline = null;
                if (item.Outline != null)
                {
                    bedOutline = new PointF[item.Outline.Length];
                    for (int vertex = 0; vertex < bedOutline.Length; vertex++)
                        bedOutline[vertex] = new PointF(
                            covers.X + item.Outline[vertex].X / preview.Width * covers.Width,
                            covers.Y + item.Outline[vertex].Y / preview.Height * covers.Height);
                }
                _cropPlanOutlines.Add(bedOutline);
            }
            _canvas.DetectedRegions = outlines;
            _canvas.DetectedRegionConfidence = confidences;
            _canvas.Invalidate();

            if (items.Count == 1)
            {
                CropRegion only = items[0];
                PinStatus(string.Format(CultureInfo.InvariantCulture,
                    "Found one item, {0:0.00} × {1:0.00} in{2}. Scan will crop to it.",
                    only.WidthInches, only.HeightInches,
                    Math.Abs(only.SkewDegrees) >= 0.3f
                        ? ", " + only.SkewDegrees.ToString("0.0", CultureInfo.InvariantCulture) + "° off square"
                        : ""));
            }
            else
            {
                PinStatus("Found " + items.Count + " items. Scan will produce " +
                          items.Count + " separate pages.");
            }
            if (detectionReport.UnresolvedCandidates > 0)
            {
                PinStatus("Found " + items.Count + " items; " + detectionReport.UnresolvedCandidates
                    + " unresolved candidate(s). Check the whole preview and diagnostics before scanning.");
                return;
            }
            if (reviewCount > 0)
                PinStatus("Found " + items.Count + " items. Review the " + reviewCount +
                    " dashed amber outline(s): an edge is clipped, irregular or has weaker support.");
        }

        /// <summary>
        /// Writes the preview and what the engine made of it, so a detection
        /// failure can be reproduced offline. One pair of files, overwritten
        /// each time, under the application's own data folder - never uploaded
        /// and never sent anywhere.
        /// </summary>
        /// <summary>
        /// Shuffles previous previews down before a new one is written, so the
        /// last few survive. previous1 is the one before this; previous4 the
        /// oldest kept.
        /// </summary>
        static void RotateDiagnostics(string dir, int keep)
        {
            try
            {
                for (int slot = keep - 1; slot >= 1; slot--)
                {
                    MoveDiagnostic(dir, slot == 1 ? "last_preview" : "previous" + (slot - 1),
                                   "previous" + slot);
                }
            }
            catch { }
        }

        static void MoveDiagnostic(string dir, string from, string to)
        {
            foreach (string extension in new string[] { ".png", ".txt" })
            {
                string source = Path.Combine(dir, from + extension);
                string target = Path.Combine(dir, to + extension);
                try
                {
                    if (!File.Exists(source)) continue;
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(source, target);
                }
                catch { }
            }
        }

        string SaveCropDiagnostic(RawImage preview, List<CropRegion> regions, long elapsedMilliseconds, PlatenDetectionReport report)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "diagnostics");
                Directory.CreateDirectory(dir);

                // Saved exactly as the detector saw it.
                //
                // This used to downscale anything over 1400 pixels "to roughly
                // what the engine works on", to keep the file small. It kept the
                // file small and made the diagnostic useless for the failures
                // that matter most: a 300 dpi preview that measures a card 15 mm
                // too wide cannot be reproduced from a third-size copy of it,
                // because the reduction is part of what went wrong. A diagnostic
                // that alters the evidence is worse than no diagnostic.
                RawImage sample = preview;

                // Keep the last few, not just the last.
                //
                // A preview that comes out wrong is the evidence, and the first
                // thing anyone does with a wrong preview is press Preview again -
                // which used to overwrite it. Several failures were lost that way
                // before anyone could look at them.
                RotateDiagnostics(dir, 5);

                string png = Path.Combine(dir, "last_preview.png");
                PageWriter.SaveSingle(sample, png, "png");

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("NextScan auto crop diagnostic");
                sb.AppendLine(DateTime.Now.ToString("u", CultureInfo.InvariantCulture));
                sb.AppendLine("device      : " + (_settings.DeviceName ?? ""));
                sb.AppendLine("saved       : " + sample.Width + "x" + sample.Height +
                              " @ " + Math.Round(sample.XDpi) + " dpi");
                sb.AppendLine("source      : " + preview.Width + "x" + preview.Height +
                              " " + preview.Channels + "ch " + preview.BitsPerChannel + "bpc @ " +
                              Math.Round(preview.XDpi) + " dpi");
                sb.AppendLine("bed         : " + _activeBedW.ToString("0.00", CultureInfo.InvariantCulture) +
                              " x " + _activeBedH.ToString("0.00", CultureInfo.InvariantCulture) + " in");
                sb.AppendLine("autoCrop    : " + _settings.AutoCrop);
                sb.AppendLine("multiRegion : " + _settings.MultiRegionCrop);
                sb.AppendLine("deskew      : " + _settings.AutoDeskew);
                sb.AppendLine("cropByUser  : " + _cropDrawnByUser);
                sb.AppendLine("cropNorm    : " + _canvas.CropNorm);
                // Record the detector that made the displayed plan. Running a
                // second policy here previously reported unrelated timings and
                // unrelated item counts in every preview diagnostic.
                sb.AppendLine("detected    : " + (regions != null && regions.Count > 0));
                sb.AppendLine("elapsedMs   : " + elapsedMilliseconds);

                sb.AppendLine("platen found: " + (regions == null ? -1 : regions.Count));
                if (regions != null)
                    foreach (CropRegion c in regions)
                    {
                        sb.AppendLine("   " + Describe(c));
                        if (c.Outline == null) continue;
                        sb.Append("   outline pixels:");
                        foreach (PointF vertex in c.Outline)
                            sb.AppendFormat(CultureInfo.InvariantCulture, " {0:0.00},{1:0.00}", vertex.X, vertex.Y);
                        sb.AppendLine();
                    }

                sb.AppendLine("candidate decisions:");
                foreach (string line in report.Lines) sb.AppendLine("   " + line);
                sb.AppendLine("unresolved candidates: " + report.UnresolvedCandidates);
                string txt = Path.Combine(dir, "last_preview.txt");
                File.WriteAllText(txt, sb.ToString(), Encoding.UTF8);
                Log("crop diagnostic written to " + dir);
                return dir;
            }
            catch (Exception ex)
            {
                Log("could not write the crop diagnostic: " + ex.Message);
                return null;
            }
        }

        static string Describe(CropRegion c)
        {
            if (c == null) return "(null)";
            return string.Format(CultureInfo.InvariantCulture,
                "{0:0.00} x {1:0.00} in at {2:0.000},{3:0.000}  {4:0.0} deg  {5}  {6}",
                c.WidthInches, c.HeightInches, c.NormRect.X, c.NormRect.Y,
                c.SkewDegrees, c.Confidence, c.Reason ?? "");
        }

        AutoCropOptions AutoCropOptionsFromSettings()
        {
            return new AutoCropOptions
            {
                Enabled = _settings.AutoCrop,
                MultiRegion = _settings.MultiRegionCrop,
                Deskew = _settings.AutoDeskew,
                Thorough = _thoroughDetect,

                // Detection only ever runs on a preview -- a scan applies the
                // plan the preview already made -- so this costs the preview a
                // second and a half and costs a scan nothing.
                UseModel = _settings.UseModel
            };
        }

        /// <summary>
        /// Cuts the page into the pieces the preview marked out.
        ///
        /// Nothing is detected here. If there is no plan - because no preview was
        /// taken, or it found nothing, or the settings changed since - the page
        /// is returned whole. A scan is never cropped on something the operator
        /// has not been shown.
        /// </summary>
        List<RawImage> ApplyAutoCrop(RawImage page)
        {
            // The plan is in inches on the glass; a scanned page covers the
            // region the scanner was asked for. Map one onto the other.
            RectangleF region = _lastRequestedRegion;
            if (region.Width <= 0.01f || region.Height <= 0.01f)
                region = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);
            return ApplyAutoCrop(page, region);
        }

        /// <summary>
        /// Cuts a page into the pieces the preview marked out, where the page is
        /// known to cover <paramref name="region"/> of the glass.
        ///
        /// The scan path passes the region it asked the scanner for. The
        /// Photoshop handoff passes the area the page on screen came from, which
        /// may be a preview of the whole bed rather than a scan of part of it.
        /// </summary>
        List<RawImage> ApplyAutoCrop(RawImage page, RectangleF region)
        {
            List<RawImage> single = new List<RawImage> { page };
            if (page == null) return single;

            if (!_settings.AutoCrop) { _autoCropNote = "auto crop is off"; return single; }
            if (_cropPlan.Count == 0)
            {
                _autoCropNote = "no preview, so the whole area was kept";
                return single;
            }
            if (region.Width <= 0.01f || region.Height <= 0.01f)
                region = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);

            List<RawImage> pieces = new List<RawImage>();
            int index = 0;

            foreach (RectangleF inches in _cropPlan)
            {
                index++;
                RectangleF norm = new RectangleF(
                    (inches.X - region.X) / region.Width,
                    (inches.Y - region.Y) / region.Height,
                    inches.Width / region.Width,
                    inches.Height / region.Height);

                Rectangle box = new Rectangle(
                    (int)Math.Round(norm.X * page.Width), (int)Math.Round(norm.Y * page.Height),
                    (int)Math.Round(norm.Width * page.Width), (int)Math.Round(norm.Height * page.Height));

                box = Rectangle.Intersect(box, new Rectangle(0, 0, page.Width, page.Height));
                if (box.Width < 16 || box.Height < 16)
                {
                    Log("planned item " + index + " falls outside the scanned area; skipped");
                    continue;
                }

                RawImage cut = null;
                if (index <= _cropPlanOutlines.Count && _cropPlanOutlines[index - 1] != null)
                {
                    cut = PolygonCropExtraction.Extract(page, new PointF[][] { _cropPlanOutlines[index - 1] },
                        _cropPlanCorners[index - 1], region, index, _settings.AutoDeskew);
                    if (cut == null)
                    {
                        _autoCropNote = "the preview outline could not be applied; the whole scan was kept";
                        return single;
                    }
                }
                else if (_settings.AutoDeskew && index <= _cropPlanCorners.Count)
                    cut = PlannedCropExtraction.Extract(page, _cropPlanCorners[index - 1], region, index);
                if (cut == null) cut = CutPage(page, box);
                if (cut != null && cut.IsValid) { cut.PageIndex = index; pieces.Add(cut); }
            }

            if (pieces.Count == 0)
            {
                _autoCropNote = "the marked areas fell outside the scan";
                return single;
            }

            _autoCropChangedPages = true;
            _autoCropNote = pieces.Count > 1
                ? "cropped to the " + pieces.Count + " items marked on the preview"
                : "cropped to the item marked on the preview";
            return pieces;
        }

        /// <summary>Copies a rectangle out of a page, keeping its depth and resolution.</summary>
        static RawImage CutPage(RawImage page, Rectangle box)
        {
            try
            {
                if (page.BitsPerChannel == 1) return null;
                int step = page.BitsPerChannel == 16 ? 2 : 1;
                int bpp = page.Channels * step;

                RawImage cut = new RawImage
                {
                    Width = box.Width,
                    Height = box.Height,
                    Channels = page.Channels,
                    BitsPerChannel = page.BitsPerChannel,
                    Stride = box.Width * bpp,
                    XDpi = page.XDpi,
                    YDpi = page.YDpi
                };
                cut.Pixels = new byte[(long)cut.Stride * cut.Height];

                for (int y = 0; y < box.Height; y++)
                    Array.Copy(page.Pixels, (long)(box.Y + y) * page.Stride + (long)box.X * bpp,
                               cut.Pixels, (long)y * cut.Stride, cut.Stride);
                return cut;
            }
            catch { return null; }
        }

        /// <summary>
        /// Called from the scan thread for every page, as it lands.
        ///
        /// Two things happen here that used to wait until the whole scan
        /// finished. The page goes to disk, so losing the program does not lose
        /// the batch; and it appears in the filmstrip, so a fifty-page feed
        /// shows progress instead of an empty window for several minutes.
        /// </summary>
        void OnPageArrived(RawImage img, int number)
        {
            // Spooling first, and on this thread: it only queues, and doing it
            // before the UI hop means a page is durable even if the window is
            // busy or closing.
            try { if (_journal != null) _journal.AddPage(img); }
            catch (Exception ex) { Log("could not spool page " + number + ": " + ex.Message); }

            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _film.AddPage(img);
                    _hasRealPage = true;

                    // Only from the second page on. For a single-page scan the
                    // canvas placement is decided once the scan finishes - a
                    // cropped sheet is shown back in its place on the glass -
                    // and setting it here first would just flash the wrong view.
                    if (number > 1) _canvas.SetImage(img);

                    BatchPageArrived(number);
                    SetStatus("Page " + number + " received" +
                              (number > 1 ? ".  " + _film.Count + " in this session." : "…"));
                });
            }
            catch { }
        }

        void OnScanDone(NsResult r, List<RawImage> pages, bool preview)
        {
            _busy = false;
            _cancelling = false;
            SetScanButtonMode();
            _canvas.Scanning = false;
            _deviceBar.SetBusy(false);
            UpdateDeviceChip();

            if (!r.Ok)
            {
                // A cancel is a normal outcome, not a fault: it must not paint the
                // connection dot red or offer a remedy for something the user did
                // on purpose. Pages already delivered are still kept.
                bool wasCancelled = (r.Code == NsError.TwainCancelled || r.Code == NsError.WiaCancelled);

                if (wasCancelled)
                {
                    int kept = pages.Count;
                    SetStatus(kept > 0
                        ? "Scan stopped. " + kept + " page" + (kept == 1 ? "" : "s") + " already received were kept."
                        : "Scan stopped.");
                }
                else
                {
                    SetStatus(r.Message + (string.IsNullOrEmpty(r.Remedy) ? "" : "  —  " + r.Remedy));
                    _deviceBar.Connected = false;
                    _deviceBar.Invalidate();
                }
                // A feeder may fail after delivering good pages. Keep those pages
                // available for saving instead of discarding the whole partial batch.
                // The pages themselves are already in the filmstrip: they were
                // put there as they arrived. A feeder that fails at page 40
                // leaves those 40 exactly where the operator has been watching
                // them appear.
                if (!preview && pages.Count > 0) _canvas.SetImage(pages[pages.Count - 1]);
                if (!preview) EndBatch(pages.Count, wasCancelled);
                return;
            }

            if (pages.Count == 0) { SetStatus("The scanner returned no page."); return; }

            _hasRealPage = true;

            if (preview)
            {
                _previewPage = pages[0];
                _canvas.SetImage(pages[0]);
                _canvas.CaptureDpi = pages[0].XDpi;

                // Where on the glass this preview actually came from. It is no
                // longer always the whole bed, and everything downstream -- the
                // detections, the plan, the crop maths -- is in bed inches, so
                // getting this wrong puts every region in the wrong place.
                RectangleF reg = _lastRequestedRegion;
                RecordPageBedRect(pages[0], reg);

                _previewBedRect = (reg.Width > 0.05f && reg.Height > 0.05f)
                    ? reg : new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);

                _capturePage = pages[0];
                _captureBedRect = _previewBedRect;

                bool partial = reg.Width > 0.05f && reg.Height > 0.05f &&
                               (reg.Width < _activeBedW - 0.05 || reg.Height < _activeBedH - 0.05);

                if (partial)
                {
                    // Shown where it came from rather than filling the window:
                    // the operator is looking closer at one part of the bed, not
                    // at a new and smaller bed.
                    RawImage bedView = MakeBedComposite(pages[0], reg);
                    if (bedView != null)
                    {
                        _canvas.SetBedViewImage(bedView);
                        _pageBedRect = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);
                    }

                    // The selection stands: it is what was just previewed, and
                    // pressing Preview again should mean the same area, not the
                    // whole platen.
                    _canvas.CropNorm = new RectangleF(
                        (float)(reg.X / _activeBedW), (float)(reg.Y / _activeBedH),
                        (float)(reg.Width / _activeBedW), (float)(reg.Height / _activeBedH));
                    _settings.CropNorm = _canvas.CropNorm;
                    _manualCrop = true;
                    _cropDrawnByUser = true;
                }
                else
                {
                    // A whole-bed preview replaces whatever was selected before
                    // it, so an earlier hand-drawn selection no longer applies.
                    // Failing to clear this is what switched auto crop off for a
                    // whole session after a single drag.
                    _manualCrop = false;
                    _cropDrawnByUser = false;

                    if (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                        ApplyPaperSizeToCanvasCrop(_supportedPaperEntries[_paperSizeIndex]);
                }

                UpdateProcessedPreview();

                // Show what auto crop can see, on the preview, before the
                // operator commits to a full-resolution pass. Without this the
                // feature is invisible until after the scan - which reads as it
                // not working at all.
                PreviewAutoCrop(pages[0]);
            }
            else
            {
                RawImage last = pages[pages.Count - 1];
                _canvas.SetImage(last);
                _canvas.CaptureDpi = last.XDpi;
                RecordPageBedRect(last, _lastRequestedRegion);

                // Taken here, before the composite below overwrites _pageBedRect
                // with the whole platen.
                _capturePage = last;
                _captureBedRect = _pageBedRect;

                // A partial scan is shown back in its place on the glass, so the
                // selection can be widened and re-scanned without another preview.
                RectangleF reg = _lastRequestedRegion;
                bool partial = !_autoCropChangedPages &&
                               reg.Width > 0.05f && reg.Height > 0.05f &&
                               (reg.Width < _activeBedW - 0.05 || reg.Height < _activeBedH - 0.05);

                RawImage bedView = partial ? MakeBedComposite(last, reg) : null;

                if (bedView != null)
                {
                    _canvas.SetBedViewImage(bedView);
                    _pageBedRect = new RectangleF(0f, 0f, (float)_activeBedW, (float)_activeBedH);

                    // Leave the box exactly where the scan came from: it is the
                    // obvious thing to drag outwards for a second, larger pass.
                    _canvas.CropNorm = new RectangleF(
                        (float)(reg.X / _activeBedW), (float)(reg.Y / _activeBedH),
                        (float)(reg.Width / _activeBedW), (float)(reg.Height / _activeBedH));
                    _settings.CropNorm = _canvas.CropNorm;
                    _manualCrop = true;
                    _lastScanUsedManualCrop = false;
                }
                else if (_lastScanUsedManualCrop)
                {
                    _canvas.CropNorm = new RectangleF(0f, 0f, 1f, 1f);
                    _settings.CropNorm = _canvas.CropNorm;
                    _manualCrop = false;
                    _lastScanUsedManualCrop = false;
                }
                else if (_paperSizeIndex >= 0 && _paperSizeIndex < _supportedPaperEntries.Count)
                {
                    ApplyPaperSizeToCanvasCrop(_supportedPaperEntries[_paperSizeIndex]);
                }

                UpdateProcessedPreview();
                OfferSheet(pages);
                PinStatus("Scanned " + pages.Count + " page" + (pages.Count == 1 ? "" : "s") +
                          ".  " + _film.Count + " in this session." +
                          (_autoCropNote.Length > 0 ? "  (" + _autoCropNote + ")" : ""));
                EndBatch(pages.Count, false);

                // Started from Photoshop's own Import menu: the page goes back
                // down the pipe it came up, not out through a file. Nothing is
                // written to disk, the bit depth is whatever was scanned, and
                // the window closes once Photoshop has the pixels.
                if (StudioPsBridge.Serving && pages.Count > 0)
                {
                    PinStatus(pages.Count == 1
                              ? "Handing the scan to Photoshop…"
                              : "Handing " + pages.Count + " items to Photoshop…");
                    string whichProfile;
                    byte[] icc = ColourProfileInUse(out whichProfile);
                    Log(whichProfile);
                    StudioPsBridge.PublishAll(pages, icc, Log);
                    BeginInvoke((MethodInvoker)delegate { Close(); });
                }
                else if (_settings.OpenInPhotoshop)
                {
                    if (pages.Count > 1) SendScanPagesToPhotoshop(pages);
                    else SendToPhotoshop();
                }
            }

            UpdateStatus();
        }

        // =====================================================================
        // Output
        // =====================================================================
        void SaveSession()
        {
            List<RawImage> pages = _film.AllImages();
            if (pages.Count == 0) { SetStatus("There are no pages to save yet."); return; }

            try
            {
                Directory.CreateDirectory(_settings.OutputDirectory);

                int documents;
                List<string> written = BatchSplitter.WriteBatch(pages, BatchOptionsFromSettings(),
                                                                ExportPlanFromSettings(), out documents);

                if (written.Count == 0)
                {
                    SetStatus(documents == 0
                        ? "Every page was blank, so nothing was saved."
                        : "Nothing could be written. Check the output folder.");
                    return;
                }

                string what = written.Count == 1
                    ? Path.GetFileName(written[0])
                    : written.Count + " files";

                // Say how the pages were grouped whenever that was not obvious,
                // so a separation rule that misfired is visible immediately
                // rather than at the end of a fifty-page job.
                string how = "";
                if (documents > 1) how = "  (" + documents + " documents)";
                else if (pages.Count > written.Count && written.Count == 1) how = "  (" + pages.Count + " pages)";

                // The pages are on disk in their final form, so the spool has
                // done its job. Only now - not when the scan ended, and not when
                // the window closes - is it safe to throw away.
                RetireJournal();

                SetStatus("Saved " + what + how + "  in " + _settings.OutputDirectory);
            }
            catch (Exception ex) { SetStatus("Save failed: " + ex.Message); }
        }

        // =====================================================================
        // Crash-safe session journal
        // =====================================================================
        /// <summary>Discards the spool after the pages have been exported.</summary>
        void RetireJournal()
        {
            ScanJournal j = _journal;
            _journal = null;
            if (j == null) return;

            try { j.Flush(10000); j.Complete(); }
            catch (Exception ex) { Log("could not retire the journal: " + ex.Message); }
        }

        /// <summary>
        /// Leaves the spool on disk so the next start can offer it back. Used on
        /// the way out with pages still unsaved.
        /// </summary>
        void KeepJournalForRecovery()
        {
            ScanJournal j = _journal;
            _journal = null;
            if (j == null) return;

            try
            {
                j.Flush(10000);
                if (j.WrittenPages > 0) j.Abandon();
                else j.Complete();      // nothing was scanned; leave no husk behind
            }
            catch (Exception ex) { Log("could not close the journal: " + ex.Message); }
        }

        /// <summary>
        /// Looks for a batch a previous run did not finish. Nothing is loaded
        /// yet - a hundred pages of 48-bit film would be several gigabytes, and
        /// the operator may not want them back at all.
        /// </summary>
        void CheckForRecoverableSession()
        {
            try
            {
                List<JournalSession> found = ScanJournal.FindIncomplete();
                if (found.Count == 0) return;

                _recoverable = found[0];
                UpdateRecoverUi();
                SetStatus("A previous session left " + _recoverable.Pages +
                          " unsaved page(s). Recover them from the File panel.");

                // Anything older is not worth carrying: offering a list of past
                // failures is clutter, and they can still be found on disk.
                for (int i = 1; i < found.Count; i++) ScanJournal.Discard(found[i].Directory);
            }
            catch (Exception ex) { Log("recovery check failed: " + ex.Message); }
        }

        void RecoverSession()
        {
            if (_recoverable == null) return;

            JournalSession session = _recoverable;
            SetStatus("Recovering " + session.Pages + " page(s)…");
            Application.DoEvents();

            try
            {
                List<RawImage> pages = ScanJournal.LoadPages(session.Directory);
                if (pages.Count == 0)
                {
                    SetStatus("Nothing in that session could be read back.");
                    ScanJournal.Discard(session.Directory);
                    _recoverable = null;
                    UpdateRecoverUi();
                    return;
                }

                foreach (RawImage img in pages) _film.AddPage(img);
                _hasRealPage = true;
                _canvas.SetImage(pages[pages.Count - 1]);
                UpdateProcessedPreview();

                // The recovered pages are now this session's unsaved work, so
                // they need a spool of their own: recovering and then crashing
                // again must not lose them a second time.
                ScanJournal.Discard(session.Directory);
                _recoverable = null;
                UpdateRecoverUi();

                if (_journal == null)
                {
                    try
                    {
                        _journal = ScanJournal.Begin("recovered session");
                        _journal.Log = delegate (string m) { Log("journal: " + m); };
                        foreach (RawImage img in pages) _journal.AddPage(img);
                    }
                    catch (Exception ex) { Log("could not re-spool the recovered pages: " + ex.Message); }
                }

                SetStatus("Recovered " + pages.Count + " page" + (pages.Count == 1 ? "" : "s") +
                          (pages.Count < session.Pages
                            ? "  (" + (session.Pages - pages.Count) + " could not be read)"
                            : "") + ".  Save them to keep them.");
            }
            catch (Exception ex) { SetStatus("Recovery failed: " + ex.Message); }
        }

        void UpdateRecoverUi()
        {
            if (_recoverPill == null) return;
            bool has = _recoverable != null;
            _recoverPill.Visible = has;
            if (has) _recoverPill.Text = "Recover " + _recoverable.Pages + " unsaved page(s)";
            _recoverPill.Invalidate();
        }

        // =====================================================================
        // Watched folder
        // =====================================================================
        string HotPathText()
        {
            return string.IsNullOrEmpty(_settings.HotFolderPath)
                ? "No folder chosen yet"
                : _settings.HotFolderPath;
        }

        void StartHotFolder()
        {
            if (_hot != null) return;

            if (string.IsNullOrEmpty(_settings.HotFolderPath) || !Directory.Exists(_settings.HotFolderPath))
            {
                SetStatus("Choose a folder to watch first.");
                return;
            }

            try
            {
                HotFolderOptions o = new HotFolderOptions
                {
                    WatchFolder = _settings.HotFolderPath,
                    OnSuccess = _settings.HotFolderDisposition,
                    Grouping = _settings.HotFolderGroupPerBatch
                        ? HotFolderGrouping.PerBatch
                        : HotFolderGrouping.PerFile
                };

                HotFolder hf = new HotFolder(o, ExportPlanFromSettings(), BatchOptionsFromSettings());
                hf.Log = delegate (string m) { Log("hot folder: " + m); };
                hf.Processed = delegate (HotFolderResult r) { OnHotFolderResult(r); };

                hf.Start();
                _hot = hf;
                SetStatus("Watching " + _settings.HotFolderPath);
            }
            catch (Exception ex)
            {
                // The constructor refuses configurations that cannot work - most
                // often output nested inside the watched folder - and its message
                // says what to change, so it is worth showing verbatim.
                SetStatus(ex.Message);
            }
            UpdateHotFolderUi();
        }

        void StopHotFolder()
        {
            HotFolder hf = _hot;
            _hot = null;
            if (hf == null) return;

            try { hf.Stop(); } catch (Exception ex) { Log("stopping the hot folder failed: " + ex.Message); }
            UpdateHotFolderUi();
        }

        /// <summary>
        /// Called from the hot folder's own thread, so everything it touches has
        /// to be marshalled onto the UI thread first.
        /// </summary>
        void OnHotFolderResult(HotFolderResult r)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    UpdateHotFolderUi();
                    if (r == null) return;

                    SetStatus(r.Ok
                        ? "Watched folder: wrote " + (r.Written.Count == 1
                            ? Path.GetFileName(r.Written[0])
                            : r.Written.Count + " files")
                        : "Watched folder: " + Path.GetFileName(r.SourcePath) + " - " + r.Error);
                });
            }
            catch (Exception ex) { Log("hot folder update failed: " + ex.Message); }
        }

        void UpdateHotFolderUi()
        {
            if (_hotPill != null)
            {
                bool on = _hot != null;
                _hotPill.Text = on ? "Stop watching" : "Start watching";
                _hotPill.Kind = on ? PillKind.Danger : PillKind.Primary;
                _hotPill.Enabled = on || !string.IsNullOrEmpty(_settings.HotFolderPath);
                _hotPill.Invalidate();
            }

            if (_hotStatus == null) return;

            if (_hot == null)
            {
                _hotStatus.Text = "Not running.";
                return;
            }

            _hotStatus.Text = string.Format(CultureInfo.InvariantCulture,
                "Running.  {0} file(s) in, {1} written{2}",
                _hot.SucceededFiles, _hot.WrittenFiles,
                _hot.FailedFiles > 0 ? ", " + _hot.FailedFiles + " in _errors" : "");
        }

        /// <summary>Turns the current settings into an export plan.</summary>
        ExportPlan ExportPlanFromSettings()
        {
            return new ExportPlan
            {
                Directory = _settings.OutputDirectory,
                Pattern = _settings.OutputNamePattern,
                Format = (_settings.OutputFormat ?? "jpg").ToLowerInvariant(),
                JpegQuality = _settings.JpegQuality,
                MultiPage = _settings.MultiPageFile,
                Context = new NameContext
                {
                    Stamp = DateTime.Now,
                    Device = _settings.DeviceName ?? "",
                    Dpi = _settings.Dpi,
                    Mode = ModeToken(_settings.Mode),
                    Source = SourceToken(_settings.Source)
                }
            };
        }

        BatchOptions BatchOptionsFromSettings()
        {
            return new BatchOptions
            {
                Separation = _settings.BatchSeparation,
                PagesPerDocument = Math.Max(1, _settings.PagesPerDocument),
                DropBlankPages = _settings.DropBlankPages
            };
        }

        static string ModeToken(ColorMode m)
        {
            if (m == ColorMode.BlackWhite1) return "bw";
            if (m == ColorMode.Gray8 || m == ColorMode.Gray16) return "gray";
            return "colour";
        }

        static string SourceToken(PaperSource s)
        {
            if (s == PaperSource.Feeder) return "adf";
            if (s == PaperSource.FeederDuplex) return "adfduplex";
            return "flatbed";
        }

        /// <summary>
        /// Hands every page of a multi-page scan to Photoshop, one document each.
        /// These pages are already cropped and toned, so nothing here re-applies
        /// the canvas selection - that selection describes the glass, not any one
        /// of the items cut out of it.
        /// </summary>
        void SendScanPagesToPhotoshop(List<RawImage> pages)
        {
            SendScanPagesToPhotoshop(pages, 0);
        }

        /// <summary>
        /// Hands each page to Photoshop as its own document.
        ///
        /// <paramref name="sourceDpi"/> is reported when it is known, because a
        /// handoff taken from a preview carries the preview's resolution, and a
        /// 100 dpi card arriving in Photoshop should not be a surprise.
        /// </summary>
        void SendScanPagesToPhotoshop(List<RawImage> pages, double sourceDpi)
        {
            int sent = 0;
            string last = null;

            foreach (RawImage page in pages)
            {
                if (page == null || !page.IsValid) continue;
                try
                {
                    using (Bitmap full = page.ToBitmap())
                    {
                        if (full == null) continue;
                        Bitmap toned = ProcessBitmap(full) ?? (Bitmap)full.Clone();
                        string path = HandOff(toned, sent == pages.Count - 1);
                        toned.Dispose();
                        if (path != null) { sent++; last = path; }
                    }
                }
                catch (Exception ex) { Log("could not hand page to Photoshop: " + ex.Message); }
            }

            string at = (sourceDpi > 1 && !double.IsNaN(sourceDpi) && !double.IsInfinity(sourceDpi))
                ? " at " + sourceDpi.ToString("0", CultureInfo.InvariantCulture) + " dpi"
                : "";

            PinStatus(sent > 1
                ? "Sent " + sent + " separate documents to Photoshop" + at + "."
                : (sent == 1 ? "Handed to Photoshop:  " + Path.GetFileName(last) + at
                             : "Nothing could be handed to Photoshop."));
        }

        /// <summary>
        /// Writes one page where Photoshop will find it and asks Photoshop to
        /// open it. Returns the path written, or null.
        /// </summary>
        string HandOff(Bitmap outBmp, bool dispatch)
        {
            if (outBmp == null) return null;
            try
            {
                Directory.CreateDirectory(_settings.OutputDirectory);
                string tmpDir = @"C:\PS_Fix\tmp";
                if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);

                string path = Path.Combine(_settings.OutputDirectory,
                    "ps_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + ".png");
                outBmp.Save(path, ImageFormat.Png);

                File.WriteAllText(Path.Combine(Path.GetTempPath(), "nextscan_handoff.txt"), path, Encoding.UTF8);
                File.WriteAllText(Path.Combine(tmpDir, "scan_output.txt"), path, Encoding.UTF8);

                DispatchToPhotoshop(path);
                return path;
            }
            catch (Exception ex)
            {
                Log("handoff failed: " + ex.Message);
                return null;
            }
        }

        void SendToPhotoshop()
        {
            // The blank platen sheet is a crop target, not a document. Sending it
            // would hand Photoshop a sheet of white paper.
            if (_canvas != null && _canvas.IsPlaceholder && (_film == null || _film.Count == 0))
            {
                SetStatus("Nothing has been scanned yet. Press F7 to scan the selected area.");
                return;
            }

            // Outlined items go across as separate documents.
            //
            // What the operator can see on the preview is what they expect to
            // receive: five outlines, five documents. Without this the button
            // sent one page containing all five, and the separating that auto
            // crop had already worked out was thrown away at the last step.
            //
            // Nothing is detected here. This cuts what the preview already found
            // and showed, exactly as a scan does.
            if (_settings.AutoCrop && _cropPlan.Count > 0 && _canvas != null &&
                !_canvas.IsPlaceholder && !_showingSheet &&
                _canvas.Image != null && _canvas.Image.IsValid)
            {
                RectangleF covers;
                RawImage shown = PageToCutFrom(out covers);
                List<RawImage> pieces = ApplyAutoCrop(shown, covers);

                // ApplyAutoCrop hands the page straight back when it could not
                // use the plan; only a real cut is worth taking this path for.
                if (pieces.Count > 0 && !ReferenceEquals(pieces[0], shown))
                {
                    SendScanPagesToPhotoshop(pieces, shown.XDpi);
                    return;
                }
            }

            // When the canvas is showing the platen, the scan itself is in the
            // filmstrip at full resolution. Cropping the display-resolution bed
            // view would silently downscale what Photoshop receives.
            Bitmap baseBmp = null;
            bool alreadyCropped = false;

            if (_canvas.IsBedView && _film != null && _film.SelectedImage != null)
            {
                Bitmap full = _film.SelectedImage.ToBitmap();
                if (full != null)
                {
                    Bitmap toned = ProcessBitmap(full);
                    if (toned != null) { full.Dispose(); baseBmp = toned; }
                    else baseBmp = full;
                    alreadyCropped = true;
                }
            }

            if (baseBmp == null) baseBmp = _processedCache ?? _canvas.DisplayBitmap;
            if (baseBmp == null && _canvas.Image != null) baseBmp = _canvas.Image.ToBitmap();
            if (baseBmp == null && _film.SelectedImage != null) baseBmp = _film.SelectedImage.ToBitmap();
            if (baseBmp == null) { SetStatus("There is no page to send."); return; }

            try
            {
                Directory.CreateDirectory(_settings.OutputDirectory);
                string tmpDir = @"C:\PS_Fix\tmp";
                if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);

                // If user has a crop selected, apply crop before sending
                RectangleF cn = alreadyCropped ? new RectangleF(0f, 0f, 1f, 1f) : _canvas.CropNorm;
                Bitmap outBmp;
                if (cn.Width < 0.98f || cn.Height < 0.98f)
                {
                    int cx = Math.Max(0, (int)Math.Round(cn.X * baseBmp.Width));
                    int cy = Math.Max(0, (int)Math.Round(cn.Y * baseBmp.Height));
                    int cw = Math.Min(baseBmp.Width - cx, (int)Math.Round(cn.Width * baseBmp.Width));
                    int ch = Math.Min(baseBmp.Height - cy, (int)Math.Round(cn.Height * baseBmp.Height));
                    if (cw > 10 && ch > 10)
                    {
                        outBmp = new Bitmap(cw, ch, PixelFormat.Format24bppRgb);
                        outBmp.SetResolution(baseBmp.HorizontalResolution, baseBmp.VerticalResolution);
                        using (Graphics g = Graphics.FromImage(outBmp))
                        {
                            g.DrawImage(baseBmp, new Rectangle(0, 0, cw, ch), new Rectangle(cx, cy, cw, ch), GraphicsUnit.Pixel);
                        }
                    }
                    else
                    {
                        outBmp = (Bitmap)baseBmp.Clone();
                    }
                }
                else
                {
                    outBmp = (Bitmap)baseBmp.Clone();
                }

                string path = Path.Combine(_settings.OutputDirectory,
                    "ps_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fffffff", CultureInfo.InvariantCulture) + ".png");

                outBmp.Save(path, ImageFormat.Png);
                outBmp.Dispose();

                // 1. Write flags for Photoshop ExtendScript (.jsx) if waiting
                string flag = Path.Combine(Path.GetTempPath(), "nextscan_handoff.txt");
                File.WriteAllText(flag, path, Encoding.UTF8);

                string outList = Path.Combine(tmpDir, "scan_output.txt");
                File.WriteAllText(outList, path, Encoding.UTF8);

                // 2. Dispatch directly to Photoshop via COM or launch process
                bool dispatched = DispatchToPhotoshop(path);

                SetStatus("Handed to Photoshop:  " + Path.GetFileName(path) + (dispatched ? " (Opened in Photoshop)" : ""));
            }
            catch (Exception ex)
            {
                SetStatus("Photoshop handoff failed: " + ex.Message);
            }
        }

        bool DispatchToPhotoshop(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            bool opened = false;
            // 1. Try COM Automation if Photoshop is running
            try
            {
                object app = Marshal.GetActiveObject("Photoshop.Application");
                if (app != null)
                {
                    string js = "try{app.displayDialogs=DialogModes.NO;}catch(e){}" +
                                "app.open(new File(\"" + path.Replace("\\", "\\\\") + "\"));" +
                                "try{app.displayDialogs=DialogModes.ERROR;}catch(e){}";
                    app.GetType().InvokeMember("DoJavaScript", BindingFlags.InvokeMethod,
                                               null, app, new object[] { js });
                    opened = true;
                }
            }
            catch { }

            // 2. If COM failed or Photoshop is not active, try launching Photoshop.exe directly
            if (!opened)
            {
                string psExe = FindPhotoshopExe();
                if (!string.IsNullOrEmpty(psExe) && File.Exists(psExe))
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(psExe, "\"" + path + "\"");
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                        opened = true;
                    }
                    catch { }
                }
            }

            // 3. Bring Photoshop window to front
            BringPhotoshopToFront();
            return opened;
        }

        static string FindPhotoshopExe()
        {
            string[] common = {
                @"C:\Program Files\Adobe\Adobe Photoshop 2026\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2025\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2024\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2023\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop (Beta)\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop CC 2019\Photoshop.exe"
            };
            foreach (string p in common) if (File.Exists(p)) return p;

            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("");
                        if (val != null && File.Exists(val.ToString())) return val.ToString();
                    }
                }
            }
            catch { }
            return "";
        }

        static void BringPhotoshopToFront()
        {
            try
            {
                Process[] psList = Process.GetProcessesByName("Photoshop");
                if (psList.Length > 0 && psList[0].MainWindowHandle != IntPtr.Zero)
                {
                    IntPtr handle = psList[0].MainWindowHandle;
                    if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
                    SetForegroundWindow(handle);
                }
            }
            catch { }
        }

        /// <summary>
        /// Renders through the tone engine. The pixel work lives in Core so it
        /// can be tested with numbers rather than judged from a screenshot -
        /// which is how the old paper whitening shipped with a hue shift and a
        /// contour line in it for as long as it did.
        /// </summary>
        /// <summary>
        /// Both threshold controls only mean anything for a two-tone rendering,
        /// and the manual slider only when the per-area threshold is off. They
        /// are greyed rather than hidden so the panel does not jump about as the
        /// preset changes.
        /// </summary>
        void UpdateCropTogglesEnabled()
        {
            if (_tgMultiCrop != null) _tgMultiCrop.Enabled = _settings.AutoCrop;
        }

        void UpdateThresholdEnabled()
        {
            bool bilevel = _settings.Tone == TonePreset.BlackAndWhiteText ||
                           _settings.Mode == ColorMode.BlackWhite1;

            if (_tgAdaptive != null) _tgAdaptive.Enabled = bilevel;
            if (_slBwThreshold != null)
                _slBwThreshold.Enabled = bilevel && !_settings.AdaptiveThreshold;
        }

        /// <summary>
        /// The profile to attach to a delivered page, and where it came from.
        ///
        /// Three answers in order of authority, because they are three different
        /// strengths of claim:
        ///
        ///   what the device said   it measured itself, or its maker did
        ///   what the operator set  they know something we do not, such as a
        ///                          profile made from an IT8 target on this glass
        ///   sRGB                   an assumption, and said to be one
        ///
        /// The device is asked first and almost always says nothing. Most WIA
        /// drivers name no profile, TWAIN has no way to hand one over at all,
        /// and eSCL is sRGB by specification. That is precisely why the middle
        /// answer exists: without it the first would be decorative.
        ///
        /// Whatever is chosen is assigned and never converted. Saying what the
        /// numbers mean is a different act from changing them, and only the
        /// first is ours to do.
        /// </summary>
        byte[] ColourProfileInUse(out string where)
        {
            string named = _broker != null ? _broker.LastColorProfile : null;
            if (!string.IsNullOrEmpty(named))
            {
                byte[] fromDevice = IccProfile.Load(named);
                if (fromDevice != null)
                {
                    where = "colour profile from the scanner: " + named;
                    return fromDevice;
                }
                Log("the scanner named a profile we could not read: " + named);
            }

            if (!string.IsNullOrEmpty(_settings.ColorProfilePath))
            {
                byte[] chosen = IccProfile.Load(_settings.ColorProfilePath);
                if (chosen != null)
                {
                    where = "colour profile chosen for this scanner: " +
                            Path.GetFileName(_settings.ColorProfilePath);
                    return chosen;
                }
                Log("the chosen colour profile could not be read: " + _settings.ColorProfilePath);
            }

            byte[] srgb = IccProfile.Srgb();
            where = srgb == null
                ? "no sRGB profile on this machine; pages go untagged"
                : "no profile from the scanner and none chosen, so pages are tagged sRGB";
            return srgb;
        }

        ToneSettings CurrentTone()
        {
            return new ToneSettings
            {
                Preset = _settings.Tone,
                Polish = _settings.Polish,
                Brightness = _settings.Brightness,
                Contrast = _settings.Contrast,
                BwThreshold = _settings.BwThreshold,
                AdaptiveThreshold = _settings.AdaptiveThreshold,
                Auto = _settings.AutoTone,
                Saturation = _settings.Saturation,
                Vibrance = _settings.Vibrance,
                Temperature = _settings.Temperature,
                Tint = _settings.Tint,
                Highlights = _settings.Highlights,
                Shadows = _settings.Shadows,
                DescreenLpi = _settings.DescreenLpi,
                Sharpen = _settings.Sharpen,
                BackgroundClean = _settings.BackgroundClean,
                Despeckle = _settings.Despeckle
            };
        }

        /// <summary>
        /// After a scan that produced several items, shows them all at once.
        ///
        /// A scan that cut five things off the glass used to put one of them on
        /// screen and the other four in a list, so the only way to find out
        /// whether the scan worked was to click through them. The question is
        /// whether all five came out right, and that is one question, so it gets
        /// one picture -- with the single-page view one click away.
        ///
        /// Only after a scan. A preview is the bed as it is, which is the thing
        /// the operator is about to decide about; tiling it would be tiling one
        /// picture.
        /// </summary>
        void OfferSheet(List<RawImage> pages)
        {
            DropSheet();
            if (pages == null || pages.Count < 2) { if (_tbSheet != null) _tbSheet.Visible = false; return; }

            try { _sheet = ContactSheet.Compose(pages, 1800); }
            catch (Exception ex) { Log("contact sheet failed: " + ex.Message); _sheet = null; }

            if (_tbSheet != null)
            {
                _tbSheet.Visible = _sheet != null;
                _tbSheet.Checked = false;
            }
            if (_sheet != null) ShowSheet(true);
        }

        void ShowSheet(bool on)
        {
            if (_canvas == null) return;

            if (on)
            {
                if (_sheet == null) return;
                if (!_showingSheet) _beforeSheet = _canvas.Image;
                _showingSheet = true;
                _canvas.SelectedRegion = -1;
                _canvas.DetectedRegions = null;
                _canvas.SetImage(_sheet);
                _canvas.ZoomFit();
                if (_tbSheet != null) _tbSheet.Checked = true;
                SetStatus("Every item from the last scan. Click the button again for one page at a time.");
            }
            else
            {
                _showingSheet = false;
                if (_beforeSheet != null && _beforeSheet.IsValid) _canvas.SetImage(_beforeSheet);
                else if (_film != null && _film.SelectedImage != null) _canvas.SetImage(_film.SelectedImage);
                _canvas.ZoomFit();
                if (_tbSheet != null) _tbSheet.Checked = false;
            }
            UpdateStatus();
        }

        void DropSheet()
        {
            _showingSheet = false;
            _sheet = null;
            _beforeSheet = null;
        }

        // =====================================================================
        // Re-rendering the preview while a slider is moving
        //
        // Every adjustment asks for the preview again, and the clean-up pass put
        // real work behind that ask: measured on a 1275 x 608 preview of a page,
        // the ID photo preset costs 21 ms but Clean document costs 136. Done on
        // the UI thread, on every tick of a slider, that is a slider that jumps
        // instead of slides.
        //
        // So a render happens on a worker, and the worker coalesces: while it is
        // busy, further changes only set a flag, and it renders once more at the
        // end with whatever the settings became. Dragging through fifty values
        // therefore costs a handful of renders rather than fifty, and the window
        // never stops responding to the drag that is causing them.
        // =====================================================================

        readonly object _previewLock = new object();
        Bitmap _previewSource;          // our own copy; only the worker reads it
        Bitmap _previewSourceOf;        // which original it was taken from
        ToneSettings _previewTone;
        ColorMode _previewMode;
        bool _previewDirty;
        bool _previewRendering;

        void UpdateProcessedPreview()
        {
            if (_canvas != null && _canvas.IsPlaceholder) return;
            if (_canvas == null) return;

            Bitmap src = _canvas.OriginalBitmap;
            if (src == null) return;

            lock (_previewLock)
            {
                if (!ReferenceEquals(_previewSourceOf, src))
                {
                    // A copy of our own, because the worker will lock its bits
                    // while the canvas may be painting the original. The old one
                    // is dropped rather than disposed: a render already running
                    // still holds it, and a few megabytes collected later is a
                    // far smaller problem than a bitmap disposed underneath a
                    // thread that is reading it.
                    _previewSource = (Bitmap)src.Clone();
                    _previewSourceOf = src;
                }

                _previewTone = CurrentTone();
                _previewMode = _settings.Mode;
                _previewDirty = true;

                if (_previewRendering) return;      // it will pick this up itself
                _previewRendering = true;
            }

            Thread worker = new Thread(RenderPreviewLoop);
            worker.IsBackground = true;
            worker.Start();
        }

        void RenderPreviewLoop()
        {
            try
            {
                for (; ; )
                {
                    Bitmap source;
                    ToneSettings tone;
                    ColorMode mode;

                    lock (_previewLock)
                    {
                        if (!_previewDirty) { _previewRendering = false; return; }
                        _previewDirty = false;
                        source = _previewSource;
                        tone = _previewTone;
                        mode = _previewMode;
                    }

                    if (source == null) continue;

                    Bitmap made;
                    try { made = ToneEngine.Apply(source, tone, mode); }
                    catch { made = null; }
                    if (made == null) continue;

                    Bitmap ready = made;
                    try { BeginInvoke((MethodInvoker)delegate { ShowProcessed(ready); }); }
                    catch { ready.Dispose(); return; }   // the window went away mid-render
                }
            }
            finally
            {
                lock (_previewLock) { _previewRendering = false; }
            }
        }

        void ShowProcessed(Bitmap processed)
        {
            if (processed == null) return;
            if (IsDisposed) { processed.Dispose(); return; }

            if (_processedCache != null && !ReferenceEquals(_processedCache, processed))
                _processedCache.Dispose();
            _processedCache = processed;
            _canvas.SetAdjusted(processed);
        }

        /// <summary>
        /// Applies the current tone settings to any bitmap.
        ///
        /// Split out of UpdateProcessedPreview so the Photoshop handoff can run the
        /// same adjustments over the FULL RESOLUTION page: the canvas may be showing
        /// a display-resolution view of the whole platen, and cropping that for
        /// export would hand Photoshop a downscaled image.
        /// </summary>
        Bitmap ProcessBitmap(Bitmap src)
        {
            if (src == null) return null;
            return ToneEngine.Apply(src, CurrentTone(), _settings.Mode);
        }

        // =====================================================================
        // Keyboard
        // =====================================================================
        /// <summary>
        /// One shortcut, both as something that happens and as something that
        /// can be read.
        /// </summary>
        class Shortcut
        {
            public Keys Key;
            public Keys Same = Keys.None;   // a second key meaning the same thing
            public bool Ctrl;
            public string Group = "";
            public string Does = "";
            public string Keys_ = "";
            public Action Do;               // null: handled elsewhere, listed here
        }

        /// <summary>
        /// Every shortcut, once.
        ///
        /// The list the operator reads and the list the keyboard obeys are the
        /// same list. Written twice they disagree the first time one is changed,
        /// and a keyboard reference that lies is worse than none: it is the one
        /// thing a reader has no way to check.
        ///
        /// Entries with no action are the ones whose handling cannot be reduced
        /// to a key and a method -- Escape means different things depending on
        /// what is open, panning is a key held rather than pressed -- so they
        /// are listed here and handled below. They are in the table because a
        /// reader does not care which of those two a shortcut is.
        /// </summary>
        List<Shortcut> Shortcuts()
        {
            return new List<Shortcut>
            {
                new Shortcut { Group = "Scanning", Does = "Preview", Keys_ = "F5",
                               Key = Keys.F5, Do = delegate { StartScan(true); } },
                new Shortcut { Group = "Scanning", Does = "Scan", Keys_ = "F7",
                               Key = Keys.F7, Do = delegate { StartScan(false); } },
                new Shortcut { Group = "Scanning", Does = "Scan", Keys_ = "Ctrl+Enter",
                               Key = Keys.Enter, Ctrl = true, Do = delegate { StartScan(false); } },
                new Shortcut { Group = "Scanning", Does = "Stop a scan", Keys_ = "Esc" },

                new Shortcut { Group = "Sending", Does = "Send to Photoshop", Keys_ = "Ctrl+P",
                               Key = Keys.P, Ctrl = true, Do = delegate { SendToPhotoshop(); } },
                new Shortcut { Group = "Sending", Does = "Save the session", Keys_ = "Ctrl+S",
                               Key = Keys.S, Ctrl = true, Do = delegate { SaveSession(); } },

                new Shortcut { Group = "View", Does = "Fit in the window", Keys_ = "Ctrl+0",
                               Key = Keys.D0, Same = Keys.NumPad0, Ctrl = true,
                               Do = delegate { _canvas.ZoomFit(); } },
                new Shortcut { Group = "View", Does = "Actual size", Keys_ = "Ctrl+1",
                               Key = Keys.D1, Same = Keys.NumPad1, Ctrl = true,
                               Do = delegate { _canvas.ZoomTo(1.0); } },
                new Shortcut { Group = "View", Does = "Zoom in", Keys_ = "Ctrl+plus",
                               Key = Keys.Oemplus, Same = Keys.Add, Ctrl = true,
                               Do = delegate { _canvas.ZoomBy(1.25); } },
                new Shortcut { Group = "View", Does = "Zoom out", Keys_ = "Ctrl+minus",
                               Key = Keys.OemMinus, Same = Keys.Subtract, Ctrl = true,
                               Do = delegate { _canvas.ZoomBy(1 / 1.25); } },
                new Shortcut { Group = "View", Does = "Zoom", Keys_ = "Ctrl+wheel" },
                new Shortcut { Group = "View", Does = "Pan the page", Keys_ = "hold Space" },

                new Shortcut { Group = "Panels", Does = "Capture, Look, Batch, Pages, Assist", Keys_ = "Alt+1 to 5" },
                new Shortcut { Group = "Panels", Does = "Settings", Keys_ = "Ctrl+comma" },
            };
        }

        void OnShellKeyDown(object sender, KeyEventArgs e)
        {
            // Escape belongs to whatever is in front. With the settings page
            // open that is the settings page, not the scan that is not running.
            if (_settingsOpen && e.KeyCode == Keys.Escape)
            {
                OpenSettings(false);
                e.Handled = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.Oemcomma)
            {
                OpenSettings(!_settingsOpen);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Alt+1..6 walks the rail. Ctrl+0 and Ctrl+1 were already zoom, and
            // moving those would break the habit of everyone who has an image
            // editor open beside this one.
            if (e.Alt && e.KeyCode >= Keys.D1 && e.KeyCode <= Keys.D6)
            {
                ShowSection(e.KeyCode - Keys.D1);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Escape is not in the lookup: what it stops depends on what is
            // running, and the settings page above has already had its say.
            if (e.KeyCode == Keys.Escape) { RequestCancel(); e.Handled = true; UpdateStatus(); return; }

            foreach (Shortcut shortcut in Shortcuts())
            {
                if (shortcut.Do == null) continue;
                if (e.Control != shortcut.Ctrl) continue;
                if (e.KeyCode != shortcut.Key && (shortcut.Same == Keys.None || e.KeyCode != shortcut.Same))
                    continue;

                shortcut.Do();
                e.Handled = true;

                // Suppressed as well as handled, or the keystroke goes on to
                // whatever has focus: zooming with the cursor in a field used to
                // type a digit into it as well.
                e.SuppressKeyPress = true;
                break;
            }

            UpdateStatus();
        }

        // =====================================================================
        // Helpers
        // =====================================================================
        void SetStatus(string text)
        {
            if (_statusText == null) return;
            _statusText.Text = text ?? "";
        }

        /// <summary>
        /// Shows a message that must survive the next routine status refresh.
        ///
        /// OnScanDone ends with UpdateStatus(), which rewrites the bar with the
        /// page geometry. Every message set while finishing a scan was therefore
        /// replaced before it could be read - which is why the auto crop
        /// diagnostics added for exactly this problem were themselves invisible.
        /// </summary>
        void PinStatus(string text)
        {
            _statusPinned = true;
            SetStatus(text);
        }

        bool _statusPinned;

        void UpdateStatus()
        {
            // Before any early return: a collapsed group still states its value,
            // so anything that changes a setting has to refresh those too.
            UpdateGroupSummaries();
            UpdatePlan();

            if (_canvas != null && _zoomText != null)
            {
                _zoomText.Text = _canvas.Image == null ? ""
                    : (_canvas.EffectiveZoom * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
            }

            if (_statusPinned) { _statusPinned = false; return; }
            if (_canvas == null || _canvas.Image == null) return;
            RawImage img = _canvas.Image;

            // Reporting the sheet's pixel count would read as "something has been
            // scanned"; describe the platen instead.
            if (_canvas.IsPlaceholder)
            {
                SetStatus(string.Format(CultureInfo.InvariantCulture,
                    "Scanner glass {0:0.00} × {1:0.00} in   ·   drag to select an area, then Scan",
                    _activeBedW, _activeBedH));
                return;
            }

            // The same correction the badge needs, for the same reason: when a
            // preview of a selection is composited onto a picture of the bed,
            // the displayed image is BedViewDpi and describing it would say 150
            // about a 300 dpi scan.
            double dpi = img.XDpi;
            int px = img.Width, py = img.Height;
            if (_canvas.CaptureDpi > 1 && img.XDpi > 1 && img.YDpi > 1 &&
                Math.Abs(_canvas.CaptureDpi - img.XDpi) > 0.01)
            {
                dpi = _canvas.CaptureDpi;
                px = (int)Math.Round(img.Width / img.XDpi * dpi);
                py = (int)Math.Round(img.Height / img.YDpi * dpi);
            }

            SetStatus(px + " × " + py + " px   ·   " +
                      dpi.ToString("0", CultureInfo.InvariantCulture) + " dpi   ·   " +
                      _film.Count + " page" + (_film.Count == 1 ? "" : "s") + " in session");
        }

        static void Log(string m)
        {
            try { StudioExport.Log(m); } catch { }
        }

        static int SourceToIndex(PaperSource s)
        {
            switch (s)
            {
                case PaperSource.Feeder: return 1;
                case PaperSource.FeederDuplex: return 2;
                case PaperSource.Film: return 3;
                default: return 0;
            }
        }

        static PaperSource IndexToSource(int i)
        {
            switch (i)
            {
                case 1: return PaperSource.Feeder;
                case 2: return PaperSource.FeederDuplex;
                case 3: return PaperSource.Film;
                default: return PaperSource.Flatbed;
            }
        }

        static int ModeToIndex(ColorMode m)
        {
            switch (m)
            {
                case ColorMode.Color48: return 1;
                case ColorMode.Gray8: return 2;
                case ColorMode.Gray16: return 3;
                case ColorMode.BlackWhite1: return 4;
                default: return 0;
            }
        }

        static ColorMode IndexToMode(int i)
        {
            switch (i)
            {
                case 1: return ColorMode.Color48;
                case 2: return ColorMode.Gray8;
                case 3: return ColorMode.Gray16;
                case 4: return ColorMode.BlackWhite1;
                default: return ColorMode.Color24;
            }
        }

        // ---- borderless window plumbing --------------------------------------
        const int WM_NCHITTEST = 0x0084;
        const int WM_NCLBUTTONDOWN = 0x00A1;
        const int HTCAPTION = 2;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                  HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        const int GripSize = 6;

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            // A borderless form loses the system resize frame, so the edges are
            // re-declared here. Without this the window can only be resized by
            // maximising, which users read as a bug.
            if (m.Msg != WM_NCHITTEST || WindowState == FormWindowState.Maximized) return;

            Point p = PointToClient(new Point(m.LParam.ToInt32()));
            bool left = p.X <= GripSize;
            bool right = p.X >= ClientSize.Width - GripSize;
            bool top = p.Y <= GripSize;
            bool bottom = p.Y >= ClientSize.Height - GripSize;

            if (top && left) m.Result = (IntPtr)HTTOPLEFT;
            else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
            else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
            else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
            else if (left) m.Result = (IntPtr)HTLEFT;
            else if (right) m.Result = (IntPtr)HTRIGHT;
            else if (top) m.Result = (IntPtr)HTTOP;
            else if (bottom) m.Result = (IntPtr)HTBOTTOM;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // A background thread holding files open would keep the process
            // alive after the window has gone.
            StopHotFolder();

            // Unsaved pages stay on disk rather than being cleaned up. Closing
            // the window is not a decision to throw away a scanned batch.
            KeepJournalForRecovery();

            try { _settings.Save(); } catch { }
            try
            {
                // Only the window size is remembered automatically; panel sizes and
                // floating positions persist when the user explicitly saves them,
                // so an accidental drag does not silently become the default.
                if (_layout.RememberWindowSize && WindowState == FormWindowState.Normal)
                {
                    StudioLayout onDisk = StudioLayout.Load();
                    onDisk.WindowWidth = Width;
                    onDisk.WindowHeight = Height;
                    onDisk.Save();
                }
            }
            catch { }
            base.OnFormClosing(e);
        }
    }
}
