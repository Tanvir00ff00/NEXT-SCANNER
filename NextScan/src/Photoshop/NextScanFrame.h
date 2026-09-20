// =============================================================================
// NextScan Studio - the shape of a frame handed to Photoshop
// Plan ref: master plan section 14.3.
//
// This header is the contract between two languages and two processes: the
// acquire module reads what the application writes. It is duplicated in C# in
// StudioPsBridge.cs, so any change here must be made there in the same commit
// or a scan will arrive as noise.
//
// Everything is fixed width and little-endian, both sides run on the same
// machine, and the struct is written byte for byte at the head of the shared
// mapping with the pixels immediately after it.
// =============================================================================
#pragma once

#include <stdint.h>

#define NEXTSCAN_FRAME_MAGIC   0x5246534EL   /* 'NSFR' little-endian */
#define NEXTSCAN_FRAME_VERSION 2

#pragma pack(push, 4)
struct NextScanFrame
{
    int32_t magic;            /* NEXTSCAN_FRAME_MAGIC */
    int32_t version;          /* NEXTSCAN_FRAME_VERSION */

    int32_t width;
    int32_t height;
    int32_t channels;         /* 1 grey, 3 RGB */
    int32_t bitsPerChannel;   /* 8 or 16 */

    /* Bytes between the starts of two rows. Rows may be padded, so this is
       not always width * channels * bytes per sample. */
    int32_t stride;

    /* Samples are RGB in that order, never BGR: the capture is BGR and the
       conversion happens once on the writing side, where it is a line of
       managed code, rather than in every reader. */
    int32_t sampleOrderIsRgb;

    double  dpiX;
    double  dpiY;

    int32_t pixelBytes;       /* stride * height */
    int32_t iccBytes;         /* 0 when the capture carried no profile */

    /* Offsets from the start of the mapping. */
    int32_t pixelOffset;
    int32_t iccOffset;

    int32_t pageIndex;        /* 1-based, for a multi-item scan */
    int32_t pageCount;
    int32_t reserved[6];
};
#pragma pack(pop)

/* Named objects, all suffixed with the session id the plug-in generates so two
   Photoshops asking at once cannot collide. Local\ rather than Global\ because
   both processes are the same user and Global needs a privilege we should not
   be asking for.

   One scan can carry several items - five cards laid on the glass are five
   pages - so the mapping name carries the page number as well:

       Local\NextScan.Frame.<session>.<page>      the page number is 1 based

   A name per page rather than one name reused, because the application builds
   the next page while the plug-in may still hold the last one open, and two
   mappings cannot share a name.

   Ready and Done are auto reset. Each page is one exchange - the application
   signals Ready, the plug-in signals Done - and an auto reset event is emptied
   by the wait that receives it, so neither side has to remember to clear it
   before the next page. A forgotten reset would not fail loudly: it would hand
   Photoshop the same page twice.

   Cancel is manual reset and means the same thing from either direction: no
   more pages. The application sets it when the operator closes it without
   scanning, and the plug-in sets it when Photoshop will take no more - which
   is also what happens on a host that ignores acquireAgain. */
#define NEXTSCAN_MAPPING_PREFIX  L"Local\\NextScan.Frame."
#define NEXTSCAN_READY_PREFIX    L"Local\\NextScan.Ready."
#define NEXTSCAN_DONE_PREFIX     L"Local\\NextScan.Done."
#define NEXTSCAN_CANCEL_PREFIX   L"Local\\NextScan.Cancel."
