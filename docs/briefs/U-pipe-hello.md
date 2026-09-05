# U-pipe-hello — an incompatible hello never authenticates, and a time-in-force the pipe cannot name is refused

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` and `AGENTS.md` (the pipe's public
contract), then Codex finding F8 in `docs/REVIEW-2026-09-05-codex.md` — a read-only claim you must first turn RED.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box.
Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-pipe-hello`, new branch `u-pipe-hello` from `main`.

**The claim (F8, `src/TradeAgent.Gateway/GatewayPipeServer.cs` ~:556).** A hello whose protocol version is not the
current one still authenticates and gets a session; and a malformed or undefined time-in-force (`"ImmediateOrCancle"`,
`"999"`) is silently mapped to Day / ATAS Default, so an order the agent meant as IOC or FOK can rest on the book.

1. **Protocol before session.** RED first: authenticate with `v = current + 1` and attempt a buy → expect refusal at
   the hello (no session, INCOMPATIBLE_PROTOCOL in the reply, one engineering-log line), and today the buy is
   accepted. Fix: the version check precedes credential acceptance; a refused hello never yields a session. Other
   direction: the current version still authenticates and trades.
2. **TIF fails closed.** RED first: with the current protocol, send `tif: "ImmediateOrCancle"` and `tif: "999"` →
   expect INVALID_REQUEST naming the field and the accepted values, zero connector calls; today they become Day. Fix:
   the parser accepts exactly the named values and nothing else — no default for a present-but-unrecognised field; an
   ABSENT field keeps whatever `docs/CONTRACTS.md` says the default is (state it if it does not). The same rule for
   every enumerated field the frame carries (side, type): list them in the report.
3. **The contract says so.** `docs/CONTRACTS.md` and `AGENTS.md` state the accepted TIF values and the refusal; the CLI
   prints the same words.

Yours: `src/TradeAgent.Gateway/GatewayPipeServer.cs`, `src/TradeAgent.Core/Ops.cs` or wherever the frame parser lives
(name it), the CLI's wording in `src/TradeAgent.TradeCli`, `docs/CONTRACTS.md`, `AGENTS.md`, tests. Not yours:
`TradingGateway.cs` (U-gates and U-press-atomic own it), the updater, the connectors. Every item: the RED quoted (or the
finding refuted, with the probe), GREEN, one mutant watched red (commit before mutating; `cp` restore; `touch`).
Test-name diff vs baseline: nothing removed. Commit per item, no trailers, no push, no other worktree. Gate: Release
`--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; per item RED (or refuted) → GREEN → mutant;
the enumerated fields covered; final counts; what you did NOT do. Verified or NOT VERIFIED.
