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
