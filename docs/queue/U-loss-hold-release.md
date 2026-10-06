# U-loss-hold-release — a lost loss-budget close the owner has settled on the Dashboard holds the closure for ever; count his answer as a decided leg
**Protects:** the loss boundary's reopen and flatten (gateway, money path). A closure must not lift over a book nobody has accounted for. It must not stay closed for
ever over a leg the owner has answered, either. Today the only way out of an ATAS lost close is that answer, so every such closure ends in a permanent hold.
`CLAUDE.md` rule 3: the owner's word is his own measurement, through in-process operator authority. A platform that says the order is still live outranks it.
`U-flatten-confirm`'s deviation (c), owed BEFORE ANY LIVE USE. Fresh builder, seat P; no rung (kv JSON only).
**Facts (SOURCE at `bdf5affa`, `G` = `src/TradeAgent.Gateway/TradingGateway.cs`; RUN = seat P's survey probe, quoted).**
- `LostCloses` (`G:9478`) answers null, so no confirm, when a lost leg is no longer UNKNOWN (`G:9495`) or a cancel-half row is non-final (`G:9487`).
  The confirm runs only where `ReconciliationProvable` (`G:9389`).
- The owner's card is the only route into `ForceResolve` (`src/TradeAgent.App/DashboardView.cs:198-203`). For an UNKNOWN row it offers FILLED or CANCELLED
  (`:763-766`), written with `ResolvedByOwnerPrefix` (`G:6120`, `G:11181`). It writes no `loss_flatten*` record.
- `HeldBy` (`G:4380`) reads `LatestFlattenWord` (`G:9709`): the first outcome's `Flat=false` →
  "TradeAgent cannot confirm that what it closed for you is closed" (`G:4450`). `ReleaseHold` lifts review holds only (`G:5008`).
- RUN (history hidden, as on ATAS; the owner settles the lost close FILLED and confirms every other row): "confirms 0; again 0", "unconfirmed work : False",
  "past eligibility : reopened []; day closed True", "held by : TradeAgent cannot confirm…". The Dashboard still asks him to confirm records, and none is left.
- RUN (the close never reached the broker; the owner answers "No order exists"): "closes 1; ES 1", held the same — nothing is ever closed again.
- RUN (the cancel half lost, the close lost; the owner settles the cancel row only): "confirms 1", "reopened [loss_reopen:…]" — today's route; it needs a provable history.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `G` `ConfirmLostCloseAsync` (`G:9379-9465`), `LostCloses`, `AskTheHistoryAsync`, `ApplyTheConfirm` (`G:9595`),
`ConfirmSentence` (`G:9649`), `ForceResolve` (`G:11115`), `HeldBy`; `src/TradeAgent.Gateway/LossFlatten.cs:132-175`; `tests/TradeAgent.FaultTests/LossFlattenConfirmTests.cs`;
the `U-flatten-confirm` and `U-flatten-absence` records.
Must NOT: decide a leg from absence beyond `AbsenceDecidesALostClose`; let the owner's word override a close the history finds live; write a record family the
landed confirm does not (`loss_flatten_confirm:` write-once, `loss_flatten_again:`/`…_again_owed:`); send a close except through the again-flatten's cancel-first;
touch `ReleaseHold`, the review hold or the reopen's other holders; make anything reachable from the pipe; weaken an assert.
Items, one commit each, one-sentence messages:
1. **An owner-settled lost leg is decided.** Its row is terminal with `ResolvedByOwnerPrefix`. The verdict is that state and fill, evidence "you confirmed it on the
   Dashboard: <note>". Where `ReconciliationProvable`, the history is still asked: an order it finds live decides nothing, and that is named. `G:9389` gates only the
   history question, so ATAS confirms once every lost leg is answered. Then the landed confirm: flat → nothing sent; open → closing again (the orchestrator's
   `U-flatten-confirm` ruling, which seat P extends to owner-decided legs).
2. **Words:** `ConfirmSentence` names each verdict's source (the platform's history, or your answer on the Dashboard); no word asks the owner to confirm records when
   none is left. `docs/CONTRACTS.md`, `docs/USER-GUIDE.md`, `AGENTS.md` and the status schema's `loss_flatten` text say so. The cancel half stays as it is (RUN above).
Proof: RED before item 1, quoted.
- (a) history hidden, the owner settles FILLED and every row → confirm written, "flat", reopened past eligibility.
- (b) the close never reached the broker, the owner answers CANCELLED, ES 1 → one closing again, flat, once.
- (c) guard: the owner answers CANCELLED while the close is WORKING at the platform → no confirm, nothing sent, named.
- (d) guard: the cancel half settled by the owner, the close from history → confirmed (green before and after).
- (e) guard: history hidden, the owner answers CANCELLED while the close still rests WORKING → the again cancels it at the platform first; one close; flat, never short.
ONE mutant, quoted: the live-order veto dropped → (c) red. `LossFlattenConfirmTests`, `LossReopenTests`, `LossReleaseTests` unchanged and green.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed;
touched classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed; `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
