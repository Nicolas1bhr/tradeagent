# U2c1a — dispatch recovery: rebase over U2a, then derive the reconciliation rule correctly

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/hardening/records/U2c1.md` down to the end of
"What the branch does", and `docs/CONTRACTS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`; full suite 8–12 min, to a file. No box.

Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u2c1-build`, branch `u2c1-dispatch-recovery` @ `1e10660`
(28 commits on the OLD U2b tip `cb2ce2f`, not on `main`). **Item 0:** `git rebase --onto main cb2ce2f
u2c1-dispatch-recovery` — 21 commits survive (the 7 old U2b commits drop out; `main` has their rewritten form). Expect
a conflict in `src/TradeAgent.Gateway/GatewayTypes.cs`: U2a sealed `AgentContext` (`IsOperator` private init, no `with`),
minted `op-{nonce}-{intent}-{index}` ids with the 61-character budget; U2c-1 added record and press types. Keep both
sides; never drop a U2a guard to compile. Then Release build `--no-incremental` (0 warnings) + full suite in Release as
the baseline; a red test after the rebase is a real interaction, fix it as part of item 0.

The rule (in CONTRACTS.md, keep it): *a request leaves the unconfirmed set only on positive, definite, stable evidence
about its own target; anything else is inconclusive and keeps trading paused.* Each item: a red-first test that FAILS
today, then the fix, both directions (the wrong evidence refused AND the right evidence still settles — a definite
CANCELLED from the right connector still clears; a correctly applied modify still reads applied).

1. Reconciliation uses only the connector whose id the record carries: a record placed on A while B is connected is
   inconclusive, reason "placed on A; connected to B" — an empty book on B settles nothing.
2. A non-definite target state is never "clear", with or without a captured set.
3. `Adopt` never treats the broker's UNKNOWN as resolved.
4. "Held still" is not a verdict: a cancel/modify whose target is unchanged after grace stays inconclusive until a
   definite state — target terminal, broker refusal, or the owner's card.
5. Latch on the definite settle path too: any persist failure after the wire → latch (round 2 covered only the
   indefinite path).
6. Modify verdict: returned order id == target id (and symbol, account); price ∈ {round-down, round-up} of the request
   on the tick grid AND ≠ the pre-modify price when the request differs; quantity only once the SDK contract says what
   `OrderInfo.Quantity` means (write that sentence in `src/TradeAgent.ConnectorSdk/Contracts.cs`), else inconclusive.

Yours: `src/TradeAgent.Gateway/**` except the pipe server and `AgentContext`, `Core/Db/Stores.cs`, `Errors.cs`, the
ConnectorSdk sentence, `docs/CONTRACTS.md`, tests. Not yours: the updater, `CoidWitness*`, `DashboardView.cs`, the
emergency-press rewrite (U2c1b), the cancelled-handler settle (U2c1c). Every fix: RED quoted, GREEN, one mutant watched
red (commit before mutating; `cp` restore; `touch`). Test-name diff vs baseline: nothing removed. Commit per item, no
trailers, no push, no other worktree. Gate: Release `--no-incremental` → 0 warnings; full suite in Release → 0 failed.

## Report — append as you go, commit with each item, ≤20 lines: tip sha; rebase conflicts and how each was resolved;
baseline counts; one line per item (RED → GREEN → mutant); final counts; what you did NOT do. Verified or NOT VERIFIED.

Rebase: 21 commits replayed onto `main` 40816da. Two conflicts. `docs/CONTRACTS.md` — both sides additive prose, kept
both (U2c-1's return-state mapping paragraph, then main's transport-ledger paragraph). `GatewayTypes.cs` — U2a's sealed
`AgentContext` class vs U2c-1's `record AgentContext` + new `OperatorPress`; kept U2a's class and its doc verbatim, put
`OperatorPress` above it. No U2a guard dropped. Two post-rebase reds, both real interactions with U2b's disposal
sentinel and both fixed as item 0 (their own comments route the gateway half to U2c-1):
`A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error` — cancellable latency no longer leaves a
DISPATCHING row now that a catch-all follows the wire, so the fixture is uncancellable latency and every assertion
stands unchanged; `A_row_left_dispatching_is_named_even_when_the_agent_disconnected_first` — the escaping
`TimeoutException` now settles UNKNOWN+flagged (asserted), and the row disposal must name is a write-ahead put in the
store directly; re-mutated `if (unfinished > 0 || unsettled.Count > 0)` back to the old `handlers.Length > 0 && (...)`
guard → RED (`Expected: "error" / Actual: null`), restored. Added
`A_cancelled_dispatch_is_flagged_rather_than_left_dispatching` for the U2c-1 half. `RecoveryConnector` gained the two
interface members `main` added (`WorstCaseOperationPath`, `EmergencyBudget`, delegated to the inner connector).
Item 0 baseline VERIFIED: Release `--no-incremental` 0 Warning(s) 0 Error(s); suite Unit 201 / Fault 166 / Integration
506, 0 failed.
1. Wrong-connector evidence. RED `A_record_from_another_platform_is_inconclusive_however_empty_this_book_is`
   (`Expected: Not CANCELLED / Actual: CANCELLED`) → GREEN with a `req.ConnectorId != Connector.Id` guard first in
   `ReconcileAsync`'s loop, reason "placed on fake; connected to atas"; the other direction
   (`A_definite_cancel_on_the_records_own_platform_still_settles`) was green before and after. Mutant `if (false && ...)`
   → RED, same assertion. Fault suite 168 green. VERIFIED.
2. Non-definite is never clear. RED `A_captured_order_the_platform_will_not_commit_to_keeps_the_sweep_unconfirmed`
   (CANCEL_PENDING, UNKNOWN) and `A_sweep_with_nothing_captured_is_unconfirmed_while_the_book_is_undecided`, all three
   `Expected: Not CANCELLED / Actual: CANCELLED` → GREEN: `IsDefinite` stopped being a filter on the live set and became
   an `undecided` set asked first, captured or not; `A_definitely_cancelled_book_still_settles_the_sweep` green
   throughout. Mutant `undecided.Count > 99` → the same three RED. Fault suite 172 green. VERIFIED.
3. `Adopt` and the broker's UNKNOWN. RED `The_brokers_own_uncertainty_is_never_adopted_as_an_outcome(state: UNKNOWN)`
   (`the flag every gate reads was cleared on the strength of the broker not knowing`; the CANCEL_PENDING case was
   already green — `RECONCILING -> CANCEL_PENDING` is not a legal transition) → GREEN with `if (!IsDefinite(match.State))
   return false;` as the first line of `Adopt`, and the inconclusive detail split so a non-definite state reads "settles
   nothing" rather than "does not fit our record"; the other direction (`A_state_the_broker_asserts_is_still_adopted`,
   WORKING/FILLED/CANCELLED) green throughout. Mutant `if (false && !IsDefinite(...))` → the same one RED. Fault suite
   177 green. VERIFIED.
4. "Held still" is not a verdict. RED `A_working_target_never_condemns_the_cancel_however_still_it_holds` and
   `Captured_orders_that_are_merely_still_working_do_not_condemn_the_press` (both `Assert.False((await
   gw.ReconcileAsync()).Clean)` — `Expected: False / Actual: True`; the pass called itself clean on stillness) → GREEN by
   deleting `_settleWatch`/`HeldStill`/`SignatureOf` and returning inconclusive for a working target and for working
   captured orders; a terminal target, absence past the grace and a definite CANCELLED still settle. Other directions,
   green: `A_target_the_platform_takes_to_a_terminal_state_still_settles_the_cancel` (FILLED → REJECTED, clean) and
   `The_owners_card_still_settles_a_cancel_the_platform_will_not_judge` (ForceResolve + the refresh its own screen does).
   Two round-2 tests asserted the deleted verdict and were rewritten in place, names kept:
   `A_cancel_whose_target_is_still_working_is_never_reconciled_as_cancelled` and
   `A_cancel_all_press_is_reconciled_by_what_is_left_on_the_book`; R1d was renamed from
   `A_working_target_must_hold_still_before_the_cancel_is_called_failed` (its name asserted the verdict this item
   removes) — no test dropped, and neither name exists on `main`. CONTRACTS.md bullet rewritten to match. Mutant
   `IsTerminal(match.State)` → `IsLive(match.State)` → 6 RED including both new ones. Unit 201 / Fault 180 /
   Integration 506, 0 failed. VERIFIED.
5. Latch on the definite settle path. RED `A_place_the_broker_took_pauses_trading_when_its_outcome_cannot_be_written`
   and `A_cancel_the_platform_carried_out_pauses_trading_when_it_cannot_be_written` (both
   `Assert.True(gw.HasUnconfirmedWork())` → `Expected: True / Actual: False`; the store is put in `PRAGMA query_only`
   from the connector's own OnPlaced/OnCancelled hook, so the wire is touched and only the write fails) → GREEN with a
   persist catch in `Settle` that latches, files `settle_failed` off-thread and throws
   `STATE_DATABASE_CORRUPT`, plus `Settle` moved BEFORE the activity line in `CancelAsync` and `ModifyAsync` (the log
   write went first, so the store refused there and the settle was never reached); `RecordIndefinite`'s retry loop and
   the new one are now one helper. Control `A_definite_outcome_the_store_took_settles_and_latches_nothing` green
   throughout. Mutant `LatchUnconfirmed(...)` → `ClearLatch(requestId)` → both RED again. Unit 201 / Fault 183 /
   Integration 506, 0 failed. VERIFIED.
