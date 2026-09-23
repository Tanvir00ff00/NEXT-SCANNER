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
            // nsaitest models <provider>
            //
            // Asks the provider for its list with the key on this machine and
            // prints it with the default marked. Listing models is not a billed
            // call on any of the three, and this is the only way to see what an
            // account actually offers -- which is the whole reason the list is
            // not written down in the source.
            if (args.Length >= 1 && args[0] == "models") return Listing(args.Length > 1 ? args[1] : "");

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
            Case("newest_is_the_default", NewestIsTheDefault);
            Case("a_date_is_not_a_version", ADateIsNotAVersion);

            Console.WriteLine("  the history");
            Case("a_chat_comes_back_whole", AChatComesBackWhole);
            Case("a_chat_is_listed", AChatIsListed);

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

        static int Listing(string id)
        {
            Console.WriteLine();
            foreach (IAiProvider provider in AiProviders.All())
            {
                if (id.Length > 0 && !string.Equals(id, provider.Info.Id, StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine(provider.Info.Name);
                if (!provider.Ready) { Console.WriteLine("  no key set"); Console.WriteLine(); continue; }

                IList<AiModel> list;
                try { list = provider.Models(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception ex) { Console.WriteLine("  " + ex.Message); Console.WriteLine(); continue; }

                string chosen = AiModels.Default(provider, list);
                foreach (AiModel model in list)
                    Console.WriteLine((model.Id == chosen ? "  > " : "    ") + model.Id +
                                      (model.Name != model.Id ? "   (" + model.Name + ")" : ""));
                Console.WriteLine("  " + list.Count + " models, default " + chosen);
                Console.WriteLine();
            }
            return 0;
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

        /// <summary>
        /// The default is the newest of the best preference, not whatever the
        /// API happened to list first.
        ///
        /// This is a real case, not an invented one. The account this was built
        /// against listed 2.5 Pro before 3.1 Pro, the first version took the
        /// first match, and 2.5 Pro answers a real request with "no longer
        /// available to new users".
        /// </summary>
        static string NewestIsTheDefault()
        {
            IAiProvider gemini = AiProviders.ById("gemini");
            var list = new List<AiModel>
            {
                new AiModel { Id = "gemini-2.5-flash-lite", Name = "Gemini 2.5 Flash Lite" },
                new AiModel { Id = "gemini-2.5-pro", Name = "Gemini 2.5 Pro" },
                new AiModel { Id = "gemini-3.1-pro-preview", Name = "Gemini 3.1 Pro" },
                new AiModel { Id = "gemini-3.1-flash", Name = "Gemini 3.1 Flash" },
            };

            string chosen = AiModels.Default(gemini, list);
            if (chosen != "gemini-3.1-pro-preview")
                throw new Exception("chose " + chosen + " over gemini-3.1-pro-preview");

            // And a list with no Pro in it falls to the newest Flash, not to a Lite.
            var noPro = new List<AiModel>
            {
                new AiModel { Id = "gemini-3.1-flash-lite" },
                new AiModel { Id = "gemini-2.5-flash" },
                new AiModel { Id = "gemini-3.1-flash" },
            };
            chosen = AiModels.Default(gemini, noPro);
            if (chosen != "gemini-3.1-flash") throw new Exception("chose " + chosen + " over gemini-3.1-flash");

            return "3.1 Pro over 2.5 Pro";
        }

        /// <summary>
        /// Every provider puts a build date in some of its ids, and a date read
        /// as a version number is twenty million versions newer than anything
        /// real.
        /// </summary>
        static string ADateIsNotAVersion()
        {
            IAiProvider claude = AiProviders.ById("claude");
            var list = new List<AiModel>
            {
                new AiModel { Id = "claude-haiku-4-5-20251001" },
                new AiModel { Id = "claude-opus-5" },
                new AiModel { Id = "claude-sonnet-4-6-20260115" },
            };

            string chosen = AiModels.Default(claude, list);
            if (chosen != "claude-opus-5")
                throw new Exception("chose " + chosen + "; a build date was read as a version");

            return "dates ignored";
        }

        // -- the history -----------------------------------------------------

        /// <summary>
        /// What goes in comes out, character for character.
        ///
        /// The format is length-prefixed rather than delimited precisely because
        /// a reply contains blank lines, dashes, and lines that look like
        /// headers of ours. So the test writes exactly those.
        /// </summary>
        static string AChatComesBackWhole()
        {
            string id = AiHistory.NewId();
            try
            {
                var said = new List<AiMessage>
                {
                    AiMessage.FromUser("এই ডকুমেন্টটা কী?"),
                    AiMessage.FromAssistant(
                        "Line one.\n\n" +
                        "user 99\n" +                        // looks like a header of ours
                        "assistant 5\n" +
                        "--- not a separator ---\n" +
                        "page=not a header\n" +
                        "  trailing spaces   "),
                    AiMessage.FromUser(""),                  // an empty turn is still a turn
                };

                AiHistory.Save(id, said, "Gemini", "Gemini 3.1 Flash", "Preview 851 x 1169 100 dpi");

                AiChat back = AiHistory.Load(id);
                if (back == null) throw new Exception("it did not read back at all");
                if (back.Messages.Count != said.Count)
                    throw new Exception("wrote " + said.Count + " turns, read " + back.Messages.Count);

                for (int i = 0; i < said.Count; i++)
                {
                    if (back.Messages[i].Role != said[i].Role) throw new Exception("turn " + i + " changed speaker");
                    if (back.Messages[i].Text != said[i].Text)
                        throw new Exception("turn " + i + " came back changed");
                }

                if (back.Page != "Preview 851 x 1169 100 dpi") throw new Exception("the page note was lost");
                if (back.Model != "Gemini 3.1 Flash") throw new Exception("the model was lost");

                // The page itself is never written. A folder of scanned identity
                // papers is not a thing to leave lying about on a shop machine.
                foreach (AiMessage message in back.Messages)
                    if (message.Image != null) throw new Exception("an image was stored");

                return said.Count + " turns, character for character";
            }
            finally { AiHistory.Forget(id); }
        }

        static string AChatIsListed()
        {
            string id = AiHistory.NewId();
            try
            {
                AiHistory.Save(id, new List<AiMessage>
                {
                    AiMessage.FromUser("what is this document?\nsecond line"),
                    AiMessage.FromAssistant("A bill."),
                }, "Gemini", "flash", "");

                foreach (AiChat chat in AiHistory.Recent(50))
                {
                    if (chat.Id != id) continue;
                    if (chat.Title != "what is this document? second line")
                        throw new Exception("the title read '" + chat.Title + "'");
                    return chat.Title;
                }
                throw new Exception("it was saved and is not in the list");
            }
            finally { AiHistory.Forget(id); }
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
