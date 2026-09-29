// =============================================================================
// NextScan Studio - what a document looks like
// Plan ref: docs/AI_LAYER.md, docs/DOCUMENT_WORKSPACE.md
//
// A model that is only given a document's text cannot tell whether it looks
// right: "the total is in the second column" says nothing about a page that
// prints crooked. So wherever the assistant is given a document -- attached to
// a message, or the one it has just changed -- it is given the pages as
// pictures as well.
//
//   Word, PDF, RTF, ODT     drawn by the document engine (its layout, so the page
//                           breaks fall where they will print);
//   Excel                   drawn here from the workbook's own XML: column widths,
//                           row heights, fills, borders, merged cells, fonts and
//                           number formats, with the column letters and row
//                           numbers a person reads addresses from;
//   PowerPoint              drawn here from the slide XML: shapes, text, pictures,
//                           tables, the layout's and the master's decoration.
//
// Excel and PowerPoint are drawn by this file, not the converter, because the
// converter's own drawing of a workbook or a slide stops the converter process
// (an access violation inside its script engine, every time, on every file).
// Nothing here is a full office renderer; it is meant to show a model the
// arrangement -- what is where, how big, what colour -- faithfully enough to
// rebuild it. What it leaves out (charts, most effects) it says so.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace NextScan.App
{
    /// <summary>One drawn page, sheet or slide, and what to tell the model about it.</summary>
    public class PageShot : IDisposable
    {
        public Bitmap Image;
        public string Caption = "";
        /// <summary>How many pixels make a millimetre of the real page in this picture; 0 where there is no page (a sheet).</summary>
        public double PxPerMm;
        public void Dispose() { if (Image != null) { Image.Dispose(); Image = null; } }
    }

    public static class Sight
    {
        // =====================================================================
        // Pages the engine draws: Word, PDF, RTF, ODT
        // =====================================================================

        /// <summary>
        /// The pages of a file the converter can lay out, as bitmaps <paramref name="width"/>
        /// pixels wide. <paramref name="total"/> is how many pages the file has.
        /// </summary>
        public static List<PageShot> EnginePages(string path, int width, int max, out int total)
        {
            total = 0;
            var shots = new List<PageShot>();
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NextScan", "sight");
            Directory.CreateDirectory(temp);
            string zip = System.IO.Path.Combine(temp, Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                string thumb = "<m_oThumbnail><format>4</format><aspect>1</aspect><first>false</first><width>" + width + "</width><height>" + (int)(width * 1.5) + "</height></m_oThumbnail>";
                NextScan.Docs.DocsEngine.ConvertWith(path, zip, 0x404, 0, null, thumb, 0);
                using (var fs = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    var all = new List<ZipArchiveEntry>();
                    foreach (ZipArchiveEntry e in archive.Entries) if (e.Length > 0) all.Add(e);
                    all.Sort((x, y) => Number(x.Name).CompareTo(Number(y.Name)));
                    total = all.Count;
                    foreach (ZipArchiveEntry e in all)
                    {
                        if (shots.Count >= max) break;
                        using (Stream s = e.Open())
                        using (var ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            ms.Position = 0;
                            using (var raw = new Bitmap(ms))
                                shots.Add(new PageShot { Image = new Bitmap(raw), Caption = "page " + (shots.Count + 1) + " of " + all.Count });
                        }
                    }
                }
            }
            finally { try { File.Delete(zip); } catch { } }
            return shots;
        }

        static int Number(string name)
        {
            string digits = "";
            foreach (char c in name) if (char.IsDigit(c)) digits += c;
            int n;
            return int.TryParse(digits, out n) ? n : int.MaxValue;
        }

        /// <summary>A ruler grid over a picture of a page: a line every 10 mm, a heavier one and a number every 50, so a position can be read off in millimetres.</summary>
        public static void Grid(Bitmap page, double pxPerMm)
        {
            if (pxPerMm <= 0) return;
            using (Graphics g = Graphics.FromImage(page))
            using (var thin = new Pen(Color.FromArgb(70, 30, 120, 255), 1f))
            using (var heavy = new Pen(Color.FromArgb(130, 30, 120, 255), 1f))
            using (var font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var ink = new SolidBrush(Color.FromArgb(220, 20, 80, 200)))
            using (var back = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                for (int mm = 10; mm * pxPerMm < page.Width; mm += 10)
                {
                    float x = (float)(mm * pxPerMm);
                    g.DrawLine(mm % 50 == 0 ? heavy : thin, x, 0, x, page.Height);
                    if (mm % 50 == 0) { g.FillRectangle(back, x + 1, 1, 30, 14); g.DrawString(mm.ToString(CultureInfo.InvariantCulture), font, ink, x + 2, 1); }
                }
                for (int mm = 10; mm * pxPerMm < page.Height; mm += 10)
                {
                    float y = (float)(mm * pxPerMm);
                    g.DrawLine(mm % 50 == 0 ? heavy : thin, 0, y, page.Width, y);
                    if (mm % 50 == 0) { g.FillRectangle(back, 1, y + 1, 30, 14); g.DrawString(mm.ToString(CultureInfo.InvariantCulture), font, ink, 2, y + 1); }
                }
            }
        }

        /// <summary>A rectangle of a page, given in millimetres from its top-left corner, cut out of a picture drawn at <paramref name="pxPerMm"/>.</summary>
        public static Bitmap Cut(Bitmap page, double pxPerMm, double x, double y, double w, double h)
        {
            int px = Math.Max(0, (int)Math.Round(x * pxPerMm)), py = Math.Max(0, (int)Math.Round(y * pxPerMm));
            int pw = Math.Min(page.Width - px, (int)Math.Round(w * pxPerMm)), ph = Math.Min(page.Height - py, (int)Math.Round(h * pxPerMm));
            if (pw < 4 || ph < 4) throw new ArgumentException("That region is outside the page.");
            return page.Clone(new Rectangle(px, py, pw, ph), page.PixelFormat);
        }

        public static byte[] Png(Bitmap b)
        {
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
        }

        // =====================================================================
        // Colours
        // =====================================================================

        static readonly Color[] Indexed = Hex(
            "000000 FFFFFF FF0000 00FF00 0000FF FFFF00 FF00FF 00FFFF 000000 FFFFFF FF0000 00FF00 0000FF FFFF00 FF00FF 00FFFF " +
            "800000 008000 000080 808000 800080 008080 C0C0C0 808080 9999FF 993366 FFFFCC CCFFFF 660066 FF8080 0066CC CCCCFF " +
            "000080 FF00FF FFFF00 00FFFF 800080 800000 008080 0000FF 00CCFF CCFFFF CCFFCC FFFF99 99CCFF FF99CC CC99FF FFCC99 " +
            "3366FF 33CCCC 99CC00 FFCC00 FF9900 FF6600 666699 969696 003366 339966 003300 333300 993300 993366 333399 333333");

        static Color[] Hex(string list)
        {
            string[] p = list.Split(' ');
            var c = new Color[p.Length];
            for (int i = 0; i < p.Length; i++) c[i] = Color.FromArgb(Convert.ToInt32(p[i], 16) | unchecked((int)0xFF000000));
            return c;
        }

        static Color Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return Color.Black;
            s = s.Trim().TrimStart('#');
            if (s.Length == 8) s = s.Substring(2);
            int v;
            return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ? Color.FromArgb(0xFF, (v >> 16) & 255, (v >> 8) & 255, v & 255) : Color.Black;
        }

        static void ToHsl(Color c, out double h, out double s, out double l)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            double d = max - min;
            if (d < 1e-9) { h = 0; s = 0; return; }
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }

        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        static Color FromHsl(double h, double s, double l)
        {
            l = Math.Max(0, Math.Min(1, l));
            double r, g, b;
            if (s < 1e-9) r = g = b = l;
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
                r = Channel(p, q, h + 1.0 / 3); g = Channel(p, q, h); b = Channel(p, q, h - 1.0 / 3);
            }
            return Color.FromArgb(0xFF, (int)Math.Round(r * 255), (int)Math.Round(g * 255), (int)Math.Round(b * 255));
        }

        /// <summary>A colour made lighter (tint above 0) or darker (below 0), as a spreadsheet's colours are.</summary>
        static Color Tint(Color c, double tint)
        {
            if (Math.Abs(tint) < 1e-6) return c;
            double h, s, l;
            ToHsl(c, out h, out s, out l);
            l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
            return FromHsl(h, s, l);
        }

        static string Attr(XmlNode n, string local)
        {
            if (n == null || n.Attributes == null) return null;
            foreach (XmlAttribute a in n.Attributes) if (a.LocalName == local) return a.Value;
            return null;
        }

        static double Num(string s, double fallback)
        {
            double v;
            return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        static XmlDocument Load(ZipArchive zip, string name)
        {
            ZipArchiveEntry e = zip.GetEntry(name);
            if (e == null) return null;
            var d = new XmlDocument { XmlResolver = null };
            using (Stream s = e.Open()) d.Load(s);
            return d;
        }

        /// <summary>The theme's twelve colours in the order the file lists them: dk1, lt1, dk2, lt2, accent1..6, hlink, folHlink.</summary>
        static Color[] Theme(ZipArchive zip, string path)
        {
            var colors = new List<Color>();
            XmlDocument d = Load(zip, path);
            if (d != null)
            {
                var scheme = d.GetElementsByTagName("clrScheme");
                if (scheme.Count > 0)
                    foreach (XmlNode c in scheme[0].ChildNodes)
                    {
                        XmlNode v = c.FirstChild;
                        if (v == null) { colors.Add(Color.Black); continue; }
                        colors.Add(Parse(Attr(v, "val") == null || v.LocalName == "sysClr" ? (Attr(v, "lastClr") ?? "000000") : Attr(v, "val")));
                    }
            }
            if (colors.Count < 10)
            {
                colors = new List<Color>(Hex("000000 FFFFFF 44546A E7E6E6 4472C4 ED7D31 A5A5A5 FFC000 5B9BD5 70AD47 0563C1 954F72"));
            }
            return colors.ToArray();
        }

        // =====================================================================
        // Excel
        // =====================================================================

        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        class XlFont { public string Name = "Calibri"; public double Size = 11; public bool Bold, Italic, Underline, Strike; public Color Color = Color.Black; }
        class XlBorder { public string Left, Right, Top, Bottom; public Color LeftC = Color.Black, RightC = Color.Black, TopC = Color.Black, BottomC = Color.Black; }
        class XlXf { public int NumFmt, Font, Fill, Border; public string H = "", V = "bottom"; public bool Wrap; public int Indent; }
        class XlCell { public int Style; public string Text = ""; public bool IsNumber, IsBool; public double Number; }

        class XlStyles
        {
            public List<XlFont> Fonts = new List<XlFont>();
            public List<Color?> Fills = new List<Color?>();
            public List<XlBorder> Borders = new List<XlBorder>();
            public List<XlXf> Xfs = new List<XlXf>();
            public Dictionary<int, string> Formats = new Dictionary<int, string>();
            public Color[] Theme;
        }

        static Color? XlColor(XmlNode n, Color[] theme)
        {
            if (n == null) return null;
            Color c;
            string rgb = Attr(n, "rgb"), th = Attr(n, "theme"), ix = Attr(n, "indexed");
            if (rgb != null) c = Parse(rgb);
            else if (th != null)
            {
                int t = (int)Num(th, 1);
                int[] map = { 1, 0, 3, 2, 4, 5, 6, 7, 8, 9, 10, 11 };
                c = theme[Math.Min(theme.Length - 1, map[Math.Max(0, Math.Min(11, t))])];
            }
            else if (ix != null) { int i = (int)Num(ix, 0); if (i == 65) c = Color.White; else if (i >= 64) c = Color.Black; else c = Indexed[Math.Max(0, i)]; }
            else return null;
            return Tint(c, Num(Attr(n, "tint"), 0));
        }

        static XlStyles XlStylesOf(ZipArchive zip)
        {
            var st = new XlStyles { Theme = Theme(zip, "xl/theme/theme1.xml") };
            XmlDocument d = Load(zip, "xl/styles.xml");
            if (d == null)
            {
                st.Fonts.Add(new XlFont()); st.Fills.Add(null); st.Borders.Add(new XlBorder()); st.Xfs.Add(new XlXf());
                return st;
            }
            var m = new XmlNamespaceManager(d.NameTable); m.AddNamespace("s", S);
            foreach (XmlNode f in d.SelectNodes("//s:numFmts/s:numFmt", m)) st.Formats[(int)Num(Attr(f, "numFmtId"), 0)] = Attr(f, "formatCode") ?? "";
            foreach (XmlNode f in d.SelectNodes("//s:fonts/s:font", m))
            {
                var font = new XlFont();
                XmlNode n;
                if ((n = f.SelectSingleNode("s:name", m)) != null) font.Name = Attr(n, "val") ?? font.Name;
                if ((n = f.SelectSingleNode("s:sz", m)) != null) font.Size = Num(Attr(n, "val"), 11);
                font.Bold = f.SelectSingleNode("s:b", m) != null && Attr(f.SelectSingleNode("s:b", m), "val") != "0";
                font.Italic = f.SelectSingleNode("s:i", m) != null && Attr(f.SelectSingleNode("s:i", m), "val") != "0";
                font.Underline = f.SelectSingleNode("s:u", m) != null && Attr(f.SelectSingleNode("s:u", m), "val") != "none";
                font.Strike = f.SelectSingleNode("s:strike", m) != null;
                Color? col = XlColor(f.SelectSingleNode("s:color", m), st.Theme);
                if (col.HasValue) font.Color = col.Value;
                st.Fonts.Add(font);
            }
            foreach (XmlNode f in d.SelectNodes("//s:fills/s:fill", m))
            {
                XmlNode p = f.SelectSingleNode("s:patternFill", m);
                if (p == null || Attr(p, "patternType") == "none" || Attr(p, "patternType") == null && p.SelectSingleNode("s:fgColor", m) == null) { st.Fills.Add(null); continue; }
                Color? fg = XlColor(p.SelectSingleNode("s:fgColor", m), st.Theme);
                if (Attr(p, "patternType") == "gray125" || Attr(p, "patternType") == "gray0625") fg = null;
                st.Fills.Add(fg);
            }
            foreach (XmlNode b in d.SelectNodes("//s:borders/s:border", m))
            {
                var border = new XlBorder();
                foreach (string side in new[] { "left", "right", "top", "bottom" })
                {
                    XmlNode n = b.SelectSingleNode("s:" + side, m);
                    string style = n == null ? null : Attr(n, "style");
                    Color? col = n == null ? null : XlColor(n.SelectSingleNode("s:color", m), st.Theme);
                    Color c = col ?? Color.Black;
                    switch (side)
                    {
                        case "left": border.Left = style; border.LeftC = c; break;
                        case "right": border.Right = style; border.RightC = c; break;
                        case "top": border.Top = style; border.TopC = c; break;
                        default: border.Bottom = style; border.BottomC = c; break;
                    }
                }
                st.Borders.Add(border);
            }
            foreach (XmlNode x in d.SelectNodes("//s:cellXfs/s:xf", m))
            {
                var xf = new XlXf { NumFmt = (int)Num(Attr(x, "numFmtId"), 0), Font = (int)Num(Attr(x, "fontId"), 0), Fill = (int)Num(Attr(x, "fillId"), 0), Border = (int)Num(Attr(x, "borderId"), 0) };
                XmlNode a = x.SelectSingleNode("s:alignment", m);
                if (a != null)
                {
                    xf.H = Attr(a, "horizontal") ?? ""; xf.V = Attr(a, "vertical") ?? "bottom";
                    xf.Wrap = Attr(a, "wrapText") == "1" || Attr(a, "wrapText") == "true";
                    xf.Indent = (int)Num(Attr(a, "indent"), 0);
                }
                st.Xfs.Add(xf);
            }
            if (st.Fonts.Count == 0) st.Fonts.Add(new XlFont());
            if (st.Xfs.Count == 0) st.Xfs.Add(new XlXf());
            return st;
        }

        static readonly string[] Months = { "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

        /// <summary>A number the way its cell's format shows it: decimals, thousands, per cent, currency signs, dates. Not every format code in the world, the ones that turn up.</summary>
        static string ShowNumber(double v, string code, int id)
        {
            if (string.IsNullOrEmpty(code))
            {
                switch (id)
                {
                    case 1: code = "0"; break;
                    case 2: code = "0.00"; break;
                    case 3: code = "#,##0"; break;
                    case 4: code = "#,##0.00"; break;
                    case 9: code = "0%"; break;
                    case 10: code = "0.00%"; break;
                    case 14: code = "dd-mm-yyyy"; break;
                    case 15: code = "d-mmm-yy"; break;
                    case 16: code = "d-mmm"; break;
                    case 17: code = "mmm-yy"; break;
                    case 18: code = "h:mm AM/PM"; break;
                    case 19: code = "h:mm:ss AM/PM"; break;
                    case 20: code = "h:mm"; break;
                    case 21: code = "h:mm:ss"; break;
                    case 22: code = "dd-mm-yyyy h:mm"; break;
                    case 37: case 38: code = "#,##0"; break;
                    case 39: case 40: code = "#,##0.00"; break;
                    case 49: return Trim(v);
                    default: return Trim(v);
                }
            }
            if (code == "General" || code == "@") return Trim(v);

            string[] sections = SplitSections(code);
            string section = sections[0];
            if (v < 0 && sections.Length > 1) { section = sections[1]; v = -v; }
            else if (v == 0 && sections.Length > 2) section = sections[2];

            // Brackets are colours, conditions and locale tags ([$৳-445]): the sign in them is kept.
            string sign = "";
            section = Regex.Replace(section, @"\[([^\]]*)\]", mm =>
            {
                string inner = mm.Groups[1].Value;
                if (inner.StartsWith("$", StringComparison.Ordinal))
                {
                    int dash = inner.IndexOf('-');
                    sign = dash > 1 ? inner.Substring(1, dash - 1) : inner.Substring(1);
                    return "\u0001";
                }
                return "";
            });

            string bare = Regex.Replace(section, "\"[^\"]*\"|\\\\.", "");
            bool date = Regex.IsMatch(bare, "[dmyhs]", RegexOptions.IgnoreCase) && !bare.Contains("0") && !bare.Contains("#");
            if (date)
            {
                DateTime dt;
                try { dt = DateTime.FromOADate(v); } catch { return Trim(v); }
                bool hasHour = Regex.IsMatch(bare, "h", RegexOptions.IgnoreCase);
                var o = new StringBuilder();
                string s = section.Replace("\u0001", sign);
                for (int i = 0; i < s.Length;)
                {
                    char c = s[i];
                    if (c == '"') { int e = s.IndexOf('"', i + 1); if (e < 0) e = s.Length - 1; o.Append(s, i + 1, e - i - 1); i = e + 1; continue; }
                    if (c == '\\' && i + 1 < s.Length) { o.Append(s[i + 1]); i += 2; continue; }
                    int run = 1;
                    while (i + run < s.Length && char.ToLowerInvariant(s[i + run]) == char.ToLowerInvariant(c)) run++;
                    char l = char.ToLowerInvariant(c);
                    if (l == 'y') o.Append(run >= 3 ? dt.Year.ToString("0000", CultureInfo.InvariantCulture) : (dt.Year % 100).ToString("00", CultureInfo.InvariantCulture));
                    else if (l == 'd') o.Append(run >= 4 ? dt.DayOfWeek.ToString() : run == 3 ? dt.DayOfWeek.ToString().Substring(0, 3) : run == 2 ? dt.Day.ToString("00", CultureInfo.InvariantCulture) : dt.Day.ToString(CultureInfo.InvariantCulture));
                    else if (l == 'm')
                    {
                        // Minutes when it follows an hour or precedes seconds.
                        string before = o.ToString().TrimEnd(), after = s.Substring(Math.Min(s.Length, i + run)).TrimStart(':', ' ');
                        bool minute = hasHour && (before.EndsWith(":", StringComparison.Ordinal) || after.StartsWith("s", StringComparison.OrdinalIgnoreCase));
                        if (minute) o.Append(run >= 2 ? dt.Minute.ToString("00", CultureInfo.InvariantCulture) : dt.Minute.ToString(CultureInfo.InvariantCulture));
                        else o.Append(run >= 4 ? Months[dt.Month - 1] : run == 3 ? Months[dt.Month - 1].Substring(0, 3) : run == 2 ? dt.Month.ToString("00", CultureInfo.InvariantCulture) : dt.Month.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (l == 'h')
                    {
                        int h = dt.Hour;
                        if (Regex.IsMatch(bare, "AM/PM|A/P", RegexOptions.IgnoreCase)) { h = h % 12; if (h == 0) h = 12; }
                        o.Append(run >= 2 ? h.ToString("00", CultureInfo.InvariantCulture) : h.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (l == 's') o.Append(run >= 2 ? dt.Second.ToString("00", CultureInfo.InvariantCulture) : dt.Second.ToString(CultureInfo.InvariantCulture));
                    else if (l == 'a' && string.Compare(s, i, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0) { o.Append(dt.Hour < 12 ? "AM" : "PM"); i += 5; continue; }
                    else o.Append(s, i, run);
                    i += run;
                }
                return o.ToString();
            }

            bool percent = bare.Contains("%");
            if (percent) v *= 100;
            int dot = bare.IndexOf('.');
            int decimals = 0;
            if (dot >= 0) for (int i = dot + 1; i < bare.Length && (bare[i] == '0' || bare[i] == '#' || bare[i] == '?'); i++) decimals++;
            bool thousands = Regex.IsMatch(bare, "[0#],[0#]");
            string body = v.ToString((thousands ? "N" : "F") + decimals, CultureInfo.InvariantCulture);
            // Leading zeros the format asks for: 00012 for 00000.
            string wholePart = dot >= 0 ? bare.Substring(0, dot) : bare;
            int zeros = 0;
            foreach (char zc in wholePart) if (zc == '0') zeros++;
            if (!thousands && zeros > 1)
            {
                bool negative = body.StartsWith("-", StringComparison.Ordinal);
                string digits = negative ? body.Substring(1) : body;
                int point = digits.IndexOf('.');
                string whole = point >= 0 ? digits.Substring(0, point) : digits, rest = point >= 0 ? digits.Substring(point) : "";
                body = (negative ? "-" : "") + whole.PadLeft(zeros, '0') + rest;
            }

            // What is written round the digits: quoted text, currency signs.
            int first = -1, last = -1;
            for (int i = 0; i < section.Length; i++) if ("0#?".IndexOf(section[i]) >= 0) { if (first < 0) first = i; last = i; }
            string prefix = first < 0 ? "" : section.Substring(0, first), suffix = first < 0 ? "" : section.Substring(last + 1);
            Func<string, string> literal = t =>
            {
                t = t.Replace("\u0001", sign).Replace("_", "").Replace("*", "").Replace("\\", "");
                t = Regex.Replace(t, "\"([^\"]*)\"", "$1");
                t = t.Replace("%", "");
                if (decimals > 0) t = t.Replace(".", "");
                return t.Trim().Length == 0 ? "" : t;
            };
            return literal(prefix) + body + (percent ? "%" : "") + literal(suffix.Replace("%", ""));
        }

        static string[] SplitSections(string code)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            foreach (char c in code)
            {
                if (c == '"') quoted = !quoted;
                if (c == ';' && !quoted) { parts.Add(cur.ToString()); cur.Length = 0; continue; }
                cur.Append(c);
            }
            parts.Add(cur.ToString());
            return parts.ToArray();
        }

        static string Trim(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e15) return Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
            return v.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        static int ColumnOf(string reference)
        {
            int n = 0, i = 0;
            while (reference != null && i < reference.Length && char.IsLetter(reference[i])) { n = n * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1); i++; }
            return n - 1;
        }

        static int RowOf(string reference)
        {
            var m = Regex.Match(reference ?? "", @"(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1 : -1;
        }

        static string ColumnName(int index)
        {
            var s = new StringBuilder();
            for (int n = index + 1; n > 0; n = (n - 1) / 26) s.Insert(0, (char)('A' + (n - 1) % 26));
            return s.ToString();
        }

        /// <summary>
        /// The sheets of a workbook (.xlsx), each drawn as a picture of its cells: at most
        /// <paramref name="maxPictures"/> in all, <paramref name="rowsPer"/> rows to a picture.
        /// </summary>
        public static List<PageShot> Sheets(string xlsx, int maxPictures, int rowsPer, out int sheetCount, out string notes)
        {
            sheetCount = 0;
            notes = "";
            var shots = new List<PageShot>();
            using (var fs = new FileStream(xlsx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                XlStyles styles = XlStylesOf(zip);

                var shared = new List<string>();
                XmlDocument ssd = Load(zip, "xl/sharedStrings.xml");
                if (ssd != null)
                {
                    var m = new XmlNamespaceManager(ssd.NameTable); m.AddNamespace("s", S);
                    foreach (XmlNode si in ssd.SelectNodes("//s:si", m))
                    {
                        var t = new StringBuilder();
                        foreach (XmlNode n in si.SelectNodes(".//s:t[not(ancestor::s:rPh)]", m)) t.Append(n.InnerText);
                        shared.Add(t.ToString());
                    }
                }

                var sheets = new List<KeyValuePair<string, string>>();
                XmlDocument wb = Load(zip, "xl/workbook.xml"), rels = Load(zip, "xl/_rels/workbook.xml.rels");
                if (wb != null && rels != null)
                {
                    var targets = new Dictionary<string, string>();
                    foreach (XmlNode rel in rels.DocumentElement.ChildNodes) targets[Attr(rel, "Id") ?? ""] = Attr(rel, "Target") ?? "";
                    foreach (XmlNode sh in wb.GetElementsByTagName("sheet"))
                    {
                        string target;
                        if (targets.TryGetValue(Attr(sh, "id") ?? "", out target))
                        {
                            target = target.TrimStart('/');
                            sheets.Add(new KeyValuePair<string, string>(Attr(sh, "name") ?? "Sheet", target.StartsWith("xl/", StringComparison.Ordinal) ? target : "xl/" + target));
                        }
                    }
                }
                sheetCount = sheets.Count;
                int unseen = 0;
                foreach (var sheet in sheets)
                {
                    if (shots.Count >= maxPictures) break;
                    XmlDocument d = Load(zip, sheet.Value);
                    if (d == null) continue;
                    string drawingNote;
                    DrawSheet(zip, d, styles, shared, sheet.Key, rowsPer, maxPictures - shots.Count, shots, out drawingNote);
                    if (drawingNote.Length > 0) notes += (notes.Length > 0 ? " " : "") + drawingNote;
                }
                if (sheets.Count > 0) unseen = Math.Max(0, sheets.Count - CountSheets(shots));
                if (unseen > 0) notes += (notes.Length > 0 ? " " : "") + unseen + " more sheet" + (unseen == 1 ? " is" : "s are") + " not drawn.";
            }
            return shots;
        }

        static int CountSheets(List<PageShot> shots)
        {
            var seen = new HashSet<string>();
            foreach (PageShot s in shots) { int cut = s.Caption.IndexOf(" — ", StringComparison.Ordinal); seen.Add(cut > 0 ? s.Caption.Substring(0, cut) : s.Caption); }
            return seen.Count;
        }

        static void DrawSheet(ZipArchive zip, XmlDocument d, XlStyles st, List<string> shared, string name, int rowsPer, int budget, List<PageShot> shots, out string note)
        {
            note = "";
            var m = new XmlNamespaceManager(d.NameTable); m.AddNamespace("s", S);

            // Widths and heights, in pixels at 96 dpi.
            double defaultW = 64, defaultH = 20;
            XmlNode fmt = d.SelectSingleNode("//s:sheetFormatPr", m);
            if (fmt != null)
            {
                if (Attr(fmt, "defaultColWidth") != null) defaultW = Num(Attr(fmt, "defaultColWidth"), 9.14) * 7;
                else if (Attr(fmt, "baseColWidth") != null) defaultW = (Num(Attr(fmt, "baseColWidth"), 8) * 7) + 5;
                defaultH = Num(Attr(fmt, "defaultRowHeight"), 15) * 96 / 72;
            }
            var colW = new Dictionary<int, double>();
            foreach (XmlNode c in d.SelectNodes("//s:cols/s:col", m))
            {
                if (Attr(c, "hidden") == "1") { for (int i = (int)Num(Attr(c, "min"), 1); i <= Math.Min(Num(Attr(c, "max"), 1), 200); i++) colW[i - 1] = 0; continue; }
                double w = Num(Attr(c, "width"), 0) * 7;
                for (int i = (int)Num(Attr(c, "min"), 1); i <= Math.Min(Num(Attr(c, "max"), 1), 200); i++) colW[i - 1] = w;
            }

            var cells = new Dictionary<long, XlCell>();
            var rowH = new Dictionary<int, double>();
            int maxRow = -1, maxCol = -1;
            foreach (XmlNode row in d.SelectNodes("//s:sheetData/s:row", m))
            {
                int r = (int)Num(Attr(row, "r"), 0) - 1;
                if (r < 0) continue;
                if (Attr(row, "hidden") == "1") rowH[r] = 0;
                else if (Attr(row, "ht") != null) rowH[r] = Num(Attr(row, "ht"), 15) * 96 / 72;
                int auto = 0;
                foreach (XmlNode c in row.SelectNodes("s:c", m))
                {
                    int col = Attr(c, "r") == null ? auto : ColumnOf(Attr(c, "r"));
                    auto = col + 1;
                    var cell = new XlCell { Style = (int)Num(Attr(c, "s"), 0) };
                    string type = Attr(c, "t");
                    XmlNode v = c.SelectSingleNode("s:v", m);
                    string raw = v == null ? "" : v.InnerText;
                    if (type == "s") { int idx; cell.Text = int.TryParse(raw, out idx) && idx >= 0 && idx < shared.Count ? shared[idx] : ""; }
                    else if (type == "inlineStr") { XmlNode isn = c.SelectSingleNode("s:is", m); cell.Text = isn == null ? "" : isn.InnerText; }
                    else if (type == "str" || type == "e") cell.Text = raw;
                    else if (type == "b") { cell.Text = raw == "1" ? "TRUE" : "FALSE"; cell.IsBool = true; }
                    else if (raw.Length > 0)
                    {
                        double num;
                        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out num))
                        {
                            cell.IsNumber = true; cell.Number = num;
                            XlXf xf = cell.Style < st.Xfs.Count ? st.Xfs[cell.Style] : st.Xfs[0];
                            string code;
                            st.Formats.TryGetValue(xf.NumFmt, out code);
                            cell.Text = ShowNumber(num, code, xf.NumFmt);
                        }
                        else cell.Text = raw;
                    }
                    bool styled = cell.Style > 0;
                    if (cell.Text.Length == 0 && !styled) continue;
                    cells[(long)r * 100000 + col] = cell;
                    if (cell.Text.Length > 0 || styled) { if (r > maxRow) maxRow = r; if (col > maxCol) maxCol = col; }
                }
            }
            if (maxRow < 0) return;

            var merges = new List<Rectangle>();      // X = first col, Y = first row, Width/Height = spans
            var covered = new HashSet<long>();
            foreach (XmlNode mc in d.SelectNodes("//s:mergeCells/s:mergeCell", m))
            {
                string[] ends = (Attr(mc, "ref") ?? "").Split(':');
                if (ends.Length != 2) continue;
                int c1 = ColumnOf(ends[0]), r1 = RowOf(ends[0]), c2 = ColumnOf(ends[1]), r2 = RowOf(ends[1]);
                if (c1 < 0 || r1 < 0 || c2 < c1 || r2 < r1) continue;
                merges.Add(new Rectangle(c1, r1, c2 - c1 + 1, r2 - r1 + 1));
                for (int r = r1; r <= r2; r++) for (int c = c1; c <= c2; c++) if (r != r1 || c != c1) covered.Add((long)r * 100000 + c);
                if (r2 > maxRow) maxRow = r2; if (c2 > maxCol) maxCol = c2;
            }

            bool grid = d.SelectSingleNode("//s:sheetView[@showGridLines='0' or @showGridLines='false']", m) == null;
            int cols = Math.Min(maxCol + 1, 40);
            if (maxCol + 1 > cols) note += "Only the first 40 columns of " + name + " are drawn. ";
            Func<int, double> widthOf = c => { double w; return colW.TryGetValue(c, out w) ? w : defaultW; };
            Func<int, double> heightOf = r => { double h; return rowH.TryGetValue(r, out h) ? h : defaultH; };

            if (zip.GetEntry("xl/drawings/drawing1.xml") != null || zip.GetEntry("xl/charts/chart1.xml") != null)
                note += "The workbook has charts or pictures, which are not drawn here. ";

            int chunks = 0;
            for (int firstRow = 0; firstRow <= maxRow && chunks < budget; firstRow += rowsPer)
            {
                int lastRow = Math.Min(maxRow, firstRow + rowsPer - 1);
                PageShot shot = DrawRows(st, cells, merges, covered, name, firstRow, lastRow, cols, widthOf, heightOf, grid, maxRow);
                shots.Add(shot);
                chunks++;
            }
            if (maxRow >= rowsPer * chunks) note += "Rows " + (rowsPer * chunks + 1) + " to " + (maxRow + 1) + " of " + name + " are not drawn. ";
            note = note.Trim();
        }

        static PageShot DrawRows(XlStyles st, Dictionary<long, XlCell> cells, List<Rectangle> merges, HashSet<long> covered, string name,
                                 int firstRow, int lastRow, int cols, Func<int, double> widthOf, Func<int, double> heightOf, bool grid, int maxRow)
        {
            const int gutterW = 38, headerH = 22;
            var x = new double[cols + 1];
            for (int c = 0; c < cols; c++) x[c + 1] = x[c] + widthOf(c);
            var y = new double[lastRow - firstRow + 2];
            for (int r = firstRow; r <= lastRow; r++) y[r - firstRow + 1] = y[r - firstRow] + heightOf(r);
            int width = (int)Math.Ceiling(x[cols]) + gutterW + 1, height = (int)Math.Ceiling(y[y.Length - 1]) + headerH + 1;
            width = Math.Max(120, Math.Min(width, 6000)); height = Math.Max(60, Math.Min(height, 6000));

            var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.None;
                var caches = new Dictionary<string, Font>();
                Func<XlFont, Font> fontOf = f =>
                {
                    string key = f.Name + "|" + f.Size + "|" + f.Bold + f.Italic + f.Underline + f.Strike;
                    Font font;
                    if (caches.TryGetValue(key, out font)) return font;
                    FontStyle style = (f.Bold ? FontStyle.Bold : 0) | (f.Italic ? FontStyle.Italic : 0) | (f.Underline ? FontStyle.Underline : 0) | (f.Strike ? FontStyle.Strikeout : 0);
                    try { font = new Font(f.Name, (float)f.Size, style, GraphicsUnit.Point); }
                    catch { font = new Font("Segoe UI", (float)f.Size, style, GraphicsUnit.Point); }
                    caches[key] = font;
                    return font;
                };

                float ox = gutterW, oy = headerH;

                // Column letters and row numbers, as a spreadsheet shows them.
                using (var head = new SolidBrush(Color.FromArgb(240, 240, 240)))
                using (var line = new Pen(Color.FromArgb(200, 200, 200)))
                using (var hf = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
                using (var hb = new SolidBrush(Color.FromArgb(90, 90, 90)))
                using (var centre = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.FillRectangle(head, 0, 0, width, headerH);
                    g.FillRectangle(head, 0, 0, gutterW, height);
                    for (int c = 0; c < cols; c++)
                    {
                        var rc = new RectangleF(ox + (float)x[c], 0, (float)(x[c + 1] - x[c]), headerH);
                        g.DrawLine(line, rc.Right, 0, rc.Right, headerH);
                        if (rc.Width > 6) g.DrawString(ColumnName(c), hf, hb, rc, centre);
                    }
                    for (int r = firstRow; r <= lastRow; r++)
                    {
                        var rc = new RectangleF(0, oy + (float)y[r - firstRow], gutterW, (float)(y[r - firstRow + 1] - y[r - firstRow]));
                        g.DrawLine(line, 0, rc.Bottom, gutterW, rc.Bottom);
                        if (rc.Height > 6) g.DrawString((r + 1).ToString(CultureInfo.InvariantCulture), hf, hb, rc, centre);
                    }
                    g.DrawLine(line, 0, headerH, width, headerH);
                    g.DrawLine(line, gutterW, 0, gutterW, height);
                }

                // The rectangle of a cell, or of the merged block it heads.
                Func<int, int, Rectangle, RectangleF> rect = (r, c, span) =>
                {
                    int c2 = Math.Min(cols - 1, c + Math.Max(1, span.Width) - 1), r2 = Math.Min(lastRow, r + Math.Max(1, span.Height) - 1);
                    float left = ox + (float)x[c], top = oy + (float)y[r - firstRow];
                    return new RectangleF(left, top, ox + (float)x[c2 + 1] - left, oy + (float)y[r2 - firstRow + 1] - top);
                };
                var mergeAt = new Dictionary<long, Rectangle>();
                foreach (Rectangle mg in merges) mergeAt[(long)mg.Y * 100000 + mg.X] = mg;

                g.SetClip(new RectangleF(ox, oy, width - ox, height - oy));

                if (grid)
                    using (var gp = new Pen(Color.FromArgb(225, 225, 225)))
                    {
                        for (int c = 0; c <= cols; c++) g.DrawLine(gp, ox + (float)x[c], oy, ox + (float)x[c], height);
                        for (int r = 0; r < y.Length; r++) g.DrawLine(gp, ox, oy + (float)y[r], width, oy + (float)y[r]);
                    }

                // Fills, then text, then borders: a border is drawn over a neighbour's fill.
                for (int r = firstRow; r <= lastRow; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        long key = (long)r * 100000 + c;
                        XlCell cell;
                        if (!cells.TryGetValue(key, out cell) || covered.Contains(key)) continue;
                        XlXf xf = cell.Style < st.Xfs.Count ? st.Xfs[cell.Style] : st.Xfs[0];
                        Color? fill = xf.Fill >= 0 && xf.Fill < st.Fills.Count ? st.Fills[xf.Fill] : null;
                        if (!fill.HasValue) continue;
                        Rectangle span; mergeAt.TryGetValue(key, out span);
                        RectangleF rc = rect(r, c, span);
                        using (var br = new SolidBrush(fill.Value)) g.FillRectangle(br, rc.X, rc.Y, rc.Width, rc.Height);
                    }

                for (int r = firstRow; r <= lastRow; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        long key = (long)r * 100000 + c;
                        XlCell cell;
                        if (!cells.TryGetValue(key, out cell) || cell.Text.Length == 0 || covered.Contains(key)) continue;
                        XlXf xf = cell.Style < st.Xfs.Count ? st.Xfs[cell.Style] : st.Xfs[0];
                        XlFont font = xf.Font >= 0 && xf.Font < st.Fonts.Count ? st.Fonts[xf.Font] : st.Fonts[0];
                        Rectangle span; mergeAt.TryGetValue(key, out span);
                        RectangleF rc = rect(r, c, span);

                        string h = xf.H;
                        if (h == "" || h == "general") h = cell.IsNumber ? "right" : cell.IsBool ? "center" : "left";
                        // Text runs on into the empty cells beside it, as it does on screen.
                        RectangleF room = rc;
                        if (!xf.Wrap && span.Width <= 1 && !cell.IsNumber && (h == "left" || h == "general"))
                        {
                            int c2 = c + 1;
                            while (c2 < cols && !cells.ContainsKey((long)r * 100000 + c2) && (c2 - c) < 12) c2++;
                            room = new RectangleF(rc.X, rc.Y, ox + (float)x[c2] - rc.X, rc.Height);
                        }
                        else if (!xf.Wrap && span.Width <= 1 && cell.IsNumber) { }

                        float indent = xf.Indent * 9;
                        var area = new RectangleF(room.X + 2 + (h == "left" ? indent : 0), room.Y, Math.Max(1, room.Width - 4 - (h == "left" ? indent : 0)), room.Height);
                        using (var fmt = new StringFormat(StringFormatFlags.NoClip))
                        using (var brush = new SolidBrush(font.Color))
                        {
                            fmt.Alignment = h == "center" || h == "centerContinuous" ? StringAlignment.Center : h == "right" ? StringAlignment.Far : StringAlignment.Near;
                            fmt.LineAlignment = xf.V == "center" ? StringAlignment.Center : xf.V == "top" ? StringAlignment.Near : StringAlignment.Far;
                            if (!xf.Wrap) fmt.FormatFlags |= StringFormatFlags.NoWrap;
                            fmt.Trimming = StringTrimming.EllipsisCharacter;
                            g.SetClip(new RectangleF(Math.Max(ox, room.X), Math.Max(oy, room.Y), room.Width, room.Height));
                            g.DrawString(cell.Text, fontOf(font), brush, area, fmt);
                            g.SetClip(new RectangleF(ox, oy, width - ox, height - oy));
                        }
                    }

                for (int r = firstRow; r <= lastRow; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        long key = (long)r * 100000 + c;
                        XlCell cell;
                        if (!cells.TryGetValue(key, out cell) || covered.Contains(key)) continue;
                        XlXf xf = cell.Style < st.Xfs.Count ? st.Xfs[cell.Style] : st.Xfs[0];
                        if (xf.Border <= 0 || xf.Border >= st.Borders.Count) continue;
                        Rectangle span; mergeAt.TryGetValue(key, out span);
                        RectangleF rc = rect(r, c, span);
                        XlBorder b = st.Borders[xf.Border];
                        // A merged block takes its right and bottom edges from its far corner.
                        if (span.Width > 1 || span.Height > 1)
                        {
                            XlCell far;
                            if (cells.TryGetValue((long)(span.Y + span.Height - 1) * 100000 + (span.X + span.Width - 1), out far))
                            {
                                XlXf fx = far.Style < st.Xfs.Count ? st.Xfs[far.Style] : st.Xfs[0];
                                if (fx.Border > 0 && fx.Border < st.Borders.Count) { XlBorder fb = st.Borders[fx.Border]; b = new XlBorder { Left = b.Left, LeftC = b.LeftC, Top = b.Top, TopC = b.TopC, Right = fb.Right, RightC = fb.RightC, Bottom = fb.Bottom, BottomC = fb.BottomC }; }
                            }
                        }
                        Edge(g, b.Top, b.TopC, rc.Left, rc.Top, rc.Right, rc.Top);
                        Edge(g, b.Bottom, b.BottomC, rc.Left, rc.Bottom, rc.Right, rc.Bottom);
                        Edge(g, b.Left, b.LeftC, rc.Left, rc.Top, rc.Left, rc.Bottom);
                        Edge(g, b.Right, b.RightC, rc.Right, rc.Top, rc.Right, rc.Bottom);
                    }
                foreach (Font f in caches.Values) f.Dispose();
            }
            return new PageShot
            {
                Image = bmp,
                Caption = name + " — rows " + (firstRow + 1) + "–" + (lastRow + 1) + " of " + (maxRow + 1) + ", columns A–" + ColumnName(cols - 1) + " (cell colours, borders, widths and merged cells as in the workbook; 1 px ≈ 0.26 mm)",
                PxPerMm = 0,
            };
        }

        static void Edge(Graphics g, string style, Color color, float x1, float y1, float x2, float y2)
        {
            if (string.IsNullOrEmpty(style) || style == "none") return;
            float w = style == "thick" ? 3f : style == "medium" || style == "mediumDashed" || style == "mediumDashDot" ? 2f : style == "double" ? 3f : 1f;
            using (var pen = new Pen(color, w))
            {
                if (style.Contains("dash") || style.Contains("Dash")) pen.DashStyle = DashStyle.Dash;
                else if (style == "dotted" || style == "hair") pen.DashStyle = DashStyle.Dot;
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        // =====================================================================
        // PowerPoint
        // =====================================================================

        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        class SlideContext
        {
            public ZipArchive Zip;
            public Color[] Theme;
            public Dictionary<string, string> ColorMap = new Dictionary<string, string> { { "bg1", "lt1" }, { "tx1", "dk1" }, { "bg2", "lt2" }, { "tx2", "dk2" } };
            public Dictionary<string, Image> Pictures = new Dictionary<string, Image>();
            public double EmuPerPx;
            public Graphics G;
            public int Charts, Skipped;
        }

        /// <summary>Slides of a .pptx, each drawn <paramref name="width"/> pixels wide.</summary>
        public static List<PageShot> Slides(string pptx, int width, int max, out int slideCount, out string notes)
        {
            notes = "";
            var shots = new List<PageShot>();
            using (var fs = new FileStream(pptx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                double cx = 9144000, cy = 5143500;
                XmlDocument pres = Load(zip, "ppt/presentation.xml");
                if (pres != null)
                {
                    var sz = pres.GetElementsByTagName("sldSz");
                    if (sz.Count > 0) { cx = Num(Attr(sz[0], "cx"), cx); cy = Num(Attr(sz[0], "cy"), cy); }
                }
                var slides = new List<KeyValuePair<int, string>>();
                foreach (ZipArchiveEntry e in zip.Entries)
                {
                    Match mt = Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$");
                    if (mt.Success) slides.Add(new KeyValuePair<int, string>(int.Parse(mt.Groups[1].Value, CultureInfo.InvariantCulture), e.FullName));
                }
                slides.Sort((p, q) => p.Key.CompareTo(q.Key));
                slideCount = slides.Count;

                var ctx = new SlideContext { Zip = zip, Theme = Theme(zip, "ppt/theme/theme1.xml") };
                int charts = 0;
                foreach (var slide in slides)
                {
                    if (shots.Count >= max) break;
                    int height = (int)Math.Round(width * cy / cx);
                    var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.Clear(Color.White);
                        ctx.G = g; ctx.EmuPerPx = cx / width;
                        DrawSlide(ctx, slide.Value, width, height);
                        charts += ctx.Charts; ctx.Charts = 0;
                    }
                    shots.Add(new PageShot { Image = bmp, Caption = "slide " + slide.Key + " of " + slides.Count + " (" + Math.Round(cx / 36000) + " × " + Math.Round(cy / 36000) + " mm)", PxPerMm = width / (cx / 36000) });
                }
                foreach (Image i in ctx.Pictures.Values) if (i != null) i.Dispose();
                if (charts > 0) notes = "Charts on the slides are not drawn here (shown as a grey box). ";
                if (ctx.Skipped > 0) notes += "Some effects and SmartArt are left out.";
            }
            notes = notes.Trim();
            return shots;
        }

        static Dictionary<string, string> Rels(ZipArchive zip, string part)
        {
            var map = new Dictionary<string, string>();
            int slash = part.LastIndexOf('/');
            string relsPath = part.Substring(0, slash + 1) + "_rels/" + part.Substring(slash + 1) + ".rels";
            XmlDocument d = Load(zip, relsPath);
            if (d == null) return map;
            foreach (XmlNode rel in d.DocumentElement.ChildNodes)
            {
                string id = Attr(rel, "Id"), target = Attr(rel, "Target");
                if (id == null || target == null) continue;
                map[id] = Resolve(part, target);
            }
            return map;
        }

        static string Resolve(string from, string target)
        {
            if (target.StartsWith("/", StringComparison.Ordinal)) return target.TrimStart('/');
            var parts = new List<string>(from.Substring(0, from.LastIndexOf('/') + 1).TrimEnd('/').Split('/'));
            foreach (string seg in target.Split('/'))
            {
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg != "." && seg.Length > 0) parts.Add(seg);
            }
            return string.Join("/", parts.ToArray());
        }

        static string RelOfType(ZipArchive zip, string part, string type)
        {
            XmlDocument d = null;
            int slash = part.LastIndexOf('/');
            d = Load(zip, part.Substring(0, slash + 1) + "_rels/" + part.Substring(slash + 1) + ".rels");
            if (d == null) return null;
            foreach (XmlNode rel in d.DocumentElement.ChildNodes)
                if ((Attr(rel, "Type") ?? "").EndsWith("/" + type, StringComparison.Ordinal)) return Resolve(part, Attr(rel, "Target") ?? "");
            return null;
        }

        static void DrawSlide(SlideContext ctx, string slidePart, int width, int height)
        {
            string layoutPart = RelOfType(ctx.Zip, slidePart, "slideLayout");
            string masterPart = layoutPart == null ? null : RelOfType(ctx.Zip, layoutPart, "slideMaster");
            XmlDocument slide = Load(ctx.Zip, slidePart);
            XmlDocument layout = layoutPart == null ? null : Load(ctx.Zip, layoutPart);
            XmlDocument master = masterPart == null ? null : Load(ctx.Zip, masterPart);

            if (master != null)
            {
                var cm = master.GetElementsByTagName("clrMap");
                if (cm.Count > 0) foreach (XmlAttribute a in cm[0].Attributes) ctx.ColorMap[a.LocalName] = a.Value;
            }

            // Background: the slide's, or the layout's, or the master's.
            foreach (XmlDocument doc in new[] { slide, layout, master })
            {
                if (doc == null) continue;
                var bgs = doc.GetElementsByTagName("bg");
                if (bgs.Count == 0) continue;
                XmlNode fill = FirstChild(bgs[0], "solidFill");
                if (fill == null) { XmlNode bp = FirstChild(bgs[0], "bgPr"); if (bp != null) fill = FirstChild(bp, "solidFill"); }
                if (fill != null) { Color c = ColorOf(ctx, fill); ctx.G.Clear(c); break; }
            }

            // The master's and the layout's own drawing (not its placeholders) sits under the slide's.
            if (master != null) DrawTree(ctx, master, masterPart, null, null, width, height, false);
            if (layout != null) DrawTree(ctx, layout, layoutPart, layout, master, width, height, false);
            DrawTree(ctx, slide, slidePart, layout, master, width, height, true);
        }

        static XmlNode FirstChild(XmlNode n, string local)
        {
            if (n == null) return null;
            foreach (XmlNode c in n.ChildNodes) if (c.LocalName == local) return c;
            return null;
        }

        static XmlNode Find(XmlNode n, params string[] path)
        {
            foreach (string p in path) { n = FirstChild(n, p); if (n == null) return null; }
            return n;
        }

        /// <summary>The colour a fill or colour node names: srgbClr, schemeClr, sysClr, prstClr, with the usual adjustments.</summary>
        static Color ColorOf(SlideContext ctx, XmlNode holder)
        {
            XmlNode c = holder;
            if (holder != null && (holder.LocalName == "solidFill" || holder.LocalName == "fgClr" || holder.LocalName == "bgClr" || holder.LocalName == "gsLst")) c = holder.FirstChild;
            if (c == null) return Color.Black;
            Color col;
            switch (c.LocalName)
            {
                case "srgbClr": col = Parse(Attr(c, "val")); break;
                case "sysClr": col = Parse(Attr(c, "lastClr") ?? "000000"); break;
                case "prstClr": { Color k = Color.FromName(Attr(c, "val") ?? "black"); col = k.IsKnownColor ? k : Color.Black; break; }
                case "schemeClr":
                    {
                        string name = Attr(c, "val") ?? "tx1", mapped;
                        if (ctx.ColorMap.TryGetValue(name, out mapped)) name = mapped;
                        string[] order = { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
                        int i = Array.IndexOf(order, name);
                        col = i >= 0 && i < ctx.Theme.Length ? ctx.Theme[i] : Color.Black;
                        break;
                    }
                default: col = Color.Black; break;
            }
            double h, s, l;
            foreach (XmlNode mod in c.ChildNodes)
            {
                double v = Num(Attr(mod, "val"), 100000) / 100000.0;
                ToHsl(col, out h, out s, out l);
                switch (mod.LocalName)
                {
                    case "lumMod": col = FromHsl(h, s, l * v); break;
                    case "lumOff": col = FromHsl(h, s, l + v); break;
                    case "tint": col = Color.FromArgb(0xFF, (int)(col.R + (255 - col.R) * (1 - v)), (int)(col.G + (255 - col.G) * (1 - v)), (int)(col.B + (255 - col.B) * (1 - v))); break;
                    case "shade": col = Color.FromArgb(0xFF, (int)(col.R * v), (int)(col.G * v), (int)(col.B * v)); break;
                    case "satMod": col = FromHsl(h, Math.Min(1, s * v), l); break;
                }
            }
            return col;
        }

        struct Frame { public double X, Y, W, H; public bool Set; }

        static Frame FrameOf(XmlNode spPr)
        {
            var f = new Frame();
            XmlNode x = spPr == null ? null : FirstChild(spPr, "xfrm");
            if (x == null) return f;
            XmlNode off = FirstChild(x, "off"), ext = FirstChild(x, "ext");
            if (off == null || ext == null) return f;
            f.X = Num(Attr(off, "x"), 0); f.Y = Num(Attr(off, "y"), 0); f.W = Num(Attr(ext, "cx"), 0); f.H = Num(Attr(ext, "cy"), 0); f.Set = true;
            return f;
        }

        /// <summary>The frame a placeholder with no frame of its own takes from the layout, or the master.</summary>
        static Frame Inherited(XmlNode sp, XmlDocument layout, XmlDocument master)
        {
            XmlNode ph = Find(sp, "nvSpPr", "nvPr", "ph");
            if (ph == null) return new Frame();
            string type = Attr(ph, "type") ?? "body", idx = Attr(ph, "idx");
            foreach (XmlDocument doc in new[] { layout, master })
            {
                if (doc == null) continue;
                foreach (XmlNode other in doc.GetElementsByTagName("sp"))
                {
                    XmlNode oph = Find(other, "nvSpPr", "nvPr", "ph");
                    if (oph == null) continue;
                    string otype = Attr(oph, "type") ?? "body", oidx = Attr(oph, "idx");
                    bool same = (idx != null && idx == oidx) || (idx == null && type == otype) || (type == "ctrTitle" && otype == "title") || (type == "title" && otype == "ctrTitle");
                    if (!same) continue;
                    Frame f = FrameOf(FirstChild(other, "spPr"));
                    if (f.Set) return f;
                }
            }
            return new Frame();
        }

        static void DrawTree(SlideContext ctx, XmlDocument doc, string part, XmlDocument layout, XmlDocument master, int width, int height, bool placeholders)
        {
            XmlNode tree = Find(doc.DocumentElement, "cSld", "spTree");
            if (tree == null) return;
            var rels = Rels(ctx.Zip, part);
            foreach (XmlNode n in tree.ChildNodes)
                DrawShape(ctx, n, rels, layout, master, placeholders, new double[] { 0, 0, 1, 1 }, part);
        }

        // Group transform: the child coordinate space {offset x, y; scale x, y} that maps a group's children onto the slide.
        static void DrawShape(SlideContext ctx, XmlNode n, Dictionary<string, string> rels, XmlDocument layout, XmlDocument master, bool placeholders, double[] t, string part)
        {
            Graphics g = ctx.G;
            string kind = n.LocalName;
            if (kind == "grpSp")
            {
                XmlNode gp = FirstChild(n, "grpSpPr");
                XmlNode xf = FirstChild(gp, "xfrm");
                double[] inner = t;
                if (xf != null)
                {
                    XmlNode off = FirstChild(xf, "off"), ext = FirstChild(xf, "ext"), co = FirstChild(xf, "chOff"), ce = FirstChild(xf, "chExt");
                    if (off != null && ext != null && co != null && ce != null && Num(Attr(ce, "cx"), 0) > 0 && Num(Attr(ce, "cy"), 0) > 0)
                    {
                        double sx = Num(Attr(ext, "cx"), 1) / Num(Attr(ce, "cx"), 1), sy = Num(Attr(ext, "cy"), 1) / Num(Attr(ce, "cy"), 1);
                        double ox = Num(Attr(off, "x"), 0) - Num(Attr(co, "x"), 0) * sx, oy = Num(Attr(off, "y"), 0) - Num(Attr(co, "y"), 0) * sy;
                        inner = new[] { t[0] + ox * t[2], t[1] + oy * t[3], t[2] * sx, t[3] * sy };
                    }
                }
                foreach (XmlNode child in n.ChildNodes) DrawShape(ctx, child, rels, layout, master, placeholders, inner, part);
                return;
            }
            if (kind != "sp" && kind != "pic" && kind != "cxnSp" && kind != "graphicFrame") return;

            XmlNode spPr = kind == "graphicFrame" ? null : FirstChild(n, "spPr");
            Frame f = kind == "graphicFrame" ? GraphicFrame(n) : FrameOf(spPr);
            XmlNode ph = kind == "sp" ? Find(n, "nvSpPr", "nvPr", "ph") : null;
            if (ph != null && !placeholders) return;
            if (!f.Set && kind == "sp") f = Inherited(n, layout, master);
            if (!f.Set) return;

            double px = ctx.EmuPerPx;
            var r = new RectangleF((float)((t[0] + f.X * t[2]) / px), (float)((t[1] + f.Y * t[3]) / px), (float)(f.W * t[2] / px), (float)(f.H * t[3] / px));

            XmlNode xfrm = spPr == null ? null : FirstChild(spPr, "xfrm");
            float rot = xfrm == null ? 0 : (float)(Num(Attr(xfrm, "rot"), 0) / 60000.0);
            bool flipH = xfrm != null && Attr(xfrm, "flipH") == "1", flipV = xfrm != null && Attr(xfrm, "flipV") == "1";

            GraphicsState state = g.Save();
            try
            {
                if (Math.Abs(rot) > 0.01 || flipH || flipV)
                {
                    g.TranslateTransform(r.X + r.Width / 2, r.Y + r.Height / 2);
                    if (Math.Abs(rot) > 0.01) g.RotateTransform(rot);
                    if (flipH || flipV) g.ScaleTransform(flipH ? -1 : 1, flipV ? -1 : 1);
                    g.TranslateTransform(-(r.X + r.Width / 2), -(r.Y + r.Height / 2));
                }

                if (kind == "graphicFrame") { DrawFrame(ctx, n, r, layout, master); return; }
                if (kind == "pic") { DrawPicture(ctx, n, rels, r, spPr); return; }

                // Fill and outline.
                string geom = "rect";
                XmlNode pg = spPr == null ? null : FirstChild(spPr, "prstGeom");
                if (pg != null) geom = Attr(pg, "prst") ?? "rect";
                bool line = kind == "cxnSp" || geom == "line" || geom == "straightConnector1";

                using (GraphicsPath path = Outline(geom, r))
                {
                    XmlNode fill = spPr == null ? null : FirstChild(spPr, "solidFill");
                    XmlNode grad = spPr == null ? null : FirstChild(spPr, "gradFill");
                    if (!line)
                    {
                        if (fill != null) using (var br = new SolidBrush(ColorOf(ctx, fill))) g.FillPath(br, path);
                        else if (grad != null)
                        {
                            XmlNode first = Find(grad, "gsLst");
                            if (first != null && first.FirstChild != null) using (var br = new SolidBrush(ColorOf(ctx, first.FirstChild))) g.FillPath(br, path);
                        }
                        else if (spPr == null || FirstChild(spPr, "noFill") == null)
                        {
                            // A shape with no fill of its own takes the theme's, through its style: accent1.
                            XmlNode style = FirstChild(n, "style");
                            XmlNode fr = style == null ? null : FirstChild(style, "fillRef");
                            if (fr != null && FirstChild(fr, "schemeClr") != null && Attr(fr, "idx") != "0") using (var br = new SolidBrush(ColorOf(ctx, fr))) g.FillPath(br, path);
                        }
                    }
                    XmlNode ln = spPr == null ? null : FirstChild(spPr, "ln");
                    Color? lineColor = null; float lw = 1f;
                    if (ln != null)
                    {
                        XmlNode lf = FirstChild(ln, "solidFill");
                        if (lf != null) lineColor = ColorOf(ctx, lf);
                        lw = (float)Math.Max(1, Num(Attr(ln, "w"), 12700) / px);
                    }
                    else if (kind != "pic")
                    {
                        XmlNode style = FirstChild(n, "style");
                        XmlNode lr = style == null ? null : FirstChild(style, "lnRef");
                        if (lr != null && FirstChild(lr, "schemeClr") != null && Attr(lr, "idx") != "0") lineColor = ColorOf(ctx, lr);
                    }
                    if (lineColor.HasValue)
                        using (var pen = new Pen(lineColor.Value, lw))
                        {
                            XmlNode dash = ln == null ? null : FirstChild(ln, "prstDash");
                            if (dash != null && (Attr(dash, "val") ?? "").IndexOf("dot", StringComparison.OrdinalIgnoreCase) >= 0) pen.DashStyle = DashStyle.Dot;
                            else if (dash != null && (Attr(dash, "val") ?? "") != "solid") pen.DashStyle = DashStyle.Dash;
                            if (line) g.DrawLine(pen, r.Left, r.Top, r.Right, r.Bottom);
                            else g.DrawPath(pen, path);
                        }
                }

                XmlNode body = kind == "sp" ? FirstChild(n, "txBody") : null;
                if (body != null) DrawText(ctx, body, r, ph, layout, master, 0, FontRefColor(ctx, n));
            }
            finally { g.Restore(state); }
        }

        /// <summary>The colour a shape's style gives its text (a filled shape's text is the theme's light colour), when it says one.</summary>
        static Color? FontRefColor(SlideContext ctx, XmlNode shape)
        {
            XmlNode style = FirstChild(shape, "style");
            XmlNode fr = style == null ? null : FirstChild(style, "fontRef");
            if (fr == null || fr.FirstChild == null) return null;
            return ColorOf(ctx, fr);
        }

        static Frame GraphicFrame(XmlNode n)
        {
            var f = new Frame();
            XmlNode x = FirstChild(n, "xfrm");
            if (x == null) return f;
            XmlNode off = FirstChild(x, "off"), ext = FirstChild(x, "ext");
            if (off == null || ext == null) return f;
            f.X = Num(Attr(off, "x"), 0); f.Y = Num(Attr(off, "y"), 0); f.W = Num(Attr(ext, "cx"), 0); f.H = Num(Attr(ext, "cy"), 0); f.Set = true;
            return f;
        }

        static GraphicsPath Outline(string geom, RectangleF r)
        {
            var p = new GraphicsPath();
            switch (geom)
            {
                case "ellipse": p.AddEllipse(r); break;
                case "roundRect":
                    {
                        float rad = Math.Min(r.Width, r.Height) * 0.16f;
                        if (rad < 1) { p.AddRectangle(r); break; }
                        p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
                        p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
                        p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
                        p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
                        p.CloseFigure();
                        break;
                    }
                case "triangle": p.AddPolygon(new[] { new PointF(r.X + r.Width / 2, r.Y), new PointF(r.Right, r.Bottom), new PointF(r.X, r.Bottom) }); break;
                case "diamond": p.AddPolygon(new[] { new PointF(r.X + r.Width / 2, r.Y), new PointF(r.Right, r.Y + r.Height / 2), new PointF(r.X + r.Width / 2, r.Bottom), new PointF(r.X, r.Y + r.Height / 2) }); break;
                case "rightArrow":
                    {
                        float m = r.Height * 0.25f, hx = r.Right - Math.Min(r.Width * 0.4f, r.Height * 0.5f);
                        p.AddPolygon(new[] { new PointF(r.X, r.Y + m), new PointF(hx, r.Y + m), new PointF(hx, r.Y), new PointF(r.Right, r.Y + r.Height / 2), new PointF(hx, r.Bottom), new PointF(hx, r.Bottom - m), new PointF(r.X, r.Bottom - m) });
                        break;
                    }
                default: p.AddRectangle(r); break;
            }
            return p;
        }

        static void DrawPicture(SlideContext ctx, XmlNode pic, Dictionary<string, string> rels, RectangleF r, XmlNode spPr)
        {
            XmlNode blip = Find(pic, "blipFill", "blip");
            string id = blip == null ? null : Attr(blip, "embed");
            string target;
            if (id == null || !rels.TryGetValue(id, out target)) return;
            Image img;
            if (!ctx.Pictures.TryGetValue(target, out img))
            {
                try
                {
                    ZipArchiveEntry e = ctx.Zip.GetEntry(target);
                    using (Stream s = e.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms); ms.Position = 0;
                        using (var raw = Image.FromStream(ms, false, false)) img = new Bitmap(raw);
                    }
                }
                catch { img = null; ctx.Skipped++; }
                ctx.Pictures[target] = img;
            }
            if (img == null) return;

            // The picture's crop: percentages of each side, in thousandths.
            XmlNode src = Find(pic, "blipFill", "srcRect");
            var from = new RectangleF(0, 0, img.Width, img.Height);
            if (src != null)
            {
                float l = (float)Num(Attr(src, "l"), 0) / 100000f, tp = (float)Num(Attr(src, "t"), 0) / 100000f, rr = (float)Num(Attr(src, "r"), 0) / 100000f, b = (float)Num(Attr(src, "b"), 0) / 100000f;
                from = new RectangleF(img.Width * l, img.Height * tp, Math.Max(1, img.Width * (1 - l - rr)), Math.Max(1, img.Height * (1 - tp - b)));
            }
            ctx.G.DrawImage(img, r, from, GraphicsUnit.Pixel);
        }

        static void DrawFrame(SlideContext ctx, XmlNode frame, RectangleF r, XmlDocument layout, XmlDocument master)
        {
            Graphics g = ctx.G;
            XmlNode data = Find(frame, "graphic", "graphicData");
            XmlNode tbl = data == null ? null : FirstChild(data, "tbl");
            if (tbl == null)
            {
                if (data != null && (Attr(data, "uri") ?? "").EndsWith("chart", StringComparison.Ordinal)) ctx.Charts++; else ctx.Skipped++;
                using (var br = new SolidBrush(Color.FromArgb(235, 235, 235))) g.FillRectangle(br, r.X, r.Y, r.Width, r.Height);
                using (var pen = new Pen(Color.FromArgb(180, 180, 180))) g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                return;
            }
            var widths = new List<double>();
            XmlNode grid = FirstChild(tbl, "tblGrid");
            double total = 0;
            if (grid != null) foreach (XmlNode gc in grid.ChildNodes) { double w = Num(Attr(gc, "w"), 0); widths.Add(w); total += w; }
            if (total <= 0) return;
            double scale = r.Width / total;
            float y = r.Y;
            foreach (XmlNode row in tbl.ChildNodes)
            {
                if (row.LocalName != "tr") continue;
                float rh = (float)(Num(Attr(row, "h"), 370840) / ctx.EmuPerPx);
                float x = r.X;
                int col = 0;
                foreach (XmlNode tc in row.ChildNodes)
                {
                    if (tc.LocalName != "tc") continue;
                    double cw = col < widths.Count ? widths[col] : 0;
                    int span = (int)Num(Attr(tc, "gridSpan"), 1);
                    for (int k = 1; k < span && col + k < widths.Count; k++) cw += widths[col + k];
                    var cell = new RectangleF(x, y, (float)(cw * scale), rh);
                    XmlNode pr = FirstChild(tc, "tcPr");
                    XmlNode fill = pr == null ? null : FirstChild(pr, "solidFill");
                    if (fill != null) using (var br = new SolidBrush(ColorOf(ctx, fill))) g.FillRectangle(br, cell);
                    using (var pen = new Pen(Color.FromArgb(150, 150, 150))) g.DrawRectangle(pen, cell.X, cell.Y, cell.Width, cell.Height);
                    XmlNode body = FirstChild(tc, "txBody");
                    if (body != null) DrawText(ctx, body, cell, null, layout, master, 14);
                    x += cell.Width;
                    col += Math.Max(1, span);
                    if (Attr(tc, "hMerge") == "1") { }
                }
                y += rh;
            }
        }

        static void DrawText(SlideContext ctx, XmlNode body, RectangleF r, XmlNode ph, XmlDocument layout, XmlDocument master, double baseSize = 0, Color? textColor = null)
        {
            Graphics g = ctx.G;
            XmlNode bp = FirstChild(body, "bodyPr");
            double px = ctx.EmuPerPx;
            float li = (float)(Num(Attr(bp, "lIns"), 91440) / px), ti = (float)(Num(Attr(bp, "tIns"), 45720) / px), ri = (float)(Num(Attr(bp, "rIns"), 91440) / px), bi = (float)(Num(Attr(bp, "bIns"), 45720) / px);
            var area = new RectangleF(r.X + li, r.Y + ti, Math.Max(2, r.Width - li - ri), Math.Max(2, r.Height - ti - bi));
            string anchor = Attr(bp, "anchor") ?? (ph != null && ((Attr(ph, "type") ?? "") == "ctrTitle" || (Attr(ph, "type") ?? "") == "title") ? "ctr" : "t");
            bool wrap = Attr(bp, "wrap") != "none";
            XmlNode fit = FirstChild(bp, "normAutofit");
            double fontScale = fit == null ? 1 : Num(Attr(fit, "fontScale"), 100000) / 100000.0;

            // Default size by kind of placeholder: what the master's styles would give.
            string type = ph == null ? "" : (Attr(ph, "type") ?? "body");
            double def = baseSize > 0 ? baseSize : type == "ctrTitle" || type == "title" ? 44 : type == "subTitle" ? 24 : type == "body" || type == "" && ph != null ? 28 : 18;
            if (ph != null && type == "") def = 28;

            float offsetTop = 0;
            var blocks = new List<PlacedParagraph>();
            foreach (XmlNode p in body.ChildNodes)
            {
                if (p.LocalName != "p") continue;
                XmlNode pPr = FirstChild(p, "pPr");
                string algn = pPr == null ? null : Attr(pPr, "algn");
                int lvl = pPr == null ? 0 : (int)Num(Attr(pPr, "lvl"), 0);
                var runs = new List<PlacedRun>();
                XmlNode end = FirstChild(p, "endParaRPr");
                foreach (XmlNode rn in p.ChildNodes)
                {
                    if (rn.LocalName != "r" && rn.LocalName != "br" && rn.LocalName != "fld") continue;
                    XmlNode rPr = FirstChild(rn, "rPr");
                    double size = rPr != null && Attr(rPr, "sz") != null ? Num(Attr(rPr, "sz"), 1800) / 100.0 : def;
                    bool bold = rPr != null && Attr(rPr, "b") == "1", italic = rPr != null && Attr(rPr, "i") == "1", under = rPr != null && (Attr(rPr, "u") ?? "none") != "none";
                    string face = "Calibri";
                    XmlNode latin = rPr == null ? null : FirstChild(rPr, "latin");
                    if (latin != null && Attr(latin, "typeface") != null && !Attr(latin, "typeface").StartsWith("+", StringComparison.Ordinal)) face = Attr(latin, "typeface");
                    XmlNode cs = rPr == null ? null : FirstChild(rPr, "cs");
                    Color color = textColor ?? Color.Black;
                    XmlNode fillNode = rPr == null ? null : FirstChild(rPr, "solidFill");
                    if (fillNode != null) color = ColorOf(ctx, fillNode);
                    else if (!textColor.HasValue && (type == "title" || type == "ctrTitle")) color = ColorOf(ctx, MakeScheme("tx1"));
                    string text = rn.LocalName == "br" ? "\n" : (FirstChild(rn, "t") == null ? "" : FirstChild(rn, "t").InnerText);
                    runs.Add(new PlacedRun { Text = text, Size = size * fontScale, Bold = bold, Italic = italic, Underline = under, Face = face, Color = color });
                }
                string bullet = null;
                XmlNode bu = pPr == null ? null : FirstChild(pPr, "buChar");
                if (bu != null) bullet = Attr(bu, "char") ?? "•";
                else if (ph != null && (type == "body" || type == "") && FirstChild(pPr, "buNone") == null && runs.Count > 0 && runs[0].Text.Length > 0) bullet = "•";
                double spBefore = 0;
                XmlNode spcBef = pPr == null ? null : Find(pPr, "spcBef", "spcPts");
                if (spcBef != null) spBefore = Num(Attr(spcBef, "val"), 0) / 100.0;
                double marL = pPr == null ? (bullet != null ? 342900 + lvl * 342900 : 0) : Num(Attr(pPr, "marL"), bullet != null ? 342900 + lvl * 342900 : lvl * 342900);
                double indent = pPr == null ? (bullet != null ? -342900 : 0) : Num(Attr(pPr, "indent"), bullet != null ? -342900 : 0);
                blocks.Add(new PlacedParagraph { Runs = runs, Align = algn ?? (type == "ctrTitle" || type == "subTitle" ? "ctr" : "l"), Bullet = bullet, MarL = (float)(marL / px), Indent = (float)(indent / px), SpaceBefore = (float)(spBefore * 96 / 72) });
                if (runs.Count == 0 && end != null) { double es = Num(Attr(end, "sz"), def * 100) / 100.0; runs.Add(new PlacedRun { Text = "", Size = es * fontScale, Face = "Calibri", Color = Color.Black }); }
            }

            // Lay out: each paragraph's runs word by word into lines.
            float y = 0;
            var drawn = new List<Action>();
            foreach (PlacedParagraph para in blocks)
            {
                float firstX = para.MarL + Math.Min(0, para.Indent);
                float avail = area.Width - para.MarL;
                if (avail < 4) avail = area.Width;
                var wordsList = new List<Word>();
                foreach (PlacedRun run in para.Runs)
                {
                    if (run.Text == "\n") { wordsList.Add(new Word { Break = true, Run = run }); continue; }
                    foreach (string piece in Regex.Split(run.Text, @"(?<=\s)"))
                        if (piece.Length > 0) wordsList.Add(new Word { Text = piece, Run = run });
                }
                var lineWords = new List<Word>();
                var paraLines = new List<List<Word>>();
                float lineW = 0;
                foreach (Word w in wordsList)
                {
                    if (w.Break) { paraLines.Add(lineWords); lineWords = new List<Word>(); lineW = 0; continue; }
                    Font f = FontOf(w.Run);
                    w.Width = g.MeasureString(w.Text.TrimEnd(), f, 100000, StringFormat.GenericTypographic).Width + (w.Text.Length - w.Text.TrimEnd().Length) * f.Size * 0.3f;
                    if (wrap && lineW + w.Width > avail && lineWords.Count > 0) { paraLines.Add(lineWords); lineWords = new List<Word>(); lineW = 0; }
                    lineWords.Add(w); lineW += w.Width;
                }
                paraLines.Add(lineWords);

                double maxSize = 0;
                foreach (PlacedRun run in para.Runs) maxSize = Math.Max(maxSize, run.Size);
                if (maxSize == 0) maxSize = def * fontScale;
                float lineH = (float)(maxSize * 96 / 72 * 1.2);
                y += para.SpaceBefore;
                bool firstLine = true;
                foreach (List<Word> lw in paraLines)
                {
                    float w = 0;
                    foreach (Word word in lw) w += word.Width;
                    float atY = y;
                    float startX = area.X + (firstLine ? firstX : para.MarL);
                    float room = area.Width - (firstLine ? firstX : para.MarL);
                    float offsetX = para.Align == "ctr" ? Math.Max(0, (room - w) / 2) : para.Align == "r" ? Math.Max(0, room - w) : 0;
                    bool first = firstLine;
                    PlacedParagraph captured = para;
                    List<Word> ws = lw;
                    float xStart = startX + offsetX;
                    drawn.Add(() =>
                    {
                        float bx = xStart, by = area.Y + atY + offsetTop;
                        if (first && captured.Bullet != null && ws.Count > 0)
                            using (var br = new SolidBrush(ws[0].Run.Color)) g.DrawString(captured.Bullet, FontOf(ws[0].Run), br, area.X + captured.MarL + Math.Min(0, captured.Indent) + 0f, by, StringFormat.GenericTypographic);
                        float cx = bx;
                        foreach (Word word in ws)
                        {
                            using (var br = new SolidBrush(word.Run.Color)) g.DrawString(word.Text, FontOf(word.Run), br, cx, by, StringFormat.GenericTypographic);
                            cx += word.Width;
                        }
                    });
                    y += lineH;
                    firstLine = false;
                }
            }
            float totalH = y;
            offsetTop = anchor == "ctr" ? Math.Max(0, (area.Height - totalH) / 2) : anchor == "b" ? Math.Max(0, area.Height - totalH) : 0;
            GraphicsState keep = g.Save();
            g.SetClip(new RectangleF(r.X, r.Y, r.Width, r.Height), CombineMode.Intersect);
            foreach (Action a in drawn) a();
            g.Restore(keep);
        }


        class PlacedRun { public string Text = ""; public double Size = 18; public bool Bold, Italic, Underline; public string Face = "Calibri"; public Color Color = Color.Black; }
        class PlacedParagraph { public List<PlacedRun> Runs; public string Align = "l"; public string Bullet; public float MarL, Indent, SpaceBefore; }
        class Word { public string Text = ""; public PlacedRun Run; public float Width; public bool Break; }

        static readonly Dictionary<string, Font> FontCache = new Dictionary<string, Font>();

        static Font FontOf(PlacedRun r)
        {
            string key = r.Face + "|" + Math.Round(r.Size, 1) + "|" + r.Bold + r.Italic + r.Underline;
            lock (FontCache)
            {
                Font f;
                if (FontCache.TryGetValue(key, out f)) return f;
                FontStyle style = (r.Bold ? FontStyle.Bold : 0) | (r.Italic ? FontStyle.Italic : 0) | (r.Underline ? FontStyle.Underline : 0);
                try { f = new Font(r.Face, (float)r.Size, style, GraphicsUnit.Point); }
                catch { f = new Font("Segoe UI", (float)r.Size, style, GraphicsUnit.Point); }
                if (FontCache.Count > 200) { foreach (Font old in FontCache.Values) old.Dispose(); FontCache.Clear(); }
                FontCache[key] = f;
                return f;
            }
        }

        static XmlNode MakeScheme(string name)
        {
            var d = new XmlDocument();
            XmlElement e = d.CreateElement("schemeClr", A);
            e.SetAttribute("val", name);
            return e;
        }
    }
}
