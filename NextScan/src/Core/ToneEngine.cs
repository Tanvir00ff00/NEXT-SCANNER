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
    }

    public class TonePresetInfo
    {
        public TonePreset Preset;
        public string Name = "";
        public string Description = "";
        /// <summary>Paper polish this preset wants, 0..100.</summary>
        public int Polish;
        public int Contrast;
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

        public ToneSettings Clone()
        {
            return new ToneSettings
            {
                Preset = Preset,
                Polish = Polish,
                Brightness = Brightness,
                Contrast = Contrast,
                BwThreshold = BwThreshold,
                AdaptiveThreshold = AdaptiveThreshold
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
                Brightness = 0
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

            byte[] lut = BuildLut(s);
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

                            b = lut[b]; g = lut[g]; r = lut[r];

                            if (polish > 0.0) Polish(ref r, ref g, ref b, polish);

                            if (toBilevel)
                            {
                                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                                int limit = localThreshold != null
                                    ? localThreshold[y * w + x]
                                    : s.BwThreshold;
                                byte bw = lum < limit ? (byte)0 : (byte)255;
                                r = bw; g = bw; b = bw;
                            }
                            else if (toGrey)
                            {
                                byte grey = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                                r = grey; g = grey; b = grey;
                            }

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

            return dst;
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
