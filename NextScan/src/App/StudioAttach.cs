// =============================================================================
// NextScan Studio - what the operator attaches to a message
// Plan ref: docs/AI_LAYER.md
//
// A file, a screenshot, a folder of photographs: anything the operator drops on
// the chat, pastes into it or picks with the paperclip. Whatever it is, it has
// to become something a model can be given, and the models differ: all of them
// take pictures, none of them takes a .docx. So the work is done here, once,
// the same for every provider:
//
//   pictures     -> a JPEG no larger than a model reads (1568 px), turned the
//                   right way up (a phone's photograph carries its orientation
//                   in a tag GDI+ ignores);
//   Word, Excel, PowerPoint (and their older forms, ODF, RTF)
//                -> the text, read straight out of the file where it is a zip of
//                   XML (docx, xlsx, pptx: no engine needed, and fast), through
//                   the document engine's converter where it is not;
//   PDF          -> its text; and where it has none, because it is a scan, its
//                   pages as pictures;
//   text, CSV, JSON, HTML, source code, logs
//                -> read, whatever its encoding;
//   a zip        -> what is in it;
//   anything else-> its name and size, said plainly, so the model knows it exists
//                   and knows it cannot read it.
//
// Reading can take a while (a PDF is converted), so a file is an attachment the
// moment it is dropped -- a tile in the box, spinning -- and reads in the
// background. Nothing here talks to a provider; what is read stays on this
// computer until the operator sends the message.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using NextScan.Ai;

namespace NextScan.App
{
    public enum AttachState { Reading, Ready, Failed }

    /// <summary>One thing attached: what it is, whether it has been read yet, and what the model will be given from it.</summary>
    public class ChatAttachment : IDisposable
    {
        public string Name = "";

        /// <summary>Where it came from on disk. Empty for a pasted or captured picture.</summary>
        public string Path = "";

        /// <summary>picture, word, sheet, slides, pdf, text, archive or other.</summary>
        public string Kind = "other";

        public long Size;
        public AttachState State = AttachState.Reading;

        /// <summary>What to know about it: cut short, scanned, cannot be read, the reason it failed.</summary>
        public string Note = "";

        /// <summary>A small square of it (96 px) for the tile. Null for anything that is not a picture.</summary>
        public Bitmap Thumb;

        /// <summary>What the model will look at: the picture itself, or the pages of a scanned PDF.</summary>
        public List<AiPicture> Pictures = new List<AiPicture>();

        /// <summary>What the model will read.</summary>
        public string Text = "";

        /// <summary>Pages or slides, where there is such a thing. 0 otherwise.</summary>
        public int Pages;

        /// <summary>Fonts worth knowing about (a Bijoy font, which holds legacy ANSI Bengali).</summary>
        public string Fonts = "";

        /// <summary>Put back from a saved conversation: only its name is left, so it is drawn without a picture or a way to remove it.</summary>
        public bool Restored;

        /// <summary>Its tile has been handed on to a message, which now owns the picture.</summary>
        public bool Released;

        public string KindName
        {
            get
            {
                switch (Kind)
                {
                    case "picture": return "Picture";
                    case "word": return "Word document";
                    case "sheet": return "Spreadsheet";
                    case "slides": return "Presentation";
                    case "pdf": return "PDF";
                    case "text": return "Text file";
                    case "archive": return "Archive";
                    default: return "File";
                }
            }
        }

        /// <summary>What fits under the name on a tile: pages or slides and size. The colour of the mark says what kind it is.</summary>
        public string Short
        {
            get
            {
                var s = new StringBuilder();
                if (Pages > 0) s.Append(Pages).Append(Kind == "slides" ? " slides" : Kind == "sheet" ? (Pages == 1 ? " sheet" : " sheets") : Pages == 1 ? " page" : " pages");
                if (Size > 0) s.Append(s.Length > 0 ? " · " : "").Append(AttachReader.Bytes(Size));
                return s.Length > 0 ? s.ToString() : KindName;
            }
        }

        /// <summary>"Word document · 24 KB", and what is worth adding.</summary>
        public string Summary
        {
            get
            {
                var s = new StringBuilder(KindName);
                if (Pages > 0) s.Append(" · ").Append(Pages).Append(Kind == "slides" ? " slides" : Pages == 1 ? " page" : " pages");
                if (Size > 0) s.Append(" · ").Append(AttachReader.Bytes(Size));
                return s.ToString();
            }
        }

        public void Dispose()
        {
            if (Released) return;
            if (Thumb != null) { Thumb.Dispose(); Thumb = null; }
        }
    }

    public static class AttachReader
    {
        /// <summary>The most the model is given to read from one file, in characters (about ten thousand words).</summary>
        public const int TextPerFile = 40000;

        /// <summary>And from everything attached to one message.</summary>
        public const int TextInAll = 120000;

        /// <summary>Pages of a scanned PDF shown to the model.</summary>
        public const int PdfPages = 8;

        /// <summary>Pages of a document shown as pictures beside its text: the look of it, without a page of tokens for every page.</summary>
        public const int SeenPages = 4, SeenSheets = 3, SeenSlides = 6;

        /// <summary>The longest edge of a picture sent: what every provider shrinks anything larger to.</summary>
        public const int LongestEdge = 1568;

        static readonly string[] PictureTypes = { ".jpg", ".jpeg", ".jfif", ".png", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".dib" };
        static readonly string[] PictureTypesNotReadable = { ".webp", ".heic", ".heif", ".avif", ".svg", ".psd", ".raw", ".cr2", ".nef" };
        static readonly string[] WordTypes = { ".docx", ".docm", ".dotx", ".dotm" };
        static readonly string[] WordOld = { ".doc", ".dot", ".rtf", ".odt", ".ott", ".wps" };
        static readonly string[] SheetTypes = { ".xlsx", ".xlsm", ".xltx", ".xltm" };
        static readonly string[] SheetOld = { ".xls", ".xlt", ".ods", ".ots" };
        static readonly string[] SlideTypes = { ".pptx", ".pptm", ".ppsx", ".potx" };
        static readonly string[] SlideOld = { ".ppt", ".pps", ".odp", ".otp" };
        static readonly string[] TextTypes =
        {
            ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".xhtml", ".log", ".ini", ".cfg", ".conf",
            ".yaml", ".yml", ".toml", ".js", ".ts", ".css", ".cs", ".vb", ".py", ".java", ".c", ".cpp", ".h", ".hpp", ".go", ".rs",
            ".php", ".rb", ".sql", ".ps1", ".bat", ".cmd", ".sh", ".tex", ".srt", ".vtt", ".eml", ".ics", ".vcf", ".reg", ".inf",
        };

        static bool In(string[] list, string ext) { return Array.IndexOf(list, ext) >= 0; }

        // =====================================================================
        // Making an attachment
        // =====================================================================

        /// <summary>
        /// An attachment for a file on disk, at once: its name, size and what
        /// kind it looks like. <see cref="Read"/> does the reading, and is
        /// meant for a worker thread.
        /// </summary>
        public static ChatAttachment ForFile(string path)
        {
            var a = new ChatAttachment { Path = path, Name = System.IO.Path.GetFileName(path) };
            try { a.Size = new FileInfo(path).Length; } catch { }
            a.Kind = KindOf(path);
            return a;
        }

        public static string KindOf(string path)
        {
            string ext = System.IO.Path.GetExtension(path ?? "").ToLowerInvariant();
            if (In(PictureTypes, ext) || In(PictureTypesNotReadable, ext)) return "picture";
            if (In(WordTypes, ext) || In(WordOld, ext)) return "word";
            if (In(SheetTypes, ext) || In(SheetOld, ext)) return "sheet";
            if (In(SlideTypes, ext) || In(SlideOld, ext)) return "slides";
            if (ext == ".pdf") return "pdf";
            if (ext == ".zip" || ext == ".docx.zip") return "archive";
            if (In(TextTypes, ext)) return "text";
            return "other";
        }

        /// <summary>A picture that has no file: a screenshot pasted in, the page on the glass.</summary>
        public static ChatAttachment ForBitmap(Bitmap bitmap, string name)
        {
            var a = new ChatAttachment { Name = name, Kind = "picture" };
            try { AddPicture(a, bitmap, name); a.State = AttachState.Ready; }
            catch (Exception ex) { a.State = AttachState.Failed; a.Note = ex.Message; }
            return a;
        }

        /// <summary>Reads it, filling in what the model will be given. Sets the state; never throws.</summary>
        public static void Read(ChatAttachment a)
        {
            try
            {
                if (!File.Exists(a.Path)) throw new FileNotFoundException("The file is not there any more.");
                string ext = System.IO.Path.GetExtension(a.Path).ToLowerInvariant();

                switch (a.Kind)
                {
                    case "picture": ReadPicture(a, ext); break;
                    case "word": ReadWord(a, ext); break;
                    case "sheet": ReadSheet(a, ext); break;
                    case "slides": ReadSlides(a, ext); break;
                    case "pdf": ReadPdf(a); break;
                    case "archive": ReadArchive(a); break;
                    case "text": ReadTextFile(a, ext); break;
                    default: ReadOther(a); break;
                }
                a.State = AttachState.Ready;
            }
            catch (Exception ex)
            {
                a.State = AttachState.Failed;
                a.Note = Plain(ex);
            }
        }

        static string Plain(Exception ex)
        {
            string m = (ex.GetBaseException().Message ?? "").Split('\n')[0].Trim();
            return m.Length > 160 ? m.Substring(0, 157) + "…" : m;
        }

        // =====================================================================
        // Pictures
        // =====================================================================

        static void ReadPicture(ChatAttachment a, string ext)
        {
            if (In(PictureTypesNotReadable, ext))
                throw new NotSupportedException(ext.ToUpperInvariant().TrimStart('.') + " pictures cannot be read here. Save it as JPG or PNG and attach that.");
            if (a.Size > 80L * 1024 * 1024) throw new NotSupportedException("That picture is over 80 MB.");

            // The file is read whole and released at once: a picture opened from
            // its path stays locked for as long as the bitmap lives.
            byte[] bytes = File.ReadAllBytes(a.Path);
            using (var ms = new MemoryStream(bytes))
            using (Image image = Image.FromStream(ms, false, false))
            using (Bitmap upright = Upright(image))
                AddPicture(a, upright, a.Name);
        }

        /// <summary>The picture turned the way up its file says it is: phones write it in a tag rather than turning the pixels.</summary>
        static Bitmap Upright(Image image)
        {
            var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            copy.SetResolution(image.HorizontalResolution > 1 ? image.HorizontalResolution : 96f, image.VerticalResolution > 1 ? image.VerticalResolution : 96f);
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.Clear(Color.White);            // transparent parts as white, not black
                g.DrawImage(image, 0, 0, image.Width, image.Height);
            }
            try
            {
                foreach (PropertyItem p in image.PropertyItems)
                {
                    if (p.Id != 0x0112 || p.Value == null || p.Value.Length < 2) continue;
                    switch (BitConverter.ToUInt16(p.Value, 0))
                    {
                        case 3: copy.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                        case 6: copy.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                        case 8: copy.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                        case 2: copy.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                        case 4: copy.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
                    }
                    break;
                }
            }
            catch { }
            return copy;
        }

        /// <summary>Adds a bitmap as a picture the model will see, and makes the tile's thumbnail from it.</summary>
        static void AddPicture(ChatAttachment a, Bitmap source, string name)
        {
            a.Pictures.Add(new AiPicture { Bytes = Jpeg(source, LongestEdge), MediaType = "image/jpeg", Name = name });
            if (a.Thumb == null) a.Thumb = Square(source, 96);
        }

        /// <summary>A JPEG at quality 92 of the bitmap, no longer than <paramref name="longest"/> on its longer side.</summary>
        public static byte[] Jpeg(Bitmap source, int longest)
        {
            int w = source.Width, h = source.Height;
            double shrink = Math.Min(1.0, (double)longest / Math.Max(w, h));
            int tw = Math.Max(1, (int)Math.Round(w * shrink)), th = Math.Max(1, (int)Math.Round(h * shrink));
            using (var small = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, tw, th));
                }
                using (var stream = new MemoryStream())
                {
                    ImageCodecInfo codec = null;
                    foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
                    if (codec == null) { small.Save(stream, ImageFormat.Jpeg); return stream.ToArray(); }
                    using (var p = new EncoderParameters(1))
                    {
                        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
                        small.Save(stream, codec, p);
                    }
                    return stream.ToArray();
                }
            }
        }

        /// <summary>The middle of the picture, as a square of <paramref name="side"/> pixels.</summary>
        public static Bitmap Square(Bitmap source, int side)
        {
            int edge = Math.Min(source.Width, source.Height);
            var from = new Rectangle((source.Width - edge) / 2, (source.Height - edge) / 2, edge, edge);
            var thumb = new Bitmap(side, side, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, side, side), from, GraphicsUnit.Pixel);
            }
            return thumb;
        }

        // =====================================================================
        // Word
        // =====================================================================

        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        static void ReadWord(ChatAttachment a, string ext)
        {
            if (In(WordTypes, ext))
            {
                string fonts;
                a.Text = Cut(a, ReadDocx(a.Path, out fonts));
                a.Fonts = fonts;
                LookAtPages(a);
                return;
            }
            // .doc, .rtf, .odt: the document engine's converter reads them.
            a.Text = Cut(a, ConvertToText(a.Path, 0x45));
            LookAtPages(a);
        }

        /// <summary>
        /// The first pages, drawn by the document engine, as pictures: the text says what
        /// a document contains, the pictures say what it looks like, and to rebuild it
        /// the model needs both. Failing to draw them is not failing to read the file.
        /// </summary>
        static void LookAtPages(ChatAttachment a)
        {
            if (!NextScan.Docs.DocsEngine.IsInstalled || a.Size > 15L * 1024 * 1024) return;
            try
            {
                int total;
                List<PageShot> shots = Sight.EnginePages(a.Path, 1100, SeenPages, out total);
                if (total > 0) a.Pages = total;
                AddShots(a, shots);
                if (shots.Count > 0)
                    a.Note = Join(a.Note, shots.Count == total ? "Its pages are also shown as pictures." : "Its first " + shots.Count + " of " + total + " pages are also shown as pictures.");
            }
            catch (Exception ex) { a.Note = Join(a.Note, "Its pages could not be drawn (" + Plain(ex) + ")."); }
        }

        static string Join(string a, string b) { return string.IsNullOrEmpty(a) ? b : a + " " + b; }

        /// <summary>Adds drawn pages to what the model is given, named for the file and the page.</summary>
        static void AddShots(ChatAttachment a, List<PageShot> shots)
        {
            foreach (PageShot shot in shots)
            {
                using (shot) { if (shot.Image != null) AddPicture(a, shot.Image, a.Name + " â€” " + shot.Caption); }
            }
        }

        /// <summary>The text of a .docx, in order: paragraphs, headings and list items marked, table cells with " | " between them.</summary>
        static string ReadDocx(string path, out string fonts)
        {
            fonts = "";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                ZipArchiveEntry entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException("This is not a Word document.");
                var doc = new XmlDocument();
                using (Stream s = entry.Open()) doc.Load(s);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("w", W);

                // Bijoy fonts (SutonnyMJ and the rest, names ending in MJ) hold
                // legacy ANSI Bengali: the letters read as English gibberish
                // unless the model is told what it is looking at.
                var fontNames = new SortedDictionary<string, int>();
                foreach (XmlNode f in doc.SelectNodes("//w:rFonts", ns))
                {
                    string n = Attr(f, "ascii") ?? Attr(f, "hAnsi");
                    if (!string.IsNullOrEmpty(n)) { int c; fontNames.TryGetValue(n, out c); fontNames[n] = c + 1; }
                }
                var bijoy = new List<string>();
                foreach (var kv in fontNames) if (kv.Key.EndsWith("MJ", StringComparison.OrdinalIgnoreCase)) bijoy.Add(kv.Key);
                if (bijoy.Count > 0) fonts = string.Join(", ", bijoy.ToArray());

                var text = new StringBuilder();
                XmlNode lastRow = null, lastCell = null;
                foreach (XmlNode p in doc.SelectNodes("//w:p", ns))
                {
                    XmlNode cell = p.SelectSingleNode("ancestor::w:tc[1]", ns);
                    XmlNode row = cell == null ? null : cell.ParentNode;

                    if (text.Length > 0)
                    {
                        if (cell != null && row == lastRow && cell != lastCell) text.Append(" | ");
                        else if (cell != null && cell == lastCell) text.Append(" / ");
                        else text.Append('\n');
                    }
                    lastRow = row; lastCell = cell;

                    var line = new StringBuilder();
                    XmlNode style = p.SelectSingleNode("w:pPr/w:pStyle", ns);
                    string styleName = style == null ? "" : (Attr(style, "val") ?? "");
                    if (styleName.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || styleName.Equals("Title", StringComparison.OrdinalIgnoreCase))
                    {
                        int level;
                        line.Append(styleName.Length > 7 && int.TryParse(styleName.Substring(7), out level) ? new string('#', Math.Min(6, level)) + " " : "# ");
                    }
                    else if (p.SelectSingleNode("w:pPr/w:numPr", ns) != null && cell == null) line.Append("- ");

                    foreach (XmlNode n in p.SelectNodes(".//w:t | .//w:tab | .//w:br | .//w:cr", ns))
                    {
                        switch (n.LocalName)
                        {
                            case "t": line.Append(n.InnerText); break;
                            case "tab": line.Append('\t'); break;
                            default: line.Append(' '); break;
                        }
                    }
                    text.Append(line);
                }
                return Tidy(text.ToString());
            }
        }

        static string Attr(XmlNode n, string local)
        {
            if (n == null || n.Attributes == null) return null;
            foreach (XmlAttribute a in n.Attributes) if (a.LocalName == local) return a.Value;
            return null;
        }

        // =====================================================================
        // Excel
        // =====================================================================

        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        static void ReadSheet(ChatAttachment a, string ext)
        {
            string path = a.Path;
            string temp = null;
            try
            {
                if (In(SheetOld, ext)) { temp = ConvertFile(path, 0x101, ".xlsx"); path = temp; }
                a.Text = Cut(a, ReadXlsx(path, a));
                try
                {
                    int sheets; string notes;
                    List<PageShot> shots = Sight.Sheets(path, SeenSheets, 45, out sheets, out notes);
                    AddShots(a, shots);
                    if (shots.Count > 0) a.Note = Join(a.Note, "The sheets are also drawn as pictures (colours, borders, widths, merged cells). " + notes);
                }
                catch (Exception ex) { a.Note = Join(a.Note, "The sheets could not be drawn (" + Plain(ex) + ")."); }
            }
            finally { DeleteQuietly(temp); }
        }

        static string ReadXlsx(string path, ChatAttachment a)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                // Shared strings: cells hold a number into this list.
                var shared = new List<string>();
                ZipArchiveEntry ss = zip.GetEntry("xl/sharedStrings.xml");
                if (ss != null)
                {
                    var d = new XmlDocument();
                    using (Stream s = ss.Open()) d.Load(s);
                    var m = new XmlNamespaceManager(d.NameTable);
                    m.AddNamespace("s", S);
                    foreach (XmlNode si in d.SelectNodes("//s:si", m))
                    {
                        var t = new StringBuilder();
                        foreach (XmlNode n in si.SelectNodes(".//s:t[not(ancestor::s:rPh)]", m)) t.Append(n.InnerText);
                        shared.Add(t.ToString());
                    }
                }

                // The sheets, in the order the workbook lists them, by name.
                var sheets = new List<KeyValuePair<string, string>>();
                ZipArchiveEntry wb = zip.GetEntry("xl/workbook.xml");
                ZipArchiveEntry rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
                if (wb != null && rels != null)
                {
                    var d = new XmlDocument(); using (Stream s = wb.Open()) d.Load(s);
                    var r = new XmlDocument(); using (Stream s = rels.Open()) r.Load(s);
                    var targets = new Dictionary<string, string>();
                    foreach (XmlNode rel in r.DocumentElement.ChildNodes)
                        if (rel.Attributes != null && rel.Attributes["Id"] != null && rel.Attributes["Target"] != null) targets[rel.Attributes["Id"].Value] = rel.Attributes["Target"].Value;
                    foreach (XmlNode sh in d.GetElementsByTagName("sheet"))
                    {
                        string name = sh.Attributes["name"] == null ? "Sheet" : sh.Attributes["name"].Value;
                        string id = Attr(sh, "id");
                        string target;
                        if (id != null && targets.TryGetValue(id, out target))
                        {
                            target = target.TrimStart('/');
                            sheets.Add(new KeyValuePair<string, string>(name, target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target));
                        }
                    }
                }
                if (sheets.Count == 0)
                    for (int i = 1; i <= 20 && zip.GetEntry("xl/worksheets/sheet" + i + ".xml") != null; i++)
                        sheets.Add(new KeyValuePair<string, string>("Sheet" + i, "xl/worksheets/sheet" + i + ".xml"));

                var text = new StringBuilder();
                a.Pages = sheets.Count;
                foreach (var sheet in sheets)
                {
                    ZipArchiveEntry e = zip.GetEntry(sheet.Value);
                    if (e == null) continue;
                    var d = new XmlDocument(); using (Stream s = e.Open()) d.Load(s);
                    var m = new XmlNamespaceManager(d.NameTable);
                    m.AddNamespace("s", S);

                    var rows = new List<KeyValuePair<int, SortedDictionary<int, string>>>();
                    int widest = 0;
                    foreach (XmlNode row in d.SelectNodes("//s:sheetData/s:row", m))
                    {
                        int rowNumber = 0;
                        string rn = Attr(row, "r");
                        if (rn != null) int.TryParse(rn, out rowNumber);
                        var cells = new SortedDictionary<int, string>();
                        int auto = 0;
                        foreach (XmlNode c in row.SelectNodes("s:c", m))
                        {
                            int col = ColumnOf(Attr(c, "r"), auto);
                            auto = col + 1;
                            string type = Attr(c, "t");
                            XmlNode v = c.SelectSingleNode("s:v", m);
                            string value = v == null ? "" : v.InnerText;
                            if (type == "s") { int idx; value = int.TryParse(value, out idx) && idx >= 0 && idx < shared.Count ? shared[idx] : ""; }
                            else if (type == "inlineStr") { XmlNode isn = c.SelectSingleNode("s:is", m); value = isn == null ? "" : isn.InnerText; }
                            else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                            if (value.Length == 0) continue;
                            cells[col] = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
                            if (col + 1 > widest) widest = col + 1;
                        }
                        if (cells.Count > 0) rows.Add(new KeyValuePair<int, SortedDictionary<int, string>>(rowNumber > 0 ? rowNumber : rows.Count + 1, cells));
                    }

                    text.Append("## ").Append(sheet.Key).Append("  (").Append(rows.Count).Append(rows.Count == 1 ? " row" : " rows").Append(", ").Append(widest).Append(" columns)\n");
                    if (rows.Count == 0) { text.Append("(empty)\n\n"); continue; }

                    // Column letters on top and row numbers down the side, so
                    // "the total in D14" means the same to the model as to the operator.
                    int cols = Math.Min(widest, 30);
                    text.Append('\t');
                    for (int c = 0; c < cols; c++) text.Append(ColumnName(c)).Append(c < cols - 1 ? "\t" : "");
                    text.Append('\n');
                    int shown = 0;
                    foreach (var row in rows)
                    {
                        if (++shown > 300) { text.Append("… ").Append(rows.Count - 300).Append(" more rows\n"); break; }
                        text.Append(row.Key);
                        for (int c = 0; c < cols; c++) { string v; row.Value.TryGetValue(c, out v); text.Append('\t').Append(v ?? ""); }
                        text.Append('\n');
                    }
                    if (widest > cols) text.Append("… ").Append(widest - cols).Append(" more columns\n");
                    text.Append('\n');
                }
                a.Note = "Numbers are as stored: a date is a serial number, a formula shows its last result.";
                return text.ToString().TrimEnd();
            }
        }

        static int ColumnOf(string reference, int fallback)
        {
            if (string.IsNullOrEmpty(reference)) return fallback;
            int n = 0, i = 0;
            while (i < reference.Length && char.IsLetter(reference[i])) { n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1); i++; }
            return n > 0 ? n - 1 : fallback;
        }

        static string ColumnName(int index)
        {
            var s = new StringBuilder();
            for (int n = index + 1; n > 0; n = (n - 1) / 26) s.Insert(0, (char)('A' + (n - 1) % 26));
            return s.ToString();
        }

        // =====================================================================
        // PowerPoint
        // =====================================================================

        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

        static void ReadSlides(ChatAttachment a, string ext)
        {
            string path = a.Path, temp = null;
            try
            {
                if (In(SlideOld, ext)) { temp = ConvertFile(path, 0x81, ".pptx"); path = temp; }
                a.Text = Cut(a, ReadPptx(path, a));
                try
                {
                    int count; string notes;
                    List<PageShot> shots = Sight.Slides(path, 1100, SeenSlides, out count, out notes);
                    AddShots(a, shots);
                    if (shots.Count > 0) a.Note = Join(a.Note, (shots.Count < count ? "The first " + shots.Count + " slides are" : "The slides are") + " also drawn as pictures. " + notes);
                }
                catch (Exception ex) { a.Note = Join(a.Note, "The slides could not be drawn (" + Plain(ex) + ")."); }
            }
            finally { DeleteQuietly(temp); }
        }

        static string ReadPptx(string path, ChatAttachment a)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var slides = new List<KeyValuePair<int, ZipArchiveEntry>>();
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    Match m = Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$");
                    if (m.Success) slides.Add(new KeyValuePair<int, ZipArchiveEntry>(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), e));
                }
                slides.Sort((x, y) => x.Key.CompareTo(y.Key));
                a.Pages = slides.Count;

                var text = new StringBuilder();
                int n = 0;
                foreach (var slide in slides)
                {
                    var d = new XmlDocument();
                    using (Stream s = slide.Value.Open()) d.Load(s);
                    var ns = new XmlNamespaceManager(d.NameTable);
                    ns.AddNamespace("a", A);
                    text.Append("--- Slide ").Append(++n).Append(" ---\n");
                    foreach (XmlNode p in d.SelectNodes("//a:p", ns))
                    {
                        var line = new StringBuilder();
                        foreach (XmlNode t in p.SelectNodes(".//a:t | .//a:br", ns)) line.Append(t.LocalName == "br" ? "\n" : t.InnerText);
                        if (line.ToString().Trim().Length > 0) text.Append(line.ToString().Trim()).Append('\n');
                    }
                    text.Append('\n');
                }
                return text.ToString().TrimEnd();
            }
        }

        // =====================================================================
        // PDF
        // =====================================================================

        static void ReadPdf(ChatAttachment a)
        {
            if (a.Size > 60L * 1024 * 1024) throw new NotSupportedException("That PDF is over 60 MB.");
            if (!NextScan.Docs.DocsEngine.IsInstalled) throw new NotSupportedException("Reading a PDF needs the document engine, which is not installed.");

            string text = "";
            try { text = ConvertToText(a.Path, 0x45); } catch (Exception ex) { a.Note = Plain(ex); }
            string trimmed = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            bool scan = trimmed.Length < 200;
            if (!scan) { a.Text = Cut(a, Tidy(text)); a.Note = ""; }

            // Its pages as pictures: for a scan they are all there is; for one with text
            // they show how it is laid out, which the text does not.
            int wanted = scan ? PdfPages : SeenPages, total = 0;
            List<PageShot> shots = null;
            try { shots = Sight.EnginePages(a.Path, scan ? 1200 : 1100, wanted, out total); }
            catch (Exception ex)
            {
                if (scan) throw;
                a.Note = Join(a.Note, "Its pages could not be drawn (" + Plain(ex) + ").");
            }
            if (shots == null) return;
            if (total > 0) a.Pages = total;
            AddShots(a, shots);
            if (scan)
            {
                if (a.Pictures.Count == 0) throw new InvalidDataException("No pages could be drawn from this PDF.");
                a.Text = trimmed.Length > 0 ? Cut(a, Tidy(text)) : "";
                a.Note = "A scan: " + (total > shots.Count ? "the first " + shots.Count + " of " + total + " pages are shown as pictures." : "its pages are shown as pictures.");
            }
            else if (shots.Count > 0)
                a.Note = Join(a.Note, total > shots.Count ? "Its first " + shots.Count + " of " + total + " pages are also shown as pictures." : "Its pages are also shown as pictures.");
        }

        static int Order(string name)
        {
            string digits = "";
            foreach (char c in name) if (char.IsDigit(c)) digits += c;
            int n;
            return int.TryParse(digits, out n) ? n : int.MaxValue;
        }

        // =====================================================================
        // Text, archives, the rest
        // =====================================================================

        static void ReadTextFile(ChatAttachment a, string ext)
        {
            if (a.Size > 25L * 1024 * 1024) throw new NotSupportedException("That file is over 25 MB.");
            string text = DecodeText(File.ReadAllBytes(a.Path));
            if (ext == ".html" || ext == ".htm" || ext == ".xhtml") text = StripHtml(text);
            a.Text = Cut(a, Tidy(text));
        }

        static void ReadArchive(ChatAttachment a)
        {
            var list = new StringBuilder();
            int count = 0;
            using (var fs = new FileStream(a.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                    count++;
                    if (count <= 60) list.Append(e.FullName).Append("  (").Append(Bytes(e.Length)).Append(")\n");
                }
            }
            if (count > 60) list.Append("… and ").Append(count - 60).Append(" more\n");
            a.Text = "Contents of the archive (" + count + (count == 1 ? " file" : " files") + "):\n" + list.ToString().TrimEnd();
            a.Note = "Only the list of files is given, not what is in them.";
        }

        static void ReadOther(ChatAttachment a)
        {
            // Anything that turns out to be text is text, whatever it is called.
            if (a.Size <= 5L * 1024 * 1024 && a.Size > 0)
            {
                byte[] bytes = File.ReadAllBytes(a.Path);
                if (LooksLikeText(bytes)) { a.Kind = "text"; a.Text = Cut(a, Tidy(DecodeText(bytes))); return; }
            }
            a.Note = "This kind of file cannot be read here; the model is told its name and size only.";
        }

        static bool LooksLikeText(byte[] bytes)
        {
            int n = Math.Min(bytes.Length, 4096), odd = 0;
            if (n >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF))) return true;
            for (int i = 0; i < n; i++)
            {
                byte b = bytes[i];
                if (b == 0) return false;
                if (b < 9 || (b > 13 && b < 32)) odd++;
            }
            return odd * 50 < Math.Max(1, n);
        }

        /// <summary>Text from bytes of any encoding: by its byte-order mark, else as UTF-8 if it is valid UTF-8, else as Windows-1252.</summary>
        static string DecodeText(byte[] b)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            try { return new UTF8Encoding(false, true).GetString(b); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252).GetString(b); }
        }

        static string StripHtml(string html)
        {
            html = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<(br|/p|/div|/li|/tr|/h\d)[^>]*>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<[^>]+>", " ");
            return System.Net.WebUtility.HtmlDecode(html);
        }

        // =====================================================================
        // The document engine's converter
        // =====================================================================

        static string TempFolder()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NextScan", "attach");
            Directory.CreateDirectory(dir);
            return dir;
        }

        static string ConvertFile(string from, int formatTo, string extension)
        {
            if (!NextScan.Docs.DocsEngine.IsInstalled) throw new NotSupportedException("Reading this kind of file needs the document engine, which is not installed.");
            string to = System.IO.Path.Combine(TempFolder(), Guid.NewGuid().ToString("N") + extension);
            NextScan.Docs.DocsEngine.ConvertWith(from, to, formatTo);
            return to;
        }

        static string ConvertToText(string from, int format)
        {
            string temp = ConvertFile(from, format, ".txt");
            try { return DecodeText(File.ReadAllBytes(temp)); }
            finally { DeleteQuietly(temp); }
        }

        static void DeleteQuietly(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { }
        }

        // =====================================================================
        // Tidying and cutting
        // =====================================================================

        /// <summary>Line endings made one kind, runs of blank lines made one, trailing spaces gone.</summary>
        static string Tidy(string text)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }

        /// <summary>The text no longer than one file may give, with a note where it was cut.</summary>
        static string Cut(ChatAttachment a, string text)
        {
            if (text.Length <= TextPerFile) return text;
            a.Note = (a.Note.Length > 0 ? a.Note + " " : "") + "Cut after " + TextPerFile.ToString("N0", CultureInfo.InvariantCulture) + " of " + text.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters.";
            return text.Substring(0, TextPerFile);
        }

        public static string Bytes(long n)
        {
            if (n < 1024) return n + " B";
            if (n < 1024 * 1024) return (n / 1024.0).ToString(n < 10240 ? "0.0" : "0", CultureInfo.InvariantCulture) + " KB";
            return (n / 1048576.0).ToString(n < 10485760 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
        }
    }
}
