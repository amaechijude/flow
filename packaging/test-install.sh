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

    echo "[1/5] Installing package with dependency checks disabled"
    dpkg --force-depends -i /tmp/flow.deb

    echo "[2/5] Verifying installed layout"
    test -x /usr/lib/flow/daemon/Flow.Daemon
    test -x /usr/lib/flow/desktop/Flow.Desktop
    test -x /usr/lib/flow/loader/flow_tcp_bytes_loader
    test -f /usr/lib/systemd/system/flow-collector.service
    test -f /usr/lib/systemd/user/flow-daemon.service
    test -f /usr/share/dbus-1/services/org.flow.Daemon.service
    test -f /usr/share/applications/flow.desktop
    test -f /usr/share/doc/flow/README.md

    echo "[3/5] Verifying group/DB permissions from postinst"
    getent group flow >/dev/null
    stat -c "%U:%G %a" /var/lib/flow | grep -q "root:flow 2770"
    id -nG flowtest | tr " " "\n" | grep -qx flow

    echo "[4/5] Removing package (data must survive)"
    echo "persisted" > /var/lib/flow/flow.db
    dpkg -r flow >/dev/null
    test ! -e /usr/lib/flow/daemon/Flow.Daemon
    test ! -e /usr/share/applications/flow.desktop
    test -f /var/lib/flow/flow.db
    grep -q persisted /var/lib/flow/flow.db

    echo "[5/5] Purging package (data must still survive)"
    dpkg -P flow >/dev/null
    test -f /var/lib/flow/flow.db

    echo "disposable install/uninstall smoke test passed"
  '
