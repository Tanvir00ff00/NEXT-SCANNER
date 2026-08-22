// =============================================================================
// NextScan Studio - Transport-neutral raster buffer
// Plan ref: MASTER_PLAN section 8.1.
//
// Everything the acquisition engines produce lands here first. Deliberately NOT
// System.Drawing.Bitmap: GDI+ mangles 48-bit data and silently drops ICC
// profiles, and we need both intact for the film and colour-managed workflows.
// =============================================================================
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NextScan.Core
{
    public class RawImage
    {
        /// <summary>Tightly packed, top-down. BGR order for 3-channel data.</summary>
        public byte[] Pixels;
        public int Width;
        public int Height;
        public int Stride;
        public int Channels;         // 1 = gray or bilevel, 3 = colour
        public int BitsPerChannel;   // 1, 8 or 16
        public double XDpi = 300;
        public double YDpi = 300;
        public int PageIndex;
        public int Side;             // 0 = front, 1 = back

        public int BitsPerPixel
        {
            get { return (BitsPerChannel == 1) ? 1 : Channels * BitsPerChannel; }
        }

        public long ByteLength
        {
            get { return (long)Height * Stride; }
        }

        public bool IsValid
        {
            get
            {
                return Pixels != null && Width > 0 && Height > 0 && Stride > 0 &&
                       Pixels.Length >= (long)Height * Stride;
            }
        }

        public override string ToString()
        {
            return Width + "x" + Height + " " + Channels + "ch " + BitsPerChannel + "bpc @ " +
                   Math.Round(XDpi) + "dpi";
        }

        /// <summary>
        /// Converts to a GDI+ bitmap for display and for the existing WinForms
        /// pipeline. 16-bit data is reduced to 8 here - the full-depth buffer stays
        /// available in Pixels for export.
        /// </summary>
        public Bitmap ToBitmap()
        {
            if (!IsValid) return null;

            if (BitsPerChannel == 1) return OneBitToBitmap();

            Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, Width, Height),
                                         ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[bd.Stride];
                int shift = (BitsPerChannel == 16) ? 8 : 0;

                for (int y = 0; y < Height; y++)
                {
                    int src = y * Stride;
                    if (Channels == 3 && BitsPerChannel == 8)
                    {
                        int copy = Math.Min(Width * 3, Math.Min(Stride, bd.Stride));
                        Buffer.BlockCopy(Pixels, src, row, 0, copy);
                    }
                    else if (Channels == 3 && BitsPerChannel == 16)
                    {
                        // Take the high byte of each 16-bit sample (little endian).
                        for (int x = 0; x < Width; x++)
                        {
                            int s = src + x * 6;
                            if (s + 5 >= Pixels.Length) break;
                            row[x * 3 + 0] = Pixels[s + 1];
                            row[x * 3 + 1] = Pixels[s + 3];
                            row[x * 3 + 2] = Pixels[s + 5];
                        }
                    }
                    else if (Channels == 1)
                    {
                        int step = BitsPerChannel / 8;
                        for (int x = 0; x < Width; x++)
                        {
                            int s = src + x * step;
                            if (s + step - 1 >= Pixels.Length) break;
                            byte v = (shift > 0) ? Pixels[s + 1] : Pixels[s];
                            row[x * 3 + 0] = v;
                            row[x * 3 + 1] = v;
                            row[x * 3 + 2] = v;
                        }
                    }
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), bd.Stride);
                }
            }
            finally { bmp.UnlockBits(bd); }

            SetResolution(bmp);
            return bmp;
        }

        Bitmap OneBitToBitmap()
        {
            Bitmap bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, Width, Height),
                                         ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[bd.Stride];
                for (int y = 0; y < Height; y++)
                {
                    int src = y * Stride;
                    for (int x = 0; x < Width; x++)
                    {
                        int byteIdx = src + (x >> 3);
                        if (byteIdx >= Pixels.Length) break;
                        // TWAIN bilevel is CHOCOLATE by default: a set bit means black.
                        bool bitSet = (Pixels[byteIdx] & (0x80 >> (x & 7))) != 0;
                        byte v = bitSet ? (byte)0 : (byte)255;
                        row[x * 3 + 0] = v;
                        row[x * 3 + 1] = v;
                        row[x * 3 + 2] = v;
                    }
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride), bd.Stride);
                }
            }
            finally { bmp.UnlockBits(bd); }

            SetResolution(bmp);
            return bmp;
        }

        void SetResolution(Bitmap bmp)
        {
            try
            {
                float xr = (float)(XDpi > 1 ? XDpi : 300);
                float yr = (float)(YDpi > 1 ? YDpi : 300);
                bmp.SetResolution(xr, yr);
            }
            catch { }
        }

        /// <summary>Builds a RawImage from a GDI+ bitmap (used by the file-import source).</summary>
        public static RawImage FromBitmap(Bitmap bmp)
        {
            if (bmp == null) return null;

            RawImage img = new RawImage();
            img.Width = bmp.Width;
            img.Height = bmp.Height;
            img.Channels = 3;
            img.BitsPerChannel = 8;
            img.Stride = bmp.Width * 3;
            img.Pixels = new byte[(long)img.Height * img.Stride];
            img.XDpi = bmp.HorizontalResolution > 1 ? bmp.HorizontalResolution : 300;
            img.YDpi = bmp.VerticalResolution > 1 ? bmp.VerticalResolution : 300;

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                         ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                for (int y = 0; y < img.Height; y++)
                    Marshal.Copy(new IntPtr(bd.Scan0.ToInt64() + (long)y * bd.Stride),
                                 img.Pixels, y * img.Stride, img.Stride);
            }
            finally { bmp.UnlockBits(bd); }

            return img;
        }
    }
}
