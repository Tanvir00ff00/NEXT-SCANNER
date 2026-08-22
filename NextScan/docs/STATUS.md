# NextScan Studio — Build Status

Last updated: 2026-08-20
Plan of record: [`NEXTSCAN_STUDIO_MASTER_PLAN.md`](../../NEXTSCAN_STUDIO_MASTER_PLAN.md)

This file records what is **actually built and verified on hardware**, versus what
is still only planned. It is deliberately conservative: if something is not listed
as verified below, assume it does not work yet.

---

## 1. What is done and verified

Verified on this machine against a **Canon CanoScan LiDE 400** and a **Canon Color
Network ScanGear 2**.

| Area | Status | Evidence |
|---|---|---|
| Device abstraction contracts (§6.1) | ✅ built | `src/Core/Contracts.cs` |
| Dependency-free JSON (§3.2) | ✅ built | `src/Core/Json.cs` |
| Shared DIB/BMP decoder (§7.2, §18.2) | ✅ built | `src/Core/DibDecoder.cs` |
| Native TWAIN 2.x engine (§7.2) | ✅ **verified** | enumerates + scans through `twain_32.dll` |
| TWAIN state machine, msg pump, caps (§7.2) | ✅ **verified** | caps read, region set, memory transfer |
| Native WIA 2.0 engine, no `wiaaut.dll` (§7.3) | ✅ **verified** | enumerates + scans via `IWiaTransfer` |
| Out-of-process Host32 / Host64 (§3.1, §5.1) | ✅ **verified** | Host32 reaches a 32-bit-only driver Host64 cannot see |
| Shared-memory frame transport (§7.1, App. D) | ✅ **verified** | 128-byte header + pixels, read back by broker |
| Device broker, dedup, transport ranking (§6.2) | ✅ **verified** | one card per scanner, TWAIN preferred |
| Crash/hang isolation + watchdog (§7.7) | ✅ built | host kill/respawn, non-zero exit surfaced |
| `nsprobe` CLI (§12.3, §13.8) | ✅ **verified** | `list` / `caps` / `scan` |
| Integration into the existing Photoshop helper | ✅ **verified** | `scan engine: native (NextScan)`, NAPS2 unused |
| TWAIN simulator skeleton, well-behaved personality (§18.3, ADR-0002) | ✅ **verified** | `nsprobe` enumerates + scans it end-to-end on both hosts |

**The headline result: NAPS2 is no longer required.** The existing
`scanhelper.exe` UI now acquires images through the native engine, and falls back
to NAPS2 only if the native path fails and NAPS2 happens to be installed.

### Verified measurements

```
nsprobe scan "LiDE" --dpi 150 --region 0,0,3,2   ->  450x300  @150dpi  (exactly 3.00 x 2.00 in)
scanhelper -nodialog (crop_h=0.2567)             ->  2481x900  @300dpi  (exactly 8.27 x 3.00 in)
TWAIN scan wall time, 150 dpi preview region     ->  4.0 s
WIA   scan wall time, 150 dpi preview region     ->  2.7 s
simulator scan, same region                      ->  450x300, 0.2 s, all 8 colour bars byte-exact
```

### TWAIN simulator (ADR-0002)

A fake `TWAINDSM.DLL` (`sim\TwainSim.cpp`, built x86 + x64 to `bin\sim\`), loaded
by setting `NEXTSCAN_TWAIN_DSM` to its path — no admin rights, nothing written
to the machine's TWAIN source list. Verified working against the full managed
stack: enumeration, capability negotiation (ENUM/ONEVALUE/RANGE containers,
FIX32), region clamping, memory-strip transfer, ENDXFER/RESET teardown, on both
Host32 and Host64. Image content is a deterministic 8-bar colour pattern
(`NEXTSCAN_SIM_IMAGE` = bars | gradient | checker | flat); all eight sampled
bar colours came back byte-exact, which also proves R/B order through our own
memory-transfer path (hardware-side confirmation still pending — see §3).

**Personalities, verified** (`NEXTSCAN_SIM_PERSONALITY`): oddwidth (451px at a
450px request), bw1/gray8/gray16/color48 forced modes (driver "accepts"
negotiation then delivers its own fixed mode — read-back catches it), bottomup
and topdown (memory transfer refused → native-DIB fallback, both row orders),
refusesui (`TwainEnableFailed` with condition code), setlies (SET succeeds,
device stays at 150 dpi — detected, image consistent with the lie), crash7
(host dies 0xC0000005 in state 7 → broker reports `HostCrashed`, caller
survives), duplex (feeder + 2 pages, back side rotated 180°, asserted both
against references and the rot180 relation).

**Golden harness, verified** (`tests\run_golden.ps1`): 16 cases — pixel-exact
image comparison (decoded 24bpp rows + dimensions + DPI) against committed
references in `tests\golden\`, plus behavioural assertions. Full run ≈ 10 s,
exit code non-zero on any failure. `-Generate` regenerates references;
`-WithHang` adds the slow watchdog proof.

**Bug the simulator found before any user could:** `DeviceBroker.RunHost`'s
crash-detection branch was unreachable — a crashed host emits no `result`
line, so the sentinel `HostPipeBroken` result (Ok=false) kept the branch's
`Result.Ok` condition false forever. Crashes were reported as pipe breaks with
no exit code. Fixed by tracking whether a `result` line was seen at all; the
`crash7_isolation` harness case is its regression test.

**Hang watchdog, proven:** with the `hang` personality the host never returns
from `MSG_ENABLEDS`; the broker killed it at the 600 s scan timeout, the
caller survived and reported `HostTimeout` with a remedy (exit 1, no orphan
host process left behind). The harness's `-WithHang` switch asserts the same
output but re-runs the full 10-minute wait, so it is excluded from the default
fast pass.

### Why the 32-bit host is not optional

On this machine:

```
C:\Windows\twaindsm.dll   does not exist
C:\Windows\twain_64\      does not exist
C:\Windows\twain_32\      SG20, ScanGearIR, wiatwain.ds     <- the real Canon driver
```

`NextScan.Host64.exe probe` finds **zero** TWAIN devices.
`NextScan.Host32.exe probe` finds the CanoScan LiDE 400.

This is plan §3.1 confirmed in practice, not in theory.

---

## 2. Bugs found and fixed during bring-up

Recorded because each one is a trap the next person will otherwise re-enter.

1. **`PRSPEC_PROPID` is 1, not 0.** Setting `PROPSPEC.ulKind = 0` selects
   `PRSPEC_LPWSTR`, so WIA dereferences the property id as a string pointer and
   the process dies with an access violation inside the WIA service. Symptom is
   an AV in `ReadMultiple` that survives every plausible marshalling fix, because
   the marshalling was never wrong.
2. **`TYMED_FILE` is 2, not 1.** 1 is `TYMED_HGLOBAL`. Writing 1 is rejected and
   the scan then only succeeds if the driver's default was already correct.
3. **`TYMED_CALLBACK` is the WIA 1.0 band mechanism.** `IWiaTransfer::Download`
   wants `TYMED_FILE`; asking for callback makes `Download` fail `E_INVALIDARG`
   even though every property write returned `S_OK`.
4. **`WIA_IPA_TYMED` must be written before `WIA_IPA_FORMAT`**, since the legal
   format set is scoped to the medium.
5. **`ICAP_UNITS` must be set before reading any dimension.** Without it the
   LiDE 400 reported its bed as `0.16 x 0` inches.
6. **`ICAP_BITDEPTH` is bits-per-channel on some drivers.** Canon rejects 24 for
   RGB and wants 8. We now try the spec value, then per-channel, then give up and
   keep the driver default.
7. **Advertising a capability is not the same as having the hardware.** The
   LiDE 400 exposes `CAP_FEEDERENABLED` and has no feeder. We now require that
   the device actually *accepts* being switched into the mode.
8. **`TW_IDENTITY` and friends need `Pack = 2`** (`#pragma pack(2)` in twain.h).
   Wrong packing does not fail loudly, it just returns shifted garbage.

---

## 3. Known issues and limitations

| Issue | Impact | Notes |
|---|---|---|
| `ICAP_BITDEPTH` enumeration is garbage on the LiDE 400 (`0,1,2,3,5,7,10,52,53,13` for both RGB and grey) | 48-bit capture cannot be detected on this device | Verified as a **driver** fault, not ours: the container decodes as `ENUM/uint16` and `ICAP_PIXELTYPE` decodes correctly with identical code. We correctly do **not** claim 48-bit support. Re-test against a second vendor before changing the decoder. |
| LiDE 400 accepts `ICAP_LIGHTPATH = TRANSMISSIVE` despite having no film unit | UI would offer a Film source that does nothing | Needs a Quirks DB entry (§7.6), which is not built yet |
| 48-bit / 16-bit-per-channel capture | Untested | Code paths exist in the transfer loop; no device here to exercise them |
| ADF / duplex | Untested | No feeder on the available hardware. WIA `WIA_TRANSFER_ACQUIRE_CHILDREN` and TWAIN `CAP_XFERCOUNT = -1` are wired but unproven |
| Colour channel order | Not independently verified | Only blank white pages were scanned; R/B swap would be invisible. Scan something with saturated colour to confirm |
| `MemoryMappedFile` frames are never explicitly released by the host | Host is short-lived so the OS reclaims them | Fine today; must be fixed if the host ever becomes long-lived |

---

## 4. Not started

Everything else in the master plan. Most notably:

- **eSCL / mDNS / WSD network transports** (§7.4) — no code yet
- **Imaging pipeline** (§9): the existing `scanhelper.cs` code (curves, detection,
  deskew, whitening) is still doing this work and has **not** been ported to the
  new `RawImage` model
- **AI subsystem** (§10) — still the Python `ai_doc_cascade.py` side-process
- **PDF / OCR / MRC output** (§11)
- **Film module, IT8, ICC colour management** (§10.3–10.6)
- **Photoshop connectors** (§14) — still the legacy `.jsx`
- **Batch/jobs, licensing, installer, quirks DB** (§12, §15, §19, §7.6)
- **Tests**: no unit, golden-image or simulator coverage exists yet. Plan §18.3
  calls for the TWAIN and eSCL simulators to be built early; they are not built,
  so all verification so far is manual and hardware-dependent.

---

## 5. How to build and run

```powershell
# engine + hosts + CLI
cd C:\PS_Fix\NextScan
.\build.ps1

# the Photoshop helper, against the native engine
cd C:\PS_Fix
.\build_scan_native.ps1
```

```powershell
# diagnostics
.\NextScan\bin\nsprobe.exe list
.\NextScan\bin\nsprobe.exe caps "LiDE"
.\NextScan\bin\nsprobe.exe scan "LiDE" --dpi 300 --out page.png
```

To go back to the old behaviour without rebuilding, set `engine=naps2` in
`C:\PS_Fix\scan.ini`. `build_scan.ps1` (the original NAPS2-only build) is
untouched, and `backup_original\scanhelper.cs.pre-native` is the pre-integration source.

---

## 6. Suggested next steps

1. **Build the TWAIN simulator (plan §18.3).** Everything above was verified by
   hand against one scanner. Without a simulator there is no regression net, and
   the bug list in section 2 is exactly the kind that silently comes back.
2. **Verify colour channel order** with a colour target.
3. **Port the imaging pipeline** from `scanhelper.cs` onto `RawImage` so 16-bit
   data survives (it currently round-trips through `System.Drawing.Bitmap`, which
   caps everything at 8 bits per channel).
4. **eSCL transport** — the biggest coverage win per unit of work, and it needs no
   vendor driver at all.
5. **Quirks DB** — there are already two real entries to seed it with (LiDE 400
   bit depth, LiDE 400 phantom film source).
