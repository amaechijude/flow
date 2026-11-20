# Flow data lifecycle policy (`.deb` packaging)

This document defines what happens to Flow's persisted data across package
lifecycle events. It applies to the prototype `.deb` built by `build-deb.sh`.

## What is package data and what is user data

| Path | Owner | Treatment |
| --- | --- | --- |
| `/usr/lib/flow/**`, systemd units, D-Bus/desktop files, `/usr/share/doc/flow` | package | Managed by dpkg; removed on `remove` and `purge`. |
| `/var/lib/flow/flow.db` | user measurement data | Created by the collector at runtime, not shipped in the package. Preserved unless explicitly purged with the opt-in below. |
| `/var/lib/flow/flow.db.pre-upgrade` | upgrade safety copy | Created by `prerm upgrade` after services stop. Overwritten on each upgrade. |
| `user_settings` table inside `flow.db` | user preference data | Same treatment as the rest of the database. |
| system group `flow` | shared local group | Created by `postinst`; intentionally not removed on purge (other users or local configuration may depend on it). Remove manually with `sudo groupdel flow` if desired. |

The database contains real measured usage and provenance. Deleting it destroys
evidence that cannot be reconstructed, so it is never removed implicitly.

## Lifecycle

| Action | Behaviour |
| --- | --- |
| Fresh install | Create `flow` group and `/var/lib/flow` (`root:flow 2770`); fix DB ownership/permissions if a previous database exists; enable the collector. |
| Upgrade | Stop services, take a quiescent copy of `flow.db` to `flow.db.pre-upgrade`, install new files, re-assert permissions, re-enable the collector. The database, history and settings are preserved. Schema migrations run at daemon start and are additive/idempotent (migrations 1–4). |
| `dpkg -r flow` (remove) | Stop services and remove package files. **All data under `/var/lib/flow` is preserved.** |
| `dpkg -P flow` (purge, default) | Remove package files. **All data under `/var/lib/flow` is still preserved** by default. |
| `dpkg -P flow` with opt-in | Same as purge, but also `rm -rf /var/lib/flow`. Opt in with `FLOW_PURGE_DATA=yes dpkg -P flow` or by creating `/etc/flow/purge-data.conf` containing `PURGE_DATA=yes`. |

`postrm` only calls `rm -rf /var/lib/flow` when that opt-in is present. The
config-file check uses an exact `PURGE_DATA=yes` match and is never sourced as
shell code (no root code execution from a user-writable file).

Note that sudo strips environment variables by default: use
`sudo env FLOW_PURGE_DATA=yes dpkg -P flow` (or the config file), not
`FLOW_PURGE_DATA=yes sudo dpkg -P flow`. `packaging/uninstall-clean.sh` handles
this and the other leftovers; see `packaging/README.md`.

## Caveats

- `flow.db.pre-upgrade` is a best-effort file copy taken after the collector is
  stopped on upgrade. It is a convenience rollback point, **not** a backup
  solution: only the most recent upgrade's copy is kept, and it is not verified.
  Take real backups if the history matters.
- Removing the `flow` group after purge is deliberately left to the operator.
- This policy changes only package lifecycle behaviour. It does not change, and
  must not weaken, the attribution/measurement caveats in the root README.
- A purge with the opt-in is irreversible. There is no "undo" for deleted
  measurement data.
