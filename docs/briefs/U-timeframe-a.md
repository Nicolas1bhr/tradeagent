# U-timeframe-a — a program declares the bar it is evaluated on; backtests and the referee judge it on hours and days, not on fee-eating minutes
**Arrow closed:** candidate → a verdict a cost-paying strategy can pass (`docs/EDGE-FACTORY.md` § 4.4; R10 #2). **Depends on `U-evidence-identity`** (its golden
vectors prove v1 programs unchanged). **Today (SOURCE at `0f47db7`, R11-checked):** every program steps on every closed minute — `EvaluationState.Start`
defaults to 1 minute (`StrategyEvaluator.cs:142-156`, `:145`), `Backtest.Over` passes `null` (`Backtest.cs:593`); lookback ≤ 500 and history ≤ 20 bars
(`StrategyLimits.cs:64,71`) ⇒ ~8.3 h of context; a run halts at `MaxTracedBars = 200_000` (`Backtest.cs:328`) ≈ 139 days; `timeframe` is parsed
(`StrategyParser.cs:96,488-491`) and hashed (`StrategyCanonical.cs:50`) but never resamples. HIST 2026-10-01: attempt 2's model-authored version made 321
trades in a quarter and fees were 62% of its loss. **Observable result:** a program declaring `bars 1h` is backtested and judged on hourly bars built from
closed minutes, with indicators, lookback, history, `max_hold_bars` and the run cap counting hours; a year runs in one run; fills, stops and targets keep their
1-minute mechanics exactly; a v1 program's text, id and golden vector are unchanged; the paper runner REFUSES, in words, to deploy a program whose `bars`
is not `1m` until `U-timeframe-b`. **No schema change.**
Read first: `docs/STRATEGY-LANGUAGE.md` (`:17,32` the `timeframe` bound); `CLAUDE.md`; `StrategyParser.cs`; `StrategyCanonical.cs:30-110` (Header `program/1` `:32`,
"ALWAYS STATED" bounds `:43-47`, `IsKeyword` `:94-101`, `IsReserved` `:104-107`); `StrategyProgram.cs:50`; `StrategyEvaluator.cs:140-160,260-285` (`MissingMinutes`);
`StrategyLimits.cs`; `Backtest.cs:360-600` (`Run`: fill pending `:393-428`, protection `:430-446`, `held` `:433`, `BarsSinceEntry` `:457`, `Step` `:459-498`, cap
`:377`, `Closed` trace `:464`); `DatasetReader.cs:7-25` (`KlineBar`); `ForwardRuns.cs:110-130`; `AgentRuntime/WorkspaceBuilder.cs:281-330`.
Items, one commit each, one-sentence messages:
1. Grammar: `bars DURATION` (1m 5m 15m 30m 1h 4h 1d), recognised ONLY as a line-leading declaration and NOT added to the reserved names (a stored program with
   `const bars = …` must keep parsing). Canonical form: absent ⇒ `bars 1m`, and the `bars` line is written ONLY when declared and not `1m` — the one stated
   exception to "always stated", keeping every v1 text and id identical; `Header` unchanged. `bars` and `timeframe` are independent; the docs say how they relate.
2. Resampling (one shared, deterministic, decimal resampler): a declared bar covers [k·d, (k+1)·d) aligned to UTC (to the program's `timezone` midnight for `1d`);
   O first open, H/L extremes, C last close, V sum; it closes when its last minute has closed; an empty window is a gap (never filled); a partial window is a bar
   whose minute count rides on a new, additive `KlineBar` init property. `MissingMinutes` keeps counting MINUTES.
3. `Backtest.Run` becomes a two-clock loop: the minute loop still fills pending orders at the next minute's open and applies stops/targets per minute exactly as
   today; `Step` runs only at a declared close, on the resampled bar; `held` and `BarsSinceEntry` count declared bars; the cap counts EVALUATED bars; the trace
   writes one `Closed` event per evaluated bar plus fill and protection events (never one per minute). `Backtest.Over` passes the program's declared interval,
   so the referee judges what was declared.
4. Limits count declared bars (500 hourly bars ≈ 21 days). `ForwardRuns` refuses to start, and ends in words, a deployment whose program declares `bars` other
   than `1m` ("this build's paper runner evaluates every minute; programs on hourly bars run after the next update"). `docs/STRATEGY-LANGUAGE.md`, the agents'
   language section in `WorkspaceBuilder` (that `bars` exists and why minute-scale turnover dies on fees), `CONTRACTS.md`.
Red-first tests (red on base: `bars` does not parse): (a) `A_bars_1h_program_is_backtested_on_hourly_bars_built_from_closed_minutes`; (b) `A_partial_hour_carries
_its_minute_count_and_an_empty_hour_is_a_gap`; (c) `A_decision_at_an_hourly_close_fills_at_the_next_minute_open_and_a_stop_still_fills_on_minutes`; (d) `A_year_of
_hourly_bars_backtests_in_one_run`; (e) `A_program_without_bars_keeps_its_id_and_its_golden_vector` (guard); (f) `A_stored_program_with_a_const_named_bars_still
_parses` (guard); (g) `The_paper_runner_refuses_a_bars_1h_deployment_in_words`. Mutants to watch red and quote: (i) resampling bypassed ⇒ (a) red; (ii) the cap
counting minutes ⇒ (d) red; (iii) `bars` added to the reserved names ⇒ (f) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Tip `389950b`** (6 commits on `7ceb7c9`; `main` since moved one docs-only commit). No schema change. Gate: `--no-incremental` Release build `0 Warning(s)` `0 Error(s)`;
Unit `Passed: 1325, Failed: 0`; Fault `Passed: 408, Failed: 0`; 3× each, all green: DeclaredBarsGrammarTests 25, DeclaredBarsGuardTests 2, DeclaredBarsResamplerTests 14,
DeclaredBarsBacktestTests 9, DeclaredBarsPaperRunnerTests (Fault) 3. **CI run 37085481666** on 389950b: success — windows-latest 36 min, ubuntu-latest 12, macos-latest 15,
package 4. Names vs `main`: `sets: base 2044 tip 2075`, `removed: 0`, `added: 31`.
1 done: `bars` is a declaration only as a line's first word, never reserved; the canonical form writes `bars <s>s` only when declared and not 1m, `Header` and manifest unmoved —
  so `bars` rides with no manifest bump and every v1 text, id and golden vector is unchanged (guard (e) runs the golden vectors' own check).
2 done: one decimal `BarResampler` on `BarGrid` (UTC; 1d at the zone's midnight, 23/25 h on DST days); partial count on `KlineBar.Minutes`; empty window = gap; `MissingMinutes` stays minutes.
3 done: two-clock `Backtest.Run` (each minute: fill at its open, stop/target on its range; declared close: max hold, then `Step`); one `Bar` line per evaluated bar; the cap counts
  evaluated bars (a year of 1h is one run). DEVIATION: no bar parameter — `Run` reads `program.Bars` (`barInterval` removed) and `EvaluationState.Start` refuses any other bar.
  JUDGEMENT: a run that ends inside a declared bar closes it as a partial bar (history: its minutes happened, the data lacks them).
4 done: limits in declared bars (test: 500 h ≈ 20.8 d); `ForwardRuns.Refuses` ends the deployment before its first bar in the brief's words; STRATEGY-LANGUAGE.md, the agents'
  bullet (away from :314), CONTRACTS.md. ADDITION (money path): `StartPaperDeploymentsDue` starts no replacement for a version the runner refuses (else: a row, a flatten and a
  paid Research wake per sweep). RED before, on base `d99155e` (code under test identical to `7ceb7c9`): (a) (c) (d), cap, max-hold, lookback, partial, Over, (e), (g) and the
  no-replacement test fail at "`bars` is not a declaration this language has"; (f), 1m cap, 1m replacement green; (b) and the grammar/resampler classes do not compile there.
Mutants, each red then reverted: (i) resampler bypassed ⇒ (a) `Expected: COMPLETED Actual: FAULTED` ("does not open on the 1h grid"); (ii) cap counting minutes ⇒ (d) FAULTED at
  minute 200,001; (iii) `bars` reserved ⇒ (f) "`bars` already means something in this language"; refusal removed ⇒ (g) `Assert.Single() Failure: The collection was empty`;
  sweep guard removed ⇒ `Expected: [0, 0, 0] Actual: [1, 1, 1]`. NOT done / NOT VERIFIED: the runner stepping declared bars (U-timeframe-b); Integration only on CI; no box run;
  the pipe's backtest reply does not name the bar; a partial bar's shortfall counts window minutes outside `from`/the data's end (stated in docs); on declared bars a minute out
  of order is refused before its fills, unlike 1m (unchanged).
