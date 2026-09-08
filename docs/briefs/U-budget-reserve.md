# U-budget-reserve — every turn is admitted inside the transaction that reserves it, and an unreported turn keeps its reservation
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rules 3 and 4, "Budgets are reserved, not checked", the `U-budget-
reserve` line), `docs/RESUME-HERE.md` step 6, then `TurnMeter.cs` (`Reservation`, `Begin`, `Close`), `AiAttemptStore.cs`,
`Trading.cs` (`AiSpendToday`), `MissionLoop.TurnAsync` (`:823-922`), `AgentSession.SendAsync`. Branch `u-budget-reserve`,
worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-budget-reserve`, rebased onto `main` first. Money path: every
guard ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money.

**Why.** Admission reads the totals (`MissionLoop.cs:838`, `TurnMeter.Reading`) and the row is inserted sixty lines later
(`:922`, `AiAttemptStore.Begin`) under a separate lock: two admissions can both read "room for one". The owner's chat turn is
metered but never reserved or admitted (`AppHost.cs:156`, `TurnMeter.cs:524-527`) and can start beside a Research turn (the
chair's session is not busy, `MissionLoop.cs:862-867`); the meter holds ONE `_open`, so the second `Close` writes a fresh row
with `ReservedCost = 0`. A turn that ENDS without usage is `ENDED` with `cost` NULL: `Reserved` sums only `LAUNCHED`, so the
whole reservation is released and nothing replaces it (`AiAttemptStore.cs:162-181,223-249`) — rule 3 broken on the non-crash
path. The reservation prices cache writes at zero (`TurnMeter.cs:511`, `RuntimeManifest.cs:837`). The property (Astra, round 1):
two admitted attempts cannot both spend the same allowance, and a restart never releases an unresolved commitment as free.

1. **Admission inside the reservation's transaction.** `AiAttemptStore.Begin` reads the day's totals (global and the role's
   share) and inserts the row in ONE `Database.Write`; when the sum would pass the cap it inserts nothing and returns the
   refusal (which cap, by how much, `ResumesAt`); the loop's pre-check stays as a cheap first look but the transaction is the
   gate. RED: two admissions racing on one database (two threads, room for exactly one reservation) → both rows written
   (today); after: exactly one, the other refused. Mutant (the check moved back outside the transaction) → two rows.
2. **Every turn is reserved and admitted, the owner's chat included.** `AgentSession.SendAsync` opens an attempt through the
   meter (role: the chair) before launching; a refused chat turn never launches and the chat shows the cap sentence as a
   System line (words only, `Theme.cs` values only); the meter holds one open attempt PER conversation, and `Close` matches
   by attempt id. RED: a chat turn beside a Research turn writes a row with `ReservedCost = 0` (today) → refused or reserved;
   mutant (the chat path skipping `Begin`) → red.
3. **An unreported turn keeps its reservation.** An attempt that ends with no usage becomes `ENDED` with `cost = reserved_cost`
   and `unpriced_reason`; the report's "reserved and unresolved" and the card's "could not be priced, so it is at least
   that" count it; the day's `Spent` includes it. RED: an ENDED attempt with no usage leaves `Spent` at 0 (today) → the
   reservation; mutant (`cost` left NULL) → red.
4. **The reservation formula:** input at the DEARER of the uncached and cache-write rates + output at the output rate
   (reasoning is inside output for codex); `TurnAllowance` shown on the Safety page as two boxes beside the cap (one press;
   the note says a bigger allowance means fewer turns fit the day); `CONTRACTS.md` and the guide state the formula. RED: a
   model whose cache-write rate exceeds input reserves at input (today); mutant (the max dropped) → red.

Not this unit: a provider-side ceiling (none for codex; a stated limitation in `CONTRACTS.md`); true concurrency and the second-`TurnMeter` `LoseOpen` hazard (`U-council-concurrent`).

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
