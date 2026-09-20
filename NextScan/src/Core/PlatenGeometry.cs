// =============================================================================
// NextScan Studio - orientation independent candidate geometry
// Plan ref: universal platen work order sections 2.1 and 4.
// Axis-aligned solidity rejects a clean ID card at 45 degrees. A convex hull
// and its minimum-area rectangle measure the stock rather than its placement.
// Angles describe geometry modulo 90 degrees, not the reading direction of text.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    internal static class PlatenGeometry
    {
        internal static RotatedBox Fit(List<PointF> boundary)
        {
            if (boundary.Count < 3) return null;
            boundary.Sort(delegate(PointF first, PointF second)
            {
                int order = first.X.CompareTo(second.X);
                return order == 0 ? first.Y.CompareTo(second.Y) : order;
            });
            List<PointF> hull = new List<PointF>();
            foreach (PointF point in boundary)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            int lower = hull.Count;
            for (int index = boundary.Count - 2; index >= 0; index--)
            {
                PointF point = boundary[index];
                while (hull.Count > lower && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(point);
            }
            hull.RemoveAt(hull.Count - 1);
            double bestArea = double.MaxValue;
            RotatedBox best = null;
            for (int edge = 0; edge < hull.Count; edge++)
            {
                PointF first = hull[edge], second = hull[(edge + 1) % hull.Count];
                double angle = Math.Atan2(second.Y - first.Y, second.X - first.X);
                while (angle < -Math.PI / 4) angle += Math.PI / 2;
                while (angle >= Math.PI / 4) angle -= Math.PI / 2;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                double left = double.MaxValue, top = double.MaxValue;
                double right = double.MinValue, bottom = double.MinValue;
                foreach (PointF point in hull)
                {
                    double across = point.X * cosine + point.Y * sine;
                    double along = -point.X * sine + point.Y * cosine;
                    left = Math.Min(left, across); right = Math.Max(right, across);
                    top = Math.Min(top, along); bottom = Math.Max(bottom, along);
                }
                // Boundary samples are pixel centres. Their half-pixel square
                // footprint must be included or even a horizontal card shrinks.
                double footprint = (Math.Abs(cosine) + Math.Abs(sine)) / 2;
                left -= footprint; right += footprint; top -= footprint; bottom += footprint;
                double area = (right - left) * (bottom - top);
                if (area >= bestArea) continue;
                bestArea = area;
                PointF[] corners = new PointF[]
                {
                    Rotate(left, top, cosine, sine), Rotate(right, top, cosine, sine),
                    Rotate(right, bottom, cosine, sine), Rotate(left, bottom, cosine, sine)
                };
                best = new RotatedBox
                {
                    IsValid = true, Corners = corners, Width = (float)(right - left),
                    Height = (float)(bottom - top), Angle = (float)(angle * 180 / Math.PI),
                    RawAngle = (float)(angle * 180 / Math.PI),
                    Center = Rotate((left + right) / 2, (top + bottom) / 2, cosine, sine)
                };
            }
            return best;
        }

        static double Cross(PointF origin, PointF first, PointF second)
        {
            return (double)(first.X - origin.X) * (second.Y - origin.Y)
                - (double)(first.Y - origin.Y) * (second.X - origin.X);
        }

        internal static double IntersectionArea(PointF[] first, PointF[] second)
        {
            List<PointF> polygon = new List<PointF>(first);
            for (int edge = 0; edge < second.Length && polygon.Count > 0; edge++)
            {
                PointF start = second[edge], end = second[(edge + 1) % second.Length];
                List<PointF> clipped = new List<PointF>();
                PointF previous = polygon[polygon.Count - 1];
                double previousSide = Cross(start, end, previous);
                foreach (PointF current in polygon)
                {
                    double currentSide = Cross(start, end, current);
                    if ((currentSide >= 0) != (previousSide >= 0))
                    {
                        double fraction = previousSide / (previousSide - currentSide);
                        clipped.Add(new PointF((float)(previous.X + (current.X - previous.X) * fraction),
                            (float)(previous.Y + (current.Y - previous.Y) * fraction)));
                    }
                    if (currentSide >= 0) clipped.Add(current);
                    previous = current;
                    previousSide = currentSide;
                }
                polygon = clipped;
            }
            double area = 0;
            for (int index = 0; index < polygon.Count; index++)
            {
                PointF firstPoint = polygon[index], next = polygon[(index + 1) % polygon.Count];
                area += (double)firstPoint.X * next.Y - (double)firstPoint.Y * next.X;
            }
            return Math.Abs(area) / 2;
        }

        static PointF Rotate(double across, double along, double cosine, double sine)
        {
            return new PointF((float)(across * cosine - along * sine), (float)(across * sine + along * cosine));
        }
    }
}
