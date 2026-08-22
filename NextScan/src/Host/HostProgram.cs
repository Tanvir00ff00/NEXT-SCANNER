// =============================================================================
// NextScan Studio - Device host process (NextScan.Host32.exe / Host64.exe)
// Plan ref: MASTER_PLAN section 5.1, 7.1.
//
// This is the ONLY process that ever loads a vendor scanner driver. It exists
// twice, once per bitness, because there is no 32/64-bit TWAIN bridge (plan
// section 3.1) - on a machine whose scanner ships a 32-bit-only data source, the
// x86 build here is the only way to reach the hardware at all.
//
// Crash isolation is the second benefit: if a driver faults, this process dies
// and the UI reports it. The UI never faults.
//
// PROTOCOL (ADR-0001): the control channel is newline-delimited JSON on stdout;
// pixel data goes through shared memory. The plan specifies named pipes; stdout
// gives identical isolation with materially less machinery, and the parent
// already has to supervise the process lifetime either way. Revisit if bi-
// directional mid-scan commands (pause / change settings) are ever needed.
//
//   probe
//   caps  --device <id> --transport twain|wia
//   scan  --device <id> --transport twain|wia --settings <base64 json>
//
// Emits: {"type":"log"|"device"|"caps"|"frame"|"progress"|"result", ...}
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using NextScan.Core;
using NextScan.Twain;
using NextScan.Wia;

namespace NextScan.Host
{
    public static class HostProgram
    {
        static readonly object OutLock = new object();
        static bool _verbose;
        static readonly List<MemoryMappedFile> KeepAlive = new List<MemoryMappedFile>();

        [STAThread]
        public static int Main(string[] args)
        {
            // A TWAIN data source parents dialogs to our window and posts messages to
            // this thread, so it must be STA with a live message queue.
            Application.EnableVisualStyles();

            try
            {
                Dictionary<string, string> a = ParseArgs(args);
                _verbose = a.ContainsKey("verbose");

                string cmd = a.ContainsKey("_cmd") ? a["_cmd"] : "";
                switch (cmd)
                {
                    case "probe": return CmdProbe();
                    case "caps": return CmdCaps(a);
                    case "scan": return CmdScan(a);
                    case "":
                        Emit(new JsonObj().Set("type", "result").Set("ok", false)
                            .Set("message", "no command given")
                            .Set("usage", "probe | caps --device <id> --transport <twain|wia> | scan --device <id> --transport <t> --settings <base64>"));
                        return 2;
                    default:
                        Emit(new JsonObj().Set("type", "result").Set("ok", false).Set("message", "unknown command '" + cmd + "'"));
                        return 2;
                }
            }
            catch (Exception ex)
            {
                // Never let an exception escape: the parent distinguishes "reported a
                // failure" from "died" by exit code, and a stack trace on stderr is
                // far more useful than a Windows crash dialog on a headless host.
                Emit(new JsonObj().Set("type", "result").Set("ok", false)
                    .Set("code", (int)NsError.Unknown)
                    .Set("message", ex.GetType().Name + ": " + ex.Message));
                Console.Error.WriteLine(ex.ToString());
                return 1;
            }
        }

        // ---------------------------------------------------------------- commands
        static int CmdProbe()
        {
            List<DeviceDescriptor> all = new List<DeviceDescriptor>();

            // Each transport is probed independently: a broken TWAIN installation
            // must not hide the WIA devices, which is exactly the failure that makes
            // competing products look like they "cannot see" a working scanner.
            try
            {
                TwainDriver twain = new TwainDriver();
                twain.Log = LogLine;
                all.AddRange(twain.Probe());
            }
            catch (Exception ex) { LogLine("TWAIN probe failed: " + ex.Message); }

            try
            {
                WiaDriver wia = new WiaDriver();
                wia.Log = LogLine;
                all.AddRange(wia.Probe());
            }
            catch (Exception ex) { LogLine("WIA probe failed: " + ex.Message); }

            foreach (DeviceDescriptor d in all)
            {
                JsonObj o = d.ToJson();
                o["type"] = "device";
                Emit(o);
            }

            Emit(new JsonObj().Set("type", "result").Set("ok", true).Set("count", all.Count));
            return 0;
        }

        static int CmdCaps(Dictionary<string, string> a)
        {
            string device = Get(a, "device", "");
            string transport = Get(a, "transport", "twain").ToLowerInvariant();

            DeviceCapabilities caps;
            NsResult r;

            if (transport == "wia")
            {
                WiaDriver wia = new WiaDriver();
                wia.Log = LogLine;
                r = wia.GetCapabilities(device, out caps);
            }
            else
            {
                TwainDriver twain = new TwainDriver();
                twain.Log = LogLine;
                r = twain.GetCapabilities(device, out caps);
            }

            if (r.Ok)
            {
                JsonObj o = caps.ToJson();
                o["type"] = "caps";
                Emit(o);
            }

            EmitResult(r);
            return r.Ok ? 0 : 1;
        }

        static int CmdScan(Dictionary<string, string> a)
        {
            string device = Get(a, "device", "");
            string transport = Get(a, "transport", "twain").ToLowerInvariant();
            string settingsB64 = Get(a, "settings", "");

            ScanSettings settings = new ScanSettings();
            if (settingsB64.Length > 0)
            {
                try
                {
                    string json = Encoding.UTF8.GetString(Convert.FromBase64String(settingsB64));
                    settings = ScanSettings.FromJson(Json.Parse(json));
                }
                catch (Exception ex)
                {
                    EmitResult(NsResult.Fail(NsError.HostProtocolViolation, "Bad settings payload: " + ex.Message, ""));
                    return 2;
                }
            }

            int pageCounter = 0;
            Func<RawImage, bool> onImage = delegate (RawImage img)
            {
                try
                {
                    AcquiredFrame frame = PublishFrame(img, pageCounter);
                    JsonObj o = frame.ToJson();
                    o["type"] = "frame";
                    Emit(o);
                    pageCounter++;
                    return true;
                }
                catch (Exception ex)
                {
                    LogLine("publishing page " + pageCounter + " failed: " + ex.Message);
                    return false;
                }
            };

            NsResult r;
            if (transport == "wia")
            {
                WiaDriver wia = new WiaDriver();
                wia.Log = LogLine;
                r = wia.Scan(device, settings, onImage);
            }
            else
            {
                TwainDriver twain = new TwainDriver();
                twain.Log = LogLine;
                r = twain.Scan(device, settings, onImage);
            }

            if (r.Ok && pageCounter == 0)
                r = NsResult.Fail(NsError.TwainTransferFailed, "The scanner completed without returning a page.", "Try scanning again.");

            EmitResult(r, pageCounter);

            // The parent reads our stdout asynchronously; give it a beat to drain
            // before the process (and its pipe) disappears.
            try { Console.Out.Flush(); } catch { }
            Thread.Sleep(60);
            return r.Ok ? 0 : 1;
        }

        // ---------------------------------------------------------------- frame publishing
        /// <summary>
        /// Copies a page into a named shared-memory region and returns its metadata.
        /// The layout is the FrameHeader from plan Appendix D: a fixed 128-byte
        /// header followed by tightly packed pixels.
        /// </summary>
        static AcquiredFrame PublishFrame(RawImage img, int pageIndex)
        {
            const int HeaderSize = 128;
            long pixelBytes = (long)img.Height * img.Stride;
            long total = HeaderSize + pixelBytes;

            string name = "NextScan.Frame." + Guid.NewGuid().ToString("N");
            MemoryMappedFile mmf = MemoryMappedFile.CreateNew(name, total);

            // The mapping must outlive this method: it is destroyed when the last
            // handle closes, and the parent has not opened it yet.
            KeepAlive.Add(mmf);

            using (MemoryMappedViewAccessor view = mmf.CreateViewAccessor(0, total))
            {
                view.Write(0, (byte)'N'); view.Write(1, (byte)'S'); view.Write(2, (byte)'F'); view.Write(3, (byte)'1');
                view.Write(4, (ushort)1);              // header version
                view.Write(6, (ushort)HeaderSize);
                view.Write(8, (uint)0);                // flags
                view.Write(12, img.Width);
                view.Write(16, img.Height);
                view.Write(20, img.Stride);
                view.Write(24, (ushort)img.Channels);
                view.Write(26, (ushort)img.BitsPerChannel);
                view.Write(28, (uint)(img.Channels == 1 ? 0 : 1));   // 0 = gray, 1 = BGR
                view.Write(32, (uint)Math.Round(img.XDpi * 65536.0));
                view.Write(36, (uint)Math.Round(img.YDpi * 65536.0));
                view.Write(40, pageIndex);
                view.Write(44, (ushort)img.Side);
                view.Write(76, HeaderSize);            // pixelDataOffset
                view.Write(80, pixelBytes);            // pixelDataLength

                view.WriteArray(HeaderSize, img.Pixels, 0, (int)Math.Min(pixelBytes, int.MaxValue));
            }

            AcquiredFrame f = new AcquiredFrame();
            f.ShmName = name;
            f.ShmSize = total;
            f.Width = img.Width;
            f.Height = img.Height;
            f.Stride = img.Stride;
            f.Channels = img.Channels;
            f.BitsPerChannel = img.BitsPerChannel;
            f.XDpi = img.XDpi;
            f.YDpi = img.YDpi;
            f.PageIndex = pageIndex;
            f.Side = img.Side;
            return f;
        }

        // ---------------------------------------------------------------- plumbing
        static void Emit(JsonObj o)
        {
            lock (OutLock)
            {
                Console.Out.WriteLine(Json.Write(o));
                Console.Out.Flush();
            }
        }

        static void EmitResult(NsResult r) { EmitResult(r, -1); }

        static void EmitResult(NsResult r, int pages)
        {
            JsonObj o = r.ToJson();
            o["type"] = "result";
            if (pages >= 0) o["pages"] = pages;
            o["hostBitness"] = IntPtr.Size * 8;
            Emit(o);
        }

        static void LogLine(string msg)
        {
            if (!_verbose) return;
            Emit(new JsonObj().Set("type", "log").Set("bitness", IntPtr.Size * 8).Set("message", msg));
        }

        static Dictionary<string, string> ParseArgs(string[] args)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                string s = args[i];
                if (s.StartsWith("--", StringComparison.Ordinal))
                {
                    string key = s.Substring(2);
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    { d[key] = args[++i]; }
                    else d[key] = "true";
                }
                else if (!d.ContainsKey("_cmd"))
                {
                    d["_cmd"] = s;
                }
            }
            return d;
        }

        static string Get(Dictionary<string, string> d, string key, string def)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : def;
        }
    }
}
