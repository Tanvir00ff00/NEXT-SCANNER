// =============================================================================
// NextScan Studio - eSCL transport driver (plan section 7.4)
//
// Same public surface as TwainDriver / WiaDriver, so the broker and nsprobe
// treat it identically. Pure managed and network-only, so it runs IN-PROCESS
// (plan section 5.1: the eSCL engine must be capable of running out-of-process
// behind the same interface, but isolation is not required for it).
//
// Pages are requested as image/jpeg: eSCL JPEG pages are by definition 8-bit,
// so decoding them through GDI+ violates neither the 48-bit-preservation rule
// (there is no 48-bit to preserve in a JPEG) nor ICC handling (eSCL scans are
// sRGB). Raw octet-stream transfer is the later upgrade for the rare devices
// that advertise it - its pixel layout is vendor-defined and needs a real
// device to verify against, which this LAN currently does not offer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using NextScan.Core;

namespace NextScan.Net
{
    public class EsclDriver
    {
        public Action<string> Log = delegate { };

        // Manual device override, in the spirit of NEXTSCAN_TWAIN_DSM: when set,
        // Probe() reports this URL as a device regardless of mDNS, because
        // corporate networks block multicast and the plan (7.4.1) requires a
        // manual-add path anyway.
        public static string ManualUrlEnv = "NEXTSCAN_ESCL_URL";

        // ---------------------------------------------------------------- probe
        public List<DeviceDescriptor> Probe()
        {
            List<DeviceDescriptor> devices = new List<DeviceDescriptor>();

            string manual = null;
            try { manual = Environment.GetEnvironmentVariable(ManualUrlEnv); }
            catch { }

            // mDNS costs a 1.5 s listen window on every probe; skip it entirely
            // when a manual URL is pinned (the corporate-network case it exists
            // for) so day-to-day probing stays instant.
            if (string.IsNullOrEmpty(manual))
            {
                foreach (MdnsService svc in MdnsDiscovery.Browse(1500))
                {
                    DeviceDescriptor d = new DeviceDescriptor();
                    d.Transport = Transport.Escl;
                    d.NativeId = svc.BaseUrl;
                    d.FriendlyName = string.IsNullOrEmpty(svc.Name) ? ("eSCL " + svc.Host) : svc.Name;
                    d.Manufacturer = svc.Model;
                    d.Model = svc.Model;
                    d.HostBitness = 64;      // in-process; bitness is irrelevant
                    d.IsNetwork = true;
                    d.QuirkKey = "escl:" + Norm(svc.Model) + "|" + Norm(svc.Name);
                    devices.Add(d);
                    Log("mDNS: " + svc.Name + " at " + svc.BaseUrl + " (rs=" + svc.RootPath + ")");
                }
            }

            if (!string.IsNullOrEmpty(manual))
            {
                DeviceDescriptor d = new DeviceDescriptor();
                d.Transport = Transport.Escl;
                d.NativeId = manual;
                d.FriendlyName = "eSCL Manual";
                d.HostBitness = 64;
                d.IsNetwork = true;
                d.QuirkKey = "escl:manual|" + Norm(manual);
                devices.Add(d);
                Log("manual eSCL device from " + ManualUrlEnv + ": " + manual);
            }

            return devices;
        }

        static string Norm(string s)
        {
            return (s ?? "").Trim().ToLowerInvariant();
        }

        // ---------------------------------------------------------------- capabilities
        public NsResult GetCapabilities(string baseUrl, out DeviceCapabilities caps)
        {
            caps = new DeviceCapabilities();
            try
            {
                EsclClient client = new EsclClient(baseUrl);
                client.Log = Log;
                byte[] xml = client.GetCapabilities();
                ParseCapabilities(xml, caps);
                return NsResult.Success();
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.NetHttpError,
                    "Could not read eSCL capabilities from " + baseUrl + ": " + ex.Message,
                    "Check the device is awake and reachable on the network.");
            }
        }

        /// <summary>
        /// Minimal honest parse of the capabilities document: input sources with
        /// bed geometry, colour modes from SettingProfiles, resolutions (discrete
        /// list or range), advertised formats. Anything absent stays absent -
        /// the UI greys out what the device does not offer.
        /// </summary>
        static void ParseCapabilities(byte[] xml, DeviceCapabilities caps)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(Encoding.UTF8.GetString(xml));

            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("scan", "http://schemas.hp.com/imaging/escl/2011/05/03");
            ns.AddNamespace("pwg", "http://www.pwg.org/schemas/2010/12/sm");

            // ---- input sources + bed geometry (eSCL geometry is in 1/300 inch) ----
            XmlNode platen = doc.SelectSingleNode("//scan:Platen", ns);
            caps.SupportsFlatbed = platen != null;
            if (caps.SupportsFlatbed)
            {
                caps.Sources.Add(PaperSource.Flatbed);
                caps.PhysicalWidthIn = ThreeHundredthsToInches(platen.SelectSingleNode("scan:PlatenMaxWidth", ns));
                caps.PhysicalHeightIn = ThreeHundredthsToInches(platen.SelectSingleNode("scan:PlatenMaxHeight", ns));
            }

            XmlNode adf = doc.SelectSingleNode("//scan:Adf", ns);
            if (adf != null)
            {
                caps.SupportsFeeder = true;
                caps.Sources.Add(PaperSource.Feeder);
                // AdfMaxWidth/Height fall back to the platen's when the ADF omits them.
                if (caps.PhysicalWidthIn <= 0)
                {
                    caps.PhysicalWidthIn = ThreeHundredthsToInches(adf.SelectSingleNode("scan:AdfMaxWidth", ns));
                    caps.PhysicalHeightIn = ThreeHundredthsToInches(adf.SelectSingleNode("scan:AdfMaxHeight", ns));
                }
                XmlNode duplex = adf.SelectSingleNode("scan:AdfDuplexMaxWidth", ns);
                if (duplex != null)
                {
                    caps.SupportsDuplex = true;
                    caps.Sources.Add(PaperSource.FeederDuplex);
                }
            }

            // ---- colour modes from the first SettingProfile that has them ----
            XmlNodeList profiles = doc.SelectNodes("//scan:SettingProfiles//scan:ColorMode", ns);
            foreach (XmlNode cm in profiles)
            {
                switch (cm.InnerText.Trim())
                {
                    case "RGB24": if (!caps.ColorModes.Contains(ColorMode.Color24)) caps.ColorModes.Add(ColorMode.Color24); break;
                    case "Grayscale8": if (!caps.ColorModes.Contains(ColorMode.Gray8)) caps.ColorModes.Add(ColorMode.Gray8); break;
                    case "BlackAndWhite1": if (!caps.ColorModes.Contains(ColorMode.BlackWhite1)) caps.ColorModes.Add(ColorMode.BlackWhite1); break;
                }
            }
            if (caps.ColorModes.Count == 0) caps.ColorModes.Add(ColorMode.Color24);

            // ---- resolutions: discrete list, range, or both ----
            XmlNodeList discrete = doc.SelectNodes("//scan:DiscreteResolutions//scan:DiscreteResolution", ns);
            foreach (XmlNode r in discrete)
            {
                XmlNode xr = r.SelectSingleNode("scan:XResolution", ns);
                if (xr == null) continue;
                int v;
                if (int.TryParse(xr.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) &&
                    v > 0 && !caps.Resolutions.Contains(v))
                    caps.Resolutions.Add(v);
            }
            caps.Resolutions.Sort();

            XmlNode range = doc.SelectSingleNode("//scan:ResolutionRange", ns);
            if (range != null && caps.Resolutions.Count == 0)
            {
                caps.MinResolution = IntOf(range.SelectSingleNode("scan:MinX", ns), 75);
                caps.MaxResolution = IntOf(range.SelectSingleNode("scan:MaxX", ns), 600);
                caps.ResolutionIsRange = true;
                int[] ladder = { 75, 100, 150, 200, 300, 600, 1200 };
                foreach (int v in ladder)
                    if (v >= caps.MinResolution && v <= caps.MaxResolution) caps.Resolutions.Add(v);
            }
            else
            {
                if (caps.Resolutions.Count > 0)
                {
                    caps.MinResolution = caps.Resolutions[0];
                    caps.MaxResolution = caps.Resolutions[caps.Resolutions.Count - 1];
                }
            }

            caps.RawCapabilityLog.Add("eSCL formats: " + JoinNodes(doc.SelectNodes("//scan:SettingProfiles//scan:DocumentFormat", ns)) +
                                      " / ext: " + JoinNodes(doc.SelectNodes("//scan:SettingProfiles//scan:DocumentFormatExt", ns)));
            caps.RawCapabilityLog.Add(string.Format(CultureInfo.InvariantCulture,
                "sources: flatbed={0} adf={1} duplex={2}, bed {3:0.##}x{4:0.##} in",
                caps.SupportsFlatbed, caps.SupportsFeeder, caps.SupportsDuplex,
                caps.PhysicalWidthIn, caps.PhysicalHeightIn));
        }

        static double ThreeHundredthsToInches(XmlNode n)
        {
            int v = IntOf(n, 0);
            return v / 300.0;
        }

        static int IntOf(XmlNode n, int def)
        {
            if (n == null) return def;
            int v;
            return int.TryParse(n.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static string JoinNodes(XmlNodeList nodes)
        {
            StringBuilder sb = new StringBuilder();
            foreach (XmlNode n in nodes)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(n.InnerText.Trim());
            }
            return sb.Length > 0 ? sb.ToString() : "(none)";
        }

        // ---------------------------------------------------------------- scan
        public NsResult Scan(string baseUrl, ScanSettings settings, Func<RawImage, bool> onImage)
        {
            string jobUri = null;
            try
            {
                EsclClient client = new EsclClient(baseUrl);
                client.Log = Log;

                byte[] jobXml = BuildScanSettingsXml(settings);
                Log("POST ScanJobs (" + jobXml.Length + " bytes)");
                jobUri = client.CreateJob(jobXml);
                Log("job URI (verbatim from Location): " + jobUri);

                int page = 0;
                while (true)
                {
                    byte[] doc = client.GetNextDocument(jobUri);
                    if (doc == null) { Log("NextDocument -> job complete after " + page + " page(s)"); break; }

                    RawImage img = DecodeJpeg(doc, settings.Dpi);
                    if (img == null)
                        return NsResult.Fail(NsError.NetEsclJobFailed,
                            "The device returned a page that is not a decodable JPEG.",
                            "Try a different colour mode or resolution on this device.");
                    img.PageIndex = page++;

                    bool keepGoing = true;
                    try { keepGoing = onImage(img); }
                    catch (Exception ex) { Log("onImage threw: " + ex.Message); }

                    if (!keepGoing || (settings.PageCount > 0 && page >= settings.PageCount))
                    {
                        client.DeleteJob(jobUri);
                        jobUri = null;
                        break;
                    }
                }
                if (page == 0)
                    return NsResult.Fail(NsError.NetEsclJobFailed,
                        "The device completed the job without delivering a page.",
                        "Check the document feeder is loaded, then try again.");
                return NsResult.Success();
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.NetEsclJobFailed,
                    "eSCL scan failed: " + ex.Message,
                    "Check the device is awake and not showing an error, then try again.");
            }
            finally
            {
                if (jobUri != null)
                {
                    try { new EsclClient(baseUrl).DeleteJob(jobUri); } catch { }
                }
            }
        }

        /// <summary>
        /// Builds the scan:ScanSettings body. Region geometry is in eSCL's
        /// 1/300-inch units; a missing region scans the full bed.
        /// </summary>
        static byte[] BuildScanSettingsXml(ScanSettings s)
        {
            double left = 0, top = 0, width = 8.5, height = 11.7;
            if (s.HasRegion)
            {
                left = s.RegionLeftIn;
                top = s.RegionTopIn;
                width = s.RegionWidthIn;
                height = s.RegionHeightIn;
            }

            string source = (s.Source == PaperSource.Feeder || s.Source == PaperSource.FeederDuplex) ? "Feeder" : "Platen";
            string colorMode;
            switch (s.Mode)
            {
                case ColorMode.Gray8:
                case ColorMode.Gray16: colorMode = "Grayscale8"; break;
                case ColorMode.BlackWhite1: colorMode = "BlackAndWhite1"; break;
                default: colorMode = "RGB24"; break;
            }

            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
            xml.Append("<scan:ScanSettings xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\"");
            xml.Append(" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\">\r\n");
            xml.Append("  <scan:Version>2.0</scan:Version>\r\n");
            xml.Append("  <scan:Intents>Document</scan:Intents>\r\n");
            xml.Append("  <scan:ScanRegions>\r\n");
            xml.Append("    <scan:ScanRegion units=\"escl:ThreeHundredthsOfInch\">\r\n");
            xml.Append("      <pwg:XOffset>").Append(InchesTo300(left)).Append("</pwg:XOffset>\r\n");
            xml.Append("      <pwg:YOffset>").Append(InchesTo300(top)).Append("</pwg:YOffset>\r\n");
            xml.Append("      <pwg:Width>").Append(InchesTo300(width)).Append("</pwg:Width>\r\n");
            xml.Append("      <pwg:Height>").Append(InchesTo300(height)).Append("</pwg:Height>\r\n");
            xml.Append("    </scan:ScanRegion>\r\n");
            xml.Append("  </scan:ScanRegions>\r\n");
            xml.Append("  <scan:InputSource>").Append(source).Append("</scan:InputSource>\r\n");
            xml.Append("  <scan:ColorMode>").Append(colorMode).Append("</scan:ColorMode>\r\n");
            xml.Append("  <scan:XResolution>").Append(Math.Max(1, s.Dpi)).Append("</scan:XResolution>\r\n");
            xml.Append("  <scan:YResolution>").Append(Math.Max(1, s.Dpi)).Append("</scan:YResolution>\r\n");
            xml.Append("  <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>\r\n");
            xml.Append("  <scan:Duplex>").Append((s.Duplex || s.Source == PaperSource.FeederDuplex) ? "true" : "false").Append("</scan:Duplex>\r\n");
            xml.Append("</scan:ScanSettings>\r\n");
            return Encoding.UTF8.GetBytes(xml.ToString());
        }

        static int InchesTo300(double inches)
        {
            return Math.Max(1, (int)Math.Round(inches * 300.0));
        }

        /// <summary>
        /// JPEG page bytes -> RawImage (BGR, 8-bit). LockBits performs the decode;
        /// see the file header for why GDI+ is acceptable on the eSCL path. The
        /// requested dpi is stamped by the caller because a JPEG carries no
        /// trustworthy resolution and GDI+ reports its 96-dpi screen default.
        /// </summary>
        static RawImage DecodeJpeg(byte[] doc, int dpi)
        {
            try
            {
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(new MemoryStream(doc)))
                {
                    RawImage img = RawImage.FromBitmap(bmp);
                    img.XDpi = dpi;
                    img.YDpi = dpi;
                    return img;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
