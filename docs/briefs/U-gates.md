# U-gates — every gate is evaluated at dispatch, for every mutating verb, and an unknown mode fails closed

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the safety rules; operator authority in-process only;
anything that moves money is two-press), `docs/CONTRACTS.md`, then Codex findings F2, F3 and F4 in
`docs/REVIEW-2026-09-05.md` (the Codex section) — read-only claims you must first turn RED. `export PATH="$HOME/.dotnet:$PATH"
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
   orders all pass one shared rate limit. The milestone reviewer EXECUTED the kill-switch half as finding 6 (probe
   P3 on `review-probes`: Stop pressed 300 ms into a place whose connector reads cost 400 ms each → FILLED). RED first,
   with a barrier: authorize a buy, then call `StopAiTrading` (and,
   separately, `ActivateLive(false)`) while the connector is held, release it → expect zero sends; and N concurrent
   orders against a rate limit of 1 → expect exactly one send. Fix: re-check STOP, live activation and mode at the
   dispatch gate after every awaited read, and make the rate limit an atomic reservation.

Yours: `src/TradeAgent.Gateway/TradingGateway.cs` and `GatewayTypes.cs`, `src/TradeAgent.Core/Trading.cs`, `Errors.cs`,
`docs/CONTRACTS.md`, tests. Not yours: the pipe server (U-pipe-hello), the press and composite regions of the gateway
(U-press-atomic), the updater (U-interlock), the connectors. Every item: the RED quoted (or, if you cannot make it
red after an honest attempt, say the finding is refuted and why, with the probe), GREEN, one mutant watched red
(commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report

Code tip `0bee79b` (from `main` @ `798ed4b`); this report is the commit on top. No box, no push, no trailers.
1. **Modify through the gates (F2).** RED over the pipe, LIVE_CONFIRM + `MaxOrderQuantity=1`, quantity 1→1000: `ok=True`, `ACKNOWLEDGED`, `modify calls on the wire : 1`.
   GREEN: risk-checked on the RESULTING order and parked like a place — `RISK_LIMIT_EXCEEDED`, no record, 0 calls; in-limits LIVE_CONFIRM parks at 0 calls and the press sends 1;
   PAPER in-limits still applies; `RISK_CHECK_UNAVAILABLE` when the book cannot show the target. Mutant (check `before.Quantity`, not the requested one) SURVIVED the first test —
   `0bee79b` makes it name the gate, then it dies: `Expected: "RISK_LIMIT_EXCEEDED" Actual: "APPROVAL_REQUIRED"`.
2. **Unknown mode fails closed (F3).** RED: `"mode":999`, `live_activated:false`, `REAL-001 simulated=False` → `ModeAllowsExecution : True`, `ModeIsLive : False`,
   `TryAuthorizeExecution : True`, buy filled. GREEN: `ModeIsRecognised` gates execution, health `PAUSED` every refresh, `SetMode` refuses an undefined value, the owner gets a
   line naming 999, the value is NOT rewritten, four named modes unchanged. Mutant (`||` for `&&`) red: `Expected: MODE_FORBIDS_EXECUTION Actual: TRADING_PERMISSION_UNAVAILABLE`.
3. **Gates decided at dispatch (F4 / finding 6).** RED, barrier inside the risk check's position read: Stop → `ok — FILLED`, 1 at the broker; `ActivateLive(false)` → `ok — FILLED`;
   an approval whose mode moved to PAPER mid-check → `ok — FILLED`; 4 callers vs limit 1 → 4 fills. GREEN: 0 sends each; 5 callers vs a budget of 3 send exactly 3. Mutants: drop
   `AuthorizeOrThrow` from the re-check → both barriers red; check the budget only in the risk pass → both concurrency rows red. A third (take the place at `Commit`) SURVIVED —
   the place path holds `_dispatchGate` across check and commit, so the lock is load-bearing only for `modify`, which has no gate.

Gate: Release `--no-incremental` **0 warnings**; suite Release **201 + 207 + 534 = 942, 0 failed**; test names **+16, 0 removed**.
NOT done: no box, no real ATAS, no UI, no money. One property outside my files — `GatewayPipeServer.ModifyHandlerPath` 4W → 6W, since `modify` now issues a placement's chain and
the drain is the max over that table (arithmetic test 256 s → 306 s; `modify` is now the longest row). `ApproveAsync` gained a MODIFY arm. The re-check's mode arm is reachable
only on the approval path: a fresh place re-reads the mode when it builds its record, so it parks instead. Untouched: press/composite, pipe protocol, updater, connectors.
