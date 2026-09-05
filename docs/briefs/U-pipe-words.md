# U-pipe-words — the pipe says what the gateway does: the schema, and close-all's answer

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (the five per-leg words; the
reconciliation rule as U2c1a wrote it), `AGENTS.md`, then findings 8 and 9 in `docs/REVIEW-2026-09-05.md` with probes
P8 and P11 on branch `review-probes` (`tests/TradeAgent.IntegrationTests/ReviewPipeProbes.cs` — lift them).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No
box. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-pipe-words`, new branch `u-pipe-words`
from `main` — after U-pipe-hello has landed (both touch `GatewayPipeServer.cs`; rebase onto that `main`).

1. **The runtime schema stops promising a deleted rule (finding 8, P8).** `GatewaySchema.cs:25`
   (`cancel_and_modify_outcomes`) tells the agent a cancel becomes REJECTED "when it has stayed working and unchanged
   for a whole grace window"; U2c1a deleted `_settleWatch` / `HeldStill`, and CONTRACTS.md says a working target "does
   not become proof by holding still". RED first: a test that the schema's sentence agrees with the reconciler's
   actual outcome for a target that stays WORKING (inconclusive, RECONCILING); GREEN: the schema states the rule as
   CONTRACTS.md does — terminal target, broker refusal, or the owner's card. Sweep the whole schema against the
   reconciler for any other sentence the U2c-1 units made false; list them.
2. **`close-all` answers by the leg word (finding 9, P11).** U2c1c made `cancel-all`'s `cancelled` / `not_cancelled`
   read the per-leg word so a never-sent leg is not counted as a cancellation that landed; `close-all`'s `closed` /
   `not_closed` still read `ExecutionRequest.State`, and `not_closed` entries carry no `outcome`. RED first: a
   never-sent close leg → expect `not_closed` with `outcome: not-sent` and `closed: 0`; GREEN; mutant (back to the
   state) → RED. `AGENTS.md`'s `close-all` paragraph matches `cancel-all`'s, word for word where the shapes agree.

3. **An offline replay never reads the book (U-press-atomic's declared gap).** `GatewayPipeServer.CancelAll` (~:888)
   and `CloseAll` (~:1399) still read the book before calling the synchronous `BeginComposite`, so an agent's replay
   with the connector unreachable fails on the read even though the gateway's `BeginCompositeAsync` takes the capture
   as a delegate and never runs it on a replay. RED first: replay a known composite id over the real pipe with the
   simulator disconnected → expect the stored outcome, zero reads; GREEN: both call sites adopt `BeginCompositeAsync`
   (one call-site change each); mutant (back to the read-first order) → RED. U-pipe-hello also touched three argument
   descriptions in `GatewaySchema.cs` — leave them.

Yours: `src/TradeAgent.Gateway/GatewaySchema.cs`, `GatewayPipeServer.cs` (the two answer shapes and the two composite
call sites), `AGENTS.md`,
`docs/CONTRACTS.md`, tests. Not yours: `TradingGateway.cs`, the hello/frame/status paths (U-pipe-hello), the
connectors. Every item: RED quoted, GREEN, one mutant watched red (commit before mutating; `cp` restore; `touch`).
Test-name diff vs baseline: nothing removed. Commit per item, no trailers, no push, no other worktree. Gate: Release
`--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED → GREEN → mutant; the schema
sentences swept and changed; final counts; what you did NOT do. Verified or NOT VERIFIED.
