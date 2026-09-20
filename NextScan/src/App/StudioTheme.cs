// =============================================================================
// NextScan Studio - Design system: palette, type, easing, animation host
// Plan ref: MASTER_PLAN section 13 (UI), 13.5 (visual design).
//
// Neutral surfaces protect colour judgement while a restrained blue accent
// separates actions from amber crop warnings. Shared contrast and spacing tokens
// prevent individual controls from drifting between the light and dark themes.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace NextScan.App
{
    public static class Theme
    {
        // ---- palette -----------------------------------------------------------
        //
        // Mutable statics rather than readonly: every control reads these at paint
        // time, so flipping the palette and invalidating is enough to restyle the
        // whole shell. Both palettes are TRUE NEUTRAL (R=G=B) - a colour cast in
        // the surround biases judgement of a scan either way round (plan 13.5).

        public static Color Ground, Surface, Raised, Field, Hover, Line, LineSoft, Proof;
        public static Color Text, TextDim, TextFaint;
        public static Color Accent, AccentSoft, Good, Danger, Warn, Info;

        /// <summary>Fill and edge for the floating bars.</summary>
        public static Color Glass, GlassLine, GlassTop;

        /// <summary>Text drawn on top of the accent colour.</summary>
        public static Color OnAccent;

        public static bool IsLight { get; private set; }

        static Theme() { Apply(true); }

        /// <summary>Switches the palette. Callers must invalidate afterwards.</summary>
        public static void Apply(bool light)
        {
            IsLight = light;

            if (light)
            {
                Ground     = Color.FromArgb(0xE8, 0xE8, 0xE8);   // canvas surround
                Surface    = Color.FromArgb(0xFA, 0xFA, 0xFA);   // chrome
                Raised     = Color.FromArgb(0xFF, 0xFF, 0xFF);   // cards, menus
                Field      = Color.FromArgb(0xF4, 0xF4, 0xF4);   // inputs
                Hover      = Color.FromArgb(0xEC, 0xEC, 0xEC);
                Line       = Color.FromArgb(0xD0, 0xD0, 0xD0);
                LineSoft   = Color.FromArgb(0xDE, 0xDE, 0xDE);
                Proof      = Color.FromArgb(0xBF, 0xBF, 0xBF);   // 18% grey equivalent

                Text       = Color.FromArgb(0x1B, 0x1B, 0x1B);
                TextDim    = Color.FromArgb(0x5C, 0x5C, 0x5C);
                TextFaint  = Color.FromArgb(0x70, 0x70, 0x70);

                Glass      = Color.FromArgb(238, 0xFF, 0xFF, 0xFF);
                GlassLine  = Color.FromArgb(40, 0, 0, 0);
                GlassTop   = Color.FromArgb(150, 255, 255, 255);
            }
            else
            {
                Ground     = Color.FromArgb(0x16, 0x16, 0x16);
                Surface    = Color.FromArgb(0x20, 0x20, 0x20);
                Raised     = Color.FromArgb(0x29, 0x29, 0x29);
                Field      = Color.FromArgb(0x2E, 0x2E, 0x2E);
                Hover      = Color.FromArgb(0x35, 0x35, 0x35);
                Line       = Color.FromArgb(0x3A, 0x3A, 0x3A);
                LineSoft   = Color.FromArgb(0x2A, 0x2A, 0x2A);
                Proof      = Color.FromArgb(0x2E, 0x2E, 0x2E);

                Text       = Color.FromArgb(0xED, 0xED, 0xED);
                TextDim    = Color.FromArgb(0xB8, 0xB8, 0xB8);
                TextFaint  = Color.FromArgb(0xA0, 0xA0, 0xA0);

                Glass      = Color.FromArgb(228, 0x1E, 0x1E, 0x1E);
                GlassLine  = Color.FromArgb(30, 255, 255, 255);
                GlassTop   = Color.FromArgb(22, 255, 255, 255);
            }

            // Blue is reserved for actions and selection; amber remains a warning.
            // Each accent has its own contrasting foreground in the paired theme.
            Accent     = light ? Color.FromArgb(36, 88, 211) : Color.FromArgb(132, 174, 255);
            AccentSoft = light ? Color.FromArgb(27, 65, 164) : Color.FromArgb(169, 197, 255);
            Good       = light ? Color.FromArgb(0x1E, 0xA9, 0x4E) : Color.FromArgb(0x32, 0xD7, 0x4B);
            Danger     = light ? Color.FromArgb(0xD9, 0x2D, 0x20) : Color.FromArgb(0xFF, 0x45, 0x3A);
            Warn       = light ? Color.FromArgb(0xC2, 0x8A, 0x00) : Color.FromArgb(0xFF, 0xD6, 0x0A);
            Info       = light ? Color.FromArgb(0x00, 0x77, 0xC2) : Color.FromArgb(0x64, 0xD2, 0xFF);
            OnAccent   = light ? Color.White : Color.FromArgb(19, 31, 51);
        }

        // ---- metrics ----------------------------------------------------------
        public const int RailWidth = 68;
        public const int DrawerWidth = 336;
        public const int FilmstripWidth = 104;

        // Tighter than before: every pixel these two take is a pixel the preview
        // does not get, and neither carries anything that needs the room.
        public const int TitleBarHeight = 50;
        public const int StatusBarHeight = 26;

        static string _family;

        /// <summary>
        /// Segoe UI Variable is the Windows 11 face; fall back to Segoe UI on 10.
        /// Resolved once - probing per font construction is measurably slow when
        /// controls rebuild their fonts during an animation.
        /// </summary>
        public static string Family
        {
            get
            {
                if (_family != null) return _family;
                _family = "Segoe UI";
                try
                {
                    foreach (FontFamily f in FontFamily.Families)
                    {
                        if (f.Name == "Segoe UI Variable Display") { _family = f.Name; break; }
                    }
                }
                catch { }
                return _family;
            }
        }

        public static Font Ui(float size) { return new Font(Family, size, FontStyle.Regular, GraphicsUnit.Point); }
        public static Font UiSemi(float size) { return new Font(Family, size, FontStyle.Bold, GraphicsUnit.Point); }

        /// <summary>Uppercase micro-label used for section headers.</summary>
        public static Font Micro() { return new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point); }

        // ---- painting helpers --------------------------------------------------
        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }

            int d = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2)) * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void Smooth(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        /// <summary>
        /// Nearest opaque ancestor background.
        ///
        /// Owner-drawn controls with a transparent BackColor do not get their
        /// background cleared by WinForms, so whatever was underneath survives and
        /// text ghosts on every repaint. Every control here clears with this first.
        /// </summary>
        public static Color BackOf(Control c)
        {
            Control p = (c != null) ? c.Parent : null;
            while (p != null)
            {
                if (p.BackColor.A == 255) return p.BackColor;
                p = p.Parent;
            }
            return Surface;
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0.0, Math.Min(1.0, t));
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>Soft drop shadow under a rounded rect, drawn as fading outlines.</summary>
        public static void Shadow(Graphics g, Rectangle r, int radius, int depth, int alpha)
        {
            for (int i = depth; i >= 1; i--)
            {
                Rectangle rr = Rectangle.Inflate(r, i, i);
                rr.Offset(0, Math.Max(1, i / 2));
                int a = Math.Max(1, alpha / (i + 1));
                using (GraphicsPath path = Round(rr, radius + i))
                using (Pen p = new Pen(Color.FromArgb(a, 0, 0, 0), 1.6f))
                    g.DrawPath(p, path);
            }
        }
    }

    // =========================================================================
    // Animation
    // =========================================================================
    /// <summary>
    /// One animated scalar. Uses a critically-damped-feeling exponential ease
    /// rather than a fixed-duration tween: targets can change mid-flight (a
    /// hover that reverses before it finished) without a visible restart.
    /// </summary>
    public class Anim
    {
        public double Value;
        public double Target;

        /// <summary>Fraction of the remaining distance consumed per tick.</summary>
        public double Rate = 0.28;

        public Anim() { }
        public Anim(double initial) { Value = Target = initial; }

        public bool Settled { get { return Math.Abs(Target - Value) < 0.0015; } }

        public void Snap(double v) { Value = Target = v; }

        /// <summary>Advances one frame. Returns true when the value moved.</summary>
        public bool Step()
        {
            if (Settled)
            {
                if (Value == Target) return false;
                Value = Target;
                return true;
            }
            Value += (Target - Value) * Rate;
            return true;
        }

        /// <summary>Smoothstep of the current value, for easing position and alpha.</summary>
        public double Eased
        {
            get
            {
                double t = Math.Max(0.0, Math.Min(1.0, Value));
                return t * t * (3.0 - 2.0 * t);
            }
        }
    }

    /// <summary>
    /// One 60 Hz timer for the whole application.
    ///
    /// Giving every control its own Timer is the usual WinForms mistake: a dozen
    /// uncoordinated timers produce visible tearing between elements that are
    /// meant to move together, and they keep ticking when nothing is animating.
    /// This host runs a single timer and stops itself once every registered
    /// animation has settled.
    /// </summary>
    public static class Animator
    {
        class Entry
        {
            public Control Owner;
            public Anim[] Anims;
        }

        static readonly List<Entry> _entries = new List<Entry>();
        static Timer _timer;


        public static void Attach(Control owner, params Anim[] anims)
        {
            if (owner == null || anims == null || anims.Length == 0) return;

            _entries.Add(new Entry { Owner = owner, Anims = anims });
            owner.Disposed += delegate { Detach(owner); };
            Ensure();
        }

        public static void Detach(Control owner)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_entries[i].Owner, owner)) _entries.RemoveAt(i);
        }

        /// <summary>Call after changing a Target so the timer wakes up.</summary>
        public static void Kick() { Ensure(); }

        static void Ensure()
        {
            if (_timer != null) { if (!_timer.Enabled) _timer.Start(); return; }

            _timer = new Timer();
            _timer.Interval = 16;
            _timer.Tick += OnTick;
            _timer.Start();
        }

        static void OnTick(object sender, EventArgs e)
        {
            bool anyRunning = false;

            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry en = _entries[i];
                if (en.Owner == null || en.Owner.IsDisposed) { _entries.RemoveAt(i); continue; }

                bool moved = false;
                for (int k = 0; k < en.Anims.Length; k++)
                {
                    if (en.Anims[k] == null) continue;
                    if (en.Anims[k].Step()) moved = true;
                    if (!en.Anims[k].Settled) anyRunning = true;
                }

                if (!moved) continue;

                try { if (en.Owner.IsHandleCreated) en.Owner.Invalidate(); }
                catch { }
            }

            // Idle apps should not burn a wake-up every 16 ms.
            if (!anyRunning && _timer != null) _timer.Stop();
        }
    }
}
