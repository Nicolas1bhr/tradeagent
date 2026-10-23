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

## Report
Base 4749811c; seat B session; no usage reset, no Mihael contact, no Co-Authored-By. Tip: see the final message (the branch's head at dispatch).
- Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` -> 0 Warning(s), 0 Error(s). Gate 2: OrgSurfaceReadsTests, OrganisationPageTests, WorkPageTests, CostsPageTests, OrgSurfaceShellTests (+ OrgLedgerTests, BillingClassesTests, which guard what I touched) 3x Release through suite.sh: Passed 49/49, three times. Gate 4: names.sh main -> "removed: 0" ([Fact]/[Theory] base 3042, tip 3063 before fix pass 1). Tests box: NOT RUN - busy (ready exit 4, a run in progress). Screenshots NOT RUN - seat B runs the Mac UI check at landing.
- CI run 39875247481 (a23d7e6d) was RED on one test, OrgLedgerTests.No_pipe_op_writes_an_org_table (my 5 new OrgStore reads were not declared in its list): declared as reads in 13c7b954, guard not weakened. Codex review: fleet/tmp/B-scratch/u-org-surface-a/U-org-surface-a-review.md (4 points, all fixed in fix pass 1).
- Item 1 DONE (OrgStore.Surface.cs: SeatAssuranceOf, MandatesOpen, DecisionsSince, EscalationsToOwner; AiAttemptStore.UnitTotalsByClass, exact per class, one shared aggregate list). Deviations: RecordedSeats() (whole chart in one query) lives in AiAttemptStore, because BillingClassesTests allows only that file to name billing_class; DeliveriesAwaiting() added (Work needs every decider's waiting deliveries in one read). Red at base: "'OrgStore' does not contain a definition for 'MandatesOpen'" (and the other four).
- Item 2 DONE (OrganisationPage). Red at base: "The type or namespace name 'OrgSurfaceSources' could not be found". Item 3 DONE (WorkPage; claims only via OrgText.Claim, quoted and attributed; curly quotes inside a claim are neutralised). Red: "'WorkWords' could not be found". Item 4 DONE (CostsPage; plan / key / not established, unknown cost named, never zero). Red: "'CostsWords' could not be found".
- Item 5 DONE (rail: Chat, Organisation, Work, Costs, then the existing pages; built in BuildPages, refreshed in UpdateShell). Red at base: The_rail_lists_the_pages_in_order "Expected [Chat, Organisation, Work, Costs] Actual [Chat, Dashboard, Report, Inbox]". Watched no-press guard: a Ui.Secondary added to WorkPage -> The_organisation_pages_hold_no_press "Assert.Empty() Failure: Collection was not empty"; reverted. Watcher guard watched red with a planted word: "Found: watcher"; reverted.
- Not done / not verified: no run of the app (screens unseen); AppHost.ResolvedSeatOf/For(host) wiring is exercised only through the window, not a unit test; the full Unit/Fault suite is CI's.

## Fix pass 1
Codex points (all four real, one commit each, red first), base for the fixes 13c7b954:
- P: probability printed with 0.#### (0.99999 -> 1). Now exact, culture-free (a64c0c53). Red: A_probability_is_shown_exactly_as_recorded "Sub-string not found".
- P: a mandate with unreadable checks threw through the refresh. MandatesOpen now returns an OrgList and refuses that row by id (cc1c512f). Red: JsonException 'not json' is an invalid JSON literal.
- P: a parent cycle / lost unit was drawn as extra roots. OrgText.Tree now returns the damage, Organisation and Costs say "the chart is damaged: ..." naming the units and draw none (ec26e22f). Red: Not found: "the chart is damaged".
- P: empty seat columns were shown as "the app's default runtime and model". Now the seat as the launch resolves it (AppHost.SeatFor via OrgSurfaceSources.SeatOf), or its refusal, or "not resolved here" (never a default) (commit after ec26e22f). Red: "The type or namespace name 'ResolvedSeat' could not be found".
- Guard fix: OrgLedgerTests declares the five reads (13c7b954). Re-review of the fix commits: started in the dispatch call, output <SCRATCH>/rereview.md.
