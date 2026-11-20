# ADR 0012 — Package data lifecycle

- Status: Accepted
- Date: 2026-11-20

## Context

The prototype `.deb` installs binaries, systemd units, D-Bus/desktop files and
documentation under `/usr`. At runtime the collector also creates
`/var/lib/flow/flow.db`, which holds real measured usage plus the
`user_settings` table. `postinst`/`prerm` existed as drafts, but there was no
explicit policy for upgrade, remove, or purge, and no `postrm`.

Deleting the database destroys evidence that cannot be reconstructed, while
silently keeping everything on purge conflicts with the expectation that purge
is a deliberate full removal.

## Decision

1. `/var/lib/flow` is **user measurement data**, not package data. It is never
   removed by `remove` or by a default `purge`.
2. On upgrade, `prerm upgrade` stops services and copies `flow.db` to
   `flow.db.pre-upgrade` (best effort, overwritten each upgrade). New binaries
   are installed, `postinst configure <old-version>` re-asserts permissions and
   reports the upgrade. Schema migrations remain additive and idempotent.
3. Destructive purge is **opt-in**: `FLOW_PURGE_DATA=yes dpkg -P flow`, or a
   file `/etc/flow/purge-data.conf` containing `PURGE_DATA=yes`. Only then does
   `postrm purge` call `rm -rf /var/lib/flow`.
4. The config file is parsed with an exact `grep`, never sourced as shell code,
   so a user-writable file cannot execute commands as root during purge.
5. The `flow` system group is intentionally left in place on purge; removing it
   could break other users or local configuration. Operators can delete it
   manually.
6. Maintainer scripts accept internal `FLOW_DATA_DIR` / `FLOW_PURGE_CONFIG`
   env overrides so the packaging tests can simulate the lifecycle without
   touching host paths.

## Consequences

- History and settings survive reinstall, upgrade, remove and default purge;
  this matches the "real data, never fabricate" requirement.
- Complete data removal requires a deliberate opt-in and is irreversible.
- The pre-upgrade copy is a convenience, not a verified backup; real backups
  remain the operator's responsibility.
- `test-deb.sh` stubs the lifecycle commands and asserts the no-delete default
  plus both opt-in paths; `test-install.sh` exercises install → upgrade →
  remove → default purge → opt-in purge in a disposable `ubuntu:24.04` container.
- This decision does not alter any attribution/measurement caveat.
