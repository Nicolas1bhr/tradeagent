<#
  Builds every shippable artifact and, when Inno Setup is present, the installer.

  Run from the repository root on Windows:
      pwsh packaging/build.ps1

  Notes that matter:
    - The desktop app and the CLI are published SELF-CONTAINED. A nontechnical user must never be
      asked to install a .NET runtime first.
    - The ATAS bridge is published separately and framework-dependent: it is loaded into the ATAS
      process, which already supplies a runtime.
    - The bridge's ATAS-facing adapter is only compiled when -AtasInstallDir points at a real ATAS
      install. Without it you still get a bridge assembly containing the tested protocol half and no
      ATAS adapter, which is deliberate: the build never pretends to have ATAS support it cannot have.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = 'artifacts',
    [string]$AtasInstallDir = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$stage = Join-Path $OutputDir 'stage'
if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host '== restore =='
dotnet restore TradeAgent.sln

if (-not $SkipTests) {
    Write-Host '== tests =='
    # Green tests are a release precondition, not a report.
    dotnet test TradeAgent.sln -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed; refusing to package' }
}

Write-Host '== publish desktop app (self-contained) =='
dotnet publish src/TradeAgent.App/TradeAgent.App.csproj -c $Configuration -r $Runtime `
    --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -o $stage

Write-Host '== publish trade CLI (self-contained, single file) =='
$cliTemp = Join-Path $OutputDir 'cli'
dotnet publish src/TradeAgent.TradeCli/TradeAgent.TradeCli.csproj -c $Configuration -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o $cliTemp
Copy-Item (Join-Path $cliTemp 'trade.exe') $stage -Force

Write-Host '== publish headless gateway (diagnostics and support) =='
$gwTemp = Join-Path $OutputDir 'gateway'
dotnet publish src/TradeAgent.GatewayHost/TradeAgent.GatewayHost.csproj -c $Configuration -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -o $gwTemp
Copy-Item (Join-Path $gwTemp 'tradeagent-gateway.exe') $stage -Force

Write-Host '== publish ATAS bridge =='
$bridgeOut = Join-Path $stage 'bridge'
$bridgeArgs = @('publish','src/TradeAgent.AtasBridge/TradeAgent.AtasBridge.csproj','-c',$Configuration,'-o',$bridgeOut)
if ($AtasInstallDir -and (Test-Path $AtasInstallDir)) {
    Write-Host "   including the ATAS adapter, referencing $AtasInstallDir"
    $bridgeArgs += @('-p:AtasBridgeBuild=true', "-p:AtasInstallDir=$AtasInstallDir")
} else {
    Write-Warning 'ATAS not supplied: publishing the bridge WITHOUT its ATAS adapter.'
    Write-Warning 'The resulting build cannot trade through ATAS. Pass -AtasInstallDir to include it.'
}
dotnet @bridgeArgs

Write-Host '== installer =='
$iscc = @(
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc "/DStageDir=$((Resolve-Path $stage).Path)" "/DOutDir=$((Resolve-Path $OutputDir).Path)" packaging/TradeAgent.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
} else {
    Write-Warning 'Inno Setup 6 not found; skipping installer. https://jrsoftware.org/isdl.php'
}

Write-Host '== checksums =='
# Checksums cover exactly the files that ship, so "the artifact tested" can be proven later.
$sums = Join-Path $OutputDir 'SHA256SUMS.txt'
Get-ChildItem $OutputDir -File -Recurse |
    Where-Object { $_.Extension -in '.exe', '.msi' } |
    ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        "$h  $($_.FullName.Substring($root.Length + 1) -replace '\\','/')"
    } | Set-Content $sums -Encoding ascii

Get-Content $sums
Write-Host "`nDone. Artifacts in $OutputDir"
