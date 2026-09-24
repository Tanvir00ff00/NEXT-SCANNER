// =============================================================================
// NextScan Studio - what the application says to a model, and hears back
// Plan ref: docs/AI_LAYER.md
//
// Nothing from a provider's SDK appears in this file, and nothing from this
// file mentions one. That is the whole point of the boundary: the shell talks
// in these types, each provider translates them, and swapping or adding a
// provider changes one class rather than every caller.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NextScan.Ai
{
    public enum AiRole { User, Assistant }

    /// <summary>
    /// How hard the model should think before answering.
    ///
    /// All three providers landed on named levels rather than token budgets, so
    /// one control drives all of them -- but they do not offer the same set,
    /// and a provider that cannot do what was asked says so through
    /// <see cref="AiProviderInfo.Describe"/> rather than quietly doing less.
    /// </summary>
    public enum ThinkingLevel { Low, Medium, High, Max }

    public class AiMessage
    {
        public AiRole Role;
        public string Text = "";

        /// <summary>A page to look at, as PNG or JPEG bytes. Null for text-only turns.</summary>
        public byte[] Image;
        public string ImageMediaType = "image/png";

        /// <summary>What the model asked the application to do, on an assistant turn.</summary>
        public List<AiToolCall> ToolCalls = new List<AiToolCall>();

        /// <summary>What those requests came to, on the user turn that follows.</summary>
        public List<AiToolResult> ToolResults = new List<AiToolResult>();

        /// <summary>
        /// The assistant turn exactly as the provider sent it, for sending
        /// back. Claude requires its thinking blocks returned unchanged before a
        /// tool result and Gemini its thought signatures; rebuilt from the text
        /// and the calls, both are refused. Only the provider named in
        /// <see cref="RawProvider"/> reads it; any other rebuilds the turn.
        /// </summary>
        public object Raw;
        public string RawProvider = "";

        public static AiMessage FromUser(string text) { return new AiMessage { Role = AiRole.User, Text = text }; }
        public static AiMessage FromAssistant(string text) { return new AiMessage { Role = AiRole.Assistant, Text = text }; }
    }

    /// <summary>
    /// Something the application can do when a model asks: a name, what it is
    /// for, and its arguments as a JSON Schema object. The same description
    /// goes to every provider; each translates it.
    /// </summary>
    public class AiTool
    {
        public string Name = "";
        public string Description = "";
        public string ParametersJson = "{\"type\":\"object\",\"properties\":{}}";
    }

    public class AiToolCall
    {
        /// <summary>The provider's id for the call, which its result must carry back.</summary>
        public string Id = "";
        public string Name = "";
        public string ArgumentsJson = "{}";
    }

    public class AiToolResult
    {
        public string CallId = "";
        public string Name = "";

        /// <summary>What the model is told. Plain text or JSON.</summary>
        public string Content = "";
        public bool IsError;

        /// <summary>What the operator is shown in the transcript. Never sent to the model.</summary>
        public string Display = "";
    }

    /// <summary>
    /// What a turn cost, as the provider counted it.
    ///
    /// Never counted here. Tokenizers differ between providers and between
    /// model generations of one provider, so a number produced locally is a
    /// number for the wrong model -- worse than showing none, because it looks
    /// authoritative.
    /// </summary>
    public class AiUsage
    {
        public int InputTokens;
        public int OutputTokens;

        /// <summary>Read from the cache rather than re-read. Zero on providers that do not report it.</summary>
        public int CachedInputTokens;

        /// <summary>Which model these were counted for. A count without this is not a count.</summary>
        public string Model = "";
    }

    public class AiReply
    {
        public string Text = "";
        public AiUsage Usage = new AiUsage();

        /// <summary>Tools the model wants run before it can finish. Empty when it has answered.</summary>
        public List<AiToolCall> ToolCalls = new List<AiToolCall>();

        /// <summary>The turn as the provider sent it; see <see cref="AiMessage.Raw"/>.</summary>
        public object Raw;
        public string RawProvider = "";

        /// <summary>Set when the turn ended badly. Null on success.</summary>
        public string Trouble;
    }

    /// <summary>
    /// One model, as the provider itself named it.
    ///
    /// Never written down here. What a provider offers changes without us, and
    /// a list in our source is a list that is wrong by the next release and
    /// silently hides whatever the operator is actually paying for.
    /// </summary>
    public class AiModel
    {
        public string Id = "";

        /// <summary>The provider's own display name, or the id where it gives none.</summary>
        public string Name = "";

        /// <summary>
        /// Whether this looks like a model for answering questions about a page.
        ///
        /// A provider's model endpoint carries far more than its chat models,
        /// and none of them says which is which in a way that can be trusted --
        /// Gemini declares generateContent on its music and image models too.
        /// So this is a guess, and it does not hide anything: it decides which
        /// rows are ticked when the operator first opens the list, and which one
        /// is chosen before they have opened it at all.
        /// </summary>
        public bool Likely = true;

        public override string ToString() { return Name.Length > 0 ? Name : Id; }
    }

    /// <summary>
    /// What a provider is and what it can actually do.
    /// </summary>
    public class AiProviderInfo
    {
        public string Id = "";
        public string Name = "";
        public string KeyHint = "";

        /// <summary>
        /// Ordered preferences, used only to choose a default out of the list
        /// the provider returned. It is not a menu: nothing here is ever
        /// offered, and a model that is not in the fetched list is not picked
        /// however high it sits here.
        /// </summary>
        public string[] Prefer = new string[0];

        /// <summary>
        /// Fragments that make a model a poor default -- the small and cheap
        /// variants, and the previews.
        ///
        /// Needed because a preference is matched as a substring, and the
        /// substring that names a family also names its cut-down members:
        /// "gemini-3" matched Gemini 3.1 Flash Lite and made it the default on
        /// a key that could reach Pro. These are still offered in the menu; they
        /// are only passed over when nothing has been chosen yet.
        /// </summary>
        public string[] Avoid = new string[0];

        /// <summary>
        /// The highest level this provider really has. Asking for Max where
        /// only High exists is not an error, but the panel says what will
        /// happen rather than showing a setting that silently does nothing.
        /// </summary>
        public ThinkingLevel Ceiling = ThinkingLevel.Max;

        public string Describe(ThinkingLevel wanted)
        {
            return wanted > Ceiling
                ? wanted + " is not offered here; " + Ceiling + " will be used"
                : "";
        }
    }

    /// <summary>One turn's worth of instruction, context and history.</summary>
    public class AiRequest
    {
        public string Model = "";
        public ThinkingLevel Thinking = ThinkingLevel.Medium;
        public int MaxOutputTokens = 4096;

        /// <summary>
        /// The standing instruction, and the first thing in the cached prefix.
        /// Nothing that changes per request belongs here -- a timestamp in this
        /// string means the cache never hits and nothing says so.
        /// </summary>
        public string Instruction = "";

        public List<AiMessage> Messages = new List<AiMessage>();

        /// <summary>What the model may ask the application to do. Empty for a plain conversation.</summary>
        public List<AiTool> Tools = new List<AiTool>();
    }

    /// <summary>
    /// Raised for trouble the operator can act on -- a missing or refused key,
    /// a model that does not exist, a provider that is down. Anything else is
    /// left as the SDK threw it.
    /// </summary>
    public class AiTrouble : Exception
    {
        public AiTrouble(string message) : base(message) { }
        public AiTrouble(string message, Exception inner) : base(message, inner) { }
    }
}
