# U-promote-bounds — the referee refuses to promote a version that declares no timeframe, data freshness or maximum decision age
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:96-97` ("A promoted strategy declares its timeframe, its required data freshness and its maximum
decision age"), the `## 2026-09-14 — U-freshness landed` section of `BUILD-STATUS.md` (the declarations are OPTIONAL and all-or-none in the language; a program
declaring none emits an intent with no `Decision`, so the dispatch gate has nothing to refuse on), then `Core/Strategy/Referee.cs` (`Verdict`, the promotion
write), `Db/PromotionStore.cs` (`Standing`), `FreshnessBounds` and `IntentDecision` as landed, `DailyReports.cs` section 4, the promotion tests. Branch
`u-promote-bounds`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-promote-bounds`, rebased onto `main` AFTER `U-freshness` lands. **Money
path** (what may execute): a test RED before the change and ONE mutant, quoted. No schema. No box, no ATAS, no money. One item; a light leg.

1. **No bounds, no promotion.** A verdict on a version whose FROZEN PROGRAM declares none of the three is not promotable, with a sentence naming the three
   declarations, and no promotion row is written. Nothing already promoted is invalidated by this unit (say so in `CONTRACTS.md`); section 4 names any standing
   promotion without bounds as one the dispatch gate cannot judge. RED: a bound-less version promotes. Mutant (the check applied when `Standing` is read
   instead of at the verdict): the promotion row exists — `Assert.Null() Failure`.
Not this unit: making the declarations required in the language; the allocator (`U-allocator-1`); any runner on the order path.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched classes 3×.
Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts, the RED and mutant quoted, what you did NOT do.
