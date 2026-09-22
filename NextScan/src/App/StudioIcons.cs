// =============================================================================
// NextScan Studio - Vector icon set and the two icon-only controls
// Plan ref: MASTER_PLAN section 13.1 (shell layout), 13.5 (visual design).
//
// Every icon is drawn on a 24 x 24 grid with a single stroke weight and then
// scaled, so the whole set stays optically consistent at any size and on any
// DPI. Unicode glyphs were the previous answer and they are not one set: the
// font picks a different designer for each character, they do not align on a
// common baseline, and several have no glyph at all in Segoe UI.
// =============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace NextScan.App
{
    public static class NsIcon
    {
        public const string Capture = "capture";
        public const string Detect = "detect";
        public const string Look = "look";
        public const string Output = "output";
        public const string Batch = "batch";
        public const string Pages = "pages";
        public const string Settings = "settings";

        public const string Fit = "fit";
        public const string Actual = "actual";
        public const string ZoomOut = "zoomout";
        public const string ZoomIn = "zoomin";
        public const string RotateLeft = "rotleft";
        public const string RotateRight = "rotright";
        public const string Grid = "grid";
        public const string Compare = "compare";
        public const string Close = "close";
        public const string Photoshop = "photoshop";
        public const string Play = "play";
        public const string Stack = "stack";
        public const string WholeBed = "wholebed";

        // The assistant. Nothing here is a robot or a brain: both are claims
        // about what is inside, and what the operator needs to recognise is
        // what the button does to their page.
        public const string Assist = "assist";
        public const string Send = "send";
        public const string Stop = "stop";
        public const string NewChat = "newchat";
        public const string Copy = "copy";

        /// <summary>
        /// Draws one icon centred in <paramref name="box"/>.
        ///
        /// Stroke width is scaled with the box but floored, so a 16 px icon is
        /// still a crisp hairline rather than a smudge.
        /// </summary>
        public static void Draw(Graphics g, string name, RectangleF box, Color colour)
        {
            float size = Math.Min(box.Width, box.Height);
            if (size < 6) return;

            float s = size / 24f;
            float width = Math.Max(1.25f, 1.7f * s);

            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(box.X + (box.Width - size) / 2f, box.Y + (box.Height - size) / 2f);
            g.ScaleTransform(s, s);

            using (Pen p = new Pen(colour, width / s))
            using (SolidBrush b = new SolidBrush(colour))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                p.LineJoin = LineJoin.Round;
                Paint(g, p, b, name);
            }

            g.Restore(state);
        }

        /// <summary>
        /// A four-point star with concave sides, drawn from Beziers so it keeps
        /// its waist at 16 px instead of collapsing into a diamond.
        /// </summary>
        static void Spark(Graphics g, SolidBrush b, float cx, float cy, float r)
        {
            float waist = r * 0.30f;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddBezier(cx, cy - r, cx + waist, cy - waist, cx + waist, cy - waist, cx + r, cy);
                path.AddBezier(cx + r, cy, cx + waist, cy + waist, cx + waist, cy + waist, cx, cy + r);
                path.AddBezier(cx, cy + r, cx - waist, cy + waist, cx - waist, cy + waist, cx - r, cy);
                path.AddBezier(cx - r, cy, cx - waist, cy - waist, cx - waist, cy - waist, cx, cy - r);
                path.CloseFigure();
                g.FillPath(b, path);
            }
        }

        static void Paint(Graphics g, Pen p, SolidBrush b, string name)
        {
            switch (name)
            {
                case Capture:
                    // A sheet on the glass with the sensor line crossing it.
                    g.DrawRectangle(p, 5f, 3.5f, 14f, 17f);
                    g.DrawLine(p, 2.5f, 12f, 21.5f, 12f);
                    g.DrawLine(p, 8.5f, 7.5f, 15.5f, 7.5f);
                    g.DrawLine(p, 8.5f, 16.5f, 13.5f, 16.5f);
                    break;

                case Detect:
                    // Crop corners closing in on what is on the glass.
                    g.DrawLines(p, new PointF[] { new PointF(3f, 8f), new PointF(3f, 3f), new PointF(8f, 3f) });
                    g.DrawLines(p, new PointF[] { new PointF(16f, 3f), new PointF(21f, 3f), new PointF(21f, 8f) });
                    g.DrawLines(p, new PointF[] { new PointF(21f, 16f), new PointF(21f, 21f), new PointF(16f, 21f) });
                    g.DrawLines(p, new PointF[] { new PointF(8f, 21f), new PointF(3f, 21f), new PointF(3f, 16f) });
                    g.DrawRectangle(p, 8.5f, 8.5f, 7f, 7f);
                    break;

                case Look:
                    // Half-filled disc: the standard mark for tone and contrast.
                    g.DrawEllipse(p, 3.5f, 3.5f, 17f, 17f);
                    g.FillPie(b, 3.5f, 3.5f, 17f, 17f, 90, 180);
                    break;

                case Output:
                    g.DrawLine(p, 12f, 3.5f, 12f, 14f);
                    g.DrawLines(p, new PointF[] { new PointF(7.5f, 9.5f), new PointF(12f, 14.2f), new PointF(16.5f, 9.5f) });
                    g.DrawLines(p, new PointF[] { new PointF(4f, 16f), new PointF(4f, 20.5f),
                                                  new PointF(20f, 20.5f), new PointF(20f, 16f) });
                    break;

                case Batch:
                case Stack:
                    g.DrawRectangle(p, 7f, 3.5f, 13f, 13f);
                    g.DrawLines(p, new PointF[] { new PointF(16.5f, 20.5f), new PointF(4f, 20.5f), new PointF(4f, 8f) });
                    break;

                case WholeBed:
                    // The platen, with the view pushing out to its corners. The
                    // deliberate opposite of Detect, which closes in on what is
                    // there: this one says take all of it.
                    g.DrawRectangle(p, 3.5f, 3.5f, 17f, 17f);
                    g.DrawLine(p, 10f, 10f, 6.6f, 6.6f);
                    g.DrawLines(p, new PointF[] { new PointF(6.5f, 10f), new PointF(6.5f, 6.5f),
                                                  new PointF(10f, 6.5f) });
                    g.DrawLine(p, 14f, 14f, 17.4f, 17.4f);
                    g.DrawLines(p, new PointF[] { new PointF(17.5f, 14f), new PointF(17.5f, 17.5f),
                                                  new PointF(14f, 17.5f) });
                    break;

                case Pages:
                    g.DrawRectangle(p, 3.5f, 3.5f, 7f, 7f);
                    g.DrawRectangle(p, 13.5f, 3.5f, 7f, 7f);
                    g.DrawRectangle(p, 3.5f, 13.5f, 7f, 7f);
                    g.DrawRectangle(p, 13.5f, 13.5f, 7f, 7f);
                    break;

                case Settings:
                    // Three sliders rather than a cog: a cog at 20 px is a blob,
                    // and sliders say "the things you set" more literally.
                    g.DrawLine(p, 3.5f, 6.5f, 20.5f, 6.5f);
                    g.DrawLine(p, 3.5f, 12f, 20.5f, 12f);
                    g.DrawLine(p, 3.5f, 17.5f, 20.5f, 17.5f);
                    g.FillEllipse(b, 13.4f, 4.4f, 4.2f, 4.2f);
                    g.FillEllipse(b, 6.4f, 9.9f, 4.2f, 4.2f);
                    g.FillEllipse(b, 15.4f, 15.4f, 4.2f, 4.2f);
                    break;

                case Fit:
                    g.DrawLines(p, new PointF[] { new PointF(3.5f, 8.5f), new PointF(3.5f, 3.5f), new PointF(8.5f, 3.5f) });
                    g.DrawLines(p, new PointF[] { new PointF(15.5f, 3.5f), new PointF(20.5f, 3.5f), new PointF(20.5f, 8.5f) });
                    g.DrawLines(p, new PointF[] { new PointF(20.5f, 15.5f), new PointF(20.5f, 20.5f), new PointF(15.5f, 20.5f) });
                    g.DrawLines(p, new PointF[] { new PointF(8.5f, 20.5f), new PointF(3.5f, 20.5f), new PointF(3.5f, 15.5f) });
                    break;

                case Actual:
                    g.DrawRectangle(p, 3.5f, 6.5f, 17f, 11f);
                    g.DrawLine(p, 9.5f, 9.5f, 9.5f, 14.5f);
                    g.DrawLine(p, 14.5f, 9.5f, 14.5f, 14.5f);
                    g.DrawLine(p, 8f, 9.5f, 9.5f, 9.5f);
                    g.DrawLine(p, 13f, 9.5f, 14.5f, 9.5f);
                    break;

                case ZoomOut:
                    g.DrawLine(p, 5f, 12f, 19f, 12f);
                    break;

                case ZoomIn:
                    g.DrawLine(p, 5f, 12f, 19f, 12f);
                    g.DrawLine(p, 12f, 5f, 12f, 19f);
                    break;

                case RotateLeft:
                    g.DrawArc(p, 4.5f, 4.5f, 15f, 15f, 120, 250);
                    g.DrawLines(p, new PointF[] { new PointF(3.4f, 5.2f), new PointF(4.6f, 10.4f), new PointF(9.8f, 9.2f) });
                    break;

                case RotateRight:
                    g.DrawArc(p, 4.5f, 4.5f, 15f, 15f, 170, 250);
                    g.DrawLines(p, new PointF[] { new PointF(20.6f, 5.2f), new PointF(19.4f, 10.4f), new PointF(14.2f, 9.2f) });
                    break;

                case Grid:
                    g.DrawRectangle(p, 3.5f, 3.5f, 17f, 17f);
                    g.DrawLine(p, 9.2f, 3.5f, 9.2f, 20.5f);
                    g.DrawLine(p, 14.8f, 3.5f, 14.8f, 20.5f);
                    g.DrawLine(p, 3.5f, 9.2f, 20.5f, 9.2f);
                    g.DrawLine(p, 3.5f, 14.8f, 20.5f, 14.8f);
                    break;

                case Compare:
                    g.DrawRectangle(p, 3.5f, 4.5f, 17f, 15f);
                    g.FillRectangle(b, 12f, 4.5f, 8.5f, 15f);
                    break;

                case Close:
                    g.DrawLine(p, 6.5f, 6.5f, 17.5f, 17.5f);
                    g.DrawLine(p, 17.5f, 6.5f, 6.5f, 17.5f);
                    break;

                case Play:
                    using (GraphicsPath path = new GraphicsPath())
                    {
                        path.AddPolygon(new PointF[] { new PointF(8f, 5f), new PointF(19f, 12f), new PointF(8f, 19f) });
                        g.FillPath(b, path);
                    }
                    break;

                case Photoshop:
                    g.DrawRectangle(p, 3.5f, 3.5f, 17f, 17f);
                    g.DrawLines(p, new PointF[] { new PointF(9f, 16.5f), new PointF(9f, 7.5f), new PointF(13f, 7.5f) });
                    g.DrawArc(p, 9f, 7.5f, 5.6f, 5.6f, 270, 180);
                    g.DrawLine(p, 9f, 13.1f, 11.8f, 13.1f);
                    break;

                case Assist:
                    // A four-point spark with a smaller one trailing it. The
                    // shape everything else in this category uses, which is the
                    // reason to use it: it is already learned.
                    Spark(g, b, 9.5f, 9.5f, 6.5f);
                    Spark(g, b, 17.5f, 17f, 3.4f);
                    break;

                case Send:
                    // Up, not sideways: the reply comes back into the same
                    // column the question left from.
                    g.DrawLine(p, 12f, 19.5f, 12f, 5.5f);
                    g.DrawLines(p, new PointF[] { new PointF(6.2f, 11.2f), new PointF(12f, 5.2f),
                                                  new PointF(17.8f, 11.2f) });
                    break;

                case Stop:
                    using (GraphicsPath path = Theme.Round(new Rectangle(7, 7, 10, 10), 2))
                        g.FillPath(b, path);
                    break;

                case Copy:
                    g.DrawRectangle(p, 8f, 3.5f, 12.5f, 12.5f);
                    g.DrawLines(p, new PointF[] { new PointF(16f, 20.5f), new PointF(3.5f, 20.5f),
                                                  new PointF(3.5f, 8f) });
                    break;

                case NewChat:
                    // A speech bubble with a plus in it. Not a bin: this starts
                    // a new conversation, it does not throw the page away, and a
                    // bin next to a scanned document reads as the worse one.
                    g.DrawLines(p, new PointF[] { new PointF(12.5f, 19.5f), new PointF(8f, 19.5f),
                                                  new PointF(4f, 22f), new PointF(4.6f, 19.2f) });
                    g.DrawArc(p, 3.5f, 3.5f, 17f, 17f, 100, 250);
                    g.DrawLine(p, 12f, 8.5f, 12f, 14.5f);
                    g.DrawLine(p, 9f, 11.5f, 15f, 11.5f);
                    break;

                default:
                    g.DrawEllipse(p, 6f, 6f, 12f, 12f);
                    break;
            }
        }
    }

    /// <summary>
    /// One entry in the left workflow rail: icon over a micro caption.
    ///
    /// The caption is not decoration. An icon-only rail is unreadable until it
    /// has been learned, and this is a tool people use occasionally; two extra
    /// rows of 7 pt type buy that back for eleven pixels.
    /// </summary>
    public class NsNavButton : NsBase
    {
        readonly Anim _sel = new Anim(0);

        public string Icon = NsIcon.Capture;
        public string Caption = "";

        bool _selected;

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                _sel.Target = value ? 1 : 0;
                Animator.Kick();
            }
        }

        public NsNavButton()
        {
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PageTab;
            Animator.Attach(this, _sel);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            double hot = HotAnim.Eased;
            double sel = _sel.Eased;

            Rectangle plate = new Rectangle(5, 3, Math.Max(1, Width - 10), Math.Max(1, Height - 6));
            if (sel > 0.01 || hot > 0.01)
            {
                Color fill = Theme.Mix(Theme.Surface,
                    Theme.Mix(Theme.Surface, Theme.Accent, Theme.IsLight ? 0.13 : 0.22),
                    Math.Max(sel, hot * 0.55));
                using (GraphicsPath path = Theme.Round(plate, 8))
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }

            Color colour = Theme.Mix(Theme.TextDim, Theme.Accent, sel);

            // The icon sits above the optical centre so the caption below does
            // not make the pair look bottom-heavy.
            // The same small lift the footer icons have, so the rail does not
            // feel like a different window from the panel beside it.
            float grow = (float)(20f * (1.0 + 0.09 * HotAnim.Eased));
            NsIcon.Draw(g, Icon, new RectangleF(Width / 2f - grow / 2f,
                plate.Top + 8f + (20f - grow) / 2f - (float)(0.8 * HotAnim.Eased), grow, grow), colour);

            using (Font font = Theme.UiSemi(6.9f))
                TextRenderer.DrawText(g, Caption, font,
                    new Rectangle(0, plate.Bottom - 17, Width, 14), colour,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);

            // A short bar on the active edge, the way every workflow rail marks
            // the current page. It reads even when the plate tint is subtle.
            if (sel > 0.01)
            {
                int barHeight = (int)Math.Round(plate.Height * 0.46 * sel);
                if (barHeight > 2)
                {
                    Rectangle bar = new Rectangle(0, plate.Top + (plate.Height - barHeight) / 2, 3, barHeight);
                    using (GraphicsPath path = Theme.Round(bar, 2))
                    using (SolidBrush brush = new SolidBrush(Theme.Accent))
                        g.FillPath(brush, path);
                }
            }

            PaintKeyboardFocus(g);
        }
    }

    /// <summary>
    /// A small square icon button, for the view controls in the status bar and
    /// the chrome buttons in the title bar.
    /// </summary>
    /// <summary>
    /// The icon Photoshop itself uses, taken from the installed application.
    ///
    /// Drawn marks are fine for the verbs in this window -- fit, rotate, close
    /// -- because those are ours. "Send this to Photoshop" is not a verb of
    /// ours, it names somebody else's program, and the thing that names it
    /// best is the icon that program already wears on the operator's own
    /// desktop. Reading it off the executable also means nothing of Adobe's is
    /// copied into this repository or shipped in the installer: if Photoshop
    /// is not installed there is no icon to read and the drawn mark is used,
    /// which is exactly the case where the drawn mark is the honest answer.
    /// </summary>
    public static class AppIcon
    {
        static bool _looked;
        static Image _photoshop;

        public static Image Photoshop
        {
            get
            {
                if (_looked) return _photoshop;
                _looked = true;
                try { _photoshop = FromExecutable(FindPhotoshop()); }
                catch { _photoshop = null; }
                return _photoshop;
            }
        }

        static Image FromExecutable(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using (Icon small = Icon.ExtractAssociatedIcon(path))
            {
                if (small == null) return null;
                // ExtractAssociatedIcon hands back a 32 pixel icon; drawing it
                // larger than that is what makes an icon look cheap, so it is
                // never asked to be.
                using (Icon sized = new Icon(small, 32, 32))
                    return sized.ToBitmap();
            }
        }

        /// <summary>
        /// The newest Photoshop on this machine. Newest rather than first,
        /// because an operator who has upgraded has both, and the one they use
        /// is the one they last installed.
        /// </summary>
        static string FindPhotoshop()
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            string best = null, bestName = null;
            foreach (string root in roots)
            {
                string adobe = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Adobe");
                if (adobe == null || !Directory.Exists(adobe)) continue;

                foreach (string folder in Directory.GetDirectories(adobe, "Adobe Photoshop*"))
                {
                    string exe = Path.Combine(folder, "Photoshop.exe");
                    if (!File.Exists(exe)) continue;
                    string name = Path.GetFileName(folder);
                    if (bestName == null || string.CompareOrdinal(name, bestName) > 0)
                    { best = exe; bestName = name; }
                }
            }
            return best;
        }
    }

    public class NsIconButton : NsBase
    {
        readonly Anim _on = new Anim(0);
        readonly Anim _press = new Anim(0);

        /// <summary>Draw this picture instead of a vector mark, when there is one.</summary>
        public Image Picture;

        public string Icon = NsIcon.Fit;

        /// <summary>Optional short text drawn instead of an icon.</summary>
        public string Glyph;

        public bool Toggle;

        /// <summary>Turns the hover plate red, for a window close button.</summary>
        public Color DangerHover = Color.Empty;

        /// <summary>
        /// Show a resting plate, not only one on hover.
        ///
        /// For a button that sits beside a labelled one. On its own in a
        /// toolbar an icon can afford to be bare, but next to "Preview" a bare
        /// icon reads as an empty box rather than as the button it is, and the
        /// operator has to hover over it to find out it was ever clickable.
        /// </summary>
        public bool Raised;

        /// <summary>A circular plate rather than a rounded square, for a send button.</summary>
        public bool Circle;

        bool _checked;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _on.Target = value ? 1 : 0;
                Animator.Kick();
            }
        }

        public NsIconButton()
        {
            Size = new Size(28, 22);
            Cursor = Cursors.Hand;
            Animator.Attach(this, _on);
            Animator.Attach(this, _press);
        }

        protected override void OnClick(EventArgs e)
        {
            if (Toggle) Checked = !Checked;
            base.OnClick(e);
        }

        // A press that only changes colour reads as a picture of a button. The
        // icon dipping under the finger and coming back is what makes it read
        // as a thing that was actually pushed.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            _press.Target = 1; Animator.Kick();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _press.Target = 0; Animator.Kick();
            base.OnMouseUp(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _press.Target = 0; Animator.Kick();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ClearBack(g);
            Theme.Smooth(g);

            double hot = HotAnim.Eased;
            double on = _on.Eased;

            Rectangle plate = new Rectangle(1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2));
            Color hoverFill = DangerHover.IsEmpty ? Theme.Hover : DangerHover;

            if (Raised || on > 0.01 || hot > 0.01)
            {
                Color rest = Raised ? Theme.Mix(Theme.BackOf(this), Theme.Field, 1.0) : Theme.BackOf(this);
                Color fill = on > hot
                    ? Theme.Mix(rest, Theme.Accent, on)
                    : Theme.Mix(rest, hoverFill, hot);
                using (GraphicsPath path = Theme.Round(plate, Circle
                                                       ? Math.Min(plate.Width, plate.Height) / 2
                                                       : 7))
                {
                    using (SolidBrush brush = new SolidBrush(fill))
                        g.FillPath(brush, path);
                    if (Raised && on < 0.5 && Picture == null && !Circle)
                        using (Pen edge = new Pen(Theme.Mix(Theme.Line, Theme.Accent, hot * 0.6), 1f))
                            g.DrawPath(edge, path);
                }
            }

            Color colour = on > 0.5 ? Theme.OnAccent
                         : (!DangerHover.IsEmpty && hot > 0.5) ? Color.White
                         : Theme.Mix(Theme.TextDim, Theme.Text, hot);

            // Grows a little under the pointer and dips under a press. Both are
            // small on purpose: an icon that jumps draws the eye away from the
            // page, which is the one thing in this window worth looking at.
            double press = _press.Eased;
            float scale = (float)(1.0 + 0.10 * hot - 0.14 * press);
            float lift = (float)(-1.0 * hot + 1.0 * press);

            if (!string.IsNullOrEmpty(Glyph))
            {
                using (Font font = Theme.UiSemi(7.75f))
                    TextRenderer.DrawText(g, Glyph, font, plate, colour,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix);
            }
            else
            {
                float box = (Math.Min(Width, Height) - 8f) * scale;
                RectangleF where = new RectangleF((Width - box) / 2f, (Height - box) / 2f + lift, box, box);

                if (Picture != null)
                {
                    // Someone else's artwork, so it is drawn rather than tinted:
                    // the only thing this window does to it is fade it back when
                    // the pointer is elsewhere, the same as every other icon here.
                    using (ImageAttributes fade = new ImageAttributes())
                    {
                        float alpha = (float)(0.72 + 0.28 * hot);
                        ColorMatrix matrix = new ColorMatrix();
                        matrix.Matrix33 = alpha;
                        fade.SetColorMatrix(matrix);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(Picture,
                            new Rectangle((int)where.X, (int)where.Y, (int)where.Width, (int)where.Height),
                            0, 0, Picture.Width, Picture.Height, GraphicsUnit.Pixel, fade);
                    }
                }
                else NsIcon.Draw(g, Icon, where, colour);
            }

            PaintKeyboardFocus(g);
        }
    }

    /// <summary>
    /// A short inline notice inside the inspector.
    ///
    /// The rule it usually carries - preview first, or the scan keeps the whole
    /// bed - used to be a 7.5 pt grey label, which is where product rules go to
    /// be ignored. Height is measured from the text so it never clips.
    /// </summary>
    public class NsNote : NsBase
    {
        public bool Warning;

        const int PadX = 11;
        const int PadY = 9;
        const int IconBox = 22;

        public NsNote() { Font = Theme.Ui(8f); }

        public static int Measure(string text, int width)
        {
            using (Bitmap bmp = new Bitmap(1, 1))
            using (Graphics g = Graphics.FromImage(bmp))
            using (Font f = Theme.Ui(8f))
            {
                int textWidth = Math.Max(20, width - PadX * 2 - IconBox);
                Size size = TextRenderer.MeasureText(g, text ?? "", f,
                    new Size(textWidth, 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                return size.Height + PadY * 2;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            ClearBack(g);

            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            Color accent = Warning ? Theme.Warn : Theme.Info;

            using (GraphicsPath path = Theme.Round(r, 6))
            {
                using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Field, 0.9)))
                    g.FillPath(b, path);
                using (Pen p = new Pen(Theme.LineSoft, 1f))
                    g.DrawPath(p, path);
            }

            // A ringed mark rather than a coloured bar down the left edge: the
            // bar reads as decoration and fights the panel's own alignment.
            float ix = PadX + 7.5f, iy = PadY + 7.5f;
            using (Pen p = new Pen(accent, 1.3f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                g.DrawEllipse(p, ix - 6.5f, iy - 6.5f, 13f, 13f);
                g.DrawLine(p, ix, iy - 3.4f, ix, iy + 0.9f);
                g.DrawLine(p, ix, iy + 3.5f, ix, iy + 3.8f);
            }

            Rectangle textRect = new Rectangle(PadX + IconBox, PadY,
                                               Math.Max(10, Width - PadX * 2 - IconBox),
                                               Math.Max(10, Height - PadY * 2));
            TextRenderer.DrawText(g, Text, Font, textRect, Theme.TextDim,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// A caption on the left and a value on the right, on a sunken plate.
    /// Used for numbers the operator is meant to check rather than change.
    /// </summary>
    public class NsReadout : NsBase
    {
        public string Caption = "";
        public string Value = "";
        public Color ValueColor = Color.Empty;

        public NsReadout() { Font = Theme.Ui(8f); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);
            ClearBack(g);

            Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using (GraphicsPath path = Theme.Round(r, 6))
            using (SolidBrush b = new SolidBrush(Theme.Mix(Theme.Surface, Theme.Field, 0.7)))
                g.FillPath(b, path);

            TextRenderer.DrawText(g, Caption, Font, new Rectangle(11, 0, Width - 22, Height),
                Theme.TextFaint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Color vc = ValueColor.IsEmpty ? Theme.Text : ValueColor;
            using (Font f = Theme.UiSemi(8f))
                TextRenderer.DrawText(g, Value, f, new Rectangle(11, 0, Width - 22, Height), vc,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right |
                    TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}
