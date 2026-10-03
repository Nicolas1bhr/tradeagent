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

## Report
Code tip `2bfb74c` on `c44425a` (cut at `8a51a16`, rebased once onto the docs-only `c44425a`): `bc3ea63` item 1, `238b069` item 2, `05697ce` item 3, `2bfb74c` item 4, then this report; each item built and its tests run before its commit. No schema change.
Gate at `2bfb74c`: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 Warning(s), 0 Error(s) · Unit 1252/1252 · Fault 402/402 · 3×, each run: PaperFrictionTests 8/8 (the one class added; none other touched), PaperSettlementTests 5/5 · a local Integration subset (Backtest/Venue/Verdict/Holdout/Report/Data/ForwardBars over pipe, MidpointEvidence, ForwardRunner) 71/71.
CI run 37057026434 at `2bfb74c`: ubuntu-latest success 12 min · macos-latest success 15 min · windows-latest success 45 min · package success 4 min.
Names vs my base `c44425a`: sets 2006 → 2014, removed 0, added 8 ([Fact] 1970 → 1978). Vs `main` `be91995`: sets 2019 → 2014, "removed 13" = exactly the 13 U-tape-store tests `main` gained after my CI dispatch (`c44425a`→`main`: removed 0, added 13); this branch removes none.
LANDING CONFLICT: a rebase onto `main` stops in `src/TradeAgent.Core/Trading.cs` — U-tape-store's `RecordMarketContext` sits directly above the paper-fee doc this unit rewrote. Resolution: keep main's block, then this branch's text, dropping main's two old "WHAT A FILL ON THE PAPER CONNECTOR IS CHARGED" lines. A trial merge so resolved (aborted; branch untouched): build 0/0, Unit 1269/1269, Fault 405/405.
1 done: Core `VenueFriction` (beside `VenueCostModel`, carrying its fee and slippage lines; id `venue-cost-model-v1/<venue>`, sha of its text) and `FrictionInForce` (a source per number — owner / venue model / none — the fill's sentence, the owner's line); `PaperFriction` gains the per-number source and model, its sentence delegates; the connector default stays `None`, FRICTIONLESS as before.
2 done: nullable `PaperFeeOverride`/`PaperSlippageOverride`; `FrictionInForce.ForPaper`: an override (0 included) ⇒ owner, else the venue model of the forward collector's venue (read off the catalogue's forward row: binance-spot); `AppHost.PaperChoice` reads it at every fill; legacy fields read, not consulted; CONTRACTS states the `git grep` at `8a51a16` (no writer) and that the row is agent-writable until containment. ADDED: the headless gateway host applies the same rule.
3 done: `Backtests.Friction`: each undeclared number takes `VenueFriction` of the dataset's venue, a declared one wins (0 included). DEVIATION 1: no venue ⇒ 0 labelled "no venue recorded" and said frictionless; a venue with no published fee is REFUSED for an undeclared number (the referee's and the increment's rule). DEVIATION 2 (no schema change; R09's "state a rule"): the provenance is stored in `strategy_run.increment_source` on a `friction: ` line after the increment's sentence, read back apart as `StrategyRunRow.FrictionSource`; the answer has `friction_source`. Texts: pipe note, schema (overview, op, fees/slippage), AGENTS.md, CONTRACTS, USER-GUIDE.
4 done: paper row "Fills pay 0.1% + 0.02% (assumption) — Binance spot standard taker, 2026-10-02" + two % boxes (each its own override; one press saves, one clears both); `status.paper_friction {source, fee, slippage, model{id, sha256}}` on every platform, `source` = `fee/slippage` words when they differ, schema described; report §4 "paper fills pay"; CONTRACTS paper section and USER-GUIDE:195 rewritten.
RED before (base's `new PaperFriction(PaperFeeFraction, PaperSlippageFraction)` restored in `AppHost.PaperFrictionFor`): (a) "Assert.Equal() Failure: Values differ Expected: 110.0220 Actual: 110"; (b) the same line, in its one-number-overridden half.
Mutant, restored identical and rebuilt: `FrictionInForce.Resolve`'s venue fee branch `(v.FeeRate, …)` → `(0m, …)` ⇒ (a) "Assert.Equal() Failure: Values differ Expected: 0.1100220 Actual: 0.0000000".
(f) `PaperSettlementTests` untouched and green. (c), (d), (e) use this unit's API and were not run on base.
NOT verified: the Settings row was not seen on screen (the app was not run); no Windows box; CI ran on `2bfb74c`, not on the trial merge. Not done: an override written into the row by anything but the Settings page is applied without a bound, as the old field was.
Rebased onto `main` `c0ce760` (after U-tape-store, U-evidence-identity and seat P's inbox fix): code tip `a855420` (`1f72007`, `ec5dfbc`, `cdada3e`, `a855420`). The one conflict, `Trading.cs` at item 2, resolved as described — main's `RecordMarketContext` block, then this unit's text, main's two old "WHAT A FILL…" lines dropped (blob `96aac1f`, the trial's); `git range-diff`: items 1, 3, 4 identical, item 2 differs only in that file's context, every +/- line in src and tests as CI tested at `2bfb74c`. Post-rebase: build `--no-incremental` 0 Warning(s), 0 Error(s) · Unit 1273/1273 · Fault 405/405 · PaperFrictionTests 8/8 and PaperSettlementTests 5/5, 3× each · names vs `main` `c0ce760`: sets 2034 → 2042, removed 0, added 8 ([Fact] 1996 → 2004). No CI re-run, as instructed.
