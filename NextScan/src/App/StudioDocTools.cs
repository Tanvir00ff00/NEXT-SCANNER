// =============================================================================
// NextScan Studio - what the assistant can do in the document workspace
// Plan ref: docs/DOCUMENT_WORKSPACE.md, docs/AI_LAYER.md
//
// The assistant sees and works on the open documents through these tools, and
// only through them. Each is small and does one thing the operator could do by
// hand -- look at what is open, open a file, start a new one, read one, change
// one, save, export, close -- and each change goes through the editor, so it is
// on screen, undoable with Ctrl+Z, and marks the tab unsaved like any other.
//
// Changing a document is done with a script for ONLYOFFICE's document API: the
// Office JavaScript API its plugins and Document Builder use, which models have
// seen widely and which reaches everything the editor can do. Reading is done by
// fixed scripts here (scripts\read-*.js), so what the model is shown of a
// document is decided by this application, not improvised each time.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NextScan.Ai;
using Docs = NextScan.Docs;

namespace NextScan.App
{
    public class StudioDocTools
    {
        readonly StudioWorkspace _space;

        /// <summary>Makes a PDF of the session's scanned pages and returns its path, or null.</summary>
        public Func<string> PdfFromPages;

        /// <summary>Where exports go when the assistant names no folder.</summary>
        public Func<string> OutputFolder;

        public StudioDocTools(StudioWorkspace space) { _space = space; }

        // =====================================================================
        // What the model is told exists
        // =====================================================================
        public List<AiTool> Tools()
        {
            const string doc = "\"document\":{\"type\":\"string\",\"description\":\"The document's id from list_documents, e.g. doc1. Leave out for the one in front.\"}";
            return new List<AiTool>
            {
                Tool("list_documents",
                     "Lists the documents open in NextScan's workspace (id, name, kind, file, unsaved, which is in front) and the files opened recently. Call this first whenever you are not sure what is open.",
                     "{\"type\":\"object\",\"properties\":{}}"),

                Tool("open_document",
                     "Opens a Word, Excel, PowerPoint or PDF file from disk in a new tab and waits until it is ready. Returns its id.",
                     "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Full path of the file.\"}},\"required\":[\"path\"]}"),

                Tool("create_document",
                     "Starts a new, empty document in a new tab and waits until it is ready. It has no file until it is saved. Returns its id.",
                     "{\"type\":\"object\",\"properties\":{\"kind\":{\"type\":\"string\",\"enum\":[\"document\",\"workbook\",\"presentation\"],\"description\":\"document = Word, workbook = Excel, presentation = PowerPoint.\"}},\"required\":[\"kind\"]}"),

                Tool("read_document",
                     "Reads a document's content. Word: each block in order (paragraphs with style, fonts and text; tables as rows of cells), optionally only blocks from..to. Excel: each sheet's used cells with values and formulas, optionally one sheet and range. PowerPoint: each slide's text. PDF: the text of the file. Read before you change anything, and again afterwards to check.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"from\":{\"type\":\"integer\",\"description\":\"Word: first block index.\"},\"to\":{\"type\":\"integer\",\"description\":\"Word: last block index.\"},\"sheet\":{\"type\":\"string\",\"description\":\"Excel: sheet name.\"},\"range\":{\"type\":\"string\",\"description\":\"Excel: an address such as A1:F30.\"}}}"),

                Tool("edit_document",
                     "Changes a document by running a script for ONLYOFFICE's document API (the Office JavaScript API of ONLYOFFICE Document Builder and plugins). The script is the body of a function given Api; whatever it returns comes back to you. The change is shown at once and can be undone by the operator. Not available for PDF.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"script\":{\"type\":\"string\",\"description\":\"JavaScript using Api, e.g. var d = Api.GetDocument(); var p = Api.CreateParagraph(); p.AddText('Hello'); d.Push(p); return d.GetElementsCount();\"},\"summary\":{\"type\":\"string\",\"description\":\"A few words for the operator saying what this changes, in their language.\"}},\"required\":[\"script\",\"summary\"]}"),

                Tool("get_selection",
                     "Returns the text the operator has selected in a document, for requests like 'rewrite this' or 'translate the selected part'.",
                     "{\"type\":\"object\",\"properties\":{" + doc + "}}"),

                Tool("save_document",
                     "Saves a document to its own file. A new document has no file: give a full path ending in .docx, .xlsx or .pptx, or leave it out and the operator is asked where. Only save when the operator asked for it.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"path\":{\"type\":\"string\",\"description\":\"Full path, only for a document that has no file yet.\"}}}"),

                Tool("export_document",
                     "Writes a copy of a document in another format without changing the open one: pdf, docx, odt, rtf, txt, xlsx, ods, csv, pptx, odp. The format is the path's extension. Without a folder the copy goes to NextScan's output folder. Returns the path written.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"path\":{\"type\":\"string\",\"description\":\"A full path, or just a file name such as bill.pdf.\"},\"overwrite\":{\"type\":\"boolean\",\"description\":\"Replace a file that is already there. Only if the operator agreed.\"}},\"required\":[\"path\"]}"),

                Tool("show_document",
                     "Brings a document's tab to the front so the operator sees it.",
                     "{\"type\":\"object\",\"properties\":{" + doc + "},\"required\":[\"document\"]}"),

                Tool("close_document",
                     "Closes a document's tab. One with unsaved changes is not closed unless discard_changes is true, which you must only set when the operator said to throw the changes away.",
                     "{\"type\":\"object\",\"properties\":{" + doc + ",\"discard_changes\":{\"type\":\"boolean\"}},\"required\":[\"document\"]}"),

                Tool("pdf_from_scanned_pages",
                     "Makes a PDF of the pages scanned in this session, saves it in the output folder and opens it in a new tab. Returns its id and path.",
                     "{\"type\":\"object\",\"properties\":{}}"),
            };
        }

        static AiTool Tool(string name, string description, string schema)
        {
            return new AiTool { Name = name, Description = description, ParametersJson = schema };
        }

        // =====================================================================
        // Doing it
        // =====================================================================
        public async Task<AiToolResult> Run(AiToolCall call)
        {
            var result = new AiToolResult { CallId = call.Id, Name = call.Name };
            Dictionary<string, object> args;
            try { args = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson) ?? new Dictionary<string, object>(); }
            catch { args = new Dictionary<string, object>(); }

            try
            {
                switch (call.Name)
                {
                    case "list_documents": List(result); break;
                    case "open_document": await Open(Str(args, "path"), result); break;
                    case "create_document": await Create(Str(args, "kind"), result); break;
                    case "read_document": await Read(Pick(args), args, result); break;
                    case "edit_document": await Edit(Pick(args), Str(args, "script"), Str(args, "summary"), result); break;
                    case "get_selection": await Selection(Pick(args), result); break;
                    case "save_document": await Save(Pick(args), Str(args, "path"), result); break;
                    case "export_document": await Export(Pick(args), Str(args, "path"), Bool(args, "overwrite"), result); break;
                    case "show_document": Show(Pick(args), result); break;
                    case "close_document": Close(Pick(args), Bool(args, "discard_changes"), result); break;
                    case "pdf_from_scanned_pages": await FromScans(result); break;
                    default: throw new ToolTrouble("There is no tool called " + call.Name + ".");
                }
            }
            catch (ToolTrouble ex) { Fail(result, ex.Message); }
            catch (Docs.DocsTrouble ex) { Fail(result, ex.Message); }
            catch (Exception ex) { Fail(result, ex.GetType().Name + ": " + ex.Message); }
            return result;
        }

        static void Fail(AiToolResult result, string message)
        {
            result.IsError = true;
            result.Content = message;
            if (result.Display.Length == 0) result.Display = message;
            else result.Display += " — " + message;
        }

        // ---- the documents -------------------------------------------------

        void List(AiToolResult result)
        {
            var open = new List<object>();
            foreach (DocTab tab in _space.Tabs)
            {
                var view = tab.Surface as Docs.DocView;
                open.Add(new Dictionary<string, object>
                {
                    { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) },
                    { "file", tab.Path.Length > 0 ? tab.Path : null },
                    { "unsaved", tab.Dirty }, { "in_front", tab == _space.Current },
                    { "ready", view != null && view.Ready },
                });
            }
            var recent = new List<string>();
            foreach (string path in DocRecent.Read()) { if (recent.Count >= 12) break; recent.Add(path); }

            result.Content = Json(new Dictionary<string, object>
            {
                { "open", open }, { "home_in_front", _space.Current == null }, { "recent_files", recent },
            });
            result.Display = open.Count == 0 ? "Looked at the workspace: nothing open"
                           : "Looked at the open documents (" + open.Count + ")";
        }

        async Task Open(string path, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolTrouble("Give the full path of the file to open.");
            if (!File.Exists(path)) throw new ToolTrouble("There is no file at " + path + ".");
            if (!DocKinds.CanOpen(path)) throw new ToolTrouble("NextScan does not open " + Path.GetExtension(path) + " files.");

            result.Display = "Opening " + Path.GetFileName(path);
            DocTab tab = _space.Open(path);
            if (tab == null) throw new ToolTrouble("The file could not be opened.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) } });
            result.Display = "Opened " + tab.Title;
        }

        async Task Create(string kind, AiToolResult result)
        {
            DocKind k = kind == "workbook" ? DocKind.Sheet : kind == "presentation" ? DocKind.Slides : DocKind.Word;
            DocTab tab = _space.New(k);
            if (tab == null) throw new ToolTrouble("The new document could not be started.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "name", tab.Title }, { "kind", KindName(tab.Kind) } });
            result.Display = "Started " + tab.Title;
        }

        async Task Read(DocTab tab, Dictionary<string, object> args, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            result.Display = "Read " + tab.Title;

            if (tab.Kind == DocKind.Pdf)
            {
                string text = await view.PdfTextAsync();
                if (text.Length > 60000) text = text.Substring(0, 60000) + "\n[... the rest is not shown]";
                result.Content = Json(new Dictionary<string, object> { { "kind", "pdf" }, { "text", text } });
                return;
            }

            string script = tab.Kind == DocKind.Sheet ? Script("read-sheet.js")
                          : tab.Kind == DocKind.Slides ? Script("read-slides.js")
                          : Script("read-word.js");
            var passed = new Dictionary<string, object>();
            foreach (string key in new[] { "from", "to", "sheet", "range" })
            {
                object v;
                if (args.TryGetValue(key, out v) && v != null) passed[key] = v;
            }
            string json = await view.RunScriptAsync("var args = " + Json(passed) + ";\n" + script, false);
            if (json.Length > 80000)
                json = json.Substring(0, 80000) + " [... cut short; ask for a smaller part with from/to or sheet/range]";
            result.Content = json;
        }

        async Task Edit(DocTab tab, string script, string summary, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(script)) throw new ToolTrouble("The script is empty.");
            if (tab.Kind == DocKind.Pdf) throw new ToolTrouble("A PDF cannot be changed by script. Export it to docx and open that instead.");
            Docs.DocView view = await Ready(tab);
            result.Display = (string.IsNullOrWhiteSpace(summary) ? "Changed" : summary.Trim()) + " — " + tab.Title;
            _space.Select(tab);
            string answer;
            try { answer = await view.RunScriptAsync(script, true); }
            catch (Docs.DocsTrouble ex)
            {
                // The error, not the editor's call stack: the stack is the same
                // few frames of NextScan's own plumbing every time.
                string why = ex.Message.Split('\n')[0].Trim();
                throw new ToolTrouble(why + ". Whatever the script did before this line is still in the document " +
                                      "(the operator can undo it): read the document before trying again.");
            }
            result.Content = Json(new Dictionary<string, object> { { "ok", true }, { "returned", answer } });
        }

        async Task Selection(DocTab tab, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            string text = await view.SelectedTextAsync();
            result.Content = Json(new Dictionary<string, object> { { "selected", new JavaScriptSerializer().Deserialize<object>(text) } });
            result.Display = "Read the selection in " + tab.Title;
        }

        async Task Save(DocTab tab, string path, AiToolResult result)
        {
            Docs.DocView view = await Ready(tab);
            result.Display = "Saving " + tab.Title;

            var saved = new TaskCompletionSource<bool>();
            EventHandler dirty = null;
            EventHandler<Docs.DocsMessageEventArgs> trouble = null;
            dirty = delegate { if (!view.Dirty) saved.TrySetResult(true); };
            trouble = delegate (object s, Docs.DocsMessageEventArgs e) { saved.TrySetException(new ToolTrouble(e.Message)); };
            view.DirtyChanged += dirty;
            view.Trouble += trouble;
            try
            {
                string before = view.FilePath;
                if (!string.IsNullOrWhiteSpace(path) && string.IsNullOrEmpty(before))
                {
                    if (!Path.IsPathRooted(path)) path = Path.Combine(Folder(), path);
                    view.SaveTo(path);
                }
                else view.Save();

                Task finished = await Task.WhenAny(saved.Task, Task.Delay(120000));
                if (finished != saved.Task) throw new ToolTrouble("The save did not finish (the operator may have cancelled choosing a place).");
                await saved.Task;
            }
            finally { view.DirtyChanged -= dirty; view.Trouble -= trouble; }

            tab.Path = view.FilePath;
            result.Content = Json(new Dictionary<string, object> { { "saved", true }, { "file", view.FilePath } });
            result.Display = "Saved " + Path.GetFileName(view.FilePath);
        }

        async Task Export(DocTab tab, string path, bool overwrite, AiToolResult result)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ToolTrouble("Give a file name or path for the copy, ending in the format wanted, e.g. bill.pdf.");
            if (!Path.IsPathRooted(path)) path = Path.Combine(Folder(), path);
            if (File.Exists(path) && !overwrite)
                throw new ToolTrouble("A file is already there: " + path + ". Choose another name, or ask the operator whether to replace it.");

            Docs.DocView view = await Ready(tab);
            result.Display = "Exporting " + Path.GetFileName(path);
            string written = await view.ExportAsync(path);
            result.Content = Json(new Dictionary<string, object> { { "written", written } });
            result.Display = "Wrote " + Path.GetFileName(written);
        }

        void Show(DocTab tab, AiToolResult result)
        {
            _space.Select(tab);
            result.Content = "{\"shown\":\"" + tab.Id + "\"}";
            result.Display = "Showed " + tab.Title;
        }

        void Close(DocTab tab, bool discard, AiToolResult result)
        {
            if (tab.Dirty && !discard)
                throw new ToolTrouble(tab.Title + " has unsaved changes. Save it first, or ask the operator whether to discard them.");
            if (discard) tab.Dirty = false;
            string title = tab.Title;
            _space.Close(tab);
            result.Content = "{\"closed\":true}";
            result.Display = "Closed " + title;
        }

        async Task FromScans(AiToolResult result)
        {
            if (PdfFromPages == null) throw new ToolTrouble("Scanned pages are not available here.");
            string path = PdfFromPages();
            if (string.IsNullOrEmpty(path)) throw new ToolTrouble("There are no scanned pages in this session, or the PDF could not be written.");
            DocTab tab = _space.Open(path);
            if (tab == null) throw new ToolTrouble("The PDF was written to " + path + " but could not be opened.");
            await Ready(tab);
            result.Content = Json(new Dictionary<string, object> { { "id", tab.Id }, { "file", path } });
            result.Display = "Made a PDF of the scanned pages";
        }

        // ---- helpers -------------------------------------------------------

        DocTab Pick(Dictionary<string, object> args)
        {
            string id = Str(args, "document");
            if (!string.IsNullOrEmpty(id))
            {
                DocTab tab = _space.ById(id);
                if (tab == null) throw new ToolTrouble("No open document has the id " + id + ". Call list_documents.");
                return tab;
            }
            if (_space.Current != null) return _space.Current;
            if (_space.Tabs.Count == 1) return _space.Tabs[0];
            throw new ToolTrouble(_space.Tabs.Count == 0 ? "No document is open." : "Several documents are open and none is in front: say which one.");
        }

        static async Task<Docs.DocView> Ready(DocTab tab)
        {
            var view = tab.Surface as Docs.DocView;
            if (view == null) throw new ToolTrouble(tab.Title + " has no editor.");
            Task ready = view.WhenReadyAsync();
            Task finished = await Task.WhenAny(ready, Task.Delay(90000));
            if (finished != ready) throw new ToolTrouble(tab.Title + " did not finish opening.");
            await ready;
            return view;
        }

        string Folder()
        {
            string folder = OutputFolder != null ? OutputFolder() : "";
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NextScan");
            Directory.CreateDirectory(folder);
            return folder;
        }

        static string KindName(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Word: return "document";
                case DocKind.Sheet: return "workbook";
                case DocKind.Slides: return "presentation";
                case DocKind.Pdf: return "pdf";
                default: return "other";
            }
        }

        static string Str(Dictionary<string, object> args, string key)
        {
            object v;
            return args.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }

        static bool Bool(Dictionary<string, object> args, string key)
        {
            object v;
            return args.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static string Json(object value)
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value);
        }

        static readonly Dictionary<string, string> Scripts = new Dictionary<string, string>();

        /// <summary>One of the reading scripts, compiled into the application (scripts\*.js).</summary>
        static string Script(string name)
        {
            lock (Scripts)
            {
                string text;
                if (Scripts.TryGetValue(name, out text)) return text;
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NextScan.App.scripts." + name))
                {
                    if (s == null) throw new ToolTrouble("The reading script " + name + " is missing from this build.");
                    using (var reader = new StreamReader(s, Encoding.UTF8)) text = reader.ReadToEnd();
                }
                Scripts[name] = text;
                return text;
            }
        }

        class ToolTrouble : Exception
        {
            public ToolTrouble(string message) : base(message) { }
        }
    }
}
