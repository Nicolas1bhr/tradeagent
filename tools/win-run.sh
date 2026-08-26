#!/bin/bash
# Run a command on the Windows machine over SSH. See tools/README.md for configuration.
set -euo pipefail
: "${TA_WIN_HOST:?set TA_WIN_HOST}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

OPTS=(-o StrictHostKeyChecking=accept-new -o ConnectTimeout=15
      -o NumberOfPasswordPrompts=1 -o LogLevel=ERROR)

if [ -n "${TA_WIN_PASSWORD:-}" ]; then
  SSHPASS="$TA_WIN_PASSWORD" exec sshpass -e ssh "${OPTS[@]}" \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    "$TA_WIN_USER@$TA_WIN_HOST" "$@"
else
  exec ssh "${OPTS[@]}" "$TA_WIN_USER@$TA_WIN_HOST" "$@"
fi
