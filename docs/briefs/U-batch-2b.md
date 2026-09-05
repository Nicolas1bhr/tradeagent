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
