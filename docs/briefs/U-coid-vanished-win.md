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

## Report

Gate and CI at `3791460`, rebased onto `main` `aaff41b`; `CoidWitnessTests.cs` only, +33/-5, no product file. **The 137 ms were the runner's disk, not the retry loop.** Harness on
PR #11, 3 runs x 3 runners (34041706666 / 34041712859 / 34041721936): in all 27 samples the rename was attempted ONCE and the retry loop spanned 0.0 ms, while the call around it
cost 10.7-50.3 ms on windows (8.1-29.2 of that before the rename) against 0.6-19.8 ubuntu and 1.7-4.5 macos, and one flush-to-device 7.2-47.1 ms on windows vs 0.4-1.1 ubuntu. That
harness run also went RED on **ubuntu** - the same message at the same line, 194 ms, run 34041721936 - so it was never one platform's. **Fixture fixed, no assertion loosened:** the
stopwatch is read only inside the injected rename, so its span is the product's own sleeps and holds no file IO; the 100 ms ceiling and the message are unchanged, and
`Assert.Single` on the attempts is added, the exact count the sibling five-attempt test already asserts. What it no longer bounds is the disk around the call, which is not the
product. NOT moved to `Timing`: the verdict needs no runner clock. **Mutant** (the exclusion by name dropped from `Transient`) -> `burned 204 ms of the retry budget on a file that
is not coming back`. **Gate, each run and quoted:** Release `--no-incremental` -> 0 warnings, 0 errors; the class 3x -> 149/149 each; full suite one project at a time, 0 other test
hosts -> 348 + 261 + 615 = 1224 passed, 0 failed, 1 skipped; `--list-tests` vs `main` 1225 = 1225, 0 removed, 0 added; secret scan clean. **Draft PR #11 run 34042541589 GREEN on
all three runners** and `package`, no Timing retry on any runner. A second run at the same sha, 34043185411: ubuntu and macos success, windows RED in the `Timing` step only and in
BOTH attempts, on `OperatorPressIsAnEmergencyTests` (`close-all returned 2813 ms after the deadline the press itself opened`, then cancel-all) - not this unit; this test passed on
all three runners in both runs, six runner-passes. **NOT done:** no product code, no box, no UI, no merge; `main` untouched. Nothing here is NOT VERIFIED.
