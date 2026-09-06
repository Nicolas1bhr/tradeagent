# U-ledger — every fill is written down, and the AI's result is a number the owner can read

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, and the `U-batch-2` section of `BUILD-STATUS.md`
(schema 4; "a removed row is never un-removed" — the ledger discipline this unit inherits). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout` on this Mac. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/ledger`, branch `u-ledger` from `main`. Another builder works
`AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `ChatView.cs`, the Dashboard's AI card and `Settings`;
you touch none of them. You own schema 5; that builder makes no schema change.

**Why.** The product's purpose is an AI that works non-stop to make at least enough to pay for itself. Nothing measures
that: `TradingGateway.OnExecutionReceived` (`TradingGateway.cs:332`) writes one activity line per fill, and no table
holds a fill, a profit, or a drawdown. This unit is the ruler. A loss budget in the gateway, the AI's own running cost
and, later, several agents judged against each other all read it — so it must be right before it is pretty.

1. **A `fill` table, schema 5** (`Database.cs`, `Versions.DatabaseSchemaVersion` 4 → 5, `GatewaySchema.cs`, a new
   `Db/FillStore.cs`): one row per execution, written by the gateway only, never updated, never deleted. Sources: the
   `ExecutionReceived` event AND a pull of `GetExecutionsAsync` at every connector (re)connect and every five minutes.
   Key `(account_id, execution_id)`: a fill seen by both sources is ONE row. Columns: at, symbol, side, quantity,
   price, connector_order_id, client_order_id, request_id (via `TA-{requestId}`), agent_session (the request's session
   when it has one — attribution per agent cannot be retrofitted), source (`event`|`pull`), fee (NULL = unknown; 0
   only when the connector said 0), recorded_at. RED first (the same execution from event and pull → two rows or none)
   → GREEN one row; mutant (the key dropped) → RED. Record each pull's `since` and its outcome in `kv`, so item 2 can
   say from when fills were observed; on ATAS `GetExecutions` (`AtasStrategyAdapter.cs:1415`) reads in-session
   `MyTrades` only, so coverage starts at the bridge's start and the pull must say so rather than imply completeness.
2. **`trade pnl --json`** (a `pnl` op in `GatewayPipeServer.cs`, `Protocol.cs`, `TradeCli/Program.cs`, the schema):
   realized P&L per symbol and per day by average cost, signed by side, using `InstrumentInfo.ContractSize`/`TickValue`
   where the connector reports them, else quantity × price; unrealized from `positions` at the last `quote`; fees
   summed where known; max drawdown of the realized+unrealized curve; fill count; `--since`, today, all. An
   `incomplete` list names what is missing (fee, multiplier, a failed pull, fills before coverage began). **An unknown
   never reads as zero.** RED first (a fill with NULL fee → net reads as if the fee were 0 and `incomplete` is empty)
   → GREEN; mutant (NULL coalesced to 0) → RED.
3. **A Performance card on the Dashboard** in a new `PerformanceCard.cs`, ONE inserted line in `DashboardView.cs`,
   colours from `Theme.cs` only: today and since the first fill — net, fees, drawdown, fills, and "incomplete: …" in
   the owner's words; built once, updated in place on the five-second refresh (CLAUDE.md). Activity keeps its line.
4. **Tests drive the Fake connector**, whose broker already emits and serves fills (`FakeConnector.cs:62,295,356`).
   No box, no ATAS, no UI run: say so in the report.

Yours: `Database.cs`, `GatewaySchema.cs`, `Db/FillStore.cs`, `TradingGateway.cs` (the fill write and the pull only —
no dispatch code), `GatewayPipeServer.cs` (`pnl`), `Protocol.cs`, `TradeCli/Program.cs`, `PerformanceCard.cs`,
`docs/CONTRACTS.md` (`pnl`), `docs/USER-GUIDE.md` (the card), tests. Commit per item, no trailers. Gate: Release
`--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file, one project at a time, nothing else
running (a `Timing` test failing beside another suite → that class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant with the outputs quoted; gate
counts; what you did NOT do. Verified or NOT VERIFIED, nothing in between.
