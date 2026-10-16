# U-proving-ground-a — the shadow home: a proving process with its own home and pipe, both stores marked and refused across kinds, and a synthetic market written through the real collection path that the unchanged backtest reads
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.14 ("a separate app process with its own home, pipe and registers"; markets "in the tape's formats"), § 8
(SIM-001), § 9; VISION § 6.4; ORGANISATION § 15 `U-proving-ground`. Part a of 5 (a home + market → b edges, traps → c host, floor, one agent, score → d
ladder → e real models, the owner's word); survey `fleet/tmp/A-org/proving-ground/survey.md`. **A protection — SIM-001, EVID-001 (a synthetic
datum never wears a real vendor's id or a real home's ledger): red-first and ONE watched mutant.** Seat A; opus/high. **No rung** (a key in `meta` and
in `tape_meta`, both existing key/value tables). Depends on landed units only (`U-tape-store`, `U-puppet-runtime`); dispatchable now.
**Today (main `e3344445`, READ):** `Paths` is per process from `TRADEAGENT_HOME`/`TRADEAGENT_PIPE` (`Paths.cs:9, 103, 108-118`); nothing marks a store
as proving (`Database.cs:150` `meta`; `TapeStore.cs:246` `tape_meta`): a copied `tradeagent.db`/`tape.db` opens anywhere. The tape classes a row by
`BuiltInLiveRule` (`TapeSourceCatalog.cs:597-609`) and a feature parse admits shipped rows only (`FeatureSpec.cs:350`, `FeatureSeries.cs:215-217`), so
synthetic rows under a real id would be classed O-LIVE as Binance's. Bars reach `Backtest.Over` (:796) only as a dataset row over a normalised file
(`KlineNormaliser.Normalise` :152 → `DatasetStore.RecordNew` :323, as `MarketDataService.cs:203` does).
**Observable result:** a test writes a seeded market — a noise instrument and one planted edge on a proving-only tape series — into a fresh proving
home through `Normalise`, `RecordNew` and `TapeStore.Append`; the UNCHANGED `Backtest.Over` finds the planted relation (net > 0 at the app's venue cost;
the same program on noise does not); the real home is byte-identical before and after; each store refuses the other kind in words naming both.
Read first: `CLAUDE.md` (refuse, never guess; public repo); ORCH §§ 2.14, 7, 8; the survey Q1-Q2; `Paths.cs`; `Database.cs:19-40, 140-160`; `TapeStore.cs:124-400,
540-600`; `TapeSourceCatalog.cs:570-612`; `FeatureSpec.cs:330-370`; `FeatureSeries.cs:200-240`; `KlineNormaliser.cs:1-160`; `DatasetStore.cs:96-140, 276-370`;
`Backtest.cs:796-860`; `VenueCostModel.cs:170-200`; `PuppetSeats.cs`, `PuppetTestHost.cs`, `tests/Shared/TestEnv.cs`; `OrgLedgerTests.cs` (source scan).
Items, one commit each, one-sentence messages:
1. **The process kind:** `TradeAgent.Core.Proving.ProvingGround` — `ThisProcess` (internal, never serialized, set only by a proving host's entry, the
   `PuppetSeats.TestHost` pattern) and `HomeRefusal(home, defaultHome, pipe)`: a proving process refuses a missing `TRADEAGENT_HOME`, the default home,
   a home without the proving mark, a missing or default `TRADEAGENT_PIPE` — in words naming the home and the pipe; checked at `Paths`' first use.
2. **The mark on both stores:** a fresh store writes `home_kind` (`real`|`proving`) first — `meta` in `Database.Migrate`, `tape_meta` in `TapeStore`; an
   open refuses the other kind naming both and the file (`TryOpen` as for a newer tape); an unmarked store is a real home's (every home before this
   build), refused by a proving process. The expected kind is an argument, so tests exercise both directions in one process.
3. **The proving-only source row:** `proving-synthetic` (origin `https://synthetic.invalid`, cadence and delay stated), recognised by the evidence-class
   rule and the feature parse ONLY for a store or reader of kind `proving` — a separate list consulted beside `Shipped()`, never inside the frozen
   dictionary; in a real home a program naming it is refused at parse and its rows can only be O-ARCH.
4. **The generator, first cut** (`Core/Proving/SyntheticMarket.cs`): an in-repo PRNG (SplitMix64 or xoshiro256**, golden first outputs in a test, never
   `System.Random`); a spec (seed, instrument, venue id from the catalogue, start, days) → RAW vendor-shaped one-minute kline files → the real
   `Normalise` → `RecordNew`; and tape series as `TapeFetch`es of `proving-synthetic` with `ReceivedAt` = source time + the declared delay, through
   `TapeStore.Append`. Noise and ONE planted edge (fixed β on one series' first-seen value, one horizon); the truth's manifest in the proving home only.
   SYNTHETIC BY CONSTRUCTION: hand-set constants, never fitted to or seeded from any real tape, dataset or ledger — said in the summary (public repo).
5. **SIM-001 by type:** a source-scan test (the `OrgLedgerTests` pattern) that fails if an evidence-path file — `Referee`, `StrategyStore`,
   `CampaignStore`, `PromotionStore`, `ResearchLedger`, `RefereeHoldout`, `DatasetStore`, `TapeReader`, `FeatureSeries`, `MissionLoop` — names
   `TradeAgent.Core.Proving` beyond item 2's and 3's kind checks (allowlisted by name).
Red-first tests (quote each red at base): (a) `A_real_process_refuses_a_proving_database_and_tape_naming_both_kinds`; (b)
`A_proving_process_refuses_the_default_home_an_unmarked_home_and_the_default_pipe`; (c) `A_feature_naming_the_proving_source_is_refused_in_a_real_home_and_parsed_in_a_proving_one`; (d)
`A_seeded_market_is_byte_identical_on_every_run_and_platform` (normalised SHA-256 and tape payload hashes golden); (e)
`The_unchanged_backtest_finds_the_planted_edge_and_not_noise_in_a_proving_home`; (f) `The_real_home_is_byte_identical_after_a_proving_market_and_run`;
(g) `No_evidence_path_names_the_proving_namespace`; (h) GUARD `A_real_home_opens_and_collects_as_before` (existing store tests unchanged).
**The watched mutant:** the kind comparison in `Database`'s open skipped ⇒ (a) red. Quote it, restore, green.
**Evidence beyond CI:** (d)'s golden hashes identical on the three CI platforms (quote them); the tests box if `ready`. No model, no network, no codex.
NOT claimed: SIM-001 against a same-user unconfined process (advisory, ORCH § 5). Out of scope: traps, half-life, regimes, power (b); host, configurations, scores (c).
Conflicts: O5a/O5b (`Database.cs` rungs 40/41 at `Migrate`'s end; item 2 writes at its start) — rebase; `U-backtest-parity` ((e) asserts a sign). Gate and
report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; full suite on branch CI, three platforms; Codex review with the CI wait; names vs `main` 0 removed, both sizes printed). `## Report` ≤ 20 lines appended here.
