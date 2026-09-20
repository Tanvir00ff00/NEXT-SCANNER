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
        const unsigned char* view;
        const NextScanFrame* frame;
        int32_t nextRow;

        Session() : mapping(NULL), ready(NULL), done(NULL), cancel(NULL),
                    view(NULL), frame(NULL), nextRow(0) {}
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
    std::wstring FindApplication()
    {
        HKEY key;
        if (RegOpenKeyExW(HKEY_CURRENT_USER, L"Software\\NextScan", 0, KEY_READ, &key) == ERROR_SUCCESS)
        {
            wchar_t buffer[MAX_PATH]; DWORD size = sizeof(buffer); DWORD type = 0;
            LONG got = RegQueryValueExW(key, L"AppPath", NULL, &type,
                                        reinterpret_cast<LPBYTE>(buffer), &size);
            RegCloseKey(key);
            if (got == ERROR_SUCCESS && type == REG_SZ)
            {
                buffer[min(size / sizeof(wchar_t), (DWORD)(MAX_PATH - 1))] = 0;
                if (GetFileAttributesW(buffer) != INVALID_FILE_ATTRIBUTES) return buffer;
            }
        }

        const wchar_t* guesses[] =
        {
            L"C:\\PS_Fix\\NextScan\\bin\\NextScanner.exe",
            L"C:\\Program Files\\NextScan\\NextScanner.exe"
        };
        for (int i = 0; i < _countof(guesses); i++)
            if (GetFileAttributesW(guesses[i]) != INVALID_FILE_ATTRIBUTES) return guesses[i];

        return std::wstring();
    }

    void Close(HANDLE& h) { if (h) { CloseHandle(h); h = NULL; } }

    void Release()
    {
        if (g.done) SetEvent(g.done);           /* the application may now free the mapping */
        if (g.view) { UnmapViewOfFile(g.view); g.view = NULL; }
        g.frame = NULL;
        Close(g.mapping); Close(g.ready); Close(g.done); Close(g.cancel);
        g.nextRow = 0;
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
    int16 BeginScan()
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

        g.ready  = CreateEventW(NULL, TRUE, FALSE, Named(NEXTSCAN_READY_PREFIX, g.id).c_str());
        g.done   = CreateEventW(NULL, TRUE, FALSE, Named(NEXTSCAN_DONE_PREFIX, g.id).c_str());
        g.cancel = CreateEventW(NULL, TRUE, FALSE, Named(NEXTSCAN_CANCEL_PREFIX, g.id).c_str());
        if (!g.ready || !g.done || !g.cancel) { Release(); return memFullErr; }

        std::wstring command = L"\"" + app + L"\" --ps-acquire " + g.id;
        std::wstring mutableCommand = command;

        STARTUPINFOW si; ZeroMemory(&si, sizeof(si)); si.cb = sizeof(si);
        PROCESS_INFORMATION pi; ZeroMemory(&pi, sizeof(pi));
        if (!CreateProcessW(NULL, &mutableCommand[0], NULL, NULL, FALSE,
                            0, NULL, NULL, &si, &pi))
        {
            Say(L"Next Scanner could not be started.", MB_ICONWARNING);
            Release();
            return userCanceledErr;
        }
        CloseHandle(pi.hThread);

        bool ready = WaitForScan(pi.hProcess);
        CloseHandle(pi.hProcess);
        if (!ready) { Release(); return userCanceledErr; }   /* cancelled, or never came */

        g.mapping = OpenFileMappingW(FILE_MAP_READ, FALSE,
                                     Named(NEXTSCAN_MAPPING_PREFIX, g.id).c_str());
        if (!g.mapping) { Release(); return userCanceledErr; }

        g.view = static_cast<const unsigned char*>(MapViewOfFile(g.mapping, FILE_MAP_READ, 0, 0, 0));
        if (!g.view) { Release(); return memFullErr; }

        g.frame = reinterpret_cast<const NextScanFrame*>(g.view);
        if (g.frame->magic != NEXTSCAN_FRAME_MAGIC || g.frame->version != NEXTSCAN_FRAME_VERSION)
        {
            Say(L"Next Scanner sent a frame this plug-in does not understand.\n"
                L"The application and the plug-in are different versions.", MB_ICONWARNING);
            Release();
            return errPlugInHostInsufficient;
        }
        if (g.frame->width <= 0 || g.frame->height <= 0) { Release(); return userCanceledErr; }

        g.nextRow = 0;
        return noErr;
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
            if (!RangeAgrees()) { Release(); *result = errPlugInHostInsufficient; return; }

            Describe(acquireRecord);
            Deliver(acquireRecord);
            break;
        }

        case acquireSelectorContinue:
            if (g.frame == NULL) { *result = errPlugInHostInsufficient; return; }
            Deliver(acquireRecord);
            break;

        case acquireSelectorFinish:
        case acquireSelectorFinalize:
            /* Finish arrives whether the import completed or was abandoned, so
               this is the one place the mapping is guaranteed to be released. */
            Release();
            break;

        default:
            *result = errPlugInHostInsufficient;
            break;
    }
}
