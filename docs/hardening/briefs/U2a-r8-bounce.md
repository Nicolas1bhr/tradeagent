# U2a — ROUND 8 BOUNCE · Codex delta on round 7 (`records/codex-U2a-r7.txt`): 1 HIGH / 1 MED / 1 LOW (+ verifier r7, below)

**Same builder, same worktree.** 8 of 11 priors FIXED. The three "NOT FIXED" record items (PRIOR 12/14/F-F) are a
bookkeeping artefact — records live on `main` and the branch carries the 2026-09-03 snapshot, so Codex read stale text;
REFUTED with that reason, no action beyond one sentence at the top of the branch's copy saying where the live record is.

## Direction

- **F1 (HIGH, class: the clock is per `Rpc`, not per emergency OPERATION).** Prerequisite reads and every cancel-all leg
  each restart the 2 s budget (`AtasConnector.cs:974`): three legs delayed 1.9 s each → IPC `cancel-all` takes ≈5.7 s.
  Rule: the `RiskReducingScope` carries ONE absolute deadline; every RPC inside the operation (the orders read, each
  target resolution, each cancel/close leg, the position read) gets `deadline − now`, never a fresh 2 s. Legs that cannot
  be sent before the deadline are NOT silently skipped: the caller's answer at the deadline reports per-leg outcomes
  (sent-and-confirmed / sent-not-confirmed / not sent) so the owner knows which orders may still be working; the
  gateway records UNKNOWN for the unconfirmed ones (U2c-1 consumes it). Where the connector allows, send the legs
  concurrently rather than serially. Acceptance: Codex's check (three 1.9 s delays → ≈2 s, not 5.7 s) plus a sweep of
  five orders under a 1 s-per-leg connector → answer at ≈2 s with the per-leg outcomes listed and nothing skipped
  silently.
- **F2 (MED).** The drain covers one connector call plus a literal 5 s (`GatewayPipeServer.cs:161`), not a composite
  handler: `LatencyMs=4000`, one working order, IPC `cancel-all`, dispose after its first read → derived drain 9 s vs
  12 s needed → the active cancel stays `DISPATCHING`. Rule: the drain is derived from the handler's worst-case
  COMPOSITE (the number of serial RPCs an operation can issue × the per-RPC bound, plus the write ceiling), and disposal
  never returns with the request unsettled (the round-5 rule) — Codex's check is the acceptance.
- **F3 (LOW).** A reply racing between the caller's timeout and `_abandoned` registration can remove `_pending` first,
  after which the judge sees `CompletedTask`; no late-answer counter records it and `_abandoned` leaks the id. Close with
  the F1 rework (register before releasing the caller; late answers counted; no leak).

## Process

As before; append `## Round 8 (build record, <date>)` to `records/U2a.md` (MAIN worktree) AS YOU GO; targeted classes,
then `dotnet build TradeAgent.sln` + FULL suite once on the Mac; the box grant for ONE verified run (pipe classes + full
suite). Report: tip sha, RED → GREEN → mutant per finding, suite counts (Mac + box), "What I did NOT do".

## Verifier round-7 findings (appended by the manager when leg [2] reports)

_pending_
