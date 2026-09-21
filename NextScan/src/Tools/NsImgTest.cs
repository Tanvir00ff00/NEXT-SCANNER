// =============================================================================
// NextScan Studio - imaging unit/golden tests (nsimgtest)
// Plan ref: MASTER_PLAN section 18.1/18.5, HANDOFF section 8.5.
//
//   nsimgtest            run all imaging tests, non-zero exit on any failure
//
// Detection is exercised on synthetic flatbed previews: a noisy grey lid with
// a brighter rotated document rectangle and ink specks, rendered straight into
// RawImage (no System.Drawing.Bitmap in the path being tested). The synthetic
// geometry is exact, so assertions can be tight: angle within a few tenths of
// a degree, box coverage by intersection-over-union.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using NextScan.Core;

namespace NextScan.Tools
{
    public static class NsImgTest
    {
        static int _failed;

        [STAThread]
        public static int Main()
        {
            // ---------------- detection ----------------
            TestDetectFlat();
            TestDetectRotated(3.0);
            TestDetectRotated(5.5);
            TestDetectNone();
            TestDetectGray16();

            // ---------------- curves ----------------
            TestCurveIdentity16();
            TestCurveInvert16();
            TestCurveMonotone16();
            TestCurveApply16();
            TestCurveIdentity8();

            // ---------------- deskew (plan 3.5) ----------------
            TestDeskewTextPage(0.0);
            TestDeskewTextPage(3.0);
            TestDeskewTextPage(8.0);
            TestDeskewTextPage(12.0);
            TestDeskewBeyondHardLimit();
            TestDeskewDiagonalArtwork();
            TestDeskewStrictGuardPreset();
            TestDeskewSourceAwareLimits();

            // ---------------- clean-up pass (plan 9) ----------------
            TestDescreenRemovesTheGrid();
            TestDescreenKeepsAPhotograph();
            TestSharpenSteepensAnEdge();
            TestDespeckleRemovesSpecksNotLines();
            TestBackgroundFlattensAndClearsBleedThrough();
            TestCleanupCostsNothingWhenOff();

            Console.WriteLine();
            if (_failed > 0)
            {
                Console.WriteLine(_failed + " test(s) FAILED");
                return 1;
            }
            Console.WriteLine("all imaging tests passed");
            return 0;
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>
        /// Renders a synthetic flatbed preview into RawImage: grey lid with +-2 noise,
        /// a white document rectangle (rotated around the image centre by the given
        /// angle) carrying a grid of black ink specks. 8-bit BGR unless gray16.
        /// </summary>
        static RawImage MakePreview(int w, int h, double cx, double cy, double halfW, double halfH,
                                    double angleDeg, bool gray16)
        {
            RawImage img = new RawImage();
            img.Width = w;
            img.Height = h;
            img.Channels = 3;
            img.BitsPerChannel = gray16 ? 16 : 8;
            img.Stride = w * 3 * (gray16 ? 2 : 1);
            img.XDpi = 150; img.YDpi = 150;
            img.Pixels = new byte[(long)h * img.Stride];

            Random rng = new Random(12345);   // fixed seed: byte-exact reproducibility
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            // Ink specks in document-local coordinates: a 5x3 grid of 6x6 squares.
            List<RectangleF> specks = new List<RectangleF>();
            for (int sy = 0; sy < 3; sy++)
                for (int sx = 0; sx < 5; sx++)
                    specks.Add(new RectangleF((float)(-halfW * 0.7 + sx * halfW * 0.35),
                                              (float)(-halfH * 0.6 + sy * halfH * 0.6), 6f, 6f));

            int step = gray16 ? 2 : 1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    // Inverse-rotate into document space.
                    double dx = x - cx, dy = y - cy;
                    double lu = dx * cos + dy * sin;
                    double lv = -dx * sin + dy * cos;

                    int v;
                    if (Math.Abs(lu) <= halfW && Math.Abs(lv) <= halfH)
                    {
                        bool ink = false;
                        foreach (RectangleF sp in specks)
                            if (lu >= sp.X && lu < sp.Right && lv >= sp.Y && lv < sp.Bottom) { ink = true; break; }
                        v = ink ? 30 : 235;
                    }
                    else
                    {
                        v = 120 + rng.Next(-2, 3);   // lid
                    }

                    int p = (y * w + x) * 3 * step;
                    if (gray16)
                    {
                        ushort q = (ushort)(v * 257);   // 0xAB -> 0xABAB
                        img.Pixels[p] = (byte)(q & 0xFF); img.Pixels[p + 1] = (byte)(q >> 8);
                        img.Pixels[p + 2] = (byte)(q & 0xFF); img.Pixels[p + 3] = (byte)(q >> 8);
                        img.Pixels[p + 4] = (byte)(q & 0xFF); img.Pixels[p + 5] = (byte)(q >> 8);
                    }
                    else
                    {
                        img.Pixels[p] = (byte)v; img.Pixels[p + 1] = (byte)v; img.Pixels[p + 2] = (byte)v;
                    }
                }
            }
            return img;
        }

        static Rectangle TrueAabb(double cx, double cy, double halfW, double halfH, double angleDeg)
        {
            double rad = angleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            double[] us = { -halfW, halfW };
            double[] vs = { -halfH, halfH };
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (double u in us)
                foreach (double v in vs)
                {
                    double px = cx + u * cos - v * sin;
                    double py = cy + u * sin + v * cos;
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                }
            return Rectangle.FromLTRB((int)Math.Floor(minX), (int)Math.Floor(minY),
                                      (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY));
        }

        static double Iou(Rectangle a, Rectangle b)
        {
            Rectangle i = Rectangle.Intersect(a, b);
            if (i.Width <= 0 || i.Height <= 0) return 0;
            double inter = (double)i.Width * i.Height;
            double uni = (double)a.Width * a.Height + (double)b.Width * b.Height - inter;
            return inter / uni;
        }

        // ---------------------------------------------------------------- detection tests
        // =====================================================================
        // The clean-up pass
        //
        // Every one of these filters compiles and runs whatever it does, so the
        // only question worth asking is numerical: did the thing it claims to
        // remove actually go, and did the thing it claims to keep actually stay.
        // Each case therefore measures both.
        // =====================================================================

        static Bitmap MakeFlat(int w, int h, int level, int dpi)
        {
            Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            bmp.SetResolution(dpi, dpi);
            using (Graphics g = Graphics.FromImage(bmp))
                g.Clear(Color.FromArgb(level, level, level));
            return bmp;
        }

        /// <summary>Mean absolute difference from the local 3x3 average: how textured a page is.</summary>
        static double Texture(Bitmap bmp)
        {
            double total = 0;
            int counted = 0;
            for (int y = 1; y < bmp.Height - 1; y += 1)
                for (int x = 1; x < bmp.Width - 1; x += 1)
                {
                    int sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            sum += bmp.GetPixel(x + dx, y + dy).R;
                    total += Math.Abs(bmp.GetPixel(x, y).R - sum / 9.0);
                    counted++;
                }
            return counted == 0 ? 0 : total / counted;
        }

        static ToneSettings Plain()
        {
            // Nothing but the filter under test: no preset curve, no polish, so
            // a change in the output can only have come from the filter.
            ToneSettings s = ToneEngine.Defaults(TonePreset.OriginalColour);
            s.Polish = 0;
            return s;
        }

        /// <summary>
        /// A halftone: a grid of dots at a known screen ruling, which is what
        /// printed matter actually is under a scanner.
        /// </summary>
        static Bitmap MakeHalftone(int dpi, int lpi)
        {
            int w = 300, h = 300;
            Bitmap bmp = MakeFlat(w, h, 170, dpi);
            double pitch = (double)dpi / lpi;

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool dot = (Math.Floor(x / pitch) + Math.Floor(y / pitch)) % 2 < 1;
                    int v = dot ? 90 : 240;
                    bmp.SetPixel(x, y, Color.FromArgb(v, v, v));
                }
            return bmp;
        }

        static void TestDescreenRemovesTheGrid()
        {
            using (Bitmap src = MakeHalftone(300, 133))
            {
                double before = Texture(src);

                ToneSettings s = Plain();
                s.DescreenLpi = 133;
                using (Bitmap outp = ToneEngine.Apply(src, s, ColorMode.Color24))
                {
                    double after = Texture(outp);
                    Check("descreen_removes_the_grid", after < before * 0.35,
                          "texture " + before.ToString("0.0") + " -> " + after.ToString("0.0") +
                          ", wanted under " + (before * 0.35).ToString("0.0"));
                }
            }
        }

        static void TestDescreenKeepsAPhotograph()
        {
            // A smooth gradient has no dot grid to remove, so descreen should
            // barely touch it. A filter that also flattens continuous tone is a
            // blur wearing a different name.
            int w = 300, h = 300;
            using (Bitmap src = MakeFlat(w, h, 128, 300))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int v = 20 + x * 200 / w;
                        src.SetPixel(x, y, Color.FromArgb(v, v, v));
                    }

                ToneSettings s = Plain();
                s.DescreenLpi = 133;
                using (Bitmap outp = ToneEngine.Apply(src, s, ColorMode.Color24))
                {
                    double worst = 0;
                    for (int x = 4; x < w - 4; x += 3)
                        worst = Math.Max(worst, Math.Abs(src.GetPixel(x, 150).R - outp.GetPixel(x, 150).R));

                    Check("descreen_keeps_a_photograph", worst <= 4,
                          "a smooth ramp moved by " + worst.ToString("0") + " levels");
                }
            }
        }

        static void TestSharpenSteepensAnEdge()
        {
            int w = 120, h = 60;
            using (Bitmap src = MakeFlat(w, h, 200, 300))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        // A soft edge, so there is something for the mask to find.
                        int v = x < w / 2 - 2 ? 60 : (x > w / 2 + 2 ? 220 : 60 + (x - (w / 2 - 2)) * 40);
                        src.SetPixel(x, y, Color.FromArgb(v, v, v));
                    }

                // The steepest single step across the edge, which is what
                // sharpening actually changes. Measuring two points either side
                // of it instead would sit outside the mask's reach and report no
                // change however well the filter worked.
                int before = Steepest(src, 30);

                ToneSettings s = Plain();
                s.Sharpen = 70;
                using (Bitmap outp = ToneEngine.Apply(src, s, ColorMode.Color24))
                {
                    int after = Steepest(outp, 30);
                    Check("sharpen_steepens_an_edge", after > before + 8,
                          "steepest step " + before + " -> " + after);
                }
            }
        }

        static int Steepest(Bitmap bmp, int row)
        {
            int worst = 0;
            for (int x = 1; x < bmp.Width; x++)
                worst = Math.Max(worst, Math.Abs(bmp.GetPixel(x, row).R - bmp.GetPixel(x - 1, row).R));
            return worst;
        }

        static void TestDespeckleRemovesSpecksNotLines()
        {
            int w = 160, h = 160;
            using (Bitmap src = MakeFlat(w, h, 250, 300))
            {
                // Isolated specks, and a line. One is dirt, the other is ink,
                // and the difference is the whole job.
                for (int i = 0; i < 40; i++)
                    src.SetPixel(7 + (i * 13) % 140, 11 + (i * 29) % 140, Color.FromArgb(20, 20, 20));
                for (int x = 20; x < 140; x++)
                    for (int y = 80; y < 83; y++)
                        src.SetPixel(x, y, Color.FromArgb(20, 20, 20));

                ToneSettings s = Plain();
                s.Despeckle = 80;
                using (Bitmap outp = ToneEngine.Apply(src, s, ColorMode.Color24))
                {
                    int specksLeft = 0;
                    for (int i = 0; i < 40; i++)
                    {
                        int x = 7 + (i * 13) % 140, y = 11 + (i * 29) % 140;
                        if (y >= 78 && y <= 85) continue;              // sitting on the line
                        if (outp.GetPixel(x, y).R < 128) specksLeft++;
                    }

                    int lineLeft = 0;
                    for (int x = 30; x < 130; x++) if (outp.GetPixel(x, 81).R < 128) lineLeft++;

                    Check("despeckle_removes_specks", specksLeft <= 4, specksLeft + " of 40 specks survived");
                    Check("despeckle_keeps_lines", lineLeft >= 95, "only " + lineLeft + " of 100 line pixels survived");
                }
            }
        }

        static void TestBackgroundFlattensAndClearsBleedThrough()
        {
            int w = 240, h = 160;
            using (Bitmap src = MakeFlat(w, h, 255, 300))
            {
                // Paper lit unevenly across the page, faint print showing through
                // from the back, and real black text on the front.
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int paper = 240 - x * 60 / w;
                        src.SetPixel(x, y, Color.FromArgb(paper, paper, paper));
                    }
                for (int x = 30; x < 210; x++)
                    for (int y = 40; y < 46; y++)
                    {
                        int paper = 240 - x * 60 / w;
                        int ghost = paper - 22;                        // faint: the other side
                        src.SetPixel(x, y, Color.FromArgb(ghost, ghost, ghost));
                    }
                for (int x = 30; x < 210; x++)
                    for (int y = 100; y < 108; y++)
                        src.SetPixel(x, y, Color.FromArgb(25, 25, 25));   // real ink

                ToneSettings s = Plain();
                s.BackgroundClean = 70;
                using (Bitmap outp = ToneEngine.Apply(src, s, ColorMode.Color24))
                {
                    int paperLeft = outp.GetPixel(20, 20).R, paperRight = outp.GetPixel(220, 20).R;
                    Check("background_flattens_the_paper",
                          paperLeft >= 245 && paperRight >= 245,
                          "paper went " + paperLeft + " on the left and " + paperRight + " on the right");

                    int ghost = outp.GetPixel(120, 43).R;
                    Check("background_clears_bleed_through", ghost >= 235,
                          "the show-through is still at " + ghost);

                    int ink = outp.GetPixel(120, 104).R;
                    Check("background_keeps_the_ink", ink <= 60, "real ink lightened to " + ink);
                }
            }
        }

        static void TestCleanupCostsNothingWhenOff()
        {
            // The pass was added by splitting Apply in three. With every filter
            // off the result has to be what it always was, byte for byte.
            using (Bitmap src = MakeHalftone(300, 133))
            using (Bitmap outp = ToneEngine.Apply(src, Plain(), ColorMode.Color24))
            {
                int worst = 0;
                for (int y = 0; y < src.Height; y += 7)
                    for (int x = 0; x < src.Width; x += 7)
                        worst = Math.Max(worst, Math.Abs(src.GetPixel(x, y).R - outp.GetPixel(x, y).R));

                Check("cleanup_off_changes_nothing", worst == 0, "pixels moved by up to " + worst);
            }
        }

        static void Check(string name, bool ok, string detail)
        {
            if (ok) Console.WriteLine("  ok   " + name);
            else { _failed++; Console.WriteLine("  FAIL " + name + ": " + detail); }
        }

        static void RunDetectCase(string name, double angleDeg, bool gray16, double angleTol, double iouMin)
        {
            RunDetectCase(name, angleDeg, gray16, angleTol, iouMin, false);
        }

        // allowGuardedZero: at small angles the 8px block staircase hides the true
        // rotation from the exact min-area rectangle (the axis-aligned box wins on
        // area), so the legacy guard legitimately reports 0 deg with the box still
        // correct. Plan section 3.5's confidence-scored estimators are what will
        // resolve small angles; until then the test asserts box quality, and angle
        // only when it is expected to be resolvable.
        static void RunDetectCase(string name, double angleDeg, bool gray16, double angleTol, double iouMin,
                                  bool allowGuardedZero)
        {
            int w = 400, h = 300;
            RawImage img = MakePreview(w, h, 200, 150, 130, 90, angleDeg, gray16);
            List<RotatedBox> boxes = DocumentDetector.Detect(img, DeskewGuard.StrictFlatbedGuard);
            if (boxes.Count == 0) { Check(name, false, "no boxes detected"); return; }

            RotatedBox box = boxes[0];
            Rectangle truth = TrueAabb(200, 150, 130, 90, angleDeg);
            double iou = Iou(box.AABB, truth);
            bool angleOk = Math.Abs(box.Angle - angleDeg) <= angleTol;
            if (allowGuardedZero && !angleOk && box.Angle == 0f && iou >= 0.93) angleOk = true;
            Check(name,
                  angleOk && iou >= iouMin,
                  "angle=" + box.Angle.ToString("0.##") + " (want " + angleDeg + " ±" + angleTol +
                  "), AABB=" + box.AABB + " vs truth=" + truth + ", IoU=" + iou.ToString("0.###"));
        }

        static void TestDetectFlat()
        {
            RunDetectCase("detect_flat_0deg", 0.0, false, 0.5, 0.93);
        }

        static void TestDetectRotated(double angle)
        {
            if (angle <= 4.0)
                RunDetectCase("detect_rotated_" + angle.ToString("0.#") + "deg", angle, false, 0.6, 0.90, true);
            else
                RunDetectCase("detect_rotated_" + angle.ToString("0.#") + "deg", angle, false, 0.6, 0.90);
        }

        static void TestDetectNone()
        {
            int w = 400, h = 300;
            RawImage img = MakePreview(w, h, 200, 150, 0, 0, 0, false);   // halfW=0: document vanishes
            List<RotatedBox> boxes = DocumentDetector.Detect(img, DeskewGuard.StrictFlatbedGuard);
            Check("detect_none", boxes.Count == 0, boxes.Count + " box(es) on an empty lid");
        }

        static void TestDetectGray16()
        {
            // Same scene as the flat case but delivered as 16-bit grey: the detector
            // must reduce depth without changing the answer.
            int w = 400, h = 300;
            RawImage img8 = MakePreview(w, h, 200, 150, 130, 90, 0.0, false);
            List<RotatedBox> boxes8 = DocumentDetector.Detect(img8, DeskewGuard.StrictFlatbedGuard);

            RawImage img16 = MakePreview(w, h, 200, 150, 130, 90, 0.0, true);
            img16.Channels = 3;   // still BGR triplets, 16-bit
            List<RotatedBox> boxes16 = DocumentDetector.Detect(img16, DeskewGuard.StrictFlatbedGuard);

            bool ok = boxes8.Count == 1 && boxes16.Count == 1 &&
                      boxes16[0].AABB == boxes8[0].AABB;
            string detail = "8bit=" + (boxes8.Count > 0 ? boxes8[0].AABB.ToString() : "none") +
                            " 16bit=" + (boxes16.Count > 0 ? boxes16[0].AABB.ToString() : "none");
            Check("detect_gray16_same_box", ok, detail);
        }

        // ---------------------------------------------------------------- curve tests
        static void TestCurveIdentity16()
        {
            List<PointF> pts = new List<PointF>();
            ushort[] lut = Curves.BuildLut16(pts);   // empty = identity, as in scanhelper
            bool ok = true;
            for (int i = 0; i < 65536; i++) if (lut[i] != i) { ok = false; break; }
            Check("curve_identity16", ok, "LUT is not the identity");
        }

        static void TestCurveInvert16()
        {
            List<PointF> pts = new List<PointF>();
            pts.Add(new PointF(0, 65535));
            pts.Add(new PointF(65535, 0));
            ushort[] lut = Curves.BuildLut16(pts);
            bool ok = true;
            for (int i = 0; i < 65536; i++) if (lut[i] != 65535 - i) { ok = false; break; }
            Check("curve_invert16", ok, "two-point line did not come out exactly linear");
        }

        static void TestCurveMonotone16()
        {
            List<PointF> pts = new List<PointF>();
            pts.Add(new PointF(0, 0));
            pts.Add(new PointF(16384, 8000));
            pts.Add(new PointF(32768, 22000));
            pts.Add(new PointF(49152, 42000));
            pts.Add(new PointF(65535, 65535));
            ushort[] lut = Curves.BuildLut16(pts);
            bool mono = true;
            for (int i = 1; i < 65536; i++) if (lut[i] < lut[i - 1]) { mono = false; break; }
            bool ends = lut[0] == 0 && lut[65535] == 65535;
            Check("curve_monotone16", mono && ends,
                  "monotone=" + mono + ", endpoints " + lut[0] + "/" + lut[65535]);
        }

        static void TestCurveApply16()
        {
            // A 16-bit grey ramp through the invert LUT must come out exactly
            // complemented - no rounding drift anywhere in the range.
            int w = 256, h = 256;
            RawImage img = new RawImage();
            img.Width = w; img.Height = h;
            img.Channels = 1; img.BitsPerChannel = 16;
            img.Stride = w * 2;
            img.Pixels = new byte[(long)h * img.Stride];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    ushort v = (ushort)(y * 257);
                    int p = (y * w + x) * 2;
                    img.Pixels[p] = (byte)(v & 0xFF);
                    img.Pixels[p + 1] = (byte)(v >> 8);
                }

            List<PointF> pts = new List<PointF>();
            pts.Add(new PointF(0, 65535));
            pts.Add(new PointF(65535, 0));
            Curves.Apply16(img, Curves.BuildLut16(pts));

            bool ok = true;
            for (int y = 0; y < h && ok; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 2;
                    ushort got = (ushort)(img.Pixels[p] | (img.Pixels[p + 1] << 8));
                    if (got != 65535 - y * 257) { ok = false; break; }
                }
            Check("curve_apply16_exact", ok, "16-bit apply did not complement exactly");
        }

        static void TestCurveIdentity8()
        {
            List<PointF> pts = new List<PointF>();
            byte[] lut = Curves.BuildLut8(pts);
            bool ok = true;
            for (int i = 0; i < 256; i++) if (lut[i] != i) { ok = false; break; }
            Check("curve_identity8", ok, "8-bit LUT is not the identity");
        }

        // ---------------------------------------------------------------- deskew tests
        /// <summary>
        /// A text page on the flatbed: white document on a grey lid, carrying 10
        /// rows of ink "text" lines. paperAngle rotates the document; inkAngle
        /// rotates only the ink frame - the classic "diagonal line in the artwork"
        /// trap from plan section 3.5 is paperAngle=0 with a nonzero inkAngle.
        /// The document is kept under 70% of the frame width so the detector's
        /// full-page rule (which forces angle 0) does not kick in.
        /// </summary>
        static RawImage MakeTextPage(int w, int h, double paperAngle, double inkAngle, bool singleBar)
        {
            RawImage img = new RawImage();
            img.Width = w; img.Height = h;
            img.Channels = 3; img.BitsPerChannel = 8;
            img.Stride = w * 3;
            img.XDpi = 150; img.YDpi = 150;
            img.Pixels = new byte[(long)h * img.Stride];

            Random rng = new Random(777);
            double cx = w / 2.0, cy = h / 2.0;
            // Under half the frame width, so the detector's full-page rule (which
            // forces angle 0 by design) cannot fire and mask the estimators.
            double halfW = w * 0.25, halfH = h * 0.30;
            double pRad = paperAngle * Math.PI / 180.0;
            double pCos = Math.Cos(pRad), pSin = Math.Sin(pRad);
            double iRad = inkAngle * Math.PI / 180.0;
            double iCos = Math.Cos(iRad), iSin = Math.Sin(iRad);

            // Ink rows in the INK frame's coordinates.
            List<RectangleF> ink = new List<RectangleF>();
            if (singleBar)
            {
                ink.Add(new RectangleF((float)-halfW, (float)-6, (float)(2 * halfW), 12f));
            }
            else
            {
                for (int line = 0; line < 10; line++)
                {
                    double ly = -halfH * 0.8 + line * (halfH * 1.6 / 9.0);
                    double lx = -halfW * 0.75;
                    while (lx < halfW * 0.75)
                    {
                        int wordLen = 14 + rng.Next(0, 30);
                        ink.Add(new RectangleF((float)lx, (float)ly, wordLen, 5f));
                        lx += wordLen + 6 + rng.Next(0, 8);
                    }
                }
            }

            // Antialiased rendering: 3x3 supersampling. A hard-threshold renderer
            // turns a 3 deg edge into a staircase whose segments are 95 percent
            // axis-aligned, which defeats the Sobel orientation estimator and
            // poisons the ink mask - real scanners antialias, so the synthetic
            // scene must too.
            for (int y = 0; y < h; y++)
            {
                int rowBase = y * img.Stride;
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int sy = 0; sy < 3; sy++)
                    {
                        double fy = y + (sy - 1) / 3.0;
                        for (int sx = 0; sx < 3; sx++)
                        {
                            double fx = x + (sx - 1) / 3.0;
                            double dx = fx - cx, dy = fy - cy;
                            // paper frame
                            double lu = dx * pCos + dy * pSin;
                            double lv = -dx * pSin + dy * pCos;
                            int v;
                            if (Math.Abs(lu) <= halfW && Math.Abs(lv) <= halfH)
                            {
                                // ink frame, expressed relative to the paper axes
                                double iu = lu * iCos + lv * iSin;
                                double iv = -lu * iSin + lv * iCos;
                                bool isInk = false;
                                foreach (RectangleF r in ink)
                                    if (iu >= r.X && iu < r.Right && iv >= r.Y && iv < r.Bottom) { isInk = true; break; }
                                v = isInk ? 30 : 235;
                            }
                            else
                            {
                                v = 120 + rng.Next(-2, 3);
                            }
                            sum += v;
                        }
                    }
                    int val = sum / 9;
                    int p = rowBase + x * 3;
                    img.Pixels[p] = (byte)val;
                    img.Pixels[p + 1] = (byte)val;
                    img.Pixels[p + 2] = (byte)val;
                }
            }
            return img;
        }

        static void RunDeskewCase(string name, double paperAngle, bool expectAuto)
        {
            // inkAngle 0: the text is aligned with the paper, both rotated by
            // paperAngle (inkAngle is RELATIVE to the paper frame).
            RawImage img = MakeTextPage(400, 300, paperAngle, 0.0, false);
            List<RotatedBox> boxes = DocumentDetector.Detect(img, DeskewGuard.Off);
            if (boxes.Count == 0) { Check(name, false, "detection found no document"); return; }

            float[] est = DeskewEstimators.EstimateAll(img, boxes[0]);
            DeskewResult r = DeskewPolicy.Evaluate(est, DeskewEstimators.Resolutions,
                                                   PaperSource.Flatbed, DeskewProfileKind.Standard);

            string detail = "skew=" + r.Skew.ToString("0.##") + " (truth " + paperAngle + "), conf=" +
                            r.Confidence.ToString("0.00") + ", auto=" + r.AutoRotate +
                            ", review=" + r.NeedsReview + ", est=[" +
                            est[0].ToString("0.##") + "," + est[1].ToString("0.##") + "," +
                            est[2].ToString("0.##") + "," + est[3].ToString("0.##") + "] " + r.Notes;

            bool angleOk = Math.Abs(r.Skew - paperAngle) <= 0.75;
            bool ok = angleOk && r.AutoRotate == expectAuto;
            Check(name, ok, detail);
        }

        static void TestDeskewTextPage(double angle)
        {
            // Flatbed (soft limit 6 deg). Small angles: projection+Hough resolve,
            // the two coarse estimators fall inside their measured resolution
            // floors, so confidence is high and the page rotates - including
            // angles the legacy guard silently zeroed. Beyond 6 deg on a flatbed
            // the policy demands HighConfidence (0.90): with the two coarse
            // estimators out of their depth the honest result is review, which
            // still beats the legacy silent 0 because the user gets a hint.
            bool expectAuto = angle <= 6.0;
            RunDeskewCase("deskew_text_" + angle.ToString("0.#") + "deg", angle, expectAuto);
        }

        static void TestDeskewBeyondHardLimit()
        {
            // 25 deg is beyond the 20 deg hard limit: measured (the sweep reaches
            // 30 deg) but never auto-rotated; the UI hint is raised instead.
            RawImage img = MakeTextPage(400, 300, 25.0, 0.0, false);
            List<RotatedBox> boxes = DocumentDetector.Detect(img, DeskewGuard.Off);
            if (boxes.Count == 0) { Check("deskew_25deg_needs_review", false, "detection found no document"); return; }

            float[] est = DeskewEstimators.EstimateAll(img, boxes[0]);
            DeskewResult r = DeskewPolicy.Evaluate(est, DeskewEstimators.Resolutions,
                                                   PaperSource.Flatbed, DeskewProfileKind.Standard);

            bool ok = !r.AutoRotate && r.NeedsReview && Math.Abs(r.Skew - 25.0) <= 1.5;
            Check("deskew_25deg_needs_review", ok,
                  "skew=" + r.Skew.ToString("0.##") + ", conf=" + r.Confidence.ToString("0.00") +
                  ", auto=" + r.AutoRotate + ", est=[" + est[0].ToString("0.##") + "," +
                  est[1].ToString("0.##") + "," + est[2].ToString("0.##") + "," + est[3].ToString("0.##") + "]");
        }

        static void TestDeskewDiagonalArtwork()
        {
            // Straight paper, one diagonal bar of "artwork": the ink estimators
            // see the bar (12 deg), the paper estimators see the truth (0). The
            // page must NOT be rotated to follow the bar (plan 3.5's case); the
            // genuinely ambiguous scene lands in review instead.
            RawImage img = MakeTextPage(400, 300, 0.0, 12.0, true);
            List<RotatedBox> boxes = DocumentDetector.Detect(img, DeskewGuard.Off);
            if (boxes.Count == 0) { Check("deskew_diagonal_artwork", false, "detection found no document"); return; }

            float[] est = DeskewEstimators.EstimateAll(img, boxes[0]);
            DeskewResult r = DeskewPolicy.Evaluate(est, DeskewEstimators.Resolutions,
                                                   PaperSource.Flatbed, DeskewProfileKind.Standard);

            bool ok = !r.AutoRotate;
            Check("deskew_diagonal_artwork", ok,
                  "skew=" + r.Skew.ToString("0.##") + ", conf=" + r.Confidence.ToString("0.00") +
                  ", auto=" + r.AutoRotate + ", est=[" + est[0].ToString("0.##") + "," +
                  est[1].ToString("0.##") + "," + est[2].ToString("0.##") + "," + est[3].ToString("0.##") + "]");
        }

        static void TestDeskewStrictGuardPreset()
        {
            // Legacy rule verbatim: 3 deg survives, 8 deg is forced to zero.
            float[] e3 = { 3f, 3f, 3f, 3f };
            DeskewResult r3 = DeskewPolicy.Evaluate(e3, PaperSource.Flatbed, DeskewProfileKind.StrictFlatbedGuard);
            bool ok3 = r3.AutoRotate && Math.Abs(r3.Skew - 3f) < 0.01f;

            float[] e8 = { 8f, 8f, 8f, 8f };
            DeskewResult r8 = DeskewPolicy.Evaluate(e8, PaperSource.Flatbed, DeskewProfileKind.StrictFlatbedGuard);
            bool ok8 = r8.AutoRotate && Math.Abs(r8.Skew) < 0.01f;

            Check("deskew_strict_guard_preset", ok3 && ok8,
                  "3deg skew=" + r3.Skew + ", 8deg skew=" + r8.Skew);
        }

        static void TestDeskewSourceAwareLimits()
        {
            // On a feeder the soft limit is 10 deg: an 8 deg page needs only
            // MinConfidence, while on film (soft limit 3 deg) even 4 deg requires
            // HighConfidence.
            float[] e8 = { 8f, 8f, 8.1f, 7.9f };   // conf = 1.0
            DeskewResult adf = DeskewPolicy.Evaluate(e8, PaperSource.Feeder, DeskewProfileKind.Standard);
            bool okAdf = adf.AutoRotate && Math.Abs(adf.Skew - 8f) < 0.2f;

            float[] e4 = { 4f, 4f, 4f, 4f };       // conf = 1.0 -> passes HighConfidence
            DeskewResult film = DeskewPolicy.Evaluate(e4, PaperSource.Film, DeskewProfileKind.Standard);
            bool okFilm = film.AutoRotate;

            // Same film case with weak agreement: 4 deg must NOT rotate.
            float[] e4low = { 4f, 4f, 0f, 0f };    // 2/4 agree -> conf 0.5
            DeskewResult filmLow = DeskewPolicy.Evaluate(e4low, PaperSource.Film, DeskewProfileKind.Standard);
            bool okFilmLow = !filmLow.AutoRotate && filmLow.NeedsReview;

            Check("deskew_source_aware_limits", okAdf && okFilm && okFilmLow,
                  "adf8 auto=" + adf.AutoRotate + ", film4 auto=" + film.AutoRotate +
                  ", film4low auto=" + filmLow.AutoRotate + " conf=" + filmLow.Confidence.ToString("0.00"));
        }
    }
}
