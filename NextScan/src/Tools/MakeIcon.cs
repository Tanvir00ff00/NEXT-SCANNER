using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace NextScan.Tools
{
    class MakeIcon
    {
        static void Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "NextScanner.ico";
            int[] sizes = new int[] { 256, 64, 48, 32, 16 };

            using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                // ICONDIR header
                bw.Write((short)0); // Reserved
                bw.Write((short)1); // Type 1 = Icon
                bw.Write((short)sizes.Length); // Count

                byte[][] imageBytes = new byte[sizes.Length][];
                int offset = 6 + (16 * sizes.Length);

                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    using (Bitmap bmp = GenerateIconBitmap(sz))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        // Save as PNG
                        bmp.Save(ms, ImageFormat.Png);
                        imageBytes[i] = ms.ToArray();
                    }
                }

                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Width
                    bw.Write((byte)(sz >= 256 ? 0 : sz)); // Height
                    bw.Write((byte)0); // Colors
                    bw.Write((byte)0); // Reserved
                    bw.Write((short)1); // Planes
                    bw.Write((short)32); // Bits per pixel
                    bw.Write((int)imageBytes[i].Length); // Bytes in res
                    bw.Write((int)offset); // Offset

                    offset += imageBytes[i].Length;
                }

                for (int i = 0; i < sizes.Length; i++)
                {
                    bw.Write(imageBytes[i]);
                }
            }

            Console.WriteLine("Generated " + outPath);
        }

        static Bitmap GenerateIconBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                float s = size / 24f;
                int pad = Math.Max(1, (int)Math.Round(size * 0.04));
                Rectangle r = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);
                int radius = Math.Max(3, (int)Math.Round(size * 0.22));

                // 1. Dark titanium squircle base
                using (GraphicsPath path = RoundRect(r, radius))
                {
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(0x18, 0x1A, 0x22)))
                        g.FillPath(b, path);

                    // Sleek subtle border
                    using (Pen p = new Pen(Color.FromArgb(0x35, 0x3A, 0x48), Math.Max(1f, 1f * s)))
                        g.DrawPath(p, path);
                }

                // 2. Stylized geometric "N"
                float nLeft = r.X + 5.5f * s;
                float nRight = r.X + r.Width - 5.5f * s;
                float nTop = r.Y + 5.0f * s;
                float nBottom = r.Y + r.Height - 5.0f * s;
                float midY = r.Y + r.Height / 2.0f;

                Color accent = Color.FromArgb(0xFF, 0x7A, 0x00); // Brand Orange

                using (Pen pN = new Pen(accent, Math.Max(1.2f, 1.8f * s)))
                {
                    pN.StartCap = LineCap.Round;
                    pN.EndCap = LineCap.Round;
                    pN.LineJoin = LineJoin.Round;

                    g.DrawLine(pN, nLeft, nTop, nLeft, nBottom);
                    g.DrawLine(pN, nLeft, nTop, nRight, nBottom);
                    g.DrawLine(pN, nRight, nTop, nRight, nBottom);
                }

                // 3. Laser scanner beam
                using (Pen pGlow = new Pen(Color.FromArgb(120, accent), Math.Max(1.5f, 3.4f * s)))
                {
                    pGlow.StartCap = LineCap.Round;
                    pGlow.EndCap = LineCap.Round;
                    g.DrawLine(pGlow, r.X + 2.5f * s, midY, r.X + r.Width - 2.5f * s, midY);
                }

                using (Pen pCore = new Pen(Color.FromArgb(0xFF, 0xFE, 0xE8), Math.Max(1f, 1.3f * s)))
                {
                    pCore.StartCap = LineCap.Round;
                    pCore.EndCap = LineCap.Round;
                    g.DrawLine(pCore, r.X + 3.0f * s, midY, r.X + r.Width - 3.0f * s, midY);
                }
            }
            return bmp;
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
