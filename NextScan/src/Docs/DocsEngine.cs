// =============================================================================
// NextScan.Docs - the editor engine on disk: where it is, the font catalog it
// reads, and the converter that turns files into what it edits and back.
// Plan ref: docs/DOCUMENT_WORKSPACE.md
//
// Layout of the install (bin\docs):
//   editors\     ONLYOFFICE sdkjs and web-apps 9.4, unmodified
//   converter\   x2t.exe and its libraries, nsfonts.exe
//   host\        host.html, host.js, shim.js -- ours
//
// And of the operator's profile (%LocalAppData%\NextScan\docs):
//   fonts\       the catalog nsfonts builds from this machine's fonts
//   work\<id>\   one folder per open document: the converted Editor.bin, media
//   webview\     WebView2's own profile
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NextScan.Docs
{
    public static class DocsEngine
    {
        // ---- where things are ---------------------------------------------

        /// <summary>
        /// The engine's folder. Beside this assembly, which the application
        /// loads from bin\docs; NEXTSCAN_DOCS may point elsewhere for tests.
        /// </summary>
        public static string Root
        {
            get
            {
                string forced = Environment.GetEnvironmentVariable("NEXTSCAN_DOCS");
                if (!string.IsNullOrEmpty(forced)) return forced;
                return Path.GetDirectoryName(typeof(DocsEngine).Assembly.Location);
            }
        }

        public static string Editors { get { return Path.Combine(Root, "editors"); } }
        public static string Converter { get { return Path.Combine(Root, "converter"); } }
        public static string Host { get { return Path.Combine(Root, "host"); } }

        public static string Data
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "NextScan", "docs");
            }
        }

        public static string Fonts { get { return Path.Combine(Data, "fonts"); } }
        public static string Work { get { return Path.Combine(Data, "work"); } }
        public static string WebProfile { get { return Path.Combine(Data, "webview"); } }

        /// <summary>True when the editor and the converter are both where they should be.</summary>
        public static bool IsInstalled
        {
            get
            {
                return File.Exists(Path.Combine(Editors, "web-apps", "apps", "api", "documents", "api.js")) &&
                       File.Exists(Path.Combine(Converter, "x2t.exe")) &&
                       File.Exists(Path.Combine(Host, "host.html"));
            }
        }

        // ---- fonts --------------------------------------------------------

        static Task<bool> _fontsTask;
        static readonly object FontsLock = new object();

        /// <summary>
        /// Makes sure the font catalog describes the fonts installed now.
        ///
        /// Built once, and again only when the fonts change: a shop installs a
        /// Bijoy font, and the next document opened must be able to use it.
        /// Twelve seconds on the development machine for 1,110 faces, which is
        /// why it starts with the application rather than with the first
        /// document.
        /// </summary>
        public static Task<bool> EnsureFontsAsync()
        {
            lock (FontsLock)
            {
                if (_fontsTask != null && !(_fontsTask.IsCompleted && !_fontsTask.Result)) return _fontsTask;
                _fontsTask = Task.Run(() => BuildFontsIfStale());
                return _fontsTask;
            }
        }

        static bool BuildFontsIfStale()
        {
            try
            {
                string signature = FontSignature();
                string stamp = Path.Combine(Fonts, "signature.txt");
                string catalog = Path.Combine(Fonts, "AllFonts.js");

                if (File.Exists(catalog) && File.Exists(stamp) &&
                    File.ReadAllText(stamp).Trim() == signature)
                    return true;

                Directory.CreateDirectory(Fonts);
                string tool = Path.Combine(Converter, "nsfonts.exe");
                if (!File.Exists(tool)) return false;

                var start = new ProcessStartInfo(tool, Quote(Fonts))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Converter,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(start))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5 * 60 * 1000)) { try { p.Kill(); } catch { } return false; }
                    if (p.ExitCode != 0 || !File.Exists(catalog)) return false;
                }

                File.WriteAllText(stamp, signature);
                lock (FontsLock) { _webCatalog = null; _fontPaths = null; }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// What changes when a font is added or removed: the count of files in
        /// the two font folders and the newest one's time.
        /// </summary>
        static string FontSignature()
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                             "Microsoft", "Windows", "Fonts")
            };
            long count = 0, newest = 0;
            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    count++;
                    long t = File.GetLastWriteTimeUtc(file).Ticks;
                    if (t > newest) newest = t;
                }
            }
            return "v1 " + count.ToString(CultureInfo.InvariantCulture) + " " + newest.ToString(CultureInfo.InvariantCulture);
        }

        static string _webCatalog;
        static List<string> _fontPaths;

        /// <summary>
        /// The catalog as the editor in a browser wants it: each face named by
        /// its number rather than its path. Measured against allfontsgen's own
        /// web output, that is the only difference between the two.
        /// </summary>
        public static string WebCatalog()
        {
            lock (FontsLock)
            {
                if (_webCatalog != null) return _webCatalog;
                string catalog = Path.Combine(Fonts, "AllFonts.js");
                if (!File.Exists(catalog)) return null;

                string text = File.ReadAllText(catalog, Encoding.UTF8);
                const string head = "window[\"__fonts_files\"] = [";
                int start = text.IndexOf(head, StringComparison.Ordinal);
                int end = start < 0 ? -1 : text.IndexOf("];", start, StringComparison.Ordinal);
                if (start < 0 || end < 0) return null;

                string list = text.Substring(start + head.Length, end - start - head.Length);
                var paths = new List<string>();
                foreach (Match m in Regex.Matches(list, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                    paths.Add(m.Groups[1].Value.Replace("\\\\", "\\"));

                var numbered = new StringBuilder();
                numbered.Append(head).Append('\n');
                for (int i = 0; i < paths.Count; i++)
                {
                    numbered.Append('"').Append(i.ToString("0000", CultureInfo.InvariantCulture)).Append('"');
                    numbered.Append(i + 1 < paths.Count ? ",\n" : "\n");
                }

                _fontPaths = paths;
                _webCatalog = text.Substring(0, start) + numbered + text.Substring(end);
                return _webCatalog;
            }
        }

        // The 16 bytes the editor's font loader XORs over the first 32 bytes
        // of every face it fetches. Read off allfontsgen's output: its web
        // copies differ from the originals in exactly those bytes, by exactly
        // this key, for every one of the faces compared.
        static readonly byte[] FontKey =
        {
            0xa0, 0x66, 0xd6, 0x20, 0x14, 0x96, 0x47, 0xfa,
            0x95, 0x69, 0xb8, 0x50, 0xb0, 0x41, 0x49, 0x48
        };

        /// <summary>One face, in the form the editor fetches it. Null if there is no such number.</summary>
        public static byte[] WebFont(int number)
        {
            if (WebCatalog() == null) return null;
            List<string> paths;
            lock (FontsLock) paths = _fontPaths;
            if (paths == null || number < 0 || number >= paths.Count) return null;

            byte[] data;
            try { data = File.ReadAllBytes(paths[number]); }
            catch { return null; }

            for (int i = 0; i < Math.Min(32, data.Length); i++) data[i] ^= FontKey[i % 16];
            return data;
        }

        // ---- conversion ---------------------------------------------------

        /// <summary>
        /// Runs x2t: a document into the editor's format, or back. The
        /// direction comes from the two names' extensions, as x2t works it out.
        /// </summary>
        public static void Convert(string from, string to, CancellationToken cancel = default(CancellationToken))
        {
            string x2t = Path.Combine(Converter, "x2t.exe");
            if (!File.Exists(x2t)) throw new DocsTrouble("The document converter is not installed.");

            Directory.CreateDirectory(Path.GetDirectoryName(to));
            if (File.Exists(to)) File.Delete(to);

            string args = Quote(from) + " " + Quote(to);
            string selection = Path.Combine(Fonts, "font_selection.bin");
            if (File.Exists(selection)) args += " " + Quote(Fonts);

            var start = new ProcessStartInfo(x2t, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Converter,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            string said;
            int code;
            using (Process p = Process.Start(start))
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                Task<string> outp = p.StandardOutput.ReadToEndAsync();
                while (!p.WaitForExit(100))
                {
                    if (cancel.IsCancellationRequested) { try { p.Kill(); } catch { } cancel.ThrowIfCancellationRequested(); }
                }
                said = (err.Result + " " + outp.Result).Trim();
                code = p.ExitCode;
            }

            if (code != 0 || !File.Exists(to))
                throw new DocsTrouble("The converter could not " + (to.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    ? "open " + Path.GetFileName(from) : "write " + Path.GetFileName(to)) +
                    " (x2t " + code.ToString(CultureInfo.InvariantCulture) + (said.Length > 0 ? ": " + said : "") + ").");
        }

        /// <summary>
        /// Merges the changes the PDF editor made into the PDF they were made
        /// to, the way a Document Server does it: x2t, told the source comes
        /// with a changes folder beside it (m_bFromChanges).
        /// </summary>
        public static void MergePdf(string basePdf, string changes, string output)
        {
            string dir = Path.Combine(Path.GetDirectoryName(basePdf), "merge");
            Directory.CreateDirectory(Path.Combine(dir, "changes"));
            string src = Path.Combine(dir, "src.pdf");
            File.Copy(basePdf, src, true);
            File.Copy(changes, Path.Combine(dir, "changes", "changes0.bin"), true);

            string xml =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                "<TaskQueueDataConvert xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">" +
                "<m_sFileFrom>" + Xml(src) + "</m_sFileFrom>" +
                "<m_sFileTo>" + Xml(output) + "</m_sFileTo>" +
                "<m_sFontDir>" + Xml(Fonts) + "</m_sFontDir>" +
                "<m_sAllFontsPath>" + Xml(Path.Combine(Fonts, "AllFonts.js")) + "</m_sAllFontsPath>" +
                "<m_sThemeDir>" + Xml(Path.Combine(Editors, "sdkjs", "slide", "themes")) + "</m_sThemeDir>" +
                "<m_bIsNoBase64>true</m_bIsNoBase64>" +
                "<m_bFromChanges>true</m_bFromChanges>" +
                "</TaskQueueDataConvert>";
            string parameters = Path.Combine(dir, "params.xml");
            File.WriteAllText(parameters, xml, new UTF8Encoding(false));

            Run(Quote(parameters), output, "merge the changes into " + Path.GetFileName(output));
        }

        static string Xml(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        static void Run(string args, string expected, string doing)
        {
            string x2t = Path.Combine(Converter, "x2t.exe");
            if (!File.Exists(x2t)) throw new DocsTrouble("The document converter is not installed.");
            if (File.Exists(expected)) File.Delete(expected);

            var start = new ProcessStartInfo(x2t, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Converter,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process p = Process.Start(start))
            {
                Task<string> err = p.StandardError.ReadToEndAsync();
                Task<string> outp = p.StandardOutput.ReadToEndAsync();
                p.WaitForExit();
                string said = (err.Result + " " + outp.Result).Trim();
                if (p.ExitCode != 0 || !File.Exists(expected))
                    throw new DocsTrouble("The converter could not " + doing + " (x2t " +
                        p.ExitCode.ToString(CultureInfo.InvariantCulture) + (said.Length > 0 ? ": " + said : "") + ").");
            }
        }

        /// <summary>A fresh working folder for one document.</summary>
        public static string NewWork(out string id)
        {
            id = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" +
                 Guid.NewGuid().ToString("N").Substring(0, 8);
            string dir = Path.Combine(Work, id);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Removes working folders left behind by a session that did not end
        /// cleanly. Older than a day, so a second window's documents are safe.
        /// </summary>
        public static void SweepWork()
        {
            try
            {
                if (!Directory.Exists(Work)) return;
                foreach (string dir in Directory.GetDirectories(Work))
                    if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1))
                        try { Directory.Delete(dir, true); } catch { }
            }
            catch { }
        }

        /// <summary>The blank file a new document starts from, in the operator's language where there is one.</summary>
        public static string Blank(string ext)
        {
            string culture = CultureInfo.CurrentUICulture.Name;
            foreach (string name in new[] { culture, "en-US" })
            {
                string path = Path.Combine(Converter, "empty", name, "new." + ext);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        internal static string Quote(string s) { return "\"" + s + "\""; }
    }

    public class DocsTrouble : Exception
    {
        public DocsTrouble(string message) : base(message) { }
        public DocsTrouble(string message, Exception inner) : base(message, inner) { }
    }
}
