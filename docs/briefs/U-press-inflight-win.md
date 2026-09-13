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
