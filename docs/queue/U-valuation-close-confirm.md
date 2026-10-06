# U-valuation-close-confirm — a data-loss exit whose close lost its answer pauses all trading and blocks every later exit until the owner acts; confirm it from history and close again while its reason holds
**Protects:** the data-loss exit (`U-flatten-3`) on the money path. A position nobody can value must not stay open because one close lost its answer. `CLAUDE.md`
rule 3: a lost close is never re-sent blind; it is decided only by the platform's history or by the owner. Owed BEFORE ANY LIVE USE (the `U-flatten-confirm` record).
Fresh builder, seat P; no rung (kv JSON only). **Dispatch after `U-loss-hold-release` LANDS:** same confirm code; re-check every `G:` pointer then.
**Facts (SOURCE at `bdf5affa`, `G` = `src/TradeAgent.Gateway/TradingGateway.cs`; RUN = seat P's survey probe, quoted).**
- `ExitLostValuationAsync` (`G:10055`) runs once per episode: `loss_valuation_exit:` written once (`G:10058`, `:10180`), and the episode points at it (`G:10193`).
  `AnswerLostValuationsAsync` never exits an episode again (`G:9947`).
- While any exit row is flagged, every later exit is declined (`G:10073-10079`). `ReconcileAsync` never settles a press row (`G:10790-10795`). The confirm reads loss
  breaches only (`ConfirmLostClosesAsync` `G:9352`, called `G:11251`).
- RUN, the close filled and its answer was lost (ES and NQ both unvaluable):
  - "exit record : loss_valuation_exit:fake:SIM-001:ES:… flat=False legs=[op-valuation-close-…-0 UNKNOWN]".
  - 30 simulated minutes later: "closes 1; ES 0 NQ 1", "unconfirmed work : True", "an agent's new order : 2 earlier request(s) are unconfirmed",
    "valuation_exit_press_already_open x91" — NQ is never exited.
  - Only the owner's card releases it: "after the owner : closes 2; ES 0 NQ 0".
- RUN, the close never reached the broker: after the owner settles, "closes 2; ES 1 NQ 0" — ES, still unvaluable, is never exited again.
- The confirm's two history questions and the absence rule are per leg (`AskTheHistoryAsync` `G:9514`, `AbsenceDecidesALostClose` `G:9576`). The exit's legs and
  nonces have the flatten's shape (`AccountForTheFlattenAsync` with the valuation kinds; `ValuationExitRecord`, `src/TradeAgent.Gateway/ValuationLoss.cs:244`).
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; `G` `ExitLostValuationAsync`, `AnswerLostValuationsAsync` (`G:9925`), the confirm (`G:9317-9700`),
`ValuationReading` (`G:10255`); `src/TradeAgent.Gateway/ValuationLoss.cs`; `tests/TradeAgent.FaultTests/LossFlattenConfirmTests.cs`; the `U-flatten-confirm` and `U-flatten-absence` records.
Must NOT: decide a leg from absence beyond `AbsenceDecidesALostClose`, or where not `ReconciliationProvable`; send a close over an undecided leg, or once the position
can be valued again; rewrite an exit record; change the exit's bound, the grace or the precautionary cancel; touch the loss flatten's records; weaken an assert.
Items, one commit each, one-sentence messages:
1. **Confirm a lost exit close.** For an exit whose lost close legs are still UNKNOWN (or settled by the owner — `U-loss-hold-release`'s owner-decided-leg rule
   and its live-order veto, unchanged), with every other row of its two nonces final:
   `AskTheHistoryAsync` per leg, unchanged. Once every leg is decided: ONE write-once `loss_valuation_exit_confirm:` (verdicts and a fresh read of the symbol),
   BEFORE any row is settled. Then `SettleTheUnresolved`'s two steps and the exit's own rows unflagged; the pause lifts and other exits resume. In the health pass,
   beside `G:11251`.
2. **Close again once, only while the reason holds:** still open AND its episode still standing → one more exit under `loss_valuation_exit_again:` (cancels first,
   reduction only). Valued again → nothing sent, said. A lost answer to the again stays for the owner.
3. **Words:** `ValuationReading` shows the latest word (again, confirm, exit) instead of the exit's alone. `docs/CONTRACTS.md`, `docs/USER-GUIDE.md` and `AGENTS.md` say so.
Proof: RED before items 1-2, quoted.
- (a) filled, answer lost → confirmed from history; NQ's exit goes out on the next pass; one ES close at the wire.
- (b) never reached, on the simulator, past the grace → CANCELLED, closed again once, flat.
- (c) guard: never reached, valued again before the confirm → confirmed, nothing sent.
- (d) guard: history hidden and no answer from the owner → nothing decided, nothing sent (green before and after).
ONE mutant, quoted: the "episode still standing" check dropped → (c) red, a close at the wire. `LossFlattenConfirmTests` and the valuation tests unchanged and green.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; Unit, Fault 0 failed;
touched classes 3×; branch CI on all three platforms; tests box or NOT RUN; names vs `main` 0 removed; `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
