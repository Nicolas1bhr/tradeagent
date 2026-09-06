# U-batch-2b — an unreadable vendor file fails visibly, a status row cannot outlive the truth, a sign-in URL is checked

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the no-terminal rule; vendor commands are data), the
`## Codex` block of `docs/REVIEW-2026-09-05b.md` (F16, F20) and its UNVERIFIED 4 and 6, and the `U-settings-closed`
section of `BUILD-STATUS.md` (the principle: an unreadable row is the most restrictive row). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/batch-2b`, branch `u-batch-2b` from `main`. Other builders work the
gateway, the Safety controls, the downloader and the material ledger; you touch none of them.

1. **A malformed `runtimes.json` or `atas.json` override fails visibly** (`RuntimeManifest.cs:411`; F16 = UNVERIFIED 6):
   today `RuntimeCatalog.Load` catches everything and returns the built-ins in silence. When an override file EXISTS
   and does not parse, no built-in is launched in its place: the agent does not start, a health row names the file in
   the owner's words, and the Checks page shows it. A genuinely absent override still means the built-ins. RED first
   (a restrictive override, corrupted, then agent start → today a built-in runs) → GREEN; mutant → RED.
2. **The bridge status row cannot outlive the truth** (`AtasHealth.cs:219`; F20): the reporter caches installation and
   version facts for a minute and invalidates only after an in-app reinstall, so an external removal or replacement
   reads stale. Invalidate on facts the probe reads cheaply (file presence, size, mtime) rather than on time, or
   shorten the cache and say in the row when it was last read. RED first → GREEN; mutant → RED.
3. **The sign-in URL is checked before `ShellExecute`** (`MainWindow.cs:739`; UNVERIFIED 4): `OpenPath` opens whatever
   `AuthUrlPattern` matched with `UseShellExecute = true` and no scheme check, and the pattern can be overridden from
   `runtimes.json`. Only `http`/`https` absolute URLs are opened; anything else is refused with a sentence in the app's
   window, never a console. RED first (an override pattern matching `file:` or a bare path) → GREEN; mutant → RED.

Yours: `RuntimeManifest.cs`, the health/Checks rendering for item 1, `AtasHealth.cs`, `MainWindow.cs` (`OpenPath`),
`docs/USER-GUIDE.md`, tests. Commit per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; each class 3×;
full suite once to a file (one suite at a time on this Mac; a `Timing` test that fails while another suite runs → that
class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant with the outputs; gate counts; what
you did NOT do. Verified or NOT VERIFIED.

Branch `u-batch-2b`, rebased onto `main` `37994bd`; code tip **`7280ca8`**, this report the commit on top. Three commits, one per item; every claim below is a quoted run.

1. **A malformed override fails visibly.** New `Core/VendorFile.cs` reads both files once: absent → the built-ins; present and unparseable (or empty) → the most restrictive value and one sentence. `RuntimeCatalog.Read/Require` then yield NO manifests, so `MainWindow`/`OnboardingView` start nothing; `AtasLayout.Read` yields empty candidate lists, so both ATAS rows, `RepairOffered` and `InstallBridge` refuse; `RuntimeFileHealth` writes the `Agent runtime` row every tick and hands it back to UNKNOWN when the file is corrected; `Doctor` adds a row per file. Two new error codes, because `AI_RUNTIME_NOT_FOUND` reads "not installed yet" and offers to install it — the wrong morning. **RED 4/4 on `main`:** `Assert.Null() Failure … Actual: RuntimeManifest { … AuthArgs = ["login"], AuthStateArgs = ["login", "status"] … }` — the built-in codex, which carries `--dangerously-bypass-approvals-and-sandbox`, returned in place of a restrictive override — and `Assert.Empty() Failure … Collection: ["%ProgramFiles(x86)\\ATAS Platform", "%ProgramFiles%\\ATAS X"]`. **GREEN 8/8.** **Mutant** (`VendorFile.Read`'s catch → `new(null, null)`, i.e. the old silent fallback) → **7 red**.
2. **The status row cannot outlive the truth.** `IAtasProbe.Stamp(detection)` — four directory entries: `atas.json`, both folders, the bridge assembly, and the platform exe the version was read from (now carried on `AtasDetection`). The reporter keeps its reading against that stamp and drops it the moment the entries disagree; the one-minute bound stays underneath for the case a stamp cannot see (an install appearing where the last pass found nothing to watch). **RED 2/2:** bridge deleted outside the app → `String: "installed, but the strategy is not starte"··· Not found: "not installed in ATAS"` (it sent the owner into ATAS to start a strategy that was no longer there); ATAS replaced → `String: "running · 8.0.14.397" Not found: "9.1.2.3"`. **GREEN.** **Mutant** (drop `|| _probe.Stamp(_cached) != _stamp`) → **3 red**, including `BridgeReinstallTests`'.
3. **The sign-in URL is checked before ShellExecute.** `MainWindow.RefusedToOpen` passes an absolute http/https URL, or a folder that exists inside `Paths.Home`; everything else is refused in the window with the target quoted back, bounded to 120 chars. The folder test compares text before touching the disk, so a target naming a share does not become a connection to it. `Open(target, explain, launch)` is the seam the tests drive, so no test opens a browser. **RED 4/4 on `main`:** three of the four gave `Assert.NotNull() Failure: Value is null` — `OpenPath` handed `ta-not-a-real-scheme://sign-in`, `\\evil-share\payload.exe` and a bare path straight to the shell and said nothing; the fourth returned the OS's own text. **GREEN 17/17** (now including `file:`, `javascript:`, `ms-settings:`, `ftp:`, and the five real sign-in/help/download URLs, which still open). **Mutant** (`if (false && RefusedToOpen(...))`) → **12 red**.

**Gate at `7280ca8`, Release.** `dotnet build TradeAgent.sln -c Release --no-incremental` → 17 projects, **0 Warning(s), 0 Error(s)**. Each touched class 3× (`VendorOverrideFileTests` 8, `OpenPathTests` 17, `AtasHealthTests` 16, `BridgeReinstallTests` 7, `RuntimeCatalogTests` 3, `DoctorReconciliationCheckTests` 9, `ErrorCatalogueTests` 1) — 21 runs, all Passed. Full suite once to `/tmp/u-batch-2b-gate.txt`, one project at a time, nothing else running: **Unit 280 + Fault 261 + Integration 587 = 1128, 0 failed**, exit 0 each. Test names vs `main`: **0 removed, 14 added**; the two tests whose meaning moved kept their names and their claims (`A_corrupt_override_file_does_not_stop_the_app` — the app still runs, it just offers nothing; `The_bridge_row_re_derives_after_a_reinstall_instead_of_waiting_out_the_cache` — now true of an ordinary tick as well as of `Forget()`). Three classes that read the two override files share one xUnit collection so a deliberate corruption cannot surface as an unexplained failure elsewhere. Secret scan of the staged diff before each commit: clean.

**NOT done, deliberately.** No Windows box, no real ATAS, no UI run: item 1's health row and Checks row are verified through `HealthRegistry` and `DoctorReport`, not photographed, and the `ChooseRuntime` screen's new refusal panel is **NOT VERIFIED on any screen**. `OpenAtasOrExplain` still starts ATAS through its own `Process.Start` and was left alone: its target is `InstallDir` + one of two hard-coded ATAS executable names, `File.Exists`-guarded, and item 1 now makes an unreadable `atas.json` yield no `InstallDir` at all — but it is not behind item 3's check, and a *readable* `atas.json` naming another folder still points it wherever the owner said. Item 1's health-row half could not be red-first (the reporter did not exist to fail); its Checks-page half was, and the mutant kills both. `TradingGateway.cs`, the Safety controls, the downloader and the material ledger: untouched. Nothing pushed, nothing merged, no other worktree entered.
