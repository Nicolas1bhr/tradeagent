#!/bin/bash
# Run TradeAgent's test suite on a Windows machine, durably. The run lives ON that machine as a
# scheduled task (tools/win-test-run.ps1), so it survives this Mac sleeping, the SSH connection
# dropping and the Bash tool's ten-minute ceiling; this script only ships, starts and reads.
#
#   TA_WIN_BOX=tests tools/win-test.sh start [--src <tree>] [--filter <expr>]
#                                        ship the tree (default: this checkout, uncommitted edits
#                                        included) and start a run; prints the run id
#   TA_WIN_BOX=tests tools/win-test.sh status [id]          where it is; the verdict once there is one
#   TA_WIN_BOX=tests tools/win-test.sh wait [id] [minutes]  block up to N minutes (default 9), then
#                                        exit 0 green · 1 red/died/stopped · 3 still running (call again)
#   TA_WIN_BOX=tests tools/win-test.sh log [id] [lines]     tail the current step's output
#   TA_WIN_BOX=tests tools/win-test.sh fetch [id] [dir]     bring status, logs and trx files back
#   TA_WIN_BOX=tests tools/win-test.sh list | stop [id]
#
# [id] defaults to the latest run. One run at a time per machine: a second `start` is refused while
# one is in progress, which is the lock — two suites at once on four cores turn the timing category
# into a coin toss. Without --filter a run is the CI test job step for step (win-test-run.ps1 says how).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"

# The default machine is the ATAS box. A suite eats it for half an hour, beside a platform holding
# simulated positions, so the machine is always named, never defaulted.
if [ -z "${TA_WIN_BOX:-}" ]; then
  echo "win-test.sh: name the machine, e.g. TA_WIN_BOX=tests $0 $*" >&2
  echo "  (unset means the ATAS box, and the suite does not run beside ATAS)" >&2
  exit 2
fi
source "$HERE/win-env.sh"

CMD="${1:-status}"; [ $# -gt 0 ] && shift
ID_RE='^[0-9]{8}-[0-9]{6}-[0-9a-z]+$'
check_id() { [ -z "$1" ] || [[ "$1" =~ $ID_RE ]] || { echo "not a run id: $1" >&2; exit 2; }; }

# Shared by every remote call. Quoted heredoc: nothing in it is expanded by bash, so PowerShell's own
# $ signs need no escaping; values come in through the one line of variables put in front of it.
read -r -d '' PRELUDE <<'PS' || true
$ErrorActionPreference = 'Stop'
$Runs = 'C:\ta\runs'
$Task = 'TradeAgent-Tests'
function Resolve-Run([string]$id) {
  if ($id) {
    $d = Join-Path $Runs $id
    if (-not (Test-Path $d)) { Write-Host "no such run: $id"; exit 2 }
    return $d
  }
  $d = Get-ChildItem $Runs -Directory -EA SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
  if (-not $d) { Write-Host 'no runs yet'; exit 2 }
  return $d.FullName
}
function Get-RunState([string]$dir) {
  $f = Join-Path $dir 'status.json'
  for ($i = 0; $i -lt 5; $i++) {
    if (-not (Test-Path $f)) { return $null }
    try { return (Get-Content -Raw $f | ConvertFrom-Json) } catch { Start-Sleep -Milliseconds 200 }
  }
  return $null
}
# green | red | error | stopped | running | queued | died
function Get-Verdict($s, [string]$dir) {
  if (-not $s) {
    if (((Get-Date) - (Get-Item $dir).CreationTime).TotalSeconds -lt 180) { return 'queued' }
    return 'died'
  }
  if ($s.phase -eq 'done') { return [string]$s.verdict }
  $p = Get-Process -Id $s.pid -EA SilentlyContinue
  if ($p -and $p.ProcessName -eq 'powershell') { return 'running' }
  return 'died'
}
function Show-Run([string]$dir) {
  $s = Get-RunState $dir; $v = Get-Verdict $s $dir
  "run       : " + (Split-Path $dir -Leaf)
  $mf = Join-Path $dir 'meta.json'
  if (Test-Path $mf) {
    $m = Get-Content -Raw $mf | ConvertFrom-Json
    "source    : " + $m.src + "  " + $m.branch + " @ " + $m.sha + $(if ([int]$m.dirty -gt 0) { "  + " + $m.dirty + " uncommitted file(s)" } else { "" })
  }
  if ($s -and $s.filter) { "filter    : " + $s.filter }
  $line = switch ($v) {
    'running' { "RUNNING - " + $s.phase }
    'queued'  { "QUEUED - the task has not reported yet" }
    'died'    { if ($s) { "DIED during '" + $s.phase + "' - the runner is gone and left no verdict" } else { "DIED - the task never started" } }
    default   { $v.ToUpper() }
  }
  "state     : $line"
  if ($s) {
    $end = if ($s.finished) { [datetime]$s.finished } else { Get-Date }
    "elapsed   : " + ($end - [datetime]$s.started).ToString('hh\:mm\:ss')
    foreach ($st in @($s.steps)) { if ($st) { "  step  {0,-13} exit {1,-4} {2,6}s  {3}" -f $st.name, $st.exit, $st.seconds, $st.log } }
    # "not run" is total minus passed minus failed: a [Skip] with its reason, or a test that never got
    # to execute. It is printed because a green whose counts do not add up reads like a hidden failure.
    foreach ($t in @($s.tests)) { if ($t) { "  tests {0,-50} {1,5} total {2,5} passed {3,4} failed {4,3} not run" -f $t.file, $t.total, $t.passed, $t.failed, ($t.total - $t.passed - $t.failed) } }
    if ($s.timing_rescued) { "  NOTE: the timing category failed its first attempt and passed the retry; the first failure stands in the record" }
    $f = @($s.failed | Where-Object { $_ })
    if ($f.Count) { "failed    : " + $f.Count; $f | Select-Object -First 40 | ForEach-Object { "  $_" } }
    if ($s.error) { "error     : " + $s.error }
  }
}
PS

remote() {  # remote '<PowerShell variable assignments>'  < body
  { printf '%s\n' "$1"; printf '%s\n' "$PRELUDE"; cat; } | "$HERE/win-ps.sh"
}

case "$CMD" in
  start)
    SRC="$ROOT"; FILTER=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --src)    SRC="$(cd "${2:?--src needs a path}" && pwd)"; shift 2 ;;
        --filter) FILTER="${2:?--filter needs an expression}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
      esac
    done
    [ -f "$SRC/TradeAgent.sln" ] || { echo "no TradeAgent.sln in $SRC" >&2; exit 2; }
    SHA="$(git -C "$SRC" rev-parse --short=8 HEAD 2>/dev/null || echo nogit)"
    BRANCH="$(git -C "$SRC" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '?')"
    DIRTY="$(git -C "$SRC" status --porcelain 2>/dev/null | wc -l | tr -d ' ')"
    ID="$(date +%Y%m%d-%H%M%S)-$SHA"
    META_B64="$(SRC="$(basename "$SRC")" BRANCH="$BRANCH" SHA="$SHA" DIRTY="$DIRTY" python3 -c '
import json, os
print(json.dumps({k.lower(): os.environ[k] for k in ("SRC", "BRANCH", "SHA", "DIRTY")}))' | base64 | tr -d '\n')"
    FILTER_B64="$(printf '%s' "$FILTER" | base64 | tr -d '\n')"

    remote "\$Id = '$ID'; \$MetaB64 = '$META_B64'; \$FilterB64 = '$FILTER_B64'" <<'PS'
$t = Get-ScheduledTask -TaskName $Task -EA SilentlyContinue
if ($t -and $t.State -eq 'Running') {
  $busy = Get-ChildItem $Runs -Directory -EA SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
  Write-Host "REFUSED: a run is in progress ($($busy.Name)). One at a time: wait for it, or stop it."
  exit 4
}
New-Item -ItemType Directory -Force -Path $Runs | Out-Null
# Older runs keep what is small (status, logs, trx) and lose what is not (sources, builds, temp):
# a built tree is ~2 GB and nobody reads one twice. The 20 newest runs are kept at all.
Get-ChildItem $Runs -Directory | ForEach-Object {
  foreach ($big in 'src', 'tmp', 'src.tgz', 'bundle.tgz') {
    $p = Join-Path $_.FullName $big
    if (Test-Path $p) { Remove-Item -Recurse -Force -Path $p -EA SilentlyContinue }
  }
}
Get-ChildItem $Runs -Directory | Sort-Object Name -Descending | Select-Object -Skip 19 |
  Remove-Item -Recurse -Force -EA SilentlyContinue
$dir = Join-Path $Runs $Id
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[IO.File]::WriteAllText((Join-Path $dir 'meta.json'), [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($MetaB64)))
$filter = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($FilterB64))
if ($filter) { [IO.File]::WriteAllText((Join-Path $dir 'filter.txt'), $filter) }
"prepared  : $dir   (C: has " + [math]::Round((Get-PSDrive C).Free / 1GB) + " GB free)"
PS

    TARBALL="$(mktemp -t ta-test).tgz"
    RUNNER="$(mktemp -t ta-run).ps1"
    trap 'rm -f "$TARBALL" "$RUNNER"' EXIT
    # COPYFILE_DISABLE: see win-push.sh — macOS tar otherwise ships ._* files that csc rejects.
    COPYFILE_DISABLE=1 tar --exclude='.git' --exclude='bin' --exclude='obj' --exclude='artifacts' \
      --exclude='TestResults' -czf "$TARBALL" -C "$SRC" .
    # The BOM: Windows PowerShell reads a .ps1 without one as ANSI (see win-ps.sh).
    printf '\xEF\xBB\xBF' > "$RUNNER"; cat "$HERE/win-test-run.ps1" >> "$RUNNER"
    win_scp "$TARBALL" "C:/ta/runs/$ID/src.tgz" >/dev/null
    win_scp "$RUNNER" "C:/ta/runs/$ID/run.ps1" >/dev/null
    echo "shipped   : $(du -h "$TARBALL" | cut -f1 | tr -d ' ') from $(basename "$SRC") ($BRANCH @ $SHA, $DIRTY uncommitted)"

    remote "\$Id = '$ID'" <<'PS'
$dir = Join-Path $Runs $Id
$arg = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$dir\run.ps1`" -RunDir `"$dir`""
$a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arg -WorkingDirectory $dir
# S4U: runs whether or not anybody is signed in, and stores no password. Priority 4 is NORMAL — the
# Task Scheduler default (7) is below-normal CPU, I/O and memory priority, and would make every
# timing test measure the scheduler instead of the product. The account comes from the token, not from
# $env:USERDOMAIN: a key-authenticated SSH session reports WORKGROUP there, and "WORKGROUP\hp" maps to
# no account at all (0x80070534).
$who = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$p = New-ScheduledTaskPrincipal -UserId $who -LogonType S4U -RunLevel Limited
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -Priority 4 `
       -ExecutionTimeLimit (New-TimeSpan -Hours 4) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $Task -Action $a -Principal $p -Settings $s -Force | Out-Null
Start-ScheduledTask -TaskName $Task
"started   : task $Task"
PS
    sleep 8
    echo "id        : $ID"
    remote "\$Id = '$ID'" <<'PS'
Show-Run (Resolve-Run $Id)
PS
    ;;

  status)
    check_id "${1:-}"
    remote "\$Id = '${1:-}'" <<'PS'
Show-Run (Resolve-Run $Id)
PS
    ;;

  wait)
    check_id "${1:-}"; MIN="${2:-9}"; [[ "$MIN" =~ ^[0-9]+$ ]] || { echo "minutes must be a number" >&2; exit 2; }
    remote "\$Id = '${1:-}'; \$Minutes = $MIN" <<'PS'
$dir = Resolve-Run $Id
$deadline = (Get-Date).AddMinutes($Minutes)
while ($true) {
  $v = Get-Verdict (Get-RunState $dir) $dir
  if ($v -notin 'running', 'queued') { break }
  if ((Get-Date) -ge $deadline) { break }
  Start-Sleep -Seconds 15
}
Show-Run $dir
if ($v -eq 'green') { exit 0 }
if ($v -in 'running', 'queued') { "TIMEOUT: still $v after $Minutes min - call wait again"; exit 3 }
exit 1
PS
    ;;

  log)
    check_id "${1:-}"; LINES="${2:-40}"; [[ "$LINES" =~ ^[0-9]+$ ]] || { echo "lines must be a number" >&2; exit 2; }
    remote "\$Id = '${1:-}'; \$Lines = $LINES" <<'PS'
$dir = Resolve-Run $Id
$l = Get-ChildItem $dir -Filter '*.log' | Sort-Object Name | Select-Object -Last 1
if (-not $l) { 'no step has started yet'; exit 0 }
"== $($l.Name), last $Lines lines"
Get-Content $l.FullName -Tail $Lines
$e = "$($l.FullName).err"
if ((Test-Path $e) -and (Get-Item $e).Length -gt 0) { "== stderr"; Get-Content $e -Tail $Lines }
PS
    ;;

  fetch)
    check_id "${1:-}"
    OUT="$(remote "\$Id = '${1:-}'" <<'PS'
$dir = Resolve-Run $Id
$b = Join-Path $dir 'bundle.tgz'
if (Test-Path $b) { Remove-Item $b -Force }
tar.exe -czf $b -C $dir --exclude=src --exclude=tmp --exclude=src.tgz --exclude=bundle.tgz . 2>&1 | Out-Null
"RUN=" + (Split-Path $dir -Leaf)
PS
)"
    RUN="$(printf '%s\n' "$OUT" | sed -n 's/^RUN=//p' | tr -d '\r')"
    [ -n "$RUN" ] || { printf '%s\n' "$OUT" >&2; exit 1; }
    DEST="${2:-${TMPDIR:-/tmp}/win-test/$RUN}"
    mkdir -p "$DEST"
    win_scp --get "C:/ta/runs/$RUN/bundle.tgz" "$DEST/bundle.tgz" >/dev/null
    tar -xzf "$DEST/bundle.tgz" -C "$DEST" && rm -f "$DEST/bundle.tgz"
    echo "fetched   : $RUN -> $DEST"
    ls "$DEST" "$DEST/trx" 2>/dev/null | sed 's/^/  /'
    ;;

  list)
    remote '' <<'PS'
$all = @(Get-ChildItem $Runs -Directory -EA SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 15)
if (-not $all.Count) { 'no runs yet'; exit 0 }
foreach ($d in $all) {
  $s = Get-RunState $d.FullName; $v = Get-Verdict $s $d.FullName
  $tot = 0; $fail = 0
  if ($s) { foreach ($t in @($s.tests)) { if ($t) { $tot += $t.total; $fail += $t.failed } } }
  "{0,-30} {1,-8} {2,6} tests {3,4} failed   {4}" -f $d.Name, $v, $tot, $fail, $(if ($s -and $s.filter) { $s.filter } else { '' })
}
PS
    ;;

  stop)
    check_id "${1:-}"
    remote "\$Id = '${1:-}'" <<'PS'
$dir = Resolve-Run $Id
$s = Get-RunState $dir
if ($s -and $s.phase -ne 'done') {
  # The whole tree: the runner, dotnet, and every testhost under it.
  & taskkill.exe /T /F /PID $s.pid 2>&1 | Out-Null
}
Stop-ScheduledTask -TaskName $Task -EA SilentlyContinue
& dotnet build-server shutdown 2>&1 | Out-Null
if ($s -and $s.phase -ne 'done') {
  $s.phase = 'done'; $s.verdict = 'stopped'; $s.finished = (Get-Date).ToString('o')
  ($s | ConvertTo-Json -Depth 6) | Set-Content -Path (Join-Path $dir 'status.json') -Encoding utf8
  Remove-Item -Recurse -Force -Path (Join-Path $dir 'tmp') -EA SilentlyContinue
}
Show-Run $dir
PS
    ;;

  *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
