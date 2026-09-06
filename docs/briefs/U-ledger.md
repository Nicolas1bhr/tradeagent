# U-ledger — every fill is written down, and the AI's result is a number the owner can read

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, and the `U-batch-2` section of `BUILD-STATUS.md`
(schema 4; "a removed row is never un-removed" — the ledger discipline this unit inherits). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout` on this Mac. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/ledger`, branch `u-ledger` from `main`. Another builder works
`AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `AppHost.cs`, the Dashboard's AI card and `Settings`;
you touch none of them. You own schema 5; nobody else changes the schema.

**Why.** The product's purpose is an AI that works non-stop to make at least enough to pay for itself. Nothing measures
that: `TradingGateway.OnExecutionReceived` (`TradingGateway.cs:332`) writes one activity line per fill and no table
holds a fill, a profit or a drawdown. This unit is the ruler that a loss budget, the AI's running cost and, later,
several agents judged against each other will all read. Right before pretty.

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
## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant quoted; gate counts; NOT done.

Code tip `38925f1` (7 commits), this report on top, rebased onto `main` `ea7c44e`; the gate ran at `d11db37`, whose code tree `38925f1` repeats.
1. **`fill`, schema 5** (`Database.cs`, `Versions` 4→5, new `Db/FillStore.cs`, `TradingGateway`, `GatewaySchema`): one row
   per execution keyed `(account_id, execution_id)`, never updated or deleted, from the event stream AND a pull of
   `GetExecutionsAsync` at every (re)connect (on the health pass that follows) and every five minutes, each pull's `since`
   and outcome and the coverage start in `kv`. RED `Assert.Single() Failure: The collection contained 2 items` — `X-2`
   twice, `Event` and `Pull`, 1 failed / 5 passed → GREEN 6/6 → mutant (`source` in the key) → the same `2 items`.
2. **`trade pnl --json`** (new `Pnl.cs`, `Protocol`, `GatewayPipeServer`, `TradeCli`, the schema, `CONTRACTS.md`): RED
   `Assert.Null() Failure: … Expected: null / Actual: 48` with an empty `incomplete`, 3 failed / 7 passed → GREEN 10/10 →
   mutant (NULL fee coalesced to 0) → `Expected: null / Actual: 48`. The pipe found a second defect: `Json.Options` drops
   nulls, so `net` and `fees` arrived as MISSING KEYS — a declared reply type fixes it, and `"net":null` is now asserted.
3. **Performance card** (new `PerformanceCard.cs`, `USER-GUIDE.md`): GREEN 3/3; mutant (a withheld net printed as a
   number) → `Expected: "—" / Actual: "0.00"`.
**Gate** at `d11db37`, bin/obj deleted, Release `--no-incremental`: 0 warnings, 0 errors, 17 projects. Touched classes
3× each: 7/7, 11/11, 3/3, 5/5 — 12 runs, 0 failed. Full suite to a file, one project at a time, no other test host:
302 + 261 + 616 = 1179, 0 failed, 1 skipped. Names vs `main`: 0 removed, 26 added (1153 → 1179). One earlier gate run
went red on my own assertion (an apostrophe the serializer escapes); fixed in `38925f1`.
**Off the brief:** `ExecutionInfo.Fee` in `ConnectorSdk`, without which "0 only when the connector said 0" has no
source; THREE inserted lines in `DashboardView.cs`, not one — a field, a layout entry, the update call.
**NOT done:** no box, no ATAS, no real money, no UI run — the card is proved by its words, not photographed. That ATAS
serves in-session `MyTrades` only is read from its source and is NOT VERIFIED.
**Rebased** onto `main` `b3e7d0a` (it moved to the U-life landing record mid-rebase; redone on the newer tip). Code tip `547e83d`:
the eight commits keep their messages, this report on top. Both conflicts sat in the card commit and each keeps both sides —
`DashboardView.cs`'s left column carries the AI section as `main` wrote it AND `_performance.Root` (field and `Update` call
auto-merged); `USER-GUIDE.md` keeps `main`'s Dashboard sentence and both new sections. **Gate** at `547e83d`, bin/obj deleted,
Release `--no-incremental`: 0 warnings, 0 errors, 17 projects; the 8 classes 3× → 24 runs, 0 failed (7,11,3,5 · 21,7,14,4); suite
348+261+615 = 1224 passed, 0 failed, 1 skipped; names vs `main` 0 removed, 26 added (1199 → 1225).
