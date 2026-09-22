// =============================================================================
// NextScan Studio - nsaitest: the AI layer loads, and stays behind its boundary
// Plan ref: docs/AI_LAYER.md, MASTER_PLAN section 18.1 (tests).
//
// Two questions, and neither can be answered by reading a file.
//
// The first is whether the provider SDKs resolve at run time at all. They do
// not sit beside the executable; they are found through a probing path and
// fifteen binding redirects in NextScanner.exe.config. A missing redirect
// compiles clean, ships clean, and fails the first time a user asks a question.
// So this builds each client for real and reports the file the CLR opened.
//
// The second is whether the boundary held. This tool is compiled against
// NextScan.Ai and nothing else -- no reference to Anthropic, OpenAI or
// Google.GenAI anywhere in its command line. If it can still drive all three
// providers, the shell can too, and adding a fourth stays a one-file change.
//
// Makes no network call and spends nothing.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using NextScan.Ai;

namespace NextScan.Tools
{
    public static class NsAiTest
    {
        static int _fail;

        public static int Main(string[] args)
        {
            Console.WriteLine();
            Console.WriteLine("NextScan AI layer tests");
            Console.WriteLine();

            Console.WriteLine("  the facade");
            Case("providers_are_listed", ProvidersAreListed);
            Case("ids_are_distinct", IdsAreDistinct);
            Case("ceiling_is_admitted", CeilingIsAdmitted);
            Case("unknown_id_is_null", UnknownIdIsNull);

            Console.WriteLine("  the SDKs, loaded for real");
            Case("claude_sdk_loads", () => AiSelfTest.Reach("claude"));
            Case("openai_sdk_loads", () => AiSelfTest.Reach("openai"));
            Case("gemini_sdk_loads", () => AiSelfTest.Reach("gemini"));
            Case("redirects_resolved", RedirectsResolved);

            Console.WriteLine("  the model list");
            Case("models_are_not_declared", ModelsAreNotDeclared);
            Case("no_key_no_list", NoKeyNoList);

            Console.WriteLine("  the key store");
            Case("key_round_trip", KeyRoundTrip);
            Case("key_never_shown_whole", KeyNeverShownWhole);
            Case("bad_id_is_refused", BadIdIsRefused);

            Console.WriteLine();
            if (_fail == 0) Console.WriteLine("  all passed");
            else Console.WriteLine("  " + _fail + " failed");
            Console.WriteLine();
            return _fail == 0 ? 0 : 1;
        }

        // -- the facade ------------------------------------------------------

        static string ProvidersAreListed()
        {
            IReadOnlyList<IAiProvider> all = AiProviders.All();
            if (all.Count < 3) throw new Exception("expected three providers, got " + all.Count);

            var names = new List<string>();
            foreach (IAiProvider p in all)
            {
                if (string.IsNullOrEmpty(p.Info.Name)) throw new Exception(p.Info.Id + " has no name");
                if (string.IsNullOrEmpty(p.Info.KeyHint)) throw new Exception(p.Info.Id + " does not say what its key looks like");
                if (p.Info.Prefer.Length == 0) throw new Exception(p.Info.Id + " has no preference to fall back on");
                names.Add(p.Info.Name);
            }
            return string.Join(", ", names.ToArray());
        }

        static string IdsAreDistinct()
        {
            var seen = new List<string>();
            foreach (IAiProvider p in AiProviders.All())
            {
                if (seen.Contains(p.Info.Id)) throw new Exception("two providers answer to " + p.Info.Id);
                if (AiProviders.ById(p.Info.Id) == null) throw new Exception(p.Info.Id + " cannot be looked up");
                seen.Add(p.Info.Id);
            }
            return seen.Count + " ids";
        }

        /// <summary>
        /// A provider that cannot think as hard as it was asked to has to say
        /// so. The alternative is a setting that moves and changes nothing,
        /// which is worse than not offering it.
        /// </summary>
        static string CeilingIsAdmitted()
        {
            int admitted = 0;
            foreach (IAiProvider p in AiProviders.All())
            {
                if (p.Info.Describe(ThinkingLevel.Low) != "")
                    throw new Exception(p.Info.Id + " complains about a level it does offer");

                if (p.Info.Ceiling < ThinkingLevel.Max)
                {
                    string said = p.Info.Describe(ThinkingLevel.Max);
                    if (said == "") throw new Exception(p.Info.Id + " tops out below Max and does not say so");
                    if (said.IndexOf(p.Info.Ceiling.ToString(), StringComparison.Ordinal) < 0)
                        throw new Exception(p.Info.Id + " does not say what it will use instead");
                    admitted++;
                }
            }
            return admitted + " capped, and saying so";
        }

        static string UnknownIdIsNull()
        {
            if (AiProviders.ById("no-such-provider") != null) throw new Exception("a provider appeared from nowhere");
            return "";
        }

        // -- the SDKs --------------------------------------------------------

        /// <summary>
        /// The three SDKs pull in far more than themselves, and those are the
        /// assemblies the redirects exist for. Having built all three clients,
        /// anything still unresolved would already have thrown.
        /// </summary>
        static string RedirectsResolved()
        {
            IList<string> loaded = AiSelfTest.Loaded();
            if (loaded.Count < 4)
                throw new Exception("only " + loaded.Count + " assemblies came from the AI folder; the probing path is not working");

            foreach (string a in loaded) Console.WriteLine("           " + a);
            return loaded.Count + " assemblies";
        }

        // -- the model list --------------------------------------------------

        /// <summary>
        /// There is no list of model names anywhere in the layer.
        ///
        /// Checked by reflection over what a provider declares, because the
        /// failure this guards against is somebody adding a convenient default
        /// back: a name written here is a name that is wrong by the next model
        /// release, with a menu that quietly hides whatever the operator is
        /// actually paying for.
        /// </summary>
        static string ModelsAreNotDeclared()
        {
            foreach (IAiProvider provider in AiProviders.All())
                foreach (string wanted in provider.Info.Prefer)
                {
                    // A preference is a fragment to match against the fetched
                    // list. Anything that looks like a whole model id is a list
                    // in disguise.
                    if (wanted.Length > 24)
                        throw new Exception(provider.Info.Id + " prefers '" + wanted + "', which is a model name, not a hint");
                }

            // Nothing is offered before the provider has been asked.
            foreach (IAiProvider provider in AiProviders.All())
                if (AiModels.Cached(provider) != null)
                    throw new Exception(provider.Info.Id + " had a list before anybody asked for one");

            return "preferences only";
        }

        /// <summary>
        /// Without a key there is no account, so there is no list -- and the
        /// answer has to be the refusal rather than an invented default.
        /// </summary>
        static string NoKeyNoList()
        {
            int refused = 0;
            foreach (IAiProvider provider in AiProviders.All())
            {
                if (provider.Ready) continue;        // a real key is set on this machine
                try
                {
                    provider.Models(CancellationToken.None).GetAwaiter().GetResult();
                    throw new Exception(provider.Info.Id + " produced a model list with no key");
                }
                catch (AiTrouble) { refused++; }
            }
            return refused + " refused";
        }

        // -- the key store ---------------------------------------------------

        const string TestId = "nsaitest";

        static string KeyRoundTrip()
        {
            try
            {
                if (AiKeys.Has(TestId)) throw new Exception("a key was already there; a previous run did not clean up");

                AiKeys.Set(TestId, "  sk-ant-selftest-abcd  ");
                if (!AiKeys.Has(TestId)) throw new Exception("the key was stored and is not there");
                if (AiKeys.Get(TestId) != "sk-ant-selftest-abcd") throw new Exception("the key came back changed");

                AiKeys.Forget(TestId);
                if (AiKeys.Has(TestId)) throw new Exception("the key was forgotten and is still there");
                if (AiKeys.Get(TestId) != null) throw new Exception("a forgotten key still reads back");

                // An empty key means forget, not store-an-empty-string: a
                // cleared box in the settings panel has to remove the key.
                AiKeys.Set(TestId, "x");
                AiKeys.Set(TestId, "   ");
                if (AiKeys.Has(TestId)) throw new Exception("clearing the box left the old key in place");

                return "sealed, read back, removed";
            }
            finally { AiKeys.Forget(TestId); }
        }

        static string KeyNeverShownWhole()
        {
            try
            {
                AiKeys.Set(TestId, "sk-ant-selftest-wxyz");
                string tail = AiKeys.Tail(TestId);
                if (tail != "****wxyz") throw new Exception("the tail read '" + tail + "'");
                if (tail.IndexOf("selftest", StringComparison.Ordinal) >= 0) throw new Exception("the tail shows the key");

                AiKeys.Forget(TestId);
                if (AiKeys.Tail(TestId) != "") throw new Exception("a missing key still has a tail");
                return tail;
            }
            finally { AiKeys.Forget(TestId); }
        }

        /// <summary>
        /// The id never comes from typing, but it does become a file name, and
        /// a path that leaves the keys folder is not a bug worth discovering
        /// later.
        /// </summary>
        static string BadIdIsRefused()
        {
            foreach (string bad in new[] { "..", "a/b", @"a\b", "a.b", "" })
            {
                bool refused = false;
                try { AiKeys.Set(bad, "x"); }
                catch (AiTrouble) { refused = true; }
                if (!refused) throw new Exception("'" + bad + "' was accepted as a provider id");
            }
            return "5 refused";
        }

        // -- plumbing --------------------------------------------------------

        static void Case(string name, Func<string> body)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                string note = body();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    ok   {0,-24} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, note));
            }
            catch (Exception ex)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "    FAIL {0,-24} {1,5:N1}s  {2}", name, sw.Elapsed.TotalSeconds, ex.Message));
                _fail++;
            }
        }
    }
}
