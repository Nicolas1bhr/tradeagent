#!/bin/bash
# Lifecycle for the in-session UI agent on the Windows test machine.
#
#   tools/win-agent.sh build     compile it there
#   tools/win-agent.sh install   register the scheduled task (starts at logon, and now)
#   tools/win-agent.sh start | stop | restart
#   tools/win-agent.sh status    is it alive, and can it actually drive a GUI?
#
# The agent must run INSIDE the interactive desktop session. A process started over SSH lands in a
# session with no desktop: screenshots come back black and clicks go nowhere (trap 2). A scheduled
# task with LogonType Interactive is the documented way into that session, so that is what this
# registers — with an AtLogon trigger, so the agent comes back by itself after every reboot and
# nobody has to be asked to start it.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
[ -f "$HOME/.tradeagent/win.env" ] && source "$HOME/.tradeagent/win.env"
: "${TA_WIN_HOST:?set TA_WIN_HOST, or create ~/.tradeagent/win.env}"
: "${TA_WIN_USER:?set TA_WIN_USER}"

EXE='C:\ta\repo\tools\winagent\bin\Release\net10.0-windows\winagent.exe'
TASK='TradeAgentUiAgent'
CMD="${1:-status}"

case "$CMD" in
  build)
    # Stop it first: a running agent holds its own winagent.exe open, and the build fails with
    # MSB3027 "the file is locked by winagent" rather than anything that names the real problem.
    "$0" stop >/dev/null 2>&1 || true
    sleep 1
    "$HERE/win-run.sh" 'cd C:\ta\repo\tools\winagent && dotnet build -c Release --nologo' \
      | grep -E 'error|Build succeeded|Error\(s\)' || true
    "$0" start >/dev/null 2>&1 || true
    sleep 2
    "$0" status
    ;;

  install)
    "$HERE/win-ps.sh" <<PS
\$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path C:\ta\agent\in, C:\ta\agent\out, C:\ta\shots | Out-Null
# Unregister-ScheduledTask, not schtasks: a native command writing to stderr becomes a TERMINATING
# error under ErrorActionPreference=Stop, so deleting a task that was never registered would abort
# the install on its very first run.
Unregister-ScheduledTask -TaskName '$TASK' -Confirm:\$false -EA SilentlyContinue
\$a = New-ScheduledTaskAction -Execute '$EXE' -WorkingDirectory 'C:\ta\agent'
\$p = New-ScheduledTaskPrincipal -UserId "\$env:COMPUTERNAME\\\$env:USERNAME" -LogonType Interactive -RunLevel Highest
\$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries \`
       -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
\$t = New-ScheduledTaskTrigger -AtLogOn -User "\$env:COMPUTERNAME\\\$env:USERNAME"
Register-ScheduledTask -TaskName '$TASK' -Action \$a -Principal \$p -Settings \$s -Trigger \$t -Force | Out-Null
"registered: $TASK  (trigger: at logon)"

# Starting it needs an interactive session to start it INTO. With nobody logged on there is none, and
# saying so is the whole point: the AtLogon trigger will bring the agent up by itself the moment
# somebody does log on, and no further action is needed then.
# NOT parsed out of 'query session' (no backticks here: this heredoc is unquoted, so bash would run
# them). Its USERNAME column is blank when nobody is logged on and the next column along is the
# session ID, so any "is there something after the name" test matches an
# empty console session and reports a user who is not there. Win32_ComputerSystem.UserName is the
# interactive console user or null, with no column guessing. (Trap 10's family.)
\$logged = -not [string]::IsNullOrWhiteSpace((Get-CimInstance Win32_ComputerSystem).UserName)
if (\$logged) {
  Start-ScheduledTask -TaskName '$TASK'
  Start-Sleep -Seconds 3
  \$p2 = Get-Process winagent -EA 0
  if (\$p2) { "started: pid " + (\$p2.Id -join ',') } else { "NOT STARTED - see C:\ta\agent\agent.log" }
} else {
  "not started: nobody is logged on, so there is no interactive session to start it into."
  "It will start by itself at the next logon."
}
PS
    ;;

  start)   "$HERE/win-run.sh" "schtasks /run /tn $TASK" ;;
  # cmd.exe is what is on the far end of ssh, and ';' is not a command separator there — it was
  # swallowed as an argument to schtasks and taskkill never ran, so 'stop' quietly stopped nothing.
  stop)
    "$HERE/win-run.sh" "schtasks /end /tn $TASK" >/dev/null 2>&1 || true
    "$HERE/win-run.sh" "taskkill /im winagent.exe /f" >/dev/null 2>&1 || true
    ;;
  restart) "$0" stop >/dev/null 2>&1 || true; sleep 1; "$0" start ;;

  status)
    "$HERE/win-ps.sh" <<'PS'
$alive = 'C:\ta\agent\alive.json'
$proc  = Get-Process winagent -EA 0
"process        : " + $(if ($proc) { "running (pid " + ($proc.Id -join ',') + ", session " + ($proc.SessionId -join ',') + ")" } else { "NOT RUNNING" })
if (Test-Path $alive) {
  $j = Get-Content $alive -Raw | ConvertFrom-Json
  $age = [int]((Get-Date) - [datetime]::Parse($j.at)).TotalSeconds
  "heartbeat      : ${age}s ago"
  "session        : " + $j.session + "   interactive=" + $j.interactive
  "desktop        : " + $j.desktop
  "screen         : " + $j.screen
} else { "heartbeat      : none — the agent has never run here" }
# Win32_ComputerSystem.UserName is the CONSOLE interactive user and nothing else, so it is empty
# for a perfectly live RDP session. It used to be printed as "logged on: NOBODY" next to an agent
# that was demonstrably driving that session. The agent's own heartbeat above is the authority.
$who = (Get-CimInstance Win32_ComputerSystem).UserName
"console user   : " + $(if ($who) { $who } else { "none (an RDP session does not show up here)" })
PS
    ;;

  *) echo "usage: $0 {build|install|start|stop|restart|status}" >&2; exit 2 ;;
esac
