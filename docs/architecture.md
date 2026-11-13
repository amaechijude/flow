# Flow architecture (initial)

> Status: **Phase 0 baseline**. This document captures the intended architecture and boundaries from `flow-project-plan.md`, informed by environment inspection on Ubuntu 26.04.1 LTS. It is not a claim that components are implemented.

## 1. Goals and constraints

- **Product:** Native-feeling Ubuntu network usage monitor with per-application/per-process attribution, history, and reports.
- **Split runtime:** `Flow.Daemon` (system service, always-on collection/persistence) and `Flow.Desktop` (Avalonia GUI, user session).
- **Primary risk:** accurate, transparent per-process byte attribution on Linux (eBPF/Netlink/proc strategy must be proven before full product build).
- **Platform target:** Ubuntu 26.04 LTS, GNOME, Wayland, x86_64. Keep Linux-specific code isolated for future portability.
- **Non-goals (initial):** packet payload capture, browsing/DNS history, cloud telemetry, tray-required UX, process termination features.

## 2. High-level component model

```text
+-------------------+        D-Bus (versioned contract)        +--------------------+
|   Flow.Desktop    | <--------------------------------------> |    Flow.Daemon     |
| (Avalonia, user)  |    queries, commands, signals, health     | (Worker Service)   |
+---------+---------+                                          +----------+---------+
          |                                                               |
          | reads models via app services                                 | uses
          v                                                               v
+-------------------+     +--------------------+     +--------------------+ 
|  Flow.Application |<--->|  Flow.Contracts    |     |   Flow.Linux       |
|  (use cases)      |     |  (DTOs/events)     |     | (eBPF/Netlink/proc)|
+---------+---------+     +--------------------+     +----------+---------+
          |                                                      |
          v                                                      v
+-------------------+                                  +--------------------+
| Flow.Infrastructure|                                 |   Flow.Native      |
| (SQLite, retention)|                                 | (C ABI shim, only  |
+---------+---------+                                  |  if required)      |
          |                                            +--------------------+
          v
+-------------------+
|    Flow.Domain    |
| (entities/rules)  |
+-------------------+
```

### Proposed projects/assemblies (from plan)

| Project | Responsibility |
|---|---|
| `Flow.Domain` | Entities/value objects, measurement definitions, time-range/aggregation rules, identity models. |
| `Flow.Application` | Use cases, query/command contracts, aggregation/reporting orchestration, persistence/monitoring abstractions. |
| `Flow.Infrastructure` | SQLite persistence, migrations, repositories, retention/export services. |
| `Flow.Linux` | Process/interface discovery, Netlink integration, eBPF provider integration, native interop, diagnostics. |
| `Flow.Contracts` | Versioned GUI↔daemon request/response/event contracts (D-Bus-facing DTOs kept stable). |
| `Flow.Daemon` | .NET Worker Service host, monitoring orchestration, ingestion/aggregation pipeline, D-Bus service, health. |
| `Flow.Desktop` | Avalonia MVVM app, views/viewmodels, charts/controls, themes/assets, D-Bus client. |
| `Flow.Native` | Minimal C/C++ ABI boundary for BPF/native libs only when C# interop is insufficient; no business logic. |
| `tests/*` | Unit, integration, and target-environment system tests. |

Layout should follow the plan’s suggested `src/` structure but may be adjusted if implementation evidence shows a boundary is unnecessary. Keep naming/branding consistent (`Flow.*`).

## 3. Data flow (intended)

1. **Collection:** Linux provider observes network activity (validated eBPF hook) and enriches with process/interface metadata (Netlink, `/proc`, cgroup/namespace info as available).
2. **Normalization:** Provider emits usage events with explicit measurement semantics, attribution confidence, and stable process identity inputs.
3. **Ingestion:** Daemon processes events through bounded async pipeline, applies idempotent aggregation into time buckets, and persists to SQLite transactionally.
4. **Query:** Desktop requests status/current/historical data over D-Bus; daemon serves from aggregated tables (not raw event scans for long ranges).
5. **Presentation:** Desktop renders live vs historical distinctly, shows unattributed traffic/coverage/health, and never presents stale data as current.

## 4. Monitoring abstraction

Define a small lifecycle/event contract around the **validated** provider (do not force unsuitable shapes):

```csharp
public interface INetworkMonitor
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync(CancellationToken cancellationToken);
}
```

Provider implementations must surface: measurement point, attribution quality, dropped-event/backpressure signals, and unsupported-case diagnostics.

## 5. IPC (D-Bus)

- Daemon owns collection/persistence; GUI is an unprivileged client.
- Proposed operations: `GetMonitoringStatus`, `GetCurrentNetworkUsage`, `GetApplicationUsage`, `GetProcessUsage`, `GetInterfaceUsage`, `GetHistoricalUsage`, `GetAvailableInterfaces`, `SetMonitoringState`, `GetMonitoringHealth`.
- Proposed signals: `UsageUpdated`, `ApplicationDiscovered`, `ProcessStarted`, `ProcessExited`, `InterfaceChanged`, `MonitoringStateChanged`, `MonitoringError`.
- Requirements: versioned contract, caller validation/authorization for privileged actions, batched updates, reconnect support, explicit errors/health states.

**Library choice (pending verification):** evaluate maintained .NET D-Bus options (commonly Tmds.DBus in Linux .NET ecosystems) against current Ubuntu/.NET 10 compatibility and API stability before adoption. Record decision in `docs/decisions/`.

## 6. Persistence and aggregation

- Local **SQLite** database, UTC timestamps, 64-bit counters, migrations, indexes driven by real query patterns.
- Tiered buckets/retention as proposed in plan (raw transient → 1m/5m/hourly/daily/monthly) with async cleanup and rollups that preserve totals.
- Preserve distinction between **unknown/unattributed** and **zero**.

## 7. Packaging/runtime integration (planned)

- Debian package for Ubuntu 26.04: desktop launcher/icons, systemd service, D-Bus config, Polkit policy (if required), native components/runtime deps.
- Daemon runs with least privilege necessary for chosen provider; GUI never runs as root.
- Upgrades preserve settings/history; uninstall behavior for retained data must be explicit.

## 8. Open decisions (to resolve with evidence)

1. Exact eBPF hook/observation point and byte semantics (critical gate).
2. CO-RE/build strategy and whether `Flow.Native` shim is needed.
3. D-Bus library and contract versioning scheme.
4. Avalonia version and verified Wayland behavior on target session.
5. Application grouping heuristics and confidence rules (XDG/Snap/Flatpak metadata).
6. Database schema details and index set after query profiling.

See `docs/decisions/` and `docs/progress.md` for evolving status.
