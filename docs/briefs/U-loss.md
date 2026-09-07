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

## Report

Code tip `54f99e4` (ten commits, one per item, no trailers); this report is the docs-only commit above it. **No rebase was owed** — `git rev-list --count u-loss..main` → `0`, `main` had not moved. The killed leg's three `gate-*.log` files were read (unit and fault complete, integration truncated at 2 lines), then deleted and not committed. Every figure below is a run in this session; nothing is carried over from that leg.
**Gate at `54f99e4`, Release.** `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 Warning(s), 0 Error(s)**; the same command at `-v:n` ran **41 `CoreCompile:` targets**, so the 1.9 s is this Mac and not a skipped build. Suites one project at a time, to files: **Unit 441 + Fault 269 + Integration 615 = 1325 passed, 1 skipped, 0 failed**, all three exit 0. Touched classes (`RiskGateTests`, `ApprovalReauthorizationTests`, `RestartTests`, `PolicyGateTests`, `TwoPressGrantTests`, `LiveModeNeedsADailyBudgetTests`, `LossSituationTests`, `LossStatusFieldsTests`, `LossBudgetCompositionTests`) **3× → 70/70 and 34/34 every time**. `U-seen-1`'s Integration suite ran alongside this one's; nothing went red, so no `Timing` re-run was owed and no assertion was touched. Test names vs `main`: **0 removed, 24 added**. Secret scan of the whole diff vs `main`: clean (every hit was `CancellationToken`).
1. **Both budgets on `RiskPolicy`, decimals, 0 = not enforced.** `Widenings` goes through one `Widens()` for the three zero-means-off caps; `Unreadable()` leaves both at 0. Persistence **verified by running** a throwaway probe (written, run, deleted, not committed): the blob read `…"max_loss_per_trade":123.5,"max_daily_loss":777.25…` and a second gateway constructed on the same db read back `123.5` / `777.25`.
2. **The day's budget refuses new risk.** RED with the guard reverted (`await LossBudgetOrThrow(...)` removed from both call sites): `Failed … A_day_past_its_loss_budget_refuses_a_new_position_and_still_lets_one_be_closed` → `Assert.Throws() Failure: No exception was thrown`. Mutant — `Loss = day < 0m ? -day : 0m` → `day > 0m ? day : 0m`, a loss read as a profit: same test, `Assert.Throws() Failure: No exception was thrown`. Restored, green.
3. **The per-position budget refuses an add and never a reduce.** RED with the `TradeReached` block deleted: `Failed … Adding_to_a_position_past_its_own_loss_budget_is_refused_and_reducing_it_is_not` → `Assert.Throws() Failure: No exception was thrown`. Mutant — `CanIncreaseExposure` reduced to `return Math.Abs(signed) > Math.Abs(held);`, so an add in the held direction stops counting as new risk: same test, `Assert.Throws() Failure: No exception was thrown`.
4. **Fail closed, never on a guess.** RED with `CannotBeRead` returning `Unknown = null` (an unknown read as a flat day): `Failed: 3, Passed: 0` — `An_open_position_with_no_price_and_no_mark_refuses_rather_than_counting_as_flat`, `An_open_position_with_a_price_but_no_contract_size_refuses_too` and `A_symbol_traded_today_with_no_contract_size_refuses_instead_of_valuing_the_day_wrong`, each `Assert.Throws() Failure: No exception was thrown`. Mutant — a position with no quote marked at its own average (`?? p.AveragePrice`): the first of those, `Assert.Throws() Failure: No exception was thrown`.
5. **A real-money mode cannot be selected at budget 0.** RED with the `SetMode` guard removed: `Failed … LiveModeNeedsADailyBudgetTests.A_real_money_mode_cannot_be_chosen_while_the_day_has_no_loss_budget` → `Assert.Throws() Failure: No exception was thrown`. Mutant — `MaxDailyLoss <= 0m` → `< 0m`, so zero stops counting as unset: same test, `Assert.Throws() Failure: No exception was thrown`.
6. **Surfaces.** Two `Ui.NumberField` rows in SAFETY LIMITS with a hint that names the account's currency only once the platform has said it, the Situation line beside the cost line, `loss_today` / `loss_budget_day` / `loss_budget_trade` absent-when-unknown, `GatewaySchema`, `CONTRACTS.md`, `USER-GUIDE.md`, `AGENTS.md`, and one activity line per budget per day. All asserted by the tests in item 7 and green; **the two rows have NOT been seen rendering** on any screen.
7. **Tests.** 24 added, 0 removed. `FakeBroker`/`LoopbackAtasAdapter` now report `UnrealizedPnl` as `null` rather than a stale `0` — without that the per-position budget would have read every losing paper position as flat. The three fixture edits (`ApprovalReauthorizationTests`, `RestartTests`, `PolicyGateTests`) only ADD a wide `MaxDailyLoss` so a real-money mode stays selectable; one assertion was added, none loosened or removed.
**NOT done:** no Windows box, no ATAS, no real money, no UI run and no screenshot — the Safety rows and the currency hint are verified by tests reading `DashboardView.cs`, not by eyes; nothing is flattened on a breach (`U-flatten`); the activity-line-once-per-day state is in memory, so a restart writes it again, deliberately; no push, no merge, no `BUILD-STATUS.md` section, no other worktree entered.
