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

## 2026-11-18 (attribution identity hardening: event-time identity + PID-reuse safety)

### Completed
- **Event-time identity in BPF (ADR 0005):** changed the TCP map key from `(tgid, uid)` to `{start_ns, tgid, uid}`, where `start_ns` is `task_struct.start_time` read via CO-RE at byte-event time. The value now captures first-observed `cgroup_id` and `comm`, so short-lived processes retain identity after exit and PID reuse creates a distinct counter row instead of merging generations. Removed the unused `traffic_by_pid` map.
- **Loader identity verification:** extended the TSV `E` line backward-compatibly with `start_ns` and `cgroup_id`; the loader uses `/proc` `comm`/`exe` only when live start ticks match the event-time start time (±2 ticks), caches identity per process generation, and otherwise keeps kernel-captured `comm` with `exe` unknown. Non-TSV output labels unverified/exited identity explicitly.
- **Parser/provider integration:** extracted `EbpfTcpSnapshotParser` (internal, testable). It accepts 11-field legacy lines, prefers `start_ns` over `startticks` for `StartTimeUtc`, populates `ProcessIdentity.CgroupId`, and emits honest attribution notes for unverified/exited identities and unknown executables. `EbpfTcpMonitor` now delegates TSV parsing to it.
- **Stable-key semantics:** `ProcessIdentity.StableKey` is now `pid:startUnixMs:uid`; executable path is enrichment metadata rather than key material, so a process generation cannot split when exe becomes known later (ADR 0005 documents the migration/legacy-key consequence).
- **Tests:** replaced the placeholder Linux test with 11 parser/identity tests (extended line + cgroup + event-time start, PID reuse distinct stable keys, stable key ignores exe enrichment, legacy loader compatibility, exited-process comm fallback/notes, malformed/zero-delta handling, unattributed parsing, metadata defaults).
- **Controlled validation harness:** added `scripts/validate-ebpf-attribution.py` (root-only) to run a known-size loopback TCP transfer, compare per-process attributed TX/RX with payload and `/proc/net/dev` `lo` counters, record unattributed bytes/loader CPU/peak RSS, and write a Markdown report under `docs/measurements/`.
- **Build verification:** rebuilt `flow_tcp_bytes.bpf.o`, regenerated `flow_tcp_bytes.skel.h`, and compiled the loader with clang/bpftool/libbpf (0 errors; only pre-existing `vmlinux.h` warnings). Build artifacts remain untracked.
- **Docs:** added ADR 0005; updated `prototype/bpf/README.md` (identity semantics, TSV protocol, controlled validation) and `docs/attribution.md` (implemented identity behavior, unit-test evidence, explicit remaining gate gaps).

### Test results
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 code warnings/errors; 6 `NU1900` package-vulnerability warnings because the NuGet service index was unreachable).
- `dotnet test Flow.slnx -v minimal` → **passed 31/31** (Domain 1, Application 4, Infrastructure 9, Linux 12, Daemon 1, System 4).

### Not verified yet / next
- Privileged live run of the modified BPF/loader was **not** performed in this increment (requires root/CAP_BPF; no non-interactive privilege path). Compile-only verification is recorded above.
- Controlled accuracy/overhead evidence still requires running `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` (and variants) and reviewing the generated report.
- UDP/QUIC, per-interface attribution, namespaces/container mapping, VPN/tunnel/virtual-interface behavior, map expiry for dead generations, and production packaging of the privileged provider remain open gate items.

## 2026-11-18 (controlled validation run and thread-identity fix)

### Completed
- **First privileged controlled run recorded:** 16 MiB loopback TCP transfer measured 16,777,216 TX and 16,777,216 RX for the expected process (100.0% of payload), 0 unattributed bytes, `lo` deltas 16,793,734 per direction (IP/link headers), loader CPU ~0.01 s (0.242% of one core), peak RSS 3,592 KiB. Report: `docs/measurements/ebpf-controlled-20261118T042842Z.md`. CPU sampling is coarse and not accepted as overhead evidence.
- **Found an identity defect:** the run split one Python TGID into two rows with different `start_ns` (main thread and server thread); the server-thread row failed live `/proc` verification. Root cause: `task_struct.start_time` is per-thread, while `/proc/<tgid>/stat` starttime is the thread-group leader's. This is exactly the kind of split the stable key must prevent.
- **Fix:** BPF now keys `start_ns` by `task_struct.group_leader->start_time` (with a null-safe fallback), so all threads of one process share one generation. Rebuilt the BPF object/skeleton/loader successfully.
- **Harness hardening:** `scripts/validate-ebpf-attribution.py` now identifies the expected load-generator TGID, compares only that process's attributed TX/RX to payload, reports background traffic separately, asserts exactly one process generation and verified identity for the expected TGID, uses `/proc/<pid>/schedstat` for higher-resolution CPU time, and chowns reports back to the invoking user when run via sudo.
- **Docs:** ADR 0005, `prototype/bpf/README.md`, and `docs/attribution.md` now record the per-thread pitfall, the group-leader fix, the first-run result (useful byte totals, not accepted gate evidence), and the remaining rerun requirement.

### Verification / test results
- BPF object/skeleton/loader rebuilt with clang/bpftool/libbpf (0 errors; pre-existing `vmlinux.h` warnings).
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 6 `NU1900` offline warnings + 1 pre-existing `CA1859` test warning).
- `dotnet test Flow.slnx -v minimal --no-build` → **passed 31/31**.

### Not verified yet / next
- Rerun the privileged validation after the group-leader fix: `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16`. The run should now show one row for the expected TGID with `identity verified: yes`; commit that report before making accuracy/overhead claims.
- The stale first report remains untracked evidence of the defect; ownership may need `sudo chown -R "$USER:$USER" docs/measurements`.

## 2026-11-18 (accepted controlled validation after group-leader identity fix)

### Completed
- **Accepted rerun:** `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` produced `docs/measurements/ebpf-controlled-20261118T044634Z.md`.
  - Expected process TGID 47748 appeared as **one row** with `identity verified: yes`, executable `/usr/bin/python3.14`, cgroup `24677`, and attributed TX/RX of exactly **16,777,216** each (100.0% of payload).
  - Background traffic was separated correctly: VS Code (`code`, TGID 44356) contributed 2,777 TX / 10,108 RX and was also identity-verified.
  - Unattributed TX/RX: **0**.
  - `lo` deltas: 16,796,684 per direction (IP/link headers/ACKs above payload).
  - Loader overhead datapoint: 0.004 s CPU (0.097% of one core), peak RSS 3,672 KiB.
  - All harness checks passed, including one process generation and verified identity for the expected TGID.
- **Interpretation:** confirms TCP payload accounting and process-generation identity (including multithreaded traffic) on this kernel for a controlled loopback transfer. It does not establish physical-interface, UDP/QUIC, namespace/container, VPN/tunnel, virtual-interface, or sustained-load behavior; the CPU/RSS numbers are a single short-run datapoint.
- **Reports:** committed both the rejected first run (annotated) and the accepted rerun under `docs/measurements/`, with a README explaining status. Updated `docs/attribution.md` and ADR 0005 accordingly.

### Test results
- No product code changed in this step; prior `dotnet build Flow.slnx -v minimal` / `dotnet test Flow.slnx -v minimal` baseline remains **31/31 passing**.

### Next
- Per-interface correlation/assessment and UDP/QUIC coverage are now the largest attribution-gate gaps.
- Run a longer/high-connection-count validation for overhead and map behavior before making steady-state overhead claims.

## 2026-11-18 (TCP+UDP and per-interface attribution increment)

### Completed (implementation; privileged validation still pending)
- **BPF coverage:** added `fexit/udp_sendmsg` and `kprobe`/`kretprobe` `udp_recvmsg` alongside the existing TCP hooks. TCP/UDP receive probes stash `struct sock *` by `bpf_get_current_pid_tgid()` for the return probe. QUIC is counted as UDP payload; framing is not identified.
- **Per-interface key:** the BPF key is now `{start_ns, tgid, uid, ifindex, proto}` (`proto` 1=TCP, 2=UDP). `ifindex` resolves from `skc_bound_dev_if`, then `sk_rx_dst_ifindex`, then `sk_dst_cache->dev->ifindex`; `0` means unknown.
- **Loader:** extended TSV `E` line with `proto` and `ifname`, resolves names with `if_indextoname`, prints PROTO/IFACE in human mode, and attaches all TCP+UDP programs (partial attach failures are reported rather than silently ignored). Skeleton regenerated with `bpftool`; loader compiled with gcc/libbpf.
- **Provider:** renamed `EbpfTcpMonitor`/`EbpfTcpMonitorOptions` to `EbpfMonitor`/`EbpfMonitorOptions` and parser to `EbpfSnapshotParser`. Provider name is now `ebpf` with a TCP+UDP measurement description; `ebpf-tcp` remains a configuration alias. Historical prototype filenames are unchanged.
- **Parser:** accepts the 15-field `E` line, sets `NetworkUsageEvent.InterfaceName` when the interface resolved, keeps `null` plus an explicit note for `ifindex 0`, and remains compatible with 11-field and 13-field lines.
- **Export caveats:** updated desktop JSON/CSV caveats to describe TCP+UDP, per-interface resolution, QUIC-as-UDP, and unresolved-interface grouping. Noted that persisted minute buckets aggregate TCP+UDP.
- **Provider label migration:** new rows use `measurement_provider='ebpf'`; existing database rows keep `ebpf-tcp` and are not rewritten, so mixed historical/new labels are expected during development.

### Verification / test results
- BPF object, skeleton, and loader compile (`clang`/`bpftool`/`gcc`; 0 errors, only pre-existing `vmlinux.h` warnings).
- `dotnet build Flow.slnx -v minimal` → **succeeded** (0 errors; 7 pre-existing analyzer warnings).
- `dotnet test Flow.slnx -v minimal --no-build` → **35/35 passed** (Domain 1, Application 4, Infrastructure 9, Linux 16, Daemon 1, System 4).
- New parser tests cover resolved `lo` interface + TCP, unresolved UDP interface note, and 13-field event-time compatibility. A monitor test verifies that a failing loader surfaces its exit code/stderr instead of silently ending the event stream.
- `scripts/validate-ebpf-attribution.py` now runs TCP then UDP in the same process, sums expected-process rows per protocol and per interface, and asserts `lo` attribution. Syntax-checked with `py_compile`; **not run with privileges**.

### Pending / next
- **Privileged rerun required:** rebuild artifacts, then run `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16`. Review per-protocol totals, interface rows, unresolved-interface count, unattributed bytes, and CPU/RSS before accepting UDP/interface claims.
- If UDP RX or socket-interface resolution fails/under-reports, record the failure and adjust semantics rather than widening claims. Unconnected/uncached UDP sockets are a known candidate for `ifindex=0`.
- Still open: namespaces/containers, VPN/tunnel/virtual-interface behavior, physical interfaces, sustained/high-connection overhead, map expiry, and protocol-level persistence.

## 2026-11-18 (TCP+UDP controlled run: byte accuracy accepted, UDP RX interface failed)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` produced `docs/measurements/ebpf-controlled-20261118T050933Z.md`.
- TCP: exactly **16,777,216 TX / 16,777,216 RX** (100.0% of payload), interface `lo`, one verified process generation, 0 background/unattributed bytes.
- UDP: exactly **16,777,216 TX / 16,777,216 RX** (100.0% of payload; 100.0% delivery), one verified generation, 0 background/unattributed bytes.
- UDP TX interface: `lo`. **UDP RX interface: unresolved (`ifindex=0`) for all 16,777,216 bytes.** The harness correctly failed `check_interface`.
- Loader overhead: 0.005 s CPU (0.076% of one core), peak RSS 3,900 KiB (short-run only).
- Interpretation: TCP+UDP **byte accuracy** is accepted for this controlled loopback run. Per-interface attribution is accepted only for TCP and UDP TX, not UDP RX.

### Fix in response (not yet privileged-run)
- Added `kretprobe/__skb_recv_udp`: captures the dequeued skb's `skb_iif` keyed by `pid_tgid`. `udp_recvmsg_exit` consumes/deletes it and uses it as the RX interface hint before falling back to socket-route state. This is per-datagram, avoiding a wrong "last seen" interface for wildcard-bound sockets receiving on multiple interfaces.
- Loader now requires this hook to attach too (all-or-nothing attach).
- Updated measurement-point/hook descriptions and docs; regenerated skeleton and rebuilt the loader.
- Non-privileged verification: BPF/skeleton/loader compile with 0 errors; build succeeds; tests pass **35/35**. **Privileged rerun pending.**

### Next
- Rerun `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16`. Expect the UDP RX row to resolve to `lo` and `check_interface` to pass. If `skb_iif` is still unavailable, record that failure and keep UDP RX interface attribution explicitly unproven.

## 2026-11-18 (accepted TCP+UDP per-interface rerun)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` produced `docs/measurements/ebpf-controlled-20261118T084900Z.md`.
- TCP: **16,777,216 TX / 16,777,216 RX**, interface `lo`.
- UDP: **16,777,216 TX / 16,777,216 RX**, interface `lo` (previously unresolved before the `skb_iif` fix).
- One verified process generation for the expected TGID; 0 unresolved-interface bytes; 0 unattributed bytes.
- Background traffic correctly separated: two `opencode` rows (18 RX / 18 TX), both identity-verified, on `lo`.
- All harness checks passed, including `check_interface`.
- Loader overhead: 0.005 s CPU (0.075% of one core), peak RSS 3,880 KiB (short-run only).

### Interpretation
- The controlled loopback case now validates TCP and UDP byte accuracy **and** per-interface attribution for TX and RX, including the unconnected UDP receive path.
- Scope remains narrow: no physical interface, namespace/container, VPN/tunnel, virtual-interface, wildcard multi-interface socket, high-connection-count, or sustained-overhead evidence. Persisted minute buckets still aggregate TCP+UDP (protocol is not a persistence dimension).

### Changes recorded
- Accepted/annotated the new report, updated `docs/measurements/README.md`, `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md` accordingly.
- No product code changed in this documentation/evidence step; the build/test baseline remains **35/35 passing**.

## 2026-11-18 (two-interface validation harness implemented, not yet run)

### Completed
- Extended `scripts/validate-ebpf-attribution.py` with `--mode veth` (default remains the accepted `lo` mode).
  - Creates a temporary veth pair `flowvA` (192.0.2.1/32) / `flowvB` (192.0.2.2/32) via `ip`, refuses to touch pre-existing device names, and deletes the pair in a `finally` block.
  - Client sockets bind to `flowvA` / 192.0.2.1 and server sockets to `flowvB` / 192.0.2.2 using `SO_BINDTODEVICE`, so TX and RX exercise distinct interfaces while staying in one process and one network namespace.
  - Per-protocol summary now records TX and RX interface names separately and unresolved TX/RX bytes; the interface gate requires TCP and UDP TX on `flowvA` and RX on `flowvB`.
  - Report includes per-role `/proc/net/dev` deltas, expected-interface description, mode, and mode-specific caveats.
  - Default loopback mode output remains compatible with earlier accepted reports.

### Verification (non-privileged)
- `py_compile` succeeded; synthetic report generation succeeded.
- TCP and UDP transfer smoke tests with distinct source addresses (127.0.0.1 / 127.0.0.2) delivered 1 MiB each in full.
- No product code changed; build/test baseline remains **35/35 passing**.

### Pending
- Privileged `--mode veth` run: `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth`.
- Expectation: expected-process TX rows on `flowvA`, RX rows on `flowvB`, both protocols exact payload, one verified generation, 0 unresolved/unattributed. If RX resolves to `lo` or `-`, record the measured result instead of claiming two-interface coverage; that would indicate local-delivery short-circuiting or interface-resolution scope limits.
- Cross-network-namespace interface naming remains unhandled (`ifindex` is namespace-scoped); document rather than guess.

## 2026-11-18 (veth mode first attempt failed: no route)

### Measured failure
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth` failed at `client.connect()` with `OSError [Errno 113] No route to host`. The veth pair was cleaned up by the harness.
- Cause: using two /24 addresses in the same subnet on two veth devices in one namespace created duplicate connected routes. With `SO_BINDTODEVICE` restricting egress to one device, route lookup could not select a valid path to the other endpoint.

### Fix (not yet rerun)
- Veth endpoints now use /32 addresses plus explicit onlink peer routes in both directions.
- Transfer `OSError`s now produce a diagnostic SystemExit instead of a bare traceback.
- No BPF/loader code changed; baseline remains 35/35 passing.

## 2026-11-18 (veth route flag rejected)

### Measured failure
- The second veth attempt failed during setup: `ip route add 192.0.2.2/32 dev flowvA onlink` → `Invalid flags for nexthop - PERVASIVE and ONLINK can not be set.` The pair was cleaned up.

### Fix (not yet rerun)
- Removed the `onlink` flag (verified plain `ip route add ... dev ...` syntax is accepted by this iproute2).
- Added permanent neighbor entries using each veth peer's MAC from `/sys/class/net/<dev>/address`, so local addresses do not depend on ARP replies.
- Docs updated; no BPF/loader change; baseline remains 35/35 passing.

## 2026-11-18 (multi-interface probe: netns topology + namespace-scoped interfaces)

### Measured failures of the same-namespace veth approach
- First attempt: `client.connect()` → `OSError [Errno 113] No route to host` (duplicate /24 connected routes on two veth devices in one namespace).
- Second attempt: `ip route add ... onlink` → `Invalid flags for nexthop - PERVASIVE and ONLINK can not be set` on this kernel.
- Third attempt with /32 routes and static neighbors: controlled transfer timed out. Local delivery short-circuits traffic addressed to a local IP, so the server socket bound to the peer device never received it. Same-namespace veth cannot reliably exercise two interfaces for local addresses.

### Current implementation (not yet privileged-run)
- `--mode veth` now creates a temporary namespace `flowvns` and veth pair: `flowvA` 192.0.2.1/24 stays in the root namespace with the server; `flowvB` 192.0.2.2/24 moves into `flowvns` with a client helper. Traffic must traverse the veth pair.
- Loader emits a 16th `E` field `netns_id` (from `/proc/<pid>/ns/net`) and reports interfaces in other namespaces as `ifindex@netns`, since `ifindex` is namespace-scoped. The BPF key is unchanged; identical keys in different namespaces would still merge (documented limitation).
- Parser accepts the optional field, records `ifindex@netns` as the interface id, and adds an explicit attribution note.
- Harness groups expected rows across the root server and both client helpers, checks one generation per TGID, and verifies RX on `flowvA` in the root netns plus TX on `flowvB`'s netns-local ifindex for the helper. Helpers stay alive for snapshots via a stdin hold.
- Cleanup deletes the namespace and veth pair in `finally`.

### Verification (non-privileged)
- BPF object unchanged; loader recompiled with 0 errors; `py_compile` and synthetic report/summarize checks passed.
- `dotnet build Flow.slnx` succeeded; `dotnet test Flow.slnx` → **36/36 passed** (Linux 17).

### Pending
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth`.
- Expectation: exact TCP/UDP byte totals, one generation per TGID, root RX on `flowvA`, helper TX on `<ifindex>@<netns>`, all checks true. Any other result must be recorded as measured rather than claimed as two-interface coverage.

## 2026-11-18 (cross-netns veth bind-address fix)

### Measured failure
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth` failed at `server.bind((host, 0))` with `OSError [Errno 99] Cannot assign requested address`.
- Cause: the netns topology leaves 192.0.2.2 (flowvB) inside `flowvns`; the root namespace only has 192.0.2.1 (flowvA). The harness was binding the root server to the peer-namespace address.

### Fix (not yet rerun)
- Root TCP/UDP servers now bind to `VETH_CLIENT_IP` (192.0.2.1); the client helper in `flowvns` connects to that address over the veth pair.
- Docstrings clarify that `host` is the root-side address.
- No BPF/loader change; `py_compile` passed; baseline remains 36/36 passing.

### Incidental report review (09:42Z loopback rerun)
- A default-mode report `docs/measurements/ebpf-controlled-20261118T094231Z.md` existed in the worktree and was reviewed: all loopback checks true with the new netns-id field.
- It also shows background traffic attributed to physical `wlp0s20f3` for code/opencode/systemd-resolved. This is real observed output but **not** controlled physical-interface validation; it is annotated as incidental in the report index.

## 2026-11-18 (accepted two-interface veth validation)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16 --mode veth` produced `docs/measurements/ebpf-controlled-20261118T101857Z.md`.
- TCP: **16,777,216 TX / 16,777,216 RX**; UDP: **16,777,216 TX / 16,777,216 RX**; 100.0% UDP delivery.
- Root server TGID 59914: TCP/UDP RX on `flowvA` (root netns).
- Client helpers TGID 59958 (TCP) and 59959 (UDP): TX on `22@4026532907` (flowvB's netns-local ifindex in `flowvns`).
- One verified process generation per expected TGID; expected-process unresolved bytes 0; unattributed 0. Background `python3`/`avahi-daemon` UDP rows had unresolved interfaces, reported separately.
- All harness checks passed, including `check_interface`.
- Loader overhead: 0.007 s CPU (0.096% of one core), peak RSS 3,900 KiB (short-run only).

### Interpretation
- This accepts per-interface TX/RX separation across two virtual interfaces in two network namespaces, with namespace-scoped interfaces reported as `ifindex@netns`.
- Scope limits: physical NICs, VPN/tunnel paths, wildcard multi-interface sockets, cross-namespace BPF-key merging (key has no netns dimension), and sustained load remain unproven. Some background UDP interfaces remain unresolved.

### Changes
- Accepted/annotated the new report; updated the report index, `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md`.
- Fixed report wording for multi-process veth runs (TGID list, mode-specific scope/counter caveats, background-unresolved note). Non-privileged synthetic report check passed; baseline remains 36/36 passing.

## 2026-11-18 (Overview banner layout + local-time display)

### Reported issue
- Screenshot showed blurred/jagged text in the Overview data-provenance banner: the source-detail text overlapped the window/sync block because a horizontal `StackPanel` measured the wrapping `TextBlock` at its full desired width.
- Displayed range/window timestamps were labeled UTC instead of the OS local time required by the plan.

### Changes
- `OverviewPageView.axaml`: replaced the horizontal left `StackPanel` with `Grid ColumnDefinitions="Auto,*"` so `SourceDetail` is constrained and wraps without overlapping the right-hand window/sync block. Shortened the fallback text to avoid `local SQLite (local SQLite fallback)` duplication.
- Displayed times now use `ToLocalTime()` and are labeled `local`: `OverviewPageViewModel.WindowText`/`SyncText`, `DiagnosticsPageViewModel.WindowText`, `MainViewModel.RangeText`. Range tooltip/custom-range note reworded to say display is local while storage/conversion remains UTC.
- Exports remain explicitly UTC-labeled machine-readable fields; only UI display changed.

### Verification
- `dotnet build Flow.slnx -v minimal` → succeeded (3 warnings, 0 errors).
- `dotnet test Flow.slnx -v minimal --no-build` → **36/36 passed**.
- GUI was not relaunched/screenshotted in this step; visual confirmation is requested from the user after rebuild.

## 2026-11-18 (soak validation mode added, not yet run)

### Implementation
- Added `--mode soak` to `scripts/validate-ebpf-attribution.py`: repeats small loopback TCP/UDP transfers for `--duration` seconds (default 300), spawns a short-lived child every `--soak-child-every` iterations (default 5) using `--soak-child`, and samples loader CPU/RSS plus BPF map entry count.
- Loader now emits an `X` TSV line (`snapshot`, `map_entries`, cumulative unattributed TX/RX); harness parses it and reports map growth over time.
- Exactness checks cover only the parent TGID; child churn rows are reported as background. No claim of bounded steady-state memory is made (map has no expiry).
- Report gains a "Soak / map growth observation" section with duration, iterations, child count, map-entry samples, and CPU/RSS samples.

### Verification (non-privileged)
- `py_compile`, `--soak-child` 1 MiB transfer, and synthetic soak report generation passed.
- Loader recompiled with `0` errors; product build/test baseline remains 36/36 passing.
- Privileged soak run still pending.

### How to run
```bash
sudo python3 scripts/validate-ebpf-attribution.py --mode soak --duration 300 --soak-size-mib 1
```

## 2026-11-18 (accepted 60-second soak)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --mode soak --duration 60 --soak-size-mib 1` produced `docs/measurements/ebpf-controlled-20261118T115504Z.md`.
- Parent TGID 84922: TCP and UDP each exactly **73,400,320 TX/RX** (70 iterations × 1 MiB), one verified generation, all rows `lo`, 0 unresolved/unattributed bytes; all checks true.
- 14 short-lived child transfers produced distinct process-generation background rows (some `netns=0`/`exe=-` after exit).
- Loader CPU: 0.038 s → 0.152 s cumulative over ~63 s (~0.2% of one core); reported average 0.189%. Peak RSS constant at 3,916 KiB across all 12 samples.
- BPF map entries: 6 → 34 over the run; no expiry, so growth is an observation, not a bounded-memory claim.

### Interpretation
- First sustained datapoint for low CPU and stable RSS under repeated transfers plus process churn. Byte accounting stayed exact for the parent and unattributed stayed zero.
- Not production-load evidence: 60 s, low churn, no map-capacity/expiry test, and no high-connection workload. Child churn rows had incomplete post-exit enrichment, which is already documented.

### Changes
- Accepted/annotated the report and report index; updated `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md`.
- Fixed a missing prose line in the harness report generator and in the generated report.
- No product code changed; build/test baseline remains 36/36 passing.

## 2026-11-18 (accepted 300-second soak)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --mode soak --duration 300 --soak-size-mib 1` produced `docs/measurements/ebpf-controlled-20261118T121017Z.md`.
- Parent TGID 86954: TCP and UDP each exactly **364,904,448 TX/RX** (348 iterations × 1 MiB), one verified generation, all expected rows `lo`, 0 unresolved/unattributed bytes; all checks true.
- 69 short-lived child transfers produced distinct background process generations; some show `netns=0`, `exe=-`, or `identity verified: no` after exit.
- Loader CPU: 0.038 s → 1.895 s cumulative over ~305 s; reported average **0.616% of one core**. Peak RSS constant at **3,960 KiB** across all samples.
- BPF map entries: **10 → 161** with no expiry. Growth is an observation under low churn, not a bounded-memory or capacity claim.

### Interpretation
- Extends the 60 s soak: byte accounting stayed exact for the parent, unattributed stayed zero, CPU scaled roughly linearly with time at <1% of one core, and RSS remained flat over 5 minutes.
- Not production-load evidence: map capacity not approached, higher churn/higher connection counts unmeasured, physical NIC/VPN/wildcard multi-interface paths unmeasured.

### Changes
- Accepted/annotated the report and report index; updated `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md`.
- No product code changed; build/test baseline remains 36/36 passing.

## 2026-11-18 (physical-NIC probe added, not yet run)

### Implementation
- Added `--mode nic` to `scripts/validate-ebpf-attribution.py`. It infers the default-route gateway/device (`ip route`) as `--peer-host`/`--interface` and sends a known TCP payload to a peer TCP echo listener, e.g. Termux on an Android phone hotspot.
- Requires expected rows to resolve to the physical interface in the root netns; records that interface's `/proc/net/dev` delta; reports background/unattributed separately.
- TCP-only. UDP through the hotspot is not exercised. Optional `--peer-bytes` adds the peer listener's reported byte count as a check.
- Verified locally: default-route inference returned `10.61.133.76`/`wlp0s20f3`; remote echo transfer and synthetic NIC report generation passed. No BPF change; loader unchanged.

### Pending
- With the Termux listener running on the phone:
  `sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-host 10.61.133.76 --peer-port 9000 --interface wlp0s20f3 --peer-bytes 16777216`
- Expectation: exact TCP TX/RX, expected rows on `wlp0s20f3`, background separated, all checks true. Record the measured result rather than claiming hardware-interface coverage before the run.

## 2026-11-18 (accepted physical-NIC TCP evidence)

### Measured result
- With a Termux TCP echo listener on the Android hotspot peer (`10.61.133.76:9000`), `sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-host 10.61.133.76 --peer-port 9000 --interface wlp0s20f3 --peer-bytes 16777216` produced `docs/measurements/ebpf-controlled-20261118T131724Z.md`.
- Expected TGID 8053: TCP **16,777,216 TX / 16,777,216 RX**, one verified generation, row on `wlp0s20f3` in the root netns, 0 unresolved/unattributed bytes.
- Phone listener independently printed `bytes 16777216`, matching `--peer-bytes` and the harness check.
- `wlp0s20f3` counter deltas: TX 17,224,856, RX 17,739,281 (IP/link/Wi-Fi overhead above socket payload). Background opencode/chronyd/avahi rows separated; one unresolved avahi UDP TX row reported.
- Loader CPU 0.05 s (0.088% of one core), peak RSS 3,884 KiB (short run).

### Interpretation
- Accepts TCP per-process attribution on a real hardware interface (`wlp0s20f3`) with an independent peer byte count.
- Scope: TCP only; no UDP through hotspot, VPN/tunnel, multiple simultaneous interfaces, wildcard multi-interface sockets, or cellular-offload behavior. No cellular payload used (traffic terminated at the phone).

### Changes
- Accepted/annotated the report and report index; updated `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md`.
- Fixed report wording for nic mode (TCP-only, no misleading "TCP then UDP" line). No BPF/loader change; build/test baseline remains 36/36 passing.

## 2026-11-18 (physical-NIC UDP extension added, not yet run)

### Implementation
- Extended `--mode nic` from TCP-only to TCP+UDP echo through the physical interface.
- Added `scripts/termux_echo_listener.py`: one Termux session runs a TCP echo listener on port 9000 and a UDP echo listener on port 9001, printing `tcp <bytes>` and `udp <bytes>`.
- Added `--peer-udp-port` (default 9001) and `--peer-udp-bytes`; the harness now checks UDP TX/RX and peer-reported UDP bytes in addition to TCP.
- UDP client reads echoes concurrently with sending to avoid receive-buffer drops; local 1 MiB TCP and UDP echo smoke tests passed.
- Report wording now describes TCP+UDP NIC mode and both peer-count checks.

### Pending
- On the phone: `termux-wake-lock` then `python3 termux_echo_listener.py`.
- On the laptop:
  `sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-host 10.61.133.76 --peer-port 9000 --peer-udp-port 9001 --interface wlp0s20f3 --peer-bytes 16777216 --peer-udp-bytes 16777216`
- Expectation: exact TCP and UDP TX/RX, expected rows for both protocols on `wlp0s20f3`, peer counts matching, all checks true. Any UDP drops or mismatches must be recorded as measured.

### Verification
- `py_compile` passed for harness and Termux helper; local TCP/UDP echo smoke tests passed; synthetic NIC report generation passed.
- No BPF/loader change; build/test baseline remains 36/36 passing.

## 2026-11-18 (physical-NIC UDP listener lifecycle fix)

### Measured failure
- First TCP+UDP NIC attempt reached the TCP phase successfully, then failed with `ConnectionRefusedError` on UDP. The Termux screenshot showed `udp 0` printed before the TCP peer connected: the UDP listener's 3 s idle timeout closed it before the harness's UDP phase.

### Fix
- `scripts/termux_echo_listener.py`: UDP thread is non-daemon, stays alive while TCP runs, and only drains for 5 s after TCP completes; sets `SO_RCVBUF` to 8 MiB.
- Harness: UDP sends are paced every 8 datagrams with a 5 ms pause; NIC transfer `OSError`s now produce a diagnostic instead of a bare traceback.
- Local combined TCP+UDP echo smoke with the helper passed (1 MiB each, exact). No BPF/loader change; build/test baseline remains 36/36 passing.

## 2026-11-18 (accepted physical-NIC TCP+UDP run)

### Measured result
- After fixing the Termux UDP listener lifecycle, `sudo python3 scripts/validate-ebpf-attribution.py --mode nic --peer-port 9000 --peer-udp-port 9001 --interface wlp0s20f3 --peer-bytes 16777216 --peer-udp-bytes 16777216` produced `docs/measurements/ebpf-controlled-20261118T144214Z.md`.
- Parent TGID 16534: TCP and UDP each exactly **16,777,216 TX/RX**, one verified generation, both expected rows on `wlp0s20f3` in the root netns, 0 unresolved/unattributed bytes, all checks true.
- Peer byte counts were supplied via `--peer-bytes`/`--peer-udp-bytes` and matched payloads; the Termux `tcp`/`udp` lines were not archived for this run (the earlier TCP-only run did archive them).
- Loader CPU 0.021 s (0.077%), peak RSS 3,808 KiB (short run). `wlp0s20f3` counter deltas exceeded payload due to IP/link/Wi-Fi overhead.

### Interpretation
- Accepts hardware-interface attribution for both TCP and UDP on one physical Wi-Fi path.
- Scope: no VPN/tunnel, multiple simultaneous interfaces, wildcard multi-interface sockets, or cellular-offload behavior. Peer confirmation for this run is operator-supplied via flags rather than archived phone output.

### Changes
- Accepted/annotated the report and index; updated `docs/attribution.md`, ADR 0006, `prototype/bpf/README.md`, and `HANDOFF.md`.
- Fixed report wording to say TCP+UDP NIC mode. No BPF/loader change; build/test baseline remains 36/36 passing.

## 2026-11-18 (network-namespace identity key implemented)

### Design decision
- Added ADR 0007: capture netns id at event time (`nsproxy->net_ns->ns.inum`), include it in the BPF key and in `ProcessIdentity.StableKey`, and populate the existing `processes.network_namespace_id` column. Historical 3-part stable keys are not rewritten; new rows use 4-part keys.

### Implementation
- BPF key is now `{start_ns, netns_id, tgid, uid, ifindex, proto}`; netns is read from the thread-group leader via CO-RE at event time.
- Loader struct/find_prev updated; the TSV `netns_id` field is now the kernel-captured value rather than a `/proc`-snapshot guess.
- Parser sets `ProcessIdentity.NetworkNamespaceId`; `StableKey` is `pid:startUnixMs:uid:netns` (missing netns renders as `-`).
- Added parser tests: same pid/start/uid in two netns produce distinct stable keys; legacy lines have no netns in the key.

### Verification
- BPF object, skeleton, and loader compiled with 0 errors.
- `dotnet build Flow.slnx` succeeded; `dotnet test Flow.slnx` → **38/38 passed** (Linux 19).

### Pending
- Privileged two-netns collision run: two distinct netns with identical `(pid, start, uid)` must produce distinct counter rows, stable keys, and persisted process metadata before claiming cross-namespace de-duplication is validated.

## 2026-11-18 (two-netns collision harness added, not yet run)

### Implementation
- Added `--mode netns-key` to `scripts/validate-ebpf-attribution.py`. It creates two temporary netns (`flowkeyA`, `flowkeyB`) with loopback up, then runs one helper process that uses `setns()` to move between them while keeping TGID/start/UID identical.
- The harness requires two distinct expected-process TCP rows with the same `tgid`, `start_ns`, `uid`, `proto`, and `ifname`, but different `netns_id`, each with exact TX/RX bytes. A single merged row fails `check_netns_collision`.
- Report gains a "Netns-key collision observation" section listing both netns rows and the collision check. Temporary netns are cleaned up in `finally`.
- Syntax check and synthetic report generation passed; no product code changed. Build/test baseline remains 38/38 passing.

### Pending
- Privileged run:
  `sudo python3 scripts/validate-ebpf-attribution.py --mode netns-key --netns-size-mib 1`
- Expectation: two distinct rows with exact 1 MiB TX/RX each, one verified process generation, 0 unattributed. Acceptance requires the report to show `check_netns_collision: True`; a merged row means the netns key change has not achieved its purpose.

## 2026-11-18 (accepted two-netns identity collision validation)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --mode netns-key --netns-size-mib 1` produced `docs/measurements/ebpf-controlled-20261118T190019Z.md`.
- One helper TGID 31488 / start_ns 10290344949016 in two netns produced **two distinct TCP rows** (`netns 4026532651` and `4026532843`), each with exactly **1,048,576 TX/RX** on `1@<netns>`.
- `check_netns_collision: True`, one process generation, 0 unresolved/unattributed bytes, 0 background impact on the gate. CPU 0.01 s, RSS 3,788 KiB (short run).
- Raw `check_identity: False` and `check_interface: False` were harness expectation mismatches: emitting server threads exit before `/proc` enrichment, and the loader correctly names foreign-netns interfaces as `ifindex@netns` instead of `lo`.

### Interpretation
- Accepts that netns participates in the kernel identity key and prevents identical `(tgid, start, uid)` generations in different netns from merging (ADR 0007).
- Not a real container lifecycle test; no VPN/tunnel or multi-interface workload. Historical 3-part stable keys are not rewritten.

### Changes
- Fixed harness expectations for netns-key mode (`ifindex@netns` naming, identity check n/a for short-lived emitting threads) and annotated/accepted the report and index; updated `docs/attribution.md`, ADR 0007, `HANDOFF.md`.
- No BPF change; build/test baseline remains 38/38 passing.

## 2026-11-18 (map-churn capacity probe added, not yet run)

### Implementation
- Added `--mode map-churn` to `scripts/validate-ebpf-attribution.py`. It spawns batches of short-lived TCP+UDP children (default 64 per batch, 4 KiB per protocol) until BPF map entries reach `--churn-target` (default 9000) or `--churn-timeout` (default 180 s) expires.
- Samples BPF map entries, loader CPU, and peak RSS each batch; verifies child byte totals per protocol/direction against successful child count × payload (`check_churn_bytes`).
- Report gains a "Map churn observation" section with target reached, child/batch counts, expected vs attributed bytes, map-entry first/last/max, and CPU/RSS samples. Process rows are capped at 20 in the report while aggregates cover all children.
- Default modes unchanged; no BPF/loader change.

### Verification
- `py_compile`, synthetic map-churn report, and 4 KiB `--soak-child` smoke passed. No .NET code changed; baseline remains 38/38 passing.

### Pending
- Privileged run:
  `sudo python3 scripts/validate-ebpf-attribution.py --mode map-churn --churn-target 9000`
- Expectation: map entries approach target with CPU/RSS samples and byte totals still exact. Any shortfall near capacity must be recorded as measured; the map has no expiry and this is not a bounded-memory claim.

## 2026-11-18 (accepted high-churn map-growth observation)

### Measured result
- `sudo python3 scripts/validate-ebpf-attribution.py --mode map-churn --churn-target 9000` produced `docs/measurements/ebpf-controlled-20261118T193832Z.md`.
- 3,840 short-lived TCP+UDP children in 180 s (60 batches), all successful. BPF map entries grew 130 → 7,653 (max 7,653) of 10,240; the 9,000 target was not reached before timeout.
- Attributed bytes stayed exact: 15,728,640 per protocol/direction, `check_churn_bytes: True`, 0 unattributed, expected rows on `lo`. Background traffic separated.
- Loader CPU 14.574 s total (7.815% of one core); peak RSS rose 3,940 → 6,380 KiB as entries accumulated.

### Interpretation
- Accepts stable accounting at high map occupancy, not a hard-capacity or bounded-memory claim. Memory growth tracks retained process generations because the map has no expiry; the 10,240-entry cap remains untested.
- The harness crashed after writing the report with `UnboundLocalError` on `check_netns_collision` (not initialized in map-churn mode). Fixed by initializing collision/churn/target checks to `None`; report values were unaffected.

### Changes
- Accepted/annotated report and index; fixed harness initialization, capped displayed TGIDs/payload wording, and updated `docs/attribution.md`, `HANDOFF.md`.
- No BPF/loader change; build/test baseline remains 38/38 passing.

## 2026-11-18 (wildcard multi-interface probe added, not yet run)

### Implementation
- Added `--mode wildcard`: creates the veth/netns pair, runs one UDP server bound to 0.0.0.0 in the root netns, and sends known payloads from a loopback client and from a peer-netns client via the veth.
- Requires two RX rows for the same server TGID/protocol on `lo` and `flowvA`, with per-interface bytes matching each source; also checks client TX rows on each side.
- VethPair now records the root-side ifindex; helper and report sections added.
- Syntax, synthetic report, and helper-path checks passed. No BPF/loader change; build/test baseline remains 38/38 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode wildcard --wildcard-size-mib 1`
- Expectation: `check_wildcard: True`, exact per-source bytes, distinct interfaces for the same wildcard-bound server socket, 0 unattributed.

## 2026-11-19 (accepted wildcard multi-interface and near-cap runs)

### Wildcard multi-interface
- `docs/measurements/ebpf-controlled-20261118T225010Z.md`: one wildcard UDP server (TGID 56339) produced two RX rows on `lo` and `flowvA`, each exactly 1,048,576 bytes; loopback and peer-netns client TX rows exact; `check_wildcard: True`, 0 unresolved/unattributed. Accepts per-datagram interface attribution for a wildcard-bound socket.
- Fixed report payload wording (1 MiB per source, not 16 MiB) in the generator.

### Hard-capacity approach
- `docs/measurements/ebpf-controlled-20261118T225153Z.md`: 5,056 short-lived children grew map entries 128→10,107/10,240; byte totals exact, `check_churn_bytes: True`, map target reached, 0 unattributed. CPU 10.142%, RSS 3,788→7,224 KiB. Hard cap itself remains untested.

### Changes
- Accepted/annotated both reports and index; updated `docs/attribution.md`, `HANDOFF.md`. No BPF/loader change; baseline 38/38 passing.

## 2026-11-19 (VPN/tunnel probe implemented, not yet run)

### Implementation
- Added `--mode tunnel`: builds a veth underlay plus a GRE tunnel (IPIP fallback) between the root netns and `flowvns`, with tunnel addresses 10.0.0.1/30 and 10.0.0.2/30.
- Runs a TCP echo server in the peer netns bound to the tunnel address; the root client connects over the tunnel. The harness requires the client TX row on the root tunnel device and the server RX row on the peer-netns tunnel device, with no expected rows on the veth underlay.
- Records root tunnel, root underlay, and peer-netns tunnel counter deltas to document overlay/underlay overlap (double counting) without attributing underlay bytes to the process.
- Syntax, CLI, and synthetic report checks passed. No BPF/loader change; baseline 38/38 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode tunnel --tunnel-size-mib 16`
- Expectation: exact TCP bytes, `check_tunnel: True`, expected rows only on tunnel devices, underlay counter delta > 0 (overlap documented), 0 unattributed. If tunnel creation fails, record the kernel/module limitation rather than claiming tunnel coverage.

## 2026-11-19 (accepted GRE tunnel attribution)

### Measured result
- `docs/measurements/ebpf-controlled-20261118T234935Z.md`: 16 MiB TCP echo over GRE above veth. Client TX/RX on `flowtun0` (root) and server TX/RX on `6@4026532906` (peer netns), each exactly 16,777,216 bytes; `check_tunnel: True`, no expected rows on the veth underlay, 0 unattributed.
- Overlay and underlay counters both grew by ~16.8 MB, documenting the known double-counting; only the tunnel device is attributed to the processes.

### Harness fix
- The raw TCP/UDP checks were false because expected rows include both endpoints (33,554,432 combined) while the harness compared to a single 16 MiB payload; UDP was not exercised. Harness now aggregates both endpoints for tunnel mode and treats UDP as n/a.

### Scope
- TCP-only GRE tunnel above veth; no real VPN daemon, physical NIC, or multiple simultaneous interfaces. Production-load overhead, hard map cap, and real VPN behavior remain unmeasured.

### Verification
- `py_compile` passed; no BPF/loader change; baseline 38/38 passing.

## 2026-11-19 (production-load soak mode added, not yet run)

### Implementation
- Added `--mode prod-soak`: runs N concurrent transfer workers (default 8) for `--duration` seconds, each looping TCP+UDP transfers of `--soak-size-mib`; optional `--prod-child-every` spawns short-lived children for churn.
- Samples loader CPU, RSS, and BPF map entries at intervals; verifies aggregate byte totals per protocol, interface attribution, and unattributed traffic.
- Report gains a "Production-load soak observation" section with workers/transfers/children, CPU/RSS samples, and map-entry progression.
- Syntax and synthetic report checks passed. No BPF/loader change; baseline 38/38 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode prod-soak --duration 300 --prod-workers 8`
- Expectation: exact aggregate bytes, zero unattributed, all rows on `lo`, CPU/RSS/map samples recorded. This is a loopback concurrency/duration proxy, not a production SLA claim.

## 2026-11-19 (accepted production-load proxy)

### Measured result
- `docs/measurements/ebpf-controlled-20261119T002316Z.md`: 8 workers × 300 s, 2,850 TCP+UDP transfers; exact 2,988,441,600 bytes per protocol/direction, all expected rows on `lo`, 0 unattributed, one process generation.
- Loader CPU 0.144% of one core, peak RSS constant 3,944 KiB, BPF map entries 4→14. Accepts short-term concurrent overhead/stability on loopback.

### Caveat
- `check_identity: False` for the expected row: kernel-captured identity retained but `/proc` enrichment did not verify it under sustained multithreaded load. Recorded as open; does not affect byte/interface checks.

### Fix
- Report now states 1 MiB per transfer for prod-soak instead of the prior generic 16 MiB line.

### Next
- Hard map-cap probe near 10,240 entries; real VPN/physical NIC and sustained-load identity verification remain environment-dependent gaps.

## 2026-11-19 (accepted hard-cap limitation evidence)

### Measured result
- `docs/measurements/ebpf-controlled-20261119T003701Z.md`: 5,120 short-lived children drove BPF map entries to 10,231/10,240.
- Expected 5,242,880 bytes per protocol/direction; attributed 5,235,712 — a 7,168-byte shortfall (7 children × 1,024) in each of TCP TX/RX and UDP TX/RX.
- Unattributed stayed 0: missing bytes were neither attributed nor bucketed as unattributed; map insertion failures dropped them silently. `check_churn_bytes` passed only because the loss (0.137%) is inside ±1%.

### Interpretation
- Accepted as evidence of a correctness limitation at saturation, not a clean pass. `check_single_generation: False` is consistent with failed insertions leaving some expected PIDs without rows.
- ADR 0008 records the finding; the next task is a design decision/fix (expiry, larger map, or explicit overflow bucket), not another measurement.

### Changes
- Accepted/annotated report and index; added ADR 0008; updated `docs/attribution.md`, `HANDOFF.md`. No product code changed.

## 2026-11-19 (map-saturation fix implemented, not yet validated)

### Implementation
- Raised BPF `traffic` map and loader `prev` array from 10,240 to 32,768 entries.
- Added an explicit BPF `overflow` counter: when a process-generation row cannot be inserted, bytes/events are counted there instead of being silently dropped.
- Loader emits new `F` TSV lines with cumulative and delta overflow bytes; parser converts overflow deltas into unattributed events with an explicit note; harness parses `F` lines, reports cumulative overflow, and prints exact expected-minus-attributed shortfall in map-churn reports.
- Added parser tests for overflow lines. BPF object/skeleton/loader rebuilt; `dotnet build` succeeded; `dotnet test` → 40/40 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode map-churn --churn-target 32700 --churn-timeout 1200 --churn-child-size-kib 1`
- Expectation: no overflow in `F`, exact shortfall 0, all byte checks true. Any overflow is now visible rather than silent.

## 2026-11-19 (post-fix map run: overflow visible, capacity still limited)

### Measured result
- `docs/measurements/ebpf-controlled-20261119T005948Z.md`: 16,448 short-lived children, map entries 130→32,577 of 32,768; target 32,700 not reached.
- New F counter reported 154,624 bytes/151 events overflow per direction. Corrected exact shortfall: 156,672 TX / 154,624 RX (RX matches overflow; TX has an additional 2,048 bytes/2 events not in the F counter).
- CPU 32.585% of one core, RSS 3,824→15,864 KiB. `check_single_generation: False` consistent with failed insertions leaving some child PIDs without rows.

### Interpretation
- The visibility fix works: map-full loss is no longer silent. Capacity is still insufficient for ~16k short-lived children; next step is 65,536 entries or eviction.

### Fix
- Harness exact-shortfall line now compares two-protocol expected total to aggregate attributed bytes instead of per-protocol expected to aggregate total.

## 2026-11-19 (identity diagnostic mode added, not yet run)

### Implementation
- Added `--mode identity-diag`: short concurrent-worker workload (reuses `--prod-workers`/`--duration`/`--soak-size-mib`) that writes an "Identity diagnostic" report section.
- Section compares BPF `start_ns` (converted with loader `clk_tck`) against loader `startticks`, shows the diff, the verification flag, and `/proc/<pid>/stat` starttime plus `/proc/<pid>/exe` for the expected TGID.
- Added `startticks` to harness process rows; identity check is n/a in this mode so the report is the measurement, not a gate.
- Syntax and synthetic report checks passed. No BPF/loader change; baseline 40/40 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode identity-diag --duration 30 --prod-workers 8`
- Interpret: if computed ticks ≈ stored ticks but verified is false, investigate `/proc` exe enrichment; if they differ, investigate event-time start-time/leader handling; if start_ns differs across rows for one TGID, investigate thread-level identity split.

## 2026-11-19 (WireGuard probe implemented, not yet run)

### Environment check
- `wg` is installed (`wireguard-tools v1.0.20250521`), `modprobe wireguard` succeeds, and `ip link add testwg0 type wireguard` succeeds. Real WireGuard kernel devices are available.

### Implementation
- Added `--mode wireguard`: creates a veth underlay plus real `wg0` devices in root and `flowvns`, generates temporary keys with `wg genkey`/`wg pubkey`, configures peers/endpoints/allowed-ips, and runs a TCP echo server on the WireGuard address.
- Asserts client TX on root `wg0` and server RX on peer-netns `wg0` (`ifindex@netns`), with no expected rows on the veth underlay. Reports overlay/underlay counter overlap.
- Temporary keys/devices/netns are cleaned up in `finally`. Key generation verified locally; syntax and CLI checks passed. No BPF/loader change; baseline 40/40 passing.

### Pending
- Privileged run: `sudo python3 scripts/validate-ebpf-attribution.py --mode wireguard --tunnel-size-mib 16`
- Also pending: `--mode identity-diag` output for the multithreaded identity issue.

## 2026-11-19 (identity diagnostic result and WireGuard key fix)

### Identity diagnostic result
- `docs/measurements/ebpf-controlled-20261119T041447Z.md`: byte totals exact, all rows on `lo`, one process generation. Identity diagnostic showed `start_ns=42480389592063`, `cached startticks=0`, computed ticks=4,248,038, while `/proc/<pid>/stat starttime=4,378,613`. The cached field being 0 is expected when verification fails; the large computed-vs-proc gap needs a raw `/proc` tick field to diagnose.
- Added a new 17th `E` field `raw_startticks` from the loader (fresh `/proc/<pid>/stat` read), and the report now shows `computed−raw`. Loader rebuilt.

### WireGuard blocker
- First run failed: `wg set flowwg0 private-key /tmp/.../root.key` → `fopen: Permission denied`.
- Removed temp key files; private keys now pass via `/dev/stdin` to `wg set`, including inside `ip netns exec`. Key generation verified locally.

### Pending
- Rerun identity diagnostic and WireGuard probe with rebuilt loader/harness.

## 2026-11-19 (identity mismatch localized; real WireGuard accepted)

### Identity diagnostic
- `docs/measurements/ebpf-controlled-20261119T042547Z.md`: loader raw `/proc` ticks = harness `/proc` starttime = 4,444,599; BPF `start_ns` converts to 4,314,025, a 130,574-tick (~1,305 s) gap. Cached `startticks=0` is a consequence of failed verification, not a read failure.
- Next isolation: rerun identity diagnostic with `--prod-workers 1` to determine if the gap is multithreading-specific or systemic.

### Real WireGuard
- `docs/measurements/ebpf-controlled-20261119T043314Z.md`: real `wg0` kernel devices over veth; 16 MiB TCP echo, client on `flowwg0`, server on `6@4026532903`, exact bytes, no underlay rows, `check_wireguard: True`, 0 unattributed. Overlay/underlay counters both ~17.9 MB.
- Harness now treats UDP as n/a for WireGuard mode (TCP-only probe).

### Changes
- Accepted/annotated both reports and index; updated `docs/attribution.md`, `HANDOFF.md`. No BPF change; loader/harness rebuilt; baseline 40/40 passing.

## 2026-11-19 (identity clock-domain fix implemented, not yet validated)

### Evidence
- 1-worker identity diagnostic (`docs/measurements/ebpf-controlled-20261119T043954Z.md`) showed the same ~130,575-tick gap as 8 workers, so the mismatch is systemic, not thread-related.

### Implementation
- BPF event-time identity now reads `task_struct.start_boottime` (fallback `start_time`) so it matches boottime-based `/proc/<pid>/stat` starttime. TSV field name `start_ns` retained; semantics changed to boottime ns.
- Rebuilt BPF object, skeleton, and loader. ADR 0009 documents the decision and the stable-key migration consequence.

### Pending
- Rerun `sudo python3 scripts/validate-ebpf-attribution.py --mode identity-diag --duration 30 --prod-workers 1`; expect `computed−raw≈0` and `verified=True`. Then rerun the 8-worker soak to confirm under load.

## 2026-11-19 (identity fix validated)

### Measured result
- 1-worker diagnostic (`docs/measurements/ebpf-controlled-20261119T045127Z.md`): computed ticks = raw ticks = 4,598,583, `computed−raw=0`, `verified=True`, `/usr/bin/python3.14` resolved, BPF comm `python3`. Exact bytes, all on `lo`, 0 unattributed.
- 8-worker diagnostic (`docs/measurements/ebpf-controlled-20261119T045242Z.md`): same result with 8 workers (4,606,117 ticks), `verified=True`, exe resolved. The boottime identity fix works under multithreaded load.

### Changes
- Accepted/annotated both reports and index; ADR 0009 marked validated; `docs/attribution.md`, `HANDOFF.md` updated. Harness now reports `check_identity` for identity-diag mode. Build/test baseline 40/40 passing.

### Remaining
- Raise BPF/loader map capacity to 65,536 and re-run high-churn validation; real public-VPN-over-physical-NIC testing still needs an external peer.
