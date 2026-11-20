#!/usr/bin/env bash
# Dispatch the installed-package validation kit to a disposable VM over SSH.
# The VM must be a real systemd host with a logged-in graphical user session.
# This script never modifies the local host and never runs as root.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deb="$repo_root/packaging/build/flow_0.1.0_amd64.deb"
validator="$repo_root/packaging/validate-user-session.sh"
target="${FLOW_VM_SSH:-}"
vm_dir="${FLOW_VM_DIR:-~/flow-validation}"
local_report="${FLOW_VALIDATE_REPORT:-$(mktemp -d "${TMPDIR:-/tmp}/flow-vm-validation-XXXXXX")}"
mkdir -p "$local_report"

die() { printf 'ERROR %s\n' "$1" >&2; exit 2; }
note() { printf '[flow-vm] %s\n' "$1"; }

[[ -n "$target" ]] || die "set FLOW_VM_SSH to a disposable VM, e.g. 'ubuntu@192.168.64.5'"
[[ -f "$deb" ]] || die "$deb not found; run ./packaging/build-deb.sh first"
[[ -f "$validator" ]] || die "$validator not found"
command -v ssh >/dev/null 2>&1 || die "ssh is required"
command -v scp >/dev/null 2>&1 || die "scp is required"

read -r -a ssh_opts <<< "${FLOW_VM_SSH_OPTS:-}"

note "target: $target"
if ! ssh "${ssh_opts[@]}" -o BatchMode=yes -o ConnectTimeout=10 "$target" true 2>"$local_report/ssh-preflight.err"; then
  die "SSH preflight failed (see $local_report/ssh-preflight.err)"
fi

if [[ "${FLOW_VM_PREFLIGHT_ONLY:-0}" == "1" ]]; then
  ssh "${ssh_opts[@]}" "$target" bash -s <<'REMOTE'
set -e
echo "PID1=$(ps -p 1 -o comm=)"
systemctl --user is-system-running || true
echo "DBUS=${DBUS_SESSION_BUS_ADDRESS:-unset}"
for tool in systemctl busctl journalctl dpkg sudo; do
  command -v "$tool" >/dev/null 2>&1 || { echo "missing tool: $tool" >&2; exit 1; }
done
sudo -n true && echo "passwordless sudo: yes" || { echo "passwordless sudo: NO" >&2; exit 1; }
REMOTE
  note "VM preflight finished; no files copied and no services changed"
  exit 0
fi

note "copying package and validation kit"
ssh "${ssh_opts[@]}" "$target" "mkdir -p '$vm_dir'"
scp "${ssh_opts[@]}" "$deb" "$target:$vm_dir/flow.deb"
scp "${ssh_opts[@]}" "$validator" "$target:$vm_dir/validate-user-session.sh"

note "running validation on the VM (output mirrored to $local_report/remote.log)"
set +e
ssh "${ssh_opts[@]}" "$target" \
  "chmod +x '$vm_dir/validate-user-session.sh' && FLOW_VALIDATE_REPORT='$vm_dir/report' bash '$vm_dir/validate-user-session.sh'" \
  2>&1 | tee "$local_report/remote.log"
remote_status="${PIPESTATUS[0]}"
set -e

note "copying report back"
scp -r "${ssh_opts[@]}" "$target:$vm_dir/report/." "$local_report/" >/dev/null 2>&1 || \
  note "could not copy the remote report directory"

if [[ "$remote_status" -eq 0 ]]; then
  note "VM validation PASSED; report: $local_report"
else
  note "VM validation FAILED (remote exit $remote_status); report: $local_report"
fi
exit "$remote_status"
