// =============================================================================
// NextScan Studio - what to do with the page you just scanned
// Plan ref: docs/AI_LAYER.md
//
// Two small things at the edges of the preview, never over the page:
//
//   at the top left   a pill with the document's own name -- "Recognising this
//                     page..." while it is being looked at, then the name, and a
//                     small x to close the lot;
//   at the right      a column of small round colourful icons -- Word, Excel,
//                     PDF, Text, Translate, Handwriting -- that pop in one after
//                     another, each naming itself when the pointer is on it.
//
// The canvas keeps a band for them (CanvasView.TopBand / RightBand), so the page is
// fitted in what is left and nothing is ever drawn over it.
//
// Nothing here asks a model anything: it offers; pressing an icon does the work.
// Painted on the canvas, not a control of its own, so edges and shadows are smooth.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NextScan.App
{
    /// <summary>One thing that is offered.</summary>
    public class SuggestAction
    {
        public string Id = "";
        public string Label = "";
        public string Tip = "";
        public Color From, To;
    }

    public class PageSuggestOverlay
    {
        readonly Control _host;
        readonly Timer _tick = new Timer { Interval = 15 };

        /// <summary>The space the canvas keeps free for it at the top and at the right, while it shows.</summary>
        public const int TopBandHeight = 36, RightBandWidth = 58;

        const int Icon = 34, Step = 42;

        public string Title = "", Subtitle = "";
        public bool Working, Failed;
        public readonly List<SuggestAction> Actions = new List<SuggestAction>();
        public Color BarFrom = Color.FromArgb(0x3B, 0x82, 0xF6), BarTo = Color.FromArgb(0x63, 0x66, 0xF1);

        public event Action<string> Chosen;
        public event Action Dismissed;
        public event Action Retry;

        bool _shown;
        DateTime _appearedAt;
        readonly Dictionary<string, DateTime> _born = new Dictionary<string, DateTime>();
        int _hot = -1;
        bool _hotClose, _hotPill;
        Rectangle _pill, _closeRect;
        readonly List<Rectangle> _icons = new List<Rectangle>();

        public PageSuggestOverlay(Control host)
        {
            _host = host;
            _tick.Tick += delegate
            {
                if (!Animating()) _tick.Stop();
                _host.Invalidate();
            };
        }

        public bool Visible { get { return _shown; } }

        bool Animating()
        {
            if (!_shown) return false;
            if (Working) return true;
            if ((DateTime.Now - _appearedAt).TotalMilliseconds < 400) return true;
            foreach (DateTime born in _born.Values) if ((DateTime.Now - born).TotalMilliseconds < 520) return true;
            return false;
        }

        // =====================================================================
        // What it shows
        // =====================================================================

        /// <summary>Shows it (sliding in if it was not there) or changes what it says. New icons pop in.</summary>
        public void Present(string title, string subtitle, bool working, bool failed, IList<SuggestAction> actions, Color barFrom, Color barTo)
        {
            bool layoutChanges = !_shown;
            Title = title ?? "";
            Subtitle = subtitle ?? "";
            Working = working;
            Failed = failed;
            BarFrom = barFrom; BarTo = barTo;

            var keep = new HashSet<string>();
            foreach (SuggestAction a in actions) keep.Add(a.Id);
            foreach (string gone in new List<string>(_born.Keys)) if (!keep.Contains(gone)) _born.Remove(gone);
            int order = 0;
            foreach (SuggestAction a in actions)
            {
                // The first showing pops the icons in one after another; a later change pops only what is new.
                if (!_born.ContainsKey(a.Id)) _born[a.Id] = DateTime.Now.AddMilliseconds((_shown ? 0 : 140) + order * 55);
                order++;
            }
            Actions.Clear();
            Actions.AddRange(actions);

            if (!_shown) { _shown = true; _appearedAt = DateTime.Now; }
            _tick.Start();
            _host.Invalidate();
            if (layoutChanges) _host.Invalidate(true);
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            _tick.Stop();
            _hot = -1; _hotClose = _hotPill = false;
            _host.Invalidate();
        }

        // =====================================================================
        // Painting
        // =====================================================================

        public void Paint(Graphics g, Rectangle client, int top)
        {
            if (!_shown) return;
            double ease = ChatFx.EaseOut(Math.Min(1, (DateTime.Now - _appearedAt).TotalMilliseconds / 320.0));

            PaintPill(g, client, top, ease);
            PaintIcons(g, client, top);
        }

        void PaintPill(Graphics g, Rectangle client, int top, double ease)
        {
            string text = Working ? (Title.Length > 0 && Title != "Preview" && !Title.StartsWith("Page ", StringComparison.Ordinal) ? Title : "Recognising this page") : Title.Length > 0 ? Title : "This page";
            string tail = Working || Failed ? "" : Subtitle;

            using (Font bold = Theme.UiSemi(8.75f))
            using (Font light = Theme.Ui(8f))
            {
                int closeW = 24;
                int maxW = Math.Max(120, Math.Min(460, client.Width - RightBandWidth - 40));
                int textW = TextRenderer.MeasureText(text, bold, new Size(1000, 20), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                int tailW = tail.Length > 0 ? TextRenderer.MeasureText("  " + tail, light, new Size(1000, 20), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width : 0;
                int w = Math.Min(maxW, 12 + 14 + 6 + textW + tailW + 8 + closeW);
                int slide = (int)Math.Round((1 - ease) * 26);
                _pill = new Rectangle(client.Left + 14 - slide, client.Top + top + 4, w, 28);

                // A soft shadow, then the pill.
                for (int i = 4; i >= 1; i--)
                {
                    var s = new Rectangle(_pill.X - i, _pill.Y - i + 2, _pill.Width + i * 2, _pill.Height + i * 2);
                    using (GraphicsPath sp = Theme.Round(s, 14 + i))
                    using (var b = new SolidBrush(Color.FromArgb(Math.Max(2, 16 - i * 3), 0, 0, 0))) g.FillPath(b, sp);
                }
                using (GraphicsPath path = Theme.Round(_pill, 14))
                {
                    using (var fill = new SolidBrush(Theme.Raised)) g.FillPath(fill, path);
                    using (var border = new Pen(Failed && _hotPill ? BarTo : Theme.LineSoft, 1f)) g.DrawPath(border, path);
                }

                // The dot in the document's colour, or a pulse while it is being looked at.
                var dot = new Rectangle(_pill.X + 11, _pill.Y + 9, 10, 10);
                if (Working)
                {
                    float pulse = ChatFx.Pulse(900);
                    int grow = (int)Math.Round(pulse * 3);
                    using (var halo = new SolidBrush(Color.FromArgb(60, BarFrom))) g.FillEllipse(halo, dot.X - grow, dot.Y - grow, dot.Width + grow * 2, dot.Height + grow * 2);
                }
                using (var gradient = new LinearGradientBrush(dot, BarFrom, BarTo, LinearGradientMode.ForwardDiagonal)) g.FillEllipse(gradient, dot);
                if (Failed) using (var pen = new Pen(Color.White, 1.6f)) { g.DrawLine(pen, dot.X + 3, dot.Y + 3, dot.Right - 3, dot.Bottom - 3); g.DrawLine(pen, dot.Right - 3, dot.Y + 3, dot.X + 3, dot.Bottom - 3); }

                var area = new Rectangle(_pill.X + 28, _pill.Y, _pill.Width - 28 - closeW - 4, _pill.Height);
                if (Working) ChatFx.ShimmerText(g, text, bold, area, Theme.TextDim, Theme.Text, 1400);
                else if (Failed)
                    TextRenderer.DrawText(g, Subtitle.Length > 0 ? Subtitle : "Could not tell what it is  ·  press to try again", light, area, _hotPill ? Theme.Text : Theme.TextDim,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                else
                {
                    TextRenderer.DrawText(g, text, bold, area, Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    if (tail.Length > 0 && textW + 20 < area.Width)
                        TextRenderer.DrawText(g, "  " + tail, light, new Rectangle(area.X + textW, area.Y, area.Width - textW, area.Height), Theme.TextFaint,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }

                // The close mark.
                _closeRect = new Rectangle(_pill.Right - closeW - 2, _pill.Y + 2, closeW, _pill.Height - 4);
                if (_hotClose) using (var b = new SolidBrush(Theme.Hover)) g.FillEllipse(b, new Rectangle(_closeRect.X + 2, _closeRect.Y + 1, 22, 22));
                using (var pen = new Pen(_hotClose ? Theme.Text : Theme.TextFaint, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    int cx = _closeRect.X + 13, cy = _closeRect.Y + _closeRect.Height / 2;
                    g.DrawLine(pen, cx - 3, cy - 3, cx + 3, cy + 3);
                    g.DrawLine(pen, cx + 3, cy - 3, cx - 3, cy + 3);
                }
            }
        }

        void PaintIcons(Graphics g, Rectangle client, int top)
        {
            _icons.Clear();
            if (client.Width < 360) return;
            int x = client.Right - RightBandWidth + (RightBandWidth - Icon) / 2 - 2;
            for (int i = 0; i < Actions.Count; i++)
            {
                var cell = new Rectangle(x, client.Top + top + 4 + i * Step, Icon, Icon);
                _icons.Add(cell);
                DrawIcon(g, Actions[i], cell, i == _hot);
            }
            if (_hot >= 0 && _hot < Actions.Count) DrawLabel(g, Actions[_hot], _icons[_hot]);
        }

        void DrawIcon(Graphics g, SuggestAction a, Rectangle cell, bool hot)
        {
            DateTime born;
            double age = _born.TryGetValue(a.Id, out born) ? (DateTime.Now - born).TotalMilliseconds : 1000;
            if (age < 0) return;                          // not yet its turn
            double grow = age >= 360 ? 1 : 0.4 + 0.6 * Back(age / 360.0);
            if (hot) grow *= 1.12;

            int size = (int)Math.Round(Icon * grow);
            var icon = new Rectangle(cell.X + (cell.Width - size) / 2, cell.Y + (cell.Height - size) / 2, size, size);

            // The shadow, then the colour.
            for (int i = 4; i >= 1; i--)
            {
                var s = new Rectangle(icon.X - i + 1, icon.Y - i + 3, icon.Width + i * 2 - 2, icon.Height + i * 2 - 2);
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(2, (hot ? 26 : 16) - i * 4), 0, 0, 0))) g.FillEllipse(b, s);
            }
            using (var gradient = new LinearGradientBrush(icon, a.From, a.To, LinearGradientMode.ForwardDiagonal)) g.FillEllipse(gradient, icon);
            using (GraphicsPath circle = new GraphicsPath())
            {
                circle.AddEllipse(icon);
                GraphicsState state = g.Save();
                g.SetClip(circle, CombineMode.Intersect);
                using (var shine = new LinearGradientBrush(icon, Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(shine, icon.X, icon.Y, icon.Width, icon.Height / 2);
                g.Restore(state);
            }
            DrawGlyph(g, a.Id, icon);
        }

        /// <summary>The icon's name, in a small dark label to its left, while the pointer is on it.</summary>
        void DrawLabel(Graphics g, SuggestAction a, Rectangle icon)
        {
            string text = a.Tip.Length > 0 ? a.Tip : a.Label;
            using (Font f = Theme.UiSemi(8f))
            {
                int w = TextRenderer.MeasureText(text, f, new Size(400, 20), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + 20;
                var r = new Rectangle(icon.X - 10 - w, icon.Y + (icon.Height - 24) / 2, w, 24);
                using (GraphicsPath path = Theme.Round(r, 12))
                using (var b = new SolidBrush(Color.FromArgb(232, 28, 32, 40))) g.FillPath(b, path);
                TextRenderer.DrawText(g, text, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        /// <summary>Overshoots a little and settles: 0..1 in, a little over 1, back to 1.</summary>
        static double Back(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            const double s = 1.7;
            double u = t - 1;
            return 1 + (s + 1) * u * u * u + s * u * u;
        }

        static void DrawGlyph(Graphics g, string id, Rectangle icon)
        {
            string text, face = "Segoe UI";
            float size = icon.Height * 0.46f;
            switch (id)
            {
                case "word": text = "W"; size = icon.Height * 0.54f; break;
                case "excel": text = "X"; size = icon.Height * 0.54f; break;
                case "pdf": text = "PDF"; size = icon.Height * 0.29f; break;
                case "read": text = "Aa"; size = icon.Height * 0.42f; break;
                case "translate": text = "Aঅ"; face = "Nirmala UI"; size = icon.Height * 0.38f; break;
                case "type": DrawPen(g, icon); return;
                default: text = "•"; break;
            }
            using (var f = new Font(face, Math.Max(6f, size), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, text, f, icon, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        /// <summary>A pen at a slant, with a line written under it: for handwriting.</summary>
        static void DrawPen(Graphics g, Rectangle icon)
        {
            float u = icon.Width / 44f;
            float cx = icon.X + icon.Width / 2f, cy = icon.Y + icon.Height / 2f;
            var body = new[]
            {
                new PointF(cx + 11 * u, cy - 12 * u), new PointF(cx + 16 * u, cy - 7 * u),
                new PointF(cx - 6 * u, cy + 12 * u), new PointF(cx - 13 * u, cy + 13 * u), new PointF(cx - 12 * u, cy + 6 * u),
            };
            using (var white = new SolidBrush(Color.White)) g.FillPolygon(white, body);
            using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255), 2.2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawBezier(pen, cx - 16 * u, cy + 15 * u, cx - 8 * u, cy + 19 * u, cx + 2 * u, cy + 11 * u, cx + 14 * u, cy + 16 * u);
        }

        // =====================================================================
        // The pointer
        // =====================================================================

        /// <summary>True when the point is over the pill or an icon (so the page under it does not get the pointer too).</summary>
        public bool MouseMove(Point p)
        {
            if (!_shown) return false;
            int icon = -1;
            for (int i = 0; i < _icons.Count; i++) if (Rectangle.Inflate(_icons[i], 3, 3).Contains(p)) icon = i;
            bool close = _closeRect.Contains(p), pill = _pill.Contains(p);
            if (icon != _hot || close != _hotClose || pill != _hotPill)
            {
                _hot = icon; _hotClose = close; _hotPill = pill;
                _host.Invalidate();
            }
            bool over = icon >= 0 || pill;
            if (over) _host.Cursor = icon >= 0 || close || (Failed && pill) ? Cursors.Hand : Cursors.Default;
            return over;
        }

        public void MouseLeave()
        {
            if (_hot < 0 && !_hotClose && !_hotPill) return;
            _hot = -1; _hotClose = _hotPill = false;
            _host.Invalidate();
        }

        /// <summary>True when the press was on the pill or an icon and has been dealt with.</summary>
        public bool MouseDown(Point p, MouseButtons button)
        {
            if (!_shown) return false;
            int icon = -1;
            for (int i = 0; i < _icons.Count; i++) if (Rectangle.Inflate(_icons[i], 3, 3).Contains(p)) icon = i;
            bool over = icon >= 0 || _pill.Contains(p);
            if (!over) return false;
            if (button != MouseButtons.Left) return true;
            if (_closeRect.Contains(p)) { Hide(); if (Dismissed != null) Dismissed(); return true; }
            if (icon >= 0 && icon < Actions.Count) { if (Chosen != null) Chosen(Actions[icon].Id); return true; }
            if (Failed && _pill.Contains(p)) { if (Retry != null) Retry(); return true; }
            return true;
        }

        public void Dispose() { _tick.Stop(); _tick.Dispose(); }
    }
}
