// =============================================================================
// NextScan Studio - the one shape three providers are made to fit
// Plan ref: docs/AI_LAYER.md
// =============================================================================
using System;
using System.Collections.Generic;
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
        /// The model to start on: the first preference that the provider
        /// actually returned, or failing that the first model it returned.
        /// </summary>
        public static string Default(IAiProvider provider, IList<AiModel> list)
        {
            if (list == null || list.Count == 0) return "";

            foreach (string wanted in provider.Info.Prefer)
                foreach (AiModel model in list)
                    if (model.Id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) return model.Id;

            return list[0].Id;
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
