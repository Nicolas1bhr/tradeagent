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

## Report

**Code tip `aa16362`, plus this report on top, 3 commits, test-only:** `SweepRequestIdTests.cs` alone — `git diff main -- src/` empty, every non-comment change is the
two budgets, the two latencies and one dead knob; no assertion added, removed or altered; no `Timing` trait granted.
- **Judged first (1).** The shipped 5 s/2 s pair here: sweep 5029 ms, `attempted=2 not_sent=0`, BOTH legs `sent-not-confirmed`/`PossiblyWritten` — the cancel had exactly 1000 ms of slack, and the `RefuseBeforeSend = 1` beside it never fires and cannot (that one-shot is consulted only after the deadline check); it is gone, with the measurement at the test. Cutting the budget to 4 s and nothing else reproduces the CI: `Assert.NotEmpty() Failure: Collection was empty`, both legs `not-sent`, `attempted=0`, `transport: null`, "'orders' could not be read, so the operation was not started. Nothing was placed or cancelled. the operation deadline passed before the simulator answered." The product's answer is honest — no product change, no RED-first test.
- **The fixture, once (2).** `B = 17 s, L = 6 s`. A read sleeps its full latency, so no more than `B − 2L = 5000 ms` can ever be left when the cancel takes its turn against the 6000 it declares: that half is arithmetic, not a clock. The runner's half is the other bound — `sent-not-confirmed` rather than `not-sent` while the disk has spent under 5000 ms, five times the old room, against 15–32 ms measured for this step over 96 windows sweeps (U-sweep-win) and 2234 ms for the worst bare commit (U-press-win-3). Price: the sweep is the budget, 5 s → 17 s. MUTANT (the new budget, the old 2 s latency): RED, `Assert.NotEmpty() Failure: Collection was empty`.
- **The sweep (3): six fixtures inject latency; one other was of the class.** `A_leg_that_failed_before_the_wire_…` AT RISK AND MOVED to `B = 7 s, L = 4 s` (and the latency reset to 0 once the sweep is over, so its closing book read stops paying it): its 300 ms for the composite commit sat BEHIND that commit; cutting the gap to 10 ms here reproduces `Assert.Contains() Failure: Sub-string not found / String: "the operation ran out of time before this"··· / Not found: "Nothing was placed or cancelled"` — the legs were never issued. NOT at risk: `A_sweep_pays_the_emergency_budget_once_…` (100 ms) and `A_five_order_sweep_answers_within_the_budget_…` (1000 ms) hold their whole margin IN FRONT of the book read, where `BeginCompositeAsync` does one SELECT and no commit — the same 10 ms probe shows a read still fits there — and more disk only produces the branch they assert; `A_five_order_sweep_carries_a_mix_…` already takes `SweepBudget`, has 17.75 s of room and asserts that bound itself; `The_simulators_two_latencies_add_up_…` is one direct connector call with no gateway and no database in its path.

**Verified by running (quoted):** gate at `0fd3f14`, whose `src/` and `tests/` are byte-identical to `aa16362` (the rebase over `9f8f878`
brought docs only). Release: 17 projects, 0 warnings, 0 errors; Integration 3× → 663 passed/1 skipped/664 each (11 m 2 s, 11 m 1 s, 11 m 2 s);
Unit 1029 + Fault 277 + Integration 663 = 1969 passed, 0 failed, 1 skipped; names vs `main` → 0 removed, 0 added (sets 1611 = 1611).
Runner: draft PR #20 (closed), run 34872880789 at `0fd3f14` — **windows-latest SUCCESS** (24 m 6 s; outside-Timing Unit 1028 8 m 20 s, Integration 575+1/576 13 m 16 s, Fault 272 13 m 27 s; Timing 88/88 first attempt, no retry), ubuntu-latest SUCCESS (12 m 16 s), macos-latest FAILURE (7 m 19 s) on `BridgeRoundTripTests.A_newly_arrived_silent_peer_…_refusal`, `System.TimeoutException : condition was
not met in time` — a file this diff does not touch, the hosted-runner class twice on the record, green here in 498 ms alone and in all three
Integration passes. Windows Integration 12 m 37 s → 13 m 16 s (+39 s), a run whose untouched Fault project moved +2 m 5 s. SECOND AND LAST
run, 34876672818 at the report tip `8e7e2ae`
(`src/` and `tests/` identical to `0fd3f14`): **all four jobs SUCCESS** — windows 23 m 26 s (Unit 1028 7 m 16 s, Integration 575+1 12 m 21 s,
Fault 272 11 m, Timing 88/88 first attempt), ubuntu 12 m 3 s, macos 14 m 34 s with the `BridgeRoundTripTests` red of run 1 green, `package` 4 m 42 s.

**NOT done:** no product code, no `Timing` membership, no box, no ATAS, no money; the macos red is not diagnosed beyond "not this diff".
