# U-test-hygiene-3 — four windows-latest reds of 2026-10-07 made deterministic or decidable: a temp held inside its grace, one writer on the stub bridge's pipe, a quote on the gateway's clock, a wait that says what it waited on
**Arrow closed:** the gate (`docs/HOW-WE-BUILD.md` step 6: red CI only on a hosted runner, product right ⇒ a fresh fixer on top of `main`, the sha recorded red until it lands). **Depends on `U-test-hygiene-2`,
LANDED** (it touches `CoidWitness.cs`, `CoidWitnessTests.cs`, `QuoteClockTests.cs`, `FakeConnector.cs`, `FakeBroker.cs`; its item 5 is `QuoteClockTests.A_bar_fed_feed_is_not_degraded_one_bar_after_its_close`,
NOT this unit's). **Protects:** trap 35's per-writer temp names (COID witness); the bridge liveness proof (`U-bridge-liveness-clock`); both quote gates on the gateway's clock (`U-runner-forward`);
resume-on-start (`U-fix-resume-on-start`). No `src/` change, no assertion loosened, no test into `Timing`.
**Today** (lines at `18a7ab10`; `U-test-hygiene-2` landed at `ad3dc494` and moved `CoidWitness.cs`, `CoidWitnessTests.cs`, `QuoteClockTests.cs`, `FakeConnector.cs`, `FakeBroker.cs` — find each by its symbol; windows-latest only, each a first sighting in the last six red runs — 37611591775 on `34a34ba0`, 37612881764 on `e631e0f3`, a docs-and-`RuntimeManifest` branch):
(a) `CoidWitnessTests.Two_writers_do_not_share_a_temp_name`, "Expected: 2 / Actual: 1" at :2302 [5 s]. No clock is in a temp's name — `{path}.tmp-{pid}-{SessionId[..8]}-{seq}`, the session a GUID
(`CoidWitness.cs:683`, :696, :2455). With nothing committed (`NeverLands`) A's stranded temp is REJECTED (:1483-1493, `DescendsFrom` :1718) and B, the next owner, renames it to `.rejected-1`
(:852, :1600-1608) once it is 2 s old (`QuarantineGraceSeconds` :184, read at :2282) — the designed move (`A_rejected_leftover_is_reported_once_and_moved_aside`, :1886-1910) — so the `.tmp*` glob counts 1.
The test races that grace: 200 ms of this witness's waits read 2335 ms on windows-latest (run 37443797669, `U-test-hygiene-2` item 2). It ignores B's answer, so B's lease refused in 50 ms reads the same.
(b) `BridgeLivenessClockTests.A_bridge_pulsing_while_the_wall_clock_steps_forward_an_hour_stays_ready` [224 ms] failed on no assertion: `InvalidOperationException: The stream is currently in use by a
previous operation on the stream` from `StreamWriter.WriteLineAsync` in `StubBridge.Send` (`Harness.cs:205`) under `Heartbeat` (:192), pulse 3 (`BridgeLivenessClockTests.cs:138`, :152). The stub's loop
(:207-229) answers the test's `accounts` RPC (:150) on the same writer with no gate, and on Windows the connector reads that answer before the stub's write task completes; the real bridge sends
every frame through one `SemaphoreSlim` (`BridgeServer.cs:31`, :479-494). The liveness path reads no wall clock (`AtasConnector.cs:628`, :640, :564-574, :1837-1842).
(c) `QuoteClockTests.Both_quote_gates_and_the_decision_gate_read_the_same_clock` at :236, "40 s inside the position read: ok — FILLED" [21 s]: the gateway reads `clock`, fixed at :184; the fake
stamps its quote on the MACHINE clock (`FakeConnector.cs:297`, :302), so step FOUR's 45 s reads 45 − (seconds since :184) and passes the 30 s gate once 15 s have gone. Item 5 of hygiene-2 pins :288-300 only.
(d) `ResumeOnStartTests.A_restart_with_the_ai_working_…` at :69 [1 m 48 s], "the wake that was due when the app closed was never taken by a turn": :62-65 held (runtime started, loop running);
`Until` (:349-358) gave 60 s to a disposition a Review wake gets only from `Settle` at a turn's end (`MissionLoop.cs:2253-2258`); the turn is `powershell.exe` (:312-328; a first launch beside the suite measured > 20 s, :85-94); the
run was ~2× main's an hour before (Unit 1 h vs 25 m 36 s). Whether a turn was in flight or never launched is not in the message: UNKNOWN.
**Observable result:** (a)-(c) green whatever the runner's speed, each with the windows red reproduced on this Mac first; (d)'s next red says which of the two it is; no product line moves.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` step 6; `U-test-hygiene-2`'s report and diff; the files above. Items, one commit each, one-sentence messages:
1. (a) A's stranded temp is held inside the grace for the test's length (its mtime an hour ahead before B writes, as `A_candidate_written_moments_ago_is_left_where_it_is` sets its own), B's lease is
   asserted taken (no "another writer owns this witness" in its failure), A's bytes are asserted untouched, and the two-temps-two-names assertions stay as they are.
2. (b) `StubBridge` sends one frame at a time: `Send` and the loop's answers through one `SemaphoreSlim(1, 1)`, `BridgeServer`'s shape, which `DisposeAsync` takes before it disposes the writer.
3. (c) The quote is stamped on the gateway's clock (`QuoteClock = clock`, `QuoteAge` 5 s, as `LossWatchTests.cs:50` does): five seconds old on `clock`, thirty days and five on the machine's, however long the
   runner takes; each step's expectation unchanged. The report lists every other `Faults.QuoteAge` user (six files) whose fake still stamps on the machine while its gateway reads a set clock.
4. (d) The `Until` that fails names the loop's state at its deadline — whether a turn took the wake and when it launched, `presence.LastAliveAt`, the loop's error count and next turn; no deadline moves.
   Then the class runs on the tests box beside the full suite: a turn in flight at 60 s is a `Timing` argument with measured numbers (step 6), not this unit; no turn launched is a PRODUCT finding for seat P.
Tests: (a) the OLD body with `Age(aTemp)` before B, red on this Mac at :2302 with Actual 1, quoted, then the new body green with and without `Age`; (b) `StubBridgeTests.A_send_waits_for_an_answer_in
_flight` holds the loop's `accounts` answer inside its write (a latched stream under the stub's writer, a stub-only seam) and sends a heartbeat: the old stub throws the quoted exception, the gated one
delivers both frames; (c) the OLD body with the quote 16 s younger (a slow runner's 16 s) red at :236 with "ok — FILLED", quoted; (d) the new message once, from a probe made to hang its turn, quoted in the report and not committed.
Mutants, quoted red: (a) the session dropped from `_tempPrefix` (`CoidWitness.cs:696`) ⇒ B's `FileMode.Create` (:715) truncates A's temp; (b) the gate removed; (c) the dispatch-time gate
(`TradingGateway.cs:3303`) reading the machine's clock ⇒ step ONE refused. First, in the report: each class's time on this Mac 3× and on CI.
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed,
touched classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
