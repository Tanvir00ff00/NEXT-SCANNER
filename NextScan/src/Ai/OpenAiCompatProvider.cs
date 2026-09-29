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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
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

            await Describe(endpoint, found, cancel).ConfigureAwait(false);
            found.Sort(delegate (AiModel a, AiModel b) { return string.CompareOrdinal(a.Id, b.Id); });
            return found;
        }

        static readonly HttpClient Raw = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>
        /// What the server says about each model beyond its name.
        ///
        /// The SDK's model list keeps only the id, the owner and the date, and
        /// throws the rest away -- but gateways send more, and it is exactly
        /// what the operator chooses by: NaraRouter marks "vision": true and
        /// gives "context_window"; OpenRouter gives "context_length",
        /// "architecture.input_modalities" and the longest answer. So the same
        /// list is read once more as plain JSON, and whatever is there is kept.
        /// Nothing here is guessed: a field the server does not send stays
        /// unknown, and any failure leaves the list as the SDK gave it.
        /// </summary>
        async Task Describe(string endpoint, List<AiModel> found, CancellationToken cancel)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, endpoint + "/models"))
                {
                    string key = AiKeys.Get(Info.Id);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", string.IsNullOrEmpty(key) ? NoKey : key);
                    using (HttpResponseMessage response = await Raw.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return;
                        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            JsonElement list = doc.RootElement, inner;
                            if (list.ValueKind == JsonValueKind.Object && list.TryGetProperty("data", out inner)) list = inner;
                            if (list.ValueKind != JsonValueKind.Array) return;

                            var byId = new Dictionary<string, AiModel>(StringComparer.Ordinal);
                            foreach (AiModel m in found) byId[m.Id] = m;
                            foreach (JsonElement e in list.EnumerateArray())
                            {
                                JsonElement v;
                                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out v) || v.ValueKind != JsonValueKind.String) continue;
                                AiModel m;
                                if (!byId.TryGetValue(v.GetString(), out m)) continue;

                                if (e.TryGetProperty("name", out v) && v.ValueKind == JsonValueKind.String && v.GetString().Length > 0) m.Name = v.GetString();
                                if (e.TryGetProperty("description", out v) && v.ValueKind == JsonValueKind.String) m.Description = FirstSentence(v.GetString());
                                long n;
                                if (Number(e, "context_window", out n) || Number(e, "context_length", out n) || Number(e, "max_context_length", out n)) m.ContextTokens = n;
                                if (Number(e, "max_output_tokens", out n) || Number(e, "max_completion_tokens", out n)) m.OutputTokens = n;
                                else if (e.TryGetProperty("top_provider", out v) && v.ValueKind == JsonValueKind.Object && Number(v, "max_completion_tokens", out n)) m.OutputTokens = n;
                                if (e.TryGetProperty("vision", out v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) m.Vision = v.GetBoolean();
                                if (e.TryGetProperty("reasoning", out v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)) m.Thinking = v.GetBoolean();
                                if (e.TryGetProperty("architecture", out v) && v.ValueKind == JsonValueKind.Object)
                                {
                                    JsonElement modalities;
                                    if (v.TryGetProperty("input_modalities", out modalities) && modalities.ValueKind == JsonValueKind.Array)
                                    {
                                        bool image = false;
                                        foreach (JsonElement x in modalities.EnumerateArray())
                                            if (x.ValueKind == JsonValueKind.String && x.GetString() == "image") image = true;
                                        m.Vision = image;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        static bool Number(JsonElement e, string name, out long value)
        {
            value = 0;
            JsonElement v;
            if (!e.TryGetProperty(name, out v) || v.ValueKind != JsonValueKind.Number) return false;
            double d;
            if (!v.TryGetDouble(out d) || d <= 0) return false;
            value = (long)d;
            return true;
        }

        static string FirstSentence(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            int dot = s.IndexOf(". ", StringComparison.Ordinal);
            if (dot > 0 && dot < 200) s = s.Substring(0, dot + 1);
            return s.Length > 200 ? s.Substring(0, 197) + "…" : s;
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
            var thought = new System.Text.StringBuilder();
            var split = new ThinkSplitter();
            Action<string> think = piece =>
            {
                if (string.IsNullOrEmpty(piece)) return;
                thought.Append(piece);
                if (request.OnThinking != null) request.OnThinking(piece);
            };
            Action<string> say = piece =>
            {
                if (string.IsNullOrEmpty(piece)) return;
                text.Append(piece);
                if (onText != null) onText(piece);
            };

            await foreach (StreamingChatCompletionUpdate update in
                           client.CompleteChatStreamingAsync(messages, options, cancel))
            {
                bool any = false;
                foreach (ChatMessageContentPart part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    any = true;
                    split.Feed(part.Text, say, think);
                }

                // Reasoning is not part of the protocol, so the SDK has no field
                // for it -- but it keeps what it does not know, and the update
                // written back out carries the server's own reasoning_content
                // (DeepSeek, Qwen, NaraRouter) or reasoning (OpenRouter).
                if (!any && update.ToolCallUpdates.Count == 0 && update.Usage == null)
                    think(Reasoning(update));

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
                    if (request.OnToolProgress != null) request.OnToolProgress(names[call.Index], args[call.Index].Length);
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

            split.Flush(say, think);
            reply.Text = text.ToString();
            reply.Thinking = thought.ToString();
            return reply;
        }

        /// <summary>The reasoning in one streamed update, or "".</summary>
        static string Reasoning(StreamingChatCompletionUpdate update)
        {
            try
            {
                string json = System.ClientModel.Primitives.ModelReaderWriter.Write(update).ToString();
                if (json.IndexOf("reasoning", StringComparison.Ordinal) < 0) return "";
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement choices, first, delta, v;
                    if (!doc.RootElement.TryGetProperty("choices", out choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return "";
                    first = choices[0];
                    if (!first.TryGetProperty("delta", out delta) || delta.ValueKind != JsonValueKind.Object) return "";
                    foreach (string name in new[] { "reasoning_content", "reasoning", "reasoning_text" })
                        if (delta.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String) return v.GetString();
                }
            }
            catch { }
            return "";
        }

        /// <summary>
        /// Takes a reply's thinking out of its text where a server sends both
        /// in the text, as "&lt;think&gt;...&lt;/think&gt;" at the start (DeepSeek
        /// R1, QwQ and others served raw). Only at the start: the same letters
        /// anywhere else are just letters.
        /// </summary>
        internal class ThinkSplitter
        {
            const string Open = "<think>", Close = "</think>";
            int _state;                     // 0 = not yet known, 1 = inside, 2 = text
            readonly System.Text.StringBuilder _held = new System.Text.StringBuilder();

            public void Feed(string piece, Action<string> say, Action<string> think)
            {
                if (_state == 2) { say(piece); return; }
                _held.Append(piece);
                string s = _held.ToString();
                if (_state == 0)
                {
                    string lead = s.TrimStart();
                    if (lead.Length < Open.Length && Open.StartsWith(lead, StringComparison.Ordinal)) return;
                    if (!lead.StartsWith(Open, StringComparison.Ordinal)) { _state = 2; _held.Length = 0; say(s); return; }
                    _state = 1;
                    s = lead.Substring(Open.Length);
                    _held.Length = 0;
                    _held.Append(s);
                }
                int end = s.IndexOf(Close, StringComparison.Ordinal);
                if (end >= 0)
                {
                    think(s.Substring(0, end));
                    _state = 2;
                    _held.Length = 0;
                    string rest = s.Substring(end + Close.Length).TrimStart('\r', '\n');
                    if (rest.Length > 0) say(rest);
                    return;
                }
                // Keep back what could be the start of the closing tag.
                int keep = Math.Min(s.Length, Close.Length - 1);
                think(s.Substring(0, s.Length - keep));
                _held.Length = 0;
                _held.Append(s.Substring(s.Length - keep));
            }

            public void Flush(Action<string> say, Action<string> think)
            {
                if (_held.Length == 0) return;
                if (_state == 1) think(_held.ToString()); else say(_held.ToString());
                _held.Length = 0;
            }
        }
    }
}