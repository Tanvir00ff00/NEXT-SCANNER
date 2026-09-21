// =============================================================================
// NextScan Studio - named jobs
// Plan ref: MASTER_PLAN section 12.
//
// The same documents come back: identity cards at one resolution into one
// folder under one naming pattern, passports at another, photographs at a
// third. All the machinery for each of those already exists. What did not was
// any way to say "this set, again", so every repeat was set up by hand from
// memory.
//
// A preset is a settings file under another name. The format, the writer and
// the reader are the ones StudioSettings already has -- Save and Load both take
// a path -- so there is no second format to keep in step, and a preset can be
// read, diffed and edited in Notepad like the settings file it is.
//
// What a preset deliberately does NOT carry is the interesting part; see
// ApplyJob.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;

namespace NextScan.App
{
    public static class StudioPresets
    {
        public const int LongestName = 48;

        /// <summary>
        /// The fields a preset must not carry, named so the self test can hold
        /// ApplyJob to them. Everything else in StudioSettings is part of a job
        /// and must be copied; see the reasoning on ApplyJob.
        /// </summary>
        static readonly string[] NotPartOfAJob =
        {
            "DeviceName", "Transport", "HostBitness", "ColorProfilePath",
            "HotFolderPath", "HotFolderGroupPerBatch", "HotFolderDisposition"
        };

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "presets");
            }
        }

        /// <summary>
        /// A preset name becomes a file name, so it is checked rather than
        /// trusted. Letters, digits, spaces and a few separators only: no dots,
        /// no slashes, nothing that can climb out of the folder or land on a
        /// reserved device name.
        /// </summary>
        public static bool IsUsableName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string trimmed = name.Trim();
            if (trimmed.Length == 0 || trimmed.Length > LongestName) return false;

            foreach (char c in trimmed)
                if (!char.IsLetterOrDigit(c) && c != ' ' && c != '-' && c != '_' &&
                    c != '(' && c != ')' && c != '+') return false;

            return true;
        }

        public static List<string> Names()
        {
            List<string> found = new List<string>();
            try
            {
                if (!Directory.Exists(Folder)) return found;
                foreach (string file in Directory.GetFiles(Folder, "*.ini"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (IsUsableName(name)) found.Add(name);
                }
                found.Sort(StringComparer.CurrentCultureIgnoreCase);
            }
            catch { }
            return found;
        }

        static string PathFor(string name)
        {
            return Path.Combine(Folder, name.Trim() + ".ini");
        }

        public static bool Exists(string name)
        {
            return IsUsableName(name) && File.Exists(PathFor(name));
        }

        public static bool Save(string name, StudioSettings live)
        {
            if (!IsUsableName(name) || live == null) return false;
            try
            {
                Directory.CreateDirectory(Folder);
                live.Save(PathFor(name));
                return true;
            }
            catch { return false; }
        }

        public static bool Delete(string name)
        {
            if (!IsUsableName(name)) return false;
            try { File.Delete(PathFor(name)); return true; }
            catch { return false; }
        }

        /// <summary>
        /// Reads a preset and copies the job out of it onto the live settings.
        /// Returns false when there is nothing by that name.
        /// </summary>
        public static bool Apply(string name, StudioSettings onto)
        {
            if (!Exists(name) || onto == null) return false;
            try
            {
                ApplyJob(StudioSettings.Load(PathFor(name)), onto);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Puts the job back to its defaults, leaving the machine alone.
        ///
        /// The application does not start where it left off. Adjustments are the
        /// reason: a page looks the way it does because of settings that are
        /// invisible once the panel is scrolled past, and the failure they cause
        /// is silent. Set Vivid and a heavy saturation for one job, come back the
        /// next morning, and every scan of the day is wrong without anything on
        /// screen having changed.
        ///
        /// So each run begins neutral, and a preset is how you get back to a
        /// configured state -- which is what presets are for, and why this is
        /// worth doing only now that they exist.
        ///
        /// What resets and what survives is not a second list: it is exactly the
        /// split ApplyJob already draws. The scanner and the watched folder are
        /// the machine, not the job, so they are still there in the morning.
        /// </summary>
        public static void ResetJob(StudioSettings live)
        {
            ApplyJob(new StudioSettings(), live);
        }

        /// <summary>
        /// Copies the fields that describe a job, and only those.
        ///
        /// A preset is written by saving the whole settings file, so it contains
        /// the scanner that happened to be selected and the watched folder that
        /// happened to be set. Neither belongs to the job:
        ///
        ///   the scanner      recalling "NID card" must not silently move the
        ///                    work to a different device, and a preset copied to
        ///                    another machine would name one that is not there
        ///   the hot folder   a watched folder is a standing arrangement, not
        ///                    something to recall; repointing a running watcher
        ///                    from a dropdown is how files end up in the wrong
        ///                    place with nobody having asked for it
        ///
        /// Listing the job fields by hand rather than copying everything and
        /// subtracting is deliberate. A field added to StudioSettings later will
        /// be left out of presets until somebody adds it here, which is a
        /// missing feature; the other way round, it would silently start
        /// overwriting the operator's scanner, which is a bug.
        /// </summary>
        public static void ApplyJob(StudioSettings from, StudioSettings to)
        {
            if (from == null || to == null) return;

            // ---- what is captured ----
            to.Dpi = from.Dpi;
            to.Mode = from.Mode;
            to.Source = from.Source;
            to.PaperSize = from.PaperSize;
            to.PaperLandscape = from.PaperLandscape;
            to.PageCount = from.PageCount;
            to.ShowVendorUi = from.ShowVendorUi;

            to.RegionLeftIn = from.RegionLeftIn;
            to.RegionTopIn = from.RegionTopIn;
            to.RegionWidthIn = from.RegionWidthIn;
            to.RegionHeightIn = from.RegionHeightIn;
            to.CropNorm = from.CropNorm;

            // ---- how the preview is taken ----
            to.PreviewDpi = from.PreviewDpi;
            to.PreviewMode = from.PreviewMode;
            to.PreviewMatchesScan = from.PreviewMatchesScan;

            // ---- what is found on the glass ----
            to.AutoCrop = from.AutoCrop;
            to.MultiRegionCrop = from.MultiRegionCrop;
            to.AutoDeskew = from.AutoDeskew;
            to.DeskewProfile = from.DeskewProfile;
            to.UseModel = from.UseModel;
            to.Polish = from.Polish;

            // ---- how it is made to look ----
            to.Tone = from.Tone;
            to.Brightness = from.Brightness;
            to.Contrast = from.Contrast;
            to.BwThreshold = from.BwThreshold;
            to.AdaptiveThreshold = from.AdaptiveThreshold;
            to.AutoTone = from.AutoTone;
            to.Saturation = from.Saturation;
            to.Vibrance = from.Vibrance;
            to.Temperature = from.Temperature;
            to.Tint = from.Tint;
            to.Highlights = from.Highlights;
            to.Shadows = from.Shadows;
            to.DescreenLpi = from.DescreenLpi;
            to.Sharpen = from.Sharpen;
            to.BackgroundClean = from.BackgroundClean;
            to.Despeckle = from.Despeckle;
            to.CurvePointsRGB = Copy(from.CurvePointsRGB);
            to.CurvePointsR = Copy(from.CurvePointsR);
            to.CurvePointsG = Copy(from.CurvePointsG);
            to.CurvePointsB = Copy(from.CurvePointsB);

            // ---- where it goes ----
            to.OutputFormat = from.OutputFormat;
            to.OutputDirectory = from.OutputDirectory;
            to.OutputNamePattern = from.OutputNamePattern;
            to.JpegQuality = from.JpegQuality;
            to.MultiPageFile = from.MultiPageFile;
            to.OpenInPhotoshop = from.OpenInPhotoshop;
            to.KeepFiles = from.KeepFiles;

            // ---- how a stack is divided up ----
            to.BatchSeparation = from.BatchSeparation;
            to.PagesPerDocument = from.PagesPerDocument;
            to.DropBlankPages = from.DropBlankPages;
            to.BatchUntilEmpty = from.BatchUntilEmpty;
        }

        /// <summary>
        /// Checks ApplyJob against every field StudioSettings actually has.
        ///
        /// ApplyJob names its fields one by one, which is the right way round --
        /// copying everything and subtracting would silently overwrite the
        /// operator's scanner the day somebody adds a field. But it has one
        /// failure mode: a field added later is simply forgotten, presets stop
        /// carrying it, and nothing says so.
        ///
        /// So this does not check a list of fields somebody wrote down. It reads
        /// the fields off StudioSettings by reflection, gives each one a value
        /// that differs between two objects, runs ApplyJob, and sees which ones
        /// moved. Anything new is required to move unless it has been named in
        /// NotPartOfAJob -- so adding a field without deciding fails here rather
        /// than disappearing.
        /// </summary>
        public static int SelfTest(Action<string> say)
        {
            StudioSettings from = new StudioSettings();
            StudioSettings to = new StudioSettings();

            FieldInfo[] fields = typeof(StudioSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);
            List<FieldInfo> testable = new List<FieldInfo>();

            foreach (FieldInfo f in fields)
            {
                object a, b;
                if (!TwoValues(f.FieldType, out a, out b))
                {
                    say(string.Format("  {0,-26} skipped, no two values of {1}", f.Name, f.FieldType.Name));
                    continue;
                }
                f.SetValue(from, a);
                f.SetValue(to, b);
                testable.Add(f);
            }

            ApplyJob(from, to);

            int wrong = 0;
            foreach (FieldInfo f in testable)
            {
                bool carried = Same(f.GetValue(from), f.GetValue(to));
                bool shouldCarry = Array.IndexOf(NotPartOfAJob, f.Name) < 0;

                if (carried == shouldCarry)
                {
                    say(string.Format("  {0,-26} {1}", f.Name, shouldCarry ? "carried" : "left alone"));
                    continue;
                }

                wrong++;
                say(string.Format("  {0,-26} {1}", f.Name, shouldCarry
                    ? "NOT CARRIED - add it to ApplyJob, or name it in NotPartOfAJob"
                    : "CARRIED - it is named in NotPartOfAJob and must not be"));
            }

            // ---- and the same split, used the other way round ----
            // ResetJob is ApplyJob from a fresh object, so it inherits the
            // classification rather than repeating it. This checks that it
            // really does: the job goes back to nothing, the machine does not.
            StudioSettings used = new StudioSettings();
            foreach (FieldInfo f in testable)
            {
                object a, b;
                if (TwoValues(f.FieldType, out a, out b)) f.SetValue(used, b);
            }

            StudioSettings fresh = new StudioSettings();
            ResetJob(used);

            foreach (FieldInfo f in testable)
            {
                bool machine = Array.IndexOf(NotPartOfAJob, f.Name) >= 0;
                bool back = Same(f.GetValue(used), f.GetValue(fresh));

                if (machine && back)
                {
                    wrong++;
                    say(string.Format("  {0,-26} {1}", f.Name, "RESET - it is the machine and must survive a restart"));
                }
                else if (!machine && !back)
                {
                    wrong++;
                    say(string.Format("  {0,-26} {1}", f.Name, "NOT RESET - it would carry over into the next session"));
                }
            }

            say("");
            say(wrong == 0
                ? "  ok: " + testable.Count + " fields, carried and reset as they should be"
                : "  FAILED: " + wrong + " field(s) on the wrong side");
            return wrong == 0 ? 0 : 1;
        }

        /// <summary>Two distinguishable values of a type, or false if it has none.</summary>
        static bool TwoValues(Type t, out object a, out object b)
        {
            a = null; b = null;

            if (t == typeof(string)) { a = "one"; b = "two"; return true; }
            if (t == typeof(int)) { a = 11; b = 22; return true; }
            if (t == typeof(double)) { a = 1.5; b = 2.5; return true; }
            if (t == typeof(float)) { a = 1.5f; b = 2.5f; return true; }
            if (t == typeof(bool)) { a = false; b = true; return true; }

            if (t == typeof(RectangleF))
            {
                a = new RectangleF(0.1f, 0.2f, 0.3f, 0.4f);
                b = new RectangleF(0.5f, 0.6f, 0.7f, 0.8f);
                return true;
            }

            if (t == typeof(List<PointF>))
            {
                a = new List<PointF> { new PointF(1, 1) };
                b = new List<PointF> { new PointF(2, 2) };
                return true;
            }

            if (t.IsEnum)
            {
                Array values = Enum.GetValues(t);
                if (values.Length < 2) return false;      // nothing to tell apart
                a = values.GetValue(0);
                b = values.GetValue(1);
                return true;
            }

            return false;
        }

        static bool Same(object a, object b)
        {
            List<PointF> pa = a as List<PointF>, pb = b as List<PointF>;
            if (pa != null && pb != null)
            {
                if (pa.Count != pb.Count) return false;
                for (int i = 0; i < pa.Count; i++) if (pa[i] != pb[i]) return false;
                return true;
            }
            return Equals(a, b);
        }

        // Copied rather than shared: two settings objects holding the same list
        // means editing a curve in one edits it in the other.
        static List<System.Drawing.PointF> Copy(List<System.Drawing.PointF> points)
        {
            return points == null
                ? new List<System.Drawing.PointF>()
                : new List<System.Drawing.PointF>(points);
        }
    }
}
