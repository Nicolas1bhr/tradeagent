# U-sweep-latency-win — a sweep fixture whose five-second budget the Windows runner's disk can spend before its own two-second cancel
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph; "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-14 — U-press-inflight-win
landed`, `## 2026-09-08 — U-sweep-win landed` and `## 2026-09-06 — U-press-win-3 landed` sections of `BUILD-STATUS.md` (the class: a Windows
disk can hold one composite commit for most of an emergency budget; the fix is the FIXTURE, once, for a test whose verdict is a record
and not the budget), then `tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs:640-700` (the test, its 5 s budget at `:654`, the 2000 ms
cancel it arranges to be the call that runs out), `ReadyWithBudget`/`SweepBudget` in that file. Branch `u-sweep-latency-win`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-sweep-latency-win`, rebased onto `main` first. Test-only unless the product is wrong;
no assertion loosened; no box, no money.

**The red.** Draft PR #19's run 34773625675, FIRST windows attempt only (green on the category's one retry; the merge shas of the day green):
`SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` → `Assert.NotEmpty() Failure:
Collection was empty`. The fixture takes a 5-second budget and arranges a 2000 ms cancel to be the call that runs out, so that the leg is
`sent-not-confirmed` and carries an UNKNOWN record; on a disk that spends the budget BEFORE the cancel is sent, the leg is `not-sent`
instead and no record exists — the product's honest answer, the fixture's assumption. The fix is a paired change: the latency the fixture
injects and the budget it takes must be sized so the cancel is always the call that runs out, on any runner's disk.

1. **Judge it on this Mac first:** cut the budget (or delay the composite commit) so the cancel is never sent, and reproduce the CI's
   assertion; quote it. If the leg can be `not-sent` with budget to spare, that is the product and the headline is a RED-first test.
2. **Then fix the fixture once:** the cancel's injected latency stays the call that runs out under a budget generous enough for the runner's
   disk to have committed the row first — say the numbers at the test (the `U-sweep-win` measurements: one commit 16–2234 ms on windows);
   every assertion byte-identical; no `Timing` membership unless the verdict needs the runner's clock, argued at the test.
3. **Sweep the file** for any other fixture that pairs a budget with an injected latency and depends on their order; move it the same
   way or name it as not at risk and why.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Integration project 3× and the three test projects in Release
to a file → 0 failed; names vs `main` → nothing removed; a draft PR's three runners green at the tip (run id quoted; close it after the
report). Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the judging run, what moved
and what did not, the run id, what you did NOT do.
