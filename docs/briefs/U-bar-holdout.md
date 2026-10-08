# U-bar-holdout — no caller on the agent-facing pipe reads a bar from inside any holdout window, of any market, through another dataset or the forward door; the referee still does
**Arrow closed:** the evidence protection — `docs/COUNCIL.md`:150 "holdout data the research process cannot reach", :231 "a leaked holdout cannot become unseen"; CONTRACTS "The holdout". **Depends on
`U-language-v2a`, LANDED** (`78be3e9d`: `Backtest.Over` keeps a `BarAudience` and builds the feature feed's holdout with an internal `TapeHoldout.Of(audience, datasets)`, `TapeHoldout.cs:106`).
**Then:** `U-run-trace` (its refusal reads this unit's holdout). **Protects:** the holdout, kept for a dataset's own reads exactly as today; the referee's reads, untouched. **No schema change, no rung.**
**Today** (main `7a053887`, its code `78be3e9d`'s; every bar path below unchanged since the probe's `d73ecd59`): bars check THEIR OWN dataset only — `DatasetReader.Read` (`DatasetReader.cs:80-85`) and
`BarFeed.Open` (`BarFeed.cs:117-133`) ask `Holdout.Refusal(set, …)`, which reads `set.HoldoutFrom` alone — while the tape's `TapeHoldout` (`TapeHoldout.cs:114-170`) reads every window. Two datasets
of one pair are the normal case: every Download press records `v{n+1}` (`MarketDataService.cs:281` → `DatasetStore.cs:320-338`) with NO cutoff (`Record` never writes `holdout_from`, `:286-295`),
twelve months a month later sharing eleven with the last, and the owner's press cuts the newest dataset alone (`SettingsView.cs:858-876`). On the pipe: `data-bars` serves `Newest(pair)`
(`GatewayPipeServer.cs:2795-2813`); `backtest` runs over any `--dataset` id (`Backtests.cs:149-195`) and answers metrics and up to 20 trades with entry and exit prices, charged to no campaign when
that dataset has no cutoff (`:178`, `:569`); `data-bars --source forward` (`GatewayPipeServer.cs:2869-2909`) applies no holdout, on CONTRACTS' premise that every forward bar post-dates every freeze
(`CONTRACTS.md:2836-2843`; again for paper runs at `:4030`), which a Download taken after the forward collector ran breaks — `ForwardBarsOverPipeTests`' headline asserts that door serving held minutes.
**The probe** (`~/Projects/ai-trading-software-for-mihael-worktrees/fleet/tmp/s-bar-holdout-survey/SBarHoldoutProbe.cs`, its `NOTES.md` beside it; A = BTCUSDT 1m v1 cut at 01:00 by `TradingGateway.SetHoldout`;
B = v2, the same 120 minutes, no cutoff; C = ETHUSDT; RED on `d73ecd59`): `1 data-bars BTCUSDT [01:00, 01:59] as the Research Director: SERVED 60 bars from dataset 2` · `2 … a caller that proved no role: SERVED …` ·
`3 backtest --dataset 2 (B) over A's window: SERVED … trades=5 …; B has an open campaign: False` · `4 CONTROL backtest --dataset 1 (A): REFUSED HOLDOUT_WITHHELD` · `5 --source forward: SERVED 60 bars` · `6 ETHUSDT (C): SERVED 60 bars`.
**The rule (seat A's decision, 2026-10-08; stated in CONTRACTS):** every dataset holding a cutoff holds its window — `TapeHoldoutWindow`, unchanged: `[holdout_from, last_bar + one bar)` — over EVERY
subject, exactly as the tape does since U-tape-holdout: correlated pairs carry a held period's regime, and one rule for every data surface is one sentence to the owner. For an audience that may not read
the holdout, a read of ANY dataset (any pair) or of any forward bars whose market span — the open of the first bar it could serve to the close of the last — reaches ANOTHER dataset's window is REFUSED
in words naming that dataset, its cutoff and the window, never clipped; an absent bound reaches every window on its side; a dataset's own cutoff keeps today's rule (`Holdout.Refusal`, asked first,
unchanged). The referee's audience passes everything. So probe 6 (ETHUSDT over BTCUSDT's window) is refused too, as the tape refuses ETHUSDT's rows there.
**Not this unit** (seat A queues it as its own): a backtest over an un-held version charged to no campaign (`Backtests.cs:170`, `:561-565`); the Settings line after a second Download (`SettingsView.cs:896-905`).
**Observable result:** with A, B and C as above, `trade data bars --pair BTCUSDT --from <in A's window>`, the same for ETHUSDT, and `trade backtest --dataset <B> --from <in A's window>` answer
`HOLDOUT_WITHHELD` naming A's cutoff for both directors and a caller with none, and `--source forward` the same; a read wholly before A's cutoff, or from its window's close on, is served; a verdict over A is unchanged.
Read first: `CLAUDE.md`; `Holdout.cs`, `TapeHoldout.cs` (whole); `DatasetReader.cs:61-101`; `BarFeed.cs:98-136`; `Backtest.cs:741-830`; `GatewayPipeServer.cs:2766-2909`; `ForwardBarStore.cs:213-260`;
`TapeStore.cs:609-618` (a read that takes no holdout is never a pipe read); `HoldoutLedgerTests`, `TapeHoldoutTests`, `HoldoutOverPipeTests`, `ForwardBarsOverPipeTests`. Items, one commit each:
1. The bar readers take the holdout WITH the ledger as a REQUIRED argument in place of the bare `BarAudience` — `TapeHoldout` (`Pipe(role, datasets)` for the pipe, `Of` for `Backtest.Over`,
   `Referee` for the referee; a rename is the builder's call, stated in the report): inside `DatasetReader.Read` and `BarFeed.Open`, after `Holdout.Refusal(set, …)`, any other dataset's window, of
   any pair, reached by the read's market span refuses — `BarWindow([], false, why)`, `BarFeedOpen.Withheld(why)` — in `TapeHoldoutWindow.Words`' family. `Backtest.Over` builds ONE holdout and
   hands it to the bars and the features; `data-bars` and `backtest` answer a refusal as `HOLDOUT_WITHHELD`, as today.
2. The forward door: `ForwardBarStore` gains the pipe's read with the holdout required (`Window(holdout, symbol, from, to, cap)` beside `Since`, every window); `ForwardBars_` calls it; `Since`
   stays the paper runner's and says it is never a pipe read (the `TapeStore.cs:614` precedent).
3. Docs: CONTRACTS "The holdout" (the rule) and "Forward bars" (`:2836-2843`, `:4030`: the post-dates-every-freeze premise replaced by it), `GatewaySchema`'s `market_data`, `data-bars` and `backtest`
   texts, and `WorkspaceBuilder`'s history paragraph (:293-303): a cutoff on any dataset holds every pair's bars back over its window, from every dataset and the forward bars, as the tape's rows.
Tests: (a) `A_second_dataset_serves_no_bar_inside_anothers_holdout_window` (probe legs 1-4 and 6: `data-bars` for each director and a caller with none, over the pipe and through `CallAsync`,
ETHUSDT included, and `backtest --dataset B`; RED on the base, quoted); (b) `The_forward_door_serves_no_bar_inside_a_holdout_window` (leg 5; RED on the base); (c)
`A_read_wholly_outside_every_window_is_served_from_the_second_dataset` (before the cutoff, and from the window's close); (d) `A_bar_whose_span_crosses_into_a_window_is_refused` (a 5m dataset against a 1m
cutoff at :02); (e) `The_referee_reads_its_campaign_dataset_with_a_second_dataset_present` (`VerdictOverPipeTests` green unchanged); (f) `No_bar_reader_can_be_called_without_its_holdout` (reflection over
the public reads of `DatasetReader`, `BarFeed`, `ForwardBarStore`, `Since` the one named exception, as `TapeHoldoutTests` names the tape's). Rewritten, named in the report: `ForwardBarsOverPipeTests`' headline.
**Mutant**, quoted red: the cross-dataset check inside `BarFeed.Open` removed ⇒ (a)'s `backtest` leg. **Answer first:** does any in-process bar read but the referee's and the runner's need it?
Gate and report per `docs/HOW-WE-BUILD.md` pass 1 and `docs/FLEET.md` "The builder pass": rebase on `main` first; Release `--no-incremental` 0 warnings; Unit, Fault, Integration 0 failed, touched
classes 3×; CI via `fleet/bin/ci-dispatch.sh` (run id, each job); names 0 removed (both sizes); the tests box run or its NOT RUN line; `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
