# U-press-pipe-fix — one test from U-pipe-hello meets U-press-atomic's new press ids

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, then the U-pipe-hello and U-press-atomic sections at the
end of `BUILD-STATUS.md` and the `## Report` of `docs/briefs/U-press-atomic.md` (in the worktree below). `export
PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-press-atomic`, branch `u-press-atomic` @ `a30d12b`
(U-press-atomic, complete and rebased over U-pipe-hello, which is on `main`). First `git rebase main` (main has moved by
docs only). You fix the combination; you do not reopen either unit.

**What happened.** Each unit's gate was green alone. At the rebased tip, the manager's gate fails ONE test, at both a
relocated and the normal output path, in 14–93 ms:
`PipeContractTests.An_operator_press_record_is_not_readable_over_the_agent_channel` —
`System.InvalidOperationException: Sequence contains no matching element` at a `.First(predicate)` inside the test.
U-pipe-hello wrote that test against the press id shape of the day, `op-close-<nonce>-<symbol>`; U-press-atomic then
made the press mint `PressLegId(kind, nonce, index)` (`op-close-<nonce>-0`, the symbol on the record, not in the id).
The test's lookup no longer matches any row.

1. Make the test find the press record under the shape the gateway now mints (through `PressLegId`, or by reading the
   press record the gateway created — never by hard-coding a nonce). Its assertion stays exactly what it proves: an
   operator press record is NOT readable over the agent channel, answered as an id nobody minted; and the agent's own
   ids still resolve. RED is the failure above; GREEN; then the mutant U-pipe-hello used (`MayRead → true`) → RED
   again, proving the product half still bites for the new id shape.
2. If the product itself now leaks a new-shaped press id to the agent channel (the mutant would not go red, or a
   probe shows the row readable), say so first and fix `MayRead` for the new shape red-first; do not weaken the test.

Yours: `tests/TradeAgent.IntegrationTests/PipeContractTests.cs`; `GatewayPipeServer.cs` ONLY if item 2 applies (one
predicate). Not yours: `TradingGateway.cs`, the press ids, anything else. Commit per item, no trailers, no push, no
other worktree. Gate: Release `--no-incremental` → 0 warnings; `PipeContractTests` 3×; the full suite once in Release
(if a run ends with exit 1 and NO summary line, another leg's old app runner killed the host — re-run it).

## Report — append as you go, commit it, ≤12 lines: tip sha; RED → GREEN → mutant; whether item 2 applied; the counts;
what you did NOT do. Verified or NOT VERIFIED.
