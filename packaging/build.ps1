<#
  Builds every shippable artifact and, when Inno Setup is present, the installer.

  Run from the repository root on Windows, with either shell:
      pwsh packaging/build.ps1
      powershell -ExecutionPolicy Bypass -File packaging/build.ps1

  Switches:
      -SkipTests         package without running the test suite (CI runs it in its own job)
      -SkipPublish       package the build already staged in artifacts/stage, without recompiling.
                         Rebuilds the installer in seconds while iterating on TradeAgent.iss.
                         The staged build is still verified before it is packaged.
      -RequireInstaller  a missing Inno Setup becomes an error instead of a warning
      -AtasInstallDir    path to a real ATAS install; compiles the bridge's ATAS adapter

  Notes that matter:
    - The desktop app and the CLI are published SELF-CONTAINED. A nontechnical user must never be
      asked to install a .NET runtime first.
    - The ATAS bridge is published separately and framework-dependent: it is loaded into the ATAS
      process, which already supplies a runtime.
    - The bridge's ATAS-facing adapter is only compiled when -AtasInstallDir points at a real ATAS
      install. Without it you still get a bridge assembly containing the tested protocol half and no
      ATAS adapter, which is deliberate: the build never pretends to have ATAS support it cannot have.
    - TradeAgent.Provisioning is NOT published on its own. It reaches the stage through the
      application's reference graph (App -> AgentRuntime -> Provisioning) and the verification step
      below asserts its assembly actually arrived, rather than assuming it did.
    - Every step that can fail is checked. A run that produces no installer, or an installer built
      from an empty stage, exits non-zero and says which file is missing. It never prints "Done".
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = 'artifacts',
    [string]$AtasInstallDir = '',
    [switch]$SkipTests,
    [switch]$SkipPublish,
    [switch]$RequireInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$stage = Join-Path $OutputDir 'stage'
$bridgeDir = Join-Path $stage 'bridge'

# ---------------------------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------------------------

# A failing native command does not stop a PowerShell script on its own in Windows PowerShell 5.1,
# and behaviour differs again across PowerShell 7 releases. So every dotnet call is checked here,
# in one place. This is the difference between "publish failed" and "publish failed, and the script
# went on to build an installer around an empty directory".
function Invoke-Dotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ')  failed with exit code $LASTEXITCODE"
    }
}

function Format-Size {
    param([Parameter(Mandatory = $true)][double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KB' -f ($Bytes / 1KB)) }
    return "$([int]$Bytes) bytes"
}

# The version is declared once, in Directory.Build.props, and handed to Inno Setup from there so the
# installer cannot drift away from the assemblies inside it.
$version = '0.1.0'
$propsFile = Join-Path $root 'Directory.Build.props'
if (Test-Path $propsFile) {
    $props = Get-Content $propsFile -Raw
    if ($props -match '<Version>([^<]+)</Version>') { $version = $Matches[1].Trim() }
}

$wantAtas = [bool]($AtasInstallDir -and (Test-Path $AtasInstallDir))

# ---------------------------------------------------------------------------------------------
# Compile and stage
# ---------------------------------------------------------------------------------------------

if ($SkipPublish) {
    if (-not (Test-Path $stage)) {
        throw "-SkipPublish was given, but there is no staged build at $stage. Run once without -SkipPublish first."
    }
    Write-Host "== skipping publish; packaging the existing stage in $stage =="
    # Clear the previous installer and checksum file so a failed run cannot leave yesterday's
    # TradeAgent-Setup-x64.exe sitting next to today's checksums.
    Get-ChildItem $OutputDir -File |
        Where-Object { $_.Extension -in '.exe', '.msi', '.txt' } |
        Remove-Item -Force
} else {
    if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null

    Write-Host '== restore =='
    Invoke-Dotnet @('restore', 'TradeAgent.sln')

    if (-not $SkipTests) {
        Write-Host '== tests =='
        # Green tests are a release precondition, not a report.
        Invoke-Dotnet @('test', 'TradeAgent.sln', '-c', $Configuration, '--nologo')
    }

    Write-Host '== publish desktop app (self-contained) =='
    # This one publish also brings in Gateway, Connectors, AgentRuntime, Provisioning, Diagnostics
    # and Security, because the app references them. They are verified below by name.
    Invoke-Dotnet @(
        'publish', 'src/TradeAgent.App/TradeAgent.App.csproj', '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true', '-p:PublishSingleFile=false', '-p:PublishReadyToRun=true', '-o', $stage)

    Write-Host '== publish trade CLI (self-contained, single file) =='
    $cliTemp = Join-Path $OutputDir 'cli'
    Invoke-Dotnet @(
        'publish', 'src/TradeAgent.TradeCli/TradeAgent.TradeCli.csproj', '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishReadyToRun=true', '-o', $cliTemp)
    Copy-Item (Join-Path $cliTemp 'trade.exe') $stage -Force
    # Removed once copied: a leftover intermediate would otherwise be hashed into SHA256SUMS.txt and
    # uploaded as if it were a shipping file.
    Remove-Item $cliTemp -Recurse -Force

    Write-Host '== publish headless gateway (diagnostics and support) =='
    $gwTemp = Join-Path $OutputDir 'gateway'
    Invoke-Dotnet @(
        'publish', 'src/TradeAgent.GatewayHost/TradeAgent.GatewayHost.csproj', '-c', $Configuration, '-r', $Runtime,
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-o', $gwTemp)
    Copy-Item (Join-Path $gwTemp 'tradeagent-gateway.exe') $stage -Force
    Remove-Item $gwTemp -Recurse -Force

    Write-Host '== publish ATAS bridge =='
    $bridgeArgs = @('publish', 'src/TradeAgent.AtasBridge/TradeAgent.AtasBridge.csproj', '-c', $Configuration, '-o', $bridgeDir)
    if ($wantAtas) {
        Write-Host "   including the ATAS adapter, referencing $AtasInstallDir"
        $bridgeArgs += @('-p:AtasBridgeBuild=true', "-p:AtasInstallDir=$AtasInstallDir")
    } else {
        Write-Warning 'ATAS not supplied: publishing the bridge WITHOUT its ATAS adapter.'
        Write-Warning 'The resulting build cannot trade through ATAS. Pass -AtasInstallDir to include it.'
    }
    Invoke-Dotnet $bridgeArgs
}

# ---------------------------------------------------------------------------------------------
# Verify the stage before anything is packaged around it
#
# Without this the script reached the checksum step and printed "Done" after a publish that produced
# nothing usable. Each artifact is named individually so a failure says which piece is missing
# rather than "packaging failed". The sizes are floors, not exact expectations: they exist to catch
# a stub, a wrapper, or a framework-dependent publish standing in for the real thing.
# ---------------------------------------------------------------------------------------------

Write-Host '== verify the staged build =='

$expected = @(
    [pscustomobject]@{ Path = 'TradeAgent.exe';                   Min = 50KB; What = 'the desktop application' }
    [pscustomobject]@{ Path = 'trade.exe';                        Min = 1MB;  What = 'the CLI the AI calls; self-contained single file' }
    [pscustomobject]@{ Path = 'tradeagent-gateway.exe';           Min = 1MB;  What = 'the headless gateway used by diagnostics' }
    [pscustomobject]@{ Path = 'TradeAgent.Provisioning.dll';      Min = 4KB;  What = 'installs Node and the AI tool per-user, with no terminal' }
    [pscustomobject]@{ Path = 'TradeAgent.AgentRuntime.dll';      Min = 4KB;  What = 'runs the AI and hosts the in-app conversation' }
    [pscustomobject]@{ Path = 'TradeAgent.Gateway.dll';           Min = 4KB;  What = 'the execution authority: risk, idempotency, reconciliation' }
    # Forward slash on purpose: Windows accepts it, and it keeps this table checkable on the
    # macOS/Linux dev hosts, where a backslash is an ordinary filename character.
    [pscustomobject]@{ Path = 'bridge/TradeAgent.AtasBridge.dll'; Min = 8KB;  What = 'the assembly that is loaded into ATAS' }
)

$problems = @()
$sizes = @{}

foreach ($e in $expected) {
    $full = Join-Path $stage $e.Path
    if (-not (Test-Path $full)) {
        $problems += "MISSING     $($e.Path)  -  $($e.What)"
        continue
    }
    $len = (Get-Item $full).Length
    $sizes[$e.Path] = $len
    if ($len -lt $e.Min) {
        $problems += "TOO SMALL   $($e.Path)  is $len bytes, expected at least $($e.Min)  -  $($e.What)"
    }
}

$bridgeFiles = @()
if (Test-Path $bridgeDir) { $bridgeFiles = @(Get-ChildItem $bridgeDir -File -Recurse) }
if ($bridgeFiles.Count -eq 0) {
    $problems += "EMPTY       bridge/  -  nothing to install into ATAS; the bridge publish produced no files"
}

$stageFiles = @(Get-ChildItem $stage -File -Recurse)
$stageBytes = 0
if ($stageFiles.Count -gt 0) { $stageBytes = ($stageFiles | Measure-Object -Property Length -Sum).Sum }
if ($stageBytes -lt 30MB) {
    $problems += ("TOO SMALL   the whole stage is $(Format-Size $stageBytes). A self-contained $Runtime publish " +
                  'is far larger than that, so this was most likely published framework-dependent, which would ' +
                  'make the user install a .NET runtime by hand.')
}

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($p in $problems) { Write-Host "   $p" -ForegroundColor Red }
    Write-Host ''
    throw "the staged build in $stage is incomplete: $($problems.Count) problem(s) listed above. Nothing was packaged."
}

Write-Host "   $($stageFiles.Count) files, $(Format-Size $stageBytes) - every expected artifact present"

# Read the ATAS adapter out of the binary rather than trusting the switch that was passed in. The
# type name lives in the assembly's metadata as ASCII; C# string literals are stored as UTF-16 and
# comments never reach the file, so there is nothing else in this assembly for it to match.
$bridgeDll = Join-Path $bridgeDir 'TradeAgent.AtasBridge.dll'
$adapterCompiledIn = $false
if (Test-Path $bridgeDll) {
    # Resolve-Path first: .NET's own file APIs resolve a relative path against the process working
    # directory, which PowerShell's Set-Location does not change. Passing $bridgeDll straight in
    # reads from wherever the shell happened to be started.
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $bridgeDll).Path)
    $adapterCompiledIn = [System.Text.Encoding]::ASCII.GetString($bytes).Contains('AtasStrategyAdapter')
    $bytes = $null
}

if ($wantAtas -and -not $adapterCompiledIn) {
    throw ("-AtasInstallDir was given, but AtasStrategyAdapter is not in $bridgeDll. " +
           'This build cannot trade through ATAS and must not be labelled as if it can.')
}
if (-not $wantAtas -and $adapterCompiledIn) {
    Write-Warning "AtasStrategyAdapter is present in $bridgeDll although no -AtasInstallDir was given."
    Write-Warning 'That means a stale bridge was reused. Rerun without -SkipPublish before shipping this.'
}

# ---------------------------------------------------------------------------------------------
# Installer
# ---------------------------------------------------------------------------------------------

Write-Host '== installer =='
# Inno Setup installs per-machine or per-user depending on how it was obtained; winget's default
# lands it under LOCALAPPDATA, where the two Program Files paths never find it.
#
# Inno Setup 5 is deliberately not searched. TradeAgent.iss uses x64compatible and WizardStyle,
# neither of which 5 can compile, so finding a 5 install would turn "install Inno Setup 6" into an
# unexplained syntax error.
$iscc = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }

$isccVersion = 'not found'
if ($iscc) {
    $vi = (Get-Item $iscc).VersionInfo
    if ($vi.FileVersion) { $isccVersion = $vi.FileVersion }
    if ($vi.FileMajorPart -gt 0 -and $vi.FileMajorPart -lt 6) {
        throw ("found Inno Setup $($vi.FileVersion) at $iscc, but packaging/TradeAgent.iss needs 6.3 or newer " +
               '(it uses ArchitecturesAllowed=x64compatible). https://jrsoftware.org/isdl.php')
    }
}

$installer = Join-Path $OutputDir 'TradeAgent-Setup-x64.exe'
$installerBuilt = $false

if ($iscc) {
    Write-Host "   $iscc  (version $isccVersion)"
    & $iscc `
        "/DStageDir=$((Resolve-Path $stage).Path)" `
        "/DOutDir=$((Resolve-Path $OutputDir).Path)" `
        "/DAppVersion=$version" `
        packaging/TradeAgent.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    # Inno Setup has been known to report success while writing somewhere unexpected. Prove the file.
    if (-not (Test-Path $installer)) {
        throw "Inno Setup exited 0 but $installer does not exist. Check OutputBaseFilename and OutputDir in packaging/TradeAgent.iss."
    }
    $installerBuilt = $true
} elseif ($RequireInstaller) {
    throw 'Inno Setup 6 not found and -RequireInstaller was given. https://jrsoftware.org/isdl.php'
} else {
    Write-Warning 'Inno Setup 6 not found; skipping installer. https://jrsoftware.org/isdl.php'
    Write-Warning 'THIS RUN PRODUCED NO TradeAgent-Setup-x64.exe. Pass -RequireInstaller to make that an error.'
}

# ---------------------------------------------------------------------------------------------
# Checksums
# ---------------------------------------------------------------------------------------------

Write-Host '== checksums =='
# Checksums cover exactly the files that ship, so "the artifact tested" can be proven later. The
# publish intermediates were deleted above, so nothing here is a copy of something else.
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
Get-ChildItem $OutputDir -File -Recurse |
    Where-Object { $_.Extension -in '.exe', '.msi' } |
    ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        "$h  $($_.FullName.Substring($root.Length + 1) -replace '\\','/')"
    } | Set-Content $sums -Encoding ascii

# ---------------------------------------------------------------------------------------------
# Manifest: say what this artifact actually is, measured, not assumed
#
# An artifact is only as trustworthy as the last measurement of it. Reporting the size of a wrapper
# instead of the thing it wraps is how a build convinces someone of a decision that is not true.
# ---------------------------------------------------------------------------------------------

$bridgeBytes = 0
if ($bridgeFiles.Count -gt 0) { $bridgeBytes = ($bridgeFiles | Measure-Object -Property Length -Sum).Sum }

Write-Host ''
Write-Host '== what this build actually contains =='
Write-Host "   version           $version"
Write-Host "   configuration     $Configuration  /  $Runtime"
Write-Host "   staged files      $($stageFiles.Count) files, $(Format-Size $stageBytes)"
Write-Host "   bridge/           $($bridgeFiles.Count) files, $(Format-Size $bridgeBytes)"

if ($adapterCompiledIn) {
    Write-Host '   ATAS adapter      PRESENT - AtasStrategyAdapter is compiled into the bridge assembly' -ForegroundColor Green
} else {
    Write-Host '   ATAS adapter      ABSENT  - this build CANNOT trade through ATAS' -ForegroundColor Yellow
    Write-Host '                     Label the artifact accordingly and rebuild with -AtasInstallDir to include it.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '   key files:'
foreach ($e in $expected) {
    $shown = $e.Path -replace '\\', '/'
    $len = 0
    if ($sizes.ContainsKey($e.Path)) { $len = $sizes[$e.Path] }
    Write-Host ("      {0,-38} {1}" -f $shown, (Format-Size $len))
}

Write-Host ''
if ($installerBuilt) {
    $installerBytes = (Get-Item $installer).Length
    Write-Host "   installer         $installer  ($(Format-Size $installerBytes))" -ForegroundColor Green
} else {
    Write-Host '   installer         NONE - this run produced no TradeAgent-Setup-x64.exe' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '== checksums =='
Get-Content $sums

Write-Host ''
if ($installerBuilt) {
    Write-Host "Done. Artifacts in $OutputDir"
} else {
    Write-Host "Finished WITHOUT an installer. Artifacts in $OutputDir" -ForegroundColor Yellow
}
