// =============================================================================
// NextScan Studio - what the assistant is told about the application
// Plan ref: docs/AI_LAYER.md
//
// An operator says "put this in Word" with the scan on the screen and a blank
// document open beside it, and means both. A model that is not told either
// asks what "this" is, or guesses. So every turn carries a few lines saying
// where the application is: which screen, whether there is a scanner and what
// is on it, how many pages have been scanned, which documents are open and
// whether they have anything in them, and what happened last -- the same
// things the operator can see.
//
// Facts, not contents: names and counts, never the text of a scan or of a
// customer's document. The model asks for those with its tools when it needs
// them, and the operator sees each time it does.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using NextScan.Core;

namespace NextScan.App
{
    public partial class StudioShell
    {
        /// <summary>The last things that happened, as the status line said them, oldest first.</summary>
        readonly List<KeyValuePair<DateTime, string>> _activity = new List<KeyValuePair<DateTime, string>>();

        /// <summary>Set while the assistant panel itself is speaking: its own messages are not news to it.</summary>
        bool _assistantSpeaking;

        void NoteActivity(string text)
        {
            if (_assistantSpeaking) return;
            string s = (text ?? "").Trim();
            if (s.Length < 4 || s == "Ready." || s.EndsWith("…", StringComparison.Ordinal) && s.Length < 16) return;
            if (_activity.Count > 0 && _activity[_activity.Count - 1].Value == s) return;
            _activity.Add(new KeyValuePair<DateTime, string>(DateTime.Now, s.Length > 160 ? s.Substring(0, 157) + "…" : s));
            if (_activity.Count > 8) _activity.RemoveAt(0);
        }

        /// <summary>The page the operator is looking at, and what to call it: the selected scan, else the preview.</summary>
        Tuple<RawImage, string> CurrentScanForAssistant()
        {
            if (_film != null && _film.SelectedImage != null)
                return Tuple.Create(_film.SelectedImage, "Scanned page " + (_film.SelectedIndex + 1) + " of " + _film.Count);
            if (_capturePage != null && _capturePage.IsValid) return Tuple.Create(_capturePage, "The preview on the scanner glass");
            if (_canvas != null && !_canvas.IsPlaceholder && _canvas.Image != null) return Tuple.Create(_canvas.Image, "The page on the scanner screen");
            return null;
        }

        /// <summary>
        /// Where the application is, for the assistant's turn. A few short
        /// lines; the documents are glanced at (block count, empty or not),
        /// never read.
        /// </summary>
        async Task<string> AssistantState()
        {
            var s = new StringBuilder();
            s.Append("Time: ").Append(DateTime.Now.ToString("dddd d MMMM yyyy, h:mm tt", CultureInfo.InvariantCulture)).Append('\n');

            string screen = _activeSection >= 0 && _activeSection < SectionNames.Length ? SectionNames[_activeSection] : "";
            s.Append("Screen: ").Append(screen == "Assist" ? "the document workspace (Assist)" : screen + " (the scanner)").Append('\n');

            s.Append("Scanner: ").Append(_device != null ? _device.FriendlyName : "none connected").Append('\n');

            int pages = _film == null ? 0 : _film.Count;
            s.Append("Scanned pages this session: ").Append(pages);
            if (pages > 0 && _film.SelectedImage != null) s.Append(" (page ").Append(_film.SelectedIndex + 1).Append(" is selected)");
            s.Append('\n');

            Tuple<RawImage, string> current = CurrentScanForAssistant();
            if (current != null)
            {
                RawImage p = current.Item1;
                double w = p.XDpi > 1 ? p.Width / p.XDpi * 25.4 : 0, h = p.YDpi > 1 ? p.Height / p.YDpi * 25.4 : 0;
                s.Append("On screen to look at: ").Append(current.Item2);
                if (w > 0) s.Append(", ").Append(Math.Round(w)).Append(" x ").Append(Math.Round(h)).Append(" mm");
                s.Append(" (look_at_scan shows it)\n");
            }
            else s.Append("Nothing has been scanned or previewed yet.\n");

            if (_workspace != null)
            {
                if (_workspace.Tabs.Count == 0) s.Append("Documents open: none\n");
                else
                {
                    s.Append("Documents open:\n");
                    foreach (DocTab tab in _workspace.Tabs)
                    {
                        s.Append("- ").Append(tab.Id).Append(": ").Append(tab.Title).Append(" (").Append(DocKinds.Noun(tab.Kind).ToLowerInvariant());
                        if (tab == _workspace.Current) s.Append(", in front");
                        if (tab.Dirty) s.Append(", unsaved");
                        if (tab.Path.Length == 0) s.Append(", never saved");
                        var view = tab.Surface as Docs.DocView;
                        if (view == null || !view.Ready) s.Append(", still opening");
                        else if (tab.Kind == DocKind.Word)
                        {
                            try
                            {
                                Task<string> glance = view.RunScriptAsync(StudioDocTools.Glance, false);
                                if (await Task.WhenAny(glance, Task.Delay(1500)) == glance && !glance.IsFaulted)
                                {
                                    var seen = new JavaScriptSerializer().DeserializeObject(glance.Result) as Dictionary<string, object>;
                                    if (seen != null)
                                    {
                                        bool empty = seen.ContainsKey("empty") && seen["empty"] is bool && (bool)seen["empty"];
                                        s.Append(empty ? ", empty" : ", " + seen["pages"] + " page(s), " + seen["blocks"] + " blocks");
                                    }
                                }
                            }
                            catch { }
                        }
                        s.Append(")\n");
                    }
                }
            }

            if (_activity.Count > 0)
            {
                s.Append("Recently:\n");
                foreach (var a in _activity)
                    s.Append("- ").Append(a.Key.ToString("h:mm tt", CultureInfo.InvariantCulture)).Append(" ").Append(a.Value).Append('\n');
            }
            return s.ToString();
        }
    }
}
