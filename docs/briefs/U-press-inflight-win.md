# U-press-inflight-win — the cancel-all press against open work had no budget left for its own leg on windows-latest
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph; "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-12 — U-press-settle-win landed`,
`## 2026-09-08 — U-sweep-win landed` and `## 2026-09-06 — U-press-win-3 landed` sections of `BUILD-STATUS.md` (the class: a Windows disk can hold
one composite commit for most of a two-second emergency budget; the product refuses honestly; the fix is the FIXTURE, once, for every test whose
verdict is the book and not the two-second promise), then `tests/TradeAgent.FaultTests/PressInFlightTests.cs` (`SlowRead.Ready()` at `:163`, the
two classes, the red test at `:171`), `UnknownCloseTests.cs:85-103` (`PressBudget` = 20 s and why), `DispatchRecoveryTests.cs:228-250` (the
budget injection shape; its eleven presses were named EXPOSED by `U-press-settle-win`). Branch `u-press-inflight-win`, worktree `~/Projects/
ai-trading-software-for-mihael-worktrees/U-press-inflight-win`, rebased onto `main` first. Test-only unless the product is wrong; no
assertion loosened; no box, no money.

**The red.** CI run 34771155930 at `45719b2` (the `U-referee-2` merge, which touched nothing on the order path), windows-latest only, ubuntu
and macos green: `TradeAgent.Tests.Fault.CancelAllAgainstOpenWorkTests.An_agent_modify_inside_the_connector_call_does_not_survive_the_cancel_
all_press` → `Assert.Empty() Failure: Collection was not empty / Collection: [OrderInfo { ConnectorOrderId = FB-1 … Quantity = 2 …WORKING }]`.
The test's own output: `agent modify: on the wire, record DISPATCHING` / `press: 1 of 1 record(s) from this press are still waiting for you` /
`agent modify answered: ACKNOWLEDGED` / `orders at the broker: FB-1 Buy 2 WORKING` / `working at the end: 1`. The press waited on the open
work, then had no budget left for its own cancel leg: refused at the deadline, the record left flagged — the contract. Unit and Integration
were green on that runner; the same test is green at every earlier sha this week.

1. **Judge the shape first, in one run on this Mac:** with the fixture's budget cut to what the runner's disk leaves (inject 100 ms the way
   `DispatchRecoveryTests:250` injects a budget), the test must show the CI's exact output — if it does not, this is not the class and the
   headline is a RED-first product test with one mutant instead. Quote the run either way.
2. **Then fix the fixture once:** every test in `PressInFlightTests.cs` whose verdict is the book — a working order gone, a modify not
   surviving the press — takes the file's generous budget (`PressBudget` 20 s, injected through `SlowRead.Ready`/`Stranded.Ready` the way
   `ReadyWithBudget`/`Unresolved.WithALostClose(budget:)` do); every assertion byte-identical (diffed, not eyeballed); any test whose
   verdict IS the two-second promise stays on the simulator's two seconds; no `Timing` membership unless the verdict needs the runner's clock.
   Mutant (the generous budget removed again with the 100 ms injection in place): the CI's red reproduced.
3. **The eleven `DispatchRecoveryTests` presses named EXPOSED on 2026-09-12:** move the ones whose verdict is the book the same way, now that a
   second class of the family has gone red; name the rest as not at risk and why. Sweep the other press classes (`EmergencyPressTests`,
   `PressAtomicityTests`, `AgentCloseAtDispatchTests`, `CompositeOwnerTests`) for the same shape and say per class: moved, or not at risk.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Fault project 3× and the three test projects in Release to a
file → 0 failed; names vs `main` → nothing removed; a draft PR's three runners green at the tip (run id quoted; close the PR after the report).
Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the judging run, what moved and what did not
per class, the run id, what you did NOT do.

## Report

Tip `0a531b5` (this report commits on top), 2 commits, test-only — `git diff main -- src/ tools/` EMPTY; rebased twice, last onto `main` `cac1787` (U-council-concurrent-1 and -2), no conflict.

**Item 1 — it IS the class, judged rather than assumed.** The red fixture's budget cut on this Mac, Release: 100 / 50 / 20 / 10 / 5 ms all PASS — this disk carries the press from its deadline to the wire in under 5 ms — and at **1 ms** it fails with the CI's own assertion, `Assert.Empty() Failure: Collection was not empty / Collection: [OrderInfo { ConnectorOrderId = FB-1 … State = WORKING }]`, and all five of its output lines byte for byte: `agent modify: on the wire, record DISPATCHING` / `press: 1 of 1 record(s) from this press are still waiting for you.` / `agent modify answered: ACKNOWLEDGED` / `orders at the broker: FB-1 Buy 2 WORKING` / `working at the end: 1`. A temporary print named the single row — `op-cancel-… UNKNOWN resolved=False :: not confirmed — check ATAS` — the press's own write-ahead row with NO leg row behind it: its `GetOrdersAsync` was refused at the deadline ("TradeAgent could not read your working orders, so nothing was cancelled"). The product refused honestly: no product test, no product change.

**Item 2 — the fixture, once.** All five presses in `PressInFlightTests` take `Unresolved.PressBudget` (20 s), through a new `emergencyBudget:` on `Stranded.Ready` (shaped like `Recovery.Ready`) and on `SlowRead.Ready`. NO fixture in that file has the two-second promise as its verdict — not one asserts a duration, a deadline, or a refusal at one — so none is left on two seconds, and no `Timing` trait was added. Assertion lines byte-identical to `main` in all seven touched files, diffed per file (17 / 48 / 10 / 375 / 62 / 18 / 13). Mutant (the generous budget removed with the 1 ms injection in place): RED in 5 of 5 runs, 1–3 of the 5 failing per run, the CI's assertion each time.

**Item 3 — the sweep, 30 fixtures moved.** `DispatchRecoveryTests`: all eleven presses named EXPOSED on 2026-09-12 moved — every verdict there is a record, a state or a book — and the EXPOSED paragraph rewritten to say why the smaller window was not immunity. `EmergencyPressTests`: the 8 outside `OperatorPressIsAnEmergencyTests` moved, plus 2 inside it (`The_position_read_before_the_close_inherits_the_scope`, `A_healthy_press_is_untouched_by_the_scope`) whose verdicts are the scope's existence and the book, argued at each test; its other 3 keep the simulator's two seconds because their verdict IS the promise, and the `Timing` trait is untouched. `PressAtomicityTests`: all 4, through its own `Ready()`. `AgentCloseAtDispatchTests` and `CompositeOwnerTests`: NOT at risk, structurally — neither presses, and `RiskReducingScope.Begin` exists at exactly three places in `src/`, none on their path.

**Gate**, Release at `0a531b5`: build `--no-incremental` → 17 projects, 0 Warning(s), 0 Error(s); Fault 3× → 277 / 277, 0 failed each; Unit 1008 + Fault 277 + Integration 657 = 1942 passed, 0 failed, 1 skipped, no other test host; names vs `main` → 1592 = 1592, 0 removed, 0 added. Secret scan clean before each commit.

**Runner: draft PR #19, CI run `34773625675` at `62dd169` — SUCCESS on all four jobs** (windows-latest, ubuntu-latest, macos-latest, `package`), 0 warnings on each. Windows-latest is the proof: `TradeAgent.FaultTests.dll` `Failed: 0, Passed: 272, Total: 272` in 9 m 34 s, against `Failed: 1, Passed: 271, Total: 272` at the red `45719b2`. That run's FIRST windows attempt was red on ONE Integration test this branch neither touches nor can reach from another assembly — `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` (`SweepRequestIdTests.cs:652`), `Assert.NotEmpty() Failure: Collection was empty` — and green on the re-run. It is a FOURTH fixture of this class: it takes a 5 s budget at `:654` and arranges for a 2000 ms cancel to be the call that runs out, so a disk that spends the budget first turns `sent-not-confirmed` into `not-sent`; its fix is a paired budget-and-latency change, not a budget bump, so it is a unit rather than a line. macos's `Timing` Integration step needed its one second attempt (87/88, then 88/88).

**NOT done:** no product code; no `Timing` membership added or removed; the three deadline tests of `OperatorPressIsAnEmergencyTests` and the healthy second gateway inside `A_wait_the_simulator_predicted_would_fit…` stay on two seconds; `SweepRequestIdTests` untouched (above); no box, no ATAS, no money.
