# U-peer-row-ubuntu — a peer-row fixture that times out on a hosted runner while the product it asserts is green on the other two
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph — membership argued at the test with measured numbers, an assertion never loosened; "The
fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-14 — U-sweep-latency-win landed` section of `BUILD-STATUS.md`, then
`tests/TradeAgent.IntegrationTests/PeerRowTests.cs:140-175` (the test, its `AuthGrace = 300 ms`, the class's `Wait` helper and its timeout, `SilentAsync`,
`PeerAsync`), `src/TradeAgent.AtasBridge/AtasConnector.cs` (`Unauthenticated`, `StatusDetail`, the auth grace and the "neither proved itself nor said"
sentence), the sibling `A_silent_peer_that_later_fails_the_challenge_…`. Branch `u-peer-row-ubuntu`, worktree `~/Projects/ai-trading-software-for-mihael-
worktrees/U-peer-row-ubuntu`, rebased onto `main` first. Test-only unless the product is wrong; no assertion loosened; no box, no money.

**The red.** Run 34881215351 at `4961989` (a test-only sha whose diff is `SweepRequestIdTests.cs` alone): ubuntu-latest, outside the `Timing` category, no
retry: `PeerRowTests.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure` → `System.TimeoutException : condition was not met in
time` (log 18:31:45Z, 21 s into the suite). Windows and macos green on the same sha. The same test went red on macos-latest in draft PR #20's first run
(34872880789, green on the re-run) and once in a builder's full local run (`BUILD-STATUS.md:3771`). Three sightings, three machines, never twice in a row.

1. **Judge it first:** find which `Wait` times out (the first, on `Unauthenticated`, or the second, on the "neither proved itself nor said" row) by
   reproducing on this Mac under load (the other suites running, or a CPU hog) and, if it will not reproduce, on a draft PR with a temporary print of
   the connector's state at the timeout — the print removed before the report. Quote what the connector said when the wait gave up.
2. **Then fix once:** if the fixture races the connector (a grace or a heartbeat measured against the runner's clock, a wait shorter than the grace it
   waits on), size the fixture by arithmetic at the test, `PressBudget`-style, and sweep the class for siblings of the same shape (name each moved or
   not at risk and why); if the product masks the new peer behind the old refusal under load, that is a RED-first test and the smallest product
   fix. `Timing` membership only if the verdict needs the runner's clock, argued at the test with the numbers.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Integration project 3× and the three test projects in Release to a
file → 0 failed; names vs `main` → nothing removed; a draft PR's runners green at the tip (run id quoted; at most two runs; close it after the report).
Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, which wait and what the connector said, what moved
and what did not, the run id, what you did NOT do.

## Report
Tip `179e23c` + this, 3 commits, TEST-ONLY (`git diff main -- src/` empty): `Harness.cs` +71, `PeerRowTests.cs`, `BridgeRoundTripTests.cs`. No assertion added, removed or altered, no timeout
raised, **no `Timing`**: the verdict needs no runner clock, only that the peer reached the connector at all.
1. **THE SECOND WAIT**, on the new peer's silence; the first never timed out. The shipped body did not reproduce in 60 iterations under 12 hogs plus another worktree's suite; with that wait
SPUN rather than polled every 50 ms — what the poll is worth where the connector's own teardown loses the CPU — it failed **40 in 40**, all on the second wait, the row still reading the
previous peer's `… could not prove it holds this installation's bridge secret (…/bridge.auth)` with `quiet.IsConnected = True`, and **40 in 40 recovered** the moment a second client
connected (hogs killed, `pgrep -x yes` → 0). MEASURED MECHANISM: off Windows a pipe is a Unix-domain socket — a connect to the single BUSY instance succeeds in **0 ms** into the backlog and
dies when the accept loop's `finally` disposes that instance, so the next instance never sees the peer (nothing in 2000 ms) while the client still reports itself connected; a read on the
orphan settles at once with **0 bytes**, on a live peer not within 500 ms. Windows retries on ERROR_PIPE_BUSY, hence green there.
2. **MOVED, 3 of 8:** `A_newly_arrived_silent_peer_…` (the red) and `A_peer_inside_the_auth_grace_…` hand over through `HandOver.ToASilentPeer`, reconnecting on that end of stream and ONLY
on it, so real masking still fails, with the row quoted; `An_authenticated_peer_that_has_not_said_hello_…` through `PeerAsync`, which redials an unanswered challenge (8 attempts), as
`Redial` already does. **NOT AT RISK, 5 of 8** (`…speaks_v2`, `…fails_the_challenge`, `…compatible_hello`, `The_connecting_line_…`, `…inside_the_grace`): one connection each on a fresh
connector with nothing being disposed, where a connect that beats the accept loop gets ENOENT, which the client's own retry covers — as it does the wrong-proof peer's single dial. **BEYOND
THE CLASS:** `BridgeRoundTripTests.A_newly_arrived_silent_peer_…_refusal`, identical shape, the macos red of run 34872880789. **GREEN AFTER:** the fixed body, same spin and load, **0 in
40**.
**Gate at `179e23c`** (rebased onto `main` `8cb95de` mid-flight; the first gate, at `4455cd8`, was green too), Release: build `--no-incremental` → **0 warnings, 0 errors**; Integration ×3 →
**668 passed, 1 skipped, 0 failed** each; Unit **1097** + Fault **304** + Integration **668** = **2069 passed, 0 failed, 1 skipped**; names vs `main` → **0 removed, 0 added** (1685 = 1685;
`[Fact]`/`[Theory]` 1709 = 1709). **Runner:** PR #21, both runs ALL FOUR GREEN — 34962898676 at `4455cd8`, **34967216164 at `179e23c`**; closed after. **NOT done:** no product code, so no
RED-first test and no mutant; no `Timing`, no box, no ATAS, no money; the SHIPPED 50 ms-poll body was never reproduced on this Mac (the starved-thread-pool attempt stalled the test host,
killed by PID), so the runner logs stay its only sighting; and the two fixtures moved beside the red have no red of their own, only the same shape and measurement.
