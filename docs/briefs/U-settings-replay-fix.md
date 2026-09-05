# U-settings-replay-fix — two offline-replay fixtures relied on an empty allowlist meaning "everything"

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then the U-pipe-replay section at the end of
`BUILD-STATUS.md` and the `## Report` of `docs/briefs/U-settings-closed.md` (in the worktree below). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-settings-closed`, branch `u-settings-closed` @ `e83eedb`
(U-settings-closed, complete and rebased over U-pipe-replay, which is on `main`). First `git rebase main` (main has moved
by docs only). You fix the combination; you do not reopen either unit.

**What happened.** Each unit's gate was green alone. At the rebased tip, the manager's gate fails TWO tests, in 4–6 ms:
`OfflineSweepReplayTests.A_completed_close_all_replayed_offline_answers_from_the_store_and_reads_nothing` —
`{"code":"RISK_LIMIT_EXCEEDED","message":"ES is not on the allowed instrument list", …}` — and
`…A_completed_cancel_all_replayed_offline_answers_from_the_store_and_reads_nothing` (`Assert.True() Failure`, the
same refusal one step earlier). U-pipe-replay wrote those fixtures against the default settings of the day, where an
EMPTY instrument allowlist meant everything was allowed; U-settings-closed made an empty allowlist allow NOTHING (the
review's finding 5). Only these two fixtures in the whole suite relied on the old default: 580/582 otherwise green.

1. Make the two fixtures allow the instruments they trade, the way the rest of the integration suite does (look at how
   `GatewayThroughPipeTests` or `SweepRequestIdTests` set their allowlist and use the same helper — do not invent a
   second way). If the shared harness has a default-settings helper that other tests rely on, fix it there once and say
   which tests it touched. RED is the failure above; GREEN; then the tests must still prove what they prove: the
   replayed sweep answers from the store with zero connector calls (re-run the class 3×).
2. Nothing in `src/` changes. If you believe the product is wrong — that an empty allowlist should allow something —
   say so and stop; the review's finding 5 and U-settings-closed's item 2 decided otherwise.

Yours: `tests/TradeAgent.IntegrationTests/OfflineSweepReplayTests.cs` and the shared test harness if item 1 leads
there. Not yours: any product file, any other test's assertions. Commit, one-sentence message, no trailers, no push,
no other worktree. Gate: Release `--no-incremental` → 0 warnings; the class 3×; the full suite once in Release (if a
run ends with exit 1 and NO summary line, re-run it).

## Report — append as you go, commit it, ≤10 lines: tip sha; RED → GREEN; where the allowlist was set and whether a
shared helper changed; the counts; what you did NOT do. Verified or NOT VERIFIED.
