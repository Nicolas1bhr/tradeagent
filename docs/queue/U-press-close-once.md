# U-press-close-once — the owner's Close all still sizes a close beside a same-side market close in flight; ask the platform first, through seat A's settle
**Protects:** the owner's emergency press, on the money path (gateway). A press that sends a close beside one still working turns a long into a short after the
control whose purpose is to flatten. `CLAUDE.md` rule 3: an order without a final answer is never closed over silently; and an emergency press never leaves risk
open with no way out (`U-fix-press-budget`): every leg it holds names ONE explicit owner action that ends the hold. Owed BEFORE ANY LIVE USE (`U-close-once`,
`U-inflight-settle` records; `docs/CONTRACTS.md:1047-1052` NOT claimed). Fresh builder, seat P; no rung. **It CALLS `SettleAnOrderInFlightAsync`; never a second one.**
**Facts (SOURCE at `cc7a0974`, `G` = `src/TradeAgent.Gateway/TradingGateway.cs`; RUN = seat P's survey probe at `bdf5affa`, quoted).**
- ONE function sizes a close outside `ClosesInFlightOn` (`G:6576`; the agent's `CLOSE_IN_FLIGHT`, `G:6903`): `CloseCapturedAsync` (`G:10991`), whose
  `ClosePositionAsync` (`G:11163`) the connector sizes at that instant (ATAS: `AtasStrategyAdapter.cs:1960`). Callers: Close all (`G:8786`), the loss flatten
  and its again (`G:9046`), the data-loss exit (`G:10688`); the app's legs cancel at the platform first, the owner's does not. Per leg today: UNKNOWN reducers
  (`G:11046` → `G:8620`), drift (`G:11088`), DISPATCHING (`src/TradeAgent.Core/Db/Stores.cs:160-163` → `waited`). Nothing else.
- RUN, the agent's close WORKING, then Close all: "sells at the wire : 1 before the press, 2 after", "pos [ES -2]"; the flatten's: "closes=2", "ES -1".
- `SettleAnOrderInFlightAsync` (`G:6636`) answers `InFlightAnswer(Settled, Live, Why, Unlisted)` (`G:6608`); it asks only about an unflagged non-press PLACE in
  ACKNOWLEDGED, WORKING, PARTIALLY_FILLED or CANCEL_PENDING where `ReconciliationProvable`; any other row comes back with who settles it (`G:6847`).
- The sweep hands an `Unlisted` row to the owner's card 15 s after its dispatch (`G:6790`: flagged, RECONCILING); `ClosesInFlightOn` still counts it. A second
  Close all is refused while the last is unresolved (`G:8306`): the owner answers it on the Dashboard first.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; in `G` `CloseCapturedAsync`, `OperatorCloseAllAsync` (`G:8740-8830`), `SettleAnUnresolvedReducerOrRefuse`, the
in-flight block (`G:6550-6880`); the `U-inflight-settle`, `U-inflight-owner` records; `tests/TradeAgent.FaultTests/` `UnknownCloseTests`, `InFlightSettleTests`, `EmergencyPressTests`.
Must NOT: read the platform about, or settle, an in-flight row other than through `SettleAnOrderInFlightAsync`; net the in-flight quantity; refuse a whole press
for one leg; settle, cancel or close over another press's row; add the step to the app's legs; touch `BeginExcludingTheStore`, the drift rule, the wire clause,
`SettleAnUnresolvedReducerOrRefuse`, the sweep, the handover or the agent's `CLOSE_IN_FLIGHT` refusal; weaken an assert.
Items, one commit each, one-sentence messages:
1. **Ask before an owner's leg sends.** In `CloseCapturedAsync` after the UNKNOWN settle, for `ClosePress` legs only, under the press's deadline (checked before
   each row, as `G:8627` does), each row `ClosesInFlightOn` counts on the leg but DISPATCHING goes through `SettleAnOrderInFlightAsync`; its answer decides:
   (a) settled, nothing filled → the drift re-read and the close, as today;
   (b) `Live` → cancelled at the platform, then asked again: (a) sends, (e) applies, anything else refuses the leg, named, on every press (it is listed live);
   (c) declined as a press's own row → the leg waits, named: it is waiting for his answer on the Dashboard; press again once he has given it;
   (d) undecided — `Unlisted`, not provable, a read that threw, the deadline, or declined as a flagged RECONCILING row → refused in the `unsettled` shape (a
       flagged row, `sending: false`, naming the order, its last state and the answer's words), THEN a write-once kv record (`Database.AddKvOnce`) that this
       press told the owner; a later Close all finding the SAME order still undecided, told by an EARLIER press, closes the leg as any leg, naming it — the
       owner's explicit second press, never a silent send nor a hold with no way out (a lost kv write: the next press tells again);
   (e) settled with any quantity filled → nothing sent, said as drift: the position changed after the press and its read may not show that fill yet.
2. **Words:** the press summary names each waited, refused, told or closed-over order and the one action that ends it; the (d) refusal says what a second press
   does and its risk (should the order still be working and fill, the position ends the other way by up to its size). `docs/CONTRACTS.md:1047-1052` becomes a
   claim with its limits; `docs/USER-GUIDE.md` (Close all, :887); `src/TradeAgent.Core/Errors.cs:862` CLOSE_IN_FLIGHT's "There is nothing to do" corrected.
Proof, RED before item 1, quoted: (b) the agent's close WORKING, then Close all → cancelled, one close, flat; (c) the flatten's close WORKING → no second
close, named; (d) the agent's sell listed nowhere → press 1: ES refused, named, NQ closed; resolved; press 2: ES closed, named; (e) the agent's sell 1 under a
long 2 filled, its update lost, the position read still 2 → nothing sent for ES; press again → closes 1, flat. Guards, green before and after: the flatten over
the agent's WORKING close → one close; `PressSettlesAnUnknownClose`, `OperatorPressIsAnEmergency`, `LossFlatten*`, `InFlightSettle` tests unchanged. ONE mutant: item 1's call removed → (b) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault
0 failed; touched classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed; `## Report` ≤ 20 lines appended here.
