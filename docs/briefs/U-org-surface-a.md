# U-org-surface-a — the organisation-first app, read-only: the chart with each position's seat and cell class in plain words, the work (mandates, deliveries, decisions, forecasts) and the costs per unit by billing class, each a page built once and updated in place
**Arrow closed:** `docs/research/2026-10-23/R24-security-board.md` § 4.7 ("Organisation-first: the chart: positions, seats and cell classes in plain words; … the work:
mandates, deliveries, decisions, forecasts; costs by billing class, per unit"), § 7 seat B item 2 (O7 re-scoped); § 0.3 ("no page shows the organisation the backend already
records"); `docs/ORCHESTRATION.md` § 2.9 (his surface). The owner, 2026-10-23: the app is "a bad reflection of the current software" (verbatim in `fleet/tmp/RESUME-2026-10-23/HEADER.md`).
Survey: `fleet/tmp/B-scratch/settings-shed/survey.md` § 4 and `U-org-surface-a-outline.md` (at `b2233886`) — re-grep every pointer.
**Protections — AUTH-001 (no press on these pages, no pipe op, no CLI verb: they only read), measurement vs claim (a rationale, report or probability is shown as the claim
it is, quoted and attributed; checks, statuses, costs and resolutions as the app's), the watcher's invisibility (ORGANISATION § 9: no page here names the watcher, its position,
its cap or its spend — Oversight alone), unknown never zero (a turn whose cost is unknown or was charged at its reservation is shown so, never as priced), the tree built once,
`Theme.cs` values only, labels in `Labels`.** Seat B; **sonnet/high** (read-only pages; no operator authority, no money path). **No rung.**
**Depends:** main (U-org-verbs-a landed: org-show's envelope reads). `U-settings-shed` builds beside it (Safety → Owner, the Board page); both edit the rail — whichever lands
second merges it.
**Today:** the rail `MainWindow.cs:39` (`enum Page`), `BuildRail` `:409-425`, pages built once `:479-503`, `Select` `:509`; no page reads `OrgStore`. Reads that exist:
`OrgStore.Units()`/`Positions()`/`Roster()`/`Standing(id)`/`UnitOf`/`Subtree`/`NameOf` (`OrgStore.cs` ~246-532), seat columns on `OrgPositionRow`; `CellProofStore.Proven()`/
`HoldsProof()`; `CellAssuranceStore.Word(EnforcementClass)`; `MandatesOf`/`Mandate`/`MandateEvents`/`MandateStatus`/`Deliveries`/`SpendOf` (`OrgStore.Mandates.cs` ~1218-1304),
`DeliveriesToJudgeOf` (`OrgStore.Packets.cs`); `DecisionsOf`/`ForecastsOn`/`OpenForecasts` (`OrgStore.Decisions.cs` ~379-424); `AiAttemptStore.UnitTotalsBetween` (~734),
`BillingClasses`. Missing: a public recorded seat assurance (`RecordedAssurance` is private, `OrgStore.Decisions.cs` ~565); one open-mandates read across the chart; decisions
since a time across positions; unit totals split by billing class in one read; escalations addressed to the owner (`to_position IS NULL`).
**Observable result:** on a seeded dev home the rail opens Chat · Organisation · Work · Costs · then the existing pages; Organisation lists each unit with its positions —
title, active or dormant, seat runtime and model, billing class, the cell class in words ("confined", "unconfined", …) and whether a proof is recorded; Work lists open
mandates (holder, decider, status, deadline, spent), deliveries awaiting judgement, and recent decisions with their forecasts and resolutions; Costs shows each unit's today
by plan, key and unknown with reserved and unknown-cost turns named; a five-second refresh changes text in place and never rebuilds a page; no page offers a press.
Items, one commit each, one-sentence messages:
1. **The reads (Core, one query each per refresh):** `OrgStore.SeatAssuranceOf(position)`, `OrgStore.MandatesOpen()`, `OrgStore.DecisionsSince(at, limit)`,
   `AiAttemptStore.UnitTotalsByClass(from, to)` (confirm what `ceiling` filters first; never add an unpriced turn as 0), `OrgStore.EscalationsToOwner()` (read only; its page is
   `U-org-surface-b`'s). Check: one test per read on a seeded store, and `The_org_reads_never_name_the_watcher`.
2. **Organisation page** (`OrganisationPage`). Checks: `The_chart_shows_each_position_with_seat_cell_class_and_billing_in_plain_words`, `…_is_built_once_and_updated_in_place`.
3. **Work page** (`WorkPage`): rationales, reports and probabilities quoted and attributed as claims. Checks: `Work_shows_open_mandates_deliveries_and_decisions_with_their_forecasts`,
   `A_claim_is_shown_quoted_and_attributed_never_as_the_apps`.
4. **Costs page** (`CostsPage`). Check: `Costs_split_each_unit_by_billing_class_and_name_unknown_cost_turns`.
5. **The rail and the no-press guard:** Organisation, Work, Costs after Chat. Checks: `The_rail_lists_the_pages_in_order`, `The_organisation_pages_hold_no_press` (a walk of each built
   tree: no Button, no editable box).
Red-first (quote each red at base): items 1–4's first checks. No money path: no watched mutant is owed; the no-press guard is watched red once (a Button added to WorkPage), quoted.
**Read first:** CLAUDE.md "Conventions" (code-built UI, Theme.cs, the tree built once); R24 §§ 0, 4.7, 5 (watcher row); the survey § 4 and the outline; `MainWindow.cs:39, 409-509`;
one existing page built once for the pattern (`OversightView.cs`); the stores named above.
**NOT in scope:** the Board page, Safety → Owner and every shed (`U-settings-shed`); ventures, the owner's inbox page, escalations shown, the veto, board acts (`U-org-surface-b`);
any press, any write, any new owner control; scores of forecasters (the audit line's).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
