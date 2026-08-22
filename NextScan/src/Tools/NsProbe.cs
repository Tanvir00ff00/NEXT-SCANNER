// =============================================================================
// NextScan Studio - nsprobe: command line diagnostics and scan tool
// Plan ref: MASTER_PLAN section 12.3 (automation surface) and 13.8 (diagnostics).
//
//   nsprobe list
//   nsprobe caps  "<device name>"
//   nsprobe scan  "<device name>" [--dpi 300] [--mode Color24] [--source Flatbed]
//                 [--region L,T,W,H] [--pages 1] [--out file.png] [--transport twain|wia]
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using NextScan.Core;
using ColorMode = NextScan.Core.ColorMode;   // disambiguate from System.Drawing.Imaging.ColorMode

namespace NextScan.Tools
{
    public static class NsProbe
    {
        [STAThread]
        public static int Main(string[] args)
        {
            DeviceBroker broker = new DeviceBroker();
            bool verbose = HasFlag(args, "--verbose");
            if (verbose) broker.Log = delegate (string m) { Console.Error.WriteLine("  . " + m); };

            string cmd = (args.Length > 0) ? args[0].ToLowerInvariant() : "list";

            switch (cmd)
            {
                case "list": return CmdList(broker);
                case "caps": return CmdCaps(broker, args);
                case "scan": return CmdScan(broker, args);
                default:
                    Console.WriteLine("usage: nsprobe list | caps <device> | scan <device> [options]");
                    return 2;
            }
        }

        static int CmdList(DeviceBroker broker)
        {
            Console.WriteLine("Host directory: " + broker.HostDirectory);
            Console.WriteLine("Searching for scanners...");
            Console.WriteLine();

            List<ScannerEntry> scanners = broker.Probe();
            if (scanners.Count == 0)
            {
                Console.WriteLine("No scanners found.");
                Console.WriteLine();
                Console.WriteLine("Things worth checking:");
                Console.WriteLine("  - the scanner is powered on and connected");
                Console.WriteLine("  - its driver is installed (TWAIN or WIA)");
                Console.WriteLine("  - no other scanning program is holding the device open");
                return 1;
            }

            foreach (ScannerEntry e in scanners)
            {
                Console.WriteLine("  " + e.DisplayName);
                foreach (DeviceDescriptor d in e.Connections)
                {
                    string star = ReferenceEquals(d, e.Preferred) ? " *" : "  ";
                    Console.WriteLine("   " + star + " " + d.Transport.ToString().PadRight(6) +
                                      " " + d.HostBitness + "-bit   " + d.NativeId);
                }
                Console.WriteLine();
            }
            Console.WriteLine("(* = connection NextScan would choose)");
            return 0;
        }

        static int CmdCaps(DeviceBroker broker, string[] args)
        {
            DeviceDescriptor dev = Resolve(broker, args);
            if (dev == null) return 1;

            Console.WriteLine("Querying " + dev.FriendlyName + " over " + dev.Transport + " (" + dev.HostBitness + "-bit host)...");
            DeviceCapabilities caps;
            NsResult r = broker.GetCapabilities(dev, out caps);
            if (!r.Ok) { Console.WriteLine("FAILED: " + r); return 1; }

            Console.WriteLine();
            Console.WriteLine("  Resolutions   : " + string.Join(", ", caps.Resolutions.ConvertAll(delegate (int i) { return i.ToString(CultureInfo.InvariantCulture); }).ToArray()));
            Console.WriteLine("  Range         : " + caps.MinResolution + " - " + caps.MaxResolution + (caps.ResolutionIsRange ? " (continuous)" : ""));
            Console.WriteLine("  Colour modes  : " + string.Join(", ", caps.ColorModes.ConvertAll(delegate (ColorMode c) { return c.ToString(); }).ToArray()));
            Console.WriteLine("  Sources       : " + string.Join(", ", caps.Sources.ConvertAll(delegate (PaperSource s) { return s.ToString(); }).ToArray()));
            Console.WriteLine("  Bed size      : " + caps.PhysicalWidthIn.ToString("0.##", CultureInfo.InvariantCulture) + " x " +
                                                    caps.PhysicalHeightIn.ToString("0.##", CultureInfo.InvariantCulture) + " in");
            Console.WriteLine("  Duplex        : " + caps.SupportsDuplex);
            Console.WriteLine("  Feeder        : " + caps.SupportsFeeder);
            Console.WriteLine("  Film          : " + caps.SupportsFilm);
            Console.WriteLine("  Device deskew : " + caps.SupportsAutoDeskew);
            Console.WriteLine("  Device crop   : " + caps.SupportsAutoBorderDetect);
            Console.WriteLine("  Hidden UI     : " + caps.SupportsHiddenUi);
            Console.WriteLine();
            Console.WriteLine("  Raw capability log:");
            foreach (string s in caps.RawCapabilityLog) Console.WriteLine("    " + s);
            return 0;
        }

        static int CmdScan(DeviceBroker broker, string[] args)
        {
            DeviceDescriptor dev = Resolve(broker, args);
            if (dev == null) return 1;

            ScanSettings s = new ScanSettings();
            s.Dpi = GetInt(args, "--dpi", 300);
            s.PageCount = GetInt(args, "--pages", 1);
            try { s.Mode = (ColorMode)Enum.Parse(typeof(ColorMode), GetStr(args, "--mode", "Color24"), true); }
            catch { }
            try { s.Source = (PaperSource)Enum.Parse(typeof(PaperSource), GetStr(args, "--source", "Flatbed"), true); }
            catch { }

            string region = GetStr(args, "--region", "");
            if (region.Length > 0)
            {
                string[] parts = region.Split(',');
                if (parts.Length == 4)
                {
                    s.RegionLeftIn = ParseD(parts[0]);
                    s.RegionTopIn = ParseD(parts[1]);
                    s.RegionWidthIn = ParseD(parts[2]);
                    s.RegionHeightIn = ParseD(parts[3]);
                }
            }

            string outPath = GetStr(args, "--out", "");
            if (outPath.Length == 0)
                outPath = Path.Combine(Environment.CurrentDirectory,
                    "scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png");

            Console.WriteLine("Scanning on " + dev.FriendlyName + " over " + dev.Transport + " (" + dev.HostBitness + "-bit host)");
            Console.WriteLine("  " + s.Dpi + " dpi, " + s.Mode + ", " + s.Source +
                              (s.HasRegion ? ", region " + s.RegionWidthIn + "x" + s.RegionHeightIn + " in" : ", full bed"));
            Console.WriteLine();

            DateTime t0 = DateTime.UtcNow;
            List<string> written = new List<string>();

            NsResult r = broker.Scan(dev, s, delegate (RawImage img)
            {
                string path = outPath;
                if (written.Count > 0)
                {
                    string dir = Path.GetDirectoryName(outPath);
                    string stem = Path.GetFileNameWithoutExtension(outPath);
                    string ext = Path.GetExtension(outPath);
                    path = Path.Combine(dir, stem + "_" + (written.Count + 1).ToString("000", CultureInfo.InvariantCulture) + ext);
                }

                Console.WriteLine("  page " + (img.PageIndex + 1) + ": " + img);
                try
                {
                    using (Bitmap bmp = img.ToBitmap())
                    {
                        if (bmp == null) { Console.WriteLine("    could not build a bitmap"); return true; }
                        bmp.Save(path, ImageFormat.Png);
                    }
                    written.Add(path);
                    Console.WriteLine("    saved " + path);
                }
                catch (Exception ex) { Console.WriteLine("    save failed: " + ex.Message); }
                return true;
            },
            delegate (string msg, int pct) { Console.WriteLine("  [" + pct + "%] " + msg); });

            double secs = (DateTime.UtcNow - t0).TotalSeconds;
            Console.WriteLine();

            if (!r.Ok)
            {
                Console.WriteLine("FAILED: " + r.Code + " - " + r.Message);
                if (r.Remedy.Length > 0) Console.WriteLine("        " + r.Remedy);
                return 1;
            }

            Console.WriteLine("Done: " + written.Count + " page(s) in " + secs.ToString("0.0", CultureInfo.InvariantCulture) + "s");
            return 0;
        }

        // ---------------------------------------------------------------- helpers
        static DeviceDescriptor Resolve(DeviceBroker broker, string[] args)
        {
            string want = (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)) ? args[1] : "";
            string wantTransport = GetStr(args, "--transport", "").ToLowerInvariant();

            List<ScannerEntry> scanners = broker.Probe();
            if (scanners.Count == 0) { Console.WriteLine("No scanners found."); return null; }

            foreach (ScannerEntry e in scanners)
            {
                if (want.Length > 0 && e.DisplayName.IndexOf(want, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (wantTransport.Length > 0)
                {
                    foreach (DeviceDescriptor d in e.Connections)
                        if (d.Transport.ToString().ToLowerInvariant() == wantTransport) return d;
                    continue;
                }
                return e.Preferred;
            }

            Console.WriteLine("No scanner matching '" + want + "'" +
                              (wantTransport.Length > 0 ? " over " + wantTransport : "") + ".");
            Console.WriteLine("Available:");
            foreach (ScannerEntry e in scanners) Console.WriteLine("  " + e.DisplayName);
            return null;
        }

        static bool HasFlag(string[] args, string name)
        {
            foreach (string a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string GetStr(string[] args, string name, string def)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return def;
        }

        static int GetInt(string[] args, string name, int def)
        {
            int v;
            return int.TryParse(GetStr(args, name, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        static double ParseD(string s)
        {
            double v;
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}
