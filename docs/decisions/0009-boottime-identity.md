# ADR 0009: Use boottime for event-time process start identity

- Status: Accepted (implemented and privileged-validated)
- Date: 2026-11-19

## Context

ADR 0005 chose `task_struct.start_time` for event-time process identity. Controlled
identity diagnostics showed a constant ~130,575-tick (~1,305 s) gap between BPF
`start_ns` converted with `clk_tck` and `/proc/<pid>/stat` starttime:

- 8 workers: computed 4,314,025 vs `/proc` 4,444,599
- 1 worker: computed 4,398,790 vs `/proc` 4,529,365

The gap is constant across runs and worker counts, so it is not thread-related.
It is consistent with a clock-domain difference: kernel `start_time` is
monotonic (excludes suspend time), while `/proc` starttime is boottime-based
(includes suspend time).

## Decision

Read `task_struct.start_boottime` via CO-RE in BPF for event-time identity, with
a fallback to `start_time` if `start_boottime` is unavailable/zero. The TSV field
name `start_ns` is retained for compatibility, but its semantic meaning becomes
boottime nanoseconds.

## Consequences

- Event-time start time should now match `/proc/<pid>/stat` starttime after
  conversion, restoring live-identity verification on suspend-capable systems.
- `ProcessIdentity.StableKey` values change for all new rows because the
  timestamp basis changes. Historical 3-part/4-part monotonic-based keys are not
  rewritten; old and new rows remain distinct. This is a development-stage
  identity migration.
- Privileged validation (2026-11-19): 1-worker and 8-worker identity diagnostics both showed `computed−raw=0`, `verified=True`, and resolved `/usr/bin/python3.14` (`docs/measurements/ebpf-controlled-20261119T045127Z.md`, `...T045242Z.md`). The verification issue is fixed for these controlled cases.
