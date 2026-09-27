using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>
    /// Puts each side of a region that started from a vision model's box onto
    /// the item's own edge.
    ///
    /// Why the shadow pass is not enough here
    /// --------------------------------------
    /// <see cref="PlatenShadowEdges"/> only ever moves an edge outward, and never
    /// across a neighbour -- right for the engine's own regions, which err on
    /// the small side, and exactly wrong for a vision model's box, which is as
    /// often a millimetre too big (a card measured 3.40 in against its true
    /// 3.37) and whose most important side is the one between two cards laid
    /// edge to edge, where there is a neighbour by definition.
    ///
    /// What this does
    /// --------------
    /// Each side is slid along its own normal, a couple of millimetres either
    /// way, at full resolution, and placed where the brightness steps most
    /// sharply across it. Of the strong steps, the outermost wins -- the edge
    /// of the item is outside everything printed on it -- except on a side
    /// that faces another item, where the one nearest the model's seam wins,
    /// because outward from there is the neighbour's print. A side lying on
    /// the frame of the image cannot be measured and is left alone. The
    /// corners are rebuilt from the four lines, so a tilted item stays tilted.
    /// </summary>
    internal static class HintEdges
    {
        /// <summary>How far a side may move either way, in millimetres.</summary>
        const double ReachMm = 2.5;

        /// <summary>Steps at least this share of the strongest count as strong.</summary>
        const double Strong = 0.8;

        internal static bool Snap(RawImage page, CropRegion region, List<CropRegion> neighbours, PlatenDetectionReport report)
        {
            if (page == null || region == null || region.Box == null || region.Box.Corners == null || region.Box.Corners.Length != 4) return false;
            if (page.BitsPerChannel != 8 || (page.Channels != 1 && page.Channels != 3 && page.Channels != 4)) return false;

            double dpi = Math.Max(50, Math.Min(page.XDpi, page.YDpi));
            int reach = Math.Max(3, (int)Math.Round(ReachMm / 25.4 * dpi));
            double gap = Math.Max(1.0, dpi / 150.0);

            PointF[] c = region.Box.Corners;
            PointF centre = new PointF((c[0].X + c[1].X + c[2].X + c[3].X) / 4, (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4);
            double[] offset = new double[4];
            bool moved = false;
            var said = new List<string>();

            for (int side = 0; side < 4; side++)
            {
                PointF a = c[side], b = c[(side + 1) % 4];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
                if (length < 8) continue;

                // The outward normal: away from the centre.
                double nx = -dy / length, ny = dx / length;
                double mx = (a.X + b.X) / 2 - centre.X, my = (a.Y + b.Y) / 2 - centre.Y;
                if (nx * mx + ny * my < 0) { nx = -nx; ny = -ny; }

                if (OnFrame(page, a, b, 3)) continue;
                bool facing = Faces(region, neighbours, a, b, nx, ny, dpi);

                double[] score = new double[2 * reach + 1];
                double best = 0;
                for (int k = -reach; k <= reach; k++)
                {
                    double s = 0; int n = 0;
                    for (int i = 0; i < 48; i++)
                    {
                        double t = 0.12 + 0.76 * i / 47.0;
                        double px = a.X + dx * t + nx * k, py = a.Y + dy * t + ny * k;
                        double inside = Lum(page, px - nx * gap, py - ny * gap), outside = Lum(page, px + nx * gap, py + ny * gap);
                        if (inside < 0 || outside < 0) continue;
                        s += Math.Abs(inside - outside); n++;
                    }
                    score[k + reach] = n > 24 ? s / n : 0;
                    best = Math.Max(best, score[k + reach]);
                }
                if (best < 4) continue;       // no edge worth the name within reach: leave the side alone

                int chosen = 0;
                if (facing)
                {
                    int nearest = int.MaxValue;
                    for (int k = -reach; k <= reach; k++)
                        if (score[k + reach] >= Strong * best && Math.Abs(k) < Math.Abs(nearest)) nearest = k;
                    chosen = nearest == int.MaxValue ? 0 : nearest;
                }
                else
                {
                    for (int k = reach; k >= -reach; k--)
                        if (score[k + reach] >= Strong * best) { chosen = k; break; }
                }

                // A step is a band two pixels wide; the edge is its middle.
                offset[side] = chosen;
                if (chosen != 0) { moved = true; said.Add(new[] { "top", "right", "bottom", "left" }[side] + " " + (chosen / dpi * 25.4).ToString("+0.0;-0.0") + " mm"); }
            }
            if (!moved) return false;

            // The four moved lines, and their corners.
            PointF[] lineA = new PointF[4], lineB = new PointF[4];
            for (int side = 0; side < 4; side++)
            {
                PointF a = c[side], b = c[(side + 1) % 4];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
                double nx = length > 0 ? -dy / length : 0, ny = length > 0 ? dx / length : 0;
                double mx = (a.X + b.X) / 2 - centre.X, my = (a.Y + b.Y) / 2 - centre.Y;
                if (nx * mx + ny * my < 0) { nx = -nx; ny = -ny; }
                lineA[side] = new PointF((float)(a.X + nx * offset[side]), (float)(a.Y + ny * offset[side]));
                lineB[side] = new PointF((float)(b.X + nx * offset[side]), (float)(b.Y + ny * offset[side]));
            }
            PointF[] corners = new PointF[4];
            for (int i = 0; i < 4; i++)
            {
                int before = (i + 3) % 4;       // corner i is where side i-1 meets side i
                PointF? meet = Cross(lineA[before], lineB[before], lineA[i], lineB[i]);
                if (meet == null) return false;
                corners[i] = meet.Value;
            }

            var box = new RotatedBox
            {
                IsValid = true,
                Corners = corners,
                Width = (float)Distance(corners[0], corners[1]),
                Height = (float)Distance(corners[1], corners[2]),
                Angle = region.Box.Angle,
                Center = new PointF((corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4,
                                    (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4),
            };
            SamRepair.Adopt(page, region, box);
            region.Reason += "; sides placed on the page's edges (" + string.Join(", ", said.ToArray()) + ")";
            report.Add("edges snapped: " + string.Join(", ", said.ToArray()));
            return true;
        }

        /// <summary>Whether a side has another region just beyond it (touching items).</summary>
        static bool Faces(CropRegion region, List<CropRegion> neighbours, PointF a, PointF b, double nx, double ny, double dpi)
        {
            if (neighbours == null) return false;
            double step = 3.0 / 25.4 * dpi;
            PointF probe = new PointF((float)((a.X + b.X) / 2 + nx * step), (float)((a.Y + b.Y) / 2 + ny * step));
            foreach (CropRegion other in neighbours)
            {
                if (ReferenceEquals(other, region) || other.Box == null || other.Box.Corners == null) continue;
                if (Inside(other.Box.Corners, probe)) return true;
            }
            return false;
        }

        static bool Inside(PointF[] poly, PointF p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                    p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X) inside = !inside;
            return inside;
        }

        static bool OnFrame(RawImage page, PointF a, PointF b, float margin)
        {
            bool left = a.X < margin && b.X < margin, right = a.X > page.Width - margin && b.X > page.Width - margin;
            bool top = a.Y < margin && b.Y < margin, bottom = a.Y > page.Height - margin && b.Y > page.Height - margin;
            return left || right || top || bottom;
        }

        static PointF? Cross(PointF a1, PointF a2, PointF b1, PointF b2)
        {
            double d = (a1.X - a2.X) * (b1.Y - b2.Y) - (a1.Y - a2.Y) * (b1.X - b2.X);
            if (Math.Abs(d) < 1e-6) return null;
            double t = ((a1.X - b1.X) * (b1.Y - b2.Y) - (a1.Y - b1.Y) * (b1.X - b2.X)) / d;
            return new PointF((float)(a1.X + t * (a2.X - a1.X)), (float)(a1.Y + t * (a2.Y - a1.Y)));
        }

        static double Distance(PointF a, PointF b) { return Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)); }

        /// <summary>Luminance at a point, bilinear, or -1 off the page.</summary>
        static double Lum(RawImage page, double x, double y)
        {
            if (x < 0 || y < 0 || x > page.Width - 2 || y > page.Height - 2) return -1;
            int x0 = (int)x, y0 = (int)y;
            double fx = x - x0, fy = y - y0;
            double p00 = Px(page, x0, y0), p10 = Px(page, x0 + 1, y0), p01 = Px(page, x0, y0 + 1), p11 = Px(page, x0 + 1, y0 + 1);
            return (p00 * (1 - fx) + p10 * fx) * (1 - fy) + (p01 * (1 - fx) + p11 * fx) * fy;
        }

        static double Px(RawImage page, int x, int y)
        {
            long at = (long)y * page.Stride + (long)x * page.Channels;
            byte[] p = page.Pixels;
            if (page.Channels == 1) return p[at];
            return 0.299 * p[at] + 0.587 * p[at + 1] + 0.114 * p[at + 2];
        }
    }
}
