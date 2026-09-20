// =============================================================================
// NextScan Studio - Main Studio Window
// Plan ref: MASTER_PLAN section 13.1, 13.4.
//
// Integrates the 4-zone layout (Command Toolbar, Interactive Canvas, Inspector,
// Filmstrip, Status Bar) with DeviceBroker, Imaging, Curves, and Photoshop Bridge.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioWindow : Form
    {
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        DeviceBroker _broker;
        StudioSettings _settings;
        List<ScannerEntry> _scanners = new List<ScannerEntry>();

        // Layout components
        Panel _topToolbar;
        StudioCanvas _canvas;
        StudioInspector _inspector;
        StudioFilmstrip _filmstrip;
        Panel _filmstripContainer;
        StatusStrip _statusBar;
        ToolStripStatusLabel _lblStatus;
        ToolStripStatusLabel _lblDimensions;
        ToolStripStatusLabel _lblDeviceBadge;

        Button _bPreview;
        Button _bScan;
        Button _bCancel;
        Button _bSendPs;
        Button _bExport;
        CheckBox _chkSplitView;

        CancellationTokenSource _activeScanCts;
        bool _isAcquiring = false;

        public StudioWindow(StudioSettings settings)
        {
            _settings = settings ?? StudioSettings.Load();
            _broker = new DeviceBroker();
            _broker.Log = OnBrokerLog;

            InitForm();
            BuildUi();
            BindEvents();
        }

        void InitForm()
        {
            Text = "NextScan Studio";
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14, 17, 24);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;
        }

        void BuildUi()
        {
            SuspendLayout();

            // 1. Top Command Toolbar
            _topToolbar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Color.FromArgb(20, 24, 32) };
            BuildTopToolbar(_topToolbar);
            Controls.Add(_topToolbar);

            // 2. Status Bar
            _statusBar = new StatusStrip { BackColor = Color.FromArgb(18, 22, 30), ForeColor = Color.FromArgb(160, 175, 195) };
            _lblStatus = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _lblDimensions = new ToolStripStatusLabel("0 x 0 px") { BorderSides = ToolStripStatusLabelBorderSides.Left };
            _lblDeviceBadge = new ToolStripStatusLabel("No Scanner Selected") { BorderSides = ToolStripStatusLabelBorderSides.Left };
            _statusBar.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblDimensions, _lblDeviceBadge });
            Controls.Add(_statusBar);

            // 3. Bottom Filmstrip Container
            _filmstripContainer = new Panel { Dock = DockStyle.Bottom, Height = 148, BackColor = Color.FromArgb(20, 24, 32) };
            BuildFilmstripBar(_filmstripContainer);
            Controls.Add(_filmstripContainer);

            // 4. Right Inspector Sidebar
            _inspector = new StudioInspector();
            Controls.Add(_inspector);

            // 5. Center Interactive Canvas
            _canvas = new StudioCanvas { Dock = DockStyle.Fill };
            Controls.Add(_canvas);

            ResumeLayout();
        }

        void BuildTopToolbar(Panel p)
        {
            // Brand title
            Label brand = new Label
            {
                Text = "NextScan Studio",
                Font = new Font("Segoe UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 195, 255),
                Location = new Point(16, 14),
                AutoSize = true
            };
            p.Controls.Add(brand);

            int x = 200;

            // Preview button (F5)
            _bPreview = CreateToolbarButton("Preview (F5)", x, 11, 110, Color.FromArgb(40, 50, 68), Color.White);
            _bPreview.Click += delegate { DoPreviewScan(); };
            p.Controls.Add(_bPreview);
            x += 118;

            // Final Scan button (F7)
            _bScan = CreateToolbarButton("Scan (F7)", x, 11, 110, Color.FromArgb(20, 115, 230), Color.White);
            _bScan.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _bScan.Click += delegate { DoFinalScan(); };
            p.Controls.Add(_bScan);
            x += 118;

            // Cancel button (Esc)
            _bCancel = CreateToolbarButton("Cancel", x, 11, 80, Color.FromArgb(60, 30, 35), Color.FromArgb(255, 160, 160));
            _bCancel.Enabled = false;
            _bCancel.Click += delegate { CancelActiveScan(); };
            p.Controls.Add(_bCancel);
            x += 96;

            // Split View toggle
            _chkSplitView = new CheckBox
            {
                Text = "Before/After Split",
                Location = new Point(x, 18),
                AutoSize = true,
                ForeColor = Color.FromArgb(200, 210, 230),
                BackColor = Color.Transparent
            };
            _chkSplitView.CheckedChanged += delegate { _canvas.SplitView = _chkSplitView.Checked; };
            p.Controls.Add(_chkSplitView);
            x += 140;

            // Reset Crop button
            Button bResetCrop = CreateToolbarButton("Full Bed", x, 14, 76, Color.FromArgb(32, 38, 50), Color.White);
            bResetCrop.Click += delegate { _canvas.CropNorm = new RectangleF(0f, 0f, 1f, 1f); };
            p.Controls.Add(bResetCrop);
            x += 84;

            // Auto-Detect Crop button
            Button bAutoCrop = CreateToolbarButton("Auto Crop", x, 14, 82, Color.FromArgb(32, 38, 50), Color.White);
            bAutoCrop.Click += delegate { TriggerAutoCrop(); };
            p.Controls.Add(bAutoCrop);
            x += 92;

            // Send to Photoshop button (Ctrl+P)
            _bSendPs = CreateToolbarButton("Photoshop (Ctrl+P)", Width - 240, 11, 130, Color.FromArgb(30, 65, 110), Color.FromArgb(180, 220, 255));
            _bSendPs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _bSendPs.Click += delegate { DoPhotoshopHandoff(); };
            p.Controls.Add(_bSendPs);

            // Export button (Ctrl+S)
            _bExport = CreateToolbarButton("Export (Ctrl+S)", Width - 100, 11, 86, Color.FromArgb(32, 44, 60), Color.White);
            _bExport.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _bExport.Click += delegate { DoExport(); };
            p.Controls.Add(_bExport);
        }

        void BuildFilmstripBar(Panel p)
        {
            // Toolbar for filmstrip
            Panel bar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(24, 29, 39) };
            Label lTitle = new Label
            {
                Text = "PAGES IN SESSION",
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(140, 155, 175),
                Location = new Point(12, 6),
                AutoSize = true
            };
            bar.Controls.Add(lTitle);

            Button bRotLeft = CreateSmallToolBtn("\u21ba 90\u00b0 Left", 130, 2, 70);
            bRotLeft.Click += delegate { _filmstrip.RotateSelected(-90); };
            bar.Controls.Add(bRotLeft);

            Button bRotRight = CreateSmallToolBtn("\u21bb 90\u00b0 Right", 205, 2, 75);
            bRotRight.Click += delegate { _filmstrip.RotateSelected(90); };
            bar.Controls.Add(bRotRight);

            Button bDel = CreateSmallToolBtn("\ud83d\uddd1 Delete", 285, 2, 65);
            bDel.Click += delegate { _filmstrip.DeleteSelected(); };
            bar.Controls.Add(bDel);

            Button bClear = CreateSmallToolBtn("Clear All", 355, 2, 65);
            bClear.Click += delegate { _filmstrip.ClearAll(); };
            bar.Controls.Add(bClear);

            p.Controls.Add(bar);

            // Filmstrip carousel
            _filmstrip = new StudioFilmstrip { Dock = DockStyle.Fill };
            _filmstrip.SelectedPageChanged += delegate
            {
                StudioPageItem page = _filmstrip.SelectedPage;
                if (page != null && page.Raw != null)
                {
                    _canvas.Image = page.Raw.ToBitmap();
                    UpdateProcessedPreview();
                    UpdateDimensionsLabel();
                }
            };
            p.Controls.Add(_filmstrip);
        }

        void BindEvents()
        {
            Load += delegate
            {
                RefreshScanners();
                _inspector.ApplySettings(_settings);
                _canvas.CropNorm = _settings.CropNorm;
            };

            FormClosing += delegate
            {
                _inspector.CollectSettings(_settings);
                _settings.CropNorm = _canvas.CropNorm;
                _settings.Save();
            };

            _inspector.SettingsChanged += delegate
            {
                _inspector.CollectSettings(_settings);
                UpdateProcessedPreview();
            };

            _inspector.CurveEditor.CurveChanged += delegate
            {
                UpdateProcessedPreview();
            };

            _inspector.RequestRefreshDevices += delegate { RefreshScanners(); };
            _inspector.RequestUsbReset += delegate { DoUsbReset(); };

            _canvas.CropChanged += delegate
            {
                _settings.CropNorm = _canvas.CropNorm;
                UpdateDimensionsLabel();
            };
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F5:
                case Keys.F6:
                    DoPreviewScan();
                    return true;
                case Keys.F7:
                case (Keys.Control | Keys.Enter):
                    DoFinalScan();
                    return true;
                case Keys.Escape:
                    if (_isAcquiring) { CancelActiveScan(); return true; }
                    break;
                case (Keys.Control | Keys.A):
                    _canvas.CropNorm = new RectangleF(0f, 0f, 1f, 1f);
                    return true;
                case (Keys.Control | Keys.D):
                    TriggerAutoCrop();
                    return true;
                case (Keys.Control | Keys.R):
                    _canvas.ResetZoom();
                    return true;
                case (Keys.Control | Keys.P):
                    DoPhotoshopHandoff();
                    return true;
                case (Keys.Control | Keys.S):
                    DoExport();
                    return true;
                case Keys.OemOpenBrackets: // [
                    if (_filmstrip.SelectedIndex > 0) _filmstrip.SelectedIndex--;
                    return true;
                case Keys.OemCloseBrackets: // ]
                    if (_filmstrip.SelectedIndex < _filmstrip.Pages.Count - 1) _filmstrip.SelectedIndex++;
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void RefreshScanners()
        {
            SetStatusText("Probing for scanners...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _scanners = _broker.Probe();
                    BeginInvoke(new Action(delegate
                    {
                        _inspector.PopulateScanners(_scanners, _settings.DeviceName, _settings.Transport);
                        DeviceDescriptor dev = _inspector.GetSelectedDeviceDescriptor();
                        _lblDeviceBadge.Text = dev != null ? dev.ToString() : "No Scanner Selected";
                        SetStatusText("Scanner list updated (" + _scanners.Count + " found)");
                    }));
                }
                catch (Exception ex)
                {
                    OnBrokerLog("Probe failed: " + ex.Message);
                }
            });
        }

        void DoPreviewScan()
        {
            DeviceDescriptor dev = _inspector.GetSelectedDeviceDescriptor();
            if (dev == null)
            {
                MessageBox.Show(this, "No scanner selected. Please select a scanner first.", "NextScan Studio",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ScanSettings s = new ScanSettings();
            s.Dpi = 100;
            s.Mode = ColorMode.Color24;
            s.Source = PaperSource.Flatbed;
            s.PageCount = 1;
            s.IsPreview = true;

            StartAcquisition(dev, s, true);
        }

        void DoFinalScan()
        {
            DeviceDescriptor dev = _inspector.GetSelectedDeviceDescriptor();
            if (dev == null)
            {
                MessageBox.Show(this, "No scanner selected. Please select a scanner first.", "NextScan Studio",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _inspector.CollectSettings(_settings);

            ScanSettings s = new ScanSettings();
            s.Dpi = _settings.Dpi;
            s.Mode = _settings.Mode;
            s.Source = _settings.Source;
            s.PageCount = _settings.PageCount;
            s.ShowVendorUi = _settings.ShowVendorUi;

            // Calculate region in inches
            // Default bed if unknown: 8.5 x 11.7
            DeviceCapabilities caps;
            double bedW = 8.5, bedH = 11.7;
            if (_broker.GetCapabilities(dev, out caps).Ok && caps.PhysicalWidthIn > 1.0)
            {
                bedW = caps.PhysicalWidthIn;
                bedH = caps.PhysicalHeightIn;
            }

            RectangleF crop = _canvas.CropNorm;
            if (crop.Width < 0.98f || crop.Height < 0.98f)
            {
                s.RegionLeftIn = crop.X * bedW;
                s.RegionTopIn = crop.Y * bedH;
                s.RegionWidthIn = crop.Width * bedW;
                s.RegionHeightIn = crop.Height * bedH;
            }

            StartAcquisition(dev, s, false);
        }

        void StartAcquisition(DeviceDescriptor dev, ScanSettings s, bool isPreview)
        {
            if (_isAcquiring) return;
            _isAcquiring = true;
            _bScan.Enabled = false;
            _bPreview.Enabled = false;
            _bCancel.Enabled = true;

            _canvas.SetScanning(true, 0f);
            SetStatusText((isPreview ? "Previewing " : "Scanning ") + dev.FriendlyName + " (" + s.Dpi + " DPI)...");

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    NsResult r = _broker.Scan(dev, s,
                        delegate (RawImage frame)
                        {
                            BeginInvoke(new Action<RawImage, bool>(OnPageAcquired), frame, isPreview);
                            return true; // continue multi-page
                        },
                        delegate (string msg, int pct)
                        {
                            BeginInvoke(new Action(delegate
                            {
                                SetStatusText(msg);
                                _canvas.SetScanning(true, pct / 100f);
                            }));
                        });

                    BeginInvoke(new Action(delegate
                    {
                        _isAcquiring = false;
                        _bScan.Enabled = true;
                        _bPreview.Enabled = true;
                        _bCancel.Enabled = false;
                        _canvas.SetScanning(false);

                        if (!r.Ok)
                        {
                            SetStatusText("Scan failed: " + r.Message);
                            MessageBox.Show(this, r.Message + (string.IsNullOrEmpty(r.Remedy) ? "" : ("\r\n\r\n" + r.Remedy)),
                                            "NextScan Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                        else
                        {
                            SetStatusText("Acquisition complete.");
                        }
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(delegate
                    {
                        _isAcquiring = false;
                        _bScan.Enabled = true;
                        _bPreview.Enabled = true;
                        _bCancel.Enabled = false;
                        _canvas.SetScanning(false);
                        SetStatusText("Error: " + ex.Message);
                    }));
                }
            });
        }

        void OnPageAcquired(RawImage frame, bool isPreview)
        {
            if (frame == null || !frame.IsValid) return;

            if (isPreview)
            {
                _canvas.Image = frame.ToBitmap();

                // Run Document Detection & Deskew Estimation
                if (_settings.AutoCrop || _settings.AutoDeskew)
                {
                    List<RotatedBox> boxes = DocumentDetector.Detect(frame, DeskewGuard.StrictFlatbedGuard);
                    if (boxes.Count > 0)
                    {
                        _canvas.DetectedBox = boxes[0];

                        // Deskew consensus
                        float[] est = DeskewEstimators.EstimateAll(frame, boxes[0]);
                        DeskewResult dr = DeskewPolicy.Evaluate(est, DeskewEstimators.Resolutions,
                                                               _settings.Source, _settings.DeskewProfile);
                        _canvas.DeskewResult = dr;

                        if (_settings.AutoCrop)
                        {
                            // Convert AABB to normalized coordinates
                            float nx = (float)boxes[0].AABB.X / frame.Width;
                            float ny = (float)boxes[0].AABB.Y / frame.Height;
                            float nw = (float)boxes[0].AABB.Width / frame.Width;
                            float nh = (float)boxes[0].AABB.Height / frame.Height;
                            _canvas.CropNorm = new RectangleF(nx, ny, nw, nh);
                        }
                    }
                }
            }
            else
            {
                // Add final scan to session filmstrip
                _filmstrip.AddPage(frame);
            }

            UpdateProcessedPreview();
            UpdateDimensionsLabel();
        }

        void UpdateProcessedPreview()
        {
            if (_canvas.Image == null) return;

            // Apply active curves / whitening to display preview
            Bitmap baseBmp = _canvas.Image;
            Bitmap adjusted = new Bitmap(baseBmp.Width, baseBmp.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            byte[] lutRgb = _inspector.CurveEditor.BuildLut8(CurveChannel.Rgb);
            byte[] lutR = _inspector.CurveEditor.BuildLut8(CurveChannel.Red);
            byte[] lutG = _inspector.CurveEditor.BuildLut8(CurveChannel.Green);
            byte[] lutB = _inspector.CurveEditor.BuildLut8(CurveChannel.Blue);

            int whiteBoost = _settings.Whitening;

            using (Graphics g = Graphics.FromImage(adjusted))
            {
                g.DrawImage(baseBmp, 0, 0);
            }

            // Quick GDI+ fast byte pass
            System.Drawing.Imaging.BitmapData bd = adjusted.LockBits(
                new Rectangle(0, 0, adjusted.Width, adjusted.Height),
                System.Drawing.Imaging.ImageLockMode.ReadWrite, adjusted.PixelFormat);
            try
            {
                unsafe
                {
                    byte* scan0 = (byte*)bd.Scan0.ToPointer();
                    int stride = bd.Stride;
                    int w = adjusted.Width;
                    int h = adjusted.Height;

                    for (int y = 0; y < h; y++)
                    {
                        byte* row = scan0 + y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int b = row[x * 3 + 0];
                            int gVal = row[x * 3 + 1];
                            int r = row[x * 3 + 2];

                            // Curves
                            b = lutB[lutRgb[b]];
                            gVal = lutG[lutRgb[gVal]];
                            r = lutR[lutRgb[r]];

                            // Whitening: lift high values toward 255
                            if (whiteBoost > 0)
                            {
                                int lum = (r * 299 + gVal * 587 + b * 114) / 1000;
                                if (lum > 180)
                                {
                                    int delta = (255 - lum) * whiteBoost / 100;
                                    r = Math.Min(255, r + delta);
                                    gVal = Math.Min(255, gVal + delta);
                                    b = Math.Min(255, b + delta);
                                }
                            }

                            row[x * 3 + 0] = (byte)b;
                            row[x * 3 + 1] = (byte)gVal;
                            row[x * 3 + 2] = (byte)r;
                        }
                    }
                }
            }
            finally
            {
                adjusted.UnlockBits(bd);
            }

            _canvas.ProcessedImage = adjusted;
        }

        void TriggerAutoCrop()
        {
            if (_canvas.Image == null) return;
            RawImage raw = RawImage.FromBitmap(_canvas.Image);
            List<RotatedBox> boxes = DocumentDetector.Detect(raw, DeskewGuard.StrictFlatbedGuard);
            if (boxes.Count > 0)
            {
                _canvas.DetectedBox = boxes[0];
                float nx = (float)boxes[0].AABB.X / raw.Width;
                float ny = (float)boxes[0].AABB.Y / raw.Height;
                float nw = (float)boxes[0].AABB.Width / raw.Width;
                float nh = (float)boxes[0].AABB.Height / raw.Height;
                _canvas.CropNorm = new RectangleF(nx, ny, nw, nh);
                SetStatusText(string.Format("Detected document: {0}x{1} px, rotation {2:0.1}\u00b0",
                                            boxes[0].AABB.Width, boxes[0].AABB.Height, boxes[0].Angle));
            }
        }

        void CancelActiveScan()
        {
            SetStatusText("Cancelling scan...");
            // DeviceBroker kills the disposable host process automatically
            try { _activeScanCts?.Cancel(); } catch { }
        }

        void DoPhotoshopHandoff()
        {
            List<RawImage> list = new List<RawImage>();
            foreach (StudioPageItem it in _filmstrip.Pages)
            {
                if (it.Raw != null) list.Add(it.Raw);
            }

            if (list.Count == 0 && _canvas.Image != null)
            {
                list.Add(RawImage.FromBitmap(_canvas.Image));
            }

            if (list.Count == 0)
            {
                MessageBox.Show(this, "No scanned pages to hand off to Photoshop.", "NextScan Studio",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string tmpDir = @"C:\PS_Fix\tmp";
            if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);

            string pattern = Path.Combine(tmpDir, "scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            List<string> files = StudioExport.SaveBatch(list, pattern, "jpg");

            if (files.Count > 0)
            {
                // Write handoff markers
                string handoffFlag = Path.Combine(Path.GetTempPath(), "nextscan_handoff.txt");
                File.WriteAllText(handoffFlag, files[0], Encoding.UTF8);

                string outList = Path.Combine(tmpDir, "scan_output.txt");
                File.WriteAllLines(outList, files.ToArray(), Encoding.UTF8);

                BringPhotoshopToFront();
                SetStatusText("Handoff to Photoshop complete (" + files.Count + " pages).");
            }
        }

        void DoExport()
        {
            List<RawImage> list = new List<RawImage>();
            foreach (StudioPageItem it in _filmstrip.Pages)
            {
                if (it.Raw != null) list.Add(it.Raw);
            }

            if (list.Count == 0 && _canvas.Image != null)
            {
                list.Add(RawImage.FromBitmap(_canvas.Image));
            }

            if (list.Count == 0)
            {
                MessageBox.Show(this, "No scanned pages to export.", "NextScan Studio",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string outDir = _settings.OutputDirectory;
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            string baseName = "scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string fullPath = Path.Combine(outDir, baseName + "." + _settings.OutputFormat);

            List<string> saved = StudioExport.SaveBatch(list, fullPath, _settings.OutputFormat);
            if (saved.Count > 0)
            {
                SetStatusText("Saved " + saved.Count + " file(s) to " + outDir);
                Process.Start("explorer.exe", "/select,\"" + saved[0] + "\"");
            }
        }

        void DoUsbReset()
        {
            DeviceDescriptor dev = _inspector.GetSelectedDeviceDescriptor();
            string name = dev != null ? dev.FriendlyName : "";
            SetStatusText("Resetting USB scanner device...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                UsbReset.Log = OnBrokerLog;
                bool ok = UsbReset.TryReset(name);
                BeginInvoke(new Action(delegate
                {
                    if (ok)
                    {
                        SetStatusText("USB reset succeeded. Settling 4s, then refreshing...");
                        Thread.Sleep(4000);
                        RefreshScanners();
                    }
                    else
                    {
                        SetStatusText("USB reset task failed or not registered.");
                        MessageBox.Show(this, "USB Reset could not be completed automatically. " +
                                        "Run tools\\setup_usb_reset_task.ps1 as administrator to enable unelevated resets.",
                                        "NextScan Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            });
        }

        void BringPhotoshopToFront()
        {
            try
            {
                Process[] ps = Process.GetProcessesByName("Photoshop");
                if (ps.Length > 0 && ps[0].MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(ps[0].MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(ps[0].MainWindowHandle);
                }
            }
            catch { }
        }

        void UpdateDimensionsLabel()
        {
            if (_canvas.Image != null)
            {
                int w = _canvas.Image.Width;
                int h = _canvas.Image.Height;
                RectangleF crop = _canvas.CropNorm;
                int cw = (int)(crop.Width * w);
                int ch = (int)(crop.Height * h);
                _lblDimensions.Text = string.Format(CultureInfo.InvariantCulture,
                    "Image: {0}x{1} px | Crop: {2}x{3} px ({4:0.##}x{5:0.##} in @ {6} DPI)",
                    w, h, cw, ch, (double)cw / _settings.Dpi, (double)ch / _settings.Dpi, _settings.Dpi);
            }
            else
            {
                _lblDimensions.Text = "0 x 0 px";
            }
        }

        void SetStatusText(string txt)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(SetStatusText), txt); return; }
            _lblStatus.Text = txt;
        }

        void OnBrokerLog(string msg)
        {
            _inspector.AppendLog(msg);
        }

        Button CreateToolbarButton(string text, int x, int y, int w, Color bg, Color fg)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 32),
                BackColor = bg,
                ForeColor = fg,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        Button CreateSmallToolBtn(string text, int x, int y, int w)
        {
            Button b = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 22),
                BackColor = Color.FromArgb(36, 44, 58),
                ForeColor = Color.FromArgb(210, 220, 235),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }
    }
}
