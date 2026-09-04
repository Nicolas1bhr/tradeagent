# U-win-flakes — a different integration test fails on windows-latest each run, all of them teardown or timing shapes

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then only the tests and helpers named below.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-win-flakes` on a new branch `u-win-flakes` from `main`. No box;
CI on your draft PR is your only Windows run — think before each push, and read the trx, not only the job colour.

**What happened.** With U14-win's three harness fixes in, windows-latest still fails one integration test per run,
a different one each time, 518/519; ubuntu and macos are green. Two are measured:
- Run 33924375698: `GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled`
  (~:1308) — `Assert.Equal() Failure: Expected 0 / Actual 1`: one row still unsettled after disposal landed inside a
  close-all wave. On Windows the pipe's disposal ordering differs from macOS; whether the product leaves a row or the
  test reads the store before the last settle lands is the first thing to establish.
- Run 33923259460: `AtasProtocolTests.Capabilities_and_accounts_come_from_the_bridge_handshake` —
  `InvalidOperationException: The stream is currently in use by a previous operation` from `StreamWriter.DisposeAsyncCore`
  inside `StubBridge.DisposeAsync` → `Quietly(step)`: the test's stub bridge disposes its writer while an async write is
  in flight, and `Quietly` does not catch that exception type. That one is the harness, whatever the platform.
- Earlier runs on `main` never showed these two; they surfaced once the witness reds stopped masking the job.

**Rules.** The product is not changed unless a failure is the product's (the first one may be: a row left DISPATCHING
after disposal is exactly what U2a's `handlers_did_not_finish` sentinel exists for). If it is, say so first and fix it
red-first with a mutant, in the gateway or pipe server only. Otherwise the fix is in the harness: `StubBridge.Quietly`
swallows the dispose-in-flight case (or awaits the pending write first); a test that reads the store after disposal
waits for the settle it asserts rather than assuming ordering. Never shorten a shipped deadline. Every premise
assertion stays. `Skip` is the last resort, with the reason in the summary comment and in the report.

**Proof.** Each touched test 3× locally in Release; its class once; the full suite once in Release; push `u-win-flakes`,
open a draft PR, and read the windows trx: paste the three job conclusions and, for windows, the count and the names of
any failed tests. Two consecutive green windows runs on the PR are the acceptance (re-run the job once). Commit per
test, one-sentence messages, no trailers, no other worktree.

## Report — append as you go, commit it, ≤20 lines: tip sha; per test what was measured and what changed (product or
harness); local counts; CI per platform, two windows runs; what you did NOT do. Verified or NOT VERIFIED.

Code tip `bcaf4cf` (report on top); rebased onto `main` `939fd89` mid-unit — U2c1a landed, clean. Draft PR #3, CI only,
NOT merged. Both fixes are HARNESS; no product code changed.
**`Capabilities_and_accounts_come_from_the_bridge_handshake`.** Measured, not inferred: a `StreamWriter` disposed with a
write in flight throws `InvalidOperationException: The stream is currently in use by a previous operation` — reproduced
here against a pipe whose far end never reads — and `Quietly` caught only `IOException`/`ObjectDisposedException`. So
`StubBridge.DisposeAsync` waits (bounded, 5 s) for its loop to release the writer, and `Quietly` covers that exception
and `TimeoutException`. VERIFIED 3x Release; class 7/7.
**`A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled`.** NOT the product: the gateway did what it
documents — the drain expired, step 4 cancelled, step 6 logged `handlers_did_not_finish`. The fixture's margin was wrong.
Measured: `close-all`'s prefix is five 500 ms calls and its first leg reaches the broker at 3028 ms against a 3200 ms
emergency budget (172 ms), and the drain derived from that budget is 6300 ms against a 4557 ms wave; the budget is now
12 s, which a healthy run never waits out. MUTANT: a 2 s stall injected once the wave is issued fails at 3200 ms with the
CI assertion exactly (`Expected: 0 / Actual: 1`, sentinel `unsettled:1`) and passes at 12 s. VERIFIED 3x; class 32/32.
**Gate, Release, rebased tip:** `--no-incremental` 0 warnings / 0 errors; 201 + 191 + 520 = 912, 0 failed. **CI, run
33929007448:** ubuntu SUCCESS, macos SUCCESS, windows SUCCESS (201 + 191 + 520, 0 failed), and the windows job re-run on
that sha SUCCESS with the same counts — two consecutive green windows runs, so acceptance is met.
**NOT done.** No deadline shortened, no `Skip`, no premise assertion removed, no box, no other worktree, not merged.
`Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows`, the third flake at `939fd89`, is UNTOUCHED and outside
this brief: measured over five runs at drain 1430 ms / disposal 1455-1469 ms, its handler settles in ~30 ms against a
300 ms margin — not the 6 % shape these two were; U2c1a's item 0 as its cause is NOT INVESTIGATED. It passed both runs.
