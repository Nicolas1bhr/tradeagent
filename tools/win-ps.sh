#!/bin/bash
# Run a PowerShell script on the Windows machine, reading it from stdin or a file.
#
# Why this exists rather than `win-run.sh 'powershell -Command "..."'`: that form has to survive
# zsh quoting, then ssh's own re-parse, then cmd.exe, then PowerShell — four layers, each with
# different escape rules. Anything containing a quote, a $ or a backslash silently arrives mangled,
# and the symptom is empty output rather than an error. -EncodedCommand takes UTF-16LE base64, so
# the script crosses all four layers untouched.
#
#   tools/win-ps.sh <<'EOF'
#   Get-Process | Select-Object -First 3
#   EOF
#
#   tools/win-ps.sh script.ps1
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
[ -f "$HOME/.tradeagent/win.env" ] && source "$HOME/.tradeagent/win.env"

SRC="$(if [ $# -gt 0 ] && [ -f "$1" ]; then cat "$1"; else cat; fi)"
# PowerShell's progress stream arrives as a CLIXML blob on stderr and reads like corruption.
SRC="\$ProgressPreference='SilentlyContinue'
$SRC"
# PowerShell wants UTF-16LE ("Unicode"), base64 with no line breaks.
ENC="$(printf '%s' "$SRC" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"
"$HERE/win-run.sh" "powershell -NoProfile -NonInteractive -EncodedCommand $ENC"
