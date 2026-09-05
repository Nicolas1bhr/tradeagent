# U-gates — every gate is evaluated at dispatch, for every mutating verb, and an unknown mode fails closed

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the safety rules; operator authority in-process only;
anything that moves money is two-press), `docs/CONTRACTS.md`, then Codex findings F2, F3 and F4 in
`docs/REVIEW-2026-09-05-codex.md` — read-only claims you must first turn RED. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-gates`, new branch `u-gates` from `main`.

The class: a request reaches the connector without every gate that applies to it. Three instances, one rule — every
mutating verb passes the same authorization, approval and risk path, that path is decided at the moment of dispatch,
and anything the gates cannot classify is refused.

1. **Modify goes through the gates (F2, `TradingGateway.cs` ~:1083).** Agent-facing Modify bypasses LIVE_CONFIRM
   approval and the quantity, notional, position, instrument and rate checks. RED first: in LIVE_CONFIRM with
   `MaxOrderQuantity = 1`, modify a working quantity-1 order to quantity 1000 through the authenticated pipe → the test
   expects AWAITING_APPROVAL and zero connector calls, and fails today. Then the fix: Modify parks and is risk-checked
   exactly as Place is, on the order's resulting size. Other direction: a modify within limits in SIM still applies.
2. **An unknown mode fails closed (F3, `Core/Trading.cs` ~:54).** An undefined numeric `TradingMode` in settings (seed
   `"mode": 999` with `live_activated: false` and a selected real account) is classified non-live and executes. RED
   first: restart with that seed and submit a buy → expect startup refusal or OBSERVE with zero sends. Fix: parsing
   refuses any value that is not one of the three named modes, and the refusal reaches the owner in the app's words.
3. **Gates decided at dispatch (F4, `TradingGateway.cs` ~:531).** Authorization and risk run before awaited reads and
   before the dispatch gate, so an order authorized before STOP or live-off still sends after it, and concurrent
   orders all pass one shared rate limit. RED first, with a barrier: authorize a buy, then call `StopAiTrading` (and,
   separately, `ActivateLive(false)`) while the connector is held, release it → expect zero sends; and N concurrent
   orders against a rate limit of 1 → expect exactly one send. Fix: re-check STOP, live activation and mode at the
   dispatch gate after every awaited read, and make the rate limit an atomic reservation.

Yours: `src/TradeAgent.Gateway/TradingGateway.cs` and `GatewayTypes.cs`, `src/TradeAgent.Core/Trading.cs`, `Errors.cs`,
`docs/CONTRACTS.md`, tests. Not yours: the pipe server (U-pipe-hello), the press and composite regions of the gateway
(U-press-atomic), the updater (U-interlock), the connectors. Every item: the RED quoted (or, if you cannot make it
red after an honest attempt, say the finding is refuted and why, with the probe), GREEN, one mutant watched red
(commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED (or refuted, with the probe) →
GREEN → mutant; final counts; what you did NOT do. Verified or NOT VERIFIED.
