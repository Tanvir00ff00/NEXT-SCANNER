using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>
    /// Joins regions that are pieces of one item back into one region.
    ///
    /// Two things cannot both be true: that these are separate items, and that
    /// they lie on top of each other. Items rest on a sheet of glass, so a
    /// second item beside the first begins where the first ends. When two
    /// accepted regions overlap by most of the smaller one and agree on their
    /// angle, they are not two items -- they are one item that arrived in
    /// pieces.
    ///
    /// Why an item arrives in pieces is the same reason a card came back at half
    /// its height: a pale card on pale glass is invisible to the question the
    /// mask is built from, so the mask breaks into bands wherever the card is
    /// pale, and each surviving band is accepted on its own. On the bed of
    /// 2026-09-20 a card lying at 21 degrees came back as a 2.98 x 0.33 in
    /// strip, a 2.27 x 1.97 in middle and a 2.95 x 0.36 in strip, all three at
    /// the same angle and all three at High confidence.
    ///
    /// The join is deliberately blunt: it takes the smallest box, at the shared
    /// angle, that contains both pieces. That box is usually still short of the
    /// item's real border, because the outermost band is itself short. Putting
    /// the border in the right place is the shadow pass's job, and it can only
    /// do it once the pieces are one region -- while they are separate they
    /// stand in each other's way, each one's search stopping at the next.
    /// </summary>
    internal static class PlatenFragments
    {
        /// <summary>
        /// How much of the smaller region must lie inside the larger before the
        /// two are the same item. Well clear of the small overlaps that two
        /// genuinely separate items produce when their corners are generous.
        /// </summary>
        const double SharedArea = 0.5;

        /// <summary>
        /// How far apart two pieces' angles may be, in degrees. Pieces of one
        /// card are measured off the same borders and agree closely; the couple
        /// of degrees of slack is for a short strip, whose angle is the least
        /// certain thing about it.
        /// </summary>
        const double AngleAgreement = 8.0;

        internal static void Join(RawImage page, List<CropRegion> found, PlatenDetectionReport report)
        {
            if (page == null || found == null || found.Count < 2) return;

            bool joined = true;
            int guard = 0;
            while (joined && guard++ < 8)
            {
                joined = false;
                for (int first = 0; first < found.Count && !joined; first++)
                {
                    for (int second = first + 1; second < found.Count && !joined; second++)
                    {
                        CropRegion a = found[first], b = found[second];
                        if (a == null || b == null) continue;

                        // Free-form stock is carried by its outline; its
                        // rectangle is an envelope and says nothing about what
                        // the piece really covers.
                        if (a.Outline != null || b.Outline != null) continue;

                        Box one = Box.From(page, a), two = Box.From(page, b);
                        if (one == null || two == null) continue;
                        if (!Agree(one.Angle, two.Angle)) continue;

                        Box small = one.Area() <= two.Area() ? one : two;
                        Box large = ReferenceEquals(small, one) ? two : one;
                        double shared = Inside(small, large);

                        string because = null;
                        if (shared >= SharedArea)
                            because = (shared * 100).ToString("0") + "% of the smaller lies inside the larger";
                        else if (Touching(one, two, page))
                            because = "they touch along a full side with no glass between them";
                        if (because == null) continue;

                        report.Add("joined " + one + " and " + two + ": " + because +
                                   " and their angles agree, so they are one item");

                        Apply(page, a, Union(one, two, large.Angle));
                        if (b.Confidence > a.Confidence) a.Confidence = b.Confidence;
                        a.Reason += "; joined with a piece of the same item";
                        found.RemoveAt(second);
                        joined = true;
                    }
                }
            }
        }

        /// <summary>
        /// Whether two regions are two halves of one thing rather than two
        /// things side by side.
        ///
        /// Items resting on a sheet of glass are separated by glass: put two
        /// cards down and there is bare platen between them. The two pages of an
        /// open passport have no such gap -- they meet at the spine, and on the
        /// owner's bed they overlap by two millimetres. So a pair that meets
        /// along a whole side, with nothing between them, is one item; a pair
        /// with a real gap is two, however alike they look.
        ///
        /// The test is done in the larger one's own axes, and needs the pair to
        /// line up along the shared side as well as meet across it. Two cards at
        /// opposite corners of the bed can have touching bounding intervals on
        /// one axis while being nowhere near each other.
        /// </summary>
        static bool Touching(Box one, Box two, RawImage page)
        {
            Box large = one.Area() >= two.Area() ? one : two;
            Box small = ReferenceEquals(large, one) ? two : one;

            double alongLow, alongHigh, acrossLow, acrossHigh;
            Span(small, large, out alongLow, out alongHigh, out acrossLow, out acrossHigh);

            // Across the join: a gap wider than this is bare glass, and glass
            // between two things means two things. Two millimetres, because the
            // spine of an open passport measures about one on this bed while
            // anything actually placed separately is centimetres apart -- a
            // scanner's own instructions ask for ten.
            const double GapAllowedMm = 2.0;
            double perPixel = 25.4 / Dpi(page == null ? 300 : page.XDpi);
            double allowed = GapAllowedMm / perPixel;

            double gap = Math.Max(acrossLow - large.HalfH, -large.HalfH - acrossHigh);
            double sideGap = Math.Max(alongLow - large.HalfW, -large.HalfW - alongHigh);

            bool meetsAcross = gap <= allowed;
            bool meetsAlong = sideGap <= allowed;
            if (!meetsAcross && !meetsAlong) return false;

            // And lined up: most of the smaller's width has to sit against the
            // larger, not merely brush a corner of it.
            double overlapAlong = Math.Min(alongHigh, large.HalfW) - Math.Max(alongLow, -large.HalfW);
            double overlapAcross = Math.Min(acrossHigh, large.HalfH) - Math.Max(acrossLow, -large.HalfH);
            double reachAlong = Math.Min(alongHigh - alongLow, large.HalfW * 2);
            double reachAcross = Math.Min(acrossHigh - acrossLow, large.HalfH * 2);

            if (meetsAcross && reachAlong > 0 && overlapAlong / reachAlong >= 0.6) return true;
            if (meetsAlong && reachAcross > 0 && overlapAcross / reachAcross >= 0.6) return true;
            return false;
        }


        /// <summary>The smaller box measured in the larger one's axes.</summary>
        static void Span(Box small, Box large, out double alongLow, out double alongHigh,
                         out double acrossLow, out double acrossHigh)
        {
            alongLow = acrossLow = double.MaxValue;
            alongHigh = acrossHigh = double.MinValue;
            foreach (PointF corner in small.Corners())
            {
                double dx = corner.X - large.Cx, dy = corner.Y - large.Cy;
                double a = dx * large.Cos + dy * large.Sin, b = -dx * large.Sin + dy * large.Cos;
                alongLow = Math.Min(alongLow, a); alongHigh = Math.Max(alongHigh, a);
                acrossLow = Math.Min(acrossLow, b); acrossHigh = Math.Max(acrossHigh, b);
            }
        }


        /// <summary>Whether two angles describe the same lie, allowing for quarter turns.</summary>
        static bool Agree(double first, double second)
        {
            double difference = Math.Abs(first - second) % 90;
            if (difference > 45) difference = 90 - difference;
            return difference <= AngleAgreement;
        }

        /// <summary>
        /// What fraction of <paramref name="small"/> lies within
        /// <paramref name="large"/>, counted over a grid of points.
        ///
        /// Counting points rather than clipping one quadrilateral against
        /// another: the answer only has to be good enough to tell most of a
        /// region from a corner of it, and a grid cannot get that wrong.
        /// </summary>
        static double Inside(Box small, Box large)
        {
            const int Steps = 12;
            int within = 0, total = 0;
            for (int i = 0; i < Steps; i++)
                for (int j = 0; j < Steps; j++)
                {
                    double a = (i + 0.5) / Steps * 2 - 1, b = (j + 0.5) / Steps * 2 - 1;
                    PointF point = small.At(a * small.HalfW, b * small.HalfH);
                    total++;
                    if (large.Holds(point)) within++;
                }
            return total == 0 ? 0 : within / (double)total;
        }

        /// <summary>The smallest box at <paramref name="angle"/> holding both.</summary>
        static Box Union(Box one, Box two, double angle)
        {
            double radians = angle * Math.PI / 180;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);

            double left = double.MaxValue, right = double.MinValue;
            double top = double.MaxValue, bottom = double.MinValue;
            foreach (Box box in new Box[] { one, two })
                foreach (PointF corner in box.Corners())
                {
                    double a = corner.X * cos + corner.Y * sin;
                    double b = -corner.X * sin + corner.Y * cos;
                    left = Math.Min(left, a); right = Math.Max(right, a);
                    top = Math.Min(top, b); bottom = Math.Max(bottom, b);
                }

            double cx = (left + right) / 2, cy = (top + bottom) / 2;
            return new Box
            {
                Cx = cx * cos - cy * sin, Cy = cx * sin + cy * cos,
                HalfW = (right - left) / 2, HalfH = (bottom - top) / 2,
                Angle = angle, Cos = cos, Sin = sin
            };
        }

        static void Apply(RawImage page, CropRegion item, Box box)
        {
            PointF[] corners = box.Corners();
            float left = corners[0].X, right = corners[0].X, top = corners[0].Y, bottom = corners[0].Y;
            foreach (PointF corner in corners)
            {
                left = Math.Min(left, corner.X); right = Math.Max(right, corner.X);
                top = Math.Min(top, corner.Y); bottom = Math.Max(bottom, corner.Y);
            }
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(page.Width, right); bottom = Math.Min(page.Height, bottom);

            if (item.Box == null) item.Box = new RotatedBox();
            item.Box.IsValid = true;
            item.Box.Corners = corners;
            item.Box.Width = (float)(box.HalfW * 2);
            item.Box.Height = (float)(box.HalfH * 2);
            item.Box.Angle = (float)box.Angle;
            item.Box.RawAngle = (float)box.Angle;
            item.Box.Center = new PointF((float)box.Cx, (float)box.Cy);
            item.Box.AABB = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                                               (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
            item.SkewDegrees = (float)box.Angle;
            item.NormRect = new RectangleF(left / page.Width, top / page.Height,
                                           (right - left) / page.Width, (bottom - top) / page.Height);
            item.WidthInches = box.HalfW * 2 / Dpi(page.XDpi);
            item.HeightInches = box.HalfH * 2 / Dpi(page.YDpi);
        }

        static double Dpi(double value)
        {
            return value >= 1 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 300.0;
        }

        /// <summary>A region in its own axes, the same frame the rest of the engine uses.</summary>
        sealed class Box
        {
            internal double Cx, Cy, HalfW, HalfH, Angle, Cos, Sin;

            internal static Box From(RawImage page, CropRegion item)
            {
                RotatedBox source = item.Box;
                if (source != null && source.IsValid && source.Width > 1 && source.Height > 1)
                {
                    double radians = source.Angle * Math.PI / 180;
                    return new Box
                    {
                        Cx = source.Center.X, Cy = source.Center.Y,
                        HalfW = source.Width / 2.0, HalfH = source.Height / 2.0,
                        Angle = source.Angle, Cos = Math.Cos(radians), Sin = Math.Sin(radians)
                    };
                }

                RectangleF norm = item.NormRect;
                double w = norm.Width * page.Width, h = norm.Height * page.Height;
                if (w < 2 || h < 2) return null;
                return new Box
                {
                    Cx = (norm.X + norm.Width / 2f) * page.Width,
                    Cy = (norm.Y + norm.Height / 2f) * page.Height,
                    HalfW = w / 2.0, HalfH = h / 2.0, Angle = 0, Cos = 1, Sin = 0
                };
            }

            internal double Area() { return HalfW * HalfH; }

            internal PointF At(double a, double b)
            {
                return new PointF((float)(Cx + Cos * a - Sin * b), (float)(Cy + Sin * a + Cos * b));
            }

            internal PointF[] Corners()
            {
                return new PointF[]
                {
                    At(-HalfW, -HalfH), At(HalfW, -HalfH), At(HalfW, HalfH), At(-HalfW, HalfH)
                };
            }

            internal bool Holds(PointF point)
            {
                double dx = point.X - Cx, dy = point.Y - Cy;
                double a = dx * Cos + dy * Sin, b = -dx * Sin + dy * Cos;
                return Math.Abs(a) <= HalfW && Math.Abs(b) <= HalfH;
            }

            public override string ToString()
            {
                return (HalfW * 2).ToString("0") + "x" + (HalfH * 2).ToString("0") +
                       " at " + Cx.ToString("0") + "," + Cy.ToString("0") +
                       " " + Angle.ToString("0.0") + " deg";
            }
        }
    }
}
