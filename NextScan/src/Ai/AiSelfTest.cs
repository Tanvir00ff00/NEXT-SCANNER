// =============================================================================
// NextScan Studio - proof that the AI layer is actually reachable
// Plan ref: docs/AI_LAYER.md
//
// This exists because of a failure mode the rest of this repository cannot
// have. Everything else here compiles against the framework and runs; the AI
// layer compiles against thirty-one files that have to be found at run time, by
// a probing path and fifteen binding redirects living in a file no compiler
// ever reads. All of that can be word-perfect and still resolve nothing.
//
// So the check is not "does the config look right". It is: build each client
// and see which file the CLR actually opened. No network call is made -- a
// constructor is enough to fault in the SDK and the assemblies it binds to,
// which is the part a config file cannot demonstrate on its own.
// =============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Anthropic;
using Anthropic.Core;

namespace NextScan.Ai
{
    public static class AiSelfTest
    {
        /// <summary>
        /// Shaped like a real key so a constructor that checks the shape is
        /// satisfied, and obviously not one so it cannot be mistaken for a
        /// leak if it ever turns up in a log.
        /// </summary>
        const string StandIn = "selftest-not-a-key";

        /// <summary>
        /// Builds the provider's client and reports the file the CLR loaded its
        /// SDK from. Throws if that cannot be done.
        /// </summary>
        public static string Reach(string id)
        {
            switch (id)
            {
                case "claude":
                {
                    var client = new AnthropicClient(new ClientOptions { ApiKey = StandIn });
                    return Where(client.GetType());
                }
                case "openai":
                {
                    var client = new OpenAI.Chat.ChatClient("gpt-5", StandIn);
                    return Where(client.GetType());
                }
                case "gemini":
                {
                    var client = new Google.GenAI.Client(apiKey: StandIn);
                    return Where(client.GetType());
                }
                default:
                    throw new AiTrouble("No provider called " + id + ".");
            }
        }

        /// <summary>
        /// Every assembly now loaded from beside NextScan.Ai. Run after
        /// <see cref="Reach"/>, this is the list of things that would otherwise
        /// have failed at the first question a user asked.
        /// </summary>
        public static IList<string> Loaded()
        {
            string home = Path.GetDirectoryName(new Uri(typeof(AiSelfTest).Assembly.CodeBase).LocalPath);
            var found = new List<string>();

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.IsDynamic) continue;

                string at;
                try { at = a.Location; }
                catch (NotSupportedException) { continue; }
                if (string.IsNullOrEmpty(at)) continue;

                if (string.Equals(Path.GetDirectoryName(at), home, StringComparison.OrdinalIgnoreCase))
                    found.Add(a.GetName().Name + " " + a.GetName().Version);
            }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        /// <summary>
        /// The loaded file, and the version that was really bound -- which is
        /// the interesting half when a redirect is involved, because the number
        /// the SDK asked for and the number it got are not the same number.
        /// </summary>
        static string Where(Type t)
        {
            AssemblyName name = t.Assembly.GetName();
            return Path.GetFileName(t.Assembly.Location) + " " + name.Version;
        }
    }
}
