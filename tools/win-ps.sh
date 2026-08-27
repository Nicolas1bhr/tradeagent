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
: "${TA_WIN_HOST:?set TA_WIN_HOST, or create ~/.tradeagent/win.env}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

SRC="$(if [ $# -gt 0 ] && [ -f "$1" ]; then cat "$1"; else cat; fi)"
# PowerShell's progress stream arrives as a CLIXML blob on stderr and reads like corruption.
SRC="\$ProgressPreference='SilentlyContinue'
$SRC"
# PowerShell wants UTF-16LE ("Unicode"), base64 with no line breaks.
ENC="$(printf '%s' "$SRC" | iconv -f UTF-8 -t UTF-16LE | base64 | tr -d '\n')"

# cmd.exe caps a command line at ~8191 characters, and UTF-16LE base64 is ~2.7x the source, so a
# script of much more than 2 KB blows the limit. The failure is "The command line is too long." on
# stderr with no output, which looks like the script failed rather than like it never ran. Past that
# size the script travels as a file instead. The encoded path stays the default because it needs one
# round trip rather than two.
#
# Branch selection is verified (a short script goes straight to ssh, a long one reaches scp first).
# The remote execution of the file path is NOT VERIFIED — the Windows machine went to sleep before
# it could be run end to end.
if [ "${#ENC}" -lt 7000 ]; then
  exec "$HERE/win-run.sh" "powershell -NoProfile -NonInteractive -EncodedCommand $ENC"
fi

LOCAL="$(mktemp -t win-ps).ps1"
printf '%s' "$SRC" > "$LOCAL"
trap 'rm -f "$LOCAL"' EXIT
REMOTE='C:/ta/win-ps-tmp.ps1'
SCP_OPTS=(-o StrictHostKeyChecking=accept-new -o LogLevel=ERROR)
if [ -n "${TA_WIN_PASSWORD:-}" ]; then
  SSHPASS="$TA_WIN_PASSWORD" sshpass -e scp "${SCP_OPTS[@]}" \
    -o PreferredAuthentications=password -o PubkeyAuthentication=no \
    "$LOCAL" "$TA_WIN_USER@$TA_WIN_HOST:$REMOTE" >/dev/null
else
  scp "${SCP_OPTS[@]}" "$LOCAL" "$TA_WIN_USER@$TA_WIN_HOST:$REMOTE" >/dev/null
fi
exec "$HERE/win-run.sh" 'powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File C:\ta\win-ps-tmp.ps1'
