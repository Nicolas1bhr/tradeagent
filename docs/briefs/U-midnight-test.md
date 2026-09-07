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
