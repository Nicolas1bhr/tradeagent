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
