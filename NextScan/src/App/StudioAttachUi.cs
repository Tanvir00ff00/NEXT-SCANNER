// =============================================================================
// NextScan Studio - how attachments are drawn
// Plan ref: docs/AI_LAYER.md
//
// One tile per attachment, the same in the box it waits in and in the
// conversation it was sent in: a picture is its own thumbnail; anything else is
// a coloured mark for what kind it is, its name and what it is. While it is
// being read the mark spins; if it could not be read the tile says so in red.
// Tiles flow left to right and wrap, and when there are more than fit a last
// tile says how many more.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using NextScan.Ai;

namespace NextScan.App
{
    public static class AttachTiles
    {
        public const int Tile = 52;
        public const int Gap = 6;
        public const int FileWidth = 168;

        public class Layout
        {
            public readonly List<Rectangle> Rects = new List<Rectangle>();
            public int Height;

            /// <summary>The tile that stands for the ones that did not fit, or -1.</summary>
            public int MoreIndex = -1;
            public int MoreCount;
        }

        /// <summary>Lays the tiles out from <paramref name="left"/> across <paramref name="width"/>, at most <paramref name="maxRows"/> rows.</summary>
        public static Layout Flow(List<ChatAttachment> list, int left, int width, int maxRows)
        {
            var layout = new Layout();
            int x = left, y = 0, row = 0;
            int right = left + Math.Max(Tile, width);
            for (int i = 0; i < list.Count; i++)
            {
                ChatAttachment a = list[i];
                // A document tile is as wide as it likes when there is room and half the row when there is not,
                // so two sit side by side in a panel this narrow.
                int w = a.Kind == "picture" && a.State != AttachState.Failed ? Tile
                      : Math.Min(FileWidth, Math.Max(96, Math.Min(right - left, (right - left - Gap) / 2)));
                if (x > left && x + w > right) { x = left; y += Tile + Gap; row++; }
                if (row >= maxRows)
                {
                    // Everything from here on is folded into the last tile shown.
                    int last = layout.Rects.Count - 1;
                    layout.MoreIndex = last;
                    layout.MoreCount = list.Count - last;
                    for (int k = i; k < list.Count; k++) layout.Rects.Add(Rectangle.Empty);
                    break;
                }
                layout.Rects.Add(new Rectangle(x, y, w, Tile));
                x += w + Gap;
            }
            if (layout.MoreIndex >= 0)
            {
                Rectangle r = layout.Rects[layout.MoreIndex];
                layout.Rects[layout.MoreIndex] = new Rectangle(r.X, r.Y, Tile, Tile);
            }
            layout.Height = layout.Rects.Count == 0 ? 0 : (Math.Min(row, maxRows - 1) + 1) * (Tile + Gap) - Gap;
            return layout;
        }

        public static Rectangle RemoveMark(Rectangle tile) { return new Rectangle(tile.Right - 18, tile.Top + 4, 14, 14); }

        static Color KindColour(string kind)
        {
            switch (kind)
            {
                case "word": return Color.FromArgb(43, 87, 154);
                case "sheet": return Color.FromArgb(33, 115, 70);
                case "slides": return Color.FromArgb(210, 71, 38);
                case "pdf": return Color.FromArgb(217, 48, 37);
                case "archive": return Color.FromArgb(183, 121, 31);
                case "picture": return Color.FromArgb(14, 165, 233);
                default: return Color.FromArgb(107, 114, 128);
            }
        }

        static string KindLetter(string kind)
        {
            switch (kind)
            {
                case "word": return "W";
                case "sheet": return "X";
                case "slides": return "P";
                case "pdf": return "PDF";
                case "text": return "T";
                case "archive": return "Z";
                case "picture": return "IMG";
                default: return "?";
            }
        }

        /// <summary>Draws one tile. <paramref name="removable"/> adds the cross that shows on the tile under the pointer.</summary>
        public static void Paint(Graphics g, Rectangle r, Color surface, ChatAttachment a, bool hot, bool removable)
        {
            Theme.Smooth(g);
            bool failed = a.State == AttachState.Failed;
            bool picture = a.Kind == "picture" && a.Thumb != null && a.State == AttachState.Ready;

            using (GraphicsPath path = Theme.Round(r, 9))
            {
                if (picture)
                {
                    GraphicsState state = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    g.DrawImage(a.Thumb, r);
                    g.Restore(state);
                    using (Pen p = new Pen(Theme.Mix(surface, Theme.Line, 1.0), 1f)) g.DrawPath(p, path);
                }
                else
                {
                    Color fill = failed ? Theme.Mix(surface, Theme.Danger, 0.08) : Theme.Mix(surface, Theme.Text, Theme.IsLight ? 0.035 : 0.07);
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (Pen p = new Pen(failed ? Theme.Danger : Theme.Line, 1f)) g.DrawPath(p, path);

                    // The mark: what kind it is, or a spinner while it is read.
                    var mark = new Rectangle(r.X + 7, r.Y + (r.Height - 32) / 2, 32, 32);
                    using (GraphicsPath mp = Theme.Round(mark, 8))
                    using (SolidBrush b = new SolidBrush(failed ? Theme.Danger : KindColour(a.Kind))) g.FillPath(b, mp);
                    if (a.State == AttachState.Reading)
                    {
                        float angle = (Environment.TickCount % 900) * 0.4f;
                        using (Pen arc = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawArc(arc, mark.X + 8, mark.Y + 8, 16, 16, angle, 110);
                    }
                    else
                    {
                        string letter = failed ? "!" : KindLetter(a.Kind);
                        using (Font f = Theme.UiSemi(letter.Length > 1 ? 7.5f : 10.5f))
                            TextRenderer.DrawText(g, letter, f, mark, Color.White,
                                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }

                    int textLeft = mark.Right + 7;
                    // The room for the cross is kept only while the cross is there.
                    int room = Math.Max(10, r.Right - textLeft - (removable && (hot || failed) ? 22 : 7));
                    using (Font f = Theme.UiSemi(8.25f))
                        TextRenderer.DrawText(g, a.Name, f, new Rectangle(textLeft, r.Y + 8, room, 18), Theme.Text,
                                              TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                    string sub = a.State == AttachState.Reading ? "Reading…" : failed ? a.Note : a.Short;
                    using (Font f = Theme.Ui(7.5f))
                        TextRenderer.DrawText(g, sub, f, new Rectangle(textLeft, r.Y + 27, room, 16), failed ? Theme.Danger : Theme.TextFaint,
                                              TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                }
            }

            // A picture still being read: a wash over the tile and the spinner.
            if (a.Kind == "picture" && a.State == AttachState.Reading)
            {
                using (GraphicsPath path = Theme.Round(r, 9))
                using (SolidBrush b = new SolidBrush(Theme.Mix(surface, Theme.Text, 0.06))) g.FillPath(b, path);
                float angle = (Environment.TickCount % 900) * 0.4f;
                using (Pen arc = new Pen(Theme.Accent, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, r.X + 17, r.Y + 17, 18, 18, angle, 110);
            }

            if (removable && (hot || failed))
            {
                Rectangle x = RemoveMark(r);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(hot ? 235 : 200, Theme.IsLight ? Color.FromArgb(40, 40, 50) : Color.FromArgb(230, 230, 235))))
                    g.FillEllipse(b, x);
                using (Pen p = new Pen(Theme.IsLight ? Color.White : Color.FromArgb(40, 40, 50), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, x.X + 4, x.Y + 4, x.Right - 4, x.Bottom - 4);
                    g.DrawLine(p, x.Right - 4, x.Y + 4, x.X + 4, x.Bottom - 4);
                }
            }
        }

        /// <summary>The tile that stands for the ones that did not fit: "+3".</summary>
        public static void PaintMore(Graphics g, Rectangle r, Color surface, int count, bool hot)
        {
            Theme.Smooth(g);
            using (GraphicsPath path = Theme.Round(r, 9))
            {
                using (SolidBrush b = new SolidBrush(hot ? Theme.Hover : Theme.Mix(surface, Theme.Text, 0.05))) g.FillPath(b, path);
                using (Pen p = new Pen(Theme.Line, 1f)) g.DrawPath(p, path);
            }
            using (Font f = Theme.UiSemi(10f))
                TextRenderer.DrawText(g, "+" + count, f, r, Theme.TextDim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    // =========================================================================
    // In the conversation
    // =========================================================================
    /// <summary>
    /// What was attached to a question, above it: the same tiles, without the
    /// crosses. A picture opens larger when it is pressed; a document opens in
    /// the workspace.
    /// </summary>
    public class NsAttachRow : ChatItemBase
    {
        readonly List<ChatAttachment> _items;
        AttachTiles.Layout _layout = new AttachTiles.Layout();
        int _hot = -1;

        public event Action<ChatAttachment> Opened;

        /// <summary>Said under the pointer: what the tile is.</summary>
        public event Action<string> Hint;

        public NsAttachRow(List<ChatAttachment> items)
        {
            _items = items;
            SetStyle(ControlStyles.Selectable, false);
        }

        public List<ChatAttachment> Items { get { return _items; } }

        protected override void Dispose(bool disposing)
        {
            // The pictures on the tiles go with the row, unless the tiles were
            // handed back to the box (Release).
            if (disposing) foreach (ChatAttachment a in _items) a.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>The tiles go to something else (the box, taking them back); they are not disposed with this row.</summary>
        public List<ChatAttachment> Release()
        {
            foreach (ChatAttachment a in _items) a.Released = true;
            return _items;
        }

        public override bool Selectable { get { return false; } }
        public override string Kind { get { return "attach"; } }

        int Indent { get { return Math.Min(46, Width / 5); } }

        public override int MeasureHeight(int width)
        {
            _layout = AttachTiles.Flow(_items, Math.Min(46, width / 5), width - Math.Min(46, width / 5), 12);
            return _layout.Height + 4;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            _layout = AttachTiles.Flow(_items, Indent, Width - Indent, 12);
            base.OnSizeChanged(e);
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < _layout.Rects.Count; i++) if (_layout.Rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i != _hot)
            {
                _hot = i;
                bool live = i >= 0 && i != _layout.MoreIndex;
                Cursor = live && (_items[i].Kind == "picture" || _items[i].Path.Length > 0) ? Cursors.Hand : Cursors.Default;
                if (Hint != null) Hint(live ? _items[i].Name + " — " + _items[i].Summary + (_items[i].Note.Length > 0 ? ". " + _items[i].Note : "") : "");
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hot != -1) { _hot = -1; Invalidate(); if (Hint != null) Hint(""); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int i = HitTest(e.Location);
            if (i >= 0 && i != _layout.MoreIndex && Opened != null) Opened(_items[i]);
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            if (_layout.Rects.Count != _items.Count) _layout = AttachTiles.Flow(_items, Indent, Width - Indent, 12);
            for (int i = 0; i < _items.Count && i < _layout.Rects.Count; i++)
            {
                Rectangle r = _layout.Rects[i];
                if (r.IsEmpty) continue;
                if (i == _layout.MoreIndex) AttachTiles.PaintMore(g, r, Theme.Surface, _layout.MoreCount, i == _hot);
                else AttachTiles.Paint(g, r, Theme.Surface, _items[i], i == _hot, false);
            }
        }
    }

    // =========================================================================
    // A picture, larger
    // =========================================================================
    /// <summary>
    /// A picture shown large in a window of its own, to look at what was
    /// attached: a screenshot pasted in cannot be opened anywhere else. It
    /// closes on a click, on Esc, or when anything else is pressed.
    /// </summary>
    public static class PicturePreview
    {
        public static void Show(Form owner, AiPicture picture, string title)
        {
            if (picture == null || picture.Bytes == null) return;
            Image image;
            try { using (var ms = new System.IO.MemoryStream(picture.Bytes)) image = new Bitmap(Image.FromStream(ms)); }
            catch { return; }

            Screen screen = owner == null ? Screen.PrimaryScreen : Screen.FromControl(owner);
            Rectangle work = screen.WorkingArea;
            double scale = Math.Min(1.0, Math.Min((work.Width * 0.85) / image.Width, (work.Height * 0.85) / image.Height));
            var size = new Size(Math.Max(120, (int)(image.Width * scale)), Math.Max(80, (int)(image.Height * scale)));

            var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                KeyPreview = true,
                BackColor = Color.Black,
                Size = new Size(size.Width + 2, size.Height + 2),
                Text = title ?? "",
            };
            form.Location = new Point(work.X + (work.Width - form.Width) / 2, work.Y + (work.Height - form.Height) / 2);
            var box = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Cursor = Cursors.Hand };
            box.Padding = new Padding(1);
            form.Controls.Add(box);
            box.Click += delegate { form.Close(); };
            form.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) form.Close(); };
            bool active = false;
            form.Activated += delegate { active = true; };
            form.Deactivate += delegate { if (active) form.Close(); };
            form.FormClosed += delegate { box.Image = null; image.Dispose(); form.Dispose(); };
            form.Show(owner);
            form.Activate();
        }
    }
}
