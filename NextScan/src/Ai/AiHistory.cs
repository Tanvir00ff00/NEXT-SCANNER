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
// Version 2 keeps what the transcript showed as well as what was said: the
// thinking before an answer (and how long it took) and each step taken, so a
// conversation reopened looks the way it did. Only what was said goes back to
// the model; the rest is for the operator's eyes.
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
    /// <summary>One thing the transcript showed.</summary>
    public class AiChatItem
    {
        /// <summary>user, assistant, thought, step, failed (a step that failed), note.</summary>
        public string Kind = "";
        public string Text = "";

        /// <summary>How long a thought took, in seconds. 0 otherwise.</summary>
        public double Seconds;

        public bool Said { get { return Kind == "user" || Kind == "assistant"; } }
    }

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

        /// <summary>How many turns the operator took.</summary>
        public int Turns;

        /// <summary>What was said, for the model: the user and assistant turns.</summary>
        public List<AiMessage> Messages = new List<AiMessage>();

        /// <summary>Everything the transcript showed, in order.</summary>
        public List<AiChatItem> Items = new List<AiChatItem>();
    }

    public static class AiHistory
    {
        const string Mark1 = "nextscan-chat 1";
        const string Mark2 = "nextscan-chat 2";

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

        /// <summary>The old form: only what was said.</summary>
        public static void Save(string id, IList<AiMessage> messages, string provider, string model, string page)
        {
            var items = new List<AiChatItem>();
            if (messages != null)
                foreach (AiMessage m in messages)
                    items.Add(new AiChatItem { Kind = m.Role == AiRole.User ? "user" : "assistant", Text = m.Text ?? "" });
            Save(id, items, provider, model, page, "");
        }

        /// <summary>
        /// Writes the conversation, replacing whatever was there.
        ///
        /// Called after every completed turn rather than when the conversation
        /// ends, because a conversation does not end -- the window closes, or
        /// the machine does, and a history written at the end is a history that
        /// is never written.
        /// </summary>
        public static void Save(string id, IList<AiChatItem> items, string provider, string model, string page, string title)
        {
            if (string.IsNullOrEmpty(id) || items == null || items.Count == 0) return;

            try
            {
                Directory.CreateDirectory(Folder);

                var text = new StringBuilder();
                text.Append(Mark2).Append('\n');
                text.Append("when=").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
                text.Append("provider=").Append(One(provider)).Append('\n');
                text.Append("model=").Append(One(model)).Append('\n');
                text.Append("page=").Append(One(page)).Append('\n');
                if (!string.IsNullOrEmpty(title)) text.Append("title=").Append(One(title)).Append('\n');

                foreach (AiChatItem item in items)
                {
                    string body = item.Text ?? "";
                    string kind = item.Kind;
                    if (kind == "thought") kind += ":" + item.Seconds.ToString("0.#", CultureInfo.InvariantCulture);
                    text.Append(kind).Append(' ');
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
        /// The list needs a title, a date and the model, and nothing else.
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

        /// <summary>Gives a conversation a name of the operator's choosing.</summary>
        public static void Rename(string id, string title)
        {
            AiChat chat = Load(id);
            if (chat == null) return;
            DateTime when = chat.When;
            Save(id, chat.Items, chat.Provider, chat.Model, chat.Page, title);
            try { if (when != default(DateTime)) File.SetLastWriteTime(PathFor(id), when); } catch { }
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

            string mark = text.StartsWith(Mark2, StringComparison.Ordinal) ? Mark2
                        : text.StartsWith(Mark1, StringComparison.Ordinal) ? Mark1 : null;
            if (mark == null) return null;

            var chat = new AiChat { Id = Path.GetFileNameWithoutExtension(file) };
            int at = mark.Length;
            if (at < text.Length && text[at] == '\n') at++;
            string title = "";

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
                    case "title": title = value; break;
                }
            }

            // ---- items ----
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

                string kind = row.Substring(0, space);
                var item = new AiChatItem { Kind = kind, Text = body };
                int colon = kind.IndexOf(':');
                if (colon > 0)
                {
                    item.Kind = kind.Substring(0, colon);
                    double.TryParse(kind.Substring(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out item.Seconds);
                }

                if (item.Kind == "user")
                {
                    chat.Turns++;
                    if (chat.Title.Length == 0) chat.Title = Line(body);
                }

                if (withMessages)
                {
                    chat.Items.Add(item);
                    if (item.Kind == "user") chat.Messages.Add(AiMessage.FromUser(body));
                    else if (item.Kind == "assistant") chat.Messages.Add(AiMessage.FromAssistant(body));
                }
            }

            if (title.Length > 0) chat.Title = title;
            if (chat.Title.Length == 0) chat.Title = "(no question)";
            return chat;
        }

        /// <summary>The first line of a message, short enough for a list row.</summary>
        static string Line(string text)
        {
            string one = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            while (one.IndexOf("  ", StringComparison.Ordinal) >= 0) one = one.Replace("  ", " ");
            return one.Length <= 60 ? one : one.Substring(0, 59).TrimEnd() + "…";
        }
    }
}
