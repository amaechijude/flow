# ADR 0008: BPF map saturation causes silent under-attribution

- Status: Accepted (measured limitation); fix implemented, post-fix privileged validation pending
- Date: 2026-11-19

## Context

The eBPF traffic map has `max_entries = 10240` and no expiry or eviction. Earlier
churn runs reached 7,653 and 10,107 entries without byte loss, so the behavior
at the hard cap was unmeasured. A dedicated churn run
(`docs/measurements/ebpf-controlled-20261119T003701Z.md`) drove the map to
**10,231 entries** (of 10,240).

## Measurement

- 5,120 short-lived children, each sending 1,024 bytes over TCP and UDP.
- Expected per protocol/direction: **5,242,880 bytes**; attributed: **5,235,712 bytes**.
- Shortfall: **7,168 bytes = 7 children × 1,024**, in each of TCP TX/RX and UDP TX/RX.
- Unattributed counters stayed **0** — the missing bytes were not bucketed as
  unattributed; the map update for new process generations simply failed and
  `add_bytes()` returned without counting.
- `check_churn_bytes` used a ±1% tolerance and therefore still reported True
  (0.137% loss). The exact shortfall is the meaningful result.
- `check_single_generation` was False because some expected child PIDs had no
  rows at all after failed insertions.

## Decision

Implemented fix: raise `traffic` and loader `prev` capacity from 10,240 to 32,768, and add an explicit BPF `overflow` counter (emitted as `F` TSV lines and Flow unattributed events) so any remaining map-full loss is visible rather than silent.

1. Treat hard-cap saturation as a **known correctness limitation**, not a clean
   validation pass.
2. Do not widen accuracy claims for high-churn scenarios until the map behavior
   is fixed.
3. Evaluate one of:
   - expiry/eviction of dead process generations,
   - a larger map, plus loader `prev` array sizing to match,
   - explicitly counting map-full failures in an unattributed/overflow bucket.
4. Keep the ±1% aggregate check, but report exact expected-vs-attributed
   deltas so small systematic losses are visible.

## Consequences

- Under sustained process churn near the map cap, the provider silently drops
  bytes from new process generations. Users cannot distinguish this from zero
  traffic without the exact shortfall evidence.
- The existing unattributed bucket does not capture map-full loss.
- Persistence identity is unaffected, but attribution completeness is not
  trustworthy at capacity.
- This ADR does not fix the problem; it records the evidence and the required
  design choice before claiming high-churn reliability.
