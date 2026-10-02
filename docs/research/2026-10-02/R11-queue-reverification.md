# 11 — The ten queued briefs, re-checked against HEAD (read-only)

HEAD `0f47db7f8a089ec8de75750b7e86c69f03907012` (`git rev-parse HEAD`). Read: R09, the ten briefs in `docs/queue/`, `docs/EDGE-FACTORY.md` v2, `CLAUDE.md`, `docs/HOW-WE-BUILD.md`, and `src/` + `tests/` at HEAD. Nothing built, run or edited. Pointer verdicts: CORRECT / WRONG / IMPRECISE. GAP = a decision the builder would have to make. TRAP = an existing test the brief breaks without naming it. "Red by reading" = traced through the code, not run.

**Cross-cutting.** (1) `docs/EDGE-FACTORY.md`, `docs/queue/` and `docs/research/` are still untracked, so a builder's worktree cut from `main` lacks them. Commit before dispatch (R09 §0, still open). (2) **No WRONG pointer survives in any of the ten.** What remains is in claims and design. (3) U-tape-store and U-decision-port carry the short gate line (no rebase, `--no-incremental` or names-removed check). The doctrine covers it, but the brief should say it. (4) Every unit appends to `Errors.cs` `Labels` and `CONTRACTS.md`, so every wave will hit mechanical rebase conflicts.

## 1. U-key-host-pin (NEW) — WRONG 0 · IMPRECISE 3 · GAP 5 · TRAP 1

| Pointer / claim | Verdict | Correction |
|---|---|---|
| `RuntimeManifest.cs:716-728`: an override replaces the built-in, BaseUrl included | CORRECT | `builtIn[i] = o` (:724-725) swaps the whole object. An unreadable file yields no runtimes (:719) |
| `:341-342` Endpoint · `Containment.cs:43` · `HarnessKey.cs` memory-only | CORRECT | |
| `:736-745` IsHarness derivation | IMPRECISE | :737-745 is the comment. `Harnesses()` is :746-747 and `IsHarness` :750-751. An override can ADD a harness id, but `RuntimeObjectFor` (AppHost.cs:205-208) maps every harness id to the one `openai-api` object, so this opens no second key route |
| `AppHost.cs:128`: the harness is built once | CORRECT | The lazy getter (:121-148) caches `_harness` (:150) for the life of the process. Only `DisposeAsync` drops it (:1624). The manifest is captured at first use |
| `ApiConversation.cs:466-472`: the bearer post | CORRECT | The key is read ONCE per turn (:242) and reused for up to 24 requests (:64, :345) |
| read-first `ApiConversation.cs:440-520` | IMPRECISE | Item 2 changes :242-252 (the key read and the no-key refusal) and :163-173 (`StartAsync` reads the key for its status line). Neither is in the range |
| "the probe path in `ApiAgentRuntime`" (`:150-230`) | IMPRECISE | The probe is `ExecuteTaskAsync` (:211-219). It has NO caller in src/ or tests/ and builds an `ApiConversation`, so item 2 covers it anyway. `KeyHeld` (:75, outside the range) also calls `apiKey()`, from :147, :189 and :227 |
| Safety key box · FakeProvider | CORRECT | DashboardView.cs:1035, :1640-1644, `SaveHarnessKey` :2007-2015. FakeProvider keeps every Authorization header (:134-135) |
| Does ANY other path send the key? | CORRECT: none | `HarnessKey.Read` is referenced only at AppHost.cs:133. The one HTTP send is `PostAsync` (:466-497, called at :345). No model listing exists, and the sign-in state is local (`KeyHeld`). Keys for the CLI runtimes are written wherever `ApiKeyPlan.File/StdinArgs` say (RuntimeManifest.cs:52-67, OnboardingView.cs:779). Those keys are agent-readable by design and outside this brief, but CONTRACTS must say so, or EDGE-FACTORY § 6.10 over-claims |

GAPS
1. **Bind the ORIGIN, not `.Host` (HIGH).** `Uri.Host` drops the scheme and the port. An override of `http://api.openai.com/v1` passes items 1 and 3 and sends the bearer key in clear text. Every loopback listener is `http://127.0.0.1:{port}` (tests/Shared/Loopback.cs:58), so test (a) uses two listeners on the same host and cannot go green with `.Host`. Use scheme + host + port (`GetLeftPart(UriPartial.Authority)`) in items 1–3.
2. **"Charging nothing" has no code path.** A turn that ends with `Usage` null is charged its reservation (AiAttemptStore.End :333-338). The only zero branch is `TurnMeter.Charge` (:677-680), and it is keyed on a vendor limit `BeforeAnyWork`. Today's no-key refusal (:243-252) IS charged its reservation. Name `Charge` and add a "refused before any request" branch, or test (b) cannot pass.
3. **Presence checks must not clear the key.** If `Read(host)` clears on a mismatch, then `KeyHeld` (health, auth state, `StartAsync`) and `ApiConversation.StartAsync` (:167) clear the key with no refusal recorded. Presence checks should use `Held`; only the send path presents an origin.
4. The paste box must show the origin from the harness's OWN manifest (`Harness.Manifest`). A fresh `RuntimeCatalog.Find` can differ mid-session.
5. Red-first (a) and (b) are red on base only by failing to compile (`Set(key, host)` is new API). Say so.

TRAP: the signature change breaks these at compile time: HarnessRoleTests :100-124, :134-156 and :163-170 (`Set(x)`, `Read()`, `new ApiAgentRuntime(manifest, _key.Read)`), and the helpers ApiWorkerTests :47, HarnessBudgetTests :58 and HarnessLoopTests :109 (`() => key`). Allow rewriting them in place with names kept.

**Feasibility:** yes, 60–90 min, once gaps 1–3 are written into the brief.

## 2. U-cost-model (revised) — WRONG 0 · IMPRECISE 1 · GAP 6 · TRAP 1

| Pointer / claim | Verdict | Correction |
|---|---|---|
| R09's pointers: GatewayPipeServer :2496; Backtest :76/:401/:487-495; Referee :137-143/:186/:218; Trading :503; PaperEligible :55-60; CampaignStore :29-79/:516-887; TradingGateway :1279-1294/:1288; VenueCatalog :38-56/:105-190; Backtests :87-193/:341-396 | CORRECT | Every R09 correction has been applied |
| New: Referee :201-206/:261-266; Database :983-987; VenueCatalog :97-100; CampaignStore :38/:78/:368-398; PromotionStore :141-153 (fact at :151, written at Referee :308); SettingsView :231-237/:502-512/:562-597; Backtests :361-366; VenueIncrementTests :239/:304-319 | CORRECT | |
| Item 1: the fee "as data per venue row" | IMPRECISE | VenueCatalog.cs:97-100, Database.cs:995 ("no fee column") and USER-GUIDE.md:960-962 all say fees are never catalogued (COUNCIL.md:155). Amend all three. Also say whether the pin reads the fee from the file catalogue or from the `venue` table. The second needs a column in rung 27, and none is listed |

GAPS
1. **Datasets with no venue (HIGH).** `VenueId` and `InstrumentSymbol` are nullable (DatasetStore.cs:143-150), and the referee fixtures leave them null (e.g. PaperEligibleVerdictTests.cs:98-103). The press (`TradingGateway.SetHoldout`) is called 22 times in 8 test files, and `CampaignStore.Open` is called directly 17 times in 16 files, all expecting a campaign. The brief must say what happens with no venue: either pin `Frictionless` labelled "no venue recorded" (the suite stands) or refuse (all of those are rewritten). RefereeVerdictTests :324 and :423 assert the fixture is profitable, and a non-zero pinned fee can flip that.
2. **The interim from W1 to W4.** BTCUSDT ships unverified (VenueCatalog.cs:186). From W1 on, every BTCUSDT campaign press refuses with "check the instrument first", but that check only exists from W4 (U-venue-verify). Word the refusal so it does not name a missing feature.
3. "Declares no friction": per field, or all or nothing? `BacktestAsk` has four independent fields (Backtests.cs:100, :147).
4. The research default makes three agent-facing texts false: GatewayPipeServer.cs:2330-2333, GatewaySchema.cs:263 and WorkspaceBuilder.cs:312-314 ("an upper bound on a frictionless market"). Item 4 does not name them.
5. "The venue chosen on the same card" means a new picker on the holdout card and a new `SetHoldout`/`Open` signature (the call sites in gap 1). Cut it and pin from the dataset's venue.
6. The mismatch check compares against the dataset's `Pair`, but the increment is looked up by `InstrumentSymbol` (Backtests.cs:351-356). Pick one.

TRAP: VenueIncrementTests :304-319 is named and may be rewritten (good). Red-first (c) is GREEN on base, because every judge is frictionless today. It is a guard, not red-first.

**Red-first:** (a), (d), (e) and (f) are red by reading. (b) is red only by failing to compile.

**Feasibility:** NO as written (about 2–3 h). Cut gap 5, move the research default (gaps 3–4) into U-paper-friction, and settle gap 1 as "Frictionless, labelled". That brings it to about 90 min.

## 3. U-paper-friction (NEW) — WRONG 0 · GAP 3

| Pointer / claim | Verdict | Correction |
|---|---|---|
| PaperFriction.cs:17-35 · Trading.cs:392-398 · AppHost.cs:761-763 · TradingGateway.cs:2247 · Protocol.cs:298 | CORRECT | `PaperChoice()` is :759-770. The friction is read at every fill through a `Func` (PaperConnector.cs:36, :493) |
| "NO writer of either field in src/ or tests/" | CORRECT | `git grep` finds only the declarations (Trading.cs:392, :398), the reads (AppHost.cs:762-763) and CONTRACTS.md:126-127. No generic settings setter exists. Caveat: settings.json is a file the unconfined agent can edit |
| Where friction enters a fill | settled | `PaperConnector.Apply`/`Fill` (:491-556). The fee applies to every fill (:553). Slippage applies to market fills only (:509, :536-539). Stops fill at the level or the open (:520), targets at the level (:529) |
| Does each fill record its friction? | settled | Yes: `paper_fill.friction TEXT NOT NULL` (PaperBook.cs:117), written at :395-408 from PaperConnector.cs:556. That sentence can carry the rate, the slippage and the model sha, so PaperBook needs no change |
| StatusAsync :2335-2365 · DailyReport.cs:547-752 · "the paper card" | CORRECT | The "card" is an option row of the Trading platform card (SettingsView.cs:129, :146-151) |

GAPS
1. `PaperFriction.Sentence` (:31-35) says any non-zero friction was "declared by the account owner". That is false for the venue default. Give `PaperFriction` a source.
2. Keep the CONNECTOR default at `PaperFriction.None` and resolve in AppHost. Otherwise PaperSettlementTests :175-193, which asserts FRICTIONLESS, breaks.
3. Also edit CONTRACTS.md:126-128 and USER-GUIDE.md:195, and give `paper_friction` a status description in GatewaySchema.

**Red-first:** (a) is red by reading (the fee is 0 today).

**Feasibility:** yes, about 60 min, or about 90 with cost-model's research default moved in.

## 4. U-runner-forward (revised) — WRONG 0 · IMPRECISE 2 · GAP 2

| Pointer / claim | Verdict | Correction |
|---|---|---|
| ForwardRuns :134/:135 · ForwardBarStore :227-238 · DatasetReader :49 · Collector :182-200/:197 · TradingGateway :2736-2741/:6089/:9933-9936 · GatewayTypes :17 · PaperConnector :257-262 · PaperBook :171-175/:186-187 · Contracts :19 · Health :69-82 · FakeConnector :58 · SettingsView :178-215 | CORRECT | `Since(…, openExclusive, limit)` already supports paging |
| Item 3: "the gateway passes its own clock at :2736-2741" | IMPRECISE | The order path has a SECOND quote gate at :2830-2832 (`priced.Quote.IsStale`). There are also :5146 (loss valuation; :5141 computes `UtcNow - quote.At` itself), :9935-9936 (health), OnboardingView.cs:440 and CoreTests.cs:409-410. If only :2738 changes, test (e) is still refused at :2830 |
| `ForwardBarCollectorTests.cs:144`: tests inject short ticks | IMPRECISE | :144 injects the delay. The 5 ms Tick is :30, passed in at :49 |

GAPS
1. TRAP: `ForwardBarCollectorTests.The_collector_backs_off_after_failures_and_recovers` (:136-186) asserts that the waits grow strictly (:164-166). Either alignment does not apply under backoff, or the test is rewritten in place.
2. Item 1: `RunBooks` takes the whole bar list (ForwardRuns.cs:136, :556), so paging must still hand it every bar. State the O(age) memory cost beside the O(age) time cost.

**Red-first:** (a)–(c) and (g) are red by reading. (d) and (e) are red by reading now that `IsStale` takes `now`, which resolves R09's doubt.

**Feasibility:** yes, at the top of the range.

## 5. U-evidence-identity (revised) — WRONG 0 · GAP 3 (one opens a hole) · TRAP 1

| Pointer / claim | Verdict | Correction |
|---|---|---|
| StrategyStore :212-214 · Referee :94/:307 · PromotionStore :258/:383-465/:445 · TradingGateway :784-799 via :737-741 · GatewayPipeServer :2484 · StrategyProgram :49-50 · StrategyVersions :44-45 · CampaignStore :38/:78 | CORRECT | |
| What existing rows hold in `evaluator_version` | settled | Every row holds `backtest=1;metrics=1;scoring=1`. The column has been NOT NULL since the table was created at rung 15 (Database.cs:890). There is one writer (Referee.cs:309), of a constant that `git log -S` shows unchanged since 43ac196. Rows without it cannot exist |
| Anything else that reads `interpreter_build` | — | One comparison only (PromotionStore.cs:445). It is also one of the hashed facts of the promotion id (:148): keep it there as provenance |

GAPS
1. **The replacement is incomplete: the Manifest (HIGH).** `interpreter_build` is the app version plus LanguageVersion, and the app version moves with every release. So today, an indicator or calendar semantics bump also withdraws evidence. After item 1, nothing compares the Manifest. `Standing` never re-parses, and `ForwardRuns.Frozen` (:527-532) re-parses without checking that the id still equals `deployment.VersionId`. A Manifest bump would then keep the standing and keep paper runs trading under the new meaning, guarded only by golden-vector coverage. Add to `Invalidation`: the version row's `manifest` (StrategyStore.cs:199) differs from `StrategyVersions.Manifest` ⇒ withdrawn. And make `Frozen` end the run when the re-parsed id does not match.
2. **The precommitted policy texts promise the old rule.** V1 and PaperV1 bind evidence to "the interpreter build … a change to any of them invalidates" it (CampaignStore.cs:37-39, :77-79; COUNCIL.md:174). The brief rightly forbids editing those texts (Referee.cs:201-206). But then the code stops honouring one of their clauses under the same sha, which is exactly what Referee.cs:196-200 says the referee never does. CONTRACTS must define "interpreter build" in those texts as the evaluation semantics (EvaluatorVersion plus the Manifest) and name the protected property, as CLAUDE.md requires. That reading is only true once gap 1 is fixed.
3. Every golden vector must declare its ExecutionModel. Otherwise U-cost-model's research default, or a fee edit, moves the shas.

TRAP: `PromotionLedgerTests.A_promotion_from_another_interpreter_or_another_policy_does_not_stand` (:350-369) asserts that an `app=0.0.1` row is INVALIDATED. Item 1 inverts that. Rewrite it in place with the name kept, e.g. using a different evaluator version.

Also: `U-rejudge`, named in item 3, appears in no EDGE-FACTORY § 9 table. It needs a rung (R09 §4: the `strategy_verdict` primary key).

**Red-first:** (a) and (c) are red by reading. (b) is red only through mutant (ii). (d) is a guard.

**Feasibility:** yes, 60–90 min including gap 1.

## 6. U-timeframe (NEW) — WRONG 0 · IMPRECISE 2 · GAP 4

| Pointer / claim | Verdict | Correction |
|---|---|---|
| StrategyEvaluator :141-146 | CORRECT | `Start` is :142-156; the 1-minute default is at :145 |
| Backtest :328, :593 · ForwardRuns :135 | CORRECT | :593 passes `null`; :135 passes `ForwardBars.BarLength` |
| StrategyLimits :63, :71 | CORRECT | `MaxHistoryDepth` 20 (:64), `MaxLookbackBars` 500 (:71) |
| StrategyParser :96, :488 | CORRECT | The keyword is at :96 and the parse at :488-491. It is hashed at StrategyCanonical.cs:50 |
| "StrategyProgram.cs (canonical form and id)" | IMPRECISE | The canonical writer is StrategyCanonical.cs:35-79 (`Header "program/1"` at :32). The id is StrategyProgram.cs:50 |
| "PaperBook.cs (how resting stops fill)" | IMPRECISE | The fills happen in `PaperConnector.Apply` (:491-533) |

**Does a v1 program keep its canonical text and id?** Only if the `bars` line is written only when it is declared and is not `1m`. StrategyCanonical.cs:43-47 says the bounds are "ALWAYS STATED" so that an absence never equals a later build's reading. The brief must make `bars` the stated exception (absent means `bars 1m`: one meaning, one text) and leave `Header` alone. It must also keep `bars` out of the reserved NAMES: `IsReserved` (:104-107) includes every `IsKeyword` (:94-101), so adding `bars` there makes a stored program containing `const bars = …` stop parsing, and a paper run on it would end (ForwardRuns.cs:121-124).

**Can stops and targets keep 1-minute mechanics? Yes, but this code has to change:**
- `Backtest.Run` (:366-510) is one loop over one stream: fill the pending order at this bar's open (:393-428), apply protection on this bar (:430-446), then `Step` (:459-498). It needs a minute loop that calls `Step` only at declared closes, on the resampled bar. `held` (:433) and `BarsSinceEntry` (:457) must count declared bars, and the cap (:377) must count evaluated bars. Trace granularity needs a decision: one `Closed` event per minute (:464) is 525,600 events for a year, 2.6× the memory bound `MaxTracedBars` exists to keep.
- `StrategyEvaluator.Step` counts gaps in `BarInterval` units into `MissingMinutes` (:267-279), which feeds `strategy_run.missing_minutes`. The unit changes for `1h`.
- In `ForwardRuns.AdvanceOneAsync` (:156-294), stops and targets are resting orders at the venue (`ProtectAsync` :222, :361-385), and the paper connector fills them per minute (PaperConnector.cs:512-530). That part is fine. But three things run once per stepped bar: placing protection when the entry fills (:213-226), cancelling the losing protective order (:202-211), and the max-hold flatten (:240-250). **If item 4 is read as "the runner steps 24 times a day", a filled entry sits unprotected for up to an hour, and the losing protective sell rests under no position for up to an hour. When it fills, the paper book goes short: `PaperBook.ApplyToPosition` (:483) turns `held = 0` into `-qty`.** Keep the per-minute pass and hold back only `Step` until a declared close.
- `Backtest.Over` (:593) must pass the program's declared interval, or the referee judges on minutes.

GAPS
1. Item 4's wording, above. This is the money path: say "evaluator steps", and add a red-first test that protection lands within one minute of the entry fill.
2. The existing `timeframe` bound (STRATEGY-LANGUAGE.md:17, :32) versus `bars`: must the two agree? The brief does not say.
3. Trace granularity and the missing-minutes unit, above.
4. "A partial window is a bar carrying its minute count" means a new init property on `KlineBar` (DatasetReader.cs:7-25). Say it is additive.

**Red-first:** (a)–(d) and (f) are red on base, because `bars` does not parse. (e) is a guard. Both mutants are red by reading.

**Feasibility:** NO (about 2–3 h). Split it. **A:** the grammar, the canonical form, resampled backtests and referee runs, and the limits, with the runner REFUSING to deploy any program whose `bars` is not `1m`. **B:** the runner evaluating on declared bars.

## 7. U-venue-verify (revised) — WRONG 0 · IMPRECISE 1 · GAP 3

| Pointer / claim | Verdict | Correction |
|---|---|---|
| VenueCatalog :105/:174-186 · PaperConnector :90-110/:101/:181-215/:203/:215/:320-330/:326 · ForwardRuns :492-516/:512 · VenueStore `Sync` :71-115 via TradingGateway :1563 · Backtests :341-396/:356/:390-396 · USER-GUIDE :935-963 · Collector :46/:52/:243-257 · SettingsView :178-215 | CORRECT | The card runs on to :221. Platforms/Connectors.cs:87-90 builds the paper connector's options |
| "Add the host to SuiteReachesNoVendorTests.cs:40-66" | IMPRECISE | exchangeInfo is served from `data-api.binance.vision`, which the scan already covers as `ForwardHost` (:66). There is nothing to add |

GAPS
1. **The definition URL is agent-writable (HIGH: evidence).** The unconfined CLI can write `venues.json` (Containment.cs:43), so a URL there can point the check at any host. The row is then served as "verified against Binance", and U-cost-model pins the judge's increment from it. Verify only when the URL's origin is the built-in row's origin (the U-key-host-pin rule); otherwise record the check and do not verify. (An agent can already write `"verified": true` today. That stays advisory until containment, and CONTRACTS should say so.)
2. More texts tell someone to edit venues.json: Protocol.cs:54-57 (agents read it), VenueCatalog.cs:150-156 and PaperConnector.cs:189-190. Item 4 names only three others.
3. The served row gains `min_notional`, but VenueCostModel v1 ignores it. State as NOT claimed that a size clears the 5 USDT minimum notional.

**Red-first:** (a) and (e) are red by reading.

**Feasibility:** borderline (90–120 min). Cut the daily re-check first.

## 8. U-tape-store (NEW) — WRONG 0 · GAP 5

| Pointer / claim | Verdict | Correction |
|---|---|---|
| PaperBook :39, :68-77, :39-124 | CORRECT | But `Migrate` (:69) runs BEFORE the newer-file check (:71-75). tape.db must check its version first |
| AppHost :552-556, :476-560 · CandleSourceCatalog :83-235 · SettingsView :178-215, :190-196 · SuiteReachesNoVendorTests :40-66 · FakeArchive | CORRECT | fapi.binance.com is a new host, so adding it to the scan is right |

GAPS
1. The override file for unkeyed sources is agent-writable. A redirected host that serves fabricated context on time would be classed O-LIVE. Record the origin of every fetch, and class rows from a non-built-in origin as O-ARCH.
2. The first-start backfill: 30 days of 5-minute rows is about 8,640 rows per series per symbol, and these endpoints page (500 rows at most per call, per Binance's docs, not re-checked here). That is about 300 calls on first start. Cut the backfill to U-tape-archive; its rows are O-ARCH anyway.
3. `AsOf` takes no audience, but EDGE-FACTORY § 4.1 says every tape reader takes a required one. Put it in the store now.
4. "A newer file is a status line" cannot hold here: `status.tape` belongs to U-tape-read (W5). In this unit it is an activity line (AppHost.cs:552-556). Fix the wording of test (e).
5. "TapeStore is the ONLY writer" conflicts with U-decision-port writing `decision_call` into tape.db. Say that rung-2 writes go through TapeStore.

Not in § 9's table: a soft dependency on U-runner-forward's alignment helper. W2 comes before W3, so it is satisfied.

**Feasibility:** yes, about 90 min, once the backfill is cut. Not with it.

## 9. U-tape-read (NEW) — WRONG 0 · GAP 0

| Pointer / claim | Verdict | Correction |
|---|---|---|
| Protocol :47, :14-141 | CORRECT | `Ops` is :12-143 |
| GatewayPipeServer :331-333 (the drain table) · :1228-1230 · :300-360, :1180-1240, :2587-2770 | CORRECT | The table is `HandlerPaths` (:308-353). `DataList` is at :2587 and `DataBars` at :2637 |
| GatewayPipeBackpressureTests :1083 | CORRECT | PnlOverPipeTests.cs:188 also requires a drain row for every GatewaySchema op |
| GatewayTypes :397 · TradingGateway :2335-2365 · DailyReport :547-752 · DatasetReader :49-86 | CORRECT | |

Test (d) restates the :1083 guard, which is fine.

**Red-first:** new code; the mutant is red by reading.

**Feasibility:** yes.

## 10. U-decision-port (revised) — WRONG 0 · IMPRECISE 1 · GAP 4

| Pointer / claim | Verdict | Correction |
|---|---|---|
| AiAttemptStore :119-135 (`AiAdmissionRule`) | IMPRECISE | The record runs :119-162 (`Reservation` :136, `Metered` :141, `Reading` :149-161) |
| :225-290 (`Begin`) | CORRECT | `Begin` is :256-281. The totals are read inside the same `db.Write` as the INSERT |
| TurnMeter :820-845 (`RuleFor`) · RuntimeManifest :384, :495-686 · FakeProvider · SuiteReachesNoVendorTests :40-66 · the absence grep | CORRECT | `RuleFor` is :824-839 and `Share` :849-853. The grep finds 0 hits |

**Every reader of `ai_attempt.role`, and what a `perception` role does to it:**
- `AiAttemptStore.Totals` (:441) filters on `COALESCE(role,'operations') = $role`. Perception rows stay out of both council roles' totals and inside the day total. Correct.
- `AiAttemptStore.Refuse` (:297-299) builds `RoleShareReached(Title(CouncilRoles.Or(rule.Role)))`. A perception budget refusal would name the "Operations Director". Wrong words.
- `CouncilRelay.Fence` (:500) checks `CouncilRoles.Or(a.Role) != role`. A perception attempt id therefore passes as an OPERATIONS launch, and ENDED or LOST launches publish (:501). That is a provenance hole by construction: an unknown role must be quarantined.
- `DailyReports.ComposeRecovery` (:878-882) labels an interrupted perception attempt "Operations Director attempt …".
- The day counters (AiAttemptStore.cs:431-439) count perception rows as `Turns`, which reach status as `AiTurnsToday` (TradingGateway.cs:2342). A perception row ENDED with `effective_model` NULL counts as `Estimated`, which flips the council card to "priced at the highest list price" (TurnMeter.cs:1056). Settle each row with the answered model id.
- The `TurnMeter` constructor calls `LoseOpen` (:575), which declares any LAUNCHED perception row LOST unless it is registered in `LiveAttempts.Shared` (:389). `End` then refuses the billed cost.

Admission itself is sound: the role cap is the perception budget, and the day cap is shared.

GAPS
1. "Fix any TurnMeter share or report" is too narrow. Name `Refuse`, `Fence`, `ComposeRecovery`, the day counters and `LiveAttempts`.
2. `LocalDay` and `Midnight` are private to TurnMeter, but the port needs the owner's local day for `From`, `To` and `ResumesAt`.
3. Core holds no HTTP client today. Say where the adapter lives: AgentRuntime or Provisioning.
4. `decision_call` must be written through TapeStore (see U-tape-store gap 5).

**Red-first:** (f) is red only by failing to compile. Mutant (ii) is now specified (the loopback host reads the ledger when the request arrives), so it is red by reading.

**Feasibility:** borderline no (about 2 h). Cut the Perception card's Test press (item 5) into a follow-up unit, or ship one host first.

## 11. Against EDGE-FACTORY v2 § 9

- **Names** match for all ten. **Dependencies** match, plus U-tape-store's unstated use of U-runner-forward's alignment helper (satisfied, because W2 comes before W3). `U-rejudge` is missing from § 9.
- **Rungs:** on the main ladder, 27 is U-cost-model (W1) and 28 is U-venue-verify (W4), so ladder order holds. On tape.db, 1 is U-tape-store (W3) and 2 is U-decision-port (W5), so that order holds too. No other brief adds a rung: U-evidence-identity defers its re-judge rung, and U-timeframe needs none.
- **W1** key-host-pin ∥ cost-model: LOW. They share only `Labels` and CONTRACTS/USER-GUIDE, in different sections.
- **W2** runner-forward ∥ evidence-identity: LOW. If evidence-identity takes on its gap 1, it edits `ForwardRuns.Frozen` (:527-532) while runner-forward pages `AdvanceOneAsync`: same file, different methods.
- **W3** timeframe ∥ tape-store: LOW (CONTRACTS only). But U-timeframe must split first.
- **W4** venue-verify ∥ paper-friction: MED. Both edit `AppHost.PaperChoice` (:759-770), `Platforms/Connectors.cs:87-90` (`PaperConnectorOptions`), the `StatusAsync` initializer (:2335-2365) and `GatewayStatus` (GatewayTypes.cs:397). Land venue-verify first, as planned.
- **W5** tape-read ∥ decision-port: MED. Both add a `GatewayStatus` field, a line in the `StatusAsync` initializer and a daily-report line (DailyReport.cs and DailyReports.cs), and both may edit TapeStore.cs (range reads versus rung 2). Land tape-read first.
- **Wave change:** the pairings stand. With the splits above:
  - W1: key-host-pin ∥ cost-model (cut down).
  - W2: unchanged.
  - W3: timeframe-A ∥ tape-store (backfill cut).
  - W4: venue-verify ∥ paper-friction, with cost-model's research default moved in.
  - **New W4b: timeframe-B alone (or beside the light R-containment), before M0**, because M0 depends on all of phase 0. It edits `AdvanceOneAsync`; venue-verify's ForwardRuns edit is `Increment` (:512).
  - W5: tape-read ∥ decision-port, with the Test card cut.
