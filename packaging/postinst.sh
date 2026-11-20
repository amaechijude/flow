#!/bin/sh
set -e

# FLOW_DATA_DIR is an internal override used by the packaging test harness.
data_dir="${FLOW_DATA_DIR:-/var/lib/flow}"

case "$1" in
  configure)
    getent group flow >/dev/null 2>&1 || groupadd --system flow
    install -d -o root -g flow -m 2770 "$data_dir"

    if [ -f "$data_dir/flow.db" ]; then
      chgrp flow "$data_dir/flow.db"
      chmod 0660 "$data_dir/flow.db"
    fi

    if [ -n "${SUDO_USER:-}" ] && id "$SUDO_USER" >/dev/null 2>&1; then
      usermod -aG flow "$SUDO_USER" >/dev/null 2>&1 || true
    fi

    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable flow-collector.service >/dev/null 2>&1 || true

    echo "Flow collector enabled. Add the desktop user to the 'flow' group and re-login if needed."
    echo "Enable the user IPC service with: systemctl --user enable --now flow-daemon.service"
    if [ -n "${2:-}" ]; then
      echo "Upgraded from ${2}: measurement history and settings were preserved."
      if [ -f "$data_dir/flow.db.pre-upgrade" ]; then
        echo "A pre-upgrade database copy is at $data_dir/flow.db.pre-upgrade."
      fi
    fi
    ;;
esac
