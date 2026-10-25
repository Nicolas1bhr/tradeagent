# U-org-chief — the root gets a head: the chief is appointed and replaced by a two-seat board act on U-board-b1's machinery (the owner's in-process press only while no board seat is active), the owner can always take it back, and the chief charters divisions and receives escalations without ever reaching `operations`, the board or an order
**Arrow closed:** ORGANISATION § 15 :431 (the row, R24-re-scoped), § 2 :99, § 3 :129-131, :545 ("the chief comes last before M-org1"); R24 § 4.1 :108, § 4.3, § 7 B5 :325, § 9 Q12
:358-360 ("the board appoints by a two-seat act; his narrowing veto removes a chief, and he may take the appointment back"); ORCHESTRATION §§ 2.1, 2.3, 2.9 :325-327, 2.10; VISION § 8
("a board, not an operator"); the generalised a2 ruling (`fleet/tmp/A-org/board-a/a2-rulings.md`:2-5: an act with no other decider that touches only plan capacity stays the
owner's in-process press, "yours until the security board is active", while `Board.AnyActive` is false). Pointers at main `3dacac76`.
**Protections — AUTH-001 (his appointment, removal and veto are in-process only: no op, verb, packet or board act reaches them), AUTH-002 (once a board seat is active the chief
moves only by a recorded `propose` + a DIFFERENT seat's `confirm` bound to the intent's hash), CHANGE-001 (the chief holds no board verb, touches no board table), ORDER-001 (never
`MayPlaceOrders`; nothing about `div-operations`), ORG-001 (heading the root grants no seat, activation, envelope raise or root tilt), containment (hired DORMANT; activated only through
the gate, never on a model seat before a RECORDED proven cell; puppets in tests only; no real model call), refuse never guess: red-first and ONE watched mutant.** Seat B;
**opus/high**. **No rung** (`org_event.kind`/`decider` are unchecked text; the chief act is a b1 `propose` act whose event carries no policy version).
**Depends:** seat A's **U-board-b1 LANDED** (shapes pushed: `origin/u-board-b1` `d860630b`); **U-org-verbs-d LANDED** (`origin/u-org-verbs-d` `e960d194`: `Restructure` core
`OrgStore.Restructure.cs:68`, `Veto` :283, `Activate` :390 refusing while `AnyActive` :392); O7 `U-org-surface-a` landed (`a82688e4`). O8 is § 15's order, not a code dependency.
**Today:** the root `org` is seeded with head NULL, "NULL here means THE OWNER" (`OrgStore.cs:14-20`, `Database.cs:3032`); the matrix answers appoint/retire/replace of the chief
`BoardAct` (`OrgRights.cs:150-155`, `:188-194`), refusing as if the board's verbs "are not in this build" (`:225-227`); `Hire` as head fills a vacant unit DORMANT (`OrgStore.Verbs.cs:221-290`),
divisions cap at 6 (`:127`), `SetSeat` is in-process (`OrgStore.Seats.cs:22`); the escalation walk stops at the root as the owner (`OrgStore.Decisions.cs:594`), as do the pipe's chain
and the Org page (`GatewayPipeServer.Mandates.cs:273`, `OrganisationView.cs:73`); `MayPlaceOrders` (`Council.cs:67`). b1 has NO chief intent — `narrow`|`propose` over policy fields only
(`BoardActs.cs:6-13`, `BoardIntent.cs:60, :217`, `BoardStore.cs:340, :481, :662`), the chief left to card `U-board-c` (`U-board-b1.md`:99, :111): THIS UNIT adds it on b1's machinery, never a parallel one.
**Observable result:** no board seat active: the owner's two presses appoint a puppet chief (dormant, seated), his Activate press activates it; it charters a division and appoints
its head (`org-charter`/`org-hire`); that head's escalation reaches the chief, the chief's reaches the owner as a record. Two puppet board seats set active under test: seat 1 proposes
a replacement, seat 2 confirms by hash — old chief fenced, mandates cancelled, new one DORMANT; his appointment press is gone; his veto of the act leaves the root vacant. One seat's
`board-narrow` of the chief, the chief's `board-*` call, its charter under `div-operations`, its order, a model-seat chief's activation without a recorded cell: refused in words, nothing written.
Items, one commit each, one-sentence messages:
1. **The one chart writer** (`OrgStore.SeatChief`, internal): appoint (root vacant), replace (the sitting chief retired through verbs-d's `Restructure` in the SAME write: mandates
   cancelled, fenced, no past charge moved), vacate; the new position minted DORMANT under the root, seated by `SetSeat`'s checks, root head set; one `chief-appointed|-replaced|
   -vacated` event, decider `owner` or `board` with the act id in app-composed detail. Check: `Replacing_the_chief_fences_the_old_one_and_hires_the_new_one_dormant_in_one_write`.
2. **The owner's route, in-process:** `AppointChief`/`ReplaceChief(runtime, model, …)` ONLY while `Board.AnyActive` is false (refused in words naming the board after);
   `VacateChief` ALWAYS — his narrowing veto, which restores no predecessor. Check: `The_owner_appoints_only_while_no_board_seat_is_active_and_removes_at_any_time`.
3. **The board's route, on b1:** `BoardIntent` accepts ONE `org.chief` change `{from: sitting chief id | null, to: {runtime, model} | null}`, never mixed with policy fields;
   `BoardDirection.Of` answers EVERY `org.chief` change `Widens` (two seats, vacating included); `Moved` reads the chart (a moved chief closes the act `stale`); `Decide`'s confirm
   applies through item 1 in the decision's write and writes no policy version; b1's EVID guard reads fields ⊆ the policy's ∪ {`org.chief`}. Check:
   `One_board_seat_never_appoints_or_replaces_the_chief`; `A_chief_act_binds_to_its_hash_and_a_moved_chief_closes_it_stale`.
4. **The veto of a chief act** (`PreviewBoardVeto`/`VetoBoardAct`): an applied act whose `to` sits now → item 1 vacates (decider `owner`, basis `veto`); otherwise a `vetoed`
   event with nothing to restore; an open one closes `vetoed`. Check: `The_owners_veto_removes_a_board_appointed_chief_and_restores_no_one`.
5. **The chief at the root:** the walk delivers to an ACTIVE root head whose recorded seat carries decision verbs, and from the chief to the owner's record; the pipe's chain,
   the Org page and `OrgIds.Root`'s doc name the chief or "vacant"; the matrix's board-act refusal words now name the board's verbs. Checks:
   `An_escalation_reaches_an_active_chief_before_the_owner`; `The_chief_charters_divisions_and_never_reaches_operations_the_board_or_an_order` (charter ≤ 6, appoint a vacant
   division's head; refused: `div-operations`, replacing a division head, root tilt/close, every `board-*` op, an order).
6. **Containment:** a chief activates only through `OrgStore.Activate`'s gate; a board-appointed chief stays DORMANT (no product writer activates it after `AnyActive`: `U-board-c`).
   Check: `A_chief_on_a_model_seat_stays_dormant_without_a_recorded_proven_cell` (puppet exempt; a removed proof refuses again).
7. **His presses on the Org page's root card** (two each, `Ui.Confirm`, built once and updated in place; closed-list runtime/model choices that no scroll changes): "Appoint a
   chief"/"Replace the chief" and "Activate" while no board seat is active — labelled "yours until the security board is active" — and "Remove the chief" always; NO veto press
   (`U-org-surface-b`'s). Checks: `Appointing_the_chief_takes_two_presses_and_the_first_writes_nothing`; `The_appointment_presses_are_gone_once_a_board_seat_is_active`.
8. **Guards:** no `GatewayPipeServer`/`BrokerDoor`/`TradeCli`/tool source names `SeatChief`, `AppointChief`, `ReplaceChief`, `VacateChief`; `SeatChief`'s callers are items 2–4
   alone; the chief's id never `MayPlaceOrders`. Check: `No_op_or_verb_reaches_the_chiefs_appointment` (red with an op added).
Red-first (quote each red at base): items 1–8's checks. **The watched mutant:** `BoardDirection.Of` answers `org.chief` → `Narrows` ⇒ one seat's `board-narrow` appoints a chief ⇒ `One_board_seat_never_appoints_or_replaces_the_chief` red. Quote, restore, green.
**Read first:** CLAUDE.md; R24 §§ 4.1, 4.3, 9 Q12; the a2 rulings; ORGANISATION §§ 1–3, 8; ORCHESTRATION §§ 2.1, 2.3, 2.9, 2.10; the b1 and verbs-d briefs and branches; `OrgRights.cs`.
**NOT in scope:** the liaison seat and the owner's words to the chief (§ 2.10 relay, two-hour fallback) — CUT to card `U-org-liaison`: it decides nothing (§ 2 :104), M-org1 is "its team
head and executors on confined seats" (§ 15 :508), Chat stays the chair's; chief proposals to the board; tilt; staff; board activation of a chief (`U-board-c`); veto UI (`U-org-surface-b`); O8.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
**RULINGS (the orchestrator, 2026-10-24 ~22:4x, BOARD Decisions; the owner's to overrule) — they OVERRIDE anything above that differs:**
Q1 while no board seat is ACTIVE the OWNER appoints, replaces and activates the chief by in-process press, labelled "yours until the security board is active" — two presses each
(appoint/activate, remove); the activation gate in code holds regardless (no real-model seat before a RECORDED proven cell; no press passes it); paper only, live closed by construction;
once a board seat is active a chief change is a two-seat board act under his narrowing veto. Q2 with the board active EVERY chief change — appoint, replace AND remove — takes two
seats; only the owner removes alone (veto or remove press). Q3 the liaison seat is CUT to card U-org-liaison, BUT his words are never cut off from the head: until U-org-liaison, his
words route VERBATIM, stamped `owner`, to the chief exactly as they reach the chair today (ORCHESTRATION § 2) — if that is more than a routing change, STOP and tell seat B before building it.
Q4 the owner's two-press Activate for NON-chief positions is U-org-surface-b's ("For you" page), not this unit's.

**SEAM RULING (the orchestrator, 2026-10-25 05:31, from seat A's U-board-b1 seam fix; binds this unit):** "a board seat is active" is decided ONLY through `BoardReader.AnyActive` (BoardStore reads it through its single `Seats()` reader) — never a query of this unit's own; every test makes a seat active through U-board-b1's REAL path (its acts), never a hand-built table or a rung-shaped fake (verbs-d's old rung-49 fake went stale under rung 51). b1's seam fix found no authority bug. U-board-b1 LANDED as rung 51 before this dispatch — re-grep every pointer on main.
**FILE SEAM (seat B, 2026-10-25):** U-org-surface-b owns the "For you"/owner inbox page, BoardView.cs and OwnerView.cs (after U-role-follows-setup lands); U-org-chief owns the Org page root card (OrganisationView.cs). Neither edits the other's view. If both add lines to OrgSurface.cs, OrgStore public members or OrgLedgerTests' list, the second to land rebases onto the first (one committed rebase), keeping both.
