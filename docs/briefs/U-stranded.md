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

## Report

Three item commits on `u-stranded`, tip `68b2883` (this report is the commit after it), rebased onto `main` at
`c98e02c`; no push, no other worktree touched.
Gate, both run: `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 Warning(s), 0 Error(s)**; `dotnet test
TradeAgent.sln -c Release --no-build` → **Unit 201 + Fault 195 + Integration 530 = 926, 0 failed** (919 on `main` + 7).
Test-name diff vs `main`: **7 added, 0 removed**.
**The bound**: `TradingGateway.DispatchStrandedAfter = Connector.WorstCaseOperationPath + GatewayOptions.DispatchSettleSlack`
= **50 + 20 = 70 s** shipped; an explicit option may only LENGTHEN it, as `HandlerDrainTimeout` does.
`ExecutionRequestStore.DefaultDispatchStrandedAfter` (the 30 s constant) is deleted.
1. `StrandedBoundDerivationTests`, 3 tests. RED: `at 40s unconfirmed : 1`; `unconfirmed work at 40s : True`;
   `at 75s : resolved=1 inconclusive=0 state=CANCELLED`. GREEN. Mutant `DerivedDispatchStrandedAfter => 30 s` → 3 red,
   `cp` restored, green again.
2. `LiveDispatcherOwnsItsRowTests`, 3 tests. RED is UNVERIFIED 4 executed: `resolved=1 … detail: owned-1: never reached
   the broker … trading resumed: True`, and `record now: RECONCILING, needs_reconciliation=True … engineering:
   startup_sweep_unknown, already_settled` while that dispatch answered FILLED. GREEN. One mutant per guard: lease check
   → `if (false)` → 1 red; `LateDefiniteSettle` deleted → 1 red. Both restored, green again.
3. One test. RED `wire-1: a dispatch is still in progress` → GREEN `wire-1: still on the wire for 90s of a possible 50s`.
   Mutant (name the bound, not the wire) → red at `possible 70s`. Restored.
P6b lifted verbatim from `review-probes`, run, then deleted (not committed): it now fails at its own premise —
`Assert.True(gw.HasUnconfirmedWork())` one second in — because a placement in flight is not stranded. P6a cannot compile
against this tree: it asserts on the constant that is gone.
**Did NOT do**: no box, no real ATAS, no money, no UI. Did not touch the press region, the authorization path, the pipe
server or the updater — `UpdateTradingInterlock` still asks the raw flag (finding 3) and so still sees none of this.
NOT VERIFIED, as the review also said: that a real bridge really spends 30–50 s in gate + frame. One behaviour change
beyond the finding: a record this process never dispatched now waits bound + grace before absence settles it, so
`A_legacy_stranded_cancel_record_is_swept_and_the_absence_path_terminates_it` was given a movable clock to say so.
