// =============================================================================
// NextScan Studio - the OpenAI-compatible servers the operator has added
// Plan ref: docs/AI_LAYER.md
//
// One compatible server was not enough: a shop may run Ollama on its own
// machine, use OpenRouter for the big models and a company gateway for the
// rest, all at once. Each is a provider of its own -- its own name in the
// panel, its own models, its own key -- and the operator adds and removes them.
//
// The list holds only an id and a name per server. The address and the key
// live where they always have, encrypted beside the other keys and named
// after the id (AiEndpoints, AiKeys), so a server added here is stored exactly
// the way the first one was.
//
// The first server keeps the id it had when there could only be one,
// "openaicompat", so an installation that already had one set up keeps it,
// its key, and the models chosen for it, without anything being moved.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NextScan.Ai
{
    public class AiServer
    {
        public string Id = "";
        public string Name = "";
    }

    public static class AiServers
    {
        /// <summary>The id the single compatible server had, kept for the first one.</summary>
        public const string LegacyId = "openaicompat";

        const string Prefix = "server";

        static readonly object Lock = new object();

        static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "keys", "servers.list");
            }
        }

        /// <summary>The servers, in the order they were added.</summary>
        public static List<AiServer> All()
        {
            lock (Lock)
            {
                var found = new List<AiServer>();
                try
                {
                    if (File.Exists(FilePath))
                    {
                        foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                        {
                            int tab = line.IndexOf('\t');
                            if (tab <= 0) continue;
                            string id = line.Substring(0, tab).Trim();
                            string name = line.Substring(tab + 1).Trim();
                            if (!ValidId(id) || name.Length == 0) continue;
                            if (found.Exists(s => s.Id == id)) continue;
                            found.Add(new AiServer { Id = id, Name = name });
                        }
                        return found;
                    }
                }
                catch { }

                // No list yet: an installation from before servers could be
                // added has at most the one, and it is kept if it was set up.
                if (AiEndpoints.Get(LegacyId).Length > 0 || AiKeys.Has(LegacyId))
                    found.Add(new AiServer { Id = LegacyId, Name = "OpenAI-compatible" });
                return found;
            }
        }

        /// <summary>Adds a server and returns it. Its address and key are stored by the caller.</summary>
        public static AiServer Add(string name)
        {
            lock (Lock)
            {
                List<AiServer> all = All();
                string id = LegacyId;
                bool legacyFree = !all.Exists(s => s.Id == LegacyId) && AiEndpoints.Get(LegacyId).Length == 0 && !AiKeys.Has(LegacyId);
                for (int n = 2; !legacyFree || all.Exists(s => s.Id == id); n++)
                {
                    id = Prefix + n.ToString(CultureInfo.InvariantCulture);
                    legacyFree = true;
                }
                var server = new AiServer { Id = id, Name = Unique(all, Clean(name), null) };
                all.Add(server);
                Write(all);
                return server;
            }
        }

        public static void Rename(string id, string name)
        {
            lock (Lock)
            {
                List<AiServer> all = All();
                AiServer server = all.Find(s => s.Id == id);
                if (server == null) return;
                string wanted = Clean(name);
                if (wanted.Length == 0) return;
                server.Name = Unique(all, wanted, id);
                Write(all);
            }
        }

        /// <summary>Removes a server with its address and its key.</summary>
        public static void Remove(string id)
        {
            lock (Lock)
            {
                List<AiServer> all = All();
                all.RemoveAll(s => s.Id == id);
                Write(all);
                AiEndpoints.Forget(id);
                AiKeys.Forget(id);
            }
        }

        public static bool IsServer(string providerId)
        {
            foreach (AiServer s in All()) if (s.Id == providerId) return true;
            return false;
        }

        static void Write(List<AiServer> all)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (AiServer s in all) lines.Add(s.Id + "\t" + s.Name);
                File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                throw new AiTrouble("Could not store the list of servers: " + ex.Message, ex);
            }
        }

        static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            foreach (char c in id) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }

        /// <summary>A name fit for a menu: one line, no tab, not empty, not too long.</summary>
        static string Clean(string name)
        {
            string s = (name ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (s.Length == 0) s = "Server";
            return s.Length > 40 ? s.Substring(0, 40).Trim() : s;
        }

        /// <summary>
        /// Names are how the panel tells servers apart, so two may not share
        /// one, nor take the name of a built-in provider.
        /// </summary>
        static string Unique(List<AiServer> all, string name, string except)
        {
            string[] taken = { "Claude", "OpenAI", "Gemini" };
            string candidate = name;
            for (int n = 2; ; n++)
            {
                bool clash = Array.Exists(taken, t => string.Equals(t, candidate, StringComparison.OrdinalIgnoreCase)) ||
                             all.Exists(s => s.Id != except && string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));
                if (!clash) return candidate;
                candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
