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
