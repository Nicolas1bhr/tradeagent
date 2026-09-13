# U-referee-2 — the verdict: a promotion record bound by hash, invalidated by a changed assumption, forward evidence after the freeze, delivered
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rule 9 `:34-36` what a promotion binds; `:32-33` what it authorises; `:74` the
Situation's promoted strategy; `:89-91` the verdict wake; `:135-136` forward evidence before capital; `:179-180` the owner's report; `:196-197`
referee-budgeted disclosures), then `U-referee-1` and `U-runner-3` on `main` (`Db/StrategyStore.cs`, `CampaignStore`, `Referee.RequestVerdict`,
`Strategy/Backtest.cs` — the run id's inputs), `Db/PublicationStore.cs` (`Commit` :167 vs `Record` :222, `IdOf` :129), `Db/MissionEventStore.cs:11-43`
(kinds), `AgentRuntime/MissionLoop.cs:320-441` and `App/AppHost.cs:965-1016` (the Situation), `Gateway/DailyReports.cs:402-436`, `DailyReport.cs:408-411`.
Branch `u-referee-2`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-referee-2`, rebased onto `main` AFTER `U-referee-1` lands.
Money-path grade: every guard ships with a test RED before it and ONE mutant, quoted. No box, no money, no order. **Schema 15.**

**Why.** `strategy_version` has no state: nothing says a version is promoted, refused or invalidated, and the binding material rule 9 demands
(code, parameters, dependencies, data, evaluator, execution-and-cost model, scoring policy) is scattered across one `strategy_run` row. Nothing
compares a version's `created_at` with a run's window, so a version frozen today promotes on last year's bars. No verdict reaches a role or the
owner: `MissionEventKind` has no verdict, section 8 has no promotion line. The referee is code; nothing here is a role's opinion.

1. **`strategy_promotion`**: one immutable content-addressed row (id = sha256 over the bound tuple) binding version id, campaign id, scoring-policy
   sha256, interpreter build, holdout dataset id and its sha256 at run time, execution model, evaluator version and the holdout run id, with
   `verdict` (`promoted`/`refused`), `reason`, `at`; app-only writer, `ON CONFLICT DO NOTHING`, no update, no delete. RED: `VersionById` returns
   a row with nothing to promote. Mutant (the id minted from the clock): one version promoted twice with two records.
2. **The holdout run is the referee's own**: `Referee.Verdict` (after `RequestVerdict` charged the budget) runs the version over the campaign's
   holdout window through the app's backtest, in-process, and scores it by the campaign's fixed policy; its trace and metrics are recorded with
   the run and never published whole. RED: no holdout run can be made — every run is refused past the cutoff (item 2 of `U-referee-1`), and the
   referee has no door. Mutant (the referee's run recorded as a research trial): the holdout charged as research.
3. **A changed assumption invalidates the evidence** (`COUNCIL.md:35`): a promotion whose dataset is later `REJECTED`, whose interpreter build
   or scoring-policy sha no longer matches the current, reads `invalidated` with the reason; `Promotions.Standing(versionId)` is the one reader.
   RED: `Reject` on the dataset leaves the promotion standing. Mutant (checked against the dataset's CURRENT sha instead of the sha on the run):
   a re-collected dataset revalidates a promotion.
4. **Forward evidence after the freeze**: a promotion requires the holdout run's window to begin AFTER the version's `created_at`; a run over
   history alone is refused in words ("evidence that precedes the freeze"). RED: a version created today promotes on last year's bars. Mutant
   (the comparison against the run's `created_at`): a run recorded today over pre-freeze bars counts as forward.
5. **The verdict is delivered and told**: a `verdict` mission event (one, keyed by promotion id) wakes the Research role; a `PublicationKind.Verdict`
   publication through `Commit` carries the SANITISED feedback the policy allows (the verdict, the reason class, never the holdout metrics or
   trace); the Situation names the promoted version (`COUNCIL.md:74`); section 8 lists every promotion, refusal and invalidation under "measured
   by TradeAgent" and its gap line goes only when a run or a verdict exists. RED: no verdict kind, no promotion line. Mutant (the holdout
   metrics published to the team): the holdout leaks through the relay.

Not this unit: `TryAuthorizeExecution` and the order path (a later unit READS `Promotions.Standing` there); paper or live running of a promoted
version (`U-flatten` and after); the directors' sealed assessments and the bounded challenge (`U-council-concurrent`); the allocator.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, what you did NOT do.
