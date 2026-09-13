# U-referee-1 — the protocol: a holdout no pipe caller can reach, a campaign with its scoring policy fixed, trials charged, verdicts scarce
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rules 8–9; `:55-57` the referee is code, never a role; `:130-136` the protocol;
`:196-197,212` what a team may see and that a leaked holdout cannot become unseen), then `U-runner-3`'s product on `main` (`Db/StrategyStore.cs`,
`Gateway/Backtests.cs`, the `backtest` op in `GatewayPipeServer.cs`, `Protocol.cs`), `Db/DatasetStore.cs:8-54` (two states, no class),
`GatewayPipeServer.cs` (`DataList`/`DataBars` take no `AgentContext`; the one role gate is `IsMutating && !MayPlaceOrders`),
`GatewayTypes.cs:99-157` (`AgentContext.Role`, null for a roleless hello), `Security/AgentGrants.cs`, `Trading.cs` (settings with defaults),
`Db/Database.cs` (the ladder), `Versioning.cs`. Branch `u-referee-1`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-referee-1`,
rebased onto `main` first. Money-path grade: every guard ships with a test RED before it and ONE mutant, quoted. No box, no money. **Schema 14.**

**Why.** Nothing holds out anything: `dataset` has `ACCEPTED`/`REJECTED` and no class, `data-bars` and `backtest` are served to any authenticated
caller — the role `U-containment` put on the context is never consulted — and no campaign, scoring policy, trial count or verdict count exists
(`grep -rn campaign src` → doc-comments). A leaked holdout cannot be unleaked, so this is the gap that cannot be closed later. Where COUNCIL is
silent the choices below are choices, said so in `CONTRACTS.md`: a holdout is a TIME CUTOFF on a dataset, not a second dataset; holdout bars
are served to NO pipe caller, the referee alone (in-process) reads them; budgets are settings with defaults.

1. **`dataset.holdout_from` (nullable UTC time) and `dataset.evaluation_class` (`research` default, `fixture`)**, set only by the app in the
   owner's window (a two-press card on the Data page: the cutoff, the words "bars from here on are evidence the research process never sees");
   no pipe op sets, clears or moves it; moving an existing cutoff EARLIER is refused (a bar once served cannot become holdout). RED: no column,
   nothing to assert. Mutant (the cutoff taken from a request field): a caller marks its own holdout.
2. **No pipe caller reads holdout bars.** `DataBars`, `DataList` and the `backtest` handler take the `AgentContext`; a request whose window
   reaches `holdout_from` is REFUSED in words — never clipped — for every role and for a roleless hello alike; `data-list` names the cutoff.
   RED: a Research grant reads bars past the cutoff today (the test cannot even be written — the handler has no context). Mutant (a null role
   treated as "not research"): the roleless caller reads the holdout.
3. **`strategy_campaign`**: id, name, the scoring policy text and its sha256 fixed at open, `trial_budget`, `verdict_budget`, `holdout_dataset_id`,
   `opened_at`, `renewed_from`, `closed_at`; opened by the app when the owner sets a holdout (one campaign per holdout dataset), renewal by code
   carrying `renewed_from` and the parent's holdout, so no new campaign resets holdout access (`COUNCIL.md:132`). RED: no table. Mutant
   (renewal minting a fresh id with no `renewed_from`): a renewed campaign starts with untouched holdout access.
4. **`strategy_trial`**: every registered research run is charged one trial against its campaign, keyed by campaign + version + run — never by
   attempt or role, so a team's replacement inherits the count; a run over a `fixture` dataset is kind `fixture`, charged nothing, never
   evidence; a campaign over budget refuses the next registration in words. RED: a thousand backtests of one version cost nothing. Mutant
   (the trial keyed by attempt): a restart resets the budget.
5. **The verdict budget exists and is enforced before anything is computed**: `Referee.RequestVerdict(version, campaign)` charges the campaign's
   verdict budget and refuses over it BEFORE the holdout run starts (every verdict leaks, `COUNCIL.md:134`); the verdict itself — the holdout
   run, the promotion record, the delivery — is `U-referee-2`. RED: no budget. Mutant (charged after the run): the leak has happened.

Not this unit: the promotion record, invalidation, forward evidence, the verdict's publication, the Situation and report lines (`U-referee-2`);
`TryAuthorizeExecution`, paper or live execution of a version, the allocator, the directors' sealed assessments.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, the choices made where COUNCIL is silent, what you did NOT do.
