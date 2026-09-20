// =============================================================================
// NextScan Studio - the installer window
// Plan ref: MASTER_PLAN section 19, 13.5 (visual design).
//
// Built from the application's own Theme, icon set, controls and Animator, so
// the first thing anyone sees of this product already looks like the product.
// A stock installer chrome would be the one screen that looks like something
// else, and it is the screen everybody sees.
//
// The motif on the left is the thing being installed: a sheet on the glass with
// the sensor line travelling down it. It is the same idea as the scan animation
// in the canvas, and it means the waiting is spent watching a scanner rather
// than watching a progress bar.
//
// Everything is drawn. Nothing here is a stock WinForms visual, for the reason
// given in StudioControls: the native controls paint a light system chrome that
// does not honour a dark surface.
// =============================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NextScan.App;

namespace NextScan.Setup
{
    public class SetupWindow : Form
    {
        enum Step { Welcome, Working, Done, Failed }

        const int RailWidth = 300;
        const int Pad = 34;

        readonly bool _uninstalling;
        readonly bool _photoshopFound;

        Step _step = Step.Welcome;
        readonly long _payloadBytes;
        string _directory;
        string _saying = "";
        string _failure = "";

        // One animation per thing that moves, all driven by the shared 60 Hz
        // ticker rather than a timer each.
        readonly Anim _reveal = new Anim(0);
        readonly Anim _slide = new Anim(1);
        readonly Anim _sweep = new Anim(0);
        readonly Anim _bar = new Anim(0);
        readonly Anim _tick = new Anim(0);

        NsPill _go, _cancel, _change;
        NsToggle _connector, _desktop;
        Label _pathLabel;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        public SetupWindow(bool uninstalling)
        {
            _uninstalling = uninstalling;
            _photoshopFound = SetupWork.PhotoshopPluginFolders().Count > 0;
            _directory = SetupWork.DefaultDirectory;
            _payloadBytes = uninstalling ? 0 : SetupWork.PayloadBytes();

            Theme.Apply(false);      // the installer is dark, whatever the shell later becomes

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(RailWidth + 500, 500);
            BackColor = Theme.Surface;
            Text = SetupWork.Product + " Setup";
            KeyPreview = true;

            BuildControls();
            Layout_();

            Animator.Attach(this, _reveal, _slide, _sweep, _bar, _tick);
            _reveal.Rate = 0.14;
            _reveal.Target = 1;
            _sweep.Rate = 0.012;
            _sweep.Target = 1;
            Animator.Kick();

            Shown += delegate { RoundTheCorners(); };
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape && _step != Step.Working) Close();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ClassStyle |= 0x00020000;      // CS_DROPSHADOW
                return p;
            }
        }

        void RoundTheCorners()
        {
            // Windows 11 rounds it properly, with its own antialiasing. Older
            // Windows ignores the attribute and keeps square corners, which is
            // what it does everywhere else on those versions anyway.
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); }
            catch { }
        }

        // ---- controls ----------------------------------------------------------

        void BuildControls()
        {
            _go = new NsPill { Kind = PillKind.Primary, Radius = 9, Font = Theme.UiSemi(9.5f) };
            _go.Text = _uninstalling ? "Remove" : "Install";
            _go.Click += delegate { Begin(); };
            Controls.Add(_go);

            _cancel = new NsPill { Kind = PillKind.Quiet, Radius = 9, Text = "Cancel" };
            _cancel.Click += delegate { Close(); };
            Controls.Add(_cancel);

            if (!_uninstalling)
            {
                _pathLabel = new Label
                {
                    AutoSize = false,
                    ForeColor = Theme.TextFaint,
                    BackColor = Color.Transparent,
                    Font = Theme.Ui(8.5f),
                    TextAlign = ContentAlignment.MiddleLeft
                };
                Controls.Add(_pathLabel);

                _change = new NsPill { Kind = PillKind.Quiet, Radius = 8, Text = "Change", Font = Theme.Ui(8.5f) };
                _change.Click += delegate { ChooseFolder(); };
                Controls.Add(_change);

                if (_photoshopFound)
                {
                    _connector = new NsToggle { Checked = true, Text = "Add Next Scanner to Photoshop's Import menu" };
                    Controls.Add(_connector);
                }

                _desktop = new NsToggle { Checked = false, Text = "Put a shortcut on the desktop" };
                Controls.Add(_desktop);
            }
        }

        void Layout_()
        {
            int left = RailWidth + Pad;
            int wide = ClientSize.Width - left - Pad;

            if (_pathLabel != null) _pathLabel.SetBounds(left, 216, wide - 96, 20);
            if (_change != null) _change.SetBounds(left + wide - 86, 210, 86, 30);
            if (_connector != null) _connector.SetBounds(left, 258, wide, 30);
            if (_desktop != null) _desktop.SetBounds(left, _connector != null ? 296 : 258, wide, 30);

            int row = ClientSize.Height - Pad - 40;
            _go.SetBounds(ClientSize.Width - Pad - 132, row, 132, 40);
            _cancel.SetBounds(ClientSize.Width - Pad - 132 - 10 - 104, row, 104, 40);
        }

        void ShowStep(Step step)
        {
            _step = step;
            bool welcome = step == Step.Welcome;

            if (_pathLabel != null) _pathLabel.Visible = welcome;
            if (_change != null) _change.Visible = welcome;
            if (_connector != null) _connector.Visible = welcome;
            if (_desktop != null) _desktop.Visible = welcome;

            _cancel.Visible = welcome;
            _go.Visible = step == Step.Done || step == Step.Failed || welcome;

            if (step == Step.Done)
            {
                _go.Text = _uninstalling ? "Close" : "Start " + SetupWork.Product;
                _tick.Rate = 0.1;
                _tick.Target = 1;
            }
            else if (step == Step.Failed) _go.Text = "Close";

            _slide.Snap(0);
            _slide.Target = 1;
            Animator.Kick();
            Invalidate();
        }

        void ChooseFolder()
        {
            // The system dialog, not one of ours. Somewhere to put a program is
            // a question the operating system already answers well, and a
            // hand-drawn folder tree would be worse at it in every way that
            // matters.
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Where should " + SetupWork.Product + " go?";
                dialog.ShowNewFolderButton = true;
                try { dialog.SelectedPath = Directory.Exists(_directory) ? _directory : Path.GetDirectoryName(_directory); }
                catch { }

                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                // A folder chosen from a picker is a parent to install into, not
                // the installation itself: picking Program Files should not empty
                // Program Files on uninstall.
                string chosen = dialog.SelectedPath;
                if (!string.Equals(Path.GetFileName(chosen.TrimEnd('\\')), SetupWork.Product,
                                   StringComparison.OrdinalIgnoreCase))
                    chosen = Path.Combine(chosen, SetupWork.Product);

                _directory = chosen;
                _pathLabel.Text = _directory;
                Invalidate();
            }
        }

        // ---- doing it ----------------------------------------------------------

        void Begin()
        {
            if (_step == Step.Done)
            {
                if (!_uninstalling) StartTheApplication();
                Close();
                return;
            }
            if (_step == Step.Failed) { Close(); return; }

            bool connector = _connector != null && _connector.Checked;
            bool desktop = _desktop != null && _desktop.Checked;
            string directory = _directory;

            ShowStep(Step.Working);

            // Off the UI thread, so the sweep keeps sweeping while a sixty
            // megabyte payload is written. Progress comes back through
            // BeginInvoke; nothing here touches a control directly.
            Thread worker = new Thread(delegate ()
            {
                try
                {
                    SetupWork.Progress say = delegate (string what, double done)
                    {
                        try { BeginInvoke((MethodInvoker)delegate { Say(what, done); }); }
                        catch { }
                    };

                    if (_uninstalling) SetupWork.Uninstall(say);
                    else SetupWork.Install(directory, connector, desktop, say);

                    try { BeginInvoke((MethodInvoker)delegate { ShowStep(Step.Done); }); } catch { }
                }
                catch (Exception ex)
                {
                    string why = ex.Message;
                    try { BeginInvoke((MethodInvoker)delegate { _failure = why; ShowStep(Step.Failed); }); }
                    catch { }
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        void Say(string what, double done)
        {
            _saying = what ?? "";
            _bar.Target = Math.Max(0.0, Math.Min(1.0, done));
            Animator.Kick();
            Invalidate();
        }

        void StartTheApplication()
        {
            try
            {
                string exe = Path.Combine(_directory, SetupWork.ExeName);
                if (File.Exists(exe))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    { UseShellExecute = true, WorkingDirectory = _directory });
            }
            catch { }
        }

        // ---- painting ----------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            g.Clear(Theme.Surface);
            PaintRail(g);
            PaintContent(g);
            PaintClose(g);

            // A hairline, so the window has an edge on a light desktop.
            using (Pen edge = new Pen(Theme.Line))
                g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            // The whole window fades up rather than appearing. It costs one
            // rectangle and it is the difference between arriving and being
            // switched on.
            if (_reveal.Value < 0.999)
            {
                int veil = (int)(255 * (1.0 - _reveal.Eased));
                using (SolidBrush b = new SolidBrush(Color.FromArgb(veil, Theme.Ground)))
                    g.FillRectangle(b, ClientRectangle);
            }

            KeepSweeping();
        }

        void KeepSweeping()
        {
            // The sensor line runs top to bottom, then starts again. Restarting
            // it here rather than on a timer keeps it on the one shared ticker.
            if (_sweep.Settled) { _sweep.Snap(0); _sweep.Target = 1; Animator.Kick(); }
        }

        void PaintRail(Graphics g)
        {
            Rectangle rail = new Rectangle(0, 0, RailWidth, ClientSize.Height);
            using (LinearGradientBrush b = new LinearGradientBrush(rail,
                       Theme.Ground, Color.FromArgb(0x0E, 0x12, 0x1C), LinearGradientMode.Vertical))
                g.FillRectangle(b, rail);

            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, RailWidth, 0, RailWidth, ClientSize.Height);

            // ---- the sheet on the glass ----
            RectangleF glass = new RectangleF(58, 96, 184, 236);
            using (GraphicsPath path = Rounded(glass, 10))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(22, 255, 255, 255))) g.FillPath(b, path);
                using (Pen p = new Pen(Color.FromArgb(46, 255, 255, 255))) g.DrawPath(p, path);
            }

            // Lines of "text" on the sheet, so there is something for the sensor
            // to pass over. Deliberately irregular: evenly spaced bars read as a
            // loading placeholder, which is the one thing this must not look like.
            float[] widths = { 0.74f, 0.52f, 0.86f, 0.63f, 0.80f, 0.41f, 0.70f, 0.57f };
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                for (int i = 0; i < widths.Length; i++)
                    g.FillRectangle(ink, glass.X + 22, glass.Y + 34 + i * 25, (glass.Width - 44) * widths[i], 7);

            // ---- the sensor line ----
            float t = (float)_sweep.Value;
            float y = glass.Y + 10 + (glass.Height - 20) * t;

            // The trail behind it, fading upwards, is what makes it read as
            // travelling rather than blinking.
            RectangleF trail = new RectangleF(glass.X, Math.Max(glass.Y, y - 70), glass.Width, 70);
            if (trail.Height > 1)
                using (LinearGradientBrush b = new LinearGradientBrush(trail,
                           Color.FromArgb(0, Theme.Accent), Color.FromArgb(70, Theme.Accent),
                           LinearGradientMode.Vertical))
                    g.FillRectangle(b, trail);

            using (Pen p = new Pen(Color.FromArgb(235, Theme.AccentSoft), 2f))
                g.DrawLine(p, glass.X + 4, y, glass.Right - 4, y);
            using (Pen p = new Pen(Color.FromArgb(70, Theme.Accent), 7f))
                g.DrawLine(p, glass.X + 4, y, glass.Right - 4, y);

            // ---- name ----
            using (Font f = Theme.UiSemi(15f))
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString("NextScan", f, b, 58, 372);
            using (Font f = Theme.Ui(15f))
            using (SolidBrush b = new SolidBrush(Theme.TextDim))
                g.DrawString("Studio", f, b, 58 + (int)g.MeasureString("NextScan", Theme.UiSemi(15f)).Width, 372);

            using (Font f = Theme.Ui(8.5f))
            using (SolidBrush b = new SolidBrush(Theme.TextFaint))
                g.DrawString("Version " + SetupWork.Version, f, b, 59, 400);
        }

        void PaintContent(Graphics g)
        {
            int left = RailWidth + Pad;
            int wide = ClientSize.Width - left - Pad;

            // Each step arrives from slightly below, so a change of step is a
            // movement rather than a repaint.
            int lift = (int)((1.0 - _slide.Eased) * 14);

            using (SolidBrush title = new SolidBrush(Theme.Text))
            using (SolidBrush body = new SolidBrush(Theme.TextDim))
            using (Font big = Theme.UiSemi(18f))
            using (Font small = Theme.Ui(9.5f))
            {
                if (_step == Step.Welcome)
                {
                    g.DrawString(_uninstalling ? "Remove NextScan Studio" : "Install NextScan Studio",
                                 big, title, left, 92 + lift);
                    g.DrawString(_uninstalling
                            ? "The program and its Photoshop connector will be removed.\nYour scans, settings and diagnostics are left where they are."
                            : "A scanner suite that finds what is on the glass, crops it,\nand hands it to Photoshop without a file in between.",
                        small, body, new RectangleF(left, 136 + lift, wide, 60));

                    if (!_uninstalling)
                    {
                        using (Font f = Theme.Micro())
                        using (SolidBrush b = new SolidBrush(Theme.TextFaint))
                            g.DrawString("INSTALL TO", f, b, left, 194 + lift);

                        using (Pen line = new Pen(Theme.LineSoft))
                            g.DrawLine(line, left, 350, left + wide, 350);

                        // Said plainly, because most of that number is the two
                        // segmentation models, and an installation without them
                        // is the failure this whole installer exists to prevent.
                        using (Font f = Theme.Ui(8.5f))
                        using (SolidBrush b = new SolidBrush(Theme.TextFaint))
                            g.DrawString(Describe(), f, b, left, 366);
                    }
                }
                else if (_step == Step.Working)
                {
                    g.DrawString(_uninstalling ? "Removing" : "Installing", big, title, left, 92 + lift);
                    PaintBar(g, left, wide, 168);

                    using (SolidBrush b = new SolidBrush(Theme.TextDim))
                        g.DrawString(_saying, small, b, left, 190);

                    using (Font f = Theme.UiSemi(9f))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.DrawString(((int)Math.Round(_bar.Value * 100)) + "%", f, b, left + wide - 44, 190);
                }
                else if (_step == Step.Done)
                {
                    PaintTick(g, left, 88);
                    g.DrawString(_uninstalling ? "Removed" : "Ready", big, title, left + 58, 92 + lift);
                    g.DrawString(_uninstalling
                            ? "NextScan Studio is no longer installed on this computer."
                            : "Installed. If Photoshop is open, restart it before using\nFile, Import, Next Scanner.",
                        small, body, new RectangleF(left, 150 + lift, wide, 60));
                }
                else
                {
                    using (SolidBrush bad = new SolidBrush(Theme.Danger))
                        g.DrawString("That did not work", big, bad, left, 92 + lift);
                    g.DrawString(_failure, small, body, new RectangleF(left, 140 + lift, wide, 120));
                }
            }
        }

        string Describe()
        {
            string size = _payloadBytes > 0
                ? string.Format("{0:N0} MB", Math.Round(_payloadBytes / (1024.0 * 1024.0)))
                : "a little space";

            return "Includes the detection models and both scanner hosts. Needs " + size + ".";
        }

        void PaintBar(Graphics g, int left, int wide, int y)
        {
            RectangleF track = new RectangleF(left, y, wide, 8);
            using (GraphicsPath p = Rounded(track, 4))
            using (SolidBrush b = new SolidBrush(Theme.Field))
                g.FillPath(b, p);

            float w = (float)(wide * _bar.Eased);
            if (w < 2) return;

            RectangleF fill = new RectangleF(left, y, w, 8);
            using (GraphicsPath p = Rounded(fill, 4))
            using (LinearGradientBrush b = new LinearGradientBrush(
                       new RectangleF(left, y, Math.Max(w, 1), 8),
                       Theme.Accent, Theme.AccentSoft, LinearGradientMode.Horizontal))
                g.FillPath(b, p);

            // A sheen riding the leading edge, tied to the same sweep as the
            // sensor line, so the two move as one thing.
            float sheenX = left + w - 26 + (float)(Math.Sin(_sweep.Value * Math.PI * 2) * 6);
            if (sheenX > left)
            {
                RectangleF sheen = new RectangleF(sheenX, y, 26, 8);
                using (LinearGradientBrush b = new LinearGradientBrush(sheen,
                           Color.FromArgb(0, Color.White), Color.FromArgb(70, Color.White),
                           LinearGradientMode.Horizontal))
                using (GraphicsPath p = Rounded(RectangleF.Intersect(sheen, fill), 4))
                    g.FillPath(b, p);
            }
        }

        void PaintTick(Graphics g, int x, int y)
        {
            float t = (float)_tick.Eased;
            using (Pen ring = new Pen(Color.FromArgb(90, Theme.Good), 2f))
                g.DrawEllipse(ring, x, y, 38, 38);

            // Drawn on, not faded in: the stroke is what says finished.
            PointF a = new PointF(x + 11, y + 20);
            PointF b = new PointF(x + 17, y + 26);
            PointF c = new PointF(x + 28, y + 13);

            using (Pen p = new Pen(Theme.Good, 2.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float first = Math.Min(1f, t / 0.45f);
                g.DrawLine(p, a, new PointF(a.X + (b.X - a.X) * first, a.Y + (b.Y - a.Y) * first));
                if (t > 0.45f)
                {
                    float second = (t - 0.45f) / 0.55f;
                    g.DrawLine(p, b, new PointF(b.X + (c.X - b.X) * second, b.Y + (c.Y - b.Y) * second));
                }
            }
        }

        void PaintClose(Graphics g)
        {
            if (_step == Step.Working) return;
            Rectangle box = CloseBox();
            using (Pen p = new Pen(Theme.TextFaint, 1.6f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawLine(p, box.X + 6, box.Y + 6, box.Right - 6, box.Bottom - 6);
                g.DrawLine(p, box.Right - 6, box.Y + 6, box.X + 6, box.Bottom - 6);
            }
        }

        Rectangle CloseBox() { return new Rectangle(ClientSize.Width - 42, 14, 28, 28); }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { p.AddRectangle(new RectangleF(r.X, r.Y, 1, 1)); return p; }

            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---- moving the window -------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;

            if (_step != Step.Working && CloseBox().Contains(e.Location)) { Close(); return; }

            // Anywhere in the top strip drags it, which is where anyone reaches
            // for a window with no title bar.
            if (e.Y < 56)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);   // WM_NCLBUTTONDOWN, HTCAPTION
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = (_step != Step.Working && CloseBox().Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Stopping halfway through writing sixty megabytes into Program Files
            // leaves something that is neither installed nor absent.
            if (_step == Step.Working) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_pathLabel != null) _pathLabel.Text = _directory;
            ShowStep(Step.Welcome);
        }
    }
}
