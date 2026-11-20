#!/usr/bin/env bash
# Real-VM validation of the installed systemd/D-Bus user-session topology.
# Run as the logged-in desktop user, NOT as root. System changes require sudo.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deb="$repo_root/packaging/build/flow_0.1.0_amd64.deb"
report="${FLOW_VALIDATE_REPORT:-$(mktemp -d "${TMPDIR:-/tmp}/flow-user-session-XXXXXX")}"
service="org.flow.Daemon"
object="/org/flow/Daemon"
iface="org.flow.Daemon1"
mkdir -p "$report"
results="$report/results.txt"
: > "$results"

CHECKS=0
FAILS=0

section() { printf '\n== %s ==\n' "$1" | tee -a "$results"; }
pass() { CHECKS=$((CHECKS + 1)); printf 'PASS  %s\n' "$1" | tee -a "$results"; }
fail() { CHECKS=$((CHECKS + 1)); FAILS=$((FAILS + 1)); printf 'FAIL  %s\n' "$1" | tee -a "$results"; }
note() { printf 'NOTE  %s\n' "$1" | tee -a "$results"; }
die() { printf 'ERROR %s\n' "$1" | tee -a "$results" >&2; exit 2; }

check_cmd() {
  local desc="$1"
  shift
  if "$@" >"$report/last.out" 2>&1; then
    pass "$desc"
  else
    fail "$desc — output in $report/last.out"
  fi
}

contains() { grep -q -- "$1"; }

section "Preflight"
if [[ "$(id -u)" -eq 0 ]]; then
  die "run as the logged-in desktop user, not root (the desktop must not run as root)"
fi
for tool in systemctl busctl journalctl dpkg timeout sudo stat; do
  command -v "$tool" >/dev/null 2>&1 || die "required tool missing: $tool"
done
[[ -f "$deb" ]] || die "$deb not found; run ./packaging/build-deb.sh first"
[[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]] || die "no session bus (DBUS_SESSION_BUS_ADDRESS is unset)"
pid1="$(ps -p 1 -o comm=)"
[[ "$pid1" == "systemd" ]] || die "PID 1 is '$pid1'; a real systemd host/VM is required"
user_state="$(systemctl --user is-system-running 2>/dev/null || true)"
[[ "$user_state" == "running" || "$user_state" == "degraded" ]] || die "systemd --user is not running (state: '$user_state')"
pass "preflight: user session, session bus, tools and package present"

current_user="$(id -un)"
if id -nG "$current_user" | tr ' ' '\n' | grep -qx flow; then
  pass "user '$current_user' is in the flow group"
else
  fail "user '$current_user' is not in the flow group — log out and back in after installing the package"
fi

if [[ "${FLOW_PREFLIGHT_ONLY:-0}" == "1" ]]; then
  note "preflight-only mode; no services or packages were modified"
  printf 'Checks: %d, failures: %d\nReport: %s\n' "$CHECKS" "$FAILS" "$report"
  [[ "$FAILS" -eq 0 ]] || exit 1
  exit 0
fi

if [[ "${FLOW_SKIP_INSTALL:-0}" != "1" ]]; then
  sudo -n true 2>/dev/null || die "passwordless sudo required for install (or FLOW_SKIP_INSTALL=1 with the package already installed)"
  section "Install prototype package"
  read -r -a dpkg_flags <<< "${FLOW_DPKG_FLAGS:---force-depends}"
  if sudo dpkg "${dpkg_flags[@]}" -i "$deb" >"$report/install.out" 2>&1; then
    pass "package installed"
  else
    fail "package install failed — output in $report/install.out"
  fi
fi

section "Collector (system unit)"
sudo systemctl restart flow-collector.service >/dev/null 2>&1 || true
check_cmd "flow-collector.service is active" sudo systemctl is-active flow-collector.service
collector_exec="$(sudo systemctl show -p ExecStart --value flow-collector.service 2>/dev/null || true)"
if contains '--Flow:Mode=collector' <<<"$collector_exec"; then pass "collector runs in collector mode"; else fail "collector ExecStart lacks --Flow:Mode=collector: $collector_exec"; fi
if contains '--Flow:Monitor=ebpf' <<<"$collector_exec"; then pass "collector uses the ebpf monitor"; else fail "collector ExecStart lacks --Flow:Monitor=ebpf: $collector_exec"; fi

section "Session bus is not claimed by the collector"
systemctl --user stop flow-daemon.service >/dev/null 2>&1 || true
sleep 1
# Close Flow.Desktop during this check; an open client can trigger D-Bus activation.
if busctl --user list --no-pager 2>/dev/null | grep -q "$service"; then
  fail "$service is still on the session bus with the user daemon stopped"
else
  pass "no $service owner while the user daemon is stopped"
fi

section "User daemon (systemd --user)"
systemctl --user enable --now flow-daemon.service >"$report/enable.out" 2>&1 || true
check_cmd "flow-daemon.service is active" systemctl --user is-active flow-daemon.service
session_exec="$(systemctl --user show -p ExecStart --value flow-daemon.service 2>/dev/null || true)"
if contains '--Flow:Mode=session' <<<"$session_exec"; then pass "session daemon runs in session mode"; else fail "session ExecStart lacks --Flow:Mode=session: $session_exec"; fi
if contains 'ebpf' <<<"$session_exec"; then fail "session daemon must not run the ebpf monitor: $session_exec"; else pass "session daemon has no privileged monitor"; fi

section "D-Bus IPC contract v3"
introspection="$(busctl --user introspect "$service" "$object" "$iface" 2>&1)"
printf '%s\n' "$introspection" > "$report/introspect.txt"
if contains 'GetStatusJson' <<<"$introspection"; then pass "interface $iface is introspectable"; else fail "$iface introspection missing GetStatusJson"; fi
version="$(busctl --user call --timeout=10 "$service" "$object" "$iface" GetContractVersion 2>&1)"
if grep -qw '3' <<<"$version"; then pass "GetContractVersion returned 3"; else fail "unexpected contract version: $version"; fi
status="$(busctl --user call --timeout=10 "$service" "$object" "$iface" GetStatusJson 2>&1)"
if contains 'state' <<<"$status"; then pass "GetStatusJson returned real status"; else fail "GetStatusJson returned no status fields: $status"; fi

start_iso="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"
end_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
summary="$(busctl --user call --timeout=10 "$service" "$object" "$iface" GetUsageSummaryJson sss "$start_iso" "$end_iso" '' 2>&1)"
if contains 'rxBytes' <<<"$summary"; then pass "GetUsageSummaryJson returned persisted data"; else fail "usage summary call failed: $summary"; fi

section "Coalesced status signal (best effort)"
if timeout 12 busctl --user monitor "$service" 2>&1 | grep -q 'StatusChanged'; then
  pass "observed StatusChanged signal"
else
  note "no StatusChanged within 12s (non-fatal: depends on live data changes)"
fi

section "D-Bus on-demand activation"
systemctl --user stop flow-daemon.service >/dev/null 2>&1 || true
sleep 1
activation="$(busctl --user call --timeout=12 "$service" "$object" "$iface" GetContractVersion 2>&1)"
if grep -qw '3' <<<"$activation"; then pass "activation call reached the daemon (v3)"; else fail "activation call failed: $activation"; fi
sleep 1
check_cmd "systemd started flow-daemon.service via D-Bus activation" systemctl --user is-active flow-daemon.service

section "Restart resilience and data path"
sudo systemctl restart flow-collector.service >/dev/null 2>&1 || true
systemctl --user restart flow-daemon.service >/dev/null 2>&1 || true
sleep 2
check_cmd "collector active after restart" sudo systemctl is-active flow-collector.service
check_cmd "session daemon active after restart" systemctl --user is-active flow-daemon.service
if [[ -f /var/lib/flow/flow.db ]]; then pass "database still present at /var/lib/flow/flow.db"; else fail "database missing after restarts"; fi
if [[ "$(stat -c '%U:%G %a' /var/lib/flow 2>/dev/null)" == "root:flow 2770" ]]; then pass "data directory permissions are root:flow 2770"; else fail "unexpected /var/lib/flow permissions: $(stat -c '%U:%G %a' /var/lib/flow 2>/dev/null)"; fi

section "Logs"
journalctl -u flow-collector.service -n 200 --no-pager >"$report/collector.log" 2>&1 || true
journalctl --user -u flow-daemon.service -n 200 --no-pager >"$report/session.log" 2>&1 || true
busctl --user list --no-pager >"$report/bus-names.txt" 2>&1 || true
note "logs and raw command output saved in $report"

printf '\nChecks: %d, failures: %d\nReport: %s\n' "$CHECKS" "$FAILS" "$report"
if [[ "$FAILS" -gt 0 ]]; then exit 1; fi
exit 0
