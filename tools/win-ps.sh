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

# An argument that is not a file is a mistake, not a script: the old form fell through to reading
# stdin, which under a heredoc-less caller is empty, and an empty script runs fine and prints
# nothing. That is indistinguishable from "the machine did not answer" — the exact failure this
# whole file exists to stop (trap 11). Say so instead.
if [ $# -gt 0 ]; then
  [ -f "$1" ] || { echo "win-ps.sh: '$1' is not a file. Pass a .ps1 path, or pipe the script on stdin:" >&2
                   echo "  tools/win-ps.sh <<'EOF'" >&2; echo "  Get-Process | Select-Object -First 3" >&2
                   echo "  EOF" >&2; exit 2; }
  SRC="$(cat "$1")"
else
  SRC="$(cat)"
fi
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
# Both branches are verified end to end. An 8,440-byte script travelled as a file and ran:
#   LONG SCRIPT PATH REACHED: C:\ta\win-ps-tmp.ps1
#   host: <redacted: host names stay out of the repo>
if [ "${#ENC}" -lt 7000 ]; then
  exec "$HERE/win-run.sh" "powershell -NoProfile -NonInteractive -EncodedCommand $ENC"
fi

LOCAL="$(mktemp -t win-ps).ps1"
# The BOM is load-bearing. Windows PowerShell reads a .ps1 with no byte-order mark as ANSI, so every
# non-ASCII character in the script — an em dash, a curly quote — arrives as mojibake and can break
# string parsing outright ("The string is missing the terminator"). The error names a line that is
# perfectly correct, which sends you hunting for an unbalanced quote that is not there. The
# -EncodedCommand path above is immune because it declares UTF-16LE; only this branch needs it, and
# the first version of this branch was verified with pure ASCII and so never showed it.
printf '\xEF\xBB\xBF%s' "$SRC" > "$LOCAL"
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
