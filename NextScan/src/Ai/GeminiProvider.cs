// =============================================================================
// NextScan Studio - Gemini
// Plan ref: docs/AI_LAYER.md
//
// Google.GenAI is Google's own SDK. Several community packages carry names a
// search will offer first -- Google_GenerativeAI among them -- and they are
// not this.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NextScan.Ai
{
    public class GeminiProvider : IAiProvider
    {
        public AiProviderInfo Info { get; } = new AiProviderInfo
        {
            Id = "gemini",
            Name = "Gemini",
            KeyHint = "AIza...",
            Models = new[] { "gemini-3-pro", "gemini-3-flash" },
            Ceiling = ThinkingLevel.Max,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

        static string LevelFor(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Low: return "low";
                case ThinkingLevel.Medium: return "medium";
                case ThinkingLevel.Max: return "max";
                default: return "high";
            }
        }

        public async Task<AiReply> Ask(AiRequest request, AiTextArrived onText, CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No Gemini key has been set.");

            var client = new Google.GenAI.Client(apiKey: key);

            // Gemini has no separate assistant role name: a reply is "model".
            var contents = new List<Google.GenAI.Types.Content>();
            foreach (AiMessage m in request.Messages)
                contents.Add(new Google.GenAI.Types.Content
                {
                    Role = m.Role == AiRole.User ? "user" : "model",
                    Parts = new List<Google.GenAI.Types.Part> { new Google.GenAI.Types.Part { Text = m.Text } },
                });

            var config = new Google.GenAI.Types.GenerateContentConfig
            {
                MaxOutputTokens = request.MaxOutputTokens,
                ThinkingConfig = new Google.GenAI.Types.ThinkingConfig { ThinkingLevel = LevelFor(request.Thinking) },
            };

            if (!string.IsNullOrEmpty(request.Instruction))
                config.SystemInstruction = new Google.GenAI.Types.Content
                {
                    Parts = new List<Google.GenAI.Types.Part> { new Google.GenAI.Types.Part { Text = request.Instruction } },
                };

            var reply = new AiReply();
            reply.Usage.Model = request.Model;
            var text = new System.Text.StringBuilder();

            await foreach (var chunk in client.Models.GenerateContentStreamAsync(request.Model, contents, config))
            {
                cancel.ThrowIfCancellationRequested();

                string piece = chunk.Text;
                if (!string.IsNullOrEmpty(piece))
                {
                    text.Append(piece);
                    if (onText != null) onText(piece);
                }

                if (chunk.UsageMetadata != null)
                {
                    reply.Usage.InputTokens = chunk.UsageMetadata.PromptTokenCount ?? 0;
                    reply.Usage.OutputTokens = chunk.UsageMetadata.CandidatesTokenCount ?? 0;
                    reply.Usage.CachedInputTokens = chunk.UsageMetadata.CachedContentTokenCount ?? 0;
                }
            }

            reply.Text = text.ToString();
            return reply;
        }
    }
}
