# U-org-envelopes — spend is reserved against the unit tree: a launch fits its unit and every ancestor inside the day's cap, in one transaction
**Protects:** spend — reserved before launch, unknown never zero, a cap is a cap (`CLAUDE.md`; `docs/COUNCIL.md` rules 3–4; `docs/ORGANISATION.md` §§ 3, 10, 13.4;
R12 § 4, § 5 O4; R17 #1, #11; R18 § 1.4). **Money-adjacent: red-first and mutants.** **Depends on `U-org-principals`** (exact keys). No schema change (the share
column came with `U-org-ledger`). Per-seat allowances are NOT in this unit: they are the CARD `U-org-seats`, because the harness enforces one role-blind bound
(`AppHost.cs:137-139` → `ApiConversation.cs:311`) and a reservation priced below what a turn may spend would walk past the cap (R18 § 1.4 row 1).
**Today (SOURCE at `1275aff`, R12/R18-checked, NOT runtime-verified):** `AiAttemptStore.Begin` reads the day's and the role's totals and inserts in one
`db.Write` (`Core/Db/AiAttemptStore.cs:256-281`; `Database.Write` holds one process lock and nests, `Database.cs:68-84`); the role filter reads a NULL role as
the chair (`COALESCE(role,$chair) = $role`, `:441-443`); the second gate is the role share, and today 1/N is `1 / CouncilRoles.All.Length` = 1/2 for ANY role
string (`Core/Trading.cs:297, 311-314`), read through `TurnMeter.RuleFor`/`Share` (`TurnMeter.cs:824-853`) — so a new position silently gets half the day;
every turn reserves one global 1.2 M in / 20 k out (`Trading.cs:689-691`) priced at the role's model (`TurnMeter.cs:881-897`).
**Observable result:** a position's launch is admitted only if, for its unit and EVERY ancestor to the root, the subtree's spent + unresolved reservations +
this reservation ≤ that unit's envelope — all read inside `Begin`'s one transaction, so two siblings cannot both take a parent's last room; the existing day-total
gate over every `ai_attempt` row stays first and unchanged, and a rule with no position (a NULL role, `perception`) keeps its `RoleCap`; with no envelope set,
the legacy `RoleShare` reads as the two divisions' shares and the legacy pair admits exactly as before; every launch still reserves the global allowance; a
refusal names the unit with no room and how much; a reservation larger than its unit's WHOLE envelope is refused with a sentence of its own.
Read first: `CLAUDE.md`; `docs/ORGANISATION.md` §§ 3, 10, 13; R12 § 4; R18 § 1.4; `AiAttemptStore.cs:108-162, 256-305, 312-349, 387, 425-454`;
`TurnMeter.cs:504-520, 824-897`; `Trading.cs:259-320, 460-468, 689-691, 871-872`; `AppHost.cs:622-708`; `BudgetReservationTests.cs:65-127`; `OrgStore.Ancestors`.
Items, one commit each, one-sentence messages:
1. `AiAdmissionRule` carries the ancestor chain (unit ids and envelopes resolved before the transaction); each ancestor's subtree totals are read INSIDE it;
   `Totals` gains a subtree form over the subtree's position ids, reading a NULL role as `operations` exactly as `:441-443` does, membership read inside the
   same `db.Write`; for a position the chain replaces `RoleCap`; the day-total gate stays first; non-position rules keep `RoleCap` (R17 #11).
2. Envelope resolution: `org_unit.envelope_share` × the parent's envelope; NULL on a legacy division ⇒ its `RoleShare` (today's behaviour); NULL elsewhere ⇒
   the parent's remaining room (no separate cap).
3. Refusals: the unit, its envelope and its room; a reservation above the unit's whole envelope gets its own sentence naming the seat, the reservation and the
   envelope — the sign a split cannot fund its own seat (R17 #1); `OrgStore` exposes the same check for `U-org-verbs` to refuse such a split.
Red-first tests: (a) `Two_sibling_teams_cannot_both_take_the_parents_last_room` — two threads as `BudgetReservationTests.cs:65-106`, through
`TurnMeter.Begin(prompt, role: <position>)` with org rows inserted by SQL (red at base), plus a sequential half: two rules built on an empty ledger, then two
`AiAttemptStore.Begin` calls (the `:112-127` pattern); (b) `A_unit_over_its_envelope_is_refused_while_its_sibling_launches`; (c)
`The_legacy_pair_admits_exactly_as_before` (a GUARD over the existing ledger tests' fixtures); (d) `A_lost_attempt_keeps_its_reservation_in_every_ancestor_across_a_restart`;
(e) `A_seat_whose_reservation_exceeds_its_units_whole_envelope_is_refused_by_name`; (g) `Non_position_spend_still_counts_against_the_daily_cap_under_the_tree`.
Mutants to watch red and quote: the subtree check removed ⇒ (a) red; subtree totals computed in `TurnMeter.RuleFor` and carried in `AiAdmissionRule` (the
defect `AiAttemptStore.cs:108-114` names) ⇒ (a)'s sequential half red.
Conflicts (R18 § 3): `U-features` (W6, CARD) — re-check at its briefing; `U-org-rights` (W6 light) must not touch `AiAttemptStore.Refuse`; `U-decision-port`
(W7) re-checks `RuleFor`/`Share`/`AiAdmissionRule` against this unit; `U-key-host-pin`'s `TurnMeter.Charge` zero branch is another method (L).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
