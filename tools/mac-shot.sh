#!/bin/bash
# Capture ONLY the TradeAgent window. A full-screen grab would catch whatever else is on the
# operator's desktop, which is nobody's business and makes the screenshot useless for review anyway.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-${TMPDIR:-/tmp}/tradeagent.png}"

INFO="$(python3 "$HERE/mac-winid.py")" || { echo "no TradeAgent window on screen"; exit 1; }
WID="${INFO%% *}"
osascript -e 'tell application "System Events" to tell process "TradeAgent" to set frontmost to true' 2>/dev/null || true
sleep 0.5
screencapture -x -o -l"$WID" "$OUT" 2>/dev/null || true
[ -s "$OUT" ] || screencapture -x -o -R"${INFO#* }" "$OUT"
echo "saved $OUT"
