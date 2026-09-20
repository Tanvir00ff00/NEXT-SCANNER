// ============================================================================
// AutoCropEngine.cs -- NextScan Studio, build plan sections 4--7.
// A scan is the glass, not the document. This policy, edge fitting and extraction
// layer keeps a tilted signature intact and turns several items into separate
// pages. Without it a hull can follow a hair, a shadow can become a photograph,
// and an uncertain detector can silently discard the operator's scan.
// All processing stays in managed pixel buffers; there is no GDI+ dependency.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;

namespace NextScan.Core
{
    public enum CropConfidence
    {
        None = 0, Low = 1, Good = 2, High = 3
    }

    public class CropRegion
    {
        public RotatedBox Box;
        // Free-form stock is carried separately: the rectangle remains an
        // orientation/envelope for legacy engine callers, never the crop mask.
        public PointF[] Outline;
        public float SkewDegrees;
        public CropConfidence Confidence;
        public float Score;
        public string Reason;
        public RectangleF NormRect;
        public double WidthInches;
        public double HeightInches;
        public int Index;
    }

    public class AutoCropOptions
    {
        public bool Enabled = true;
        public bool MultiRegion = true;
        /// <summary>Extra margin outside the detected edge, in millimetres.</summary>
        public double MarginMm = 0.5;
        /// <summary>Straighten each region during extraction.</summary>
        public bool Deskew = true;
        /// <summary>Minimum physical area, in square millimetres.</summary>
        public double MinAreaMm2 = 400.0;
        public CropConfidence MinConfidence = CropConfidence.Low;
        public int MaxRegions = 16;
        /// <summary>Maximum long edge of the box-averaged detection image.</summary>
        public int WorkingLongEdge = 1400;

        /// <summary>
        /// Ask the segmentation model, when one is installed.
        ///
        /// Off unless a caller asks, so that the test suites stay deterministic
        /// and quick: they measure the classical engine, and a model would make
        /// eighty cases each a second and a half slower while answering
        /// questions about synthetic pages it was never meant to see. The
        /// application turns it on for previews, which is the only place
        /// detection happens at all.
        /// </summary>
        public bool UseModel;

        /// <summary>
        /// Look harder, at the cost of time.
        ///
        /// Detection is deterministic: asked the same question about the same
        /// capture it returns the same answer, so an operator who presses
        /// "detect again" after a miss gets the miss again. This is what that
        /// button now asks for instead -- a denser sweep and a lower bar, which
        /// is a different question and can have a different answer.
        /// </summary>
        public bool Thorough;
    }

    public class AutoCropResult
    {
        public List<CropRegion> Regions = new List<CropRegion>();
        public bool Detected;
        public string Notes = "";
        public long ElapsedMs;
    }

    public static class AutoCropEngine
    {
        public static Action<string> Log;

        // ===== Detection =====

        /// <summary>Finds document borders. Uncertainty returns an empty result.</summary>
        public static AutoCropResult Detect(RawImage page, AutoCropOptions options)
        {
            AutoCropResult result = new AutoCropResult();
            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                AutoCropOptions settings = Snapshot(options);
                string problem = Validate(page);
                if (problem != null)
                {
                    result.Notes = problem;
                    return result;
                }
                if (!settings.Enabled)
                {
                    result.Notes = "Automatic cropping is switched off.";
                    return result;
                }
                WorkImage working = Downscale(page, settings.WorkingLongEdge);
                EstimateBackground(working);
                byte[] mask = Evidence(working);
                List<Component> pieces = Components(mask, working.Width, working.Height, true);
                // Cleaning before closing prevents isolated dust from growing bridges.
                int minimumPixels = Math.Max(4, (int)(working.Width * (long)working.Height / 500000));
                foreach (Component piece in pieces)
                {
                    if (piece.Count >= minimumPixels)
                    {
                        continue;
                    }
                    foreach (int position in piece.Pixels)
                    {
                        mask[position] = 0;
                    }
                }
                mask = Close(mask, working.Width, working.Height);
                pieces = Components(mask, working.Width, working.Height, false);
                MergeFragments(pieces, working, page);
                List<CropRegion> candidates = new List<CropRegion>();
                bool touchesFrame = false;
                foreach (Component piece in pieces)
                {
                    if (piece.Count < minimumPixels)
                    {
                        continue;
                    }
                    int touching = (piece.Left <= working.Left + 2 ? 1 : 0)
                        + (piece.Top <= working.Top + 2 ? 1 : 0)
                        + (piece.Right >= working.Right - 3 ? 1 : 0)
                        + (piece.Bottom >= working.Bottom - 3 ? 1 : 0);
                    if (touching >= 3)
                    {
                        touchesFrame = true;
                        continue;
                    }
                    CropRegion candidate = Fit(piece, working, page, settings);
                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                    }
                }
                // An enclosing border takes precedence over disconnected printing inside it.
                RemoveContained(candidates);
                ApplyConsistency(candidates);
                candidates.RemoveAll(delegate(CropRegion region)
                {
                    return region.Confidence < settings.MinConfidence;
                });
                candidates.Sort(delegate(CropRegion first, CropRegion second)
                {
                    return second.Score.CompareTo(first.Score);
                });
                if (!settings.MultiRegion && candidates.Count > 1)
                {
                    // Two comparably credible items do not identify a single intended page.
                    if (candidates[0].Score - candidates[1].Score < 0.12f)
                    {
                        result.Notes = "Several equally plausible documents were found; single-document cropping is ambiguous.";
                        return result;
                    }
                    candidates.RemoveRange(1, candidates.Count - 1);
                }
                if (candidates.Count > settings.MaxRegions)
                {
                    candidates.RemoveRange(settings.MaxRegions, candidates.Count - settings.MaxRegions);
                    result.Notes = "The region limit was reached; only the strongest candidates are included.";
                }
                ReadingOrder(candidates);
                result.Regions = candidates;
                result.Detected = candidates.Count > 0;
                if (!result.Detected)
                {
                    result.Notes = touchesFrame
                        ? "The document reaches the edges of the glass, so its border cannot be found."
                        : "The page appears to be empty, or no sufficiently clear document border was found.";
                }
            }
            catch (Exception exception)
            {
                result.Regions.Clear();
                result.Detected = false;
                result.Notes = "Automatic cropping could not establish a safe border: " + exception.Message;
                SafeLog(result.Notes);
            }
            finally
            {
                result.ElapsedMs = elapsed.ElapsedMilliseconds;
            }
            return result;
        }

        // ===== Full-resolution extraction =====

        /// <summary>Produces a separate buffer at the original depth and DPI.</summary>
        public static RawImage Extract(RawImage page, CropRegion region, AutoCropOptions options)
        {
            try
            {
                if (Validate(page) != null || !Usable(region, page))
                {
                    return null;
                }
                AutoCropOptions settings = Snapshot(options);
                double angle = settings.Deskew ? region.SkewDegrees * Math.PI / 180.0 : 0.0;
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                double minimumHorizontal = double.MaxValue;
                double maximumHorizontal = double.MinValue;
                double minimumVertical = double.MaxValue;
                double maximumVertical = double.MinValue;
                foreach (PointF corner in region.Box.Corners)
                {
                    double horizontal = corner.X * cosine + corner.Y * sine;
                    double vertical = -corner.X * sine + corner.Y * cosine;
                    minimumHorizontal = Math.Min(minimumHorizontal, horizontal);
                    maximumHorizontal = Math.Max(maximumHorizontal, horizontal);
                    minimumVertical = Math.Min(minimumVertical, vertical);
                    maximumVertical = Math.Max(maximumVertical, vertical);
                }
                // With unequal scanner resolutions a rotated axis has its own pixels/inch.
                double horizontalDpi = AxisDpi(page, cosine, sine);
                double verticalDpi = AxisDpi(page, -sine, cosine);
                double horizontalMargin = settings.MarginMm * horizontalDpi / 25.4;
                double verticalMargin = settings.MarginMm * verticalDpi / 25.4;
                double glassLeft = Math.Min(0, page.Width * cosine) + Math.Min(0, page.Height * sine);
                double glassRight = Math.Max(0, page.Width * cosine) + Math.Max(0, page.Height * sine);
                double glassTop = Math.Min(0, -page.Width * sine) + Math.Min(0, page.Height * cosine);
                double glassBottom = Math.Max(0, -page.Width * sine) + Math.Max(0, page.Height * cosine);
                minimumHorizontal = Math.Max(glassLeft, minimumHorizontal - horizontalMargin);
                maximumHorizontal = Math.Min(glassRight, maximumHorizontal + horizontalMargin);
                minimumVertical = Math.Max(glassTop, minimumVertical - verticalMargin);
                maximumVertical = Math.Min(glassBottom, maximumVertical + verticalMargin);
                int width = checked((int)Math.Ceiling(maximumHorizontal - minimumHorizontal));
                int height = checked((int)Math.Ceiling(maximumVertical - minimumVertical));
                if (width <= 0 || height <= 0)
                {
                    return null;
                }
                long stride = page.BitsPerChannel == 1 ? ((long)width + 7) / 8
                    : (long)width * page.Channels * (page.BitsPerChannel / 8);
                long length = stride * height;
                if (length > int.MaxValue || stride > int.MaxValue)
                {
                    return null;
                }
                RawImage output = new RawImage
                {
                    Width = width, Height = height, Stride = (int)stride,
                    Channels = page.Channels, BitsPerChannel = page.BitsPerChannel,
                    Pixels = new byte[(int)length], XDpi = page.XDpi, YDpi = page.YDpi,
                    PageIndex = region.Index, Side = page.Side
                };
                double[] background = NativeBackground(page);
                Parallel.For(0, height, delegate(int row)
                {
                    double vertical = minimumVertical + row + 0.5;
                    for (int column = 0; column < width; column++)
                    {
                        double horizontal = minimumHorizontal + column + 0.5;
                        // Inverse mapping by +angle implements a -angle rotation. Coordinates
                        // describe pixel centres; omitting the half pixel causes repeated blur.
                        double sourceX = horizontal * cosine - vertical * sine - 0.5;
                        double sourceY = horizontal * sine + vertical * cosine - 0.5;
                        int left = (int)Math.Floor(sourceX);
                        int top = (int)Math.Floor(sourceY);
                        double fractionX = sourceX - left;
                        double fractionY = sourceY - top;
                        for (int channel = 0; channel < page.Channels; channel++)
                        {
                            double upper = Sample(page, left, top, channel, background[channel]) * (1 - fractionX)
                                + Sample(page, left + 1, top, channel, background[channel]) * fractionX;
                            double lower = Sample(page, left, top + 1, channel, background[channel]) * (1 - fractionX)
                                + Sample(page, left + 1, top + 1, channel, background[channel]) * fractionX;
                            // Bilevel data has no intermediate greys to store: interpolate its
                            // coverage first, then quantise once, rather than rotate with NN.
                            int value = (int)Math.Round(upper * (1 - fractionY) + lower * fractionY);
                            Write(output, column, row, channel, value);
                        }
                    }
                });
                return output;
            }
            catch (Exception exception)
            {
                SafeLog("Crop extraction failed: " + exception.Message);
                return null;
            }
        }

        /// <summary>Uncertain detection or incomplete extraction preserves the original scan.</summary>
        public static List<RawImage> DetectAndExtract(RawImage page, AutoCropOptions options)
        {
            try
            {
                AutoCropOptions settings = Snapshot(options);
                AutoCropResult result = Detect(page, settings);
                if (!result.Detected)
                {
                    return new List<RawImage>
                    {
                        page
                    };
                }
                List<RawImage> output = new List<RawImage>();
                foreach (CropRegion region in result.Regions)
                {
                    RawImage extracted = Extract(page, region, settings);
                    if (extracted == null)
                    {
                        // Returning only the successful cards would silently lose an item.
                        return new List<RawImage>
                        {
                            page
                        };
                    }
                    output.Add(extracted);
                }
                return output;
            }
            catch (Exception exception)
            {
                SafeLog("Automatic cropping failed: " + exception.Message);
                return new List<RawImage>
                {
                    page
                };
            }
        }

        // ===== Pixel evidence =====

        private sealed class WorkImage
        {
            internal int Width, Height, Left, Top, Right, Bottom;
            internal int NativeLeft, NativeTop, NativeRight, NativeBottom;
            internal double ScaleX, ScaleY, Noise;
            internal byte[] Blue, Green, Red, Gray;
            internal float[] Gradient;
            internal double[] Background;
            internal double ColourThreshold;
        }

        private static WorkImage Downscale(RawImage page, int longEdge)
        {
            double scale = Math.Min(1.0, (double)longEdge / Math.Max(page.Width, page.Height));
            int width = Math.Max(1, (int)Math.Round(page.Width * scale));
            int height = Math.Max(1, (int)Math.Round(page.Height * scale));
            int nativeLeft = StripDepth(page, 2);
            int nativeTop = StripDepth(page, 0);
            int nativeRight = page.Width - StripDepth(page, 3);
            int nativeBottom = page.Height - StripDepth(page, 1);
            WorkImage result = new WorkImage
            {
                Width = width, Height = height,
                Left = (int)((long)nativeLeft * width / page.Width),
                Top = (int)((long)nativeTop * height / page.Height),
                Right = (int)Math.Ceiling((double)nativeRight * width / page.Width),
                Bottom = (int)Math.Ceiling((double)nativeBottom * height / page.Height),
                NativeLeft = nativeLeft, NativeTop = nativeTop,
                NativeRight = nativeRight, NativeBottom = nativeBottom,
                ScaleX = (double)page.Width / width, ScaleY = (double)page.Height / height,
                Blue = new byte[width * height], Green = new byte[width * height],
                Red = new byte[width * height], Gray = new byte[width * height]
            };
            Parallel.For(0, height, delegate(int row)
            {
                int top = Math.Max(nativeTop, (int)((long)row * page.Height / height));
                int bottom = Math.Min(nativeBottom, (int)((long)(row + 1) * page.Height / height));
                for (int column = 0; column < width; column++)
                {
                    int left = Math.Max(nativeLeft, (int)((long)column * page.Width / width));
                    int right = Math.Min(nativeRight, (int)((long)(column + 1) * page.Width / width));
                    if (bottom <= top || right <= left)
                    {
                        continue;
                    }
                    long blue = 0, green = 0, red = 0;
                    for (int sourceY = top; sourceY < bottom; sourceY++)
                    {
                        int rowStart = sourceY * page.Stride;
                        for (int sourceX = left; sourceX < right; sourceX++)
                        {
                            int blueValue, greenValue, redValue;
                            Read8(page, rowStart, sourceX, out blueValue, out greenValue, out redValue);
                            blue += blueValue;
                            green += greenValue;
                            red += redValue;
                        }
                    }
                    // Integer box partitions cover every input pixel once, including the
                    // final row. Their edge error is below one source pixel, never one work pixel.
                    long count = (long)(bottom - top) * (right - left);
                    int position = row * width + column;
                    result.Blue[position] = (byte)(blue / count);
                    result.Green[position] = (byte)(green / count);
                    result.Red[position] = (byte)(red / count);
                    result.Gray[position] = (byte)((red * 299 + green * 587 + blue * 114) / (1000 * count));
                }
            });
            return result;
        }

        private static void Read8(RawImage page, int rowStart, int column,
            out int blue, out int green, out int red)
        {
            if (page.BitsPerChannel == 1)
            {
                // Scanner bilevel rows are packed MSB first, and set bits mean white.
                blue = (page.Pixels[rowStart + (column >> 3)] & (0x80 >> (column & 7))) != 0 ? 255 : 0;
                green = red = blue;
                return;
            }
            int bytes = page.BitsPerChannel / 8;
            int offset = rowStart + column * page.Channels * bytes + bytes - 1;
            blue = page.Pixels[offset];
            green = page.Channels == 3 ? page.Pixels[offset + bytes] : blue;
            red = page.Channels == 3 ? page.Pixels[offset + 2 * bytes] : blue;
        }

        private static void EstimateBackground(WorkImage image)
        {
            List<double>[] samples = new List<double>[]
            {
                new List<double>(), new List<double>(), new List<double>()
            };
            int step = Math.Max(1, Math.Max(image.Width, image.Height) / 512);
            for (int horizontal = image.Left; horizontal < image.Right; horizontal += step)
            {
                AddBackgroundSample(image, horizontal, image.Top, samples);
                AddBackgroundSample(image, horizontal, image.Bottom - 1, samples);
            }
            for (int vertical = image.Top; vertical < image.Bottom; vertical += step)
            {
                AddBackgroundSample(image, image.Left, vertical, samples);
                AddBackgroundSample(image, image.Right - 1, vertical, samples);
            }
            image.Background = new double[]
            {
                Median(samples[0]), Median(samples[1]), Median(samples[2])
            };
            List<double> deviations = new List<double>();
            for (int index = 0; index < samples[0].Count; index++)
            {
                deviations.Add(Math.Max(Math.Abs(samples[0][index] - image.Background[0]),
                    Math.Max(Math.Abs(samples[1][index] - image.Background[1]),
                    Math.Abs(samples[2][index] - image.Background[2]))));
            }
            // A median absolute deviation does not let one card at the frame set the noise.
            image.Noise = Median(deviations) * 1.4826;
            image.ColourThreshold = Math.Max(18, image.Noise * 3);
            // Excluded strip pixels must not feed Sobel or the variance window. Leaving
            // their black values here would reconnect the strip to a page touching it.
            for (int row = 0; row < image.Height; row++)
            {
                for (int column = 0; column < image.Width; column++)
                {
                    if (row >= image.Top && row < image.Bottom && column >= image.Left && column < image.Right)
                    {
                        continue;
                    }
                    int position = row * image.Width + column;
                    image.Blue[position] = (byte)image.Background[0];
                    image.Green[position] = (byte)image.Background[1];
                    image.Red[position] = (byte)image.Background[2];
                    image.Gray[position] = (byte)((image.Background[2] * 299 + image.Background[1] * 587
                        + image.Background[0] * 114) / 1000);
                }
            }
        }

        private static int StripDepth(RawImage page, int side)
        {
            // Measure source rows before averaging: on one copier the strip occupied
            // exactly seven rows and row seven was paper. A percentage guard, or a
            // rounded working-image strip, would throw away real document pixels.
            // Only contiguous, very dark, nearly complete rows qualify. Four per cent
            // is a search ceiling, never a fixed amount removed from the image.
            int depthLimit = Math.Max(1, (side < 2 ? page.Height : page.Width) / 25);
            int length = side < 2 ? page.Width : page.Height;
            int step = Math.Max(1, length / 1024);
            int depth = 0;
            for (; depth < depthLimit; depth++)
            {
                int dark = 0, samples = 0;
                for (int along = 0; along < length; along += step)
                {
                    int horizontal = side == 2 ? depth : side == 3 ? page.Width - 1 - depth : along;
                    int vertical = side == 0 ? depth : side == 1 ? page.Height - 1 - depth : along;
                    int blue, green, red;
                    Read8(page, vertical * page.Stride, horizontal, out blue, out green, out red);
                    samples++;
                    if ((red * 299 + green * 587 + blue * 114) / 1000 < 48)
                    {
                        dark++;
                    }
                }
                if (dark < samples * 0.94)
                {
                    break;
                }
            }
            // A dark lid continuing beyond the ceiling is a background, not a strip.
            return depth == depthLimit ? 0 : depth;
        }

        private static void AddBackgroundSample(WorkImage image, int horizontal, int vertical, List<double>[] samples)
        {
            int position = vertical * image.Width + horizontal;
            samples[0].Add(image.Blue[position]);
            samples[1].Add(image.Green[position]);
            samples[2].Add(image.Red[position]);
        }

        private static byte[] Evidence(WorkImage image)
        {
            int width = image.Width;
            int height = image.Height;
            byte[] mask = new byte[width * height];
            image.Gradient = new float[mask.Length];
            int integralStride = width + 1;
            double[] sums = new double[(width + 1) * (height + 1)];
            double[] squares = new double[sums.Length];
            for (int row = 0; row < height; row++)
            {
                double sum = 0, square = 0;
                for (int column = 0; column < width; column++)
                {
                    int value = image.Gray[row * width + column];
                    sum += value;
                    square += value * value;
                    int position = (row + 1) * integralStride + column + 1;
                    sums[position] = sums[position - integralStride] + sum;
                    squares[position] = squares[position - integralStride] + square;
                }
            }
            double gradientThreshold = Math.Max(12, image.Noise * 3);
            double contrastThreshold = Math.Max(7, image.Noise * 3);
            Parallel.For(Math.Max(1, image.Top), Math.Max(1, Math.Min(height - 1, image.Bottom)), delegate(int row)
            {
                for (int column = Math.Max(1, image.Left); column < Math.Min(width - 1, image.Right); column++)
                {
                    int position = row * width + column;
                    byte[] gray = image.Gray;
                    int gradientX = gray[position - width + 1] + 2 * gray[position + 1] + gray[position + width + 1]
                        - gray[position - width - 1] - 2 * gray[position - 1] - gray[position + width - 1];
                    int gradientY = gray[position + width - 1] + 2 * gray[position + width] + gray[position + width + 1]
                        - gray[position - width - 1] - 2 * gray[position - width] - gray[position - width + 1];
                    double gradient = Math.Sqrt((double)gradientX * gradientX + (double)gradientY * gradientY) / 4;
                    image.Gradient[position] = (float)gradient;
                    int left = Math.Max(image.Left, column - 2);
                    int right = Math.Min(image.Right, column + 3);
                    int top = Math.Max(image.Top, row - 2);
                    int bottom = Math.Min(image.Bottom, row + 3);
                    double count = (right - left) * (bottom - top);
                    double mean = Window(sums, integralStride, left, top, right, bottom) / count;
                    double variance = Window(squares, integralStride, left, top, right, bottom) / count - mean * mean;
                    // Colour finds plain paper against a different lid. Gradients find an
                    // edge shadow on a matching lid. Local variance retains textured print
                    // as evidence inside the border instead of requiring solid foreground.
                    double colour = ColourDistance(image, position);
                    if (colour >= image.ColourThreshold || gradient >= gradientThreshold
                        || variance >= contrastThreshold * contrastThreshold)
                    {
                        mask[position] = 1;
                    }
                }
            });
            return mask;
        }

        private static double Window(double[] integral, int stride, int left, int top, int right, int bottom)
        {
            return integral[bottom * stride + right] - integral[top * stride + right]
                - integral[bottom * stride + left] + integral[top * stride + left];
        }

        private static double ColourDistance(WorkImage image, int position)
        {
            return Math.Max(Math.Abs(image.Blue[position] - image.Background[0]),
                Math.Max(Math.Abs(image.Green[position] - image.Background[1]),
                Math.Abs(image.Red[position] - image.Background[2])));
        }

        // ===== Components and geometry =====

        private sealed class Component
        {
            internal int Left = int.MaxValue, Top = int.MaxValue, Right, Bottom, Count;
            internal List<int> Pixels = new List<int>();
            internal List<PointF> Boundary = new List<PointF>();
        }

        private static List<Component> Components(byte[] mask, int width, int height, bool keepPixels)
        {
            byte[] visited = new byte[mask.Length];
            int[] queue = new int[mask.Length];
            List<Component> output = new List<Component>();
            for (int start = 0; start < mask.Length; start++)
            {
                if (mask[start] == 0 || visited[start] != 0)
                {
                    continue;
                }
                Component component = new Component();
                int head = 0, tail = 1;
                queue[0] = start;
                visited[start] = 1;
                while (head < tail)
                {
                    int position = queue[head++];
                    int horizontal = position % width;
                    int vertical = position / width;
                    component.Count++;
                    component.Left = Math.Min(component.Left, horizontal);
                    component.Right = Math.Max(component.Right, horizontal);
                    component.Top = Math.Min(component.Top, vertical);
                    component.Bottom = Math.Max(component.Bottom, vertical);
                    if (keepPixels)
                    {
                        component.Pixels.Add(position);
                    }
                    bool boundary = false;
                    for (int changeY = -1; changeY <= 1; changeY++)
                    {
                        for (int changeX = -1; changeX <= 1; changeX++)
                        {
                            int nextX = horizontal + changeX;
                            int nextY = vertical + changeY;
                            if (nextX < 0 || nextX >= width || nextY < 0 || nextY >= height)
                            {
                                boundary = true;
                                continue;
                            }
                            int next = nextY * width + nextX;
                            if (mask[next] == 0)
                            {
                                boundary = true;
                            }
                            else if (visited[next] == 0)
                            {
                                visited[next] = 1;
                                queue[tail++] = next;
                            }
                        }
                    }
                    if (boundary && !keepPixels)
                    {
                        component.Boundary.Add(new PointF(horizontal + 0.5f, vertical + 0.5f));
                    }
                }
                output.Add(component);
            }
            return output;
        }

        private static byte[] Close(byte[] source, int width, int height)
        {
            byte[] first = new byte[source.Length];
            byte[] second = new byte[source.Length];
            // A one-pixel radius joins broken edge shadows without bridging 4 mm gaps.
            for (int row = 1; row < height - 1; row++)
            {
                for (int column = 1; column < width - 1; column++)
                {
                    int position = row * width + column;
                    first[position] = (byte)(source[position - 1] | source[position] | source[position + 1]);
                }
            }
            for (int position = width; position < source.Length - width; position++)
            {
                second[position] = (byte)(first[position - width] | first[position] | first[position + width]);
            }
            for (int row = 1; row < height - 1; row++)
            {
                for (int column = 1; column < width - 1; column++)
                {
                    int position = row * width + column;
                    first[position] = (byte)(second[position - 1] & second[position] & second[position + 1]);
                }
            }
            for (int position = width; position < source.Length - width; position++)
            {
                second[position] = (byte)(first[position - width] & first[position] & first[position + width]);
            }
            return second;
        }

        private static void MergeFragments(List<Component> pieces, WorkImage image, RawImage page)
        {
            // Both physical and relative limits matter: a 4 mm inter-card gap must
            // survive regardless of scan resolution. Overlap here means mask bounds;
            // narrow overlaps between tilted neighbours are checked against hulls.
            double physicalGap = 2.5 * Math.Min(page.XDpi / image.ScaleX, page.YDpi / image.ScaleY) / 25.4;
            for (int first = 0; first < pieces.Count; first++)
            {
                if (pieces[first].Boundary.Count < 8)
                {
                    continue;
                }
                for (int second = first + 1; second < pieces.Count; second++)
                {
                    Component left = pieces[first];
                    Component right = pieces[second];
                    if (right.Boundary.Count < 8)
                    {
                        continue;
                    }
                    double gapX = Math.Max(0, Math.Max(left.Left, right.Left) - Math.Min(left.Right, right.Right) - 1);
                    double gapY = Math.Max(0, Math.Max(left.Top, right.Top) - Math.Min(left.Bottom, right.Bottom) - 1);
                    double smaller = Math.Min(Math.Min(left.Right - left.Left, left.Bottom - left.Top),
                        Math.Min(right.Right - right.Left, right.Bottom - right.Top));
                    double gapLimit = Math.Min(physicalGap, smaller * 0.06);
                    if (Math.Sqrt(gapX * gapX + gapY * gapY) > gapLimit)
                    {
                        continue;
                    }
                    List<PointF> leftHull = Hull(left.Boundary);
                    List<PointF> rightHull = Hull(right.Boundary);
                    if (Separated(leftHull, rightHull, gapLimit) || Separated(rightHull, leftHull, gapLimit))
                    {
                        continue;
                    }
                    // Aligned fragments need substantial overlap along their shared edge;
                    // proximity at a single corner is not evidence of a divided photograph.
                    double overlapX = Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left);
                    double overlapY = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top);
                    if (gapX > 0 && overlapY < smaller * 0.5 || gapY > 0 && overlapX < smaller * 0.5)
                    {
                        continue;
                    }
                    left.Left = Math.Min(left.Left, right.Left);
                    left.Right = Math.Max(left.Right, right.Right);
                    left.Top = Math.Min(left.Top, right.Top);
                    left.Bottom = Math.Max(left.Bottom, right.Bottom);
                    left.Count += right.Count;
                    left.Boundary.AddRange(right.Boundary);
                    pieces.RemoveAt(second);
                    // Revisit earlier fragments after the aggregate grows.
                    second = first;
                }
            }
        }

        private static bool Separated(List<PointF> first, List<PointF> second, double gap)
        {
            for (int index = 0; index < first.Count; index++)
            {
                PointF start = first[index];
                PointF end = first[(index + 1) % first.Count];
                double normalX = -(end.Y - start.Y);
                double normalY = end.X - start.X;
                double length = Math.Sqrt(normalX * normalX + normalY * normalY);
                if (length == 0)
                {
                    continue;
                }
                double maximum = double.MinValue;
                foreach (PointF point in second)
                {
                    maximum = Math.Max(maximum, ((point.X - start.X) * normalX + (point.Y - start.Y) * normalY) / length);
                }
                if (maximum < -gap)
                {
                    return true;
                }
            }
            return false;
        }

        private static List<PointF> Hull(List<PointF> points)
        {
            List<PointF> sorted = new List<PointF>(points);
            sorted.Sort(delegate(PointF first, PointF second)
            {
                int comparison = first.X.CompareTo(second.X);
                return comparison != 0 ? comparison : first.Y.CompareTo(second.Y);
            });
            List<PointF> hull = new List<PointF>();
            foreach (PointF point in sorted)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(point);
            }
            int lowerCount = hull.Count;
            for (int index = sorted.Count - 2; index >= 0; index--)
            {
                while (hull.Count > lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], sorted[index]) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(sorted[index]);
            }
            if (hull.Count > 1)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        private static double Cross(PointF origin, PointF first, PointF second)
        {
            return (double)(first.X - origin.X) * (second.Y - origin.Y)
                - (double)(first.Y - origin.Y) * (second.X - origin.X);
        }

        private sealed class Frame
        {
            internal double Angle, Left, Right, Top, Bottom;
        }

        private static Frame MinimumRectangle(List<PointF> hull)
        {
            Frame best = null;
            double bestArea = double.MaxValue;
            for (int edge = 0; edge < hull.Count; edge++)
            {
                PointF first = hull[edge];
                PointF second = hull[(edge + 1) % hull.Count];
                double angle = Normalise(Math.Atan2(second.Y - first.Y, second.X - first.X) * 180 / Math.PI) * Math.PI / 180;
                double cosine = Math.Cos(angle), sine = Math.Sin(angle);
                Frame frame = new Frame
                {
                    Angle = angle, Left = double.MaxValue, Top = double.MaxValue,
                    Right = double.MinValue, Bottom = double.MinValue
                };
                foreach (PointF point in hull)
                {
                    double horizontal = point.X * cosine + point.Y * sine;
                    double vertical = -point.X * sine + point.Y * cosine;
                    frame.Left = Math.Min(frame.Left, horizontal);
                    frame.Right = Math.Max(frame.Right, horizontal);
                    frame.Top = Math.Min(frame.Top, vertical);
                    frame.Bottom = Math.Max(frame.Bottom, vertical);
                }
                double area = (frame.Right - frame.Left) * (frame.Bottom - frame.Top);
                if (area < bestArea)
                {
                    best = frame;
                    bestArea = area;
                }
            }
            return best;
        }

        // ---- independent robust edge fitting ----

        private sealed class EdgeLine
        {
            internal double Intercept, Slope, Coverage, Strength;
        }

        private static CropRegion Fit(Component component, WorkImage image, RawImage page, AutoCropOptions settings)
        {
            List<PointF> hull = Hull(component.Boundary);
            if (hull.Count < 4)
            {
                return null;
            }
            Frame frame = MinimumRectangle(hull);
            double width = frame.Right - frame.Left;
            double height = frame.Bottom - frame.Top;
            double roughArea = width * image.ScaleX / page.XDpi * height * image.ScaleY / page.YDpi * 25.4 * 25.4;
            if (width < 8 || height < 8 || roughArea < settings.MinAreaMm2 * 0.8)
            {
                return null;
            }
            EdgeLine[] edges = new EdgeLine[4];
            for (int edge = 0; edge < 4; edge++)
            {
                edges[edge] = FitEdge(component.Boundary, frame, edge, image);
                if (edges[edge] == null || edges[edge].Coverage < 0.55)
                {
                    return null;
                }
            }
            PointF[] local = new PointF[]
            {
                Intersect(edges[0], edges[2]), Intersect(edges[1], edges[2]),
                Intersect(edges[1], edges[3]), Intersect(edges[0], edges[3])
            };
            double squareError = 0;
            for (int vertical = 0; vertical < 2; vertical++)
            {
                for (int horizontal = 2; horizontal < 4; horizontal++)
                {
                    squareError = Math.Max(squareError, Math.Abs(Math.Atan(edges[vertical].Slope)
                        + Math.Atan(edges[horizontal].Slope)) * 180 / Math.PI);
                }
            }
            // A modest trapezoid is retained at Low confidence with its actual corners.
            // Severe perspective has no safe rectangular crop; this is not a dewarper.
            if (squareError > 10)
            {
                return null;
            }
            PointF[] corners = new PointF[4];
            double cosine = Math.Cos(frame.Angle), sine = Math.Sin(frame.Angle);
            for (int index = 0; index < 4; index++)
            {
                double horizontal = (local[index].X * cosine - local[index].Y * sine) * image.ScaleX;
                double vertical = (local[index].X * sine + local[index].Y * cosine) * image.ScaleY;
                // Never hide an extrapolated corner outside the glass by clipping it into
                // a plausible rectangle. A subpixel boundary at the frame is tolerated.
                if (!Finite(horizontal) || !Finite(vertical) || horizontal < -image.ScaleX
                    || vertical < -image.ScaleY || horizontal > page.Width + image.ScaleX
                    || vertical > page.Height + image.ScaleY)
                {
                    return null;
                }
                corners[index] = new PointF((float)Math.Max(image.NativeLeft, Math.Min(image.NativeRight, horizontal)),
                    (float)Math.Max(image.NativeTop, Math.Min(image.NativeBottom, vertical)));
            }
            double areaPixels = PolygonArea(corners);
            if (areaPixels * 25.4 * 25.4 / (page.XDpi * page.YDpi) < settings.MinAreaMm2)
            {
                return null;
            }
            double difference = 0;
            int texture = 0, sampleCount = 0;
            // The central three fifths exclude the edge shadow itself from texture.
            for (int row = 0; row < 17; row++)
            {
                for (int column = 0; column < 17; column++)
                {
                    double horizontal = frame.Left + width * (0.2 + 0.6 * column / 16);
                    double vertical = frame.Top + height * (0.2 + 0.6 * row / 16);
                    int sourceX = (int)(horizontal * cosine - vertical * sine);
                    int sourceY = (int)(horizontal * sine + vertical * cosine);
                    if (sourceX < 0 || sourceX >= image.Width || sourceY < 0 || sourceY >= image.Height)
                    {
                        continue;
                    }
                    int position = sourceY * image.Width + sourceX;
                    difference += ColourDistance(image, position);
                    texture += image.Gradient[position] > Math.Max(12, image.Noise * 3) ? 1 : 0;
                    sampleCount++;
                }
            }
            double coverage = 0, strength = 0;
            foreach (EdgeLine edge in edges)
            {
                coverage += edge.Coverage / 4;
                strength += edge.Strength / 4;
            }
            difference /= Math.Max(1, sampleCount);
            if (texture < sampleCount * 0.02 && strength < Math.Max(12, image.Noise * 3)
                && difference < image.ColourThreshold * 1.8)
            {
                return null;
            }
            // Invisible blank margins cannot be reconstructed from content alone. Long,
            // supported outer edges are required; variance is supporting evidence only.
            if (strength < Math.Max(6, image.Noise * 1.5))
            {
                return null;
            }
            double topLength = Distance(corners[0], corners[1]);
            double sideLength = Distance(corners[1], corners[2]);
            PointF edgeStart = corners[0];
            PointF edgeEnd = topLength >= sideLength ? corners[1] : corners[3];
            double angleDegrees = Normalise(Math.Atan2(edgeEnd.Y - edgeStart.Y, edgeEnd.X - edgeStart.X) * 180 / Math.PI);
            // Average the opposite long edges to reduce a single row's quantisation error.
            PointF oppositeStart = topLength >= sideLength ? corners[3] : corners[1];
            PointF oppositeEnd = corners[2];
            double oppositeAngle = Normalise(Math.Atan2(oppositeEnd.Y - oppositeStart.Y, oppositeEnd.X - oppositeStart.X) * 180 / Math.PI);
            angleDegrees = Normalise(angleDegrees + Normalise(oppositeAngle - angleDegrees) * 0.5);
            double angleRadians = angleDegrees * Math.PI / 180;
            Frame fitted = Project(corners, angleRadians);
            double fittedWidth = fitted.Right - fitted.Left;
            double fittedHeight = fitted.Bottom - fitted.Top;
            if (fittedWidth <= 0 || fittedHeight <= 0 || fittedWidth > page.Width || fittedHeight > page.Height)
            {
                return null;
            }
            Rectangle bounds = Bounds(corners, page);
            float score = (float)Math.Min(0.98, 0.45 + coverage * 0.32 + Math.Min(1, strength / 40) * 0.18);
            if (squareError > 3)
            {
                score = Math.Min(score, 0.49f);
            }
            RotatedBox box = new RotatedBox
            {
                Angle = (float)angleDegrees, RawAngle = (float)angleDegrees,
                Width = (float)fittedWidth, Height = (float)fittedHeight,
                Center = new PointF((corners[0].X + corners[1].X + corners[2].X + corners[3].X) / 4,
                    (corners[0].Y + corners[1].Y + corners[2].Y + corners[3].Y) / 4),
                Corners = corners, AABB = bounds, IsValid = true, Score = score
            };
            CropRegion result = new CropRegion
            {
                Box = box, SkewDegrees = (float)angleDegrees, Score = score, Confidence = Confidence(score),
                Reason = squareError > 3 ? "Four edges found, but corners depart from square by " + squareError.ToString("F1") + " degrees."
                    : "Four fitted corners and straight edges; boundary support " + (coverage * 100).ToString("F0") + "%.",
                NormRect = new RectangleF((float)bounds.Left / page.Width, (float)bounds.Top / page.Height,
                    (float)bounds.Width / page.Width, (float)bounds.Height / page.Height),
                WidthInches = fittedWidth / AxisDpi(page, Math.Cos(angleRadians), Math.Sin(angleRadians)),
                HeightInches = fittedHeight / AxisDpi(page, -Math.Sin(angleRadians), Math.Cos(angleRadians))
            };
            return Usable(result, page) ? result : null;
        }

        private static EdgeLine FitEdge(List<PointF> boundary, Frame frame, int edge, WorkImage image)
        {
            bool vertical = edge < 2;
            double target = edge == 0 ? frame.Left : edge == 1 ? frame.Right : edge == 2 ? frame.Top : frame.Bottom;
            double start = vertical ? frame.Top : frame.Left;
            double finish = vertical ? frame.Bottom : frame.Right;
            double band = Math.Max(3, Math.Min(frame.Right - frame.Left, frame.Bottom - frame.Top) * 0.065);
            double cosine = Math.Cos(frame.Angle), sine = Math.Sin(frame.Angle);
            List<PointF> points = new List<PointF>();
            foreach (PointF point in boundary)
            {
                double horizontal = point.X * cosine + point.Y * sine;
                double other = -point.X * sine + point.Y * cosine;
                double along = vertical ? other : horizontal;
                double across = vertical ? horizontal : other;
                if (Math.Abs(across - target) > band || along < start + (finish - start) * 0.05
                    || along > finish - (finish - start) * 0.05)
                {
                    continue;
                }
                points.Add(new PointF((float)along, (float)across));
            }
            if (points.Count < 8)
            {
                return null;
            }
            points.Sort(delegate(PointF first, PointF second)
            {
                return first.X.CompareTo(second.X);
            });
            double bestIntercept = target, bestSlope = 0, bestScore = -1;
            // Deterministic separated pairs make RANSAC repeatable and keep a hair from
            // controlling an entire edge. Twenty-four hypotheses cover both end thirds.
            for (int iteration = 0; iteration < 24; iteration++)
            {
                PointF first = points[(iteration * 37) % Math.Max(1, points.Count / 3)];
                PointF second = points[points.Count - 1 - (iteration * 53) % Math.Max(1, points.Count / 3)];
                double separation = second.X - first.X;
                if (separation < (finish - start) * 0.3)
                {
                    continue;
                }
                double slope = (second.Y - first.Y) / separation;
                if (Math.Abs(slope) > 0.18)
                {
                    continue;
                }
                double intercept = first.Y - slope * first.X;
                double support = 0;
                foreach (PointF point in points)
                {
                    double residual = Math.Abs(point.Y - intercept - slope * point.X);
                    support += residual < 1.3 ? 1 - residual / 2.6 : 0;
                }
                if (support > bestScore)
                {
                    bestScore = support;
                    bestIntercept = intercept;
                    bestSlope = slope;
                }
            }
            for (int iteration = 0; iteration < 3; iteration++)
            {
                double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0, count = 0;
                foreach (PointF point in points)
                {
                    if (Math.Abs(point.Y - bestIntercept - bestSlope * point.X) > 1.3)
                    {
                        continue;
                    }
                    sumX += point.X;
                    sumY += point.Y;
                    sumXX += (double)point.X * point.X;
                    sumXY += (double)point.X * point.Y;
                    count++;
                }
                double denominator = count * sumXX - sumX * sumX;
                if (count < 6 || denominator < 1e-8)
                {
                    return null;
                }
                bestSlope = (count * sumXY - sumX * sumY) / denominator;
                bestIntercept = (sumY - bestSlope * sumX) / count;
            }
            bool[] bins = new bool[20];
            foreach (PointF point in points)
            {
                if (Math.Abs(point.Y - bestIntercept - bestSlope * point.X) <= 1.3)
                {
                    int bin = Math.Max(0, Math.Min(19, (int)(20 * (point.X - start) / (finish - start))));
                    bins[bin] = true;
                }
            }
            int occupied = 0;
            foreach (bool bin in bins)
            {
                occupied += bin ? 1 : 0;
            }
            // Fitting the outer mask is conservatively biased by the two-pixel contrast
            // window. Seek the strongest real edge across the normal before intersection.
            List<double> shifts = new List<double>();
            List<double> actualStrength = new List<double>();
            for (int sample = 1; sample < 20; sample++)
            {
                double along = start + (finish - start) * sample / 20;
                double across = bestIntercept + bestSlope * along;
                double strongest = 0, bestShift = 0;
                for (int offset = -6; offset <= 6; offset++)
                {
                    double shift = offset * 0.5;
                    double horizontal = vertical ? across + shift : along;
                    double other = vertical ? along : across + shift;
                    double sourceX = horizontal * cosine - other * sine - 0.5;
                    double sourceY = horizontal * sine + other * cosine - 0.5;
                    double strength = GradientSample(image, sourceX, sourceY);
                    if (strength > strongest)
                    {
                        strongest = strength;
                        bestShift = shift;
                    }
                }
                shifts.Add(bestShift);
                actualStrength.Add(strongest);
            }
            bestIntercept += Median(shifts);
            return new EdgeLine
            {
                Intercept = bestIntercept, Slope = bestSlope,
                Coverage = occupied / 20.0, Strength = Median(actualStrength)
            };
        }

        private static double GradientSample(WorkImage image, double horizontal, double vertical)
        {
            int left = Math.Max(0, Math.Min(image.Width - 1, (int)Math.Floor(horizontal)));
            int top = Math.Max(0, Math.Min(image.Height - 1, (int)Math.Floor(vertical)));
            int right = Math.Min(image.Width - 1, left + 1);
            int bottom = Math.Min(image.Height - 1, top + 1);
            double fractionX = Math.Max(0, Math.Min(1, horizontal - left));
            double fractionY = Math.Max(0, Math.Min(1, vertical - top));
            return (image.Gradient[top * image.Width + left] * (1 - fractionX)
                + image.Gradient[top * image.Width + right] * fractionX) * (1 - fractionY)
                + (image.Gradient[bottom * image.Width + left] * (1 - fractionX)
                + image.Gradient[bottom * image.Width + right] * fractionX) * fractionY;
        }

        private static PointF Intersect(EdgeLine vertical, EdgeLine horizontal)
        {
            double denominator = 1 - vertical.Slope * horizontal.Slope;
            double across = (vertical.Intercept + vertical.Slope * horizontal.Intercept) / denominator;
            return new PointF((float)across, (float)(horizontal.Intercept + horizontal.Slope * across));
        }

        // ===== Policy and validation =====

        private static void RemoveContained(List<CropRegion> candidates)
        {
            for (int first = candidates.Count - 1; first >= 0; first--)
            {
                for (int second = 0; second < candidates.Count; second++)
                {
                    if (first == second || PolygonArea(candidates[second].Box.Corners) <= PolygonArea(candidates[first].Box.Corners))
                    {
                        continue;
                    }
                    bool contains = true;
                    foreach (PointF corner in candidates[first].Box.Corners)
                    {
                        contains &= Inside(corner, candidates[second].Box.Corners);
                    }
                    if (contains)
                    {
                        candidates.RemoveAt(first);
                        break;
                    }
                }
            }
        }

        private static void ApplyConsistency(List<CropRegion> candidates)
        {
            if (candidates.Count < 3)
            {
                return;
            }
            int[] matches = new int[candidates.Count];
            bool repeated = false;
            for (int first = 0; first < candidates.Count; first++)
            {
                for (int second = first + 1; second < candidates.Count; second++)
                {
                    double firstShort = Math.Min(candidates[first].WidthInches, candidates[first].HeightInches);
                    double secondShort = Math.Min(candidates[second].WidthInches, candidates[second].HeightInches);
                    double firstLong = Math.Max(candidates[first].WidthInches, candidates[first].HeightInches);
                    double secondLong = Math.Max(candidates[second].WidthInches, candidates[second].HeightInches);
                    if (Math.Abs(firstShort - secondShort) / Math.Max(firstShort, secondShort) < 0.08
                        && Math.Abs(firstLong - secondLong) / Math.Max(firstLong, secondLong) < 0.08)
                    {
                        matches[first]++;
                        matches[second]++;
                        repeated = true;
                    }
                }
            }
            for (int index = 0; index < candidates.Count; index++)
            {
                CropRegion region = candidates[index];
                // Agreement supports a real rectangle, but cannot repair non-square edges.
                if (matches[index] > 0 && region.Score >= 0.5f)
                {
                    region.Score = Math.Min(1, region.Score + 0.08f);
                    region.Reason += " Size agrees with other items.";
                }
                else if (repeated && matches[index] == 0)
                {
                    region.Score = Math.Max(0, region.Score - 0.18f);
                    region.Reason += " Size differs from the repeated items.";
                }
                region.Confidence = Confidence(region.Score);
                region.Box.Score = region.Score;
            }
        }

        private static void ReadingOrder(List<CropRegion> regions)
        {
            List<double> heights = new List<double>();
            foreach (CropRegion region in regions)
            {
                heights.Add(region.Box.AABB.Height);
            }
            double band = Median(heights) * 0.5;
            regions.Sort(delegate(CropRegion first, CropRegion second)
            {
                return first.Box.Center.Y.CompareTo(second.Box.Center.Y);
            });
            int start = 0;
            while (start < regions.Count)
            {
                int finish = start + 1;
                double anchor = regions[start].Box.Center.Y;
                // Anchoring each row prevents a chain of small offsets joining two rows.
                while (finish < regions.Count && regions[finish].Box.Center.Y - anchor <= band)
                {
                    finish++;
                }
                regions.Sort(start, finish - start, Comparer<CropRegion>.Create(delegate(CropRegion first, CropRegion second)
                {
                    return first.Box.Center.X.CompareTo(second.Box.Center.X);
                }));
                start = finish;
            }
            for (int index = 0; index < regions.Count; index++)
            {
                regions[index].Index = index + 1;
            }
        }

        private static AutoCropOptions Snapshot(AutoCropOptions options)
        {
            AutoCropOptions source = options ?? new AutoCropOptions();
            return new AutoCropOptions
            {
                Enabled = source.Enabled, MultiRegion = source.MultiRegion, Deskew = source.Deskew,
                MarginMm = Finite(source.MarginMm) ? Math.Max(0, source.MarginMm) : 0.5,
                MinAreaMm2 = Finite(source.MinAreaMm2) ? Math.Max(0, source.MinAreaMm2) : 400,
                MinConfidence = (CropConfidence)Math.Max(0, Math.Min(3, (int)source.MinConfidence)),
                MaxRegions = Math.Max(1, Math.Min(1024, source.MaxRegions)),
                // Extremely large requests defeat bounded working memory. Smaller requests
                // are honoured; a caller choosing a tiny image accepts reduced precision.
                WorkingLongEdge = Math.Max(1, Math.Min(4096, source.WorkingLongEdge))
            };
        }

        private static string Validate(RawImage page)
        {
            if (page == null || page.Pixels == null || page.Width <= 0 || page.Height <= 0 || page.Stride <= 0)
            {
                return "The scan has no usable pixel buffer.";
            }
            if ((page.Channels != 1 && page.Channels != 3)
                || (page.BitsPerChannel != 1 && page.BitsPerChannel != 8 && page.BitsPerChannel != 16)
                || (page.BitsPerChannel == 1 && page.Channels != 1))
            {
                return "The scan has an unsupported pixel layout.";
            }
            long minimumStride = page.BitsPerChannel == 1 ? ((long)page.Width + 7) / 8
                : (long)page.Width * page.Channels * (page.BitsPerChannel / 8);
            if (page.Stride < minimumStride || (long)page.Stride * page.Height > page.Pixels.LongLength)
            {
                return "The scan buffer does not contain every declared row.";
            }
            if (!Finite(page.XDpi) || !Finite(page.YDpi) || page.XDpi <= 0 || page.YDpi <= 0)
            {
                return "The scan resolution is missing; physical crop dimensions cannot be established.";
            }
            return null;
        }

        private static bool Usable(CropRegion region, RawImage page)
        {
            if (region == null || region.Box == null || !region.Box.IsValid || region.Box.Corners == null
                || region.Box.Corners.Length != 4 || !Finite(region.SkewDegrees) || Math.Abs(region.SkewDegrees) > 45
                || !Finite(region.Box.Width) || !Finite(region.Box.Height) || region.Box.Width <= 0
                || region.Box.Height <= 0 || region.Box.Width > page.Width || region.Box.Height > page.Height
                || !Finite(region.Box.Center.X) || !Finite(region.Box.Center.Y))
            {
                return false;
            }
            PointF[] corners = region.Box.Corners;
            double sign = 0;
            for (int index = 0; index < 4; index++)
            {
                PointF corner = corners[index];
                if (!Finite(corner.X) || !Finite(corner.Y) || corner.X < 0 || corner.X > page.Width
                    || corner.Y < 0 || corner.Y > page.Height)
                {
                    return false;
                }
                double cross = Cross(corner, corners[(index + 1) % 4], corners[(index + 2) % 4]);
                if (!Finite(cross) || Math.Abs(cross) < 1e-8 || index > 0 && cross * sign <= 0)
                {
                    return false;
                }
                sign = cross;
            }
            return PolygonArea(corners) >= 1;
        }

        private static CropConfidence Confidence(float score)
        {
            return score >= 0.85f ? CropConfidence.High : score >= 0.65f ? CropConfidence.Good
                : score > 0 ? CropConfidence.Low : CropConfidence.None;
        }

        private static double Normalise(double angle)
        {
            // Rectangle axes are equivalent modulo 90 degrees, including portrait pages.
            while (angle > 45)
            {
                angle -= 90;
            }
            while (angle < -45)
            {
                angle += 90;
            }
            return angle;
        }

        private static Frame Project(PointF[] points, double angle)
        {
            Frame frame = new Frame
            {
                Angle = angle, Left = double.MaxValue, Top = double.MaxValue,
                Right = double.MinValue, Bottom = double.MinValue
            };
            foreach (PointF point in points)
            {
                double horizontal = point.X * Math.Cos(angle) + point.Y * Math.Sin(angle);
                double vertical = -point.X * Math.Sin(angle) + point.Y * Math.Cos(angle);
                frame.Left = Math.Min(frame.Left, horizontal);
                frame.Right = Math.Max(frame.Right, horizontal);
                frame.Top = Math.Min(frame.Top, vertical);
                frame.Bottom = Math.Max(frame.Bottom, vertical);
            }
            return frame;
        }

        private static Rectangle Bounds(PointF[] corners, RawImage page)
        {
            Frame frame = Project(corners, 0);
            int left = Math.Max(0, (int)Math.Floor(frame.Left));
            int top = Math.Max(0, (int)Math.Floor(frame.Top));
            int right = Math.Min(page.Width, (int)Math.Ceiling(frame.Right));
            int bottom = Math.Min(page.Height, (int)Math.Ceiling(frame.Bottom));
            return Rectangle.FromLTRB(left, top, right, bottom);
        }

        private static bool Inside(PointF point, PointF[] corners)
        {
            for (int index = 0; index < 4; index++)
            {
                if (Cross(corners[index], corners[(index + 1) % 4], point) < -0.01)
                {
                    return false;
                }
            }
            return true;
        }

        private static double PolygonArea(PointF[] corners)
        {
            double area = 0;
            for (int index = 0; index < corners.Length; index++)
            {
                PointF first = corners[index];
                PointF second = corners[(index + 1) % corners.Length];
                area += (double)first.X * second.Y - (double)first.Y * second.X;
            }
            return Math.Abs(area) * 0.5;
        }

        private static double Distance(PointF first, PointF second)
        {
            double horizontal = first.X - second.X;
            double vertical = first.Y - second.Y;
            return Math.Sqrt(horizontal * horizontal + vertical * vertical);
        }

        private static double AxisDpi(RawImage page, double horizontal, double vertical)
        {
            return 1 / Math.Sqrt(horizontal * horizontal / (page.XDpi * page.XDpi)
                + vertical * vertical / (page.YDpi * page.YDpi));
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static double Median(List<double> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }
            double[] sorted = values.ToArray();
            Array.Sort(sorted);
            return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) * 0.5;
        }

        private static double[] NativeBackground(RawImage page)
        {
            WorkImage small = Downscale(page, 512);
            EstimateBackground(small);
            List<double>[] samples = new List<double>[page.Channels];
            for (int channel = 0; channel < samples.Length; channel++)
            {
                samples[channel] = new List<double>();
            }
            int left = small.NativeLeft;
            int top = small.NativeTop;
            int right = small.NativeRight - 1;
            int bottom = small.NativeBottom - 1;
            // Native-depth medians preserve the low bytes of a 48-bit lid colour too.
            for (int sample = 0; sample < 512; sample++)
            {
                int horizontal = left + (int)((long)(right - left) * sample / 511);
                int vertical = top + (int)((long)(bottom - top) * sample / 511);
                for (int channel = 0; channel < page.Channels; channel++)
                {
                    samples[channel].Add(Sample(page, horizontal, top, channel, 0));
                    samples[channel].Add(Sample(page, horizontal, bottom, channel, 0));
                    samples[channel].Add(Sample(page, left, vertical, channel, 0));
                    samples[channel].Add(Sample(page, right, vertical, channel, 0));
                }
            }
            double[] result = new double[page.Channels];
            for (int channel = 0; channel < result.Length; channel++)
            {
                result[channel] = Median(samples[channel]);
            }
            return result;
        }

        private static double Sample(RawImage page, int horizontal, int vertical, int channel, double background)
        {
            if (horizontal < 0 || horizontal >= page.Width || vertical < 0 || vertical >= page.Height)
            {
                return background;
            }
            int offset = vertical * page.Stride;
            if (page.BitsPerChannel == 1)
            {
                return (page.Pixels[offset + (horizontal >> 3)] & (0x80 >> (horizontal & 7))) != 0 ? 255 : 0;
            }
            offset += (horizontal * page.Channels + channel) * (page.BitsPerChannel / 8);
            return page.BitsPerChannel == 8 ? page.Pixels[offset]
                : page.Pixels[offset] | (page.Pixels[offset + 1] << 8);
        }

        private static void Write(RawImage image, int horizontal, int vertical, int channel, int value)
        {
            int offset = vertical * image.Stride;
            if (image.BitsPerChannel == 1)
            {
                if (value >= 128)
                {
                    image.Pixels[offset + (horizontal >> 3)] |= (byte)(0x80 >> (horizontal & 7));
                }
                return;
            }
            offset += (horizontal * image.Channels + channel) * (image.BitsPerChannel / 8);
            image.Pixels[offset] = (byte)(value & 255);
            if (image.BitsPerChannel == 16)
            {
                image.Pixels[offset + 1] = (byte)(value >> 8);
            }
        }

        private static void SafeLog(string message)
        {
            try
            {
                Action<string> logger = Log;
                if (logger != null)
                {
                    logger(message);
                }
            }
            catch (Exception)
            {
                // A user-supplied logging callback must not turn a recoverable refusal
                // into a failed scan. Resource exhaustion remains a CLR-level limit.
            }
        }
    }

    // ===== In-memory regression cases =====

    public static class AutoCropSelfTest
    {
        /// <summary>Runs every case. Returns the number of failures; zero means all passed.</summary>
        public static int Run(Action<string> report)
        {
            int failures = 0;
            try
            {
                Case(report, ref failures, "single_straight", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 8, 1);
                    Item truth = new Item(425, 550, 500, 700, 0);
                    Paint(page, truth, 245, false, false);
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "single_rotated_7deg", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 8, 1);
                    Item truth = new Item(425, 550, 500, 700, 7);
                    Paint(page, truth, 245, false, false);
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "white_on_white", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 245, 8, 1);
                    Item truth = new Item(425, 550, 500, 700, -2);
                    Paint(page, truth, 245, true, false);
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "four_id_cards", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 185, 8, 3);
                    Item[] truth = new Item[]
                    {
                        new Item(220, 300, 337, 213, 0), new Item(625, 295, 337, 213, 0),
                        new Item(220, 700, 337, 213, 0), new Item(625, 705, 337, 213, 0)
                    };
                    foreach (Item item in truth)
                    {
                        Paint(page, item, 242, false, false);
                    }
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    Require(result.Regions.Count == 4, "regions " + result.Regions.Count + ", expected 4; " + result.Notes);
                    double worst = 0;
                    for (int index = 0; index < 4; index++)
                    {
                        CropRegion region = result.Regions[index];
                        double error = Math.Max(Math.Abs(region.Box.Width - truth[index].Width) / truth[index].Width,
                            Math.Abs(region.Box.Height - truth[index].Height) / truth[index].Height);
                        worst = Math.Max(worst, error);
                        Require(error < 0.02 && Separation(region.Box.Center, new PointF((float)truth[index].X, (float)truth[index].Y)) < 5
                            && region.Index == index + 1, "item " + (index + 1) + " size error " + (error * 100).ToString("F2")
                            + "%, centre " + region.Box.Center + "; expected reading-order centre within 5 px and size within 2%");
                    }
                    return "4 in reading order, maximum size error " + (worst * 100).ToString("F2") + "%";
                });
                Case(report, ref failures, "three_photos_rotated", delegate
                {
                    RawImage page = Canvas(1050, 1400, 100, 190, 8, 3);
                    Item[] truth = new Item[]
                    {
                        new Item(270, 330, 350, 470, -9),
                        new Item(780, 335, 350, 470, 5), new Item(500, 1010, 350, 470, 13)
                    };
                    foreach (Item item in truth)
                    {
                        Paint(page, item, 235, false, false);
                    }
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    Require(result.Regions.Count == 3, "regions " + result.Regions.Count + ", expected 3; " + result.Notes);
                    string measured = "";
                    for (int index = 0; index < 3; index++)
                    {
                        double error = Math.Abs(result.Regions[index].SkewDegrees - truth[index].Angle);
                        Require(error < 0.5, "item " + index + " angle " + result.Regions[index].SkewDegrees.ToString("F2")
                            + ", expected " + truth[index].Angle + " +/- 0.5 degrees");
                        measured += result.Regions[index].SkewDegrees.ToString("F2") + " degrees; ";
                    }
                    return measured;
                });
                Case(report, ref failures, "dust_is_ignored", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 8, 1);
                    Item truth = new Item(425, 550, 500, 700, 2);
                    Paint(page, truth, 245, false, false);
                    for (int index = 0; index < 90; index++)
                    {
                        int horizontal = 10 + index * 79 % 825;
                        int vertical = 10 + index * 113 % 1075;
                        for (int speck = 0; speck < 3; speck++)
                        {
                            Set(page, horizontal + speck, vertical, 25);
                        }
                    }
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "shadow_is_ignored", delegate
                {
                    RawImage page = Canvas(1000, 1100, 100, 220, 8, 1);
                    Item truth = new Item(360, 550, 500, 700, 0);
                    Paint(page, truth, 250, false, false);
                    for (int row = 260; row < 840; row++)
                    {
                        for (int column = 650; column < 950; column++)
                        {
                            double radius = Math.Pow((column - 800) / 90.0, 2) + Math.Pow((row - 550) / 160.0, 2);
                            Set(page, column, row, 220 - (int)(29 * Math.Exp(-radius)));
                        }
                    }
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "touching_items_merge_not", delegate
                {
                    RawImage page = Canvas(900, 800, 100, 190, 8, 1);
                    double gap = 4 * 100 / 25.4;
                    Paint(page, new Item(250, 400, 300, 420, 0), 245, false, false);
                    Paint(page, new Item(550 + gap, 400, 300, 420, 0), 245, false, false);
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    Require(result.Regions.Count == 2, "regions " + result.Regions.Count + ", expected 2 at a 4 mm gap");
                    return "2 regions, gap " + gap.ToString("F2") + " px (4 mm)";
                });
                Case(report, ref failures, "fragment_merges", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 245, 8, 1);
                    Item truth = new Item(425, 550, 500, 700, 0);
                    Paint(page, truth, 150, false, true);
                    return CheckOne(page, truth, 0.01, 0.5);
                });
                Case(report, ref failures, "empty_glass", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 245, 8, 1);
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    List<RawImage> fallback = AutoCropEngine.DetectAndExtract(page, null);
                    Require(!result.Detected && result.Regions.Count == 0 && !string.IsNullOrEmpty(result.Notes)
                        && fallback.Count == 1 && object.ReferenceEquals(page, fallback[0]),
                        "detected " + result.Detected + ", regions " + result.Regions.Count + ", notes '" + result.Notes
                        + "'; expected refusal with notes and identical original fallback");
                    return "no regions; original buffer returned";
                });
                Case(report, ref failures, "full_bleed_page", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 245, 8, 1);
                    // Sparse printing ensures an all-white special case cannot pass alone.
                    for (int row = 250; row < 800; row += 30)
                    {
                        for (int column = 200; column < 650; column++)
                        {
                            Set(page, column, row, 80);
                        }
                    }
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    bool whole = result.Regions.Count == 1 && result.Regions[0].Box.AABB.Width >= page.Width * 0.99
                        && result.Regions[0].Box.AABB.Height >= page.Height * 0.99;
                    Require(!result.Detected || whole, "regions " + result.Regions.Count + "; expected refusal or the entire glass");
                    return result.Detected ? "whole glass retained" : "border unavailable; crop refused";
                });
                Case(report, ref failures, "extract_is_straight", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 8, 1);
                    Paint(page, new Item(425, 550, 500, 700, 7), 245, false, false);
                    AutoCropResult found = AutoCropEngine.Detect(page, null);
                    Require(found.Regions.Count == 1, "regions " + found.Regions.Count + ", expected one before extraction");
                    AutoCropOptions settings = new AutoCropOptions
                    {
                        MarginMm = 6
                    };
                    RawImage extracted = AutoCropEngine.Extract(page, found.Regions[0], settings);
                    Require(extracted != null, "extraction null, expected pixels");
                    AutoCropResult checkedCrop = AutoCropEngine.Detect(extracted, null);
                    Require(checkedCrop.Regions.Count == 1, "redetected " + checkedCrop.Regions.Count + ", expected one with a visible margin");
                    double angle = checkedCrop.Regions[0].SkewDegrees;
                    Require(Math.Abs(angle) < 0.3, "extracted skew " + angle.ToString("F3") + ", expected less than 0.3 degrees");
                    return "extracted skew " + angle.ToString("F3") + " degrees";
                });
                Case(report, ref failures, "extract_preserves_depth", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 16, 3);
                    page.YDpi = 101;
                    page.Side = 1;
                    Paint(page, new Item(425, 550, 500, 700, 7), 245, false, false);
                    AutoCropResult found = AutoCropEngine.Detect(page, null);
                    Require(found.Regions.Count == 1, "regions " + found.Regions.Count + ", expected 1");
                    RawImage output = AutoCropEngine.Extract(page, found.Regions[0], null);
                    Require(output != null, "extraction null, expected 48-bit pixels");
                    int centre = output.Height / 2 * output.Stride + output.Width / 2 * 6;
                    int value = output.Pixels[centre] | output.Pixels[centre + 1] << 8;
                    Require(output.Channels == 3 && output.BitsPerChannel == 16 && output.XDpi == 100 && output.YDpi == 101
                        && output.Side == 1 && output.PageIndex == 1 && value == 245 * 257,
                        "channels/depth " + output.Channels + "/" + output.BitsPerChannel + ", DPI " + output.XDpi + "/"
                        + output.YDpi + ", centre " + value + "; expected 3/16, 100/101, centre 62965 and preserved metadata");
                    return "48-bit, DPI 100/101, centre value " + value + " (low byte retained)";
                });
                Case(report, ref failures, "no_black_corners", delegate
                {
                    RawImage page = Canvas(400, 500, 100, 205, 8, 3);
                    // A valid box near the upper edge plus a large margin deliberately
                    // samples beyond the glass; an ordinary centred crop would miss this bug.
                    Item truth = new Item(155, 150, 230, 240, 13);
                    PointF[] corners = TruthCorners(truth);
                    CropRegion region = new CropRegion
                    {
                        Index = 1, SkewDegrees = 13,
                        Box = new RotatedBox
                        {
                            IsValid = true, Width = 230, Height = 240,
                            Center = new PointF(155, 150), Corners = corners
                        }
                    };
                    RawImage output = AutoCropEngine.Extract(page, region, new AutoCropOptions
                    {
                        MarginMm = 20
                    });
                    Require(output != null, "extraction null, expected background-filled pixels");
                    int[] positions = new int[]
                    {
                        0, (output.Width - 1) * 3,
                        (output.Height - 1) * output.Stride, (output.Height - 1) * output.Stride + (output.Width - 1) * 3
                    };
                    int minimum = 255, maximum = 0;
                    foreach (int position in positions)
                    {
                        for (int channel = 0; channel < 3; channel++)
                        {
                            minimum = Math.Min(minimum, output.Pixels[position + channel]);
                            maximum = Math.Max(maximum, output.Pixels[position + channel]);
                        }
                    }
                    Require(minimum == 205 && maximum == 205, "corner range " + minimum + ".." + maximum + ", expected background 205");
                    return "all corner channels 205, including samples outside the glass";
                });
                Case(report, ref failures, "performance", delegate
                {
                    RawImage page = Canvas(2480, 3508, 300, 190, 8, 3);
                    Paint(page, new Item(1240, 1754, 1500, 2100, 2), 245, false, false);
                    // Earlier cases warm the JIT; the entire Detect call, including scale
                    // conversion and allocations, is timed. No relaxed timing fallback.
                    Stopwatch elapsed = Stopwatch.StartNew();
                    AutoCropResult result = AutoCropEngine.Detect(page, null);
                    elapsed.Stop();
                    Require(result.Regions.Count == 1 && elapsed.Elapsed.TotalMilliseconds < 400,
                        "detection " + elapsed.Elapsed.TotalMilliseconds.ToString("F1") + " ms, regions " + result.Regions.Count
                        + "; expected under 400 ms and one region at 2480 x 3508");
                    return elapsed.Elapsed.TotalMilliseconds.ToString("F1") + " ms, 2480 x 3508 colour";
                });
                Case(report, ref failures, "packed_and_padded_pixels", delegate
                {
                    int tested = 0;
                    foreach (int depth in new int[]
                    {
                        1, 8, 16
                    })
                    {
                        foreach (int channels in new int[]
                        {
                            1, 3
                        })
                        {
                            if (depth == 1 && channels == 3)
                            {
                                continue;
                            }
                            RawImage page = Canvas(401, 503, 100, 255, depth, channels);
                            Set(page, 31, 43, 0);
                            CropRegion region = new CropRegion
                            {
                                Index = 3, SkewDegrees = 0,
                                Box = new RotatedBox
                                {
                                    IsValid = true, Width = 120, Height = 140,
                                    Center = new PointF(80, 100), Corners = TruthCorners(new Item(80, 100, 120, 140, 0))
                                }
                            };
                            byte[] original = (byte[])page.Pixels.Clone();
                            RawImage output = AutoCropEngine.Extract(page, region, new AutoCropOptions
                            {
                                MarginMm = 0
                            });
                            Require(output != null && output.Width == 120 && output.Height == 140,
                                "layout " + channels + "/" + depth + ", expected 120 x 140 crop");
                            int black = Get(output, 11, 13);
                            int white = Get(output, 12, 13);
                            Require(black == 0 && white == 255 && output.PageIndex == 3,
                                "layout " + channels + "/" + depth + " values " + black + "/" + white + ", expected 0/255, index 3");
                            for (int index = 0; index < original.Length; index++)
                            {
                                Require(original[index] == page.Pixels[index], "source changed at byte " + index + ", expected unchanged");
                            }
                            tested++;
                        }
                    }
                    return tested + " padded layouts, exact pixel positions, source unchanged";
                });
                Case(report, ref failures, "measured_calibration_strip", delegate
                {
                    RawImage page = Canvas(850, 1100, 100, 190, 8, 1);
                    Paint(page, new Item(425, 357, 500, 700, 0), 245, false, false);
                    for (int row = 0; row < 7; row++)
                    {
                        for (int column = 0; column < page.Width; column++)
                        {
                            Set(page, column, row, 15);
                        }
                    }
                    AutoCropResult result = AutoCropEngine.Detect(page, new AutoCropOptions
                    {
                        WorkingLongEdge = 400
                    });
                    Require(result.Regions.Count == 1, "regions " + result.Regions.Count + ", expected 1 adjacent to a seven-row strip; " + result.Notes);
                    double top = Math.Min(result.Regions[0].Box.Corners[0].Y, result.Regions[0].Box.Corners[1].Y);
                    Require(top >= 7 && top <= 8, "top " + top.ToString("F2") + ", expected source row 7 +/- 1 without retaining the strip");
                    return "top source row " + top.ToString("F2") + ", seven-row strip, 400 px working edge";
                });
                Case(report, ref failures, "invalid_input_and_disabled", delegate
                {
                    RawImage page = Canvas(40, 50, 100, 200, 8, 1);
                    AutoCropOptions disabled = new AutoCropOptions
                    {
                        Enabled = false
                    };
                    AutoCropResult result = AutoCropEngine.Detect(page, disabled);
                    List<RawImage> untouched = AutoCropEngine.DetectAndExtract(page, disabled);
                    Require(!result.Detected && untouched.Count == 1 && object.ReferenceEquals(page, untouched[0]),
                        "disabled detection " + result.Detected + ", expected original reference");
                    page.Stride = 1;
                    result = AutoCropEngine.Detect(page, null);
                    Require(!result.Detected && result.Notes.Length > 0 && AutoCropEngine.Extract(page, null, null) == null,
                        "invalid buffer detected " + result.Detected + ", expected refusal and null extraction");
                    result = AutoCropEngine.Detect(null, null);
                    Require(!result.Detected && result.Notes.Length > 0, "null input detected " + result.Detected + ", expected refusal with notes");
                    return "disabled scan unchanged; invalid stride and null scan refused";
                });
            }
            catch (Exception exception)
            {
                failures++;
                Report(report, "FAIL self_test_runner: " + exception.Message);
            }
            return failures;
        }

        private sealed class Item
        {
            internal double X, Y, Width, Height, Angle;
            internal Item(double horizontal, double vertical, double width, double height, double angle)
            {
                X = horizontal; Y = vertical; Width = width; Height = height; Angle = angle;
            }
        }

        private static RawImage Canvas(int width, int height, double dpi, int background, int depth, int channels)
        {
            // Seven padding bytes expose implementations that assume tightly packed rows.
            int stride = (depth == 1 ? (width + 7) / 8 : width * channels * (depth / 8)) + 7;
            RawImage page = new RawImage
            {
                Width = width, Height = height, Stride = stride,
                Pixels = new byte[stride * height], Channels = channels, BitsPerChannel = depth,
                XDpi = dpi, YDpi = dpi, PageIndex = 9
            };
            for (int row = 0; row < height; row++)
            {
                for (int column = 0; column < width; column++)
                {
                    Set(page, column, row, background);
                }
            }
            return page;
        }

        private static void Paint(RawImage page, Item item, int colour, bool shadow, bool band)
        {
            double cosine = Math.Cos(item.Angle * Math.PI / 180);
            double sine = Math.Sin(item.Angle * Math.PI / 180);
            for (int row = 0; row < page.Height; row++)
            {
                for (int column = 0; column < page.Width; column++)
                {
                    double horizontal = (column + 0.5 - item.X) * cosine + (row + 0.5 - item.Y) * sine;
                    double vertical = -(column + 0.5 - item.X) * sine + (row + 0.5 - item.Y) * cosine;
                    double outside = Math.Max(Math.Abs(horizontal) - item.Width / 2, Math.Abs(vertical) - item.Height / 2);
                    if (outside <= 0)
                    {
                        // A 2 mm lid-coloured band creates genuinely disconnected halves.
                        Set(page, column, row, band && Math.Abs(vertical) < page.YDpi / 25.4 ? 245 : colour);
                    }
                    else if (shadow && outside < 3)
                    {
                        Set(page, column, row, 245 - (int)(28 * (1 - outside / 3)));
                    }
                }
            }
        }

        private static void Set(RawImage page, int column, int row, int value)
        {
            int position = row * page.Stride;
            if (page.BitsPerChannel == 1)
            {
                int bit = 0x80 >> (column & 7);
                position += column >> 3;
                page.Pixels[position] = (byte)(value >= 128 ? page.Pixels[position] | bit : page.Pixels[position] & ~bit);
                return;
            }
            int bytes = page.BitsPerChannel / 8;
            position += column * page.Channels * bytes;
            for (int channel = 0; channel < page.Channels; channel++)
            {
                page.Pixels[position + channel * bytes] = (byte)value;
                if (bytes == 2)
                {
                    page.Pixels[position + channel * bytes + 1] = (byte)value;
                }
            }
        }

        private static int Get(RawImage page, int column, int row)
        {
            if (page.BitsPerChannel == 1)
            {
                return (page.Pixels[row * page.Stride + (column >> 3)] & (0x80 >> (column & 7))) == 0 ? 0 : 255;
            }
            int bytes = page.BitsPerChannel / 8;
            return page.Pixels[row * page.Stride + column * page.Channels * bytes + bytes - 1];
        }

        private static PointF[] TruthCorners(Item item)
        {
            PointF[] corners = new PointF[4];
            double cosine = Math.Cos(item.Angle * Math.PI / 180);
            double sine = Math.Sin(item.Angle * Math.PI / 180);
            for (int index = 0; index < 4; index++)
            {
                double horizontal = (index == 0 || index == 3 ? -1 : 1) * item.Width / 2;
                double vertical = (index < 2 ? -1 : 1) * item.Height / 2;
                corners[index] = new PointF((float)(item.X + horizontal * cosine - vertical * sine),
                    (float)(item.Y + horizontal * sine + vertical * cosine));
            }
            return corners;
        }

        private static string CheckOne(RawImage page, Item truth, double tolerance, double angleTolerance)
        {
            AutoCropResult result = AutoCropEngine.Detect(page, null);
            Require(result.Detected && result.Regions.Count == 1,
                "regions " + result.Regions.Count + ", expected 1; " + result.Notes);
            CropRegion region = result.Regions[0];
            PointF[] corners = TruthCorners(truth);
            double worst = 0;
            foreach (PointF corner in corners)
            {
                double nearest = double.MaxValue;
                foreach (PointF detected in region.Box.Corners)
                {
                    nearest = Math.Min(nearest, Separation(corner, detected));
                }
                worst = Math.Max(worst, nearest / Math.Min(truth.Width, truth.Height));
            }
            double angleError = Math.Abs(region.SkewDegrees - truth.Angle);
            string measured = "angle " + region.SkewDegrees.ToString("F3") + " degrees, maximum corner error " + (worst * 100).ToString("F3") + "%";
            Require(worst <= tolerance && angleError <= angleTolerance, measured + "; expected corners within "
                + (tolerance * 100).ToString("F1") + "% and angle " + truth.Angle + " +/- " + angleTolerance);
            return measured;
        }

        private static double Separation(PointF first, PointF second)
        {
            return Math.Sqrt(Math.Pow(first.X - second.X, 2) + Math.Pow(first.Y - second.Y, 2));
        }

        private static void Require(bool condition, string measured)
        {
            if (!condition)
            {
                throw new InvalidOperationException(measured);
            }
        }

        private static void Case(Action<string> report, ref int failures, string name, Func<string> test)
        {
            try
            {
                string measured = test();
                Report(report, "ok " + name + ": " + measured);
            }
            catch (Exception exception)
            {
                failures++;
                Report(report, "FAIL " + name + ": " + exception.Message);
            }
        }

        private static void Report(Action<string> report, string message)
        {
            try
            {
                if (report != null)
                {
                    report(message);
                }
            }
            catch (Exception)
            {
                // A failing test reporter must not skip subsequent regression cases.
                try
                {
                    Action<string> logger = AutoCropEngine.Log;
                    if (logger != null)
                    {
                        logger("The self-test reporting callback failed.");
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
