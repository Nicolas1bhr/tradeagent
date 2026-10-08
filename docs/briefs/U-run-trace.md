# U-run-trace — a read-only verb serves every closed trade of a completed research run, in bounded pages, to any role; never the referee's holdout run, never a run a cutoff now holds back
**Arrow closed:** "receive fills, P&L, costs and evidence → keep / modify / kill / branch" (`docs/PRINCIPLES.md` § The loop). Attempt 3's Research, asked by Operations for an exit-cause attribution of two
runs: "No retained per-trade rows or read-only command to retrieve a completed run's trace were available" (`BUILD-STATUS.md` at `cc7a0974`, gap (a); `fleet/records/U-observed-loop.part:74-75`).
**Depends on `U-language-v2a` (`78be3e9d`) and `U-bar-holdout` (`72bcd8dd`), both LANDED** (1(b) and 1(c) are their refusals; seat A re-checked this brief at `72bcd8dd`). **Then:** a paper run's per-trade view (`EDGE-FACTORY.md:225`, "forward
figures are theirs"); a deterministic trace replay served only when it re-hashes to the row's `trace_sha256`. **Protects:** the bars' and the tape's holdout; verdict-only evidence (`EDGE-FACTORY.md:225`,
"Agents see verdict and reason class only"); no op writes a run or a trade (`Protocol.cs:101-103`).
**Today** (main `72bcd8dd`): every closed trade of every run IS kept — `strategy_trade` (`Database.cs:709-720`, rung 12), written by `StrategyStore.RecordRun` (`StrategyStore.cs:324-364`) for a research
run (`Backtests.cs:540`) AND for the referee's holdout run, role `referee` (`Referee.cs:105`, `:422-430`; window `HoldoutFrom` to no end, `:352`). `TradesOf` (`StrategyStore.cs:431-449`) reads them and
nothing in `src/` calls it. The per-bar trace is not kept, only its hash (`BacktestTrace.cs:220`, "It is not persisted"). An agent sees a run's trades once, in its own `backtest` answer: the first
`TradesShown` = 20 (`Backtests.cs:77`; `GatewayPipeServer.cs:2368`) beside `trade_count`, while the schema says "and the closed trades" (`GatewaySchema.cs:362`) with no 20. After that, `trade report`
names the 6 newest runs with figures and no trades (`DailyReports.cs:86`, `:605-625`). So attempt 3's trades 21-103, 21-94 and 21-65 were served to no one, and no role can read another role's trades.
**Observable result:** `trade run trades --run <id> [--after <ordinal>] [--limit <n>]` (op `run-trades`) answers the run's row as recorded (id, version, role, dataset and its sha, window, execution model,
increment and friction source, outcome, fault, the row's figures, `trace_sha256`, `trade_count`) and its closed trades in ordinal order: at most 1,000 rows and 256 KiB of rows a call, a limit outside
1..1,000 refused in words, never clamped; an answer a bound stopped says `more`, `capped_by` and `next_after` (`data-tape`'s pattern: `GatewayPipeServer.cs:2942`, `:3024`, `:3136`). Every
caller, any role or none, any research run — reviewing the other role's run is the case that asked; reading grants nothing. Nothing re-runs, so no trial and no verdict is charged. **No schema change** (main at 30).
Read first: `CLAUDE.md`; `Holdout.cs:30-140`; `TapeHoldout.cs` (whole); `Referee.cs:1-120`, `:400-440`; `HoldoutOverPipeTests.cs:160-560`; `BarHoldoutOverPipeTests.cs`. Items, one commit each, one-sentence messages:
1. The reader, with the audience INSIDE it (`Holdout.cs`'s rule: a required argument, the check inside the reader): `StrategyStore.ReadTrades(string run, TapeHoldout holdout, long after, int limit,
   long maxBytes, Func<StrategyTradeRow, long> size)` (U-bar-holdout's convention: the holdout WITH its ledger) → a page or a refusal in words. For an audience that may not read the holdout it refuses (a) a row whose role is `Referee.RunRole` or whose id is any
   promotion's `holdout_run_id` — words naming it the referee's holdout run and `trade verdict` as what serves its verdict and reason class, and NO figure, count, trace hash or instant of it; (b) a run
   whose recorded dataset and window `holdout.Refusal(set, from, to)` (`TapeHoldout.cs:227`: its own cutoff, then EVERY dataset's window, any pair) refuses READ AFRESH (a cutoff set after the run counts; a dataset gone from the ledger refuses); (c) a version that reads
   features, where v2a's tape-holdout check refuses the run's feature read window — exactly what `Backtest.Over` would refuse today. The id is the full run id or a unique prefix of 12 or more (the report's
   `Short`, `DailyReports.cs:602`); unknown, ambiguous or shorter is refused naming what was asked. `TradesOf` stays as the in-process reader (4 tests call it).
2. The op: `Ops.RunTrades = "run-trades"` in `Protocol.cs` (a READ, not in `Mutating`, its summary saying so), its arm in the dispatcher (`GatewayPipeServer.cs:1210-1250`; refusals `HOLDOUT_WITHHELD` and
   `INVALID_REQUEST`), its drain row `new(Core.Ops.RunTrades, TimeSpan.Zero, "the strategy ledger's run row and its trades, in process")` (`:308-353`), its `GatewaySchema` entry (a row is a closed trade the
   app recorded; a position open at the last bar is none; the trace is not kept and its hash is; never a record of a fill), `GrantedWorkerTools.TradeOps` (`:111-117`), and `trade run trades` in
   `TradeCli/Program.cs` (`:169-275`'s shape).
3. The wording: the backtest's schema text (`GatewaySchema.cs:362`) and reply note say the first 20 closed trades, that `trade_count` counts them all, and that `trade run trades --run <run_id>` serves every one;
   and (light, folded by seat A) `HOLDOUT_WITHHELD`'s owner text (`Errors.cs:797`, "The bars you held back") names what is held back now: every pair's bars, the tape's rows and a run's trades.
**Kept:** `BarAudience.Referee` stays internal (`HoldoutLedgerTests`'s door list unchanged); `trade verdict` still answers verdict, reason class and note only (`GatewaySchema.cs:376`); the report's holdout line
still names the run and values nothing (`DailyReports.cs:621-625`); no op edits or deletes a run or a trade; the backtest answer's 20 and its fields do not move (v2a's `features` included).
Tests: (a) `Every_closed_trade_of_a_research_run_is_served_in_pages` (fixture dataset, more than 20 trades: the backtest answers 20; the op serves all N equal to `TradesOf`, across `next_after`, by a role
that did not run it); (b) `The_referees_holdout_run_is_refused_with_nothing_of_it` (after a verdict; by full id and by 12-prefix; Research and roleless: `HOLDOUT_WITHHELD`, no instant at or after the
cutoff, no figure and no `trace_sha256` of that run anywhere in the reply); (c) `A_cutoff_set_after_the_run_withholds_it` (on its own dataset AND on another dataset of another pair; a run ending before the new window is still served); (d)
`Limits_and_ids_are_refused_in_words_never_clamped` (limit 0, 1001, 2.5; unknown; ambiguous; 11 characters); (e) `HoldoutOverPipeTests`: `Held` (`:358-375`) also reads `entry_bar` and `exit_bar`, and both
every-op sweeps (`:330-356`, `:474-515`) pass `run` = a referee holdout run recorded in their fixture (a verdict first, as `:517-560` does) — red with 1(a) removed; (f)
`Every_operation_the_dispatcher_handles_has_a_row_in_the_drain_table` (`GatewayPipeBackpressureTests.cs:1083`) and the CLI parse green. Mutants, quoted red: 1(a)'s role clause dropped ⇒ (b), (e);
the dataset read once instead of afresh ⇒ (c).
First, in the report: the byte size of a 1,000-row page of attempt 3's shape (1h bars, 103 trades) measured, so the two caps are figures and not guesses.
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main`; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed, touched classes 3×;
CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines; no push to `main`.
