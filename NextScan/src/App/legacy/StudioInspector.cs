// =============================================================================
// NextScan Studio - Studio inspector sidebar panels
// Plan ref: MASTER_PLAN section 13.1, 13.2.
//
// Collapsible/tabbed controls for Scanner selection, Scan parameters,
// 16-bit Curves & Tone Master, Document Cleanup, Export, and Diagnostics.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioInspector : Panel
    {
        public event EventHandler SettingsChanged;
        public event EventHandler RequestUsbReset;
        public event EventHandler RequestRefreshDevices;

        // UI Controls
        ComboBox _cbDevice;
        ComboBox _cbTransport;
        ComboBox _cbSource;
        ComboBox _cbDpi;
        ComboBox _cbColorMode;
        ComboBox _cbPageSize;
        CheckBox _chkAutoDeskew;
        ComboBox _cbDeskewPolicy;
        CheckBox _chkAutoCrop;
        NsSlider _tbWhitening;
        Label _lblWhiteningVal;
        CheckBox _chkNativeUi;
        ComboBox _cbFormat;
        TextBox _txtOutDir;
        CheckBox _chkOpenInPs;
        CheckBox _chkKeepFiles;

        StudioCurveEditor _curveEditor;
        ComboBox _cbCurvePresets;
        NsSegmented _segChannel;

        TextBox _txtLog;

        List<ScannerEntry> _scanners = new List<ScannerEntry>();

        public StudioCurveEditor CurveEditor { get { return _curveEditor; } }

        public StudioInspector()
        {
            Width = 340;
            Dock = DockStyle.Right;
            BackColor = Color.FromArgb(22, 26, 35);
            AutoScroll = true;

            BuildLayout();
        }

        void BuildLayout()
        {
            SuspendLayout();

            Panel content = new Panel();
            content.Width = 318;
            content.AutoSize = true;
            content.Location = new Point(4, 4);

            int y = 8;

            // --- 1. Device & Source Card ---
            content.Controls.Add(CreateHeaderLabel("DEVICE & CONNECTION", 10, y));
            y += 26;

            Label lDev = CreateFieldLabel("Scanner:", 12, y);
            content.Controls.Add(lDev);
            _cbDevice = CreateComboBox(12, y + 18, 290);
            _cbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbDevice.SelectedIndexChanged += OnDeviceChanged;
            content.Controls.Add(_cbDevice);
            y += 48;

            Label lTrans = CreateFieldLabel("Transport connection:", 12, y);
            content.Controls.Add(lTrans);
            _cbTransport = CreateComboBox(12, y + 18, 200);
            _cbTransport.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbTransport.SelectedIndexChanged += OnTransportChanged;
            content.Controls.Add(_cbTransport);

            NsButton bRefresh = CreateSmallButton("Probe", 220, y + 17, 82);
            bRefresh.Click += delegate { if (RequestRefreshDevices != null) RequestRefreshDevices(this, EventArgs.Empty); };
            content.Controls.Add(bRefresh);
            y += 48;

            Label lSrc = CreateFieldLabel("Source / Paper feed:", 12, y);
            content.Controls.Add(lSrc);
            _cbSource = CreateComboBox(12, y + 18, 290);
            _cbSource.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbSource.Items.AddRange(new object[] { "Flatbed (Glass)", "Feeder (ADF Simplex)", "Feeder (ADF Duplex)", "Film / Transparency" });
            _cbSource.SelectedIndex = 0;
            _cbSource.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbSource);
            y += 56;

            // --- 2. Scan Settings Card ---
            content.Controls.Add(CreateHeaderLabel("SCAN SETTINGS", 10, y));
            y += 26;

            Label lDpi = CreateFieldLabel("Resolution (DPI):", 12, y);
            content.Controls.Add(lDpi);
            _cbDpi = CreateComboBox(12, y + 18, 138);
            _cbDpi.Items.AddRange(new object[] { "75", "100", "150", "200", "300", "600", "1200" });
            _cbDpi.Text = "300";
            _cbDpi.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbDpi);

            Label lMode = CreateFieldLabel("Colour Mode:", 164, y);
            content.Controls.Add(lMode);
            _cbColorMode = CreateComboBox(164, y + 18, 138);
            _cbColorMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbColorMode.Items.AddRange(new object[] { "Color 24-bit", "Color 48-bit", "Gray 8-bit", "Gray 16-bit", "B&W 1-bit" });
            _cbColorMode.SelectedIndex = 0;
            _cbColorMode.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbColorMode);
            y += 48;

            Label lSize = CreateFieldLabel("Target Size:", 12, y);
            content.Controls.Add(lSize);
            _cbPageSize = CreateComboBox(12, y + 18, 138);
            _cbPageSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPageSize.Items.AddRange(new object[] { "Full Bed", "Letter (8.5x11)", "Legal (8.5x14)", "A4 (210x297mm)", "Photo 4x6", "Auto Detect" });
            _cbPageSize.SelectedIndex = 0;
            _cbPageSize.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbPageSize);

            _chkNativeUi = CreateCheckBox("Show vendor driver UI", 164, y + 19);
            _chkNativeUi.CheckedChanged += delegate { FireChanged(); };
            content.Controls.Add(_chkNativeUi);
            y += 56;

            // --- 3. Tone & Curves Master ---
            content.Controls.Add(CreateHeaderLabel("TONE & 16-BIT CURVES MASTER", 10, y));
            y += 26;

            // Initialize Curve Editor instance first to guarantee availability for child controls
            _curveEditor = new StudioCurveEditor();
            _curveEditor.Location = new Point(12, y + 28);
            _curveEditor.Size = new Size(290, 160);

            // Channel selector. A segmented control rather than four radio
            // buttons: the native radio glyph is painted by the system in a light
            // colour that BackColor cannot override, so it reads as a rendering
            // defect on an obsidian panel.
            Panel pChannels = new Panel { Location = new Point(12, y), Size = new Size(290, 26), BackColor = Color.Transparent };
            _segChannel = new NsSegmented
            {
                Location = new Point(0, 0),
                Size = new Size(206, 26),
                Items = new string[] { "RGB", "R", "G", "B" },
                SelectedIndex = 0
            };
            _segChannel.SelectedIndexChanged += delegate
            {
                if (_curveEditor == null) return;
                switch (_segChannel.SelectedIndex)
                {
                    case 1: _curveEditor.ActiveChannel = CurveChannel.Red; break;
                    case 2: _curveEditor.ActiveChannel = CurveChannel.Green; break;
                    case 3: _curveEditor.ActiveChannel = CurveChannel.Blue; break;
                    default: _curveEditor.ActiveChannel = CurveChannel.Rgb; break;
                }
            };
            pChannels.Controls.Add(_segChannel);

            NsButton bResetCurve = CreateSmallButton("Reset", 216, 0, 74);
            bResetCurve.Click += delegate { if (_curveEditor != null) _curveEditor.ResetActiveCurve(); };
            pChannels.Controls.Add(bResetCurve);

            content.Controls.Add(pChannels);
            y += 28;

            content.Controls.Add(_curveEditor);
            y += 166;

            Label lCurvePreset = CreateFieldLabel("Curve preset:", 12, y);
            content.Controls.Add(lCurvePreset);
            _cbCurvePresets = CreateComboBox(95, y - 2, 207);
            _cbCurvePresets.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbCurvePresets.Items.AddRange(new object[] { "Linear (Default)", "High Contrast", "Shadow Boost", "Brighten Midtones", "Invert Tone" });
            _cbCurvePresets.SelectedIndex = 0;
            _cbCurvePresets.SelectedIndexChanged += OnCurvePresetChanged;
            content.Controls.Add(_cbCurvePresets);
            y += 34;

            // --- 4. Document Clean & Deskew ---
            content.Controls.Add(CreateHeaderLabel("DOCUMENT CLEAN & DESKEW", 10, y));
            y += 26;

            _chkAutoCrop = CreateCheckBox("Auto-crop to document boundary", 12, y);
            _chkAutoCrop.Checked = true;
            _chkAutoCrop.CheckedChanged += delegate { FireChanged(); };
            content.Controls.Add(_chkAutoCrop);
            y += 24;

            _chkAutoDeskew = CreateCheckBox("Auto-deskew rotation", 12, y);
            _chkAutoDeskew.Checked = true;
            _chkAutoDeskew.CheckedChanged += delegate { FireChanged(); };
            content.Controls.Add(_chkAutoDeskew);
            y += 24;

            Label lDeskewPol = CreateFieldLabel("Deskew policy:", 12, y);
            content.Controls.Add(lDeskewPol);
            _cbDeskewPolicy = CreateComboBox(115, y - 2, 187);
            _cbDeskewPolicy.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbDeskewPolicy.Items.AddRange(new object[] { "Standard (4-Estimator Consensus)", "Strict Flatbed Guard ([0.6\u00b0..6.0\u00b0])" });
            _cbDeskewPolicy.SelectedIndex = 0;
            _cbDeskewPolicy.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbDeskewPolicy);
            y += 28;

            Label lWhite = CreateFieldLabel("Paper Whitening:", 12, y);
            content.Controls.Add(lWhite);
            _lblWhiteningVal = new Label { Text = "40%", Location = new Point(265, y), ForeColor = Color.FromArgb(0, 195, 255), AutoSize = true, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            content.Controls.Add(_lblWhiteningVal);
            y += 18;

            _tbWhitening = new NsSlider { Minimum = 0, Maximum = 100, Value = 40, Location = new Point(12, y), Size = new Size(290, 24) };
            _tbWhitening.ValueChanged += delegate
            {
                _lblWhiteningVal.Text = _tbWhitening.Value + "%";
                FireChanged();
            };
            content.Controls.Add(_tbWhitening);
            y += 38;

            // --- 5. Output & Destination ---
            content.Controls.Add(CreateHeaderLabel("OUTPUT & DESTINATION", 10, y));
            y += 26;

            Label lFmt = CreateFieldLabel("Format:", 12, y);
            content.Controls.Add(lFmt);
            _cbFormat = CreateComboBox(70, y - 2, 90);
            _cbFormat.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbFormat.Items.AddRange(new object[] { "JPG", "PNG", "TIFF", "PDF" });
            _cbFormat.SelectedIndex = 0;
            _cbFormat.SelectedIndexChanged += delegate { FireChanged(); };
            content.Controls.Add(_cbFormat);

            _chkOpenInPs = CreateCheckBox("Open in Photoshop", 12, y);
            _chkOpenInPs.Checked = true;
            _chkOpenInPs.CheckedChanged += delegate { FireChanged(); };
            content.Controls.Add(_chkOpenInPs);

            _chkKeepFiles = CreateCheckBox("Keep files on disk", 170, y);
            _chkKeepFiles.Checked = true;
            _chkKeepFiles.CheckedChanged += delegate { FireChanged(); };
            content.Controls.Add(_chkKeepFiles);
            y += 28;

            Label lDir = CreateFieldLabel("Folder:", 12, y);
            content.Controls.Add(lDir);
            _txtOutDir = new TextBox { Location = new Point(65, y - 2), Size = new Size(185, 22), BackColor = Color.FromArgb(32, 38, 50), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Text = @"C:\PS_Fix\scans" };
            content.Controls.Add(_txtOutDir);

            NsButton bBrowse = CreateSmallButton("...", 255, y - 3, 47);
            bBrowse.Click += delegate
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.SelectedPath = _txtOutDir.Text;
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        _txtOutDir.Text = fbd.SelectedPath;
                        FireChanged();
                    }
                }
            };
            content.Controls.Add(bBrowse);
            y += 36;

            // --- 6. Diagnostics Card ---
            content.Controls.Add(CreateHeaderLabel("DIAGNOSTICS & RECOVERY", 10, y));
            y += 26;

            NsButton bResetUsb = CreateSmallButton("Reset USB Device", 12, y, 140);
            bResetUsb.Click += delegate { if (RequestUsbReset != null) RequestUsbReset(this, EventArgs.Empty); };
            content.Controls.Add(bResetUsb);

            _txtLog = new TextBox
            {
                Location = new Point(12, y + 30),
                Size = new Size(290, 80),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(16, 19, 26),
                ForeColor = Color.FromArgb(150, 175, 195),
                Font = new Font("Consolas", 8f),
                BorderStyle = BorderStyle.FixedSingle
            };
            content.Controls.Add(_txtLog);
            y += 116;

            Controls.Add(content);
            ResumeLayout();
        }

        public void PopulateScanners(List<ScannerEntry> scanners, string selectedDeviceName, Transport selectedTransport)
        {
            _scanners = scanners ?? new List<ScannerEntry>();
            _cbDevice.Items.Clear();

            int pick = 0;
            for (int i = 0; i < _scanners.Count; i++)
            {
                _cbDevice.Items.Add(_scanners[i].DisplayName);
                if (!string.IsNullOrEmpty(selectedDeviceName) &&
                    _scanners[i].DisplayName.IndexOf(selectedDeviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    pick = i;
                }
            }

            if (_cbDevice.Items.Count > 0)
            {
                _cbDevice.SelectedIndex = pick;
            }
            UpdateTransportsForSelectedDevice(selectedTransport);
        }

        void OnDeviceChanged(object sender, EventArgs e)
        {
            UpdateTransportsForSelectedDevice(Transport.None);
            FireChanged();
        }

        void UpdateTransportsForSelectedDevice(Transport preferred)
        {
            _cbTransport.Items.Clear();
            if (_cbDevice.SelectedIndex < 0 || _cbDevice.SelectedIndex >= _scanners.Count) return;

            ScannerEntry entry = _scanners[_cbDevice.SelectedIndex];
            int pick = 0;
            for (int i = 0; i < entry.Connections.Count; i++)
            {
                DeviceDescriptor d = entry.Connections[i];
                string star = ReferenceEquals(d, entry.Preferred) ? " \u2605" : "";
                string label = string.Format("{0} ({1}-bit){2}", d.Transport, d.HostBitness, star);
                _cbTransport.Items.Add(label);
                if (preferred != Transport.None && d.Transport == preferred) pick = i;
                else if (preferred == Transport.None && ReferenceEquals(d, entry.Preferred)) pick = i;
            }

            if (_cbTransport.Items.Count > 0)
            {
                _cbTransport.SelectedIndex = pick;
            }
        }

        void OnTransportChanged(object sender, EventArgs e)
        {
            FireChanged();
        }

        public DeviceDescriptor GetSelectedDeviceDescriptor()
        {
            if (_cbDevice.SelectedIndex < 0 || _cbDevice.SelectedIndex >= _scanners.Count) return null;
            ScannerEntry entry = _scanners[_cbDevice.SelectedIndex];
            if (_cbTransport.SelectedIndex < 0 || _cbTransport.SelectedIndex >= entry.Connections.Count)
                return entry.Preferred;
            return entry.Connections[_cbTransport.SelectedIndex];
        }

        public void ApplySettings(StudioSettings s)
        {
            if (s == null) return;
            if (_cbDpi != null) _cbDpi.Text = s.Dpi.ToString();
            if (_cbColorMode != null)
            {
                switch (s.Mode)
                {
                    case ColorMode.Color48: _cbColorMode.SelectedIndex = 1; break;
                    case ColorMode.Gray8: _cbColorMode.SelectedIndex = 2; break;
                    case ColorMode.Gray16: _cbColorMode.SelectedIndex = 3; break;
                    case ColorMode.BlackWhite1: _cbColorMode.SelectedIndex = 4; break;
                    default: _cbColorMode.SelectedIndex = 0; break;
                }
            }
            if (_cbSource != null)
            {
                switch (s.Source)
                {
                    case PaperSource.Feeder: _cbSource.SelectedIndex = 1; break;
                    case PaperSource.FeederDuplex: _cbSource.SelectedIndex = 2; break;
                    case PaperSource.Film: _cbSource.SelectedIndex = 3; break;
                    default: _cbSource.SelectedIndex = 0; break;
                }
            }
            if (_chkAutoDeskew != null) _chkAutoDeskew.Checked = s.AutoDeskew;
            if (_cbDeskewPolicy != null) _cbDeskewPolicy.SelectedIndex = (s.DeskewProfile == DeskewProfileKind.StrictFlatbedGuard) ? 1 : 0;
            if (_chkAutoCrop != null) _chkAutoCrop.Checked = s.AutoCrop;
            if (_tbWhitening != null)
            {
                _tbWhitening.Value = Math.Max(0, Math.Min(100, s.Whitening));
                if (_lblWhiteningVal != null) _lblWhiteningVal.Text = _tbWhitening.Value + "%";
            }
            if (_chkNativeUi != null) _chkNativeUi.Checked = s.ShowVendorUi;
            if (_chkOpenInPs != null) _chkOpenInPs.Checked = s.OpenInPhotoshop;
            if (_chkKeepFiles != null) _chkKeepFiles.Checked = s.KeepFiles;
            if (_txtOutDir != null) _txtOutDir.Text = s.OutputDirectory ?? "";

            if (_cbFormat != null)
            {
                string fmt = (s.OutputFormat ?? "jpg").ToUpperInvariant();
                for (int i = 0; i < _cbFormat.Items.Count; i++)
                {
                    if (_cbFormat.Items[i].ToString().StartsWith(fmt)) { _cbFormat.SelectedIndex = i; break; }
                }
            }
        }

        public void CollectSettings(StudioSettings s)
        {
            if (s == null) return;
            DeviceDescriptor dev = GetSelectedDeviceDescriptor();
            if (dev != null)
            {
                s.DeviceName = dev.FriendlyName;
                s.Transport = dev.Transport;
                s.HostBitness = dev.HostBitness;
            }

            int dpi;
            if (_cbDpi != null && int.TryParse(_cbDpi.Text, out dpi) && dpi > 0) s.Dpi = dpi;

            if (_cbColorMode != null)
            {
                switch (_cbColorMode.SelectedIndex)
                {
                    case 1: s.Mode = ColorMode.Color48; break;
                    case 2: s.Mode = ColorMode.Gray8; break;
                    case 3: s.Mode = ColorMode.Gray16; break;
                    case 4: s.Mode = ColorMode.BlackWhite1; break;
                    default: s.Mode = ColorMode.Color24; break;
                }
            }

            if (_cbSource != null)
            {
                switch (_cbSource.SelectedIndex)
                {
                    case 1: s.Source = PaperSource.Feeder; break;
                    case 2: s.Source = PaperSource.FeederDuplex; break;
                    case 3: s.Source = PaperSource.Film; break;
                    default: s.Source = PaperSource.Flatbed; break;
                }
            }

            if (_chkAutoDeskew != null) s.AutoDeskew = _chkAutoDeskew.Checked;
            if (_cbDeskewPolicy != null) s.DeskewProfile = (_cbDeskewPolicy.SelectedIndex == 1) ?
                DeskewProfileKind.StrictFlatbedGuard : DeskewProfileKind.Standard;
            if (_chkAutoCrop != null) s.AutoCrop = _chkAutoCrop.Checked;
            if (_tbWhitening != null) s.Whitening = _tbWhitening.Value;
            if (_chkNativeUi != null) s.ShowVendorUi = _chkNativeUi.Checked;
            if (_chkOpenInPs != null) s.OpenInPhotoshop = _chkOpenInPs.Checked;
            if (_chkKeepFiles != null) s.KeepFiles = _chkKeepFiles.Checked;
            if (_cbFormat != null && _cbFormat.SelectedItem != null) s.OutputFormat = _cbFormat.Text.ToLowerInvariant();
            if (_txtOutDir != null) s.OutputDirectory = _txtOutDir.Text;
        }

        public void AppendLog(string msg)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), msg);
                return;
            }
            if (_txtLog.TextLength > 8000) _txtLog.Text = _txtLog.Text.Substring(4000);
            _txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + msg + "\r\n");
        }

        void OnCurvePresetChanged(object sender, EventArgs e)
        {
            if (_curveEditor == null) return;
            switch (_cbCurvePresets.SelectedIndex)
            {
                case 0: // Linear
                    _curveEditor.ResetActiveCurve();
                    break;
                case 1: // High Contrast (S-Curve)
                    _curveEditor.SetKnots(_curveEditor.ActiveChannel, new List<PointF> {
                        new PointF(0, 0), new PointF(64, 40), new PointF(192, 215), new PointF(255, 255)
                    });
                    break;
                case 2: // Shadow Boost
                    _curveEditor.SetKnots(_curveEditor.ActiveChannel, new List<PointF> {
                        new PointF(0, 0), new PointF(64, 90), new PointF(180, 205), new PointF(255, 255)
                    });
                    break;
                case 3: // Brighten Midtones
                    _curveEditor.SetKnots(_curveEditor.ActiveChannel, new List<PointF> {
                        new PointF(0, 0), new PointF(128, 160), new PointF(255, 255)
                    });
                    break;
                case 4: // Invert
                    _curveEditor.SetKnots(_curveEditor.ActiveChannel, new List<PointF> {
                        new PointF(0, 255), new PointF(255, 0)
                    });
                    break;
            }
        }

        Label CreateHeaderLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(295, 18),
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 195, 255)
            };
        }

        Label CreateFieldLabel(string text, int x, int y)
        {
            return new Label
            {
                Text = text,
                Location = new Point(x, y),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.FromArgb(175, 185, 205)
            };
        }

        // Returns NsComboBox, which derives from ComboBox - so every existing
        // call site keeps working while the painting becomes ours. A stock
        // ComboBox paints a white system dropdown regardless of BackColor.
        ComboBox CreateComboBox(int x, int y, int w)
        {
            return new NsComboBox
            {
                Location = new Point(x, y),
                Size = new Size(w, 26)
            };
        }

        CheckBox CreateCheckBox(string text, int x, int y)
        {
            return new NsCheckBox
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(text.Length * 7 + 30, 22)
            };
        }

        NsButton CreateSmallButton(string text, int x, int y, int w)
        {
            return new NsButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 26),
                Kind = NsButtonKind.Ghost,
                Font = Theme.UiBold(8.5f)
            };
        }

        void FireChanged()
        {
            if (SettingsChanged != null) SettingsChanged(this, EventArgs.Empty);
        }
    }
}
