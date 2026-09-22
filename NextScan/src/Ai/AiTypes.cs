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

        public static AiMessage FromUser(string text) { return new AiMessage { Role = AiRole.User, Text = text }; }
        public static AiMessage FromAssistant(string text) { return new AiMessage { Role = AiRole.Assistant, Text = text }; }
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

        /// <summary>Set when the turn ended badly. Null on success.</summary>
        public string Trouble;
    }

    /// <summary>
    /// What a provider is and what it can actually do, for the settings panel.
    /// </summary>
    public class AiProviderInfo
    {
        public string Id = "";
        public string Name = "";
        public string KeyHint = "";
        public string[] Models = new string[0];

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
