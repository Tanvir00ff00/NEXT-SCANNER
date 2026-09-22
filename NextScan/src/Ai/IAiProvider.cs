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
}
