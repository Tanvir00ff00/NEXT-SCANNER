// =============================================================================
// NextScan Studio - finding the items lying on the glass
// Plan ref: MASTER_PLAN section 8.1 and universal platen work order sections 2-5.
// Oriented hulls avoid rotation-dependent rejection; independent contrast and
// gradient hypotheses avoid one smudge bridge controlling the entire result.
// Every rejected candidate is recorded in a per-call report for the preview.
// Limitations: a seamless contact, invisible border, fully occluded corner or
// full-bleed blank sheet has no unique physical solution in the raster. Curled
// stock is still approximated by straight edges; geometric angles do not reveal
// text orientation. Unresolved hypotheses are reported, not certified as crops.
//
// Why this exists alongside AutoCropEngine.
//
// AutoCropEngine is good at the part it was specified for: given a document it
// has accepted, it fits four edges and returns exact corners and skew. Its own
// suite proves that, and so do our independent cases. What it does badly is
// decide *which* blobs on a real platen are documents. Measured on the preview
// this scanner actually produces, with two ID cards side by side:
//
//   AutoCropEngine, as delivered ......... nothing at all
//   after removing the illumination ...... one card, never both
//   a plain threshold and component pass . both cards, at every threshold tried
//     drop 6  -> 3.63 x 2.07 at 0.058 and 3.45 x 2.07 at 0.504
//     drop 14 -> 3.48 x 2.07 at 0.073 and 3.42 x 2.07 at 0.504
//     drop 24 -> 3.46 x 2.07 at 0.073 and 3.41 x 2.07 at 0.504
//   (the cards are 3.37 x 2.13 in, at x = 0.07 and x = 0.49)
//
// The evidence was always there; the policy layer above it was discarding one
// card. This file extends that component finding stage. PlatenEdgeFitter measures each
// isolated candidate without invoking the engine policy again. On the reference
// preview the old refinement accepted no result, leaving threshold shadows as
// final edges. Its 20% size gate also could not establish physical accuracy.
//
// The one step that is not optional is flattening the illumination. This platen
// carries a soft smudge and faint vertical streaking; the engine reads that
// spread as sensor noise, which lifts its colour threshold to 35.6 while a pale
// card sits only 25 to 30 levels from the lid. No threshold choice survives that.
// A coarse grid of high percentiles, interpolated between block centres, models
// the shading well enough that the difference disappears.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.Threading.Tasks;

namespace NextScan.Core
{
    // One report belongs to one call. A batch must not share a mutable global
    // log buffer or attribute one page's rejected candidates to another page.
    public sealed class PlatenDetectionReport
    {
        public readonly List<string> Lines = new List<string>();
        public int UnresolvedCandidates;

        internal void Add(string message)
        {
            Lines.Add(message);
        }
    }

    public static class PlatenDetector
    {
        public static Action<string> Log = delegate { };

        /// <summary>Long edge of the image detection actually works on.</summary>
        const int WorkingLongEdge = 1200;

        /// <summary>Blocks across the width when modelling the illumination.</summary>
        const int BackgroundBlocks = 6;

        /// <summary>
        /// Percentile of each block taken as "lid there". High enough that a
        /// block mostly covered by a document still reports the lid around it.
        /// </summary>
        const double BackgroundPercentile = 0.85;

        /// <summary>
        /// How far below a flattened lid a pixel must fall to be an item.
        /// Measured: anything from 6 to 24 finds both cards on the real page, so
        /// 14 sits in the middle of a wide plateau rather than on an edge.
        /// </summary>
        const int DropBelowLid = 14;

        // =====================================================================
        // Entry point
        // =====================================================================
        /// <summary>
        /// Every item found on the glass, in reading order. Never throws; returns
        /// an empty list when it is not confident, so a scan is never cropped on
        /// a guess.
        /// </summary>
        public static List<CropRegion> Detect(RawImage page, AutoCropOptions options)
        {
            return Detect(page, options, new PlatenDetectionReport());
        }

        public static List<CropRegion> Detect(RawImage page, AutoCropOptions options, PlatenDetectionReport report)

        {
            if (report == null) report = new PlatenDetectionReport();
            List<CropRegion> found = new List<CropRegion>();
            report.Add("begin platen detection; angles are geometric, modulo 90 degrees");
            if (page == null || !page.IsValid) return found;
            if (page.XDpi <= 0 || page.YDpi <= 0 || double.IsNaN(page.XDpi) ||
                double.IsNaN(page.YDpi) || double.IsInfinity(page.XDpi) || double.IsInfinity(page.YDpi)) return found;
            if (options == null) options = new AutoCropOptions();
            if (!options.Enabled) return found;
            if (options.MaxRegions < 1 || double.IsNaN(options.MinAreaMm2) ||
                double.IsInfinity(options.MinAreaMm2) || options.MinAreaMm2 < 0) return found;

            try
            {
                Stopwatch stageTime = Stopwatch.StartNew();
                int scale = Math.Max(1, (int)Math.Ceiling(
                    Math.Max(page.Width, page.Height) / (double)WorkingLongEdge));

                int w, h;
                byte[][] channels;
                byte[] grey = Reduce(page, scale, out w, out h, out channels);
                if (w < 32 || h < 32) return found;

                // Chain extraction reads immutable reduced pixels and can overlap
                // illumination/mask work. Joining it before proposal evaluation
                // keeps diagnostics and selection deterministic.
                Task<List<PlatenBoundaryProposals.Segment>> boundaryChains = Task.Run(delegate
                {
                    return PlatenBoundaryProposals.Segments(channels, w, h, Math.Min(page.XDpi, page.YDpi) / scale / 25.4);
                });
                int[] flat = Flatten(grey, w, h);
                bool[] corroborated = CorroboratingEvidence(channels, grey, flat, w, h);
                bool[] edges = new bool[w * h], broadEdges = new bool[w * h];
                // Sensitivity follows physical working resolution, not the source
                // reduction factor. Replicating a 100 dpi page at 300 dpi must
                // produce the same reduced evidence and identical decisions.
                double workingResolution = Math.Min(page.XDpi, page.YDpi) / scale / 100;
                double sensitivity = Math.Min(1, workingResolution * workingResolution);
                int minimumDifference = Math.Max(5, (int)Math.Round(20 * sensitivity));
                double minimumGradient = Math.Max(1, 4 * sensitivity);
                // The two gradient baselines write separate buffers. Running
                // them together reduces preparation latency without racing the
                // OR updates from different colour channels into one mask.
                Parallel.Invoke(
                    delegate
                    {
                        foreach (byte[] channel in channels) AddEdgeEvidence(channel, edges, w, h, minimumDifference, 1);
                    },
                    delegate
                    {
                        foreach (byte[] channel in channels) AddEdgeEvidence(channel, broadEdges, w, h, minimumDifference, 5);
                    });
                double pixelsPerMmX = page.XDpi / 25.4 / scale;
                double pixelsPerMmY = page.YDpi / 25.4 / scale;
                int minimumPixels = Math.Max(64, (int)(options.MinAreaMm2 * pixelsPerMmX * pixelsPerMmY * 0.35));
                int radius = Math.Max(2, Math.Min(w, h) / 260);
                // Independent hypotheses prevent one low threshold's smudge
                // bridge from erasing a component found at another contrast.
                // 6/14/28 cover the measured 6..24 plateau and darker covers.
                // Only a supported physical fit can turn a hypothesis into a crop.
                report.Add("preparation ms: " + stageTime.ElapsedMilliseconds);
                int[] drops = { DropBelowLid, 6, 28, 0 };
                EvidencePass[] prepared = new EvidencePass[drops.Length];
                // Mask hypotheses are independent. Two workers bound memory and
                // CPU use while avoiding four serial full-raster morphology runs.
                // Fits and report merging retain the original hypothesis order.
                Parallel.For(0, drops.Length, new ParallelOptions { MaxDegreeOfParallelism = 2 }, delegate(int pass)
                {
                    prepared[pass] = PrepareEvidence(drops[pass], flat, edges, broadEdges, corroborated,
                        w, h, radius, minimumPixels, pixelsPerMmX, scale);
                });
                foreach (EvidencePass evidence in prepared)
                {
                    stageTime.Restart();
                    report.Lines.AddRange(evidence.Report.Lines);
                    report.UnresolvedCandidates += evidence.Report.UnresolvedCandidates;
                    bool[] contourEvidence = evidence.ContourEvidence;
                    bool[] filledContourEvidence = null;
                    int frameInset = evidence.FrameInset;
                    List<Blob> blobs = evidence.Blobs;
                    foreach (Blob blob in blobs)
                    {
                        CropRegion candidate = Accept(page, blob, w, h, scale, options, report, grey, false, frameInset);
                        if (candidate == null) continue;
                        if (candidate.Reason.StartsWith("frame-limited", StringComparison.Ordinal) && found.Count > 0)
                        {
                            report.Add("rejected frame-limited enclosing hypothesis: independently fitted items already present");
                            continue;
                        }
                        // Repeated hypotheses already covered by an approved
                        // boundary do not need another contour traversal.
                        if (Covered(candidate, found))
                        {
                            report.Add("rejected duplicate hypothesis " + candidate.NormRect + ": already covered by an accepted boundary");
                            continue;
                        }
                        bool contourMeasured = false;
                        if (candidate.Score < 0.96f && blob.Left > 1 && blob.Top > 1 && blob.Right < w - 2 && blob.Bottom < h - 2)
                        {
                            if (filledContourEvidence == null) filledContourEvidence = FillHoles(contourEvidence, w, h);
                            contourMeasured = PlatenContour.Fit(filledContourEvidence, channels, w, h, scale, page, blob.Boundary, candidate, minimumGradient);
                        }
                        if (!contourMeasured && candidate.Score < 0.55f)
                        {
                            report.Add("rejected " + candidate.NormRect + ": irregular silhouette has no supported contour");
                            report.UnresolvedCandidates++;
                            continue;
                        }
                        // A previously fitted outer boundary already accounts for
                        // nested print and repeat silhouettes at other thresholds.
                        if (Covered(candidate, found))
                        {
                            report.Add("rejected duplicate hypothesis " + candidate.NormRect + ": already covered by an accepted boundary");
                            continue;
                        }
                        if (!contourMeasured && !candidate.Reason.StartsWith("frame-limited", StringComparison.Ordinal) &&
                            !PlatenEdgeFitter.Fit(channels, w, h, scale, page, candidate, minimumGradient))
                        {
                            report.Add("rejected " + candidate.NormRect + ": physical edge fit failed; " + candidate.Reason
                                + "; unresolved outline: blur, occlusion or merged items require manual review");
                            report.UnresolvedCandidates++;
                        }
                        else if (FittedFrameContacts(candidate, page, Math.Max(frameInset, radius) * scale) >= 3)
                        {
                            // The fit can move an inset component onto the frame.
                            // Re-check after fitting, before it suppresses real items.
                            report.Add("rejected fitted boundary " + candidate.NormRect + ": three frame contacts appeared during edge fitting; stock edges remain unobserved");
                            report.UnresolvedCandidates++;
                        }
                        else if (candidate.Confidence < options.MinConfidence)
                        {
                            report.Add("rejected " + candidate.NormRect + ": fitted confidence " + candidate.Confidence
                                + " below requested " + options.MinConfidence);
                            report.UnresolvedCandidates++;
                        }
                        else if (Covered(candidate, found))
                            report.Add("rejected duplicate fitted boundary " + candidate.NormRect);
                        else
                        {
                            if (!ReplaceContained(candidate, found, report)) continue;
                            found.Add(candidate);
                            report.Add("accepted " + candidate.NormRect + ": " + candidate.Reason);
                        }
                    }
                    report.Add("evidence refinement ms: " + stageTime.ElapsedMilliseconds);
                }
                FindShadowSeparatedStock(page, grey, channels, w, h, scale, options, report, found, minimumGradient);
                if (found.Count == 0)
                {
                    CropRegion frameSheet = FindFrameSheet(page, grey, w, h, scale, options, report);
                    if (frameSheet != null)
                    {
                        if (frameSheet.Confidence >= options.MinConfidence)
                        {
                            found.Add(frameSheet);
                            report.Add("accepted " + frameSheet.NormRect + ": " + frameSheet.Reason);
                        }
                        else report.Add("rejected large sheet: confidence below requested " + options.MinConfidence);
                    }
                }
                stageTime.Restart();
                List<CropRegion> supported = new List<CropRegion>();
                foreach (CropRegion existing in found)
                    if (existing.Outline != null || PlatenBoundaryProposals.BoundarySupport(channels, w, h, scale, existing) >= 0.5)
                        supported.Add(existing);
                List<CropRegion> alternatives = PlatenBoundaryProposals.Find(channels, w, h, scale, page, options, report,
                    delegate(CropRegion proposal) { return Covered(proposal, supported); }, boundaryChains.Result);
                List<CropRegion> outerStock = new List<CropRegion>();
                if (report.UnresolvedCandidates > 0)
                    FindShadowSeparatedStock(page, grey, channels, w, h, scale, options, report, outerStock, minimumGradient);
                alternatives.InsertRange(0, outerStock);
                ResolveBoundaryAlternatives(alternatives, found, channels, w, h, scale, page, report);
                report.Add("boundary alternatives ms: " + stageTime.ElapsedMilliseconds);
                DropSoftRegionsLyingAcrossItems(found, report);
                PlatenFragments.Join(page, found, report);

                // The model answers "how many items, and roughly where"; the
                // shadow pass below answers "and exactly where does this one
                // stop". Neither can do the other's job, and the order matters:
                // repairing the set of regions first means the shadow pass
                // measures the right things.
                if (options.UseModel)
                {
                    SamProposals.Result model = SamProposals.Propose(page, found, options.Thorough);
                    if (model.Boxes.Count > 0)
                    {
                        SamRepair.Apply(page, found, model, options, report);

                        // Again, because the model hands back an open passport as
                        // its two pages and the second of them only exists after
                        // the repair above has added it. Two pages that meet at
                        // the spine with no glass between them are one item.
                        PlatenFragments.Join(page, found, report);
                    }
                    else report.Add("model: " + model.Note);
                }

                PlatenShadowEdges.Refine(page, found, report);
                Order(found);

                if (found.Count > options.MaxRegions)
                {
                    for (int index = options.MaxRegions; index < found.Count; index++)
                        report.Add("rejected " + found[index].NormRect + ": MaxRegions limit " + options.MaxRegions);
                    report.UnresolvedCandidates += found.Count - options.MaxRegions;
                    found.RemoveRange(options.MaxRegions, found.Count - options.MaxRegions);
                }
                if (found.Count == 0) report.Add("no accepted boundary; invisible or full-bed stock cannot be distinguished from an empty lid without evidence");
                SafeLog("platen detector found " + found.Count + " item(s)");
            }
            catch (Exception ex)
            {
                report.Add("platen detection failed: " + ex.Message);
                SafeLog("platen detection failed: " + ex.Message);
                found.Clear();
            }
            return found;
        }

        sealed class EvidencePass
        {
            internal bool[] ContourEvidence;
            internal List<Blob> Blobs;
            internal int FrameInset;
            internal PlatenDetectionReport Report;
        }

        static EvidencePass PrepareEvidence(int drop, int[] flat, bool[] edges, bool[] broadEdges,
            bool[] corroborated, int width, int height, int radius, int minimumPixels, double pixelsPerMm, int scale)
        {
            Stopwatch watch = Stopwatch.StartNew();
            PlatenDetectionReport report = new PlatenDetectionReport();
            report.Add("evidence pass: " + (drop == 0 ? "broad gradient only" : "luminance drop " + drop)
                + "; narrow gradients " + (drop != 0 && drop != 28)
                + "; colour/variance corroboration " + (drop == 6) + "; working scale " + scale);
            bool[] mask = new bool[width * height];
            for (int pixel = 0; pixel < flat.Length; pixel++)
                mask[pixel] = (drop > 0 && flat[pixel] < 240 - drop) ||
                    (drop != 28 && (drop == 0 ? broadEdges[pixel] : edges[pixel])) || (drop == 6 && corroborated[pixel]);
            bool[] contourEvidence = mask;
            mask = Close(mask, width, height, radius);
            int frameInset = RemoveEnclosingFrame(mask, width, height, radius, pixelsPerMm, report);
            SealFrameContours(mask, width, height, frameInset);
            mask = FillHoles(mask, width, height);
            List<Blob> blobs = Components(mask, width, height, minimumPixels, report);
            if (drop == 14 || drop == 28) blobs = SeparateNecks(mask, width, blobs, minimumPixels, pixelsPerMm, report);
            report.Add("mask and components ms: " + watch.ElapsedMilliseconds);
            return new EvidencePass { ContourEvidence = contourEvidence, Blobs = blobs, FrameInset = frameInset, Report = report };
        }

        // =====================================================================
        // Reduction and illumination
        // =====================================================================
        /// <summary>
        /// Greyscale at working size, box averaged. Averaging rather than
        /// sampling matters: a single dust pixel must not survive the reduction
        /// with its full weight, while a real edge must.
        /// </summary>
        static byte[] Reduce(RawImage page, int scale, out int w, out int h, out byte[][] channels)
        {
            w = Math.Max(1, page.Width / scale);
            h = Math.Max(1, page.Height / scale);
            byte[] grey = new byte[w * h];
            channels = page.Channels == 3
                ? new byte[][] { new byte[w * h], new byte[w * h], new byte[w * h] }
                : new byte[][] { grey };

            for (int y = 0; y < h; y++)
            {
                int sy0 = y * scale, sy1 = Math.Min(page.Height, sy0 + scale);
                for (int x = 0; x < w; x++)
                {
                    int sx0 = x * scale, sx1 = Math.Min(page.Width, sx0 + scale);
                    int total = 0, count = 0, blue = 0, green = 0, red = 0;
                    int step = page.BitsPerChannel == 16 ? 2 : 1;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            if (page.Channels == 3)
                            {
                                int offset = sy * page.Stride + sx * 3 * step + step - 1;
                                blue += page.Pixels[offset];
                                green += page.Pixels[offset + step];
                                red += page.Pixels[offset + 2 * step];
                            }
                            else total += Luminance(page, sx, sy);
                            count++;
                        }
                    }
                    int destination = y * w + x;
                    if (page.Channels == 3)
                    {
                        channels[0][destination] = (byte)(blue / count);
                        channels[1][destination] = (byte)(green / count);
                        channels[2][destination] = (byte)(red / count);
                        total = (blue * 114 + green * 587 + red * 299) / 1000;
                    }
                    grey[destination] = (byte)(total / count);
                }
            }
            return grey;
        }

        static int Luminance(RawImage p, int x, int y)
        {
            if (p.BitsPerChannel == 1)
                return (p.Pixels[y * p.Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 255 : 0;

            int step = p.BitsPerChannel == 16 ? 2 : 1;
            int o = y * p.Stride + x * p.Channels * step;
            if (p.BitsPerChannel == 16) o += 1;         // high byte of the little-endian pair

            if (p.Channels == 1) return p.Pixels[o];
            return (p.Pixels[o + step * 2] * 299 + p.Pixels[o + step] * 587 + p.Pixels[o] * 114) / 1000;
        }

        /// <summary>
        /// Divides out the illumination so every part of the glass reads as 240
        /// where it is bare. The field comes from a coarse grid of high
        /// percentiles, interpolated between block centres so the correction
        /// follows the shading instead of stepping at block edges.
        /// </summary>
        static int[] Flatten(byte[] grey, int w, int h)
        {
            int bw = Math.Max(2, BackgroundBlocks);
            int bh = Math.Max(2, (int)Math.Round(BackgroundBlocks * (double)h / w));
            double cellW = (double)w / bw, cellH = (double)h / bh;

            double[,] field = new double[bw, bh];
            int[] histogram = new int[256];

            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    Array.Clear(histogram, 0, histogram.Length);
                    int x0 = (int)(bx * cellW), x1 = (int)Math.Min(w, (bx + 1) * cellW);
                    int y0 = (int)(by * cellH), y1 = (int)Math.Min(h, (by + 1) * cellH);
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++) histogram[grey[y * w + x]]++;
                    int target = (int)(((x1 - x0) * (y1 - y0) - 1) * BackgroundPercentile);
                    int cumulative = 0, percentile = 0;
                    for (; percentile < 255; percentile++)
                    {
                        cumulative += histogram[percentile];
                        if (cumulative > target) break;
                    }
                    field[bx, by] = percentile;
                }

            int[] flat = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                double fy = y / cellH - 0.5;
                int j0 = (int)Math.Floor(fy); double ty = fy - j0;
                int ja = Clamp(j0, bh), jb = Clamp(j0 + 1, bh);

                for (int x = 0; x < w; x++)
                {
                    double fx = x / cellW - 0.5;
                    int i0 = (int)Math.Floor(fx); double tx = fx - i0;
                    int ia = Clamp(i0, bw), ib = Clamp(i0 + 1, bw);

                    double top = field[ia, ja] * (1 - tx) + field[ib, ja] * tx;
                    double bottom = field[ia, jb] * (1 - tx) + field[ib, jb] * tx;
                    double lid = Math.Max(1.0, top * (1 - ty) + bottom * ty);

                    int v = (int)Math.Round(grey[y * w + x] * 240.0 / lid);
                    flat[y * w + x] = v < 0 ? 0 : (v > 255 ? 255 : v);
                }
            }
            return flat;
        }

        static bool[] CorroboratingEvidence(byte[][] channels, byte[] grey, int[] flat, int width, int height)
        {
            bool[] supported = new bool[flat.Length];
            int[][] colour = new int[channels.Length][];
            Parallel.For(0, channels.Length, delegate(int channel)
            {
                colour[channel] = channels.Length == 1 ? flat : Flatten(channels[channel], width, height);
            });
            for (int y = 2; y < height - 2; y++)
            {
                for (int x = 2; x < width - 2; x++)
                {
                    int pixel = y * width + x;
                    if (flat[pixel] >= 237) continue;
                    double sum = 0, squares = 0;
                    for (int row = -1; row <= 1; row++)
                        for (int column = -1; column <= 1; column++)
                        {
                            double value = grey[pixel + row * width + column];
                            sum += value; squares += value * value;
                        }
                    int minimum = 255, maximum = 0;
                    foreach (int[] plane in colour)
                    {
                        minimum = Math.Min(minimum, plane[pixel]);
                        maximum = Math.Max(maximum, plane[pixel]);
                    }
                    // Three-level luminance evidence needs either a tint residual
                    // above the +/-3 noise fixture or variance beyond that noise.
                    // Neither texture nor tint alone proves an enclosing boundary.
                    supported[pixel] = maximum - minimum >= 10 || squares / 9 - sum * sum / 81 >= 25;
                }
            }
            return supported;
        }

        static bool DistributedTexture(byte[] grey, int width, Blob blob)
        {
            int supportedCells = 0;
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    int left = blob.Left + (blob.Right - blob.Left) * column / 3 + 2;
                    int right = blob.Left + (blob.Right - blob.Left) * (column + 1) / 3 - 2;
                    int top = blob.Top + (blob.Bottom - blob.Top) * row / 3 + 2;
                    int bottom = blob.Top + (blob.Bottom - blob.Top) * (row + 1) / 3 - 2;
                    int textured = 0, samples = 0;
                    for (int y = top; y < bottom; y += 2)
                        for (int x = left; x < right; x += 2)
                        {
                            int pixel = y * width + x;
                            if (Math.Abs(grey[pixel] - grey[pixel + 1]) >= 12 ||
                                Math.Abs(grey[pixel] - grey[pixel + width]) >= 12) textured++;
                            samples++;
                        }
                    if (samples > 0 && textured > samples * 0.01) supportedCells++;
                }
            }
            // A uniform three-sided hinge slab has no interior texture. Five of
            // nine cells require spatial evidence rather than one calibration rule.
            return supportedCells >= 5;
        }

        static int Clamp(int v, int n) { return v < 0 ? 0 : (v >= n ? n - 1 : v); }

        // =====================================================================
        // Mask work
        // =====================================================================
        static void AddEdgeEvidence(byte[] grey, bool[] mask, int width, int height, int minimumDifference, int baseline)
        {
            // A five-level shadow is below the fourteen-level stock threshold.
            // Five parallel differences preserve it while averaging the +/-3
            // sensor noise used by the realistic platen fixture. Requiring a
            // closed, solid component and four supported lines remains essential:
            // a hair has gradients too, but is not a physical rectangle.
            int border = Math.Max(2, (baseline + 1) / 2);
            for (int y = border; y < height - border; y++)
            {
                for (int x = border; x < width - border; x++)
                {
                    int horizontal = 0, vertical = 0;
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        horizontal += grey[(y + offset) * width + x + baseline / 2] - grey[(y + offset) * width + x - (baseline + 1) / 2];
                        vertical += grey[(y + baseline / 2) * width + x + offset] - grey[(y - (baseline + 1) / 2) * width + x + offset];
                    }
                    if (Math.Abs(horizontal) >= minimumDifference || Math.Abs(vertical) >= minimumDifference) mask[y * width + x] = true;
                }
            }
        }

        static bool[] Close(bool[] src, int w, int h, int r)
        {
            bool[] grown = Dilate(src, w, h, r);
            return Erode(grown, w, h, r);
        }

        static int RemoveEnclosingFrame(bool[] mask, int width, int height, int radius,
                                        double pixelsPerMm, PlatenDetectionReport report)
        {
            // An enclosing calibration contour turns every background pixel
            // into a hole. Measure its inward extent from support on three sides
            // before hole filling; removing an accepted full-bed box is too late.
            // The 3 mm search covers the captured LiDE frame and the 7-pixel
            // synthetic frame, while refusing to search arbitrarily into stock.
            int limit = Math.Min(Math.Min(width, height) / 8, (int)Math.Ceiling(3 * pixelsPerMm));
            int last = -1;
            for (int inset = 0; inset <= limit; inset++)
            {
                int top = 0, bottom = 0, left = 0, right = 0;
                for (int x = limit; x < width - limit; x++)
                {
                    if (mask[inset * width + x]) top++;
                    if (mask[(height - inset - 1) * width + x]) bottom++;
                }
                for (int y = limit; y < height - limit; y++)
                {
                    if (mask[y * width + inset]) left++;
                    if (mask[y * width + width - inset - 1]) right++;
                }
                int supported = (top > (width - 2 * limit) * 0.65 ? 1 : 0)
                    + (bottom > (width - 2 * limit) * 0.65 ? 1 : 0)
                    + (left > (height - 2 * limit) * 0.65 ? 1 : 0)
                    + (right > (height - 2 * limit) * 0.65 ? 1 : 0);
                if (supported >= 3) last = inset;
            }
            if (last < 0) return radius;
            int band = last + 1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (x < band || y < band || x >= width - band || y >= height - band)
                        mask[y * width + x] = false;
            report.Add("removed enclosing frame evidence through working inset " + last + "; stock contours restored separately before hole filling");
            return Math.Max(radius, band);
        }

        static void SealFrameContours(bool[] mask, int width, int height, int inset)
        {
            // A pale overhanging card has a U-shaped outline. Flooding from the
            // frame enters that U and leaves only its print. Close each connected
            // outline along the frame it actually reaches, after erosion has
            // removed the scanner's connecting calibration line. Closing the
            // whole frame instead would turn bare glass into an enclosed item.
            foreach (Blob outline in Components(mask, width, height, 64))
            {
                int contacts = (outline.Left <= inset ? 1 : 0) + (outline.Top <= inset ? 1 : 0)
                    + (outline.Right >= width - inset - 1 ? 1 : 0) + (outline.Bottom >= height - inset - 1 ? 1 : 0);
                // Sealing three or four sides would recreate an enclosing platen
                // contour. Large sheets need the independent physical-edge path.
                if (contacts >= 3) continue;
                if (outline.Top <= inset)
                    for (int y = 0; y <= inset; y++)
                        for (int x = outline.Left; x <= outline.Right; x++) mask[y * width + x] = true;
                if (outline.Bottom >= height - inset - 1)
                    for (int y = height - inset - 1; y < height; y++)
                        for (int x = outline.Left; x <= outline.Right; x++) mask[y * width + x] = true;
                if (outline.Left <= inset)
                    for (int x = 0; x <= inset; x++)
                        for (int y = outline.Top; y <= outline.Bottom; y++) mask[y * width + x] = true;
                if (outline.Right >= width - inset - 1)
                    for (int x = width - inset - 1; x < width; x++)
                        for (int y = outline.Top; y <= outline.Bottom; y++) mask[y * width + x] = true;
            }
        }

        // Separable, so the cost is linear in the radius rather than square.
        static bool[] Dilate(bool[] source, int width, int height, int radius)
        {
            return Morphology(source, width, height, radius, false);
        }

        static bool[] Erode(bool[] source, int width, int height, int radius)
        {
            return Morphology(source, width, height, radius, true);
        }

        // A sliding population count preserves the same square neighbourhood
        // and outside-is-false erosion rule. Recounting all seven neighbours at
        // every pixel consumed much of the four evidence passes' preview budget.
        static bool[] Morphology(bool[] source, int width, int height, int radius, bool erosion)
        {
            bool[] horizontal = new bool[source.Length], result = new bool[source.Length];
            int required = erosion ? radius * 2 + 1 : 1;
            for (int row = 0; row < height; row++)
            {
                int count = 0, origin = row * width;
                for (int column = 0; column < Math.Min(radius, width); column++) if (source[origin + column]) count++;
                for (int column = 0; column < width; column++)
                {
                    if (column + radius < width && source[origin + column + radius]) count++;
                    if (column - radius - 1 >= 0 && source[origin + column - radius - 1]) count--;
                    horizontal[origin + column] = count >= required;
                }
            }
            for (int column = 0; column < width; column++)
            {
                int count = 0;
                for (int row = 0; row < Math.Min(radius, height); row++) if (horizontal[row * width + column]) count++;
                for (int row = 0; row < height; row++)
                {
                    if (row + radius < height && horizontal[(row + radius) * width + column]) count++;
                    if (row - radius - 1 >= 0 && horizontal[(row - radius - 1) * width + column]) count--;
                    result[row * width + column] = count >= required;
                }
            }
            return result;
        }

        /// <summary>
        /// Marks every background pixel that cannot be reached from the frame.
        /// Reaching in from the edge rather than searching for enclosed regions
        /// means an outline that touches the frame is left open, which is right:
        /// the glass beyond it is not inside anything.
        /// </summary>
        static bool[] FillHoles(bool[] mask, int w, int h)
        {
            bool[] outside = new bool[mask.Length];
            int[] stack = new int[mask.Length];
            int top = 0;

            for (int x = 0; x < w; x++)
            {
                int a = x, b = (h - 1) * w + x;
                if (!mask[a] && !outside[a]) { outside[a] = true; stack[top++] = a; }
                if (!mask[b] && !outside[b]) { outside[b] = true; stack[top++] = b; }
            }
            for (int y = 0; y < h; y++)
            {
                int a = y * w, b = y * w + w - 1;
                if (!mask[a] && !outside[a]) { outside[a] = true; stack[top++] = a; }
                if (!mask[b] && !outside[b]) { outside[b] = true; stack[top++] = b; }
            }

            while (top > 0)
            {
                int q = stack[--top];
                int qx = q % w, qy = q / w;
                if (qx > 0 && !mask[q - 1] && !outside[q - 1]) { outside[q - 1] = true; stack[top++] = q - 1; }
                if (qx < w - 1 && !mask[q + 1] && !outside[q + 1]) { outside[q + 1] = true; stack[top++] = q + 1; }
                if (qy > 0 && !mask[q - w] && !outside[q - w]) { outside[q - w] = true; stack[top++] = q - w; }
                if (qy < h - 1 && !mask[q + w] && !outside[q + w]) { outside[q + w] = true; stack[top++] = q + w; }
            }

            bool[] filled = new bool[mask.Length];
            for (int i = 0; i < mask.Length; i++) filled[i] = mask[i] || !outside[i];
            return filled;
        }

        class Blob
        {
            public int Left, Top, Right, Bottom, Count;
            public readonly List<PointF> Boundary = new List<PointF>();
        }

        static List<Blob> Components(bool[] mask, int w, int h, int minimumPixels, PlatenDetectionReport report = null)
        {
            List<Blob> found = new List<Blob>();
            bool[] seen = new bool[mask.Length];
            int[] stack = new int[mask.Length];

            for (int start = 0; start < mask.Length; start++)
            {
                if (!mask[start] || seen[start]) continue;

                int top = 0;
                stack[top++] = start;
                seen[start] = true;

                Blob blob = new Blob
                {
                    Left = start % w, Right = start % w,
                    Top = start / w, Bottom = start / w
                };

                while (top > 0)
                {
                    int q = stack[--top];
                    blob.Count++;
                    int qx = q % w, qy = q / w;
                    if (qx == 0 || qy == 0 || qx == w - 1 || qy == h - 1 ||
                        !mask[q - 1] || !mask[q + 1] || !mask[q - w] || !mask[q + w])
                        blob.Boundary.Add(new PointF(qx + 0.5f, qy + 0.5f));
                    if (qx < blob.Left) blob.Left = qx;
                    if (qx > blob.Right) blob.Right = qx;
                    if (qy < blob.Top) blob.Top = qy;
                    if (qy > blob.Bottom) blob.Bottom = qy;

                    if (qx > 0 && mask[q - 1] && !seen[q - 1]) { seen[q - 1] = true; stack[top++] = q - 1; }
                    if (qx < w - 1 && mask[q + 1] && !seen[q + 1]) { seen[q + 1] = true; stack[top++] = q + 1; }
                    if (qy > 0 && mask[q - w] && !seen[q - w]) { seen[q - w] = true; stack[top++] = q - w; }
                    if (qy < h - 1 && mask[q + w] && !seen[q + w]) { seen[q + w] = true; stack[top++] = q + w; }
                }

                if (blob.Count >= minimumPixels) found.Add(blob);
                else if (report != null) report.Add("rejected component " + blob.Left + "," + blob.Top + ".."
                    + blob.Right + "," + blob.Bottom + ": " + blob.Count + " pixels below component minimum " + minimumPixels);
            }
            return found;
        }

        static List<Blob> SeparateNecks(bool[] mask, int width, List<Blob> blobs,
                                        int minimumPixels, double pixelsPerMm, PlatenDetectionReport report)
        {
            List<Blob> proposals = new List<Blob>();
            foreach (Blob blob in blobs)
            {
                RotatedBox box = PlatenGeometry.Fit(blob.Boundary);
                if (blob.Left <= 3 || blob.Top <= 3 || blob.Right >= width - 4 || box == null || blob.Count / (double)(box.Width * box.Height) >= 0.88)
                {
                    proposals.Add(blob);
                    continue;
                }
                int localWidth = blob.Right - blob.Left + 1, localHeight = blob.Bottom - blob.Top + 1;
                bool[] local = new bool[localWidth * localHeight];
                for (int row = 0; row < localHeight; row++)
                    Array.Copy(mask, (blob.Top + row) * width + blob.Left, local, row * localWidth, localWidth);
                // A 1.5 mm inset breaks a corner contact while leaving a receipt
                // or card core. It is only a proposal: gradients must restore the
                // physical edges, and a white band inside filled stock stays solid.
                local = Erode(local, localWidth, localHeight, Math.Max(1, (int)Math.Ceiling(pixelsPerMm * 1.5)));
                List<Blob> pieces = Components(local, localWidth, localHeight, minimumPixels);
                if (pieces.Count < 2)
                {
                    proposals.Add(blob);
                    continue;
                }
                report.Add("component " + blob.Left + "," + blob.Top + ": narrow contact split into " + pieces.Count + " core hypotheses");
                foreach (Blob piece in pieces)
                {
                    piece.Left += blob.Left; piece.Right += blob.Left;
                    piece.Top += blob.Top; piece.Bottom += blob.Top;
                    for (int index = 0; index < piece.Boundary.Count; index++)
                        piece.Boundary[index] = new PointF(piece.Boundary[index].X + blob.Left, piece.Boundary[index].Y + blob.Top);
                    proposals.Add(piece);
                }
                // Retain the parent as a fallback if the separated cores do not
                // support physical edges. Duplicate suppression accounts for it
                // when children have already been fitted successfully.
                proposals.Add(blob);
            }
            return proposals;
        }

        // =====================================================================
        // Turning a blob into an item
        // =====================================================================
        static CropRegion Accept(RawImage page, Blob blob, int w, int h, int scale, AutoCropOptions options, PlatenDetectionReport report, byte[] grey, bool observedFrameEdge = false, int removedFrameInset = 0)
        {
            // A component remaining immediately inside the removed calibration
            // band still touches the frame; it has not gained physical edges.
            int frameInset = Math.Max(removedFrameInset, Math.Max(2, Math.Min(w, h) / 260));
            int touching = (blob.Left <= frameInset ? 1 : 0) + (blob.Top <= frameInset ? 1 : 0)
                         + (blob.Right >= w - frameInset - 1 ? 1 : 0) + (blob.Bottom >= h - frameInset - 1 ? 1 : 0);
            string identity = "component " + blob.Left + "," + blob.Top + ".." + blob.Right + "," + blob.Bottom;
            // Contact does not prove a frame. Without an enclosing observable
            // boundary, full-bed stock remains ambiguous and must be reviewed.
            bool frameLimited = touching >= 3;
            // Interior texture can belong to several items surrounded by the
            // scanner frame. It must never certify that frame as one sheet and
            // suppress every subsequent item as a duplicate (LiDE mixed bed).
            // Only the separate full-span-edge proposal can authorise this path.
            if (frameLimited && (!observedFrameEdge || !DistributedTexture(grey, w, blob)))
            {
                report.Add("rejected " + identity + ": three or more frame contacts; no independently observed stock edge; frame or mixed bed unresolved");
                report.UnresolvedCandidates++;
                return null;
            }

            int x = blob.Left * scale, y = blob.Top * scale;
            int bw = (blob.Right - blob.Left + 1) * scale;
            int bh = (blob.Bottom - blob.Top + 1) * scale;
            bw = Math.Min(bw, page.Width - x);
            bh = Math.Min(bh, page.Height - y);
            if (bw < 8 || bh < 8)
            {
                report.Add("rejected " + identity + ": narrow debris below eight source pixels");
                return null;
            }
            RotatedBox oriented = PlatenGeometry.Fit(blob.Boundary);
            if (oriented == null)
            {
                report.Add("rejected " + identity + ": degenerate convex hull");
                return null;
            }

            double areaMm2 = oriented.Width * oriented.Height * scale * scale / Dpi(page.XDpi) / Dpi(page.YDpi) * 25.4 * 25.4;
            double stockAreaMm2 = blob.Count * (double)scale * scale / Dpi(page.XDpi) / Dpi(page.YDpi) * 25.4 * 25.4;
            if (areaMm2 < options.MinAreaMm2 || stockAreaMm2 < options.MinAreaMm2)
            {
                report.Add("rejected " + identity + ": oriented area below MinAreaMm2");
                return null;
            }

            // Solidity: a real item fills most of its own box. A ragged smear of
            // shadow does not, and this is the cheapest way to tell them apart.
            double fill = blob.Count / (double)(oriented.Width * oriented.Height);
            if (fill < 0.20)
            {
                report.Add("rejected " + identity + ": oriented solidity " + fill.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " below 0.20; insufficient stock for a supported outline");
                report.UnresolvedCandidates++;
                return null;
            }
            CropRegion region = Make(page, new Rectangle(x, y, bw, bh), oriented.Angle,
                fill > 0.9 ? CropConfidence.High : CropConfidence.Good,
                (float)fill, "oriented solidity " + fill.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            oriented.Width *= scale;
            oriented.Height *= scale;
            oriented.Center = new PointF(oriented.Center.X * scale, oriented.Center.Y * scale);
            for (int index = 0; index < oriented.Corners.Length; index++)
                oriented.Corners[index] = new PointF(oriented.Corners[index].X * scale, oriented.Corners[index].Y * scale);
            oriented.AABB = new Rectangle(x, y, bw, bh);
            region.Box = oriented;
            if (frameLimited)
            {
                // Distributed interior print supports a large sheet hypothesis,
                // but its hidden edges remain unknown. Preserve visible extent;
                // never claim the frame is a ruler measurement of physical stock.
                region = Make(page, new Rectangle(x, y, bw, bh), 0,
                    CropConfidence.Good, (float)fill,
                    "frame-limited sheet with distributed interior texture; visible extent only, review clipped edges");
                report.UnresolvedCandidates++;
            }
            report.Add("candidate " + identity + ": " + region.Reason + ", angle " + oriented.Angle.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            return region;
        }

        static CropRegion Make(RawImage page, Rectangle box, float skew,
                               CropConfidence confidence, float score, string reason)
        {
            CropRegion c = new CropRegion();
            c.NormRect = new RectangleF(box.X / (float)page.Width, box.Y / (float)page.Height,
                                        box.Width / (float)page.Width, box.Height / (float)page.Height);
            c.WidthInches = box.Width / Dpi(page.XDpi);
            c.HeightInches = box.Height / Dpi(page.YDpi);
            c.SkewDegrees = skew;
            c.Confidence = confidence;
            c.Score = score;
            c.Reason = reason;

            RotatedBox rb = new RotatedBox
            {
                IsValid = true,
                Angle = skew,
                RawAngle = skew,
                Width = box.Width,
                Height = box.Height,
                Center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f),
                AABB = box
            };
            rb.Corners = new PointF[]
            {
                new PointF(box.Left, box.Top), new PointF(box.Right, box.Top),
                new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom)
            };
            c.Box = rb;
            return c;
        }

        /// <summary>
        /// Top to bottom, then left to right, with rows banded: two cards whose
        /// tops differ by a few pixels belong to the same row and must be
        /// numbered left to right, not by that difference.
        /// </summary>
        static void Order(List<CropRegion> items)
        {
            if (items.Count == 0) return;
            List<float> heights = new List<float>();
            foreach (CropRegion item in items) heights.Add(item.NormRect.Height);
            heights.Sort();
            float band = heights[heights.Count / 2] * 0.5f;
            items.Sort(delegate (CropRegion first, CropRegion second)
            {
                int comparison = first.NormRect.Y.CompareTo(second.NormRect.Y);
                return comparison != 0 ? comparison : first.NormRect.X.CompareTo(second.NormRect.X);
            });
            // Fixed row anchors make ordering transitive. A pairwise tolerance
            // comparator can say A precedes B, B precedes C and C precedes A.
            for (int start = 0; start < items.Count;)
            {
                int end = start + 1;
                while (end < items.Count && items[end].NormRect.Y - items[start].NormRect.Y <= band) end++;
                items.Sort(start, end - start, Comparer<CropRegion>.Create(delegate (CropRegion first, CropRegion second)
                {
                    return first.NormRect.X.CompareTo(second.NormRect.X);
                }));
                start = end;
            }
            for (int index = 0; index < items.Count; index++) items[index].Index = index + 1;
        }

        // A wide stock edge can remain separate in the pixels even when lid
        // shading joins its mask to smaller items above it. A quiet exterior
        // followed by a dark valley and a sharp return supplies a new proposal.
        // This bounded recovery covers near-horizontal stock, not arbitrary curl
        // or a hidden edge. The ordinary fitter must still validate the proposal.
        static void FindShadowSeparatedStock(RawImage page, byte[] grey, byte[][] channels,
            int width, int height, int scale, AutoCropOptions options, PlatenDetectionReport report,
            List<CropRegion> found, double minimumGradient)
        {
            int exterior = Math.Max(8, (int)Math.Round(5 * page.YDpi / 25.4 / scale));
            int[] supportCounts = new int[height], firstColumns = new int[height], lastColumns = new int[height];
            for (int row = exterior + 3; row < height - exterior; row++)
            {
                int supported = 0, first = width, last = 0;
                for (int column = 3; column < width - 3; column += 3)
                {
                    int inside = grey[(row + 2) * width + column];
                    int valley = Math.Min(grey[(row - 1) * width + column], grey[(row - 3) * width + column]);
                    int outside = grey[(row - exterior) * width + column];
                    int farther = grey[(row - exterior - 3) * width + column];
                    // Eighteen levels exclude faint streaks; a three-row exterior
                    // change under eight levels allows the captured lid gradient.
                    if (inside - valley < 18 || outside - valley < 18 || Math.Abs(outside - farther) > 8) continue;
                    supported++; first = Math.Min(first, column); last = column;
                }
                supportCounts[row] = supported; firstColumns[row] = first; lastColumns[row] = last;
            }
            int previousBoundary = -exterior;
            for (int row = exterior + 3; row < height - exterior; row++)
            {
                if (supportCounts[row] * 3 < width * 0.60 || row - previousBoundary < exterior) continue;
                // The first row of a blurred shadow may only expose the centre
                // of an edge. Its best-supported row supplies the lateral extent.
                int bestRow = row;
                for (int candidateRow = row + 1; candidateRow < Math.Min(height - exterior, row + exterior); candidateRow++)
                    if (supportCounts[candidateRow] > supportCounts[bestRow]) bestRow = candidateRow;
                row = bestRow;
                previousBoundary = row;
                int first = firstColumns[row], last = lastColumns[row];
                int left = Math.Max(0, first - 3), right = Math.Min(width, last + 3);
                // This recovery requires the lower stock edge near the captured
                // bottom. Floating wide stock ending mid-bed still relies on the
                // component finder; no unseen height is inferred here.
                int bottom = height - exterior;
                CropRegion candidate = Make(page, Rectangle.FromLTRB(left * scale, row * scale, right * scale, bottom * scale),
                    0, CropConfidence.Good, 1, "wide edge with a quiet exterior and shadow valley");
                if (Covered(candidate, found))
                {
                    report.Add("rejected shadow-separated proposal at row " + row + ": already covered by an accepted boundary");
                    continue;
                }
                if (!PlatenEdgeFitter.Fit(channels, width, height, scale, page, candidate, minimumGradient))
                {
                    report.Add("rejected shadow-separated proposal at row " + row + ": " + candidate.Reason);
                    continue;
                }
                if (candidate.WidthInches * candidate.HeightInches * 25.4 * 25.4 < options.MinAreaMm2)
                {
                    report.Add("rejected shadow-separated proposal: fitted stock below minimum area");
                    continue;
                }
                candidate.Confidence = CropConfidence.Good;
                candidate.Reason += "; separated from shading by an observed exterior shadow edge; review extent";
                if (candidate.Confidence < options.MinConfidence)
                {
                    report.Add("rejected shadow-separated stock: review confidence below requested " + options.MinConfidence);
                    report.UnresolvedCandidates++;
                    continue;
                }
                if (Covered(candidate, found))
                {
                    report.Add("rejected duplicate fitted shadow-separated boundary " + candidate.NormRect);
                    continue;
                }
                if (!ReplaceContained(candidate, found, report)) continue;
                found.Add(candidate);
                report.Add("accepted shadow-separated stock " + candidate.NormRect + ": " + candidate.Reason);
            }
        }

        static CropRegion FindFrameSheet(RawImage page, byte[] grey, int width, int height,
                                         int scale, AutoCropOptions options, PlatenDetectionReport report)
        {
            // A sheet touching three sides leaves one measurable full-span edge.
            // Bright stock can normalise to the lid and disappear from every dark
            // threshold, so this evidence is measured on the original grey plane.
            // Full-bleed blank stock with no remaining edge is still unknowable.
            for (int direction = 0; direction < 2; direction++)
            {
                int acrossExtent = direction == 0 ? height : width;
                int alongExtent = direction == 0 ? width : height;
                for (int across = 20; across < acrossExtent - 20; across++)
                {
                    int supported = 0, count = 0;
                    for (int along = 3; along < alongExtent - 3; along += 3)
                    {
                        int pixel = direction == 0 ? across * width + along : along * width + across;
                        int before = pixel - (direction == 0 ? width : 1);
                        if (Math.Abs(grey[pixel] - grey[before]) >= 4) supported++;
                        count++;
                    }
                    if (supported < count * 0.85) continue;
                    Blob leading = new Blob { Left = 0, Top = 0, Right = direction == 0 ? width - 1 : across - 1,
                        Bottom = direction == 0 ? across - 1 : height - 1 };
                    Blob trailing = new Blob { Left = direction == 0 ? 0 : across, Top = direction == 0 ? across : 0,
                        Right = width - 1, Bottom = height - 1 };
                    bool leadingTexture = DistributedTexture(grey, width, leading);
                    bool trailingTexture = DistributedTexture(grey, width, trailing);
                    if (leadingTexture == trailingTexture) continue;
                    Blob sheet = leadingTexture ? leading : trailing;
                    sheet.Count = (sheet.Right - sheet.Left + 1) * (sheet.Bottom - sheet.Top + 1);
                    sheet.Boundary.Add(new PointF(sheet.Left + 0.5f, sheet.Top + 0.5f));
                    sheet.Boundary.Add(new PointF(sheet.Right + 0.5f, sheet.Top + 0.5f));
                    sheet.Boundary.Add(new PointF(sheet.Right + 0.5f, sheet.Bottom + 0.5f));
                    sheet.Boundary.Add(new PointF(sheet.Left + 0.5f, sheet.Bottom + 0.5f));
                    report.Add("large sheet hypothesis: full-span edge at " + across + ", axis " + direction + "; distributed interior texture on one side only");
                    return Accept(page, sheet, width, height, scale, options, report, grey, true);
                }
            }
            return null;
        }

        // A component is a proposal, not an irrevocable owner of its interior.
        // Independent complete boundaries may expose a weak enclosing fit. A
        // supported outer contour, however, must keep its printed panels inside.
        static void ResolveBoundaryAlternatives(List<CropRegion> alternatives, List<CropRegion> found,
            byte[][] channels, int width, int height, int scale, RawImage page, PlatenDetectionReport report)
        {
            for (int index = found.Count - 1; index >= 0; index--)
            {
                CropRegion enclosing = found[index];
                if (enclosing.Outline != null || enclosing.Reason.StartsWith("frame-limited", StringComparison.Ordinal) || PlatenBoundaryProposals.BoundarySupport(channels, width, height, scale, enclosing) >= 0.5) continue;
                List<CropRegion> independent = new List<CropRegion>();
                foreach (CropRegion candidate in alternatives)
                {
                    if (StockArea(candidate) > StockArea(enclosing) * 0.5 || OverlapArea(candidate, enclosing) < StockArea(candidate) * 0.95) continue;
                    bool separate = true;
                    foreach (CropRegion other in independent)
                        if (OverlapArea(other, candidate) > Math.Min(StockArea(other), StockArea(candidate)) * 0.1) separate = false;
                    if (separate) independent.Add(candidate);
                }
                if (independent.Count < 2) continue;
                report.Add("superseded weak enclosing fit " + enclosing.NormRect + ": " + independent.Count + " independent boundaries contradict its incomplete perimeter");
                found.RemoveAt(index);
            }
            foreach (CropRegion candidate in alternatives)
            {
                if (Covered(candidate, found)) continue;
                List<CropRegion> nested = new List<CropRegion>();
                double pixelsPerMm = Math.Min(page.XDpi, page.YDpi) / scale / 25.4;
                foreach (CropRegion item in found)
                    if (item.Outline == null && OverlapArea(item, candidate) > StockArea(item) * 0.95)
                        nested.Add(item);
                bool internalPanels = nested.Count > 1 &&
                    PlatenBoundaryProposals.BoundarySupport(channels, width, height, scale, candidate) >= 0.8 &&
                    PlatenBoundaryProposals.ExteriorSupport(channels, width, height, scale, candidate, pixelsPerMm) >= 0.7;
                foreach (CropRegion item in nested)
                    if (PlatenBoundaryProposals.ExteriorSupport(channels, width, height, scale, item, pixelsPerMm) >= 0.7) internalPanels = false;
                if (internalPanels)
                    foreach (CropRegion item in nested)
                    {
                        found.Remove(item);
                        report.Add("superseded internal panel " + item.NormRect + ": exterior remains textured inside a supported stock boundary");
                    }
                if (!ReplaceContained(candidate, found, report)) continue;
                found.Add(candidate);
                report.Add("accepted independent boundary " + candidate.NormRect + ": " + candidate.Reason);
            }
        }

        static int FittedFrameContacts(CropRegion item, RawImage page, double inset)
        {
            return (item.NormRect.Left * page.Width <= inset ? 1 : 0)
                + (item.NormRect.Top * page.Height <= inset ? 1 : 0)
                + (item.NormRect.Right * page.Width >= page.Width - inset ? 1 : 0)
                + (item.NormRect.Bottom * page.Height >= page.Height - inset ? 1 : 0);
        }

        static double StockArea(CropRegion item)
        {
            return item.Outline == null ? item.Box.Width * item.Box.Height : Math.Abs(PlatenContour.Area(item.Outline));
        }

        static double OverlapArea(CropRegion first, CropRegion second)
        {
            if (first.Outline == null && second.Outline == null)
                return PlatenGeometry.IntersectionArea(first.Box.Corners, second.Box.Corners);
            PointF[] firstOutline = first.Outline ?? first.Box.Corners;
            PointF[] secondOutline = second.Outline ?? second.Box.Corners;
            Rectangle intersection = Rectangle.Intersect(first.Box.AABB, second.Box.AABB);
            if (intersection.Width <= 0 || intersection.Height <= 0) return 0;
            int inside = 0;
            // This grid is only for duplicate suppression, never for extraction
            // or size measurement. A card in an L-shaped notch is not contained
            // by the L merely because its oriented bounding rectangle covers it.
            for (int row = 0; row < 40; row++)
                for (int column = 0; column < 40; column++)
                {
                    double x = intersection.Left + (column + 0.5) * intersection.Width / 40;
                    double y = intersection.Top + (row + 0.5) * intersection.Height / 40;
                    if (PlatenContour.Contains(firstOutline, x, y) && PlatenContour.Contains(secondOutline, x, y)) inside++;
                }
            return intersection.Width * (double)intersection.Height * inside / 1600;
        }

        static bool ReplaceContained(CropRegion candidate, List<CropRegion> accepted, PlatenDetectionReport report)
        {
            List<CropRegion> contained = new List<CropRegion>();
            foreach (CropRegion item in accepted)
                if (OverlapArea(item, candidate) > StockArea(item) * 0.95)
                    contained.Add(item);
            foreach (CropRegion item in contained)
            {
                // A contained item whose own edges are soft does not block.
                //
                // The rule below protects a separately measured document from
                // being swallowed by a boundary that leans on the image frame.
                // Printing on a sheet is not such a document: its edges are a
                // broad tonal gradient, where real stock gives a sharp
                // transition with a shadow beside it. An open passport running
                // off the end of the scan was refused because a printed panel
                // on its own right-hand page objected to being absorbed.
                bool printedPanel = item.Reason.Contains("broad gradient support");

                if (!printedPanel && candidate.Reason.Contains("reaches image frame") &&
                    !item.Reason.Contains("reaches image frame") &&
                    !item.Reason.StartsWith("frame-limited", StringComparison.Ordinal))
                {
                    report.Add("rejected enclosing boundary " + candidate.NormRect
                        + ": an unmeasured frame edge cannot replace observed stock edges; blocked by "
                        + item.NormRect + " [" + item.Reason + "]");
                    report.UnresolvedCandidates++;
                    return false;
                }
            }
            if (contained.Count > 1)
            {
                report.Add("rejected enclosing boundary " + candidate.NormRect + ": would merge " + contained.Count + " independently fitted items");
                report.UnresolvedCandidates++;
                return false;
            }
            foreach (CropRegion item in contained)
            {
                report.Add("superseded internal boundary " + item.NormRect + ": a larger physical edge was supported by another evidence pass");
                accepted.Remove(item);
            }
            return true;
        }

        /// <summary>
        /// Removes a soft-edged region that lies across a measured one.
        ///
        /// A broad tonal gradient is what a shadow or printing produces; real
        /// stock gives a sharp transition with a shadow beside it, and the
        /// fitter already records which it saw. So a region whose own edges are
        /// soft, sitting on top of something measured, is that item's shadow or
        /// its print - not a second document lying across the first. One of them
        /// straddled the top edge of the owner's passport and was delivered as
        /// an extra page of nothing.
        ///
        /// Done here rather than in Covered because the two can be accepted in
        /// either order, by different passes, and Covered only ever sees what
        /// happens to have been accepted already.
        /// </summary>
        static void DropSoftRegionsLyingAcrossItems(List<CropRegion> found, PlatenDetectionReport report)
        {
            for (int index = found.Count - 1; index >= 0; index--)
            {
                CropRegion soft = found[index];
                if (soft.Reason == null || !soft.Reason.Contains("broad gradient support")) continue;

                for (int other = 0; other < found.Count; other++)
                {
                    if (other == index) continue;
                    CropRegion firm = found[other];
                    if (firm.Reason != null && firm.Reason.Contains("broad gradient support")) continue;

                    // Measured against the soft region's own area, so a large
                    // weak region cannot dilute its way past by being big.
                    if (OverlapArea(firm, soft) <= StockArea(soft) * 0.25) continue;

                    report.Add("dropped soft-edged region " + soft.NormRect
                        + ": it lies across the measured " + firm.NormRect);
                    found.RemoveAt(index);
                    break;
                }
            }
        }

        static bool Covered(CropRegion candidate, List<CropRegion> accepted)
        {
            foreach (CropRegion item in accepted)
            {
                double intersection = OverlapArea(item, candidate);
                double smaller = Math.Min(StockArea(item), StockArea(candidate));
                // 80% containment suppresses threshold duplicates while leaving
                // corner contacts independent. This is proposal suppression, not
                // proof that two visually inseparable touching sheets are one.
                if (intersection > smaller * 0.8 && StockArea(candidate) <= StockArea(item) * 1.12) return true;

            }
            return false;
        }

        static void SafeLog(string message)
        {
            // Diagnostic subscribers run outside this component's control. A
            // failed logger must not escape a method promising a safe refusal.
            try
            {
                Action<string> logger = Log;
                if (logger != null) logger(message);
            }
            catch (Exception) { }
        }

        static double Dpi(double v)
        {
            return (v > 0 && !double.IsInfinity(v) && !double.IsNaN(v)) ? v : 300.0;
        }
    }
}

