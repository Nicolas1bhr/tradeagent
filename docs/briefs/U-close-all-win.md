# U-close-all-win — a close-all press fixture that leaves one position open on the hosted Windows runner while the product is green everywhere else
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph — membership argued at the test with measured numbers, an assertion never loosened; "The
fresh-fixer rule"), `CLAUDE.md` (rule 3; two-press), the `## 2026-09-14 — U-press-inflight-win landed`, `## 2026-09-12 — U-press-settle-win landed` and
`## 2026-09-08 — U-sweep-win landed` sections of `BUILD-STATUS.md` (the Windows runner's disk spending a press budget inside the press's own commits; the
thirty fixtures moved to the generous budget and judged first), then `tests/TradeAgent.FaultTests/DispatchRecoveryTests.cs:916-975`
(`OperatorEmergencyRecordTests`, the test at `:947-963`, `Recovery.Ready(emergencyBudget: Unresolved.PressBudget)`), `UnknownCloseTests.cs` (`PressBudget`,
the arithmetic behind it), `src/TradeAgent.Gateway/TradingGateway.cs:3817-4037` (`OperatorCloseAllAsync`: `BeginComposite`, per symbol
`SettleAnUnresolvedReducerOrRefuse`, the flagged write-ahead row, `Connector.ClosePositionAsync`, the outcomes), the fake connector's `Closes` counter and
its broker's `Positions`. Branch `u-close-all-win`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-close-all-win`, rebased onto `main`
first. Test-only unless the product is wrong; no assertion loosened; no test deleted; no box, no ATAS, no money.

**The red, first sighting.** CI run 34944735920 at `44f33a4` — a DOCS-ONLY sha — windows-latest, step "Test everything outside the timing category",
Fault 288/289, the test ran 1 m 05 s:
```
TradeAgent.Tests.Fault.OperatorEmergencyRecordTests.Close_all_with_a_healthy_connector_closes_each_position_once_and_records_each
Assert.Empty() Failure: Collection was not empty
Collection: [PositionInfo { Id = P-NQ, AccountId = SIM-001, Symbol = NQ, Quantity = 1, AveragePrice = 112.50, UnrealizedPnl =  }]
   at …\tests\TradeAgent.FaultTests\DispatchRecoveryTests.cs:line 959
```
The two asserts before it passed: two targets, two closes reached the connector. The second position was still on the fake broker when the press
returned. ubuntu green, macos red on an unrelated unit test, the same sha green on windows at `1577739` one minute earlier. Never seen before.

1. **Judge it first:** establish from the code what a press does when a leg's close outlives the press budget or the composite commit stalls (the row
   left UNKNOWN, the position untouched on the fake broker, the press returning with two targets) and whether that is the path that produces exactly
   this collection; then reproduce on this Mac with the composite commit slowed in a throwaway copy (a delay in the fake connector's close, or the
   database write) and quote the failure. Quote what the press record said (`gw.Requests.Query("request_id LIKE 'op-close-%'")`) at the failure.
2. **Then fix once:** if the fixture's `PressBudget` cannot cover two sequential legs plus the runner's disk inside the settle's commits, size it by
   arithmetic at the test the way `UnknownCloseTests` does and say why 65 s was spent (the runner's numbers, quoted from the trx or a draft PR run);
   sweep `OperatorEmergencyRecordTests` and the rest of the file for siblings with two or more legs under one budget — name each moved or not at
   risk and why; a product defect (a leg the press abandons without flagging) is a RED-first test and the smallest product fix. `Timing` membership
   only if the verdict needs the runner's clock, argued with the numbers.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Fault project 3× and the three test projects in Release to a file → 0
failed; names vs `main` → nothing removed; a draft PR's windows runner green at the tip (run id quoted; at most two runs; close it after the report).
Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the path named, the reproduction quoted, what moved
and what did not, the run id, what you did NOT do.

## Report

**Tip `6b3c1cf` plus this report commit**, rebased onto `69c44d7`. TEST-ONLY: `git diff main -- src/` is EMPTY; every `Assert.` line in `DispatchRecoveryTests.cs` byte-identical to `main` (diffed); `UnknownCloseTests.cs` gains a doc comment and one expression; no test added, renamed or removed.
**1. Judged from the code, and it is one path.** ONE absolute deadline is opened per press (`RiskReducingScope.Begin`, top of `OperatorCloseAllAsync`) and everything between it and a leg's wire call is durable SQLite at `synchronous=FULL`. When it expires inside those commits the NEXT leg's close takes `FakeConnector.ClosePositionAsync` → `Wire(ct,"positions")` → `HonourTheOperationDeadline`'s `left <= 0` branch, which throws BEFORE the book is read — while `RecoveryConnector.ClosePositionAsync` has already counted the call. That produces exactly `two targets, two closes reached the connector, one position still on the fake broker`, and only that; the shared `CloseCapturedAsync` then records the leg UNKNOWN and flags it, so no leg is abandoned unflagged and NOTHING IN THE PRODUCT IS WRONG.
**Reproduced on this Mac** (throwaway hook before `tx.Commit()` in `Database.Write`, reverted; every commit of the press held for 2234 ms, the worst commit measured on that runner image): the real fixture on `PressBudget` fails byte for byte — `Assert.Empty() Failure: Collection was not empty / Collection: [PositionInfo { Id = P-NQ, AccountId = SIM-001, Symbol = NQ, Quantity = 1, AveragePrice = 112.50, UnrealizedPnl =  }]` — and the press record reads `op-close-…-0 ES FILLED needsRecon=True` beside `op-close-…-1 NQ UNKNOWN needsRecon=True err='positions' could not be read, so the operation was not started. Nothing was placed or cancelled. the operation deadline had already passed and nothing was sent to the simulator.`
**Why the 65 s, from the red run's own trx.** Commits counted here: a close-all press makes 9 durable commits for one position and 14 for two (12 before the second leg's close reaches the wire); a cancel-all makes 10 and 13 — `5 + 5×legs` bounds both. The red's fixture makes 35 in all, and 65.58 s over 35 is 1874 ms a commit: inside the 16–2234 ms band measured on that image, on a run whose whole Fault project averaged 8.6 s a test (2484.9 s over 289, run 34944735920) against 0.26 s here. Its 14 press commits at that rate are 26 s against a 20 s budget. So it IS the settle's-own-commits family of `U-sweep-win`, `U-press-settle-win` and `U-press-inflight-win`, one term further along: not a budget a stalled disk ate, but a budget never sized for two legs.
**2. Fixed once, at the test:** `Unresolved.PressBudgetFor(legs) = (5 + 5×legs) × 2234 ms` → 23 s, 34 s, 45 s, with both commit counts and the runner's worst commit stated there. GREEN under the 2234 ms stall: the press takes 31 s, both legs FILLED, `positions: []`. MUTANT (the flat `PressBudget` put back, same stall): RED with the `Assert.Empty()` message above.
**Swept — all ELEVEN presses in the file moved,** each site naming its own legs. TWO legs: `Close_all_with_a_healthy_connector…` (the red), `Cancel_all_records_every_order_it_cancels`, `Close_all_keeps_going_after_one_position_fails`, `A_close_that_is_only_submitted…`, `Close_all_says_it_closed_everything…`, `Each_order_is_settled_by_the_answer_to_its_own_cancel`, `A_sweep_that_accounted_for_everything…`. ONE leg, moved too because the same arithmetic puts 20 s short of 23 s: `A_close_that_landed_then_failed…`, `A_cancel_all_that_failed_on_the_wire…` (87 s on the red runner, and it passed), `Confirming_one_outcome_does_not_lift_another…`, `The_emergency_controls_still_work…` (two presses, one leg each).
**NOT moved, named with counts** — five multi-leg presses still on the flat 20 s, outside this brief's file: `UnknownCloseTests.cs:259` (2 positions), `EmergencyPressTests.cs:224` and `:252` (2 orders each), `PressInFlightTests.cs:151` (2 positions), `LossFlattenTests.cs:206` (ES+NQ; `U-flatten-3` is briefed against that file). Each is one token, `PressBudgetFor(2)`. This is the call the 2026-09-12 EXPOSED paragraph made and a red came of it, so it is left as a named debt, not as a judgement that they are safe.
**Gate at `6b3c1cf`, Release, every output to a file:** build `--no-incremental`, 17 projects → 0 warnings, 0 errors; Fault 3× `--no-build` → 329/329 each, 0 failed; Unit 1117/1117, Fault 329/329, Integration 668 passed + 1 skipped of 669 → 0 failed; names vs `main` → 0 removed, 0 added (sets 1750 = 1750, `[Fact]`/`[Theory]` 1750 = 1750). Draft PR #22, run 34988626096 at `6b3c1cf`: ALL FOUR JOBS GREEN — windows-latest (Fault 324/324, the fixture 5.43 s), ubuntu-latest, macos-latest, `package`. PR closed after this report.
**What that runner does NOT prove:** its disk was healthy — the Fault project took 821.3 s at a 2.08 s median against the red run's 2484.9 s at 6.19 s — so it re-ran the fixture, it did not re-run the stall. The stalled-disk claim rests on the local reproduction and its mutant, quoted above.
**NOT done:** no product code and no product mutant — nothing in the product changed; no `Timing` trait, because the verdict is still what the press DID and this number exists to keep the runner's clock out of it; no assertion loosened or touched; the five presses above not moved; no box, no ATAS, no money.
