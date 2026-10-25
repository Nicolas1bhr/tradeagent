# U-org-surface-b — the owner sees what reached the root (escalations nobody below could take, his words still owed and why), every board act whole, and vetoes a board act or a structural act in two presses that only ever take away; ventures show a named empty state
**Arrow closed:** R24 (`docs/research/2026-10-23/R24-security-board.md`) § 7 seat B item 4 (:324), § 4.7 (:224-245: "the owner's inbox: escalations at the root, owner
words owed"; "his inbox and his veto"; each decision "with its history, rationale, deciders, evidence class and score"), § 4.3 (:166-183: the veto narrows, two presses,
"Nothing waits on him"; composition and budget his), § 4.4 (power proposals shown to him), § 4.6 (ventures); ORCHESTRATION § 2.9 (:340-341: at the root an escalation
"reaches the owner as a record only — no wake, no pipe path; his surface shows it"), § 2.5 (:206-207: owed words shown "undelivered with the reason"); ORGANISATION
§ 15 row O7 (:430). Pointers re-grepped at main `3dacac76`, 2026-10-24 21:3x.
**Protections — AUTH-001 (every press here is in-process: no pipe op, door verb, `trade` verb or board act reaches the veto, the structural veto, composition or budget),
two-press (each veto, each composition/budget press; the owner's 10-22 rule: no value changes by scrolling), R24 Q2 (a veto never widens: the UI offers none where the
store would refuse), CHANGE-001, INBOX-001 (escalation reasons, rationales, power proposals and his words are CLAIMS, printed in quotes after their writer — `OrgText`),
EVID-001 (evidence class and score are the app's; a null is shown as "none yet", never inferred), honesty, built-once: red-first and ONE watched mutant.** Seat B;
**opus/high**. **No rung** (reads and presses over b1's and verbs-d's tables).
**Depends:** seat A's **U-board-b1 LANDED** to land (it carries a rung and the tables). Its SHAPES ARE PUSHED — `origin/u-board-b1` `d860630b` carries all ten items:
`BoardReader.Acts()/Act(id)/PowerProposals()` (`Core/Board/BoardReader.cs:53-59`), `BoardActRecord`/`BoardActEventRow`/`BoardVetoPreview` (`Core/Board/BoardActs.cs:90-145`),
`TradingGateway.PreviewBoardVeto/VetoBoardAct/SetBoardBudgetShare/FoundBoardSeat/RetireBoardSeat` (`Gateway/TradingGateway.Board.cs:101-146`), `Labels.Board` words
(`Core/Labels.Board.cs:174-187`) — so the builder may START stacked on it and rebase at its landing. Item 4's structural half needs **U-org-verbs-d LANDED**
(`OrgStore.Veto(eventId, now)`, `origin/u-org-verbs-d` `37e4f48c`, `Core/Db/OrgStore.Restructure.cs:269`; its Codex fix pass is open): build it last.
**Today (at 3dacac76):** the rail and pages `App/MainWindow.cs:39, :52-66, :503-519`; `Inbox` is the material hand-over (`App/InboxView.cs:29`), not his inbox. The Board
page reads only and says the veto "is his own page's" (`App/BoardView.cs:13-15`); it shows policy VERSIONS with deciders/class/score (`:129-155`), no act. Escalations
to the owner are written (`org_escalation.to_position NULL`, `Core/Db/Database.cs:2612-2614`) and read by `OrgStore.EscalationsToOwner()` (`Core/Db/OrgStore.Surface.cs:164-185`)
— which NOTHING calls. Owed words: `MissionEventStore.OwnerWordsOpen(role)` per role only (`Core/Db/MissionEventStore.cs:1155`), the reason in `disposition_detail` by
`BlockOwnerWords` (`:1135`); no all-roles reader, no page. Org pages' sources `App/OrgSurface.cs:19-50`, keyed in-place cards `LiveList` (`:132`). Two-press `Ui.Confirm`
(`App/Ui.cs:226, :234`); `ConfirmIf` is ONE press when its sentence is null (`:358`). Owner page `App/OwnerView.cs:12-30`; its allowlist test `OwnerPageTests.cs:43`;
no-press tests `BoardPageTests.cs:101`, `OrgSurfaceShellTests.cs:68`. Ventures: no data on main (`git grep -i venture src` = one comment, `Core/Trading.cs:20`).
**Observable result:** with a puppet escalation climbed to the root and a word blocked by a refused launch, the owner's new page shows both (the reason quoted as the
sender's, the block reason as the app's); the Board page lists a confirmed raise with its proposer, confirmer, families, both rationales, evidence class "none yet"
and its forecasts' resolutions; on his page press one of "Veto this act" shows each field's now → restored, press two writes a version decided by `owner`; a narrowing
act shows no veto; a closed team's structural veto restores it DORMANT in two presses; ventures read "No venture yet"; no tick disarms a half-pressed veto.
Items, one commit each, one-sentence messages:
1. **Readers** (Core, read-only): `MissionEventStore.OwnerWordsOwed()` — every role's open owner words with addressee, received-at and block reason; `OrgStore.Vetoable()`
   — chartered/closed/retired/hired events decided by a position and not vetoed, merges excluded, each with what the veto would do in app words. Check:
   `Owed_words_across_roles_carry_their_block_reason`, `Vetoable_lists_exactly_what_Veto_accepts` (each listed one vetoes; each refusal kind is absent).
2. **The owner's inbox page** (`OwnerInboxView.cs`, a rail entry after Owner; label Q1): escalations at the root (sender, unit, mandate, skipped heads and why), owed words,
   power proposals ("applied by nothing"), each a `LiveList`; it raises no wake and holds no answer press. Check: `Escalations_at_the_root_and_owed_words_show_on_the_owners_inbox`.
3. **The board's acts on the Board page**, read-only: per act its status, intent, events (deciders, families, `same_family` said), every rationale as a claim, evidence
   class, forecasts and resolutions as the score (an audit score is `U-org-audit-b`'s: "not scored yet"). Check: `Every_board_act_shows_history_rationale_deciders_class_and_score`;
   `The_board_page_holds_no_press_that_writes` stays green.
4. **The veto, two presses, on the inbox page:** board acts whose `PreviewBoardVeto` allows (press one shows its fields, press two `Gateway.VetoBoardAct`); structural events
   from item 1 through a new in-process `AppHost.VetoStructure(eventId)` (Activity line, `StateChanged`). `Ui.Confirm` always — never `ConfirmIf`. Check:
   `A_veto_takes_two_presses_and_the_first_writes_nothing`, `A_narrowing_act_offers_no_veto`, `The_structural_veto_restores_dormant_in_two_presses`.
5. **Composition and budget, the owner's** (Owner page; allowlist +3): budget share in an `AmountField`, found (closed-list choices from `BoardSeats.SeatClasses`/`BoardModelClass.All`),
   retire — each two presses, the store's refusal shown verbatim. Check: `The_boards_composition_and_budget_take_two_presses_and_refuse_in_words`.
6. **Ventures:** an Organisation-page section with `Labels.VenturesNone` ("No venture yet — the board approves them once they exist"). Check: `Ventures_show_a_named_empty_state`.
7. **Guards + in place:** no `GatewayPipeServer`/`BrokerDoor`/`TradeCli`/tool source names `VetoStructure`, `OwnerWordsOwed` or `Vetoable`; the new page's presses are exactly
   the veto labels; a tick keeps every card and an armed press. Check: `No_op_or_verb_reaches_the_owners_inbox_presses`, `The_owners_inbox_is_built_once_and_updated_in_place`.
Red-first (quote each red at base): items 1–7's checks. **The watched mutant:** item 4's press built with `Ui.ConfirmIf(label, () => null, veto)` (one press) ⇒ `A_veto_takes_two_presses_and_the_first_writes_nothing` red. Quote, restore, green.
**Read first:** CLAUDE.md; R24 §§ 4.2–4.7, 9; ORCHESTRATION §§ 2.5, 2.9, 2.10; `docs/briefs/U-board-b1.md` (seams, item 9); `U-org-verbs-d.md` item 4; `BoardView.cs`, `OrgSurface.cs`, `OwnerView.cs:12-30`.
**NOT in scope:** answering an escalation by a press (his words go by chat); any board verb, act or seat activation; the chief; a ventures store (`U-ventures-a`); an audit
score; renaming the material Inbox; the watcher's page. **Open (seat B):** Q1 the page's label (default "For you"); Q2 build item 4's structural half before verbs-d lands on a local merge, or after.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line; a mac-shot of the inbox page). `## Report` ≤ 20 lines appended here.
**RULINGS (the orchestrator, 2026-10-24 ~22:4x; the owner's to overrule) — they OVERRIDE anything above that differs:** the new page is named "For you". It ALSO carries the owner's
two-press **Activate** for non-chief positions (an M-org1 gap): live only while no board seat is active, labelled "yours until the security board is active"; afterwards a two-seat
board act (it widens) — the press refuses in words naming the board; the code's activation gate refuses any real-model seat before a RECORDED proven cell, whatever the press
(a test). The structural-veto half is built after U-org-verbs-d lands (stack on it or rebase). Check: `Activate_is_two_presses_and_refused_once_a_board_seat_is_active`.

**SEAM RULING (the orchestrator, 2026-10-25 05:31, from seat A's U-board-b1 seam fix; binds this unit):** "a board seat is active" is decided ONLY through `BoardReader.AnyActive` (BoardStore reads it through its single `Seats()` reader) — never a query of this unit's own; every test makes a seat active through U-board-b1's REAL path (its acts), never a hand-built table or a rung-shaped fake (verbs-d's old rung-49 fake went stale under rung 51). b1's seam fix found no authority bug. U-board-b1 (rung 51), U-repair-position-home and U-role-follows-setup LANDED before this dispatch; U-org-chief (the Org root card) may land while you work — rebase before CI — re-grep every pointer on main.
**FILE SEAM (seat B, 2026-10-25):** U-org-surface-b owns the "For you"/owner inbox page, BoardView.cs and OwnerView.cs (after U-role-follows-setup lands); U-org-chief owns the Org page root card (OrganisationView.cs). Neither edits the other's view. If both add lines to OrgSurface.cs, OrgStore public members or OrgLedgerTests' list, the second to land rebases onto the first (one committed rebase), keeping both.

## Report
Tip 790db439 (code; rebased on main f4dfcc1b — U-org-chief and U-board-b2 — one conflict, OrgLedgerTests' mandate list, kept both "Chief…" and "Vetoable"). First builder: items 1–4, 6, 7-guard and Activate; continuation builder: item 5, the rebase, the gate.
Gate 1: Release `--no-incremental` → `0 Warning(s)` `0 Error(s)` (an xUnit2017 warning in the first builder's in-place test fixed, ad953197).
Gate 2: OrgLedger, OrgStructure, OwnerInbox, DashboardShed, BoardPage, OwnerPage, NumberEntryCulture, OrgSurfaceShell, OrgChief ×3, TZ=UTC → `Passed: 143` ×3.
Gate 3: CI run 40187850836 `success` — ubuntu, macos, windows 1–5, linux-host, package, package-linux all green. Codex review: fleet/tmp/B-scratch/u-org-surface-b/U-org-surface-b-review.md (2× P2, 1× P3; not acted on — the manager triages).
Gate 4: names.sh main → `removed: 0` `added: 12`, [Fact]/[Theory] base 3328, tip 3340.
1 readers — done (first builder). 2 For you page — done (first). 3 board acts on the Board page — done (first).
4 vetoes, board act and structural (verbs-d's `OrgStore.Veto` on main), two presses — done (first); Activate ruling — done (first), board-active asked only of `BoardReader.AnyActive`, seats made active through b1's own `SetSeatStateUnderTest` seam.
5 composition and budget on the Owner page (share, found, retire; `ChoiceBox` no scroll moves) — done (mine, from the first builder's uncommitted draft, kept whole), 5f2d71d6.
6 ventures — DEVIATION: on the Board page, not the Organisation page (FILE SEAM: OrganisationView.cs is U-org-chief's) (first).
7 guard + in place — done (first; `No_op_or_verb_reaches_the_owners_inbox_presses`, `The_owners_inbox_is_built_once_and_updated_in_place`).
RED item 5 at base: `OwnerPageTests.cs(104,18): error CS0246: The type or namespace name 'BoardComposition' could not be found`; mutant Found via `ConfirmIf(label, () => null, …)` ⇒ composition check `Expected: 3 Actual: 4` (a seat founded on press one), restored green.
WATCHED MUTANT (re-watched after the rebase): the board-act veto built with `Ui.ConfirmIf(Labels.VetoBoardActPress, () => null, …)` ⇒ `A_veto_takes_two_presses_and_the_first_writes_nothing` FAILED `Expected: Tuple (2, "board", …) Actual: Tuple (3, "owner", …)`; restored → `Passed: 1`.
Tests box: run 20261025-121223-790db439 RED, 2 of 2597 Unit, neither file in this diff, both green on CI windows: `CouncilLoopTests.An_idle_day_costs_only_the_looks…` (`LocalMidnight` DST-day offset, CouncilLoopWakeTests.cs:79) and `ModelCatalogTests.The_seat_home_gets_the_catalog…` (`could not open …model-catalog.json.….tmp to publish it`: path not found under the run's long tmp path); Fault 502/502, Integration 749 + 65 not run, timing green; his files unchanged.
NOT DONE: the mac-shot of the For you page (no screen grant in this leg). NOT VERIFIED: the first builder's RED-before quotes for items 1–4, 6, 7 (its tests carry "RED at base" notes; none is quoted here); the two tests-box reds as box-only (not re-run there).
