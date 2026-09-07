# U-sweep-win — the sweep test that saw nothing working on windows-latest at the U-wakes merge sha
Read `docs/HOW-WE-BUILD.md` (step 6 of the landing checklist, the `Timing` paragraph), `CLAUDE.md`, the `## 2026-09-05 —
U-press-win-3 landed` and `## 2026-09-07 — U-wakes landed` sections of `BUILD-STATUS.md`. Branch `u-sweep-win`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-sweep-win`, rebased onto `main` first. One item; product code only if
the product is wrong; no assertion loosened; no box, no ATAS, no money.

**The red.** CI run 34146285162 at `cef122b` (the `U-wakes` merge), windows-latest only, macos and ubuntu green, the Unit
and Fault suites green on the same runner: `TradeAgent.Tests.Integration.SweepRequestIdTests.Two_sweeps_mint_different_ids`
→ `Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 0` at `SweepRequestIdTests.cs:1652`
(`Assert.Equal(1, data.GetProperty("attempted").GetInt32())`): the `cancel-all` sweep sent right after a `place` that the
fake broker leaves working (`FaultProfile { Fill = FillBehaviour.LeaveWorking }`) attempted NOTHING — the order was not in
the working set when the sweep looked. The same tree passed 615/615 Integration on this Mac and on the two other runners;
the test has no `Timing` category. `U-wakes`' only product change near this path (`git diff 86dda13 cef122b --
src/TradeAgent.Gateway/TradingGateway.cs`) raises a wake after `OnOrderChanged`'s transition and after a recorded fill,
through a delegate that is null in a host with no mission; `Wake()` serialises its payload before the null check.

1. **Find where the second is lost, by measurement, not by reading.** Instrument the test's two calls on a draft PR
   across all three runners (the `U-press-win-3` harness pattern: a mark per step — place returned, the order's state as
   the gateway holds it at that instant, the sweep's read, what the fake broker held): is the `place` reply sent before the
   gateway has recorded the order as working (a schedule the fixture assumed), or does the sweep's working-set read race
   the transition (product), or did `U-wakes`' serialisation move a timing that was always marginal? Quote the marks.
2. **Fix the right thing, once.** If the fixture assumed a schedule: make it wait for the fact (poll the gateway's orders
   for the working state with a bounded wait) and keep every assertion byte-identical; say whether the class belongs in
   `Timing` with the measured numbers, and add it only if its verdict needs the runner's clock. If the product races
   (a `cancel-all` that can miss an order the platform already acknowledged): that is a money-path defect — a RED-first
   test and one mutant, and say so in the report as the headline. Either way the harness is removed from the tip.
3. **Sweep the siblings:** the other tests in `SweepRequestIdTests.cs` that place-then-sweep with `LeaveWorking`; run them
   under the same marks; fix the same shape the same way; name the rest as not at risk and why.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Integration project 3× and the three test
projects in Release to a file → 0 failed; test names vs `main` → nothing removed; the draft PR's three runners green at
the tip. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, the gate counts pasted, the
marks quoted per runner, the RED and mutant if the product changed, the sweep's list, and what you did NOT do.



## Report

**Gated tip `4f2c78b`**, this report on top (rebased twice, over `U-data-binance` then `U-council-thin`, no conflict either time). Four commits, test-only; `git diff main -- src/` EMPTY.
**HEADLINE: the product does not race** — a sweep that cannot issue its leg names the order `not-sent`, which is the contract; no red-first test, no mutant. Draft PR #14, closed after this.
**Measured** (runs 34164725697 + 34165321766; 24 place-then-sweep pairs per runner per run, 144 sweeps, plus a control each): the book read never lost the order — `nothing_to_do=False`, one target in the plan, the broker holding `FB-n:WORKING` when the place reply was read, 144/144 — so the brief's first candidate is FALSE.
Budget left of 2000 ms at leg 0 (`budget[enter=2000 read=1/2000 composite=1/1985 leg0=1985]`): windows min/med 1968/1985, ubuntu 1999/1999, macos 1996/1999 — worst prefix spend 32 / 1 / 4 ms. The ONE step in that window is `BeginCompositeAsync`'s composite row, a single `synchronous=FULL` commit (40-commit probe: windows med 16 p95 32 max 47 ms, ubuntu 2, macos 6; sweep round trips windows med 78 max 156, ubuntu 8, macos 13).
CONTROL, all three runners — a connector answering the read correctly then holding until the deadline goes — gives the red's own value: `attempted=0 cancelled=0 nothing_to_do=False not_sent=1`, leg `not-sent/NothingWritten/"the operation ran out of time before this leg was issued; it was not sent"`. With the book non-empty that is the only path to it. **NOT VERIFIED:** no such outlier occurred in 96 windows sweeps here; U-press-win-3 measured one on the same image (one-row commits 16-2234 ms).
**Fixed once, in the fixture:** `ReadyWithBudget` takes the fill and is `internal`, and the file's own `SweepBudget` (20 s, already there for the mixed-outcome test) now serves the 14 fixtures that are NOT about the budget — assertions byte-identical, nothing loosened, no class joins `Timing`: at 20 s each test's own 10 s `WaitAsync` expires first, so the operation deadline decides nothing.
**Siblings:** MOVED the 14 whose verdict needs a leg on the wire (ten cancel-all/close-all tests in `SweepRequestIdTests`, incl. `A_sweep_cannot_collide…` which was vacuous rather than red, plus the four in `ReplayedSweepSendsNothingTests`); LEFT the 16 not at risk — seven id-refusal/length tests with no leg in the verdict, nine that ARE about the budget and set their own.
**Gate, Release, run whole at `f1faf13` and again at `640c5b7` after the rebase:** build `--no-incremental` → 0 Warning(s), 0 Error(s); Integration 3× → `Failed: 0, Passed: 621, Skipped: 1, Total: 622` each of the three (615 each pre-rebase); three projects → 539 + 277 + 621 = 1437 passed, 0 failed, 1 skipped, no other test host; names vs `main` → 1201 = 1201, 0 removed, 0 added (Fact/Theory 1179 = 1179); output files deleted. CI 34166551105 at `f1faf13`: three runners + `package` green. CI at the two rebased tips, 34168445545 (`640c5b7`) and 34170462786 (this tip): ubuntu and macos green both times, and on windows Integration 533/533 with 0 failed both times — the ONE red, both times, is `BinanceArchiveTests.A_month_with_no_sidecar_at_all_is_not_collected` (Unit, from `U-data-binance` on `main`, a file I did not touch, on a runner whose Unit project took 30 m 17 s). **So the three runners are NOT green at this tip, and the reason is not mine** — back to the manager; `docs/briefs/U-archive-win.md` is already open for it.
**NOT done:** no product code, no `Timing` membership, no assertion touched, no box, no ATAS, no money.
