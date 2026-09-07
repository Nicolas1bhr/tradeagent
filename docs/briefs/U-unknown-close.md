# U-unknown-close — an UNKNOWN close on an instrument can no longer be doubled by a press or by the agent
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/RESUME-HERE.md` step 6 (e), `docs/CONTRACTS.md:700-704`, `BUILD-STATUS.md`
§ "U-press-inflight landed" (its last bullet). Branch `u-unknown-close`, worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/U-unknown-close`, rebased onto `main` first. Money path: every guard
ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money.

**Why.** `Stores.cs:131-135`'s `$wire` clause blocks a press leg only while another close on the instrument is
`DISPATCHING`; `UNKNOWN` was left out deliberately (`Stores.cs:94-103`) because refusing the WHOLE press on it re-imposed
the pause the button exists to bypass. So an agent close the broker ACCEPTED with the acknowledgement lost
(`FakeBroker.FaultProfile.DropAfterBrokerAccept`, `FakeConnector.cs:352`) sits `UNKNOWN` in the book, Close All sends its
own leg (`OperatorCloseAllAsync`, `TradingGateway.cs:3377`, sized by the platform), both fill, long 2 becomes short 2; the
agent's own second `close` doubles the same way (`RefuseAStaleCloseOrThrow`, `:1982`, passes while the first still works).
Fixed here before any live order, per LEG, never per press.

1. **The press settles before it sends.** For each leg whose instrument carries an `UNKNOWN` request of intent `Close`
   (or any UNKNOWN order that would reduce it — same side as the leg), inside the press's own deadline: read that order
   by client id (`GetOrderAsync`, or `GetExecutionsAsync(since)` where the connector has no id) — FILLED or CANCELLED →
   settle the row the way `ReconcileAsync` does and proceed on the re-read position; WORKING → cancel it and proceed only
   on a DEFINITE cancel; no answer, or the deadline reached → that leg is REFUSED with `CLOSE_UNRESOLVED_ON_INSTRUMENT`,
   the press's row is still written, every other leg goes out, the card and the activity line name the instrument and
   say the position may still be open. Every extra call is charged to the press's deadline exactly as its wire calls are
   (`OperatorPressIsAnEmergencyTests`, `Timing`): nothing goes out late. `RefuseWhileAPressIsOpen` is unchanged.
2. **The agent's `close` is refused while an UNKNOWN close is on the instrument** (`CloseAsync`, before `PlaceAsync`):
   `CLOSE_UNRESOLVED`, naming the request id and `trade reconcile` as the route; a reduce that is not a close obeys the
   same rule. `Confirming_one_outcome_does_not_lift_another_requests_pause` stays green: the press writes its row.
3. **Tests, RED first, then the mutant** (FaultTests, `DispatchRecoveryTests`/`PressInFlightTests` harnesses): (a) an agent
   close ACCEPTED-then-lost, then Close All, then both would fill → the book ends FLAT and exactly one close reached the
   broker, or the leg is refused and the book still shows long 2 (assert whichever the connector's answer allows,
   both branches); mutant: the UNKNOWN check dropped → short 2. (b) the unanswerable case (the read hangs past the
   deadline) → the leg refused inside the deadline, the other instrument closed. (c) the agent's second close refused;
   mutant: the guard dropped → two closes in the book. (d) the existing P6 tests unchanged and green.
4. **Words:** `docs/CONTRACTS.md` § the press replaces "what that leaves open" with the rule above; `Stores.cs:94-103`'s
   comment says why UNKNOWN is now handled per leg; `GatewaySchema.cs` and `Errors.cs` carry both codes and the owner's
   sentence; one sentence in `USER-GUIDE.md`'s emergency section. `Theme.cs` values only for anything on the card.

Not this unit: a venue reduce-only flag (none in `PlaceOrderCommand`); `U-flatten`; the reconciler settling a press leg.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, the gate counts pasted, one line per item with its RED and mutant, what you did NOT do.


## Report
Code tip `62d00a6`, 3 commits, and the gate below was run there; the commit carrying this report adds nothing but these lines, so it is the branch tip and `62d00a6` is what was measured. Rebased onto `main` 3× (`ba4a85b` → `44ee58d` → `5aed4a4` → `6e46312`), no conflict at any; `git rev-list --count HEAD..main` → 0 and `git diff main` deletes only the four lines this unit replaces, so nothing of U-model, U-crlf-win or U-data-binance was removed.
Gate at the tip, Release: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)`, `0 Error(s)`; suites → `Passed: 481` + `Passed: 277` + `Passed: 615` = 1373, `Failed: 0`, 1 skip (the pre-existing `PipeContractTests` one). Touched classes 3× → Fault `Failed: 0, Passed: 37` three times, Integration `Failed: 0, Passed: 19` three times; no `Timing` red, so no re-run needed. Names vs `main` → 0 removed, 8 added (1109 → 1117).
1. **The press settles before it sends.** RED: `orders at the broker : FB-1 Buy 2 FILLED | FB-3 Sell 2 FILLED | FB-4 Sell 2 FILLED`, `position at the end : ES -2`, `Assert.DoesNotContain() Failure: Filter matched in collection … Quantity = -2`.
   Mutant `foreach (var req in UnresolvedReducersOn(symbol, side))` → `foreach (var req in new List<ExecutionRequest>())`: 3 RED, `position at the end : ES -2` again.
2. **The agent's close and its reduce are refused** (`CLOSE_UNRESOLVED`). RED: `Assert.Throws() Failure: No exception was thrown` / `Expected: typeof(TradeAgent.Gateway.GatewayDeniedException)`, on all three tests.
   Mutant `intent.Side == side` → `intent.Side != side` in `CouldMoveThePositionLike`: the same three RED.
3. **Tests**: 8 in `UnknownCloseTests.cs`, both directions each; none in `Timing` — 2 × 1200 ms inside a 2000 ms budget cannot be made to fit by any runner. P6, `EmergencyPressTests` and `Confirming_one_outcome_…_pause`: untouched, green.
4. **Words**: `CONTRACTS.md` (the "what that leaves open" paragraph replaced by the rule), `Stores.cs`, `Errors.cs` and `GatewaySchema.cs` (both codes), `USER-GUIDE.md`. Nothing new on screen, so no `Theme.cs` value was needed.

**NOT `trade reconcile`, and this is a deviation from the brief.** No such verb exists (`Core.Ops` has none; `grep -rn reconcile src/TradeAgent.TradeCli` → comments only), and `ReconcileAsync` only walks `Unreconciled()`, which excludes the unflagged UNKNOWN row this refuses over — it could never settle it, and naming it would be a false promise. The refusal names the two routes that do settle one: the owner's card, and Close all positions (item 1 is what makes that true).
**NOT DONE**: no box, no ATAS, no money, no UI run (`DashboardView` renders `PressOutcome.Summary` verbatim — read, not run); the reconciler is untouched and still never settles a press leg — a press's own UNKNOWN row refuses a leg here rather than being settled by it.
