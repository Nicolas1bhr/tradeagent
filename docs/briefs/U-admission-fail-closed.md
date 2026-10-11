# U-admission-fail-closed — a launch whose admission cannot be written is refused in words, never run unrecorded
**Protects:** spend — reserved before launch, with no exception; a cap is a cap; unknown is never zero (`CLAUDE.md`; `docs/COUNCIL.md` rules 3–4;
`docs/ORCHESTRATION.md` § 2.4 first bullet; R23 finding A04). **Money-adjacent: red-first and ONE watched mutant.** No schema change. Light.
**Today (SOURCE at `main` `9b6298db`, NOT runtime-verified):** `TurnMeter.Begin` wraps `AiAttemptStore.Begin` and turns ANY exception into
`AiAdmission.Unrecorded` (`src/TradeAgent.AgentRuntime/TurnMeter.cs:858-874`), whose `Admitted` is true (`src/TradeAgent.Core/Db/AiAttemptStore.cs:164-181`,
the doc comment says so on purpose: "the turn still runs"). So a database that will not take the row starts a paid process the day's cap never sees, and the
mission loop then even spends the wake as `unrecorded` (`MissionLoop.cs:1851-1860`). Other fail-open edges of the same value: `AppHost.cs:1700-1708`
(`host.Meter?.Begin(...) ?? AiAdmission.Unrecorded` — no meter ⇒ run), `AgentSession.cs:337` and `ApiConversation.cs:200` (`Admit?.Invoke(...) ??
AiAdmission.Unrecorded`), and the `IMissionHost.BeginTurn` default (`MissionLoop.cs:436-448`). The decision port already fails closed and is the model:
`TypeSafeWire.cs:305-315` ("a call nobody reserved is spending no ceiling sees").
**Observable result:** in the shipped app, a launch whose `ai_attempt` row cannot be written — the write throws, or there is no meter to write it — is a
REFUSAL: no process starts, no wake is consumed, the refusal is in the owner's words and names the launch ledger (not the day's limit nor a role's share — a
new `AiAdmission.Ceiling` value, e.g. `Ledger`, so no caller sends the owner to the Safety page), and the reason to take a turn stays owed and is tried again at
the next look (the loop's existing refused-turn path, `MissionLoop.cs:1835-1846`; no busy loop faster than today's refusal retry, no throw out of `TurnAsync`).
The owner's typed turn is held exactly as a capped one is (`AgentSession.cs:338-343`, `ApiConversation.cs:201`). A host BUILT WITHOUT a ledger (unit-test
hosts) keeps an EXPLICIT path: rename `AiAdmission.Unrecorded` to a name that says so (e.g. `NoLedger`), used only where no ledger exists by construction —
never as the answer to a failed write. The builder proves by grep which production paths can reach a null meter / null `Admit` and makes each refuse.
Read first: `CLAUDE.md` (safety rules); `docs/ORCHESTRATION.md` § 2.4; `TurnMeter.cs:786-880, 1020-1045, 1060-1095`; `AiAttemptStore.cs:160-200, 240-310`;
`MissionLoop.cs:430-450, 1815-1865`; `AppHost.cs:820-840, 1695-1710`; `AgentSession.cs:330-350`; `ApiConversation.cs:170-205`; `TypeSafeWire.cs:295-320`;
`tests/TradeAgent.UnitTests/BudgetReservationTests.cs`, `MissionLoopTests.cs:160-175`, `MissionOwnerMessageTests.cs:60-70`.
Items, one commit each, one-sentence messages:
1. `TurnMeter.Begin`: a failed admission write returns a refusal (ledger ceiling, the owner's words, the cause's message as `TypeSafeWire` does) — never
   `Admitted`. The ungated `_attempts.Begin(opened)` at `:1039` (recording a turn that already ran) is a record, not an admission: leave it, say so.
2. The other fail-open edges: `AppHost` with no meter refuses; a conversation with no `Admit` in the shipped app refuses (or is shown unreachable, quoted);
   the explicit no-ledger value is renamed and its doc comment rewritten; `MissionLoop`'s `recorded is null` branch keeps meaning "a host with no ledger" only.
Red-first tests (red at base, quoted): (a) `A_launch_whose_admission_cannot_be_written_is_refused_and_consumes_no_wake` — `TurnMeter.Begin` over a ledger whose
write throws (any honest way: a dropped/renamed `ai_attempt`, a read-only file); `Admitted` false, `Ceiling` the ledger's, the wake still unconsumed;
(b) `The_mission_loop_starts_no_process_when_the_ledger_will_not_take_the_row` — no `SendMissionAsync`, the turn retried at the next look and launched once
the ledger takes rows again; (c) `The_owners_typed_turn_is_held_when_its_admission_cannot_be_written`; (d) a GUARD: a host built without a ledger still runs
its turns through the explicit value (the existing test hosts stay green, names kept).
Mutant to watch red and quote: restore `catch (Exception) { return <the admitted value>; }` in `TurnMeter.Begin` ⇒ (a) and (b) red.
Conflicts: `U-org-envelopes` (O4) extends `AiAttemptStore.Begin`'s rule — this unit touches only the catch and the callers' fallbacks, not the rule; seat A's
`U-seat-home` touches launch environments, not admission.
Gate and report per `fleet/BUILDER-PROMPT-v2.md`: rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; branch CI all three
platforms; names vs `main` 0 removed (renames declared); `## Report` ≤ 20 lines appended here. No push to main, no merge.
