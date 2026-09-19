# U-runner — the frozen version runs forward on paper: closed bars in, intents through the existing gateway, protection by code, crash-safe replay
**Arrow closed:** deployment → forward intents → fills (`docs/PRINCIPLES.md`: "Frozen strategies execute through the app's deterministic runner and existing
gateway"; "no model call is required for each signal or for emergency protection"). **Observable result:** a closed forward bar drives the deployed program
through `StrategyEvaluator.Step`; an entry becomes a `PlaceIntent` naming the version and the allocation, dispatched under the deployment identity through
`PlaceAsync` and every gate, filled by the paper connector at the next open; the stop and the target rest at the venue and the loser is cancelled; the
maximum hold closes at the bar's close before the evaluator is asked; a crash after an order was accepted recovers on restart with exactly one order at the
wire. No schema rung. MONEY PATH in PAPER: the first production producer of a `PlaceIntent` from a `StrategyIntent`. Test (e) is Astra's one pre-run
insistence. The record may claim "forward paper observation under declared bar-fill assumptions" and nothing more.
Read first: `docs/PRINCIPLES.md` §§ Evidence, loop; `docs/CONTRACTS.md` "The backtest" (2019-2088), "The paper connector", "The paper deployment", "Forward
bars"; `StrategyEvaluator.cs:38-80,142-156,251-265,356`; `StrategyEvaluation.cs:27,83-116,171`; `Backtest.cs:435-487` (protection BEFORE the event; then
`AccountReading`, `Step`, exit sizing, `RoundDown`); `GatewayTypes.cs:317-337` (`IntentDecision.From`); `TradingGateway.cs:4859-4893`, `:4200`;
`ForwardBarStore.Since`/`Freshness`, `BarClosed`; `IPaperBarSource`, the paper fill semantics; `Deployments`, `deployment_op`, `AgentContext.Deployment`. Rebase first.
Items, one commit each with a one-sentence message:
1. `ForwardRuns` (Gateway, beside `Backtests.cs`): on each `BarClosed(symbol, open)` — and on start-up for every bar since the cursor — for each `active`
   deployment on that symbol, rebuild `EvaluationState` deterministically by replaying the forward bars from the deployment's start (`EvaluationState.Start`
   with the frozen program and the bar interval; `Step` per bar with the account reading recorded on that bar's op rows); a fault is sticky and ENDS the
   deployment with the reason; an out-of-order or duplicate bar is skipped with a recorded note, never stepped twice.
2. Protection first, by code, in the backtest's order: the resting stop and target are the venue's business (placed as orders in item 3); the maximum hold is
   the runner's — at the close of the bar reaching the limit a `flatten` op closes at market BEFORE `Step` is asked, so the evaluator reads a flat account.
   Sizes rounded DOWN to the increment; a size that rounds to nothing is a recorded no-trade with the reason.
3. Dispatch: each `StrategyIntent` → `IntentDecision.From(intent)` (null → the deployment ENDS with "program declares no bounds"; the referee refuses such
   programs, say so) → `PlaceIntent` (market, `StrategyVersionId`, `Decision`, comment `deployment:<id> rule <n>`) → a `deployment_op` written `planned` →
   `PlaceAsync(AgentContext.Deployment(id), op.RequestId, intent)` → `dispatched` → the answer recorded (`SENT` or the refusal code — `DECISION_EXPIRED`,
   `ALLOCATION_EXCEEDED` included) → `resolved`/`refused`. On an entry FILL (the gateway's execution event for that request id) the stop and the target are
   placed as resting stop and limit orders under the same identity as `stop`/`target` ops; when one fills the other is cancelled; an `exit when` signal is a
   market close op. The cursor advances only when the bar's ops are all resolved or refused; a refused op advances it too.
4. Feedback: fills land in the fill ledger scoped to the paper connector and account, attributed to the version and the deployment through `execution_request`;
   `pnl` and the report show them; a deployment's END and each UTC day-close raise ONE persisted wake to Research with a note (fills, net after declared costs,
   state, "a declared simulation at the next open") — paper results are the role's own experiment, not holdout evidence, so the figures may cross.
   `CONTRACTS.md` "The runner": what is claimed and what is not (no executability, no queue position, bar-granularity protection).
Red-first tests (Integration, the paper connector with the in-memory bar source, an injected clock; the envelope, the paper-eligible version, its allocation and
deployment set up through the PRODUCTION calls, never rows): (a) `A_closed_bar_drives_an_entry_through_PlaceAsync_and_the_paper_connector_fills_it_at_the_next_
open` (wire count 1; the request row names version, allocation and `deployment:<id>`); (b) `The_stop_and_target_rest_after_the_entry_fills_and_the_loser_is_
cancelled_when_the_other_fills`; (c) `The_maximum_hold_closes_at_the_bars_close_before_the_evaluator_is_asked`; (d) `A_stale_bar_is_refused_DECISION_EXPIRED_
recorded_and_the_cursor_still_advances_with_nothing_at_the_wire`; (e) `A_crash_after_the_order_was_accepted_recovers_on_restart_with_exactly_one_order_at_the_
wire_and_the_same_position` (the production path, both halves); (f) `A_second_runner_over_the_same_rows_reaches_the_same_state_and_the_gateway_refuses_its_
duplicate_request_id`. Mutants to watch red and quote: (i) the cursor advanced before the ops resolve → (e) red; (ii) the maximum hold checked after `Step` →
(c) red. Nothing removed or renamed.
Gate and report as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed; classes 3×; names 0 removed; `## Report` ≤ 20 lines
here). No push, no merge; touch nothing in `docs/briefs/` but this file.
