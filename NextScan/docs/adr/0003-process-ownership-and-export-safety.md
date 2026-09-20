# ADR-0003: Host ownership and complete export commits

Date: 2026-09-07
Status: accepted

The broker used to kill every host PID not present in its own in-process list.
A Studio instance and a CLI test have different lists, so either could terminate
the other's active scan. A process name is not evidence of orphanhood.

Only spawned child processes may be reaped by this broker. Remove the foreign
host sweep; clean up owned children on failure/exit. A future Windows Job Object
should guarantee cleanup after abrupt parent death. This change intentionally
trades speculative orphan cleanup for preservation of live acquisition jobs.
Builds similarly refuse to overwrite active binaries rather than killing users'
sessions. Recovery retries are bounded to one and disabled for simulator runs.

Exporters write a temporary sibling file and replace the destination only after
encoding succeeds. Partial scan batches remain available when later pages fail.
These decisions support the master plan's isolation and data-preservation goals.

HTTPS no longer accepts arbitrary certificates. It uses Windows trust validation;
self-signed scanners require a trusted certificate until explicit device pinning
is implemented. HTTP remains supported for devices that expose that transport.
