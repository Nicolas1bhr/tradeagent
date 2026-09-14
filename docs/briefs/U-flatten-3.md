# U-flatten-3 — when the book cannot be valued: the bounded refresh, the data-loss exit, and what the thresholds do not promise
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (rule 3: ambiguous is never a refusal), `docs/COUNCIL.md:14-15,33`, `docs/CONTRACTS.md` (the `U-flatten-1` and
`-2` sections), then `src/TradeAgent.Gateway/TradingGateway.cs:506` (`OnConnectionChanged`), `:4620-4630` (health from quote age), `LossBudget.cs`
(`CannotBeRead`), the `U-flatten-1` watcher and the `U-flatten-2` exception as landed, `HealthRegistry`, `DailyReports.cs` section 4. Branch
`u-flatten-3`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-flatten-3`, rebased onto `main` AFTER `U-flatten-2` lands. **Money path:**
every guard ships with a test RED before it and ONE mutant, quoted, over `RecordingConnector`. No schema. No box, no ATAS, no money.

**Why.** `U-flatten-1` records nothing on a suspect print and `-2` never flattens from one — right, and it leaves the other failure: a feed that goes
silent, a platform that stops valuing, a disconnect that outlives the day. Silent indefinite exposure is a failure mode too. Astra's line (2026-09-14,
final answers only), adopted: a persistent data loss needs its own pre-authorised response with a reason distinct from a breach, and no claim of
unattended protection is made without it.

1. **Unavailable is a state with a clock.** When the watcher cannot value an open position (no executable mark within `MaxQuoteAge`, no instrument,
   a disconnect) it records `valuation_unavailable_since` per symbol in `kv`, denies new risk (today's rule), cancels risk-increasing working orders on
   that symbol through `-2`'s cancel mechanics, and refreshes on every tick. RED: a silent feed with an open position, ten ticks → nothing recorded, an
   opener still resting. Mutant (the clock reset by a stale cached quote): unavailable never ages.
2. **The data-loss exit, bounded and distinct.** After `ValuationLossExitAfter` (a `GatewayOptions` value the owner sets; widening it is two-press) of
   continuous unavailability with the connection UP, the position is closed through `-2`'s exception under its own reason `VALUATION_LOST`, its own
   record and its own boundary, never `LOSS_BUDGET_REACHED`; with the connection DOWN nothing can be sent: the pause holds and the report says so on
   every day it lasts. RED: a valued-then-silent book stays open past the bound. Mutant (the exit's reason folded into the breach record): the report
   claims a budget breach that never happened.
3. **Disclosure.** Section 4 and `CONTRACTS.md` state the thresholds (tick, confirmation window, quote age, exit bound), that they trigger an
   intervention and cannot bound the realised loss (gap, spread, slippage, missing fees), and the sampling delay measured on this machine by the
   builder (quoted). RED: the report names the budget as a maximum loss. Mutant (the disclosure line printed only when a budget is set to zero): a
   live budget reads as a guarantee.
Not this unit: strategy stops and targets (`U-protect`, needs a runner on the order path and a schema rung); the box; a real venue's feed.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed; touched classes
3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20 lines): tip sha, gate counts,
one line per item with its RED and mutant, the measured sampling delay, what you did NOT do.
