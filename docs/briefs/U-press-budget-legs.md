# U-press-budget-legs — the five multi-leg press fixtures outside DispatchRecoveryTests still on the flat 20 s budget take the per-leg arithmetic
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph; "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-15 — U-close-all-win landed` section of
`BUILD-STATUS.md` (the commit count per press, `5 + 5×legs`, the runner's worst commit 2234 ms, and the five presses it named and did NOT move), then
`tests/TradeAgent.FaultTests/UnknownCloseTests.cs` (`Unresolved.PressBudget`, `PressBudgetFor(legs)` as landed, and the press at `:259` with 2 positions),
`EmergencyPressTests.cs:224,252` (2 orders each), `PressInFlightTests.cs:151` (2 positions), `LossFlattenTests.cs:206` (ES+NQ — `U-flatten-3` touches this file; rebase
after it lands). Branch `u-press-budget-legs`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-press-budget-legs`, rebased onto `main` first.
Test-only; no assertion loosened; no test deleted; no box, no ATAS, no money.

**Why.** `U-close-all-win` measured that a two-leg press makes 14 durable commits before its second leg reaches the wire and that the hosted Windows runner's worst
commit was 2234 ms, so a flat 20 s is short for any press with two legs; it moved the eleven presses in its own file and named these five as a debt, not as safe.

1. **Move the five, one token each** (`Unresolved.PressBudgetFor(2)`), stating the legs at each site; then sweep `tests/` for any other press fixture with two or more
   legs on the flat budget (`grep -rn 'PressBudget\b' tests/`) and name each moved or not at risk with its leg count. Prove one of them bites: the throwaway
   commit-stall hook `U-close-all-win` used (every commit held 2234 ms, reverted after) → the flat budget RED on the two-position press, the sized budget GREEN.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Fault project 3× and the three test projects in Release to a file → 0 failed; names vs
`main` → nothing removed. Commit once, one sentence, no trailers. Append `## Report` (≤12 lines): tip sha, gate counts, the sites moved with their leg counts, the
stall reproduction quoted, what you did NOT do.
