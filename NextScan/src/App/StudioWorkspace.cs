// =============================================================================
// NextScan Studio - the document workspace
// Plan ref: docs/DOCUMENT_WORKSPACE.md
//
// What the middle of the window becomes when Assist is chosen: a Home page and
// a strip of open documents, the shape Word's start screen and WPS Office's tab
// strip share. Scanning, the canvas and the crop tools are not here; they come
// back exactly as they were when another section is chosen.
//
// This file knows nothing about how a document is drawn or edited. A tab asks
// the shell for a surface (MakeSurface) and the shell hands it whatever the
// editor engine provides, so the workspace, its Home and its recent list can be
// built, tested and looked at without the engine being present.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace NextScan.App
{
    public enum DocKind { Word, Sheet, Slides, Pdf, Other }

    /// <summary>What kind of document a file is, and how each kind is shown.</summary>
    public static class DocKinds
    {
        public static DocKind Of(string path)
        {
            switch ((Path.GetExtension(path ?? "") ?? "").ToLowerInvariant())
            {
                case ".docx": case ".doc": case ".docm": case ".dotx": case ".odt": case ".rtf": case ".txt":
                    return DocKind.Word;
                case ".xlsx": case ".xls": case ".xlsm": case ".xltx": case ".ods": case ".csv":
                    return DocKind.Sheet;
                case ".pptx": case ".ppt": case ".ppsx": case ".odp":
                    return DocKind.Slides;
                case ".pdf":
                    return DocKind.Pdf;
                default:
                    return DocKind.Other;
            }
        }

        /// <summary>
        /// The colour each kind is known by. Everybody who has used Office reads
        /// blue as a document, green as a sheet and red as a PDF before reading
        /// the name, so the badge carries the kind and the text carries the file.
        /// </summary>
        public static Color Tint(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Word: return Color.FromArgb(0x2B, 0x57, 0x9A);
                case DocKind.Sheet: return Color.FromArgb(0x21, 0x73, 0x46);
                case DocKind.Slides: return Color.FromArgb(0xC4, 0x3E, 0x1C);
                case DocKind.Pdf: return Color.FromArgb(0xD0, 0x2B, 0x2B);
                default: return Color.FromArgb(0x6B, 0x6B, 0x6B);
            }
        }

        public static string Letter(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Word: return "W";
                case DocKind.Sheet: return "X";
                case DocKind.Slides: return "P";
                case DocKind.Pdf: return "PDF";
                default: return "?";
            }
        }

        public static string Noun(DocKind kind)
        {
            switch (kind)
            {
                case DocKind.Word: return "Document";
                case DocKind.Sheet: return "Workbook";
                case DocKind.Slides: return "Presentation";
                case DocKind.Pdf: return "PDF";
                default: return "File";
            }
        }

        public const string OpenFilter =
            "Documents, workbooks, presentations and PDF|*.docx;*.doc;*.docm;*.dotx;*.odt;*.rtf;*.txt;" +
            "*.xlsx;*.xls;*.xlsm;*.xltx;*.ods;*.csv;*.pptx;*.ppt;*.ppsx;*.odp;*.pdf|" +
            "Word documents|*.docx;*.doc;*.docm;*.dotx;*.odt;*.rtf;*.txt|" +
            "Excel workbooks|*.xlsx;*.xls;*.xlsm;*.xltx;*.ods;*.csv|" +
            "PowerPoint presentations|*.pptx;*.ppt;*.ppsx;*.odp|" +
            "PDF|*.pdf|" +
            "All files|*.*";

        public static bool CanOpen(string path) { return Of(path) != DocKind.Other; }

        /// <summary>The kind's badge: a rounded square with its letter, in its own colour.</summary>
        public static void DrawBadge(Graphics g, DocKind kind, Rectangle r)
        {
            using (GraphicsPath path = Theme.Round(r, Math.Max(3, r.Width / 5)))
            using (SolidBrush fill = new SolidBrush(Tint(kind)))
                g.FillPath(fill, path);

            string letter = Letter(kind);
            float size = letter.Length > 1 ? r.Height * 0.30f : r.Height * 0.48f;
            using (Font f = new Font("Segoe UI", Math.Max(5f, size), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, letter, f, r, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>
    /// Files opened in the workspace, newest first. Paths only: nothing of the
    /// files themselves is copied, and a file that has since moved simply drops
    /// off the list the next time it is read.
    /// </summary>
    public static class DocRecent
    {
        const int Most = 40;

        static string FilePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "NextScan", "recent-documents.txt");
            }
        }

        public static List<string> Read()
        {
            var found = new List<string>();
            try
            {
                if (!File.Exists(FilePath)) return found;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string path = line.Trim();
                    if (path.Length == 0 || found.Contains(path)) continue;
                    if (!File.Exists(path)) continue;
                    found.Add(path);
                }
            }
            catch { }
            return found;
        }

        public static void Touch(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            List<string> list = Read();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            Write(list);
        }

        public static void Forget(string path)
        {
            List<string> list = Read();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            Write(list);
        }

        static void Write(List<string> list)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                if (list.Count > Most) list.RemoveRange(Most, list.Count - Most);
                File.WriteAllLines(FilePath, list.ToArray(), Encoding.UTF8);
            }
            catch { }
        }
    }

    /// <summary>One open document.</summary>
    public class DocTab
    {
        /// <summary>The file on disk. Empty for a new document that has not been saved yet.</summary>
        public string Path = "";

        public DocKind Kind;

        /// <summary>What the tab says: the file name, or "Document 1" for a new one.</summary>
        public string Title = "";

        public bool Dirty;

        /// <summary>The editor for this document, from the shell. Null until it is made.</summary>
        public Control Surface;

        public override string ToString() { return Title; }
    }

    // =========================================================================
    // The workspace
    // =========================================================================
    public class StudioWorkspace : Panel
    {
        readonly NsDocTabs _strip;
        readonly NsDocHome _home;
        readonly List<DocTab> _tabs = new List<DocTab>();
        DocTab _current;               // null while Home is showing
        int _untitled;

        /// <summary>
        /// Makes the editor for a tab. The workspace owns the tab and the place
        /// on screen; the shell owns the engine. Returning null means the
        /// document could not be opened, and the shell has said why.
        /// </summary>
        public Func<DocTab, Control> MakeSurface;

        /// <summary>
        /// Asked before a tab with unsaved changes closes. True lets it close.
        /// Without a handler, a changed document is never closed silently.
        /// </summary>
        public Func<DocTab, bool> ConfirmClose;

        /// <summary>How many scanned pages the session holds, for "PDF from pages".</summary>
        public Func<int> ScannedPages;

        /// <summary>Asked to make a PDF of the session's pages; returns its path, or null.</summary>
        public Func<string> PdfFromPages;

        public Action<string> Status;

        /// <summary>The document now in front changed, or Home came to the front (null).</summary>
        public event EventHandler CurrentChanged;

        public StudioWorkspace()
        {
            BackColor = Theme.Ground;
            AllowDrop = true;

            // The strip is made here but not placed here: it lives in the
            // window's title bar, where the scanner bar is in Capture -- the
            // owner's layout, and the one browsers and Office use. The shell
            // takes it through Strip and puts it there.
            _strip = new NsDocTabs(this);
            _home = new NsDocHome(this) { Dock = DockStyle.Fill };

            Controls.Add(_home);
        }

        /// <summary>Home, the document tabs and the plus, for the shell to place in its title bar.</summary>
        public NsDocTabs Strip { get { return _strip; } }

        public DocTab Current { get { return _current; } }
        public IList<DocTab> Tabs { get { return _tabs.AsReadOnly(); } }

        // ---- opening -------------------------------------------------------

        /// <summary>Opens a file, or brings it forward if it is already open.</summary>
        public DocTab Open(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { path = System.IO.Path.GetFullPath(path); } catch { }

            foreach (DocTab open in _tabs)
                if (string.Equals(open.Path, path, StringComparison.OrdinalIgnoreCase)) { Select(open); return open; }

            if (!File.Exists(path)) { Say("That file is not there any more: " + path); DocRecent.Forget(path); _home.Reload(); return null; }
            if (!DocKinds.CanOpen(path)) { Say("NextScan does not open " + System.IO.Path.GetExtension(path) + " files."); return null; }

            DocTab tab = new DocTab
            {
                Path = path,
                Kind = DocKinds.Of(path),
                Title = System.IO.Path.GetFileName(path)
            };
            if (!Attach(tab)) return null;

            DocRecent.Touch(path);
            _home.Reload();
            return tab;
        }

        /// <summary>A new, empty document of the given kind. It has no file until it is saved.</summary>
        public DocTab New(DocKind kind)
        {
            _untitled++;
            DocTab tab = new DocTab { Kind = kind, Title = DocKinds.Noun(kind) + " " + _untitled };
            return Attach(tab) ? tab : null;
        }

        public void OpenWithDialog()
        {
            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = DocKinds.OpenFilter,
                Multiselect = true,
                Title = "Open"
            })
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                foreach (string file in dialog.FileNames) Open(file);
            }
        }

        public void MakePdfFromPages()
        {
            if (PdfFromPages == null) return;
            string made = PdfFromPages();
            if (!string.IsNullOrEmpty(made)) Open(made);
        }

        bool Attach(DocTab tab)
        {
            Control surface = null;
            try { surface = MakeSurface != null ? MakeSurface(tab) : null; }
            catch (Exception ex) { Say("Could not open " + tab.Title + ": " + ex.Message); }

            if (surface == null)
            {
                if (MakeSurface == null) Say("The document editor is not installed.");
                return false;
            }

            tab.Surface = surface;
            surface.Visible = false;
            surface.Dock = DockStyle.Fill;
            Controls.Add(surface);

            surface.BringToFront();

            _tabs.Add(tab);
            Select(tab);
            return true;
        }

        // ---- switching -----------------------------------------------------

        /// <summary>Brings a tab forward. Null shows Home.</summary>
        public void Select(DocTab tab)
        {
            if (tab != null && !_tabs.Contains(tab)) return;
            _current = tab;

            SuspendLayout();
            foreach (DocTab each in _tabs)
                if (each.Surface != null) each.Surface.Visible = each == tab;
            _home.Visible = tab == null;
            if (tab == null) _home.Reload();
            ResumeLayout();

            if (tab != null && tab.Surface != null) tab.Surface.Focus();
            _strip.Invalidate();
            if (CurrentChanged != null) CurrentChanged(this, EventArgs.Empty);
        }

        public void ShowHome() { Select(null); }

        public void Next(int step)
        {
            // Home counts as a position: Ctrl+Tab from the last document goes Home.
            int count = _tabs.Count + 1;
            int at = _current == null ? 0 : _tabs.IndexOf(_current) + 1;
            at = ((at + step) % count + count) % count;
            Select(at == 0 ? null : _tabs[at - 1]);
        }

        // ---- closing -------------------------------------------------------

        public bool Close(DocTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return true;

            if (tab.Dirty)
            {
                Select(tab);
                if (ConfirmClose == null || !ConfirmClose(tab)) return false;
            }

            int index = _tabs.IndexOf(tab);
            _tabs.Remove(tab);
            if (tab.Surface != null)
            {
                Controls.Remove(tab.Surface);
                try { tab.Surface.Dispose(); } catch { }
                tab.Surface = null;
            }

            // The neighbour takes its place, as it does in every tabbed program;
            // Home only when nothing is left.
            if (_current == tab)
                Select(_tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)]);
            else
                _strip.Invalidate();
            return true;
        }

        /// <summary>Asks to close every tab. False if the operator kept one open.</summary>
        public bool CloseAll()
        {
            foreach (DocTab tab in new List<DocTab>(_tabs))
                if (!Close(tab)) return false;
            return true;
        }

        /// <summary>A tab's surface reports a change in its saved state or its file.</summary>
        public void Changed(DocTab tab)
        {
            if (tab == null) return;
            if (!string.IsNullOrEmpty(tab.Path))
            {
                tab.Title = System.IO.Path.GetFileName(tab.Path);
                tab.Kind = DocKinds.Of(tab.Path);
                DocRecent.Touch(tab.Path);
            }
            _strip.Invalidate();
            if (tab == _current && CurrentChanged != null) CurrentChanged(this, EventArgs.Empty);
        }

        // ---- keyboard and files dropped on it ------------------------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Control | Keys.O: OpenWithDialog(); return true;
                case Keys.Control | Keys.N: New(DocKind.Word); return true;
                case Keys.Control | Keys.W: if (_current != null) Close(_current); return true;
                case Keys.Control | Keys.Tab: Next(1); return true;
                case Keys.Control | Keys.Shift | Keys.Tab: Next(-1); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            e.Effect = Openable(e) ? DragDropEffects.Copy : DragDropEffects.None;
            _home.Dropping = e.Effect != DragDropEffects.None;
            base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e) { _home.Dropping = false; base.OnDragLeave(e); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            _home.Dropping = false;
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) foreach (string file in files) if (DocKinds.CanOpen(file)) Open(file);
            base.OnDragDrop(e);
        }

        static bool Openable(DragEventArgs e)
        {
            string[] files = e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;
            if (files == null) return false;
            foreach (string file in files) if (DocKinds.CanOpen(file)) return true;
            return false;
        }

        internal void Say(string text) { if (Status != null) Status(text); }

        public void ApplyTheme()
        {
            BackColor = Theme.Ground;
            _strip.Invalidate();
            _home.ApplyTheme();
        }
    }

    // =========================================================================
    // The tab strip
    // =========================================================================
    /// <summary>
    /// Home, then one tab per open document, then a plus. Drawn as one control
    /// rather than a row of buttons, because tabs shrink together as more open
    /// and a row of separate controls cannot share out a width.
    ///
    /// It sits in the window's title bar, as browser tabs do: standing on the
    /// bar's bottom edge, with the one in front opening into the page below.
    /// The space between and after the tabs is still title bar, so pressing
    /// there moves the window (EmptyPressed) and a double click maximises it.
    /// </summary>
    public class NsDocTabs : NsBase
    {
        const int TabHeight = 36;
        const int HomeWidth = 92;
        const int PlusWidth = 36;
        const int MaxTab = 220;
        const int MinTab = 96;

        readonly StudioWorkspace _space;
        int _hot = -2;          // -1 Home, 0.. tabs, -3 plus, -2 nothing
        bool _hotClose;
        readonly ToolTip _tips = new ToolTip();
        string _tipFor = "";

        /// <summary>Pressed where there is no tab: the shell hands this to the window, as a title bar would.</summary>
        public event MouseEventHandler EmptyPressed;

        /// <summary>Double-clicked where there is no tab.</summary>
        public event EventHandler EmptyDoubleClicked;

        public NsDocTabs(StudioWorkspace space)
        {
            _space = space;
            Height = TabHeight + 8;
            SetStyle(ControlStyles.Selectable, false);
        }

        int Top_() { return Math.Max(2, Height - TabHeight); }

        /// <summary>How much of the width the tabs actually use, so the shell can size the strip.</summary>
        public int WantedWidth
        {
            get { return 8 + HomeWidth + _space.Tabs.Count * MaxTab + PlusWidth + 8; }
        }

        int TabWidth()
        {
            int count = Math.Max(1, _space.Tabs.Count);
            int room = Width - HomeWidth - PlusWidth - 16;
            return Math.Max(MinTab, Math.Min(MaxTab, room / count));
        }

        Rectangle HomeRect() { return new Rectangle(8, Top_(), HomeWidth - 4, Height - Top_()); }

        Rectangle TabRect(int i)
        {
            int w = TabWidth();
            return new Rectangle(8 + HomeWidth + i * w, Top_(), w - 2, Height - Top_());
        }

        Rectangle PlusRect()
        {
            int x = 8 + HomeWidth + _space.Tabs.Count * TabWidth() + 4;
            int size = 28;
            return new Rectangle(Math.Min(x, Width - PlusWidth - 4), Top_() + (Height - Top_() - size) / 2, size, size);
        }

        static Rectangle CloseRect(Rectangle tab)
        {
            return new Rectangle(tab.Right - 26, tab.Y + (tab.Height - 20) / 2, 20, 20);
        }

        int HitTest(Point p, out bool onClose)
        {
            onClose = false;
            if (HomeRect().Contains(p)) return -1;
            for (int i = 0; i < _space.Tabs.Count; i++)
            {
                Rectangle r = TabRect(i);
                if (r.Right > Width) break;
                if (!r.Contains(p)) continue;
                onClose = CloseRect(r).Contains(p);
                return i;
            }
            if (PlusRect().Contains(p)) return -3;
            return -2;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool onClose;
            int hot = HitTest(e.Location, out onClose);
            if (hot != _hot || onClose != _hotClose) { _hot = hot; _hotClose = onClose; Invalidate(); }

            string tip = hot >= 0 ? (_space.Tabs[hot].Path.Length > 0 ? _space.Tabs[hot].Path : "Not saved yet")
                       : hot == -3 ? "New or open (Ctrl+N, Ctrl+O)" : "";
            if (tip != _tipFor) { _tipFor = tip; _tips.SetToolTip(this, tip); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hot = -2; _hotClose = false; Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            bool onClose;
            if (HitTest(e.Location, out onClose) == -2 && e.Button == MouseButtons.Left)
            {
                if (e.Clicks >= 2) { if (EmptyDoubleClicked != null) EmptyDoubleClicked(this, EventArgs.Empty); }
                else if (EmptyPressed != null) EmptyPressed(this, e);
                return;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool onClose;
            int at = HitTest(e.Location, out onClose);

            if (e.Button == MouseButtons.Middle && at >= 0) _space.Close(_space.Tabs[at]);
            else if (e.Button == MouseButtons.Left)
            {
                if (at == -1 || at == -3) _space.ShowHome();
                else if (at >= 0 && onClose) _space.Close(_space.Tabs[at]);
                else if (at >= 0) _space.Select(_space.Tabs[at]);
            }
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            // The title bar's own bottom rule, continued under the strip; the
            // tab in front covers its stretch of it.
            using (Pen line = new Pen(Theme.LineSoft))
                g.DrawLine(line, 0, Height - 1, Width, Height - 1);

            // ---- Home
            Rectangle home = HomeRect();
            PaintTab(g, home, _space.Current == null, _hot == -1);
            Color homeInk = _space.Current == null ? Theme.Text : Theme.TextDim;
            NsIcon.Draw(g, NsIcon.Home, new RectangleF(home.X + 10, home.Y + (home.Height - 18) / 2f, 18, 18), homeInk);
            using (Font f = Theme.UiSemi(9f))
                TextRenderer.DrawText(g, "Home", f, new Rectangle(home.X + 32, home.Y, home.Width - 34, home.Height),
                    homeInk, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            // ---- documents
            for (int i = 0; i < _space.Tabs.Count; i++)
            {
                DocTab tab = _space.Tabs[i];
                Rectangle r = TabRect(i);
                if (r.Right > Width) break;

                bool on = tab == _space.Current;
                PaintTab(g, r, on, _hot == i);

                DocKinds.DrawBadge(g, tab.Kind, new Rectangle(r.X + 10, r.Y + (r.Height - 18) / 2, 18, 18));

                Rectangle text = new Rectangle(r.X + 34, r.Y, r.Width - 34 - 28, r.Height);
                using (Font f = on ? Theme.UiSemi(9f) : Theme.Ui(9f))
                    TextRenderer.DrawText(g, tab.Title, f, text, on ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

                // The close mark, or the unsaved dot where it will be. The dot
                // turns into the cross under the pointer, as editors do, so an
                // unsaved document is visible from across the room.
                Rectangle close = CloseRect(r);
                bool hotHere = _hot == i;
                if (tab.Dirty && !hotHere)
                {
                    using (SolidBrush dot = new SolidBrush(Theme.TextDim))
                        g.FillEllipse(dot, close.X + 6, close.Y + 6, 8, 8);
                }
                else if (on || hotHere)
                {
                    if (hotHere && _hotClose)
                        using (GraphicsPath p = Theme.Round(close, 5))
                        using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
                    using (Pen x = new Pen(Theme.TextDim, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(x, close.X + 6, close.Y + 6, close.Right - 6, close.Bottom - 6);
                        g.DrawLine(x, close.Right - 6, close.Y + 6, close.X + 6, close.Bottom - 6);
                    }
                }
            }

            // ---- plus
            Rectangle plus = PlusRect();
            if (_hot == -3)
                using (GraphicsPath p = Theme.Round(plus, 6))
                using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);
            using (Pen pen = new Pen(Theme.TextDim, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = plus.X + plus.Width / 2f, cy = plus.Y + plus.Height / 2f;
                g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
                g.DrawLine(pen, cx, cy - 6, cx, cy + 6);
            }
        }

        /// <summary>
        /// The one in front joins the page below it: same colour as the
        /// document area, no line under it. The rest sit back on the strip.
        /// </summary>
        void PaintTab(Graphics g, Rectangle r, bool on, bool hot)
        {
            if (!on && !hot) return;
            if (!on) r = new Rectangle(r.X, r.Y + 3, r.Width, r.Height - 6);
            Rectangle plate = new Rectangle(r.X, r.Y, r.Width, on ? r.Height + 8 : r.Height);
            using (GraphicsPath path = Theme.Round(plate, 8))
            {
                Color fill = on ? Theme.Raised : Theme.Hover;
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                if (on) using (Pen edge = new Pen(Theme.LineSoft)) g.DrawPath(edge, path);
            }
            if (on)
                using (SolidBrush cover = new SolidBrush(Theme.Raised))
                    g.FillRectangle(cover, r.X + 1, Height - 2, r.Width - 1, 2);
        }
    }

    // =========================================================================
    // Home
    // =========================================================================
    /// <summary>
    /// New, open, and what was open before. Word's start screen, without the
    /// templates gallery: the files a shop works on are its own, and the
    /// list of them is what is needed first.
    /// </summary>
    public class NsDocHome : NsBase
    {
        const int Side = 40;
        const int TileW = 132, TileH = 150, TileGap = 16;
        const int RowH = 44;

        readonly StudioWorkspace _space;
        List<string> _recent = new List<string>();
        int _scroll;
        int _contentHeight;
        bool _dropping;

        // What the pointer is over: a tile ("t:0".."t:4") or a recent row ("r:3").
        string _hot = "";

        public NsDocHome(StudioWorkspace space)
        {
            _space = space;
            BackColor = Theme.Raised;
            SetStyle(ControlStyles.Selectable, true);
            Reload();
        }

        public bool Dropping
        {
            get { return _dropping; }
            set { if (_dropping == value) return; _dropping = value; Invalidate(); }
        }

        public void Reload()
        {
            _recent = DocRecent.Read();
            Invalidate();
        }

        public void ApplyTheme() { BackColor = Theme.Raised; Invalidate(); }

        // ---- geometry ------------------------------------------------------

        struct Tile { public string Label, Note; public DocKind Kind; public bool Folder; public bool Enabled; }

        List<Tile> Tiles()
        {
            int pages = _space.ScannedPages != null ? _space.ScannedPages() : 0;
            return new List<Tile>
            {
                new Tile { Label = "Blank document", Kind = DocKind.Word, Enabled = true },
                new Tile { Label = "Blank workbook", Kind = DocKind.Sheet, Enabled = true },
                new Tile { Label = "Blank presentation", Kind = DocKind.Slides, Enabled = true },
                new Tile { Label = "PDF from pages", Kind = DocKind.Pdf, Enabled = pages > 0 && _space.PdfFromPages != null,
                           Note = pages > 0 ? pages + (pages == 1 ? " scanned page" : " scanned pages") : "No pages scanned yet" },
                new Tile { Label = "Open…", Folder = true, Enabled = true, Note = "or drop a file here" },
            };
        }

        int Column() { return Math.Min(Width - Side * 2, 980); }
        int ColumnLeft() { return Math.Max(Side, (Width - Column()) / 2); }

        const int TitleTop = 34;
        const int NewTop = 104;
        int RecentTop() { return NewTop + 28 + TileH + 40; }

        Rectangle TileRect(int i)
        {
            return new Rectangle(ColumnLeft() + i * (TileW + TileGap), NewTop + 28 - _scroll, TileW, TileH);
        }

        Rectangle RowRect(int i)
        {
            return new Rectangle(ColumnLeft(), RecentTop() + 28 + 30 + i * RowH - _scroll, Column(), RowH);
        }

        // ---- input ---------------------------------------------------------

        string HitTest(Point p)
        {
            List<Tile> tiles = Tiles();
            for (int i = 0; i < tiles.Count; i++)
                if (TileRect(i).Contains(p)) return "t:" + i;
            for (int i = 0; i < _recent.Count; i++)
                if (RowRect(i).Contains(p)) return "r:" + i;
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string hot = HitTest(e.Location);
            if (hot != _hot) { _hot = hot; Cursor = hot.Length > 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = ""; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-e.Delta / 2);
            base.OnMouseWheel(e);
        }

        void ScrollBy(int by)
        {
            int most = Math.Max(0, _contentHeight - Height);
            int to = Math.Max(0, Math.Min(most, _scroll + by));
            if (to == _scroll) return;
            _scroll = to;
            Invalidate();
        }

        protected override void OnResize(EventArgs e) { ScrollBy(0); base.OnResize(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            string hit = HitTest(e.Location);
            if (hit.StartsWith("t:", StringComparison.Ordinal) && e.Button == MouseButtons.Left)
            {
                int i = int.Parse(hit.Substring(2), CultureInfo.InvariantCulture);
                Tile tile = Tiles()[i];
                if (!tile.Enabled) { _space.Say(tile.Note ?? ""); }
                else if (tile.Folder) _space.OpenWithDialog();
                else if (tile.Kind == DocKind.Pdf) _space.MakePdfFromPages();
                else _space.New(tile.Kind);
            }
            else if (hit.StartsWith("r:", StringComparison.Ordinal))
            {
                string path = _recent[int.Parse(hit.Substring(2), CultureInfo.InvariantCulture)];
                if (e.Button == MouseButtons.Left) _space.Open(path);
                else if (e.Button == MouseButtons.Right) RowMenu(path, e.Location);
            }
            base.OnMouseUp(e);
        }

        void RowMenu(string path, Point at)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open", null, delegate { _space.Open(path); });
            menu.Items.Add("Show in folder", null, delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); }
                catch (Exception ex) { _space.Say("Could not open the folder: " + ex.Message); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Remove from this list", null, delegate { DocRecent.Forget(path); Reload(); });
            menu.Closed += delegate { BeginInvoke((MethodInvoker)delegate { menu.Dispose(); }); };
            menu.Show(this, at);
        }

        // ---- painting ------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Raised);
            Theme.Smooth(g);

            int left = ColumnLeft();
            int column = Column();

            using (Font f = Theme.UiSemi(17f))
                TextRenderer.DrawText(g, "Documents", f, new Point(left - 2, TitleTop - _scroll), Theme.Text,
                    TextFormatFlags.NoPrefix);
            using (Font f = Theme.Ui(9.5f))
                TextRenderer.DrawText(g, "Word, Excel, PowerPoint and PDF, opened and edited here.", f,
                    new Point(left, TitleTop + 36 - _scroll), Theme.TextDim, TextFormatFlags.NoPrefix);

            // ---- New
            Header(g, "New", left, NewTop - _scroll);
            List<Tile> tiles = Tiles();
            for (int i = 0; i < tiles.Count; i++) PaintTile(g, tiles[i], TileRect(i), _hot == "t:" + i);

            // ---- Recent
            int top = RecentTop() - _scroll;
            Header(g, "Recent", left, top);

            if (_recent.Count == 0)
            {
                using (Font f = Theme.Ui(9.5f))
                    TextRenderer.DrawText(g,
                        "Files you open appear here. Drop a Word, Excel, PowerPoint or PDF file anywhere on this page to open it.",
                        f, new Rectangle(left, top + 34, column, 40), Theme.TextFaint,
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                _contentHeight = RecentTop() + 120;
            }
            else
            {
                int nameW = (int)(column * 0.42), folderW = (int)(column * 0.40);
                using (Font f = Theme.UiSemi(8.25f))
                {
                    int y = top + 28;
                    TextRenderer.DrawText(g, "Name", f, new Rectangle(left + 44, y, nameW, 26), Theme.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, "Folder", f, new Rectangle(left + 44 + nameW, y, folderW, 26), Theme.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, "Modified", f, new Rectangle(left + 44 + nameW + folderW, y,
                        column - 44 - nameW - folderW - 8, 26), Theme.TextFaint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                    using (Pen line = new Pen(Theme.LineSoft)) g.DrawLine(line, left, y + 28, left + column, y + 28);
                }

                for (int i = 0; i < _recent.Count; i++)
                {
                    Rectangle r = RowRect(i);
                    if (r.Bottom < 0 || r.Top > Height) continue;
                    PaintRow(g, _recent[i], r, _hot == "r:" + i, nameW, folderW);
                }
                _contentHeight = RecentTop() + 28 + 30 + _recent.Count * RowH + 40;
            }

            if (_dropping)
            {
                Rectangle drop = new Rectangle(12, 12, Width - 25, Height - 25);
                using (GraphicsPath path = Theme.Round(drop, 14))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(28, Theme.Accent))) g.FillPath(b, path);
                    using (Pen pen = new Pen(Theme.Accent, 2f) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
                }
                using (Font f = Theme.UiSemi(13f))
                    TextRenderer.DrawText(g, "Drop to open", f, drop, Theme.Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }

            PaintScrollMark(g);
        }

        static void Header(Graphics g, string text, int x, int y)
        {
            using (Font f = Theme.UiSemi(10.5f))
                TextRenderer.DrawText(g, text, f, new Point(x - 1, y), Theme.Text, TextFormatFlags.NoPrefix);
        }

        void PaintTile(Graphics g, Tile tile, Rectangle r, bool hot)
        {
            bool live = tile.Enabled;
            Rectangle sheet = new Rectangle(r.X, r.Y, r.Width, r.Height - 40);

            using (GraphicsPath path = Theme.Round(sheet, 10))
            {
                Color fill = hot && live ? Theme.Mix(Theme.Field, Theme.Hover, 0.7) : Theme.Field;
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                using (Pen edge = new Pen(hot && live ? Theme.Mix(Theme.Line, Theme.Accent, 0.6) : Theme.LineSoft, hot && live ? 1.4f : 1f))
                    g.DrawPath(edge, path);
            }

            if (tile.Folder)
            {
                NsIcon.Draw(g, NsIcon.Folder, new RectangleF(sheet.X + sheet.Width / 2f - 18, sheet.Y + sheet.Height / 2f - 18, 36, 36),
                            hot ? Theme.Accent : Theme.TextDim);
            }
            else
            {
                // A page, a sheet or a slide drawn in the kind's colour, so the
                // four read differently before the labels are read.
                Rectangle page = PageShape(tile.Kind, sheet);
                Color tint = live ? DocKinds.Tint(tile.Kind) : Theme.Line;
                using (SolidBrush white = new SolidBrush(Color.White)) g.FillRectangle(white, page);
                using (Pen edge = new Pen(Theme.Mix(Theme.Line, tint, 0.35))) g.DrawRectangle(edge, page);
                PaintPageLines(g, tile.Kind, page, tint);
                DocKinds.DrawBadge(g, tile.Kind, new Rectangle(page.Right - 14, page.Bottom - 14, 22, 22));
            }

            using (Font f = Theme.UiSemi(9f))
                TextRenderer.DrawText(g, tile.Label, f, new Rectangle(r.X, sheet.Bottom + 6, r.Width, 18),
                    live ? Theme.Text : Theme.TextFaint, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(tile.Note))
                using (Font f = Theme.Ui(8f))
                    TextRenderer.DrawText(g, tile.Note, f, new Rectangle(r.X, sheet.Bottom + 23, r.Width, 16),
                        Theme.TextFaint, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        static Rectangle PageShape(DocKind kind, Rectangle sheet)
        {
            if (kind == DocKind.Slides || kind == DocKind.Sheet)
            {
                int w = sheet.Width - 34, h = (int)(w * 0.66);
                return new Rectangle(sheet.X + (sheet.Width - w) / 2, sheet.Y + (sheet.Height - h) / 2 - 2, w, h);
            }
            int ph = sheet.Height - 22, pw = (int)(ph * 0.72);
            return new Rectangle(sheet.X + (sheet.Width - pw) / 2, sheet.Y + 11, pw, ph);
        }

        static void PaintPageLines(Graphics g, DocKind kind, Rectangle page, Color tint)
        {
            Color ink = Color.FromArgb(70, tint);
            using (Pen pen = new Pen(ink, 1.4f))
            {
                if (kind == DocKind.Sheet)
                {
                    for (int y = page.Y + 10; y < page.Bottom - 2; y += 9) g.DrawLine(pen, page.X + 4, y, page.Right - 4, y);
                    for (int x = page.X + 18; x < page.Right - 4; x += 16) g.DrawLine(pen, x, page.Y + 4, x, page.Bottom - 4);
                }
                else if (kind == DocKind.Slides)
                {
                    using (SolidBrush b = new SolidBrush(ink))
                        g.FillRectangle(b, page.X + 8, page.Y + 8, page.Width - 16, 7);
                    g.DrawLine(pen, page.X + 12, page.Y + 26, page.Right - 24, page.Y + 26);
                    g.DrawLine(pen, page.X + 12, page.Y + 34, page.Right - 34, page.Y + 34);
                }
                else
                {
                    for (int y = page.Y + 12, n = 0; y < page.Bottom - 16; y += 8, n++)
                        g.DrawLine(pen, page.X + 8, y, page.Right - (n % 3 == 2 ? 22 : 8), y);
                }
            }
        }

        void PaintRow(Graphics g, string path, Rectangle r, bool hot, int nameW, int folderW)
        {
            if (hot)
                using (GraphicsPath p = Theme.Round(new Rectangle(r.X - 6, r.Y + 2, r.Width + 12, r.Height - 4), 8))
                using (SolidBrush b = new SolidBrush(Theme.Hover)) g.FillPath(b, p);

            DocKind kind = DocKinds.Of(path);
            DocKinds.DrawBadge(g, kind, new Rectangle(r.X + 6, r.Y + (r.Height - 24) / 2, 24, 24));

            using (Font f = Theme.UiSemi(9.25f))
                TextRenderer.DrawText(g, Path.GetFileName(path), f, new Rectangle(r.X + 44, r.Y, nameW - 12, r.Height),
                    Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            using (Font f = Theme.Ui(8.75f))
            {
                TextRenderer.DrawText(g, Path.GetDirectoryName(path), f, new Rectangle(r.X + 44 + nameW, r.Y, folderW - 12, r.Height),
                    Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis);

                string when = "";
                try { when = Ago(File.GetLastWriteTime(path)); } catch { }
                TextRenderer.DrawText(g, when, f, new Rectangle(r.X + 44 + nameW + folderW, r.Y, r.Width - 44 - nameW - folderW - 8, r.Height),
                    Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            using (Pen line = new Pen(Theme.Mix(Theme.Raised, Theme.LineSoft, 0.6)))
                g.DrawLine(line, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
        }

        static string Ago(DateTime when)
        {
            TimeSpan ago = DateTime.Now - when;
            if (ago.TotalMinutes < 1) return "just now";
            if (ago.TotalHours < 1) return (int)ago.TotalMinutes + " min ago";
            if (when.Date == DateTime.Today) return "today " + when.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (when.Date == DateTime.Today.AddDays(-1)) return "yesterday";
            if (ago.TotalDays < 7) return when.ToString("dddd", CultureInfo.InvariantCulture);
            return when.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }

        void PaintScrollMark(Graphics g)
        {
            if (_contentHeight <= Height) return;
            int track = Height - 16;
            int thumb = Math.Max(30, track * Height / _contentHeight);
            int y = 8 + (track - thumb) * _scroll / Math.Max(1, _contentHeight - Height);
            using (GraphicsPath p = Theme.Round(new Rectangle(Width - 8, y, 4, thumb), 2))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(90, Theme.TextFaint))) g.FillPath(b, p);
        }
    }
}
