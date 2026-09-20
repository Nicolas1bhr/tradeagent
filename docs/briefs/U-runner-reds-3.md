# U-runner-reds-3 — three hosted-runner reds of 2026-09-20, test-only: measure, fix the fixture or argue Timing with numbers, prove on the runners
**Boundary protected:** the honesty of the CI record (`docs/HOW-WE-BUILD.md` step 6: a hosted-runner red in a test the target passes is a fresh fixer on top of
`main`, the sha recorded red until it lands; the `Timing` category is the one place a second attempt exists, membership argued at the test with MEASURED numbers,
never granted to whatever went red, no assertion loosened). **Observable result:** the three tests below pass on ubuntu-latest, windows-latest and macos-latest
on a draft PR of this branch, each fixture's schedule measured, and nothing product-side changes (`git diff main -- src/` empty).
The reds (SOURCE: the two runs' `--log-failed`; the SAME code is GREEN at the neighbouring shas `8636204`, `c17d5c1` and `3746774`):
1. `d9ae716`, windows-latest, run 35501396212 (that job's Unit suite took 19 m 14 s against ~40 s normal — a starved runner): `SweepRequestIdTests.Every_sent_not_
   confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` → `Assert.NotEmpty() Failure: Collection was empty` [34 s] (its family: `U-sweep-win`,
   `U-sweep-latency-win`); `ValuationLossSurfacesTests.An_exit_is_reported_under_its_own_line_and_says_no_budget_was_reached` → `Assert.Contains() Failure:
   Sub-string not found` [1 m 3 s, a UNIT test] — first sighting.
2. `ed3b224`, ubuntu-latest, run 35501981449: `BridgeRoundTripTests.A_bridge_speaking_the_previous_protocol_raises_no_events_into_the_application` →
   `Assert.Equal() Failure: Values differ` [317 ms] — first sighting.
Read first: `docs/HOW-WE-BUILD.md` step 6; the `U-sweep-win`, `U-sweep-latency-win`, `U-press-settle-win` and `U-peer-row-ubuntu` sections of `BUILD-STATUS.md`
(how a fixture's schedule was MEASURED per step on the runners before anything moved); the three test files and the fixtures they share (`PressBudget`,
`SweepBudget`, the bridge harness). Product code is off-limits: `git diff main -- src/` must be empty at every commit.
Items, one commit each: (a) instrument each fixture (a throwaway probe printing the measured schedule, reverted after) on a draft PR — `gh pr create --draft`
from THIS branch; push only this branch, never `main` — and read the numbers on all three runners at least twice; (b) fix each fixture (the generous per-leg
budget, a hand-over instead of a race, an assertion on the product's record instead of the runner's clock) OR move it into `Category=Timing` with the measured
numbers written at the test — never loosen an assertion, never delete or rename a test; (c) sweep the sibling fixtures of the same shape and name what you did
NOT move. Report ≤ 20 lines appended here: the PR number, the numbers per runner, the red-on-runner evidence and the green after, one line per test.
Gate as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed on this Mac; touched classes 3×; names 0 removed). Close the
draft PR after the proof (never merge it); no `Co-Authored-By`; no push to `main`; touch nothing in `docs/briefs/` but this file.
