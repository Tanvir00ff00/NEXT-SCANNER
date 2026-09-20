using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>
    /// Checks each edge of an accepted region against the shadow the item
    /// actually casts on the glass, and moves an edge that was placed on a
    /// printed line rather than on the item's border.
    ///
    /// Why this exists
    /// ---------------
    /// Everything upstream of here decides where an item is by asking whether a
    /// pixel looks different from the glass. That question has no answer for a
    /// white card on white glass: the card's own pale margin and the bare platen
    /// are the same brightness, within a level or two. The card's mask therefore
    /// breaks into bands wherever the card is pale, and a boundary settles on
    /// the first printed edge it can find. That is how a card 2.13 inches tall
    /// was delivered at 1.06 inches -- the right width, exactly half the height,
    /// and reported at High confidence, because every test the boundary had to
    /// pass was about the printed line it had landed on rather than the card.
    ///
    /// What is different here is the question. An item lying on glass lifts its
    /// own edge a fraction of a millimetre and casts a narrow shadow all the way
    /// around itself. Print does not. So instead of asking whether the pixels
    /// look like an item, this asks whether there is a trough -- darker than the
    /// material on both sides of it -- and how deep that trough is.
    ///
    /// Depth is measured in multiples of the page's own noise, taken from the
    /// capture itself. That is the one threshold in this engine that means the
    /// same physical thing at every resolution and on every scanner: the rest
    /// are absolute level counts measured on 100 dpi pages, which quietly change
    /// meaning when the same bed is scanned at 300, because averaging to working
    /// size cuts the noise about threefold. A real border on the reference
    /// captures runs from 17 to 34 sigma deep; the printed line that swallowed
    /// half the card measures 3.8. No tuning fits inside that gap.
    ///
    /// Along the item's own axes
    /// -------------------------
    /// The profile is taken along the edge and stepped along its normal, both in
    /// the item's own frame rather than the image's. Sampling along image rows
    /// instead would smear a tilted border across them until the trough stopped
    /// being a trough, which is why this used to refuse anything past three
    /// degrees of skew -- and why a card lying at 21 degrees came back as three
    /// separate pieces while the one beside it at 5.6 degrees came back whole.
    ///
    /// Deliberately narrow
    /// -------------------
    /// An edge is only ever moved outward, and only to a place carrying a
    /// border's own evidence. Any part of an edge with another region beyond it
    /// is dropped from the measurement rather than measured across, so an item
    /// can never annex the one beside it; where nothing is left to measure, the
    /// edge is left exactly as it was found.
    /// </summary>
    internal static class PlatenShadowEdges
    {
        /// <summary>How deep a trough must be, in multiples of sensor noise, to be a border.</summary>
        const double BorderSigma = 8.0;

        /// <summary>How far outward to look, as a multiple of the region's own extent.</summary>
        const double SearchExtent = 1.25;

        /// <summary>Shoulder half-width either side of a trough, in inches.</summary>
        const double ShoulderInches = 0.06;

        /// <summary>How large a step onto glass must be, in multiples of sensor noise.</summary>
        const double StepSigma = 8.0;

        /// <summary>
        /// How far a step may take to complete and still be an item's border, in
        /// inches. A border is a geometric discontinuity: the paper stops, and
        /// the level changes within a pixel or two of where it stops. A diffuse
        /// shadow reaches the same size over several millimetres, which is what
        /// separates the two -- not how big the change is, but how abruptly it
        /// happens.
        /// </summary>
        const double SharpInches = 0.03;

        /// <summary>
        /// How far past an edge the search for glass begins, in inches. The item
        /// casts its own shadow into this band, so nothing here is glass and
        /// measuring it would only ever say so.
        /// </summary>
        const double GlassSkipInches = 0.12;

        /// <summary>
        /// How long a stretch of glass settles the question, in inches. It has
        /// to be long: a card's pale margin is as smooth as glass across a tenth
        /// of an inch, and only gives itself away over a wider stretch, where
        /// print returns and glass does not.
        /// </summary>
        const double GlassRunInches = 0.40;

        /// <summary>
        /// How far the material past an edge may bend away from its own straight
        /// trend and still be glass, in multiples of sensor noise.
        /// </summary>
        const double GlassFlatSigma = 1.5;

        /// <summary>
        /// How much the material past a step may wander and still be glass, in
        /// multiples of sensor noise. Bare glass varies only by its own grain
        /// and the lamp's slow falloff; anything printed wanders further.
        /// </summary>
        const double GlassSigma = 2.0;

        /// <summary>How wide a berth to give another region, in inches.</summary>
        const double NeighbourClearanceInches = 0.05;

        /// <summary>
        /// Skew beyond which an edge is left alone.
        ///
        /// Forty-five degrees, which is every angle a rotated box can carry --
        /// they are normalised to plus or minus forty-five. The limit used to be
        /// three, because the profile was taken along image rows and a tilted
        /// border smeared across them until the trough stopped being a trough.
        /// The profile now follows the item's own axes, so the limit is no
        /// longer measuring anything, and it is kept only as the place to put a
        /// bound back if one is ever needed.
        /// </summary>
        const double MaxSkewDegrees = 45.0;

        /// <summary>
        /// A region in its own axes: centre, half extents along those axes, and
        /// the axes themselves. u = (Cos, Sin) runs along the width, v =
        /// (-Sin, Cos) along the height, matching how every rotated box in this
        /// engine is built.
        /// </summary>
        sealed class Frame
        {
            internal double Cx, Cy, HalfW, HalfH, Cos, Sin;

            internal Frame Copy()
            {
                return new Frame { Cx = Cx, Cy = Cy, HalfW = HalfW, HalfH = HalfH, Cos = Cos, Sin = Sin };
            }

            internal bool Same(Frame other)
            {
                return Math.Abs(Cx - other.Cx) < 0.01 && Math.Abs(Cy - other.Cy) < 0.01
                    && Math.Abs(HalfW - other.HalfW) < 0.01 && Math.Abs(HalfH - other.HalfH) < 0.01;
            }
        }

        internal static void Refine(RawImage page, List<CropRegion> found, PlatenDetectionReport report)
        {
            if (page == null || found == null || found.Count == 0) return;
            if (page.Width < 8 || page.Height < 8) return;

            double sigma = NoiseFloor(page);
            if (sigma <= 0.05) return;         // a synthetic page with no grain: nothing to scale by
            report.Add("sensor noise floor " + sigma.ToString("0.00") + " levels");

            // Free-form stock is carried by its outline, not by a rectangle, so
            // moving the rectangle's sides would say nothing about where it is.
            Frame[] others = new Frame[found.Count];
            for (int index = 0; index < found.Count; index++)
                others[index] = found[index] == null ? null : ToFrame(page, found[index]);

            for (int index = 0; index < found.Count; index++)
            {
                CropRegion item = found[index];
                if (item == null || item.Outline != null) continue;

                Frame frame = ToFrame(page, item);
                if (frame == null || frame.HalfW < 4 || frame.HalfH < 4) continue;
                if (Math.Abs(Angle(frame)) > MaxSkewDegrees) continue;

                Frame moved = frame.Copy();
                for (int side = 0; side < 4; side++)
                    Extend(page, others, index, moved, frame, side, sigma, report);

                if (!moved.Same(frame)) Apply(page, item, moved, frame, report);
            }
        }

        /// <summary>
        /// The region in its own axes. A rotated box carries them directly; a
        /// region that never got one is square to the image, and its normalised
        /// rectangle says the same thing.
        /// </summary>
        static Frame ToFrame(RawImage page, CropRegion item)
        {
            RotatedBox box = item.Box;
            if (box != null && box.IsValid && box.Width > 1 && box.Height > 1)
            {
                double radians = box.Angle * Math.PI / 180;
                return new Frame
                {
                    Cx = box.Center.X, Cy = box.Center.Y,
                    HalfW = box.Width / 2.0, HalfH = box.Height / 2.0,
                    Cos = Math.Cos(radians), Sin = Math.Sin(radians)
                };
            }

            RectangleF norm = item.NormRect;
            double w = norm.Width * page.Width, h = norm.Height * page.Height;
            if (w < 2 || h < 2) return null;
            return new Frame
            {
                Cx = (norm.X + norm.Width / 2f) * page.Width,
                Cy = (norm.Y + norm.Height / 2f) * page.Height,
                HalfW = w / 2.0, HalfH = h / 2.0, Cos = 1, Sin = 0
            };
        }

        /// <summary>The frame's lie, in degrees.</summary>
        static double Angle(Frame f)
        {
            return Math.Atan2(f.Sin, f.Cos) * 180 / Math.PI;
        }

        static Rectangle Bounds(RawImage page, CropRegion item)
        {
            if (item.Box != null && item.Box.IsValid && item.Box.AABB.Width > 0) return item.Box.AABB;
            RectangleF norm = item.NormRect;
            return Rectangle.FromLTRB(
                (int)Math.Floor(norm.Left * page.Width), (int)Math.Floor(norm.Top * page.Height),
                (int)Math.Ceiling(norm.Right * page.Width), (int)Math.Ceiling(norm.Bottom * page.Height));
        }

        /// <summary>The outward normal of a side. 0 left, 1 top, 2 right, 3 bottom.</summary>
        static void Outward(Frame f, int side, out double x, out double y)
        {
            switch (side)
            {
                case 0: x = -f.Cos; y = -f.Sin; break;
                case 1: x = f.Sin; y = -f.Cos; break;
                case 2: x = f.Cos; y = f.Sin; break;
                default: x = -f.Sin; y = f.Cos; break;
            }
        }

        /// <summary>The direction along a side.</summary>
        static void Along(Frame f, int side, out double x, out double y)
        {
            if (side == 0 || side == 2) { x = -f.Sin; y = f.Cos; }
            else { x = f.Cos; y = f.Sin; }
        }

        /// <summary>How far the side sits from the centre.</summary>
        static double Reach(Frame f, int side) { return (side == 0 || side == 2) ? f.HalfW : f.HalfH; }

        /// <summary>Half the length of the side.</summary>
        static double Span(Frame f, int side) { return (side == 0 || side == 2) ? f.HalfH : f.HalfW; }

        static void Extend(RawImage page, Frame[] others, int self,
                           Frame frame, Frame original, int side, double sigma,
                           PlatenDetectionReport report)
        {
            double dpi = Dpi((side == 0 || side == 2) ? page.XDpi : page.YDpi);
            int shoulder = Math.Max(2, (int)(ShoulderInches * dpi));
            int run = Math.Max(3, (int)(0.04 * dpi));
            int sharp = Math.Max(2, (int)(SharpInches * dpi));
            int skip = Math.Max(2, (int)(GlassSkipInches * dpi));
            int glass = Math.Max(8, (int)(GlassRunInches * dpi));
            int clearance = Math.Max(2, (int)(NeighbourClearanceInches * dpi));

            int limit = (int)(Reach(original, side) * 2 * SearchExtent);
            limit = Math.Min(limit, MaxGrowth(others, self, frame, side, limit, clearance));
            if (limit < 4) return;

            double[] profile = Profile(page, others, self, frame, side,
                                       shoulder, limit + skip + glass + 1, clearance);
            if (profile == null) return;

            // One question decides whether this edge is wrong, and it is not
            // how convincing the edge looks. A printed line is as sharp as a
            // border and can be deeper: the midline that halved the card scores
            // 13 sigma measured here at full resolution, so no bar low enough to
            // admit a real border would reject it. What a printed line cannot do
            // is have bare glass on the far side of it. So an edge with glass
            // beyond it is finished and is left untouched, whatever it scores,
            // and only an edge with more item beyond it is searched outward.
            if (GlassBeyond(profile, shoulder, skip, glass, sigma, true)) return;
            double here = Depth(profile, shoulder, shoulder);

            int best = -1; bool bestIsStep = false; int trough = -1; double troughDepth = 0;
            string why = null;
            for (int d = shoulder + 2; d < profile.Length - shoulder; d++)
            {
                // Whatever the evidence at the candidate, the far side of it
                // has to be bare glass. Without this an edge walks out into a
                // broad soft shadow -- a passport cover throws one ninety pixels
                // wide -- and settles wherever the decay happens to look like a
                // step, which is not where the paper stops.
                // How soon the glass has to start depends on which signature is
                // being tested, and the two differ physically. A step onto glass
                // IS the glass beginning, so it has to be there within a pixel
                // or two. A trough is the middle of a shadow, which lies on the
                // glass outside the item, so glass begins a shadow's width later
                // by definition. Holding both to the trough's distance lets a
                // step qualify that far short of the border and pulls the edge
                // in -- the card that was half its height came back three
                // millimetres under. Holding both to the step's distance
                // rejects every genuine shadow and pushes edges out instead.

                // Two signatures, because an item can show either. One lying
                // loose casts a shadow trough all round itself; one pressed
                // flat by the lid casts almost none, and shows instead as a
                // single sharp step onto glass that then stays quiet.
                //
                // The first one found going outward is the border, and the
                // search stops there. Not the furthest: everything past the
                // point where glass begins is also glass, so a candidate beyond
                // the first is evidence of something else on the bed, not of
                // this item continuing. Preferring the furthest only looked
                // right while the search was clamped so tightly it could not
                // reach that far -- loosen the clamp and every card grew four
                // millimetres.
                double depth = Depth(profile, d, shoulder);
                if (depth >= BorderSigma * sigma && GlassBeyond(profile, d, skip, glass, sigma, false))
                {
                    best = d; bestIsStep = false; trough = d; troughDepth = depth;
                    why = "a " + (depth / sigma).ToString("0.0") + " sigma shadow";
                    break;
                }

                double step;
                if (!GlassBeyond(profile, d, sharp, glass, sigma, false)) continue;
                if (!StepOntoGlass(profile, d, run, sharp, sigma, out step)) continue;
                best = d; bestIsStep = true;
                why = "a " + (step / sigma).ToString("0.0") + " sigma step onto quiet glass";
                break;
            }
            if (best < 0) return;

            // An item casts its shadow outside itself, so the glass beyond that
            // shadow steps up exactly as the item's own border would. Nothing of
            // the item lies past its shadow: when the outermost candidate is a
            // step and a shadow sits immediately inside it, that step is the far
            // side of the shadow and the border is the shadow.
            if (bestIsStep && trough >= 0 && trough < best && best - trough <= shoulder + run)
            {
                best = trough; bestIsStep = false;
                why = "a " + (troughDepth / sigma).ToString("0.0") + " sigma shadow just inside it";
            }

            // A shadow is cast on the glass beside the item, not on the item,
            // so its middle is already outside. The border is where the shadow
            // begins: the half-way point up its inner flank, which is the same
            // place whether the shadow is wide and soft or narrow and sharp.
            if (!bestIsStep) best = InnerFlank(profile, best, shoulder);

            int move = best - shoulder;
            if (move < 2) return;

            report.Add("edge " + Name(side) + " of the " + Size(original) + " region at " +
                       original.Cx.ToString("0") + "," + original.Cy.ToString("0") + " sat on a " +
                       (here / sigma).ToString("0.0") + " sigma step, which is print, not a border; moved out " +
                       move + " px to " + why);

            // The side moves out by `move`, so the region grows by `move` along
            // that axis and its centre shifts half as far in the same direction.
            double ox, oy;
            Outward(frame, side, out ox, out oy);
            frame.Cx += ox * move / 2.0;
            frame.Cy += oy * move / 2.0;
            if (side == 0 || side == 2) frame.HalfW += move / 2.0; else frame.HalfH += move / 2.0;
        }

        /// <summary>
        /// How far this side may travel before the region it belongs to would
        /// overlap another one.
        ///
        /// Dropping the rays that point at a neighbour is not enough on its own.
        /// Two items sitting above this one leave a gap between them, the rays
        /// through that gap are clear all the way to the far side, and the
        /// search happily settles on something two hundred pixels past both --
        /// which is how a passport grew upward through the space between two
        /// cards and came back half the bed tall. An item may not grow into the
        /// space another item occupies, whatever the gaps between them.
        /// </summary>
        static int MaxGrowth(Frame[] others, int self, Frame f, int side, int limit, int clearance)
        {
            int stride = Math.Max(2, clearance / 2);
            for (int t = stride; t <= limit; t += stride)
            {
                Frame grown = f.Copy();
                double ox, oy;
                Outward(f, side, out ox, out oy);
                grown.Cx += ox * t / 2.0;
                grown.Cy += oy * t / 2.0;
                if (side == 0 || side == 2) grown.HalfW += t / 2.0; else grown.HalfH += t / 2.0;

                for (int other = 0; other < others.Length; other++)
                {
                    if (other == self || others[other] == null) continue;

                    // The boxes themselves, not their enclosing rectangles. A
                    // card lying at twenty degrees has an enclosing rectangle
                    // whose empty corners reach a long way past the card, and
                    // testing those refuses it any room to grow before a pixel
                    // has been measured -- which froze a joined card three
                    // millimetres short of its own border.
                    if (Overlap(grown, others[other], clearance)) return Math.Max(0, t - stride - clearance);
                }
            }
            return limit;
        }

        /// <summary>
        /// Whether a point lies within a region, allowing a margin.
        ///
        /// Measured in the region's own axes. Its enclosing rectangle will not
        /// do: a card lying at twenty degrees has an enclosing rectangle whose
        /// empty corners reach a long way past the card, and testing against
        /// those declares a collision with a photo two centimetres clear of it.
        /// That is what froze a card four millimetres short -- it was refused
        /// any room to grow before a single pixel had been measured.
        /// </summary>
        static bool Holds(Frame f, double x, double y, double pad)
        {
            double dx = x - f.Cx, dy = y - f.Cy;
            double a = dx * f.Cos + dy * f.Sin, b = -dx * f.Sin + dy * f.Cos;
            return Math.Abs(a) <= f.HalfW + pad && Math.Abs(b) <= f.HalfH + pad;
        }

        /// <summary>
        /// Whether two regions overlap, each measured in its own axes.
        ///
        /// Separating axis test: two rectangles miss each other exactly when
        /// some edge direction of one of them has all of the other entirely to
        /// one side.
        /// </summary>
        static bool Overlap(Frame first, Frame second, double pad)
        {
            return !Apart(first, second, pad) && !Apart(second, first, pad);
        }

        static bool Apart(Frame frame, Frame other, double pad)
        {
            PointF[] corners = new PointF[]
            {
                Corner(other, -other.HalfW, -other.HalfH), Corner(other, other.HalfW, -other.HalfH),
                Corner(other, other.HalfW, other.HalfH), Corner(other, -other.HalfW, other.HalfH)
            };

            double lowA = double.MaxValue, highA = double.MinValue;
            double lowB = double.MaxValue, highB = double.MinValue;
            foreach (PointF corner in corners)
            {
                double dx = corner.X - frame.Cx, dy = corner.Y - frame.Cy;
                double a = dx * frame.Cos + dy * frame.Sin, b = -dx * frame.Sin + dy * frame.Cos;
                lowA = Math.Min(lowA, a); highA = Math.Max(highA, a);
                lowB = Math.Min(lowB, b); highB = Math.Max(highB, b);
            }
            return lowA > frame.HalfW + pad || highA < -frame.HalfW - pad
                || lowB > frame.HalfH + pad || highB < -frame.HalfH - pad;
        }

        static Rectangle Enclosing(Frame f)
        {
            PointF[] corners = new PointF[]
            {
                Corner(f, -f.HalfW, -f.HalfH), Corner(f, f.HalfW, -f.HalfH),
                Corner(f, f.HalfW, f.HalfH), Corner(f, -f.HalfW, f.HalfH)
            };
            float left = corners[0].X, right = corners[0].X, top = corners[0].Y, bottom = corners[0].Y;
            foreach (PointF corner in corners)
            {
                left = Math.Min(left, corner.X); right = Math.Max(right, corner.X);
                top = Math.Min(top, corner.Y); bottom = Math.Max(bottom, corner.Y);
            }
            return Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                                      (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
        }

        /// <summary>
        /// Mean brightness along the edge, stepping outward along its normal.
        ///
        /// Averaging across the middle of the edge and not its ends keeps a
        /// rounded corner out of the measurement, and any part of the edge with
        /// another region beyond it is dropped: looking outward there means
        /// looking straight at the neighbour, whose border would be the largest
        /// step in the window. Half an edge measured over bare glass is worth
        /// more than a whole one measured across the item next to it.
        /// </summary>
        static double[] Profile(RawImage page, Frame[] others, int self, Frame f, int side,
                                int inside, int outside, int clearance)
        {
            int length = inside + outside;
            if (length < 8) return null;

            double ox, oy, ax, ay;
            Outward(f, side, out ox, out oy);
            Along(f, side, out ax, out ay);
            double reach = Reach(f, side), span = Span(f, side) * 0.6;
            if (span < 1) return null;

            double step = Math.Max(1.0, span * 2 / 160);
            List<double> offsets = new List<double>();
            for (double b = -span; b <= span; b += step)
                if (!Blocked(page, others, self, f, side, b, length - inside, clearance))
                    offsets.Add(b);
            if (offsets.Count < 8) return null;

            double[] profile = new double[length];
            for (int d = 0; d < length; d++)
            {
                double t = reach + (d - inside);
                double total = 0;
                for (int i = 0; i < offsets.Count; i++)
                {
                    double value = Sample(page, f.Cx + ox * t + ax * offsets[i],
                                                f.Cy + oy * t + ay * offsets[i]);
                    if (value < 0) return Trim(profile, d);     // ran off the page
                    total += value;
                }
                profile[d] = total / offsets.Count;
            }
            return profile;
        }

        /// <summary>
        /// Whether the outward ray at this point along the edge runs into
        /// another region before the search would end.
        /// </summary>
        static bool Blocked(RawImage page, Frame[] others, int self, Frame f, int side,
                            double offset, int distance, int clearance)
        {
            double ox, oy, ax, ay;
            Outward(f, side, out ox, out oy);
            Along(f, side, out ax, out ay);
            double reach = Reach(f, side);

            for (int other = 0; other < others.Length; other++)
            {
                if (other == self || others[other] == null) continue;

                // Step the ray rather than solve it: the boxes are few, the
                // stride is coarse, and a region big enough to matter cannot
                // hide between two samples.
                for (int t = 0; t <= distance; t += Math.Max(2, clearance))
                {
                    double x = f.Cx + ox * (reach + t) + ax * offset;
                    double y = f.Cy + oy * (reach + t) + ay * offset;
                    if (Holds(others[other], x, y, clearance)) return true;
                }
            }
            return false;
        }

        static string Size(Frame f)
        {
            return (f.HalfW * 2).ToString("0") + "x" + (f.HalfH * 2).ToString("0");
        }

        /// <summary>
        /// Brightness at a point between pixels.
        ///
        /// Bilinear, because the profile steps along the item's normal and not
        /// along whole pixels: rounding to the nearest would blunt a one-pixel
        /// border into a two-pixel ramp on every tilted item, and the sharpness
        /// of that step is exactly what tells a border from a shadow.
        /// </summary>
        static double Sample(RawImage p, double x, double y)
        {
            if (x < 0 || y < 0 || x >= p.Width - 1 || y >= p.Height - 1) return -1;

            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            double top = Luminance(p, x0, y0) * (1 - fx) + Luminance(p, x0 + 1, y0) * fx;
            double bottom = Luminance(p, x0, y0 + 1) * (1 - fx) + Luminance(p, x0 + 1, y0 + 1) * fx;
            return top * (1 - fy) + bottom * fy;
        }

        /// <summary>
        /// Writes the widened frame back onto the region, in the same form the
        /// rest of the engine builds: corners in the box's own order, the
        /// enclosing rectangle taken from them, and the physical size measured
        /// along the box's axes rather than the image's.
        /// </summary>
        static void Apply(RawImage page, CropRegion item, Frame f, Frame was, PlatenDetectionReport report)
        {
            PointF[] corners = new PointF[]
            {
                Corner(f, -f.HalfW, -f.HalfH), Corner(f, f.HalfW, -f.HalfH),
                Corner(f, f.HalfW, f.HalfH), Corner(f, -f.HalfW, f.HalfH)
            };

            float left = corners[0].X, right = corners[0].X, top = corners[0].Y, bottom = corners[0].Y;
            foreach (PointF corner in corners)
            {
                left = Math.Min(left, corner.X); right = Math.Max(right, corner.X);
                top = Math.Min(top, corner.Y); bottom = Math.Max(bottom, corner.Y);
            }
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(page.Width, right); bottom = Math.Min(page.Height, bottom);

            if (item.Box != null)
            {
                item.Box.Corners = corners;
                item.Box.Width = (float)(f.HalfW * 2);
                item.Box.Height = (float)(f.HalfH * 2);
                item.Box.Center = new PointF((float)f.Cx, (float)f.Cy);
                item.Box.AABB = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                                                   (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
            }
            item.NormRect = new RectangleF(left / page.Width, top / page.Height,
                                           (right - left) / page.Width, (bottom - top) / page.Height);
            item.WidthInches = f.HalfW * 2 / Dpi(page.XDpi);
            item.HeightInches = f.HalfH * 2 / Dpi(page.YDpi);
            item.Reason += "; edges verified against the shadow the item casts";

            report.Add("shadow verification grew " + Size(was) + " to " + Size(f) +
                       " at " + f.Cx.ToString("0") + "," + f.Cy.ToString("0"));
        }

        static PointF Corner(Frame f, double a, double b)
        {
            return new PointF((float)(f.Cx + f.Cos * a - f.Sin * b),
                              (float)(f.Cy + f.Sin * a + f.Cos * b));
        }

        /// <summary>
        /// The capture's own noise, as a standard deviation in levels.
        ///
        /// Measured from differences between neighbouring pixels rather than
        /// from levels, so the illumination falloff across the bed cannot
        /// inflate it, and taken as a median so that print -- which is a
        /// minority of the page and much larger than grain -- cannot drag it.
        /// </summary>
        static double NoiseFloor(RawImage page)
        {
            int stepX = Math.Max(1, page.Width / 220);
            int stepY = Math.Max(1, page.Height / 220);

            List<int> samples = new List<int>();
            for (int y = 0; y + 1 < page.Height; y += stepY)
                for (int x = 0; x + 1 < page.Width; x += stepX)
                    samples.Add(Math.Abs(Luminance(page, x, y) - Luminance(page, x + 1, y)));
            if (samples.Count < 64) return 0;

            samples.Sort();
            double median = samples[samples.Count / 2];

            // A difference of two independent samples is sqrt(2) wider than the
            // noise itself, and the median of a half-normal is 0.6745 sigma.
            double sigma = median / (0.6745 * Math.Sqrt(2.0));

            // Never let the scale fall below one level. A capture with almost no
            // grain -- a synthetic page, or a scanner averaging heavily -- would
            // otherwise make eight sigma a step of a fraction of a level, and
            // every ripple in a soft shadow would read as a border. One level is
            // the smallest difference the image can express at all, so nothing
            // smaller can be evidence of anything.
            return Math.Max(sigma, 1.0);
        }

        static double[] Trim(double[] profile, int length)
        {
            if (length < 8) return null;
            double[] cut = new double[length];
            Array.Copy(profile, cut, length);
            return cut;
        }

        /// <summary>
        /// How far the profile dips at <paramref name="at"/> below the material
        /// on both sides of it, in levels.
        ///
        /// The shallower shoulder is the one that counts. A step from dark print
        /// up to bright glass has a bright shoulder and no dark one, and scoring
        /// it against the bright side alone would make every printed edge look
        /// like a border, which is the mistake this pass exists to undo.
        /// </summary>
        static double Depth(double[] profile, int at, int shoulder)
        {
            if (at - 1 < 0 || at + 1 >= profile.Length) return 0;

            // A shadow is a low point. Without insisting on that, every sample
            // part way up a ramp scores as a trough, because the shoulder behind
            // it reaches back across the real border into the bright inside of
            // the item -- which is how a card whose edge was already correct had
            // it pushed five pixels out into the shadow beside it.
            if (profile[at] > profile[at - 1] || profile[at] > profile[at + 1]) return 0;

            double low = profile[at];
            for (int d = Math.Max(0, at - 1); d <= Math.Min(profile.Length - 1, at + 1); d++)
                if (profile[d] < low) low = profile[d];

            double left = double.MinValue, right = double.MinValue;
            for (int d = Math.Max(0, at - shoulder); d < at - 1; d++) if (profile[d] > left) left = profile[d];
            for (int d = at + 2; d <= Math.Min(profile.Length - 1, at + shoulder); d++) if (profile[d] > right) right = profile[d];
            if (left == double.MinValue || right == double.MinValue) return 0;

            return Math.Min(left, right) - low;
        }

        /// <summary>
        /// Whether the material immediately beyond an edge is bare glass.
        ///
        /// Glass is recognised by being featureless over a stretch several times
        /// wider than any shadow: it varies only by the sensor's own grain and
        /// the lamp's slow falloff across the bed. Both the spread over the
        /// whole stretch and the step between neighbours are bounded, because
        /// the pale margin of a card is smooth from one row to the next yet
        /// still drifts several levels across a tenth of an inch, and it is
        /// exactly that margin this has to tell apart from glass.
        /// </summary>
        static bool GlassBeyond(double[] profile, int edge, int skip, int run, double sigma,
                                bool whenUnknown)
        {
            int from = edge + skip, to = Math.Min(profile.Length - 1, edge + skip + run);
            int n = to - from + 1;
            // Too close to the page border, or to the item next door, to see a
            // full stretch. Both callers then want the same thing -- leave the
            // boundary where it is -- which is a different answer for each: an
            // edge that cannot be checked is treated as finished, and a place
            // that cannot be checked is not somewhere to move an edge to.
            if (n < run / 2) return whenUnknown;

            // Flatness is measured against a straight line, not against the
            // level, because bare glass is not level: the lamp falls off across
            // the bed and drifts six or seven levels over a tenth of an inch,
            // which is more than the whole budget a flatness test can afford to
            // spend. What glass does not do is depart from that drift. A card's
            // pale margin drifts too, and bends away from its own trend by
            // several times the grain while doing it.
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int d = from; d <= to; d++)
            {
                double x = d - from, y = profile[d];
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double denominator = n * sxx - sx * sx;
            if (Math.Abs(denominator) < 1e-9) return true;
            double slope = (n * sxy - sx * sy) / denominator;
            double intercept = (sy - slope * sx) / n;

            for (int d = from; d <= to; d++)
                if (Math.Abs(profile[d] - (slope * (d - from) + intercept)) > GlassFlatSigma * sigma)
                    return false;
            return true;
        }

        /// <summary>
        /// Whether the profile steps at <paramref name="at"/> onto material
        /// quiet enough to be bare glass.
        ///
        /// The step is taken in absolute value on purpose. A dark card on the
        /// glass steps up at its edge and a pale one steps down, and which way
        /// it goes says nothing about whether it is an edge. What does say so is
        /// that the far side stops varying: print, texture and the item's own
        /// content all wander by more than grain, and glass does not.
        /// </summary>
        static bool StepOntoGlass(double[] profile, int at, int run, int sharp, double sigma, out double step)
        {
            step = 0;
            int gap = sharp;                     // the transition itself, measured by nobody
            if (at - gap - run < 0 || at + gap + run >= profile.Length) return false;

            // The two sides are measured clear of the transition, not up against
            // it. Against it, a gentle ramp yields a small step over a short
            // distance and passes for sharp; clear of it, the same ramp yields a
            // larger step that visibly takes the whole width to happen.
            double inside = 0, outside = 0;
            for (int d = at - gap - run; d < at - gap; d++) inside += profile[d];
            for (int d = at + gap + 1; d <= at + gap + run; d++) outside += profile[d];
            inside /= run; outside /= run;
            step = Math.Abs(outside - inside);
            if (step < StepSigma * sigma) return false;

            for (int d = at + gap + 1; d < at + gap + run; d++)
                if (Math.Abs(profile[d + 1] - profile[d]) > GlassSigma * sigma) return false;

            // How long the change takes, measured between the points where it
            // is one fifth and four fifths done. A border crosses both within a
            // pixel or two of each other; a shadow that fades over four
            // millimetres does not, however large it eventually gets.
            double low = Math.Min(inside, outside), high = Math.Max(inside, outside);
            double near = low + (high - low) * 0.2, far = low + (high - low) * 0.8;
            int startsAt = -1, endsAt = -1;
            for (int d = at - gap - run; d <= at + gap + run; d++)
            {
                bool pastNear = outside > inside ? profile[d] >= near : profile[d] <= near;
                bool pastFar = outside > inside ? profile[d] >= far : profile[d] <= far;
                if (pastNear && startsAt < 0) startsAt = d;
                if (pastFar && endsAt < 0) endsAt = d;
            }
            if (startsAt < 0 || endsAt < 0 || endsAt < startsAt) return false;
            if (endsAt - startsAt > sharp) return false;

            // The step has to be HERE, not merely somewhere in the window. Two
            // sides measured a step-width apart see the same transition from
            // anywhere nearby, so without this every position for several
            // pixels either side scores identically and which one wins is
            // decided by the glass test rather than by where the card ends --
            // a card whose border sat four pixels away was moved twelve.
            double middle = (startsAt + endsAt) / 2.0;
            return Math.Abs(middle - at) <= Math.Max(1, sharp / 2);
        }

        /// <summary>
        /// Walks inward from a shadow's darkest point to where it has recovered
        /// half way to the material inside, which is where the item starts.
        /// </summary>
        static int InnerFlank(double[] profile, int at, int shoulder)
        {
            double low = profile[at];
            double inside = double.MinValue;
            for (int d = Math.Max(0, at - shoulder); d < at; d++)
                if (profile[d] > inside) inside = profile[d];
            if (inside == double.MinValue || inside <= low) return at;

            double half = (low + inside) / 2;
            for (int d = at - 1; d >= Math.Max(0, at - shoulder); d--)
                if (profile[d] >= half) return d;
            return at;
        }

        static string Name(int side)
        {
            return side == 0 ? "left" : side == 1 ? "top" : side == 2 ? "right" : "bottom";
        }

        static double Dpi(double value)
        {
            return value >= 1 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 300.0;
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
    }
}
