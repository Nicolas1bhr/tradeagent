# U-loss — per-trade and daily loss budgets, enforced by the gateway from the ledger
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/RESUME-HERE.md` step 6. Branch `u-loss`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-loss`, rebased onto `main` first. Money path: every guard ships
with a test that was RED before it and ONE mutant watched going red, quoted in the report. No box, no ATAS, no money.

**Why.** `PlaceAsync` (`TradingGateway.cs:1258`) never reads the fill ledger: `RiskPolicy` (`Trading.cs:23`) caps one
order and one minute, and nothing caps a losing day. No human in the loop means the app bounds the loss, ahead of any
broker or prop firm. This unit REFUSES new risk; closing positions on a breach is the next unit (`U-flatten`).

1. `RiskPolicy` gains `MaxLossPerTrade` and `MaxDailyLoss`: decimals in the ACCOUNT'S currency, default 0 = not enforced,
   persisted in the settings blob, treated by `Widenings`, `Unreadable()` and the Safety page's `BuildSaveLimits` exactly
   as `MaxNotionalPerOrder` is (0 is the widest value; raising asks twice, lowering saves at once).
2. The daily budget bites at the order. Before any order that can INCREASE exposure (a close or reduce is never refused —
   the position cap's own test), the gateway computes today's loss = realized today from `_fills.Since(StartOfDay)` by the
   average-cost book `Pnl` already keeps + unrealized on open positions (`PositionInfo.UnrealizedPnl`, else the last quote
   and the instrument multiplier). Loss ≥ `MaxDailyLoss` → `GatewayDeniedException` with a new `ErrorCode.LOSS_BUDGET_REACHED`
   naming both numbers and the currency. "Today" is `TradingGateway.StartOfDay` (UTC), the day `trade pnl` uses.
3. The per-trade budget bites the same way: an order that ADDS to a position whose unrealized loss is already ≥
   `MaxLossPerTrade` is refused with the same code; one that reduces it is allowed.
4. Fail closed, never on a guess: a symbol traded today whose multiplier is unknown, or an open position whose unrealized
   loss cannot be read and has no quote, refuses with `RISK_CHECK_UNAVAILABLE` (`ContractSizeOrThrow`'s pattern), places
   nothing and writes no request row. A fill whose fee is unknown counts at its gross; the surfaces say how many.
   A budget of 0 reads nothing from the ledger and refuses nothing (the no-cap test at `RiskGateTests.cs:217`).
5. A real-money mode (`LIVE_CONFIRM`, `LIVE_AUTONOMOUS`) cannot be selected while `MaxDailyLoss` is 0; the refusal names the field.
6. Surfaces, words only, `Theme.cs` values only: two `Ui.NumberField` rows in the Safety page's SAFETY LIMITS block ("Most
   it may lose on one position", "Most it may lose in one day", the currency named when the platform is connected, hint
   "0 means not enforced"); a Situation line beside the cost line (`MissionLoop.cs:201`): lost X of Y today, Z per position,
   "no new positions until tomorrow" once reached, or "could not be computed — <why>; new positions are refused until it
   can"; `GatewayStatus` fields `loss_today`, `loss_budget_day`, `loss_budget_trade`, ABSENT when unknown (the `ai_cost_today`
   convention, `GatewayTypes.cs:161`), described in `GatewaySchema.cs` and `docs/CONTRACTS.md`; two sentences in
   `USER-GUIDE.md`'s Safety section; one activity line the first time each budget refuses in a day.
7. Tests, RED first, then the mutant: FaultTests — two simulator fills put the day past the budget, the next opening
   order is refused and a close goes through (mutant: the check dropped); adding to a loser past the per-trade budget
   refused, reducing allowed; unknown multiplier → `RISK_CHECK_UNAVAILABLE`, 0 places, no row (copy `RiskGateTests.cs:181`);
   UnitTests — raising a budget takes two presses (copy `TwoPressGrantTests.cs:180`); the Situation line and the absent
   status fields (`MissionCostSurfacesTests.cs` pattern); a real-money mode refused at budget 0.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×. Commit per item, one sentence each, no trailers. Append `## Report` (≤20 lines): tip sha,
the gate counts pasted, one line per item with its RED and mutant output, and what you did NOT do.
