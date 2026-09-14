# U-flatten-2 — the reduction-only exception: a confirmed breach closes what is open, by code, under the app's own press kind, resolved by machine
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (rule 3; two-press; rule 4), `docs/COUNCIL.md:14-15,33`, `docs/CONTRACTS.md` (the `U-flatten-1` section), then
`src/TradeAgent.Gateway/TradingGateway.cs:3817-4037` (`OperatorCloseAllAsync`: `RiskReducingScope.Begin` first, `RefuseWhileAPressIsOpen`, `BeginComposite`,
per symbol `SettleAnUnresolvedReducerOrRefuse` `:3704-3778`, the drift re-read `:3917-3935`, `OpenPressRow` `:3486-3540` written FLAGGED with its rationale
`:3419-3449`, `Connector.ClosePositionAsync` `:3964`, outcomes `:3986-4019`), `:3225-3238` (`IsPressRecord`/`PressKindOf`: exactly two kinds), `:3296-3314`
("a press ends when a person has read them"), `:1234-1247` (a flagged row pauses all order flow), `:1526-1536` (`CanIncreaseExposure`), the cancel press
(`CancelPress`), `:2936-2951` (`CloseAsync` and the gates it inherits — NOT the route), `tests/TradeAgent.FaultTests/EmergencyPressTests.cs`,
`UnknownCloseTests.cs:75-85` (`PressBudget` 20 s for a verdict that is the book), `PressAtomicityTests.cs`. Branch `u-flatten-2`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-flatten-2`, rebased onto `main` AFTER `U-flatten-1` lands. **Money path:** every guard ships with a
test RED before it and ONE mutant, quoted, over `RecordingConnector`. No schema. No box, no ATAS, no money; the simulator only.

**Why.** `U-flatten-1` closes the day and sends nothing. The only flatten is the owner's press, and a press is a person's: its rows stay flagged until
that person reads them, which pauses every order until a human looks — a human in the loop after every breach. The press mechanics are right; what is
new is an app-owned kind, a code-enforced reduction-only exception to two-press (named in `CONTRACTS.md`), and resolution by MACHINE: a leg is
resolved when its close is terminal AND the position reads back flat; anything else stays flagged and pauses, exactly as today.

1. **Openers first.** On a confirmed daily breach every working order on the account that could increase exposure is cancelled through the cancel
   press mechanics under the app's kind (`op-budget-cancel-`) before any close; a per-symbol breach cancels that symbol's. RED: a resting opener fills
   after the flatten and the book is not flat. Mutant (the cancel skipped): the same.
2. **The reduction-only exception, enforced in code.** `op-budget-close-` legs reuse `OperatorCloseAllAsync`'s sequence through ONE shared method —
   `RiskReducingScope`, `BeginComposite`, `SettleAnUnresolvedReducerOrRefuse`, the drift re-read, the flagged write-ahead row, `ClosePositionAsync` —
   plus a check before the wire that the leg opposes the position and is at most its size; a leg that would reverse or open throws before sending. A
   daily breach closes every position, a per-symbol breach that symbol, daily outranking; the activity line names the budget and the record, never
   "you pressed"; `LOSS_BUDGET_REACHED` stays the caller's answer. RED: two open positions, a confirmed breach → the book flat. Mutant (routed through
   `CloseAsync`): `TRADING_PAUSED_UNRECONCILED` after the first leg. Second mutant (size check removed, position drifted smaller): a long 2 reads short.
3. **Resolved by machine, or paused.** A leg whose close is terminal and whose position reads back flat is resolved by the app — flag cleared, the
   outcome appended once to the breach record (request ids, fills, residual); a rejected, cancelled, timed-out or unsettled leg stays flagged, pauses
   order flow as today, and the report says which. The owner's button is never refused because of an app press (a third kind in `IsPressRecord`/
   `PressKindOf`); an app leg unresolved on a symbol refuses the owner's leg there in words — the existing rule. RED: after an all-filled app flatten
   the next day's first order is refused `TRADING_PAUSED_UNRECONCILED`. Mutant (resolved on "terminal" alone, no read-back): a cancelled close resolves
   with the position still open.
4. **Crash recovery.** A breach record with no outcome at startup re-runs the flatten, after reconciliation and never over an unreconciled row (the
   startup sweep's rule). RED: killed between the record and the composite, restarted → the position stays open and nothing says so. Mutant (recovery
   keyed on the composite's existence instead of the outcome): a composite begun and killed is never finished.
5. **Told.** Section 4, the Situation, `status` (`loss_flatten`: `flat` / `unresolved` / `review-pending` / `eligible`), the Safety rows; every sentence
   promising "nothing is closed for you" (`GatewaySchema.cs:90-97`, `WorkspaceBuilder.cs:331-341`, `USER-GUIDE.md:747-754`, `LossBudgetOrThrow`'s doc,
   `CONTRACTS.md`) rewritten. RED: the report after a flatten reads as before. Mutant (`flat` from the composite's ok, not the read-back): an
   unresolved leg reads flat.
Not this unit: the data-loss exit (`U-flatten-3`); strategy stops (`U-protect`); the directors' assessments; the box.
Gate and report as `U-flatten-1`, plus: a press fixture whose verdict is the book takes `PressBudget`, never the simulator's 2 s (the windows-red family).
