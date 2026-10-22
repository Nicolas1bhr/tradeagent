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
