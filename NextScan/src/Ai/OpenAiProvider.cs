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
            Models = new[] { "gpt-5", "gpt-5-mini" },

            // Three levels, not four. The panel says so rather than offering a
            // Max that quietly behaves like High.
            Ceiling = ThinkingLevel.High,
        };

        public bool Ready { get { return AiKeys.Has(Info.Id); } }

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
                messages.Add(m.Role == AiRole.User
                    ? (ChatMessage)ChatMessage.CreateUserMessage(m.Text)
                    : ChatMessage.CreateAssistantMessage(m.Text));

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
