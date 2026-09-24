// =============================================================================
// NextScan.Docs - what the assistant can do to an open document.
// Plan ref: docs/DOCUMENT_WORKSPACE.md
//
// Everything goes through the editor itself, never around it: a script for
// ONLYOFFICE's document API (the one its plugins and Document Builder use) is
// sent to the page, run by the shim's __nsRun inside the editor's own undo
// history, and its result comes back under the request's id. So a change the
// assistant makes is on screen at once, marks the tab unsaved, and Ctrl+Z
// takes it back, exactly like a change the operator made.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace NextScan.Docs
{
    public partial class DocView
    {
        readonly Dictionary<string, TaskCompletionSource<string>> _pending = new Dictionary<string, TaskCompletionSource<string>>();
        int _requests;

        /// <summary>The name the tab shows: the file name, or "Document 1.docx" for a new one.</summary>
        public string Title { get { return _title; } }

        /// <summary>
        /// Runs code against the document's API and returns its result as JSON.
        /// The code is the body of a function given <c>Api</c>; what it returns
        /// comes back. Throws DocsTrouble with the editor's own message when the
        /// code fails.
        /// </summary>
        public Task<string> RunScriptAsync(string code, bool recalculate = true)
        {
            return Send("run", code, recalculate, false);
        }

        /// <summary>The text the operator has selected, or empty.</summary>
        public Task<string> SelectedTextAsync()
        {
            return Send("run", "", false, true);
        }

        Task<string> Send(string type, string code, bool recalculate, bool selection)
        {
            if (_web.CoreWebView2 == null || !_ready)
                return FromError("The document is still opening.");

            string id = "r" + (++_requests).ToString(CultureInfo.InvariantCulture);
            var done = new TaskCompletionSource<string>();
            _pending[id] = done;

            var message = new Dictionary<string, object>
            {
                { "type", type }, { "id", id }, { "code", code ?? "" },
                { "recalc", recalculate }, { "selection", selection },
            };
            _web.CoreWebView2.PostWebMessageAsString(new JavaScriptSerializer().Serialize(message));

            // A script that never answers -- an endless loop in it, or an editor
            // that has gone -- must not hold the assistant forever.
            var timeout = new System.Windows.Forms.Timer { Interval = 60000 };
            timeout.Tick += delegate
            {
                timeout.Stop();
                timeout.Dispose();
                TaskCompletionSource<string> late;
                if (_pending.TryGetValue(id, out late))
                {
                    _pending.Remove(id);
                    late.TrySetException(new DocsTrouble("The editor did not answer within a minute."));
                }
            };
            timeout.Start();
            return done.Task;
        }

        static Task<string> FromError(string message)
        {
            var failed = new TaskCompletionSource<string>();
            failed.SetException(new DocsTrouble(message));
            return failed.Task;
        }

        void Ran(string id, Dictionary<string, object> message)
        {
            TaskCompletionSource<string> done;
            if (!_pending.TryGetValue(id, out done)) return;
            _pending.Remove(id);

            object ok;
            if (message.TryGetValue("ok", out ok) && ok is bool && (bool)ok)
            {
                object result;
                done.TrySetResult(message.TryGetValue("result", out result) && result != null ? result.ToString() : "null");
            }
            else
            {
                object error;
                done.TrySetException(new DocsTrouble(message.TryGetValue("error", out error) ? Convert.ToString(error, CultureInfo.InvariantCulture) : "The script failed."));
            }
        }

        /// <summary>Completes when the document is on screen and can be read or changed.</summary>
        public Task WhenReadyAsync()
        {
            if (_ready) return Task.FromResult(true);
            var done = new TaskCompletionSource<bool>();
            EventHandler ready = null;
            EventHandler<DocsMessageEventArgs> failed = null;
            ready = delegate
            {
                ReadyChanged -= ready; Trouble -= failed;
                done.TrySetResult(true);
            };
            failed = delegate (object s, DocsMessageEventArgs e)
            {
                if (_ready) return;
                ReadyChanged -= ready; Trouble -= failed;
                done.TrySetException(new DocsTrouble(e.Message));
            };
            ReadyChanged += ready;
            Trouble += failed;
            return done.Task;
        }

        // ---- files -----------------------------------------------------------------

        /// <summary>
        /// Writes the document, as it is now, to another file in the format its
        /// extension names -- PDF, DOCX, ODT, TXT, XLSX, CSV and the rest -- without
        /// changing which file the tab is. The converter is given this machine's
        /// fonts, so a PDF matches what Word would make.
        /// </summary>
        public async Task<string> ExportAsync(string target)
        {
            string ext = Path.GetExtension(target).TrimStart('.').ToLowerInvariant();
            int format = DocsEngine.FormatOf(ext);
            if (format == 0) throw new DocsTrouble("NextScan cannot write ." + ext + " files.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            string work = _work;
            if (_ext == "pdf")
            {
                // The PDF as it was opened. Changes made in the PDF editor reach
                // a file through Save, which merges them; exporting reads the
                // original.
                string source = Path.Combine(work, "source.pdf");
                await Task.Run(() =>
                {
                    if (format == 0x201) File.Copy(source, target, true);
                    else DocsEngine.ConvertWith(source, target, format);
                });
                return target;
            }

            // The editor's own binary of the document now, beside its media.
            string binary = await BinaryAsync().ConfigureAwait(true);
            await Task.Run(() =>
            {
                string bin = Path.Combine(work, "Editor.bin");
                File.WriteAllBytes(bin, Convert.FromBase64String(binary));
                DocsEngine.ConvertWith(bin, target, format);
            });
            return target;
        }

        /// <summary>The document in the editor's binary format, as base64.</summary>
        Task<string> BinaryAsync()
        {
            return RunScriptAsync(
                "var ed = (window.Asc && window.Asc.editor) || window.editor;" +
                "var before = window.native, length = -1;" +
                "window.native = { Save_End: function (h, len) { length = len; }, Save_Begin: function () {} };" +
                "var data; try { data = ed.asc_nativeGetFileData(); } finally { if (before === undefined) delete window.native; else window.native = before; }" +
                "if (length >= 0 && length < data.length) data = data.subarray(0, length);" +
                "var s = ''; for (var i = 0; i < data.length; i += 0x8000) s += String.fromCharCode.apply(null, data.subarray(i, i + 0x8000));" +
                "return btoa(s);", false)
                .ContinueWith(t => new JavaScriptSerializer().Deserialize<string>(t.Result),
                              TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        /// <summary>
        /// The text of a PDF, pulled out by the converter. The PDF editor has
        /// no document API to read with; the file as opened is converted to
        /// plain text instead.
        /// </summary>
        public Task<string> PdfTextAsync()
        {
            string source = Path.Combine(_work, "source.pdf");
            string target = Path.Combine(_work, "text", "document.txt");
            return Task.Run(() =>
            {
                if (!File.Exists(target)) DocsEngine.ConvertWith(source, target, 0x45);
                return File.ReadAllText(target, Encoding.UTF8);
            });
        }

        /// <summary>Saves to a given file. Used when the assistant was told where.</summary>
        public void SaveTo(string path)
        {
            _saveAsPath = path;
            Request("saveAs", Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
        }
    }
}
