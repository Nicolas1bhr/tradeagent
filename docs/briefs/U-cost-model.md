# U-cost-model — one app-owned venue cost model, pinned by each campaign, used by the referee; a program must trade the dataset's instrument
**Arrow closed:** candidate → a verdict that can mean something (`docs/EDGE-FACTORY.md` § 4.5). **Today (SOURCE at `0f47db7`, manager-, R09- and R11-checked):**
`trade verdict` passes no model (`Gateway/GatewayPipeServer.cs:2496`), so the judge is `Frictionless` = fee 0, slippage 0, increment **1**, capital **10,000**
(`Core/Strategy/Backtest.cs:76`, `Referee.cs:218`); every BTCUSDT size rounds to nothing or costs more than the capital (`Backtest.cs:487-495,401`) ⇒ every verdict
is no-trade and still burns one of three (`Referee.cs:137-143,186`; `Trading.cs:503`); fixtures priced 96–105 hide it (`PaperEligibleVerdictTests.cs:55-60`).
**Observable result:** a BTCUSDT program sized `capital_fraction 0.5` trades on the holdout, net of the venue's published fee; the submitter still never
chooses the judge's friction; a dataset with no recorded venue keeps today's behaviour, labelled. **Schema 27** (every later rung lands after this).
Fact: Binance spot regular tier 0.100% maker / 0.100% taker (R04 § 7, DOC 2026-10-02). The research-run default is NOT in this unit (`U-paper-friction`).
Read first: `docs/EDGE-FACTORY.md` § 4.5; `CLAUDE.md`; `Referee.cs` (whole); `Backtest.cs:31-167,380-500`; `CampaignStore.cs:29-79` (DO NOT edit the V1 / PaperV1
texts at `:38,:78` — their sha is checked at `Referee.cs:201-206,261-266`), `:368-398` (`Renew`), `:516-887`; `PromotionStore.cs:141-153` (`executionModel` is
already a hashed fact, written at `Referee.cs:308`), `:383-465`; `TradingGateway.cs:1279-1294`; `App/SettingsView.cs:231-237,502-512,562-597`;
`Core/Db/DatasetStore.cs:143-150` (`VenueId`, `InstrumentSymbol` nullable — referee fixtures leave them null); `VenueCatalog.cs:38-56,97-100` and
`Database.cs:995` and `COUNCIL.md:155` (fees are never catalogued — keep it so); `Backtests.cs:87-193,341-396`; `VenueIncrementTests.cs:239,304-319`.
Items, one commit each, one-sentence messages:
1. `VenueCostModel` v1 (Core; version + canonical hash): fee per fill from a BUILT-IN table of published standard taker rates per venue id, each with its
   source sentence and date (code, not the catalogue, not an override file); slippage 0.0002 per fill, labelled TradeAgent's ASSUMPTION; the quantity
   increment from the instrument's VERIFIED row only (never guess, as `Backtests.cs:361-366`); capital = new setting `JudgeCapital` (default 10,000).
2. Schema 27: `strategy_campaign.cost_model_canonical` + `cost_model_sha` (nullable). The owner's existing press pins the model in the opening transaction
   from the DATASET'S venue (no new picker); `Renew` copies it. A dataset with NO recorded venue pins `Frictionless` labelled "no venue recorded" (every
   fixture-backed test stands). A recorded venue whose instrument row is unverified refuses the press in words that name no missing feature: "BTCUSDT's
   quantity step is not confirmed against the venue's own definition; a judge that guessed it would judge a different strategy". LEGACY campaigns: none
   ever charged ⇒ pinned at the first verdict request inside the charge transaction, refused BEFORE charging if unverified; any charged ⇒ `Frictionless`,
   recorded as "legacy frictionless judge", old verdicts untouched. `Referee.Verdict` with no caller model uses the pin. The two `ALTER TABLE strategy_campaign
   DROP COLUMN …` lines join the roll-back lists at `VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275`, names kept (R18 C1: no add-if-missing).
3. A research run and the referee refuse BEFORE charging a program whose `instrument` differs from the dataset's `InstrumentSymbol` (the key the increment
   is looked up by, `Backtests.cs:351-356`) when the dataset records one; naming both. `VenueIncrementTests.The_increment_is_looked_up_by_the_datasets
   _instrument_and_not_the_programs` is REWRITTEN IN PLACE, name kept (program and dataset agree; the increment comes from the dataset's row); the mismatch
   gets its own new test.
4. `CONTRACTS.md` (the model, the pin, no-venue and legacy rules), `USER-GUIDE.md` (the holdout card shows the pinned model), `docs/RESEARCH-REQUIRED.md` (fee source).
Red-first tests (fixtures priced ≈ 85,000, increment 0.00001, a venue recorded): (a) `A_btc_priced_program_trades_on_the_holdout_under_the_pinned_cost_model`;
(b) `The_pin_is_fixed_at_campaign_open_and_survives_a_settings_change_and_a_renewal` (red by failing to compile — say so); (c) `A_legacy_campaign_with_a_charged
_verdict_keeps_the_frictionless_judge` (a GUARD, green on base); (d) `A_legacy_campaign_never_charged_is_pinned_at_its_first_verdict`; (e) `An_instrument_dataset
_mismatch_is_refused_before_a_trial_or_a_verdict_is_charged`; (f) `An_unverified_instrument_refuses_the_campaign_press_in_words`; (g) `A_dataset_without_a_venue
_keeps_the_labelled_frictionless_judge` (guard). Mutants to watch red and quote: (i) `model ?? Frictionless` restored ⇒ (a) red; (ii) the instrument check
removed ⇒ (e) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
Tip `617a82a` (the CI-tested last code commit; 4 item commits on `73cfaca`). Schema 27.
Build `-c Release --no-incremental`: "0 Warning(s) 0 Error(s)". Unit "Failed: 0, Passed: 1228"; Fault "Failed: 0, Passed: 399"; extra local Integration "Failed: 0, Passed: 699, Skipped: 1".
3×, each run "Failed: 0, Passed: 42": VenueCostModelTests, CostModelPinTests, InstrumentMatchTests, VenueIncrementTests, VenueCatalogTests, PaperEligibleVerdictTests.
CI run 37022783765 @617a82a: windows-latest success (40 min), ubuntu-latest success, macos-latest success, package success.
Names vs main: sets base 1969 tip 1985; removed 0; added 16.
1 done: `VenueCostModel` v1 (Core/Strategy): code table of published taker rates with source and date (binance-spot 0.001, read 2026-10-02), slippage 0.0002 labelled ASSUMPTION, verified-row step only, `JudgeCapital` 10,000 (≤0 reads 10,000); header, canonical text, `Sha256`; `Read` refuses any text that is not its sha's or not v1's.
2 done: rung 27 adds two nullable columns, no backfill; the press pins from the dataset's venue in the opening tx, or refuses "BTCUSDT's quantity step is not confirmed…" writing nothing; `Renew` copies (null too); no venue ⇒ `Frictionless` "no venue recorded"; legacy decided inside the charge tx (lineage charged ⇒ "legacy frictionless judge", else pinned from the venue or refused before charging); `Verdict` judges `model ?? pinned.Model`; holdout card "Judged under"; both roll-back lists +2 lines.
3 done: one `InstrumentMatch.Refusal`, called by `Backtests.Run` before increment, trial and run and by `Referee.RequestVerdict` before the charge; the brief's test rewritten in place, name kept. Deviation: two more `VenueIncrementTests` over ETHUSDT data now give their program `instrument ETHUSDT` (names and assertions kept), else the new rule refuses them first.
4 done: CONTRACTS (backtest, catalogue, campaign, verdict sections), USER-GUIDE (holdout card; fees; instrument), RESEARCH-REQUIRED C6.
RED before (on `73cfaca`): (a) "the holdout run took no trade: no-trade-on-the-holdout"; (d) expected "fees=0.001;…" actual "fees=0;slippage=0;increment=1…"; (e) "Assert.Throws() Failure: No exception was thrown"; (f) "the press opened a campaign over a step nobody confirmed"; the added legacy-unconfirmed test red too; (c), (g) green (guards); (b) fails to compile: "CS1061: 'TradeAgentSettings' does not contain a definition for 'JudgeCapital'".
Mutants (reverted): (i) `model ?? ExecutionModel.Frictionless` ⇒ (a) "the holdout run took no trade: no-trade-on-the-holdout"; (ii) `InstrumentMatch.Refusal` short-circuited to null ⇒ (e) "Assert.Throws() Failure: No exception was thrown".
Start path: reached only by rung 27's two `ALTER TABLE`s and the referee's construction moved after `LoadSettings`; no AI-runtime launch, sign-in or wake code touched.
Judgements: legacy pins are written by the next verdict request, not a migration; a venue with no published fee (revolut-x) is refused like an unconfirmed step; the judge's label lives in the pinned text.
NOT done/verified: the Binance fee page not re-opened by me (R04 § 7, DOC 78); no Windows-box run; holdout card not viewed on screen (`JudgeLine` unit-tested); agent-facing texts on the judge's friction unchanged; research-run default, paper friction, min notional, verifying BTCUSDT: later units.
Rebased onto `36934e9` at seat A's request: one conflict, `docs/RESEARCH-REQUIRED.md` (main re-dated the § D heading; I had inserted C6 above it) resolved by keeping both, C6 intact above main's D. Post-rebase code tip `e0da92e`: build `--no-incremental` "0 Warning(s) 0 Error(s)", Unit "Failed: 0, Passed: 1240" (1224 + 16); CI not re-run (it tested `617a82a`).
