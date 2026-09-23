// =============================================================================
// NextScan Studio - past conversations
// Plan ref: docs/AI_LAYER.md
//
// One file per conversation, under the operator's own profile beside the keys
// and the presets. On disk rather than in memory because a history that empties
// when the application closes is not a history -- the question an operator asks
// it is "what did we work out about this customer's papers last week".
//
// What is NOT kept is the page. These transcripts are about scanned documents,
// and a folder of identity papers is a different thing to leave on a shop
// machine than a folder of text about them. A conversation reopened therefore
// carries what was said and not what was looked at, and the panel says so.
//
// The format is length-prefixed rather than delimited. A model's reply contains
// blank lines, dashes and anything else that would otherwise have to be escaped,
// and a separator that appears inside a message is a file that reads back as a
// different conversation.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NextScan.Ai
{
    public class AiChat
    {
        public string Id = "";
        public DateTime When;
        public string Provider = "";
        public string Model = "";

        /// <summary>The page it was about, as the panel named it. Not the page itself.</summary>
        public string Page = "";

        /// <summary>The first thing the operator asked, for the menu.</summary>
        public string Title = "";

        public List<AiMessage> Messages = new List<AiMessage>();
    }

    public static class AiHistory
    {
        const string Mark = "nextscan-chat 1";

        public static string Folder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "chats");
            }
        }

        /// <summary>
        /// An id that sorts by time and cannot collide within a second.
        /// </summary>
        public static string NewId()
        {
            return DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                   "-" + Guid.NewGuid().ToString("N").Substring(0, 4);
        }

        static string PathFor(string id)
        {
            // The id is ours, never typed, but it becomes a file name.
            foreach (char c in id)
                if (!char.IsLetterOrDigit(c) && c != '-') throw new AiTrouble("bad conversation id");

            return Path.Combine(Folder, id + ".chat");
        }

        /// <summary>
        /// Writes the conversation, replacing whatever was there.
        ///
        /// Called after every completed turn rather than when the conversation
        /// ends, because a conversation does not end -- the window closes, or
        /// the machine does, and a history written at the end is a history that
        /// is never written.
        /// </summary>
        public static void Save(string id, IList<AiMessage> messages, string provider, string model, string page)
        {
            if (string.IsNullOrEmpty(id) || messages == null || messages.Count == 0) return;

            try
            {
                Directory.CreateDirectory(Folder);

                var text = new StringBuilder();
                text.Append(Mark).Append('\n');
                text.Append("when=").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                text.Append("provider=").Append(One(provider)).Append('\n');
                text.Append("model=").Append(One(model)).Append('\n');
                text.Append("page=").Append(One(page)).Append('\n');

                foreach (AiMessage message in messages)
                {
                    string body = message.Text ?? "";
                    text.Append(message.Role == AiRole.User ? "user " : "assistant ");
                    text.Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    text.Append(body).Append('\n');
                }

                File.WriteAllText(PathFor(id), text.ToString(), Encoding.UTF8);
            }
            catch { }   // a history that cannot be written must not stop the chat
        }

        /// <summary>A header value cannot carry a newline. Nothing else is escaped.</summary>
        static string One(string value)
        {
            return (value ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// The most recent conversations, newest first, without their messages.
        /// The menu needs a title and a date and nothing else.
        /// </summary>
        public static IList<AiChat> Recent(int most)
        {
            var found = new List<AiChat>();
            try
            {
                if (!Directory.Exists(Folder)) return found;

                var files = new List<string>(Directory.GetFiles(Folder, "*.chat"));
                files.Sort(StringComparer.OrdinalIgnoreCase);
                files.Reverse();                     // the id starts with the date

                foreach (string file in files)
                {
                    AiChat chat = Read(file, false);
                    if (chat == null) continue;
                    found.Add(chat);
                    if (found.Count >= most) break;
                }
            }
            catch { }
            return found;
        }

        public static AiChat Load(string id)
        {
            try { return Read(PathFor(id), true); }
            catch { return null; }
        }

        public static void Forget(string id)
        {
            try { File.Delete(PathFor(id)); } catch { }
        }

        /// <summary>Removes every conversation. For a machine more than one person uses.</summary>
        public static int ForgetAll()
        {
            int gone = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                foreach (string file in Directory.GetFiles(Folder, "*.chat"))
                {
                    try { File.Delete(file); gone++; } catch { }
                }
            }
            catch { }
            return gone;
        }

        static AiChat Read(string file, bool withMessages)
        {
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch { return null; }

            if (!text.StartsWith(Mark, StringComparison.Ordinal)) return null;

            var chat = new AiChat { Id = Path.GetFileNameWithoutExtension(file) };
            int at = Mark.Length;
            if (at < text.Length && text[at] == '\n') at++;

            // ---- headers ----
            while (at < text.Length)
            {
                int line = text.IndexOf('\n', at);
                if (line < 0) return chat;

                string row = text.Substring(at, line - at);
                int eq = row.IndexOf('=');
                if (eq < 0) break;                   // the first message header

                string key = row.Substring(0, eq);
                string value = row.Substring(eq + 1);
                at = line + 1;

                switch (key)
                {
                    case "when":
                    {
                        DateTime when;
                        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                                              DateTimeStyles.RoundtripKind, out when)) chat.When = when;
                        break;
                    }
                    case "provider": chat.Provider = value; break;
                    case "model": chat.Model = value; break;
                    case "page": chat.Page = value; break;
                }
            }

            // ---- messages ----
            while (at < text.Length)
            {
                int line = text.IndexOf('\n', at);
                if (line < 0) break;

                string row = text.Substring(at, line - at);
                at = line + 1;

                int space = row.LastIndexOf(' ');
                if (space <= 0) break;

                int length;
                if (!int.TryParse(row.Substring(space + 1), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out length)) break;
                if (length < 0 || at + length > text.Length) break;

                string body = text.Substring(at, length);
                at += length;
                if (at < text.Length && text[at] == '\n') at++;

                bool mine = row.Substring(0, space) == "user";
                if (chat.Title.Length == 0 && mine) chat.Title = Line(body);

                if (withMessages)
                    chat.Messages.Add(mine ? AiMessage.FromUser(body) : AiMessage.FromAssistant(body));
                else if (chat.Title.Length > 0)
                    break;                           // the menu has what it needs
            }

            if (chat.Title.Length == 0) chat.Title = "(no question)";
            return chat;
        }

        /// <summary>The first line of a message, short enough for a menu row.</summary>
        static string Line(string text)
        {
            string one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (one.IndexOf("  ", StringComparison.Ordinal) >= 0) one = one.Replace("  ", " ");
            return one.Length <= 52 ? one : one.Substring(0, 51).TrimEnd() + "…";
        }
    }
}
