# ADR 0003: D-Bus IPC library and session-bus v1 topology (supersedes ADR 0002 IPC choice)

- **Date:** 2026-11-15
- **Status:** Accepted (v1, evidence-backed for session bus; system-bus deployment deferred)
- **Deciders:** Flow implementation
- **Supersedes (IPC portion):** ADR 0002

## Context
Flow requires a versioned GUI↔daemon IPC mechanism on Ubuntu 26.04 (GNOME/Wayland, .NET 10 target) so the unprivileged desktop can query real daemon status and historical usage without mocking critical data. ADR 0002 deferred the D-Bus library and topology pending verification. A D-Bus scaffold has now been implemented and validated on-target.

## Decision
- **Library:** `Tmds.DBus` **0.95.2** (NuGet) for daemon and desktop IPC integration on .NET 10+.
- **Bus/topology (v1):** **Session bus, per-user**. Daemon registers when a session/system D-Bus address is available (prefers session), best-effort with graceful degradation/logging if unavailable or name registration fails. This fits the current per-user execution/DB model and was validated on the target user session.
- **Service naming/contract:** `org.flow.Daemon` service, `/org/flow/Daemon` object, `org.flow.Daemon1` interface, `ContractVersion = 1` (`Flow.Contracts.Ipc.FlowIpc`).
- **Transport shape:** request/response D-Bus methods returning JSON-serialized DTO payloads (`IpcJson`) for contract stability: `GetContractVersion`, `GetStatusJson`, `GetUsageSummaryJson`, `GetTopApplicationsJson`, `GetUsageTimeSeriesJson`. Historical query fallback to direct local DB reads in desktop remains a resilience path to the same persisted data when IPC is unreachable; live daemon diagnostics are only reported from real IPC (truthful unavailable otherwise).

## Evidence (2026-11-15, Ubuntu 26.04.1 GNOME Wayland, session bus)
- Daemon (`Flow__Monitor=procfs`) logged successful registration: `Flow D-Bus IPC started ... service=org.flow.Daemon path=/org/flow/Daemon` on `unix:path=/run/user/1000/bus`.
- `busctl --user` showed the name owned and introspection exposed `org.flow.Daemon1` with the expected method signatures.
- `GetContractVersion` returned `1`; `GetStatusJson` returned real pipeline diagnostics (Monitoring, provider, counters/timestamps); query methods returned real JSON results from the daemon query path.
- After daemon stop, calls failed with `ServiceUnknown` (no fabricated metrics). User-provided desktop UI evidence showed connected Monitoring/procfs status and real unattributed historical totals.

## Consequences
- Unblocks real GUI↔daemon integration for per-user/session use without requiring root-owned system-bus policy for read-only queries.
- No change/status signals yet (polling/request-response), no control operations or Polkit authorization exposed, and no multi-instance/name-collision strategy beyond registration failure.
- **System-bus / systemd activation / D-Bus policy files** (and any privileged multi-user collector model) are explicitly deferred; they require separate design, policy/activation artifacts, and authorization before adoption.

## Follow-ups
- Add reconnect/timeout/error-mapping hardening and contract/integration tests.
- Consider batched signals for status/usage updates.
- If a system-wide daemon is needed, add D-Bus policy + systemd activation + Polkit and re-evaluate bus topology in a superseding ADR with evidence.
