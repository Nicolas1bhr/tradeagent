# One-time setup of a Windows machine the Mac drives over SSH: run it ONCE, on that machine, in an
# ADMINISTRATOR PowerShell, signed in as the account the Mac will use. It is the only step a person
# has to do by hand, which is why it is written down rather than described.
#
#   .\win-bootstrap.ps1 -PublicKey 'ssh-ed25519 AAAA... comment'
#
# The key is the Mac's PUBLIC key (~/.ssh/<name>.pub) — no password ever leaves the machine, and
# deleting one line from the authorized-keys file revokes the Mac completely. Re-running is harmless.
#
# What it changes, all of it reversible:
#   - OpenSSH Server installed, started, and set to start at boot
#   - port 22 open to Tailscale addresses (100.64.0.0/10) only; the stock any-address rule disabled
#   - the key appended to the authorized-keys file sshd actually reads for this account
#   - sleep and hibernate off on mains power, so a long test run is not cut off halfway
#   - C:\ta created, the harness's working area
param([Parameter(Mandatory = $true)][string]$PublicKey)
$ErrorActionPreference = 'Stop'

$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
if ($cap.State -ne 'Installed') { Add-WindowsCapability -Online -Name $cap.Name | Out-Null }
Set-Service sshd -StartupType Automatic
Start-Service sshd

Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Disable-NetFirewallRule
if (-not (Get-NetFirewallRule -Name 'TradeAgent-SSH-Tailscale' -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -Name 'TradeAgent-SSH-Tailscale' -DisplayName 'OpenSSH (Tailscale only)' `
    -Direction Inbound -Protocol TCP -LocalPort 22 -RemoteAddress 100.64.0.0/10 -Action Allow -Profile Any | Out-Null
}

# sshd reads an ADMINISTRATOR's keys from ProgramData, never from the profile, and ignores that file
# unless only Administrators and SYSTEM can write it. A key in ~\.ssh of an admin account is silently
# skipped and the login fails as if the key were wrong. whoami lists the Administrators SID for an
# admin account whether or not this window is elevated.
$isAdmin = [bool](whoami /groups | Select-String 'S-1-5-32-544')
if ($isAdmin) {
  $file = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
} else {
  $file = Join-Path $env:USERPROFILE '.ssh\authorized_keys'
  New-Item -ItemType Directory -Force -Path (Split-Path $file) | Out-Null
}
if (-not ((Test-Path $file) -and (Select-String -Path $file -SimpleMatch $PublicKey -Quiet))) {
  Add-Content -Path $file -Value $PublicKey -Encoding ascii
}
if ($isAdmin) { icacls $file /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null }

# C:\ta is the harness's working area on every machine (tools/README.md); long scripts land there.
New-Item -ItemType Directory -Force -Path 'C:\ta' | Out-Null

powercfg /change standby-timeout-ac 0
powercfg /change hibernate-timeout-ac 0

$ts = 'C:\Program Files\Tailscale\tailscale.exe'
''
'Done. Send these four lines back:'
"  user      : $env:USERNAME   (administrator: $isAdmin)"
"  machine   : $env:COMPUTERNAME"
"  tailscale : " + $(if (Test-Path $ts) { (& $ts ip -4) -join ' ' } else { 'tailscale.exe not found' })
"  sshd      : " + (Get-Service sshd).Status
