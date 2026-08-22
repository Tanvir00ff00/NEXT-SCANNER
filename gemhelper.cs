using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class GemHelper
{
    const string ROOT = @"C:\PS_Fix";
    static string INI = Path.Combine(ROOT, "gem_send.ini");
    static string LOG = Path.Combine(ROOT, "gem_send_log.txt");
    static Dictionary<string, string> S = new Dictionary<string, string>();

    // ---------- win32 ----------
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    delegate bool EnumProc(IntPtr h, IntPtr p);

    const int SW_RESTORE = 9;
    const byte VK_CONTROL = 0x11, VK_V = 0x56, VK_RETURN = 0x0D;
    const uint KEYUP = 0x0002;

    static void L(string m)
    {
        try { File.AppendAllText(LOG, DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n"); }
        catch { }
    }

    static void LoadIni()
    {
        S["url"]        = "https://gemini.google.com/app";
        S["downloads"]  = "";
        S["watchmin"]   = "10";
        S["stablems"]   = "700";
        S["browser"]    = "chrome";
        // auto typing
        S["autopaste"]  = "on";
        S["autoenter"]  = "on";
        S["titlematch"] = "Gemini";
        S["loadwait"]   = "1500";   // title must stay unchanged this long
        S["maxwait"]    = "40";     // give up after this many seconds
        S["afterfocus"] = "700";    // pause after focusing the window
        S["afterpaste"] = "1800";   // pause between paste and Enter

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
            sb.AppendLine("# Gemini Send settings");
            foreach (KeyValuePair<string, string> kv in S)
                sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(INI, sb.ToString(), Encoding.UTF8);
        } catch { }
    }

    static int Num(string k, int def)
    {
        int v;
        if (S.ContainsKey(k) && int.TryParse(S[k], out v)) return v;
        return def;
    }
    static bool On(string k) { return S.ContainsKey(k) && S[k].ToLower() == "on"; }

    static string DownloadsDir()
    {
        if (S["downloads"].Length > 0 && Directory.Exists(S["downloads"])) return S["downloads"];
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ---------- clipboard ----------
    static bool ToClipboard(string file)
    {
        try {
            Image img;
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read))
            using (MemoryStream ms = new MemoryStream()) {
                byte[] buf = new byte[65536];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                ms.Position = 0;
                img = Image.FromStream(ms);
            }
            DataObject dob = new DataObject();
            dob.SetImage(img);
            Clipboard.SetDataObject(dob, true);
            L("clipboard set");
            return true;
        } catch (Exception ex) { L("clipboard FAILED: " + ex.Message); return false; }
    }

    // ---------- browser ----------
    static void OpenBrowser(string url)
    {
        if (S["browser"].ToLower() == "chrome") {
            string[] paths = {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             @"Google\Chrome\Application\chrome.exe")
            };
            foreach (string p in paths) {
                if (!File.Exists(p)) continue;
                try { Process.Start(p, "\"" + url + "\""); L("chrome opened"); return; }
                catch { }
            }
        }
        try { Process.Start(url); L("default browser opened"); }
        catch (Exception ex) { L("browser FAILED: " + ex.Message); }
    }

    // ---------- find the browser window by title ----------
    static IntPtr FindWin(string needle, out string title)
    {
        IntPtr found = IntPtr.Zero;
        string got = "";
        string low = needle.ToLower();

        EnumWindows(delegate (IntPtr h, IntPtr p) {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len < 2) return true;
            StringBuilder sb = new StringBuilder(len + 2);
            GetWindowText(h, sb, sb.Capacity);
            string t = sb.ToString();
            if (t.ToLower().IndexOf(low) >= 0) { found = h; got = t; return false; }
            return true;
        }, IntPtr.Zero);

        title = got;
        return found;
    }

    static bool Focus(IntPtr h)
    {
        try {
            ShowWindow(h, SW_RESTORE);
            for (int i = 0; i < 5; i++) {
                SetForegroundWindow(h);
                System.Threading.Thread.Sleep(120);
                if (GetForegroundWindow() == h) return true;

                // stubborn window -> attach input threads and retry
                uint pid;
                uint target = GetWindowThreadProcessId(h, out pid);
                uint me = GetCurrentThreadId();
                AttachThreadInput(me, target, true);
                SetForegroundWindow(h);
                AttachThreadInput(me, target, false);
                System.Threading.Thread.Sleep(120);
                if (GetForegroundWindow() == h) return true;
            }
        } catch (Exception ex) { L("focus: " + ex.Message); }
        return false;
    }

    static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(vk, 0, KEYUP, IntPtr.Zero);
    }

    static void CtrlV()
    {
        keybd_event(VK_CONTROL, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(VK_V, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        keybd_event(VK_V, 0, KEYUP, IntPtr.Zero);
        System.Threading.Thread.Sleep(40);
        keybd_event(VK_CONTROL, 0, KEYUP, IntPtr.Zero);
    }

    // wait until the title appears AND stops changing -> page settled
    static void AutoPaste()
    {
        if (!On("autopaste")) { L("autopaste off"); return; }

        string needle = S["titlematch"];
        int maxWait   = Num("maxwait", 40) * 1000;
        int loadWait  = Num("loadwait", 1500);
        int afterFoc  = Num("afterfocus", 700);
        int afterPas  = Num("afterpaste", 1800);

        DateTime stop = DateTime.Now.AddMilliseconds(maxWait);
        string lastTitle = null;
        DateTime sameSince = DateTime.Now;
        IntPtr win = IntPtr.Zero;

        while (DateTime.Now < stop) {
            string title;
            IntPtr h = FindWin(needle, out title);

            if (h == IntPtr.Zero) {
                lastTitle = null;
                System.Threading.Thread.Sleep(300);
                continue;
            }

            if (title != lastTitle) {
                lastTitle = title;
                sameSince = DateTime.Now;
                L("title -> " + title);
            } else if ((DateTime.Now - sameSince).TotalMilliseconds >= loadWait) {
                win = h;
                break;
            }
            System.Threading.Thread.Sleep(250);
        }

        if (win == IntPtr.Zero) { L("window never settled, skipping autopaste"); return; }

        if (!Focus(win)) { L("could not focus, skipping autopaste"); return; }
        L("focused");

        System.Threading.Thread.Sleep(afterFoc);
        CtrlV();
        L("ctrl+v sent");

        if (On("autoenter")) {
            System.Threading.Thread.Sleep(afterPas);
            if (GetForegroundWindow() != win) { L("focus lost, Enter skipped"); return; }
            Tap(VK_RETURN);
            L("enter sent");
        }
    }

    // ---------- photoshop ----------
    static bool PushToPhotoshop(string file)
    {
        List<string> ids = new List<string>();
        ids.Add("Photoshop.Application");
        for (int v = 30; v >= 8; v--) {
            ids.Add("Photoshop.Application." + v);
            ids.Add("Photoshop.Application." + (v * 10));
        }
        string js = "app.open(new File(\"" + file.Replace("\\", "\\\\") + "\"));";
        foreach (string pid in ids) {
            object app = null;
            try { app = Marshal.GetActiveObject(pid); } catch { }
            if (app == null) continue;
            try {
                app.GetType().InvokeMember("DoJavaScript", BindingFlags.InvokeMethod,
                                           null, app, new object[] { js });
                L("pushed via " + pid);
                return true;
            } catch (Exception ex) { L("push " + pid + ": " + ex.Message); }
        }
        L("push failed on all progids");
        return false;
    }

    // ---------- watcher ----------
    static bool IsImage(string f)
    {
        string e = Path.GetExtension(f).ToLower();
        return e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".webp";
    }

    static bool Stable(string f, int ms)
    {
        try {
            long l1 = new FileInfo(f).Length;
            System.Threading.Thread.Sleep(ms);
            FileInfo b = new FileInfo(f);
            if (!b.Exists || b.Length != l1 || b.Length == 0) return false;
            using (FileStream fs = File.Open(f, FileMode.Open, FileAccess.Read, FileShare.None)) { }
            return true;
        } catch { return false; }
    }

    static void Watch(DateTime armed)
    {
        string dir = DownloadsDir();
        L("watching " + dir);
        DateTime stop = DateTime.Now.AddMinutes(Num("watchmin", 10));
        int stablems = Num("stablems", 700);
        Dictionary<string, bool> seen = new Dictionary<string, bool>();

        while (DateTime.Now < stop) {
            try {
                foreach (string f in Directory.GetFiles(dir)) {
                    if (!IsImage(f) || seen.ContainsKey(f)) continue;
                    if (new FileInfo(f).LastWriteTime < armed) continue;
                    if (File.Exists(f + ".crdownload")) continue;
                    if (!Stable(f, stablems)) continue;

                    seen[f] = true;
                    L("new download: " + f);
                    if (PushToPhotoshop(f)) return;
                }
            } catch (Exception ex) { L("watch: " + ex.Message); }
            System.Threading.Thread.Sleep(600);
        }
        L("watch timed out");
    }

    // ---------- settings ----------
    static void ShowSettings()
    {
        LoadIni();
        Form f = new Form();
        f.Text = "Gemini Send - Settings";
        f.Size = new Size(620, 460);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false; f.MinimizeBox = false;
        f.Font = new Font("Segoe UI", 9.75f);

        int y = 16;
        Label l1 = new Label(); l1.Text = "Gem / chat URL";
        l1.Location = new Point(16, y); l1.Size = new Size(120, 22);
        TextBox tUrl = new TextBox();
        tUrl.Location = new Point(146, y - 3); tUrl.Size = new Size(430, 24);
        tUrl.Text = S["url"];
        y += 36;

        Label l2 = new Label(); l2.Text = "Downloads folder";
        l2.Location = new Point(16, y); l2.Size = new Size(120, 22);
        TextBox tDl = new TextBox();
        tDl.Location = new Point(146, y - 3); tDl.Size = new Size(430, 24);
        tDl.Text = S["downloads"];
        y += 28;
        Label l2b = new Label();
        l2b.Text = "empty = default   ->   " + DownloadsDir();
        l2b.Location = new Point(146, y); l2b.Size = new Size(430, 20);
        l2b.ForeColor = Color.Gray;
        y += 32;

        Label l3 = new Label(); l3.Text = "Watch minutes";
        l3.Location = new Point(16, y); l3.Size = new Size(120, 22);
        TextBox tMin = new TextBox();
        tMin.Location = new Point(146, y - 3); tMin.Size = new Size(70, 24);
        tMin.Text = S["watchmin"];

        Label l4 = new Label(); l4.Text = "Browser";
        l4.Location = new Point(240, y); l4.Size = new Size(64, 22);
        ComboBox cb = new ComboBox();
        cb.DropDownStyle = ComboBoxStyle.DropDownList;
        cb.Location = new Point(310, y - 3); cb.Size = new Size(266, 24);
        cb.Items.AddRange(new object[] { "chrome", "default" });
        cb.SelectedItem = (S["browser"].ToLower() == "default") ? "default" : "chrome";
        y += 44;

        GroupBox gb = new GroupBox();
        gb.Text = "Auto typing";
        gb.Location = new Point(16, y); gb.Size = new Size(560, 170);

        CheckBox cPaste = new CheckBox();
        cPaste.Text = "Send Ctrl+V when the page has settled";
        cPaste.Location = new Point(16, 26); cPaste.Size = new Size(320, 24);
        cPaste.Checked = On("autopaste");

        CheckBox cEnter = new CheckBox();
        cEnter.Text = "Then send Enter";
        cEnter.Location = new Point(346, 26); cEnter.Size = new Size(190, 24);
        cEnter.Checked = On("autoenter");

        Label m1 = new Label(); m1.Text = "Window title contains";
        m1.Location = new Point(16, 62); m1.Size = new Size(140, 22);
        TextBox tTitle = new TextBox();
        tTitle.Location = new Point(162, 59); tTitle.Size = new Size(180, 24);
        tTitle.Text = S["titlematch"];

        Label m2 = new Label(); m2.Text = "Settle ms";
        m2.Location = new Point(360, 62); m2.Size = new Size(70, 22);
        TextBox tLoad = new TextBox();
        tLoad.Location = new Point(432, 59); tLoad.Size = new Size(100, 24);
        tLoad.Text = S["loadwait"];

        Label m3 = new Label(); m3.Text = "Give up after (s)";
        m3.Location = new Point(16, 98); m3.Size = new Size(140, 22);
        TextBox tMax = new TextBox();
        tMax.Location = new Point(162, 95); tMax.Size = new Size(180, 24);
        tMax.Text = S["maxwait"];

        Label m4 = new Label(); m4.Text = "After focus ms";
        m4.Location = new Point(360, 98); m4.Size = new Size(70, 22);
        TextBox tFoc = new TextBox();
        tFoc.Location = new Point(432, 95); tFoc.Size = new Size(100, 24);
        tFoc.Text = S["afterfocus"];

        Label m5 = new Label(); m5.Text = "Paste to Enter ms";
        m5.Location = new Point(16, 134); m5.Size = new Size(140, 22);
        TextBox tPas = new TextBox();
        tPas.Location = new Point(162, 131); tPas.Size = new Size(180, 24);
        tPas.Text = S["afterpaste"];

        Label m6 = new Label();
        m6.Text = "slow net -> raise Settle ms";
        m6.Location = new Point(360, 134); m6.Size = new Size(190, 22);
        m6.ForeColor = Color.Gray;

        gb.Controls.AddRange(new Control[] { cPaste, cEnter, m1, tTitle, m2, tLoad,
                                             m3, tMax, m4, tFoc, m5, tPas, m6 });
        y += 182;

        Button bSave = new Button();
        bSave.Text = "Save"; bSave.Location = new Point(398, y); bSave.Size = new Size(84, 30);
        Button bCancel = new Button();
        bCancel.Text = "Cancel"; bCancel.Location = new Point(492, y); bCancel.Size = new Size(84, 30);

        bSave.Click += delegate {
            S["url"]        = tUrl.Text.Trim();
            S["downloads"]  = tDl.Text.Trim();
            S["watchmin"]   = tMin.Text.Trim();
            S["browser"]    = cb.SelectedItem.ToString();
            S["autopaste"]  = cPaste.Checked ? "on" : "off";
            S["autoenter"]  = cEnter.Checked ? "on" : "off";
            S["titlematch"] = tTitle.Text.Trim();
            S["loadwait"]   = tLoad.Text.Trim();
            S["maxwait"]    = tMax.Text.Trim();
            S["afterfocus"] = tFoc.Text.Trim();
            S["afterpaste"] = tPas.Text.Trim();
            SaveIni();
            MessageBox.Show("Saved.", "Gemini Send");
            f.Close();
        };
        bCancel.Click += delegate { f.Close(); };

        f.Controls.AddRange(new Control[] { l1, tUrl, l2, tDl, l2b, l3, tMin, l4, cb,
                                            gb, bSave, bCancel });
        Application.Run(f);
    }

    // ---------- main ----------
    [STAThread]
    public static void Main(string[] args)
    {
        LoadIni();

        if (args != null && args.Length > 0 && args[0].ToLower().IndexOf("setting") >= 0) {
            Application.EnableVisualStyles();
            ShowSettings();
            return;
        }

        string img = "";
        for (int i = 0; i < args.Length; i++)
            if (args[i].ToLower() == "-send" && i + 1 < args.Length) { img = args[i + 1]; i++; }
        if (img.Length == 0 && args.Length > 0 && File.Exists(args[0])) img = args[0];

        if (img.Length == 0 || !File.Exists(img)) {
            MessageBox.Show("No image given.", "Gemini Send");
            return;
        }

        try { if (File.Exists(LOG) && new FileInfo(LOG).Length > 200000) File.Delete(LOG); } catch { }
        L("=== send " + img + " ===");

        DateTime armed = DateTime.Now.AddSeconds(-2);

        bool clip = ToClipboard(img);
        OpenBrowser(S["url"]);

        if (clip) AutoPaste();
        else MessageBox.Show("Clipboard failed. Drag this file into Gemini:\n" + img, "Gemini Send");

        Watch(armed);
    }
}