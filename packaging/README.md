# Packaging scaffolding and prototype `.deb`

These files implement the systemd/D-Bus topology from `docs/packaging-plan.md`.
A prototype `.deb` can be built and validated with the scripts below. It is
still a prototype: not all release-quality items are complete.

## Files

- `flow-collector.service` — system unit, root, `Flow:Mode=collector`, starts the
  privileged eBPF loader, no D-Bus registration.
- `flow-daemon.service` — user unit, `Flow:Mode=session`, D-Bus IPC only.
- `org.flow.Daemon.service` — D-Bus session activation for the user daemon.
- `flow.desktop` — desktop launcher.
- `postinst.sh`, `prerm.sh`, `postrm.sh` — maintainer scripts for group/DB
  permissions, upgrade backup and data-lifecycle handling.
- `data-policy.md` — **authoritative** policy for what happens to the database
  on install, upgrade, remove and purge. See also ADR 0012.

## Intended layout

```text
/usr/lib/flow/daemon/Flow.Daemon
/usr/lib/flow/desktop/Flow.Desktop
/usr/lib/flow/loader/flow_tcp_bytes_loader
/usr/lib/systemd/system/flow-collector.service
/usr/lib/systemd/user/flow-daemon.service
/usr/share/dbus-1/services/org.flow.Daemon.service
/var/lib/flow/flow.db                 # runtime measurement data, not a package file
/var/lib/flow/flow.db.pre-upgrade     # upgrade safety copy
```

Systemd `ExecStart` uses command-line configuration keys with `:` (for example
`--Flow:Mode=collector`); environment variables use `__` instead. The collector
writes the shared DB as root. The desktop user reads it through the system
`flow` group. Any member of that group can read all usage history;
per-user isolation would require per-user DBs or a broker and is out of scope
for the first package.

## Polkit decision

No Polkit policy is shipped for the first package. The session daemon exposes
only read-only queries on the user's session bus; data is already readable from
`/var/lib/flow/flow.db` via the `flow` group, and the root collector has no
D-Bus interface. See ADR 0011. Add Polkit only if privileged control operations
or system-bus access are introduced later.

## Data lifecycle (summary)

- Upgrade: stop services, copy `flow.db` → `flow.db.pre-upgrade`, install new
  files, re-assert permissions. History and settings are preserved.
- `dpkg -r flow` (remove): data preserved.
- `dpkg -P flow` (purge): data preserved **by default**.
- Explicit full removal: `FLOW_PURGE_DATA=yes dpkg -P flow`, or set
  `PURGE_DATA=yes` in `/etc/flow/purge-data.conf` before purging.

The full policy, caveats and rationale are in `data-policy.md` and ADR 0012.

## Building the prototype binary package

```bash
./packaging/build-deb.sh
```

The script publishes the daemon and desktop projects, requires the already-built
`prototype/bpf/flow_tcp_bytes_loader`, validates the desktop file if available,
and writes `packaging/build/flow_0.1.0_amd64.deb`. Build output is gitignored.

## Validating the package

```bash
./packaging/build-deb.sh
./packaging/test-deb.sh
./packaging/test-install.sh
```

- `test-deb.sh` extracts the `.deb`, verifies layout/control and shipped docs,
  validates the desktop entry, syntax-checks maintainer scripts, and simulates
  install/upgrade/remove/purge with stubbed commands (no host services, groups
  or paths are modified). It asserts the no-delete default and both opt-in
  purge paths.
- `test-install.sh` performs a disposable Docker-based
  install → upgrade → remove → default purge → opt-in purge smoke test in
  `ubuntu:24.04`. It uses `dpkg --force-depends` because the container does not
  include the .NET runtime, removes the image's doc-exclusion dpkg config so
  packaged docs install, verifies group/DB permissions, the upgrade backup and
  that history survives every non-opt-in step.

## Validating the user session on a real VM

`validate-user-session.sh` and `docs/validation/user-session.md` cover what the
container tests cannot: a real systemd user session, collector/session role
separation, session-bus ownership, D-Bus activation and restart resilience.

```bash
FLOW_PREFLIGHT_ONLY=1 ./packaging/validate-user-session.sh   # no system changes
FLOW_READONLY=1 ./packaging/validate-user-session.sh         # installed package, no restarts
./packaging/validate-user-session.sh                          # needs sudo on a VM
./packaging/validate-source-session.sh                        # source binaries, private bus
```

Run as the logged-in user (never root). Read-only validation (16/16) and the
source-session validator (15/15) were actually executed here; the mutating full
run still requires a disposable VM with sudo and is an operator step. Neither
purges data nor proves eBPF byte accuracy.

`validate-dbus-activation.sh` is an opt-in, no-sudo check that briefly stops the
user daemon and verifies D-Bus activation brings it back (with a recovery trap).
`validate-source-session.sh` runs the same topology with source binaries on a
private bus, including activation.

`run-user-session-validation.sh` dispatches the kit to such a VM over SSH
(`FLOW_VM_SSH='user@vm'`, optional `FLOW_VM_PREFLIGHT_ONLY=1`) and copies the
report back. No VM was available where this was prepared, so the full run was
not executed.

## Manual validation before building a .deb

```bash
sudo install -d -o root -g flow -m 2770 /var/lib/flow
sudo cp packaging/flow-collector.service /usr/lib/systemd/system/
sudo cp packaging/flow-daemon.service /usr/lib/systemd/user/
sudo cp packaging/org.flow.Daemon.service /usr/share/dbus-1/services/
sudo systemctl daemon-reload
sudo systemctl enable --now flow-collector.service
systemctl --user enable --now flow-daemon.service
```

Stop with `sudo systemctl stop flow-collector.service` and
`systemctl --user stop flow-daemon.service`. The maintainer scripts preserve
`/var/lib/flow` on remove and default purge; see `data-policy.md` for explicit
full removal.
