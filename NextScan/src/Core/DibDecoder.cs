// =============================================================================
// NextScan Studio - Windows DIB / BMP decoder
// Plan ref: MASTER_PLAN section 7.2 (native transfer) and 18.2 (regression list).
//
// Both TWAIN native transfer and WIA callback transfer hand back Windows DIBs, so
// this decoder is shared. The regression list in the plan exists because of this
// file: bottom-up rows, BI_BITFIELDS, 1-bit palette polarity, and odd-width 24-bit
// stride padding are all real bugs found in shipping drivers.
// =============================================================================
using System;
using System.Runtime.InteropServices;

namespace NextScan.Core
{
    public static class DibDecoder
    {
        const int BI_RGB = 0;
        const int BI_RLE8 = 1;
        const int BI_RLE4 = 2;
        const int BI_BITFIELDS = 3;

        /// <summary>Decodes a .bmp byte stream (BITMAPFILEHEADER + DIB).</summary>
        public static NsResult DecodeBmpFile(byte[] bmp, out RawImage image)
        {
            image = null;
            if (bmp == null || bmp.Length < 54)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat, "BMP data is too short to be valid", "");

            if (bmp[0] != 'B' || bmp[1] != 'M')
                return NsResult.Fail(NsError.ImagingUnsupportedFormat, "Missing BM signature - not a bitmap stream", "");

            GCHandle h = GCHandle.Alloc(bmp, GCHandleType.Pinned);
            try
            {
                // Skip the 14-byte BITMAPFILEHEADER; everything after is a packed DIB.
                IntPtr dib = new IntPtr(h.AddrOfPinnedObject().ToInt64() + 14);
                return Decode(dib, bmp.Length - 14, out image);
            }
            finally { h.Free(); }
        }

        /// <summary>
        /// Decodes a packed DIB in memory. availableBytes may be 0 when the size is
        /// unknown (a locked global handle), in which case bounds checks relax to
        /// what the header itself claims.
        /// </summary>
        public static NsResult Decode(IntPtr dib, long availableBytes, out RawImage image)
        {
            image = null;
            if (dib == IntPtr.Zero)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat, "Null DIB pointer", "");

            int headerSize = Marshal.ReadInt32(dib, 0);
            if (headerSize < 40 || headerSize > 260)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                    "Unsupported DIB header size " + headerSize, "");

            int width = Marshal.ReadInt32(dib, 4);
            int rawHeight = Marshal.ReadInt32(dib, 8);
            short planes = Marshal.ReadInt16(dib, 12);
            short bitCount = Marshal.ReadInt16(dib, 14);
            int compression = Marshal.ReadInt32(dib, 16);
            int xPelsPerMeter = Marshal.ReadInt32(dib, 24);
            int yPelsPerMeter = Marshal.ReadInt32(dib, 28);
            int clrUsed = Marshal.ReadInt32(dib, 32);

            // A negative height means the rows are already top-down.
            bool bottomUp = rawHeight > 0;
            int height = Math.Abs(rawHeight);

            if (width <= 0 || height <= 0 || planes != 1)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                    "Unsupported DIB geometry " + width + "x" + rawHeight + " planes=" + planes, "");

            if (compression == BI_RLE4 || compression == BI_RLE8)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                    "RLE-compressed bitmaps are not supported",
                    "Set the scanner to an uncompressed transfer mode.");
            if (compression != BI_RGB && compression != BI_BITFIELDS)
                return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                    "Unsupported DIB compression " + compression,
                    "Set the scanner to an uncompressed transfer mode.");

            int paletteEntries = 0;
            if (bitCount <= 8) paletteEntries = (clrUsed != 0) ? clrUsed : (1 << bitCount);

            // BI_BITFIELDS puts three DWORD masks after a plain 40-byte header. V4 and
            // V5 headers already contain the masks, so headerSize covers them.
            int maskBytes = (compression == BI_BITFIELDS && headerSize == 40) ? 12 : 0;
            int paletteOffset = headerSize + maskBytes;
            int paletteBytes = paletteEntries * 4;

            IntPtr bits = new IntPtr(dib.ToInt64() + paletteOffset + paletteBytes);
            int srcStride = ((width * bitCount + 31) / 32) * 4;

            if (availableBytes > 0)
            {
                long needed = (long)paletteOffset + paletteBytes + (long)srcStride * height;
                if (needed > availableBytes)
                    return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                        "DIB is truncated: header needs " + needed + " bytes, only " + availableBytes + " available", "");
            }

            byte[] palette = null;
            if (paletteBytes > 0)
            {
                palette = new byte[paletteBytes];
                Marshal.Copy(new IntPtr(dib.ToInt64() + paletteOffset), palette, 0, paletteBytes);
            }

            int channels, bitsPerChannel, dstStride;
            if (bitCount == 1)
            {
                channels = 1; bitsPerChannel = 1; dstStride = (width + 7) / 8;
            }
            else if (bitCount <= 8 && IsGrayscalePalette(palette, paletteEntries))
            {
                channels = 1; bitsPerChannel = 8; dstStride = width;
            }
            else
            {
                channels = 3; bitsPerChannel = 8; dstStride = width * 3;
            }

            long totalBytes = (long)height * dstStride;
            if (totalBytes > int.MaxValue)
                return NsResult.Fail(NsError.ImagingAllocFailed,
                    "Image needs " + (totalBytes / 1048576) + " MB, which exceeds the buffer limit",
                    "Scan at a lower resolution.");

            byte[] pixels;
            try { pixels = new byte[totalBytes]; }
            catch (OutOfMemoryException)
            {
                return NsResult.Fail(NsError.ImagingAllocFailed,
                    "Out of memory allocating " + (totalBytes / 1048576) + " MB", "Scan at a lower resolution.");
            }

            byte[] srcRow = new byte[srcStride];

            for (int y = 0; y < height; y++)
            {
                int srcY = bottomUp ? (height - 1 - y) : y;
                Marshal.Copy(new IntPtr(bits.ToInt64() + (long)srcY * srcStride), srcRow, 0, srcStride);
                int dst = y * dstStride;

                switch (bitCount)
                {
                    case 1:
                        // 1-bit DIBs are palette-indexed. When palette[0] is white the
                        // polarity is inverted relative to our convention (set bit =
                        // black), so flip while copying rather than after.
                        bool invert = (palette != null && paletteBytes >= 4 && palette[0] > 127);
                        for (int b = 0; b < dstStride && b < srcStride; b++)
                            pixels[dst + b] = invert ? (byte)~srcRow[b] : srcRow[b];
                        break;

                    case 4:
                        for (int x = 0; x < width; x++)
                        {
                            int idx = ((x & 1) == 0) ? (srcRow[x >> 1] >> 4) : (srcRow[x >> 1] & 0x0F);
                            WritePalettePixel(pixels, dst, x, channels, palette, idx);
                        }
                        break;

                    case 8:
                        for (int x = 0; x < width; x++)
                            WritePalettePixel(pixels, dst, x, channels, palette, srcRow[x]);
                        break;

                    case 16:
                        // Default RGB555 when no bitfield masks are present.
                        for (int x = 0; x < width; x++)
                        {
                            int v = srcRow[x * 2] | (srcRow[x * 2 + 1] << 8);
                            pixels[dst + x * 3 + 0] = (byte)((v & 0x1F) << 3);
                            pixels[dst + x * 3 + 1] = (byte)(((v >> 5) & 0x1F) << 3);
                            pixels[dst + x * 3 + 2] = (byte)(((v >> 10) & 0x1F) << 3);
                        }
                        break;

                    case 24:
                        Buffer.BlockCopy(srcRow, 0, pixels, dst, Math.Min(width * 3, srcStride));
                        break;

                    case 32:
                        for (int x = 0; x < width; x++)
                        {
                            pixels[dst + x * 3 + 0] = srcRow[x * 4 + 0];
                            pixels[dst + x * 3 + 1] = srcRow[x * 4 + 1];
                            pixels[dst + x * 3 + 2] = srcRow[x * 4 + 2];
                        }
                        break;

                    default:
                        return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                            "Unsupported DIB colour depth " + bitCount + " bpp", "");
                }
            }

            image = new RawImage();
            image.Pixels = pixels;
            image.Width = width;
            image.Height = height;
            image.Stride = dstStride;
            image.Channels = channels;
            image.BitsPerChannel = bitsPerChannel;
            image.XDpi = (xPelsPerMeter > 0) ? Math.Round(xPelsPerMeter * 0.0254) : 300;
            image.YDpi = (yPelsPerMeter > 0) ? Math.Round(yPelsPerMeter * 0.0254) : 300;
            return NsResult.Success();
        }

        static void WritePalettePixel(byte[] pixels, int dst, int x, int channels, byte[] palette, int index)
        {
            int pi = index * 4;
            if (palette == null || pi + 2 >= palette.Length)
            {
                if (channels == 1) pixels[dst + x] = (byte)index;
                else { pixels[dst + x * 3] = pixels[dst + x * 3 + 1] = pixels[dst + x * 3 + 2] = (byte)index; }
                return;
            }

            if (channels == 1)
            {
                pixels[dst + x] = palette[pi];   // grayscale palette: B == G == R
            }
            else
            {
                pixels[dst + x * 3 + 0] = palette[pi + 0];
                pixels[dst + x * 3 + 1] = palette[pi + 1];
                pixels[dst + x * 3 + 2] = palette[pi + 2];
            }
        }

        /// <summary>
        /// A fully grayscale palette lets us keep the image as one channel instead of
        /// tripling it. Checked exhaustively - sampling misses palettes that are grey
        /// except for a handful of spot colours.
        /// </summary>
        static bool IsGrayscalePalette(byte[] palette, int entries)
        {
            if (palette == null || entries <= 0) return false;
            for (int i = 0; i < entries; i++)
            {
                int p = i * 4;
                if (p + 2 >= palette.Length) break;
                if (palette[p] != palette[p + 1] || palette[p + 1] != palette[p + 2]) return false;
            }
            return true;
        }

        /// <summary>
        /// TWAIN memory transfer delivers RGB triplets while Windows DIBs are BGR.
        /// Callers normalise to BGR so the rest of the pipeline has one convention.
        /// </summary>
        public static void SwapRedBlue(RawImage img)
        {
            if (img == null || img.Channels != 3 || img.Pixels == null) return;

            int bytesPerSample = Math.Max(1, img.BitsPerChannel / 8);
            int pixelBytes = 3 * bytesPerSample;

            for (int y = 0; y < img.Height; y++)
            {
                int row = y * img.Stride;
                for (int x = 0; x < img.Width; x++)
                {
                    int p = row + x * pixelBytes;
                    if (p + pixelBytes > img.Pixels.Length) return;
                    for (int b = 0; b < bytesPerSample; b++)
                    {
                        byte t = img.Pixels[p + b];
                        img.Pixels[p + b] = img.Pixels[p + 2 * bytesPerSample + b];
                        img.Pixels[p + 2 * bytesPerSample + b] = t;
                    }
                }
            }
        }
    }
}
