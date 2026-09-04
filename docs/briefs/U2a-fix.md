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

Tip `047ebfb` + this report commit. Product code UNCHANGED at every commit; 314 integration tests before and after.
The variance is per-platform, not run-to-run, and I measured it: a probe (NamedPipeServerStream, shipped 8 KiB buffer,
1 KiB writes) gives the WORST gap between two completed writes while a peer drips 1 KiB/800 ms — macOS 1.60 s, Linux
5.61 s, against the 2 s an emergency watches. Ubuntu's failure reproduced locally in a Linux container; every fix was
verified there and on this Mac.
- `A_peer_reading_below_one_chunk_per_window` (ubuntu, deterministic; reproduced RED). The peer takes ONE 7 KiB gulp
  mid-window instead of dripping — still under one old 8 KiB chunk, so both premises (`>0`, `<8 KiB`) and the verdict
  stand. Pacing cannot work: at 4 KiB/s, the fastest drip under that ceiling, Linux's worst gap is still 1.77 s.
  3/3 Linux, 5/5 macOS. Mutant `WriteChunkBytes = 8192` → RED, "Not found: busy".
- `An_emergency_spends_one_budget…` (macos). Nineteen paced 80 ms reads → one 1.2 s wait the test holds, plus two new
  premise asserts on when the gate was released. Mutant (write given a fresh clock after the gate) → RED at 3.21 s.
- `Local_queueing_under_load…` (macos). No safe deadline exists: this Mac drains all 300 calls in 220 ms (≥250 ms ⇒ no
  contention, 3/3 RED) while Linux at 0.5 core needs >50 ms for one chunk (3/5 RED). The bridge now costs 20 ms a
  quote (`QuotesAtAPace`), a 6 s machine-independent floor, deadline 2 s. 8/8 at 0.5 core, 8/8 at 2 cores, 5/5 macOS.
  Mutant (drop on gate expiry) → RED.
Class 3× Release here: 47/47, 47/47, 47/47 (3 m 56/56/57 s). `dotnet build TradeAgent.sln -c Release --no-incremental`
→ 0 Warning(s), 0 Error(s). Full suite Release: 75 + 108 + 314 = 497 passed, 0 failed.
CI 33905797433 (draft PR #1): ubuntu SUCCESS, macos SUCCESS, windows FAILURE — two `WitnessSnapshotTests`/
`CoidWitnessTests` harness IOExceptions that fail identically on `main` (run 33905102658); my three passed there.
NOT done: no product change, no `Timing` trait, no workflow change, no other worktree, no box, no merge.
