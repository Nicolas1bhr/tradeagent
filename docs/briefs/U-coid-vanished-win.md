# U-coid-vanished-win — `A_vanished_temp_is_not_waited_for` has gone red twice on windows-latest

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md` (step 6: the `Timing` category is the one place a second attempt
exists; membership is argued at the test with measured numbers, and an assertion is never loosened to get in), the
`U-press-stopwatch`, `U-attest-precondition` and `U-sweep-words-win` sections of `BUILD-STATUS.md` (how the earlier
runner reds were measured and settled), and `tests/TradeAgent.IntegrationTests/CoidWitnessTests.cs` around line 2618.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/coid`, branch `u-coid-vanished-win` from `main`. Test-only unless
the product is the cause; another builder works `U-meter` and you touch none of its files.

**The red, twice:** first on PR #10's runs 34016321810 / 34015391617 (recorded once in the `U-attest-precondition`
section, "a brief each when one recurs"), and now run **34035317665** at the docs-only commit
`e238932`, windows-latest, `CoidWitnessTests.A_vanished_temp_is_not_waited_for [139 ms]`, message: `burned 137 ms of
the retry budget on a file that is not coming back`, at `CoidWitnessTests.cs:2618`. Green on this Mac and on the Windows
target every time it was run there.

1. Find what the 137 ms are: the product's retry loop deciding a vanished temp file is gone, or the runner's file
   system answering slowly, or the fixture asserting a schedule (the `U-attest-precondition` shape). Measure on the
   runner with a draft PR whose test prints its timings, on all three platforms, three runs. Then either fix the
   fixture, or argue `Timing` membership with the numbers, or — only if the product waits when it should not — fix the
   product with a red-first test and one mutant quoted. No assertion is loosened.
2. Gate: Release `--no-incremental` → 0 warnings; the class 3×; the full suite once to a file, one project at a time,
   nothing else running; names vs `main` 0 removed; the draft PR green on all three runners, quoted.

## Report — append here, commit it, ≤12 lines: tip sha; what the 137 ms were, with the runner's numbers; what changed
and why; gate counts and the three runner results. Verified or NOT VERIFIED. No push except the draft PR.
