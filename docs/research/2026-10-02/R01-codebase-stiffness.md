# 01 — Where TradeAgent is stiff, and where its seams are (read-only survey)

HEAD `0f47db7f8a089ec8de75750b7e86c69f03907012` (`main`, clean), 2026-10-02. Nothing under `src/`, `tests/` or `docs/` was
changed; nothing was built, tested or launched. **Labels:** every `path:line` is **SOURCE**, read in the tree at that sha.
Paths are abbreviated: `Core/Data/BarFeed.cs` = `src/TradeAgent.Core/Data/BarFeed.cs` (likewise `Gateway/`, `App/`,
`AgentRuntime/`, `ConnectorSdk/`, `Connectors.Paper/`, `Platforms/`, `Provisioning/`). **HIST(date)** = taken from
`BUILD-STATUS.md` / `docs/RESUME-HERE.md`. A sentence about what code *would do* is an inference from reading — **NOT
VERIFIED** at runtime. Product constraints read first: `docs/PRINCIPLES.md` (language kept "until a demonstrated strategy
need", :69; broader languages / venues / copy trading "not the next priority", :102) and the narrowness it inherits from
`docs/COUNCIL.md`:150-170 ("Never: … model calls, training, multi-instrument, shorting, leverage", :163-164).

## 0. Latent blockers met while reading (SOURCE inference, NOT runtime-verified)

Not design stiffness: defects that would stop today's paper loop from producing evidence before any redesign.
1. **The referee cannot trade BTCUSDT.** `trade verdict` calls `Referee.Verdict` with no model (`Gateway/GatewayPipeServer.cs:2496`),
   so the judge runs `ExecutionModel.Frictionless` = fee 0, slippage 0, **increment 1, capital 10,000** (`Core/Strategy/Referee.cs:218`,
   `Core/Strategy/Backtest.cs:76`). Every size < 1 rounds to 0 (`Backtest.cs:487-495`); ≥ 1 BTC costs more than 10,000
   (`:401`) ⇒ 0 trades ⇒ `no-trade-on-the-holdout` (`Referee.cs:549`). HIST(2026-10-01) run figures imply ~112k USD/BTC
   (fees 3,597.03 over 321 trades at 0.05 and 0.001). The charge is taken before the run (`Referee.cs:137-143,186`), so each such
   verdict burns 1 of the lineage's 3. Tests pass because fixture bars sit at 96–105 (`tests/TradeAgent.UnitTests/PaperEligibleVerdictTests.cs:55-60`).
2. **A paper deployment stops seeing new bars after ~6.9 days.** The runner replays `_bars.Since(symbol, StartedAt)`
   (`Gateway/ForwardRuns.cs:134`) whose default limit is 10,000 rows, oldest first (`Core/Db/ForwardBarStore.cs:227-238`);
   nothing pages. After 10,000 minutes no bar is "live" (`ForwardRuns.cs:196-197`): no rule exit, no max-hold flatten, no day note.
3. **Out of the box nothing can be paper-traded.** BTCUSDT ships `Verified = false` (`Core/Data/VenueCatalog.cs:181-186`); the
   paper connector offers only verified rows (`Connectors.Paper/PaperConnector.cs:181-215`) and the runner sizes nothing without
   one (`ForwardRuns.cs:492-516`). Only a hand-edited `venues.json` fixes it; no file in `App/` writes one (grep).
4. **Every app release withdraws every verdict and ends every paper run.** Interpreter build = `app=<assembly version>;language=1`
   (`Core/Db/StrategyStore.cs:213-214`, `Core/Versioning.cs:287-289`); a mismatch invalidates (`Core/Db/PromotionStore.cs:445`);
   a deployment whose verdict no longer stands is ENDED (`Gateway/TradingGateway.cs:784-799`); `trade verdict` then returns the
   stale row instead of re-judging (`GatewayPipeServer.cs:2484`). The app self-updates (`Provisioning/UpdateService.cs`).
5. **Runner market orders need a quote ≤ 30 s old** (`TradingGateway.cs:2736-2741`, `Gateway/GatewayTypes.cs:17`) but the paper
   quote is stamped at the last bar's close (`PaperConnector.cs:257-262`) and bars arrive on an unaligned 60 s delay loop
   (`Provisioning/ForwardBarCollector.cs:179-196`) ⇒ refused `MARKET_DATA_UNAVAILABLE` whenever a bar lands > 30 s after its
   close (phase-dependent). HIST(2026-10-01): "Market data reads 'degraded' on the paper connector (closed 1-minute bars are always >30 s old)".
6. **A research run never checks the program's instrument against the dataset's pair** (`Gateway/Backtests.cs:87-197`; only
   the paper allocator compares instrument and envelope symbol, `TradingGateway.cs:335-337`).

## A. Stiffness maps

### A1. Data plane

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| One record type `KlineBar(OpenTime,O,H,L,C,V)+Quality` (`Core/Data/DatasetReader.cs:7-25`); normalised CSV `open_time,open,high,low,close,volume[,quality]` (`Core/Data/KlineNormaliser.cs:68,81`) | The only observation in the product; no data type for funding, OI, liquidations, book snapshots, trades, news, on-chain or social (grep of `src/` finds none) | A typed observation `(source, series, symbol, event_time, available_at, values)`; bars become one `series` | closed-only; gaps counted never filled; provenance per fetch; agent cannot write |
| Archive collection: `ICandleSource` (`Core/Data/CandleSource.cs:93-156`), rows as data (`Core/Data/CandleSourceCatalog.cs:83,144-205`), generic `CollectAsync(ICandleSource…)` (`Provisioning/MarketDataService.cs:60-111`) | Candles only; Binance spot monthly 1m × 12 months (`Core/Data/BinanceArchive.cs:27-36,107-115`); Revolut X 5m row has no endpoint (`CandleSourceCatalog.cs:163-180`); owner press per pair (`App/SettingsView.cs:185,377`) | A sibling `IObservationSource` with the same Periods/RequireSymbol/PublishesChecksum contract, feeding the same `dataset`/`dataset_file` provenance | published vs computed sha; REJECTED on mismatch (`Core/Db/DatasetStore.cs:346`); collection is an owner press, never a pipe op (`Core/Protocol.cs:37-47`) |
| Forward collector, 60 s loop (`ForwardBarCollector.cs:38,179-196`), one pair `MarketDataPair="BTCUSDT"` (`Core/Trading.cs:359`, `App/AppHost.cs:540-543`), market-data-only host (`CandleSourceCatalog.cs:106-116`) | one symbol, one interval, one host; poll not aligned to the minute | subscription list of (catalogue row, series, symbol), one loop each, boundary-aligned | closure-before-receipt; first reading stands (insert conflict) |
| Storage: frozen datasets = CSV + `dataset`/`dataset_file` (`Core/Db/Database.cs:444-482`); forward = `forward_fetch/bar/gap` rows (`Database.cs:1385-1430`) | Forward data can never become a dataset (`CandleSourceCatalog.cs:267-271`) and backtests take only a dataset id (`Backtests.cs:14-24`) | an app-frozen, hashed "forward snapshot" dataset class (never holdout evidence) | forward wording "not evaluation evidence" (`Core/Data/ForwardBars.cs:46-47`) |
| Agent reads: `data-list`, `data-bars` (`Core/Protocol.cs:47`; `GatewayPipeServer.cs:2587,2637-2770`) | Binance-pair regex (`:2640`); NEWEST dataset only (`:2666`); 10,000-bar cap (`DatasetReader.cs:49`) | `data-bars --dataset <id> --series <name>` over the generic store, same cap | read-only channel; audience is a required reader argument (`DatasetReader.cs:65-70`) |
| Holdout: inclusive time cutoff on one dataset, refused never clipped (`Core/Data/Holdout.cs:103-137`); `BarAudience.Referee` is `internal` (`:79`); checked in both readers (`DatasetReader.cs:70`, `Core/Data/BarFeed.cs:132`) | one cutoff per dataset; none on forward data; the CLI agent's process can read `state/` directly (`AgentRuntime/Containment.cs:43-45`) | one cutoff applied to a bundle of series through the same `Holdout.Refusal`; OS containment to make it physical | charge-first referee door; no op reads past a cutoff |

**Time semantics.** Archive bars carry event time only; knowledge time exists per FILE (`dataset_file.downloaded_at`) and a
bar is admitted only if it closed before the EARLIEST download (`MarketDataService.cs:97-102`). Forward bars carry
`close_time`, `received_at`, `fetch_id` (`ForwardBars.cs:175-177`; `Database.cs:1402-1415`) — a single-vintage point-in-time
record: a re-fetch that differs is refused and counted, never stored. Nothing is bitemporal (no vintages, no as-of reads);
the evaluator and runner order by `open_time` and never read `received_at` (`ForwardRuns.cs:134-160`); causality is
enforced instead by "signals from closed bars execute at the next open" (`StrategyEvaluation.cs:83-116`) and, live, by the
decision-age gate (`TradingGateway.cs:6085-6119`).

**Seams for new information, keeping replay causal and deterministic:**
- **(a) Non-bar market data** (funding, OI, liquidations, book snapshots, trades): clone the forward ledger's shape — an
  `observation_fetch` (url, requested/received, http, body sha) + `observation` keyed `(source, series, symbol, event_time)`
  with `available_at` and the conflict clause — collected by the `ForwardBarCollector` loop generalised over `sources.json`
  rows; history through an `IObservationSource` into `dataset`/`dataset_file`. Evaluation gets them by an as-of join
  (`available_at ≤ bar close`) done in the FEED (`BarFeed`, `ForwardRuns`) and passed to `Step` as an input like
  `AccountReading` (`Core/Strategy/StrategyEvaluator.cs:251`). Rung 27.
- **(b) Timestamped unstructured events** (news, posts, filings): the `material`/`material_note` split (`Database.cs:177-208`)
  is the template — an `event_item` row (content sha, source, claimed `published_at`, measured `received_at`) written only by
  a collector, plus agent claims in a separate table. Items reach a strategy only as numbers from a frozen scorer, i.e. (c).
- **(c) Derived features** (agent code or a model): a `feature_def` identified by sha(code or artifact, inputs, params,
  runtime version) and a `feature_run` recording input dataset shas → output sha — the `strategy_version`/`strategy_run`
  pattern (`Database.cs:621-678`) — executed by the APP in a contained runner over prefix-only inputs, output stored as a
  hashed series with `available_at` = compute time. The evaluator stays closed; the strategy references the def by hash.

### A2. Strategy plane

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| Closed AST: 5 series, 8 indicators, number/boolean, `private protected` node ctor (`Core/Strategy/StrategyAst.cs:4,12,15,59-65`) | no node for any external value; an indicator reads a bar series only (`docs/STRATEGY-LANGUAGE.md:71-72`) | one node `FeatureRef(name, back)` bound to a declared `feature NAME = <input hash>`, value supplied by the caller per event | closed node set; no I/O, clock or model inside the evaluator |
| One instrument, long/flat, one position (`Core/Strategy/StrategyProgram.cs:12-14,62-63`; `StrategyEvaluation.cs:6-10,42-46`); sizing ≤ 1× capital (`Core/Strategy/StrategyLimits.cs:103`) | no short, pair, spread, basket, cross-asset input | (i) `side` on entry rules + signed `AccountReading`; (ii) read-only reference series before multi-leg orders | leverage cap; allocation ceilings already on `Math.Abs` exposure (`TradingGateway.cs:3101`) |
| Evaluation clock = 1-minute bar: `Start` defaults 1m (`StrategyEvaluator.cs:145`), backtest passes none (`Backtest.cs:593`), runner passes 1m (`ForwardRuns.cs:135`); `timeframe` is hashed and stored, never used to resample or gate (grep; `IntentDecision` carries only freshness and decision age, `Gateway/GatewayTypes.cs:362-377`) | lookback ≤ 500 bars, history ≤ 20 (`StrategyLimits.cs:64,71`) ⇒ ≤ ~8.3 h of context; no daily/weekly/regime signal | deterministic resampling of closed 1m bars to the declared timeframe in the feed | closed bars only; warm-up refusal; gap counting |
| Limits: 16 indicators, 20 rules, 200 nodes, depth 8, 16,384 ops/event, 256 KB state (`StrategyLimits.cs:21-141`) | sized for "a cheap model asked for a rule set" (`:13-16`) | keep for the rule layer; feature/model inputs get their own declared budget | per-event bound; total parser |
| Backtest model: fee/slippage fractions, increment, capital (`Backtest.cs:36-167`); next-open fill (`:397`), stop-wins/gap-at-open (`:438-443`), cost > cash = no trade (`:401`), round down (`:487`), halt at 200,000 bars (`:328`) | no spread, latency beyond next open, volume cap, partial fill, funding or borrow; one dataset per run; ~139 days per run (HIST 2026-10-01: a 12-month run refused in words) | optional fields in `ExecutionModel.Canonical` (spread, latency bars, participation) — absent fields keep old run ids | run id = sha(version, dataset id + sha, window, model) (`Backtest.cs:276-277`) |
| Identity `StrategyId = sha(canonical\nparameters\nmanifest)` (`StrategyProgram.cs:50`); manifest = language/indicators/calendar (`Core/Strategy/StrategyVersions.cs:44-45`) | no slot for inputs (series, feature defs, model artifacts) | manifest gains `inputs=<sorted hashes>`; promotion id (`PromotionStore.cs:141-153`) the same | one meaning, one id |
| Runner replays every forward bar since start on every pass (`ForwardRuns.cs:25-46,116-160`), one series (`:77`) | O(age) per pass per deployment; frozen at 10,000 bars (§0.2) | page `Since`, or cache evaluator snapshots per (deployment, bar) checked by recompute | "no state a restart loses"; cursor frontier; write-ahead ops |

**Fill models.** Backtest: signal at a bar's close fills at the next open ± slippage; stop/target intrabar, stop wins a
double touch, a gap through the stop fills at the open, the target never better than its level (`Backtest.cs:380-460`).
Paper connector: same rules at the next CLOSED bar's open, friction default 0 = "FRICTIONLESS" (`Connectors.Paper/PaperFriction.cs:17-35`);
HIST(2026-09-20): paper fills land two bars after the signal bar. No model anywhere has spread, latency, queue or impact.

**Invariants that break.** *Shorting:* `PositionSide{Flat,Long}` (`StrategyEvaluation.cs:6-10`), risk sizing needs a stop
below the close (`docs/STRATEGY-LANGUAGE.md:142-145`), trade P&L is `(exit − entry) × qty` (`Backtest.cs:532`), the runner
cancels resting protection because a stop under no position "OPENS a short" (`ForwardRuns.cs:199-201`).
*Multi-instrument:* intents name `program.Instrument` (`StrategyEvaluator.cs:523,539`); one bar stream per state; one dataset
per run; deployment symbol = envelope symbol (`TradingGateway.cs:651`); paper positions keyed by symbol per account
(`Connectors.Paper/PaperBook.cs:120-121`), so two versions on one symbol net and charge each other's allocation
(`TradingGateway.cs:3101-3103`). *External features:* `Step(state, bar, account)` has no slot; determinism needs hashed
inputs; the holdout must cover them; `NotBefore`/freshness must include the input's availability. *Frozen model:* doctrine
forbids model calls/training (`docs/COUNCIL.md`:163-164); needs a pinned deterministic inference runtime, the artifact hash in
the manifest, and its own compute budget outside 16,384 ops/event.

**What the runner does per bar** (`ForwardRuns.cs:116-301`): settle answered ops and advance the cursor; re-parse the frozen
text; rebuild the run's own books from `deployment_op`⋈`execution_request`⋈`fill`; skip non-ascending bars; one note per UTC
day closed; cancel the losing protection leg after a stop/target/exit/flatten fill; place stop + target after an entry fill;
max-hold flatten BEFORE `Step`; `Step`; fault ⇒ end; intent ⇒ `IntentDecision.From` (no bounds ⇒ end) ⇒ `PlaceIntent` via
`PlaceAsync(AgentContext.Deployment)` with a `dp-<12>-<bar>-<seq>` id written `planned` first; nothing planned past an
unanswered op. **Freezing:** a version is the parsed text's hash; the first submission's row stands (`ON CONFLICT DO NOTHING`, `Backtests.cs:432-436`).

### A3. Evidence plane

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| `ScoringPolicyV1`: holdout window starts after the freeze; completed; ≥ 1 trade; net > 0 (`Referee.cs:547-550`); `PaperPolicyV1` = same without the freeze clause (`:584-593`); texts fixed by sha (`Core/Db/CampaignStore.cs:29-44,66-79`) | a sign test on one window: no minimum sample, dispersion, drawdown, turnover, benchmark or confidence bound; metrics are trades, wins, gross/fees/net, max DD (`Core/Strategy/BacktestMetrics.cs:31-88`) | a V2 text + code: minimum trades, lower confidence bound of net > 0, deflated by the trial count registered at verdict time | precommitment by sha; reason class only, no figure crosses (`Referee.cs:611-660`) |
| Judge's model = Frictionless (§0.1) | not venue-realistic in either direction | campaign copies the judge's model at open from the verified venue row + envelope capital, hashed like the policy | the submitter never picks its friction (`Core/Protocol.cs:105-111`) |
| Holdout window = [cutoff, end of dataset] (`Referee.cs:223-224`); cutoff + campaign only from the owner's press (`TradingGateway.cs:1279-1294`) | monthly archives ⇒ "promoted" needs months collected after the freeze; forward data is never judged | a forward-evidence verdict over an app-frozen post-freeze forward snapshot, referee-only | post-freeze evidence; charge-first |
| Budgets 200 trials / 50 exploration / 3 verdicts (`Core/Trading.cs:487,503,525`); trial per (campaign, version, run) (`Database.cs:806-815`); family charge via declared parent (`CampaignStore.cs:516-525,615-655`); verdict charged first, counted across renewals (`CampaignStore.cs:789,839-887`); idempotent per (version, campaign) (`GatewayPipeServer.cs:2484`) | multiple testing handled ONLY by counts; ancestry is voluntary, "never inferred" (`StrategyStore.cs:78-96`) | feed `TrialsCharged` (and the family's) into V2 deflation; advisory near-duplicate detection on canonical forms | counts survive restarts and team replacement |
| Standing computed at read (`PromotionStore.cs:383-424`) | interpreter build includes the app version (§0.4) | bind to `StrategyVersions.Manifest` + `Referee.EvaluatorVersion` (`Referee.cs:94`) | changed assumption ⇒ invalidated |
| Paper-eligible ⇒ paper allocation at the envelope ceiling, first-come over the newest 100 promotions (`TradingGateway.cs:316-357`) ⇒ deployment while `max_deployments` allows (`:618-660`) | forward P&L reaches Research as a note only (`TradingGateway.cs:1122-1170`); nothing feeds it into standing, ranking or retirement; `OpenRetirement` has no caller (`Core/Db/BoundaryStore.cs:454-480`) | a "forward standing" computed at read from a deployment's own fills, used by app policy to end losers and order allocations | paper never authorises live (`TradingGateway.cs:3009-3011`); live capital stays an owner press |

**Verdict mechanics.** `Referee.Verdict` refuses a program without the three execution bounds BEFORE charging (`Referee.cs:182,372-386`),
charges (`:186`, `:119-158`), re-parses and re-hashes the recorded source, runs the campaign's holdout in-process, scores V1, and only
on `evidence-precedes-the-freeze` scores PaperV1 on the same run (`:234-287`); records the run under role `referee` with no
trial, an immutable promotion row, a sanitised note to Research, and a 24 h director boundary for non-paper verdicts (`:289-340`).
Multiple testing across candidates is bounded by budgets and by never returning holdout figures — not by statistics.

### A4. Agent plane

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| Roles `[operations, research]` (`Core/Council.cs:41`); orders by role name (`:63`, `GatewayTypes.cs:270`); folder by name (`Council.cs:80`); share 1/N (`AgentRuntime/TurnMeter.cs:556`); boundaries need every role (`BoundaryStore.cs:602,622`) | fixed two-node organisation; no spawn/delegate tool (grep: none) | an app-minted identity row (id, parent, capabilities, allowance, expiry); `CouncilRoles.All` becomes a query; `MayPlaceOrders` a capability | identity minted by the app; operator authority not delegable |
| Runtime per role: Operations always the vendor CLI; Research the harness when a key is held (`App/AppHost.cs:194-198`) | harness speaks one shape, OpenAI chat-completions (`AgentRuntime/RuntimeManifest.cs:650-686`); price table OpenAI-only (`AgentRuntime/ListPrices.cs:81-98`) | more rows in `runtimes.json` / `costs.json` (already data) | key in memory only; reservation before launch |
| Harness tools: `read_file`, `list_files`, `write_file`, `trade`, `data`, `report` (`AgentRuntime/GrantedWorkerTools.cs:49-54`), default deny (`:133-143`), writes only `out/`, `trading/` (`:74`) | no code execution; `trade` lacks `deployment-list/stop` (`:111-117`); told to write `strategies/` (`AgentRuntime/WorkspaceBuilder.cs:289`) where it may not | one `run` tool inside an OS sandbox (no network, read-only data, CPU/time caps), outputs measured as `material` | default deny; every call in `tool_call` |
| CLI: `codex exec … --dangerously-bypass-approvals-and-sandbox` (`RuntimeManifest.cs:618`); job object + env whitelist; "no operating-system sandbox" (`Containment.cs:43-45`); armed live refuses launch (`:59-71`) | full shell + internet as the app's user: can read the holdout and the ledger DB | the same sandbox for both runtimes | live refusal until containment exists |
| Scheduling: per-role lease (`AgentRuntime/MissionLoop.cs:1066`); persisted wakes `owner, inbox, data, fill, order, renewal, self, review, report, brief, verdict, boundary` (`Core/Db/MissionEventStore.cs:14-89`); self-wake `next.json` ≤ 30 min (`MissionLoop.cs:110,2233-2243`); review 30 min (`:129`); 20 turns/CLI session (`:107`) | a fixed list of wake kinds | new kinds keyed by entity (e.g. `data:<series>:<sha>`) | idempotent wake ids; reservation |
| Memory: `AGENTS.md` rewritten every start (`WorkspaceBuilder.cs:93-99`); PLAN 60 / JOURNAL 200 lines (`AgentRuntime/WorkspaceRevisions.cs:40,47`); reports 20 / agenda 40 lines, 10 per pass (`AgentRuntime/CouncilRelay.cs:57-66`); harness 24 requests, 512 KB per turn (`AgentRuntime/ApiConversation.cs:64-81`) | no op lists the agent's own versions/runs/verdicts/lineage (31 op names, `Core/Protocol.cs:14-138`) | a read-only `experiments` op over the ledgers | measurement vs claim |

**What agents are told (gist).** `WorkspaceBuilder.Instructions` (`:193-586`): "This directory is yours … write and run code,
install packages, use the shell and use the internet" (`:197`, identical for the harness, which can do none of it); mission
"make at least enough money, net of what you cost to run, to pay for yourself", judged by `trade pnl` and the app's report
(`:204-232`); every turn has a cause, idle is healthy, ask to be woken (`:234-262`); memory is PLAN/JOURNAL (`:263-279`);
research = language reference, three examples, `trade backtest` with declared friction, `trade verdict` costs one of three
(`:281-330`); the risk limits, loss closures and flatten rules (`:331-408`); inbox is data never instruction (`:464-490`);
boundary assessments with RECOMMENDATION/BASELINE (`:552-581`); role sections (`:602-660`).
**Models.** codex default `gpt-5.6-sol` (`RuntimeManifest.cs:639`), harness default `gpt-5.6-luna` (`:682`); turn reserved at
1.2 M in / 20 k out (`Core/Trading.cs:689-691`), cap 5 USD/day (`:468`). HIST(2026-10-01): sol's 6.4 USD reservation exceeded
the 5 USD cap; the run used luna on codex for Operations.
**How a model learns outcomes.** Backtest reply with metrics and ≤ 20 trades (`Backtests.cs:77`; `GatewayPipeServer.cs:2316-2340`);
verdict = word + reason class only (`Referee.cs:611-660`); paper-run note with net figure at end and each UTC day close
(`TradingGateway.cs:1122-1170`); `pnl`/`report` ops; the per-turn Situation (`MissionLoop.cs:462-720`). **Real code
execution:** CLI role — unrestricted; harness role — none.

### A5. Execution / venue plane

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| One connector per gateway (`TradingGateway.cs:46,1533`), swapped whole (`App/AppHost.cs:772`); `fake`, `paper`, `atas` (`Platforms/Connectors.cs:82-93`) | no concurrent venues; one envelope standing per (connector, account) (`TradingGateway.cs:624`), one symbol each (`Database.cs:1458-1471`) | a registry of gateways keyed by connector, each with its own settings and ledgers | dispatch identity (connector, mode, account) on every row |
| `ITradingConnector` (`ConnectorSdk/Contracts.cs:172-236`): accounts, instruments, quote, positions, orders, executions, place/modify/cancel/close + events | spot-shaped: no funding, borrow, margin, leverage, book or bar stream; `QuoteInfo` only (`:15-20`) | optional capability interfaces (funding, margin) beside the core contract | rules 1-3 below |
| Paper connector: real forward bars in, simulated fills out, idempotent SQLite book (`Connectors.Paper/PaperBook.cs:86-130`); verified instruments only (`PaperConnector.cs:181-215`) | single bar source/interval; friction 0 by default; positions per symbol | per-instrument friction from the venue row | `IsPaper` + no HTTP client in the assembly (HIST 2026-09-19) |
| Fake: fixed deterministic quotes (`Connectors.Fake/FakeBroker.cs:62-66`); ATAS: capabilities from the bridge hello (`Connectors.Atas/AtasConnector.cs:595-596`) | — | — | never place orders through a UI (`CLAUDE.md` rule 4) |

**A new venue adapter must:** carry `PlaceOrderCommand.ClientOrderId` (`Contracts.cs:90-98`) and read it back on `OrderInfo`
(`:40-42`); serve order history back to `since` (`:211`) or report `SupportsOrderHistory=false`; report capabilities truthfully —
`ReconciliationProvable` gates `LIVE_AUTONOMOUS` (`:107-116`; `TradingGateway.cs:2638-2641`); throw `ConnectorRejectedException`
only for a definite refusal and let transport/ambiguity propagate (`:119,125`; `docs/CONTRACTS.md`:35-40); mark the transport
ledger on every mutation (`Contracts.cs:143-171`); be added to `Connectors.Create` (`Platforms/Connectors.cs:84-93`) and its
instruments to `venues.json` (`VenueCatalog.cs:105`).
**Dispatch checks, in order:** role gate for mutating ops (`GatewayPipeServer.cs:1179`) · request id required + idempotent
replay (`TradingGateway.cs:5427-5438`) · `TryAuthorizeExecution`: update installing (`:2590`), mode allows execution (`:2599`),
AI stopped / kill switch (`:2604`), deployment in a live mode (`:2624-2627`), live not activated (`:2635`), autonomy needs
provable state (`:2641`), account chosen (`:2662`), unreconciled requests (`:2671,2679`), health trustable (`:2685`) ·
`RiskCheckOrThrow`: allowlist (`:2707`), quantity > 0 and ≤ max (`:2710-2715`), PAPER mode needs a simulated account
(`:2719`), rate limit (`:2731`), fresh quote (`:2736-2741`), notional cap (`:2747-2753`) · inside `_dispatchGate`:
open-position cap (`:2946`), unresolved reducer (`:6040`), loss budgets (`:3197`), per-order limits re-asked (`:2818`),
allocation ceiling / envelope reservation (`:3059-3134`) · LIVE_CONFIRM parks for approval (`:5459-5460,5495-5499`) ·
`DispatchPlaceAsync`: stale close (`:5942`), re-authorise + same mode (`:5341-5347`), decision age (`:6085`), dispatch slot
(`:5295`), write-ahead `DISPATCHING` (`:6158`), then the wire.

### A6. Accounting / reporting

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| `fill` ledger, PK (account, execution), fee NULL = unknown, `request_id`, `agent_session`, `connector` (`Database.cs:297-315,1303`); P&L by symbol and day with `incomplete` (`Gateway/Pnl.cs:32-48`), scoped to (connector, account) (`Core/Db/FillStore.cs:124`) | no per-version / per-deployment P&L reader except the runner's note | a read view joining `fill`→`execution_request.strategy_version_id`/`deployment_op` | one writer; never updated or deleted |
| `ai_attempt` reserve-before-launch, LOST keeps its reservation (`Core/Db/AiAttemptStore.cs:30-88`; `Database.cs:349-372`), per role (`Database.cs:521`) | cost attributed to role/attempt, not to a hypothesis or version (a version records only its freezing attempt, `Database.cs:621-632`) | an `experiment` id carried on attempts, versions, runs, tool calls | unknown ≠ zero; reservations survive restarts |
| Daily report, 10 fixed sections (`Gateway/DailyReport.cs:547-752`); other costs = live market data only (`:717-719`); Performance card today/all P&L (`App/PerformanceCard.cs:121`) | no population or per-strategy economics | per-version rows in sections 4 and 8 | composed by the app, no write op (`Core/Protocol.cs:66-75`) |

### A7. Lineage / evolution

| What exists | Narrow / hard-coded | Smallest generalization | Protection to keep |
|---|---|---|---|
| `strategy_version.parent_version_id` declared via `--parent` (`Database.cs:1212`; `Backtests.cs:128-142`); `ChildrenOf`/`Ancestry` (`StrategyStore.cs:257-279`) | voluntary, single parent, no branch/merge, no population read | an `experiments`/lineage op; optional inferred-similarity edges kept apart from declared ones | declared ≠ inferred; first declaration stands |
| Family charging + exploration/refinement pots (`CampaignStore.cs:516-525,615-655`) | pots are trial counts only | weight pots by forward record | counts cannot be bought by re-hashing |
| Retirement boundary + fence (`BoundaryStore.cs:454-480,869-900`) | no production caller | app policy opens it from the forward standing | retirement erases nothing |
| Attribution: version/run `role`,`attempt` (`Database.cs:621-660`); `execution_request.strategy_version_id/allocation_id` (`:1179-1180`); `deployment_op.request_id` (`:1589-1600`); `tool_call` (`:703-715`) | allocation is "envelope ceiling, first-come" (`TradingGateway.cs:340-341`), never by track record | allocation ordered by forward standing inside the envelope | paper ≠ live; owner press for live |

### A8. Data-driven extension points (built-ins in code, override file in `%LOCALAPPDATA%\TradeAgent`)

| File | Controls | Anchor | Limit |
|---|---|---|---|
| `runtimes.json` | runtime manifests: executable, args incl. sandbox flags, auth URL pattern, vendor-limit parsing, model args/defaults, harness base URL/path | `RuntimeManifest.cs:384,495-686` | unreadable ⇒ no runtimes (fail closed) |
| `costs.json` | per-model prices over `ListPrices` | `RuntimeManifest.cs:844-893`; `ListPrices.cs:81-98` | prices are claims, labelled |
| `sources.json` | candle sources: venue, interval, coverage, URL/checksum shapes, volume flag, verified | `CandleSourceCatalog.cs:83,218-235` | candles only; Binance archive has its own code path (`:273-275`) |
| `venues.json` | venues, instruments, tick, increment, verified | `VenueCatalog.cs:105` | gates paper trading and runner sizing (§0.3) |
| `atas.json` | ATAS install / indicator paths | `Connectors.Atas/AtasInstallation.cs:79` | — |
| settings JSON in `kv('settings')` | mode, risk, caps, budgets, pair, paper friction | `TradingGateway.cs:2160,2247`; `Core/Trading.cs` | owner UI only |
| **Not data:** scoring policies (`CampaignStore.cs:29,66`), evaluator version (`Referee.cs:94`), roles (`Council.cs:41`), tools (`GrantedWorkerTools.cs:49-54`), zones (`StrategyLimits.cs:155-156`), op list (`Protocol.cs:14-141`) | | | |

## B. Where a moat could attach

**Good foundations (extend, do not replace):**
- **Point-in-time capture:** `forward_fetch`/`forward_bar`/`forward_gap` — receipt stamps, body sha per fetch, first reading
  stands, gaps recorded never filled (`Database.cs:1385-1430`; `ForwardBars.cs:175-228`). As-received history cannot be bought
  later; generalised to any series it is the core of a proprietary data flywheel.
- **Frozen-evidence substrate:** `dataset`/`dataset_file` with published vs computed sha and REJECTED-on-mismatch, the time
  holdout, the internal `BarAudience.Referee`, campaigns (`Database.cs:444-482,724-848`; `Holdout.cs:51-137`).
- **Measurement vs claim:** `material`/`material_note` + `AppFileManifest` origin (`Database.cs:159-210`; `Core/AppFiles.cs:39`);
  the DDL comment already anticipates a model artifact in the inbox (`Database.cs:173`).
- **Content-addressed experiment graph:** version id, run id, 9-fact promotion id, evaluator version (`StrategyProgram.cs:50`;
  `Backtest.cs:276-277`; `PromotionStore.cs:141-153`; `Referee.cs:94`).
- **Selection accounting:** trial/verdict budgets with family charging and renewal lineage (`CampaignStore.cs:516-887`) — the
  place a statistical scorer gets its trial counts.
- **Cost and action provenance:** `ai_attempt` reservations, `tool_call`, `publication`/`delivery` (`Database.cs:349-372,
  524-546,703-715`) — the inputs for "cost per edge found" and per-agent fitness.
- **Event bus:** `mission_event` wakes keyed by entity (`data:<sha>`, `verdict:<promotion>`, paper-run occasions)
  (`MissionEventStore.cs:207-297`).
- **Deterministic forward execution:** deployment + write-ahead ops + replayable runner + `IntentDecision` bounds + every gate
  (`Database.cs:1550-1600`; `ForwardRuns.cs`; `TradingGateway.cs:5425-6160`) — any frozen signal can ride it once `Step` takes inputs.
- **Forecast ledger:** sealed director assessments with RECOMMENDATION/BASELINE and code defaults (`BoundaryStore.cs:83-133,310`)
  — a calibration record per agent.
- **Vendor agility:** catalogues as data with `verified` flags (A8).

**Missing for a moat:** a general observation store with `available_at` and as-of reads (bitemporal vintages if revisions
matter); a feature/model registry with app-run, contained, deterministic execution and hashed outputs; a pinned inference
runtime for frozen models; OS containment so the holdout and ledgers are physically private (today a contract, `Containment.cs:43-45`);
a statistics-aware referee with a realistic judge model; forward standing, population view and track-record allocation;
multi-instrument / multi-venue data and execution with derivative economics; delegation with app-minted identities; an
agent-facing read API over the experiment graph.

## C. DB schema inventory (`state/` SQLite, `Core/Db/Database.cs`; `DatabaseSchemaVersion = 26`, `Core/Versioning.cs:274`; next free rung 27)

| Table | Rung (DDL line) | Purpose |
|---|---|---|
| `meta` | 0 (:105) | `schema_version` |
| `execution_request` | 1 (:111); +`strategy_version_id`,`allocation_id` 20 (:1179) | one row per thing sent to a broker; write-ahead state machine, client order id |
| `activity` / `engineering_log` / `health_event` | 1 (:135/:138/:143) | owner-readable history / structured log / health transitions |
| `runtime_install` / `onboarding` / `kv` | 1 (:147/:151/:154) | installed runtimes / onboarding steps / settings JSON and other key-values |
| `material` | 2 (:177); +`version` 4 (:267) | scanner-only file observations (path, sha, origin, seen/removed) |
| `material_note` | 2 (:198) | agent CLAIMS about files (ran/derived/note) |
| `composite_request` | 3 (:235) | cancel-all/close-all plan + nonce so a replay sends nothing |
| `fill` | 5 (:297); +`connector` 22 (:1303) | append-only executions ledger, PK (account, execution) |
| `ai_attempt` | 6 (:349); +`role` 9 (:521) | per-turn reservation, tokens, cost, state |
| `mission_event` | 7 (:404); +`role` 9, +`disposition_detail` 10 (:567) | persisted wakes and their dispositions |
| `dataset` / `dataset_file` | 8 (:444/:470); +holdout/class 14, venue 17, coverage 18 | frozen archive datasets and per-file provenance |
| `publication` / `delivery` | 9 (:524/:538); index 11 (:583) | relay artifacts (report, brief, note, plan, journal, verdict, assessment, challenge) and delivery state |
| `strategy_version` | 12 (:621); +bounds 19 (:1108), +`parent_version_id` 21 (:1212) | frozen programs by hash, source kept, role/attempt |
| `strategy_run` / `strategy_trade` | 12 (:634/:663); +`increment_source` 17 (:1043) | app-computed backtest runs (metrics, trace sha) and their trades |
| `tool_call` | 13 (:703) | every harness tool call, served or refused |
| `strategy_campaign` / `strategy_trial` / `strategy_verdict` | 14 (:771/:806/:839); +exploration 21, +paper policy 23 (:1333) | holdout campaign, charged trials, verdict charges |
| `strategy_promotion` | 15 (:881); +bounds 19 (:1111) | immutable verdict rows (promoted / refused / paper-eligible) |
| `boundary_event` / `boundary_submission` | 16 (:936/:954); +baselines 21 (:1274) | consequential boundaries, sealed assessments, one challenge |
| `venue` / `venue_instrument` | 17 (:999/:1007) | catalogue mirror: tick, increment, verified |
| `strategy_allocation` | 20 (:1148); +scope/connector/mode/account/envelope 25 (:1502) | capital or paper allocation per version |
| `forward_fetch` / `forward_bar` / `forward_gap` | 24 (:1385/:1402/:1417) | forward collection attempts, first-reading bars with `received_at`, gap runs |
| `paper_envelope` | 25 (:1458) | owner's one-time paper grant (account, symbol, ceilings, max deployments, expiry) |
| `strategy_deployment` / `deployment_op` | 26 (:1550/:1589) | paper deployment identity, cursor, state; write-ahead runner operations |

**Outside the schema:** `state/paper-<account>.db` — `paper_meta` (own schema 1), `paper_order`, `paper_fill` UNIQUE(coid,
bar), `paper_position` keyed by symbol (`PaperBook.cs:86-130`); normalised CSVs and raw zips under `state/data/…`
(`BinanceArchive.cs:121-125`); `state/app-files.tsv` (`AppFiles.cs:39`); `coid-witness.json` (`AtasBridge/CoidWitness.cs:119`);
workspace PLAN/JOURNAL/archive, `.tradeagent/next.json`, `context.json`; the five override JSON files (A8); the harness key in memory only.

## D. Top 10 stiffness points, ranked by how much they cap expressing or discovering an edge

1. **Bars are the only observation.** One OHLCV record, one store shape, one feed, `data-bars` only; no other information
   source exists in data, ops or evaluator (`DatasetReader.cs:7-25`; `StrategyEvaluator.cs:251`; `Protocol.cs:47`).
2. **The language cannot read anything but its bar.** Closed AST, 5 series, 8 indicators, one instrument, long/flat, no
   feature or model node (`StrategyAst.cs:4-65`; `StrategyProgram.cs:12-14`).
3. **No trustworthy way for agents to build features or tools.** Harness: no exec (`GrantedWorkerTools.cs:49-54`); CLI:
   unconfined shell whose outputs cannot be admitted as evidence and which can read the holdout (`RuntimeManifest.cs:618`;
   `Containment.cs:43-45`); no feature registry.
4. **One-minute clock, ≤ ~8 h memory.** Every program steps per 1m bar; 500-bar lookback, 20-bar history; `timeframe` never
   resamples (`StrategyEvaluator.cs:145`; `StrategyLimits.cs:64,71`).
5. **The referee is a frictionless sign test.** net > 0 and ≥ 1 trade on one window, judged at increment 1 / capital 10,000,
   no sample size or confidence, no multiple-testing correction (`Referee.cs:218,547-550`; `GatewayPipeServer.cs:2496`; §0.1).
6. **Forward results never select anything.** Paper P&L is a note; no forward standing, population view, ranking,
   track-record allocation or wired retirement (`TradingGateway.cs:316-357,1122-1170`; `BoundaryStore.cs:454-480`).
7. **One venue, one pair, one envelope symbol at a time.** One connector per gateway; one collected pair; positions per
   (account, symbol) shared by every version (`TradingGateway.cs:46,624,651,3101`; `Trading.cs:359`; `PaperBook.cs:120-121`).
8. **Two fixed roles, no delegation.** Role list, order permission, workspace and 1/N budget share are keyed by two names
   (`Council.cs:41,63`; `TurnMeter.cs:556`); search breadth is two turns at a time.
9. **Evidence is slow and perishable.** Holdouts on monthly archives by owner press, 3 verdicts per lineage, idempotent per
   campaign, invalidated by every app release (`Trading.cs:503`; `GatewayPipeServer.cs:2484`; `StrategyStore.cs:213-214`).
10. **Execution and compute realism.** Next-open bar fills with no spread/latency/volume/funding; 200,000-bar runs; the
    runner replays from start and freezes at 10,000 bars (`Backtest.cs:328,397-443`; `ForwardRuns.cs:134`).
