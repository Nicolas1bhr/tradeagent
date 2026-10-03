# U-fix-bridge-heartbeat — the "a failing capability read does not stop the heartbeat" test went red on hosted macOS on a docs-only sha: find why; if the pulse can really stop, fix the product
**Protects:** the connector's liveness truth, on the money path (connectors) — a healthy ATAS bridge must not be declared dead, and a dead one must not read
alive; the test's own summary: a throw in `Describe()` must degrade to the plain pulse, never stop it (`CLAUDE.md` rule 3 keeps an ambiguous state UNKNOWN,
not a guess). **A FRESH FIXER, seat P's queue, ranked by the orchestrator.**
**Evidence (RUN, `gh run view`, 2026-10-03):** `main` `5427746` (a docs-only dispatch commit), run 37058319403, macos-latest job 111008396929:
`BridgeRoundTripTests.A_failing_capability_read_does_not_stop_the_heartbeat` failed [1 s] with "Assert.True() Failure / Expected: True / Actual: False";
Integration 1 failed / 611 passed / 1 skipped; ubuntu-latest and windows-latest green; the same build tree green on all three at `d0bc32f` and after.
First sighting in `fleet/ci-ledger.md` and `BUILD-STATUS.md`.
**Facts to start from (SOURCE at `84a4664`; facts, not a diagnosis):** the test (`tests/TradeAgent.IntegrationTests/BridgeRoundTripTests.cs:307-333`) gives
the connector `HeartbeatTimeout = 600 ms` (`:311`) and the bridge `HeartbeatInterval = 100 ms` (`:316`) over an adapter whose `Describe()` throws after the
handshake (`ThrowsAfterHandshake`, `:272`), waits `Task.Delay(1500)` (`:321`), then asserts `IsConnectedAsync()` (`:324`, the first assert after the delay),
READY and no reconciliation proof. The connector judges liveness by `DateTimeOffset.UtcNow - lastHeard > HeartbeatTimeout`
(`src/TradeAgent.Connectors.Atas/AtasConnector.cs:557`; a third of the timeout at `:538`; default 15 s at `:572`); the pulse is sent by
`src/TradeAgent.AtasBridge/BridgeServer.cs`'s heartbeat loop.
**THE PRODUCT QUESTION, answered with evidence:** can the bridge's heartbeat stop, or stall past the timeout, when `Describe()` throws — a race in the loop
or in the frame that carries the capabilities — or did the runner starve a 100 ms loop for more than 600 ms? If the PRODUCT: fix it with a red-first test
that is deterministic (drive the throw and the clock through seams; no sleeps that wait for luck) and one watched mutant, quoted (money path). If only the
RUNNER: say so with measured numbers from macOS runs (the gap between pulses at the failure), and only then argue the test into `build.yml`'s `Timing`
category at the test itself, per that file's rule — membership is argued, never granted to whatever went red.
**Reproduce on CI** (`fleet/bin/ci-dispatch.sh`; read the macos job); this Mac is macOS too, but its green proves nothing about the hosted runner. Diagnostic
runs may narrow `.github/workflows/build.yml` on your branch (macos only, the class looped) and print each pulse's send and receipt times — branch only,
removed before the proving run, each named in the report. The Windows box is NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` (the fresh-fixer rule; the `Timing` rule in `build.yml`); `BridgeRoundTripTests.cs` (the class, its `Wait`
helper `:1927-1936`, the neighbour `Capabilities_do_not_outlive_the_bridges_ability_to_attest_them`); `BridgeServer.cs` (the heartbeat and `Describe` path);
`AtasConnector.cs:520-580`; `docs/RESUME-HERE.md` trap list for the bridge (the 2026-09-01 `SendRaw` freeze).
Must NOT: raise `HeartbeatTimeout`, the interval or the delay to make it pass; add a retry; skip it on macOS; weaken an assert; move it into `Timing`
without the measured argument; change how a dead bridge is declared except to fix a demonstrated defect.
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from a macOS run: the pulses' send and receipt times around the failing assert, and whether the loop kept running.
2. Fix it where it is wrong (product, red-first + mutant) or argue `Timing` membership at the test with those numbers.
Done: the cause quoted; for a product fix the red-first test red before and green after and the mutant red; the full workflow green on all three platforms
on the final tip (run id); any narrowed stress run quoted with its count; the gate. Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder
pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes
printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.
