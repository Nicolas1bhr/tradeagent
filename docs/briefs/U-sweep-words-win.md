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

## Report — append as you go, commit it, ≤12 lines: tip sha; which of (1)/(2); RED → GREEN → mutant; counts; the two
windows runs; what you did NOT do. Verified or NOT VERIFIED.

## Report

- Item **(1)**, and the brief's mechanism is wrong: it is not the deadline. Verified: the failing CI test ran in
  `[1 s]` against a 5 s budget, so no deadline passed; the `not-sent` legs carry `1 earlier request(s) are unconfirmed`.
  The lost answer settles UNKNOWN and flags the store while later legs of the SAME wave are still short of
  `ReauthorizeAtDispatchOrThrow` — which runs after the awaited target resolution, on purpose. No (2): a leg refused
  before dispatch, record CREATED, no transport, is `not-sent` by the `docs/CONTRACTS.md` table. No product change.
- RED, verified: the same sweep at `LatencyMs=1` lost a `confirmed` in 61 of 150 runs and one run produced exactly the
  CI multiset `{sent-not-confirmed, rejected, not-sent x3}`; at 2 ms, 100 legs clean. So the wave's issue spread here is
  under 2 ms, and 2 x 39.52 (the worst windows file-IO factor the workflow records) is 79 ms > the 50 ms it was given.
- GREEN, verified: latency 750 ms, budget 20 s, the multiset asserted exactly, and both fixture bounds asserted by name.
  Release `--no-incremental` 0 warnings; the class 3x = 44/44, 44/44, 44/44; full suite 219+238+582 = 1039 passed, 0 failed.
- Mutant, verified: `Dispatched(t) => TheAnswer(LegOutcome.Confirmed, t)` -> RED, `Expected: ["confirmed","confirmed",
  "not-sent","rejected","sent-not-confirmed"] Actual: ["confirmed","confirmed","confirmed","not-sent","rejected"]`. Reverted.
- U-press-budget's `TheCancellableWait` clips only when the wait exceeds what is left; this sweep spends 3 x 750 ms inside
  20 s, so it cannot fire. Verified by READING `git show u-press-budget -- .../FakeConnector.cs`; NOT VERIFIED merged.
- Windows job: NOT VERIFIED YET — pushed, draft PR opened, awaiting two consecutive greens.
- NOT done: no product file touched, no other worktree, no merge, no push to `main`.
