// =============================================================================
// NextScan Studio - physical edge measurements for isolated platen candidates
// Plan ref: MASTER_PLAN section 8.1; auto crop accuracy work order section 5.1.
// A threshold silhouette includes shadows and loses pale stock. Four independent
// gradient profiles measure the boundary instead; agreement along an edge keeps
// printed details and isolated dirt from steering its line.
// Limit: no method can identify invisible stock from its printed rectangle
// alone. This fitter needs a coherent boundary in the reduced colour channels;
// subpixel texture without a measurable boundary is not resolved here. Its
// confidence describes edge support, not proof of the item's semantic identity.
// Strongly unequal X/Y sampling is not covered by the physical-angle corpus:
// the seed and local rotation are in pixel space, although lengths use both DPIs.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace NextScan.Core
{
    internal static class PlatenEdgeFitter
    {
        sealed class EdgeLine
        {
            public double Slope, Intercept, Support, Strength;
            public EdgeLine Original;
            public bool Clipped, Blurred, ShadowRecovered, UnsupportedBeforeShadow, ThinShadowRecovered;
        }

        internal static bool Fit(byte[][] channels, int width, int height, int scale,
                                 RawImage page, CropRegion item, double minimumGradient = 4, double maximumLineAngle = 11)
        {
            if (Math.Abs(item.SkewDegrees) > 8)
                return FitOriented(channels, width, height, scale, page, item, minimumGradient);
            // The hull already supplies orientation. Two degrees of residual
            // freedom covers its shadow error in the 0..89 degree fixtures;
            // the former eleven-degree search could lock onto the inner print.
            if (item.Box != null)
                maximumLineAngle = Math.Min(maximumLineAngle, Math.Abs(item.SkewDegrees) + 2);
            double left = item.NormRect.Left * page.Width / scale;
            double right = item.NormRect.Right * page.Width / scale;
            double top = item.NormRect.Top * page.Height / scale;
            double bottom = item.NormRect.Bottom * page.Height / scale;
            double horizontalBand = 6 * page.XDpi / 25.4 / scale;
            double verticalBand = 6 * page.YDpi / 25.4 / scale;
            bool allowMissingShadow = left > 2 && top > 2 && right < width - 2 && bottom < height - 2;
            EdgeLine[] lines = new EdgeLine[4];
            // Profiles for separate edges read the same immutable channel planes
            // and own their accumulators. Concurrent fits reduce latency on beds
            // containing several difficult candidates without changing their votes.
            Parallel.For(0, lines.Length, delegate(int edge)
            {
                bool horizontal = edge == 1 || edge == 3;
                bool leading = edge < 2;
                double expected = edge == 0 ? left : edge == 1 ? top : edge == 2 ? right : bottom;
                lines[edge] = MeasureSupported(channels, width, height, horizontal, leading, expected,
                    horizontal ? left : top, horizontal ? right : bottom, horizontal ? verticalBand : horizontalBand,
                    minimumGradient, maximumLineAngle, allowMissingShadow);
            });
            for (int edge = 0; edge < lines.Length; edge++)
            {
                if (lines[edge] == null) return Failure(item, "unsupported edge " + edge + " (left, top, right, bottom)");
            }
            int recoveredEdges = 0;
            foreach (EdgeLine edgeLine in lines) if (edgeLine.ShadowRecovered) recoveredEdges++;
            if (recoveredEdges > 1)
            {
                for (int edge = 0; edge < lines.Length; edge++)
                {
                    if (!lines[edge].ShadowRecovered) continue;
                    if (lines[edge].Original == null) return Failure(item, "multiple missing edges need unsupported shadow extrapolation");
                    lines[edge] = lines[edge].Original;
                }
            }
            if (Math.Abs(lines[0].Slope - lines[2].Slope) > 0.035 || Math.Abs(lines[1].Slope - lines[3].Slope) > 0.035)
            {
                // A shadow alternative must not invalidate an otherwise coherent
                // four-edge fit. Retain the measured narrow-band line in that case.
                for (int edge = 0; edge < lines.Length; edge++)
                    if (lines[edge].Original != null) lines[edge] = lines[edge].Original;
            }
            foreach (EdgeLine edgeLine in lines)
                if (edgeLine.UnsupportedBeforeShadow && Array.Exists(lines, delegate(EdgeLine other) { return other.Clipped; }))
                    return Failure(item, "missing edge and clipped frame cannot validate an extended shadow search");
            PointF[] corners = new PointF[]
            {
                Intersect(lines[0], lines[1], scale),
                Intersect(lines[2], lines[1], scale),
                Intersect(lines[2], lines[3], scale),
                Intersect(lines[0], lines[3], scale)
            };
            double measuredWidth = (Distance(corners[0], corners[1], page.XDpi, page.YDpi)
                                  + Distance(corners[3], corners[2], page.XDpi, page.YDpi)) / 2;
            double measuredHeight = (Distance(corners[0], corners[3], page.XDpi, page.YDpi)
                                   + Distance(corners[1], corners[2], page.XDpi, page.YDpi)) / 2;
            // Opposite physical edges must agree. A line through a printed panel
            // can be strong, but it cannot validate an incompatible quadrilateral.
            if (Math.Abs(lines[0].Slope - lines[2].Slope) > 0.035 ||
                Math.Abs(lines[1].Slope - lines[3].Slope) > 0.035)
            {
                // An overhanging edge is the image frame, not a measured paper
                // edge. Its slope cannot validate or invalidate the opposite one.
                if (!(lines[0].Clipped || lines[1].Clipped || lines[2].Clipped || lines[3].Clipped))
                    return Failure(item, "opposite lines disagree: " + lines[0].Slope + "," + lines[1].Slope + "," + lines[2].Slope + "," + lines[3].Slope);
            }
            // Not merely positive: physically possible. Two opposite edges that
            // resolve within a fraction of a millimetre of each other describe
            // no document, and a "document" 0.1 mm wide reached the operator as
            // an extra region on a bed that did not hold one. The smallest thing
            // anyone puts on a platen - a postage stamp - is about 20 mm, so
            // 4 mm is clear of any real item and of any real measurement error.
            // Two unmeasured sides is not a measurement.
            //
            // An edge that is clipped by the capture carries no strength: the
            // fitter places it at the frame and says so. One of those is fine -
            // a passport running off the end of the scan still has three real
            // edges and a known visible extent. Two is a guess in both
            // directions, and on the 17:52 bed such a guess - strengths
            // 26.3, 16.2, 0.0, 0.0 - was accepted and swallowed two portraits
            // and a passport that had each been measured on all four sides.
            int unmeasured = 0;
            foreach (EdgeLine line in lines) if (line.Clipped || line.Strength <= 0) unmeasured++;
            if (unmeasured >= 2)
                return Failure(item, "only " + (4 - unmeasured) + " of four edges were measured");

            const double SmallestPlausibleMillimetres = 4.0;
            if (measuredWidth * 25.4 < SmallestPlausibleMillimetres ||
                measuredHeight * 25.4 < SmallestPlausibleMillimetres)
                return Failure(item, "fitted extent of " + (measuredWidth * 25.4).ToString("0.0")
                    + " x " + (measuredHeight * 25.4).ToString("0.0") + " mm is not a document");
            float minimumX = page.Width, minimumY = page.Height, maximumX = 0, maximumY = 0;
            foreach (PointF corner in corners)
            {
                if (float.IsNaN(corner.X) || float.IsNaN(corner.Y)) return Failure(item, "non-finite intersection");
                minimumX = Math.Min(minimumX, corner.X);
                maximumX = Math.Max(maximumX, corner.X);
                minimumY = Math.Min(minimumY, corner.Y);
                maximumY = Math.Max(maximumY, corner.Y);
            }
            minimumX = Math.Max(0, minimumX);
            minimumY = Math.Max(0, minimumY);
            maximumX = Math.Min(page.Width, maximumX);
            maximumY = Math.Min(page.Height, maximumY);
            // An edge that was never measured must not vote on the angle.
            //
            // A passport running past the end of the scan contributes a line
            // along the frame: dead flat, strength zero, and not a property of
            // the document at all. Averaging it with the opposite real edge
            // halved the measured tilt - three genuine edges at -2.3 degrees
            // were delivered as -1.2 - and the page arrived visibly crooked.
            // Averaging survives only for the edges that were actually seen.
            double slopeTotal = 0;
            int slopeCount = 0;
            foreach (int index in new int[] { 1, 3 })
            {
                if (lines[index].Clipped || lines[index].Strength <= 0) continue;
                slopeTotal += lines[index].Slope;
                slopeCount++;
            }
            if (slopeCount == 0)
            {
                // Nothing on this axis was measured. The perpendicular pair
                // describes the same rectangle, so it answers instead.
                foreach (int index in new int[] { 0, 2 })
                {
                    if (lines[index].Clipped || lines[index].Strength <= 0) continue;
                    slopeTotal += -lines[index].Slope;
                    slopeCount++;
                }
            }
            if (slopeCount == 0) { slopeTotal = lines[1].Slope + lines[3].Slope; slopeCount = 2; }

            float angle = (float)(Math.Atan(slopeTotal / slopeCount) * 180 / Math.PI);
            double support = 1;
            bool clipped = false, blurred = false, shadowRecovered = false, thinShadowRecovered = false;
            foreach (EdgeLine line in lines)
            {
                support = Math.Min(support, line.Support);
                clipped |= line.Clipped;
                blurred |= line.Blurred;
                shadowRecovered |= line.ShadowRecovered;
                thinShadowRecovered |= line.ThinShadowRecovered;
            }
            item.NormRect = new RectangleF(minimumX / page.Width, minimumY / page.Height,
                (maximumX - minimumX) / page.Width, (maximumY - minimumY) / page.Height);
            item.WidthInches = measuredWidth;
            item.HeightInches = measuredHeight;
            item.SkewDegrees = angle;
            item.Score = (float)support;
            item.Confidence = clipped || blurred || shadowRecovered ? CropConfidence.Good : (support >= 0.85 ? CropConfidence.High : CropConfidence.Good);
            item.Reason = "physical gradient lines; minimum edge support " + support.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + "; line strengths " + string.Join(",", Array.ConvertAll(lines, delegate(EdgeLine line) { return line.Strength.ToString("0.0") + (line.ShadowRecovered ? "S" : ""); }))
                + (clipped ? "; reaches image frame: only visible extent is measurable" : "")
                + (blurred ? "; broad gradient support: review softened edge" : "")
                + (thinShadowRecovered ? "; thin shadow valley resolved to the inward stock transition" : "")
                + (shadowRecovered ? "; sharp stock edge separated from a broad outer shadow" : "");
            item.Box = new RotatedBox
            {
                IsValid = true, Corners = corners, Angle = angle, RawAngle = angle,
                Width = (float)((PixelDistance(corners[0], corners[1]) + PixelDistance(corners[3], corners[2])) / 2),
                Height = (float)((PixelDistance(corners[0], corners[3]) + PixelDistance(corners[1], corners[2])) / 2),
                Center = new PointF((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2),
                AABB = Rectangle.FromLTRB((int)Math.Floor(minimumX), (int)Math.Floor(minimumY),
                    (int)Math.Ceiling(maximumX), (int)Math.Ceiling(maximumY)), Score = (float)support
            };
            return true;
        }

        static bool Failure(CropRegion item, string reason)
        {
            item.Reason += "; edge fit: " + reason;
            return false;
        }

        static bool FitOriented(byte[][] channels, int width, int height, int scale,
                                RawImage page, CropRegion item, double minimumGradient)
        {
            RotatedBox seed = item.Box;
            double angle = seed.Angle * Math.PI / 180;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double centreX = seed.Center.X / scale, centreY = seed.Center.Y / scale;
            double stockWidth = seed.Width / scale, stockHeight = seed.Height / scale;
            // Eight millimetres encloses the existing six-millimetre profile
            // band without resampling the whole bed for every angled item.
            int padding = (int)Math.Ceiling(8 * Math.Max(page.XDpi, page.YDpi) / 25.4 / scale);
            int localWidth = (int)Math.Ceiling(stockWidth) + 2 * padding;
            int localHeight = (int)Math.Ceiling(stockHeight) + 2 * padding;
            byte[][] localChannels = new byte[channels.Length][];
            for (int channel = 0; channel < channels.Length; channel++)
                localChannels[channel] = new byte[localWidth * localHeight];
            for (int y = 0; y < localHeight; y++)
            {
                for (int x = 0; x < localWidth; x++)
                {
                    double across = x + 0.5 - localWidth / 2.0;
                    double along = y + 0.5 - localHeight / 2.0;
                    double sourceX = centreX + across * cosine - along * sine - 0.5;
                    double sourceY = centreY + across * sine + along * cosine - 0.5;
                    for (int channel = 0; channel < channels.Length; channel++)
                        localChannels[channel][y * localWidth + x] = Sample(channels[channel], width, height, sourceX, sourceY);
                }
            }
            RawImage localPage = new RawImage
            {
                Width = localWidth, Height = localHeight,
                XDpi = page.XDpi / scale, YDpi = page.YDpi / scale
            };
            CropRegion measured = new CropRegion
            {
                NormRect = new RectangleF((float)((localWidth - stockWidth) / 2 / localWidth),
                    (float)((localHeight - stockHeight) / 2 / localHeight),
                    (float)(stockWidth / localWidth), (float)(stockHeight / localHeight))
            };
            if (!Fit(localChannels, localWidth, localHeight, 1, localPage, measured, minimumGradient, 2)) return Failure(item, measured.Reason);
            PointF[] corners = measured.Box.Corners;
            double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
            for (int index = 0; index < corners.Length; index++)
            {
                double across = corners[index].X - localWidth / 2.0;
                double along = corners[index].Y - localHeight / 2.0;
                corners[index] = new PointF((float)((centreX + across * cosine - along * sine) * scale),
                    (float)((centreY + across * sine + along * cosine) * scale));
                // A rotated edge outside the acquired raster is not recoverable
                // by padding. Do not describe replicated border pixels as stock.
                if (corners[index].X < 0 || corners[index].Y < 0 ||
                    corners[index].X > page.Width || corners[index].Y > page.Height) return Failure(item, "rotated fitted corner is outside the acquired raster");
                left = Math.Min(left, corners[index].X); right = Math.Max(right, corners[index].X);
                top = Math.Min(top, corners[index].Y); bottom = Math.Max(bottom, corners[index].Y);
            }
            item.Box = measured.Box;
            item.Box.Center = seed.Center;
            item.Box.Width *= scale;
            item.Box.Height *= scale;
            item.Box.Angle += seed.Angle;
            item.Box.RawAngle = item.Box.Angle;
            item.Box.AABB = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top), (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
            item.NormRect = new RectangleF((float)(left / page.Width), (float)(top / page.Height),
                (float)((right - left) / page.Width), (float)((bottom - top) / page.Height));
            item.WidthInches = (Distance(corners[0], corners[1], page.XDpi, page.YDpi)
                + Distance(corners[3], corners[2], page.XDpi, page.YDpi)) / 2;
            item.HeightInches = (Distance(corners[0], corners[3], page.XDpi, page.YDpi)
                + Distance(corners[1], corners[2], page.XDpi, page.YDpi)) / 2;
            item.SkewDegrees = item.Box.Angle;
            item.Score = measured.Score;
            item.Confidence = measured.Confidence;
            item.Reason = "oriented " + measured.Reason;
            return true;
        }

        static byte Sample(byte[] channel, int width, int height, double x, double y)
        {
            x = Math.Max(0, Math.Min(width - 1, x));
            y = Math.Max(0, Math.Min(height - 1, y));
            int left = (int)x, top = (int)y;
            int right = Math.Min(width - 1, left + 1), bottom = Math.Min(height - 1, top + 1);
            double horizontal = x - left, vertical = y - top;
            return (byte)Math.Round((channel[top * width + left] * (1 - horizontal) + channel[top * width + right] * horizontal) * (1 - vertical)
                + (channel[bottom * width + left] * (1 - horizontal) + channel[bottom * width + right] * horizontal) * vertical);
        }

        static EdgeLine MeasureSupported(byte[][] channels, int width, int height, bool horizontal, bool leading,
                                         double expected, double start, double end, double band, double minimumGradient, double maximumLineAngle, bool allowMissingShadow)
        {
            EdgeLine line = Measure(channels, width, height, horizontal, leading, expected, start, end, band, minimumGradient, maximumLineAngle, 1);
            if (line == null)
                line = Measure(channels, width, height, horizontal, leading, expected, start, end, band, minimumGradient, maximumLineAngle, 5);
            if (line == null && !allowMissingShadow) return null;
            if (line != null && !line.Clipped && line.Strength < 8 && ResolveThinShadow(channels, width, height, horizontal, leading, line, start, end, band)) return line;
            if (line != null && (line.Clipped || !line.Blurred || line.Strength / 5 >= 8)) return line;
            if (line != null && ResolveThinShadow(channels, width, height, horizontal, leading, line, start, end, band)) return line;
            // On the LiDE passport a weak fitted recovery lay beyond the sharp
            // cover edge. A wider search may replace a broad, weak line only
            // when a long outward shadow independently supports the sharp step.
            // Narrow faint stock edges stay intact: the white-border fixture
            // would otherwise be replaced by its much darker printed panel.
            EdgeLine shadowEdge = Measure(channels, width, height, horizontal, leading, expected, start, end,
                band * 4, Math.Max(12, minimumGradient * 3), maximumLineAngle, 1, true);
            if (shadowEdge != null && !shadowEdge.Clipped &&
                HasOuterShadow(channels, width, height, horizontal, leading, shadowEdge, start, end, band))
            {
                shadowEdge.ShadowRecovered = true;
                shadowEdge.UnsupportedBeforeShadow = line == null;
                shadowEdge.Original = line;
                return shadowEdge;
            }
            return line;
        }

        static bool ResolveThinShadow(byte[][] channels, int width, int height, bool horizontal, bool leading,
            EdgeLine line, double start, double end, double band)
        {
            // A five-level, 0.45 mm shadow around the rotated white-stock fixture
            // survives a broad derivative, but its outer transition is not paper.
            // Average aligned profiles before locating the inward half-height
            // transition. Equal bright plateaus and a narrow dark valley are
            // required; an ordinary dark document or broad curl cannot satisfy it.
            int radius = Math.Max(4, (int)Math.Ceiling(band / 4));
            double[] profile = new double[2 * radius + 1];
            int samples = 0, direction = leading ? 1 : -1;
            for (int sample = 0; sample < 40; sample++)
            {
                int along = (int)Math.Round(start + (end - start) * (0.2 + 0.6 * sample / 39));
                double across = line.Intercept + line.Slope * along;
                if (along < 0 || along >= (horizontal ? width : height) ||
                    across - radius < 0 || across + radius >= (horizontal ? height : width)) continue;
                for (int offset = -radius; offset <= radius; offset++)
                {
                    double value = 0;
                    foreach (byte[] channel in channels)
                        value += Sample(channel, width, height, horizontal ? along : across + direction * offset,
                            horizontal ? across + direction * offset : along);
                    profile[offset + radius] += value / channels.Length;
                }
                samples++;
            }
            if (samples < 24) return false;
            for (int index = 0; index < profile.Length; index++) profile[index] /= samples;
            double outside = (profile[0] + profile[1]) / 2;
            double inside = (profile[profile.Length - 1] + profile[profile.Length - 2]) / 2;
            if (Math.Abs(inside - outside) > 3) return false;
            int valley = 2;
            for (int index = 3; index < profile.Length - 2; index++)
                if (profile[index] < profile[valley]) valley = index;
            if (Math.Min(inside, outside) - profile[valley] < 2.5) return false;
            double halfHeight = (inside + profile[valley]) / 2;
            int crossing = valley + 1;
            while (crossing < profile.Length - 2 && profile[crossing] < halfHeight) crossing++;
            if (profile[crossing] < halfHeight || profile[crossing] <= profile[crossing - 1]) return false;
            double position = crossing - 1 + (halfHeight - profile[crossing - 1]) /
                (profile[crossing] - profile[crossing - 1]) - radius;
            line.Intercept += direction * position + 0.5;
            line.ThinShadowRecovered = true;
            return true;
        }

        static bool HasOuterShadow(byte[][] channels, int width, int height, bool horizontal, bool leading,
                                   EdgeLine line, double start, double end, double band)
        {
            int direction = leading ? -1 : 1;
            int nearOffset = 2, middleOffset = Math.Max(5, (int)Math.Round(band * 0.3));
            int farOffset = Math.Max(middleOffset + 4, (int)Math.Round(band));
            int supported = 0, sampled = 0;
            for (int sample = 0; sample < 40; sample++)
            {
                int along = (int)Math.Round(start + (end - start) * (0.2 + 0.6 * sample / 39));
                int edge = (int)Math.Round(line.Intercept + line.Slope * along);
                int inside = edge - direction * nearOffset, near = edge + direction * nearOffset;
                int middle = edge + direction * middleOffset, far = edge + direction * farOffset;
                int extent = horizontal ? height : width;
                if (Math.Min(inside, far) < 0 || Math.Max(inside, far) >= extent ||
                    along < 0 || along >= (horizontal ? width : height)) continue;
                double insideValue = ProfileValue(channels, width, horizontal, along, inside);
                double nearValue = ProfileValue(channels, width, horizontal, along, near);
                double middleValue = ProfileValue(channels, width, horizontal, along, middle);
                for (int offset = nearOffset; offset <= middleOffset; offset++)
                    nearValue = Math.Min(nearValue, ProfileValue(channels, width, horizontal, along, edge + direction * offset));
                double farValue = ProfileValue(channels, width, horizontal, along, far);
                sampled++;
                // Twelve/eighteen levels separate the captured deep passport
                // shadow from the +/-3 sensor-noise and faint-border fixtures.
                // The recovery must persist over millimetres, not a dark rule.
                if (insideValue >= nearValue + 12 && farValue >= nearValue + 18 &&
                    middleValue <= farValue + 3)
                    supported++;
            }
            return sampled >= 24 && supported >= sampled * 0.65;
        }

        static double ProfileValue(byte[][] channels, int width, bool horizontal, int along, int across)
        {
            int pixel = horizontal ? across * width + along : along * width + across;
            double total = 0;
            foreach (byte[] channel in channels) total += channel[pixel];
            return total / channels.Length;
        }

        static EdgeLine Measure(byte[][] channels, int width, int height, bool horizontal, bool leading,
                                double expected, double start, double end, double band, double minimumGradient, double maximumLineAngle, int baseline, bool requireShadow = false)
        {
            int extent = horizontal ? height : width;
            // Closing previously erased three rows from both real cards where
            // they meet row zero. The frame bounds visible data, never black ink.
            int frameInset = Math.Max(2, Math.Min(width, height) / 260);
            if (expected <= frameInset + 0.01) return new EdgeLine { Intercept = 0, Support = 1, Clipped = true };
            if (expected >= extent - frameInset - 0.01) return new EdgeLine { Intercept = extent, Support = 1, Clipped = true };
            double centre = (start + end) / 2;
            // Ten degrees moves an ID card's edge midpoint about 7.5 mm from
            // its bounding-box extreme, beyond the ordinary 6 mm shadow band.
            double rotationExcursion = (end - start) * Math.Tan(maximumLineAngle * Math.PI / 180) / 2;
            band = Math.Max(band, rotationExcursion);
            int first = (int)Math.Ceiling(start + (end - start) * 0.18);
            int last = (int)Math.Floor(end - (end - start) * 0.18);
            int step = Math.Max(1, (last - first) / 100);
            int low = Math.Max((baseline + 1) / 2, (int)Math.Floor(expected - band - rotationExcursion));
            int high = Math.Min(extent - 1 - baseline / 2, (int)Math.Ceiling(expected + band + rotationExcursion));
            if (last <= first || high <= low) return null;
            List<int> positions = new List<int>();
            List<double[]> profiles = new List<double[]>();
            for (int along = first; along <= last; along += step)
            {
                double[] profile = new double[high - low + 1];
                for (int across = low; across <= high; across++)
                {
                    // Three neighbouring profiles suppress single-pixel noise
                    // without moving a boundary across the pixel it separates.
                    double strength = 0;
                    foreach (byte[] channel in channels)
                    {
                        double difference = 0;
                        for (int neighbour = -1; neighbour <= 1; neighbour++)
                        {
                            int tangent = along + neighbour;
                            if (tangent < 0 || tangent >= (horizontal ? width : height)) continue;
                            int beforePosition = across - (baseline + 1) / 2, afterPosition = across + baseline / 2;
                            int before = horizontal ? beforePosition * width + tangent : tangent * width + beforePosition;
                            int after = horizontal ? afterPosition * width + tangent : tangent * width + afterPosition;
                            difference += channel[after] - channel[before];
                        }
                        strength = Math.Max(strength, Math.Abs(difference / 3));
                    }
                    profile[across - low] = strength;
                }
                positions.Add(along);
                profiles.Add(profile);
            }
            double bestScore = double.NegativeInfinity, bestSlope = 0, bestCentre = expected;
            // A quarter degree costs under one working pixel over an ID card's
            // half edge. Least squares below removes this search quantisation.
            int angleSteps = (int)Math.Round(maximumLineAngle / 0.25);
            for (int angleStep = -angleSteps; angleStep <= angleSteps; angleStep++)
            {
                double slope = Math.Tan(angleStep * 0.25 * Math.PI / 180);
                for (double intercept = expected - band; intercept <= expected + band; intercept += 0.5)
                {
                    double score = 0;
                    int supported = 0;
                    for (int sample = 0; sample < profiles.Count; sample++)
                    {
                        int location = (int)Math.Round(intercept + slope * (positions[sample] - centre)) - low;
                        if (location < 0 || location >= profiles[sample].Length) continue;
                        double strength = profiles[sample][location];
                        // Cap the vote: a dark printed letter must not outweigh
                        // several centimetres of a faint continuous stock edge.
                        score += Math.Min(40, strength);
                        if (strength >= minimumGradient) supported++;
                    }
                    if (supported < profiles.Count * 0.6) continue;
                    // Compare the line's extreme with the component extreme,
                    // not its midpoint. Otherwise rotation widens the search
                    // into the printed picture and the neighbouring card.
                    double extreme = intercept + (leading ? -1 : 1) * Math.Abs(slope) * (end - start) / 2;
                    // Two working pixels allow reduction and shadow thickness.
                    // The remaining distance penalty separates the 4 mm neighbour
                    // and 8 mm printed-border fixtures without preferring a tilted
                    // line solely because its extreme is closer to the seed.
                    score = score / profiles.Count / 40 + 3.0 * supported / profiles.Count
                        - Math.Max(0, Math.Abs(extreme - expected) - 2) * 0.1;
                    if (score > bestScore)
                    {
                        if (requireShadow && !HasOuterShadow(channels, width, height, horizontal, leading,
                            new EdgeLine { Slope = slope, Intercept = intercept - slope * centre }, start, end, band / 4)) continue;
                        bestScore = score;
                        bestSlope = slope;
                        bestCentre = intercept;
                    }
                }
            }
            if (double.IsNegativeInfinity(bestScore)) return null;
            List<double> alongPoints = new List<double>(), acrossPoints = new List<double>();
            double totalStrength = 0;
            int[] sectionSamples = new int[3], sectionSupport = new int[3];
            for (int sample = 0; sample < profiles.Count; sample++)
            {
                int section = Math.Min(2, sample * 3 / profiles.Count);
                sectionSamples[section]++;
                int predicted = (int)Math.Round(bestCentre + bestSlope * (positions[sample] - centre));
                int best = predicted;
                double strength = 0;
                for (int location = Math.Max(low, predicted - 2); location <= Math.Min(high, predicted + 2); location++)
                {
                    double candidate = profiles[sample][location - low];
                    if (candidate > strength) { strength = candidate; best = location; }
                }
                if (strength < minimumGradient) continue;
                sectionSupport[section]++;
                totalStrength += strength;
                alongPoints.Add(positions[sample]);
                acrossPoints.Add(best);
            }
            if (alongPoints.Count < profiles.Count * 0.6) return null;
            // A merged bed can borrow its top from a portrait and its side from
            // a passport. Support confined to one portion is not a full edge.
            for (int section = 0; section < sectionSamples.Length; section++)
                if (sectionSupport[section] < sectionSamples[section] * 0.5) return null;
            double sumAlong = 0, sumAcross = 0, sumSquare = 0, sumProduct = 0;
            for (int i = 0; i < alongPoints.Count; i++)
            {
                double along = alongPoints[i] - centre;
                sumAlong += along;
                sumAcross += acrossPoints[i];
                sumSquare += along * along;
                sumProduct += along * acrossPoints[i];
            }
            double count = alongPoints.Count;
            double fittedSlope = (sumProduct - sumAlong * sumAcross / count) / (sumSquare - sumAlong * sumAlong / count);
            return new EdgeLine
            {
                Slope = fittedSlope,
                Intercept = (sumAcross - fittedSlope * sumAlong) / count - fittedSlope * centre,
                Support = count / profiles.Count,
                Blurred = baseline > 1,
                Strength = totalStrength / count
            };
        }

        static PointF Intersect(EdgeLine vertical, EdgeLine horizontal, int scale)
        {
            double horizontalPosition = (vertical.Intercept + vertical.Slope * horizontal.Intercept)
                / (1 - vertical.Slope * horizontal.Slope);
            return new PointF((float)(horizontalPosition * scale),
                (float)((horizontal.Intercept + horizontal.Slope * horizontalPosition) * scale));
        }

        static double Distance(PointF first, PointF second, double horizontalDpi, double verticalDpi)
        {
            double horizontal = (second.X - first.X) / horizontalDpi;
            double vertical = (second.Y - first.Y) / verticalDpi;
            return Math.Sqrt(horizontal * horizontal + vertical * vertical);
        }

        static double PixelDistance(PointF first, PointF second)
        {
            return Distance(first, second, 1, 1);
        }
    }
}


