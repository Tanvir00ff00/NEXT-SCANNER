// =============================================================================
// NextScan Studio - Main application entry point & CLI handler
// Plan ref: MASTER_PLAN section 12.3, 13.1, 14.3.
//
// Boots the standalone Studio UI (NextScanner.exe) or executes headless scans
// for Photoshop automation scripts (-nodialog / -jsx).
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
    public static class StudioApp
    {
        const string AppMutexName = "NextScan.Studio.SingleInstance";

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        public static int Main(string[] args)
        {
            if (AttachConsole(-1))
            {
                try
                {
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Checked before anything is recorded anywhere. The self test is
            // the one thing a throwaway build gets run for, and recording its
            // location would point the acquire module at a scratch copy for
            // every scan afterwards.
            foreach (string a in args)
                if (string.Equals(a, "--ps-selftest", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("NextScan Photoshop frame layout");
                    Console.WriteLine();
                    return StudioPsBridge.SelfTest(Console.WriteLine);
                }

            foreach (string a in args)
                if (string.Equals(a, "--preset-selftest", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("NextScan presets: what a job carries");
                    Console.WriteLine();
                    return StudioPresets.SelfTest(Console.WriteLine);
                }

            // The acquire module starts us with a session id when the operator
            // picks File, Import, Next Scanner. Read before the shell is built,
            // so it knows from the start that a scan has somewhere to go.
            StudioPsBridge.ReadCommandLine(args);
            StudioPsBridge.RecordLocation();

            StudioSettings settings = StudioSettings.Load();

            // Every run starts neutral. The file still holds what was last used,
            // and presets are how you get a configured state back; see
            // StudioPresets.ResetJob for why remembering it was the wrong
            // default.
            StudioPresets.ResetJob(settings);

            bool noDialog = false;
            bool isJsx = false;
            string explicitOutPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "-nodialog" || a == "-silent" || a == "--silent") noDialog = true;
                else if (a == "-jsx" || a == "--jsx") isJsx = true;
                else if ((a == "-device" || a == "--device") && i + 1 < args.Length) settings.DeviceName = args[++i];
                else if ((a == "-driver" || a == "--driver") && i + 1 < args.Length)
                {
                    string drv = args[++i].ToLowerInvariant();
                    if (drv == "wia") settings.Transport = Transport.Wia;
                    else if (drv == "escl") settings.Transport = Transport.Escl;
                    else settings.Transport = Transport.Twain;
                }
                else if ((a == "-dpi" || a == "--dpi") && i + 1 < args.Length)
                {
                    int dpi;
                    if (int.TryParse(args[++i], out dpi) && dpi > 0) settings.Dpi = dpi;
                }
                else if ((a == "-mode" || a == "--mode" || a == "-colormode") && i + 1 < args.Length)
                {
                    try { settings.Mode = (ColorMode)Enum.Parse(typeof(ColorMode), args[++i], true); } catch { }
                }
                else if ((a == "-source" || a == "--source") && i + 1 < args.Length)
                {
                    try { settings.Source = (PaperSource)Enum.Parse(typeof(PaperSource), args[++i], true); } catch { }
                }
                else if ((a == "-format" || a == "--format") && i + 1 < args.Length)
                {
                    settings.OutputFormat = args[++i].ToLowerInvariant();
                }
                else if ((a == "-out" || a == "--out" || a == "-o") && i + 1 < args.Length)
                {
                    explicitOutPath = args[++i];
                }
                else if (a == "-help" || a == "--help" || a == "/?")
                {
                    PrintHelp();
                    return 0;
                }
            }

            if (noDialog)
            {
                return RunHeadlessScan(settings, explicitOutPath, isJsx);
            }

            // Launch interactive Studio GUI
            try
            {
                Application.Run(new StudioShell(settings));
                return 0;
            }
            catch (Exception ex)
            {
                string errLog = @"C:\PS_Fix\scan_log.txt";
                try { File.AppendAllText(errLog, DateTime.Now.ToString("HH:mm:ss") + " [FATAL] " + ex + "\r\n"); } catch { }
                MessageBox.Show("NextScan Studio encountered an error:\r\n" + ex.Message, "NextScan Studio",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int RunHeadlessScan(StudioSettings settings, string explicitOutPath, bool isJsx)
        {
            DeviceBroker broker = new DeviceBroker();
            List<ScannerEntry> scanners = broker.Probe();

            DeviceDescriptor target = null;
            if (!string.IsNullOrEmpty(settings.DeviceName))
            {
                foreach (ScannerEntry e in scanners)
                {
                    if (e.DisplayName.IndexOf(settings.DeviceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (DeviceDescriptor d in e.Connections)
                        {
                            if (settings.Transport != Transport.None && d.Transport == settings.Transport)
                            {
                                target = d;
                                break;
                            }
                        }
                        if (target == null) target = e.Preferred;
                        break;
                    }
                }
            }

            if (target == null && scanners.Count > 0)
            {
                target = scanners[0].Preferred;
            }

            if (target == null)
            {
                Console.Error.WriteLine("Error: No scanning device found.");
                return 1;
            }

            ScanSettings s = new ScanSettings();
            s.Dpi = settings.Dpi;
            s.Mode = settings.Mode;
            s.Source = settings.Source;
            s.PageCount = settings.PageCount;

            // Compute region in inches from bed dimensions
            DeviceCapabilities caps;
            double bedW = 8.5, bedH = 11.7;
            if (broker.GetCapabilities(target, out caps).Ok && caps.PhysicalWidthIn > 1.0)
            {
                bedW = caps.PhysicalWidthIn;
                bedH = caps.PhysicalHeightIn;
            }

            RectangleF crop = settings.CropNorm;
            if (crop.Width < 0.98f || crop.Height < 0.98f)
            {
                s.RegionLeftIn = crop.X * bedW;
                s.RegionTopIn = crop.Y * bedH;
                s.RegionWidthIn = crop.Width * bedW;
                s.RegionHeightIn = crop.Height * bedH;
            }

            List<RawImage> acquiredPages = new List<RawImage>();
            NsResult r = broker.Scan(target, s, delegate (RawImage frame)
            {
                if (frame != null && frame.IsValid) acquiredPages.Add(frame);
                return true;
            }, null);

            if (!r.Ok || acquiredPages.Count == 0)
            {
                Console.Error.WriteLine("Scan failed: " + r.Message);
                return 1;
            }

            string outPath = explicitOutPath;
            if (string.IsNullOrEmpty(outPath))
            {
                string dir = settings.OutputDirectory;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                outPath = Path.Combine(dir, "scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "." + settings.OutputFormat);
            }

            List<string> saved = StudioExport.SaveBatch(acquiredPages, outPath, settings.OutputFormat);

            if (saved.Count > 0)
            {
                if (isJsx)
                {
                    string handoffFlag = Path.Combine(Path.GetTempPath(), "nextscan_handoff.txt");
                    File.WriteAllText(handoffFlag, saved[0], Encoding.UTF8);

                    string outList = Path.Combine(@"C:\PS_Fix\tmp", "scan_output.txt");
                    File.WriteAllLines(outList, saved.ToArray(), Encoding.UTF8);
                }

                Console.WriteLine("Acquired " + saved.Count + " page(s): " + string.Join(", ", saved.ToArray()));
                return 0;
            }

            return 1;
        }

        static void PrintHelp()
        {
            Console.WriteLine("NextScan Studio (NextScanner.exe)");
            Console.WriteLine("Usage:");
            Console.WriteLine("  NextScanner [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -nodialog, --silent        Run headless scan without showing GUI");
            Console.WriteLine("  -jsx, --jsx                Signal Photoshop ExtendScript handoff on completion");
            Console.WriteLine("  -device <name>             Target scanner friendly name");
            Console.WriteLine("  -driver <twain|wia|escl>   Preferred transport");
            Console.WriteLine("  -dpi <75..1200>            Scanning resolution");
            Console.WriteLine("  -mode <Color24|Gray8|..>   Color bit-depth mode");
            Console.WriteLine("  -source <Flatbed|Feeder>   Paper source");
            Console.WriteLine("  -format <jpg|png|tif|pdf>  Output file format");
            Console.WriteLine("  -out <path>                Explicit output file path");
        }
    }
}
