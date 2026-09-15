# U-allocator-1 — the allocation record, and the ceiling the gateway enforces per promoted version
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four rules; two-press for anything that moves money), `docs/COUNCIL.md:55-57` (the capital allocator is
code), `:59-63` (a change of allocation is a consequential boundary; code applies the allocation policy), `:14-15` (the capital gate), `:32-33` (only a
promoted version executes), `:135-136` (forward evidence before capital), `:210-211` (which allocation caused an operation is unrecoverable later),
`:174-175`, then `Gateway/TradingGateway.cs:1135-1226,1236-1285,1376-1476,1605-1611,1637-1676` (the gates; the create and the dispatch gate on ONE
position reading), `:100-103` (`Referee`, `Promotions`), `GatewayTypes.cs:166-171` (`PlaceIntent`: no version), `Core/Trading.cs:23-131,909-929`,
`Db/PromotionStore.cs:275-292` (`Standing`, computed at read time), `DailyReport.cs:365-382` (section 4), `MissionLoop.cs:627-640` (`PromotedLine`),
`docs/CONTRACTS.md`. Branch `u-allocator-1`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-allocator-1`, rebased onto `main` AFTER
`U-freshness` lands (both touch `PlaceIntent` and the dispatch gate). **Money path:** every guard ships with a test RED before it and ONE mutant,
quoted, over `RecordingConnector` proving the wire stayed empty. No box, no ATAS, no money, no order sent. **Schema 20.**

**Why.** No line of `src` allocates capital (the only "allocation" is the AI budget's role share) and nothing on the order path reads
`Promotions.Standing`: a promoted version and a quantity the gateway would allow have nothing binding them; `PlaceIntent` names no version and
`execution_request` records none, so what caused an order is unrecoverable. COUNCIL never names what capital is allocated TO — THE CHOICE: a
promoted strategy version, the only executable thing under rule 8; the ceiling is the owner's declared number, not a fraction of a balance the
app does not persist; both in `CONTRACTS.md` as choices. The honest limit, stated: no runner emits a live or paper intent yet, so this unit binds
the GATE end to end and the deployment it ceilings does not exist.

1. **`strategy_allocation`, app-written only:** id = sha256 over (version id, promotion id, policy version, `max_quantity`, `max_notional`,
   currency, `effective_from`); `effective_to`, `reason`, `at`; one `INSERT … ON CONFLICT DO NOTHING`, no update, no delete, no pipe op, no
   verb — the `Promotions` shape, the store's surface held by a reflection test. RED: `no such table: strategy_allocation`. Mutant (the
   clock hashed into the id): two rows for one allocation.
2. **Written only for a version whose `Standing` is `promoted` at that instant,** through the owner's two-press card (words only, `Theme.cs`
   values only; widening an allocation is two-press like `Widens()`). RED: an allocation recorded against an unjudged version. Mutant
   (`Standing` replaced by "a promotion row exists"): an invalidated promotion still allocates.
3. **An intent may name its version and the record keeps it:** `PlaceIntent.StrategyVersionId` (nullable); `execution_request.strategy_version_id`
   and `allocation_id` (nullable) written at create in `PlaceAsync`, never re-derived from the parameters blob. RED: a placement naming a
   version stores nothing naming it. Mutant (read back from `ParametersJson`): a rewritten blob re-attributes a sent order.
4. **The ceiling, inside the dispatch gate on the same reading as the other three:** an order naming a version with no allocation whose promotion
   still reads `promoted`, or over `max_quantity` (or `max_notional` where set), is REFUSED with an `ALLOCATION_*` code — nothing sent, no row
   written; a close or a reduce always passes (the loss budget's reason). RED: an order ten times the allocation dispatches. Mutant (the check
   moved into `RiskCheckOrThrow`, above the awaited reads): two placements held at `Barrier(2, HeldCall.Place)` both pass one allocation.
5. **The owner and the roles are told:** section 4 gains one line per standing allocation (version, ceiling, currency, from when, policy
   version), marking one whose promotion now reads invalidated; the promoted line in the Situation says what is allocated, no holdout figure.
   RED: the report silent with an allocation standing. Mutant (printed while `Standing` is `invalidated`): a withdrawn allocation reads live.
Not this unit: a runner on the order path; `U-flatten`; the boundary event and the sealed assessments (`U-council-concurrent-2`); parentage,
exploration, retirement (`U-allocator-2`); a balance- or equity-derived ceiling.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, the choices restated, what you did NOT do.

## Report

Tip `3e8d399`, rebased onto `main` `926e87e`. U-flatten-1 landed mid-unit and both units' behaviour is kept: the ceiling is the FOURTH gate inside the dispatch gate on the one position reading, after `LossBudgetOrThrow`, and like it returns before refusing when `CanIncreaseExposure` is false, so a close is never refused. Schema **20** (U-flatten-1 took no rung); U-allocator-2 takes 21.
Gate on this tip: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)` `0 Error(s)`, 17 projects. `Passed! - Failed: 0, Passed: 1097, Skipped: 0, Total: 1097` (Unit) · `Failed: 0, Passed: 304, Skipped: 0, Total: 304` (Fault) · `Failed: 0, Passed: 668, Skipped: 1, Total: 669` (Integration). Touched classes 3× each, all green: AllocationLedgerTests 10, AllocationGateTests 10, AllocationSurfacesTests 6, TwoPressGrantTests 27, VenueCatalogTests 8. No `Timing` red. Test names `main` 1712 → HEAD 1741, `[Fact]`/`[Theory]` 1680 → 1709; nothing removed, nothing moved.
1. Mutant, the clock hashed into the id → `The_same_allocation_recorded_twice…`: `Assert.Single() Failure: The collection contained 2 items`, the two rows differing only in `At`.
2. Mutant, `Standing` replaced by `_promotions.ById(allocation.PromotionId) is null` → `An_invalidated_promotion_cannot_be_allocated_capital`: `version 6b52acd9f0d5 may trade up to 1 from 2026-09-13 12:00:00Z.`
3. Mutant, `strategy_version_id` scraped from `parameters` instead of its column → `A_rewritten_parameters_blob…`: `Assert.Equal() Failure: Strings differ / Expected: "6b52acd9f0d5034d31b60a0085727047eae1a9e58"··· / Actual: "a-version-that-never-placed-anything"`.
4. Mutant, the ceiling evaluated above the awaited reads → `Two_placements_in_flight_together…`: `Assert.Equal() Failure: Values differ / Expected: 1 / Actual: 2`, with `connector place calls: 2` and 2 orders at the broker.
5. RED (item-5 behaviour stripped, shapes kept, so the failure is silence and not a build error): all 5 red, e.g. `Assert.Contains() Failure: Sub-string not found / Not found: "- allocated: none"`. Mutant, the WITHDRAWN mark dropped from `AllocationLine` → `Not found: "WITHDRAWN: its promotion no longer stands"···`. Second RED, for the contract guard: `Not found: "What capital is allocated TO is a promote"···`.
**Two defects found by the first full suite and fixed** (`090c74c`): item 3 added two columns to `Cols` but not to `TryCreateFlagged`'s VALUES list — `SQLite Error 1: '19 values for 21 columns'`, 43 Fault + 1 Integration red, every operator emergency press unable to write its row; and the rung-16 rollback fixture did not undo schema 20 — `duplicate column name: strategy_version_id`, 1 Unit red. Neither assertion was loosened; the fixture's own comment already required every rung above 16 to be undone.
**Choices, restated.** Capital is allocated to a promoted strategy version (COUNCIL names no subject; rule 8 makes it the only executable thing). The ceiling is the owner's declared number, not a fraction of a balance this app does not persist. A position is not attributable to a version and the ceiling counts it anyway — conservative, it can only refuse. All three are now IN `docs/CONTRACTS.md` as choices with a test holding them (`3e8d399`): the brief required it, the file carried none of it, and `AllocationCeilingOrThrow` already cited the file by name for the third.
**Deviations.** The previous builder's item-5 work was kept on its merits and recommitted with a real message, not a WIP one. One commit (`090c74c`) spans items 1 and 3 because one root cause sits in each. Item 5's RED is reproduced by stripping the behaviour and keeping the shapes, because the true "before" state does not compile against the tests.
**NOT done.** No runner emits a live or paper intent, so the deployment this gate ceilings does not exist — the gate is bound end to end and stated as a limit in `CONTRACTS.md`. No box, no ATAS, no money, nothing sent to any venue. `docs/USER-GUIDE.md` is untouched: the brief did not ask for it and the Capital card is the owner's only route. Not this unit: parentage, exploration, retirement (U-allocator-2); the boundary event and sealed assessments; a balance-derived ceiling.
