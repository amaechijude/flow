#!/usr/bin/env bash
# Complete, auditable uninstall of the Flow prototype package.
# Run with sudo: FLOW's system unit, group and /var/lib/flow need root.
# By default this script removes measurement data; --keep-data preserves it.
set -uo pipefail

keep_data=0
dry_run=0
assume_yes=0
remove_dev_data=0

usage() {
  cat <<'EOF'
Usage: sudo ./packaging/uninstall-clean.sh [options]

Options:
  --keep-data        Keep /var/lib/flow (default: delete all measurement data).
  --remove-dev-data  Also delete ~/.local/share/flow (source-run database).
  --dry-run          Print every action without changing anything.
  --yes              Skip the confirmation prompt.
  -h, --help         Show this help.

The script stops both units, purges the package, removes leftover systemd
enablement, the 'flow' group (if it has no other members), the optional purge
config, and verifies the result. Irreversible data removal requires confirmation
unless --yes is given.
EOF
}

for arg in "$@"; do
  case "$arg" in
    --keep-data) keep_data=1 ;;
    --remove-dev-data) remove_dev_data=1 ;;
    --dry-run) dry_run=1 ;;
    --yes) assume_yes=1 ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown option: %s\n' "$arg" >&2; usage >&2; exit 2 ;;
  esac
done

if [[ "$(id -u)" -ne 0 && "$dry_run" -eq 0 ]]; then
  echo "error: run as root (sudo ./packaging/uninstall-clean.sh ...)" >&2
  exit 2
fi
target_user="${SUDO_USER:-$(id -un)}"
target_uid="$(id -u "$target_user" 2>/dev/null || echo 0)"
# sudo/env_reset drops the session bus variables; set them explicitly for
# runuser so 'systemctl --user' and busctl target the right session.
user_env=(env "XDG_RUNTIME_DIR=/run/user/$target_uid" "DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$target_uid/bus")

run() {
  printf '+ %s\n' "$*"
  if [[ "$dry_run" -eq 1 ]]; then return 0; fi
  "$@"
}

run_shell() {
  printf '+ sh -c %q\n' "$1"
  if [[ "$dry_run" -eq 1 ]]; then return 0; fi
  sh -c "$1"
}

user_systemctl() {
  if [[ -z "$target_user" ]]; then
    printf 'skip (no SUDO_USER): systemctl --user %s\n' "$*"
    return 0
  fi
  run runuser -u "$target_user" -- "${user_env[@]}" systemctl --user "$@"
}

printf 'Flow cleanup plan\n'
printf '  package purge:        %s\n' "flow"
printf '  measurement data:     %s\n' "$([[ "$keep_data" -eq 1 ]] && echo 'PRESERVED (/var/lib/flow)' || echo 'DELETED (/var/lib/flow)')"
printf '  source-run data:      %s\n' "$([[ "$remove_dev_data" -eq 1 ]] && echo "DELETED (/$target_user/.local/share/flow)" || echo 'PRESERVED (use --remove-dev-data)')"
printf '  dry run:              %s\n' "$([[ "$dry_run" -eq 1 ]] && echo yes || echo no)"

if [[ "$dry_run" -eq 0 && "$assume_yes" -eq 0 ]]; then
  printf '\nThis permanently removes Flow and its data. Continue? [y/N] '
  read -r answer
  [[ "$answer" == "y" || "$answer" == "Y" ]] || { echo "aborted"; exit 0; }
fi

printf '\n== Stop units ==\n'
user_systemctl disable --now flow-daemon.service
run systemctl disable --now flow-collector.service

printf '\n== Stop leftover daemon processes ==\n'
run_shell "pkill -f '^/usr/lib/flow/daemon/Flow.Daemon' 2>/dev/null || true"

printf '\n== Purge package ==\n'
if dpkg -s flow >/dev/null 2>&1 || [[ "$dry_run" -eq 1 ]]; then
  if [[ "$keep_data" -eq 1 ]]; then
    run dpkg -P flow
  else
    run env FLOW_PURGE_DATA=yes dpkg -P flow
  fi
else
  echo "package flow is not installed; skipping purge"
fi

printf '\n== Remove leftover user-unit enablement ==\n'
if [[ -n "$target_user" ]]; then
  user_home="$(getent passwd "$target_user" | cut -d: -f6)"
  if [[ -n "$user_home" ]]; then
    run_shell "rm -f '$user_home/.config/systemd/user/default.target.wants/flow-daemon.service' 2>/dev/null || true"
  fi
fi
run systemctl daemon-reload
if [[ -n "$target_user" ]]; then
  user_systemctl daemon-reload
fi

printf '\n== Remove optional purge config ==\n'
run_shell "rm -f /etc/flow/purge-data.conf 2>/dev/null || true"
run_shell "rmdir /etc/flow 2>/dev/null || true"

printf '\n== Remove flow group ==\n'
if [[ "$keep_data" -eq 1 ]]; then
  echo "keeping group 'flow': preserved /var/lib/flow is root:flow"
else
  group_line="$(getent group flow || true)"
  if [[ -n "$group_line" ]]; then
    members="${group_line##*:}"
    # A single member equal to the invoking user is acceptable to remove.
    if [[ -z "$members" || "$members" == "$target_user" ]]; then
      run groupdel flow
    else
      echo "keeping group 'flow': other members exist ($members)"
    fi
  else
    echo "group 'flow' already absent"
  fi
fi

if [[ "$remove_dev_data" -eq 1 && -n "$target_user" && -n "${user_home:-}" ]]; then
  printf '\n== Remove source-run data ==\n'
  run_shell "rm -rf '$user_home/.local/share/flow'"
fi

printf '\n== Verification ==\n'
if [[ "$dry_run" -eq 1 ]]; then
  echo "DRY RUN: no changes were made, so verification is skipped."
  echo "Dry run complete. Re-run without --dry-run (as root) to actually clean up."
  exit 0
fi
if dpkg -s flow >/dev/null 2>&1; then
  echo "FAIL: package 'flow' is still installed"
else
  echo "PASS: package removed"
fi
if [[ -f /var/lib/flow/flow.db ]]; then
  if [[ "$keep_data" -eq 1 ]]; then echo "PASS: /var/lib/flow/flow.db preserved"; else echo "FAIL: database still present"; fi
elif [[ "$keep_data" -eq 1 ]]; then
  echo "note: no database was present to preserve"
else
  echo "PASS: /var/lib/flow removed"
fi
if getent group flow >/dev/null; then echo "note: group 'flow' still exists"; else echo "PASS: group removed"; fi
if pgrep -f '^/usr/lib/flow/daemon/Flow.Daemon' >/dev/null; then echo "FAIL: daemon processes still running"; else echo "PASS: no daemon processes"; fi
if busctl --address="unix:path=/run/user/$target_uid/bus" list --no-pager 2>/dev/null | awk '$1 == "org.flow.Daemon" && $2 != "-" { found = 1 } END { exit !found }'; then
  echo "FAIL: org.flow.Daemon still owned on the session bus"
else
  echo "PASS: no session-bus owner"
fi

printf '\nDone. A clean install can now be performed with: sudo dpkg -i <flow.deb>\n'
