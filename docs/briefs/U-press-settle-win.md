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

## Report

**Gated tip `dbaf5e5`** (this report commit is docs-only on top), rebased onto `main` at `583f24e`, no conflict; `git diff main -- src/` EMPTY — harness and hook gone, the `is { } stuck` call site byte-identical. `main` has since moved five commits (`U-turn-commit`); the re-rebase is the manager's step 2.
**1 — MEASURED, and the product is right: the runner's disk spends the budget, so the fix is the fixture, the `U-sweep-win` way.** 48 presses of the failing test's own fixture — 8 per runner per run, PR #16 runs 34188680175 (`1b2d58c`) and 34189533668 (`d519d87`) — reached the leg and SENT it EVERY time on EVERY runner: `states=[ES:FILLED] pos=[] sellsFilled=1`. Budget left of the 2000 ms at `leg0-close-sent`, run1 / run2: windows 1687-1765 / 1813-1985, ubuntu 1993-1997 / 1995-1996, macos 1916-1998 / 1997-1999. Windows' whole spend is record-keeping: the two settle transitions (`settle-cancel-answered`→`settle-written`) 78-141 ms and the leg's write-ahead (`leg0-settled`→`leg0-row`) 79-172 ms, against 20 bare one-row commits on that same disk at med 16-47 / max 47-110 ms, `gcPause=0`, a 20 ms tick arriving at 31-47 ms — the process was running.
THE CONTROL settles it: the settle's cancel answered correctly, then the caller held past the deadline — which is what one stalled `synchronous=FULL` commit does — reproduces the CI red byte for byte on ALL THREE runners. `settle-cancel-answered` = windows -31 / ubuntu -24 / macos -29, and `lost=CANCELLED book=[FB-1 Buy 2 FILLED | FB-3 Sell 2 CANCELLED] pos=[ES 2] sellsFilled=0`, which is the red's own output at `8197163`. No leg was ever refused with budget to spare, so NO product change, no red-first test, no mutant — and the test's green on windows at `3003b89` (run 34186596626) is the same story from the other side.
**2 — the fixture.** The three presses whose verdict is the book take `PressBudget` = 20 s; `A_settle_that_runs_out_of_the_presss_deadline…` keeps the simulator's two seconds; every `Assert.`/`Out.WriteLine` line byte-identical to `main` in BOTH files (diffed, not eyeballed); no `[Trait]`, so no `Timing`. Two numbers in the budget argument were wrong and are corrected: "thirteen of the worst commit (2234 ms) fit in 20 s" → eight, and the tick range 32-47 → 31-47 ms. The control is written in as the argument's evidence.
**3 — siblings.** `AgentCloseOverAnUnknownCloseTests`' four are NOT at risk, measured rather than assumed: `deadlines=[none]` on every wire call on all three runners — no press, no `RiskReducingScope`, no clock. The eleven press fixtures in `DispatchRecoveryTests.cs` are NOT moved and NOT immune: same 2 s, same disk, but two durable commits before the leg instead of four (no reducer to settle) and none has gone red. Said at `Recovery.Ready` rather than left silent — moving a file-wide default over a red that has not happened is the manager's call, not a fixer's.
**Gate**, Release, at `dbaf5e5` (another leg's suite ran on this Mac concurrently; nothing went red, so nothing was re-run alone): `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 Warning(s), 0 Error(s)**, 17 projects. Fault 3× → **277 passed, 0 failed** each. Unit 588 + Fault 277 + Integration 627 = **1492 passed, 0 failed, 1 skipped**. Names vs `main` → **0 removed, 0 added** (sets 1290 = 1290; `[Fact]`/`[Theory]` 1232 = 1232). Secret scan printed and clean before each commit; no trailers.
**CI run 34697322435 at `dbaf5e5`: SUCCESS on all three runners and `package`.** windows-latest GREEN — the press-settles test with it. First attempt was red on ubuntu-latest only, on `MissionLoopTests.A_file_the_owner_drops_between_turns_is_still_recorded_as_theirs` (`Expected: Inbox / Actual: InboxUnattested`) — a file this branch does not touch, on a product path it does not touch, green on `main` at `583f24e` 75 minutes earlier (run 34694003715) and green on the re-run of the same job. Fault 272/272 and Integration 539/540 were green on that failed attempt too.
**NOT done:** no product code and no `Timing` membership; the eleven `DispatchRecoveryTests` presses left on two seconds (above); the branch not re-rebased over the five commits `main` took while the gate ran; no box, no ATAS, no money.
