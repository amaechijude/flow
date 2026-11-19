#!/bin/sh
set -e

# Draft packaging helper. Intended for the future .deb postinst script.
case "$1" in
  configure)
    getent group flow >/dev/null 2>&1 || groupadd --system flow
    install -d -o root -g flow -m 2770 /var/lib/flow

    if [ -f /var/lib/flow/flow.db ]; then
      chgrp flow /var/lib/flow/flow.db
      chmod 0660 /var/lib/flow/flow.db
    fi

    if [ -n "$SUDO_USER" ] && id "$SUDO_USER" >/dev/null 2>&1; then
      usermod -aG flow "$SUDO_USER" >/dev/null 2>&1 || true
    fi

    systemctl daemon-reload >/dev/null 2>&1 || true
    systemctl enable flow-collector.service >/dev/null 2>&1 || true

    echo "Flow collector enabled. Add the desktop user to the 'flow' group and re-login if needed."
    echo "Enable the user IPC service with: systemctl --user enable --now flow-daemon.service"
    ;;
esac
