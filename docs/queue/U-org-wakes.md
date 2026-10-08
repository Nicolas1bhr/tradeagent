# U-org-wakes — heads wake on work and a maximum interval, members only for work, dormant positions never, and N positions share the serial loop
**RE-SCOPE BEFORE DISPATCH (R21, R22):** R21 re-scoped this unit to extend `U-reconcile-wakes` (objectives per role, a wake on a divergence, the packet) to positions; the text below is the pre-R21 scope — re-brief it as a CARD after `U-reconcile-wakes` lands, and never dispatch it as written.
**Arrow closed:** "never stopping is a scheduler, not a loop" for an organisation (`docs/COUNCIL.md`; `docs/ORGANISATION.md` §§ 2, 10; R12 § 5 O3; R17 #10;
R18 § 1.5; the factory plan's `U-worker-events` + `U-worker-scheduler`). **Depends on `U-org-rights`; land after `U-org-envelopes`** (both edit `MissionLoop`,
other methods) **and after `U-quiet-review`** (it changes `Schedule`'s review interval; R20, 2026-10-08). No schema change. **Today (SOURCE at `1275aff`, R12/R18-checked, NOT runtime-verified):** `MissionLoop.Schedule`
(`AgentRuntime/MissionLoop.cs:1853-1888`) writes a review (`:129`, from `MissionReviewMinutes`, `Trading.cs:419`, `AppHost.cs:674`) and renewal wakes, walking
`CouncilRoles.All` (`:1861`); selection (`:1476-1518`; `MissionEventStore.cs:476-486` `RolesDue` orders by `MIN(due_at), MIN(rowid)`; `DueFor` `:452-464`)
already serves the oldest due first and steps over turning, unaffordable and vendor-held roles (`MissionLoop.cs:1493, 1497-1502`); `RolesDue` has no status
filter; verdict and paper notes go to Research only (`Referee.cs:417`; `TradingGateway.cs:426, 1163`); the loop has no `OrgStore`; turns are serial (one
`LoopAsync`, `:1332, 1391`) and stay so here (parallel turns are `U-org-parallel`).
**Observable result:** the legacy pair keep their review as `U-quiet-review` left it (every `ReviewEvery`, backing off while only its own ticks wake them); every other head gets no timer review — it wakes on renewal, deliveries, the notes of item
4, and a maximum interval (default 24 h); members never get a heartbeat and wake only on a delivery addressed to them (until `U-org-assignments`, a
test-written delivery event); equal `due_at` is served by unit depth, then id; a dormant or ended position's due wakes are settled with a disposition, never
launched; verdict and paper notes ALSO reach the version's author when it is not one of the legacy pair; legacy routes and behaviour are unchanged.
Read first: `CLAUDE.md`; `docs/ORGANISATION.md` §§ 2, 10; R12 § 1 seams 7–10; R18 § 1.5; `MissionLoop.cs:100-135, 210-270, 1143, 1182-1192, 1222-1232,
1465-1520, 1767-1810, 1842, 1853-1888`; `MissionEventStore.cs:100-135, 180-200, 380-400, 450-520, 540-620`; `StrategyStore.cs:230`; `OrgStore` as landed.
Items, one commit each, one-sentence messages:
1. `Schedule` walks active positions: the legacy pair as today; other heads renewal + the maximum interval (`MaxHeadIntervalHours`, a setting, default 24);
   members nothing. The loop reads positions (id, heads-a-unit, status) through a new DEFAULT member of `IMissionHost` (as `Events`, `:262`) that answers the
   legacy pair as active heads, so the eight test hosts compile and `MissionLoopTests.cs:482-483` stays green; `AppHost.MissionHost` (`:1269`) answers from `OrgStore`.
2. Selection: oldest due first as today; ties by unit depth, then position id.
3. Dormant or ended position: its due wakes are settled in one transaction at no cost — a new `MissionEventDisposition` constant written with
   `SettleWithoutTurn` (`MissionEventStore.cs:601-617`), `consumed_by` a non-attempt marker like `MissionLoop.Unrecorded` (`:1842`).
4. Verdict and paper notes also go to the version's author (`strategy_version.role`, written at `StrategyStore.cs:230`) when it is not `operations`/`research`.
Red-first tests: (a) `A_head_position_is_scheduled_and_a_member_is_not` (red at base on its head half: `Schedule` walks only `CouncilRoles.All`); (a2)
`A_new_head_gets_only_its_maximum_interval`; (b) `A_dormant_positions_due_wake_is_settled_never_launched` (red: no status filter); (c)
`Three_positions_due_at_once_are_served_oldest_first_and_none_starves` (a GUARD for oldest-first; red only on its tie half); (d)
`A_verdict_note_reaches_the_non_legacy_author` (red: Research only); (e) `No_due_wake_means_no_paid_turn` (a GUARD, with a restart and a replay of consumed
events). Mutant to watch red and quote: a 30-minute review for every head ⇒ (a2) red.
Conflicts (R18 § 3): `U-decision-port` (W7, same wave) shares `TradingGateway.cs` (`:421-430`, `:1158-1167` against `:2340-2345`; L — rebase).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
