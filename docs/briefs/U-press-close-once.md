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
## Report
Code tip `9d5ea94f` on `main` `6bd92855`: `9e692e7c` red-first tests, `c1a31a56` item 1, `9d5ea94f` item 2; this report is the last commit, docs only.
Gate (Release, this Mac, at the tip): `--no-incremental` build "0 Warning(s) 0 Error(s)"; Unit "Failed: 0, Passed: 1466"; Fault "Failed: 0, Passed: 470"; `PressCloseOnceTests` + `InFlightSettleTests` + `SecondPressRefusedTests` 3×, "Passed: 17" each run.
CI `37696688736` at `9d5ea94f`: ubuntu-latest success 13 min, macos-latest success 24 min, windows-latest success 54 min, package success 3 min. Item 1 alone, `37694859394` at `c1a31a56`: all four success (windows 48 min).
Names vs `main`: 2298 → 2304, removed 0, added 6 (the new class); [Fact]/[Theory] 2250 → 2256. Tests box: NOT RUN — `ready : NO - the machine does not answer (…)` (22:33Z, exit 1, not retried).
Item 1 DONE: `CloseCapturedAsync`, `ClosePress` legs only, after the UNKNOWN settle → `AskAboutTheClosesInFlightAsync`: every non-DISPATCHING `ClosesInFlightOn` row through `SettleAnOrderInFlightAsync` alone, deadline checked per row; (a) sends; (b) cancelled, asked again, else refused on every press; (c) waits, no row; (d) unsettled-shape row THEN `AddKvOnce("press_told:{request}")`, and a press with another nonce finding it still undecided closes over it, named; (e) drift, nothing sent. App legs untouched.
Item 2 DONE: the summary names every waited, refused, told, closed-over or cancelled order and the one action that ends it; the (d) row says "press Close all again, and it closes ES over that order: should it still be working and fill, ES ends the other way by up to 2"; CONTRACTS claim with its limits; USER-GUIDE; CLOSE_IN_FLIGHT's "There is nothing to do" corrected.
Deviations: (1) order of questions — press rows first, every other row asked before any cancel, so a leg that will not send cancels nothing; (2) (d) is the complement — any answer neither settled nor live (also an absence inside the clock, a row moved while asking); a row the stream made final meanwhile counts as settled; (3) a press settle marks the fill pull due, as the sweep's does; (4) `SecondPressRefusedTests.A_second_close_all_is_refused_while_the_first_is_unresolved`: its second half asserted a second close beside press 1's still-resting close (`Assert.Equal(2, c.Closes)`) — the doubling this unit removes, case (c) — and now asserts 1 close, the resting row named, flat once it fills; first half and name unchanged; (5) `InFlightSettleTests`' helpers made `internal`, nothing else.
RED before item 1 (base binaries): (b) "sells at the wire : 1 before the press, 2 after", "ES -2", `Expected: 0 Actual: -2`; (b, never closed over) `Expected: 0 Actual: 1` closes; (c) "closes at the wire : 1 before, 2 after"; (d) press 1 closed ES beside the undecided order, `Expected: 1 Actual: 2`; (e) "the book ES -1", `Expected: 0 Actual: 1`. Guard (the flatten over the agent's WORKING close → one close) green before and after.
Mutant: item 1's call replaced by a Sends verdict ⇒ (b) red, `Expected: 0 Actual: -2`, "sells at the wire : 1 before the press, 2 after", "ES -2"; restored, rebuilt, green.
(e) simulated by `RecordingConnector.PositionsTrail` (inert unless set): a frozen positions list that both the read and the close's own sizing answer — ATAS's `ClosePosition` sizes from its position object — cleared when the read catches up.
NOT done / NOT verified: (c) residual — another press's in-flight row the owner confirmed "still working" whose update was then lost holds that leg until the platform reports it (the card offers only "it is working" for such a row, and nothing settles a press's row); NOT claimed in CONTRACTS, for the manager. Where ATAS cannot prove its history every in-flight row is (d), never (b) — read, NOT run. ATAS box not granted: whether ATAS lists a cancelled close at once after the press's cancel is NOT VERIFIED (a slow list refuses the leg, named). A lost `press_told` write is not exercised by a test. No app run; Integration on CI only.
