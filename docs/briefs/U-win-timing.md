# U-win-timing — the CLASS behind the one-test-per-run Windows reds: timing fixtures tuned on fast machines

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the U2a-fix, U-win-flakes and U2c1b sections of
`BUILD-STATUS.md`, and `tests/TradeAgent.IntegrationTests/Harness.cs` (parallelization is ALREADY off:
`[assembly: CollectionBehavior(DisableTestParallelization = true)]` — contention between tests is not the cause).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-win-timing`, new branch `u-win-timing` from `main`. No box;
CI on your draft PR is your only Windows run — read the trx, not the job colour.

**What happened.** After U2a-fix (three tests) and U-win-flakes (two tests) each fixed their instances, windows-latest
still fails ONE integration test per run, a different one each time, 519/520; ubuntu and macos are green. The
instances so far: `ConnectorSendDeadlineTests.A_caller_that_cancels_an_emergency_releases_its_slot_and_still_counts_a_late_answer`
("the connection was judged on a cancellation that came from this side", run 33931934317);
`GatewayPipeBackpressureTests.Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` (`Expected null /
Actual "error"`, run 33927117880 — its handler settles in ~30 ms against a 300 ms margin, measured 5×). All are
fixtures that pace a peer or wait a margin against a shipped deadline, tuned on this Mac and the box; the hosted
two-core runner is slower and noisier by a factor nobody has measured.

**Rules.** Never shorten a shipped deadline or grace; never drop a premise assertion; the product changes only if a
failure is the product's, said first and fixed red-first with a mutant. Fix the CLASS, not the next instance:
1. Measure first: a probe test (kept, `Trait("Category","Timing")`) that records on each platform the ratio between a
   fixed CPU/IO workload's duration here and on the runner — the number goes in the report and in the class's summary.
2. Then ONE of: (a) a fixture-side scale (`TA_TEST_TIME_SCALE`, default 1, set in `.github/workflows/build.yml` for
   windows-latest to the measured factor) applied to fixture waits and margins ONLY, never to product deadlines; or
   (b) a `Timing` trait on the timing-sensitive classes and a workflow step that re-runs only that category once on
   windows-latest when it fails, with the first failure still logged. Say why you chose it; (a) is preferred if the
   factor is stable across three runs, (b) if it is not.
3. Acceptance: three consecutive fully green windows-latest runs on the PR (re-run the job), 0 skipped, counts pasted.

**Proof.** Local Release: the touched classes 3×; full suite once. Commit per step, one-sentence messages, no
trailers, no other worktree, not merged.

## Report — append as you go, commit it, ≤20 lines: tip sha; the measured factor per platform; which option and why;
per-instance what changed; local counts; the three windows runs; what you did NOT do. Verified or NOT VERIFIED.
