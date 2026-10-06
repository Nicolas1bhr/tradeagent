# U-inflight-settle — a market order whose platform update was lost is read back from the platform's own order list and settles from what it says, so it stops holding the position's automated closes
**Protects (money path: the gateway's close guard):** `CLAUDE.md` rule 3 — only a FINAL answer the platform asserts settles a row; a throw, a live answer or an unprovable absence settles nothing, and
nothing is sent, re-sent, cancelled, flagged or paused here; rule 2 — "not listed" decides only on a complete history whose closes carry our id. **Accounting:** the final state goes through the stream's
OWN writer; a fill enters the ledger only by `RecordFill` (`G:2118`, event or pull), never by the settle. Owed BEFORE ANY LIVE USE (judged at `494e4d64`). Fresh builder, seat A. **No schema change.**
**Today** (`main` `bdf5affa`; `G` = `src/TradeAgent.Gateway/TradingGateway.cs`): `ClosesInFlightOn` (`G:6515`) counts PLACE MARKET rows `DISPATCHING`…`RECONCILING` for both
`RefuseAnUnresolvedReducerOrThrow` (`G:6555,:6586`). Who moves each: `DISPATCHING` — the call's answer (`Settle` `G:6029`), past `DispatchStrandedAfter` (`G:2884`) the reconciler (`Unreconciled`
`G:2904` → `ReconcileAsync` `G:10683`: flags, pauses); `RECONCILING` — the reconciler; `ACKNOWLEDGED`/`WORKING`/`PARTIALLY_FILLED`/`CANCEL_PENDING` — ONLY the stream (`OnOrderChanged` `G:10645`, write
`:10655-10673`; it skips an event landing while the row is `DISPATCHING`, so an older call answer sticks), the reconciler once flagged; `CancelAsync` (`G:7033`) settles the cancel's row, never its target's.
Lost: ATAS — `BridgeServer.cs:455-495` drops an event with no peer or a timed-out write and nothing replays it; the reconnect's READY (`AtasConnector.cs:969`) only marks the fill pull due (`G:2047`).
Simulator — `FakeConnector.CancelOrderAsync` (`:404-417`) and cancel-all raise no `OrderChanged`, `FakeBroker.FillWorking` (`:169`) raises nothing, the book is memory (`FakeBroker.cs:17`); only `Fill`
`LeaveWorking`/`PartialFill` (`:251`; dev host `GatewayHost/Program.cs:121-127`) leaves a market order in flight. Paper raises in-process (`PaperConnector.cs:404,450,644-645`): only a process ending
between book and handler loses one. The END after `CancelWhatStillWorksAsync` (`G:1251`), the owed close (`G:1310`, once a minute), the runner's exits and the agent's `close` stay refused (read, not run).
**Extend, don't fork:** `AskTheHistoryAsync` (`G:9514`, the loss flatten's confirm, every health pass `G:11251`) already asks the order under the row's client id in `CreatedAt − 5 min` (terminal →
verdict; live or a throw → undecided), then fills under it (→ FILLED), then absence (→ CANCELLED only where `AbsenceDecidesALostClose` `G:9576` — `ReconciliationProvable && ClosesCarryClientOrderId`:
paper, the Simulator, never ATAS — and past `AbsenceCountsFrom` `G:1857` + `AbsenceGrace` `GatewayTypes.cs:47`). **Stale is that clock, no new number:** `Now − AbsenceCountsFrom(row) ≥ AbsenceGrace` —
the dispatch, or the dispatch + `DispatchStrandedAfter` (`WorstCaseOperationPath` + `DispatchSettleSlack`, `GatewayTypes.cs:61`) when this process did not watch it end.
**Observable result:** on the Simulator a market close left `WORKING` whose fill or cancel is never reported settles `FILLED`/`CANCELLED` on the first health pass past that clock, and a close it held
goes out next, sized from the re-read position; a live answer, a throw, a non-provable connector, or ATAS not listing it changes nothing and the refusal still names the row. (A run's OWN flatten
settled `CANCELLED` owes no close — `OwesItsClose` `G:737`, as when the stream says it: out of scope, named in the report.)
Read first: `CLAUDE.md`; `G:6470-6600, 9318-9590, 10645-10910, 11217-11260`; the press `G:8243-8355, 10470-10495` (seat P's `U-press-close-once` calls item 2 there); `CONTRACTS.md:1019-1041`.
Items, one commit each, one-sentence messages:
1. **One writer:** `OnOrderChanged`'s write (owner-state skip, `CanTransition`, `Transition`, `StateChanged`, terminal `Wake`) becomes `ApplyAPlatformAnswer(ExecutionRequest req, ExecutionState to,
   string? connectorOrderId, decimal? filled, string? error = null)`; the stream calls it, behaviour unchanged.
2. **The ONE function (seat P calls it too):** `Task<InFlightAnswer> SettleAnOrderInFlightAsync(ExecutionRequest row, CancellationToken ct)` beside `ClosesInFlightOn`, `record InFlightAnswer(bool Settled,
   OrderInfo? Live, string Why)`: only an unflagged, non-press (`IsPressRecord` `G:7770`) PLACE row on `Connector.Id` in a stream state (else `Why` names its owner), only where `ReconciliationProvable`;
   asks `AskTheHistoryAsync` (now also handing back a live match); a verdict → `ApplyAPlatformAnswer`, absence's `error` naming the empty history, never "never reached" (the platform answered it).
3. **The sweep:** `SettleStaleOrdersInFlightAsync(accountId, ct)` on the health pass right after `ConfirmLostClosesAsync` (`G:11251`): every row item 2 takes on this platform and account that is MARKET
   or unreadable (the guard counts both) and stale; a settle marks the fill pull due (`G:2088`) and logs `inflight_settled`; it never throws.
4. **Docs:** `CONTRACTS.md:1039-1041`'s NOT-claimed `WORKING` row becomes a claim with its limits (ATAS not-listed stays; a lagging position read stays NOT claimed); the refusal (`G:6566-6572`) and
   `GatewaySchema.cs:425` add "or once TradeAgent reads its outcome from the platform".
**Answer first, in the report:** does ATAS list a FILLED PLACE market order under its client id after a bridge drop? NOT VERIFIED — `AtasConnector.cs:589-596` measured an ATAS-built close; if not,
ATAS keeps this residual, said so; the ATAS box only by grant.
Tests (red-first; seams `Faults.Fill = LeaveWorking`, then `Broker.FillWorking`/`Broker.Cancel` — no event; `MovableClock` `DispatchRecoveryTests.cs:1700`, `TestClock` `PaperDeploymentTests.cs:49`):
(a) `A_market_close_whose_fill_report_was_lost_settles_from_the_platform_and_the_next_close_goes_out`; (b) `An_END_held_by_a_market_order_whose_cancel_was_never_reported_closes_on_its_next_minute`
(`PaperDeploymentCloseOnceTests.cs`' harness); (c) `A_close_the_platform_still_lists_working_changes_nothing_and_is_still_refused`; (d) `A_close_the_platform_does_not_list_settles_only_where_absence
_decides_and_past_the_grace` (history hidden, `RecordingConnector.HistoryThrows` `:223`, `ClosesCarryTheId = false` `:144`, inside the clock: each stays; a restarted Simulator past it: `CANCELLED`).
(a), (b) RED on the base, quoted. Mutant to watch red and quote: absence settling without `AbsenceDecidesALostClose` ⇒ (d)'s `ClosesCarryTheId = false` arm red.
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release `--no-incremental` 0 warnings; Unit and Fault 0 failed locally, touched classes
3×; the full suite on CI via `fleet/bin/ci-dispatch.sh` (run id, every job's verdict); names vs `main` 0 removed (both set sizes printed); the tests box run or "tests box: NOT RUN — <ready's answer>";
`## Report` ≤ 20 lines appended here. No push to `main`, no merge.
