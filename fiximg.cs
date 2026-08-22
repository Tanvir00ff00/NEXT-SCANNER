using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public class FixImg
{
    const string ROOT  = @"C:\PS_Fix";
    static string QDIR = Path.Combine(ROOT, "fixqueue");
    static string ALIVE = Path.Combine(QDIR, "daemon.alive");
    static string LogPath = Path.Combine(ROOT, "exe_log.txt");
    static bool verbose = false;

    static void L(string m)
    {
        if (!verbose) return;
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + m + "\r\n"); }
        catch { }
    }

    // ---------- sRGB so Photoshop never asks about a missing profile ----------
    static ColorContext srgb = null;
    static bool srgbTried = false;

    static ColorContext GetSrgb()
    {
        if (srgbTried) return srgb;
        srgbTried = true;
        try {
            string p = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"spool\drivers\color\sRGB Color Space Profile.icm");
            if (File.Exists(p)) { srgb = new ColorContext(new Uri(p)); return srgb; }
        } catch { }
        try { srgb = new ColorContext(PixelFormats.Bgr24); } catch { }
        return srgb;
    }

    static void SaveTo(BitmapSource img, string outPath)
    {
        FormatConvertedBitmap conv = new FormatConvertedBitmap();
        conv.BeginInit();
        conv.Source = img;
        conv.DestinationFormat = PixelFormats.Bgr24;
        conv.EndInit();
        conv.Freeze();

        BitmapFrame frame;
        ColorContext cc = GetSrgb();
        if (cc != null) {
            List<ColorContext> cl = new List<ColorContext>();
            cl.Add(cc);
            frame = BitmapFrame.Create(conv, null, null,
                        new ReadOnlyCollection<ColorContext>(cl));
        } else {
            frame = BitmapFrame.Create(conv);
        }

        string ext = Path.GetExtension(outPath).ToLower();
        BitmapEncoder enc;
        if (ext == ".png") enc = new PngBitmapEncoder();
        else {
            JpegBitmapEncoder je = new JpegBitmapEncoder();
            je.QualityLevel = 94;
            enc = je;
        }
        enc.Frames.Add(frame);

        // build it in memory, then one single write -- much faster than temp files
        byte[] outBytes;
        using (MemoryStream ms = new MemoryStream(1 << 20)) {
            enc.Save(ms);
            outBytes = ms.ToArray();
        }
        File.WriteAllBytes(outPath, outBytes);
    }

    static bool ConvertOne(string inPath, string outPath)
    {
        byte[] raw;
        try { raw = File.ReadAllBytes(inPath); }      // read fully, release the handle
        catch (Exception ex) { L("read FAILED: " + ex.Message); return false; }

        try {
            BitmapSource img;
            using (MemoryStream ms = new MemoryStream(raw)) {
                BitmapDecoder dec = BitmapDecoder.Create(ms,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapSource f0 = dec.Frames[0];

                int ori = 1;
                try {
                    BitmapMetadata md = dec.Frames[0].Metadata as BitmapMetadata;
                    if (md != null) {
                        object v = md.GetQuery("/app1/ifd/{ushort=274}");
                        if (v != null) ori = Convert.ToInt32(v);
                    }
                } catch { }

                int angle = 0;
                if (ori == 3) angle = 180;
                else if (ori == 6) angle = 90;
                else if (ori == 8) angle = 270;

                img = (angle != 0)
                    ? (BitmapSource)new TransformedBitmap(f0, new RotateTransform(angle))
                    : f0;
                img.Freeze();
            }
            SaveTo(img, outPath);
            return true;
        } catch (Exception ex) { L("WIC failed: " + ex.Message); }

        // GDI+ is kinder to broken jpeg headers
        try {
            BitmapSource img2;
            using (MemoryStream ms = new MemoryStream(raw))
            using (System.Drawing.Image bmp = System.Drawing.Image.FromStream(ms))
            using (MemoryStream ms2 = new MemoryStream()) {
                bmp.Save(ms2, System.Drawing.Imaging.ImageFormat.Png);
                ms2.Position = 0;
                BitmapDecoder d2 = BitmapDecoder.Create(ms2,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                img2 = d2.Frames[0];
                img2.Freeze();
            }
            SaveTo(img2, outPath);
            return true;
        } catch (Exception ex) { L("GDI failed: " + ex.Message); return false; }
    }

    // ---------- talking to Photoshop ----------
    // The script used to sit in a loop waiting for us, which froze Photoshop solid because
    // $.sleep() never lets it answer Windows. Now the script drops the job and leaves, and
    // WE open the finished picture. Photoshop is free the whole time.

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
        return false;
    }

    /// <summary>
    /// Open the repaired file, on its own STA thread so the queue keeps moving, and with
    /// retries because Photoshop refuses COM calls while it is still running the script
    /// that queued this job.
    /// </summary>
    static void OpenInPhotoshopAsync(string file, bool ok)
    {
        Thread t = new Thread(delegate ()
        {
            StringBuilder js = new StringBuilder();
            if (ok) {
                js.Append("try{app.displayDialogs=DialogModes.NO;}catch(e){}");
                js.Append("app.open(new File(\"" + PsPath(file) + "\"));");
                js.Append("try{app.displayDialogs=DialogModes.ERROR;}catch(e){}");
            } else {
                js.Append("alert(\"This image could not be repaired.\\n\\n"
                        + "Open it once in Windows Photos to check the file is not damaged.\");");
            }

            string code = js.ToString();
            for (int i = 0; i < 20; i++) {
                if (RunInPhotoshop(code)) { L("opened in photoshop after " + i + " tries"); return; }
                Thread.Sleep(750);
            }
            L("could not reach photoshop to open " + file);
        });
        t.IsBackground = true;
        try { t.SetApartmentState(ApartmentState.STA); } catch { }
        t.Start();
    }

    // ---------- warm the whole pipeline so the first real photo is instant ----------
    static void Warmup()
    {
        try {
            GetSrgb();
            byte[] px = new byte[16 * 3];
            BitmapSource tiny = BitmapSource.Create(4, 4, 96, 96,
                PixelFormats.Bgr24, null, px, 4 * 3);
            tiny.Freeze();
            string t = Path.Combine(Path.GetTempPath(), "psfix_warm.jpg");
            SaveTo(tiny, t);
            try { File.Delete(t); } catch { }
        } catch { }
    }

    // ---------- one job file ----------
    static void RunJob(string jobFile)
    {
        string inPath = "", outPath = "", flag = "";
        bool openAfter = false;
        try {
            foreach (string raw in File.ReadAllLines(jobFile, Encoding.UTF8)) {
                string ln = raw.Trim();
                if (ln.StartsWith("IN="))        inPath  = ln.Substring(3);
                else if (ln.StartsWith("OUT="))  outPath = ln.Substring(4);
                else if (ln.StartsWith("FLAG=")) flag    = ln.Substring(5);
                else if (ln.StartsWith("OPEN=")) openAfter = (ln.Substring(5) == "1");
            }
        } catch (Exception ex) { L("job read: " + ex.Message); }
        try { File.Delete(jobFile); } catch { }

        if (inPath.Length == 0) return;
        if (outPath.Length == 0) {
            string dir = Path.GetDirectoryName(inPath);
            string bas = Path.GetFileNameWithoutExtension(inPath);
            outPath = Path.Combine(dir, bas + "_fix.jpg");
        }

        bool ok = File.Exists(inPath) && ConvertOne(inPath, outPath);
        L((ok ? "ok   " : "fail ") + Path.GetFileName(inPath));

        if (flag.Length > 0) {
            try { File.WriteAllText(flag, ok ? "1" : "0"); } catch { }
        }

        if (openAfter) OpenInPhotoshopAsync(outPath, ok);
    }

    // ---------- resident mode ----------
    static void Daemon()
    {
        bool fresh;
        Mutex mx = new Mutex(true, "Global\\PS_Fix_ImgDaemon", out fresh);
        if (!fresh) return;                       // one is already resident

        try { if (!Directory.Exists(QDIR)) Directory.CreateDirectory(QDIR); } catch { }

        // sweep away leftovers from an earlier session
        try {
            foreach (string f in Directory.GetFiles(QDIR, "*.done")) {
                try { if (File.GetLastWriteTime(f) < DateTime.Now.AddMinutes(-10)) File.Delete(f); }
                catch { }
            }
            // Every job predating this daemon is stale, whatever its age. One that was
            // queued while no daemon was running belongs to a Photoshop session that has
            // moved on, and running it now would reopen photos out of nowhere.
            foreach (string f in Directory.GetFiles(QDIR, "*.job")) {
                try { File.Delete(f); L("dropped a stale job: " + Path.GetFileName(f)); }
                catch { }
            }
        } catch { }

        Warmup();
        L("daemon up");

        AutoResetEvent poke = new AutoResetEvent(false);
        try {
            FileSystemWatcher fsw = new FileSystemWatcher(QDIR, "*.job");
            fsw.Created += delegate { poke.Set(); };
            fsw.Changed += delegate { poke.Set(); };
            fsw.EnableRaisingEvents = true;
        } catch (Exception ex) { L("watcher: " + ex.Message); }

        DateTime lastBeat = DateTime.MinValue;

        while (true) {
            try {
                string[] jobs = Directory.GetFiles(QDIR, "*.job");
                if (jobs.Length > 0) {
                    Array.Sort(jobs);
                    foreach (string j in jobs) RunJob(j);
                }
            } catch (Exception ex) { L("loop: " + ex.Message); }

            // heartbeat so the script knows we are alive
            if ((DateTime.Now - lastBeat).TotalSeconds >= 2) {
                lastBeat = DateTime.Now;
                try { File.WriteAllText(ALIVE, DateTime.Now.Ticks.ToString()); } catch { }
            }

            poke.WaitOne(200);                    // wakes instantly on a new job
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        bool daemon = false;
        bool openAfter = false;
        string outPath = "", flagPath = "";
        List<string> files = new List<string>();

        for (int i = 0; i < args.Length; i++) {
            string a = args[i];
            if (a.Equals("-daemon", StringComparison.OrdinalIgnoreCase)) daemon = true;
            else if (a.Equals("-open", StringComparison.OrdinalIgnoreCase)) openAfter = true;
            else if (a.Equals("-v", StringComparison.OrdinalIgnoreCase)) verbose = true;
            else if (a.Equals("-Out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) outPath = args[++i];
            else if (a.Equals("-Flag", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) flagPath = args[++i];
            else files.Add(a);
        }

        if (daemon) { Daemon(); return; }

        // one shot: drag and drop, SendTo, or a direct call
        if (files.Count == 0 && outPath.Length > 0 && File.Exists(outPath)) {
            files.Add(outPath); outPath = "";
        }

        int done = 0;
        string lastTarget = "";
        foreach (string f in files) {
            if (!File.Exists(f)) continue;
            string full = Path.GetFullPath(f);
            string target = outPath;
            if (target.Length == 0) {
                string dir = Path.GetDirectoryName(full);
                string bas = Path.GetFileNameWithoutExtension(full);
                if (string.IsNullOrEmpty(dir)) dir = ROOT;
                target = Path.Combine(dir, bas + "_fix.jpg");
            }
            lastTarget = target;
            if (ConvertOne(full, target)) done++;
        }

        if (flagPath.Length > 0) {
            try { File.WriteAllText(flagPath, done.ToString()); } catch { }
        }

        // Same deal on the one-shot path: we open it, the script never waits.
        if (openAfter && lastTarget.Length > 0) {
            OpenInPhotoshopAsync(lastTarget, done > 0);
            Thread.Sleep(16000);        // let the retry thread finish before we exit
        }
    }
}