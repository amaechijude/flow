[text](../../../..)# Flow --- Ubuntu Network Usage Monitor

> **Product plan and implementation specification**\
> Target platform: Ubuntu 26.04 LTS · GNOME · Wayland\
> Primary stack: C# / .NET 10 LTS · Avalonia UI · Linux eBPF / Netlink

## 1. Product vision

**Flow** is a native-feeling Ubuntu desktop application that shows how
much internet/network data each application and process uses. It should
make it easy to answer questions such as:

-   Which applications are using the network right now?
-   Which applications have used the most data today, this week, or this
    month?
-   How much data was uploaded versus downloaded?
-   Which network interface or connection is responsible for the
    traffic?
-   How has usage changed compared with a previous period?

Flow consists of two independently executable components:

1.  **Flow Daemon** --- a background system service that collects and
    stores usage data. It continues monitoring when the desktop GUI is
    closed.
2.  **Flow Desktop** --- an Avalonia desktop application that displays
    live usage, history, charts, reports, and settings.

The GUI must remain useful without a system tray. Closing the GUI must
not stop the daemon or erase collected data.

### Product principles

-   Per-application attribution is the central feature; interface totals
    alone are not a substitute.
-   Be transparent about attribution quality and traffic that cannot be
    assigned confidently.
-   Keep monitoring local by default. No account, cloud service,
    telemetry, or external data transfer is required.
-   Do not capture packet payloads or build browsing-history,
    DNS-history, or content-inspection features.
-   Keep the daemon lightweight, resilient, and independent of the GUI.
-   Prefer a polished, accessible GNOME-friendly experience over visual
    complexity.
-   Never display stale measurements as if they were current.

## 2. Target platform and technology

### Supported target

-   Ubuntu 26.04 LTS
-   GNOME desktop
-   Wayland session
-   x86_64 as the primary architecture
-   ARM64 may be supported after the primary target is stable

The initial release should focus on the target above rather than
claiming compatibility with every Linux distribution or desktop
environment. Keep Linux-specific code behind clear interfaces so other
distributions can be considered later.

### Technology choices

  -----------------------------------------------------------------------
  Area                                Technology
  ----------------------------------- -----------------------------------
  Main language                       C#

  Runtime                             .NET 10 LTS

  Desktop UI                          Avalonia UI

  UI architecture                     MVVM

  Background service                  .NET Worker Service

  Local database                      SQLite

  GUI-to-daemon IPC                   D-Bus

  Service lifecycle                   systemd

  Privilege authorization             Polkit

  Process/interface discovery         Linux `/proc`, Netlink, and system
                                      APIs

  Per-process traffic accounting      eBPF-based approach, subject to a
                                      working prototype

  Native integration                  Small C ABI wrapper and C# P/Invoke
                                      where required

  Packaging                           Debian package (`.deb`)
  -----------------------------------------------------------------------

Use maintained libraries where they simplify integration, but verify
their current Linux, Wayland, .NET 10, and Ubuntu compatibility before
adopting them. Keep native code as small and isolated as practical.

## 3. Critical technical requirement: per-application attribution

Per-application network accounting is the most important technical risk
in this project. Validate it before building a large UI.

Linux interface counters can report traffic for an interface, but they
do not by themselves identify which process generated each byte. Netlink
is useful for discovering interfaces, routes, addresses, and
socket-related metadata; do not assume that Netlink alone provides
complete per-process byte accounting.

### Preferred investigation

Prototype an eBPF-based collection path that can associate network
activity with process identity and account for bytes at a clearly
defined observation point. Investigate the available kernel hooks and
their permissions, compatibility, overhead, and limitations on the
target Ubuntu release.

Use Netlink and `/proc` for complementary metadata, such as:

-   Network interfaces and their state
-   Interface addresses and routes
-   Process information and lifecycle
-   Socket metadata where available
-   Network namespace information where practical

Do not commit to a particular eBPF hook until a prototype demonstrates
that it provides the required attribution and measurement semantics on
the target kernel.

### Process identity

A PID alone is not a stable identity because Linux can reuse PIDs. A
process record should include a stable identity composed from at least:

-   PID
-   Process start time or another reliable process-generation identifier
-   Executable path when available
-   UID
-   Relevant cgroup or container metadata when available

Investigate socket cookies or equivalent identifiers where they help
associate socket events with process identity. Handle process exit, PID
reuse, short-lived processes, IPv4, IPv6, TCP, UDP, and network
namespaces to the extent supported by the chosen collection approach.

### Measurement semantics

Different observation points produce different byte counts. Application
payload bytes, transport-layer bytes, network-layer bytes, and
physical-interface bytes are not interchangeable.

Flow must:

-   Document exactly what its per-process counters measure.
-   Distinguish attributed process totals from interface totals.
-   Show unattributed traffic rather than silently discarding it.
-   Expose attribution coverage or confidence where meaningful.
-   Avoid double-counting traffic as it traverses virtual interfaces,
    bridges, VPN interfaces, and physical interfaces.
-   Explain that VPN and virtual-interface accounting can differ from
    physical-interface accounting.
-   Avoid claiming perfect attribution when the kernel or collection
    method cannot support it.

The UI should make the selected measurement model understandable without
overwhelming ordinary users.

### Technical gate

Before implementing the full product, build a command-line proof of
concept that:

1.  Identifies processes generating network traffic.
2.  Reports upload and download byte counters per process, using a
    documented measurement point.
3.  Handles process exit and PID reuse.
4.  Separates unattributed traffic.
5.  Compares aggregate results with interface counters and controlled
    test traffic.
6.  Reports its own CPU and memory overhead.
7.  Documents unsupported cases and attribution limitations.

If this gate fails, revisit the collection architecture before
proceeding.

## 4. Application discovery and grouping

Flow should present recognizable desktop applications rather than only
executable names.

Investigate application metadata from:

-   XDG desktop entries
-   Desktop file IDs
-   Executable paths and command lines
-   Application icons
-   Snap metadata
-   Flatpak metadata

Provide a clear fallback for processes that cannot be matched to a
desktop application. System services, background processes, and unknown
processes must remain visible when relevant.

Group processes under an application only when the relationship is
supported by reliable metadata. Do not merge unrelated processes merely
because their names look similar. Keep the underlying process-level
records available for inspection.

## 5. Architecture

Use a modular architecture with clear boundaries, but avoid introducing
abstractions that do not solve a real problem.

### Main components

**Flow.Domain** - Core entities and value objects - Usage measurement
definitions - Time-range and aggregation rules - Application and process
identity

**Flow.Application** - Use cases and application services - Query and
command contracts - Aggregation and reporting rules - Interfaces for
persistence and monitoring

**Flow.Infrastructure** - SQLite persistence - Database migrations -
Repository implementations - Retention and export services

**Flow.Linux** - Linux process discovery - Network interface discovery -
Netlink integration - eBPF provider integration - Native interop -
Linux-specific diagnostics

**Flow.Contracts** - Versioned GUI/daemon request and response
contracts - Usage update and lifecycle event contracts

**Flow.Daemon** - .NET Worker Service entry point - Monitoring
orchestration - Event processing and aggregation - D-Bus service -
Health reporting

**Flow.Desktop** - Avalonia application - Views and ViewModels - Charts
and reusable controls - Themes, icons, and accessibility resources -
D-Bus client

**Flow.Native** (only if needed) - Small C/C++ ABI boundary for native
libraries or eBPF integration - No business logic that belongs in the C#
application

**Tests** - Unit, integration, and system tests

**Packaging** - Debian package metadata - systemd service definition -
D-Bus service and policy files - Polkit policy - Desktop launcher and
icons - Install, upgrade, and uninstall scripts

### Suggested repository layout

``` text
flow/
├── src/
│   ├── Flow.Domain/
│   ├── Flow.Application/
│   ├── Flow.Infrastructure/
│   ├── Flow.Linux/
│   │   ├── Processes/
│   │   ├── NetworkInterfaces/
│   │   ├── Netlink/
│   │   ├── Ebpf/
│   │   └── Native/
│   ├── Flow.Contracts/
│   ├── Flow.Daemon/
│   ├── Flow.Desktop/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Converters/
│   │   ├── Assets/
│   │   └── Themes/
│   └── Flow.Native/
├── tests/
│   ├── Flow.Domain.Tests/
│   ├── Flow.Application.Tests/
│   ├── Flow.Infrastructure.Tests/
│   ├── Flow.Linux.Tests/
│   ├── Flow.Daemon.Tests/
│   └── Flow.SystemTests/
├── packaging/
├── docs/
├── scripts/
├── Directory.Build.props
├── Directory.Packages.props
├── Flow.sln
└── README.md
```

Adjust this layout if the implementation demonstrates that a project or
boundary is unnecessary. Keep namespaces, assembly names, package
identifiers, service names, D-Bus names, and desktop identifiers
consistently branded as Flow.

### Monitoring abstraction

Define a small monitoring abstraction around lifecycle and event
delivery. For example:

``` csharp
public interface INetworkMonitor
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<NetworkUsageEvent> ReadEventsAsync(
        CancellationToken cancellationToken);
}
```

The exact contract should follow the needs of the validated provider. Do
not force every native provider into an unsuitable abstraction.

## 6. Daemon and desktop communication

The daemon owns collection and persistence. The desktop application
queries it and receives updates over D-Bus.

Potential D-Bus operations:

-   `GetMonitoringStatus`
-   `GetCurrentNetworkUsage`
-   `GetApplicationUsage`
-   `GetProcessUsage`
-   `GetInterfaceUsage`
-   `GetHistoricalUsage`
-   `GetAvailableInterfaces`
-   `SetMonitoringState`
-   `GetMonitoringHealth`

Potential signals:

-   `UsageUpdated`
-   `ApplicationDiscovered`
-   `ProcessStarted`
-   `ProcessExited`
-   `InterfaceChanged`
-   `MonitoringStateChanged`
-   `MonitoringError`

These names are a proposed contract, not a claim that an implementation
already exists.

### IPC requirements

-   Version the contract so GUI and daemon upgrades can be handled
    safely.
-   Validate callers and authorize privileged operations.
-   Batch updates rather than sending excessive tiny messages.
-   Support GUI reconnection after daemon restart or temporary
    disconnection.
-   Return explicit errors and health states.
-   Do not allow an unprivileged GUI to request arbitrary privileged
    operations.

## 7. Data model and persistence

Use SQLite for local history. Persist enough information to retain
application and process history after a process exits or the daemon
restarts.

### Suggested entities/tables

**Applications** - Stable application key - Display name - Desktop file
ID, when available - Executable path, when available - Icon metadata or
resolvable icon reference - First seen and last seen timestamps

**Processes** - Stable process identity - PID - Process start time -
Parent process identity when available - Executable path and command
metadata as appropriate - UID - Associated application key, if
confidently identified - First seen and last seen timestamps

**NetworkUsageBuckets** - Application key, nullable for unattributed
traffic - Process identity, nullable where not available - Interface
identifier - Bucket start and duration - Uploaded bytes - Downloaded
bytes - Measurement type - Attribution confidence or quality metadata

**NetworkInterfaces** - Stable interface identifier where possible -
Interface name - Interface type - First seen and last seen timestamps -
Relevant metadata

**InterfaceUsageBuckets** - Interface identifier - Bucket start and
duration - Uploaded bytes - Downloaded bytes - Measurement type

**MonitoringSessions** - Session start and end - Provider and
measurement version - Health state - Collection gaps or diagnostic
metadata

**UserSettings** - Monitoring preferences - Retention configuration -
Display preferences - Notification preferences

Use 64-bit counters. Make aggregation idempotent and transactional. Use
database migrations and indexes based on actual query patterns. Preserve
the distinction between unknown attribution and zero usage.

### History aggregation and retention

Use tiered aggregation to control database growth while preserving
totals. Initial proposed retention:

  Granularity                                          Retention
  ------------------ -------------------------------------------
  Raw events           Transient; process and aggregate promptly
  1-minute buckets                                      48 hours
  5-minute buckets                                        7 days
  Hourly buckets                                         90 days
  Daily buckets                                          2 years
  Monthly buckets                                      Long-term

These are defaults to validate, not immutable limits. Make retention
configurable where practical. Cleanup must run asynchronously and must
not block collection or make the UI unresponsive. Verify that totals
remain consistent when fine-grained buckets are rolled into coarser
buckets.

## 8. Time ranges, time zones, and comparisons

The product must support the following time filters:

-   Live
-   Last hour
-   Rolling 6 hours
-   Rolling 12 hours
-   Rolling 24 hours
-   Today
-   Yesterday
-   Rolling 7 days
-   This week
-   Last week
-   Rolling 30 days
-   This month
-   Last month
-   Rolling 90 days
-   Last 6 months
-   Last 12 months
-   This year
-   Last year
-   Custom range

Clearly distinguish rolling periods from calendar periods. For example,
"Last 24 hours" is not the same as "Today."

### Time handling rules

-   Store timestamps in UTC.
-   Display times in the operating system's local time zone.
-   Do not hardcode a country or UTC offset.
-   Calculate calendar boundaries using the system time zone and its
    daylight-saving rules.
-   Use half-open query intervals: `[start, end)`.
-   Define week-start behavior consistently and document it.
-   Handle daylight-saving transitions and time-zone changes correctly.

### Comparison periods

Support comparisons such as:

-   Today vs. yesterday
-   This week vs. last week
-   This month vs. last month
-   Current rolling 24 hours vs. previous rolling 24 hours

Show absolute and percentage differences. Handle a zero previous-period
value without division errors; display the percentage as unavailable or
otherwise clearly defined.

### Chart resolution

Chart resolution is a presentation choice and must not be confused with
the underlying collection frequency.

  Selected range     Suggested chart interval
  ------------------ --------------------------
  Live               1--5 seconds
  Last hour          1 minute
  6 hours            5 minutes
  12 hours           5--10 minutes
  24 hours / Today   15 minutes
  7 days             1 hour
  30 / 90 days       1 day
  6 months           1 day or 1 week
  12 months          1 week or 1 month

Choose the best available stored aggregation and avoid querying huge
numbers of raw records for long periods.

## 9. Desktop experience and navigation

Build a polished, responsive desktop utility that feels at home on
GNOME. Use GNOME Settings, GNOME System Monitor, and Windows Task
Manager as broad usability references, not as designs to copy.

### Main navigation

-   **Overview**
-   **Network Usage**
    -   Applications
    -   Processes
    -   Network Interfaces
-   **Reports**
    -   History
    -   Comparisons
-   **Settings**
    -   General
    -   Monitoring
    -   Data Retention
    -   Appearance
    -   About

### Overview

Show:

-   Total downloaded and uploaded data for the selected period
-   Current download and upload speeds
-   Network interface selector
-   Time-range selector
-   Usage-over-time chart
-   Top applications by usage
-   Recent activity
-   Monitoring health and attribution coverage

Make current speed visually distinct from historical totals. If
monitoring is paused or unavailable, show that state rather than
presenting an old speed as current.

### Applications

Provide a searchable, sortable list with:

-   Application icon and name
-   Total usage
-   Downloaded and uploaded bytes
-   Current download and upload speed
-   Last active time

Allow sorting by total usage, upload, download, current speed, last
active, and name.

An application detail view should include its executable or desktop
identity where available, associated processes, current rates,
historical usage, interface breakdown, and first/last seen information.

### Processes

Display:

-   PID
-   Process name
-   Parent process, where available
-   Executable path
-   Associated application, if known
-   Current upload/download speed
-   Historical upload/download totals
-   Process start time
-   Last network activity

Retain historical records for exited processes. Do not add process
termination or process-killing functionality to the initial release.

### Network interfaces

Display Wi-Fi, Ethernet, VPN, loopback, and virtual interfaces where
available.

For each interface, show status, addresses where available, current
rates, and historical totals. Allow users to select one interface or all
interfaces.

Explain that VPN and virtual interfaces may expose different accounting
points and that adding interface totals together can double-count
traffic.

### Reports and history

Provide:

-   Period totals
-   Average and peak rates
-   Top applications
-   Upload/download distribution
-   Usage trends
-   Period comparisons
-   Interface breakdown

Support CSV export and optionally JSON export. Include the selected date
range, time zone, interface filter, measurement method, attribution
caveats, and export generation time in the export metadata.

### Settings

**General** - Launch at login - Start monitoring automatically - Start
minimized, if supported - Default page

**Monitoring** - Enable or pause monitoring - Collection
provider/status - Collection frequency where configurable - Interface
inclusion/exclusion - Show or hide system processes - Show unattributed
traffic

**Data retention** - Retention settings - Current database size - Clear
history with explicit confirmation

**Appearance** - System/light/dark theme - Accessible contrast and text
sizing options where practical

**Notifications** - Configurable usage thresholds - Optional alerts for
unusually high usage - Notification rate limits to avoid spam

**About** - Version - Monitoring provider and measurement information -
Diagnostics and limitations - License and project links

### Visual design system

-   Use a restrained, consistent visual language.
-   Support light and dark themes.
-   Use tabular numerals for rates and byte counts.
-   Use clear units: B, KiB, MiB, GiB, and appropriate per-second units.
-   Keep download and upload colors consistent throughout the app.
-   Use semantic colors for warnings, errors, and success, with labels
    or icons so color is never the only signal.
-   Support keyboard navigation, accessible names, screen readers, and
    sensible focus order.
-   Keep animations subtle and avoid unnecessary motion.
-   Use responsive layouts that work at common laptop window sizes.

A system tray indicator may be added later. GNOME tray support can
depend on extensions and desktop configuration, so the application must
remain fully usable without it.

## 10. Security and privacy

The GUI should run as the logged-in user without root privileges. The
daemon should use the least privilege necessary for its selected
monitoring provider.

Investigate systemd sandboxing and Linux capabilities rather than
running the entire application with unrestricted root access. Use Polkit
for operations that genuinely require authorization.

Security requirements:

-   Validate all D-Bus callers and inputs.
-   Restrict privileged operations to explicit authorized actions.
-   Protect the SQLite database with appropriate filesystem permissions.
-   Do not store packet payloads.
-   Do not collect browsing history or DNS query history by default.
-   Do not transmit usage information externally.
-   Do not expose arbitrary shell execution through the daemon.
-   Document the privileges required by eBPF and other collection
    mechanisms.
-   Ensure logs do not accidentally contain sensitive command-line
    arguments or unnecessary personal data.

## 11. Performance and reliability

Monitoring should have low and predictable CPU and memory overhead.

Requirements:

-   Use asynchronous I/O.
-   Use bounded queues or channels for event processing.
-   Batch database writes.
-   Apply backpressure deliberately.
-   If events are dropped because a queue overflows, expose the loss in
    diagnostics and do not silently claim complete accounting.
-   Keep chart and table queries efficient.
-   Keep the GUI responsive while data refreshes.
-   Avoid excessive D-Bus message frequency.
-   Ensure retention and export work do not interrupt collection.

### Health states

Expose clear states such as:

-   Initializing
-   Monitoring
-   Paused
-   Degraded
-   Unavailable

Include a last-update timestamp and useful diagnostic information. A
disconnected GUI must be able to reconnect to the daemon.

### Recovery scenarios

Test and handle:

-   Network disconnect/reconnect
-   Wi-Fi changes
-   VPN connect/disconnect
-   Interface creation and removal
-   Suspend/resume
-   Daemon restart
-   GUI restart
-   eBPF provider failure
-   Database interruption or migration failure
-   Process exit during collection
-   System shutdown while data is being persisted

Use systemd restart behavior where appropriate. Do not conceal
collection gaps or provider failures.

## 12. Testing strategy

### Unit tests

Cover:

-   Byte and rate calculations
-   Unit formatting
-   Process identity and PID reuse
-   Application grouping rules
-   Time-range boundaries
-   Time-zone and daylight-saving behavior
-   Aggregation and roll-up correctness
-   Idempotent event processing
-   Retention calculations
-   Comparison calculations, including zero previous-period usage

### Integration tests

Cover:

-   SQLite persistence and migrations
-   D-Bus request/response contracts
-   Daemon lifecycle
-   Monitoring provider integration
-   Process and interface discovery
-   Export generation
-   Retention and aggregation jobs

### System tests

On the target Ubuntu environment, verify:

1.  Generate controlled traffic from multiple applications and compare
    attribution.
2.  Compare aggregate accounting with independent interface counters.
3.  Verify upload and download directions.
4.  Exit a process and confirm its history remains available.
5.  Restart the daemon and verify history and monitoring recovery.
6.  Disconnect and reconnect Wi-Fi.
7.  Connect and disconnect a VPN.
8.  Suspend and resume the machine.
9.  Test every supported time range.
10. Close the GUI and confirm the daemon continues monitoring.
11. Run under the intended Wayland desktop session.
12. Verify that unattributed traffic and collection gaps are visible.

Document test conditions and known measurement differences. Do not treat
a successful build as proof that per-process accounting is correct.

## 13. Packaging and installation

The initial distribution target is a Debian package for Ubuntu 26.04
LTS.

The package should install and configure:

-   Flow Desktop launcher and icons
-   Flow Daemon systemd service
-   D-Bus service configuration
-   Polkit policy, if required
-   Native components and their runtime dependencies
-   Database location and permissions
-   Upgrade and uninstall behavior

Installation should not require users to manually compile eBPF programs
or edit configuration files. If a kernel feature or permission is
unavailable, explain the limitation and expose a useful health state.

Upgrades must preserve user settings and history. Uninstallation
behavior for retained data must be clear and intentional; do not
silently delete a user's history.

## 14. Implementation phases

Work incrementally. The per-process accounting prototype is the main
technical gate.

### Phase 0 --- Environment and feasibility research

-   Inspect Ubuntu version, kernel, architecture, and .NET SDK.
-   Verify Avalonia support in the target Wayland environment.
-   Investigate D-Bus integration options for .NET.
-   Investigate eBPF libraries, hooks, permissions, and kernel
    compatibility.
-   Investigate Netlink libraries or a minimal native implementation.
-   Write architecture, monitoring, attribution, security, database, and
    implementation-plan documents.
-   Record decisions and unresolved risks.

**Exit criteria:** a documented, evidence-based technical design and a
realistic path to per-process byte attribution.

### Phase 1 --- Monitoring proof of concept

-   Build a command-line prototype.
-   Discover processes and interfaces.
-   Attribute controlled network traffic to processes.
-   Handle process lifecycle and PID reuse.
-   Track unattributed traffic.
-   Compare with independent interface totals.
-   Measure overhead and document limitations.

**Exit criteria:** repeatable evidence that the proposed monitoring
approach works sufficiently on the target environment.

### Phase 2 --- Domain and persistence foundation

-   Create the solution and project structure.
-   Implement core identities, measurement types, and time-range rules.
-   Add SQLite schema and migrations.
-   Implement event ingestion and transactional aggregation.
-   Add unit and integration tests.

### Phase 3 --- Daemon

-   Implement the Worker Service.
-   Integrate the validated monitoring provider.
-   Add process and application discovery.
-   Add health states and diagnostics.
-   Add systemd packaging and lifecycle handling.

### Phase 4 --- Historical reporting

-   Implement time-range queries and roll-ups.
-   Implement retention policies.
-   Implement comparison calculations.
-   Implement CSV export and optional JSON export.
-   Validate historical totals against collected events.

### Phase 5 --- Desktop GUI

-   Create the Avalonia application and theme system.
-   Implement Overview.
-   Implement Applications and application details.
-   Implement Processes and Network Interfaces.
-   Implement History, Comparisons, and Settings.
-   Add accessible states, empty states, loading states, and error
    states.

### Phase 6 --- D-Bus integration

-   Define and version the IPC contract.
-   Implement daemon service methods and signals.
-   Implement the desktop client.
-   Add authorization, reconnect, and compatibility tests.

### Phase 7 --- Hardening and release

-   Test recovery scenarios and long-running operation.
-   Profile CPU, memory, and database growth.
-   Review security and privacy.
-   Validate Wayland behavior.
-   Build and test the `.deb` package.
-   Write installation, upgrade, troubleshooting, and uninstall
    documentation.

## 15. Development-agent instructions

If an AI coding agent is used to implement Flow, give it these rules:

1.  Work in small, verifiable increments. Build and run relevant tests
    after each meaningful change.
2.  Begin with environment inspection and the monitoring feasibility
    prototype. Do not start by building the dashboard.
3.  Research current official documentation and verify library APIs
    before using them.
4.  Do not invent APIs, kernel capabilities, D-Bus methods, or package
    behavior.
5.  Do not replace per-process attribution with interface totals while
    claiming the requirement is complete.
6.  Keep measurement semantics, attribution limitations, and collection
    gaps documented.
7.  Do not run the desktop GUI as root.
8.  Avoid unnecessary abstractions, speculative features, and premature
    optimization.
9.  Add tests for time boundaries, aggregation, process identity, and
    persistence.
10. Maintain `docs/progress.md` with completed work, current work, test
    results, blockers, and next steps.
11. Record important technical decisions in `docs/decisions/`.
12. Do not leave placeholder implementations or silently swallow errors
    in completed features.
13. Keep the application usable when the tray is unavailable.
14. Preserve existing data across upgrades and make destructive
    operations explicit.

### First task for the coding agent

Before implementing product features:

1.  Inspect the development environment and target Ubuntu/kernel
    details.
2.  Confirm .NET 10 SDK availability.
3.  Verify Avalonia's current Wayland behavior for the target
    environment.
4.  Investigate maintained .NET D-Bus options.
5.  Prototype eBPF process attribution and use Netlink for complementary
    network metadata.
6.  Measure accuracy and overhead with controlled traffic.
7.  Write:
    -   `docs/architecture.md`
    -   `docs/monitoring.md`
    -   `docs/attribution.md`
    -   `docs/security.md`
    -   `docs/database.md`
    -   `docs/implementation-plan.md`
    -   `docs/progress.md`
8.  Present findings, limitations, and the recommended implementation
    path before proceeding to the full daemon or GUI.

## 16. Definition of done

Flow's initial release is ready only when:

-   It installs and runs on the target Ubuntu 26.04 LTS GNOME Wayland
    environment.
-   The daemon can monitor independently of the GUI.
-   Per-process and per-application upload/download accounting works to
    the documented level of accuracy.
-   Unattributed traffic and measurement limitations are visible.
-   Process identity remains correct across PID reuse and process exit.
-   Application grouping is reliable and unknown processes remain
    inspectable.
-   Interface totals and per-process totals are clearly distinguished.
-   All specified time ranges and comparisons work correctly.
-   Historical data persists across daemon and machine restarts.
-   Aggregation and retention preserve totals.
-   The GUI provides Overview, Applications, Processes, Interfaces,
    Reports, and Settings.
-   Monitoring health and stale data are represented honestly.
-   D-Bus access and privileged operations are secured.
-   Performance is acceptable during sustained monitoring.
-   Unit, integration, and system tests cover the critical paths.
-   The Debian package installs, upgrades, and uninstalls predictably.
-   Documentation explains setup, measurement semantics, limitations,
    troubleshooting, and privacy.

## 17. Useful technical references

Use current official documentation and verify version-specific details
during implementation.

-   [Ubuntu release information](https://releases.ubuntu.com/)
-   [.NET support
    policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)
-   [Avalonia documentation](https://docs.avaloniaui.net/)
-   [Linux kernel documentation](https://docs.kernel.org/)
-   [Linux BPF documentation](https://docs.kernel.org/bpf/)
-   [Linux Netlink
    documentation](https://docs.kernel.org/userspace-api/netlink/index.html)
-   [systemd
    documentation](https://www.freedesktop.org/software/systemd/man/)
-   [D-Bus
    specification](https://dbus.freedesktop.org/doc/dbus-specification.html)
-   [Polkit documentation](https://polkit.pages.freedesktop.org/polkit/)

------------------------------------------------------------------------

**Project name:** Flow\
**Product category:** Ubuntu desktop network-usage monitor\
**Primary objective:** accurate, transparent, persistent per-application
network usage\
**First engineering milestone:** prove per-process byte attribution on
the target Ubuntu system
