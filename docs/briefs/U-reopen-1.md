# U-reopen-1 — a closed scope reopens by code only when it has earned it: a computed instant, a fresh flat book, a write-once receipt, a clock that cannot lie it open
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (rule 3; two-press), `docs/COUNCIL.md:14-15,33,59-63`, the `U-flatten-1` and `U-flatten-2` sections of `docs/CONTRACTS.md`, then
`src/TradeAgent.Gateway/LossBreach.cs` (keys `:47,53`, `LossBreachRecord :139-197`), `TradingGateway.cs:213` (`Now` = `_opt.Clock`), `:727`, `:844-862` (`ClosureToday`),
`:1536-1612` (`LossBudgetOrThrow`: `CanIncreaseExposure` → `DayClosed :1554` → `SymbolClosed :1557` → zero-budget return → ledger), `:1635-1658`, `:1716-1906` (`LossWatchAsync`,
`Confirmed :1808`, `Close :1826`), `:1975-2001`, `:2979,3096` (the approval path re-runs the gate), `:5169-5250` (the health pass; the watch is LAST `:5243`), `GatewayTypes.cs:33,
89,104,335-342`, `Db/Database.cs:154,1132-1168` (`kv`; `SetKv` is an UPSERT — write-once is enforced in code today), `GatewaySchema.cs:98-104`, `WorkspaceBuilder.cs:341-350`,
`MissionLoop.cs:492,611`, `Core/Trading.cs:845-897`, `DailyReports.cs:351-356`, `DailyReport.cs:429-457`, `DashboardView.cs:1511-1516`, the `U-flatten-2` flatten record as landed,
`tests/TradeAgent.FaultTests/LossDayClosureTests.cs` (the rollover test `:212-250` pins today's rule and MOVES), `LossWatchTests.cs`, `LossDayClosedSurfacesTests.cs`,
`tests/Shared/RecordingConnector.cs`. Branch `u-reopen-1`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-reopen-1`, rebased onto `main` AFTER `U-flatten-2`
lands. **Money path:** every guard ships with a test RED before it and ONE mutant, quoted, over `RecordingConnector`. No schema. No box, no ATAS, no money. Settled with the
owner 2026-09-15: option B — reopen by code, never "until the owner has read it" — on his condition of "exquisite logic and edge case handling"; one Astra consult shaped the rules.
**Why.** Today a closure ends because the key changes at UTC midnight: a 23:58Z breach reopens at 00:00Z; nothing checks that the flatten resolved, that the book is flat, that
the clock moved forward honestly, or that a parked approval predates the breach; and no record says a reopen happened.
1. **The closure is a state, not a key.** `DayClosed`/`SymbolClosed` and `ClosureToday` look across days: a scope (account, or account+symbol) is closed from a breach
   record's `ConfirmedAt` until a receipt `loss_reopen:{account}:{utcDay}` / `…:{symbol}:{utcDay}` exists for it, found by `KvStartingWith("loss_breach:{account}:")`.
   While a scope is closed the watcher writes NO further breach record for it (one episode until reopened: the flatten's own fills after midnight are not a second
   breach). RED: a breach at 23:58Z, the clock at 00:01Z → `PlaceAsync` dispatches. Mutant (`ClosureToday` back to the day key): the day reads open at midnight.
2. **Eligibility is computed from the immutable record; the reopen is a receipt.** `EligibleAt` = max(the first UTC midnight after `ConfirmedAt`, `ConfirmedAt` +
   `LossMinClosure`), a pure function of the record; `LossMinClosure` = **24 h**, a `GatewayOptions` value fixed in this unit (an owner setting in `U-reopen-2`). The
   receipt is written ONLY by the watcher tick, under `_dispatchGate`, write-once AT THE SQL LAYER (a new `Database.AddKvOnce`: `INSERT … ON CONFLICT(key) DO NOTHING`,
   returning whether it inserted), with its evidence: `EligibleAt`, the instant, the fresh flat read, the epoch. The admission gate NEVER writes it — until the receipt
   exists it refuses. RED: eligible, no tick yet → an order dispatches. Mutant (the gate writes the receipt on first admission): two placements in flight both pass.
3. **Flatness is fresh evidence, not a terminal state.** The receipt requires, on the tick: a `GetPositionsAsync` from the current connection epoch reading flat for
   the scope; the `U-flatten-2` record for the breach reading `flat` when one exists (a breach confirmed with no open position needs none); no open or flagged request
   on the scope (`HasUnconfirmedWork`, every `op-budget-*` leg resolved, every opener cancellation settled); a valuation that is available (`RISK_CHECK_UNAVAILABLE`
   stays independently blocking). A daily closure and a symbol closure compose by AND. RED: eligible by time, one leg still flagged → the receipt is written. Mutant
   (flatness taken from the flatten record alone): a position re-opened after the flatten reads flat. Second RED: a resting opener still working → the receipt is written.
4. **A clock that steps back cannot reopen.** Each tick persists `clock_high_water` (`kv`, monotone: written only when `Now` exceeds it); eligibility also requires
   `Now` ≥ that mark, and a `Now` below it records `loss_clock_suspect` once, refuses the reopen, and says so in `status`. A forward jump only lengthens nothing and
   is allowed. RED: the clock moved back 26 h after the breach → the receipt is written. Mutant (the mark upserted on every tick): a stepped-back clock reopens.
5. **A parked approval from before the breach dies with it.** `ApproveAsync` refuses, after reopen too, any `AWAITING_APPROVAL` request of the scope whose `CreatedAt`
   precedes the latest breach's `ConfirmedAt` — its own code and sentence, never `LOSS_BUDGET_REACHED`. RED: approved after the receipt → sent. Mutant (compared to
   `EligibleAt` instead of `ConfirmedAt`): a request parked during the closure is approved.
6. **Told, and the old sentences rewritten:** `status` gains `loss_reopens_at` (computed; absent when held by 3 or 4, with the reason) and `loss_reopened_at` (the receipt);
   the Situation line, the Safety row, section 4 ("reopened at … by code: 24 h after the breach, the book flat" / "closed since … — earliest … — waiting on: …"),
   `GatewaySchema.cs:98-104`, `WorkspaceBuilder.cs:341-350`, `USER-GUIDE.md`, `CONTRACTS.md` (the rule; 24 h as the owner's to overrule; the day is UTC, not a venue's; records
   are keyed by account so a change of platform carries no closure; the reopened day's figure is its own UTC date's ledger, flatten fills included). RED: the report silent
   on a reopen. Mutant (`loss_reopened_at` derived from the day key): reads reopened while held.
Not this unit: strikes, the review card, owner-set durations, a bounded director hold (`U-reopen-2`); the data-loss exit (`U-flatten-3`); stops (`U-protect`). Gate and
report as `U-flatten-1`; the moved rollover test named in the report with what it now pins.
