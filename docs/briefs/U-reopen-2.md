# U-reopen-2 — two strikes hold the scope for the owner, the durations become the owner's to narrow with two presses, and the record says which rule applied
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (two-press; operator authority in-process only, no verb, no pipe op), `docs/COUNCIL.md:59-63`, the `U-reopen-1` section of
`docs/CONTRACTS.md` and the receipt, eligibility and lookup as landed, then `src/TradeAgent.Gateway/TradingGateway.cs` (`Close`, the receipt write, `ApproveAsync`),
`GatewayTypes.cs` (`LossMinClosure`), `Core/Trading.cs:107-132` (`Widenings`, `Widens`: turning a budget off is the widest widening), `DashboardView.cs:700-770` (the
override card: the ONLY card with a required free-text note — copy its shape), `:1115-1123,1139,1162,1185` (`ConfirmIf`: a second press only in the risky direction),
`Ui.cs:225-280` (`Confirm`, `Arm`, `Disarm`), `SafetyPage :1241-1300,1425-1470`, `DailyReport.cs:429-457`, `Db/BoundaryStore.cs:71-78,160,422-437` (`Deploy`/`Hold`,
the 24 h window, `ApplyDue`), `tests/TradeAgent.FaultTests/LossDayClosureTests.cs`, `tests/TradeAgent.UnitTests/TwoPressGrantTests.cs`. Branch `u-reopen-2`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-reopen-2`, rebased onto `main` AFTER `U-reopen-1` lands. **Money path** (a release lets orders flow): every
guard ships with a test RED before it and ONE mutant, quoted. No schema. No box, no ATAS, no money.

**Why.** `U-reopen-1` reopens every closure once it is earned. A scope that breaches twice in a week is a strategy or a market the owner has to look at, and reopening
it by code every day bleeds one budget per day. The durations are engineering defaults; the owner may narrow them, and narrowing is the risky direction.

1. **Two strikes hold the scope for the owner.** At CONFIRMATION (`Close`, under the gate) count the scope's distinct prior breach records inside `LossStrikeWindow`
   (**7 UTC dates**: today and the six before, evaluated at confirmation, never at reopening); a second one writes `loss_hold:{account}[:{symbol}]:{utcDay}`
   write-once (`AddKvOnce`) naming both episodes. A held scope has no `EligibleAt`; the watcher writes no receipt; `status` says `held for review`. Each scope
   counts once per incident — a daily and a symbol closure on one day are one incident each for their own scope; a restart or a duplicate pull adds none.
   RED: a second breach in four days → the receipt is written 24 h later. Mutant (the window measured at reopening): a strike ages out while the scope is closed.
2. **Release is the owner's, two-press, with a note, and still earns the reopen.** A "Reopen after review" card on the Safety page — the override card's shape:
   required note, second press with the full sentence — writes `loss_release:{account}[:{symbol}]:{utcDay}` once, with the note and the episodes it acknowledges;
   the release lifts the HOLD only: the receipt still needs `U-reopen-1`'s eligibility, flatness and clock. In-process only: no verb, no pipe op, no setting.
   RED: released → an order dispatches with a leg still flagged. Mutant (the release writes the receipt): the same. Second mutant (one press): the hold lifts.
3. **The durations are settings, narrowing is two-press, and every record snapshots what applied.** `LossMinClosure` (24 h) and `LossStrikeWindow` (7 days) move
   into the risk settings; `Widenings` names a narrowed closure or a shortened window so `BuildSaveLimits` arms the second press; each breach record and each
   receipt carries the values that applied to IT, and eligibility is computed from the record's snapshot, never from the live setting — a setting narrowed
   after a breach cannot advance that breach's reopen, a setting widened cannot delay a receipt already written. RED: the closure narrowed to 1 h after the
   breach → reopens at 1 h. Mutant (the snapshot dropped from the receipt): the report cannot say which rule reopened the day.
4. **The directors' hold is bounded, or it is nothing.** The loss boundary's `Hold` stays informational unless a sealed assessment asks for more time: at most ONE
   extension of at most `LossMinClosure` per episode, applied by code from the boundary row, never past a release, and stated on every surface with its end. RED:
   a `Hold` disposition with no end → the scope never reopens. Mutant (the extension re-applied on every tick): the same.
5. **Told:** section 4 lists the scope's closures of the last window with rule, instant and outcome (reopened / held / released by whom, note quoted); the Situation
   and `status` (`loss_held_for_review`, `loss_released_at`, the snapshot); `USER-GUIDE.md`; `CONTRACTS.md` (the defaults as the owner's, the snapshot rule, the
   bounded extension). RED: a held scope reads as "earliest …". Mutant (the note optional): a release with an empty note is written.
Not this unit: an allocation ladder (aggregate enforcement and attribution do not exist yet — say so in `CONTRACTS.md`); the data-loss exit (`U-flatten-3`).
Gate and report as `U-flatten-1`.
