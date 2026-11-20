#!/bin/sh
set -e

# FLOW_DATA_DIR / FLOW_PURGE_CONFIG are internal overrides used by the packaging
# test harness. Defaults are the real packaging paths.
data_dir="${FLOW_DATA_DIR:-/var/lib/flow}"
purge_config="${FLOW_PURGE_CONFIG:-/etc/flow/purge-data.conf}"

# Measurement history is user data, never package data. It survives remove and
# the default purge. Destruction requires an explicit opt-in; see data-policy.md.
case "${1:-}" in
  purge)
    do_purge=no
    if [ "${FLOW_PURGE_DATA:-}" = "yes" ]; then
      do_purge=yes
    elif [ -r "$purge_config" ] &&
      grep -Eq '^[[:space:]]*PURGE_DATA[[:space:]]*=[[:space:]]*yes[[:space:]]*$' "$purge_config"; then
      do_purge=yes
    fi

    if [ "$do_purge" = "yes" ]; then
      rm -rf "$data_dir"
      echo "Flow measurement data removed from $data_dir (explicit opt-in)."
    else
      echo "Flow measurement data preserved in $data_dir."
      echo "To delete it: FLOW_PURGE_DATA=yes dpkg -P flow, or set PURGE_DATA=yes in /etc/flow/purge-data.conf."
    fi
    ;;
  remove)
    echo "Flow measurement data preserved in $data_dir."
    ;;
  upgrade|failed-upgrade|*)
    ;;
esac
