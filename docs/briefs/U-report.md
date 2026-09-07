# U-report — the owner's daily report, written by the app from what it measured, with no inference and nothing invented
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rule 10, "The owner's report, every day, from the app", the round-4
lines on the owner's words), `docs/RESUME-HERE.md` step 6. Branch `u-report`, worktree `~/Projects/ai-trading-software-for-
mihael-worktrees/U-report`, rebased onto `main` first (`U-wakes`, and when landed `U-council-thin`'s and `U-data-binance`'s
tables). Every guard ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no money.

**Why.** Nothing named report, daily or standup exists (`grep` over `src/` finds prose only). The materials do: `PerformanceCard.Words`
(`PerformanceCard.cs:150`, a text seam), `GatewayStatus` (`GatewayTypes.cs:144-200`, absent-never-zero), `trade pnl`, the
meter's `Today` (`TurnMeter.cs:562`), `LossTodayAsync` (`TradingGateway.cs:614`), `mission_event` with its dispositions,
`ai_attempt` totals, the activity day separators (`DashboardView.cs:1618`). Rule 10: the app generates the daily factual
report itself; an incomplete net figure never reads as a complete profitability claim; a paid note only when something changed.

1. **The report, as data and as a file.** `DailyReport` (a record the app composes from one timestamped snapshot, every field
   nullable-and-labelled, never defaulted) with Astra's ten sections: identity (interval, timezone, snapshot time, app and
   schema versions); mission state (working / healthy idle / blocked / paused, the coded reason, the next eligible wake, per
   role when roles exist); trading readiness (connector, account, mode, session, data age, missing capabilities, activation
   blockers); capital and performance (exposure, realized, unrealized with its valuation time, known fees, missing costs
   NAMED, drawdown, remaining loss allowance); execution health (orders, fills, rejections, UNKNOWN operations by age,
   reconciliation, disconnects); AI spending (by role, runtime and model: spend, reservations, unresolved commitments,
   remaining caps, CLI advisory apart from API); other costs and what is missing; research evidence (datasets, accepted
   artifacts, app metrics apart from agent claims); decisions and changes (owner messages with dispositions, limits
   changed, cap events); recovery and next work (interrupted attempts, pending handoffs, blockers, the next review).
   Written at local midnight and on demand to
   `state/reports/<yyyy-MM-dd>.md` (LF, ≤ 120 lines, the app rejects its own over-long draft and says so), app-owned, and
   raised as a `report` event nobody consumes by inference. RED: a day with an unknown fee prints `net: —` and names the
   fee as missing; mutant (the missing cost dropped from the text) → a net printed as complete.
2. **A Report page in the rail** (words only, `Theme.cs` values only): today's report as sections, a picker for earlier
   days, "Write it now"; the header says when it was written and that nothing in it is inferred. The Operations note
   (≤ 20 lines, only when an app predicate changed) is `U-council-thin`'s to produce; shown here when a `publication` of
   kind `note` exists for the day, absent otherwise.
3. **Dispositions completed:** `delegated` (an owner message turned into a brief — links the publication id), `blocked`
   (the reason), `superseded` (a later message on the same subject) join `answered`/`failed` in `MissionEventStore`; the
   report lists every owner message of the day with its disposition and the doctrine's deadline (a setting, default 24 h;
   overdue is a line, never a paid turn). RED: a message with no turn in 24 h is absent; mutant (the deadline check inverted).
4. **`trade report --json`** (read-only, the `pnl` pattern: declared reply type, `JsonIgnoreCondition.Never` on nullables, the
   deadline table at `TimeSpan.Zero`) so the AI reads the facts the owner reads, never writes them; schema text, `CONTRACTS.md`, the guide.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
