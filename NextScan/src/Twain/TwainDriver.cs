// =============================================================================
// NextScan Studio - High level TWAIN driver
// Plan ref: MASTER_PLAN section 6.1 / 7.2.
//
// Translates the transport-neutral ScanSettings + DeviceCapabilities vocabulary
// into TWAIN capability triplets, and owns the message pump the data source needs.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Forms;
using NextScan.Core;

namespace NextScan.Twain
{
    public class TwainDriver
    {
        public Action<string> Log = delegate { };

        // ---------------------------------------------------------------- probe
        public List<DeviceDescriptor> Probe()
        {
            List<DeviceDescriptor> devices = new List<DeviceDescriptor>();
            using (TwainSession s = new TwainSession())
            {
                s.Log = Log;
                using (TwainPumpForm pump = new TwainPumpForm())
                {
                    NsResult r = s.OpenDsm(pump.Handle);
                    if (!r.Ok) { Log("TWAIN probe: " + r); return devices; }

                    foreach (TW_IDENTITY id in s.EnumerateSources())
                    {
                        // Microsoft's WIA-to-TWAIN shim appears as a data source on every
                        // machine. Surfacing it would show each scanner twice, and it is
                        // strictly worse than talking to WIA directly, so it is filtered.
                        if (IsWiaCompatibilityShim(id)) { Log("skipping WIA compatibility source: " + id.ProductName); continue; }

                        DeviceDescriptor d = new DeviceDescriptor();
                        d.Transport = Transport.Twain;
                        d.NativeId = id.ProductName ?? "";
                        d.FriendlyName = id.ProductName ?? "";
                        d.Manufacturer = id.Manufacturer ?? "";
                        d.Model = id.ProductFamily ?? "";
                        d.HostBitness = IntPtr.Size * 8;
                        d.IsNetwork = LooksLikeNetworkDevice(id.ProductName);
                        d.QuirkKey = "twain:" + Norm(id.Manufacturer) + "|" + Norm(id.ProductFamily) + "|" + Norm(id.ProductName);
                        devices.Add(d);
                    }
                    s.CloseDsm();
                }
            }
            return devices;
        }

        static bool IsWiaCompatibilityShim(TW_IDENTITY id)
        {
            string n = (id.ProductName ?? "").ToLowerInvariant();
            string m = (id.Manufacturer ?? "").ToLowerInvariant();
            return n.Contains("wia-") || n.StartsWith("wia ") ||
                   (m.Contains("microsoft") && n.Contains("wia"));
        }

        static bool LooksLikeNetworkDevice(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            return n.Contains("network") || n.Contains("lan") || n.Contains("wsd") || n.Contains("copier");
        }

        static string Norm(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant();
        }

        // ---------------------------------------------------------------- capabilities
        public NsResult GetCapabilities(string productName, out DeviceCapabilities caps)
        {
            caps = new DeviceCapabilities();
            using (TwainSession s = new TwainSession())
            {
                s.Log = Log;
                using (TwainPumpForm pump = new TwainPumpForm())
                {
                    NsResult r = s.OpenDsm(pump.Handle);
                    if (!r.Ok) return r;

                    r = s.OpenSource(productName);
                    if (!r.Ok) { s.CloseDsm(); return r; }

                    try { ReadCapabilities(s, caps); }
                    catch (Exception ex) { Log("capability read threw: " + ex.Message); }

                    s.CloseSource();
                    s.CloseDsm();
                }
            }
            return NsResult.Success();
        }

        void ReadCapabilities(TwainSession s, DeviceCapabilities caps)
        {
            // Pin the unit system before reading anything dimensional. Without this
            // the bed size comes back in whatever unit the driver last used - on the
            // LiDE 400 that read as 0.16 "inches" for an 8.5 inch bed.
            s.CapSet(ICAP.UNITS, TWTY.UINT16, TWUN.INCHES);

            // ---- resolution ----
            bool isRange;
            List<double> res = s.CapGetValues(ICAP.XRESOLUTION, out isRange);
            caps.ResolutionIsRange = isRange;
            if (isRange && res.Count >= 3)
            {
                caps.MinResolution = (int)res[0];
                caps.MaxResolution = (int)res[1];
                int step = Math.Max(1, (int)res[2]);
                // Offer the conventional ladder, clipped to what the device allows,
                // rather than every step in a 75..4800 range.
                int[] ladder = { 75, 100, 150, 200, 240, 300, 400, 600, 720, 800, 1200, 1600, 2400, 3200, 4800, 6400, 9600 };
                foreach (int v in ladder)
                    if (v >= caps.MinResolution && v <= caps.MaxResolution && (v - caps.MinResolution) % step == 0)
                        caps.Resolutions.Add(v);
                if (caps.Resolutions.Count == 0) caps.Resolutions.Add(caps.MinResolution);
            }
            else
            {
                foreach (double v in res)
                {
                    int iv = (int)Math.Round(v);
                    if (iv > 0 && !caps.Resolutions.Contains(iv)) caps.Resolutions.Add(iv);
                }
                caps.Resolutions.Sort();
                if (caps.Resolutions.Count > 0)
                {
                    caps.MinResolution = caps.Resolutions[0];
                    caps.MaxResolution = caps.Resolutions[caps.Resolutions.Count - 1];
                }
            }
            caps.RawCapabilityLog.Add("ICAP_XRESOLUTION: " + Join(res) + (isRange ? " (range)" : " (list)"));

            // ---- pixel types ----
            List<double> pix = s.CapGetValues(ICAP.PIXELTYPE, out isRange);
            foreach (double v in pix)
            {
                switch ((ushort)v)
                {
                    case TWPT.BW: if (!caps.ColorModes.Contains(ColorMode.BlackWhite1)) caps.ColorModes.Add(ColorMode.BlackWhite1); break;
                    case TWPT.GRAY: if (!caps.ColorModes.Contains(ColorMode.Gray8)) caps.ColorModes.Add(ColorMode.Gray8); break;
                    case TWPT.RGB: if (!caps.ColorModes.Contains(ColorMode.Color24)) caps.ColorModes.Add(ColorMode.Color24); break;
                }
            }
            if (caps.ColorModes.Count == 0) caps.ColorModes.Add(ColorMode.Color24);
            caps.RawCapabilityLog.Add("ICAP_PIXELTYPE " + s.DescribeLastContainer() + ": " + Join(pix));

            // ---- bit depth: is 48-bit colour or 16-bit grey available? ----
            //
            // ICAP_BITDEPTH is scoped to the CURRENT ICAP_PIXELTYPE. Querying it
            // cold returns whatever the driver last had selected - on the LiDE 400
            // that produced the meaningless list 0,1,2,3,5,7,10,52,53,13. Select
            // the pixel type first, then ask, then restore.
            double originalPixelType = s.CapGetCurrent(ICAP.PIXELTYPE, TWPT.RGB);

            if (caps.ColorModes.Contains(ColorMode.Color24) && s.CapSet(ICAP.PIXELTYPE, TWTY.UINT16, TWPT.RGB))
            {
                List<double> rgbDepths = s.CapGetValues(ICAP.BITDEPTH, out isRange);
                foreach (double v in rgbDepths)
                {
                    // Accept either convention: 48 bits per pixel or 16 per channel.
                    int iv = (int)v;
                    if ((iv == 48 || iv == 16) && !caps.ColorModes.Contains(ColorMode.Color48))
                        caps.ColorModes.Add(ColorMode.Color48);
                }
                caps.RawCapabilityLog.Add("ICAP_BITDEPTH (RGB) " + s.DescribeLastContainer() + ": " + Join(rgbDepths));
            }

            if (caps.ColorModes.Contains(ColorMode.Gray8) && s.CapSet(ICAP.PIXELTYPE, TWTY.UINT16, TWPT.GRAY))
            {
                List<double> grayDepths = s.CapGetValues(ICAP.BITDEPTH, out isRange);
                foreach (double v in grayDepths)
                    if ((int)v == 16 && !caps.ColorModes.Contains(ColorMode.Gray16))
                        caps.ColorModes.Add(ColorMode.Gray16);
                caps.RawCapabilityLog.Add("ICAP_BITDEPTH (grey) " + s.DescribeLastContainer() + ": " + Join(grayDepths));
            }

            s.CapSet(ICAP.PIXELTYPE, TWTY.UINT16, originalPixelType);

            // ---- sources ----
            //
            // Merely exposing CAP_FEEDERENABLED does not mean a feeder exists: the
            // LiDE 400 is a bare flatbed yet advertises the capability, and offering
            // a phantom ADF in the UI is worse than not offering one. The reliable
            // test is whether the device will actually accept being switched to it.
            caps.SupportsFlatbed = true;
            caps.Sources.Add(PaperSource.Flatbed);

            caps.SupportsFeeder = false;
            if (s.CapIsSupported(CAP.FEEDERENABLED))
            {
                double actual;
                double original = s.CapGetCurrent(CAP.FEEDERENABLED, 0);
                if (s.CapSet(CAP.FEEDERENABLED, TWTY.BOOL, 1, out actual) && actual > 0)
                {
                    caps.SupportsFeeder = true;
                    caps.Sources.Add(PaperSource.Feeder);
                }
                s.CapSet(CAP.FEEDERENABLED, TWTY.BOOL, original);
            }

            caps.SupportsDuplex = caps.SupportsFeeder && s.CapGetCurrent(CAP.DUPLEX, 0) > 0;
            if (caps.SupportsDuplex) caps.Sources.Add(PaperSource.FeederDuplex);

            // Same reasoning for the transparency unit: ICAP_LIGHTPATH is present on
            // plenty of scanners with no film adapter, so require that transmissive
            // mode is actually selectable.
            caps.SupportsFilm = false;
            if (s.CapIsSupported(ICAP.LIGHTPATH))
            {
                double actual;
                double original = s.CapGetCurrent(ICAP.LIGHTPATH, TWLP.REFLECTIVE);
                if (s.CapSet(ICAP.LIGHTPATH, TWTY.UINT16, TWLP.TRANSMISSIVE, out actual) &&
                    (ushort)actual == TWLP.TRANSMISSIVE)
                {
                    caps.SupportsFilm = true;
                    caps.Sources.Add(PaperSource.Film);
                }
                s.CapSet(ICAP.LIGHTPATH, TWTY.UINT16, original);
            }
            caps.RawCapabilityLog.Add("CAP_FEEDERENABLED supported: " + caps.SupportsFeeder +
                                      ", CAP_DUPLEX: " + caps.SupportsDuplex +
                                      ", ICAP_LIGHTPATH: " + caps.SupportsFilm);

            // ---- bed size ----
            caps.PhysicalWidthIn = s.CapGetCurrent(ICAP.PHYSICALWIDTH, 0);
            caps.PhysicalHeightIn = s.CapGetCurrent(ICAP.PHYSICALHEIGHT, 0);

            // A scanner smaller than an inch does not exist. Rather than propagate a
            // nonsense value into the crop UI, fall back to Letter/A4 and say so.
            if (caps.PhysicalWidthIn < 1.0 || caps.PhysicalHeightIn < 1.0)
            {
                caps.RawCapabilityLog.Add(string.Format(CultureInfo.InvariantCulture,
                    "implausible bed size {0:0.###} x {1:0.###} in reported - assuming 8.5 x 11.7",
                    caps.PhysicalWidthIn, caps.PhysicalHeightIn));
                caps.PhysicalWidthIn = 8.5;
                caps.PhysicalHeightIn = 11.7;
            }
            caps.RawCapabilityLog.Add(string.Format(CultureInfo.InvariantCulture,
                "physical bed: {0:0.##} x {1:0.##} in", caps.PhysicalWidthIn, caps.PhysicalHeightIn));

            // ---- device-side processing ----
            caps.SupportsAutoDeskew = s.CapIsSupported(ICAP.AUTOMATICDESKEW);
            caps.SupportsAutoBorderDetect = s.CapIsSupported(ICAP.AUTOMATICBORDERDETECTION);
            caps.SupportsBlankPageRemoval = s.CapIsSupported(ICAP.AUTODISCARDBLANKPAGES);

            // ---- hidden UI ----
            double uiControllable = s.CapGetCurrent(CAP.UICONTROLLABLE, double.NaN);
            // Absent capability means "unknown", and the overwhelming majority of
            // sources do honour ShowUI=FALSE, so default to true and let the quirks
            // database override for the ones that lie.
            caps.SupportsHiddenUi = double.IsNaN(uiControllable) || uiControllable > 0;

            // ---- enhancement ranges ----
            List<double> bright = s.CapGetValues(ICAP.BRIGHTNESS, out isRange);
            if (bright.Count >= 2) { caps.HasBrightness = true; caps.BrightnessMin = bright[0]; caps.BrightnessMax = bright[1]; }
            List<double> contrast = s.CapGetValues(ICAP.CONTRAST, out isRange);
            if (contrast.Count >= 2) { caps.HasContrast = true; caps.ContrastMin = contrast[0]; caps.ContrastMax = contrast[1]; }
        }

        static string Join(List<double> v)
        {
            if (v == null || v.Count == 0) return "(unsupported)";
            List<string> parts = new List<string>();
            int max = Math.Min(v.Count, 40);
            for (int i = 0; i < max; i++) parts.Add(v[i].ToString("0.##", CultureInfo.InvariantCulture));
            if (v.Count > max) parts.Add("...");
            return string.Join(", ", parts.ToArray());
        }

        // ---------------------------------------------------------------- scan
        /// <summary>
        /// Runs one acquisition. onImage is called per page on this thread; return
        /// false from it to stop a batch early.
        /// </summary>
        public NsResult Scan(string productName, ScanSettings settings, Func<RawImage, bool> onImage)
        {
            using (TwainSession s = new TwainSession())
            {
                s.Log = Log;
                using (TwainPumpForm pump = new TwainPumpForm())
                {
                    pump.Session = s;

                    NsResult r = s.OpenDsm(pump.Handle);
                    if (!r.Ok) return r;

                    try
                    {
                        r = s.OpenSource(productName);
                        if (!r.Ok) return r;

                        ApplySettings(s, settings);

                        bool showUi = settings.ShowVendorUi;
                        r = s.EnableSource(showUi, showUi, pump.Handle);
                        if (!r.Ok) return r;

                        // Pump until the source says it has an image, asks to close, or
                        // we hit the watchdog (plan section 7.7).
                        int timeoutMs = settings.IsPreview ? 120000 : 300000;
                        if (showUi) timeoutMs = 900000;     // a human is driving the vendor dialog

                        Stopwatch sw = Stopwatch.StartNew();
                        Application.AddMessageFilter(pump);
                        try
                        {
                            while (!s.XferReady && !s.CloseRequested)
                            {
                                Application.DoEvents();
                                System.Threading.Thread.Sleep(10);
                                if (sw.ElapsedMilliseconds > timeoutMs)
                                {
                                    s.Cancel();
                                    return NsResult.Fail(NsError.HostTimeout,
                                        "The scanner did not respond within " + (timeoutMs / 1000) + " seconds.",
                                        "Check the scanner is powered on and not showing an error, then try again.");
                                }
                            }
                        }
                        finally { Application.RemoveMessageFilter(pump); }

                        if (s.CloseRequested && !s.XferReady)
                        {
                            s.DisableSource();
                            return NsResult.Fail(NsError.TwainCancelled, "Scan cancelled.", "");
                        }

                        r = s.TransferAll(onImage, settings.PageCount);

                        s.DisableSource();
                        return r;
                    }
                    finally
                    {
                        try { s.CloseSource(); } catch { }
                        try { s.CloseDsm(); } catch { }
                    }
                }
            }
        }

        /// <summary>
        /// Capability order matters: transfer mechanism, then pixel type, then bit
        /// depth (which is scoped to the pixel type), then units, then resolution,
        /// then geometry. Setting resolution before units silently reinterprets the
        /// number in whatever unit the device happened to be in.
        /// </summary>
        void ApplySettings(TwainSession s, ScanSettings settings)
        {
            double actual;

            // 1. Memory transfer is the default (plan section 7.2).
            s.CapSet(ICAP.XFERMECH, TWTY.UINT16, TWSX.MEMORY);

            // 2. Pixel type.
            ushort pixelType;
            int bitDepth;
            switch (settings.Mode)
            {
                case ColorMode.BlackWhite1: pixelType = TWPT.BW; bitDepth = 1; break;
                case ColorMode.Gray8: pixelType = TWPT.GRAY; bitDepth = 8; break;
                case ColorMode.Gray16: pixelType = TWPT.GRAY; bitDepth = 16; break;
                case ColorMode.Color48: pixelType = TWPT.RGB; bitDepth = 48; break;
                default: pixelType = TWPT.RGB; bitDepth = 24; break;
            }
            s.CapSet(ICAP.PIXELTYPE, TWTY.UINT16, pixelType, out actual);
            if ((ushort)actual != pixelType)
                Log("device clamped ICAP_PIXELTYPE " + pixelType + " -> " + actual);

            // 3. Bit depth, scoped to the pixel type just set.
            //
            // The spec says ICAP_BITDEPTH is bits per PIXEL, but a good number of
            // drivers (Canon's ScanGear among them) implement it as bits per
            // CHANNEL and reject 24 with TWCC_BADVALUE. Try the spec value, then
            // the per-channel one, and treat total failure as "keep the default",
            // which is correct for every driver seen so far.
            double got;
            if (!s.CapSet(ICAP.BITDEPTH, TWTY.UINT16, bitDepth, out got))
            {
                int perChannel = (pixelType == TWPT.RGB) ? bitDepth / 3 : bitDepth;
                if (perChannel != bitDepth && perChannel > 0)
                {
                    if (s.CapSet(ICAP.BITDEPTH, TWTY.UINT16, perChannel, out got))
                        Log("ICAP_BITDEPTH accepted as bits-per-channel (" + perChannel + ")");
                    else
                        Log("ICAP_BITDEPTH not settable - using the driver default");
                }
            }

            // 4. Units before any geometry or resolution value.
            s.CapSet(ICAP.UNITS, TWTY.UINT16, TWUN.INCHES);

            // 5. Resolution.
            double dpi = settings.Dpi;
            s.CapSet(ICAP.XRESOLUTION, TWTY.FIX32, dpi, out actual);
            if (Math.Abs(actual - dpi) > 0.5) Log("device clamped resolution " + dpi + " -> " + actual);
            s.CapSet(ICAP.YRESOLUTION, TWTY.FIX32, actual);

            // 6. Paper source.
            bool wantFeeder = settings.Source == PaperSource.Feeder || settings.Source == PaperSource.FeederDuplex;
            s.CapSet(CAP.FEEDERENABLED, TWTY.BOOL, wantFeeder ? 1 : 0);
            if (settings.Source == PaperSource.FeederDuplex || settings.Duplex)
                s.CapSet(CAP.DUPLEXENABLED, TWTY.BOOL, 1);
            else
                s.CapSet(CAP.DUPLEXENABLED, TWTY.BOOL, 0);

            if (settings.Source == PaperSource.Film)
                s.CapSet(ICAP.LIGHTPATH, TWTY.UINT16, TWLP.TRANSMISSIVE);

            // 7. Device-side processing - only when explicitly asked for. Our own
            // pipeline is better on flatbeds, but firmware deskew on an ADF is free
            // and more accurate than post-hoc correction.
            if (settings.UseDeviceAutoDeskew) s.CapSet(ICAP.AUTOMATICDESKEW, TWTY.BOOL, 1);
            if (settings.UseDeviceAutoCrop) s.CapSet(ICAP.AUTOMATICBORDERDETECTION, TWTY.BOOL, 1);

            // 8. Scan region.
            if (settings.HasRegion)
            {
                s.SetScanRegion(settings.RegionLeftIn, settings.RegionTopIn,
                                settings.RegionLeftIn + settings.RegionWidthIn,
                                settings.RegionTopIn + settings.RegionHeightIn);
            }

            // 9. Enhancement.
            if (Math.Abs(settings.Brightness) > 0.001) s.CapSet(ICAP.BRIGHTNESS, TWTY.FIX32, settings.Brightness);
            if (Math.Abs(settings.Contrast) > 0.001) s.CapSet(ICAP.CONTRAST, TWTY.FIX32, settings.Contrast);

            // 10. How many pages. -1 means "until the feeder is empty".
            int xferCount = settings.PageCount <= 0 ? -1 : settings.PageCount;
            s.CapSet(CAP.XFERCOUNT, TWTY.INT16, xferCount);

            // 11. Suppress the driver's own progress window when we are driving.
            if (!settings.ShowVendorUi) s.CapSet(CAP.INDICATORS, TWTY.BOOL, 0);
        }
    }

    /// <summary>
    /// The window a data source parents its dialogs to, and the message filter that
    /// forwards every message to the DS before normal dispatch. TWAIN sources
    /// communicate MSG_XFERREADY through this path, so it is not optional.
    /// </summary>
    public class TwainPumpForm : Form, IMessageFilter
    {
        public TwainSession Session;

        public TwainPumpForm()
        {
            // Never visible, never in the taskbar, but a real HWND - some drivers
            // reject HWND_MESSAGE windows and a few reject IntPtr.Zero outright.
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-32000, -32000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
            CreateHandle();
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        public bool PreFilterMessage(ref Message m)
        {
            TwainSession s = Session;
            if (s == null) return false;
            try { return s.ProcessEvent(ref m); }
            catch { return false; }
        }
    }
}
