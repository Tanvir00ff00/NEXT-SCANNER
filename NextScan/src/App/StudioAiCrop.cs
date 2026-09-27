// =============================================================================
// NextScan Studio - finding the items on the glass with a vision model
// Plan ref: docs/AI_LAYER.md, the auto crop (PlatenDetector)
//
// The auto crop fails in one way above all: not "the edge is a millimetre
// out" but "that card was never seen", "that card came back as three
// pieces" -- a pale card on pale glass gives a threshold nothing, and the
// segmentation model is only prompted on a blind grid of points. A vision
// model does not have that problem: shown the preview, it can say "an ID card
// here, a passport page there, a photograph there".
//
// It cannot place an edge to the millimetre, and it is not asked to. Its boxes
// go into the detector as hints (AutoCropOptions.Hints): the segmentation model
// is asked at each of them, an item nothing else found becomes a region of its
// own, and the shadow pass then puts every edge where the item's own shadow
// says it is. The model says what and roughly where; the engine says exactly.
//
// Only when the operator presses "Find items with AI", or turned on "Use AI
// on every preview": each call is a paid request with the page on it.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NextScan.Ai;

namespace NextScan.App
{
    public static class AiCrop
    {
        public class Item
        {
            public string Label = "";

            /// <summary>Where it is, as fractions of the page (0..1).</summary>
            public RectangleF Box;
        }

        /// <summary>
        /// The model that looks: Gemini first, because finding objects and
        /// giving their boxes is something it was trained to do and documents;
        /// otherwise the assistant's own provider, then the others.
        /// </summary>
        public static IAiProvider Looker(string preferredId)
        {
            return Looker("", preferredId);
        }

        /// <summary>
        /// The model the operator chose in Settings (<paramref name="via"/>, a
        /// provider id -- any of them, a server included), when it is set up;
        /// otherwise the automatic choice above.
        /// </summary>
        public static IAiProvider Looker(string via, string preferredId)
        {
            if (!string.IsNullOrEmpty(via))
            {
                IAiProvider chosen = AiProviders.ById(via);
                if (chosen != null && chosen.Ready) return chosen;
            }
            IAiProvider gemini = AiProviders.ById("gemini");
            if (gemini != null && gemini.Ready) return gemini;
            return JevSorter.Reader(preferredId);
        }

        /// <summary>The model to look with: the one chosen for this provider, or its fast one.</summary>
        public static Task<string> Model(IAiProvider looker, string via, string chosenModel, string panelModel, CancellationToken cancel)
        {
            if (!string.IsNullOrEmpty(via) && looker.Info.Id == via && !string.IsNullOrEmpty(chosenModel))
                return Task.FromResult(chosenModel);
            return JevSorter.ReaderModel(looker, panelModel, cancel);
        }

        const string Ask =
            "This is a preview from a flatbed scanner: the whole glass, seen from below. Find every separate " +
            "physical item lying on the glass -- ID cards, photographs, receipts, bills, letters, certificates, " +
            "passport pages, business cards -- and give each one's box.\n\n" +
            "Rules:\n" +
            "- One box per physical item, from its edge to its edge, including any white margin of the item itself.\n" +
            "- Never a box for something printed on an item: the photo on an ID card is part of the card.\n" +
            "- Items often touch each other edge to edge. Two cards side by side are TWO items even when they are " +
            "the same colour: look for the thin line where one ends and the next begins, and for a repeated layout " +
            "(two photos, two headers) -- a card is about 86 x 54 mm, so a 'card' twice that long is two cards.\n" +
            "- An open passport or booklet whose two pages touch at the spine is ONE item.\n" +
            "- Items can be tilted; the box must still contain the whole item.\n" +
            "- The glass, its frame and shadows are not items. If nothing is on the glass, answer [].\n\n" +
            "Answer with JSON only, no other text: a list of {\"label\": \"id card\", \"box_2d\": [ymin, xmin, ymax, xmax]} " +
            "with the coordinates scaled 0 to 1000 across this image.";

        /// <summary>Asks the model where the items are on a preview (JPEG of the whole preview).</summary>
        public static async Task<List<Item>> Find(IAiProvider looker, string model, byte[] jpeg, CancellationToken cancel)
        {
            var request = new AiRequest
            {
                Model = model,
                Thinking = ThinkingLevel.Low,
                MaxOutputTokens = 2048,
                Instruction = "You locate objects in images and answer with JSON only.",
            };
            AiMessage turn = AiMessage.FromUser(Ask);
            turn.Image = jpeg;
            turn.ImageMediaType = "image/jpeg";
            request.Messages.Add(turn);

            int[] waits = { 2000, 6000 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    AiReply reply = await looker.Ask(request, null, cancel).ConfigureAwait(false);
                    return Parse(reply.Text ?? "");
                }
                catch (OperationCanceledException) { throw; }
                catch (FormatException) { throw; }
                catch (Exception)
                {
                    if (attempt >= waits.Length || cancel.IsCancellationRequested) throw;
                }
                await Task.Delay(waits[attempt], cancel).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Reads the model's list. Tolerant of what models wrap JSON in (a
        /// code fence, a sentence before it) and strict about the boxes: four
        /// numbers in 0..1000, in the documented order, or the item is left out.
        /// </summary>
        public static List<Item> Parse(string text)
        {
            string s = (text ?? "").Trim();
            Match fenced = Regex.Match(s, "```(?:json)?\\s*(.*?)```", RegexOptions.Singleline);
            if (fenced.Success) s = fenced.Groups[1].Value.Trim();
            int start = s.IndexOf('['), end = s.LastIndexOf(']');
            if (start < 0 || end < start) throw new FormatException("The model did not answer with a list of boxes.");
            s = s.Substring(start, end - start + 1);

            object parsed;
            try { parsed = new JavaScriptSerializer().DeserializeObject(s); }
            catch (Exception ex) { throw new FormatException("The model's list of boxes could not be read: " + ex.Message); }

            var items = new List<Item>();
            var list = parsed as object[];
            if (list == null) return items;
            foreach (object entry in list)
            {
                var d = entry as Dictionary<string, object>;
                if (d == null) continue;
                object raw;
                if (!d.TryGetValue("box_2d", out raw) && !d.TryGetValue("box", out raw)) continue;
                var nums = raw as object[];
                if (nums == null || nums.Length != 4) continue;
                double[] v = new double[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                {
                    try { v[i] = Convert.ToDouble(nums[i], CultureInfo.InvariantCulture); }
                    catch { ok = false; }
                    if (!ok || v[i] < -5 || v[i] > 1005) { ok = false; break; }
                }
                if (!ok) continue;

                double ymin = Math.Min(v[0], v[2]), ymax = Math.Max(v[0], v[2]);
                double xmin = Math.Min(v[1], v[3]), xmax = Math.Max(v[1], v[3]);
                ymin = Clamp(ymin); ymax = Clamp(ymax); xmin = Clamp(xmin); xmax = Clamp(xmax);
                if (xmax - xmin < 8 || ymax - ymin < 8) continue;

                object label;
                items.Add(new Item
                {
                    Label = d.TryGetValue("label", out label) && label != null ? Convert.ToString(label, CultureInfo.InvariantCulture) : "item",
                    Box = RectangleF.FromLTRB((float)(xmin / 1000), (float)(ymin / 1000), (float)(xmax / 1000), (float)(ymax / 1000)),
                });
            }
            return items;
        }

        static double Clamp(double v) { return Math.Max(0, Math.Min(1000, v)); }
    }
}
