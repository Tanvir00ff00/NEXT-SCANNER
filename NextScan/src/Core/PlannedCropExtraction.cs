// =============================================================================
// NextScan Studio - extraction of preview-approved corners in bed inches
// Plan ref: MASTER_PLAN section 8.1/13; accuracy work order sections 1 and 5.3.
// An upright rectangle loses the angle the preview showed. Carrying the four
// approved corners into the scan permits straightening without fresh detection.
// =============================================================================
using System;
using System.Drawing;

namespace NextScan.Core
{
    internal static class PlannedCropExtraction
    {
        internal static RawImage Extract(RawImage page, PointF[] bedCorners, RectangleF requestedRegion, int index)
        {
            if (page == null || !page.IsValid || bedCorners == null || bedCorners.Length != 4 ||
                requestedRegion.Width <= 0 || requestedRegion.Height <= 0) return null;
            try
            {
                PointF[] corners = new PointF[4];
                double centreX = 0, centreY = 0;
                for (int corner = 0; corner < 4; corner++)
                {
                    corners[corner] = new PointF(
                        (bedCorners[corner].X - requestedRegion.X) * page.Width / requestedRegion.Width,
                        (bedCorners[corner].Y - requestedRegion.Y) * page.Height / requestedRegion.Height);
                    if (float.IsNaN(corners[corner].X) || float.IsInfinity(corners[corner].X) ||
                        float.IsNaN(corners[corner].Y) || float.IsInfinity(corners[corner].Y)) return null;
                    centreX += corners[corner].X / 4;
                    centreY += corners[corner].Y / 4;
                }

                // A corner outside the captured page used to abandon straightening
                // altogether, and the caller then delivered the item's upright
                // envelope - that is, the item still crooked, inside a box of
                // platen. Two real cases hit this constantly: anything tilted far
                // enough that its corners swing past the edge, and any document
                // large enough to reach the end of the scan.
                //
                // The part that WAS captured can be straightened perfectly well.
                // Only the part that was never scanned is unknowable, and the
                // sampler already fills that with the page's own background
                // rather than inventing detail. What is refused here is a quad
                // whose middle was never captured, because then there is no item
                // on this page to straighten.
                if (centreX < 0 || centreY < 0 || centreX > page.Width || centreY > page.Height) return null;

                // AutoCropEngine.Extract refuses a quad with a corner outside
                // the page, and that refusal is load-bearing for its own suite,
                // so a clipped item goes to the polygon path instead. That code
                // already clamps its output frame to what was captured and reads
                // background beyond it, which is exactly what is wanted here.
                bool clipped = false;
                for (int corner = 0; corner < 4; corner++)
                    if (corners[corner].X < 0 || corners[corner].Y < 0 ||
                        corners[corner].X > page.Width || corners[corner].Y > page.Height) clipped = true;

                if (clipped)
                    return PolygonCropExtraction.Extract(page, new PointF[][] { bedCorners },
                                                         bedCorners, requestedRegion, index, true);

                double horizontal = corners[1].X - corners[0].X;
                double vertical = corners[1].Y - corners[0].Y;
                float angle = (float)(Math.Atan2(vertical, horizontal) * 180 / Math.PI);

                // Fold into +/-45. Turning by the short edge's direction squares
                // the item just as well as turning by the long edge's - it only
                // exchanges width for height - but an unfolded angle is refused
                // outright downstream, and the item is then delivered crooked.
                // An item at 45 degrees produced an angle of -45.000002 from
                // Atan2 and lost its straightening to a rounding error.
                while (angle > 45f) angle -= 90f;
                while (angle <= -45f) angle += 90f;
                CropRegion region = new CropRegion
                {
                    Index = index, SkewDegrees = angle,
                    Box = new RotatedBox
                    {
                        IsValid = true, Corners = corners, Angle = angle,
                        Width = Length(corners[0], corners[1]), Height = Length(corners[0], corners[3]),
                        Center = new PointF((corners[0].X + corners[2].X) / 2, (corners[0].Y + corners[2].Y) / 2)
                    }
                };
                // Extract is a pixel transform only. In particular this must
                // never call Detect or DetectAndExtract at scan resolution.
                return AutoCropEngine.Extract(page, region, new AutoCropOptions { Deskew = true, MarginMm = 0 });
            }
            catch (Exception)
            {
                return null;
            }
        }

        static float Length(PointF first, PointF second)
        {
            double horizontal = second.X - first.X, vertical = second.Y - first.Y;
            return (float)Math.Sqrt(horizontal * horizontal + vertical * vertical);
        }
    }
}
