# U-stranded — the reconciler never writes off an order whose dispatcher is still alive

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (rule 2; the gateway records UNKNOWN and reconciles),
`docs/CONTRACTS.md` (unconfirmed work; the reconciliation rule), then finding 1 and UNVERIFIED 4 in
`docs/REVIEW-2026-09-05.md` with probes P6a/P6b on branch `review-probes` (`tests/TradeAgent.FaultTests/ReviewProbes.cs`
— lift them). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in
Release. No box. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-stranded`, new branch
`u-stranded` from `main`.

**The finding, executed (P6b).** `DispatchStrandedAfter` is 30 s (`Stores.cs:95`; its comment says "the connector's
10 s RPC deadline plus 20 s of slack") while `AtasConnector.WorstCaseOrderPath` is 50 s (10 s send gate + 30 s whole
frame + 10 s reply). A placement legitimately in flight for 30–50 s is "stranded" and already past `AbsenceGrace`
(15 s) when the reconciler sees it: it is settled CANCELLED, "never reached the broker", unflagged, trading resumes —
then the order FILLS; the handler's real `Settle(DISPATCHING → FILLED)` fails the CAS and is logged `already_settled`.
The broker holds a position the durable record denies.

1. **The bound derives from the connector.** `DispatchStrandedAfter` is no constant: it is the live connector's
   `WorstCaseOrderPath` plus a stated slack (as U2a derived the shutdown drain), re-derived on a connector switch;
   absence is judged from the later of the dispatch time and that bound. RED first: lift P6b at 1/1000 scale —
   reconcile during an in-flight placement → expect inconclusive, DISPATCHING kept, trading still paused; GREEN;
   mutant (the old 30 s constant) → RED.
2. **A live dispatcher owns its record.** While a handler is inside the connector call the reconciler does not move
   its row (a dispatch lease in memory, or U2c1c's attempt marker, checked first); and a `Settle` that arrives after
   the reconciler moved a row to UNKNOWN / RECONCILING WINS when it carries the broker's definite answer —
   `already_settled` is never the fate of a real FILLED or REJECTED. RED first from UNVERIFIED 4's shape; GREEN;
   mutant. Both directions: a genuinely stranded row (its dispatcher gone: crash, restart) still reconciles at the bound.
3. **The row says what the owner needs.** An inconclusive reason names "still on the wire for N s of a possible M s".

Yours: `src/TradeAgent.Core/Db/Stores.cs` (the bound), `src/TradeAgent.Gateway/TradingGateway.cs` (reconciler and
dispatch path), `GatewayTypes.cs`, `docs/CONTRACTS.md`, tests. Not yours: the press region (U-press-atomic), the
authorization path (U-gates), the pipe server, the updater. Every item: RED quoted, GREEN, one mutant watched red
(commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED → GREEN → mutant; the derived
bound's formula and its shipped value; final counts; what you did NOT do. Verified or NOT VERIFIED.
