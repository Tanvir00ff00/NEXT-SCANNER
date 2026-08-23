// =============================================================================
// NextScan Studio - TWAIN simulator (fake Data Source Manager)
// Plan ref: MASTER_PLAN section 18.3, ADR-0002.
//
// This is a TWAINDSM.DLL replacement: it exports DSM_Entry and plays BOTH the
// DSM and one built-in data source, so the managed TWAIN stack - state machine,
// container marshalling, capability negotiation, both transfer mechanisms -
// runs exactly as it would against hardware, deterministically, with no admin
// rights and no pollution of the machine's global source list.
//
// It is loaded through the NEXTSCAN_TWAIN_DSM environment variable (see
// TwainSession.FindDsmCandidates); the variable pins this DLL as the only DSM
// candidate, so the harness is deterministic. Personality comes from
// NEXTSCAN_SIM_PERSONALITY, image content from NEXTSCAN_SIM_IMAGE.
//
// Layout rules that must not be "tidied":
//   * Every TWAIN struct below sits under "#pragma pack(2)", mirroring twain.h
//     and the managed side's Pack=2 declarations. Wrong packing does not fail
//     loudly - it shifts fields and returns garbage dimensions (STATUS.md bug 8).
//   * Containers handed to the application are GlobalAlloc(GMEM_MOVEABLE)
//     allocations, because this DSM never claims DF_DSM2 and therefore never
//     provides DSM_Mem* entry points - the session frees containers with
//     GlobalFree, exactly as it does against the legacy twain_32.dll on the
//     reference machine ("2.x memory functions: False").
//   * Memory-transfer strips are RGB, row-packed with no DIB-style 4-byte row
//     alignment; 4-byte alignment applies only to native DIB transfer. The
//     session swaps R/B for memory transfer, so emitting BGR here would come
//     out with red and blue exchanged.
// =============================================================================

// getenv/_stricmp trigger MSVC deprecation warnings; the "secure" variants
// add nothing here (the strings are process-controlled test config, not input).
#define _CRT_SECURE_NO_WARNINGS

#include <windows.h>
#include <string.h>
#include <stdio.h>
#include <stdarg.h>
#include <math.h>

// ------------------------------------------------------------------ pack(2) structures
#pragma pack(push, 2)

typedef unsigned short TW_UINT16;
typedef short          TW_INT16;
typedef unsigned int   TW_UINT32;
typedef int            TW_INT32;
typedef void*          TW_MEMREF;

typedef struct { TW_UINT16 MajorNum; TW_UINT16 MinorNum; TW_UINT16 Language; TW_UINT16 Country; char Info[34]; } TW_VERSION;
typedef struct { TW_UINT32 Id; TW_VERSION Version; TW_UINT16 ProtocolMajor; TW_UINT16 ProtocolMinor; TW_UINT32 SupportedGroups;
                 char Manufacturer[34]; char ProductFamily[34]; char ProductName[34]; } TW_IDENTITY;
typedef struct { TW_UINT16 ConditionCode; TW_UINT16 Data; } TW_STATUS;
typedef struct { TW_UINT16 ShowUI; TW_UINT16 ModalUI; HWND hParent; } TW_USERINTERFACE;
typedef struct { TW_MEMREF pEvent; TW_UINT16 TWMessage; } TW_EVENT;
typedef struct { TW_UINT16 Cap; TW_UINT16 ConType; HANDLE hContainer; } TW_CAPABILITY;
typedef struct { TW_UINT16 ItemType; TW_UINT32 Item; } TW_ONEVALUE;
typedef struct { TW_UINT16 ItemType; TW_UINT32 NumItems; TW_UINT32 CurrentIndex; TW_UINT32 DefaultIndex; } TW_ENUMERATION;
typedef struct { TW_UINT16 ItemType; TW_UINT32 NumItems; } TW_ARRAY;
typedef struct { TW_UINT16 ItemType; TW_UINT32 MinValue; TW_UINT32 MaxValue; TW_UINT32 StepSize; TW_UINT32 DefaultValue; TW_UINT32 CurrentValue; } TW_RANGE;
typedef struct { short Whole; unsigned short Frac; } TW_FIX32;
typedef struct { TW_FIX32 Left, Top, Right, Bottom; } TW_FRAME;
typedef struct { TW_FRAME Frame; TW_UINT32 DocumentNumber, PageNumber, FrameNumber; } TW_IMAGELAYOUT;
typedef struct { TW_FIX32 XResolution, YResolution; TW_INT32 ImageWidth, ImageLength; TW_INT16 SamplesPerPixel;
                 TW_INT16 BitsPerSample[8]; TW_INT16 BitsPerPixel; TW_UINT16 Planar; TW_INT16 PixelType; TW_UINT16 Compression; } TW_IMAGEINFO;
typedef struct { TW_UINT32 MinBufSize, MaxBufSize, Preferred; } TW_SETUPMEMXFER;
typedef struct { TW_UINT32 Flags, Length; TW_MEMREF TheMem; } TW_MEMORY;
typedef struct { TW_UINT16 Compression; TW_UINT32 BytesPerRow, Columns, Rows, XOffset, YOffset, BytesWritten; TW_MEMORY Memory; } TW_IMAGEMEMXFER;
typedef struct { TW_UINT16 Count; TW_UINT32 EOJ; } TW_PENDINGXFERS;

#pragma pack(pop)

// ------------------------------------------------------------------ constants (mirror TwainTypes.cs)
#define DG_CONTROL  0x0001u
#define DG_IMAGE    0x0002u

#define DAT_NULL            0x0000
#define DAT_CAPABILITY      0x0001
#define DAT_EVENT           0x0002
#define DAT_IDENTITY        0x0003
#define DAT_PARENT          0x0004
#define DAT_PENDINGXFERS    0x0005
#define DAT_SETUPMEMXFER    0x0006
#define DAT_STATUS          0x0008
#define DAT_USERINTERFACE   0x0009
#define DAT_IMAGEINFO       0x0101
#define DAT_IMAGELAYOUT     0x0102
#define DAT_IMAGEMEMXFER    0x0103
#define DAT_IMAGENATIVEXFER 0x0104
#define DAT_ENTRYPOINT      0x0401

#define MSG_NULL         0x0000
#define MSG_GET          0x0001
#define MSG_GETCURRENT   0x0002
#define MSG_GETDEFAULT   0x0003
#define MSG_GETFIRST     0x0004
#define MSG_GETNEXT      0x0005
#define MSG_SET          0x0006
#define MSG_RESET        0x0007
#define MSG_QUERYSUPPORT 0x0008
#define MSG_XFERREADY    0x0101
#define MSG_CLOSEDSREQ   0x0102
#define MSG_CLOSEDSOK    0x0103
#define MSG_OPENDSM      0x0301
#define MSG_CLOSEDSM     0x0302
#define MSG_OPENDS       0x0401
#define MSG_CLOSEDS      0x0402
#define MSG_DISABLEDS    0x0501
#define MSG_ENABLEDS     0x0502
#define MSG_PROCESSEVENT 0x0601
#define MSG_ENDXFER      0x0701

#define TWRC_SUCCESS       0
#define TWRC_FAILURE       1
#define TWRC_CHECKSTATUS   2
#define TWRC_CANCEL        3
#define TWRC_DSEVENT       4
#define TWRC_NOTDSEVENT    5
#define TWRC_XFERDONE      6
#define TWRC_ENDOFLIST     7

#define TWCC_SUCCESS        0
#define TWCC_MAXCONNECTIONS 4
#define TWCC_OPERATIONERROR 5
#define TWCC_BADPROTOCOL    9
#define TWCC_BADVALUE       10
#define TWCC_SEQERROR       11
#define TWCC_CAPUNSUPPORTED 13
#define TWCC_CAPBADOPERATION 14

#define TWON_ARRAY       3
#define TWON_ENUMERATION 4
#define TWON_ONEVALUE    5
#define TWON_RANGE       6

#define TWTY_UINT16 0x0004
#define TWTY_INT16  0x0001
#define TWTY_BOOL   0x0006
#define TWTY_FIX32  0x0007

#define CAP_XFERCOUNT     0x0001
#define CAP_UICONTROLLABLE 0x030e
#define CAP_INDICATORS    0x100b
#define CAP_DUPLEX        0x1012
#define CAP_DUPLEXENABLED 0x1013
#define CAP_FEEDERENABLED 0x1002

#define ICAP_COMPRESSION   0x0100
#define ICAP_PIXELTYPE     0x0101
#define ICAP_UNITS         0x0102
#define ICAP_XFERMECH      0x0103
#define ICAP_BITDEPTH      0x1122
#define ICAP_BRIGHTNESS    0x1100
#define ICAP_CONTRAST      0x1103
#define ICAP_XRESOLUTION   0x1118
#define ICAP_YRESOLUTION   0x1119
#define ICAP_PHYSICALWIDTH  0x1130
#define ICAP_PHYSICALHEIGHT 0x110f
#define ICAP_AUTOMATICDESKEW 0x1151

#define TWPT_BW   0
#define TWPT_GRAY 1
#define TWPT_RGB  2

#define TWSX_NATIVE 0
#define TWSX_MEMORY 2

// The private message posted to the parent window after MSG_ENABLEDS so the
// application's message pump wakes up and calls DAT_EVENT/MSG_PROCESSEVENT.
// Real sources deliver MSG_XFERREADY exactly this way - through the pump, not
// synchronously from ENABLEDS - so the simulator must too, or the session's
// pump loop starves and its watchdog fires.
#define SIM_WAKEUP_MSG (WM_APP + 0x450)

// ------------------------------------------------------------------ personality / pattern
// NEXTSCAN_SIM_PERSONALITY selects which driver misbehaviour to reproduce.
// Every personality below is a behaviour observed in a real shipping driver;
// they exist so the failure paths can be exercised without hardware.
enum SimPersonality
{
    PERS_WELLBEHAVED = 0,
    PERS_ODDWIDTH,       // forces an odd pixel width: 24-bit stride/padding stressor
    PERS_BW1,            // delivers 1-bit no matter what pixel type was negotiated
    PERS_GRAY8,          // delivers 8-bit grey regardless of negotiation
    PERS_GRAY16,         // delivers 16-bit grey regardless of negotiation
    PERS_COLOR48,        // delivers 48-bit colour regardless of negotiation
    PERS_BOTTOMUP,       // memory transfer refused -> native DIB, bottom-up rows
    PERS_TOPDOWN,        // memory transfer refused -> native DIB, negative height
    PERS_REFUSESUI,      // rejects ShowUI=FALSE (CAP_UICONTROLLABLE = 0)
    PERS_SETLIES,        // MSG_SET on resolution succeeds, value silently ignored
    PERS_HANG,           // never returns from MSG_ENABLEDS (watchdog test)
    PERS_CRASH7,         // access violation on the first DAT_IMAGEMEMXFER (state 7)
    PERS_DUPLEX,         // feeder+duplex: two pages, back side rotated 180 degrees
    PERS_BUSY,           // first OPENDS refuses with TWCC_MAXCONNECTIONS, retry wins
};

enum SimPattern
{
    PAT_BARS = 0,      // 8 vertical colour bars
    PAT_GRADIENT,      // horizontal ramp, one full ramp per row
    PAT_CHECKER,       // 1x1 pixel checkerboard at the extreme values
    PAT_FLAT,          // single flat value (white)
};

// ------------------------------------------------------------------ state
static struct SimState
{
    bool dsmOpen;
    bool dsOpen;
    bool enabled;
    bool imageReady;         // a page is waiting to be transferred
    bool xferReadyReported;  // MSG_XFERREADY already delivered for this page
    HWND parent;

    // negotiated settings (what MSG_SET actually left us with)
    double xRes, yRes;
    unsigned short pixelType;
    unsigned short bitDepth;      // bits per PIXEL (spec convention)
    unsigned short units;
    unsigned short xferMech;
    bool feederEnabled;
    short xferCount;
    double brightness, contrast;

    // scan region in inches; defaults to the full bed
    double frameL, frameT, frameR, frameB;
    bool frameSet;

    // transfer progress
    int pendingPages;
    int pageIndex;
    int rowsSent;            // rows of the current page already delivered
    int imageWidth, imageHeight;

    SimPersonality personality;
    SimPattern pattern;
    int forcedType;           // -1, or a TWPT_* the device delivers regardless of SET
    int forcedDepth;          // bits per pixel that goes with forcedType
    int opendsAttempts;       // busy personality: refuse the first OPENDS
    TW_UINT16 lastCC;

    FILE* log;
} S;

static void Logf(const char* fmt, ...)
{
    if (!S.log) return;
    va_list ap; va_start(ap, fmt);
    vfprintf(S.log, fmt, ap);
    fputc('\n', S.log);
    fflush(S.log);
    va_end(ap);
}

static void ReadConfig()
{
    S.personality = PERS_WELLBEHAVED;
    S.forcedType = -1;
    S.forcedDepth = 0;

    const char* pers = getenv("NEXTSCAN_SIM_PERSONALITY");
    if (pers && pers[0])
    {
        if      (!_stricmp(pers, "wellbehaved")) S.personality = PERS_WELLBEHAVED;
        else if (!_stricmp(pers, "oddwidth"))   S.personality = PERS_ODDWIDTH;
        else if (!_stricmp(pers, "bw1"))     { S.personality = PERS_BW1;     S.forcedType = TWPT_BW;   S.forcedDepth = 1;  }
        else if (!_stricmp(pers, "gray8"))   { S.personality = PERS_GRAY8;   S.forcedType = TWPT_GRAY; S.forcedDepth = 8;  }
        else if (!_stricmp(pers, "gray16"))  { S.personality = PERS_GRAY16;  S.forcedType = TWPT_GRAY; S.forcedDepth = 16; }
        else if (!_stricmp(pers, "color48")) { S.personality = PERS_COLOR48; S.forcedType = TWPT_RGB;  S.forcedDepth = 48; }
        else if (!_stricmp(pers, "bottomup")) S.personality = PERS_BOTTOMUP;
        else if (!_stricmp(pers, "topdown"))  S.personality = PERS_TOPDOWN;
        else if (!_stricmp(pers, "refusesui")) S.personality = PERS_REFUSESUI;
        else if (!_stricmp(pers, "setlies"))   S.personality = PERS_SETLIES;
        else if (!_stricmp(pers, "hang"))      S.personality = PERS_HANG;
        else if (!_stricmp(pers, "crash7"))    S.personality = PERS_CRASH7;
        else if (!_stricmp(pers, "duplex"))    S.personality = PERS_DUPLEX;
        else if (!_stricmp(pers, "busy"))      S.personality = PERS_BUSY;
        else Logf("unknown personality '%s', using wellbehaved", pers);
    }

    const char* img = getenv("NEXTSCAN_SIM_IMAGE");
    S.pattern = PAT_BARS;
    if (img)
    {
        if (!_stricmp(img, "gradient")) S.pattern = PAT_GRADIENT;
        else if (!_stricmp(img, "checker")) S.pattern = PAT_CHECKER;
        else if (!_stricmp(img, "flat")) S.pattern = PAT_FLAT;
    }

    const char* logPath = getenv("NEXTSCAN_SIM_LOG");
    if (logPath && logPath[0]) fopen_s(&S.log, logPath, "w");
}

// ------------------------------------------------------------------ fix32
static TW_FIX32 FixFromDouble(double v)
{
    TW_FIX32 f;
    bool neg = v < 0;
    if (neg) v = -v;
    unsigned int total = (unsigned int)floor(v * 65536.0 + 0.5);
    int whole = (int)(total >> 16);
    unsigned int frac = total & 0xFFFF;
    if (neg)
    {
        if (frac == 0) whole = -whole;
        else { whole = -whole - 1; frac = 0x10000 - frac; }
    }
    f.Whole = (short)whole;
    f.Frac = (unsigned short)frac;
    return f;
}

static double FixToDouble(TW_FIX32 f)
{
    return f.Whole + f.Frac / 65536.0;
}

// ------------------------------------------------------------------ container builders
// All returned handles are GlobalAlloc(GMEM_MOVEABLE) - the application frees
// them with GlobalFree because we never advertise DSM 2.x memory entry points.
static HANDLE AllocContainer(int bytes)
{
    return GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, bytes);
}

static HANDLE BuildOneU16(unsigned short itemType, unsigned int item)
{
    HANDLE h = AllocContainer(sizeof(TW_ONEVALUE));
    if (!h) return NULL;
    TW_ONEVALUE* p = (TW_ONEVALUE*)GlobalLock(h);
    p->ItemType = itemType; p->Item = item;
    GlobalUnlock(h);
    return h;
}

static HANDLE BuildOneFix(double v)
{
    TW_FIX32 f = FixFromDouble(v);
    unsigned int raw = ((unsigned int)(unsigned short)f.Whole) | ((unsigned int)f.Frac << 16);
    return BuildOneU16(TWTY_FIX32, raw);
}

static HANDLE BuildEnumU16(unsigned short itemType, const unsigned int* items, int n, int cur, int def)
{
    // Items are stored widened to 32 bits and written at their natural size, so
    // one builder serves UINT16/INT16/BOOL (2 bytes) and UINT32/INT32 (4).
    int itemSize = (itemType == TWTY_UINT16 || itemType == TWTY_INT16 || itemType == TWTY_BOOL) ? 2 : 4;
    int bytes = sizeof(TW_ENUMERATION) + n * itemSize;
    HANDLE h = AllocContainer(bytes);
    if (!h) return NULL;
    unsigned char* base = (unsigned char*)GlobalLock(h);
    TW_ENUMERATION* en = (TW_ENUMERATION*)base;
    en->ItemType = itemType;
    en->NumItems = n;
    en->CurrentIndex = cur;
    en->DefaultIndex = def;
    for (int i = 0; i < n; i++)
    {
        if (itemSize == 2) *(unsigned short*)(base + sizeof(TW_ENUMERATION) + i * 2) = (unsigned short)items[i];
        else *(unsigned int*)(base + sizeof(TW_ENUMERATION) + i * 4) = items[i];
    }
    GlobalUnlock(h);
    return h;
}

static HANDLE BuildEnumFix(const double* items, int n, int cur, int def)
{
    int bytes = sizeof(TW_ENUMERATION) + n * sizeof(TW_FIX32);
    HANDLE h = AllocContainer(bytes);
    if (!h) return NULL;
    unsigned char* base = (unsigned char*)GlobalLock(h);
    TW_ENUMERATION* en = (TW_ENUMERATION*)base;
    en->ItemType = TWTY_FIX32;
    en->NumItems = n;
    en->CurrentIndex = cur;
    en->DefaultIndex = def;
    for (int i = 0; i < n; i++)
        *(TW_FIX32*)(base + sizeof(TW_ENUMERATION) + i * sizeof(TW_FIX32)) = FixFromDouble(items[i]);
    GlobalUnlock(h);
    return h;
}

static HANDLE BuildRangeFix(double lo, double hi, double step, double def, double cur)
{
    HANDLE h = AllocContainer(sizeof(TW_RANGE));
    if (!h) return NULL;
    TW_RANGE* r = (TW_RANGE*)GlobalLock(h);
    TW_FIX32 flo = FixFromDouble(lo), fhi = FixFromDouble(hi), fst = FixFromDouble(step), fd = FixFromDouble(def), fc = FixFromDouble(cur);
    r->ItemType = TWTY_FIX32;
    r->MinValue = ((unsigned int)(unsigned short)flo.Whole) | ((unsigned int)flo.Frac << 16);
    r->MaxValue = ((unsigned int)(unsigned short)fhi.Whole) | ((unsigned int)fhi.Frac << 16);
    r->StepSize = ((unsigned int)(unsigned short)fst.Whole) | ((unsigned int)fst.Frac << 16);
    r->DefaultValue = ((unsigned int)(unsigned short)fd.Whole) | ((unsigned int)fd.Frac << 16);
    r->CurrentValue = ((unsigned int)(unsigned short)fc.Whole) | ((unsigned int)fc.Frac << 16);
    GlobalUnlock(h);
    return h;
}

// ------------------------------------------------------------------ image geometry
static void ComputeImageSize()
{
    double wIn = S.frameR - S.frameL;
    double hIn = S.frameB - S.frameT;
    if (!S.frameSet || wIn <= 0) wIn = 8.5;
    if (!S.frameSet || hIn <= 0) hIn = 11.7;
    S.imageWidth = (int)floor(wIn * S.xRes + 0.5);
    S.imageHeight = (int)floor(hIn * S.yRes + 0.5);
    // Guard rails a hostile driver would not have: the session validates sizes
    // too, but staying sane keeps tests deterministic.
    if (S.imageWidth < 1) S.imageWidth = 1;
    if (S.imageHeight < 1) S.imageHeight = 1;
    if (S.imageWidth > 20000) S.imageWidth = 20000;
    if (S.imageHeight > 20000) S.imageHeight = 20000;
    if (S.personality == PERS_ODDWIDTH && (S.imageWidth % 2) == 0) S.imageWidth += 1;
}

static int SamplesPerPixel()
{
    return (S.pixelType == TWPT_RGB) ? 3 : 1;
}

static int BitsPerSample()
{
    if (S.pixelType == TWPT_BW) return 1;
    return S.bitDepth / SamplesPerPixel();
}

static int BitsPerPixel()
{
    if (S.pixelType == TWPT_BW) return 1;
    return S.bitDepth;
}

static int BytesPerRowPacked()
{
    if (S.pixelType == TWPT_BW) return (S.imageWidth + 7) / 8;
    return S.imageWidth * SamplesPerPixel() * (BitsPerSample() / 8);
}

// ------------------------------------------------------------------ synthetic pixels
// Pure function of position so any strip split reproduces the same image -
// the golden-image harness depends on this determinism.
static double Pattern(int x, int y, int ch)
{
    int w = S.imageWidth;
    switch (S.pattern)
    {
    case PAT_GRADIENT:
        return (w <= 1) ? 1.0 : (double)x / (double)(w - 1);
    case PAT_CHECKER:
        return ((x + y) & 1) ? 1.0 : 0.0;
    case PAT_FLAT:
        return 1.0;
    case PAT_BARS:
    default:
    {
        static const double bars[8][3] = {
            {1,0,0},{0,1,0},{0,0,1},{1,1,0},{0,1,1},{1,0,1},{0.5,0.5,0.5},{1,1,1}
        };
        int bar = (x * 8) / (w < 8 ? 8 : w);
        if (bar > 7) bar = 7;
        if (S.pixelType == TWPT_RGB) return bars[bar][ch];
        // Grey/bitonal derive from the bar's luma, so the pattern stays
        // recognisable in every mode without a second table.
        double lum = 0.299 * bars[bar][0] + 0.587 * bars[bar][1] + 0.114 * bars[bar][2];
        return lum >= 0.5 ? 1.0 : lum;   // 1-bit threshold, grey keeps the level
    }
    }
}

// Pattern with duplex flip applied: the back side arrives rotated 180 degrees,
// which is how several ADF drivers actually deliver it (and the reason the
// regression list in plan section 18.2 calls out "duplex back-side ordering
// and 180 rotation").
static double Pat(int x, int y, int ch)
{
    if (S.pageIndex & 1)
    {
        x = S.imageWidth - 1 - x;
        y = S.imageHeight - 1 - y;
    }
    return Pattern(x, y, ch);
}

// Fills one row into dst in memory-transfer order (RGB, row-packed).
static void FillRow(unsigned char* dst, int y)
{
    int bpc = BitsPerSample();
    if (S.pixelType == TWPT_BW)
    {
        memset(dst, 0, BytesPerRowPacked());
        for (int x = 0; x < S.imageWidth; x++)
            if (Pat(x, y, 0) >= 0.5)
                dst[x >> 3] |= (unsigned char)(0x80 >> (x & 7));
        return;
    }
    int spp = SamplesPerPixel();
    if (bpc == 16)
    {
        unsigned short* p = (unsigned short*)dst;
        for (int x = 0; x < S.imageWidth; x++)
            for (int c = 0; c < spp; c++)
                *p++ = (unsigned short)floor(Pat(x, y, c) * 65535.0 + 0.5);
        return;
    }
    for (int x = 0; x < S.imageWidth; x++)
        for (int c = 0; c < spp; c++)
            *dst++ = (unsigned char)floor(Pat(x, y, c) * 255.0 + 0.5);
}

// ------------------------------------------------------------------ capability handling
static bool CapGet(unsigned short cap, unsigned short msg, TW_CAPABILITY* pCap)
{
    HANDLE h = NULL;
    unsigned short con = TWON_ONEVALUE;

    switch (cap)
    {
    case ICAP_XRESOLUTION:
    case ICAP_YRESOLUTION:
    {
        static const double res[] = { 75, 150, 300, 600, 1200 };
        double cur = (cap == ICAP_XRESOLUTION) ? S.xRes : S.yRes;
        int curIdx = 2, defIdx = 2;
        for (int i = 0; i < 5; i++) if (res[i] == cur) curIdx = i;
        if (msg == MSG_GET) { h = BuildEnumFix(res, 5, curIdx, defIdx); con = TWON_ENUMERATION; }
        else h = BuildOneFix(cur);
        break;
    }
    case ICAP_PIXELTYPE:
    {
        if (msg == MSG_GET)
        {
            unsigned int vals[3] = { TWPT_BW, TWPT_GRAY, TWPT_RGB };
            int cur = 2;
            for (int i = 0; i < 3; i++) if (vals[i] == S.pixelType) cur = i;
            h = BuildEnumU16(TWTY_UINT16, vals, 3, cur, 2);
            con = TWON_ENUMERATION;
        }
        else h = BuildOneU16(TWTY_UINT16, S.pixelType);
        break;
    }
    case ICAP_BITDEPTH:
    {
        unsigned int depths[2];
        int n = 0, cur = 0;
        if (S.pixelType == TWPT_RGB) { depths[0] = 24; depths[1] = 48; n = 2; }
        else if (S.pixelType == TWPT_GRAY) { depths[0] = 8; depths[1] = 16; n = 2; }
        else { depths[0] = 1; n = 1; }
        for (int i = 0; i < n; i++) if ((int)depths[i] == (int)S.bitDepth) cur = i;
        if (msg == MSG_GET) { h = BuildEnumU16(TWTY_UINT16, depths, n, cur, 0); con = TWON_ENUMERATION; }
        else h = BuildOneU16(TWTY_UINT16, S.bitDepth);
        break;
    }
    case ICAP_UNITS:
    {
        if (msg == MSG_GET)
        {
            unsigned int vals[7] = { 0,1,2,3,4,5,6 };
            int cur = (int)S.units; if (cur > 6) cur = 6;
            h = BuildEnumU16(TWTY_UINT16, vals, 7, cur, 0);
            con = TWON_ENUMERATION;
        }
        else h = BuildOneU16(TWTY_UINT16, S.units);
        break;
    }
    case ICAP_XFERMECH:
    {
        if (msg == MSG_GET)
        {
            unsigned int vals[3] = { TWSX_NATIVE, 1, TWSX_MEMORY };
            int cur = (S.xferMech == TWSX_MEMORY) ? 2 : 0;
            h = BuildEnumU16(TWTY_UINT16, vals, 3, cur, 2);
            con = TWON_ENUMERATION;
        }
        else h = BuildOneU16(TWTY_UINT16, S.xferMech);
        break;
    }
    case ICAP_COMPRESSION:
        h = BuildOneU16(TWTY_UINT16, 0);
        break;
    case ICAP_PHYSICALWIDTH:  h = BuildOneFix(8.5);  break;
    case ICAP_PHYSICALHEIGHT: h = BuildOneFix(11.7); break;
    case ICAP_BRIGHTNESS:
        if (msg == MSG_GET) { h = BuildRangeFix(-1000, 1000, 1, 0, S.brightness); con = TWON_RANGE; }
        else h = BuildOneFix(S.brightness);
        break;
    case ICAP_CONTRAST:
        if (msg == MSG_GET) { h = BuildRangeFix(-1000, 1000, 1, 0, S.contrast); con = TWON_RANGE; }
        else h = BuildOneFix(S.contrast);
        break;
    case CAP_XFERCOUNT:
        h = BuildOneU16(TWTY_INT16, (unsigned short)S.xferCount);
        break;
    case CAP_UICONTROLLABLE:
        // The refuses-UI personality advertises that hidden UI is impossible,
        // which is how the UI learns to show the vendor dialog instead.
        h = BuildOneU16(TWTY_BOOL, (S.personality == PERS_REFUSESUI) ? 0 : 1);
        break;
    case CAP_INDICATORS:
        h = BuildOneU16(TWTY_BOOL, 1);
        break;
    case CAP_FEEDERENABLED:
        // Advertised, like the LiDE 400, but a flatbed refuses to be switched
        // on - the honest behaviour the capability reader tests for. The duplex
        // personality has real (simulated) feeder hardware and accepts it.
        if (msg == MSG_GET)
        {
            unsigned int vals[2] = { 0, 1 };
            int cur = S.feederEnabled ? 1 : 0;
            h = BuildEnumU16(TWTY_BOOL, vals, 2, cur, 0);
            con = TWON_ENUMERATION;
        }
        else h = BuildOneU16(TWTY_BOOL, S.feederEnabled ? 1 : 0);
        break;
    case CAP_DUPLEX:
        // Only the duplex personality has duplex hardware to report; anything
        // else leaves the capability unsupported, as a flatbed should.
        if (S.personality == PERS_DUPLEX) h = BuildOneU16(TWTY_UINT16, 1);   // TWDX_1PASSDUPLEX
        else { S.lastCC = TWCC_CAPUNSUPPORTED; return false; }
        break;
    case CAP_DUPLEXENABLED:
        h = BuildOneU16(TWTY_BOOL, 0);
        break;
    default:
        S.lastCC = TWCC_CAPUNSUPPORTED;
        return false;
    }

    if (!h) { S.lastCC = TWCC_OPERATIONERROR; return false; }
    pCap->Cap = cap;
    pCap->ConType = con;
    pCap->hContainer = h;
    return true;
}

// Returns true when the SET is accepted. pCap's container is application-owned
// (ONEVALUE); we must read it and let the caller free it.
static bool CapSet(unsigned short cap, TW_CAPABILITY* pCap)
{
    if (!pCap || !pCap->hContainer) { S.lastCC = TWCC_BADVALUE; return false; }
    TW_ONEVALUE* ov = (TW_ONEVALUE*)GlobalLock(pCap->hContainer);
    if (!ov) { S.lastCC = TWCC_OPERATIONERROR; return false; }
    unsigned int item = ov->Item;
    GlobalUnlock(pCap->hContainer);

    switch (cap)
    {
    case ICAP_XRESOLUTION:
    case ICAP_YRESOLUTION:
        // The set-lies personality accepts MSG_SET and then ignores it, staying
        // at 150 dpi forever - exactly the "returns a resolution it was not
        // asked for" trap. The session's read-back verification is the defence.
        if (S.personality != PERS_SETLIES)
        {
            if (cap == ICAP_XRESOLUTION) S.xRes = FixToDouble(*(TW_FIX32*)&item);
            else S.yRes = FixToDouble(*(TW_FIX32*)&item);
        }
        break;
    case ICAP_PIXELTYPE:
    {
        unsigned short pt = (unsigned short)item;
        if (pt != TWPT_BW && pt != TWPT_GRAY && pt != TWPT_RGB) { S.lastCC = TWCC_BADVALUE; return false; }
        // A forced-mode personality swallows the SET without applying it; the
        // device keeps delivering what its "firmware" is stuck in.
        if (S.forcedType < 0)
        {
            S.pixelType = pt;
            S.bitDepth = (pt == TWPT_RGB) ? 24 : (pt == TWPT_GRAY ? 8 : 1);
        }
        break;
    }
    case ICAP_BITDEPTH:
    {
        // Spec convention: bits per PIXEL. Accept only depths legal for the
        // current pixel type; Canon-style bits-per-channel drivers are a
        // separate personality.
        unsigned int d = item;
        bool ok = (S.pixelType == TWPT_RGB && (d == 24 || d == 48)) ||
                  (S.pixelType == TWPT_GRAY && (d == 8 || d == 16)) ||
                  (S.pixelType == TWPT_BW && d == 1);
        if (!ok) { S.lastCC = TWCC_BADVALUE; return false; }
        if (S.forcedType < 0) S.bitDepth = (unsigned short)d;
        break;
    }
    case ICAP_UNITS:
        if (item > 6) { S.lastCC = TWCC_BADVALUE; return false; }
        S.units = (unsigned short)item;
        break;
    case ICAP_XFERMECH:
        if (item > 2) { S.lastCC = TWCC_BADVALUE; return false; }
        S.xferMech = (unsigned short)item;
        break;
    case ICAP_COMPRESSION:
        if (item != 0) { S.lastCC = TWCC_BADVALUE; return false; }
        break;
    case CAP_XFERCOUNT:
    {
        short c = (short)(unsigned short)item;
        if (c < -1 || (c > 50 && c != -1)) { S.lastCC = TWCC_BADVALUE; return false; }
        S.xferCount = c;
        break;
    }
    case CAP_INDICATORS:
        break;   // accepted, no visible behaviour to change
    case CAP_DUPLEXENABLED:
        break;   // accepted but there is no duplex hardware
    case CAP_FEEDERENABLED:
        if (item != 0)
        {
            // Flatbeds refuse the feeder; only the duplex personality has one.
            if (S.personality != PERS_DUPLEX) { S.lastCC = TWCC_BADVALUE; return false; }
            S.feederEnabled = true;
        }
        else S.feederEnabled = false;
        break;
    case ICAP_BRIGHTNESS: S.brightness = FixToDouble(*(TW_FIX32*)&item); break;
    case ICAP_CONTRAST:   S.contrast   = FixToDouble(*(TW_FIX32*)&item); break;
    default:
        S.lastCC = TWCC_CAPUNSUPPORTED;
        return false;
    }
    return true;
}

static bool CapSupported(unsigned short cap)
{
    switch (cap)
    {
    case ICAP_XRESOLUTION: case ICAP_YRESOLUTION: case ICAP_PIXELTYPE:
    case ICAP_BITDEPTH: case ICAP_UNITS: case ICAP_XFERMECH:
    case ICAP_COMPRESSION: case ICAP_PHYSICALWIDTH: case ICAP_PHYSICALHEIGHT:
    case ICAP_BRIGHTNESS: case ICAP_CONTRAST: case CAP_XFERCOUNT:
    case CAP_UICONTROLLABLE: case CAP_INDICATORS: case CAP_FEEDERENABLED:
    case CAP_DUPLEXENABLED:
        return true;
    default:
        return false;
    }
}

// ------------------------------------------------------------------ identity
static void FillIdentity(TW_IDENTITY* id)
{
    memset(id, 0, sizeof(*id));
    id->Id = 0x51A4;
    id->Version.MajorNum = 1; id->Version.MinorNum = 0;
    id->Version.Language = 13; id->Version.Country = 1;
    strcpy_s(id->Version.Info, "NextScan Simulator 1.0");
    id->ProtocolMajor = 2; id->ProtocolMinor = 4;
    id->SupportedGroups = 0x0003;   // DG_CONTROL | DG_IMAGE, no DF_DSM2/DS2
    strcpy_s(id->Manufacturer, "NextScan");
    strcpy_s(id->ProductFamily, "Simulator");
    strcpy_s(id->ProductName, "NextScan Simulator");
}

// ------------------------------------------------------------------ native DIB (fallback path)
static HANDLE BuildNativeDib()
{
    // 24-bit bottom-up BI_RGB DIB with 4-byte row alignment - the layout the
    // session's DibDecoder expects from a real source. Used only when memory
    // transfer failed, which the well-behaved personality never triggers.
    int w = S.imageWidth, h = S.imageHeight;
    int stride = ((w * 3 + 3) / 4) * 4;
    BITMAPINFOHEADER bh;
    memset(&bh, 0, sizeof(bh));
    bh.biSize = sizeof(BITMAPINFOHEADER);
    bh.biWidth = w;
    // Positive height = bottom-up rows (the DIB default); the top-down
    // personality ships the negative-height variant, which DibDecoder must
    // accept. Both exist in real drivers.
    bh.biHeight = (S.personality == PERS_TOPDOWN) ? -h : h;
    bh.biPlanes = 1;
    bh.biBitCount = 24;
    bh.biCompression = BI_RGB;
    bh.biSizeImage = (DWORD)(stride * h);
    bh.biXPelsPerMeter = (DWORD)(S.xRes * 10000.0 / 254.0 + 0.5);
    bh.biYPelsPerMeter = (DWORD)(S.yRes * 10000.0 / 254.0 + 0.5);

    HANDLE hDib = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, sizeof(bh) + (SIZE_T)stride * h);
    if (!hDib) return NULL;
    unsigned char* base = (unsigned char*)GlobalLock(hDib);
    memcpy(base, &bh, sizeof(bh));
    for (int y = 0; y < h; y++)
    {
        // Bottom-up DIBs store the last image row first.
        int dibRow = (S.personality == PERS_TOPDOWN) ? y : (h - 1 - y);
        unsigned char* row = base + sizeof(bh) + (SIZE_T)dibRow * stride;
        for (int x = 0; x < w; x++)
        {
            // DIBs are BGR; the pattern function speaks RGB.
            row[x * 3 + 0] = (unsigned char)floor(Pat(x, y, 2) * 255.0 + 0.5);
            row[x * 3 + 1] = (unsigned char)floor(Pat(x, y, 1) * 255.0 + 0.5);
            row[x * 3 + 2] = (unsigned char)floor(Pat(x, y, 0) * 255.0 + 0.5);
        }
    }
    GlobalUnlock(hDib);
    return hDib;
}

// ------------------------------------------------------------------ DSM_Entry
#define TW_CALL __stdcall

typedef TW_IDENTITY* pTW_IDENTITY;

extern "C" TW_UINT16 TW_CALL DSM_Entry(
    pTW_IDENTITY pOrigin, pTW_IDENTITY pDest,
    TW_UINT32 DG_, TW_UINT16 DAT_, TW_UINT16 MSG_, TW_MEMREF pData)
{
    (void)pOrigin;

    if (DAT_ == DAT_PARENT && MSG_ == MSG_OPENDSM)
    {
        memset(&S, 0, sizeof(S));
        ReadConfig();
        S.dsmOpen = true;
        S.parent = pData ? *(HWND*)pData : NULL;
        S.xRes = S.yRes = 300;
        S.pixelType = TWPT_RGB;
        S.bitDepth = 24;
        // A forced-mode personality is stuck from the moment the source opens,
        // the way a driver with a firmware-fixed mode is.
        if (S.forcedType >= 0) { S.pixelType = (unsigned short)S.forcedType; S.bitDepth = (unsigned short)S.forcedDepth; }
        // The set-lies personality scans at 150 dpi no matter what was asked.
        if (S.personality == PERS_SETLIES) S.xRes = S.yRes = 150;
        S.xferMech = TWSX_MEMORY;
        S.frameSet = false;
        Logf("OPENDSM parent=%p personality=%d", (void*)S.parent, (int)S.personality);
        return TWRC_SUCCESS;
    }
    if (DAT_ == DAT_PARENT && MSG_ == MSG_CLOSEDSM)
    {
        S.dsmOpen = false;
        Logf("CLOSEDSM");
        return TWRC_SUCCESS;
    }

    if (!S.dsmOpen) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }

    // ---- DSM-level status: legal before any data source opens, and the ONLY
    // way the application learns WHY OPENDS refused (a real DSM answers it at
    // this level too), so it must sit above the dsOpen gate ----
    if (DAT_ == DAT_STATUS && MSG_ == MSG_GET)
    {
        TW_STATUS* st = (TW_STATUS*)pData;
        if (st) { st->ConditionCode = S.lastCC; st->Data = 0; }
        TW_UINT16 cc = S.lastCC;
        S.lastCC = TWCC_SUCCESS;   // reading status clears it, as real DSMs do
        return TWRC_SUCCESS;
    }

    // ---- DSM-level: source enumeration ----
    if (DAT_ == DAT_IDENTITY && (MSG_ == MSG_GETFIRST || MSG_ == MSG_GETNEXT))
    {
        if (MSG_ == MSG_GETFIRST && pData)
        {
            FillIdentity((TW_IDENTITY*)pData);
            Logf("GETFIRST -> NextScan Simulator");
            return TWRC_SUCCESS;
        }
        return TWRC_ENDOFLIST;
    }

    if (DAT_ == DAT_IDENTITY && MSG_ == MSG_OPENDS)
    {
        if (!pData) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }

        // The busy personality reproduces the field failure that forced a reboot:
        // a previous host died holding the data source, so the first OPENDS gets
        // TWCC_MAXCONNECTIONS ("another program is using the scanner") and only a
        // patient retry succeeds. The session's retry-with-backoff is the fix.
        if (S.personality == PERS_BUSY && S.opendsAttempts == 0)
        {
            S.opendsAttempts++;
            S.lastCC = TWCC_MAXCONNECTIONS;
            Logf("OPENDS refused with MAXCONNECTIONS (personality=busy)");
            return TWRC_FAILURE;
        }
        S.opendsAttempts++;

        FillIdentity((TW_IDENTITY*)pData);
        S.dsOpen = true;
        Logf("OPENDS");
        return TWRC_SUCCESS;
    }
    if (DAT_ == DAT_IDENTITY && MSG_ == MSG_CLOSEDS)
    {
        S.dsOpen = false;
        S.enabled = false;
        Logf("CLOSEDS");
        return TWRC_SUCCESS;
    }

    // ---- everything below is a data-source triplet ----
    if (!S.dsOpen) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }

    if (DAT_ == DAT_CAPABILITY)
    {
        TW_CAPABILITY* pCap = (TW_CAPABILITY*)pData;
        if (!pCap) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }

        if (MSG_ == MSG_QUERYSUPPORT)
        {
            if (!CapSupported(pCap->Cap)) { S.lastCC = TWCC_CAPUNSUPPORTED; return TWRC_FAILURE; }
            pCap->ConType = TWON_ONEVALUE;
            pCap->hContainer = BuildOneU16(TWTY_UINT16, MSG_GET | MSG_GETCURRENT | MSG_GETDEFAULT | MSG_SET);
            return pCap->hContainer ? TWRC_SUCCESS : TWRC_FAILURE;
        }
        if (MSG_ == MSG_GET || MSG_ == MSG_GETCURRENT || MSG_ == MSG_GETDEFAULT)
        {
            if (!CapGet(pCap->Cap, MSG_, pCap))
            {
                Logf("CAP GET 0x%04x -> unsupported", pCap->Cap);
                return TWRC_FAILURE;
            }
            Logf("CAP GET 0x%04x ok", pCap->Cap);
            return TWRC_SUCCESS;
        }
        if (MSG_ == MSG_SET)
        {
            if (!CapSet(pCap->Cap, pCap))
            {
                Logf("CAP SET 0x%04x -> refused (cc=%u)", pCap->Cap, S.lastCC);
                return TWRC_FAILURE;
            }
            Logf("CAP SET 0x%04x ok", pCap->Cap);
            return TWRC_SUCCESS;
        }
        S.lastCC = TWCC_CAPBADOPERATION;
        return TWRC_FAILURE;
    }

    if (DAT_ == DAT_USERINTERFACE && MSG_ == MSG_ENABLEDS)
    {
        TW_USERINTERFACE* ui = (TW_USERINTERFACE*)pData;
        if (ui && ui->hParent) S.parent = ui->hParent;

        // refuses-ShowUI=FALSE is a documented real-driver behaviour; the DSM
        // reports OPERATIONERROR and the session surfaces TwainEnableFailed.
        if (S.personality == PERS_REFUSESUI && ui && ui->ShowUI == 0)
        {
            S.lastCC = TWCC_OPERATIONERROR;
            Logf("ENABLEDS refused (ShowUI=0, personality=refusesui)");
            return TWRC_FAILURE;
        }

        // The hang personality never comes back. Nothing in-process can save
        // the caller; this is what the broker's watchdog exists for.
        if (S.personality == PERS_HANG)
        {
            Logf("ENABLEDS hanging forever (personality=hang)");
            Sleep(INFINITE);
        }

        S.enabled = true;
        S.imageReady = true;
        S.xferReadyReported = false;
        S.pageIndex = 0;
        // Natural page count: front+back for a duplex feeder, one otherwise.
        // CAP_XFERCOUNT caps it when the application asked for fewer.
        int natural = (S.personality == PERS_DUPLEX && S.feederEnabled) ? 2 : 1;
        S.pendingPages = natural;
        if (S.xferCount >= 1 && S.xferCount < natural) S.pendingPages = S.xferCount;
        ComputeImageSize();
        S.rowsSent = 0;

        // Deliver MSG_XFERREADY the way real sources do: wake the parent's
        // pump so it calls PROCESSEVENT. Returning it synchronously here is
        // also legal, but the pump path is what production exercises.
        if (S.parent) PostMessageW(S.parent, SIM_WAKEUP_MSG, 0, 0);
        Logf("ENABLEDS showUI=%u image %dx%d pending=%d",
             ui ? ui->ShowUI : 0xFFFF, S.imageWidth, S.imageHeight, S.pendingPages);
        return TWRC_SUCCESS;
    }
    if (DAT_ == DAT_USERINTERFACE && MSG_ == MSG_DISABLEDS)
    {
        S.enabled = false;
        S.imageReady = false;
        Logf("DISABLEDS");
        return TWRC_SUCCESS;
    }

    if (DAT_ == DAT_EVENT && MSG_ == MSG_PROCESSEVENT)
    {
        TW_EVENT* ev = (TW_EVENT*)pData;
        if (!ev) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }
        if (S.enabled && S.imageReady && !S.xferReadyReported)
        {
            ev->TWMessage = MSG_XFERREADY;
            S.xferReadyReported = true;
            Logf("PROCESSEVENT -> XFERREADY");
            return TWRC_DSEVENT;
        }
        ev->TWMessage = MSG_NULL;
        return TWRC_NOTDSEVENT;
    }

    if (DAT_ == DAT_IMAGELAYOUT && MSG_ == MSG_SET)
    {
        TW_IMAGELAYOUT* lay = (TW_IMAGELAYOUT*)pData;
        if (!lay) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }
        double l = FixToDouble(lay->Frame.Left), t = FixToDouble(lay->Frame.Top);
        double r = FixToDouble(lay->Frame.Right), b = FixToDouble(lay->Frame.Bottom);
        // Clamp to the physical bed; a source silently clamping is what
        // hardware does, and the session verifies sizes afterwards anyway.
        if (l < 0) l = 0; if (t < 0) t = 0;
        if (r > 8.5) r = 8.5; if (b > 11.7) b = 11.7;
        if (r - l < 0.01) r = l + 0.01;
        if (b - t < 0.01) b = t + 0.01;
        S.frameL = l; S.frameT = t; S.frameR = r; S.frameB = b;
        S.frameSet = true;
        Logf("IMAGELAYOUT %.3f,%.3f -> %.3f,%.3f", l, t, r, b);
        return TWRC_SUCCESS;
    }
    if (DAT_ == DAT_IMAGELAYOUT && MSG_ == MSG_GET)
    {
        TW_IMAGELAYOUT* lay = (TW_IMAGELAYOUT*)pData;
        if (lay)
        {
            lay->Frame.Left = FixFromDouble(S.frameSet ? S.frameL : 0);
            lay->Frame.Top = FixFromDouble(S.frameSet ? S.frameT : 0);
            lay->Frame.Right = FixFromDouble(S.frameSet ? S.frameR : 8.5);
            lay->Frame.Bottom = FixFromDouble(S.frameSet ? S.frameB : 11.7);
            lay->DocumentNumber = 1; lay->PageNumber = (TW_UINT32)(S.pageIndex + 1); lay->FrameNumber = 1;
        }
        return TWRC_SUCCESS;
    }

    if (DAT_ == DAT_SETUPMEMXFER && MSG_ == MSG_GET)
    {
        TW_SETUPMEMXFER* sm = (TW_SETUPMEMXFER*)pData;
        if (sm) { sm->MinBufSize = 32768; sm->MaxBufSize = 1u << 21; sm->Preferred = 65536; }
        return TWRC_SUCCESS;
    }

    if (DAT_ == DAT_PENDINGXFERS && MSG_ == MSG_ENDXFER)
    {
        TW_PENDINGXFERS* px = (TW_PENDINGXFERS*)pData;
        if (S.pendingPages > 0) S.pendingPages--;
        if (S.pendingPages > 0)
        {
            // Another page is queued: re-arm the ready state. The session's
            // transfer loop asks for IMAGEINFO again without re-pumping, so no
            // message post is needed here.
            S.imageReady = true;
            S.xferReadyReported = true;
            S.pageIndex++;
            ComputeImageSize();
            S.rowsSent = 0;
        }
        else
        {
            S.imageReady = false;
        }
        if (px) { px->Count = (TW_UINT16)S.pendingPages; px->EOJ = S.pendingPages ? 0 : 1; }
        Logf("ENDXFER pending=%d", S.pendingPages);
        return TWRC_SUCCESS;
    }
    if (DAT_ == DAT_PENDINGXFERS && MSG_ == MSG_RESET)
    {
        TW_PENDINGXFERS* px = (TW_PENDINGXFERS*)pData;
        S.pendingPages = 0;
        S.imageReady = false;
        if (px) { px->Count = 0; px->EOJ = 1; }
        Logf("PENDINGXFERS RESET");
        return TWRC_SUCCESS;
    }

    if (DAT_ == DAT_IMAGEINFO && MSG_ == MSG_GET)
    {
        if (!S.imageReady) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }
        TW_IMAGEINFO* info = (TW_IMAGEINFO*)pData;
        if (info)
        {
            memset(info, 0, sizeof(*info));
            info->XResolution = FixFromDouble(S.xRes);
            info->YResolution = FixFromDouble(S.yRes);
            info->ImageWidth = S.imageWidth;
            info->ImageLength = S.imageHeight;
            info->SamplesPerPixel = (TW_INT16)SamplesPerPixel();
            int bps = BitsPerSample();
            for (int i = 0; i < 8; i++) info->BitsPerSample[i] = (TW_INT16)((i < SamplesPerPixel()) ? bps : 0);
            info->BitsPerPixel = (TW_INT16)BitsPerPixel();
            info->Planar = 0;
            info->PixelType = (TW_INT16)S.pixelType;
            info->Compression = 0;
        }
        Logf("IMAGEINFO %dx%d spp=%d bps=%d", S.imageWidth, S.imageHeight, SamplesPerPixel(), BitsPerSample());
        return TWRC_SUCCESS;
    }

    if (DAT_ == DAT_IMAGEMEMXFER && MSG_ == MSG_GET)
    {
        if (!S.imageReady) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }

        // Sources that implement TWSX_MEMORY badly or not at all force the
        // session onto its native-DIB fallback - that fallback is the code the
        // bottom-up/top-down personalities exist to cover.
        if (S.personality == PERS_BOTTOMUP || S.personality == PERS_TOPDOWN)
        {
            S.lastCC = TWCC_BADPROTOCOL;
            Logf("IMAGEMEMXFER refused (native-only personality)");
            return TWRC_FAILURE;
        }

        // The crash personality faults on the first strip call, in state 7 -
        // the moment a vendor driver is most likely to die. The host process
        // is expected to vanish; the point of the test is that nothing else
        // does.
        if (S.personality == PERS_CRASH7)
        {
            Logf("IMAGEMEMXFER crashing deliberately (personality=crash7)");
            *(volatile int*)0 = 0x0BADF00D;
        }

        TW_IMAGEMEMXFER* mx = (TW_IMAGEMEMXFER*)pData;
        if (!mx || !mx->Memory.TheMem) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }

        int bpr = BytesPerRowPacked();
        int rowsLeft = S.imageHeight - S.rowsSent;
        if (rowsLeft <= 0) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }

        // never exceed one row past the buffer the application provided
        DWORD cap = mx->Memory.Length;
        int maxRows = (int)(cap / (DWORD)bpr);
        if (maxRows < 1) maxRows = 1;
        int rows = rowsLeft < maxRows ? rowsLeft : maxRows;
        if ((DWORD)rows * bpr > cap) rows = (int)(cap / bpr);
        if (rows < 1) { S.lastCC = TWCC_BADVALUE; return TWRC_FAILURE; }

        unsigned char* dst = (unsigned char*)mx->Memory.TheMem;
        for (int i = 0; i < rows; i++)
            FillRow(dst + (SIZE_T)i * bpr, S.rowsSent + i);
        S.rowsSent += rows;

        mx->Compression = 0;
        mx->BytesPerRow = (TW_UINT32)bpr;
        mx->Columns = (TW_UINT32)S.imageWidth;
        mx->Rows = (TW_UINT32)rows;
        mx->XOffset = 0;
        mx->YOffset = (TW_UINT32)(S.rowsSent - rows);
        mx->BytesWritten = (TW_UINT32)(rows * bpr);

        bool done = S.rowsSent >= S.imageHeight;
        Logf("IMAGEMEMXFER rows=%u/%d%s", mx->Rows, S.imageHeight, done ? " DONE" : "");
        return done ? TWRC_XFERDONE : TWRC_SUCCESS;
    }

    if (DAT_ == DAT_IMAGENATIVEXFER && MSG_ == MSG_GET)
    {
        if (!S.imageReady) { S.lastCC = TWCC_SEQERROR; return TWRC_FAILURE; }
        HANDLE hDib = BuildNativeDib();
        if (!hDib) { S.lastCC = TWCC_OPERATIONERROR; return TWRC_FAILURE; }
        if (pData) *(HANDLE*)pData = hDib;
        S.rowsSent = S.imageHeight;   // whole image delivered in one handle
        Logf("IMAGENATIVEXFER dib=%p", (void*)hDib);
        return TWRC_XFERDONE;
    }

    if (DAT_ == DAT_ENTRYPOINT && MSG_ == MSG_GET)
    {
        // We never claim DF_DSM2, so a compliant application does not ask;
        // fail cleanly if it does, and keep the session on the Global* path.
        S.lastCC = TWCC_CAPUNSUPPORTED;
        return TWRC_FAILURE;
    }

    Logf("unhandled triplet DG=%04x DAT=%04x MSG=%04x", DG_, DAT_, MSG_);
    S.lastCC = TWCC_CAPUNSUPPORTED;
    return TWRC_FAILURE;
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(hModule);
    return TRUE;
}
