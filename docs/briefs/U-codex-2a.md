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

Fresh builder, 2026-09-06. Code tip `c64e9b8`; this report commits on top. Rebased onto `main` five times, all clean.
**Kept all four of the previous builder's uncommitted files, discarded none.** Its F1 direction is right, and its
quote-barrier move in `RecordingConnector`/`DispatchGateTests` is forced by it: the position read sits inside the gate now, so a barrier there can never assemble a wave.
**F1 RED→GREEN→mutant.** RED on `main`'s gateway under the same tests: `cap 1 / connector place calls: 2 / positions open: 2`; a WORKING opening order read `positions reported: 0` and was not refused. Mutant `>=`→`>`: `place calls: 2`.
Cost: `A_cold_placement_…drain_assumes` names the same FIVE ops, `positions` 2nd→4th — count, and so the drain, unchanged.
**F2 RED→GREEN→mutant.** Cap between the raw and the ×50 notional; three ways the multiplier goes missing (read throws / null `ContractSize` / zero). RED: all three `Assert.Throws() Failure: No exception was thrown` — the order went out.
GREEN: `RISK_CHECK_UNAVAILABLE`, 0 place calls, no row. Mutant dropping `|| size <= 0` → the zero row red. The fake now describes `YM` (it already traded it); `XYZ` stays undescribed on purpose and its two tests set no value cap.
**F5 — half fixed, half REFUTED.** RED: Stop pressed inside a close's stale-position re-read → `outcome: ok — FILLED / orders at the broker: 2 (was 1)`. GREEN by making the re-check the last thing before the wire; mutant (delete it,
keep the pre-read one) red identically. Codex's own probe REFUTES: paused inside the connector's send the order is placed and cannot be recalled — `CONTRACTS.md` states the bound, one `WorstCaseOperationPath` = 50 s at shipped ATAS
values (`Stranded.AtasOrderPath`; NOT re-measured here).
**F18 RED→GREEN→mutant.** RED: owner `{"cancelled":2,…}` vs duplicate AND stored `{"cancelled":1,["FB-1=DISPATCHING",…]}` — the transient answer persisted first. GREEN with an in-memory owner lease; mutant (no re-read after
the wait) → the duplicate recomputes an equal answer, `from the store: False`.
**Gate**, run alone, on the tree this tip carries: Release `--no-incremental` → 0 Warning(s), 0 Error(s); Unit 236 + Fault 261 + Integration 587 = 1084, 0 failed; touched classes 3× (29/29; backpressure 34/34); names vs `main` 0
removed, 9 added. The only `main` commit after that run is `be65c25`, BUILD-STATUS.md only, rebased onto and not re-gated.
**NOT done:** no box, UI, pipe op or operator authority; F5's connector-send half is refuted, not closed; the press's sync `BeginComposite` takes no lease (fresh-nonce `op-` ids the pipe refuses); a MODIFICATION no longer runs the
open-position cap (in `CONTRACTS.md`).
