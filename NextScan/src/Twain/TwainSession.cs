// =============================================================================
// NextScan Studio - Native TWAIN 2.x session
// Plan ref: MASTER_PLAN section 7.2.
//
// This class runs ONLY inside a host process (NextScan.Host32 / Host64). It must
// never be loaded into the UI process: vendor data sources hang, leak and crash,
// and isolating them is the whole point of the architecture (plan section 5.1).
//
// It must also only ever be driven from a single STA thread that owns a window
// and pumps messages - see TwainPump.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NextScan.Core;

namespace NextScan.Twain
{
    /// <summary>TWAIN protocol states 1-7. Every triplet asserts a legal source state.</summary>
    public enum TwainState
    {
        PreSession = 1,
        DsmLoaded = 2,
        DsmOpen = 3,
        SourceOpen = 4,
        SourceEnabled = 5,
        TransferReady = 6,
        Transferring = 7
    }

    /// <summary>
    /// One TWAIN session: DSM binding, state machine, capability negotiation and
    /// image transfer. Acquired pages are handed back as Core.RawImage.
    /// </summary>
    public class TwainSession : IDisposable
    {
        // ---------------------------------------------------------------- state
        IntPtr _hDsm = IntPtr.Zero;
        DsmEntryDelegate _dsmEntry;
        IntPtr _appIdPtr = IntPtr.Zero;
        IntPtr _dsIdPtr = IntPtr.Zero;
        TwainState _state = TwainState.PreSession;
        string _dsmPath = "";
        bool _dsmIs2x;

        // DSM 2.x memory functions. When the DSM is 1.x these stay null and we fall
        // back to the Global* family, which is what the legacy twain_32.dll expects.
        DsmMemAllocateDelegate _memAllocate;
        DsmMemFreeDelegate _memFree;
        DsmMemLockDelegate _memLock;
        DsmMemUnlockDelegate _memUnlock;

        /// <summary>Raised when the DS signals MSG_XFERREADY through the event loop.</summary>
        public bool XferReady { get; set; }
        /// <summary>Raised when the DS asks to be closed (user hit Cancel in a vendor UI).</summary>
        public bool CloseRequested { get; set; }

        public TwainState State { get { return _state; } }
        public string DsmPath { get { return _dsmPath; } }
        public bool DsmIs2x { get { return _dsmIs2x; } }

        public Action<string> Log = delegate { };

        /// <summary>Last condition code fetched from the DS, for diagnostics.</summary>
        public ushort LastConditionCode { get; private set; }

        // ---------------------------------------------------------------- DSM discovery
        /// <summary>
        /// Locates a usable Data Source Manager for THIS process's bitness.
        /// There is no cross-bitness bridge (plan section 3.1) - a 64-bit process
        /// simply cannot see a 32-bit data source, which is why Host32 exists.
        /// </summary>
        public static List<string> FindDsmCandidates()
        {
            List<string> paths = new List<string>();
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            // Preferred: the TWAIN Working Group DSM, if the machine has one.
            // Note SpecialFolder.System already resolves to SysWOW64 inside a 32-bit
            // process on 64-bit Windows, so this is correct for both hosts.
            paths.Add(Path.Combine(sys, "TWAINDSM.DLL"));

            // App-local copy, if a future installer ever ships one.
            try
            {
                string appDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(appDir))
                    paths.Add(Path.Combine(appDir, "TWAINDSM.DLL"));
            }
            catch { }

            // Legacy Microsoft-shipped 1.x DSM. 32-bit only - it lives in the Windows
            // directory and is the ONLY DSM present on a large number of machines.
            if (IntPtr.Size == 4)
                paths.Add(Path.Combine(win, "twain_32.dll"));

            return paths;
        }

        public NsResult OpenDsm(IntPtr hwnd)
        {
            if (_state >= TwainState.DsmOpen) return NsResult.Success();

            List<string> candidates = FindDsmCandidates();
            string chosen = null;
            foreach (string p in candidates)
            {
                if (!File.Exists(p)) { Log("DSM candidate missing: " + p); continue; }
                _hDsm = NativeMethods.LoadLibraryW(p);
                if (_hDsm != IntPtr.Zero) { chosen = p; break; }
                Log("LoadLibrary failed for " + p + " (win32 " + Marshal.GetLastWin32Error() + ")");
            }

            if (chosen == null)
            {
                return NsResult.Fail(NsError.TwainDsmNotFound,
                    "No TWAIN Data Source Manager found for the " + (IntPtr.Size * 8) + "-bit host.",
                    IntPtr.Size == 8
                        ? "This machine has no 64-bit TWAIN DSM. 32-bit scanners are still reachable through the 32-bit host."
                        : "Install your scanner's TWAIN driver, or use the WIA connection instead.");
            }

            _dsmPath = chosen;
            IntPtr proc = NativeMethods.GetProcAddress(_hDsm, "DSM_Entry");
            if (proc == IntPtr.Zero)
            {
                NativeMethods.FreeLibrary(_hDsm);
                _hDsm = IntPtr.Zero;
                return NsResult.Fail(NsError.TwainDsmOpenFailed,
                    "DSM_Entry not exported by " + chosen, "The TWAIN driver installation looks damaged.");
            }

            _dsmEntry = (DsmEntryDelegate)Marshal.GetDelegateForFunctionPointer(proc, typeof(DsmEntryDelegate));
            _state = TwainState.DsmLoaded;
            Log("DSM loaded: " + chosen);

            // Announce as TWAIN 2.4 first. If the DSM rejects that (some very old
            // 1.x managers do), retry as 1.9 without the APP2 flag.
            NsResult r = TryOpenDsm(hwnd, 2, 4, true);
            if (!r.Ok)
            {
                Log("OPENDSM as 2.4 failed, retrying as 1.9");
                r = TryOpenDsm(hwnd, 1, 9, false);
            }
            return r;
        }

        NsResult TryOpenDsm(IntPtr hwnd, ushort protoMajor, ushort protoMinor, bool app2)
        {
            FreeIdentity(ref _appIdPtr);

            TW_IDENTITY app = new TW_IDENTITY();
            app.Id = 0;
            app.Version.MajorNum = 1;
            app.Version.MinorNum = 0;
            app.Version.Language = 13;  // TWLG_ENGLISH_USA
            app.Version.Country = 1;    // TWCY_USA
            app.Version.Info = "NextScan Studio 1.0";
            app.ProtocolMajor = protoMajor;
            app.ProtocolMinor = protoMinor;
            app.SupportedGroups = DG.IMAGE | DG.CONTROL;
            if (app2) app.SupportedGroups |= DF.APP2;
            app.Manufacturer = "NextScan";
            app.ProductFamily = "NextScan Studio";
            app.ProductName = "NextScan Studio";

            _appIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_IDENTITY)));
            Marshal.StructureToPtr(app, _appIdPtr, false);

            IntPtr hwndPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(hwndPtr, hwnd);
                ushort rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.PARENT, MSG.OPENDSM, hwndPtr);
                if (rc != TWRC.SUCCESS)
                {
                    return NsResult.Fail(NsError.TwainDsmOpenFailed,
                        "MSG_OPENDSM returned " + rc, "Try restarting the application.");
                }
            }
            finally { Marshal.FreeHGlobal(hwndPtr); }

            _state = TwainState.DsmOpen;

            // Re-read our identity: a 2.x DSM sets DF_DSM2 in SupportedGroups to tell
            // us its memory functions are available.
            TW_IDENTITY back = (TW_IDENTITY)Marshal.PtrToStructure(_appIdPtr, typeof(TW_IDENTITY));
            _dsmIs2x = (back.SupportedGroups & DF.DSM2) != 0;

            if (_dsmIs2x) AcquireEntryPoints();
            Log("DSM open (protocol " + protoMajor + "." + protoMinor + ", 2.x memory functions: " + _dsmIs2x + ")");
            return NsResult.Success();
        }

        void AcquireEntryPoints()
        {
            TW_ENTRYPOINT ep = new TW_ENTRYPOINT();
            ep.Size = (uint)Marshal.SizeOf(typeof(TW_ENTRYPOINT));
            IntPtr p = Marshal.AllocHGlobal((int)ep.Size);
            try
            {
                Marshal.StructureToPtr(ep, p, false);
                ushort rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.ENTRYPOINT, MSG.GET, p);
                if (rc != TWRC.SUCCESS) { Log("DAT_ENTRYPOINT/MSG_GET returned " + rc + " - using Global* memory"); return; }

                ep = (TW_ENTRYPOINT)Marshal.PtrToStructure(p, typeof(TW_ENTRYPOINT));
                if (ep.DSM_MemAllocate != IntPtr.Zero)
                    _memAllocate = (DsmMemAllocateDelegate)Marshal.GetDelegateForFunctionPointer(ep.DSM_MemAllocate, typeof(DsmMemAllocateDelegate));
                if (ep.DSM_MemFree != IntPtr.Zero)
                    _memFree = (DsmMemFreeDelegate)Marshal.GetDelegateForFunctionPointer(ep.DSM_MemFree, typeof(DsmMemFreeDelegate));
                if (ep.DSM_MemLock != IntPtr.Zero)
                    _memLock = (DsmMemLockDelegate)Marshal.GetDelegateForFunctionPointer(ep.DSM_MemLock, typeof(DsmMemLockDelegate));
                if (ep.DSM_MemUnlock != IntPtr.Zero)
                    _memUnlock = (DsmMemUnlockDelegate)Marshal.GetDelegateForFunctionPointer(ep.DSM_MemUnlock, typeof(DsmMemUnlockDelegate));
                Log("DSM 2.x memory entry points acquired");
            }
            catch (Exception ex) { Log("AcquireEntryPoints failed: " + ex.Message); }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- memory helpers
        // Mixing GlobalAlloc with a 2.x DSM's allocator is a real leak/corruption
        // source, so all container memory goes through these four.
        internal IntPtr MemAlloc(int bytes)
        {
            if (_memAllocate != null) return _memAllocate((uint)bytes);
            return NativeMethods.GlobalAlloc(NativeMethods.GHND, (UIntPtr)(uint)bytes);
        }

        internal void MemFree(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            if (_memFree != null) { _memFree(handle); return; }
            NativeMethods.GlobalFree(handle);
        }

        internal IntPtr MemLock(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            if (_memLock != null) return _memLock(handle);
            return NativeMethods.GlobalLock(handle);
        }

        internal void MemUnlock(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            if (_memUnlock != null) { _memUnlock(handle); return; }
            NativeMethods.GlobalUnlock(handle);
        }

        // ---------------------------------------------------------------- source enumeration
        public List<TW_IDENTITY> EnumerateSources()
        {
            List<TW_IDENTITY> list = new List<TW_IDENTITY>();
            if (_state < TwainState.DsmOpen) return list;

            int size = Marshal.SizeOf(typeof(TW_IDENTITY));
            IntPtr p = Marshal.AllocHGlobal(size);
            try
            {
                ZeroMemory(p, size);
                ushort rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.IDENTITY, MSG.GETFIRST, p);
                int guard = 0;
                while (rc == TWRC.SUCCESS && guard++ < 128)
                {
                    TW_IDENTITY id = (TW_IDENTITY)Marshal.PtrToStructure(p, typeof(TW_IDENTITY));
                    list.Add(id);
                    ZeroMemory(p, size);
                    rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.IDENTITY, MSG.GETNEXT, p);
                }
                if (rc != TWRC.ENDOFLIST && rc != TWRC.SUCCESS)
                    Log("IDENTITY/GETNEXT ended with rc " + rc);
            }
            catch (Exception ex) { Log("EnumerateSources failed: " + ex.Message); }
            finally { Marshal.FreeHGlobal(p); }

            Log("TWAIN sources found: " + list.Count);
            return list;
        }

        public NsResult OpenSource(string productName)
        {
            if (_state < TwainState.DsmOpen)
                return NsResult.Fail(NsError.TwainSequenceError, "DSM is not open", "Internal error - restart the scan.");
            if (_state >= TwainState.SourceOpen) return NsResult.Success();

            List<TW_IDENTITY> sources = EnumerateSources();
            if (sources.Count == 0)
                return NsResult.Fail(NsError.TwainNoDataSource,
                    "No TWAIN data sources are installed for the " + (IntPtr.Size * 8) + "-bit host.",
                    "Install the scanner's TWAIN driver, or use the WIA connection.");

            TW_IDENTITY match = default(TW_IDENTITY);
            bool found = false;
            if (!string.IsNullOrEmpty(productName))
            {
                foreach (TW_IDENTITY id in sources)
                {
                    if (string.Equals(id.ProductName, productName, StringComparison.OrdinalIgnoreCase))
                    { match = id; found = true; break; }
                }
                // Fall back to a contains-match: some drivers pad or decorate the name.
                if (!found)
                {
                    foreach (TW_IDENTITY id in sources)
                    {
                        if (id.ProductName != null &&
                            id.ProductName.IndexOf(productName, StringComparison.OrdinalIgnoreCase) >= 0)
                        { match = id; found = true; break; }
                    }
                }
            }
            if (!found) { match = sources[0]; }

            FreeIdentity(ref _dsIdPtr);
            _dsIdPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_IDENTITY)));
            Marshal.StructureToPtr(match, _dsIdPtr, false);

            ushort rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.IDENTITY, MSG.OPENDS, _dsIdPtr);
            if (rc != TWRC.SUCCESS)
            {
                ushort cc = GetStatus();
                FreeIdentity(ref _dsIdPtr);
                return NsResult.Fail(NsError.TwainOpenDsFailed,
                    "MSG_OPENDS failed for '" + match.ProductName + "' (rc " + rc + ", " + TWCC.Describe(cc) + ")",
                    cc == TWCC.MAXCONNECTIONS
                        ? "Another program is already using this scanner. Close it and try again."
                        : "Check the scanner is powered on and connected.");
            }

            _state = TwainState.SourceOpen;
            Log("Source opened: " + match.ProductName + " by " + match.Manufacturer);
            return NsResult.Success();
        }

        public TW_IDENTITY GetOpenSourceIdentity()
        {
            if (_dsIdPtr == IntPtr.Zero) return default(TW_IDENTITY);
            return (TW_IDENTITY)Marshal.PtrToStructure(_dsIdPtr, typeof(TW_IDENTITY));
        }

        // ---------------------------------------------------------------- status
        public ushort GetStatus()
        {
            if (_dsmEntry == null || _appIdPtr == IntPtr.Zero) return 0;
            TW_STATUS st = new TW_STATUS();
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_STATUS)));
            try
            {
                Marshal.StructureToPtr(st, p, false);
                // Ask the DS if one is open, otherwise the DSM.
                IntPtr dest = (_state >= TwainState.SourceOpen) ? _dsIdPtr : IntPtr.Zero;
                ushort rc = _dsmEntry(_appIdPtr, dest, DG.CONTROL, DAT.STATUS, MSG.GET, p);
                if (rc != TWRC.SUCCESS) return 0;
                st = (TW_STATUS)Marshal.PtrToStructure(p, typeof(TW_STATUS));
                LastConditionCode = st.ConditionCode;
                return st.ConditionCode;
            }
            catch { return 0; }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- capabilities
        /// <summary>Raw triplet call against the open data source.</summary>
        ushort DsEntry(uint dg, ushort dat, ushort msg, IntPtr pData)
        {
            return _dsmEntry(_appIdPtr, _dsIdPtr, dg, dat, msg, pData);
        }

        /// <summary>MSG_QUERYSUPPORT tells us which operations a capability allows.</summary>
        public bool CapIsSupported(ushort cap)
        {
            TW_CAPABILITY twCap = new TW_CAPABILITY();
            twCap.Cap = cap;
            twCap.ConType = TWON.DONTCARE16;
            twCap.hContainer = IntPtr.Zero;

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_CAPABILITY)));
            try
            {
                Marshal.StructureToPtr(twCap, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.CAPABILITY, MSG.QUERYSUPPORT, p);
                if (rc != TWRC.SUCCESS) return false;
                twCap = (TW_CAPABILITY)Marshal.PtrToStructure(p, typeof(TW_CAPABILITY));
                if (twCap.hContainer == IntPtr.Zero) return false;
                MemFree(twCap.hContainer);
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>
        /// Reads a capability container. Returns the decoded values, the current value
        /// and the type. Handles ONEVALUE, ENUMERATION, RANGE and ARRAY.
        /// </summary>
        public bool CapGet(ushort cap, ushort msg, out List<double> values, out double current, out ushort itemType)
        {
            values = new List<double>();
            current = 0;
            itemType = TWTY.UINT16;
            LastContainerWasRange = false;

            TW_CAPABILITY twCap = new TW_CAPABILITY();
            twCap.Cap = cap;
            twCap.ConType = TWON.DONTCARE16;
            twCap.hContainer = IntPtr.Zero;

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_CAPABILITY)));
            IntPtr container = IntPtr.Zero;
            IntPtr locked = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(twCap, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.CAPABILITY, msg, p);
                if (rc != TWRC.SUCCESS && rc != TWRC.CHECKSTATUS) return false;

                twCap = (TW_CAPABILITY)Marshal.PtrToStructure(p, typeof(TW_CAPABILITY));
                container = twCap.hContainer;
                if (container == IntPtr.Zero) return false;

                locked = MemLock(container);
                if (locked == IntPtr.Zero) return false;

                LastContainerType = twCap.ConType;

                switch (twCap.ConType)
                {
                    case TWON.ONEVALUE:
                    {
                        TW_ONEVALUE ov = (TW_ONEVALUE)Marshal.PtrToStructure(locked, typeof(TW_ONEVALUE));
                        itemType = ov.ItemType;
                        LastItemType = itemType;
                        double v = DecodeItem(locked + 2, ov.ItemType, 0);
                        values.Add(v);
                        current = v;
                        break;
                    }
                    case TWON.ENUMERATION:
                    {
                        TW_ENUMERATION en = (TW_ENUMERATION)Marshal.PtrToStructure(locked, typeof(TW_ENUMERATION));
                        itemType = en.ItemType;
                        LastItemType = itemType;
                        IntPtr items = locked + 14;   // 2 + 4 + 4 + 4, pack(2)
                        int n = (int)Math.Min(en.NumItems, 4096u);
                        for (int i = 0; i < n; i++)
                            values.Add(DecodeItem(items, en.ItemType, i));
                        if (en.CurrentIndex < (uint)values.Count) current = values[(int)en.CurrentIndex];
                        else if (values.Count > 0) current = values[0];
                        break;
                    }
                    case TWON.RANGE:
                    {
                        TW_RANGE rg = (TW_RANGE)Marshal.PtrToStructure(locked, typeof(TW_RANGE));
                        itemType = rg.ItemType;
                        LastItemType = itemType;
                        LastContainerWasRange = true;
                        double min = DecodeRaw(rg.MinValue, rg.ItemType);
                        double max = DecodeRaw(rg.MaxValue, rg.ItemType);
                        double step = DecodeRaw(rg.StepSize, rg.ItemType);
                        current = DecodeRaw(rg.CurrentValue, rg.ItemType);
                        // Expose the range as [min, max, step] so the caller can tell it
                        // apart from a discrete enumeration via the ConType it asked for.
                        values.Add(min);
                        values.Add(max);
                        values.Add(step <= 0 ? 1 : step);
                        break;
                    }
                    case TWON.ARRAY:
                    {
                        TW_ARRAY ar = (TW_ARRAY)Marshal.PtrToStructure(locked, typeof(TW_ARRAY));
                        itemType = ar.ItemType;
                        LastItemType = itemType;
                        IntPtr items = locked + 6;    // 2 + 4, pack(2)
                        int n = (int)Math.Min(ar.NumItems, 4096u);
                        for (int i = 0; i < n; i++)
                            values.Add(DecodeItem(items, ar.ItemType, i));
                        if (values.Count > 0) current = values[0];
                        break;
                    }
                    default:
                        return false;
                }
                return true;
            }
            catch (Exception ex) { Log("CapGet(0x" + cap.ToString("x4") + ") threw: " + ex.Message); return false; }
            finally
            {
                if (locked != IntPtr.Zero) MemUnlock(container);
                if (container != IntPtr.Zero) MemFree(container);
                Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>Convenience: current value of a capability, or def when unsupported.</summary>
        public double CapGetCurrent(ushort cap, double def)
        {
            List<double> vals; double cur; ushort type;
            if (!CapGet(cap, MSG.GETCURRENT, out vals, out cur, out type)) return def;
            return cur;
        }

        /// <summary>Returns the full supported set (enumeration or [min,max,step] range).</summary>
        public List<double> CapGetValues(ushort cap, out bool isRange)
        {
            isRange = false;
            List<double> vals; double cur; ushort type;
            if (!CapGet(cap, MSG.GET, out vals, out cur, out type)) return new List<double>();

            // A range comes back as exactly three synthesised entries; re-query the
            // container type to be sure rather than guessing from the count.
            isRange = LastContainerWasRange;
            return vals;
        }

        /// <summary>Set by CapGet so callers can distinguish RANGE from ENUMERATION.</summary>
        public bool LastContainerWasRange { get; private set; }

        /// <summary>TWON_* container type from the last CapGet, for diagnostics.</summary>
        public ushort LastContainerType { get; private set; }

        /// <summary>TWTY_* item type from the last CapGet, for diagnostics.</summary>
        public ushort LastItemType { get; private set; }

        /// <summary>Human-readable description of the last container, for the raw capability log.</summary>
        public string DescribeLastContainer()
        {
            string con;
            switch (LastContainerType)
            {
                case TWON.ONEVALUE: con = "ONEVALUE"; break;
                case TWON.ENUMERATION: con = "ENUM"; break;
                case TWON.RANGE: con = "RANGE"; break;
                case TWON.ARRAY: con = "ARRAY"; break;
                default: con = "con" + LastContainerType; break;
            }
            string ty;
            switch (LastItemType)
            {
                case TWTY.INT8: ty = "int8"; break;
                case TWTY.UINT8: ty = "uint8"; break;
                case TWTY.INT16: ty = "int16"; break;
                case TWTY.UINT16: ty = "uint16"; break;
                case TWTY.INT32: ty = "int32"; break;
                case TWTY.UINT32: ty = "uint32"; break;
                case TWTY.BOOL: ty = "bool"; break;
                case TWTY.FIX32: ty = "fix32"; break;
                case TWTY.FRAME: ty = "frame"; break;
                default: ty = "ty" + LastItemType; break;
            }
            return con + "/" + ty;
        }

        double DecodeItem(IntPtr basePtr, ushort itemType, int index)
        {
            int size = TWTY.SizeOf(itemType);
            IntPtr p = new IntPtr(basePtr.ToInt64() + (long)index * size);
            switch (itemType)
            {
                case TWTY.INT8: return (sbyte)Marshal.ReadByte(p);
                case TWTY.UINT8: return Marshal.ReadByte(p);
                case TWTY.INT16: return Marshal.ReadInt16(p);
                case TWTY.BOOL:
                case TWTY.UINT16: return (ushort)Marshal.ReadInt16(p);
                case TWTY.INT32: return Marshal.ReadInt32(p);
                case TWTY.UINT32: return (uint)Marshal.ReadInt32(p);
                case TWTY.FIX32:
                {
                    TW_FIX32 f = (TW_FIX32)Marshal.PtrToStructure(p, typeof(TW_FIX32));
                    return f.ToDouble();
                }
                default: return 0;
            }
        }

        double DecodeRaw(uint raw, ushort itemType)
        {
            switch (itemType)
            {
                case TWTY.INT8: return (sbyte)(raw & 0xFF);
                case TWTY.UINT8: return raw & 0xFF;
                case TWTY.INT16: return (short)(raw & 0xFFFF);
                case TWTY.BOOL:
                case TWTY.UINT16: return raw & 0xFFFF;
                case TWTY.INT32: return unchecked((int)raw);
                case TWTY.UINT32: return raw;
                case TWTY.FIX32: return TW_FIX32.FromRaw(raw).ToDouble();
                default: return raw;
            }
        }

        /// <summary>
        /// Sets a capability with a ONEVALUE container, then reads it back.
        /// A MSG_SET that returns SUCCESS can still have clamped the value, so the
        /// verification read is not optional (plan section 7.2).
        /// </summary>
        public bool CapSet(ushort cap, ushort itemType, double value, out double actual)
        {
            actual = value;
            int itemSize = TWTY.SizeOf(itemType);
            int containerSize = 2 + Math.Max(4, itemSize);

            IntPtr container = MemAlloc(containerSize);
            if (container == IntPtr.Zero) return false;

            IntPtr capPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_CAPABILITY)));
            try
            {
                IntPtr locked = MemLock(container);
                if (locked == IntPtr.Zero) { MemFree(container); return false; }
                try
                {
                    Marshal.WriteInt16(locked, 0, unchecked((short)itemType));
                    IntPtr itemPtr = locked + 2;
                    switch (itemType)
                    {
                        case TWTY.INT8: Marshal.WriteByte(itemPtr, unchecked((byte)(sbyte)value)); break;
                        case TWTY.UINT8: Marshal.WriteByte(itemPtr, (byte)value); break;
                        case TWTY.INT16: Marshal.WriteInt16(itemPtr, (short)value); break;
                        case TWTY.BOOL:
                        case TWTY.UINT16: Marshal.WriteInt16(itemPtr, unchecked((short)(ushort)value)); break;
                        case TWTY.INT32: Marshal.WriteInt32(itemPtr, (int)value); break;
                        case TWTY.UINT32: Marshal.WriteInt32(itemPtr, unchecked((int)(uint)value)); break;
                        case TWTY.FIX32: Marshal.WriteInt32(itemPtr, unchecked((int)TW_FIX32.FromDouble(value).ToRaw())); break;
                        default: Marshal.WriteInt32(itemPtr, (int)value); break;
                    }
                }
                finally { MemUnlock(container); }

                TW_CAPABILITY twCap = new TW_CAPABILITY();
                twCap.Cap = cap;
                twCap.ConType = TWON.ONEVALUE;
                twCap.hContainer = container;
                Marshal.StructureToPtr(twCap, capPtr, false);

                ushort rc = DsEntry(DG.CONTROL, DAT.CAPABILITY, MSG.SET, capPtr);
                bool ok = (rc == TWRC.SUCCESS || rc == TWRC.CHECKSTATUS);
                if (!ok)
                {
                    ushort cc = GetStatus();
                    Log("CapSet(0x" + cap.ToString("x4") + ", " + value.ToString("0.##", CultureInfo.InvariantCulture) +
                        ") rc=" + rc + " cc=" + TWCC.Describe(cc));
                }

                // Verify. TWRC_CHECKSTATUS explicitly means "I changed your value".
                double back = CapGetCurrent(cap, double.NaN);
                if (!double.IsNaN(back)) actual = back;
                return ok;
            }
            catch (Exception ex) { Log("CapSet threw: " + ex.Message); return false; }
            finally
            {
                MemFree(container);
                Marshal.FreeHGlobal(capPtr);
            }
        }

        public bool CapSet(ushort cap, ushort itemType, double value)
        {
            double actual;
            return CapSet(cap, itemType, value, out actual);
        }

        /// <summary>Sets ICAP_FRAMES to restrict the scan to a sub-region of the bed.</summary>
        public bool SetScanRegion(double leftIn, double topIn, double rightIn, double bottomIn)
        {
            TW_FRAME frame = new TW_FRAME();
            frame.Left = TW_FIX32.FromDouble(leftIn);
            frame.Top = TW_FIX32.FromDouble(topIn);
            frame.Right = TW_FIX32.FromDouble(rightIn);
            frame.Bottom = TW_FIX32.FromDouble(bottomIn);

            // DAT_IMAGELAYOUT is honoured far more widely than ICAP_FRAMES, so try it
            // first and only fall back to the capability when the DS refuses.
            TW_IMAGELAYOUT layout = new TW_IMAGELAYOUT();
            layout.Frame = frame;
            layout.DocumentNumber = 1;
            layout.PageNumber = 1;
            layout.FrameNumber = 1;

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_IMAGELAYOUT)));
            try
            {
                Marshal.StructureToPtr(layout, p, false);
                ushort rc = DsEntry(DG.IMAGE, DAT.IMAGELAYOUT, MSG.SET, p);
                if (rc == TWRC.SUCCESS || rc == TWRC.CHECKSTATUS)
                {
                    Log(string.Format(CultureInfo.InvariantCulture,
                        "scan region set via IMAGELAYOUT: {0:0.###},{1:0.###} -> {2:0.###},{3:0.###} in",
                        leftIn, topIn, rightIn, bottomIn));
                    return true;
                }
                Log("IMAGELAYOUT/MSG_SET rc=" + rc + " cc=" + TWCC.Describe(GetStatus()));
                return false;
            }
            catch (Exception ex) { Log("SetScanRegion threw: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- enable / disable
        public NsResult EnableSource(bool showUi, bool modal, IntPtr hwnd)
        {
            if (_state != TwainState.SourceOpen)
                return NsResult.Fail(NsError.TwainSequenceError,
                    "EnableSource called in state " + _state, "Internal error - restart the scan.");

            XferReady = false;
            CloseRequested = false;

            TW_USERINTERFACE ui = new TW_USERINTERFACE();
            ui.ShowUI = (ushort)(showUi ? 1 : 0);
            ui.ModalUI = (ushort)(modal ? 1 : 0);
            ui.hParent = hwnd;

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_USERINTERFACE)));
            try
            {
                Marshal.StructureToPtr(ui, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.USERINTERFACE, MSG.ENABLEDS, p);

                // Some sources deliver MSG_XFERREADY synchronously from ENABLEDS instead
                // of through the event loop; both are legal.
                if (rc == TWRC.SUCCESS || rc == TWRC.CHECKSTATUS)
                {
                    _state = TwainState.SourceEnabled;
                    Log("source enabled (showUi=" + showUi + ")");
                    return NsResult.Success();
                }

                ushort cc = GetStatus();
                return NsResult.Fail(NsError.TwainEnableFailed,
                    "MSG_ENABLEDS failed (rc " + rc + ", " + TWCC.Describe(cc) + ")",
                    MapRemedy(cc));
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.TwainEnableFailed, "MSG_ENABLEDS threw: " + ex.Message, "");
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        public void DisableSource()
        {
            if (_state < TwainState.SourceEnabled) return;
            TW_USERINTERFACE ui = new TW_USERINTERFACE();
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_USERINTERFACE)));
            try
            {
                Marshal.StructureToPtr(ui, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.USERINTERFACE, MSG.DISABLEDS, p);
                if (rc == TWRC.SUCCESS) _state = TwainState.SourceOpen;
                else Log("MSG_DISABLEDS rc=" + rc);
            }
            catch (Exception ex) { Log("DisableSource threw: " + ex.Message); }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- event loop
        /// <summary>
        /// Forwards a Windows message to the data source. MUST be called for every
        /// message on the thread that owns the TWAIN session, before normal dispatch.
        /// Returns true when the DS consumed the message.
        /// </summary>
        public bool ProcessEvent(ref System.Windows.Forms.Message m)
        {
            if (_state < TwainState.SourceEnabled) return false;

            // Rebuild a Win32 MSG for the DS. The managed Message struct is layout
            // compatible with MSG except it lacks the trailing time/pt fields, which
            // sources do not read, but we allocate the full size anyway to be safe.
            WinMsg wm = new WinMsg();
            wm.hwnd = m.HWnd;
            wm.message = (uint)m.Msg;
            wm.wParam = m.WParam;
            wm.lParam = m.LParam;

            IntPtr msgPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinMsg)));
            IntPtr evtPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_EVENT)));
            try
            {
                Marshal.StructureToPtr(wm, msgPtr, false);

                TW_EVENT evt = new TW_EVENT();
                evt.pEvent = msgPtr;
                evt.TWMessage = MSG.NULL;
                Marshal.StructureToPtr(evt, evtPtr, false);

                ushort rc = DsEntry(DG.CONTROL, DAT.EVENT, MSG.PROCESSEVENT, evtPtr);
                evt = (TW_EVENT)Marshal.PtrToStructure(evtPtr, typeof(TW_EVENT));

                switch (evt.TWMessage)
                {
                    case MSG.XFERREADY:
                        XferReady = true;
                        _state = TwainState.TransferReady;
                        Log("MSG_XFERREADY");
                        break;
                    case MSG.CLOSEDSREQ:
                        CloseRequested = true;
                        Log("MSG_CLOSEDSREQ");
                        break;
                    case MSG.CLOSEDSOK:
                        CloseRequested = true;
                        Log("MSG_CLOSEDSOK");
                        break;
                    case MSG.DEVICEEVENT:
                        Log("MSG_DEVICEEVENT");
                        break;
                }

                return rc == TWRC.DSEVENT;
            }
            catch { return false; }
            finally
            {
                Marshal.FreeHGlobal(msgPtr);
                Marshal.FreeHGlobal(evtPtr);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WinMsg
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        // ---------------------------------------------------------------- image info
        public bool GetImageInfo(out TW_IMAGEINFO info)
        {
            info = new TW_IMAGEINFO();
            info.BitsPerSample = new short[8];

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_IMAGEINFO)));
            try
            {
                Marshal.StructureToPtr(info, p, false);
                ushort rc = DsEntry(DG.IMAGE, DAT.IMAGEINFO, MSG.GET, p);
                if (rc != TWRC.SUCCESS) { Log("IMAGEINFO/MSG_GET rc=" + rc); return false; }
                info = (TW_IMAGEINFO)Marshal.PtrToStructure(p, typeof(TW_IMAGEINFO));
                return true;
            }
            catch (Exception ex) { Log("GetImageInfo threw: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------------------------------------------------------------- transfer
        /// <summary>
        /// Runs the transfer loop until the source reports no pending transfers.
        /// onImage is invoked once per page; returning false cancels the batch.
        /// </summary>
        public NsResult TransferAll(Func<RawImage, bool> onImage, int maxPages)
        {
            if (_state < TwainState.TransferReady)
                return NsResult.Fail(NsError.TwainSequenceError,
                    "TransferAll called in state " + _state, "Internal error - restart the scan.");

            int pageIndex = 0;
            int pending = 1;

            while (pending > 0)
            {
                TW_IMAGEINFO info;
                if (!GetImageInfo(out info))
                {
                    EndXfer(out pending);
                    return NsResult.Fail(NsError.TwainTransferFailed,
                        "Could not read image information from the scanner.", "Try scanning again.");
                }

                RawImage img = null;
                NsResult r = MemoryTransfer(info, out img);

                if (!r.Ok && r.Code == NsError.TwainTransferFailed)
                {
                    // Memory transfer is the default because it copes with any image
                    // size and with 48-bit data. Native transfer is the fallback for
                    // sources that implement TWSX_MEMORY badly or not at all.
                    Log("memory transfer failed, falling back to native DIB transfer");
                    r = NativeTransfer(out img);
                }

                if (!r.Ok)
                {
                    EndXfer(out pending);
                    return r;
                }

                img.PageIndex = pageIndex++;
                bool keepGoing = true;
                try { keepGoing = onImage(img); }
                catch (Exception ex) { Log("onImage callback threw: " + ex.Message); }

                if (!EndXfer(out pending))
                    break;

                if (!keepGoing)
                {
                    Log("caller cancelled the batch - resetting pending transfers");
                    ResetXfers();
                    break;
                }

                if (maxPages > 0 && pageIndex >= maxPages)
                {
                    Log("page cap " + maxPages + " reached - stopping");
                    ResetXfers();
                    break;
                }
            }

            return NsResult.Success();
        }

        /// <summary>Memory (strip) transfer - DAT_IMAGEMEMXFER.</summary>
        NsResult MemoryTransfer(TW_IMAGEINFO info, out RawImage image)
        {
            image = null;

            TW_SETUPMEMXFER setup;
            if (!GetSetupMemXfer(out setup))
                return NsResult.Fail(NsError.TwainTransferFailed, "DAT_SETUPMEMXFER failed", "");

            uint bufSize = setup.Preferred;
            if (bufSize == 0 || bufSize == 0xFFFFFFFF) bufSize = 65536;
            if (bufSize < setup.MinBufSize) bufSize = setup.MinBufSize;
            if (setup.MaxBufSize > 0 && setup.MaxBufSize != 0xFFFFFFFF && bufSize > setup.MaxBufSize)
                bufSize = setup.MaxBufSize;
            if (bufSize > 8 * 1024 * 1024) bufSize = 8 * 1024 * 1024;

            int width = info.ImageWidth;
            int height = info.ImageLength;
            int bpp = info.BitsPerPixel;
            int spp = info.SamplesPerPixel;

            if (width <= 0 || bpp <= 0)
                return NsResult.Fail(NsError.TwainTransferFailed,
                    "Scanner reported an invalid image size (" + width + "x" + height + " @ " + bpp + "bpp)", "");

            // ICAP_UNDEFINEDIMAGESIZE sources report -1 height and stream until done.
            bool unknownHeight = height <= 0;
            int estimatedHeight = unknownHeight ? 16384 : height;

            int channels = (spp >= 3) ? 3 : 1;
            int bitsPerChannel = (bpp == 1) ? 1 : (bpp / Math.Max(1, spp));
            if (bitsPerChannel != 1 && bitsPerChannel != 8 && bitsPerChannel != 16)
                bitsPerChannel = (bpp >= 48) ? 16 : 8;

            int dstStride = (bpp == 1)
                ? ((width + 7) / 8)
                : width * channels * (bitsPerChannel / 8);

            List<byte[]> strips = new List<byte[]>();
            int totalRows = 0;

            IntPtr buffer = MemAlloc((int)bufSize);
            if (buffer == IntPtr.Zero)
                return NsResult.Fail(NsError.TwainLowMemory, "Could not allocate a " + bufSize + " byte transfer buffer", "Close other applications and try again.");

            IntPtr memXferPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_IMAGEMEMXFER)));
            try
            {
                _state = TwainState.Transferring;
                ushort rc;
                int guard = 0;

                do
                {
                    IntPtr lockedBuf = MemLock(buffer);
                    if (lockedBuf == IntPtr.Zero)
                        return NsResult.Fail(NsError.TwainLowMemory, "Could not lock the transfer buffer", "");

                    TW_IMAGEMEMXFER mx = new TW_IMAGEMEMXFER();
                    mx.Compression = TWCP.NONE;
                    mx.BytesPerRow = 0;
                    mx.Columns = 0;
                    mx.Rows = 0;
                    mx.XOffset = 0;
                    mx.YOffset = 0;
                    mx.BytesWritten = 0;
                    mx.Memory.Flags = TWMF.APPOWNS | TWMF.POINTER;
                    mx.Memory.Length = bufSize;
                    mx.Memory.TheMem = lockedBuf;
                    Marshal.StructureToPtr(mx, memXferPtr, false);

                    rc = DsEntry(DG.IMAGE, DAT.IMAGEMEMXFER, MSG.GET, memXferPtr);
                    mx = (TW_IMAGEMEMXFER)Marshal.PtrToStructure(memXferPtr, typeof(TW_IMAGEMEMXFER));

                    if (rc == TWRC.SUCCESS || rc == TWRC.XFERDONE)
                    {
                        int rows = (int)mx.Rows;
                        int srcStride = (int)mx.BytesPerRow;
                        if (rows > 0 && srcStride > 0)
                        {
                            byte[] strip = new byte[(long)rows * dstStride <= int.MaxValue ? rows * dstStride : 0];
                            if (strip.Length == 0)
                            {
                                MemUnlock(buffer);
                                return NsResult.Fail(NsError.ImagingAllocFailed, "Image is too large to assemble in memory", "Scan at a lower resolution.");
                            }

                            for (int y = 0; y < rows; y++)
                            {
                                IntPtr srcRow = new IntPtr(lockedBuf.ToInt64() + (long)y * srcStride);
                                int copy = Math.Min(srcStride, dstStride);
                                Marshal.Copy(srcRow, strip, y * dstStride, copy);
                            }
                            strips.Add(strip);
                            totalRows += rows;
                        }
                    }

                    MemUnlock(buffer);

                    if (rc != TWRC.SUCCESS && rc != TWRC.XFERDONE)
                    {
                        if (rc == TWRC.CANCEL)
                            return NsResult.Fail(NsError.TwainCancelled, "Scan cancelled at the scanner.", "");
                        ushort cc = GetStatus();
                        return NsResult.Fail(NsError.TwainTransferFailed,
                            "DAT_IMAGEMEMXFER failed (rc " + rc + ", " + TWCC.Describe(cc) + ")", MapRemedy(cc));
                    }

                    if (totalRows > estimatedHeight + 64) break;   // runaway guard
                }
                while (rc != TWRC.XFERDONE && guard++ < 100000);

                if (totalRows == 0)
                    return NsResult.Fail(NsError.TwainTransferFailed, "The scanner returned no image rows.", "Try scanning again.");

                int finalHeight = unknownHeight ? totalRows : Math.Min(height, totalRows);

                byte[] pixels = new byte[(long)finalHeight * dstStride <= int.MaxValue ? finalHeight * dstStride : 0];
                if (pixels.Length == 0)
                    return NsResult.Fail(NsError.ImagingAllocFailed, "Image is too large to assemble in memory", "Scan at a lower resolution.");

                int offset = 0;
                foreach (byte[] strip in strips)
                {
                    int remaining = pixels.Length - offset;
                    if (remaining <= 0) break;
                    int take = Math.Min(strip.Length, remaining);
                    Buffer.BlockCopy(strip, 0, pixels, offset, take);
                    offset += take;
                }

                image = new RawImage();
                image.Pixels = pixels;
                image.Width = width;
                image.Height = finalHeight;
                image.Stride = dstStride;
                image.Channels = channels;
                image.BitsPerChannel = bitsPerChannel;
                image.XDpi = info.XResolution.ToDouble();
                image.YDpi = info.YResolution.ToDouble();

                // Memory transfer delivers RGB triplets, unlike the BGR of a Windows
                // DIB. Callers get a consistent BGR buffer, so swap here.
                if (channels == 3) DibDecoder.SwapRedBlue(image);

                Log(string.Format(CultureInfo.InvariantCulture,
                    "memory transfer complete: {0}x{1} {2}ch {3}bpc @ {4:0.#}x{5:0.#} dpi ({6} strips)",
                    width, finalHeight, channels, bitsPerChannel, image.XDpi, image.YDpi, strips.Count));
                return NsResult.Success();
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.TwainTransferFailed, "Memory transfer threw: " + ex.Message, "");
            }
            finally
            {
                MemFree(buffer);
                Marshal.FreeHGlobal(memXferPtr);
            }
        }

        bool GetSetupMemXfer(out TW_SETUPMEMXFER setup)
        {
            setup = new TW_SETUPMEMXFER();
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_SETUPMEMXFER)));
            try
            {
                Marshal.StructureToPtr(setup, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.SETUPMEMXFER, MSG.GET, p);
                if (rc != TWRC.SUCCESS) return false;
                setup = (TW_SETUPMEMXFER)Marshal.PtrToStructure(p, typeof(TW_SETUPMEMXFER));
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Native transfer - the DS hands back a Windows DIB handle.</summary>
        NsResult NativeTransfer(out RawImage image)
        {
            image = null;
            IntPtr hBitmapVar = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(hBitmapVar, IntPtr.Zero);
                _state = TwainState.Transferring;

                ushort rc = DsEntry(DG.IMAGE, DAT.IMAGENATIVEXFER, MSG.GET, hBitmapVar);
                if (rc == TWRC.CANCEL)
                    return NsResult.Fail(NsError.TwainCancelled, "Scan cancelled at the scanner.", "");
                if (rc != TWRC.XFERDONE)
                {
                    ushort cc = GetStatus();
                    return NsResult.Fail(NsError.TwainTransferFailed,
                        "DAT_IMAGENATIVEXFER failed (rc " + rc + ", " + TWCC.Describe(cc) + ")", MapRemedy(cc));
                }

                IntPtr hDib = Marshal.ReadIntPtr(hBitmapVar);
                if (hDib == IntPtr.Zero)
                    return NsResult.Fail(NsError.TwainTransferFailed, "The scanner returned an empty image handle.", "");

                try
                {
                    IntPtr dib = MemLock(hDib);
                    if (dib == IntPtr.Zero)
                        return NsResult.Fail(NsError.TwainTransferFailed, "Could not lock the returned image.", "");
                    // Size is unknown for a locked handle, so GlobalSize is used where
                    // it is meaningful and 0 (meaning "trust the header") otherwise.
                    long avail = 0;
                    try { avail = (long)NativeMethods.GlobalSize(hDib).ToUInt64(); } catch { }
                    try { return DibDecoder.Decode(dib, avail, out image); }
                    finally { MemUnlock(hDib); }
                }
                finally { MemFree(hDib); }
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.TwainTransferFailed, "Native transfer threw: " + ex.Message, "");
            }
            finally { Marshal.FreeHGlobal(hBitmapVar); }
        }

        bool EndXfer(out int pending)
        {
            pending = 0;
            TW_PENDINGXFERS px = new TW_PENDINGXFERS();
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_PENDINGXFERS)));
            try
            {
                Marshal.StructureToPtr(px, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.PENDINGXFERS, MSG.ENDXFER, p);
                if (rc != TWRC.SUCCESS) { Log("PENDINGXFERS/ENDXFER rc=" + rc); return false; }
                px = (TW_PENDINGXFERS)Marshal.PtrToStructure(p, typeof(TW_PENDINGXFERS));
                pending = px.Count;
                _state = (pending > 0) ? TwainState.TransferReady : TwainState.SourceEnabled;
                Log("end transfer, pending=" + (pending == -1 ? "unknown" : pending.ToString(CultureInfo.InvariantCulture)));
                // A count of -1 means "the source does not know" - keep going and let
                // the feeder-empty condition stop us.
                if (pending == -1) pending = 1;
                return true;
            }
            catch (Exception ex) { Log("EndXfer threw: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        void ResetXfers()
        {
            TW_PENDINGXFERS px = new TW_PENDINGXFERS();
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(TW_PENDINGXFERS)));
            try
            {
                Marshal.StructureToPtr(px, p, false);
                ushort rc = DsEntry(DG.CONTROL, DAT.PENDINGXFERS, MSG.RESET, p);
                if (rc == TWRC.SUCCESS) _state = TwainState.SourceEnabled;
            }
            catch { }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>Aborts an in-flight batch from outside the transfer loop.</summary>
        public void Cancel()
        {
            try
            {
                if (_state >= TwainState.TransferReady) ResetXfers();
                if (_state >= TwainState.SourceEnabled) DisableSource();
            }
            catch { }
        }

        internal static string MapRemedy(ushort cc)
        {
            switch (cc)
            {
                case TWCC.PAPERJAM: return "Clear the paper jam and try again.";
                case TWCC.PAPERDOUBLEFEED: return "Re-stack the pages and try again.";
                case TWCC.INTERLOCK: return "Close the scanner cover.";
                case TWCC.CHECKDEVICEONLINE: return "Switch the scanner on and check the cable.";
                case TWCC.MAXCONNECTIONS: return "Close any other scanning software and try again.";
                case TWCC.LOWMEMORY: return "Close other applications, or scan at a lower resolution.";
                case TWCC.NOMEDIA: return "Load paper into the scanner.";
                case TWCC.SEQERROR: return "Restart the scan.";
                default: return "";
            }
        }

        // ---------------------------------------------------------------- teardown
        public void CloseSource()
        {
            if (_state < TwainState.SourceOpen) return;
            if (_state >= TwainState.SourceEnabled) DisableSource();
            try
            {
                ushort rc = _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.IDENTITY, MSG.CLOSEDS, _dsIdPtr);
                if (rc == TWRC.SUCCESS) _state = TwainState.DsmOpen;
                else Log("MSG_CLOSEDS rc=" + rc);
            }
            catch (Exception ex) { Log("CloseSource threw: " + ex.Message); }
        }

        public void CloseDsm()
        {
            if (_state < TwainState.DsmOpen) return;
            if (_state >= TwainState.SourceOpen) CloseSource();

            IntPtr hwndPtr = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(hwndPtr, IntPtr.Zero);
                _dsmEntry(_appIdPtr, IntPtr.Zero, DG.CONTROL, DAT.PARENT, MSG.CLOSEDSM, hwndPtr);
                _state = TwainState.DsmLoaded;
            }
            catch (Exception ex) { Log("CloseDsm threw: " + ex.Message); }
            finally { Marshal.FreeHGlobal(hwndPtr); }
        }

        static void FreeIdentity(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try { Marshal.DestroyStructure(p, typeof(TW_IDENTITY)); } catch { }
            Marshal.FreeHGlobal(p);
            p = IntPtr.Zero;
        }

        static void ZeroMemory(IntPtr p, int bytes)
        {
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
        }

        public void Dispose()
        {
            try { CloseDsm(); } catch { }
            FreeIdentity(ref _dsIdPtr);
            FreeIdentity(ref _appIdPtr);
            if (_hDsm != IntPtr.Zero)
            {
                // Deliberately NOT calling FreeLibrary: several vendor data sources
                // spawn background threads that outlive DSM close, and unloading the
                // module under them faults the process on exit. The host process is
                // short-lived and disposable, so leaking the module is the safe trade.
                _hDsm = IntPtr.Zero;
            }
            _state = TwainState.PreSession;
        }
    }
}
