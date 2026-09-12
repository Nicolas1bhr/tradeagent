# U-runner-3 — the backtest: a declared execution model on bars, a trace the app computes metrics from, a persisted version and run, the report
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:126-166` (rule 8; metrics computed by the app from its own trace; a backtest declares
fees, slippage, gaps and conservative stop-versus-target ordering), then `Core/Strategy/` (`U-runner-1`, `-2`), `Versioning.cs:97` and `Db/Database.cs:
103-590` (schema 11, the migration ladder), `Db/DatasetStore.cs:56-80`, `Gateway/DailyReports.cs:400-433` and `DailyReport.cs:399-402` (section 8, its
"nothing has been backtested" gap), `Core/Trading.cs:26,34-36`, `FakeBroker.cs:86-118` (a tick grid, NOT a fill model), `GatewaySchema.cs`, `GatewayPipeServer.cs:
924-925,1952-1993` (the read-only data ops — the shape of a new op), `TradeCli/Program.cs:192-201`. Branch `u-runner-3`, worktree `~/Projects/ai-trading-
software-for-mihael-worktrees/U-runner-3`, rebased onto `main` AFTER `U-runner-2` lands. Every item ships with a test RED before it and ONE mutant,
quoted. No box, no ATAS, no money, no orders. **Schema 12.**

**Why.** Nothing measures a strategy and the report says so (`DailyReports.cs:425-427`). A version has no identity beyond a file
in `strategies/`, a run has no trace, and the referee (`U-referee`) needs lineage by hash, a declared execution model and metrics
the app computed itself, never a claim. The agent's only route to the runner must be a request the app fulfils and records.

1. **Three app-owned tables at schema 12, additive:** `strategy_version` (id = `StrategyId`, source, canonical form, manifest, interpreter
   build = app version + language version, parse verdict, warm-up, created_at, role and attempt when known); `strategy_run` (id = hash of
   version id + dataset id + the dataset's normalised sha256 + window + execution model; outcome, counts, created_at); `strategy_trade`
   (run id, entry and exit bar times and prices, quantity, exit reason, pnl). Written by the app only; the ladder and the `Versioning.cs`
   paragraph as the others. RED: a program is only a file. Mutant (the version id minted from the attempt): every restart a new version.
2. **The declared execution model:** an intent from bar n fills at bar n+1's open plus declared slippage; declared fees per fill; sizing
   to a quantity rounded DOWN to the run's declared increment (the dataset carries none — stated), zero = no trade with the reason; stops,
   targets and maximum holding bars checked on every later bar with CONSERVATIVE ordering (a bar touching both counts the stop);
   protection is the backtest's bookkeeping. RED: a fill at the signal bar's own close. Mutant (target before stop): the fixture's pnl changes.
3. **The trace and the metrics:** every fill, exit and evaluation outcome in the trace; metrics computed FROM the trace only —
   trades, win rate, net pnl after fees, max drawdown on equity including the open trade, exposure bars, gap and fault counts;
   every unknown a labelled dash. RED: drawdown from closed trades only → from equity. Mutant (a metric read from the program's
   text): red.
4. **Deterministic and refusable:** the same version, dataset sha, window and model → a byte-identical trace and the same run id
   (no clock read inside a run); a changed fee → a different run id; a `REJECTED` dataset refused; the dataset's sha at run time
   recorded so a later rejection can be traced to every run it fed. RED: two runs of one input differ. Mutant (the dataset sha
   left out of the run id): red.
5. **The request and the report.** `trade backtest --strategy <file in the role's own home> --dataset <id> [--from] [--to] [--fees] [--slippage]
   [--increment] [--capital]`: a new pipe op, non-mutating for the gateway (no order, no authority), one run at a time per role; the app parses
   the program (a refusal names the line), records the version and the run, answers the run id and the metrics; section 8 lists runs "measured
   by TradeAgent" and its gap line disappears only when a run exists. RED: the gap line stands with a completed run. Mutant (an agent's claimed
   metric listed as measured): red. The guide and `CONTRACTS.md` say what a backtest cannot prove (no fill, queue or intrabar evidence).
Not this unit: live or paper execution of a version, app-owned protection between evaluations (`U-flatten`), the relay's
publication kind for programs, the referee, holdout data, promotion, trial budgets.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0
failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
