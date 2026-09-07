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
