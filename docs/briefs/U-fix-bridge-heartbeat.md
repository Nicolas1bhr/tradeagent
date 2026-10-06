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

## Report
**Tip (code) `0ffc1b3d`** on `main` `d89f9cdc` (since moved by docs only); rebased three times, no conflict. Commits: diagnostics `43c7bc85`, `c92d888a` (ran as `5e8d98a3`, `1ed90305`), removed by `940054ea` (item 1);
`0ffc1b3d` (item 2); this report. Diff vs `main`: one trait and 19 comment lines in `BridgeRoundTripTests.cs` — no product code, no rung, so no RED-before is owed; the mutant below is the watched guard.
**Gate at `0ffc1b3d`:** build `-c Release --no-incremental` → 0 Warning(s), 0 Error(s). Unit `Passed! - Failed: 0, Passed: 1410` (8 m 28 s); Fault `Passed! - Failed: 0, Passed: 435` (1 m 49 s); `BridgeRoundTripTests` 3× `Passed: 41`;
`Category=Timing` selects the test, `Category!=Timing` does not (both run). CI 37420396771: ubuntu-latest success 12 min, macos-latest success 25 min, windows-latest success 52 min, package success 4 min — this test passed
first time on all three; macos's Timing step was red first in ANOTHER test (below). Names: sets 2199/2199, removed 0, added 0. Tests box: NOT RUN — `ready : NO - the machine does not answer` (exit 1, asked once).
**Item 1 — done: the cause is the RUNNER.** Six macos-latest runs printed every pulse's send/receipt time beside three canaries (pool Task.Delay, a dedicated thread's Thread.Sleep, pool dispatch) — 37413639706/-642258/-644218,
37415665297/-667586/-669858: 3,639 executions, 44,675 pulses with Describe() throwing, 0 connections lost, 0 loop exits, no pulse lost; worst gap per execution p50 166 ms, p99 189 ms (the VM wakes every timed wait up to ~90 ms
late, the dedicated thread as much as Task.Delay); four passed 300 ms, worst 511 ms (rep268, -667586: slept +651, woke +1038, the dedicated thread's sleep 285 ms late, 38 ms of CPU in 1.6 s — the VM stopped running it).
**Declared deviation:** the natural red never recurred, so the times "around the failing assert" are an injected 750 ms SIGSTOP's: stall008 (-665297) pulsed every 100-154 ms to +1316, froze to +2276 (sleep canary 934 ms late),
then b.wake, c.poll, `c.quiet:960ms-since-last-beat`, c.drop in one millisecond → `connected=False`, the loop still running; all 36 went red, 13 there and 23 one assert later (connected, not READY). The original red's own gap is unrecoverable.
**Item 2 — done** (`0ffc1b3d`): `[Trait("Category","Timing")]`, the numbers argued at the test; no timeout, interval, delay or assertion changed. **What the Timing step does differently:** no tolerance — the same test,
asserts and deadlines, run in the category's own `dotnet test` after the main step beside the other 96 Timing tests, which shelters it from nothing (the stalls above were measured in processes that small); on a failure it
writes the names to the job summary, annotates the run, keeps that trx and re-runs the whole category ONCE, which decides the step; red twice in a row is a red run. **Why it is the fix, not a hiding:** nothing here can stop the VM stalling; what was ours was a test needing the runner to turn a 100 ms loop once per 600 ms, filed where a first red
is taken as proof of what the product DID. A stall must now hit the same 1.5 s window twice (none reached 600 ms in 3,639 executions; one red in ~150 ledger macOS jobs), while what the test guards fails deterministically —
**mutant** (Describe() unguarded, its throw ending the loop): red on two runs in a row, `Assert.True() Failure Expected: True Actual: False` at `:342`; restored, green. Still possible, as `build.yml` says: an intermittent
product stall rescued once per run, its first failure still annotated — none in 44,675 pulses. **For the manager:** run 37420396771's macos Timing first attempt failed `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc`,
NullReferenceException at `:333` (`(JsonElement)reply.Data!`, a CancelAll reply with no Data after its duration asserts passed); green on the re-run and on ubuntu/windows; my diff cannot reach it; U-fix-press-budget's area.
**NOT done / NOT verified:** what stalls the macOS VM (host-side); no Windows hardware run (tests box off, ATAS box not granted). Outside the brief, unchanged: liveness reads `DateTimeOffset.UtcNow` (`AtasConnector.cs:557`, `:1760`)
where write/answer deadlines read `TickCount64`, so a backward clock step would keep a silent bridge READY; not this red (no poll ever won, so a forward step cannot drop this pair).
