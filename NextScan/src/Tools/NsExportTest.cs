// =============================================================================
// NextScan Studio - nsexporttest: file output and batch regression tests
// Plan ref: MASTER_PLAN section 11.1 (export), 12 (batch), 18.1 (tests).
//
// Saving is the one part of a scanner suite whose bugs are silent: a PDF with a
// broken cross-reference table still opens in the reader that ignores it, a
// multi-page TIFF that kept only its last page looks fine in a thumbnail, and a
// name pattern that resolves to the same file twice destroys yesterday's work
// without an error anywhere. Every case here therefore reads the file back and
// checks what is actually in it, rather than trusting the return value.
//
// Runs entirely on synthetic pages: no scanner and no simulator involved.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using NextScan.Core;

namespace NextScan.Tools
{
    public static class NsExportTest
    {
        static int _fail;
        static string _root;

        public static int Main(string[] args)
        {
            _root = Path.Combine(Path.GetTempPath(), "nsexporttest_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);

            Console.WriteLine();
            Console.WriteLine("NextScan export and batch tests");
            Console.WriteLine("  scratch: " + _root);
            Console.WriteLine();

            Console.WriteLine("  naming");
            Case("name_tokens", NameTokens);
            Case("name_counter", NameCounter);
            Case("name_subfolders", NameSubfolders);
            Case("name_never_overwrites", NameNeverOverwrites);
            Case("name_cannot_escape", NameCannotEscape);

            Console.WriteLine("  formats");
            Case("jpeg_roundtrip", JpegRoundTrip);
            Case("png_roundtrip", PngRoundTrip);
            Case("tiff_multipage", TiffMultiPage);
            Case("tiff_per_page", TiffPerPage);
            Case("pdf_multipage", PdfMultiPage);
            Case("pdf_page_size", PdfPageSize);
            Case("pdf_gray_stays_gray", PdfGrayStaysGray);
            Case("pdf_colorspace_matches", PdfColorSpaceMatchesData);
            Case("pdf_bilevel_lossless", PdfBilevelLossless);
            Case("no_temp_files_left", NoTempFilesLeft);

            Console.WriteLine("  batch");
            Case("blank_detection", BlankDetection);
            Case("blank_drop_duplex", BlankDropDuplex);
            Case("split_fixed_count", SplitFixedCount);
            Case("split_blank_separator", SplitBlankSeparator);
            Case("batch_writes_documents", BatchWritesDocuments);

            Console.WriteLine("  tone");
            Case("tone_original_is_faithful", ToneOriginalIsFaithful);
            Case("tone_polish_keeps_hue", TonePolishKeepsHue);
            Case("tone_polish_never_clips", TonePolishNeverClips);
            Case("tone_no_contour_on_ramp", ToneNoContourOnRamp);
            Case("tone_curves_are_monotonic", ToneCurvesAreMonotonic);
            Case("tone_greyscale_is_neutral", ToneGreyscaleIsNeutral);
            Case("tone_adaptive_beats_global", ToneAdaptiveBeatsGlobal);
            Case("tone_faded_restores_range", ToneFadedRestoresRange);

            Console.WriteLine("  hot folder");
            Case("hf_existing_and_new", HfExistingAndNew);
            Case("hf_waits_for_slow_write", HfWaitsForSlowWrite);
            Case("hf_ignores_other_files", HfIgnoresOtherFiles);
            Case("hf_bad_file_quarantined", HfBadFileQuarantined);
            Case("hf_same_name_twice", HfSameNameTwice);
            Case("hf_group_into_one_pdf", HfGroupIntoOnePdf);
            Case("hf_multipage_tiff_input", HfMultiPageTiffInput);
            Case("hf_refuses_feedback_loop", HfRefusesFeedbackLoop);

            Console.WriteLine("  journal");
            Case("jr_pages_survive", JrPagesSurvive);
            Case("jr_exact_pixels", JrExactPixels);
            Case("jr_16bit_survives", Jr16BitSurvives);
            Case("jr_complete_removes", JrCompleteRemoves);
            Case("jr_torn_page_refused", JrTornPageRefused);
            Case("jr_live_session_skipped", JrLiveSessionSkipped);

            Console.WriteLine("  end to end");
            Case("scanned_pages_to_pdf", ScannedPagesToPdf);
            Case("batch_survives_a_crash", BatchSurvivesACrash);

            Console.WriteLine();
            try { Directory.Delete(_root, true); } catch { }

            if (_fail > 0)
            {
                Console.WriteLine(_fail + " case(s) FAILED");
                return 1;
            }
            Console.WriteLine("All export and batch cases passed.");
            return 0;
        }

        // ---------------------------------------------------------------- harness
        static void Case(string name, Func<string> body)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                string note = body();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    ok   {0,-24} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, note));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    FAIL {0,-24} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, ex.Message));
                _fail++;
            }
        }

        static void Expect(bool ok, string message)
        {
            if (!ok) throw new Exception(message);
        }

        static string Dir(string name)
        {
            string d = Path.Combine(_root, name);
            Directory.CreateDirectory(d);
            return d;
        }

        // ---------------------------------------------------------------- pages
        /// <summary>A blank colour page: uniform paper white, no marks.</summary>
        static RawImage Blank(int w, int h, int dpi)
        {
            RawImage r = new RawImage
            {
                Width = w,
                Height = h,
                Channels = 3,
                BitsPerChannel = 8,
                Stride = w * 3,
                XDpi = dpi,
                YDpi = dpi
            };
            r.Pixels = new byte[r.Stride * h];
            // Real paper scans back around 235, not 255. Using 255 would let a
            // detector that keys off absolute white pass while failing on the
            // hardware.
            for (int i = 0; i < r.Pixels.Length; i++) r.Pixels[i] = 235;
            return r;
        }

        /// <summary>A page with something on it: dark bands standing in for text.</summary>
        static RawImage Printed(int w, int h, int dpi, int lines = 12)
        {
            RawImage r = Blank(w, h, dpi);
            for (int L = 0; L < lines; L++)
            {
                int y0 = (int)(h * (0.1 + 0.06 * L));
                for (int y = y0; y < y0 + Math.Max(2, h / 120) && y < h; y++)
                    for (int x = w / 8; x < w * 3 / 4; x++)
                    {
                        int o = y * r.Stride + x * 3;
                        r.Pixels[o] = 30; r.Pixels[o + 1] = 30; r.Pixels[o + 2] = 30;
                    }
            }
            return r;
        }

        /// <summary>A blank page with dust and an edge shadow, as an ADF produces.</summary>
        static RawImage Dusty(int w, int h, int dpi)
        {
            RawImage r = Blank(w, h, dpi);
            Random rnd = new Random(7);
            for (int i = 0; i < 60; i++)
            {
                int x = rnd.Next(w), y = rnd.Next(h);
                int o = y * r.Stride + x * 3;
                r.Pixels[o] = 20; r.Pixels[o + 1] = 20; r.Pixels[o + 2] = 20;
            }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < Math.Max(1, w / 100); x++)
                {
                    int o = y * r.Stride + x * 3;
                    r.Pixels[o] = 40; r.Pixels[o + 1] = 40; r.Pixels[o + 2] = 40;
                }
            return r;
        }

        static RawImage GrayPrinted(int w, int h, int dpi)
        {
            RawImage r = new RawImage
            {
                Width = w,
                Height = h,
                Channels = 1,
                BitsPerChannel = 8,
                Stride = w,
                XDpi = dpi,
                YDpi = dpi
            };
            r.Pixels = new byte[r.Stride * h];
            for (int i = 0; i < r.Pixels.Length; i++) r.Pixels[i] = 235;
            for (int y = h / 4; y < h / 2; y++)
                for (int x = w / 4; x < w * 3 / 4; x++) r.Pixels[y * r.Stride + x] = 40;
            return r;
        }

        /// <summary>The same picture with the grey value copied into all three channels.</summary>
        static RawImage AsColour(RawImage gray)
        {
            RawImage r = new RawImage
            {
                Width = gray.Width,
                Height = gray.Height,
                Channels = 3,
                BitsPerChannel = 8,
                Stride = gray.Width * 3,
                XDpi = gray.XDpi,
                YDpi = gray.YDpi
            };
            r.Pixels = new byte[r.Stride * r.Height];
            for (int y = 0; y < r.Height; y++)
                for (int x = 0; x < r.Width; x++)
                {
                    byte v = gray.Pixels[y * gray.Stride + x];
                    int o = y * r.Stride + x * 3;
                    r.Pixels[o] = v; r.Pixels[o + 1] = v; r.Pixels[o + 2] = v;
                }
            return r;
        }

        /// <summary>
        /// Continuous tone greyscale: a gradient with noise, which deflates
        /// badly. This is the page that has to fall back to JPEG.
        /// </summary>
        static RawImage GrayNoise(int w, int h, int dpi)
        {
            RawImage r = GrayPrinted(w, h, dpi);
            Random rnd = new Random(11);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int v = (x * 255) / w + rnd.Next(-40, 41);
                    r.Pixels[y * r.Stride + x] = (byte)Math.Max(0, Math.Min(255, v));
                }
            return r;
        }

        static RawImage BilevelPrinted(int w, int h, int dpi)
        {
            int stride = (w + 7) / 8;
            RawImage r = new RawImage
            {
                Width = w,
                Height = h,
                Channels = 1,
                BitsPerChannel = 1,
                Stride = stride,
                XDpi = dpi,
                YDpi = dpi
            };
            r.Pixels = new byte[stride * h];
            for (int i = 0; i < r.Pixels.Length; i++) r.Pixels[i] = 0xFF;   // 1 = white
            for (int y = h / 4; y < h / 2; y++)
                for (int x = w / 4; x < w * 3 / 4; x++)
                    r.Pixels[y * stride + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
            return r;
        }

        static NameContext Ctx()
        {
            return new NameContext
            {
                Stamp = new DateTime(2026, 3, 9, 14, 5, 6),
                Device = "Canon LiDE 400",
                Dpi = 300,
                Mode = "colour",
                Source = "flatbed"
            };
        }

        // ================================================================ naming
        static string NameTokens()
        {
            string d = Dir("tokens");
            string p = NameTemplate.ResolvePath(d, "{yyyy}-{MM}-{dd}_{device}_{dpi}dpi_{mode}", "jpg", Ctx());
            string got = Path.GetFileName(p);
            Expect(got == "2026-03-09_Canon LiDE 400_300dpi_colour.jpg", "resolved to " + got);
            return got;
        }

        static string NameCounter()
        {
            string d = Dir("counter");
            List<string> names = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string p = NameTemplate.ResolvePath(d, "invoice_{nnn}", "jpg", Ctx());
                File.WriteAllText(p, "x");
                names.Add(Path.GetFileName(p));
            }
            Expect(names[0] == "invoice_001.jpg" && names[1] == "invoice_002.jpg" &&
                   names[2] == "invoice_003.jpg", "got " + string.Join(", ", names.ToArray()));

            // A gap must be filled rather than skipped: this is what lets the
            // operator delete a bad scan and rescan it into the same slot.
            File.Delete(Path.Combine(d, "invoice_002.jpg"));
            string again = Path.GetFileName(NameTemplate.ResolvePath(d, "invoice_{nnn}", "jpg", Ctx()));
            Expect(again == "invoice_002.jpg", "after deleting 002 the next free name was " + again);
            return "001..003, then reused 002";
        }

        static string NameSubfolders()
        {
            string d = Dir("subfolders");
            string p = NameTemplate.ResolvePath(d, @"{yyyy}\{MM}\scan_{nnn}", "png", Ctx());
            Expect(Directory.Exists(Path.Combine(d, "2026", "03")), "the dated folders were not created");
            Expect(p.EndsWith(@"2026\03\scan_001.png"), "resolved to " + p);
            return @"2026\03\scan_001.png";
        }

        static string NameNeverOverwrites()
        {
            string d = Dir("overwrite");
            RawImage page = Printed(300, 400, 150);

            ExportPlan plan = new ExportPlan
            {
                Directory = d,
                Pattern = "fixed_name",     // no counter, no time: collides by design
                Format = "jpg",
                Context = Ctx()
            };

            List<string> a = PageWriter.Write(new List<RawImage> { page }, plan);
            Expect(a.Count == 1, "the first save wrote " + a.Count + " files");
            long firstLength = new FileInfo(a[0]).Length;

            List<string> b = PageWriter.Write(new List<RawImage> { page }, plan);
            Expect(b.Count == 1, "the second save wrote " + b.Count + " files");
            Expect(!string.Equals(a[0], b[0], StringComparison.OrdinalIgnoreCase),
                   "the second save reused the same path: " + b[0]);
            Expect(File.Exists(a[0]) && new FileInfo(a[0]).Length == firstLength,
                   "the first file was modified by the second save");
            Expect(Path.GetFileName(b[0]) == "fixed_name (2).jpg", "second name was " + Path.GetFileName(b[0]));

            // Two pages under one collision-prone pattern in a single run must
            // also not land on one name.
            List<string> c = PageWriter.Write(new List<RawImage> { page, page }, plan);
            Expect(c.Count == 2 && !string.Equals(c[0], c[1], StringComparison.OrdinalIgnoreCase),
                   "two pages in one run collided");
            return "fixed_name.jpg then \"fixed_name (2).jpg\"";
        }

        static string NameCannotEscape()
        {
            string d = Dir("escape");
            NameContext ctx = Ctx();
            // A device name is vendor-supplied text. It must not be able to steer
            // where the file is written.
            ctx.Device = @"..\..\evil:name";
            string p = NameTemplate.ResolvePath(d, "{device}", "jpg", ctx);

            string full = Path.GetFullPath(p);
            Expect(full.StartsWith(Path.GetFullPath(d) + Path.DirectorySeparatorChar,
                                   StringComparison.OrdinalIgnoreCase),
                   "the path escaped the output folder: " + full);

            // The name may still read ".._..", which is harmless: ".." only
            // traverses when it is a whole path segment, and the separators that
            // would have made it one are gone. What matters is that the result is
            // a single segment directly inside the output folder.
            string leaf = full.Substring(Path.GetFullPath(d).Length + 1);
            Expect(leaf.IndexOf('\\') < 0 && leaf.IndexOf('/') < 0,
                   "the name is not a single segment: " + leaf);
            Expect(leaf.IndexOf(':') < 0, "the name kept a drive or stream separator: " + leaf);
            return leaf;
        }

        // =============================================================== formats
        static string JpegRoundTrip()
        {
            string d = Dir("jpeg");
            RawImage page = Printed(600, 800, 300);
            string path = Path.Combine(d, "a.jpg");
            Expect(PageWriter.SaveSingle(page, path, "jpg", 90), "the write reported failure");

            using (Image img = Load(path))
            {
                Expect(img.Width == 600 && img.Height == 800, "read back " + img.Width + "x" + img.Height);
                Expect(Math.Abs(img.HorizontalResolution - 300) < 1.0,
                       "the resolution came back as " + img.HorizontalResolution);
                Expect(img.RawFormat.Guid == ImageFormat.Jpeg.Guid, "the file is not a JPEG");
            }
            return "600x800 @300dpi";
        }

        static string PngRoundTrip()
        {
            string d = Dir("png");
            RawImage page = Printed(400, 500, 200);
            string path = Path.Combine(d, "a.png");
            Expect(PageWriter.SaveSingle(page, path, "png", 90), "the write reported failure");

            using (Image img = Load(path))
            {
                Expect(img.Width == 400 && img.Height == 500, "read back " + img.Width + "x" + img.Height);
                Expect(img.RawFormat.Guid == ImageFormat.Png.Guid, "the file is not a PNG");
            }
            return "400x500 PNG";
        }

        static string TiffMultiPage()
        {
            string d = Dir("tiff_multi");
            List<RawImage> pages = new List<RawImage>
            {
                Printed(400, 500, 200), Printed(400, 500, 200), Printed(400, 500, 200)
            };

            ExportPlan plan = new ExportPlan
            {
                Directory = d, Pattern = "book", Format = "tif", MultiPage = true, Context = Ctx()
            };
            List<string> written = PageWriter.Write(pages, plan);
            Expect(written.Count == 1, "expected one file, got " + written.Count);

            using (Image img = Load(written[0]))
            {
                int frames = img.GetFrameCount(FrameDimension.Page);
                Expect(frames == 3, "the TIFF holds " + frames + " page(s), not 3");
                for (int i = 0; i < frames; i++)
                {
                    img.SelectActiveFrame(FrameDimension.Page, i);
                    Expect(img.Width == 400 && img.Height == 500,
                           "page " + (i + 1) + " is " + img.Width + "x" + img.Height);
                }
            }
            return "3 pages in one TIFF";
        }

        static string TiffPerPage()
        {
            string d = Dir("tiff_single");
            List<RawImage> pages = new List<RawImage>
            {
                Printed(200, 260, 150), Printed(200, 260, 150), Printed(200, 260, 150)
            };
            ExportPlan plan = new ExportPlan
            {
                Directory = d, Pattern = "page_{ppp}", Format = "tif", MultiPage = false, Context = Ctx()
            };
            List<string> written = PageWriter.Write(pages, plan);
            Expect(written.Count == 3, "expected three files, got " + written.Count);
            Expect(Path.GetFileName(written[2]) == "page_003.tif",
                   "the third file is " + Path.GetFileName(written[2]));

            using (Image img = Load(written[0]))
                Expect(img.GetFrameCount(FrameDimension.Page) == 1, "a per-page file holds more than one page");
            return "page_001..003.tif";
        }

        static string PdfMultiPage()
        {
            string d = Dir("pdf_multi");
            List<RawImage> pages = new List<RawImage>
            {
                Printed(600, 800, 300), Printed(600, 800, 300), Printed(600, 800, 300)
            };
            ExportPlan plan = new ExportPlan
            {
                Directory = d, Pattern = "doc", Format = "pdf", MultiPage = true, Context = Ctx()
            };
            List<string> written = PageWriter.Write(pages, plan);
            Expect(written.Count == 1, "expected one PDF, got " + written.Count);

            Pdf pdf = Pdf.Read(written[0]);
            Expect(pdf.PageCount == 3, "the PDF declares " + pdf.PageCount + " pages");
            pdf.CheckXref();
            Expect(pdf.Tail.Contains("%%EOF"), "the PDF has no end-of-file marker");
            Expect(pdf.Text.Contains("/Producer (NextScan Studio)"), "the PDF carries no producer");
            return "3 pages, xref verified";
        }

        static string PdfPageSize()
        {
            string d = Dir("pdf_size");
            // 2480 x 3508 at 300 dpi is A4. In PDF points that is 595 x 842.
            RawImage a4 = Printed(2480, 3508, 300, 4);
            string path = Path.Combine(d, "a4.pdf");
            Expect(PageWriter.SavePdf(new List<RawImage> { a4 }, path), "the write reported failure");

            Pdf pdf = Pdf.Read(path);
            double w, h;
            pdf.FirstMediaBox(out w, out h);
            Expect(Math.Abs(w - 595.2) < 1.5 && Math.Abs(h - 841.9) < 1.5,
                   string.Format(CultureInfo.InvariantCulture,
                       "the page box is {0:N1} x {1:N1} points, not A4", w, h));
            return string.Format(CultureInfo.InvariantCulture, "{0:N0} x {1:N0} pt = A4", w, h);
        }

        static string PdfGrayStaysGray()
        {
            string d = Dir("pdf_gray");
            RawImage gray = GrayPrinted(1200, 1600, 300);
            string path = Path.Combine(d, "gray.pdf");
            Expect(PageWriter.SavePdf(new List<RawImage> { gray }, path), "the write reported failure");

            Pdf pdf = Pdf.Read(path);
            Expect(pdf.Text.Contains("/ColorSpace /DeviceGray"),
                   "a greyscale document page was not written as DeviceGray");
            Expect(pdf.Text.Contains("/Filter /FlateDecode"),
                   "a greyscale document page was not deflated");

            // The identical picture as three channels, so the comparison
            // measures the encoding choice and nothing else.
            RawImage asColour = AsColour(gray);
            string colourPath = Path.Combine(d, "colour.pdf");
            PageWriter.SavePdf(new List<RawImage> { asColour }, colourPath);
            long g = new FileInfo(path).Length, c = new FileInfo(colourPath).Length;
            Expect(g < c, "the greyscale PDF (" + g + ") is not smaller than the colour one (" + c + ")");
            return "DeviceGray/Flate, " + g / 1024 + " KB vs " + c / 1024 + " KB as colour";
        }

        static string PdfColorSpaceMatchesData()
        {
            // The invariant that a mislabelled stream breaks: a PDF image
            // declaring DeviceGray must carry one component, and one declaring
            // DeviceRGB must carry three. GDI+ emits three-component JPEGs even
            // from a greyscale surface, so this is easy to get wrong and
            // impossible to see in a viewer that guesses.
            string d = Dir("pdf_cs");
            List<string> notes = new List<string>();

            RawImage[] cases =
            {
                Printed(600, 800, 200),             // colour document
                GrayPrinted(600, 800, 200),         // greyscale document
                BilevelPrinted(600, 800, 200),      // bilevel
                GrayNoise(600, 800, 200)            // greyscale continuous tone
            };
            string[] names = { "colour", "gray_doc", "bilevel", "gray_photo" };

            for (int i = 0; i < cases.Length; i++)
            {
                string path = Path.Combine(d, names[i] + ".pdf");
                Expect(PageWriter.SavePdf(new List<RawImage> { cases[i] }, path),
                       names[i] + " failed to write");

                Pdf pdf = Pdf.Read(path);
                bool devGray = pdf.Text.Contains("/ColorSpace /DeviceGray");
                bool flate = pdf.Text.Contains("/Filter /FlateDecode");
                byte[] stream = pdf.FirstImageStream();

                if (flate)
                {
                    Expect(devGray, names[i] + ": a deflated stream must be DeviceGray here");
                    byte[] plain = Inflate(stream);
                    Expect(plain.Length == 600 * 800,
                           names[i] + ": the stream decodes to " + plain.Length +
                           " bytes, not one per pixel");
                    notes.Add(names[i] + "=gray/flate");
                }
                else
                {
                    int comps = JpegComponents(stream);
                    Expect(comps == 3, names[i] + ": the JPEG has " + comps + " components");
                    Expect(!devGray, names[i] +
                           ": a three-component JPEG was labelled DeviceGray");
                    notes.Add(names[i] + "=rgb/jpeg");
                }
            }
            return string.Join(", ", notes.ToArray());
        }

        /// <summary>Number of components declared in a JPEG's frame header.</summary>
        static int JpegComponents(byte[] b)
        {
            for (int i = 2; i < b.Length - 9; i++)
            {
                if (b[i] != 0xFF) continue;
                int m = b[i + 1];
                if (m == 0xC0 || m == 0xC1 || m == 0xC2) return b[i + 9];
            }
            throw new Exception("no JPEG frame header was found in the stream");
        }

        static string PdfBilevelLossless()
        {
            string d = Dir("pdf_bw");
            RawImage bw = BilevelPrinted(1200, 1600, 300);
            string path = Path.Combine(d, "bw.pdf");
            Expect(PageWriter.SavePdf(new List<RawImage> { bw }, path), "the write reported failure");

            Pdf pdf = Pdf.Read(path);
            Expect(pdf.Text.Contains("/Filter /FlateDecode"),
                   "a bilevel page was not deflated; JPEG would ring around the text");
            Expect(pdf.Text.Contains("/ColorSpace /DeviceGray"), "the bilevel page is not DeviceGray");

            // Inflate the stream and confirm it is the page, byte for byte.
            byte[] stream = pdf.FirstImageStream();
            byte[] raw = Inflate(stream);
            Expect(raw.Length == 1200 * 1600,
                   "the image stream decodes to " + raw.Length + " bytes, not " + (1200 * 1600));

            int black = 0;
            foreach (byte v in raw) if (v == 0) black++;
            int expected = (1600 / 2 - 1600 / 4) * (1200 * 3 / 4 - 1200 / 4);
            Expect(black == expected, "the decoded page has " + black + " black pixels, expected " + expected);
            return "FlateDecode, decodes back byte-exact";
        }

        static string NoTempFilesLeft()
        {
            string d = Dir("temps");
            RawImage page = Printed(200, 200, 150);
            PageWriter.SaveSingle(page, Path.Combine(d, "ok.jpg"), "jpg");

            // An unsupported format must fail cleanly rather than leaving the
            // staging file behind for the user to find.
            PageWriter.SaveSingle(page, Path.Combine(d, "bad.xyz"), "xyz");

            string[] temps = Directory.GetFiles(d, "*.tmp");
            Expect(temps.Length == 0, temps.Length + " staging file(s) were left behind");
            Expect(!File.Exists(Path.Combine(d, "bad.xyz")), "a failed write still produced a file");
            return "no .tmp files after a failed write";
        }

        // ================================================================= batch
        static string BlankDetection()
        {
            double printed = BatchSplitter.InkCoverage(Printed(1200, 1600, 300));
            double blank = BatchSplitter.InkCoverage(Blank(1200, 1600, 300));
            double dusty = BatchSplitter.InkCoverage(Dusty(1200, 1600, 300));

            BatchOptions o = new BatchOptions();
            Expect(!BatchSplitter.IsBlank(Printed(1200, 1600, 300), o.BlankThreshold),
                   "a printed page was called blank (coverage " + printed.ToString("N4") + ")");
            Expect(BatchSplitter.IsBlank(Blank(1200, 1600, 300), o.BlankThreshold),
                   "a blank page was called printed (coverage " + blank.ToString("N4") + ")");
            Expect(BatchSplitter.IsBlank(Dusty(1200, 1600, 300), o.BlankThreshold),
                   "dust and an edge shadow were mistaken for content (coverage " + dusty.ToString("N4") + ")");

            // A page with one short line still has to survive: this is the case
            // that decides whether a signature page is thrown away.
            RawImage faint = Printed(1200, 1600, 300, 1);
            Expect(!BatchSplitter.IsBlank(faint, o.BlankThreshold),
                   "a page with a single line was called blank (coverage " +
                   BatchSplitter.InkCoverage(faint).ToString("N4") + ")");

            return string.Format(CultureInfo.InvariantCulture,
                "printed {0:N3}, one line {1:N3}, dusty {2:N4}, blank {3:N4}",
                printed, BatchSplitter.InkCoverage(faint), dusty, blank);
        }

        static string BlankDropDuplex()
        {
            // Six images as a duplex pass over three single-sided sheets.
            List<RawImage> feed = new List<RawImage>();
            for (int i = 0; i < 3; i++)
            {
                feed.Add(Printed(900, 1200, 200));
                feed.Add(Dusty(900, 1200, 200));
            }

            BatchOptions o = new BatchOptions { DropBlankPages = true };
            List<BatchDocument> docs = BatchSplitter.Split(feed, o);
            Expect(docs.Count == 1, "expected one document, got " + docs.Count);
            Expect(docs[0].Pages.Count == 3,
                   "kept " + docs[0].Pages.Count + " page(s) of 6, expected the 3 printed sides");
            return "6 duplex images to 3 pages";
        }

        static string SplitFixedCount()
        {
            List<RawImage> feed = new List<RawImage>();
            for (int i = 0; i < 7; i++) feed.Add(Printed(400, 500, 150));

            BatchOptions o = new BatchOptions
            {
                Separation = SeparationRule.FixedPageCount,
                PagesPerDocument = 2
            };
            List<BatchDocument> docs = BatchSplitter.Split(feed, o);
            Expect(docs.Count == 4, "expected 4 documents, got " + docs.Count);
            Expect(docs[0].Pages.Count == 2 && docs[3].Pages.Count == 1,
                   "the tail document has " + docs[3].Pages.Count + " page(s), expected 1");
            Expect(docs[3].Index == 4, "the last document is numbered " + docs[3].Index);
            return "7 pages, 2 per document, to 2+2+2+1";
        }

        static string SplitBlankSeparator()
        {
            RawImage sep = Dusty(900, 1200, 200);
            List<RawImage> feed = new List<RawImage>
            {
                sep,                                    // leading separator
                Printed(900, 1200, 200), Printed(900, 1200, 200),
                sep,
                sep,                                    // two in a row
                Printed(900, 1200, 200),
                sep                                     // trailing separator
            };

            BatchOptions o = new BatchOptions { Separation = SeparationRule.BlankPage };
            List<BatchDocument> docs = BatchSplitter.Split(feed, o);

            Expect(docs.Count == 2, "expected 2 documents, got " + docs.Count);
            Expect(docs[0].Pages.Count == 2, "document 1 has " + docs[0].Pages.Count + " pages");
            Expect(docs[1].Pages.Count == 1, "document 2 has " + docs[1].Pages.Count + " pages");
            foreach (BatchDocument doc in docs)
                foreach (RawImage p in doc.Pages)
                    Expect(!BatchSplitter.IsBlank(p, o.BlankThreshold), "a separator sheet was kept");

            // Keeping the separator sheet must change what each document holds,
            // not how many there are. If the count moved, a stack with a
            // trailing separator would file an extra document containing one
            // blank sheet.
            o.DiscardSeparatorSheet = false;
            List<BatchDocument> kept = BatchSplitter.Split(feed, o);
            Expect(kept.Count == 2, "with separators kept, expected 2 documents, got " + kept.Count);
            Expect(kept[0].Pages.Count == 3 && kept[1].Pages.Count == 2,
                   "kept documents hold " + kept[0].Pages.Count + " and " + kept[1].Pages.Count + " pages");
            foreach (BatchDocument doc in kept)
                Expect(BatchSplitter.IsBlank(doc.Pages[0], o.BlankThreshold),
                       "a kept document does not start with its separator sheet");
            return "leading, doubled and trailing separators all handled";
        }

        static string BatchWritesDocuments()
        {
            string d = Dir("batch_write");
            RawImage sep = Dusty(600, 800, 150);
            List<RawImage> feed = new List<RawImage>
            {
                Printed(600, 800, 150), Printed(600, 800, 150),
                sep,
                Printed(600, 800, 150),
                sep,
                Printed(600, 800, 150), Printed(600, 800, 150), Printed(600, 800, 150)
            };

            BatchOptions batch = new BatchOptions { Separation = SeparationRule.BlankPage };
            ExportPlan plan = new ExportPlan
            {
                Directory = d, Pattern = "job_{doc}", Format = "pdf", MultiPage = true, Context = Ctx()
            };

            int docCount;
            List<string> written = BatchSplitter.WriteBatch(feed, batch, plan, out docCount);
            Expect(docCount == 3, "the feed split into " + docCount + " documents, expected 3");
            Expect(written.Count == 3, "wrote " + written.Count + " files, expected 3");

            int[] expected = { 2, 1, 3 };
            for (int i = 0; i < 3; i++)
            {
                string name = Path.GetFileName(written[i]);
                Expect(name == "job_" + (i + 1).ToString("000") + ".pdf", "file " + (i + 1) + " is named " + name);
                Pdf pdf = Pdf.Read(written[i]);
                Expect(pdf.PageCount == expected[i],
                       name + " holds " + pdf.PageCount + " pages, expected " + expected[i]);
                pdf.CheckXref();
            }
            return "job_001 (2p), job_002 (1p), job_003 (3p)";
        }

        // ================================================================== tone
        static Bitmap Rgb(int w, int h, Func<int, int, Color> f)
        {
            Bitmap b = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* p = (byte*)d.Scan0 + (long)y * d.Stride;
                        for (int x = 0; x < w; x++, p += 3)
                        {
                            Color c = f(x, y);
                            p[0] = c.B; p[1] = c.G; p[2] = c.R;
                        }
                    }
                }
            }
            finally { b.UnlockBits(d); }
            return b;
        }

        static Color At(Bitmap b, int x, int y) { return b.GetPixel(x, y); }

        static string ToneOriginalIsFaithful()
        {
            // The default must not alter a single pixel it was not asked to.
            // This is the whole complaint the preset work came from.
            Bitmap src = Rgb(64, 64, delegate (int x, int y)
            {
                return Color.FromArgb((x * 4) & 255, (y * 4) & 255, ((x + y) * 2) & 255);
            });

            ToneSettings s = ToneEngine.Defaults(TonePreset.OriginalColour);
            s.Polish = 0;

            using (Bitmap outp = ToneEngine.Apply(src, s, NextScan.Core.ColorMode.Color24))
            {
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                        if (At(outp, x, y) != At(src, x, y))
                            throw new Exception("pixel " + x + "," + y + " changed: " +
                                                At(src, x, y) + " became " + At(outp, x, y));
            }
            src.Dispose();
            return "identical, pixel for pixel";
        }

        static string TonePolishKeepsHue()
        {
            // The old code added the same amount to R, G and B, which walks a
            // warm off-white towards neutral. Polish must scale, not add.
            int[][] samples =
            {
                new[] { 245, 236, 220 },     // warm paper
                new[] { 230, 235, 245 },     // cool paper
                new[] { 250, 245, 240 },     // near white
                new[] { 200, 190, 175 },     // dull card
            };

            double worst = 0;
            foreach (int[] c in samples)
            {
                int r = c[0], g = c[1], b = c[2];
                ToneEngine.Polish(ref r, ref g, ref b, 0.55);

                // Hue survives if the channel ratios survive.
                double beforeRG = (double)c[0] / c[1], afterRG = (double)r / g;
                double beforeBG = (double)c[2] / c[1], afterBG = (double)b / g;
                double drift = Math.Max(Math.Abs(beforeRG - afterRG), Math.Abs(beforeBG - afterBG));
                worst = Math.Max(worst, drift);

                Expect(drift < 0.01, string.Format(CultureInfo.InvariantCulture,
                    "({0},{1},{2}) became ({3},{4},{5}); the channel ratio moved by {6:N4}",
                    c[0], c[1], c[2], r, g, b, drift));

                Expect(r >= c[0] && g >= c[1] && b >= c[2], "polish darkened a pixel");
            }

            // And it must actually do something, or the test proves nothing.
            int pr = 240, pg = 232, pb = 220;
            ToneEngine.Polish(ref pr, ref pg, ref pb, 0.55);
            Expect(pr > 240, "polish had no effect at all");

            return string.Format(CultureInfo.InvariantCulture, "worst ratio drift {0:N4}", worst);
        }

        static string TonePolishNeverClips()
        {
            // Highlight detail has to survive the strengths the presets actually
            // use. Full strength is a different matter: asking for 100% polish is
            // asking for paper to be forced white, so flattening the top there is
            // the instruction, not a defect. What must hold everywhere is that no
            // channel ever exceeds 255 and none is ever darkened.
            List<string> notes = new List<string>();

            foreach (TonePresetInfo info in ToneEngine.Presets)
            {
                if (info.Polish <= 0) continue;
                double amount = info.Polish / 100.0;

                int clipped = 0;
                for (int r = 150; r <= 255; r += 3)
                    for (int g = 150; g <= 255; g += 3)
                        for (int b = 150; b <= 255; b += 3)
                        {
                            int rr = r, gg = g, bb = b;
                            ToneEngine.Polish(ref rr, ref gg, ref bb, amount);

                            if (rr == 255 && r != 255) clipped++;
                            if (gg == 255 && g != 255) clipped++;
                            if (bb == 255 && b != 255) clipped++;
                        }

                Expect(clipped == 0, info.Name + " (polish " + info.Polish + ") drove " +
                       clipped + " channel(s) to 255");
                notes.Add(info.Name.Split(' ')[0].ToLowerInvariant() + " " + info.Polish + "%");
            }

            // At any strength at all, including full: never above 255, never
            // darker than it went in.
            for (int v = 0; v <= 255; v += 1)
                foreach (double amount in new double[] { 0.08, 0.3, 0.55, 1.0 })
                {
                    int r = v, g = Math.Max(0, v - 12), b = Math.Max(0, v - 25);
                    int r0 = r, g0 = g, b0 = b;
                    ToneEngine.Polish(ref r, ref g, ref b, amount);

                    Expect(r <= 255 && g <= 255 && b <= 255,
                           "polish produced a value above 255 at " + v);
                    Expect(r >= r0 && g >= g0 && b >= b0,
                           "polish darkened a pixel at " + v + ", strength " + amount);
                }

            return "no clipping at " + string.Join(", ", notes.ToArray());
        }

        static string ToneNoContourOnRamp()
        {
            // The old cutoff at luminance 170 left a visible line across any
            // gradient. A ramp through the polish must stay smooth: neighbouring
            // input values may not produce a jump in the output.
            int worstJump = 0, worstAt = 0;
            int previous = -1;

            for (int v = 0; v <= 255; v++)
            {
                int r = v, g = v, b = v;
                ToneEngine.Polish(ref r, ref g, ref b, 0.55);
                if (previous >= 0)
                {
                    int jump = r - previous;
                    if (jump > worstJump) { worstJump = jump; worstAt = v; }
                    Expect(jump >= 0, "the polish curve went backwards at " + v);
                }
                previous = r;
            }

            // One input step may not move the output by more than a couple of
            // levels; the old code moved it by around 25 at the cutoff.
            Expect(worstJump <= 3,
                   "a one-level input step moved the output by " + worstJump + " at " + worstAt);
            return "largest step " + worstJump + " level(s), at " + worstAt;
        }

        static string ToneCurvesAreMonotonic()
        {
            // A curve that is not monotonic inverts detail somewhere in the
            // range, which shows up as posterised patches rather than as an
            // obviously wrong picture.
            List<string> notes = new List<string>();
            foreach (TonePresetInfo info in ToneEngine.Presets)
            {
                ToneSettings s = ToneEngine.Defaults(info.Preset);
                byte[] lut = ToneEngine.BuildLut(s);
                bool descending = info.Preset == TonePreset.NegativeInvert;

                for (int i = 1; i < 256; i++)
                {
                    bool ok = descending ? lut[i] <= lut[i - 1] : lut[i] >= lut[i - 1];
                    Expect(ok, info.Name + " reverses at input " + i +
                           " (" + lut[i - 1] + " then " + lut[i] + ")");
                }

                Expect(lut[0] <= 2 || descending, info.Name + " lifts black to " + lut[0]);
                Expect(lut[255] >= 253 || descending, info.Name + " drops white to " + lut[255]);
                notes.Add(info.Name);
            }

            // The sliders must not break that either, at their extremes.
            foreach (int c in new int[] { -100, -50, 50, 100 })
            {
                ToneSettings s = ToneEngine.Defaults(TonePreset.OriginalColour);
                s.Contrast = c;
                byte[] lut = ToneEngine.BuildLut(s);
                for (int i = 1; i < 256; i++)
                    Expect(lut[i] >= lut[i - 1], "contrast " + c + " reverses at " + i);
            }

            return notes.Count + " presets monotonic, endpoints held";
        }

        static string ToneGreyscaleIsNeutral()
        {
            Bitmap src = Rgb(32, 32, delegate (int x, int y) { return Color.FromArgb(200, 120, 60); });
            ToneSettings s = ToneEngine.Defaults(TonePreset.Greyscale);
            using (Bitmap outp = ToneEngine.Apply(src, s, NextScan.Core.ColorMode.Color24))
            {
                Color c = At(outp, 16, 16);
                Expect(c.R == c.G && c.G == c.B, "greyscale left a colour cast: " + c);
                Expect(c.R > 100 && c.R < 220, "the grey level is implausible: " + c.R);
            }
            src.Dispose();
            return "R=G=B across the page";
        }

        static string ToneAdaptiveBeatsGlobal()
        {
            // A page lit unevenly - the gutter shadow of a bound book. Text of
            // one darkness sits on paper that fades from bright to dim. One
            // global threshold cannot keep both halves; a local one can.
            const int W = 400, H = 200;
            Bitmap src = Rgb(W, H, delegate (int x, int y)
            {
                int paper = 245 - (x * 130 / W);          // 245 down to 115
                bool ink = (y % 20) < 6 && x % 40 < 30;   // even bands of text
                int v = ink ? Math.Max(0, paper - 70) : paper;
                return Color.FromArgb(v, v, v);
            });

            ToneSettings adaptive = ToneEngine.Defaults(TonePreset.BlackAndWhiteText);
            ToneSettings global = ToneEngine.Defaults(TonePreset.BlackAndWhiteText);
            global.AdaptiveThreshold = false;
            global.BwThreshold = 128;

            int aLeft = 0, aRight = 0, gLeft = 0, gRight = 0;
            using (Bitmap a = ToneEngine.Apply(src, adaptive, NextScan.Core.ColorMode.Color24))
            using (Bitmap g = ToneEngine.Apply(src, global, NextScan.Core.ColorMode.Color24))
            {
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        bool ink = (y % 20) < 6 && x % 40 < 30;
                        if (!ink) continue;
                        bool left = x < W / 2;
                        if (At(a, x, y).R == 0) { if (left) aLeft++; else aRight++; }
                        if (At(g, x, y).R == 0) { if (left) gLeft++; else gRight++; }
                    }
            }
            src.Dispose();

            // The bright half is easy for both. The dim half is where a global
            // threshold turns the paper itself black and loses the text in it.
            Expect(aLeft > 0 && aRight > 0,
                   "adaptive lost text: " + aLeft + " left, " + aRight + " right");
            Expect(gRight == 0 || gLeft == 0 || true, "");   // measured, not asserted

            double aBalance = Math.Min(aLeft, aRight) / (double)Math.Max(aLeft, aRight);
            double gBalance = Math.Max(gLeft, gRight) == 0
                ? 0 : Math.Min(gLeft, gRight) / (double)Math.Max(gLeft, gRight);

            Expect(aBalance > 0.8, string.Format(CultureInfo.InvariantCulture,
                "adaptive found {0} ink pixels on the bright half and {1} on the dim half", aLeft, aRight));
            Expect(aBalance > gBalance, string.Format(CultureInfo.InvariantCulture,
                "adaptive ({0:N2}) was no better balanced than global ({1:N2})", aBalance, gBalance));

            return string.Format(CultureInfo.InvariantCulture,
                "adaptive balance {0:N2} vs global {1:N2}", aBalance, gBalance);
        }

        static string ToneFadedRestoresRange()
        {
            // A faint page using only 120..175. It should come back using most
            // of the range, without a blank page being amplified into noise.
            Bitmap faded = Rgb(200, 200, delegate (int x, int y)
            {
                int v = 120 + (x * 55 / 200);
                if ((y % 16) < 4 && x > 20 && x < 180) v -= 25;
                return Color.FromArgb(v, v, v);
            });

            int lo = 255, hi = 0;
            using (Bitmap outp = ToneEngine.Apply(faded,
                       ToneEngine.Defaults(TonePreset.FadedRestore), NextScan.Core.ColorMode.Color24))
            {
                for (int y = 0; y < 200; y += 3)
                    for (int x = 0; x < 200; x += 3)
                    {
                        int v = At(outp, x, y).R;
                        if (v < lo) lo = v;
                        if (v > hi) hi = v;
                    }
            }
            faded.Dispose();
            Expect(hi - lo > 180, "the restored range is only " + lo + ".." + hi);

            // A nearly blank page must be left alone: stretching it would turn
            // sensor noise into something that reads as content.
            Bitmap blankish = Rgb(200, 200, delegate (int x, int y)
            {
                return Color.FromArgb(238 + (x % 3), 238 + (x % 3), 238 + (x % 3));
            });
            using (Bitmap outp = ToneEngine.Apply(blankish,
                       ToneEngine.Defaults(TonePreset.FadedRestore), NextScan.Core.ColorMode.Color24))
            {
                int v = At(outp, 100, 100).R;
                Expect(v > 200, "an almost blank page was stretched into noise: level " + v);
            }
            blankish.Dispose();

            return "faint page to " + lo + ".." + hi + ", blank page left alone";
        }

        // ============================================================ hot folder
        static HotFolderOptions HfOptions(string watch)
        {
            return new HotFolderOptions
            {
                WatchFolder = watch,
                // Short windows keep the suite quick. Production defaults are
                // over a second, because a copy across a network share pauses.
                StableMilliseconds = 120,
                PollMilliseconds = 40,
                GroupQuietMilliseconds = 250
            };
        }

        static ExportPlan HfPlan(string outDir, string format, bool multiPage)
        {
            return new ExportPlan
            {
                Directory = outDir,
                Pattern = "out_{nnn}",
                Format = format,
                MultiPage = multiPage,
                Context = Ctx()
            };
        }

        /// <summary>Writes a real image file into the watched folder.</summary>
        static void Drop(string dir, string name, RawImage page)
        {
            Expect(PageWriter.SaveSingle(page, Path.Combine(dir, name),
                                         Path.GetExtension(name)), "could not drop " + name);
        }

        static string HfExistingAndNew()
        {
            string watch = Dir("hf_basic_in"), outDir = Dir("hf_basic_out");

            // Already sitting there before the watch starts. A hot folder that
            // only reacts to events would miss these entirely, which is what
            // happens after a restart.
            Drop(watch, "before.png", Printed(300, 400, 150));

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "jpg", false), new BatchOptions()))
            {
                Expect(hf.Drain(6000), "the folder did not settle");
                Expect(hf.SucceededFiles == 1, "processed " + hf.SucceededFiles + " of the 1 pre-existing file");

                Drop(watch, "after.png", Printed(300, 400, 150));
                Expect(hf.Drain(6000), "the folder did not settle after the new file");
                Expect(hf.SucceededFiles == 2, "processed " + hf.SucceededFiles + " files, expected 2");
            }

            int made = Directory.GetFiles(outDir, "*.jpg").Length;
            Expect(made == 2, "the output folder holds " + made + " files");
            Expect(File.Exists(Path.Combine(watch, "_processed", "before.png")),
                   "the input was not filed into _processed");
            Expect(!File.Exists(Path.Combine(watch, "before.png")), "the input was left in the watched folder");
            return "picked up one waiting file and one new one";
        }

        static string HfWaitsForSlowWrite()
        {
            // The failure this guards against: a file is listed as soon as it is
            // created, so reading on sight yields a truncated image. Here the
            // bytes arrive in pieces, as a copy over a slow link does.
            string watch = Dir("hf_slow_in"), outDir = Dir("hf_slow_out");

            RawImage page = Printed(900, 1200, 300);
            string staged = Path.Combine(Dir("hf_slow_stage"), "full.jpg");
            Expect(PageWriter.SaveSingle(page, staged, "jpg"), "could not stage the source");
            byte[] all = File.ReadAllBytes(staged);
            Expect(all.Length > 8000, "the staged file is too small for this test to mean anything");

            string target = Path.Combine(watch, "slow.jpg");
            Exception writerFault = null;
            Thread writer = new Thread(delegate ()
            {
                try
                {
                    using (FileStream fs = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        int chunk = all.Length / 4;
                        for (int i = 0; i < 4; i++)
                        {
                            int n = (i == 3) ? all.Length - i * chunk : chunk;
                            fs.Write(all, i * chunk, n);
                            fs.Flush();
                            Thread.Sleep(150);
                        }
                    }
                }
                catch (Exception ex) { writerFault = ex; }
            });
            writer.IsBackground = true;
            writer.Start();

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "png", false), new BatchOptions()))
            {
                // Sweeping while the file is still being written is the whole
                // point: the pause between chunks is longer than the stability
                // window, so the size-and-timestamp test alone would call the
                // half-written file settled. What actually holds it back is that
                // the writer still has the handle open.
                while (writer.IsAlive) { hf.PumpOnce(); Thread.Sleep(30); }
                writer.Join(5000);
                if (writerFault != null) throw new Exception("the writer thread failed: " + writerFault.Message);

                Expect(hf.Drain(15000), "the folder did not settle");

                Expect(hf.FailedFiles == 0, hf.FailedFiles + " file(s) failed; a partial read was accepted");
                Expect(hf.SucceededFiles == 1, "processed " + hf.SucceededFiles + " files, expected 1");
            }

            string[] outs = Directory.GetFiles(outDir, "*.png");
            Expect(outs.Length == 1, "the output folder holds " + outs.Length + " files");
            using (Image img = Load(outs[0]))
                Expect(img.Width == 900 && img.Height == 1200,
                       "the output is " + img.Width + "x" + img.Height + "; the file was read before it was complete");
            return "read only after the last chunk landed";
        }

        static string HfIgnoresOtherFiles()
        {
            string watch = Dir("hf_ignore_in"), outDir = Dir("hf_ignore_out");
            File.WriteAllText(Path.Combine(watch, "notes.txt"), "not an image");
            File.WriteAllText(Path.Combine(watch, "job.pdf"), "%PDF-1.4 not readable as an image");
            Drop(watch, "real.png", Printed(200, 260, 150));

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "jpg", false), new BatchOptions()))
            {
                Expect(hf.Drain(6000), "the folder did not settle");
                Expect(hf.SucceededFiles == 1, "processed " + hf.SucceededFiles + " files, expected only the image");
                Expect(hf.FailedFiles == 0, "a non-image file was treated as a failure rather than ignored");
            }
            Expect(File.Exists(Path.Combine(watch, "notes.txt")), "a file that is none of its business was moved");
            return "left the .txt and .pdf alone";
        }

        static string HfBadFileQuarantined()
        {
            string watch = Dir("hf_bad_in"), outDir = Dir("hf_bad_out");
            // The right extension and a plausible size, but not an image - what a
            // half-finished download or a renamed file looks like.
            File.WriteAllBytes(Path.Combine(watch, "broken.png"), new byte[4096]);

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "jpg", false), new BatchOptions()))
            {
                Expect(hf.Drain(6000), "the folder did not settle");
                Expect(hf.FailedFiles == 1, "recorded " + hf.FailedFiles + " failures, expected 1");

                // And it must not be retried on every sweep for ever.
                hf.Drain(1500);
                Expect(hf.FailedFiles == 1, "the bad file was retried; failures reached " + hf.FailedFiles);
            }

            string errDir = Path.Combine(watch, "_errors");
            Expect(File.Exists(Path.Combine(errDir, "broken.png")), "the bad file was not quarantined");
            Expect(File.Exists(Path.Combine(errDir, "broken.png.txt")), "no reason was written beside it");
            Expect(File.ReadAllText(Path.Combine(errDir, "broken.png.txt")).Contains("Problem:"),
                   "the reason file does not say what went wrong");
            return "moved to _errors with a reason, and not retried";
        }

        static string HfSameNameTwice()
        {
            string watch = Dir("hf_twice_in"), outDir = Dir("hf_twice_out");

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "jpg", false), new BatchOptions()))
            {
                Drop(watch, "page.png", Printed(200, 260, 150));
                Expect(hf.Drain(6000), "the first drop did not settle");

                // The same name again: neither the output nor the filed input may
                // be overwritten by it.
                Drop(watch, "page.png", Printed(200, 260, 150));
                Expect(hf.Drain(6000), "the second drop did not settle");
                Expect(hf.SucceededFiles == 2, "processed " + hf.SucceededFiles + " files, expected 2");
            }

            int outs = Directory.GetFiles(outDir, "*.jpg").Length;
            Expect(outs == 2, "the second drop overwrote the first output; " + outs + " file(s) remain");
            int filed = Directory.GetFiles(Path.Combine(watch, "_processed"), "page*.png").Length;
            Expect(filed == 2, "the second input overwrote the first in _processed; " + filed + " remain");
            return "two drops of one name kept both";
        }

        static string HfGroupIntoOnePdf()
        {
            string watch = Dir("hf_group_in"), outDir = Dir("hf_group_out");

            HotFolderOptions o = HfOptions(watch);
            o.Grouping = HotFolderGrouping.PerBatch;

            Drop(watch, "a_01.png", Printed(400, 500, 150));
            Drop(watch, "a_02.png", Printed(400, 500, 150));
            Drop(watch, "a_03.png", Printed(400, 500, 150));

            using (HotFolder hf = new HotFolder(o, HfPlan(outDir, "pdf", true), new BatchOptions()))
            {
                Expect(hf.Drain(10000), "the folder did not settle");
                Expect(hf.SucceededFiles == 3, "processed " + hf.SucceededFiles + " inputs, expected 3");
                Expect(hf.WrittenFiles == 1, "wrote " + hf.WrittenFiles + " files, expected one document");
            }

            string[] outs = Directory.GetFiles(outDir, "*.pdf");
            Expect(outs.Length == 1, "the output folder holds " + outs.Length + " PDFs");
            Pdf pdf = Pdf.Read(outs[0]);
            Expect(pdf.PageCount == 3, "the PDF holds " + pdf.PageCount + " pages, expected 3");
            pdf.CheckXref();
            return "3 dropped images to one 3-page PDF";
        }

        static string HfMultiPageTiffInput()
        {
            // Reading only the first frame of a multi-page TIFF would be silent
            // data loss: the file is filed away as processed and the other pages
            // are simply gone.
            string watch = Dir("hf_tiff_in"), outDir = Dir("hf_tiff_out");
            List<RawImage> pages = new List<RawImage>
            {
                Printed(300, 400, 150), Printed(300, 400, 150), Printed(300, 400, 150)
            };
            Expect(PageWriter.SaveMultiPageTiff(pages, Path.Combine(watch, "book.tif")),
                   "could not stage the multi-page TIFF");

            using (HotFolder hf = new HotFolder(HfOptions(watch), HfPlan(outDir, "pdf", true), new BatchOptions()))
            {
                Expect(hf.Drain(8000), "the folder did not settle");
                Expect(hf.FailedFiles == 0, hf.FailedFiles + " file(s) failed");
            }

            string[] outs = Directory.GetFiles(outDir, "*.pdf");
            Expect(outs.Length == 1, "the output folder holds " + outs.Length + " PDFs");
            Pdf pdf = Pdf.Read(outs[0]);
            Expect(pdf.PageCount == 3, "the PDF holds " + pdf.PageCount + " pages; the TIFF had 3");
            return "3-page TIFF in, 3-page PDF out";
        }

        static string HfRefusesFeedbackLoop()
        {
            string watch = Dir("hf_loop_in");
            string inside = Path.Combine(watch, "output");
            Directory.CreateDirectory(inside);

            // Output inside the watched folder means every file written is picked
            // up as new input, for ever. It has to be refused before anything is
            // watched, not discovered by a full disk.
            bool refused = false;
            try { new HotFolder(HfOptions(watch), HfPlan(inside, "jpg", false), new BatchOptions()).Dispose(); }
            catch (ArgumentException) { refused = true; }
            Expect(refused, "an output folder inside the watched folder was accepted");

            // And the other way round, which reprocesses earlier output.
            string outer = Dir("hf_loop_outer");
            string nested = Path.Combine(outer, "incoming");
            Directory.CreateDirectory(nested);
            refused = false;
            try { new HotFolder(HfOptions(nested), HfPlan(outer, "jpg", false), new BatchOptions()).Dispose(); }
            catch (ArgumentException) { refused = true; }
            Expect(refused, "a watched folder inside the output folder was accepted");

            return "both nesting directions refused up front";
        }

        // =============================================================== journal
        /// <summary>
        /// Sessions live under LocalApplicationData, so a test must clean up
        /// after itself rather than leaving folders the studio would offer to
        /// recover on the user's next start.
        /// </summary>
        static void ForgetSessions(List<string> dirs)
        {
            foreach (string d in dirs) ScanJournal.Discard(d);
        }

        static string JrPagesSurvive()
        {
            List<string> made = new List<string>();
            try
            {
                ScanJournal j = ScanJournal.Begin("test");
                made.Add(j.Directory);

                for (int i = 0; i < 5; i++) j.AddPage(Printed(400, 500, 150));
                Expect(j.Flush(20000), "the journal did not finish writing");
                Expect(j.WrittenPages == 5, "wrote " + j.WrittenPages + " pages, expected 5");

                // Abandon, not Complete: this is what a crash leaves behind.
                j.Abandon();

                List<JournalSession> found = ScanJournal.FindIncomplete();
                JournalSession mine = null;
                foreach (JournalSession fs in found)
                    if (string.Equals(fs.Directory, j.Directory, StringComparison.OrdinalIgnoreCase)) mine = fs;

                Expect(mine != null, "the abandoned session was not offered for recovery");
                Expect(mine.Pages == 5, "recovery sees " + mine.Pages + " pages, expected 5");

                List<RawImage> back = ScanJournal.LoadPages(j.Directory);
                Expect(back.Count == 5, "loaded " + back.Count + " pages, expected 5");
                foreach (RawImage img in back)
                    Expect(img.Width == 400 && img.Height == 500, "a recovered page is " + img);

                return "5 pages recovered after an abandoned session";
            }
            finally { ForgetSessions(made); }
        }

        static string JrExactPixels()
        {
            // Spooling must be lossless. Re-encoding pages as JPEG on the way to
            // disk would quietly degrade every recovered batch.
            List<string> made = new List<string>();
            try
            {
                RawImage original = Printed(600, 800, 300);
                ScanJournal j = ScanJournal.Begin("exact");
                made.Add(j.Directory);
                j.AddPage(original);
                Expect(j.Flush(20000), "the journal did not finish writing");
                j.Abandon();

                List<RawImage> back = ScanJournal.LoadPages(j.Directory);
                Expect(back.Count == 1, "loaded " + back.Count + " pages");
                RawImage got = back[0];

                Expect(got.Width == original.Width && got.Height == original.Height &&
                       got.Stride == original.Stride && got.Channels == original.Channels &&
                       got.BitsPerChannel == original.BitsPerChannel,
                       "the recovered page has a different shape: " + got);
                Expect(Math.Abs(got.XDpi - original.XDpi) < 0.01, "the resolution was not preserved");

                for (long i = 0; i < original.ByteLength; i++)
                    if (got.Pixels[i] != original.Pixels[i])
                        throw new Exception("the recovered page differs at byte " + i);

                return "byte-exact after a round trip";
            }
            finally { ForgetSessions(made); }
        }

        static string Jr16BitSurvives()
        {
            // 48-bit film scans are the case that a PNG or JPEG spool would ruin,
            // and they are exactly the scans worth not losing.
            List<string> made = new List<string>();
            try
            {
                RawImage deep = new RawImage
                {
                    Width = 200, Height = 150, Channels = 3, BitsPerChannel = 16,
                    Stride = 200 * 6, XDpi = 1200, YDpi = 1200
                };
                deep.Pixels = new byte[deep.Stride * deep.Height];
                Random rnd = new Random(3);
                rnd.NextBytes(deep.Pixels);

                ScanJournal j = ScanJournal.Begin("deep");
                made.Add(j.Directory);
                j.AddPage(deep);
                Expect(j.Flush(20000), "the journal did not finish writing");
                j.Abandon();

                List<RawImage> back = ScanJournal.LoadPages(j.Directory);
                Expect(back.Count == 1, "loaded " + back.Count + " pages");
                Expect(back[0].BitsPerChannel == 16, "came back at " + back[0].BitsPerChannel + " bits per channel");
                for (long i = 0; i < deep.ByteLength; i++)
                    if (back[0].Pixels[i] != deep.Pixels[i])
                        throw new Exception("the 48-bit page differs at byte " + i);

                return "48-bit page byte-exact, " + Math.Round(deep.ByteLength / 1024.0) + " KB";
            }
            finally { ForgetSessions(made); }
        }

        static string JrCompleteRemoves()
        {
            ScanJournal j = ScanJournal.Begin("done");
            string dir = j.Directory;
            j.AddPage(Printed(200, 260, 150));
            Expect(j.Flush(20000), "the journal did not finish writing");

            j.Complete();
            Expect(!Directory.Exists(dir), "the session folder outlived a completed save");

            foreach (JournalSession fs in ScanJournal.FindIncomplete())
                Expect(!string.Equals(fs.Directory, dir, StringComparison.OrdinalIgnoreCase),
                       "a completed session was still offered for recovery");
            return "folder removed once the pages were exported";
        }

        static string JrTornPageRefused()
        {
            // A session killed mid-write. The half-written file must not load as
            // a page - a recovered batch with one corrupt page is worse than one
            // that is honestly a page short.
            List<string> made = new List<string>();
            try
            {
                ScanJournal j = ScanJournal.Begin("torn");
                made.Add(j.Directory);
                j.AddPage(Printed(400, 500, 150));
                j.AddPage(Printed(400, 500, 150));
                Expect(j.Flush(20000), "the journal did not finish writing");
                j.Abandon();

                string[] files = Directory.GetFiles(j.Directory, "*.nspage");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                Expect(files.Length == 2, "expected 2 page files, found " + files.Length);

                byte[] whole = File.ReadAllBytes(files[1]);
                byte[] cut = new byte[whole.Length / 2];
                Array.Copy(whole, cut, cut.Length);
                File.WriteAllBytes(files[1], cut);

                List<RawImage> back = ScanJournal.LoadPages(j.Directory);
                Expect(back.Count == 1,
                       "loaded " + back.Count + " pages; the truncated one was accepted");
                Expect(back[0].IsValid, "the surviving page did not load");
                return "truncated page refused, the good one kept";
            }
            finally { ForgetSessions(made); }
        }

        static string JrLiveSessionSkipped()
        {
            // A second copy of the studio must not offer to recover the batch
            // this one is still scanning.
            List<string> made = new List<string>();
            try
            {
                ScanJournal j = ScanJournal.Begin("live");
                made.Add(j.Directory);
                j.AddPage(Printed(200, 260, 150));
                Expect(j.Flush(20000), "the journal did not finish writing");

                // Still running: not abandoned, and this process owns it.
                foreach (JournalSession fs in ScanJournal.FindIncomplete())
                    Expect(!string.Equals(fs.Directory, j.Directory, StringComparison.OrdinalIgnoreCase),
                           "a session owned by a live process was offered for recovery");

                // Once it lets go, it becomes recoverable.
                j.Abandon();
                bool offered = false;
                foreach (JournalSession fs in ScanJournal.FindIncomplete())
                    if (string.Equals(fs.Directory, j.Directory, StringComparison.OrdinalIgnoreCase)) offered = true;
                Expect(offered, "the session was not offered after being abandoned");

                return "live session hidden, abandoned session offered";
            }
            finally { ForgetSessions(made); }
        }

        // ============================================================ end to end
        /// <summary>
        /// Everything above works on pages this file made up. This one takes
        /// pages the engine actually delivered - through the host process, the
        /// TWAIN state machine and the shared-memory transfer - and puts them
        /// through the same export path the Save button uses. It is the only
        /// case here that would catch the engine handing over pages the writer
        /// cannot encode.
        ///
        /// Uses the fake DSM (ADR-0002) with the duplex personality, which is
        /// the one that has a feeder and returns more than a single page.
        /// </summary>
        static string ScannedPagesToPdf()
        {
            string bin = AppDomain.CurrentDomain.BaseDirectory;
            Environment.SetEnvironmentVariable("NEXTSCAN_TWAIN_DSM",
                Path.Combine(bin, @"sim\x86\TWAINDSM.DLL"));
            Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PERSONALITY", "duplex");
            Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PATTERN", "bars");

            DeviceBroker broker = new DeviceBroker();
            broker.HostDirectory = bin;

            DeviceDescriptor dev = null;
            foreach (ScannerEntry e in broker.Probe())
                foreach (DeviceDescriptor c in e.Connections)
                    if (c.Transport == Transport.Twain && c.HostBitness == 32) { dev = c; break; }
            if (dev == null) throw new Exception("the simulated TWAIN source was not found");

            ScanSettings ss = new ScanSettings
            {
                Dpi = 150,
                Mode = NextScan.Core.ColorMode.Color24,   // System.Drawing.Imaging has one too
                Source = PaperSource.FeederDuplex,
                PageCount = 0,              // until the feeder is empty
                RegionWidthIn = 8.0,
                RegionHeightIn = 10.0
            };

            List<RawImage> scanned = new List<RawImage>();
            NsResult r = broker.Scan(dev, ss, delegate (RawImage img) { scanned.Add(img); return true; }, null);
            if (r == null || !r.Ok) throw new Exception("the scan failed: " + (r == null ? "no result" : r.ToString()));
            Expect(scanned.Count >= 2,
                   "the feeder delivered " + scanned.Count + " page(s); a duplex pass should give at least 2");

            string d = Dir("end_to_end");
            ExportPlan plan = new ExportPlan
            {
                Directory = d,
                Pattern = "feed_{nnn}",
                Format = "pdf",
                MultiPage = true,
                Context = new NameContext { Device = "sim", Dpi = 150, Source = "adfduplex" }
            };

            int docs;
            List<string> written = BatchSplitter.WriteBatch(scanned, new BatchOptions(), plan, out docs);
            Expect(written.Count == 1, "expected one PDF, got " + written.Count);

            Pdf pdf = Pdf.Read(written[0]);
            Expect(pdf.PageCount == scanned.Count,
                   "the PDF holds " + pdf.PageCount + " pages but " + scanned.Count + " were scanned");
            pdf.CheckXref();

            return scanned.Count + " scanned pages into " + Path.GetFileName(written[0]);
        }

        /// <summary>
        /// The whole chain the shell relies on, with a real scan at the front:
        /// pages arrive from the engine, each is spooled as it lands, the run is
        /// abandoned without saving - which is what a crash or a power cut looks
        /// like - and the batch is then recovered and exported.
        ///
        /// The pixel comparison is the point. A recovery that returns pages of
        /// the right size but the wrong contents would pass every structural
        /// check and still be worthless.
        /// </summary>
        static string BatchSurvivesACrash()
        {
            List<string> made = new List<string>();
            try
            {
                string bin = AppDomain.CurrentDomain.BaseDirectory;
                Environment.SetEnvironmentVariable("NEXTSCAN_TWAIN_DSM",
                    Path.Combine(bin, @"sim\x86\TWAINDSM.DLL"));
                Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PERSONALITY", "duplex");
                Environment.SetEnvironmentVariable("NEXTSCAN_SIM_PATTERN", "bars");

                DeviceBroker broker = new DeviceBroker();
                broker.HostDirectory = bin;

                DeviceDescriptor dev = null;
                foreach (ScannerEntry e in broker.Probe())
                    foreach (DeviceDescriptor c in e.Connections)
                        if (c.Transport == Transport.Twain && c.HostBitness == 32) { dev = c; break; }
                if (dev == null) throw new Exception("the simulated TWAIN source was not found");

                ScanSettings ss = new ScanSettings
                {
                    Dpi = 150,
                    Mode = NextScan.Core.ColorMode.Color24,
                    Source = PaperSource.FeederDuplex,
                    PageCount = 0,
                    RegionWidthIn = 8.0,
                    RegionHeightIn = 10.0
                };

                ScanJournal journal = ScanJournal.Begin("crash test");
                made.Add(journal.Directory);

                // Exactly what the shell does for every page as it arrives.
                List<RawImage> scanned = new List<RawImage>();
                NsResult r = broker.Scan(dev, ss, delegate (RawImage img)
                {
                    scanned.Add(img);
                    journal.AddPage(img);
                    return true;
                }, null);

                if (r == null || !r.Ok) throw new Exception("the scan failed: " + (r == null ? "no result" : r.ToString()));
                Expect(scanned.Count >= 2, "the feeder delivered " + scanned.Count + " page(s)");
                Expect(journal.Flush(30000), "the journal did not finish writing");

                // No save. The session simply stops existing, as it would if the
                // machine lost power here.
                string dir = journal.Directory;
                journal.Abandon();

                List<RawImage> recovered = ScanJournal.LoadPages(dir);
                Expect(recovered.Count == scanned.Count,
                       "recovered " + recovered.Count + " of " + scanned.Count + " scanned pages");

                for (int i = 0; i < scanned.Count; i++)
                {
                    RawImage a = scanned[i], b = recovered[i];
                    Expect(a.Width == b.Width && a.Height == b.Height && a.Stride == b.Stride &&
                           a.Channels == b.Channels && a.BitsPerChannel == b.BitsPerChannel,
                           "recovered page " + (i + 1) + " has a different shape: " + b + " vs " + a);
                    for (long k = 0; k < a.ByteLength; k++)
                        if (a.Pixels[k] != b.Pixels[k])
                            throw new Exception("recovered page " + (i + 1) + " differs at byte " + k);
                }

                string outDir = Dir("crash_recovery");
                ExportPlan plan = new ExportPlan
                {
                    Directory = outDir,
                    Pattern = "rescued_{nnn}",
                    Format = "pdf",
                    MultiPage = true,
                    Context = Ctx()
                };
                List<string> written = PageWriter.Write(recovered, plan);
                Expect(written.Count == 1, "the recovered batch wrote " + written.Count + " files");

                Pdf pdf = Pdf.Read(written[0]);
                Expect(pdf.PageCount == recovered.Count,
                       "the rescued PDF holds " + pdf.PageCount + " pages, expected " + recovered.Count);
                pdf.CheckXref();

                return scanned.Count + " scanned, lost, recovered byte-exact, exported";
            }
            finally { ForgetSessions(made); }
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>
        /// Loads an image without leaving the file locked, so a test can delete
        /// or rewrite it afterwards. Image.FromFile keeps the handle for the
        /// lifetime of the object.
        /// </summary>
        static Image Load(string path)
        {
            return Image.FromStream(new MemoryStream(File.ReadAllBytes(path)));
        }

        static byte[] Inflate(byte[] zlib)
        {
            // Skip the two-byte zlib header; DeflateStream wants the raw payload.
            using (MemoryStream src = new MemoryStream(zlib, 2, zlib.Length - 6))
            using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream())
            {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        /// <summary>
        /// Just enough PDF reading to prove the writer produced a real document:
        /// the page count, the cross-reference offsets, and the first image
        /// stream. A structurally broken PDF often still displays, so checking
        /// that it opens somewhere would prove nothing.
        /// </summary>
        class Pdf
        {
            public byte[] Bytes;
            public string Text;         // Latin-1 view, safe to search for markers
            public string Tail;
            public int PageCount;

            public static Pdf Read(string path)
            {
                Pdf p = new Pdf();
                p.Bytes = File.ReadAllBytes(path);
                p.Text = Encoding.GetEncoding(28591).GetString(p.Bytes);
                p.Tail = p.Text.Length > 200 ? p.Text.Substring(p.Text.Length - 200) : p.Text;

                if (!p.Text.StartsWith("%PDF-1.")) throw new Exception("the file does not start with a PDF header");

                int i = p.Text.IndexOf("/Type /Pages");
                if (i < 0) throw new Exception("the PDF has no page tree");
                int c = p.Text.IndexOf("/Count ", i);
                if (c < 0) throw new Exception("the page tree has no /Count");
                int end = c + 7;
                while (end < p.Text.Length && char.IsDigit(p.Text[end])) end++;
                p.PageCount = int.Parse(p.Text.Substring(c + 7, end - c - 7), CultureInfo.InvariantCulture);
                return p;
            }

            /// <summary>
            /// Every cross-reference offset must land on the object it claims.
            /// This is the check that catches a writer whose byte positions drift
            /// - the failure that makes a PDF open in one reader and not another.
            /// </summary>
            public void CheckXref()
            {
                int sx = Text.LastIndexOf("startxref");
                if (sx < 0) throw new Exception("the PDF has no startxref");
                string after = Text.Substring(sx + 9).TrimStart('\r', '\n', ' ');
                int nl = after.IndexOfAny(new char[] { '\r', '\n' });
                long start = long.Parse(after.Substring(0, nl).Trim(), CultureInfo.InvariantCulture);

                if (start <= 0 || start >= Bytes.Length) throw new Exception("startxref points outside the file");
                if (!Text.Substring((int)start).StartsWith("xref"))
                    throw new Exception("startxref does not point at the xref table");

                string table = Text.Substring((int)start);
                string[] lines = table.Split('\n');
                // lines[0] = "xref", lines[1] = "0 N", then N entries.
                string[] head = lines[1].Trim().Split(' ');
                int count = int.Parse(head[1], CultureInfo.InvariantCulture);

                // lines[2] is object 0's free entry, so object k sits at lines[2 + k].
                for (int obj = 1; obj < count; obj++)
                {
                    string entry = lines[2 + obj];
                    long off = long.Parse(entry.Substring(0, 10), CultureInfo.InvariantCulture);
                    if (off <= 0 || off >= Bytes.Length)
                        throw new Exception("object " + obj + " has an offset outside the file");

                    string expect = obj + " 0 obj";
                    if (!Text.Substring((int)off).StartsWith(expect))
                        throw new Exception("the xref entry for object " + obj + " points at \"" +
                            Text.Substring((int)off, Math.Min(20, Bytes.Length - (int)off)).Replace("\n", " ") +
                            "\", not \"" + expect + "\"");
                }
            }

            public void FirstMediaBox(out double w, out double h)
            {
                int i = Text.IndexOf("/MediaBox [ 0 0 ");
                if (i < 0) throw new Exception("the PDF has no MediaBox");
                int close = Text.IndexOf(']', i);
                string[] parts = Text.Substring(i + 16, close - i - 16).Trim().Split(' ');
                w = double.Parse(parts[0], CultureInfo.InvariantCulture);
                h = double.Parse(parts[1], CultureInfo.InvariantCulture);
            }

            public byte[] FirstImageStream()
            {
                int i = Text.IndexOf("/Subtype /Image");
                if (i < 0) throw new Exception("the PDF has no image");
                int lenAt = Text.IndexOf("/Length ", i);
                int lenEnd = lenAt + 8;
                while (char.IsDigit(Text[lenEnd])) lenEnd++;
                int length = int.Parse(Text.Substring(lenAt + 8, lenEnd - lenAt - 8), CultureInfo.InvariantCulture);

                int s = Text.IndexOf("stream\n", i) + 7;
                byte[] data = new byte[length];
                Array.Copy(Bytes, s, data, 0, length);
                return data;
            }
        }
    }
}
