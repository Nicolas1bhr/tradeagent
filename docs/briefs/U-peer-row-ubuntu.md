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
