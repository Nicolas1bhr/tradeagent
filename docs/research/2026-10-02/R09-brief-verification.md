# 09 — The six queued briefs, checked against HEAD (read-only)

HEAD `0f47db7f8a089ec8de75750b7e86c69f03907012` (`git rev-parse HEAD`). `src/` and `tests/` were read at HEAD. The working tree has the
manager's uncommitted doc edits (CLAUDE.md, COUNCIL.md, HOW-WE-BUILD.md, PRINCIPLES.md, RESUME-HERE.md, manager-prompt.md),
and nothing under `src/` or `tests/` was changed. Nothing was built, run or edited. Verdicts: CORRECT, WRONG, IMPRECISE, plus
GAP (the brief leaves a design decision to the builder) and TRAP (an existing test or doctrine the builder will hit).
"Red by reading" means I traced the code path. It does not mean I ran anything.

## 0. Cross-cutting findings

- **The briefs cite files that are not committed.** `docs/EDGE-FACTORY.md` and `docs/research/2026-10-02/*` are untracked
  (`git status`). A builder's worktree is made from `main`, so it will not have them. Commit them, and the queue, before dispatch.
- **Running three at a time breaks the doctrine.** HOW-WE-BUILD.md:18 says "At most two heavy legs at once", and :80 says "One
  gate at a time on this Mac: two suites overlapping flake the timing tests". EDGE-FACTORY §9 says three builders. Either amend
  the doctrine or serialise the gates.
- **Rung numbers.** EDGE-FACTORY §9 says "Schema rungs are assigned at landing", but the briefs hard-code 27 and 28. See §7: the
  order they land in matters.
- **Vendor-host scan.** Every earlier vendor unit added its host to `tests/TradeAgent.UnitTests/SuiteReachesNoVendorTests.cs`
  (:40-66; the forward host is :66). U-tape adds a new host (fapi) and U-decision-port adds two (TypeSafe, OpenRouter), and
  neither brief asks for the scan to be extended.
- **No name collisions.** `git grep` for every new name (TapeStore, VenueCostModel, InstrumentVerifier, instrument_check,
  decision_call, IDecisionModel, EvaluationSemantics, FillSemanticsVersion, JudgeCapital, AlignOffset, systemone, typesafe,
  openrouter, tape.db) finds nothing under `src/` or `tests/`.

## 1. U-cost-model — WRONG 1 · IMPRECISE 3 · GAP 7

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `GatewayPipeServer.cs:2496` | CORRECT | `gateway.Referee.Verdict(version, campaign.Id, stop: ct)` passes no model |
| `Backtest.cs:76` | CORRECT | `Frictionless = new(0m, 0m, 1m, 10_000m)` |
| `Referee.cs:218` | CORRECT | `var judged = model ?? ExecutionModel.Frictionless;` |
| `Backtest.cs:487-495`, `:401` | CORRECT | `RoundDown` gives NoTrade "rounds down to nothing", and `if (cost > cash)` gives NoTrade. At increment 1 and about 85,000, every size below 1 BTC rounds to 0 and 1 BTC or more costs more than 10,000 |
| `Referee.cs:137-143,186` | CORRECT | `ChargeVerdict` (:137) runs before the audience exists. "Three" checks out: `CampaignVerdictBudget = 3` (Trading.cs:503) |
| `PaperEligibleVerdictTests.cs:55-60` | CORRECT | `size fixed 1`, with closes `96m + i % 10` (:92) |
| `PaperFriction.cs:17-35` | CORRECT | `None = new(0m,0m)`. An unset value is wired as 0 at AppHost.cs:761-763 |
| `CampaignStore.cs:29-79,516-887`; `TradingGateway.cs:1279-1294`; `VenueCatalog.cs:105-190`; `Backtests.cs:87-197` | CORRECT | `SetHoldout` calls `_campaigns.Open` at :1288. `Run` is :87-193. The increment lookup that refuses unverified rows is :341-396 |
| `PromotionStore.cs:141-153` ("the sha joins the promotion's facts") | IMPRECISE | `executionModel` is already one of the nine hashed facts (:151, written at Referee.cs:308), so a different judge already counts as different evidence. A separate sha would need a `strategy_promotion` column, and rung 27's list does not include one |
| `PromotionStore.cs:383-445` | IMPRECISE | `Invalidation` runs to :465 |
| `SettingsView.cs:476-504` | IMPRECISE | :476-485 is `Age()`. The holdout card is built at :231-237, the presses are at :502-512, and `ApplyHoldout` is :562-597 (`SetHoldout` at :579) |
| Item 1: "minimum notional from the instrument's catalogue row" | **WRONG** | No such field exists. `VenueInstrumentEntry` (VenueCatalog.cs:38-56) and `venue_instrument` (VenueStore.cs:60-61) hold only tick and increment. VenueCatalog.cs:97-100 and USER-GUIDE.md:961-963 both say "no minimum notional", and `git grep -i minnotional src` finds nothing. The brief must name the source: a new catalogue field plus a column in rung 27, or U-venue-verify's check |
| GAP: unverified increment | GAP | "its verified flag named" conflicts with the never-guess refusals at Backtests.cs:361-366, PaperConnector.cs:320-327 and ForwardRuns.cs:494-500. It also conflicts with the TRAP test `VenueIncrementTests.An_unverified_row_is_served_as_unverified_and_refused_as_an_increment` (:239). The brief must say which rule wins for the judge and which for research |
| GAP: mismatch refusal (item 3) | TRAP | It inverts `VenueIncrementTests.The_increment_is_looked_up_by_the_datasets_instrument_and_not_the_programs` (:304-319), which runs `instrument ES` over BTCUSDT bars and asserts success. The names-removed gate forbids deleting it, so the brief must allow rewriting it in place |
| GAP: "owner has never set" paper friction | GAP | `PaperFeeFraction` is a non-nullable `decimal` (Trading.cs:392-398). It is saved by `Json.Write(Settings)` (TradingGateway.cs:2247), which omits only nulls (Protocol.cs:298). So every existing install, including the observed-run home, already stores an explicit 0 and would stay frictionless. The brief must state the upgrade rule |
| GAP: renewal | GAP | `CampaignStore.Renew` (:368-398) copies the parent's policies. The brief must say the cost-model pin is copied too |
| GAP: legacy "migration pins v1" | GAP | v1 needs the venue fee and the catalogue increment, and rung 17 forbids freezing vendor facts into a migration (Database.cs:983-987). The brief must say how v1 is computed, or allow a NULL pin that is fixed at first verdict |
| GAP: "the run records which applied" | GAP | `strategy_run` has only `increment_source` (StrategyStore.cs:205-210). The brief must add a column to 27 or state a rule |
| GAP: minimum notional in `ExecutionModel.Canonical` | GAP | Adding it to the canonical form moves run ids (Backtest.cs:31-34). The brief should say "appended only when non-zero" or "not hashed" |
| TRAP: policy texts | TRAP | V1 and PaperV1 say "declared execution model" (CampaignStore.cs:38,78). Leave the texts alone: changing them changes the sha, and Referee.cs:201-206 and :261-266 then refuse every open campaign |

**Absence claims:** both CORRECT. (1) The submitter cannot choose the friction: Protocol.cs:108-110, and :2496 takes no
model. (2) Paper fills default to 0: AppHost.cs:761-763.

**Feasibility:** not one 40–90-minute unit (an estimate, not a measurement). It has five items, a rung with a legacy backfill,
two refusal sites, a change to a settings type, three surfaces, and the seven gaps above. Suggested split:
- **A:** items 1–3 (the model, the pin, the referee, the research default, the mismatch refusal).
- **B:** items 4–5 (the paper friction default and the surfaces).

**Red-first:**
- (a) Red by reading.
- (b) and (c) are red only by failing to compile (new API).
- (d) Red by reading: VenueIncrementTests:304-319 shows a mismatched program runs today, and Referee has no instrument check
  before :186.
- (e) Red by reading (an unset fraction is 0).
- (f) Red by reading (SetHoldout opens a campaign over any dataset).
- Mutants (i) and (ii): red by reading.

## 2. U-venue-verify — WRONG 0 · IMPRECISE 1 · GAP 2

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `VenueCatalog.cs:174-186` | CORRECT | Venue `Verified = false` at :176, BTCUSDT row `Verified = false` at :186 |
| `PaperConnector.cs:181-215` | CORRECT | `.Where(i => i.Verified)` at :203 and :215 |
| `ForwardRuns.cs:492-516` | CORRECT | `Increment()` reads only verified rows from `_venues.Instruments()` (:512-516) |
| `VenueCatalog.cs:105` + "no screen writes venues.json" | CORRECT | `git grep File.WriteAllText src` finds vendor-file writers only for runtimes.json (RuntimeManifest.cs:733) and atas.json (AtasInstallation.cs:107) |
| "the observed-run home has none" | NOT CHECKABLE | It is the owner's machine |
| `VenueStore.cs` delete-then-insert `Sync` | CORRECT | :71-115. Called once, at gateway construction (TradingGateway.cs:1563) |
| `ForwardBarCollector.cs` (leash, body bound, row per attempt) | CORRECT | :46, :52, :243-257 |
| `SettingsView.cs:157-200` | IMPRECISE | :157-177 is the Account card. The Market data card is :178-215 |
| `FakeArchive.cs`; CONTRACTS venue section | CORRECT | CONTRACTS.md:2221 |
| Item 3: the paper connector reads "the served values" | **GAP** | `PaperConnector` reads the file catalogue once, at construction (`_catalogue = _opt.Catalogue ?? VenueCatalog.Read()`, :101), not the database. A check that succeeds after start, or lapses on day 7, never reaches it. The brief must specify one Core "served instruments" read, used by VenueStore's readers (ForwardRuns :512, Backtests :356, venue-list) and passed to the connector as a `Func<VenueCatalogRead>` option (Platforms/Connectors.cs) |
| Owner-facing text | GAP | USER-GUIDE.md:935-963 still tells the owner to edit venues.json, and so do the refusals at Backtests.cs:390-396 and PaperConnector.cs:326. All of them must change |
| Schema 28 with "26 → 28 is expected" | TRAP | See §7: rung 28 must never land before 27 |

**Feasibility:** borderline at 90 minutes, and only if the brief fixes the serving design.
**Red-first:**
- (a) Red by reading.
- (e) Red by reading, but only once the connector reads served values.
- (b), (c), (d), (f) are new behaviour. The mutants are red by reading.

## 3. U-runner-forward — WRONG 2 · IMPRECISE 3 (smallest; feasible)

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `ForwardRuns.cs:134` | CORRECT | One call: `_bars.Since(deployment.Symbol, deployment.StartedAt, source: Source)` |
| `ForwardBarStore.cs:227-238` | CORRECT | `limit = DatasetReader.MaxBars` (10_000, DatasetReader.cs:49), ascending |
| "nothing pages" | CORRECT | `git grep "\.Since("` in ForwardRuns.cs finds only :134 (and :559, which is `Fills.Since`) |
| `ForwardRuns.cs:196-197` | IMPRECISE | The `live` predicate is right, but bars after the 10,000th are never read at all (:134), so nothing past about 6.9 days is stepped. :134 is the line that decides it |
| `TradingGateway.cs:2736-2741`; `GatewayTypes.cs:17` | CORRECT | `quote.IsStale(_opt.MaxQuoteAge)`, otherwise MARKET_DATA_UNAVAILABLE; the limit is 30 s |
| `PaperConnector.cs:257-262` | CORRECT | `At = SettledThrough + Interval`. Side finding: `Interval` is the spacing of the last two settled bars (PaperBook.cs:171-175, 186-187), so after a gap the quote is stamped up to the gap's length in the future and passes `IsStale` |
| `ForwardBarCollector.cs:179-196` | IMPRECISE | The loop is :182-200; the fixed wait is :197, `_delay(Backoff(Tick, _failures), ct)` |
| The HIST quote | CORRECT | RESUME-HERE.md:34 at HEAD ("always >30 s old") |
| `TradingGateway.cs:2590-2760,5425-6160` | CORRECT | From `PlaceAsync` (:5425) to `DispatchPlaceAsync` (:6121) |
| Item 3: "the paper connector's MARKET-DATA HEALTH line" | **WRONG** | That row belongs to the gateway: `RefreshHealthAsync` at TradingGateway.cs:9933-9936. It does not gate execution (Core/Health.cs:69-82). Key the wider bound on `PaperConnector.ConnectorId` or `!SupportsStreaming`, not `IsPaper`: the practice simulator is IsPaper too (FakeConnector.cs:58) |
| Item 4: "the Data page" | **WRONG** | There is no Data page (MainWindow.cs:397-409). The card is Settings → Market data (USER-GUIDE.md:289-291) |
| Item 2: "next minute boundary" | IMPRECISE | It must be the next multiple of the injected `Tick`, because tests inject short ticks (ForwardBarCollectorTests.cs:144) |

**Red-first:**
- (a) Red by reading.
- (b) Red by reading (MaxHoldBars ≤ 10,000, StrategyLimits.cs:89, so an entry before bar 10,000 can reach its flatten after it).
- (c) Red by reading (:197 waits a fixed Tick).
- (d) **Cannot tell without running.** `QuoteInfo.IsStale` reads `DateTimeOffset.UtcNow` (ConnectorSdk/Contracts.cs:19), while
  the decision gate reads the gateway clock (TradingGateway.cs:6089). "The test's clock" never reaches the quote gate, so
  mutant (ii)→(d) is uncertain.
- (f) Red only if the bar's time is built relative to the machine clock.

## 4. U-evidence-identity — WRONG 1 · IMPRECISE 1 · GAP 3

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `StrategyStore.cs:212-214` | CORRECT | `$"app={Versions.App};language={StrategyVersions.LanguageVersion}"`. `Versions.App` is the assembly version (Versioning.cs:287-289) |
| `PromotionStore.cs:445` | CORRECT (one nuance) | It compares the promotion row's `interpreter_build`, which Referee.cs:307 stamps |
| `TradingGateway.cs:784-799` | CORRECT | `!standing.Authorises` gives "no longer stands", and the run is ended at :737-741 via `EndPaperDeploymentAsync` |
| `GatewayPipeServer.cs:2484` | CORRECT | `For(version).FirstOrDefault(p => p.CampaignId == campaign.Id)` is answered whether or not it is invalidated |
| `UpdateService.cs`; `StrategyVersions.cs`; `Referee.cs:90-100`; `StrategyStore.cs:190-260`; `TradingGateway.cs:760-800`; `GatewayPipeServer.cs:2440-2520` | CORRECT | `EvaluatorVersion` is at :94. It is recorded but not compared today (`Invalidation`, :422-465) |
| `PromotionStore.cs:383-445` | IMPRECISE | Should be :383-465 |
| "No schema change" + item 3 "charged like any verdict" | **WRONG** | `strategy_verdict` has PRIMARY KEY(campaign_id, version_id) (Database.cs:839-845), and `ChargeVerdict` answers Ok without writing for a pair already charged (CampaignStore.cs:846-847). A re-judge cannot be charged without a new rung |
| Item 3: the re-judged run | GAP | The run id is sha(version, dataset, dataset sha, window, model) (Backtest.cs:275-276) and includes no semantics. `RecordRun` is first-writer-wins, so the old figures stay under the id the new promotion cites |
| Item 1: Manifest inside the identity | GAP | The Manifest is part of StrategyId (StrategyProgram.cs:49-50). A Manifest bump re-identifies programs, and Referee.cs:213-216 then refuses to judge the old id. Re-judging only works for evaluator, fill or cost-model bumps |
| "app version kept in its own field" | GAP | The promotion's columns are fixed (PromotionStore.cs:256-260). The brief must name the existing column that carries the semantics identity (e.g. `evaluator_version`), or this becomes a schema change |
| TRAPs | TRAP | (1) Do not edit the V1 or PaperV1 text, which says "the interpreter build" (CampaignStore.cs:38,78): its sha would change. (2) `*.csv` is not pinned to `eol=lf` in .gitattributes, so golden bar files check out differently on Windows CI |

**Feasibility:** items 1, 2 and 4 fit in one unit. Item 3 should be its own unit, with a rung.
**Red-first:**
- (a) Red by reading: write a promotion row with a different `app=` value.
- (c) The "judged again" half is red by reading (:2484). The "charged" half cannot be done as briefed.
- (b) Red through mutant (ii).
- (d) Green on base: it is a guard, not a red-first test.

## 5. U-tape — WRONG 2 · IMPRECISE 1 · GAP 2

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `DatasetReader.cs:7-25`; `Protocol.cs:47`; `DatasetReader.cs:49-86` | CORRECT | `KlineBar`; `DataList`/`DataBars`; `Read(set, BarAudience audience, …)` |
| `PaperBook.cs:23,55` | IMPRECISE | The version constant is :39, the newer-file refusal :71-75, and the write-once `SetMetaIfAbsent` :77. There is no upgrade ladder: `Migrate` (:87-124) is a single block |
| `ForwardBarStore.cs`, `ForwardBarCollector.cs`; `CandleSourceCatalog.cs:83-235`; `AppHost.cs:476-560`; `FakeArchive.cs` | CORRECT | The catalogue has no DocsUrl field; the doc URL is written into the `Source` prose (:109-116) |
| `Protocol.cs:37-60` and handlers | CORRECT | Drain table at GatewayPipeServer.cs:331-333, dispatch at :1228-1230. A new op needs a drain-table row, which GatewayPipeBackpressureTests.cs:1083 enforces |
| `SettingsView.cs:157-165` | **WRONG** | That is the Account card ("Look again" and its notes). The pattern to copy is the Market data card (:178-215) and its live-bars toggle (:190-196) |
| "Data-page toggle", "USER-GUIDE.md Data page" | **WRONG** | There is no Data page (MainWindow.cs:397-409). Use Settings → Market data (USER-GUIDE.md:289) |
| "no rung on the main ladder, no contention" | CORRECT | The main database is one shared connection behind one lock (Database.cs:68-93) |
| Items 2–3: class of backfilled rows | **GAP (evidence risk)** | A polled row declares O-LIVE, and the brief says it "backfills … as its declared class", which labels 30 days of history O-LIVE. Compute the class per observation: anything not seen on arrival is O-ARCH (or O-PIT with a stated basis) |
| `natural_key` | GAP | How each row derives its natural key (which field carries event time) is not stated. Revisions mean nothing without it |

**Absence:** CORRECT. `git grep -i "fapi|premiumIndex|fundingRate|longShort" src` finds nothing. The bar is the only
observation.
**Feasibility:** not one unit (store, a 5-row catalogue, a 5-loop collector with backfill, op/CLI/schema/data-list/status/report,
a toggle, docs, 7 tests). Split it:
- **U-tape-store:** store, catalogue, collector, toggle.
- **U-tape-read:** op, CLI, data-list, status, report.

**Red-first:** none of these tests can be red on base, because the code is all new and this is not the money path. Mutants (i)
and (ii) are red by reading.

## 6. U-decision-port — WRONG 0 · GAP 3 (one contradicts a protection)

| Pointer / claim | Verdict | What the code says, and the correction |
|---|---|---|
| `RuntimeManifest.cs:384`, `:495-686`, harness `:650-686` | CORRECT | `RuntimeCatalog.OverridePath` is runtimes.json. Codex is :494-644 and the harness :645-686 (BaseUrl at :675) |
| `ListPrices.cs:81-98` | CORRECT (one nuance) | It is a table in code (:79-99), not a data file |
| `Security/HarnessKey.cs` | CORRECT | Memory-only, one static `Shared` (:31). A second key needs a holder of its own |
| `AiAttemptStore.cs:30-88` | CORRECT | The guard that decides it is `Begin` (:256-281): the admission and the INSERT happen in one main-database transaction |
| `TurnMeter.cs`, `FakeProvider.cs`; R02 §1, §2, §6 | CORRECT | The brief's facts match R02 lines 42-65 |
| "no model other than the agent runtimes is reachable" | CORRECT | `git grep -i "systemone|typesafe|openrouter" src` finds nothing |
| Ledger in tape.db "counted inside the owner's daily AI cap" | **GAP that contradicts a protection** | The cap is atomic only inside one main-database transaction (AiAttemptStore.cs:233-281, COUNCIL rule 3). A reservation in a second file reopens the race. Reserve through `AiAttemptStore.Begin` and keep the call record in tape.db, keyed by the attempt id |
| `openrouter-jev` pinned to `jev-1.13.0` | GAP | R02:65 gives OpenRouter's id as `typesafe/jev-1.13`. Set the pin and the answered-id comparison per host, or every call through OpenRouter reads UNPINNED |
| "its own version line there" | GAP | See §7 |

**Feasibility:** not one unit (two adapters across three hosts, ledger, cap, key holder, Safety card, Test press). Split it:
- **First unit:** the port, TypeSafeWire, the ledger and the cap.
- **Second unit:** LlmJsonDecision and the local runtime.

**Red-first:**
- Mutant (i): red by reading.
- Mutant (ii): cannot tell without running. The loopback host must read the ledger at the moment the request arrives.

## 7. Schema rungs and the second file

- **Main ladder.** `Database.Migrate` (Database.cs:103-1611) is a sequence of `if (have < N) { …; schema_version=N }` blocks.
  `Versions.DatabaseSchemaVersion = 26` (Versioning.cs:274). A database newer than the build is refused (Database.cs:1607-1610),
  and BacktestLedgerTests.cs:209-212 pins meta to the constant. A 26 → 28 ladder works mechanically on a fresh database.
- **The precedent only covers one landing order.** Rungs 13/14 and 16/17 were written out of order, but the lower rung always
  landed first ("landing 16 first cost it nothing", Database.cs:968-971). If 28 lands first, every persistent database opened
  in between (dev homes, the box, the observed-run home) records 28 and skips `if (have < 27)` for good. U-cost-model's columns
  are then missing, and the read fails at runtime with "no such column". Rule: **never land U-venue-verify before
  U-cost-model**, or renumber at landing as EDGE-FACTORY §9 says. If U-evidence-identity item 3 keeps its re-charge, it needs a
  third rung.
- **The second file only partly follows PaperBook.**
  - Faithful: its own file under `Paths.State`, its own connection (WAL, synchronous=FULL, busy_timeout 5000, :68), and a meta
    key/value version with a refusal for newer files (:71-77).
  - Not faithful: PaperBook has never upgraded. Its version is written once by `SetMetaIfAbsent`, and there is no ladder.
    U-decision-port's second "version line" in the same file has no precedent.
  - Fix in U-tape: give tape.db an `if (have < N)` ladder from v1, so U-decision-port adds rung 2.
  - A newer tape.db should be a status line, not a crash, following AppHost.cs:552-556.
  - No precedent spans two files in one transaction (see §6).

## 8. Conflict matrix (files each brief most likely touches)

| File | cost | venue | runner | evid | tape | port |
|---|---|---|---|---|---|---|
| Core/Strategy/Referee.cs, Db/PromotionStore.cs | X | | | X | | |
| Core/Strategy/Backtest.cs; Db/CampaignStore.cs | X | | | X | | |
| Core/Db/Database.cs (ladder tail :1604) + Versioning.cs:274 | X | X | | (X if PK rung) | | |
| Core/Data/VenueCatalog.cs; Db/VenueStore.cs | X | X | | | | |
| Core/Trading.cs (settings) | X | | | | X | X |
| Gateway/Backtests.cs (Increment :341-396 / RecordVersion :422) | X | X | | X | | |
| Gateway/TradingGateway.cs (StatusAsync :2335-2365, health :9933, SetHoldout :1279) | X | X | X | | X | X |
| Gateway/GatewayTypes.cs (`GatewayStatus` :397) | X | X | | | X | X |
| Gateway/GatewayPipeServer.cs (op table :331, dispatch :1228, verdict :2484) | | X | | X | X | |
| Gateway/ForwardRuns.cs; Provisioning/ForwardBarCollector.cs | | X(:512) | X | | (pattern) | |
| Connectors.Paper/PaperConnector.cs (+ Platforms/Connectors.cs) | X | X | (maybe) | | | |
| App/AppHost.cs (StartAsync :532-556, friction :761) | X | X | | | X | X |
| App/SettingsView.cs (holdout card :231 / Market data card :178-215) | X | X | (wording) | | X | |
| App/DashboardView.cs (Safety page); Security/* | | | | | | X |
| Core/Errors.cs `Labels`; DailyReport(s).cs | X | X | | | X | X |
| Core/Protocol.cs; GatewaySchema.cs; TradeCli/Program.cs | | | | | X | |
| tests/…/SuiteReachesNoVendorTests.cs | | | | | X | X |
| docs/CONTRACTS.md · USER-GUIDE.md (:935-963 shared by cost and venue) · RESEARCH-REQUIRED.md | X | X | X | X | X | X |

Pairs, by how likely they are to collide:
- **HIGH:** cost × venue (ladder tail, Versioning, VenueCatalog, Backtests.Increment, USER-GUIDE.md:935-963, status);
  cost × evid (Referee :218/:306-309, PromotionStore IdOf/Invalidation, CampaignStore; also a declared dependency);
  venue × tape (the same Market data card section :197-215, AppHost.StartAsync :532-556, status, Labels).
- **MED:** cost × tape (settings, `GatewayStatus` initialiser, daily report); runner × tape (AppHost collector block; the
  alignment logic is duplicated, so share one helper); venue × runner (PaperConnector, card wording, TradingGateway);
  venue × port (AppHost, status, Labels).
- **LOW:** cost × runner; cost × port; evid × {runner, tape, port, venue}; runner × port. tape → port runs in sequence anyway.

## 9. Recommended dispatch

Hard order:
- cost-model before evidence-identity (declared dependency, and the HIGH pair).
- tape before decision-port (declared).
- cost-model's rung 27 before venue-verify's 28 (§7).
- tape before venue-verify (the same card and the same StartAsync block).

With **two legs at once** (the doctrine at HOW-WE-BUILD.md:18):
- **W1:** U-cost-model ∥ U-runner-forward (LOW pair). Land runner-forward first: it is the smallest and has no rung.
- **W2:** after cost-model lands, U-evidence-identity ∥ U-tape (LOW pair).
- **W3:** after both cost-model and tape have landed, U-venue-verify ∥ U-decision-port (MED: AppHost and status). Land venue-verify
  first.
- M0 needs cost-model, runner-forward, venue-verify and evidence-identity, so it is reachable after W3's first landing.

With **three at once** (EDGE-FACTORY §9, only if gates are serialised):
- **Wave A:** cost-model, runner-forward, tape. Land in that order: runner-forward, then cost-model, then tape.
- **Wave B:** start only after all of Wave A has landed (not "as A lands"): venue-verify, evidence-identity, decision-port. Land
  venue-verify (28) first.

Before dispatch, fix in the briefs the WRONG pointers (§1, §3, §4, §5), the GAPs marked **bold**, and the splits suggested for
cost-model, tape and decision-port. Every brief should also require extending SuiteReachesNoVendorTests for any new host, and
allow the inverted VenueIncrementTests to be rewritten in place.
