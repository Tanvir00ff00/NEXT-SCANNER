// =============================================================================
// NextScan.Docs - one open document: the editor in a WebView2, and everything
// a Document Server would otherwise answer for it.
// Plan ref: docs/DOCUMENT_WORKSPACE.md
//
// Every request the page makes goes to https://nextscan.docs/ and is answered
// here, from disk, with no server and no network:
//
//   /editors/...                    ONLYOFFICE's files, as installed
//   /editors/sdkjs/common/AllFonts.js   this machine's font catalog
//   /editors/sdkjs/common/Images/fonts_thumbnail*   its picker thumbnails
//   /editors/fonts/<n>              one of this machine's fonts, as the editor reads them
//   /host/...                       host.html and host.js
//   /work/<id>/...                  this document, converted: Editor.bin, media
//   POST /save/<id>?ext=            the edited document, to convert and write
//
// Nothing in the public surface names a WebView2 type, so the shell, which
// builds with csc and no packages, can hold one of these without them.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NextScan.Docs
{
    public partial class DocView : UserControl
    {
        const string Origin = "https://nextscan.docs";

        readonly WebView2 _web;
        readonly Label _cover;          // what shows until the editor has drawn the document
        string _id = "";
        string _work = "";
        string _ext = "docx";
        string _path = "";
        string _title = "";
        bool _dirty;
        bool _ready;
        bool _dark;

        /// <summary>The file this document saves to. Empty for a new one never saved.</summary>
        public string FilePath { get { return _path; } }

        /// <summary>The format it is edited and saved in, without the dot: docx, xlsx, pptx, pdf.</summary>
        public string Extension { get { return _ext; } }

        public bool Dirty { get { return _dirty; } }
        public bool Ready { get { return _ready; } }

        /// <summary>Asked for a place to save when there is none yet. Returns a path, or null to cancel.</summary>
        public Func<string, string, string> AskSavePath;

        public Action<string> Status;

        /// <summary>Every request answered "not found", for diagnosing a document that will not open.</summary>
        public static Action<string> Trace;

        public event EventHandler DirtyChanged;
        public event EventHandler ReadyChanged;
        public event EventHandler PathChanged;

        /// <summary>File > Create new, in the editor. The message is the kind: word, cell, slide or pdf.</summary>
        public event EventHandler<DocsMessageEventArgs> NewWanted;

        /// <summary>File > Close file, in the editor.</summary>
        public event EventHandler CloseWanted;

        /// <summary>Something went wrong that the operator should be told about.</summary>
        public event EventHandler<DocsMessageEventArgs> Trouble;

        public DocView()
        {
            BackColor = Color.FromArgb(0xF3, 0xF3, 0xF3);

            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            _cover = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(0x5C, 0x5C, 0x5C),
                BackColor = BackColor,
                Text = "Opening…"
            };

            Controls.Add(_web);
            Controls.Add(_cover);
            _cover.BringToFront();
        }

        public void SetDark(bool dark)
        {
            _dark = dark;
            BackColor = dark ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xF3, 0xF3, 0xF3);
            _cover.BackColor = BackColor;
            _cover.ForeColor = dark ? Color.FromArgb(0xB8, 0xB8, 0xB8) : Color.FromArgb(0x5C, 0x5C, 0x5C);
        }

        // ---- the shared browser ---------------------------------------------

        static Task<CoreWebView2Environment> _environment;

        /// <summary>
        /// One WebView2 environment for every document: one browser process,
        /// one cache, one profile folder.
        /// </summary>
        static Task<CoreWebView2Environment> Environment_()
        {
            if (_environment != null) return _environment;

            // The loader is found beside the SDK by default, and the SDK is in
            // bin\docs, which the process was not started from.
            string arch = IntPtr.Size == 8 ? "win-x64" : "win-x86";
            string loader = Path.Combine(DocsEngine.Root, "runtimes", arch, "native");
            if (Directory.Exists(loader))
                try { CoreWebView2Environment.SetLoaderDllFolderPath(loader); } catch { }

            Directory.CreateDirectory(DocsEngine.WebProfile);
            var options = new CoreWebView2EnvironmentOptions
            {
                // A document from disk opening a local editor: nothing here is
                // a site, and the smaller the browser's surface the better.
                AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection --disable-background-networking"
            };
            _environment = CoreWebView2Environment.CreateAsync(null, DocsEngine.WebProfile, options);
            return _environment;
        }

        /// <summary>Starts the browser process early, so the first document does not wait for it.</summary>
        public static Task Warm() { return Environment_(); }

        // ---- opening ----------------------------------------------------------

        /// <summary>Opens a file. The file itself is never written until it is saved.</summary>
        public async Task OpenAsync(string path)
        {
            _path = path;
            _title = Path.GetFileName(path);
            _ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            await Begin(path);
        }

        /// <summary>A new, empty document. It has no file until the first save.</summary>
        public async Task NewAsync(string ext, string title)
        {
            string blank = DocsEngine.Blank(ext);
            if (blank == null) throw new DocsTrouble("There is no blank " + ext + " to start from.");
            _path = "";
            _title = title + "." + ext;
            _ext = ext;
            await Begin(blank);
        }

        async Task Begin(string source)
        {
            if (!DocsEngine.IsInstalled) throw new DocsTrouble("The document editor is not installed.");

            Say("Preparing " + _title + "…");
            _work = DocsEngine.NewWork(out _id);

            // Fonts first: the editor asks for the catalog as it starts, and a
            // catalog built halfway through is one the editor never sees.
            Task<bool> fonts = DocsEngine.EnsureFontsAsync();

            string ext = _ext;
            string work = _work;
            await Task.Run(() =>
            {
                if (ext == "pdf") File.Copy(source, Path.Combine(work, "source.pdf"), true);
                else DocsEngine.Convert(source, Path.Combine(work, "Editor.bin"));
            });

            await fonts;

            CoreWebView2Environment env = await Environment_();
            await _web.EnsureCoreWebView2Async(env);
            Wire(_web.CoreWebView2);

            string url = Origin + "/host/host.html?id=" + Uri.EscapeDataString(_id) +
                         "&ext=" + Uri.EscapeDataString(_ext) +
                         "&title=" + Uri.EscapeDataString(_title) +
                         "&lang=" + Uri.EscapeDataString(Language()) +
                         (_dark ? "&theme=dark" : "");
            _web.CoreWebView2.Navigate(url);
            Say("Opening " + _title + "…");
        }

        static string Language()
        {
            string two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return string.IsNullOrEmpty(two) || two == "iv" ? "en" : two;
        }

        bool _wired;

        void Wire(CoreWebView2 core)
        {
            if (_wired) return;
            _wired = true;

            CoreWebView2Settings s = core.Settings;
            s.AreDevToolsEnabled = System.Environment.GetEnvironmentVariable("NEXTSCAN_DOCS_DEVTOOLS") == "1";
            s.AreDefaultContextMenusEnabled = true;     // the editor draws its own; this is the fallback
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;             // the editor has its own zoom
            s.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+S, Ctrl+P and the rest belong to the editor
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;

            string shim = File.ReadAllText(Path.Combine(DocsEngine.Host, "shim.js"), Encoding.UTF8);
            core.AddScriptToExecuteOnDocumentCreatedAsync(shim);

            core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += delegate (object sender, CoreWebView2NewWindowRequestedEventArgs e)
            {
                // A link in a document opens in the operator's browser, not in
                // a second editor window with nothing behind it.
                e.Handled = true;
                try
                {
                    if (e.Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                        System.Diagnostics.Process.Start(e.Uri);
                }
                catch { }
            };
            core.DownloadStarting += OnDownloadStarting;
            core.ProcessFailed += delegate (object sender, CoreWebView2ProcessFailedEventArgs e)
            {
                Fail("The editor stopped (" + e.ProcessFailedKind + "). Unsaved changes in this tab may be lost.");
            };
        }

        // ---- answering the page's requests ------------------------------------

        void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out uri)) return;
            string path = Uri.UnescapeDataString(uri.AbsolutePath);

            try
            {
                if (Trace != null && e.Request.Method == "POST")
                {
                    long length = -1;
                    try { if (e.Request.Content != null) length = e.Request.Content.Length; } catch { }
                    var headers = new StringBuilder();
                    foreach (var h in e.Request.Headers) headers.Append(h.Key).Append('=').Append(h.Value).Append("; ");
                    Trace("POST " + uri.PathAndQuery + " body " + length + " | " + headers);
                    string dump = System.Environment.GetEnvironmentVariable("NEXTSCAN_DOCS_DUMP");
                    if (!string.IsNullOrEmpty(dump) && e.Request.Content != null)
                        using (var f = File.Create(Path.Combine(dump, "post-" + DateTime.Now.Ticks + ".bin")))
                        { e.Request.Content.CopyTo(f); e.Request.Content.Position = 0; }
                }
                if (e.Request.Method == "POST" && path.StartsWith("/save/", StringComparison.Ordinal))
                {
                    Save(e, uri);
                    return;
                }
                if (e.Request.Method == "POST" && path.StartsWith("/editors/downloadas/", StringComparison.Ordinal))
                {
                    DownloadAs(e, uri);
                    return;
                }
                if (path.StartsWith("/out/" + _id + "/", StringComparison.Ordinal))
                {
                    e.Response = FileUnder(Path.Combine(_work, "out"), path.Substring(("/out/" + _id + "/").Length));
                    return;
                }
                e.Response = Answer(path);
                if (Trace != null && e.Response != null && e.Response.StatusCode != 200)
                    Trace(e.Response.StatusCode + " " + e.Request.Method + " " + path);
            }
            catch (Exception ex)
            {
                e.Response = Text(500, "text/plain", ex.Message);
            }
        }

        CoreWebView2WebResourceResponse Answer(string path)
        {
            // ---- fonts
            if (path == "/editors/sdkjs/common/AllFonts.js")
            {
                string catalog = DocsEngine.WebCatalog();
                return catalog == null ? Missing() : Text(200, "application/javascript", catalog);
            }
            if (path.StartsWith("/editors/fonts/", StringComparison.Ordinal))
            {
                int n;
                byte[] face = int.TryParse(path.Substring("/editors/fonts/".Length), NumberStyles.None,
                                           CultureInfo.InvariantCulture, out n) ? DocsEngine.WebFont(n) : null;
                return face == null ? Missing() : Bytes(face, "application/octet-stream", true);
            }
            if (path.StartsWith("/editors/sdkjs/common/Images/fonts_thumbnail", StringComparison.Ordinal))
            {
                string made = Path.Combine(DocsEngine.Fonts, "images", Path.GetFileName(path));
                if (File.Exists(made)) return File_(made);
                // Fall through to the installed one: better an old picker than none.
            }

            // ---- this document
            if (path.StartsWith("/work/" + _id + "/", StringComparison.Ordinal))
                return FileUnder(_work, path.Substring(("/work/" + _id + "/").Length));

            // Images a converted document refers to, which the editor asks for
            // relative to its own page because the document's "url" is
            // "_offline_".
            int media = path.IndexOf("/_offline_media/", StringComparison.Ordinal);
            if (media >= 0)
                return FileUnder(Path.Combine(_work, "media"), path.Substring(media + "/_offline_media/".Length));

            // ---- installed files
            if (path.StartsWith("/editors/", StringComparison.Ordinal))
            {
                // No service worker: it would cache the editor under our feet
                // and answer from its cache instead of from here.
                if (path.EndsWith("service_worker.js", StringComparison.Ordinal)) return Missing();
                return FileUnder(DocsEngine.Editors, path.Substring("/editors/".Length));
            }
            if (path.StartsWith("/host/", StringComparison.Ordinal))
                return FileUnder(DocsEngine.Host, path.Substring("/host/".Length));

            return Missing();
        }

        CoreWebView2WebResourceResponse FileUnder(string root, string relative)
        {
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string top = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            // Nothing outside the folder it was asked of, whatever the path says.
            if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase)) return Text(403, "text/plain", "no");
            if (!File.Exists(full)) return Missing();
            return File_(full);
        }

        CoreWebView2WebResourceResponse File_(string full)
        {
            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
            return Env().CreateWebResourceResponse(stream, 200, "OK", Headers(Mime(full), true));
        }

        CoreWebView2WebResourceResponse Bytes(byte[] data, string mime, bool cache)
        {
            return Env().CreateWebResourceResponse(new MemoryStream(data), 200, "OK", Headers(mime, cache));
        }

        CoreWebView2WebResourceResponse Text(int status, string mime, string text)
        {
            return Env().CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(text)), status,
                status == 200 ? "OK" : "Error", Headers(mime + "; charset=utf-8", false));
        }

        CoreWebView2WebResourceResponse Missing() { return Text(404, "text/plain", "not found"); }

        CoreWebView2Environment Env() { return _web.CoreWebView2.Environment; }

        static string Headers(string mime, bool cache)
        {
            return "Content-Type: " + mime + "\r\n" +
                   (cache ? "Cache-Control: max-age=86400\r\n" : "Cache-Control: no-store\r\n") +
                   "Access-Control-Allow-Origin: " + Origin;
        }

        static readonly Dictionary<string, string> Mimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".htm", "text/html; charset=utf-8" },
            { ".js", "application/javascript" }, { ".mjs", "application/javascript" },
            { ".css", "text/css" }, { ".json", "application/json" },
            { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" },
            { ".gif", "image/gif" }, { ".svg", "image/svg+xml" }, { ".ico", "image/x-icon" },
            { ".wasm", "application/wasm" }, { ".woff", "font/woff" }, { ".woff2", "font/woff2" },
            { ".ttf", "font/ttf" }, { ".otf", "font/otf" }, { ".pdf", "application/pdf" },
            { ".xml", "application/xml" }, { ".txt", "text/plain; charset=utf-8" },
        };

        static string Mime(string file)
        {
            string mime;
            return Mimes.TryGetValue(Path.GetExtension(file), out mime) ? mime : "application/octet-stream";
        }

        // ---- saving -------------------------------------------------------------

        /// <summary>Saves to the document's own file, asking for one first if it has none.</summary>
        public void Save() { Request("save", null); }

        /// <summary>Saves to a new file, in the format its extension names.</summary>
        public void SaveAs(string path)
        {
            _saveAsPath = path;
            Request("saveAs", Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        }

        string _saveAsPath;

        void Request(string type, string ext)
        {
            if (_web.CoreWebView2 == null || !_ready) { Say("The document is still opening."); return; }
            string message = "{\"type\":\"" + type + "\"" + (ext != null ? ",\"ext\":\"" + ext + "\"" : "") + "}";
            _web.CoreWebView2.PostWebMessageAsString(message);
        }

        /// <summary>
        /// The edited document arrives here as the editor's own binary. Where
        /// it is written is decided on this thread -- a new document asks for a
        /// path now -- and the conversion runs off it.
        /// </summary>
        void Save(CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
        {
            string ext = Query(uri, "ext") ?? _ext;
            bool pdfChanges = Query(uri, "pdfchanges") == "1";

            string target = _saveAsPath;
            _saveAsPath = null;
            if (target == null)
            {
                target = _path;
                if (string.IsNullOrEmpty(target) || !string.Equals(Path.GetExtension(target).TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase))
                {
                    target = AskSavePath != null ? AskSavePath(Path.GetFileNameWithoutExtension(_title), ext) : null;
                    if (string.IsNullOrEmpty(target)) { e.Response = Json(false, "Not saved.", ""); return; }
                }
            }

            byte[] body;
            using (Stream content = e.Request.Content)
            using (var copy = new MemoryStream())
            {
                if (content == null) { e.Response = Json(false, "The editor sent nothing.", ""); return; }
                content.CopyTo(copy);
                body = copy.ToArray();
            }

            CoreWebView2Deferral deferral = e.GetDeferral();
            Say("Saving " + Path.GetFileName(target) + "…");

            string work = _work;
            string sourceExt = _ext;
            Task.Run(() => Write(work, sourceExt, body, pdfChanges, target)).ContinueWith(done =>
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (done.IsFaulted)
                        {
                            string why = done.Exception.GetBaseException().Message;
                            e.Response = Json(false, why, "");
                            Fail("Could not save: " + why);
                        }
                        else
                        {
                            e.Response = Json(true, "", target);
                            bool moved = !string.Equals(_path, target, StringComparison.OrdinalIgnoreCase);
                            _path = target;
                            _title = Path.GetFileName(target);
                            _ext = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
                            SetDirty(false);
                            if (moved && PathChanged != null) PathChanged(this, EventArgs.Empty);
                            Say("Saved " + _title);
                        }
                    }
                    finally { deferral.Complete(); }
                });
            });
        }

        static void Write(string work, string sourceExt, byte[] body, bool pdfChanges, string target)
        {
            string dir = Path.Combine(work, "save-" + DateTime.Now.Ticks.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(target);
            string made = Path.Combine(dir, "out" + ext);

            if (sourceExt == "pdf")
            {
                if (!pdfChanges) File.WriteAllBytes(made, body);
                else
                {
                    // Base and changes, as host.js packed them: two lengths, then both.
                    int baseLength = BitConverter.ToInt32(body, 0);
                    int changesLength = BitConverter.ToInt32(body, 4);
                    string basePdf = Path.Combine(dir, "base.pdf");
                    string changes = Path.Combine(dir, "changes.bin");
                    using (var f = File.Create(basePdf)) f.Write(body, 8, baseLength);
                    using (var f = File.Create(changes)) f.Write(body, 8 + baseLength, changesLength);
                    DocsEngine.MergePdf(basePdf, changes, made);
                }
            }
            else
            {
                // The binary goes back into the document's own working folder,
                // beside the media it refers to, and x2t reads it from there.
                string bin = Path.Combine(work, "Editor.bin");
                File.WriteAllBytes(bin, body);
                int format = DocsEngine.FormatOf(ext);
                if (format > 0) DocsEngine.ConvertWith(bin, made, format);
                else DocsEngine.Convert(bin, made);
            }

            // Written beside the destination, then swapped in: a save that
            // fails halfway must not leave the operator's file half-written.
            string temp = target + ".nextscan-saving";
            File.Copy(made, temp, true);
            if (File.Exists(target)) File.Replace(temp, target, null);
            else File.Move(temp, target);

            try { Directory.Delete(dir, true); } catch { }
        }

        static string Query(Uri uri, string key)
        {
            string q = uri.Query.TrimStart('?');
            foreach (string part in q.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && Uri.UnescapeDataString(part.Substring(0, eq)) == key)
                    return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        CoreWebView2WebResourceResponse Json(bool ok, string message, string path)
        {
            string json = "{\"ok\":" + (ok ? "true" : "false") +
                          ",\"message\":\"" + Escape(message) + "\",\"path\":\"" + Escape(path) + "\"}";
            return Text(200, "application/json", json);
        }

        static string Escape(string s)
        {
            var b = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else b.Append(c);
            }
            return b.ToString();
        }

        // ---- messages from the page -------------------------------------------

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string raw;
            try { raw = e.TryGetWebMessageAsString(); } catch { return; }
            if (string.IsNullOrEmpty(raw)) return;

            Dictionary<string, object> message;
            try { message = new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw); }
            catch { return; }
            if (message == null) return;
            Func<string, string> Field = delegate (string name)
            {
                object v;
                return message.TryGetValue(name, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
            };

            string type = Field("type");
            if (Trace != null) Trace("message " + (raw.Length > 200 ? raw.Substring(0, 200) : raw));
            switch (type)
            {
                case "ready":
                    _ready = true;
                    _cover.Visible = false;
                    Say("");
                    if (ReadyChanged != null) ReadyChanged(this, EventArgs.Empty);
                    break;
                case "download":
                    SaveCopy(Field("url"));
                    break;
                case "print":
                    PrintFile(Field("url"));
                    break;
                case "new":
                    if (NewWanted != null) NewWanted(this, new DocsMessageEventArgs(Field("kind")));
                    break;
                case "close":
                    if (CloseWanted != null) CloseWanted(this, EventArgs.Empty);
                    break;
                case "dirty":
                    object dirty;
                    SetDirty(message.TryGetValue("value", out dirty) && dirty is bool && (bool)dirty);
                    break;
                case "ran":
                    Ran(Field("id"), message);
                    break;
                case "failed":
                case "error":
                    Fail(Field("message"));
                    if (!_ready) _cover.Text = "This document could not be opened.\n\n" + Field("message");
                    break;
            }
        }

        void SetDirty(bool dirty)
        {
            if (_dirty == dirty) return;
            _dirty = dirty;
            if (DirtyChanged != null) DirtyChanged(this, EventArgs.Empty);
        }

        void Fail(string message)
        {
            Say(message);
            if (Trouble != null) Trouble(this, new DocsMessageEventArgs(message));
        }

        void Say(string text) { if (Status != null) Status(text); }

        // ---- going away ---------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _web.Dispose(); } catch { }
                string work = _work;
                if (!string.IsNullOrEmpty(work))
                    Task.Run(() => { try { Directory.Delete(work, true); } catch { } });
            }
            base.Dispose(disposing);
        }
    }

    public class DocsMessageEventArgs : EventArgs
    {
        public DocsMessageEventArgs(string message) { Message = message; }
        public string Message { get; private set; }
    }
}
