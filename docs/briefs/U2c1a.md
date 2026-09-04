# U2c1a — dispatch recovery: rebase over U2a, then derive the reconciliation rule correctly

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/hardening/records/U2c1.md` down to the end of
"What the branch does", and `docs/CONTRACTS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`; full suite 8–12 min, to a file. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u2c1-build`, branch `u2c1-dispatch-recovery` @ `1e10660`
(28 commits on the OLD U2b tip `cb2ce2f`, not on `main`). **Item 0:** `git rebase --onto main cb2ce2f
u2c1-dispatch-recovery` — 21 commits survive (the 7 old U2b commits drop out; `main` has their rewritten form). Expect
a conflict in `src/TradeAgent.Gateway/GatewayTypes.cs`: U2a sealed `AgentContext` (`IsOperator` private init, no `with`),
minted `op-{nonce}-{intent}-{index}` ids with the 61-character budget; U2c-1 added record and press types. Keep both
sides; never drop a U2a guard to compile. Then Release build `--no-incremental` (0 warnings) + full suite in Release as
the baseline; a red test after the rebase is a real interaction, fix it as part of item 0.

The rule (already written in CONTRACTS.md, keep it): *a request leaves the unconfirmed set only on positive, definite,
stable evidence about its own target; anything else is inconclusive and keeps trading paused.* Each item is a red-first
test that FAILS today, then the fix. Both directions every time: the wrong evidence is refused AND the right evidence
still settles (a definite CANCELLED from the right connector still clears; a correctly applied modify still reads applied).

1. Reconciliation uses only the connector whose id the record carries: a record placed on A while B is connected is
   inconclusive, reason "placed on A; connected to B" — an empty book on B settles nothing.
2. A non-definite target state is never "clear", with or without a captured set.
3. `Adopt` never treats the broker's UNKNOWN as resolved.
4. "Held still" is not a verdict: a cancel/modify whose target is unchanged after grace stays inconclusive until a
   definite state — target terminal, broker refusal, or the owner's card.
5. Latch on the definite settle path too: any persist failure after the wire → latch (round 2 covered only the
   indefinite path).
6. Modify verdict: returned order id == target id (and symbol, account); price ∈ {round-down, round-up} of the request
   on the tick grid AND ≠ the pre-modify price when the request differs; quantity only once the SDK contract says what
   `OrderInfo.Quantity` means (write that sentence in `src/TradeAgent.ConnectorSdk/Contracts.cs`), else inconclusive.

Yours: `src/TradeAgent.Gateway/**` except the pipe server and `AgentContext`, `Core/Db/Stores.cs`, `Errors.cs`, the
ConnectorSdk sentence, `docs/CONTRACTS.md`, tests. Not yours: the updater, `CoidWitness*`, `DashboardView.cs`, the
emergency-press rewrite (U2c1b), the cancelled-handler settle (U2c1c). Every fix: RED quoted, GREEN, one mutant watched
red (commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report

Append here as you go and commit it with each item, ≤20 lines: tip sha; the rebase conflicts and how each was resolved;
baseline counts; one line per item (RED → GREEN → mutant); final counts; what you did NOT do. Verified or NOT VERIFIED.
