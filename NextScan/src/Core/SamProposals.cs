using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;

namespace NextScan.Core
{
    /// <summary>
    /// Asks a segmentation model what is lying on the glass, and turns each
    /// answer into a region proposal.
    ///
    /// What this is for, and what it is not for
    /// ----------------------------------------
    /// Every failure this detector has had in the field has been of one kind:
    /// not "the border is a millimetre out" but "that card came back as three
    /// pieces", "that card came back at half its height", "the passport's left
    /// page was never seen at all". They share a cause. The mask everything
    /// upstream is built from answers the question "is this pixel different
    /// from the glass?", and for pale paper on pale glass there is no answer --
    /// the card's own margin and the bare platen are the same brightness within
    /// a level or two. So the mask breaks into bands and whatever survives gets
    /// accepted on its own.
    ///
    /// A segmentation model does not ask that question. It was trained on a
    /// million images to answer "what are the objects here", and a white card
    /// on a white background is an object to it in a way it never was to a
    /// threshold. On the reference beds it finds every document, including the
    /// passport page this engine cannot see at all.
    ///
    /// What it does not do is place a border to the millimetre. Its masks are
    /// good to roughly half a millimetre to two millimetres, because it works
    /// on a 1024-pixel version of a 2552-pixel capture. So it is used for what
    /// it is good at -- how many items there are and roughly where -- and
    /// <see cref="PlatenShadowEdges"/> then puts each border where the item's
    /// own shadow says it is. Neither half can do the other's job.
    ///
    /// Optional, always
    /// ----------------
    /// No model, no runtime, or any failure at all, and this returns null and
    /// says why in the report. The detector then runs exactly as it did before
    /// this file existed.
    /// </summary>
    internal static class SamProposals
    {
        /// <summary>The side the model works at. Fixed by how it was trained.</summary>
        const int WorkingSide = 1024;

        /// <summary>Below this the model is not confident enough to be worth a look.</summary>
        const float MinScore = 0.88f;

        /// <summary>A mask smaller than this fraction of the bed is print, not an item.</summary>
        const double MinArea = 0.002;

        /// <summary>A mask larger than this fraction of the bed is the glass, not an item.</summary>
        const double MaxArea = 0.45;

        /// <summary>Two masks overlapping by more than this are the same thing.</summary>
        const double SameThing = 0.5;

        /// <summary>How far apart the exploring prompts are, as a fraction of the bed.</summary>
        const double GridStep = 0.16;

        /// <summary>
        /// The most prompts worth paying for. Each costs about sixty
        /// milliseconds, and a preview the operator waits five seconds for is a
        /// worse product than one that occasionally misses a small item. The
        /// sweep runs coarse-to-fine, so a cap cuts the least useful ones.
        /// </summary>
        const int MostPrompts = 26;

        internal sealed class Result
        {
            internal List<RotatedBox> Boxes = new List<RotatedBox>();

            /// <summary>
            /// What every reading of every prompt was, and what became of it.
            /// The model offers several honest answers per point and the
            /// choosing is ours; when the wrong one is chosen this is the only
            /// place that says which ones were on the table.
            /// </summary>
            internal List<string> Lines = new List<string>();
            internal string Note;
            internal long EncodeMs, PromptMs;
            internal int Prompts;
        }

        static string _encoderPath, _decoderPath;

        // What the last page was encoded to, kept so that asking about one more
        // point costs a prompt rather than another pass of the encoder. Reading
        // the image takes about a second; a prompt takes forty milliseconds, and
        // the difference is whether clicking a missed document feels like a
        // question or like a wait.
        static readonly object CacheGate = new object();
        static RawImage _seen;
        static float[] _embedding;
        static long[] _embeddingShape;
        static int _seenWidth, _seenHeight;

        /// <summary>Where the weights live: beside the application, under models\.</summary>
        internal static void Locate(out string encoder, out string decoder)
        {
            if (_encoderPath == null)
            {
                string root = AppDomain.CurrentDomain.BaseDirectory;
                string models = Path.GetFullPath(Path.Combine(root, "..", "models"));
                if (!Directory.Exists(models)) models = Path.Combine(root, "models");
                _encoderPath = Path.Combine(models, "mobile_sam_image_encoder.onnx");
                _decoderPath = Path.Combine(models, "sam_mask_decoder_multi.onnx");
            }
            encoder = _encoderPath; decoder = _decoderPath;
        }

        /// <summary>
        /// Segments the page. <paramref name="known"/> are the regions the
        /// classical detector already found; each contributes a prompt of its
        /// own, so an item it half-found gets asked about directly rather than
        /// waiting for the grid to land on it.
        /// </summary>
        internal static Result Propose(RawImage page, List<CropRegion> known, bool thorough)
        {
            Result result = new Result();
            if (page == null || !page.IsValid) { result.Note = "no page"; return result; }

            string encoderPath, decoderPath;
            Locate(out encoderPath, out decoderPath);
            if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            { result.Note = "no model installed at " + Path.GetDirectoryName(encoderPath); return result; }
            if (!Ort.Available) { result.Note = Ort.Availability; return result; }

            int width, height;
            float[] image = Resample(page, out width, out height);
            if (image == null) { result.Note = "page too small to segment"; return result; }

            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                using (Ort.Session encoder = new Ort.Session(encoderPath, Environment.ProcessorCount))
                {
                    if (!encoder.Loaded) { result.Note = "encoder: " + encoder.Error; return result; }

                    long[][] shapes;
                    float[][] embedding = encoder.Run(new float[][] { image },
                        new long[][] { new long[] { height, width, 3 } }, out shapes);
                    if (embedding == null) { result.Note = "encoder: " + encoder.Error; return result; }
                    result.EncodeMs = clock.ElapsedMilliseconds;

                    lock (CacheGate)
                    {
                        _seen = page; _embedding = embedding[0]; _embeddingShape = shapes[0];
                        _seenWidth = width; _seenHeight = height;
                    }

                    using (Ort.Session decoder = new Ort.Session(decoderPath, Environment.ProcessorCount))
                    {
                        if (!decoder.Loaded) { result.Note = "decoder: " + decoder.Error; return result; }
                        clock.Restart();
                        Segment(page, known, decoder, embedding[0], shapes[0], width, height, thorough, result);
                        result.PromptMs = clock.ElapsedMilliseconds;
                    }
                }
                result.Note = result.Boxes.Count + " segmented";
                return result;
            }
            catch (Exception ex) { result.Note = "segmentation failed: " + ex.Message; return result; }
        }

        /// <summary>
        /// Asks about one point, and nothing else.
        ///
        /// For the operator who can see a document the detector missed: they
        /// point at it, and this asks the model what is there. It reuses the
        /// reading of the page taken during the preview, so the answer arrives
        /// in the time of a single prompt. Without that reading -- a different
        /// page, or no preview yet -- it returns null rather than spending a
        /// second encoding one behind the operator's back.
        /// </summary>
        internal static RotatedBox ProbeAt(RawImage page, double normX, double normY)
        {
            return ProbeAt(page, normX, normY, 1.0);
        }

        /// <summary>
        /// <paramref name="ceiling"/> is the largest share of the bed the answer
        /// may cover. A point on bare glass has an honest answer -- the sheet,
        /// the platen, everything -- and without a ceiling that answer comes
        /// back as a document.
        /// </summary>
        internal static RotatedBox ProbeAt(RawImage page, double normX, double normY, double ceiling)
        {
            float[] embedding; long[] shape; int width, height;
            lock (CacheGate)
            {
                if (!ReferenceEquals(_seen, page) || _embedding == null) return null;
                embedding = _embedding; shape = _embeddingShape;
                width = _seenWidth; height = _seenHeight;
            }

            string encoderPath, decoderPath;
            Locate(out encoderPath, out decoderPath);
            if (!File.Exists(decoderPath) || !Ort.Available) return null;

            double scale = (double)width / page.Width;
            float px = (float)(normX * width), py = (float)(normY * height);
            if (px < 0 || py < 0 || px >= width || py >= height) return null;

            try
            {
                using (Ort.Session decoder = new Ort.Session(decoderPath, Environment.ProcessorCount))
                {
                    if (!decoder.Loaded) return null;

                    const int Low = 256;
                    int lowWidth = Math.Max(1, (int)Math.Round(width / 4.0));
                    int lowHeight = Math.Max(1, (int)Math.Round(height / 4.0));
                    int lowArea = lowWidth * lowHeight;

                    long[][] outShapes;
                    float[][] outputs = decoder.Run(
                        new float[][] { embedding, new float[] { px, py }, new float[] { 1 },
                                        new float[Low * Low], new float[] { 0 },
                                        new float[] { height, width } },
                        new long[][] { shape, new long[] { 1, 1, 2 }, new long[] { 1, 1 },
                                       new long[] { 1, 1, Low, Low }, new long[] { 1 }, new long[] { 2 } },
                        new int[] { 1, 2 }, out outShapes);
                    if (outputs == null) return null;

                    float[] scores = outputs[0], masks = outputs[1];
                    int readings = Math.Min(scores.Length, masks.Length / (Low * Low));

                    // The largest reading that still fits under the ceiling.
                    //
                    // Not the smallest. A point on an identity card is honestly
                    // the card, the photograph printed on it, and the panel
                    // around that photograph, and taking the smallest hands back
                    // the photograph every time -- which is exactly what it did.
                    // The operator pointed at a document, so the answer wanted is
                    // the biggest thing at that point that is still a document
                    // rather than the sheet underneath it.
                    double cap = Math.Min(MaxArea, ceiling);
                    int bestReading = -1; int bestArea = -1;
                    for (int reading = 0; reading < readings; reading++)
                    {
                        if (scores[reading] < MinScore) continue;
                        int offset = reading * Low * Low, area = 0;
                        for (int y = 0; y < lowHeight; y++)
                        {
                            int row = offset + y * Low;
                            for (int x = 0; x < lowWidth; x++) if (masks[row + x] > 0) area++;
                        }
                        if (area < MinArea * lowArea || area > cap * lowArea) continue;
                        if (area > bestArea) { bestArea = area; bestReading = reading; }
                    }
                    if (bestReading < 0) return null;

                    float[] mask = new float[Low * Low];
                    Array.Copy(masks, bestReading * Low * Low, mask, 0, Low * Low);
                    return BoxOf(mask, Low, lowWidth, lowHeight, 4.0 / scale);
                }
            }
            catch { return null; }
        }

        static void Segment(RawImage page, List<CropRegion> known, Ort.Session decoder,
                            float[] embedding, long[] embeddingShape, int width, int height,
                            bool thorough, Result result)
        {
            float[] noMaskInput = new float[256 * 256];
            long[] embeddingDims = embeddingShape;
            double scale = (double)width / page.Width;

            List<PointF> prompts = new List<PointF>();

            // What the classical detector already believes, asked about
            // directly. A card it found as a sliver still has a point inside
            // the real card, so this recovers the whole item.
            if (known != null)
                foreach (CropRegion item in known)
                {
                    if (item == null) continue;
                    RectangleF box = item.NormRect;
                    prompts.Add(new PointF((float)((box.X + box.Width / 2f) * width),
                                           (float)((box.Y + box.Height / 2f) * height)));
                }

            // And a coarse sweep for whatever it never saw.
            double step = thorough ? GridStep / 2 : GridStep;
            int stepX = Math.Max(8, (int)(width * step));
            int stepY = Math.Max(8, (int)(height * step));
            for (int y = stepY / 2; y < height; y += stepY)
                for (int x = stepX / 2; x < width; x += stepX)
                    prompts.Add(new PointF(x, y));

            // The small mask, not the full one. The decoder costs the same
            // either way, but the full mask is three megabytes that have to be
            // copied out of native memory on every single prompt, and nothing
            // here needs that much detail: a proposal only has to say which item
            // this is and roughly where, and the shadow pass puts the border
            // where it belongs afterwards.
            int[] wanted = new int[] { 1, 2 };                 // iou_predictions, low_res_masks
            const int Low = 256;
            int lowWidth = Math.Max(1, (int)Math.Round(width / 4.0));
            int lowHeight = Math.Max(1, (int)Math.Round(height / 4.0));
            int lowArea = lowWidth * lowHeight;

            bool[] covered = new bool[Low * Low];
            List<Candidate> distinct = new List<Candidate>();
            float[] coords = new float[2];
            float[] labels = new float[1];
            float[] size = new float[] { height, width };

            foreach (PointF prompt in prompts)
            {
                if (result.Prompts >= (thorough ? MostPrompts * 4 : MostPrompts)) break;

                // Ground already spoken for needs no second opinion. This is
                // most of the saving: the sweep is coarse enough to find a
                // missed item, but almost every one of its points lands on
                // something already segmented and is never paid for.
                int cx = (int)(prompt.X / 4), cy = (int)(prompt.Y / 4);
                if (cx < 0 || cy < 0 || cx >= Low || cy >= Low) continue;
                if (covered[cy * Low + cx]) continue;

                coords[0] = prompt.X; coords[1] = prompt.Y; labels[0] = 1;
                long[][] outShapes;
                float[][] outputs = decoder.Run(
                    new float[][] { embedding, coords, labels, noMaskInput, new float[] { 0 }, size },
                    new long[][] { embeddingDims, new long[] { 1, 1, 2 }, new long[] { 1, 1 },
                                   new long[] { 1, 1, 256, 256 }, new long[] { 1 }, new long[] { 2 } },
                    wanted, out outShapes);
                result.Prompts++;
                if (outputs == null) continue;

                // One point on a card can honestly mean three things: the card,
                // the card and what it is resting on, or the whole group of
                // them. A decoder that returns a single mask has to guess, and
                // on these beds it guessed the larger reading nearly every time
                // -- cards came back the right width and twice the height. This
                // one returns every reading it can see, and the size filters and
                // the pipeline below decide between them rather than the model.
                float[] scores = outputs[0];
                float[] masks = outputs[1];
                int readings = Math.Min(scores.Length, masks.Length / (Low * Low));

                for (int reading = 0; reading < readings; reading++)
                {
                    float score = scores[reading];
                    float bar = thorough ? MinScore - 0.08f : MinScore;

                    int offset = reading * Low * Low;
                    int area = 0;
                    for (int y = 0; y < lowHeight; y++)
                    {
                        int row = offset + y * Low;
                        for (int x = 0; x < lowWidth; x++) if (masks[row + x] > 0) area++;
                    }

                    string label = "  at " + ((int)prompt.X) + "," + ((int)prompt.Y) + " reading " + reading
                        + " score " + score.ToString("0.000") + " area " + (area / (double)lowArea).ToString("0.0000");
                    if (score < bar) { result.Lines.Add(label + " -> below score"); continue; }
                    if (area < MinArea * lowArea) { result.Lines.Add(label + " -> too small"); continue; }
                    if (area > MaxArea * lowArea) { result.Lines.Add(label + " -> too large"); continue; }

                    float[] mask = new float[Low * Low];
                    Array.Copy(masks, offset, mask, 0, Low * Low);

                    double worst = 0;
                    foreach (Candidate settled in distinct)
                    {
                        double share = Overlap(mask, settled.Mask, Low, lowWidth, lowHeight);
                        if (share > worst) worst = share;
                    }
                    if (worst > SameThing)
                    { result.Lines.Add(label + " -> same as one already taken (" + worst.ToString("0.00") + ")"); continue; }
                    result.Lines.Add(label + " -> taken");

                    // Only something accepted may claim its ground. A mask
                    // rejected for being the whole glass, or a scrap of print,
                    // still covers the item underneath it, and marking that
                    // covered silences the prompt that would have found the item
                    // properly. Two cards went missing exactly that way.
                    for (int y = 0; y < lowHeight; y++)
                    {
                        int row = y * Low;
                        for (int x = 0; x < lowWidth; x++) if (mask[row + x] > 0) covered[row + x] = true;
                    }

                    distinct.Add(new Candidate { Score = score, Area = area, Mask = mask });
                }
            }

            foreach (Candidate candidate in distinct)
            {
                RotatedBox box = BoxOf(candidate.Mask, Low, lowWidth, lowHeight, 4.0 / scale);
                if (box != null) result.Boxes.Add(box);
            }
        }

        sealed class Candidate
        {
            internal float Score;
            internal int Area;
            internal float[] Mask;
        }

        static double Overlap(float[] first, float[] second, int stride, int width, int height)
        {
            int intersection = 0, union = 0;
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    bool a = first[row + x] > 0, b = second[row + x] > 0;
                    if (a && b) intersection++;
                    if (a || b) union++;
                }
            }
            return union == 0 ? 0 : intersection / (double)union;
        }

        /// <summary>
        /// The smallest rectangle, at any angle, holding a mask -- measured on
        /// the mask's outline and given back in the page's own pixels.
        /// </summary>
        static RotatedBox BoxOf(float[] mask, int stride, int width, int height, double toPage)
        {
            // One piece only. A single prompt can come back as a mask covering
            // two things at once -- a card and the photo above it -- and one
            // rectangle drawn round both is the right width and twice the
            // height, which is exactly how these cards were being reported.
            // The mask area gave it away: it matched a single card while its
            // box was twice a card, so the mask was filling less than half of
            // what was drawn round it.
            int[] label = new int[stride * height];
            int best = 0, bestSize = 0;

            // Stride * height, exactly like the labels above, and not
            // width * height: the queue holds the flat, strided index
            // y * stride + x, so its highest reachable value is
            // (height - 1) * stride + (width - 1). Sizing it width * height
            // left it short by (stride - width) * (height - 1) entries, which
            // is zero for a square mask and positive for every page whose
            // low-res mask is narrower than the stride -- so a portrait A4
            // capture overflowed the moment a component reached the bottom
            // quarter of the page, and Propose's catch turned the exception
            // into a note saying the model was unusable.
            int[] queue = new int[stride * height];

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int seed = y * stride + x;
                    if (mask[seed] <= 0 || label[seed] != 0) continue;

                    int mark = ++best, size = 0, head = 0, tail = 0;
                    queue[tail++] = seed; label[seed] = mark;
                    while (head < tail)
                    {
                        int at = queue[head++]; size++;
                        int ax = at % stride, ay = at / stride;
                        if (ax > 0) Push(mask, label, queue, ref tail, at - 1, mark);
                        if (ax < width - 1) Push(mask, label, queue, ref tail, at + 1, mark);
                        if (ay > 0) Push(mask, label, queue, ref tail, at - stride, mark);
                        if (ay < height - 1) Push(mask, label, queue, ref tail, at + stride, mark);
                    }
                    if (size > bestSize) { bestSize = size; }
                }

            if (bestSize == 0) return null;

            // Which label was the biggest.
            int[] sizes = new int[best + 1];
            for (int i = 0; i < label.Length; i++) if (label[i] > 0) sizes[label[i]]++;
            int keep = 1;
            for (int i = 1; i <= best; i++) if (sizes[i] > sizes[keep]) keep = i;

            List<PointF> outline = new List<PointF>();
            for (int y = 0; y < height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    if (label[row + x] != keep) continue;
                    bool edge = x == 0 || y == 0 || x == width - 1 || y == height - 1
                        || label[row + x - 1] != keep || label[row + x + 1] != keep
                        || label[row - stride + x] != keep || label[row + stride + x] != keep;
                    if (edge) outline.Add(new PointF((float)(x * toPage), (float)(y * toPage)));
                }
            }
            if (outline.Count < 8) return null;
            return PlatenGeometry.Fit(outline);
        }

        static void Push(float[] mask, int[] label, int[] queue, ref int tail, int at, int mark)
        {
            if (mask[at] <= 0 || label[at] != 0) return;
            label[at] = mark;
            queue[tail++] = at;
        }

        /// <summary>
        /// The page at the size the model expects, as red, green and blue in
        /// that order. The capture is blue first, and at any depth the model has
        /// never seen, so both are put right here rather than anywhere else.
        /// </summary>
        static float[] Resample(RawImage page, out int width, out int height)
        {
            width = height = 0;
            if (page.Width < 8 || page.Height < 8) return null;

            double scale = (double)WorkingSide / Math.Max(page.Width, page.Height);
            width = Math.Max(1, (int)Math.Round(page.Width * scale));
            height = Math.Max(1, (int)Math.Round(page.Height * scale));

            float[] output = new float[width * height * 3];
            double stepX = (double)page.Width / width, stepY = (double)page.Height / height;

            for (int y = 0; y < height; y++)
            {
                int fromY = (int)(y * stepY), toY = (int)((y + 1) * stepY);
                if (toY <= fromY) toY = fromY + 1;
                if (toY > page.Height) toY = page.Height;

                for (int x = 0; x < width; x++)
                {
                    int fromX = (int)(x * stepX), toX = (int)((x + 1) * stepX);
                    if (toX <= fromX) toX = fromX + 1;
                    if (toX > page.Width) toX = page.Width;

                    // Box average, not nearest: a card's edge is one pixel wide
                    // at 300 dpi and would otherwise alias away entirely on the
                    // way down to a quarter of the size.
                    int red = 0, green = 0, blue = 0, count = 0;
                    for (int sy = fromY; sy < toY; sy++)
                        for (int sx = fromX; sx < toX; sx++)
                        {
                            int r, g, b;
                            Sample(page, sx, sy, out r, out g, out b);
                            red += r; green += g; blue += b; count++;
                        }
                    if (count == 0) count = 1;

                    int offset = (y * width + x) * 3;
                    output[offset] = red / (float)count;
                    output[offset + 1] = green / (float)count;
                    output[offset + 2] = blue / (float)count;
                }
            }
            return output;
        }

        static void Sample(RawImage p, int x, int y, out int red, out int green, out int blue)
        {
            if (p.BitsPerChannel == 1)
            {
                int on = (p.Pixels[y * p.Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? 255 : 0;
                red = green = blue = on;
                return;
            }

            int step = p.BitsPerChannel == 16 ? 2 : 1;
            int o = y * p.Stride + x * p.Channels * step;
            if (p.BitsPerChannel == 16) o += 1;            // high byte of the little-endian pair

            if (p.Channels == 1) { red = green = blue = p.Pixels[o]; return; }
            blue = p.Pixels[o];
            green = p.Pixels[o + step];
            red = p.Pixels[o + step * 2];
        }
    }
}
