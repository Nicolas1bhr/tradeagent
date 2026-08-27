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
[ -f "$HOME/.tradeagent/win.env" ] && source "$HOME/.tradeagent/win.env"
: "${TA_WIN_HOST:?set TA_WIN_HOST, or create ~/.tradeagent/win.env}"

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
$session = (quser 2>&1 | Out-String)
$state   = if ($session -match '\s(Active|Disc)\s') { $Matches[1] } else { 'unknown' }
$sid     = if ($session -match '\s+(\d+)\s+(Active|Disc)\s') { [int]$Matches[1] } else { -1 }
$rdp     = $session -match 'rdp-tcp'
$locked  = [bool](Get-Process LogonUI -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $sid })

Say "host"      $env:COMPUTERNAME
Say "user"      $env:USERNAME
Say "session"   ("$state (id $sid" + $(if ($rdp) { ", over RDP)" } else { ", console)" }))
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

Write-Output ""
if ($locked -or $state -ne 'Active') {
  Write-Output "  VERDICT: console work only. There is no live desktop, so ATAS cannot be started and"
  Write-Output "           screen captures will come back blank. Sign in on the machine to go further."
  exit 3
}
if ($rdp) {
  Write-Output "  VERDICT: desktop is live, but it belongs to an RDP session. ATAS and GUI work are"
  Write-Output "           available to whoever is at that session. tools/win-shot.sh is NOT: its"
  Write-Output "           scheduled task lands on the physical console, which is a different desktop"
  Write-Output "           and captures blank. Screen captures need someone signed in at the console."
  exit 0
}
Write-Output "  VERDICT: desktop is live on the console. GUI work, ATAS and screen captures all work."
PS
