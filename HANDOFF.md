# Flow handoff

Date: 2026-11-18  
Repo: `/home/amaechi/code/flow` (git, branch `main`; UI/IPC increment committed as `474bf00`; accepted TCP-loopback validation committed as `db9d980`; TCP+UDP/per-interface increment is uncommitted — see `docs/progress.md`)

## What this is
Flow is an Ubuntu 26.04 (GNOME/Wayland) network-usage monitor with a background daemon and Avalonia desktop GUI (see `flow-project-plan.md`). Core requirement: transparent per-application/per-process byte attribution with persistent history. **Do not claim full completion without validated evidence.**

## Environment (dev/target snapshot)
- Ubuntu 26.04.1 LTS, kernel `7.0.0-43-generic` x86_64, GNOME Wayland
- .NET SDKs 10.0.404/11.0.100 (`/opt/dotnet`); product target .NET 10
- BTF `/sys/kernel/btf/vmlinux`, bpftool/libbpf/clang/llvm available; `kernel.unprivileged_bpf_disabled=2` (eBPF attach requires privileges)
- Default DB: `~/.local/share/flow/flow.db` (override with `Flow__DatabasePath`)

## Current state (what is done and working)

### Prototype & core
- **Docs/decisions:** architecture, monitoring, attribution, security, database, implementation-plan, progress, interface-preparation; ADRs 0001–0006 (`docs/decisions/`). IPC v1 session-bus choice pinned in **ADR 0003** (Tmds.DBus 0.95.2, `org.flow.Daemon` `/org/flow/Daemon` `org.flow.Daemon1`, JSON-over-D-Bus); **ADR 0004** records the page shell, custom chart controls, picker exports, and the v3 coalesced `StatusChanged` signal; **ADR 0005** records event-time process identity, guarded `/proc` enrichment, and the narrowed stable key; **ADR 0006** records TCP+UDP hooks, per-interface keys, and the TCP/UDP bucket-aggregation decision. Avalonia a11y and system-bus/activation/policy/auth remain follow-ups.
- **Phase 1 prototype (eBPF, TCP+UDP):** `prototype/bpf/` (`vmlinux.h`, `flow_tcp_bytes.bpf.c`, `flow_tcp_bytes_loader.c`, README). Privileged attach works for TCP+UDP byte hooks (one controlled run). UDP RX interface correlation via `__skb_recv_udp` is compiled but not yet privileged-run. Per-TGID/UID TCP+UDP byte attribution with deltas and explicit unattributed (`tgid==0`) observed. Identity hardening keys `{start_ns, tgid, uid}` using the thread-group leader's start time, captures first-observed `cgroup_id`/`comm`, and only enriches `/proc` when the live PID matches. The key adds `ifindex`/`proto`; QUIC is counted as UDP payload. **Accepted controlled TCP-loopback rerun** (`docs/measurements/ebpf-controlled-20261118T044634Z.md`): one verified process generation, exactly 16,777,216 TX/RX for a 16 MiB payload, background traffic separated, 0 unattributed, 0.004 s loader CPU / 3,672 KiB peak RSS (short-run only). **TCP+UDP controlled run** (`docs/measurements/ebpf-controlled-20261118T050933Z.md`): TCP and UDP each exactly 16,777,216 TX/RX, one generation, 0 unattributed; TCP/UDP TX resolved to `lo`, but UDP RX was unresolved (16,777,216 bytes). A per-datagram `skb_iif` (`__skb_recv_udp`) fix is compiled and awaits a rerun. Prototype-stage, not full gate completion.
- **Solution:** `Flow.slnx` with layered projects (`src/Flow.Domain`, `Application`, `Infrastructure`, `Linux`, `Contracts`, `Daemon`, `Desktop`) and `tests/*`. **Build succeeds** (0 code warnings/errors; offline NuGet vulnerability warnings only); **tests pass 35/35** (`dotnet test Flow.slnx`: Domain 1, Application 4, Infrastructure 9, Linux 16, Daemon 1, System 4).

### Monitoring, ingestion, persistence
- **Providers:** `NullNetworkMonitor`; `ProcNetDevMonitor` (interface-level `/proc/net/dev` deltas, explicitly **unattributed** fallback, no privileges); `EbpfMonitor` (subprocess TSV integration of prototype loader, TCP+UDP return-byte deltas including QUIC-as-UDP, privileged, event-time start time/cgroup/comm identity with guarded `/proc` enrichment, per-interface attribution when the socket route resolves, explicit unattributed events). Persisted minute buckets aggregate TCP+UDP for the same process/interface.
- **Pipeline:** bounded-channel ingestion + 1-minute UTC aggregation + batched/idempotent flush (`UsageIngestionPipeline`), diagnostics/provider labeling, health recovery after transient write failure, failed flushes retained for retry (in-memory, timer-driven), idempotent shutdown/cancellation handling.
- **SQLite (Microsoft.Data.Sqlite):** WAL/pragmas, migrator v1+v2. **v2** fixes bucket consolidation with NULL-safe logical uniqueness/index and `ON CONFLICT` upserts (dedupes existing rows), coalescing repeated flushes (including null app/process/interface/unattributed cases). `UsageRepository` supports metadata/session upserts, minute-bucket writes, summary, top apps, interfaces, processes, and time series queries.
- **Daemon:** DB init/migration, DI, monitor selection (`Flow:Monitor=null|procfs|ebpf|ebpf-tcp`), pipeline start/diagnostics. Example: `Flow__Monitor=procfs dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj` or privileged ebpf: `sudo env Flow__Monitor=ebpf Flow__DatabasePath="$HOME/.local/share/flow/flow.db" dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj`.
- **Verified:** attributed TCP usage persisted with ebpf (privileged); procfs yields real interface totals as unattributed (expected). User-reported desktop/IPC evidence recorded in `docs/progress.md`.

### IPC & desktop (real data, no fabricated metrics)
- **D-Bus IPC (contract v3):** shared `Flow.Contracts.Ipc` + `Tmds.DBus` service/client. Daemon exposes real `IMonitoringStatusService` and `IUsageQueryService` (status, summary, top apps, time series, interfaces, processes) and a coalesced `StatusChanged` signal (polled every 2s; emitted only when the real diagnostics snapshot changes). Signals carry JSON `StatusChangedSignalDto` and are treated by the desktop as a refresh hint, not as authoritative values. Desktop uses `DbusDaemonClient` with retry-on-reconnect; `HybridDaemonStatusClient` reports truthful unavailable when IPC unreachable (no invented live metrics), `HybridUsageQueryClient` prefers daemon IPC and can fall back to direct local SQLite reads of the **same persisted data** when IPC is unavailable.
- **Automated IPC tests:** `Flow.SystemTests` starts a private `dbus-daemon`, registers the real daemon service object, and asserts contract v3, JSON round-trip of every query method over real D-Bus, signal delivery, and publisher coalescing. Requires `dbus-daemon` on `PATH` (Linux); does not cover system bus/policy.
- **On-target validation (session bus):** registration/introspection/calls verified earlier (`busctl --user`, contract methods present; real JSON results). On 2026-11-18 the same path exposed `.StatusChanged signal s` with contract `i 3`, and `busctl --user monitor org.flow.Daemon` observed coalesced `StatusChanged` JSON payloads (~2s) carrying real procfs pipeline counters. Caveat: running the daemon as root (e.g. for ebpf) can prevent claiming `org.flow.Daemon` due to bus/policy context — IPC may be unavailable while collection/persistence still works (desktop can show real DB history; live IPC diagnostics unavailable).
- **Desktop UI (Avalonia shell, functional):** fixed header + sidebar navigation with four pages — Overview (bento metrics, RX/TX + attribution split bars, custom RX/TX time-series chart, top apps/interfaces, recent buckets), Apps & Processes (sortable/searchable applications and processes, attributed/unattributed filter, detail panels), Network Interfaces (totals, provider semantics, sortable/searchable breakdown, detail panel), Diagnostics & Daemon (state/provider/health, pipeline counters, drop share, IPC/signal source and contract identity, searchable persisted buckets). Rolling ranges (1/6/12/24h, 7/30d), `Today`/`Yesterday`, custom date range, interface filter. CSV **and JSON** export of the loaded view use a real save-file picker (`Flow__ExportDirectory` overrides for automation) and include provenance + caveats. Attribution/measurement caveats and live-vs-SQLite fallback are explicit in the UI.

## Current limitations / known issues
- **Attribution/gate:** ebpf provider counts TCP and UDP (QUIC as UDP payload), subprocess+privileged, with per-interface attribution when the socket route resolves (`ifindex 0` is grouped as unresolved). Controlled TCP-loopback and TCP+UDP byte-accuracy evidence is accepted (exact payloads, one verified process generation, 0 unattributed, short-run CPU/RSS). Interface attribution is accepted for TCP and UDP TX; UDP RX interface resolution failed and the `skb_iif` fix awaits a rerun. Remaining gate gaps: physical-interface behavior, ICMP/non-TCP-UDP paths, namespaces/containers, VPN/tunnel/virtual-interface behavior, sustained/high-connection overhead, and map expiry for dead process generations. Procfs is unattributed by design.
- **IPC:** per-user session-bus v1 only; signals are best-effort/coalesced (no replay, up to ~2s latency) and resubscription after daemon restart is lightly exercised; no control operations/Polkit, no system-bus policy/activation artifacts. Root-run ebpf daemon may not register IPC name for user-session desktop.
- **Persistence/reporting:** no rollups (1m→5m→h→d→mo), no retention/cleanup jobs; Reports/Settings pages and comparisons are not implemented (diagnostics/export cover current reporting).
- **Reliability:** flush retry is in-memory/timer-driven (no durable queue/explicit backoff policy); `LastError` not cleared on recovery; monitoring session measurement version/provider details minimal.
- **UI/productization:** custom charts are minimal (no zoom/tooltips/per-point inspection) and not a11y-verified; app startup was smoke-tested on Wayland but no screenshot/a11y conformance pass; no systemd/D-Bus/Polkit packaging or `.deb`.
- **Docs:** ADR 0003 remains the v1 topology record; ADR 0004 records contract v3 and UI choices.

## Suggested next steps (prioritized)
1. **Validate the UDP RX interface fix:** run `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` again; the UDP RX row must resolve to `lo` and `check_interface` must pass before claiming UDP RX interface coverage. Then test namespaces/containers, VPN/virtual-interface behavior, and run longer/high-connection workloads for steady-state overhead and map growth.
2. **IPC hardening/topology:** signal continuity across daemon restarts, reconnect/error-mapping tests, and a documented decision for privileged capture + user-desktop IPC (system-bus activation/policy/Polkit or split capture/IPC) with evidence.
3. **UI accessibility & reporting:** Wayland/keyboard/screen-reader pass (especially charts and tables), chart interaction/textual summaries, Reports/History/Settings pages, and comparisons built on real data.
4. **Persistence/reporting:** rollups preserving totals, async retention/cleanup, and exports with full caveats.
5. **Productization:** systemd units, D-Bus/Polkit policy, `.deb` packaging, upgrade/uninstall data handling, recovery/system/security/privacy tests.

## How to build, test, and run
```bash
cd /home/amaechi/code/flow
dotnet build Flow.slnx -v minimal
dotnet test Flow.slnx -v minimal   # expect 35/35
```
- **Daemon (no root, unattributed interface totals):** `Flow__Monitor=procfs dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj`
- **Daemon (privileged, TCP+UDP attributed, prototype):** `sudo env Flow__Monitor=ebpf Flow__DatabasePath="$HOME/.local/share/flow/flow.db" dotnet run --project src/Flow.Daemon/Flow.Daemon.csproj` (IPC name registration may fail under sudo; DB persistence still works)
- **Desktop:** `dotnet run --project src/Flow.Desktop/Flow.Desktop.csproj` (uses same DB path/env; connects via D-Bus when available, else truthful unavailable + real local DB fallback)
- **eBPF controlled validation (root required):** rebuild prototype artifacts, then `sudo python3 scripts/validate-ebpf-attribution.py --size-mib 16` (payload per protocol: TCP then UDP). Review the Markdown report under `docs/measurements/` — per-protocol totals, `lo` interface attribution, unresolved-interface count, unattributed bytes, CPU/RSS — before making accuracy/coverage/overhead claims.
- **Inspect DB:** `sqlite3 ~/.local/share/flow/flow.db ".headers on" ".mode column" "select * from usage_buckets order by id desc limit 20;"`
- **IPC check (daemon running, session bus):** `busctl --user introspect org.flow.Daemon /org/flow/Daemon` (expect contract v3 query methods and `StatusChanged` signal); `busctl --user monitor org.flow.Daemon` to observe coalesced status signals.

## UI polish & design references
Local design guides for the four pages are in `flow_designs/{network_overview,apps_and_proceses,network_interface,diagnostics}/` (`screen.png` + `DESIGN.md`). Use these plus the external references below to guide polishing the Avalonia shell (GNOME-friendly, honest attribution/live-vs-historical/health states) — **inspiration/patterns, not pixel copies**:

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
- Decisions: `docs/decisions/` (0001–0006)
- Prototype: `prototype/bpf/README.md`, `scripts/check-prereqs.sh`, `scripts/validate-ebpf-attribution.py`
- Main code: `src/Flow.Daemon/Ipc/*`, `src/Flow.Application/Monitoring/UsageIngestionPipeline.cs`, `src/Flow.Infrastructure/Sqlite/*`, `src/Flow.Linux/Monitoring/*`, `src/Flow.Desktop/*`

## Starting fresh
1. Read this file, `docs/progress.md`, and `flow-project-plan.md`.
2. Run `dotnet build Flow.slnx` and `dotnet test Flow.slnx` to confirm baseline.
3. Pick the next prioritized item (recommend **remaining attribution coverage**: per-interface correlation, UDP/QUIC, namespaces/VPNs, and sustained overhead) and work incrementally, keeping all metrics/caveats real and evidence-backed.
