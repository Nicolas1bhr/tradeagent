# U3 — the update path, proven end to end on real Windows (2026-09-02) — D2

Reconstructed 2026-09-03 from the machine leg's final report. The 341-line command log and screenshots `U3-04..14`
were lost with the session scratchpad; every figure below was quoted in that report from a command that ran.

## Pre-state

The box ran the repo Release build (0.1.0+16d4862). The per-user install under `%LOCALAPPDATA%\Programs\TradeAgent` was
an installer TEST build from 2026-08-27 (pre schema-2). The leg first installed the PUBLISHED v0.1.0 from GitHub on the
box: download 117,641,311 B, sha256 `f243bdcc5906e99a2e43dbe0ac517780a6597a71d0d3913871433e909fcc0a1b` == manifest, no
Zone.Identifier (no SmartScreen), ran silently in session 1 via the UI agent with `/SILENT /NORESTART /SUPPRESSMSGBOXES
/relaunch=1 /LOG`; Setup's own window only; `/relaunch=1` started the installed app (pid 7552 from `Programs\TradeAgent`,
ProductVersion `0.1.0+16d4862…`, TradeAgent.exe sha256 `20db5d5f…b51e` == manifest `artifacts/stage/TradeAgent.exe`);
`trade status` → app_version 0.1.0, every ATAS row READY, `open_requests 1` (the legacy stranded record).

## Building and publishing v0.1.1

The first release build on the box failed its test stage on ONE test that read the real machine
(`AtasHealthTests.The_reporter_asks_the_platform_afresh_but_not_the_filesystem`: expected FAILED, got READY because ATAS
is installed and running there). Fixed on the Mac by putting the platform probe behind `IAtasProbe` (main `3931c10`;
production default byte-identical, repro executed both ways, 4 mutants). Tree re-pushed; identity proven on the box by
`Directory.Build.props` `<Version>0.1.1</Version>` and `IAtasProbe` present in `AtasHealth.cs` (128 files on both
machines). Windows test run for 3931c10: **45 / 108 / 146 green = 299**, the previously failing test passing on the ATAS
machine. Manifest: `version 0.1.1`, `ATAS adapter PRESENT - AtasStrategyAdapter is compiled into the bridge assembly`,
installer `artifacts\TradeAgent-Setup-x64.exe (112.2 MB)`, EXITCODE 0. Installer sha256 `9b238179…a668` identical on the
box (PowerShell), the Mac (`shasum`), `SHA256SUMS.txt`, the GitHub asset digest, and the copy the app later downloaded;
size 117,649,031 in all five. `gh release create v0.1.1 --target 3931c1015d7f393311c03544d6f281f04f945a2e` (a SHORT sha
is rejected with HTTP 422) — draft false, prerelease false, both assets uploaded, `releases/latest` → v0.1.1.

## The update, watched

The running 0.1.0 app's Settings card went from `0.1.0 — you have the newest one` to `Newest published version 0.1.1`;
banner "TradeAgent 0.1.1 is available · 112.2 MB. You are running 0.1.0."; two-step arming (`Confirm: close TradeAgent
and install 0.1.1`). A 1 Hz process log: old pid 7552 last seen **23:13:32.168**, Setup pids 17868/18052 **23:13:32 →
23:13:41**, new TradeAgent pid 9840 started **23:13:42**. Screens: one window, "Setup - TradeAgent version 0.1.1",
"Extracting files… libSkiaSharp.dll", progress bar — no console, no UAC, no SmartScreen, no error box. ATAS pid 24104
present in every one of 2312 samples, bridge still `[Started]`. Installed TradeAgent.exe sha256 `6f7430d1…c634` ==
manifest `artifacts/stage/TradeAgent.exe`; HKCU uninstall `TradeAgent 0.1.1`. Post-update `trade status`: app_version
0.1.1, **ATAS bridge READY "connected · bridge 8.0.14, protocol 2" at 23:13:48.5** (~6 s after relaunch, ~21 s after the
press), every ATAS row READY, `open_requests 1`, unreconciled 0 — the database survived. Activity page: `23:13 TradeAgent
started` above the intact 2026-08-28 order history. Settings card afterwards: `This version 0.1.1 / 0.1.1 — you have the
newest one / Last checked 2 September, 23:13.`

## Deviations and harness facts

`--target` needs a branch or a FULL sha. `Start-Process`-based detach does not survive the SSH session (Windows OpenSSH
kills the session's job object) — launch long builds with `Invoke-CimMethod Win32_Process Create` on a `.cmd`, then poll
the log. `scp` with two remote sources copies only the first. After an app restart re-read the window rect before
clicking (it moved to 208,208; scroll coordinates missed silently). `ProductVersion` carries a stale `+16d4862` sha
because `C:\ta\repo` has no `.git`; the updater compares the 3-part AppVersion, which is correct.

## NOT VERIFIED

Code signing (none exists) and SmartScreen on a browser-downloaded copy; machine-wide install/elevation; update while the
AI is running; any order round trip after the update; the banner's Install button and "What's new"; downgrade, rollback,
an interrupted install; update with ATAS not running; no Setup log exists for the app-driven install (the app passes no
`/LOG` and `TradeAgent.iss` has no `SetupLogging`).
