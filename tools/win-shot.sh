#!/bin/bash
# Capture the TradeAgent window on the Windows desktop and bring the PNG back.
#
# This goes through a scheduled task with LogonType Interactive because a GUI program started over
# SSH runs in a session with no desktop: screenshots come back black and clicks go nowhere. The
# desktop must also be UNLOCKED — a locked session captures blank white, which reads as a broken app
# rather than a locked screen.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/win-env.sh"
OUT="${1:-${TMPDIR:-/tmp}/tradeagent-windows.png}"

"$HERE/win-run.sh" 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\ta\tools\shotwin.ps1 -Proc TradeAgent -Out C:\ta\shots\latest.png'

win_scp --get 'C:/ta/shots/latest.png' "$OUT"
echo "saved $OUT"
