# U-fix-loss-reopen — on hosted Windows the loss watch twice failed to do what it owes: a breach closed the day without flattening, and a due reopen did not happen; find the cause; if a real gateway can do either, fix the product
**Protects:** the loss boundary on the money path (gateway). A confirmed breach closes the day AND the app closes the book (`U-flatten-2`) — an open position
left open past the daily loss limit is the dangerous direction; a day that is due to reopen reopens only on the receipt the gateway writes after looking
(`LossReopen`) — a missed reopen keeps trading held (the safe direction, still a defect); no fix may reopen early or skip a flatten condition (`CLAUDE.md`).
**A FRESH FIXER, seat P, FIRST — the second sighting moved it to the front** (the orchestrator's rule of 2026-10-03).
**Evidence (RUN, `gh run view`), windows-latest only, ubuntu and macos green, both on shas that change no code:**
(1) `main` `c15040a`, run 37166688583, job 111330935377: `LossWatchTests.A_book_that_goes_through_the_budget_with_no_order_arriving_closes_the_day`
(`tests/TradeAgent.FaultTests/LossWatchTests.cs:84`) failed [23 s] — "Expected: 1 / Actual: 0" at `:133`, `Assert.Equal(1, conn.Closes)`; its stdout: "after
one pass: closed=False", the record "loss=1025.00 budget=1000", "pulls: first=2 confirming=3", the why-sentence "TradeAgent closed today to new risk at 12:00
UTC …", and "mutations before/after: 1/1, orders 1/1" — the day closed and NO close was sent.
(2) `main` `3b03041`, run 37098316726, job 111132683376: `LossHoldSurfacesTests.A_release_is_told_with_the_owners_note_quoted` failed [41 s] — "Assert.Single()
Failure: The collection was empty" at `tests/TradeAgent.UnitTests/LossHoldSurfacesTests.cs:79`, `Assert.Single((await gw.LossWatchAsync()).Reopened)` after
`clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + 1 min)`; its `Breach` helper (`:55-69`) had just closed episode one ("episode-one closed :
[loss_breach:SIM-001:2026-03-10]") — a due reopen did not happen.
**Facts to start from (SOURCE at `c15040a`; facts, not a diagnosis):** both tests step a substituted clock (`GatewayOptions.Clock`) yet took 23 s and 41 s
on that runner — where the time went is part of item 1. The watch: `TradingGateway.LossWatchAsync` (`src/TradeAgent.Gateway/TradingGateway.cs:3594` on; a
closure needs a second, distinct pull inside `LossBreachConfirmWithin`; a real `Stopwatch` times each reading and "decides nothing"). The flatten:
`FlattenWhatWasJustClosedAsync` (`:3717`), `ReFlattenClosuresWithNoOutcomeAsync` (`:3762`), `FlattenForBreachAsync` (`:8035`), `AccountForTheFlattenAsync`
(`:8438`). The reopen: `src/TradeAgent.Gateway/LossReopen.cs` (`EligibleAt` `:81`). Test (1) drives `gw.RefreshHealthAsync()`; test (2) calls `LossWatchAsync`.
**Hypotheses to TEST, not conclusions (the orchestrator, 2026-10-03):** `U-runner-forward` (`f7f0b09`) moved every order-path age check onto the gateway's
clock — confirm that the watch, the flatten and the reopen, and everything they wait on, read the same injected clock; and whether one wait on Windows inside
the breach or flatten path explains the 23 s, the 41 s, the missing close and the missing reopen alike.
**THE PRODUCT QUESTION, answered with evidence:** can a real gateway on a real Windows machine close the day on a confirmed breach yet send no close, or miss a
due reopen — one cause or two? If the PRODUCT: fix it with deterministic red-first tests (seams or the injected clock; no sleeps waiting for luck) and one
watched mutant per guard, quoted. If only the harness or the runner: say exactly where it differs from the product's path and why both properties are still
proven. **Reproduce on CI** (`fleet/bin/ci-dispatch.sh`; read the windows job — green on this Mac proves nothing about Windows). Diagnostic runs may narrow
`.github/workflows/build.yml` on your branch (windows only, both classes looped) and print each watch pass, each flatten step and their timings — branch only,
removed before the proving run, each named in the report. The Windows box is NOT granted.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` (the fresh-fixer rule; `build.yml`'s `Timing` rule); `LossWatchTests.cs` and `LossHoldSurfacesTests.cs` (whole);
`LossReopen.cs`; `TradingGateway.cs` around the lines above; `BUILD-STATUS.md`'s `U-flatten-2` and `U-runner-forward` records; `TestEnv.Ready`; the Fake broker.
Must NOT: loosen a flatten or reopen condition (the two-pull confirmation, the receipt after looking, `EligibleAt`); reopen early; send a close the rule does
not owe; add a retry or a sleep or raise a timeout until it passes; skip on Windows; weaken an assert; move a test into `Timing` unless it truly asserts a
deadline the runner must keep, argued at the test with measured numbers.
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from Windows runs: in each test, every watch pass's outcome, every flatten step, and where the 23 s and 41 s went.
2. Fix it where it is wrong: product (red-first, deterministic, the mutants quoted, the protected properties named and kept) or harness (the reason above).
Done: the cause quoted; for a product fix each red-first test red before and green after and its mutant red; the full workflow green on all three platforms on
the final tip (run id); any narrowed stress run quoted with its count; the gate per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass" (rebase on
`main` first; `--no-incremental` Release 0 warnings; Unit and Fault 0 failed; touched classes 3×; names 0 removed with both set sizes); `## Report` ≤ 20 lines.
## Report
**Tip** `d0d14159` (code; this report is the next commit), rebased on `main` `e651eaa0`, no conflict. **Property kept:** a closed day never coexists silently with an
open position — the flatten goes out at the breach unless it cannot; then every pass retries, the dashboard says so, and nothing reopens until it reads flat.
**Item 1, DONE — one cause behind both sightings:** the flatten ran on the connector's 2 s emergency budget, a real wall clock (`RiskReducingScope`, `TickCount64`; every
instant is `GatewayOptions.Clock`), and its own write-ahead commits were charged to it: on a slow disk they spent it before the first platform call, that call was refused
before the wire, and the outcome was written once, final, with a flagged row refusing the sweep — nothing retried; a day later the reopen was held by "ES 1 open". Runs
37170522369 + 37170524233 (branch-only `LossFlattenDiagTests` + windows-only `build.yml`, both removed in `d0d14159`), under the suite's load: each flatten step's spend a
~30 ms commit (first read 1922 ms left, close 1828 ms); 120 instrumented runs and 2×12 class loops green, no sighting red again; the CONTROL (deadline gone at the first flatten call) reproduced
both sightings on windows exactly (`closes=0, mutations 1/1`; `reopened=[]`, ES 1). 23 s/41 s = these fixtures (0.7–4.8 s there) on a disk 5–60× slower: 2–4 commits ≈ 2 s.
**Item 2, DONE (product):** (A) `RiskReducingScope.BeginExcludingTheStore` + `Core.Db.StoreTime`: the app's flatten is not charged for its own `Database` time; presses,
sweeps and the 2 s value unchanged. (B) one `TransportRecord` per attempt: empty ⇒ its rows settled not-sent and unflagged, no outcome, an owed note `loss_flatten_owed:`
("has NOT closed your open positions yet … tries again on every pass") on every surface, the sweep re-runs it each pass, `HeldBy` holds it; dispatched ⇒ final as before.
**RED before:** slow-store test `Expected: 1 Actual: 0` ("store held by another : 3050 ms against a 2000 ms budget"); owed test `Assert.Null() Failure` (final record,
`UNKNOWN flagged=1`); every-pass test "1 attempts in all, 1 by the confirming pass". **Mutants:** A, refund removed ⇒ slow-store `Expected: 1 Actual: 0`; B, proof removed
⇒ wire guard `Expected: 1 Actual: 2`, book `[Buy 1 ES FILLED | Sell 1 ES CANCELLED | Sell 1 ES WORKING]`, and 2 `LossFlattenTests` red. Both restored identical.
**Gate:** `-c Release --no-incremental` 0 warnings 0 errors; Unit 1381/1381, Fault 420/420; 3× green: LossFlattenOwedTests, LossFlattenTests, LossWatchTests, LossHold-
and LossFlattenSurfacesTests. CI 37185798775 at `d0d1415`: ubuntu, macos, windows (44 min, Timing first try), package all success. Names: removed 0, added 4, 2153 → 2157.
**Declared deviation:** `LossFlattenTests.A_position_that_shrinks…` (not renamed) now reads its refused attempt alone (nothing sent, REJECTED/REVERSE, ES 1, owed) and then
the next pass's single Sell 1 and a flat book, in place of the final flagged record the ruling reverses. Docs: CONTRACTS, USER-GUIDE, status schema, AGENTS.md.
**NOT done / NOT verified:** U-flatten-3's exit still charges its store; a close that MAY have reached the platform still waits for the owner; no box, app not run.
