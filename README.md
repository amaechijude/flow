# Flow

Flow is a Linux network-usage monitor with a background daemon and an Avalonia
desktop GUI. It records real persisted usage in SQLite and separates attributed
per-process traffic from explicit unattributed traffic. It is a prototype, not
a packaged release.

## Current status

- Build: `dotnet build Flow.slnx -v minimal`
- Tests: `dotnet test Flow.slnx -v minimal` → **86/86 passing**
- Desktop: functional shell with Overview, Apps & Processes, Network Interfaces,
  Diagnostics, Reports, History, Settings, and Comparison pages; CSV/JSON export uses a real
  file picker and includes provenance/caveats. Interactive and custom-drawn
  controls have accessibility names; a full screen-reader conformance pass is
  still outstanding.
- Daemon: SQLite persistence, D-Bus session-bus IPC (contract v3), real
  monitoring diagnostics, and a truthful SQLite fallback when IPC is unavailable.
- Attribution provider: privileged eBPF loader with TCP+UDP byte accounting and
  per-interface attribution where the socket route resolves.

`procfs` mode is unattributed by design: it reports real interface totals only.
The eBPF provider is required for per-process attribution.

## Run (development)

```bash
dotnet build Flow.slnx -v minimal
dotnet test Flow.slnx -v minimal

# Unattributed interface totals, no privileges
Flow__Monitor=procfs dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj

# Privileged per-process attribution (prototype loader)
sudo env Flow__Monitor=ebpf Flow__DatabasePath="$HOME/.local/share/flow/flow.db" \
  dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj

# Headless collector role (no D-Bus registration; intended for systemd)
Flow__Mode=collector Flow__Monitor=procfs Flow__DatabasePath=/var/lib/flow/flow.db \
  dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj

# Session IPC role (no monitor; intended for systemd --user)
Flow__Mode=session Flow__DatabasePath=/var/lib/flow/flow.db \
  dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj

# Desktop
dotnet run --project src/Flow.Desktop/Flow.Desktop.csproj
```

The privileged loader must be built from `prototype/bpf/` before using
`Flow__Monitor=ebpf`. See `prototype/bpf/README.md`.

## Validation evidence

Controlled, privileged runs are recorded in `docs/measurements/`. The index is
`docs/measurements/README.md`. Interpretation, gates, and ADRs are in
`docs/attribution.md` and `docs/decisions/`.

Accepted evidence includes:

- exact TCP and UDP payload accounting for controlled transfers;
- per-interface attribution on loopback, veth, physical Wi-Fi (`wlp0s20f3`),
  GRE, and real WireGuard devices;
- event-time process identity, network-namespace identity, and boottime-based
  start time;
- 60 s/300 s soaks and a concurrent production-load proxy;
- explicit overflow accounting when the BPF map cannot accept new generations.

## Known limitations

Do not treat the attribution gate as closed. Important caveats:

- **High-churn LRU inaccuracy:** with ~33,000 short-lived process generations
  in 30 minutes, the LRU map eliminated silent map-full loss and kept aggregate
  byte checks within the declared ±1% tolerance, but exact totals were
  **282,624 bytes (~0.41%) over** and `check_single_generation` was false.
  Short-lived generations can be split or recreated across snapshots. Do not use
  Flow for billing, strict quotas, or audit-grade exact accounting under such
  churn. See `docs/measurements/ebpf-controlled-20261119T061332Z.md` and ADR 0010.
- ICMP and non-TCP/UDP paths are not counted. QUIC is counted as UDP payload;
  QUIC framing is not identified.
- Per-interface attribution depends on socket route resolution; `ifindex 0` is
  grouped without an interface, not guessed.
- Real WireGuard devices and GRE tunnels are validated, but a public VPN over a
  physical NIC with an external byte-counting peer has not been measured.
- The eBPF provider requires root/CAP_BPF and runs as a subprocess loader.
- Minute buckets aggregate TCP and UDP; protocol is not a persisted dimension.
- Historical rows keep older stable-key shapes after identity migrations.
- IPC is session-bus v1 only; signals are best-effort/coalesced, and a root-run
  daemon may not claim the user-session D-Bus name.
- Rollups (5m/1h/1d/1mo) are rebuilt idempotently from 1m buckets and consumed by queries for long ranges (one granularity per query; boundary buckets are included whole, so long-range totals may include bytes slightly outside the exact window). Retention prunes 1m after 7 days, 5m after 35 days, and 1h after 400 days; 1d/1mo are kept indefinitely. Minute deltas are written to a durable SQLite queue before leaving the in-memory buffer and applied with exponential retry/backoff (1s→30s); apply-and-delete share one transaction so retries cannot double-count, and the rollup watermark stays behind pending rows. Reports/History/Settings pages exist. Queries are half-open (`>= start`, `< end`); the Comparison page reports the exact bucket coverage of each window and refuses to call a padded coarse-window comparison exact or billing-grade.
- The durable queue covers byte deltas only; application/process/interface metadata upserts remain best-effort and are re-upserted from later events.
- Charts and tables have accessibility names and a textual chart summary, but have not had a full keyboard/screen-reader conformance pass.

## Packaging

A prototype `.deb` can be built and validated with `packaging/build-deb.sh`,
`packaging/test-deb.sh` and `packaging/test-install.sh` (the latter needs Docker).
A real-VM user-session validation kit is in
`packaging/validate-user-session.sh` / `docs/validation/user-session.md`, with an
SSH dispatcher in `packaging/run-user-session-validation.sh` (preflight passes
here; the full sudo-driven run is an operator step). Static UI accessibility is
audited by `scripts/a11y-audit.sh`; see `docs/a11y-audit.md` (runtime
screen-reader verification is still outstanding).
It is not a release-quality package. Data survives upgrade, remove and default
purge; full removal requires an explicit opt-in (`packaging/data-policy.md`,
ADR 0012). Remaining productization work is listed in `HANDOFF.md`: full UI
accessibility conformance and recovery/security/privacy tests. Keep the caveats
above in any shipped documentation.
