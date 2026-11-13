# Flow progress log

## 2026-11-13 (initial environment inspection and planning pass)

### Completed
- Inspected the development environment (Ubuntu 26.04.1 LTS, kernel 7.0.0-43-generic, x86_64, GNOME/Wayland session).
- Confirmed .NET SDK availability: 10.0.404 and 11.0.100 installed under `/opt/dotnet/sdk`.
- Confirmed relevant Linux/eBPF tooling present: `bpftool v7.7.0` (libbpf 1.7), `libbpf1`, BCC packages, kernel BTF available at `/sys/kernel/btf/vmlinux`.
- Noted `kernel.unprivileged_bpf_disabled=2` (unprivileged BPF restricted), so privileged/capability-based collection is expected for a production daemon.
- Created initial documentation set required by the project plan:
  - `docs/architecture.md`
  - `docs/monitoring.md`
  - `docs/attribution.md`
  - `docs/security.md`
  - `docs/database.md`
  - `docs/implementation-plan.md`
  - `docs/decisions/0001-initial-technical-direction.md`

### Current work
- Phase 0 / first-task baseline: documenting feasibility, risks, and recommended path before full daemon/GUI implementation.
- No product features, daemon, or desktop UI implemented yet (per plan: prototype/validation gate comes first).

### Test results
- No automated tests executed yet (no solution/projects created in this pass).
- Environment checks performed manually: `uname -a`, `/etc/os-release`, `dotnet --list-sdks`, `dotnet --list-runtimes`, Wayland session variables, `bpftool`/BTF presence.

### Blockers / risks
- **Per-process byte attribution prototype not yet validated end-to-end.** This is the critical technical gate; implementation must not claim completion without repeatable evidence on target.
- `clang`/LLVM toolchain for compiling BPF C objects was not explicitly confirmed in this pass; packaging/build prerequisites for eBPF artifacts need verification.
- Avalonia Wayland behavior and maintained .NET D-Bus library choice require validation against current upstream docs/APIs before locking dependencies.
- Hook choice (e.g. cgroup skb vs socket/tc/kprobe-based accounting), namespace handling, VPN/virtual interface semantics, and overhead must be proven with controlled traffic.

### Next steps
1. **Unblock BPF object build:** install/verify `clang` + LLVM tools and libbpf development artifacts as needed for the chosen CO-RE workflow; document exact packages and record ADR.
2. Build a minimal eBPF CLI proof of concept in `prototype/` (or `src/`) that:
   - loads a BPF program with explicit privileges/capabilities,
   - attributes send/receive bytes to process identity (pid/start-time/comm/exe/cgroup/uid where possible),
   - reports unattributed traffic and compares aggregates to interface counters under controlled load.
3. Measure CPU/memory overhead and document unsupported cases (namespaces/VPN/virtual interfaces/short-lived processes).
4. Decide and record D-Bus library and Avalonia version pinning after compatibility checks.
5. Only after the monitoring gate passes: scaffold solution/projects, implement domain/persistence, then daemon, then desktop per plan.

## 2026-11-13 (prototype scaffolding and toolchain verification)

### Completed
- Verified additional prerequisites: kernel headers for `7.0.0-43-generic` present, BTF available, `libbpf.so.1` present; **`clang`/LLVM build tools not found on PATH** and `libbpf-dev` not installed (BPF C compilation currently blocked in this environment).
- Generated CO-RE reference header: `prototype/bpf/vmlinux.h` (from `/sys/kernel/btf/vmlinux` via `bpftool`).
- Scaffolded Phase 1 prototype workspace:
  - `prototype/README.md`
  - `prototype/bpf/flow_monitor.bpf.c.stub` (placeholder; not compiled/validated)
  - `prototype/Flow.MonitorPrototype/` (.NET 10 console diagnostic CLI)
- Implemented and verified a **baseline diagnostic** CLI (not per-process attribution):
  - prints environment/tooling/privilege indicators,
  - reads aggregate per-interface counters from `/proc/net/dev` and samples deltas,
  - samples process identity inputs (PID, UTC start time, UID, comm, exe where readable).
- Build/run verification:
  - `dotnet build prototype/Flow.MonitorPrototype/Flow.MonitorPrototype.csproj -v minimal` → **succeeded** (net10.0)
  - `dotnet run --project prototype/Flow.MonitorPrototype/Flow.MonitorPrototype.csproj -- sample` → **ran successfully**, showing live interface deltas and process samples (still no per-process byte attribution).

### Current work
- Phase 0/1 transition prep: prototype scaffolding exists, but the **eBPF attribution technical gate remains open** pending BPF toolchain availability and a validated hook/loader implementation.

### Test results
- Manual prototype execution succeeded on target host; output confirms baseline discovery/counter sampling only.
- No unit/integration/system tests added yet.

### Blockers / risks
- Cannot compile/load a custom BPF program yet without `clang` (and compatible BPF build workflow) and likely `libbpf-dev` headers.
- Attribution semantics/hook selection still unproven; current prototype must not be interpreted as satisfying per-application/per-process accounting requirements.
- Process sample in this environment shows many kernel threads with unreadable exe paths (expected for restricted `/proc/[pid]/exe`); userspace correlation design must handle this.
- Avalonia Wayland behavior and maintained .NET D-Bus library/API choice remain unverified on target.

## 2026-11-13 (git initialization and continued prototype prep)

### Completed
- Initialized git repository and created `.gitignore` for .NET/build/local-db/prototype artifacts.
- Commits:
  - `9694f3d` — baseline plan/docs/prototype scaffold and generated `vmlinux.h`
  - `815f58b` — prerequisite check script, pending UI/IPC ADR, and draft eBPF program
- Added `scripts/check-prereqs.sh` to report toolchain/kernel/BTF/libbpf prerequisites (no package installation performed).
- Added `docs/decisions/0002-pending-ui-and-ipc-choices.md` documenting pending verification for Avalonia (Wayland) and .NET D-Bus library selection.
- Added `prototype/bpf/flow_net_monitor.bpf.c.draft` (hypothesis draft, e.g. cgroup_skb ingress/egress accounting sketch) — **not compiled or validated**; hook/byte semantics must be chosen from evidence.

### Current work
- Repository is now version-controlled; Phase 0/1 remains blocked on BPF build toolchain availability and validated attribution prototype.

### Test results
- `scripts/check-prereqs.sh` created (executable) for repeatable environment checks; prior prototype build/run still passing as reported above.

### Next steps
1. **Run privileged validation of TCP prototype:** execute `sudo ./prototype/bpf/flow_tcp_bytes_loader <interval> <rounds>` while generating controlled TCP traffic (e.g. `curl` downloads), compare TGID-attributed deltas to generating processes and to interface counter deltas (`/proc/net/dev`, `ip -s link`); record results, gaps, and overhead in `docs/attribution.md`/`docs/progress.md`.
2. **Harden prototype toward gate criteria:** enrich attribution to stable process identity (PID + start time/generation id, exe/cmd/uid/cgroup/namespace as available), handle process exit/PID reuse, add explicit **unattributed** accounting/diagnostics, and evaluate UDP/other-path coverage and hook/semantics tradeoffs (cgroup_skb vs fentry/tc/etc.) with evidence.
3. Spike Avalonia Wayland smoke test and D-Bus prototype; supersede ADR 0002 with accepted decisions and pinned versions.

## 2026-11-13 (BPF TCP prototype compiled and loader built)

### Completed
- BPF build prerequisites now available on host (clang/LLVM/llvm-strip/libbpf-dev confirmed via `scripts/check-prereqs.sh`).
- Implemented and compiled TCP byte-counter BPF prototype:
  - `prototype/bpf/flow_tcp_bytes.bpf.c` (`fexit/tcp_sendmsg`, `fexit/tcp_recvmsg` returning >0 bytes; per-(tgid,uid) cumulative maps)
  - compiled `flow_tcp_bytes.bpf.o` (CO-RE/BTF target, warnings only from `vmlinux.h`)
  - generated `prototype/bpf/flow_tcp_bytes.skel.h`
- Built userspace loader `prototype/bpf/flow_tcp_bytes_loader.c` (libbpf skeleton-based; prints periodic TGID/UID/TX/RX snapshots). Binary built locally but intentionally not committed (rebuildable).
- Added `prototype/bpf/README.md` documenting measurement semantics, limitations (TCP-only hooks, non-final attribution), rebuild steps, privileged run, and controlled-validation approach.

### Test results
- BPF object compilation: **succeeded**.
- Loader compilation: **succeeded** (`cc ... -lbpf -lelf -lz`).
- Runtime without sufficient privilege: **failed as expected** with `EPERM` / `failed to load BPF object` on this host (`unprivileged_bpf_disabled=2`), confirming privilege requirement; **no validated per-process attribution data collected yet**.

### Blockers / risks
- **Gate criteria still unmet:** live privileged run confirmed basic TCP per-TGID/UID counting, but prototype still lacks stable process identity (PID start-time/generation, exe/cgroup/ns correlation), PID-reuse/process-exit handling, explicit **unattributed** bucket, UDP/non-TCP coverage, controlled interface-counter comparison for the same transfer window, overhead measurements, and namespace/VPN/virtual-interface validation.
- **Hook scope/semantics:** counts bytes from `tcp_sendmsg` return / `tcp_recvmsg` return at hook point (TCP-path only); not directly comparable to link/interface byte totals and may miss or differ across offload/tunnel paths. RX hook uses `kretprobe` for kernel 7.0 compatibility (fexit signature mismatch observed earlier).

## 2026-11-13 (privileged TCP prototype validation - initial evidence)

### Completed
- User executed rebuilt TCP prototype with `sudo ./prototype/bpf/flow_tcp_bytes_loader 2 15`.
- Both hooks attached successfully: `TX: tcp_sendmsg, RX: tcp_recvmsg`.
- Loader produced cumulative per-(TGID,UID) snapshots showing attributed TCP bytes during the run (example observed: TGID 64022 UID 1000 RX reached 36 bytes; TGID 64042 UID 1000 TX reached 956 bytes), confirming **basic privileged eBPF TCP per-process/TGID byte attribution works on this kernel** at the chosen hook points.

### Interpretation / caveats
- Positive evidence for feasibility of eBPF-based per-task accounting on target (kernel 7.0.0-43, BTF, unprivileged_bpf_disabled=2 requiring elevation).
- Not evidence of full gate completion: TGID-only keying, no stable identity enrichment/lifecycle, no unattributed bucket, TCP-only coverage, and no recorded controlled comparison to interface counters or overhead metrics for this run.

## 2026-11-13 (enriched loader validation run)

### Completed
- User ran improved loader: `sudo ./prototype/bpf/flow_tcp_bytes_loader 2 20` with enriched snapshots/deltas and `/proc` correlation.
- **Observed attributed live traffic across multiple apps/userspace contexts:** `msedge` (`/opt/microsoft/msedge/msedge`), `code` (`/snap/code/268/usr/share/code/code`), and `opencode` (`/home/amaechi/.opencode/bin/opencode`) showed non-zero cumulative/delta TCP TX/RX bytes over time.
- **Process-exit enrichment gap observed:** some TGIDs (e.g. 102158, 102346) appeared with counters but `COMM=?`, `EXE=-` at snapshot time, indicating processes exited before/at enrichment lookup (bytes were attributed to TGID, but identity context was unavailable at sample time). This highlights need for event-time identity capture/lifecycle handling.
- **Loader bug found/fixed:** `startticks` was reported as `0` in this run due to `/proc/<tgid>/stat` field parsing offset; fixed in `prototype/bpf/flow_tcp_bytes_loader.c` (requires rebuild + rerun to collect correct start-time values).

### Test results
- Privileged attach and live per-TGID TCP byte attribution with deltas: **observed working**.
- Stable identity completeness, PID-reuse safety, unattributed accounting, interface-counter comparison, and overhead: **still pending**.

### Next steps
1. Continue **Phase 2**: expand SQLite persistence (app/process/interface upserts, interface buckets, rollups/retention jobs, migrations beyond v1) and add unit/integration tests (time boundaries, identity/PID reuse, aggregation/rollups, migration correctness).
2. Implement `Flow.Application` use cases/query services and daemon ingestion pipeline (bounded channel, idempotent aggregation, health/diagnostics) on top of new abstractions.
3. Implement `Flow.Linux` monitoring integration surface (provider adapter informed by prototype) and `Flow.Contracts` expansion for GUI/daemon IPC.
4. Progress daemon (`Flow.Daemon`) runtime wiring and D-Bus integration after contracts are verified; continue desktop (`Flow.Desktop`) once query/health paths are stable.
5. Revisit Phase 1 gate evidence as needed: controlled interface-counter comparisons, overhead, UDP/namespace/VPN coverage, and stable persisted identity behavior.

## 2026-11-13 (solution scaffolding and initial domain foundation)

### Completed
- User-provided enriched TCP prototype run (with fixed `startticks`) showed correct per-process identity correlation fields for live processes (e.g. non-zero startticks across msedge/code/opencode). Prototype validation step considered sufficient for now by user direction; **full Phase 1 gate criteria remain not fully satisfied** (UDP/coverage, controlled counter/overhead reports, persisted stable identity, etc.).
- Scaffolded .NET solution/app structure:
  - `Flow.slnx`, `Directory.Build.props` (net10.0, nullable, implicit usings)
  - `src/`: `Flow.Domain`, `Flow.Application`, `Flow.Infrastructure`, `Flow.Linux`, `Flow.Contracts`, `Flow.Daemon` (worker), `Flow.Desktop` (Avalonia MVVM template)
  - `tests/`: `Flow.Domain.Tests`, `Flow.Application.Tests`, `Flow.Infrastructure.Tests`, `Flow.Linux.Tests`, `Flow.Daemon.Tests`, `Flow.SystemTests`
  - Project references wired per layered architecture baseline
- Implemented initial `Flow.Domain` foundation:
  - `Identity`: `ProcessIdentity` (stable key inputs), `ApplicationIdentity`
  - `Measurement`: `TrafficDirection`, `ByteCounters`, `MeasurementPoint`
  - `Monitoring`: `MonitoringState`
  - `Time`: `TimeRangeKind`, `TimeWindow` (`[start,end)`), `TimeRangeResolver` (rolling/calendar ranges, local TZ/DST-aware boundary handling, configurable week start)
  - `Model`: `NetworkUsageSample`

### Test results
- `dotnet build src/Flow.Domain/Flow.Domain.csproj -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet build Flow.slnx -v minimal` → **timed out in this session** (Avalonia restore/build in progress or slow); full solution build status not yet confirmed.

## 2026-11-13 (application/contracts abstractions and initial SQLite persistence)

### Completed
- Added initial monitoring abstractions in `Flow.Application`:
  - `Monitoring/NetworkUsageEvent.cs`, `INetworkMonitor.cs` (async event stream), `IMonitoringController.cs`
- Added initial IPC/query DTOs in `Flow.Contracts`:
  - `Monitoring/MonitoringStatusDto.cs`, `Usage/UsageSummaryDto.cs`
- Added SQLite foundation in `Flow.Infrastructure.Sqlite` (using `Microsoft.Data.Sqlite`):
  - `SqliteConnectionFactory` (WAL/synchronous/foreign_keys pragmas)
  - `DbMigrator` with `schema_migrations` and initial schema v1 (applications, processes, network_interfaces, usage_buckets, interface_usage_buckets, monitoring_sessions, user_settings + indexes)
  - `UsageRepository` with summary query over `[start,end)` buckets and minute-bucket upsert/aggregation helper
- Removed template `Class1.cs` placeholders from core src projects.

### Test results
- `dotnet build src/Flow.Infrastructure/Flow.Infrastructure.csproj -v minimal` (and dependencies) → **succeeded**.
- Full `dotnet build Flow.slnx` / `dotnet test Flow.slnx` not rerun in this increment (previous verified state: build and tests passing before these additions).

### Current work
- Phase 2 in progress: ingestion/aggregation pipeline, metadata persistence hooks, and daemon wiring in place (null monitor, SQLite-backed writer, health/diagnostics, session tracking); richer repositories/queries, migrations evolution, real Linux provider adapter, IPC, and broader tests still pending.

## 2026-11-13 (metadata persistence, session tracking, and infrastructure tests)

### Completed
- Added `Flow.Application.Persistence.IMonitoringMetadataStore` and implemented in `Flow.Infrastructure.Sqlite.UsageRepository`:
  - application/process/interface upserts (with `last_seen` updates, normalized interface IDs)
  - monitoring session start/end records
- Extended `UsageIngestionPipeline` to optionally persist seen applications/processes/interfaces during flush and to record monitoring sessions (start/end) tied to pipeline lifecycle.
- Wired `IMonitoringMetadataStore` in `Flow.Daemon`.
- Added `tests/Flow.Infrastructure.Tests/SqliteUsageRepositoryTests.cs` covering migration idempotency, minute-bucket delta aggregation/summary (attributed + unattributed), and metadata/session smoke behavior.

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal` → **passed** (7 tests total: Domain 1, Application 1, Infrastructure 2, Linux 1, Daemon 1, System 1).

## 2026-11-13 (application/daemon ingestion pipeline)

### Completed
- Implemented ingestion/aggregation pipeline in `Flow.Application`:
  - `Persistence/UsageBucketDelta.cs`, `IUsageBucketStore.cs` (batch write contract)
  - `Monitoring/MonitoringDiagnostics.cs` (state, last event/flush, errors, received/dropped/aggregated/batches/writeErrors counters)
  - `Monitoring/UsageIngestionPipeline.cs` — bounded channel (`DropOldest`) ingestion from `INetworkMonitor`, in-memory aggregation keyed by 1-minute UTC bucket + app/process/interface/attribution, periodic and size-triggered **batch flushes** to `IUsageBucketStore` (idempotent upsert path), start/pause/stop lifecycle and diagnostics
  - `Monitoring/NullNetworkMonitor.cs` placeholder provider
- Updated `Flow.Infrastructure.Sqlite/UsageRepository.cs` to implement `IUsageBucketStore.WriteMinuteDeltasAsync(...)` (single-transaction batched upserts).
- Wired daemon (`Flow.Daemon`):
  - `Program.cs` configures DB path (`Flow:DatabasePath` or local app data `flow/flow.db`), runs migrations, registers factory/store/monitor/pipeline/controller, and starts host
  - `Worker.cs` starts pipeline and logs periodic diagnostics/health state
- Verified runtime: `dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj` starts pipeline, migrates/creates SQLite DB, reaches `Monitoring` state with null provider, and shuts down cleanly.

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).

### Notes / gaps
- Write failures currently mark pipeline degraded and drop the failed batch (retry/backoff queue not yet implemented).
- Interface identity normalization, process/app persistence/upserts, and real Linux monitor integration still pending.

## 2026-11-13 (prototype hardening: unattributed + identity caching)

### Completed
- Updated BPF prototype (`prototype/bpf/flow_tcp_bytes.bpf.c`) to count `tgid==0` hook contexts into an explicit **unattributed** map (tx/rx bytes and event counts) instead of dropping them.
- Updated loader (`prototype/bpf/flow_tcp_bytes_loader.c`) to report unattributed totals/deltas/events and to cache `/proc` identity (comm/exe/startticks) on first-seen/alive observations for later snapshots when processes exit.
- Rebuilt BPF object/skeleton and verified loader compiles after changes. (Binaries/objects remain rebuildable artifacts; source committed.)
- User confirmed rerun with fixed `startticks` looked correct (non-zero startticks observed for live processes).

### Gate impact
- Improves transparency (unattributed visibility) and resilience of enrichment across process exit, but **does not** yet satisfy full gate requirements (stable persisted identity model, PID-reuse-proof records, controlled accuracy/overhead report, UDP/namespace/VPN coverage).
