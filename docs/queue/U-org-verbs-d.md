# U-org-verbs-d — a head closes, merges or retires inside its subtree as one transaction that disposes every open mandate, fences what it touches and moves no past charge; the owner vetoes or activates in-process; and the owner's unconfined-seat ceiling is code
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.9 (:318-321 `close`/`merge`/`retire` with throttles; :322-324 reserved identities; :328-330 *Restructuring is a
transaction*: "disposes every open mandate …, fences the grants it touches, carries reservations, debts and history to the successor, and keeps funding lineage; a veto
restores structure, never work already done"), § 2.3 (fences; "Lifting the gate starts nothing paid: positions still come through the owner's appointment chain"), § 2.4
(lineage immutable); ORGANISATION § 3 :124-137 (lifecycle; 14 days; veto), § 10 :319-321 (the owner's unconfined-seat ceiling, default 3), § 13 :362, :374; VISION :513
(CRED-001: growth past three waits for containment); § 15 O6 (:427), part d of 5. Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` (main `25bae9bc`) — re-grep.
**Protections — AUTH-001 (veto, activation and the ceiling are IN-PROCESS only: no op, verb, setting raise or packet field), ORDER-001 (nothing about `operations`
or its division; research never retired by an agent; a division head replaced only by the owner or an app trigger), CRED-001 (the seat ceiling), BUDGET-001
(`ai_attempt_funding` never rewritten; a LOST attempt keeps its reservation), EVID-001 (trial and verdict counts carry; keys carry no role), refuse never guess:
red-first and ONE watched mutant.** Seat B; **opus/high**. **No rung** (`org_unit.status/closed_at`, `org_position.status/ended_at/unit_id`, `org_event`).
**Depends:** parts b (fences) and c (rights matrix, the chart writers, decision kinds) LANDED.
**Applied pending ORCHESTRATOR (survey Q1, Q2, Q4, Q13):** the row's "unconfined-seat ceiling" is the owner's SEAT-count ceiling (the lane ceiling landed in O9,
`Lanes.cs:93`); activation is an owner writer; the weekly two counts close/merge; position-wide fences are `org_event fenced` rows.
**Today:** no transition exists beyond O5a's cancel and part a's rescope; units and positions are mutable rows (`Database.cs:1717-1756`); funding chains are recorded per
attempt (`Database.cs:2295-2380`) and units' spend read through them (`AiAttemptStore.cs:660-662`); the activation gate (`PositionActivation.cs:169-222`) refuses the legacy
pair beside any active non-legacy position (`:186-188`) and has no seat-count rule; facts composed in `AppHost.cs:398-413`; proof `CellProofStore.cs:126`.
**Observable result:** a puppet division head retires a team member mid-turn (its turn ends, its mandates are cancelled with the reason), merges two of its teams
(B inherits A's positions and child units; A reads closed with its successor; past charges stay on A's chain), and closes a team chartered 15 days ago; the owner's
in-process veto restores the closed team (decider of record `owner`, positions back DORMANT, no work undone) and his activation turns a dormant hire active only
while the seat ceiling has room; every reserved identity, a 10-day-old unit, a third change that week and an unconfined caller are refused in words, nothing written.
Items, one commit each, one-sentence messages:
1. **The transaction** (`OrgStore.Restructure` core, one write): every open mandate held or decided inside what ends is cancelled with an app-composed reason; every
   position touched gets an `org_event fenced` (part b ends its turn); wakes withdrawn; nothing in `ai_attempt_funding` changes. Check:
   `A_restructure_cancels_fences_and_moves_no_past_charge` (a unit's spent before = after, read through the recorded chain).
2. **`org-retire`**: position `ended`/`ended_at`; a head's unit goes vacant; a `retire` decision; refuses `operations`, `research`, a division head, the chief, the
   caller itself. Check: per refusal; `Retiring_a_position_mid_turn_ends_its_turn` (puppet, `signal`/`await`).
3. **`org-close`**: unit `closed`/`closed_at`, its positions ended, a `close` decision; refuses root, `div-operations`, `div-research`, open child units, < 14 days
   unless a kill forecast on its charter resolved, the weekly two. **`org-merge`** (siblings of one kind): A closed with B named, A's positions and child units moved
   to B, a `merge` decision, both fenced. Check: one test per refusal; `A_merge_moves_positions_and_children_and_keeps_As_history`.
4. **The owner's writers, in-process** (`OrgStore.Veto(eventId)`, `OrgStore.Activate(position)`; no `Ops` entry, a guard asserts none): a veto undoes one structural
   event (charter → closed; close/retire → restored DORMANT; hire → ended), decider `owner`, fences, never counted in the throttle; activation dormant → active.
   Check: `The_veto_restores_structure_never_work`; GUARD `No_pipe_op_or_door_verb_reaches_veto_or_activation` (red with an op added).
5. **The unconfined-seat ceiling** in the activation gate: a position whose declared seat runs a model below Confined is refused activation and launch while that
   many such ACTIVE positions — the legacy pair counted — reach `min(setting, 3)`; a setting lowers only (a value above 3 reads as 3, as `LanePolicy.Clamp`
   does); puppets exempt; refusal kind HostOrLaunch with both counts named. Check: `A_fourth_unconfined_seat_is_refused_whatever_the_setting_says`.
6. **Names, door, CLI:** `OrgRetire`, `OrgClose`, `OrgMerge`; schema; `trade org retire|close|merge`; one `PuppetLife` act; conformance rows from part c extended.
Red-first (quote each red at base): items 1–5's checks.
**The watched mutant:** the transaction skips cancelling mandates HELD in the closed unit ⇒ item 1's test red (an open mandate survives its holder's unit). Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.1–2.5, 2.9, 8; ORGANISATION §§ 2, 3, 10, 13; VISION § 3, CRED-001; the survey; parts b and c; `PositionActivation.cs`, `Lanes.cs`.
**NOT in scope:** the Org page and its presses (O7); sleep/dormancy of a subtree (the circuit breaker); a division head's replacement trigger (O8); envelopes (e); helpers.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
