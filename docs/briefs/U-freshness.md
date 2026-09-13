# U-freshness — a strategy declares its timeframe, its data freshness and its maximum decision age, and the runner asks again at the wire
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the four rules; a definite refusal versus an ambiguous failure), `docs/COUNCIL.md:96-97` (verbatim: "A
promoted strategy declares its timeframe, its required data freshness and its maximum decision age, and the runner checks them again when the
intent reaches execution"), `:14-15` (freshness among the code-enforced gates), `:33` ("never a late trade"), `:34-36` (a changed assumption
invalidates the evidence), `:74-76` (fixture versus market data in the Situation), then `Core/Strategy/StrategyProgram.cs:61`, the parser and
`StrategyCanonical.cs` (what is hashed), `docs/STRATEGY-LANGUAGE.md:16`, `Strategy/StrategyEvaluator.cs` (an intent stamped with its bar),
`Db/Database.cs:621-632` (`strategy_version`: no timeframe), the promotion row (`U-referee-2`), `Gateway/GatewayTypes.cs:17` (`MaxQuoteAge`),
`TradingGateway.cs:1244-1262` (the quote-age check and `ContractSizeOrThrow` inside the dispatch path), `:1571`, `DispatchPlaceAsync` (~`:2219`),
`Gateway/DailyReport.cs:348-352` (section 3), `MissionLoop.cs:585-600` (the Situation's data line). Branch `u-freshness`, worktree `~/Projects/
ai-trading-software-for-mihael-worktrees/U-freshness`, rebased onto `main` AFTER `U-referee-2` and `U-venue-catalog` land. **Money path:** every
guard ships with a test RED before it and ONE mutant, quoted. No box, no ATAS, no money, no order sent. **Schema 19.**

**Why.** A program declares an instrument and a timezone and nothing else; `strategy_version` has no timeframe; the only age the gateway knows
is a QUOTE's (`MaxQuoteAge` 30 s). Nothing knows how old the bar an intent came from is, so rule 1's freshness gate and `:33`'s "never a late
trade" have no implementation. A live or paper bar feed does not exist and COUNCIL names no unit that produces one: this gate is proved over
recorded bars and a controlled clock, and says so.

1. **Three declarations on the program,** parsed, canonicalised and hashed — `timeframe`, `data_freshness`, `max_decision_age` (durations) — so a
   changed bound is a different `StrategyId`; the three day-one programs gain them and their golden ids move once, recorded. RED: a program
   declaring them fails to parse. Mutant (left out of the canonical form): two different bounds share one id.
2. **They travel onto the version and the promotion row** (a changed assumption invalidates the evidence). RED: `no such column:
   max_decision_age`. Mutant (read from the request at promotion time instead of the frozen program): a promotion restates its own bound.
3. **An intent carries the bar it was computed from** (open time, close time) out of the evaluator and through `PlaceIntent`; nothing today
   names a time a gate could read. RED: an intent reaches the gateway with no bar time. Mutant (the bar's open time used as the decision time):
   a one-minute intent a minute younger than it is.
4. **The gate at DISPATCH, beside the quote check and `ContractSizeOrThrow`:** an intent older than its `max_decision_age`, or computed from a
   bar older than its `data_freshness`, is REFUSED with the policy's safe outcome and nothing is sent; a definite refusal, recorded, never an
   UNKNOWN. RED: an intent an hour old dispatches. Mutant (the check moved above the awaited reads): an intent that aged during the reads still
   sends — `Expected: refused / Actual: DISPATCHING`.
5. **The owner and the roles are told:** section 3 gains the freshest bar's age per dataset and whether the promoted strategy's bound is
   satisfiable now; the Situation's data line says the same and marks fixture versus market data. RED: the report silent about data age with a
   promoted strategy present. Mutant (age from `accepted_at` instead of the last bar): a year-old dataset collected today reads fresh.

Not this unit: a live or paper bar feed; `U-flatten`; the allocator; any venue connector; the referee's verdict (`U-referee-2`).
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched
classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha,
gate counts, one line per item with its RED and mutant, the moved golden ids, what you did NOT do.
