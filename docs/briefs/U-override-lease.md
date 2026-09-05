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
