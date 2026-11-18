# ADR 0005: Event-time process identity for byte attribution

- Status: Accepted
- Date: 2026-11-18

## Context

The eBPF TCP prototype keyed byte counters by `(tgid, uid)` and enriched
identity from `/proc` at snapshot time. Two problems followed:

1. **PID reuse merged generations.** If a TGID was reused while the loader was
   attached, both processes shared one counter row and one persisted identity.
2. **Short-lived processes lost identity.** A process could exit before the next
   snapshot, leaving counters that could not be safely enriched. Enriching a
   reused TGID from `/proc` could attach the wrong executable to old bytes.

## Decision

1. Capture identity **at event time in the kernel**. The BPF map key is
   `{start_ns, tgid, uid}`, where `start_ns` is `task_struct.start_time`
   (nanoseconds since boot, read with CO-RE). The value also stores the
   first-observed `cgroup_id` and `comm` so attribution survives process exit.
2. Emit these fields from the loader. The TSV `E` line is extended
   backward-compatibly to:
   `E  tgid  uid  tx_total  rx_total  tx_delta  rx_delta  comm  exe  startticks  verified  start_ns  cgroup_id`.
3. Only enrich `comm`/`exe` from `/proc` when the live PID's start ticks match
   the event-time `start_ns` (tolerance ±2 ticks). Otherwise keep the
   kernel-captured `comm` and leave the executable unknown.
4. Derive `ProcessIdentity.StartTimeUtc` from `boot_unix_ms + start_ns` when
   available, falling back to `startticks / clk_tck`, then to the current time
   (documented degraded identity).
5. Define `ProcessIdentity.StableKey` as `pid:startUnixMs:uid`. Executable path
   and other enrichment metadata are attributes, not key material: they can
   become known later or change between snapshots and must not split one
   process generation into multiple identities.
6. Persist the kernel-captured cgroup id in the existing `cgroup_id` metadata
   column (numeric id as text). This is an opaque cgroup v2 id, not a path.

## Consequences

- PID reuse produces distinct counter rows, stable keys, and process metadata.
- A short-lived process retains event-time start time, comm, and cgroup; its
  executable may remain unknown. Attribution notes state this rather than
  guessing.
- Rows can no longer be merged by `(tgid, uid)` alone; map capacity is shared
  across process generations until the loader detaches (no expiry yet).
- Old loader output (11-field `E` lines) is still accepted by `Flow.Linux`.
- The stable-key change means previously persisted rows keyed with the old
  `pid:start:uid:exe` format are not merged with new rows. This is a
  development-stage schema/identity migration; no automatic rewrite is done.
- This ADR does **not** close the attribution gate. UDP, per-interface mapping,
  controlled counter comparison, overhead, namespaces, and VPN/tunnel behavior
  still require measurement evidence (`docs/attribution.md`).
