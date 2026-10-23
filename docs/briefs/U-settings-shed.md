# U-settings-shed — Safety and Settings shed every board decision and the owner's live presses; a read-only Board page shows each decision in force; the owner's surface is R24 § 4.3's
**Arrow closed:** `docs/research/2026-10-23/R24-security-board.md` § 4.7 (the owner's surface; "everything else becomes the board's decisions … shown as what is in force now — a
founding value or a board act — with its history, rationale, deciders, evidence class and score"), § 7 seat B item 1 (in `U-board-a`'s train, "so no build shows a box that no
longer governs"), § 4.3 (what the owner keeps), § 4.5 (founding values are the app's; a home in live is refused). The owner, 2026-10-23: "way too much decision making in the
settings"; "the security should come from the security board not programmatic immutable parameters in the ui". Survey: `fleet/tmp/B-scratch/settings-shed/survey.md` — re-grep every pointer.
**Protections — AUTH-001 (what stays is in-process; the Board page reads only; no pipe op, no CLI verb), two-press (every press that stays keeps its count: stop one, resume
two; cancel all, close all, ATAS, real-money account, install, holdout, sign-out two; real-money cap, perception budget and a lower AI price two on the loosening side), EVID-001
(price, friction, holdout stay the owner's; no evidence key is a board decision), BUDGET-001 (the real-money cap stays his), honesty (a founding value is labelled the app's,
never his), refuse never guess (a key with no label refuses; a home in live is refused with an in-app repair), the tree built once, `Theme.cs`: red-first and ONE watched mutant.**
Seat B; **opus/high** — it removes the owner's live presses and moves two-press controls (operator authority). No rung (the rung is `U-board-a`'s).
**Depends:** `U-board-a1` (seat A; rung 47 is A's) — THE SEAM IS SEAT A'S, adopted by seat B 2026-10-23 and pushed as a1's FIRST commit `beb1bb2d` on branch
`u-board-a1` (namespace `TradeAgent.Core.Board`): `AppHost.Board` → `BoardReader` with `InForce()` → `BoardPolicyInForce(Version, Decider, Basis, WrittenAt, BodySha256,
Policy, Fields[BoardPolicyField(Name, Value, SetByVersion)])` with `CouldNotBeRead`/`Why`; `History()` → `BoardPolicyVersion(Version, Decider, Basis, WrittenAt,
BodySha256, Rationale?, Deciders, EvidenceClass?, Score?)`; `Seats()` → `BoardSeat(Id, SeatClass, ModelClass, VendorFamily, Status, FoundedAt)`; `AnyActive` (false
in a1). Field names are the body's snake_case paths (`mode`, `live_activated`, `risk.max_order_quantity`, `plan_day_total`, `role_shares`, `rewake_floor_minutes`,
`paper_envelope`, …). Gateway: `TradingGateway.EarlierLiveSetting` → `EarlierLiveSetting?(Mode?, LiveActivated?, HomeSchema, Sentence)` and
`SetAsideEarlierLiveSetting()` (ONE press — a narrowing), its label spelled once as `Labels.SetAsideEarlierLiveSetting`, the refusal code
`ErrorCode.HOME_ARMED_BEFORE_THE_BOARD`. Still coming in a1 (compile-relevant): `TradeAgentSettings.Mode`/`LiveActivated` removed (read `gateway.Policy` or
`GatewayStatus`), gateway `SetMode`/`ActivateLive` deleted, the Safety page's mode card (`BuildModeRow`, `_liveButton`) deleted; later a1 commits only ADD members.
This branch starts on a1's head and lands in ONE TRAIN after it (U-board-a1 first); the survey's § Seam is superseded.
**Seat B's rulings:** the owner keeps every press that only TAKES AWAY (R24 § 4.3: the kill-switch family and the narrowing veto) and loses every press that grants or widens —
so "Stop this deployment" AND envelope withdraw stay his (Q3), the envelope grant goes; "Reopen after review" (PAPER scopes only) stays his while no board seat is ACTIVE and becomes the
board's from the first active board seat — a runtime condition, not a landing; its label reads "yours until the security board is active, then the board's", and it is offered only while `AppHost.Board.AnyActive` is false (Q2, the
orchestrator's ruling of 2026-10-23 on the owner's behalf, his to overrule: board seats wait for a recorded proven cell, and a held paper scope must not stall paper runs; every
widening of a live limit or of real money stays the board's, and he gets no live press back — record this in the unit's record); market data stays on Settings (Q4: a connection, not a limit); the plan's window use stays a reading (Q5); campaign budgets shown read-only
(Q6: no new owner control, ruling 2); the Safety page becomes **"Owner"** and Settings keeps preferences + market data (Q8). Read "You" below as "Owner".
**Today:** `SafetyPage` `DashboardView.cs:1037` (ctor `:1748`, layout `:2167-2175`) holds the mode row `:1752`, live activation `:1754`, nine limits + allowlist `:1792-1802`,
allocation `:2039`, envelope `:2076-2079`, plan cap `:1867`, allowance `:1886`, review `:1907`, lanes `:1925`, model `:1730`, research runtime/model/share `:2328, :2497, :1976`
beside the emergency column `:1768-1787`, the real-money cap `:1857`, keys `:1834`, price `:1985`, perception `PerceptionCard.cs`; 26 `AmountField`s. Dashboard approvals
`:579-581`. Settings `SettingsView.cs:146`. Rail `MainWindow.cs:409-425`, pages `:479-503`. No page reads a policy; no in-app path for a refused home exists.
**Observable result:** on a fresh home the rail reads Chat · Dashboard · Board · Daily report · Inbox · You · Settings · Activity · Checks · Oversight; Board lists every
decision in force as "founding (the app's), since …" with an empty history; You holds exactly the emergency family, connections and keys, the real-money cap with perception's
budget, the evidence card and updates; no screen offers a mode, a limit, an allocation, an envelope grant, a lane, a model, a live switch or a per-order approval; a home whose
settings hold a live mode opens on A's refusal sentence naming both versions, and one press sets the earlier live setting aside, never converting or deleting it.
Items, one commit each, one-sentence messages:
1. **The Board page** (`BoardPage`, read-only, `Page.Board` after Dashboard): the policy in force from `BoardReader.InForce()` — its version, decider ("the app" for the
   founding record, never the owner), basis and since — and one row per field, value and the version that set it, built once, `Update` writing in place; `History()` (each
   version's rationale as a claim, deciders, evidence class, score or "not yet scored") built on expand, never on the tick; the board's seats from `Seats()` (dormant, in
   words); labels in `Labels`. Checks: `Every_policy_key_has_a_plain_label`, `A_founding_value_is_labelled_the_apps_never_the_owners`, `The_board_page_holds_no_press_that_writes`,
   `The_board_page_is_built_once_and_updated_in_place`.
2. **Safety → You:** delete the BOARD controls and confirm both live presses are gone (U-board-a's item 7 deletes the mode row and the real-money switch), with their factories and handlers (`BuildSaveLimits`, `BuildSavePlanCapacity`, `BuildSaveLanes`,
   `BuildSaveReviewEvery`, `BuildSaveTurnAllowance`, `BuildSaveResearchShare`, `BuildAllocateConfirm`, `BuildEnvelopeConfirm`, `BuildEnvelopeWithdrawConfirm`, the model and runtime
   rows); keep the emergency column, the real-money cap, keys, perception, the AI price, the release card, stop-deployment; today's plan use stays a reading, and its midnight sentence promises recovery only when a fresh day can fund one turn, else it names the board's ceiling or a cheaper model
   (U-billing-classes' Codex P2, `DashboardView.cs` ~1414). Checks:
   `The_owners_page_holds_exactly_the_owners_presses` (label allowlist over the built tree), the kill-switch and emergency tests of `TwoPressGrantTests` re-pointed, `MissionCostSurfacesTests`,
   `HarnessKeyOriginTests`, `HarnessRoleTests.The_key_box_…_is_masked`, `BillingClassesTests` page lines re-pointed; `NumberEntryCultureTests` walk 26 → 4 and its six page tests
   re-targeted to boxes that stay; the removed tests named in the report (survey § 2).
3. **Evidence and connections:** an "Evidence" card on You (price; campaign trial/verdict/exploration budgets and judge capital read-only); harness and perception keys under
   "Connections" with platform, account and bridge. Checks: `No_evidence_setting_is_a_policy_key`, `PaperFrictionTests` and `CostModelPinTests` green unchanged.
4. **Dashboard:** the LIVE_CONFIRM approval card and its "Approve"/"Decline" go (R24 § 4.2, Q9 default). Check: `The_dashboard_offers_no_per_order_approval`.
5. **The rail** in the order above; Oversight last. Check: `The_rail_lists_the_pages_in_order` (reads `_nav` via the page enum). `U-org-surface-a` (building in parallel) adds
   Organisation, Work and Costs after Chat and owns the final organisation-first order (R24 § 4.7); whichever of the two lands second merges the rail.
6. **An earlier armed home, in the app:** `TradingGateway.EarlierLiveSetting` non-null → a card with its `Sentence` (both versions and the repair, A's words) and ONE
   press labelled `Labels.SetAsideEarlierLiveSetting` that calls `SetAsideEarlierLiveSetting()` (a narrowing: one press, like stop; never converted, never deleted); no
   terminal (CLAUDE.md: the refusal ships with its in-app path). Checks: `An_earlier_armed_home_is_refused_in_the_app_and_one_press_sets_its_live_setting_aside`,
   `An_earlier_armed_home_is_left_untouched_until_the_press`.
Red-first (quote each red at base): items 1, 2, 4, 6's first checks.
**The watched mutant:** re-add "Switch real-money trading ON" to You ⇒ `The_owners_page_holds_exactly_the_owners_presses` red. Quote, restore, green.
**Read first:** CLAUDE.md (Conventions; operator authority; no backward compatibility); R24 §§ 0, 3, 4.1-4.7, 5, 9; the survey; seat A's `U-board-a` brief and the agreed seam;
`DashboardView.cs` `SafetyPage`; `SettingsView.cs`; `MainWindow.cs:39, 409-509`; `TwoPressGrantTests`, `NumberEntryCultureTests`.
**NOT in scope:** board verbs, the veto and any board press (`U-board-b`, `U-org-surface-b`); the policy record, gateway/meter/allocator readers, `GatewayHost` console, field
removal from `TradeAgentSettings` and kernel tests (`U-board-a`); the chart, work and costs pages (`U-org-surface-a`); ventures; any new owner limit control.
Removed names (the factories in item 2) are this unit's purpose: the names check lists them and the landing accepts them (CLAUDE.md, no backward compatibility).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; Codex review; names vs
`main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.

## Report
Tip `99d5ddd5` (code; this report commit on top), on U-board-a1's head `9f5c446c` (rebased; conflicts only in SafetyPage, deleted here, a1's deletions kept). Gate: Release `--no-incremental` 0 warnings, 0 errors · touched classes 3×: Unit 214/214 ×3, Fault UnreadableSettingsTests 14/14 ×3 · CI run 39891265229: 9 jobs green, 3 red (test ubuntu, macos, windows-4), all one test, `OrgLedgerTests.No_pipe_op_writes_an_org_table` (expected list lacks `IsOffChart`): a1's own, red identically at `9f5c446c` alone (run locally), my diff touches neither OrgStore nor the test → seat A · names vs a1: 26 removed = 16 deleted + 10 renamed, 22 added.
1 Board page (`BoardView.cs`, `Labels.Board.cs`): done. Item 1 fixed after the rebase for a1's founding envelope (`paper_envelope` null path, `days` none = "no end date").
2 Safety → Owner (`OwnerView.cs`): done. Board controls and both live presses gone with their factories. Kept with their press counts: kill switch, cancel and close all, keys, real-money cap, perception, AI price, stop deployment and withdraw. Release is offered only while `!Board.AnyActive`, with the ruling's sentence. Today's midnight sentence fixed (P2). Deviation: owner-facing "Safety page" sentences repointed in Errors, AppHost, Chat, Settings, LossHold, RuntimeManifest and ApiAgentRuntime.
3 Evidence/Connections: done. Settings builds and the owner's page shows platform, account, key and bridge, price, friction, holdout, campaign budgets and judge capital (read-only, effective values) and updates. Settings keeps the market data.
4 Dashboard approval card + window "asking permission" banner gone: done. 5 Rail as data, in the brief's order: done. 6 Earlier armed home card, one press: done (UI tested with a fake; the gateway half is a1's).
RED before: item 1 CS0246 'BoardPage'; item 2 CS0246 'OwnerPage' (at item-1 commit); item 4 `Sub-string found "Gateway.ApproveAsync("`; item 5 CS0117 'Rail'; item 6 CS0426 'EarlierArmedHome'.
Mutant: "Switch real-money trading ON" re-added to the emergency column → `The_owners_page_holds_exactly_the_owners_presses` red (Collections differ, pos 13 "Switch real-money trading ON"); restored, green.
Deleted (control shed): TwoPress ×10 (7 limit/closure saves, 3 allocation) + `Raising_the_plan_ceiling…`, `The_turn_allowance_saves…`, `Raising_the_lanes…`, `Editing_a_lane_box…`, `The_safety_page_offers_the_ai_tool…`, `The_safety_page_puts_both_budgets…`.
Renamed (page renamed or retargeted, content kept): the safety_page → owners_page tests (kill switch, three factories, release factory, key box, both-ceilings, reached-lines, number boxes), the envelope comma → real-money cap box, `Granting_an_envelope…` → `Withdrawing_an_envelope…`, look words → Board page. The 26→4 walk is retargeted to the boxes that stay.
NOT done (not my files, owed): "Safety page" text in TradingGateway.cs (4050, 5711), Canon.cs, OrgStore.Seats.cs, SeatResolver.cs and APPROVAL_REQUIRED's repair; docs/USER-GUIDE.md.
Codex review: `fleet/tmp/B-scratch/u-settings-shed/U-settings-shed-review.md` (written, not acted on). Tests box: NOT RUN — busy (exit 4). Screenshots NOT RUN — seat B runs the Mac UI check at landing.
