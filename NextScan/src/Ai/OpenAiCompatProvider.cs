// =============================================================================
// NextScan Studio - any OpenAI-compatible server
// Plan ref: docs/AI_LAYER.md
//
// Ollama, llama.cpp, LM Studio, vLLM, OpenRouter, Groq, Together, a company
// gateway, and the rest. One provider for all of them, because they differ in
// the address and nothing else this application uses.
//
// It is the official OpenAI SDK pointed somewhere else, not a hand-written
// HTTP client. OpenAIClientOptions.Endpoint is the supported way to do exactly
// this, and reusing the SDK is what makes streaming, the tool-call reassembly
// and the retry policy behave the way they do against OpenAI itself. A
// hand-rolled client would have to re-derive all three, and would be the thing
// that breaks when a server changes a detail.
// =============================================================================
using System;
using System.Collections.Generic;
using System.ClientModel;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;

namespace NextScan.Ai
{
    public class OpenAiCompatProvider : IAiProvider
    {
        /// <summary>The first server's id, for callers from when there was only one.</summary>
        public OpenAiCompatProvider() : this(AiServers.LegacyId, "OpenAI-compatible") { }

        /// <summary>
        /// One server the operator added (AiServers). Its id names its key and
        /// address files, so two servers never read each other's.
        /// </summary>
        public OpenAiCompatProvider(string id, string name)
        {
            Info = MakeInfo(id, name);
        }

        public AiProviderInfo Info { get; private set; }

        static AiProviderInfo MakeInfo(string id, string name) => new AiProviderInfo
        {
            Id = id,
            Name = name,
            KeyHint = "the server's key, or blank if it wants none",
            // No preferences and nothing avoided. A self-hosted list has no
            // house naming scheme, so any guess about which of a stranger's
            // model names is the good one is a guess about the wrong machine.
            // The operator ticks what they want, and Default falls back to the
            // newest of whatever is left.
            Prefer = new string[0],
            Avoid = new string[0],
            PrefersNothing = true,

            // Asked for, not assumed. A server may take the parameter, ignore
            // it, or refuse the request, and the panel reports what the server
            // did rather than promising a capability.
            Ceiling = ThinkingLevel.Max,
        };

        /// <summary>
        /// True once there is an address to send to.
        ///
        /// This is what decides Ready, not the key: llama.cpp and LM Studio
        /// want no key at all, and requiring one for a provider whose whole
        /// point is running your own model is backwards.
        /// </summary>
        public bool Ready { get { return BaseUrl.Length > 0; } }

        /// <summary>The address to send to, or "" when the operator has not set one.</summary>
        public string BaseUrl { get { return Normalise(AiEndpoints.Get(Info.Id)); } }

        /// <summary>
        /// Turns what the operator typed into something the SDK accepts.
        ///
        /// Every case here is one they get wrong on a first attempt, and the
        /// error message does not help: a missing scheme, a missing /v1, a
        /// trailing slash the SDK appends a path to, a host name that is not a
        /// URL at all. Fixed rather than reported, because for each one the
        /// right answer is already known here.
        /// </summary>
        public static string Normalise(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            string url = raw.Trim();

            // A bare host, or host:port: the most common thing to type, and
            // what every one of these servers prints on its own console.
            if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "http://" + url;

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return "";
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return "";

            string path = parsed.AbsolutePath.TrimEnd('/');

            // The SDK appends its own /v1/... to whatever it is given, so a
            // base already ending in /v1 is right and one not ending in it is
            // the near-miss. Appended rather than rejected: the operator typed
            // an address, not a statement about our paths, and a server
            // mounted somewhere other than /v1 is a real thing.
            if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";

            var built = new UriBuilder(parsed) { Path = path, Query = "", Fragment = "" };
            return built.Uri.ToString().TrimEnd('/');
        }

        /// <summary>True when the operator has typed something we can send to.</summary>
        public static bool AddressLooksRight(string raw) { return Normalise(raw).Length > 0; }

        static OpenAIClientOptions OptionsFor(string endpoint)
        {
            return new OpenAIClientOptions { Endpoint = new Uri(endpoint, UriKind.Absolute) };
        }

        /// <summary>
        /// What kind of reply this is, for the history: every server speaks the
        /// same protocol, so replies are marked with the kind and not the server.
        /// The key and the address are the per-server part, read by Info.Id.
        /// </summary>
        internal const string Kind = "openaicompat";

        /// <summary>
        /// A credential, or an empty one for a server that wants none.
        ///
        /// The SDK has no "no authentication" constructor, and it refuses an
        /// empty key outright ("Value cannot be an empty string"), so a server
        /// with no key was unusable. Ollama, LM Studio and llama.cpp ignore the
        /// header unless told to check it, so a placeholder goes instead --
        /// the same thing their own documentation tells OpenAI clients to do.
        /// </summary>
        ApiKeyCredential Credential()
        {
            string key = AiKeys.Get(Info.Id);
            return new ApiKeyCredential(string.IsNullOrEmpty(key) ? NoKey : key);
        }

        /// <summary>Sent as the key to a server that wants none.</summary>
        const string NoKey = "no-key";

        /// <summary>The models this server offers, asked of the server.</summary>
        public async Task<IList<AiModel>> Models(CancellationToken cancel)
        {
            var found = new List<AiModel>();
            string endpoint = BaseUrl;
            if (endpoint.Length == 0)
                throw new AiTrouble("No address has been set for " + Info.Name + ".");

            try
            {
                var client = new OpenAIModelClient(Credential(), OptionsFor(endpoint));
                OpenAIModelCollection all = await client.GetModelsAsync(cancel).ConfigureAwait(false);

                foreach (OpenAIModel model in all)
                {
                    string id = model.Id;
                    if (string.IsNullOrEmpty(id)) continue;

                    // Nothing is filtered out. The OpenAI provider drops
                    // everything that is not a gpt- or o- name, which is
                    // right there and wrong here: a self-hosted list is
                    // whatever the operator loaded, and a row removed by our
                    // guess is a row they cannot argue with.
                    var m = new AiModel { Id = id, Name = id, Likely = true, Owner = model.OwnedBy ?? "" };

                    // Servers that do not know when a model was made send 0,
                    // which is 1970 and not a date anybody should be shown.
                    try { if (model.CreatedAt.Year > 2000) m.Released = model.CreatedAt.UtcDateTime; } catch { }
                    found.Add(m);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Not a broken provider: plenty of these servers implement
                // only /v1/chat/completions and answer /v1/models with a 404.
                // Saying so is the difference between "this does not work"
                // and "type the model name in", which is all they need.
                throw new AiTrouble(
                    "The server did not return a model list (" + ex.Message + "). " +
                    "You can still type the model names in Settings.", ex);
            }

            found.Sort(delegate (AiModel a, AiModel b) { return string.CompareOrdinal(a.Id, b.Id); });
            return found;
        }

        static ChatReasoningEffortLevel EffortFor(ThinkingLevel level)
        {
            switch (level)
            {
                case ThinkingLevel.Low: return ChatReasoningEffortLevel.Low;
                case ThinkingLevel.High: return ChatReasoningEffortLevel.High;
                default: return ChatReasoningEffortLevel.Medium;
            }
        }

        /// <summary>Sends a turn and streams the reply, exactly as OpenAI's own.</summary>
        public async Task<AiReply> Ask(AiRequest request, AiTextArrived onText, CancellationToken cancel)
        {
            string endpoint = BaseUrl;
            if (endpoint.Length == 0)
                throw new AiTrouble("No address has been set for " + Info.Name + ".");

            var client = new ChatClient(request.Model, Credential(), OptionsFor(endpoint));

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(request.Instruction))
                messages.Add(ChatMessage.CreateSystemMessage(request.Instruction));

            foreach (AiMessage m in request.Messages)
            {
                if (m.ToolResults.Count > 0)
                {
                    // One message per result, which is the shape this API
                    // expects, each carrying back the id of its own call.
                    foreach (AiToolResult r in m.ToolResults)
                    {
                        messages.Add(ChatMessage.CreateToolMessage(
                            r.CallId,
                            new ChatMessageContentPart[] {
                                ChatMessageContentPart.CreateTextPart(
                                    r.IsError ? "Error: " + (r.Content ?? "") : (r.Content ?? "")) }));
                    }
                    continue;
                }

                if (m.Role == AiRole.Assistant)
                {
                    if (m.ToolCalls.Count > 0)
                    {
                        var calls = new List<ChatToolCall>();
                        foreach (AiToolCall c in m.ToolCalls)
                            calls.Add(ChatToolCall.CreateFunctionToolCall(c.Id, c.Name,
                                BinaryData.FromString(string.IsNullOrEmpty(c.ArgumentsJson) ? "{}" : c.ArgumentsJson)));
                        var assistant = new AssistantChatMessage(calls);
                        if (!string.IsNullOrEmpty(m.Text))
                            assistant.Content.Add(ChatMessageContentPart.CreateTextPart(m.Text));
                        messages.Add(assistant);
                        continue;
                    }
                    messages.Add(ChatMessage.CreateAssistantMessage(m.Text ?? ""));
                    continue;
                }

                if (m.Image == null) { messages.Add(ChatMessage.CreateUserMessage(m.Text ?? "")); continue; }

                // Sent as the API specifies. Many of these servers accept
                // images and many do not; a server that cannot says so with an
                // error naming the field, which is a better answer than
                // quietly sending text and pretending the page was read.
                messages.Add(ChatMessage.CreateUserMessage(
                    ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(m.Image), m.ImageMediaType ?? "image/jpeg", null),
                    ChatMessageContentPart.CreateTextPart(m.Text ?? "")));
            }

            var options = new ChatCompletionOptions { ReasoningEffortLevel = EffortFor(request.Thinking) };
            foreach (AiTool t in request.Tools)
                options.Tools.Add(ChatTool.CreateFunctionTool(
                    t.Name, t.Description, BinaryData.FromString(t.ParametersJson), null));

            return await Stream(client, request, messages, options, onText, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// The streaming half, kept apart so Ask reads as one thing.
        ///
        /// Tool calls arrive keyed by their index in the turn -- the id and the
        /// name come once, the arguments a fragment at a time -- so the three
        /// have to be reassembled by index before the call means anything.
        /// </summary>
        static async Task<AiReply> Stream(ChatClient client, AiRequest request,
                                         List<ChatMessage> messages, ChatCompletionOptions options,
                                         AiTextArrived onText, CancellationToken cancel)
        {
            var reply = new AiReply { RawProvider = Kind };
            reply.Usage.Model = request.Model;
            var text = new System.Text.StringBuilder();

            var ids = new SortedDictionary<int, string>();
            var names = new Dictionary<int, string>();
            var args = new Dictionary<int, System.Text.StringBuilder>();

            await foreach (StreamingChatCompletionUpdate update in
                           client.CompleteChatStreamingAsync(messages, options, cancel))
            {
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    text.Append(part.Text);
                    if (onText != null) onText(part.Text);
                }

                foreach (StreamingChatToolCallUpdate call in update.ToolCallUpdates)
                {
                    if (!ids.ContainsKey(call.Index))
                    {
                        ids[call.Index] = ""; names[call.Index] = "";
                        args[call.Index] = new System.Text.StringBuilder();
                    }
                    if (!string.IsNullOrEmpty(call.ToolCallId)) ids[call.Index] = call.ToolCallId;
                    if (!string.IsNullOrEmpty(call.FunctionName)) names[call.Index] = call.FunctionName;
                    if (call.FunctionArgumentsUpdate != null) args[call.Index].Append(call.FunctionArgumentsUpdate.ToString());
                }

                // Usage arrives on the last update and is null on the others.
                if (update.Usage != null)
                {
                    reply.Usage.InputTokens = update.Usage.InputTokenCount;
                    reply.Usage.OutputTokens = update.Usage.OutputTokenCount;
                }
            }

            foreach (var kv in ids)
                reply.ToolCalls.Add(new AiToolCall
                {
                    Id = kv.Value,
                    Name = names[kv.Key],
                    ArgumentsJson = args[kv.Key].Length > 0 ? args[kv.Key].ToString() : "{}",
                });

            reply.Text = text.ToString();
            return reply;
        }
    }
}