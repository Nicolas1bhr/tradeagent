#!/bin/bash
# Drive the Windows GUI from here. One command, one round trip, JSON back.
#
#   tools/win-ui.sh ping
#   tools/win-ui.sh windows
#   tools/win-ui.sh shot [--window ATAS] [--full] [--out /tmp/x.png]
#   tools/win-ui.sh tree --window ATAS [--depth 6] [--all]
#   tools/win-ui.sh find --window ATAS --query Strategies [--type Button]
#   tools/win-ui.sh invoke --ref e12
#   tools/win-ui.sh click  --ref e12 | --x 400 --y 300 [--button right] [--double]
#   tools/win-ui.sh type   --text "hello"
#   tools/win-ui.sh key    --keys "CTRL+a"
#   tools/win-ui.sh setvalue --ref e12 --value "ES"
#   tools/win-ui.sh launch --path 'C:\Program Files (x86)\ATAS Platform\OFT.Platform.exe'
#   tools/win-ui.sh wait   --window ATAS [--timeoutMs 60000]
#   tools/win-ui.sh raw '<json>'          # anything, including {"op":"batch","items":[...]}
#
# The transport is a pair of directories on the machine rather than a socket, so nothing ever asks
# Windows Defender Firewall for permission — there is no user in front of this to click Yes, and the
# product's whole promise is that there is at most one such prompt ever. The JSON is built here,
# base64'd, and written on the far side by PowerShell, so it survives zsh, ssh, cmd.exe and
# PowerShell without a single escaped quote (trap 11, one layer deeper).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
[ -f "$HOME/.tradeagent/win.env" ] && source "$HOME/.tradeagent/win.env"
: "${TA_WIN_HOST:?set TA_WIN_HOST, or create ~/.tradeagent/win.env}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

[ $# -ge 1 ] || { sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }

OP="$1"; shift
TIMEOUT=90
LOCAL_OUT=""
REMOTE_SHOT='C:\ta\shots\ui.png'

if [ "$OP" = "raw" ]; then
  REQ="${1:?raw needs a JSON argument}"
else
  # Build the request JSON from --flags. Numbers and booleans are emitted unquoted so the agent's
  # typed reads (Int/Bool) work; everything else is a JSON string, correctly escaped.
  REQ="$(OP="$OP" python3 - "$@" <<'PY'
import json, os, sys
req = {"op": os.environ["OP"]}
args, i = sys.argv[1:], 0
while i < len(args):
    a = args[i]
    if not a.startswith("--"):
        raise SystemExit(f"unexpected argument '{a}' (flags look like --window ATAS)")
    key = a[2:]
    if i + 1 < len(args) and not args[i + 1].startswith("--"):
        val = args[i + 1]
        i += 2
        if val.lstrip("-").isdigit():
            req[key] = int(val)
        elif val.lower() in ("true", "false"):
            req[key] = val.lower() == "true"
        else:
            req[key] = val
    else:
        req[key] = True          # a bare flag such as --full or --all
        i += 1
if "out" in req:                 # local-only; not part of the wire request
    del req["out"]
print(json.dumps(req))
PY
)"
  # --out is ours, not the agent's: capture to a known remote path, then fetch it.
  while [ $# -gt 0 ]; do
    case "$1" in
      --out) LOCAL_OUT="${2:?--out needs a path}"; shift 2 ;;
      *) shift ;;
    esac
  done
  if [ -n "$LOCAL_OUT" ]; then
    REQ="$(REQ="$REQ" P="$REMOTE_SHOT" python3 -c 'import json,os;r=json.loads(os.environ["REQ"]);r["path"]=os.environ["P"];print(json.dumps(r))')"
  fi
fi

B64="$(printf '%s' "$REQ" | base64 | tr -d '\n')"

# Retry ONLY an SSH authentication failure, and only because of what it proves: auth happens before
# a single byte of the request is written, so the agent never saw it and nothing was actuated. This
# machine refuses roughly one connection in ten under rapid use. A blanket retry would be unsafe —
# re-sending a click that may already have landed is how an automated trading UI presses a button
# twice — so the retry is keyed to that one message and nothing else.
run_once() { "$HERE/win-ps.sh" <<PS
\$ErrorActionPreference = 'Stop'
\$root = 'C:\ta\agent'
if (-not (Test-Path "\$root\in")) { New-Item -ItemType Directory -Force -Path "\$root\in","\$root\out" | Out-Null }

# Refuse rather than hang when nothing is listening. A request that sits in the queue for ninety
# seconds is indistinguishable from a slow UI, and that is the wrong thing to be guessing about.
\$alive = "\$root\alive.json"
\$fresh = \$false
if (Test-Path \$alive) {
  \$j = Get-Content \$alive -Raw | ConvertFrom-Json
  \$fresh = ((Get-Date) - [datetime]::Parse(\$j.at)).TotalSeconds -lt 15
}
if (-not \$fresh) {
  '{"ok":false,"error":"the UI agent is not running (no fresh heartbeat). tools/win-agent.sh status"}'
  exit 0
}

\$id = [guid]::NewGuid().ToString('n').Substring(0,12)
\$body = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$B64'))
Set-Content -Path "\$root\in\\\$id.json" -Value \$body -Encoding utf8 -NoNewline

\$deadline = (Get-Date).AddSeconds($TIMEOUT)
\$out = "\$root\out\\\$id.json"
while ((Get-Date) -lt \$deadline) {
  if (Test-Path \$out) { Get-Content \$out -Raw; Remove-Item \$out -Force -EA 0; exit 0 }
  Start-Sleep -Milliseconds 100
}
'{"ok":false,"error":"the UI agent did not answer within $TIMEOUT s"}'
PS
}

RESULT=""
for attempt in 1 2 3 4; do
  RESULT="$(run_once 2>&1)" || true
  case "$RESULT" in
    *"Permission denied"*|*"Connection closed"*|*"kex_exchange"*|*"Connection reset"*)
      [ "$attempt" -lt 4 ] || break
      sleep $((attempt * 2))
      continue ;;
  esac
  break
done

echo "$RESULT"

if [ -n "$LOCAL_OUT" ] && printf '%s' "$RESULT" | grep -q '"ok":true'; then
  SCP_OPTS=(-o StrictHostKeyChecking=accept-new -o LogLevel=ERROR)
  if [ -n "${TA_WIN_PASSWORD:-}" ]; then
    SSHPASS="$TA_WIN_PASSWORD" sshpass -e scp "${SCP_OPTS[@]}" \
      -o PreferredAuthentications=password -o PubkeyAuthentication=no \
      "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/shots/ui.png" "$LOCAL_OUT" >/dev/null
  else
    scp "${SCP_OPTS[@]}" "$TA_WIN_USER@$TA_WIN_HOST:C:/ta/shots/ui.png" "$LOCAL_OUT" >/dev/null
  fi
  echo "saved $LOCAL_OUT"
fi
