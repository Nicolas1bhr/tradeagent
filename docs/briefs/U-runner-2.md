# U-runner-2 — the evaluator: closed bars of a named dataset in, bounded intents out, a fault a defined outcome
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:126-166`, then `U-runner-1`'s product on `main` (`docs/STRATEGY-LANGUAGE.md`,
`Core/Strategy/`, `StrategyLimits`, `Program.WarmUpBars`), `Db/DatasetStore.cs:132-193` (`Checked` re-hashes every raw file on EVERY
read), `Data/DatasetReader.cs:20-61` (path-based, a 10 000-bar cap that REFUSES), `KlineNormaliser.cs:44-45,152-164` (a gap is never
filled), `GatewayPipeServer.cs:1975-1993` (`data-bars` resolves `Newest(pair)` only). Branch `u-runner-2`, worktree `~/Projects/ai-
trading-software-for-mihael-worktrees/U-runner-2`, rebased onto `main` AFTER `U-runner-1` lands. Every item ships with a test RED
before it and ONE mutant watched going red, quoted. No box, no ATAS, no money, no gateway, no orders. **No schema number.**

**Why.** No dataset can be read by id, and the one reader refuses a twelve-month window; no indicator and no evaluator exist. The
language's semantics — closed bars only, a gap reported and never filled, exits before entries, a signal at bar n executing only
after it, per-event limits, a fault meaning no new exposure — are prose. Per-bar quality flags and instrument increments do NOT
exist on a dataset (`DatasetStore.cs:31-54`, counts only): the evaluator takes what the dataset has and says so in `CONTRACTS.md`.

1. **Bars by dataset id.** `DatasetStore.ById(long)`; a `BarFeed` streaming "all closed bars of dataset X in [t0, t1], ascending"
   in chunks with no cap; the `Checked` verdict taken ONCE at the start of a run and a `REJECTED` dataset refused with the reason
   (`DatasetStore.cs:154`). RED: no read by id; a twelve-month window is refused `OverCap`. Mutant (`Checked` skipped): a rejected
   dataset feeds a run.
2. **Indicators** SMA, EMA, ATR, RSI, rolling high/low and the session opening-range accumulator, exactly as `docs/STRATEGY-
   LANGUAGE.md` specifies (initialisation, numeric semantics), computed from closed bars only, undefined during warm-up, each
   with a golden series pinned over a fixture of bars. RED: no indicator exists. Mutant (EMA seeded from the first close instead
   of the documented initialisation): the golden series changes.
3. **Gaps and warm-up.** A missing minute is not a bar: it advances no lookback and the run counts and reports it; evaluation
   before `WarmUpBars` is refused. RED: over a gap fixture the SMA differs from the documented value → equal. Mutant (the
   previous close carried across the gap): red.
4. **The rule engine per closed bar.** State = position (long/flat), entry price, bars since entry, the pending intent; exits
   evaluated before entries; no same-event reversal; no duplicate entry while one is pending; a signal from bar n yields an
   intent stamped with n and executable only after it — the evaluator EMITS intents and places nothing; time filters honoured
   with the versioned timezone and calendar (weekdays, entry windows, the opening-range interval, the scheduled session exit;
   DST and a missing session defined and tested). RED: two entries on one event. Mutant (entries before exits): a reversal
   in one event.
5. **Limits and faults.** A per-event operation budget and a state-size limit from `StrategyLimits`; an interpreter fault, a
   division by zero, an over-budget event or a timeout is a DEFINED outcome (`EvaluationOutcome.Faulted` with the reason, the
   run halts, no intent emitted), never an exception escaping the evaluator. RED: a program over the per-event budget throws.
   Mutant (operations counted per rule instead of per event): a program of many small rules passes. The three day-one
   programs evaluate end to end over a synthetic bar fixture with the expected intents pinned per program.
Not this unit: fills, fees, slippage, stops and targets between evaluations, capital, the trace, persistence, the report, the
pipe (`U-runner-3`); live or paper execution; the relay; promotion; the referee.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0
failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
