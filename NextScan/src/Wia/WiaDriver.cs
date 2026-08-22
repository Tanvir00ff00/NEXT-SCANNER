// =============================================================================
// NextScan Studio - Native WIA 2.0 driver
// Plan ref: MASTER_PLAN section 7.3.
//
// Uses IWiaTransfer + a managed IStream so scan data streams straight into our
// buffers, and enumerates FRONT/BACK child items so duplex actually works - the
// automation layer cannot do either (plan correction 3.4).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using NextScan.Core;

namespace NextScan.Wia
{
    public class WiaDriver
    {
        public Action<string> Log = delegate { };

        /// <summary>Image format negotiated with the driver for the current scan.</summary>
        public Guid TransferFormat = Guid.Empty;

        // ---------------------------------------------------------------- helpers
        static IWiaDevMgr2 CreateDevMgr()
        {
            Type t = Type.GetTypeFromCLSID(WiaConst.CLSID_WiaDevMgr2, false);
            if (t == null) return null;
            object o = Activator.CreateInstance(t);
            return o as IWiaDevMgr2;
        }

        static void Release(object com)
        {
            if (com == null) return;
            try { if (Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); }
            catch { }
        }

        /// <summary>Reads one integer property, returning def when absent.</summary>
        public static int ReadInt(IWiaPropertyStorage storage, uint propId, int def)
        {
            if (storage == null) return def;
            IntPtr spec = PropBuf.AllocSpec(propId);
            IntPtr var = PropBuf.AllocVariant();
            try
            {
                if (storage.ReadMultiple(1, spec, var) != WiaConst.S_OK) return def;
                ushort vt = PropBuf.GetVt(var);
                if (vt == PROPVARIANT.VT_EMPTY) return def;

                switch (vt)
                {
                    case PROPVARIANT.VT_I2: return (short)(PropBuf.GetInt32(var) & 0xFFFF);
                    case PROPVARIANT.VT_UI2: return PropBuf.GetInt32(var) & 0xFFFF;
                    case PROPVARIANT.VT_I4:
                    case PROPVARIANT.VT_UI4:
                    case PROPVARIANT.VT_BOOL: return PropBuf.GetInt32(var);
                    case PROPVARIANT.VT_R4: return (int)BitConverter.ToSingle(BitConverter.GetBytes(PropBuf.GetInt32(var)), 0);
                    default: return def;
                }
            }
            catch { return def; }
            finally { PropBuf.FreeVariant(var); PropBuf.FreeSpec(spec); }
        }

        public static string ReadString(IWiaPropertyStorage storage, uint propId, string def)
        {
            if (storage == null) return def;
            IntPtr spec = PropBuf.AllocSpec(propId);
            IntPtr var = PropBuf.AllocVariant();
            try
            {
                if (storage.ReadMultiple(1, spec, var) != WiaConst.S_OK) return def;
                ushort vt = PropBuf.GetVt(var);
                IntPtr sp = PropBuf.GetPtr(var);

                string s = null;
                if (vt == PROPVARIANT.VT_LPWSTR || vt == PROPVARIANT.VT_BSTR) s = Marshal.PtrToStringUni(sp);
                else if (vt == PROPVARIANT.VT_LPSTR) s = Marshal.PtrToStringAnsi(sp);
                else if (vt == PROPVARIANT.VT_I4 || vt == PROPVARIANT.VT_UI4)
                    s = PropBuf.GetInt32(var).ToString(CultureInfo.InvariantCulture);

                return string.IsNullOrEmpty(s) ? def : s;
            }
            catch { return def; }
            finally { PropBuf.FreeVariant(var); PropBuf.FreeSpec(spec); }
        }

        public static Guid ReadGuid(IWiaPropertyStorage storage, uint propId)
        {
            if (storage == null) return Guid.Empty;
            IntPtr spec = PropBuf.AllocSpec(propId);
            IntPtr var = PropBuf.AllocVariant();
            try
            {
                if (storage.ReadMultiple(1, spec, var) != WiaConst.S_OK) return Guid.Empty;
                if (PropBuf.GetVt(var) != PROPVARIANT.VT_CLSID) return Guid.Empty;
                IntPtr gp = PropBuf.GetPtr(var);
                if (gp == IntPtr.Zero) return Guid.Empty;
                byte[] g = new byte[16];
                Marshal.Copy(gp, g, 0, 16);
                return new Guid(g);
            }
            catch { return Guid.Empty; }
            finally { PropBuf.FreeVariant(var); PropBuf.FreeSpec(spec); }
        }

        /// <summary>Writes one integer property. Returns false when the driver refuses.</summary>
        public bool WriteInt(IWiaPropertyStorage storage, uint propId, int value)
        {
            if (storage == null) return false;
            IntPtr spec = PropBuf.AllocSpec(propId);
            IntPtr var = PropBuf.AllocVariant();
            try
            {
                PropBuf.SetVt(var, PROPVARIANT.VT_I4);
                PropBuf.SetInt32(var, value);

                int hr = storage.WriteMultiple(1, spec, var, WiaConst.WIA_IPA_ITEM_NAME);
                if (hr != WiaConst.S_OK)
                {
                    Log("WIA property " + propId + " = " + value + " refused (hr 0x" + hr.ToString("x8") + ")");
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Log("WriteInt(" + propId + ") threw: " + ex.Message); return false; }
            finally
            {
                // We wrote a plain VT_I4, so there is nothing for PropVariantClear to
                // release; free the buffer directly to avoid clearing a value the
                // driver may still reference.
                Marshal.FreeCoTaskMem(var);
                PropBuf.FreeSpec(spec);
            }
        }

        public bool WriteGuid(IWiaPropertyStorage storage, uint propId, Guid value)
        {
            if (storage == null) return false;
            IntPtr spec = PropBuf.AllocSpec(propId);
            IntPtr var = PropBuf.AllocVariant();
            IntPtr mem = Marshal.AllocCoTaskMem(16);
            try
            {
                Marshal.Copy(value.ToByteArray(), 0, mem, 16);
                PropBuf.SetVt(var, PROPVARIANT.VT_CLSID);
                PropBuf.SetPtr(var, mem);

                int hr = storage.WriteMultiple(1, spec, var, WiaConst.WIA_IPA_ITEM_NAME);
                if (hr != WiaConst.S_OK)
                {
                    Log("WIA GUID property " + propId + " refused (hr 0x" + hr.ToString("x8") + ")");
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Log("WriteGuid(" + propId + ") threw: " + ex.Message); return false; }
            finally
            {
                Marshal.FreeCoTaskMem(mem);
                Marshal.FreeCoTaskMem(var);
                PropBuf.FreeSpec(spec);
            }
        }

        // ---------------------------------------------------------------- probe
        public List<DeviceDescriptor> Probe()
        {
            List<DeviceDescriptor> devices = new List<DeviceDescriptor>();
            IWiaDevMgr2 mgr = null;
            IEnumWIA_DEV_INFO en = null;
            try
            {
                mgr = CreateDevMgr();
                if (mgr == null) { Log("WIA: could not create WiaDevMgr2"); return devices; }

                int hr = mgr.EnumDeviceInfo(0, out en);
                if (hr != WiaConst.S_OK || en == null)
                {
                    Log("WIA: EnumDeviceInfo failed (hr 0x" + hr.ToString("x8") + ")");
                    return devices;
                }

                while (true)
                {
                    IWiaPropertyStorage storage;
                    uint fetched;
                    hr = en.Next(1, out storage, out fetched);
                    if (hr != WiaConst.S_OK || fetched == 0 || storage == null) break;

                    try
                    {
                        string id = ReadString(storage, WiaConst.WIA_DIP_DEV_ID, "");
                        string name = ReadString(storage, WiaConst.WIA_DIP_DEV_NAME, id);
                        string vendor = ReadString(storage, WiaConst.WIA_DIP_VEND_DESC, "");
                        string desc = ReadString(storage, WiaConst.WIA_DIP_DEV_DESC, "");
                        if (string.IsNullOrEmpty(id)) continue;

                        DeviceDescriptor d = new DeviceDescriptor();
                        d.Transport = Transport.Wia;
                        d.NativeId = id;
                        d.FriendlyName = string.IsNullOrEmpty(name) ? id : name;
                        d.Manufacturer = vendor;
                        d.Model = desc;
                        d.HostBitness = IntPtr.Size * 8;
                        d.IsNetwork = id.IndexOf("wsd", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      name.IndexOf("network", StringComparison.OrdinalIgnoreCase) >= 0;
                        d.QuirkKey = "wia:" + (vendor ?? "").ToLowerInvariant() + "|" + (name ?? "").ToLowerInvariant();
                        devices.Add(d);
                    }
                    finally { Release(storage); }
                }
            }
            catch (Exception ex) { Log("WIA probe threw: " + ex.Message); }
            finally { Release(en); Release(mgr); }

            Log("WIA devices found: " + devices.Count);
            return devices;
        }

        // ---------------------------------------------------------------- capabilities
        public NsResult GetCapabilities(string deviceId, out DeviceCapabilities caps)
        {
            caps = new DeviceCapabilities();
            IWiaDevMgr2 mgr = null;
            IWiaItem2 root = null;
            try
            {
                mgr = CreateDevMgr();
                if (mgr == null)
                    return NsResult.Fail(NsError.WiaDevMgrFailed, "Windows Image Acquisition is unavailable.",
                                         "Check that the Windows Image Acquisition (WIA) service is running.");

                int hr = mgr.CreateDevice(0, deviceId, out root);
                if (hr != WiaConst.S_OK || root == null)
                    return FailFromHr(hr, "CreateDevice");

                IWiaPropertyStorage devProps = root as IWiaPropertyStorage;

                // Bed size is reported in thousandths of an inch.
                int bedW = ReadInt(devProps, WiaConst.WIA_DPS_HORIZONTAL_BED_SIZE, 0);
                int bedH = ReadInt(devProps, WiaConst.WIA_DPS_VERTICAL_BED_SIZE, 0);
                caps.PhysicalWidthIn = bedW / 1000.0;
                caps.PhysicalHeightIn = bedH / 1000.0;

                int handling = ReadInt(devProps, WiaConst.WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES, 0);
                caps.SupportsFeeder = (handling & WiaConst.FEEDER) != 0;
                caps.SupportsFlatbed = (handling & WiaConst.FLATBED) != 0 || handling == 0;
                caps.SupportsDuplex = (handling & WiaConst.DUPLEX) != 0;
                caps.RawCapabilityLog.Add("WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES = 0x" + handling.ToString("x"));

                int optX = ReadInt(devProps, WiaConst.WIA_DPS_OPTICAL_XRES, 0);
                if (optX > 0) caps.MaxResolution = optX;

                // Item-level scan properties live on the child items, not the root.
                List<IWiaItem2> children = EnumerateChildren(root, IntPtr.Zero);
                try
                {
                    foreach (IWiaItem2 child in children)
                    {
                        Guid category;
                        if (child.GetItemCategory(out category) != WiaConst.S_OK) continue;

                        if (category == WiaConst.WIA_CATEGORY_FLATBED) { caps.SupportsFlatbed = true; AddSource(caps, PaperSource.Flatbed); }
                        else if (category == WiaConst.WIA_CATEGORY_FEEDER) { caps.SupportsFeeder = true; AddSource(caps, PaperSource.Feeder); }
                        else if (category == WiaConst.WIA_CATEGORY_FILM) { caps.SupportsFilm = true; AddSource(caps, PaperSource.Film); }

                        IWiaPropertyStorage itemProps = child as IWiaPropertyStorage;
                        if (itemProps != null && caps.Resolutions.Count == 0)
                            ReadResolutionRange(itemProps, caps);
                    }
                }
                finally { foreach (IWiaItem2 c in children) Release(c); }

                if (caps.Sources.Count == 0) caps.Sources.Add(PaperSource.Flatbed);
                if (caps.SupportsDuplex) AddSource(caps, PaperSource.FeederDuplex);

                // WIA exposes intents rather than an explicit pixel-type list; every
                // scanner supports these three.
                caps.ColorModes.Add(ColorMode.Color24);
                caps.ColorModes.Add(ColorMode.Gray8);
                caps.ColorModes.Add(ColorMode.BlackWhite1);

                if (caps.Resolutions.Count == 0)
                {
                    int[] ladder = { 75, 100, 150, 200, 300, 400, 600, 1200, 2400, 4800 };
                    foreach (int r in ladder)
                        if (r <= caps.MaxResolution) caps.Resolutions.Add(r);
                    if (caps.Resolutions.Count == 0) caps.Resolutions.Add(300);
                }

                caps.SupportsHiddenUi = true;
                return NsResult.Success();
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.WiaPropertyFailed, "WIA capability query failed: " + ex.Message, "");
            }
            finally { Release(root); Release(mgr); }
        }

        static void AddSource(DeviceCapabilities caps, PaperSource s)
        {
            if (!caps.Sources.Contains(s)) caps.Sources.Add(s);
        }

        void ReadResolutionRange(IWiaPropertyStorage props, DeviceCapabilities caps)
        {
            int cur = ReadInt(props, WiaConst.WIA_IPS_XRES, 0);
            if (cur > 0) caps.RawCapabilityLog.Add("WIA_IPS_XRES current = " + cur);
        }

        // ---------------------------------------------------------------- scan
        public NsResult Scan(string deviceId, ScanSettings settings, Func<RawImage, bool> onImage)
        {
            IWiaDevMgr2 mgr = null;
            IWiaItem2 root = null;
            List<IWiaItem2> children = new List<IWiaItem2>();

            try
            {
                mgr = CreateDevMgr();
                if (mgr == null)
                    return NsResult.Fail(NsError.WiaDevMgrFailed, "Windows Image Acquisition is unavailable.",
                                         "Check that the Windows Image Acquisition (WIA) service is running.");

                int hr = mgr.CreateDevice(0, deviceId, out root);
                if (hr != WiaConst.S_OK || root == null) return FailFromHr(hr, "CreateDevice");

                bool wantFeeder = settings.Source == PaperSource.Feeder || settings.Source == PaperSource.FeederDuplex;

                // Tell the device which paper path to use before selecting the item.
                IWiaPropertyStorage devProps = root as IWiaPropertyStorage;
                int handlingSelect = wantFeeder ? WiaConst.FEEDER : WiaConst.FLATBED;
                if (settings.Source == PaperSource.FeederDuplex || settings.Duplex) handlingSelect |= WiaConst.DUPLEX;
                WriteInt(devProps, WiaConst.WIA_DPS_DOCUMENT_HANDLING_SELECT, handlingSelect);

                if (wantFeeder && settings.PageCount > 0)
                    WriteInt(devProps, WiaConst.WIA_IPS_PAGES, settings.PageCount);

                children = EnumerateChildren(root, IntPtr.Zero);
                if (children.Count == 0)
                    return NsResult.Fail(NsError.WiaCreateItemFailed,
                        "The scanner reported no scannable items.", "Try the TWAIN connection for this device.");

                IWiaItem2 target = PickItem(children, settings.Source);
                if (target == null)
                    return NsResult.Fail(NsError.WiaCreateItemFailed,
                        "No item matching the selected paper source.", "Choose a different source.");

                IWiaPropertyStorage itemProps = target as IWiaPropertyStorage;

                Guid pickedCat;
                target.GetItemCategory(out pickedCat);
                int pickedType;
                target.GetItemType(out pickedType);
                Log("WIA item '" + ReadString(itemProps, WiaConst.WIA_IPA_ITEM_NAME, "?") +
                    "' category=" + pickedCat + " type=0x" + pickedType.ToString("x"));

                ApplySettings(itemProps, settings);

                IWiaTransfer transfer = target as IWiaTransfer;
                if (transfer == null)
                    return NsResult.Fail(NsError.WiaTransferFailed,
                        "The selected item does not support image transfer.", "Try the TWAIN connection for this device.");

                WiaTransferSink sink = new WiaTransferSink();
                sink.Log = Log;

                // ACQUIRE_CHILDREN is what makes a duplex/ADF batch return every page
                // instead of only the first.
                int flags = wantFeeder ? WiaConst.WIA_TRANSFER_ACQUIRE_CHILDREN : 0;
                hr = transfer.Download(flags, sink);

                if (hr != WiaConst.S_OK && sink.Streams.Count == 0)
                    return FailFromHr(hr, "Download");

                if (sink.Streams.Count == 0)
                    return NsResult.Fail(NsError.WiaTransferFailed, "The scanner returned no pages.", "Try scanning again.");

                int page = 0;
                foreach (MemoryIStream ms in sink.Streams)
                {
                    byte[] data = ms.ToArray();
                    if (data == null || data.Length < 54) { Log("skipping empty stream for page " + page); continue; }

                    RawImage img;
                    NsResult dr = DecodePage(data, out img);
                    if (!dr.Ok) { Log("page " + page + " decode failed: " + dr); continue; }

                    img.PageIndex = page;
                    // ADF duplex interleaves front/back; odd indices are back sides.
                    img.Side = (settings.Duplex || settings.Source == PaperSource.FeederDuplex) ? (page % 2) : 0;
                    if (img.XDpi < 2) { img.XDpi = settings.Dpi; img.YDpi = settings.Dpi; }
                    page++;

                    bool keepGoing = true;
                    try { keepGoing = onImage(img); }
                    catch (Exception ex) { Log("onImage threw: " + ex.Message); }
                    if (!keepGoing) break;
                }

                if (page == 0)
                    return NsResult.Fail(NsError.WiaTransferFailed,
                        "The scanner returned data that could not be decoded.", "Try the TWAIN connection for this device.");

                return NsResult.Success();
            }
            catch (COMException cex) { return FailFromHr(cex.ErrorCode, "scan"); }
            catch (Exception ex) { return NsResult.Fail(NsError.WiaTransferFailed, "WIA scan failed: " + ex.Message, ""); }
            finally
            {
                foreach (IWiaItem2 c in children) Release(c);
                Release(root);
                Release(mgr);
            }
        }

        /// <summary>
        /// Decodes one transferred page. BMP goes through our own decoder so the
        /// bit depth and palette handling match the TWAIN path exactly; the
        /// compressed formats fall back to GDI+, which is acceptable because we only
        /// reach them when the driver refused every lossless option.
        /// </summary>
        NsResult DecodePage(byte[] data, out RawImage img)
        {
            img = null;

            bool looksLikeBmp = data.Length > 2 && data[0] == 'B' && data[1] == 'M';
            if (TransferFormat == WiaConst.WiaImgFmt_BMP || looksLikeBmp)
                return DibDecoder.DecodeBmpFile(data, out img);

            // MEMORYBMP is a packed DIB with no 14-byte file header.
            if (TransferFormat == WiaConst.WiaImgFmt_MEMORYBMP)
            {
                GCHandle h = GCHandle.Alloc(data, GCHandleType.Pinned);
                try { return DibDecoder.Decode(h.AddrOfPinnedObject(), data.Length, out img); }
                finally { h.Free(); }
            }

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (System.Drawing.Image gdi = System.Drawing.Image.FromStream(ms))
                using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(gdi))
                {
                    img = RawImage.FromBitmap(bmp);
                    return img != null
                        ? NsResult.Success()
                        : NsResult.Fail(NsError.ImagingUnsupportedFormat, "Could not convert the scanned page.", "");
                }
            }
            catch (Exception ex)
            {
                return NsResult.Fail(NsError.ImagingUnsupportedFormat,
                    "Could not decode the scanned page (" + TransferFormat + "): " + ex.Message, "");
            }
        }

        void ApplySettings(IWiaPropertyStorage props, ScanSettings settings)
        {
            if (props == null) return;

            // Data type first: depth and the extent limits are all scoped to it.
            int dataType;
            switch (settings.Mode)
            {
                case ColorMode.BlackWhite1: dataType = WiaConst.WIA_DATA_THRESHOLD; break;
                case ColorMode.Gray8:
                case ColorMode.Gray16: dataType = WiaConst.WIA_DATA_GRAYSCALE; break;
                default: dataType = WiaConst.WIA_DATA_COLOR; break;
            }
            WriteInt(props, WiaConst.WIA_IPA_DATATYPE, dataType);

            WriteInt(props, WiaConst.WIA_IPS_XRES, settings.Dpi);
            WriteInt(props, WiaConst.WIA_IPS_YRES, settings.Dpi);

            // Extents are in pixels at the resolution just set, so they must be
            // written after it - writing them first makes the driver clamp against
            // the old resolution and silently crop the scan.
            if (settings.HasRegion)
            {
                int x = (int)Math.Round(settings.RegionLeftIn * settings.Dpi);
                int y = (int)Math.Round(settings.RegionTopIn * settings.Dpi);
                int w = (int)Math.Round(settings.RegionWidthIn * settings.Dpi);
                int h = (int)Math.Round(settings.RegionHeightIn * settings.Dpi);
                WriteInt(props, WiaConst.WIA_IPS_XPOS, Math.Max(0, x));
                WriteInt(props, WiaConst.WIA_IPS_YPOS, Math.Max(0, y));
                WriteInt(props, WiaConst.WIA_IPS_XEXTENT, Math.Max(1, w));
                WriteInt(props, WiaConst.WIA_IPS_YEXTENT, Math.Max(1, h));
            }

            if (Math.Abs(settings.Brightness) > 0.001)
                WriteInt(props, WiaConst.WIA_IPS_BRIGHTNESS, (int)Math.Round(settings.Brightness));
            if (Math.Abs(settings.Contrast) > 0.001)
                WriteInt(props, WiaConst.WIA_IPS_CONTRAST, (int)Math.Round(settings.Contrast));

            // Transfer medium and format.
            //
            // TYMED_CALLBACK is the WIA 1.0 band-transfer mechanism and is NOT what
            // IWiaTransfer::Download wants; asking for it makes Download fail with
            // E_INVALIDARG even though every property write reports success.
            // WIA 2.0 delivers a complete file into the stream from GetNextStream,
            // which is TYMED_FILE.
            //
            // TYMED must also be set BEFORE FORMAT, because the set of legal formats
            // is scoped to the medium.
            WriteInt(props, WiaConst.WIA_IPA_TYMED, WiaConst.TYMED_FILE);

            // Drivers disagree about which formats they accept, so negotiate rather
            // than assume. Lossless first: BMP and PNG round-trip exactly, JPEG is a
            // last resort that costs quality.
            Guid[] preferred =
            {
                WiaConst.WiaImgFmt_BMP,
                WiaConst.WiaImgFmt_PNG,
                WiaConst.WiaImgFmt_TIFF,
                WiaConst.WiaImgFmt_JPEG
            };

            TransferFormat = Guid.Empty;
            foreach (Guid g in preferred)
            {
                if (WriteGuid(props, WiaConst.WIA_IPA_FORMAT, g)) { TransferFormat = g; break; }
            }
            if (TransferFormat == Guid.Empty)
            {
                // Nothing we asked for was accepted - take whatever it is already on
                // and let the decoder work it out.
                TransferFormat = ReadGuid(props, WiaConst.WIA_IPA_FORMAT);
                Log("no preferred WIA format accepted; falling back to " + TransferFormat);
            }

            Log("WIA transfer configured: tymed=" + ReadInt(props, WiaConst.WIA_IPA_TYMED, -1) +
                " format=" + TransferFormat);
        }

        static IWiaItem2 PickItem(List<IWiaItem2> children, PaperSource source)
        {
            Guid want;
            switch (source)
            {
                case PaperSource.Feeder:
                case PaperSource.FeederDuplex: want = WiaConst.WIA_CATEGORY_FEEDER; break;
                case PaperSource.Film: want = WiaConst.WIA_CATEGORY_FILM; break;
                case PaperSource.Flatbed: want = WiaConst.WIA_CATEGORY_FLATBED; break;
                default: want = Guid.Empty; break;
            }

            if (want != Guid.Empty)
            {
                foreach (IWiaItem2 c in children)
                {
                    Guid cat;
                    if (c.GetItemCategory(out cat) == WiaConst.S_OK && cat == want) return c;
                }
            }

            // Fall back to the first transferable image item.
            foreach (IWiaItem2 c in children)
            {
                int type;
                if (c.GetItemType(out type) == WiaConst.S_OK &&
                    (type & WiaConst.WiaItemTypeImage) != 0) return c;
            }
            return children.Count > 0 ? children[0] : null;
        }

        List<IWiaItem2> EnumerateChildren(IWiaItem2 parent, IntPtr categoryPtr)
        {
            List<IWiaItem2> items = new List<IWiaItem2>();
            IEnumWiaItem2 en = null;
            try
            {
                int hr = parent.EnumChildItems(categoryPtr, out en);
                if (hr != WiaConst.S_OK || en == null) return items;

                while (true)
                {
                    IWiaItem2 child;
                    uint fetched;
                    hr = en.Next(1, out child, out fetched);
                    if (hr != WiaConst.S_OK || fetched == 0 || child == null) break;
                    items.Add(child);
                    if (items.Count > 64) break;   // runaway guard
                }
            }
            catch (Exception ex) { Log("EnumerateChildren threw: " + ex.Message); }
            finally { Release(en); }
            return items;
        }

        static NsResult FailFromHr(int hr, string where)
        {
            NsError code;
            string msg;
            string remedy;

            switch (hr)
            {
                case WiaConst.WIA_ERROR_PAPER_JAM:
                    code = NsError.WiaPaperJam; msg = "Paper jam."; remedy = "Clear the jam and try again."; break;
                case WiaConst.WIA_ERROR_PAPER_EMPTY:
                    code = NsError.WiaFeederEmpty; msg = "The document feeder is empty."; remedy = "Load pages into the feeder."; break;
                case WiaConst.WIA_ERROR_PAPER_PROBLEM:
                    code = NsError.WiaPaperJam; msg = "Paper feed problem."; remedy = "Re-stack the pages and try again."; break;
                case WiaConst.WIA_ERROR_OFFLINE:
                    code = NsError.WiaOffline; msg = "The scanner is offline."; remedy = "Switch it on and check the cable."; break;
                case WiaConst.WIA_ERROR_BUSY:
                    code = NsError.WiaBusy; msg = "The scanner is busy."; remedy = "Wait for the current job to finish, then try again."; break;
                case WiaConst.WIA_ERROR_WARMING_UP:
                    code = NsError.WiaBusy; msg = "The scanner lamp is warming up."; remedy = "Try again in a few seconds."; break;
                case WiaConst.WIA_ERROR_COVER_OPEN:
                    code = NsError.WiaCoverOpen; msg = "The scanner cover is open."; remedy = "Close the cover."; break;
                case WiaConst.WIA_ERROR_USER_INTERVENTION:
                    code = NsError.WiaOffline; msg = "The scanner needs attention."; remedy = "Check the scanner's display for an error."; break;
                case WiaConst.WIA_ERROR_DEVICE_COMMUNICATION:
                    code = NsError.WiaOffline; msg = "Lost communication with the scanner."; remedy = "Reconnect the cable and try again."; break;
                case WiaConst.WIA_ERROR_DEVICE_LOCKED:
                    code = NsError.WiaBusy; msg = "Another program is using the scanner."; remedy = "Close it and try again."; break;
                case WiaConst.WIA_ERROR_EXCEPTION_IN_DRIVER:
                    code = NsError.WiaTransferFailed; msg = "The scanner driver faulted."; remedy = "Try the TWAIN connection for this device."; break;
                case WiaConst.WIA_ERROR_LAMP_OFF:
                    code = NsError.WiaOffline; msg = "The scanner lamp is off."; remedy = "Switch the scanner off and on again."; break;
                default:
                    if (hr == unchecked((int)0x80070005))
                    { code = NsError.WiaTransferFailed; msg = "Access denied by the scanner driver."; remedy = "Try running without other scanning software open."; }
                    else
                    { code = NsError.WiaTransferFailed; msg = where + " failed (hr 0x" + hr.ToString("x8") + ")"; remedy = ""; }
                    break;
            }

            NsResult r = NsResult.Fail(code, msg, remedy);
            r.DeviceCode = "0x" + hr.ToString("x8");
            return r;
        }
    }

    // ------------------------------------------------------------------ transfer sink
    /// <summary>
    /// Receives the scan. GetNextStream is called once per page, so collecting the
    /// streams here is what makes multi-page ADF batches work.
    /// </summary>
    public class WiaTransferSink : IWiaTransferCallback
    {
        public Action<string> Log = delegate { };
        public readonly List<MemoryIStream> Streams = new List<MemoryIStream>();
        public int LastPercent;
        public bool Cancelled;

        public int TransferCallback(int lFlags, ref WiaTransferParams p)
        {
            switch (p.lMessage)
            {
                case WiaConst.IT_MSG_STATUS:
                    LastPercent = p.lPercentComplete;
                    break;
                case WiaConst.IT_MSG_NEW_PAGE:
                    Log("WIA page boundary at " + p.ulTransferredBytes + " bytes");
                    break;
                case WiaConst.IT_MSG_TERMINATION:
                    Log("WIA transfer complete: " + Streams.Count + " stream(s), " + p.ulTransferredBytes + " bytes");
                    break;
            }
            return Cancelled ? unchecked((int)0x80004004) /* E_ABORT */ : WiaConst.S_OK;
        }

        public int GetNextStream(int lFlags, string bstrItemName, string bstrFullItemName, out IStream ppDestination)
        {
            MemoryIStream ms = new MemoryIStream();
            Streams.Add(ms);
            ppDestination = ms;
            Log("WIA stream requested for '" + bstrItemName + "' (page " + Streams.Count + ")");
            return WiaConst.S_OK;
        }
    }

    /// <summary>
    /// Minimal in-memory IStream. WIA writes sequentially and occasionally seeks
    /// back to patch the BMP header, both of which this supports.
    /// </summary>
    public class MemoryIStream : IStream
    {
        readonly MemoryStream _ms = new MemoryStream();

        public byte[] ToArray() { return _ms.ToArray(); }
        public long Length { get { return _ms.Length; } }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int read = _ms.Read(pv, 0, cb);
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, read);
        }

        public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        {
            _ms.Write(pv, 0, cb);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt32(pcbWritten, cb);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            SeekOrigin origin;
            switch (dwOrigin)
            {
                case 1: origin = SeekOrigin.Current; break;
                case 2: origin = SeekOrigin.End; break;
                default: origin = SeekOrigin.Begin; break;
            }
            long pos = _ms.Seek(dlibMove, origin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, pos);
        }

        public void SetSize(long libNewSize) { _ms.SetLength(libNewSize); }

        public void Stat(out System.Runtime.InteropServices.ComTypes.STATSTG pstatstg, int grfStatFlag)
        {
            pstatstg = new System.Runtime.InteropServices.ComTypes.STATSTG();
            pstatstg.cbSize = _ms.Length;
            pstatstg.type = 2;   // STGTY_STREAM
        }

        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
        {
            byte[] buf = new byte[Math.Min(cb, 81920)];
            long copied = 0;
            while (copied < cb)
            {
                int want = (int)Math.Min(buf.Length, cb - copied);
                int got = _ms.Read(buf, 0, want);
                if (got <= 0) break;
                pstm.Write(buf, got, IntPtr.Zero);
                copied += got;
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt64(pcbRead, copied);
            if (pcbWritten != IntPtr.Zero) Marshal.WriteInt64(pcbWritten, copied);
        }

        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) { }
        public void UnlockRegion(long libOffset, long cb, int dwLockType) { }
        public void Clone(out IStream ppstm) { ppstm = null; }
    }
}
