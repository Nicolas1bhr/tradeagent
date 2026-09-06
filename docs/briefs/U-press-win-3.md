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
