// =============================================================================
// NextScan Studio - hot folder (watched folder) automation
// Plan ref: MASTER_PLAN section 12 ("hot folders: drop images -> processed by a
// job spec -> output + optional move/delete of source"), persona P4.
//
// Drop files in, get named and converted output out. The hard part is not the
// watching, it is everything around it:
//
//   * A file appears in a directory listing long before the program writing it
//     has finished. Reading it then yields a truncated image, or an exception,
//     or - worst - a valid image missing its bottom half. Nothing is read until
//     it has stopped changing AND can be opened for exclusive access.
//   * The same file must never be processed twice, and a file that failed must
//     not be retried forever.
//   * Writing output into the folder being watched is an infinite loop. That is
//     refused up front rather than discovered at three in the morning.
//
// This polls the directory instead of using FileSystemWatcher. A watcher is
// event-driven and therefore faster, but it silently drops events when its
// buffer overflows - precisely when a batch of files lands at once, which is the
// case a hot folder exists to handle - so any correct implementation needs a
// periodic rescan anyway. Given that, the rescan alone is the whole mechanism,
// and there is no second code path that only misbehaves under load.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;

namespace NextScan.Core
{
    /// <summary>What happens to an input file once it has been processed.</summary>
    public enum SourceDisposition
    {
        MoveToSubfolder = 0,
        Delete = 1,
        LeaveInPlace = 2,
    }

    public enum HotFolderGrouping
    {
        /// <summary>Each input file produces its own output.</summary>
        PerFile = 0,
        /// <summary>Files that land together become one document.</summary>
        PerBatch = 1,
    }

    public class HotFolderOptions
    {
        public string WatchFolder = "";
        public bool Recursive;

        public SourceDisposition OnSuccess = SourceDisposition.MoveToSubfolder;
        public string DoneFolderName = "_processed";
        public string ErrorFolderName = "_errors";

        /// <summary>
        /// How long a file must stop changing before it is considered complete.
        /// Copies over a slow network share need more than a local drop.
        /// </summary>
        public int StableMilliseconds = 1200;

        /// <summary>How often the folder is listed.</summary>
        public int PollMilliseconds = 500;

        public HotFolderGrouping Grouping = HotFolderGrouping.PerFile;

        /// <summary>
        /// PerBatch only: how long to wait for more files before closing the
        /// group. Too short splits one drop into several documents.
        /// </summary>
        public int GroupQuietMilliseconds = 2000;

        public static readonly string[] Extensions =
            { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".bmp" };
    }

    /// <summary>What the folder did, for the status line and the log.</summary>
    public class HotFolderResult
    {
        public string SourcePath = "";
        public List<string> Written = new List<string>();
        public string Error;                    // null when it succeeded
        public bool Ok { get { return Error == null; } }
    }

    public class HotFolder : IDisposable
    {
        readonly HotFolderOptions _o;
        readonly ExportPlan _plan;
        readonly BatchOptions _batch;

        readonly object _gate = new object();
        readonly Dictionary<string, FileState> _seen = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _ready = new List<string>();

        Thread _worker;
        volatile bool _stop;
        DateTime _lastArrival = DateTime.MinValue;

        public Action<string> Log = delegate { };
        public Action<HotFolderResult> Processed = delegate { };

        public int SucceededFiles;
        public int FailedFiles;
        public int WrittenFiles;

        class FileState
        {
            public long Length = -1;
            public DateTime Written;
            public DateTime StillSince = DateTime.MaxValue;
            public bool Handled;
        }

        public HotFolder(HotFolderOptions options, ExportPlan plan, BatchOptions batch)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (plan == null) throw new ArgumentNullException("plan");

            _o = options;
            _plan = plan;
            _batch = batch ?? new BatchOptions();

            Validate();
        }

        /// <summary>
        /// Refuses configurations that cannot work, before anything is watched.
        /// The output-inside-input case is the important one: each file written
        /// would be picked up as a new input and processed again, for ever.
        /// </summary>
        void Validate()
        {
            if (string.IsNullOrEmpty(_o.WatchFolder))
                throw new ArgumentException("No folder to watch was given.");

            string watch = Path.GetFullPath(_o.WatchFolder);
            if (!Directory.Exists(watch))
                throw new DirectoryNotFoundException("The watched folder does not exist: " + watch);

            string outDir = Path.GetFullPath(string.IsNullOrEmpty(_plan.Directory)
                ? Environment.CurrentDirectory : _plan.Directory);

            if (Contains(watch, outDir) || string.Equals(watch, outDir, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "The output folder is inside the watched folder, so every file written " +
                    "would be picked up again. Choose an output folder outside " + watch + ".");

            if (Contains(outDir, watch))
                throw new ArgumentException(
                    "The watched folder is inside the output folder, which would reprocess " +
                    "earlier output. Choose folders that do not contain each other.");
        }

        static bool Contains(string outer, string inner)
        {
            string a = outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return inner.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================
        // Running
        // =====================================================================
        public bool Running { get { return _worker != null; } }

        public void Start()
        {
            if (_worker != null) return;
            _stop = false;
            _worker = new Thread(Loop) { IsBackground = true, Name = "NextScan hot folder" };
            _worker.Start();
            Log("watching " + Path.GetFullPath(_o.WatchFolder));
        }

        public void Stop()
        {
            _stop = true;
            Thread t = _worker;
            _worker = null;
            if (t != null && !t.Join(5000)) Log("the hot folder thread did not stop cleanly");
        }

        public void Dispose() { Stop(); }

        void Loop()
        {
            while (!_stop)
            {
                try { PumpOnce(); }
                catch (Exception ex) { Log("hot folder sweep failed: " + ex.Message); }
                Thread.Sleep(Math.Max(50, _o.PollMilliseconds));
            }
        }

        /// <summary>
        /// One sweep: notice files, promote the settled ones, process what is
        /// due. Public so a test can drive it without depending on thread
        /// timing, and so a "process now" button has something to call.
        /// </summary>
        public void PumpOnce()
        {
            Scan();
            List<string> due = TakeDue();
            if (due.Count == 0) return;

            if (_o.Grouping == HotFolderGrouping.PerBatch) ProcessGroup(due);
            else foreach (string f in due) ProcessGroup(new List<string> { f });
        }

        /// <summary>
        /// Runs sweeps until nothing is pending, or the timeout expires.
        /// Returns true if the folder went quiet. Used by tests and by Stop-
        /// after-finishing; polling here is unavoidable because the thing being
        /// waited for is a file appearing on disk.
        /// </summary>
        public bool Drain(int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                PumpOnce();
                lock (_gate)
                {
                    if (_ready.Count == 0 && !AnyPending()) return true;
                }
                Thread.Sleep(Math.Min(100, Math.Max(20, _o.PollMilliseconds / 4)));
            }
            return false;
        }

        bool AnyPending()
        {
            foreach (FileState st in _seen.Values) if (!st.Handled) return true;
            return false;
        }

        // =====================================================================
        // Noticing files
        // =====================================================================
        void Scan()
        {
            string watch = Path.GetFullPath(_o.WatchFolder);
            string done = Path.Combine(watch, _o.DoneFolderName);
            string errors = Path.Combine(watch, _o.ErrorFolderName);

            string[] files;
            try
            {
                files = Directory.GetFiles(watch, "*.*",
                    _o.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) { Log("cannot list the watched folder: " + ex.Message); return; }

            DateTime now = DateTime.UtcNow;

            foreach (string f in files)
            {
                // Our own output subfolders are not input, however deep the scan
                // goes. Without this a recursive watch reprocesses everything it
                // has already filed.
                if (Contains(done, f) || Contains(errors, f)) continue;
                if (!IsImage(f)) continue;

                FileInfo fi;
                try { fi = new FileInfo(f); if (!fi.Exists) continue; }
                catch { continue; }

                lock (_gate)
                {
                    FileState st;
                    if (!_seen.TryGetValue(f, out st))
                    {
                        st = new FileState();
                        _seen[f] = st;
                        _lastArrival = DateTime.UtcNow;
                        Log("noticed " + Path.GetFileName(f));
                    }
                    if (st.Handled)
                    {
                        // A name can come back: an operator re-drops a corrected
                        // page under the same filename, or a sync replaces it.
                        // Keying only on the path would ignore that file for as
                        // long as the program ran - silently, which is the worst
                        // way for a hot folder to fail. A handled entry whose
                        // file no longer matches what was handled is a new file.
                        if (fi.Length == st.Length && fi.LastWriteTimeUtc == st.Written) continue;
                        st.Handled = false;
                        st.StillSince = DateTime.MaxValue;
                    }

                    // Still being written: the clock restarts on every change.
                    if (fi.Length != st.Length || fi.LastWriteTimeUtc != st.Written)
                    {
                        st.Length = fi.Length;
                        st.Written = fi.LastWriteTimeUtc;
                        st.StillSince = now;
                        continue;
                    }

                    if (st.StillSince == DateTime.MaxValue) { st.StillSince = now; continue; }
                    if ((now - st.StillSince).TotalMilliseconds < _o.StableMilliseconds) continue;

                    // Size and timestamp have settled - but a program can hold a
                    // file open without writing to it, and a zero-byte placeholder
                    // is perfectly stable. Both are caught here.
                    if (fi.Length <= 0) continue;
                    if (!CanOpenExclusively(f)) continue;

                    st.Handled = true;
                    if (!_ready.Contains(f)) _ready.Add(f);
                }
            }

            // Files that vanished (moved away by hand, or by us) stop being
            // tracked, so the dictionary does not grow without limit and a name
            // that comes back is treated as new.
            lock (_gate)
            {
                List<string> gone = new List<string>();
                foreach (string k in _seen.Keys) if (!File.Exists(k)) gone.Add(k);
                foreach (string k in gone) if (!_ready.Contains(k)) _seen.Remove(k);
            }
        }

        static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            foreach (string e in HotFolderOptions.Extensions) if (e == ext) return true;
            return false;
        }

        static bool CanOpenExclusively(string path)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Returns the files that should be processed now. With PerBatch that is
        /// nothing until the drop has gone quiet, so a folder of forty pages
        /// becomes one document rather than forty.
        /// </summary>
        List<string> TakeDue()
        {
            lock (_gate)
            {
                if (_ready.Count == 0) return new List<string>();

                if (_o.Grouping == HotFolderGrouping.PerBatch)
                {
                    double quiet = (DateTime.UtcNow - _lastArrival).TotalMilliseconds;
                    if (quiet < _o.GroupQuietMilliseconds) return new List<string>();
                }

                // Sorted so page order is the operator's file order rather than
                // whatever the filesystem happened to return.
                List<string> take = new List<string>(_ready);
                take.Sort(StringComparer.OrdinalIgnoreCase);
                _ready.Clear();
                return take;
            }
        }

        // =====================================================================
        // Processing
        // =====================================================================
        void ProcessGroup(List<string> files)
        {
            HotFolderResult result = new HotFolderResult();
            result.SourcePath = files.Count == 1 ? files[0] : files[0] + " +" + (files.Count - 1);

            List<RawImage> pages = new List<RawImage>();
            List<string> loaded = new List<string>();
            List<string> failed = new List<string>();

            foreach (string f in files)
            {
                try
                {
                    List<RawImage> fromFile = LoadPages(f);
                    if (fromFile.Count == 0) throw new IOException("the file holds no image");
                    pages.AddRange(fromFile);
                    loaded.Add(f);
                }
                catch (Exception ex)
                {
                    Log("cannot read " + Path.GetFileName(f) + ": " + ex.Message);
                    Quarantine(f, ex.Message);
                    Forget(f);
                    failed.Add(f);
                    FailedFiles++;
                }
            }

            if (pages.Count == 0)
            {
                result.Error = "nothing could be read";
                Processed(result);
                return;
            }

            try
            {
                ExportPlan plan = Clone(_plan);
                if (plan.Context != null)
                {
                    plan.Context.Stamp = DateTime.Now;
                    // {name} is not a token, but the source filename is the one
                    // thing an operator most often wants carried through, so the
                    // batch name is set to it and reachable as {batch}.
                    plan.Context.BatchName = Path.GetFileNameWithoutExtension(loaded[0]);
                }

                int documents;
                List<string> written = BatchSplitter.WriteBatch(pages, _batch, plan, out documents);
                if (written.Count == 0) throw new IOException("no output file was written");

                result.Written.AddRange(written);
                WrittenFiles += written.Count;
                SucceededFiles += loaded.Count;

                foreach (string f in loaded) { Retire(f); Forget(f); }
                Log("wrote " + written.Count + " file(s) from " + loaded.Count + " input(s)");
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                Log("export failed: " + ex.Message);
                foreach (string f in loaded) { Quarantine(f, ex.Message); Forget(f); FailedFiles++; }
            }

            Processed(result);
        }

        static ExportPlan Clone(ExportPlan p)
        {
            NameContext c = p.Context ?? new NameContext();
            return new ExportPlan
            {
                Directory = p.Directory,
                Pattern = p.Pattern,
                Format = p.Format,
                JpegQuality = p.JpegQuality,
                MultiPage = p.MultiPage,
                Context = new NameContext
                {
                    Stamp = c.Stamp,
                    Device = c.Device,
                    Dpi = c.Dpi,
                    Mode = c.Mode,
                    Source = c.Source,
                    BatchName = c.BatchName
                }
            };
        }

        /// <summary>
        /// Reads every page of an input file. Multi-page TIFFs are the reason
        /// this returns a list: dropping one in and getting only its first page
        /// back would be silent data loss.
        /// </summary>
        static List<RawImage> LoadPages(string path)
        {
            List<RawImage> pages = new List<RawImage>();

            // Read through memory so the file is not left locked - it has to be
            // movable the moment this returns.
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream ms = new MemoryStream(bytes))
            using (Image img = Image.FromStream(ms))
            {
                int frames = 1;
                try { frames = Math.Max(1, img.GetFrameCount(FrameDimension.Page)); }
                catch { frames = 1; }

                for (int i = 0; i < frames; i++)
                {
                    if (frames > 1) img.SelectActiveFrame(FrameDimension.Page, i);
                    using (Bitmap bmp = new Bitmap(img))
                    {
                        // Bitmap(Image) drops the source resolution on some
                        // formats, so it is copied across explicitly: the output
                        // page size in a PDF depends entirely on it.
                        bmp.SetResolution(img.HorizontalResolution > 0 ? img.HorizontalResolution : 300f,
                                          img.VerticalResolution > 0 ? img.VerticalResolution : 300f);
                        RawImage raw = RawImage.FromBitmap(bmp);
                        if (raw != null && raw.IsValid)
                        {
                            raw.PageIndex = i;
                            pages.Add(raw);
                        }
                    }
                }
            }
            return pages;
        }

        // =====================================================================
        // What happens to the input afterwards
        // =====================================================================
        void Retire(string path)
        {
            try
            {
                switch (_o.OnSuccess)
                {
                    case SourceDisposition.Delete:
                        File.Delete(path);
                        break;

                    case SourceDisposition.LeaveInPlace:
                        // It stays where it is and stays marked handled, so the
                        // next sweep will not pick it up again.
                        break;

                    default:
                        MoveInto(path, _o.DoneFolderName);
                        break;
                }
            }
            catch (Exception ex) { Log("could not retire " + Path.GetFileName(path) + ": " + ex.Message); }
        }

        void Quarantine(string path, string reason)
        {
            try
            {
                string moved = MoveInto(path, _o.ErrorFolderName);
                if (moved == null) return;

                // The reason travels with the file. An errors folder that only
                // says "something went wrong" is a folder nobody ever empties.
                File.WriteAllText(moved + ".txt",
                    "NextScan hot folder" + Environment.NewLine +
                    DateTime.Now.ToString("u", CultureInfo.InvariantCulture) + Environment.NewLine +
                    "Original: " + path + Environment.NewLine +
                    "Problem:  " + reason + Environment.NewLine);
            }
            catch (Exception ex) { Log("could not quarantine " + Path.GetFileName(path) + ": " + ex.Message); }
        }

        /// <summary>
        /// Stops tracking a path once the file has left it, so the name is free
        /// for a different file and the table does not grow all day. Files left
        /// in place are deliberately not forgotten - that is what stops them
        /// being processed again on the next sweep.
        /// </summary>
        void Forget(string path)
        {
            if (_o.OnSuccess == SourceDisposition.LeaveInPlace && File.Exists(path)) return;
            lock (_gate) { _seen.Remove(path); }
        }

        /// <summary>Moves a file into a subfolder of the watched folder, without overwriting.</summary>
        string MoveInto(string path, string subfolder)
        {
            string dir = Path.Combine(Path.GetFullPath(_o.WatchFolder), subfolder);
            Directory.CreateDirectory(dir);

            string stem = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string target = Path.Combine(dir, stem + ext);

            for (int n = 2; File.Exists(target) && n < 100000; n++)
                target = Path.Combine(dir, stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext);

            File.Move(path, target);
            return target;
        }
    }
}
