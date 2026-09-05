# U-pipe-replay — an agent's offline replay of a sweep never reads the book

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (the replay contract and the
composite binding U-press-atomic wrote), then the U-press-atomic and U-pipe-words sections at the end of
`BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min
in Release. No box. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-pipe-replay`, new branch
`u-pipe-replay` from `main` — after U-pipe-words has landed (both touch `GatewayPipeServer.cs`).

**The gap, measured by two units and fixed by neither.** U-press-atomic made `TradingGateway.BeginCompositeAsync` take
the capture as a delegate and never run it on a replay of a known outer request id, and bound the replay to the verb
and session that created it. `GatewayPipeServer.CancelAll` (~:888) and `CloseAll` (~:1399) still read the book BEFORE
calling the synchronous `BeginComposite`, so over the pipe, after a completed sweep, with the connector unreachable
(`Faults.Disconnected`), the same id answered `ok=False code=TRADING_CONNECTION_MISSING` with one connector call during
the replay — instead of the stored outcome with zero calls. U-pipe-words wrote that RED test and did not commit it,
because the async entry point was not yet on `main`; it is now.

1. **One change at each call site.** `CancelAll` and `CloseAll` call `BeginCompositeAsync`, with the book read moved
   into the capture delegate, so a replay returns the stored outcome before any live read and the verb/session binding
   is the one gate. RED first, over the real pipe, both sweeps: complete a sweep, `Faults.Disconnected`, replay the same
   id → expect the original answer and `connector calls during the replay : 0`; today `TRADING_CONNECTION_MISSING` and 1.
   GREEN; mutant (the read back before the call) → RED. Other direction: a NEW id with the connector unreachable still
   answers `TRADING_CONNECTION_MISSING`, and a fresh sweep still reads the book exactly once.
2. **The contract says so.** `docs/CONTRACTS.md`'s replay paragraph states that a replayed sweep performs no read; the
   `AGENTS.md` template (via `WorkspaceBuilder`) says the same to the agent in one sentence.

Yours: `src/TradeAgent.Gateway/GatewayPipeServer.cs` (the two call sites only), `WorkspaceBuilder.cs` (one sentence),
`docs/CONTRACTS.md`, tests. Not yours: `TradingGateway.cs`, `GatewaySchema.cs`, the connectors. RED quoted, GREEN, one
mutant watched red (commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit
per item, no trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release
→ 0 failed.

## Report — append as you go, commit with each item, ≤12 lines: tip sha; RED → GREEN → mutant; final counts; what you
did NOT do. Verified or NOT VERIFIED.

## Report — fresh fixer, 2026-09-05

Branch `u-pipe-replay`, rebased onto `main` `25586ee`; 4 commits, 4 files of substance (+236/−17); last code commit `eb0f959`, this report on top of it.
1. **RED over the real pipe**, new `OfflineSweepReplayTests` (4 tests, 2 red): completed sweep, `Faults.Disconnected`,
   same id replayed → `ok=False {"code":"TRADING_CONNECTION_MISSING","message":"simulator is disconnected"}` and
   `connector calls during the replay : 1`, for cancel-all AND close-all — exactly what U-pipe-words measured.
   **GREEN** after one change at each call site (`BeginComposite` → `await BeginCompositeAsync`, the book/position read
   moved into the capture delegate): `ok=True`, the first answer byte-for-byte, `connector calls during the replay : 0`.
   **Mutant** (the read hoisted back above the call at both sites, the delegate handed the already-read list) → the same
   2 RED at `... : 1`; `cp` restored, `touch`ed, re-run 4/4.
2. Other direction, green: a NEW id with the connector unreachable still answers `TRADING_CONNECTION_MISSING` on both
   sweeps and claims no `composite_request` row; a fresh sweep reads once (`order reads it made : 1`, `position reads
   it made : 1`).
3. `docs/CONTRACTS.md`'s replay paragraph now states a replayed sweep performs NO read; the `AGENTS.md` template says it
   in one sentence, pinned by an assertion in the existing `The_agent_is_told_the_things_it_must_not_get_wrong`; second
   mutant (sentence deleted) → RED `Not found: "reads nothing from the platform"`, restored.
Gate, Release: `--no-incremental` → 0 Warning(s), 0 Error(s), 17 projects. Suite pre-rebase at `f3e8847`: 218 + 218 +
582 = 1018, 0 failed. Suite at the rebased tip: 218 + 218 + **581/582**, the one failure
`GatewayPipeBackpressureTests.A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled` thrown by its own setup
(`PipeClient.ConnectAsync` → "the trading service closed the connection", 10 ms) in the `Category=Timing` class, while
another builder's full suite ran on this Mac; that class re-run 3× at the tip → 34/34 each. Names vs `main`: 0 removed,
4 added. **NOT done:** `TradingGateway.cs`, `GatewaySchema.cs`, the connectors untouched; sync `BeginComposite` left in
place (the two operator press paths still call it); no box, no ATAS, no UI, no push, no other worktree.
