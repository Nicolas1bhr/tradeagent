# U-merge-evidence-shares — a merge keeps every evidence share whole: the successor's children never split more than the whole of a trial or verdict share, and a merged unit's spent trials and verdicts stay spent under its successor
**Arrow closed:** the seam between U-org-verbs-d (merge, `OrgStore.Restructure`) and U-org-verbs-e (trial and verdict shares, rung 50), found by the re-stack fixer of
U-org-verbs-e (2026-10-25) and ruled IN SCOPE before M-org1 by the orchestrator (EVID-001). Also folds U-verbs-d-followups (the Codex re-review 2 of verbs-d:
`fleet/tmp/B-scratch/u-org-verbs-d/U-org-verbs-d-rereview2.md`). ORCHESTRATION § 2.4 ("trials and verdicts are reserved atomically …"; lineage immutable), § 2.9
(*Restructuring is a transaction*: "carries reservations, debts and history to the successor"); ORGANISATION § 3. Pointers at the re-stacked verbs-e tip 47f873a8 — re-grep on main.
**Protections — EVID-001 (a merge can never give back a spent trial or verdict: the campaign's own gates stay first and unchanged; use is read through the recorded
chain plus the merge's successor link; `strategy_run`, `strategy_verdict` and `ai_attempt_funding` never rewritten), BUDGET-001 (Σ ≤ 1 per currency), refuse never
guess: red-first and ONE watched mutant.** Seat B; **opus/high** (evidence). **No rung.**
**Depends:** U-org-verbs-d and U-org-verbs-e LANDED.
**Today:** `Merge`'s split check (`src/TradeAgent.Core/Db/OrgStore.Restructure.cs:219-226`) sums only `envelope_share`; a merge can leave B's children's `trial_share`
or `verdict_share` summing past 1. `OrgStore.CarriedPairs` (`OrgStore.Restructure.cs:432`) carries merged units for AI totals only (`AiAttemptStore.cs:771-777`);
the per-unit trial use (`CampaignStore.cs` ~:1064-1080, `trial_share` over the run's recorded chain) and verdict use (~:1091-1140) do not follow a successor link, so
after A merges into B, A's spent trials and verdicts no longer count against B. `CarriedPairs` adds one UNION term per carried pair (re-review 2: SQLite's
500-term compound limit after 32 successive merges).
**Observable result:** two sibling teams each holding verdict share 0.6 under one division → their parents' merge is refused in words naming the currency, nothing
written; a team that spent its delegated verdict merges into its sibling → the successor's next verdict past the combined share is refused; 40 successive merges →
the Costs page's class totals and admission's totals still read.
Items, one commit each, one-sentence messages:
1. **Σ per currency:** `Merge` refuses when B's children would split more than 1 in ANY of AI, trial or verdict share. Check: `A_merge_whose_children_split_more_than_a_whole_verdict_share_is_refused`.
2. **Use carries:** per-unit trial and verdict use reads A's charges as B's through the same successor link `CarriedPairs` reads. Check:
   `A_merged_units_spent_verdict_stays_spent_under_its_successor` (+ the trials twin).
3. **Bounded carry query:** replace the UNION-per-pair with a bounded form (a recursive CTE over the `merged` links, or a temp table filled in the read's transaction). Check:
   `Forty_successive_merges_still_read_the_class_totals_and_the_day_totals`.
Red-first (quote each red at base): items 1–3's checks.
**The watched mutant:** item 2's carry removed from the verdict count ⇒ item 2's test red. Quote, restore, green.
**Read first:** CLAUDE.md; both units' briefs and records (`docs/briefs/U-org-verbs-d.md`, `U-org-verbs-e.md`; `fleet/records/`); `OrgStore.Restructure.cs`; `CampaignStore.cs:1040-1240`; `AiAttemptStore.cs:760-860`.
**NOT in scope:** close (a closed unit keeps its own history); vetoing a merge (refused by d); the campaign's own counts.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
