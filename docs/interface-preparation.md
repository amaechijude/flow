# Interface preparation (desktop GUI + daemon IPC)

Date: 2026-11-15  
Status: preparation/incremental scaffolding (no final D-Bus library pinned yet; see ADR 0002)

> **Update (2026-11-18):** Much of this preparation is now implemented: Tmds.DBus session-bus IPC with contract v3, daemon query/status methods, coalesced `StatusChanged` signals, Avalonia shell with four pages, graphical charts, and CSV/JSON picker exports (see ADR 0003/0004 and `docs/progress.md`). Still pending: control operations/authorization, process/interface detail depth, Reports/History/Settings, system-bus/activation/Polkit packaging, and full Wayland/a11y verification.

## Goal
Build a GNOME-friendly Avalonia desktop UI that queries the Flow daemon for live/historical usage and health, without replacing per-process attribution semantics with interface totals and without requiring a system tray.

## Current baseline
- **Daemon:** .NET worker (`src/Flow.Daemon`) with DB migration, monitor selection, ingestion pipeline, diagnostics logging. No IPC/query endpoint yet.
- **Desktop:** Avalonia MVVM template (`src/Flow.Desktop`) with placeholder `MainWindow`.
- **Contracts:** initial DTOs in `src/Flow.Contracts` (`MonitoringStatusDto`, `UsageSummaryDto`, `ApplicationUsageDto`, `UsageTimePointDto`).
- **Query path:** `Flow.Application.Queries.IUsageQueryService` implemented by `Flow.Infrastructure.Sqlite.UsageRepository` (summary/top apps/time series over `[start,end)` UTC 1-minute buckets). Registered in daemon DI for upcoming IPC service.

## Recommended UI/IPC architecture

### 1) Keep transport behind an adapter (do first)
- Define/version request/response and event contracts in `Flow.Contracts` (transport-agnostic DTOs/enums only).
- Add a daemon-side application service that composes `IUsageQueryService`, `UsageIngestionPipeline` diagnostics/controller, and time-range resolution for UI requests.
- Implement IPC as a thin adapter (candidate to spike: **Tmds.DBus**; verify maintenance, .NET 10, Ubuntu 26.04/GNOME session/system bus fit per ADR 0002) exposing explicit methods/signals; do not couple ViewModels directly to a D-Bus library.

### 2) Proposed initial contract surface (v1, versioned)
- **Health/status:** state, provider, measurement description, last event/flush/update, counters (received/dropped/aggregated/batches/writeErrors), last error, attribution/limitation notes (e.g. ebpf TCP+UDP scope, unresolved interfaces, unattributed buckets present).
- **Queries:** usage summary, top applications, time series, (next) process list/details, interface list/usage — all accepting `[start,end)` UTC bounds, interface filter, and explicit range kind/label metadata.
- **Control:** start/pause/stop monitoring where authorized (privileged operations must be validated/authorized; GUI should run unprivileged).
- **Signals/updates (batched):** status/health changed, usage snapshot/refresh hint, monitoring error; support reconnect and stale-data indication.

### 3) Desktop (Avalonia MVVM) structure
- **Shell/navigation:** Overview, Applications, Processes, Interfaces, Reports/History, Settings (usable without tray).
- **ViewModels:** consume an `IDaemonClient` abstraction (async queries + observable status/updates) so UI can run against a mock client before IPC is ready.
- **Presentation rules:** distinguish live rate vs historical totals, show unattributed traffic/coverage and measurement/provider caveats, never show stale values as current, use clear units and consistent up/down semantics.
- **Resilience:** explicit disconnected/degraded/unavailable states, retry/reconnect, loading/empty/error views.

### 4) Incremental build order
1. Spike/decide D-Bus library on target Wayland session (method + signal + reconnect), then supersede ADR 0002 with pinned choice.
2. Implement daemon query/status service over `IUsageQueryService` + pipeline diagnostics and expose read-only IPC methods (status, summary, top apps, time series).
3. Add `Flow.Desktop` `IDaemonClient` + mock implementation; build Overview/Applications read-only views with honest health/attribution states.
4. Add control operations, process/interface queries, history/comparisons, exports, and packaging/policies (systemd, D-Bus, Polkit) with tests.

## Open verification items
- Final D-Bus library and bus topology (session vs system, activation, auth/Polkit).
- Avalonia behavior on Ubuntu 26.04 GNOME Wayland (rendering, input, scaling, accessibility) — smoke test before heavy UI work.
- How to represent live rates vs persisted 1-minute buckets in the UI without implying unsupported precision.
