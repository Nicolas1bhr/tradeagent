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

## Report
Code tip `6ec443a` on `4ce671f` (rebased on `492ae79`, then `ace127f`, then `4ce671f` after U-cost-model): `91b4439` item 1, `486e995` item 2, `aad5a0a` item 3, `6ec443a` item 4, then this report. No schema change.
Gate at `6ec443a`: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 Warning(s), 0 Error(s) · Unit 1244/1244 · Fault 402/402 · 3×, each run: ForwardBarCollectorTests+ProtocolTests+ValuationLossSurfacesTests 18/18, QuoteClockTests+DecisionFreshnessTests+LossWatchTests+RiskGateTests+ValuationLossTests+ApprovalReauthorizationTests 64/64, ForwardRunnerTests 10/10 · Integration in full locally on items 1–3 before the last rebase: 702 passed, 1 skipped · names vs `main`: sets 1997 → 2006, removed 0, added 9 (8 tests + the rig's `Minutes` helper; [Fact] 1962 → 1970).
CI run 37033753438 at `6ec443a`: ubuntu-latest success 12 min · macos-latest success 15 min · windows-latest success 45 min · package success 4 min.
1 done: `EveryBarSince` pages `Since` from `StartedAt` on the last open time until a page comes back short and hands every bar to the replay and `RunBooks`; cursor, frontier, write-ahead ops unchanged. One pass over 50,000 bars (a temporary test under suite.sh, not committed): 98–281 ms over two programs (shipped ma-crossover; entry/exit on the close), 71.0–72.5 MiB allocated a pass, the bars held 28.0–28.5 MiB ≈ 590 B a bar (5 of 6 readings; one read −0.0, the previous fixture's garbage collected inside the window). This Mac was swapping heavily on 2026-10-02 (12.6 of 13.3 GB at 16:15 per the seat; 5.57 of 7.17 GB, load ≈ 4 at the run): an upper bound on this machine, not a property of the code. CONTRACTS states the O(age) time and memory cost.
2 done: the success path waits `TickAlignment.WaitForNextLook(now, Tick, AlignOffset)` — one public helper in Provisioning beside the collector; `ForwardBars.LookOffset` (2 s) lives in Core because the gateway needs it too; `Interval` is now `ForwardBars.BarLength`. The failure backoff and its test are unchanged.
3 done: the quote is stamped `SettledThrough + _source.BarLength`. DEVIATION: a bar's close needs its length and `KlineBar` carries only its open, so `IPaperBarSource` gains `BarLength` (forward ledger 1 min; `MemoryBarSource` 1 min, init-settable); `PaperBook.Interval` and its meta write are removed. `IsStale(maxAge, now)`: both gates and `MarkFor` pass `Now`, onboarding passes `DateTimeOffset.UtcNow`.
DEVIATION, fixtures (item 3): the one clock turned red 10 Fault + 2 Unit tests whose simulator quotes were stamped on the machine clock under an injected gateway clock (ValuationLossTests 5, ApprovalReauthorizationTests 2, LossWatchTests, RiskGateTests, DecisionFreshnessTests, ValuationLossSurfacesTests 2). Fix: `FakeConnector.QuoteClock` (default the system clock, so the shipped simulator is unchanged), `TestEnv.Ready` hands it `options.Clock`, four fixtures set it; DecisionFreshnessTests' mutant test widens its quote bound to 10 min, because its 5-minute clock move now also ages the risk check's price and the quote gate sits first. No assertion moved, nothing renamed or removed; the integration rig keeps its wide quote bound with its comment corrected.
4 done: `BarFedQuoteBound` = BarLength + LookOffset + MaxQuoteAge = 92 s when `!SupportsStreaming`, stated on the row; the simulator (IsPaper, streams) keeps 30 s; the order gates keep 30 s. CONTRACTS: the stamp (paper connector), the one clock (beside the dispatch-time quote rule), the health bound, alignment (Forward bars), paging and its cost (the runner). USER-GUIDE Market data: looks 2 s past each minute; the paper dot allows 92 s; orders still need 30 s.
RED before (base `492ae79` + the new tests): (a) and (f) `Assert.Single() Failure … Collection: []`, (a) logging "replayed 10000" of 10,005; (b) no flatten, "Position = Long … BarsSinceEntry = 9997"; (c) Expected [12:01:02, 12:02:02, 12:03:02] / Actual [12:01:37.5, 12:02:37.5, 12:03:37.5]; (d) Expected 15:36:00 / Actual 15:45:00; (e) "MARKET_DATA_UNAVAILABLE — no price newer than 30s for ES" where "ok" was expected; (g) DEGRADED on base, and on the item-3 tree "60 s after the close : DEGRADED — last price is older than 30s".
Mutants, each alone, restored identical and rebuilt: (i) paging removed → (a) `Assert.Single() … Collection: []`; (ii) alignment removed → (c) Actual [12:01:37.5, 12:02:37.5, 12:03:37.5]; (iii) the `+ Interval` stamp and its bookkeeping restored → (d) Expected 16:01:00 / Actual 16:10:00; (iv) `:2830` on `DateTimeOffset.UtcNow` → (e) "MARKET_DATA_UNAVAILABLE …" where "ok" was expected.
NOT done: the paper connector's own settle still reads one 10,000-bar page per call, so a fresh book over an older ledger catches up over a few reads and refuses its stale price meanwhile (outside the brief; untouched). NOT verified: the app was not run (the Settings card and the Dashboard dot unseen); the item commits were not built one by one (the tip was); no Windows box; no order placed anywhere.
