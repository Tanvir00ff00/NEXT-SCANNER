// =============================================================================
// NextScan Studio - Imaging pipeline core: document detection + curves
// Plan ref: MASTER_PLAN section 8.1/9, HANDOFF section 8.5.
//
// Ported from the proven implementation inside scanhelper.cs onto the RawImage
// model. Every algorithm and every evidence comment below came from there and
// was tuned against real previews (Canon LiDE 400 + a Canon network copier);
// do not "simplify" any of it - the comments record measured failures that the
// code exists to prevent.
//
// Bit-depth policy: detection works on an 8-bit luma/BGR reduction of the
// input. Detection accuracy does not improve with 16-bit data (its thresholds
// are noise-derived, not depth-derived), and keeping the 8-bit core makes this
// port faithful to the verified original. Curves, by contrast, are native
// 16-bit: a 65536-entry LUT is the whole point of moving onto RawImage,
// because System.Drawing.Bitmap capped everything at 8 bits per channel.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    /// <summary>A detected document: rotated rectangle plus its axis-aligned box.</summary>
    public class RotatedBox
    {
        public float Angle;          // degrees, normalised to [-45, 45]
        public float RawAngle;       // degrees, before any deskew guard touched it (plan 3.5 estimator input)
        public float Width, Height;  // along the box's own axes
        public PointF Center;
        public PointF[] Corners = new PointF[4];
        public Rectangle AABB;
        public bool IsValid;
        public float Score;
    }

    /// <summary>
    /// Deskew angle policy. StrictFlatbedGuard is the legacy scanhelper
    /// behaviour (accept [0.6 deg, 6.0 deg], anything outside forced to 0)
    /// kept verbatim as a preset while plan section 3.5's confidence-scored
    /// policy is not yet implemented.
    /// </summary>
    public enum DeskewGuard
    {
        StrictFlatbedGuard,
        Off
    }

    public static class DocumentDetector
    {
        public static Action<string> Log = delegate { };

        // ---------------------------------------------------------------- entry
        /// <summary>
        /// Returns every plausible document blob in the image, best first.
        /// Accepts RawImage of any depth (1/8/16-bit, gray or BGR).
        /// </summary>
        public static List<RotatedBox> Detect(RawImage img, DeskewGuard guard)
        {
            List<RotatedBox> results = new List<RotatedBox>();
            if (img == null || !img.IsValid) return results;
            int w = img.Width;
            int h = img.Height;
            if (w < 16 || h < 16) return results;

            byte[] gray, b, g, r;
            BuildPlanes8(img, out gray, out b, out g, out r);

            int bgR, bgG, bgB, bgNoise;
            EstimateBackground(w, h, b, g, r, out bgR, out bgG, out bgB, out bgNoise);

            // Adaptive thresholds: a noisy or textured lid raises the bar so dust and
            // glass smudges stop registering as document.
            int colThresh = Math.Max(18, bgNoise * 3);
            int edgeThresh = Math.Max(20, bgNoise * 3);

            // ---- Foreground energy: colour distance from lid OR local luma gradient ----
            byte[] fg = new byte[w * h];
            for (int y = 1; y < h - 1; y++)
            {
                int rowBase = y * w;
                for (int x = 1; x < w - 1; x++)
                {
                    int idx = rowBase + x;
                    int cdiff = Math.Abs(r[idx] - bgR) + Math.Abs(g[idx] - bgG) + Math.Abs(b[idx] - bgB);
                    if (cdiff >= colThresh) { fg[idx] = 1; continue; }
                    int gx = gray[idx + 1] - gray[idx - 1];
                    int gy = gray[idx + w] - gray[idx - w];
                    if (Math.Abs(gx) + Math.Abs(gy) >= edgeThresh) fg[idx] = 1;
                }
            }

            // ---- Adaptive border artifact guard ----
            // Flatbeds and network copiers render a dark calibration strip along the glass
            // edge. It is never document, but pinned at x=0/y=0 it dragged the convex hull
            // out to the whole frame. A FIXED guard is wrong in both directions: measured
            // on this copier the strip was exactly 7 rows deep and row 7 was already paper,
            // so a 1% guard (11px) discarded 4 rows of real document, while a smaller one
            // would have let the black band into the crop. So measure the strip instead.
            int bgLum = (bgR * 299 + bgG * 587 + bgB * 114) / 1000;
            int darkCut = bgLum - 40;
            int maxGuard = Math.Max(2, (int)(Math.Min(w, h) * 0.03));
            int gTop    = Math.Max(22, EdgeStripDepth(gray, w, h, 0, maxGuard, darkCut));
            int gBottom = Math.Max(15, EdgeStripDepth(gray, w, h, 1, maxGuard, darkCut));
            int gLeft   = Math.Max(22, EdgeStripDepth(gray, w, h, 2, maxGuard, darkCut));
            int gRight  = Math.Max(22, EdgeStripDepth(gray, w, h, 3, maxGuard, darkCut));

            for (int y = 0; y < h; y++)
            {
                int rb = y * w;
                if (y < gTop || y >= h - gBottom)
                {
                    for (int x = 0; x < w; x++) fg[rb + x] = 0;
                }
                else
                {
                    for (int x = 0; x < gLeft; x++) fg[rb + x] = 0;
                    for (int x = w - gRight; x < w; x++) fg[rb + x] = 0;
                }
            }

            // ---- Block density grid (ceiling division: the old floor dropped up to 7px
            // of the right/bottom edge entirely) ----
            int bSize = 8;
            int gw = (w + bSize - 1) / bSize;
            int gh = (h + bSize - 1) / bSize;
            byte[] grid = new byte[gw * gh];

            for (int gy2 = 0; gy2 < gh; gy2++)
            {
                int y0 = gy2 * bSize;
                int y1 = Math.Min(y0 + bSize, h);
                for (int gx = 0; gx < gw; gx++)
                {
                    int x0 = gx * bSize;
                    int x1 = Math.Min(x0 + bSize, w);
                    int cnt = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int rb = y * w;
                        for (int x = x0; x < x1; x++)
                            if (fg[rb + x] != 0) cnt++;
                    }
                    if (cnt >= 4) grid[gy2 * gw + gx] = 1;
                }
            }

            // ---- Connected components on the RAW grid ----
            // There is deliberately NO global morphological close here. A 3x3 close bridges
            // up to two blocks (16px), and on the LiDE preview that welded the ID card onto
            // an adjacent lid artifact: measured, the card's own blob was x=208..584
            // y=64..320, but after closing it became x=192..720 y=32..472 -- 30% oversized,
            // which is exactly the loose crop that reached Photoshop. Interior holes are
            // filled per blob instead (see FillBlobHoles), which cannot merge neighbours.
            bool[] visited = new bool[gw * gh];
            int minBlocks = Math.Max(6, (gw * gh) / 400);
            List<List<Point>> blobs = new List<List<Point>>();   // GRID coords per blob

            int[] qx = new int[gw * gh];
            int[] qy = new int[gw * gh];

            for (int gy2 = 0; gy2 < gh; gy2++)
            {
                for (int gx = 0; gx < gw; gx++)
                {
                    int seed = gy2 * gw + gx;
                    if (grid[seed] == 0 || visited[seed]) continue;

                    int head = 0, tail = 0;
                    qx[tail] = gx; qy[tail] = gy2; tail++;
                    visited[seed] = true;
                    List<Point> pts = new List<Point>();

                    while (head < tail)
                    {
                        int cx = qx[head];
                        int cy = qy[head];
                        head++;

                        pts.Add(new Point(cx, cy));   // grid coords; pixel corners derived later

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = cy + dy;
                            if (ny < 0 || ny >= gh) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = cx + dx;
                                if (nx < 0 || nx >= gw) continue;
                                int ni = ny * gw + nx;
                                if (grid[ni] == 0 || visited[ni]) continue;
                                visited[ni] = true;
                                qx[tail] = nx; qy[tail] = ny; tail++;
                            }
                        }
                    }

                    if (pts.Count >= minBlocks) blobs.Add(pts);
                }
            }

            if (blobs.Count == 0)
            {
                Log("detect: no blob above threshold");
                return results;
            }

            // ---- Score each blob for document-likeness, then take the best ----
            // Ranking by raw area is wrong. On this LiDE preview the artifacts were a
            // 317-block band down the left margin and a 162-block band across the top;
            // both are large, and neither is a document. What separates a real document is
            // that it FILLS its own bounding box. Measured: card 0.85, left band 0.28,
            // top band 0.53, right-hand lid artifact 0.30.
            double imgArea = (double)w * h;
            byte[] blobFg = new byte[w * h];   // scratch: one blob's foreground at a time
            foreach (List<Point> blob in blobs)
            {
                int bx0 = int.MaxValue, bx1 = int.MinValue, by0 = int.MaxValue, by1 = int.MinValue;
                foreach (Point c in blob)
                {
                    if (c.X < bx0) bx0 = c.X;
                    if (c.X > bx1) bx1 = c.X;
                    if (c.Y < by0) by0 = c.Y;
                    if (c.Y > by1) by1 = c.Y;
                }
                int bw = bx1 - bx0 + 1;
                int bh = by1 - by0 + 1;
                if (bw <= 0 || bh <= 0) continue;

                int solid = FillBlobHoles(blob, bx0, by0, bw, bh);
                double fillRatio = (double)solid / ((double)bw * bh);
                double aspect = (double)Math.Max(bw, bh) / Math.Max(1, Math.Min(bw, bh));
                if (fillRatio < 0.20) continue;   // allow white cards with thin outline/text to be detected cleanly
                if (aspect > 15.0) continue;      // a hairline streak is not a document

                // Convert grid cells to pixel corners now, so the hull spans each block's
                // true extent instead of sitting half a block inside the real paper edge.
                List<Point> pxPts = new List<Point>(blob.Count * 4);
                foreach (Point c in blob)
                {
                    int px0 = c.X * bSize;
                    int py0 = c.Y * bSize;
                    int px1 = Math.Min(px0 + bSize, w) - 1;
                    int py1 = Math.Min(py0 + bSize, h) - 1;
                    pxPts.Add(new Point(px0, py0));
                    pxPts.Add(new Point(px1, py0));
                    pxPts.Add(new Point(px1, py1));
                    pxPts.Add(new Point(px0, py1));
                }

                List<Point> hull = ComputeConvexHull(pxPts);
                RotatedBox box = FindMinimumAreaBoundingBox(hull, guard);
                if (!box.IsValid) continue;

                // Snap the coarse 8px-grid box onto the true paper boundary -- but profile
                // only THIS blob's own footprint. Refining against the global foreground let
                // a neighbouring lid artifact leak in: its columns carried about 39% of the
                // card's peak count, just over the 35% cut, which stretched the card's right
                // edge from 580 out to 624. The blob's block footprint always fully contains
                // the document, so clipping to it costs no precision.
                for (int ci = 0; ci < blob.Count; ci++)
                {
                    Point c = blob[ci];
                    int sx = c.X * bSize, sy = c.Y * bSize;
                    int ex = Math.Min(sx + bSize, w), ey = Math.Min(sy + bSize, h);
                    for (int yy = sy; yy < ey; yy++)
                    {
                        int rbb = yy * w;
                        for (int xx = sx; xx < ex; xx++) blobFg[rbb + xx] = fg[rbb + xx];
                    }
                }

                box = RefineBoxByProjection(blobFg, w, h, box);

                for (int ci = 0; ci < blob.Count; ci++)
                {
                    Point c = blob[ci];
                    int sx = c.X * bSize, sy = c.Y * bSize;
                    int ex = Math.Min(sx + bSize, w), ey = Math.Min(sy + bSize, h);
                    for (int yy = sy; yy < ey; yy++)
                    {
                        int rbb = yy * w;
                        for (int xx = sx; xx < ex; xx++) blobFg[rbb + xx] = 0;
                    }
                }

                Rectangle ab = box.AABB;
                int fx = Math.Max(0, ab.X);
                int fy = Math.Max(0, ab.Y);
                int fw = Math.Min(w - fx, ab.Width);
                int fh = Math.Min(h - fy, ab.Height);
                if (fw <= 0 || fh <= 0) continue;

                // No snap-to-glass-edge any more. That rule existed to paper over the old
                // engine's under-detection; now that the edges are measured properly it
                // only pulled the crop back over the dark calibration strip.
                box.AABB = new Rectangle(fx, fy, fw, fh);
                if ((double)fw * fh < imgArea * 0.015) continue;   // reject dust / smudges
                if (fw < w * 0.06 || fh < h * 0.03) continue;

                box.Score = (float)(solid * fillRatio * fillRatio);
                results.Add(box);
            }

            results.Sort(delegate(RotatedBox a, RotatedBox bb)
            {
                return bb.Score.CompareTo(a.Score);
            });

            if (results.Count > 0)
            {
                // Full-page document handling (A4, Birth Certificate, Legal Deed, Form, Porcha):
                // A full-sheet document must span BOTH >= 70% width AND >= 50% height of the bed.
                RotatedBox t = results[0];
                if ((t.Width >= w * 0.70f || t.AABB.Width >= (int)(w * 0.70)) && (t.Height >= h * 0.50f || t.AABB.Height >= (int)(h * 0.50)))
                {
                    int paperBottom = Math.Min(h, t.AABB.Bottom);
                    for (int y = Math.Min(h - 2, paperBottom + 30); y >= Math.Max(20, paperBottom - 20); y--)
                    {
                        int rowSum = 0;
                        int rb = y * w;
                        for (int x = (int)(w * 0.2); x < (int)(w * 0.8); x++) rowSum += fg[rb + x];
                        if (rowSum > 4) { paperBottom = y + 4; break; }
                    }
                    paperBottom = Math.Min(h, Math.Max(100, paperBottom));

                    t.Angle = 0f;
                    t.Width = w;
                    t.Height = paperBottom;
                    t.Center = new PointF(w / 2f, paperBottom / 2f);
                    t.AABB = new Rectangle(0, 0, w, paperBottom);
                    t.Corners[0] = new PointF(0, 0);
                    t.Corners[1] = new PointF(w, 0);
                    t.Corners[2] = new PointF(w, paperBottom);
                    t.Corners[3] = new PointF(0, paperBottom);
                    results[0] = t;
                }
            }

            return results;
        }

        // ---------------------------------------------------------------- planes
        /// <summary>
        /// 8-bit working planes from any RawImage layout. 16-bit input keeps its
        /// high byte: detection thresholds are noise-derived, and the reduction is
        /// exactly what the eye sees, which is what the thresholds were tuned on.
        /// </summary>
        static void BuildPlanes8(RawImage img, out byte[] gray, out byte[] b, out byte[] g, out byte[] r)
        {
            int w = img.Width, h = img.Height;
            gray = new byte[w * h];
            b = new byte[w * h];
            g = new byte[w * h];
            r = new byte[w * h];

            if (img.BitsPerChannel == 1)
            {
                // TWAIN bilevel is CHOCOLATE: a set bit is black.
                for (int y = 0; y < h; y++)
                {
                    int src = y * img.Stride;
                    int rowBase = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        byte v = (img.Pixels[src + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
                        int i = rowBase + x;
                        b[i] = v; g[i] = v; r[i] = v; gray[i] = v;
                    }
                }
                return;
            }

            int step = img.BitsPerChannel == 16 ? 2 : 1;
            int ch = img.Channels;
            for (int y = 0; y < h; y++)
            {
                int src = y * img.Stride;
                int rowBase = y * w;
                for (int x = 0; x < w; x++)
                {
                    int s = src + x * ch * step;
                    int i = rowBase + x;
                    if (ch == 3)
                    {
                        // RawImage 3-channel data is BGR.
                        if (step == 2)
                        {
                            b[i] = img.Pixels[s + 1]; g[i] = img.Pixels[s + 3]; r[i] = img.Pixels[s + 5];
                        }
                        else
                        {
                            b[i] = img.Pixels[s]; g[i] = img.Pixels[s + 1]; r[i] = img.Pixels[s + 2];
                        }
                        gray[i] = (byte)((r[i] * 299 + g[i] * 587 + b[i] * 114) / 1000);
                    }
                    else
                    {
                        byte v = step == 2 ? img.Pixels[s + 1] : img.Pixels[s];
                        b[i] = v; g[i] = v; r[i] = v; gray[i] = v;
                    }
                }
            }
        }

        // ---------------------------------------------------------------- background
        // ================= Robust Flatbed Background Estimation =================
        // The previous version averaged the bottom 10% band. Whenever the document reached
        // the bottom of the glass -- a full A4 page, or the 16.4in landscape deed -- that band
        // sampled the DOCUMENT rather than the lid, the colour difference term collapsed to
        // near zero, and detection silently degraded to edge-gradient only.
        //
        // Sampling a ring around all four margins and taking the per-channel MEDIAN (not the
        // mean) is robust: a document intruding into one or two margins cannot move the median
        // as long as most of the ring is still lid.
        static void EstimateBackground(int w, int h, byte[] b, byte[] g, byte[] r,
                                       out int bgR, out int bgG, out int bgB, out int bgNoise)
        {
            int ring = Math.Max(3, Math.Min(w, h) / 50);          // ~2% of the short side
            if (ring * 2 >= h) ring = Math.Max(1, h / 4);
            if (ring * 2 >= w) ring = Math.Max(1, w / 4);

            int[] hR = new int[256];
            int[] hG = new int[256];
            int[] hB = new int[256];
            long count = 0;

            int midH = h - 2 * ring;
            // (x, y, width, height) bands: top, bottom, left, right.
            int[,] bands = new int[,]
            {
                { 0,        0,        w,    ring },
                { 0,        h - ring, w,    ring },
                { 0,        ring,     ring, midH > 0 ? midH : 0 },
                { w - ring, ring,     ring, midH > 0 ? midH : 0 }
            };

            for (int bi = 0; bi < 4; bi++)
            {
                int bx = bands[bi, 0], by = bands[bi, 1];
                int bw2 = bands[bi, 2], bh2 = bands[bi, 3];
                if (bw2 <= 0 || bh2 <= 0) continue;
                int yEnd = by + bh2;
                int xEnd = bx + bw2;
                for (int y = by; y < yEnd; y++)
                {
                    int rowBase = y * w;
                    for (int x = bx; x < xEnd; x++)
                    {
                        int i = rowBase + x;
                        hB[b[i]]++; hG[g[i]]++; hR[r[i]]++;
                        count++;
                    }
                }
            }

            if (count == 0) { bgR = 255; bgG = 255; bgB = 255; bgNoise = 4; return; }

            bgR = HistMedian(hR, count);
            bgG = HistMedian(hG, count);
            bgB = HistMedian(hB, count);

            // MEDIAN absolute deviation, NOT mean.
            // Measured on a real Canon photocopier preview: the dark strip a flatbed always
            // produces along the glass edge dragged mean-abs-dev to 18 on a lid whose true
            // noise is 2. That tripled the foreground threshold to 54, and a cream stamp
            // paper whose colour distance from the lid is only 37 became INVISIBLE to the
            // detector -- which is why the box collapsed onto the printed ink and the hull
            // then stretched across the full width. The median ignores those outliers.
            int bgLuma = (bgR * 299 + bgG * 587 + bgB * 114) / 1000;
            int[] devHist = new int[256];
            long devN = 0;
            for (int v = 0; v < 256; v++)
            {
                int cnt = hR[v] + hG[v] + hB[v];
                if (cnt == 0) continue;
                int dv = Math.Abs(v - bgLuma);
                if (dv > 255) dv = 255;
                devHist[dv] += cnt;
                devN += cnt;
            }
            bgNoise = (devN > 0) ? HistMedian(devHist, (int)Math.Min(devN, (long)int.MaxValue)) : 4;
            if (bgNoise < 2) bgNoise = 2;
            if (bgNoise > 24) bgNoise = 24;
        }

        static int HistMedian(int[] hist, long total)
        {
            long half = total / 2;
            long run = 0;
            for (int v = 0; v < 256; v++)
            {
                run += hist[v];
                if (run >= half) return v;
            }
            return 255;
        }

        // ---------------------------------------------------------------- hull + min-area box
        // ================= Convex Hull (monotone chain) =================
        static List<Point> ComputeConvexHull(List<Point> points)
        {
            if (points.Count <= 3) return points;
            points.Sort(delegate(Point a, Point bb)
            {
                return a.X == bb.X ? a.Y.CompareTo(bb.Y) : a.X.CompareTo(bb.X);
            });

            List<Point> lower = new List<Point>();
            foreach (Point p in points)
            {
                while (lower.Count >= 2 && CrossProduct(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            List<Point> upper = new List<Point>();
            for (int i = points.Count - 1; i >= 0; i--)
            {
                Point p = points[i];
                while (upper.Count >= 2 && CrossProduct(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        static long CrossProduct(Point o, Point a, Point b)
        {
            return (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);
        }

        // ================= Exact Minimum-Area Enclosing Rectangle =================
        // The minimum-area enclosing rectangle of a convex polygon always has at least one
        // side collinear with a polygon edge. Testing only the hull-edge-aligned angles is
        // therefore EXACT -- and cheaper than a 720-step 0.25 deg sweep, which was both an
        // approximation (up to 0.125 deg of residual skew survived) and an approximation
        // besides. Corners is always populated, so callers can never hit a null-corner
        // failure when no swept angle satisfies a size gate.
        static RotatedBox FindMinimumAreaBoundingBox(List<Point> hull, DeskewGuard guard)
        {
            RotatedBox best = new RotatedBox();
            best.Corners = new PointF[4];
            best.IsValid = false;
            if (hull == null || hull.Count == 0) return best;

            // Axis-aligned result first, so we always have a valid fallback.
            int axMinX = int.MaxValue, axMaxX = int.MinValue;
            int axMinY = int.MaxValue, axMaxY = int.MinValue;
            foreach (Point p in hull)
            {
                if (p.X < axMinX) axMinX = p.X;
                if (p.X > axMaxX) axMaxX = p.X;
                if (p.Y < axMinY) axMinY = p.Y;
                if (p.Y > axMaxY) axMaxY = p.Y;
            }
            double minArea = (double)(axMaxX - axMinX) * (axMaxY - axMinY);
            best.Angle = 0f;
            best.Width = axMaxX - axMinX;
            best.Height = axMaxY - axMinY;
            best.Center = new PointF((axMinX + axMaxX) / 2f, (axMinY + axMaxY) / 2f);
            best.Corners[0] = new PointF(axMinX, axMinY);
            best.Corners[1] = new PointF(axMaxX, axMinY);
            best.Corners[2] = new PointF(axMaxX, axMaxY);
            best.Corners[3] = new PointF(axMinX, axMaxY);
            best.IsValid = true;

            int n = hull.Count;
            for (int i = 0; i < n; i++)
            {
                Point a = hull[i];
                Point bb = hull[(i + 1) % n];
                double dx = bb.X - a.X;
                double dy = bb.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;

                double cos = dx / len;
                double sin = dy / len;

                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;
                for (int k = 0; k < n; k++)
                {
                    Point pt = hull[k];
                    double u = pt.X * cos + pt.Y * sin;
                    double v = -pt.X * sin + pt.Y * cos;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }

                double cw = maxU - minU;
                double ch = maxV - minV;
                double area = cw * ch;
                if (area >= minArea || cw <= 0 || ch <= 0) continue;

                minArea = area;
                double centerU = (minU + maxU) / 2.0;
                double centerV = (minV + maxV) / 2.0;

                best.Center = new PointF((float)(centerU * cos - centerV * sin),
                                         (float)(centerU * sin + centerV * cos));
                best.Width = (float)cw;
                best.Height = (float)ch;
                best.Angle = (float)(Math.Atan2(dy, dx) * 180.0 / Math.PI);

                best.Corners[0] = new PointF((float)(minU * cos - minV * sin), (float)(minU * sin + minV * cos));
                best.Corners[1] = new PointF((float)(maxU * cos - minV * sin), (float)(maxU * sin + minV * cos));
                best.Corners[2] = new PointF((float)(maxU * cos - maxV * sin), (float)(maxU * sin + maxV * cos));
                best.Corners[3] = new PointF((float)(minU * cos - maxV * sin), (float)(minU * sin + maxV * cos));
            }

            // Normalise: snap to closest grid axis [-45 deg, 45 deg] to faithfully preserve
            // natural Portrait vs Landscape orientation.
            while (best.Angle > 45f)
            {
                best.Angle -= 90f;
                float tmp = best.Width;
                best.Width = best.Height;
                best.Height = tmp;
            }
            while (best.Angle < -45f)
            {
                best.Angle += 90f;
                float tmp = best.Width;
                best.Width = best.Height;
                best.Height = tmp;
            }
            best.RawAngle = best.Angle;

            // Strict Flatbed Angle Safety Guard (legacy preset, HANDOFF section 8.5):
            // On flatbed scanners, authentic human placement skew is always small (<= 6.0 deg).
            // Any angle > 6.0 deg or < -6.0 deg is an internal diagonal graphic, watermark, or
            // corner shadow artifact! Plan section 3.5 replaces this default with a
            // confidence-scored policy; until then this guard IS the default.
            if (guard == DeskewGuard.StrictFlatbedGuard)
            {
                if (Math.Abs(best.Angle) < 0.6f || Math.Abs(best.Angle) > 6.0f)
                {
                    best.Angle = 0f;
                }
            }

            float aMinX = float.MaxValue, aMaxX = float.MinValue;
            float aMinY = float.MaxValue, aMaxY = float.MinValue;
            foreach (PointF c in best.Corners)
            {
                if (c.X < aMinX) aMinX = c.X;
                if (c.X > aMaxX) aMaxX = c.X;
                if (c.Y < aMinY) aMinY = c.Y;
                if (c.Y > aMaxY) aMaxY = c.Y;
            }
            best.AABB = new Rectangle((int)Math.Floor(aMinX), (int)Math.Floor(aMinY),
                                      (int)Math.Ceiling(aMaxX - aMinX), (int)Math.Ceiling(aMaxY - aMinY));
            return best;
        }

        // ---------------------------------------------------------------- projection refine
        // ================= Sub-Block Edge Refinement (Projection Profile) =================
        // The coarse box comes from an 8px block grid, so every edge sits up to a block away
        // from the real paper boundary -- ~48px of background sliver once scaled to 600 DPI.
        //
        // This projects the foreground mask onto the box's OWN two axes and finds where each
        // 1-D profile collapses. A genuine paper edge is where a whole row/column stops being
        // foreground at once, so the profile has a sharp cliff there; stray ink, dust and
        // speckle never accumulate enough count to move it. Works for rotated boxes because
        // the projection axes rotate with the box.
        //
        // Validated against the Canon photocopier preview: coarse (0,0)-(1663,839) refined to
        // x=324..1651 y=9..834, versus a ground truth of x=328..1652 y=11..833.
        static RotatedBox RefineBoxByProjection(byte[] fg, int w, int h, RotatedBox box)
        {
            try
            {
                double rad = box.Angle * Math.PI / 180.0;
                double cos = Math.Cos(rad);
                double sin = Math.Sin(rad);

                // Search 20% wider than the coarse box so an edge can GROW outward as well
                // as shrink inward.
                double halfU = box.Width * 0.60;
                double halfV = box.Height * 0.60;
                int nU = (int)(halfU * 2) + 1;
                int nV = (int)(halfV * 2) + 1;
                if (nU < 8 || nV < 8 || nU > 30000 || nV > 30000) return box;

                int[] uHist = new int[nU];
                int[] vHist = new int[nV];

                for (int y = 0; y < h; y++)
                {
                    int rb = y * w;
                    double dyc = y - box.Center.Y;
                    for (int x = 0; x < w; x++)
                    {
                        if (fg[rb + x] == 0) continue;
                        double dxc = x - box.Center.X;
                        double u = dxc * cos + dyc * sin;
                        if (u < -halfU || u >= halfU) continue;
                        double v = -dxc * sin + dyc * cos;
                        if (v < -halfV || v >= halfV) continue;
                        uHist[(int)(u + halfU)]++;
                        vHist[(int)(v + halfV)]++;
                    }
                }

                int u0, u1, v0, v1;
                if (!ProfileExtent(uHist, out u0, out u1)) return box;
                if (!ProfileExtent(vHist, out v0, out v1)) return box;

                double du0 = u0 - halfU, du1 = u1 - halfU;
                double dv0 = v0 - halfV, dv1 = v1 - halfV;
                double newW = du1 - du0 + 1;
                double newH = dv1 - dv0 + 1;

                // Refuse implausible collapses -- if the profile disagrees violently with the
                // blob, trust the blob rather than emit a box around a single ink stripe.
                if (newW < box.Width * 0.25 || newH < box.Height * 0.25) return box;

                double mu = (du0 + du1) / 2.0;
                double mv = (dv0 + dv1) / 2.0;

                RotatedBox r = new RotatedBox();
                r.Angle = box.Angle;
                r.Width = (float)newW;
                r.Height = (float)newH;
                r.Center = new PointF((float)(box.Center.X + mu * cos - mv * sin),
                                      (float)(box.Center.Y + mu * sin + mv * cos));
                r.IsValid = true;
                r.Corners = new PointF[4];
                double hw = newW / 2.0, hh = newH / 2.0;
                double[,] off = new double[,] { { -hw, -hh }, { hw, -hh }, { hw, hh }, { -hw, hh } };
                for (int i = 0; i < 4; i++)
                {
                    r.Corners[i] = new PointF(
                        (float)(r.Center.X + off[i, 0] * cos - off[i, 1] * sin),
                        (float)(r.Center.Y + off[i, 0] * sin + off[i, 1] * cos));
                }

                float mnX = float.MaxValue, mxX = float.MinValue;
                float mnY = float.MaxValue, mxY = float.MinValue;
                foreach (PointF c in r.Corners)
                {
                    if (c.X < mnX) mnX = c.X;
                    if (c.X > mxX) mxX = c.X;
                    if (c.Y < mnY) mnY = c.Y;
                    if (c.Y > mxY) mxY = c.Y;
                }

                // If the document is flush against the top glass ruler (within 30px on preview):
                // snap top edge to Y = 0 so no top headers or barcodes are clipped.
                if (mnY <= 30f)
                {
                    float dy2 = mnY;
                    mnY = 0f;
                    r.Height += dy2;
                    r.Center = new PointF(r.Center.X, r.Center.Y - dy2 / 2f);
                }
                if (mnX <= 30f)
                {
                    float dx2 = mnX;
                    mnX = 0f;
                    r.Width += dx2;
                    r.Center = new PointF(r.Center.X - dx2 / 2f, r.Center.Y);
                }

                r.AABB = new Rectangle((int)Math.Floor(mnX), (int)Math.Floor(mnY),
                                       (int)Math.Ceiling(mxX - mnX), (int)Math.Ceiling(mxY - mnY));
                return r;
            }
            catch { return box; }
        }

        // Extent of a 1-D profile: everything at or above 4% of the profile's robust
        // (90th-percentile) peak. Using a percentile rather than the raw max stops one
        // dense ink row from setting the bar for the whole sheet.
        static bool ProfileExtent(int[] hist, out int first, out int last)
        {
            first = 0; last = 0;
            int[] sorted = new int[hist.Length];
            int nz = 0;
            for (int i = 0; i < hist.Length; i++)
                if (hist[i] > 0) sorted[nz++] = hist[i];
            if (nz == 0) return false;

            Array.Sort(sorted, 0, nz);
            int peak = sorted[(int)(nz * 0.90)];
            if (peak < 1) peak = sorted[nz - 1];
            // Optimal threshold (4% of peak) preserves unprinted paper margins and headers
            // while cleanly dropping empty glass lid below the paper.
            int thr = Math.Max(1, (int)(peak * 0.04));

            int f = -1, l = -1;
            for (int i = 0; i < hist.Length; i++)
            {
                if (hist[i] < thr) continue;
                if (f < 0) f = i;
                l = i;
            }
            if (f < 0) return false;
            first = f; last = l;
            return true;
        }

        // How many rows/columns deep the dark calibration strip runs on one edge.
        // side: 0=top 1=bottom 2=left 3=right. Sampled across the middle 60% so a dark
        // document sitting in one corner cannot be mistaken for the strip.
        static int EdgeStripDepth(byte[] gray, int w, int h, int side, int maxDepth, int darkCut)
        {
            int depth = 0;
            for (int i = 0; i < maxDepth; i++)
            {
                long sum = 0;
                int n = 0;
                if (side == 0 || side == 1)
                {
                    int y = (side == 0) ? i : h - 1 - i;
                    if (y < 0 || y >= h) break;
                    int x0 = w / 5, x1 = w - w / 5;
                    for (int x = x0; x < x1; x++) { sum += gray[y * w + x]; n++; }
                }
                else
                {
                    int x = (side == 2) ? i : w - 1 - i;
                    if (x < 0 || x >= w) break;
                    int y0 = h / 5, y1 = h - h / 5;
                    for (int y = y0; y < y1; y++) { sum += gray[y * w + x]; n++; }
                }
                if (n == 0) break;
                if ((int)(sum / n) < darkCut) depth = i + 1;
                else break;
            }
            return depth;
        }

        // Grid cells the blob covers once its interior holes are filled.
        //
        // Needed because fill ratio alone would punish a perfectly good document. A white card
        // on a white lid registers only as an OUTLINE -- a ring -- whose raw fill ratio is low
        // even though it is exactly the thing we want. Filling holes first makes the ratio
        // measure "is this a solid rectangular object" instead of "did the interior happen to
        // contrast with the lid". Scoped to one blob's own bounding box, so unlike a global
        // morphological close it can never merge two neighbouring objects.
        static int FillBlobHoles(List<Point> cells, int bx0, int by0, int bw, int bh)
        {
            byte[] loc = new byte[bw * bh];
            foreach (Point c in cells)
            {
                int lx = c.X - bx0, ly = c.Y - by0;
                if (lx < 0 || lx >= bw || ly < 0 || ly >= bh) continue;
                loc[ly * bw + lx] = 1;
            }

            // Flood the outside inward from the border; whatever stays unreached is a hole.
            int[] stack = new int[bw * bh];
            int sp = 0;
            for (int x = 0; x < bw; x++)
            {
                int iTop = x, iBot = (bh - 1) * bw + x;
                if (loc[iTop] == 0) { loc[iTop] = 2; stack[sp++] = iTop; }
                if (loc[iBot] == 0) { loc[iBot] = 2; stack[sp++] = iBot; }
            }
            for (int y = 0; y < bh; y++)
            {
                int iL = y * bw, iR = y * bw + bw - 1;
                if (loc[iL] == 0) { loc[iL] = 2; stack[sp++] = iL; }
                if (loc[iR] == 0) { loc[iR] = 2; stack[sp++] = iR; }
            }
            while (sp > 0)
            {
                int i = stack[--sp];
                int cx = i % bw, cy = i / bw;
                if (cx > 0 && loc[i - 1] == 0) { loc[i - 1] = 2; stack[sp++] = i - 1; }
                if (cx < bw - 1 && loc[i + 1] == 0) { loc[i + 1] = 2; stack[sp++] = i + 1; }
                if (cy > 0 && loc[i - bw] == 0) { loc[i - bw] = 2; stack[sp++] = i - bw; }
                if (cy < bh - 1 && loc[i + bw] == 0) { loc[i + bw] = 2; stack[sp++] = i + bw; }
            }

            int count = 0;
            for (int i = 0; i < loc.Length; i++) if (loc[i] != 2) count++;
            return count;
        }
    }

    // =========================================================================
    // Curves: monotone cubic (Fritsch-Carlson) spline LUTs, native 16-bit.
    // Same spline the scanhelper curve editor used at 8 bits, extended to the
    // 65536-entry domain so 48-bit scans can be graded without losing depth.
    // =========================================================================
    public static class Curves
    {
        /// <summary>Builds a 65536-entry LUT from control points in [0,65535] space.</summary>
        public static ushort[] BuildLut16(List<PointF> pts)
        {
            double[] v = BuildSplineLut(pts, 65535);
            ushort[] lut = new ushort[65536];
            for (int i = 0; i < 65536; i++) lut[i] = (ushort)Math.Max(0, Math.Min(65535, (int)Math.Round(v[i])));
            return lut;
        }

        /// <summary>Builds a 256-entry LUT from control points in [0,255] space.</summary>
        public static byte[] BuildLut8(List<PointF> pts)
        {
            double[] v = BuildSplineLut(pts, 255);
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v[i])));
            return lut;
        }

        static double[] BuildSplineLut(List<PointF> pts, int maxVal)
        {
            double[] lut = new double[maxVal + 1];
            if (pts == null || pts.Count == 0)
            {
                for (int i = 0; i <= maxVal; i++) lut[i] = i;
                return lut;
            }

            List<PointF> sorted = new List<PointF>(pts);
            sorted.Sort(delegate(PointF a, PointF b) { return a.X.CompareTo(b.X); });

            if (sorted[0].X > 0) sorted.Insert(0, new PointF(0, sorted[0].Y));
            if (sorted[sorted.Count - 1].X < maxVal) sorted.Add(new PointF(maxVal, sorted[sorted.Count - 1].Y));

            int n = sorted.Count;
            double[] x = new double[n];
            double[] y = new double[n];
            for (int i = 0; i < n; i++)
            {
                x[i] = sorted[i].X;
                y[i] = sorted[i].Y;
            }

            double[] dx = new double[n - 1];
            double[] m = new double[n - 1];
            for (int i = 0; i < n - 1; i++)
            {
                dx[i] = Math.Max(0.001, x[i + 1] - x[i]);
                m[i] = (y[i + 1] - y[i]) / dx[i];
            }

            // Fritsch-Carlson: zero the derivative at local extrema, average elsewhere,
            // then clamp so no segment overshoots (the h>3 circle test). A plain cubic
            // here rings around control points and inverts tone locally, which shows up
            // as posterised skies on photos.
            double[] d = new double[n];
            d[0] = m[0];
            d[n - 1] = m[n - 2];
            for (int i = 1; i < n - 1; i++)
            {
                if (m[i - 1] * m[i] <= 0.0) d[i] = 0.0;
                else d[i] = (m[i - 1] + m[i]) / 2.0;
            }

            for (int i = 0; i < n - 1; i++)
            {
                if (Math.Abs(m[i]) < 0.0001)
                {
                    d[i] = 0.0;
                    d[i + 1] = 0.0;
                }
                else
                {
                    double a = d[i] / m[i];
                    double b = d[i + 1] / m[i];
                    double hh = Math.Sqrt(a * a + b * b);
                    if (hh > 3.0)
                    {
                        double t = 3.0 / hh;
                        d[i] = t * a * m[i];
                        d[i + 1] = t * b * m[i];
                    }
                }
            }

            int seg = 0;
            for (int i = 0; i <= maxVal; i++)
            {
                while (seg < n - 2 && i > x[seg + 1]) seg++;
                double hh = dx[seg];
                double t = (i - x[seg]) / hh;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double t2 = t * t;
                double h00 = (1 + 2 * t) * (1 - t) * (1 - t);
                double h10 = t * (1 - t) * (1 - t);
                double h01 = t2 * (3 - 2 * t);
                double h11 = t2 * (t - 1);
                lut[i] = h00 * y[seg] + h10 * hh * d[seg] + h01 * y[seg + 1] + h11 * hh * d[seg + 1];
            }
            return lut;
        }

        /// <summary>
        /// Applies a 16-bit LUT in place. Only valid for 16-bit-per-channel images;
        /// 8-bit data must use an 8-bit LUT so no precision is silently lost.
        /// </summary>
        public static void Apply16(RawImage img, ushort[] lut)
        {
            if (img == null || img.Pixels == null || lut == null || lut.Length != 65536) return;
            if (img.BitsPerChannel != 16) throw new InvalidOperationException("Apply16 needs a 16-bit-per-channel image");

            int n = img.Stride * img.Height / 2;
            ushort[] px = new ushort[n];
            Buffer.BlockCopy(img.Pixels, 0, px, 0, n * 2);
            for (int i = 0; i < n; i++) px[i] = lut[px[i]];
            Buffer.BlockCopy(px, 0, img.Pixels, 0, n * 2);
        }

        /// <summary>Applies an 8-bit LUT in place to every channel of an 8-bit image.</summary>
        public static void Apply8(RawImage img, byte[] lut)
        {
            if (img == null || img.Pixels == null || lut == null || lut.Length != 256) return;
            if (img.BitsPerChannel != 8) throw new InvalidOperationException("Apply8 needs an 8-bit-per-channel image");

            long n = (long)img.Stride * img.Height;
            for (long i = 0; i < n; i++) img.Pixels[i] = lut[img.Pixels[i]];
        }
    }
}
