// =============================================================================
// NextScan Studio - nscanceltest: scan cancellation regression tests
// Plan ref: MASTER_PLAN section 13.4 (Esc cancels), 7.7 (failure recovery).
//
// Cancellation cannot be tested against real hardware with any repeatability: a
// 300 dpi A4 page finishes in about ten seconds, so a hand-timed Escape either
// lands before the scan starts or after it ends. These cases drive the fake DSM
// (ADR-0002) with the "slow" personality, which delays every transfer strip, and
// cancel at a known point.
//
// Written in C# rather than PowerShell because the tests need a real background
// thread sharing one DeviceBroker instance, which a PowerShell script block
// cannot provide reliably.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using NextScan.Core;

namespace NextScan.Tools
{
    public static class NsCancelTest
    {
        static int _pass;
        static int _fail;
        static bool _verbose;

        [STAThread]
        public static int Main(string[] args)
        {
            foreach (string a in args) if (a == "--verbose") _verbose = true;

            Console.WriteLine();
            Console.WriteLine("NextScan cancellation tests");
            Console.WriteLine();

            Case("graceful_cancel", GracefulCancel);
            Case("cancel_is_prompt", CancelIsPrompt);
            Case("kill_fallback", KillFallback);
            Case("idle_cancel", IdleCancel);
            Case("scan_still_works_after", ScanStillWorksAfterCancel);

            Console.WriteLine();
            if (_fail > 0)
            {
                Console.WriteLine(_fail + " case(s) FAILED");
                return 1;
            }
            Console.WriteLine("All cancellation cases passed.");
            return 0;
        }

        // ---------------------------------------------------------------- harness
        static void Case(string name, Func<string> body)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                string note = body();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  ok   {0,-22} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, note));
                _pass++;
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  FAIL {0,-22} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, ex.Message));
                _fail++;
            }
            finally
            {
                KillStrayHosts();
            }
        }

        static string BinDir
        {
            get { return AppDomain.CurrentDomain.BaseDirectory; }
        }

        static DeviceBroker MakeBroker(string personality)
        {
            // The fake DSM is pinned through the same override the golden suite
            // uses, so these tests never touch real hardware.
            Environment.SetEnvironmentVariable("NEXTSCAN_TWAIN_DSM",
                Path.Combine(BinDir, "sim\\x86\\TWAINDSM.DLL"));
            Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PERSONALITY", personality);
            Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PATTERN", "bars");

            DeviceBroker b = new DeviceBroker();
            b.HostDirectory = BinDir;
            if (_verbose) b.Log = delegate (string m) { Console.WriteLine("      . " + m); };
            return b;
        }

        static DeviceDescriptor FindSimDevice(DeviceBroker b)
        {
            foreach (ScannerEntry e in b.Probe())
                foreach (DeviceDescriptor d in e.Connections)
                    if (d.Transport == Transport.Twain && d.HostBitness == 32) return d;

            throw new Exception("the simulated TWAIN source was not found");
        }

        static ScanSettings Settings(int dpi)
        {
            ScanSettings s = new ScanSettings();
            s.Dpi = dpi;
            s.Mode = ColorMode.Color24;
            s.Source = PaperSource.Flatbed;
            s.PageCount = 1;
            s.RegionWidthIn = 8.0;
            s.RegionHeightIn = 10.0;
            return s;
        }

        /// <summary>Starts a scan on a background thread and returns its result holder.</summary>
        class ScanRun
        {
            public NsResult Result;
            public Thread Thread;
            public int Pages;
        }

        static ScanRun StartScan(DeviceBroker b, DeviceDescriptor dev, ScanSettings s)
        {
            ScanRun run = new ScanRun();
            run.Thread = new Thread(delegate ()
            {
                run.Result = b.Scan(dev, s,
                    delegate (RawImage img) { run.Pages++; return true; }, null);
            });
            run.Thread.IsBackground = true;
            run.Thread.SetApartmentState(ApartmentState.STA);
            run.Thread.Start();
            return run;
        }

        static void WaitFor(ScanRun run, int ms)
        {
            if (!run.Thread.Join(ms)) throw new Exception("the scan thread did not finish within " + ms + " ms");
        }

        static int HostCount()
        {
            int n = 0;
            try { n += Process.GetProcessesByName("NextScan.Host32").Length; } catch { }
            try { n += Process.GetProcessesByName("NextScan.Host64").Length; } catch { }
            return n;
        }

        static void KillStrayHosts()
        {
            foreach (string name in new string[] { "NextScan.Host32", "NextScan.Host64" })
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try { p.Kill(); } catch { }
                    }
                }
                catch { }
            }
        }

        /// <summary>Waits until a scan is actually under way, or gives up.</summary>
        static void WaitUntilScanning(DeviceBroker b, ScanRun run, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (b.ScanInProgress && HostCount() > 0) return;

                // If the scan already finished it never became cancellable, and
                // the reason it gave is far more useful than "nothing was
                // running" - which is what this check used to report.
                if (!run.Thread.IsAlive)
                {
                    throw new Exception("the scan ended before it could be cancelled: " +
                        (run.Result == null ? "no result returned" : run.Result.ToString()));
                }
                Thread.Sleep(100);
            }
            throw new Exception("no scan was running after " + ms + " ms");
        }

        // ---------------------------------------------------------------- cases
        static string GracefulCancel()
        {
            DeviceBroker b = MakeBroker("slow");
            DeviceDescriptor dev = FindSimDevice(b);

            ScanRun run = StartScan(b, dev, Settings(200));
            WaitUntilScanning(b, run, 12000);
            Thread.Sleep(800);          // let a strip or two go by

            if (!b.CancelScan()) throw new Exception("CancelScan reported nothing to cancel");
            WaitFor(run, 20000);

            if (HostCount() > 0) throw new Exception("a host process survived the cancel");
            if (run.Result == null) throw new Exception("the scan returned no result");
            if (run.Result.Code != NsError.TwainCancelled)
                throw new Exception("expected TwainCancelled, got " + run.Result.Code);

            return "reported " + run.Result.Code;
        }

        static string CancelIsPrompt()
        {
            DeviceBroker b = MakeBroker("slow");
            DeviceDescriptor dev = FindSimDevice(b);

            ScanRun run = StartScan(b, dev, Settings(200));
            WaitUntilScanning(b, run, 12000);
            Thread.Sleep(800);

            Stopwatch sw = Stopwatch.StartNew();
            b.CancelScan();
            double took = sw.Elapsed.TotalSeconds;
            WaitFor(run, 20000);

            // The signal is polled between strips, so stopping costs about one
            // strip. Anything near the 4 s grace period means the host had to be
            // killed instead of stopping on its own, which would leave the data
            // source open on real hardware.
            if (took >= 3.5)
                throw new Exception(string.Format(CultureInfo.InvariantCulture,
                    "cancel took {0:N1}s - the host was killed rather than signalled", took));

            return string.Format(CultureInfo.InvariantCulture, "stopped in {0:N2}s", took);
        }

        static string KillFallback()
        {
            // "hang" never returns from MSG_ENABLEDS, so the cancel poll is never
            // reached. The parent must still get rid of the host.
            DeviceBroker b = MakeBroker("hang");
            DeviceDescriptor dev = FindSimDevice(b);

            ScanRun run = StartScan(b, dev, Settings(200));
            WaitUntilScanning(b, run, 12000);
            Thread.Sleep(1500);

            Stopwatch sw = Stopwatch.StartNew();
            b.CancelScan();
            double took = sw.Elapsed.TotalSeconds;
            WaitFor(run, 30000);

            if (HostCount() > 0) throw new Exception("the wedged host survived the cancel");

            return string.Format(CultureInfo.InvariantCulture, "killed after {0:N1}s of grace", took);
        }

        static string IdleCancel()
        {
            DeviceBroker b = MakeBroker("wellbehaved");
            if (b.ScanInProgress) throw new Exception("a scan was reported while idle");
            if (b.CancelScan()) throw new Exception("CancelScan claimed to cancel something while idle");
            return "no-op when idle";
        }

        static string ScanStillWorksAfterCancel()
        {
            // The point of stopping through the state machine rather than by
            // killing: the next scan must succeed immediately, with no lingering
            // MAXCONNECTIONS from a data source left open.
            DeviceBroker b = MakeBroker("slow");
            DeviceDescriptor dev = FindSimDevice(b);

            ScanRun first = StartScan(b, dev, Settings(200));
            WaitUntilScanning(b, first, 12000);
            Thread.Sleep(800);
            b.CancelScan();
            WaitFor(first, 20000);

            Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PERSONALITY", "wellbehaved");
            ScanRun second = StartScan(b, dev, Settings(100));
            WaitFor(second, 60000);

            if (second.Result == null || !second.Result.Ok)
                throw new Exception("the scan after a cancel failed: " +
                    (second.Result == null ? "no result" : second.Result.ToString()));
            if (second.Pages < 1) throw new Exception("the scan after a cancel produced no page");

            return "next scan delivered " + second.Pages + " page(s)";
        }
    }
}
