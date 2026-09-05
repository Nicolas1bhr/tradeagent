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
