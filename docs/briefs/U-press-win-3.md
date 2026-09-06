# U-press-win-3 — the operator-press emergency tests went red on windows-latest in both Timing attempts

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md` (step 6: the `Timing` category, one second attempt, "a red twice in a
row is still a red run", an assertion is never loosened to get in), the `U-press-stopwatch` section of
`BUILD-STATUS.md` (how the class joined `Timing` and what the ubuntu overrun turned out to be: the runner's disk inside
one post-deadline settle), the `U-press-budget` and `U-press-inflight` sections, and
`tests/TradeAgent.FaultTests/EmergencyPressTests.cs` (`OperatorPressIsAnEmergencyTests`, line 316). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/press3`, branch `u-press-win-3` from `main`. Test-only unless the
product is the cause; another builder works `U-prices` (`TurnMeter.cs`, `Settings`, the Safety page, `costs.json`) and
you touch none of its files.

**The red:** PR #11's second run at the same sha, 34043185411, windows-latest, the `Timing` step red in BOTH attempts:
`close-all returned 2813 ms after the deadline the press itself opened`, then the cancel-all sibling. Its first run
34042541589 and a third, 34044573112, were green on every runner. The class is on this record three times already
(`the press took 3.4s against a 2s emergency budget` on ubuntu, `U-press-stopwatch`'s finding). A windows runner that
overruns a press deadline by 2.8 s twice in a row is either a fixture asserting a schedule the runner cannot keep, or a
product wait that the press must not contain. Which one is the question.

1. Measure before changing anything: a draft PR whose test prints where the 2.8 s went (the stopwatch pattern
   `U-press-stopwatch` used), three runs on all three runners; quote the numbers. Then: fix the fixture, or — only if
   the product waits past the deadline the press opened — fix the product with a red-first test and one mutant quoted.
   No assertion is loosened; nothing leaves `Timing` or joins it without measured numbers.
2. Gate: Release `--no-incremental` → 0 warnings; the class 3×; the full suite once to a file, one project at a time,
   nothing else running; names vs `main` 0 removed; the draft PR green on all three runners twice, quoted.

## Report — append here, commit it, ≤12 lines: tip sha; where the 2.8 s went, with the runner's numbers; what changed
and why; gate counts and the runner results. Verified or NOT VERIFIED. No push except the draft PR; no merge.

## Report

**Tip `8867f86` + this docs commit. A fixture, not the product: `git diff origin/main -- src/` → empty.**
**Where it went — verified by running** the harness on PR #13 (runs 34046090536, 34046100888; 8 presses per runner per
job, per-step marks). The budget cut EVERY stalled platform call at the deadline on all three runners
(`live-read-threw`/`cancel-call-threw` 2000-2016 vs `deadlineAt=2000`; macos 2110, its timer floor), so all of the
overrun is the post-deadline record. Worst windows press: `cancel-call-threw=2000 leg-settle-indefinite=3890
press-settle=5359 complete-composite=5468 activity-line=5578` — one `SafelyRecordIndefinite` commit at
`synchronous=FULL` cost 1890 ms, the `SafelySettle` 1469 ms, 3578 ms over — with `gcPause=0` and a 20 ms tick arriving
at 47 ms on a dedicated thread AND on the pool, so the process was running. Ten bare one-row commits, same database,
same test: ubuntu 4-11 ms, macos 0-7 ms, windows 16-2234 ms. Windows overran 15-63 ms in 12 of 16 presses and 219 /
1000 / 1516 / 3578 ms in the other 4; ubuntu 5-16 ms, macos 13-140 ms. **No product wait**; one commit alone is over H.
**Changed (tests only):** the wall clock charged the press E and H together and gave the sum to an assertion about E.
`RecoveryConnector` now stamps every platform call in and out (`WireCalls` — permanent, with the finding on it) and
`TheStalledPressGaveUpOnItsOwnDeadline` sums the part of each call past the deadline against the SAME `HandlerOverhead`:
0-1 ms ubuntu, 0-16 ms windows, 8-138 ms macos over the 24 presses. The five tests' behavioural assertions are
byte-identical, the class stays in `Timing`, the harness and its product hook are gone. **Verified by running** a mutant
(the late simulator ignoring its token) → RED `a press whose simulator ran late was still on the platform 1408 ms after
the deadline the press itself opened (orders 1407 ms, cancel 1 ms), against 1s of handler overhead`.
**Gate, verified by running** at `8867f86`: Release `--no-incremental` → `0 Warning(s) 0 Error(s)`; the class 3× →
`Passed! - Failed: 0, Passed: 5` each; full suite one project at a time, no other test host (`pgrep testhost.dll` → 0)
→ 392 + 615 + 261 = 1268 passed, 0 failed, 1 skipped; names vs `origin/main` (every `[Fact]`/`[Theory]`, class-qualified)
→ 1014 each side, **0 removed, 0 added**. **CI, four runs, all three runners green in every one, 1268 passed / 0 failed
per runner, zero Timing retries executed:** 34052601589 (PR) and 34052615610 at `7fa64f1`, 34053292187 (PR) and
34053305400 at `8867f86` — `package` SUCCESS in all four. **NOT done:** no merge; no product code; no box, ATAS or UI.
