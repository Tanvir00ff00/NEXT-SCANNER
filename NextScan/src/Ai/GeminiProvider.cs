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
            Prefer = new[] { "pro", "flash" },
            // Not "preview": on Gemini that is how the newest models ship, and
            // avoiding it chose 2.5 Pro on an account that had 3.1 Pro -- which
            // then refused the request as no longer available to new users.
            Avoid = new[] { "lite", "nano", "exp", "thinking", "tts", "image", "live" },
            Ceiling = ThinkingLevel.Max,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

        /// <summary>
        /// Everything on this endpoint that is not a model for answering a
        /// question about a page.
        ///
        /// This started out as a guess-free filter on the provider's own
        /// SupportedActions, and that turned out to be wrong: on a real key,
        /// forty-two models came back and every one of them declared
        /// generateContent -- the music generator, the image models, the
        /// text-to-speech ones, the robotics one and the agent previews
        /// included. SupportedActions separates generateContent from embedding
        /// and tuning, and nothing else.
        ///
        /// So this is a guess about names, like the OpenAI one, and is written
        /// down as such. It only decides what the menu offers; nothing is hidden
        /// that the operator has typed in themselves.
        /// </summary>
        static readonly string[] NotForChat =
        {
            "image", "tts", "transcribe", "robotics", "computer-use", "banana",
        };

        /// <summary>
        /// The models that can actually answer a question about a page.
        /// </summary>
        public async Task<IList<AiModel>> Models(CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No Gemini key has been set.");

            var client = new Google.GenAI.Client(apiKey: key);
            var found = new List<AiModel>();

            var page = await client.Models.ListAsync(
                new Google.GenAI.Types.ListModelsConfig { QueryBase = true }, cancel).ConfigureAwait(false);

            await foreach (Google.GenAI.Types.Model model in page.WithCancellation(cancel))
            {
                if (model.SupportedActions == null ||
                    !model.SupportedActions.Contains("generateContent")) continue;

                // Returned as "models/gemini-3-pro". The request wants the bare
                // name, so the prefix is taken off here rather than at the two
                // places that would otherwise each have to remember.
                string id = model.Name ?? "";
                if (id.StartsWith("models/", StringComparison.Ordinal)) id = id.Substring(7);
                if (id.Length == 0) continue;

                // Gemma, Lyria, the agent previews and the research models all
                // live on this endpoint under their own names. Those are other
                // products, not other Gemini models, so they are dropped.
                if (!id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)) continue;

                // The rest are kept and marked. Marked rather than dropped
                // because the operator chooses in Settings which models the
                // panel offers, and a filter that removes a row they might have
                // wanted is a filter they cannot argue with.
                bool likely = true;
                foreach (string no in NotForChat)
                    if (id.IndexOf(no, StringComparison.OrdinalIgnoreCase) >= 0) { likely = false; break; }

                found.Add(new AiModel { Id = id, Name = model.DisplayName ?? id, Likely = likely });
            }

            return found;
        }

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
            {
                // A turn this provider produced goes back as it came, thought
                // signatures and all: Gemini refuses a function call returned
                // without the signature it was issued with.
                var raw = m.Raw as List<Google.GenAI.Types.Part>;
                if (raw != null && m.RawProvider == Info.Id)
                {
                    contents.Add(new Google.GenAI.Types.Content { Role = "model", Parts = raw });
                    continue;
                }

                var parts = new List<Google.GenAI.Types.Part>();
                foreach (AiToolResult r in m.ToolResults)
                    parts.Add(new Google.GenAI.Types.Part
                    {
                        FunctionResponse = new Google.GenAI.Types.FunctionResponse
                        {
                            Id = Real(r.CallId),
                            Name = r.Name,
                            Response = new Dictionary<string, object>
                            {
                                { r.IsError ? "error" : "result", string.IsNullOrEmpty(r.Content) ? "(nothing)" : r.Content },
                            },
                        },
                    });

                if (m.Image != null)
                    parts.Add(new Google.GenAI.Types.Part
                    {
                        InlineData = new Google.GenAI.Types.Blob
                        {
                            Data = m.Image,
                            MimeType = m.ImageMediaType ?? "image/jpeg",
                        },
                    });
                if (!string.IsNullOrEmpty(m.Text) || parts.Count == 0)
                    parts.Add(new Google.GenAI.Types.Part { Text = m.Text ?? "" });

                foreach (AiToolCall c in m.ToolCalls)
                    parts.Add(new Google.GenAI.Types.Part
                    {
                        FunctionCall = new Google.GenAI.Types.FunctionCall
                        {
                            Id = Real(c.Id),
                            Name = c.Name,
                            Args = AiJson.ToDictionary(c.ArgumentsJson),
                        },
                    });

                contents.Add(new Google.GenAI.Types.Content
                {
                    Role = m.Role == AiRole.User ? "user" : "model",
                    Parts = parts,
                });
            }

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

            if (request.Tools.Count > 0)
            {
                var declarations = new List<Google.GenAI.Types.FunctionDeclaration>();
                foreach (AiTool t in request.Tools)
                    declarations.Add(new Google.GenAI.Types.FunctionDeclaration
                    {
                        Name = t.Name,
                        Description = t.Description,
                        Parameters = GeminiSchema.From(t.ParametersJson),
                    });
                config.Tools = new List<Google.GenAI.Types.Tool>
                {
                    new Google.GenAI.Types.Tool { FunctionDeclarations = declarations },
                };
            }

            var reply = new AiReply { RawProvider = Info.Id };
            reply.Usage.Model = request.Model;
            var text = new System.Text.StringBuilder();
            var turn = new List<Google.GenAI.Types.Part>();
            int unnamed = 0;

            await foreach (var chunk in client.Models.GenerateContentStreamAsync(request.Model, contents, config))
            {
                cancel.ThrowIfCancellationRequested();

                var candidate = chunk.Candidates != null && chunk.Candidates.Count > 0 ? chunk.Candidates[0] : null;
                var chunkParts = candidate != null && candidate.Content != null ? candidate.Content.Parts : null;
                if (chunkParts != null)
                {
                    foreach (var part in chunkParts)
                    {
                        turn.Add(part);
                        if (part.FunctionCall != null)
                        {
                            reply.ToolCalls.Add(new AiToolCall
                            {
                                // Gemini gives calls an id only sometimes; the
                                // name then pairs the result with its call.
                                Id = string.IsNullOrEmpty(part.FunctionCall.Id) ? NoId + (++unnamed) : part.FunctionCall.Id,
                                Name = part.FunctionCall.Name ?? "",
                                ArgumentsJson = part.FunctionCall.Args == null ? "{}" : AiJson.Serialize(part.FunctionCall.Args),
                            });
                        }
                        else if (!string.IsNullOrEmpty(part.Text) && part.Thought != true)
                        {
                            text.Append(part.Text);
                            if (onText != null) onText(part.Text);
                        }
                    }
                }

                if (chunk.UsageMetadata != null)
                {
                    reply.Usage.InputTokens = chunk.UsageMetadata.PromptTokenCount ?? 0;
                    reply.Usage.OutputTokens = chunk.UsageMetadata.CandidatesTokenCount ?? 0;
                    reply.Usage.CachedInputTokens = chunk.UsageMetadata.CachedContentTokenCount ?? 0;
                }
            }

            reply.Raw = turn;
            reply.Text = text.ToString();
            return reply;
        }

        /// <summary>
        /// Marks an id made up here for a call Gemini sent without one. It
        /// pairs the result with its call inside the application and is never
        /// sent back: an id Gemini did not issue is one it may refuse.
        /// </summary>
        const string NoId = "nextscan-noid-";

        static string Real(string id)
        {
            return string.IsNullOrEmpty(id) || id.StartsWith(NoId, StringComparison.Ordinal) ? null : id;
        }
    }
}
