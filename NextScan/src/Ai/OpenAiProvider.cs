// =============================================================================
// NextScan Studio - OpenAI
// Plan ref: docs/AI_LAYER.md
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenAI.Chat;

namespace NextScan.Ai
{
    public class OpenAiProvider : IAiProvider
    {
        public AiProviderInfo Info { get; } = new AiProviderInfo
        {
            Id = "openai",
            Name = "OpenAI",
            KeyHint = "sk-...",
            Prefer = new[] { "gpt-5", "gpt-4.1", "o4", "gpt-4o" },

            // Three levels, not four. The panel says so rather than offering a
            // Max that quietly behaves like High.
            Ceiling = ThinkingLevel.High,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

        /// <summary>
        /// Everything else this endpoint returns: embeddings, speech, images,
        /// moderation, transcription. There is no capability flag on an OpenAI
        /// model, so the only way to tell a chat model from a text-to-speech one
        /// is its name. This is a guess, and it is written down as one -- a
        /// model wrongly excluded here is a model the operator has paid for and
        /// cannot select.
        /// </summary>
        static readonly string[] NotForChat =
        {
            "embedding", "tts", "whisper", "dall-e", "audio", "realtime",
            "transcribe", "image", "moderation", "search", "computer-use",
        };

        public async Task<IList<AiModel>> Models(CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No OpenAI key has been set.");

            var client = new OpenAI.Models.OpenAIModelClient(key);
            var found = new List<AiModel>();

            OpenAI.Models.OpenAIModelCollection all =
                await client.GetModelsAsync(cancel).ConfigureAwait(false);

            foreach (OpenAI.Models.OpenAIModel model in all)
            {
                string id = model.Id ?? "";
                if (id.Length == 0) continue;

                // The chat models are the gpt- and o-series. Anything else on
                // this endpoint is a different kind of model entirely.
                bool couldChat = id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase) ||
                                 (id.Length > 1 && id[0] == 'o' && char.IsDigit(id[1]));
                if (!couldChat) continue;

                bool ruled = false;
                foreach (string no in NotForChat)
                    if (id.IndexOf(no, StringComparison.OrdinalIgnoreCase) >= 0) { ruled = true; break; }
                if (ruled) continue;

                found.Add(new AiModel { Id = id, Name = id });
            }

            found.Sort(delegate (AiModel a, AiModel b) { return string.CompareOrdinal(a.Id, b.Id); });
            return found;
        }

        static ChatReasoningEffortLevel EffortFor(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Low: return ChatReasoningEffortLevel.Low;
                case ThinkingLevel.Medium: return ChatReasoningEffortLevel.Medium;
                default: return ChatReasoningEffortLevel.High;   // Max folds into High
            }
        }

        public async Task<AiReply> Ask(AiRequest request, AiTextArrived onText, CancellationToken cancel)
        {
            string key = AiKeys.Get(Info.Id);
            if (string.IsNullOrEmpty(key)) throw new AiTrouble("No OpenAI key has been set.");

            var client = new ChatClient(request.Model, key);

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(request.Instruction))
                messages.Add(ChatMessage.CreateSystemMessage(request.Instruction));
            foreach (AiMessage m in request.Messages)
            {
                if (m.Role != AiRole.User) { messages.Add(ChatMessage.CreateAssistantMessage(m.Text)); continue; }
                if (m.Image == null) { messages.Add(ChatMessage.CreateUserMessage(m.Text)); continue; }

                messages.Add(ChatMessage.CreateUserMessage(
                    ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(m.Image), m.ImageMediaType ?? "image/jpeg", null),
                    ChatMessageContentPart.CreateTextPart(m.Text ?? "")));
            }

            var options = new ChatCompletionOptions { ReasoningEffortLevel = EffortFor(request.Thinking) };

            var reply = new AiReply();
            reply.Usage.Model = request.Model;
            var text = new System.Text.StringBuilder();

            await foreach (StreamingChatCompletionUpdate update in
                           client.CompleteChatStreamingAsync(messages, options, cancel))
            {
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    text.Append(part.Text);
                    if (onText != null) onText(part.Text);
                }

                // Usage arrives on the last update and is null on the others.
                if (update.Usage != null)
                {
                    reply.Usage.InputTokens = update.Usage.InputTokenCount;
                    reply.Usage.OutputTokens = update.Usage.OutputTokenCount;
                }
            }

            reply.Text = text.ToString();
            return reply;
        }
    }
}
