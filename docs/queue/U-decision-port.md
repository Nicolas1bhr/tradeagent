# U-decision-port — bounded decision models (Jev) behind one replaceable port, every call reserved, recorded, priced and pinned
**Arrow closed:** perception (`docs/EDGE-FACTORY.md` § 4.2, phase 4) — the substrate lenses and the annotator stand on; nothing here annotates automatically,
no agent can call it, and the owner's Perception card with its Test press is the follow-up `U-decision-card`. **Depends on `U-tape-store`** (rung 2 of `tape.db`,
written THROUGH `TapeStore`) **and `U-key-host-pin`**; **RE-BRIEF BEFORE DISPATCH (R22):** written to land after `U-org-principals`/`U-org-envelopes` (which key `Fence`, `Refuse`,
`ComposeRecovery` and the counters by exact position and keep `RoleCap` for a role that is no position), it now lands BEFORE them (R21): re-read at `main` how item 3 turns ALL of (e) green on today's role keys and how `Begin`'s role filter and share treat a role that is no council role; those two briefs carry `perception` forward. **Today:** nothing reaches a decision model (`git grep -i "systemone|typesafe|
openrouter" src` is empty); Core holds no HTTP client. **Observable result (connected mechanics, by test):** a call to the pinned instrument is reserved inside the
owner's daily AI cap and a perception budget before it is sent, recorded with what was asked and answered, settled with the billed or estimated cost, and
refused in words for a different answering model, an over-limit schema, an exhausted budget or a key bound to another origin — and no existing role, report or
card mistakes a perception call for an Operations turn.
Facts (R02 § 1, DOC 2026-10-02): `POST {base}/v1/systemone` (OpenRouter `{base}/api/v1/systemone`); `{model, state, questions}` → `{model, answers, usage}` (+ `usage.cost`
on OpenRouter); pins TypeSafe `jev-1.13.0`, OpenRouter `typesafe/jev-1.13`; Choice ≤ 255, Score 2–10 (schema enforces neither); 64K / 32K budgets (OpenRouter 32K);
$0.042 per M input, output free; Bearer; 429/529; answers not repeatable; no C# SDK (hand-write against OpenAPI v0.2.0, no unofficial NuGet).
Read first: `docs/EDGE-FACTORY.md` § 4.2, § 6.7, § 6.10; R02 § 2 (never distil or imitate); `CLAUDE.md`; the `HarnessKey` holder as `U-key-host-pin` left it;
`Core/Db/AiAttemptStore.cs:119-162` (`AiAdmissionRule`), `:256-281` (`Begin`: totals and INSERT in ONE `db.Write`), `:297-299` (`Refuse`), `:325-340` (`End`),
`:425-445` (day counters, `Totals` `:441`); `AgentRuntime/TurnMeter.cs:380-395` (`LiveAttempts.Shared`), `:570-580` (`LoseOpen`), `:820-860` (`RuleFor`, `Share`,
private `LocalDay`/`Midnight`), `:1050-1060` (the "estimated" card); `CouncilRelay.cs:495-505` (`Fence`); `DailyReports.cs:875-885` (`ComposeRecovery`);
`TradingGateway.cs:2340-2345` (`AiTurnsToday`); the `TapeStore` ladder; `RuntimeManifest.cs:384,495-686`; `FakeProvider.cs`; `SuiteReachesNoVendorTests.cs:40-66`.
Items, one commit each, one-sentence messages:
1. `IDecisionModel` and its request/result types in Core (no HTTP there): `SchemaRef(id, version, sha)`, canonical-JSON state, typed questions, instrument id;
   every unrounded distribution, the answered model id, usage, client-measured latency, status, error class. Limits checked BEFORE sending. The `TypeSafeWire`
   adapter lives in Provisioning beside the other HTTP clients; built-in instruments `typesafe-direct` and `openrouter-jev` with per-host pins, dated price,
   limits and doc URL; hosts only from built-ins (any override may change prices or limits, never an origin); its own key holder under the origin rule.
2. Spend: `Begin` with `AiAdmissionRule { Role = "perception", Cap = the owner's daily AI cap, RoleCap = new setting PerceptionDailyBudget (default 1 USD) }`,
   From/To/ResumesAt from the owner's local day (move `LocalDay`/`Midnight` into one shared helper); register the launch in `LiveAttempts.Shared` so
   `LoseOpen` does not declare it LOST; settle with the answered model id and the billed cost when the host reports it, else the estimate; a lost answer keeps its reservation.
3. `perception` is a meter role, never scheduled and never a council role. Fix every reader that would say otherwise: `Refuse` names the perception budget, not
   the Operations Director; `CouncilRelay.Fence` QUARANTINES an attempt whose role is not a council role instead of reading it as Operations; `ComposeRecovery`
   labels it perception; the day counters and `AiTurnsToday` count it apart from council turns; a settled perception row never flips the council cost card to
   "estimated".
4. Record: `decision_call` (tape.db rung 2, written through `TapeStore`) keyed by the attempt id — instrument, origin, requested and answered model id, schema sha,
   state sha, instants, latency, tokens, estimated and billed cost apart, status, `UNPINNED` (answered ≠ pin ⇒ excluded from evidence), the full answers.
   `CONTRACTS.md` (claimed: what was asked and answered, by which instrument, at what cost; NOT claimed: repeatability, calibration, correctness; never used to
   distil or imitate Jev), `docs/RESEARCH-REQUIRED.md`; add `api.typesafe.ai` and `openrouter.ai` to `SuiteReachesNoVendorTests`.
Tests (loopback host serving canned TypeSafe JSON): (a) `An_answer_from_a_model_other_than_the_hosts_pin_is_recorded_unpinned`; (b) `Limits_are_refused_before
_sending`; (c) `A_call_is_reserved_in_the_main_database_before_it_is_sent_and_a_lost_answer_keeps_it`; (d) `The_perception_budget_and_the_daily_cap_bind_in_one
_transaction`; (e) `A_perception_attempt_is_never_read_as_an_operations_turn` (`Fence`, `Refuse`, `ComposeRecovery`, counters); (f) `The_key_goes_only_to_its
_pasted_origin_and_is_never_written`; (g) `No_pipe_op_reaches_the_decision_port`. Mutants to watch red and quote: (i) the pin comparison removed ⇒ (a) red;
(ii) the reservation moved after the send ⇒ (c) red (the loopback host reads the ledger when the request arrives); (iii) `Fence` back on `CouncilRoles.Or` ⇒ (e) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
