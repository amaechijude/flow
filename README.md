# Flow

Flow is a Linux network-usage monitor with a background daemon and an Avalonia
desktop GUI. It records real persisted usage in SQLite and separates attributed
per-process traffic from explicit unattributed traffic. It is a prototype, not
a packaged release.

## Current status

- Build: `dotnet build Flow.slnx -v minimal`
- Tests: `dotnet test Flow.slnx -v minimal` → **40/40 passing**
- Desktop: functional shell with Overview, Apps & Processes, Network Interfaces,
  and Diagnostics pages; CSV/JSON export uses a real file picker and includes
  provenance/caveats.
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
- No rollups, retention jobs, durable flush queue, or Reports/Settings pages yet.
- Charts and tables have not had a full keyboard/screen-reader accessibility pass.

## Packaging

Not packaged yet. Remaining productization work is listed in `HANDOFF.md`:
IPC/privilege topology, UI accessibility/reporting, persistence/rollups, and
systemd/D-Bus/Polkit/`.deb` packaging. Keep the caveats above in any shipped
documentation.
