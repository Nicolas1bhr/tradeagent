# U-board-a2 — part a2 of U-board-a: the security board as data: three dormant seats outside the chart, one founding policy decided by `app` that the gateway, the meter and the paper allocator read instead of the owner's boxes, and live closed by construction
**THIS PASS IS PART a2 ONLY** (seat A, 2026-10-23 ~06:0x): items 8–11 below, their red-first tests, and the extra items A–D here. Opus/high. No rung. Part a1 (items 1–7)
is BUILT on branch `u-board-a1`, at a1ffb56b or later: rung 47, `BoardStore`, `BoardPolicy`, `BoardFounding` (the founding paper values are already in the record, 9f5c446c),
`BoardReader`, and the earlier-armed-home refusal. Read a1's `## Report` and `## Fix pass 1` in its brief, which is on that branch. This branch is STACKED on a1: never rebase it onto
main alone. Your worktree starts at a1's head; `git -C <WT> rebase origin/u-board-a1` whenever a1 moves.
**The train:** [U-board-a1, U-board-a2, U-settings-shed] land together; seat A takes `land`. Seat B's U-settings-shed owns every App view and rebases onto your tip at the end, so
your App changes are ONLY item 11's compile-only deletions.
**The orchestrator's rulings, binding** (`$F/tmp/A-org/board-a/a2-rulings.md`; record them in your report):
- GENERAL RULE: until a board seat is ACTIVE (`BoardReader.AnyActive` false), an ACT that affects only paper or plan capacity, and would otherwise have no decider, stays the owner's
  in-process press. Do NOT delete such a press. Seat choices, the loss-hold release and "Reopen after review" stay his while no seat is active; envelope withdraw is his permanently.
- VALUES (limits, envelopes, allowances) are not acts: they move to the founding policy, decider `app`. Live mode, live activation and real-money allocation never return to him.
- The founding paper values are those of a1's record: allowlist [BTCUSDT]; one envelope on the app's paper venue; ≤ 0.05 BTC and ≤ 5,000 USDT notional per deployment, the
  notional cap binding first; deployment count by the existing defaults; NO expiry while no seat is active.
- The owner's real-money AI cap (`AiDailyCostCap`), the perception budget inside it, the evidence settings, the kill-switch family and the watcher's cap stay HIS settings.
**Extra items, one commit each:**
- A. STALE OWNER-FACING TEXT: repoint every sentence in seat A's files that names "Safety page" (re-grep `Safety page`: TradingGateway.cs ~:4050 and ~:5711, Canon.cs,
  OrgStore.Seats.cs, SeatResolver.cs, the APPROVAL_REQUIRED repair sentence). U-settings-shed deletes that page and renames the owner's page "Owner"; ask seat B
  (`ab40151319822aa64`) for the exact page names before you write. Seat B takes docs/USER-GUIDE.md.
- B. A PRESS SURVIVES A RESTART in a refused armed home. a1 holds presses in memory while an earlier armed row stands, because it never rewrites that row, so a kill-switch press made
  then is lost on restart. Persist such presses without rewriting the armed row. Test (RED first): press Stop in a refused armed home, restart, set aside → Stop still holds.
- C. THE BOARD'S FOUNDING BUDGET SHARE: a1's placeholder 0.10 of the plan day-total is UNRULED. Read nothing from it in a2 and leave its value; the orchestrator rules it.
- D. THE DAILY-LOSS GATE: a1 deleted "live needs a daily-loss bound" with SetMode, because nothing selects live. Leave it deleted. U-board-live re-adds it on the board's act;
  that is a CARD, not yours.
**Protection unit (seat A; R24 §§ 4.1, 4.5, 7 row 1; ORGANISATION § 15 row `U-board-a`). AUTH-001/AUTH-002, CHANGE-001, ORDER-001/SIGNAL-001, EVID-001,
BUDGET-001, refuse-never-guess, recovery. RUNG 47 (tradeagent.db). ONE TRAIN with seat B's `U-settings-shed`, this unit first.**
**Model/effort: opus/xhigh for part a1 (it rewrites the dispatch gate's source of mode and activation, adds a rung, and refuses an older home that was
armed for real money — the money path); opus/high for part a2 (mechanical: readers switched, tests rewritten).** The split is proposed below; one builder
pass cannot carry both safely (≈ 700 test lines across ~100 files change, see survey Q3).
Source: the read-only survey `fleet/tmp/A-org/board-a/survey.md` (main `82dd6ed3`). R24 §§ 0, 3, 4.1–4.5, 4.7, 5, 7, 9 are the design; read them.
**Depends:** `U-org-verbs-a` and `U-number-entry-culture` LANDED (both touch `RiskPolicy`, `TradingGateway.cs`, `TurnMeter.cs`, the Safety page); re-grep
every pointer at your rebase. Rung 47 is this unit's unless the BOARD says otherwise when you start.

**Observable result.** A fresh home opens with a `board_policy` row version 1, decider `app`, basis `founding`, and three `board_seat` rows, all dormant.
Every limit the gateway enforces, every ceiling the meter admits against except the owner's real-money cap, the lanes, the wake policy and the paper
envelope come from that row; the settings row no longer holds any of them, and a number left in an older settings row governs nothing. No code path,
no press, no pipe op, no verb and no stdin line can select a live mode or switch real money on: the board's table cannot store one. A home whose settings
were armed for real money by a build before the board sends nothing, says so naming both versions and the repair, and keeps its row as it was.

## Part a1 — the record, the seats, live closed (rung 47; opus/xhigh)
Items, one commit each, one-sentence messages:
1. **Rung 47.** `board_seat`, `board_policy`, `board_event` in `tradeagent.db` — never `org_*`. `board_policy.decider` CHECK = `'app'` (U-board-b widens it),
   `mode` CHECK IN (`'OBSERVE'`,`'PAPER'`), `live_activated` CHECK = 0 (U-board-live relaxes both in its own rung), `body` canonical JSON with `body_sha256`.
   Every table refuses UPDATE and DELETE by trigger. Statements idempotent, the stamp last (a crash before the stamp reruns cleanly). No backfill from the
   settings row. `Versions.DatabaseSchemaVersion` 47 with its paragraph.
2. **The founding record and the seats.** `Core/Board/BoardStore.cs` (the ONLY writer of `board_*`), `BoardPolicy` (typed: mode, live_activated, `RiskPolicy`,
   plan day-total, role shares, turn allowance, lanes ×3, review minutes, head ceiling, re-wake floors, the standing paper envelope's terms) and
   `BoardFounding.V1` with the build's current defaults (survey Q1) plus the orchestrator's decided values for the allowlist and envelope (survey Q4).
   Written once by the app at open when no row exists. Three seats `board-1..3`: seat class broker|cell|harness (never `cli`), ≥ 2 distinct model classes,
   vendor family recorded, status dormant — nothing launches them (the activation gate, ORCHESTRATION § 2.3). The policy in force is the highest version,
   read by a STRICT reader: unknown or missing field, or a hash that differs → refused, never defaulted.
3. **Outside the chart.** Board seat ids are refused wherever `AppPrincipals` names are refused today (`OrgStore` mint, holder, decider, note sender and
   recipient, verb caller) — by prefix, spelled once (`BoardSeats.IsBoardId`); `OrgStore.Positions()` never returns one. The board's budget is a founding
   share recorded in the founding record and changeable only by the owner (no writer for it in this unit).
4. **Mode and activation move.** `Mode` and `LiveActivated` leave `TradeAgentSettings`; the gateway reads them from the policy in force (dispatch gate
   `TradingGateway.cs:3488-3540`, paper checks :303, :945, :3615, every record stamp, status :3156-3159). `SetMode` and `ActivateLive` are deleted; the
   dev host's stdin `mode`, `live`, `risk` are deleted (`GatewayHost/Program.cs:79, 82, 88-97`). `Containment.RefusalToLaunch` (`AppHost.cs:1181-1182`),
   `DailyReports.cs:320` and `Doctor.cs:264` read the policy. Tests that drive the live gates (`DispatchGateTests`, `FaultTests`, `Approval*`, …) set the
   policy through an INTERNAL seam (`InternalsVisibleTo` the three test projects on `TradeAgent.Gateway.csproj`; the `PuppetSeats.TestHost` precedent).
   Nothing in `src/` sets it (a guard says so).
5. **The earlier armed home.** `LoadSettings` reads the raw row's `mode`/`live_activated` keys (this build never writes them). A live mode or `true` →
   `TryAuthorizeExecution` refuses everything under a new code (`HOME_ARMED_BEFORE_THE_BOARD`), health PAUSED, one Activity line naming the home's schema
   stamp and 47 and the repair; the row is not rewritten. In-process only: `TradingGateway.EarlierLiveSetting` (what the row held, or null) and
   `SetAsideEarlierLiveSetting()` — one press, a narrowing — copies the raw row to kv `settings_before_board` and writes the current shape. A non-live
   `mode` key is ignored with no refusal (owner-set values simply go, R24 § 4.5).
6. **Fail closed.** An unreadable policy row reads as `BoardPolicy.Unreadable()` — `TradeAgentSettings.Unreadable()`'s shape: OBSERVE, plan total 0, envelope
   none, nothing admitted — with the row kept and health PAUSED; never the founding defaults re-derived.
7. **App compile only** (the seam, below): delete the Safety page's mode row and real-money switch (`DashboardView.cs:1708-1711` and what backs them) and
   the dev host lines; NO new UI.

## Part a2 — the readers (no rung; opus/high)
8. **Gateway limits.** Every `Settings.Risk` read (`TradingGateway.cs:2810, 3602, 3719, 3849, 4153, 4457, 5632, 6085-6092, 6295, 11167`, status, canon via
   status) reads the policy's `RiskPolicy`. `Risk` leaves `TradeAgentSettings` and `Unreadable()`.
9. **The meter and the loop.** `AppHost` delegates: plan day-total (`:203`), role shares (`:1203`), allowance (`:388-389`), lanes (`:2306-2314`), review
   (`:1234`), head ceiling and re-wake floors (`:1267-1280`) read the policy; those fields leave settings. `AiDailyCostCap` (`:197`) and
   `PerceptionDailyBudget` (`:223`) stay the owner's settings — the board never raises a paid-key cap.
10. **The paper allocator.** The founding envelope: written by the app through the gateway's grant path (both witnesses, PAPER) only on connector `paper`,
    account `PAPER-1`, when none stands while the founding policy governs; its reason and Activity line name the founding policy, never "you".
11. **App compile only:** delete the Safety page writers whose settings members are gone (`SaveLimits` :2711, `SavePlanCapacity` :2586, `SaveTurnAllowance`,
    `SaveLanes`, `SaveReviewEvery` :2682, `SaveResearchShare`, envelope grant/withdraw :2891/:2943, `Allocate` :2819) and the boxes behind them. NO new UI.

## Red-first (quote each red at base)
- `Founding_policy_is_one_app_record_and_the_gateway_reads_it` (a1) — fresh home: v1, decider `app`, basis `founding`, hash verifies; status shows its limits.
- `No_row_of_the_board_store_holds_a_live_mode_or_an_activation` (a1) — raw INSERT/UPDATE of LIVE_CONFIRM, LIVE_AUTONOMOUS, `live_activated=1` refused by the schema.
- `An_earlier_home_armed_for_real_money_places_nothing_and_keeps_its_row` (a1) — settings `{"mode":"LIVE_AUTONOMOUS","live_activated":true,…}` at schema 46:
  every order refused with `HOME_ARMED_BEFORE_THE_BOARD`, words name 46 and 47 and the repair, raw row byte-identical; after `SetAsideEarlierLiveSetting`
  the row sits under `settings_before_board`, the refusal lifts, the mode is PAPER from the policy and nothing was converted into a policy row.
- `The_board_is_outside_the_chart` (a1) — `board-1` refused as a minted position, holder, decider, note party and verb caller; no `org_*` row names it.
- `Founding_composition_is_three_dormant_seats_on_two_model_classes` (a1) — never `cli`; nothing launches one.
- `An_unreadable_policy_fails_closed` (a1) — tampered body or hash: nothing dispatched, nothing admitted on the plan, row kept.
- `Rung_47_rerun_after_a_crash_before_its_stamp_writes_nothing_twice` (a1) — recovery.
- `A_limit_left_in_an_earlier_settings_row_governs_nothing` (a2) — row with `max_order_quantity` 50 and plan capacity 400: quantity 2 refused, admission at 40.
- `The_owners_real_money_cap_is_not_the_policys` (a2) — the policy has no member that sets `AiDailyCostCap` or `PerceptionDailyBudget`; both still read settings.
- `The_founding_envelope_is_the_apps_and_only_on_the_paper_venue` (a2) — on `paper`/`PAPER-1` one envelope with the decided terms; on the simulator or ATAS none.
- GUARDS: `No_app_path_selects_a_live_mode` (no public gateway writer of mode or activation; no `src/` caller of the seam; no stdin line); `Only_BoardStore_writes_board_tables`
  (source scan); `The_policy_holds_no_evidence_setting` (reflection: no friction, holdout, price, campaign or judge-capital member).

**ONE watched mutant:** delete the earlier-armed-home check from the dispatch gate (item 5) ⇒ `An_earlier_home_armed_for_real_money_places_nothing_and_keeps_its_row`
red (the paper-on-real refusal alone gives the wrong code and no words). Watch it red, restore.

## Protections, and how each is kept
- **AUTH-001** — the kill switch family, connections, sign-ins, the paid-key cap, updates, the evidence settings and the watcher are untouched and stay in-process.
  **AUTH-002** — the board's authority is data here with NO writer but the app's founding; U-board-b adds the board's acts.
- **CHANGE-001** — the board's composition and budget are founding values only the owner changes; nothing in this unit lets a board seat write anything.
- **ORDER-001 / SIGNAL-001** — no board field names, times or sizes an order; the founding policy sets ceilings and one paper envelope; the runner trades.
- **EVID-001** — friction, holdout, AI price, campaign budgets and judge capital stay settings (guard); the founding envelope is paper and authorises nothing live.
- **Refuse, never guess** — strict policy reader; the earlier armed home refused in words naming both versions and the repair; never converted, never switched.
- **Never destroy** — the earlier row is kept as found and, on the owner's press, under its own key; ledgers and the activity record of his past presses stay.
- **Recovery** — rung 47 idempotent, stamp last; the founding write and its event in one transaction.
- **Honesty** — founding values and the founding envelope are labelled the app's; nothing says the owner set them.

## The seam with seat B's `U-settings-shed` (same train; B rebases on this unit)
- **U-board-a owns:** `Core/Board/*` (new), `Core/Trading.cs` (fields out), `Core/Versioning.cs`, `Core/Db/Database.cs` (rung 47), `Core/Db/OrgStore.cs`
  (board-id refusal), `Gateway/TradingGateway.cs`, `Gateway/DailyReports.cs`, `GatewayHost/Program.cs`, `Diagnostics/Doctor.cs`, `AgentRuntime/*` readers,
  `App/AppHost.cs` (wiring, `Board` reader), the compile-only deletions of items 7 and 11, `TradeAgent.Gateway.csproj`, and every test of the above.
- **U-settings-shed owns:** every new or reshaped App view — the Board page, Safety and Settings reduced to R24 § 4.3, the earlier-armed-home sentence and its
  press, Approve/Decline of LIVE_CONFIRM removed, the App surface tests (`LossBudgetSurfaces`, `AllocationSurfaces`, `TwoPressGrant`, `MissionCostSurfaces`,
  `OrgLanes` UI parts, `SafetyLookWords`, `NumberEntryCulture` limit parts).
- **The one API the Board page reads:** `AppHost.Board` → `BoardReader` with `InForce()` (version, decider, basis, written_at, hash, and per field its value
  and the version that set it), `History()` (every version with rationale, deciders, evidence class and score — null for the founding record),
  `Seats()`; plus `TradingGateway.EarlierLiveSetting` / `SetAsideEarlierLiveSetting()`. Land the reader's shape as a1's FIRST push so B builds on it.
- **Alone:** U-board-a alone leaves an earlier armed home with no in-app repair (CLAUDE.md: the refusal ships with its in-app path) and no page showing what
  governs; U-settings-shed alone does not compile (no `BoardReader`). Hence one train.

## Conflicts
`u-number-entry-culture` (`RiskPolicy.FigureOf/Changes` in `Trading.cs`; ±330 lines of `DashboardView.cs`) and `u-org-verbs-a` (`OrgStore`, `TradingGateway.cs`,
`TurnMeter.cs`) land first — rebase onto both. `u-proving-ground-b`, `u-security-sweep-a`, `u-cli-code-mode`: none of note (the sweep's csproj pins vs
item 4's `InternalsVisibleTo`: trivial). `u-org-verbs-b`: `AppHost` composition, soft.

## NOT in scope
The board's verbs, acts, the two-seat rule, the blind confirmer, the owner's veto and his composition/budget presses (`U-board-b`). Live mode, activation,
capital allocation as board acts, the raise-pace guard, the runner's live dispatch (`U-board-live`, `U-runner-live`). The watcher's detectors over the board
(`U-watcher-board`). Ventures (`U-ventures-a`). Seat choices (model/runtime per role) unless the orchestrator moves them here (open question). The audit
line's budget (`U-org-audit-b`). Any new UI (`U-settings-shed`).

## R24's hard lines, which this unit must not cross
The board never trades — no field or act names, times or sizes an order. The board never touches evidence — no friction, holdout, price, campaign budget,
verdict or live-eligibility class is in its record. The board never widens its own power — its composition, its budget, the audit line's budget, the watcher,
the kill switch and the kernel's invariants have no writer reachable from a board position.

Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.
