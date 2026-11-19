# Packaging scaffolding (draft)

These files draft the systemd/D-Bus topology from `docs/packaging-plan.md`. They
are not yet wired into a `.deb`; `dpkg-deb` metadata and maintainer scripts still
need to be added.

## Files

- `flow-collector.service` — system unit, root, `Flow:Mode=collector`, starts the
  privileged eBPF loader, no D-Bus registration.
- `flow-daemon.service` — user unit, `Flow:Mode=session`, D-Bus IPC only.
- `org.flow.Daemon.service` — D-Bus session activation for the user daemon.
- `postinst.sh` / `prerm.sh` — draft maintainer helpers for group/DB permissions
  and service lifecycle.

## Intended layout

```text
/usr/lib/flow/daemon/Flow.Daemon
/usr/lib/flow/desktop/Flow.Desktop
/usr/lib/flow/loader/flow_tcp_bytes_loader
/usr/lib/systemd/system/flow-collector.service
/usr/lib/systemd/user/flow-daemon.service
/usr/share/dbus-1/services/org.flow.Daemon.service
/var/lib/flow/flow.db
```

Systemd `ExecStart` uses command-line configuration keys with `:` (for example
`--Flow:Mode=collector`); environment variables use `__` instead. The collector
writes the shared DB as root. The desktop user reads it through
the system `flow` group. Any member of that group can read all usage history;
per-user isolation would require per-user DBs or a broker and is out of scope
for the first package.

## Building the prototype binary package

```bash
./packaging/build-deb.sh
```

The script publishes the daemon and desktop projects, requires the already-built
`prototype/bpf/flow_tcp_bytes_loader`, validates the desktop file if available,
and writes `packaging/build/flow_0.1.0_amd64.deb`. Build output is gitignored.

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
`/var/lib/flow/flow.db` on removal.
