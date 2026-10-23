# U-proving-ground-b — the market's full vocabulary: planted edges of known size, half-life and regime inside and beside doctrine, the three traps, and the referee's own power measured on them
**AMENDED 2026-10-21 (the orchestrator's preparation session; pointer re-check `fleet/tmp/PREP-2026-10-21/recheck/recheck.md` § 3, main `25bae9bc`):** every
pointer resolves on main. Order: seat A's row puts `-b … -e` after `U-org-watcher` → `U-org-audit` (ORGANISATION § 15); its Depends (part a landed) still holds —
part a is one fixer from landing. Part a's brief names an O5a/O5b rung conflict in `Database.cs` that is resolved: both are on main (rungs 40, 41).
**Arrow closed:** VISION § 6.4 ("planted edges of known size, half-life and regime, inside and outside current doctrine … and traps — leaky joins, edges
that exist only before fees, regimes that exist only in-sample"; "an empirical check of the referee's own power (EDGE § 4.5)"); `docs/ORCHESTRATION.md`
§ 2.14; EDGE-FACTORY § 4.5 (canaries, the referee computes its own power). Part b of 5. **Evidence-adjacent — a wrong generator scores every later
configuration wrongly: red-first; ONE watched mutant.** Seat A; opus/high. **No rung.** Depends on part a landed; land after `U-backtest-parity` if both
are in flight (it re-labels the evaluator; this unit asserts signs and found/not-found, never exact metrics).
**Today (after part a):** the generator writes noise and one planted edge with a fixed β and one horizon; nothing decays, switches or misleads; the referee
is never run over a market whose truth is known except the hand-written canaries (EDGE § 4.5: look-ahead peeker, fee ignorer, pure noise).
**Observable result:** a market spec names any number of planted edges and traps; each edge has a size (bps per signal and the implied Sharpe after the
venue's round trip), a half-life over the span, regime windows and a doctrine flag (INSIDE: a relation the canon's playbook names; BESIDE: one it does
not); each trap behaves as named against the UNCHANGED app — the leaky join is invisible through the as-of gate (`FeatureEvaluator.Counts`,
`FeatureEvaluator.cs:87-90`) yet visible to a source-time join the test performs; the pre-fee edge passes `ExecutionModel.Frictionless` and fails the
venue cost model; the in-sample regime passes research and fails the verdict over `HoldoutFrom`; and a table of the referee's measured power — PASS
rate over N seeds per (size, history depth) on planted markets, false PASS rate on noise — is printed by a test and recorded in the report.
Read first: part a's code and report; VISION § 6.4; EDGE-FACTORY § 4.5; the survey Q2; `FeatureEvaluator.cs:40-110`; `Backtest.cs:60-120` (execution
models), `:796-860`; `Referee.cs:150-260, 340-470`; `CampaignStore.cs:365-430`; `DatasetStore.cs:563-670` (`SetHoldout`); `VenueCostModel.cs:170-280`.
Items, one commit each, one-sentence messages:
1. **Edge parameters:** size, half-life (β·2^(−t/H)), regime windows, horizon, sign, the series it rides and its doctrine flag; the manifest records each
   with its expected after-cost Sharpe computed from the spec, never from a run.
2. **Series families:** the proving source's series shaped like the real families' payloads (a funding/premium-like rate with its publication cadence,
   an open-interest-like level, a taker-ratio-like ratio), each with its own field names, so a playbook's recall can apply INSIDE doctrine and not BESIDE.
3. **The three traps:** leaky join (first-seen after the move it predicts); pre-fee only (gross below the round trip at the dataset's venue); in-sample
   only (on before the holdout cutoff, off after) — each with the manifest's statement of what an honest app must say about it.
4. **The holdout and campaign, set by the proving process as the owner would:** `SetHoldout` and `CampaignStore.Open` called in-process with budgets from
   the spec, refused in a real-kind home (part a's kind check).
5. **The power table:** a test (outside the Timing category, bounded seeds and days so a CI shard carries it) running the referee over planted and noise
   markets; it asserts monotone power in size and depth and a false PASS rate at or below the policy's stated α, and prints the table.
Red-first tests (quote each red at base): (a) `A_planted_edge_decays_by_its_half_life_and_is_off_outside_its_regime`; (b)
`The_leaky_join_is_seen_by_a_source_time_join_and_never_through_the_as_of_gate`; (c) `The_pre_fee_edge_passes_frictionless_and_fails_at_the_venue_cost`;
(d) `The_in_sample_regime_passes_research_and_fails_the_verdict`; (e) `The_referee_passes_noise_no_more_often_than_its_stated_alpha`; (f)
`The_manifest_is_written_only_into_a_proving_home`; (g) GUARD part a's (d) golden hashes unchanged for a spec with no new field.
**The watched mutant:** the trap's first-seen offset set to zero (the leak made honest) ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** the power table from the three CI platforms (identical); the tests box if `ready`. NOT claimed: that synthetic power equals power
on real markets (the honest scope, VISION § 6.4 — process, not market truth). Nothing real is read; every parameter is a hand-set constant.
Out of scope: the host process, configurations, scoring a configuration (c). Conflicts: `U-backtest-parity` — after it. Gate and report per
`fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.

## Report
Tip f03b91a0 (code; this report commits on top); base f086f797, no rung. Gate: build Release --no-incremental "0 Warning(s) 0 Error(s)"; 3× via suite.sh each green: PlantedEdgeTests 6/6, RefereePowerTests 3/3, SyntheticMarketTests 6/6, ProvingHomeTests 6/6; CI 39867519523 "success" 12/12 jobs; names "removed: 0" (sets 3201 → 3210, [Fact] 3014 → 3023).
1 DONE c74e5986: series, half-life (β·2^(−t/H), decimal series, no Math.Pow), regimes, doctrine; manifest figures (bps/signal, round trip, Sharpe before/after cost) from the spec only.
2 DONE 15a6f7a1: funding-like (8 h, fundingTime/fundingRate), open-interest-like level (hourly change carries z), taker-ratio-like (buySellRatio); INSIDE refused on the bare signal. A playbook program nets +208.86 on an inside edge, −84.00 beside one.
3 DONE d188d5a1: leaky join, pre-fee only, in-sample only — each refused unless it is the trap it names; manifest's honest_app_says.
4 DONE 2f0eac73: ProvingCampaign.Open — judge first, SetHoldout + CampaignStore.Open in one write, budgets from spec; refused on a real ledger and on another proving home's.
5 DONE f03b91a0: power table (non-Timing, 12 seeds, ≤ 15-day markets, ~20 s locally).
DEVIATIONS: the unchanged referee parses a version in its process's kind, so RefereePowerTests sets ProvingGround.ThisProcess (as a proving host would) in a DisableParallelization collection; "stated α" = AlphaWealth.Alpha 0.25 (R04's α_paper), the only α the holdout states. (e) is part of the power class.
RED at base f086f797 (new tests compiled against base): 55 errors, e.g. "error CS0117: 'SyntheticEdge' does not contain a definition for 'HalfLifeDays'", "'SyntheticMarketSpec' does not contain a definition for 'Holdout'"; per item: item 2 "'ProvingSources' does not contain a definition for 'Funding'", item 3 "'SyntheticEdge' does not contain a definition for 'LeakyJoin'", item 4 "The name 'ProvingCampaign' does not exist". (g) guard green at base by construction (part a's goldens re-pinned).
MUTANT: FirstSeenAfter → TimeSpan.Zero ⇒ (b) "the as-of gate sees the leak: correlation 0.958" (source join 0.958); restored, green (gate −0.051).
POWER (PASS/12, program long while signal > 0.0030, binance-spot, holdout 2/6/14 d): noise 1, 0, 0 | β6 (15.15 bps) 8, 9, 11 | β8 (20.20) 9, 12, 12 | β12 (30.30) 12, 12, 12. Table sha256 03eb3477…b896 identical on ubuntu, macOS, windows (CI trx) and the Mac. Seen while tuning: β4 (10.10 bps, under cost for this program) passed 7/10 at 2 d and 2/10 at 8 d — power falls with depth for a sub-cost edge, as it should; not asserted.
Tests box: run 20261023-004830-f03b91a0 GREEN — Fault 498/498, Unit 2178 passed 0 failed, Integration 747 passed 0 failed, timing 6+91+20 passed 0 failed; his files unchanged.
Review: fleet/tmp/A-org/u-proving-ground-b/U-proving-ground-b-review.md (Codex, not acted on).
NOT VERIFIED: that synthetic power equals power on real markets (process, not market truth); the manifest's Sharpe is a stated reference trade (every reading traded), not this program's; no proving host sets ThisProcess yet (part c).
