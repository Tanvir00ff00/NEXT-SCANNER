// =============================================================================
// NextScan Studio - the one shape three providers are made to fit
// Plan ref: docs/AI_LAYER.md
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace NextScan.Ai
{
    /// <summary>
    /// Called as the reply arrives, on a worker thread. The panel marshals to
    /// the UI thread itself, because a provider has no business knowing there
    /// is one.
    /// </summary>
    public delegate void AiTextArrived(string fragment);

    public interface IAiProvider
    {
        AiProviderInfo Info { get; }

        /// <summary>True once a key has been stored for this provider.</summary>
        bool Ready { get; }

        /// <summary>
        /// The models this key can actually reach, asked of the provider.
        ///
        /// Asked rather than declared, because the answer depends on the
        /// account: two keys for the same provider do not see the same list,
        /// and a list written into our source would be out of date by the next
        /// model release with nothing to say so.
        /// </summary>
        Task<IList<AiModel>> Models(CancellationToken cancel);

        /// <summary>
        /// Sends a turn and streams the reply.
        ///
        /// Streaming rather than a single answer, and not as a preference: a
        /// long reply on a non-streaming call can outlive the HTTP timeout, and
        /// a chat that shows nothing for thirty seconds reads as a chat that has
        /// broken.
        /// </summary>
        Task<AiReply> Ask(AiRequest request, AiTextArrived onText, CancellationToken cancel);
    }

    /// <summary>
    /// Which providers exist, and which one the panel is using.
    /// </summary>
    public static class AiProviders
    {
        public static IReadOnlyList<IAiProvider> All()
        {
            return new IAiProvider[]
            {
                new ClaudeProvider(),
                new OpenAiProvider(),
                new GeminiProvider(),
            };
        }

        public static IAiProvider ById(string id)
        {
            foreach (IAiProvider p in All())
                if (string.Equals(p.Info.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }
    }

    /// <summary>
    /// Which models the operator has said the panel may offer.
    ///
    /// Stored as one line of "provider/model" ids, because that is what fits in
    /// the settings file this application has always used, and an operator
    /// looking at that file can read it and edit it.
    ///
    /// Empty means "nothing has been said", not "nothing is allowed". The two
    /// are easy to conflate and the second one is a panel with no models in it.
    /// </summary>
    public class AiAllowed
    {
        readonly List<string> _ids = new List<string>();

        public static AiAllowed Read(string line)
        {
            var it = new AiAllowed();
            if (string.IsNullOrEmpty(line)) return it;

            foreach (string piece in line.Split(','))
            {
                string trimmed = piece.Trim();
                if (trimmed.Length > 0 && !it._ids.Contains(trimmed)) it._ids.Add(trimmed);
            }
            return it;
        }

        public override string ToString() { return string.Join(",", _ids.ToArray()); }

        static string Key(string providerId, string modelId) { return providerId + "/" + modelId; }

        public bool Has(string providerId, string modelId) { return _ids.Contains(Key(providerId, modelId)); }

        public bool Any(string providerId)
        {
            string prefix = providerId + "/";
            foreach (string id in _ids) if (id.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        public void Set(string providerId, string modelId, bool on)
        {
            string key = Key(providerId, modelId);
            if (on) { if (!_ids.Contains(key)) _ids.Add(key); }
            else _ids.Remove(key);
        }

        public void Clear(string providerId)
        {
            string prefix = providerId + "/";
            _ids.RemoveAll(delegate (string id) { return id.StartsWith(prefix, StringComparison.Ordinal); });
        }
    }

    /// <summary>
    /// The fetched model lists, kept for as long as the application runs.
    ///
    /// Cached against the key rather than against the provider: changing the
    /// key changes the account, and an account change that kept the old list
    /// would offer models the new key cannot reach. Nothing here is written to
    /// disk -- a stale list on disk is the failure this whole class exists to
    /// avoid.
    /// </summary>
    public static class AiModels
    {
        static readonly object Lock = new object();
        static readonly Dictionary<string, IList<AiModel>> Known = new Dictionary<string, IList<AiModel>>();

        static string KeyFor(IAiProvider provider)
        {
            return provider.Info.Id + "/" + AiKeys.Tail(provider.Info.Id);
        }

        public static IList<AiModel> Cached(IAiProvider provider)
        {
            if (provider == null) return null;
            lock (Lock)
            {
                IList<AiModel> found;
                return Known.TryGetValue(KeyFor(provider), out found) ? found : null;
            }
        }

        public static async Task<IList<AiModel>> Fetch(IAiProvider provider, bool again, CancellationToken cancel)
        {
            if (provider == null) return new List<AiModel>();

            if (!again)
            {
                IList<AiModel> had = Cached(provider);
                if (had != null) return had;
            }

            IList<AiModel> list = await provider.Models(cancel).ConfigureAwait(false);
            lock (Lock) { Known[KeyFor(provider)] = list; }
            return list;
        }

        public static void Forget(IAiProvider provider)
        {
            if (provider == null) return;
            lock (Lock) { Known.Remove(KeyFor(provider)); }
        }

        /// <summary>
        /// The model to start on: the newest of the highest preference the
        /// provider actually returned.
        ///
        /// Newest matters, and the first attempt did not do it. Taking the first
        /// match in the order the API happened to return them chose Gemini 2.5
        /// Pro on an account that also had 3.1 Pro, and 2.5 Pro answers a real
        /// request with "no longer available to new users". The list is the
        /// provider's; the ordering of it is not something to rely on.
        /// </summary>
        public static string Default(IAiProvider provider, IList<AiModel> list)
        {
            if (list == null || list.Count == 0) return "";

            // A model the operator would not be shown is not a model to start
            // them on.
            var usable = new List<AiModel>();
            foreach (AiModel model in list) if (model.Likely) usable.Add(model);
            if (usable.Count > 0) list = usable;

            foreach (string wanted in provider.Info.Prefer)
            {
                string best = Newest(provider, list, wanted);
                if (best.Length > 0) return best;
            }

            string any = Newest(provider, list, "");
            return any.Length > 0 ? any : list[0].Id;
        }

        static string Newest(IAiProvider provider, IList<AiModel> list, string wanted)
        {
            string best = "";
            double bestVersion = double.MinValue;

            foreach (AiModel model in list)
            {
                if (Poor(provider, model)) continue;
                if (wanted.Length > 0 &&
                    model.Id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;

                double version = Version(model.Id);
                if (best.Length == 0 || version > bestVersion) { best = model.Id; bestVersion = version; }
            }
            return best;
        }

        /// <summary>
        /// The version number inside a model id: 3.1 from "gemini-3.1-pro",
        /// 5 from "claude-opus-5", 4.1 from "gpt-4.1".
        ///
        /// The FIRST number, not the largest. The first attempt took the largest
        /// that was not obviously a date, and every provider also puts a release
        /// month in its ids: "deep-research-pro-preview-12-2025" scored 12 and
        /// became the newest thing on the account.
        /// </summary>
        static double Version(string id)
        {
            int i = 0;
            while (i < id.Length && !char.IsDigit(id[i])) i++;
            if (i >= id.Length) return -1;

            int start = i;
            while (i < id.Length && (char.IsDigit(id[i]) || id[i] == '.')) i++;

            string piece = id.Substring(start, i - start).Trim('.');
            int digits = 0;
            foreach (char c in piece) if (char.IsDigit(c)) digits++;
            if (digits > 4) return -1;

            double v;
            return double.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v <= 100
                ? v : -1;
        }

        static bool Poor(IAiProvider provider, AiModel model)
        {
            foreach (string no in provider.Info.Avoid)
                if (model.Id.IndexOf(no, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// The models the panel offers for one provider: the operator's choice
        /// where they have made one, and the likely ones until they do.
        /// </summary>
        public static IList<AiModel> Offered(IAiProvider provider, AiAllowed allowed)
        {
            var found = new List<AiModel>();
            IList<AiModel> all = Cached(provider);
            if (all == null) return found;

            bool chosen = allowed != null && allowed.Any(provider.Info.Id);
            foreach (AiModel model in all)
                if (chosen ? allowed.Has(provider.Info.Id, model.Id) : model.Likely) found.Add(model);

            // Never leave the menu empty because of a filter of ours. If the
            // guess ruled everything out, the guess is what is wrong.
            if (found.Count == 0 && !chosen) foreach (AiModel model in all) found.Add(model);
            return found;
        }

        public static string NameOf(IAiProvider provider, string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            IList<AiModel> list = Cached(provider);
            if (list != null)
                foreach (AiModel model in list)
                    if (model.Id == id) return model.ToString();
            return id;
        }
    }
}
