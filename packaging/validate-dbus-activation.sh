#!/usr/bin/env bash
# Opt-in: briefly stop the installed user daemon and verify D-Bus activation
# brings it back. A trap restarts it manually if activation fails.
# Run as the logged-in user (never root). No sudo required.
set -uo pipefail

report="${FLOW_VALIDATE_REPORT:-$(mktemp -d "${TMPDIR:-/tmp}/flow-activation-XXXXXX")}"
mkdir -p "$report"
results="$report/results.txt"
: > "$results"
service="org.flow.Daemon"
object="/org/flow/Daemon"
iface="org.flow.Daemon1"
unit="flow-daemon.service"
CHECKS=0
FAILS=0

pass() { CHECKS=$((CHECKS + 1)); printf 'PASS  %s\n' "$1" | tee -a "$results"; }
fail() { CHECKS=$((CHECKS + 1)); FAILS=$((FAILS + 1)); printf 'FAIL  %s\n' "$1" | tee -a "$results"; }
note() { printf 'NOTE  %s\n' "$1" | tee -a "$results"; }
die() { printf 'ERROR %s\n' "$1" | tee -a "$results" >&2; exit 2; }

owner_pid() {
  busctl --user list --no-pager 2>/dev/null |
    awk -v n="$service" '$1 == n && $2 != "-" { print $2; exit }'
}

recover() {
  if [[ "$(systemctl --user is-active "$unit" 2>/dev/null)" != "active" ]]; then
    note "recovery: manually starting $unit"
    systemctl --user start "$unit" >/dev/null 2>&1 || true
  fi
}
trap recover EXIT

[[ "$(id -u)" -ne 0 ]] || die "run as the logged-in user, not root"
[[ -n "${DBUS_SESSION_BUS_ADDRESS:-}" ]] || die "no session bus"
command -v busctl >/dev/null 2>&1 || die "busctl missing"
systemctl --user is-system-running >/dev/null 2>&1 || die "systemd --user is not running"

old_pid="$(owner_pid)"
[[ -n "$old_pid" ]] || die "$service has no owner; start $unit first"
note "current owner pid: $old_pid"

printf '%s\n' "Stopping $unit briefly (recovery trap armed)..."
systemctl --user stop "$unit" >/dev/null 2>&1 || die "could not stop $unit"

stopped=0
for _ in $(seq 1 30); do
  if [[ -z "$(owner_pid)" && "$(systemctl --user is-active "$unit" 2>/dev/null)" != "active" ]]; then
    stopped=1
    break
  fi
  sleep 0.2
done
if [[ "$stopped" == "1" ]]; then pass "unit stopped and bus name released"; else fail "unit or bus name still present after stop"; fi

note "calling GetContractVersion to trigger activation"
activation="$(busctl --user call --timeout=12 "$service" "$object" "$iface" GetContractVersion 2>&1)"
printf '%s\n' "$activation" > "$report/activation.out"
# systemd activation is asynchronous: the call that triggers it can arrive
# before the daemon has registered its objects. The desktop client retries;
# the validator checks a retry after the unit is active.
if grep -qw '3' <<<"$activation"; then
  pass "activating call returned contract v3"
else
  note "activating call raced daemon startup: $(head -1 <<<"$activation")"
fi

active=0
new_pid=""
for _ in $(seq 1 40); do
  if [[ "$(systemctl --user is-active "$unit" 2>/dev/null)" == "active" ]]; then
    new_pid="$(owner_pid)"
    if [[ -n "$new_pid" ]]; then active=1; break; fi
  fi
  sleep 0.25
done
if [[ "$active" == "1" ]]; then pass "systemd re-activated $unit (new owner pid $new_pid)"; else fail "$unit did not return after activation"; fi
if [[ -n "$new_pid" && "$new_pid" != "$old_pid" ]]; then pass "activation created a new daemon process"; else fail "owner pid did not change (old=$old_pid new=$new_pid)"; fi

retry="$(busctl --user call --timeout=10 "$service" "$object" "$iface" GetContractVersion 2>&1)"
if grep -qw '3' <<<"$retry"; then pass "post-activation call returned contract v3"; else fail "post-activation call failed: $retry"; fi

summary="$(busctl --user call --timeout=10 "$service" "$object" "$iface" GetUsageSummaryJson sss \
  "$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" '' 2>&1)"
if grep -q 'rxBytes' <<<"$summary"; then pass "post-activation query works"; else fail "post-activation query failed: $summary"; fi

journalctl --user -u "$unit" -n 80 --no-pager > "$report/journal.log" 2>&1 || true
printf '\nChecks: %d, failures: %d\nReport: %s\n' "$CHECKS" "$FAILS" "$report"
[[ "$FAILS" -eq 0 ]] || exit 1
exit 0
