# U2c1c — intent survives the layer that transforms operations, a cancelled handler settles, the gateway marks the attempt

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/hardening/records/U2c1.md` down to the end of
"What the branch does", and `docs/CONTRACTS.md` (the transport tri-state, the attempt marker, the five per-leg words).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u2c1-build`, branch `u2c1-dispatch-recovery` at its tip
(U2c1a and U2c1b landed). First `git rebase main`, Release build `--no-incremental` (0 warnings) + full suite in
Release as the baseline.

1. **Intent through the connector (C1).** Agent `close` and every `close-all` leg call `PlaceAsync`, so the connector
   sees `Place` and U2a's risk-reducing fast path (the 2 s emergency gate) never applies to them. Carry the
   risk-reducing intent through `ITradingConnector` for close legs — a `Close` intent, not an offsetting `Place` — so
   the connector classifies it correctly; the single `close` included. Acceptance: `trade close ES` through the real
   `GatewayPipeServer` against a bridge that answers the position lookup then stalls → completes near the emergency
   deadline with the emergency wording. Both connectors (`Atas`, `Fake`) implement it; `docs/CONTRACTS.md` states it.
2. **A cancelled handler must settle (C4).** `TradingGateway.cs` (~:696 before U2c1a; find it): a handler cancelled
   during the pipe server's disposal returns with its record DISPATCHING, `needs_reconciliation=0`, and only the NEXT
   start's sweep notices. The cancellation path settles UNKNOWN before the store closes. And (~:660): a
   `ConnectorTransportException` whose transport result is `NothingWritten` settles not-sent (no UNKNOWN, no pause);
   only `PossiblyWritten` settles UNKNOWN — U2a exposes the transport result for this.
3. **The gateway marks the attempt (C5).** `not-sent` is an assurance a connector must opt into via the attempt marker
   (`TransportLedger`); a third-party connector written to the public contract that never calls it would report
   `not-sent` for a cancel it really performed. U2a closed the pipe-server side; the better fix is here: the gateway
   marks the attempt itself at every dispatch site of a mutating connector call, so no connector can under-report.
   Acceptance: a fake connector that performs a mutating call WITHOUT touching the ledger still yields UNKNOWN +
   reconciliation at the record, never a clean `not-sent`; the three never-dispatched legs still read `not-sent`.

Yours: `src/TradeAgent.Gateway/**` including the pipe server for item 1's wiring, `src/TradeAgent.ConnectorSdk/**`,
both connectors' implementation of the new intent, `Core/Db/Stores.cs`, `Errors.cs`, `docs/CONTRACTS.md`, `AGENTS.md`
if a sentence there changes, tests. Not yours: the updater, `CoidWitness*`, `DashboardView.cs`. Every fix: RED
quoted, GREEN, one mutant watched red (commit before mutating; `cp` restore; `touch`). Both directions on item 1: the
close is fast-pathed AND an ordinary `Place` is not. Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; one line per item (RED → GREEN → mutant);
final counts; what you did NOT do. Verified or NOT VERIFIED.
