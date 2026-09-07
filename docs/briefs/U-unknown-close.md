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
