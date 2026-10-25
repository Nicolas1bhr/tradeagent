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

## Report
**Tip** (code) `0b52f9ee`, on main `c720c9f7` (b1 landed there replayed, so `rebase --onto origin/main 29e1b348`; clean). 13 commits. **No rung.**
**Gate.** (1) Release `--no-incremental` → "0 Warning(s) 0 Error(s)". (2) TZ=UTC ×3 via suite.sh: BoardTurnTests|PuppetBoardLivesTests|BoardActTests|HomeFilesGuardTests|PuppetLivesTests|PuppetSeatTests "Passed: 76, Total: 76" ×3; OversightInvisibilityTests (Integration) "Passed: 6, Total: 6" ×3; neighbours once (Board|Puppet|Mission|Org|Seat|Canon|Workspace|Relay|TurnMeter|Billing|Lane|Grant|Gate|Envelope|Owner) "Passed: 987, Skipped: 6". (3) CI **40174190089 success, 12/12** on 0b52f9ee (first run 40172165988: 11/12, windows-5 red on my own life's race — board-2 could turn before its script was written; fixed `0b52f9ee`). (3b) Codex review `fleet/tmp/A-org/board-b/U-board-b2-review.md` — not acted on. (4) names.sh main → "removed: 0", added 15.
**Items.** 1 done: `BoardSeatGate` (standing via the one seat reader; recorded class = verified class, ≥ DecisionVerbs minimum; non-puppet needs `HoldsProof`; no unconfined neighbour) asked by the meter, the seam and AtLaunch through `PositionActivation.Decide`. 2 done: `AiAdmissionRule.BoardShare` — day ceiling first, then share × plan day-total in force over every board row (`BoardTotalsBetween`, unpriced row at its reservation, an unpriced launch refused), no `ai_attempt_funding` row. 3 done: home `workspace/board/<id>` (board canon only; relay publishes and snapshots nothing for a board id), grant role = seat id. 4 done: one `board` wake per OTHER active seat in Propose's write (class Unblocking), closed on decide/stale/confirm/expire/veto; Walk and self-requests skip board ids; `BoardPacket` = policy in force + blind reads + commands. 5–7 done (`PuppetBoardLivesTests`, 8 lives/guards). 8 done (`WidenedSince` reads every version since the last grant).
**Deviations.** (a) No model-class→runtime profile exists: a board seat runs the app's prepared runtime (puppet in a test host). (b) Board ceiling = share × PLAN day-total for every billing class (a key launch must also fit the money cap). (c) A test-host puppet plays the broker class unverified (`SeatProfile`, broker only) and is never priced at the broker pin (`AppHost.ModelOn`) — else no broker seat can be played and rows would name gpt-6-luna. (d) `RuntimeManifest.PuppetSeatClasses` (per-role class, test code only) and `IsTestHostPuppet`. (e) Reviewed-list entry added to `HomeFilesGuardTests` (BuildBoardHome's two folder creations) and `AiAttemptStore.BoardTotalsBetween` pinned in `OversightInvisibilityTests` (a new total joins the list, as that guard says).
**RED → mutant.** 1 src stashed: 2 red ("Values differ"/"Sub-string not found") · Standing ignores status → "a dormant board seat was launched". 2 no BoardShare → "the board spent past its own share". 3 no board HomeDir → "'board-1' is not a position id, so it has no home". 4 Propose raises no wake → "Collections differ" + board-2 never turned · rationale rendered into the packet → "Assert.DoesNotContain() Failure: Sub-string found" (2 tests). 7 puppet at broker pin → "(board-1) names a model: requested gpt-6-luna" · composition root inserts board-1 active → "Assert.All() Failure … board-1 … Status = active". 8 RED "Expected (0.08, …11-13) Actual (0.08, …11-04)" (no pass between) · mutant `from = setBy - 1` → same red.
**Tests box:** 20261025-071030-6dbc0bb6 RED on 6dbc0bb6 (Fault 502/0, Integration 747/2, Unit 2516/3): mine, fixed after it — 2 OversightInvisibility (new total unpinned) and BoardTurnTests home path (mixed separators on Windows); not my diff — CouncilLoopTests `LocalMidnight` (the DST-day card) and ModelCatalogTests "Le chemin d'accès spécifié est introuvable" on a long temp path (green on windows-latest CI). Not re-run (ready asked once).
**NOT done / NOT verified.** No product writer of `active` (U-board-activate); the app not run; no real model turn; a cell-class board seat not exercised on a puppet; the full local suite not run (CI's).
**Fix pass (fresh fixer, 2026-10-25)** — code tip `63550ae3` on main `e04c173c`; commits 6e2dcb3b (P1-1), 964262cb + f83cc54b + 63550ae3 (P1-2, the last two from Codex re-reviews 1–2). No rung.
P1-1: the board's ceiling sums every board row of EVERY billing class (`BoardTotalsBetween` lost its class filter; a key launch still fits the money cap). RED `The_boards_share_is_one_budget_across_every_billing_class` "the board spent its share in plan and then again in key" (and key→plan); mutant: class filter restored → both red.
P1-2: `BoardStore.VetoedVersion`; `WidenedSince` renews nothing over a range holding a vetoed version; `AuthorisedLife` replays every grant on the pair; a veto withdraws a standing envelope past its authorised life even with no version. RED `A_vetoed_widening_never_lengthens_an_envelopes_life` (3 cases) "Expected (0.04, …11-04) Actual (0.04, …11-13)", `A_veto_that_restores_no_terms_…`, `A_surviving_widening_never_carries_a_vetoed_ones_duration`; mutants: veto check removed → 3 red; veto withdrawal removed → red; vetoed version skipped → red.
Gate: Release --no-incremental 0/0; TZ=UTC ×3 BoardTurn|BoardAct "Passed: 50", OversightInvisibility "Passed: 6"; neighbours "Passed: 314"; names.sh removed 0, added 20. CI **40179212376 success 12/12** on 63550ae3.
Reviews: `fleet/tmp/A-org/board-b/U-board-b2-rereview-1.md`, `-2.md` (both acted on), `-rereview.md` (3: one P2 — any vetoed version in a range, even one that never touched `paper_envelope`, fails it closed and can withdraw a valid envelope; errs short, not acted on). Deviation: OversightInvisibilityTests' per-ceiling BoardTotalsBetween call removed with the overload.
**Re-stack (fresh fixer, 2026-10-25)** — branch `u-board-b2-r2`, code tip `73096028` on main `416365e8` (U-org-chief). One rebase, one conflict (`BoardStore.ConfirmAct`): kept main's chief path (no version for a chief act, its event names the version in force) and added b2's `CloseWakes` on confirm; the veto hunk merged clean (both the chief's unseat and `CloseWakes`). No rung.
Seam: `VetoedVersion` reads `board_policy.act_id`, which a chief act never writes, so a vetoed chief act withdraws no envelope and blocks no renewal — green at the resolved tip, no code change. Test `A_vetoed_chief_act_neither_withdraws_an_envelope_nor_blocks_its_renewal` (and a vetoed policy act still withdraws); watched mutant (VetoedVersion also counting a vetoed act whose confirmed event names the version) → "vetoing the chief act … read as a veto of version 3".
Gate: Release --no-incremental 0/0; TZ=UTC ×3 BoardTurn|BoardAct|BoardTests|BoardReadersTests|OrgChiefTests "Passed: 86", OversightInvisibility "Passed: 6"; PuppetBoardLives "Passed: 8"; names.sh removed 0, added 21. CI **40182134741 success 12/12** on 73096028.
Review of the resolution: `fleet/tmp/A-org/board-b/U-board-b2-r2-review.md` — "No defects found in the requested rebase-resolution scope."
