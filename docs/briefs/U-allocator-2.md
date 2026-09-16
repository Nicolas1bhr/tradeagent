# U-allocator-2 — versioned parentage, comparable trials, an exploration budget reserved, the retirement disposition that erases nothing
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:271-272` (this unit's line: versioned parentage, comparable trials, exploration, bounded
replacement, the retirement-candidate event, the directors evaluated too), `:200-201`, `:218-226` (what the allocator "as first specified did
not give"; retirement never erases history, cancels reconciliation or kills a deployed strategy; not a new agent-reachable permission),
`:131-132` (a renewal never resets holdout access), `:94` (task priority — NOT capital; say so), then `Db/Database.cs:621-632` (`strategy_version`:
no parent), `:771-786` (`strategy_campaign`, `renewed_from`), `Db/CampaignStore.cs:241,291-297,320-337,344-420` (`Lineage`; `TrialRefusal` reads and
`RegisterTrial` writes in two transactions — the by-one race `U-referee-1` named), `AiAttemptStore.cs:107-160,255-280` (a budget reserved, not
checked, in one write), `boundary_event` and the sealed assessments from `U-council-concurrent-2`, `MissionEventStore.cs`, `DailyReports.cs`
section 8, `docs/CONTRACTS.md`. Branch `u-allocator-2`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-allocator-2`, rebased
onto `main` AFTER `U-council-concurrent-2` and `U-allocator-1` land. Every item ships with a test RED before it and ONE mutant, quoted. NOT the
money path: nothing here reaches `PlaceAsync`. No box, no ATAS, no money. **Schema 21.**

**Why.** The only lineage in the build is the campaign's `renewed_from`; a version has no parent, so a variant is a fresh hash that can buy
fresh holdout access. No exploration budget exists — only `trial_budget` and `verdict_budget` — and the trial charge is read in one transaction
and written in another. Nothing records a retirement candidate, a disposition, or a director's forecast against a declared baseline. No
team or candidate-agent entity exists in this build: the parts of `:219-222` whose subject is such an entity (heritable candidate definition,
diversity budgets, births, probation, rollback) are NOT here and are named in `CONTRACTS.md` as waiting on one.

1. **`strategy_version.parent_version_id`, nullable, DECLARED by the submitter and never inferred; outside the strategy hash**, so parentage never
   moves an id; a version with none is its own root. RED: `no such column: parent_version_id`. Mutant (the parent inferred from the role's last
   version): two unrelated programs read as parent and child.
2. **Comparable trials: a child is charged to its parent's campaign lineage,** so a variant cannot buy holdout access by being a new hash. RED: a
   child registers a trial against a fresh budget. Mutant (charged to the child's own campaign): the lineage count unchanged by a variant.
3. **An exploration budget on the campaign, RESERVED in one transaction** (the `AiAttemptStore.Begin` shape): a bounded part of the trial budget
   set aside for versions with no parent or an unpromoted parent; `TrialRefusal` and `RegisterTrial` become one `Database.Write`, which closes
   `U-referee-1`'s by-one race. RED: exploration spends the whole trial budget; two roles racing (`Barrier(2)`) register two trials on a budget of
   one. Mutant (the reservation outside the transaction): `Expected: 1 / Actual: 2`.
4. **The retirement-candidate disposition, applied by code, erasing nothing:** a `retirement` boundary event (the `U-council-concurrent-2` machine,
   not a second one) whose disposition writes no DELETE and no UPDATE to any history row, cancels no reconciliation, clears no standing
   allocation, and fences new assignments only. RED: a retirement deletes or rewrites a trial row. Mutant (retirement clearing the allocation
   row): a retired candidate's deployed strategy silently loses its capital.
5. **The directors are evaluated too:** each assessment at a boundary records its recommendation and a DECLARED baseline at declaration time,
   compared at the registered review time; section 8 names each director's record against its baseline. RED: a disposition records no
   recommendation and no baseline. Mutant (the baseline read at review time): every forecast reads correct.

Not this unit: the allocation ceiling and the order path (`U-allocator-1`); the boundary-event table, the sealed assessments and the challenge
(`U-council-concurrent-2` — this unit depends on it and must not rebuild it); anything needing a team or candidate-agent entity.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, what you did NOT do.

## Report

Gated tip `e7add72`, five item commits rebased onto `main` at `910795b`; the report commit sits above it. **Schema 21.** NOT the money path: nothing here reaches `PlaceAsync`; no box, no ATAS, no money.
Gate, Release, all to files under the scratchpad: `dotnet build TradeAgent.sln -c Release --no-incremental` (bin/obj deleted first) → 17 projects, `0 Warning(s)`, `0 Error(s)`. Unit `Failed: 0, Passed: 1140`; Fault `Failed: 0, Passed: 345`; Integration `Failed: 0, Passed: 668, Skipped: 1, Total: 669` — 2153 passed, 0 failed. Six touched classes 3× each — VersionLineage 6, RetirementBoundary 7, BoundaryLedger 9, CampaignLedger 20, CouncilRelay 20, VenueCatalog 8 — every run `Passed!`; no `Timing` red anywhere, so no re-run was owed. Names vs `main`: sets 1807 → 1820, `[Fact]`/`[Theory]` 1775 → 1788, **0 removed**, 13 added, none moved or renamed. The rung-16 rollback fixture now undoes 21 as well (`parent_version_id`, `charged_to`, `ix_trial_charged_to`, `exploration`, `exploration_budget`, `recommendation`, `baseline`, `review_baseline`).
1. **`strategy_version.parent_version_id`** — declared by the submitter through the `backtest` op's `parent`, self-referencing, outside the version hash, first declaration stands. RED: `SQLite Error 1: 'table strategy_version has no column named parent_version_id'`. Mutant (parent inferred from the role's last version): `Assert.Null() Failure: Value is not null / Actual: "6b52acd9f0d5…"`.
2. **A variant charged to its ancestry's campaign lineage** (`strategy_trial.charged_to`, walked to the eldest charged ancestor then forward through renewals; counted under either number, so it can only tighten). RED (item reverted): `Assert.Equal() Failure: Values differ / Expected: 2 / Actual: 1`. Mutant (charged to the child's own campaign): `Expected: 2 / Actual: 1` — the lineage count unmoved by a variant.
3. **The exploration reserve**, two pots summing to the trial budget, both counts read inside `RegisterTrial`'s own `Database.Write`. RED: `Assert.False() Failure / Expected: False / Actual: True` (a third exploration trial taken with 3 of 5 unspent) and, on `Barrier(2)`, `Expected: 1 / Actual: 2`. Mutant (the reserve refused by the pre-run look and not at the gate — the reservation outside the transaction): `Expected: 1 / Actual: 2`.
4. **The retirement disposition**, opened on the `U-council-concurrent-2` machine, default frozen at open to bounded replacement, applied by `ApplyDue`, fencing a NEW trial and a NEW verdict only. RED: `Assert.False() Failure / Expected: False / Actual: True` — a retired candidate registered a further trial. Mutant (retirement clearing the allocation row): `Assert.NotNull() Failure: Value is null` — the deployed strategy lost its capital.
5. **The directors' own record** — `RECOMMENDATION:` and `BASELINE:` declared on the assessment and sealed with it, against `review_baseline` written in the same UPDATE as the disposition; section 8 prints one line per director per settled boundary, silence included. RED: `Assert.Equal() Failure: Strings differ / Expected: "promoted" / Actual: null` and `Not found: "recommended deploy, code applied hold — d"…`. Mutant (the declared baseline read at review time): `Expected: "unjudged" / Actual: "promoted"` and `Not found: "forecast promoted, measured unjudged — di"…` — every forecast reads correct.
**The brief's item-4 RED corrected:** nothing in the product applied a retirement at all, so no code path could delete or rewrite a trial row; the reachable RED is the absent fence, and "erases nothing" is held by the row-by-row assertions plus the mutant above.
**Choices, now in `docs/CONTRACTS.md` under `U-allocator-2`:** the candidate is a strategy VERSION, and the retirement default is bounded replacement (a successor whose promotion stands) rather than "promoted ⇒ keep" — under the latter a retired candidate could never hold capital and `:223`'s "never kills a deployed strategy" would be unreachable; `CampaignExplorationBudget` = **50** of 200, clamped to `[0, trial_budget]`, a campaign opened with none declared recording its whole budget; the stated limit that a parent claim is unverifiable but **creates no allowance**, the two pots summing to the trial budget; a loss-budget boundary has no measurable baseline and none is required of its assessment.
**Deviations:** `TrialRefusal` gained a `versionId` parameter (two call sites in `CampaignLedgerTests` updated, no assertion changed); three exact column-list assertions extended for the new columns and still exact, still asserting no role and no attempt column; the assessment fixtures in `BoundaryLedgerTests` and `CouncilRelayTests` now declare the two lines the app requires, and the directors' guide says so.
**NOT done:** nothing in this build calls `OpenRetirement` — the policy that decides WHEN a candidate is put up is about a population; every part of `docs/COUNCIL.md`:219-222 whose subject is a TEAM or CANDIDATE AGENT (heritable candidate definition, diversity budgets, bounded births and turnover, probation, rollback, a cross-candidate selection protocol with registered review times) is named in `CONTRACTS.md` as waiting on an entity this build does not have. No verb, no pipe op and no UI for retirement; `docs/USER-GUIDE.md` untouched; no screen, no box, no ATAS, nothing sent to any venue.
