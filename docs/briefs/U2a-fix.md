# U2a-fix — two deadline tests fail on hosted runners and pass on the Windows target

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then only the two tests below and the helpers they use
in `tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs` (2020 lines; do not read it all). Toolchain:
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout` binary. Work in a fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u2a-fix` on a new branch `u2a-fix` from `main` (`6138fdd`).

**What happened.** U2a landed on `main` at `6138fdd`. CI run 33898144843 (`dotnet test -c Release`): windows-latest
green (497), macos-latest and ubuntu-latest each red on ONE different test of this class, 313/314 green otherwise.
The full suite passed twice on this Mac (Debug) and the pipe classes passed on the Windows box at round 7.

- macos: `An_emergency_spends_one_budget_across_the_gate_and_the_write` (~:1026) —
  `Assert.Contains() Failure: Not found: "still being sent"` in `"'cancel-all' is NOT confirmed — check you…"`.
  The fixture pays out 8 KiB every 80 ms and stops after 152 KiB so the holder frame is accepted "at about 1.5 s"; on a
  slow runner it is not, the gate is still held at the 2 s expiry, and the sentence names the gate, not the write.
- ubuntu: `A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped` (~:555) —
  `Assert.Contains() Failure: Not found: "busy"`. The peer reads 1 KiB every 800 ms; the premise assertions
  (accepted > 0 and < 8 KiB during the wait) decide which side of the chunk boundary the run landed on.
- Rerun of the same sha: ubuntu the same test again; macos the same test again plus
  `Local_queueing_under_load_does_not_disconnect_a_healthy_bridge` (312/314) — that test is in scope too. This Mac,
  Release, the whole class twice: 47/47 and 47/47 (3 m 57 s each). Per-runner deterministic, not a coin flip.

**Rules.** Never shorten a shipped deadline or a grace to make a test pass (the deadlines are the product). Every
premise assertion stays: a test that can no longer tell the busy case from the stalled case proves nothing. The class
under test is unchanged unless you find a product defect, in which case say so first and fix it red-first with a
mutant. The fix is in the tests or the fixtures: pace the peer relative to what the runner actually accepts, or wait
for the fixture's precondition (holder frame fully accepted; at least one sub-chunk read) before starting the timer,
or both. If a test genuinely cannot be made runner-independent, move it behind an xUnit trait `Timing` that
`.github/workflows/build.yml` filters OUT on ubuntu and macos and keeps on windows, with the reason in the test's
summary comment; that is the last resort and the report must say why the first two did not work.

**Proof.** The whole class 3× in a row locally in Release (`dotnet test tests/TradeAgent.IntegrationTests -c Release
--filter "FullyQualifiedName~ConnectorSendDeadlineTests"`), counts pasted; then the full suite once in Release; then
push the branch and open a draft PR so CI runs on all three platforms — paste the three job conclusions. Commit per
test, one-sentence messages, no `Co-Authored-By` trailers, no other worktree, no box.

## Report

Append here as you go and commit it, ≤20 lines: tip sha; per test what the run-to-run variance actually was and what
you changed; the 3× local counts; the full-suite counts; the CI conclusions per platform; what you did NOT do.
Verified by running, or NOT VERIFIED; no hedging words.
