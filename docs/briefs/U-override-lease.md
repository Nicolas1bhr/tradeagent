# U-override-lease — the human override respects the dispatch lease, and a broker's definite answer still lands on a row a human moved

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (unconfirmed work; the override),
finding 1 of `docs/REVIEW-2026-09-05b.md` with its probe output (P3), and the `U-stranded` section of `BUILD-STATUS.md`
(the lease the reconciler already has). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`.
Worktree `~/Projects/ai-trading-software-for-mihael-worktrees/override-lease`, branch `u-override-lease` from `main`.
Another builder works `TradingGateway.cs`'s press code at the same time (`U-press-inflight`): stay out of the emergency
press and its drift re-read; you own `ForceResolve`, `Settle`/`LateDefiniteSettle` and the Dashboard's unconfirmed card.

**The finding, proven on branch `review-probes-b` (`tests/TradeAgent.FaultTests/ReviewProbes2.cs`,
`P3_the_operator_override_writes_off_an_order_whose_dispatcher_is_still_on_the_wire`).** `Unreconciled()` feeds the
Dashboard card (`DashboardView.cs:228`) without consulting the lease, so a live DISPATCHING row is rendered with its two
override buttons; `ForceResolve` (`TradingGateway.cs:3052`) writes a terminal state straight onto it, clears the flag and
the latch, and trading resumes; the dispatcher's FILLED then meets `Settle`'s rescue arm `actual.State is UNKNOWN or
RECONCILING` (`:990`), which a ForceResolved row is not, and is filed `already_settled`. End state quoted by P3: the broker
holds ES 1, the record says CANCELLED "resolved by user: no such order exists", unflagged, trading resumed.

1. **`ForceResolve` refuses while `_dispatches[id].Live`**, with a sentence naming the two numbers the reconciler's
   refusal names (still on the wire for X s of a possible Y s) and telling the owner to wait; the card renders that
   sentence instead of the buttons for a leased row (`Unreconciled()` or the card asks the lease). RED first: lift P3 as
   the unit's test → GREEN; mutant (the lease check deleted) → RED with P3's end state.
2. **A broker's definite answer still lands on a row a human moved.** Widen `LateDefiniteSettle`'s `from` set so a
   dispatcher's FILLED/REJECTED for a row the owner ForceResolved (possible only after the lease has expired, once item 1
   is in) re-flags the row and records the platform's answer beside the owner's claim — never silently. RED first (a
   seam: the lease expires, the override runs, the late FILLED arrives) → GREEN; mutant → RED.
3. **Both directions:** an override on a row whose dispatcher is DEAD (no live lease) works exactly as today; one test.

Yours: `TradingGateway.cs` (`ForceResolve`, `Settle`, `LateDefiniteSettle`, `Unreconciled` or a lease query beside it),
`DashboardView.cs` (the card), any other in-process caller of `ForceResolve`, `docs/CONTRACTS.md`'s override paragraph,
`docs/USER-GUIDE.md`'s override section. No pipe op, no CLI verb: operator authority stays in-process. Commit per item,
no trailers. Gate: Release `--no-incremental` → 0 warnings; the class 3×; full suite once to a file (one suite at a time
on this Mac; a `Timing` test that fails while another suite runs → that class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; RED → GREEN → mutant per item with the outputs; gate counts;
what you did NOT do. Verified or NOT VERIFIED.

Branch `u-override-lease`, rebased onto `main` `38351a1`, worktree clean; last code commit `49292df`, and the branch tip is this report's own commit on top of it. Every claim below is a quoted run.
**1. `ForceResolve` refuses while `_dispatches[id].Live`.** RED (P3 lifted into `OverrideLeaseTests`, Fault): `Assert.Throws() Failure: No exception was thrown`, over `the card asks the lease : still on the wire for 120s of a possible 50s`. GREEN: `the override : INVALID_REQUEST — TradeAgent is still sending this order — still on the wire for 120s of a possible 50s. Wait for it to answer before resolving it: it can still reach the broker…`, then `record now : FILLED`, `position at the broker : ES 1`, `engineering :` (empty).
MUTANT (lease check deleted) → RED with P3's end state verbatim: `the override : went through` · `record now : CANCELLED, needs_reconciliation=False, last_error=resolved by user: I checked in ATAS and no such order exists` · `orders at the broker : 1 FB-1 FILLED 1 ES` · `trading : resumed` · `engineering : already_settled`.
`Unreconciled()` is NOT filtered — the row stays on the card and keeps trading paused; the card asks `StillOnTheWire` every tick and puts that sentence where the two buttons are, disarming a half-pressed one. One sentence, built once, for the reconciler and the card.
**2. A broker's definite answer lands on a row a human moved.** Seam: two gateways over one store (the app and `tradeagent-gateway.exe`), lease in the dispatcher, card in the other. RED: `record now : CANCELLED, needs_reconciliation=False` · `broker reference : none — the broker never sent one back` · `engineering : already_settled`.
GREEN: `needs_reconciliation=True` · `broker reference : FB-1` · `what the card will read : resolved by user: I checked in ATAS and no such order exists — but Simulator (built in) then answered FILLED for order FB-1, 1 filled. That is not CANCELLED, and this record is flagged again until you have looked.` · `trading in the dispatcher: paused` · `engineering : late_definite_over_an_override`.
The row keeps the owner's state — the table refuses to leave a terminal one and rewriting it would erase their account — and gains the flag, the platform's answer and reference, and a paused gate. MUTANT (the terminal arm of the `from` set deleted) → RED, back to `already_settled`, unflagged, trading resumed.
**3. Both directions.** One test, both shapes of a dead dispatcher: an entry this process watched END (`the card asks the lease : (nothing — the dispatch ended)` → `CANCELLED, needs_reconciliation=False`, `trading : resumed`) and a DISPATCHING row it never dispatched (`(nothing — nobody is flying it)` → `FILLED`, `trading : resumed`). Unchanged from today.
**Gate.** `dotnet build TradeAgent.sln -c Release --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`. `OverrideLeaseTests` 3× → `Passed! - Failed: 0, Passed: 3` each time. Full suite in Release, one at a time, to a file: **Unit 219 + Fault 242 + Integration 582 = 1043, 0 failed.**
An earlier full run had 1 failed: `PeerRowTests.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure`, `TimeoutException : condition was not met in time` on its 10 s wall-clock wait, in `AtasConnector` pipe code this unit does not touch. That class then passed 3× alone (`Passed: 8` each) and the whole suite passed on the re-run quoted above.
Names: `git diff main --name-status -- tests/` prints one line, `A tests/TradeAgent.FaultTests/OverrideLeaseTests.cs` — 3 added, **0 removed**, no existing test file touched. Also changed: `Stores.MarkNeedsReconciliation` takes an optional broker reference (filled in, never overwritten), `docs/CONTRACTS.md`'s override paragraph, `docs/USER-GUIDE.md`'s two new "will look like a fault" paragraphs.
**NOT done.** No pipe op and no CLI verb — operator authority stays in-process. Nothing in `OperatorCloseAllAsync`, `OperatorCancelAllAsync`, the press capture or the drift re-read (`U-press-inflight` owns them). No box, no real ATAS, no money, no UI run: **the card's two visual states are NOT VERIFIED on screen** — only the query they read and the gateway refusal behind them. No mutation sweep beyond the one mutant per guard. Pushed nothing, merged nothing, entered no other worktree.
