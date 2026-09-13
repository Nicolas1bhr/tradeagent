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
