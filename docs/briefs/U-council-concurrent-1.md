# U-council-concurrent-1 — two roles' turns may overlap: every single-slot assumption the serial council allowed is made per role
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (`:259-260` serial "so leases can wait"; `:79` leases in the execution record; `:30-31`
rule 7, quiescence of EVERY managed agent; `:26-29`; `:99-101`), then `MissionLoop.cs` (`:949` "ONE ROLE PER TURN, AND ONE TURN AT A TIME", `LoopAsync`,
`TurnAsync`, the single fields `_sessionTurns`/`_consecutiveErrors`/`_reportedCap`, the per-role `_cutLastTurn`), `TurnMeter.cs` (`_open` per role since
`U-budget-reserve`; `_staged` ONE slot `:461,571,585-594,637`; the ctor's `LoseOpen` `:485-494`), `AiAttemptStore.cs:255-280,340,375-386`, `CouncilRelay.cs:
141-158,183,206-285,317-327,365-385`, `AgentPresence.cs:65-68`, `MaterialScanner.cs:29`, `MissionEventStore.cs:349,449`, `Database.cs:17,68-84` (one lock,
re-entrant), `AppHost.cs:214-217` (stale), `CONTRACTS.md:640-646,859-862` (the app's two leases are in memory, deliberately). Branch `u-council-concurrent-1`,
worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-council-concurrent-1`, rebased onto `main` first. Money-path grade: every guard ships
with a test RED before it and ONE mutant, quoted. No box, no ATAS, no money, no order. **No schema number** (the choice below).

**Why.** Nothing prevents a second `TurnAsync`; `_staged` is one slot, so role B's close can land inside role A's transaction (named by
`U-budget-reserve`); a second `TurnMeter` over the same file marks EVERY live launch LOST; `Run(role: null)` walks both roles' `out/` and quarantines
the other's in-flight file; quiescence is process-wide, so any overlap records every dropped file `InboxUnattested`; one `_sessionTurns` rotates
whichever conversation runs. COUNCIL is silent on where a lease lives — THE CHOICE: in memory (as `CONTRACTS.md:643-646` chooses for the app's two
leases), the LAUNCHED `ai_attempt` row its durable witness, reconciled by `LoseOpen` on the next start; said so in `CONTRACTS.md`. Two roles, one turn each.

1. **A per-role turn lease.** Taken before `BeginTurn`, released in the `finally` beside `Commit`; a second `TurnAsync` for the same role is
   REFUSED in words, never queued; the loop may launch the other role while one is turning (the chair's busy check and the owner's chat
   serialisation unchanged). RED (the `Barrier(2)` idiom of `BudgetReservationTests:65-107`): two threads on one loop both admit and both
   consume the same wake — `Expected: 1 / Actual: 2` LAUNCHED rows for the role. Mutant (the lease taken after `BeginTurn`): the same.
2. **`_staged` per role, keyed by attempt.** `CommitStaged(role)` commits only that role's held close; `Begin`'s opportunistic drain drains only
   its own role's. RED: A stages, B ends, A commits → A `Expected: ENDED / Actual: LAUNCHED`, B's close inside A's transaction. Mutant
   (`CommitStaged` ignoring the role): the same.
3. **`LoseOpen` loses only what no live turn in this process holds:** a registry of live attempt ids (in memory); the ctor pass and any second
   meter over the same file skip them; every other LAUNCHED row still becomes LOST at its reservation. RED: a second `TurnMeter` built mid-turn →
   `Expected: LAUNCHED / Actual: LOST`, the day short by a reservation. Mutant (filtered by role instead of by liveness): the other role's live
   turn lost.
4. **The relay pass is the turning role's own, and never holds the lock across disk:** `Run(role, attempt)` touches only that role's `out/`;
   the reconcile-everything pass runs only on start when nothing is live; the files are READ before the `Database.Write` opens so one role's
   commit never blocks the other's turn on I/O. RED: A's pass quarantines B's in-flight `report-<b>.md` (`Assert.Empty(Quarantined(research))`).
   Mutant (`Attributable` accepting any LAUNCHED row): a stale process's file published as live work.
5. **Quiescence still means every managed agent, and the loop's counters are per role:** a scan pass runs only when no role is turning;
   `_sessionTurns`, `_consecutiveErrors` and `_reportedCap` per role; the card names every turning role. RED: with two roles overlapping a
   dropped file is `Expected: Inbox / Actual: InboxUnattested`, and A's five failures rotate B's session. Mutant (quiescence asked of the
   turning role only): the attestation red. `CouncilLoopTests`' `Peak == 1` becomes the measured `Peak == 2`, and `AppHost.cs:214-217` is rewritten.
Not this unit: the consequential boundary, assessments, challenges, dispositions and the trial-budget race (`U-council-concurrent-2`); a third
role; the chair on the harness; the allocator; two active roles on a screen beyond the card's line.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, the lease choice restated, what you did NOT do.
