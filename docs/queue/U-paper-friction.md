# U-paper-friction — paper fills and research runs default to the venue cost model the referee judges with, and the friction in force is named everywhere
**Arrow closed:** forward paper evidence and research runs net of costs (`docs/EDGE-FACTORY.md` § 4.5: one cost model at every stage). **Depends on
`U-cost-model`** (`VenueCostModel`). **Today (SOURCE at `0f47db7`, R09/R11-checked):** `PaperFriction.None = (0, 0)` (`Connectors.Paper/PaperFriction.cs:17-35`),
and its `Sentence` (`:31-35`) calls any non-zero friction "declared by the account owner"; `AppHost.PaperChoice()` (`App/AppHost.cs:759-770`) builds the
connector from `Settings.PaperFeeFraction` / `PaperSlippageFraction`, non-nullable decimals defaulting to 0 (`Core/Trading.cs:392-398`), saved as explicit zeros
(`TradingGateway.cs:2247`, `Protocol.cs:298`); `git grep` finds NO writer of either field in `src/` or `tests/` (R11), so every stored 0 is a default. The fee
applies to every paper fill and slippage to market fills (`PaperConnector.cs:491-556`, fee `:553`, slippage `:509,536-539`); each fill already records its
friction sentence (`paper_fill.friction`, `PaperBook.cs:117,395-408`). Research runs without declared friction run frictionless, and three agent-facing texts
say so (`GatewayPipeServer.cs:2330-2333`, `GatewaySchema.cs:263`, `AgentRuntime/WorkspaceBuilder.cs:312-314`). **Observable result:** on an install that never
chose friction, a paper fill pays the venue's fee and the stated slippage, and a research run that leaves a friction field undeclared gets the venue model's
value FOR THAT FIELD; an owner's value (0 included) is used exactly; the Trading platform card, `status` and the daily report say which friction applies and
where it came from. **No schema change** (settings JSON only).
Read first: `docs/EDGE-FACTORY.md` § 4.5; `CLAUDE.md`; `PaperFriction.cs`; `PaperConnector.cs:30-40,180-215,480-560`; `PaperBook.cs:110-120,390-410`;
`PaperSettlementTests.cs:175-193` (asserts FRICTIONLESS on the connector default — keep that default); `Trading.cs:380-400`; `AppHost.cs:755-775`;
`App/SettingsView.cs:129,146-151` (the paper option row of the Trading platform card); `Backtests.cs:95-160` (`BacktestAsk`'s four friction fields);
`TradingGateway.cs:2335-2365`; `Gateway/DailyReport.cs:547-752`; `CONTRACTS.md:126-128`; `USER-GUIDE.md:195`; `VenueCostModel` as landed.
Items, one commit each, one-sentence messages:
1. `PaperFriction` gains a SOURCE (owner / venue cost model <id, sha> / none); its sentence says which; the connector's own default stays `None`.
2. New nullable settings `PaperFeeOverride` and `PaperSlippageOverride`; `PaperChoice()` resolves: an override (0 included) ⇒ owner; else the venue cost
   model of the paper connector's venue; the old fields stay readable and are no longer consulted (state the `git grep` evidence, and that a settings file is
   agent-writable until containment, in CONTRACTS).
3. Research: each friction field a `trade backtest` leaves undeclared takes the venue cost model's value, per field; declared fields win per field; the run
   records which applied. Update the three agent-facing texts above so none still promises a frictionless default.
4. Owner surface: the paper option row shows "fills pay <fee>% + <slippage>% (assumption) — <venue> standard taker, <date>", with an override (two fields,
   cleared with one press); `status` gains `paper_friction {source, fee, slippage, model}` with its `GatewaySchema` description; the daily report one
   line. `CONTRACTS.md:126-128` and `USER-GUIDE.md:195` rewritten.
Red-first tests: (a) `An_install_that_never_chose_friction_pays_the_venue_fee_on_a_paper_fill` (red on base); (b) `An_owner_override_of_zero_is_used_exactly`;
(c) `An_undeclared_research_friction_field_takes_the_venue_value_and_a_declared_one_wins`; (d) `The_fill_sentence_names_the_venue_model_not_the_owner`;
(e) `A_settings_file_from_before_this_unit_reads_as_unset`; (f) `PaperSettlementTests` stays green (the connector default is untouched). Mutant to watch red and
quote: the venue-model branch replaced by 0 ⇒ (a) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
