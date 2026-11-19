#!/bin/sh
set -e

# Draft packaging helper. Intended for the future .deb prerm script.
systemctl stop flow-collector.service >/dev/null 2>&1 || true
systemctl disable flow-collector.service >/dev/null 2>&1 || true

if [ -n "$SUDO_USER" ] && id "$SUDO_USER" >/dev/null 2>&1; then
  runuser -u "$SUDO_USER" -- systemctl --user stop flow-daemon.service >/dev/null 2>&1 || true
  runuser -u "$SUDO_USER" -- systemctl --user disable flow-daemon.service >/dev/null 2>&1 || true
fi
