# NextScan Studio — Engineering Handoff

**For:** the engineer or AI agent taking over this project
**From:** the previous agent
**Date:** 2026-08-20
**Working directory:** `C:\PS_Fix`

Read this file completely before touching anything. It takes ten minutes and will
save you several days. The parts that look like pedantic detail are the parts that
cost the most to rediscover.

---

## 1. What this project is, and why it exists

We are building **NextScan Studio** — a commercial Windows scanning suite plus a
Photoshop acquisition bridge. The ambition is to beat SilverFast, VueScan and the
OEM utilities (Epson ScanSmart, ScanSnap Home) at once.

The product thesis, in one paragraph:

> One application, one per-user licence, that (a) talks to *any* scanner over four
> independent transports, (b) never crashes because a vendor driver misbehaved,
> (c) produces archival-grade colour and archival-grade PDFs, and (d) lands the
> result inside Photoshop in under a second.

### Where we started

The user already had a working tool: `C:\PS_Fix\scanhelper.cs` — a 5,200-line
C# WinForms app that drives a scanner and hands images to Photoshop. It contains a
lot of genuinely good, hard-won code: flatbed background estimation, convex-hull
minimum-area document detection, projection-profile edge refinement, a curves
engine, and a polished studio UI.

Its fatal flaw: **it shelled out to `NAPS2.Console.exe`** for every scan. That is
an external dependency the product cannot ship with, it is slow, and it gives us
no control over the device.

### What the previous agent did

1. Researched the market and the platform, then wrote a full architecture and
   product plan: **`C:\PS_Fix\NEXTSCAN_STUDIO_MASTER_PLAN.md`** (~118 KB). That
   document is the plan of record.
2. Built the **native acquisition engine** that replaces NAPS2 — direct TWAIN 2.x
   and WIA 2.0, in isolated host processes — and **verified it on real hardware**.
3. Wired that engine into the user's existing UI so the whole app now works with
   **zero external dependencies**, while keeping every bit of their old code.

---

## 2. Read these, in this order

| # | File | Why |
|---|---|---|
| 1 | `NextScan\docs\STATUS.md` | Exactly what works, what does not, and the bug list. **Most important file.** |
| 2 | `NEXTSCAN_STUDIO_MASTER_PLAN.md` section 3 | The corrections to the original spec. Non-negotiable. |
| 3 | `NEXTSCAN_STUDIO_MASTER_PLAN.md` sections 5-7 | Architecture and the acquisition engines |
| 4 | `NextScan\docs\adr\0001-host-protocol.md` | Why the host protocol is what it is |
| 5 | `NextScan\src\Core\Contracts.cs` | The vocabulary every transport translates into |
| 6 | `NextScan\src\Twain\TwainSession.cs` | The most intricate code in the project |
| 7 | `NEXTSCAN_STUDIO_MASTER_PLAN.md` section 24 | Milestones M0-M12 and their acceptance criteria |

Then run this and watch it work:

```powershell
cd C:\PS_Fix\NextScan
.\build.ps1
.\bin\nsprobe.exe list
.\bin\nsprobe.exe caps "LiDE"
```

---

## 3. The single most important fact about this machine

**There is no 64-bit TWAIN on the user's PC.**

```
C:\Windows\twaindsm.dll   absent
C:\Windows\twain_64\      absent
C:\Windows\twain_32\      SG20, ScanGearIR, wiatwain.ds   <- the real Canon driver
```

`NextScan.Host64.exe probe` finds **zero** TWAIN devices.
`NextScan.Host32.exe probe` finds the CanoScan LiDE 400.

There is no 32/64-bit TWAIN bridge in existence. A 64-bit process physically
cannot load a 32-bit data source. This is why the architecture puts drivers in
**separate host processes, one per bitness**, and it is not negotiable — remove it
and the user's own scanner stops working.

The same split gives us crash isolation for free: a vendor driver that faults kills
a disposable host process, not the UI. That is our biggest durable advantage over
VueScan and SilverFast, both of which load drivers in-process.

---

## 4. Architecture as built

```
scanhelper.exe (existing WinForms UI, AnyCPU)
  └── NextScanBridge.cs ────────────► NextScan.Engine.dll
                                        └── DeviceBroker
                                             │  spawns, supervises, kills
                    ┌────────────────────────┴────────────────────────┐
                    ▼                                                 ▼
        NextScan.Host32.exe (x86)                      NextScan.Host64.exe (x64)
          TWAIN via twain_32.dll                         TWAIN via TWAINDSM.DLL
          WIA 2.0 (32-bit COM)                           WIA 2.0 (64-bit COM)

  control channel : newline-delimited JSON on the host's stdout
  pixel channel   : named shared memory, 128-byte header + packed pixels
```

**Rule that must never be broken:** the UI process never calls `LoadLibrary` on a
vendor DLL, never creates a vendor COM object, and never pumps a vendor message
loop. Everything hardware-facing happens in a host process.

### Source map

```
C:\PS_Fix\
├── NEXTSCAN_STUDIO_MASTER_PLAN.md   the plan of record
├── HANDOFF.md                       this file
├── scanhelper.cs                    the user's existing 5,200-line app (edited, surgically)
├── NextScanBridge.cs                adapter: old app -> new engine
├── build_scan_native.ps1            builds scanhelper.exe against the engine
├── build_scan.ps1                   ORIGINAL NAPS2-only build, untouched, still works
├── backup_original\scanhelper.cs.pre-native   pre-integration source
└── NextScan\
    ├── build.ps1                    builds engine, both hosts, nsprobe
    ├── bin\                         build output
    ├── docs\STATUS.md               what works / what does not
    ├── docs\adr\0001-host-protocol.md
    └── src\
        ├── Core\      Contracts, Json, RawImage, DibDecoder, DeviceBroker
        ├── Twain\     TwainTypes, TwainSession, TwainDriver
        ├── Wia\       WiaInterop, WiaDriver
        ├── Host\      HostProgram          (compiled twice: x86 and x64)
        └── Tools\     NsProbe (CLI), WiaDiag (diagnostic harness, not in build)
```

---

## 5. Current stage

Roughly **plan milestone M0 + most of M1 + the WIA half of M4**, plus integration.

**Working and verified on hardware:**
- Native TWAIN 2.x: DSM binding, states 1-7, message pump, capability negotiation,
  memory transfer, native DIB transfer
- Native WIA 2.0 via raw vtable interop — deliberately **not** `wiaaut.dll`
- Both host processes, shared-memory frames, broker, dedup, transport ranking
- `nsprobe` CLI
- The user's app now scans with **NAPS2 never invoked**

**Measured:**
```
nsprobe scan --dpi 150 --region 0,0,3,2  ->  450x300 @150dpi  = exactly 3.00 x 2.00 in
scanhelper -nodialog (crop_h=0.2567)     -> 2481x900 @300dpi  = exactly 8.27 x 3.00 in
```

**Not started:** everything else — eSCL/mDNS/WSD, the imaging pipeline port, AI,
PDF/OCR/MRC, film module, ICC/IT8, Photoshop connectors, batch/jobs, licensing,
installer, quirks DB, **and all tests**.

---

## 6. Traps. Read this section twice.

Every one of these cost real debugging time. They are in the code as comments —
do not "clean up" those comments.

1. **`PRSPEC_PROPID = 1`, not 0.** `0` is `PRSPEC_LPWSTR`. Set `ulKind = 0` and WIA
   dereferences your property id as a string pointer: instant access violation
   inside the WIA service. The symptom looks exactly like a marshalling bug, so you
   will "fix" the marshalling five times before finding it.
2. **`TYMED_FILE = 2`, not 1.** `1` is `TYMED_HGLOBAL`.
3. **`TYMED_CALLBACK` is the WIA 1.0 band mechanism.** `IWiaTransfer::Download`
   wants `TYMED_FILE`. Ask for callback and `Download` fails `E_INVALIDARG` even
   though every property write returned `S_OK`.
4. **Write `WIA_IPA_TYMED` before `WIA_IPA_FORMAT`** — the legal format set is
   scoped to the medium.
5. **Set `ICAP_UNITS` before reading any dimension.** Otherwise the LiDE 400
   reports its bed as `0.16 x 0` inches.
6. **TWAIN structs need `Pack = 2`** (`#pragma pack(2)` in twain.h). Wrong packing
   does not throw — it silently returns shifted garbage.
7. **`ICAP_BITDEPTH` means bits-per-*channel* on some drivers.** Canon rejects 24
   for RGB and wants 8.
8. **A capability being advertised does not mean the hardware exists.** The
   LiDE 400 advertises `CAP_FEEDERENABLED` and has no feeder. Require that the
   device *accepts* the mode before believing it.
9. **`wiaaut.dll` cannot return the back side of a duplex scan.** Microsoft
   documents this as by-design (KB2709992). Never use the automation layer.
10. **Drain stdout and stderr asynchronously.** Reading one to completion before
    the other deadlocks once a chatty driver fills the 4 KB pipe buffer. This
    codebase already hit that bug once in the NAPS2 path.
11. **`MSG_SET` returning success does not mean your value was applied.** Always
    read back.

### Environment constraints

- **No .NET SDK on this machine.** `dotnet build` does not work. Use Roslyn
  `csc.exe` from
  `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe`,
  targeting .NET Framework 4.8 by referencing
  `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\*.dll` with `-nostdlib+`.
  There are **no** reference assemblies under `Reference Assemblies\`.
  This is why WPF-on-.NET-9 and WinUI 3 are off the table today.
- In Git Bash, csc switches must use `-` not `/`; `/nologo` gets path-converted
  into `C:/Program Files/Git/nologo`.
- `Add-Type` does not pass `/unsafe`. Call `csc.exe` directly.
- **`C:\PS_Fix` is not a git repository.** There is no undo. Your first action
  should be `git init` plus a `.gitignore` for `bin/`, `out/`, `scans/`, `tmp/`,
  `*.exe`, `*.dll`, then an initial commit. Do this before changing anything.

---

## 7. How to write code on this project

The user explicitly asked that you code to the standard already set. Concretely:

### 7.1 Comments carry evidence, not narration

Do not write comments that restate the code. Write comments that record **why**,
and preserve measurements and failure symptoms so the next person cannot
accidentally undo the fix.

Bad:
```csharp
// set the units to inches
s.CapSet(ICAP.UNITS, TWTY.UINT16, TWUN.INCHES);
```

Good — this is the house style:
```csharp
// Pin the unit system before reading anything dimensional. Without this the bed
// size comes back in whatever unit the driver last used - on the LiDE 400 that
// read as 0.16 "inches" for an 8.5 inch bed.
s.CapSet(ICAP.UNITS, TWTY.UINT16, TWUN.INCHES);
```

Every non-obvious constant, ordering requirement or workaround gets a comment
naming the device and the symptom.

### 7.2 Errors are data, never exceptions across a boundary

Use `NsResult` (`src/Core/Contracts.cs`). Every failure carries a stable
`NsError` code, the device's own condition code verbatim, a human message, **and a
remedy string telling the user what to actually do**. "Paper jam." is half a
message; "Paper jam. Clear the jam and try again." is a whole one.

Never let an exception escape a host process — the parent distinguishes "reported
a failure" from "died" by exit code.

### 7.3 Never trust a driver

Assume every driver lies, in both directions. Read back what you set. Validate
sizes before you index. Clamp instead of failing where a sane clamp exists. Guard
loops that depend on driver-supplied counts. Treat a scanner as hostile input.

### 7.4 Authoritative sources over memory

Several interop constants in this project were wrong when taken from memory, and
each cost an hour. The Windows SDK is installed at
`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\` — read
`um\wia_lh.h`, `um\wiadef.h`, `um\propidl.h`, `um\objidl.h` and confirm every GUID,
vtable ordering and enum value **before** writing the interop. Use `grep`/`awk` on
the headers; it takes seconds.

For COM interfaces, method **declaration order defines the vtable slot**. Declare
every method in order, even ones you never call, and never reorder them.

### 7.5 Verify against hardware before claiming anything works

"It compiles" is not "it works". Nothing gets marked verified in `STATUS.md`
without a real device producing a real image with the expected pixel dimensions.
If you cannot test something, say so explicitly and mark it untested — the existing
`STATUS.md` does this and you must maintain that honesty. Overstating status is the
single most damaging thing you can do to this handover.

### 7.6 Small, compiling increments

Compile after every file. The build takes about three seconds:

```powershell
cd C:\PS_Fix\NextScan; .\build.ps1 -NoWait
```

Do not write 2,000 lines then debug. When something misbehaves in interop, write a
throwaway diagnostic (see `src/Tools/WiaDiag.cs` for the pattern: print struct
sizes, QI results, vtable slots, then call the method manually to isolate whether
the fault is the CLR or the arguments). That harness is what found trap #1.

### 7.7 Style specifics

- .NET Framework 4.8, C# 7.3, `-unsafe` available.
- Four-space indent, Allman braces, `PascalCase` public, `_camelCase` private
  fields, `camelCase` locals.
- No `var` for non-obvious types; this codebase favours explicit types.
- No LINQ in hot pixel loops.
- No `System.Drawing.Bitmap` in the acquisition or processing path — it mangles
  48-bit data and drops ICC profiles. `RawImage` is the currency. `ToBitmap()`
  exists only for display and legacy interop.
- No third-party NuGet packages without checking master plan section 3.2 first:
  permissive licences only (MIT/BSD/Apache/zlib), built from source in CI, no
  user-visible prerequisite ever.
- Keep the existing bilingual reality in mind: user-facing strings should be
  localisable; do not hardcode English into new UI without a resource path.

### 7.8 Respect the user's existing code

`scanhelper.cs` is their working product. It has been edited **surgically** —
native engine first, NAPS2 retained as a fallback behind `engine=` in `scan.ini`,
and `build_scan.ps1` left untouched. Continue that discipline: additive, guarded,
reversible. Do not rewrite their UI wholesale because you would have structured it
differently.

---

## 8. What to do next, in priority order

### 8.1 First: `git init`

No version control exists. Do this before anything else.

### 8.2 Build the TWAIN simulator (plan section 18.3) — highest value

Everything so far was verified by hand against one scanner. There is **no
regression net**, and the eleven traps in section 6 are exactly the class of bug
that silently returns.

Write `NextScan.TwainSimulator` as a real installable TWAIN data source with
switchable personalities: well-behaved, slow, refuses `ShowUI=FALSE`, returns a
resolution it was not asked for, hangs on `MSG_ENABLEDS`, crashes in state 7,
32-bit only, duplex with reversed backs. It unblocks CI, lets you test failure
paths you cannot produce on real hardware, and doubles as the basis for the
NextScan TWAIN data source the plan wants us to publish later (section 14.5,
differentiator D7).

Pair it with a golden-image harness before writing new imaging code.

### 8.3 Verify colour channel order

Only blank white pages have been scanned, so an R/B swap would be invisible. Scan
something with saturated colour and confirm. Ten minutes, removes a real risk.

### 8.4 eSCL transport (plan section 7.4)

Best coverage-per-effort of anything remaining: needs no vendor driver at all, and
is how modern network MFPs are meant to be driven. Discovery via the Win32 DNS-SD
API with a raw mDNS fallback, then the HTTP/XML job flow. Mind the `503` retry
policy and the fact that the `rs=` TXT key is not always `eSCL`.

### 8.5 Port the imaging pipeline onto `RawImage`

Their detection and curves code currently round-trips through
`System.Drawing.Bitmap`, capping everything at 8 bits per channel. Port the good
algorithms out of `scanhelper.cs` (`PixBuf`, `EstimateBackground`,
`ComputeConvexHull`, `FindMinimumAreaBoundingBox`, `RefineBoxByProjection`,
`BuildCurveLut`) onto the 16-bit `RawImage` model. Keep their algorithms — they are
well-tuned and the comments explain real measured failures. Note that master plan
section 3.5 replaces the `[0.6 deg, 6.0 deg]` deskew clamp with a confidence-scored
policy; implement that, but keep the old behaviour available as a "Strict Flatbed
Guard" preset.

### 8.6 Then follow master plan section 24 milestones M5 onward

Output/PDF/OCR, AI subsystem, Photoshop connectors, film module, installer,
licensing. Each milestone has explicit acceptance criteria — honour them.

---

## 9. Rules that must not be broken

1. Vendor drivers load **only** in host processes, never in the UI.
2. Both bitness hosts always ship; never assume 64-bit is enough.
3. Never use `wiaaut.dll`.
4. No external runtime prerequisites, ever (master plan section 3.2).
5. Never claim something works without hardware evidence; keep `STATUS.md` honest.
6. Keep `build_scan.ps1` and the `engine=naps2` fallback working until the native
   path has been proven over weeks of real use.
7. Read the SDK headers before writing interop.
8. Comments that record a measured failure are load-bearing. Do not delete them.
9. When you change behaviour the plan specifies, write an ADR in
   `NextScan\docs\adr\` explaining why (see ADR-0001 for the format).

---

## 10. Sanity check before you start

```powershell
cd C:\PS_Fix\NextScan
.\build.ps1 -NoWait                          # expect: 4 targets, all "ok"
.\bin\nsprobe.exe list                       # expect: 2 scanners, TWAIN 32-bit starred
.\bin\NextScan.Host64.exe probe --verbose    # expect: 0 TWAIN devices, 2 WIA
.\bin\NextScan.Host32.exe probe --verbose    # expect: 2 TWAIN + 2 WIA
```

If the Host32/Host64 asymmetry above does not reproduce, something is wrong with
your environment, not with the code — investigate that before writing anything.

Good luck. The foundation is solid and proven on hardware; the discipline that got
it there is what matters most from here.
