# ADR-0002: The TWAIN simulator is a fake DSM, not an installable data source

Status: accepted
Date: 2026-08-22
Plan reference: MASTER_PLAN §18.3, HANDOFF §8.2 (this decision overrides both)

## Context

Plan §18.3 and HANDOFF §8.2 call for `NextScan.TwainSimulator` as a real
installable TWAIN data source (`.ds`) registered under `C:\Windows\twain_32\`,
with switchable personalities, to give the project a regression net.

Everything verified so far was verified by hand against one physical scanner.
There is no automated test coverage, and the bug list in STATUS.md §2 is exactly
the class of interop bug that silently returns.

## Decision

Build the simulator as a **fake Data Source Manager**: a native C++ DLL named
`TWAINDSM.DLL` that exports `DSM_Entry` and implements both the DSM role and a
built-in data source with switchable personalities. It is loaded through the
existing app-local candidate path — no installation.

Three parts:

1. **Deterministic selection.** `TwainSession.FindDsmCandidates()` gains an
   environment-variable override, `NEXTSCAN_TWAIN_DSM`: when set, that path is
   the only candidate. This is checked *before* the System32/app-local/legacy
   candidates, so a test harness can pin the fake DSM explicitly instead of
   depending on the accidental ordering of the candidate list. Production
   behaviour is unchanged: the variable is unset on user machines.

2. **The simulator DLL.** Native C++ (MSVC 14.50, available on this machine),
   built for both x86 and x64 — `DSM_Entry` is an exported C function and
   unmanaged exports from C# are fragile. Personality is selected by
   environment variable (e.g. `NEXTSCAN_SIM_PERSONALITY`):
   - well-behaved (baseline)
   - odd image width (24-bit stride padding — plan §18.2 regression list)
   - 1-bit / 8-bit grey / 16-bit-per-channel transfers
   - bottom-up and top-down row order
   - refuses `ShowUI=FALSE`
   - accepts `MSG_SET` but reports a different value (read-back verification)
   - hangs on `MSG_ENABLEDS` (watchdog test)
   - crashes in state 7 (host crash-isolation test)
   - duplex with reversed backs

   The hang and crash personalities are the reason this work matters most:
   they exercise the architecture's headline claims (crash/hang isolation,
   HANDOFF §3) on demand, which real hardware cannot produce.

3. **Golden-image harness.** The simulator emits deterministic synthetic frames
   (colour bars, gradient, 1×1 checkerboard, fixed DPI); the harness compares
   output byte-exact against committed reference PNGs. Checkerboard + odd width
   are the stride/padding bug detectors named in plan §18.2.

## Rationale

An installable `.ds` under `C:\Windows\twain_32\` requires admin rights,
pollutes the machine-wide TWAIN source list (the user's real workflow and
other applications would see fake devices), and is awkward to run in CI.
The fake DSM needs no admin rights, no registration, leaves the machine
clean, and is deterministic.

`FindDsmCandidates()` already probes an app-local `TWAINDSM.DLL` as its second
candidate, so the engine talks to the simulator through the exact production
code path: DSM binding, state machine, container marshalling, capability
negotiation, and the transfer loop are all exercised for real.

## Consequences

- What is tested: all of our managed TWAIN code, including failure paths real
  hardware cannot produce. What is **not** tested: the genuine Microsoft DSM's
  own quirks, and real vendor drivers. Hardware verification remains mandatory
  (HANDOFF §7.5); this is a regression net, not a substitute.
- Production code gains one small, guarded change (the env-var check); it must
  not alter any default behaviour and gets a comment in the house style.
- The shipped NextScan TWAIN data source (plan §14.5, D7) will eventually still
  need to be a genuine installable `.ds`; the simulator's personality engine is
  the intended base for it.

## Revisit if

- Tests need to exercise the genuine DSM ↔ data-source interaction path
  through the real `twain_32.dll`, or
- work begins on the shipped `NextScan.ds` product feature.
