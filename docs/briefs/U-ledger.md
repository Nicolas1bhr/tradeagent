# U-ledger — every fill is written down, and the AI's result is a number the owner can read

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, and the `U-batch-2` section of `BUILD-STATUS.md`
(schema 4; "a removed row is never un-removed" — the ledger discipline this unit inherits). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout` on this Mac. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/ledger`, branch `u-ledger` from `main`. Another builder works
`AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `AppHost.cs`, the Dashboard's AI card and `Settings`;
you touch none of them. You own schema 5; nobody else changes the schema.

**Why.** The product's purpose is an AI that works non-stop to make at least enough to pay for itself. Nothing measures
that: `TradingGateway.OnExecutionReceived` (`TradingGateway.cs:332`) writes one activity line per fill and no table
holds a fill, a profit or a drawdown. This unit is the ruler; a loss budget, the AI's running cost and, later, several
agents judged against each other all read it. Right before pretty.

1. **A `fill` table, schema 5** (`Database.cs`, `Versions.DatabaseSchemaVersion` 4 → 5, `GatewaySchema.cs`, new
   `Db/FillStore.cs`): one row per execution, written by the gateway only, never updated or deleted. Sources: the
   `ExecutionReceived` event AND a pull of `GetExecutionsAsync` at every connector (re)connect and every five minutes.
   Key `(account_id, execution_id)`: a fill seen by both sources is ONE row. Columns: at, symbol, side, quantity, price,
   connector_order_id, client_order_id, request_id (via `TA-{requestId}`), agent_session (from the request row —
   attribution per agent cannot be retrofitted), source (`event`|`pull`), fee (NULL = unknown; 0 only when the
   connector said 0), recorded_at. Record each pull's `since` and outcome in `kv`; on ATAS `GetExecutions`
   (`AtasStrategyAdapter.cs:1415`) reads in-session `MyTrades` only, so coverage starts at the bridge's start and the
   record must say so. RED first (the same execution from event and pull → two rows) → GREEN one; mutant → RED.
2. **`trade pnl --json`** (a `pnl` op: `GatewayPipeServer.cs`, `Protocol.cs`, `TradeCli/Program.cs`, the schema):
   realized P&L per symbol and per day by average cost, signed by side, using `InstrumentInfo.ContractSize`/`TickValue`
   where reported, else quantity × price; unrealized from `positions` at the last `quote`; fees where known; max
   drawdown of the realized+unrealized curve; fill count; `--since`, today, all. An `incomplete` list names what is
   missing (fee, multiplier, a failed pull, fills before coverage began). **An unknown never reads as zero.** RED first
   (NULL fee → net reads as if 0 and `incomplete` is empty) → GREEN; mutant (NULL coalesced to 0) → RED.
3. **A Performance card** in a new `PerformanceCard.cs`, ONE inserted line in `DashboardView.cs`, colours from
   `Theme.cs` only: today and since the first fill — net, fees, drawdown, fills, "incomplete: …" in the owner's words;
   built once, updated in place on the five-second refresh (CLAUDE.md). Activity keeps its line per fill.
4. **Tests drive the Fake connector**, whose broker emits and serves fills (`FakeConnector.cs:62,295,356`).

Yours: the files named above, `docs/CONTRACTS.md` (`pnl`), `docs/USER-GUIDE.md` (the card), tests. Commit per item, no
trailers. Gate: Release `--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file, one project
at a time, nothing else running (a `Timing` test failing beside another suite → that class alone 3×); names vs `main`
0 removed. No box, no ATAS, no UI run: say so.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant with the outputs quoted; gate
counts; what you did NOT do. Verified or NOT VERIFIED, nothing in between.
