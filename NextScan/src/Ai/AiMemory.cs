// =============================================================================
// NextScan Studio - what the assistant remembers between conversations
// Plan ref: docs/AI_LAYER.md
//
// A conversation forgets everything when it ends; a shop does not. The name on
// the letterhead, that deposit slips are printed three to a page, that this
// customer's bills are in SutonnyMJ -- told once, these should not have to be
// told again. So the assistant keeps short facts here, when the operator asks
// it to or when something is plainly worth keeping, and every conversation
// starts knowing them.
//
// Short facts, not transcripts: one line each, a few dozen at most, each with
// an id the operator (or the assistant) forgets it by. The panel lists them and
// the operator can delete any; nothing is kept that cannot be seen.
//
// No page content ever goes here, for the same reason AiHistory keeps no scans:
// a line about a customer's papers on a shared machine is still their papers.
// The instruction to the model says so, and the list is the operator's to read.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NextScan.Ai
{
    public class AiFact
    {
        public string Id = "";
        public DateTime When;
        public string Text = "";
    }

    public static class AiMemory
    {
        /// <summary>A handful of lines is a memory; hundreds are a document nobody reads.</summary>
        public const int Most = 60;

        public static string File_
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "NextScan", "memory.txt");
            }
        }

        static readonly object Gate = new object();

        public static List<AiFact> All()
        {
            lock (Gate)
            {
                var facts = new List<AiFact>();
                try
                {
                    if (!File.Exists(File_)) return facts;
                    foreach (string line in File.ReadAllLines(File_, Encoding.UTF8))
                    {
                        string[] parts = line.Split(new[] { '\t' }, 3);
                        if (parts.Length < 3 || parts[2].Trim().Length == 0) continue;
                        DateTime when;
                        DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out when);
                        facts.Add(new AiFact { Id = parts[0], When = when, Text = parts[2] });
                    }
                }
                catch { }
                return facts;
            }
        }

        static void Write(List<AiFact> facts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_));
            var text = new StringBuilder();
            foreach (AiFact f in facts)
                text.Append(f.Id).Append('\t').Append(f.When.ToString("o", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(f.Text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ")).Append('\n');
            File.WriteAllText(File_, text.ToString(), Encoding.UTF8);
        }

        /// <summary>Keeps a fact and returns it. A fact already kept, word for word, is not kept twice.</summary>
        public static AiFact Remember(string text)
        {
            string clean = (text ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            if (clean.Length == 0) throw new AiTrouble("There is nothing to remember.");
            if (clean.Length > 400) clean = clean.Substring(0, 400);
            lock (Gate)
            {
                List<AiFact> facts = All();
                foreach (AiFact f in facts)
                    if (string.Equals(f.Text, clean, StringComparison.OrdinalIgnoreCase)) return f;
                if (facts.Count >= Most)
                    throw new AiTrouble("Memory is full (" + Most + " facts). Forget some first -- the oldest or the least useful.");
                int next = 1;
                foreach (AiFact f in facts)
                {
                    int n;
                    if (f.Id.StartsWith("m", StringComparison.Ordinal) && int.TryParse(f.Id.Substring(1), out n)) next = Math.Max(next, n + 1);
                }
                var fact = new AiFact { Id = "m" + next.ToString(CultureInfo.InvariantCulture), When = DateTime.Now, Text = clean };
                facts.Add(fact);
                Write(facts);
                return fact;
            }
        }

        /// <summary>Forgets a fact by its id. False when there was none.</summary>
        public static bool Forget(string id)
        {
            lock (Gate)
            {
                List<AiFact> facts = All();
                int gone = facts.RemoveAll(f => string.Equals(f.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (gone > 0) Write(facts);
                return gone > 0;
            }
        }

        public static void ForgetAll()
        {
            lock (Gate) { try { File.Delete(File_); } catch { } }
        }

        /// <summary>
        /// The facts as the model is told them, or "" when there are none. Part
        /// of the standing instruction, so it changes only when a fact does and
        /// the cached prefix survives every other turn.
        /// </summary>
        public static string ForInstruction()
        {
            List<AiFact> facts = All();
            if (facts.Count == 0) return "";
            var text = new StringBuilder("\n\nWhat you remember from earlier conversations with this shop (forget any that are wrong):\n");
            foreach (AiFact f in facts) text.Append("- [").Append(f.Id).Append("] ").Append(f.Text).Append('\n');
            return text.ToString();
        }
    }
}
