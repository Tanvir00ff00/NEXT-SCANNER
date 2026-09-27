// =============================================================================
// NextScan Studio - sorting scanned documents by kind, with Jev
// Plan ref: docs/AI_LAYER.md
//
// When the session is saved, each document is given a kind -- a bill, a
// prescription, a letter -- and that kind can name its folder or its file
// ({kind} in the name pattern). Two models do it, each at what it is for:
//
//   1. a fast model of the assistant's provider (Flash, Haiku, mini) reads
//      the document's first page, since Jev reads text and cannot look at a
//      picture, and
//   2. Jev decides which of the operator's kinds that text is, with a
//      confidence. Below half, the document is "Other" rather than a guess.
//
// Nothing here runs unless the operator turned it on, and it says which model
// read the page, because that model is the one that costs money.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NextScan.Ai;
using NextScan.Core;

namespace NextScan.App
{
    public static class JevSorter
    {
        /// <summary>A kind below this confidence is "Other": a wrong folder is worse than an unsorted one.</summary>
        public const double MinConfidence = 0.5;

        public const string OtherKind = "Other";

        /// <summary>The operator's kinds, cleaned, with "Other" always among them.</summary>
        public static List<string> Kinds(string line)
        {
            var kinds = new List<string>();
            foreach (string piece in (line ?? "").Split(','))
            {
                string k = piece.Trim();
                foreach (char bad in new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) k = k.Replace(bad, ' ');
                k = k.Trim();
                if (k.Length == 0 || k.Length > 40) continue;
                if (kinds.Exists(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase))) continue;
                kinds.Add(k);
            }
            if (!kinds.Exists(x => string.Equals(x, OtherKind, StringComparison.OrdinalIgnoreCase))) kinds.Add(OtherKind);
            return kinds;
        }

        /// <summary>
        /// The model that reads the pages: the assistant's own when it can see
        /// pictures, otherwise the first of Claude, Gemini and OpenAI that is
        /// set up. A compatible server is not used: whether it can see is not
        /// something this application can know.
        /// </summary>
        public static IAiProvider Reader(string preferredId)
        {
            IAiProvider preferred = AiProviders.ById(preferredId ?? "");
            if (preferred != null && preferred.Ready && !(preferred is OpenAiCompatProvider)) return preferred;
            foreach (string id in new[] { "claude", "gemini", "openai" })
            {
                IAiProvider p = AiProviders.ById(id);
                if (p != null && p.Ready) return p;
            }
            return null;
        }

        /// <summary>
        /// The reader's model. Telling what a document is needs its first
        /// lines read, not the largest model on the account: the fast, cheap
        /// tier (Flash, Haiku, mini) is chosen, the newest of it, and only when
        /// there is none the assistant's own model or the provider's default.
        /// Also because the large ones are the ones a free key has no quota for.
        /// </summary>
        public static async Task<string> ReaderModel(IAiProvider reader, string preferredModel, CancellationToken cancel)
        {
            IList<AiModel> list = await AiModels.Fetch(reader, false, cancel).ConfigureAwait(false);
            if (Cheap(preferredModel)) return preferredModel;

            AiModel best = null;
            foreach (AiModel m in list)
            {
                if (!m.Likely || !Cheap(m.Id) || m.Id.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (m.Id.IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.Id.IndexOf("nano", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // too small to read Bengali well
                if (best == null || Newer(m, best)) best = m;
            }
            if (best != null) return best.Id;
            return !string.IsNullOrEmpty(preferredModel) ? preferredModel : AiModels.Default(reader, list);
        }

        static bool Cheap(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (string tier in new[] { "flash", "haiku", "mini" })
                if (id.IndexOf(tier, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Newer by the provider's date when it gives one, otherwise by the version in the id.</summary>
        static bool Newer(AiModel a, AiModel b)
        {
            if (a.Released != DateTime.MinValue && b.Released != DateTime.MinValue && a.Released != b.Released)
                return a.Released > b.Released;
            return Version(a.Id) > Version(b.Id);
        }

        /// <summary>The first number in an id: 3.1 from "gemini-3.1-flash", 4 from "claude-haiku-4-5".</summary>
        static double Version(string id)
        {
            int i = 0;
            while (i < id.Length && !char.IsDigit(id[i])) i++;
            int start = i;
            while (i < id.Length && (char.IsDigit(id[i]) || id[i] == '.')) i++;
            double v;
            return double.TryParse(id.Substring(start, i - start).Trim('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v < 100 ? v : -1;
        }

        /// <summary>Reads a page's text: enough of it to tell what the document is.</summary>
        public static async Task<string> Read(IAiProvider reader, string model, byte[] jpeg, CancellationToken cancel)
        {
            var request = new AiRequest
            {
                Model = model,
                Thinking = ThinkingLevel.Low,
                MaxOutputTokens = 1024,
                Instruction = "You read scanned documents for a filing system. You answer with the text only.",
            };
            AiMessage turn = AiMessage.FromUser(
                "Transcribe the text on this scanned page as plain text: the headings, names, and the first " +
                "lines of the body, up to about 250 words. Keep the language it is written in. Bengali set in " +
                "a Bijoy font such as SutonnyMJ can look like Latin letters; write it as the Bengali it shows. " +
                "Write only the text, with no comment.");
            turn.Image = jpeg;
            turn.ImageMediaType = "image/jpeg";
            request.Messages.Add(turn);

            // "High demand, try again later" and rate limits are passing
            // weather, not a verdict: two more tries, a little apart, before
            // the document is left unsorted.
            int[] waits = { 2000, 6000 };
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    AiReply reply = await reader.Ask(request, null, cancel).ConfigureAwait(false);
                    return (reply.Text ?? "").Trim();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception)
                {
                    if (attempt >= waits.Length || cancel.IsCancellationRequested) throw;
                }
                await Task.Delay(waits[attempt], cancel).ConfigureAwait(false);
            }
        }

        static string Article(string word)
        {
            return word.Length > 0 && "aeiouAEIOU".IndexOf(word[0]) >= 0 ? "an" : "a";
        }

        /// <summary>Which of the kinds a text is, or "Other" when Jev is not sure enough.</summary>
        public static async Task<JevChoice> KindOf(string text, IList<string> kinds, CancellationToken cancel)
        {
            var criteria = new Dictionary<string, string>();
            foreach (string k in kinds)
                criteria[k] = string.Equals(k, OtherKind, StringComparison.OrdinalIgnoreCase)
                    ? "None of the other kinds, or it cannot be told from the text."
                    : "The document is " + Article(k) + " " + k.ToLower(CultureInfo.InvariantCulture) + ".";

            JevChoice answer = await JevClient.Choose(text,
                "What kind of document is this? It was scanned in a shop in Bangladesh; its text may be in " +
                "Bengali or English, and may have reading mistakes.", criteria, cancel).ConfigureAwait(false);

            if (answer.Confidence < MinConfidence) answer.Choice = OtherKind;
            return answer;
        }
    }
}
