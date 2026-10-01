// =============================================================================
// NextScan Studio - looking closely at a scanned page
// Plan ref: docs/AI_LAYER.md, docs/DOCUMENT_WORKSPACE.md
//
// To rebuild a scanned form in Word the assistant needs three things a plain
// picture does not give it:
//
//   measure   a millimetre grid over the page and close-ups at full scan
//             resolution, so "the logo is 40 mm from the left" is read off the
//             page rather than guessed from a picture shrunk to 1568 px;
//   find      where the pictures are -- a logo, an emblem, a stamp, a photograph
//             -- as opposed to printed words, which are text to be typed. Models
//             are poor at the coordinates of things in a picture, so the
//             application finds candidates itself (ink that is coloured, or solid,
//             in a block too big to be a letter) and the model confirms them;
//   take      a picture out of the scan as a file (trimmed to its edges, white
//             made see-through if asked), to be placed in the Word document.
//
// Everything here works on the page as scanned, in millimetres from its top-left
// corner, whatever the scan's resolution.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using NextScan.Core;

namespace NextScan.App
{
    /// <summary>A place on a scanned page that looks like a picture, not printed words.</summary>
    public class ScanPicture
    {
        public int Number;
        /// <summary>Millimetres from the page's top-left corner.</summary>
        public double X, Y, Width, Height;
        /// <summary>colour (a logo or a stamp in ink of some colour) or dark (solid black or grey artwork).</summary>
        public string Kind = "dark";
        public double Fill;      // how much of its box is ink, 0..1
    }

    public static class ScanSight
    {
        public static double PxPerMm(RawImage page) { return (page.XDpi > 1 ? page.XDpi : 300) / 25.4; }
        public static double PageWidthMm(RawImage page) { return page.Width / PxPerMm(page); }
        public static double PageHeightMm(RawImage page) { return page.Height / (page.YDpi > 1 ? page.YDpi / 25.4 : PxPerMm(page)); }

        /// <summary>
        /// The page, or a rectangle of it in millimetres, as a bitmap no longer than
        /// <paramref name="longest"/> pixels, with an optional millimetre grid. Close-ups are cut
        /// from the scan itself, so they keep all its detail.
        /// </summary>
        public static Bitmap View(RawImage page, double[] region, bool grid, int longest, out double pxPerMm)
        {
            using (Bitmap full = page.ToBitmap())
            {
                double sx = PxPerMm(page), sy = page.YDpi > 1 ? page.YDpi / 25.4 : sx;
                Rectangle from = new Rectangle(0, 0, full.Width, full.Height);
                double offX = 0, offY = 0;
                if (region != null)
                {
                    int x = Math.Max(0, (int)Math.Round(region[0] * sx)), y = Math.Max(0, (int)Math.Round(region[1] * sy));
                    int w = Math.Min(full.Width - x, (int)Math.Round(region[2] * sx)), h = Math.Min(full.Height - y, (int)Math.Round(region[3] * sy));
                    if (w < 8 || h < 8) throw new ArgumentException("That region is outside the page (the page is " + Math.Round(PageWidthMm(page), 1) + " x " + Math.Round(PageHeightMm(page), 1) + " mm).");
                    from = new Rectangle(x, y, w, h);
                    offX = x / sx; offY = y / sy;
                }
                double shrink = Math.Min(1.0, (double)longest / Math.Max(from.Width, from.Height));
                int tw = Math.Max(1, (int)Math.Round(from.Width * shrink)), th = Math.Max(1, (int)Math.Round(from.Height * shrink));
                var view = new Bitmap(tw, th, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(view))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(full, new Rectangle(0, 0, tw, th), from, GraphicsUnit.Pixel);
                }
                pxPerMm = sx * shrink;
                if (grid) DrawGrid(view, pxPerMm, offX, offY);
                return view;
            }
        }

        /// <summary>A line every 10 mm, heavier and numbered every 50, in the page's own millimetres (so a close-up's numbers are still page positions).</summary>
        static void DrawGrid(Bitmap view, double pxPerMm, double offX, double offY)
        {
            using (Graphics g = Graphics.FromImage(view))
            using (var thin = new Pen(Color.FromArgb(70, 30, 120, 255), 1f))
            using (var heavy = new Pen(Color.FromArgb(140, 30, 120, 255), 1f))
            using (var font = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
            using (var ink = new SolidBrush(Color.FromArgb(230, 20, 80, 200)))
            using (var back = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int step = pxPerMm * 10 < 14 ? 50 : 10;
                for (int mm = (int)Math.Ceiling(offX / step) * step; (mm - offX) * pxPerMm < view.Width; mm += step)
                {
                    float x = (float)((mm - offX) * pxPerMm);
                    bool big = mm % 50 == 0;
                    g.DrawLine(big ? heavy : thin, x, 0, x, view.Height);
                    if (big || step == 50 || (view.Width / pxPerMm) < 120) { g.FillRectangle(back, x + 1, 1, 28, 14); g.DrawString(mm.ToString(CultureInfo.InvariantCulture), font, ink, x + 2, 1); }
                }
                for (int mm = (int)Math.Ceiling(offY / step) * step; (mm - offY) * pxPerMm < view.Height; mm += step)
                {
                    float y = (float)((mm - offY) * pxPerMm);
                    bool big = mm % 50 == 0;
                    g.DrawLine(big ? heavy : thin, 0, y, view.Width, y);
                    if (big || step == 50 || (view.Height / pxPerMm) < 120) { g.FillRectangle(back, 1, y + 1, 28, 14); g.DrawString(mm.ToString(CultureInfo.InvariantCulture), font, ink, 2, y + 1); }
                }
            }
        }

        // =====================================================================
        // Finding the pictures
        // =====================================================================

        /// <summary>
        /// Blocks of the page that are artwork rather than words: ink that is solid or coloured in a
        /// box too big to be a letter and too filled to be a table's lines. Not exact (a heavy heading
        /// can pass for artwork, a faint stamp can be missed): the model looks at what is found and
        /// confirms or corrects it.
        /// </summary>
        public static List<ScanPicture> FindPictures(RawImage page)
        {
            double sx = PxPerMm(page), sy = page.YDpi > 1 ? page.YDpi / 25.4 : sx;
            double analysisDpi = 100, shrink = Math.Min(1.0, analysisDpi / (sx * 25.4));
            int w = Math.Max(8, (int)Math.Round(page.Width * shrink)), h = Math.Max(8, (int)Math.Round(page.Height * shrink));
            double pxMmX = sx * shrink, pxMmY = sy * shrink;

            bool[] ink = new bool[w * h], colour = new bool[w * h];
            using (Bitmap full = page.ToBitmap())
            using (var small = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(full, new Rectangle(0, 0, w, h));
                }
                BitmapData data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[Math.Abs(data.Stride)];
                    for (int y = 0; y < h; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, w * 3);
                        for (int x = 0; x < w; x++)
                        {
                            int b = row[x * 3], gr = row[x * 3 + 1], r = row[x * 3 + 2];
                            int max = Math.Max(r, Math.Max(gr, b)), min = Math.Min(r, Math.Min(gr, b));
                            double lum = 0.299 * r + 0.587 * gr + 0.114 * b;
                            double sat = max == 0 ? 0 : (double)(max - min) / max;
                            bool coloured = sat > 0.28 && lum < 235;
                            if (lum < 170 || coloured) { ink[y * w + x] = true; colour[y * w + x] = coloured; }
                        }
                    }
                }
                finally { small.UnlockBits(data); }
            }

            // Close the gaps: ink within ~2 mm of ink is one blob.
            int radius = Math.Max(2, (int)Math.Round(0.9 * pxMmX));
            bool[] closed = Dilate(ink, w, h, radius);

            var label = new int[w * h];
            var found = new List<ScanPicture>();
            int next = 0;
            var stack = new Stack<int>();
            for (int start = 0; start < closed.Length; start++)
            {
                if (!closed[start] || label[start] != 0) continue;
                next++;
                int minX = w, minY = h, maxX = 0, maxY = 0, inkCount = 0, colourCount = 0;
                stack.Push(start); label[start] = next;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    int px = p % w, py = p / w;
                    if (px < minX) minX = px; if (px > maxX) maxX = px; if (py < minY) minY = py; if (py > maxY) maxY = py;
                    if (ink[p]) { inkCount++; if (colour[p]) colourCount++; }
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx, ny = py + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int n = ny * w + nx;
                            if (closed[n] && label[n] == 0) { label[n] = next; stack.Push(n); }
                        }
                }
                double widthMm = (maxX - minX + 1) / pxMmX, heightMm = (maxY - minY + 1) / pxMmY;
                if (widthMm < 7 || heightMm < 7) continue;                     // a letter or a dot
                if (widthMm > PageWidthMm(page) * 0.9 && heightMm > PageHeightMm(page) * 0.9) continue;   // the page's edge or a scanner shadow
                double fill = (double)inkCount / ((maxX - minX + 1) * (double)(maxY - minY + 1));
                double colourShare = inkCount == 0 ? 0 : (double)colourCount / inkCount;
                bool picture = colourShare > 0.35 && inkCount > 40 || fill > 0.45;
                if (!picture) continue;
                // A long thin dark block is a rule or a heavy line of type, not artwork.
                double aspect = widthMm / heightMm;
                if (colourShare <= 0.35 && (aspect > 6 || aspect < 1 / 6.0)) continue;
                found.Add(new ScanPicture
                {
                    X = Math.Max(0, minX / pxMmX - 0.5), Y = Math.Max(0, minY / pxMmY - 0.5),
                    Width = widthMm + 1, Height = heightMm + 1,
                    Kind = colourShare > 0.35 ? "colour" : "dark", Fill = fill,
                });
            }
            found.Sort((a, b) => (b.Width * b.Height).CompareTo(a.Width * a.Height));
            if (found.Count > 10) found.RemoveRange(10, found.Count - 10);
            for (int i = 0; i < found.Count; i++)
            {
                found[i].Number = i + 1;
                found[i].X = Math.Round(found[i].X, 1); found[i].Y = Math.Round(found[i].Y, 1);
                found[i].Width = Math.Round(found[i].Width, 1); found[i].Height = Math.Round(found[i].Height, 1);
                found[i].Fill = Math.Round(found[i].Fill, 2);
            }
            return found;
        }

        /// <summary>Everything within <paramref name="radius"/> pixels of a set pixel is set (a box, done as two passes).</summary>
        static bool[] Dilate(bool[] src, int w, int h, int radius)
        {
            var horizontal = new bool[src.Length];
            for (int y = 0; y < h; y++)
            {
                int last = -1000000;
                for (int x = 0; x < w; x++) { if (src[y * w + x]) last = x; if (x - last <= radius) horizontal[y * w + x] = true; }
                last = 1000000;
                for (int x = w - 1; x >= 0; x--) { if (src[y * w + x]) last = x; if (last - x <= radius) horizontal[y * w + x] = true; }
            }
            var result = new bool[src.Length];
            for (int x = 0; x < w; x++)
            {
                int last = -1000000;
                for (int y = 0; y < h; y++) { if (horizontal[y * w + x]) last = y; if (y - last <= radius) result[y * w + x] = true; }
                last = 1000000;
                for (int y = h - 1; y >= 0; y--) { if (horizontal[y * w + x]) last = y; if (last - y <= radius) result[y * w + x] = true; }
            }
            return result;
        }

        /// <summary>The page with each found picture boxed and numbered, to show the model what was found.</summary>
        public static Bitmap Outline(RawImage page, List<ScanPicture> pictures, int longest, out double pxPerMm)
        {
            Bitmap view = View(page, null, false, longest, out pxPerMm);
            using (Graphics g = Graphics.FromImage(view))
            using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                foreach (ScanPicture p in pictures)
                {
                    Color c = p.Kind == "colour" ? Color.FromArgb(230, 220, 30, 60) : Color.FromArgb(230, 20, 120, 220);
                    using (var pen = new Pen(c, 2.5f))
                    using (var brush = new SolidBrush(c))
                    {
                        var r = new RectangleF((float)(p.X * pxPerMm), (float)(p.Y * pxPerMm), (float)(p.Width * pxPerMm), (float)(p.Height * pxPerMm));
                        g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                        string label = p.Number.ToString(CultureInfo.InvariantCulture);
                        SizeF size = g.MeasureString(label, font);
                        g.FillRectangle(brush, r.X, Math.Max(0, r.Y - size.Height), size.Width + 4, size.Height);
                        g.DrawString(label, font, Brushes.White, r.X + 2, Math.Max(0, r.Y - size.Height));
                    }
                }
            }
            return view;
        }

        // =====================================================================
        // Taking a picture out
        // =====================================================================

        /// <summary>
        /// A rectangle of the page, in millimetres, as a PNG at the scan's own resolution (down to
        /// <paramref name="maxEdge"/> pixels on its longer side). <paramref name="trim"/> cuts the empty
        /// white margin off; <paramref name="transparent"/> makes white see-through, so artwork sits
        /// on any background in the document. Returns the finished picture's size in mm.
        /// </summary>
        public static Bitmap Take(RawImage page, double[] region, bool trim, bool transparent, int maxEdge, out double widthMm, out double heightMm)
        {
            double sx = PxPerMm(page), sy = page.YDpi > 1 ? page.YDpi / 25.4 : sx;
            using (Bitmap full = page.ToBitmap())
            {
                int x = Math.Max(0, (int)Math.Round(region[0] * sx)), y = Math.Max(0, (int)Math.Round(region[1] * sy));
                int w = Math.Min(full.Width - x, (int)Math.Round(region[2] * sx)), h = Math.Min(full.Height - y, (int)Math.Round(region[3] * sy));
                if (w < 8 || h < 8) throw new ArgumentException("That region is outside the page (the page is " + Math.Round(PageWidthMm(page), 1) + " x " + Math.Round(PageHeightMm(page), 1) + " mm).");
                Bitmap cut = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(cut)) { g.Clear(Color.White); g.DrawImage(full, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel); }

                Rectangle keep = new Rectangle(0, 0, w, h);
                if (trim) keep = InkBounds(cut);
                if (keep.Width < 4 || keep.Height < 4) keep = new Rectangle(0, 0, w, h);
                widthMm = keep.Width / sx; heightMm = keep.Height / sy;

                double shrink = Math.Min(1.0, (double)maxEdge / Math.Max(keep.Width, keep.Height));
                int tw = Math.Max(1, (int)Math.Round(keep.Width * shrink)), th = Math.Max(1, (int)Math.Round(keep.Height * shrink));
                var result = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(cut, new Rectangle(0, 0, tw, th), keep, GraphicsUnit.Pixel);
                }
                cut.Dispose();
                if (transparent) WhiteToAlpha(result);
                return result;
            }
        }

        static Rectangle InkBounds(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, minX = w, minY = h, maxX = -1, maxY = -1;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        int lum = (row[x * 4] * 114 + row[x * 4 + 1] * 587 + row[x * 4 + 2] * 299) / 1000;
                        int max = Math.Max(row[x * 4 + 2], Math.Max(row[x * 4 + 1], row[x * 4])), min = Math.Min(row[x * 4 + 2], Math.Min(row[x * 4 + 1], row[x * 4]));
                        if (lum < 225 || max - min > 40)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            if (maxX < 0) return new Rectangle(0, 0, w, h);
            int pad = Math.Max(1, Math.Min(w, h) / 100);
            minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad); maxX = Math.Min(w - 1, maxX + pad); maxY = Math.Min(h - 1, maxY + pad);
            return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>White becomes see-through, light greys partly: the usual way of lifting a logo off its paper.</summary>
        static void WhiteToAlpha(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < h; y++)
                {
                    IntPtr at = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(at, row, 0, w * 4);
                    for (int x = 0; x < w; x++)
                    {
                        int low = Math.Min(row[x * 4], Math.Min(row[x * 4 + 1], row[x * 4 + 2]));
                        // Fully clear from 245 up, fully solid below 205, a ramp between.
                        int alpha = low >= 245 ? 0 : low <= 205 ? 255 : (int)((245 - low) * 255.0 / 40);
                        row[x * 4 + 3] = (byte)alpha;
                    }
                    System.Runtime.InteropServices.Marshal.Copy(row, 0, at, w * 4);
                }
            }
            finally { bmp.UnlockBits(data); }
        }
    }
}
