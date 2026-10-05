#!/bin/bash
# "Can I actually do work on the Windows machine right now?" — answered in one command.
#
# Three different things all present as "it did not work", and telling them apart by hand costs a
# session each time:
#   * the machine is asleep or off Tailscale        -> nothing answers
#   * it answers SSH but the desktop is LOCKED      -> console apps run, GUI work silently fails:
#                                                      screen captures come back blank white and
#                                                      clicks go nowhere (tools/README.md, trap 2)
#   * the desktop is live                           -> everything is available
#
# ATAS is a GUI program. It cannot start, sign in, or load the bridge without an unlocked desktop,
# so this script refuses to let that be discovered halfway through a trading test.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "$HERE/win-env.sh"

NAME="${TA_WIN_NAME:-$TA_WIN_HOST}"

echo "== reachability =="
if command -v tailscale >/dev/null && tailscale ping -c 1 --timeout 5s "$NAME" >/dev/null 2>&1; then
  echo "  tailscale        : up ($(tailscale ping -c 1 --timeout 5s "$NAME" 2>&1 | head -1))"
elif ping -c 1 -t 5 "$TA_WIN_HOST" >/dev/null 2>&1; then
  echo "  icmp             : up"
else
  echo "  UNREACHABLE      : $NAME ($TA_WIN_HOST) does not answer."
  echo "  -> wake the machine, or check it is signed in to Tailscale."
  exit 1
fi

echo "== machine =="
"$HERE/win-ps.sh" <<'PS'
function Say($k,$v){ Write-Output ("  {0,-17}: {1}" -f $k,$v) }

# LogonUI must be matched to the SESSION, not merely to the machine. Windows runs a LogonUI in the
# physical console session whenever that console sits at the lock screen — which it does permanently
# on a box that is only ever reached over RDP. Asking "is any LogonUI running" therefore answers
# "yes" forever and reports a perfectly live remote desktop as locked, which is exactly the wrong
# way round for the one check that decides whether GUI work is possible at all.
# quser speaks the machine's language: a French Windows says "Actif" and "Déco" (the é arrives as
# whatever the console code page makes of it), and an English-only match read a live console session
# on the tests box as "unknown (id -1)" and "no active session".
$session = (quser 2>&1 | Out-String)
$state   = if ($session -match '\s(Active|Actif)\s') { 'Active' } elseif ($session -match '\s(Disc|D\S{1,3}co)\s') { 'Disc' } else { 'unknown' }
$sid     = if ($session -match '\s+(\d+)\s+(Active|Actif|Disc|D\S{1,3}co)\s') { [int]$Matches[1] } else { -1 }
$rdp     = $session -match 'rdp-tcp'
$locked  = [bool](Get-Process LogonUI -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid })

Say "host"      $env:COMPUTERNAME
Say "user"      $env:USERNAME
# The SESSIONNAME column goes blank once a session disconnects, so "not RDP" and "cannot tell"
# are the same reading there. Say which one it is rather than asserting the console.
$where = if ($rdp) { ", over RDP" } elseif ($state -eq 'Active') { ", console" } else { "" }
Say "session"   ("$state (id $sid$where)")
Say "desktop"   $(if ($locked) { "LOCKED" } elseif ($state -ne 'Active') { "no active session" } else { "live" })
Say "uptime"    ((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).ToString("d\d\ hh\:mm")

Write-Output "== ATAS =="
$atas = Test-Path "C:\Program Files (x86)\ATAS Platform"
Say "installed" $atas
Say "running"   ([bool](Get-Process OFT.Platform,OFT.PlatformX -ErrorAction SilentlyContinue))
Say "%APPDATA%" $(if (Test-Path "$env:APPDATA\ATAS") { "present" } else { "absent - ATAS has never been run" })
Say "Strategies" $(if (Test-Path "$env:APPDATA\ATAS\Strategies") { (Get-ChildItem "$env:APPDATA\ATAS\Strategies" -File -EA 0).Count.ToString() + " file(s)" } else { "absent" })

Write-Output "== TradeAgent =="
Say "repo"      $(if (Test-Path "C:\ta\repo") { "C:\ta\repo" } else { "absent - run tools/win-push.sh" })
Say "installed" (Test-Path "$env:LOCALAPPDATA\Programs\TradeAgent")
Say "home"      (Test-Path "$env:LOCALAPPDATA\TradeAgent")

# The UI agent is the authority now, and it settles this by trying rather than reasoning. A locked or
# disconnected session used to be reported as "console work only", which is wrong in the way that
# costs most: UI Automation and the bridge both keep working there, and only screen capture stops.
$alive = 'C:\ta\agent\alive.json'
$ag = $null
if (Test-Path $alive) {
  $j = Get-Content $alive -Raw | ConvertFrom-Json
  if (((Get-Date) - [datetime]::Parse($j.at)).TotalSeconds -lt 20) { $ag = $j }
}
Write-Output "== UI agent =="
if ($ag) {
  Say "session"    ($ag.session.ToString() + "  interactive=" + $ag.interactive)
  Say "automation" $(if ($ag.can_automate) { "WORKS - read the tree, find and invoke elements" } else { "NO" })
  Say "capture"    $(if ($ag.can_capture)  { "WORKS" } else { "NO - this session renders nothing (disconnected RDP?)" })
} else {
  Say "agent" "NOT RUNNING - tools/win-agent.sh status"
}

Write-Output ""
if ($ag -and $ag.can_automate -and $ag.can_capture) {
  Write-Output "  VERDICT: everything works. GUI automation, ATAS and screen captures are all available."
  exit 0
}
if ($ag -and $ag.can_automate) {
  Write-Output "  VERDICT: automation works, pictures do not. tools/win-ui.sh can read the UI tree,"
  Write-Output "           find elements and invoke them, and the ATAS bridge is unaffected — but"
  Write-Output "           'shot' fails, because a disconnected RDP session renders nothing. Nothing"
  Write-Output "           is blocked except LOOKING at it. Reconnect, or reboot into the console"
  Write-Output "           session (autologon is on), to get captures back."
  exit 0
}
if (-not $ag) {
  Write-Output "  VERDICT: no UI agent. Console work only until it is running -"
  Write-Output "           tools/win-agent.sh status, then install/start. It starts itself at logon,"
  Write-Output "           so this usually means nobody is logged on at all."
  exit 3
}
Write-Output "  VERDICT: the agent is up but reports it cannot drive the UI. Read the two lines above"
Write-Output "           before assuming anything about ATAS."
exit 3
PS
