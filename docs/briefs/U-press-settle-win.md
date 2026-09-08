# U-press-settle-win — the press that cancelled the unknown close but had no budget left for its own leg on windows-latest
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph, "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-08 —
U-unknown-close landed`, `U-sweep-win landed` and `U-press-win-3 landed` sections of `BUILD-STATUS.md`. Branch
`u-press-settle-win`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-press-settle-win`, rebased onto `main`
first. Test-only unless the product is wrong; no assertion loosened; no box, no ATAS, no money.

**The red.** CI run 34187380076 at `8197163` (the `U-archive-win` merge), windows-latest only, ubuntu and macos green, the same
test green on windows at its own merge sha `acff18a`: `TradeAgent.Tests.Fault.PressSettlesAnUnknownCloseTests.A_press_cancels_
the_unknown_close_before_it_closes_and_the_book_ends_flat` → `Assert.DoesNotContain() Failure: Filter matched in collection`,
the collection `[PositionInfo { Symbol = ES, Quantity = 2 }]`. The test's own output tells the story: `before the press: ES 2 —
FB-1 Buy 2 FILLED | FB-3 Sell 2 WORKING`; `press: 1 of 1 record(s) from this press are still waiting for you`; `the lost close's
record: CANCELLED`; `orders at the broker: FB-1 Buy 2 FILLED | FB-3 Sell 2 CANCELLED`; `position at the end: ES 2`. The press
DID cancel the unknown close (item 1 of `U-unknown-close`) and then did NOT send its own leg: the leg was refused at the
press's two-second deadline and left as a flagged record — the contract when the platform cannot be answered in time. On the
Windows runner the cancel's round trip plus the composite commits spent the budget (`U-press-win-3`: one commit 16–2234 ms;
`U-sweep-win`: the budget spent inside one composite commit, 144 sweeps measured). The product refused honestly; the fixture
assumed the runner's disk would leave time for two wire calls inside two seconds.

1. **Measure before touching anything** (a draft PR, the `U-sweep-win` harness shape: a mark per step with the budget left —
   the cancel sent, the cancel answered, the re-read, the leg — on all three runners, several presses each): quote the marks.
   If the leg was refused with budget to spare, that is the product and the headline: RED-first test, one mutant.
2. **Then fix the fixture once, the `U-sweep-win` way:** every test in that class whose verdict is "the book ends flat" and not
   "the press keeps its two-second promise" takes the file's generous budget (20 s, injected the way `ReadyWithBudget`/
   `SweepBudget` do) so no assertion depends on the runner's disk; every assertion byte-identical; the budget tests untouched;
   no `Timing` membership unless the verdict needs the runner's clock (argue it at the test with the numbers).
3. **Sweep the siblings** in `UnknownCloseTests.cs` (both press directions, the agent's refused close) under the same marks;
   move the same shape the same way; name the rest as not at risk and why. Remove the harness from the tip.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Fault project 3× and the three test projects
in Release to a file → 0 failed; test names vs `main` → nothing removed; the draft PR's three runners green at the tip (quote
the run ids). Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, the gate counts pasted, the
marks per runner, what was fixed, the run ids, what you did NOT do. Close the draft PR after the report.
