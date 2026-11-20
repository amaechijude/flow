#!/usr/bin/env bash
# Validate the collector/session topology with source-built binaries in a real
# D-Bus session, WITHOUT installing a package or touching the user's bus.
# Uses a private dbus-daemon so an already-running Flow daemon is undisturbed.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
daemon="$repo_root/src/Flow.Daemon/bin/Debug/net10.0/Flow.Daemon"
report="${FLOW_VALIDATE_REPORT:-$(mktemp -d "${TMPDIR:-/tmp}/flow-source-session-XXXXXX")}"
runtime="$(mktemp -d "${TMPDIR:-/tmp}/flow-source-session-run-XXXXXX")"
mkdir -p "$report"
CHECKS=0
FAILS=0
pids=()

pass() { CHECKS=$((CHECKS + 1)); printf 'PASS  %s\n' "$1" | tee -a "$report/results.txt"; }
fail() { CHECKS=$((CHECKS + 1)); FAILS=$((FAILS + 1)); printf 'FAIL  %s\n' "$1" | tee -a "$report/results.txt"; }
die() { printf 'ERROR %s\n' "$1" >&2; exit 2; }

cleanup() {
  for pid in "${pids[@]}"; do kill "$pid" >/dev/null 2>&1 || true; done
  wait >/dev/null 2>&1 || true
  cp "$runtime"/*.log "$report/" 2>/dev/null || true
  cp "$runtime/bus.info" "$report/" 2>/dev/null || true
  rm -rf "$runtime"
}
trap cleanup EXIT

for tool in dbus-daemon busctl python3; do
  command -v "$tool" >/dev/null 2>&1 || die "required tool missing: $tool"
done
if [[ ! -x "$daemon" ]]; then
  dotnet build "$repo_root/src/Flow.Daemon/Flow.Daemon.csproj" -v minimal >/dev/null || die "daemon build failed"
fi
: > "$report/results.txt"

db="$runtime/flow.db"
printf 'Starting private session bus (only a private Flow activation file)\n'
mkdir -p "$runtime/empty/dbus-1/services"
cat > "$runtime/empty/dbus-1/services/org.flow.Daemon.service" <<EOF
[D-BUS Service]
Name=org.flow.Daemon
Exec=$daemon --Flow:Mode=session --Flow:DatabasePath=$db
EOF
XDG_DATA_HOME="$runtime/empty" XDG_DATA_DIRS="$runtime/empty" \
  dbus-daemon --session --fork --print-address=1 --print-pid=1 >"$runtime/bus.info" 2>"$runtime/bus.err" || die "dbus-daemon failed"
bus_addr="$(sed -n '1p' "$runtime/bus.info")"
bus_pid="$(sed -n '2p' "$runtime/bus.info")"
[[ -n "$bus_addr" && -n "$bus_pid" ]] || die "could not read private bus address/pid: $(cat "$runtime/bus.info")"
pids+=("$bus_pid")
bctl() { busctl --address="$bus_addr" "$@"; }

# busctl list includes *activatable* names with a "-" PID, so checking for the
# name alone is not ownership. Only a row with a real PID is an owner.
name_owner_pid() {
  bctl list --no-pager 2>/dev/null | awk '$1 == "org.flow.Daemon" && $2 != "-" { print $2; exit }'
}

start_daemon() {
  local mode="$1" monitor="${2:-}"
  if [[ -n "$monitor" ]]; then
    DBUS_SESSION_BUS_ADDRESS="$bus_addr" "$daemon" --Flow:Mode="$mode" --Flow:Monitor="$monitor" \
      --Flow:DatabasePath="$db" >"$runtime/$mode.log" 2>&1 &
  else
    DBUS_SESSION_BUS_ADDRESS="$bus_addr" "$daemon" --Flow:Mode="$mode" \
      --Flow:DatabasePath="$db" >"$runtime/$mode.log" 2>&1 &
  fi
  started_pid=$!
  pids+=("$started_pid")
  # Do not return via command substitution: that runs the function in a subshell
  # and the cleanup trap would never see the PID.
}

wait_for_name() {
  local want="$1" timeout="$2"
  local start=$SECONDS
  while (( SECONDS - start < timeout )); do
    if [[ -n "$(name_owner_pid)" ]]; then
      [[ "$want" == "present" ]] && return 0
    else
      [[ "$want" == "absent" ]] && return 0
    fi
    sleep 0.2
  done
  return 1
}

count_buckets() {
  python3 - "$db" <<'PY'
import sqlite3, sys
try:
    conn = sqlite3.connect(sys.argv[1])
    print(conn.execute("select count(*) from usage_buckets").fetchone()[0])
except Exception:
    print(0)
PY
}

printf '\n== Session daemon owns the bus ==\n'
start_daemon session
session_pid="$started_pid"
if wait_for_name present 6; then pass "session daemon registered org.flow.Daemon"; else fail "session daemon did not register the bus name"; fi
owner_pid="$(name_owner_pid)"
if [[ "$owner_pid" == "$session_pid" ]]; then pass "bus owner is the source-built daemon (pid $owner_pid)"; else fail "unexpected bus owner pid '$owner_pid' (expected $session_pid)"; fi

introspection="$(bctl introspect org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 2>&1)"
printf '%s\n' "$introspection" > "$report/introspect.txt"
if grep -q 'method' <<<"$introspection"; then
  for method in GetContractVersion GetStatusJson GetUsageSummaryJson; do
    if grep -q "$method" <<<"$introspection"; then pass "interface exposes $method"; else fail "interface missing $method"; fi
  done
  if grep -q 'StatusChanged' <<<"$introspection"; then pass "interface exposes StatusChanged signal"; else fail "StatusChanged signal missing"; fi
else
  printf 'NOTE  busctl introspect unavailable: %s\n' "$(head -1 <<<"$introspection")" | tee -a "$report/results.txt"
fi

version="$(bctl call --timeout=10 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetContractVersion 2>&1)"
if grep -qw 3 <<<"$version"; then pass "GetContractVersion returned 3"; else fail "unexpected contract version: $version"; fi

status="$(bctl call --timeout=10 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetStatusJson 2>&1)"
if grep -q 'state' <<<"$status"; then pass "GetStatusJson returned real status"; else fail "GetStatusJson failed: $status"; fi
if grep -q 'pendingDurableDeltas' <<<"$status"; then pass "status includes durable diagnostics"; else fail "durable diagnostics missing from status"; fi

start_iso="$(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ)"
end_iso="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
summary="$(bctl call --timeout=10 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetUsageSummaryJson sss "$start_iso" "$end_iso" '' 2>&1)"
if grep -q 'rxBytes' <<<"$summary"; then pass "usage query answered over IPC"; else fail "usage query failed: $summary"; fi

printf '\n== Collector role separation ==\n'
kill "$session_pid" >/dev/null 2>&1 || true
if wait_for_name absent 6; then pass "bus name released when session daemon stopped"; else fail "bus name still owned after session daemon stopped"; fi

start_daemon collector procfs
collector_pid="$started_pid"
# procfs samples every 2s and the pipeline flushes every 5s; allow time for a real batch.
sleep 9
if wait_for_name absent 2; then pass "collector did not claim org.flow.Daemon"; else fail "collector claimed the session bus name"; fi
buckets="$(count_buckets)"
if [[ "$buckets" -gt 0 ]]; then pass "collector wrote $buckets real bucket rows (procfs)"; else fail "collector wrote no buckets"; fi
kill "$collector_pid" >/dev/null 2>&1 || true

printf '\n== Session daemon reads collector data ==\n'
start_daemon session
session_pid="$started_pid"
if wait_for_name present 6; then pass "session daemon restarted on the bus"; else fail "session daemon did not restart"; fi
summary2="$(bctl call --timeout=10 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetUsageSummaryJson sss "$start_iso" "$end_iso" '' 2>&1)"
if grep -q 'rxBytes' <<<"$summary2"; then pass "session daemon queried the shared database"; else fail "post-restart query failed: $summary2"; fi

printf '\n== D-Bus on-demand activation (source binary) ==\n'
kill "$session_pid" >/dev/null 2>&1 || true
if wait_for_name absent 6; then pass "name released before activation test"; else fail "name still owned before activation test"; fi
activation="$(bctl call --timeout=12 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetContractVersion 2>&1)"
printf '%s\n' "$activation" > "$report/activation.out"
if grep -qw '3' <<<"$activation"; then
  pass "activating call returned contract v3 without a retry"
else
  note "activating call raced startup: $(head -1 <<<"$activation")"
fi
if wait_for_name present 8; then pass "activation spawned a new daemon"; else fail "activation did not spawn a daemon"; fi
activated_pid="$(name_owner_pid)"
if [[ -n "$activated_pid" && "$activated_pid" != "$session_pid" ]]; then
  pids+=("$activated_pid")
  pass "activated daemon is a new process (pid $activated_pid)"
else
  fail "activated daemon pid unchanged: $activated_pid"
fi
retry="$(bctl call --timeout=10 org.flow.Daemon /org/flow/Daemon org.flow.Daemon1 GetContractVersion 2>&1)"
if grep -qw '3' <<<"$retry"; then pass "post-activation call returned v3"; else fail "post-activation call failed: $retry"; fi

printf '\nChecks: %d, failures: %d\nReport: %s\n' "$CHECKS" "$FAILS" "$report"
[[ "$FAILS" -eq 0 ]] || exit 1
exit 0
