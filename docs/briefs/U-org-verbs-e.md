# U-org-verbs-e — every unit's envelope has three dimensions — AI spend, trials and verdicts — each reserved against its parent's at the call that spends it; a head tilts its children's shares inside the weekly bound; and only a division head, or a unit it delegated a verdict share to, spends verdicts
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.4 (:185-191 currencies v1 = AI spend, trials, verdicts; "trials and verdicts are reserved atomically at the broker call
that spends them"; lineage immutable; `envelope_share` a fraction), § 2.9 (:321-322 `tilt` ≤ 20 points a week, a decision record with a forecast); ORGANISATION § 2
:101 and :108 ("only division heads spend verdicts"; a team "unless the division delegates a count"), § 3 :121-123 (three dimensions; re-splitting is the head's
record; a split that cannot fund its seat is refused), § 8 :254-261 (tilt); § 15 O6 (:427), part e of 5. Survey: `fleet/tmp/B-scratch/org-verbs/survey.md` § 3 (main `25bae9bc`). Pointers re-checked against 6fd6c4aa, 2026-10-24.
**Protections — EVID-001 (the campaign's own trial and verdict gates run FIRST and UNCHANGED; unit gates only add refusals; trial and verdict keys unchanged and
role-free; the verdict narrows who may SPEND, never what is judged or seen), BUDGET-001 (O4's AI admission unchanged; `Unfundable` and Σ ≤ 1 before a split is
written; no verb raises the root's or a parent's envelope), ORG-001 (headship only narrows, it grants nothing), refuse never guess: red-first and ONE watched mutant.**
Seat B; **opus/high** (the evidence path: verdicts, trials). **A rung: the LOWEST FREE at its LANDING (the rule since 2026-10-23; 50 today) — the number spelled once
(const + `Versions`) and renumbered at the rebase if another rung lands first; 47 stays unused.**
**Depends:** part c LANDED (`unit-admissible`, decision kinds, the rights matrix). May run beside part d (shared: the `Ops.Organisation` line only).
**R24-checked at dispatch (seat B, 2026-10-24):** the board sets the plan's AI day-total and its TOP division (R24 § 4.1, read from `BoardPolicy` since U-board-a2);
a head's tilt only re-splits INSIDE its own unit's envelope and no verb raises a parent's; the campaigns' evidence budgets stay the owner's (§ 4.3, EVID-001) and are
read, never written, here.
**Today:** one dimension — `envelope_share` "IS THE AI-SPEND ENVELOPE" (`Database.cs:1789-1790, 1799`; `OrgEnvelopes.Resolve` `OrgStore.cs:247-261`); trials charged in
`CampaignStore.RegisterTrial` (`:996`, called inside the run's write `Backtests.cs:688`) — the run carries its attempt (`StrategyStore.cs:290`), so a trial's chain is
`strategy_run.attempt` ⋈ `ai_attempt_funding`; verdicts charged by `ChargeVerdict(campaign, version, at)` (`CampaignStore.cs:1296`, insert `:1326`) inside the referee's
write (`Strategy/Referee.cs:225-236`) into `strategy_verdict` with NO attempt (`Database.cs:962-968`); any ACTIVE position may ask a verdict
(`Backtests.cs:319-342`; `VerdictFor` `GatewayPipeServer.cs:2913-2918`), asserted for a TEAM head by `OrgRightsTests.cs:154-157` (rewritten here, named in the report).
**Observable result:** a puppet division head tilts its team's AI share by 0.15 and sets its trial and verdict shares; the team's executor's backtests are refused
in words naming the team, its share, its use and the campaign budget once the team's trials are spent, while the campaign still has room; the team head's verdict is
refused until its unit holds a verdict share, then served and charged to the team's chain; `research` and `operations` (division heads, NULL shares) are unchanged;
a second 0.15 tilt that week and a split leaving a sibling Σ > 1 are refused, nothing written.
Items, one commit each, one-sentence messages:
1. **The rung:** `org_unit.trial_share`, `org_unit.verdict_share` (decimal TEXT, a fraction of the campaign budget, NULL = no own cap — the legacy divisions); `strategy_verdict.attempt_id`
   (NULL on a row before the rung = funded by no unit, as rung 38 read app principals). Check: fresh home columns; rerun after a crash before the stamp.
2. **Trials:** inside `RegisterTrial`'s write, AFTER the campaign's gates, each unit of the run's attempt's RECORDED chain with a share: charged trials under that unit
   + 1 ≤ share × the campaign's trial budget, else refused in words. Check: `A_units_trials_run_out_before_the_campaigns_and_the_campaign_gate_runs_first`.
3. **Verdicts:** `ChargeVerdict` takes the attempt; the same per-unit gate on verdicts after the campaign's; the row records the attempt. Check:
   `A_verdict_is_charged_to_the_askers_chain_and_refused_past_its_units_share`; an already-judged version still answers free (unchanged).
4. **Verdicts narrowed:** in `VerdictFor`, after `RoleOf`, before anything is charged: the caller heads a unit of kind `division`, or its own unit (or an
   ancestor below the division) holds a verdict share > 0; else refused in words naming who may. Check: `A_team_head_without_a_delegated_share_is_refused_a_verdict`;
   GUARD `The_legacy_pair_ask_verdicts_as_before`.
5. **`org-tilt`** (head of the unit's parent; one currency per call): UPDATE the share, an `envelope-tilt` decision with a forecast, `org_event tilted`; refuses |Δ| >
   0.20 within 7 days (summed per unit and currency), Σ active siblings > 1, `Unfundable` (AI), a share worth < 1 trial or verdict of the open campaign, the matrix.
   Charter (part c) accepts trial and verdict shares too. Check: one test per refusal; `org-show` shows all three dimensions.
6. **Names, door, CLI:** `OrgTilt`; schema; `trade org tilt`; one `PuppetLife` act; the conformance rows extended.
7. **Folded in at dispatch (seat B's card U-verbs-c-followups, 2026-10-24 — the same charter path item 5 extends):** the Codex re-review of U-org-verbs-c fix 1
   (`fleet/tmp/B-scratch/u-org-verbs-c-fix1/rereview.md`): a ZERO cap in one billing ceiling makes `LegacyShares` (`GatewayPipeServer.Structure.cs:108-128`) drop a legacy
   division's share that another ceiling sizes, and a funded charter is refused. Read each legacy share from the ceiling the charter's funding is judged in; a zero cap
   in a ceiling the charter does not draw on drops nothing (a zero cap in the one it draws on still refuses, fail-closed). Check (the re-review's setting):
   `A_zero_cap_in_one_ceiling_keeps_the_legacy_share_the_other_ceiling_sizes` — real meter, legacy CLI seats on plan, plan cap 0, chief on key with money cap 10,
   split 0.3/0.6, a valid 0.1 experiment division chartered; plus its fail-closed twin.
Red-first (quote each red at base): items 2–5's and 7's checks.
**The watched mutant:** item 2's unit gate placed BEFORE the campaign's (or reading today's chart instead of the recorded chain) ⇒ item 2's test red. Quote, restore, green.
**Read first:** CLAUDE.md; ORCHESTRATION §§ 2.4, 2.9, 8; ORGANISATION §§ 2, 3, 8, 13; the survey § 3; O4 and U-holdout-campaign records; `CampaignStore.cs`,
`Referee.cs:170-260`, `Backtests.cs:280-330, 640-700`, `VerdictFor`.
**NOT in scope:** billing classes (`U-billing-classes`); the 75/25 contribution split and cut rules (O8); a `verdict-request` decision record (survey Q11); paper slots.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
