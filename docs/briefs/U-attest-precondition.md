# U-attest-precondition — the F7 probe races its own fixture: the proof is cleared before the harness has opened the gates

Fresh fixer on Opus, ONE item, test-only. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the `U-codex-2b` section at the end
of `BUILD-STATUS.md` (F7: a heartbeat that cannot attest clears the proof), then the two tests below. `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/attest-precondition`, branch `u-attest-precondition` from `main`.
No box; a draft PR is your only hosted-runner instrument.

**What happened.** CI run 34014790766 at `07cbb91` (the U-codex-2b merge), ubuntu, Integration 521/523:
`BridgeRoundTripTests.Capabilities_do_not_outlive_the_bridges_ability_to_attest_them` [300 ms] — `the harness never
authorized autonomous dispatch, so losing it proves nothing` (`BridgeRoundTripTests.cs:384`). The same sha passed the
class 3× and the full suite (1152) on this Mac. Read the fixture: `ThrowsAfterHandshake` makes `Describe()` throw on EVERY
heartbeat after the handshake, at `HeartbeatInterval = 100 ms`, so the proof the test asserts at `:365` is cleared by the
first bare pulse ≈100 ms later — and the gateway construction that follows (`TestEnv.NewDb()`, a `TradingGateway` over
SQLite at `synchronous=FULL`, `Update`, `ActivateLive`) must finish inside that window for the precondition at `:384` to
read the proof as present. On this Mac it does; on ubuntu it did not. The test asserts a schedule, not the product.

1. **Open the gates BEFORE the bridge stops attesting.** Use the sibling's fixture (`ThrowsWhileTold`, as in
   `A_bridge_that_can_describe_itself_again_is_believed_again`): the handshake and the heartbeats attest until the
   harness has built the gateway, opened every gate and asserted `TryAuthorizeExecution` true; only then flip
   `Throwing = true`, and the poll to the deadline follows unchanged. No assertion is loosened, nothing in the product
   changes, and the test still fails against the pre-F7 code (prove it: revert `4c68a11`'s product change in your
   worktree, run the test, quote the RED, restore). Then run the class 20× in a loop here.
2. **Sweep the two F7 tests and the fixer's hammer** for any other assertion that depends on finishing inside one
   heartbeat interval; fix the same way, or say there is none.
3. **Draft PR**, the whole matrix green twice, ubuntu included; quote the run ids.

Commit per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; the class 3× and the test 20×; full suite
once to a file (regardless of other test hosts; a `Category=Timing` failure → that class alone 3×, quoted); names vs
`main` 0 removed.

## Report — append here, commit it, ≤12 lines: tip sha; the RED against the pre-F7 product quoted; the 20× loop; the
sweep's answer; gate counts; the two PR runs; what you did NOT do.

Code tip `67eae98` — one commit, `tests/TradeAgent.IntegrationTests/BridgeRoundTripTests.cs` only, +19/−5; the branch tip is this docs-only report commit on top of it, so every figure below is measured at `67eae98`. **1 — the gates open before the bridge stops attesting.** `ThrowsWhileTold` replaces `ThrowsAfterHandshake`, so the handshake and every heartbeat attest while the harness builds the SQLite gateway, opens the four health rows and activates live; `adapter.Throwing = true` now comes after `Assert.True(gw.TryAuthorizeExecution(…))`, and the 10 s poll and every assertion are untouched. No product file changed, nothing loosened, nothing removed.
**Still RED against the pre-F7 product** — `4c68a11`'s two edits to `AtasConnector.cs` (the heartbeat's `_hello = Attested(f)`, and the `PendingHello` wording) reverted in the worktree, run, then restored with a file-scoped `git checkout --`, tree clean: `after 10.0s of pulses that cannot describe the bridge: … coid=True history=True provable=True` / `autonomous dispatch : authorized=True` → `Assert.False() Failure  Expected: False  Actual: True` at `BridgeRoundTripTests.cs:427`.
**20× loop: 20/20 green, 41/41 each, 0 red** (the class, Release, the cut-0.1.2 leg's own Integration suite running alongside for the first 16). On the final gate build: the class 3× → 41/41 each, and the test alone 20× → 20/20 at ~141 ms.
**The brief understates the fault.** At `07cbb91` the red came back on **all three** hosted runners, not ubuntu alone — windows, ubuntu and macos each `Failed: 1, Passed: 521, Total: 523`, the test dead in **300 ms** on the precondition (CI 34014790766). Fixed, it passes in 120 ms (ubuntu), 294 ms (macos), 366 ms (windows), read off the runs' own trx.
**2 — the sweep's answer: there is none.** Every `IAtasAdapter` in the suite was read; only `ThrowsWhileTold` and `ThrowsAfterHandshake` can throw from `Describe()`, and `ThrowsAfterHandshake` now has one user, `A_failing_capability_read_does_not_stop_the_heartbeat`, whose every assertion runs after a deliberate 1500 ms delay and asserts the **cleared** state — waiting longer only makes it truer. `A_bridge_that_can_describe_itself_again_is_believed_again`, `A_heartbeat_at_a_version_this_build_does_not_speak_attests_nothing` and the hammer `No_reader_ever_dereferences_a_hello_the_heartbeat_has_cleared` all drive `StubBridge`, which beats only when told, so no background pulse can clear a precondition there.
**One near-miss, named and deliberately left alone:** that same test's `Assert.Equal(HealthState.READY, await connector.GetHealthAsync())` needs a beat inside `HeartbeatTimeout` = 600 ms against a 100 ms interval — a six-beat **liveness** margin, not a precondition race. It is the assertion the test exists for, it cannot be ordered away, and loosening it is forbidden. Not seen red on any runner.
**Gate at `67eae98`, Release:** `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 warnings, 0 errors**, 17 assembly outputs emitted (the integration test dll's mtime 9 s old, so it really recompiled); full suite once to a file with **0 other test hosts** (`pgrep -fl testhost.dll`): **281 + 261 + 610 = 1152 passed, 0 failed, 1 skipped**, exit 0 — no `Category=Timing` failure, so no class re-run was owed. Names vs `main` via `dotnet test --list-tests` on both trees (main's copy of the one changed file built in place, then restored): **1153 = 1153, 0 removed, 0 added.** Secret scan of the whole diff vs `main`: clean.
**3 — draft PR #10, and the matrix is green twice at the tip.** `34017814011` @ `67eae98`: **all four jobs SUCCESS, run_attempt 1, both test steps green first time**. `34015391617` @ `67eae98`: **all four jobs SUCCESS**, but windows needed a job rerun — its first attempt was red on `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` (`Assert.NotEmpty() Failure` at `:652`), a test this unit does not touch. Also `34015304579` @ `4767594` (this fix, comment-only difference): all four jobs SUCCESS first attempt; and `34016321810` @ `67eae98`: ubuntu and macos SUCCESS, windows red on a third unrelated test. **The fixed test passed on every runner in every one of those runs — ten runner-passes, no exceptions.**
**Two new hosted-runner reds for the record, neither mine, neither previously written down, both windows-latest, both outside `Timing` so neither got the workflow's retry:** `SweepRequestIdTests.Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled` (same family as `U-sweep-words-win`'s two, a different test) and `CoidWitnessTests.A_vanished_temp_is_not_waited_for`. Whether either is a fixture asserting a schedule or a real product fault is **NOT VERIFIED** — out of this unit's scope, and each wants its own brief.
**NOT done:** no product code touched, no assertion loosened, removed or re-worded, no test name changed; the two windows reds above were diagnosed only far enough to name them, not fixed; no `Timing` membership argued or granted anywhere; no box, no ATAS, no UI, no `BUILD-STATUS.md` section; **not rebased** — `main` moved `840d0fc` → `ecf7e89` while this ran, on `BUILD-STATUS.md` alone, so the rebase is conflict-free and is left to the manager rather than taken here, because rebasing would move the sha the four CI runs name; the draft PR is left open and unmerged and nothing was pushed to `main`; this report commit is docs-only and its own CI run is not awaited.
