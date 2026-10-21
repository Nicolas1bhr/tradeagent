# U-org-verbs-c — a head charters a unit and hires a position into its subtree through the app, each act a decision record with a forecast, inside the rights matrix, the chart's bounds, the weekly throttle and the earning test — and a hired position waits dormant
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.9 (:318-321 unit heads' `charter`/`hire` "each a decision record with a forecast, inside the throttles"; :322-324 ONE rights
matrix; :325-327 the earning test), § 2.1 (only `OrgStore` writes `org_*`, ids app-minted); ORGANISATION § 2 :107 ("only the parent unit's head charters … hires"),
§ 3 :119-137 (charter, bounds, throttles), § 5 :202-207 (mandatory records; a charter's predicate); VISION § 3 :122-130; `OrgStore.cs:303-311` ("the verbs … arrive as
this class's writers in `U-org-verbs`"); § 15 O6 (:427), part c of 5. Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` (main `25bae9bc`) — re-grep every pointer.
**Protections — ORG-001 (ids minted, titles and charters claims, verbs on RECORDED assurance never headship), ORDER-001 and AUTH-001 (no verb about `operations` or
its division; no verb reaches operator authority; nothing here activates a position), BUDGET-001 (`OrgStore.Unfundable` before a split is written), measurement vs
claim, refuse never guess: red-first and ONE watched mutant.** Seat B; **opus/high**. **A rung (the next free at dispatch; main's is 42).**
**Depends:** part a LANDED (names, `org-show`); part b need not precede (charter and hire fence nothing).
**Applied pending ORCHESTRATOR (survey Q2–Q8):** hire writes DORMANT (an active non-legacy position refuses the legacy pair's unconfined launches,
`PositionActivation.cs:186-188`); the hirer is the head of the target unit's parent; the weekly two counts charter/close/merge; vocabulary v2 adds `unit-admissible`;
earning v1 as survey Q6; the charter's home is a decision; a fourth manager layer is refused by the earning test, never by a depth constant (VISION § 3 :115).
**Today:** no writer (`OrgStore.cs:303-311`); kinds/statuses `:73-119`, `OrgEventKind` = `seeded` (`:126-130`); `MintPosition` `:41`, `PositionIdRefusal` `:52`, homes
`Council.cs:90-98`; `org_unit` DDL with the never-written `charter_publication_id` (`Database.cs:1717-1729`); `OrgDecisionKind.Mandatory` refused by `Decide`
(`OrgStore.Decisions.cs:63-74, 236-240`) — no `hire`/`retire` kind; vocabulary v1 (`:83-104`), resolver `ForecastResolver.cs:25`; `Unfundable` `OrgStore.cs:531`; the
root's head is the owner (NULL), so no agent charters a division before `U-org-chief`.
**Observable result:** a puppet division head (Mediated manifest) charters a team under its division with an AI share, a rationale and a `unit-admissible`
forecast, then hires two dormant positions into it; the chart, the events, the decisions and the ledger rationale read back; a third charter that week, a hire
into `div-operations`, a charter citing nothing current, a hire past five members and the legacy pair on their unconfined seats are each refused in words, nothing written.
Items, one commit each, one-sentence messages:
1. **The rung:** `org_unit.charter_decision_id` (REFERENCES `org_decision`) replaces `charter_publication_id` (no compat owed; the mapper and seed follow).
   Check: a fresh home's `org_unit` columns; the rung re-runs cleanly after a crash before its stamp.
2. **The rights matrix as data** (`OrgRights`: operation × the caller's relation to the target × reserved identities — root, `div-operations`, `operations`, `div-research`,
   `research`, a division head's replacement, the chief's appointment — each answer "allowed", "the owner's two presses", "app trigger or owner"), read by every
   structural writer; the O5a/O5b verbs listed as "the mandate row decides". Check: `ConformanceTests` iterate EVERY row × assurance class
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
   Check: `A_unit_admissible_forecast_resolves_from_lineage_not_todays_chart`.
6. **Names, door, CLI:** `OrgCharter`, `OrgHire` join `Ops.Organisation`; schema, `trade org charter|hire`; one `PuppetLife` act. Check: the disjointness guard; door lists.
Red-first (quote each red at base): items 2–5's checks.
**The watched mutant:** `Hire` writes `active` ⇒ `A_hired_position_is_dormant_and_the_legacy_pair_keep_turning` red (the pair Neighboured). Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.1–2.4, 2.9, 2.11, 8; ORGANISATION §§ 2, 3, 5, 13, 14; VISION § 3; the survey; O2a/O4/O5b records; `OrgStore*.cs`, `PositionActivation.cs`.
**NOT in scope:** close/merge/retire/veto/activation/the seat ceiling (part d); trial and verdict shares, tilt, verdict narrowing (e); fences; helpers; seats (`U-org-seats`).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
