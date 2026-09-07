# U-midnight-test — a U-wakes test that goes red for any run started within ten minutes of local midnight
Read `docs/HOW-WE-BUILD.md` (step 6, the `Timing` paragraph, "The fresh-fixer rule"), `CLAUDE.md`, the `## 2026-09-07 —
U-wakes landed` section of `BUILD-STATUS.md`. Branch `u-midnight-test`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-midnight-test`, rebased onto `main` first. Test-only; no product code;
no assertion loosened; no box, no ATAS, no money.

**The red, found by the `U-data-binance` builder on this Mac at 23:53:56 local:** `MissionLoopTests.The_delay_the_ai_asks_
for_becomes_an_event_that_survives_the_loop` → `Range: (00:09:50 - 00:10:00) / Actual: 00:06:03.7870220`. The loop's
`Schedule` (`MissionLoop.cs`, the `Renewal` at `LocalMidnightAfter(now)`) raises the midnight renewal AHEAD and the loop
returns the earliest due event, so a `next.json` asking for ten minutes is beaten by a renewal six minutes away whenever the
wall clock is within ten minutes of local midnight. The same test passed at 00:06. The product is right (midnight IS the
earlier event); the fixture read the real clock. Not in `Timing`, and it must not join it: its verdict needs no runner
clock, it needs a clock it controls. On the hosted runners "local" is UTC, so this red would land on any CI run that
reaches this test between 23:50 and 00:00 UTC.

1. **The 12:00 pin landed with `U-council-thin`** (its builder pinned this test's clock to midday, assertion unchanged) —
   verify it is on `main`, then ADD the other branch explicitly: with `now` at 23:55 local the wait returned is the
   renewal's (≈ 5 min), because that is the product's rule. RED first with `now` = 23:55 against the old range assertion
   (quote the range failure), then GREEN with both cases; mutant (the renewal scheduled a day late) → the 23:55 case red.
2. **Sweep the class:** every test in `MissionLoopTests.cs`, `MissionEventTests.cs`, `MissionOwnerMessageTests.cs` and
   `QuiescenceBarrierTests.cs` that reads the real clock through a loop or store without injecting `now` (grep `new
   MissionLoop(` without `now:`, `DateTimeOffset.Now`, `UtcNow` in those files); list them; inject a fixed clock where the
   verdict depends on the wall clock; name the rest as not at risk and why (a test that only needs "some time" is fine).

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the Unit project 3× and the three test projects
in Release to a file → 0 failed; test names vs `main` → nothing removed. Commit per item, one sentence, no trailers. Append
`## Report` (≤20 lines): tip sha, the gate counts pasted, the RED and mutant output quoted, the sweep's list, what you did NOT do.

## Report

1. **The 12:00 pin IS on `main`** (`git show`): `now: () => noon`, `InRange(590, 600)` unchanged. Added
   `Within_five_minutes_of_midnight_the_renewal_beats_the_delay_the_ai_asked_for`. RED, at 23:55 vs the old range:
   `Assert.InRange() Failure: Value not in range / Range:  (00:09:50 - 00:10:00) / Actual: 00:05:00`. GREEN:
   `Assert.Equal(TimeSpan.FromMinutes(5), wait)`, exact on a pinned clock, plus the AI's own wake unconsumed at +10 min.
   Mutant (`LocalMidnightAfter` `AddDays(1)`→`AddDays(2)`): `Assert.Equal() Failure: Values differ / Expected: 00:05:00 /
   Actual: 00:10:00`, 1 of 32 red and not the midday test. Reverted.
2. **Sweep, the four classes' 40 tests.** A pinned `Midday` injected into the 6 whose verdict is a wait, a turn count or
   the card's line: `With_nothing_due_ticks…`, `One_due_event_is_one_turn…`, `The_review_tick_…_scheduled_ahead…` (`>
   UtcNow` → `> Midday`), `The_card_says_what_the_loop_is_waiting_for` (two-way `or` TIGHTENED to one string), the landed
   midday test, `A_message_typed_while_it_worked…`. Not at risk: 16 `MissionLoopTests` with no queue (`Events` null →
   `Schedule`/`Idle` never run) or no loop; `The_wake_is_consumed…` and 3 owner-message tests (one turn, and an extra due
   renewal is taken by that turn and is not an owner row); 9 `MissionEventTests`, 3 `QuiescenceBarrierTests`.

**Gate on tip `2ec874f`** (rebased onto `main` `6aa103e`, no conflict; test-only): build `-c Release --no-incremental` → 0
Warning(s), 0 Error(s); Unit 3× → 559 passed, 0 failed each run; Release Unit 559 + Fault 277 + Integration 621 = 1457
passed, 0 failed, 1 skipped, output files deleted; names vs `main` → 0 removed, 1 added (1252 → 1253). `U-sweep-win`'s
suite ran beside mine; nothing failed. **NOT done:** no product code, no assertion loosened (two tightened); no `Timing`
membership; no box, no ATAS, no money.
