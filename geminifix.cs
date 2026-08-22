using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public class GeminiFix
{
    const string ROOT     = @"C:\PS_Fix";
    static string KEYS    = Path.Combine(ROOT, "keys.txt");
    static string PROMPT  = Path.Combine(ROOT, "prompt.txt");
    static string INI     = Path.Combine(ROOT, "settings.ini");
    static string STATE   = Path.Combine(ROOT, "keystate.txt");
    static string GLOG    = Path.Combine(ROOT, "gemini_log.txt");

    static void Log(string m)
    {
        try { File.AppendAllText(GLOG, DateTime.Now.ToString("HH:mm:ss") + "  " + m + "\r\n"); }
        catch { }
    }

    // ---------------- settings ----------------
    static Dictionary<string, string> S = new Dictionary<string, string>();

    static void LoadSettings()
    {
        S["model"]    = "gemini-3.1-flash-image";
        S["size"]     = "1K";
        S["aspect"]   = "";            // empty = keep source shape
        S["retries"]  = "3";
        S["notify"]   = "com";         // com = push into Photoshop, poll = jsx waits
        S["timeout"]  = "120";

        try {
            if (File.Exists(INI)) {
                foreach (string raw in File.ReadAllLines(INI)) {
                    string ln = raw.Trim();
                    if (ln.Length == 0 || ln.StartsWith("#")) continue;
                    int eq = ln.IndexOf('=');
                    if (eq < 1) continue;
                    S[ln.Substring(0, eq).Trim().ToLower()] = ln.Substring(eq + 1).Trim();
                }
            }
        } catch { }
    }

    static void SaveSettings()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Gemini Fix settings");
        foreach (KeyValuePair<string, string> kv in S)
            sb.AppendLine(kv.Key + "=" + kv.Value);
        File.WriteAllText(INI, sb.ToString(), Encoding.UTF8);
    }

    static string DefaultPrompt()
    {
        return "Restore and enhance this portrait photograph for professional ID/passport printing. "
             + "Remove noise, scratches and blur. Sharpen facial details naturally. "
             + "Correct lighting and skin tone realistically. "
             + "Replace the background with a clean, even, pure white background. "
             + "Keep the person's face, features, expression, clothing and identity EXACTLY the same. "
             + "Do not beautify, do not change age, do not alter face shape. "
             + "Keep the original framing and aspect ratio. Output only the edited image.";
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

    // ---------------- key pool ----------------
    class KeyRec { public string Key; public int Used; public DateTime CoolUntil; public bool Dead; }
    static List<KeyRec> Pool = new List<KeyRec>();

    static void LoadKeys()
    {
        Pool.Clear();
        try {
            if (!File.Exists(KEYS)) {
                File.WriteAllText(KEYS,
                    "# One Gemini API key per line. Lines starting with # are ignored.\r\n"
                  + "# Get keys at https://aistudio.google.com/apikey\r\n", Encoding.UTF8);
                return;
            }
            foreach (string raw in File.ReadAllLines(KEYS)) {
                string ln = raw.Trim();
                if (ln.Length < 10 || ln.StartsWith("#")) continue;
                KeyRec r = new KeyRec();
                r.Key = ln; r.Used = 0; r.CoolUntil = DateTime.MinValue; r.Dead = false;
                Pool.Add(r);
            }
        } catch { }

        // remember today's dead keys and usage counts
        try {
            if (File.Exists(STATE)) {
                foreach (string raw in File.ReadAllLines(STATE)) {
                    string[] p = raw.Split('|');
                    if (p.Length < 3) continue;
                    foreach (KeyRec r in Pool) {
                        if (r.Key != p[0]) continue;
                        int u; int.TryParse(p[1], out u); r.Used = u;
                        DateTime dt;
                        if (DateTime.TryParse(p[2], CultureInfo.InvariantCulture,
                                              DateTimeStyles.None, out dt)) {
                            if (dt.Date == DateTime.Now.Date && dt > DateTime.Now) {
                                r.CoolUntil = dt;
                                if (dt > DateTime.Now.AddHours(6)) r.Dead = true;
                            }
                        }
                    }
                }
            }
        } catch { }
    }

    static void SaveState()
    {
        try {
            StringBuilder sb = new StringBuilder();
            foreach (KeyRec r in Pool)
                sb.AppendLine(r.Key + "|" + r.Used + "|"
                            + r.CoolUntil.ToString("o", CultureInfo.InvariantCulture));
            File.WriteAllText(STATE, sb.ToString(), Encoding.UTF8);
        } catch { }
    }

    // least used, not cooling down, not dead
    static KeyRec PickKey()
    {
        KeyRec best = null;
        foreach (KeyRec r in Pool) {
            if (r.Dead) continue;
            if (r.CoolUntil > DateTime.Now) continue;
            if (best == null || r.Used < best.Used) best = r;
        }
        if (best != null) return best;

        // everything cooling -> take the one that frees up first
        KeyRec soon = null;
        foreach (KeyRec r in Pool) {
            if (r.Dead) continue;
            if (soon == null || r.CoolUntil < soon.CoolUntil) soon = r;
        }
        return soon;
    }

    // ---------------- json helpers ----------------
    static string JEsc(string s)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in s) {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32 || c > 126) sb.Append("\\u" + ((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // pull the first base64 blob that sits inside an inlineData block
    static string ExtractImage(string json)
    {
        int at = 0;
        while (true) {
            int i = json.IndexOf("inlineData", at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) i = json.IndexOf("inline_data", at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;

            int d = json.IndexOf("\"data\"", i, StringComparison.OrdinalIgnoreCase);
            if (d < 0) { at = i + 5; continue; }

            int q1 = json.IndexOf('"', json.IndexOf(':', d) + 1);
            if (q1 < 0) { at = i + 5; continue; }
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) { at = i + 5; continue; }

            string b64 = json.Substring(q1 + 1, q2 - q1 - 1);
            if (b64.Length > 100) return b64;
            at = q2;
        }
    }

    // ---------------- the call ----------------
    static bool CallGemini(string inPath, string outPath, out string err)
    {
        err = "";
        LoadKeys();
        if (Pool.Count == 0) { err = "No API key in keys.txt"; return false; }

        byte[] raw;
        try { raw = File.ReadAllBytes(inPath); }
        catch (Exception ex) { err = "read: " + ex.Message; return false; }

        string b64in = Convert.ToBase64String(raw);
        string mime = inPath.ToLower().EndsWith(".png") ? "image/png" : "image/jpeg";
        string prompt = GetPrompt();
        string model = S["model"];
        string size = S["size"];
        string aspect = S["aspect"];

        StringBuilder cfg = new StringBuilder();
        cfg.Append("\"responseModalities\":[\"TEXT\",\"IMAGE\"]");
        StringBuilder ic = new StringBuilder();
        if (size.Length > 0) ic.Append("\"imageSize\":\"" + size + "\"");
        if (aspect.Length > 0) {
            if (ic.Length > 0) ic.Append(",");
            ic.Append("\"aspectRatio\":\"" + aspect + "\"");
        }
        if (ic.Length > 0) cfg.Append(",\"imageConfig\":{" + ic + "}");

        string body = "{\"contents\":[{\"parts\":["
                    + "{\"text\":\"" + JEsc(prompt) + "\"},"
                    + "{\"inline_data\":{\"mime_type\":\"" + mime + "\",\"data\":\"" + b64in + "\"}}"
                    + "]}],\"generationConfig\":{" + cfg + "}}";

        int maxTries;
        if (!int.TryParse(S["retries"], out maxTries)) maxTries = 3;
        int timeoutSec;
        if (!int.TryParse(S["timeout"], out timeoutSec)) timeoutSec = 120;

        int attempts = Math.Max(maxTries, 1) * Math.Max(Pool.Count, 1);

        for (int t = 0; t < attempts; t++) {
            KeyRec kr = PickKey();
            if (kr == null) { err = "all keys exhausted"; break; }

            if (kr.CoolUntil > DateTime.Now) {
                int wait = (int)(kr.CoolUntil - DateTime.Now).TotalMilliseconds;
                if (wait > 0 && wait < 65000) System.Threading.Thread.Sleep(wait);
            }

            string url = "https://generativelanguage.googleapis.com/v1beta/models/"
                       + model + ":generateContent";
            try {
                ServicePointManager.SecurityProtocol =
                    (SecurityProtocolType)3072 | (SecurityProtocolType)768;
            } catch { }

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers.Add("x-goog-api-key", kr.Key);
            req.Timeout = timeoutSec * 1000;
            req.ReadWriteTimeout = timeoutSec * 1000;

            try {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                req.ContentLength = payload.Length;
                using (Stream st = req.GetRequestStream()) st.Write(payload, 0, payload.Length);

                string json;
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                    json = sr.ReadToEnd();

                kr.Used++;
                string b64out = ExtractImage(json);
                if (b64out == null) {
                    err = "no image in reply (blocked or text-only)";
                    Log("key#" + Pool.IndexOf(kr) + " no image");
                    continue;
                }

                byte[] outBytes = Convert.FromBase64String(b64out);
                string tmp = outPath + ".part";
                File.WriteAllBytes(tmp, outBytes);
                if (File.Exists(outPath)) File.Delete(outPath);
                File.Move(tmp, outPath);

                SaveState();
                Log("OK  key#" + Pool.IndexOf(kr) + "  " + Path.GetFileName(outPath));
                return true;
            }
            catch (WebException we) {
                int code = 0;
                string detail = we.Message;
                try {
                    HttpWebResponse hr = we.Response as HttpWebResponse;
                    if (hr != null) {
                        code = (int)hr.StatusCode;
                        using (StreamReader sr = new StreamReader(hr.GetResponseStream()))
                            detail = sr.ReadToEnd();
                    }
                } catch { }

                Log("key#" + Pool.IndexOf(kr) + " http " + code + "  " + Trim(detail));

                if (code == 429) {
                    string d = (detail == null) ? "" : detail.ToLower();
                    bool perDay = (d.IndexOf("perday") >= 0) || (d.IndexOf("per day") >= 0)
                               || (d.IndexOf("daily") >= 0);

                    int delay = 60;
                    int rd = detail.IndexOf("retryDelay", StringComparison.OrdinalIgnoreCase);
                    if (rd > 0) {
                        string tail = detail.Substring(rd);
                        StringBuilder num = new StringBuilder();
                        foreach (char c in tail) {
                            if (c >= '0' && c <= '9') num.Append(c);
                            else if (num.Length > 0) break;
                        }
                        int parsed;
                        if (int.TryParse(num.ToString(), out parsed) && parsed > 0 && parsed < 3600)
                            delay = parsed + 2;
                    }

                    if (perDay) {
                        kr.CoolUntil = DateTime.Now.AddHours(12);
                        kr.Dead = true;
                        Log("key#" + Pool.IndexOf(kr) + " DAILY quota gone, parked for today");
                    } else {
                        kr.CoolUntil = DateTime.Now.AddSeconds(delay);
                        Log("key#" + Pool.IndexOf(kr) + " cooling " + delay + "s");
                    }
                } else if (code == 401 || code == 403) {
                    kr.Dead = true;
                    kr.CoolUntil = DateTime.Now.AddHours(12);
                } else if (code == 400) {
                    err = "400 " + Trim(detail);
                    SaveState();
                    return false;
                } else {
                    System.Threading.Thread.Sleep(1500);
                }
                err = "http " + code + " " + Trim(detail);
            }
            catch (Exception ex) { err = ex.Message; System.Threading.Thread.Sleep(1000); }
        }

        SaveState();
        return false;
    }

    static string Trim(string s)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }

    // ---------------- push result into photoshop ----------------
    static bool PushToPhotoshop(string file)
    {
        string[] ids = { "Photoshop.Application", "Photoshop.Application.180",
                         "Photoshop.Application.90", "Photoshop.Application.80" };
        foreach (string pid in ids) {
            try {
                object app = Marshal.GetActiveObject(pid);
                if (app == null) continue;
                string js = "app.open(new File(\"" + file.Replace("\\", "\\\\") + "\"));";
                app.GetType().InvokeMember("DoJavaScript", BindingFlags.InvokeMethod,
                                           null, app, new object[] { js });
                Log("pushed to " + pid);
                return true;
            } catch { }
        }
        Log("push failed");
        return false;
    }

    // ---------------- settings window ----------------
    static void ShowSettings()
    {
        LoadSettings();
        GetPrompt();
        LoadKeys();

        Form f = new Form();
        f.Text = "Gemini Fix - Settings";
        f.Size = new Size(620, 560);
        f.StartPosition = FormStartPosition.CenterScreen;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.MaximizeBox = false; f.MinimizeBox = false;

        Font ui = new Font("Segoe UI", 9.75f);
        f.Font = ui;
        int y = 16;

        Label l1 = new Label();
        l1.Text = "Model"; l1.Location = new Point(18, y); l1.Size = new Size(120, 22);
        ComboBox cbModel = new ComboBox();
        cbModel.DropDownStyle = ComboBoxStyle.DropDown;
        cbModel.Location = new Point(150, y - 3); cbModel.Size = new Size(420, 24);
        cbModel.Items.AddRange(new object[] {
            "gemini-2.5-flash-image", "gemini-3.1-flash-image", "gemini-3-pro-image" });
        cbModel.Text = S["model"];
        y += 38;

        Label l2 = new Label();
        l2.Text = "Resolution"; l2.Location = new Point(18, y); l2.Size = new Size(120, 22);
        ComboBox cbSize = new ComboBox();
        cbSize.DropDownStyle = ComboBoxStyle.DropDownList;
        cbSize.Location = new Point(150, y - 3); cbSize.Size = new Size(140, 24);
        cbSize.Items.AddRange(new object[] { "512", "1K", "2K", "4K" });
        cbSize.SelectedItem = S["size"];
        if (cbSize.SelectedIndex < 0) cbSize.SelectedItem = "1K";

        Label l2b = new Label();
        l2b.Text = "1K is enough for 1.6x2 inch prints";
        l2b.Location = new Point(304, y); l2b.Size = new Size(280, 22);
        l2b.ForeColor = Color.Gray;
        y += 38;

        Label l3 = new Label();
        l3.Text = "Aspect ratio"; l3.Location = new Point(18, y); l3.Size = new Size(120, 22);
        ComboBox cbAsp = new ComboBox();
        cbAsp.DropDownStyle = ComboBoxStyle.DropDownList;
        cbAsp.Location = new Point(150, y - 3); cbAsp.Size = new Size(140, 24);
        cbAsp.Items.AddRange(new object[] { "(keep source)", "1:1", "3:4", "4:3", "4:5", "9:16", "16:9" });
        cbAsp.SelectedItem = (S["aspect"].Length == 0) ? "(keep source)" : S["aspect"];
        if (cbAsp.SelectedIndex < 0) cbAsp.SelectedIndex = 0;
        y += 38;

        Label l4 = new Label();
        l4.Text = "When finished"; l4.Location = new Point(18, y); l4.Size = new Size(120, 22);
        ComboBox cbNotify = new ComboBox();
        cbNotify.DropDownStyle = ComboBoxStyle.DropDownList;
        cbNotify.Location = new Point(150, y - 3); cbNotify.Size = new Size(300, 24);
        cbNotify.Items.AddRange(new object[] {
            "com  -  push into Photoshop (no freeze)",
            "poll -  Photoshop waits for the file" });
        cbNotify.SelectedIndex = (S["notify"] == "poll") ? 1 : 0;
        y += 42;

        Label l5 = new Label();
        l5.Text = "Prompt sent to Gemini";
        l5.Location = new Point(18, y); l5.Size = new Size(300, 22);
        y += 24;
        TextBox tbPrompt = new TextBox();
        tbPrompt.Multiline = true; tbPrompt.ScrollBars = ScrollBars.Vertical;
        tbPrompt.Location = new Point(18, y); tbPrompt.Size = new Size(556, 130);
        try { tbPrompt.Text = File.ReadAllText(PROMPT, Encoding.UTF8); } catch { }
        y += 140;

        Label l6 = new Label();
        l6.Text = "API keys  (one per line)";
        l6.Location = new Point(18, y); l6.Size = new Size(300, 22);
        Label lCount = new Label();
        lCount.Text = Pool.Count + " key(s) loaded";
        lCount.Location = new Point(430, y); lCount.Size = new Size(150, 22);
        lCount.ForeColor = Color.Gray;
        y += 24;
        TextBox tbKeys = new TextBox();
        tbKeys.Multiline = true; tbKeys.ScrollBars = ScrollBars.Vertical;
        tbKeys.Location = new Point(18, y); tbKeys.Size = new Size(556, 90);
        try { if (File.Exists(KEYS)) tbKeys.Text = File.ReadAllText(KEYS, Encoding.UTF8); } catch { }
        y += 100;

        Button bSave = new Button();
        bSave.Text = "Save"; bSave.Location = new Point(398, y); bSave.Size = new Size(84, 30);
        Button bCancel = new Button();
        bCancel.Text = "Cancel"; bCancel.Location = new Point(490, y); bCancel.Size = new Size(84, 30);
        Button bReset = new Button();
        bReset.Text = "Reset prompt"; bReset.Location = new Point(18, y); bReset.Size = new Size(120, 30);

        bReset.Click += delegate { tbPrompt.Text = DefaultPrompt(); };

        bSave.Click += delegate {
            S["model"]  = cbModel.Text.Trim();
            S["size"]   = cbSize.SelectedItem.ToString();
            S["aspect"] = (cbAsp.SelectedIndex == 0) ? "" : cbAsp.SelectedItem.ToString();
            S["notify"] = (cbNotify.SelectedIndex == 1) ? "poll" : "com";
            try {
                SaveSettings();
                File.WriteAllText(PROMPT, tbPrompt.Text.Trim(), Encoding.UTF8);
                File.WriteAllText(KEYS, tbKeys.Text, Encoding.UTF8);
                if (File.Exists(STATE)) File.Delete(STATE);
                MessageBox.Show("Saved.", "Gemini Fix");
                f.Close();
            } catch (Exception ex) { MessageBox.Show("Save failed:\n" + ex.Message); }
        };
        bCancel.Click += delegate { f.Close(); };

        f.Controls.AddRange(new Control[] { l1, cbModel, l2, cbSize, l2b, l3, cbAsp,
                                            l4, cbNotify, l5, tbPrompt, l6, lCount,
                                            tbKeys, bReset, bSave, bCancel });
        Application.Run(f);
    }

    // ---------------- main ----------------
    [STAThread]
    public static void Main(string[] args)
    {
        LoadSettings();

        if (args != null && args.Length > 0 && args[0].ToLower().IndexOf("setting") >= 0) {
            Application.EnableVisualStyles();
            ShowSettings();
            return;
        }

        string jobPath = Path.Combine(ROOT, @"tmp\gjob.txt");
        if (args != null && args.Length > 0 && File.Exists(args[0])) jobPath = args[0];

        string dir = Path.GetDirectoryName(jobPath);
        string flag = Path.Combine(dir, "gdone.flag");
        try { if (File.Exists(flag)) File.Delete(flag); } catch { }

        int ok = 0;
        List<string> made = new List<string>();
        string lastErr = "";

        try {
            string[] lines = File.ReadAllLines(jobPath, Encoding.UTF8);
            string cin = null;
            foreach (string raw in lines) {
                string ln = raw.Trim();
                if (ln.StartsWith("IN=")) cin = ln.Substring(3);
                else if (ln.StartsWith("OUT=") && cin != null) {
                    string outp = ln.Substring(4);
                    string e;
                    if (CallGemini(cin, outp, out e)) { ok++; made.Add(outp); }
                    else { lastErr = e; Log("FAIL " + cin + " :: " + e); }
                    cin = null;
                }
            }
        } catch (Exception ex) { lastErr = ex.Message; Log("job read: " + ex.Message); }

        try { File.WriteAllText(flag, ok + "|" + lastErr); } catch { }

        if (S["notify"] == "com" && made.Count > 0) {
            System.Threading.Thread.Sleep(300);
            foreach (string m in made) PushToPhotoshop(m);
        }

        if (ok == 0) {
            try {
                MessageBox.Show("Gemini could not process the image.\r\n\r\n" + lastErr,
                                "Gemini Fix", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            } catch { }
        }
    }
}