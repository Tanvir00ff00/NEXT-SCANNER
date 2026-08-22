using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PS_Fix.Cdp;
using WMedia = System.Windows.Media;
using WImg = System.Windows.Media.Imaging;

public class GemServer
{
    const string ROOT    = @"C:\PS_Fix";
    static string QUEUE  = Path.Combine(ROOT, "queue");
    static string OUTD   = Path.Combine(ROOT, "out");
    static string BAK    = Path.Combine(ROOT, "backup");
    static string PROFD  = Path.Combine(ROOT, "profiles");
    static string INI    = Path.Combine(ROOT, "gem_server.ini");
    static string PROFINI= Path.Combine(ROOT, "profiles.ini");
    static string PROMPT = Path.Combine(ROOT, "gem_prompt.txt");
    static string LOG    = Path.Combine(ROOT, "gem_server_log.txt");

    static Dictionary<string, string> S = new Dictionary<string, string>();
    static object gate = new object();

    static NotifyIcon tray;
    static int served = 0, returned = 0, failed = 0;

    static CdpClient cdpClient = null;
    static JobRunner jobRunner = null;
    static int cdpPort = 0;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr h, int index, int val);
    delegate bool EnumProc(IntPtr h, IntPtr p);

    const int SW_HIDE = 0, SW_SHOW = 5, SW_SHOWNOACTIVATE = 4;
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_APPWINDOW  = 0x00040000;

    static void L(string m)
    {
        try {
            lock (gate)
                File.AppendAllText(LOG, DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n");
        } catch { }
    }

    // ================= jobs =================
    class Job
    {
        public string Id;
        public string Img;
        public string Orig;
        public bool Claimed;
        public DateTime At;
        public DateTime ClaimedAt;
        public int Tries;
    }

    static List<Job> jobs = new List<Job>();

    static int CountQueued()
    { lock (gate) { int n = 0; foreach (Job j in jobs) if (!j.Claimed) n++; return n; } }

    static int CountRunning()
    { lock (gate) { int n = 0; foreach (Job j in jobs) if (j.Claimed) n++; return n; } }

    // ================= settings =================
    static void LoadIni()
    {
        S["port"]            = "8756";
        S["site"]            = "gemini";
        S["url_aistudio"]    = "https://aistudio.google.com/prompts/new_chat?model=gemini-3.1-flash-lite-image";
        S["url_gemini"]      = "https://gemini.google.com/app";
        S["gemprompt"]       = "off";
        S["autosubmit"]      = "on";
        S["retries"]         = "2";
        S["jobttl"]          = "900";
        S["parallel"]        = "3";        // how many photos at once
        S["mode"]            = "hidden";
        S["notaskbar"]       = "on";
        S["forceoff"]        = "on";
        S["offx"]            = "-32000";
        S["offy"]            = "-32000";
        S["autolaunch"]      = "on";
        S["chrome"]          = "";
        S["idlemin"]         = "0";
        S["overlay"]         = "on";
        S["overlaypos"]      = "bottomright";
        S["replace"]         = "on";
        S["backup"]          = "on";
        S["jpegq"]           = "96";
        S["cooldownhrs"]     = "0";
        S["minresultpx"]     = "512";     // anything smaller is not a photo, never overwrite with it
        S["loose_capture"]   = "off";     // on = also accept images without the generated-image marker
        S["stucksec"]        = "100";     // give up on a wedged "creating image" after this
        S["jobgap"]          = "0";       // seconds to wait between photos, raise if Google gets suspicious
        S["puter_model"]     = "gemini-3.1-flash-image-preview";
        S["puter_quality"]   = "2K";      // 512 / 1K / 2K / 4K
        S["puter_test"]      = "off";     // on = free sample image, uses no credits
        S["puter_keepratio"] = "on";      // send the photo's own w:h so it returns the same shape
        S["puter_timeout"]   = "300";     // seconds allowed for one restore
        S["cdp_port"]        = "0";
        S["cdp_timeout"]     = "30";
        S["capture_network"] = "on";
        S["dom_fallback"]    = "on";
        S["save_debug_shots"]= "off";

        try {
            if (!File.Exists(INI)) { SaveIni(); return; }
            foreach (string raw in File.ReadAllLines(INI)) {
                string ln = raw.Trim();
                if (ln.Length == 0 || ln.StartsWith("#")) continue;
                int eq = ln.IndexOf('=');
                if (eq < 1) continue;
                S[ln.Substring(0, eq).Trim().ToLower()] = ln.Substring(eq + 1).Trim();
            }
        } catch { }
    }

    static void SaveIni()
    {
        try {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Gemini bridge server");
            foreach (KeyValuePair<string, string> kv in S)
                sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(INI, sb.ToString(), Encoding.UTF8);
        } catch { }
    }

    static int Num(string k, int d)
    { int v; if (S.ContainsKey(k) && int.TryParse(S[k], out v)) return v; return d; }
    static bool On(string k)
    { return S.ContainsKey(k) && S[k].ToLower() == "on"; }

    static string Site() { return S["site"].ToLower().Trim(); }

    /// <summary>puternode runs entirely outside Chrome, so nothing should launch one.</summary>
    static bool SiteNeedsBrowser() { return Site() != "puternode"; }

    static JobRunner offlineRunner = null;

    static string SiteUrl()
    {
        string site = Site();
        if (site == "gemini") return S["url_gemini"];
        // The puter page is served by our own local server, so it has a real http origin.
        if (site == "puter") return "http://127.0.0.1:" + livePort + "/puter";
        if (site == "puternode") return "";      // no page, no browser
        return S["url_aistudio"];
    }

    /// <summary>Which port the local server actually got. SiteUrl needs the real one.</summary>
    static int livePort = 8756;

    // ================= profiles =================
    class Prof
    {
        public string Id, Name, Dir;
        public DateTime Cool;
        public int Used;
    }

    static List<Prof> profs = new List<Prof>();
    static int curProf = -1;

    static void LoadProfiles()
    {
        profs.Clear();
        try {
            if (!Directory.Exists(PROFD)) Directory.CreateDirectory(PROFD);
            if (File.Exists(PROFINI)) {
                foreach (string raw in File.ReadAllLines(PROFINI)) {
                    string ln = raw.Trim();
                    if (ln.Length == 0 || ln.StartsWith("#")) continue;
                    string[] p = ln.Split('|');
                    if (p.Length < 3) continue;
                    Prof pr = new Prof();
                    pr.Id = p[0].Trim(); pr.Name = p[1].Trim(); pr.Dir = p[2].Trim();
                    pr.Cool = DateTime.MinValue;
                    if (p.Length > 3) {
                        DateTime d;
                        if (DateTime.TryParse(p[3].Trim(), out d)) pr.Cool = d;
                    }
                    if (p.Length > 4) int.TryParse(p[4].Trim(), out pr.Used);
                    profs.Add(pr);
                }
            }
        } catch (Exception ex) { L("profiles: " + ex.Message); }

        if (profs.Count == 0) {
            Prof pr = new Prof();
            pr.Id = "p1"; pr.Name = "Account 1";
            pr.Dir = Path.Combine(PROFD, "p1"); pr.Cool = DateTime.MinValue;
            profs.Add(pr);
            SaveProfiles();
        }
    }

    static void SaveProfiles()
    {
        try {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# id|name|folder|cooldownUntil|used");
            foreach (Prof p in profs)
                sb.AppendLine(p.Id + "|" + p.Name + "|" + p.Dir + "|"
                            + (p.Cool == DateTime.MinValue ? "" : p.Cool.ToString("s"))
                            + "|" + p.Used);
            File.WriteAllText(PROFINI, sb.ToString(), Encoding.UTF8);
        } catch { }
    }

    static bool Usable(Prof p)
    { return p.Cool == DateTime.MinValue || p.Cool <= DateTime.Now; }

    static int PickProfileIndex()
    {
        int best = -1;
        for (int i = 0; i < profs.Count; i++) {
            if (!Usable(profs[i])) continue;
            if (best < 0 || profs[i].Used < profs[best].Used) best = i;
        }
        return best;
    }

    static void MarkExhausted(int idx)
    {
        if (idx < 0 || idx >= profs.Count) return;
        int hrs = Num("cooldownhrs", 0);
        profs[idx].Cool = (hrs > 0)
            ? DateTime.Now.AddHours(hrs)
            : DateTime.Today.AddDays(1).AddMinutes(5);
        SaveProfiles();
        L("profile " + profs[idx].Name + " parked until " + profs[idx].Cool);
    }

    static int UsableCount()
    { int n = 0; foreach (Prof p in profs) if (Usable(p)) n++; return n; }

    static void ResetAllProfiles()
    { foreach (Prof p in profs) p.Cool = DateTime.MinValue; SaveProfiles(); }

    // ================= json =================
    static string JEsc(string s)
    {
        return SimpleJson.EscapeString(s);
    }

    static string PickString(string json, string key)
    {
        var dict = SimpleJson.ParseObject(json);
        return SimpleJson.GetString(dict, key);
    }

    static byte[] ReadBody(NetworkStream ns, int clen)
    {
        byte[] b = new byte[clen];
        int got = 0;
        while (got < clen) {
            int n = ns.Read(b, got, clen - got);
            if (n <= 0) break;
            got += n;
        }
        if (got == clen) return b;
        byte[] t = new byte[got];
        Array.Copy(b, t, got);
        return t;
    }

    // ================= image writing =================
    static WMedia.ColorContext srgb = null;
    static string lastImageSize = "";
    static bool srgbTried = false;

    static WMedia.ColorContext GetSrgb()
    {
        if (srgbTried) return srgb;
        srgbTried = true;
        try {
            string p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"spool\drivers\color\sRGB Color Space Profile.icm");
            if (File.Exists(p)) { srgb = new WMedia.ColorContext(new Uri(p)); return srgb; }
        } catch { }
        try { srgb = new WMedia.ColorContext(WMedia.PixelFormats.Bgr24); } catch { }
        return srgb;
    }

    static bool WriteImage(byte[] raw, string outPath)
    {
        try {
            WImg.BitmapSource src;
            using (MemoryStream ms = new MemoryStream(raw)) {
                WImg.BitmapDecoder dec = WImg.BitmapDecoder.Create(ms,
                    WImg.BitmapCreateOptions.PreservePixelFormat,
                    WImg.BitmapCacheOption.OnLoad);
                src = dec.Frames[0];
            }

            // Worth seeing in the log: it is the only proof the full-size copy really came
            // through, rather than the 1024px preview.
            lastImageSize = src.PixelWidth + "x" + src.PixelHeight;

            WImg.FormatConvertedBitmap conv = new WImg.FormatConvertedBitmap();
            conv.BeginInit();
            conv.Source = src;
            conv.DestinationFormat = WMedia.PixelFormats.Bgr24;
            conv.EndInit();
            conv.Freeze();

            WImg.BitmapFrame frame;
            WMedia.ColorContext cc = GetSrgb();
            if (cc != null) {
                List<WMedia.ColorContext> cl = new List<WMedia.ColorContext>();
                cl.Add(cc);
                frame = WImg.BitmapFrame.Create(conv, null, null,
                            new ReadOnlyCollection<WMedia.ColorContext>(cl));
            } else {
                frame = WImg.BitmapFrame.Create(conv);
            }

            string ext = Path.GetExtension(outPath).ToLower();
            WImg.BitmapEncoder enc;
            if (ext == ".png") enc = new WImg.PngBitmapEncoder();
            else {
                WImg.JpegBitmapEncoder je = new WImg.JpegBitmapEncoder();
                je.QualityLevel = Num("jpegq", 96);
                enc = je;
            }
            enc.Frames.Add(frame);

            byte[] outBytes;
            using (MemoryStream ms = new MemoryStream(1 << 20)) {
                enc.Save(ms);
                outBytes = ms.ToArray();
            }
            File.WriteAllBytes(outPath, outBytes);
            return true;
        } catch (Exception ex) {
            L("WriteImage FAILED: " + ex.Message);
            try { File.WriteAllBytes(outPath, raw); return true; } catch { }
            return false;
        }
    }

    static void BackupOriginal(string orig)
    {
        if (!On("backup")) return;
        try {
            if (!File.Exists(orig)) return;
            if (!Directory.Exists(BAK)) Directory.CreateDirectory(BAK);
            string name = Path.GetFileNameWithoutExtension(orig)
                        + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
                        + Path.GetExtension(orig);
            File.Copy(orig, Path.Combine(BAK, name), true);
        } catch (Exception ex) { L("backup failed: " + ex.Message); }
    }

    // ================= overlay =================
    static Form busyForm;
    static Label busyLabel;
    static ProgressBar busyBar;
    static Button btnCancel, btnRestart;
    static DateTime busySince = DateTime.MinValue;
    static System.Windows.Forms.Timer busyTimer;

    static void BuildOverlay()
    {
        busyForm = new Form();
        busyForm.FormBorderStyle = FormBorderStyle.None;
        busyForm.ShowInTaskbar = false;
        busyForm.TopMost = true;
        busyForm.StartPosition = FormStartPosition.Manual;
        busyForm.Size = new Size(330, 76);
        busyForm.BackColor = Color.FromArgb(24, 24, 27);
        busyForm.Opacity = 0.94;

        busyLabel = new Label();
        busyLabel.ForeColor = Color.White;
        busyLabel.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        busyLabel.Location = new Point(14, 12);
        busyLabel.Size = new Size(230, 22);

        btnRestart = new Button();
        btnRestart.Text = "\u21BB";
        btnRestart.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        btnRestart.Size = new Size(30, 26);
        btnRestart.Location = new Point(254, 10);
        btnRestart.FlatStyle = FlatStyle.Flat;
        btnRestart.FlatAppearance.BorderSize = 0;
        btnRestart.BackColor = Color.FromArgb(48, 48, 54);
        btnRestart.ForeColor = Color.White;
        btnRestart.Cursor = Cursors.Hand;
        btnRestart.Click += delegate { RestartAll(); };

        btnCancel = new Button();
        btnCancel.Text = "\u2715";
        btnCancel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        btnCancel.Size = new Size(30, 26);
        btnCancel.Location = new Point(288, 10);
        btnCancel.FlatStyle = FlatStyle.Flat;
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.BackColor = Color.FromArgb(70, 32, 32);
        btnCancel.ForeColor = Color.White;
        btnCancel.Cursor = Cursors.Hand;
        btnCancel.Click += delegate { CancelAll(); };

        busyBar = new ProgressBar();
        busyBar.Style = ProgressBarStyle.Marquee;
        busyBar.MarqueeAnimationSpeed = 30;
        busyBar.Location = new Point(14, 44);
        busyBar.Size = new Size(304, 14);

        busyForm.Controls.Add(busyLabel);
        busyForm.Controls.Add(btnRestart);
        busyForm.Controls.Add(btnCancel);
        busyForm.Controls.Add(busyBar);

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        if (S["overlaypos"].ToLower() == "topright")
            busyForm.Location = new Point(wa.Right - busyForm.Width - 18, wa.Top + 18);
        else
            busyForm.Location = new Point(wa.Right - busyForm.Width - 18,
                                          wa.Bottom - busyForm.Height - 18);

        IntPtr force = busyForm.Handle;
        GC.KeepAlive(force);

        busyTimer = new System.Windows.Forms.Timer();
        busyTimer.Interval = 700;
        busyTimer.Tick += delegate { RefreshOverlay(); };
    }

    static void RefreshOverlay()
    {
        if (busyForm == null) return;
        try {
            if (busyForm.InvokeRequired) {
                busyForm.BeginInvoke((MethodInvoker)delegate { RefreshOverlay(); });
                return;
            }

            int run = CountRunning(), wait = CountQueued();
            if (run + wait == 0) {
                busyTimer.Stop();
                busySince = DateTime.MinValue;
                busyForm.Hide();
                return;
            }

            if (!On("overlay")) { busyForm.Hide(); return; }
            if (busySince == DateTime.MinValue) busySince = DateTime.Now;

            int s = (int)(DateTime.Now - busySince).TotalSeconds;
            string txt = run + " running";
            if (wait > 0) txt += ",  " + wait + " waiting";
            txt += "    " + s + "s";
            busyLabel.Text = txt;

            if (!busyForm.Visible) { busyForm.Show(); busyForm.TopMost = true; }
            busyTimer.Start();
        } catch { }
    }

    static void CancelAll()
    {
        lock (gate) { jobs.Clear(); }
        L("all jobs cancelled");
        RefreshOverlay();
        System.Threading.Thread t = new System.Threading.Thread(delegate () {
            StopBrowser();
        });
        t.IsBackground = true;
        t.Start();
    }

    static void RestartAll()
    {
        List<Job> copy = new List<Job>();
        lock (gate) { copy.AddRange(jobs); jobs.Clear(); }
        if (copy.Count == 0) { RefreshOverlay(); return; }

        L("restarting " + copy.Count + " job(s) in fresh targets");
        System.Threading.Thread t = new System.Threading.Thread(delegate () {
            StopBrowser();
            System.Threading.Thread.Sleep(900);
            try {
                if (!Directory.Exists(QUEUE)) Directory.CreateDirectory(QUEUE);
                foreach (Job j in copy) {
                    if (!File.Exists(j.Img)) continue;
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("IMG=" + j.Img);
                    if (j.Orig.Length > 0) sb.AppendLine("ORIG=" + j.Orig);
                    File.WriteAllText(Path.Combine(QUEUE,
                        DateTime.Now.Ticks + "_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".job"),
                        sb.ToString(), Encoding.UTF8);
                }
            } catch (Exception ex) { L("restart: " + ex.Message); }
        });
        t.IsBackground = true;
        t.Start();
    }

    // ================= browser =================
    static Process browser = null;
    static DateTime lastUse = DateTime.MinValue;

    static bool BrowserAlive()
    {
        // A live CDP socket is the real proof the browser is up. Chrome's launcher
        // process often exits after handing off, so HasExited was making us relaunch
        // (and kill) a perfectly healthy browser every 800 ms.
        try { return cdpClient != null && cdpClient.IsConnected; }
        catch { return false; }
    }

    static int TuckWindowsAway()
    {
        if (browser == null || browser.HasExited) return 0;
        ChromeLauncher.TuckWindowOffscreen(browser.Id, L);
        return 1;
    }

    static void StopBrowser()
    {
        try {
            if (cdpClient != null)
            {
                cdpClient.Dispose();
                cdpClient = null;
            }
            if (browser != null)
            {
                // Kill the whole tree. browser.Kill() only ends the launcher stub and
                // leaves the real Chrome processes holding the profile.
                ChromeLauncher.KillTree(browser.Id, L);
                L("browser stopped");
            }
        }
        catch { }
        browser = null;
    }

    static bool StartBrowser(string mode, int profIdx, string url)
    {
        if (profIdx < 0 || profIdx >= profs.Count) return false;
        StopBrowser();

        try {
            curProf = profIdx;
            string profileDir = profs[profIdx].Dir;

            // mode=visible in gem_server.ini keeps the window on screen so it can be watched.
            ChromeLauncher.Visible = (mode == "visible");
            ChromeLauncher.OffX = Num("offx", -32000);
            ChromeLauncher.OffY = Num("offy", -32000);

            L(string.Format("launching Chrome for profile={0}...", profs[profIdx].Name));

            var launchTask = ChromeLauncher.LaunchAsync(profileDir, S["chrome"], L);
            launchTask.Wait(20000);

            var res = launchTask.Result;
            browser = res.Process;
            cdpPort = res.DebugPort;
            string wsUrl = res.WsUrl;

            cdpClient = new CdpClient();
            var connectTask = cdpClient.ConnectAsync(wsUrl);
            connectTask.Wait(15000);

            jobRunner = new JobRunner(L, OnCdpFinishResult, OnCdpRotateProfile, OnCdpJobError);
            jobRunner.SetCdpClient(cdpClient, Num("parallel", 3), Num("retries", 2));

            lastUse = DateTime.Now;
            L(string.Format("cdp connected, chrome pid={0} port={1}", browser.Id, cdpPort));
            return true;
        } catch (Exception ex) {
            // Task.Wait wraps everything in an AggregateException whose own message is
            // "One or more errors occurred" - useless. Dig out the real one.
            Exception real = ex;
            while (real is AggregateException && ((AggregateException)real).InnerExceptions.Count > 0)
                real = ((AggregateException)real).InnerExceptions[0];
            L("browser start FAILED: " + real.Message);
            return false;
        }
    }

    // A failed launch must never be retried in a tight loop. ScanQueue runs every 800 ms,
    // so without a cooldown one stuck profile spawned a Chrome process twice a second
    // until the machine gave up.
    static DateTime nextStartAllowed = DateTime.MinValue;
    static int startFails = 0;

    static void EnsureBrowser()
    {
        if (!On("autolaunch")) return;

        // Belt and braces: this backend never needs a browser, so refuse to start one
        // no matter who asks. Chrome was still coming up after a settings change.
        if (!SiteNeedsBrowser()) return;

        lastUse = DateTime.Now;
        if (BrowserAlive()) { startFails = 0; return; }

        if (DateTime.Now < nextStartAllowed) return;

        int idx = PickProfileIndex();
        if (idx < 0) {
            L("every profile is resting");
            try {
                tray.BalloonTipTitle = "All accounts are at their limit";
                tray.BalloonTipText = "Add another profile, or wait until tomorrow.";
                tray.ShowBalloonTip(4000);
            } catch { }
            return;
        }
        if (StartBrowser(S["mode"].ToLower(), idx, SiteUrl()))
        {
            startFails = 0;
            return;
        }

        startFails++;
        int waitSec = startFails * 5;
        if (waitSec > 60) waitSec = 60;
        nextStartAllowed = DateTime.Now.AddSeconds(waitSec);

        if (startFails == 3)
        {
            L("Chrome will not start. Usually an older Chrome is still holding this "
              + "profile folder - close every chrome.exe in Task Manager, or use "
              + "Restart browser from the tray.");
            try {
                tray.BalloonTipTitle = "Chrome will not start";
                tray.BalloonTipText = "An old Chrome may still be holding the profile. "
                                    + "Close all Chrome windows and try again.";
                tray.ShowBalloonTip(6000);
            } catch { }
        }
        if (startFails >= 6) nextStartAllowed = DateTime.Now.AddMinutes(5);
    }

    static void RotateProfile(string why)
    {
        L("rotating: " + why);
        MarkExhausted(curProf);
        StopBrowser();

        // hand every claimed job back to the queue
        lock (gate) {
            foreach (Job j in jobs) { j.Claimed = false; j.Tries++; }
            jobs.RemoveAll(delegate (Job j) { return j.Tries > profs.Count; });
        }

        int idx = PickProfileIndex();
        if (idx < 0) {
            L("no profile left today");
            lock (gate) { jobs.Clear(); }
            RefreshOverlay();
            try {
                tray.BalloonTipTitle = "All accounts are at their limit";
                tray.BalloonTipText = "Add another profile, or wait until tomorrow.";
                tray.ShowBalloonTip(5000);
            } catch { }
            return;
        }
        System.Threading.Thread.Sleep(800);
        StartBrowser(S["mode"].ToLower(), idx, SiteUrl());
    }

    static void IdleCheck()
    {
        int mins = Num("idlemin", 0);
        if (mins <= 0) return;
        if (!BrowserAlive()) return;
        lock (gate) { if (jobs.Count > 0) return; }
        if ((DateTime.Now - lastUse).TotalMinutes >= mins) StopBrowser();
    }

    static void LoginProfile(int idx)
    {
        if (idx < 0 || idx >= profs.Count) return;
        StopBrowser();
        System.Threading.Thread.Sleep(600);
        StartBrowser("visible", idx, SiteUrl());
        MessageBox.Show(
            "A normal Chrome window is opening for:\r\n\r\n    " + profs[idx].Name +
            "\r\n\r\nSign in, open the site once, pick the model or the Gem, then "
          + "close the window.", "Gemini Bridge - profile setup");
    }

    // ================= queue =================
    static void ScanQueue()
    {
        try {
            if (!Directory.Exists(QUEUE)) Directory.CreateDirectory(QUEUE);

            // drop jobs that have been claimed too long
            lock (gate) {
                int ttl = Num("jobttl", 420);
                jobs.RemoveAll(delegate (Job j) {
                    if (j.Claimed && (DateTime.Now - j.ClaimedAt).TotalSeconds > ttl) {
                        L("job " + j.Id + " expired");
                        return true;
                    }
                    return false;
                });
            }

            string[] files = Directory.GetFiles(QUEUE, "*.job");
            if (files.Length > 0)
            {
                Array.Sort(files);

                foreach (string f in files) {
                    string[] lines;
                    try { lines = File.ReadAllLines(f, Encoding.UTF8); }
                    catch { continue; }
                    try { File.Delete(f); } catch { }

                    string img = "", orig = "";
                    foreach (string raw in lines) {
                        string ln = raw.Trim();
                        if (ln.Length == 0) continue;
                        if (ln.StartsWith("IMG=")) img = ln.Substring(4);
                        else if (ln.StartsWith("ORIG=")) orig = ln.Substring(5);
                        else if (img.Length == 0) img = ln;
                    }
                    if (!File.Exists(img)) { L("queued image missing: " + img); continue; }

                    Job j = new Job();
                    j.Id = Guid.NewGuid().ToString("N").Substring(0, 10);
                    j.Img = img; j.Orig = orig;
                    j.Claimed = false; j.At = DateTime.Now; j.Tries = 0;
                    lock (gate) { jobs.Add(j); }
                    L("queued " + j.Id + "  " + Path.GetFileName(img));
                }
            }

            RefreshOverlay();

            // Everything stops until the owner has answered the question. Sending more
            // photos now would fail identically and deepen Google's suspicion.
            if (captchaWaiting)
            {
                if (!captchaToldOnce)
                {
                    captchaToldOnce = true;
                    L("holding " + CountQueued() + " photo(s) until the CAPTCHA is answered");
                }
                return;
            }

            JobRunner runner = null;
            if (SiteNeedsBrowser())
            {
                EnsureBrowser();
                if (cdpClient != null && cdpClient.IsConnected) runner = jobRunner;
            }
            else
            {
                // No Chrome on this path at all.
                if (offlineRunner == null)
                {
                    offlineRunner = new JobRunner(L, OnCdpFinishResult, OnCdpRotateProfile, OnCdpJobError);
                    offlineRunner.SetOffline(Num("parallel", 2), Num("retries", 2));
                    L("using the browser-free Puter backend");
                }
                runner = offlineRunner;
            }

            if (runner != null)
            {
                List<Job> toRun = new List<Job>();
                lock (gate)
                {
                    foreach (Job j in jobs)
                    {
                        if (!j.Claimed)
                        {
                            j.Claimed = true;
                            j.ClaimedAt = DateTime.Now;
                            toRun.Add(j);
                        }
                    }
                }

                foreach (Job j in toRun)
                {
                    string site = Site();
                    bool isGemini = (site == "gemini");
                    bool isPuter  = (site == "puter");

                    object adapter;
                    if (site == "puternode")
                        adapter = new SitePuterNode(S["puter_model"], S["puter_quality"],
                                                    On("puter_test"), On("puter_keepratio"),
                                                    Num("puter_timeout", 300), L);
                    else if (isPuter)
                        adapter = new SitePuter(SiteUrl(), S["puter_model"], S["puter_quality"],
                                                On("puter_test"), On("puter_keepratio"));
                    else if (isGemini) adapter = new SiteGemini();
                    else               adapter = new SiteAIStudio();

                    // Puter has no Gem carrying the prompt, so it always needs one sent.
                    bool sendPrompt = isGemini ? On("gemprompt") : true;
                    string prompt = sendPrompt ? GetPrompt() : "";
                    string url = SiteUrl();

                    served++;
                    lastUse = DateTime.Now;
                    if (curProf >= 0 && curProf < profs.Count) { profs[curProf].Used++; SaveProfiles(); }

                    Task.Run(async () =>
                    {
                        await runner.ProcessJobAsync(j, adapter, url, prompt, sendPrompt);
                    });
                }
            }
        } catch (Exception ex) { L("queue: " + ex.Message); }
    }

    // ================= cdp job callbacks =================
    static void OnCdpFinishResult(object jObj, byte[] bytes)
    {
        Job j = (Job)jObj;
        lock (gate) { jobs.Remove(j); }
        FinishResult(j, bytes);
        RefreshOverlay();
    }

    static void OnCdpRotateProfile(string why)
    {
        RotateProfile(why);
    }

    /// <summary>
    /// Set when Google has put a "prove you are not a robot" page in front of us.
    /// While it is set nothing new is sent, because every photo would hit the same wall
    /// and each attempt makes Google more suspicious, not less.
    /// </summary>
    static bool captchaWaiting = false;
    static bool captchaToldOnce = false;

    static void OnCdpJobError(object jObj, string why)
    {
        Job j = (Job)jObj;
        lock (gate) { jobs.Remove(j); }
        failed++;
        L("failed " + j.Id + ": " + why);

        if (why != null && why.StartsWith("CAPTCHA:")) EnterCaptchaHold();

        RefreshOverlay();
        UpdateTip();
    }

    static void EnterCaptchaHold()
    {
        if (captchaWaiting) return;
        captchaWaiting = true;
        captchaToldOnce = false;

        L("=== PAUSED: Google is asking for a CAPTCHA ===");
        L("    The browser window has been opened with the question showing.");
        L("    Answer it, then use the tray menu: \"CAPTCHA solved, carry on\".");

        // Put the question where it can actually be seen and clicked.
        try { if (browser != null) ChromeLauncher.ShowWindowOnScreen(browser.Id, L); } catch { }

        try {
            tray.BalloonTipTitle = "Google wants you to prove you are not a robot";
            tray.BalloonTipText = "The browser is now on screen. Answer the question, then "
                                + "right-click this icon and choose \"CAPTCHA solved, carry on\".";
            tray.ShowBalloonTip(10000);
        } catch { }
    }

    static void ClearCaptchaHold()
    {
        if (!captchaWaiting) {
            MessageBox.Show("Nothing is on hold right now.", "Gemini Bridge");
            return;
        }
        captchaWaiting = false;
        captchaToldOnce = false;
        L("carrying on after the CAPTCHA");
        try { if (browser != null) ChromeLauncher.HideWindowAgain(browser.Id, L); } catch { }
    }

    // ================= photoshop =================
    static string PsPath(string p) { return p.Replace("\\", "\\\\"); }

    static bool RunInPhotoshop(string js)
    {
        List<string> ids = new List<string>();
        ids.Add("Photoshop.Application");
        for (int v = 30; v >= 8; v--) {
            ids.Add("Photoshop.Application." + v);
            ids.Add("Photoshop.Application." + (v * 10));
        }
        foreach (string pid in ids) {
            object app = null;
            try { app = Marshal.GetActiveObject(pid); } catch { }
            if (app == null) continue;
            try {
                app.GetType().InvokeMember("DoJavaScript", BindingFlags.InvokeMethod,
                                           null, app, new object[] { js });
                return true;
            } catch { }
        }
        L("no photoshop reachable");
        return false;
    }

    static void ReplaceInPhotoshop(string target)
    {
        StringBuilder js = new StringBuilder();
        js.Append("try{app.displayDialogs=DialogModes.NO;}catch(e){}");
        js.Append("var P=\"" + PsPath(target) + "\";");
        js.Append("for(var i=app.documents.length-1;i>=0;i--){");
        js.Append("  var d=app.documents[i];");
        js.Append("  try{ if(String(d.fullName.fsName).toLowerCase()==P.toLowerCase()){");
        js.Append("    d.close(SaveOptions.DONOTSAVECHANGES);} }catch(e){}");
        js.Append("}");
        js.Append("app.open(new File(P));");
        js.Append("try{app.displayDialogs=DialogModes.ERROR;}catch(e){}");
        RunInPhotoshop(js.ToString());
    }

    static void OpenInPhotoshop(string file)
    {
        StringBuilder js = new StringBuilder();
        js.Append("try{app.displayDialogs=DialogModes.NO;}catch(e){}");
        js.Append("app.open(new File(\"" + PsPath(file) + "\"));");
        js.Append("try{app.displayDialogs=DialogModes.ERROR;}catch(e){}");
        RunInPhotoshop(js.ToString());
    }

    /// <summary>
    /// Is this plausibly a restored photo, or something we picked up by mistake?
    ///
    /// A profile picture, an icon, or a stray avatar has been written over a customer's
    /// original before now. Overwriting is the one thing that cannot be undone from the
    /// shop floor, so the picture has to earn it: it must decode, and it must be big
    /// enough to be a photograph.
    /// </summary>
    static bool LooksLikeAPhoto(byte[] bytes, out string why)
    {
        why = "";
        int minPx = Num("minresultpx", 512);

        if (bytes == null || bytes.Length < 20 * 1024)
        { why = "only " + (bytes == null ? 0 : bytes.Length / 1024) + " KB"; return false; }

        try {
            using (MemoryStream ms = new MemoryStream(bytes)) {
                WImg.BitmapDecoder dec = WImg.BitmapDecoder.Create(ms,
                    WImg.BitmapCreateOptions.PreservePixelFormat,
                    WImg.BitmapCacheOption.OnLoad);
                WImg.BitmapSource f = dec.Frames[0];
                int longSide = f.PixelWidth > f.PixelHeight ? f.PixelWidth : f.PixelHeight;
                if (longSide < minPx)
                { why = f.PixelWidth + "x" + f.PixelHeight + ", too small to be a photo"; return false; }
                return true;
            }
        }
        catch (Exception ex) { why = "it will not decode (" + ex.Message + ")"; return false; }
    }

    static void FinishResult(Job j, byte[] imgBytes)
    {
        string finalPath;
        bool replaced = false;

        // Check BEFORE touching the original, not after.
        string badWhy;
        bool trustworthy = LooksLikeAPhoto(imgBytes, out badWhy);

        if (On("replace") && j.Orig.Length > 0 && !trustworthy) {
            if (!Directory.Exists(OUTD)) Directory.CreateDirectory(OUTD);
            string sp = Path.Combine(OUTD,
                "suspect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            try { File.WriteAllBytes(sp, imgBytes); } catch { }

            L("REFUSED to replace " + j.Orig + " - what came back is not a photo ("
              + badWhy + "). Saved it to " + sp + " instead, your original is untouched.");

            failed++;
            RefreshOverlay();
            UpdateTip();
            try {
                tray.BalloonTipTitle = "That one did not come back properly";
                tray.BalloonTipText = "Your original was left alone. Try the photo again.";
                tray.ShowBalloonTip(5000);
            } catch { }
            return;
        }

        if (On("replace") && j.Orig.Length > 0) {
            BackupOriginal(j.Orig);
            if (WriteImage(imgBytes, j.Orig)) { finalPath = j.Orig; replaced = true; }
            else {
                if (!Directory.Exists(OUTD)) Directory.CreateDirectory(OUTD);
                finalPath = Path.Combine(OUTD,
                    "gem_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".jpg");
                File.WriteAllBytes(finalPath, imgBytes);
            }
        } else {
            if (!Directory.Exists(OUTD)) Directory.CreateDirectory(OUTD);
            finalPath = Path.Combine(OUTD,
                "gem_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            WriteImage(imgBytes, finalPath);
        }
        L((replaced ? "replaced " : "saved ") + finalPath
          + (lastImageSize.Length > 0 ? "   [" + lastImageSize + "]" : ""));

        returned++;
        lastUse = DateTime.Now;
        UpdateTip();

        if (replaced) ReplaceInPhotoshop(finalPath);
        else OpenInPhotoshop(finalPath);
    }

    // ================= http =================
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
    const uint HANDLE_FLAG_INHERIT = 1;

    static void ServerLoop()
    {
        int want = Num("port", 8756);
        TcpListener lis = null;

        // Try a few ports. A leftover Chrome from a previous bridge can still be holding
        // the usual one, because a child process inherits its parent's open socket handles.
        for (int p = want; p < want + 5 && lis == null; p++)
        {
            try {
                TcpListener t = new TcpListener(IPAddress.Loopback, p);
                t.Start();

                // Stop the next Chrome we launch from inheriting this socket, so the port
                // is released the moment this process ends.
                try { SetHandleInformation(t.Server.Handle, HANDLE_FLAG_INHERIT, 0); } catch { }

                lis = t;
                livePort = p;
                L("listening on 127.0.0.1:" + p + (p == want ? "" : "  (" + want + " was taken)"));
            }
            catch { }
        }

        if (lis == null)
        {
            // Not fatal any more. The extension is gone, so nothing in the photo flow needs
            // this server - it exists only for future local integrations.
            L("could not open a local port, carrying on without the local server");
            return;
        }

        while (true) {
            try {
                TcpClient c = lis.AcceptTcpClient();
                System.Threading.Thread t =
                    new System.Threading.Thread(delegate () { Handle(c); });
                t.IsBackground = true;
                t.Start();
            } catch { System.Threading.Thread.Sleep(200); }
        }
    }

    static void Send(NetworkStream ns, string status, string ctype, byte[] body)
    {
        StringBuilder h = new StringBuilder();
        h.Append("HTTP/1.1 " + status + "\r\n");
        h.Append("Content-Type: " + ctype + "\r\n");
        h.Append("Content-Length: " + (body == null ? 0 : body.Length) + "\r\n");
        h.Append("Access-Control-Allow-Origin: *\r\n");
        h.Append("Access-Control-Allow-Headers: *\r\n");
        h.Append("Access-Control-Allow-Methods: GET,POST,OPTIONS\r\n");
        h.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        byte[] hb = Encoding.ASCII.GetBytes(h.ToString());
        ns.Write(hb, 0, hb.Length);
        if (body != null && body.Length > 0) ns.Write(body, 0, body.Length);
        ns.Flush();
    }

    static void SendText(NetworkStream ns, string status, string ctype, string body)
    { Send(ns, status, ctype, Encoding.UTF8.GetBytes(body)); }

    static void Handle(TcpClient c)
    {
        try {
            using (c)
            using (NetworkStream ns = c.GetStream()) {
                c.ReceiveTimeout = 30000;

                MemoryStream buf = new MemoryStream();
                byte[] one = new byte[1];
                int headerEnd = -1;
                while (buf.Length < 65536) {
                    int n = ns.Read(one, 0, 1);
                    if (n <= 0) break;
                    buf.WriteByte(one[0]);
                    byte[] a = buf.ToArray();
                    if (a.Length >= 4 && a[a.Length - 4] == 13 && a[a.Length - 3] == 10
                                      && a[a.Length - 2] == 13 && a[a.Length - 1] == 10) {
                        headerEnd = a.Length; break;
                    }
                }
                if (headerEnd < 0) return;

                string head = Encoding.ASCII.GetString(buf.ToArray(), 0, headerEnd);
                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                if (lines.Length == 0) return;
                string[] req = lines[0].Split(' ');
                if (req.Length < 2) return;
                string method = req[0].ToUpper();
                string path = req[1];

                int clen = 0;
                foreach (string ln in lines)
                    if (ln.ToLower().StartsWith("content-length:"))
                        int.TryParse(ln.Substring(15).Trim(), out clen);

                if (method == "OPTIONS") { Send(ns, "204 No Content", "text/plain", null); return; }

                if (path.StartsWith("/ping")) {
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    return;
                }

                if (path.StartsWith("/pending")) {
                    ScanQueue();
                    int q = CountQueued(), r = CountRunning();
                    SendText(ns, "200 OK", "application/json",
                        "{\"queued\":" + q + ",\"running\":" + r
                      + ",\"parallel\":" + Num("parallel", 3)
                      + ",\"url\":\"" + JEsc(SiteUrl()) + "\"}");
                    return;
                }

                if (path.StartsWith("/job")) {
                    ScanQueue();
                    Job take = null;
                    lock (gate) {
                        foreach (Job j in jobs) {
                            if (!j.Claimed) { j.Claimed = true; j.ClaimedAt = DateTime.Now; take = j; break; }
                        }
                    }
                    if (take == null) {
                        SendText(ns, "200 OK", "application/json", "{\"job\":null}");
                        return;
                    }

                    byte[] raw = File.ReadAllBytes(take.Img);
                    string b64 = Convert.ToBase64String(raw);
                    string mime = take.Img.ToLower().EndsWith(".png") ? "image/png" : "image/jpeg";
                    bool gemini = (S["site"].ToLower() == "gemini");
                    bool sendPrompt = gemini ? On("gemprompt") : true;

                    StringBuilder j2 = new StringBuilder();
                    j2.Append("{\"job\":{");
                    j2.Append("\"id\":\"" + take.Id + "\",");
                    j2.Append("\"name\":\"" + JEsc(Path.GetFileName(take.Img)) + "\",");
                    j2.Append("\"mime\":\"" + mime + "\",");
                    j2.Append("\"site\":\"" + (gemini ? "gemini" : "aistudio") + "\",");
                    j2.Append("\"sendprompt\":" + (sendPrompt ? "true" : "false") + ",");
                    j2.Append("\"submit\":\"" + (gemini ? "enter" : "ctrlenter") + "\",");
                    j2.Append("\"autosubmit\":" + (On("autosubmit") ? "true" : "false") + ",");
                    j2.Append("\"retries\":" + Num("retries", 2) + ",");
                    j2.Append("\"prompt\":\"" + JEsc(sendPrompt ? GetPrompt() : "") + "\",");
                    j2.Append("\"data\":\"" + b64 + "\"");
                    j2.Append("}}");

                    served++;
                    lastUse = DateTime.Now;
                    if (curProf >= 0) { profs[curProf].Used++; SaveProfiles(); }
                    L("served " + take.Id);
                    RefreshOverlay();
                    UpdateTip();
                    SendText(ns, "200 OK", "application/json", j2.ToString());
                    return;
                }

                if (path.StartsWith("/result")) {
                    if (clen <= 0) { SendText(ns, "400 Bad Request", "text/plain", "no body"); return; }
                    string json = Encoding.UTF8.GetString(ReadBody(ns, clen));

                    string data = PickString(json, "data");
                    string id = PickString(json, "id");
                    string ext = PickString(json, "ext");
                    if (ext == null || ext.Length == 0) ext = "png";

                    Job j3 = null;
                    lock (gate) {
                        foreach (Job x in jobs) if (x.Id == id) { j3 = x; break; }
                        if (j3 != null) jobs.Remove(j3);
                    }

                    if (j3 == null) {
                        L("result for an unknown job " + id);
                        SendText(ns, "200 OK", "application/json", "{\"ok\":false}");
                        return;
                    }
                    if (data == null || data.Length < 100) {
                        failed++;
                        RefreshOverlay();
                        SendText(ns, "200 OK", "application/json", "{\"ok\":false}");
                        return;
                    }

                    int comma = data.IndexOf(",");
                    if (data.StartsWith("data:") && comma > 0) data = data.Substring(comma + 1);

                    FinishResult(j3, Convert.FromBase64String(data));
                    RefreshOverlay();
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    return;
                }

                if (path.StartsWith("/retry")) {
                    if (clen > 0) ReadBody(ns, clen);
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    return;
                }

                if (path.StartsWith("/limit")) {
                    string why = "";
                    if (clen > 0) {
                        string rj = Encoding.UTF8.GetString(ReadBody(ns, clen));
                        string w = PickString(rj, "why");
                        if (w != null) why = w;
                    }
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    System.Threading.Thread rt = new System.Threading.Thread(delegate () {
                        RotateProfile(why);
                    });
                    rt.IsBackground = true;
                    rt.Start();
                    return;
                }

                if (path.StartsWith("/log")) {
                    if (clen > 0) {
                        string lj = Encoding.UTF8.GetString(ReadBody(ns, clen));
                        string msg = PickString(lj, "msg");
                        if (msg != null) L("[http] " + msg);
                    }
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    return;
                }

                if (path.StartsWith("/fail")) {
                    string why = "", id = "";
                    if (clen > 0) {
                        string fj = Encoding.UTF8.GetString(ReadBody(ns, clen));
                        string w = PickString(fj, "why");
                        string i2 = PickString(fj, "id");
                        if (w != null) why = w;
                        if (i2 != null) id = i2;
                    }
                    lock (gate) {
                        jobs.RemoveAll(delegate (Job x) { return x.Id == id; });
                    }
                    failed++;
                    L("failed " + id + ": " + why);
                    RefreshOverlay();
                    UpdateTip();
                    SendText(ns, "200 OK", "application/json", "{\"ok\":true}");
                    return;
                }

                if (path.StartsWith("/puter")) {
                    // Serve the Puter backend page. It must come from http, not file://,
                    // or puter.js cannot keep a sign-in.
                    string pf = Path.Combine(ROOT, "puter_page.html");
                    if (!File.Exists(pf)) {
                        SendText(ns, "500 Internal Server Error", "text/plain",
                                 "puter_page.html is missing from C:\\PS_Fix");
                        return;
                    }
                    Send(ns, "200 OK", "text/html; charset=utf-8", File.ReadAllBytes(pf));
                    return;
                }

                SendText(ns, "404 Not Found", "text/plain", "no");
            }
        } catch (Exception ex) { L("handle: " + ex.Message); }
    }

    // ================= prompt =================
    static string DefaultPrompt()
    {
        return "Edit this photo into a professional white-background studio portrait. "
             + "Output the edited image, never text only.\n\n"
             + "1. Straighten it. Rotate so both eyes sit on the same horizontal line and the "
             + "shoulders are level. Crop away any blank corners.\n"
             + "2. Replace the background with a clean pure white studio backdrop. Keep hair and "
             + "shoulder edges clean, no halo. Center the subject like a passport photo.\n"
             + "3. Soft even studio lighting, with a gentle rim light on hair and shoulders.\n"
             + "4. Sharpen eyes, eyebrows, lashes and hair. Remove small blemishes but keep real "
             + "skin texture. Reduce noise. Do not smooth the skin flat.\n"
             + "5. Natural realistic skin tone, balanced exposure and contrast.\n\n"
             + "If two or more people are present, apply all steps to each person.\n\n"
             + "Keep every face fully recognizable. Do not reshape features, do not change age, "
             + "do not beautify.";
    }

    static string GetPrompt()
    {
        try {
            if (!File.Exists(PROMPT)) File.WriteAllText(PROMPT, DefaultPrompt(), Encoding.UTF8);
            string p = File.ReadAllText(PROMPT, Encoding.UTF8).Trim();
            if (p.Length > 0) return p;
        } catch { }
        return DefaultPrompt();
    }

    // ================= tray + settings =================
    static void UpdateTip()
    {
        try {
            if (tray == null) return;
            string who = (curProf >= 0 && curProf < profs.Count) ? profs[curProf].Name : "-";
            tray.Text = "Bridge  ok " + returned + " / fail " + failed
                      + "  " + who + "  (" + UsableCount() + "/" + profs.Count + ")";
        } catch { }
    }

    static string ProfLabel(Prof p)
    {
        string st = Usable(p) ? "ready" : ("resting until " + p.Cool.ToString("HH:mm"));
        return p.Name + "   [" + st + "]   used " + p.Used;
    }

    static void ShowSettings()
    {
        Form f = new Form();
        f.Text = "Gemini Bridge - Settings (CDP Protocol)";
        f.Size = new Size(660, 740);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false; f.MinimizeBox = false;
        f.Font = new Font("Segoe UI", 9.75f);

        int y = 14;

        Label ls = new Label(); ls.Text = "Site";
        ls.Location = new Point(16, y); ls.Size = new Size(80, 22);
        ComboBox cbSite = new ComboBox();
        cbSite.DropDownStyle = ComboBoxStyle.DropDownList;
        cbSite.Location = new Point(100, y - 3); cbSite.Size = new Size(130, 24);
        cbSite.Items.AddRange(new object[] { "aistudio", "gemini", "puter", "puternode" });
        cbSite.SelectedItem = S["site"].ToLower();
        if (cbSite.SelectedIndex < 0) cbSite.SelectedIndex = 0;

        CheckBox cGemP = new CheckBox();
        cGemP.Text = "Send prompt on Gemini (off when using a Gem)";
        cGemP.Location = new Point(246, y - 3); cGemP.Size = new Size(310, 24);
        cGemP.Checked = On("gemprompt");

        Label lpar = new Label(); lpar.Text = "At once";
        lpar.Location = new Point(556, y); lpar.Size = new Size(56, 22);
        TextBox tPar = new TextBox();
        tPar.Location = new Point(600, y - 3); tPar.Size = new Size(34, 24);
        tPar.Text = S["parallel"];
        y += 32;

        Label lu1 = new Label(); lu1.Text = "AI Studio";
        lu1.Location = new Point(16, y); lu1.Size = new Size(80, 22);
        TextBox tU1 = new TextBox();
        tU1.Location = new Point(100, y - 3); tU1.Size = new Size(534, 24);
        tU1.Text = S["url_aistudio"];
        y += 30;

        Label lu2 = new Label(); lu2.Text = "Gemini";
        lu2.Location = new Point(16, y); lu2.Size = new Size(80, 22);
        TextBox tU2 = new TextBox();
        tU2.Location = new Point(100, y - 3); tU2.Size = new Size(534, 24);
        tU2.Text = S["url_gemini"];
        y += 30;

        // ---- Puter row ----
        Label lpq = new Label(); lpq.Text = "Puter";
        lpq.Location = new Point(16, y); lpq.Size = new Size(80, 22);

        ComboBox cbQual = new ComboBox();
        cbQual.DropDownStyle = ComboBoxStyle.DropDownList;
        cbQual.Location = new Point(100, y - 3); cbQual.Size = new Size(80, 24);
        cbQual.Items.AddRange(new object[] { "512", "1K", "2K", "4K" });
        cbQual.SelectedItem = S["puter_quality"];
        if (cbQual.SelectedIndex < 0) cbQual.SelectedIndex = 2;

        CheckBox cPTest = new CheckBox();
        cPTest.Text = "Test mode (free sample image, uses no credits)";
        cPTest.Location = new Point(196, y - 3); cPTest.Size = new Size(300, 24);
        cPTest.Checked = On("puter_test");

        CheckBox cPRatio = new CheckBox();
        cPRatio.Text = "Keep shape";
        cPRatio.Location = new Point(500, y - 3); cPRatio.Size = new Size(134, 24);
        cPRatio.Checked = On("puter_keepratio");
        y += 32;

        GroupBox gp = new GroupBox();
        gp.Text = "Accounts";
        gp.Location = new Point(16, y); gp.Size = new Size(618, 190);

        ListBox lb = new ListBox();
        lb.Location = new Point(14, 24); lb.Size = new Size(436, 152);
        foreach (Prof p in profs) lb.Items.Add(ProfLabel(p));

        Button bAdd = new Button();
        bAdd.Text = "Add"; bAdd.Location = new Point(462, 24); bAdd.Size = new Size(142, 27);
        Button bLogin = new Button();
        bLogin.Text = "Sign in"; bLogin.Location = new Point(462, 56); bLogin.Size = new Size(142, 27);
        Button bRen = new Button();
        bRen.Text = "Rename"; bRen.Location = new Point(462, 88); bRen.Size = new Size(142, 27);
        Button bWake = new Button();
        bWake.Text = "Wake all"; bWake.Location = new Point(462, 120); bWake.Size = new Size(142, 27);
        Button bDel = new Button();
        bDel.Text = "Remove"; bDel.Location = new Point(462, 152); bDel.Size = new Size(142, 24);

        MethodInvoker refresh = delegate {
            int keep = lb.SelectedIndex;
            lb.Items.Clear();
            foreach (Prof p in profs) lb.Items.Add(ProfLabel(p));
            if (keep >= 0 && keep < lb.Items.Count) lb.SelectedIndex = keep;
            UpdateTip();
        };

        bAdd.Click += delegate {
            int n = 1;
            while (true) {
                bool taken = false;
                foreach (Prof p in profs) if (p.Id == "p" + n) { taken = true; break; }
                if (!taken) break;
                n++;
            }
            Prof np = new Prof();
            np.Id = "p" + n; np.Name = "Account " + n;
            np.Dir = Path.Combine(PROFD, np.Id); np.Cool = DateTime.MinValue;
            profs.Add(np); SaveProfiles(); refresh();
            if (MessageBox.Show("Profile added.\r\nSign in now?", "Gemini Bridge",
                                MessageBoxButtons.YesNo) == DialogResult.Yes)
                LoginProfile(profs.Count - 1);
        };
        bLogin.Click += delegate {
            if (lb.SelectedIndex < 0) { MessageBox.Show("Pick a profile."); return; }
            LoginProfile(lb.SelectedIndex);
        };
        bRen.Click += delegate {
            if (lb.SelectedIndex < 0) { MessageBox.Show("Pick a profile."); return; }
            Prof p = profs[lb.SelectedIndex];
            Form d = new Form();
            d.Text = "Rename"; d.Size = new Size(360, 140);
            d.FormBorderStyle = FormBorderStyle.FixedDialog;
            d.StartPosition = FormStartPosition.CenterParent;
            d.MaximizeBox = false; d.MinimizeBox = false;
            TextBox tb = new TextBox();
            tb.Location = new Point(16, 20); tb.Size = new Size(310, 24); tb.Text = p.Name;
            Button ok = new Button();
            ok.Text = "OK"; ok.Location = new Point(240, 60); ok.Size = new Size(86, 28);
            ok.Click += delegate { p.Name = tb.Text.Trim(); SaveProfiles(); d.Close(); };
            d.Controls.Add(tb); d.Controls.Add(ok);
            d.ShowDialog(); refresh();
        };
        bWake.Click += delegate { ResetAllProfiles(); refresh(); };
        bDel.Click += delegate {
            if (lb.SelectedIndex < 0) { MessageBox.Show("Pick a profile."); return; }
            if (profs.Count <= 1) { MessageBox.Show("Keep at least one."); return; }
            profs.RemoveAt(lb.SelectedIndex); SaveProfiles(); refresh();
        };

        gp.Controls.AddRange(new Control[] { lb, bAdd, bLogin, bRen, bWake, bDel });
        y += 200;

        CheckBox cRep = new CheckBox();
        cRep.Text = "Replace original photo";
        cRep.Location = new Point(16, y); cRep.Size = new Size(178, 24);
        cRep.Checked = On("replace");
        CheckBox cBak = new CheckBox();
        cBak.Text = "Keep backup";
        cBak.Location = new Point(200, y); cBak.Size = new Size(120, 24);
        cBak.Checked = On("backup");
        CheckBox cSub = new CheckBox();
        cSub.Text = "Auto submit";
        cSub.Location = new Point(318, y); cSub.Size = new Size(120, 24);
        cSub.Checked = On("autosubmit");
        CheckBox cOv = new CheckBox();
        cOv.Text = "Progress bar";
        cOv.Location = new Point(444, y); cOv.Size = new Size(120, 24);
        cOv.Checked = On("overlay");
        y += 30;

        Label lm = new Label(); lm.Text = "Window";
        lm.Location = new Point(16, y); lm.Size = new Size(80, 22);
        ComboBox cbMode = new ComboBox();
        cbMode.DropDownStyle = ComboBoxStyle.DropDownList;
        cbMode.Location = new Point(100, y - 3); cbMode.Size = new Size(110, 24);
        cbMode.Items.AddRange(new object[] { "hidden", "visible" });
        cbMode.SelectedItem = S["mode"].ToLower();
        if (cbMode.SelectedIndex < 0) cbMode.SelectedIndex = 0;
        CheckBox cHide = new CheckBox();
        cHide.Text = "Hide from taskbar";
        cHide.Location = new Point(222, y - 3); cHide.Size = new Size(148, 24);
        cHide.Checked = On("notaskbar");
        CheckBox cForce = new CheckBox();
        cForce.Text = "Force off screen";
        cForce.Location = new Point(376, y - 3); cForce.Size = new Size(136, 24);
        cForce.Checked = On("forceoff");
        Label lrt = new Label(); lrt.Text = "Tries";
        lrt.Location = new Point(520, y); lrt.Size = new Size(40, 22);
        TextBox tRet = new TextBox();
        tRet.Location = new Point(560, y - 3); tRet.Size = new Size(40, 24);
        tRet.Text = S["retries"];
        y += 34;

        Label lp = new Label(); lp.Text = "Prompt  (AI Studio)";
        lp.Location = new Point(16, y); lp.Size = new Size(300, 22);
        y += 22;
        TextBox tPrompt = new TextBox();
        tPrompt.Multiline = true; tPrompt.ScrollBars = ScrollBars.Vertical;
        tPrompt.Location = new Point(16, y); tPrompt.Size = new Size(618, 150);
        try { tPrompt.Text = File.ReadAllText(PROMPT, Encoding.UTF8); } catch { }
        y += 160;

        Button bReset = new Button();
        bReset.Text = "Reset prompt"; bReset.Location = new Point(16, y); bReset.Size = new Size(118, 30);
        Button bBak = new Button();
        bBak.Text = "Backups"; bBak.Location = new Point(142, y); bBak.Size = new Size(100, 30);
        Button bSave = new Button();
        bSave.Text = "Save"; bSave.Location = new Point(448, y); bSave.Size = new Size(88, 30);
        Button bClose = new Button();
        bClose.Text = "Close"; bClose.Location = new Point(546, y); bClose.Size = new Size(88, 30);

        bReset.Click += delegate { tPrompt.Text = DefaultPrompt(); };
        bBak.Click += delegate {
            try { if (!Directory.Exists(BAK)) Directory.CreateDirectory(BAK);
                  Process.Start("explorer.exe", BAK); } catch { }
        };
        bSave.Click += delegate {
            S["site"]         = cbSite.SelectedItem.ToString();
            S["gemprompt"]    = cGemP.Checked ? "on" : "off";
            S["parallel"]     = tPar.Text.Trim();
            S["url_aistudio"] = tU1.Text.Trim();
            S["url_gemini"]   = tU2.Text.Trim();
            S["puter_quality"]   = cbQual.SelectedItem.ToString();
            S["puter_test"]      = cPTest.Checked ? "on" : "off";
            S["puter_keepratio"] = cPRatio.Checked ? "on" : "off";
            S["replace"]      = cRep.Checked ? "on" : "off";
            S["backup"]       = cBak.Checked ? "on" : "off";
            S["autosubmit"]   = cSub.Checked ? "on" : "off";
            S["overlay"]      = cOv.Checked ? "on" : "off";
            S["mode"]         = cbMode.SelectedItem.ToString();
            S["notaskbar"]    = cHide.Checked ? "on" : "off";
            S["forceoff"]     = cForce.Checked ? "on" : "off";
            S["retries"]      = tRet.Text.Trim();
            try {
                SaveIni();
                File.WriteAllText(PROMPT, tPrompt.Text.Trim(), Encoding.UTF8);
                MessageBox.Show("Saved.\r\nUse Restart browser for site or window changes.",
                                "Gemini Bridge");
            } catch (Exception ex) { MessageBox.Show("Save failed:\n" + ex.Message); }
        };
        bClose.Click += delegate { f.Close(); };

        f.Controls.AddRange(new Control[] { ls, cbSite, cGemP, lpar, tPar,
                                            lpq, cbQual, cPTest, cPRatio,
                                            lu1, tU1, lu2, tU2, gp,
                                            cRep, cBak, cSub, cOv,
                                            lm, cbMode, cHide, cForce, lrt, tRet,
                                            lp, tPrompt, bReset, bBak, bSave, bClose });
        f.ShowDialog();
    }

    [STAThread]
    public static void Main(string[] args)
    {
        bool fresh;
        Mutex mx = new Mutex(true, "Global\\PS_Fix_GemBridge", out fresh);
        if (!fresh) return;

        LoadIni();
        LoadProfiles();
        GetPrompt();
        foreach (string d in new string[] { QUEUE, OUTD, BAK, PROFD })
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
        try { if (File.Exists(LOG) && new FileInfo(LOG).Length > 400000) File.Delete(LOG); }
        catch { }

        L("=== bridge start (CDP Engine) ===  site=" + S["site"] + "  parallel=" + S["parallel"]);
        Application.EnableVisualStyles();
        BuildOverlay();

        tray = new NotifyIcon();
        tray.Icon = SystemIcons.Application;
        tray.Visible = true;
        UpdateTip();

        ContextMenu cm = new ContextMenu();
        cm.MenuItems.Add(new MenuItem("Settings", delegate { ShowSettings(); }));
        cm.MenuItems.Add(new MenuItem("Restart browser", delegate {
            StopBrowser();
            System.Threading.Thread.Sleep(500);
            int i = PickProfileIndex();
            if (i < 0) MessageBox.Show("Every account is resting. Use Wake all.", "Gemini Bridge");
            else StartBrowser(S["mode"].ToLower(), i, SiteUrl());
        }));
        cm.MenuItems.Add(new MenuItem("Show browser window", delegate {
            // This used to call TuckWindowsAway(), i.e. it pushed the window further away
            // instead of showing it. Now it really brings Chrome back on screen.
            if (browser != null) ChromeLauncher.ShowWindowOnScreen(browser.Id, L);
            else MessageBox.Show("The browser is not running.", "Gemini Bridge");
        }));
        cm.MenuItems.Add(new MenuItem("Hide browser window", delegate {
            if (browser != null) ChromeLauncher.HideWindowAgain(browser.Id, L);
        }));
        cm.MenuItems.Add("-");
        cm.MenuItems.Add(new MenuItem("CAPTCHA solved, carry on", delegate {
            ClearCaptchaHold();
        }));
        cm.MenuItems.Add("-");
        cm.MenuItems.Add(new MenuItem("Stop browser", delegate { StopBrowser(); }));
        cm.MenuItems.Add(new MenuItem("Wake all accounts", delegate {
            ResetAllProfiles(); UpdateTip();
        }));
        cm.MenuItems.Add("-");
        cm.MenuItems.Add(new MenuItem("Open backups", delegate {
            try { Process.Start("explorer.exe", BAK); } catch { }
        }));
        cm.MenuItems.Add(new MenuItem("Open log", delegate {
            try { Process.Start("notepad.exe", LOG); } catch { }
        }));
        cm.MenuItems.Add("-");
        cm.MenuItems.Add(new MenuItem("Exit", delegate {
            StopBrowser(); tray.Visible = false; Application.Exit();
        }));
        tray.ContextMenu = cm;

        System.Threading.Thread srv =
            new System.Threading.Thread(new System.Threading.ThreadStart(ServerLoop));
        srv.IsBackground = true;
        srv.Start();

        System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer();
        poll.Interval = 800;
        poll.Tick += delegate { ScanQueue(); IdleCheck(); UpdateTip(); };
        poll.Start();

        Application.Run();
        GC.KeepAlive(mx);
    }
}