#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deb="$repo_root/packaging/build/flow_0.1.0_amd64.deb"

if [[ ! -f "$deb" ]]; then
  echo "error: $deb not found; run ./packaging/build-deb.sh first" >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
root="$tmp/root"
mkdir -p "$root"

echo "[1/5] Extracting package layout"
dpkg-deb -x "$deb" "$root"

required=(
  "usr/lib/flow/daemon/Flow.Daemon"
  "usr/lib/flow/desktop/Flow.Desktop"
  "usr/lib/flow/loader/flow_tcp_bytes_loader"
  "usr/lib/systemd/system/flow-collector.service"
  "usr/lib/systemd/user/flow-daemon.service"
  "usr/share/dbus-1/services/org.flow.Daemon.service"
  "usr/share/applications/flow.desktop"
  "usr/share/doc/flow/README.md"
  "usr/share/doc/flow/data-policy.md"
)
for rel in "${required[@]}"; do
  [[ -f "$root/$rel" ]] || { echo "missing packaged file: $rel" >&2; exit 1; }
done
[[ -x "$root/usr/lib/flow/daemon/Flow.Daemon" ]] || { echo "daemon not executable" >&2; exit 1; }
[[ -x "$root/usr/lib/flow/desktop/Flow.Desktop" ]] || { echo "desktop not executable" >&2; exit 1; }
[[ -x "$root/usr/lib/flow/loader/flow_tcp_bytes_loader" ]] || { echo "loader not executable" >&2; exit 1; }

echo "[2/5] Checking package control"
[[ "$(dpkg-deb -f "$deb" Package)" == "flow" ]]
[[ "$(dpkg-deb -f "$deb" Version)" == "0.1.0" ]]
grep -q "Flow:Mode=collector" "$root/usr/lib/systemd/system/flow-collector.service"
grep -q "Flow:Mode=session" "$root/usr/lib/systemd/user/flow-daemon.service"
grep -q "SystemdService=flow-daemon.service" "$root/usr/share/dbus-1/services/org.flow.Daemon.service"
grep -q "CAP_DAC_OVERRIDE" "$root/usr/lib/systemd/system/flow-collector.service"
grep -q "UMask=0007" "$root/usr/lib/systemd/user/flow-daemon.service"
grep -q "Flow__DatabasePath=/var/lib/flow/flow.db" "$root/usr/share/applications/flow.desktop"
if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$root/usr/share/applications/flow.desktop"
fi

echo "[3/5] Syntax-checking maintainer scripts"
sh -n "$repo_root/packaging/postinst.sh"
sh -n "$repo_root/packaging/prerm.sh"
sh -n "$repo_root/packaging/postrm.sh"
if command -v shellcheck >/dev/null 2>&1; then
  shellcheck -s sh "$repo_root/packaging/postinst.sh" "$repo_root/packaging/prerm.sh" "$repo_root/packaging/postrm.sh"
fi

echo "[4/5] Simulating maintainer scripts with stubbed commands"
real_user="$(id -un)"
stubbin="$tmp/bin"
mkdir -p "$stubbin"
log="$tmp/maintainer.log"
: > "$log"
cat > "$stubbin/getent" <<EOF
#!/bin/sh
echo "getent \$*" >> "$log"
exit 1
EOF
chmod +x "$stubbin/getent"
for cmd in groupadd install usermod systemctl runuser id cp chgrp chown chmod rm; do
  cat > "$stubbin/$cmd" <<EOF
#!/bin/sh
echo "$cmd \$*" >> "$log"
exit 0
EOF
  chmod +x "$stubbin/$cmd"
done

PATH="$stubbin:$PATH" SUDO_USER="$real_user" sh "$repo_root/packaging/postinst.sh" configure >/dev/null
PATH="$stubbin:$PATH" SUDO_USER="$real_user" sh "$repo_root/packaging/prerm.sh" >/dev/null

grep -q "groupadd --system flow" "$log" || { echo "postinst did not create flow group" >&2; exit 1; }
grep -q "systemctl enable flow-collector.service" "$log" || { echo "postinst did not enable collector" >&2; exit 1; }
grep -q "chown root:flow .*flow.db" "$log" || { echo "postinst did not fix database ownership" >&2; exit 1; }
grep -q "chmod 0660 .*flow.db" "$log" || { echo "postinst did not fix database permissions" >&2; exit 1; }
grep -q "systemctl stop flow-collector.service" "$log" || { echo "prerm did not stop collector" >&2; exit 1; }
grep -Eq "runuser -u .* systemctl --user stop flow-daemon.service" "$log" || { cat "$log" >&2; echo "prerm did not stop user daemon" >&2; exit 1; }

echo "[5/5] Simulating upgrade / remove / purge data policy"
data="$tmp/data"
mkdir -p "$data"
echo "history" > "$data/flow.db"

: > "$log"
PATH="$stubbin:$PATH" SUDO_USER="$real_user" FLOW_DATA_DIR="$data" sh "$repo_root/packaging/prerm.sh" upgrade >/dev/null
grep -q "cp -p $data/flow.db $data/flow.db.pre-upgrade" "$log" || { cat "$log" >&2; echo "prerm upgrade did not back up the database" >&2; exit 1; }

: > "$log"
PATH="$stubbin:$PATH" SUDO_USER="$real_user" FLOW_DATA_DIR="$data" sh "$repo_root/packaging/postinst.sh" configure 0.0.9 >>"$log" 2>&1
grep -q "Upgraded from 0.0.9" "$log" || { cat "$log" >&2; echo "postinst did not report the upgrade" >&2; exit 1; }

: > "$log"
PATH="$stubbin:$PATH" FLOW_DATA_DIR="$data" sh "$repo_root/packaging/postrm.sh" remove >/dev/null
if grep -q "rm -rf $data" "$log"; then echo "remove must not delete data" >&2; exit 1; fi

: > "$log"
PATH="$stubbin:$PATH" FLOW_DATA_DIR="$data" sh "$repo_root/packaging/postrm.sh" purge >/dev/null
if grep -q "rm -rf $data" "$log"; then echo "default purge must not delete data" >&2; exit 1; fi

: > "$log"
PATH="$stubbin:$PATH" FLOW_DATA_DIR="$data" FLOW_PURGE_DATA=yes sh "$repo_root/packaging/postrm.sh" purge >/dev/null
grep -q "rm -rf $data" "$log" || { cat "$log" >&2; echo "env opt-in purge did not delete data" >&2; exit 1; }

echo "PURGE_DATA=yes" > "$tmp/purge-data.conf"
: > "$log"
PATH="$stubbin:$PATH" FLOW_DATA_DIR="$data" FLOW_PURGE_CONFIG="$tmp/purge-data.conf" sh "$repo_root/packaging/postrm.sh" purge >/dev/null
grep -q "rm -rf $data" "$log" || { cat "$log" >&2; echo "config opt-in purge did not delete data" >&2; exit 1; }

echo "package layout, maintainer scripts, and data-policy simulation passed"
