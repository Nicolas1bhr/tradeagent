#!/bin/bash
# Capture the TradeAgent window on the Windows desktop and bring the PNG back.
#
# This goes through a scheduled task with LogonType Interactive because a GUI program started over
# SSH runs in a session with no desktop: screenshots come back black and clicks go nowhere. The
# desktop must also be UNLOCKED — a locked session captures blank white, which reads as a broken app
# rather than a locked screen.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-${TMPDIR:-/tmp}/tradeagent-windows.png}"
: "${TA_WIN_HOST:?set TA_WIN_HOST}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

"$HERE/win-run.sh" 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\ta\tools\shotwin.ps1 -Proc TradeAgent -Out C:\ta\shots\latest.png'

if [ -n "${TA_WIN_PASSWORD:-}" ]; then
  SSHPASS="$TA_WIN_PASSWORD" sshpass -e scp -o StrictHostKeyChecking=accept-new -o LogLevel=ERROR \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/shots/latest.png" "$OUT"
else
  scp -o StrictHostKeyChecking=accept-new -o LogLevel=ERROR \
    "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/shots/latest.png" "$OUT"
fi
echo "saved $OUT"
