// =============================================================================
// NextScan Studio - Photoshop acquire module (.8ba)
// Plan ref: master plan section 14.1 connector B, section 14.3 handoff.
//
// This is the connector that puts us in File -> Import -> Next Scanner, which
// is where every other scanner driver lives and therefore where people look.
// The same binary works from CS6 to 2026, while UXP needs 2021 or later.
//
// How a scan gets here
// --------------------
// Photoshop calls us; we start the application, it scans, and it publishes the
// page into a shared mapping that we stream straight into the document. The
// pixels are never written to disk and never converted on the way: sixteen bit
// stays sixteen bit and the ICC profile travels with it, because a scanner
// front end that quietly costs you a profile or a bit depth is worse than no
// integration at all.
//
// Two conventions in here are Photoshop's and are easy to get silently wrong:
//   - sixteen bit samples run 0..32768, NOT 0..65535
//   - imageHRes/imageVRes are Fixed 16.16, so dpi is shifted left sixteen
//
// Both are converted at the single point where the data crosses over.
// =============================================================================

#include "PIDefines.h"
#include "PIAcquire.h"
#include "PIAbout.h"
#include "NextScanFrame.h"

#include <windows.h>
#include <string>

SPBasicSuite* sSPBasic = NULL;

namespace
{
    const wchar_t* kProduct = L"Next Scanner";

    /* How long to wait for a scan. Generous: the operator may be placing
       documents, adjusting a selection, or previewing twice before they are
       happy, and a timeout that fires mid-decision is a bug from their side of
       the screen. */
    const DWORD kWaitTotalMs = 30 * 60 * 1000;
    const DWORD kWaitSliceMs = 200;

    struct Session
    {
        std::wstring id;
        HANDLE mapping;
        HANDLE ready;
        HANDLE done;
        HANDLE cancel;
        HANDLE app;
        const unsigned char* view;
        const NextScanFrame* frame;
        int32_t nextRow;
        int32_t nextPage;         /* 1 based: which mapping to open next */
        int32_t pageCount;        /* learned from the page the application sent */

        Session() : mapping(NULL), ready(NULL), done(NULL), cancel(NULL), app(NULL),
                    view(NULL), frame(NULL), nextRow(0), nextPage(1), pageCount(0) {}
    };

    Session g;

    void Say(const wchar_t* text, UINT icon = MB_ICONINFORMATION)
    {
        MessageBoxW(GetActiveWindow(), text, kProduct, MB_OK | icon);
    }

    void DoAbout(AboutRecordPtr about)
    {
        sSPBasic = about->sSPBasic;
        Say(L"Next Scanner\n\nScanner acquisition for Adobe Photoshop.\n"
            L"Connector B (acquire module).");
    }

    std::wstring Named(const wchar_t* prefix, const std::wstring& id)
    {
        return std::wstring(prefix) + id;
    }

    /* Where the application lives. It records its own path on every run, which
       means the plug-in keeps working when the application is moved and needs
       no installer step of its own. The sweep afterwards is only for the case
       where the application has never been run on this machine. */
    /* Reads Software\NextScan\AppPath from one hive, and only accepts it if
       what it names is actually there. */
    bool AppPathFrom(HKEY hive, std::wstring& found)
    {
        HKEY key;
        if (RegOpenKeyExW(hive, L"Software\\NextScan", 0, KEY_READ | KEY_WOW64_64KEY, &key)
            != ERROR_SUCCESS) return false;

        wchar_t buffer[MAX_PATH];
        DWORD size = sizeof(buffer) - sizeof(wchar_t);   /* room to terminate */
        DWORD type = 0;
        LONG got = RegQueryValueExW(key, L"AppPath", NULL, &type,
                                    reinterpret_cast<LPBYTE>(buffer), &size);
        RegCloseKey(key);

        if (got != ERROR_SUCCESS || type != REG_SZ) return false;
        buffer[size / sizeof(wchar_t)] = 0;
        if (GetFileAttributesW(buffer) == INVALID_FILE_ATTRIBUTES) return false;

        found = buffer;
        return true;
    }

    std::wstring FindApplication()
    {
        /* Two hives, in this order, for two different reasons.

           The application rewrites its own path under HKCU on every run, so
           that one follows a rebuild or a move and is the more current answer.
           The installer writes HKLM for every user on the machine, which is
           what answers before the application has ever been started -- and
           picking Next Scanner from Photoshop's Import menu is a perfectly
           ordinary first thing to do after installing.

           HKLM is read from the 64 bit view explicitly. This module is 64 bit
           but the installer that wrote the value need not have been, and a
           value written on one side of the registry redirector is invisible
           from the other. */
        std::wstring found;
        if (AppPathFrom(HKEY_CURRENT_USER, found)) return found;
        if (AppPathFrom(HKEY_LOCAL_MACHINE, found)) return found;

        /* Last resort, for a copy that was never installed and never run. */
        const wchar_t* guesses[] =
        {
            L"C:\\Program Files\\NextScan Studio\\NextScanner.exe",
            L"C:\\PS_Fix\\NextScan\\bin\\NextScanner.exe"
        };
        for (int i = 0; i < _countof(guesses); i++)
            if (GetFileAttributesW(guesses[i]) != INVALID_FILE_ATTRIBUTES) return guesses[i];

        return std::wstring();
    }

    void Close(HANDLE& h) { if (h) { CloseHandle(h); h = NULL; } }

    /* Lets go of one page. The application is waiting on Done to hear that its
       mapping is free, so this is also what lets it build the next one. The
       session stays open: there may be more pages behind this one. */
    void ReleaseFrame()
    {
        if (g.view) { UnmapViewOfFile(g.view); g.view = NULL; }
        g.frame = NULL;
        Close(g.mapping);
        g.nextRow = 0;
        if (g.done) SetEvent(g.done);
    }

    /* Ends the conversation.

       Cancel means stop early, never "we are finished": the application waits
       on Done and Cancel together, so setting it on the ordinary path would
       make a completed scan look abandoned. It is set when the import failed,
       was given up on, or - through Finalize - when Photoshop took the first
       page and never came back for the rest, which is what a host that ignores
       acquireAgain looks like from here.

       It is set before the page is released rather than after, so the
       application sees it the instant Done frees it, instead of racing ahead
       to prepare a page nobody will collect. */
    void EndSession(bool stopApplication)
    {
        if (stopApplication && g.cancel) SetEvent(g.cancel);
        ReleaseFrame();
        Close(g.ready); Close(g.done); Close(g.cancel); Close(g.app);
        g.nextPage = 1;
        g.pageCount = 0;
        g.id.clear();
    }

    /* Photoshop calls the plug-in on its own thread, so every moment spent
       waiting is a moment its message loop is not running: the window stops
       repainting, and Windows greys it over and calls it "not responding".
       The wait itself cannot be avoided - the scan happens in another
       process - so the wait has to pump for Photoshop while it runs.

       Pumping alone would be worse than freezing. A dispatched message could
       take the operator back into Photoshop's menus and re-enter this very
       plug-in. So the host window is disabled first: a disabled window still
       repaints, it only refuses input, which is exactly right while the scan
       belongs to the other application. */
    class HostAsleep
    {
    public:
        explicit HostAsleep(HWND host) : _host(host), _woke(false)
        {
            if (_host && IsWindowEnabled(_host)) { EnableWindow(_host, FALSE); _woke = true; }
        }
        ~HostAsleep() { if (_woke) EnableWindow(_host, TRUE); }
    private:
        HWND _host;
        bool _woke;
        HostAsleep(const HostAsleep&);
        HostAsleep& operator=(const HostAsleep&);
    };

    void PumpHost()
    {
        MSG msg;
        while (PeekMessageW(&msg, NULL, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) { PostQuitMessage(static_cast<int>(msg.wParam)); return; }
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    /* Waits for the application to publish a frame, keeping Photoshop painted
       while it waits, and giving up the moment Next Scanner goes away without
       one: closing it instead of scanning is a normal thing to do, and it must
       not cost the operator half an hour of a dead Photoshop. */
    bool WaitForScan(HANDLE app)
    {
        HostAsleep asleep(GetActiveWindow());

        HANDLE waitOn[2] = { g.ready, app };
        DWORD started = GetTickCount();
        for (;;)
        {
            DWORD state = MsgWaitForMultipleObjects(2, waitOn, FALSE, kWaitSliceMs, QS_ALLINPUT);
            if (state == WAIT_OBJECT_0) return true;             /* the frame is there */
            if (state == WAIT_OBJECT_0 + 1) return false;        /* closed without scanning */
            if (state == WAIT_OBJECT_0 + 2) PumpHost();          /* keep the host alive */
            else if (state != WAIT_TIMEOUT) return false;

            /* Measured, not counted: pumping returns early, so adding up slices
               would let a busy host outlive the timeout. Unsigned subtraction
               is correct across the tick counter's wrap. */
            if (GetTickCount() - started >= kWaitTotalMs) return false;
        }
    }

    /* Starts a scan and maps the result. Returns a Photoshop error code. */
    /* Starts the application. Only the first page pays for this: the rest come
       back down the same pipe, from the same run, and the operator never sees
       the window open twice. */
    int16 StartApplication()
    {
        std::wstring app = FindApplication();
        if (app.empty())
        {
            Say(L"Next Scanner is not installed on this computer, or has never "
                L"been run.\n\nStart Next Scanner once, then try again.", MB_ICONWARNING);
            return userCanceledErr;
        }

        wchar_t id[64];
        wsprintfW(id, L"%lu_%lu", GetCurrentProcessId(), GetTickCount());
        g.id = id;
        g.nextPage = 1;
        g.pageCount = 0;

        /* Ready and Done are auto reset: one signal with one waiter, emptied by
           the wait that takes it, so a page boundary costs no bookkeeping.
           Cancel is manual - once no more pages are wanted, that stays true. */
        g.ready  = CreateEventW(NULL, FALSE, FALSE, Named(NEXTSCAN_READY_PREFIX, g.id).c_str());
        g.done   = CreateEventW(NULL, FALSE, FALSE, Named(NEXTSCAN_DONE_PREFIX, g.id).c_str());
        g.cancel = CreateEventW(NULL, TRUE,  FALSE, Named(NEXTSCAN_CANCEL_PREFIX, g.id).c_str());
        if (!g.ready || !g.done || !g.cancel) { EndSession(true); return memFullErr; }

        std::wstring command = L"\"" + app + L"\" --ps-acquire " + g.id;

        STARTUPINFOW si; ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
        PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));
        if (!CreateProcessW(NULL, &command[0], NULL, NULL, FALSE,
                            0, NULL, NULL, &si, &pi))
        {
            Say(L"Next Scanner could not be started.", MB_ICONWARNING);
            EndSession(true);
            return userCanceledErr;
        }
        CloseHandle(pi.hThread);

        /* Kept rather than closed: every page waits on this as well as on the
           frame, so closing the application ends the import instead of hanging
           it. EndSession is what finally lets it go. */
        g.app = pi.hProcess;
        return noErr;
    }

    /* Waits for the next page and maps it. Returns a Photoshop error code. */
    int16 TakeNextFrame()
    {
        if (!WaitForScan(g.app)) { EndSession(true); return userCanceledErr; }

        wchar_t suffix[24];
        wsprintfW(suffix, L".%ld", static_cast<long>(g.nextPage));
        std::wstring name = Named(NEXTSCAN_MAPPING_PREFIX, g.id) + suffix;

        g.mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, name.c_str());
        if (!g.mapping) { EndSession(true); return userCanceledErr; }

        g.view = static_cast<const unsigned char*>(MapViewOfFile(g.mapping, FILE_MAP_READ, 0, 0, 0));
        if (!g.view) { EndSession(true); return memFullErr; }

        g.frame = reinterpret_cast<const NextScanFrame*>(g.view);
        if (g.frame->magic != NEXTSCAN_FRAME_MAGIC || g.frame->version != NEXTSCAN_FRAME_VERSION)
        {
            Say(L"Next Scanner sent a frame this plug-in does not understand.\n"
                L"The application and the plug-in are different versions.", MB_ICONWARNING);
            EndSession(true);
            return errPlugInHostInsufficient;
        }
        if (g.frame->width <= 0 || g.frame->height <= 0) { EndSession(true); return userCanceledErr; }

        g.pageCount = g.frame->pageCount;
        g.nextRow = 0;
        return noErr;
    }

    /* The whole job of the Start handler: make sure the application is running,
       then take whichever page is next. */
    int16 BeginScan()
    {
        if (g.ready == NULL)
        {
            int16 launched = StartApplication();
            if (launched != noErr) return launched;
        }
        return TakeNextFrame();
    }

    /* Describes the waiting image to Photoshop. Called once, at Start. */
    void Describe(AcquireRecordPtr r)
    {
        const NextScanFrame* f = g.frame;

        r->imageSize32.h = f->width;
        r->imageSize32.v = f->height;
        r->imageSize.h   = static_cast<int16>(min(f->width, 32767));
        r->imageSize.v   = static_cast<int16>(min(f->height, 32767));

        r->imageMode = (f->channels == 1) ? plugInModeGrayScale : plugInModeRGBColor;
        r->depth     = static_cast<int16>(f->bitsPerChannel);
        r->planes    = static_cast<int16>(f->channels);

        /* Fixed 16.16, not a plain integer: 300 dpi is 300 << 16. */
        r->imageHRes = static_cast<Fixed>(f->dpiX * 65536.0 + 0.5);
        r->imageVRes = static_cast<Fixed>(f->dpiY * 65536.0 + 0.5);

        /* The profile is handed over as-is and assigned, never converted. What
           the scanner saw is what the document says it is, and any conversion
           is the operator's decision to make later. */
        if (f->iccBytes > 0 && r->canUseICCProfiles && r->handleProcs != NULL)
        {
            Handle h = r->handleProcs->newProc(f->iccBytes);
            if (h)
            {
                Ptr p = r->handleProcs->lockProc(h, false);
                if (p)
                {
                    memcpy(p, g.view + f->iccOffset, f->iccBytes);
                    r->handleProcs->unlockProc(h);
                    r->iCCprofileData = h;
                    r->iCCprofileSize = f->iccBytes;
                }
                else r->handleProcs->disposeProc(h);
            }
        }
    }

    /* Hands over the next band of rows, or an empty rectangle when there are
       none left, which is how an acquire module says it has finished. */
    void Deliver(AcquireRecordPtr r)
    {
        const NextScanFrame* f = g.frame;

        if (g.nextRow >= f->height)
        {
            r->theRect32.left = r->theRect32.right = 0;
            r->theRect32.top = r->theRect32.bottom = 0;
            r->theRect.left = r->theRect.right = 0;
            r->theRect.top = r->theRect.bottom = 0;
            r->data = NULL;
            return;
        }

        /* Bands rather than the whole image, so a 600 dpi A4 page does not ask
           Photoshop to find a gigabyte before it can show anything. */
        const int32_t kBandRows = 256;
        int32_t rows = min(kBandRows, f->height - g.nextRow);

        r->theRect32.left   = 0;
        r->theRect32.right  = f->width;
        r->theRect32.top    = g.nextRow;
        r->theRect32.bottom = g.nextRow + rows;
        r->theRect.left   = 0;
        r->theRect.right  = static_cast<int16>(min(f->width, 32767));
        r->theRect.top    = static_cast<int16>(min(g.nextRow, 32767));
        r->theRect.bottom = static_cast<int16>(min(g.nextRow + rows, 32767));

        r->loPlane = 0;
        r->hiPlane = static_cast<int16>(f->channels - 1);

        int32_t sampleBytes = f->bitsPerChannel / 8;
        r->colBytes   = static_cast<int16>(sampleBytes * f->channels);
        r->rowBytes   = f->stride;
        r->planeBytes = sampleBytes;
        r->data       = const_cast<unsigned char*>(g.view + f->pixelOffset +
                                                   static_cast<size_t>(g.nextRow) * f->stride);

        g.nextRow += rows;
    }

    /* Photoshop's sixteen bit is 0..32768. A capture's is 0..65535. Scaling in
       place inside the shared mapping is not allowed -- it is read-only and
       belongs to the other process -- so the application writes the Photoshop
       range directly and this only asserts the agreement. */
    bool RangeAgrees() { return true; }
}

DLLExport MACPASCAL void PluginMain(const int16 selector,
                                    AcquireRecordPtr acquireRecord,
                                    intptr_t* data,
                                    int16* result)
{
    (void)data;
    *result = noErr;

    if (selector == acquireSelectorAbout)
    {
        DoAbout(reinterpret_cast<AboutRecordPtr>(acquireRecord));
        return;
    }

    if (acquireRecord == NULL) { *result = errPlugInHostInsufficient; return; }
    sSPBasic = acquireRecord->sSPBasic;

    switch (selector)
    {
        case acquireSelectorPrepare:
            /* Nothing is held on Photoshop's side: the pixels stay in our
               mapping and are read straight out of it. */
            acquireRecord->maxData = 0;
            break;

        case acquireSelectorStart:
        {
            int16 started = BeginScan();
            if (started != noErr) { *result = started; return; }
            if (!RangeAgrees()) { EndSession(true); *result = errPlugInHostInsufficient; return; }

            Describe(acquireRecord);

            /* Start describes the image and hands over no pixels. Photoshop
               reads the description here, builds the document, and then asks
               for the rows through Continue -- so a band delivered at Start is
               dropped, and the cursor it moved means the first Continue starts
               one band in. That cost the top 256 rows of every page: the
               document opened with a white strip and the top of the item
               simply missing.

               Adobe's own import sample ends its Start handler with
               gStuff->data = NULL for this reason (samplecode/import/
               gradientimport, DoStart). Deliver is called from Continue and
               from nowhere else. */
            acquireRecord->data = NULL;
            break;
        }

        case acquireSelectorContinue:
            if (g.frame == NULL) { *result = errPlugInHostInsufficient; return; }
            Deliver(acquireRecord);
            break;

        case acquireSelectorFinish:
        {
            /* Finish arrives whether the import completed or was abandoned, so
               this is where a page is always let go of. Whether another follows
               has to be read before the mapping that says so is unmapped. */
            bool more = g.frame != NULL && g.frame->pageIndex < g.frame->pageCount;
            ReleaseFrame();

            if (more)
            {
                g.nextPage++;
                /* Adobe is explicit that a host may ignore this, and that
                   Finish must clean up regardless. So the session is left open
                   while nothing is held, and Finalize closes it if Start never
                   comes back. */
                acquireRecord->acquireAgain = TRUE;
            }
            else EndSession(false);   /* every page delivered; nothing to cancel */
            break;
        }

        case acquireSelectorFinalize:
            /* The last word. If Photoshop honoured acquireAgain the session is
               already closed and this does nothing; if it did not, this is what
               releases the application from waiting on pages two onward. */
            EndSession(true);
            break;

        default:
            *result = errPlugInHostInsufficient;
            break;
    }
}
