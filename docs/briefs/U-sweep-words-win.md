# U-sweep-words-win — the five-order sweep test expects a `confirmed` leg the Windows runner reports as `sent-not-confirmed`

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (the five per-leg words; when a
leg reads `sent-not-confirmed`), the U2c1c and U-press-budget sections at the end of `BUILD-STATUS.md`, then the test
below. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. No box; a draft PR is your only
Windows run. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-sweep-words-win`, new branch
`u-sweep-words-win` from `main`.

**What happened, twice.** windows-latest, `SweepRequestIdTests.A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`:
`Assert.Contains() Failure … Not found: "confirmed"`, collection `["sent-not-confirmed", "rejected", "not-sent",
"not-sent", "not-sent"]` — run 33966990967 at `572c2be` (docs-only commit) and the first attempt of run 33970406089 on
PR #6; green on the re-run both times, green on ubuntu and macos every time. The test is outside the `Timing` category,
so the windows retry never reaches it. The leg that should read `confirmed` reads `sent-not-confirmed`: the fake
broker's answer arrived after whatever deadline the sweep gives a leg, on a slow runner — and U-press-budget just made
the simulator clip its latency to the deadline like the shipped connector, which can only ever report
`PossiblyWritten` on the clipped leg.

1. **Establish which word the product owes.** For a leg the fake FILLS before the sweep's deadline, `confirmed`; for a
   leg whose answer arrives after it, `sent-not-confirmed` is the honest word (the contract's `PossiblyWritten`). If the
   test's fixture gives that leg a latency near the deadline, the multiset is runner-dependent by construction: move
   the fill well inside the deadline (a fixture latency that no runner can push past it, measured against the
   runner-speed probe's factors), keep the five distinct words the test exists to show, and assert the multiset exactly.
   RED first: reproduce with a seam that delivers the fill late → the CI collection above; GREEN; mutant (the word
   for a late fill read `confirmed`) → RED.
2. **If the product misreports** — a fill that DID arrive inside the deadline read as `sent-not-confirmed` — say so
   first and fix it red-first in the gateway's leg classification, with a mutant.

Yours: `tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs` and its fixtures; the gateway's leg-word code ONLY
if item 2 applies. Commit per item, no trailers, no other worktree. Gate: Release `--no-incremental` → 0 warnings; the
class 3×; full suite once; push, draft PR; the windows job green twice in a row.

## Report

- Item (1). Code tip `90c40f4`, rebased onto `main` cda7088 (U-press-budget landed under me); the only later commit is this report, docs-only.
- The brief's mechanism is wrong, verified: the failing CI test ran in `[1 s]` against a 5 s budget, so no deadline passed.
- What it is: the lost answer flags the store while later legs of the SAME wave are still short of `ReauthorizeAtDispatchOrThrow` — which runs AFTER the awaited target resolution — and their errors read `1 earlier request(s) are unconfirmed`.
- Not (2), so no product file is touched: such a leg (record CREATED, no transport) IS `not-sent` by the `docs/CONTRACTS.md` table.
- RED, verified: the same sweep at `LatencyMs=1` lost a `confirmed` in 61 of 150 runs, one giving exactly the CI multiset `{sent-not-confirmed, rejected, not-sent x3}`; clean over 100 legs at 2 ms.
- So the wave's issue spread is under 2 ms here, and 2 x 39.52 — the worst windows file-IO factor the workflow records — is 79 ms, against the 50 ms the fixture gave it.
- GREEN: 750 ms of room inside a 20 s budget, the multiset asserted exactly, and both fixture bounds asserted by name so the next runner to push past one says which.
- Gate, verified: Release `--no-incremental` 0 warnings; the class 3x = 44/44, 44/44, 44/44; full suite 219+239+582 = 1040 passed, 0 failed.
- Mutant, verified: `Dispatched(t) => TheAnswer(LegOutcome.Confirmed, t)` -> RED, `Actual: ["confirmed","confirmed","confirmed","not-sent","rejected"]`. Reverted.
- U-press-budget's clip does not reach this fixture — it fires only when the wait exceeds what is left, and this sweep spends 3 x 750 ms inside 20 s. Verified by running the class rebased ON that code, not by reading it.
- Windows job green four times running, verified: 33973434576, 33974221120, and 33974342472 attempts 1 and 2 at the code tip. Whole run green on all three platforms on 33974221120 and on 33974342472 attempt 1.
- NOT mine, NOT fixed, both intermittent and both briefed elsewhere: macos on 33973434576 failed `A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` (NRE at `(JsonElement)reply.Data!`; its fixture leaves 100 ms between the scope and a 1900 ms read), and ubuntu on 33974342472 attempt 2 failed U-press-budget's own `A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline` by 24 ms. I did not merge, did not push to `main`, and touched no other worktree.
