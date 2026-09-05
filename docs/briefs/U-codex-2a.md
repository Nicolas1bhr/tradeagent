# U-codex-2a — four read-only claims on the gateway's risk and authority path: each turns RED first, or is refuted

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md`, the `## Codex` block of
`docs/REVIEW-2026-09-05b.md` (F1, F2, F5, F18) and the `U-gates`, `U-press-atomic` and `U-pipe-replay` sections of
`BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/codex-2a`, branch `u-codex-2a` from `main`. These are Codex's
READ-ONLY claims: for each, write the probe Codex names first. RED → fix it and watch one mutant; GREEN → keep the probe
as the test that refutes the claim and write one sentence saying why the code holds. Rebase onto `main` before you
start and again before the gate: `U-override-lease` and `U-press-inflight` land in `TradingGateway.cs` while you work;
resolve your own conflicts.

1. **F1 — `MaxOpenPositions` counts filled positions only** (`TradingGateway.cs:741`), so two opening orders in flight
   both pass a cap of one. Probe: cap 1, empty account, two different-symbol buys barriered before their fills → at most
   one connector place call. Fix direction: open work (`DISPATCHING`/`UNKNOWN`/working opening requests) counts with
   filled positions, decided inside the gate.
2. **F2 — the notional gate swallows an instrument-read failure and substitutes `ContractSize = 1`** (`:757`), and the
   ATAS mapping's `LotSize` is unproved, so futures exposure is understated. Probe: missing metadata, and real ES
   metadata with a cap between the raw and the multiplied notional → both refused before the connector. Fix direction:
   missing metadata fails CLOSED with a sentence; the multiplier's provenance is stated in `docs/CONTRACTS.md`.
3. **F5 — dispatch re-authorization is not atomic with `SetMode`/`ActivateLive`/`StopAiTrading`/`Update`** (`:1234`):
   a revoke between the re-check and the wire still sends. Probe: pause the fake connector immediately before its
   simulated wire write, revoke, release → what reaches the wire. State the bound honestly: if the window is the
   connector's own send and cannot be closed without holding the gate across the wire, say so in `docs/CONTRACTS.md`
   with its measured width and refute; if it closes cheaply (the revoke cancels the pending send), close it.
4. **F18 — a second concurrent composite call treats a still-running owner as crash recovery** (`:1954`), can observe
   `DISPATCHING` legs, and can persist that transient answer. Probe: block the first sweep's connector call, issue the
   same request id concurrently → the duplicate waits for, and returns, the owner's final stored result.

Yours: `TradingGateway.cs` (risk checks, the dispatch gate, the composite owner), `RiskPolicy`, `docs/CONTRACTS.md`,
tests. No pipe op, no UI. Commit per item, no trailers. Gate: Release `--no-incremental` → 0 warnings; each class 3×;
full suite once to a file (one suite at a time on this Mac; a `Timing` test that fails while another suite runs → that
class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant or REFUTED with the probe's output;
gate counts; what you did NOT do. Verified or NOT VERIFIED.
