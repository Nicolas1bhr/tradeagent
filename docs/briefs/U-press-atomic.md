# U-press-atomic — one emergency press at a time, and a replay bound to the verb and session that made it

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md`, the U2c1b section of
`BUILD-STATUS.md` (the press became its records; the composite replay), then Codex findings F6 and F7 in
`docs/REVIEW-2026-09-05-codex.md` — read-only claims you must first turn RED. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-press-atomic`, new branch `u-press-atomic` from `main` — after
U-gates has landed (both touch `TradingGateway.cs`; rebase onto that `main`).

1. **The second-press refusal and the first press row are one step (F6, `TradingGateway.cs` ~:1634).** The refusal
   check (`RefuseWhileAPressIsOpen`) and the first durable press row are not atomic, so two simultaneous presses can
   both pass and both send. RED first, with a barrier: two `OperatorCloseAllAsync` calls released together right after
   the check, both seeing the same open position → expect exactly ONE set of wire calls and one press, the other
   refused with "close-all sent at HH:MM; resolve it first". Fix: the check and the first write are one transaction or
   one lock held across both, and the press row's key is unique per open press. Other direction: a press after the
   previous one is resolved still goes to the wire.
2. **A replay is bound to its verb and session, and looked up before any live read (F7, ~:1355).** A composite's outer
   request id is not bound to the operation and session that created it, and the replay lookup happens after live
   prerequisite reads, so an incomplete Close All composite replayed as Cancel All resumes the wrong verb, and an
   offline replay is not safe. RED first: seed an incomplete Close All composite, replay its id as Cancel All while the
   first book read is empty → expect refusal (wrong verb), zero wire calls; and replay the right verb with the connector
   unreachable → expect the stored outcome with no read attempted. Fix: the composite row carries verb and session;
   the lookup is the first step of every mutating op; a mismatch refuses with INVALID_REQUEST in the replay contract's
   words; `docs/CONTRACTS.md` and `AGENTS.md` state the binding.

Yours: `src/TradeAgent.Gateway/TradingGateway.cs` (press and composite regions), `GatewayTypes.cs`, `Core/Db/Stores.cs`
and the schema (bump if a column is added; migrations must be checksummed as the existing ones are), `Errors.cs`,
`docs/CONTRACTS.md`, `AGENTS.md`, tests. Not yours: the pipe server, the updater, the connectors, the authorization
path (U-gates). Every item: the RED quoted (or the finding refuted, with the probe), GREEN, one mutant watched red
(commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED (or refuted) → GREEN → mutant;
final counts; what you did NOT do. Verified or NOT VERIFIED.
