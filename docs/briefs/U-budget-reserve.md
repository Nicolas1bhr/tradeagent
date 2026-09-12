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

## Report

Tip `f8abb57`: rebased onto `u-turn-commit` `0da64d7`, its four conflicts resolved inside the rebase, then onto `main`
`b5803f8` (its last commit is docs only, landing after the gate; `src/` and `tests/` identical). Nine commits, clean
tree, this report sits on it. Gate, Release, every run mine: `clean` + `--no-incremental` → `0 Warning(s)  0 Error(s)`;
the 9 touched classes 3× → `Failed: 0, Passed: 88` each; unit 629, fault 277, integration 627 with 1 skipped (as on
`main`), all `Failed: 0`. Names 1198 → 1212, NONE removed. No `Timing` red, no orphaned runner.
1. RED, the guard removed: `Expected: 1 / Actual: 2` LAUNCHED rows from two racing threads. Mutant, the check moved back
   outside `Database.Write`: `Expected: 1 / Actual: 2`, on three runs of three.
2. RED, one `_open` and the chat unadmitted: `Assert.All() Failure … Expected: 1.28 / Actual: 0`, the dumped row being
   `Role = research, ReservedCost = 0` holding the Research turn's usage. Mutant, the chat skipping `Begin`: the same.
3. RED, `cost=$cost` as before the unit: `Expected: 1.28 / Actual: null`. Mutant, `cost` left NULL while the row is
   still MARKED unreported: `Expected: 1.28 / Actual: null`.
4. RED, the allowance priced as plain input: `Expected: 6.40 / Actual: 5.200`. Mutant, the max dropped: `Expected: 6.40
   / Actual: 5.20`, in the formula and in the committed row.
Fifth commit KEPT and tested: without it a refusal eats the owner's question. Its untested half — the refusal recorded
against their message — is now pinned too (mutant `Expected: "blocked" / Actual: null`).
Gaps closed: the card, the AI's line and the report's two places (mutants `Expected: 1 / Actual: 0` and
`Assert.Contains() Failure: Sub-string not found`); the provider-side ceiling, absent from both docs, now stated.
NOT done: no box, no ATAS, no money. `_staged` is still ONE slot, safe only while the loop is serial
(`U-council-concurrent`); the owner's chat close is written at once, which is what keeps the two from colliding today.
