# U-red-says-why — an emergency's wall-clock red names a cause it never measured: a runner that did not run the process reads as "the owner's cancel is still on the full deadline"
**Protects:** the owner's emergency deadline (`BridgeBudgets.Emergency`, 2 s; MONEY PATH: cancel, cancel-all, close) by keeping its red a red AND legible, and the honesty rule (`fleet/ci-ledger.md`,
`BUILD-STATUS.md`): a red says what it measured; and CI's one retry (item 3, the orchestrator's 08:11 ruling — shared by both lanes). Tests + `build.yml` only; seat B (seat P closed, R21); no rung; light.
Surveyed read-only at `main` `7e29fc65` (SURVEY-P-cancel); pointers re-checked by seat P at `b35653c2` (none changed since). RENAMED from U-emergency-red-says-why: its 12:54 dispatch was parked by the wind-down unstarted; that branch stays at `11dba05c`.
**Facts (`main` `7e29fc65`; the connector's send path is byte-identical at `c51502ba`).**
- Windows run 37524459410 (`u-bridge-liveness-clock` `c51502ba`), Timing RETRY only: `A_cancellation_fails_fast_on_a_stalled_bridge_whoever_issued_it(caller: "button")` "took 11.80s behind a stalled
  write — it is still on the full deadline" (`tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs:459-460`). Attempt 1 of that job: 91/91 green; the retry ran only because FaultTests' press
  test went red first (fixed since: U-test-hygiene-2 item 6, `ad3dc494`). Sole sighting in 200 runs (annotations + failed logs, 2026-10-03..08); 0 Timing retries in the 31 windows jobs that reached the step since `ad3dc494`.
- The button's own time is bounded on the product's clock and never awaits the stalled write: `AtasConnector.cs:146`, `:1636` (emergency), `:1647-1650` (one absolute deadline), `:1677-1679` → `:1266`
  (gate wait = what is left of 2 s), `:1293-1296` (no writer progress → `DropStalledPeer`, `PeerStalled`, no await between), `:1396-1400` (synchronous; on Windows `PipeStream.Dispose` is CloseHandle
  only and `Flush` a no-op — dotnet/runtime `PipeStream.cs`).
- Survey probe (Mac, throwaway, the fixture's exact shape): button ×10 2.000–2.005 s "not responding"; the stalled writer on `WriteTimeout` 60 s → button 2.002 s; an ORDINARY read behind the same
  stall → 9.754 s "could not reach the ATAS bridge" (what the full deadline really reads); the whole test host stopped 9.8 s from 1.9 s into the call → 11.753 s, `ConnectorTransportException`,
  "'cancel-all' is NOT confirmed … not responding", a dedicated-thread and a pool 20 ms tick 9807 / 9810 ms late. Mutant `:146` without `CancelAll`: the real button row
  red "took 9.75s behind a stalled write — it is still on the full deadline", the probe 9.750 / 9.752 s "could not reach the ATAS bridge", witnesses ≤ 8 ms — the SAME sentence the stalled runner earned at 11.80 s.
- `build.yml:67-94` (the Timing step): attempt 1 runs the whole category (`:74-75`); on a red it records the failed names (`:80-90`) and re-runs the WHOLE category
  (`:92-94`) — so in 37524459410 the button row, green on attempt 1, went red only because a FaultTests press test's red had re-run everything. "Red twice is red" belongs per test.
- So the 6 s ceiling can only say "this machine took that long"; the clause after it asserts a cause nothing measured, and nothing in the class tells a runner that did not run the process from the
  product (`RunnerSpeedProbeTests.cs` says so of the whole category). `build.yml:56-58` records the instrument that can: U-press-stopwatch's dedicated-thread + pool tick.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md:91-94`; `.github/workflows/build.yml:33-100`; `RunnerSpeedProbeTests.cs` header; `ConnectorSendDeadlineTests.cs:387-462`, `:1625-1690`; `AtasConnector.cs:1253-1364`, `:1595-1700`.
Must NOT: touch `src/`; raise the 6 s ceiling, the 2 s budget or any deadline; add a retry or a skip (item 3 only narrows the one retry that exists); excuse, subtract or pass on a measured stall (the witness LABELS a red and never decides one — a
late pool can be product code); scale anything with `TestTime`; change `Timing` membership or any other step of the workflow; weaken, delete or reorder away an assert.
Items, one commit each, one-sentence messages:
1. **A runner-stall witness** in `tests/Shared`, linked through `tests/Directory.Build.props` like `TestTime.cs`: a 20 ms sleep on a dedicated thread and a 20 ms `Task.Delay` loop on the pool, each
   keeping its max lateness across a window, read as one clause ("dedicated 9807 ms, pool 9810 ms late"). Its clock is a seam (`TimeProvider`); its test steps that clock 1.5 s and reads ≥ 1480 ms (the step less one period) —
   RED first against a witness that never reads its clock — and asserts no ceiling.
2. **Every wall-clock ceiling in `ConnectorSendDeadlineTests`** (13 at `7e29fc65`: `:179`, `:407`, `:459`, `:540`, `:834`, `:914`, `:1137`, `:1186`, `:1241`, `:1294`, `:1409`, `:1471`, `:1544`) goes
   through one helper that runs the witness across the timed call and fails with the elapsed time, the call's own outcome (`ex.Message`, or "returned") and the witness clause; the unmeasured causes
   ("still on the full deadline", "still queueing behind the stalled write", …) leave the text. Bounds, order and every other assert unchanged; a stalled runner's red stays red.
3. **The Timing retry re-runs only attempt 1's failed tests** (`build.yml:92-94`): the names attempt 1's trx files list as failed (`:81-82`) become the retry's filter, a
   Theory's rows at method granularity, names escaped for the filter syntax; attempt 1 naming no failed test (a crash, an abort) re-runs nothing and fails the step, saying
   so; the first-failure record (`:80-90`: summary, annotation, its own trx, uploaded) unchanged; a test red on both attempts is a red run. The step's comment says the new scope.
   Proof: the filter logic as a small script beside the workflow, run locally against three sample trx files (one failed row; a failed Theory row; none named) — RED first
   against the old step's whole-category filter — and ONE branch CI run with a TEMPORARY Timing test red on its first attempt only (reverted before the report): the retry ran
   it alone (its count quoted) and the job is green. The final tip's run, all three platforms, has no retry.
Proof (items 1-2): RED-before quoted — the button row with the test host stopped 9.8 s from 1.9 s into the call (`kill -STOP`/`-CONT` from a throwaway harness, as the survey did): before "took 11.75s … still on
the full deadline", after the same red carrying "not responding" and both witness readings ≈ 9.8 s. ONE mutant on the money path: `AtasConnector.cs:146` without `CancelAll` → the button row red with the
full deadline's own sentence and a quiet witness, quoted. Item 1's step test red-first, quoted.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; three suites 0 failed; this class 3× through `suite.sh`;
branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed (both sizes); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
