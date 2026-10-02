# U-runner-forward — a paper deployment keeps acting after its first week, and its quotes are neither stale by phase nor stamped in the future
**Arrow closed:** deployment → forward intents → fills for as long as the deployment lives (`manager-prompt.md` § 5). **Today (SOURCE at `0f47db7`,
R09/R11-checked, NOT runtime-verified):** (1) the runner reads `_bars.Since(symbol, StartedAt)` once (`Gateway/ForwardRuns.cs:134`), limit 10,000 oldest-first
(`ForwardBarStore.cs:227-238`, `DatasetReader.cs:49`; `Since(…, openExclusive, limit)` can already page) — nothing after ~6.9 days is stepped. (2) The collector
waits a fixed `Tick` after each look (`Provisioning/ForwardBarCollector.cs:182-200`, `:197`), so a bar often lands > 30 s after its close and the runner's market
order is refused `MARKET_DATA_UNAVAILABLE`. (3) The paper quote is stamped `SettledThrough + Interval`, `Interval` being the last two bars' spacing
(`PaperConnector.cs:257-262`, `PaperBook.cs:171-175,186-187`): after a gap it is stamped IN THE FUTURE and a stale price passes. (4) `QuoteInfo.IsStale` reads the
machine clock (`ConnectorSdk/Contracts.cs:19`) at two order-path gates — `TradingGateway.cs:2736-2741` and `:2830-2832` — while the decision gate reads the
gateway's (`:6089`); the loss valuation computes its own `UtcNow - quote.At` (`:5141-5146`). (5) The health row (`RefreshHealthAsync`, `:9933-9936`) reads
"degraded" on a healthy bar feed; health does not gate execution (`Core/Health.cs:69-82`). **Observable result:** a deployment with 25,000 forward bars acts on bar
24,000; a quote after a gap is stamped at its bar's close and refused when stale; every order-path and valuation age check reads the gateway's clock.
**No schema change.**
Read first: `CLAUDE.md`; `ForwardRuns.cs` (whole; `RunBooks` takes the whole bar list `:136,556`); `ForwardBarStore.cs`; `ForwardBarCollector.cs`;
`ForwardBarCollectorTests.cs:30,49,136-186` (the 5 ms tick; the backoff test asserts strictly growing waits at `:164-166`); `PaperConnector.cs`; `PaperBook.cs:160-200`;
`ConnectorSdk/Contracts.cs:1-40`; `TradingGateway.cs:2590-2850,5130-5150,5425-6160,9900-9960`; `Connectors.Fake/FakeConnector.cs:58`; `OnboardingView.cs:440`;
`CoreTests.cs:409-410`; `App/SettingsView.cs:178-215`.
Items, one commit each, one-sentence messages:
1. The runner reads EVERY bar since `StartedAt` by paging `Since` until exhausted and still hands `RunBooks` every bar, keeping "no state a restart loses",
   the cursor frontier, write-ahead ops and "nothing planned past an unanswered op". Measure one pass over 50,000 bars on this Mac and quote it; CONTRACTS
   states the O(age) time AND memory cost.
2. The collector's next look on the SUCCESS path is at the next multiple of the injected `Tick` + `AlignOffset` (default 2 s); the failure backoff keeps its
   strictly growing waits exactly as today (the backoff test stands unchanged); closed-bar rule, first-reading-stands and gap rows unchanged. Put the
   alignment in one helper other collectors reuse.
3. The paper quote is stamped at the CLOSE of the bar it comes from. `IsStale` takes `now`; both order-path gates (`:2736-2741`, `:2830-2832`) and the loss
   valuation (`:5141-5146`) pass the gateway's clock — the one the decision gate reads; UI and onboarding call sites pass `UtcNow` explicitly. Money path:
   red-first and mutants.
4. The health row's market-data bound for a connector WITHOUT streaming quotes (key on the capability, not `IsPaper` — the practice simulator is `IsPaper`
   too) is `Tick + AlignOffset + 30 s`, stated on the row; the 30 s ORDER gates are unchanged. `CONTRACTS.md` (paging and its costs, alignment, the quote stamp,
   the one clock, the health bound); `USER-GUIDE.md` (Settings → Market data wording, if it changes).
Red-first tests (injected clock and timer; loopback listener; never the real host): (a) `A_deployment_acts_on_a_bar_after_the_first_ten_thousand`;
(b) `Max_hold_flattens_after_ten_thousand_bars_of_deployment_age`; (c) `The_collector_looks_align_offset_after_each_tick_boundary_when_healthy`; (d) `A_quote
_after_a_bar_gap_is_stamped_at_its_close_and_a_stale_price_is_refused`; (e) `Both_quote_gates_and_the_decision_gate_read_the_same_clock`; (f) `A_restart_mid
_run_resumes_from_the_cursor_without_a_duplicate_op_after_paging` (guard); (g) `A_bar_fed_feed_is_not_degraded_one_bar_after_its_close`. Mutants to watch
red and quote: (i) paging removed ⇒ (a) red; (ii) alignment removed ⇒ (c) red; (iii) the `+ Interval` stamp restored ⇒ (d) red; (iv) `:2830` back on the
machine clock ⇒ (e) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
