**THIS DISPATCH BUILDS PART `-b` ONLY (items b1–b4, its mutant, its checks).** Part `-a` LANDED (train B12b; record `fleet/records/U-org-scorecards-a.md`); U-org-verbs-e LANDED as rung 50 (record `fleet/records/U-org-verbs-e.md`). a4 stays held for U-board-b1 (card U-scorecards-board-score) — not in this pass. No rung: if b3's `review-answer` kind or `OrgRefKind.event` would need a schema CHECK change, STOP and report rather than take a rung.

# U-org-scorecards (O8) — every scored forecast is scored against a base rate the app measured before it was written, scorecards per position and manager version are computed at read and never stored, the exploration floor holds 25 % of every pot, and a cut rule raises a review event its head answers with a record — it retires, re-splits and reseats nobody
**Arrow closed:** `docs/ORGANISATION.md` § 5 :206-213 (scored "against the genome bank's base-rate forecaster, so a near-certain forecast earns nothing"; Brier and log
"per manager version"), § 8 :251-265 (scorecards "computed by the app at read"; a 25 % floor split equally, the 75 % equal before ≥ 90 forward days; cut rules "app review
events, not agent appeals"), § 13.6 :366; ORCHESTRATION § 2.11 :356-362, § 2.16 :396; VISION :146, :529; R24 §§ 4.2, 4.7 (a board decision's "evidence class and score"); § 15 :439.
**Protections — EVID-001 (scores from the app's resolutions and measurements only; the base rate reads only measurements stamped BEFORE the forecast's `at`, so nothing
written after a forecast moves its score; the scorer reads no claim but the forecast's own predicate, target and probability; nothing stored), ORG-001 (a score or a review
event grants, removes, re-splits and reseats nothing. ORGANISATION § 8 :263-264 DOES say otherwise for the 90-day rules — "drops to the floor" and "it is retired — records,
trials and memory kept, envelope returned", and § 13.3 :361 "retirements stay app policy" — neither is measurable before M-org1 and neither is built here: open question),
R24 (a structural act after a review is the head's own O6 verb, a board act, or the owner's narrowing veto — never the app's), refuse never guess (an unknown cost, model or
reference class is said, never zero): red-first and ONE watched mutant per part.** Seat B; **opus/high**. **No rung** (compute at read; a review is an `org_event` + a wake).
**TWO PASSES, `-a` then `-b`:** `-a` is a pure reader on landed O5b; `-b` writes events and gates the share writers that `U-org-verbs-e` is building — one pass would wait
on an in-flight unit and mix a reader with writers. **Depends:** `-a`: O5b, `U-forecast-nul`, O7 `-a` LANDED; `U-board-b1` optional (a4 only). `-b`: `-a`, `U-org-verbs-e`.
**Today (main `3dacac76`):** `org_forecast` (app-computed `scored`/`unscored_why`) and `org_forecast_resolution` yes|no|void (`Database.cs:2633-2665`; writers
`OrgStore.Decisions.cs:344-405, :509`); the resolver's three named measurement reads (`ForecastResolver.cs:30-51`), swept from `AppHost.cs:2206-2217`; vocabulary v2
`OrgStore.Decisions.cs:89-135`; a forecast's attempt holds `runtime`/`effective_model` (`Database.cs:471-492`) — the manager version. No score, base rate or scorecard in
`src` (grep); `BoardPolicyVersion.Score` always null (`BoardReader.cs:25-26`, `BoardStore.cs:168`), drawn by `BoardView.cs:152`; unit spend by recorded chain
(`AiAttemptStore.cs:734`); shares `OrgStore.cs:247-261`; half-spend escalation per MANDATE only (`OrgStore.Mandates.cs:970`); decision kinds `OrgStore.Decisions.cs:63-77`;
wake kinds in `WakePriority` (`MissionEventStore.cs:216`), ids `:456`. The watcher's `spend-no-accept` (`Oversight/Detectors.cs:121`) is the owner's line, untouched.
**Observable result (puppets, test host):** `-a`: a puppet head's charter forecasts `unit-admissible` at 0.8, an outsider forecasts `mandate-state`; both resolve;
`Scorecards.Of(position)` gives per manager version n, Brier, base Brier, skill and log, void/unscored/open apart; a second model is its own row; the Organisation page
says it. `-b`: a puppet team spends half its day envelope with nothing accepted ⇒ ONE `review` event and ONE `cut-review` wake to its division head, answered by a
`review-answer` record with a forecast; the team's share, status and seats unchanged; a tilt below the floor refused in words.
Items, one commit each, one-sentence messages. **Part `-a`:**
a1. **Base rate + scorer** (`OrgStore.Scorecards.cs`, `ForecastScorer`): base probability Laplace `(k+1)/(n+2)` over a MEASURED reference class, each read by a named
    constant (`BaseRateSql`) cut at the forecast's `at`: `verdict-class` — versions first judged before it, share first judged the target; `mandate-state` — mandates closed
    before it, share whose first closing event is the target; `unit-admissible` — units of the subject's kind chartered ≥ the horizon before it, share with an admissible
    verdict on a version they funded within the horizon (`UnitAdmissibleSql`'s joins). Brier `(p−o)²`; log with p clamped to [0.001, 0.999]; skill = base Brier − Brier.
    Check: `A_forecast_is_scored_against_the_base_rate_measured_before_it`; `A_near_certain_forecast_of_a_near_certain_outcome_earns_nothing`.
a2. **Per position × manager version** (runtime + effective model of the forecast's attempt; none recorded ⇒ its own named row): resolved `scored` rows only; void,
    unscored, open apart; sums, not means of means. Check: `Scores_are_kept_per_manager_version_and_void_or_unscored_never_enter`. a3. **Per unit** (`OfUnit(unit, from,
    to)`): cost by recorded chain (unknown said), mandates opened/accepted/expired, admissible candidates per AI dollar, contribution "no forward record" below 90 forward days. Check: `A_units_yield_reads_only_its_recorded_funding_chain`.
a4. **One scorer for the board** (if `U-board-b1` is on main at the rebase; else named in the report): `BoardStore.History` fills `Score` from the act's `board_forecast`
    rows through `ForecastScorer`. Check: `A_board_acts_score_comes_from_the_same_scorer`.
a5. **Guards + the page:** GUARD `The_scorer_reads_no_claim_but_the_forecasts_own_probability`; GUARD `No_score_is_stored`; one calibration line per position on the
    Organisation page (`LabelsOrganisation`, `Theme`, updated in place). Check: `Each_position_shows_its_calibration_in_words_without_a_rebuild`.
**Part `-b`:**
b1. **The floor where shares are written** (verbs-e's `org-tilt`, verbs-c's `charter`): a write leaving an active child below `0.25 / n` of its parent's pot (the new one
    counted) is refused in words naming the floor; `OfUnit` shows rule share vs actual. Check: `A_tilt_below_the_exploration_floor_is_refused`.
b2. **Review events** (`OrgStore.ReviewSweep` after the resolver in `SweepMandates`; thresholds policy data): half the unit's day envelope spent, nothing accepted in its
    subtree ⇒ its head; skill < 0 over ≥ 10 scored in one manager version ⇒ the position's decider. Each an `org_event review` (decider `app`, app-composed detail) and a
    `cut-review` wake, id `review:{rule}:{subject}:{period}`, one write. Check: `A_unit_past_half_its_envelope_raises_one_review_and_a_restart_none`.
b3. **The answer:** decision kind `review-answer` (mandatory, ≥ 1 forecast, refs the event — `OrgRefKind` gains `event`) closes the wake. Check: `A_review_is_answered_only_by_a_record`.
b4. **Names, door:** kinds in the rights matrix and `WakePriority`; one `PuppetLife` act. Check: `A_review_event_changes_no_share_status_or_seat`.
Red-first (quote each red at base): a1–a5 and b1–b4's checks. **The watched mutants:** `-a`: one `BaseRateSql` loses its `< at` cut ⇒ a1's first test red. `-b`: the review writer also sets the unit's share to the floor ⇒ b4 red.
**Read first:** CLAUDE.md; ORGANISATION §§ 5, 8, 13; ORCHESTRATION §§ 2.4, 2.11, 2.16; R24 §§ 4.1–4.2, 4.7; `ForecastResolver.cs`; `OrgStore.Decisions.cs`; verbs-e's brief.
**NOT in scope:** marginal contribution and the 75 % `max(0, MC)` weighting (no forward record exists: a card for the first unit at 90 days); the 90-day drop and the
two-quarter retirement (above); the chief's and staff's ≤ 10 % pots (`U-org-chief`); floor spend read apart from yield spend; cycle time, refusals, near-misses and audit
findings on the card; any op or packet showing a position its score; the watcher's calibration detector (seat A); worker replacement, seat shadowing; a genome bank.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
**RULINGS (seat B, accepted by the orchestrator 2026-10-24 ~22:4x):** cut rules raise REVIEW EVENTS only — any retirement is a board act (R24 over ORGANISATION § 8's automatic retire);
-a and -b both in scope; -b after U-org-verbs-e lands. The base-rate reference class, the floor as a refusal at the share writers, and the review defaults are accepted as drafted.

## Report
Code tip `497bd22c` (b1 `649b8b46` · b2 `40f23620` · b3 `12187957` · b4 `497bd22c`), base `cd6a7cac`. No rung, no schema CHECK change (org_event/mission_event/org_decision kinds are unconstrained).
Gate: Release `--no-incremental` "0 Warning(s) 0 Error(s)". Touched classes ×3 through suite.sh (TZ=UTC): UnitTests OrgTiltTests+OrgReviewTests+OrgLedgerTests+OrgRightsConformanceTests+PuppetLivesTests "Passed: 57" ×3; IntegrationTests BrokerDoorTests "Passed: 24" ×3. Branch CI 40160885361: every job green (seat B). names vs merge-base `cd6a7cac`: "removed: 0, added: 6" (3233 → 3239); vs today's moved `main` the 20 "removed" are main's newer tests, not on this base.
b1 DONE — the exploration floor (`OrgBounds.ExplorationFloor` 0.25, split over the parent's ACTIVE children, the new one counted) refuses a charter or an AI tilt below it, in words naming the floor; `OfUnit(...).Share` gives floor, rule share and the held share. DEVIATIONS: AI share only (trial/verdict shares are delegations of a campaign's evidence budget, not the pot); a tilt that RAISES a share is never refused by the floor; two test data changed for the floor, declared in place (OrgTiltTests 0.1→0.15, PuppetLivesTests charter 0.2→0.25).
b2 DONE — `OrgStore.ReviewSweep` after the resolver in `SweepMandates`, `ReviewThresholds.Default` (0.5, 10, 0.0) as host-passed policy; one `review` org_event (decider app) + one `cut-review` wake, id `review:{rule}:{subject}:{period}`, one write; restart raises none. DEVIATIONS: half-envelope reviewer is the nearest active head ABOVE the unit (the observable result's "division head"; the head whose verbs reach its share); no reviewer (the root's head is the owner's seat) ⇒ not reviewed; calibration period = manager-version hash @ scored/10×10; spend under the share with unpriced turns is said, not judged.
b3 DONE — `review-answer` mandatory decision kind, `OrgRefKind.Event`, `OrgStore.AnswerReview`: only the woken reviewer, ≥ 1 forecast, the event its first ref, once per review; untaken wake closed `answered`.
b4 DONE — `cut-review` in WakePriority (Research) and MissionLoop's reason; `org-review-answer` op, schema, `trade org review-answer`, door; rights-matrix row `review-answer` → "the review event decides"; puppet act `A_puppet_division_head_answers_a_review_with_a_record`.
RED before: b1 with the two floor calls removed ⇒ "Assert.Throws() Failure: No exception was thrown"; b2–b4 red at base by compile (15 errors, e.g. "'MissionEventKind' does not contain a definition for 'CutReview'").
MUTANT (-b): the review writer also sets the reviewed unit's share to its floor ⇒ `A_review_event_changes_no_share_status_or_seat` red ("team-4e71e002 … 0.4" expected, "0.125" actual); restored, green.
Codex review: `fleet/tmp/B-scratch/u-org-scorecards-b/U-org-scorecards-b-review.md` (seat B triaged: two real — the wake lacks the event id; UnitDayOf reads empty for an ended sole position — left for a fresh fixer, per seat B; the floor-on-increase finding ruled not a defect).
Tests box: run 20261025-051034-497bd22c at 497bd22c — Fault 502/502, Integration 748 passed/63 skipped, Unit 2447 passed/1 failed: `CouncilLoopTests.An_idle_day_costs_only_the_looks_an_open_objective_asks_for` ("The UTC Offset of the local dateTime parameter does not match", `CouncilLoopWakeTests.cs:79 LocalMidnight`) — the DST-day red in a file this diff does not touch; NOT this unit's.
NOT DONE / NOT VERIFIED: no lost-launch re-raise of a review wake; no Organisation-page line for reviews; no real model turn; the sweep's cost per tick on a large chart not measured.
