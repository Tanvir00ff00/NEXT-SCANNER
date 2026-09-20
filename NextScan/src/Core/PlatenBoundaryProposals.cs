// =============================================================================
// NextScan Studio - boundary proposals independent of connected foreground
// Plan ref: universal platen work order section 4; research follow-up September 16.
// A cast shadow can join separate sheets into one component. Coherent gradient
// chains offer alternative seeds without closing those gaps or assuming sizes.
// This is an original managed implementation, not LSD or GrabCut. Rectangular
// proposals supplement the existing free-form contour path; they cannot explain
// an invisible, occluded or curled boundary. Every proposal needs a physical fit.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace NextScan.Core
{
    internal static class PlatenBoundaryProposals
    {
        internal sealed class Segment
        {
            internal double X, Y, Cosine, Sine, Start, End;
            internal bool FaintExtension;
        }

        internal static List<CropRegion> Find(byte[][] channels, int width, int height, int scale,
            RawImage page, AutoCropOptions options, PlatenDetectionReport report, Func<CropRegion, bool> alreadyExplained, List<Segment> segments)
        {
            List<Segment> primary = segments.FindAll(delegate(Segment segment) { return !segment.FaintExtension; });
            List<CropRegion> result = FindLevel(channels, width, height, scale, page, options, report, alreadyExplained, primary, false);
            // Fainter chains may extend an inner printed panel to its physical
            // stock, but must not displace a complete stronger fit by seed order.
            List<CropRegion> supplement = FindLevel(channels, width, height, scale, page, options, report,
                delegate(CropRegion candidate)
                {
                    if (alreadyExplained(candidate)) return true;
                    foreach (CropRegion existing in result)
                    {
                        double area = candidate.Box.Width * candidate.Box.Height;
                        double existingArea = existing.Box.Width * existing.Box.Height;
                        if (area <= existingArea * 1.12 && PlatenGeometry.IntersectionArea(candidate.Box.Corners,
                            existing.Box.Corners) > 0.8 * Math.Min(area, existingArea)) return true;
                    }
                    return false;
                }, segments, true);
            result.AddRange(supplement);
            result.Sort(delegate(CropRegion first, CropRegion second)
            {
                return (second.Box.Width * second.Box.Height).CompareTo(first.Box.Width * first.Box.Height);
            });
            return result;
        }

        static List<CropRegion> FindLevel(byte[][] channels, int width, int height, int scale,
            RawImage page, AutoCropOptions options, PlatenDetectionReport report, Func<CropRegion, bool> alreadyExplained,
            List<Segment> segments, bool supplementary)
        {
            double pixelsPerMm = Math.Min(page.XDpi, page.YDpi) / scale / 25.4;
            List<CropRegion> result = new List<CropRegion>();
            List<CropRegion> measuredSeeds = new List<CropRegion>();
            List<CropRegion> candidates = new List<CropRegion>();
            report.Add("independent gradient chains: " + segments.Count);
            for (int first = 0; first < segments.Count; first++)
            {
                Segment firstSegment = segments[first];
                for (int second = first + 1; second < segments.Count; second++)
                {
                    Segment secondSegment = segments[second];
                    if (supplementary && !firstSegment.FaintExtension && !secondSegment.FaintExtension) continue;
                    double parallel = Math.Abs(firstSegment.Cosine * secondSegment.Cosine + firstSegment.Sine * secondSegment.Sine);
                    // Three degrees tolerates endpoint noise before the edge fitter
                    // tests the opposite physical lines at its tighter tolerance.
                    if (parallel < Math.Cos(3 * Math.PI / 180)) continue;
                    double displacement = (secondSegment.X - firstSegment.X) * firstSegment.Cosine + (secondSegment.Y - firstSegment.Y) * firstSegment.Sine;
                    double separation = -(secondSegment.X - firstSegment.X) * firstSegment.Sine + (secondSegment.Y - firstSegment.Y) * firstSegment.Cosine;
                    if (Math.Abs(separation) < 8 * pixelsPerMm) continue;
                    double secondStart = displacement - (secondSegment.End - secondSegment.Start) / 2;
                    double secondEnd = displacement + (secondSegment.End - secondSegment.Start) / 2;
                    // Partial chains may stop at rounded corners or low contrast.
                    // They must share most of the shorter span; the complete
                    // perimeter test, rather than endpoints, validates the stock.
                    double overlap = Math.Min(firstSegment.End, secondEnd) - Math.Max(firstSegment.Start, secondStart);
                    if (overlap < 0.70 * Math.Min(firstSegment.End - firstSegment.Start, secondEnd - secondStart)) continue;
                    double start = Math.Min(firstSegment.Start, secondStart), end = Math.Max(firstSegment.End, secondEnd);
                    double extensionLimit = Math.Max(6 * pixelsPerMm, end - start);
                    foreach (Segment side in segments)
                    {
                        if (Math.Abs(side.Cosine * firstSegment.Cosine + side.Sine * firstSegment.Sine) > Math.Sin(3 * Math.PI / 180)) continue;
                        double along = (side.X - firstSegment.X) * firstSegment.Cosine + (side.Y - firstSegment.Y) * firstSegment.Sine;
                        double across = -(side.X - firstSegment.X) * firstSegment.Sine + (side.Y - firstSegment.Y) * firstSegment.Cosine;
                        double half = (side.End - side.Start) / 2;
                        double shared = Math.Min(Math.Max(0, separation), across + half) - Math.Max(Math.Min(0, separation), across - half);
                        // A short photo edge above the booklet spans under 25%
                        // of its width. Require 40% before extending a pair, so
                        // neighbouring stock cannot supply the missing corner.
                        if (shared < 0.5 * Math.Min(Math.Abs(separation), 2 * half) || shared < 0.4 * Math.Abs(separation)) continue;
                        // A pale side can survive only over half its length.
                        // An observed perpendicular chain may restore that span;
                        // the complete perimeter is still checked before fitting.
                        if (along < start && start - along < extensionLimit) start = along;
                        if (along > end && along - end < extensionLimit) end = along;
                    }
                    if ((end - start) * Math.Abs(separation) / (pixelsPerMm * pixelsPerMm) < options.MinAreaMm2) continue;
                    List<PointF> points = new List<PointF>();
                    foreach (double along in new double[] { start, end })
                        foreach (double across in new double[] { 0, separation })
                            points.Add(new PointF((float)((firstSegment.X + along * firstSegment.Cosine - across * firstSegment.Sine) * scale),
                                (float)((firstSegment.Y + along * firstSegment.Sine + across * firstSegment.Cosine) * scale)));
                    RotatedBox box = PlatenGeometry.Fit(points);
                    if (box == null) continue;
                    float left = float.MaxValue, top = float.MaxValue, right = 0, bottom = 0;
                    foreach (PointF corner in box.Corners)
                    {
                        left = Math.Min(left, corner.X); top = Math.Min(top, corner.Y);
                        right = Math.Max(right, corner.X); bottom = Math.Max(bottom, corner.Y);
                    }
                    // One acquisition edge can truncate real stock. Its visible
                    // extent is fitted at lower confidence, never extrapolated.
                    int contacts = (left < 2 * scale ? 1 : 0) + (top < 2 * scale ? 1 : 0)
                        + (right > page.Width - 2 * scale ? 1 : 0) + (bottom > page.Height - 2 * scale ? 1 : 0);
                    if (contacts > 1) continue;
                    left = Math.Max(0, left); top = Math.Max(0, top);
                    right = Math.Min(page.Width, right); bottom = Math.Min(page.Height, bottom);
                    CropRegion candidate = new CropRegion { Box = box, SkewDegrees = box.Angle,
                        NormRect = RectangleF.FromLTRB(left / page.Width, top / page.Height, right / page.Width, bottom / page.Height),
                        Reason = "independent opposite gradient chains" };
                    box.AABB = Rectangle.FromLTRB((int)left, (int)top, (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
                    string seed = candidate.NormRect.ToString();
                    if (alreadyExplained(candidate))
                    {
                        report.Add("rejected boundary seed " + seed + ": already explained by a supported existing boundary");
                        continue;
                    }
                    bool duplicate = false;
                    foreach (CropRegion measured in measuredSeeds)
                    {
                        double intersection = PlatenGeometry.IntersectionArea(candidate.Box.Corners, measured.Box.Corners);
                        if (intersection > 0.97 * Math.Max(candidate.Box.Width * candidate.Box.Height, measured.Box.Width * measured.Box.Height))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate)
                    {
                        report.Add("rejected duplicate boundary seed " + seed);
                        continue;
                    }
                    // Chain endpoints are only a seed. Rounded corners and a
                    // spine can move that seed several working pixels; demanding
                    // final-fit precision here prevented the fitter from running.
                    if (BoundarySupport(channels, width, height, scale, candidate,
                        Math.Max(2, (int)Math.Ceiling(1.5 * pixelsPerMm)), 20) < 0.55)
                    {
                        report.Add("rejected boundary proposal " + seed + ": seed perimeter is incomplete");
                        continue;
                    }
                    measuredSeeds.Add(new CropRegion { Box = candidate.Box });
                    candidates.Add(candidate);
                }
            }
            CropRegion[] fitted = new CropRegion[candidates.Count];
            PlatenDetectionReport[] decisions = new PlatenDetectionReport[candidates.Count];
            // Selection and seed de-duplication are serial. Independent fits own
            // their regions and reports; merging by index preserves every tie and
            // diagnostic order while bounding simultaneous edge fits to two.
            Parallel.For(0, candidates.Count, new ParallelOptions { MaxDegreeOfParallelism = 2 }, delegate(int index)
            {
                decisions[index] = new PlatenDetectionReport();
                if (FitProposal(channels, width, height, scale, page, candidates[index], options, decisions[index], pixelsPerMm))
                    fitted[index] = candidates[index];
            });
            for (int index = 0; index < fitted.Length; index++)
            {
                report.Lines.AddRange(decisions[index].Lines);
                report.UnresolvedCandidates += decisions[index].UnresolvedCandidates;
                if (fitted[index] != null) result.Add(fitted[index]);
            }
            result.Sort(delegate(CropRegion first, CropRegion second)
            {
                return (second.Box.Width * second.Box.Height).CompareTo(first.Box.Width * first.Box.Height);
            });
            return result;
        }

        static bool FitProposal(byte[][] channels, int width, int height, int scale, RawImage page,
            CropRegion candidate, AutoCropOptions options, PlatenDetectionReport report, double pixelsPerMm)
        {
            string seed = candidate.NormRect.ToString();

            if (!PlatenEdgeFitter.Fit(channels, width, height, scale, page, candidate, 2))
            {
                report.Add("rejected boundary proposal " + seed + ": " + candidate.Reason);
                return false;
            }

            // The capture is not a document.
            //
            // With the paper size set to Maximum the preview takes in the
            // platen's own border, and a boundary drawn round the whole of it
            // fits perfectly well: four edges, quiet exterior, complete
            // perimeter. It was then accepted and superseded the five real items
            // sitting on the glass, which is how a bed of cards and a passport
            // came back as one 8.47 x 11.63 in region and the operator saw no
            // outlines at all.
            //
            // The component path has refused this for a long time - reaching
            // three sides of the frame means the glass rather than something on
            // it - but the proposal path had no equivalent. Both dimensions are
            // required, so a real sheet that fills the bed one way round is
            // still found: A4 on this platen is 97.2 % of its width, Letter is
            // 94 % of its height.
            const float WholeCapture = 0.98f;
            if (candidate.NormRect.Width >= WholeCapture && candidate.NormRect.Height >= WholeCapture)
            {
                report.Add("rejected boundary proposal " + seed + ": covers the whole capture; that is the glass, not an item on it");
                return false;
            }

            double support = BoundarySupport(channels, width, height, scale, candidate);
            string exteriorDetail;
            double exterior = ExteriorSupport(channels, width, height, scale, candidate, pixelsPerMm, out exteriorDetail);
            if (HasContinuingBorders(channels, width, height, scale, candidate, pixelsPerMm))
            {
                report.Add("rejected boundary proposal " + seed + ": opposite borders continue beyond the same side; probable internal division");
                return false;
            }
            if (support < 0.80 || exterior < 0.70 || candidate.Confidence < options.MinConfidence)
            {
                report.Add("rejected boundary proposal " + seed + ": complete perimeter support " + support.ToString("0.00")
                    + "; quiet exterior " + exterior.ToString("0.00") + " [per side: " + exteriorDetail + "]");
                return false;
            }
            if (exterior < 0.85) candidate.Confidence = CropConfidence.Good;
            if (candidate.Confidence < options.MinConfidence)
            {
                report.Add("rejected boundary proposal " + seed + ": exterior review confidence below requested " + options.MinConfidence);
                report.UnresolvedCandidates++;
                return false;
            }
            candidate.Reason += "; independent chains; complete perimeter support " + support.ToString("0.00") + "; quiet exterior " + exterior.ToString("0.00");
            report.Add("validated independent boundary " + candidate.NormRect + ": " + candidate.Reason);
            return true;
        }

        internal static List<Segment> Segments(byte[][] channels, int width, int height, double pixelsPerMm)
        {
            // Strong and faint chains remain separate hypotheses: admitting weak
            // noise into a strong chain can bend it and erase a genuine edge.
            float[] horizontalGradient, verticalGradient, squaredMagnitude;
            PrepareGradients(channels, width, height, out horizontalGradient, out verticalGradient, out squaredMagnitude);
            List<Segment> segments = SegmentsAtThreshold(horizontalGradient, verticalGradient, squaredMagnitude, width, height, pixelsPerMm, 16);
            List<Segment> faintSegments = SegmentsAtThreshold(horizontalGradient, verticalGradient, squaredMagnitude, width, height, pixelsPerMm, 4);
            faintSegments.AddRange(SegmentsAtThreshold(horizontalGradient, verticalGradient, squaredMagnitude, width, height, pixelsPerMm, 2.25));
            foreach (Segment candidate in faintSegments)
            {
                bool repeated = false;
                foreach (Segment existing in segments)
                {
                    double horizontal = candidate.X - existing.X, vertical = candidate.Y - existing.Y;
                    if (horizontal * horizontal + vertical * vertical <= 4 &&
                        Math.Abs((candidate.End - candidate.Start) - (existing.End - existing.Start)) <= 4 &&
                        Math.Abs(candidate.Cosine * existing.Cosine + candidate.Sine * existing.Sine) > Math.Cos(Math.PI / 180))
                    {
                        repeated = true;
                        break;
                    }
                }
                if (!repeated) segments.Add(candidate);
            }
            segments.Sort(delegate(Segment first, Segment second) { return (second.End - second.Start).CompareTo(first.End - first.Start); });
            if (segments.Count > 160) segments.RemoveRange(160, segments.Count - 160);
            return segments;
        }

        static void PrepareGradients(byte[][] channels, int width, int height,
            out float[] horizontal, out float[] vertical, out float[] squaredMagnitude)
        {
            int count = width * height;
            horizontal = new float[count]; vertical = new float[count]; squaredMagnitude = new float[count];
            for (int y = 2; y < height - 2; y++)
                for (int x = 2; x < width - 2; x++)
                {
                    int position = y * width + x;
                    double best = 0;
                    foreach (byte[] channel in channels)
                    {
                        double horizontalDifference = (channel[position + 1 - width] + 2 * channel[position + 1] + channel[position + 1 + width]
                            - channel[position - 1 - width] - 2 * channel[position - 1] - channel[position - 1 + width]) / 8.0;
                        double verticalDifference = (channel[position + width - 1] + 2 * channel[position + width] + channel[position + width + 1]
                            - channel[position - width - 1] - 2 * channel[position - width] - channel[position - width + 1]) / 8.0;
                        double magnitude = horizontalDifference * horizontalDifference + verticalDifference * verticalDifference;
                        if (magnitude <= best) continue;
                        best = magnitude; horizontal[position] = (float)horizontalDifference; vertical[position] = (float)verticalDifference;
                    }
                    // This normalised Sobel response is half the step contrast.
                    // Rasterisation of the five-level, 0.45 mm shadow in the
                    // all-angle fixture lowers some connecting pixels to 1.5.
                    // Final perimeter validation still requires four-level contrast.
                    squaredMagnitude[position] = (float)best;
                    if (best < 2.25) continue;
                    double length = Math.Sqrt(best);
                    horizontal[position] /= (float)length; vertical[position] /= (float)length;
                }
        }

        static List<Segment> SegmentsAtThreshold(float[] horizontal, float[] vertical, float[] squaredMagnitude,
            int width, int height, double pixelsPerMm, double squaredFloor)
        {
            int count = width * height;
            bool[] eligible = new bool[count], visited = new bool[count];
            for (int index = 0; index < count; index++) eligible[index] = squaredMagnitude[index] >= squaredFloor;
            List<Segment> result = new List<Segment>();
            int[] queue = new int[count];
            // A rasterised faint diagonal alternates Sobel normals across its staircase.
            // A 30-degree growth window retains those pixels; the fitted chain
            // still has to remain within 1.5 pixels of one straight line.
            double alignment = Math.Cos((squaredFloor < 4 ? 30 : 15) * Math.PI / 180);
            for (int seed = 0; seed < count; seed++)
            {
                if (!eligible[seed] || visited[seed]) continue;
                int head = 0, tail = 1;
                queue[0] = seed; visited[seed] = true;
                double normalX = horizontal[seed], normalY = vertical[seed];
                double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, sumYY = 0, sumMagnitude = 0;
                while (head < tail)
                {
                    int current = queue[head++], x = current % width, y = current / width;
                    sumX += x; sumY += y; sumXX += x * (double)x; sumXY += x * (double)y; sumYY += y * (double)y;
                    sumMagnitude += squaredMagnitude[current];
                    for (int row = -1; row <= 1; row++)
                        for (int column = -1; column <= 1; column++)
                        {
                            int next = current + row * width + column;
                            if (!eligible[next] || visited[next]) continue;
                            if (horizontal[next] * normalX + vertical[next] * normalY < alignment) continue;
                            visited[next] = true; queue[tail++] = next;
                        }
                }
                if (tail < 8 * pixelsPerMm) continue;
                // The wider direction window exists for faint raster staircases.
                // Strong print already has a stricter chain hypothesis; growing
                // that print through weak noise creates expensive false seeds.
                if (squaredFloor < 4 && sumMagnitude / tail >= 16) continue;
                double centreX = sumX / tail, centreY = sumY / tail;
                double angle = Math.Atan2(2 * (sumXY / tail - centreX * centreY),
                    sumXX / tail - centreX * centreX - sumYY / tail + centreY * centreY) / 2;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                double start = double.MaxValue, end = double.MinValue, squaredDistance = 0;
                for (int index = 0; index < tail; index++)
                {
                    double x = queue[index] % width - centreX, y = queue[index] / width - centreY;
                    double along = x * cosine + y * sine, across = -x * sine + y * cosine;
                    start = Math.Min(start, along); end = Math.Max(end, along); squaredDistance += across * across;
                }
                if (end - start < 8 * pixelsPerMm || Math.Sqrt(squaredDistance / tail) > 1.5) continue;
                // Store the midpoint so opposite spans can be compared independently
                // of how gradient thickness weighted the chain's centroid.
                double middle = (start + end) / 2;
                result.Add(new Segment { X = centreX + middle * cosine, Y = centreY + middle * sine,
                    Cosine = cosine, Sine = sine, Start = start - middle, End = end - middle, FaintExtension = squaredFloor < 4 });
            }
            result.Sort(delegate(Segment first, Segment second) { return (second.End - second.Start).CompareTo(first.End - first.Start); });
            // Long supported chains dominate proposals. Bound pair enumeration on
            // densely printed pages; the component/contour path remains available.
            if (result.Count > 160) result.RemoveRange(160, result.Count - 160);
            return result;
        }

        static bool OnFrame(PointF first, PointF last, int width, int height, int scale, int tolerance)
        {
            return (first.X <= tolerance * scale && last.X <= tolerance * scale) ||
                (first.Y <= tolerance * scale && last.Y <= tolerance * scale) ||
                (first.X >= (width - tolerance) * scale && last.X >= (width - tolerance) * scale) ||
                (first.Y >= (height - tolerance) * scale && last.Y >= (height - tolerance) * scale);
        }
        internal static double BoundarySupport(byte[][] channels, int width, int height, int scale, CropRegion candidate, int searchRadius = 2, int maximumSamples = 64)
        {
            double minimum = 1;
            for (int edge = 0; edge < 4; edge++)
            {
                PointF first = candidate.Box.Corners[edge], last = candidate.Box.Corners[(edge + 1) % 4];
                // Seed uncertainty at a clipped, sloping edge spans the fitter
                // six-millimetre search band. A final fit only exempts the actual
                // two-pixel frame edge; the other visible sides remain measured.
                if (OnFrame(first, last, width, height, scale, searchRadius > 2 ? searchRadius * 4 : 2)) continue;
                double horizontalDifference = (last.X - first.X) / scale, verticalDifference = (last.Y - first.Y) / scale;
                double length = Math.Sqrt(horizontalDifference * horizontalDifference + verticalDifference * verticalDifference), normalX = -verticalDifference / length, normalY = horizontalDifference / length;
                int samples = Math.Min(maximumSamples, Math.Max(16, (int)(length / 3))), supported = 0;
                int[] sectionTotal = new int[4], sectionSupported = new int[4];
                for (int sample = 0; sample < samples; sample++)
                {
                    double fraction = 0.04 + 0.92 * (sample + 0.5) / samples;
                    double x = first.X / scale + horizontalDifference * fraction, y = first.Y / scale + verticalDifference * fraction;
                    double strongest = 0;
                    foreach (byte[] channel in channels)
                        for (int offset = -searchRadius; offset <= searchRadius; offset++)
                        {
                            double before = Sample(channel, width, height, x + normalX * (offset - 1), y + normalY * (offset - 1));
                            double after = Sample(channel, width, height, x + normalX * (offset + 1), y + normalY * (offset + 1));
                            strongest = Math.Max(strongest, Math.Abs(after - before));
                        }
                    int section = Math.Min(3, sample * 4 / samples);
                    sectionTotal[section]++;
                    if (strongest >= 4) { supported++; sectionSupported[section]++; }
                }
                minimum = Math.Min(minimum, supported / (double)samples);
                for (int section = 0; section < 4; section++)
                    minimum = Math.Min(minimum, sectionSupported[section] / (double)sectionTotal[section]);
                if (minimum < 0.55) return minimum;
            }
            return minimum;
        }

        internal static double ExteriorSupport(byte[][] channels, int width, int height, int scale,
                                               CropRegion candidate, double pixelsPerMm)
        {
            string ignored;
            return ExteriorSupport(channels, width, height, scale, candidate, pixelsPerMm, out ignored);
        }

        /// <summary>
        /// How much platen there is around each side of a candidate.
        ///
        /// What it is for is telling an object lying on the glass from a panel
        /// printed on a larger sheet: a printed panel has sheet on all four
        /// sides, an object has platen. <paramref name="detail"/> carries the
        /// per-side figures back for the diagnostic, through a parameter rather
        /// than a field because detection runs on more than one thread.
        /// </summary>
        internal static double ExteriorSupport(byte[][] channels, int width, int height, int scale,
                                               CropRegion candidate, double pixelsPerMm, out string detail)
        {
            List<double> perEdge = new List<double>(4);
            System.Text.StringBuilder sides = new System.Text.StringBuilder();
            for (int edge = 0; edge < 4; edge++)
            {
                PointF first = candidate.Box.Corners[edge], last = candidate.Box.Corners[(edge + 1) % 4];
                if (OnFrame(first, last, width, height, scale, 2)) { sides.Append("frame "); continue; }
                double horizontalDifference = (last.X - first.X) / scale, verticalDifference = (last.Y - first.Y) / scale;
                double length = Math.Sqrt(horizontalDifference * horizontalDifference + verticalDifference * verticalDifference), normalX = verticalDifference / length, normalY = -horizontalDifference / length;
                int quiet = 0;
                for (int sample = 0; sample < 32; sample++)
                {
                    double fraction = 0.08 + 0.84 * (sample + 0.5) / 32;
                    double x = first.X / scale + horizontalDifference * fraction + normalX * 2 * pixelsPerMm;
                    double y = first.Y / scale + verticalDifference * fraction + normalY * 2 * pixelsPerMm;
                    double texture = 0;
                    foreach (byte[] channel in channels)
                    {
                        double sum = 0, sumSquared = 0, sumSlope = 0;
                        for (int offset = -4; offset <= 4; offset++)
                        {
                            double value = Sample(channel, width, height, x + offset * horizontalDifference / length, y + offset * verticalDifference / length);
                            sum += value; sumSquared += value * value; sumSlope += offset * value;
                        }
                        // Remove a locally linear illumination ramp. The pale
                        // portrait's surrounding cast shadow changes by 12..18
                        // levels across this interval without being printed texture.
                        double residual = Math.Max(0, (sumSquared - sum * sum / 9 - sumSlope * sumSlope / 60) / 9);
                        texture = Math.Max(texture, Math.Sqrt(residual));
                    }
                    // The captured pale surround has residual noise up to 3.5
                    // levels. Four is also the physical edge fitter's floor.
                    if (texture < 4) quiet++;
                }
                perEdge.Add(quiet / 32.0);
                sides.Append((quiet / 32.0).ToString("0.00")).Append(' ');
            }

            detail = sides.ToString();
            if (perEdge.Count == 0) return 1;
            perEdge.Sort();

            // One busy side is forgiven, but only when all four were measured.
            //
            // A side is busy for reasons that have nothing to do with the item:
            // a neighbour a few millimetres away, the lid shadow falling one
            // way, a smudge on the glass. Taking the worst of the four threw
            // away a portrait that scored 1.00, 1.00, 1.00 on its other three -
            // on three separate beds - while the same portrait moved slightly
            // was accepted.
            //
            // The concession stops at candidates that already lean on the image
            // frame for a side. Measured on the two-card reference, the boundary
            // that tries to swallow BOTH cards scores "frame 0.06 1.00 1.00":
            // its second-worst side is also 1.00, so second-worst alone cannot
            // tell it from the portrait. Four measured sides can. A candidate
            // that has already given up one side to the frame does not get a
            // second concession on top of it.
            if (perEdge.Count < 4) return perEdge[0];
            return perEdge[1];
        }

        internal static bool HasContinuingBorders(byte[][] channels, int width, int height, int scale, CropRegion candidate, double pixelsPerMm)
        {
            PointF[] corners = candidate.Box.Corners;
            for (int side = 0; side < 4; side++)
            {
                if (Continues(channels, width, height, scale, corners[(side + 3) % 4], corners[side], pixelsPerMm) &&
                    Continues(channels, width, height, scale, corners[(side + 2) % 4], corners[(side + 1) % 4], pixelsPerMm)) return true;
            }
            return false;
        }

        static bool Continues(byte[][] channels, int width, int height, int scale, PointF start, PointF end, double pixelsPerMm)
        {
            double horizontal = end.X - start.X, vertical = end.Y - start.Y;
            double length = Math.Sqrt(horizontal * horizontal + vertical * vertical);
            horizontal /= length; vertical /= length;
            int positive = 0, negative = 0;
            // The paper's contour-extension penalty distinguishes a stock corner
            // from a line crossing a printed panel. A 1.5..4 mm interval lies in
            // the four-millimetre gap fixture rather than on its neighbouring card.
            for (int sample = 0; sample < 8; sample++)
            {
                double distance = (1.5 + 2.5 * sample / 8) * pixelsPerMm;
                double x = end.X / scale + horizontal * distance, y = end.Y / scale + vertical * distance;
                if (x < 3 || y < 3 || x >= width - 3 || y >= height - 3) return false;
                double strongest = 0;
                foreach (byte[] channel in channels)
                    for (int offset = -2; offset <= 2; offset++)
                    {
                        double sampleX = x - vertical * offset, sampleY = y + horizontal * offset;
                        double normal = Sample(channel, width, height, sampleX - vertical, sampleY + horizontal)
                            - Sample(channel, width, height, sampleX + vertical, sampleY - horizontal);
                        double tangent = Sample(channel, width, height, sampleX + horizontal, sampleY + vertical)
                            - Sample(channel, width, height, sampleX - horizontal, sampleY - vertical);
                        // Nearby print supplies contrast in every direction. An
                        // actual continuing border must retain its orientation
                        // and polarity, not merely contain dark pixels nearby.
                        if (Math.Abs(tangent) > Math.Abs(normal) * 0.414214) continue;
                        if (Math.Abs(normal) > Math.Abs(strongest)) strongest = normal;
                    }
                if (strongest >= 4) positive++;
                if (strongest <= -4) negative++;
            }
            return Math.Max(positive, negative) >= 7;
        }

        static double Sample(byte[] channel, int width, int height, double x, double y)
        {
            int column = Math.Max(0, Math.Min(width - 1, (int)Math.Round(x)));
            int row = Math.Max(0, Math.Min(height - 1, (int)Math.Round(y)));
            return channel[row * width + column];
        }
    }
}
