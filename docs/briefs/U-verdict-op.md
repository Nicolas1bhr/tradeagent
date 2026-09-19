# U-verdict-op — the candidate → verdict arrow: an agent asks the app's referee to judge its own frozen version
**Arrow closed:** research candidate → bounded independent verdict (`docs/PRINCIPLES.md` § "Evidence supports iteration": models propose, software decides
admission and issues the verdict within the existing evidence budget). **Observable result:** a council-role caller on the pipe runs `trade verdict --version
<hash>` and receives the verdict and its reason class in words with no holdout figure; a `strategy_promotion` row exists that no developer wrote; the campaign's
verdict budget bounds it. No schema rung. NOT the money path: nothing here reaches `PlaceAsync`. `U-paper-verdict` (a parallel leg) owns `Referee.cs`,
`PromotionStore.cs`, `AllocationStore.cs`, `CampaignStore.cs` and `Database.cs` — do not edit those; call what exists.
Read first: `docs/PRINCIPLES.md` § Evidence; `docs/CONTRACTS.md` 2212-2352 (campaign, verdict); `docs/INSPECTION-2026-09-19.md` tables 2-3; `Referee.cs:119-290`;
the backtest op end to end as the pattern: `Protocol.cs:80-93`, `GatewayPipeServer.cs:333,1207,2264` (`BacktestFor`), `Backtests.cs:87-110,209-219` (`RoleOf`),
`Program.cs:223`, `GrantedWorkerTools.cs:96-110`, `GatewaySchema.cs:223`, `WorkspaceBuilder.cs:286`. Rebase onto `main` first; resolve conflicts yourself.
Items, one commit each with a one-sentence message:
1. `Ops.Verdict = "verdict"` in `Protocol.cs`, read-only for the gateway (NOT in `Mutating`; deadline `TimeSpan.Zero` like `Backtest`), documented like `Backtest`:
   it asks the app's referee for one of the final judgements a campaign budgets; it reads no bar, moves no cutoff, opens and renews no campaign, and the
   execution model is the judge's default. Args: `version` (the hash, required); `dataset` (optional — when absent, the ONE dataset this role has a completed
   research run of this version over; zero or several → refused in words asking for `--dataset`).
2. `VerdictFor(ctx, req, ct)` in `GatewayPipeServer.cs` beside `BacktestFor`. Admission, each refused in words: a known council role (the `Backtests.RoleOf`
   refusal; operator and roleless callers refused); an OPEN campaign for that dataset (`Campaigns.OpenForDataset`; none → name the owner's holdout press); a
   COMPLETED `strategy_run` of this version by this role over that dataset (its trial registered) — a version nobody measured buys no verdict, a losing one may;
   one verdict at a time in process, refused rather than queued, like the backtest. IDEMPOTENT: a promotion already recorded for (version, campaign) is answered
   as it stands and nothing runs, so a dataset that grew between two asks buys no second peek. Otherwise call the EXISTING `gateway.Referee.Verdict(version,
   campaign.Id)` unchanged. Reply `VerdictReply`: `version`, `campaign`, `verdict` (`promoted`/`refused`/other closed value/null), `reason` (the class), `text` =
   `RefereeFeedback.Text(promotion)`, `verdicts_spent`, `verdicts_budget`; a refusal to judge (`RefereeVerdict.Ok == false`) carries `why` and `verdict = null`.
   NO metric, no trace hash, no run figure and no bar crosses. Copy `verdict` through as the referee's own string, so a value `U-paper-verdict` adds passes untouched.
3. `trade verdict --version <hash> [--dataset <id>]` in `Program.cs`; `Ops.Verdict` in `GrantedWorkerTools.TradeOps` (every role, as `Backtest`); described in
   `GatewaySchema.cs` beside `backtest` and in the mission instructions (`WorkspaceBuilder.cs:286` region) in two lines: what it costs (one of the campaign's
   final judgements, counted across renewals, at most three by default) and what comes back (a verdict and a reason class, never a figure).
4. `docs/CONTRACTS.md` § campaign: "no pipe op and no `trade` verb reaches it" → "the verdict REQUEST is `Ops.Verdict`; the holdout, the campaign, the trial and
   the disposition still have none", naming the preserved property (the per-lineage verdict budget and the sanitised text bound "searching the holdout by
   asking"). `USER-GUIDE.md` one line beside `trade backtest`; `MissionInstructionsTests` updated only if the text is asserted.
Red-first tests (Integration, over the pipe; `TestEnv.Ready()` and the `VerdictOverPipeTests` fixture — a version frozen BEFORE the cutoff, one completed run):
- `A_council_role_asks_for_a_verdict_over_the_pipe_and_is_told_the_verdict_and_the_reason_and_no_figure` — RED today (unknown op); GREEN: `verdict = promoted`,
  `text` contains `PROMOTED version <12>`, the run's `NetPnl`/`GrossPnl` strings absent from the WHOLE reply JSON, a promotion row exists.
- `Asking_twice_after_the_dataset_grew_is_one_verdict_one_charge_and_no_second_run` — same promotion id, `verdicts_spent` unchanged, no new `referee` run row.
- `The_campaigns_verdict_budget_refuses_the_next_version_in_words` — three versions judged, the fourth answers `verdict = null` naming 3/3; no row.
- `A_version_this_role_never_ran_over_that_dataset_is_refused_in_words`; `A_roleless_or_operator_caller_gets_no_verdict` — no charge, no row, either way.
- `HoldoutOverPipeTests` extended with the new op: the cutoff row does not move; `data-bars` past the cutoff is still `HOLDOUT_WITHHELD` after a verdict.
Mutant to watch red and quote: `VerdictReply.text` built from the holdout run's metrics instead of `RefereeFeedback.Text` → the no-figure assertion goes red.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings, 0 errors; the three suites in Release to files → 0 failed; touched classes 3×; test
names vs `main` → 0 removed. Report ≤ 20 lines appended here as `## Report`: tip sha, gate counts pasted, one line per item, RED and mutant lines quoted, what
you did NOT do. Do not push, do not merge, touch nothing in `docs/briefs/` but this file.

## Report
Tip: `ff9b9ff`, the fourth item commit, with this report commit on top of it. Rebased onto `main` at `a617486`; 0 behind at the gate.
Gate: `dotnet clean` then `build TradeAgent.sln -c Release --no-incremental` → "Build succeeded. 0 Warning(s) 0 Error(s)", 17 projects compiled. Unit `Failed: 0, Passed: 1146, Skipped: 0`; Fault `Failed: 0, Passed: 374, Skipped: 0`;
Integration `Failed: 0, Passed: 677, Skipped: 1` (that skip is `[Fact(Skip=…)]` on `main`). Touched classes 3×: 19/19 Integration, 12/12 Unit, thrice each. Test names vs `main` 1855 → 1861, **0 removed**, 6 added.
1. DONE — `Ops.Verdict`, documented like `Backtest`, not in `Mutating`, `HandlerPaths` row at `TimeSpan.Zero`.
2. DONE — `VerdictFor` beside `BacktestFor`: `Backtests.RoleOf` (now `internal`, refusal unchanged), `Campaigns.OpenForDataset`, a COMPLETED run of that version by that role over that dataset whose trial is
   registered in the campaign's lineage, one at a time per role, idempotent on (version, campaign) — an existing promotion is answered as it stands and the referee is not called. New read
   `StrategyStore.CompletedRunsOf`; the reply is the eight fields and nothing else, `verdict` copied through as the referee's own string.
3. DONE — `trade verdict --version <hash> [--dataset <id>]`, `Ops.Verdict` in `TradeOps`, `GatewaySchema` beside `backtest`, two lines of mission instructions; `MissionInstructionsTests` asserts none of that
   text, so it is unchanged. 4. DONE — `CONTRACTS.md` § campaign rewritten (the request is reachable; holdout, campaign, trial and disposition still have none), naming the lineage-wide budget, the app's own
   months, scorer and execution model, and the sanitised text; `USER-GUIDE.md` one sentence beside `trade backtest`.
RED: `{"code":"INVALID_REQUEST","message":"unknown operation 'verdict'"}` — 4 of the 6 new tests; the two role tests were GREEN on the base for the wrong reason, so each now names its clause ("presented no
   launch grant" / "has never completed a run") and both were then re-run RED.
MUTANT: `text` from the run's metrics → `Assert.DoesNotContain() Failure: Sub-string found  String: ···"48, gross 48, trace 802c9c601482cc1e6e26e"···` at `VerdictOverPipeTests.cs:line 279`. Reverted; 6/6
   green after.
Deviations: (a) `PROMOTED version <12>` is in the owner's REPORT, not `RefereeFeedback.Text` (`U-paper-verdict`'s file) — asserted `text == RefereeFeedback.Text(promotion)` byte-for-byte instead.
   (b) `RefereeVerdictTests.No_pipe_op_asks_for_a_verdict_or_writes_a_promotion` banned any op NAMED "verdict"; it now asserts exactly one such op, that it is `Ops.Verdict`, non-mutating, args exactly
   {version, dataset}, the other six name bans intact, and the test's name kept. (c) I overwrote the pre-existing `VerdictOverPipeTests.cs` before noticing it; its test is restored verbatim and the three
   commits rebuilt, so no commit here is missing it.
NOT DONE: no test of the one-at-a-time refusal (it needs two concurrent callers); `RefereeFeedback.Text` still ends "You cannot ask for one: TradeAgent decides when a version is judged", now stale — `Referee.cs` is `U-paper-verdict`'s. No push, no merge, no box, no provider, no order anywhere real.
