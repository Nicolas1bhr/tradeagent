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

## Report

Gate at **`b53b29a`** (4 commits, rebased onto `main` @ `44fb21f`; this report is the commit on top). Baseline there: 0 warnings, 913 / 0 failed.
**1 — intent through the connector. VERIFIED.** `OrderIntent` on `PlaceOrderCommand`/`PlaceIntent`; both connectors honour it (`Rpc(…, reducesRisk)`; the Fake puts a closing placement on the operation deadline). RED: `trade close ES` over the real pipe → `AtasConnector` → stub bridge answering the position lookup and muting `place` — "took 10.05s against a 2s emergency budget"; connector-level `(intent: Close)` "waited 10.00s". GREEN: < 6 s, "NOT confirmed", `'place'`. Other direction intact — `(intent: Open)` and all 3 `An_ordinary_op_behind_a_stalled_write…` cases still > 5 s.
A second test pins that a close takes what is LEFT of the operation deadline rather than a fresh one; mutant `opensExposure = OpensExposure(op)` → RED `Not found: "not sent"`. Drain rows keep their ordinary term on purpose: the intent is an obligation a third-party connector may ignore, and CONTRACTS.md now says so.
**2a — a cancelled handler settles. VERIFIED.** U2c1a's catch-all settles UNKNOWN+flagged only if disposal waits: at `SettleAfterCancelTimeout = 0`, RED `Expected: UNKNOWN / Actual: DISPATCHING` at the instant disposal returned. Fixed by flooring the post-cancel wait (`WriteBackAfterCancel = max(setting, HandlerOverhead)`); the drain still reads the raw setting, so the test that configures it away still proves what it did.
**2b — a proven not-sent no longer pauses. VERIFIED.** `ConnectorTransportException` + `NothingWritten` → CANCELLED, unflagged; `PossiblyWritten`/`ReplyReceived`/silence stay UNKNOWN. RED "trading is still paused after a leg the connector PROVED it never sent: 1 earlier request(s) are unconfirmed" → GREEN; mutant (`is ReplyReceived`) → 3 RED. `cancel-all`'s `cancelled`/`not_cancelled` now read the per-leg WORD, so a never-sent leg is never counted as a cancellation that landed; `OrderStateMachine`'s fourth-caller tripwire comment updated as it asks.
**3 — the gateway marks the attempt. VERIFIED.** `TransportLedger.MarkDispatch()` at all 5 mutating dispatch sites; it reuses a leg's record (a second would hide the connector's reports from the leg holding it) and attaches one where there is none. RED: single `cancel` "trading is paused after a cancel the connector PROVED it never sent"; ledger-blind leg `Expected "PossiblyWritten" / Actual null`. GREEN, and that connector's record is UNKNOWN + needs_reconciliation; the three never-dispatched legs still read `not-sent` (5-order sweep, `nothing_to_close`, approval leg — untouched). Mutant (drop `existing.Attempt()`) → RED.
**Decision:** `ITradingConnector.CancelAllOrdersAsync` **stays** — it is the only harness for the bridge's send-deadline measurements (17 tests), which are about transport rather than about sweeping. Recorded in CONTRACTS.md.
**Gate at `b53b29a`:** Release `--no-incremental` → 0 warnings, 0 errors, 17 projects; suite → Unit 201 + Fault 188 + Integration 530 = **919, 0 failed**. Names vs baseline: **1 removed, 7 added**.
The removal is a RENAME: `A_leg_refused_before_the_wire_reads_not_sent_even_though_its_record_is_unknown` → `…_and_leaves_nothing_to_reconcile`. Its name asserts the residual this unit closes, so keeping it would leave a false name; its five mapping assertions are unchanged and the mapping claim is still covered by `LegWordFor`.
**NOT done / NOT VERIFIED:** nothing on Windows, nothing against real ATAS, no UI run (Dashboard build-verified only). The operator press paths mark their attempts but deliberately do NOT get the not-sent settle — a press record is written flagged before the wire and only the owner's card clears it. `main` moved to `32d91d1` (U8 docs) after my rebase; `44fb21f..main` touches no `src/` or `tests/` file, so the manager's rebase is docs-only.
