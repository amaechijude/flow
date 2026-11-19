# ADR 0010: LRU eviction with epoch-based delta reset

- Status: Accepted (implemented; privileged validation pending)
- Date: 2026-11-19

## Context

ADR 0008 measured that even a 65,536-entry BPF hash loses ~2.3% of bytes at
33.5k short-lived process generations because new rows cannot be inserted when
the map is full. Overflow counters made the loss visible, but capacity increases
only postpone it. Dead process generations accumulate because the map has no
expiry.

## Decision

1. Change the BPF `traffic` map from `BPF_MAP_TYPE_HASH` to
   `BPF_MAP_TYPE_LRU_HASH`. Least-recently-used rows are evicted automatically
   under pressure; active generations are touched on every byte event and stay.
2. Add an `epoch` (`bpf_ktime_get_ns()` at row creation) to `traffic_val`.
3. Track `epoch` in the loader's `prev` entries. If a row's epoch changes
   between snapshots, treat its counters as freshly created (full value is the
   delta) instead of computing `val - prev`, which would undercount or double
   count after eviction/recreation.
4. Keep the explicit overflow counter: LRU handles pressure, but any bytes that
   still cannot get a row remain visible rather than silent.

## Consequences

- Dead generations are reclaimed automatically; high-churn attribution should
  stay exact without unbounded map growth.
- A quiet-but-live process could be evicted and recreated; bytes between its
  last access and the next loader snapshot may still be missed. This is a known
  residual limitation and must be validated, not assumed away.
- Stable keys are unchanged (epoch is value metadata, not key material).
- New rows include epoch, so loader/BPF must be rebuilt together. Historical
  persisted data is unaffected.
