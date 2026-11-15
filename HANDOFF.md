# Flow handoff

Date: 2026-11-15  
Repo: `/home/amaechi/code/flow` (git, branch `main`)

## What this is
Flow is an Ubuntu 26.04 (GNOME/Wayland) network-usage monitor with a background daemon and Avalonia desktop GUI (see `flow-project-plan.md`). Core requirement: transparent per-application/per-process byte attribution with persistent history. **Do not claim full completion without validated evidence.**

## Environment (dev/target snapshot)
- Ubuntu 26.04.1 LTS, kernel `7.0.0-43-generic` x86_64, GNOME Wayland
- .NET SDKs 10.0.404/11.0.100 (`/opt/dotnet`); product target .NET 10
- BTF `/sys/kernel/btf/vmlinux`, bpftool/libbpf/clang/llvm available; `kernel.unprivileged_bpf_disabled=2` (eBPF attach requires privileges)

## Completed (high level)
- **Docs/decisions:** `docs/architecture.md`, `docs/monitoring.md`, `docs/attribution.md`, `docs/security.md`, `docs/database.md`, `docs/implementation-plan.md`, `docs/progress.md`, ADRs `docs/decisions/0001-*`, `0002-*`.
- **Phase 1 prototype (eBPF, TCP):** `prototype/bpf/` contains `vmlinux.h`, BPF source (`flow_tcp_bytes.bpf.c`), userspace loader (`flow_tcp_bytes_loader.c`) with human and **TSV** output (`M/S/E/U`), and README. Validated on target: privileged attach works; per-TGID/UID TCP byte attribution (deltas/cached identity/startticks) observed, including explicit unattributed (`tgid==0`) accounting in prototype evolution.
- **Solution scaffold (`Flow.slnx`, `Directory.Build.props`):** `src/Flow.Domain`, `Flow.Application`, `Flow.Infrastructure`, `Flow.Linux`, `Flow.Contracts`, `Flow.Daemon` (Worker), `Flow.Desktop` (Avalonia MVVM template); `tests/*` projects; layered references wired. Full build and tests passing as of last run.
- **Domain:** identities (`ProcessIdentity` with stable-key inputs, `ApplicationIdentity`), measurement primitives, monitoring state, time-range/window/resolver (`[start,end)` UTC, local-TZ/DST-aware calendar/rolling ranges), usage sample model.
- **Application:** `INetworkMonitor`, `NetworkUsageEvent`, monitoring controller/diagnostics, bounded-channel ingestion + 1-minute UTC aggregation + batched/idempotent flush pipeline (`UsageIngestionPipeline`), metadata store abstraction, null monitor.
- **Infrastructure (SQLite, `Microsoft.Data.Sqlite`):** connection factory (WAL/pragmas), migrator + schema v1 (`applications`, `processes`, `network_interfaces`, `usage_buckets`, `interface_usage_buckets`, `monitoring_sessions`, `user_settings`, indexes), `UsageRepository` (minute-bucket upserts/batch writes, summary, top apps, time series, metadata/session upserts).
- **Linux monitors:** `ProcNetDevMonitor` (interface-level `/proc/net/dev` deltas, **unattributed** fallback) and `EbpfTcpMonitor` (subprocess integration of prototype loader TSV; emits attributed per-process TCP delta events with boot+startticks-derived start time/app hints and explicit unattributed events). Measurement semantics documented.
- **Daemon:** DB init/migration, DI wiring, monitor selection (`Flow:Monitor=null|procfs|ebpf|ebpf-tcp`), pipeline start/diagnostics logging.
  - Example: `sudo env Flow__Monitor=ebpf dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj`
  - Optional: `Flow__DatabasePath`, `Flow__Ebpf__LoaderPath`, `Flow__Ebpf__IntervalSeconds`
- **Verified end-to-end (2026-11-15):** with `Flow__Monitor=ebpf` (privileged), attributed TCP usage persisted to SQLite (`usage_buckets`, `measurement_provider='ebpf-tcp'`, process/app records). Prior flush-loop bug fixed (`PeriodicTimer` concurrent wait → no flush) in `fdc602a`.
- **Tests:** infra tests for migration/aggregation/queries/metadata; solution build/tests previously passing (see `docs/progress.md`).

## Current limitations / known issues
- **eBPF provider:** TCP-only (`tcp_sendmsg`/`tcp_recvmsg` return bytes), snapshot-delta granularity, subprocess + privileged loader (not in-process libbpf), no per-interface mapping in events, identity/lifecycle/PID-reuse hardening incomplete, coverage/accuracy/overhead not fully gate-validated.
- **Data correctness follow-up:** observed possible duplicate rows for same 1-minute bucket/process/attribution in one run; verify UNIQUE/conflict/upsert coalescing and flush idempotency.
- **Pipeline:** failed flush marks degraded and currently drops batch (no retry/backoff/durability queue).
- **Not implemented yet:** production-grade D-Bus IPC/GUI integration, rollups/retention jobs, exports, full per-app/process/interface reporting UX, systemd/Polkit/`.deb` packaging, broad system/gate tests.

## Remaining steps (prioritized)
1. **Correctness:** fix/verify bucket consolidation (upsert keys/coalescing), add pipeline/integration tests (fake monitor, batching, diagnostics, idempotency).
2. **Provider/identity hardening:** event-time persisted identity, PID-reuse/process-exit safety, interface mapping where possible, UDP/namespace/VPN/virtual-interface coverage assessment, controlled counter comparisons + overhead (gate evidence), ADRs.
3. **Daemon services & IPC:** query/health services over repositories/time ranges; versioned D-Bus contract (`Flow.Contracts`), daemon service + desktop client, auth/validation/reconnect.
4. **Persistence/reporting:** rollups (1m→5m→h→d→mo) preserving totals, async retention/cleanup, CSV/JSON export with range/TZ/measurement/attribution caveats.
5. **Desktop (Avalonia):** Overview/Apps/Processes/Interfaces/Reports/Settings wired to IPC, explicit live-vs-historical, health/stale/unattributed states, GNOME-friendly/a11y (no tray dependency).
6. **Release:** systemd/D-Bus/Polkit policies, `.deb` packaging, upgrade/uninstall data handling, Wayland and recovery/system tests, security/privacy review.

## Quick pointers
- Plans/status: `docs/progress.md`, `docs/implementation-plan.md`, `docs/attribution.md`, `docs/monitoring.md`
- Prototype: `prototype/bpf/README.md`, `prototype/README.md`, `scripts/check-prereqs.sh`
- DB default: `~/.local/share/flow/flow.db` (or `Flow__DatabasePath`)
- Build/test: `dotnet build Flow.slnx` / `dotnet test Flow.slnx`
- Inspect DB: `sqlite3 <db> ".headers on" ".mode column" "select * from usage_buckets order by id desc limit 20;"`

## Starting fresh
Read this file + `docs/progress.md` + `flow-project-plan.md`, confirm build/tests, then pick the next item from "Remaining steps" (recommend **#1 correctness + pipeline tests**). Keep changes incremental, document measurement/attribution semantics and gaps, and do not weaken per-process attribution claims.