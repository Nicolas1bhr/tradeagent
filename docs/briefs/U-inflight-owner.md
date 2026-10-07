# U-inflight-owner — an order the platform answered and no longer lists reaches the owner's card past the clock, pausing trading like any unconfirmed order; his two-press answer frees the closes it held
**Protects (money path: the in-flight sweep, the reconciler's absence rule, the owner's card):** `CLAUDE.md` rule 3 — such an order is AMBIGUOUS (it may have filled): it is recorded UNKNOWN and
reconciled by the reconciler's own writes, never settled from that absence nor written off "never reached the broker"; a throw, a live answer or an unprovable connector hands nothing over. No
terminal: the way out is the existing card. Operator authority: the one press is `ForceResolve`, reached only from the card (`DashboardView.cs:198-203`), no pipe op, no verb; two-press with a required
note (`Ui.Confirm`, `:763-766`). Owed BEFORE ANY LIVE USE (judged at `1828a188`). Fresh builder, seat A. **No schema change (30).**
**Today** (lines at `1828a188` — at `e2f4daf8` `G` +12 past :98, +58 past :2752, `GatewaySchema` +39 past :233, `AppHost` +30 past :905, `CONTRACTS` +6; `G` = `src/TradeAgent.Gateway/TradingGateway.cs`): the sweep (`G:6628`, stale `G:6640`) asks each unflagged non-press PLACE market row through `SettleAnOrderInFlightAsync`
(`G:6570`) → `AskTheHistoryAsync` (`G:9715`). On ATAS (`AbsenceDecidesALostClose` false, `G:9780`) "no order and no fill" is undecided (`G:9752-9753`), logged once (`G:6666-6676`), and the row stays
`WORKING`, unflagged, off the card (it lists `Unreconciled()`, `G:2904`; `DashboardView.cs:306`), holding `ClosesInFlightOn` (`G:6515`) for good. Undecided is only a string (throw `G:9728`, `:9741`; live
`:9735`). **Two traps.** Flagged as it is, `ReconcileAsync` moves it to RECONCILING (`G:11017-11026`) and, past the grace, writes it off `CANCELLED` "never reached the broker" (`G:11089-11098`; it reads
`ReconciliationProvable`, `G:11029`, never `ClosesCarryClientOrderId`) on the same tick (`AppHost.cs:1038`). Left in UNKNOWN, Close all and the loss flatten refuse its leg (`G:6472`, `G:8509`, `G:10686`).
**Design, decided — (a) the existing flagged path, not (b) a card line that does not pause:** one list feeds the gate (`G:3078-3082`), the health row (`G:11507-11516`), the card and
the reconciler (`G:2894-2896`); the card shows exactly what pauses (`DashboardView.cs:300-305`). The ledger can lack this order's fill, and the loss budget reads it (`G:2137-2138`): new exposure waits.
**Observable result:** on the simulator with closes carrying no id, a market close whose fill was never reported and that a restarted book no longer lists is on the card as RECONCILING, trading paused,
from the first pass past the clock until the owner answers; then the held close goes out sized from the re-read position. A live answer, a throw, inside the clock or an unprovable connector: unchanged.
Read first: `CLAUDE.md`; `G:2884-2923, 6470-6700, 9715-9790, 10849-10900, 10903-11120, 11295-11400`; `DashboardView.cs:661-850`; `Stores.cs:271-340`; `CONTRACTS.md:901-912, 1041-1062`. Items, one commit each:
1. **The answered absence, structured:** `AskTheHistoryAsync` also hands back, as a named field and never a string match, that both reads answered and neither lists the order (`G:9752`'s arm only);
   `InFlightAnswer` carries it (`Unlisted`) — the fact seat P's `U-press-close-once` calls "not working at the platform now": ONE field. No behaviour change.
2. **The reconciler's guard, before anything is handed to it:** its absence arm never decides a row carrying the platform's reference (`ConnectorOrderId` non-empty) where `!AbsenceDecidesALostClose`:
   inconclusive, untouched, paused. A final state it lists (`Adopt` `G:11295`) or fills under its id still settle it; a stream event is refused (`G:10874-10875`), its fill still reaching `RecordFill`
   (`G:2118`). One root: it also covers the dispatch's indefinite answer that carries a reference (`G:6962-6965`), on ATAS written off today.
3. **The hand-over**, in the sweep only (`SettleAnOrderInFlightAsync` still flags nothing): `Unlisted` on a row stale by `G:6640` (no new number) → the reconciler's two writes, CAS'd on the state read:
   `→ UNKNOWN`, `needsReconciliation: true`, the evidence as last error (what was asked, when; both reads answered, nothing listed; here that does not prove it did not fill; what it holds; either answer
   frees it); then `UNKNOWN → RECONCILING`, never resting in UNKNOWN; the sweep then skips it (`G:6635-6636`). A lost CAS hands nothing over; no latch; `StateChanged`, `inflight_handed_over`, activity.
4. **The card**, no new control or `Theme` value: the evidence shows in "Last check" (`DashboardView.cs:851`); for a row carrying the platform's reference "No order exists" reads "It did not fill" /
   "Confirm: I checked in ATAS and this order did not fill" (it existed), from a static helper the unit tests read.
5. **Docs:** `CONTRACTS.md:1041-1062` (the NOT-claimed row becomes this claim) and `:909`; the refusal (`G:6765-6767`) and `GatewaySchema.cs:425` add the owner's card; `USER-GUIDE.md` near `:867`.
**Answer first, in the report:** can an ATAS stream-state row carry an empty `ConnectorOrderId` (`AtasStrategyAdapter.cs:3240`: else `ext:none/<comment>`, which `Place` sets)? If so, item 3 also writes a
marker item 2 reads, as `OwnerResolved` reads `ResolvedByOwnerPrefix` (`G:6120-6124`). **NOT claimed, in CONTRACTS:** an unprovable connector (the sweep returns, `G:6630`: ATAS until its id is re-proven);
a read past ATAS's retention, which throws for ever (`AtasStrategyAdapter.cs:1018, 1024`); "It was filled" records a state, not a fill; a run's END settled CANCELLED owes no close (`OwesItsClose` `G:737`);
after an ATAS restart "not listed" is weaker (a new strategy's `Orders`; `ICache`, `MyTrades` NOT VERIFIED) and treated alike.
Tests (red-first; `InFlightSettleTests.cs` `Ready`/`AWorkingClose` `:39-72`, restart `:232-258`; a pass = `RefreshHealthAsync`, then `ReconcileAsync` if `HasUnconfirmedWork()`; `ClosesCarryTheId = false` is
ATAS's absence; `HistoryThrows` `RecordingConnector.cs:223`): (a) `An_order_the_platform_cannot_account_for_reaches_the_owner_past_the_clock` (inside the clock nothing; past it RECONCILING, flagged, in
`Unreconciled()`, a new order `TRADING_PAUSED_UNRECONCILED`, ten passes the same, no "never reached"); (b) `The_owner_settles_it_and_the_held_close_goes_out` (FILLED and CANCELLED arms, flat);
(c) `A_row_the_platform_still_lists_working_is_never_handed_over`; (d) `A_read_that_threw_hands_nothing_over` (and `Faults.HideOrderHistory`); (e) `A_later_platform_answer_settles_it_without_the_owner`;
(f) `The_reconciler_never_writes_off_an_answered_order_as_never_reached` (an UNKNOWN row with a reference; the plain simulator unchanged). (a), (b), (f) RED on the base, quoted;
the landed (d)'s `noId` arm (`:207`, `:216-221`) is this case: its assertions move to the hand-over, name kept, said in the report. **Mutant:** item 2's guard dropped ⇒ (a) red, quoted.
**Seat P:** `U-loss-hold-release` (landing) gates `AskTheHistoryAsync` on `ReconciliationProvable` (not `Unlisted`); `U-valuation-close-confirm` deconstructs its tuple; `U-press-close-once` sees a
handed-over row "waiting to be confirmed" (`G:6713-6715`), its case (d). Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release
`--no-incremental` 0 warnings; Unit and Fault 0 failed locally, touched classes 3×; the full suite on CI via `fleet/bin/ci-dispatch.sh` (run id, every job's verdict); names vs `main` 0 removed (both set
sizes printed); the tests box run or "tests box: NOT RUN — <ready's answer>"; `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
