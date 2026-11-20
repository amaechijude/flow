#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deb="$repo_root/packaging/build/flow_0.1.0_amd64.deb"

if [[ ! -f "$deb" ]]; then
  echo "error: $deb not found; run ./packaging/build-deb.sh first" >&2
  exit 1
fi
if ! command -v docker >/dev/null 2>&1; then
  echo "error: docker is required for the disposable install smoke test" >&2
  exit 1
fi

image="ubuntu:24.04"
docker run --rm \
  -e SUDO_USER=flowtest \
  -v "$deb:/tmp/flow.deb:ro" \
  "$image" bash -euo pipefail -c '
    export DEBIAN_FRONTEND=noninteractive
    rm -f /etc/dpkg/dpkg.cfg.d/excludes
    useradd -m -s /bin/bash flowtest

    echo "[1/7] Installing package with dependency checks disabled"
    dpkg --force-depends -i /tmp/flow.deb

    echo "[2/7] Verifying installed layout"
    test -x /usr/lib/flow/daemon/Flow.Daemon
    test -x /usr/lib/flow/desktop/Flow.Desktop
    test -x /usr/lib/flow/loader/flow_tcp_bytes_loader
    test -f /usr/lib/systemd/system/flow-collector.service
    test -f /usr/lib/systemd/user/flow-daemon.service
    test -f /usr/share/dbus-1/services/org.flow.Daemon.service
    test -f /usr/share/applications/flow.desktop
    test -f /usr/share/doc/flow/README.md
    test -f /usr/share/doc/flow/data-policy.md

    echo "[3/7] Verifying group/DB permissions and packaged paths"
    getent group flow >/dev/null
    stat -c "%U:%G %a" /var/lib/flow | grep -q "root:flow 2770"
    stat -c "%U:%G %a" /var/lib/flow/flow.db | grep -q "root:flow 660"
    id -nG flowtest | tr " " "\n" | grep -qx flow
    grep -q "CAP_DAC_OVERRIDE" /usr/lib/systemd/system/flow-collector.service
    grep -q "UMask=0007" /usr/lib/systemd/user/flow-daemon.service
    grep -q "Flow__DatabasePath=/var/lib/flow/flow.db" /usr/share/applications/flow.desktop

    echo "[4/7] Simulating an upgrade: backup created, history preserved"
    echo "history-and-settings" > /var/lib/flow/flow.db
    sh /var/lib/dpkg/info/flow.prerm upgrade
    test -f /var/lib/flow/flow.db.pre-upgrade
    grep -q history-and-settings /var/lib/flow/flow.db.pre-upgrade
    dpkg --force-depends -i /tmp/flow.deb
    grep -q history-and-settings /var/lib/flow/flow.db
    stat -c "%U:%G %a" /var/lib/flow | grep -q "root:flow 2770"

    echo "[5/7] Removing package (data must survive)"
    dpkg -r flow >/dev/null
    test ! -e /usr/lib/flow/daemon/Flow.Daemon
    test ! -e /usr/share/applications/flow.desktop
    test -f /var/lib/flow/flow.db
    grep -q history-and-settings /var/lib/flow/flow.db

    echo "[6/7] Purging package by default (data must still survive)"
    dpkg -P flow >/dev/null
    test -f /var/lib/flow/flow.db
    grep -q history-and-settings /var/lib/flow/flow.db

    echo "[7/7] Opt-in purge removes data explicitly"
    dpkg --force-depends -i /tmp/flow.deb
    FLOW_PURGE_DATA=yes dpkg -P flow >/dev/null
    test ! -e /var/lib/flow

    echo "disposable install/upgrade/remove/purge smoke test passed"
  '
