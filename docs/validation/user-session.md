# Real-VM validation: systemd collector + user-session IPC

This kit validates the *installed* topology, not just the source tree:
a privileged root **collector** writes SQLite, and an unprivileged
`systemd --user` **session daemon** exposes read-only D-Bus IPC from the same
database. It cannot prove byte-accuracy of the eBPF provider; that remains a
separate privileged validation (see `docs/measurements/`).

## Why this needs a real VM/host

The check requires PID 1 to be systemd, a graphical/logged-in user session with
a session bus, and sudo for installing the package and restarting the system
unit. It is deliberately **not** run as root, because the desktop must not run
as root and because bus ownership is checked on the *user's* session bus.

The script `packaging/validate-user-session.sh` was prepared here, where PID 1
and `systemd --user` are real but there is no passwordless sudo and no installed
package. Running it end-to-end is an operator step on a disposable VM.

## Prerequisites

1. A disposable Ubuntu 24.04+ VM with a graphical login (GNOME is fine).
2. A built prototype package: `./packaging/build-deb.sh` on the dev machine,
   then copy `packaging/build/flow_0.1.0_amd64.deb` to the VM.
3. The validating user has passwordless `sudo`, or the package is already
   installed and `FLOW_SKIP_INSTALL=1` is used.
4. Build tools are not needed on the VM. `busctl`, `systemctl`, `journalctl`,
   `dpkg`, `stat` and `timeout` are (all present in Ubuntu).
5. Close any running `Flow.Desktop` window before the "session bus is not
   claimed" check — an open client may trigger D-Bus activation.

## Run

```bash
# Fast preflight: no system changes, checks session/systemd/tools/group.
FLOW_PREFLIGHT_ONLY=1 ./packaging/validate-user-session.sh

# Full validation (installs the package and restarts units).
FLOW_VALIDATE_REPORT=/tmp/flow-user-session-validation \
  ./packaging/validate-user-session.sh
```

If the user was just added to the `flow` group by `postinst`, **log out and back
in** before the full run; supplementary groups are not refreshed in an existing
session. `FLOW_DPKG_FLAGS` can override the default `--force-depends` (used
because the VM may not have the .NET runtime installed), and
`FLOW_SKIP_INSTALL=1` validates an already installed package.

### Dispatched runner (disposable VM over SSH)

`packaging/run-user-session-validation.sh` copies the built `.deb` and the
validation kit to a disposable VM and brings the report back locally. It never
modifies the local host and never runs as root.

```bash
FLOW_VM_SSH='ubuntu@192.168.64.5' ./packaging/run-user-session-validation.sh
FLOW_VM_SSH='ubuntu@192.168.64.5' FLOW_VM_PREFLIGHT_ONLY=1 \
  ./packaging/run-user-session-validation.sh
```

The VM must have a graphical login, passwordless sudo, and `ssh`/`scp` access.
`FLOW_VM_SSH_OPTS` can supply extra SSH flags and `FLOW_VM_DIR` the remote
staging directory. The local report defaults to a fresh `/tmp` directory and can
be pinned with `FLOW_VALIDATE_REPORT`. This runner was prepared and syntax/
preflight-checked here, but no VM was available in this session, so the full run
remains an operator step.

The script prints `PASS`/`FAIL` per check, exits nonzero on any failure, and
writes raw output plus `journalctl` excerpts into the report directory.

## Manual checklist (same steps, explicit commands)

| # | Check | Command | Expected |
| --- | --- | --- | --- |
| 1 | Preconditions | `systemctl --user is-system-running`; `echo $DBUS_SESSION_BUS_ADDRESS` | `running` (or `degraded`); a non-empty bus address |
| 2 | Group membership | `id -nG "$USER" \| tr ' ' '\n' \| grep -qx flow` | exits 0 after re-login |
| 3 | Install | `sudo dpkg -i flow_0.1.0_amd64.deb` | exits 0 (use `--force-depends` if runtime deps are absent) |
| 4 | Collector active | `sudo systemctl is-active flow-collector.service` | `active` |
| 5 | Collector role | `sudo systemctl show -p ExecStart --value flow-collector.service` | contains `--Flow:Mode=collector` and `--Flow:Monitor=ebpf` |
| 6 | No leaked bus name | stop `flow-daemon.service`, then `busctl --user list \| grep org.flow.Daemon` | no match |
| 7 | Session daemon active | `systemctl --user is-active flow-daemon.service` | `active` |
| 8 | Session role | `systemctl --user show -p ExecStart --value flow-daemon.service` | contains `--Flow:Mode=session`, **no** `ebpf` |
| 9 | Introspection | `busctl --user introspect org.flow.Daemon /org/flow/Daemon org.flow.Daemon1` | lists `GetStatusJson`, `GetUsageSummaryJson`, `StatusChanged` |
| 10 | Contract | `busctl --user call org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetContractVersion` | returns `3` |
| 11 | Real status | `busctl --user call ... GetStatusJson` | non-empty JSON with `state`, `providerName`, `pendingDurableDeltas` |
| 12 | Real queries | `busctl --user call ... GetUsageSummaryJson sss <start> <end> ""` | JSON with `rxBytes` / `txBytes` |
| 13 | Signal | `timeout 12 busctl --user monitor org.flow.Daemon` | `StatusChanged` observed (best effort) |
| 14 | On-demand activation | stop unit, then call `GetContractVersion` | call succeeds and unit becomes active |
| 15 | Restart resilience | restart both units, re-run 9–12 | all still pass; `/var/lib/flow/flow.db` present |
| 16 | Data path | `stat -c '%U:%G %a' /var/lib/flow` | `root:flow 2770` |

All query values must come from the real database. If a call returns a plausible
number with no persisted buckets behind it, that is a failure, not a pass.

## Interpreting failures

| Symptom | Likely cause | Action |
| --- | --- | --- |
| Group check fails | `postinst` added the group after login | Re-login; re-run |
| Collector not active | missing CAP_BPF/CAP_PERFMON, eBPF loader missing, or no network target | Check `journalctl -u flow-collector.service`; keep the documented attribution caveat |
| `org.flow.Daemon` owned with user daemon stopped | stale session process or an open desktop client | Close Flow.Desktop, `systemctl --user stop flow-daemon.service`, re-check |
| Activation call fails | D-Bus service file not installed/refresh needed, or systemd activation not wired | `sudo systemctl daemon-reload`; check `/usr/share/dbus-1/services/org.flow.Daemon.service` and `journalctl --user -u flow-daemon.service` |
| `GetStatusJson` returns unavailable | session daemon cannot read the DB | Check group membership and `stat` on `/var/lib/flow` |
| No `StatusChanged` in 12 s | no data/health change during the window | Non-fatal; confirm with a longer `busctl monitor` while generating traffic |

## Cleanup

```bash
systemctl --user stop flow-daemon.service
systemctl --user disable flow-daemon.service
sudo systemctl stop flow-collector.service
sudo systemctl disable flow-collector.service
sudo dpkg -r flow          # data under /var/lib/flow is preserved
```

Do **not** purge in this kit: lifecycle (including deliberate full removal) is
covered by `packaging/test-install.sh` and `packaging/data-policy.md`.

## Limitations / honest scope

- This validates lifecycle, identity, bus ownership and real query plumbing.
  It does not prove attribution correctness, high-churn behaviour or GUI
  accessibility.
- The eBPF byte-accuracy evidence remains in `docs/measurements/`.
- With `--force-depends`, dependency resolution itself is not validated.
- Status-signal observation is timing-dependent and reported as advisory.
