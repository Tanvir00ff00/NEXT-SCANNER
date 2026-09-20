using System;
using System.Collections.Generic;

namespace NextScan.Core
{
    /// <summary>
    /// Lays several finished pages out side by side, as one picture to look at.
    ///
    /// After a scan that cut five items out of the glass, showing one of them
    /// and putting the rest in a list asks the operator to click four times to
    /// find out whether the scan worked. What they want to know is whether all
    /// five came out right, and that is one question about five pictures, so it
    /// deserves one picture.
    ///
    /// This is a view and only a view. It is built at display resolution from
    /// pages that are kept at full resolution elsewhere, and nothing that saves
    /// or exports may read it: what it is good for is answering "did that
    /// work", and what it would be terrible for is being mistaken for the scan.
    /// </summary>
    internal static class ContactSheet
    {
        /// <summary>Gap between pages, and around them, in pixels of the sheet.</summary>
        const int Gutter = 14;

        /// <summary>The grey the pages sit on. Lighter than the app, darker than paper.</summary>
        const byte Ground = 232;

        /// <summary>
        /// Composes the pages into one image whose long edge is about
        /// <paramref name="longEdge"/>. Returns null rather than something
        /// unusable if there is nothing to show.
        /// </summary>
        internal static RawImage Compose(IList<RawImage> pages, int longEdge)
        {
            if (pages == null || pages.Count == 0) return null;

            List<RawImage> usable = new List<RawImage>();
            foreach (RawImage page in pages) if (page != null && page.IsValid) usable.Add(page);
            if (usable.Count == 0) return null;
            if (longEdge < 200) longEdge = 200;

            int columns = (int)Math.Ceiling(Math.Sqrt(usable.Count));
            int rows = (int)Math.Ceiling(usable.Count / (double)columns);

            // Every cell is the same size, taken from the widest and tallest
            // page there is. Pages keep their own shape inside it, so a card
            // next to a passport still reads as a card next to a passport
            // rather than both being stretched to agree.
            double widest = 1, tallest = 1;
            foreach (RawImage page in usable)
            {
                widest = Math.Max(widest, page.Width);
                tallest = Math.Max(tallest, page.Height);
            }

            double sheetAspect = (columns * widest) / (rows * tallest);
            int sheetWidth, sheetHeight;
            if (sheetAspect >= 1) { sheetWidth = longEdge; sheetHeight = Math.Max(1, (int)(longEdge / sheetAspect)); }
            else { sheetHeight = longEdge; sheetWidth = Math.Max(1, (int)(longEdge * sheetAspect)); }

            int cellWidth = Math.Max(8, (sheetWidth - Gutter * (columns + 1)) / columns);
            int cellHeight = Math.Max(8, (sheetHeight - Gutter * (rows + 1)) / rows);

            sheetWidth = cellWidth * columns + Gutter * (columns + 1);
            sheetHeight = cellHeight * rows + Gutter * (rows + 1);

            RawImage sheet = new RawImage
            {
                Width = sheetWidth,
                Height = sheetHeight,
                Channels = 3,
                BitsPerChannel = 8,
                Stride = sheetWidth * 3,
                XDpi = 96,
                YDpi = 96
            };
            sheet.Pixels = new byte[(long)sheet.Stride * sheet.Height > int.MaxValue
                ? 0 : sheet.Stride * sheet.Height];
            if (sheet.Pixels.Length == 0) return null;
            for (int i = 0; i < sheet.Pixels.Length; i++) sheet.Pixels[i] = Ground;

            for (int index = 0; index < usable.Count; index++)
            {
                RawImage page = usable[index];
                int column = index % columns, row = index / columns;
                int cellX = Gutter + column * (cellWidth + Gutter);
                int cellY = Gutter + row * (cellHeight + Gutter);

                double fit = Math.Min(cellWidth / (double)page.Width, cellHeight / (double)page.Height);
                int drawWidth = Math.Max(1, (int)(page.Width * fit));
                int drawHeight = Math.Max(1, (int)(page.Height * fit));
                int offsetX = cellX + (cellWidth - drawWidth) / 2;
                int offsetY = cellY + (cellHeight - drawHeight) / 2;

                Blit(page, sheet, offsetX, offsetY, drawWidth, drawHeight);
            }
            return sheet;
        }

        /// <summary>
        /// Draws one page into the sheet at the size given, averaging whatever
        /// falls in each output pixel. Averaging rather than picking: a page
        /// coming down by a factor of six would otherwise lose its own text to
        /// aliasing and look like a bad scan of a good scan.
        /// </summary>
        static void Blit(RawImage page, RawImage sheet, int atX, int atY, int width, int height)
        {
            double stepX = page.Width / (double)width, stepY = page.Height / (double)height;

            for (int y = 0; y < height; y++)
            {
                int toY = atY + y;
                if (toY < 0 || toY >= sheet.Height) continue;

                int fromY = (int)(y * stepY), untilY = (int)((y + 1) * stepY);
                if (untilY <= fromY) untilY = fromY + 1;
                if (untilY > page.Height) untilY = page.Height;

                for (int x = 0; x < width; x++)
                {
                    int toX = atX + x;
                    if (toX < 0 || toX >= sheet.Width) continue;

                    int fromX = (int)(x * stepX), untilX = (int)((x + 1) * stepX);
                    if (untilX <= fromX) untilX = fromX + 1;
                    if (untilX > page.Width) untilX = page.Width;

                    int blue = 0, green = 0, red = 0, count = 0;
                    for (int sy = fromY; sy < untilY; sy++)
                        for (int sx = fromX; sx < untilX; sx++)
                        {
                            int b, g, r;
                            Read(page, sx, sy, out b, out g, out r);
                            blue += b; green += g; red += r; count++;
                        }
                    if (count == 0) count = 1;

                    int offset = toY * sheet.Stride + toX * 3;
                    sheet.Pixels[offset] = (byte)(blue / count);
                    sheet.Pixels[offset + 1] = (byte)(green / count);
                    sheet.Pixels[offset + 2] = (byte)(red / count);
                }
            }
        }

        static void Read(RawImage p, int x, int y, out int blue, out int green, out int red)
        {
            if (p.BitsPerChannel == 1)
            {
                int on = (p.Pixels[y * p.Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 255 : 0;
                blue = green = red = on;
                return;
            }

            int step = p.BitsPerChannel == 16 ? 2 : 1;
            int o = y * p.Stride + x * p.Channels * step;
            if (p.BitsPerChannel == 16) o += 1;          // high byte of the little-endian pair

            if (p.Channels == 1) { blue = green = red = p.Pixels[o]; return; }
            blue = p.Pixels[o];
            green = p.Pixels[o + step];
            red = p.Pixels[o + step * 2];
        }
    }
}
