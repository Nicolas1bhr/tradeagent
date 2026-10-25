# U-board-b2 — part b2 of U-board-b: a board seat takes a turn through the real launch path — woken only by a proposal, admitted on the board's own budget, on its recorded class, blind in its packet — and whole board lives run on puppet seats in CI with no model called
**THIS PASS IS PART b2 ONLY** (written by seat A's brief leg, 2026-10-24 ~18:5x): items 1–7 below. STACKED on `U-board-b1` (`U-board-b1.md` beside this file):
start from b1's head, branch `u-board-b1-r51b` (b1 is RUNG 51, landing in seat A's train A11, 2026-10-25): `git rebase origin/u-board-b1-r51b` whenever it moves; once b1 is on main, rebase onto origin/main. Read b1's brief, its `## Report` and every fix pass first.
**Protection unit (seat A; R24 §§ 4.1 "Its cost" and "Its seats are real-model seats under the activation gate", 7 A2; ORGANISATION § 15 `U-board-b`). Containment,
BUDGET-001, AUTH-002 (blindness), ORDER-001, INBOX-001, CHANGE-001. NO RUNG expected (wakes are mission events; the board's spend is its launches' rows by role).
If one proves needed: the lowest free at landing, asked of the BOARD. Model/effort: opus/high** — it reuses the launch path b1 does not touch and adds no money path;
the policy writes, the two-seat rule and the veto are b1's and are only driven here.
**Depends:** `U-board-b1` (its acts, `BoardVerbCaller`, `board_seat_state`, `SetSeatStateUnderTest`, `BudgetShare`); `U-puppet-runtime` (landed). Pointers at `e317c011`.

**Observable result.** In CI, a life starts the real `AppHost` with two or three board seats ACTIVE through the test seam, each on a puppet manifest: board-1 is
woken, its puppet runs `trade board-propose` with the launch's own grant, board-2 is woken by the proposal alone, its prompt holds the intent and its hash and not
one word of board-1's rationale, it runs `trade board-decide --confirm`, and the gateway's policy in force is version 2 decided by `board`. No model is called, no
real money is spent, and in the product composition every board seat stays dormant and none is ever launched.

## Today (verified at e317c011)
- Admission refuses any name that is not an active position (`AgentRuntime/TurnMeter.cs:951`, `:1801-1820`); the activation gate reads the chart's standing and the
  proven-cell record (`:1474-1500`; `Core/Db/CellProofStore.cs:210`); the funding chain is org units only — `ai_attempt_funding.unit_id` REFERENCES `org_unit`
  (`Core/Db/Database.cs:2399-2405`), so a board launch can have no funding row there.
- Homes and seats per role: `App/AppHost.cs:322, 489` (`HomeFor`), `AgentRuntime/SeatResolver.cs:10`; grants per launch `Security/AgentGrants.cs:199`, orders only
  for `operations` (`:65`, `Core/Council.cs:67`). Wakes are mission events with a role (`Core/Db/MissionEventStore.cs:685`, kinds from `:12`); the loop
  `AgentRuntime/MissionLoop.cs:1815`.
- The puppet: `AgentRuntime/PuppetSeats.cs` (test host only), the life harness `tests/TradeAgent.UnitTests/PuppetLife.cs:100-120` (how to add a life), `:150`
  (`Begin`, with a `policy` hook), `:261` (`Wake`); the scripted program `tests/TradeAgent.Puppet/Program.cs`.
- The board's founding budget share 0.10 (`Core/Board/BoardFounding.cs:89`, read by `BoardStore.BudgetShare()`, `BoardStore.cs:94`; b1 adds the owner's later share).

## Items — one commit each, one-sentence messages
1. **A board seat's standing at admission.** `TurnMeter.Begin` admits a board id only when its latest `board_seat_state` is `active` — dormant, retired or unread is
   refused in words, nothing written, a refusal about the seat (`RefusalKind.Position`). The gate for a board seat: its RECORDED seat class from `board_seat`
   (broker|cell|harness, never `cli`) verified by the meter and ≥ `Minimum(DecisionVerbs)`; any runtime that is not a puppet ALSO needs
   `CellProofStore.HoldsProof()` (R24 § 4.1). Red: `A_dormant_board_seat_is_never_launched`, `A_board_seat_launches_only_on_its_recorded_class`.
   **Mutant:** admit a board id without reading its state ⇒ red.
2. **The board's own budget** (BUDGET-001; CHANGE-001). A board launch is reserved against the board's ceiling = its budget share (the owner's latest, else the
   founding 0.10) × the plan day-total in force — the day-total gate first, reserve before launch, an unpriced turn never counted as zero — summed over `ai_attempt`
   rows whose role is a board id; never an org unit's envelope and no `ai_attempt_funding` row. Nothing a board act writes changes that share (b1 item 8).
   Red: `The_board_spends_inside_its_own_share_and_no_units`, `A_board_act_cannot_raise_the_boards_share`.
3. **Seat, runtime, home, grant.** A board seat's home is its own folder under the board's (never under an org position's, never shared between seats — no side
   channel around the blind read); its runtime is the profile's for its model class, a puppet only in a test host; its grant's role is the seat id, which never
   places an order. Red: `Each_board_seat_has_its_own_home_and_a_grant_that_cannot_trade`.
4. **The wake and the packet.** On `proposed`, ONE `board` mission event per OTHER active seat, never the proposer; the review tick and every scheduled look skip
   board ids ("never on a heartbeat", R24 § 4.1); the wake closes when the seat decides or the act closes. The packet is app-composed: for a confirmer the BLIND
   read (b1 item 4) — intent, hash, direction, refs, figures in force, expiry and the commands — and nothing of the proposer's rationale or forecasts, no other
   seat's words, no watcher total. Red: `A_proposal_wakes_every_other_active_seat_and_nothing_else_does`, `The_confirmers_prompt_holds_no_rationale`
   (read the puppet's recorded prompt). **Mutant:** render the act's rationale into the packet ⇒ red.
5. **Board lives I — the acts** (`PuppetBoardLivesTests`, one class; every life `PuppetLife.Begin` + the seat seam): a narrowing by one seat refuses the next order
   above it; a widening proposed by board-1 and confirmed by board-2 is in force and the paper envelope follows; a refusal by board-2 applies nothing.
6. **Board lives II — the edges:** an expiry (the clock past `ConfirmationLife`) and a stale proposal apply nothing and are recorded; the owner's veto after a
   confirmed widening returns every field to the narrower of now and before; a power proposal changes nothing and is listed for the owner; a board puppet's
   `trade buy` and `trade ledger-add` are refused; a position's puppet's `trade board-propose` is refused; two seats of one vendor family record `same_family`.
7. **Guards.** NO REAL MODEL: every board life's manifest is a puppet and no `ai_attempt` row of a life names a real model or spends money. NOTHING ACTIVATES IN THE
   PRODUCT: `AppHost.Composed` with no test seam leaves every seat dormant through a loop tick, launches none, and `Board.AnyActive` stays false (so the owner's
   paper-only presses stay his, the a2 rule). **Mutant:** a composition-root line that marks board-1 active ⇒ red.

## NOT in scope
b1's acts, intents, direction table, veto and power records (`U-board-b1`). A position's petition to the board, boundary and detector wakes (`U-board-c`,
`U-watcher-board`). The product writer of `active` (`U-board-activate`, after a RECORDED proven cell). Live anything (`U-board-live`). Any UI (seat B).

## Protections, and how each is kept
**Containment** — a board seat launches only active, only on its recorded class, a real model only with a recorded proven cell, and nothing in the product
activates one. **BUDGET-001** — its own share, gated first, reserved before launch, unknown never zero. **AUTH-002** — the confirmer's packet, home and reach
carry no rationale. **ORDER-001/SIGNAL-001** — a board grant never places an order. **INBOX-001** — the board acts only by verb calls of its own launches.
**CHANGE-001** — nothing a board seat does moves its share or its seats. **Honesty** — no life claims a model ran.

Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.

## Added 2026-10-25 by seat A (from U-board-b1's re-review 2, fleet/tmp/A-org/board-b/U-board-b1-rereview2.md)
**Item 8 — an envelope's life follows every act since its last grant, not only the last one** (`src/TradeAgent.Gateway/TradingGateway.cs` ~:516, as b1 landed it). Today a confirmed two-seat widening followed by a one-seat narrowing before the next health pass loses the widening's authorised life (the replacement ends on the old expiry). It fails CLOSED (earlier, never later), so it was not fixed in b1. Fix: the life is fresh when ANY act since the envelope's grant was a confirmed widening of its terms; a narrowing alone never lengthens it (b1's rule kept). Settling check, red-first: grant `.05` for ten days; on day nine confirm `.10`, then narrow to `.08`; with and without a health refresh between the acts, both expire on day nineteen. ONE watched mutant.
**Card noted, not this unit:** CouncilLoopTests' `Noon()` helper fails 38 tests in local time on a DST-change day ("The UTC Offset of the local dateTime parameter does not match"); CI runs in UTC.
