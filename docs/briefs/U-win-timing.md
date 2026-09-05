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

Tip `0ee01ef`; this report is the only commit after it and the code diff against it is empty. The predecessor's probe and its premise
fix are kept and were checked against the product; its 2 s settle margin is replaced — the runner measured it wrong twice.

**The factor, from the probe's own runs on this PR** (ratios against this Mac): windows cpu 1.06-1.22, timer 1.02-1.18, pipe 2.24-4.27,
**file-io 3.79 / 39.52 / 4.89 / 5.69**; ubuntu all under 1.6 (file-io 0.13-0.74); macos under 1.6 but **timer 3.26 / 4.58**.

**Option (b), on two measured grounds.** File IO is what these failures are made of and it spread tenfold across four runs — one scale
covering the worst needs >40x, past the ceiling `TestTime` itself refuses, applied to fixtures bounded at 1.08x — and one of the two
instances was a missing premise, not a margin, which no scale fixes. `TA_TEST_TIME_SCALE` is NOT set in CI; the probe prints `TA_TEST_TIME_SCALE-in-effect=1.00` every run to prove it.

- `Trait("Category","Timing")` on `ConnectorSendDeadlineTests` and `GatewayPipeBackpressureTests`, the only two classes with
  windows-only reds (4 + 2 tests, three fix units). `build.yml` runs `Category!=Timing` with no retry on all three platforms, then
  `Category=Timing` re-run ONCE on windows-latest alone — first failure printed, named in the job summary, annotated and uploaded as its own trx. It caught a real one on its second windows run, the next item.
- `Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` failed AGAIN at 2 s (33941113025 attempt 2, `Expected: null /
  Actual: "error"`). The fixture now shortens nothing: the settle window runs at the shipped 5 s, the slow call grows to 12 s so the
  derived 6.1 s drain still expires inside it, the record wait to 90 s against a measured 36 s. Mutant (1 ms settle) → RED, that message.
- `OurWriteIsOver` leaves its own request pending and the first half read `PendingRequests` on the instant. Mutant (200 ms between the
  two frames) → RED `Expected: 0 / Actual: 1` at line 1778; green with the mutant still in.

**Local Release, 0 warnings:** `Category=Timing` 3x → 81/81 each; `Category!=Timing` → 201 + 188 + 444 = 833; 0 failed, 0 skipped, and
`--list-tests` splits 914 as 833 + 81 exactly. Un-rebased branch — CI tests the merge with `main`, where U2c1c's six extra tests make it
920. **windows-latest on `0ee01ef`, run 33943343018 attempts 1-3: all green, 201 + 188 + 445 + 86 = 920, 0 failed, 0 skipped, no re-run used.**

**NOT done:** no product code; nothing shipped shortened (one un-shortened); no premise assertion dropped; no `Skip`; (a) not wired into
CI, `TestTime` left as the knob for a deliberately slowed local run; no box; no rebase; not merged.
