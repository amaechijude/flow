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
- Phase 2 in progress: ingestion/aggregation pipeline, metadata/session persistence, initial historical query APIs, and daemon wiring in place; migrations evolution, production per-process Linux provider adapter, IPC/GUI integration, and broader tests still pending.

## 2026-11-13 (query APIs, procfs monitor, and tests)

### Completed
- Added usage query DTOs and repository methods:
  - `Flow.Contracts.Usage.ApplicationUsageDto`, `UsageTimePointDto`
  - `Flow.Infrastructure.Sqlite.UsageRepository`: `GetTopApplicationsAsync`, `GetUsageTimeSeriesAsync` (grouped 1m bucket series)
- Added `Flow.Linux.Monitoring.ProcNetDevMonitor` (`/proc/net/dev` delta sampling) as an **unattributed interface-level** `INetworkMonitor` fallback (explicit measurement point/attribution note; not per-process attribution).
- `Flow.Daemon` monitor selection: `Flow:Monitor=procfs` uses `ProcNetDevMonitor`, otherwise `NullNetworkMonitor`.
- Expanded infrastructure tests (`SqliteUsageRepositoryTests`) to cover top-app and time-series queries (3 tests total in project).

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal` → **passed** (8 tests total: Domain 1, Application 1, Infrastructure 3, Linux 1, Daemon 1, System 1).

### Notes
- `ProcNetDevMonitor` provides operational end-to-end flow for interface totals only; per-application/per-process attribution remains dependent on validated eBPF/provider work.

## 2026-11-13 (per-process eBPF provider integration - initial)

### Completed
- Enhanced prototype loader `prototype/bpf/flow_tcp_bytes_loader.c` with machine-readable `tsv` mode (`M` meta clk_tck/boot time, `S` snapshots, `E` per-TGID/UID delta records with cached identity, `U` unattributed deltas/events) and rebuilt loader/BPF artifacts locally.
- Implemented `Flow.Linux.Monitoring.EbpfTcpMonitor` (`INetworkMonitor`) integrating the validated TCP eBPF prototype via privileged subprocess loader:
  - parses TSV snapshots, derives process start time from boot time + startticks/clk_tck, emits **attributed per-process delta events** with `ProcessIdentity`/application hints and **explicit unattributed delta events** (`tgid==0`) with documented measurement semantics.
  - configurable via `Flow:Monitor=ebpf|ebpf-tcp` and `Flow:Ebpf:LoaderPath` / `Flow:Ebpf:IntervalSeconds`.
- Documented semantics/limitations in `docs/attribution.md` (TCP hook point, TGID/UID + startticks-based identity, unattributed handling, subprocess/privilege/granularity/coverage caveats).

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal` → **passed** (8 tests total).

### Notes / gaps
- Integration is subprocess-based and requires privileges to load/attach BPF; not an in-process libbpf implementation.
- Attribution is TCP-only (sendmsg/recvmsg return bytes), no per-interface mapping in events, and identity persistence/PID-reuse lifecycle hardening in production storage remains incomplete. Further controlled accuracy/overhead/gate validation still required.

## 2026-11-15 (eBPF daemon ingestion verification)

### Completed
- Fixed ingestion flush-loop bug: `PeriodicTimer.WaitForNextTickAsync` was awaited concurrently across iterations, causing `InvalidOperationException`, degraded state, and no DB flushes despite received/aggregated events (`fdc602a`).
- User-verified end-to-end run with `Flow__Monitor=ebpf` (privileged): per-process attributed TCP usage persisted to SQLite (`usage_buckets` with `measurement_provider='ebpf-tcp'`, process/app keys populated, e.g. `msedge` and `opencode`).

### Observations / follow-ups
- Some 1-minute bucket/process combinations appeared as multiple rows in observed output; investigate aggregation/upsert key behavior and flush coalescing to ensure expected idempotent consolidation.
- Continue gate work: controlled counter/overhead validation, broader protocol/namespace/VPN coverage, and robust PID-reuse-safe persisted identity lifecycle.

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

## 2026-11-15 (usage bucket consolidation correctness fix)

### Completed
- **Diagnosed duplicate-row risk:** `usage_buckets` v1 `UNIQUE(bucket_start_utc,bucket_seconds,granularity,application_key,process_stable_key,interface_id,is_attributed)` does not conflict when nullable key dimensions are `NULL` (SQLite treats NULLs as distinct), so repeated upserts/flushes for the same logical bucket (e.g. unattributed or missing app/process/interface) could insert duplicate rows instead of coalescing. Pipeline in-memory aggregation already coalesces by logical key while buffered, but DB-level idempotent consolidation depended on a conflict target that was not NULL-safe.
- **Migration v2 (`usage_buckets_null_safe_uniqueness`):** consolidates existing `usage_buckets` rows by logical key (`COALESCE` over nullable dimensions), summing `rx/tx` and preserving/coalescing note/provider/hook metadata, then creates `ux_usage_buckets_logical` (unique index on the same NULL-safe expressions) to enforce idempotent consolidation going forward.
- **Repository upserts:** `UpsertMinuteBucketAsync` and `WriteMinuteDeltasAsync` now use `ON CONFLICT(bucket_start_utc,bucket_seconds,granularity,COALESCE(application_key,''),COALESCE(process_stable_key,''),COALESCE(interface_id,''),is_attributed)` so repeated writes for the same 1-minute UTC bucket + dimensions accumulate into one row.
- **Tests:** added `RepeatedWritesWithNullDimensionsCoalesceIntoSingleLogicalBucket` in `tests/Flow.Infrastructure.Tests/SqliteUsageRepositoryTests.cs` (separate writes/flush-equivalent with NULL app/process/interface coalesce to one row with correct totals). Existing infra tests remain valid.

### Test results
- `dotnet test tests/Flow.Infrastructure.Tests/Flow.Infrastructure.Tests.csproj -v minimal` → **passed** (4 tests).
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal` → **passed** (9 tests total: Domain 1, Application 1, Infrastructure 4, Linux 1, Daemon 1, System 1).

### Notes / remaining gaps
- `is_attributed` remains part of the logical key; attributed and unattributed buckets are not merged.
- Failed flush behavior unchanged: pipeline still marks degraded and drops the failed batch (no retry/backoff/durability queue).
- Broader pipeline/integration tests with a fake `INetworkMonitor` (end-to-end batching, diagnostics, multi-flush idempotency across the full ingestion loop) and further provider/identity/gate validation are still pending.

## 2026-11-15 (pipeline integration tests and health-state fix)

### Completed
- Added `tests/Flow.Application.Tests/UsageIngestionPipelineTests.cs` with fake monitors/stores to validate ingestion pipeline behavior without privileged providers or SQLite:
  - event ingestion + 1-minute aggregation + periodic flush produces expected bucket deltas and diagnostics (received/aggregated/batches/flush timestamps),
  - same logical bucket arriving across separate flush cycles is written as repeated deltas for the same key (store-level coalescing/idempotent accumulation model),
  - flush failure records write errors and surfaces degraded state/last error.
- Fixed `UsageIngestionPipeline.PauseMonitoringAsync` to preserve `Degraded`/`Unavailable` health states on pause/stop instead of overwriting them with `Paused`, so fault conditions remain visible (honest health reporting).

### Test results
- `dotnet test tests/Flow.Application.Tests/Flow.Application.Tests.csproj -v minimal` → **passed** (3 tests).
- `dotnet test Flow.slnx -v minimal` → **passed** (11 tests total: Domain 1, Application 3, Infrastructure 4, Linux 1, Daemon 1, System 1).

### Notes / remaining gaps
- Failed flushes are still dropped after being marked degraded (no retry/backoff/durable queue); idempotent upserts make safe retries possible but retry policy is not implemented.
- Metadata/session persistence paths and end-to-end pipeline+SQLite behavior under sustained load/restart remain lightly tested; broader provider identity hardening, IPC, rollups/retention, exports, GUI, and packaging still pending.

## 2026-11-15 (flush retry on transient failure)

### Completed
- Updated `UsageIngestionPipeline.FlushAsync` to snapshot pending usage deltas/metadata, write them, and only commit (subtract/remove) flushed amounts on success. On write failure the pipeline now **retains buffered deltas/metadata for retry** on subsequent flush cycles instead of dropping them, increments write errors, and marks degraded; retries are safe because bucket writes are idempotent/additive (`ON CONFLICT` upsert) and metadata upserts are idempotent.
- Added test `FailedFlushIsRetriedAndPersistsAfterTransientError` (fake store fails once, then succeeds) confirming data is eventually persisted.

### Test results
- `dotnet test tests/Flow.Application.Tests/Flow.Application.Tests.csproj -v minimal` → **passed** (4 tests).
- `dotnet test Flow.slnx -v minimal` → **passed** (12 tests total: Domain 1, Application 4, Infrastructure 4, Linux 1, Daemon 1, System 1).

### Notes / remaining gaps
- Retry is in-memory and timer/flush-driven (no explicit backoff policy, bounded queue beyond existing buffering, or durable write-ahead queue). Sustained outage can grow buffered state; degraded health remains signaled but state is not automatically restored to `Monitoring` after recovery.
- Further correctness/integration work still pending: pipeline+SQLite end-to-end/idempotency under restart, provider/identity hardening and gate validation, daemon query/health services + D-Bus, rollups/retention/exports, GUI, packaging.

## 2026-11-15 (pipeline + SQLite integration coverage)

### Completed
- Added `tests/Flow.Infrastructure.Tests/UsageIngestionPipelineSqliteTests.cs` exercising `UsageIngestionPipeline` end-to-end with a scripted fake monitor and real `SqliteConnectionFactory`/`DbMigrator`/`UsageRepository` (as both `IUsageBucketStore` and `IMonitoringMetadataStore`).
- Verifies: attributed usage, metadata (application/process/interface), and monitoring session rows are persisted; unattributed NULL-dimension deltas emitted across separate flush cycles coalesce into a single logical 1-minute row in SQLite with correct totals (NULL-safe upsert path).

### Test results
- `dotnet test tests/Flow.Infrastructure.Tests/Flow.Infrastructure.Tests.csproj -v minimal` → **passed** (5 tests).
- `dotnet test Flow.slnx -v minimal` → **passed** (13 tests total: Domain 1, Application 4, Infrastructure 5, Linux 1, Daemon 1, System 1).

### Notes / remaining gaps
- Monitoring session records currently store `provider_name` as null (pipeline does not set diagnostics provider name); session lifecycle persistence is covered but provider/version labeling remains a small gap.
- Still pending: health-state recovery after successful retry, durable/backoff flush policy, restart/recovery scenarios, provider/identity hardening and attribution gate evidence, daemon query/health services + versioned D-Bus, rollups/retention/exports, GUI, and packaging.

## 2026-11-15 (reliability fixes and interface preparation)

### Completed
- **Pipeline diagnostics/health:** set `MonitoringDiagnostics.ProviderName` from the monitor implementation when the read loop starts (persisted in monitoring session records) and restore state to `Monitoring` on successful flush after a write-error `Degraded` state (while running), so recovery is reflected honestly. Updated retry integration test to assert recovery/provider labeling.
- **Interface scaffolding:** added `Flow.Application.Queries.IUsageQueryService` and implemented it on `Flow.Infrastructure.Sqlite.UsageRepository`; registered in daemon DI for future IPC/query endpoints. Added `docs/interface-preparation.md` outlining transport-agnostic contract approach, proposed v1 IPC/query/health surface, Avalonia MVVM structure, incremental build order, and open verification items (D-Bus library/topology, Wayland smoke test) consistent with ADR 0002.

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 1 pre-existing warning in `Flow.Application.Tests`).
- `dotnet test Flow.slnx -v minimal` → **passed** (13 tests total).

### Notes / next steps
- D-Bus library and bus/authorization topology remain unverified (ADR 0002 pending); spike before committing to IPC implementation details.
- Next implementation steps: expose daemon read-only status/query service over a thin IPC adapter, add `IDaemonClient` + mock in `Flow.Desktop`, and build read-only Overview/Apps views with explicit health/attribution/measurement caveats.

## 2026-11-15 (interface start: real status/query services and desktop shell)

### Completed
- **Daemon/application services (real, not mocked):** added `IMonitoringDiagnosticsSource` on `UsageIngestionPipeline`, `IMonitoringStatusService`/`MonitoringStatusService` returning `MonitoringStatusDto` (state, provider, last event/flush, counters, last error, health message) from live pipeline diagnostics; registered in daemon DI alongside `IUsageQueryService`.
- **Contracts:** extended `MonitoringStatusDto` with optional diagnostics fields (timestamps, counters, last error) while preserving existing fields.
- **Desktop (Avalonia) initial shell:** added `Flow.Desktop` references to Infrastructure and wired real local SQLite reads via `SqliteConnectionFactory`/`UsageRepository`/`IUsageQueryService` -> `IUsageQueryClient` (`LocalUsageQueryClient`) using the same default DB path/env var as the daemon. This surfaces **real persisted historical data** (last 24h summary and top applications) when present.
- **Live daemon status (truthful, no fabricated metrics):** added `IDaemonStatusClient` with `PendingIpcDaemonStatusClient` reporting `Unavailable`/"daemon IPC not connected" until versioned IPC is implemented; no live in-process daemon metrics are invented.
- **UI:** replaced template window with initial Overview layout showing daemon status (truthful), last-24h range/summary with attributed vs unattributed totals, top applications from DB, and explicit attribution/measurement caveats; refresh command and empty-state messaging included.

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 1 pre-existing warning in `Flow.Application.Tests`).
- `dotnet test Flow.slnx -v minimal` → **passed** (13 tests total).

### Notes / next steps
- Desktop currently reads historical data directly from the local DB file (read path) and cannot observe live daemon pipeline diagnostics without IPC; this is intentional and explicit. Next: spike/choose D-Bus transport (ADR 0002), expose daemon status+query over versioned IPC, and replace `PendingIpcDaemonStatusClient` with a real IPC client while keeping data real.
- UI needs follow-up for proper byte formatting in app rows, time-range selection, process/interface views, charts, and GNOME/Wayland accessibility/visual polish.

## 2026-11-15 (D-Bus IPC scaffolding: daemon service and desktop client)

### Completed
- **Versioned IPC contract (real, not mocked):** added `Flow.Contracts.Ipc` with `FlowIpc` (service/object/interface names, contract version), `IpcJson` serializer settings, and shared Tmds.DBus interface `IFlowDaemonDbus` (`GetContractVersionAsync`, `GetStatusJsonAsync`, `GetUsageSummaryJsonAsync`, `GetTopApplicationsJsonAsync`, `GetUsageTimeSeriesJsonAsync`) using JSON-serialized DTO payloads for transport stability.
- **Daemon IPC host:** added `FlowDaemonDbusService` backed by real `IMonitoringStatusService` and `IUsageQueryService`, and `DbusIpcHostedService` (registered in daemon) that connects to session/system bus when available, registers `org.flow.Daemon` at `/org/flow/Daemon` (`org.flow.Daemon1`), and degrades gracefully with warnings if D-Bus is unavailable or registration fails.
- **Desktop IPC client:** added `DbusDaemonClient` (real D-Bus proxy calls), `HybridDaemonStatusClient` (real daemon status via IPC or truthful unavailable when unreachable), and `HybridUsageQueryClient` (prefers real daemon IPC query API, falls back to direct local SQLite reads of the same persisted data if IPC is unavailable). `App.axaml.cs` now uses these clients.
- **Dependencies:** added `Tmds.DBus` (0.95.2) to `Flow.Contracts`, `Flow.Daemon`, and `Flow.Desktop`.

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 1 pre-existing warning in `Flow.Application.Tests`).
- `dotnet test Flow.slnx -v minimal` → **passed** (13 tests total).

### Notes / limitations
- IPC is a first scaffold: JSON-over-D-Bus methods, no signals/subscriptions yet, no Polkit/authorization on control operations (none exposed), and session-bus registration may conflict in multi-instance scenarios. Desktop falls back to local DB reads for historical queries when daemon IPC is unavailable (same real data, different access path) and reports live status as unavailable rather than fabricating it.
- Needs on-target validation (Ubuntu 26.04 GNOME Wayland, session/system bus behavior, service naming/policy files) and ADR 0002 follow-up to confirm/pin the D-Bus approach.

## 2026-11-15 (on-target IPC validation - user reported)

### Completed
- User reported D-Bus IPC appears to be working on-target (daemon registration/calls and desktop connectivity path). Detailed command outputs/evidence and final session-vs-system bus/activation/policy decision should still be captured in `docs/progress.md` and used to supersede ADR 0002.

### User-provided UI evidence (2026-11-15, converted from screenshot)
- Desktop Overview showed: **State: Monitoring (provider: ProcNetDevMonitor)**, "Monitoring", refresh available; **Last 24 hours (UTC): 2026-11-14 04:18:48Z – 2026-11-15 04:18:48Z**; **Attributed ↓ 0 B ↑ 0 B | Unattributed ↓ 15.52 MiB ↑ 7.97 MiB | Total ↓ 15.52 MiB ↑ 7.97 MiB**; top application **(unattributed)** with `Attributed ↓ 0 ↑ 0 | Unattributed ↓ 16276344 ↑ 8357416`.
- Interpretation: confirms daemon↔desktop status/query path working and displaying real persisted data with honest attribution semantics for `procfs` (interface-level totals reported as unattributed). Detailed `busctl`/introspection command outputs and final session-vs-system bus/activation/policy decision still to be captured for ADR 0002 supersession.

## 2026-11-15 (step 1: D-Bus IPC on-target validation and shutdown fix)

### Completed
- **On-target validation (Ubuntu 26.04.1 LTS, GNOME Wayland, session bus `unix:path=/run/user/1000/bus`, .NET 11.0.100):** ran daemon with `Flow__Monitor=procfs` (temp DB) and confirmed session-bus IPC:
  - Log: `Flow D-Bus IPC started at ... service=org.flow.Daemon path=/org/flow/Daemon`.
  - `busctl --user list` showed `org.flow.Daemon` owned; `busctl --user introspect org.flow.Daemon /org/flow/Daemon` exposed `org.flow.Daemon1` methods (`GetContractVersion`, `GetStatusJson`, `GetUsageSummaryJson(sss)->s`, `GetTopApplicationsJson(sssi)->s`, `GetUsageTimeSeriesJson(ssss)->s`) plus standard D-Bus interfaces.
  - `GetContractVersion` returned `i 1`.
  - `GetStatusJson` returned real pipeline diagnostics JSON (e.g. `state=Monitoring`, `providerName=ProcNetDevMonitor`, event/flush counters/timestamps, `writeErrors=0`) — not fabricated.
  - `GetUsageSummaryJson`/`GetTopApplicationsJson`/`GetUsageTimeSeriesJson` returned real query JSON (`{"...rxBytes":0,...}` / `[]` for a fresh DB/window) via the daemon query path.
  - After daemon stop, name was absent and calls failed with `ServiceUnknown` (truthful unreachable state); user-provided desktop evidence (earlier entry) showed connected Monitoring/procfs status and real unattributed historical totals.
- **Shutdown fix (discovered during validation):** made `UsageIngestionPipeline` stop/dispose idempotent (guard `_cts` cancel/dispose), and treat flush cancellation during shutdown as normal (retain buffered state without marking degraded/write-error) to avoid noisy/unhandled shutdown exceptions.
- **Decision recorded:** added `docs/decisions/0003-dbus-ipc-session-bus-v1.md` (supersedes ADR 0002 IPC choice) selecting `Tmds.DBus` 0.95.2, session-bus per-user v1 topology/names, JSON-over-D-Bus request/response contract; system-bus activation/policy/auth remain follow-up.

### Test results
- `dotnet test tests/Flow.Application.Tests/Flow.Application.Tests.csproj -v minimal` → **passed** (4 tests).
- `dotnet build Flow.slnx -v minimal` / `dotnet test Flow.slnx -v minimal` run before commit (see commit verification).

### Notes / follow-ups
- Earlier parallel validation attempt killed the daemon during in-flight query calls, producing `recipient disconnected` for those calls; sequential re-run confirmed query methods work. Desktop connected-vs-disconnected behavior is evidenced by hybrid client design + user UI evidence + `ServiceUnknown` on stopped daemon; automated IPC integration tests remain a follow-up.
- IPC remains request/response only (no signals), no control operations/Polkit, and no system-bus policy/activation artifacts yet.

## 2026-11-15 (step 2: interface usefulness - ranges, formatting, interfaces/processes)

### Completed
- **UI (real data, no fabricated metrics):** added rolling time-range selection (1h/6h/24h/7d/30d, UTC), consistent byte formatting, and clearer status/data-source/attribution notes in `Flow.Desktop` Overview. Top applications now show formatted attributed/unattributed/total values.
- **Query/API expansion (end-to-end):** added `InterfaceUsageDto` and `ProcessUsageDto`, extended `IUsageQueryService`/`UsageRepository` with interface and process aggregations (joined to metadata tables where available), and exposed them through daemon D-Bus service, desktop D-Bus client, and local-DB fallback path. Bumped IPC `ContractVersion` to **2** and added `GetInterfaceUsageJsonAsync` / `GetTopProcessesJsonAsync`.
- **UI views:** added Interfaces and Top Processes sections backed by real query results, with explicit caveats (per-interface mapping may be unavailable for some providers; process identity/lifecycle hardening ongoing).
- **Tests:** added `InterfaceAndProcessUsageQueriesReturnAggregatedData` in `Flow.Infrastructure.Tests`.

### Test results
- `dotnet test tests/Flow.Infrastructure.Tests/Flow.Infrastructure.Tests.csproj -v minimal` → **passed** (6 tests).
- `dotnet test Flow.slnx -v minimal` → **passed** (14 tests total: Domain 1, Application 4, Infrastructure 6, Linux 1, Daemon 1, System 1).

### Notes / caveats
- ebpf-tcp events currently do not provide per-interface attribution, so interface breakdowns may show unknown/unmapped buckets depending on provider; process identity remains stable-key based but full PID-reuse/lifecycle hardening and richer process metadata are still pending.
- Desktop still prefers daemon IPC for live status/queries and falls back to direct local DB reads for historical data when IPC is unreachable (same persisted data, disclosed). No live metrics are fabricated.

## 2026-11-15 (next UI/IPC increments: filtering, time series, reconnect, contract tests)

### Completed
- **Desktop (real data):** added interface filter (applies to summary/apps/processes/time series; interface breakdown remains unfiltered for the selected range), additional rolling ranges, and a "Usage over time" view showing recent 1-minute bucket points (real persisted data) with totals/rx/tx formatting.
- **IPC client hardening:** `DbusDaemonClient` now retries a call once after resetting the connection/proxy on failure to improve reconnect behavior when the daemon restarts or the bus connection drops (no fabricated data; failures still surface as truthful unavailable/fallback behavior in hybrid clients).
- **Tests:** added `IpcContractSerializationTests` to verify JSON round-tripping of key IPC DTO payloads.

### Test results
- `dotnet test Flow.slnx -v minimal` → **passed** (16 tests total: Domain 1, Application 4, Infrastructure 8, Linux 1, Daemon 1, System 1).

### Notes / remaining next steps
- No live D-Bus change/status signals yet (poll/refresh model), no automated daemon↔client IPC integration test, and no custom calendar date picker/time-zone-aware calendar range UX.
- Charting is textual/list-based (real buckets), not a graphical chart library (Avalonia charting dependency/Wayland fit still to verify if added).
- Provider/identity/gate hardening (interface mapping for ebpf, PID-reuse/event-time identity, UDP/namespace/VPN/virtual-interface semantics, controlled accuracy/overhead evidence) and rollups/retention/exports/packaging remain pending.

## 2026-11-18 (UI ranges and CSV export)

### Completed
- **Desktop UI (real data):** added `Today`/`Yesterday` calendar ranges (local day boundaries converted to UTC `[start,end)`) and a `Custom range` mode with date pickers and apply action; range computation centralized for refresh/export.
- **CSV export:** added "Export CSV" action exporting the currently selected range/interface-filter view (summary, applications, processes, interfaces, 1-minute time series) to `~/flow-export-<utc-timestamp>.csv` with generation time, range/TZ context, filter, and attribution/measurement caveats in headers. Exports use real persisted/query results (no fabricated data).
- **Docs:** progress updated. (IPC contract is v2 in implementation; ADR 0003 text still references initial v1 methods and should be reconciled in a follow-up ADR/update.)

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 1 pre-existing warning).
- `dotnet test Flow.slnx -v minimal` → **passed** (16 tests total).

### Notes / remaining next steps
- Export writes to a fixed user-home path (no file picker yet). Graphical charts, live D-Bus signals, automated IPC integration tests, retention/rollups, and provider/identity/gate hardening remain pending.

## 2026-11-18 (UI shell/pages, graphical charts, picker exports, IPC v3 signals, integration tests)

### Completed
- **Desktop shell and pages (real data only):** replaced the single Overview window with a fixed header + sidebar shell and four pages — Overview, Apps & Processes (`flow_designs/apps_and_proceses`), Network Interfaces (`flow_designs/network_interface`), Diagnostics & Daemon (`flow_designs/diagnostics`). Shared header shows truthful daemon state, IPC/signal state, range and interface filters, refresh, CSV/JSON export. Dark "Obsidian Telemetry" palette and card/pill/nav/table styles in `Styles/FlowStyles.axaml`.
- **Shared snapshot orchestration:** `MainViewModel` performs one coherent refresh and passes the same real `UsageSnapshot` to all page view models. Pages add presentation only: sortable columns, search filters, attributed/unattributed filters, selection/detail panels. Header totals are not recomputed from filtered rows.
- **Graphical charts (no new dependency):** added custom `TimeSeriesChart` (RX/TX dual-series area chart from persisted 1-minute buckets), `SplitBar` (RX/TX and attributed/unattributed), and `UsageBar` (row share), rendered with Avalonia vector primitives. Rationale recorded in **ADR 0004**; avoids unverified chart packages on the Skia/Wayland path. Bucket list remains the textual/accessible source of truth.
- **Exports:** CSV retained and JSON added; both include range/filter/generation time, data source/provider, and attribution/measurement/identity/coverage caveats. Destinations use the Avalonia `StorageProvider.SaveFilePickerAsync`; `Flow__ExportDirectory` overrides the picker for automation.
- **IPC contract v3 + status signals:** `FlowIpc.ContractVersion = 3`; new `StatusChanged` D-Bus signal carrying JSON `StatusChangedSignalDto(ObservedUtc, Reason, Status)`. The daemon polls real diagnostics every 2s and publishes only when the serialized snapshot changes (`initial`/`changed`), so signals are coalesced. `DbusDaemonClient` subscribes best-effort, exposes signal state, and treats signals as a refresh hint (authoritative values are re-queried). Older daemons without the signal remain usable.
- **Automated daemon↔client tests:** `tests/Flow.SystemTests/DbusIntegrationTests.cs` starts a private `dbus-daemon`, registers the real `FlowDaemonDbusService`, and drives the real `DbusDaemonClient`: contract v3, JSON round-trip for every query method over real D-Bus, signal delivery, and publisher coalescing (stable snapshots do not repeat signals) using the real hosted service. Added `StatusChangedSignalDto` JSON round-trip coverage in `IpcContractSerializationTests`. Test-only seams: hosted service `AddressOverride`/`StatusPollInterval` (internal).
- **Docs:** added ADR 0004 (shell/pages, custom charts, exports, contract v3 signals, private-bus tests); noted contract evolution in ADR 0003.

### Verification / test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal --no-build` → **passed 20/20** (Domain 1, Application 4, Infrastructure 9, Linux 1, Daemon 1, System 4).
- **On-target session-bus check (non-root, `Flow__Monitor=procfs`, temp DB):** `busctl --user introspect org.flow.Daemon /org/flow/Daemon` exposed contract v3 methods plus `.StatusChanged signal s`; `GetContractVersion` returned `i 3`; `busctl --user monitor org.flow.Daemon` observed `StatusChanged` JSON payloads roughly every 2s with real pipeline counters/state (`ProcNetDevMonitor`, `state=Monitoring`, increasing `eventsReceived`/`eventsAggregated`), confirming the production signal path and coalescing interval.
- Non-root desktop smoke: launched the built app in the existing GNOME/Wayland session; it stayed running for the smoke window with no startup/XAML errors. This is a launch smoke check, not a screenshot/a11y verification; chart interaction and full Wayland/accessibility conformance remain unverified.

### Notes / remaining gaps
- Signals are best-effort and coalesced (no replay; up to ~2s detection latency). Reconnect/resubscribe after daemon restart and error mapping are still lightly exercised.
- Charts are intentionally minimal (no zoom/tooltip/per-point inspection) and not a11y-verified; the textual bucket list remains the honest source of detail.
- Private-bus integration tests are Linux-only and require `dbus-daemon`; they do not cover system-bus activation/policy/Polkit or privileged-daemon topology.
- Provider/identity/gate hardening, rollups/retention, Reports/Settings pages, and packaging remain pending.
