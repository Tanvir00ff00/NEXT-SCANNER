// =============================================================================
// NextScan Studio - batch scanning: blank detection and document separation
// Plan ref: MASTER_PLAN section 12 (jobs & batch), persona P3 "office / records
// clerk: ADF batch, duplex, blank-page drop, separation, auto-naming".
//
// A batch is not simply "scan more pages". The three things that make a batch
// feature usable in an office are:
//
//   1. dropping the blank backs produced by duplexing single-sided paper,
//   2. splitting one long feed into separate documents, and
//   3. naming each of those documents predictably.
//
// This file does the first two. The decisions are kept pure - pages in,
// groups out, no file or device access - because the interesting failures
// (a faint page treated as blank, a separator sheet kept by mistake) are
// exactly the ones a test can pin down and a hand test cannot.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NextScan.Core
{
    public enum SeparationRule
    {
        /// <summary>Everything scanned belongs to one document.</summary>
        None = 0,
        /// <summary>A new document every N pages - invoices, forms, ID cards.</summary>
        FixedPageCount = 1,
        /// <summary>A blank sheet between documents starts a new one.</summary>
        BlankPage = 2,
    }

    public class BatchOptions
    {
        public SeparationRule Separation = SeparationRule.None;
        public int PagesPerDocument = 1;

        /// <summary>
        /// Drop blank pages instead of keeping them. The common case is duplex
        /// scanning of single-sided paper, where every second image is blank.
        /// </summary>
        public bool DropBlankPages;

        /// <summary>
        /// Fraction of the page that must carry ink before it counts as content.
        /// Default 0.4%: a page with a single short line of text is around 1%,
        /// and a clean sheet with dust is well under 0.1%.
        /// </summary>
        public double BlankThreshold = 0.004;

        /// <summary>
        /// With BlankPage separation, throw the separator sheet away rather than
        /// keeping it as the first page of the next document.
        /// </summary>
        public bool DiscardSeparatorSheet = true;
    }

    public class BatchDocument
    {
        public int Index = 1;                       // 1-based, for {doc}
        public List<RawImage> Pages = new List<RawImage>();
    }

    public static class BatchSplitter
    {
        // =====================================================================
        // Blank detection
        // =====================================================================
        /// <summary>
        /// Fraction of the page carrying ink, in the range 0..1.
        ///
        /// The page is divided into tiles and a tile counts as inked only if
        /// several of its pixels are dark, which is what separates text from
        /// dust: a speck darkens one or two pixels in one tile and is discarded,
        /// while even a single word darkens a tile properly. Working per tile
        /// rather than per pixel also makes the result independent of scan
        /// resolution, so the same threshold holds at 150 and at 600 dpi.
        ///
        /// The darkness test is relative to the page's own white point rather
        /// than to absolute black, because scanned paper is rarely brighter than
        /// about 235 and coloured stock is darker still.
        /// </summary>
        public static double InkCoverage(RawImage img)
        {
            if (img == null || !img.IsValid) return 0.0;

            const int Tiles = 48;               // per axis
            const double Margin = 0.03;         // ignore the outer 3%
            const int InkPixelsPerTile = 6;
            const int DarkerThanWhiteBy = 48;

            int x0 = (int)(img.Width * Margin), x1 = img.Width - x0;
            int y0 = (int)(img.Height * Margin), y1 = img.Height - y0;
            if (x1 - x0 < Tiles || y1 - y0 < Tiles) { x0 = 0; y0 = 0; x1 = img.Width; y1 = img.Height; }
            if (x1 <= x0 || y1 <= y0) return 0.0;

            // Cap the work so a 600 dpi A3 page costs the same as an A4 one.
            int step = Math.Max(1, (int)Math.Sqrt((double)(x1 - x0) * (y1 - y0) / 1500000.0));

            // First pass: the white point, as the brightest level that at least
            // 10% of samples reach. A plain maximum would be set by a single
            // blown-out pixel.
            int[] hist = new int[256];
            long samples = 0;
            for (int y = y0; y < y1; y += step)
                for (int x = x0; x < x1; x += step)
                {
                    hist[Lum(img, x, y)]++;
                    samples++;
                }
            if (samples == 0) return 0.0;

            long want = samples / 10;
            long acc = 0;
            int white = 255;
            for (int v = 255; v >= 0; v--)
            {
                acc += hist[v];
                if (acc >= want) { white = v; break; }
            }

            int inkLevel = Math.Max(8, white - DarkerThanWhiteBy);

            // Second pass: count dark pixels per tile.
            int[,] dark = new int[Tiles, Tiles];
            double tw = (double)(x1 - x0) / Tiles, th = (double)(y1 - y0) / Tiles;
            for (int y = y0; y < y1; y += step)
            {
                int ty = (int)((y - y0) / th);
                if (ty >= Tiles) ty = Tiles - 1;
                for (int x = x0; x < x1; x += step)
                {
                    if (Lum(img, x, y) >= inkLevel) continue;
                    int tx = (int)((x - x0) / tw);
                    if (tx >= Tiles) tx = Tiles - 1;
                    dark[tx, ty]++;
                }
            }

            // The per-tile threshold is expressed in sampled pixels, so it has to
            // scale down as the sampling step grows or a downsampled page would
            // never reach it.
            double tileSamples = (tw / step) * (th / step);
            int need = Math.Max(2, (int)Math.Round(InkPixelsPerTile * Math.Min(1.0, tileSamples / 400.0)));

            int inked = 0;
            for (int tx = 0; tx < Tiles; tx++)
                for (int ty = 0; ty < Tiles; ty++)
                    if (dark[tx, ty] >= need) inked++;

            return (double)inked / (Tiles * Tiles);
        }

        /// <summary>Luminance 0..255 at one pixel, whatever the page's format.</summary>
        static byte Lum(RawImage img, int x, int y)
        {
            int row = y * img.Stride;

            if (img.BitsPerChannel == 1)
            {
                int b = img.Pixels[row + (x >> 3)];
                return (b & (0x80 >> (x & 7))) != 0 ? (byte)255 : (byte)0;
            }

            if (img.Channels == 1)
            {
                return img.BitsPerChannel == 16
                    ? img.Pixels[row + x * 2 + 1]
                    : img.Pixels[row + x];
            }

            if (img.BitsPerChannel == 16)
            {
                int o = row + x * 6;
                int r = img.Pixels[o + 1], g = img.Pixels[o + 3], bl = img.Pixels[o + 5];
                return (byte)((r * 77 + g * 150 + bl * 29) >> 8);
            }
            else
            {
                int o = row + x * 3;
                int r = img.Pixels[o], g = img.Pixels[o + 1], bl = img.Pixels[o + 2];
                return (byte)((r * 77 + g * 150 + bl * 29) >> 8);
            }
        }

        public static bool IsBlank(RawImage img, double threshold)
        {
            return InkCoverage(img) < threshold;
        }

        // =====================================================================
        // Separation
        // =====================================================================
        /// <summary>
        /// Groups a scanned run into documents according to the options.
        /// Never returns an empty document, and never returns null.
        /// </summary>
        public static List<BatchDocument> Split(IList<RawImage> pages, BatchOptions o)
        {
            List<BatchDocument> docs = new List<BatchDocument>();
            if (pages == null || pages.Count == 0) return docs;
            if (o == null) o = new BatchOptions();

            BatchDocument current = null;

            // Counted separately from Pages.Count because a document that is
            // holding nothing but a separator sheet is not a document. Without
            // this, two separators in a row - or one at the end of the stack,
            // which is how most people mark the end of a job - would each
            // produce an empty document in the output folder.
            int contentPages = 0;

            for (int i = 0; i < pages.Count; i++)
            {
                RawImage p = pages[i];
                if (p == null || !p.IsValid) continue;

                // Blank is measured once per page even when both the drop and the
                // separation rule need it, because the scan is by far the most
                // expensive part and this is the second most expensive.
                bool needBlank = o.DropBlankPages || o.Separation == SeparationRule.BlankPage;
                bool blank = needBlank && IsBlank(p, o.BlankThreshold);

                if (o.Separation == SeparationRule.BlankPage && blank)
                {
                    // Keeping the separator sheet changes what a document
                    // contains, never how many documents there are - so the
                    // close-out test is the same either way.
                    if (contentPages > 0) docs.Add(current);
                    current = null;
                    contentPages = 0;

                    if (o.DiscardSeparatorSheet) continue;

                    current = NewDoc(docs.Count + 1);
                    current.Pages.Add(p);
                    continue;
                }

                if (blank && o.DropBlankPages) continue;

                if (current == null) current = NewDoc(docs.Count + 1);
                current.Pages.Add(p);
                contentPages++;

                if (o.Separation == SeparationRule.FixedPageCount &&
                    o.PagesPerDocument > 0 && current.Pages.Count >= o.PagesPerDocument)
                {
                    docs.Add(current);
                    current = null;
                    contentPages = 0;
                }
            }

            if (contentPages > 0) docs.Add(current);

            for (int i = 0; i < docs.Count; i++) docs[i].Index = i + 1;
            return docs;
        }

        static BatchDocument NewDoc(int index)
        {
            BatchDocument d = new BatchDocument();
            d.Index = index;
            return d;
        }

        // =====================================================================
        // Writing a whole batch
        // =====================================================================
        /// <summary>
        /// Splits a run into documents and writes each one. Returns every file
        /// created, in order. A document that fails to write does not stop the
        /// rest: in a fifty-page job, losing forty-nine documents because one
        /// could not be encoded is the worse outcome.
        /// </summary>
        public static List<string> WriteBatch(IList<RawImage> pages, BatchOptions batch,
                                              ExportPlan plan, out int documentCount)
        {
            List<string> written = new List<string>();
            List<BatchDocument> docs = Split(pages, batch);
            documentCount = docs.Count;
            if (docs.Count == 0) return written;

            foreach (BatchDocument d in docs)
            {
                ExportPlan p = new ExportPlan
                {
                    Directory = plan.Directory,
                    Pattern = plan.Pattern,
                    Format = plan.Format,
                    JpegQuality = plan.JpegQuality,
                    MultiPage = plan.MultiPage,
                    Context = plan.Context
                };
                if (p.Context != null) p.Context.Document = d.Index;

                try { written.AddRange(PageWriter.Write(d.Pages, p)); }
                catch (Exception ex) { PageWriter.Log("document " + d.Index + " failed: " + ex.Message); }
            }
            return written;
        }
    }
}
