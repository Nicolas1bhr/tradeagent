#!/bin/bash
# Run a command on the Windows machine over SSH. See tools/README.md for configuration;
# TA_WIN_BOX=<name> picks a machine other than the default one (tools/win-env.sh).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/win-env.sh"

win_ssh "$@"
