# ADR-0001: Host control channel is NDJSON on stdout, not a named pipe

Status: accepted
Date: 2026-08-20
Plan reference: MASTER_PLAN §5.1, §7.1

## Context

The master plan specifies a named-pipe JSON-RPC 2.0 control channel between the
UI process and the device host processes, with pixel data in shared memory.

The architectural requirement that actually matters is **isolation**: vendor
scanner drivers must never be loaded into the UI process, and a driver that hangs
or faults must not take the UI with it. Bitness separation (§3.1) and crash
isolation both follow from process separation, not from the choice of transport.

## Decision

Pixels go through shared memory exactly as planned. The **control channel is
newline-delimited JSON written to the host's stdout**, with the parent reading it
asynchronously.

## Rationale

- Identical isolation guarantees: it is the same separate process either way.
- The parent must already supervise process lifetime (spawn, timeout, kill) for
  the watchdog in §7.7, and `Process` gives that for free.
- No pipe name generation, no connection handshake, no listener lifetime, no
  orphaned-pipe cleanup.
- Trivially debuggable: `NextScan.Host32.exe probe --verbose` prints the entire
  protocol to a terminal, which is how every bug in section 2 of STATUS.md was
  actually found.
- The protocol is currently request/response per process invocation, so the
  bidirectional capability of a pipe would be unused.

## Consequences

- Commands are one-shot: the host starts, does one job, exits. Startup cost is
  roughly 100 ms, which is negligible next to scanner warm-up.
- Mid-scan commands from the UI to the host (pause, change settings) are **not**
  possible. Cancellation works by killing the host, which is acceptable and is
  what we would do for an unresponsive driver anyway.
- Anything the host writes to stdout that is not valid JSON corrupts the channel.
  Hosts must never `Console.WriteLine` directly; everything goes through `Emit`.
- stdout and stderr **must** both be drained asynchronously. Reading one to
  completion before the other deadlocks once a chatty driver fills the 4 KB pipe
  buffer on the unread channel — a bug this codebase has already hit once in the
  NAPS2 path.

## Revisit if

- The host becomes long-lived (e.g. to keep a data source open across previews to
  avoid re-warming the lamp), **or**
- mid-scan bidirectional control is needed, **or**
- per-invocation startup cost becomes measurable in a batch workflow.

At that point switch to the planned named pipe; `HostProgram.Emit` and
`DeviceBroker.RunHost` are the only two places that would change.
