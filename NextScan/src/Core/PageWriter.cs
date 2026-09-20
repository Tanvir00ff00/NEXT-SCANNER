// =============================================================================
// NextScan Studio - file output: JPEG, PNG, single and multi-page TIFF, PDF
// Plan ref: MASTER_PLAN section 11.1, 14.3.
//
// This lives in Core rather than in the UI project for one practical reason:
// writing files is the part of the product that most needs a test suite, and a
// test that has to start a WinForms shell to save a JPEG is a test nobody runs.
// StudioExport is now a thin shim over this.
//
// Every write is staged through a temporary file in the destination folder and
// committed with File.Replace. A scanner suite that truncates yesterday's PDF
// because page 14 of today's job failed to encode has done more damage than one
// that simply refuses to save.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using GdiEncoder = System.Drawing.Imaging.Encoder;   // System.Text.Encoder also exists

namespace NextScan.Core
{
    /// <summary>What one export run should produce.</summary>
    public class ExportPlan
    {
        public string Directory = "";
        public string Pattern = "scan_{date}_{time}";
        public string Format = "jpg";       // jpg, png, tif, bmp, pdf
        public long JpegQuality = 92L;

        /// <summary>
        /// For TIFF and PDF: one file holding every page, rather than one file
        /// per page. Ignored for formats that cannot hold more than one image.
        /// </summary>
        public bool MultiPage = true;

        public NameContext Context = new NameContext();
    }

    public static class PageWriter
    {
        public static Action<string> Log = delegate { };

        /// <summary>True if this format can hold more than one page in one file.</summary>
        public static bool SupportsMultiPage(string format)
        {
            string f = Norm(format);
            return f == "pdf" || f == "tif";
        }

        static string Norm(string format)
        {
            string f = (format ?? "jpg").TrimStart('.').ToLowerInvariant();
            if (f == "jpeg") f = "jpg";
            if (f == "tiff") f = "tif";
            return f;
        }

        // =====================================================================
        // Entry point
        // =====================================================================
        /// <summary>
        /// Writes every page according to the plan and returns the files created.
        /// The list is empty if nothing could be written; it is never partial for
        /// a multi-page container, which is either complete or absent.
        /// </summary>
        public static List<string> Write(IList<RawImage> pages, ExportPlan plan)
        {
            List<string> written = new List<string>();
            if (pages == null || pages.Count == 0) return written;
            if (plan == null) plan = new ExportPlan();

            string fmt = Norm(plan.Format);
            NameContext ctx = plan.Context ?? new NameContext();

            // Names already handed out in this run count as taken. Without this,
            // a pattern with no counter and no time token would resolve to the
            // same free name for every page, because none of them exist yet.
            HashSet<string> issued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> taken = delegate (string p)
            {
                return issued.Contains(p) || File.Exists(p);
            };

            if (plan.MultiPage && SupportsMultiPage(fmt))
            {
                ctx.Page = 1;
                string path = NameTemplate.ResolvePath(plan.Directory, plan.Pattern, fmt, ctx, taken);
                bool ok = fmt == "pdf"
                    ? SavePdf(pages, path, plan.JpegQuality)
                    : SaveMultiPageTiff(pages, path);
                if (ok) written.Add(path);
                return written;
            }

            for (int i = 0; i < pages.Count; i++)
            {
                RawImage page = pages[i];
                if (page == null || !page.IsValid) { Log("page " + (i + 1) + " is not valid; skipped"); continue; }

                ctx.Page = i + 1;
                ctx.Side = page.Side;
                string path = NameTemplate.ResolvePath(plan.Directory, plan.Pattern, fmt, ctx, taken);
                issued.Add(path);

                bool ok = fmt == "pdf"
                    ? SavePdf(new List<RawImage> { page }, path, plan.JpegQuality)
                    : SaveSingle(page, path, fmt, plan.JpegQuality);
                if (ok) written.Add(path);
            }
            return written;
        }

        // =====================================================================
        // Single images
        // =====================================================================
        public static bool SaveSingle(RawImage img, string outPath, string format, long jpegQuality = 92L)
        {
            if (img == null || !img.IsValid) return false;
            if (Norm(format) == "pdf") return SavePdf(new List<RawImage> { img }, outPath, jpegQuality);

            using (Bitmap bmp = img.ToBitmap())
            {
                if (bmp == null) return false;
                return SaveBitmap(bmp, outPath, format, jpegQuality);
            }
        }

        public static bool SaveBitmap(Bitmap bmp, string path, string format, long jpegQuality = 92L)
        {
            if (bmp == null) return false;
            return Commit(path, delegate (string tmp)
            {
                return WriteBitmap(bmp, tmp, format, Clamp(jpegQuality));
            });
        }

        static long Clamp(long q) { return Math.Max(0L, Math.Min(100L, q)); }

        /// <summary>
        /// Runs a writer against a temporary file and moves it into place only
        /// once it has returned success.
        /// </summary>
        static bool Commit(string path, Func<string, bool> writer)
        {
            string temporary = null;
            try
            {
                string destination = Path.GetFullPath(path);
                string dir = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (!writer(temporary)) return false;

                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
                temporary = null;
                return true;
            }
            catch (Exception ex)
            {
                Log("writing " + path + " failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (temporary != null && File.Exists(temporary))
                    try { File.Delete(temporary); } catch { }
            }
        }

        static ImageCodecInfo Codec(Guid formatId)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == formatId) return c;
            return null;
        }

        static bool WriteBitmap(Bitmap bmp, string path, string format, long jpegQuality)
        {
            switch (Norm(format))
            {
                case "png":
                    bmp.Save(path, ImageFormat.Png);
                    return true;

                case "bmp":
                    bmp.Save(path, ImageFormat.Bmp);
                    return true;

                case "tif":
                    {
                        ImageCodecInfo enc = Codec(ImageFormat.Tiff.Guid);
                        if (enc == null) { bmp.Save(path, ImageFormat.Tiff); return true; }
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(GdiEncoder.Compression, (long)TiffCompressionFor(bmp));
                            bmp.Save(path, enc, ep);
                        }
                        return true;
                    }

                case "jpg":
                    {
                        ImageCodecInfo enc = Codec(ImageFormat.Jpeg.Guid);
                        if (enc == null) { bmp.Save(path, ImageFormat.Jpeg); return true; }
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(GdiEncoder.Quality, jpegQuality);
                            bmp.Save(path, enc, ep);
                        }
                        return true;
                    }

                default:
                    Log("unsupported export format: " + format);
                    return false;
            }
        }

        /// <summary>
        /// TIFF compression is lossless in every case here, so the only question
        /// is which lossless scheme suits the data. CCITT Group 4 is dramatically
        /// better than LZW on bilevel scans - the difference between a 60 KB page
        /// and a 700 KB one - but it is only defined for 1 bit per pixel.
        /// </summary>
        static EncoderValue TiffCompressionFor(Bitmap bmp)
        {
            return bmp.PixelFormat == PixelFormat.Format1bppIndexed
                ? EncoderValue.CompressionCCITT4
                : EncoderValue.CompressionLZW;
        }

        // =====================================================================
        // Multi-page TIFF
        // =====================================================================
        /// <summary>
        /// Writes every page into one TIFF. GDI+ builds multi-page files through
        /// SaveAdd: the first page is saved with EncoderValue.MultiFrame, each
        /// later page with FrameDimensionPage against the same Image object, and
        /// the file is closed with Flush. The pages must stay alive until the
        /// flush, which is why the bitmaps are disposed only at the end.
        /// </summary>
        public static bool SaveMultiPageTiff(IList<RawImage> pages, string outPath)
        {
            if (pages == null || pages.Count == 0) return false;
            foreach (RawImage p in pages) if (p == null || !p.IsValid) return false;

            return Commit(outPath, delegate (string tmp)
            {
                List<Bitmap> bitmaps = new List<Bitmap>();
                try
                {
                    foreach (RawImage p in pages)
                    {
                        Bitmap b = p.ToBitmap();
                        if (b == null) return false;
                        bitmaps.Add(b);
                    }

                    ImageCodecInfo enc = Codec(ImageFormat.Tiff.Guid);
                    if (enc == null) { Log("no TIFF encoder is available"); return false; }

                    Bitmap first = bitmaps[0];
                    using (EncoderParameters ep = new EncoderParameters(2))
                    {
                        ep.Param[0] = new EncoderParameter(GdiEncoder.SaveFlag, (long)EncoderValue.MultiFrame);
                        ep.Param[1] = new EncoderParameter(GdiEncoder.Compression, (long)TiffCompressionFor(first));
                        first.Save(tmp, enc, ep);
                    }

                    for (int i = 1; i < bitmaps.Count; i++)
                    {
                        using (EncoderParameters ep = new EncoderParameters(2))
                        {
                            ep.Param[0] = new EncoderParameter(GdiEncoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
                            ep.Param[1] = new EncoderParameter(GdiEncoder.Compression, (long)TiffCompressionFor(bitmaps[i]));
                            first.SaveAdd(bitmaps[i], ep);
                        }
                    }

                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(GdiEncoder.SaveFlag, (long)EncoderValue.Flush);
                        first.SaveAdd(ep);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Log("multi-page TIFF failed: " + ex.Message);
                    return false;
                }
                finally
                {
                    foreach (Bitmap b in bitmaps) try { b.Dispose(); } catch { }
                }
            });
        }

        // =====================================================================
        // PDF 1.4, written directly
        // =====================================================================
        // No PDF library is used. Adding one would mean either a NuGet
        // dependency this build system cannot consume or several megabytes of
        // vendored code, for a document structure that is a few hundred lines
        // when the only content is one full-page image per page.
        // =====================================================================
        public static bool SavePdf(IList<RawImage> pages, string outPath, long jpegQuality = 92L)
        {
            if (pages == null || pages.Count == 0) return false;
            foreach (RawImage p in pages) if (p == null || !p.IsValid) return false;

            long q = Clamp(jpegQuality);
            return Commit(outPath, delegate (string tmp) { return WritePdf(pages, tmp, q); });
        }

        /// <summary>One page's image, already encoded the way the PDF will carry it.</summary>
        class PdfImage
        {
            public byte[] Data;
            public string Filter;       // DCTDecode or FlateDecode
            public string ColorSpace;   // DeviceRGB or DeviceGray
            public int Width;
            public int Height;
        }

        /// <summary>
        /// Chooses how to carry one page.
        ///
        /// The constraint that shapes this: GDI+ always emits a three-component
        /// JPEG, even when handed an 8-bit greyscale surface. So a JPEG stream
        /// can only ever be declared DeviceRGB - claiming DeviceGray over it, as
        /// an earlier version of this writer did, produces a file whose header
        /// contradicts its contents, which strict readers reject and lenient
        /// ones render by luck.
        ///
        /// A one-channel page therefore goes in deflated, where the byte layout
        /// is ours and DeviceGray is truthful. That is also the better encoding
        /// for the pages that arrive in one channel: bilevel and greyscale text,
        /// where JPEG rings around the letterforms. The exception is continuous
        /// tone - a greyscale photograph - which deflates badly, so if Flate
        /// fails to make a real dent the page falls back to JPEG and is labelled
        /// DeviceRGB, honestly.
        /// </summary>
        static PdfImage EncodeForPdf(RawImage raw, long jpegQuality)
        {
            PdfImage im = new PdfImage();
            im.Width = raw.Width;
            im.Height = raw.Height;

            if (raw.Channels == 1)
            {
                byte[] gray = GrayBytes(raw);
                byte[] flate = Deflate(gray);

                // Bilevel is never worth JPEG-encoding whatever the ratio.
                if (raw.BitsPerChannel == 1 || flate.LongLength * 4 < gray.LongLength)
                {
                    im.Filter = "FlateDecode";
                    im.ColorSpace = "DeviceGray";
                    im.Data = flate;
                    return im;
                }
            }

            im.Filter = "DCTDecode";
            im.ColorSpace = "DeviceRGB";

            using (Bitmap bmp = raw.Channels == 1 ? GrayBitmap(raw) : raw.ToBitmap())
            using (MemoryStream ms = new MemoryStream())
            {
                ImageCodecInfo enc = Codec(ImageFormat.Jpeg.Guid);
                using (EncoderParameters ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(GdiEncoder.Quality, jpegQuality);
                    if (enc != null) bmp.Save(ms, enc, ep);
                    else bmp.Save(ms, ImageFormat.Jpeg);
                }
                im.Data = ms.ToArray();
            }
            return im;
        }

        /// <summary>
        /// One byte per pixel, row-packed with no padding - the layout a PDF
        /// image stream wants, which is not the same as a bitmap stride.
        /// </summary>
        static byte[] GrayBytes(RawImage raw)
        {
            byte[] outBytes = new byte[(long)raw.Width * raw.Height <= int.MaxValue
                ? raw.Width * raw.Height : 0];
            if (outBytes.Length == 0) throw new IOException("the page is too large to embed");

            int o = 0;
            for (int y = 0; y < raw.Height; y++)
            {
                int row = y * raw.Stride;
                if (raw.BitsPerChannel == 1)
                {
                    for (int x = 0; x < raw.Width; x++)
                    {
                        int b = raw.Pixels[row + (x >> 3)];
                        bool bit = (b & (0x80 >> (x & 7))) != 0;
                        outBytes[o++] = bit ? (byte)255 : (byte)0;
                    }
                }
                else if (raw.BitsPerChannel == 16)
                {
                    for (int x = 0; x < raw.Width; x++) outBytes[o++] = raw.Pixels[row + x * 2 + 1];
                }
                else
                {
                    for (int x = 0; x < raw.Width; x++) outBytes[o++] = raw.Pixels[row + x];
                }
            }
            return outBytes;
        }

        static Bitmap GrayBitmap(RawImage raw)
        {
            // GDI+ has no writable 8-bit greyscale surface, so the JPEG encoder
            // is fed a 24-bit bitmap whose channels are equal. The encoder emits
            // a single-component JPEG for it, which is what DeviceGray needs.
            byte[] g = GrayBytes(raw);
            Bitmap bmp = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
            bmp.SetResolution((float)Dpi(raw.XDpi), (float)Dpi(raw.YDpi));
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, raw.Width, raw.Height),
                                         ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < raw.Height; y++)
                    {
                        byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                        int src = y * raw.Width;
                        for (int x = 0; x < raw.Width; x++)
                        {
                            byte v = g[src + x];
                            dst[x * 3] = v; dst[x * 3 + 1] = v; dst[x * 3 + 2] = v;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        static double Dpi(double v)
        {
            return (v > 0 && !double.IsInfinity(v) && !double.IsNaN(v)) ? v : 300.0;
        }

        /// <summary>
        /// Raw deflate wrapped in the two-byte zlib header and Adler-32 trailer
        /// that PDF's FlateDecode expects. DeflateStream produces the payload
        /// only, so the wrapper has to be added here.
        /// </summary>
        static byte[] Deflate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                ms.WriteByte(0x78);
                ms.WriteByte(0x9C);
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);

                uint a = 1, b = 0;
                foreach (byte v in data)
                {
                    a = (a + v) % 65521;
                    b = (b + a) % 65521;
                }
                ms.WriteByte((byte)(b >> 8)); ms.WriteByte((byte)b);
                ms.WriteByte((byte)(a >> 8)); ms.WriteByte((byte)a);
                return ms.ToArray();
            }
        }

        static bool WritePdf(IList<RawImage> pages, string outPath, long jpegQuality)
        {
            try
            {
                using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                {
                    // Object numbers, assigned in the order they are written:
                    //   1        catalog
                    //   2        page tree
                    //   3        document information
                    //   4 + i*3  page i, then its image, then its content stream
                    const int First = 4;
                    int pageCount = pages.Count;

                    List<long> offsets = new List<long>();
                    offsets.Add(0);     // object 0 is the free-list head

                    Action<string> ascii = delegate (string s)
                    {
                        byte[] b = Encoding.ASCII.GetBytes(s);
                        fs.Write(b, 0, b.Length);
                    };

                    ascii("%PDF-1.4\n%");
                    fs.Write(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3, 10 }, 0, 5);

                    offsets.Add(fs.Position);
                    ascii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

                    offsets.Add(fs.Position);
                    StringBuilder kids = new StringBuilder();
                    for (int i = 0; i < pageCount; i++) kids.Append((First + i * 3) + " 0 R ");
                    ascii("2 0 obj\n<< /Type /Pages /Kids [ " + kids + "] /Count " + pageCount + " >>\nendobj\n");

                    // Readers, indexing tools and archivists all look here to see
                    // what produced a file; leaving it out marks the PDF as
                    // machine-generated by something anonymous.
                    offsets.Add(fs.Position);
                    ascii("3 0 obj\n<< /Producer (NextScan Studio) /Creator (NextScan Studio) /CreationDate ("
                          + PdfDate(DateTime.Now) + ") >>\nendobj\n");

                    for (int i = 0; i < pageCount; i++)
                    {
                        RawImage raw = pages[i];
                        int pageObj = First + i * 3;
                        int imgObj = pageObj + 1;
                        int contentObj = pageObj + 2;

                        // PDF measures in points, 72 to the inch, so the page box
                        // is the pixel count divided by the scan resolution. This
                        // is what makes a 300 dpi A4 scan print back at A4 rather
                        // than at whatever size the pixel count implies at 72.
                        double ptW = (raw.Width / Dpi(raw.XDpi)) * 72.0;
                        double ptH = (raw.Height / Dpi(raw.YDpi)) * 72.0;

                        PdfImage im = EncodeForPdf(raw, jpegQuality);

                        offsets.Add(fs.Position);
                        ascii(string.Format(CultureInfo.InvariantCulture,
                            "{0} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 {1:0.##} {2:0.##} ] " +
                            "/Contents {3} 0 R /Resources << /XObject << /Im{4} {5} 0 R >> >> >>\nendobj\n",
                            pageObj, ptW, ptH, contentObj, i + 1, imgObj));

                        offsets.Add(fs.Position);
                        ascii(string.Format(CultureInfo.InvariantCulture,
                            "{0} 0 obj\n<< /Type /XObject /Subtype /Image /Width {1} /Height {2} " +
                            "/ColorSpace /{3} /BitsPerComponent 8 /Filter /{4} /Length {5} >>\nstream\n",
                            imgObj, im.Width, im.Height, im.ColorSpace, im.Filter, im.Data.Length));
                        fs.Write(im.Data, 0, im.Data.Length);
                        ascii("\nendstream\nendobj\n");

                        string cm = string.Format(CultureInfo.InvariantCulture,
                            "q {0:0.##} 0 0 {1:0.##} 0 0 cm /Im{2} Do Q\n", ptW, ptH, i + 1);
                        byte[] cmBytes = Encoding.ASCII.GetBytes(cm);

                        offsets.Add(fs.Position);
                        ascii(string.Format(CultureInfo.InvariantCulture,
                            "{0} 0 obj\n<< /Length {1} >>\nstream\n", contentObj, cmBytes.Length));
                        fs.Write(cmBytes, 0, cmBytes.Length);
                        ascii("endstream\nendobj\n");
                    }

                    long startXref = fs.Position;
                    ascii("xref\n0 " + offsets.Count + "\n");
                    ascii("0000000000 65535 f \n");
                    for (int j = 1; j < offsets.Count; j++)
                        ascii(offsets[j].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");

                    ascii("trailer\n<< /Size " + offsets.Count + " /Root 1 0 R /Info 3 0 R >>\nstartxref\n"
                          + startXref + "\n%%EOF\n");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("PDF encoding failed: " + ex.Message);
                return false;
            }
        }

        static string PdfDate(DateTime t)
        {
            TimeSpan off = TimeZoneInfo.Local.GetUtcOffset(t);
            return string.Format(CultureInfo.InvariantCulture,
                "D:{0:yyyyMMddHHmmss}{1}{2:00}'{3:00}'",
                t, off.Ticks < 0 ? "-" : "+", Math.Abs(off.Hours), Math.Abs(off.Minutes));
        }
    }
}
