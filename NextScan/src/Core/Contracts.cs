// =============================================================================
// NextScan Studio - Device abstraction contracts
// Plan ref: MASTER_PLAN section 6.1. One vocabulary that TWAIN, WIA and eSCL all
// translate into, so the UI never learns a transport-specific dialect.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NextScan.Core
{
    public enum Transport { None = 0, Twain = 1, Wia = 2, Escl = 3, Wsd = 4, File = 5, Simulator = 6 }

    public enum PaperSource { Auto = 0, Flatbed = 1, Feeder = 2, FeederDuplex = 3, Film = 4 }

    public enum ColorMode { Color24 = 0, Color48 = 1, Gray8 = 2, Gray16 = 3, BlackWhite1 = 4 }

    /// <summary>Stable, transport-tagged identity for one physical device.</summary>
    public class DeviceDescriptor
    {
        public Transport Transport;
        /// <summary>Transport-native id: TWAIN ProductName, WIA DeviceID, eSCL base URL.</summary>
        public string NativeId = "";
        public string FriendlyName = "";
        public string Manufacturer = "";
        public string Model = "";
        /// <summary>32 or 64 - which host process owns this device.</summary>
        public int HostBitness;
        public bool IsNetwork;
        /// <summary>Key into the Device Quirks Database (plan section 7.6).</summary>
        public string QuirkKey = "";

        /// <summary>Globally unique handle the UI passes back to open this device.</summary>
        public string Id
        {
            get { return Transport.ToString().ToLowerInvariant() + ":" + HostBitness + ":" + NativeId; }
        }

        public override string ToString()
        {
            return "[" + Transport.ToString().ToUpperInvariant() + (HostBitness == 32 ? "32" : "64") + "] " + FriendlyName;
        }

        public JsonObj ToJson()
        {
            return new JsonObj()
                .Set("transport", Transport.ToString())
                .Set("nativeId", NativeId)
                .Set("friendlyName", FriendlyName)
                .Set("manufacturer", Manufacturer)
                .Set("model", Model)
                .Set("hostBitness", HostBitness)
                .Set("isNetwork", IsNetwork)
                .Set("quirkKey", QuirkKey);
        }

        public static DeviceDescriptor FromJson(JsonObj o)
        {
            DeviceDescriptor d = new DeviceDescriptor();
            try { d.Transport = (Transport)Enum.Parse(typeof(Transport), o.Str("transport", "None"), true); }
            catch { d.Transport = Transport.None; }
            d.NativeId = o.Str("nativeId", "");
            d.FriendlyName = o.Str("friendlyName", d.NativeId);
            d.Manufacturer = o.Str("manufacturer", "");
            d.Model = o.Str("model", "");
            d.HostBitness = o.Int("hostBitness", 64);
            d.IsNetwork = o.Bool("isNetwork", false);
            d.QuirkKey = o.Str("quirkKey", "");
            return d;
        }
    }

    /// <summary>
    /// What a device can actually do. Every field is filled from a real capability
    /// query - never guessed - so the UI can grey out the impossible instead of
    /// failing at scan time.
    /// </summary>
    public class DeviceCapabilities
    {
        public List<int> Resolutions = new List<int>();
        public int MinResolution = 75;
        public int MaxResolution = 1200;
        /// <summary>True when the device reports a continuous range, not a discrete list.</summary>
        public bool ResolutionIsRange;

        public List<ColorMode> ColorModes = new List<ColorMode>();
        public List<PaperSource> Sources = new List<PaperSource>();

        public bool SupportsDuplex;
        public bool SupportsFeeder;
        public bool SupportsFlatbed;
        public bool SupportsFilm;
        public bool SupportsAutoDeskew;
        public bool SupportsAutoBorderDetect;
        public bool SupportsBlankPageRemoval;
        public bool SupportsHiddenUi = true;

        /// <summary>Physical bed size in inches (0 when the device will not say).</summary>
        public double PhysicalWidthIn;
        public double PhysicalHeightIn;

        public bool HasBrightness, HasContrast;
        public double BrightnessMin, BrightnessMax, ContrastMin, ContrastMax;

        /// <summary>Raw capability dump for the Diagnostics panel (plan section 13.8).</summary>
        public List<string> RawCapabilityLog = new List<string>();

        public JsonObj ToJson()
        {
            List<object> res = new List<object>();
            foreach (int r in Resolutions) res.Add(r);
            List<object> cm = new List<object>();
            foreach (ColorMode c in ColorModes) cm.Add(c.ToString());
            List<object> sr = new List<object>();
            foreach (PaperSource s in Sources) sr.Add(s.ToString());
            List<object> raw = new List<object>();
            foreach (string s in RawCapabilityLog) raw.Add(s);

            return new JsonObj()
                .Set("resolutions", res)
                .Set("minResolution", MinResolution)
                .Set("maxResolution", MaxResolution)
                .Set("resolutionIsRange", ResolutionIsRange)
                .Set("colorModes", cm)
                .Set("sources", sr)
                .Set("supportsDuplex", SupportsDuplex)
                .Set("supportsFeeder", SupportsFeeder)
                .Set("supportsFlatbed", SupportsFlatbed)
                .Set("supportsFilm", SupportsFilm)
                .Set("supportsAutoDeskew", SupportsAutoDeskew)
                .Set("supportsAutoBorderDetect", SupportsAutoBorderDetect)
                .Set("supportsBlankPageRemoval", SupportsBlankPageRemoval)
                .Set("supportsHiddenUi", SupportsHiddenUi)
                .Set("physicalWidthIn", PhysicalWidthIn)
                .Set("physicalHeightIn", PhysicalHeightIn)
                .Set("hasBrightness", HasBrightness)
                .Set("hasContrast", HasContrast)
                .Set("brightnessMin", BrightnessMin)
                .Set("brightnessMax", BrightnessMax)
                .Set("contrastMin", ContrastMin)
                .Set("contrastMax", ContrastMax)
                .Set("rawCapabilityLog", raw);
        }

        public static DeviceCapabilities FromJson(JsonObj o)
        {
            DeviceCapabilities c = new DeviceCapabilities();
            if (o == null) return c;

            List<object> res = o.Arr("resolutions");
            if (res != null)
                foreach (object r in res)
                    try { c.Resolutions.Add(Convert.ToInt32(r, CultureInfo.InvariantCulture)); } catch { }

            List<object> cm = o.Arr("colorModes");
            if (cm != null)
                foreach (object m in cm)
                    try { c.ColorModes.Add((ColorMode)Enum.Parse(typeof(ColorMode), Convert.ToString(m), true)); } catch { }

            List<object> sr = o.Arr("sources");
            if (sr != null)
                foreach (object s in sr)
                    try { c.Sources.Add((PaperSource)Enum.Parse(typeof(PaperSource), Convert.ToString(s), true)); } catch { }

            List<object> raw = o.Arr("rawCapabilityLog");
            if (raw != null)
                foreach (object s in raw) c.RawCapabilityLog.Add(Convert.ToString(s));

            c.MinResolution = o.Int("minResolution", 75);
            c.MaxResolution = o.Int("maxResolution", 1200);
            c.ResolutionIsRange = o.Bool("resolutionIsRange", false);
            c.SupportsDuplex = o.Bool("supportsDuplex", false);
            c.SupportsFeeder = o.Bool("supportsFeeder", false);
            c.SupportsFlatbed = o.Bool("supportsFlatbed", true);
            c.SupportsFilm = o.Bool("supportsFilm", false);
            c.SupportsAutoDeskew = o.Bool("supportsAutoDeskew", false);
            c.SupportsAutoBorderDetect = o.Bool("supportsAutoBorderDetect", false);
            c.SupportsBlankPageRemoval = o.Bool("supportsBlankPageRemoval", false);
            c.SupportsHiddenUi = o.Bool("supportsHiddenUi", true);
            c.PhysicalWidthIn = o.Dbl("physicalWidthIn", 0);
            c.PhysicalHeightIn = o.Dbl("physicalHeightIn", 0);
            c.HasBrightness = o.Bool("hasBrightness", false);
            c.HasContrast = o.Bool("hasContrast", false);
            c.BrightnessMin = o.Dbl("brightnessMin", 0);
            c.BrightnessMax = o.Dbl("brightnessMax", 0);
            c.ContrastMin = o.Dbl("contrastMin", 0);
            c.ContrastMax = o.Dbl("contrastMax", 0);
            return c;
        }
    }

    /// <summary>What the caller asks the device to do.</summary>
    public class ScanSettings
    {
        public int Dpi = 300;
        public ColorMode Mode = ColorMode.Color24;
        public PaperSource Source = PaperSource.Flatbed;

        /// <summary>Scan region in inches from the top-left of the bed. Zero width/height = full bed.</summary>
        public double RegionLeftIn, RegionTopIn, RegionWidthIn, RegionHeightIn;

        /// <summary>0 = scan until the feeder empties; otherwise a hard page cap.</summary>
        public int PageCount = 1;

        public bool ShowVendorUi;
        public bool UseDeviceAutoDeskew;
        public bool UseDeviceAutoCrop;
        public bool Duplex;

        public double Brightness;   // device units, 0 = leave alone
        public double Contrast;

        /// <summary>Preview passes drop resolution hard and skip post-processing.</summary>
        public bool IsPreview;

        public bool HasRegion
        {
            get { return RegionWidthIn > 0.01 && RegionHeightIn > 0.01; }
        }

        public JsonObj ToJson()
        {
            return new JsonObj()
                .Set("dpi", Dpi)
                .Set("mode", Mode.ToString())
                .Set("source", Source.ToString())
                .Set("regionLeftIn", RegionLeftIn)
                .Set("regionTopIn", RegionTopIn)
                .Set("regionWidthIn", RegionWidthIn)
                .Set("regionHeightIn", RegionHeightIn)
                .Set("pageCount", PageCount)
                .Set("showVendorUi", ShowVendorUi)
                .Set("useDeviceAutoDeskew", UseDeviceAutoDeskew)
                .Set("useDeviceAutoCrop", UseDeviceAutoCrop)
                .Set("duplex", Duplex)
                .Set("brightness", Brightness)
                .Set("contrast", Contrast)
                .Set("isPreview", IsPreview);
        }

        public static ScanSettings FromJson(JsonObj o)
        {
            ScanSettings s = new ScanSettings();
            if (o == null) return s;
            s.Dpi = o.Int("dpi", 300);
            try { s.Mode = (ColorMode)Enum.Parse(typeof(ColorMode), o.Str("mode", "Color24"), true); } catch { }
            try { s.Source = (PaperSource)Enum.Parse(typeof(PaperSource), o.Str("source", "Flatbed"), true); } catch { }
            s.RegionLeftIn = o.Dbl("regionLeftIn", 0);
            s.RegionTopIn = o.Dbl("regionTopIn", 0);
            s.RegionWidthIn = o.Dbl("regionWidthIn", 0);
            s.RegionHeightIn = o.Dbl("regionHeightIn", 0);
            s.PageCount = o.Int("pageCount", 1);
            s.ShowVendorUi = o.Bool("showVendorUi", false);
            s.UseDeviceAutoDeskew = o.Bool("useDeviceAutoDeskew", false);
            s.UseDeviceAutoCrop = o.Bool("useDeviceAutoCrop", false);
            s.Duplex = o.Bool("duplex", false);
            s.Brightness = o.Dbl("brightness", 0);
            s.Contrast = o.Dbl("contrast", 0);
            s.IsPreview = o.Bool("isPreview", false);
            return s;
        }
    }

    /// <summary>
    /// Metadata for one acquired page. Pixels live in a shared-memory region named
    /// by <see cref="ShmName"/>; see FrameHeader (plan Appendix D).
    /// </summary>
    public class AcquiredFrame
    {
        public string ShmName = "";
        public long ShmSize;
        public int Width, Height, Stride;
        public int Channels;         // 1, 3 or 4
        public int BitsPerChannel;   // 1, 8 or 16
        public double XDpi, YDpi;
        public int PageIndex;
        public int Side;             // 0 = front, 1 = back
        public bool BottomUp;
        /// <summary>Set when the host wrote a ready-to-open image file instead of raw pixels.</summary>
        public string FilePath = "";

        public JsonObj ToJson()
        {
            return new JsonObj()
                .Set("shmName", ShmName)
                .Set("shmSize", ShmSize)
                .Set("width", Width)
                .Set("height", Height)
                .Set("stride", Stride)
                .Set("channels", Channels)
                .Set("bitsPerChannel", BitsPerChannel)
                .Set("xDpi", XDpi)
                .Set("yDpi", YDpi)
                .Set("pageIndex", PageIndex)
                .Set("side", Side)
                .Set("bottomUp", BottomUp)
                .Set("filePath", FilePath);
        }

        public static AcquiredFrame FromJson(JsonObj o)
        {
            AcquiredFrame f = new AcquiredFrame();
            if (o == null) return f;
            f.ShmName = o.Str("shmName", "");
            f.ShmSize = o.Long("shmSize", 0);
            f.Width = o.Int("width", 0);
            f.Height = o.Int("height", 0);
            f.Stride = o.Int("stride", 0);
            f.Channels = o.Int("channels", 3);
            f.BitsPerChannel = o.Int("bitsPerChannel", 8);
            f.XDpi = o.Dbl("xDpi", 300);
            f.YDpi = o.Dbl("yDpi", 300);
            f.PageIndex = o.Int("pageIndex", 0);
            f.Side = o.Int("side", 0);
            f.BottomUp = o.Bool("bottomUp", false);
            f.FilePath = o.Str("filePath", "");
            return f;
        }
    }

    /// <summary>Stable error codes. Ranges are defined in plan Appendix G.</summary>
    public enum NsError
    {
        None = 0,

        HostSpawnFailed = 1000,
        HostPipeBroken = 1001,
        HostProtocolViolation = 1002,
        HostTimeout = 1003,
        HostCrashed = 1004,

        TwainDsmNotFound = 1100,
        TwainDsmOpenFailed = 1101,
        TwainNoDataSource = 1102,
        TwainOpenDsFailed = 1103,
        TwainCapabilityFailed = 1104,
        TwainEnableFailed = 1105,
        TwainTransferFailed = 1106,
        TwainCancelled = 1107,
        TwainPaperJam = 1108,
        TwainFeederEmpty = 1109,
        TwainCoverOpen = 1110,
        TwainDeviceOffline = 1111,
        TwainSequenceError = 1112,
        TwainLowMemory = 1113,
        TwainBadValue = 1114,

        WiaDevMgrFailed = 1300,
        WiaDeviceNotFound = 1301,
        WiaCreateItemFailed = 1302,
        WiaPropertyFailed = 1303,
        WiaTransferFailed = 1304,
        WiaPaperJam = 1305,
        WiaFeederEmpty = 1306,
        WiaCoverOpen = 1307,
        WiaOffline = 1308,
        WiaBusy = 1309,
        WiaCancelled = 1310,

        NetDiscoveryFailed = 1500,
        NetHttpError = 1501,
        NetEsclJobFailed = 1502,

        ImagingAllocFailed = 1700,
        ImagingUnsupportedFormat = 1701,

        OutputWriteFailed = 2000,

        Unknown = 9999
    }

    /// <summary>Errors cross the process boundary as data, never as exceptions.</summary>
    public class NsResult
    {
        public bool Ok;
        public NsError Code = NsError.None;
        /// <summary>Transport's own condition code, verbatim, for diagnostics.</summary>
        public string DeviceCode = "";
        public string Message = "";
        /// <summary>What the user can actually do about it.</summary>
        public string Remedy = "";

        public static NsResult Success() { return new NsResult { Ok = true }; }

        public static NsResult Fail(NsError code, string message, string remedy)
        {
            return new NsResult { Ok = false, Code = code, Message = message, Remedy = remedy ?? "" };
        }

        public JsonObj ToJson()
        {
            return new JsonObj()
                .Set("ok", Ok)
                .Set("code", (int)Code)
                .Set("codeName", Code.ToString())
                .Set("deviceCode", DeviceCode)
                .Set("message", Message)
                .Set("remedy", Remedy);
        }

        public static NsResult FromJson(JsonObj o)
        {
            NsResult r = new NsResult();
            if (o == null) { r.Ok = false; r.Code = NsError.Unknown; r.Message = "no response"; return r; }
            r.Ok = o.Bool("ok", false);
            r.Code = (NsError)o.Int("code", 0);
            r.DeviceCode = o.Str("deviceCode", "");
            r.Message = o.Str("message", "");
            r.Remedy = o.Str("remedy", "");
            return r;
        }

        public override string ToString()
        {
            if (Ok) return "OK";
            return Code + ": " + Message + (Remedy.Length > 0 ? " -- " + Remedy : "");
        }
    }
}
