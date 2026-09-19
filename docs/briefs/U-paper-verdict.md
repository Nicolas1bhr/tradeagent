# U-paper-verdict — a favourable verdict over never-served HISTORICAL holdout months makes a version eligible for PAPER, never for live
**Arrow closed:** verdict → eligibility for paper observation (`docs/PRINCIPLES.md`: "Distinguish acceptance of a program, a favourable historical verdict,
eligibility for paper observation, and eligibility for live capital"; "Forward paper evidence cannot be required before the very first paper run that produces
it"). **Observable result:** a version frozen AFTER the cutoff whose holdout run completes, trades and nets above zero is judged `paper-eligible`; `promoted`
still needs a window that post-dates the freeze; the live allocation path refuses a paper-eligible version categorically. **Schema 23.** MONEY PATH touched:
`Allocations.Record`'s standing check (a refusal added, none removed). `U-verdict-op` (a parallel leg) owns `Protocol.cs`, `GatewayPipeServer.cs`, `Program.cs`,
`GrantedWorkerTools.cs`, `GatewaySchema.cs`, `WorkspaceBuilder.cs` — do not edit those.
Read first: `docs/PRINCIPLES.md` § Evidence; `docs/CONTRACTS.md` 2212-2352; `Referee.cs:177-290,454-522` (`Verdict`, `ScoringPolicyV1`, `RefereeFeedback`),
`CampaignPolicy` (`Referee.cs:201`); `PromotionStore.cs` (`PromotionVerdict`, `PromotionReason`, `PromotionState`, `Standing`); `CampaignStore.cs:64-65,258,307`
(policy copied at open and renew); `AllocationStore.cs:126-129,192-193`; `MissionLoop.cs:692` (`PromotedLine`); `DailyReports.cs` section 8; `Database.cs`
(rung 22 → 23; the rung-16 rollback fixture undoes 23 too). Rebase onto `main` first; resolve conflicts yourself.
Items, one commit each with a one-sentence message:
1. `CampaignPolicy.PaperV1`: a second, separately identified policy TEXT + sha — V1's performance clauses (run completed, ≥ 1 closed trade, net after declared
   costs > 0) WITHOUT the freeze clause, worded "historical holdout evidence: eligible for paper observation only". Schema 23 adds `campaign.paper_policy` and
   `campaign.paper_policy_sha256`, copied at `Open`, carried by `Renew`; the migration pins the build's PaperV1 onto every existing campaign row (the app's own
   constant — the one backfill this rung makes, stated in `CONTRACTS.md`).
2. `Referee.Verdict`: the holdout run is made ONCE; `ScoringPolicyV1.Reason` is asked first, unchanged; when its answer is `evidence-precedes-the-freeze`, the
   paper policy's clauses are ACTUALLY evaluated on the same run: all met → `PromotionVerdict.PaperEligible` (`"paper-eligible"`) with a reason class of its own
   (closed vocabulary), else `refused` with the performance clause that failed — the informative reason, never the freeze. The row's `ScoringPolicySha256` is
   the sha of the policy that produced the verdict (PaperV1's for paper-eligible) and the campaign must hold it, refused in words otherwise.
   `RefereeFeedback.Text` says PAPER-ELIGIBLE in words and why it is not promoted; no figure.
3. `Promotions.Standing` answers a fifth state `paper_eligible`, invalidated exactly as `promoted` is (dataset gone/rejected/re-collected, interpreter or policy
   sha moved); `IsPromoted` stays FALSE for it; add `IsPaperEligible`. `Allocations.Record` (the live path) refuses a paper-eligible version in words naming
   "historical evidence; paper only". The dispatch gate is unchanged (no allocation → `ALLOCATION_NONE` as today).
4. A paper-eligible verdict is DELIVERED to Research (the wake and the sanitised note, as today) but OPENS NO consequential boundary: paper observation is an
   ordinary experiment by app policy; the directors' 24 h review stays the ceremony of a `promoted` verdict (the principles: "every ordinary experiment need
   not purchase a fixed ceremony"). `PromotedLine`, report section 8, `CONTRACTS.md` "The verdict" say all of it; `USER-GUIDE.md` one sentence.
Red-first tests (the `VerdictOverPipeTests` fixture shape, in process; freeze the version AFTER the cutoff for the paper arm):
- `A_profitable_version_frozen_after_the_cutoff_is_paper_eligible_and_not_promoted` — RED today: refused, `evidence-precedes-the-freeze`.
- `An_unprofitable_version_frozen_after_the_cutoff_is_refused_for_the_performance_clause_not_the_freeze`.
- `A_version_frozen_before_the_cutoff_is_still_promoted_under_V1` — the deployed contract; if GREEN on the base, record it as a guard checked, not a RED.
- `The_live_allocation_path_refuses_a_paper_eligible_version` — quote the refusal. `A_paper_eligible_verdict_opens_no_boundary_and_still_wakes_research`.
- `Standing_invalidates_a_paper_eligible_verdict_like_a_promotion` — the dataset re-collected under another sha → `invalidated`.
Mutants to watch red and quote: (i) `IsPromoted` true for `paper_eligible` → the live-allocation test red; (ii) the paper clauses skipped (the freeze refusal
mapped straight to paper-eligible) → the unprofitable-version test red.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings, 0 errors; the three suites in Release to files → 0 failed; touched classes 3×; test
names vs `main` → 0 removed. Report ≤ 20 lines appended here as `## Report`: tip sha, gate counts pasted, one line per item, RED and mutant lines quoted, what
you did NOT do. Do not push, do not merge, touch nothing in `docs/briefs/` but this file.

## Report
The gate below ran at `d71e9ad`, which is this report commit's PARENT and the last commit of code; the tip adds this report and nothing else. Rebased onto `main` `3557e1e`, which already carried `U-verdict-op`; conflicts: none.
Gate at that tip: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s) / 0 Error(s)`, 17 projects.
Unit `Failed: 0, Passed: 1156, Skipped: 0`; Fault `Failed: 0, Passed: 374, Skipped: 0`; Integration `Failed: 0, Passed: 677, Skipped: 1`.
Touched classes (`PaperEligibleVerdictTests`, `RefereeVerdictTests`, `VenueCatalogTests`) 3× → `Passed: 30` each run. Test names vs `main`: 1861 → 1871, **0 removed**.
1. DONE — `CampaignPolicy.PaperV1` + sha, `campaign.paper_policy(_sha256)` at schema 23, copied by `Open`, carried by `Renew`, migration pins PaperV1 onto existing rows; rung-16 fixture undoes 23; `CONTRACTS.md` names it as this rung's one backfill.
2. DONE — one run; `ScoringPolicyV1.Reason` first and unchanged; on `evidence-precedes-the-freeze` only, `PaperPolicyV1` is evaluated on the same run → `paper-eligible` / `meets-the-paper-policy-on-historical-evidence` carrying PaperV1's sha (campaign must hold it, refused in words otherwise), else `refused` with the performance clause. `RefereeFeedback.Text` says PAPER-ELIGIBLE, no figure.
3. DONE — fifth state `paper_eligible`, invalidated as `promoted` is (re-checked against the sha of the policy that produced it); `IsPromoted` FALSE; `IsPaperEligible` added; `Allocations.Record` refuses naming "historical evidence; paper only"; dispatch gate untouched.
4. DONE — delivered (wake + sanitised note), `OpenBoundary` skipped; `PromotedLine`, report section 8, `CONTRACTS.md`, `USER-GUIDE.md` say it.
5. DONE (manager added mid-leg) — the stale "You cannot ask for one" sentence in `RefereeFeedback.Text` replaced with `trade verdict --version <hash>` / `--dataset <id>`, the budget counted across renewals, and what comes back; no digits, so the no-figure test is unchanged and still exact.
RED on the base (`a617486`, 5 of 6 red, quoted): paper-eligible → `Expected: "paper-eligible" / Actual: "refused"`; unprofitable → `Expected: "not-profitable-after-costs" / Actual: "evidence-precedes-the-freeze"`; standing → `Expected: "paper_eligible" / Actual: "refused"`; boundary and live-allocation tests red on the same precondition line. `A_version_frozen_before_the_cutoff_is_still_promoted_under_V1` was GREEN on the base — recorded as **a guard checked, not a RED**.
Mutants, both watched red then put back: (i) `IsPromoted` true for `paper_eligible` → `Failed … The_live_allocation_path_refuses_a_paper_eligible_version` / `capital was allocated to a paper-eligible version`; (ii) paper clauses skipped → `Failed … An_unprofitable_version_…` / `Expected: "refused" / Actual: "paper-eligible"`.
Assertion changed, named as required: `RefereeVerdictTests.A_version_frozen_after_the_held_back_window_begins_is_refused_in_words` — kept, same name, same fixture; its `Refused`/`PrecedesTheFreeze` assertions now read `PaperEligible`/`MetOnHistory`, because that arm's recorded word is exactly what this unit changes. The property it was written for is asserted unchanged (clause applied against the WINDOW, figures good, still not promoted).
Also touched, stated: `BoundaryBaselines.IsKnown` accepts `paper_eligible` — `Measure` returns `Standing(...).State`, so without it a director could not declare a baseline the app can measure. A vocabulary widened to match its own source, no guard weakened.
NOT DONE / NOT VERIFIED: nothing runs a paper-eligible version forward on paper — this unit records eligibility only. `Promotions.Current()` still answers only promoted/invalidated, so the new `PromotedLine` arm is reached by a caller holding the version's own standing (asserted directly); I did not widen `Current`, which the brief did not ask for. `DailyReports.cs` needed no change — section 8 prints the verdict word and the reason class, both new, and the test asserts the rendered document. `MissionLoop.PromotedLine`'s `_` arm still says "You cannot ask for a verdict"; the manager's item named `RefereeFeedback.Text` only, so I left it — it is now stale too. Nothing was run on Windows, no provider or venue was called, no order was placed. Not pushed, not merged.
