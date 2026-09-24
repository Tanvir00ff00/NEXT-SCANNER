// =============================================================================
// NextScan Studio - Claude
// Plan ref: docs/AI_LAYER.md
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

namespace NextScan.Ai
{
    public class ClaudeProvider : IAiProvider
    {
        public AiProviderInfo Info { get; } = new AiProviderInfo
        {
            Id = "claude",
            Name = "Claude",
            KeyHint = "sk-ant-...",
            Prefer = new[] { "opus", "sonnet", "haiku" },
            Avoid = new[] { "latest" },
            Ceiling = ThinkingLevel.Max,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

        /// <summary>
        /// Everything /v1/models returns. No filtering: this endpoint lists the
        /// message models and nothing else, so anything dropped here would be a
        /// model the account has and the panel does not offer.
        /// </summary>
        public async Task<IList<AiModel>> Models(CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No Claude key has been set.");

            var client = new AnthropicClient(new ClientOptions { ApiKey = key });
            var found = new List<AiModel>();

            ModelListPage page = await client.Models.List(new ModelListParams(), cancellationToken: cancel)
                                                    .ConfigureAwait(false);
            foreach (ModelInfo model in page.Items)
                found.Add(new AiModel { Id = model.ID, Name = model.DisplayName ?? model.ID });

            return found;
        }

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
                messages.Add(new MessageParam
                {
                    Role = m.Role == AiRole.User ? Role.User : Role.Assistant,
                    Content = ContentFor(m),
                });

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

            // Tools go over as the JSON the API documents, built once here:
            // {name, description, input_schema}. The SDK takes a tool as raw
            // JSON, which keeps the one description every provider shares.
            if (request.Tools.Count > 0)
            {
                var tools = new List<ToolUnion>();
                foreach (AiTool t in request.Tools)
                {
                    string json = AiJson.Object(w =>
                    {
                        w.WriteString("name", t.Name);
                        w.WriteString("description", t.Description);
                        w.WritePropertyName("input_schema");
                        AiJson.WriteRaw(w, t.ParametersJson);
                    });
                    tools.Add(new ToolUnion(AiJson.Parse(json)));
                }
                parameters = parameters with { Tools = tools };
            }

            var reply = new AiReply { RawProvider = Info.Id };
            var text = new StringBuilder();
            var blocks = new SortedDictionary<long, Block>();

            await foreach (RawMessageStreamEvent e in client.Messages.CreateStreaming(parameters, cancellationToken: cancel))
            {
                if (e.TryPickContentBlockStart(out var begun))
                {
                    blocks[begun.Index] = Block.From(begun.ContentBlock.Json);
                }
                else if (e.TryPickContentBlockDelta(out var grew))
                {
                    Block block;
                    if (!blocks.TryGetValue(grew.Index, out block)) continue;

                    if (grew.Delta.TryPickText(out var piece))
                    {
                        block.Text.Append(piece.Text);
                        text.Append(piece.Text);
                        if (onText != null) onText(piece.Text);
                    }
                    else if (grew.Delta.TryPickInputJson(out var input)) block.Input.Append(input.PartialJson);
                    else if (grew.Delta.TryPickThinking(out var thought)) block.Thinking.Append(thought.Thinking);
                    else if (grew.Delta.TryPickSignature(out var signed)) block.Signature.Append(signed.Signature);
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

            // The turn as sent, block for block, for the next request to carry
            // back: thinking with its signature, text, and each tool call.
            var raw = new List<string>();
            foreach (Block block in blocks.Values)
            {
                raw.Add(block.ToJson());
                if (block.Type == "tool_use")
                    reply.ToolCalls.Add(new AiToolCall
                    {
                        Id = block.Id,
                        Name = block.Name,
                        ArgumentsJson = block.Input.Length > 0 ? block.Input.ToString() : "{}",
                    });
            }

            reply.Raw = raw;
            reply.Text = text.ToString();
            return reply;
        }

        /// <summary>
        /// One message, as Claude takes it. An assistant turn this provider
        /// produced goes back exactly as it came; anything else is built from
        /// the neutral fields.
        /// </summary>
        MessageParamContent ContentFor(AiMessage m)
        {
            var blocks = new List<ContentBlockParam>();

            var raw = m.Raw as List<string>;
            if (raw != null && m.RawProvider == Info.Id)
            {
                foreach (string json in raw) blocks.Add(new ContentBlockParam(AiJson.Parse(json)));
                return blocks;
            }

            // Results first: Claude requires the tool_result blocks to lead the
            // turn that answers the calls.
            foreach (AiToolResult r in m.ToolResults)
                blocks.Add(new ContentBlockParam(AiJson.Parse(AiJson.Object(w =>
                {
                    w.WriteString("type", "tool_result");
                    w.WriteString("tool_use_id", r.CallId);
                    w.WriteString("content", string.IsNullOrEmpty(r.Content) ? "(nothing)" : r.Content);
                    if (r.IsError) w.WriteBoolean("is_error", true);
                }))));

            if (m.Image != null)
            {
                // Image first, then the question. Not a style choice: the page
                // is the stable part of the prefix and the question is not, and
                // a cache breakpoint can only be placed after everything it
                // covers.
                blocks.Add(new ContentBlockParam(new ImageBlockParam(new Base64ImageSource
                {
                    Data = Convert.ToBase64String(m.Image),
                    MediaType = MediaFor(m.ImageMediaType),
                })
                {
                    // The whole point of sending the page once. Without this
                    // the same scan is re-read at full price on every turn of
                    // the conversation, and the only sign is an input count
                    // that never drops.
                    CacheControl = new CacheControlEphemeral(),
                }, null));
            }

            if (!string.IsNullOrEmpty(m.Text))
                blocks.Add(new ContentBlockParam(new TextBlockParam(m.Text), null));

            foreach (AiToolCall c in m.ToolCalls)
                blocks.Add(new ContentBlockParam(AiJson.Parse(AiJson.Object(w =>
                {
                    w.WriteString("type", "tool_use");
                    w.WriteString("id", c.Id);
                    w.WriteString("name", c.Name);
                    w.WritePropertyName("input");
                    AiJson.WriteRaw(w, string.IsNullOrEmpty(c.ArgumentsJson) ? "{}" : c.ArgumentsJson);
                }))));

            if (blocks.Count == 0) return m.Text ?? "";
            return blocks;
        }

        /// <summary>One content block as it streams in, put back together at the end.</summary>
        class Block
        {
            public string Type = "";
            public string Id = "";
            public string Name = "";
            public string Start = "";
            public readonly StringBuilder Text = new StringBuilder();
            public readonly StringBuilder Input = new StringBuilder();
            public readonly StringBuilder Thinking = new StringBuilder();
            public readonly StringBuilder Signature = new StringBuilder();

            public static Block From(JsonElement start)
            {
                var b = new Block { Start = start.GetRawText() };
                JsonElement v;
                if (start.TryGetProperty("type", out v)) b.Type = v.GetString() ?? "";
                if (start.TryGetProperty("id", out v)) b.Id = v.GetString() ?? "";
                if (start.TryGetProperty("name", out v)) b.Name = v.GetString() ?? "";
                if (start.TryGetProperty("text", out v) && v.ValueKind == JsonValueKind.String) b.Text.Append(v.GetString());
                if (start.TryGetProperty("thinking", out v) && v.ValueKind == JsonValueKind.String) b.Thinking.Append(v.GetString());
                if (start.TryGetProperty("signature", out v) && v.ValueKind == JsonValueKind.String) b.Signature.Append(v.GetString());
                return b;
            }

            public string ToJson()
            {
                switch (Type)
                {
                    case "text":
                        return AiJson.Object(w => { w.WriteString("type", "text"); w.WriteString("text", Text.ToString()); });
                    case "thinking":
                        return AiJson.Object(w =>
                        {
                            w.WriteString("type", "thinking");
                            w.WriteString("thinking", Thinking.ToString());
                            w.WriteString("signature", Signature.ToString());
                        });
                    case "tool_use":
                        return AiJson.Object(w =>
                        {
                            w.WriteString("type", "tool_use");
                            w.WriteString("id", Id);
                            w.WriteString("name", Name);
                            w.WritePropertyName("input");
                            AiJson.WriteRaw(w, Input.Length > 0 ? Input.ToString() : "{}");
                        });
                    default:
                        // redacted_thinking and anything newer arrive whole at
                        // the start and go back as they came.
                        return Start;
                }
            }
        }
    }
}
