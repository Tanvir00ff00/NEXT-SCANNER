// =============================================================================
// NextScan Studio - TWAIN 2.x structures, constants and native bindings
// Plan ref: MASTER_PLAN section 7.2 + Appendix A.
//
// CRITICAL: twain.h wraps every structure in "#pragma pack(2)" on Windows. Every
// struct below therefore declares Pack = 2. Getting this wrong does not fail
// loudly - it silently shifts fields by a byte or two and the driver returns
// garbage dimensions, which is the single most common bug in hand-written TWAIN
// layers. Do not "tidy" the Pack attributes away.
// =============================================================================
using System;
using System.Runtime.InteropServices;

namespace NextScan.Twain
{
    // ------------------------------------------------------------------ DG / DAT / MSG
    public static class DG
    {
        public const uint CONTROL = 0x0001;
        public const uint IMAGE   = 0x0002;
        public const uint AUDIO   = 0x0004;
    }

    public static class DAT
    {
        public const ushort NULL            = 0x0000;
        public const ushort CAPABILITY      = 0x0001;
        public const ushort EVENT           = 0x0002;
        public const ushort IDENTITY        = 0x0003;
        public const ushort PARENT          = 0x0004;
        public const ushort PENDINGXFERS    = 0x0005;
        public const ushort SETUPMEMXFER    = 0x0006;
        public const ushort SETUPFILEXFER   = 0x0007;
        public const ushort STATUS          = 0x0008;
        public const ushort USERINTERFACE   = 0x0009;
        public const ushort XFERGROUP       = 0x000a;
        public const ushort CUSTOMDSDATA    = 0x000c;
        public const ushort DEVICEEVENT     = 0x000d;
        public const ushort FILESYSTEM      = 0x000e;
        public const ushort PASSTHRU        = 0x000f;
        public const ushort CALLBACK        = 0x0010;
        public const ushort STATUSUTF8      = 0x0011;
        public const ushort CALLBACK2       = 0x0012;

        public const ushort IMAGEINFO       = 0x0101;
        public const ushort IMAGELAYOUT     = 0x0102;
        public const ushort IMAGEMEMXFER    = 0x0103;
        public const ushort IMAGENATIVEXFER = 0x0104;
        public const ushort IMAGEFILEXFER   = 0x0105;
        public const ushort CIECOLOR        = 0x0106;
        public const ushort GRAYRESPONSE    = 0x0107;
        public const ushort RGBRESPONSE     = 0x0108;
        public const ushort JPEGCOMPRESSION = 0x0109;
        public const ushort PALETTE8        = 0x010a;
        public const ushort EXTIMAGEINFO    = 0x010b;
        public const ushort FILTER          = 0x010c;

        public const ushort ENTRYPOINT      = 0x0401;
    }

    public static class MSG
    {
        public const ushort NULL            = 0x0000;
        public const ushort GET             = 0x0001;
        public const ushort GETCURRENT      = 0x0002;
        public const ushort GETDEFAULT      = 0x0003;
        public const ushort GETFIRST        = 0x0004;
        public const ushort GETNEXT         = 0x0005;
        public const ushort SET             = 0x0006;
        public const ushort RESET           = 0x0007;
        public const ushort QUERYSUPPORT    = 0x0008;
        public const ushort GETHELP         = 0x0009;
        public const ushort GETLABEL        = 0x000a;
        public const ushort GETLABELENUM    = 0x000b;
        public const ushort SETCONSTRAINT   = 0x000c;

        public const ushort XFERREADY       = 0x0101;
        public const ushort CLOSEDSREQ      = 0x0102;
        public const ushort CLOSEDSOK       = 0x0103;
        public const ushort DEVICEEVENT     = 0x0104;

        public const ushort OPENDSM         = 0x0301;
        public const ushort CLOSEDSM        = 0x0302;

        public const ushort OPENDS          = 0x0401;
        public const ushort CLOSEDS         = 0x0402;
        public const ushort USERSELECT      = 0x0403;

        public const ushort DISABLEDS       = 0x0501;
        public const ushort ENABLEDS        = 0x0502;
        public const ushort ENABLEDSUIONLY  = 0x0503;

        public const ushort PROCESSEVENT    = 0x0601;

        public const ushort ENDXFER         = 0x0701;
        public const ushort STOPFEEDER      = 0x0702;

        public const ushort CHANGEDIRECTORY = 0x0801;
    }

    // ------------------------------------------------------------------ return codes
    public static class TWRC
    {
        public const ushort SUCCESS       = 0;
        public const ushort FAILURE       = 1;
        public const ushort CHECKSTATUS   = 2;
        public const ushort CANCEL        = 3;
        public const ushort DSEVENT       = 4;
        public const ushort NOTDSEVENT    = 5;
        public const ushort XFERDONE      = 6;
        public const ushort ENDOFLIST     = 7;
        public const ushort INFONOTSUPPORTED = 8;
        public const ushort DATANOTAVAILABLE = 9;
        public const ushort BUSY          = 10;
        public const ushort SCANNERLOCKED = 11;
    }

    public static class TWCC
    {
        public const ushort SUCCESS         = 0;
        public const ushort BUMMER          = 1;
        public const ushort LOWMEMORY       = 2;
        public const ushort NODS            = 3;
        public const ushort MAXCONNECTIONS  = 4;
        public const ushort OPERATIONERROR  = 5;
        public const ushort BADCAP          = 6;
        public const ushort BADPROTOCOL     = 9;
        public const ushort BADVALUE        = 10;
        public const ushort SEQERROR        = 11;
        public const ushort BADDEST         = 12;
        public const ushort CAPUNSUPPORTED  = 13;
        public const ushort CAPBADOPERATION = 14;
        public const ushort CAPSEQERROR     = 15;
        public const ushort DENIED          = 16;
        public const ushort FILEEXISTS      = 17;
        public const ushort FILENOTFOUND    = 18;
        public const ushort NOTEMPTY        = 19;
        public const ushort PAPERJAM        = 20;
        public const ushort PAPERDOUBLEFEED = 21;
        public const ushort FILEWRITEERROR  = 22;
        public const ushort CHECKDEVICEONLINE = 23;
        public const ushort INTERLOCK       = 24;
        public const ushort DAMAGEDCORNER   = 25;
        public const ushort FOCUSERROR      = 26;
        public const ushort DOCTOOLIGHT     = 27;
        public const ushort DOCTOODARK      = 28;
        public const ushort NOMEDIA         = 29;

        public static string Describe(ushort cc)
        {
            switch (cc)
            {
                case SUCCESS: return "success";
                case BUMMER: return "unspecified driver failure";
                case LOWMEMORY: return "driver out of memory";
                case NODS: return "no data source";
                case MAXCONNECTIONS: return "driver already in use by another application";
                case OPERATIONERROR: return "driver internal operation error";
                case BADCAP: return "capability not recognised";
                case BADPROTOCOL: return "unrecognised operation for this state";
                case BADVALUE: return "value out of range for this device";
                case SEQERROR: return "operation issued in the wrong TWAIN state";
                case BADDEST: return "unknown destination";
                case CAPUNSUPPORTED: return "capability not supported by this device";
                case CAPBADOPERATION: return "capability does not support that operation";
                case CAPSEQERROR: return "capability set in the wrong order";
                case DENIED: return "operation denied (file is read only)";
                case FILEEXISTS: return "file already exists";
                case FILENOTFOUND: return "file not found";
                case NOTEMPTY: return "directory not empty";
                case PAPERJAM: return "paper jam";
                case PAPERDOUBLEFEED: return "double feed detected";
                case FILEWRITEERROR: return "file write error";
                case CHECKDEVICEONLINE: return "device is offline or powered down";
                case INTERLOCK: return "cover is open";
                case DAMAGEDCORNER: return "damaged page corner";
                case FOCUSERROR: return "focus error";
                case DOCTOOLIGHT: return "document too light";
                case DOCTOODARK: return "document too dark";
                case NOMEDIA: return "no media in the device";
                default: return "condition code " + cc;
            }
        }
    }

    // ------------------------------------------------------------------ container / item types
    public static class TWON
    {
        public const ushort ARRAY       = 3;
        public const ushort ENUMERATION = 4;
        public const ushort ONEVALUE    = 5;
        public const ushort RANGE       = 6;
        public const ushort DONTCARE16  = 0xFFFF;
    }

    public static class TWTY
    {
        public const ushort INT8   = 0x0000;
        public const ushort INT16  = 0x0001;
        public const ushort INT32  = 0x0002;
        public const ushort UINT8  = 0x0003;
        public const ushort UINT16 = 0x0004;
        public const ushort UINT32 = 0x0005;
        public const ushort BOOL   = 0x0006;
        public const ushort FIX32  = 0x0007;
        public const ushort FRAME  = 0x0008;
        public const ushort STR32  = 0x0009;
        public const ushort STR64  = 0x000a;
        public const ushort STR128 = 0x000b;
        public const ushort STR255 = 0x000c;
        public const ushort HANDLE = 0x000f;

        /// <summary>Byte width of one item of this type. FRAME and the strings are fixed-size blobs.</summary>
        public static int SizeOf(ushort type)
        {
            switch (type)
            {
                case INT8:
                case UINT8: return 1;
                case INT16:
                case UINT16:
                case BOOL: return 2;
                case INT32:
                case UINT32:
                case FIX32: return 4;
                case FRAME: return 16;
                case STR32: return 34;
                case STR64: return 66;
                case STR128: return 130;
                case STR255: return 256;
                case HANDLE: return IntPtr.Size;
                default: return 4;
            }
        }
    }

    // ------------------------------------------------------------------ capabilities
    public static class CAP
    {
        public const ushort XFERCOUNT            = 0x0001;
        public const ushort SUPPORTEDCAPS        = 0x0101;
        public const ushort UICONTROLLABLE       = 0x030e;
        public const ushort DEVICEONLINE         = 0x030f;
        public const ushort AUTOFEED             = 0x1007;
        public const ushort CLEARPAGE            = 0x1008;
        public const ushort FEEDPAGE             = 0x1009;
        public const ushort REWINDPAGE           = 0x100a;
        public const ushort INDICATORS           = 0x100b;
        public const ushort PAPERDETECTABLE      = 0x100d;
        public const ushort DUPLEX               = 0x1012;
        public const ushort DUPLEXENABLED        = 0x1013;
        public const ushort ENABLEDSUIONLY       = 0x1014;
        public const ushort FEEDERENABLED        = 0x1002;
        public const ushort FEEDERLOADED         = 0x1003;
        public const ushort AUTOSCAN             = 0x1017;
        public const ushort CLEARBUFFERS         = 0x1018;
        public const ushort MAXBATCHBUFFERS      = 0x1019;
        public const ushort AUTOMATICSENSEMEDIUM = 0x1027;
        public const ushort PAPERHANDLING        = 0x100f;
    }

    public static class ICAP
    {
        public const ushort COMPRESSION               = 0x0100;
        public const ushort PIXELTYPE                 = 0x0101;
        public const ushort UNITS                     = 0x0102;
        public const ushort XFERMECH                  = 0x0103;
        public const ushort PLANARCHUNKY              = 0x1108;
        public const ushort BITDEPTH                  = 0x1122;
        public const ushort BITORDER                  = 0x1123;
        public const ushort BRIGHTNESS                = 0x1100;
        public const ushort CONTRAST                  = 0x1103;
        public const ushort GAMMA                     = 0x1110;
        public const ushort HIGHLIGHT                 = 0x1112;
        public const ushort SHADOW                    = 0x1131;
        public const ushort THRESHOLD                 = 0x1138;
        public const ushort PIXELFLAVOR               = 0x112f;
        public const ushort XRESOLUTION               = 0x1118;
        public const ushort YRESOLUTION               = 0x1119;
        public const ushort XSCALING                  = 0x1139;
        public const ushort YSCALING                  = 0x113a;
        public const ushort PHYSICALWIDTH             = 0x1130;
        public const ushort PHYSICALHEIGHT            = 0x110f;
        public const ushort SUPPORTEDSIZES            = 0x1137;
        public const ushort FRAMES                    = 0x1114;
        public const ushort ORIENTATION               = 0x1127;
        public const ushort ROTATION                  = 0x1132;
        public const ushort IMAGEFILEFORMAT           = 0x111c;
        public const ushort JPEGQUALITY               = 0x1145;
        public const ushort AUTOMATICDESKEW           = 0x1151;
        public const ushort AUTOMATICBORDERDETECTION  = 0x1150;
        public const ushort AUTOMATICROTATE           = 0x1152;
        public const ushort AUTODISCARDBLANKPAGES     = 0x1134;
        public const ushort FLIPROTATION              = 0x1136;
        public const ushort MIRROR                    = 0x1146;
        public const ushort NOISEFILTER               = 0x1147;
        public const ushort OVERSCAN                  = 0x1148;
        public const ushort LIGHTPATH                 = 0x1128;
        public const ushort LIGHTSOURCE               = 0x1129;
        public const ushort FILMTYPE                  = 0x115b;
        public const ushort EXPOSURETIME              = 0x1107;
        public const ushort BARCODEDETECTIONENABLED   = 0x1153;
        public const ushort PATCHCODEDETECTIONENABLED = 0x1157;
        public const ushort ICCPROFILE                = 0x1149;
        public const ushort UNDEFINEDIMAGESIZE        = 0x113c;
    }

    /// <summary>ICAP_PIXELTYPE values.</summary>
    public static class TWPT
    {
        public const ushort BW      = 0;
        public const ushort GRAY    = 1;
        public const ushort RGB     = 2;
        public const ushort PALETTE = 3;
        public const ushort CMY     = 4;
        public const ushort CMYK    = 5;
        public const ushort YUV     = 6;
        public const ushort YUVK    = 7;
        public const ushort CIEXYZ  = 8;
        public const ushort INFRARED = 16;
    }

    /// <summary>ICAP_XFERMECH values.</summary>
    public static class TWSX
    {
        public const ushort NATIVE  = 0;
        public const ushort FILE    = 1;
        public const ushort MEMORY  = 2;
        public const ushort MEMFILE = 4;
    }

    /// <summary>ICAP_UNITS values.</summary>
    public static class TWUN
    {
        public const ushort INCHES      = 0;
        public const ushort CENTIMETERS = 1;
        public const ushort PICAS       = 2;
        public const ushort POINTS      = 3;
        public const ushort TWIPS       = 4;
        public const ushort PIXELS      = 5;
        public const ushort MILLIMETERS = 6;
    }

    /// <summary>ICAP_PIXELFLAVOR values.</summary>
    public static class TWPF
    {
        public const ushort CHOCOLATE = 0;   // 0 = black (normal for bitonal)
        public const ushort VANILLA   = 1;   // 0 = white (inverted)
    }

    /// <summary>ICAP_LIGHTPATH values - transparency unit for film.</summary>
    public static class TWLP
    {
        public const ushort REFLECTIVE   = 0;
        public const ushort TRANSMISSIVE = 1;
    }

    public static class TWCP
    {
        public const ushort NONE     = 0;
        public const ushort PACKBITS = 1;
        public const ushort GROUP31D = 2;
        public const ushort GROUP4   = 5;
        public const ushort JPEG     = 6;
        public const ushort RLE4     = 8;
    }

    /// <summary>Data source SupportedGroups flags.</summary>
    public static class DF
    {
        public const uint DSM2  = 0x10000000;
        public const uint APP2  = 0x20000000;
        public const uint DS2   = 0x40000000;
    }

    // ------------------------------------------------------------------ structures
    // Every struct below mirrors twain.h under #pragma pack(2).

    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    public struct TW_VERSION
    {
        public ushort MajorNum;
        public ushort MinorNum;
        public ushort Language;
        public ushort Country;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string Info;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
    public struct TW_IDENTITY
    {
        public uint Id;
        public TW_VERSION Version;
        public ushort ProtocolMajor;
        public ushort ProtocolMinor;
        public uint SupportedGroups;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string Manufacturer;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string ProductFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 34)]
        public string ProductName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_STATUS
    {
        public ushort ConditionCode;
        public ushort Data;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_USERINTERFACE
    {
        public ushort ShowUI;    // TW_BOOL
        public ushort ModalUI;   // TW_BOOL
        public IntPtr hParent;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_EVENT
    {
        public IntPtr pEvent;
        public ushort TWMessage;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_CAPABILITY
    {
        public ushort Cap;
        public ushort ConType;
        public IntPtr hContainer;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ONEVALUE
    {
        public ushort ItemType;
        public uint Item;
    }

    /// <summary>Fixed header of TW_ENUMERATION; ItemList follows immediately after.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ENUMERATION
    {
        public ushort ItemType;
        public uint NumItems;
        public uint CurrentIndex;
        public uint DefaultIndex;
    }

    /// <summary>Fixed header of TW_ARRAY; ItemList follows immediately after.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ARRAY
    {
        public ushort ItemType;
        public uint NumItems;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_RANGE
    {
        public ushort ItemType;
        public uint MinValue;
        public uint MaxValue;
        public uint StepSize;
        public uint DefaultValue;
        public uint CurrentValue;
    }

    /// <summary>
    /// TWAIN's fixed-point type: signed whole part, unsigned 1/65536 fraction.
    /// Sign handling is the classic bug here - negative values must borrow from
    /// Whole, so -0.5 is Whole = -1, Frac = 32768.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_FIX32
    {
        public short Whole;
        public ushort Frac;

        public double ToDouble()
        {
            return Whole + (Frac / 65536.0);
        }

        public static TW_FIX32 FromDouble(double value)
        {
            TW_FIX32 f;
            // Round to the nearest 1/65536 first, then split. Truncating instead of
            // rounding drifts by up to 1/65536 per conversion, which shows up as a
            // sub-pixel scan-region offset after several round trips.
            bool negative = value < 0;
            if (negative) value = -value;
            uint total = (uint)Math.Round(value * 65536.0);
            int whole = (int)(total >> 16);
            uint frac = total & 0xFFFF;
            if (negative)
            {
                if (frac == 0) { whole = -whole; }
                else { whole = -whole - 1; frac = 0x10000 - frac; }
            }
            if (whole > short.MaxValue) whole = short.MaxValue;
            if (whole < short.MinValue) whole = short.MinValue;
            f.Whole = (short)whole;
            f.Frac = (ushort)frac;
            return f;
        }

        /// <summary>Reinterpret a raw 32-bit container item as TW_FIX32.</summary>
        public static TW_FIX32 FromRaw(uint raw)
        {
            TW_FIX32 f;
            f.Whole = unchecked((short)(raw & 0xFFFF));
            f.Frac = (ushort)((raw >> 16) & 0xFFFF);
            return f;
        }

        public uint ToRaw()
        {
            return (uint)((ushort)Whole) | ((uint)Frac << 16);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_FRAME
    {
        public TW_FIX32 Left;
        public TW_FIX32 Top;
        public TW_FIX32 Right;
        public TW_FIX32 Bottom;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_IMAGELAYOUT
    {
        public TW_FRAME Frame;
        public uint DocumentNumber;
        public uint PageNumber;
        public uint FrameNumber;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_IMAGEINFO
    {
        public TW_FIX32 XResolution;
        public TW_FIX32 YResolution;
        public int ImageWidth;
        public int ImageLength;
        public short SamplesPerPixel;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public short[] BitsPerSample;
        public short BitsPerPixel;
        public ushort Planar;      // TW_BOOL
        public short PixelType;
        public ushort Compression;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_SETUPMEMXFER
    {
        public uint MinBufSize;
        public uint MaxBufSize;
        public uint Preferred;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_MEMORY
    {
        public uint Flags;
        public uint Length;
        public IntPtr TheMem;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_IMAGEMEMXFER
    {
        public ushort Compression;
        public uint BytesPerRow;
        public uint Columns;
        public uint Rows;
        public uint XOffset;
        public uint YOffset;
        public uint BytesWritten;
        public TW_MEMORY Memory;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_PENDINGXFERS
    {
        public ushort Count;
        public uint EOJ;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct TW_ENTRYPOINT
    {
        public uint Size;
        public IntPtr DSM_Entry;
        public IntPtr DSM_MemAllocate;
        public IntPtr DSM_MemFree;
        public IntPtr DSM_MemLock;
        public IntPtr DSM_MemUnlock;
    }

    /// <summary>TW_MEMORY.Flags values.</summary>
    public static class TWMF
    {
        public const uint APPOWNS   = 0x0001;
        public const uint DSMOWNS   = 0x0002;
        public const uint DSOWNS    = 0x0004;
        public const uint POINTER   = 0x0008;
        public const uint HANDLE    = 0x0010;
    }

    // ------------------------------------------------------------------ delegates
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmEntryDelegate(IntPtr origin, IntPtr dest, uint dg, ushort dat, ushort msg, IntPtr pData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr DsmMemAllocateDelegate(uint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DsmMemFreeDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr DsmMemLockDelegate(IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DsmMemUnlockDelegate(IntPtr handle);

    // ------------------------------------------------------------------ win32
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true, BestFitMapping = false)]
        internal static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern UIntPtr GlobalSize(IntPtr hMem);

        internal const uint GMEM_FIXED    = 0x0000;
        internal const uint GMEM_MOVEABLE = 0x0002;
        internal const uint GMEM_ZEROINIT = 0x0040;
        internal const uint GHND          = GMEM_MOVEABLE | GMEM_ZEROINIT;
    }
}
