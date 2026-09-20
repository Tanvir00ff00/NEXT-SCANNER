// =============================================================================
// NextScan Studio - Persisted window layout
// Plan ref: MASTER_PLAN section 13 (UI), 16 (settings live under %APPDATA%).
//
// Panel sizes and floating-bar positions are a user preference, not a build
// constant. They are kept apart from StudioSettings (which is about how to SCAN)
// so that resetting the layout never disturbs scan settings, and vice versa.
//
// Floating positions are stored as FRACTIONS of the canvas, not pixels: the
// window is resizable and a pixel position pinned on a 1038 px canvas would put
// the dock off-screen the first time someone maximised.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using NextScan.Core;

namespace NextScan.App
{
    public class StudioLayout
    {
        /// <summary>
        /// Inspector groups the operator left open, by title.
        ///
        /// Null means "never chosen" - the shell then uses its own defaults
        /// rather than opening nothing, which is what an empty list means.
        /// </summary>
        public List<string> OpenGroups;

        public bool IsGroupOpen(string title, bool fallback)
        {
            if (OpenGroups == null) return fallback;
            for (int i = 0; i < OpenGroups.Count; i++)
                if (string.Equals(OpenGroups[i], title, StringComparison.Ordinal)) return true;
            return false;
        }

        // ---- docked panels (pixels; they are edge-anchored) -------------------
        // Defaults are the arrangement the user settled on, not the first guess
        // the shell shipped with.
        public int DrawerWidth = 336;
        public int FilmstripWidth = 104;
        public int RailWidth = 60;

        // ---- floating overlays (fractions of the canvas) ----------------------
        /// <summary>Horizontal centre of the HUD, 0..1 across the canvas.</summary>
        public double HudX = 0.5;
        public double HudY = 0.012;
        public double DockX = 0.5;
        public double DockY = 0.945;

        /// <summary>Size multiplier for the floating bars, 0.8 .. 1.5.</summary>
        public double FloatScale = 1.0;

        // ---- title bar --------------------------------------------------------
        public int DeviceBarX = 178;

        // ---- visibility -------------------------------------------------------
        public bool ShowHud = true;
        public bool ShowDock = true;
        public bool ShowFilmstrip = true;

        // ---- window -----------------------------------------------------------
        public int WindowWidth;
        public int WindowHeight;
        public bool RememberWindowSize = true;

        /// <summary>Light is the default; dark is opt-in from the Layout page.</summary>
        public bool LightTheme = true;

        public static string PathOnDisk
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextScan");
                return Path.Combine(dir, "layout.json");
            }
        }

        public static StudioLayout Load()
        {
            StudioLayout l = new StudioLayout();
            try
            {
                if (!File.Exists(PathOnDisk)) return l;

                JsonObj o = Json.Parse(File.ReadAllText(PathOnDisk));
                l.DrawerWidth = Clamp(o.Int("drawerWidth", l.DrawerWidth), 300, 520);
                l.FilmstripWidth = Clamp(o.Int("filmstripWidth", l.FilmstripWidth), 0, 220);
                l.RailWidth = Clamp(o.Int("railWidth", l.RailWidth), 60, 120);

                l.HudX = ClampD(o.Dbl("hudX", l.HudX), 0.0, 1.0);
                l.HudY = ClampD(o.Dbl("hudY", l.HudY), 0.0, 1.0);
                l.DockX = ClampD(o.Dbl("dockX", l.DockX), 0.0, 1.0);
                l.DockY = ClampD(o.Dbl("dockY", l.DockY), 0.0, 1.0);
                l.FloatScale = ClampD(o.Dbl("floatScale", l.FloatScale), 0.8, 1.5);

                l.DeviceBarX = Clamp(o.Int("deviceBarX", l.DeviceBarX), 150, 4000);

                l.ShowHud = o.Bool("showHud", l.ShowHud);
                l.ShowDock = o.Bool("showDock", l.ShowDock);
                l.ShowFilmstrip = o.Bool("showFilmstrip", l.ShowFilmstrip);

                l.WindowWidth = o.Int("windowWidth", 0);
                l.WindowHeight = o.Int("windowHeight", 0);
                l.RememberWindowSize = o.Bool("rememberWindowSize", true);
                l.LightTheme = o.Bool("lightTheme", true);

                string groups = o.Str("openGroups", null);
                if (groups != null)
                {
                    l.OpenGroups = new List<string>();
                    foreach (string part in groups.Split('|'))
                        if (part.Length > 0) l.OpenGroups.Add(part);
                }
            }
            catch { }
            return l;
        }

        public bool Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(PathOnDisk);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                JsonObj o = new JsonObj()
                    .Set("drawerWidth", DrawerWidth)
                    .Set("filmstripWidth", FilmstripWidth)
                    .Set("railWidth", RailWidth)
                    .Set("hudX", HudX)
                    .Set("hudY", HudY)
                    .Set("dockX", DockX)
                    .Set("dockY", DockY)
                    .Set("floatScale", FloatScale)
                    .Set("deviceBarX", DeviceBarX)
                    .Set("showHud", ShowHud)
                    .Set("showDock", ShowDock)
                    .Set("showFilmstrip", ShowFilmstrip)
                    .Set("windowWidth", WindowWidth)
                    .Set("windowHeight", WindowHeight)
                    .Set("rememberWindowSize", RememberWindowSize)
                    .Set("lightTheme", LightTheme);

                // A pipe-joined string rather than a JSON array: the reader in
                // Core only does scalars, and group titles never contain a pipe.
                if (OpenGroups != null) o.Set("openGroups", string.Join("|", OpenGroups.ToArray()));

                File.WriteAllText(PathOnDisk, Json.Write(o));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Back to the shipped arrangement, without touching scan settings.</summary>
        public void ResetToDefaults()
        {
            StudioLayout d = new StudioLayout();
            DrawerWidth = d.DrawerWidth;
            FilmstripWidth = d.FilmstripWidth;
            RailWidth = d.RailWidth;
            HudX = d.HudX; HudY = d.HudY;
            DockX = d.DockX; DockY = d.DockY;
            FloatScale = d.FloatScale;
            DeviceBarX = d.DeviceBarX;
            ShowHud = d.ShowHud;
            ShowDock = d.ShowDock;
            ShowFilmstrip = d.ShowFilmstrip;
            LightTheme = d.LightTheme;
            OpenGroups = null;
            WindowWidth = 0;
            WindowHeight = 0;
        }

        static int Clamp(int v, int lo, int hi) { return Math.Max(lo, Math.Min(hi, v)); }
        static double ClampD(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }
    }
}
