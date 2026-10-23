# U-org-verbs-c — a head charters a unit and hires a position into its subtree through the app, each act a decision record with a forecast, inside the rights matrix, the chart's bounds, the weekly throttle and the earning test — and a hired position waits dormant
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.9 (:318-321 unit heads' `charter`/`hire` "each a decision record with a forecast, inside the throttles"; :322-324 ONE rights
matrix; :325-327 the earning test), § 2.1 (only `OrgStore` writes `org_*`, ids app-minted); ORGANISATION § 2 :107 ("only the parent unit's head charters … hires"),
§ 3 :119-137 (charter, bounds, throttles), § 5 :202-207 (mandatory records; a charter's predicate); VISION § 3 :122-130; `OrgStore.cs:319-326` ("the verbs … arrive as
this class's writers in `U-org-verbs`"); § 15 O6 (:427), part c of 5. Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` (main `25bae9bc`) — re-grep every pointer. Pointers re-checked against 46f7bbd3 (main 4b2af1e7 + U-org-verbs-a), 2026-10-23.
**Protections — ORG-001 (ids minted, titles and charters claims, verbs on RECORDED assurance never headship), ORDER-001 and AUTH-001 (no verb about `operations` or
its division; no verb reaches operator authority; nothing here activates a position), BUDGET-001 (`OrgStore.Unfundable` before a split is written), measurement vs
claim, refuse never guess: red-first and ONE watched mutant.** Seat B; **opus/high**. **RUNG 48** (the board's assignment at dispatch, 2026-10-23; 47 is seat A's `U-board-a1` — rungs land in landing order, so a unit overtaken renumbers at its rebase).
**Depends:** part a LANDED (names, `org-show`); part b need not precede (charter and hire fence nothing).
**Applied pending ORCHESTRATOR (survey Q2–Q8):** hire writes DORMANT (an active non-legacy position refuses the legacy pair's unconfined launches,
`PositionActivation.cs:186-188`); the hirer is the head of the target unit's parent; the weekly two counts charter/close/merge; vocabulary v2 adds `unit-admissible`;
earning v1 as survey Q6; the charter's home is a decision; a fourth manager layer is refused by the earning test, never by a depth constant (VISION § 3 :115).
**Today:** no writer (`OrgStore.cs:319-326`); kinds/statuses `:73-119`, `OrgEventKind` = `seeded` (`:126-130`); `MintPosition` `:41`, `PositionIdRefusal` `:52`, homes
`Council.cs:90-98`; `org_unit` DDL with the never-written `charter_publication_id` (`Database.cs:1793-1803`); `OrgDecisionKind.Mandatory` refused by `Decide`
(`OrgStore.Decisions.cs:63-74, 236-240`) — no `hire`/`retire` kind; vocabulary v1 (`:83-104`), resolver `ForecastResolver.cs:25`; `Unfundable` `OrgStore.cs:548`; the
root's head is the owner (NULL), so no agent charters a division before `U-org-chief`.
**Observable result:** a puppet division head (Mediated manifest) charters a team under its division with an AI share, a rationale and a `unit-admissible`
forecast, then hires two dormant positions into it; the chart, the events, the decisions and the ledger rationale read back; a third charter that week, a hire
into `div-operations`, a charter citing nothing current, a hire past five members and the legacy pair on their unconfined seats are each refused in words, nothing written.
Items, one commit each, one-sentence messages:
1. **The rung:** `org_unit.charter_decision_id` (REFERENCES `org_decision`) replaces `charter_publication_id` (no compat owed; the mapper and seed follow).
   Check: a fresh home's `org_unit` columns; the rung re-runs cleanly after a crash before its stamp.
2. **The rights matrix as data** (`OrgRights`: operation × the caller's relation to the target × reserved identities — root, `div-operations`, `operations`, `div-research`,
   `research`, a division head's replacement, the chief's appointment — each answer "allowed", "a two-seat board act", "app trigger or a board act"), read by every
   structural writer — **R24-checked at dispatch (seat B, 2026-10-23):** every answer that was "the owner's two presses" is now "a two-seat board act" (R24 § 4.1: the
   board holds what ORGANISATION § 3 gave the owner about `operations` — its seat, occupant and unit — and the chief's appointment and replacement; § 7 B3: a
   structural veto is a board act or the owner's narrowing veto); no answer is the owner's unless R24 § 4.3 keeps it for him (the board's composition and budget,
   the audit line's budget, the unconfined-seat ceiling, his narrowing veto — none of them is this matrix's). The board's verbs arrive with `U-board-b`: until a
   board seat is ACTIVE, an act answered "a two-seat board act" is REFUSED in words naming that, never silently allowed and never handed back to the owner; the O5a/O5b verbs listed as "the mandate row decides". Check: `ConformanceTests` iterate EVERY row × assurance class
   (unconfined/mediated/brokered/confined seeded attempts, the `OrgRightsTests` pattern) × position kind.
3. **`org-charter`** (`OrgStore.Charter`, one write): unit row (kind team|division|subdivision under the caller's unit, head vacant, status active), the `charter`
   decision through an internal mandatory-kind path of `Decide` (rationale in the ledger), its forecasts (≥ 1 `unit-admissible`), `org_event chartered`; refuses: the
   matrix, > 6 divisions, a 4th manager layer without an experiment record, the earning test, a 3rd structural change in 7 days, an AI share making Σ siblings > 1 or
   failing `Unfundable` for the parent's seat reservation. Check: one test per refusal; `A_charter_writes_unit_decision_ledger_entry_and_event_in_one_write`.
4. **`org-hire`** (`OrgStore.Hire`): a minted position, DORMANT, home `org/<id>`, seat NULL (`U-org-seats` binds), optionally as a VACANT unit's head (appointment;
   replacement refused — a different operation), a `hire` decision (kind added), `org_event hired`; refuses: matrix, > 10 non-ended positions, > 5 members per unit,
   a closed unit, the earning test. Check: per refusal; `A_hired_position_is_dormant_and_the_legacy_pair_keep_turning` (activation seam).
5. **Vocabulary v2** (`ForecastVocabulary.Version = 2`, a stored v1 forecast still resolves; an unknown version refused): `unit-admissible` resolved yes/no at its date
   from `strategy_promotion` (promoted | paper-eligible) ⋈ `strategy_run.attempt` ⋈ `ai_attempt_funding` — the verdict class only, never a holdout figure (EVID-001).
   Check: `A_unit_admissible_forecast_resolves_from_lineage_not_todays_chart`. AND the packet's forecast reads (`OrgStore.Packets.cs`, `ForecastsDue` and the
   resolution read, ~99-114) refuse a forecast whose vocabulary version or predicate this build does not know, by id in words, as the resolver does — never rendered
   as shown (the Codex second pass of `U-org-packets-b`, 2026-10-23, folded here by seat B). Check: `A_packet_refuses_a_forecast_of_an_unknown_vocabulary_version`.
6. **Names, door, CLI:** `OrgCharter`, `OrgHire` join `Ops.Organisation`; schema, `trade org charter|hire`; one `PuppetLife` act. Check: the disjointness guard; door lists.
Red-first (quote each red at base): items 2–5's checks.
**The watched mutant:** `Hire` writes `active` ⇒ `A_hired_position_is_dormant_and_the_legacy_pair_keep_turning` red (the pair Neighboured). Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.1–2.4, 2.9, 2.11, 8; ORGANISATION §§ 2, 3, 5, 13, 14; VISION § 3; the survey; O2a/O4/O5b records; `OrgStore*.cs`, `PositionActivation.cs`.
**NOT in scope:** close/merge/retire/veto/activation/the seat ceiling (part d); trial and verdict shares, tilt, verdict narrowing (e); fences; helpers; seats (`U-org-seats`).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.

## Report
Tip of code: `f0962ad5` (base `50f4d45c`, rung 48). Gate: `build -c Release --no-incremental` → 0 Warning(s), 0 Error(s) · Org{Ledger,RightsConformance,Decision,Structure}Tests + PuppetLivesTests 3× → Passed 85/85 each; BrokerDoorTests (integration) 3× → 24/24 each · CI 39898458908 SUCCESS, all three platforms · names.sh: removed: 0, added: 14 (sets base 3262, tip 3276) · tests box: NOT RUN — busy (exit 4, a run in progress).
1 done `27da52e6` — `charter_decision_id` replaces `charter_publication_id`; crash-before-stamp: old dropped/new not added, all-but-stamp, and an old home each reopen at 48 with the column and the charter kept (`The_charter_rung_replaces_…_reruns_after_a_crash_before_its_stamp`).
2 done `e8d55850` (+ pipe half in item 6) — `OrgRights`; every "owner's two presses" is "a two-seat board act", refused naming that no board seat is active; no answer is the owner's.
3 done `0157aa19` — `OrgStore.Charter`, one test per refusal (17-row theory). Deviation: the share is sized against the caller's own recorded launch reservation (no seat binds a new unit yet); a host with no meter refuses.
4 done `20eee3c1` — `OrgStore.Hire` (dormant; appointment only into a vacant head). Declared: a hire must carry ≥ 1 forecast; earning passes as the unit's first, inside a live bounded experiment's unit, or citing current work.
5 done `1cfcb755` — vocabulary v2 `unit-admissible`; packet reads refuse unknown version/predicate by id. Resolver guard revised out loud (strategy_run read for version+attempt only); the "unknown version" fixture moved v2→v3.
6 done `64af4f37` — ops, schema, CLI, door, guard pinned exact args; conformance over every row × 4 classes; puppet act + legacy refusal. `f0962ad5`: docs follow.
RED at base: item 2 CS0103 `OrgRelation`; 3 CS0246 `CharterFunding`; 4 CS0246 `HireRequest`; 5 CS0117 `UnitAdmissible`, and the packet test on base reads: Expected [fc-7a2c…] Actual [3 rows]; 6 CS0117 `Ops.OrgCharter`.
Mutant (`Hire` writes active): "'operations' runs unconfined, and 'pos-892ce923ca1e' is active beyond the legacy pair…" Failed 1; restored, Passed.
Codex review: `fleet/tmp/B-scratch/u-org-verbs-c/U-org-verbs-c-review.md` — complete, 2 findings (P1 unit-admissible credits a run after the deadline; P2 sibling-share sum skips NULL legacy shares), NOT acted on.
Not done/verified: no full local suite; Windows tests box not run; the earning test's delivery/promotion routes untested; no board verb or owner press built.

## Fix pass 1
Tip of code: `de3795fa` (rebased on origin/main, rung 48 kept). Gate: Release `--no-incremental` → 0 Warning(s), 0 Error(s) · Org{Structure,RightsConformance,Ledger,Decision}Tests + PuppetLivesTests 3× under suite.sh → Passed 87/87 each · CI 39915377318 SUCCESS (12/12) · names.sh: removed: 0, added: 16 · tests box: NOT RUN — busy (a run in progress).
1 (P1) `4764d144` — `UnitAdmissibleSql` cuts the run at `r.created_at <= $by` (the forecast's date); the promotion is cut in `DecideUnit`; the funding row has no time but is written in its launch's admission transaction, before any run, so the run's cutoff bounds it. Guard test revised out loud (`r.created_at` is the one non-key column read). RED at base (`A_run_recorded_after_the_date_never_resolves_a_unit_admissible_forecast`): Expected ("no", "unit", "team-a1c0ffee") Actual ("yes", "promotion", "prom-8192…"). MUTANT (cutoff removed): same test red, Actual ("yes", "promotion", "prom-3e44…"); restored, green.
2 (P2) `de3795fa` — reading settled at `OrgStore.cs` `OrgEnvelopes` doc: a legacy division's NULL is "that division's position's share of the day — the split on the Safety page"; NULL elsewhere "is no separate cap". So legacy NULLs count at the split (`CharterFunding.LegacyShares`, read by the gateway as the division's envelope over its parent's) and one unread is refused "never guessed"; other NULLs hold no fraction. RED at base (`A_charter_counts_the_legacy_divisions_split_among_its_siblings`): "Assert.Throws() Failure: No exception was thrown" (a 0.1 division chartered over the 0.5/0.5 split).
Re-review: one P2 regression (fail-closed: a zero cap in a legacy role's ceiling drops its share and refuses a charter funded in the other ceiling), carded by seat B as U-verbs-c-followups.
NOT verified: the gateway's `LegacyShares` reading has no pipe-level test (the store's use of it is tested); no full local suite; Windows tests box not run.
