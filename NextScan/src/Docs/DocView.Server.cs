// =============================================================================
// NextScan.Docs - the parts of a Document Server the editor's own menus need.
// Plan ref: docs/DOCUMENT_WORKSPACE.md
//
// Print and File > Download As did nothing but say "Unknown error". Both send
// the document to a Document Server's downloadas endpoint and wait for the URL
// of a converted file; with no server, the POST went unanswered. It is answered
// here, the way ONLYOFFICE's own server answers it (sdkjs common/apiBase.js,
// editorscommon.js saveWithParts / sendCommand, release 9.4):
//
//   POST /editors/downloadas/<key>?cmd={"c":"save","outputformat":513,...}
//     body: the document in the editor's own format, or -- for PDF, PDF/A and
//     pictures -- the editor's drawing of its pages (format 0x2004), which x2t
//     turns into the file.
//     Large bodies arrive in parts: savetype 0 (first), 1 (middle), 2 (last);
//     3 is a body that fits in one. Every part but the last is answered with a
//     save key, which comes back on the next.
//   answer: {"type":"save","status":"ok","data":"<url of the file>"}
//
// The editor then does with the URL what it would online: prints it from a
// hidden frame (Print), or starts a download (Download As), which WebView2
// reports to DownloadStarting, where the operator is asked where to put it.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace NextScan.Docs
{
    public partial class DocView
    {
        readonly Dictionary<string, MemoryStream> _parts = new Dictionary<string, MemoryStream>();
        int _outCount;

        /// <summary>Converted files by the URL the editor was given for them.</summary>
        readonly Dictionary<string, string> _outFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void DownloadAs(CoreWebView2WebResourceRequestedEventArgs e, Uri uri)
        {
            string raw = Query(uri, "cmd");
            Dictionary<string, object> cmd;
            try { cmd = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(raw ?? "{}"); }
            catch { e.Response = ServerAnswer("err", "-1"); return; }

            byte[] body;
            using (Stream content = e.Request.Content)
            using (var copy = new MemoryStream())
            {
                if (content != null) content.CopyTo(copy);
                body = copy.ToArray();
            }

            int saveType = Int(cmd, "savetype", 3);
            string key = Str(cmd, "savekey");

            // ---- parts
            if (saveType == 0 || saveType == 1)
            {
                if (saveType == 0 || string.IsNullOrEmpty(key) || !_parts.ContainsKey(key))
                {
                    key = Guid.NewGuid().ToString("N");
                    _parts[key] = new MemoryStream();
                }
                _parts[key].Write(body, 0, body.Length);
                e.Response = ServerAnswer("ok", key);
                return;
            }
            if (saveType == 2 && !string.IsNullOrEmpty(key) && _parts.ContainsKey(key))
            {
                MemoryStream whole = _parts[key];
                _parts.Remove(key);
                whole.Write(body, 0, body.Length);
                body = whole.ToArray();
            }

            // ---- another file brought into this one: Compare, Combine, Text from File
            if (cmd.ContainsKey("outputurls"))
            {
                BringIn(e, cmd, body);
                return;
            }

            // ---- the whole document: convert it, off this thread
            int formatTo = Int(cmd, "outputformat", 0x201);
            string title = SafeName(Str(cmd, "title") ?? (Path.GetFileNameWithoutExtension(_title) + ".pdf"));
            bool print = Int(cmd, "inline", 0) == 1;
            string json = null;
            object jp;
            if (cmd.TryGetValue("jsonparams", out jp) && jp != null) json = new JavaScriptSerializer().Serialize(jp);
            string thumbnail = Thumbnail(cmd);
            int lcid = Int(cmd, "lcid", 0);

            int n = ++_outCount;
            string dir = Path.Combine(_work, "out", n.ToString(CultureInfo.InvariantCulture));
            string target = Path.Combine(dir, title);
            string url = Origin + "/out/" + _id + "/" + n + "/" + Uri.EscapeDataString(title);
            string work = _work;

            CoreWebView2Deferral deferral = e.GetDeferral();
            Say((print ? "Preparing to print " : "Preparing ") + title + "…");

            bool fromOrigin = Str(cmd, "c") == "savefromorigin";

            Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                bool document = body.Length > 4 && IsCanvasDocument(body);
                if (fromOrigin)
                {
                    // A PDF being edited: the body is only what changed. It is
                    // merged into the PDF as opened, as a server would; with no
                    // changes the original is the answer.
                    string original = Path.Combine(work, "source.pdf");
                    string merged = Path.Combine(dir, "merged.pdf");
                    if (body.Length > 26)
                    {
                        string changes = Path.Combine(dir, "changes.bin");
                        File.WriteAllBytes(changes, body);
                        DocsEngine.MergePdf(original, changes, merged);
                    }
                    else File.Copy(original, merged, true);

                    if (formatTo == 0x201 || formatTo == 0) File.Copy(merged, target, true);
                    else DocsEngine.ConvertWith(merged, target, formatTo, 0, json, thumbnail, lcid);
                }
                else if (document)
                {
                    // The document itself: it goes back beside its media, where
                    // x2t finds the pictures it refers to.
                    string bin = Path.Combine(work, "Editor.bin");
                    File.WriteAllBytes(bin, body);
                    DocsEngine.ConvertWith(bin, target, formatTo, 0, json, thumbnail, lcid);
                }
                else
                {
                    // The editor's drawing of its pages.
                    string bin = Path.Combine(dir, "Editor.bin");
                    File.WriteAllBytes(bin, body);
                    DocsEngine.ConvertWith(bin, target, formatTo, 0x2004, json, thumbnail, lcid);
                }
            }).ContinueWith(done =>
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (done.IsFaulted)
                        {
                            string why = done.Exception.GetBaseException().Message;
                            e.Response = ServerAnswer("err", "-80");
                            Fail("Could not make " + title + ": " + why);
                        }
                        else
                        {
                            _outFiles[url] = target;

                            // Word, Excel and PowerPoint hand the file back
                            // through getFile (shim.js); the PDF editor does not
                            // reliably. Either way it is offered once: if the
                            // editor has not asked within a moment, NextScan does.
                            if (!print)
                            {
                                var nudge = new Timer { Interval = 1500 };
                                nudge.Tick += delegate
                                {
                                    nudge.Stop();
                                    nudge.Dispose();
                                    if (!_offered.Contains(url)) SaveCopy(url);
                                };
                                nudge.Start();
                            }
                            if (Trace != null) Trace("converted " + target + " -> " + url);
                            e.Response = ServerAnswer("ok", url);
                            Say(print ? "Sending " + title + " to the printer…" : "");
                        }
                    }
                    finally { deferral.Complete(); }
                });
            });
        }

        /// <summary>
        /// Review > Compare and Combine, and Insert > Text from File: the editor
        /// reads the other file from disk and sends its bytes with
        /// "outputurls":true, and wants back, instead of one URL, a map of the
        /// file converted to its own format and the pictures in it:
        /// {"output.bin": url, "media/image1.png": url, ...}.
        /// </summary>
        void BringIn(CoreWebView2WebResourceRequestedEventArgs e, Dictionary<string, object> cmd, byte[] body)
        {
            string format = (Str(cmd, "format") ?? "").Trim('.').ToLowerInvariant();
            if (body.Length == 0 || DocsEngine.FormatOf(format) == 0)
            {
                e.Response = ServerAnswer("err", "-2");
                Fail(body.Length == 0 ? "That file could not be read." : "NextScan cannot bring in ." + format + " files.");
                return;
            }

            int n = ++_outCount;
            string dir = Path.Combine(_work, "out", n.ToString(CultureInfo.InvariantCulture));
            string urlBase = Origin + "/out/" + _id + "/" + n + "/";
            CoreWebView2Deferral deferral = e.GetDeferral();
            Say("Reading the other file…");

            Task.Run(() =>
            {
                Directory.CreateDirectory(dir);
                string input = Path.Combine(dir, "input." + format);
                File.WriteAllBytes(input, body);
                DocsEngine.Convert(input, Path.Combine(dir, "Editor.bin"));
                File.Delete(input);
            }).ContinueWith(done =>
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        if (done.IsFaulted)
                        {
                            e.Response = ServerAnswer("err", "-80");
                            Fail("Could not read the other file: " + done.Exception.GetBaseException().Message);
                            return;
                        }
                        var map = new Dictionary<string, object>();
                        foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        {
                            string rel = file.Substring(dir.Length + 1).Replace('\\', '/');
                            string url = urlBase + string.Join("/", Array.ConvertAll(rel.Split('/'), Uri.EscapeDataString));
                            map[rel == "Editor.bin" ? "output.bin" : rel] = url;
                            _outFiles[url] = file;
                        }
                        string json = new JavaScriptSerializer().Serialize(new Dictionary<string, object>
                        {
                            { "type", Str(cmd, "c") ?? "save" }, { "status", "ok" }, { "data", map },
                        });
                        e.Response = Text(200, "application/json", json);
                        Say("");
                    }
                    finally { deferral.Complete(); }
                });
            });
        }

        /// <summary>The first bytes of a document in the editor's format: DOCY, XLSY, PPTY, VSDY.</summary>
        static bool IsCanvasDocument(byte[] b)
        {
            string head = Encoding.ASCII.GetString(b, 0, Math.Min(4, b.Length));
            return head == "DOCY" || head == "XLSY" || head == "PPTY" || head == "VSDY";
        }

        /// <summary>Pictures of pages: x2t's thumbnail options, as the editor asked for them.</summary>
        static string Thumbnail(Dictionary<string, object> cmd)
        {
            object t;
            if (!cmd.TryGetValue("thumbnail", out t)) return null;
            var d = t as Dictionary<string, object>;
            if (d == null) return null;
            var x = new StringBuilder("<m_oThumbnail>");
            foreach (var kv in d)
            {
                string v = kv.Value is bool ? ((bool)kv.Value ? "true" : "false") : Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
                x.Append('<').Append(kv.Key).Append('>').Append(v).Append("</").Append(kv.Key).Append('>');
            }
            return x.Append("</m_oThumbnail>").ToString();
        }

        CoreWebView2WebResourceResponse ServerAnswer(string status, string data)
        {
            string json = "{\"type\":\"save\",\"status\":\"" + status + "\",\"data\":\"" + Escape(data) + "\"}";
            return Text(200, "application/json", json);
        }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        static int Int(Dictionary<string, object> d, string key, int otherwise)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return otherwise;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return otherwise; }
        }

        static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "document" : name;
        }

        // ---- Download As: where to put it -------------------------------------

        /// <summary>
        /// The editor starts a download of a file it was just given. It is a
        /// file already on this disk, so the browser's download is cancelled
        /// and the operator chooses where the copy goes.
        /// </summary>
        void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            // Nothing is downloaded from anywhere: every file this page can
            // offer is already on this disk, and SaveCopy puts it where the
            // operator says.
            string url = e.DownloadOperation.Uri;
            e.Cancel = true;
            e.Handled = true;
            BeginInvoke((MethodInvoker)delegate { SaveCopy(url); });
        }

        /// <summary>Asks where a converted file goes, and copies it there.</summary>
        readonly HashSet<string> _offered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void SaveCopy(string url)
        {
            if (!string.IsNullOrEmpty(url) && !_offered.Add(url)) return;   // once per file
            string made;
            if (string.IsNullOrEmpty(url) || !_outFiles.TryGetValue(url, out made) || !File.Exists(made))
            {
                Fail("That file could not be found.");
                return;
            }
            try
            {
                string ext = Path.GetExtension(made).TrimStart('.');
                string to = AskSavePath != null ? AskSavePath(Path.GetFileNameWithoutExtension(made), ext) : null;
                if (string.IsNullOrEmpty(to)) { Say("Not saved."); return; }
                File.Copy(made, to, true);
                Say("Saved a copy as " + Path.GetFileName(to));
                if (CopySaved != null) CopySaved(this, new DocsMessageEventArgs(to));
            }
            catch (Exception ex) { Fail("Could not save the copy: " + ex.Message); }
        }

        // ---- Print --------------------------------------------------------------

        Microsoft.Web.WebView2.WinForms.WebView2 _printer;

        /// <summary>
        /// Prints the PDF the editor made of this document.
        ///
        /// Windows' own Print dialog chooses the printer, the copies and the
        /// pages; a second browser, behind the editor, holds the PDF and prints
        /// it with those settings. WebView2's ShowPrintUI was tried first and
        /// reported success without ever showing its dialog from a browser that
        /// is not in front, so the choice is made here and handed to it.
        /// </summary>
        async void PrintFile(string url)
        {
            string made;
            if (string.IsNullOrEmpty(url) || !_outFiles.TryGetValue(url, out made) || !File.Exists(made))
            {
                Fail("The pages to print could not be found.");
                return;
            }

            try
            {
                string printer;
                short copies;
                string pages;
                using (var dialog = new PrintDialog
                {
                    AllowSomePages = true,
                    AllowSelection = false,
                    UseEXDialog = true,
                    Document = new System.Drawing.Printing.PrintDocument()
                })
                {
                    dialog.PrinterSettings.MinimumPage = 1;
                    dialog.PrinterSettings.MaximumPage = 9999;
                    dialog.PrinterSettings.FromPage = 1;
                    dialog.PrinterSettings.ToPage = 9999;
                    if (dialog.ShowDialog(FindForm()) != DialogResult.OK) { Say("Not printed."); return; }
                    printer = dialog.PrinterSettings.PrinterName;
                    copies = dialog.PrinterSettings.Copies;
                    pages = dialog.PrinterSettings.PrintRange == System.Drawing.Printing.PrintRange.SomePages
                        ? dialog.PrinterSettings.FromPage + "-" + dialog.PrinterSettings.ToPage
                        : "";
                }

                CoreWebView2Environment env = await Environment_();
                if (_printer == null)
                {
                    _printer = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
                    Controls.Add(_printer);
                    _printer.SendToBack();
                    await _printer.EnsureCoreWebView2Async(env);
                    _printer.CoreWebView2.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                    _printer.CoreWebView2.WebResourceRequested += OnRequest;
                }

                var loaded = new TaskCompletionSource<bool>();
                EventHandler<CoreWebView2NavigationCompletedEventArgs> done = null;
                done = delegate (object s, CoreWebView2NavigationCompletedEventArgs e)
                {
                    _printer.CoreWebView2.NavigationCompleted -= done;
                    loaded.TrySetResult(e.IsSuccess);
                };
                _printer.CoreWebView2.NavigationCompleted += done;
                _printer.CoreWebView2.Navigate(url);
                if (!await loaded.Task) { Fail("The pages to print could not be opened."); return; }
                await Task.Delay(700);      // the PDF viewer draws a moment after it loads

                CoreWebView2PrintSettings settings = env.CreatePrintSettings();
                settings.PrinterName = printer;
                settings.Copies = Math.Max(1, (int)copies);
                if (pages.Length > 0) settings.PageRanges = pages;
                settings.ShouldPrintBackgrounds = true;
                settings.ShouldPrintHeaderAndFooter = false;

                Say("Printing " + Path.GetFileNameWithoutExtension(made) + " on " + printer + "…");
                CoreWebView2PrintStatus status = await _printer.CoreWebView2.PrintAsync(settings);
                if (status == CoreWebView2PrintStatus.Succeeded) Say("Sent to " + printer + ".");
                else if (status == CoreWebView2PrintStatus.PrinterUnavailable) Fail(printer + " is not available.");
                else Fail("Printing did not finish (" + status + ").");
                if (Trace != null) Trace("print status " + status);
            }
            catch (Exception ex) { Fail("Could not print: " + ex.Message); }
        }

        /// <summary>A copy in another format was written (Download As). The message is its path.</summary>
        public event EventHandler<DocsMessageEventArgs> CopySaved;
    }
}
