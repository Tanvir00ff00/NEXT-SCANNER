// =============================================================================
// NextScan Studio - what a conversation is drawn with
// Plan ref: docs/AI_LAYER.md
//
// The first transcript was one kind of thing, a bubble of plain text, and it
// showed everything the same way: an answer, a step the assistant took, a
// failure. Every assistant people actually use draws four different things,
// and so does this one now:
//
//   * the answer, formatted -- headings, bold, lists, tables, code -- since a
//     model writes Markdown whether or not anything reads it, and shown raw it
//     is a page of asterisks;
//   * the thinking, as it happens, in a block that says "Thinking" while it
//     runs and "Thought for 12 s" after, closed unless it is opened;
//   * each step it takes, as a line with a spinner that becomes a tick or a
//     cross, so a turn that works for a minute visibly works;
//   * and a status line at the foot of the turn, saying what it is doing now.
//
// All of them are measured by the transcript the same way (ITranscriptItem):
// asked how tall they are at the width they will be drawn at.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>Anything in the transcript: says how tall it is at a width.</summary>
    public interface ITranscriptItem
    {
        int MeasureHeight(int width);
    }

    // =========================================================================
    // Markdown, as RTF
    // =========================================================================
    /// <summary>
    /// The Markdown a model writes, as RTF for a RichTextBox: headings, bold,
    /// italic, inline code, code blocks, bullet and numbered lists, quotes,
    /// rules, links and pipe tables. Not a full CommonMark parser, and not
    /// meant to be: it is the part of Markdown models actually use, drawn in
    /// the panel's own colours. Anything it does not understand is shown as the
    /// text it is.
    /// </summary>
    public static class ChatMarkdown
    {
        public static string ToRtf(string markdown, float size, Color text, Color dim, Color accent, Color codeBack)
        {
            int fs = (int)Math.Round(size * 2);
            var o = new StringBuilder();
            o.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fnil Segoe UI;}{\f1\fmodern Consolas;}{\f2\fnil Nirmala UI;}}");
            o.Append(@"{\colortbl ;").Append(Col(text)).Append(Col(dim)).Append(Col(accent)).Append(Col(codeBack)).Append("}");
            o.Append(@"\viewkind4\f0\cf1\fs").Append(fs).Append(' ');

            string[] lines = (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool firstBlock = true;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();

                // ---- fenced code ----
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    var code = new List<string>();
                    for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                    Para(o, ref firstBlock, @"\li120\ri120\sb60\sa60");
                    o.Append(@"\f1\fs").Append(fs - 2).Append(@"\highlight4 ");
                    for (int c = 0; c < code.Count; c++)
                    {
                        if (c > 0) o.Append(@"\line ");
                        Esc(o, code[c].Length == 0 ? " " : code[c]);
                    }
                    o.Append(@"\highlight0\f0\fs").Append(fs).Append(' ');
                    continue;
                }

                // ---- a table: consecutive lines starting with '|' ----
                if (trimmed.StartsWith("|", StringComparison.Ordinal) && i + 1 < lines.Length && IsRule(lines[i + 1]))
                {
                    var rows = new List<List<string>>();
                    rows.Add(Cells(trimmed));
                    i += 1;
                    while (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith("|", StringComparison.Ordinal)) rows.Add(Cells(lines[++i].Trim()));
                    Table(o, ref firstBlock, rows, fs);
                    continue;
                }

                if (trimmed.Length == 0) continue;

                // ---- a rule ----
                if (Regex.IsMatch(trimmed, @"^(\*\s*){3,}$|^(-\s*){3,}$|^(_\s*){3,}$"))
                {
                    Para(o, ref firstBlock, @"\sb40\sa40");
                    o.Append(@"\cf2 ");
                    for (int r = 0; r < 16; r++) Esc(o, ((char)0x2500).ToString());
                    o.Append(@"\cf1 ");
                    continue;
                }

                // ---- headings ----
                Match h = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
                if (h.Success)
                {
                    int level = h.Groups[1].Value.Length;
                    int hs = level == 1 ? fs + 8 : level == 2 ? fs + 5 : fs + 2;
                    Para(o, ref firstBlock, @"\sb" + (firstBlock ? 0 : 140) + @"\sa60\keepn");
                    o.Append(@"\b\fs").Append(hs).Append(' ');
                    Inline(o, h.Groups[2].Value.TrimEnd('#', ' '), fs);
                    o.Append(@"\b0\fs").Append(fs).Append(' ');
                    continue;
                }

                // ---- lists ----
                Match bullet = Regex.Match(line, @"^(\s*)[-*+]\s+(.*)$");
                Match number = Regex.Match(line, @"^(\s*)(\d{1,3})[.)]\s+(.*)$");
                if (bullet.Success || number.Success)
                {
                    int depth = Math.Min(3, ((bullet.Success ? bullet.Groups[1].Value : number.Groups[1].Value).Replace("\t", "    ").Length) / 2);
                    // Room for "10." before the text starts, or the first line of
                    // item ten sits further right than the lines under it.
                    int hang = number.Success ? 380 : 220;
                    int left = (number.Success ? 440 : 300) + depth * 280;
                    Para(o, ref firstBlock, @"\fi-" + hang + @"\li" + left + @"\tx" + left + @"\sa30");
                    if (bullet.Success)
                    {
                        o.Append(@"\cf3 ").Append(depth == 0 ? "\\u8226?" : "\\u9702?").Append(@"\cf1\tab ");
                        Inline(o, bullet.Groups[2].Value, fs);
                    }
                    else
                    {
                        o.Append(@"\cf3 ").Append(number.Groups[2].Value).Append(@".\cf1\tab ");
                        Inline(o, number.Groups[3].Value, fs);
                    }
                    continue;
                }

                // ---- a quote ----
                if (trimmed.StartsWith(">", StringComparison.Ordinal))
                {
                    Para(o, ref firstBlock, @"\li240\sa60");
                    o.Append(@"\cf2\i ");
                    Inline(o, trimmed.TrimStart('>', ' '), fs);
                    o.Append(@"\i0\cf1 ");
                    continue;
                }

                // ---- a paragraph: this line and the ones that run on ----
                var body = new StringBuilder(trimmed);
                while (i + 1 < lines.Length)
                {
                    string next = lines[i + 1].Trim();
                    if (next.Length == 0 || next.StartsWith("#", StringComparison.Ordinal) || next.StartsWith("```", StringComparison.Ordinal) ||
                        next.StartsWith("|", StringComparison.Ordinal) || next.StartsWith(">", StringComparison.Ordinal) ||
                        Regex.IsMatch(lines[i + 1], @"^\s*([-*+]|\d{1,3}[.)])\s+")) break;
                    i++;
                    // A line break inside a paragraph is kept: in a chat it is
                    // almost always meant (an address, a list without marks).
                    body.Append('\n').Append(next);
                }
                Para(o, ref firstBlock, @"\sa100");
                string[] parts = body.ToString().Split('\n');
                for (int p = 0; p < parts.Length; p++)
                {
                    if (p > 0) o.Append(@"\line ");
                    Inline(o, parts[p], fs);
                }
            }
            o.Append("}");
            return o.ToString();
        }

        static void Para(StringBuilder o, ref bool first, string format)
        {
            if (!first) o.Append(@"\par");
            o.Append(@"\pard").Append(format).Append(' ');
            first = false;
        }

        static bool IsRule(string line) { return Regex.IsMatch(line.Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$"); }

        static List<string> Cells(string row)
        {
            string r = row.Trim();
            if (r.StartsWith("|", StringComparison.Ordinal)) r = r.Substring(1);
            if (r.EndsWith("|", StringComparison.Ordinal)) r = r.Substring(0, r.Length - 1);
            var cells = new List<string>();
            foreach (string c in r.Split('|')) cells.Add(c.Trim());
            return cells;
        }

        /// <summary>
        /// A table, as cards: each row a short block with its first cell in
        /// bold and every other cell under it as "Header: value". A grid of
        /// padded columns was tried first and wrapped into nonsense in a panel
        /// a third of the window wide, which is the only width this has; the
        /// cards read the same at any width and copy out as plain lines.
        /// </summary>
        static void Table(StringBuilder o, ref bool first, List<List<string>> rows, int fs)
        {
            List<string> head = rows[0];
            for (int ri = 1; ri < rows.Count; ri++)
            {
                List<string> row = rows[ri];
                if (row.Count == 0) continue;
                Para(o, ref first, (ri == 1 ? @"\sb60" : @"\sb100") + @"\sa0\li120");
                o.Append(@"\b ");
                Inline(o, row[0], fs);
                o.Append(@"\b0 ");
                for (int c = 1; c < row.Count; c++)
                {
                    if (row[c].Length == 0) continue;
                    o.Append(@"\line ");
                    if (c < head.Count && head[c].Length > 0)
                    {
                        o.Append(@"\cf2 ");
                        Inline(o, head[c], fs);
                        o.Append(@": \cf1 ");
                    }
                    Inline(o, row[c], fs);
                }
            }
            if (rows.Count == 1) { Para(o, ref first, @"\sa60"); Inline(o, string.Join("  ", head.ToArray()), fs); }
            Para(o, ref first, @"\sa40");
        }

        /// <summary>Bold, italic, strike, code and links within a line.</summary>
        static void Inline(StringBuilder o, string s, int fs)
        {
            int i = 0;
            var plain = new StringBuilder();
            // Plain text is gathered and escaped in runs, not a letter at a
            // time: a Bengali word must reach Esc whole to be set in one font.
            Action flush = delegate { if (plain.Length > 0) { Esc(o, plain.ToString()); plain.Length = 0; } };
            bool bold = false, italic = false, strike = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#+-.!|~".IndexOf(s[i + 1]) >= 0) { plain.Append(s[i + 1]); i += 2; continue; }
                if (c == '`')
                {
                    int end = s.IndexOf('`', i + 1);
                    if (end > i)
                    {
                        flush(); o.Append(@"\f1\fs").Append(fs - 1).Append(@"\highlight4 ");
                        Esc(o, s.Substring(i + 1, end - i - 1));
                        flush(); o.Append(@"\highlight0\f0\fs").Append(fs).Append(' ');
                        i = end + 1;
                        continue;
                    }
                }
                if (c == '*' && i + 1 < s.Length && s[i + 1] == '*' || c == '_' && i + 1 < s.Length && s[i + 1] == '_')
                {
                    string mark = s.Substring(i, 2);
                    if (bold || s.IndexOf(mark, i + 2, StringComparison.Ordinal) > i + 2)
                    {
                        bold = !bold;
                        flush(); o.Append(bold ? @"\b " : @"\b0 ");
                        i += 2;
                        continue;
                    }
                }
                if (c == '~' && i + 1 < s.Length && s[i + 1] == '~')
                {
                    if (strike || s.IndexOf("~~", i + 2, StringComparison.Ordinal) > i + 2)
                    {
                        strike = !strike;
                        flush(); o.Append(strike ? @"\strike " : @"\strike0 ");
                        i += 2;
                        continue;
                    }
                }
                if (c == '*')
                {
                    bool opens = !italic && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]) && s.IndexOf('*', i + 1) > i + 1 && (i == 0 || !char.IsLetterOrDigit(s[i - 1]));
                    bool closes = italic && i > 0 && !char.IsWhiteSpace(s[i - 1]);
                    if (opens || closes)
                    {
                        italic = !italic;
                        flush(); o.Append(italic ? @"\i " : @"\i0 ");
                        i++;
                        continue;
                    }
                }
                if (c == '[')
                {
                    Match link = Regex.Match(s.Substring(i), @"^\[([^\]]+)\]\(([^)\s]+)\)");
                    if (link.Success)
                    {
                        flush(); o.Append(@"\cf3\ul ");
                        Esc(o, link.Groups[1].Value);
                        flush(); o.Append(@"\ulnone\cf1 ");
                        if (!link.Groups[2].Value.Equals(link.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                        {
                            flush(); o.Append(@"\cf2 (");
                            Esc(o, link.Groups[2].Value);
                            flush(); o.Append(@")\cf1 ");
                        }
                        i += link.Length;
                        continue;
                    }
                }
                plain.Append(c);
                i++;
            }
            flush();
            if (bold) o.Append(@"\b0 ");
            if (italic) o.Append(@"\i0 ");
            if (strike) o.Append(@"\strike0 ");
        }

        /// <summary>
        /// Text, escaped for RTF. Bengali goes in a group of its own set in
        /// Nirmala UI: the rich edit control does not fall back to a font that
        /// has the letters the way a text box does, and the first Bengali
        /// answer came out as a column of empty boxes.
        /// </summary>
        static void Esc(StringBuilder o, string s)
        {
            bool bengali = false;
            foreach (char c in s)
            {
                bool b = c >= (char)0x0980 && c <= (char)0x09FF || (bengali && (c == (char)0x200C || c == (char)0x200D));
                if (b && !bengali) { o.Append(@"{\f2 "); bengali = true; }
                else if (!b && bengali && c != ' ') { o.Append('}'); bengali = false; }

                if (c == '\\' || c == '{' || c == '}') o.Append('\\').Append(c);
                else if (c == '\t') o.Append(@"\tab ");
                else if (c < 128) o.Append(c);
                else o.Append(@"\u").Append(((int)(short)c).ToString(CultureInfo.InvariantCulture)).Append('?');
            }
            if (bengali) o.Append('}');
        }

        static string Col(Color c) { return @"\red" + c.R + @"\green" + c.G + @"\blue" + c.B + ";"; }
    }

    /// <summary>
    /// A RichTextBox on the current rich edit control (msftedit, RICHEDIT50W)
    /// rather than the 1990s one WinForms creates by default, which lays out
    /// complex scripts poorly and binds no fonts for them. Falls back to the
    /// old control where the new one cannot be loaded.
    /// </summary>
    public class NsRichEdit : RichTextBox
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr LoadLibrary(string name);

        static IntPtr _library;
        static bool _tried;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!_tried) { _tried = true; try { _library = LoadLibrary("msftedit.dll"); } catch { } }
                if (_library != IntPtr.Zero) cp.ClassName = "RICHEDIT50W";
                return cp;
            }
        }
    }

    // =========================================================================
    // Motion
    // =========================================================================
    /// <summary>
    /// The small amount of animation the transcript uses, in one place so the
    /// timings agree with each other: everything eases out, nothing bounces,
    /// and nothing moves that does not mean something (a thing arriving, a
    /// thing working, a thing finished).
    /// </summary>
    public static class ChatFx
    {
        /// <summary>0..1..0 over the period, from the clock: a slow breath.</summary>
        public static float Pulse(double periodMs)
        {
            return (float)(0.5 + 0.5 * Math.Sin(Environment.TickCount / periodMs * 2 * Math.PI));
        }

        static float Clamp01(float v) { return Math.Max(0f, Math.Min(1f, v)); }

        /// <summary>
        /// The size of the text in the conversation, as a multiple of its usual
        /// one: Ctrl+wheel, or Ctrl and plus or minus, in the panel. For the
        /// operator who reads a customer's papers off it at arm's length.
        /// </summary>
        public static float Zoom = 1f;

        /// <summary>Fast at first and settling: the way things arrive.</summary>
        public static double EaseOut(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return 1 - Math.Pow(1 - t, 3);
        }

        /// <summary>
        /// Text with a band of light travelling across it, for "it is working
        /// on this now". Painted with a gradient brush, so it is only for short
        /// status lines; the answer itself is a text box.
        /// </summary>
        public static void ShimmerText(Graphics g, string text, Font font, Rectangle r, Color baseColour, Color light, int periodMs)
        {
            // A status line is never worth a paint failure: a control that
            // throws in OnPaint is drawn as a red cross, so this falls back to
            // plain text rather than let anything through.
            try { Shimmer(g, text, font, r, baseColour, light, periodMs); }
            catch
            {
                TextRenderer.DrawText(g, text ?? "", font, r, baseColour, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
        }

        static void Shimmer(Graphics g, string text, Font font, Rectangle r, Color baseColour, Color light, int periodMs)
        {
            if (r.Width < 6 || r.Height < 2 || string.IsNullOrEmpty(text)) return;

            using (var sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags |= StringFormatFlags.NoWrap;

                // The brush is as wide as the words, and the band of light is
                // a moving stop inside it -- a gradient brush cannot be clamped,
                // and one narrower than the text would tile.
                float wide = Math.Max(8f, Math.Min(r.Width, g.MeasureString(text, font, 4000, sf).Width + 4f));
                float phase = (Environment.TickCount % periodMs) / (float)periodMs;
                float half = 0.22f;
                float centre = -half + phase * (1f + 2f * half);
                float[] at = { 0f, Clamp01(centre - half), Clamp01(centre), Clamp01(centre + half), 1f };
                for (int i = 1; i < at.Length; i++) if (at[i] <= at[i - 1]) at[i] = Math.Min(1f, at[i - 1] + 0.0001f);
                at[4] = 1f;
                for (int i = 3; i >= 1; i--) if (at[i] >= at[i + 1]) at[i] = Math.Max(0f, at[i + 1] - 0.0001f);

                using (var brush = new LinearGradientBrush(new RectangleF(r.X, r.Y, wide, r.Height), baseColour, baseColour, LinearGradientMode.Horizontal))
                {
                    var blend = new ColorBlend(5);
                    blend.Colors = new[] { baseColour, baseColour, light, baseColour, baseColour };
                    blend.Positions = at;
                    brush.InterpolationColors = blend;
                    TextRenderingHint was = g.TextRenderingHint;
                    g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                    g.DrawString(text, font, brush, r, sf);
                    g.TextRenderingHint = was;
                }
            }
        }
    }

    // =========================================================================
    // What every item in the transcript can do
    // =========================================================================
    /// <summary>
    /// The transcript is a column of separate controls -- a bubble, a formatted
    /// answer, a line for each step -- and a person expects it to behave as one
    /// page: drag from the first line to the last and copy the lot. A control
    /// per item cannot do that by itself, since a text box selects inside its
    /// own edges and nowhere else, so every item answers the same few
    /// questions (what is your text, what of it is selected, select this part
    /// of you) and the panel drives them together (StudioAiPanel, "selection").
    ///
    /// Items with a native text box on them select natively; the painted ones
    /// (a step, the thinking) are selected or not as a whole and show it with a
    /// tint.
    /// </summary>
    public abstract class ChatItemBase : NsBase, ITranscriptItem
    {
        /// <summary>The left button went down on this item, at this character.</summary>
        public event Action<ChatItemBase, int> SelectStarted;

        /// <summary>The pointer moved with the button down; the point is on screen.</summary>
        public event Action<ChatItemBase, Point> SelectDragged;

        public event Action<ChatItemBase> SelectEnded;

        /// <summary>A wheel was turned over the text of this item. The transcript scrolls.</summary>
        public event MouseEventHandler Wheeled;

        /// <summary>Painted items: selected as a whole.</summary>
        public bool Marked;

        /// <summary>The tick the item appeared at while it is still arriving; 0 once it has.</summary>
        public int Born;

        public virtual bool Selectable { get { return true; } }

        /// <summary>Has a native text box, which selects for itself.</summary>
        public virtual bool Native { get { return false; } }

        public virtual string Kind { get { return "item"; } }

        public abstract int MeasureHeight(int width);

        /// <summary>The text as it is drawn.</summary>
        public virtual string PlainText { get { return ""; } }

        /// <summary>The text as it was written (Markdown for an answer).</summary>
        public virtual string MarkdownText { get { return PlainText; } }

        public virtual string SelectedText { get { return Marked ? PlainText : ""; } }
        public virtual bool AllSelected { get { return Marked; } }
        public virtual bool AnySelected { get { return Marked; } }
        public virtual int TextLength { get { return 1; } }

        public virtual void SelectAllText() { SetMarked(true); }
        public virtual void SelectRange(int from, int to) { SetMarked(to > from); }
        public virtual void ClearSelection() { SetMarked(false); }

        /// <summary>The character nearest a point on screen. Painted items have one: 0 in their top half, 1 below.</summary>
        public virtual int CharAt(Point screen)
        {
            Point p = PointToClient(screen);
            return p.Y < Height / 2 ? 0 : 1;
        }

        protected void SetMarked(bool on)
        {
            if (Marked == on) return;
            Marked = on;
            Invalidate();
        }

        /// <summary>What a painted item clears itself with: the surface, tinted when it is selected.</summary>
        protected Color Back
        {
            get { return Marked ? Theme.Mix(Theme.Surface, Theme.Accent, Theme.IsLight ? 0.14 : 0.24) : Theme.Surface; }
        }

        protected void RaiseWheel(MouseEventArgs e) { if (Wheeled != null) Wheeled(this, e); }

        /// <summary>Sends a native box's mouse to the same three events a painted item raises itself.</summary>
        protected void HookNative(TextBoxBase box)
        {
            // Kept lit when the box is not the one with the focus: the
            // selection is the panel's, and the panel says when it goes.
            box.HideSelection = false;

            // The press is remembered here rather than read back from the
            // move: a move only says which buttons are down when it arrives
            // from the mouse, and a drag that is wanted must not depend on it.
            bool down = false;
            box.MouseDown += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                down = true;
                if (SelectStarted != null) SelectStarted(this, IndexAt(box, box.PointToScreen(e.Location)));
            };
            box.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (down && SelectDragged != null) SelectDragged(this, box.PointToScreen(e.Location));
            };
            box.MouseUp += delegate { down = false; if (SelectEnded != null) SelectEnded(this); };
            box.MouseWheel += delegate (object s, MouseEventArgs e)
            {
                // Marked handled, or the edit control passes it on to its
                // parents as well and the transcript moves twice.
                HandledMouseEventArgs handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
                RaiseWheel(e);
            };
        }

        /// <summary>The character a screen point is over in a text box, clamped to its ends.</summary>
        protected static int IndexAt(TextBoxBase box, Point screen)
        {
            if (box.TextLength == 0) return 0;
            Point p = box.PointToClient(screen);
            if (p.Y < 0) return 0;
            if (p.Y > box.Height) return box.TextLength;
            return Math.Max(0, Math.Min(box.TextLength, box.GetCharIndexFromPosition(p)));
        }

        bool _leftDown;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && Selectable)
            {
                _leftDown = true;
                if (SelectStarted != null) SelectStarted(this, CharAt(PointToScreen(e.Location)));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_leftDown && Selectable && SelectDragged != null) SelectDragged(this, PointToScreen(e.Location));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _leftDown = false;
            if (SelectEnded != null) SelectEnded(this);
            base.OnMouseUp(e);
        }
    }

    // =========================================================================
    // What the operator said, and notes
    // =========================================================================
    /// <summary>
    /// One turn of the operator's, or a note from the application: a painted
    /// plate with real, selectable text on it.
    ///
    /// The text was drawn with TextRenderer at first, and that was wrong. It
    /// looked right and it could not be selected, dragged over, right-clicked or
    /// copied with the keyboard -- and the thing an operator most wants out of
    /// this panel is a transcription, which is to say the text.
    ///
    /// So the text is a read-only TextBox on top of the plate. That costs a
    /// window handle per turn, and both are worth it: Ctrl+C, drag-select and
    /// right-click Copy are what everyone already knows.
    /// </summary>
    public class NsBubble : ChatItemBase
    {
        const int PadX = 13;
        const int PadY = 9;

        readonly TextBox _box;

        /// <summary>The operator's own turn. Tinted and inset; a note is not.</summary>
        public bool Mine;

        /// <summary>Something went wrong in this turn, so it is not a reply to be read.</summary>
        public bool Trouble;

        /// <summary>
        /// A quiet note ("Reopened...", "Stopped."): small, dim, no plate, so
        /// the transcript reads as the conversation with the notes along the
        /// side of it.
        /// </summary>
        public bool Quiet;

        /// <summary>The operator's newest turn can be taken back into the box and changed.</summary>
        public bool Editable;

        public event EventHandler EditWanted;

        bool _overEdit;

        public NsBubble()
        {
            Font = Theme.Ui(9f * ChatFx.Zoom);
            Cursor = Cursors.Default;

            // The plate is read, not operated; the box on top of it takes the
            // focus when the operator actually selects something.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            _box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Multiline = true,
                WordWrap = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.None,
                TabStop = false,
                Font = Font,
                Cursor = Cursors.IBeam
            };
            HookNative(_box);
            _box.MouseMove += delegate (object sender, MouseEventArgs e)
            {
                // The mark sits over the plate, not over the box, so the box has
                // to hand the pointer back when it crosses it.
                Point at = new Point(e.X + _box.Left, e.Y + _box.Top);
                Hover(EditMark().Contains(at));
            };
            Controls.Add(_box);
            Paint_();
        }

        public override bool Native { get { return true; } }
        public override string Kind { get { return Mine ? "user" : "note"; } }

        public override string Text
        {
            get { return _box == null ? "" : _box.Text; }
            set
            {
                if (_box == null) return;
                // Windows line endings, or every newline from a model shows as a
                // box character in a multi-line TextBox.
                // Trailing newlines are dropped: a model's reply usually ends
                // with one, and the box would give it a blank line of its own.
                string text = (value ?? "").Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", "\r\n");
                if (_box.Text == text) return;
                _box.Text = text;
            }
        }

        // ---- selection --------------------------------------------------------
        public override string PlainText { get { return (_box.Text ?? "").Replace("\r\n", "\n"); } }
        public override string SelectedText { get { return _box.SelectionLength > 0 ? _box.SelectedText.Replace("\r\n", "\n") : ""; } }
        public override bool AnySelected { get { return _box.SelectionLength > 0; } }
        public override bool AllSelected { get { return _box.TextLength > 0 && _box.SelectionLength >= _box.TextLength; } }
        public override int TextLength { get { return _box.TextLength; } }
        public override void SelectAllText() { _box.SelectAll(); }
        public override void SelectRange(int from, int to) { _box.Select(Math.Max(0, from), Math.Max(0, to - from)); }
        public override void ClearSelection() { if (_box.SelectionLength > 0) _box.Select(0, 0); }
        public override int CharAt(Point screen) { return IndexAt(_box, screen); }

        public ContextMenuStrip Menu { set { _box.ContextMenuStrip = value; } }

        /// <summary>
        /// The height this bubble needs at a given width. The transcript asks
        /// before placing it, because a wrapped paragraph has no other way to
        /// say how tall it is.
        /// </summary>
        public override int MeasureHeight(int width)
        {
            // The same width Paint_ gives the box, room for the pencil included:
            // measuring at a wider one left the box lying over the pencil.
            int inner = Math.Max(20, width - Indent() - PadX * 2 - (Editable ? 14 : 0));

            // The box is asked how many lines it wrapped to, at the width it
            // will have. TextRenderer was asked at first, and it breaks lines
            // by different rules: a long reply came out a dozen lines taller
            // than its text, an empty band at the foot of every turn.
            if (_box.IsHandleCreated && _box.TextLength > 0)
            {
                if (_box.Width != inner) _box.Width = inner;
                int lines = _box.GetLineFromCharIndex(_box.TextLength) + 1;
                int line = TextRenderer.MeasureText("Ag", Font, Size.Empty, Flags).Height;
                return Math.Max(32, lines * line + PadY * 2 + 4);
            }

            // Before the box has a window there is nothing to ask. A line of
            // slack, because one line short clips the last one.
            Size size = TextRenderer.MeasureText(Text ?? "", Font, new Size(inner, int.MaxValue), Flags);
            return Math.Max(32, size.Height + PadY * 2 + Font.Height);
        }

        /// <summary>
        /// How far the operator's own turns are pulled in from the left.
        ///
        /// A reply gets the full column. A question is short and is the thing
        /// being answered, so the inset is what separates the two without a
        /// name label on every single turn.
        /// </summary>
        int Indent() { return Mine ? Math.Min(46, Width / 5) : 0; }

        static TextFormatFlags Flags
        {
            get { return TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding; }
        }

        Rectangle EditMark() { return new Rectangle(Width - 25, 5, 18, 18); }

        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Paint_(); }

        void Paint_()
        {
            if (_box == null) return;

            int indent = Indent();
            _box.SetBounds(indent + PadX, PadY,
                           Math.Max(20, Width - indent - PadX * 2 - (Editable ? 14 : 0)), Math.Max(12, Height - PadY * 2));

            _box.BackColor = Fill();
            _box.ForeColor = Trouble ? Theme.Danger : Quiet ? Theme.TextDim : Theme.Text;
            if (Quiet && _box.Font.Size > 8.5f) _box.Font = Theme.Ui(8.25f);
        }

        Color Fill()
        {
            if (Quiet) return Theme.Surface;
            return Mine ? Theme.Mix(Theme.Surface, Theme.Accent, Theme.IsLight ? 0.14 : 0.24)
                        : Theme.Mix(Theme.Surface, Theme.Ground, 0.5);
        }

        /// <summary>Re-reads the palette and the flags after either has changed.</summary>
        public void Refresh_() { Paint_(); Invalidate(); }

        /// <summary>The conversation's text size changed.</summary>
        public void ApplyZoom()
        {
            if (Quiet) return;
            Font = Theme.Ui(9f * ChatFx.Zoom);
            _box.Font = Font;
            Paint_();
        }

        void Hover(bool on)
        {
            on = on && Editable;
            if (on == _overEdit) return;
            _overEdit = on;
            Cursor = on ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Hover(EditMark().Contains(e.Location));
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { Hover(false); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_overEdit && EditWanted != null) { EditWanted(this, EventArgs.Empty); return; }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            int indent = Indent();
            Rectangle r = new Rectangle(indent, 0, Math.Max(1, Width - indent - 1), Math.Max(1, Height - 1));
            if (r.Width <= 2 || r.Height <= 2) return;

            Color edge = Trouble ? Theme.Danger
                       : Mine ? Theme.Mix(Theme.Line, Theme.Accent, 0.45) : Theme.LineSoft;

            using (GraphicsPath path = Theme.Round(r, 10))
            {
                using (SolidBrush b = new SolidBrush(Fill())) g.FillPath(b, path);
                if (!Quiet) using (Pen pen = new Pen(edge, 1f)) g.DrawPath(pen, path);
            }

            // Taking the turn back into the box sits over this turn's own
            // top-right corner rather than in a toolbar: it belongs to this
            // turn, and only the newest one can be taken back.
            if (Editable)
            {
                Rectangle mark = EditMark();
                if (_overEdit)
                    using (GraphicsPath path = Theme.Round(new Rectangle(mark.X - 3, mark.Y - 3, mark.Width + 6, mark.Height + 6), 6))
                    using (SolidBrush b = new SolidBrush(Theme.Mix(Fill(), Theme.Accent, 0.16)))
                        g.FillPath(b, path);
                NsIcon.Draw(g, NsIcon.Edit, new Rectangle(mark.X + 1, mark.Y + 1, 16, 16), _overEdit ? Theme.Accent : Theme.TextDim);
            }
        }
    }

    // =========================================================================
    // The answer
    // =========================================================================
    /// <summary>
    /// The assistant's answer, formatted. A read-only RichTextBox, so the text
    /// can be selected, dragged over and copied like any text -- the thing an
    /// operator most wants out of this panel is often a transcription.
    ///
    /// No plate behind it: an answer is the page, not a speech balloon. The
    /// operator's own turn keeps its tinted bubble (NsBubble), which is what
    /// tells the two apart at a glance.
    ///
    /// While it is being written a small dot breathes at the end of the last
    /// word, where the next one will appear.
    /// </summary>
    public class NsReply : ChatItemBase
    {
        const int PadX = 2;
        const int PadY = 2;

        readonly RichTextBox _box;
        readonly Panel _dot;
        string _markdown = "";
        string _shown = null;
        bool _overCopy;
        bool _streaming;

        public bool Trouble;
        public event EventHandler CopyWanted;

        public NsReply()
        {
            Font = Theme.Ui(9f);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;

            _box = new NsRichEdit
            {
                BorderStyle = BorderStyle.None,
                ReadOnly = true,
                ScrollBars = RichTextBoxScrollBars.None,
                TabStop = false,
                DetectUrls = true,
                WordWrap = true,
                ShortcutsEnabled = true,
                Cursor = Cursors.IBeam,
                BackColor = Theme.Surface,
                Font = Theme.Ui(9.5f * ChatFx.Zoom),
            };
            HookNative(_box);
            _box.LinkClicked += delegate (object sender, LinkClickedEventArgs e) { OpenLink(e.LinkText); };
            _box.MouseMove += delegate (object sender, MouseEventArgs e)
            {
                Point at = new Point(e.X + _box.Left, e.Y + _box.Top);
                Hover(CopyMark().Contains(at));
            };
            Controls.Add(_box);

            _dot = new Panel { Size = new Size(9, 9), Visible = false, BackColor = Theme.Accent };
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(0, 0, 8.5f, 8.5f);
                _dot.Region = new Region(gp);
            }
            Controls.Add(_dot);
            _dot.BringToFront();
        }

        static void OpenLink(string url)
        {
            // Only what a browser opens. A model can write any string as a link.
            if (string.IsNullOrEmpty(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        public override bool Native { get { return true; } }
        public override string Kind { get { return "reply"; } }

        /// <summary>The answer as the model wrote it, Markdown and all.</summary>
        public string Markdown
        {
            get { return _markdown; }
            set { _markdown = value ?? ""; Render(); }
        }

        public override string Text { get { return _markdown; } set { Markdown = value; } }

        public ContextMenuStrip Menu { set { _box.ContextMenuStrip = value; } }

        // ---- selection --------------------------------------------------------
        public override string PlainText { get { return (_box.Text ?? "").Replace("\r\n", "\n"); } }
        public override string MarkdownText { get { return _markdown; } }
        public override string SelectedText { get { return _box.SelectionLength > 0 ? _box.SelectedText.Replace("\r\n", "\n") : ""; } }
        public override bool AnySelected { get { return _box.SelectionLength > 0; } }
        public override bool AllSelected { get { return _box.TextLength > 0 && _box.SelectionLength >= _box.TextLength; } }
        public override int TextLength { get { return _box.TextLength; } }
        public override void SelectAllText() { _box.SelectAll(); }
        public override void SelectRange(int from, int to) { _box.Select(Math.Max(0, from), Math.Max(0, to - from)); }
        public override void ClearSelection() { if (_box.SelectionLength > 0) _box.Select(0, 0); }
        public override int CharAt(Point screen) { return IndexAt(_box, screen); }

        // ---- the dot ------------------------------------------------------------

        /// <summary>Being written now: the dot shows, and breathes (<see cref="Breathe"/>).</summary>
        public bool Streaming
        {
            get { return _streaming; }
            set
            {
                if (_streaming == value) return;
                _streaming = value;
                _dot.Visible = value;
                PlaceDot();
            }
        }

        /// <summary>Called from the panel's clock while the answer is being written.</summary>
        public void Breathe()
        {
            if (!_streaming) return;
            float p = ChatFx.Pulse(700);
            _dot.BackColor = Theme.Mix(Theme.Surface, Theme.Accent, 0.4 + 0.6 * p);
        }

        /// <summary>Puts the dot after the last character written, wrapping to the next line where that one is full.</summary>
        void PlaceDot()
        {
            if (!_streaming || _box == null || !_box.IsHandleCreated) return;
            int n = _box.TextLength;
            int lineHeight = (int)Math.Ceiling(_box.Font.GetHeight() * 1.35);
            Point p;
            if (n == 0) p = new Point(2, 4);
            else
            {
                try
                {
                    p = _box.GetPositionFromCharIndex(n - 1);
                    char last = _box.Text[n - 1];
                    int w = char.IsControl(last) ? 0 : TextRenderer.MeasureText(last.ToString(), _box.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                    p = new Point(p.X + w + 5, p.Y + Math.Max(0, (lineHeight - 9) / 2) - 1);
                    if (char.IsControl(last)) p = new Point(4, p.Y + lineHeight);
                    if (p.X > _box.Width - 12) p = new Point(4, p.Y + lineHeight);
                }
                catch { p = new Point(2, 4); }
            }
            _dot.Location = new Point(_box.Left + p.X, _box.Top + p.Y);
            _dot.BringToFront();
        }

        public void Render()
        {
            if (_box == null) return;
            string md = _markdown.TrimEnd();
            Color text = Trouble ? Theme.Danger : Theme.Text;
            string key = md + "|" + text.ToArgb() + "|" + Theme.IsLight;
            if (key == _shown) return;
            _shown = key;
            _box.BackColor = Theme.Surface;
            Color code = Theme.Mix(Theme.Surface, Theme.Text, Theme.IsLight ? 0.07 : 0.12);
            try { _box.Rtf = ChatMarkdown.ToRtf(md, 9.5f * ChatFx.Zoom, text, Theme.TextDim, Theme.Accent, code); }
            catch { _box.Text = md; }
        }

        /// <summary>The conversation's text size changed: the same words, drawn again at the new size.</summary>
        public void ApplyZoom()
        {
            _box.Font = Theme.Ui(9.5f * ChatFx.Zoom);
            _shown = null;
            Render();
            PlaceDot();
        }

        public override int MeasureHeight(int width)
        {
            int inner = Math.Max(20, width - PadX * 2 - 22);
            if (_box.Width != inner) _box.Width = inner;
            if (_box.TextLength == 0) return _streaming ? 26 : 22;
            if (!_box.IsHandleCreated) return TextRenderer.MeasureText(_box.Text, Font, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height + 20;

            // Where the last character is, plus that line's own height: the
            // box has no "how tall is all this" of its own that can be trusted
            // before it has been drawn at this width.
            int last = _box.TextLength - 1;
            Point end = _box.GetPositionFromCharIndex(last);
            int line = LastLineHeight();
            int h = Math.Max(22, end.Y + line + PadY * 2 + 6);
            PlaceDot();
            return h;
        }

        /// <summary>
        /// The height of a line of body text. The last line of an answer is
        /// body text or code in all but freak cases, and asking the box for
        /// the font at the end would move the operator's selection.
        /// </summary>
        int LastLineHeight() { return (int)Math.Ceiling(_box.Font.GetHeight() * 1.45); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_box != null) _box.SetBounds(PadX, PadY, Math.Max(20, Width - PadX * 2 - 22), Math.Max(12, Height - PadY * 2));
            PlaceDot();
        }

        Rectangle CopyMark() { return new Rectangle(Width - 20, 2, 18, 18); }

        void Hover(bool on)
        {
            if (on == _overCopy) return;
            _overCopy = on;
            Cursor = on ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e) { Hover(CopyWanted != null && CopyMark().Contains(e.Location)); base.OnMouseMove(e); }
        protected override void OnMouseEnter(EventArgs e) { Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover(false); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_overCopy) { if (CopyWanted != null) CopyWanted(this, EventArgs.Empty); return; }
            base.OnMouseDown(e);
        }

        public void ApplyTheme() { _shown = null; Render(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            // The copy mark is always there, faint, where it can be found; it
            // takes the whole answer. A part is what selecting is for.
            if (_markdown.Length > 0 && !_streaming)
                NsIcon.Draw(g, NsIcon.Copy, CopyMark(), _overCopy ? Theme.Accent : Theme.Mix(Theme.Surface, Theme.TextFaint, 0.55));
        }
    }

    // =========================================================================
    // The thinking
    // =========================================================================
    /// <summary>
    /// What the model thought before it answered: "Thinking" with the last few
    /// lines running underneath while it happens, then "Thought for 12 s",
    /// closed, which opens to the whole of it. Painted, not a text box: it is
    /// looked at, not copied from, and a closed one must cost nothing.
    /// </summary>
    public class NsThought : ChatItemBase
    {
        const int Head = 24;
        readonly StringBuilder _text = new StringBuilder();
        public DateTime Started = DateTime.Now;
        public TimeSpan Took;
        bool _live = true;
        bool _open;

        /// <summary>Opened or closed by the operator. The transcript lays itself out again.</summary>
        public event EventHandler Toggled;

        public NsThought()
        {
            Font = Theme.Ui(8.25f);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, false);
        }

        public override string Kind { get { return "thought"; } }
        public override string PlainText { get { return _text.ToString().Trim(); } }

        public void Add(string piece) { _text.Append(piece); Invalidate(); }
        public string Thinking { get { return _text.ToString(); } set { _text.Length = 0; _text.Append(value ?? ""); Invalidate(); } }

        public bool Live
        {
            get { return _live; }
            set
            {
                if (_live && !value) Took = DateTime.Now - Started;
                _live = value;
                Invalidate();
            }
        }

        public bool Open { get { return _open; } set { _open = value; Invalidate(); } }

        string Body()
        {
            string t = _text.ToString().Trim();
            if (_open) return t;
            if (!_live) return "";
            // The tail, whole lines where there are any.
            if (t.Length > 320) { t = t.Substring(t.Length - 320); int nl = t.IndexOf(' '); if (nl > 0 && nl < 40) t = "…" + t.Substring(nl); }
            return t;
        }

        public override int MeasureHeight(int width)
        {
            string body = Body();
            if (body.Length == 0) return Head + 2;
            int inner = Math.Max(40, width - 34);
            int h = TextRenderer.MeasureText(body, Font, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            if (!_open) h = Math.Min(h, Font.Height * 4 + 2);
            return Head + h + 10;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Y <= Head + 4 || _open)
            {
                _open = !_open;
                Invalidate();
                if (Toggled != null) Toggled(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Back);
            Theme.Smooth(g);

            double seconds = (_live ? DateTime.Now - Started : Took).TotalSeconds;
            string when = seconds < 1 ? "" : seconds < 60 ? ((int)seconds) + " s" : ((int)seconds / 60) + " min " + ((int)seconds % 60) + " s";
            string title = _live ? "Thinking" + (when.Length > 0 ? " · " + when : "…") : (when.Length > 0 ? "Thought for " + when : "Thought");

            // A slow pulse on the mark while it thinks: the one thing on the
            // screen that says it has not stalled.
            float pulse = _live ? 0.55f + 0.45f * ChatFx.Pulse(900) : 1f;
            Color mark = Theme.Mix(Back, Theme.Accent, _live ? pulse : 0.8);
            NsIcon.Draw(g, NsIcon.Sparkle, new RectangleF(0, 4, 16, 16), mark);

            using (Font f = Theme.UiSemi(8.5f))
            {
                Size ts = TextRenderer.MeasureText(title, f);
                Rectangle titleBox = new Rectangle(22, 0, Width - 40, Head);
                if (_live) ChatFx.ShimmerText(g, title, f, titleBox, Theme.TextDim, Theme.Accent, 1700);
                else TextRenderer.DrawText(g, title, f, titleBox, Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                if (_text.Length > 0)
                {
                    // The chevron: right when closed, down when open.
                    float cx = 22 + ts.Width + 6, cy = Head / 2f;
                    using (Pen p = new Pen(Theme.TextFaint, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        if (_open) g.DrawLines(p, new[] { new PointF(cx, cy - 2), new PointF(cx + 4, cy + 2), new PointF(cx + 8, cy - 2) });
                        else g.DrawLines(p, new[] { new PointF(cx + 2, cy - 4), new PointF(cx + 6, cy), new PointF(cx + 2, cy + 4) });
                    }
                }
            }

            string body = Body();
            if (body.Length == 0) return;
            int top = Head + 2;
            // A rule down the left, as a quote is drawn: this is the model
            // talking to itself, not to the operator.
            using (Pen rule = new Pen(_live ? Theme.Mix(Theme.LineSoft, Theme.Accent, 0.35 * pulse) : Theme.LineSoft, 2f)) g.DrawLine(rule, 7, top, 7, Height - 6);
            var box = new Rectangle(20, top, Width - 34, Height - top - 6);
            TextFormatFlags flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            if (!_open)
            {
                // Bottom-aligned, so the newest line is the one in view.
                int full = TextRenderer.MeasureText(body, Font, new Size(box.Width, int.MaxValue), flags).Height;
                if (full > box.Height) box = new Rectangle(box.X, box.Bottom - full, box.Width, full);
                g.SetClip(new Rectangle(20, top, Width - 34, Height - top - 6));
            }
            TextRenderer.DrawText(g, body, Font, box, Theme.TextFaint, flags);
            g.ResetClip();
            if (!_open)
                using (var fade = new LinearGradientBrush(new Rectangle(0, top, Width, 14), Back, Color.FromArgb(0, Back), 90f))
                    g.FillRectangle(fade, 12, top, Width - 12, 14);
        }
    }

    // =========================================================================
    // A step
    // =========================================================================
    /// <summary>
    /// One thing the assistant did ("Read bill.docx"): a spinner while it runs,
    /// then a tick that draws itself, or a cross and the reason; and how long
    /// it took where that was more than a moment. A line, not a bubble: the
    /// work is noted along the side of the conversation, not said in it.
    /// </summary>
    public class NsStep : ChatItemBase
    {
        public enum StepState { Running, Done, Failed }

        string _label = "";
        string _detail = "";
        StepState _state = StepState.Running;
        readonly int _started = Environment.TickCount;
        int _ended;

        public NsStep()
        {
            Font = Theme.Ui(8.25f);
            SetStyle(ControlStyles.Selectable, false);
        }

        public override string Kind { get { return "step"; } }

        public override string PlainText
        {
            get { return (_state == StepState.Failed ? "✗ " : _state == StepState.Done ? "✓ " : "… ") + _label + (_detail.Length > 0 ? "\n" + _detail : ""); }
        }

        public string Label { get { return _label; } set { _label = value ?? ""; Invalidate(); } }

        /// <summary>Why it failed, under the line. Empty otherwise.</summary>
        public string Detail { get { return _detail; } set { _detail = value ?? ""; Invalidate(); } }

        public StepState State
        {
            get { return _state; }
            set
            {
                if (_state == value) return;
                if (_state == StepState.Running) _ended = Environment.TickCount;
                _state = value;
                Invalidate();
            }
        }

        /// <summary>Still drawing itself: the panel's clock keeps repainting it.</summary>
        public bool Animating
        {
            get { return _state == StepState.Running || (_ended != 0 && unchecked(Environment.TickCount - _ended) < 320); }
        }

        /// <summary>How long it ran, in seconds, once it has finished.</summary>
        public double Took { get { return _ended == 0 ? 0 : unchecked(_ended - _started) / 1000.0; } }

        public override int MeasureHeight(int width)
        {
            int h = 22;
            if (_detail.Length > 0)
                h += Math.Min(Font.Height * 3, TextRenderer.MeasureText(_detail, Font, new Size(Math.Max(40, width - 28), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height) + 2;
            return h;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Back);
            Theme.Smooth(g);

            var mark = new RectangleF(2, 4, 14, 14);
            if (_state == StepState.Running)
            {
                // A quarter-arc going round, one turn a second.
                float angle = (Environment.TickCount % 1000) * 0.36f;
                using (Pen track = new Pen(Theme.LineSoft, 2f)) g.DrawEllipse(track, mark.X + 1, mark.Y + 1, mark.Width - 2, mark.Height - 2);
                using (Pen arc = new Pen(Theme.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, mark.X + 1, mark.Y + 1, mark.Width - 2, mark.Height - 2, angle, 100);
            }
            else if (_state == StepState.Done)
            {
                // The tick draws itself: 320 ms from the first stroke to the last.
                double t = _ended == 0 ? 1 : ChatFx.EaseOut(unchecked(Environment.TickCount - _ended) / 320.0);
                PointF a = new PointF(4, 11.5f), b = new PointF(7.5f, 15), c = new PointF(14, 7.5f);
                double first = 0.35;
                var pts = new List<PointF> { a };
                if (t <= first) pts.Add(new PointF(a.X + (b.X - a.X) * (float)(t / first), a.Y + (b.Y - a.Y) * (float)(t / first)));
                else
                {
                    pts.Add(b);
                    float u = (float)((t - first) / (1 - first));
                    pts.Add(new PointF(b.X + (c.X - b.X) * u, b.Y + (c.Y - b.Y) * u));
                }
                using (Pen p = new Pen(Theme.Mix(Theme.TextFaint, Color.FromArgb(34, 170, 90), 0.85), 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    if (pts.Count >= 2) g.DrawLines(p, pts.ToArray());
            }
            else
            {
                using (Pen p = new Pen(Theme.Danger, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, 5, 7.5f, 12, 14.5f);
                    g.DrawLine(p, 12, 7.5f, 5, 14.5f);
                }
            }

            double seconds = _state == StepState.Running ? unchecked(Environment.TickCount - _started) / 1000.0 : Took;
            string took = seconds >= 2 ? (int)seconds + " s" : "";
            int right = 0;
            if (took.Length > 0)
            {
                right = TextRenderer.MeasureText(took, Font).Width + 8;
                TextRenderer.DrawText(g, took, Font, new Rectangle(Width - right, 0, right, 22), Theme.TextFaint,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
            }

            Color colour = _state == StepState.Failed ? Theme.Danger : _state == StepState.Running ? Theme.Text : Theme.TextDim;
            TextRenderer.DrawText(g, _label, Font, new Rectangle(24, 0, Math.Max(10, Width - 26 - right), 22), colour,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (_detail.Length > 0)
                TextRenderer.DrawText(g, _detail, Font, new Rectangle(24, 22, Width - 28, Height - 22), Theme.TextFaint,
                                      TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // =========================================================================
    // A list of rows to open or delete
    // =========================================================================
    /// <summary>
    /// Earlier conversations, or the facts in memory: a scrolling list of rows,
    /// each a line of title and a quieter line under it, with a bin that
    /// appears on the row under the pointer. Painted, like the model list, so a
    /// hundred rows cost one control.
    /// </summary>
    public class NsRowList : NsBase
    {
        public class Row
        {
            public string Title = "";
            public string Sub = "";
            public bool Header;
            public bool Current;
            public object Tag;
        }

        readonly List<Row> _rows = new List<Row>();
        int _hot = -1;
        bool _overBin;
        int _scroll;
        public string Empty = "Nothing here yet.";

        public event Action<object> Opened;
        public event Action<object> Deleted;

        public NsRowList()
        {
            Font = Theme.Ui(8.75f);
            SetStyle(ControlStyles.Selectable, false);
            WheelDelta += delegate (int delta) { ScrollBy(delta); };
        }

        public void SetRows(IEnumerable<Row> rows)
        {
            _rows.Clear();
            _rows.AddRange(rows);
            _scroll = 0;
            _hot = -1;
            Invalidate();
        }

        static int HeightOf(Row r) { return r.Header ? 26 : (r.Sub.Length > 0 ? 44 : 32); }

        int Total() { int n = 4; foreach (Row r in _rows) n += HeightOf(r); return n; }

        int RowAt(int y)
        {
            int at = 2 - _scroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                int h = HeightOf(_rows[i]);
                if (y >= at && y < at + h) return _rows[i].Header ? -1 : i;
                at += h;
            }
            return -1;
        }

        int TopOf(int index)
        {
            int at = 2 - _scroll;
            for (int i = 0; i < index; i++) at += HeightOf(_rows[i]);
            return at;
        }

        Rectangle BinOf(int index) { return new Rectangle(Width - 34, TopOf(index) + (HeightOf(_rows[index]) - 22) / 2, 22, 22); }

        void ScrollBy(int delta)
        {
            int most = Math.Max(0, Total() - Height);
            _scroll = Math.Max(0, Math.Min(most, _scroll - delta / 3));
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A)
            {
                int delta = unchecked((short)((long)m.WParam >> 16));
                if (EatWheel(delta)) return;
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            bool bin = i >= 0 && Deleted != null && BinOf(i).Contains(e.Location);
            if (i != _hot || bin != _overBin) { _hot = i; _overBin = bin; Invalidate(); }
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; _overBin = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = RowAt(e.Y);
            if (i >= 0)
            {
                object tag = _rows[i].Tag;
                if (Deleted != null && BinOf(i).Contains(e.Location)) Deleted(tag);
                else if (Opened != null) Opened(tag);
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);

            if (_rows.Count == 0)
            {
                TextRenderer.DrawText(g, Empty, Font, new Rectangle(0, 20, Width, 40), Theme.TextFaint,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return;
            }

            int y = 2 - _scroll;
            for (int i = 0; i < _rows.Count; i++)
            {
                Row r = _rows[i];
                int h = HeightOf(r);
                if (y + h > 0 && y < Height)
                {
                    if (r.Header)
                    {
                        using (Font f = Theme.UiSemi(7.25f))
                            TextRenderer.DrawText(g, r.Title.ToUpperInvariant(), f, new Rectangle(10, y + 6, Width - 20, h - 6),
                                                  Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                    }
                    else
                    {
                        if (i == _hot || r.Current)
                            using (GraphicsPath path = Theme.Round(new Rectangle(4, y + 1, Width - 8, h - 2), 7))
                            using (SolidBrush b = new SolidBrush(r.Current && i != _hot ? Theme.Mix(Theme.Surface, Theme.Accent, 0.12) : Theme.Hover))
                                g.FillPath(b, path);

                        int room = Width - 24 - (i == _hot && Deleted != null ? 30 : 0);
                        using (Font f = Theme.UiSemi(8.75f))
                            TextRenderer.DrawText(g, r.Title, f, new Rectangle(12, y + (r.Sub.Length > 0 ? 5 : 0), room, r.Sub.Length > 0 ? 20 : h),
                                                  Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                        if (r.Sub.Length > 0)
                            using (Font f = Theme.Ui(7.75f))
                                TextRenderer.DrawText(g, r.Sub, f, new Rectangle(12, y + 23, room, 16), Theme.TextFaint,
                                                      TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                        if (i == _hot && Deleted != null)
                            NsIcon.Draw(g, NsIcon.Trash, BinOf(i), _overBin ? Theme.Danger : Theme.TextFaint);
                    }
                }
                y += h;
            }

            int total = Total();
            if (total > Height)
            {
                int thumb = Math.Max(24, Height * Height / total);
                int top = (Height - thumb) * _scroll / Math.Max(1, total - Height);
                using (SolidBrush b = new SolidBrush(Theme.Line)) g.FillRectangle(b, Width - 4, top + 2, 3, thumb - 4);
            }
        }
    }

    // =========================================================================
    // What it is doing now
    // =========================================================================
    /// <summary>
    /// The line at the foot of a turn while it runs: three dots and what is
    /// happening -- "Thinking", "Writing", "Reading the document" -- with a
    /// band of light travelling over the words and how long it has been. The
    /// answer to "is it still doing something".
    /// </summary>
    public class NsWorking : ChatItemBase
    {
        string _what = "Thinking";
        public DateTime Started = DateTime.Now;

        public NsWorking()
        {
            Font = Theme.Ui(8.25f);
            SetStyle(ControlStyles.Selectable, false);
        }

        public override bool Selectable { get { return false; } }
        public override string Kind { get { return "working"; } }

        public string What { get { return _what; } set { _what = value ?? ""; Invalidate(); } }

        public override int MeasureHeight(int width) { return 26; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);

            double phase = (Environment.TickCount % 1200) / 1200.0;
            for (int i = 0; i < 3; i++)
            {
                double at = phase - i * 0.16;
                if (at < 0) at += 1.0;
                double lift = Math.Max(0, Math.Sin(at * Math.PI * 2));
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Accent, 0.35 + 0.65 * lift)))
                    g.FillEllipse(b, 3 + i * 9, 10 - (float)(lift * 2.5), 6, 6);
            }

            int seconds = (int)(DateTime.Now - Started).TotalSeconds;
            string text = _what + (seconds >= 2 ? "  ·  " + seconds + " s" : "");
            ChatFx.ShimmerText(g, text, Font, new Rectangle(34, 0, Width - 36, Height), Theme.TextDim, Theme.Text, 1600);
        }
    }

    // =========================================================================
    // What to ask next
    // =========================================================================
    /// <summary>
    /// A few things worth asking after an answer, as chips under it: the same
    /// three in every shop -- put it in Word, translate it, say it shorter.
    /// Each is a whole question that is sent as it stands, so pressing one is
    /// the same as typing it. Only the newest answer has them.
    /// </summary>
    public class NsFollowUps : ChatItemBase
    {
        public class Chip
        {
            public string Text = "";
            public string Prompt = "";
        }

        public readonly List<Chip> Chips = new List<Chip>();

        /// <summary>A chip was pressed: what it says, and the whole question it stands for.</summary>
        public event Action<string, string> Chosen;

        readonly List<Rectangle> _rects = new List<Rectangle>();
        int _hot = -1;

        public NsFollowUps()
        {
            Font = Theme.Ui(8.25f);
            SetStyle(ControlStyles.Selectable, false);
        }

        public override bool Selectable { get { return false; } }
        public override string Kind { get { return "followups"; } }

        /// <summary>Lays the chips out left to right, wrapping, and says how tall that comes to.</summary>
        int Flow(int width)
        {
            _rects.Clear();
            int x = 0, y = 2, row = 26;
            foreach (Chip chip in Chips)
            {
                int w = TextRenderer.MeasureText(chip.Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 24;
                if (x > 0 && x + w > width) { x = 0; y += row + 6; }
                _rects.Add(new Rectangle(x, y, w, row));
                x += w + 6;
            }
            return y + row + 4;
        }

        public override int MeasureHeight(int width) { return Flow(width); }

        protected override void OnSizeChanged(EventArgs e) { Flow(Width); base.OnSizeChanged(e); }

        int HitTest(Point p)
        {
            for (int i = 0; i < _rects.Count; i++) if (_rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != _hot) { _hot = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hot != -1) { _hot = -1; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i >= 0 && Chosen != null) Chosen(Chips[i].Text, Chips[i].Prompt);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);
            if (_rects.Count != Chips.Count) Flow(Width);

            for (int i = 0; i < Chips.Count && i < _rects.Count; i++)
            {
                Rectangle r = _rects[i];
                bool hot = i == _hot;
                using (GraphicsPath path = Theme.Round(r, 13))
                {
                    if (hot) using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Accent, Theme.IsLight ? 0.10 : 0.20))) g.FillPath(b, path);
                    using (Pen p = new Pen(hot ? Theme.Accent : Theme.Line, 1f)) g.DrawPath(p, path);
                }
                TextRenderer.DrawText(g, Chips[i].Text, Font, r, hot ? Theme.Accent : Theme.TextDim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }
    }

    // =========================================================================
    // Under the answer
    // =========================================================================
    /// <summary>
    /// The row under the newest answer: what answered it and how long it took
    /// on the left, and on the right what can be done with it -- copy it, ask
    /// again, copy or save the whole conversation. Only the newest answer has
    /// one; a conversation with a row under every reply is a page of toolbars.
    /// </summary>
    public class NsActions : ChatItemBase
    {
        public string Info = "";
        public bool CanRetry = true;

        public event Action CopyClicked;
        public event Action RetryClicked;
        public event Action MoreClicked;

        int _hot = -1;
        static readonly string[] Names = { "Copy this answer", "Try again", "Copy or save the conversation" };

        public NsActions()
        {
            Font = Theme.Ui(7.75f);
            SetStyle(ControlStyles.Selectable, false);
            Cursor = Cursors.Default;
        }

        public override bool Selectable { get { return false; } }
        public override string Kind { get { return "actions"; } }
        public override int MeasureHeight(int width) { return 30; }

        Rectangle Button(int i) { return new Rectangle(Width - (3 - i) * 28 - 2, 3, 26, 24); }

        int HitTest(Point p)
        {
            for (int i = 0; i < 3; i++) if (Button(i).Contains(p) && (i != 1 || CanRetry)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != _hot) { _hot = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { if (_hot != -1) { _hot = -1; Invalidate(); } base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i == 0 && CopyClicked != null) CopyClicked();
            else if (i == 1 && RetryClicked != null) RetryClicked();
            else if (i == 2 && MoreClicked != null) MoreClicked();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Smooth(g);

            string left = _hot >= 0 ? Names[_hot] : Info;
            TextRenderer.DrawText(g, left, Font, new Rectangle(2, 0, Math.Max(10, Width - 92), Height),
                                  _hot >= 0 ? Theme.TextDim : Theme.TextFaint,
                                  TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            string[] icons = { NsIcon.Copy, NsIcon.Retry, NsIcon.Save };
            for (int i = 0; i < 3; i++)
            {
                if (i == 1 && !CanRetry) continue;
                Rectangle r = Button(i);
                if (i == _hot)
                    using (GraphicsPath path = Theme.Round(r, 6))
                    using (SolidBrush b = new SolidBrush(Theme.Hover))
                        g.FillPath(b, path);
                NsIcon.Draw(g, icons[i], new Rectangle(r.X + 5, r.Y + 4, 16, 16), i == _hot ? Theme.Accent : Theme.TextFaint);
            }
        }
    }
}
