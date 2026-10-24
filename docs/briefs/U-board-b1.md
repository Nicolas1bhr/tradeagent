# U-board-b1 — part b1 of U-board-b: the board's acts as records and broker verbs — one seat narrows at once, two seats widen (a proposer and a confirmer blind to its rationale, bound to the intent's hash, expiring), the owner's in-process veto narrows only, and the board's own power has no writer it can reach
**THIS PASS IS PART b1 ONLY** (written by seat A's brief leg, 2026-10-24 ~18:5x): items 1–10 below, their red-first tests and watched mutants. Part b2 (`U-board-b2.md`
beside this file: the board seat's turn and the whole board lives on puppet seats) is the next unit, stacked on this branch. Do not build it.
**Protection unit (seat A; R24 §§ 4.1–4.4, 5, 7 A2; ORGANISATION § 15 row `U-board-b`, `docs/ORGANISATION.md:433`). AUTH-001/AUTH-002, CHANGE-001, ORDER-001/SIGNAL-001,
EVID-001, INBOX-001, BUDGET-001, refuse-never-guess, recovery. A RUNG: the LOWEST FREE at landing (50 today; `U-org-verbs-e` also wants one — renumber at the rebase,
the number spelled once: `Versions.BoardActsRung` + `Database.cs`). Model/effort: opus/xhigh** — it writes the policy the dispatch gate enforces, and every widening
path of it is AUTH-002 in code; a wrong direction or a veto that widens is a limit raised by nobody.
Source: R24 (`docs/research/2026-10-23/R24-security-board.md`) §§ 0, 4.1–4.5, 5, 7, 9 — read them; the a1/a2 briefs and `a2-rulings.md` (`fleet/tmp/A-org/board-a/`)
— the general rule binds here: until a board seat is ACTIVE the owner's paper-only presses stay his, and NOTHING IN THIS UNIT ACTIVATES A BOARD SEAT IN THE PRODUCT.
**Depends:** U-board-a1/a2 + U-settings-shed LANDED (rung 49). Pointers below are at `e317c011` (code identical at `bb4f177e`); re-grep at your rebase.

**Observable result.** In a test host, a launch the ledger recorded for an active board seat (the `OrgAssignmentTests.cs:68` pattern; b2 builds the real
turn) sends `board-narrow` over the pipe and the gateway refuses the next order above the new limit — one
seat, a new policy version decided by `board`, in force at once. Another seat's `board-propose` of a raise changes nothing until a different seat confirms it with
the intent's hash; before its decision is sealed that seat can read the intent, the app-checked evidence ids and the app's own figures, never the proposer's
rationale or forecasts; a refusal, an expiry and a stale proposal are records. The owner's in-process veto of a confirmed raise writes a version decided by `owner`
whose every field is the narrower of now and before. No op, verb or board act reaches the veto, the board's composition or its budget. In the product, nothing
changes: every board seat stays dormant and the founding policy governs.

## Today (verified at e317c011)
- `board_policy` CHECKs `decider='app'`, `basis='founding'`, mode OBSERVE|PAPER, `live_activated=0`, `supersedes = version-1` (`Core/Db/Database.cs:2956-2967`);
  `board_seat.status` CHECK `'dormant'` and the row is sealed against UPDATE (`:2948-2955`, `:2978-2981`) — so a seat's state CANNOT live on its row.
- `BoardStore.InForce` refuses any top version not `app`/`founding` (`Core/Board/BoardStore.cs:130-131`); `History()` has no rationale (`:167-168`);
  `BoardDecider`/`BoardBasis` hold one value each (`Core/Board/BoardFounding.cs:101-110`); `AnyActive` reads the sealed row's status (`BoardReader.cs:46`).
- The gateway reads the policy ONCE, at construction (`Gateway/TradingGateway.cs:2560`, `:3351-3360`); `AppHost` reads `Gateway.Policy` live (`App/AppHost.cs:240,
  425, 1253, 1284`). The envelope is granted from the terms only under `app`/`founding` (`TradingGateway.cs:465-470`); withdraw is `:900`.
- The org verbs: `Ops.Organisation` (`Core/Protocol.cs:282`), `Mutating` (`:288`), the caller gate `OrgVerbCaller` (`Gateway/GatewayPipeServer.Mandates.cs:25`),
  the op table and dispatch (`GatewayPipeServer.cs:363-380`, `:1418-1440`), the schema (`GatewaySchema.cs:617`), the CLI (`TradeCli/Program.cs:398`).
  Board ids are refused as positions everywhere (`OrgStore.cs:603`, `OrgStore.Decisions.cs:268, 682, 687`). Order permission is `operations` alone (`Core/Council.cs:67`).
- Refs are checked by `OrgStore.RefsRefusal`/`RefExists` (`OrgStore.Decisions.cs:216, 232`, private); the closed forecast vocabulary v2 (`:89-135`); the resolver's
  sweep (`ForecastResolver.cs:54`), run from `AppHost.SweepMandates` (`App/AppHost.cs:2206-2216`).
- Guards to extend: `No_app_path_selects_a_live_mode`, `Only_BoardStore_writes_board_tables`, `The_policy_holds_no_evidence_setting` (`tests/.../BoardTests.cs:642,
  669, 686`); `Organisation_verb_names_and_pipe_op_names_are_disjoint` (`OrgAssignmentTests.cs:346`); the recorded-launch helper (`OrgAssignmentTests.cs:68`).

## Items — one commit each, one-sentence messages
1. **The rung.** `board_act` (id `bact-*`, kind `narrow`|`propose`, proposer seat, its attempt, the intent canonical JSON + `intent_sha256`, the policy version it was
   made against, `rationale` (a CLAIM, verbatim), refs (app-checked), `evidence_class` NULL with a CHECK that keeps it NULL — no b1 intent allocates to a version;
   `U-board-live` adds the allocation kind and writes it — `expires_at`, `at`); `board_act_event` (proposed | applied | confirmed | refused | expired | stale | vetoed,
   seat, attempt, its own sealed rationale, the seats' vendor families and `same_family`, policy version written); `board_forecast` + `board_forecast_resolution`
   (`org_forecast`'s shape, forecaster a board seat); `board_power_proposal` (item 8); `board_seat_state` (seat, state dormant|active|retired, by `app`|`owner`, at).
   `board_policy` REBUILT (no backward compat; its rows copied byte-for-byte, hash unchanged) with `decider IN ('app','board','owner')`, `basis IN
   ('founding','act','veto')`, `act_id` (NULL iff founding), mode and `live_activated` CHECKs UNCHANGED. Every table: UPDATE/DELETE/insert-over-key refused by trigger
   (rung 49's three seals). Statements idempotent, the rebuild in one transaction, the stamp last. `Versions` paragraph.
   Red: `Rung_N_rerun_after_a_crash_before_its_stamp_writes_nothing_twice`; `No_row_of_the_board_store_holds_a_live_mode_or_an_activation` re-run after the rebuild.
   **Mutant:** rebuild `board_policy` without its mode CHECK ⇒ the live-mode test red.
2. **The typed intent and its direction** (`Core/Board/BoardIntent.cs`, pure). An intent is 1–10 changes `{field, from, to}` over the paths `Labels.BoardFields` names
   (`Labels.Board.cs:33-70`), strictly parsed (unknown field, `live_activated`, a live mode, a value not in the field's type → refused), canonical, hashed.
   `BoardDirection.Of(field, from, to)` — ONE table: lower narrows for every quantity, notional, count, loss, allowance, lane and the plan day-total;
   HIGHER narrows for `loss_min_closure_hours`, `loss_strike_window_days`, `review_minutes`, `head_ceiling_hours`, each re-wake floor; lower narrows
   `valuation_loss_exit_minutes`; an allowlist narrows only by removal; `role_shares` narrows only if no share rises; the envelope narrows to none, or with the same
   venue/account/instrument and no ceiling, count or life larger (null days = unbounded); PAPER→OBSERVE narrows. An unlisted field, or ANY change that is not a
   narrowing, is a WIDENING; an intent narrows only if every change does. Red: `A_raise_is_never_a_narrowing` (every field, both ways); `An_unlisted_field_widens`.
   **Mutant:** default an unlisted field to narrowing ⇒ red.
3. **One seat narrows, at once** (`BoardStore.Narrow`). Inside ONE write: the caller is an ACTIVE seat (item 6), every `from` equals the value in force, the intent
   narrows, refs exist (make the ref check an internal static shared with `OrgStore`), forecasts are v2 vocabulary (validated as `OrgStore.AddForecast` does) —
   then the act, its `applied` event, the new policy version (`board`/`act`, `supersedes` the last, body strict-read back) and its forecasts. Refused in words,
   nothing written, otherwise. `ForecastResolver.Sweep` resolves `board_forecast` rows too. Red: `A_narrowing_by_one_seat_is_in_force_for_the_next_order`
   (the gateway refuses quantity above it); `A_narrowing_against_a_value_no_longer_in_force_is_refused`. **Mutant:** skip the narrowing check ⇒ `board_narrow` of a raise passes, red.
4. **Two seats widen** (`BoardStore.Propose`/`Decide`/`Expire`). Propose writes the act + `proposed`, applies nothing, expires after `BoardLimits.ConfirmationLife`
   = 24 h (an app constant, never policy data — CHANGE-001). Decide (confirm|refuse) by a DIFFERENT active seat, naming the act and its `intent_sha256` (mismatch
   refused), its OWN rationale sealed in the event; one decision per seat per act; a seat on another vendor family is REQUIRED when one is active, else
   `same_family=1` is recorded with its words. Confirm re-checks every `from` against the value in force — a moved field closes the act `stale` — and applies in the
   decision's write. Refusal and expiry are events (`Expire` runs on the mandate sweep's tick and lazily at decide). THE BLIND READ: `board-show` of an open act to a
   seat that has not sealed returns the intent, its hash, direction, refs, the app's figures in force and the expiry — never the proposer's rationale, forecasts or
   any other seat's rationale; after its seal, all. Red: `The_confirmer_sees_no_rationale_until_its_decision_is_sealed`, `A_confirmation_binds_to_the_intents_hash`,
   `The_proposer_cannot_confirm_its_own_act`, `An_expired_or_stale_proposal_applies_nothing_and_is_recorded`, `A_refusal_is_a_record`,
   `Same_family_is_recorded_and_refused_when_another_family_is_active`. **Mutant:** return the rationale to an unsealed seat ⇒ the blind test red.
5. **The envelope and the gateway follow the policy at once.** After every act or veto commits, the gateway re-reads the policy in force (`PolicyInForce`) before
   the verb answers; a restart reads the store. The standing policy envelope follows its terms: a narrowing withdraws it in the act's write (the existing withdraw
   path) and the narrower terms are granted; a widening grants under the act's version; the reason and Activity line name "the security board" and the version,
   never the owner (`GrantFoundingEnvelopeIfDue` generalised; Q5's withdraw stays his). Red: `A_narrowed_envelope_is_withdrawn_in_the_acts_write`,
   `A_confirmed_envelope_widening_grants_under_the_act`. **Mutant:** keep the founding-only condition at `TradingGateway.cs:470` ⇒ the widening test red.
6. **The board's caller, and nothing else it reaches.** `BoardVerbCaller(ctx)`: the role its launch grant proved is a board id (`BoardSeats.IsBoardId`) whose latest
   `board_seat_state` is `active`, the attempt the ledger recorded is that seat's, its RECORDED assurance ≥ `CapabilityPolicy.Minimum(DecisionVerbs)`; a position
   is refused, `OrgVerbCaller` still refuses board ids. A board id is refused on EVERY write op that is not a board verb (ledger, lens, material note, mandate-*,
   decision-*, orders): no side channel around the blind read, no text that acts. `BoardReader.AnyActive` reads the state table. The ONLY writer of `active` is
   `BoardStore.SetSeatStateUnderTest` (internal; the guard's `…UnderTest` rule). Red: `Only_an_active_board_seat_exercises_a_board_verb`,
   `A_board_seat_writes_nothing_but_board_acts`. **Mutant:** accept a dormant seat ⇒ red.
7. **The verbs on the pipe.** `Ops.Board` = `board-narrow`, `board-propose`, `board-decide`, `board-list`, `board-show`, `board-power-propose` — named apart from
   every pipe op and every org verb, NOT in `Mutating`, in the op table, the schema and `trade board …`. The disjointness test grows a third family. Red:
   `Board_verb_names_are_disjoint_from_pipe_ops_and_org_verbs`, `No_board_verb_is_mutating_and_none_names_an_order_field`.
8. **CHANGE-001.** `board-power-propose` writes a `board_power_proposal`: a target from a closed list (composition, board budget, audit budget, connection or key,
   founding-policy shape, kernel change) and the seat's text (a claim) — a record shown to the owner (`BoardReader.PowerProposals()`), applied by nothing. The owner's
   in-process seams, two-press in seat B's UI: `TradingGateway.SetBoardBudgetShare(decimal)` (a `board_event` the readers take over the founding share) and
   `FoundBoardSeat(class, model, vendor)` / `RetireBoardSeat(id)` (rows and `retired` states; the composition rule of `Found` re-checked: ≥ 2 model classes, never
   `cli`). Red: `A_power_proposal_changes_nothing`, `Only_the_owner_changes_the_boards_composition_or_budget`.
9. **The owner's veto** (AUTH-001; R24 Q2). `TradingGateway.PreviewBoardVeto(actId)` (press one shows what returns, field by field) and `VetoBoardAct(actId)` (press
   two). An open proposal: closed `vetoed`. An applied widening: a version decided by `owner`, basis `veto`, every field the act changed set to the NARROWER of its
   value now and its `from` (by item 2's table); nothing narrower to restore is a `vetoed` event with no version. A narrowing act: refused in words — restoring it
   would widen. Never a new envelope or limit above now. Red: `A_veto_never_widens` (every field, superseding acts between), `A_veto_cancels_an_open_proposal`.
   **Mutant:** restore `from` without the narrower-of ⇒ red.
10. **Guards** (one commit). AUTH-001: no `GatewayPipeServer`/`BrokerDoor`/`TradeCli` source calls `VetoBoardAct`, `PreviewBoardVeto`, `SetBoardBudgetShare`,
    `FoundBoardSeat`, `RetireBoardSeat` or `SetSeatStateUnderTest`, and no op maps to them. ORDER-001/SIGNAL-001: a board id never `MayPlaceOrders`; no intent field
    names an order, a price, a time or a size of one. EVID-001: `BoardDirection`'s fields ⊆ the policy's (which holds no evidence setting, the existing guard).
    `Only_BoardStore_writes_board_tables` still green. **Mutant:** `MayPlaceOrders(role) => role == Operations || IsBoardId(role)` ⇒ red.

## R24 § 9 defaults that apply, decided by default
Q2 the veto narrows only (item 9). Q3 two seats for every widening (items 2, 4). Q6 composition as founded, changed only by the owner (item 8). Q7 the board's budget
a founding share changed only by him (item 8); the audit line's is `U-org-audit-b`'s. Q8 the evidence settings stay his (item 10). Q12 the chief's appointment is a
two-seat board act — this unit builds the two-seat machinery it will use; the act itself is `U-board-c`'s (below).

## Decisions this brief made that R24 does not (the orchestrator's to overrule)
State on events, not rows (code: sealed rows); rationale in `board_act`, never the research ledger (every role reads it — the blind read would leak); the confirmer
does not see the proposer's forecasts either; confirmation life 24 h, an app constant; a different vendor family required when an active seat offers one; a board
seat writes nothing but board acts; the veto is field-wise narrower-of and refuses a narrowing act; `evidence_class` stays NULL until `U-board-live`.

## NOT in scope — name the drift and stop
Live mode, activation, allocation of a version and `LiveActivated` as acts, the raise-pace guard, arming-with-two-admitted-seats, the live daily-loss gate a1 deleted
(`U-board-live`, which MUST re-add it). The board seat's turn — wakes, admission on its budget, homes, packets — and the puppet lives (`U-board-b2`). The watcher's
detectors and invisibility to board seats (`U-watcher-board`). Ventures (`U-ventures-a`). Any UI (seat B's `U-org-surface-b`). **A card `U-board-c`** (seat A, before
any product path activates a board seat): seat choices, autonomy grants, structural vetoes, the chief's appointment, quarantine and its lift, `operations`' seat,
the loss-hold release and "Reopen after review" as board acts. **A card `U-board-activate`**: the product writer of a board seat's `active`, after a RECORDED proven cell.

## The seams
**To `U-org-surface-b` (seat B):** `BoardReader.Acts()`/`Act(id)` (every act with its events, deciders, families, forecasts — rationale shown to the owner always),
`PowerProposals()`, `PreviewBoardVeto`/`VetoBoardAct` (two presses), `SetBoardBudgetShare`, `FoundBoardSeat`/`RetireBoardSeat`, their `Labels.Board` words. Push the
reader and seam SHAPES first so B can build. **To `U-board-live`:** `BoardIntent` gains an allocation kind and `evidence_class`; the mode/activation CHECKs relax in
its rung; it uses items 4 and 9 unchanged.

## Protections, and how each is kept
**AUTH-001** — the veto, composition and budget are in-process seams no op/verb reaches (guard). **AUTH-002** — every policy version after founding is a board act
by active seats through their own verbs, or the owner's veto. **CHANGE-001** — power is proposable, never writable from a board seat. **ORDER-001/SIGNAL-001** — no
board op is mutating, no intent names an order, board ids never place one. **EVID-001** — no evidence field is a board field. **INBOX-001** — an act is a verb call
by a proved caller; text binds nothing; a board seat cannot write text the organisation reads. **BUDGET-001** — the plan day-total moves by acts inside his plan; his
real-money cap is untouched. **Containment** — no product path activates a seat. **Recovery** — append-only, act and effect in one write, stamp last.

Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.

## Report
**Tip** (code) `2cbd5369`, rebased on main `0ebda6d2` (schema 49 there: **rung 50 = `Versions.BoardActsRung`**, no renumber). 16 commits, one per item and per fix.
**Gate.** (1) `dotnet build -c Release --no-incremental` → "Build succeeded. 0 Warning(s) 0 Error(s)". (2) BoardActTests+BoardTests+BoardReadersTests+CouncilLoopTests ×3 → "Passed: 134, Total: 134" ×3; schema classes once → Unit "Passed: 397, Total: 397", Integration (replay-version) "Passed: 3". (3) CI **40132986531 success, 12/12 jobs** on 2cbd5369 (first run 40118241282 green on d860630b, already complete — nothing to cancel). (3b) review `fleet/tmp/A-org/board-b/U-board-b1-review.md` (5 findings, fixed below); re-review `…/U-board-b1-rereview.md` — not acted on. (4) names.sh vs main: "removed: 0", added 33, [Fact]/[Theory] base 3159 tip 3192. (5) **tests box: 20261024-231005-2cbd5369 GREEN** on 2cbd5369 — Unit 2369 passed/0 failed (42 not run), Integration 748/0 (63 not run), Fault 502/502, timing 20+91+6 passed, 0 failed (also GREEN on d860630b, run 20261024-195447).
**Items.** 1 done (rung: 6 tables, board_policy/board_event rebuilt byte for byte in one tx, FK-checked, seals recreated; the copy is `BoardStore.CopyForActsRung` so the one-writer guard holds). 2 done (`BoardIntent`/`BoardDirection`, one table; zero read as each field's enforcing code reads it: notional/loss caps and valuation exit 0 = unenforced = widest, review/head-ceiling 0 = none = NARROWEST, allowance ≤0 = default). 3 done. 4 done (a refusal closes nothing; one decision per seat by unique index). 5 done. 6 done (board seat allowlisted to its verbs + named reads; every other op refused before read). 7 done (`trade board narrow|propose|decide|list|show|power-propose`; decide's arg is `answer` — `DecisionPortTests` forbids "decision"). 8 done (+`BoardReader.PowerProposals/BudgetShare`, `Labels` words for seat B). 9 done. 10 done. Plus the observable end to end over the pipe.
**RED-before → mutant (each quoted in the run).** 1 rerun/49→50 tests red (no tables) · mode CHECK removed → live-mode test red. 2 no-direction stand-in red · unlisted→narrowing red. 3 no-Narrow stand-in red · narrowing check skipped → raise passed, red. 4 no-Propose stand-in red · rationale to unsealed seat → blind test red. 5 no-following stand-in red · founding-only grant → widening test red. 6 verbs+gate removed red · dormant accepted → "board-list was served to a dormant seat". 7 CLI absent red · `price` arg → red. 8 verb+seams absent red · two-class rule dropped from retire → red. 9 no-veto stand-in red · `from` without narrower-of → red. 10 pipe naming VetoBoardAct / evidence field red · `MayPlaceOrders ‖ IsBoardId` → "board-1 may place orders".
**Fix pass (review).** P1 refresh order: older read never replaces newer (lock + version) — red "Expected 20, Actual 30" · mutant comparison removed red. P1 envelope terms re-read inside the grant's write — red (v2 granted after v3 removed it) · mutant pre-write terms red. P1 narrowing never lengthens life (capped at the replaced envelope's expiry unless the terms widen) — red "ran the envelope to 11-12, past 11-03" · mutant cap off red. P2 loop reads review interval/ceilings live (`MissionOptions.LiveReviewEvery/LiveCeilings`, `AppHost.LoopOptions`) — red "Expected 01:30, Actual 01:00" and "(00:30,1.00:00)" · mutant no live sources red. P2 same-family refusal names the active other-family seat — red · mutant words-by-decision red.
**Decisions (orchestrator's to overrule).** A veto version grants NO envelope (it only takes away); an envelope is granted once per decision of its TERMS (the version that set them), so the owner's withdrawal survives unrelated acts (Q5); `paper_envelope.*` sub-paths are refused in intents (the envelope changes whole); a seat sees an act's rationales only once it proposed or decided THAT act (closed acts included); state rows for `active` written by `SetSeatStateUnderTest` alone; `TradingGateway` made partial (`TradingGateway.Board.cs`). Existing test edits: `BoardTests` live-mode test re-run over a valid board/act row (+ control insert); armed-home sentence asserts this build's schema, not the const 49; `GrantFoundingEnvelopeIfDue`→`GrantPolicyEnvelopeIfDue` call rename.
**NOT done / NOT verified.** No b2 (no real seat turn, no door exposure of board verbs); no UI; nothing activates a seat in the product. The full local suite was not run (CI and the tests box ran it). Head-ceiling liveness is proven at the options level, not by a ceiling wake in a running loop.
**Fix pass 2 (re-review).** Loop: a review interval or level ceiling narrowed to none withdraws the looks and floors already pending before the pick (only where the interval is live — a fixed none raised none; the static `NoHeartbeat` test options keep their behaviour) — red "Assert.Empty() Failure: Collection was not empty" (both launched) · mutant ceiling withdrawal removed red. Envelope life read from the act that SET the terms (terms at `setBy−1` vs `setBy`), so a two-seat widening after a narrowing gets its full life — red "Expected (0.02, 11-12) Actual (0.02, 11-03)" · mutant prior terms read from `setBy` red.
**Gate 2.** Tip `d96f4b5c` on main `bfb0f359` (schema 49; rung 50 unchanged). Build 0 warnings/0 errors. BoardActTests+BoardTests+BoardReadersTests+CouncilLoopTests ×3 under `TZ=UTC` → "Passed: 136, Total: 136" ×3. Locally in CEST on 2026-10-25 (the day DST ends) the pre-existing `CouncilLoopTests.Noon()` throws "The UTC Offset of the local dateTime parameter does not match" — 38 failures, every one that message, none in this diff. names.sh: "removed: 0", added 35 (base 3159, tip 3194).
**CI 40137472570: 11/12 green; linux-host red** — "stop ran on tomorrow's word": the workflow's `TZ=Europe/Brussels date -d tomorrow` ran at 22:46 UTC, 00:46 on Brussels' 25-hour DST-end day, where 24 h later is still 2026-10-25. This diff touches no `tools/` or `.github/`; the same job was green on 40132986531 → the manager's to judge. Re-review 2: `fleet/tmp/A-org/board-b/U-board-b1-rereview2.md`, not acted on. Tests box not re-run this pass.
