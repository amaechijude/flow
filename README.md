# Flow

Flow is a Linux network-usage monitor with a background daemon and an Avalonia
desktop GUI. It records real persisted usage in SQLite and separates attributed
per-process traffic from explicit unattributed traffic. It is a prototype with a
buildable `.deb`, not a release-quality package.

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

## Install (prototype `.deb`)

**Prerequisites:** Ubuntu 24.04+ with a graphical login, sudo, and the runtime
dependencies (`dotnet-runtime-10.0`, `libbpf1`, `libelf1`, `zlib1g`). A systemd
user session and session D-Bus are required for live IPC.

On the build machine, compile the eBPF loader and build the package
(`clang`, `bpftool`, libbpf/libelf/zlib dev packages are required):

```bash
clang -O2 -g -target bpf -D__TARGET_ARCH_x86 -I prototype/bpf \
  -c prototype/bpf/flow_tcp_bytes.bpf.c -o prototype/bpf/flow_tcp_bytes.bpf.o
bpftool gen skeleton prototype/bpf/flow_tcp_bytes.bpf.o > prototype/bpf/flow_tcp_bytes.skel.h
cc -O2 -g -Wall -Wextra -I prototype/bpf \
  -o prototype/bpf/flow_tcp_bytes_loader prototype/bpf/flow_tcp_bytes_loader.c -lbpf -lelf -lz

./packaging/build-deb.sh
# output: packaging/build/flow_0.1.0_amd64.deb
```

Copy the `.deb` to the target machine, then install it:

```bash
sudo apt install ./flow_0.1.0_amd64.deb
# If .NET 10 is not in the distro repos, use the prototype form:
sudo dpkg --force-depends -i ./flow_0.1.0_amd64.deb
```

`postinst` creates the `flow` group and `/var/lib/flow` (`root:flow 2770`),
creates/owns the database as `root:flow 0660`, and enables the collector.
Add your user and **log out and back in** so the group takes effect:

```bash
sudo usermod -aG flow "$USER"
id -nG "$USER" | tr ' ' '\n' | grep -qx flow && echo "group OK"
```

Start both roles:

```bash
sudo systemctl enable --now flow-collector.service   # root, eBPF, no D-Bus
systemctl --user enable --now flow-daemon.service    # user, read-only D-Bus IPC
```

Open Flow from the applications menu. The launcher passes
`Flow__DatabasePath=/var/lib/flow/flow.db`; the collector writes that database
and the session daemon serves queries from it. Do **not** run the desktop as root.
Allow ~10 s after startup for the first real flush, then click ⟳.

Verify:

```bash
systemctl is-active flow-collector.service
systemctl --user is-active flow-daemon.service
busctl --user call org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetContractVersion
# -> i 3
FLOW_READONLY=1 ./packaging/validate-user-session.sh
```

If the UI shows `DAEMON OFFLINE` with `0 B`, check
`journalctl -u flow-collector.service -n 30`; older packages failed with
`attempt to write a readonly database`. See `packaging/README.md` →
"Troubleshooting" for the fix and the rebuild that includes it.

## Uninstall and clean reinstall

The database is **user measurement data**, not package data:

| Command | Package files | `/var/lib/flow` data |
| --- | --- | --- |
| `sudo dpkg -r flow` | removed | preserved |
| `sudo dpkg -P flow` | removed | preserved |
| `sudo env FLOW_PURGE_DATA=yes dpkg -P flow` | removed | **deleted** |

Note the `sudo env` form — plain `FLOW_PURGE_DATA=yes sudo dpkg -P flow` loses
the variable because sudo strips the environment. An alternative is to create
`/etc/flow/purge-data.conf` containing `PURGE_DATA=yes` before purging.

For an auditable full cleanup, use the helper (dry-run first):

```bash
sudo ./packaging/uninstall-clean.sh --dry-run
sudo ./packaging/uninstall-clean.sh                 # purge + delete data
sudo ./packaging/uninstall-clean.sh --keep-data     # purge, keep history
sudo ./packaging/uninstall-clean.sh --remove-dev-data  # also delete ~/.local/share/flow
```

It stops both units, purges with the correct opt-in, removes stale enablement
symlinks and the optional purge config, and removes the `flow` group only when
no data is preserved. Deleting data requires confirmation unless `--yes` is
passed. The development database at `~/.local/share/flow/flow.db` is separate
from the packaged `/var/lib/flow/flow.db` and is only removed with
`--remove-dev-data`.

To reinstall cleanly:

```bash
sudo systemctl stop flow-collector.service
systemctl --user stop flow-daemon.service
sudo dpkg -i ./flow_0.1.0_amd64.deb
sudo systemctl restart flow-collector.service
systemctl --user restart flow-daemon.service
```

Close and reopen Flow. If you kept data, `/var/lib/flow/flow.db` and its history
are reused; after a full cleanup the collector creates a fresh database.

Full policy and rationale: `packaging/data-policy.md` and ADR 0012.

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

## Packaging and validation

Build/validate with `packaging/build-deb.sh`, `packaging/test-deb.sh` and
`packaging/test-install.sh` (the latter needs Docker). A real-VM user-session
kit is in `packaging/validate-user-session.sh` / `docs/validation/user-session.md`,
with an SSH dispatcher in `packaging/run-user-session-validation.sh`; the
source-session and activation checks use `packaging/validate-source-session.sh`
and `packaging/validate-dbus-activation.sh`. Static UI accessibility is audited
by `scripts/a11y-audit.sh` (`docs/a11y-audit.md`); runtime screen-reader
verification is still outstanding.

This is not a release-quality package. Remaining productization work is in
`HANDOFF.md`. Keep the caveats above in any shipped documentation.
