# U-cut-0.1.2 — cut v0.1.2 on the box, publish it, and watch the installed app update itself

Fresh builder on Opus, with the box GRANTED to you alone. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/DEPLOYMENT.md`
(§0–§5, §8: the walked steps of v0.1.1's cut), `docs/RESUME-HERE.md` (box facts, traps 24/35/40/42/43, step 6),
`tools/README.md`, `packaging/build.ps1` (its parameters and the summary block at `:310-319`), and the `## 2026-09-05` /
`## 2026-09-06` sections of `BUILD-STATUS.md` from `U-box-precut` on (what the box is running, what landed since).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/cut-0.1.2`, branch `u-cut-0.1.2` from `main`; the release targets
the FULL sha of `main`'s tip at the moment you cut, named in your report. Credentials are in `~/.tradeagent/win.env`;
never print them or a host name. Sim accounts only; never change mode, live activation, kill switch or settings; never
close ATAS unless capture works; stop TradeAgent before a push (trap 42); prove the pushed tree by hash.

1. **The version.** `Directory.Build.props` `<Version>` 0.1.1 → 0.1.2, one commit; the assembly, the installer
   (`/DAppVersion`) and the release tag read it from that one place — quote where each reads it.
2. **Build on the box, with the ATAS adapter PRESENT.** `packaging/build.ps1 -AtasInstallDir "C:\Program Files (x86)\ATAS
   Platform"` (the exact folder `atas.json` names) on the pushed tree; the summary block must read `ATAS adapter PRESENT`
   — read back out of the compiled assembly, not the switch; `TreatWarningsAsErrors` is on for the bridge, so 0 warnings.
   CI's artifact is `NO-ATAS-ADAPTER` and is never published.
3. **Hash before you trust.** `sha256` and byte count of `TradeAgent-Setup-x64.exe` on the box, on the Mac after copying
   it back, and in `SHA256SUMS.txt`: three equal readings before anything is published.
4. **Publish.** `gh release create v0.1.2 --target <FULL 40-char sha> artifacts\TradeAgent-Setup-x64.exe
   artifacts\SHA256SUMS.txt` with release notes in the owner's words: what changed for them since v0.1.1 (two-press for
   every grant, the bridge at protocol 3 with `Reinstall the bridge`, the emergency close confirmed on the real bridge,
   the unreadable-settings and unreadable-vendor-file behaviour, the inbox measurement) and what is NOT in it: the ATAS
   installer hash is not pinned (`installerSha256` null, the owner's decision pending). Then the asset's digest on
   GitHub equals the three readings: four.
5. **Watch the update install itself.** Stop the repo build on the box; start the INSTALLED 0.1.1 app
   (`%LOCALAPPDATA%\Programs\TradeAgent`); it must find v0.1.2, verify it against `SHA256SUMS.txt`, refuse to install over
   unconfirmed work if any, and relaunch as 0.1.2 — `trade status` → `app_version 0.1.2`, every health row READY, bridge
   `protocol 3`, the copy it downloaded hashing equal: five. Quote each sentence the app showed on the way. If the
   update refuses, quote why and stop: do not install by hand.
6. **Leave the box** with 0.1.2 installed and running, ATAS up with the bridge started, the book flat, and the repo
   build stopped. Do not delete the v0.1.1 release; do not touch v0.1.0.

Commit per item, no trailers. Gate here before the push: Release `--no-incremental` → 0 warnings; full suite once to a
file; names vs `main` 0 removed. Everything on the box is a measurement, quoted.

## Report — append here, commit it, ≤20 lines: the sha the release targets; the five hashes; the summary block's
adapter line; each sentence the updating app showed; `trade status` after; the box state left; what you did NOT do.
