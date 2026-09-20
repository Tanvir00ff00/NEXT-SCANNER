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
        const int Version = 1;
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
        static EventWaitHandle _ready, _done;

        /// <summary>
        /// Publishes one page and waits for Photoshop to finish reading it.
        ///
        /// Returns false when there is nobody to hand it to, which is the
        /// ordinary case: the application is usually started by a person.
        /// </summary>
        internal static bool Publish(RawImage page, int pageIndex, int pageCount, Action<string> log)
        {
            if (!Serving || page == null || !page.IsValid) return false;

            try
            {
                int channels = page.Channels >= 3 ? 3 : 1;
                int bits = page.BitsPerChannel >= 16 ? 16 : 8;
                int sampleBytes = bits / 8;
                int stride = page.Width * channels * sampleBytes;

                byte[] icc = null;                 // reserved: the capture carries none yet
                int iccBytes = icc == null ? 0 : icc.Length;

                long pixelBytes = (long)stride * page.Height;
                long total = HeaderBytes + pixelBytes + iccBytes;
                if (total > int.MaxValue) { if (log != null) log("page too large to hand over"); return false; }

                _mapping = MemoryMappedFile.CreateNew(MappingPrefix + Session, total);
                using (MemoryMappedViewAccessor view = _mapping.CreateViewAccessor(0, total))
                {
                    WriteHeader(view, page, channels, bits, stride, (int)pixelBytes, iccBytes, pageIndex, pageCount);
                    WritePixels(view, page, channels, bits, stride);
                    if (iccBytes > 0) view.WriteArray(HeaderBytes + pixelBytes, icc, 0, iccBytes);
                }

                _ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyPrefix + Session);
                _done = new EventWaitHandle(false, EventResetMode.ManualReset, DonePrefix + Session);

                _ready.Set();
                if (log != null) log("handed " + page.Width + "x" + page.Height + " to Photoshop, waiting");

                // Photoshop reads straight out of the mapping, so it must stay
                // alive until the plug-in says it is done with it.
                if (!_done.WaitOne(TimeSpan.FromMinutes(10)) && log != null)
                    log("Photoshop did not acknowledge the frame");

                return true;
            }
            catch (Exception ex)
            {
                if (log != null) log("handing over failed: " + ex.Message);
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

            using (EventWaitHandle done = new EventWaitHandle(false, EventResetMode.ManualReset,
                                                              DonePrefix + Session))
            {
                done.Set();                     // nobody is reading; do not wait for them
                Publish(page, 1, 1, null);
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
            byte[] row = new byte[4];
            PutSixteen(row, 0, 65535);
            PutSixteen(row, 2, 0);
            check("65535 becomes", row[0] | (row[1] << 8), 32768);
            check("0 becomes", row[2] | (row[3] << 8), 0);

            Session = null;
            say(bad == 0 ? "  ok" : "  FAILED: " + bad + " field(s) wrong");
            return bad == 0 ? 0 : 1;
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
            try { if (_ready != null) _ready.Dispose(); } catch { }
            try { if (_done != null) _done.Dispose(); } catch { }
            try { if (_mapping != null) _mapping.Dispose(); } catch { }
            _ready = null; _done = null; _mapping = null;
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
