// =============================================================================
// NextScan Studio - Confidence-scored deskew (plan section 3.5)
//
// Replaces the legacy "clamp to [0.6 deg, 6.0 deg]" rule, which left an 8 deg
// page completely uncorrected while a genuine 0.3 deg skew stayed visible at
// 600 dpi. The angle alone is not the signal - AGREEMENT between independent
// estimators is what separates "the page is skewed" from "there is a diagonal
// line in the artwork".
//
// Estimators (each may return NaN when it has no opinion):
//   0  text-baseline projection-profile variance maximisation (ink rows)
//   1  Hough peak over the paper's own boundary edges
//   2  Sobel gradient-orientation histogram (folded mod 90 deg)
//   3  the exact minimum-area rectangle's raw angle from DocumentDetector
//
// Sign convention: every estimator returns the SKEW of the page in image
// coordinates (x right, y down): positive = text lines run downhill to the
// right. The rotation needed to level the page is the NEGATED skew, which is
// what DeskewResult.AppliedAngle carries.
//
// The legacy behaviour is preserved verbatim as the StrictFlatbedGuard preset.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NextScan.Core
{
    /// <summary>How aggressive the deskew policy should be.</summary>
    public enum DeskewProfileKind
    {
        /// <summary>Plan section 3.5 confidence-scored policy. The default.</summary>
        Standard,
        /// <summary>The legacy scanhelper rule: accept [0.6, 6.0] deg, force 0 outside.</summary>
        StrictFlatbedGuard
    }

    public class DeskewResult
    {
        public float Skew;             // consensus detected skew, degrees
        public float AppliedAngle;     // rotation to apply to level the page (= -Skew)
        public bool AutoRotate;        // is applying AppliedAngle sanctioned?
        public bool NeedsReview;       // surface a UI hint instead of rotating
        public float Confidence;       // 0..1 estimator agreement
        public float[] EstimatorAngles;
        public string Notes = "";
    }

    public static class DeskewPolicy
    {
        // ---- plan section 3.5 defaults ----
        public const float DeadZoneDeg = 0.6f;
        public const float SoftLimitFlatbedDeg = 6.0f;
        public const float SoftLimitAdfDeg = 10.0f;
        public const float SoftLimitFilmDeg = 3.0f;
        public const float HardLimitDeg = 20.0f;
        public const float MinConfidence = 0.72f;
        public const float HighConfidence = 0.90f;

        /// <summary>Angles within this distance of the consensus count as agreeing.</summary>
        const float AgreeTolDeg = 1.0f;

        public static DeskewResult Evaluate(float[] estimatorAngles, PaperSource source, DeskewProfileKind profile)
        {
            return Evaluate(estimatorAngles, null, source, profile);
        }

        /// <summary>
        /// resolutions[i] is estimator i's angular resolution in degrees: a vote
        /// agrees when it sits within max(1 deg, resolution) of the consensus.
        /// Measured on synthetic pages: projection and Hough resolve ~0.5 deg;
        /// the minimum-area box carries an 8px block-staircase floor (~3 deg);
        /// gradient-direction voting cannot resolve shallow slopes on a pixel
        /// grid at all and its floor is ~5 deg. Without per-estimator tolerances
        /// those two structural floors would veto every small-angle page - the
        /// exact failure mode section 3.5 exists to fix.
        /// </summary>
        public static DeskewResult Evaluate(float[] estimatorAngles, float[] resolutions,
                                            PaperSource source, DeskewProfileKind profile)
        {
            DeskewResult r = new DeskewResult();
            r.EstimatorAngles = estimatorAngles ?? new float[0];

            // ---- consensus: median, then mean of everything near it ----
            List<float> avail = new List<float>();
            foreach (float a in r.EstimatorAngles)
                if (!float.IsNaN(a)) avail.Add(a);

            if (avail.Count == 0)
            {
                r.Skew = 0; r.AppliedAngle = 0; r.Confidence = 0;
                r.AutoRotate = false; r.NeedsReview = true;
                r.Notes = "no estimator produced an angle";
                return r;
            }

            avail.Sort();
            float median = avail[avail.Count / 2];
            double sum = 0; int near = 0;
            foreach (float a in avail)
                if (Math.Abs(a - median) <= 1.5f * AgreeTolDeg) { sum += a; near++; }
            r.Skew = (float)(sum / Math.Max(1, near));

            // Normalised agreement: the fraction of available estimators whose
            // vote is within their OWN resolution of the consensus. One lone
            // estimator has no corroboration at all, so it gets 0.5 - below
            // MinConfidence, above "nothing worked".
            if (avail.Count == 1)
            {
                r.Confidence = 0.5f;
            }
            else
            {
                int agreeing = 0;
                for (int i = 0; i < r.EstimatorAngles.Length; i++)
                {
                    float a = r.EstimatorAngles[i];
                    if (float.IsNaN(a)) continue;
                    float tol = (resolutions != null && i < resolutions.Length) ? resolutions[i] : AgreeTolDeg;
                    if (Math.Abs(a - r.Skew) <= Math.Max(AgreeTolDeg, tol)) agreeing++;
                }
                r.Confidence = (float)agreeing / avail.Count;
            }

            if (profile == DeskewProfileKind.StrictFlatbedGuard)
            {
                // Legacy behaviour, verbatim: accept [0.6, 6.0], force zero outside.
                if (Math.Abs(r.Skew) < DeadZoneDeg || Math.Abs(r.Skew) > SoftLimitFlatbedDeg)
                {
                    r.Skew = 0;
                    r.Notes = "strict flatbed guard: outside [0.6, 6.0] deg, forced to 0";
                }
                r.AppliedAngle = -r.Skew;
                r.AutoRotate = true;
                return r;
            }

            // ---- source-aware soft limit (plan 3.5) ----
            float softLimit = SoftLimitFlatbedDeg;
            if (source == PaperSource.Feeder || source == PaperSource.FeederDuplex) softLimit = SoftLimitAdfDeg;
            else if (source == PaperSource.Film) softLimit = SoftLimitFilmDeg;

            float abs = Math.Abs(r.Skew);
            if (abs < DeadZoneDeg)
            {
                // No visible benefit, avoids resample loss.
                r.Skew = 0;
                r.AppliedAngle = 0;
                r.AutoRotate = true;
                r.Notes = "inside dead zone, no rotation applied";
            }
            else if (abs > HardLimitDeg)
            {
                // Never auto-rotate this far; raise the UI hint instead.
                r.AppliedAngle = 0;
                r.AutoRotate = false;
                r.NeedsReview = true;
                r.Notes = "beyond hard limit " + HardLimitDeg + " deg - manual review";
            }
            else if (abs <= softLimit)
            {
                r.AutoRotate = r.Confidence >= MinConfidence;
                r.NeedsReview = !r.AutoRotate;
                r.AppliedAngle = r.AutoRotate ? -r.Skew : 0;
                r.Notes = "within soft limit " + softLimit + " deg, confidence " + r.Confidence.ToString("0.00");
            }
            else
            {
                r.AutoRotate = r.Confidence >= HighConfidence;
                r.NeedsReview = !r.AutoRotate;
                r.AppliedAngle = r.AutoRotate ? -r.Skew : 0;
                r.Notes = "beyond soft limit " + softLimit + " deg, needs confidence " +
                          HighConfidence.ToString("0.00") + ", have " + r.Confidence.ToString("0.00");
            }
            return r;
        }
    }

    /// <summary>
    /// The four independent skew estimators. All operate on an 8-bit reduction of
    /// the document interior (same reasoning as DocumentDetector: thresholds are
    /// contrast-derived, not depth-derived).
    /// </summary>
    public static class DeskewEstimators
    {
        /// <summary>
        /// Angular resolution of each estimator, in the order EstimateAll returns
        /// them: {projection, hough, sobel, minAreaBox}. Measured on synthetic
        /// pages; see DeskewPolicy.Evaluate for why these matter.
        /// </summary>
        public static readonly float[] Resolutions = new float[] { 0.5f, 0.5f, 5.0f, 3.0f };

        /// <summary>
        /// Returns {projection, hough, sobel, minAreaBox} skew angles in degrees;
        /// NaN where an estimator declines to answer.
        /// </summary>
        public static float[] EstimateAll(RawImage img, RotatedBox box)
        {
            float[] result = new float[4];
            for (int i = 0; i < 4; i++) result[i] = float.NaN;

            if (img == null || !img.IsValid || box == null || !box.IsValid)
                return result;
            result[3] = box.RawAngle;

            // ---- crop to the document (AABB, small inset so paper edges stay) ----
            int x0 = Math.Max(1, box.AABB.X);
            int y0 = Math.Max(1, box.AABB.Y);
            int x1 = Math.Min(img.Width - 1, box.AABB.Right);
            int y1 = Math.Min(img.Height - 1, box.AABB.Bottom);
            int w = x1 - x0;
            int h = y1 - y0;
            if (w < 32 || h < 32) return result;

            // Subsample stride keeps worst-case cost bounded on 600 dpi pages.
            int stride = Math.Max(1, (int)Math.Sqrt((double)w * h / 400000));

            // ---- luma + paper level (median of a sparse sample) ----
            int[] lumaHist = new int[256];
            int lumaCount = 0;
            BuildLuma(img, x0, y0, w, h, stride, lumaHist, ref lumaCount);
            if (lumaCount < 64) return result;
            int paper = HistMedian(lumaHist, lumaCount);

            int inkCut = paper - 40;
            if (inkCut < 10) return result;   // not "dark ink on light paper"; bail honestly

            // ---- page mask (paper-like pixels) for the boundary estimator ----
            // Coverage > 98% means the crop never sees the lid - a full-page scan
            // has no visible paper edges, so the Hough estimator must abstain
            // rather than report the axis-aligned image border as skew 0.
            int pagePixels = 0, totalPixels = 0;
            for (int y = 0; y < h; y += stride)
            {
                int rowBase = (y0 + y) * img.Stride;
                for (int x = 0; x < w; x += stride)
                {
                    totalPixels++;
                    if (Math.Abs(LumaAt(img, rowBase, x0 + x) - paper) <= 25) pagePixels++;
                }
            }
            bool haveLid = totalPixels > 0 && (double)pagePixels / totalPixels < 0.98;

            // ---- estimator 0: projection-profile variance over the ink mask ----
            // Ink = dark pixels ON the page. Dark alone is not enough: the lid is
            // darker than the paper too, and projecting the whole lid/document
            // silhouette answers "which way is the paper" - exactly what the
            // boundary estimator is for - instead of "which way does the text run".
            result[0] = ProjectionVariance(img, x0, y0, w, h, stride, paper, inkCut);

            // ---- estimator 1: Hough over paper boundary points ----
            if (haveLid)
                result[1] = HoughPaperEdges(img, x0, y0, w, h, stride, paper);
            // else: stays NaN - no lid visible, no paper boundary to measure

            // ---- estimator 2: Sobel orientation histogram ----
            result[2] = SobelOrientation(img, x0, y0, w, h, stride, paper);

            return result;
        }

        // ------------------------------------------------------------------ shared
        static void BuildLuma(RawImage img, int x0, int y0, int w, int h, int stride,
                              int[] hist, ref int count)
        {
            for (int y = 0; y < h; y += stride)
            {
                int rowBase = (y0 + y) * img.Stride;
                for (int x = 0; x < w; x += stride)
                {
                    hist[LumaAt(img, rowBase, x0 + x)]++;
                    count++;
                }
            }
        }

        static int LumaAt(RawImage img, int rowBase, int px)
        {
            if (img.BitsPerChannel == 1)
                return (img.Pixels[rowBase + (px >> 3)] & (0x80 >> (px & 7))) != 0 ? 0 : 255;
            int s = rowBase + px * img.Channels * (img.BitsPerChannel == 16 ? 2 : 1);
            if (img.Channels == 3)
            {
                int hi = img.BitsPerChannel == 16 ? 1 : 0;
                int b = img.Pixels[s + hi];
                int g = img.Pixels[s + 2 + hi];
                int r = img.Pixels[s + 4 + hi];
                return (r * 299 + g * 587 + b * 114) / 1000;
            }
            return img.BitsPerChannel == 16 ? img.Pixels[s + 1] : img.Pixels[s];
        }

        static int HistMedian(int[] hist, int total)
        {
            int half = total / 2, run = 0;
            for (int v = 0; v < 256; v++)
            {
                run += hist[v];
                if (run >= half) return v;
            }
            return 255;
        }

        // ------------------------------------------------------------------ 0: projection
        // Text baselines are parallel lines: projecting ink onto the axis
        // PERPENDICULAR to them piles rows into sharp peaks. The trial angle whose
        // profile has the highest sum-of-squares is the text line angle.
        //
        // Sweep is +-30 deg - WIDER than the 20 deg hard limit, because reporting
        // "beyond the hard limit, needs review" requires actually measuring angles
        // out there; clamping the sweep to the limit would make 25 deg look like 20.
        static float ProjectionVariance(RawImage img, int x0, int y0, int w, int h, int stride,
                                        int paper, int inkCut)
        {
            // Collect ink points first (bounded), then sweep angles over the points.
            // "On the page" test: a page-like pixel within +-2 samples in the
            // strided grid (a cheap 5x5 dilation of the page mask).
            List<int> ptsX = new List<int>();
            List<int> ptsY = new List<int>();
            for (int y = 2 * stride; y < h - 2 * stride; y += stride)
            {
                int rowBase = (y0 + y) * img.Stride;
                for (int x = 2 * stride; x < w - 2 * stride; x += stride)
                {
                    if (LumaAt(img, rowBase, x0 + x) >= inkCut) continue;
                    bool nearPage = false;
                    for (int dy = -2; dy <= 2 && !nearPage; dy += 2)
                    {
                        int nbRow = (y0 + y + dy * stride) * img.Stride;
                        for (int dx = -2; dx <= 2; dx += 2)
                        {
                            if (Math.Abs(LumaAt(img, nbRow, x0 + x + dx * stride) - paper) <= 25)
                            { nearPage = true; break; }
                        }
                    }
                    if (nearPage) { ptsX.Add(x); ptsY.Add(y); }
                }
            }
            if (ptsX.Count < 200) return float.NaN;

            int n = ptsX.Count;
            int[] xs = ptsX.ToArray();
            int[] ys = ptsY.ToArray();

            double bestScore = -1; float bestAngle = 0;
            for (int ai = -300; ai <= 300; ai++)
            {
                float a = ai * 0.1f;
                double rad = a * Math.PI / 180.0;
                double sin = Math.Sin(rad), cos = Math.Cos(rad);
                // v in [-w..w]; histogram with bin = 1 px
                int span = w + h + 2;
                int[] hist = new int[span];
                for (int i = 0; i < n; i++)
                {
                    int v = (int)(-xs[i] * sin + ys[i] * cos) + w;
                    if (v >= 0 && v < span) hist[v]++;
                }
                double score = 0;
                for (int i = 0; i < span; i++) score += (double)hist[i] * hist[i];
                if (score > bestScore) { bestScore = score; bestAngle = a; }
            }
            return bestAngle;
        }

        // ------------------------------------------------------------------ 1: hough
        // Hough over the paper's own boundary (page-like pixels bordering
        // non-page pixels). Only horizontal-ish edges can peak inside the +-20 deg
        // sweep, so the peak IS the paper's skew - the detector's box can't be
        // fooled by a diagonal graphic inside the artwork the way ink-based
        // estimators can.
        static float HoughPaperEdges(RawImage img, int x0, int y0, int w, int h, int stride, int paper)
        {
            List<int> ptsX = new List<int>();
            List<int> ptsY = new List<int>();
            for (int y = 1; y < h - 1; y += stride)
            {
                int rowBase = (y0 + y) * img.Stride;
                int rowUp = (y0 + y - 1) * img.Stride;
                int rowDn = (y0 + y + 1) * img.Stride;
                for (int x = 1; x < w - 1; x += stride)
                {
                    int px = x0 + x;
                    int lv = LumaAt(img, rowBase, px);
                    if (Math.Abs(lv - paper) > 25) continue;
                    // page pixel bordering non-page (4-neighbourhood)?
                    bool border = Math.Abs(LumaAt(img, rowBase, px - 1) - paper) > 45 ||
                                  Math.Abs(LumaAt(img, rowBase, px + 1) - paper) > 45 ||
                                  Math.Abs(LumaAt(img, rowUp, px) - paper) > 45 ||
                                  Math.Abs(LumaAt(img, rowDn, px) - paper) > 45;
                    if (border) { ptsX.Add(x); ptsY.Add(y); }
                }
            }
            if (ptsX.Count < 100) return float.NaN;

            int n = ptsX.Count;
            int[] xs = ptsX.ToArray();
            int[] ys = ptsY.ToArray();

            // theta in +-30 deg (wider than the hard limit, so beyond-limit skews
            // are measurable), step 0.25; rho = y cos - x sin, 1 px bins.
            const int ThetaSteps = 241;
            int rhoSpan = w + h + 2;
            int[][] acc = new int[ThetaSteps][];
            for (int t = 0; t < ThetaSteps; t++) acc[t] = new int[rhoSpan];

            for (int t = 0; t < ThetaSteps; t++)
            {
                double rad = (-30.0 + t * 0.25) * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);
                int[] accRow = acc[t];
                for (int i = 0; i < n; i++)
                {
                    int rho = (int)(ys[i] * cos - xs[i] * sin) + w;
                    if (rho >= 0 && rho < rhoSpan) accRow[rho]++;
                }
            }

            int bestCount = 0; float bestAngle = float.NaN;
            for (int t = 0; t < ThetaSteps; t++)
            {
                int[] accRow = acc[t];
                for (int rI = 0; rI < rhoSpan; rI++)
                    if (accRow[rI] > bestCount)
                    {
                        bestCount = accRow[rI];
                        bestAngle = (float)(-30.0 + t * 0.25);
                    }
            }
            return bestAngle;
        }

        // ------------------------------------------------------------------ 2: sobel
        // Gradient-orientation histogram over strong edges, folded mod 90 deg:
        // text strokes at s and s+90 both produce gradients folded onto s, so the
        // peak locates the page axes without caring which stroke dominates.
        //
        // The kernel aperture is +-4 SAMPLES, not +-1 pixel. A 3 deg edge on a
        // pixel grid is a staircase of long horizontal runs and single vertical
        // steps; a 1px kernel therefore reads the nominal 3 deg boundary as
        // mostly-horizontal and votes for 0 deg. Averaging over an 8-sample span
        // covers several staircase periods and recovers the true direction - the
        // measured failure was sobel reporting 0.5 deg on a 3 deg synthetic page.
        static float SobelOrientation(RawImage img, int x0, int y0, int w, int h, int stride, int paper)
        {
            // 90 bins of 1 deg in the folded domain, weighted by gradient magnitude.
            double[] hist = new double[90];
            int strong = 0;
            int aperture = 4 * Math.Max(1, stride);

            for (int y = aperture; y < h - aperture; y += stride)
            {
                int rowBase = (y0 + y) * img.Stride;
                int rowUp = (y0 + y - aperture) * img.Stride;
                int rowDn = (y0 + y + aperture) * img.Stride;
                for (int x = aperture; x < w - aperture; x += stride)
                {
                    int px = x0 + x;
                    int gx = LumaAt(img, rowBase, px + aperture) - LumaAt(img, rowBase, px - aperture);
                    int gy = LumaAt(img, rowDn, px) - LumaAt(img, rowUp, px);
                    int mag = Math.Abs(gx) + Math.Abs(gy);
                    // Ink-to-paper contrast gate: noise on flat paper is rejected.
                    if (mag < 40) continue;
                    strong++;

                    double ang = Math.Atan2(gy, gx) * 180.0 / Math.PI;   // (-180, 180]
                    double folded = ang - 90.0 * Math.Floor(ang / 90.0); // [0, 90)
                    int bin = (int)folded;
                    if (bin > 89) bin = 89;
                    hist[bin] += mag;
                }
            }
            if (strong < 200) return float.NaN;

            // Smooth circularly by 3 bins, then take the peak; a single hot bin is
            // usually an artifact edge, text smears across several.
            double best = -1; int bestBin = 0;
            for (int i = 0; i < 90; i++)
            {
                double s = hist[(i + 89) % 90] + hist[i] + hist[(i + 1) % 90];
                if (s > best) { best = s; bestBin = i; }
            }
            float angle = bestBin + 0.5f;
            return angle <= 45f ? angle : angle - 90f;
        }
    }
}
