# U-pipe-hello — the pipe refuses what it cannot name, counts what it says it counts, and answers for the caller

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` and `AGENTS.md` (the pipe's
public contract), then Codex F8 in `docs/REVIEW-2026-09-05.md` (a read-only claim: turn it RED first or refute it) and
findings 7 and 10 and UNVERIFIED 6 there, with probes P7 and P9 on branch `review-probes`
(`tests/TradeAgent.IntegrationTests/ReviewPipeProbes.cs` — lift them). `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box. Fresh worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/u-pipe-hello`, new branch `u-pipe-hello` from `main`.

1. **Protocol before session (F8).** A hello with `v = current + 1` still authenticates. RED first: such a hello, then
   a buy → expect refusal at the hello (no session; INCOMPATIBLE_PROTOCOL; one log line). Fix: the version check
   precedes credential acceptance. Other direction: the current version still authenticates and trades.
2. **TIF fails closed (F8).** `tif: "ImmediateOrCancle"` and `tif: "999"` silently become Day. RED first → expect
   INVALID_REQUEST naming the field and the accepted values, zero connector calls. Fix: every enumerated field the
   frame carries (tif, side, type — list them) accepts exactly its named values; a present-but-unrecognised value is
   refused; an absent field keeps the default `docs/CONTRACTS.md` states (write it if it does not).
3. **The frame cap counts bytes (finding 10, P9).** `ReadFrame` compares `StringBuilder.Length` (UTF-16 chars) with
   `MaxFrameBytes`; a 2,700,096-byte frame passes a "1 MiB" cap, before the hello check, for an unauthenticated peer.
   Fix: count encoded bytes on the wire; the refusal drops the peer as the backpressure rules do.
4. **Status answers for the caller (finding 7, P7).** `StatusAsync` evaluates `TryAuthorizeExecution(AgentContext.Operator)`,
   so an agent with the kill switch down reads `execution_available: true` and no reason, while its own buy is refused
   `AI_TRADING_STOPPED`. Fix: `status` and `schema` are computed with the caller's context; the reason field is present
   whenever execution is not available for THAT caller.
5. **An agent reads only its own records (UNVERIFIED 6).** `trade order op-close-<nonce>-ES` returns the operator's
   press record through `FindOrder → GetRequest(id)`. Fix: the agent path resolves agent-prefixed ids only; an operator
   id answers NOT_FOUND in the replay contract's words. Both directions: the agent's own ids still resolve.

Yours: `src/TradeAgent.Gateway/GatewayPipeServer.cs`, the frame parser wherever it lives (name it), `Ops`, the CLI
wording in `src/TradeAgent.TradeCli`, `docs/CONTRACTS.md`, `AGENTS.md`, tests. Not yours: `TradingGateway.cs` (U-gates,
U-press-atomic, U-stranded own it), the updater, the connectors. Every item: RED quoted (or F8 refuted with the probe),
GREEN, one mutant watched red (commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing
removed. Commit per item, no trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full
suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED (or refuted) → GREEN → mutant;
the enumerated fields covered; final counts; what you did NOT do. Verified or NOT VERIFIED.
