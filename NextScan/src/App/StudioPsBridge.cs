using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using NextScan.Core;

namespace NextScan.App
{
    /// <summary>
    /// Hands a scanned page to the Photoshop acquire module.
    ///
    /// Plan ref: master plan section 14.3.
    ///
    /// The shape of the frame is duplicated from src\Photoshop\NextScanFrame.h
    /// and the two must change together. It is repeated rather than generated
    /// because two languages that each declare the same twenty fields are
    /// easier to read than a generator neither side owns -- but a mismatch here
    /// arrives as a page of noise in Photoshop, so the magic and version are
    /// checked on the other side before a single pixel is trusted.
    ///
    /// Two of Photoshop's conventions are applied here rather than in the
    /// plug-in, because this is the side that can afford to think:
    ///
    ///   - sixteen bit samples are written in Photoshop's 0..32768 range, not
    ///     the capture's 0..65535
    ///   - samples are written red first; the capture is blue first
    ///
    /// Doing both once, on the writing side, keeps the plug-in a copy loop.
    /// </summary>
    internal static class StudioPsBridge
    {
        const int Magic = 0x5246534E;      // 'NSFR'
        const int Version = 2;
        // 8 int32, then 2 double, then 6 int32, then 6 reserved int32. Counted
        // against the struct rather than guessed: a header a few bytes short
        // shifts every pixel and the page still arrives, just wrong.
        const int HeaderBytes = 96;

        const string MappingPrefix = @"Local\NextScan.Frame.";
        const string ReadyPrefix = @"Local\NextScan.Ready.";
        const string DonePrefix = @"Local\NextScan.Done.";
        const string CancelPrefix = @"Local\NextScan.Cancel.";

        /// <summary>The session this run is serving, or null when started normally.</summary>
        internal static string Session { get; private set; }

        internal static bool Serving { get { return !string.IsNullOrEmpty(Session); } }

        /// <summary>
        /// Reads the session id out of the command line. Photoshop starts us
        /// with one when the operator picks File, Import, Next Scanner.
        /// </summary>
        internal static void ReadCommandLine(string[] args)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "--ps-acquire", StringComparison.OrdinalIgnoreCase))
                { Session = args[i + 1]; return; }
        }

        /// <summary>
        /// Records where this application lives, so the plug-in can start it.
        ///
        /// Written on every run rather than by an installer: the plug-in then
        /// keeps working when the application is moved or rebuilt somewhere
        /// else, which during development is most days.
        /// </summary>
        internal static void RecordLocation()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly() != null
                    ? System.Reflection.Assembly.GetEntryAssembly().Location
                    : null;
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\NextScan"))
                    if (key != null) key.SetValue("AppPath", exe,
                        Microsoft.Win32.RegistryValueKind.String);
            }
            catch { }   // a missing registry entry costs the plug-in a guess, not a crash
        }

        static MemoryMappedFile _mapping;

        /// <summary>
        /// Hands every page of a scan to Photoshop and waits for each one to be
        /// read before building the next.
        ///
        /// One at a time, not all at once: five 600 dpi pages is a great deal
        /// of memory to hold a second copy of, and Photoshop takes them one at
        /// a time regardless -- it asks for the next only once it has opened
        /// the last.
        ///
        /// Returns false when there is nobody to hand them to, which is the
        /// ordinary case: the application is usually started by a person.
        /// </summary>
        internal static bool PublishAll(IList<RawImage> pages, byte[] icc, Action<string> log)
        {
            if (!Serving || pages == null || pages.Count == 0) return false;

            try
            {
                // Opened once for the whole scan rather than per page. Ready and
                // Done are auto reset, so each page is one exchange and neither
                // side has to clear anything between them.
                using (EventWaitHandle ready = new EventWaitHandle(
                           false, EventResetMode.AutoReset, ReadyPrefix + Session))
                using (EventWaitHandle done = new EventWaitHandle(
                           false, EventResetMode.AutoReset, DonePrefix + Session))
                using (EventWaitHandle cancel = new EventWaitHandle(
                           false, EventResetMode.ManualReset, CancelPrefix + Session))
                {
                    for (int i = 0; i < pages.Count; i++)
                    {
                        if (cancel.WaitOne(0))
                        {
                            if (log != null)
                                log("Photoshop stopped after " + i + " of " + pages.Count + " pages");
                            return i > 0;
                        }

                        if (!Publish(pages[i], i + 1, pages.Count, icc, ready, done, cancel, log))
                            return i > 0;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("handing over failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Publishes one page into a mapping of its own and waits for the
        /// plug-in to finish reading it.
        ///
        /// A mapping per page, because the application builds the next page
        /// while the plug-in may still hold the last one open, and two mappings
        /// cannot share a name.
        /// </summary>
        static bool Publish(RawImage page, int pageIndex, int pageCount, byte[] icc,
                            EventWaitHandle ready, EventWaitHandle done, EventWaitHandle cancel,
                            Action<string> log)
        {
            if (page == null || !page.IsValid) return false;

            try
            {
                int channels = page.Channels >= 3 ? 3 : 1;
                int bits = page.BitsPerChannel >= 16 ? 16 : 8;
                int sampleBytes = bits / 8;
                int stride = page.Width * channels * sampleBytes;

                // Carried on every page rather than once per scan: each page is
                // its own mapping and its own document, and a few kilobytes is
                // nothing beside the pixels.
                int iccBytes = icc == null ? 0 : icc.Length;

                long pixelBytes = (long)stride * page.Height;
                long total = HeaderBytes + pixelBytes + iccBytes;
                if (total > int.MaxValue) { if (log != null) log("page too large to hand over"); return false; }

                _mapping = MemoryMappedFile.CreateNew(
                    MappingPrefix + Session + "." + pageIndex, total);
                using (MemoryMappedViewAccessor view = _mapping.CreateViewAccessor(0, total))
                {
                    WriteHeader(view, page, channels, bits, stride, (int)pixelBytes, iccBytes, pageIndex, pageCount);
                    WritePixels(view, page, channels, bits, stride);
                    if (iccBytes > 0) view.WriteArray(HeaderBytes + pixelBytes, icc, 0, iccBytes);
                }

                ready.Set();
                if (log != null)
                    log("handed page " + pageIndex + " of " + pageCount + ", " +
                        page.Width + "x" + page.Height + ", waiting");

                // Photoshop reads straight out of the mapping, so it must stay
                // alive until the plug-in says it is done with it. Cancel is
                // waited on alongside Done: an import the operator gave up on
                // must not cost them ten minutes of a stuck window.
                WaitHandle[] either = new WaitHandle[] { done, cancel };
                int woke = WaitHandle.WaitAny(either, TimeSpan.FromMinutes(10));

                if (woke == WaitHandle.WaitTimeout)
                {
                    if (log != null) log("Photoshop did not acknowledge page " + pageIndex);
                    return false;
                }
                if (woke == 1)
                {
                    if (log != null) log("Photoshop wants no more pages");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("handing over page " + pageIndex + " failed: " + ex.Message);
                return false;
            }
            finally { Release(); }
        }

        /// <summary>
        /// Writes a known frame, reads it back, and checks every field landed
        /// where the plug-in will look for it.
        ///
        /// The same reasoning as the ONNX interop test: the two sides agree on
        /// a byte layout, nothing checks that agreement at compile time, and a
        /// field written one slot out does not crash -- it delivers a page that
        /// looks like a scan and is not one.
        /// </summary>
        internal static int SelfTest(Action<string> say)
        {
            const int W = 64, H = 40;
            RawImage page = new RawImage
            {
                Width = W, Height = H, Channels = 3, BitsPerChannel = 8,
                Stride = W * 3, XDpi = 300, YDpi = 600
            };
            page.Pixels = new byte[page.Stride * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int o = y * page.Stride + x * 3;
                    page.Pixels[o] = 10;        // blue
                    page.Pixels[o + 1] = 20;    // green
                    page.Pixels[o + 2] = 30;    // red
                }

            Session = "selftest";
            int bad = 0;
            Action<string, long, long> check = delegate(string what, long got, long want)
            {
                bool ok = got == want;
                if (!ok) bad++;
                say(string.Format("  {0,-16} {1,-10} {2}", what, got, ok ? "ok" : "EXPECTED " + want));
            };

            using (EventWaitHandle ready = new EventWaitHandle(
                       false, EventResetMode.AutoReset, ReadyPrefix + Session))
            using (EventWaitHandle done = new EventWaitHandle(
                       false, EventResetMode.AutoReset, DonePrefix + Session))
            using (EventWaitHandle cancel = new EventWaitHandle(
                       false, EventResetMode.ManualReset, CancelPrefix + Session))
            {
                done.Set();                     // nobody is reading; do not wait for them
                Publish(page, 1, 1, Fake(), ready, done, cancel, null);
            }

            // Publish disposes the mapping on the way out, so the check runs on
            // a second copy written the same way.
            long total = HeaderBytes + (long)page.Stride * H;
            using (MemoryMappedFile map = MemoryMappedFile.CreateNew(MappingPrefix + "verify", total))
            {
                using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, total))
                {
                    WriteHeader(view, page, 3, 8, page.Stride, page.Stride * H, 0, 1, 1);
                    WritePixels(view, page, 3, 8, page.Stride);

                    check("magic", view.ReadInt32(0), Magic);
                    check("version", view.ReadInt32(4), Version);
                    check("width", view.ReadInt32(8), W);
                    check("height", view.ReadInt32(12), H);
                    check("channels", view.ReadInt32(16), 3);
                    check("bits", view.ReadInt32(20), 8);
                    check("stride", view.ReadInt32(24), page.Stride);
                    check("rgbOrder", view.ReadInt32(28), 1);
                    check("dpiX", (long)view.ReadDouble(32), 300);
                    check("dpiY", (long)view.ReadDouble(40), 600);
                    check("pixelBytes", view.ReadInt32(48), page.Stride * H);
                    check("iccBytes", view.ReadInt32(52), 0);
                    check("pixelOffset", view.ReadInt32(56), HeaderBytes);
                    check("iccOffset when none", view.ReadInt32(60), 0);
                    check("pageIndex", view.ReadInt32(64), 1);
                    check("pageCount", view.ReadInt32(68), 1);

                    // Red first, at the offset the plug-in will start from.
                    check("first R", view.ReadByte(HeaderBytes), 30);
                    check("first G", view.ReadByte(HeaderBytes + 1), 20);
                    check("first B", view.ReadByte(HeaderBytes + 2), 10);

                    // Last pixel of the last row: proves the stride arithmetic.
                    long last = HeaderBytes + (long)(H - 1) * page.Stride + (W - 1) * 3;
                    check("last R", view.ReadByte(last), 30);
                    check("last B", view.ReadByte(last + 2), 10);
                }
            }

            // Sixteen bit has to land in Photoshop's range, not the capture's.
            // A frame that carries a profile. Until now iccBytes was always
            // zero, so neither the writer nor the plug-in had ever run this
            // path -- and a profile written to the wrong offset arrives as a
            // document that claims to know what its colours mean.
            byte[] fake = Fake();
            long withIcc = HeaderBytes + (long)page.Stride * H + fake.Length;
            using (MemoryMappedFile map = MemoryMappedFile.CreateNew(MappingPrefix + "icc", withIcc))
            using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, withIcc))
            {
                WriteHeader(view, page, 3, 8, page.Stride, page.Stride * H, fake.Length, 1, 1);
                WritePixels(view, page, 3, 8, page.Stride);
                view.WriteArray(HeaderBytes + (long)page.Stride * H, fake, 0, fake.Length);

                check("iccBytes with profile", view.ReadInt32(52), fake.Length);
                check("iccOffset", view.ReadInt32(60), HeaderBytes + page.Stride * H);

                byte[] back = new byte[fake.Length];
                view.ReadArray(view.ReadInt32(60), back, 0, back.Length);

                int same = 0;
                for (int i = 0; i < back.Length; i++) if (back[i] == fake[i]) same++;
                check("profile read back", same, fake.Length);
                check("profile still a profile", IccProfile.IsProfile(back) ? 1 : 0, 1);
            }

            // And the real thing, which is what actually gets sent.
            byte[] real = IccProfile.Srgb();
            say(string.Format("  {0,-16} {1,-10} {2}", "sRGB profile",
                              real == null ? 0 : real.Length,
                              real == null ? "NOT ON THIS MACHINE - pages go untagged"
                                           : "ok, " + real.Length + " bytes"));

            byte[] row = new byte[4];
            PutSixteen(row, 0, 65535);
            PutSixteen(row, 2, 0);
            check("65535 becomes", row[0] | (row[1] << 8), 32768);
            check("0 becomes", row[2] | (row[3] << 8), 0);

            Session = null;
            if (bad != 0) { say("  FAILED: " + bad + " field(s) wrong"); return 1; }
            say("  layout ok");

            say("");
            say("NextScan Photoshop handover");
            say("");
            return Handshake(say);
        }

        /// <summary>
        /// Plays the plug-in against the real publisher and checks that every
        /// page arrives, in order, and that giving up stops the rest.
        ///
        /// The layout test above proves the two sides agree about bytes. This
        /// proves they agree about turns, which is the half that fails by
        /// hanging rather than by looking wrong -- and a hang inside Photoshop
        /// is the least debuggable place it could happen. Every wait here is
        /// bounded so a deadlock fails the build instead of stopping it.
        /// </summary>
        static int Handshake(Action<string> say)
        {
            int bad = 0;
            Action<string, bool> check = delegate(string what, bool ok)
            {
                if (!ok) bad++;
                say("  " + what.PadRight(38) + (ok ? "ok" : "FAILED"));
            };

            // Three pages of different sizes, so a page delivered out of order
            // is visible rather than merely plausible.
            List<RawImage> pages = new List<RawImage>();
            for (int n = 1; n <= 3; n++)
            {
                int w = 8 * n, h = 4 * n;
                RawImage page = new RawImage
                {
                    Width = w, Height = h, Channels = 3, BitsPerChannel = 8,
                    Stride = w * 3, XDpi = 300, YDpi = 300
                };
                page.Pixels = new byte[page.Stride * h];
                for (int i = 0; i < page.Pixels.Length; i++) page.Pixels[i] = (byte)n;
                pages.Add(page);
            }

            check("all three pages, in order", Deal(pages, 3, 3) == "8x4#1/3 16x8#2/3 24x12#3/3");
            check("giving up stops the rest", Deal(pages, 1, 3) == "8x4#1/3");

            say(bad == 0 ? "  ok" : "  FAILED: " + bad + " check(s)");
            return bad == 0 ? 0 : 1;
        }

        /// <summary>
        /// Publishes the pages while a second thread collects them the way the
        /// plug-in does, stopping after <paramref name="take"/> of them.
        /// Returns what was collected, as text, so a wrong order reads as a
        /// wrong string rather than as a count that happens to match.
        /// </summary>
        static string Deal(List<RawImage> pages, int take, int expect)
        {
            Session = "handshake" + Environment.TickCount;
            List<string> got = new List<string>();

            Thread reader = new Thread(delegate()
            {
                // Anything that goes wrong here is recorded rather than thrown.
                // A reader that dies on a background thread would take the whole
                // self test down with a stack trace, when what the caller needs
                // is one line saying which turn went wrong -- and a publisher
                // left waiting on a Done that will never come is exactly the
                // fault this test exists to catch.
                try
                {
                    using (EventWaitHandle ready = new EventWaitHandle(
                               false, EventResetMode.AutoReset, ReadyPrefix + Session))
                    using (EventWaitHandle done = new EventWaitHandle(
                               false, EventResetMode.AutoReset, DonePrefix + Session))
                    using (EventWaitHandle cancel = new EventWaitHandle(
                               false, EventResetMode.ManualReset, CancelPrefix + Session))
                    {
                        for (int n = 1; n <= take; n++)
                        {
                            if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
                            { got.Add("page " + n + " never came"); return; }

                            try
                            {
                                using (MemoryMappedFile map = MemoryMappedFile.OpenExisting(
                                           MappingPrefix + Session + "." + n))
                                using (MemoryMappedViewAccessor view = map.CreateViewAccessor())
                                {
                                    got.Add(view.ReadInt32(8) + "x" + view.ReadInt32(12) +
                                            "#" + view.ReadInt32(64) + "/" + view.ReadInt32(68));
                                }
                            }
                            finally
                            {
                                // Released even when the read threw, so a broken
                                // reader reports a wrong page instead of hanging
                                // the publisher for ten minutes.
                                if (n == take && take < expect) cancel.Set();
                                done.Set();
                            }
                        }
                    }
                }
                catch (Exception ex) { got.Add(ex.GetType().Name); }
            });
            reader.IsBackground = true;
            reader.Start();

            PublishAll(pages, null, null);
            reader.Join(TimeSpan.FromSeconds(20));

            Session = null;
            return string.Join(" ", got.ToArray());
        }

        /// <summary>
        /// A smallest possible thing that passes for a profile: the right
        /// length in the right byte order, and the signature at offset 36.
        /// Used by the self test so the profile path can be exercised without
        /// depending on what is installed on the machine running it.
        /// </summary>
        static byte[] Fake()
        {
            byte[] p = new byte[132];
            p[0] = 0; p[1] = 0; p[2] = 0; p[3] = 132;      // size, big endian
            p[36] = (byte)'a'; p[37] = (byte)'c';
            p[38] = (byte)'s'; p[39] = (byte)'p';
            for (int i = 40; i < p.Length; i++) p[i] = (byte)(i * 7);
            return p;
        }

        /// <summary>Lets the plug-in stop waiting when the operator closes us.</summary>
        internal static void Abandon()
        {
            if (!Serving) return;
            try
            {
                using (EventWaitHandle cancel = new EventWaitHandle(false, EventResetMode.ManualReset,
                                                                    CancelPrefix + Session))
                    cancel.Set();
            }
            catch { }
            Release();
        }

        static void Release()
        {
            try { if (_mapping != null) _mapping.Dispose(); } catch { }
            _mapping = null;
        }

        static void WriteHeader(MemoryMappedViewAccessor view, RawImage page, int channels, int bits,
                                int stride, int pixelBytes, int iccBytes, int pageIndex, int pageCount)
        {
            long at = 0;
            view.Write(at, Magic); at += 4;
            view.Write(at, Version); at += 4;
            view.Write(at, page.Width); at += 4;
            view.Write(at, page.Height); at += 4;
            view.Write(at, channels); at += 4;
            view.Write(at, bits); at += 4;
            view.Write(at, stride); at += 4;
            view.Write(at, 1); at += 4;                       // samples are red first
            view.Write(at, Dpi(page.XDpi)); at += 8;
            view.Write(at, Dpi(page.YDpi)); at += 8;
            view.Write(at, pixelBytes); at += 4;
            view.Write(at, iccBytes); at += 4;
            view.Write(at, HeaderBytes); at += 4;             // where the pixels start
            view.Write(at, iccBytes > 0 ? HeaderBytes + pixelBytes : 0); at += 4;
            view.Write(at, pageIndex); at += 4;
            view.Write(at, pageCount); at += 4;
            for (int i = 0; i < 6; i++) { view.Write(at, 0); at += 4; }
        }

        static double Dpi(double value)
        {
            return value >= 1 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 300.0;
        }

        /// <summary>
        /// Copies the page into the mapping, red first, and in Photoshop's own
        /// sixteen bit range.
        ///
        /// Row by row through a managed buffer rather than pixel by pixel
        /// through the accessor: a 600 dpi A4 page is a hundred million
        /// samples, and WriteArray once per row is the difference between a
        /// handover the operator does not notice and one they wait for.
        /// </summary>
        static void WritePixels(MemoryMappedViewAccessor view, RawImage page,
                                int channels, int bits, int stride)
        {
            byte[] row = new byte[stride];
            int sourceStep = page.BitsPerChannel == 16 ? 2 : 1;

            for (int y = 0; y < page.Height; y++)
            {
                int source = y * page.Stride;

                if (bits == 8)
                {
                    for (int x = 0; x < page.Width; x++)
                    {
                        int at = x * channels;
                        if (page.Channels >= 3)
                        {
                            int o = source + x * page.Channels * sourceStep;
                            if (page.BitsPerChannel == 16) o += 1;      // high byte
                            row[at] = page.Pixels[o + sourceStep * 2];  // red
                            row[at + 1] = page.Pixels[o + sourceStep];  // green
                            row[at + 2] = page.Pixels[o];               // blue
                        }
                        else
                        {
                            int o = source + x * sourceStep;
                            if (page.BitsPerChannel == 16) o += 1;
                            row[at] = page.Pixels[o];
                        }
                    }
                }
                else
                {
                    for (int x = 0; x < page.Width; x++)
                    {
                        int at = x * channels * 2;
                        int o = source + x * page.Channels * 2;
                        if (page.Channels >= 3)
                        {
                            PutSixteen(row, at, Sample(page, o + 4));
                            PutSixteen(row, at + 2, Sample(page, o + 2));
                            PutSixteen(row, at + 4, Sample(page, o));
                        }
                        else PutSixteen(row, at, Sample(page, o));
                    }
                }

                view.WriteArray(HeaderBytes + (long)y * stride, row, 0, stride);
            }
        }

        static int Sample(RawImage page, int offset)
        {
            return page.Pixels[offset] | (page.Pixels[offset + 1] << 8);   // little-endian pair
        }

        /// <summary>
        /// Writes one sample in Photoshop's sixteen bit range.
        ///
        /// Photoshop counts 0..32768, not 0..65535 -- a range with 32769 values
        /// in it, which is not a typo of theirs and cannot be produced by a
        /// shift. Handing it 0..65535 delivers a page that looks right until
        /// someone measures it.
        /// </summary>
        static void PutSixteen(byte[] row, int at, int sample)
        {
            int scaled = (int)((sample * 32768L + 32767L) / 65535L);
            if (scaled > 32768) scaled = 32768;
            row[at] = (byte)(scaled & 0xFF);
            row[at + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }
}
