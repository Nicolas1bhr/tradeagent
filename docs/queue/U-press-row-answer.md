# U-press-row-answer — a press's own close the owner answered "still working", whose platform update is then lost, holds his Close all for good: the card has no other answer, and the row is on no card
**Protects:** OWNER AUTHORITY and the MONEY PATH (the Dashboard card, the gateway): his Close all must be able to end, and must never send a close beside an order the
platform still lists live (a long becomes a short). `U-press-close-once`'s (c) residual, NOT claimed at `docs/CONTRACTS.md:1069-1072`; owed BEFORE LIVE. In-process only.
**Facts (SOURCE at `main` `82b3592c`, unchanged in these files since `7e29fc65`; read, not run; `G` = `src/TradeAgent.Gateway/TradingGateway.cs`, `D` = `src/TradeAgent.App/DashboardView.cs`).**
- Close all waits on another press's own row in flight and never asks, cancels or writes about it (`G:11421-11443`); the unflagged arm (`G:11439-11442`) is a row he
  answered "still working". Nothing else settles a press's row: the reconciler skips it (`G:11800-11805`), the in-flight sweep excludes it (`G:6713`),
  `SettleAnOrderInFlightAsync` declines it (`G:6855-6857`), the UNKNOWN settle refuses on it (`G:8627-8628`).
- ACKNOWLEDGED, WORKING, PARTIALLY_FILLED, CANCEL_PENDING get one answer, "Our record is right — it is working" (`D:755-768`, `D:935-938`); FILLED, CANCELLED only other
  states (`D:770-784`). `ForceResolve` (`G:12135-12213`) has no press rule — wire refusal `G:12156`, same state clears flag and latch `G:12172-12184`, terminal never
  re-answered, else the move with `resolved by user:` `G:12201` — and both are legal from the four (`src/TradeAgent.Core/OrderStateMachine.cs:58-61`): the card withholds them.
- The card's only feed is `Unreconciled()` (`D:306`; `G:2966-2978`), so a row answered "still working" (flag cleared) is on no card. Fixture:
  `tests/TradeAgent.FaultTests/PressCloseOnceTests.cs:189-236`; the lost update: `FakeBroker.Cancel` (`src/TradeAgent.Connectors.Fake/FakeBroker.cs:199-208`). Precedent,
  `TheOwnersAnswerAsync` (`G:9944-9979`): where `ReconciliationProvable`, a non-final listing or a read that throws decides nothing, "…outranks your answer" (`G:9954-9971`).
- A flag reopens its press (`G:8218-8225`): that kind's next press refused until answered (`G:8309-8313`), the loss flatten held (`G:9011-9017`), the reopen gate named
  (`G:4533-4537`). A press row's insert loses its claim to ANY flagged row of its kind (`src/TradeAgent.Core/Db/Stores.cs:156-158`, `G:8422-8423`).
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; the `U-press-close-once` and `U-loss-hold-release` records; `G:6639-6668, 8218-8230, 8397-8440, 9944-9979, 11021-11120,
11290-11308, 11367-11520, 12135-12213`; `D:198-204, 600-865, 925-945`; `InFlightSettleTests.cs:30-80`; `ForceResolveRouteTests.cs`.
Must NOT: reach the answer from a pipe op, a `trade` verb or a GatewayHost console verb (grepped in the report); let a press ask about, cancel or close beside a press's
own row; settle a press row from absence, or put the reconciler, the sweep or `SettleAnOrderInFlightAsync` on one; let his answer stand where a provable platform lists
the order non-final or cannot be read; change `ForceResolve`'s rules or any other row's answers; flag before every leg has written its rows; add a kv, table or rung.
Commits, one each, one-sentence messages:
0. **Red-first tests and the two seams they call, behaviour unchanged:** the card builds each row's answers from one pure `DashboardPage.Answers(state, isPress, reference)`
   → (label, armed, outcome); its one route is `TradingGateway.AnswerFromTheCardAsync(requestId, outcome, note, ct)` — `ForceResolve`, then `RefreshHealthAsync`
   (today's `D:843-850`). New `tests/TradeAgent.FaultTests/PressRowAnswerTests.cs` over `InFlightSettleTests.Harness` and that fixture: (a)
   `A_press_close_the_platform_dropped_reaches_the_card_and_the_next_close_all_closes_once` — the loss flatten's close rests WORKING, he answers it still working, the
   platform cancels it unreported; Close all 1 sends nothing AND the row is in `Unreconciled()` (RED here); he answers CANCELLED by the route; Close all 2 sends one
   close; ES flat, one sell. (b) `A_press_close_the_platform_still_lists_working_is_never_answered_away` — his answer refused, named, nothing written; Close all 2
   sends nothing; price arrives; one sell, ES flat (RED here: a second sell, ES −2). (c) GUARD `Where_no_history_can_be_asked_his_answer_stands` (`HideOrderHistory`):
   written unread, Close all 2 closes once. (d) `UnconfirmedCardTests`: CANCELLED and FILLED offered a press row in each live state, one answer otherwise (RED here).
1. **The answers and the veto.** A press's own row in those four states gets "It is no longer working at your platform" (CANCELLED — not "It did not fill": a partly
   filled row did) and "It was filled" (FILLED) beside its own: two presses, a note, hidden on the wire, as every answer. For such a row answered otherwise, the route
   runs `TheOwnersAnswerAsync`'s read and veto where `ReconciliationProvable` — one helper both call, `LossHoldReleaseTests` 8/8 as before — refusing a non-final
   listing or a failed read; elsewhere his word stands, as for an UNKNOWN row. The words at `G:11436-11438` and `docs/USER-GUIDE.md:909-911` now hold.
2. **Back on the card.** After every leg (`G:11305`, so no earlier Close all's row takes this press's claim), the owner's Close all flags again each press row it waited
   on in the unflagged arm — `MarkNeedsReconciliation` (`Stores.cs:318`), a sentence naming this press and the answer; a refused write is said, never thrown — and that
   arm's words say so; CONTRACTS `:1069-1072` becomes a claim with its limits; the guide names the answer.
Proof: RED at commit 0, quoted — (a) "not on the card", (b) "ES −2", (d) one answer. `PressCloseOnceTests`' (c) test keeps its name and every assertion but the
words at `:231-232`, which follow item 2 — declared. ONE mutant: the route's veto dropped ⇒ (b) red, two sells; quoted, restored. NOT provable here: ATAS (no box), the card on screen.
Gate and report per `docs/FLEET.md` "The builder pass": rebase on `main`; `-c Release --no-incremental` 0 warnings; Unit, Fault 0 failed; touched classes 3×;
`ci-dispatch.sh` + `ci-wait.sh`, every job quoted; tests box or "NOT RUN — <ready's answer>"; names vs `main` 0 removed; `## Report` ≤ 20 lines here; no merge.
