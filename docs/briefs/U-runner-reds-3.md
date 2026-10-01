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
**Resumed 2026-10-01 — the branch is the handoff; read it first.** The first leg ended on an access error (`Your organization has disabled Claude subscription
access for Claude Code`) after nine commits: tip `9f48bb8`, on `309f389`, CI GREEN on all four jobs (run 35515899143), draft PR #23 open, no report. At the
manager's message it had taken a fourth red: `659eb5b`, macos-latest, run 35503895941, `CouncilLoopTests.A_second_turn_for_a_role_already_turning_is_refused_and_
never_launched` → `Assert.Equal() Failure: Values differ`; and item 2's test went red again at `b3582d7`, ubuntu-latest, run 35515252973. The finisher: rebase onto
`main`, check every commit against items (a)–(c) for all four tests, take the per-runner numbers from the PR's runs, run the gate, write the report, close #23.

## Report
**Code tip `a24c083`** (PR #23, draft; report is the commit above) on `main` `516376c`, no conflicts, `src/` diff empty; `main` moved to `88a23a2` since: merges clean.
Gate at `a24c083`, Release `--no-incremental`: **0 warnings, 0 errors**; passed/failed/skipped Unit **1189/0/0** (26 s), Fault **399/0/0** (1 m 27 s), Integration
**695/0/1** (11 m 4 s; the skip pre-existing). 3×, 0 failed every run: Council 11, Valuation 5, Bridge 41, Sweep 44. Names vs `main`: 1907 → 1907, **0 removed**.
(a) Probes ran on all three runners in 35504722157 and both attempts of 35513092386 (council: those two only), reverted in `591652e`; `StubBridge.Ended` stays for the fix.
1. `SweepRequestIdTests.Every_sent_not_confirmed_…` — red 35501396212 windows (`d9ae716`), `Assert.NotEmpty()` [34 s]. D at latency 0, 3 rounds × 3 runs: ubuntu
   4-10 ms, macos 2-6, windows 31-254, against 5000 ms of room (20x); room costs 3x itself in wall time → `Category=Timing` (`6ecc493`), nothing loosened.
2. `ValuationLossSurfacesTests.An_exit_is_reported_…` — same run, `Assert.Contains()` [1 m 3 s]. Exit pass 26.8-32.8 / 16.5-22.4 / 712.5-1258 ms (ubuntu/macos/
   windows) of the shipped 2 s; a 1 ms budget reproduced the red in all 9 jobs. Fixed (`77b8922`): 20 s budget, loop driven to the exit record (bound 12, 4-5 needed).
3. `BridgeRoundTripTests.A_bridge_speaking_the_previous_protocol_…` — red 35501981449 (`ed3b224`) and 35515252973 (`b3582d7`), ubuntu, `Assert.Equal()`. Snapshot
   inside the refusal's own two events in 5/7/9, 9/6/7, 9/9/6 of 10 spun rounds (ubuntu/macos/windows), 0/90 polled. Fixed (`90b59ef`): read after end-of-stream.
4. `CouncilLoopTests.A_second_turn_…` — red 35503895941 macos (`659eb5b`), `Assert.Equal()`. 20 ms preemption → 2 launches in 25,30/30 ubuntu, 29,30/30 macos,
   0,0/30 windows (turn ≥ 100.9 ms), 30,30/30 this Mac (re-run here). Fixed (`06c1e65`): lease held until the loser is answered → 1 launch in 30/30 everywhere.
Green after: 36896121229 at `a24c083`, 4/4 jobs success, its windows job at 1.45-2.0x its usual time (slower than the red one); also 36888851400, 35515899143,
35515732610 at the same fixture code; `Timing` passed first time in all twelve test jobs. Mine, comments only: `c6a6b26` sets three fixtures' figures to the
probe output; `a24c083` drops the brief's "~40 s" windows baseline (Unit took 12-15 min on the green windows jobs either side of the red one: 1.25-1.56x, not 29x).
(c) Moved: `A_leg_that_failed_before_the_wire_…` → Timing (`e41831f`, 3000 ms room, 12x); the class's episode fixture driven to its record. **NOT moved, unmeasured:**
`A_leg_refused_before_the_wire_…` (B 5 s, L 0: the SAME 5000 ms room as item 1), `The_simulators_two_latencies_…` (stopwatch), `A_five_order_sweep_…`; 2 s-budget
presses in `Loss{DayClosed,Flatten,Hold,Reopen}SurfacesTests`, `LossBoundary/LossWatch/PressIdShape/FaultTests`, `PipeContractTests`; `An_authenticated_peer_…`;
`Barrier` races in `BudgetReservation/CampaignLedger/VersionLineageTests`. NOT DONE: no box, no order; nothing to `main`; #23 is closed after this push, not merged.
