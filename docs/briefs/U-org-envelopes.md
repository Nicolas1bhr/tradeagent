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

## Report
Tip: code `04056b78` on `c8004ffc` (rebased once on seat B's word; first CI 38172171124 cancelled), four item commits b054e803 → 04056b78; this report commit after. Codex review (not acted on): `fleet/tmp/B-scratch/u-org-envelopes/U-org-envelopes-review.md` (diff base `0efd6a30`, the rebased base, not b211fbb3).
Gates: 1. Release `--no-incremental`: 0 Warning(s), 0 Error(s). 2. 28 classes (new OrgEnvelopeTests; touched BudgetReservationTests, OrgLedgerTests; meter, decision-port, org and every DatabaseSchemaVersion ladder class) 3× after the rebase: `Passed! - Failed: 0, Passed: 309` ×3. 3. CI 38173080347 on 04056b7: success, all 12 jobs (windows ×5, macos, ubuntu, linux-host, packages). 4. names vs main: `removed: 0`, base 2604, tip 2616.
Item 1 (b054e803): rung 38, `ai_attempt_funding(attempt_id, unit_id, depth)`, written by `Begin` in the row's commit from `AiAttempt.Funding`; `OrgStore.FundingLine` (one snapshot; no position, a missing unit, a loop or a line not ending at the root throws); `TurnMeter.Begin` refuses an unreadable chain (`Labels.FundingChainUnreadable`, Ceiling `AiAdmission.Chart`), no row, no wake; the owner's recorded (unadmitted) turn records its chain where readable.
Item 3 (463649cb): `OrgEnvelopes.Resolve` — root = day cap; share × nearest envelope above; NULL on div-operations/div-research = its position's `ShareForRole` × cap; NULL elsewhere = no separate cap; a share not in [0,1] (invariant, no exponent) is damage → refused.
Item 2 (c32e879a): `AiAdmissionRule.Funding` (ids + envelopes, no totals); in `Begin`: day gate first and unchanged, then each unit's spent + reserved over the rows its recorded chain names, read in the transaction; the rule's chain must equal the attempt's or the write throws (→ ledger refusal); `Totals` gains the unit form, count split kept; rules with no chain (perception) keep RoleCap. `RuleFor` no longer reads `Share(role)` for a position.
Item 4 (04056b78): unit refusal names unit, envelope, room (Ceiling = unit id); a turn above the unit's whole envelope gets `Labels.SeatExceedsEnvelope` (seat, reservation, envelope) via `OrgStore.Unfundable`, the check exposed for U-org-verbs; `:303`'s `CouncilRoles.Or` fold removed.
RED at base b211fbb3 (Release): (a) threads `Assert.Single() … contained 2 matching items`; (b), (d), (e) `Assert.False() Failure`; (g), (j) `no such table: ai_attempt_funding`; (h) `pos-bad was admitted on a chain nobody could read`. (c) is a GUARD, green at base (5/5) and after. (a)'s sequential half needs the new rule API: red under the mutants only.
Mutants: subtree check removed ⇒ (a) both halves red (`contained 2 matching items`; `Assert.False() Failure`); totals carried in the rule from `RuleFor` ⇒ (a) sequential `Assert.False() Failure` (threads too); unreadable chain admitted on the day gate ⇒ (h) `pos-bad was admitted on a chain nobody could read`.
Rung 38: one table + index (CREATE IF NOT EXISTS) and one backfill statement over rows with no chain yet (NULL → operations' chain, a position → its unit's chain as the chart stands, app principals and non-positions none), ON CONFLICT DO NOTHING; test drops the table at stamp 37 and reopens (filled), then moves the chart and re-stamps 37 (nothing re-derived, 9 rows, stamp = build).
Tests box: GREEN on 11ed2669 (this code before the docs/c8004ffc rebase) — unit 1792/1792, fault 498/498, integration 692 + 1 not run, timing green; his files unchanged.
Declared: `BudgetReservationTests.A_roles_share_refuses_the_launch_while_the_day_still_has_room` now asserts Ceiling `div-research` and the whole-envelope sentence (item 4's intent; Over 0.28 unchanged); `OrgLedgerTests.No_pipe_op_writes_an_org_table`'s OrgStore surface list gains FundingChain, FundingLine, Unfundable (reads / a pure check, no writer). A NULL-role launch records the chair's chain with no envelopes (day gate only, as before) so the legacy split still counts it.
NOT done: `TurnMeter.TodayFor`/the card's cheap first look still reads the role split (½ for a new position) — the gate is Begin; moot until activation lets a third position run. NOT verified: nothing beyond the lines above.

## Fix report (pass 1)

- 52e821d5: one admission reads the day's cap once and sizes both its unit envelopes and its rule's cap from that value (P1).
- f6968f0a: rung 38 records every funding chain whole; a looping chart refuses the rung in one rolled-back transaction, home left at 37 (P2).
- Unit project run 3x (logs show TradeAgent.UnitTests.dll only, no class filter recorded): run1 Passed 290, Failed 0; run2 Passed 290, Failed 0; run3 Passed 290, Failed 0.
- CI 38176161802 (pre-rebase fix tip 468fdec4): success. Jobs all success: dedupe-gate, shard-plan, package, package-linux, linux-host, test ubuntu-latest, test macos-latest, test windows-latest 1-5.
- Landing CI 38933247781 on the rebased tip f6968f0a is in flight; not yet verified.
- Re-review, "No remaining defect or introduced regression found in these two fixes." (/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/fleet/tmp/B-scratch/u-org-envelopes-fix1/fix1-review.md)
- RED-before and mutant lines for the two fixes: NOT in the surviving logs — NOT VERIFIED by this clerk.
- Written by a clerk from the fixer's logs; the fixer's own gate output beyond those logs is NOT VERIFIED.
