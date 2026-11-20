# Packaging plan

Goal: turn Flow from a development prototype into an installable Linux app without
weakening the attribution caveats already documented in `README.md`.

## Phase 1 — Privilege/IPC topology (current)

**Problem:** the eBPF provider needs root, but a root daemon may fail to register
`org.flow.Daemon` on the user session bus. The desktop then loses live IPC even
though collection and persistence still work.

**Design:** split collection from session IPC.

- `Flow:Mode=full` — current behaviour: monitor + ingestion pipeline + D-Bus IPC.
  Development/default mode.
- `Flow:Mode=collector` — monitor + ingestion pipeline + SQLite writes only;
  no D-Bus registration. Intended to run as root under a systemd system unit.
- `Flow:Mode=session` — D-Bus IPC and queries only; no monitor or pipeline.
  Runs as the logged-in user under `systemd --user`. Reads the same SQLite DB.
  This mode needs a persisted diagnostics/status source and is a later step.

Success criteria for Phase 1:

- collector mode starts, writes real buckets, and registers no D-Bus service (implemented and smoke-tested 2026-11-19);
- full mode is unchanged (implemented);
- session mode starts IPC from persisted data without touching the monitor (implemented and smoke-tested 2026-11-19);
- build/tests pass and the mode is documented.

Session mode status comes from the latest persisted monitoring session. Live pipeline counters are not available to it and are reported as zero; collector-owned counters remain authoritative.

## Phase 2 — Shared DB and permissions (scaffolding added 2026-11-19)

- Choose a system DB location, e.g. `/var/lib/flow/flow.db`.
- Create a `flow` group; collector writes as root, session/desktop reads via group.
- Document that group members can read all usage history (single-user target).
- Multi-user isolation would require per-user DBs or a broker and is out of scope
  for the first package.

## Phase 3 — systemd and D-Bus integration (draft units added 2026-11-19)

- `flow-collector.service` (system): root, `Flow:Mode=collector`, restart on failure.
- `flow-daemon.service` or `flow-daemon@.service` (user): `Flow:Mode=session`,
  registers `org.flow.Daemon` on the session bus.
- D-Bus session activation file for the user daemon.
- Polkit decision accepted (ADR 0011): no policy for read-only session-bus queries; add only if privileged control operations or system-bus access are introduced.

## Phase 4 — Remaining product work before a release-quality `.deb`

A prototype `.deb` can now be built with `./packaging/build-deb.sh`; the remaining items below are for release quality.

- IPC hardening: signal continuity, reconnect/error mapping, `LastError` clearing.
- Persistence: 1m→5m→h→d→mo rollups preserving totals are implemented (idempotent rebuild worker) and consumed by long-range queries via a single-granularity policy; retention prunes 1m/5m/1h after 7/35/400 days (1d/1mo kept indefinitely). Durable flush/retry with exponential backoff is implemented with atomic apply-and-delete and restart recovery; durable diagnostics are exposed over IPC.
- UI: Reports/History/Settings pages and basic accessibility names/textual chart
  summary are implemented; full screen-reader conformance pass, chart
  interaction/per-point inspection and comparisons remain.
- Package: binaries, units, D-Bus service file, desktop launcher and dependencies
  (`libbpf`, `libelf`, `zlib`) are in the prototype `.deb`. `postinst`/`prerm`/
  `postrm` and upgrade/uninstall data handling are implemented and validated by
  `test-deb.sh` and `test-install.sh` (see `packaging/data-policy.md` and ADR 0012).
  Remaining: security/privacy/recovery tests and release-quality hardening.

## Accepted limitations for the first package

The high-churn LRU caveat, public-VPN/physical-NIC gap, and non-attributed procfs
mode remain documented limitations. They do not block an honest prototype package
but must stay in the shipped README/UI caveats.
