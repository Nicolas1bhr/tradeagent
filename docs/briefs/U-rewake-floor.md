# U-rewake-floor — the sweep re-wakes a holder whose launch ended without delivering no sooner than its level's floor, doubling while it keeps not delivering, as policy data the owner sets; its own requested wake, a retry, a rejection and the owner's words are never delayed
**Arrow closed:** `docs/ORCHESTRATION.md` § 0.4 ("Turns are capital … a turn is spent on a divergence"), § 2.5 (:209-212: "an executor owns its open
mandate and its own requested wakes, never an app timer or heartbeat. **Timescale ceilings** (VISION § 7.5) are policy data — a maximum interval per
level, a floor and never a heartbeat"), § 4 flow 3 (the reconciliation pass re-raises each owed item once), § 6 (priority, triage and timescale ceilings
decide who gets the six turns); VISION § 7.5 ("A level's maximum wake interval follows from its timescale"); ORGANISATION § 10 (:327 "executors wake only
for assignments"), § 11 (code first: wake rules); § 15 row B `U-rewake-floor` (DUE BEFORE M-org1 — the orchestrator's ruling, 2026-10-22).
**Protections — BUDGET-001 (the floor only removes turns: admission, the allocation, the half-spent escalation and the deadline unchanged), the owner's
words never delayed (the floor writes `mandate`-kind rows only), recovery (the key and its idempotence unchanged; the due a function of records, so a
restart computes the same one), refuse never guess (a holder the chart cannot place takes the largest floor, said in words), autonomy inside authority
(the executor's own request keeps its time — `docs/PRINCIPLES.md`): red-first and ONE watched mutant.** Seat B; **opus/high** (the scheduler's spend).
**No rung** (`mission_event.due_at` exists; a settings property is not schema). No canon change.
**Depends:** O5a (rung 40) and O3 LANDED; **`U-org-packets-a` LANDED first** (its puppet life needs the re-raise: that life runs with floors zero, or waits
for the floor — item 4). Survey: `fleet/tmp/B-scratch/s-rewake-floor/survey.md` (main `ec41416f`) — re-grep every pointer.
**Applied pending ORCHESTRATOR (survey Q1–Q8):** defaults executor 15 min, team 60, department 240; doubling, cap 8× and never past the level's ceiling;
the re-raise due no later than the holder's own pending `self` wake; a LOST launch counts, the floor from the restart; an unplaceable holder takes the
largest floor; due ≤ max(now, deadline − F); no UI; a pending row is not re-dated when the floor changes.
**Today:** `OrgStore.Sweep` re-raises an owed mandate (issued/retry/rejected, no untaken `mandate` wake, no LAUNCHED attempt) with `Raise(...)`, DUE NOW,
keyed `mandate:<m>:<latest event>:after:<last launch>` (`OrgStore.Mandates.cs:793-800`; `MissionEventIds.Mandate` `MissionEventStore.cs:516-517`); the sweep
runs every tick and an ended lane ticks the loop (`MissionLoop.cs:2158-2178, 3413`), so an executor that ends a turn without delivering turns again at
once. `RaiseDue` (:670-678), `Due`/`Queue` (:731, :788) and `Idle → NextDueAt` (`MissionLoop.cs:3351`, store :807) already serve a future due. The holder's
`self` wake (`MissionLoop.cs:3445-3448`) does not stop the re-raise, and a launch woken by it alone serves no mandate (`OneMandate` :3625-3630). `Deliver`
(`OrgStore.Mandates.cs:487`) closes no untaken `mandate` wake. Precedents: `MissionOptions.Ceilings` per level (:146-157) from `HeadCeilingHours`
(`Trading.cs:505-511`, `AppHost.cs:1045`); `NextLook`'s doubling, cap `QuietLookCeiling = 8` (`MissionLoop.cs:3090, 3111-3119`).
**Observable result:** a member holding an issued mandate whose launch ends undelivered at T is re-woken at T + 15 min, then (still undelivered) at its
end + 30, + 60, + 120, + 120 …; a team head holding one at + 60 … ≤ 8 h, a department head ≤ min(32 h, its 24 h ceiling); the same re-raise after a restart
is due from the restart; the issued wake, a code retry, a rejection, an owner's message to that holder and its own `next.json` wake (sooner than the floor:
the mandate wake rides with it and the turn is charged to the mandate) are served at once; a delivery closes the holder's pending re-raise for that
mandate before any retry is raised; floors set to zero give today's behaviour exactly.
Read first: CLAUDE.md; ORCHESTRATION §§ 0, 2.5, 4, 8; ORGANISATION §§ 4, 10, 11; the survey; O5a's and U-quiet-review's records (`BUILD-STATUS.md`);
`OrgStore.Mandates.cs:640-871, 987-1012`; `MissionLoop.cs:110-160, 3085-3125, 3340-3450`; `AppHost.cs:1035-1050, 1890-1905`; `Trading.cs:480-515`.
Items, one commit each, one-sentence messages:
1. **The policy data.** `TradeAgentSettings.RewakeFloorMinutes` (a dictionary keyed by `OrgLevel`, shipped with the three defaults; below zero reads 0;
   a level missing reads its default), its summary in the `HeadCeilingHours` voice ("the owner's, in-process: no verb, no pipe op and no agent reaches
   it"; "not an authority boundary" — a `state/` neighbour lowering it restores today's bound, never more, as `AiLanes` says); a `RewakeFloors` record in
   Core (per level, the 8× cap, each level's ceiling) built in `AppHost` from the settings and `MissionOptions.Ceilings`. Settles: (f).
2. **The floored re-raise.** `OrgStore.Sweep(now, RewakeFloors floors)` (required; existing tests pass `RewakeFloors.None`): for an owed mandate with a
   last launch ENDED or LOST, `RaiseDue` at `max(now, min(ended_at + F × 2^(k−1) capped, the holder's earliest pending self due, deadline − F))`, k = the
   mandate's launches started after its latest mandate event; the key unchanged; `after:none` due now; the holder's level from `Roster()` — unplaceable ⇒
   the largest floor and an activity line naming the holder. Settles: (a)-(d), (g).
3. **A delivery withdraws the re-raise.** `Deliver` closes the mandate's untaken `mandate` wakes (withdrawn, "delivered <d>") in its own write BEFORE
   `Triage` raises a retry. Settles: (e).
4. **The puppet life.** `PuppetLivesTests` (packets-a's sessions life) runs with floors zero and says why; one new life: a member that never delivers
   turns at T, T+F, T+3F under a fake clock, and a `next.json` of 60 s brings the mandate wake to 60 s. Settles: (h).
Red-first (each red at base quoted): (a) `An_undelivered_launch_is_rewoken_no_sooner_than_its_levels_floor_and_the_floor_doubles_to_its_cap`;
(b) `A_restart_reraises_once_due_from_the_restart_and_a_second_pass_raises_nothing`; (c) GUARD `The_issued_wake_a_retry_a_rejection_and_the_owners_words_are_never_delayed`;
(d) `The_holders_own_requested_wake_brings_the_reraise_to_its_time_and_the_turn_is_charged_to_the_mandate`; (e) `A_delivery_withdraws_the_pending_reraise_before_its_retry`;
(f) `Floors_zero_give_todays_reraise_exactly_and_a_negative_reads_zero`; (g) `A_holder_the_chart_cannot_place_takes_the_largest_floor_and_says_so`;
(h) `A_member_that_never_delivers_spends_three_turns_where_it_spent_every_tick` (spend: count the paid turns over 1 h of fake clock, floors default vs zero).
**The watched mutant:** the re-raise's due computed as `now` (item 2's `RaiseDue` → `Raise`) ⇒ (a) and (h) red. Quote it, restore, green.
**Evidence beyond CI:** the puppet life of item 4 printed — the member's attempts' `started_at`, the `mandate` rows' `created_at`/`due_at` and their keys —
floors default vs zero. No model.
**NOT VERIFIED until a real run:** the defaults' cost on the owner's Plus plan (M-org1's measured turns); attempt 4 does not exercise it (legacy pair only).
**NOT in scope:** a Safety-page field (O7 `U-org-surface`); re-dating pending rows when the owner changes a floor; a floor on a head's judgment wakes
(`Rejudge` — already once per LOST launch); the legacy pair's looks (U-quiet-review's); any rule over what an executor does inside its turn.
Conflicts: O6 `U-org-verbs-a` (`OrgStore.Mandates.cs`: its `rescoped`/`proposed` events — if `proposed` leaves the mandate owed, the sweep's owed set
(:793) and this anchor include it; whichever lands second reconciles; a successor's issued wake is never delayed); `U-org-packets-b` (`MissionLoop.cs`,
disjoint lines); `U-org-packets-a` (its puppet life, item 4). Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings;
touched classes 3×; names vs `main` 0 removed; three platforms; Codex review; tests box line). `## Report` ≤ 20 lines appended here.
