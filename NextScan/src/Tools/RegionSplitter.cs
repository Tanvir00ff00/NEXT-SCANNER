// =============================================================================
// Historical regression component only. Plan ref: accuracy work order section 3.
// PlatenDetector separates the tested 4 mm gap without a gutter split. The old
// algorithm remains in nscroptest to preserve its three regression cases, but is
// excluded from the engine and application so it cannot split white stock bands.
// NextScan Studio - splitting a detected region along an empty gutter
// Plan ref: MASTER_PLAN section 8.1 (multi-region detection).
//
// Measured on a real scan: two ID cards lying side by side 7 mm apart on a
// CanoScan LiDE 400 came back from the detector as a single 7.00 x 2.06 in
// region covering both. The detector's own fragment-merge rule was not at fault
// - its limit is 2.5 mm and the gap is nearly three times that - so the two
// cards were already one connected blob in the evidence mask, bridged by the
// faint streaking this platen produces between them.
//
// Rather than re-tune the detector's evidence, which is tuned against a great
// many other cases, this looks at the finished region and asks a much narrower
// question: does a clean, full-height band of background run all the way across
// it? Two documents side by side always have one. A single document with a
// white margin down its middle does not, because its own edges interrupt the
// band at top and bottom.
//
// It is deliberately conservative. A rotated pair has no straight gutter, so
// nothing is found and the detector's answer stands unchanged; that is the right
// outcome, because a wrong split is worse than a merged pair the operator can
// see and fix.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;

namespace NextScan.Core
{
    public static class RegionSplitter
    {
        public static Action<string> Log = delegate { };

        /// <summary>
        /// Narrowest gutter that counts as a separation, in millimetres.
        ///
        /// Measured: two cards placed 5 mm apart profiled as only 2.4 mm of
        /// completely clean columns, because each card's edge casts a shadow a
        /// millimetre or so into the gap. The limit is set below what the
        /// physical gap measures for that reason, and the plausibility check on
        /// the resulting parts is what keeps it honest rather than a wider
        /// threshold here.
        /// </summary>
        public const double MinimumGutterMm = 2.0;

        /// <summary>
        /// Splits regions that turn out to hold several documents side by side.
        /// Returns the input unchanged when nothing convincing is found.
        /// </summary>
        public static List<CropRegion> Split(RawImage page, IList<CropRegion> regions, AutoCropOptions options)
        {
            List<CropRegion> output = new List<CropRegion>();
            if (regions == null || regions.Count == 0) return output;
            if (options == null) options = new AutoCropOptions();

            foreach (CropRegion region in regions)
            {
                List<CropRegion> parts = null;
                try { parts = SplitOne(page, region, options); }
                catch (Exception ex) { Log("gutter split failed: " + ex.Message); }

                if (parts == null || parts.Count < 2) output.Add(region);
                else output.AddRange(parts);
            }

            // Reading order, banded so that two items whose centres differ by a
            // few pixels are still ordered left to right rather than by that
            // difference.
            output.Sort(delegate (CropRegion a, CropRegion b)
            {
                float band = Math.Max(0.02f, Math.Min(a.NormRect.Height, b.NormRect.Height) * 0.5f);
                float da = a.NormRect.Y, db = b.NormRect.Y;
                if (Math.Abs(da - db) > band) return da.CompareTo(db);
                return a.NormRect.X.CompareTo(b.NormRect.X);
            });
            for (int i = 0; i < output.Count; i++) output[i].Index = i + 1;

            return output;
        }

        // =====================================================================
        // One region
        // =====================================================================
        static List<CropRegion> SplitOne(RawImage page, CropRegion region, AutoCropOptions options)
        {
            if (page == null || !page.IsValid || region == null) return null;

            // A rotated document has no straight gutter, and forcing one would cut
            // through the page. Leave those to the detector.
            if (Math.Abs(region.SkewDegrees) > 1.5f) return null;

            Rectangle box = PixelBox(page, region.NormRect);
            if (box.Width < 32 || box.Height < 32) return null;

            byte[] background = SampleBackground(page, box);
            int ink = InkThreshold(page, box, background);
            bool[] columnEmpty = EmptyColumns(page, box, background, ink);
            bool[] rowEmpty = EmptyRows(page, box, background, ink);

            int minGutterX = Math.Max(4, (int)Math.Round(MinimumGutterMm * page.XDpi / 25.4));
            int minGutterY = Math.Max(4, (int)Math.Round(MinimumGutterMm * page.YDpi / 25.4));

            List<int> cutsX = Gutters(columnEmpty, minGutterX);
            List<int> cutsY = Gutters(rowEmpty, minGutterY);
            if (cutsX.Count == 0 && cutsY.Count == 0) return null;

            // Vertical and horizontal cuts together give a grid, which is what a
            // sheet of photographs or a page of cards actually is.
            List<Rectangle> cells = Grid(box, cutsX, cutsY);
            if (cells.Count < 2) return null;

            double minimumArea = options.MinAreaMm2 * page.XDpi * page.YDpi / (25.4 * 25.4);
            List<CropRegion> parts = new List<CropRegion>();

            foreach (Rectangle cell in cells)
            {
                Rectangle tight = Trim(page, cell, background, ink);
                if (tight.Width < 16 || tight.Height < 16) continue;
                if ((double)tight.Width * tight.Height < minimumArea) continue;

                // A gutter alone is not proof: a table with a wide column rule
                // has one too. Each piece must also be shaped like something
                // somebody would put on a scanner - a sliver is a rule, not a
                // document.
                double aspect = (double)tight.Width / tight.Height;
                if (aspect < 0.2 || aspect > 5.0)
                {
                    Log("gutter split rejected: a part was " +
                        aspect.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                        " wide to tall");
                    return null;
                }

                parts.Add(Make(page, region, tight));
            }

            if (parts.Count < 2) return null;

            Log("gutter split: one region became " + parts.Count);
            return parts;
        }

        static Rectangle PixelBox(RawImage page, RectangleF norm)
        {
            int x = (int)Math.Round(norm.X * page.Width);
            int y = (int)Math.Round(norm.Y * page.Height);
            int w = (int)Math.Round(norm.Width * page.Width);
            int h = (int)Math.Round(norm.Height * page.Height);

            x = Math.Max(0, Math.Min(page.Width - 1, x));
            y = Math.Max(0, Math.Min(page.Height - 1, y));
            w = Math.Max(1, Math.Min(page.Width - x, w));
            h = Math.Max(1, Math.Min(page.Height - y, h));
            return new Rectangle(x, y, w, h);
        }

        // =====================================================================
        // Background and emptiness
        // =====================================================================
        /// <summary>
        /// The gap colour, taken from a ring just outside the region. Sampling
        /// inside it would pick up whatever the documents are printed on, which
        /// on a pale card is close enough to the platen to make everything look
        /// like a gutter.
        /// </summary>
        static byte[] SampleBackground(RawImage page, Rectangle box)
        {
            List<int> blue = new List<int>(), green = new List<int>(), red = new List<int>();
            int reach = Math.Max(3, Math.Min(page.Width, page.Height) / 100);

            for (int y = box.Top; y < box.Bottom; y += 3)
            {
                Sample(page, box.Left - reach, y, blue, green, red);
                Sample(page, box.Right + reach, y, blue, green, red);
            }
            for (int x = box.Left; x < box.Right; x += 3)
            {
                Sample(page, x, box.Top - reach, blue, green, red);
                Sample(page, x, box.Bottom + reach, blue, green, red);
            }

            if (blue.Count < 8) return new byte[] { 235, 235, 235 };
            return new byte[] { Median(blue), Median(green), Median(red) };
        }

        static void Sample(RawImage p, int x, int y, List<int> b, List<int> g, List<int> r)
        {
            if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) return;
            int o = y * p.Stride + x * Bpp(p);
            if (p.Channels == 1)
            {
                int v = Value(p, o);
                b.Add(v); g.Add(v); r.Add(v);
                return;
            }
            b.Add(Value(p, o));
            g.Add(Value(p, o + (p.BitsPerChannel == 16 ? 2 : 1)));
            r.Add(Value(p, o + (p.BitsPerChannel == 16 ? 4 : 2)));
        }

        static int Bpp(RawImage p)
        {
            if (p.BitsPerChannel == 1) return 1;
            return p.Channels * (p.BitsPerChannel == 16 ? 2 : 1);
        }

        static int Value(RawImage p, int offset)
        {
            if (p.BitsPerChannel == 16) return p.Pixels[offset + 1];
            return p.Pixels[offset];
        }

        static byte Median(List<int> values)
        {
            values.Sort();
            return (byte)values[values.Count / 2];
        }

        /// <summary>How far a pixel sits from the gap colour, summed over channels.</summary>
        static int Distance(RawImage p, int x, int y, byte[] background)
        {
            if (p.BitsPerChannel == 1)
            {
                int bit = (p.Pixels[y * p.Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 255 : 0;
                return Math.Abs(bit - background[0]) * 3;
            }

            int o = y * p.Stride + x * Bpp(p);
            if (p.Channels == 1)
            {
                int v = Value(p, o);
                return Math.Abs(v - background[0]) * 3;
            }
            int step = p.BitsPerChannel == 16 ? 2 : 1;
            return Math.Abs(Value(p, o) - background[0])
                 + Math.Abs(Value(p, o + step) - background[1])
                 + Math.Abs(Value(p, o + step * 2) - background[2]);
        }

        // A column counts as empty when nearly every pixel in it matches the gap
        // colour. "Nearly" rather than "every" because a speck of dust in the gap
        // between two cards must not weld them together.

        // Measured on the real scan that prompted this: the 5 mm gap between two
        // ID cards profiled at 0.068, 0.019 and 0.000 inked across its width. A
        // demand for 0.985 clean only accepted the middle of it, shrinking a
        // 5 mm gutter below the 3 mm minimum and losing the split entirely. The
        // edges of a gap always carry a little bleed from the shadow either side.
        const double EmptyFraction = 0.96;

        /// <summary>
        /// How far from the gap colour a pixel must sit to count as content,
        /// summed over three channels.
        ///
        /// A fixed number cannot work here, and trying one proved it: 42 read the
        /// real streaky platen correctly but treated blank card stock as
        /// background on a clean synthetic page, while 24 did the reverse. The
        /// figure has to come from the image. It is set above the spread of the
        /// background ring itself, so a calm platen gets a low bar and a streaky
        /// one gets a high bar, which is exactly the difference between the two
        /// cases.
        /// </summary>
        static int InkThreshold(RawImage page, Rectangle box, byte[] background)
        {
            List<int> distances = new List<int>();
            int reach = Math.Max(3, Math.Min(page.Width, page.Height) / 100);

            for (int y = box.Top; y < box.Bottom; y += 3)
            {
                AddDistance(page, box.Left - reach, y, background, distances);
                AddDistance(page, box.Right + reach, y, background, distances);
            }
            for (int x = box.Left; x < box.Right; x += 3)
            {
                AddDistance(page, x, box.Top - reach, background, distances);
                AddDistance(page, x, box.Bottom + reach, background, distances);
            }

            if (distances.Count < 16) return 24;

            distances.Sort();
            int spread = distances[(int)(distances.Count * 0.9)];   // ignore the worst tenth

            // The multiplier is fitted to two measured pages, not derived: on a
            // real LiDE 400 scan the ring spread is 13, and 3.5 puts the bar at
            // 46, where the 5 mm gap between two cards measures 30 clean columns;
            // on a flat synthetic page the spread is 0 and the bar falls to its
            // floor of 18, below blank card stock at 42. Anything near 2.5 fails
            // the first and anything near 6 starts calling faint print empty.
            return Math.Max(18, Math.Min(96, (int)Math.Round(spread * 3.5)));
        }

        static void AddDistance(RawImage p, int x, int y, byte[] background, List<int> into)
        {
            if (x < 0 || y < 0 || x >= p.Width || y >= p.Height) return;
            into.Add(Distance(p, x, y, background));
        }

        static bool[] EmptyColumns(RawImage page, Rectangle box, byte[] background, int ink)
        {
            bool[] empty = new bool[box.Width];
            int step = Math.Max(1, box.Height / 400);

            for (int i = 0; i < box.Width; i++)
            {
                int x = box.Left + i, inked = 0, seen = 0;
                for (int y = box.Top; y < box.Bottom; y += step)
                {
                    seen++;
                    if (Distance(page, x, y, background) > ink) inked++;
                }
                empty[i] = seen > 0 && (seen - inked) >= seen * EmptyFraction;
            }
            return empty;
        }

        static bool[] EmptyRows(RawImage page, Rectangle box, byte[] background, int ink)
        {
            bool[] empty = new bool[box.Height];
            int step = Math.Max(1, box.Width / 400);

            for (int i = 0; i < box.Height; i++)
            {
                int y = box.Top + i, inked = 0, seen = 0;
                for (int x = box.Left; x < box.Right; x += step)
                {
                    seen++;
                    if (Distance(page, x, y, background) > ink) inked++;
                }
                empty[i] = seen > 0 && (seen - inked) >= seen * EmptyFraction;
            }
            return empty;
        }

        /// <summary>
        /// Centres of interior empty runs at least <paramref name="minimum"/>
        /// wide. Runs touching either end are the region's own margin, not a
        /// separation between two things.
        /// </summary>
        static List<int> Gutters(bool[] empty, int minimum)
        {
            List<int> cuts = new List<int>();
            int start = -1;

            for (int i = 0; i <= empty.Length; i++)
            {
                bool inRun = i < empty.Length && empty[i];
                if (inRun && start < 0) start = i;
                else if (!inRun && start >= 0)
                {
                    int length = i - start;
                    bool interior = start > 0 && i < empty.Length;
                    if (interior && length >= minimum) cuts.Add(start + length / 2);
                    start = -1;
                }
            }
            return cuts;
        }

        static List<Rectangle> Grid(Rectangle box, List<int> cutsX, List<int> cutsY)
        {
            List<int> xs = new List<int> { 0 };
            xs.AddRange(cutsX);
            xs.Add(box.Width);
            List<int> ys = new List<int> { 0 };
            ys.AddRange(cutsY);
            ys.Add(box.Height);

            List<Rectangle> cells = new List<Rectangle>();
            for (int j = 0; j + 1 < ys.Count; j++)
                for (int i = 0; i + 1 < xs.Count; i++)
                {
                    int w = xs[i + 1] - xs[i], h = ys[j + 1] - ys[j];
                    if (w <= 0 || h <= 0) continue;
                    cells.Add(new Rectangle(box.Left + xs[i], box.Top + ys[j], w, h));
                }
            return cells;
        }

        /// <summary>Shrinks a cell onto its content, discarding the gutter it was cut from.</summary>
        static Rectangle Trim(RawImage page, Rectangle cell, byte[] background, int ink)
        {
            int left = cell.Left, right = cell.Right - 1, top = cell.Top, bottom = cell.Bottom - 1;
            int stepY = Math.Max(1, cell.Height / 400), stepX = Math.Max(1, cell.Width / 400);

            while (left < right && ColumnEmpty(page, left, top, bottom, stepY, background, ink)) left++;
            while (right > left && ColumnEmpty(page, right, top, bottom, stepY, background, ink)) right--;
            while (top < bottom && RowEmpty(page, top, left, right, stepX, background, ink)) top++;
            while (bottom > top && RowEmpty(page, bottom, left, right, stepX, background, ink)) bottom--;

            return new Rectangle(left, top, right - left + 1, bottom - top + 1);
        }

        static bool ColumnEmpty(RawImage page, int x, int y0, int y1, int step, byte[] background, int ink)
        {
            int inked = 0, seen = 0;
            for (int y = y0; y <= y1; y += step)
            {
                seen++;
                if (Distance(page, x, y, background) > ink) inked++;
            }
            return seen > 0 && (seen - inked) >= seen * EmptyFraction;
        }

        static bool RowEmpty(RawImage page, int y, int x0, int x1, int step, byte[] background, int ink)
        {
            int inked = 0, seen = 0;
            for (int x = x0; x <= x1; x += step)
            {
                seen++;
                if (Distance(page, x, y, background) > ink) inked++;
            }
            return seen > 0 && (seen - inked) >= seen * EmptyFraction;
        }

        static CropRegion Make(RawImage page, CropRegion parent, Rectangle box)
        {
            CropRegion c = new CropRegion();
            c.NormRect = new RectangleF(
                box.X / (float)page.Width, box.Y / (float)page.Height,
                box.Width / (float)page.Width, box.Height / (float)page.Height);
            c.WidthInches = box.Width / Dpi(page.XDpi);
            c.HeightInches = box.Height / Dpi(page.YDpi);
            c.SkewDegrees = parent.SkewDegrees;
            c.Confidence = parent.Confidence;
            c.Score = parent.Score;
            c.Reason = "separated from a wider region along an empty gutter";

            RotatedBox rb = new RotatedBox();
            rb.IsValid = true;
            rb.Angle = parent.SkewDegrees;
            rb.RawAngle = parent.SkewDegrees;
            rb.Width = box.Width;
            rb.Height = box.Height;
            rb.Center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);
            rb.AABB = box;
            rb.Corners = new PointF[]
            {
                new PointF(box.Left, box.Top), new PointF(box.Right, box.Top),
                new PointF(box.Right, box.Bottom), new PointF(box.Left, box.Bottom)
            };
            c.Box = rb;
            return c;
        }

        static double Dpi(double v)
        {
            return (v > 0 && !double.IsInfinity(v) && !double.IsNaN(v)) ? v : 300.0;
        }
    }
}
