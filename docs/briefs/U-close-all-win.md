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
