# One test run, ON the Windows machine. tools/win-test.sh copies this into C:\ta\runs\<id>\ beside the
# source tarball and starts it as a scheduled task; nothing here is meant to be run by hand.
#
# Why a scheduled task and not the SSH session: Win32-OpenSSH puts everything a session starts into
# one job and closes it when the connection drops, so a suite started over SSH dies with the Wi-Fi,
# the lid or the Bash tool's ten-minute ceiling — the suite takes longer than that on its own. A
# task belongs to Task Scheduler, not to anybody's connection.
#
# What it runs is the CI workflow's test job, step for step (.github/workflows/build.yml): restore,
# Release build, everything outside Category=Timing with no retry, then Category=Timing re-run once
# on a red — and, as there, the first timing failure stays in the record whatever the retry says.
# A run with a filter.txt runs that one filter instead, once, no retry.
#
# Everything it knows goes into status.json after every step, so a reader never has to guess
# whether a quiet run is working, finished or dead: `pid` is checked against the live process list.
param([Parameter(Mandatory = $true)][string]$RunDir)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

$src = Join-Path $RunDir 'src'
$statusFile = Join-Path $RunDir 'status.json'
$state = [ordered]@{
  phase = 'starting'; verdict = $null; started = (Get-Date).ToString('o'); updated = $null
  finished = $null; pid = $PID; filter = $null; steps = @(); tests = @(); failed = @(); timing_rescued = $false
  watched = $null; owner_files_changed = $null; backstop = $null
}
function Save {
  $state.updated = (Get-Date).ToString('o')
  # Write then rename, so a reader polling mid-write never parses half a file.
  $tmp = "$statusFile.tmp"
  ($state | ConvertTo-Json -Depth 6) | Set-Content -Path $tmp -Encoding utf8
  Move-Item -Path $tmp -Destination $statusFile -Force
}

# English tool output on a French Windows: logs are read from a Mac, and grep for "error" should work.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'; $env:VSLANG = '1033'
$env:DOTNET_NOLOGO = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
# Every test process leaves a scratch home under the temp directory and nothing deletes it (34 GB of
# them piled up on the Mac by 2026-10-04). A temp directory per run puts them where the run's own
# cleanup reaches.
$tmpDir = Join-Path $RunDir 'tmp'
New-Item -ItemType Directory -Force -Path $tmpDir, $src | Out-Null
$env:TMP = $tmpDir; $env:TEMP = $tmpDir

# THE MACHINE IS SOMEBODY'S, WITH HIS OWN TRADEAGENT AND ATAS ON IT (tools/README.md). Every test
# assembly already points TRADEAGENT_HOME and TRADEAGENT_PIPE at scratch values of its own (TestEnv);
# the ATAS bridge pipe it leaves at the product default, `TradeAgent.Bridge` — the name his own app
# hosts and his own ATAS bridge dials. No test opens it at the time of writing (five construct a
# connector on the default name and never connect), so this is a backstop, and the one deliberate
# difference from the CI job: a future test that does connect on the default meets a pipe of this run,
# not his. The home and the gateway pipe get the same backstop for an assembly without TestEnv.
$runName = Split-Path $RunDir -Leaf
$env:TRADEAGENT_HOME = Join-Path $tmpDir 'home'
$env:TRADEAGENT_PIPE = "ta-run-$runName-gateway"
$env:TRADEAGENT_BRIDGE_PIPE = "ta-run-$runName-bridge"
$state.backstop = [ordered]@{ TRADEAGENT_HOME = $env:TRADEAGENT_HOME; TRADEAGENT_PIPE = $env:TRADEAGENT_PIPE; TRADEAGENT_BRIDGE_PIPE = $env:TRADEAGENT_BRIDGE_PIPE }

# A tripwire, not a guard: what the run must never change, measured before and after. A change is
# reported, never repaired — it may be his own use of the machine during the run, and only he can say.
$watched = [ordered]@{
  'TradeAgent home'    = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TradeAgent'
  'TradeAgent install' = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\TradeAgent'
  'ATAS data'          = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ATAS'
}
function Fingerprint {
  $out = [ordered]@{}
  foreach ($k in $watched.Keys) {
    $w = $watched[$k]
    if (Test-Path $w) {
      $f = @(Get-ChildItem $w -Recurse -File -Force -EA SilentlyContinue)
      $newest = $f | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
      $out[$k] = "{0} files, {1} bytes, newest {2}" -f $f.Count, ($f | Measure-Object Length -Sum).Sum, $(if ($newest) { $newest.LastWriteTimeUtc.ToString('o') } else { '-' })
    } else { $out[$k] = 'absent' }
  }
  return $out
}
$before = Fingerprint
$state.watched = $before

# A laptop on battery idles to sleep after minutes, mid-suite, with nobody to notice. Asking Windows
# to stay awake for as long as this thread lives costs nothing and ends with the run. (A closed lid
# is a different, forced sleep; only the machine's own lid setting covers that.)
Add-Type -Namespace TradeAgent -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
[TradeAgent.Power]::SetThreadExecutionState([uint32]2147483649) | Out-Null   # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

$n = 0
function Step([string]$name, [string]$exe, [string]$arguments) {
  $script:n++
  $state.phase = $name; Save
  $out = Join-Path $RunDir ('{0:00}-{1}.log' -f $script:n, $name)
  $t = Get-Date
  $p = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $src -NoNewWindow -PassThru `
         -RedirectStandardOutput $out -RedirectStandardError "$out.err"
  $null = $p.Handle   # without this, ExitCode reads back empty once the process is gone
  $p.WaitForExit()
  $code = $p.ExitCode
  $state.steps += [ordered]@{ name = $name; exit = $code; seconds = [int]((Get-Date) - $t).TotalSeconds; log = (Split-Path $out -Leaf) }
  Save
  return $code
}

function Read-Trx {
  $trxDir = Join-Path $RunDir 'trx'
  New-Item -ItemType Directory -Force -Path $trxDir | Out-Null
  $state.tests = @(); $state.failed = @()
  Get-ChildItem -Path (Join-Path $src 'tests') -Recurse -Filter 'results*.trx' -EA SilentlyContinue | ForEach-Object {
    $project = $_.Directory.Parent.Name
    Copy-Item $_.FullName (Join-Path $trxDir "$project-$($_.Name)") -Force
    $x = [xml](Get-Content -Raw -Path $_.FullName)
    $c = $x.TestRun.ResultSummary.Counters
    $state.tests += [ordered]@{ file = "$project-$($_.Name)"; total = [int]$c.total; passed = [int]$c.passed; failed = [int]$c.failed }
    @($x.TestRun.Results.UnitTestResult) | Where-Object { $_ -and $_.outcome -eq 'Failed' } |
      ForEach-Object { $state.failed += "$($project): $($_.testName)" }
  }
}

try {
  $filterFile = Join-Path $RunDir 'filter.txt'
  if (Test-Path $filterFile) { $state.filter = (Get-Content -Raw $filterFile).Trim() }

  $ok = (Step 'extract' 'tar.exe' "-xzf `"$RunDir\src.tgz`" -C `"$src`"") -eq 0
  if ($ok) { $ok = (Step 'restore' 'dotnet' 'restore TradeAgent.sln') -eq 0 }
  # No build servers: one that outlives the run holds files in src\ and the next run cannot clean it.
  if ($ok) { $ok = (Step 'build' 'dotnet' 'build TradeAgent.sln -c Release --no-restore -nodeReuse:false -p:UseSharedCompilation=false') -eq 0 }

  if (-not $ok) {
    $state.verdict = 'red'
  } elseif ($state.filter) {
    $code = Step 'test' 'dotnet' "test TradeAgent.sln -c Release --no-build --filter `"$($state.filter)`" --logger `"trx;LogFileName=results.trx`""
    $state.verdict = $(if ($code -eq 0) { 'green' } else { 'red' })
  } else {
    $main = Step 'test' 'dotnet' 'test TradeAgent.sln -c Release --no-build --filter "Category!=Timing" --logger "trx;LogFileName=results.trx"'
    $timing = Step 'timing' 'dotnet' 'test TradeAgent.sln -c Release --no-build --filter "Category=Timing" --logger "trx;LogFileName=results-timing.trx"'
    if ($timing -ne 0) {
      $timing = Step 'timing-retry' 'dotnet' 'test TradeAgent.sln -c Release --no-build --filter "Category=Timing" --logger "trx;LogFileName=results-timing-retry.trx"'
      $state.timing_rescued = ($timing -eq 0)
    }
    $state.verdict = $(if ($main -eq 0 -and $timing -eq 0) { 'green' } else { 'red' })
  }
  Read-Trx
} catch {
  $state.verdict = 'error'
  $state.error = $_.ToString()
} finally {
  & dotnet build-server shutdown *> $null
  try {
    $after = Fingerprint
    $state.owner_files_changed = @($watched.Keys | Where-Object { $before[$_] -ne $after[$_] } | ForEach-Object { "$($_): $($before[$_]) -> $($after[$_])" })
  } catch { $state.owner_files_changed = @("the tripwire itself failed: $_") }
  Remove-Item -Recurse -Force -Path $tmpDir -EA SilentlyContinue
  $state.phase = 'done'
  $state.finished = (Get-Date).ToString('o')
  Save
}
