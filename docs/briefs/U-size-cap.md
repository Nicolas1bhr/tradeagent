# U-size-cap — a risk-sized entry may declare the most of its capital it spends, `size risk_fraction <f> max_capital_fraction <c>`, so a tight stop sizes to the cap instead of to no trade; the shipped breakout gains it
**Arrow closed:** "research and create a candidate → test" (`docs/PRINCIPLES.md` § The loop). Attempt 3's Research: "55 signals unfunded", `risk_fraction` "has no cash-notional cap" (`BUILD-STATUS.md` at
`cc7a0974`, gap (c)). Its claim is true of the product (below); the language cannot say what it asked for. **Depends on `U-language-v2a`, LANDED at `78be3e9d`**; seat A re-checked this brief there on
2026-10-08 and every pointer below is `78be3e9d`'s. **Then:** M0 attempt 4. **Protects:** total parse and freezing by hash — every v1 and v2a text keeps its canonical form, id and trace, the one DECLARED
exception being item 4's shipped file; leverage stays unspellable (`StrategyLimits.cs:115-120`); every gate is untouched — allocation ceilings (`TradingGateway.cs:3601`, `:3610`), per-order value (`:3227`,
`:3320`), the paper envelope, the loss budget, two-press — because a cap only ever makes a size smaller; rounding DOWN (`ForwardRuns.cs:761-787`) unchanged; paper confers no live authority.
**Today:** a program has one sizing rule (`docs/STRATEGY-LANGUAGE.md:21`); `risk_fraction` is `equity * f / (close - stop)` with nothing bounding it (`:248`; `StrategyEvaluator.cs:721-735`;
`StrategyParser.cs:624-655` reads exactly two words). In a backtest an entry whose cost passes the cash is dropped whole — "the declared capital cannot pay for this fill" (`Backtest.cs:553-566`) — and
every such reason is a `missing` line of the answer (`BacktestMetrics.cs:163`, `:208`); on paper the entry goes at the full declared size (`ForwardRuns.cs:443-457`, capital = the allocation's ceiling
`:1021-1032`) and the gateway refuses it whole, ALLOCATION_EXCEEDED, "so nothing was sent" (`TradingGateway.cs:3601-3612`). The arithmetic: with `stop percent p` the notional is the constant `100f/p` of
equity — `risk_fraction` is then a `capital_fraction` in disguise and an `f` a margin below `p/100` (costs and the next open on top) never overshoots; with `stop atr` or `stop fixed` it is `f * close / distance`,
without bound as the stop nears the price, so no `f` keeps a tight-stop signal funded and no declared `capital` does either (size and cost both scale with equity). Risk sizing differs from capital sizing exactly
where it cannot be bounded. The agent was told this accurately (`STRATEGY-LANGUAGE.md:247-251`, copied into its folder, `ResearchLibrary.cs:16`; `GatewaySchema.cs:366`): no misreading. In a backtest the account's `StrategyCapital` is the cash left (`Backtest.cs:632-638`); on paper it is the allocation's ceiling (`ForwardRuns.cs:1093-1095`).
**Observable result:** `size risk_fraction 0.01 max_capital_fraction 0.95` parses and the evaluator sizes `min(equity * f / (reference - stop), StrategyCapital * c / reference)` — what the base `capital_fraction`
reads (`StrategyEvaluator.cs:713-719`) — so backtest and paper agree; the trace's Signal line carries the capped quantity (`Backtest.cs:674`) and the trace format gains no column (every recorded
`trace_sha256` stays reproducible). A program without the clause parses, hashes, sizes and traces byte for byte as today. `StrategyVersions.Manifest`, `LanguageVersion` and `Referee.EvaluatorVersion` do not
move (no existing program's output changes, `EvaluationGoldenVectorTests` green unedited), so no verdict is withdrawn. **No schema change** (main at 30).
Read first: `CLAUDE.md`; `docs/STRATEGY-LANGUAGE.md`; `docs/EDGE-FACTORY.md` § 4.4; `StrategyDeclarations.cs`; `StrategyEvaluator.cs:690-740`; `Backtest.cs:530-690`. Items, one commit each:
1. The clause: `size risk_fraction <value> [max_capital_fraction <value>]` (a NUMBER or a declared constant); `Sizing` (`StrategyAst.cs:153`) gains the cap; `ReadSizing` refuses in words, naming the size
   line, a cap on `fixed` or `capital_fraction` (a capital fraction is already its own cap; a fixed quantity is a constant), a cap at or below 0 or above `MaxSizingFraction` (the leverage words), and any
   other tail; the canonical size form (`StrategyCanonical.cs:166-171`) gains ` max_capital_fraction:<c>` ONLY when declared — `bars`' and `feature`'s exception, so no existing canonical form changes; a
   declaration kind of its own in `StrategyDeclarations` (`All`, and `Of` when declared). `Backtest.Implements` is `All` (`Backtest.cs:363`) and `ForwardRuns.Implements` is `All` but `feature`
   (`ForwardRuns.cs:840-841`): both size through the evaluator, so both implement the kind — say so at each.
2. The size: `Quantity` (`StrategyEvaluator.cs:706-738`) returns the smaller of the risk size and `StrategyCapital * c / reference` when a cap is declared; the reference-price fault keeps its words.
3. The words: `STRATEGY-LANGUAGE.md` grammar (`:21`) and Sizing paragraph (`:247-251`) state the arithmetic above, that a cap applies at the signal's reference price so fees, slippage and the next open
   come on top (`c = 1` under the venue model's 0.1 % and 0.02 % is refused unless the next open is 0.12 % below the close — 0.95 leaves room), and that a gateway limit REFUSES an order whole and nothing
   downstream makes one smaller to fit (`:249-250`'s "the gateway's limits happen downstream", sharpened); `CONTRACTS.md`'s canonical form; `GatewaySchema.cs:366` adds that more capital does not fund
   a risk-sized entry, whose size grows with it, and names the clause.
4. The shipped breakout (`src/TradeAgent.AgentRuntime/Strategies/opening-range-breakout.strategy`: risk 0.01, `stop atr 2 14`, 1m) asks `0.01 * close / (2 * ATR14)` of equity — more than all of it whenever a
   minute's ATR(14) is under 0.5 % of the price (arithmetic, NOT VERIFIED by a run), whatever the capital — so it never trades. Seat A's call: its size line becomes `size risk_fraction riskfraction
   max_capital_fraction 0.95` (0.95, not 1, by item 3's arithmetic; a literal, so its parameters do not move), and `STRATEGY-LANGUAGE.md`'s copy (`:302-325`) with it. Its id moves — a DECLARED re-pin, named
   in the report: its OLD text becomes a test fixture under its old id `70ec1a6e…`, so `DeclaredBarsGuardTests.cs:36-76` and `FeatureProgramGrammarTests.cs:50-56` still prove a v1 text keeps its id;
   `DayOneStrategyTests.cs:54`, `:140` pin the new id and canonical form.
Tests: (a) `A_capped_risk_size_is_the_smaller_of_the_two` (evaluator: a small ATR sizes to `capital * c / close`, a large one to the risk size); (b) `A_tight_stop_fills_capped_and_is_no_trade_uncapped`
(backtest over a fixture whose ATR shrinks: without the clause the reason is "the declared capital cannot pay", with it a fill at the capped size); (c) `EvaluationGoldenVectorTests`,
`DeclaredBarsGuardTests` and v2a's golden vectors green UNEDITED but for item 4's fixture swap; (d) the refusals of item 1, each naming the line; (e) a paper run under an allocation ceiling: the capped entry
is sent and fills, the uncapped one is refused ALLOCATION_EXCEEDED exactly as today; (f) `The_shipped_breakout_is_funded_at_its_cap` (a backtest of the shipped file over a fixture whose minute ATR is under
0.5 % of the price fills at `0.95 * cash / close` where its old text answers "cannot pay"). Mutants, quoted red: the `min` dropped ⇒ (a), (b), (f); the canonical form writing the cap when undeclared ⇒ (c).
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed, touched classes 3×;
CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
