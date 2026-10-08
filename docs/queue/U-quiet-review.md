# U-quiet-review — the app's scheduled look slows down while nothing but its own ticks wakes a role, and an idle turn's cost is told truthfully
**Protects:** the owner's AI allowance and `docs/PRINCIPLES.md` "No useful eligible work is healthy idleness … It does not mean constant paid inference" (COUNCIL rule 7: justified idleness
launches no inference), while keeping the agent's own choice of when to work ("Agents can choose a next task or wake condition within their allowance"). Light; seat B (seat P closed, R21); no rung; not the money
path. Lands before M0 attempt 4 and before `U-org-wakes` (both edit `MissionLoop.Schedule`). From R20 (`docs/research/2026-10-08/R20-autonomy-audit.md` § 4).
**Facts (RECORD and SOURCE at `30fe7a32`, read and not run).**
- M0 attempt 3 (`BUILD-STATUS.md:8816-8817`): "after the last brief/report (15:54:58Z) 16 review turns (16:10–20:15Z) spent 2.0337 USD, 53 % of the run, while Operations' plan read
  "No agenda or wake is pending"" — 0.127 USD a turn on the app's meter against the run's 0.107 average (`:8807`: 36 turns, 3.8444 USD): a resumed idle turn re-sends the session.
- `AgentRuntime/MissionLoop.cs` `Schedule` (`:1958-1993`) raises a scheduled Review per role at `now + ReviewEvery` (`:1968-1974`; 30 min, `Core/Trading.cs:476`, wired `App/AppHost.cs:787`)
  whenever none is unconsumed, whatever woke the last turn. `NextWait` (`:2188-2208`) reads the role's `next.json` (`AskedForDelay`, `:2336-2352`, capped at `MaxDelay` 30 min, `:111`),
  raises its Self wake (`:2202`) and calls `Schedule` (`:2206`); the turn's `wake` (`:1664`, `DueFor`) is not passed to it. Failed or vendor-held turns skip `NextWait` (`:1859-1865`).
- `Core/Db/MissionEventStore.cs`: rows keep `created_at`, `due_at`, `payload`, `consumed_by` (`:332-334`); no re-date or cancel method (`Consume` `:544`, `SettleWithoutTurn` `:601`); a review's
  id encodes its due minute (`:235-236`, role suffix `:247`). The owner's "work on its own" press raises a chair Review due now WITH a payload (`AppHost.cs:1467`); scheduled ones have none.
- AGENTS.md tells the agent "a turn that ends in ten seconds costs your owner almost nothing" (`AgentRuntime/WorkspaceBuilder.cs:247-248`) — false on a resumed session, per the record above.
- Pinned and to stay green: `MissionLoopTests.The_review_tick_and_the_renewal_are_scheduled_ahead_and_never_pile_up` (`:585`), `…The_card_says_what_the_loop_is_waiting_for` (`:610`);
  `MissionEventTests` `:84`, `:101` (id format); self-wake tests run with reviews off (`MissionLoopTests` `:506`, `:545`, `:680`; `CouncilLoopTests` `:953`).
Read first: `CLAUDE.md`; `docs/PRINCIPLES.md` (the "Never stopping" paragraph); `MissionLoop.cs:100-135`, `:1551-1670`, `:1840-1870`, `:1950-2000`, `:2130-2210`, `:2330-2355`;
`MissionEventStore.cs:100-135`, `:230-250`, `:330-380`, `:440-520`, `:540-620`; `WorkspaceBuilder.cs:236-262`; `docs/queue/U-org-wakes.md`.
Must NOT: skip, delay or settle any wake that is not a scheduled Review — owner words, inbox, data, fills, orders, reports, briefs, verdicts, boundaries, renewal and the role's own Self
wake keep their timing; change `MaxDelay`, the error backoff, the day-cap wait or the renewal; read prose (a plan saying "nothing pending") as a signal; add a setting, a table or a rung;
touch the other role's pending review; change the review id format.
Items, one commit each, one-sentence messages:
1. **The look decays while only looks wake the role.** When `NextWait` schedules after a turn whose consumed wake rows were ALL scheduled Reviews (no payload), that role's next review
   is raised at twice the interval of the review it consumed (its `due_at − created_at`), capped at 8 × `ReviewEvery` (4 h at the default); after any other wake — the owner's
   payload Review and the role's own Self wake included — it is raised at `ReviewEvery`. The interval is read back from the rows, so a restart keeps it; no new state.
2. **The agent is told the truth and how to keep working.** AGENTS.md's idle paragraph (`WorkspaceBuilder.cs:245-250`) replaces "costs your owner almost nothing" with what the record
   measured (an idle turn on a resumed session costs about what a working one does) and says: with work in progress, ask for your next wake in `next.json`; the app's own scheduled
   look slows down while nothing else happens and returns to every `ReviewEvery` on the first real event.
Red-first tests (each quoted red at base): (a) `Quiet_looks_back_off_and_a_real_event_resets_them` — review-only wakes give intervals 30, 60, 120, 240, 240 min, then a Report wake →
30; (b) `A_roles_own_wake_resets_the_look` — a `next.json` Self wake → the following review at `ReviewEvery`; (c) `The_owners_work_on_its_own_press_is_not_a_quiet_look` (payload
Review → `ReviewEvery`); (d) `The_back_off_survives_a_restart` — a new loop over the same database continues from the pending row's interval; (e) `Attempt_threes_quiet_hours_cost_at_most_six_turns`
— two roles, no event for 4 h after the last report, paid review turns ≤ 6 (16 at base). Mutants to watch red and quote: no reset (always doubling) ⇒ (a) red on its reset half;
no decay ⇒ (a) and (e) red.
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; three suites 0 failed; `MissionLoopTests`,
`MissionEventTests`, `CouncilLoopTests` 3×; branch CI on all three platforms; tests box or NOT RUN with `ready`'s answer; names vs `main` 0 removed (both set sizes printed);
`## Report` ≤ 20 lines appended here. No push to `main`, no merge.
