# U-decision-port — bounded decision models (Jev) behind one replaceable port, every call reserved, recorded, priced and pinned
**Arrow closed:** perception (`docs/EDGE-FACTORY.md` § 4.2, phase 4), the substrate lenses and the annotator stand on; nothing here annotates and no agent can call it; the owner's Perception card (key,
budget, Test press) is `U-decision-card`. **Protects (EDGE § 6):** 3 measurement vs claim; 7 spend, reserved in `AiAttemptStore`'s one transaction under the daily AI cap, unknown never zero; 10 a key goes
only to its built-in origin. Money-adjacent: red-first, mutants. Seat B. **Depends on `U-tape-store`** (merge `c8fc2cc`, `BUILD-STATUS.md:7291`; `tape.db` rung 2 is this unit's) **and `U-key-host-pin`**
(`3065e39`, `:7134`), both landed; **lands BEFORE `U-org-principals` (O2a) and `U-org-envelopes` (O4)**, on today's role keys (§ 15; R22), and before `U-billing-classes` (R18 § 3). Main schema 30 stays.
**Facts (SOURCE at `eb906b17`, read and not run; DOC read 2026-10-09).**
- Nothing reaches a decision model (`git grep -i "systemone|typesafe|openrouter" src` is empty); Core holds no HTTP client. The one keyed sender is `AgentRuntime/ApiConversation.cs:252-293`: the key leaves
  `Security/HarnessKey.cs` only by `ReadFor(origin)` (`:113-126`; rule `Core/UrlOrigin.cs:31-46`). Provisioning references neither Security (keys) nor AgentRuntime (`LiveAttempts`).
- `perception` is `AppPrincipals.Perception` (`Core/Db/OrgStore.cs:121-126`). `AiAttemptStore.Begin` (`:256-281`) totals the day over EVERY row and a named role by exact match (`:441`): a perception rule binds
  the cap and its own budget, no council share reads it. `TurnMeter` keys by `CouncilRoles.Or` (`:1173`; it would take the chair's slot, `:836-838`) and halves the day for a role with no `RoleShare` (`:873-902`).
- Misread today: `Fence` (`CouncilRelay.cs:500` publishes it as Operations'), `Refuse` (`AiAttemptStore.cs:297-299`: "The Operations Director has used its share"), `ComposeRecovery` (`DailyReports.cs:945`), and
  `Totals`' role-less counts (`:431-439`) behind the card (`App/DashboardView.cs:479-496`; label `TurnMeter.cs:1105`), `AiTurnsToday` (`App/AppHost.cs:955-970`), the Situation, the report (`DailyReports.cs:587-596`).
- `tape.db` is at rung 1 (`TapeStore.cs:64`, `:167-218`; `Write` `:707-716`); `TapeReader.cs:145` wants the exact version; the app holds one store (`AppHost.cs:508, 625`), null when refused.
- DOC: TypeSafe (docs.typesafe.ai/api, /models, OpenAPI 3.1.0 v0.2.0) `POST https://api.typesafe.ai/v1/systemone`, Bearer, `{model, state, questions{name: {type, instructions, criteria}}}` → `{model,
  answers, usage{input_tokens, output_tokens}}`; `jev-1.13.0` answers as itself; $0.042/M input, output free; 64K a request, 32K state + longest question; 100K tokens and 80 requests a second; 401, 422, 429
  (`retry-after`), 529; Choice ≤ 255 and Score 2–10, unenforced by the schema; ~300 tokens billed for 70 bytes. OpenRouter `POST https://openrouter.ai/api/v1/systemone`, same shapes + `id`, `provider`,
  `usage.cost`; asked `typesafe/jev-1.13`, it answers `typesafe/jev-1.13-20260917`; 32K prompt (model page: 64K). No C# SDK, no unofficial NuGet. MCA § 2.3(b): never distil or imitate.
Read first: `CLAUDE.md`; EDGE §§ 4.2, 6; R02 §§ 1–2, 6.1–6.4; `docs/CONTRACTS.md:2320-2343` (the holder rule); the files above; `FakeProvider.cs`; `SuiteReachesNoVendorTests.cs:40-97, 143-188`.
Must NOT: route perception through `TurnMeter` or give it a `RoleShare`, a schedule, a home or an AGENTS.md line; touch `MissionLoop`, `MissionEventStore`, `WorkspaceBuilder` or `TradingGateway`; send an
alias or OpenRouter's alpha Decisions API; send twice on one reservation; keep the state's text (its sha and source refs only); call with no `TapeStore` open; add a main rung.
Items, one commit each, one-sentence messages:
1. Port: `IDecisionModel` and its types in Core (no HTTP): `SchemaRef(id, version, sha)`, canonical-JSON state, typed questions, instrument id; unrounded distributions, answered model id, usage, host
   response id, latency, status, error class; structural limits refused BEFORE sending. `TypeSafeWire` in AgentRuntime beside `ApiConversation`; built-in `typesafe-direct` and `openrouter-jev`, each with origin,
   request id, answered-id pin, dated price, limits, doc URL (an override may change prices or limits, never an origin); its key holder a second `HarnessKey` (never `.Shared`), read at the send.
2. Spend: `AiAttemptStore.Begin` with `AiAdmissionRule { Role = AppPrincipals.Perception, Cap = the daily AI cap (one delegate, as `AppHost.cs:750`), RoleCap = new setting PerceptionDailyBudget (1 USD; 0 in
   `Unreadable()`), Reservation = 64K tokens × the dated input price }`; From/To/ResumesAt from `LocalDay`/`Midnight` moved from `TurnMeter` (`:1129-1138`) to one Core helper; held in `LiveAttempts.Shared` until
   settled with the answered id and the host's billed cost, else input tokens × the dated price (`pricing_basis` says which); no answer keeps the reservation.
3. Readers: `Fence` compares `a.Role ?? CouncilRoles.Default` exactly, so a perception id is quarantined in every pass; `Refuse` names the perception budget (a new label); `ComposeRecovery` calls it a
   perception call; `Totals`' role-less counts (turns, unpriced, estimated, open, unreported) skip app principals, spent and reserved keep them; the report's basis line names perception's billed cost.
4. Record: `decision_call` (tape.db rung 2, `Schema` 2, one `TapeStore` transaction) keyed by the attempt id — instrument, origin, requested and answered id, host response id, schema and state sha, instants,
   latency, tokens, estimated and billed cost apart, status, `UNPINNED` (answered ≠ the pin ⇒ out of evidence), the full answers — written BEFORE the attempt settles; `TapeStoreTests.cs:341-409` and
   `TapeReadTests.cs:74-81` to `Schema`/`Schema + 1`, `TapeOverPipeTests.cs:362` dumps it; `CONTRACTS.md` (claimed: asked, answered, by which instrument, at what cost; NOT: repeatability, calibration,
   correctness; never to distil or imitate Jev); `RESEARCH-REQUIRED.md` § D2 (your re-read of the DOC); the vendor scan adds `api.typesafe.ai`, `openrouter.ai` and an adapter file rule like `:184-188`.
Tests (loopback host, canned TypeSafe JSON; `FakeProvider` gains a hook run as a request arrives): (a) `An_answer_from_a_model_other_than_the_hosts_pin_is_recorded_unpinned` (per instrument); (b)
`Limits_are_refused_before_sending`; (c) `A_call_is_reserved_in_the_main_database_before_it_is_sent_and_a_lost_answer_keeps_it`; (d) `The_perception_budget_and_the_daily_cap_bind_in_one_transaction` (a council
share unchanged); (e) `A_perception_attempt_is_never_read_as_an_operations_turn` (`Fence`, `Refuse`, `ComposeRecovery`, counters, the card's label, `AiTurnsToday`; red at base on a seeded row); (f)
`The_key_goes_only_to_its_pasted_origin_and_is_never_written`; (g) `No_pipe_op_reaches_the_decision_port` (nor its budget, pin or key). Mutants, red and quoted: (i) no pin comparison ⇒ (a); (ii) the
reservation after the send ⇒ (c), the hook reading the ledger; (iii) `Fence` back on `CouncilRoles.Or` ⇒ (e).
Gate and report per `docs/HOW-WE-BUILD.md` and `docs/FLEET.md` "The builder pass": rebase on `main` first; `--no-incremental` Release 0 warnings; three suites 0 failed; touched classes 3×; branch CI on
all three platforms; tests box or NOT RUN with `ready`'s answer; names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
