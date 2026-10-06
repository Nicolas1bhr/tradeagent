# U-test-hygiene-1 — two test faults, no product change: six meter tests read "today" at a second instant and go red across midnight; test homes are never deleted
**Protects:** the suite's honesty — a red must mean the product is wrong (`docs/HOW-WE-BUILD.md`: "A red twice in a row is still a red run"), and a suite must
not fill the disk it runs on. Batches the orchestrator's `U-fix-midnight-turns` and `U-test-home-cleanup` (2026-10-04). **Light leg, seat P's slot 1 right
after `U-inbox-unreadable`.** Tests only; the product's day boundary is NOT changed.
**Item 1 — evidence (RUN, `gh run view`, 2026-10-04):** `main` `5a54452` (docs only), run 37163465037, red on ubuntu-latest (job 111321527935) and
macos-latest (job 111321527963), windows-latest green, all failing at 00:00:08–00:00:11Z — a run that crossed midnight UTC:
`TurnRecordTests.Todays_totals_are_sums_over_the_launch_ledger` ("Expected: 2 / Actual: 0"), `TurnRecordTests.Cached_input_is_billed_once_at_the_cached_rate`
(0.007056 / 0), `TurnRecordTests.With_no_prices_the_turn_is_recorded_unpriced_rather_than_free` (1 / 0),
`OwnerPriceTests.The_owners_rate_prices_a_turn_nothing_named_a_model_for_and_is_not_an_estimate` (0.017256 / 0),
`UnknownModelIsPricedHighTests.The_estimate_is_labelled_on_the_price_on_the_line_and_on_the_days_total` (1 / 0) and
`UnknownModelIsPricedHighTests.The_daily_cap_is_reached_by_turns_whose_model_was_never_named` (0.056268 / 0) — all in
`tests/TradeAgent.UnitTests/TurnMeterTests.cs`. First sighting.
**Item 1 — facts (SOURCE at `c987128`; read by seat P, NOT runtime-verified):** the tests record at one `DateTimeOffset.Now` read (e.g. `:348-349`,
`CodexTurn(DateTimeOffset.Now)`) and ask the meter's `Today`, whose clock defaults to another (`Meter()`'s `now: … () => DateTimeOffset.Now`, `:292`), and
`LocalDayOf(DateTimeOffset.Now)` (`:355`, helper `:370`) at a third. The product's day turns at LOCAL midnight by design (`TurnMeter.LocalDay`); hosted runners
are on UTC, so a test that straddles 00:00Z records yesterday and sums today.
**Item 2 — facts:** `TestEnv.Init` (`tests/Shared/TestEnv.cs:18-24`, a module initializer) creates `$TMPDIR/tradeagent-tests/<guid>` for every test process
and never deletes it; four classes make their own root there and delete it only after every assert passed (no `finally`): `LossBudgetSurfacesTests.cs:293/303`,
`LossDayClosedSurfacesTests.cs:249/257`, `LossFlattenSurfacesTests.cs:179/187`, `LossReopenSurfacesTests.cs:244/255`. The orchestrator measured 34 GB of such
homes on this Mac since 2026-09-03; `fleet/bin/purge-test-homes.sh` (homes untouched > 6 h, run by `gate.sh` and `suite.sh`) is a stopgap, not the fix.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `TurnMeterTests.cs` (the three classes, `Meter()`, `CodexTurn`, `LocalDayOf`); `TurnMeter.cs` (`LocalDay`,
`Today`, the `now` seam); `tests/Shared/TestEnv.cs`; the four `Loss*SurfacesTests` named above; `build.yml` (the `Timing` rule).
Must NOT: change the product's day boundary or any product file; weaken an assert or loosen a figure; skip or move a test into `Timing`; add a retry; delete
anything outside `$TMPDIR/tradeagent-tests`; let a cleanup failure (a file still held by a child process on Windows) fail the run.
Items, one commit each, one-sentence messages:
1. Every test in the three classes — and any other test you find that records and then reads "today" through two clock reads (grep `DateTimeOffset.Now` and
   `.UtcNow` around `Today`, `LocalDayOf`, `Between`) — pins ONE instant (mid-day local, or the meter's `now` seam) for both; plus a GUARD that pins the product's
   semantics: a turn recorded at 23:59:59 local is not in the total read at 00:00:01 local, and is in it at 23:59:59.5 (deterministic, through the seam).
2. `TestEnv`'s home and the four classes' roots are deleted at the end — process exit for `TestEnv` (best effort, errors swallowed), `try/finally` for the
   four — and one is kept only when an environment variable you name asks for it (its path then printed once); `TestEnv`'s summary says so.
Proof: item 1 — show the crossing deterministically BEFORE the change (e.g. one affected test driven with the record instant at 23:59:59.9 and the read at
00:00:00.1 through the seam, red at base as on CI) and green after; item 2 — count `$TMPDIR/tradeagent-tests` entries before and after a full local Unit run
(both counts quoted), and the kept-home variable shown working once.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit and
Fault 0 failed; touched classes 3×; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
## Report
**Tip** `edb248c1` (code; this report is the next commit), rebased on `main` `2a12951c`, no conflict. Tests only: no product file, no rung, no test removed or renamed.
**Item 1, DONE:** every meter test that records a turn and then reads "today" pins ONE instant — `TestEnv.LocalNoon()` (today's local noon, the suite's own idiom) or the meter's
`now` — in the three named classes and, by the brief's grep, `TurnAllowanceTests` ×2 and `AiAttemptLedgerTests` ×1 (a row starts 3–5 s before its record instant), plus four
with two real-clock reads: `HarnessBudgetTests`, `HarnessKeyOriginTests`, `VendorLimitTests` (and its ±1-day `OnlyRow`), `BudgetReservationTests` ×2 (loop wait = meter's midnight − loop clock).
GUARD `TurnRecordTests.A_turn_recorded_a_second_before_local_midnight_…`: 2026-07-15 23:59:59 local, read at +0.5 s → 1 turn at its exact cost; read at 00:00:01 → 0.
**Item 1 proof (RUN, real clock, a `zic` zone whose local midnight had just passed):** base `2a12951c`, the three classes, bracketed 00:00:01→00:00:02 local: 6 of 20 red — the
six of run 37163465037 with its figures (2/0, 0.007056/0, 1/0, 0.017256/0, 1/0, 0.056268/0); at the fix 21/21. `TurnAllowance`+`AiAttemptLedger` at 00:00:00→00:00:01: base 3 red
(3.60/0, 4/1, `Single` on empty), fix 13/13. The four ms-window classes: NOT shown red (window too narrow). Guard mutant, `LocalDay` summing the UTC day: `Expected: 0 Actual: 1`, l. 433; `src` restored.
**Item 2, DONE — declared deviation:** the home goes at the END OF THE ASSEMBLY'S RUN, `Shared/TestHomeFramework.cs` (xunit 2.9.3's executor and runner, its body checked against the IL,
one step in `BeforeTestAssemblyFinishedAsync`; registered for all three projects in `tests/Directory.Build.props`), and again at process exit — not exit alone: VSTest kills the host ~100 ms after
asking it to stop (a 2 s exit handler measured never finishing) and deleting a copy of a full Unit home (316 MB, 1,375 files) took 136 ms. Errors swallowed; nothing deleted outside `tradeagent-tests`.
`TA_TEST_KEEP_HOME=1` keeps it, its path printed once on the host's output (`dotnet test` shows it at `--logger "console;verbosity=normal"`). The four Loss roots: `using var root = TestEnv.NewScratch(…)`, a try/finally, now inside the home.
**Item 2 proof:** base, filtered run: 1 home left. Fix, full Unit: raw 28 → 28, 0 newer than the marker; exit handler disabled: 0 left (the hook alone); keep run: printed `…/88a551d2…`, the one new entry, its two scratch dirs inside; deleted by hand.
**Gate:** `--no-incremental` Release 0 warnings, 0 errors. Local Unit 1382/1382, Fault 420/420 (the 2 entries newer than its marker hold other worktrees' paths: u-flatten-confirm, u-runner-exit-hygiene-a). 3× the 13 touched classes: 93/93 each.
CI 37391490827 at `edb248c`: ubuntu ✓ 13 min, macos ✓ 16 min, windows ✓ 50 min (each: Unit 1382, Fault 420, Integration 718 + 1 skipped; Timing first try), package ✓. Names vs `main`: removed 0, added 1, 2157 → 2158.
**Midnight:** that run was dispatched 23:59:12Z and crossed 00:00Z, but its test steps began 00:00:19Z (ubuntu), 00:00:45Z (macos), 00:00:48Z (windows) — past the 12.5 s window: the six passed there; NOT crossing evidence.
**Tests box:** NOT RUN — ready: NO - the machine does not answer (…).
**NOT done / NOT verified:** `RiskGateTests.A_day_past_its_loss_budget_refuses_…` (Fault) has the shape on the UTC day — fills stamped by `FakeBroker`'s own `UtcNow` (`:146`), no seam — so closing it needs a
product change; left. Windows leftovers (held files) not measured; a host killed mid-run still leaves its home to `purge-test-homes.sh`; an IDE reusing one host for two runs is not handled.
