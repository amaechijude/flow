# ADR 0011: Session-bus IPC needs no Polkit for the first package

- Status: Accepted
- Date: 2026-11-19

## Context

The packaged topology splits privileged collection from user-session IPC
(`docs/packaging-plan.md`, ADR 0010 area). The root `flow-collector.service`
writes SQLite but registers no D-Bus name. The unprivileged `flow-daemon.service`
runs as the logged-in user under `systemd --user` and registers
`org.flow.Daemon` on that user's session bus.

The daemon exposes contract v3 query methods (`GetStatus`, summary, top apps,
processes, interfaces, time series) and a coalesced `StatusChanged` signal. It
does not expose control operations, privilege escalation, or a system bus
service.

## Decision

Do **not** ship a Polkit policy or system-bus activation for the first package.

Rationale:

- D-Bus session bus access is already scoped to the logged-in user; cross-user
  access is not part of the topology.
- All exposed methods are read-only queries over data the same user can already
  read from `/var/lib/flow/flow.db` via the `flow` group.
- The privileged collector has no D-Bus interface, so there is no privileged
  method to authorize.

Add Polkit only if a future version introduces privileged control operations
(for example starting/stopping the collector or changing configuration from the
desktop), or if queries move to the system bus for multi-user access.

## Consequences

- No new policy files or authorization prompts in the first package.
- Any process running in the user's session can call the read-only IPC methods;
  this is the same trust boundary as the desktop app and its direct SQLite
  fallback, and must be documented.
- Multi-user isolation is not solved; it would require per-user DBs or a broker
  and is out of scope for the first package.
