# ADR 0004: Desktop shell/pages, custom chart rendering, exports, and IPC status signals (contract v3)

- **Date:** 2026-11-18
- **Status:** Accepted
- **Related:** ADR 0002 (Avalonia/Wayland verification), ADR 0003 (D-Bus session-bus v1)

## Context

The product plan's next increment was UI polish and structure backed by real persisted/daemon data, plus IPC hardening. ADR 0003 left the IPC contract at request/response only (no change signals, no automated daemon↔client tests), and ADR 0002 deferred Avalonia/Wayland verification. The UI work adds multiple pages, sortable/searchable tables, detail views, graphical charts, and file-picker exports; these choices affect dependencies, rendering behavior, and the IPC contract, so they are recorded here.

## Decisions

### 1. Shell with four pages sharing one snapshot

- `MainViewModel` owns shell state (range selection, interface filter, busy/notice/export state) and orchestrates four page view models: `OverviewPageViewModel`, `ApplicationsPageViewModel`, `InterfacesPageViewModel`, `DiagnosticsPageViewModel`.
- A refresh performs one coherent set of real daemon/local-SQLite queries and hands the same immutable `UsageSnapshot` to every page. Pages apply only presentation concerns (formatting, sorting, filtering, selection).
- Search and attribution filters hide rows locally; they never recompute cross-page totals, so headline numbers stay comparable to the exported/query-window totals.

Rationale: avoids duplicated queries and inconsistent numbers between pages, and keeps live-vs-historical/provenance handling in one place. Page-local sorting/filtering keeps filtering cheap and predictable.

### 2. Custom, dependency-free chart controls

- Added `TimeSeriesChart` (dual RX/TX area/line chart), `SplitBar` (RX/TX and attributed/unattributed splits), and `UsageBar` (per-row share) in `src/Flow.Desktop/Controls/`, rendered with Avalonia drawing primitives.
- No third-party charting package was added.

Rationale: keeps the rendering path on Avalonia's verified Skia/Wayland pipeline instead of introducing an unverified chart dependency and its own rendering/DPI behavior. Charts are derived only from real persisted 1-minute buckets; no synthetic points or interpolation are added.

Verification: the desktop app was launched directly (`dotnet src/Flow.Desktop/bin/Debug/net10.0/Flow.Desktop.dll`) as the non-root user inside the existing GNOME/Wayland session and remained running without startup/XAML errors for the smoke window. No screenshot-based visual or accessibility conformance claim is made.

Limitations: the chart is intentionally basic (no zoom/pan/tooltips or per-point inspection); the adjacent bucket list remains the accessible/textual source of truth; keyboard/screen-reader behavior of the chart has not been formally validated.

### 3. CSV and JSON export with a real file picker

- Export uses `StorageProvider.SaveFilePickerAsync` (`StorageProviderExportFilePicker`) to let the user choose the destination.
- `Flow__ExportDirectory` overrides the picker and writes `flow-usage-<utc-timestamp>.{csv|json}` for automation/tests.
- Both formats include range, interface filter, generation time, data source, provider, and explicit attribution/measurement/identity/coverage caveats where applicable.

Rationale: a picker is the expected desktop UX; an environment override keeps export testable and scriptable without UI interaction.

### 4. IPC contract v3: coalesced `StatusChanged` signal

- `FlowIpc.ContractVersion = 3`; new signal `StatusChanged` carrying JSON-serialized `StatusChangedSignalDto(ObservedUtc, Reason, Status)`.
- Daemon (`DbusIpcHostedService`) polls the real `IMonitoringStatusService` every 2 seconds and publishes only when the serialized snapshot changes (`initial` on first observation, `changed` afterwards). Signals are therefore coalesced to at most one per interval.
- Desktop (`DbusDaemonClient`) subscribes best-effort after connecting, exposes `StatusChanged`/`IsSignalSubscribed`/`LastSignalAtUtc`, and treats a signal as a "refresh now" hint: authoritative values are always re-queried.
- Older daemons that do not expose the signal remain usable; the client falls back to manual/periodic refresh and reports the signal state truthfully.

Rationale: a change signal removes the need for aggressive polling without inventing values or flooding the bus with per-event messages. Coalescing keeps the contract simple and keeps the daemon's diagnostics snapshot authoritative.

Evidence (2026-11-18, non-root GNOME/Wayland session, `Flow__Monitor=procfs`, temp DB): `busctl --user introspect` exposed `.StatusChanged signal s` and contract `i 3`; `busctl --user monitor org.flow.Daemon` observed JSON `StatusChanged` payloads roughly every 2s with real pipeline counters/state.

### 5. Private-bus integration tests

- `tests/Flow.SystemTests/DbusIntegrationTests.cs` starts a private `dbus-daemon --session --print-address --nofork`, registers the real `FlowDaemonDbusService`, and drives it with the real `DbusDaemonClient`.
- Coverage: contract version 3, JSON round-trip of every query method over real D-Bus, `StatusChanged` delivery, and publisher coalescing (no duplicate signal while the snapshot is stable) using the real hosted service with a 100 ms test interval.
- Internal test seams `AddressOverride` and `StatusPollInterval` exist only to point the hosted service at the private bus and shorten the poll interval.

Rationale: method-level unit tests cannot catch D-Bus signature/serialization or signal-matching mistakes; a real bus is the smallest faithful integration environment and needs no system-wide configuration.

## Consequences

- Positive: consistent cross-page data, no new UI dependency, real end-to-end IPC/signal coverage, and exports that preserve provenance and caveats.
- Negative/limits: custom charts are basic and not a11y-verified; signals are best-effort/coalesced (no replay or ordering guarantees beyond the latest snapshot; a client that subscribes after an emission waits for the next change); signal detection is poll-based (up to the 2 s interval of latency).
- Integration tests are Linux-only and require `dbus-daemon` on `PATH` (present on the target). They do not exercise system-bus policy/activation, a privileged daemon, or name-collision behavior.

## Follow-ups

- Accessibility/keyboard pass over the new pages and charts; optional textual chart summary improvements.
- Signal continuity across daemon restarts (resubscribe/reconnect behavior) and explicit reconnect/error-mapping tests.
- System-bus/systemd/Polkit topology remains deferred per ADR 0003.
