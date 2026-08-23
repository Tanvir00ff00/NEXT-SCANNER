// =============================================================================
// NextScan Studio - Device broker (parent side of the host protocol)
// Plan ref: MASTER_PLAN section 5.1, 6.2, 7.1.
//
// Spawns and supervises the two host processes, merges their device lists, and
// reads acquired frames out of shared memory. This is the only place in the UI
// process that knows a scanner exists - and it never loads a vendor driver.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace NextScan.Core
{
    /// <summary>One physical scanner, with every way we can reach it.</summary>
    public class ScannerEntry
    {
        public string DisplayName = "";
        public readonly List<DeviceDescriptor> Connections = new List<DeviceDescriptor>();

        /// <summary>
        /// Best available connection. TWAIN first (richest capabilities), then eSCL,
        /// then WIA, then WSD - the ranking from plan section 6.2.
        /// </summary>
        public DeviceDescriptor Preferred
        {
            get
            {
                DeviceDescriptor best = null;
                int bestRank = int.MaxValue;
                foreach (DeviceDescriptor d in Connections)
                {
                    int rank;
                    switch (d.Transport)
                    {
                        case Transport.Twain: rank = 0; break;
                        case Transport.Escl: rank = 1; break;
                        case Transport.Wia: rank = 2; break;
                        case Transport.Wsd: rank = 3; break;
                        default: rank = 9; break;
                    }
                    if (rank < bestRank) { bestRank = rank; best = d; }
                }
                return best ?? (Connections.Count > 0 ? Connections[0] : null);
            }
        }

        public override string ToString() { return DisplayName; }
    }

    public class DeviceBroker
    {
        public Action<string> Log = delegate { };

        /// <summary>Directory holding NextScan.Host32.exe / NextScan.Host64.exe.</summary>
        public string HostDirectory;

        /// <summary>
        /// Host processes this broker spawned. Hosts are one-shot by design, so any
        /// NextScan.Host32/64 process that this broker did NOT spawn is a leftover
        /// from a previous (crashed, killed, or orphaned) run - and a leftover that
        /// still holds a TWAIN data source open keeps the scanner locked for every
        /// later attempt, which in the field looks like "scanner in use by another
        /// program" until a reboot. Children are also killed when THIS process
        /// exits, because Windows does not reap child processes on parent death.
        /// </summary>
        static readonly List<int> ChildPids = new List<int>();
        static readonly object ChildLock = new object();
        static bool _exitHookRegistered;

        public DeviceBroker()
        {
            HostDirectory = ResolveHostDirectory();
            RegisterExitHook();
        }

        static void RegisterExitHook()
        {
            lock (ChildLock)
            {
                if (_exitHookRegistered) return;
                _exitHookRegistered = true;
            }
            AppDomain.CurrentDomain.ProcessExit += delegate { KillChildren("process exit"); };
        }

        static void TrackChild(Process p)
        {
            lock (ChildLock) { ChildPids.Add(p.Id); }
        }

        static void UntrackChild(Process p)
        {
            lock (ChildLock) { ChildPids.Remove(p.Id); }
        }

        static void KillChildren(string reason)
        {
            int[] pids;
            lock (ChildLock) { pids = ChildPids.ToArray(); ChildPids.Clear(); }
            foreach (int pid in pids)
            {
                try
                {
                    Process p = Process.GetProcessById(pid);
                    if (!p.HasExited) { p.Kill(); Log0("killed child host " + pid + " (" + reason + ")"); }
                }
                catch { }
            }
        }

        static void Log0(string msg)
        {
            // Static-context logging without needing an instance delegate.
            try { System.Diagnostics.Debug.WriteLine("[DeviceBroker] " + msg); } catch { }
        }

        /// <summary>
        /// Hosts being spawned right now. When Probe() runs its two host probes
        /// in parallel, one RunHost's stale-kill pass would race the other's
        /// spawn (a host is only tracked as OUR child after Process.Start
        /// returns) and could kill a perfectly fresh sibling. So the stale sweep
        /// only runs when no other spawn is in flight - the sequential Scan/Caps
        /// paths always qualify, and Probe() does its own sweep up front before
        /// opening the parallel window.
        /// </summary>
        static int _spawnsInFlight;

        /// <summary>
        /// Kills every NextScan.Host32/64 process this broker did not spawn itself.
        /// Called before each host run: hosts are stateless one-shot workers, so a
        /// foreign instance can only be garbage from an earlier run - usually one
        /// that was watchdog-killed mid-transfer (TerminateProcess never runs the
        /// vendor driver's CloseDS) and whose driver-side lock is exactly what
        /// blocks the next scan with TWCC_MAXCONNECTIONS.
        /// </summary>
        void KillStaleHosts()
        {
            int[] ours;
            lock (ChildLock) { ours = ChildPids.ToArray(); }

            foreach (string name in new string[] { "NextScan.Host32", "NextScan.Host64" })
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); }
                catch { continue; }
                foreach (Process p in procs)
                {
                    bool isOurs = false;
                    foreach (int pid in ours) if (pid == p.Id) { isOurs = true; break; }
                    if (isOurs) continue;

                    try
                    {
                        Log("killing stale host process " + name + " (pid " + p.Id + ") left over from an earlier run");
                        p.Kill();
                        p.WaitForExit(3000);
                    }
                    catch { }
                    finally { try { p.Dispose(); } catch { } }
                }
            }
        }

        static string ResolveHostDirectory()
        {
            List<string> candidates = new List<string>();
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                candidates.Add(baseDir);
                candidates.Add(Path.Combine(baseDir, "bin"));
                candidates.Add(Path.Combine(baseDir, "NextScan\\bin"));
            }
            catch { }
            candidates.Add(@"C:\PS_Fix\NextScan\bin");
            candidates.Add(@"C:\Program Files\NextScanner");

            foreach (string c in candidates)
            {
                try { if (File.Exists(Path.Combine(c, "NextScan.Host32.exe"))) return c; }
                catch { }
            }
            return candidates.Count > 0 ? candidates[0] : ".";
        }

        public string Host32Path { get { return Path.Combine(HostDirectory, "NextScan.Host32.exe"); } }
        public string Host64Path { get { return Path.Combine(HostDirectory, "NextScan.Host64.exe"); } }

        // ---------------------------------------------------------------- probe
        /// <summary>
        /// Enumerates every device on both hosts and groups them by physical scanner.
        /// Both bitnesses are always probed: a 32-bit-only TWAIN driver is invisible
        /// to the 64-bit host and vice versa (plan section 3.1).
        /// </summary>
        public List<ScannerEntry> Probe()
        {
            List<DeviceDescriptor> all = new List<DeviceDescriptor>();

            // Sweep for leftovers BEFORE opening the parallel window, so the two
            // host probes and the mDNS browse below never race the killer.
            KillStaleHosts();

            // All three transports probe in parallel: the two host processes
            // (~0.4 s each, serial before) and the mDNS listen window used to add
            // up to ~2 s; overlapped, the whole probe takes as long as the slowest
            // single probe (plan NFR2: full list well under the 3.5 s budget).
            System.Threading.Tasks.Task<List<DeviceDescriptor>> t32 =
                System.Threading.Tasks.Task.Run(delegate { return ProbeHost(Host32Path, 32); });
            System.Threading.Tasks.Task<List<DeviceDescriptor>> t64 =
                System.Threading.Tasks.Task.Run(delegate { return ProbeHost(Host64Path, 64); });
            System.Threading.Tasks.Task<List<DeviceDescriptor>> escl =
                System.Threading.Tasks.Task.Run(delegate
                {
                    try
                    {
                        NextScan.Net.EsclDriver esclDriver = new NextScan.Net.EsclDriver();
                        esclDriver.Log = Log;
                        return esclDriver.Probe();
                    }
                    catch (Exception ex) { Log("eSCL probe failed: " + ex.Message); return new List<DeviceDescriptor>(); }
                });

            try { all.AddRange(t32.Result); } catch (Exception ex) { Log("host32 probe failed: " + ex.Message); }
            try { all.AddRange(t64.Result); } catch (Exception ex) { Log("host64 probe failed: " + ex.Message); }
            try { all.AddRange(escl.Result); } catch (Exception ex) { Log("eSCL probe failed: " + ex.Message); }

            return GroupByScanner(all);
        }

        List<DeviceDescriptor> ProbeHost(string exe, int bitness)
        {
            List<DeviceDescriptor> found = new List<DeviceDescriptor>();
            if (!File.Exists(exe)) { Log("host missing: " + exe); return found; }

            HostRun run = RunHost(exe, "probe", 45000, null);
            foreach (JsonObj o in run.Messages)
            {
                if (o.Str("type", "") != "device") continue;
                DeviceDescriptor d = DeviceDescriptor.FromJson(o);
                d.HostBitness = bitness;
                found.Add(d);
            }
            Log("host" + bitness + " reported " + found.Count + " device(s)");
            return found;
        }

        /// <summary>
        /// Collapses the same physical scanner reached over several transports into
        /// one entry, so the user sees one card per device rather than four.
        /// </summary>
        static List<ScannerEntry> GroupByScanner(List<DeviceDescriptor> all)
        {
            List<ScannerEntry> entries = new List<ScannerEntry>();

            foreach (DeviceDescriptor d in all)
            {
                string key = NormalizeName(d.FriendlyName);
                ScannerEntry match = null;
                foreach (ScannerEntry e in entries)
                {
                    if (NormalizeName(e.DisplayName) == key) { match = e; break; }
                }
                if (match == null)
                {
                    match = new ScannerEntry();
                    match.DisplayName = d.FriendlyName;
                    entries.Add(match);
                }
                match.Connections.Add(d);
            }
            return entries;
        }

        static string NormalizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // ---------------------------------------------------------------- capabilities
        public NsResult GetCapabilities(DeviceDescriptor device, out DeviceCapabilities caps)
        {
            caps = new DeviceCapabilities();
            if (device == null)
                return NsResult.Fail(NsError.HostProtocolViolation, "No device selected.", "");

            // eSCL runs in-process (pure managed, plan 5.1).
            if (device.Transport == Transport.Escl)
            {
                NextScan.Net.EsclDriver escl = new NextScan.Net.EsclDriver();
                escl.Log = Log;
                return escl.GetCapabilities(device.NativeId, out caps);
            }

            string exe = (device.HostBitness == 32) ? Host32Path : Host64Path;
            string args = "caps --device \"" + Escape(device.NativeId) + "\" --transport " + device.Transport.ToString().ToLowerInvariant();

            HostRun run = RunHost(exe, args, 60000, null);
            foreach (JsonObj o in run.Messages)
                if (o.Str("type", "") == "caps") caps = DeviceCapabilities.FromJson(o);

            return run.Result;
        }

        // ---------------------------------------------------------------- scan
        /// <summary>
        /// Runs an acquisition. onFrame is called per page on a background thread.
        /// Return false from it to stop the batch.
        /// </summary>
        public NsResult Scan(DeviceDescriptor device, ScanSettings settings,
                             Func<RawImage, bool> onFrame, Action<string, int> onProgress)
        {
            if (device == null)
                return NsResult.Fail(NsError.HostProtocolViolation, "No device selected.", "");

            // eSCL runs in-process; no host spawn, no shared-memory hop.
            if (device.Transport == Transport.Escl)
            {
                NextScan.Net.EsclDriver escl = new NextScan.Net.EsclDriver();
                escl.Log = Log;
                return escl.Scan(device.NativeId, settings, onFrame);
            }

            string exe = (device.HostBitness == 32) ? Host32Path : Host64Path;
            if (!File.Exists(exe))
                return NsResult.Fail(NsError.HostSpawnFailed, "Scanner host program is missing: " + exe,
                                     "Reinstall NextScan Studio.");

            string json = Json.Write(settings.ToJson());
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            string args = "scan --device \"" + Escape(device.NativeId) + "\"" +
                          " --transport " + device.Transport.ToString().ToLowerInvariant() +
                          " --settings " + b64;

            int pages = 0;
            // Vendor drivers are slow and a network copier can take minutes; the host
            // has its own watchdog, this is the outer backstop.
            int timeout = settings.IsPreview ? 180000 : 600000;

            HostRun run = RunHost(exe, args, timeout, delegate (JsonObj o)
            {
                string type = o.Str("type", "");
                if (type == "frame")
                {
                    AcquiredFrame f = AcquiredFrame.FromJson(o);
                    if (onProgress != null)
                        onProgress("Receiving page " + (f.PageIndex + 1) + "...", 70);

                    RawImage img = ReadFrame(f);
                    if (img == null) { Log("could not map frame " + f.ShmName); return true; }

                    pages++;
                    if (onFrame != null)
                    {
                        try { return onFrame(img); }
                        catch (Exception ex) { Log("onFrame threw: " + ex.Message); }
                    }
                }
                else if (type == "progress" && onProgress != null)
                {
                    onProgress(o.Str("message", ""), o.Int("percent", 50));
                }
                return true;
            });

            if (run.Result.Ok && pages == 0)
                return NsResult.Fail(NsError.HostProtocolViolation,
                    "The scanner host finished without delivering a page.", "Try scanning again.");

            // Device-wedge recovery (field incident: Canon carriage stuck at the
            // bottom, ScanGear Code 2,250,4, cable re-plug was the only cure).
            // On the failure codes a wedged device produces, power-cycle the USB
            // node through the elevated helper task and try ONCE more. Disable
            // with NEXTSCAN_USB_RESET=0.
            if (!run.Result.Ok && LooksLikeDeviceWedge(run.Result) && UsbResetEnabled())
            {
                Log("scan failed with " + run.Result.Code + " - attempting USB device reset");
                UsbReset.Log = Log;
                if (UsbReset.TryReset(device.FriendlyName))
                {
                    // Field evidence (2026-08-23, LiDE 400): the USB node is back
                    // after ~2s but the Canon driver needs several more seconds to
                    // finish re-initialising - a retry fired immediately failed in
                    // under a second. Give it a settle window first.
                    Log("device reset succeeded - settling 8s, then retrying the scan once");
                    Thread.Sleep(8000);
                    NsResult retry = Scan(device, settings, onFrame, onProgress);
                    if (retry.Ok) return retry;
                    Log("scan still failing after reset: " + retry.Message);
                    return retry;
                }
                Log("USB reset could not run or the device did not return");
                run.Result = NsResult.Fail(run.Result.Code,
                    run.Result.Message,
                    "The scanner stopped responding and automatic recovery did not succeed. " +
                    "Unplug the scanner's cable, wait a moment, and reconnect it, then try again.");
            }

            return run.Result;
        }

        /// <summary>
        /// The failure codes a physically wedged scanner produces: the device
        /// refuses to open, refuses to enable, dies mid-transfer, or WIA reports
        /// it offline. Deliberately narrow - a paper jam or a user cancel must
        /// NOT trigger a USB power-cycle.
        /// </summary>
        static bool LooksLikeDeviceWedge(NsResult r)
        {
            switch (r.Code)
            {
                case NsError.TwainOpenDsFailed:
                case NsError.TwainEnableFailed:
                case NsError.TwainTransferFailed:
                case NsError.WiaOffline:
                case NsError.WiaTransferFailed:
                    return true;
                default:
                    return false;
            }
        }

        static bool UsbResetEnabled()
        {
            try
            {
                return Environment.GetEnvironmentVariable("NEXTSCAN_USB_RESET") != "0";
            }
            catch { return true; }
        }

        /// <summary>Maps a published frame out of shared memory into a RawImage.</summary>
        RawImage ReadFrame(AcquiredFrame f)
        {
            if (string.IsNullOrEmpty(f.ShmName)) return null;
            try
            {
                using (MemoryMappedFile mmf = MemoryMappedFile.OpenExisting(f.ShmName))
                using (MemoryMappedViewAccessor view = mmf.CreateViewAccessor(0, f.ShmSize, MemoryMappedFileAccess.Read))
                {
                    byte m0 = view.ReadByte(0), m1 = view.ReadByte(1), m2 = view.ReadByte(2), m3 = view.ReadByte(3);
                    if (m0 != 'N' || m1 != 'S' || m2 != 'F' || m3 != '1')
                    {
                        Log("frame " + f.ShmName + " has a bad magic header");
                        return null;
                    }

                    int pixelOffset = view.ReadInt32(76);
                    long pixelLength = view.ReadInt64(80);
                    if (pixelLength <= 0 || pixelLength > int.MaxValue) return null;

                    RawImage img = new RawImage();
                    img.Width = f.Width;
                    img.Height = f.Height;
                    img.Stride = f.Stride;
                    img.Channels = f.Channels;
                    img.BitsPerChannel = f.BitsPerChannel;
                    img.XDpi = f.XDpi;
                    img.YDpi = f.YDpi;
                    img.PageIndex = f.PageIndex;
                    img.Side = f.Side;
                    img.Pixels = new byte[pixelLength];
                    view.ReadArray(pixelOffset, img.Pixels, 0, (int)pixelLength);
                    return img;
                }
            }
            catch (Exception ex)
            {
                Log("ReadFrame(" + f.ShmName + ") failed: " + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------- host runner
        class HostRun
        {
            public readonly List<JsonObj> Messages = new List<JsonObj>();
            public NsResult Result = NsResult.Fail(NsError.HostPipeBroken, "The scanner host did not respond.", "");
            public int ExitCode = -1;
            /// <summary>
            /// True once the host emitted a "result" line. Without this flag a
            /// crashed host is indistinguishable from a silent one: the crash leaves
            /// the sentinel Result (Ok=false) in place, which used to make the
            /// HostCrashed branch below unreachable. The simulator's crash7
            /// personality (ADR-0002) is the regression test for exactly this.
            /// </summary>
            public bool SawResult;
        }

        /// <summary>
        /// Runs a host command, streaming its NDJSON stdout.
        ///
        /// stdout and stderr are BOTH read asynchronously. Reading one to completion
        /// before the other deadlocks as soon as a chatty driver fills the 4 KB pipe
        /// buffer on the channel we are not reading - a failure this codebase has hit
        /// before, and one that presents as a scanner that "randomly hangs".
        /// </summary>
        HostRun RunHost(string exe, string args, int timeoutMs, Func<JsonObj, bool> onMessage)
        {
            HostRun run = new HostRun();
            if (!File.Exists(exe))
            {
                run.Result = NsResult.Fail(NsError.HostSpawnFailed, "Scanner host program is missing: " + exe,
                                           "Reinstall NextScan Studio.");
                return run;
            }

            // A leftover host from an earlier run may still hold the driver; clear
            // it before spawning so this run cannot lose the race for the device.
            // The FIRST spawn of a wave does the sweep WHILE HOLDING THE LOCK:
            // releasing between "decide to sweep" and "sweep done" let a sibling
            // thread spawn + get killed by the still-running sweep (found as a
            // flaky 1-in-5 missing device on the parallel probe, 2026-08-23).
            lock (ChildLock)
            {
                _spawnsInFlight++;
                if (_spawnsInFlight == 1) KillStaleHosts();
            }
            try
            {
            Process p = null;
            bool cancelled = false;
            StringBuilder stderr = new StringBuilder();

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.WorkingDirectory = HostDirectory;

                p = new Process();
                p.StartInfo = psi;

                p.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null || e.Data.Length == 0) return;
                    JsonObj o;
                    try { o = Json.Parse(e.Data); }
                    catch { Log("host emitted unparseable line: " + e.Data); return; }

                    string type = o.Str("type", "");
                    if (type == "log") { Log("[host] " + o.Str("message", "")); return; }

                    lock (run.Messages) { run.Messages.Add(o); }

                    if (type == "result") { run.Result = NsResult.FromJson(o); run.SawResult = true; }

                    if (onMessage != null)
                    {
                        bool keepGoing;
                        try { keepGoing = onMessage(o); }
                        catch (Exception ex) { Log("message handler threw: " + ex.Message); keepGoing = true; }
                        if (!keepGoing) { cancelled = true; TryKill(p); }
                    }
                };

                p.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) { lock (stderr) { stderr.AppendLine(e.Data); } }
                };

                p.Start();
                TrackChild(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutMs))
                {
                    Log("host timed out after " + (timeoutMs / 1000) + "s: " + exe + " " + args);
                    TryKill(p);
                    run.Result = NsResult.Fail(NsError.HostTimeout,
                        "The scanner stopped responding after " + (timeoutMs / 1000) + " seconds.",
                        "Check the scanner is powered on and not showing an error, then try again.");
                    return run;
                }

                // WaitForExit(int) does not guarantee the async readers have drained;
                // the parameterless overload does.
                p.WaitForExit();
                run.ExitCode = p.ExitCode;

                string err;
                lock (stderr) { err = stderr.ToString().Trim(); }
                if (err.Length > 0) Log("[host stderr] " + err);

                if (cancelled)
                {
                    run.Result = NsResult.Success();
                }
                else if (run.ExitCode != 0 && (run.Result.Ok || !run.SawResult))
                {
                    // Non-zero exit with no reported failure means the host died
                    // rather than returning - almost always a vendor driver fault,
                    // which is exactly what running out-of-process protects us from.
                    run.Result = NsResult.Fail(NsError.HostCrashed,
                        "The scanner driver stopped unexpectedly (exit code " + run.ExitCode + ").",
                        "The application is unaffected. Try scanning again, or use a different connection for this scanner.");
                }
                return run;
            }
            catch (Exception ex)
            {
                run.Result = NsResult.Fail(NsError.HostSpawnFailed, "Could not start the scanner host: " + ex.Message, "");
                return run;
            }
            finally
            {
                if (p != null)
                {
                    UntrackChild(p);
                    try { p.Dispose(); } catch { }
                }
            }
            }
            finally
            {
                lock (ChildLock) { _spawnsInFlight--; }
            }
        }

        static void TryKill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(); }
            catch { }
        }

        static string Escape(string s)
        {
            return (s ?? "").Replace("\"", "\\\"");
        }
    }
}
