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
