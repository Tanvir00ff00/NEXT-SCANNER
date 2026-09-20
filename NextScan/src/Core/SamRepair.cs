using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>
    /// Uses the segmentation model's answers to repair the classical result.
    ///
    /// Additive on purpose
    /// -------------------
    /// The model is not in charge. It is asked what is on the glass, and its
    /// answer is used for the three things the classical detector has actually
    /// been getting wrong, and for nothing else:
    ///
    ///   an item that came back in pieces      -> the pieces become one region
    ///   an item that came back short          -> the region takes the fuller extent
    ///   an item that was never seen at all    -> a new region
    ///
    /// A region the model did not see is never removed, because the model has
    /// its own blind spots and a detector that deletes real items on a model's
    /// silence is worse than one that occasionally reports an extra. Nothing
    /// here places a border either: every region this touches is handed to
    /// <see cref="PlatenShadowEdges"/> afterwards, which measures the item's own
    /// shadow and puts the edge where it belongs. The model works on a
    /// 1024-pixel version of the capture and is good to a millimetre or so --
    /// enough to say "one card here, not three", not enough to cut by.
    /// </summary>
    internal static class SamRepair
    {
        /// <summary>
        /// How much of a classical region must lie inside a model region before
        /// they are talking about the same item.
        /// </summary>
        const double Claims = 0.6;

        /// <summary>
        /// How much longer a side must be, in millimetres, before the classical
        /// region counts as cut short rather than merely imprecise.
        ///
        /// A length, not a ratio. A ratio asks the wrong question: a card found
        /// at 81.5 mm instead of 85.6 is four millimetres short and unusable,
        /// yet only nine per cent small by area, so any ratio loose enough to
        /// catch it also swallows every region the classical engine had right.
        /// </summary>
        const double ShortByMm = 2.0;

        /// <summary>
        /// How much of a model region may lie inside an existing one before it
        /// is a detail of that item rather than an item. The model segments a
        /// photograph printed on a card as readily as the card, and a printed
        /// panel is not a thing to crop.
        /// </summary>
        const double PartOfSomethingElse = 0.7;

        /// <summary>
        /// A model region covering more of the bed than this is the glass, or
        /// the sheet everything is resting on, and not an item.
        /// </summary>
        const double TooMuchOfTheBed = 0.42;

        internal static void Apply(RawImage page, List<CropRegion> found,
                                   SamProposals.Result model, AutoCropOptions options,
                                   PlatenDetectionReport report)
        {
            if (page == null || found == null || model == null || model.Boxes.Count == 0) return;

            report.Add("model: " + model.Note + " in " + (model.EncodeMs + model.PromptMs) + " ms ("
                       + model.Prompts + " prompts)");

            double bedArea = (double)page.Width * page.Height;
            double perInchX = Dpi(page.XDpi), perInchY = Dpi(page.YDpi);

            // Largest first: a region that stands on its own gets to claim its
            // pieces before a smaller reading of the same item turns up.
            List<RotatedBox> boxes = new List<RotatedBox>(model.Boxes);
            boxes.Sort(delegate(RotatedBox a, RotatedBox b)
            { return ((double)b.Width * b.Height).CompareTo((double)a.Width * a.Height); });

            List<CropRegion> added = new List<CropRegion>();
            foreach (RotatedBox box in boxes)
            {
                if (box == null || !box.IsValid || box.Width < 2 || box.Height < 2) continue;

                double areaMm2 = (box.Width / perInchX * 25.4) * (box.Height / perInchY * 25.4);
                if (areaMm2 < options.MinAreaMm2) continue;
                if ((double)box.Width * box.Height > TooMuchOfTheBed * bedArea) continue;

                PointF[] quad = box.Corners;
                if (quad == null || quad.Length != 4) continue;

                List<int> claimed = new List<int>();
                for (int index = 0; index < found.Count; index++)
                {
                    CropRegion item = found[index];
                    if (item == null || item.Box == null || item.Box.Corners == null) continue;
                    double mine = PlatenGeometry.IntersectionArea(item.Box.Corners, quad);
                    double own = Math.Abs(Area(item.Box.Corners));
                    if (own > 1 && mine / own >= Claims) claimed.Add(index);
                }

                if (claimed.Count == 0)
                {
                    // Before calling this a new item, ask the other way round:
                    // how much of IT lies inside something already found. The
                    // first test only notices a model region that swallows an
                    // existing one, and says nothing about a small region deep
                    // inside a large one -- which is how a printed panel on a
                    // card and a photograph inside a passport were both added
                    // as items of their own.
                    double own = Math.Abs(Area(quad));
                    bool part = false;
                    foreach (CropRegion item in found)
                    {
                        if (item == null || item.Box == null || item.Box.Corners == null) continue;
                        if (own > 1 && PlatenGeometry.IntersectionArea(item.Box.Corners, quad) / own >= PartOfSomethingElse)
                        { part = true; break; }
                    }
                    if (part) continue;

                    // Nothing here at all. The passport page that reads the same
                    // as the glass it lies on arrives this way.
                    if (OverlapsAny(quad, added)) continue;
                    CropRegion fresh = Make(page, box, "found by the segmentation model");
                    added.Add(fresh);
                    report.Add("model added " + Describe(box, perInchX, perInchY) + ": nothing was found here");
                    continue;
                }

                if (claimed.Count > 1)
                {
                    // Several regions inside one item: the item arrived in
                    // pieces. Keep the first and let it take the whole extent.
                    CropRegion keeper = found[claimed[0]];
                    report.Add("model joined " + claimed.Count + " regions into "
                               + Describe(box, perInchX, perInchY) + ": one item in pieces");
                    Adopt(page, keeper, box);
                    keeper.Reason += "; pieces joined by the segmentation model";
                    for (int i = claimed.Count - 1; i >= 1; i--) found.RemoveAt(claimed[i]);
                    continue;
                }

                CropRegion single = found[claimed[0]];

                // Compare like with like: long side against long side. The two
                // may describe the same card with width and height the other way
                // round, and subtracting across that swap invents an error of
                // thirty millimetres where there is none.
                double hadLong = Math.Max(single.WidthInches, single.HeightInches) * 25.4;
                double hadShort = Math.Min(single.WidthInches, single.HeightInches) * 25.4;
                double nowLong = Math.Max(box.Width / perInchX, box.Height / perInchY) * 25.4;
                double nowShort = Math.Min(box.Width / perInchX, box.Height / perInchY) * 25.4;

                if (nowLong - hadLong >= ShortByMm || nowShort - hadShort >= ShortByMm)
                {
                    report.Add("model grew " + (single.WidthInches * 25.4).ToString("0.0") + " x "
                               + (single.HeightInches * 25.4).ToString("0.0") + " mm to "
                               + Describe(box, perInchX, perInchY) + ": the item was cut short");
                    Adopt(page, single, box);
                    single.Reason += "; extent corrected by the segmentation model";
                }
            }

            found.AddRange(added);
        }

        static bool OverlapsAny(PointF[] quad, List<CropRegion> regions)
        {
            double own = Math.Abs(Area(quad));
            foreach (CropRegion region in regions)
            {
                if (region.Box == null || region.Box.Corners == null) continue;
                if (own > 1 && PlatenGeometry.IntersectionArea(region.Box.Corners, quad) / own > 0.3) return true;
            }
            return false;
        }

        static CropRegion Make(RawImage page, RotatedBox box, string reason)
        {
            CropRegion region = new CropRegion
            {
                Confidence = CropConfidence.Good,
                Score = 0.5f,
                Reason = reason
            };
            Adopt(page, region, box);
            return region;
        }

        /// <summary>Puts a model box onto a region, in the form the rest of the engine builds.</summary>
        static void Adopt(RawImage page, CropRegion region, RotatedBox box)
        {
            PointF[] corners = box.Corners;
            float left = corners[0].X, right = corners[0].X, top = corners[0].Y, bottom = corners[0].Y;
            foreach (PointF corner in corners)
            {
                left = Math.Min(left, corner.X); right = Math.Max(right, corner.X);
                top = Math.Min(top, corner.Y); bottom = Math.Max(bottom, corner.Y);
            }
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(page.Width, right); bottom = Math.Min(page.Height, bottom);

            if (region.Box == null) region.Box = new RotatedBox();
            region.Box.IsValid = true;
            region.Box.Corners = corners;
            region.Box.Width = box.Width;
            region.Box.Height = box.Height;
            region.Box.Angle = box.Angle;
            region.Box.RawAngle = box.Angle;
            region.Box.Center = box.Center;
            region.Box.AABB = Rectangle.FromLTRB((int)Math.Floor(left), (int)Math.Floor(top),
                                                 (int)Math.Ceiling(right), (int)Math.Ceiling(bottom));
            region.SkewDegrees = box.Angle;
            region.Outline = null;
            region.NormRect = new RectangleF(left / page.Width, top / page.Height,
                                             (right - left) / page.Width, (bottom - top) / page.Height);
            region.WidthInches = box.Width / Dpi(page.XDpi);
            region.HeightInches = box.Height / Dpi(page.YDpi);
        }

        static string Describe(RotatedBox box, double perInchX, double perInchY)
        {
            return (box.Width / perInchX * 25.4).ToString("0.0") + " x "
                 + (box.Height / perInchY * 25.4).ToString("0.0") + " mm";
        }

        static double Area(PointF[] quad)
        {
            double total = 0;
            for (int i = 0; i < quad.Length; i++)
            {
                PointF a = quad[i], b = quad[(i + 1) % quad.Length];
                total += (double)a.X * b.Y - (double)b.X * a.Y;
            }
            return total / 2;
        }

        static double Dpi(double value)
        {
            return value >= 1 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 300.0;
        }
    }
}
