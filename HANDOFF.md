# Flow handoff

Date: 2026-11-18  
Repo: `/home/amaechi/code/flow` (git, branch `main`, clean working tree)

## What this is
Flow is an Ubuntu 26.04 (GNOME/Wayland) network-usage monitor with a background daemon and Avalonia desktop GUI (see `flow-project-plan.md`). Core requirement: transparent per-application/per-process byte attribution with persistent history. **Do not claim full completion without validated evidence.**

## Environment (dev/target snapshot)
- Ubuntu 26.04.1 LTS, kernel `7.0.0-43-generic` x86_64, GNOME Wayland
- .NET SDKs 10.0.404/11.0.100 (`/opt/dotnet`); product target .NET 10
- BTF `/sys/kernel/btf/vmlinux`, bpftool/libbpf/clang/llvm available; `kernel.unprivileged_bpf_disabled=2` (eBPF attach requires privileges)
- Default DB: `~/.local/share/flow/flow.db` (override with `Flow__DatabasePath`)

## Current state (what is done and working)

### Prototype & core
- **Docs/decisions:** architecture, monitoring, attribution, security, database, implementation-plan, progress, interface-preparation; ADRs 0001–0003 (`docs/decisions/`). IPC v1 session-bus choice pinned in **ADR 0003** (Tmds.DBus 0.95.2, `org.flow.Daemon` `/org/flow/Daemon` `org.flow.Daemon1`, JSON-over-D-Bus); Avalonia/Wayland and system-bus/activation/policy/auth remain follow-ups (ADR 0002 partially superseded).
- **Phase 1 prototype (eBPF, TCP):** `prototype/bpf/` (`vmlinux.h`, `flow_tcp_bytes.bpf.c`, `flow_tcp_bytes_loader.c`, README). Privileged attach works; per-TGID/UID TCP byte attribution with deltas/cached identity/startticks and explicit unattributed (`tgid==0`) observed. Prototype-stage, not full gate completion.
- **Solution:** `Flow.slnx` with layered projects (`src/Flow.Domain`, `Application`, `Infrastructure`, `Linux`, `Contracts`, `Daemon`, `Desktop`) and `tests/*`. **Build succeeds** (0 errors, 1 pre-existing warning in `tests/Flow.Application.Tests/UsageIngestionPipelineTests.cs` CA1859); **tests pass 16/16** (`dotnet test Flow.slnx`: Domain 1, Application 4, Infrastructure 8, Linux 1, Daemon 1, System 1).

### Monitoring, ingestion, persistence
- **Providers:** `NullNetworkMonitor`; `ProcNetDevMonitor` (interface-level `/proc/net/dev` deltas, explicitly **unattributed** fallback, no privileges); `EbpfTcpMonitor` (subprocess TSV integration of prototype loader, **TCP-only** `tcp_sendmsg`/`tcp_recvmsg` return-byte deltas, privileged, boot+startticks-derived start/app hints, explicit unattributed events, no per-interface mapping in events).
- **Pipeline:** bounded-channel ingestion + 1-minute UTC aggregation + batched/idempotent flush (`UsageIngestionPipeline`), diagnostics/provider labeling, health recovery after transient write failure, failed flushes retained for retry (in-memory, timer-driven), idempotent shutdown/cancellation handling.
- **SQLite (Microsoft.Data.Sqlite):** WAL/pragmas, migrator v1+v2. **v2** fixes bucket consolidation with NULL-safe logical uniqueness/index and `ON CONFLICT` upserts (dedupes existing rows), coalescing repeated flushes (including null app/process/interface/unattributed cases). `UsageRepository` supports metadata/session upserts, minute-bucket writes, summary, top apps, interfaces, processes, and time series queries.
- **Daemon:** DB init/migration, DI, monitor selection (`Flow:Monitor=null|procfs|ebpf|ebpf-tcp`), pipeline start/diagnostics. Example: `Flow__Monitor=procfs dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj` or privileged ebpf: `sudo env Flow__Monitor=ebpf Flow__DatabasePath="$HOME/.local/share/flow/flow.db" dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj`.
- **Verified:** attributed TCP usage persisted with ebpf (privileged); procfs yields real interface totals as unattributed (expected). User-reported desktop/IPC evidence recorded in `docs/progress.md`.

### IPC & desktop (real data, no fabricated metrics)
- **D-Bus IPC (v2 contract methods):** shared `Flow.Contracts.Ipc` + `Tmds.DBus` service/client. Daemon exposes real `IMonitoringStatusService` and `IUsageQueryService` (status, summary, top apps, time series, interfaces, processes). Desktop uses `DbusDaemonClient` with retry-on-reconnect; `HybridDaemonStatusClient` reports truthful unavailable when IPC unreachable (no invented live metrics), `HybridUsageQueryClient` prefers daemon IPC and can fall back to direct local SQLite reads of the **same persisted data** when IPC is unavailable.
- **On-target validation (session bus):** registration/introspection/calls verified (`busctl --user`, contract v2 methods present; status/queries return real JSON). Caveat: running the daemon as root (e.g. for ebpf) can prevent claiming `org.flow.Daemon` due to bus/policy context — IPC may be unavailable while collection/persistence still works (desktop can show real DB history; live IPC diagnostics unavailable).
- **Desktop UI (Avalonia shell, functional):** Overview with real daemon status/diagnostics, explicit IPC-vs-local-DB data-source notes, rolling ranges (1/6/12/24h, 7/30d), `Today`/`Yesterday` (local day → UTC `[start,end)`), custom date range, interface filter, attributed/unattributed/total summary, top applications, top processes, interfaces, recent 1-minute time-series list (real buckets), and **CSV export** of current view to `~/flow-export-<utc-timestamp>.csv` with range/filter/generation/caveat metadata. Attribution/measurement caveats shown in UI.

## Current limitations / known issues
- **Attribution/gate:** ebpf is TCP-only, subprocess+privileged, no per-interface mapping in events; UDP/namespace/VPN/virtual-interface coverage, controlled counter/overhead evidence, and PID-reuse/event-time/process-exit identity hardening incomplete. Procfs is unattributed by design.
- **IPC:** per-user session-bus v1 only; no live D-Bus signals (poll/refresh), no automated daemon↔client integration tests, no control operations/Polkit, no system-bus policy/activation artifacts. Root-run ebpf daemon may not register IPC name for user-session desktop.
- **Persistence/reporting:** no rollups (1m→5m→h→d→mo), no retention/cleanup jobs, no JSON export/file-picker UX; comparisons/Reports UI minimal.
- **Reliability:** flush retry is in-memory/timer-driven (no durable queue/explicit backoff policy); `LastError` not cleared on recovery; monitoring session measurement version/provider details minimal.
- **UI/productization:** single Overview shell (not full nav/pages), no graphical charts (textual buckets), Wayland/a11y/accessibility not formally validated/polished, no systemd/D-Bus/Polkit packaging or `.deb`.
- **Reconcile docs:** ADR 0003 text references initial method set; implementation is contract v2 — update/supersede details as needed when changing IPC.

## Suggested next steps (prioritized)
1. **UI polish & structure (real data):** app shell/navigation (Overview/Applications/Processes/Interfaces/Reports/History/Settings), shared theme/resources, sortable/searchable tables and detail views, better empty/loading/error/stale/disconnected states, file-picker export (+ optional JSON), and graphical time-series charts after verifying Avalonia chart dependency/Wayland fit. Keep attributed/unattributed, provider caveats, and live-vs-historical explicit.
2. **IPC hardening:** add status/change signals (batched), automated daemon↔client contract/integration tests, improve reconnect/error mapping, and decide/document privileged-collector + user-desktop topology (system-bus policy/activation/Polkit or split capture/IPC) with evidence.
3. **Provider/identity hardening & gate evidence:** event-time persisted identity, PID-reuse/process-exit safety, per-interface mapping where feasible, UDP/namespace/VPN/virtual-interface assessment, controlled counter comparisons and overhead; update `docs/attribution.md` and ADRs with evidence (do not weaken claims).
4. **Persistence/reporting:** rollups preserving totals, async retention/cleanup, comparisons/reports UX, and exports with full caveats.
5. **Productization:** systemd units, D-Bus/Polkit policy, `.deb` packaging, upgrade/uninstall data handling, GNOME Wayland/a11y pass, recovery/system/security/privacy tests.

## How to build, test, and run
```bash
cd /home/amaechi/code/flow
dotnet build Flow.slnx -v minimal
dotnet test Flow.slnx -v minimal   # expect 16/16
```
- **Daemon (no root, unattributed interface totals):** `Flow__Monitor=procfs dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj`
- **Daemon (privileged, TCP attributed, prototype):** `sudo env Flow__Monitor=ebpf Flow__DatabasePath="$HOME/.local/share/flow/flow.db" dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj` (IPC name registration may fail under sudo; DB persistence still works)
- **Desktop:** `dotnet run --project src/Flow.Desktop/Flow.Desktop.csproj` (uses same DB path/env; connects via D-Bus when available, else truthful unavailable + real local DB fallback)
- **Inspect DB:** `sqlite3 ~/.local/share/flow/flow.db ".headers on" ".mode column" "select * from usage_buckets order by id desc limit 20;"`
- **IPC check (daemon running, session bus):** `busctl --user introspect org.flow.Daemon /org/flow/Daemon` (expect contract v2 methods)

## UI polish & design references
Use these to guide polishing the current Avalonia shell (GNOME-friendly, honest attribution/live-vs-historical/health states) — **inspiration/patterns, not pixel copies**:

- **GNOME HIG:** https://developer.gnome.org/hig/
- **Adwaita (style/pattern reference):** https://gnome.pages.gitlab.gnome.org/libadwaita/doc/main/ and https://gitlab.gnome.org/GNOME/libadwaita
- **GNOME apps to study:** https://apps.gnome.org/ (esp. GNOME System Monitor, Usage, Settings for navigation/tables/live-vs-totals)
- **Avalonia docs:** https://docs.avaloniaui.net/
- **Avalonia samples/control gallery:** https://github.com/AvaloniaUI/Avalonia and https://github.com/AvaloniaUI/Avalonia.Samples
- **Fluent 2 guidance:** https://fluent2.microsoft.design/
- **Observability/dashboard layout inspiration (adapt carefully):** https://grafana.com/grafana/dashboards/
- **Visual inspiration (adapt, don’t copy):** https://dribbble.com/search/system-monitor and https://www.figma.com/community

## Quick pointers
- Plans/status: `docs/progress.md`, `docs/implementation-plan.md`, `docs/interface-preparation.md`, `docs/attribution.md`, `docs/monitoring.md`
- Decisions: `docs/decisions/` (0001–0003)
- Prototype: `prototype/bpf/README.md`, `scripts/check-prereqs.sh`
- Main code: `src/Flow.Daemon/Ipc/*`, `src/Flow.Application/Monitoring/UsageIngestionPipeline.cs`, `src/Flow.Infrastructure/Sqlite/*`, `src/Flow.Linux/Monitoring/*`, `src/Flow.Desktop/*`

## Starting fresh
1. Read this file, `docs/progress.md`, and `flow-project-plan.md`.
2. Run `dotnet build Flow.slnx` and `dotnet test Flow.slnx` to confirm baseline.
3. Pick the next prioritized item (recommend **UI polish & structure** or **IPC hardening**) and work incrementally, keeping all metrics/caveats real and evidence-backed.
