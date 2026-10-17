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

## Report
Code tip `b6bb3d4c` (rebased on main `24db59f2`; docs-only moves since dispatch). No rung: `meta.home_kind`, `tape_meta.home_kind`.
- Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` → "Build succeeded. 0 Warning(s) 0 Error(s)".
- Gate 2: `ProvingHomeTests|SyntheticMarketTests` 3× → "Passed! - Failed: 0, Passed: 8" ×3; neighbours (TapeStore, TapeSourceCatalog, FeatureProgramBacktest, TapeRead, TapeHoldout, TapeArchiveStore, FeatureSpec, StrategyParser) → "Passed: 55".
- Gate 3: CI run 39026060128 → success, every job (ubuntu, macos, windows ×5, linux-host). Review (Codex, not acted on): `fleet/tmp/A-org/u-proving-ground-a-build/U-proving-ground-a-review.md`.
- Gate 4: names.sh main → "removed: 0", "added: 8", "[Fact]/[Theory] base: 2786 tip: 2794".
- Item 1 done: `ProvingGround.ThisProcess` (internal), `HomeRefusal`, checked in `Paths.Resolve` at first use, before anything is created; `Paths.DefaultHome()`, `Paths.DefaultPipeName`.
- Item 2 done: both stores mark first and refuse the other kind (an unmarked store for a proving open) before any write; `Kind` is a constructor argument (default: the process's). `TapeReader` checks it too.
- Item 3 done: `ProvingSources` (`proving-synthetic`, `https://synthetic.invalid`, 3600 s, 2 s delay, subjects SYNA–SYND); `TapeSourceCatalog.LiveRule(source, kind)`/`Readable(kind)` beside the frozen rule; parse, store class, reader, FeatureSeries, FeatureFeed read by kind; tape-sources.json may not take the id.
- Item 4 done: `SplitMix64` + `SyntheticMarket.Write` (integer-only, raw 12-column klines → `Normalise` → `RecordNew`; signal → `TapeStore.Append`; truth in `proving-truth/`). Synthetic by construction: hand-set constants and a seed only.
- Item 5 done: (g) source scan; its one allowlist is TapeReader's kind check (5 names); a planted `using TradeAgent.Core.Proving;` in Referee.cs went red, then reverted.
- Deviation: StrategyParser.Parse/FeatureSpec.Parse take an optional kind (default: the process's), because Over takes a parsed program; Backtest.Over is unchanged.
- RED at base bb390ed7: (a)–(h) fail to build ("CS0234 'Proving' does not exist in the namespace 'TradeAgent.Core'"). A base-compatible (a) gave "Assert.Throws() Failure: No exception was thrown / Expected: typeof(TradeAgent.Core.TradeAgentException)".
- MUTANT: Database's kind check removed → (a) "[FAIL] … Assert.Throws() Failure: No exception was thrown"; restored → green.
- (d) golden, the same on all three CI platforms: raw d2122a79…0cf4, normalised b9e22f0d…da01, payloads 0bd71369…72db. (e) on the Mac: SYNB 56 trades, net +313.03; SYNA 57 trades, net −148.02.
- tests box: NOT RUN — the machine does not answer
- Not done or verified: SIM-001 against an unconfined same-user process (advisory, ORCH § 5); no proving host sets ThisProcess yet (part c); (f) uses a stand-in real home, not the process's own; no model, no network.

## Fix pass 1
Tip `2f1b774e` (rebased on main; fix base `74f8652e`). CI run 39028629168 → success, every job (ubuntu, macos, windows ×5, linux-host, package, package-linux).
- Gate: Release `--no-incremental` → "0 Warning(s) 0 Error(s)"; touched classes (ProvingHome, SyntheticMarket, Tape*, Schema, Version, DatasetStore, FeatureSpec, StrategyParser, Paths, FeatureProgramBacktest) 3× → "Passed: 187" ×3; FaultTests stores → "Passed: 9"; names.sh vs main → "removed: 0", "added: 11", base 2786 tip 2797.
- (d) golden hashes unchanged: test (d) passes on all three CI platforms with the pins unchanged (raw files keep their bytes; only their names changed).
- R1: RED "Assert.Empty() Failure: Collection was not empty" (public constructor); with that check left out at base, "Exception type was not an exact match … Actual: TradeAgentException". Green: `ProvingHome` is a sealed class, private ctor, `Create` refuses a directory that already holds files and has no proving mark on its database; `HomeMark.Read` opens a closed store `immutable=1` so the check leaves no `-wal`/`-shm` in a real home; `Write` refuses stores not at the home's paths (`Database.FilePath` added).
- R2: RED "the real open of the database returned a handle over a store marked proving", then the same for the tape once the database was fixed (internal per-thread `HomeMark.AfterCheck` hook). Green: `ClaimKind` re-checks and marks in one `BEGIN IMMEDIATE` transaction in both stores, and the mark is read back after the ladder; the tape's ladder starts from the version read under the lock.
- R3: RED "Assert.NotEqual() Failure: Strings are equal" (same raw path); with that check left out, "dataset 1: the raw archive file for 2026-08 no longer matches the hash recorded for it". Green: raw `{sym}-1m-{month}-{key}.csv`, truth `{sym}-{key}.json`, staged `.staged-{key}.csv` (key = 16 hex of SHA-256 over the spec), written CreateNew; the same market written again is refused before anything is written.
- MUTANT: R1's factory check disabled (`false &&`) → R1 "[FAIL] … Expected: typeof(System.InvalidOperationException) Actual: typeof(TradeAgent.Core.TradeAgentException)"; restored → green.
- Re-review (Codex): `fleet/tmp/A-org/u-proving-ground-a-fix/rereview.md` — not read.
- Observed, not in the triage: two markets for one instrument over OVERLAPPING windows still revise the same tape readings (the subject/time natural key), so the first market's planted relation would read the second's signal. R3's test uses windows a week apart. Not fixed.
- NOT VERIFIED: the R2 race on Windows file locking beyond CI's runs; tests box not tried; SIM-001 against an unconfined same-user process (ORCH § 5).
