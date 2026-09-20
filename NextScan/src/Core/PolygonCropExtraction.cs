// =============================================================================
// NextScan Studio - pixel extraction through preview-approved polygon masks
// Plan ref: irregular-document follow-up; preview decides, scan obeys.
// Four corners cannot describe a notch or a curved edge. Every approved vertex
// remains in bed inches and is mapped once into the requested scan region.
// Files remain rectangular RGB/grey/bilevel rasters; excluded pixels are white.
// No foreground detection, standard-size snapping or new edge fitting occurs here.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace NextScan.Core
{
    internal static class PolygonCropExtraction
    {
        internal static RawImage Extract(RawImage page, PointF[][] bedRings, PointF[] bedOrientation,
                                         RectangleF requestedRegion, int index, bool deskew)
        {
            if (page == null || !page.IsValid || bedRings == null || bedRings.Length == 0 ||
                requestedRegion.Width <= 0 || requestedRegion.Height <= 0) return null;
            try
            {
                double angle = 0;
                if (deskew && bedOrientation != null && bedOrientation.Length == 4)
                    angle = Math.Atan2((bedOrientation[1].Y - bedOrientation[0].Y) * page.Height / requestedRegion.Height,
                        (bedOrientation[1].X - bedOrientation[0].X) * page.Width / requestedRegion.Width);
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                List<PointF[]> projected = new List<PointF[]>();
                double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
                foreach (PointF[] ring in bedRings)
                {
                    if (ring == null || ring.Length < 3) return null;
                    PointF[] points = new PointF[ring.Length];
                    for (int vertex = 0; vertex < ring.Length; vertex++)
                    {
                        double x = (ring[vertex].X - requestedRegion.X) * page.Width / requestedRegion.Width;
                        double y = (ring[vertex].Y - requestedRegion.Y) * page.Height / requestedRegion.Height;
                        if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(y) || double.IsInfinity(y)) return null;
                        // Clipping a scan does not authorise extending its data.
                        // Keep the complete plan; sample only captured pixels.
                        double across = x * cosine + y * sine, along = -x * sine + y * cosine;
                        points[vertex] = new PointF((float)across, (float)along);
                        left = Math.Min(left, across); right = Math.Max(right, across);
                        top = Math.Min(top, along); bottom = Math.Max(bottom, along);
                    }
                    projected.Add(points);
                }
                left = Math.Max(left, Math.Min(0, page.Width * cosine) + Math.Min(0, page.Height * sine));
                right = Math.Min(right, Math.Max(0, page.Width * cosine) + Math.Max(0, page.Height * sine));
                top = Math.Max(top, Math.Min(0, -page.Width * sine) + Math.Min(0, page.Height * cosine));
                bottom = Math.Min(bottom, Math.Max(0, -page.Width * sine) + Math.Max(0, page.Height * cosine));
                int width = checked((int)Math.Ceiling(right - left)), height = checked((int)Math.Ceiling(bottom - top));
                if (width <= 0 || height <= 0) return null;
                int stride = page.BitsPerChannel == 1 ? checked((width + 7) / 8)
                    : checked(width * page.Channels * (page.BitsPerChannel / 8));
                RawImage result = new RawImage
                {
                    Width = width, Height = height, Stride = stride, Channels = page.Channels,
                    BitsPerChannel = page.BitsPerChannel, XDpi = page.XDpi, YDpi = page.YDpi,
                    Pixels = new byte[checked(stride * height)], PageIndex = index, Side = page.Side
                };
                for (int offset = 0; offset < result.Pixels.Length; offset++) result.Pixels[offset] = 255;
                double originX = left, originY = top;
                Parallel.For(0, height, delegate(int row)
                {
                    double y = originY + row + 0.5;
                    List<double> intersections = new List<double>();
                    foreach (PointF[] ring in projected)
                        for (int current = 0, previous = ring.Length - 1; current < ring.Length; previous = current++)
                        {
                            PointF first = ring[previous], second = ring[current];
                            if ((first.Y > y) == (second.Y > y)) continue;
                            intersections.Add(first.X + (y - first.Y) * (second.X - first.X) / (second.Y - first.Y));
                        }
                    intersections.Sort();
                    // Even/odd spans preserve concavities and approved inner
                    // holes without evaluating a polygon for every output pixel.
                    for (int span = 0; span + 1 < intersections.Count; span += 2)
                    {
                        int firstColumn = Math.Max(0, (int)Math.Ceiling(intersections[span] - originX - 0.5));
                        int lastColumn = Math.Min(width, (int)Math.Ceiling(intersections[span + 1] - originX - 0.5));
                        for (int column = firstColumn; column < lastColumn; column++)
                        {
                            double x = originX + column + 0.5;
                            double sourceX = x * cosine - y * sine - 0.5, sourceY = x * sine + y * cosine - 0.5;
                            int sourceLeft = (int)Math.Floor(sourceX), sourceTop = (int)Math.Floor(sourceY);
                            double horizontal = sourceX - sourceLeft, vertical = sourceY - sourceTop;
                            for (int channel = 0; channel < page.Channels; channel++)
                            {
                                double upper = Read(page, sourceLeft, sourceTop, channel) * (1 - horizontal)
                                    + Read(page, sourceLeft + 1, sourceTop, channel) * horizontal;
                                double lower = Read(page, sourceLeft, sourceTop + 1, channel) * (1 - horizontal)
                                    + Read(page, sourceLeft + 1, sourceTop + 1, channel) * horizontal;
                                Write(result, column, row, channel, (int)Math.Round(upper * (1 - vertical) + lower * vertical));
                            }
                        }
                    }
                });
                return result;
            }
            catch (Exception) { return null; }
        }

        static int Read(RawImage page, int x, int y, int channel)
        {
            if (x < 0 || y < 0 || x >= page.Width || y >= page.Height) return page.BitsPerChannel == 16 ? 65535 : 255;
            if (page.BitsPerChannel == 1) return (page.Pixels[y * page.Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 255 : 0;
            int offset = y * page.Stride + (x * page.Channels + channel) * (page.BitsPerChannel / 8);
            return page.BitsPerChannel == 16 ? page.Pixels[offset] | page.Pixels[offset + 1] << 8 : page.Pixels[offset];
        }

        static void Write(RawImage page, int x, int y, int channel, int value)
        {
            if (page.BitsPerChannel == 1)
            {
                int offset = y * page.Stride + (x >> 3), bit = 0x80 >> (x & 7);
                if (value < 128) page.Pixels[offset] = (byte)(page.Pixels[offset] & ~bit);
                return;
            }
            int destination = y * page.Stride + (x * page.Channels + channel) * (page.BitsPerChannel / 8);
            page.Pixels[destination] = (byte)value;
            if (page.BitsPerChannel == 16) page.Pixels[destination + 1] = (byte)(value >> 8);
        }
    }
}
