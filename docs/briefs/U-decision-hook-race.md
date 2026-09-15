# U-decision-hook-race — a unit test that swaps a process-wide hook reads another class's line through it
Read `docs/HOW-WE-BUILD.md` (step 6: a red thrown by a test's own setup is a fresh fixer on top of `main`; "The fresh-fixer rule"), `CLAUDE.md`, the
`## 2026-09-15 — U-flatten-1 landed` section of `BUILD-STATUS.md` (the two earlier sightings, named there as "the loopback class, watched"), then
`tests/TradeAgent.UnitTests/DownloadPartBindingTests.cs:194-212` (the test: `Downloader.RecordDecision` swapped for a list, `Assert.Single(recorded)` at
`:208`), `src/TradeAgent.Core/…` wherever `Downloader.RecordDecision` is declared (a static, process-wide delegate), every OTHER test that assigns it or
installs a file with `Integrity.Unverified` (the dataset installers: grep `RecordDecision`, `Unverified(`, `.csv` across `tests/`), and the xunit
parallelisation settings of the Unit project (`xunit.runner.json`, `[Collection]`, `[CollectionDefinition]`). Branch `u-decision-hook-race`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-decision-hook-race`, rebased onto `main` first. Test-only unless the product is wrong; no
assertion loosened; no test deleted; no box, no ATAS, no money.

**The red, three sightings, one message.** (1) Builder's DEBUG run of `U-flatten-1` (recorded in the landing section); (2) the manager's Release gate at
`ec0f443` on 2026-09-14 evening, Unit 1077/1078: `DownloadPartBindingTests … Assert.Single(): 2 items`; (3) CI run 34944735920 at `44f33a4` — a DOCS-ONLY
sha — macos-latest, step "Test everything outside the timing category", Unit 1076/1077:
```
TradeAgent.Tests.Unit.DownloadPartBindingTests.An_install_with_no_checksum_writes_the_decision_and_the_reason
Assert.Single() Failure: The collection contained 2 items
Collection: ["Installed BTC-USD-5m-2026-06-09_2026-09-06.csv wit"…, "Installed ATASPlatform.exe without checking it aga"…]
   at …/tests/TradeAgent.UnitTests/DownloadPartBindingTests.cs:line 208
```
The first line is a DATASET install nobody in this test asked for: another test class, running in parallel, installed a checksum-less `.csv` while this
test's list was the hook. ubuntu and windows were green on the same sha. Never twice in a row.

1. **Judge it first:** name the test (class and method) that produced the `BTC-USD-5m-…csv` line, and reproduce on this Mac by running the Unit project
   with the two classes forced to overlap (a loop of 10 full Unit runs in Release, or the two classes under `--parallel` with a delay injected in a
   throwaway copy) — quote the reproduction. If it will not reproduce, say so and quote what you ran.
2. **Then fix once, structurally:** a process-wide static hook that tests swap is the root cause, and two findings with one root cause get one fix. Prefer
   the smallest change that removes the sharing — the hook scoped per call (an `Action<string>` on the call or an options object the caller passes, the
   static kept only as the app's default) — over serialising the classes with a `[Collection]`. If serialising is all that is honest for the product as
   built, argue why at the test in one sentence. Sweep `tests/` for every other test that assigns a static on `Downloader` or on any other product type
   and swaps it back in `finally`: name each, fix the same way or say why it is not at risk. The assertion stays `Assert.Single` on THIS test's own
   decisions; it is never widened to "contains".
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; the Unit project 5×
→ 0 failed; names vs `main` → nothing removed. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the
producing test named, the reproduction quoted (or the honest failure to reproduce), what moved and what did not, what you did NOT do.

## Report
Gated tip `aaf9a20` (this report commits on top), rebased onto `main` at `25f79b7`. The product gains ONE optional parameter and no behaviour: `recordDecision` defaults to null, and null means `Downloader.RecordDecision` exactly as before.
**Gate, Release, on the rebased tip:** build `--no-incremental` → 0 warnings, 0 errors; Unit 1098/1098, Fault 304/304, Integration 668 passed + 1 skipped of 669, each `--no-build` to a file → 0 failed; Unit 5× → 0 failed (1098 each time); names vs `main` → 0 removed, 1 added (1756 → 1757); scan clean before each commit; no trailers.
1. **Producer named, red reproduced on this Mac.** `CandleSourceTests.A_source_that_publishes_no_checksum_records_no_published_hash_and_says_why` — the ONLY other test that swaps the static. It installs `BTC-USD-5m-2026-06-09_2026-09-06.csv` (`Symbol` `BTC-USD`, 5m/90d, `Now` 2026-09-07) and whichever class assigned the hook second owns both classes' decisions. The two classes alone, Release, 10 runs → **4 red**:
```
Assert.Single() Failure: The collection contained 2 items
Collection: ["Installed ATASPlatform.exe without checking it aga"···, "Installed BTC-USD-5m-2026-06-09_2026-09-06.csv wit"···]
   at …/tests/TradeAgent.UnitTests/DownloadPartBindingTests.cs:line 208
```
The two TESTS alone, 10 runs → **10 red the other way**: `Assert.Contains() Failure: Filter not matched in collection` / `Collection: []` at `CandleSourceTests.cs:line 298` — CandleSourceTests' own line had gone into DownloadPartBindingTests' list.
2. **Scoped per call, not serialised.** `Action<string>? recordDecision` on `Downloader.DownloadAsync`, `CandleSourceClient.FetchPeriodAsync`/`CollectAsync` and both `MarketDataService.CollectAsync`; the static is now the app's default only (`AppHost.cs:520` untouched). Both tests pass their own sink; `Assert.Single(recorded)` unchanged; NO `[Collection]` added. The same 10 + 10 runs after the fix → 0 red. New `[Fact] No_test_assigns_the_process_wide_decision_hook` scans `tests/` through `SuiteReachesNoVendorTests.TestSources()`; mutant — the swap put back at `CandleSourceTests.cs:288` → RED: "…pass `recordDecision:` on the call instead: CandleSourceTests.cs:288".
**Sweep — every other `finally`-restored shared state in `tests/`; none at risk, none changed:** `CoreTests.cs:371-381` `CultureInfo.CurrentCulture` (per async flow since .NET Core 2.0, not process-wide); `CoreTests.cs:626-644` the `RuntimeCatalog` override FILE (already under `[Collection(VendorOverrideFiles.Name)]`); `CoreTests.cs:730-739` `PathEntry` PATH (the suite's only PATH writer and its own class runs serially); `AgentEnvironmentTests.cs:26-36,59-70` two `TA_TEST_*` names nothing else reads; `PressAtomicityTests.cs:105` `Pressing` is an `AsyncLocal` declared in the test.
**NOT done, NOT verified:** no `[Collection]`; no assertion loosened or widened; no test deleted; no product behaviour changed and so no RED-first product test; no Windows box, no CI run, no ATAS, no money.
