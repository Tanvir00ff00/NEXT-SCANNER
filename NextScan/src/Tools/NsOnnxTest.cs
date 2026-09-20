using System;
using System.Globalization;
using System.IO;
using NextScan.Core;

namespace NextScan.Tools
{
    /// <summary>
    /// Proves the hand-bound ONNX Runtime interop against a known answer.
    ///
    /// The table of function pointers this binds to is ordered, not named, so
    /// an index that is wrong by one produces a plausible-looking crash or,
    /// worse, plausible-looking numbers. The only honest check is to run a
    /// tensor whose answer was computed elsewhere and compare: this takes the
    /// exact input array the Python runtime was given, so nothing but the
    /// interop can differ.
    /// </summary>
    public static class NsOnnxTest
    {
        public static int Main(string[] args)
        {
            Console.WriteLine("NextScan ONNX interop test");
            Console.WriteLine();
            Console.WriteLine("  runtime: " + Ort.Availability);
            if (!Ort.Available) { Console.WriteLine("  FAILED: no runtime"); return 1; }

            string root = AppDomain.CurrentDomain.BaseDirectory;
            string model = Find(args, 0, Path.Combine(root, @"..\models\mobile_sam_image_encoder.onnx"));
            string input = Find(args, 1, Path.Combine(root, @"..\models\input_745x1024x3.f32"));

            if (!File.Exists(model)) { Console.WriteLine("  SKIP: model not at " + model); return 0; }
            if (!File.Exists(input)) { Console.WriteLine("  SKIP: reference input not at " + input); return 0; }

            using (Ort.Session session = new Ort.Session(model, Environment.ProcessorCount))
            {
                if (!session.Loaded) { Console.WriteLine("  FAILED to load: " + session.Error); return 1; }
                Console.WriteLine("  inputs:  " + string.Join(", ", session.Inputs));
                Console.WriteLine("  outputs: " + string.Join(", ", session.Outputs));

                float[] image = ReadFloats(input);
                Console.WriteLine("  input:   " + image.Length + " floats, sum "
                                  + Sum(image).ToString("0.000", CultureInfo.InvariantCulture));

                long[][] shapes;
                DateTime started = DateTime.UtcNow;
                float[][] outputs = session.Run(new float[][] { image },
                                                new long[][] { new long[] { 1024, 745, 3 } }, out shapes);
                double seconds = (DateTime.UtcNow - started).TotalSeconds;

                if (outputs == null) { Console.WriteLine("  FAILED to run: " + session.Error); return 1; }

                float[] embedding = outputs[0];
                Console.WriteLine("  output:  [" + string.Join(",", Array.ConvertAll(shapes[0], d => d.ToString()))
                                  + "] in " + seconds.ToString("0.00", CultureInfo.InvariantCulture) + " s");
                Console.WriteLine("  sum:     " + Sum(embedding).ToString("0.0000", CultureInfo.InvariantCulture));

                string first = "";
                for (int i = 0; i < 6 && i < embedding.Length; i++)
                    first += embedding[i].ToString("0.00000", CultureInfo.InvariantCulture) + " ";
                Console.WriteLine("  first 6: " + first.Trim());

                // Computed by the Python runtime on this same array.
                const double Expected = -1575.6245;
                double actual = Sum(embedding);
                double drift = Math.Abs(actual - Expected);
                Console.WriteLine();
                Console.WriteLine("  expected sum " + Expected.ToString("0.0000", CultureInfo.InvariantCulture)
                                  + ", drift " + drift.ToString("0.0000", CultureInfo.InvariantCulture));

                if (embedding.Length != 1 * 256 * 64 * 64)
                { Console.WriteLine("  FAILED: wrong element count"); return 1; }
                if (drift > 1.0)
                { Console.WriteLine("  FAILED: the interop does not agree with the reference"); return 1; }

                Console.WriteLine("  ok");
            }

            Console.WriteLine();
            if (Environment.GetEnvironmentVariable("NS_SHEET") == "1") { Sheet(root); return 0; }
            Segment(root);
            return 0;
        }

        /// <summary>
        /// Runs the whole proposal stage over every private reference bed and
        /// prints what came back, in millimetres. Sizes rather than pixels
        /// because the question is always the same one: is that an ID card, and
        /// is it whole.
        /// </summary>
        static void Segment(string root)
        {
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "..", "tests", "real"));
            if (!Directory.Exists(folder)) { Console.WriteLine("  no reference beds at " + folder); return; }

            string[] beds = Directory.GetFiles(folder, "lide400_*.png");
            Array.Sort(beds);
            foreach (string bed in beds)
            {
                if (bed.IndexOf("halfcard", StringComparison.OrdinalIgnoreCase) < 0 &&
                    bed.IndexOf("tilted", StringComparison.OrdinalIgnoreCase) < 0 &&
                    bed.IndexOf("passport_split", StringComparison.OrdinalIgnoreCase) < 0 &&
                    bed.IndexOf("passport_open", StringComparison.OrdinalIgnoreCase) < 0) continue;

                using (System.Drawing.Bitmap bitmap = new System.Drawing.Bitmap(bed))
                {
                    bitmap.SetResolution(300, 300);
                    RawImage page = RawImage.FromBitmap(bitmap);

                    PlatenDetectionReport report = new PlatenDetectionReport();
                    System.Collections.Generic.List<CropRegion> found =
                        PlatenDetector.Detect(page, new AutoCropOptions(), report);

                    Console.WriteLine("  " + Path.GetFileName(bed));
                    Console.WriteLine("     without the model: " + found.Count + " regions");
                    foreach (CropRegion item in found) Console.WriteLine("        " + Size(item));

                    AutoCropOptions withModel = new AutoCropOptions();
                    withModel.UseModel = true;
                    PlatenDetectionReport modelReport = new PlatenDetectionReport();
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                    System.Collections.Generic.List<CropRegion> repaired =
                        PlatenDetector.Detect(page, withModel, modelReport);
                    clock.Stop();

                    Console.WriteLine("     with the model:    " + repaired.Count + " regions in "
                                      + clock.ElapsedMilliseconds + " ms total");
                    foreach (CropRegion item in repaired) Console.WriteLine("        " + Size(item));
                    foreach (string line in modelReport.Lines)
                        if (line.StartsWith("model") || line.StartsWith("joined"))
                            Console.WriteLine("        . " + line);
                }
            }
        }

        /// <summary>Composes a few reference pages into a contact sheet, to look at.</summary>
        static void Sheet(string root)
        {
            string folder = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "..", "tests", "real"));
            string[] beds = Directory.GetFiles(folder, "lide400_*.png");
            Array.Sort(beds);

            System.Collections.Generic.List<RawImage> pages = new System.Collections.Generic.List<RawImage>();
            foreach (string bed in beds)
            {
                if (pages.Count >= 5) break;
                using (System.Drawing.Bitmap b = new System.Drawing.Bitmap(bed)) pages.Add(RawImage.FromBitmap(b));
            }
            Console.WriteLine("  composing " + pages.Count + " pages");

            RawImage sheet = ContactSheet.Compose(pages, 1200);
            if (sheet == null) { Console.WriteLine("  FAILED: nothing composed"); return; }
            Console.WriteLine("  sheet " + sheet.Width + "x" + sheet.Height + ", " + sheet.Channels
                              + " channels, stride " + sheet.Stride);

            string outPath = System.IO.Path.Combine(
                Environment.GetEnvironmentVariable("TEMP"), "contact_sheet.png");
            using (System.Drawing.Bitmap bmp = sheet.ToBitmap())
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("  wrote " + outPath);
        }

        static string Size(CropRegion item)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0,6:0.0} x {1,6:0.0} mm  {2,6:0.0} deg  {3}",
                item.WidthInches * 25.4, item.HeightInches * 25.4, item.SkewDegrees, item.Confidence);
        }

        static string Find(string[] args, int index, string fallback)
        {
            return args != null && args.Length > index ? args[index] : Path.GetFullPath(fallback);
        }

        static double Sum(float[] values)
        {
            double total = 0;
            foreach (float value in values) total += value;
            return total;
        }

        static float[] ReadFloats(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            float[] values = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, values, 0, values.Length * 4);
            return values;
        }
    }
}
