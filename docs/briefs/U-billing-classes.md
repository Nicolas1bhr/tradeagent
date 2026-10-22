# U-billing-classes — every launch is counted against the ceiling of its own billing class — the owner's plan capacity (a list-price equivalent, not money) or the real-money cap — decided from the seat the app verified and recorded with the row; an unknown class counts against both; the envelopes nest in each; the Safety page shows and sets both, a raise asking twice
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.4 ("Currencies: AI spend in two billing classes (plan capacity — a list-price equivalent the owner sets — apart
from the real-money cap, `U-billing-classes`)"; "envelope_share is a fraction of the parent's envelope in the same currency and period"), § 6, § 8 (BUDGET-001);
ORGANISATION § 10 (:305-309 "Two billing classes, two ceilings"), § 13.4, § 13.11 (two presses for "the plan-capacity ceiling"), § 16 (2026-10-02: "a separate
plan-capacity ceiling at the equivalent of $40 a day, with $5 kept as the real-money cap for API keys"; 2026-10-21: the tier is Plus); COUNCIL rule 4
(`docs/COUNCIL.md:40-42` "subscription charges, API charges and list-price equivalents are three figures, never one"); R17 #1; § 15 `U-billing-classes` (:423).
**Protections — BUDGET-001 (one admission transaction; the class's day gate first; envelopes nest in each class; unknown never zero — counted in both;
ceilings read once per admission), two presses to raise a ceiling (the plan ceiling's raise asks twice, a lowering saves in one, Unreadable → 0), measurement
vs claim (the class app-composed from the verdict, never an agent's word; older rows `unknown`), AUTH-001 (no op, verb or door reaches either ceiling), honesty
(the plan figure is never shown as money): red-first and ONE watched mutant.** Seat B; **opus/high**. **A rung (the next free at dispatch):**
`ai_attempt.billing_class`. No canon change (`Canon.cs:379` speaks of "the daily limit" in general).
**Depends (landed):** O4 (rung 38), `U-vendor-limit-wakes`; **`U-org-seats` first** (same files; the class rides the seat verdict). Lands AFTER M0 attempt 4
(its 10 € raise bounds that run's plan turns today). Survey: `fleet/tmp/B-scratch/s-org-seats-billing/survey.md`.
**Today:** one `AiDailyCostCap` (`Trading.cs:560`, 5; the owner's 10 € for attempt 4) counts every row: the day gate over all rows (`AiAttemptStore.cs:390-398`),
envelopes sized from it (`TurnMeter.cs:897-906`), perception's budget inside it (`Trading.cs:563-576`, `TypeSafeWire.cs:294-302`). A codex seat may be signed in with
ChatGPT OR a pasted key (`RuntimeManifest.cs:773-790`), and the app keeps only `login status`'s exit code (`SeatProfile.cs:1111-1122`) though both texts are
measured ("Logged in using ChatGPT", `BUILD-STATUS.md:6748`; "Logged in using an API key - …", `RuntimeManifest.cs:943-944`). Attempt 3's 36 plan turns took
3.84 USD of the 5 USD cap (`BUILD-STATUS.md:8807-8808`) — capacity counted as money.
**Vendor facts (learn.chatgpt.com/docs/pricing, read 2026-10-22):** Plus "$20/month"; "These estimates are not fixed message limits"; "Weekly limits may also apply";
"ChatGPT Plus and Pro users who reach their usage limit can purchase additional credits"; credits per 1M tokens equal `ListPrices.cs`'s dollars ÷ 0.04 on every
listed column (INFERENCE), so $40/day ≙ 1,000 credits/day; Plus's included credits per window and the credit price: UNKNOWN.
**Observable result:** a codex seat signed in with ChatGPT is admitted against `AiPlanDailyCapacity` (default 40, in the price rows' currency, a list-price
equivalent) over the day's `plan` and `unknown` rows, and never against `AiDailyCostCap`; a harness, perception or API-key codex launch against `AiDailyCostCap`
over `key` and `unknown` rows; a seat whose sign-in text is neither pattern, or with no seat profile, is `unknown` and must fit both. Each unit envelope is its
shares × the class's ceiling over that class's rows. The row carries `billing_class`. The Situation's spend line names its own class's ceiling ("of a $40.00
daily plan-capacity limit — your owner's ChatGPT plan counted at list price, not money" / "of a $10.00 daily limit of real money"); the Dashboard card and the
Safety page show both figures; raising the plan ceiling asks twice; the page says the vendor's own limit may stop turns sooner, that the owner's own Codex use
shares the plan and is not counted here, and that turns past the plan's included use spend bought credits.
Read first: the survey; ORCHESTRATION §§ 2.4, 6, 8; ORGANISATION §§ 10, 13, 16; `AiAttemptStore.cs:136-222, 330-470, 630-700`; `TurnMeter.cs:873-1110, 1400-1430`;
`Trading.cs:540-580, 720-760, 855-1025`; `SeatProfile.cs:1015-1125`; `AgentSession.cs:338-370`; `RuntimeManifest.cs:930-1000`; `TypeSafeWire.cs:280-310`;
`MissionLoop.cs:1315-1345`; `DashboardView.cs:470-500, 1330-1360, 1685-1760, 2480-2490`; `AppHost.cs:2005-2020`.
Items, one commit each, one-sentence messages:
1. **The rung and the class.** `ai_attempt.billing_class TEXT NOT NULL CHECK (billing_class IN ('plan','key','unknown'))`, existing rows `unknown`, UPDATE of it
   refused by trigger, the `Versions` note. `SeatReading` gains the sign-in kind from two manifest patterns (`PlanSignInPattern`, `KeySignInPattern` — vendor data,
   codex filled from the measured texts; not in the profile hash); `SeatVerdict.BillingClass`: harness `key`, a codex reading by pattern, no seat profile or no
   match `unknown`; perception's attempt `key`. `TurnMeter.Begin` writes it from the same verdict as `SeatClass`. Settles: (d), (h).
2. **Admission per class.** `AiAdmissionRule` carries the class and both ceilings, read ONCE per admission; `Begin`'s day gate reads the rows of the class
   (+ `unknown`) — both gates for an `unknown` launch; `OrgEnvelopes` sizes each unit from the class's ceiling and `UnitTotalsBetween` filters by class;
   perception's role cap stays inside the money cap. Settles: (a), (b), (c), (e).
3. **The readings.** `AiSpendToday` per class (`TodayIn(class)`; `TodayFor(role)` reads the class of the role's last admitted attempt, `unknown` before one — so
   the loop's first look asks both); `SpendLine`, `MissionCost` and `AppHost.cs:2011-2014`'s refusal words name the class's ceiling. Settles: (g).
4. **The setting.** `AiPlanDailyCapacity` (default 40, the owner's answer of 2026-10-02; Unreadable → 0; zero = no plan turns); `AiDailyCostCap`'s doc says it
   is the real-money cap. Settles: (f).
5. **The Safety page.** "What the AI costs" shows two rows — real money (API keys) and the ChatGPT plan's capacity (a list-price equivalent, not money) — each
   with today's figure, its box and its press (`BuildSaveDailyCap`'s two-press pattern for the plan box, disarmed by an edit), and the three sentences of the
   observable result; every seat's class in words. Settles: (f), (g).
Red-first (each red at base quoted): (a) `A_plan_turn_counts_against_the_plan_ceiling_and_never_the_money_cap`; (b) `A_key_turn_counts_against_the_money_cap_and_never_the_plan_ceiling`
(harness and perception); (c) `An_unknown_class_counts_against_both_ceilings`; (d) `The_sign_in_names_the_class_and_an_unrecognised_one_is_unknown`;
(e) `Envelopes_nest_in_each_class_and_two_siblings_cannot_both_take_a_classes_last_room`; (f) `Raising_the_plan_ceiling_asks_twice_and_lowering_saves_in_one`
(+ Unreadable reads 0); (g) `The_situation_names_its_seats_ceiling_and_the_safety_page_shows_both`; (h) `The_class_is_recorded_with_the_row_from_the_verdict_admitted`
(+ the rung's crash-between-statements reopen test); GUARD `No_pipe_op_door_verb_or_trade_verb_reaches_either_ceiling`.
**The watched mutant:** the class filter dropped from the plan day gate (plan launches read every row against the plan ceiling, key rows included) — or, the
mirror, plan rows counted in the money gate ⇒ (a) red. Quote it, restore, green.
**Evidence beyond CI:** a puppet life with two puppet seats declared `plan` and `key`, ceilings 1 and 1: launches admitted until each refuses for its own
class only; the rows' `billing_class` and both readings printed. The Safety page captured with `tools/mac-shot.sh` on a dev home (no model).
**NOT VERIFIED until a real run:** "Logged in using ChatGPT" under the pinned codex (measured on 0.153.4 only; the owner's login may not be used to re-read it —
an unmatched text reads `unknown`, counted in both); the plan's included use against $40 (the vendor's estimates are "not fixed message limits").
**NOT in scope:** the 5-hour window as a counted period (`U-vendor-limit`'s hold stays its gate); per-class mandate allocations (O5a's stay one list-price
figure); a local-model class; detecting bought credits (no CLI reading exists — UNKNOWN); the Org page (O7).
Conflicts: `U-org-seats` (before this, same files); O6 and `U-org-packets-b` (the rung order; `OrgEnvelopes`, `OrgStore`); `U-rewake-floor` (`Trading.cs`
settings, the Safety page); seat A's 2.7 files (`SeatProfile.cs`, `RuntimeManifest.cs`, `AgentSession.cs`) — the orchestrator tells seat A. On landing, the
record edits ORCHESTRATION § 2.4/§ 8 ("the day-total gate of the attempt's class first; unknown in both"), ORGANISATION § 10 ("Today one $5 cap counts both" →
history) and says in one line that later plan runs are bounded by the $40 plan ceiling, the 10 € staying on the money cap. Gate and report per
`fleet/BUILDER-PROMPT-v2.md` (the rung's crash-between-statements reopen test; names vs `main` 0 removed; three platforms; Codex review; tests box line).
`## Report` ≤ 20 lines appended here.

## Report
Tip `eb4c40b0a8` (base `efd8e399`; code unchanged since the CI run). **RUNG 46** (`const int billingRung`, one constant; `Versions` note says "after U-org-packets-b's 45"). No model call.
Gate: 1. `build -c Release --no-incremental` → "0 Warning(s) 0 Error(s)". 2. suite.sh 3× each: BillingClassesTests 12/12, PerceptionReadersTests 1/1, DecisionPortTests 8/8, OversightInvisibilityTests 5/5 (Integration) — all three runs green. Local full: Unit 2132/0 (6 skipped), Integration 823 passed + 1 red (the oversight pin, fixed in `eb4c40b0`, then 5/5). 3. CI 39742538924 on eb4c40b0: success, 12/12 jobs (dedupe, shard-plan, package, package-linux, ubuntu, macos, windows 1–5, linux-host); no PromptStdin sighting. 4. names.sh: "removed: 0", sets base 3077 tip 3089 (added 12). 5. tests box: NOT RUN — `ready` exit 4, "a run is in progress (20261022-092021-6065718d)".
1. `4cd851f8` rung 46 `ai_attempt.billing_class TEXT NOT NULL DEFAULT 'unknown' CHECK IN ('plan','key','unknown')` + trigger `ai_attempt_billing_fixed`; `SeatPlan.PlanSignInPattern`/`KeySignInPattern` (codex: the two measured texts; both or neither → unknown; not hashed); `SeatReading.BillingClass` read by `CliSeatProbe` off `login status`; `SeatVerdict.BillingClass` (harness key, no seat profile unknown); perception's row key; `TurnMeter.Begin` writes it beside `SeatClass`.
2. `2a5783fd` `AiAdmissionRule.BillingClass` (required) + `PlanCap` + `PlanFunding`; the store gates each ceiling of the class (plan → plan cap over plan+unknown rows; key → money cap over key+unknown; unknown both, money first), units sized per ceiling from one chart read; refusal `PlanDay` in the plan's words; perception's role cap stays under the money ceiling only.
3. `48479295` `TodayIn(class)`, `Today` = both (money reading + `PlanReading`), `TodayFor` = the role's last admitted class (in-process), unknown before one; SpendLine, MissionCost, the arm sentence, SpendCapReached and the loop's wait words name their ceiling ("…daily plan-capacity limit — your owner's ChatGPT plan counted at list price, not money" / "…daily limit of real money").
4. `35e2a5a5` `AiPlanDailyCapacity` = 40 (Q6), Unreadable 0; AppHost hands it to the meter; AiDailyCostCap doc = real money. 5. `72b746a6` Safety page: two rows (figure, box, press — the plan's raise asks twice, an edit disarms), the three sentences incl. bought credits = real money TradeAgent cannot see (Q8, no setting), every seat's class in words; GUARD. `eb4c40b0` pins `TurnMeter.TodayIn` on the oversight line's enumerated totals and sweeps both store totals per ceiling (declared test edit).
RED-before: the unit's behaviour reverted (`fleet/tmp/B-scratch/u-billing-classes/revert.py`, API kept) → "Failed: 11, Passed: 1" — every new test red, the GUARD green (as a guard). Mutant (class filter dropped from the plan day gate) → `A_plan_turn_counts_against_the_plan_ceiling_and_never_the_money_cap` "Assert.True() Failure Expected: True Actual: False"; restored → 12/12.
Rung reopen: trigger dropped / stamp missing → schema back at 46, trigger present, the row still `plan`, UPDATE refused (`The_billing_rung_arrives_at_its_number_and_a_crash_before_its_stamp_runs_it_again`).
Scan phrases: `per million tokens`, `TurnAllowance\.Default\.InputTokens`, `ChatGPT workspace token` (three "token" false positives, each read). Review HELD for the observed run: `fleet/tmp/B-scratch/u-billing-classes/U-billing-classes-review-prompt.md`.
Declared: test edits — PerceptionReadersTests (the rule's required class + the perception attempt `key`), DecisionPortTests (+ perception row is `key`), OversightInvisibilityTests (pin + per-ceiling sweep). A meter built with no plan ceiling holds plan to its own money reading (every older test unchanged); the shipped app always hands both. The puppet-life evidence is done as unit tests with fake seats read `plan`/`key`, not a puppet manifest (no puppet billing hook built).
NOT DONE / NOT VERIFIED: the Safety page screenshot (opening that page needs the desktop's screen control, which asks the owner, away); "Logged in using ChatGPT" under the pinned codex (unmatched → unknown, both); the plan's included use vs $40; the daily report shows the money ceiling only; the class shown per seat is in-process (unknown after a restart until its next admission); doc edits (ORCHESTRATION §§ 2.4/8, ORGANISATION § 10) are the landing's. Lands after M0 attempt 4: plan runs after it are bounded by $40, the 10 € staying on the money cap.

## Fix pass 1
Code tip `be1e7e4b` (rebased onto main `b7cd6fd0`, U-org-packets-b's rung 45; Database.cs/Versioning.cs conflicts resolved: 45 stays, ours 46 after it; `DatabaseSchemaVersion = 46`). No model call, no codex.
Gate (on the rebased tree): 1. `build -c Release --no-incremental` → "0 Warning(s) 0 Error(s)". 2. suite.sh 3×: BillingClassesTests 15/15, PerceptionCardTests 6/6 — all three green; once: OrgPacketTests 11/11, CouncilLoopTests 74/74. 3. CI 39755340922 on be1e7e4: success, 12/12 jobs (39754755783 on the pre-rebase tip cancelled by seat B when main moved). 4. names.sh: "removed: 0", sets base 3094 tip 3110.
1. DONE `becdd580` — a role's class from durable truth: declared harness → `key` by code; else `AiAttemptStore.LastBillingClass(role, declared seat class)`, the ledger's last row on the seat it is on now; no row / unreadable → `unknown` (both). `_lastClass` (process memory) removed; `TodayFor`, `ClassOf` and so `affordable`/`CappedUntilMidnight` and the Safety page's seat line read it. Design call: not the seat verdict — `CheckSeat` runs the CLI probe and rewrites the session's `_admittedProfile`, and the conversation is attached only after the loop's cap look. Residual: a CLI role with NO row in this home still reads both until its first admission.
2. DONE `dfd3a61a` — perception card's day = `TotalsBetween(..., ceiling: Key)` (key + unknown rows).
3. DONE `f9eb435f` — `SafetyPage.TodayLines` money line from `today with { PlanReading = null }`.
4. DONE `be1e7e4b` — report: "spent in real money"/"daily ceiling of real money"/"turns against real money" (key+unknown), "plan capacity used: X of Y a day, Z committed — … list price in USD, not money" and "turns against plan capacity" (plan+unknown); `ReportRole.Class`, a plan role in capacity words, an unknown role named both; the pipe's report reply carries `Class` and the four plan figures (read-only, declared wire addition). AppHost unchanged (it hands `Today`, which carries `PlanReading`).
RED-before (base c668d477): fix 1 `After_an_app_start_a_role_is_held_to_its_own_classes_ceiling_from_the_first_look` → "Assert.True() Failure Expected: True Actual: False" (`TodayFor(Operations).AdmitsAnotherTurn`, money 0, plan 40, a prior plan row); fix 2 `The_card_never_adds_plan_rows_as_money` → "Assert.False() Failure Expected: False Actual: True"; fix 3 `Each_safety_page_line_says_reached_only_for_its_own_ceiling` → money line "…10 USD in real money — reached; …"; fix 4 `The_daily_report_counts_plan_turns_and_never_prints_plan_figures_as_money` → `PlanTurns` "Expected: 2 Actual: null" (`Turns` 0 passed — the review's "turns 0").
Mutant (money path, fix 1): role filter dropped from `LastBillingClass` (`OR 1 = 1`) → fix 1's test "Assert.True() Failure Expected: True Actual: False" at :267; restored → 15/15.
Tests box: run 20261022-112136-3267fc4f GREEN (Unit 2099/0, Integration 733/0, Fault 498/0, timing green; his files unchanged) — on the PRE-REBASE tip 3267fc4f, not be1e7e4b.
Re-review HELD for the observed run: prompt `fleet/tmp/B-scratch/u-billing-classes-fix1/rereview-prompt.md`.
NOT DONE / NOT VERIFIED: no full local suite (CI is the evidence); the report's wording seen in tests only, no app run; point 5 (rung order) settled by the rebase; doc edits stay the landing's.
