# U-flatten-1 — the breach is a durable fact the app watches for, not a refusal an order happens to meet
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four rules; two-press), `docs/COUNCIL.md:14-15,33,59-63` (the loss gate; "never a late trade"; a loss-budget
event is a consequential boundary: both directors sealed, a deadline, code applies the policy), `docs/CONTRACTS.md:965-1000` ("Nothing is flattened"),
then `src/TradeAgent.Gateway/TradingGateway.cs:1430-1500` (`LossBudgetOrThrow`; callers `:1702` inside `_dispatchGate` after the ONE position read at
`:1690`, and `:2520` on the approval path; `_dailyLossSaidFor` `:1545` is all it remembers), `:1295-1300` (`MaxQuoteAge`), `:423,673-678` (`QuoteChanged` →
`_quotes` → `LastQuote`), `:506`, `:538,4654-4660` (`FillPullInterval`, the one periodic pull), `:211-212` (the injected clock), `LossBudget.cs:39-108`,
`Core/Trading.cs:804-845`, `Db/BoundaryStore.cs` (`CouncilBoundaries.Open`, `BoundaryKind`), `Db/Database.cs:1062-1070` (`kv`), `tests/Shared/FakeConnector.cs:
277-282` (the simulator raises `QuoteChanged` only inside a pull); the surfaces: `GatewaySchema.cs:90-97`, `WorkspaceBuilder.cs:331-341`, `DailyReports.cs:
300-336`, `MissionLoop.cs:611`, `DashboardView.cs:1417-1418`, `USER-GUIDE.md:747-754`. Branch `u-flatten-1`, worktree `~/Projects/ai-trading-software-for-
mihael-worktrees/U-flatten-1`, rebased onto `main`. **Money path:** every guard ships with a test RED before it and ONE mutant, quoted. NO schema rung:
the record lives in `kv` (app-owned; a table waits on `U-protect`). SENDS NOTHING — the flatten is `U-flatten-2`. No box, no ATAS, no money.

**Why.** Both budgets are evaluated only when an order arrives; a breach refuses that order and remembers nothing — a close realised under the budget
reopens the day, a restart forgets it, and with no order arriving a bleeding book is never measured. Settled with the owner's rule (no human in the loop)
and Astra's consult of 2026-09-14 (final answers only): the day closes and reopens by code; the record, the evidence and the boundary exist BEFORE the first send.

1. **The record is written once, before anything else, and outranks the ledger.** `loss_breach:{account}:{utcDay}` in `kv`, written on the first
   CONFIRMED breach (item 2) with its evidence (both limits, the settings revision, the ledger figure and its missing-fee count, each open position with
   the mark used — side, source, age — the connection epoch, timestamps), never updated. `LossBudgetOrThrow` and the approval path refuse `LOSS_BUDGET_REACHED`
   off the record before reading anything: a close realised under the budget, a restart and a widened budget never reopen the day; the next UTC day expires it. `MaxLossPerTrade` the same per `{account}:{symbol}:{utcDay}` (opens and adds on that symbol
   refused for the day, a daily record outranking). RED: breach at −1000 unrealised on a 1000 budget, close at −950 realised, place → dispatches.
   Mutant (keyed on the local date): a 23:00-local breach dispatches at 22:30 UTC. Per-symbol RED: an ES re-entry after an ES breach dispatches.
2. **The watch: a tick as the backbone, quotes as accelerants, the gate's own lock.** Every `LossWatchInterval` (`GatewayOptions`, seconds, in
   `CONTRACTS.md`) the gateway PULLS a fresh quote per open-position symbol outside the lock, then evaluates both budgets with `LossBudget.Read` and
   writes under `_dispatchGate`, so a breach and an opening order cannot race; `QuoteChanged` schedules one immediate evaluation, coalesced. A mark is
   the executable side (bid for a long, ask for a short), younger than `MaxQuoteAge`, from the current connection epoch (`_quotes` dropped on a
   disconnect); an unavailable valuation denies new risk (`RISK_CHECK_UNAVAILABLE`, today) and records NOTHING. A breach is confirmed only when a second
   DISTINCT pull within `LossBreachConfirmWithin` agrees; the admission gate refuses on first sight, no tolerance, and does not by itself record.
   RED: no order arrives, a pull values the book through the budget → nothing written. Mutant (the confirming read from `_quotes`): one bad print closes the day.
3. **A breach is a boundary.** The record's write opens ONE boundary of a new `BoundaryKind.LossBudget` for `{account}:{utcDay}` through `CouncilBoundaries.Open`
   (deduplicated by entity and revision, both directors' sealed assessments scheduled, default disposition `hold`); the mission wakes on it; an exhausted AI budget
   or an unreachable provider delays nothing. RED: a breach opens no boundary. Mutant (entity = the refused order's request id): a second refused order opens a second boundary.
4. **The day is told closed, honestly.** `trade status` gains `loss_day_closed_at` and `loss_symbols_closed` (absent when nothing is closed); the Situation line
   and section 4 say since when, why, and that NOTHING WAS CLOSED FOR YOU; the Safety rows show it; the schema text, the AGENTS text and the guide say the day
   stays closed until the next UTC day; `CONTRACTS.md` records the choices (automatic reopening — the owner may overrule to "until read"; `kv` until a table;
   the tick and window values). RED: a breached day's report reads as open. Mutant (`loss_day_closed_at` derived from today's ledger figure): after the close at −950 it reads open.
Not this unit: any send (`U-flatten-2`); the data-loss exit (`U-flatten-3`); strategy stops and targets (`U-protect`); a table; the assessments' content.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched classes 3×;
a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, one line
per item with its RED and mutant, the option values chosen, what you did NOT do.
