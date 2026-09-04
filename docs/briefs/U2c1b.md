# U2c1b — the emergency controls become one-shot + pause + human, and a replayed sweep sends nothing

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/hardening/records/U2c1.md` down to the end of
"What the branch does" plus its "Class B" paragraph, and `docs/CONTRACTS.md`. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release, to a file. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u2c1-build`, branch `u2c1-dispatch-recovery` at its tip
(U2c1a landed). First `git rebase main`, Release build `--no-incremental` (0 warnings) + full suite in Release as the
baseline. Then delete what the simplification retires as you go: the reconstruct-at-restart path, the same-nonce retry,
the account-wide sweep call — a simplification that leaves the old path reachable is not one.

1. **A press is its records.** Cancel-all / close-all writes per-target write-ahead records, sends the wire calls, and
   from that moment trading is paused: the press's records count as unconfirmed work (WORKING closes included) until
   the owner resolves them through the card, which shows the outcome per target and the current position. No in-memory
   `OperatorPress` survives a restart — the durable records ARE the press.
2. **A second press while unresolved is refused**: "close-all sent at HH:MM; resolve it first". No retry with the same
   nonce; a definitely failed close never holds the press forever.
3. **Cancel-all is per-order cancels of the captured set**; no account-wide sweep on the wire. Close-all re-reads the
   position immediately before the wire call and refuses (fresh two-press) if it differs from the captured one; the
   wire call keeps ATAS's own close-position. Completion and outcome read the ACCOUNT stored on the records.
4. **Replay sends nothing (C2).** The composite — outer request id → captured child plan → per-child results — is
   persisted BEFORE effects, for the agent's sweep as for the operator's press; a replay of a known outer id returns the
   stored outcome. Acceptance: sweep order A as `sweep-1`, lose the reply, create order B, repeat `sweep-1` → B stays
   working and the original result is returned. Idempotency by request id applies to every mutating op, not only `Place`.
5. **The operator's own press gets the fast path (C3).** Open `RiskReducingScope` at the GATEWAY level inside the
   operator emergency methods, so button, CLI and agent inherit the 2 s emergency bound and the owner-readable sentence.
   Acceptance: operator Close All against a stalled bridge with a held gate ≈ 2 s with "not confirmed — check ATAS";
   the position read before the close inherits the scope.

Yours: `src/TradeAgent.Gateway/**` except the pipe server and `AgentContext`, `Core/Db/Stores.cs`, `Errors.cs`,
`src/TradeAgent.App/DashboardView.cs` (the card only), `docs/CONTRACTS.md`, tests. Not yours: the updater,
`CoidWitness*`, the connector intent change (U2c1c). Every fix: RED quoted, GREEN, one mutant watched red (commit
before mutating; `cp` restore; `touch`). Both directions: a normal press with a stable position still goes to the wire.
Test-name diff vs baseline: a test removed only because it pinned a retired path, named in the report. Commit per item,
no trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; what the simplification deleted; one line per
item (RED → GREEN → mutant); final counts; what you did NOT do. Verified or NOT VERIFIED.

## Report — U2c1b, builder (Opus), 2026-09-05
Code tip **4e18a5f** (this report is the commit on top), rebased onto main `939fd89`. Baseline at b5446b7: 0 warnings,
912 passed. **Deleted:** `OperatorPress` + `OutstandingPressNonce` (reconstruct-at-restart); the `pressNonce`
parameters, `NewOperatorPressNonce` and both `IdempotencyEnabled` press-replay checks (same-nonce retry); the gateway's
`Connector.CancelAllOrdersAsync` call and the reconciler's whole CANCEL_ALL captured-set arm; three sweep hooks on the
test connector. `ITradingConnector.CancelAllOrdersAsync` STAYS: 17 ATAS send-deadline tests measure the bridge through
it and the connector is U2c1c's — nothing calls it now, and CONTRACTS.md says so.
Items 1+2+3 are one rewrite of two methods and landed in one commit. 1 RED `op-close-…-ES is WORKING and unflagged`
(3 failed) → GREEN → mutant (drop `MarkNeedsReconciliation` from the write-ahead) → 3 failed. 2 RED `Assert.Throws()
Failure: No exception was thrown` → GREEN → mutant (refusal returns early) → 2 failed. 3 GREEN → mutant (ignore the
drift + restore the sweep call) → 2 failed (`Expected: 0 Actual: 1` closes, `Expected: 0 Actual: 2` sweeps).
4 RED `Expected: WORKING Actual: CANCELLED` (order B swept by the replay) and `Collection: []` (position B closed) →
GREEN → mutant (ignore the stored answer) → 2 failed; schema v3 adds `composite_request`. 5 RED `the press took 6.0s
against a 2s emergency budget`, deadline null on the pre-close read → GREEN → mutant (`Begin()`, no budget) → 3 failed.
**Gate at 4e18a5f:** Release `--no-incremental` → `0 Warning(s) 0 Error(s)`; Release suite → Unit 201 / Fault 188 /
Integration 524 = **913 passed, 0 failed**. Test names −20 / +21: all 20 pinned a retired path (3 retry, 3
`OperatorPress`, 5 press-set-and-restart, 4 press reconciliation, 4 F2-through-a-press, 1 partial-sweep answer), and
the F2 rule and "a press is judged by its own records" were re-homed onto surviving paths rather than dropped.
**NOT MINE:** I edited `GatewayPipeServer.CancelAll`/`CloseAll` (~12 lines) though the brief excludes the pipe server —
item 4 is unreachable without it. **NOT VERIFIED:** the Dashboard card (compiles; no UI run). No box, no real ATAS.
