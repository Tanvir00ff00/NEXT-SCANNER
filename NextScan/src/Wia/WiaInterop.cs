// =============================================================================
// NextScan Studio - Native WIA 2.0 COM interop
// Plan ref: MASTER_PLAN section 7.3 + correction 3.4.
//
// IMPORTANT: this deliberately does NOT use wiaaut.dll (the WIA Automation
// Layer). That layer is built on WIA 1.0 semantics and CANNOT return the back
// side of a duplex scan - Microsoft documents this as by-design in KB2709992.
// Everything here is raw vtable interop against IWiaDevMgr2 / IWiaItem2.
//
// All GUIDs and vtable orderings below were read from the Windows SDK headers
// (wia_lh.h, wiadef.h), not from memory. Method DECLARATION ORDER defines the
// vtable slot for each entry - never reorder or remove one, even if unused.
// =============================================================================
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace NextScan.Wia
{
    // ------------------------------------------------------------------ constants
    public static class WiaConst
    {
        // Device property IDs
        public const uint WIA_DIP_DEV_ID = 2;
        public const uint WIA_DIP_VEND_DESC = 3;
        public const uint WIA_DIP_DEV_NAME = 7;
        public const uint WIA_DIP_DEV_DESC = 8;

        // Scanner device properties
        public const uint WIA_DPS_HORIZONTAL_BED_SIZE = 3074;
        public const uint WIA_DPS_VERTICAL_BED_SIZE = 3075;
        public const uint WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES = 3086;
        public const uint WIA_DPS_DOCUMENT_HANDLING_STATUS = 3087;
        public const uint WIA_DPS_DOCUMENT_HANDLING_SELECT = 3088;
        public const uint WIA_DPS_OPTICAL_XRES = 3091;
        public const uint WIA_DPS_OPTICAL_YRES = 3092;

        // Item properties
        public const uint WIA_IPA_ITEM_NAME = 4098;
        public const uint WIA_IPA_FULL_ITEM_NAME = 4099;
        public const uint WIA_IPA_DATATYPE = 4103;
        public const uint WIA_IPA_DEPTH = 4104;
        public const uint WIA_IPA_FORMAT = 4106;
        public const uint WIA_IPA_TYMED = 4108;
        public const uint WIA_IPA_CHANNELS_PER_PIXEL = 4109;
        public const uint WIA_IPA_BITS_PER_CHANNEL = 4110;
        public const uint WIA_IPA_PIXELS_PER_LINE = 4112;
        public const uint WIA_IPA_BYTES_PER_LINE = 4113;
        public const uint WIA_IPA_NUMBER_OF_LINES = 4114;
        public const uint WIA_IPA_ITEM_SIZE = 4116;
        public const uint WIA_IPA_ITEM_CATEGORY = 4125;

        // Scan parameters
        public const uint WIA_IPS_CUR_INTENT = 6146;
        public const uint WIA_IPS_XRES = 6147;
        public const uint WIA_IPS_YRES = 6148;
        public const uint WIA_IPS_XPOS = 6149;
        public const uint WIA_IPS_YPOS = 6150;
        public const uint WIA_IPS_XEXTENT = 6151;
        public const uint WIA_IPS_YEXTENT = 6152;
        public const uint WIA_IPS_BRIGHTNESS = 6154;
        public const uint WIA_IPS_CONTRAST = 6155;
        public const uint WIA_IPS_PAGES = 3096;
        public const uint WIA_IPS_DOCUMENT_HANDLING_SELECT = 3088;
        public const uint WIA_IPS_PREVIEW = 3100;

        // WIA_IPA_DATATYPE values
        public const int WIA_DATA_THRESHOLD = 0;
        public const int WIA_DATA_DITHER = 1;
        public const int WIA_DATA_GRAYSCALE = 2;
        public const int WIA_DATA_COLOR = 3;

        // WIA_IPA_TYMED values. The low ones come from the OLE TYMED enum in
        // objidl.h, where FILE is 2 - 1 is TYMED_HGLOBAL. Writing 1 here is
        // rejected, and the scan then only succeeds if the driver's default
        // happened to be right.
        public const int TYMED_HGLOBAL = 1;
        public const int TYMED_FILE = 2;
        public const int TYMED_ISTREAM = 4;
        public const int TYMED_CALLBACK = 128;
        public const int TYMED_MULTIPAGE_FILE = 256;
        public const int TYMED_MULTIPAGE_CALLBACK = 512;

        // Document handling select flags
        public const int FEEDER = 0x001;
        public const int FLATBED = 0x002;
        public const int DUPLEX = 0x004;
        public const int FRONT_FIRST = 0x008;
        public const int BACK_FIRST = 0x010;

        // Document handling status
        public const int FEED_READY = 0x001;

        // IWiaTransferCallback messages
        public const int IT_MSG_DATA_HEADER = 0x0001;
        public const int IT_MSG_DATA = 0x0002;
        public const int IT_MSG_STATUS = 0x0003;
        public const int IT_MSG_TERMINATION = 0x0004;
        public const int IT_MSG_NEW_PAGE = 0x0005;

        public const int IT_STATUS_TRANSFER_FROM_DEVICE = 0x0001;
        public const int IT_STATUS_PROCESSING_DATA = 0x0002;
        public const int IT_STATUS_TRANSFER_TO_CLIENT = 0x0004;

        // IWiaTransfer::Download flags
        public const int WIA_TRANSFER_ACQUIRE_CHILDREN = 0x00000001;

        // Item type flags
        public const int WiaItemTypeImage = 0x00000010;
        public const int WiaItemTypeFolder = 0x00000004;
        public const int WiaItemTypeTransfer = 0x00000100;
        public const int WiaItemTypeProgrammableDataSource = 0x00000400;

        // Category GUIDs (wiadef.h)
        public static readonly Guid WIA_CATEGORY_FINISHED_FILE = new Guid("ff2b77ca-cf84-432b-a735-3a130dde2a88");
        public static readonly Guid WIA_CATEGORY_FLATBED = new Guid("fb607b1f-43f3-488b-855b-fb703ec342a6");
        public static readonly Guid WIA_CATEGORY_FEEDER = new Guid("fe131934-f84c-42ad-8da4-6129cddd7288");
        public static readonly Guid WIA_CATEGORY_FILM = new Guid("fcf65be7-3ce3-4473-af85-f5d37d21b68a");
        public static readonly Guid WIA_CATEGORY_ROOT = new Guid("f193526f-59b8-4a26-9888-e16e4f97ce10");
        public static readonly Guid WIA_CATEGORY_FEEDER_FRONT = new Guid("4823175c-3b28-487b-a7e6-eebc17614fd1");
        public static readonly Guid WIA_CATEGORY_FEEDER_BACK = new Guid("61ca74d4-39db-42aa-89b1-8c19c9cd4c23");

        // Image format GUIDs
        public static readonly Guid WiaImgFmt_BMP = new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e");
        public static readonly Guid WiaImgFmt_MEMORYBMP = new Guid("b96b3caa-0728-11d3-9d7b-0000f81ef32e");
        public static readonly Guid WiaImgFmt_PNG = new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e");
        public static readonly Guid WiaImgFmt_JPEG = new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e");
        public static readonly Guid WiaImgFmt_TIFF = new Guid("b96b3cb1-0728-11d3-9d7b-0000f81ef32e");
        public static readonly Guid WiaImgFmt_RAWRGB = new Guid("bca48b55-f272-4371-b0f1-4a150d057bb4");

        public static readonly Guid CLSID_WiaDevMgr2 = new Guid("B6C292BC-7C88-41ee-8B54-8EC92617E599");

        // WIA HRESULTs (FACILITY_WIA = 33)
        public const int WIA_ERROR_GENERAL_ERROR = unchecked((int)0x80210001);
        public const int WIA_ERROR_PAPER_JAM = unchecked((int)0x80210002);
        public const int WIA_ERROR_PAPER_EMPTY = unchecked((int)0x80210003);
        public const int WIA_ERROR_PAPER_PROBLEM = unchecked((int)0x80210004);
        public const int WIA_ERROR_OFFLINE = unchecked((int)0x80210005);
        public const int WIA_ERROR_BUSY = unchecked((int)0x80210006);
        public const int WIA_ERROR_WARMING_UP = unchecked((int)0x80210007);
        public const int WIA_ERROR_USER_INTERVENTION = unchecked((int)0x80210008);
        public const int WIA_ERROR_ITEM_DELETED = unchecked((int)0x80210009);
        public const int WIA_ERROR_DEVICE_COMMUNICATION = unchecked((int)0x8021000A);
        public const int WIA_ERROR_INVALID_COMMAND = unchecked((int)0x8021000B);
        public const int WIA_ERROR_INCORRECT_HARDWARE_SETTING = unchecked((int)0x8021000C);
        public const int WIA_ERROR_DEVICE_LOCKED = unchecked((int)0x8021000D);
        public const int WIA_ERROR_EXCEPTION_IN_DRIVER = unchecked((int)0x8021000E);
        public const int WIA_ERROR_INVALID_DRIVER_RESPONSE = unchecked((int)0x8021000F);
        public const int WIA_ERROR_COVER_OPEN = unchecked((int)0x80210010);
        public const int WIA_ERROR_LAMP_OFF = unchecked((int)0x80210011);
        public const int WIA_ERROR_DESTINATION = unchecked((int)0x80210012);
        public const int WIA_ERROR_NETWORK_RESERVATION_FAILED = unchecked((int)0x80210013);
        public const int WIA_ERROR_MULTI_FEED = unchecked((int)0x80210014);

        public const int S_OK = 0;
        public const int S_FALSE = 1;
    }

    // ------------------------------------------------------------------ PROPSPEC / PROPVARIANT
    /// <summary>
    /// PROPSPEC. The union member is pointer-sized, so sequential layout produces
    /// the correct 8 bytes on x86 and 16 on x64 without explicit offsets.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPSPEC
    {
        public uint ulKind;      // 0 = PRSPEC_PROPID, 1 = PRSPEC_LPWSTR
        public IntPtr union;     // holds the PROPID in its low 32 bits

        public static PROPSPEC FromId(uint propId)
        {
            PROPSPEC ps;
            ps.ulKind = 1;               // PRSPEC_PROPID - see PropBuf.AllocSpec
            ps.union = new IntPtr(propId);
            return ps;
        }
    }

    /// <summary>
    /// PROPVARIANT, laid out as an 8-byte header plus two pointer-sized union slots:
    /// 16 bytes on x86, 24 on x64, which matches the native definition on both.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr p2;

        public const ushort VT_EMPTY = 0;
        public const ushort VT_I2 = 2;
        public const ushort VT_I4 = 3;
        public const ushort VT_R4 = 4;
        public const ushort VT_R8 = 5;
        public const ushort VT_BSTR = 8;
        public const ushort VT_BOOL = 11;
        public const ushort VT_UI2 = 18;
        public const ushort VT_UI4 = 19;
        public const ushort VT_LPSTR = 30;
        public const ushort VT_LPWSTR = 31;
        public const ushort VT_CLSID = 72;

        public static PROPVARIANT FromInt(int value)
        {
            PROPVARIANT pv = new PROPVARIANT();
            pv.vt = VT_I4;
            pv.p = new IntPtr(value);
            return pv;
        }

        public int AsInt()
        {
            switch (vt)
            {
                case VT_I2: return (short)(p.ToInt64() & 0xFFFF);
                case VT_UI2: return (int)(p.ToInt64() & 0xFFFF);
                case VT_I4:
                case VT_UI4:
                case VT_BOOL: return unchecked((int)p.ToInt64());
                case VT_R4: return (int)BitConverter.ToSingle(BitConverter.GetBytes(p.ToInt64()), 0);
                case VT_R8: return (int)BitConverter.Int64BitsToDouble(p.ToInt64());
                default: return 0;
            }
        }

        public string AsString()
        {
            try
            {
                if (vt == VT_LPWSTR || vt == VT_BSTR) return Marshal.PtrToStringUni(p);
                if (vt == VT_LPSTR) return Marshal.PtrToStringAnsi(p);
                if (vt == VT_CLSID && p != IntPtr.Zero)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(p, g, 0, 16);
                    return new Guid(g).ToString();
                }
                if (vt == VT_I4 || vt == VT_UI4) return AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return "";
        }

        public Guid AsGuid()
        {
            try
            {
                if (vt == VT_CLSID && p != IntPtr.Zero)
                {
                    byte[] g = new byte[16];
                    Marshal.Copy(p, g, 0, 16);
                    return new Guid(g);
                }
            }
            catch { }
            return Guid.Empty;
        }
    }

    // ------------------------------------------------------------------ interfaces
    [ComImport, Guid("79C07CF1-CBDD-41ee-8EC3-F00080CADA7A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaDevMgr2
    {
        [PreserveSig] int EnumDeviceInfo(int lFlag, out IEnumWIA_DEV_INFO ppIEnum);
        [PreserveSig] int CreateDevice(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID, out IWiaItem2 ppWiaItem2Root);
        [PreserveSig] int SelectDeviceDlg(IntPtr hwndParent, int lDeviceType, int lFlags,
                                          [MarshalAs(UnmanagedType.BStr)] ref string pbstrDeviceID, out IWiaItem2 ppItemRoot);
        [PreserveSig] int SelectDeviceDlgID(IntPtr hwndParent, int lDeviceType, int lFlags,
                                            [MarshalAs(UnmanagedType.BStr)] ref string pbstrDeviceID);
        [PreserveSig] int RegisterEventCallbackInterface(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
                                                         ref Guid pEventGUID, IntPtr pIWiaEventCallback, out IntPtr pEventObject);
        [PreserveSig] int RegisterEventCallbackProgram(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
                                                       ref Guid pEventGUID, [MarshalAs(UnmanagedType.BStr)] string bstrCommandline,
                                                       [MarshalAs(UnmanagedType.BStr)] string bstrName,
                                                       [MarshalAs(UnmanagedType.BStr)] string bstrDescription,
                                                       [MarshalAs(UnmanagedType.BStr)] string bstrIcon);
        [PreserveSig] int RegisterEventCallbackCLSID(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
                                                     ref Guid pEventGUID, ref Guid pClsID,
                                                     [MarshalAs(UnmanagedType.BStr)] string bstrName,
                                                     [MarshalAs(UnmanagedType.BStr)] string bstrDescription,
                                                     [MarshalAs(UnmanagedType.BStr)] string bstrIcon);
        [PreserveSig] int GetImageDlg(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrDeviceID,
                                      IntPtr hwndParent, [MarshalAs(UnmanagedType.BStr)] string bstrFolderName,
                                      [MarshalAs(UnmanagedType.BStr)] string bstrFilename,
                                      ref int plNumFiles, IntPtr ppbstrFilePaths, IntPtr ppItem);
    }

    [ComImport, Guid("5e38b83c-8cf1-11d1-bf92-0060081ed811"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumWIA_DEV_INFO
    {
        [PreserveSig] int Next(uint celt, out IWiaPropertyStorage rgelt, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumWIA_DEV_INFO ppIEnum);
        [PreserveSig] int GetCount(out uint celt);
    }

    [ComImport, Guid("98B5E8A0-29CC-491a-AAC0-E6DB4FDCCEB6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaPropertyStorage
    {
        // PROPSPEC and PROPVARIANT are unions whose layout the CLR array marshaller
        // does not reproduce reliably; passing raw buffers we lay out ourselves is
        // both correct and lets us call PropVariantClear on the exact address.
        [PreserveSig] int ReadMultiple(uint cpspec, IntPtr rgpspec, IntPtr rgpropvar);
        [PreserveSig] int WriteMultiple(uint cpspec, IntPtr rgpspec, IntPtr rgpropvar, uint propidNameFirst);
        [PreserveSig] int DeleteMultiple(uint cpspec, IntPtr rgpspec);
        [PreserveSig] int ReadPropertyNames(uint cpropid, IntPtr rgpropid, IntPtr rglpwstrName);
        [PreserveSig] int WritePropertyNames(uint cpropid, IntPtr rgpropid, IntPtr rglpwstrName);
        [PreserveSig] int DeletePropertyNames(uint cpropid, IntPtr rgpropid);
        [PreserveSig] int Commit(uint grfCommitFlags);
        [PreserveSig] int Revert();
        [PreserveSig] int Enum(out IntPtr ppenum);
        [PreserveSig] int SetTimes(IntPtr pctime, IntPtr patime, IntPtr pmtime);
        [PreserveSig] int SetClass(ref Guid clsid);
        [PreserveSig] int Stat(IntPtr pstatpsstg);
        [PreserveSig] int GetPropertyAttributes(uint cpspec, IntPtr rgpspec, IntPtr rgflags, IntPtr rgpropvar);
        [PreserveSig] int GetCount(out uint pulNumProps);
        [PreserveSig] int GetPropertyStream(ref Guid pCompatibilityId, out IStream ppIStream);
        [PreserveSig] int SetPropertyStream(ref Guid pCompatibilityId, IStream pIStream);
    }

    [ComImport, Guid("6CBA0075-1287-407d-9B77-CF0E030435CC"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaItem2
    {
        [PreserveSig] int CreateChildItem(int lItemFlags, int lCreationFlags,
                                          [MarshalAs(UnmanagedType.BStr)] string bstrItemName, out IWiaItem2 ppIWiaItem2);
        [PreserveSig] int DeleteItem(int lFlags);
        // pCategoryGUID is [unique][in]: a NULL pointer means "every child item".
        // Declared as IntPtr rather than "ref Guid" so that NULL is expressible -
        // Guid.Empty is a different request and returns nothing.
        [PreserveSig] int EnumChildItems(IntPtr pCategoryGUID, out IEnumWiaItem2 ppIEnumWiaItem2);
        [PreserveSig] int FindItemByName(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrFullItemName, out IWiaItem2 ppIWiaItem2);
        [PreserveSig] int GetItemCategory(out Guid pItemCategoryGUID);
        [PreserveSig] int GetItemType(out int pItemType);
        [PreserveSig] int DeviceDlg(int lFlags, IntPtr hwndParent,
                                    [MarshalAs(UnmanagedType.BStr)] string bstrFolderName,
                                    [MarshalAs(UnmanagedType.BStr)] string bstrFilename,
                                    out int plNumFiles, out IntPtr ppbstrFilePaths, out IWiaItem2 ppItem);
        [PreserveSig] int DeviceCommand(int lFlags, ref Guid pCmdGUID, out IWiaItem2 ppIWiaItem2);
        [PreserveSig] int EnumDeviceCapabilities(int lFlags, out IntPtr ppIEnumWIA_DEV_CAPS);
        [PreserveSig] int CheckExtension(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrName,
                                         ref Guid riidExtensionInterface, [MarshalAs(UnmanagedType.Bool)] out bool pbExtensionExists);
        [PreserveSig] int GetExtension(int lFlags, [MarshalAs(UnmanagedType.BStr)] string bstrName,
                                       ref Guid riidExtensionInterface, out IntPtr ppOut);
        [PreserveSig] int GetParentItem(out IWiaItem2 ppIWiaItem2);
        [PreserveSig] int GetRootItem(out IWiaItem2 ppIWiaItem2);
        [PreserveSig] int GetPreviewComponent(int lFlags, out IntPtr ppWiaPreview);
        [PreserveSig] int EnumRegisterEventInfo(int lFlags, ref Guid pEventGUID, out IntPtr ppIEnum);
        [PreserveSig] int Diagnostic(uint ulSize, IntPtr pBuffer);
    }

    [ComImport, Guid("59970AF4-CD0D-44d9-AB24-52295630E582"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumWiaItem2
    {
        [PreserveSig] int Next(uint celt, out IWiaItem2 ppIWiaItem2, out uint pceltFetched);
        [PreserveSig] int Skip(uint celt);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumWiaItem2 ppIEnum);
        [PreserveSig] int GetCount(out uint celt);
    }

    [ComImport, Guid("c39d6942-2f4e-4d04-92fe-4ef4d3a1de5a"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaTransfer
    {
        [PreserveSig] int Download(int lFlags, IWiaTransferCallback pIWiaTransferCallback);
        [PreserveSig] int Upload(int lFlags, IStream pSource, IWiaTransferCallback pIWiaTransferCallback);
        [PreserveSig] int Cancel();
        [PreserveSig] int EnumWIA_FORMAT_INFO(out IntPtr ppEnum);
    }

    [ComImport, Guid("27d4eaaf-28a6-4ca5-9aab-e678168b9527"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWiaTransferCallback
    {
        [PreserveSig] int TransferCallback(int lFlags, ref WiaTransferParams pWiaTransferParams);
        [PreserveSig] int GetNextStream(int lFlags,
                                        [MarshalAs(UnmanagedType.BStr)] string bstrItemName,
                                        [MarshalAs(UnmanagedType.BStr)] string bstrFullItemName,
                                        out IStream ppDestination);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WiaTransferParams
    {
        public int lMessage;
        public int lPercentComplete;
        public ulong ulTransferredBytes;
        public int hrErrorStatus;
    }

    internal static class Ole32
    {
        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(IntPtr pvar);
    }

    /// <summary>
    /// Hand-rolled layout for the two COM unions WIA uses. Both have a
    /// pointer-aligned union member, so the value slot lands at offset 8 on x86
    /// and x64 alike; only the total size differs.
    /// </summary>
    public static class PropBuf
    {
        public static int PropSpecSize { get { return (IntPtr.Size == 4) ? 8 : 16; } }
        public static int PropVariantSize { get { return 8 + 2 * IntPtr.Size; } }

        /// <summary>PRSPEC_LPWSTR = 0, PRSPEC_PROPID = 1 (propidl.h).</summary>
        public const int PRSPEC_LPWSTR = 0;
        public const int PRSPEC_PROPID = 1;

        /// <summary>
        /// Allocates and zeroes a PROPSPEC array holding one property id.
        /// ulKind MUST be PRSPEC_PROPID. Leaving it 0 selects the LPWSTR arm of the
        /// union, and the callee then dereferences the property id as a string
        /// pointer - an immediate access violation inside the WIA service.
        /// </summary>
        public static IntPtr AllocSpec(uint propId)
        {
            IntPtr p = Marshal.AllocCoTaskMem(PropSpecSize);
            Zero(p, PropSpecSize);
            Marshal.WriteInt32(p, 0, PRSPEC_PROPID);
            Marshal.WriteInt32(p, UnionOffset, (int)propId);   // union.propid
            return p;
        }

        public static IntPtr AllocVariant()
        {
            IntPtr p = Marshal.AllocCoTaskMem(PropVariantSize);
            Zero(p, PropVariantSize);
            return p;
        }

        /// <summary>PROPSPEC's union starts after ulKind plus its alignment padding.</summary>
        public static int UnionOffset { get { return IntPtr.Size; } }

        /// <summary>PROPVARIANT's value slot: vt + three reserved words = 8 bytes.</summary>
        public const int ValueOffset = 8;

        public static ushort GetVt(IntPtr pv) { return unchecked((ushort)Marshal.ReadInt16(pv, 0)); }
        public static void SetVt(IntPtr pv, ushort vt) { Marshal.WriteInt16(pv, 0, unchecked((short)vt)); }

        public static int GetInt32(IntPtr pv) { return Marshal.ReadInt32(pv, ValueOffset); }
        public static void SetInt32(IntPtr pv, int v) { Marshal.WriteInt32(pv, ValueOffset, v); }

        public static IntPtr GetPtr(IntPtr pv) { return Marshal.ReadIntPtr(pv, ValueOffset); }
        public static void SetPtr(IntPtr pv, IntPtr v) { Marshal.WriteIntPtr(pv, ValueOffset, v); }

        public static void Zero(IntPtr p, int bytes)
        {
            for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
        }

        public static void FreeVariant(IntPtr pv)
        {
            if (pv == IntPtr.Zero) return;
            try { Ole32.PropVariantClear(pv); } catch { }
            Marshal.FreeCoTaskMem(pv);
        }

        public static void FreeSpec(IntPtr ps)
        {
            if (ps != IntPtr.Zero) Marshal.FreeCoTaskMem(ps);
        }
    }
}
