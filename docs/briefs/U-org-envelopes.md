# U-org-envelopes — spend is reserved against the unit tree: a launch fits its unit and every ancestor inside the day's cap, in one transaction
**Protects:** spend — reserved before launch, unknown never zero, a cap is a cap (BUDGET-001, `docs/ORCHESTRATION.md` §§ 2.4, 8; `CLAUDE.md`; `docs/COUNCIL.md` rules 3–4; `docs/ORGANISATION.md` §§ 3,
10, 13.4; R12 § 4, § 5 O4; R17 #1, #11; R18 § 1.4). **Money-adjacent: red-first and mutants.** Opus, high. **Depends on `U-org-principals`** (exact keys; `OrgStore.Standing`). **Rebases over
`U-admission-fail-closed`** (in flight, `docs/briefs/`): `TurnMeter.Begin`'s catch (`TurnMeter.cs:874`) becomes a refusal with a ledger ceiling, and `AiAdmission.Unrecorded` (`AiAttemptStore.cs:176`)
is renamed to an explicit no-ledger value — this unit uses whatever names that unit landed and keeps its rule work separate: it touches neither that catch nor the callers' fallbacks.
**`perception`'s rule (U-decision-port, landed) keeps working:** built by the decision port (`TypeSafeWire.cs:294-303`), never by `TurnMeter.RuleFor`; `Cap` the day's, `RoleCap` the perception budget
(`Trading.cs:550`); it carries no ancestor chain: perception spend counts in the day-total gate and in no subtree, and `Totals`' role-less count split (`AiAttemptStore.cs:453-457`) survives the
subtree form (R22). Per-seat allowances are NOT here: they are the CARD `U-org-seats`, because the harness enforces one role-blind bound (`AppHost.cs:186-188` → `ApiConversation.cs:363`) and a
reservation priced below what a turn may spend would walk past the cap (R18 § 1.4 row 1). AI spend only: trials and verdicts are reserved at the broker call that spends them, and a mandate's
allocation is `U-org-assignments`' (§ 2.4). **Schema: ONE rung, number 38** (the orchestrator, 2026-10-11: recording the funding chain at admission is what § 2.4's immutable lineage means; rungs in LANDING order — if this unit is ready to land while another unit's rung 38 is on main, renumber at rebase and say so).
**Today (SOURCE at `9b6298db`; code unchanged since `fd7174ee`, re-grepped at `3cfb6ef7`; NOT runtime-verified):** `AiAttemptStore.Begin` reads the day's and the role's totals and inserts in one
`db.Write` (`Core/Db/AiAttemptStore.cs:256-281`; `Database.Write` holds one process lock and nests, `Database.cs:81`); the role filter reads a NULL role as the chair (`COALESCE(role,$chair) =
$role`, `:457`); the second gate is the role share, `ShareForRole` = the chosen fraction or `1 / CouncilRoles.All.Length` = 1/2 for ANY role string (`Core/Trading.cs:297, 310-314`), read through
`TurnMeter.RuleFor`/`Share` (`TurnMeter.cs:905-934`, fallback `:933`; wired `AppHost.cs:839`) — so a new position silently gets half the day; every turn reserves one global 1.2 M in / 20 k out
(`Trading.cs:512-515`, `TurnAllowance.Default` `:793`) priced at the role's model (`TurnMeter.Reservation`, `:985-1000`). `org_unit.envelope_share` exists (TEXT, seeded NULL, `Database.cs:1723,
2315`) and is read by nothing; `OrgStore.Ancestors` (`OrgStore.cs:232`) answers `[]` for an id that is no unit. A role-share refusal names `CouncilRoles.Title(Or(role))` (`AiAttemptStore.cs:303`).
**Observable result:** a position's launch is admitted only if, for every unit of its funding chain to the root, the units' spent + unresolved reservations + this reservation ≤ that unit's envelope —
all read inside `Begin`'s one transaction, so two siblings cannot both take a parent's last room; the day-total gate over every `ai_attempt` row stays first and unchanged; a rule with no position (a
NULL role, `perception`) keeps its `RoleCap`; with no envelope set, the legacy `RoleShare` reads as the two divisions' shares and the legacy pair admits exactly as before; every launch still reserves
the global allowance; a refusal names the unit with no room and how much; a reservation larger than its unit's WHOLE envelope is refused with a sentence of its own; a position whose chain cannot be
read is REFUSED, never admitted on the day gate alone (fail-closed, § 2.4); a past charge never moves when the chart does (§ 2.4, "funding lineage is immutable").
Read first: `CLAUDE.md`; `docs/ORCHESTRATION.md` §§ 2.4, 3, 8; `docs/ORGANISATION.md` §§ 3, 10, 13; R12 § 4; R18 § 1.4; `AiAttemptStore.cs:100-200, 256-310, 391-470`; `TurnMeter.cs:575-600, 820-878,
905-934, 985-1000`; `Trading.cs:285-315, 508-516, 534-555, 786-800`; `AppHost.cs:826-850`; `TypeSafeWire.cs:280-320`; `OrgStore.cs:178-270`; `BudgetReservationTests.cs:65-175`.
Items, one commit each, one-sentence messages:
1. The funding chain (Q2): resolved before the transaction from `OrgStore.Standing` + `Ancestors`; an unreadable or empty chain for a position is a refusal in words; recorded with the row INSIDE
   `Begin`'s transaction (one row per unit, e.g. `ai_attempt_funding(attempt_id, unit_id)`), the rung filling the home's existing rows from their role (NULL → `operations`; app principals none).
2. `AiAdmissionRule` carries the chain (unit ids and envelopes resolved before the transaction); each unit's totals over the attempts it funded are read INSIDE it; `Totals` gains that form, keeping
   the role-less count split; for a position the chain replaces `RoleCap`; the day-total gate stays first; non-position rules keep `RoleCap` (R17 #11).
3. Envelope resolution: `org_unit.envelope_share` (a fraction of the parent's envelope in the same currency and period, never an amount, § 2.4) × the parent's envelope; the root's is the day's cap;
   NULL on a legacy division ⇒ its `RoleShare` (today's behaviour); NULL elsewhere ⇒ the parent's remaining room (no separate cap).
4. Refusals: the unit, its envelope and its room (`Ceiling` = the unit's id, beside `Day` `:195` and `U-admission-fail-closed`'s ledger value; `:303`'s folded title replaced by the unit's name); a
   reservation above the unit's whole envelope gets its own sentence naming the seat, the reservation and the envelope — the sign a split cannot fund its own seat (R17 #1); `OrgStore` exposes the
   same check for `U-org-verbs` to refuse such a split.
Red-first tests: (a) `Two_sibling_teams_cannot_both_take_the_parents_last_room` — two threads as `BudgetReservationTests.cs:65-110`, through `TurnMeter.Begin(prompt, role: <position>)` with org
rows inserted by SQL (red at base), plus a sequential half: two rules built on an empty ledger, then two `AiAttemptStore.Begin` calls (the `:112-131` pattern); (b)
`A_unit_over_its_envelope_is_refused_while_its_sibling_launches`; (c) `The_legacy_pair_admits_exactly_as_before` (a GUARD over the existing ledger tests' fixtures); (d)
`A_lost_attempt_keeps_its_reservation_in_every_ancestor_across_a_restart`; (e) `A_seat_whose_reservation_exceeds_its_units_whole_envelope_is_refused_by_name`; (g)
`Non_position_spend_still_counts_against_the_daily_cap_under_the_tree`; (h) `A_position_whose_funding_chain_cannot_be_read_is_refused`; (j) `A_past_charge_stays_with_the_chain_it_was_admitted_under`.
Mutants to watch red and quote: the subtree check removed ⇒ (a); subtree totals computed in `TurnMeter.RuleFor` and carried in `AiAdmissionRule` (the defect `AiAttemptStore.cs:108-114` names) ⇒
(a)'s sequential half; an unreadable chain admitted on the day gate ⇒ (h).
Conflicts (re-check at dispatch): `U-admission-fail-closed` (above); `U-org-rights` (∥) must not touch `AiAttemptStore.Refuse`; `U-decision-port` landed — re-check its `AiAdmissionRule` use, its
`Refuse` branch (`:301-302`) and the count split, and keep its tests (d) and (e) green, names kept; `U-features` landed (`90aece2a`); `U-billing-classes` (CARD) adds currencies after this.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three platforms; tests box `ready`
once or NOT RUN; names vs `main` 0 removed (both set sizes printed); the rung ladder's check green. `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
