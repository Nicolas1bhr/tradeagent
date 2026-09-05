# U-press-inflight — the emergency press sees a closing order already on the wire, so Close All flattens instead of reversing

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (the emergency controls, the five
leg words), finding 2 of `docs/REVIEW-2026-09-05b.md` with P6 and P6b quoted, and the `U-press-atomic` and `U-gates`
sections of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/press-inflight`, branch `u-press-inflight` from `main`. Another
builder works `ForceResolve`/`Settle` in `TradingGateway.cs` at the same time (`U-override-lease`): you own the press —
`OperatorCloseAllAsync`, `OperatorCancelAllAsync`, the capture, the drift re-read (`:2497`), the leg dispatch (`:2269`).

**The finding, proven on branch `review-probes-b` (`tests/TradeAgent.FaultTests/ReviewProbes2.cs`,
`P6_an_agent_close_in_flight_and_the_operators_close_all_press_both_send`, bounded by `P6b_…`).** The press's uniqueness
constraint covers PRESS rows and the drift re-read compares POSITIONS; an agent's `close` is an ordinary
`execution_request` inside the connector call, so the re-read still shows ES 2, the press captures 2 and sends a market
sell 2 while the agent's sell 2 is on the wire. P6: three orders, `position at the end: ES -2`, after the owner pressed
the control whose purpose is to flatten. P6b: with the press row on disk FIRST, `U-gates`' dispatch re-check refuses the
agent's leg — the reverse ordering holds; only the overlap is open.

1. **A press leg is refused while open work exists against its instrument.** Before a close leg reaches the wire the
   press asks the set the gateway already keeps — a `DISPATCHING` or `UNKNOWN` `execution_request` on that instrument,
   any session, any verb that can move the position — and refuses that leg with the honest leg word and a sentence
   naming the request it is waiting on; the press's answer says so ("1 leg waited on an order still on the wire"). A
   bounded wait inside the emergency budget before the refusal is allowed, not required. The check and the wire are
   one statement over one set, no window between them. RED first: lift P6 → GREEN (nothing sent for ES by the press;
   the agent's close fills; position 0); mutant (the open-work check deleted) → RED with P6's `ES -2`; P6b lifted too.
2. **The agent's own `close` is the same class** (Codex F3, `TradingGateway.cs:1855`): its size and side come from a
   position read BEFORE the awaited account and risk reads, so an intervening fill turns a close into a new or reversing
   position. Re-read at dispatch, inside the gate, and recompute or refuse when the position moved. RED first
   (a fill lands between the snapshot and the wire) → GREEN; mutant → RED.
3. **Cancel All**: say, with a probe, whether a cancel leg can race an agent's modify or cancel of the same order in
   the same way; fix it with the same statement, or record why it cannot happen.
4. **The words:** `docs/CONTRACTS.md` and `GatewaySchema.cs` say what a waited-on leg reads; the Dashboard renders it.

Yours: the press code in `TradingGateway.cs`, `GatewayPipeServer.cs`'s close-all/cancel-all answers if a field is added,
`GatewaySchema.cs`, `docs/CONTRACTS.md`, `DashboardView.cs`'s press-result rendering. Commit per item, no trailers.
Gate: Release `--no-incremental` → 0 warnings; the class 3×; full suite once to a file (one suite at a time on this
Mac; a `Timing` test that fails while another suite runs → that class alone 3×); names vs `main` 0 removed.

## Report — append here, commit it, ≤20 lines: tip sha; RED → GREEN → mutant per item with the outputs; gate counts;
what you did NOT do. Verified or NOT VERIFIED.

## Report

Code tip `ef7c4b2` (4 commits, everything below was gated there); this report is the 5th commit on top. Branch `u-press-inflight`, rebased onto `main` `d630974` (the rebase changed nothing under `src`, `tests` or the solution). Verified by running the commands below.

1. **A press leg is refused while open work exists against its instrument.** RED (P6 lifted, `An_agent_close_in_flight_stops_the_press_sending_a_second_close_for_that_instrument`): `orders at the broker: 3 FB-1 Buy 2 FILLED FB-3 Sell 2 FILLED FB-5 Sell 2 FILLED / position at the end: ES -2`. GREEN: `press: 1 leg waited on an order still on the wire, so nothing was sent for it: ES is waited on by p6-agent, still DISPATCHING. Press again once that order has an answer.` / `orders: 2` / `agent close answered: FILLED` / `position at the end:` (flat). The guard is the leg's own write-ahead INSERT (`Stores.TryCreateFlagged`'s new `$wire` clause) so check and wire are one statement; per leg, so `The_other_instruments_of_the_same_press_are_still_closed` (NQ closed, ES waited). P6b lifted unchanged: `TRADING_PAUSED_UNRECONCILED`, 2 orders. MUTANT (the `$wire` NOT EXISTS replaced by `1=1`) → 2 RED, `Expected: 2 Actual: 3`, `position at the end: ES -2`.
2. **The agent's own `close` (Codex F3).** RED (`A_fill_between_the_snapshot_and_the_wire...`, a fill inside the quote read): `Assert.Throws() Failure: No exception was thrown`, position 2 → 1 → close Sell 2 sent. GREEN: `POSITION_MOVED — ES was 2 when this close was sized and is 1 now, so Sell 2 would not flatten it; nothing was sent.`, record `CREATED`, 2 orders, position `ES 1`; plus the flipped case (2 → −2, refused not doubled) and the ordinary close still `FILLED`. MUTANT (`if (live == sizedFrom) return;` → `return;`) → 2 RED, `No exception was thrown`. Over the pipe: `outcome: not-sent`, `state: CREATED`, `not_sent: 1`, `attempted: 0`, `closed: 0`.
3. **Cancel All: not the same class, with probes.** A cancel leg computes nothing from a reading. Agent modify held inside `ModifyOrderAsync`, then the press: `orders: FB-1 CANCELLED`, `working at the end: 0`, the modify `UNKNOWN, flagged=True`. Agent cancel held inside `CancelOrderAsync`: `agent cancel answered: REJECTED`, `working at the end: 0`. No fix; recorded in `docs/CONTRACTS.md`.
4. **The words.** `docs/CONTRACTS.md` (two press bullets + a dispatch-gates paragraph) and `GatewaySchema.cs` (`close`, `close-all`). `DashboardView.PressAsync` already renders `PressOutcome.Summary` verbatim — read, not run.

**Deviation from the brief, stated:** the set is `DISPATCHING`, **not** `DISPATCHING or UNKNOWN`. With UNKNOWN in it the shipped `UnconfirmedLatchTests.Confirming_one_outcome_does_not_lift_another_requests_pause` went RED — `Assert.Single() Failure: The collection was empty` — because the press wrote no row at all: an UNKNOWN record is the ordinary state of the emergency somebody is pressing the button about, so refusing on it re-imposes the pause these controls bypass on purpose. **Residual, NOT fixed:** an UNKNOWN closing order on the same instrument can still fill after the press's close and reverse the position; stated in `docs/CONTRACTS.md`.

**Gate** (Release, at the tip): `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 Warning(s), 0 Error(s)**. This unit's classes 3× → 8/8 Fault + 2/2 Integration each time. Full suite once to `/tmp/u-press-inflight-suite.txt` → **Unit 219 + Fault 247 + Integration 584 = 1050, 0 failed** (run at the pre-rebase tip; `git diff` over `src`, `tests` and `TradeAgent.sln` across the rebase is empty). Test names vs `main`: **0 removed, 10 added**. Secret scan of the whole diff vs `main`: clean.

**NOT done:** no box, no real ATAS, no money, no UI run — `DashboardView` is unchanged and its rendering of the new sentence is read only. `AtasStrategyAdapter.Modify`'s refusal of a finished order (`:1596`) is quoted from source, never executed. No new pipe op and no new operator authority. Nothing touched in `ForceResolve`, `Settle` or `LateDefiniteSettle` (`U-override-lease`'s). One `ErrorCode` added (`POSITION_MOVED`) with its catalogue entry. `ModifyAsync`'s record now carries its instrument instead of `"-"`, which is what makes a modify visible to the new guard.
