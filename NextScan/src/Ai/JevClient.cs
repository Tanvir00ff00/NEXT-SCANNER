// =============================================================================
// NextScan Studio - Jev, TypeSafe's "System One" model
// Plan ref: docs/AI_LAYER.md
//
// Jev is not a chat model and is not added as one. It does not write text: it
// is given some text (the "state") and typed questions about it -- yes or no
// ("noul"), one of a set of options ("choice"), a place on a scale ("score")
// -- and answers each with probabilities and a confidence, fast and cheaply.
// Text only: it cannot look at a picture.
//
// So it is used for what it is for. The assistant can hand it questions about
// a document's text (StudioDocTools, jev_ask), and saving can ask it what kind
// of document a scan is, once another model has read the page (JevSorter).
//
// TypeSafe publishes Python and JavaScript SDKs and no .NET one, so this is the
// documented HTTP API, and nothing else (docs.typesafe.ai/api):
//
//   POST https://api.typesafe.ai/v1/systemone   Authorization: Bearer <key>
//     { "model": "jev-latest", "state": <text | object | array>,
//       "questions": { "<id>": { "type": "choice", "instructions": "...",
//                                "criteria": { "<option>": "<description>" } } } }
//   -> { "model": "...", "answers": { "<id>": { "type": "choice", "choice": "...",
//          "probabilities": {...}, "confidence": 0.92 } }, "usage": {...} }
//
//   GET https://api.typesafe.ai/v1/models     lists the models; costs nothing,
//                                             so it is what a key is checked with.
//
// Jev can also be reached through a gateway the operator already uses: an
// OpenAI-compatible server (AiServers) that passes the same /systemone call on
// under its own address, key and model name -- NaraRouter offers it as "jev".
// Such a server cannot serve Jev as a chat model (it refuses a chat request
// for it), so it is the /systemone call that is routed, not a chat.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NextScan.Ai
{
    /// <summary>One answer from Jev to a choice question, as the sorter uses it.</summary>
    public class JevChoice
    {
        public string Choice = "";
        public double Confidence;
        public string Model = "";
    }

    public static class JevClient
    {
        /// <summary>The name its key is stored under (AiKeys).</summary>
        public const string KeyId = "jev";

        public const string Name = "Jev";

        /// <summary>TypeSafe's own name for the newest Jev.</summary>
        public const string OfficialModel = "jev-latest";

        // ---- where Jev is reached ------------------------------------------

        /// <summary>A server's provider id (AiServers) to reach Jev through, or "" for TypeSafe's own API.</summary>
        public static string Via { get; private set; } = "";

        static string _viaModel = "";

        /// <summary>Chooses the route: TypeSafe's API (via = ""), or a server and the model name it gives Jev.</summary>
        public static void Route(string via, string model)
        {
            Via = (via ?? "").Trim();
            _viaModel = (model ?? "").Trim();
        }

        /// <summary>The model name sent: TypeSafe's, or the one the server lists.</summary>
        public static string Model { get { return Via.Length == 0 ? OfficialModel : (_viaModel.Length > 0 ? _viaModel : "jev"); } }

        /// <summary>Who answers, for the operator: "TypeSafe", or the server's name.</summary>
        public static string Where
        {
            get
            {
                if (Via.Length == 0) return "TypeSafe";
                AiServer server = AiServers.All().Find(s => s.Id == Via);
                return server != null ? server.Name : Via;
            }
        }
        /// <summary>
        /// The address calls go to: the chosen server's, or TypeSafe's.
        /// NEXTSCAN_JEV_BASE may point TypeSafe's elsewhere for tests, as
        /// NEXTSCAN_DOCS does for the editors.
        /// </summary>
        static string Base
        {
            get
            {
                if (Via.Length > 0)
                {
                    string server = OpenAiCompatProvider.Normalise(AiEndpoints.Get(Via));
                    if (server.Length == 0) throw new AiTrouble("The server chosen for Jev has no address any more.");
                    return server + "/";
                }
                string forced = Environment.GetEnvironmentVariable("NEXTSCAN_JEV_BASE");
                return string.IsNullOrEmpty(forced) ? "https://api.typesafe.ai/v1/" : forced.TrimEnd('/') + "/";
            }
        }

        /// <summary>Where a key is made.</summary>
        public const string KeyPage = "https://console.typesafe.ai";

        /// <summary>One client for the process: HttpClient is meant to be kept, not made per call.</summary>
        static readonly HttpClient Http = MakeClient();

        static HttpClient MakeClient()
        {
            AiNet.ModernTls();
            return new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        }

        /// <summary>
        /// True when Jev can be asked: TypeSafe's key is saved, or the chosen
        /// server is still there with an address.
        /// </summary>
        public static bool Ready
        {
            get
            {
                if (Via.Length == 0) return AiKeys.Has(KeyId);
                return AiServers.IsServer(Via) && AiEndpoints.Get(Via).Length > 0;
            }
        }

        /// <summary>
        /// Checks that Jev answers on the chosen route and says how. TypeSafe:
        /// its model list, which costs nothing. A server: one tiny question,
        /// because a server's model list says a name is there, not that the
        /// /systemone call reaches it.
        /// </summary>
        public static async Task<string> Check(CancellationToken cancel)
        {
            if (Via.Length == 0)
            {
                List<string> names = await Models(cancel).ConfigureAwait(false);
                return names.Count > 0 ? "Models: " + string.Join(", ", names.ToArray()) : "Connected.";
            }
            string json = await Ask("\"Hello, this is a test.\"",
                "{\"t\":{\"type\":\"noul\",\"instructions\":\"Is this text a greeting?\"}}", cancel).ConfigureAwait(false);
            string model = "";
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement m;
                    if (doc.RootElement.TryGetProperty("model", out m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
                    if (!doc.RootElement.TryGetProperty("answers", out m)) throw new AiTrouble("It answered, but not as Jev does: " + Brief(json));
                }
            }
            catch (JsonException) { throw new AiTrouble("It answered, but not as Jev does: " + Brief(json)); }
            return "Jev answers through " + Where + (model.Length > 0 ? " (model " + model + ")" : "") + ".";
        }

        /// <summary>
        /// Checks the key by listing the models, which costs nothing, and
        /// returns their names.
        /// </summary>
        public static async Task<List<string>> Models(CancellationToken cancel)
        {
            string json = await Send(HttpMethod.Get, "models", null, cancel).ConfigureAwait(false);
            var names = new List<string>();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                // Documented as a list of entries; read either a bare array or
                // one wrapped in "data"/"models", whichever the server sends.
                JsonElement list = doc.RootElement;
                if (list.ValueKind == JsonValueKind.Object)
                {
                    JsonElement inner;
                    if (list.TryGetProperty("data", out inner) || list.TryGetProperty("models", out inner)) list = inner;
                }
                if (list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement e in list.EnumerateArray())
                    {
                        JsonElement n;
                        if (e.ValueKind == JsonValueKind.Object && (e.TryGetProperty("name", out n) || e.TryGetProperty("id", out n)))
                            names.Add(n.GetString() ?? "");
                        else if (e.ValueKind == JsonValueKind.String) names.Add(e.GetString());
                    }
            }
            return names;
        }

        /// <summary>
        /// Asks questions about a state, both given as JSON exactly as the API
        /// takes them, and returns the whole answer as JSON. This is what the
        /// assistant's tool uses: it writes the questions, and reads the answer.
        /// </summary>
        public static Task<string> Ask(string stateJson, string questionsJson, CancellationToken cancel)
        {
            string body = AiJson.Object(w =>
            {
                w.WriteString("model", Model);
                w.WritePropertyName("state");
                AiJson.WriteRaw(w, stateJson);
                w.WritePropertyName("questions");
                AiJson.WriteRaw(w, questionsJson);
            });
            return Send(HttpMethod.Post, "systemone", body, cancel);
        }

        /// <summary>
        /// Which of several kinds a text is: one choice question, with each
        /// kind described in words. Used to sort scanned documents.
        /// </summary>
        public static async Task<JevChoice> Choose(string text, string instructions,
                                                  IDictionary<string, string> kinds, CancellationToken cancel)
        {
            string state = JsonSerializer.Serialize(text ?? "");
            string questions = AiJson.Object(w =>
            {
                w.WriteStartObject("kind");
                w.WriteString("type", "choice");
                w.WriteString("instructions", instructions);
                w.WriteStartObject("criteria");
                foreach (KeyValuePair<string, string> k in kinds) w.WriteString(k.Key, k.Value);
                w.WriteEndObject();
                w.WriteEndObject();
            });

            string json = await Ask(state, questions, cancel).ConfigureAwait(false);
            var result = new JevChoice();
            using (JsonDocument doc = JsonDocument.Parse(json))
            {
                JsonElement root = doc.RootElement, e;
                if (root.TryGetProperty("model", out e) && e.ValueKind == JsonValueKind.String) result.Model = e.GetString();
                if (root.TryGetProperty("answers", out e) && e.TryGetProperty("kind", out e))
                {
                    JsonElement v;
                    if (e.TryGetProperty("choice", out v) && v.ValueKind == JsonValueKind.String) result.Choice = v.GetString();
                    if (e.TryGetProperty("confidence", out v) && v.ValueKind == JsonValueKind.Number) result.Confidence = v.GetDouble();
                }
            }
            if (result.Choice.Length == 0) throw new AiTrouble("Jev gave no answer.");
            return result;
        }

        static async Task<string> Send(HttpMethod method, string path, string body, CancellationToken cancel)
        {
            // TypeSafe needs its key; a server takes its own, or none.
            string key = Via.Length == 0 ? AiKeys.Get(KeyId) : AiKeys.Get(Via);
            if (Via.Length == 0 && string.IsNullOrEmpty(key)) throw new AiTrouble("No Jev key has been set.");
            if (string.IsNullOrEmpty(key)) key = "no-key";

            using (var request = new HttpRequestMessage(method, Base + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try { response = await Http.SendAsync(request, cancel).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw new AiTrouble("Could not reach " + Where + ": " + ex.GetBaseException().Message, ex); }

                using (response)
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode) return text;

                    // The documented codes, said the way the operator can act on.
                    string who = Where;
                    string why;
                    switch ((int)response.StatusCode)
                    {
                        case 401: why = who + " does not accept this key."; break;
                        case 404: why = who + " has no Jev at this address" + (Via.Length > 0 ? " (it may not pass Jev's /systemone call on)." : "."); break;
                        case 422: why = who + " refused the request: " + Brief(text); break;
                        case 429: why = "Too many requests to Jev just now. Try again in a moment."; break;
                        case 529: why = "Jev is overloaded just now. Try again in a moment."; break;
                        default: why = who + " answered " + (int)response.StatusCode + ": " + Brief(text); break;
                    }
                    throw new AiTrouble(why);
                }
            }
        }

        static string Brief(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 200 ? s.Substring(0, 197) + "…" : s;
        }
    }
}
