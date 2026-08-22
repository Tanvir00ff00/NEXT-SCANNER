# NEXT SCANNER STUDIO (NextScan) — Master Engineering Plan v1.0

**Document type:** Product + Architecture + Implementation specification
**Target audience:** Senior Windows/C++/C# engineers, or an autonomous coding agent
**Status:** Approved-for-build blueprint (no code in this document — this is the plan)
**Date:** 2026-08-20
**Owner:** Product owner (tanvirhossain53160@gmail.com)

---

## 0. HOW TO USE THIS DOCUMENT

This document is written so that it can be handed to **either** a human engineering team **or** an AI coding agent, and building can begin immediately without further clarification.

Reading order:

| If you are… | Read |
|---|---|
| Product owner / stakeholder | §1, §2, §3, §4, §24, §25 |
| Lead architect | §5 → §12 |
| Device / driver engineer | §6, §7, Appendix A, B, C |
| Imaging / graphics engineer | §8, §9, §10, §11 |
| Photoshop integration engineer | §14, Appendix E |
| UI engineer | §13 |
| Build / release engineer | §19, §20, §21 |
| QA lead | §18, §22 |
| AI coding agent | Start at §5, then execute §24 milestone-by-milestone. Never skip §3. |

**Rules for the implementing agent:**

1. §3 (Reality Check) contains corrections to the original brief. Those corrections **override** the original brief where they conflict. Do not silently re-introduce the corrected assumptions.
2. Every milestone in §24 has explicit **Definition of Done**. Do not advance until DoD is met.
3. Anything marked **[MUST]** is release-blocking. **[SHOULD]** is v1.0 target. **[LATER]** is post-1.0 backlog.
4. All hardware-touching code must be written behind the abstractions in §6.1 so it is testable **without hardware** (see §18.3 simulators).

### 0.1 বাংলা সারসংক্ষেপ (Bangla Executive Summary)

এই ডকুমেন্টটি **Next Scanner Studio** — একটি সম্পূর্ণ Windows scanner suite + Photoshop plug-in — তৈরির পূর্ণাঙ্গ ইঞ্জিনিয়ারিং প্ল্যান।

মূল সিদ্ধান্তগুলো সংক্ষেপে:

- **স্ট্যাক:** .NET 9 (C#) + WPF shell, ইমেজ প্রসেসিং SIMD/C++ ও GPU compute-এ, scanner driver code আলাদা process-এ।
- **সবচেয়ে গুরুত্বপূর্ণ আর্কিটেকচার সিদ্ধান্ত:** scanner driver (TWAIN/WIA) কখনোই main app process-এ load হবে না। আলাদা `Host64`/`Host32` process-এ চলবে। কারণ — (ক) 32-bit ও 64-bit TWAIN driver একসাথে চালানোর অন্য কোনো উপায় নেই, (খ) খারাপ vendor driver crash করলেও আমাদের app বেঁচে থাকবে। **এটাই VueScan/SilverFast-এর তুলনায় আমাদের সবচেয়ে বড় স্থায়ী সুবিধা।**
- **"Zero dependency" মানে:** ইউজারকে আলাদা কিছু ইনস্টল করতে হবে না (§3.2)। তার মানে এই নয় যে সব কিছু শূন্য থেকে লিখতে হবে — permissive-license (MIT/BSD/Apache) native library গুলো আমরা নিজেদের installer-এর ভিতরে static/bundled করে, নিজেদের certificate দিয়ে sign করে দেব।
- **Photoshop:** পুরনো `.8ba` plug-in একা যথেষ্ট নয়। CS6–CC2019-এর জন্য `.jsx`, 2021+ (v22+)-এর জন্য **UXP Hybrid plugin**, আর `File > Import` মেনুর জন্য `.8ba` — তিনটাই লাগবে (§14)।
- **আমাদের "next level" পার্থক্য:** non-destructive RAW workflow, per-user license (SilverFast-এর মতো per-scanner নয়), GPU real-time preview, AI (dewarp/shadow removal/denoise/auto-naming), auto-updating **Device Quirks Database**, এবং নিজেরাই একটি **TWAIN Data Source** publish করা — যাতে Word/Photoshop/যেকোনো app আমাদের ইঞ্জিনের মধ্য দিয়ে scan করতে পারে।
- **সময়:** 13 milestone, আনুমানিক 12–15 মাস (3–5 জন ইঞ্জিনিয়ার) বা একটি AI agent + 1 জন reviewer দিয়ে উল্লেখযোগ্যভাবে কম (§24.15)।

মূল ঝুঁকি ও তার সমাধান §25-এ আছে। প্রতিটি মাইলস্টোনের acceptance criteria §24-এ দেওয়া।

---

## 1. EXECUTIVE SUMMARY

**Next Scanner Studio (NextScan)** is a commercial Windows desktop scanning suite and Photoshop acquisition bridge. It targets three markets simultaneously — a positioning no incumbent currently holds:

| Market | Incumbent | Their weakness we attack |
|---|---|---|
| Film/photo archival | SilverFast 9 | Per-scanner licensing, dated UI, slow, no real non-destructive catalog |
| Universal device support | VueScan | Scan-only, weak color, minimal editing, no Photoshop-native path |
| Document capture | Epson ScanSmart / ScanSnap Home / ABBYY | OEM lock-in, no film support, no pro colour management |

**Product thesis:** one application, one per-user licence, that (a) talks to *any* scanner via four independent transports, (b) never crashes because a vendor driver misbehaved, (c) produces archival-grade colour and archival-grade PDFs, and (d) lands the result inside Photoshop in under 50 ms.

**Primary binary:** `NextScanner.exe` (self-contained, no runtime prerequisite)
**Installers:** `NextScanner_Setup.exe` (Inno Setup, primary) and `NextScanner.msi` (WiX, enterprise/GPO)
**OS:** Windows 10 21H2+ and Windows 11 (x64 primary; ARM64 [SHOULD]; x86 only as the driver-host surrogate)
**Photoshop:** CS6 → Photoshop 2026 (see §14.2 version matrix)

---

## 2. COMPETITIVE RESEARCH FINDINGS

Research conducted 2026-08-20. Sources listed in §26.

### 2.1 SilverFast 9 (LaserSoft Imaging) — the quality benchmark

Feature set worth matching or beating:

| Feature | What it does | Our response |
|---|---|---|
| **iSRD** | Hardware infrared-channel dust/scratch detection & removal at scan time | §10.4 — IR-channel pipeline + software SRDx-equivalent fallback |
| **Multi-Exposure** | Two scans at different exposures merged → ~2× more grey levels; **patented (EP 1744278 / US 8,693,808)**; does not work on reflective originals | §10.5 — **patent-avoidance required.** Ship *Multi-Sample* (identical exposure, noise-averaging — unpatented, prior art) + *Bracketed HDR Merge* driven by user-visible exposure bracketing, which is a distinct mechanism. Legal review gate before shipping. |
| **NegaFix** | 120+ negative film emulsion profiles | §10.3 — open, community-editable JSON film profile format; ship 60+ at 1.0, crowd-source the rest |
| **AACO** | Adaptive contrast optimisation in dark areas without blowing highlights | §9.6 — local tone mapping operator |
| **IT8 calibration** | Automatic ICC profile generation from IT8.7/ISO 12641-2 target | §11.4 — full IT8 auto-detect + profile build |
| **WorkflowPilot** | Guided, correct-order tool sequencing | §13.7 — "Guided Mode" |
| **JobManager** | Batch job queue | §12 |
| Licensing | **Tied to one scanner model, one machine** — the single most complained-about thing about SilverFast | **Per-user, up to 3 machines, unlimited scanners.** This is a headline marketing bullet. |

### 2.2 VueScan (Hamrick) — the compatibility benchmark

- Its real moat is **~7000 supported devices** built up over 25 years — a per-model behaviour database, not clever code.
- Weaknesses: colour accuracy behind SilverFast; essentially scan-only; spartan UI that hides rather than removes complexity; no film-profile depth.
- **Strategic implication:** we cannot out-support VueScan on day one. We beat it by (a) *never failing hard* — four transports with automatic fallback, (b) a **remotely-updatable Device Quirks Database** (§7.6) so a new device fix ships in hours, not in a new build, and (c) an in-app "device report" that turns every user into a compatibility contributor.

### 2.3 Document-capture tier (ScanSmart, ScanSnap Home, ABBYY, CamScanner)

Table-stakes features we must have or we look amateur:

- Auto-crop / auto-orient / auto-deskew, blank-page removal, background removal, dirt detection, punch-hole removal
- Multi-page batch, barcode/patch-code document separation, separator sheets
- Auto-categorisation of documents (receipt vs invoice vs contract) and **auto-file-naming from OCR content** — Epson's ScanSmart AI is the current reference
- Searchable PDF / PDF/A, hot folders, table extraction to CSV/XLSX

**Gap we exploit:** none of them do film, none do proper colour management, none integrate natively with Photoshop, and none has a modern non-destructive edit history.

### 2.4 Our differentiation matrix (the "next level" list)

| # | Differentiator | Nobody else has it |
|---|---|---|
| D1 | **Out-of-process driver hosting with crash isolation + automatic transport fallback** | ✅ Correct — this is the single biggest reliability win |
| D2 | **Non-destructive `.nsraw` archive + replayable edit stack** (re-render any scan from the original sensor data years later) | ✅ |
| D3 | **Per-user licence, unlimited scanners, 3 machines, 90-day offline** | ✅ (directly attacks SilverFast) |
| D4 | **GPU (D3D12 compute) real-time full-resolution preview with tiled processing** | ✅ |
| D5 | **AI stack on ONNX Runtime + DirectML**: dewarp, shadow removal, denoise, super-resolution, auto-classification, OCR-driven auto-naming | Partially (CamScanner mobile only) |
| D6 | **Live Device Quirks Database**, updated server-side without shipping a build | ✅ |
| D7 | **NextScan publishes its own TWAIN Data Source** — any TWAIN app (Word, Acrobat, Photoshop's TWAIN plug-in, legacy LOB apps) can scan *through our pipeline* | ✅ Unique |
| D8 | **Photoshop Hybrid UXP plug-in** (not just legacy ExtendScript) with Action recording | ✅ |
| D9 | **MRC-compressed PDF** (JBIG2 mask + JPEG2000 layers) → 8–10× smaller colour scans | Only enterprise SDKs (LEADTOOLS, GdPicture) |
| D10 | **Local automation API** (CLI + named-pipe JSON-RPC + JS scripting) so NextScan can be embedded into anyone's workflow | ✅ |
| D11 | **Full localisation incl. Bangla, and true accessibility (screen reader, keyboard-only)** | ✅ |

---

## 3. REALITY CHECK — CORRECTIONS TO THE ORIGINAL BRIEF

**These findings override the original specification. Read before writing any code.**

### 3.1 [CRITICAL] There is no 32↔64-bit TWAIN bridge. A surrogate process is mandatory.

The 64-bit `TWAINDSM.DLL` can only load 64-bit TWAIN data sources; the 32-bit DSM only 32-bit ones. No DSM bridges bitness. A large fraction of installed scanner bases — particularly older Canon, Epson, Fujitsu and HP flatbeds — ship **32-bit-only** `.ds` data sources.

**Consequence:** the original brief's "dynamically load 64-bit `twaindsm.dll` with automatic fallback to 32-bit `twain_32.dll`" is *impossible inside one process*. The correct design is:

- `NextScanner.exe` (x64) — UI, imaging, never loads a vendor driver
- `NextScan.Host64.exe` (x64) — hosts 64-bit TWAIN DSM + 64-bit WIA
- `NextScan.Host32.exe` (x86) — hosts 32-bit TWAIN DSM / `TWAIN_32.DLL` + 32-bit WIA
- Both hosts enumerate; results are merged and de-duplicated in the UI (§7.4)

This is not a workaround; it is the only correct architecture, and it doubles as crash isolation (D1). **It is the most important structural decision in this document.**

### 3.2 [CRITICAL] "Zero external dependencies" must be redefined

The correct, achievable, commercially normal definition:

> **Zero *user-visible* prerequisites.** The user runs one signed installer. No .NET runtime install, no VC++ redistributable prompt, no NAPS2, no Ghostscript, no Python, no external `.exe` invoked at runtime, no network required for core function.

It does **not** mean "write a JPEG2000 codec from scratch." Writing our own JBIG2 encoder, ICC colour engine and JPEG codec would add ~18 months and be *less* correct than battle-tested libraries. The rule instead is:

**Bundling rules [MUST]:**
1. Only **permissive licences**: MIT, BSD-2/3, Apache-2.0, zlib. **No GPL/LGPL-with-dynamic-linking-obligations in the shipped product** unless legal signs off in writing (note: the reference TWAIN DSM itself is LGPL — see §7.2, we do **not** ship it).
2. Statically linked into our own DLLs where the licence permits; otherwise shipped as our own signed DLLs in the app folder — never installed to System32, never registered globally.
3. Every third-party component is pinned by version + hash, listed in the SBOM (§21.5), and rebuilt by us from source in CI (§20.4). We never ship a binary we did not build.
4. No component may be invoked as a child process for image work.

**Approved dependency list (v1.0):**

| Component | Purpose | Licence | Link mode |
|---|---|---|---|
| ONNX Runtime (+ DirectML EP) | AI inference | MIT | Bundled DLL, our build |
| Little-CMS 2 (lcms2 ≥ 2.19) | ICC v2/v4 colour transforms, 16-bit & float | MIT | Static into `NextScan.Imaging.Native.dll` |
| libjpeg-turbo | JPEG encode/decode (SIMD) | BSD-3/IJG | Static |
| OpenJPEG | JPEG 2000 for MRC/JPX layers | BSD-2 | Static |
| jbig2enc + leptonica | JBIG2 mask encoding for MRC | Apache-2.0 / BSD-2 | Static |
| libtiff | TIFF read (write is ours) | libtiff/BSD-like | Static |
| zlib-ng | Deflate | zlib | Static |
| Tesseract 5 (LSTM) + traineddata | Tier-2 OCR breadth | Apache-2.0 | Static + data files |
| libwebp / libheif *(optional)* | Modern output formats | BSD / LGPL⚠️ | libheif LGPL — **defer to [LATER]**, legal review |
| SQLite | Local catalogue/profiles DB | Public domain | Static |

**Written by us (no library):** TWAIN layer, WIA layer, eSCL/WSD stack, mDNS, PDF writer, TIFF writer, PNG writer, all image processing, all UI, IPC, installer logic, licensing.

### 3.3 [CRITICAL] The Photoshop story is more complex than "`.8ba` + `.jsx`"

Findings:

- The C++ plug-in SDK (`.8ba` acquire modules) **still exists and still works**, and Adobe now formally bridges it to UXP via **Hybrid Plugins** and `PIUXPSuite` (PIActionDescriptor-based messaging). Photoshop 2025 (v26) shipped UXP v8.
- **ExtendScript is not deprecated but is legacy.** UXP is the recommended path for Photoshop 22 (2021) and later. Photoshop 2025 added *recording a UXP plugin function call as an Action step* — which is exactly what the original brief wanted from ActionDescriptors.
- Adobe stopped shipping the TWAIN plug-in with Photoshop as far back as CS5 (download-only, 32-bit Windows), and Adobe's own guidance was to prefer WIA over TWAIN on Windows. **Do not build on Photoshop's TWAIN plug-in.** We *replace* it.
- Photoshop CC 2019 and later are **64-bit only**. A 32-bit `.8ba` is needed only for CS6/CC2014-era installs.

**Consequence:** ship **three** connectors (§14.1), auto-selected per detected Photoshop version. Also requires an **Adobe Developer account + Photoshop SDK licence agreement** — this is a procurement task with lead time (§25 R7).

### 3.4 [HIGH] `wiaaut.dll` (WIA Automation Layer 2.0) cannot do duplex — by design

The automation layer is built on WIA 1.0 semantics; duplex scans return only the front side. Microsoft's own KB confirms this and recommends coding against native WIA 2.0.

**Consequence:** the brief's mention of `wiaaut.dll` must be dropped. Implement **native `IWiaDevMgr2` / `IWiaItem2` / `IWiaTransfer` COM vtable interop only**, enumerating `WIA_CATEGORY_FRONT` / `WIA_CATEGORY_BACK` child items with folder transfer for duplex (§7.3).

### 3.5 [HIGH] The "Physics-Enforced Flatbed Guard" as specified is a bug generator

The brief demands: deskew angle clamped to `[0.6°, 6.0°]`, anything outside forced to `0.0°`.

Problems: a page skewed 8° (very common on a hand-placed flatbed and near-universal on ADF misfeeds) would be left **completely uncorrected**; a genuine 0.3° skew is visible on text at 600 dpi; and the rule ignores *confidence*, which is the actual signal that separates "the page is skewed" from "there is a diagonal line in the artwork."

**Approved replacement (preserves the intent, removes the failure mode):**

```
DeskewPolicy {
  DeadZoneDeg      = 0.6    // below this → 0.0 (no visible benefit, avoids resample loss)
  SoftLimitDeg     = 6.0    // above this → require high confidence
  HardLimitDeg     = 20.0   // above this → never auto-rotate, raise UI hint instead
  MinConfidence    = 0.72   // Sobel+Hough+projection-profile agreement score
  HighConfidence   = 0.90   // required to exceed SoftLimit
  Source-aware: ADF defaults SoftLimit=10°, Flatbed 6°, Film holder 3°
}
```

Evidence combination: (1) Sobel gradient orientation histogram, (2) Hough peak angle, (3) text-baseline projection-profile variance maximisation, (4) minimum-area bounding box via convex hull + rotating calipers. Confidence = normalised agreement among the four estimators. **Ship the legacy exact-brief behaviour as a selectable preset named "Strict Flatbed Guard"** so the original intent is available. Default = the policy above.

### 3.6 [MEDIUM] Other corrections

| Brief statement | Correction |
|---|---|
| "32-bit compatible" main app | The **UI is x64 only**. x86 exists solely as `NextScan.Host32.exe`. ARM64 build ships x64-emulated host32. |
| "`DG_AUDIO` triplets" | Audio DAT groups are for cameras with audio annotation; irrelevant to scanners. Implement the enum for completeness, wire nothing. [LATER] |
| "Under 50 ms for hundreds of MB into Photoshop" | Achievable **only** with shared memory and only for the handoff itself; Photoshop's own document creation is not under our control. Restate as: *our side of the transfer is ≤ 50 ms per 500 MB; end-to-end target ≤ 900 ms.* |
| "Searchable PDF" with zero dependencies | Requires an OCR engine. Tiered design in §11.2: Windows built-in OCR (already on every Win10+ machine, zero bundle cost) → bundled Tesseract 5 → optional ONNX PP-OCR-class model. |
| Single-file `PublishSingleFile` | Works for WPF/.NET self-contained. Native DLLs are extracted to a temp dir on first run unless `IncludeNativeLibrariesForSelfExtract` is managed carefully — measure cold-start; if >2.5 s, ship a **small launcher + app folder** instead and keep "single installer" as the user-facing promise. |
| WPF vs WinUI 3 | **WPF on .NET 9** chosen (§5.2). WinUI 3 adds a Windows App SDK runtime dependency, self-contained bloat, and unpackaged-identity limits — all in direct conflict with §3.2. .NET 9 gives WPF a first-party Fluent theme + `ThemeMode`, closing the visual gap. |

---

## 4. PRODUCT DEFINITION

### 4.1 Personas

| ID | Persona | Needs | Editions |
|---|---|---|---|
| P1 | **Photo/film archivist** ("Rahim, 8000 negatives") | Film profiles, IR dust removal, 48-bit, IT8 colour, batch, non-destructive re-render | Studio |
| P2 | **Photoshop retoucher** | Fast scan → straight into PS as a layer, exact colour, no round-trip to disk | Pro, Studio |
| P3 | **Office / records clerk** | ADF batch, duplex, blank-page drop, barcode separation, searchable PDF, auto-naming | Pro |
| P4 | **Small print/copy shop** (high volume, mixed jobs) | Job presets, hot folders, speed, PDF size, multi-scanner | Pro, Studio |
| P5 | **Enterprise IT deployer** | MSI, GPO, silent install, per-machine licence, no telemetry | Pro/Studio + Volume |
| P6 | **Developer / integrator** | CLI, JSON job spec, our TWAIN DS, local API | Studio |

### 4.2 Editions & pricing structure

| | **Home** | **Pro** | **Studio** |
|---|---|---|---|
| Devices: TWAIN/WIA/eSCL/WSD | ✅ | ✅ | ✅ |
| Bit depth | 24-bit | 24/48-bit | 24/48-bit + RAW `.nsraw` |
| Curves / tone master | Basic | Full | Full + LUT export |
| Document clean-up + OCR searchable PDF | Basic | ✅ | ✅ |
| MRC hyper-compressed PDF | — | ✅ | ✅ |
| ADF / duplex / batch | ✅ | ✅ | ✅ |
| Barcode & separator splitting, hot folders | — | ✅ | ✅ |
| AI: dewarp / shadow / denoise / super-res | 1 model | ✅ | ✅ |
| Film module (NegaFix-class, IR clean, multi-sample HDR) | — | — | ✅ |
| IT8 / ICC profiling | — | Use profiles | Create profiles |
| Photoshop connector | ✅ | ✅ | ✅ |
| Lightroom / Capture One connector | — | ✅ | ✅ |
| NextScan TWAIN DS provider, CLI, scripting API | — | — | ✅ |
| Machines per licence | 2 | 3 | 3 |
| Indicative price | $39 | $89 | $149 (perpetual, 1 yr updates) |

*Deliberately: every edition supports every scanner. No per-device licensing, ever.*

### 4.3 Non-functional requirements [MUST]

| ID | Requirement | Target |
|---|---|---|
| NFR1 | Cold start to interactive window | ≤ 1.8 s on a 2020-class laptop (SSD) |
| NFR2 | Device enumeration (all transports) | First device visible ≤ 800 ms; full list ≤ 3.5 s |
| NFR3 | Preview scan A4 @150 dpi → on canvas | ≤ 700 ms after device delivers data |
| NFR4 | Curve/LUT apply, 8000×11000 48-bit | ≤ 120 ms on 8-core (tiled, parallel) |
| NFR5 | Pan/zoom on canvas | Sustained 60 fps at 4K, any zoom 10–800 % |
| NFR6 | Peak RAM, 600 dpi A3 48-bit + preview | ≤ 2.5 GB (tiled/paged; never load whole page at full depth twice) |
| NFR7 | Driver crash | Never terminates UI; recovered with a user-visible, actionable message |
| NFR8 | Any scan of ≥1 page must be recoverable after a power loss | Journal to disk per page |
| NFR9 | Accessibility | Full keyboard operation; UIA/screen-reader names on every control; contrast ≥ 4.5:1 |
| NFR10 | Localisation | en, bn, de, es, fr, ja, pt-BR, ru, zh-Hans at 1.0; RTL-ready layout |
| NFR11 | Offline | 100 % of core function works with no network |
| NFR12 | Installer size | ≤ 180 MB (≤ 320 MB with all OCR languages + AI models) |

---

## 5. SYSTEM ARCHITECTURE

### 5.1 Process model (the backbone)

```
┌──────────────────────────────────────────────────────────────────────────┐
│  NextScanner.exe   (x64, WPF, STA UI thread)                             │
│  ├─ UI Shell / Canvas / Inspector          (§13)                         │
│  ├─ Session & Job Orchestrator             (§12)                         │
│  ├─ Imaging Core  (managed + native SIMD + D3D12 compute)  (§9)          │
│  ├─ AI Runtime  (ONNX Runtime / DirectML)  (§10)                         │
│  ├─ Output Encoders (PDF/TIFF/PNG/JPEG/MRC/OCR)   (§11)                  │
│  ├─ Catalogue + Profiles (SQLite)          (§16)                         │
│  └─ Device Broker  ── named pipes / shared memory ──┐                    │
└─────────────────────────────────────────────────────┼────────────────────┘
                                                      │
        ┌──────────────────────────────┬──────────────┴─────────────┬───────────────────┐
        ▼                              ▼                            ▼                   ▼
┌──────────────────┐   ┌──────────────────┐   ┌────────────────────┐  ┌──────────────────────┐
│ NextScan.Host64  │   │ NextScan.Host32  │   │ NextScan.Net.exe   │  │ NextScan.PsBridge    │
│ .exe  (x64)      │   │ .exe  (x86)      │   │ (x64, in-proc opt) │  │ .exe (x64/x86)       │
│ • TWAIN DSM 64   │   │ • TWAIN DSM 32   │   │ • mDNS/DNS-SD      │  │ • Photoshop handoff  │
│ • WIA2 (x64 COM) │   │ • TWAIN_32.DLL   │   │ • eSCL HTTP client │  │ • shared-mem writer  │
│ • msg-only HWND  │   │ • WIA2 (x86 COM) │   │ • WSD/WS-Scan      │  └──────────────────────┘
│ • STA driver thr │   │ • msg-only HWND  │   │ • pure managed     │
└──────────────────┘   └──────────────────┘   └────────────────────┘
```

**Rules [MUST]:**
- The UI process **never** calls `LoadLibrary` on a vendor DLL, never creates a vendor COM object, never pumps a vendor's message loop.
- Hosts are **stateless between sessions** and are killed + respawned on any protocol violation, hang (watchdog, §7.7), or crash.
- Image data crosses the boundary via **shared memory** (`CreateFileMapping` + `MapViewOfFile`), never through the pipe. Pipe carries control JSON + a shared-memory handle/name + a completion signal.
- The eSCL/WSD engine is pure managed and safe; it runs **in-process by default** but must be *capable* of running out-of-process (same interface) for future sandboxing.

### 5.2 Technology decisions & rationale

| Decision | Choice | Rationale | Rejected alternative |
|---|---|---|---|
| UI framework | **WPF on .NET 9** (Fluent theme, `ThemeMode`) | No runtime prerequisite; first-class single-file self-contained publish; mature; our canvas is custom-rendered anyway so WPF's DX9 compositor is not on the hot path | WinUI 3 (adds Windows App SDK runtime dep + unpackaged identity limits — conflicts with §3.2); WinForms (no DPI/vector story); Avalonia (no benefit, Windows-only product) |
| Canvas rendering | **Custom D3D11/12 swap-chain panel** hosted via `D3DImage`/HwndHost; CPU fallback path | 60 fps at 4K with 500 MP images requires GPU tiles + mipmap pyramid | SkiaSharp (CPU-fast but adds 8 MB native and no GPU story in WPF without ANGLE); WPF `Image` control (dies above ~100 MP) |
| Image compute | **C++/SIMD (AVX2 + SSE4.2 fallback) in `NextScan.Imaging.Native.dll`** + **HLSL compute shaders** for GPU path; C# orchestration | `System.Numerics.Vector<T>` is good but hand-written AVX2 on 16-bit interleaved data is 2–3× better; compute shaders needed for real-time preview | Pure C# unsafe (fast enough for LUTs, not for convolution at 600 dpi) |
| Colour engine | **lcms2 (static)** | ICC v4 complete, 16-bit and float pipelines, MIT, industry standard | WCS/WIC (known divergence on v4 targets; less control); writing our own (months, worse) |
| AI runtime | **ONNX Runtime + DirectML EP** | Works on any DX12 GPU (AMD/Intel/NVIDIA) with CPU fallback in the same package; no CUDA | CUDA (NVIDIA only); WinML (less control over EPs) |
| Local store | **SQLite (static)** + JSON profiles on disk | Zero-install, transactional, queryable catalogue | LiteDB (managed, fine, but SQLite tooling wins) |
| IPC | **Named pipes (control, JSON-RPC 2.0) + shared memory (pixels)** | Simple, fast, debuggable, no admin rights | COM (bitness pain), gRPC (adds deps + a listening socket = firewall prompts) |
| Installer | **Inno Setup (primary) + WiX v4 (MSI for enterprise)** | Inno = best UX & scripting; MSI = GPO/SCCM requirement from P5 | MSIX (identity/file-association tradeoffs; keep as [LATER] for Store) |
| Signing | **Azure Trusted Signing (a.k.a. Azure Artifact Signing)** if org eligibility met, else OV cert in Azure Key Vault | ~$10/mo, HSM-held keys, Microsoft-recommended; EV no longer buys instant SmartScreen trust | Local EV token (key handling risk, CI pain) |

### 5.3 Solution / repository layout

```
NextScan/
├─ src/
│  ├─ app/
│  │  ├─ NextScanner/                  # WPF host, DI composition root, single-file publish target
│  │  ├─ NextScan.Shell/               # Windows, panels, canvas, view-models (MVVM, CommunityToolkit.Mvvm)
│  │  └─ NextScan.Shell.Controls/      # Canvas, curve editor, histogram, crop overlay, filmstrip
│  ├─ core/
│  │  ├─ NextScan.Core/                # Domain: ScanSession, Page, EditStack, Profile, JobSpec
│  │  ├─ NextScan.Devices/             # IScanDevice, IScanTransport, DeviceBroker, QuirksDb
│  │  ├─ NextScan.Imaging/             # Managed pipeline, tiling, histogram, curves, geometry
│  │  ├─ NextScan.Imaging.Gpu/         # D3D12 compute dispatch + HLSL
│  │  ├─ NextScan.Ai/                  # ONNX session pool, model registry, pre/post-processing
│  │  ├─ NextScan.Output/              # PDF/TIFF/PNG/JPEG writers, MRC, OCR orchestration
│  │  ├─ NextScan.Licensing/           # Activation, entitlements, offline tokens
│  │  └─ NextScan.Automation/          # CLI parser, JSON job spec, JS scripting host, local API
│  ├─ hosts/
│  │  ├─ NextScan.Host.Shared/         # IPC contracts, shared-memory frame protocol
│  │  ├─ NextScan.Host64/              # x64 device host (TWAIN + WIA)
│  │  ├─ NextScan.Host32/              # x86 device host (same sources, different RID)
│  │  ├─ NextScan.Twain/               # TWAIN 2.5 layer (netstandard, AnyCPU-aware)
│  │  ├─ NextScan.Wia/                 # Native WIA 2.0 vtable interop
│  │  └─ NextScan.Net/                 # mDNS + eSCL + WSD
│  ├─ native/
│  │  ├─ NextScan.Imaging.Native/      # C++ AVX2 kernels; statically links lcms2, turbo, openjpeg, jbig2enc, leptonica, tesseract, zlib-ng, sqlite
│  │  └─ NextScan.Twain.Native/        # (optional) C++ shim if any DS requires true native stack frames
│  ├─ connectors/
│  │  ├─ photoshop-uxp/                # UXP hybrid plugin (manifest.json, JS/TS, PS 22+)
│  │  ├─ photoshop-cpp/                # C++ SDK acquire module → NextScanner.8ba / .8ba (x86)
│  │  ├─ photoshop-jsx/                # Next_Scanner.jsx for CS6..CC2019
│  │  ├─ lightroom-plugin/             # Lightroom Classic .lrplugin (Lua)  [SHOULD]
│  │  └─ twain-ds/                     # NextScan.ds — WE are the TWAIN data source  [Studio]
│  └─ tools/
│     ├─ NextScan.DeviceReport/        # Diagnostic bundle collector
│     ├─ NextScan.TwainSimulator/      # Fake TWAIN DS for CI (§18.3)
│     └─ NextScan.EsclSimulator/       # Fake eSCL device for CI
├─ tests/
│  ├─ unit/  integration/  golden/     # Golden-image regression corpus
│  └─ perf/                            # BenchmarkDotNet suites tied to NFR table
├─ assets/  models/  profiles/  locales/  targets-it8/
├─ installer/
│  ├─ inno/NextScanner.iss
│  ├─ wix/NextScanner.wxs
│  └─ scripts/build_all.ps1
├─ docs/                               # This plan + ADRs + protocol specs
└─ .github/workflows/ or azure-pipelines.yml
```

### 5.4 Cross-cutting engineering standards

- **Language levels:** C# 13 / .NET 9, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<AnalysisLevel>latest-recommended</AnalysisLevel>`. C++20 for native, `/W4 /WX`, `/guard:cf`, `/sdl`, CET-compatible.
- **Async:** all I/O async; hardware calls are **synchronous on their own dedicated STA thread inside the host process** and surfaced to the UI as async messages. Never `.Result`/`.Wait()` on the UI thread.
- **Errors:** no exceptions across the IPC boundary. Hosts return `Result<T>` with a stable `NsErrorCode` (Appendix G) + a device-supplied condition code + a human message + a suggested remedy string ID.
- **Logging:** structured (Serilog-style sink written by us or `Microsoft.Extensions.Logging` + our file sink), rolling, redacting file paths under a privacy flag. Every TWAIN triplet and every eSCL HTTP exchange logged at Trace, with a "Capture Diagnostic Bundle" button in the UI.
- **DI:** `Microsoft.Extensions.DependencyInjection`, composition root in `NextScanner`.
- **Threading model for images:** immutable `ImageBuffer` handles, copy-on-write tiles, `Parallel.For` over tile rows with a partitioner tuned to L2 size.
- **ADR discipline:** every non-obvious decision gets `docs/adr/NNNN-title.md`.

---

## 6. DEVICE ABSTRACTION LAYER

### 6.1 Core interfaces (contract, not code)

```
IScanTransport            // TWAIN | WIA | ESCL | WSD | FILE(import) | SIMULATOR
    ProbeAsync(ct) -> IReadOnlyList<DeviceDescriptor>
    OpenAsync(deviceId, ct) -> IScanDeviceSession

DeviceDescriptor          // stable identity across transports
    TransportId, NativeId, VendorId/ProductId (if resolvable), FriendlyName,
    Bitness, Capabilities(summary), IsNetwork, Endpoint, QuirkKey

IScanDeviceSession : IAsyncDisposable
    GetCapabilitiesAsync()        -> DeviceCapabilities
    NegotiateAsync(ScanSettings)  -> NegotiationResult   // what we actually got vs asked
    StartAsync(ScanIntent)        -> IAsyncEnumerable<AcquiredFrame>
    CancelAsync()
    ShowVendorUiAsync(owner)      // optional, TWAIN only
    Events: StatusChanged, PaperJam, CoverOpen, FeederEmpty, PageStarted, PageProgress

AcquiredFrame
    SharedMemoryRegion (name, size), Width, Height, BitsPerChannel, Channels,
    Stride, PixelLayout, XRes, YRes, PageIndex, Side(Front/Back), IsPreview,
    InfraredPlane?, DeviceTimestamp
```

**Capability model:** a single `DeviceCapabilities` record normalises TWAIN CAPs, WIA properties and eSCL XML into one vocabulary (resolutions, colour modes, sources, duplex, bit depths, physical bed size, auto-crop/deskew support, IR channel, brightness/contrast/gamma ranges, ADF capacity, supported output formats). Each transport ships a translator + a **fidelity score** so the UI can grey out what the device genuinely cannot do rather than failing at scan time.

### 6.2 Transport selection & fallback policy [MUST]

For a physical device visible on multiple transports, rank:

1. **TWAIN (native vendor DS)** — richest features (hardware colour correction, multi-stream, advanced ADF/duplex, deskew/hole-punch in firmware)
2. **eSCL** — for network devices; no driver at all, fast, stable
3. **WIA 2.0 native** — reliable baseline, good memory behaviour
4. **WSD** — last resort for network devices without eSCL

Rules:
- De-duplicate by (vendor, model, serial/USB path) — one device, one card in the UI, with a "Connection: TWAIN 64-bit / eSCL / WIA" selector.
- If the ranked transport fails **twice in a row** for a given operation, auto-fall back one rank, show a non-blocking toast ("Switched to WIA — this scanner's TWAIN driver stopped responding"), and record it in the Quirks DB local overlay.
- Never fall back silently for *colour-critical* work (film/48-bit): ask, because fallback can change colour rendering.

---

## 7. ACQUISITION ENGINES — DETAILED SPECIFICATION

### 7.1 Host process protocol (§5.1 boundary)

**Control channel:** named pipe `\\.\pipe\NextScan.Host.{sessionGuid}`, JSON-RPC 2.0, UTF-8, length-prefixed.
Methods: `probe`, `open`, `caps`, `negotiate`, `start`, `cancel`, `showUi`, `close`, `ping`, `shutdown`.
Notifications (host→app): `status`, `pageStart`, `pageProgress`, `frameReady`, `pageEnd`, `deviceEvent`, `log`.

**Data channel:** for each frame the host creates `Local\NextScan.Frame.{guid}` file mapping, writes a `FrameHeader` (128-byte, versioned, Appendix D) followed by raw pixels, then sends `frameReady`. The app maps it read-only, copies/converts into the pipeline (or wraps it zero-copy if layout matches), then acks `frameConsumed` so the host can free/reuse. **Ring of N=4 buffers** to keep the scanner streaming.

**Liveness:** app pings every 2 s; host must reply within 5 s. Host runs its own watchdog: if a driver call blocks > `QuirkTimeout` (default 120 s for scan, 20 s for caps), the host reports `DeviceHung`, and the app offers "Cancel / Restart driver / Switch transport". App may `TerminateProcess` the host — this is safe by design.

### 7.2 Native TWAIN 2.5 engine (`NextScan.Twain`)

**DSM binding [MUST]:**
1. Search order (per bitness):
   - `%WINDIR%\System32\TWAINDSM.DLL` (64-bit host) / `%WINDIR%\SysWOW64\TWAINDSM.DLL` (32-bit host)
   - App-local `TWAINDSM.DLL` **only if we are legally allowed to ship it** — the reference DSM is LGPL; **default: do not ship**. Instead, if no DSM is present, fall back to:
   - `%WINDIR%\TWAIN_32.DLL` (32-bit host only; the legacy Microsoft-shipped 1.x DSM)
2. `LoadLibrary` + `GetProcAddress("DSM_Entry")`. Bind as a delegate with `CallingConvention.StdCall` (x86) / default (x64), `SetLastError=false`.
3. Identify ourselves as a **TWAIN 2.x application** (`SupportedGroups |= DF_APP2`) so `DAT_ENTRYPOINT` memory functions are provided; store `DSM_MemAllocate/Free/Lock/Unlock` and **use them instead of `GlobalAlloc` whenever the DSM is 2.x** (this is a common source of leaks/crashes in naive implementations).

**State machine [MUST]** — model explicitly as an enum with legal-transition assertions:

| State | Meaning | Entering triplet |
|---|---|---|
| 1 | Pre-session (DSM not loaded) | — |
| 2 | DSM loaded | `LoadLibrary` |
| 3 | DSM open | `DG_CONTROL/DAT_PARENT/MSG_OPENDSM` |
| 4 | Source open | `DG_CONTROL/DAT_IDENTITY/MSG_OPENDS` |
| 5 | Source enabled | `DG_CONTROL/DAT_USERINTERFACE/MSG_ENABLEDS` (or `…_UIONLY`) |
| 6 | Transfer ready | `MSG_XFERREADY` received |
| 7 | Transferring | `DG_IMAGE/DAT_IMAGENATIVEXFER|IMAGEMEMXFER/MSG_GET` |

Down-transitions: `MSG_ENDXFER` → 6 or 5; `MSG_RESET` → 5; `MSG_DISABLEDS` → 4; `MSG_CLOSEDS` → 3; `MSG_CLOSEDSM` → 2. **Every triplet call asserts the current state is legal, logs `(DG,DAT,MSG,state,rc,cc)`, and on `TWRC_FAILURE` fetches `DG_CONTROL/DAT_STATUS/MSG_GET`.**

**Message pump [MUST]:** a dedicated **STA thread** owning a message-only window (`HWND_MESSAGE`) whose handle is passed to `MSG_OPENDSM` (x86: `TW_INT32` HWND; both: pass the real HWND, not `IntPtr.Zero`). The pump loop: `GetMessage` → build `TW_EVENT{pEvent=&msg}` → `DG_CONTROL/DAT_EVENT/MSG_PROCESSEVENT`. Handle `MSG_XFERREADY`, `MSG_CLOSEDSREQ`, `MSG_CLOSEDSOK`, `MSG_DEVICEEVENT`. **Never** run this pump on the UI thread (that is precisely why it lives in a host process).

**Transfer mechanisms:**
- **`TWSX_MEMORY` is the default** (`DAT_IMAGEMEMXFER`): allocate strips of `TW_SETUPMEMXFER.Preferred` size, loop `MSG_GET` until `TWRC_XFERDONE`, assembling into our shared-memory frame. Works for arbitrarily large images and for 48-bit where DIB handling is fragile.
- **`TWSX_NATIVE`** (`DAT_IMAGENATIVEXFER`) as fallback: returns an `HBITMAP`/DIB handle → `GlobalLock` → parse `BITMAPINFOHEADER` (and `BITMAPV5HEADER` when `biSize` says so) → handle 1/4/8/24/32-bit, bottom-up rows, palette for ≤8-bit, and **`BI_BITFIELDS`**. Free with the DSM's memory functions when DSM 2.x supplied them, else `GlobalFree`.
- **`TWSX_FILE` / `TWSX_MEMFILE`** [SHOULD]: some ADF sheetfeds are dramatically faster here and emit JPEG/TIFF directly.
- Always read `DAT_IMAGEINFO` before and `DAT_EXTIMAGEINFO` after (page number, bar codes, blank-page detection, skew angle, patch code — **many scanners report these in firmware and we should prefer them over our own detection when available**).

**Capability negotiation [MUST]:** helper that does `MSG_GET` → `MSG_GETCURRENT` → `MSG_GETDEFAULT` → `MSG_SET` → **verify with `MSG_GETCURRENT`** and reports the delta. Never assume a `MSG_SET` succeeded. Full CAP list in Appendix A. Minimum set: `CAP_XFERCOUNT`, `CAP_XFERMECH`, `CAP_DUPLEXENABLED`, `CAP_FEEDERENABLED`, `CAP_AUTOFEED`, `CAP_AUTOSCAN`, `CAP_UICONTROLLABLE`, `CAP_INDICATORS`, `ICAP_PIXELTYPE`, `ICAP_BITDEPTH`, `ICAP_XRESOLUTION`, `ICAP_YRESOLUTION`, `ICAP_UNITS`, `ICAP_SUPPORTEDSIZES`, `ICAP_PHYSICALWIDTH/HEIGHT`, `ICAP_FRAMES`, `ICAP_AUTOMATICBORDERDETECTION`, `ICAP_AUTOMATICDESKEW`, `ICAP_AUTOMATICROTATE`, `ICAP_BRIGHTNESS`, `ICAP_CONTRAST`, `ICAP_GAMMA`, `ICAP_PLANARCHUNKY`, `ICAP_PIXELFLAVOR`, `ICAP_COMPRESSION`, `ICAP_IMAGEFILEFORMAT`, `ICAP_JPEGQUALITY`, `ICAP_FILMTYPE`/`ICAP_LIGHTPATH` (transparency unit — **required for film**), `ICAP_LIGHTSOURCE`.

**Container types:** implement `TW_ONEVALUE`, `TW_ENUMERATION`, `TW_RANGE`, `TW_ARRAY` marshalling with correct `TWTY_*` sizing, including `TW_FIX32` (two 16-bit halves) and `TW_FRAME`. This is where most third-party implementations get subtly wrong — write property-based tests (§18.2).

**UI modes:** support (a) hidden UI (`ShowUI=FALSE`, our own controls — the default and the product's whole point), (b) `MSG_ENABLEDSUIONLY` for "open the vendor's dialog" escape hatch, (c) vendor UI shown. Some sources refuse `ShowUI=FALSE`; the Quirks DB records this and we transparently degrade to vendor UI with an explanatory banner.

### 7.3 Native WIA 2.0 engine (`NextScan.Wia`)

**[MUST] Native COM vtable interop only.** No `wiaaut.dll`, no `WIA.CommonDialog` (§3.4).

- Create `IWiaDevMgr2` (CLSID_WiaDevMgr2), `EnumDeviceInfo` → `IEnumWIA_DEV_INFO` → property storage (`WIA_DIP_DEV_ID`, `WIA_DIP_DEV_NAME`, `WIA_DIP_VEND_DESC`, `WIA_DIP_BAUDRATE`).
- `CreateDevice` → `IWiaItem2` root → `EnumChildItems(&WIA_CATEGORY_FLATBED / FEEDER / FILM)`.
- **Duplex:** enumerate `WIA_CATEGORY_FRONT` / `WIA_CATEGORY_BACK` child items; set `WIA_DPS_DOCUMENT_HANDLING_SELECT |= DUPLEX`; use **folder/multi-item transfer** so both sides come back. Detect `ADVANCED_DUP` for independent front/back settings.
- **Transfer:** implement `IWiaTransfer` + our own `IWiaTransferCallback` (`TransferCallback`, `GetNextStream`) returning an `IStream` implemented over our shared-memory region (implement `IStream` ourselves; do **not** use `SHCreateMemStream` since we need to hand the buffer onward). Use `WIA_TRANSFER_ACQUIRE_CHILDREN` for batch/ADF.
- **Properties:** `WIA_IPS_XRES/YRES`, `WIA_IPS_XPOS/YPOS/XEXTENT/YEXTENT`, `WIA_IPA_DATATYPE`, `WIA_IPA_DEPTH`, `WIA_IPA_FORMAT` (prefer `WiaImgFmt_BMP`/`RAW` for lossless; `TIFF`/`JPEG` when the device is faster), `WIA_IPS_BRIGHTNESS`, `WIA_IPS_CONTRAST`, `WIA_IPS_CUR_INTENT`, `WIA_IPS_PAGES`, `WIA_IPS_DOCUMENT_HANDLING_SELECT`, `WIA_DPS_PAGES`, `WIA_IPS_PREVIEW`, `WIA_IPS_SEGMENTATION`, `WIA_IPS_FILM_SCAN_MODE` (film).
- Read property **valid values** (`WIA_PROP_LIST` / `WIA_PROP_RANGE` / `WIA_PROP_FLAG`) before setting — clamp instead of failing.
- Handle `WIA_ERROR_PAPER_JAM`, `_PAPER_EMPTY`, `_COVER_OPEN`, `_BUSY`, `_OFFLINE`, `_WARMING_UP`, `_USER_INTERVENTION` and map each to an actionable UI string.

### 7.4 Network engines (`NextScan.Net`)

**7.4.1 Discovery [MUST]**
- Primary: Win32 **DNS-SD API** (`DnsServiceBrowse` / `DnsServiceResolve`, available Win10+) — avoids raw-socket/firewall complications.
- Fallback: our own **mDNS client** on UDP 5353 (multicast 224.0.0.251 / FF02::FB), sending PTR queries for `_uscan._tcp.local` and `_uscans._tcp.local`, parsing PTR/SRV/TXT/A/AAAA, honouring TTLs and the one-shot/continuous query rules.
- Also browse `_scanner._tcp` and WSD via **WS-Discovery** (SOAP-over-UDP `239.255.255.250:3702`, `Probe` for `wsdp:Device`/`scan:ScanDeviceType`).
- Parse the TXT record: `rs=` gives the eSCL root path (usually `eSCL`, but **not always** — never hard-code), `ty=`, `note=`, `mdl=`, `adminurl=`, `pdl=`, `is=` (input sources), `cs=` (colour space), `duplex=`, `uuid=`.
- **Manual add** by IP/URL must exist (`escl:Name:http://192.168.1.50:8080/eSCL`) — corporate networks block mDNS.
- Cache discovered devices with last-seen timestamps; re-probe on network change (`NetworkChange` events).

**7.4.2 eSCL client [MUST]**
- `GET {root}/ScannerCapabilities` → parse XML (`pwg:`/`scan:` namespaces): `Platen`/`Adf` input caps, `SettingProfiles` (colour modes, `DocumentFormats` incl. `application/pdf`, `image/jpeg`, `image/tiff`, `application/octet-stream`), discrete + range resolutions, `MaxWidth/MaxHeight/MinWidth/MinHeight`, `MaxOpticalXResolution`, `RiskyLeftMargins`, `SharpenSupport`, `ColorModes`, `CcdChannels`, `BinaryRenderings`, `FeedDirections`, `AdfDuplexMaxWidth`.
- `GET {root}/ScannerStatus` → `Jobs`, `State` (Idle/Processing/Testing/Stopped), `AdfState` (`ScannerAdfLoaded` / `ScannerAdfEmpty` / `ScannerAdfJam`).
- `POST {root}/ScanJobs` with a `scan:ScanSettings` XML body (Version, Intent, ScanRegions[XOffset,YOffset,Width,Height,ContentRegionUnits=escl:ThreeHundredthsOfInch], InputSource, ColorMode, XResolution/YResolution, DocumentFormatExt, Duplex, Brightness/Contrast/Threshold, CompressionFactor, BlankPageDetection).
  → expect **HTTP 201 + `Location:` header** containing the job URI (id may be int or UUID; **use the returned Location verbatim**, do not reconstruct).
- `GET {jobUri}/NextDocument` → image bytes; repeat until **404/410** (= job complete). **`503` must be retried**: 30 attempts × 1000 ms for `NextDocument`, 10 attempts for other requests (real devices — e.g. HP LaserJet MFP M28w — need this at high DPI).
- `DELETE {jobUri}` to cancel.
- HTTPS (`_uscans._tcp`) with self-signed certs: accept per-device pinned cert after an explicit user confirmation; never blanket-disable validation.
- Decode JPEG/PDF/TIFF/raw responses in-stream, page by page, feeding the pipeline as pages arrive (never buffer a 100-page job in RAM).

**7.4.3 WSD / WS-Scan [SHOULD]** — SOAP `CreateScanJob` / `RetrieveImage`; needed for older network MFPs (Brother, Samsung/HP LaserJet) with no eSCL.

**7.4.4 TWAIN Direct** [LATER] — evaluate at v1.1.

### 7.5 Additional acquisition sources [SHOULD]

- **Import from file/folder** (JPEG/TIFF/PNG/PDF/RAW) so the whole editing/output pipeline works on existing images — cheap to build, big perceived value, and essential for a "HDR Studio"-style workflow like SilverFast's.
- **Camera capture** [LATER]: phone-as-scanner via a QR-paired local HTTP endpoint (this is CamScanner's whole business; we can take it later with the AI dewarp already in place).

### 7.6 Device Quirks Database (D6) [MUST]

A signed JSON document, shipped with the app and refreshable from our CDN (opt-out-able, cached, versioned):

```
QuirkKey  = "twain:{manufacturer}|{productFamily}|{productName}"  (normalised, case-folded)
          | "escl:{mdl}|{ty}"  | "wia:{vendorId}:{productId}"

Quirk {
  key, matchPattern (glob/regex), appliesToDsVersionRange,
  forceTransferMech, avoidNativeXfer, requiresVendorUi, maxResolutionOverride,
  brokenCapabilities[], resolutionsWhitelist[], needsCapResetBeforeSet,
  scanTimeoutSeconds, postCloseDelayMs, disableAutoDeskew, invertPixelFlavor,
  irChannelLayout, filmHolderOffsetsMm, notes, verifiedBy, verifiedOn
}
```

Local overlay file records user- or fallback-learned quirks and can be uploaded with consent via **Help → Send Device Report**. **This database is the compounding moat; instrument everything to feed it.**

### 7.7 Failure taxonomy & recovery [MUST]

| Failure | Detection | Recovery |
|---|---|---|
| Host process crash | Pipe broken | Respawn host, restore session, resume from last completed page, tell the user which page to re-feed |
| Driver hang | Watchdog timeout | Offer cancel → `TerminateProcess` host → mark quirk → suggest transport fallback |
| Paper jam / feeder empty / cover open | TWAIN `TWCC_*`, WIA `WIA_ERROR_*`, eSCL `AdfState` | Non-modal actionable banner with Resume/Cancel; ADF resume must not renumber pages |
| Device unplugged mid-scan | Enumeration + I/O error | Preserve captured pages, mark session "interrupted" |
| Out of disk during batch | Pre-flight + live check | Pause job, prompt for another location |
| Bitness mismatch | Host32 has device, Host64 doesn't | Transparent; user never sees the word "bitness" unless they open Diagnostics |

---

## 8. IMAGE DATA MODEL

### 8.1 The pixel contract

- **Internal working format:** planar-or-interleaved **16-bit unsigned per channel, linear or device-encoded (tagged)**, plus an optional 16-bit **infrared plane** and an optional 8-bit alpha/mask plane.
- 8-bit inputs are promoted to 16-bit at ingest (shift + dither on the way back down). All processing is 16-bit minimum; float32 for tone-mapping and AI stages.
- Every buffer carries a `ColorSpaceTag` (ICC profile handle or well-known: sRGB, AdobeRGB, ProPhoto, Linear-sRGB, Device) and a `TransferState` (device / linear / display).
- **Never** convert to `System.Drawing.Bitmap`. GDI+ mangles 48-bit and ICC. The only GDI/WIC use is optional final-format interop.

### 8.2 Tiling & memory strategy [MUST]

- Images are `TiledImage`: 256×256 tiles (tunable), with a **mip pyramid** for canvas display.
- Backing store: RAM up to a budget (default 40 % of physical), then a **memory-mapped scratch file** in `%LOCALAPPDATA%\NextScan\scratch` (deleted on clean exit; reaped on start).
- Edits are **non-destructive**: `EditStack` = ordered list of parameterised operations. Display = pyramid level rendered on demand; export = full-resolution replay, tile-parallel.
- Undo/redo = EditStack snapshots (cheap — parameters only, not pixels).

### 8.3 `.nsraw` archive container (D2) [Studio, SHOULD]

A ZIP-based container (deflate/store) with:
```
/manifest.json          schema version, device, settings, timestamps, app version
/raw/page-0001.bin      original sensor data as delivered (uncompressed or device-native)
/raw/page-0001.ir.bin   infrared plane if present
/meta/page-0001.json    IMAGEINFO/EXTIMAGEINFO/eSCL job XML, calibration state
/edits/page-0001.json   EditStack
/preview/page-0001.jpg  fast thumbnail
/profiles/*.icc         embedded ICC used at capture
```
Guarantee: **any `.nsraw` can be re-rendered by any future version.** Version the EditStack schema and keep a migration table.

---

## 9. IMAGE PROCESSING ENGINE

### 9.1 Pipeline order (canonical; the "Guided Mode" enforces it)

```
1  Ingest / bit-depth promote / endianness / pixel-flavour normalise
2  Device linearisation (per-device calibration LUT, if IT8/Quirk provides one)
3  Infrared defect detection      → mask               [film, §10.4]
4  Multi-sample / bracket merge   (HDR)                [film, §10.5]
5  Colour: device profile → working space (lcms2, 16-bit, BPC, rendering intent)
6  Geometry: perspective/dewarp → deskew → crop → rotate → mirror   [§9.4, §10.2]
7  Defect removal: IR-guided inpaint, dust/scratch (SRD-class), hole punch
8  Noise reduction (edge-preserving) / grain management
9  Illumination & background: shadow removal, background estimation, whitening [§9.5]
10 Tone: exposure → curves (RGB + per-channel) → levels → local contrast (AACO-class) → saturation/vibrance
11 Negative inversion + film profile                    [film, §10.3]
12 Sharpening (USM / deconvolution-lite) — always last spatial op
13 Binarisation (Otsu / Sauvola / adaptive) — document B&W only
14 Output transform: working → output space, bit-depth reduce + dither, resample
```

Every stage is a node with: parameters, an "enabled" flag, a preview-quality path and an export-quality path, and a deterministic golden-image test.

### 9.2 Tone & Curves Master [MUST]

- Channels: **RGB (master), R, G, B, plus L (luminosity) [SHOULD]**.
- Interpolation: **monotone cubic Hermite (Fritsch–Carlson)** as default — guarantees no overshoot; **Akima** as an option for smoother midtones. Both must be exactly reproducible across versions (pin the algorithm; golden-test the LUT bytes).
- Knots: up to 16, draggable, deletable, with numeric entry; input/output readouts in 0–255 and 0–65535 and %.
- **LUT:** 16-bit in → 16-bit out, 65536-entry per channel, rebuilt on the UI thread (< 2 ms) and swapped atomically (double-buffered) so the render thread never sees a torn LUT.
- **Apply:** AVX2 kernel, 16 pixels/iteration, gather-free (LUT is 128 KB per channel → fits L2; use a 4096-entry LUT + linear interpolation when cache pressure matters). `Parallel.For` over tile rows.
- Extras: auto-levels (per-channel white/black clip percentiles), neutral-pipette grey balance (SilverFast parity), black/white/grey point droppers, per-channel histogram with clipping warning overlay, "before/after" split.
- Presets: save/load `.nscurve`; **export to `.acv` (Photoshop curves) and 3D `.cube` LUT** [SHOULD] — a real differentiator for the retoucher persona.

### 9.3 Histogram & analysis

- 16-bit histograms computed on a decimated pyramid level for interactivity, exact on export.
- Displays: RGB overlay, per-channel, luminosity, log/linear toggle, clipping indicators, **densitometer with up to 4 pinned sample points** (SilverFast's "Multiple Densitometer" parity) showing RGB/CMY/Lab/density.

### 9.4 Geometry: edge detection, deskew, crop

- **Edge/document detection:** downsample → bilateral/median → Sobel or Scharr gradients → adaptive threshold → contour trace → convex hull → **rotating calipers minimum-area rectangle**; validate with an area/aspect sanity check against the declared bed size.
- **Deskew:** four independent estimators (Hough peak, gradient-orientation histogram, projection-profile variance, min-area-rect angle) combined into an angle + confidence; **apply §3.5 DeskewPolicy**.
- Prefer the **device's own** deskew/border-detect (TWAIN `ICAP_AUTOMATICDESKEW`/`ICAP_AUTOMATICBORDERDETECTION`, eSCL, firmware) when available and enabled — it is free and better; our software path handles everything else.
- **Resampling:** Catmull-Rom / Lanczos-3 selectable; rotation done in one combined affine transform with the crop (never two resamples).
- **Multi-region:** one flatbed sweep → N crop frames → N output files (essential for photos/receipts scanned in batches). Auto-detect multiple photos on the platen with a size/aspect filter.

### 9.5 Document clean-up & whitening [MUST]

- **Background illumination estimation:** large-radius morphological opening/closing (or a fast box-median at 1/8 scale, then bicubic upsample) → division-based flat-fielding. Must not eat large solid-black areas (guard by local variance).
- **Whitening:** map the estimated paper luminance to 250–255 with a soft knee that preserves faint pencil/stamp detail; user slider "Paper white strength" with live preview.
- **Tint neutralisation (aged/blue/yellow paper):** estimate paper chroma in Lab from the background layer, then apply a *chroma-selective* neutralisation that excludes pixels far from the paper chroma cluster — this is what protects blue signatures and red rubber stamps. Implement as a soft mask in ab-space with a user "Protect coloured ink" toggle (default ON).
- **Binarisation:** global **Otsu**, local **Sauvola** (with integral-image acceleration; window and k exposed), plus **Wolf–Jolion** [SHOULD]. Auto-pick by content analysis.
- Punch-hole removal, edge/black-border cleanup, despeckle (connected-component size filter), blank-page detection (ink coverage + variance thresholds, tunable, with a preview list so the user can veto).

### 9.6 Local tone (AACO-class)

Contrast-limited adaptive histogram equalisation on the L channel with a shadow-weighted gain curve, or a fast local Laplacian approximation. Requirement: recovers dark, high-contrast areas **without** touching highlight detail; A/B against SilverFast AACO output during QA.

### 9.7 Sharpening & noise

- USM with radius/amount/threshold on luminance only; masked to avoid haloing (edge-aware).
- Noise reduction: guided filter or BM3D-lite for chroma noise; grain-preserving mode for film [Studio].
- Both must have a "preview at 100 %" rule — never judge sharpening at a fit-to-window zoom; the UI must warn.

### 9.8 GPU path (D4)

- HLSL compute shaders for: LUT apply, colour matrix, convolution (separable), resample, histogram (wave-intrinsic reduction), local contrast, background estimation.
- Dispatch via D3D12 with a ring of upload/readback heaps; tile-granular so only visible tiles are processed for preview.
- **Rule:** GPU results must match CPU within 1 LSB at 16-bit for every kernel (golden test). Any mismatch → the CPU path is authoritative for export.
- Automatic fallback if no DX12, if the adapter is a basic-render driver, or if a shader compile fails.

---

## 10. AI SUBSYSTEM (D5) & FILM MODULE

### 10.1 Runtime

- **ONNX Runtime with the DirectML EP** (works on AMD/Intel/NVIDIA, no CUDA), CPU EP fallback in the same package.
- Only the `onnxruntime-directml` variant is bundled (the CPU-only and DirectML packages conflict).
- Export all models with `dynamic_axes` for variable input size; validate DirectML shape support per model; use **FP16** where quality permits (≈30–50 % faster, half the VRAM).
- Model registry: `models/registry.json` with name, task, version, sha256, input/output spec, licence, min VRAM, tile size, and an "enabled by edition" flag. Models are **downloadable on demand** (with an offline bundle option for enterprise) to keep the base installer small.
- Session pool with a hard VRAM budget; large images processed in overlapping tiles with feathered blending.

### 10.2 v1.0 model set

| Task | Approach | Notes |
|---|---|---|
| **Document shadow removal** | DocShadow-class (pre-exported ONNX exists) or LP-IOANet for latency | Highest-visible-value AI feature for documents |
| **Dewarp / page flattening** | DocTr / DocReal / Marior-class | For book spines, curled pages, phone captures |
| **Generalist restoration** | DocRes (unifies several restoration tasks in one graph) | Reduces model count; evaluate first |
| **Denoise / grain** | Lightweight U-Net, our own training on film scans [LATER] | Classical NR ships at 1.0; AI NR at 1.1 |
| **Super-resolution ×2** | Efficient SR backbone (NTIRE-class efficient track) | "Enhance to 1200 dpi" marketing feature; must be labelled as *interpolated* |
| **Text detection/recognition** | PP-OCR-class detector + recogniser | Tier-3 OCR (§11.2) and orientation detection |
| **Document classification** | Small CNN/ViT on page thumbnails, 12 classes (invoice, receipt, ID, letter, form, photo, book page, business card, cheque, contract, handwritten, other) | Powers auto-naming + auto-routing (Epson ScanSmart parity) |
| **Photo vs document detection** | Same head | Chooses the default processing profile automatically |

**Governance [MUST]:** every model's licence recorded in the SBOM; no model with a non-commercial licence ships. Where no permissively-licensed pre-trained model exists, we train on licensed/synthetic data or defer the feature. **Never ship weights of unknown provenance.**

**UX rule:** AI is always *suggestive and reversible* — every AI stage appears as a toggleable node in the EditStack with a before/after and a strength slider. No irreversible "magic."

### 10.3 Film profiles (NegaFix-class) [Studio]

- Open format `profiles/film/*.nsfilm` (JSON): manufacturer, stock, ISO, process (C-41/E-6/B&W/Kodachrome), per-channel density curves, mask/base colour (D-min) parameters, per-channel gamma, cross-over correction, notes, contributor, version.
- Ship **60+ profiles at 1.0** (Portra 160/400/800, Gold, ColorPlus, Ektar, UltraMax, Superia 200/400/X-TRA, Pro 400H, Vista, Agfa, Cinestill, Tri-X, HP5, Delta, Acros, plus generic C-41 negatives by decade).
- Inversion math: D-min estimation from the film base (unexposed rebate strip if visible, else robust percentile), per-channel log-density inversion, then profile curve application, then white balance. **The "Kodachrome mode" blue-cast correction is a named preset.**
- **Profile editor** with live preview + export/import so the community can extend the library — SilverFast's library is closed; ours being open is a marketing weapon.

### 10.4 Infrared dust & scratch removal (iSRD-class) [Studio]

- Detect IR capability: TWAIN (`ICAP_PIXELTYPE` extended / vendor caps / 4-channel output), WIA film categories, or Quirks DB per model.
- Pipeline: normalise IR plane → flat-field → threshold (adaptive, with user "sensitivity") → morphological cleanup → defect mask → **content-aware inpaint** (PatchMatch-style or exemplar-based; AI inpaint [LATER]) with edge-aware blending.
- **Never apply to Kodachrome or silver-halide B&W by default** (the silver scatters IR → false positives) — auto-warn based on the selected film profile.
- **Software fallback (SRDx-class)** for scanners without IR: multi-scale morphological + median deviation defect detection with a preview mask overlay and manual brush add/remove.

### 10.5 Multi-Sample & Bracketed HDR (patent-safe alternative to Multi-Exposure) [Studio]

- **Multi-Sample:** N passes at *identical* exposure, aligned (sub-pixel phase correlation to fix carriage repeatability) and averaged → √N noise reduction. Long-standing prior art, unencumbered.
- **Bracketed HDR merge:** user-visible exposure bracket (device exposure/gain caps where exposed) → radiometric alignment → weighted merge into 32-bit float → tone map. Present as an explicit, user-driven bracket, not an automatic exposure-varying double scan.
- **[MUST] Legal gate:** before shipping either as a marketing claim of "increased dynamic range", counsel reviews EP 1744278 / US 8,693,808 claims. Document the outcome in `docs/legal/`. If in doubt, ship Multi-Sample only.

### 10.6 IT8 / ICC scanner profiling [Studio]

- Auto-detect an IT8.7/2 or ISO 12641-2 target in a preview (grid detection via the target's fiducials), read patch averages, load the target's reference `.txt`/CGATS data file, and build an ICC v4 input profile (matrix+TRC and/or LUT-based, `AToB0`) with lcms2.
- Support both bundled reference data and user-supplied batch files.
- Validate: report ΔE00 mean/max against the reference; refuse to save a profile above a configurable ΔE threshold and explain why (dirty target, wrong reference file, exposure clipping).
- Monitor profiling is **out of scope** (use the OS); printer profiling [LATER].

---

## 11. OUTPUT & DOCUMENT INTELLIGENCE

### 11.1 Writers (all written by us) [MUST]

| Format | Notes |
|---|---|
| **PDF 1.7 / PDF/A-1b, -2b, -3b** | Our own writer. Image XObjects with `DCTDecode`, `JPXDecode`, `CCITTFaxDecode`, `JBIG2Decode`, `FlateDecode`. Correct `/OutputIntent` + embedded ICC for PDF/A, XMP metadata, font embedding for the OCR text layer, linearisation [SHOULD], AES-256 encryption [SHOULD], digital signature [LATER] |
| **TIFF** | Single & multi-page; LZW, ZIP/Deflate, JPEG, CCITT G4, uncompressed; 1/8/16-bit; embedded ICC; correct resolution tags; BigTIFF above 4 GB |
| **PNG** | 8/16-bit, `iCCP`, `pHYs`, no interlace by default |
| **JPEG** | libjpeg-turbo, quality curve, 4:4:4 for photos, optional progressive, EXIF+ICC |
| **JPEG 2000** | OpenJPEG, for MRC layers and archival [SHOULD] |
| **`.nsraw`** | §8.3 |
| **Clipboard / Photoshop handoff** | §14 |
| WebP / AVIF / HEIF | [LATER] — licence review first |

### 11.2 OCR — three tiers [MUST tier 1+2]

| Tier | Engine | Why |
|---|---|---|
| **1 — Fast/native** | `Windows.Media.Ocr` (`OcrEngine`) | Already on every Win10+ machine, zero bundle size, fully offline, ~25 languages, good on clean scans. **Default for supported languages.** |
| **2 — Breadth** | Bundled **Tesseract 5 (LSTM)** | 100+ languages, built-in `OcrPdfRenderer` for searchable PDF, Apache-2.0. Language packs downloaded on demand. |
| **3 — Quality [SHOULD]** | ONNX PP-OCR-class detector+recogniser | Better on degraded/rotated/multi-column pages; feeds layout analysis |

- **Text layer construction is ours** regardless of tier: word boxes → invisible text render mode `3` → correct per-word font sizing and horizontal scaling so text selection aligns with the image (this is what separates a professional searchable PDF from a bad one).
- Preprocessing before OCR (this is the single biggest accuracy lever): deskew, binarise, DPI normalise to 300, despeckle, and — importantly — **MRC's clean text mask improves OCR**, so run OCR on the mask layer when MRC is enabled.
- Orientation detection (0/90/180/270) via script/text-line analysis, applied before OCR and optionally to the image.

### 11.3 MRC hyper-compression (D9) [Pro/Studio]

- Segment page into **mask (bilevel text/line art)**, **foreground (text colour, heavily downsampled)**, **background (everything else, downsampled + JPEG/JPX)**.
- Encode: mask → **JBIG2 (generic region; symbol mode optional)**, foreground/background → **JPEG or JPEG 2000**.
- Compose as PDF: background image + foreground image **stencil-masked** by the JBIG2 mask (`/ImageMask` + `/Decode`), correct z-order.
- Targets: 300 dpi colour A4 → **20–120 KB** typical; 8–10× smaller than plain JPEG PDF at equal legibility.
- **[MUST] Safety:** JBIG2 *symbol-mode* compression is known to cause character substitution (the notorious Xerox bug). **Default to generic-region coding**; symbol mode is opt-in, off by default, with an explicit warning dialog, and never allowed in PDF/A output.
- Quality presets: Archive (lossless-ish) / Balanced / Smallest; always A/B-previewable before saving.

### 11.4 Document intelligence [Pro]

- **Barcode & patch-code detection** for document separation: Code 39, Code 128, EAN/UPC, ITF, QR, DataMatrix, PDF417, plus TWAIN patch codes T1–T4. Prefer the scanner's own `DAT_EXTIMAGEINFO` barcode results when present. (Own implementation; if quality is insufficient, evaluate a permissive library in a later revision — record as ADR.)
- **Separation rules engine:** split by blank page / barcode value / patch code / page count / OCR regex match. Each rule can also drive the output filename and folder.
- **Auto-naming from content:** template language `{date}_{docType}_{barcode}_{ocr:regex}_{counter:0000}` with a live preview of the resulting names.
- **Auto-classification** → automatic profile + destination selection (§10.2).
- **Table extraction to CSV/XLSX** [SHOULD, v1.1] — layout analysis, ruling-line detection + cell clustering. Be honest in marketing about accuracy.
- **Duplicate detection** across a batch (perceptual hash) [SHOULD].

---

## 12. JOBS, BATCH & AUTOMATION (D10)

### 12.1 Session & job model

```
ScanSession  → Pages[]  → EditStack, Metadata, Source(Front/Back), Status
Job          → { DeviceRef, ScanSettings, ProcessingProfile, OutputSpec[], Separation, Naming, Destination[] }
JobSpec      → serialisable JSON (the same schema used by CLI, hot folders, the Photoshop connector, and the local API)
```

- **Page journal:** every acquired page is written to the scratch store immediately with a journal entry, so a crash/power loss loses at most the in-flight page. Recovery dialog on next start.
- ADF batch: continuous feed with per-page processing on a worker pipeline (acquire → process → encode overlap), re-scan/insert/delete/rotate/reorder individual pages in the filmstrip, "scan more pages into this job".

### 12.2 Profiles & presets

- **Device profile** (settings for a specific scanner), **Processing profile** (the imaging stack), **Output profile** (formats + destinations), and **Job profile** (all three + separation/naming). Ship 20+ curated presets: "Document B&W 300", "Document Colour Clean 300 → searchable PDF", "Photo Print 600 → TIFF 16-bit", "35 mm Negative 3200 → nsraw + TIFF", "ID Card both sides on one page", "Book scan (dewarp + split spine)", "Receipt to CSV", "Business card to contact".
- Import/export profiles as `.nsprofile` files for team sharing; enterprise can lock profiles via a policy file (P5).

### 12.3 Automation surfaces

- **CLI:** `nextscanner scan --device "Epson V850" --profile "Doc300" --out "C:\Scans\{date}\{counter}.pdf" --adf --duplex --pages 0 --json-result`. Exit codes documented (Appendix G). Also `nextscanner list-devices --json`, `nextscanner run-job job.json`, `nextscanner render input.nsraw --preset X`.
- **Hot folders / watch folders:** drop images → processed by a job spec → output + optional move/delete of source.
- **Local API [Studio]:** named pipe JSON-RPC (same schema as CLI). **No TCP listener by default** (avoids firewall prompts and attack surface); optional loopback HTTP behind an explicit toggle + token.
- **Scripting [Studio]:** embedded JS engine (Jint or ClearScript — ADR required; Jint is pure managed and avoids a native dep) exposing `session`, `page`, `edits`, `output`. Sandboxed: no filesystem beyond declared job folders.
- **Windows integration:** Explorer context menu ("Scan to NextScan", "Process with NextScan"), `App Paths` registration so `Win+R → nextscanner` works, WM_DEVICECHANGE-driven "scanner connected" toast, and **scanner hardware-button ("Scan" button) capture via WIA event registration** [SHOULD] — a delightful feature the incumbents do badly.

---

## 13. USER INTERFACE & UX SPECIFICATION

### 13.1 Shell layout

```
┌────────────────────────────────────────────────────────────────────────────────┐
│ ☰  NextScan Studio    [Device ▾ Epson V850 ● Ready]   Preview  Scan   ⚙  ?  ⬤ │  Command bar (48px)
├──────────────────────────────────────────────┬─────────────────────────────────┤
│                                              │  INSPECTOR (resizable 300–520)  │
│         CANVAS (GPU, 60fps)                  │  ▸ Device & Source              │
│   • fit/zoom 10–800 %, smooth wheel + pinch  │  ▸ Scan Settings (dpi/mode/size)│
│   • 9-handle crop, rule-of-thirds guides     │  ▸ Image / Tone  (curves, hist) │
│   • multi-region crop frames                 │  ▸ Document Clean               │
│   • before/after split (draggable)           │  ▸ AI Enhance                   │
│   • live scan sweep animation                │  ▸ Film  (Studio)               │
│   • loupe (hold Z), densitometer pins        │  ▸ Output & Destination         │
│                                              │                                 │
├──────────────────────────────────────────────┴─────────────────────────────────┤
│ FILMSTRIP  [1][2][3][4]…  + Scan more  ⟲ ⟳ ✂ 🗑  (drag to reorder)             │  120px, collapsible
├────────────────────────────────────────────────────────────────────────────────┤
│ Status: Ready · 600 dpi · 48-bit · 8.3 MP · Colour: AdobeRGB · Job: 4 pages     │  Status bar
└────────────────────────────────────────────────────────────────────────────────┘
```

### 13.2 Three complexity levels (solves the VueScan "hidden complexity" problem)

- **Simple** — device, what you're scanning (Document/Photo/Film/ID), where it goes. Four controls, one button.
- **Standard** — + resolution, colour mode, size, basic adjustments, output format.
- **Expert** — everything, all panels, densitometer, raw capability inspector.
Level is per-user and switchable anytime; **settings are never lost when switching down** (they're retained and shown as "Advanced settings active" chips).

### 13.3 Canvas requirements [MUST]

- GPU-composited; tiles streamed from the pyramid; never blocks on full-res.
- Crop: 9 handles + edge drag + move; constrained aspect (with a preset list); numeric entry in mm/inch/px; snapping to detected document edges; sub-pixel arrow-key nudge (Shift = ×10).
- Overlays: detected document outline, deskew angle readout with confidence badge, safe-area for the selected paper size, IR defect mask preview, blank-page/clipping warnings.
- Live scan progress: a moving sweep line with rows appearing as they arrive (this is technically easy — data arrives in strips — and enormously reassuring to users).

### 13.4 Keyboard map [MUST]

| Key | Action |
|---|---|
| `F5` / `F6` | Preview scan / Preview + auto-detect |
| `F7` / `Ctrl+Enter` | Final scan |
| `Esc` | Cancel current scan |
| `Ctrl+A` / `Ctrl+D` / `Ctrl+R` | Select all (full bed) / Auto-detect crop / Reset crop |
| Arrows / `Shift`+Arrows | Nudge crop 1 px / 10 px |
| `Ctrl` `+` / `-` / `0` / `1` | Zoom in / out / fit / 100 % |
| `Z` (hold) | Loupe |
| `\` (hold) | Show original (before) |
| `Ctrl+Z` / `Ctrl+Shift+Z` | Undo / Redo |
| `Ctrl+S` / `Ctrl+Shift+S` | Save / Save As |
| `Ctrl+P` | Send to Photoshop |
| `[` `]` | Previous/next page in filmstrip |
| `Ctrl+K` | Command palette (searchable actions) — a modern touch none of the incumbents have |
| Global `F6` | Bring NextScan forward / trigger scan from anywhere (registered hotkey, user-configurable, conflict-detected) |

### 13.5 Visual design

- WPF Fluent theme (`ThemeMode`) with light/dark/system + a **neutral-grey "colour-critical" theme** (18 % grey chrome) that professionals need for judging colour.
- Per-monitor DPI-aware v2; all icons vector; no bitmap chrome.
- Motion: 120–180 ms ease-out; respect "reduce motion".
- Empty/first-run state: big "Find my scanner" with live progress per transport, and a genuinely helpful failure page ("We found no TWAIN drivers. Your scanner may still work over the network — searching…").

### 13.6 Accessibility & localisation [MUST]

- Full keyboard reachability, visible focus, UIA `AutomationProperties` names/help on every interactive element, live regions for scan status, ≥4.5:1 contrast, no colour-only signalling, honours Windows high contrast.
- All strings in RESX; no concatenation; pseudo-loc build in CI to catch truncation; RTL mirroring ready.
- **Bangla (bn) is a first-class launch language.**

### 13.7 Guided Mode (WorkflowPilot parity) [SHOULD]

An opt-in step-by-step wizard that walks: source → preview → crop → colour → tone → defects → sharpen → output, enforcing correct order and explaining *why* each step is where it is. Excellent for onboarding and for the marketing video.

### 13.8 Diagnostics UI [MUST]

`Help → Diagnostics`: transport status per host process, device capability dump (raw TWAIN CAP list / WIA property list / eSCL XML), last 500 log lines, "Capture diagnostic bundle" (zip: logs, caps, quirks, system info, optional sample scan), "Send device report". This turns support tickets into Quirks DB entries.

---

## 14. PHOTOSHOP INTEGRATION

### 14.1 Three connectors, auto-selected

| Connector | Artifact | Photoshop range | Role |
|---|---|---|---|
| **A. UXP Hybrid plugin** | `NextScanner.ccx` / dev-loaded folder (manifest v5) + our C++ hybrid module | **2021 (v22) → 2026** | Primary. Panel + menu entry, **Action-recordable** (PS 26 records a UXP plugin function call as an Action step), talks to `NextScanner.exe` over the local pipe |
| **B. Classic Acquire module** | `NextScanner.8ba` (x64; x86 build for CS6) | **CS6 → 2026** | Puts us in **`File → Import → Next Scanner…`** exactly as the brief requires; uses `PIActionDescriptor`/Buffer suites; can bridge to the UXP plugin via `PIUXPSuite` |
| **C. ExtendScript** | `Next_Scanner.jsx` in `Presets/Scripts/` | **CS6 → 2020** (works later too) | Fallback + scriptable/Action-recordable path for old installs; also our "it always works" safety net |

**Decision rule at install time:** for each detected Photoshop, install A if v22+, install B always (both bitnesses as appropriate), install C if ≤ v21 or if the user opts in.

### 14.2 Photoshop version/bitness matrix

| Photoshop | Version | Bitness | Plug-in folder | Connectors |
|---|---|---|---|---|
| CS6 | 13 | x86 + x64 | `…\Adobe Photoshop CS6\Plug-ins\` | B(x86+x64), C |
| CC 2014–2018 | 15–19 | x64 (x86 through 2015) | `…\Adobe Photoshop CC 20xx\Plug-ins\` | B(x64), C |
| CC 2019–2020 | 20–21 | x64 | same | B, C |
| 2021–2023 | 22–24 | x64 | same | **A**, B |
| 2024–2026 | 25–27 | x64 (+ARM64) | same | **A**, B |

Discovery at install time (§19.3): registry `HKLM\SOFTWARE\Adobe\Photoshop\<ver>\ApplicationPath`, `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Photoshop.exe`, Creative Cloud's install manifest, and a filesystem sweep of `%ProgramFiles%\Adobe\*`, `%ProgramFiles(x86)%\Adobe\*`, plus any user-specified path. Also honour the user's **additional plug-ins folder** preference.

### 14.3 Handoff protocol (zero-latency, D8)

```
Photoshop plug-in                          NextScanner.exe
      │  1. named pipe connect \\.\pipe\NextScan.PsBridge
      │─── hello{psVersion, bitness, docState, targetMode} ──────────►
      │◄── ack{appVersion, capabilities} ────────────────────────────│
      │  2. "showUi" → NextScanner window comes forward (or headless
      │     if driven by an Action with recorded parameters)
      │◄── scanComplete{ shmName, width, height, channels, bps,
      │                 stride, colorSpace, iccBytesLen, dpi, mode } ─│
      │  3. plug-in maps shm read-only, streams into PS
      │     (Buffer suite / PIImagePlane rows, or UXP imaging API)
      │─── consumed{ok} ────────────────────────────────────────────►│  (app frees shm)
```

- **Target modes:** New Document, New Layer in active doc, Replace selection, Place as Smart Object [SHOULD].
- ICC profile travels with the pixels and is assigned to the created document — **not** converted (the user decides).
- 16-bit/channel documents supported; 8-bit conversion happens in our pipeline with dithering, never by truncation in the plug-in.
- **Performance contract (per §3.6):** our side ≤ 50 ms/500 MB; end-to-end ≤ 900 ms typical.
- **Failure path:** if shared memory fails (rights, size), fall back to a temp file in `%TEMP%` and `open` it — the user must never see a dead end.

### 14.4 Action recording parameters

Expose as ActionDescriptor keys (identical names in A, B and C): `device`, `source` (`flatbed|adf|adfDuplex|film`), `dpi`, `pageSize`, `bitDepth`, `colorMode`, `profileName`, `curvesPreset`, `autoCrop`, `autoDeskew`, `docClean`, `aiEnhance`, `target` (`newDoc|newLayer|smartObject`), `headless`. This makes NextScan fully usable inside `.atn` batch actions and Image Processor workflows.

### 14.5 Other host integrations

- **Lightroom Classic plug-in** (Lua, `.lrplugin`) — "Scan into catalogue" [SHOULD]
- **Capture One** — via hot folder/AppleScript-equivalent (Windows: watched folder + import script) [LATER]
- **Affinity Photo / GIMP / Krita** — served by the TWAIN DS (D7) with no per-app work
- **Our TWAIN Data Source (D7) [Studio]:** ship `NextScan.ds` in `C:\Windows\twain_32` and `twain_64` that presents "NextScan Studio" as a TWAIN source to *any* app; internally it launches/attaches our UI and returns the processed image. This single component makes us available inside Word, Acrobat, LOB apps and every editor at once. Effort ~4 weeks; strategic value very high.

---

## 15. LICENSING, ACTIVATION & MONETISATION

- **Model:** per-user licence key, up to N machines (edition-dependent), **unlimited scanners**, perpetual with 12 months of updates; optional subscription tier [LATER].
- **Activation:** online activation → signed entitlement token (Ed25519, includes edition, machine binding hash, expiry) cached locally; **works offline for 90 days**, then requires a re-check. Manual/offline activation via a challenge-response file for air-gapped enterprises (P5).
- **Machine binding:** salted hash of a stable composite (volume serial + machine GUID + CPU features) — tolerant to single-component change (hardware upgrades must not lock people out; this is a top-3 source of one-star reviews).
- **Trial:** 21 days, full feature, watermark-free but **outputs limited to 30 scans** and 1200 dpi max; trial state stored server-side against a hashed identity so reinstall doesn't reset it, but never in a way that blocks a legitimate re-install after hardware failure.
- **Anti-piracy stance:** cheap-to-defeat obfuscation is not worth the user pain. Implement clean, honest checks + server-side abuse detection (same key, many machines). Focus engineering on value, not on DRM.
- **Anti-tamper:** verify our own binary signatures at start (Authenticode chain), and refuse to load plug-in DLLs that we did not sign.

---

## 16. DATA, SETTINGS & PRIVACY

| Data | Location | Notes |
|---|---|---|
| Settings, profiles, curves | `%APPDATA%\NextScan\` (JSON) | Human-readable, exportable, versioned with migration |
| Catalogue (scan history, jobs) | `%LOCALAPPDATA%\NextScan\catalog.db` (SQLite) | Optional; user can disable/purge |
| Scratch/tiles | `%LOCALAPPDATA%\NextScan\scratch\` | Reaped at startup; configurable drive |
| Models, OCR languages | `%PROGRAMDATA%\NextScan\models\` | Shared across users; signed + hash-verified |
| Quirks DB | `%PROGRAMDATA%\NextScan\quirks\` | Signed; user overlay in `%APPDATA%` |
| Logs | `%LOCALAPPDATA%\NextScan\logs\` | Rolling, 20 MB cap |

**Privacy [MUST]:** scans never leave the machine. Telemetry is **opt-in only**, anonymous, documented field-by-field in the UI, and consists of device/transport success statistics and crash dumps only — **never image content, never filenames**. A single "Privacy: everything stays on this PC" switch disables all network activity including update checks.

---

## 17. SECURITY

- **Attack surface:** file parsers (TIFF/JPEG/PNG/PDF/JPX/XML), network parsers (mDNS/HTTP/XML), IPC, plug-in load paths.
- Parsers run with bounds-checked code; native decoders are **fuzzed in CI** (libFuzzer/AFL++ harnesses on the TIFF/JPEG/JPX/mDNS/eSCL-XML entry points) — this is non-optional given we accept network input.
- **XML:** DTD processing and external entity resolution disabled everywhere (eSCL/WSD responses are untrusted input).
- IPC pipes: ACL'd to the current user SID only; nonce-authenticated handshake; message size caps; strict schema validation.
- DLL search-order hardening: `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` + explicit paths; vendor drivers are loaded **only** in host processes with a restricted mitigation policy where compatible.
- Compiler mitigations: `/GS /guard:cf /guard:ehcont /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT`, CFG, ASLR; .NET: no `AllowPartiallyTrustedCallers`, no `BinaryFormatter`.
- Updates over HTTPS with **certificate pinning + Ed25519 signature on the payload**; downgrade protection.
- Never require admin at runtime. Installer elevates once; per-user install option available.
- Coordinated disclosure policy + `SECURITY.md` + a security contact address.

---

## 18. TESTING & QUALITY

### 18.1 Test pyramid

| Layer | Scope | Tooling |
|---|---|---|
| Unit | Container marshalling, LUT math, curve interpolation, XML parsing, filename templating, licensing | xUnit + FluentAssertions; property-based tests (FsCheck) for TWAIN container round-trips |
| Golden-image | Every processing node, CPU vs GPU parity, film inversion, MRC output | Fixed input corpus + PSNR/ΔE thresholds + byte-exact LUT checks; failures emit a visual diff HTML report |
| Integration | Full acquisition against simulators | §18.3 |
| Hardware | Real devices in a device lab | §18.4 |
| Performance | NFR table (§4.3) as executable assertions | BenchmarkDotNet, fail CI on >10 % regression |
| Fuzz | Parsers | libFuzzer corpora committed |
| UI | Smoke + accessibility tree assertions | WinAppDriver/FlaUI |
| Install | Clean VM matrix, upgrade, uninstall-cleanliness, silent install | Windows Sandbox + VM snapshots |

### 18.2 Notorious-bug regression list (write these tests first)

Bottom-up DIB rows; `BI_BITFIELDS`; 1-bit palette inversion (`ICAP_PIXELFLAVOR`); `TW_FIX32` sign handling; odd-width 24-bit stride padding; 16-bit endianness from memory transfer; ADF page renumbering after a jam; duplex back-side ordering and 180° rotation; eSCL `Location` header with a UUID job id; eSCL 503 retry storm; mDNS TXT `rs=` not equal to "eSCL"; WIA property lists vs ranges; scanner reporting a resolution it cannot deliver; cancel during transfer leaving the DS in state 6; DSM 2.x memory functions vs `GlobalAlloc`.

### 18.3 Simulators [MUST — these unblock everything]

- **`NextScan.TwainSimulator`** — a real, installable TWAIN data source (`.ds`) we write, with switchable personalities: "well-behaved", "slow", "refuses ShowUI=FALSE", "returns wrong resolution", "hangs on MSG_ENABLEDS", "crashes on state 7", "32-bit only", "duplex with reversed backs". Drives CI without hardware and doubles as the base for our shipped TWAIN DS (D7).
- **`NextScan.EsclSimulator`** — an HTTP server implementing eSCL with the same personality switches (503 storms, missing Location, chunked pages, HTTPS self-signed).
- A **WIA test driver** is harder (needs a driver package); rely on real devices + the two simulators above, and abstract WIA behind the same interface so most logic is covered.

### 18.4 Hardware compatibility lab [MUST for GA]

Minimum device matrix for 1.0 sign-off (buy used where possible; this is a real budget line):

| Class | Devices |
|---|---|
| Photo/film flatbed | Epson V600/V850, Canon 9000F Mk II, Plustek OpticFilm 8200i |
| Office flatbed/MFP | HP OfficeJet/LaserJet MFP, Brother MFC, Canon PIXMA |
| Sheetfed ADF duplex | Fujitsu/Ricoh ScanSnap iX1400/iX1600, Brother ADS, Epson DS-series |
| Network-only | Any eSCL-capable MFP + one WSD-only legacy MFP |
| Awkward legacy | One 32-bit-driver-only USB flatbed (validates §3.1) |

Each device gets a recorded test pass (matrix of source × dpi × colour mode × duplex × transport), logged into the Quirks DB.

### 18.5 Definition of Done (per feature)

Code + unit tests + golden test (if imaging) + docs string + localisation strings + accessibility names + telemetry event (if applicable) + entry in `CHANGELOG.md` + no new analyser warnings + perf within NFR.

---

## 19. INSTALLER & DEPLOYMENT

### 19.1 Packages

- **`NextScanner_Setup.exe`** (Inno Setup 6) — primary consumer installer; per-machine (default) or per-user; silent flags `/VERYSILENT /NORESTART /LOG`.
- **`NextScanner.msi`** (WiX v4) — enterprise; supports `msiexec /qn`, transforms, GPO deployment, `INSTALLDIR`, `LICENSEKEY`, `NOPHOTOSHOP=1`, `NOTELEMETRY=1` properties.
- **Portable ZIP** [SHOULD] — no installer, no Photoshop registration, for locked-down environments.

### 19.2 Install actions

1. Detect OS/arch; block below Win10 21H2 with a clear message.
2. Install app to `%ProgramFiles%\NextScan\` (bin, models, profiles, locales, connectors).
3. Register: Start Menu group, optional Desktop shortcut, `App Paths\nextscanner.exe`, ARP entry with icon/publisher/size/uninstall, file associations for `.nsraw`/`.nsprofile` (opt-in), Explorer context menu (opt-in).
4. Firewall: **do not** add rules by default (we make outbound connections only; mDNS is multicast-out/in on the local subnet — if a rule is genuinely required, ask with a clear explanation).
5. Photoshop discovery + connector injection (§19.3).
6. Optional: install `NextScan.ds` TWAIN source (Studio, opt-in).
7. Optional: download AI models + OCR languages (with an offline-bundle installer variant).

### 19.3 Photoshop auto-registration [MUST]

- Enumerate installs (§14.2). Show the user a **checklist of detected Photoshops** with checkboxes — never silently modify another vendor's application folder without consent.
- Copy `.8ba` (matching bitness) into `Plug-ins\`, `.jsx` into `Presets\Scripts\`, and install the UXP plugin via the CC plugin folder / `.ccx` for v22+.
- If Photoshop is running, warn that a restart is required.
- Write an inventory manifest so the **uninstaller removes exactly what it added** (and nothing else).
- Provide `Tools → Reinstall Photoshop Connectors` in-app for the very common case of "I installed Photoshop after NextScan."

### 19.4 Updates

- Background check (opt-out), delta or full download, signature + hash verified, installed on next launch with release notes. Channels: Stable / Beta. Enterprise can pin a version.
- **Quirks DB and model updates ship independently of the app** (signed data-only updates).

### 19.5 `build_all.ps1` responsibilities

```
build_all.ps1 -Configuration Release -Version 1.0.0 [-Sign] [-Installer] [-Models] [-Test]
  1  Verify toolchain (VS 2022 17.x + C++ workload, .NET 9 SDK, WiX, Inno, signtool/Trusted Signing CLI)
  2  Restore + build native (x64, x86, arm64) → NextScan.Imaging.Native.dll (+static third-party)
  3  Build managed: app (x64), Host64 (x64), Host32 (x86), tools
  4  Build connectors: .8ba (x64+x86), UXP bundle, .jsx, NextScan.ds
  5  Run unit + golden + perf gates
  6  Publish self-contained single-file, ReadyToRun, trimmed-safe (no trimming of reflection-heavy paths)
  7  Sign every binary (not just the installer — a documented common mistake)
  8  Build Inno + WiX packages; sign them too
  9  Generate SBOM (CycloneDX) + checksums + symbol upload
 10  Emit build manifest and (optionally) publish to the update channel
```

---

## 20. BUILD, CI/CD & ENGINEERING OPS

- **CI:** GitHub Actions or Azure Pipelines on `windows-latest` + a self-hosted runner with a GPU (for DirectML/GPU-parity tests) and the device lab (nightly hardware suite).
- **Gates on every PR:** build (x64/x86/arm64) → analysers → unit → golden → perf → fuzz smoke (60 s) → package smoke install in Windows Sandbox.
- **Nightly:** full fuzz, full hardware matrix, install/upgrade/uninstall matrix, localisation pseudo-loc build, SBOM diff.
- **Branching:** trunk-based with short-lived branches; release branches `release/1.0`; semantic versioning; every release tagged with its SBOM.
- **Crash reporting:** minidumps with user consent, symbolised server-side; a crash-free-sessions dashboard is the top health metric.
- **Code signing:** Azure Trusted Signing (Basic ~$10/mo) if the org qualifies (US/CA/EU/UK organisation, 3+ years verifiable history); else OV certificate in Azure Key Vault. Note: **EV no longer grants instant SmartScreen trust**; reputation accrues with downloads either way. Sign **both** the installer and every binary inside it.

---

## 21. PERFORMANCE ENGINEERING

- Budgets are the NFR table (§4.3), encoded as failing tests.
- Key techniques: tile-parallel processing with a work-stealing partitioner; AVX2 kernels with runtime CPUID dispatch (AVX2 → SSE4.2 → scalar); LUT-in-L2 discipline; avoiding a single 48-bit full-page copy (stream strips from the host straight into tiles); GPU compute for preview; overlapping acquire/process/encode stages in the batch pipeline; `ArrayPool`/`MemoryPool` and pinned pooled buffers to avoid LOH churn and GC pauses; `ServerGC` + `ConcurrentGC` tuned in `runtimeconfig`.
- Measure with ETW/PerfView + BenchmarkDotNet; keep a public-to-the-team performance dashboard per commit.

---

## 22. RISK-DRIVEN QUALITY: TOP FAILURE MODES TO DESIGN AGAINST

1. **A vendor driver hangs the app** → solved structurally by §5.1 + §7.7 watchdog.
2. **A device works in the vendor's own software but not ours** → Diagnostics bundle + Quirks DB + transport fallback + "Use vendor UI" escape hatch.
3. **Colour looks different from Photoshop/vendor software** → strict ICC discipline, "colour audit" panel showing exactly which profiles were applied at each step.
4. **A 100-page duplex batch fails at page 87** → journal + resume + never-renumber guarantee.
5. **Huge scan exhausts RAM** → tiling + memory-mapped scratch + a hard pre-flight size estimate with a warning.
6. **The user cannot find their scanner** → four transports, manual IP add, a genuinely helpful empty state, and a "why can't I see my scanner?" troubleshooter.
7. **Photoshop connector silently doesn't appear** → post-install verification step that actually launches a check, plus in-app "Reinstall connectors" and a documented manual procedure.

---

## 23. DOCUMENTATION DELIVERABLES

| Doc | Audience |
|---|---|
| User Guide (HTML + in-app F1, localised) | End users |
| Quick Start (2 pages, illustrated) | End users |
| Scanner Compatibility List (generated from the Quirks DB) | Buyers/support |
| Troubleshooting & FAQ | Support |
| Photoshop Integration Guide (incl. Action recording) | P2 |
| Automation Guide: CLI, JobSpec schema, scripting API | P6 |
| Enterprise Deployment Guide (MSI, GPO, silent, licensing) | P5 |
| Architecture Decision Records + protocol specs | Engineering |
| SECURITY.md, PRIVACY.md, THIRD-PARTY-NOTICES.txt, SBOM | Legal/security |

---

## 24. EXECUTION PLAN — MILESTONES

Sequencing rule: **acquisition and imaging are proven before UI polish**; the simulators come early because everything depends on them.

### M0 — Foundation (2 weeks)
Repo, solution skeleton (§5.3), CI, coding standards, DI/logging/config, `Result`/error-code plumbing, IPC contracts + shared-memory frame protocol, empty host processes, ADR process started, third-party build pipeline (lcms2/turbo/etc. building from source in CI).
**DoD:** `build_all.ps1` produces a signed, runnable empty shell; CI green; a host process can be spawned, pinged, and killed cleanly.

### M1 — TWAIN engine + simulator (5 weeks) ★ highest technical risk, do it first
`NextScan.TwainSimulator` (all personalities) → DSM binding, state machine, message-only window/STA pump, container marshalling, capability negotiator, memory + native transfer, ExtImageInfo, cancel/abort, Host32/Host64 enumeration + merge, quirks scaffolding.
**DoD:** headless CLI scans from the simulator and from **at least 3 real scanners** (one 32-bit-only), memory transfer at 1/8/24/48-bit verified byte-exact, duplex ADF batch of 50 pages with a simulated jam and a clean resume, driver hang recovered without killing the UI process.

### M2 — Imaging core (5 weeks)
`TiledImage`, memory-mapped scratch, colour management via lcms2, curves/LUT engine + AVX2 kernels, histogram, geometry (edge detect/deskew per §3.5/crop/resample), golden-image harness with the initial corpus.
**DoD:** NFR4 met; CPU kernels golden-tested; ICC round-trips validated against reference transforms; deskew policy validated on a 200-page skew corpus with precision/recall reported.

### M3 — Studio UI v1 (5 weeks)
Shell, device panel, settings panel, GPU canvas + pyramid, crop overlay, filmstrip, curves UI, histogram/densitometer, keyboard map, three complexity levels, theming, localisation scaffolding + en/bn.
**DoD:** an end-to-end scan → adjust → save flow is usable by a non-engineer; 60 fps canvas at 4K on a 500 MP image; keyboard-only operation passes; screen-reader smoke test passes.

### M4 — WIA + network transports (4 weeks)
Native WIA 2.0 interop incl. duplex child items and `IWiaTransfer`/`IStream`; eSCL simulator; mDNS/DNS-SD discovery; eSCL client with the 503 retry policy; WSD; manual device add; transport ranking and fallback (§6.2).
**DoD:** the same UI scans identically over TWAIN, WIA and eSCL from at least two physical devices each; a device visible on three transports appears exactly once; fallback demonstrated with a killed driver.

### M5 — Output & document intelligence (5 weeks)
PDF writer (+PDF/A), TIFF/PNG/JPEG writers, OCR tiers 1–2 with our own text-layer builder, MRC pipeline (JBIG2 generic + JPX/JPEG), document clean-up & whitening (§9.5), blank-page/barcode/patch separation, naming templates, destinations.
**DoD:** PDF/A-2b validates in veraPDF; searchable-PDF text selection aligns within 2 px; MRC achieves ≥6× size reduction at equal legibility on a 50-page corpus; symbol-mode JBIG2 is off by default and blocked in PDF/A.

### M6 — Batch, jobs, automation (3 weeks)
Job/session model, journal + crash recovery, profiles/presets, hot folders, CLI, local API, Explorer integration, scanner-button capture.
**DoD:** a 500-page duplex job completes unattended with separation and naming; power-cut recovery loses at most one page; CLI job spec matches the UI output byte-for-byte.

### M7 — AI subsystem (4 weeks)
ONNX/DirectML runtime, model registry + signed download, tiled inference, shadow removal, dewarp, classification/auto-naming, super-resolution; CPU fallback; per-model licence audit.
**DoD:** each model has an on/off/strength control, a measured quality win on a held-out corpus, no crash on a GPU-less machine, and a cleared licence entry in the SBOM.

### M8 — Photoshop connectors (4 weeks)
UXP hybrid plugin, `.8ba` acquire module (x64+x86), `.jsx`, shared-memory handoff, target modes, ActionDescriptor parameter set, Action recording verification.
**DoD:** verified on CS6, CC2019, 2021, 2024 and 2026; ≤900 ms end-to-end for a 400 MB scan; a recorded Action replays a headless scan; 16-bit and ICC survive the trip.

### M9 — Film & colour module (5 weeks, Studio)
Film source/transparency-unit handling, `.nsfilm` profile format + 60 profiles + editor, negative inversion, IR pipeline and inpainting, software SRD fallback, multi-sample + bracketed HDR (post legal gate), IT8 auto-calibration + ICC generation with ΔE reporting.
**DoD:** blind A/B against SilverFast on 20 negatives/slides judged by two experienced users; IT8 profile mean ΔE00 ≤ 2.0 on the reference target; IR removal shows no false positives on a Kodachrome/B&W guard set.

### M10 — Licensing, installer, packaging (3 weeks)
Activation service integration, offline tokens, trial, editions gating; Inno + WiX; Photoshop auto-registration + uninstall inventory; update channel; signing pipeline; SBOM.
**DoD:** clean-VM install/upgrade/uninstall leaves no orphans; silent enterprise install with a licence property works; SmartScreen behaviour documented; every shipped binary is signed.

### M11 — Hardening & compatibility (4 weeks)
Full hardware lab matrix, fuzzing campaign, perf tuning to NFRs, accessibility audit, localisation completion, Quirks DB population, docs.
**DoD:** zero P0/P1 bugs; crash-free sessions ≥ 99.5 % in beta telemetry; all NFRs met on the reference machine; a11y audit passed.

### M12 — Beta → GA (4 weeks)
Closed beta (150 users, weighted toward P1/P3), telemetry review, quirks harvesting, marketing site/compat list generation, support playbook, launch.
**DoD:** beta exit criteria met, rollback plan tested, day-1 patch pipeline verified.

### 24.15 Timeline & staffing

- **Serial critical path:** ~49 weeks. With parallelism (imaging + devices + UI in parallel after M0): **~12–15 months at 3–5 engineers** (1 device/driver, 1 imaging/GPU, 1 UI, 1 output/AI, 0.5 build/QA), plus ~$4–6k of hardware and ~$2k/yr of tooling/signing.
- **For an AI coding agent:** execute milestones in order, but note that M1's simulator and M2's golden harness are prerequisites for meaningful autonomous progress — do not defer them. Human review gates are mandatory at: §3 corrections acknowledged, M1 DoD (real hardware), M5 (PDF/A validity), M9 (legal gate), M10 (signing/licensing).

### 24.16 Post-1.0 backlog (the "many features" phase)

Mobile companion capture; cloud destinations (Drive/OneDrive/Dropbox/SharePoint); email/FTP/network-folder destinations; table extraction v2; handwriting recognition; batch face/photo auto-rotation; book-scanning spine split + auto page-turn detection; printer profiling; TWAIN Direct; macOS port (the core is portable if we keep Windows APIs behind the transport interface — **design for this now, build later**); plug-in SDK for third parties; team/shared profile sync.

---

## 25. RISK REGISTER

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | 32-bit TWAIN sources unusable | Certain if ignored | Fatal | §3.1 surrogate host — designed in from M0 |
| R2 | Vendor driver hangs/crashes | Certain | High | Out-of-process + watchdog + kill/respawn + fallback |
| R3 | Device compatibility long tail (VueScan's 25-year moat) | High | High | Four transports, Quirks DB, device reports, published compat list, fast data-only updates |
| R4 | SilverFast Multi-Exposure patent | Medium | High | §10.5 patent-safe alternatives + legal gate before any DR claim |
| R5 | JBIG2 symbol-mode character substitution | Medium | Severe (reputational) | Generic region default, symbol mode opt-in + warning, blocked in PDF/A |
| R6 | AI model licence contamination | Medium | High | Model registry with licence field, SBOM audit, no unknown-provenance weights |
| R7 | Adobe SDK access / plug-in signing delays | Medium | Medium | Start SDK agreement at M0; `.jsx` connector works with no SDK at all as a hedge |
| R8 | SmartScreen warnings hurt early conversion | High | Medium | Sign everything early, build reputation with beta downloads, publish hashes, document for users |
| R9 | Single-file publish slows cold start (native extraction) | Medium | Low | Measure at M0; fall back to launcher + app folder |
| R10 | Scope creep (the feature list is enormous) | High | High | Editions gating (§4.2), [MUST]/[SHOULD]/[LATER] discipline, milestone DoDs |
| R11 | Colour output doesn't match vendor software → "your app is wrong" | Medium | High | Colour audit panel, IT8 validation, documented rendering intents |
| R12 | Hardware lab cost/availability | Medium | Medium | Buy used; recruit beta testers by device model; simulator-first development |

---

## 26. RESEARCH SOURCES

- TWAIN DSM (32/64-bit, bridging limitation, DSM naming): [twain/twain-dsm on GitHub](https://github.com/twain/twain-dsm), [TWAIN DSM on SourceForge](https://sourceforge.net/projects/twain-dsm/files/), [Vintasoft: TWAIN device manager](https://www.vintasoft.com/docs/vstwain/TWAIN%20device%20manager.html)
- WIA vs TWAIN, automation-layer duplex limitation: [Microsoft KB 2709992 — WIA 2.0 Automation doesn't support duplex](https://support.microsoft.com/en-us/kb/2709992), [Simple Duplex-Capable Document Feeder](https://learn.microsoft.com/en-us/windows-hardware/drivers/image/simple-duplex-capable-document-feeder), [Advanced Duplex-Capable Document Feeder](https://learn.microsoft.com/en-us/windows-hardware/drivers/image/advanced-duplex-capable-document-feeder), [GdPicture: TWAIN vs WIA](https://www.gdpicture.com/blog/twain-vs-wia/)
- eSCL / AirScan: [Mopria eSCL Specification](https://mopria.org/mopria-escl-specification), [sane-airscan](https://github.com/alexpevzner/sane-airscan), [sane-airscan(5) man page](https://www.mankier.com/5/sane-airscan), [Debian eSCL wiki](https://wiki.debian.org/eSCL)
- Photoshop extensibility: [TWAIN scanner plug-in compatibility (Adobe)](https://helpx.adobe.com/photoshop/kb/twain-scanner-plugin.html), [Downloadable plug-ins (Adobe)](https://helpx.adobe.com/photoshop/kb/downloadable-plugins-and-content.html), [Communication with C++ Plugin SDK (PIUXPSuite)](https://developer.adobe.com/photoshop/uxp/2022/ps-reference/media/cpp-pluginsdk), [UXP Changelog](https://developer.adobe.com/photoshop/uxp/2022/uxp-api/changelog3P/), [Photoshop extension technologies overview](https://mapsoft.com/posts/photoshop-extension-technologies.html)
- SilverFast / VueScan: [SilverFast 9](https://www.silverfast.com/silverfast9/), [Multi-Exposure (patent EP 1744278 / US 8,693,808)](https://www.silverfast.com/about-silverfast-why-scanning-basics-of-scanning/why-silverfast/silverfast-feature-highlights/multi-exposure-more-dynamic-range-for-scanning-more-details-less-noise/), [NegaFix](https://www.silverfast.com/about-silverfast-why-scanning-basics-of-scanning/why-silverfast/silverfast-feature-highlights/negafix-converting-negatives-true-color-quick-easy/), [SilverFast (Wikipedia)](https://en.wikipedia.org/wiki/SilverFast), [DPReview: VueScan vs SilverFast](https://www.dpreview.com/reviews/comparison-review-can-vuescan-or-silverfast-archive-your-film-better)
- OCR: [Tesseract OCR](https://tesseractocr.org/), [C# OCR libraries comparison (HackerNoon)](https://hackernoon.com/c-ocr-libraries-the-definitive-net-comparison-for-2026)
- MRC / PDF compression: [Mixed raster content (Wikipedia)](https://en.wikipedia.org/wiki/Mixed_raster_content), [Dynamsoft: MRC compression for PDF](https://www.dynamsoft.com/blog/imaging/mrc-compression-pdf/), [internetarchive/archive-pdf-tools discussion](https://github.com/paperless-ngx/paperless-ngx/discussions/10881)
- AI models / runtime: [DocShadow ONNX](https://github.com/fabio-sim/DocShadow-ONNX-TensorRT), [DocRes: generalist document restoration](https://arxiv.org/pdf/2405.04408), [Document image processing paper collection](https://github.com/ZZZHANG-jx/Recommendations-Document-Image-Processing), [ONNX Runtime + DirectML setup](https://github.com/ChharithOeun/onnxruntime-directml-setup)
- Colour management: [Little-CMS](https://github.com/mm2/Little-CMS), [littlecms.com colour engine](https://littlecms.com/color-engine/), [ArgyllCMS](http://www.argyllcms.com/), [Windows Color System](https://en.wikipedia.org/wiki/Windows_Color_System)
- UI framework & deployment: [Distribute an unpackaged WinUI 3 app](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app), [Windows App SDK self-contained deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- Code signing: [Code signing options for Windows app developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options), [SmartScreen reputation for developers](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation), [Azure Artifact Signing](https://azure.microsoft.com/en-us/products/artifact-signing), [Authenticode in 2025 (textslashplain)](https://textslashplain.com/2025/03/12/authenticode-in-2025-azure-trusted-signing/)
- Document-capture competitors: [Epson ScanSmart / ScanSnap Home / ABBYY feature comparisons](https://thedigitalprojectmanager.com/tools/best-document-scanning-software/)

---

# APPENDICES

## Appendix A — TWAIN triplet & capability cheat sheet

**Session triplets (in order):**

| # | DG | DAT | MSG | State after |
|---|---|---|---|---|
| 1 | CONTROL | PARENT | OPENDSM | 3 |
| 2 | CONTROL | ENTRYPOINT | GET | 3 (DSM 2.x memory fns) |
| 3 | CONTROL | IDENTITY | GETFIRST / GETNEXT / GETDEFAULT / USERSELECT | 3 |
| 4 | CONTROL | IDENTITY | OPENDS | 4 |
| 5 | CONTROL | CAPABILITY | GET / GETCURRENT / GETDEFAULT / SET / RESET / QUERYSUPPORT | 4 |
| 6 | CONTROL | USERINTERFACE | ENABLEDS / ENABLEDSUIONLY | 5 |
| 7 | CONTROL | EVENT | PROCESSEVENT (loop) | 5→6 on MSG_XFERREADY |
| 8 | IMAGE | IMAGEINFO | GET | 6 |
| 9 | IMAGE | IMAGELAYOUT | GET / SET | 6 |
| 10 | CONTROL | SETUPMEMXFER | GET | 6 |
| 11 | IMAGE | IMAGEMEMXFER / IMAGENATIVEXFER / IMAGEFILEXFER | GET (loop) | 7 |
| 12 | IMAGE | EXTIMAGEINFO | GET | 7 |
| 13 | CONTROL | PENDINGXFERS | ENDXFER / RESET / STOPFEEDER | 6/5 |
| 14 | CONTROL | USERINTERFACE | DISABLEDS | 4 |
| 15 | CONTROL | IDENTITY | CLOSEDS | 3 |
| 16 | CONTROL | PARENT | CLOSEDSM | 2 |

**Return codes to handle:** `TWRC_SUCCESS`, `TWRC_FAILURE` (+ fetch `TW_STATUS`), `TWRC_CHECKSTATUS`, `TWRC_CANCEL`, `TWRC_DSEVENT`, `TWRC_NOTDSEVENT`, `TWRC_XFERDONE`, `TWRC_ENDOFLIST`.
**Condition codes:** `TWCC_SUCCESS`, `BUMMER`, `LOWMEMORY`, `NODS`, `MAXCONNECTIONS`, `OPERATIONERROR`, `BADCAP`, `BADPROTOCOL`, `BADVALUE`, `SEQERROR`, `BADDEST`, `CAPUNSUPPORTED`, `CAPBADOPERATION`, `CAPSEQERROR`, `DENIED`, `FILEEXISTS`, `FILENOTFOUND`, `NOTEMPTY`, `PAPERJAM`, `PAPERDOUBLEFEED`, `FILEWRITEERROR`, `CHECKDEVICEONLINE`, `INTERLOCK`, `DAMAGEDCORNER`, `FOCUSERROR`, `DOCTOOLIGHT`, `DOCTOODARK`, `NOMEDIA`.

**Capability groups to implement** — Negotiation (`CAP_XFERCOUNT`, `CAP_SUPPORTEDCAPS`, `CAP_UICONTROLLABLE`, `CAP_INDICATORS`, `CAP_DEVICEONLINE`), Feeder (`CAP_FEEDERENABLED`, `CAP_FEEDERLOADED`, `CAP_AUTOFEED`, `CAP_AUTOSCAN`, `CAP_CLEARPAGE`, `CAP_FEEDPAGE`, `CAP_REWINDPAGE`, `CAP_PAPERDETECTABLE`, `CAP_FEEDERALIGNMENT`, `CAP_FEEDERORDER`, `CAP_MAXBATCHBUFFERS`), Duplex (`CAP_DUPLEX`, `CAP_DUPLEXENABLED`), Image (`ICAP_PIXELTYPE`, `ICAP_BITDEPTH`, `ICAP_BITORDER`, `ICAP_PLANARCHUNKY`, `ICAP_PIXELFLAVOR`, `ICAP_XRESOLUTION`, `ICAP_YRESOLUTION`, `ICAP_XSCALING`, `ICAP_YSCALING`, `ICAP_UNITS`, `ICAP_SUPPORTEDSIZES`, `ICAP_PHYSICALWIDTH`, `ICAP_PHYSICALHEIGHT`, `ICAP_FRAMES`, `ICAP_ORIENTATION`, `ICAP_ROTATION`, `ICAP_IMAGEFILEFORMAT`, `ICAP_COMPRESSION`, `ICAP_JPEGQUALITY`), Enhancement (`ICAP_BRIGHTNESS`, `ICAP_CONTRAST`, `ICAP_GAMMA`, `ICAP_HIGHLIGHT`, `ICAP_SHADOW`, `ICAP_THRESHOLD`, `ICAP_AUTOBRIGHT`, `ICAP_AUTOMATICDESKEW`, `ICAP_AUTOMATICBORDERDETECTION`, `ICAP_AUTOMATICROTATE`, `ICAP_AUTODISCARDBLANKPAGES`, `ICAP_FLIPROTATION`, `ICAP_MIRROR`, `ICAP_NOISEFILTER`, `ICAP_OVERSCAN`, `ICAP_BARCODEDETECTIONENABLED`, `ICAP_PATCHCODEDETECTIONENABLED`), Film/transparency (`ICAP_LIGHTPATH`, `ICAP_LIGHTSOURCE`, `ICAP_FILMTYPE`, `ICAP_EXPOSURETIME`), Colour (`ICAP_ICCPROFILE`, `ICAP_COLORMANAGEMENTENABLED`).

**ExtImageInfo TWEI values to read:** `TWEI_BARCODETEXT`, `TWEI_BARCODETYPE`, `TWEI_PATCHCODE`, `TWEI_DESKEWSTATUS`, `TWEI_SKEWORIGINALANGLE`, `TWEI_PAGESIDE`, `TWEI_PAPERCOUNT`, `TWEI_ENDORSEDTEXT`, `TWEI_MAGDATA`, `TWEI_BOOKNAME`, `TWEI_DOCUMENTNUMBER`, `TWEI_PAGENUMBER`, `TWEI_FRAME`, `TWEI_PIXELFLAVOR`.

## Appendix B — WIA 2.0 property reference (implementation checklist)

Device: `WIA_DIP_DEV_ID`, `WIA_DIP_VEND_DESC`, `WIA_DIP_DEV_DESC`, `WIA_DIP_DEV_TYPE`, `WIA_DIP_PORT_NAME`, `WIA_DIP_DEV_NAME`, `WIA_DIP_UI_CLSID`, `WIA_DIP_HARDWARE_CONFIG`, `WIA_DIP_BAUDRATE`.
Scanner device: `WIA_DPS_HORIZONTAL_BED_SIZE`, `WIA_DPS_VERTICAL_BED_SIZE`, `WIA_DPS_DOCUMENT_HANDLING_CAPABILITIES`, `WIA_DPS_DOCUMENT_HANDLING_STATUS`, `WIA_DPS_DOCUMENT_HANDLING_SELECT`, `WIA_DPS_PAGES`, `WIA_DPS_SHEET_FEEDER_REGISTRATION`, `WIA_DPS_MAX_SCAN_TIME`, `WIA_DPS_OPTICAL_XRES/YRES`, `WIA_DPS_SCAN_AHEAD_PAGES`.
Item: `WIA_IPA_ITEM_NAME`, `WIA_IPA_FULL_ITEM_NAME`, `WIA_IPA_ITEM_CATEGORY` (FLATBED/FEEDER/FILM/FRONT/BACK), `WIA_IPA_ITEM_FLAGS`, `WIA_IPA_DATATYPE`, `WIA_IPA_DEPTH`, `WIA_IPA_CHANNELS_PER_PIXEL`, `WIA_IPA_BITS_PER_CHANNEL`, `WIA_IPA_PIXELS_PER_LINE`, `WIA_IPA_NUMBER_OF_LINES`, `WIA_IPA_BYTES_PER_LINE`, `WIA_IPA_FORMAT`, `WIA_IPA_TYMED`, `WIA_IPA_COMPRESSION`, `WIA_IPA_ICM_PROFILE_NAME`, `WIA_IPA_BUFFER_SIZE`, `WIA_IPA_ITEM_SIZE`.
Scan params: `WIA_IPS_XRES`, `WIA_IPS_YRES`, `WIA_IPS_XPOS`, `WIA_IPS_YPOS`, `WIA_IPS_XEXTENT`, `WIA_IPS_YEXTENT`, `WIA_IPS_BRIGHTNESS`, `WIA_IPS_CONTRAST`, `WIA_IPS_CUR_INTENT`, `WIA_IPS_ROTATION`, `WIA_IPS_MIRROR`, `WIA_IPS_THRESHOLD`, `WIA_IPS_PHOTOMETRIC_INTERP`, `WIA_IPS_PAGES`, `WIA_IPS_PAGE_SIZE`, `WIA_IPS_PAGE_WIDTH/HEIGHT`, `WIA_IPS_PREVIEW`, `WIA_IPS_SEGMENTATION`, `WIA_IPS_SHEET_FEEDER_REGISTRATION`, `WIA_IPS_AUTO_DESKEW`, `WIA_IPS_FILM_SCAN_MODE`, `WIA_IPS_LAMP`, `WIA_IPS_WARM_UP_TIME_*`, `WIA_IPS_TRANSFER_CAPABILITIES`.
Errors to map: `WIA_ERROR_GENERAL_ERROR`, `_PAPER_JAM`, `_PAPER_EMPTY`, `_PAPER_PROBLEM`, `_OFFLINE`, `_BUSY`, `_WARMING_UP`, `_USER_INTERVENTION`, `_ITEM_DELETED`, `_DEVICE_COMMUNICATION`, `_INVALID_COMMAND`, `_INCORRECT_HARDWARE_SETTING`, `_DEVICE_LOCKED`, `_EXCEPTION_IN_DRIVER`, `_INVALID_DRIVER_RESPONSE`, `_COVER_OPEN`, `_LAMP_OFF`, `_DESTINATION`, `_NETWORK_RESERVATION_FAILED`.

## Appendix C — eSCL request/response shapes

**ScannerCapabilities (excerpt to parse):**
```xml
<scan:ScannerCapabilities xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03"
                          xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm">
  <pwg:MakeAndModel>…</pwg:MakeAndModel>
  <pwg:SerialNumber>…</pwg:SerialNumber>
  <scan:Platen><scan:PlatenInputCaps>
    <scan:MinWidth/><scan:MaxWidth/><scan:MinHeight/><scan:MaxHeight/>
    <scan:MaxOpticalXResolution/><scan:RiskyLeftMargin/>
    <scan:SettingProfiles><scan:SettingProfile>
      <scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode>
                       <scan:ColorMode>Grayscale8</scan:ColorMode>
                       <scan:ColorMode>BlackAndWhite1</scan:ColorMode></scan:ColorModes>
      <scan:DocumentFormats><pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>…</scan:DocumentFormats>
      <scan:SupportedResolutions><scan:DiscreteResolutions>…</scan:DiscreteResolutions></scan:SupportedResolutions>
    </scan:SettingProfile></scan:SettingProfiles>
  </scan:PlatenInputCaps></scan:Platen>
  <scan:Adf><scan:AdfSimplexInputCaps>…</scan:AdfSimplexInputCaps>
            <scan:AdfDuplexInputCaps>…</scan:AdfDuplexInputCaps>
            <scan:AdfOptions><scan:AdfOption>DetectPaperLoaded</scan:AdfOption></scan:AdfOptions></scan:Adf>
</scan:ScannerCapabilities>
```
**ScanJobs request:** `scan:ScanSettings` with `pwg:Version`, `scan:Intent`, `pwg:ScanRegions/pwg:ScanRegion{XOffset,YOffset,Width,Height,ContentRegionUnits=escl:ThreeHundredthsOfInch}`, `pwg:InputSource{Platen|Feeder}`, `scan:ColorMode`, `scan:XResolution/YResolution`, `pwg:DocumentFormat`/`scan:DocumentFormatExt`, `scan:Duplex`, `scan:Brightness/Contrast/Threshold`, `scan:CompressionFactor`, `scan:BlankPageDetection`.
**Flow:** `201 Created` + `Location` → `GET {Location}/NextDocument` (repeat) → `404/410` = done. `503` → retry (30× for NextDocument, 10× otherwise, 1000 ms apart). Cancel = `DELETE {Location}`.

## Appendix D — Shared-memory frame header (v1)

```
offset size field
  0     4   magic  'N''S''F''1'
  4     2   headerVersion (=1)
  6     2   headerSize (=128)
  8     4   flags (bit0 preview, bit1 hasIR, bit2 bottomUp, bit3 planar, bit4 premultiplied)
 12     4   width
 16     4   height
 20     4   stride (bytes)
 24     2   channels          (1,3,4)
 26     2   bitsPerChannel    (1,8,16)
 28     4   pixelLayout enum  (Gray|RGB|BGR|RGBA|BGRA|CMYK|IR)
 32     4   xResolutionDpi (fixed 16.16)
 36     4   yResolutionDpi (fixed 16.16)
 40     4   pageIndex
 44     2   side (0=front,1=back)
 46     2   reserved
 48     4   iccOffset  (0 = none)
 52     4   iccLength
 56     4   irPlaneOffset (0 = none)
 60     4   irPlaneLength
 64     8   deviceTimestampUtcTicks
 72     4   transport enum
 76     4   pixelDataOffset
 80     8   pixelDataLength
 88    40   reserved (zero)
```
All little-endian. The consumer must validate every field against the mapping size before touching pixels.

## Appendix E — Photoshop connector file inventory

```
%ProgramFiles%\NextScan\connectors\
  photoshop\x64\NextScanner.8ba
  photoshop\x86\NextScanner.8ba
  photoshop\jsx\Next_Scanner.jsx
  photoshop\uxp\NextScanner.ccx          (and an unpacked dev folder)
  photoshop\install-manifest.json         ← exact list of files written per PS install (for uninstall)
```

## Appendix F — JobSpec JSON schema (abridged)

```json
{
  "$schema": "https://nextscan.app/schemas/jobspec-1.json",
  "device":   { "match": "Epson Perfection V850", "transport": "auto|twain|wia|escl|wsd" },
  "scan":     { "source":"flatbed|adf|adfDuplex|film", "dpi":600, "colorMode":"color24|color48|gray8|gray16|bw1",
                "pageSize":"A4|Letter|Legal|IDCard|Custom", "custom":{ "wMm":210,"hMm":297 },
                "autoCrop":true, "autoDeskew":true, "multiRegion":false, "pages":0, "brightness":0, "contrast":0 },
  "process":  { "profile":"Document Clean 300", "overrides": { "whitening":0.6, "sharpen":{"amount":80,"radius":0.8},
                "ai":{ "shadowRemoval":true, "dewarp":false, "denoise":0.3 },
                "curves":{ "preset":"Neutral" } } },
  "separate": { "mode":"none|blank|barcode|patch|pageCount", "barcodeTypes":["code128","qr"], "pageCount":2,
                "removeSeparator":true },
  "output":   [ { "format":"pdf", "pdfa":"2b", "ocr":{ "enabled":true, "languages":["eng","ben"] },
                  "mrc":{ "enabled":true, "preset":"balanced" },
                  "path":"C:\\Scans\\{date}\\{docType}_{counter:0000}.pdf" },
                { "format":"tiff", "compression":"lzw", "bitDepth":16, "path":"…" } ],
  "destinations": [ { "type":"folder", "path":"…" }, { "type":"photoshop", "target":"newDoc" } ],
  "onError":  "stop|skipPage|continue"
}
```

## Appendix G — Error code ranges

| Range | Area |
|---|---|
| 1000–1099 | Host/IPC (spawn failed, pipe broken, protocol violation, timeout) |
| 1100–1299 | TWAIN (mapped from `TWCC_*` + our own state/marshalling errors) |
| 1300–1499 | WIA (mapped from `WIA_ERROR_*` + HRESULTs) |
| 1500–1699 | Network (discovery, HTTP status, eSCL job, TLS, WSD) |
| 1700–1899 | Imaging (allocation, unsupported layout, GPU device lost, ICC failure) |
| 1900–1999 | AI (model missing/hash mismatch, EP unavailable, OOM) |
| 2000–2199 | Output (encoder, PDF/A violation, OCR, disk full, path invalid) |
| 2200–2299 | Licensing/activation |
| 2300–2399 | Automation/CLI/scripting |

CLI exit codes: `0` success, `1` generic, `2` bad arguments, `3` no device, `4` device error, `5` licence, `6` output/disk, `7` cancelled.

## Appendix H — Glossary

**ADF** automatic document feeder · **AACO** adaptive contrast optimisation · **CAP/ICAP** TWAIN capability · **DS** TWAIN data source · **DSM** data source manager · **eSCL** driverless network scan protocol (AirScan/Mopria) · **EP** ONNX execution provider · **IT8** ISO 12641 colour reference target · **iSRD** infrared-based dust/scratch removal · **MRC** mixed raster content · **PCS** profile connection space · **`.nsraw`** our archival container · **Quirks DB** per-model behaviour database · **TWSX** TWAIN transfer mechanism · **UXP** Adobe's Unified Extensibility Platform · **WIA** Windows Image Acquisition · **WSD** Web Services on Devices.

---

*End of Master Plan v1.0. Change control: any deviation from §3 or §5.1 requires a new ADR and product-owner sign-off.*
