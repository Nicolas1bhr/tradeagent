# U-timeframe-b — the paper runner evaluates a program on its declared bars while protection still acts within the minute
**Arrow closed:** an hourly-bar program runs forward on paper (`docs/EDGE-FACTORY.md` § 4.4, § 4.6). **Depends on `U-timeframe-a`** (grammar and resampler) and `U-runner-forward`
(paging). Money path: the runner places orders through the gateway. **Today (SOURCE at `0f47db7`, R11-checked):** `ForwardRuns.AdvanceOneAsync` (`Gateway/ForwardRuns.cs:156-294`) does,
once per stepped bar: settle answered ops; cancel the losing protective order after an exit, stop or target fill (`:202-211`); place stop + target when an entry fills (`:213-226`,
`ProtectAsync` `:361-385`); the max-hold flatten (`:240-250`); then `Step`. Stops and targets rest at the venue and the paper connector fills them per minute
(`Connectors.Paper/PaperConnector.cs:512-530`). R11's warning, which this unit exists to honour: if the runner simply stepped once per declared hour, a filled entry would sit UNPROTECTED
for up to an hour, and a leftover protective sell under no position would OPEN A PAPER SHORT when it fills (`PaperBook.ApplyToPosition`, `:483`, turns `held = 0` into `-qty`). After
`U-timeframe-a` the runner refuses `bars` ≠ `1m`.
**Observable result:** a `bars 1h` deployment's rules are evaluated once per hour on the resampled bar, while protection is placed, cancelled and enforced within one minute of the fill
that requires it, exactly as on a `1m` program; the refusal from `U-timeframe-a` is removed. **No schema change.**
Read first: `CLAUDE.md`; `ForwardRuns.cs` (whole); the resampler `U-timeframe-a` landed; `PaperConnector.cs:480-560`; `PaperBook.cs:470-500`; `TradingGateway.cs:5425-6160` (dispatch;
change nothing there); the runner's existing tests (crash-after-acceptance, replay, protection).
Items, one commit each, one-sentence messages:
1. Keep the per-minute pass for everything that protects: settle ops, cancel the losing protective order, place protection on an entry fill, the max-hold flatten (now counting declared
   bars), the day note. Hold back ONLY `Step` until a declared bar closes, and feed it that resampled bar.
2. The deployment's state rebuild on restart replays minutes into the same resampler, so a restart mid-hour resumes with the same evaluator state and without a duplicate op; the paging
   of `U-runner-forward` is kept.
3. Remove `U-timeframe-a`'s refusal (`ForwardRuns.Refuses`, called at `ForwardRuns.cs:128-129`) AND its sweep guard in `StartPaperDeploymentsDue` (`TradingGateway.cs:660-664`,
   `RunnerRefuses` `:685-692`) together — the orchestrator's amendment at dispatch: the sweep must still never churn a version the runner cannot run for ANY other sticky reason (a frozen
   text that no longer parses or re-identifies), each such sweep a row, a flatten and a paid Research wake; `CONTRACTS.md` (what runs per minute and what per declared bar),
   `USER-GUIDE.md` if the paper card shows the bar.
Red-first tests (money path; injected clock; the paper connector over loopback bars): (a) `A_bars_1h_deployment_steps_its_rules_once_per_hour`; (b)
`Protection_is_placed_within_one_minute_of_the_entry_fill` (red against a once-per-hour pass); (c) `The_losing_protective_order_is_cancelled_within_one_minute_and_no_paper_short_opens`;
(d) `Max_hold_counts_declared_bars`; (e) `A_restart_mid_hour_resumes_with_the_same_state_and_no_duplicate_op` (guard); (f) `A_bars_1m_deployment_behaves_exactly_as_before` (guard); (g)
`The_sweep_never_starts_a_replacement_for_a_version_the_runner_cannot_run` (a version whose frozen text no longer re-identifies; green if today's guard already covers it, else red
first). Mutants to watch red and quote: (i) protection placement moved to declared closes ⇒ (b) red; (ii) the cancel moved to declared closes ⇒ (c) red; (iii) the sweep's sticky-refusal
check removed ⇒ (g) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Tip `22d83f47`** — the code tip CI tested, on `main` 35606c56 (`main` since moved by docs-only a68b14b4, no file in common). Item 1 `c6ebeaf5`, item 2 `b0b1309a`, item 3 `22d83f47`.
**Gate.** `dotnet build TradeAgent.sln -c Release --no-incremental`: 0 Warning(s), 0 Error(s). Unit: Passed 1333, Failed 0. Fault: Passed 415, Failed 0. 3× each: `ForwardRunnerTests` 16/16,
`DeclaredBarsPaperRunnerTests` 4/4, `DeclaredBarsResamplerTests` 15/15. **CI run 37102232929** on 22d83f4: success — test (ubuntu-latest) success 12 min, test (macos-latest) success
15 min, test (windows-latest) success 46 min, package success 3 min. **Names** (`names.sh main u-timeframe-b`): sets base 2090, tip 2097; **removed 2**, added 9. Removed, declared: both pinned the
refusal this unit removes — `The_paper_runner_refuses_a_bars_1h_deployment_in_words` (superseded by (a)) and `A_run_the_runner_refused_for_its_bars_is_not_replaced_on_the_next_sweep` (kept by (g)).
1. DONE, one deviation: settle, the loser's cancel, protection on an entry fill and the max hold (counted in declared bars, `BarGrid.Between`) run every minute; only `Step` waits for a declared close,
   on the resampled bar, its ops keyed on the minute that closed it. U-timeframe-a's refusal AND its sweep guard went in THIS commit, together: item 1 cannot be observed behind the refusal, and the
   amendment forbids one without the other. Also the runner's two no-trade sentences write decimals invariantly (manager's note). `TradingGateway`'s dispatch region untouched.
2. DONE, no code beyond item 1: each pass builds its own resampler from the deployment's start over every page (`EveryBarSince` kept); (e) restarts mid-hour past 10,000 minutes and equals an uninterrupted control.
3. DONE: the sweep now asks `ForwardRuns.CannotRun` (no row, a frozen text that no longer parses or re-identifies — the runner's own pre-step question) before any replacement, (g) a 2-case [Theory];
   CONTRACTS.md (per minute vs per declared bar), STRATEGY-LANGUAGE.md, the agents' bars bullet. USER-GUIDE.md unchanged: the paper card's line shows no bar.
**RED before** (base 3dff4294): (a)(b)(c)(d) `Assert.Null() Failure: Value is not null … Actual: "this program declares `bars 1h`, and this build's "···`; (g) `Assert.Equal() Failure: Collections
differ Expected: [0, 0, 0] Actual: [1, 1, 1]`, against main's bars guard and against none. (e)(f) guards: (f)'s golden transcript was captured on the base runner and is green there.
**Mutants watched red:** (i) protection only on declared closes ⇒ (b) `Values differ Expected: 2 Actual: 0`; (ii) cancel only on declared closes ⇒ (c) `Values differ Expected: 0 Actual: -1.000`
(a paper short); (iii) the sweep's check removed ⇒ (g) `Expected: [0, 0, 0] Actual: [1, 1, 1]`; and the hold counted in minutes ⇒ (d) `Assert.DoesNotContain() Failure`.
**Not done / not verified:** no Windows box run (CI windows-latest only); Integration run locally only for `ForwardRunnerTests`. Not guarded: a version with no execution bounds (ended at its first
intent, not before its first bar; guarding it contradicts `PaperDeploymentTests`). A run's first declared bar is the partial window it saw from its start, as documented.
**Found, not fixed (pre-existing, 1m too):** a gate-refused close keeps its request row `CREATED` and `RunBooks` counts it in flight — (f) pins it (minute 11, `pending=True` after); "the run never enters
again" is read from the code, not run. By reading only, not reproduced: a stop/target fill first seen after its minute stopped being live leaves the loser resting. Each wants its own unit.
