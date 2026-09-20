// =============================================================================
// NextScan Studio - output filename templates
// Plan ref: MASTER_PLAN section 12 (jobs & automation), P3 persona "auto-naming".
//
// Every scanner suite worth using lets the operator decide what files are
// called; the alternative is a folder of scan_20260907_143012.jpg that nobody
// can search. This expands a pattern such as
//
//     {yyyy}\{MM}\invoice_{nnnn}
//
// into a real path. Backslashes in the pattern are kept as folder separators
// (dated subfolders are the most-requested naming feature in this class of
// software); backslashes appearing inside a *token value* are stripped, because
// a scanner model name must never be able to redirect where a file lands.
//
// The counter is not stored in a settings file. It is discovered by probing the
// output folder for the first free name. A stored counter goes stale the moment
// the user moves, renames or restores files, and the failure mode is silently
// overwriting a scan the operator believed was safe. Probing cannot do that.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NextScan.Core
{
    /// <summary>Values a template can refer to. All fields are optional.</summary>
    public class NameContext
    {
        public DateTime Stamp = DateTime.Now;
        public string Device = "";
        public int Dpi;
        public string Mode = "";        // colour, gray, bw
        public string Source = "";      // flatbed, adf, adfduplex
        public int Page = 1;            // page number within the document
        public int Document = 1;        // document number within the batch
        public int Side;                // 0 = front, 1 = back
        public string BatchName = "";
    }

    public static class NameTemplate
    {
        /// <summary>
        /// Marks where the counter goes before its width is known. It is a
        /// printable string rather than a control character so a half-resolved
        /// pattern can be logged, shown in the UI and compared in a test without
        /// carrying anything invisible.
        /// </summary>
        const string CounterSlot = "{#}";

        /// <summary>The tokens this build understands, for UI help text.</summary>
        public static readonly string[] Tokens = new string[]
        {
            "{date}", "{time}", "{yyyy}", "{yy}", "{MM}", "{dd}", "{HH}", "{mm}", "{ss}",
            "{n}", "{nnn}", "{nnnn}", "{nnnnnn}",
            "{p}", "{ppp}", "{doc}", "{side}",
            "{device}", "{dpi}", "{mode}", "{source}", "{batch}", "{user}"
        };

        /// <summary>
        /// Expands every token except the counter, which is left as a slot for
        /// <see cref="ResolvePath"/> to fill in once the folder is known.
        /// </summary>
        public static string Expand(string pattern, NameContext c, out string counterFormat)
        {
            counterFormat = null;
            if (string.IsNullOrEmpty(pattern)) pattern = "scan_{date}_{time}";
            if (c == null) c = new NameContext();

            StringBuilder sb = new StringBuilder(pattern.Length + 32);
            int i = 0;
            while (i < pattern.Length)
            {
                if (pattern[i] != '{') { sb.Append(pattern[i]); i++; continue; }

                int close = pattern.IndexOf('}', i);
                if (close < 0) { sb.Append(pattern, i, pattern.Length - i); break; }

                string token = pattern.Substring(i + 1, close - i - 1);
                i = close + 1;

                // A counter token becomes a slot rather than a value: its width
                // is decided here, but which number is free is only knowable
                // once the destination folder is known.
                if (token.Length > 0 && token.TrimEnd('n').Length == 0)
                {
                    counterFormat = new string('0', token.Length);
                    sb.Append(CounterSlot);
                    continue;
                }

                sb.Append(Clean(Value(token, c)));
            }
            return sb.ToString();
        }

        static string Value(string token, NameContext c)
        {
            switch (token)
            {
                case "date": return c.Stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                case "time": return c.Stamp.ToString("HHmmss", CultureInfo.InvariantCulture);
                case "yyyy": return c.Stamp.ToString("yyyy", CultureInfo.InvariantCulture);
                case "yy": return c.Stamp.ToString("yy", CultureInfo.InvariantCulture);
                case "MM": return c.Stamp.ToString("MM", CultureInfo.InvariantCulture);
                case "dd": return c.Stamp.ToString("dd", CultureInfo.InvariantCulture);
                case "HH": return c.Stamp.ToString("HH", CultureInfo.InvariantCulture);
                case "mm": return c.Stamp.ToString("mm", CultureInfo.InvariantCulture);
                case "ss": return c.Stamp.ToString("ss", CultureInfo.InvariantCulture);

                case "p": return c.Page.ToString(CultureInfo.InvariantCulture);
                case "pp": return c.Page.ToString("00", CultureInfo.InvariantCulture);
                case "ppp": return c.Page.ToString("000", CultureInfo.InvariantCulture);
                case "doc": return c.Document.ToString("000", CultureInfo.InvariantCulture);
                case "side": return c.Side == 1 ? "back" : "front";

                case "device": return c.Device;
                case "dpi": return c.Dpi > 0 ? c.Dpi.ToString(CultureInfo.InvariantCulture) : "";
                case "mode": return c.Mode;
                case "source": return c.Source;
                case "batch": return c.BatchName;
                case "user": return Environment.UserName;

                // An unknown token stays visible rather than being silently
                // dropped, so a typo shows up in the filename instead of quietly
                // collapsing two different patterns onto one name.
                default: return "{" + token + "}";
            }
        }

        static readonly char[] Illegal = new char[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

        /// <summary>Makes a token *value* safe to embed in one path segment.</summary>
        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char ch in s)
            {
                if (ch < 32) continue;
                bool bad = false;
                foreach (char k in Illegal) if (ch == k) { bad = true; break; }
                sb.Append(bad ? '_' : ch);
            }
            return sb.ToString().Trim();
        }

        static readonly string[] Reserved = new string[]
        {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
        };

        /// <summary>
        /// Makes one path segment legal on Windows: no trailing dot or space
        /// (the filesystem drops them silently, which turns "a." and "a" into
        /// the same file), and no reserved device name.
        /// </summary>
        static string SafeSegment(string seg)
        {
            seg = seg.TrimEnd(' ', '.');
            if (seg.Length == 0) return "_";

            string stem = seg;
            int dot = stem.IndexOf('.');
            if (dot > 0) stem = stem.Substring(0, dot);
            foreach (string r in Reserved)
                if (string.Equals(stem, r, StringComparison.OrdinalIgnoreCase)) return "_" + seg;

            return seg;
        }

        /// <summary>
        /// Turns a pattern into a full path that does not yet exist, creating any
        /// folders the pattern asked for.
        /// </summary>
        /// <param name="exists">
        /// Overridable existence test. The batch writer passes one that also
        /// treats names it has handed out but not yet written as taken, so two
        /// pages in the same run cannot claim one name.
        /// </param>
        public static string ResolvePath(string directory, string pattern, string extension,
                                         NameContext ctx, Func<string, bool> exists = null)
        {
            if (exists == null) exists = File.Exists;
            string ext = "." + (extension ?? "jpg").TrimStart('.').ToLowerInvariant();

            string counterFormat;
            string expanded = Expand(pattern, ctx, out counterFormat);

            // Split on both separators so a pattern written with forward slashes,
            // which people do out of habit, still produces subfolders.
            string[] parts = expanded.Split('\\', '/');
            List<string> segs = new List<string>();
            for (int k = 0; k < parts.Length; k++)
            {
                string p = parts[k].Trim();
                if (p.Length == 0) continue;
                segs.Add(p);
            }
            if (segs.Count == 0) segs.Add("scan");

            string stem = segs[segs.Count - 1];
            segs.RemoveAt(segs.Count - 1);

            string dir = directory ?? "";
            foreach (string s in segs) dir = Path.Combine(dir, SafeSegment(s));
            if (dir.Length > 0) Directory.CreateDirectory(dir);

            if (counterFormat != null)
            {
                // The pattern asked for a counter, so find the first free one.
                for (int n = 1; n <= 999999; n++)
                {
                    string name = stem.Replace(CounterSlot,
                        n.ToString(counterFormat, CultureInfo.InvariantCulture)) + ext;
                    string full = Path.Combine(dir, SafeSegment(name));
                    if (!exists(full)) return full;
                }
                throw new IOException("the counter in the name pattern is exhausted");
            }

            // No counter in the pattern. The name still must not overwrite an
            // existing scan, so fall back to a suffix - what Explorer does, and
            // immediately recognisable to the user.
            string plain = Path.Combine(dir, SafeSegment(stem + ext));
            if (!exists(plain)) return plain;
            for (int n = 2; n <= 999999; n++)
            {
                string full = Path.Combine(dir,
                    SafeSegment(stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext));
                if (!exists(full)) return full;
            }
            throw new IOException("could not find a free name for " + stem + ext);
        }
    }
}
