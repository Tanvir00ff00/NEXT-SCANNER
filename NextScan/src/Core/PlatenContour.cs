// =============================================================================
// NextScan Studio - measured free-form stock contours
// Plan ref: universal platen work order; irregular-document follow-up.
// A convex hull erases missing corners and concavities. Directed pixel-boundary
// tracing preserves them, then a distance-bounded polygon retains the measured
// outline without imposing four corners or a catalogue of document sizes.
// Limits: the mask is evidence, not semantic recognition. Invisible boundaries,
// enclosed holes indistinguishable from print, and occluded stock remain uncertain.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    internal static class PlatenContour
    {
        sealed class Segment
        {
            internal int Start, End, Direction;
            internal bool Used;
        }

        internal static bool Fit(bool[] mask, byte[][] channels, int width, int height, int scale,
                                 RawImage page, List<PointF> boundary, CropRegion item, double minimumGradient)
        {
            if (boundary.Count < 3) return false;
            // Raster shadows in the 5-degree A4 and ID fixtures can create
            // hundreds of apparent corners despite a nearly rectangular mask.
            // Preserve line fitting for masks with less than 4% missing stock;
            // small notches on otherwise rectangular items remain a limitation.
            if (item.Score >= 0.96f) return false;
            // Closing is useful for finding connected stock, but rounds re-entrant
            // corners. Trace the unclosed evidence within this candidate instead.
            int minimumColumn = width - 1, minimumRow = height - 1, maximumColumn = 0, maximumRow = 0;
            foreach (PointF point in boundary)
            {
                minimumColumn = Math.Min(minimumColumn, (int)point.X); maximumColumn = Math.Max(maximumColumn, (int)point.X);
                minimumRow = Math.Min(minimumRow, (int)point.Y); maximumRow = Math.Max(maximumRow, (int)point.Y);
            }
            List<PointF> measuredBoundary = new List<PointF>();
            for (int row = minimumRow; row <= maximumRow; row++)
                for (int column = minimumColumn; column <= maximumColumn; column++)
                {
                    int pixel = row * width + column;
                    if (!mask[pixel]) continue;
                    if (column == 0 || row == 0 || column == width - 1 || row == height - 1 ||
                        !mask[pixel - 1] || !mask[pixel + 1] || !mask[pixel - width] || !mask[pixel + width])
                        measuredBoundary.Add(new PointF(column + 0.5f, row + 0.5f));
                }
            PointF[] outline = Trace(mask, width, height, measuredBoundary);
            if (outline == null) return false;
            double pixelsPerMm = Math.Min(page.XDpi, page.YDpi) / 25.4 / scale;
            double angle = item.Box.Angle * Math.PI / 180;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double centreX = item.Box.Center.X / scale, centreY = item.Box.Center.Y / scale;
            double halfWidth = item.Box.Width / scale / 2, halfHeight = item.Box.Height / scale / 2;
            double deviation = 0;
            foreach (PointF point in outline)
            {
                // Frame pixels bound the acquisition, not an observed stock edge.
                if (point.X <= 1 || point.Y <= 1 || point.X >= width - 1 || point.Y >= height - 1) return false;
                double x = point.X - centreX, y = point.Y - centreY;
                deviation = Math.Max(deviation, Math.Min(halfWidth - Math.Abs(x * cosine + y * sine),
                    halfHeight - Math.Abs(-x * sine + y * cosine)));
            }
            // Sub-millimetre raster stair steps do not turn a rectangular card
            // into an irregular document. A measured notch deeper than 1 mm does.
            if (deviation < pixelsPerMm) return false;
            outline = Simplify(outline, Math.Max(0.35, pixelsPerMm * 0.2));
            List<PointF> samples = new List<PointF>();
            for (int edge = 0; edge < outline.Length; edge++)
            {
                PointF first = outline[edge], second = outline[(edge + 1) % outline.Length];
                double length = Distance(first, second);
                int steps = Math.Max(1, (int)Math.Ceiling(length / Math.Max(1, pixelsPerMm)));
                for (int step = 0; step < steps; step++)
                    samples.Add(new PointF(first.X + (second.X - first.X) * step / steps,
                        first.Y + (second.Y - first.Y) * step / steps));
            }
            List<PointF> fitted = new List<PointF>();
            int supported = 0;
            double band = Math.Max(2, pixelsPerMm);
            for (int index = 0; index < samples.Count; index++)
            {
                PointF previous = samples[(index + samples.Count - 1) % samples.Count];
                PointF next = samples[(index + 1) % samples.Count], point = samples[index];
                double tangentX = next.X - previous.X, tangentY = next.Y - previous.Y;
                double length = Math.Sqrt(tangentX * tangentX + tangentY * tangentY);
                if (length < 0.01) return false;
                double normalX = tangentY / length, normalY = -tangentX / length;
                double bestScore = double.NegativeInfinity, bestOffset = 0, bestStrength = 0;
                for (double offset = -band; offset <= band; offset += 0.5)
                {
                    double strength = 0;
                    foreach (byte[] channel in channels)
                    {
                        double before = Sample(channel, width, height, point.X + normalX * (offset - 0.5), point.Y + normalY * (offset - 0.5));
                        double after = Sample(channel, width, height, point.X + normalX * (offset + 0.5), point.Y + normalY * (offset + 0.5));
                        strength = Math.Max(strength, Math.Abs(after - before));
                    }
                    double score = Math.Min(40, strength) - 1.5 * Math.Abs(offset);
                    if (score > bestScore) { bestScore = score; bestOffset = offset; bestStrength = strength; }
                }
                if (bestStrength >= minimumGradient) supported++;
                else
                {
                    if (index + 1 - supported > samples.Count * 0.02)
                    {
                        item.Reason += "; contour has more than 2% unsupported perimeter samples";
                        return false;
                    }
                    continue;
                }
                fitted.Add(new PointF((float)(point.X + normalX * bestOffset), (float)(point.Y + normalY * bestOffset)));
            }
            // An arbitrary contour has fewer geometric constraints than four
            // lines. Demand support around almost its entire perimeter instead.
            // The 24-tip fixture loses 13 of 885 samples at sharp corners; a
            // 2% allowance retains those while refusing the real shadow at 4%.
            if (supported < samples.Count * 0.98) { item.Reason += "; contour support " + supported + "/" + samples.Count; return false; }
            PointF[] refined = Simplify(fitted.ToArray(), Math.Max(0.35, pixelsPerMm * 0.2));
            refined = RemoveSmallLoops(refined, pixelsPerMm * 6);
            if (refined.Length < 3 || SelfIntersects(refined)) { item.Reason += "; contour intersects"; return false; }
            double perimeter = 0;
            for (int vertex = 0; vertex < refined.Length; vertex++)
                perimeter += Distance(refined[vertex], refined[(vertex + 1) % refined.Length]);
            // One-millimetre profile sampling cannot establish independent
            // direction changes more often than every two millimetres. The
            // captured merged-card shadow violated this density by several times.
            // Finer serrations require a higher-resolution contour measurement.
            if (refined.Length > perimeter / pixelsPerMm / 2)
            {
                item.Reason += "; contour detail exceeds physical sampling support";
                return false;
            }
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            double minimumX = double.MaxValue, minimumY = double.MaxValue, maximumX = 0, maximumY = 0;
            for (int index = 0; index < refined.Length; index++)
            {
                refined[index] = new PointF(refined[index].X * scale, refined[index].Y * scale);
                PointF point = refined[index];
                double x = point.X * cosine + point.Y * sine, y = -point.X * sine + point.Y * cosine;
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                minimumX = Math.Min(minimumX, point.X); maximumX = Math.Max(maximumX, point.X);
                minimumY = Math.Min(minimumY, point.Y); maximumY = Math.Max(maximumY, point.Y);
            }
            item.Outline = refined;
            item.Box.Corners = new PointF[] { Rotate(left, top, cosine, sine), Rotate(right, top, cosine, sine),
                Rotate(right, bottom, cosine, sine), Rotate(left, bottom, cosine, sine) };
            item.Box.Width = (float)(right - left); item.Box.Height = (float)(bottom - top);
            item.Box.Center = Rotate((left + right) / 2, (top + bottom) / 2, cosine, sine);
            item.Box.AABB = Rectangle.FromLTRB((int)Math.Floor(minimumX), (int)Math.Floor(minimumY), (int)Math.Ceiling(maximumX), (int)Math.Ceiling(maximumY));
            item.NormRect = new RectangleF((float)(minimumX / page.Width), (float)(minimumY / page.Height),
                (float)((maximumX - minimumX) / page.Width), (float)((maximumY - minimumY) / page.Height));
            item.WidthInches = PhysicalLength(item.Box.Corners[0], item.Box.Corners[1], page);
            item.HeightInches = PhysicalLength(item.Box.Corners[0], item.Box.Corners[3], page);
            item.Score = (float)supported / samples.Count;
            item.Confidence = CropConfidence.Good;
            item.Reason = "measured irregular contour, " + refined.Length + " points; perimeter support "
                + item.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "; review outline";
            return true;
        }

        static PointF[] Trace(bool[] mask, int width, int height, List<PointF> boundary)
        {
            int vertexWidth = width + 1;
            List<Segment> segments = new List<Segment>();
            Dictionary<int, List<int>> outgoing = new Dictionary<int, List<int>>();
            foreach (PointF point in boundary)
            {
                int x = (int)point.X, y = (int)point.Y, pixel = y * width + x;
                int vertex = y * vertexWidth + x;
                if (y == 0 || !mask[pixel - width]) Add(segments, outgoing, vertex, vertex + 1, 0);
                if (x == width - 1 || !mask[pixel + 1]) Add(segments, outgoing, vertex + 1, vertex + vertexWidth + 1, 1);
                if (y == height - 1 || !mask[pixel + width]) Add(segments, outgoing, vertex + vertexWidth + 1, vertex + vertexWidth, 2);
                if (x == 0 || !mask[pixel - 1]) Add(segments, outgoing, vertex + vertexWidth, vertex, 3);
            }
            PointF[] largest = null;
            double largestArea = 0;
            for (int start = 0; start < segments.Count; start++)
            {
                if (segments[start].Used) continue;
                List<PointF> loop = new List<PointF>();
                int current = start;
                while (!segments[current].Used)
                {
                    Segment segment = segments[current]; segment.Used = true;
                    loop.Add(new PointF(segment.Start % vertexWidth, segment.Start / vertexWidth));
                    if (segment.End == segments[start].Start) break;
                    List<int> choices;
                    if (!outgoing.TryGetValue(segment.End, out choices)) return null;
                    int chosen = -1, bestTurn = 5;
                    foreach (int choice in choices)
                    {
                        if (segments[choice].Used) continue;
                        int turn = (segments[choice].Direction - segment.Direction + 4) % 4;
                        int preference = turn == 1 ? 0 : turn == 0 ? 1 : turn == 3 ? 2 : 3;
                        if (preference < bestTurn) { bestTurn = preference; chosen = choice; }
                    }
                    if (chosen < 0) return null;
                    current = chosen;
                }
                double area = Area(loop.ToArray());
                if (area > largestArea) { largestArea = area; largest = loop.ToArray(); }
            }
            return largest;
        }

        static void Add(List<Segment> segments, Dictionary<int, List<int>> outgoing, int start, int end, int direction)
        {
            List<int> indices;
            if (!outgoing.TryGetValue(start, out indices)) { indices = new List<int>(); outgoing.Add(start, indices); }
            indices.Add(segments.Count);
            segments.Add(new Segment { Start = start, End = end, Direction = direction });
        }

        internal static PointF[] Simplify(PointF[] points, double tolerance)
        {
            if (points.Length < 4) return points;
            int split = 1;
            for (int index = 2; index < points.Length; index++)
                if (Distance(points[0], points[index]) > Distance(points[0], points[split])) split = index;
            PointF[] closed = new PointF[points.Length + 1];
            Array.Copy(points, closed, points.Length); closed[points.Length] = points[0];
            bool[] keep = new bool[closed.Length]; keep[0] = keep[split] = keep[points.Length] = true;
            Stack<int[]> intervals = new Stack<int[]>();
            intervals.Push(new int[] { 0, split }); intervals.Push(new int[] { split, points.Length });
            while (intervals.Count > 0)
            {
                int[] interval = intervals.Pop(); int first = interval[0], last = interval[1], farthest = -1;
                double maximum = tolerance;
                for (int index = first + 1; index < last; index++)
                {
                    double distance = SegmentDistance(closed[index], closed[first], closed[last]);
                    if (distance > maximum) { maximum = distance; farthest = index; }
                }
                if (farthest < 0) continue;
                keep[farthest] = true;
                intervals.Push(new int[] { first, farthest }); intervals.Push(new int[] { farthest, last });
            }
            List<PointF> result = new List<PointF>();
            for (int index = 0; index < points.Length; index++) if (keep[index]) result.Add(points[index]);
            return result.ToArray();
        }

        internal static bool Contains(PointF[] outline, double x, double y)
        {
            bool inside = false;
            for (int current = 0, previous = outline.Length - 1; current < outline.Length; previous = current++)
            {
                PointF first = outline[previous], second = outline[current];
                if ((first.Y > y) != (second.Y > y) &&
                    x < first.X + (y - first.Y) * (second.X - first.X) / (second.Y - first.Y)) inside = !inside;
            }
            return inside;
        }

        internal static double Area(PointF[] points)
        {
            double area = 0;
            for (int index = 0; index < points.Length; index++)
            {
                PointF first = points[index], second = points[(index + 1) % points.Length];
                area += (double)first.X * second.Y - (double)first.Y * second.X;
            }
            return area / 2;
        }

        // Independent normal fits can cross near a sharp tip. Remove only the
        // short loop at their measured intersection; a large crossing remains
        // ambiguous and is rejected by the caller. Six millimetres bounds the
        // affected perimeter, not an outward crop allowance.
        static PointF[] RemoveSmallLoops(PointF[] points, double maximumPerimeter)
        {
            List<PointF> result = new List<PointF>(points);
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int first = 0; first < result.Count && !changed; first++)
                    for (int second = first + 2; second < result.Count; second++)
                    {
                        if (first == 0 && second == result.Count - 1) continue;
                        PointF a = result[first], b = result[(first + 1) % result.Count];
                        PointF c = result[second], d = result[(second + 1) % result.Count];
                        if (Cross(a, b, c) * Cross(a, b, d) >= 0 || Cross(c, d, a) * Cross(c, d, b) >= 0) continue;
                        double sideA = Cross(c, d, a), sideB = Cross(c, d, b);
                        double fraction = sideA / (sideA - sideB);
                        PointF intersection = new PointF((float)(a.X + (b.X - a.X) * fraction), (float)(a.Y + (b.Y - a.Y) * fraction));
                        double perimeter = Distance(intersection, b) + Distance(c, intersection);
                        for (int index = first + 1; index < second; index++) perimeter += Distance(result[index], result[index + 1]);
                        if (perimeter > maximumPerimeter) continue;
                        result.RemoveRange(first + 1, second - first);
                        result.Insert(first + 1, intersection);
                        changed = true;
                        break;
                    }
            }
            return result.ToArray();
        }

        static bool SelfIntersects(PointF[] points)
        {
            for (int first = 0; first < points.Length; first++)
                for (int second = first + 2; second < points.Length; second++)
                {
                    if (first == 0 && second == points.Length - 1) continue;
                    PointF a = points[first], b = points[(first + 1) % points.Length];
                    PointF c = points[second], d = points[(second + 1) % points.Length];
                    if (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0) return true;
                }
            return false;
        }

        static double Cross(PointF a, PointF b, PointF c) { return (double)(b.X - a.X) * (c.Y - a.Y) - (double)(b.Y - a.Y) * (c.X - a.X); }
        static double Distance(PointF a, PointF b) { return Math.Sqrt((double)(b.X - a.X) * (b.X - a.X) + (double)(b.Y - a.Y) * (b.Y - a.Y)); }
        static double SegmentDistance(PointF point, PointF a, PointF b)
        {
            double x = b.X - a.X, y = b.Y - a.Y, square = x * x + y * y;
            double fraction = square <= 0 ? 0 : Math.Max(0, Math.Min(1, ((point.X - a.X) * x + (point.Y - a.Y) * y) / square));
            return Distance(point, new PointF((float)(a.X + fraction * x), (float)(a.Y + fraction * y)));
        }
        static PointF Rotate(double x, double y, double cosine, double sine) { return new PointF((float)(x * cosine - y * sine), (float)(x * sine + y * cosine)); }
        static double PhysicalLength(PointF a, PointF b, RawImage page) { double x = (b.X - a.X) / page.XDpi, y = (b.Y - a.Y) / page.YDpi; return Math.Sqrt(x * x + y * y); }
        static double Sample(byte[] plane, int width, int height, double x, double y)
        {
            x = Math.Max(0, Math.Min(width - 1, x - 0.5)); y = Math.Max(0, Math.Min(height - 1, y - 0.5));
            int left = (int)x, top = (int)y; double horizontal = x - left, vertical = y - top;
            int right = Math.Min(width - 1, left + 1), bottom = Math.Min(height - 1, top + 1);
            return (plane[top * width + left] * (1 - horizontal) + plane[top * width + right] * horizontal) * (1 - vertical)
                + (plane[bottom * width + left] * (1 - horizontal) + plane[bottom * width + right] * horizontal) * vertical;
        }
    }
}

