# U-org-audit-a — the public audit line, part a: its own app identity outside the chart, its own file, a read-only reader of approved evidence only, and salted samples of accepted deliveries checked by code — no model, no money path, no rung
**READY (seat A, 2026-10-22 16:1x; drafted at main `efd8e399` — the builder re-checks every pointer against main at dispatch).** Survey: `fleet/tmp/A-org/org-audit/survey.md` (every pointer below re-checked there). SPLIT -a/-b/-c (cards at the end):
one pass cannot carry identity + store + reader + sampler + budget + traps + catch rates + brief + relay + model gate (~12 items); -a is buildable now and touches no money path.
**Arrow closed:** ORCHESTRATION § 2.13 (:366-371 — "app-owned, outside the chief's subtree"; "it reads approved evidence, never hidden reasoning; it samples deliveries"; "It can
neither instruct nor block"), § 3 (:403 "the audit line's … own tables, outside `org_*`"), § 8 (:471 measurement vs claim); ORGANISATION § 1 (:62), § 2's audit row (:98 — code first;
wakes on sampled deliveries), § 3 (:117 "not units of the organisation"), § 4 (:190 "outside every information grant"), § 13 items 5, 6 (:364-367); VISION § 5.3 (:167-171).
**Protections — measurement vs claim (the line's file is written only by code; every finding cites ids and carries no agent-supplied string), evidence (it reads approved
records only, on a read-only connection; never a transcript, a draft, a tool argument or the watcher's file), the line outside the organisation (no chart row; every org
verb refuses its id; no agent-reachable assembly holds its types; no org view carries its rows), "neither instruct nor block" (it writes nothing the organisation reads and
launches nothing), BUDGET-001 (it spends nothing: no launch path exists — part b brings the budget), append-only (triggers), refuse-never-guess (a newer `audit.db` refused
naming both versions, untouched), recovery (a crash between rung 1's statements and its stamp reopens), no terminal: red-first and ONE watched mutant.**
Seat A; **opus/high**, a protection unit. **No rung of `tradeagent.db`** (schema stays 44, `Versioning.cs:437`; 45 is seat B's `U-org-packets-b`): rung 1 of `audit.db`'s own ladder.
**Depends: O5 `U-org-assignments` (LANDED), `U-org-watcher-a` (LANDED `52a09706` — the pattern this copies).** Not on watcher-b (shares only `AppHost.cs`).
**Today (main efd8e399):** the chief heads the ROOT (ORG :116) — so "outside the chief's subtree" is outside the whole `org_unit` tree, and no chart row can hold the line. The app's
own principals are `referee`, `allocator`, `perception` (`AppPrincipals`, `OrgStore.cs:155-174`; `IsAppPrincipal` :544), refused as a minted position (`PositionIdRefusal`
:52-66), a mandate holder (`OrgStore.Mandates.cs:283-284`), a decider (`OrgStore.Decisions.cs:237`), a note's sender or recipient (:628, :632-633), an active position at the
pipe (`TradingGateway.cs:386-390`) and the meter (`TurnMeter.cs:1636`); the AI's turn counts skip them (`AiAttemptStore.cs:679-691`), and the watcher's spend does
(`Detectors.cs:136`). `PositionIdRefusal("audit")` answers null today — the id could be minted. Deliveries are sealed (`Deliver` `OrgStore.Mandates.cs:487-546`; id = SHA-256 of
the manifest :515, `Sha256Hex` :1114; triggers `Database.cs:2580-2583`, which an unconfined shell can drop — ORCH :165, ORG :382) and judged (`Accept` :701-708). Nothing
samples or audits them. The watcher's line is the model: own file and ladder (`OversightStore.cs:56-135`, triggers :207-225), a read-only `query_only` reader (`OrgRecords`,
`OversightSweep.cs:74-117`), its own timer from one AppHost field (`AppHost.cs:50`, :1074-1078, :2540), guards in `OversightInvisibilityTests`.
**Observable result:** `state/audit.db` exists after a start; each accepted delivery is sampled or not by a salted draw nobody in the organisation can predict; each sample
carries the app's re-checks (seal, refs, origin, acceptance) and every failing one is a finding citing ids; a sample a model reader would want is a `flag` row saying the audit
agent is NOT RUN (dormant). Nothing the organisation can read changes. NOT SHOWN to the owner until part c (the watcher-a precedent).
Items, one commit each, one-sentence messages:
1. **The identity.** `AppPrincipals.Audit = "audit"` joins `All` (`OrgStore.cs:173`) with its doc paragraph (the public audit line; code, never a position, outside the chart;
   ORCH § 2.13). Every refusal above then covers it; nothing else changes. (The manager, at landing: ORCH § 2.1 :110's list gains `audit`.)
2. **The line's file** (`TradeAgent.Core.Audit`, `AuditStore`): `state/audit.db` (path a constant of this class, not of `Paths`), ladder `audit_meta` key `schema` = 1 in one
   transaction with its version row; tables `audit_salt` (one row: 32 bytes from the OS generator, written once at create, never read by anything but the sampler),
   `audit_cursor`, `audit_sample`, `audit_check`, `audit_finding`, `audit_flag`, `audit_failure` (the line's own failures — never the activity or engineering log); every
   table but the version row refuses UPDATE and DELETE by trigger; a newer file refused naming both versions, nothing read or written.
3. **The register of approved evidence and its reader** (`AuditEvidence`): one closed list of the tables the line may read — `org_mandate`, `org_mandate_event`,
   `org_delivery`, `org_check`, `ai_attempt`, `ai_attempt_funding`, `ledger_entry`, `strategy_run`, `strategy_promotion`, `boundary_event`, `publication`, `org_unit`,
   `org_position` — on a read-only, `query_only` connection (the `OrgRecords` pattern). Excluded and named in its doc: seat-home transcripts (`Paths.cs:17`), workspaces and
   drafts (:22, :46), `tool_call.argument` (`Database.cs:825-835`), and `oversight.db` (a finding of this line reaches heads in part c, so it never rests on the watcher).
4. **The sample** (`AuditSampler`): a cursor over `org_mandate_event` rows of kind `accepted` (by id); a delivery is drawn when HMAC-SHA256(salt, delivery id)'s first
   8 bytes, as an unsigned integer, fall below `AuditPolicy.SampleRate` × 2^64 (default ¼, a named policy constant — the seat's ruling); one `audit_sample` row per draw
   (delivery, mandate, unit, cursor id, at), idempotent by its key, so a restart or a second sweep draws nothing twice. Rejected and unjudged deliveries are never drawn.
5. **The code checks on a sample**, each an `audit_check` row (name, passed, app-composed reason): `seal` — SHA-256 of the stored manifest equals the id, and the manifest's
   report, deviations and refs equal the row's columns; `refs-exist-now` and `refs-of-this-mandate-now` — the delivery's refs re-resolved through the register at audit time,
   independently of `OrgStore.Run` (:550-588); `accepted-clean` — no check of the accepted delivery failed unless it named a deviation. A failed check is an
   `audit_finding` (severity `severe` for `seal`, else `info`), citing the delivery, mandate and attempt ids only — no report text, no agent string. A sample with deviations
   and every check passed writes an `audit_flag` "a reader of the deviations is warranted; audit agent NOT RUN — dormant (no proven cell; activation is the owner's)". The
   overlap with the watcher's `accepted-failed-check` (`Detectors.cs:312`) is deliberate independence (VISION :170) — said in the class doc.
6. **The line** (`AuditLine`): its own `PeriodicTimer` (60 s; the first sweep one cadence after start), one AppHost field beside `_oversight` (start beside :1077-1078, dispose
   beside :2540); a file it will not open is held as `Refusal`, never told the activity log; it opens no file but the app's database (read-only) and `audit.db`.
Red-first (each red at base quoted): (a) `The_audit_principal_is_refused_wherever_a_position_is_named` — minted as a position (red: `PositionIdRefusal("audit")` is null), a
mandate to it, a note to and from it, a decision under it, an org verb under a grant in its name (by the read loop and the door) — each refused naming it a principal;
(b) `The_line_file_is_its_own_append_only_ladder_and_refuses_a_newer_build` (+ `A_crash_between_rung_one_and_its_stamp_reopens`); (c) `The_reader_reads_only_the_register_and_cannot_write`
— a write through it throws; every statement it issues names only registered tables; no source of `TradeAgent.Core.Audit` names `Paths.SeatHome`, `Paths.Workspace`,
`Paths.AgentHome`, `tool_call` or the watcher's namespace or file; (d) `A_delivery_rewritten_past_its_triggers_is_a_severe_finding` (drop the trigger, UPDATE the report, sweep at
rate 1); (e) `The_draw_is_salted_idempotent_and_survives_a_restart` (same salt, same draws; a second sweep adds none; another salt draws differently; rate 1 draws every accepted
delivery and no rejected one); (f) `A_sample_whose_refs_vanished_or_came_from_another_mandate_is_found` and `An_accepted_delivery_with_a_failed_check_and_no_deviation_is_found`;
(g) `A_flag_records_the_dormant_audit_agent_and_launches_nothing` (`ai_attempt` unchanged); (h) GUARD `The_sweep_leaves_the_organisations_database_and_logs_unchanged`
(watcher-a's (d) pattern, every table and every log file); (i) GUARD `No_agent_reachable_assembly_holds_the_audit_line` (watcher-a's (c) reflection pattern over Gateway,
AgentRuntime, TradeCli for `TradeAgent.Core.Audit` types and `audit.db`/`audit_` in their sources); (j) GUARD `No_org_view_answers_differently_when_the_line_has_run`
(watcher-a's (b) harness: identically seeded homes, one swept; timestamps masked).
**The watched mutant:** the `seal` check comparing the stored id with itself (the re-hash skipped) ⇒ (d) red. Quoted red, restored, green.
**Evidence beyond CI:** none needed — no UI, no model, no paid run; the tests box line per the gate. NOT VERIFIED stays: the timer in a live app; `audit.db` carries no
proving home-kind mark (add to `U-proving-ground-c`'s list beside `oversight.db`).
Out of scope: the budget, traps and catch rates (part b); the adversarial brief, findings to a unit's parent head, the owner's surface, audit agents (part c).
**Conflicts:** watcher-b (`AppHost.cs` :50 and its accessor), `U-execution-environment` (`AppHost.cs`), `U-org-packets-b` and `U-rewake-floor` (`AppHost.cs`; their
`OrgStore.Mandates.cs` hunks do not meet item 1's `OrgStore.cs` line) — whichever lands second rebases; watcher-a's guard (c) (`OversightInvisibilityTests`) re-run after the
rebase (item 1 changes three refusal sentences that list the principals: `OrgStore.cs:58`, `Mandates.cs:284`, `Decisions.cs:633` — any test pinning those words follows).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms; names vs `main` 0 removed; tests box
line). Codex review: write its prompt — "re-review HELD for the observed run" while it is live. `## Report` ≤ 20 lines appended here.

**Seat A's rulings (2026-10-22):** R1 the principal is named `audit`; R2 the sample rate is ¼ (spends nothing); R3 its own file `state/audit.db` with its own ladder, no `tradeagent.db` rung (the watcher's precedent); R4 findings shown NOWHERE until part c (the audit line is public; watcher-b's Oversight page is the owner's alone). Parts -b and -c stay CARDs (owner Q1–Q3 queued with the orchestrator).
