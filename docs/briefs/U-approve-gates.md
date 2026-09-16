# U-approve-gates — an approval runs every gate a placement runs, on one position reading, and records the allocation it went out under
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (two-press; an approval is a dispatch decision authorized at the moment it is made), `docs/COUNCIL.md:14-15` (EVERY order passes
the five gates), `docs/REVIEW-2026-09-16.md` finding 4 with its probes `C3a` and `C3b`, `docs/CONTRACTS.md` ("An approval is a dispatch decision…", `U-allocator-1`), then
`src/TradeAgent.Gateway/TradingGateway.cs:3826-3851` (`PlaceAsync`'s four gates on the one position reading: `OpenPositionCapOrThrow`, `RefuseAnUnresolvedReducerOrThrow`,
`LossBudgetOrThrow`, `AllocationCeilingOrThrow`, then `TryCreate` with `AllocationId`), `:4786-4790` (`ApproveAsync` re-running only two of them), the probe tests on
branch `review-probes-c` @ `58aa4fa` — your RED tests: bring them over, quote them red on your base, make them green. Branch `u-approve-gates`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-approve-gates`, from `main`. **Money path:** a test RED before the guard and ONE mutant, quoted, over
`RecordingConnector`. No schema. No box, no money. One item; a light leg.

**Why.** `ApproveAsync` drops the capital gate and the reconciliation refusal: a parked order is approved onto a ceiling the owner has since withdrawn and is attributed
to an allocation that no longer stands (`record.AllocationId` never recomputed), and a parked reduce is approved over an order this gateway cannot account for — the
doubling `CLOSE_UNRESOLVED` exists to refuse. Two arms, one cause: two copies of the gate sequence.

1. **One gate sequence, two callers.** The four position gates move into ONE method taking `(intent, positions, account, requestId)`; `PlaceAsync` and `ApproveAsync` both
   call it on their own single position reading inside `_dispatchGate`, in the same order, and the approval path assigns `AllocationId` from the answer before dispatch —
   a withdrawn or exceeded ceiling refuses `ALLOCATION_*`, an unresolved reducer refuses `CLOSE_UNRESOLVED`, exactly as a fresh order is refused. RED: `C3a` (fresh:
   `ALLOCATION_EXCEEDED`; parked: SENT) and `C3b` (fresh: `CLOSE_UNRESOLVED`; parked: SENT, ES 4 → ES 1 on two reducers). Mutant (the approval path calling the old
   two-gate sequence): both red again. Second mutant (`AllocationId` not reassigned): the sent order attributed to the withdrawn allocation — assert the row.
Not this unit: the per-order limits above the gate (MED 7, `U-review-med`); the approval TTL; the mode re-check (already re-run).
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched classes 3×. Commit per
item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the REDs and mutants quoted, what you did NOT do.
