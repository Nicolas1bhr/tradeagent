# U-features — a feature is a spec as data, known by its hash, computed by the app in decimal from only what had arrived by its instant
**Arrow closed:** tape → features (`docs/EDGE-FACTORY.md` § 4.3, § 9 phase 2). **Depends on `U-tape-read`:** `TapeReader.Window`, `TapeQuery`, `TapeReader.Row`,
`TapeStore.Served` (landed: merge `9ef81d30`, record `e2f4daf8`). **Then:** `U-features-b` (`data-feature` op, cross-sectional rank, z-score), `U-language-v2a`.
**Protects:** no model output on the signal path (§ 6.1: app code computes every value from raw market rows); evidence binding (§ 6.2: the id v2a binds); measurement
versus claim (§ 6.3: a spec proposes, the app computes, nothing is stored); class and terms carried (R07 § 5.3; research-only confers nothing); no look-ahead, by construction.
**Today:** no feature exists. `TapeStore.AsOf` (`TapeStore.cs:559-575`) joins on ARRIVAL (`received_at <= t`), withholds a quarantined payload (`Served` `:589`; every row is screened,
`:678`), takes an audience though no tape holdout exists (`:548`); `TapeReader.Window` (`TapeReader.cs:176`) pages ≤ 5,000 rows, newest first, `From`/`To` on source time,
each revision its own row. Classes are per row, only lowered (`Tape.cs:19-43`); an archive row's source time is its label, `O-PIT` meaning the vendor's storage dates it
by then (`TapeStore.cs:458`); `docs/CONTRACTS.md:2987` (at `e2f4daf8`) leaves "reading `O-PIT` as known at its label" to this unit, and `:3052-3055` (with
`Database.cs:1844-1847`) the tape's licence readings to "the unit that first lets tape evidence count". Only GDELT writes archive rows (`GdeltRecorder.cs:446`):
first-target inputs are `O-LIVE`/`O-ARCH`. Moving `StrategyVersions.Manifest` re-identifies every program (`StrategyProgram.cs:52`, `StrategyCanonical.cs:53-57`).
**Observable result** (code and tests; no op, verb or screen yet): `FeatureSpec.Parse` takes e.g. `{"kind":"latest","input":{"source":"binance-um-premium","series":
"premium-index","subject":"BTCUSDT","field":"lastFundingRate"},"latency_s":5,"max_age_s":180}` or refuses it in words; one meaning, one id; `FeatureSeries.Read` serves
decimal values or a stated absence with its rows' worst class and digest, the clean-history start, each input's terms and the live refusal. **No schema change.**
Read first: `CLAUDE.md`; `docs/EDGE-FACTORY.md` § 4.1, § 4.3-4.4, § 6.1-6.3; R07 § 5.1-5.4; `Tape.cs`; `TapeStore.cs:400-605`; `TapeReader.cs`; `Holdout.cs:51-136`;
`DatasetReader.cs:49-98`; `StrategyCanonical.cs`; `DataLicence.cs`; `EvaluationGoldenVectorTests.cs`. Items, one commit each, one-sentence messages, in `Core/Features/`:
1. `FeatureSpec`, `FeatureCanonical`, `FeatureVersions`: a TOTAL parse — an unknown key or kind, a source whose parser is not `binance-um-json` (`TapeSourceCatalog.cs:192`),
   an unknown series, a missing or negative duration refused in words; kinds `latest`, `change` (`diff`|`ratio`), `mean`, `min`, `max`, `pct-rank` (share of window values
   ≤ the latest), exact in decimal; durations required whole seconds (latency 0-86,400; `max_age_s` always; `window_s`, `min_rows` for window kinds); canonical text
   `feature/1` by `StrategyCanonical`'s rules; `Id = Sha256Hex.Of(canonical + "\n" + "features=1")`; `StrategyVersions.Manifest` does not move.
2. `FeatureEvaluator.At(spec, rows, t)`, PURE and the only gate: a row counts at `t` only if its first-seen — `ReceivedAt`, or `SourceTime` for an `O-PIT` first reading —
   is ≤ `t − latency` and its source time ≤ `t`, each key at its highest counted revision; window = counted rows stamped after `t − latency − window`; `change` = `latest(t)`
   against `latest(t − lookback)`; fields (JSON string or number) as invariant decimal; ABSENT, never zero or skipped over: latest stamped over `max_age_s` before `t`,
   under `min_rows`, a field missing or unreadable, a payload withheld, a ratio's base 0; carries `TapeClass.Lower` and a SHA-256 of the rows read (id, revision, hash).
3. `FeatureSeries.Read(reader, audience, spec, from, to, step)`: audience REQUIRED, handed to `TapeReader.Window` (paged on `Before`; no `AsOf`, the evaluator gates); ≤ 10,000
   points, ≤ 50,000 rows an input, REFUSED beyond; clean-history start = latest across inputs of the first first-seen of an `O-LIVE`/`O-PIT` row, plus latency and window or
   lookback; each input's `Terms`/`TermsUrl` (Binance's empty: `TapeSourceCatalog.cs:240-242`); `FeatureLicence.LiveRefusal(spec, newest)` null only if every input's newest
   licence reading `Confers` (`DataLicence.cs:50`) — no tape source has one, so every feature reads research-only today. No rung, no table, no reading seeded.
4. `docs/CONTRACTS.md` "Features" after "The tape" (spec, first-seen rule, absence, classes, licence, no op yet); (11) answered; `:3052-3055` and the comment at
   `Database.cs:1847` re-pointed (seat A's call): a reading decides only live eligibility and an absent one confers nothing, so the readings (a rung; each venue's
   terms read that day) fall to the unit that opens a conferring path for tape evidence — the conferring-dataset unit — never to one that only computes.
Tests: (a) `A_feature_reads_only_what_had_arrived_by_its_as_of_time_minus_its_latency`; (b) `Rows_that_arrived_after_t_change_no_value_even_when_handed_in`; (c)
`Source_time_alone_never_admits_a_row`; (d) `An_O_PIT_row_counts_from_its_label_and_a_late_revision_from_its_arrival`; (e) `A_look_ahead_spec_cannot_be_written`; (f)
`One_spec_spelled_two_ways_has_one_id`, `Every_field_moves_the_id`; (g) `FeatureGoldenVectorTests` (each kind, golden ids; red unless `features=` moves); (h) `A_value
_carries_the_worst_class_of_its_rows`; (i) `Clean_history_starts_once_every_input_was_first_live_plus_its_window`; (j) `Unlicensed_inputs_confer_nothing`; (k) `A_withheld
_payload_makes_the_value_absent_never_skipped`; (l) `A_stale_latest_is_absent_not_carried_forward`; (m) `A_text_source_is_refused`; (n) `A_range_over_the_cap_is_refused
_not_truncated`; (o) `A_series_equals_the_pure_evaluator_over_all_rows`. Mutant, quoted red: item 2's first-seen test made `SourceTime <= t − latency` ⇒ (a)-(c).
First, in the report: the fields real first-target rows carry, read off a recorded tape — else NOT VERIFIED beside the fixtures' (`TapeStoreTests.cs:46`, `TapeCollectorTests.cs:53,66`).
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault 0 failed,
touched classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
