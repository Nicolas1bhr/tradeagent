# 18 — The five organisation briefs and three edited briefs, checked against `main` (read-only)

HEAD `1275aff0ff0ca5f9d0be9d94fa3d85e6129b2280`. `src/` and `tests/` were read there; `git diff 0f47db7 1275aff -- src tests` is empty, so the older briefs'
`0f47db7` pointers hold. The briefs were read as they stand on disk; the queue was last written at 13:22. `docs/ORGANISATION.md`, `docs/EDGE-FACTORY.md` and
`docs/RESUME-HERE.md` changed at 13:51–13:52, after R17. §§ 2–3 check that CURRENT text, and the earlier text where it differs. Nothing was built or run, and
nothing was edited except this file. **Labels:** **SOURCE** = `file:line` at `1275aff` (abbreviated as R12: `Core/` = `src/TradeAgent.Core/`, and so on);
**RUN** = a read-only `git grep`/`grep`/`wc`/`awk` by this leg; **INFERENCE**; "red by reading" = traced through the code, not run.
**Verdicts:** WRONG, IMPRECISE, GAP (a decision left to the builder), TRAP (an existing test the brief breaks without naming it).
Where R17 reached the same point by design review, the row cites it.

## 0. Ten-line answer

1. **U-price-rows — ready with corrections (7).** Red-first (a) is red by reading, but only on `codex` ($16.00 at `gpt-6-astra`; the harness's dearest row is `gpt-5.5-pro`, $39.60). Two existing tests go red without being named. Moving `ReadOn` re-dates all 18 rows. The promotional-price rule can be read both ways (= R17 #29).
2. **U-org-ledger — ready with corrections (10).** (b) is false: `referee` and `allocator` are historical role values. (c) cannot go red as worded, and the `org_event` seed is not idempotent. (e) names no existing test. Two roll-back tests must drop the new tables. The fixed "Schema 28" now contradicts ORGANISATION § 15's "next free rung". No agent-facing op can write org rows.
3. **U-org-principals — ready with corrections (8); a split is recommended.** Mutant (iii) cannot go red, because the third position is a member, not a head. "Seeded through OrgStore" is impossible. If `HomeDir` read the row, ≈33 lines in 15 test files would break and a row could name any folder: keep `HomeDir` static and total. Two seams are missing (`AgentSupervisor` `BuildAll`; `CouncilRelay.Reconcile`).
4. **U-org-envelopes — NOT ready as written (HIGH, spend).** Item 3 prices a reservation at a seat allowance the harness never enforces, behind one role-blind bound (`AppHost.cs:137-139` → `ApiConversation.cs:311`), and nothing reads `seat_model`/`seat_runtime`. Mutant (ii) is not deterministic. NULL-role history and non-position spend are unplaced (= R17 #11). It is stale against § 15's "day-total gate kept first; splits that cannot fund their head refused" (R17 #1). Its "sixteen positions" claim does not describe today's code.
5. **U-org-wakes — ready with corrections (8).** Red-first (a) is green on base. Fairness already exists, so (c) is a guard except for its tie rule. The "requesting position" is recorded nowhere. A non-legacy head's interval is unstated (= R17 #10). Positions must reach the loop through a default `IMissionHost` member.
6. **Edited briefs.** `U-venue-verify`: the edit introduced a WRONG line — the card and `StartAsync` are shared with `U-tape-store`, not `U-cost-model` — and its "Schema 29, after U-org-ledger" is now stale (R17 #23). `U-cost-model`: rung 27 carries a runtime trap. `U-decision-port`: the order is consistent; after O2, three of the four halves of its test (e) are green on base.
7. **Ladder.** 27 = `U-cost-model` is fixed everywhere. Since 13:51 the plan says 28 and 29 are "the next free rung at landing" (ORGANISATION:345-347, RESUME-HERE:25, EDGE-FACTORY:431). Five brief lines and EDGE-FACTORY:376 still fix 28/29 and make lane A wait for lane B. R12 (six places) keeps the pre-renumbering order.
8. **A ladder trap that no rung brief names.** `VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275` roll a file back by listing every later rung's additions, then reopen it. Rung 27's `ADD COLUMN`s fail on reopen unless appended there ("duplicate column"), and 28/29 belong there by those tests' own rule.
9. **Waves (current W1–W11).**
   - Every pair with a new brief is L, D or none.
   - W4 `U-venue-verify` ∥ `U-paper-friction` is R11's MED pair: land VV first.
   - W7 puts `U-decision-port` beside the light `U-billing-classes` (CARD), and both would edit the admission path (H): order them.
   - W1 runs three builders on this Mac, so builder gates must be serialised.
10. **Before dispatch.** Commit `docs/ORGANISATION.md`, the five briefs and R12–R18 (untracked, so a worktree from `main` lacks them). Paste the rung lines (§ 2). Split O2 into O2a (W5) and O2b (W6, light). Cut O4 item 3 into a CARD `U-org-seats`.

**Sizes and gate (RUN `wc -l`):** price-rows 24, org-ledger 28, org-principals 27, org-envelopes 29, org-wakes 25, venue-verify 36, cost-model 36, decision-port 40 — all ≤ 40.
**Gate paragraphs:** each carries HOW-WE-BUILD.md's terms — rebase first, `--no-incremental` Release build at 0 warnings, suites at 0 failed, the names-removed check, a report of ≤ 20 lines. The money-adjacent briefs carry red-first tests and at least one watched mutant. Every brief stays ≤ 40 lines after the pastes below (INFERENCE).

## 1. Corrections, per brief

### 1.1 U-price-rows — IMPRECISE 3 · GAP 2 · TRAP 2

**Verified CORRECT:**
- `ReadOn = "2026-09-06"` (`AgentRuntime/ListPrices.cs:44`); the URLs (`:47`, `:50`).
- No `gpt-6-luna`, `gpt-6-sol` or `gpt-6.1-sol` row in `OpenAi` (`:79-99`) or `CodexModels` (`:115-119`).
- `Highest` (`RuntimeManifest.cs:963-971`), `Applicable` (`:929-940`), `Belongs` (`:946-948`); `TurnMeter.Reservation` (`:881-897`); `TurnAllowance` (`Trading.cs:689-691`).
- $16.00 = 1.2 M × 12.50 + 20 k × 50 (`Bound`, `RuntimeManifest.cs:1161-1166`); $39.60 = 1.2 M × 30 + 20 k × 180. Both are over the $5 cap (`Trading.cs:841-842`).

**Red-first path (asked):** red by reading, on `codex`.
- `Reservation` calls `CostCatalog.Reserve(…, requestedModel: ModelOf(role))` (`TurnMeter.cs:893-894`).
- In `Compute`, no model is named or declared (`RuntimeManifest.cs:1061`), so `askedFor` = the requested model (`:1070`).
- No row matches it (`:1091-1093`), so the dearest row is taken: `Highest`, by output then input (`:1099-1101`).

| # | Location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| 1 | L19 (a) | IMPRECISE. "Astra" holds only on `codex`. Opencode and the harness price every OpenAI row (`ListPrices.cs:137-147`), and their dearest is `gpt-5.5-pro` | SOURCE `RuntimeManifest.cs:1099-1101` | "(it reserves at the Astra row)" → "(on `codex`: `CostCatalog.Reserve(TurnAllowance.Default, "codex", requestedModel: "gpt-6-luna")` is 16.00 labelled `PricedAtHighestListPrice` at base and 0.16 after; the class sits in `[Collection(VendorOverrideFiles.Name)]` like `ShippedListPriceTests`, `TurnMeterTests.cs:531`)" |
| 2 | L20 "existing tests" | TRAP. `The_rows_the_vendor_prices_cache_writes_for_carry_that_figure_and_the_rest_carry_none` requires a NULL cache-write rate on every row outside its four-entry dictionary. The GPT-6 rows carry 0.125 and 2.50 | SOURCE `TurnMeterTests.cs:574-600` | append: "; that test's cache-write dictionary (`TurnMeterTests.cs:576-582`) gains each new row's published figure, rewritten in place, name kept" |
| 3 | L17 "update `ReadOn`" | GAP (honesty). `Price()` stamps EVERY row with `ReadOn`, so moving it claims all 18 old rows were re-read ("Read, not remembered", `:16-18`) | SOURCE `ListPrices.cs:164` | "update `ReadOn`;" → "update `ReadOn` only after re-reading EVERY existing row that day (it dates every row, `ListPrices.cs:164`); quote old → new for any figure that moved and rewrite the figures it pins (`RequestedModelPriceTests.cs:37,40`, `TurnMeterTests.cs:576-582`) in place, names kept;" |
| 4 | items, L22 | TRAP. `MissionCostSurfacesTests.cs:374-391` and `:399-406` assert that `ReadOn` appears in `USER-GUIDE.md` (today at `:475`, `:1087`) and `RESEARCH-REQUIRED.md` (today at `:209`). The brief names no doc, and "no other file overlaps" is false | SOURCE as cited | add "4. `docs/USER-GUIDE.md` (`:475`, `:1087`) and `docs/RESEARCH-REQUIRED.md` § D (`:209`) carry the new day and rows."; L22 "no other file overlaps a queued brief" → "shares only `USER-GUIDE.md`/`RESEARCH-REQUIRED.md` with W1's `U-cost-model` and `U-key-host-pin` (other sections; rebase)" |
| 5 | L15-16 promo | GAP (spend; = R17 #29). An under-charge is the direction that walks past the cap | SOURCE `ListPrices.cs:68-70` | "a promotional price is taken only with its end date in a comment, and the standard price is the one that bills after it" → "a row carries the STANDARD price; a promotional price appears only in a comment with its end date" |
| 6 | item 2 | IMPRECISE. The rows also reach the harness, whose count must equal opencode's. On chat-completions, GPT-6.1 Sol calls no tools, and GPT-6 Luna/Sol call them only at reasoning `none` (R15 § 2.1, DOC[O4]) | SOURCE `HarnessCatalogTests.cs:77-92` | append to item 2: "NOT claimed: that a harness turn on a GPT-6 model can call tools before `U-harness-responses` (`docs/ORGANISATION.md:363`)." |
| 7 | L20 (b) | IMPRECISE. Green on base: every row is stamped, and the harness case is already asserted | SOURCE `HarnessCatalogTests.cs:89`; `TurnMeterTests.cs:549-561` | "(b) `Every_built_in_row_carries_the_read_on_day`" → "(b) `Every_built_in_row_carries_the_read_on_day` (a GUARD, green on base)" |

### 1.2 U-org-ledger — WRONG 3 · IMPRECISE 6 · GAP 1 · TRAP 1

**Verified CORRECT:**
- `DatabaseSchemaVersion = 26` (`Versioning.cs:274`); `Migrate` (`Database.cs:103`), rung 25 (`:1431`), rung 26 (`:1511`), the newer-file refusal (`:1607-1610`).
- The role-valued columns, none with a CHECK: `:521` ai_attempt, `:522` mission_event, `:526` publication, `:540` delivery.recipient, `:630` strategy_version, `:658` strategy_run, `:707` tool_call, `:956` boundary_submission. That makes eight tables, counting `recipient`.
- `AgentGrants.Issue(string role, …)` (`AgentGrants.cs:147`); `Council.cs:32-41`. No name collision (RUN: `git grep -i "OrgStore|org_unit|org_position|org_event"` is empty).

**Can an agent-facing op write org rows? No app path:**
- `OrgStore` has no public writer (item 2).
- None of the 33 ops in `Ops` (`Protocol.cs:14-138`) touches the org tables, and the dispatch default refuses every other op (`GatewayPipeServer.cs:1244`).
- A CLI seat can still write `state/`; item 3 says so.

| # | Location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| 1 | L23-24 (b) | WRONG. Historical non-NULL roles include `referee` (`strategy_run` at `Referee.cs:301`; `publication` at `:415`) and `allocator` (`TradingGateway.cs:278`, written at `:424` and `:1161`). U-org-principals item 3 says neither ever becomes a position | SOURCE as cited | "every distinct non-NULL `role` in a schema-26 fixture maps to a position id" → "every distinct non-NULL role value today's stores write (the eight columns) is a seeded position id or an app principal — `referee` (`Referee.cs:81`) or `allocator` (`TradingGateway.cs:278`) — which `OrgStore` names as app principals, never as positions" |
| 2 | L23 (a) | IMPRECISE. There is no older-schema fixture harness: the "arrives at schema N" tests use `TestEnv.NewDb()` (e.g. `BacktestLedgerTests.cs:203-224`). The base at dispatch is 27 | SOURCE as cited | "`An_upgrade_from_26_seeds_one_root_two_divisions_and_the_two_legacy_positions` (quoted red at base: no tables)" → "`An_upgrade_seeds_one_root_two_divisions_and_the_two_legacy_positions` (on `TestEnv.NewDb()` and on a file stamped back by raw SQL as `PaperEligibleVerdictTests.cs:251-276` does; red at base: no tables)" |
| 3 | L25-26 (c) + mutant | WRONG. `have` is read once (`Database.cs:106`) and every rung is `if (have < N)`, so a second open never re-runs the seed, and the mutant cannot go red. A re-run happens only after a crash between the seed and the stamp, because rungs run in autocommit (`:37-42`) | SOURCE as cited | "`Upgrading_twice_seeds_nothing_twice`" → "`Rerunning_the_ledger_rung_seeds_nothing_twice` (upgrade, write the previous rung's number into meta, reopen: one row and one `org_event` per seed)" |
| 4 | L17-18 item 1 | GAP. `org_event.id` is an INTEGER key, so ON CONFLICT on units and positions still lets a re-run add five seed events. Also R17 #30: `detail` is unspecified | INFERENCE (item 1's own schema) | "one `org_event` per seed with decider `app`" → "one `org_event` per seed with decider `app`, inserted `WHERE NOT EXISTS` a seed event for that id; `detail` holds app-composed ids, numbers and fixed words only" |
| 5 | (unnamed) | TRAP. Two tests undo "EVERY rung above" by an explicit list, then reopen (their rule: `:206-209` and `:255-258`) | SOURCE `VenueCatalogTests.cs:214-249`; `PaperEligibleVerdictTests.cs:259-275` | add: "Append `DROP TABLE org_event; DROP TABLE org_position; DROP TABLE org_unit;` to the roll-back lists at `VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275`, rewritten in place, names kept." |
| 6 | L25-26 (e) | WRONG. No ledger "schema/reconciler check" exists. `SchemaMatchesReconcilerTests` compares the pipe schema with the ORDER reconciler | SOURCE `IntegrationTests/SchemaMatchesReconcilerTests.cs:21-28` | "(e) the schema/reconciler check extended" → "(e) `The_organisation_ledger_arrives_at_its_rung` — floor at the rung, meta equals `Versions.DatabaseSchemaVersion`, the three tables exist (the `BacktestLedgerTests.cs:203-224` pattern)" |
| 7 | L3, L13 | IMPRECISE (stale since 13:51). ORGANISATION now gives O1 "the next free rung at landing (expected 28)", so neither lane waits on the other's number (R17 #23). R12 § 5, which the brief cites, still says O1 = 29 after `U-venue-verify` | SOURCE `ORGANISATION.md:345-347`; R12:146, :152, :180 | "**Schema 28. Dispatch only after `U-cost-model` (schema 27) has landed; `U-venue-verify` becomes schema 29 and lands after this — never a higher rung before a lower one.**" → "**Schema: the next free rung at your rebase (expected 28, after `U-cost-model`'s 27); if `main` gained it first, renumber on rebase — the ladder stays contiguous (R12's numbers predate this).**"; item 1 "Schema 28," → "The rung:" |
| 8 | L9, L20, L21 | IMPRECISE. The seed is written by `Database.Migrate`, not `OrgStore`, and five ids are fixed, not two | INFERENCE (items 1–2) | "`OrgStore` is the only writer" → "the app is the only writer (the rung's seed, then `OrgStore` from `U-org-verbs`)"; "the two legacy ids are the only fixed" → "the five seeded ids are the only fixed ones"; item 3 "one writer" → "app-only writers" |
| 9 | L25 (d) | IMPRECISE. Green on base (no op exists), so it is a guard. The precedent checks op names | SOURCE `RefereeVerdictTests.cs:519-545` | "— enumerates `Protocol` ops and the dispatch table" → "(a GUARD; over `GatewaySchema.Ops()` as `RefereeVerdictTests.cs:519-545` does)" |
| 10 | L14 `envelope_share` | IMPRECISE (naming). "Envelope" already names the paper-allocation grant | SOURCE `Database.cs:1458`; `TradingGateway.cs:268` | "envelope_share NULL (decimal string)" → "envelope_share NULL (decimal string; the AI-spend envelope, not `paper_envelope` — CONTRACTS says so)" |

### 1.3 U-org-principals — WRONG 2 · IMPRECISE 3 · GAP 3

**Verified CORRECT:**
- RUN: 121 `CouncilRoles.` lines in 29 src files, 635 in 50 test files.
- `Or` (`Council.cs:53`) and its uses at `TurnMeter.cs:1124`, `MissionLoop.cs:1245-1263, 2277`, `MissionEventStore.cs:192`, `CouncilRelay.cs:500`, `AiAttemptStore.cs:299` and `DailyReports.cs:881`. The last two are wording, not keys.
- `HomeDir` (`:80`); `resume --last` filtered by working directory (`RuntimeManifest.cs:572-575`); routing (`CouncilRelay.cs:587-608`).
- `RoleOf` (`Backtests.cs:209-216`, called from `:92` and `GatewayPipeServer.cs:2454`); `MayPlaceOrders` (`Council.cs:63`; `GatewayTypes.cs:270`).

**Red by reading at base:**
- (a) `Paths.RoleHome("x")` is `workspace/agent` (`Paths.cs:50` → `Council.cs:80`).
- (b) `_open[Key("x")]` is the chair's slot (`TurnMeter.cs:806`, `:1124`).
- (d) The third position writes into `agent/out/`. Its file passes `Fence`, because `Or("x")` = `operations` (`:500`), and goes to Research (`:607-608`).

**Blast radius (asked):**
- If `HomeDir`, `Paths.RoleHome` and `BuildAll` keep their signatures, no test file breaks at compile time. Tests use the constants and the static `HomeDir`, none calls `Or` (RUN), and `RefereeVerdictTests.cs:207` holds.
- Read literally, "HomeDir reads the row" would break 5 src + 18 test lines in 6 files (`HomeDir`) and 2 src + 15 test lines in 10 files (`RoleHome`): ≈33 lines in 15 test files.
- No constructor may gain a required argument: `new MaterialScanner(` has 50 call sites, `new MissionLoop(` 52, `new TurnMeter(` 17. `MaterialScanner` and `CouncilRelay` already hold the `Database` (`MaterialScanner.cs:25`, `CouncilRelay.cs:98`).

| # | Location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| 1 | L8-9 | WRONG. `OrgStore` has no writer, and Core grants no `InternalsVisibleTo` (only `AgentRuntime.csproj:23` and `App.csproj:17` do) | SOURCE as cited | "seeded by a TEST through `OrgStore`" → "seeded by a TEST with raw SQL through `Database.Cmd` (`Database.cs:95`, as `DispatchRecoveryTests.cs:294` writes rows)" |
| 2 | L22-24 (c) + mutant (iii) | WRONG. The third position is a member, so "MayPlaceOrders by headship" still says no, and (c) stays green under the mutant | SOURCE `Council.cs:63`; `GatewayTypes.cs:270` | "(c) `A_third_position_may_backtest_and_is_refused_orders`" → "(c) `A_third_position_may_backtest_and_is_refused_orders` — it HEADS a test-seeded team under `div-research`, and `research` (head of `div-research`) is asserted refused too" |
| 3 | L15 item 2 | GAP (blast radius and protection). A home path read from `state/`, which the CLI agent can write until containment, can name any folder | INFERENCE; RUN counts | "Homes: `HomeDir` reads `org_position.home_dir` (legacy `agent`/`research` unchanged); new positions live under `workspace/org/<id>/`" → "Homes: `CouncilRoles.HomeDir` stays static and total — `operations` → `agent`, `research` → `research`, any other id → `org/<id>` (id checked as `[a-z0-9-]+`, never a path read from a row); `org_position.home_dir` records the same value; whether an id is an ACTIVE position is checked where a `Database` is held (launch, `Backtests.RoleOf`, the relay)" |
| 4 | L16 walkers | GAP. `AgentSupervisor.cs:78,140` are the only `BuildAll` callers, so without them no third home is built. `Reconcile` walks `All` (`CouncilRelay.cs:250`), so a crashed third position's output is never fenced. `:79` is the legacy-only move; the home line is `:76` | SOURCE as cited | "`WorkspaceBuilder` (`:50, 79, 91, 104-105`), `MaterialScanner` (`:99-102`) and `AppHost` (`:263-264`)" → "`WorkspaceBuilder` (`:50, 76, 91, 103-105`; `BuildAll` takes an optional positions argument, default the legacy pair), `AgentSupervisor` (`:78, 140`), `MaterialScanner.RolePaths` (`:99-102`, from its own `Database`), `CouncilRelay.Reconcile` (`:250`) and `AppHost` (`:263-264`); `Paths.EnsureAllVerbose` (called inside `StartAsync`, `AppHost.cs:512`) is left as it is" |
| 5 | L13 item 1 | IMPRECISE. `All` is also live in the two-senior boundary ceremony (R12 seam 19: "kept") and the default 1/N share | SOURCE `BoundaryStore.cs:423,602,622,818`; `Trading.cs:314` | "stay for HISTORY only" → "stay as the legacy pair — history, the two-senior boundary ceremony (`BoundaryStore.cs:423,602,622,818`, unchanged) and the default share (`Trading.cs:314`) until `U-org-envelopes`" |
| 6 | L8 | IMPRECISE. It is wording only ("for the refusal's wording and for nothing else"), and it would call a valid third position "a caller that proved no role" | SOURCE `Holdout.cs:68,73` | "`Holdout.cs:73`)" → "`Holdout.cs:73`, wording only — name an active position by its id there)" |
| 7 | L24 | GAP. See "Blast radius" above | RUN | "Existing tests break only at compile time where a signature changed; rewrite in place, names kept." → "Keep `HomeDir`, `Paths.RoleHome`, `BuildAll` and every constructor source-compatible (optional arguments only); `Backtests.RoleOf` (two callers) may change; a test that still breaks is rewritten in place, name kept." |
| 8 | L25 conflicts | IMPRECISE (stale against the current waves). VV is now W4, before M0; this unit's W5 partner is `U-tape-read` | SOURCE `ORGANISATION.md:377`; TR brief items 1–2 | "`U-venue-verify` shares `Backtests`, `Protocol` texts and `AppHost.StartAsync` (L — rebase; land whichever is first)" → "`U-tape-read` (W5) shares `WorkspaceBuilder.cs` (instructions against `:50-105`) and `GatewayPipeServer.cs` (`:308-353`, `:2587-2770` against `:2454`); L — rebase" |

### 1.4 U-org-envelopes — WRONG 1 · IMPRECISE 1 · GAP 5 (one HIGH)

**Verified:**
- `AiAttemptStore`: `Begin` (`:256-281`; totals and INSERT in one `db.Write`), `AiAdmissionRule` (`:119-162`), `Refuse` (`:288-305`), `End` (`:312-349`), LOST (`:387`). `Totals` runs `:425-454`; the brief says `:425-445`.
- `TurnMeter`: `RuleFor` (`:824-839`), `Share` (`:849-853`), `Reservation` (`:881-897`).
- `Trading.cs`: `:297`, `:311-314`, `:468`, `:689-691`, `:871-872`. `AppHost.cs:636-650`, inside `ComposeTheAi` (`:622-708`).

**Concurrency (asked):**
- `Database.Write` holds one process lock and nests (`Database.cs:68-84`), so two `Begin`s never interleave inside the transaction.
- `BudgetReservationTests.cs:65-106` races two threads, with a `Barrier(2)` after the cheap first look (`meter.Today`) and before `meter.Begin`. It proves only that the first look is not the gate.
- Nothing holds a thread between a read outside the transaction and `db.Write`, so that mutant goes red by scheduling luck. The deterministic form is the stale look (`:112-127`).

| # | Location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| 1 | L10-11, item 3 | GAP, HIGH (spend). The harness enforces one role-blind bound from settings (`AppHost.cs:137-139` → `ApiAgentRuntime.cs:53,175` → `ApiConversation.cs:311`), so a reservation priced at a smaller seat allowance is below what the turn may spend. Nothing reads `seat_model`/`seat_runtime`: the meter prices `RequestedModelFor`/`RuntimeForRole` (`AppHost.cs:648-653`), and the conversation runs `Settings.ModelForRole` (`:249`). `TurnMeter.cs:890-892` says why a reservation must use the model the turn runs on | SOURCE as cited | Cut item 3 and test (e) into a CARD `U-org-seats` ("the meter, the conversation and the harness bound read one seat: model, runtime, allowance"). In the observable result, "a seat's own allowance, when set AND enforced (harness seats), prices its reservation, while a CLI seat keeps 1.2 M (R15 § 3c);" → "every launch still reserves the global allowance (seat allowances are `U-org-seats`);" |
| 2 | L20 (if item 3 stays) | GAP. "Cannot price" is undefined. Today a model with no row falls back to the dearest row when asked for, and only a runtime with no catalogue is `Unknown` | SOURCE `RuntimeManifest.cs:1088-1089,1099-1101` | "is not seated (refused in words)" → "is refused before `Begin` when its reservation is `TurnPrice.Unknown` or the dearest-row fallback (`Labels.PricedAtHighestListPrice`)" |
| 3 | L22 (a), L25 mutant (ii) | GAP. See "Concurrency" above | SOURCE `BudgetReservationTests.cs:65-106,112-127`; `AiAttemptStore.cs:108-114` | "— concurrent `Begin`s, quoted red at base;" → "— two threads as `BudgetReservationTests.cs:65-106`, through `TurnMeter.Begin(prompt, role: <position>)` with org rows inserted by SQL (compiles and is red at base), plus a sequential half: two rules built on an empty ledger, then two `AiAttemptStore.Begin` calls (the `:112-127` pattern);" and "ancestor totals read outside the transaction ⇒ (a) red" → "subtree totals computed in `TurnMeter.RuleFor` and carried in `AiAdmissionRule` (the defect `AiAttemptStore.cs:108-114` names) ⇒ (a)'s sequential half red" |
| 4 | L16 item 1 | GAP. Today's role filter `COALESCE(role,$chair) = $role` counts NULL-role history as Operations. A `role IN (ids)` form drops it, and the legacy pair no longer admits "exactly as before" | SOURCE `AiAttemptStore.cs:441-443` | "`Totals` gains a subtree form over the subtree's position ids;" → "`Totals` gains a subtree form over the subtree's position ids, reading a NULL role as `operations` exactly as `:441-443` does, with membership read inside the same `db.Write`;" |
| 5 | L8-10, item 1 | GAP (cross-brief; = R17 #11). `U-decision-port`'s `perception` and the watcher are no positions. ORGANISATION § 15 now also promises "the day-total gate kept first" and "splits that cannot fund their head refused" (R17 #1b), which the brief does not carry | SOURCE `ORGANISATION.md:353`; `AiAttemptStore.cs:130-133` | append to item 1: "The existing day-total gate over every `ai_attempt` row stays first and unchanged; a rule with no position (null role, `perception`) keeps its `RoleCap`; for a position the chain replaces `RoleCap`." Then either add R17 #1(b)'s item and red test (`A_split_that_cannot_fund_its_own_head_is_refused`) or drop the clause from § 15 |
| 6 | L7 | WRONG about today. N is `CouncilRoles.All.Length` = 2 for any role string | SOURCE `Trading.cs:311-314` | "At luna's 0.324 USD reservation under the 5 USD cap, sixteen or more positions could never launch (R12 § 4, RUN there)." → "Today 1/N is `1 / CouncilRoles.All.Length` = 1/2 for ANY role string (`Trading.cs:314`), so every new position silently gets half the day; were N the position count, at luna's 0.324 USD sixteen or more could never launch (R12 § 4, arithmetic)." |
| 7 | L26 conflicts | IMPRECISE (stale). PF is now W4; this unit's W6 partner is `U-features` (CARD) | SOURCE `ORGANISATION.md:378` | "`U-paper-friction` shares `Trading.cs` (L — rebase)" → "`U-features` (W6, CARD) — re-check at its briefing; `U-paper-friction` (W4) lands earlier, sharing `Trading.cs` and `AppHost.cs` (other methods)" |

### 1.5 U-org-wakes — WRONG 1 · IMPRECISE 4 · GAP 3

**Verified:**
- `Schedule` runs `:1853-1888` (the brief says `-1882`) and walks `CouncilRoles.All` at `:1861`. The review interval is `:129`, set from `MissionReviewMinutes` (`Trading.cs:419`, `AppHost.cs:674`).
- Selection is `:1476-1518`. `RolesDue` (`MissionEventStore.cs:476-486`) orders by `MIN(due_at), MIN(rowid)`; `DueFor` is `:452-464`.
- Routes: owner words (`:388-396`), data (`MarketDataService.cs:193-206`), notes (`Referee.cs:417`; `TradingGateway.cs:426`, `:1163`). One `LoopAsync` (`MissionLoop.cs:1332`, `:1391`).

**Fairness (asked):** it holds today.
- The oldest due wake goes first (`RolesDue`).
- Turning, unaffordable and vendor-held roles are stepped over (`MissionLoop.cs:1493`, `:1497-1502`). With one `LoopAsync`, only the owner's typed chat turn can hold another lease.
- Only the tie rule is new. **Red at base:** (b), because `RolesDue` has no status filter; (d), because the note goes to Research only.

| # | Location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| 1 | L20 (a) | WRONG. At this unit's base `Schedule` still walks only the legacy pair, so a member already gets no heartbeat and (a) is green. A non-legacy HEAD getting nothing is what is red | SOURCE `MissionLoop.cs:1861` | "(a) `A_member_position_gets_no_heartbeat` (quoted red at base: every position scheduled)" → "(a) `A_head_position_is_scheduled_and_a_member_is_not` (red at base on its head half: `Schedule` walks only `CouncilRoles.All`, `MissionLoop.cs:1861`)" |
| 2 | L16 item 1 | GAP (= R17 #10). ORGANISATION says heads wake "on material events and a maximum interval" (`:273`, `:281`, `:354`). Today's code routes no fill, standing or alarm event to a non-legacy position | SOURCE as cited | "The review interval stays 30 minutes for the legacy pair (unchanged)." → "The legacy pair keep their 30-minute review; every other head gets no timer review — renewal, deliveries, the notes of item 4, and a maximum interval (default 24 h, R17 #10); members never." Red test `A_new_head_gets_only_its_maximum_interval` |
| 3 | L17, L21 (c) | IMPRECISE. See "Fairness" above | SOURCE `MissionEventStore.cs:476-486`; `MissionLoop.cs:1493` | "(c) `Three_positions_due_at_once_are_served_oldest_first_and_none_starves`" → "(c) `Three_positions_due_at_once_are_served_oldest_first_and_none_starves` (a GUARD for oldest-first, which `RolesDue` already does; red at base only on its tie half — equal `due_at` served by unit depth then id, not insertion order)" |
| 4 | L19 item 4 | GAP. `strategy_verdict` and `strategy_promotion` record no requester. The holdout run is `referee` (`Referee.cs:301`), `Referee.Verdict` is not told the caller (`GatewayPipeServer.cs:2496`), and the allocator's note has none (`TradingGateway.cs:421-430`) | SOURCE `Database.cs:839-845,881-896` | "(from the backtest/verdict row's role)" → "(the version's author, `strategy_version.role`, written at `StrategyStore.cs:230`)" |
| 5 | L16 item 1 | GAP. `MissionLoop` has no `OrgStore`. Nine classes implement `IMissionHost` (`AppHost.cs:1269` and eight test hosts, e.g. `MissionLoopTests.cs:96`), and it already gains members with defaults (`MissionLoop.cs:217`, `:230`, `:239`, `:262`) | SOURCE as cited | append to item 1: "The loop reads positions (id, heads-a-unit, status) through a new DEFAULT member of `IMissionHost` (as `Events`, `:262`) answering the legacy pair as active heads, so the eight test hosts compile and `MissionLoopTests.cs:482-483` stays green; `AppHost.MissionHost` (`:1269`) answers from `OrgStore`." |
| 6 | L18 item 3 | IMPRECISE. The disposition vocabulary is a closed set (`MissionEventStore.cs:102-133`), and `Consume` needs an attempt id (`:544`) | SOURCE as cited | append: "(a new `MissionEventDisposition` constant written with `SettleWithoutTurn`, `:601-617`; `consumed_by` a non-attempt marker like `MissionLoop.Unrecorded`, `:1842`)" |
| 7 | L1 title | IMPRECISE. It over-claims: there are no material-event wakes beyond the notes | INFERENCE (items 1–4) | "heads wake when something material happened, executors only for work, and N positions share the loop fairly" → "heads wake on work and a maximum interval, members only for work, dormant positions never, and N positions share the serial loop" |
| 8 | L23 conflicts | IMPRECISE (stale). `U-runner-forward` lands in W2. `U-decision-port` is now this unit's W7 partner, not "later" | SOURCE `ORGANISATION.md:379` | "`U-runner-forward` shares `TradingGateway` (another region, L); `U-decision-port` (later) shares the counters (L)" → "`U-decision-port` (W7, same wave) shares `TradingGateway.cs` (`:421-430`, `:1158-1167` against `:2340-2345`; L — rebase)" |

### 1.6 The three edited briefs (and a rung-27 trap)

| # | Brief / location | Finding | Label | Replacement (old → new) |
|---|---|---|---|---|
| V1 | U-venue-verify L3 | WRONG (introduced by the edit). `U-cost-model` edits the holdout card (`SettingsView.cs:231-237,502-512,562-597`) and no `AppHost` code. `U-tape-store` edits `SettingsView.cs:178-215` and `AppHost.cs:476-560` (R09 § 9) | SOURCE the two read-first lists | "cost-model and this both edit the Market data card and" → "`U-tape-store` and this both edit the Market data card and" |
| V2 | U-venue-verify L2-3, L11, L18 | IMPRECISE (stale since 13:51; R17 #23). Lane A must not wait on an inert lane-B unit | SOURCE `ORGANISATION.md:345-347` | "after `U-cost-model` (schema 27), `U-org-ledger` (schema 28) AND `U-tape-store` have landed** — rung 29 never before 28 or 27 (R09 § 7);" → "after `U-cost-model` (schema 27) AND `U-tape-store` have landed** — this unit takes the next free rung at its rebase (expected 29) and the ladder stays contiguous;"; "**Schema 29** (renumbered 2026-10-02: …)" → "**The next free rung** (expected 29)"; item 1 "Schema 29:" → "The rung:"; append "`DROP TABLE instrument_check;` joins the roll-back lists at `VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275`." |
| C1 | U-cost-model item 2 | TRAP (red at runtime). There is no add-if-missing helper (RUN: `grep table_info Database.cs` is empty). Reopening a file stamped 16 or 22 re-runs 27's `ADD COLUMN`s: "duplicate column name" | SOURCE as cited | append: "`ALTER TABLE strategy_campaign DROP COLUMN cost_model_canonical; ALTER TABLE strategy_campaign DROP COLUMN cost_model_sha;` join the roll-back lists at `VenueCatalogTests.cs:214-249` and `PaperEligibleVerdictTests.cs:259-275`, rewritten in place, names kept." |
| C2 | U-cost-model L7 | IMPRECISE (stale). The numbers 28 and 29 are now only expected | SOURCE `ORGANISATION.md:345-347` | "(`U-org-ledger`'s 28 and `U-venue-verify`'s 29 land AFTER this)" → "(every later rung lands after this)" |
| D1 | U-decision-port L3-5 | IMPRECISE. The order (W7, after O2 in W5 and O4 in W6) holds. But O2 removes the folds at `CouncilRelay.cs:500`, `AiAttemptStore.cs:299` and `DailyReports.cs:881`, and the counters O2 keys are the loop's (`MissionLoop.cs:1245-1263`), not item 3's day counters (`AiAttemptStore.cs:425-454`) | SOURCE as cited | "which key `Fence`, `Refuse` and the counters by exact position" → "which key `Fence`, `Refuse`, `ComposeRecovery` and the loop's counters by exact position — so at dispatch (e)'s Fence, Refuse and report halves are green and only its day-counter half is red — and which keep `RoleCap` for a role that is no position" |

## 2. Schema ladder and stale numbers

| Rung | Unit | Current plan text | Brief text | Status |
|---|---|---|---|---|
| 27 | `U-cost-model` | ORGANISATION:345 "27"; RESUME-HERE:23; EDGE-FACTORY:371, :430 | its L7 and L18 | consistent |
| next (expected 28) | `U-org-ledger` | "the next free rung at landing" (ORGANISATION:345-347); "next free rung, inert" (RESUME-HERE:25; EDGE-FACTORY:431) | "Schema 28 … `U-venue-verify` becomes schema 29 and lands after this" (its L3, L13) | **stale** — § 1.2 row 7 |
| next (expected 29) | `U-venue-verify` | "next rung" (ORGANISATION:374; EDGE-FACTORY:431); RESUME-HERE:25 | "after … `U-org-ledger` (schema 28)", "Schema 29" (its L3, L11, L18) | **stale** — V2 |
| next | `U-org-assignments` (was `U-org-orders` (30)) | "next rung" (ORGANISATION:381) | none (CARD) | consistent. `U-rejudge` also needs one (EDGE-FACTORY:410) |
| tape.db 1 → 2 | `U-tape-store` → `U-decision-port` | separate file and ladder | both briefs | consistent |

**Stale mentions, beyond the brief lines above:**
1. EDGE-FACTORY:376 (the phase-0 row for VV) still reads "(rung 29 since `U-org-ledger` took 28)" with Depends "`U-org-ledger`". Change both to "next free rung" and drop the dependency.
2. EDGE-FACTORY:362 still says "plus `R-containment` as a light third", against the current lights (`U-price-rows`, `U-org-ledger`, `U-tape-events`, `U-tape-archive`, `U-billing-classes`, `U-org-packets`).
3. R12:21, :22, :146, :152, :174, :180 give O1 rung 29 and VV 28. U-org-ledger cites R12 § 5 (§ 1.2 row 7); add a one-line erratum at R12's head.
4. R11:219 and R09 § 7 are dated records at `0f47db7`; note the change in the README rather than rewriting them.
5. Under "next free rung", the roll-back lists (`VenueCatalogTests.cs:214-249`, `PaperEligibleVerdictTests.cs:259-275`) gain each rung's undo lines in LANDING order. 27's `DROP COLUMN`s are a hard requirement (C1). The table drops for O1 and VV follow that rule.

## 3. Conflict matrix and wave check (current waves, ORGANISATION:369-385)

**Codes:** R ladder · H same method (ordered; the second re-checks) · M same method, conditional (notes) · L same file, another region (rebase) · D shared docs
only (`CONTRACTS`, `USER-GUIDE`, `RESEARCH-REQUIRED`) · d declared dependency · — none. The cells are INFERENCE from each brief's named edits plus § 1.

| New ↓ / queued → | KHP W1 | CM W1 | RF W2 | EI W2 | TA W3 | TS W3 | VV W4 | PF W4 | TB W4b | TR W5 | DP W7 | PR | O1 | O2 | O4 | O3 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **PR** `U-price-rows` (W1 light) | D | D | D | D | — | D | D | D | D | D | D | · | — | — | d | — |
| **O1** `U-org-ledger` (W2 light) | D | R | D | D | D | D | R | D | D | D | D | — | · | d | d | d |
| **O2** `U-org-principals` (W5) | L | L | — | M¹ | L | L² | L² | L | — | L | H | — | d | · | H | L |
| **O4** `U-org-envelopes` (W6) | L³ | L | — | — | — | L | L | L | — | — | H | d | d | H | · | L |
| **O3** `U-org-wakes` (W7) | — | L | L | L | — | — | L | L | — | L | L | — | d | L | L | · |

**Notes on the coded cells:**
1. O2 × EI: `GatewayPipeServer.VerdictFor` is one method (O2 at `:2454`, EI at `:2484`), so the pair is M, not R12's L.
2. O2 × TS and O2 × VV become M only if O2 edits the `Paths.EnsureAllVerbose()` call inside `StartAsync` (`AppHost.cs:512`). § 1.3 row 4 avoids that.
3. O4 × KHP is M on the harness constructor (`AppHost.cs:130-139`) only if O4 item 3 stays.

**Other H pairs:** O2 → O4 on `AiAttemptStore.Refuse` (`:288-305`); O2 and O4 → DP on `Fence`, `Refuse`, `RuleFor`/`Share` and `AiAdmissionRule`. Every one of them is sequential in the current plan.

| Wave | Heavy ∥ heavy (+ light) | Overlap | Verdict |
|---|---|---|---|
| W1 | KHP ∥ CM (+ R-containment on the box, PR) | KHP×CM L (R11 § 11); PR×both D | OK. Three builders on this Mac: serialise builder gates (HOW-WE-BUILD.md:80) |
| W2 | RF ∥ EI (+ O1) | RF×EI L (`ForwardRuns.cs`); O1×both D | OK. O1 after CM (27) |
| W3 | TA ∥ TS | R11: low | OK |
| W4 | VV ∥ PF (+ U-tape-events, CARD) | MED (R11 § 11): `AppHost.PaperChoice` `:759-770`, `Platforms/Connectors.cs:87-90`, the `StatusAsync` initializer, `GatewayStatus` | OK only if ordered: land VV first, then PF re-checks against it |
| W4b | TB (+ U-tape-archive, CARD) | TB edits `ForwardRuns.cs` only | OK |
| W5 | O2 ∥ TR | L (`WorkspaceBuilder.cs`, `GatewayPipeServer.cs`, other methods) | OK. Under § 4's split, O2a takes this slot |
| W6 | O4 ∥ U-features (CARD) | unknown until it is briefed | Re-check at briefing. O2 → O4 is ordered (W5 → W6) |
| W7 | O3 ∥ DP (+ U-billing-classes, CARD) | O3×DP L (`TradingGateway.cs`). U-billing-classes ("each seat counted against its class", ORGANISATION:355) would edit the admission path that DP item 2 edits — H, INFERENCE from its one line | Order DP and U-billing-classes, or move the CARD to W8. O4 → O3 and O2/O4 → DP are ordered |

## 4. Recommended splits and reorders

1. **Split U-org-principals.** It has 5 items, ≈14 src files, a new hold mechanism, 5 tests and 3 mutants (INFERENCE ≈2 h). HOW-WE-BUILD.md: "Anything that needs more is two units."
   - **O2a** (keys and homes): items 1–2 with § 1.3 rows 3–5 and 7; tests (a), (b), (e); mutants `Or` in `TurnMeter.Key` and the `HomeDir` fallback. W5, beside TR.
   - **O2b** (rights and relay): items 3–5; tests (c) (fixed per row 2) and (d); the headship mutant. W6 light, beside O4 ∥ U-features. O4 needs only O2a's exact keys, and O2b shares no file with O4.
   - O3 (W7) follows O2b, because its item 4 assumes a non-legacy position may ask for a verdict.
2. **Cut U-org-envelopes item 3 and test (e) into a CARD `U-org-seats`** (§ 1.4 row 1). It closes a spend hole by construction, and nothing seats a position before `U-org-verbs`. O4 keeps the tree, the day-total gate first, and R17 #1(b) if adopted. Brief the CARD before any API-key seat (`U-harness-responses`, W10).
3. **Order W7's `U-decision-port` before the `U-billing-classes` CARD, or move the CARD to W8.** Keep O2 off `Paths.EnsureAllVerbose`, and serialise builder gates in W1. No other reorder is needed.
4. **Before the first dispatch:**
   - Commit `docs/ORGANISATION.md`, the five new briefs and R12–R18. `git status` shows them untracked, so a worktree from `main` lacks them; R09 § 0 and R11 (1) raised the same point.
   - Paste the rung lines (§ 1.2 row 7, V2, C2) and the R12 erratum.
   - The rows that keep a builder from stalling are § 1.2 rows 1, 3 and 6; § 1.3 rows 1–4; § 1.4 rows 1, 3, 4 and 5; § 1.5 rows 1 and 5; C1.
