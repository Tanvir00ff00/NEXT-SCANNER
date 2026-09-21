// =============================================================================
// NextScan Studio - tone rendering: presets, paper polish, adaptive threshold
// Plan ref: MASTER_PLAN section 11 (image pipeline), 13 (studio controls).
//
// This replaces the tone code that lived inside the shell. It is in Core so the
// rendering can be tested against real numbers - "the whites are blowing out" is
// not something a screenshot settles.
//
// The defect it exists to fix: paper whitening used to add the same value to R,
// G and B for every pixel brighter than a fixed luminance of 170. Three things
// were wrong with that, and all three were visible on real scans:
//
//   * A hard cutoff at one luminance leaves a contour line across any smooth
//     gradient - one side lifted, the other not.
//   * Adding an equal amount to three channels changes their ratios, so a warm
//     off-white walks towards neutral. Colours looked "right" overall while
//     light areas quietly lost their tint.
//   * Nothing stopped a channel reaching 255, so highlight detail was destroyed
//     rather than brightened.
//
// The replacement multiplies instead of adding (ratios, and therefore hue, are
// preserved), fades in smoothly (no contour), and caps the multiplier so no
// channel can be pushed to clipping.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NextScan.Core
{
    public enum TonePreset
    {
        /// <summary>What the sensor saw. The default, and deliberately dull.</summary>
        OriginalColour = 0,
        Photo = 1,
        DocumentColour = 2,
        Greyscale = 3,
        BlackAndWhiteText = 4,
        FadedRestore = 5,
        NegativeInvert = 6,

        // Added in 1.2. Numbered, never reordered: the settings file stores the
        // number, so renumbering these would silently change every saved job.
        AutoEnhance = 7,
        IdPortrait = 8,
        VividColour = 9,
        SoftColour = 10,
        WarmPaper = 11,
        CoolClean = 12,
        InkBoost = 13,
        OldPhotoRestore = 14,
        PrintedPhoto = 15,
        CleanDocument = 16,
    }

    /// <summary>
    /// How much the page is allowed to decide for itself.
    ///
    /// Everything else in ToneSettings is a number the operator chose. These are
    /// the settings that read the page first and then choose, which is the whole
    /// difference between a slider and an automatic correction.
    /// </summary>
    public enum AutoTone
    {
        /// <summary>Nothing is measured. The default.</summary>
        Off = 0,

        /// <summary>
        /// Stretch the tonal range to fill it, using one measurement across all
        /// three channels so colour is left exactly as it was found.
        /// </summary>
        Contrast = 1,

        /// <summary>
        /// Stretch each channel on its own. This removes a colour cast -- the
        /// yellow of an old lamp, the blue of a cold one -- because a cast is a
        /// channel whose range does not reach as far as the others.
        /// </summary>
        Colour = 2,

        /// <summary>
        /// Per channel, and then pull the midtones towards neutral as well.
        /// Strongest, and the one that can be wrong: a photograph that is
        /// genuinely mostly one colour has its cast removed along with its
        /// subject.
        /// </summary>
        Full = 3,
    }

    public class TonePresetInfo
    {
        public TonePreset Preset;
        public string Name = "";
        public string Description = "";
        /// <summary>Paper polish this preset wants, 0..100.</summary>
        public int Polish;
        public int Contrast;

        // What the preset starts the newer controls at. Left at zero by every
        // preset that predates them, so none of them changed behaviour.
        public AutoTone Auto = AutoTone.Off;
        public int Saturation;
        public int Vibrance;
        public int Temperature;
        public int Tint;
        public int Highlights;
        public int Shadows;
        public int DescreenLpi;
        public int Sharpen;
        public int BackgroundClean;
        public int Despeckle;
    }

    public class ToneSettings
    {
        public TonePreset Preset = TonePreset.OriginalColour;

        /// <summary>
        /// Paper polish, 0..100. Lifts near-white towards white without shifting
        /// hue or clipping. 0 is entirely untouched.
        /// </summary>
        public int Polish = 8;

        public int Brightness;      // -100..100
        public int Contrast;        // -100..100

        /// <summary>Global threshold, used only when adaptive thresholding is off.</summary>
        public int BwThreshold = 128;

        /// <summary>
        /// Decide the black/white boundary from each pixel's surroundings rather
        /// than from one number for the whole page.
        /// </summary>
        public bool AdaptiveThreshold = true;

        // ---- added in 1.2 ----

        /// <summary>What the page is allowed to work out for itself.</summary>
        public AutoTone Auto = AutoTone.Off;

        /// <summary>-100..100. Every colour, equally.</summary>
        public int Saturation;

        /// <summary>
        /// -100..100. Saturates the colours that are not saturated yet, and
        /// leaves the ones that already are. On a portrait this is the
        /// difference between a face with more colour in it and a face that has
        /// turned orange.
        /// </summary>
        public int Vibrance;

        /// <summary>-100..100, cool to warm.</summary>
        public int Temperature;

        /// <summary>-100..100, green to magenta. The other axis of a cast.</summary>
        public int Tint;

        /// <summary>-100..100. Negative pulls detail back out of bright areas.</summary>
        public int Highlights;

        /// <summary>-100..100. Positive opens up what is hiding in the dark.</summary>
        public int Shadows;

        // ---- the clean-up pass, 1.2 ----
        // These are the ones that need to see a pixel's neighbours, which is why
        // they run as their own pass rather than through the curve.

        /// <summary>
        /// Screen ruling of the original, in lines per inch, or 0 for off.
        ///
        /// Printed matter is not continuous tone: it is a grid of dots, and
        /// scanning that grid on another grid produces moire. Removing it means
        /// blurring at exactly the dot pitch, which is why the number that
        /// matters is the printing screen and not a strength.
        ///
        /// Newspaper is about 85, magazines about 133, good art printing 175.
        /// </summary>
        public int DescreenLpi;

        /// <summary>0..100. Unsharp mask. Always applied last; see ApplyCleanup.</summary>
        public int Sharpen;

        /// <summary>
        /// 0..100. Flattens the paper and pushes anything close to it white,
        /// which is what takes out uneven lighting and the ghost of print on the
        /// other side of a thin page.
        /// </summary>
        public int BackgroundClean;

        /// <summary>0..100. Removes isolated specks without softening edges.</summary>
        public int Despeckle;

        public ToneSettings Clone()
        {
            return new ToneSettings
            {
                Preset = Preset,
                Polish = Polish,
                Brightness = Brightness,
                Contrast = Contrast,
                BwThreshold = BwThreshold,
                AdaptiveThreshold = AdaptiveThreshold,
                Auto = Auto,
                Saturation = Saturation,
                Vibrance = Vibrance,
                Temperature = Temperature,
                Tint = Tint,
                Highlights = Highlights,
                Shadows = Shadows,
                DescreenLpi = DescreenLpi,
                Sharpen = Sharpen,
                BackgroundClean = BackgroundClean,
                Despeckle = Despeckle
            };
        }
    }

    public static class ToneEngine
    {
        // =====================================================================
        // The presets
        // =====================================================================
        /// <summary>
        /// In display order. The descriptions are shown in the UI, so they say
        /// what the preset is for rather than what it does numerically.
        /// </summary>
        public static readonly TonePresetInfo[] Presets = new TonePresetInfo[]
        {
            new TonePresetInfo {
                Preset = TonePreset.OriginalColour, Name = "Original colour",
                Description = "Accurate. What the scanner saw, with only a light paper polish.",
                Polish = 8, Contrast = 0 },

            new TonePresetInfo {
                Preset = TonePreset.Photo, Name = "Photo",
                Description = "Gentle contrast for prints and photographs. No paper lift.",
                Polish = 0, Contrast = 0 },

            new TonePresetInfo {
                Preset = TonePreset.DocumentColour, Name = "Document colour",
                Description = "Clean white paper, colour kept for stamps, logos and signatures.",
                Polish = 55, Contrast = 12 },

            new TonePresetInfo {
                Preset = TonePreset.Greyscale, Name = "Greyscale",
                Description = "Neutral grey with a clean background. Small files, readable text.",
                Polish = 30, Contrast = 8 },

            new TonePresetInfo {
                Preset = TonePreset.BlackAndWhiteText, Name = "Black and white text",
                Description = "Two tones, thresholded per area so shadows and gutters stay readable.",
                Polish = 0, Contrast = 0 },

            new TonePresetInfo {
                Preset = TonePreset.FadedRestore, Name = "Faded document",
                Description = "Restores contrast on old or faint paper by finding its real range.",
                Polish = 20, Contrast = 0 },

            new TonePresetInfo {
                Preset = TonePreset.NegativeInvert, Name = "Negative invert",
                Description = "Inverts the image, for film negatives.",
                Polish = 0, Contrast = 0 },

            // ---- added in 1.2 ----

            new TonePresetInfo {
                Preset = TonePreset.AutoEnhance, Name = "Auto enhance",
                Description = "Reads the page, then sets its own range, cast and midtones. Start here when unsure.",
                Polish = 10, Contrast = 0, Auto = AutoTone.Full, Vibrance = 12, Shadows = 8 },

            new TonePresetInfo {
                Preset = TonePreset.IdPortrait, Name = "ID photo",
                Description = "For faces on cards and passports. Opens shadows and adds colour without turning skin orange.",
                Polish = 0, Contrast = 6, Auto = AutoTone.Contrast,
                Vibrance = 20, Saturation = -4, Temperature = 4, Shadows = 16, Highlights = -8 },

            new TonePresetInfo {
                Preset = TonePreset.VividColour, Name = "Vivid",
                Description = "Deeper colour and firmer contrast. For artwork, labels and printed photographs.",
                Polish = 0, Contrast = 14, Saturation = 22, Vibrance = 10 },

            new TonePresetInfo {
                Preset = TonePreset.SoftColour, Name = "Soft",
                Description = "Flatter and gentler, holding both ends. For originals that will be edited afterwards.",
                Polish = 0, Contrast = -12, Highlights = -14, Shadows = 12 },

            new TonePresetInfo {
                Preset = TonePreset.WarmPaper, Name = "Warm paper",
                Description = "Keeps the warmth of aged paper instead of bleaching it to white.",
                Polish = 18, Contrast = 8, Temperature = 14, Vibrance = 6 },

            new TonePresetInfo {
                Preset = TonePreset.CoolClean, Name = "Cool clean",
                Description = "Neutral white paper with any yellow lamp cast taken out.",
                Polish = 45, Contrast = 10, Auto = AutoTone.Colour, Temperature = -8 },

            new TonePresetInfo {
                Preset = TonePreset.InkBoost, Name = "Ink boost",
                Description = "Deepens print and stamps while leaving paper where it is. For faint ink and carbon copies.",
                Polish = 35, Contrast = 18, Shadows = -18, Saturation = 8 },

            new TonePresetInfo {
                Preset = TonePreset.OldPhotoRestore, Name = "Old photo restore",
                Description = "Removes the cast of an aged print, recovers its range, and lifts what is lost in the dark.",
                Polish = 0, Contrast = 6, Auto = AutoTone.Full,
                Vibrance = 26, Shadows = 20, Highlights = -12 },

            new TonePresetInfo {
                Preset = TonePreset.PrintedPhoto, Name = "Printed photo",
                Description = "For photographs cut from magazines or books. Removes the printing dots, then sharpens what is underneath.",
                Polish = 0, Contrast = 8, Auto = AutoTone.Contrast,
                Vibrance = 14, DescreenLpi = 133, Sharpen = 45 },

            new TonePresetInfo {
                Preset = TonePreset.CleanDocument, Name = "Clean document",
                Description = "Flattens the paper, clears the print showing through from the back, and removes specks.",
                Polish = 20, Contrast = 14, Auto = AutoTone.Colour,
                BackgroundClean = 65, Despeckle = 40, Sharpen = 25 },
        };

        public static TonePresetInfo Info(TonePreset p)
        {
            foreach (TonePresetInfo i in Presets) if (i.Preset == p) return i;
            return Presets[0];
        }

        /// <summary>Settings a preset starts from when the operator picks it.</summary>
        public static ToneSettings Defaults(TonePreset p)
        {
            TonePresetInfo info = Info(p);
            return new ToneSettings
            {
                Preset = p,
                Polish = info.Polish,
                Contrast = info.Contrast,
                Brightness = 0,
                Auto = info.Auto,
                Saturation = info.Saturation,
                Vibrance = info.Vibrance,
                Temperature = info.Temperature,
                Tint = info.Tint,
                Highlights = info.Highlights,
                Shadows = info.Shadows,
                DescreenLpi = info.DescreenLpi,
                Sharpen = info.Sharpen,
                BackgroundClean = info.BackgroundClean,
                Despeckle = info.Despeckle
            };
        }

        // =====================================================================
        // Tone curve
        // =====================================================================
        /// <summary>
        /// The per-channel curve for a preset, before anything that needs to see
        /// the whole image. Identity for Original colour: a default that alters
        /// pixels is a default that has to be argued with.
        /// </summary>
        public static byte[] BuildLut(ToneSettings s)
        {
            double[] v = new double[256];
            for (int i = 0; i < 256; i++) v[i] = i / 255.0;

            switch (s.Preset)
            {
                case TonePreset.Photo:
                    // A shallow S: a little more contrast through the midtones
                    // while both ends stay put, so nothing is crushed or blown.
                    for (int i = 0; i < 256; i++) v[i] = SCurve(v[i], 0.18);
                    break;

                case TonePreset.DocumentColour:
                    // Print sits well below paper white, so the top of the range
                    // is where the useful separation is.
                    for (int i = 0; i < 256; i++) v[i] = SCurve(v[i], 0.10);
                    break;

                case TonePreset.NegativeInvert:
                    for (int i = 0; i < 256; i++) v[i] = 1.0 - v[i];
                    break;
            }

            // Manual adjustment last, so the sliders behave the same whichever
            // preset is selected.
            double contrast = (s.Contrast + Info(s.Preset).Contrast) / 100.0;
            double brightness = s.Brightness / 100.0 * 0.5;

            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double x = v[i];
                if (Math.Abs(contrast) > 0.0001) x = SCurve(x, contrast);
                x += brightness;
                lut[i] = Clamp255(x * 255.0);
            }
            return lut;
        }

        /// <summary>
        /// A contrast curve that is flat at both ends, so it never clips. Positive
        /// amount steepens the middle, negative flattens it.
        /// </summary>
        static double SCurve(double x, double amount)
        {
            if (x <= 0.0) return 0.0;
            if (x >= 1.0) return 1.0;
            if (Math.Abs(amount) < 1e-6) return x;

            // Smoothstep blended with the identity by "amount". Blending rather
            // than replacing keeps the curve monotonic for every amount in range,
            // which is what stops banding.
            double smooth = x * x * (3.0 - 2.0 * x);
            double a = Math.Max(-1.0, Math.Min(1.0, amount));
            return a >= 0 ? x + (smooth - x) * a : x - (smooth - x) * (-a);
        }

        static byte Clamp255(double v)
        {
            int i = (int)Math.Round(v);
            return (byte)(i < 0 ? 0 : (i > 255 ? 255 : i));
        }

        // =====================================================================
        // Paper polish
        // =====================================================================
        /// <summary>
        /// Lifts near-white towards white. Multiplying rather than adding keeps
        /// the ratio between channels - and therefore the hue - exactly as it
        /// was; the multiplier is capped so no channel can reach clipping; and
        /// the effect fades in over a range instead of switching on at one
        /// luminance, which is what leaves a contour across a gradient.
        /// </summary>
        public static void Polish(ref int r, ref int g, ref int b, double amount)
        {
            if (amount <= 0.0) return;

            double lum = (r * 0.299 + g * 0.587 + b * 0.114);
            if (lum <= 1.0) return;

            // Nothing below 55% brightness is paper; everything above 96% is
            // already white. Between those the effect ramps in smoothly.
            double t = (lum / 255.0 - 0.55) / (0.96 - 0.55);
            if (t <= 0.0) return;
            if (t > 1.0) t = 1.0;
            double weight = t * t * (3.0 - 2.0 * t);

            double target = lum + amount * weight * (255.0 - lum);
            double scale = target / lum;

            // Polish cleans paper; it does not manufacture pure white out of
            // pixels that still had detail in them. A channel that arrived below
            // 255 must still be below 255 afterwards, so the ceiling is one level
            // short of white - and capping the scale, rather than clamping the
            // result, keeps the three channels in exactly the same ratio.
            int maxChannel = r > g ? (r > b ? r : b) : (g > b ? g : b);
            double ceiling = maxChannel < 255 ? 254.4 : 255.0;
            if (maxChannel > 0 && scale * maxChannel > ceiling) scale = ceiling / maxChannel;
            if (scale <= 1.0) return;

            r = (int)Math.Round(r * scale);
            g = (int)Math.Round(g * scale);
            b = (int)Math.Round(b * scale);
            if (r > 255) r = 255;
            if (g > 255) g = 255;
            if (b > 255) b = 255;
        }

        // =====================================================================
        // Applying it to a page
        // =====================================================================
        // =====================================================================
        // Reading the page before correcting it
        // =====================================================================

        /// <summary>
        /// What Analyse found: where each channel actually starts and stops, and
        /// how far its midtones sit from neutral.
        /// </summary>
        public class AutoPlan
        {
            public bool Active;
            public int[] Low = { 0, 0, 0 };          // R, G, B
            public int[] High = { 255, 255, 255 };
            public double[] Gamma = { 1.0, 1.0, 1.0 };
        }

        /// <summary>Ignored at each end when deciding where the range really is.</summary>
        const double ClipFraction = 0.005;

        /// <summary>
        /// Measures the page.
        ///
        /// A scan almost never uses the full range: paper is not 255 and print is
        /// not 0, and a warm lamp leaves the blue channel short at the top. Both
        /// are visible in the histogram as a channel that stops early, which is
        /// why one measurement answers both questions.
        ///
        /// The extremes are ignored rather than trusted -- half a percent at each
        /// end -- because one dust speck at 255 would otherwise be the whole
        /// page's white point, and one black scanner edge its black point.
        ///
        /// Sampled rather than exhaustive on anything large. A histogram of every
        /// fourth pixel of a 300 dpi page is built from over a million samples,
        /// which settles the percentiles to well inside a level.
        /// </summary>
        public static AutoPlan Analyse(Bitmap src, AutoTone mode)
        {
            AutoPlan plan = new AutoPlan();
            if (src == null || mode == AutoTone.Off) return plan;

            int w = src.Width, h = src.Height;
            if (w < 8 || h < 8) return plan;

            int step = 1;
            while ((long)(w / step) * (h / step) > 1200000 && step < 8) step++;

            int[][] histogram = new int[3][];
            for (int c = 0; c < 3; c++) histogram[c] = new int[256];
            int[] luma = new int[256];
            long counted = 0;

            BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                                         PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* baseP = (byte*)sd.Scan0;
                    for (int y = 0; y < h; y += step)
                    {
                        byte* row = baseP + (long)y * sd.Stride;
                        for (int x = 0; x < w; x += step)
                        {
                            byte* q = row + (long)x * 3;
                            int b = q[0], g = q[1], r = q[2];
                            histogram[0][r]++; histogram[1][g]++; histogram[2][b]++;
                            luma[(r * 299 + g * 587 + b * 114) / 1000]++;
                            counted++;
                        }
                    }
                }
            }
            finally { src.UnlockBits(sd); }

            if (counted < 64) return plan;

            if (mode == AutoTone.Contrast)
            {
                // One range for all three, so the stretch cannot move a colour.
                int lo, hi;
                Percentiles(luma, counted, out lo, out hi);
                if (hi - lo < 8) return plan;
                for (int c = 0; c < 3; c++) { plan.Low[c] = lo; plan.High[c] = hi; }
                plan.Active = true;
                return plan;
            }

            // Per channel: a cast is a channel that does not reach as far as the
            // others, so giving each its own range is what removes it.
            for (int c = 0; c < 3; c++)
            {
                int lo, hi;
                Percentiles(histogram[c], counted, out lo, out hi);
                if (hi - lo < 8) return plan;          // flat channel: leave everything alone
                plan.Low[c] = lo; plan.High[c] = hi;
            }
            plan.Active = true;

            if (mode != AutoTone.Full) return plan;

            // Midtones as well. Each channel is bent until its average lands on
            // the average of all three, which is the grey world assumption: over
            // a whole page the colours average out to something neutral.
            double[] mean = new double[3];
            for (int c = 0; c < 3; c++)
            {
                double sum = 0;
                for (int i = 0; i < 256; i++)
                {
                    double stretched = (i - plan.Low[c]) / (double)(plan.High[c] - plan.Low[c]);
                    sum += Math.Max(0.0, Math.Min(1.0, stretched)) * histogram[c][i];
                }
                mean[c] = sum / counted;
            }

            double target = (mean[0] + mean[1] + mean[2]) / 3.0;
            if (target < 0.02 || target > 0.98) return plan;    // nearly black or blown: no midtone to speak of

            for (int c = 0; c < 3; c++)
            {
                if (mean[c] < 0.02 || mean[c] > 0.98) continue;
                double gamma = Math.Log(target) / Math.Log(mean[c]);

                // Bounded hard. An unbounded gamma turns a page that really is
                // mostly one colour into a grey one, and that is a correction
                // nobody asked for.
                plan.Gamma[c] = Math.Max(0.6, Math.Min(1.6, gamma));
            }
            return plan;
        }

        static void Percentiles(int[] histogram, long total, out int low, out int high)
        {
            long want = (long)(total * ClipFraction);
            long seen = 0;

            low = 0;
            for (int i = 0; i < 256; i++)
            {
                seen += histogram[i];
                if (seen > want) { low = i; break; }
            }

            seen = 0;
            high = 255;
            for (int i = 255; i >= 0; i--)
            {
                seen += histogram[i];
                if (seen > want) { high = i; break; }
            }

            if (high <= low) { low = 0; high = 255; }
        }

        // =====================================================================
        // The curve, once per channel
        // =====================================================================

        /// <summary>
        /// One curve per channel: the measured stretch, then the preset and the
        /// sliders, then the things that have to be per channel.
        ///
        /// BuildLut is still the single source of what a preset does, called
        /// through here rather than reimplemented, so the two cannot drift.
        /// </summary>
        public static byte[][] BuildChannelLuts(ToneSettings s, AutoPlan plan)
        {
            if (s == null) s = new ToneSettings();
            byte[] shared = BuildLut(s);

            double shadows = s.Shadows / 100.0;
            double highlights = s.Highlights / 100.0;
            double warm = s.Temperature / 100.0;
            double tint = s.Tint / 100.0;

            byte[][] luts = new byte[3][];
            for (int c = 0; c < 3; c++)
            {
                double gain = 1.0;
                if (c == 0) gain += warm * 0.16 + tint * 0.07;
                else if (c == 1) gain += -tint * 0.10;
                else gain += -warm * 0.16 + tint * 0.07;

                byte[] lut = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    double x = i / 255.0;

                    if (plan != null && plan.Active)
                    {
                        double lo = plan.Low[c] / 255.0, hi = plan.High[c] / 255.0;
                        if (hi - lo > 0.02) x = (x - lo) / (hi - lo);
                        x = Math.Max(0.0, Math.Min(1.0, x));
                        if (Math.Abs(plan.Gamma[c] - 1.0) > 0.001) x = Math.Pow(x, plan.Gamma[c]);
                    }

                    // The preset and the manual sliders, through the one curve
                    // that defines them.
                    x = shared[(int)(Math.Max(0.0, Math.Min(1.0, x)) * 255.0 + 0.5)] / 255.0;

                    // Weighted towards the end each one belongs to, so lifting
                    // shadows does not wash out paper and pulling highlights
                    // back does not muddy ink.
                    if (Math.Abs(shadows) > 0.0001)
                    {
                        double weight = (1.0 - x) * (1.0 - x);
                        x += shadows * weight * 0.45;
                    }
                    if (Math.Abs(highlights) > 0.0001)
                    {
                        double weight = x * x;
                        x += highlights * weight * 0.45;
                    }

                    lut[i] = Clamp255(x * gain * 255.0);
                }
                luts[c] = lut;
            }
            return luts;
        }

        /// <summary>
        /// Saturation and vibrance, which cannot be done in a per-channel curve:
        /// both need to know how colourful the pixel already is, and a curve only
        /// ever sees one channel at a time.
        /// </summary>
        static void Colourise(ref int r, ref int g, ref int b, double saturation, double vibrance)
        {
            int high = r > g ? (r > b ? r : b) : (g > b ? g : b);
            int low = r < g ? (r < b ? r : b) : (g < b ? g : b);

            double already = high <= 0 ? 0.0 : (high - low) / (double)high;

            // Vibrance spends itself on the colours that have least, which is why
            // it can be pushed much further than saturation before skin goes.
            double amount = saturation + vibrance * (1.0 - already);
            if (Math.Abs(amount) < 0.0001) return;

            double grey = (r * 299 + g * 587 + b * 114) / 1000.0;
            r = Clamp255(grey + (r - grey) * (1.0 + amount));
            g = Clamp255(grey + (g - grey) * (1.0 + amount));
            b = Clamp255(grey + (b - grey) * (1.0 + amount));
        }

        // =====================================================================
        // The clean-up pass: everything that has to look at a pixel's neighbours
        // =====================================================================

        /// <summary>Is there anything for the clean-up pass to do?</summary>
        static bool NeedsCleanup(ToneSettings s)
        {
            return s.DescreenLpi > 0 || s.Sharpen > 0 || s.BackgroundClean > 0 || s.Despeckle > 0;
        }

        /// <summary>
        /// Runs the spatial filters over an already tone-corrected buffer, in the
        /// one order that works:
        ///
        ///   descreen  -  despeckle  -  background  -  sharpen
        ///
        /// Sharpening is last and that is not a preference. Sharpening a halftone
        /// before removing it amplifies the very dot grid the descreen is there to
        /// destroy, and the result is worse than doing neither. Every scanning
        /// guide that mentions the two says the same thing, and it is the one
        /// ordering constraint in this file that cannot be traded away.
        ///
        /// <paramref name="dpi"/> is the scan resolution, needed because a
        /// printed dot is a fixed size on paper and therefore a different number
        /// of pixels at every resolution.
        /// </summary>
        static void ApplyCleanup(byte[] rgb, int w, int h, ToneSettings s, double dpi)
        {
            if (dpi < 1.0) dpi = 300.0;

            if (s.DescreenLpi > 0)
            {
                // One dot of the printing screen, measured in scanned pixels.
                // Blur by about half of it: enough to lose the grid, not enough
                // to lose what the grid was drawing.
                double pitch = dpi / s.DescreenLpi;
                int radius = (int)Math.Round(pitch / 2.0);
                if (radius >= 1) Blur(rgb, w, h, Math.Min(radius, 6), 2);
            }

            if (s.Despeckle > 0) Despeckle(rgb, w, h, s.Despeckle);
            if (s.BackgroundClean > 0) FlattenBackground(rgb, w, h, s.BackgroundClean, dpi);
            if (s.Sharpen > 0) UnsharpMask(rgb, w, h, s.Sharpen);
        }

        /// <summary>
        /// Box blur, repeated. Two or three passes of a box are indistinguishable
        /// from a Gaussian, and a box blur costs the same per pixel whatever its
        /// radius because it slides a running total rather than summing a window.
        /// That matters here: the background estimate wants a radius of forty
        /// pixels on a page of nine million.
        /// </summary>
        static void Blur(byte[] rgb, int w, int h, int radius, int passes)
        {
            if (radius < 1 || w < 2 || h < 2) return;
            byte[] tmp = new byte[rgb.Length];
            for (int i = 0; i < passes; i++)
            {
                BlurRows(rgb, tmp, w, h, radius);
                BlurColumns(tmp, rgb, w, h, radius);
            }
        }

        static void BlurRows(byte[] src, byte[] dst, int w, int h, int radius)
        {
            int span = radius * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 3;
                for (int c = 0; c < 3; c++)
                {
                    // Edges repeat rather than fade: a window that ran off the
                    // page and counted zeros would draw a dark frame.
                    int sum = src[row + c] * (radius + 1);
                    for (int x = 1; x <= radius; x++) sum += src[row + Math.Min(x, w - 1) * 3 + c];

                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 3 + c] = (byte)(sum / span);
                        int add = src[row + Math.Min(x + radius + 1, w - 1) * 3 + c];
                        int drop = src[row + Math.Max(x - radius, 0) * 3 + c];
                        sum += add - drop;
                    }
                }
            }
        }

        static void BlurColumns(byte[] src, byte[] dst, int w, int h, int radius)
        {
            int span = radius * 2 + 1;
            int stride = w * 3;
            for (int x = 0; x < w; x++)
            {
                int col = x * 3;
                for (int c = 0; c < 3; c++)
                {
                    int sum = src[col + c] * (radius + 1);
                    for (int y = 1; y <= radius; y++) sum += src[Math.Min(y, h - 1) * stride + col + c];

                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col + c] = (byte)(sum / span);
                        int add = src[Math.Min(y + radius + 1, h - 1) * stride + col + c];
                        int drop = src[Math.Max(y - radius, 0) * stride + col + c];
                        sum += add - drop;
                    }
                }
            }
        }

        /// <summary>
        /// Unsharp mask: the difference between the page and a blurred copy of
        /// it is its detail, and adding some of that back is sharpening.
        /// </summary>
        static void UnsharpMask(byte[] rgb, int w, int h, int strength)
        {
            byte[] soft = (byte[])rgb.Clone();
            Blur(soft, w, h, 1, 2);

            double amount = strength / 100.0 * 1.6;
            for (int i = 0; i < rgb.Length; i++)
            {
                int detail = rgb[i] - soft[i];

                // Small differences are film grain and sensor noise, and
                // sharpening those is how a scan comes out looking gritty.
                if (detail > -3 && detail < 3) continue;

                rgb[i] = Clamp255(rgb[i] + detail * amount);
            }
        }

        /// <summary>
        /// Replaces a pixel with the median of its neighbours, but only where it
        /// disagrees with them sharply. A median everywhere would round off every
        /// corner of every letter; a median only at the outliers removes dust and
        /// leaves the text alone.
        /// </summary>
        static void Despeckle(byte[] rgb, int w, int h, int strength)
        {
            if (w < 3 || h < 3) return;

            byte[] src = (byte[])rgb.Clone();
            int stride = w * 3;
            int trigger = 90 - (int)(strength / 100.0 * 60.0);   // 90 down to 30
            int[] window = new int[9];

            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    int at = y * stride + x * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int centre = src[at + c];

                        // A speck is a pixel that lies outside everything around
                        // it. Finding that takes sixteen comparisons; finding the
                        // median takes a sort. So the cheap question is asked
                        // first, and on a real page it answers "not a speck" for
                        // almost every pixel, which is the difference between
                        // this filter costing 200 ms and costing 20.
                        //
                        // It is also the more correct test. A pixel on an edge
                        // sits between its neighbours and is left alone here,
                        // where sorting first and comparing to the median would
                        // have rounded the corner off every letter.
                        int low = 255, high = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int v = src[at + dy * stride + dx * 3 + c];
                                if (v < low) low = v;
                                if (v > high) high = v;
                            }

                        if (centre >= low && centre <= high) continue;
                        if (centre > low - trigger && centre < high + trigger) continue;

                        int k = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                window[k++] = src[at + dy * stride + dx * 3 + c];

                        Array.Sort(window);
                        rgb[at + c] = (byte)window[4];
                    }
                }
            }
        }

        /// <summary>
        /// Flattens the paper and clears what is faintly on it.
        ///
        /// A heavy blur of the page is a picture of its paper: text and detail
        /// are small and average away, while uneven lamp lighting and the broad
        /// grey of a shadow survive. Dividing the page by that estimate makes the
        /// paper the same shade everywhere.
        ///
        /// Then anything sitting within a margin of that local paper is taken to
        /// be paper. That margin is what removes print showing through from the
        /// other side of a thin page: it is faint by definition, which is exactly
        /// what makes it separable from the print on this side.
        /// </summary>
        static void FlattenBackground(byte[] rgb, int w, int h, int strength, double dpi)
        {
            byte[] paper = (byte[])rgb.Clone();

            // Wide enough that letters vanish into it, in inches rather than
            // pixels so it behaves the same at every resolution.
            int radius = (int)Math.Max(6, Math.Min(90, dpi * 0.12));
            Blur(paper, w, h, radius, 2);

            double margin = strength / 100.0 * 0.16;

            for (int i = 0; i < rgb.Length; i++)
            {
                int background = paper[i];
                if (background < 24) continue;      // genuinely dark: nothing to flatten

                double flattened = rgb[i] / (double)background;
                if (flattened > 1.0) flattened = 1.0;

                // Anything within the margin of local paper becomes paper.
                //
                // A gain on the top end, not a shift of the bottom one. Written
                // the other way round -- (v - margin) / (1 - margin) -- it does
                // the opposite of what it says: faint marks are pulled further
                // from white and come out more visible, not less.
                flattened /= (1.0 - margin);
                if (flattened > 1.0) flattened = 1.0;
                rgb[i] = Clamp255(flattened * 255.0);
            }
        }

        /// <summary>
        /// Renders a bitmap through a preset. Returns a new bitmap; the source is
        /// left alone so the operator can change their mind without rescanning.
        /// </summary>
        public static Bitmap Apply(Bitmap src, ToneSettings s, ColorMode mode)
        {
            if (src == null) return null;
            if (s == null) s = new ToneSettings();

            int w = src.Width, h = src.Height;
            Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            dst.SetResolution(src.HorizontalResolution, src.VerticalResolution);

            AutoPlan plan = Analyse(src, s.Auto);
            byte[][] luts = BuildChannelLuts(s, plan);
            byte[] lutR = luts[0], lutG = luts[1], lutB = luts[2];

            double saturation = s.Saturation / 100.0;
            double vibrance = s.Vibrance / 100.0;
            bool colouring = Math.Abs(saturation) > 0.0001 || Math.Abs(vibrance) > 0.0001;

            double polish = Math.Max(0, Math.Min(100, s.Polish)) / 100.0;

            // Presets that need to see the whole page before deciding anything.
            byte[] rangeLut = null;
            if (s.Preset == TonePreset.FadedRestore) rangeLut = BuildRangeLut(src);

            bool toGrey = s.Preset == TonePreset.Greyscale ||
                          mode == ColorMode.Gray8 || mode == ColorMode.Gray16;
            bool toBilevel = s.Preset == TonePreset.BlackAndWhiteText ||
                             mode == ColorMode.BlackWhite1;

            byte[] localThreshold = null;
            if (toBilevel && s.AdaptiveThreshold) localThreshold = BuildLocalThreshold(src);

            BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            BitmapData dd = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* sBase = (byte*)sd.Scan0;
                    byte* dBase = (byte*)dd.Scan0;

                    for (int y = 0; y < h; y++)
                    {
                        byte* sp = sBase + (long)y * sd.Stride;
                        byte* dp = dBase + (long)y * dd.Stride;

                        for (int x = 0; x < w; x++, sp += 3, dp += 3)
                        {
                            int b = sp[0], g = sp[1], r = sp[2];

                            if (rangeLut != null) { b = rangeLut[b]; g = rangeLut[g]; r = rangeLut[r]; }

                            b = lutB[b]; g = lutG[g]; r = lutR[r];

                            // Before polish: polish decides what counts as nearly
                            // white, and it should be judging the finished colour.
                            if (colouring) Colourise(ref r, ref g, ref b, saturation, vibrance);

                            if (polish > 0.0) Polish(ref r, ref g, ref b, polish);

                            dp[0] = (byte)b; dp[1] = (byte)g; dp[2] = (byte)r;
                        }
                    }
                }
            }
            finally
            {
                src.UnlockBits(sd);
                dst.UnlockBits(dd);
            }

            // Everything that needs neighbours, and then the decisions that have
            // to come after it. Thresholding a page before it has been cleaned
            // turns dust into ink and bleed-through into text, so this order is
            // the point of doing it in two passes rather than one.
            bool cleanup = NeedsCleanup(s);
            if (cleanup || toGrey || toBilevel)
            {
                byte[] pixels = ReadPixels(dst, w, h);

                if (cleanup) ApplyCleanup(pixels, w, h, s, src.HorizontalResolution);

                if (toBilevel || toGrey)
                    Flatten(pixels, w, h, s, toBilevel, localThreshold);

                WritePixels(dst, pixels, w, h);
            }

            return dst;
        }

        /// <summary>
        /// Grey or two tones, after the clean-up pass rather than during the
        /// per-pixel one.
        ///
        /// The adaptive threshold map is still measured from the original scan.
        /// That is a deliberate limit rather than an oversight: rebuilding it
        /// from the cleaned page would be more correct, and it would also change
        /// what every existing black-and-white preset produces.
        /// </summary>
        static void Flatten(byte[] rgb, int w, int h, ToneSettings s, bool toBilevel, byte[] localThreshold)
        {
            int stride = w * 3;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int at = row + x * 3;
                    int b = rgb[at], g = rgb[at + 1], r = rgb[at + 2];
                    int lum = (r * 299 + g * 587 + b * 114) / 1000;

                    byte v;
                    if (toBilevel)
                    {
                        int limit = localThreshold != null ? localThreshold[y * w + x] : s.BwThreshold;
                        v = lum < limit ? (byte)0 : (byte)255;
                    }
                    else v = (byte)lum;

                    rgb[at] = v; rgb[at + 1] = v; rgb[at + 2] = v;
                }
            }
        }

        static byte[] ReadPixels(Bitmap bmp, int w, int h)
        {
            byte[] rgb = new byte[w * h * 3];
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                                        PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride),
                                 rgb, y * w * 3, w * 3);
            }
            finally { bmp.UnlockBits(d); }
            return rgb;
        }

        static void WritePixels(Bitmap bmp, byte[] rgb, int w, int h)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly,
                                        PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(rgb, y * w * 3,
                                 new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), w * 3);
            }
            finally { bmp.UnlockBits(d); }
        }

        // =====================================================================
        // Faded paper: find the range the page actually uses
        // =====================================================================
        /// <summary>
        /// Faint documents occupy a narrow slice of the range - say 90 to 200 -
        /// so a fixed curve cannot help them. This finds the ends the page really
        /// uses and stretches them out, ignoring the brightest and darkest 0.5%
        /// so that a speck of dust or a hole punch does not set the limits.
        /// </summary>
        static byte[] BuildRangeLut(Bitmap src)
        {
            int[] hist = new int[256];
            int w = src.Width, h = src.Height;
            int step = Math.Max(1, (int)Math.Sqrt((double)w * h / 400000.0));
            long counted = 0;

            BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* baseP = (byte*)sd.Scan0;
                    for (int y = 0; y < h; y += step)
                    {
                        byte* p = baseP + (long)y * sd.Stride;
                        for (int x = 0; x < w; x += step)
                        {
                            byte* q = p + (long)x * 3;
                            hist[(q[2] * 299 + q[1] * 587 + q[0] * 114) / 1000]++;
                            counted++;
                        }
                    }
                }
            }
            finally { src.UnlockBits(sd); }

            byte[] lut = new byte[256];
            if (counted == 0)
            {
                for (int i = 0; i < 256; i++) lut[i] = (byte)i;
                return lut;
            }

            long tail = Math.Max(1, counted / 200);      // 0.5% each end
            int lo = 0, hi = 255;
            long acc = 0;
            for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= tail) { lo = i; break; } }
            acc = 0;
            for (int i = 255; i >= 0; i--) { acc += hist[i]; if (acc >= tail) { hi = i; break; } }

            // Too narrow a range means an almost blank page; stretching that
            // would amplify sensor noise into something that looks like text.
            if (hi - lo < 24)
            {
                for (int i = 0; i < 256; i++) lut[i] = (byte)i;
                return lut;
            }

            double span = hi - lo;
            for (int i = 0; i < 256; i++) lut[i] = Clamp255((i - lo) / span * 255.0);
            return lut;
        }

        // =====================================================================
        // Adaptive threshold
        // =====================================================================
        /// <summary>
        /// A threshold per pixel, taken from the average of its surroundings.
        ///
        /// One number for the whole page fails on everything real: the gutter
        /// shadow of a bound book, a lamp brighter on one side, a photocopy that
        /// darkens towards an edge. Those pages come out with a black band or a
        /// blank corner. Comparing each pixel with its own neighbourhood keeps
        /// text readable across all of them.
        ///
        /// Computed from a summed-area table so the window size costs nothing -
        /// the work is two passes over the page regardless of how wide the
        /// window is.
        /// </summary>
        public static byte[] BuildLocalThreshold(Bitmap src)
        {
            int w = src.Width, h = src.Height;
            byte[] grey = new byte[w * h];

            BitmapData sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    byte* baseP = (byte*)sd.Scan0;
                    for (int y = 0; y < h; y++)
                    {
                        byte* p = baseP + (long)y * sd.Stride;
                        int row = y * w;
                        for (int x = 0; x < w; x++, p += 3)
                            grey[row + x] = (byte)((p[2] * 299 + p[1] * 587 + p[0] * 114) / 1000);
                    }
                }
            }
            finally { src.UnlockBits(sd); }

            // Summed-area table, one row of padding so the window maths needs no
            // special cases at the edges.
            long[] sum = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++)
            {
                long rowSum = 0;
                int gRow = y * w, sRow = (y + 1) * (w + 1);
                int sPrev = y * (w + 1);
                for (int x = 0; x < w; x++)
                {
                    rowSum += grey[gRow + x];
                    sum[sRow + x + 1] = sum[sPrev + x + 1] + rowSum;
                }
            }

            // A window around one fifteenth of the page: wide enough to average
            // paper rather than a letter stroke, narrow enough to follow a
            // shadow across the page.
            int radius = Math.Max(8, Math.Min(w, h) / 15);
            const int Bias = 8;     // ink must be this much darker than its surroundings

            byte[] result = new byte[w * h];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                    long area = (long)(x1 - x0 + 1) * (y1 - y0 + 1);

                    long total = sum[(y1 + 1) * (w + 1) + (x1 + 1)]
                               - sum[y0 * (w + 1) + (x1 + 1)]
                               - sum[(y1 + 1) * (w + 1) + x0]
                               + sum[y0 * (w + 1) + x0];

                    int mean = (int)(total / area);
                    int t = mean - Bias;
                    result[y * w + x] = (byte)(t < 0 ? 0 : (t > 255 ? 255 : t));
                }
            }
            return result;
        }
    }
}
