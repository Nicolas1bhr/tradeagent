# U-board-a1 — part a1 of U-board-a: the security board as data: three dormant seats outside the chart, one founding policy decided by `app` that the gateway, the meter and the paper allocator read instead of the owner's boxes, and live closed by construction
**THIS PASS IS PART a1 ONLY** (seat A, 2026-10-23 ~02:3x): items 1–7, the red-first tests that cover them, and the watched mutant. Part a2 (items 8–11) is the next
unit, `U-board-a2`, which will be stacked on this branch. Do not build it. Opus/xhigh.
**Seat A's rulings:**
- The store is `tradeagent.db` at **RUNG 47** (from the BOARD). The board's tables have ONE writer, `BoardStore`. CHECK constraints refuse what R24 forbids the board to store.
- The split a1/a2 is as proposed below.
- Seat choices (model and runtime per role) stay settings until U-board-b.
- a1 does NOT touch the owner's loss-hold release press or his envelope-withdraw press; the orchestrator rules on them. The precedent: "Reopen after review" stays the
  owner's while no board seat is ACTIVE.
- The founding paper values are a2's, and the orchestrator decides them.
**FIRST PUSH: the reader's shape, for seat B,** before item 1's rung. Commit the public shape that B's U-settings-shed builds on: `AppHost.Board` → `BoardReader` with
`InForce()`, `History()`, `Seats()` and `AnyActive` (true only when a board seat is ACTIVE; always false in a1), plus `TradingGateway.EarlierLiveSetting` and
`SetAsideEarlierLiveSetting()`. It must compile; its members may return the founding shape until item 2 fills them. Push it WITHOUT CI:
`$F/bin/scan.sh <WT> origin/main HEAD && git -C <WT> push -q origin u-board-a1`. Then:
- SendMessage to seat B (`ab40151319822aa64`): "U-board-a1 reader shape pushed: branch u-board-a1 <sha>";
- write the sha to `$F/tmp/A-org/board-a/a1-api-sha.txt`;
- go on with the items.
Later changes to the shape only ADD members. A rename or removal is a message to seat B first.
**The train:** [U-board-a1, U-board-a2, U-settings-shed] land together. Seat A takes `land`.
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

## Report
Code tip **f3979578** on main a51152ef (rebased; merge-base a51152ef). Reader shape sent to seat B: **beb1bb2d** (now 2f1c517c after the rebase, same content); B told every new head. Rung **47** (`Versions.BoardRung`).
Gate: (1) `dotnet build -c Release --no-incremental` → "Build succeeded. 0 Warning(s) 0 Error(s)". (2) touched classes at 9f5c446c, suite lock: Unit "Passed: 209, Total: 209", Fault "Passed: 436, Total: 436", Integration "Passed: 320, Skipped: 1, Total: 321"; BoardTests 3× "Passed: 12, Total: 12"; after the guard fix, Org/Council/Audit/Oversight/Position unit classes "Passed: 344, Total: 344". (3) CI 39889621609 failure (OrgLedgerTests.No_pipe_op_writes_an_org_table: IsOffChart undeclared — mine, fixed f3979578); **CI 39893757367 success, all 12 jobs**. (3b) review `fleet/tmp/A-org/u-board-a1/U-board-a1-review.md` (on f3979578; the 9f5c446c one kept as `-9f5c446c.md`), not acted on. (4) names.sh: removed 7, added 13 — all declared below.
Items: 1 done (24f8e538: three tables, CHECKs decider='app', basis='founding', mode IN (OBSERVE,PAPER), live_activated=0, seat_class never cli, status dormant; UPDATE/DELETE triggers; stamp last). 2 done (0771ff91: BoardStore sole writer, founding v1 + 3 seats + founded event in one transaction inside the rung, strict reader: hash, canonical spelling, Disallow-unmapped, required members, round-trip). 3 done (e2b3b5e5 + f3979578: BoardSeats.IsBoardId refused at mint, holder, decider, note sender/recipient, verb caller; Positions/Position/Roster/Standing/ActivePositionsBeyondLegacy/IsPositionId exclude board ids). 4 done (3ae96d80). 5 done (7b0dd240). 6 done (b35f3607). 7 done (a76557d2, committed BEFORE 4 so every commit compiles).
Deviations: (a) founding paper values per the orchestrator's ruling relayed by seat A (the record already carried the fields): 9f5c446c — allowlist [BTCUSDT], envelope paper/PAPER-1/BTCUSDT 0.05 BTC + 5,000 USDT (notional binds first; text says so), 1 deployment, `PaperEnvelopeTerms.Days` now int? = null (no expiry). (b) the board's founding budget share 0.10 of the plan day-total is MY placeholder — nobody ruled it; nothing reads it yet: orchestrator's to decide. (c) SetMode's "live needs a daily-loss bound" selection rule went with SetMode (no live selection exists); U-board-live re-adds it on the board's act. (d) settings no longer carry OBSERVE, so an unreadable settings row now refuses by an explicit gate (MODE_FORBIDS_EXECUTION, Labels.SettingsCouldNotBeRead). (e) while an earlier armed home stands, SaveSettings writes nothing over its row (presses, the kill switch included, hold in memory) so no press or restart erases it; emergency presses never pass the dispatch gate and stay reachable. (f) a `mode` key that is not plainly OBSERVE/PAPER (999, an unknown word) counts as armed → refused; an earlier OBSERVE is ignored and the founding PAPER governs (brief: non-live keys go). (g) status limits still read settings until a2 item 8 (test asserts equality but for the allowlist). (h) item 4's commit carries the dispatch gate's policy-unreadable line (item 6).
RED before each fix (quoted): at shape beb1bb2d — Founding_policy…/No_row…/Rung_47…/An_unreadable…: "SQLite Error 1: 'no such table: board_policy'"; Founding_composition…: "'no such table: board_seat'"; The_board_is_outside_the_chart: "Sub-string not found … Not found: \"security board\""; No_app_path_selects_a_live_mode: "Assert.Null() Failure … Actual: TradeAgent.Core.TradingMode Mode"; An_earlier_home…: "Assert.Throws() Failure: No exception was thrown" (a LIVE_AUTONOMOUS row placed a real-account order). At item 4 (3ae96d80): An_earlier_home… "Expected: HOME_ARMED_BEFORE_THE_BOARD Actual: MODE_ACCOUNT_MISMATCH"; An_unreadable_policy_fails_closed "Expected: 0 Actual: 40" (plan cap).
Mutant (the earlier-armed check deleted from TryAuthorizeExecution) → An_earlier_home… RED "Expected: HOME_ARMED_BEFORE_THE_BOARD Actual: TRADING_PERMISSION_UNAVAILABLE" (the paused health refuses before the paper-on-real guard the brief predicted — wrong code, no words either way); restored, green.
Tests removed/renamed (declared): TwoPressGrantTests A_real_money_mode_takes_two_presses_and_says_what_the_second_one_does, A_mode_that_only_reduces_stays_one_press, The_five_second_refresh_does_not_undo_a_half_pressed_real_money_mode, The_mode_row_wraps_so_an_armed_sentence_is_not_clipped (the mode row is deleted); LossBudgetSurfaces A_real_money_mode_cannot_be_chosen_while_the_day_has_no_loss_budget (SetMode deleted); UnknownModeTests A_mode_this_build_does_know_is_read_and_honoured → A_mode_an_earlier_build_saved_is_never_the_mode_in_force, Observe_still_refuses_with_the_mode_it_actually_is → An_earlier_watch_only_mode_is_ignored_and_the_founding_policy_governs (a settings mode never governs now).
Rewritten in place (declared): UnknownModeTests.An_undefined_numeric_mode_executes_nothing / The_owner_is_told_… (HOME_ARMED, words); UnreadableSettingsTests.A_numeric_mode_is_the_unknown_mode_check_and_not_this_one and its damage helper (damage moved to ai_trading_stopped: the row holds no mode); RestartTests.Settings_survive_a_restart (a live mode never survives); TwoPressGrantTests.The_safety_page_builds_all_three_controls_… (mode-row line dropped); OrgLedgerTests.No_pipe_op_writes_an_org_table (IsOffChart declared); ProtectedConfigurationTests ×4 (BoardPolicy, not settings); ResumeOnStart armed test (seam). Mechanical in ~60 files: `s.Mode = PAPER` dropped, live via internal SetModeUnderTest/ActivateLiveUnderTest, TestEnv.Ready(mode:, live:).
tests box: NOT RUN — NO - a run is in progress (20261023-041957-12a0103d); wait for it
NOT done / NOT verified: part a2 (items 8–11) not built; the app not run (no new UI here; seat B's); no full local suite (CI's); the review not triaged; the budget share (b) unruled.
Checked by the fresh fixer (2026-10-23 05:5x) against the builder's logs and runs: every gate, RED and mutant line above is in `fleet/tmp/A-org/u-board-a1/` (gate-build, gate-{Unit,Fault,Integration}Tests, gate-board-{1,2,3}, org-fix, red-shape, item4-board, item5-unit, mutant); `gh run view`: 39889621609 "failure" on 9f5c446c, 9 of 12 jobs, `OrgLedgerTests.No_pipe_op_writes_an_org_table [FAIL]`; 39893757367 "success" on f3979578, 12 of 12. Rebased onto main 50f4d45c (one conflict in that guard, both lists kept): reader shape **c4193267** (was 2f1c517c, first beb1bb2d), code head **ab5ac565** (was f3979578).
names.sh main u-board-a1 (run by the fixer): "sets: base 3262 tip 3268 / removed: 7" = the seven declared above; "added: 13"; "[Fact]/[Theory] base: 3075  tip: 3081". The changed-tests list (66 files) is `changed-tests.txt` there.
