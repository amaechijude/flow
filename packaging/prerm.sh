#!/bin/sh
set -e

# FLOW_DATA_DIR is an internal override used by the packaging test harness.
data_dir="${FLOW_DATA_DIR:-/var/lib/flow}"

stop_services() {
  systemctl stop flow-collector.service >/dev/null 2>&1 || true
  systemctl disable flow-collector.service >/dev/null 2>&1 || true

  if [ -n "${SUDO_USER:-}" ] && id "$SUDO_USER" >/dev/null 2>&1; then
    runuser -u "$SUDO_USER" -- systemctl --user stop flow-daemon.service >/dev/null 2>&1 || true
    runuser -u "$SUDO_USER" -- systemctl --user disable flow-daemon.service >/dev/null 2>&1 || true
  fi
}

case "${1:-}" in
  upgrade)
    stop_services
    # Taken after services stop so the copy is quiescent. Never deletes data.
    if [ -f "$data_dir/flow.db" ]; then
      cp -p "$data_dir/flow.db" "$data_dir/flow.db.pre-upgrade" >/dev/null 2>&1 || true
      chgrp flow "$data_dir/flow.db.pre-upgrade" >/dev/null 2>&1 || true
      chmod 0660 "$data_dir/flow.db.pre-upgrade" >/dev/null 2>&1 || true
    fi
    ;;
  remove|purge|deconfigure|*)
    stop_services
    ;;
esac
