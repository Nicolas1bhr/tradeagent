# U-org-ledger — the organisation becomes app-minted data: a root, two divisions and the two legacy positions, written by the app, read by nothing yet
**Arrow closed:** the organisation plane's substrate (`docs/ORGANISATION.md` §§ 3, 15; R12 § 5 O1; the factory plan's `U-worker-identity`, first half).
**Schema: rung 28 (`U-cost-model`'s 27 is on `main` at `afd1bb6`); if `main` gained it first, renumber on rebase — the ladder stays
contiguous (R12's numbers predate this, R18 § 2).** **Today (SOURCE re-checked at `c44425a` by seat P at dispatch, R12/R18-checked, NOT runtime-verified):** `Versioning.DatabaseSchemaVersion
= 27` (`Core/Versioning.cs:282`, `U-cost-model`'s rung); the ladder is `if (have < N)` blocks in `Database.Migrate` (`Core/Db/Database.cs:103`; `have` read once at `:106`; rungs in
autocommit, `:37-42`; the last rung, 27, `:1607`); "role" is unconstrained TEXT on eight columns (`Database.cs:521, 522, 526, 540, 630, 658, 707, 956`) and on every
launch grant (`Security/AgentGrants.cs:147`); historical values also include the app principals `referee` (`Strategy/Referee.cs:96`, written `:396`, `:509-510`) and
`allocator` (`TradingGateway.cs:278`); the two roles are constants (`Core/Council.cs:32-41`); nothing models a unit, parent, head, seat or envelope.
**Observable result:** after the upgrade the database holds the root unit `org` (head NULL: the owner, in-process), divisions `div-operations` (head
`operations`) and `div-research` (head `research`), and two positions whose ids ARE the legacy role strings with homes `agent` and `research` — so no historical
row changes meaning; the app is the only writer (the rung's seed now, `OrgStore` from `U-org-verbs`); no op writes it; no behaviour changes.
Read first: `CLAUDE.md`; `docs/ORGANISATION.md` §§ 2–3, 13–14; R12 §§ 2, 5, 6; R18 § 1.2; `Database.cs:30-110` and the rung 25/26/27 blocks (`:1431`, `:1511`, `:1607`);
`Council.cs` (whole); `Core/Db/BoundaryStore.cs` as a store precedent; `docs/CONTRACTS.md` (its ledger sections); `BacktestLedgerTests.cs:195-231`.
Items, one commit each, one-sentence messages:
1. The rung, TEXT kinds validated by the store, never an enum: `org_unit` (id PK, parent_id NULL only for the root, kind ∈ root/division/subdivision/team/
   staff, head_position_id NULL, status ∈ chartered/active/dormant/closed, envelope_share NULL — a decimal string, the AI-spend envelope, NOT `paper_envelope`,
   and CONTRACTS says so — charter_publication_id NULL, created_at, closed_at); `org_position` (id PK, unit_id, home_dir, status ∈ active/dormant/ended,
   seat_runtime, seat_model, seat_allow_in, seat_allow_out — all NULL = today's behaviour, created_at, ended_at); `org_event` (id INTEGER PK, at, unit_id,
   position_id NULL, kind, decider, detail) — append-only; `detail` holds app-composed ids, numbers and fixed words only, never agent text. Seed the root, both
   divisions and both positions in the SAME step with fixed ids and `ON CONFLICT DO NOTHING`, and one `org_event` per seed with decider `app`, inserted
   `WHERE NOT EXISTS` a seed event for that id; only the root has kind `root`.
2. `OrgStore` (Core/Db): reads — `Units()`, `Positions()`, `Position(id)`, `UnitOf(positionId)`, `Ancestors(unitId)`, `Subtree(unitId)`, `IsAppPrincipal(role)`
   (`referee`, `allocator`; the queued `perception`) — and NO public writer yet. The five seeded ids are the only fixed ones; later ids are random.
3. Append `DROP TABLE org_event; DROP TABLE org_position; DROP TABLE org_unit;` to the roll-back lists at `VenueCatalogTests.cs:214-252` and
   `PaperEligibleVerdictTests.cs:261-278` (they undo every rung above by an explicit list and reopen), rewritten in place, names kept.
4. `docs/CONTRACTS.md`: the organisation ledger — rows are app measurement; titles and charters will be publications (claims); app-only writers; NOT claimed:
   protection from a CLI agent that writes `state/` directly (advisory until containment, `docs/ORGANISATION.md` § 14).
Red-first tests: (a) `An_upgrade_seeds_one_root_two_divisions_and_the_two_legacy_positions` — on `TestEnv.NewDb()` and on a file stamped back by raw SQL as
`PaperEligibleVerdictTests.cs:251-296` does; quoted red at base (no tables); (b) `Every_role_value_today_s_stores_write_is_a_position_or_an_app_principal` —
the eight columns' distinct non-NULL values map to a seeded position or to `referee`/`allocator`; (c) `Rerunning_the_ledger_rung_seeds_nothing_twice` — upgrade,
write the previous rung's number into `meta`, reopen: one row and one `org_event` per seed; (d) `No_pipe_op_writes_an_org_table` (a GUARD over
`GatewaySchema.Ops()`, as `RefereeVerdictTests.cs:519-544`); (e) `The_organisation_ledger_arrives_at_its_rung` (the `BacktestLedgerTests.cs:195-231` pattern).
Mutant to watch red and quote: drop the `WHERE NOT EXISTS` on the seed events ⇒ (c) red.
Light leg (W2): no queued brief shares its code; `U-cost-model` shares the ladder (rung before) and the roll-back lists (append in landing order).
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
