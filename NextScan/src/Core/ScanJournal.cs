// =============================================================================
// NextScan Studio - crash-safe scan journal
// Plan ref: MASTER_PLAN section 12, and the stated failure case in section 18:
// "a 100-page duplex batch fails at page 87 -> journal + resume + never-renumber".
//
// Until now a batch existed only in memory until somebody pressed Save. Losing
// the program at page 87 lost all 87 pages, and the paper had already gone
// through the feeder - so the work was not merely unsaved, it was unrepeatable
// without re-stacking a hundred sheets.
//
// Every page is therefore written to disk the moment it arrives, into a session
// folder that survives the process. The folder is deleted once the pages have
// been exported; anything left behind is by definition unfinished work, and is
// offered back on the next start.
//
// Three details decide whether this is trustworthy:
//
//   * Writing happens on a background thread with a queue. Spooling 26 MB per
//     page on the acquisition thread would stall the feeder, and a stalled
//     feeder on real hardware means a paper jam, not just a slow scan.
//   * Each page file is written under a temporary name and renamed once
//     complete, so a session that died mid-write has no half-page to load.
//   * A session folder records the process that owns it. A second copy of the
//     studio running at the same time must not offer to "recover" the batch the
//     first one is still scanning.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;

namespace NextScan.Core
{
    /// <summary>An unfinished session found on disk.</summary>
    public class JournalSession
    {
        public string Directory = "";
        public int Pages;
        public DateTime Started;
        public string Note = "";

        public override string ToString()
        {
            return Pages + " page(s) from " + Started.ToString("g", CultureInfo.CurrentCulture);
        }
    }

    public class ScanJournal : IDisposable
    {
        const string PageExtension = ".nspage";
        const string SessionFile = "session.json";
        const string OwnerFile = "owner.txt";

        readonly string _dir;
        readonly Queue<RawImage> _queue = new Queue<RawImage>();
        readonly object _gate = new object();
        readonly ManualResetEvent _idle = new ManualResetEvent(true);

        Thread _writer;
        volatile bool _stop;
        int _written;
        int _queued;

        public Action<string> Log = delegate { };

        /// <summary>Pages safely on disk.</summary>
        public int WrittenPages { get { return _written; } }

        public string Directory { get { return _dir; } }

        /// <summary>Where sessions live. Local app data, not roaming: these are large.</summary>
        public static string Root
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NextScan", "sessions");
            }
        }

        ScanJournal(string dir) { _dir = dir; }

        // =====================================================================
        // Starting and stopping
        // =====================================================================
        public static ScanJournal Begin(string note)
        {
            string dir = Path.Combine(Root, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                                            + "_" + Guid.NewGuid().ToString("N").Substring(0, 6));
            System.IO.Directory.CreateDirectory(dir);

            JsonObj meta = new JsonObj()
                .Set("started", DateTime.Now.ToString("o", CultureInfo.InvariantCulture))
                .Set("note", note ?? "");
            File.WriteAllText(Path.Combine(dir, SessionFile), Json.Write(meta), Encoding.UTF8);

            // Claim the folder. Recovery uses this to tell a dead session from
            // one another running copy of the studio is still filling.
            Process me = Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(dir, OwnerFile),
                me.Id.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + me.ProcessName,
                Encoding.UTF8);

            ScanJournal j = new ScanJournal(dir);
            j._writer = new Thread(j.WriteLoop) { IsBackground = true, Name = "NextScan journal" };
            j._writer.Start();
            return j;
        }

        /// <summary>
        /// Queues a page. Returns at once: the caller is the acquisition path and
        /// must not wait on disk.
        /// </summary>
        public void AddPage(RawImage img)
        {
            if (img == null || !img.IsValid) return;
            lock (_gate)
            {
                _queue.Enqueue(img);
                _queued++;
                _idle.Reset();
                Monitor.Pulse(_gate);
            }
        }

        /// <summary>Waits for the queue to drain. False if it did not finish in time.</summary>
        public bool Flush(int timeoutMs)
        {
            return _idle.WaitOne(timeoutMs);
        }

        /// <summary>
        /// The pages have been exported, so the session is no longer needed.
        /// Called only after a successful save - never on the way out of a scan.
        /// </summary>
        public void Complete()
        {
            Stop();
            try { System.IO.Directory.Delete(_dir, true); }
            catch (Exception ex) { Log("could not remove the finished session: " + ex.Message); }
        }

        /// <summary>Stops writing but leaves the folder for recovery.</summary>
        public void Abandon()
        {
            Stop();
            // The owner claim goes, or the next start would think the session
            // still belongs to a live process and skip it.
            try { File.Delete(Path.Combine(_dir, OwnerFile)); } catch { }
        }

        void Stop()
        {
            _stop = true;
            lock (_gate) { Monitor.PulseAll(_gate); }
            Thread t = _writer;
            _writer = null;
            if (t != null && !t.Join(15000)) Log("the journal writer did not stop in time");
        }

        public void Dispose() { Abandon(); }

        // =====================================================================
        // Writing
        // =====================================================================
        void WriteLoop()
        {
            while (true)
            {
                RawImage img;
                lock (_gate)
                {
                    while (_queue.Count == 0 && !_stop) Monitor.Wait(_gate);
                    if (_queue.Count == 0)
                    {
                        _idle.Set();
                        if (_stop) return;
                        continue;
                    }
                    img = _queue.Dequeue();
                }

                try { WritePage(img, _written + 1); _written++; }
                catch (Exception ex) { Log("could not spool a page: " + ex.Message); }

                lock (_gate) { if (_queue.Count == 0) _idle.Set(); }
            }
        }

        void WritePage(RawImage img, int number)
        {
            string name = "page_" + number.ToString("0000", CultureInfo.InvariantCulture) + PageExtension;
            string final = Path.Combine(_dir, name);
            string temp = final + ".part";

            byte[] pixels = new byte[img.ByteLength];
            Array.Copy(img.Pixels, pixels, pixels.Length);
            byte[] packed = Deflate(pixels);

            JsonObj head = new JsonObj()
                .Set("w", img.Width)
                .Set("h", img.Height)
                .Set("stride", img.Stride)
                .Set("ch", img.Channels)
                .Set("bpc", img.BitsPerChannel)
                .Set("xdpi", img.XDpi)
                .Set("ydpi", img.YDpi)
                .Set("page", img.PageIndex)
                .Set("side", img.Side)
                .Set("bytes", packed.Length);

            using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                byte[] line = Encoding.UTF8.GetBytes(Json.Write(head) + "\n");
                fs.Write(line, 0, line.Length);
                fs.Write(packed, 0, packed.Length);
                fs.Flush(true);      // to the platter, not just to the cache
            }

            // Only now does the file exist under a name recovery will look at.
            if (File.Exists(final)) File.Delete(final);
            File.Move(temp, final);
        }

        static byte[] Deflate(byte[] data)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true))
                    ds.Write(data, 0, data.Length);
                return ms.ToArray();
            }
        }

        // =====================================================================
        // Recovery
        // =====================================================================
        /// <summary>
        /// Sessions left behind by a run that did not finish, newest first.
        /// Sessions still owned by a live process are skipped.
        /// </summary>
        public static List<JournalSession> FindIncomplete()
        {
            List<JournalSession> found = new List<JournalSession>();
            string root = Root;
            if (!System.IO.Directory.Exists(root)) return found;

            string[] dirs;
            try { dirs = System.IO.Directory.GetDirectories(root); }
            catch { return found; }

            foreach (string d in dirs)
            {
                try
                {
                    if (IsOwnedByLiveProcess(d)) continue;

                    string[] pages = System.IO.Directory.GetFiles(d, "*" + PageExtension);
                    if (pages.Length == 0)
                    {
                        // An empty husk from a session that started and scanned
                        // nothing. Nothing to offer, and nothing worth keeping.
                        try { System.IO.Directory.Delete(d, true); } catch { }
                        continue;
                    }

                    JournalSession s = new JournalSession { Directory = d, Pages = pages.Length };
                    string metaPath = Path.Combine(d, SessionFile);
                    if (File.Exists(metaPath))
                    {
                        JsonObj meta = Json.Parse(File.ReadAllText(metaPath, Encoding.UTF8));
                        s.Note = meta.Str("note", "");
                        DateTime t;
                        if (DateTime.TryParse(meta.Str("started", ""), CultureInfo.InvariantCulture,
                                              DateTimeStyles.RoundtripKind, out t)) s.Started = t;
                    }
                    if (s.Started == default(DateTime)) s.Started = System.IO.Directory.GetCreationTime(d);

                    found.Add(s);
                }
                catch { }
            }

            found.Sort(delegate (JournalSession a, JournalSession b) { return b.Started.CompareTo(a.Started); });
            return found;
        }

        static bool IsOwnedByLiveProcess(string dir)
        {
            string ownerPath = Path.Combine(dir, OwnerFile);
            if (!File.Exists(ownerPath)) return false;

            try
            {
                string[] lines = File.ReadAllLines(ownerPath);
                if (lines.Length < 2) return false;

                int pid;
                if (!int.TryParse(lines[0], out pid)) return false;
                if (pid == Process.GetCurrentProcess().Id) return true;

                Process p;
                try { p = Process.GetProcessById(pid); }
                catch { return false; }          // no such process: the owner is gone

                // A pid is reused eventually, so the name has to match too.
                return !p.HasExited &&
                       string.Equals(p.ProcessName, lines[1].Trim(), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// Reads a session's pages back in scan order. Page numbers come from the
        /// filenames, which were assigned as the pages arrived - so a recovered
        /// batch keeps its original order and numbering rather than being
        /// renumbered by whatever order the filesystem lists.
        /// </summary>
        public static List<RawImage> LoadPages(string dir)
        {
            List<RawImage> pages = new List<RawImage>();
            string[] files = System.IO.Directory.GetFiles(dir, "*" + PageExtension);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string f in files)
            {
                try
                {
                    RawImage img = LoadPage(f);
                    if (img != null && img.IsValid) pages.Add(img);
                }
                catch { }
            }
            return pages;
        }

        static RawImage LoadPage(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                // The header is one UTF-8 line; read it a byte at a time rather
                // than with a buffered reader, which would swallow pixel data.
                List<byte> line = new List<byte>(512);
                int b;
                while ((b = fs.ReadByte()) >= 0 && b != '\n') line.Add((byte)b);
                if (line.Count == 0) return null;

                JsonObj head = Json.Parse(Encoding.UTF8.GetString(line.ToArray()));

                RawImage img = new RawImage
                {
                    Width = head.Int("w", 0),
                    Height = head.Int("h", 0),
                    Stride = head.Int("stride", 0),
                    Channels = head.Int("ch", 3),
                    BitsPerChannel = head.Int("bpc", 8),
                    XDpi = head.Dbl("xdpi", 300),
                    YDpi = head.Dbl("ydpi", 300),
                    PageIndex = head.Int("page", 0),
                    Side = head.Int("side", 0)
                };

                int packedLength = head.Int("bytes", 0);
                if (packedLength <= 0 || img.Width <= 0 || img.Height <= 0) return null;

                byte[] packed = new byte[packedLength];
                int read = 0;
                while (read < packedLength)
                {
                    int n = fs.Read(packed, read, packedLength - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read != packedLength) return null;       // truncated: refuse it

                img.Pixels = Inflate(packed, (int)((long)img.Height * img.Stride));
                return img.IsValid ? img : null;
            }
        }

        static byte[] Inflate(byte[] packed, int expected)
        {
            using (MemoryStream src = new MemoryStream(packed))
            using (DeflateStream ds = new DeflateStream(src, CompressionMode.Decompress))
            using (MemoryStream outMs = new MemoryStream(Math.Max(1024, expected)))
            {
                ds.CopyTo(outMs);
                return outMs.ToArray();
            }
        }

        public static void Discard(string dir)
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }
}
