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
