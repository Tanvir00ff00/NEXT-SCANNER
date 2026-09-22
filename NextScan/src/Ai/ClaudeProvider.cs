// =============================================================================
// NextScan Studio - Claude
// Plan ref: docs/AI_LAYER.md
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;

namespace NextScan.Ai
{
    public class ClaudeProvider : IAiProvider
    {
        public AiProviderInfo Info { get; } = new AiProviderInfo
        {
            Id = "claude",
            Name = "Claude",
            KeyHint = "sk-ant-...",
            Models = new[] { "claude-opus-5", "claude-sonnet-5", "claude-haiku-4-5" },
            Ceiling = ThinkingLevel.Max,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

        /// <summary>
        /// Depth is set through effort, not a token budget.
        ///
        /// The budget form -- thinking: {type: "enabled", budget_tokens: N} --
        /// is what most writing about this API still shows, and it is rejected
        /// with a 400 on the current models. It would compile here and fail at
        /// the first call.
        /// </summary>
        static Effort EffortFor(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Low: return Effort.Low;
                case ThinkingLevel.Medium: return Effort.Medium;
                case ThinkingLevel.Max: return Effort.Max;
                default: return Effort.High;
            }
        }

        static MediaType MediaFor(string mime)
        {
            if (string.Equals(mime, "image/png", StringComparison.OrdinalIgnoreCase)) return MediaType.ImagePng;
            if (string.Equals(mime, "image/gif", StringComparison.OrdinalIgnoreCase)) return MediaType.ImageGif;
            if (string.Equals(mime, "image/webp", StringComparison.OrdinalIgnoreCase)) return MediaType.ImageWebP;
            return MediaType.ImageJpeg;
        }

        public async Task<AiReply> Ask(AiRequest request, AiTextArrived onText, CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No Claude key has been set.");

            var client = new AnthropicClient(new ClientOptions { ApiKey = key });

            var messages = new List<MessageParam>();
            foreach (AiMessage m in request.Messages)
            {
                MessageParamContent content;

                if (m.Image == null) content = m.Text;
                else
                {
                    // Image first, then the question. Not a style choice: the
                    // page is the stable part of the prefix and the question is
                    // not, and a cache breakpoint can only be placed after
                    // everything it covers.
                    var blocks = new List<ContentBlockParam>
                    {
                        new ContentBlockParam(new ImageBlockParam(new Base64ImageSource
                        {
                            Data = Convert.ToBase64String(m.Image),
                            MediaType = MediaFor(m.ImageMediaType),
                        })
                        {
                            // The whole point of sending the page once. Without
                            // this the same scan is re-read at full price on
                            // every turn of the conversation, and the only sign
                            // is an input count that never drops.
                            CacheControl = new CacheControlEphemeral(),
                        }, null),
                        new ContentBlockParam(new TextBlockParam(m.Text ?? ""), null),
                    };
                    content = blocks;
                }

                messages.Add(new MessageParam
                {
                    Role = m.Role == AiRole.User ? Role.User : Role.Assistant,
                    Content = content,
                });
            }

            var parameters = new MessageCreateParams
            {
                Model = request.Model,
                MaxTokens = request.MaxOutputTokens,
                Messages = messages,
                System = request.Instruction,

                // Adaptive: Claude decides when and how much to think, and
                // effort sets how far it may go.
                Thinking = new ThinkingConfigAdaptive(),
                OutputConfig = new OutputConfig { Effort = EffortFor(request.Thinking) },
            };

            var reply = new AiReply();
            var text = new System.Text.StringBuilder();

            await foreach (RawMessageStreamEvent e in client.Messages.CreateStreaming(parameters, cancellationToken: cancel))
            {
                if (e.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var piece))
                {
                    text.Append(piece.Text);
                    if (onText != null) onText(piece.Text);
                }
                else if (e.TryPickStart(out var start))
                {
                    reply.Usage.Model = request.Model;
                    reply.Usage.InputTokens = (int)start.Message.Usage.InputTokens;
                    reply.Usage.CachedInputTokens = (int)(start.Message.Usage.CacheReadInputTokens ?? 0);
                }
                else if (e.TryPickDelta(out var final))
                {
                    reply.Usage.OutputTokens = (int)final.Usage.OutputTokens;
                }
            }

            reply.Text = text.ToString();
            return reply;
        }
    }
}
