# U-thinking-seats-a — the activation gate: each capability has a minimum seat assurance, checked when a position opens and again, fail-closed, before model work on every adapter path
**Arrow closed:** "parallel, gated" (the owner, 2026-10-11): `docs/ORCHESTRATION.md` § 2.3 (the activation gate, no unconfined neighbour), § 2.5 (a refused
position is stepped over), § 5, § 8 (armed live); ORGANISATION § 15 rows `U-thinking-seats` and "The gate across both seats". Part a of 3 (a → b door → c
broker-only codex seat; the survey: `fleet/tmp/A-org/thinking-seats/survey.md`). **A protection — ORG-001, ORDER-001, CHANGE-001 (no position runs on a
seat weaker than its capabilities need): red-first and ONE watched mutant.** Seat A; opus/high. **Rung 39** (`ai_attempt.seat_class`, `ai_attempt.assurance`;
after O4's 38 — if this unit would land before O4, ask the orchestrator: the first to land takes 38, the other renumbers). Builds on `main`; rebase over O4 if it lands first.
**Today (main `8f261176`, READ):** the seam `PositionActivation.Refusal(id, standing)` (Core/Db/PositionActivation.cs:38) refuses every non-legacy id
outright and passes the legacy pair; it is asked FIRST in `AppHost.ConversationFor` (AppHost.cs:282) and by the loop through `IMissionHost.ActivationRefusal`
(AppHost.cs:324; default MissionLoop.cs:287; `Runnable` :2469). Nothing asks it at launch: `TurnMeter.Begin` (TurnMeter.cs:829) checks only an active name
(:837) and the seat profile (:840); the owner's chat opens the chair WITHOUT the seam (AppHost.cs:78-104, ChatView.cs:144/285). No capability or assurance
type exists: every CLI seat runs code (`AgentReach.RunsCode`, AgentReach.cs:129-190), `SeatPlan.CliClass` is the only class (RuntimeManifest.cs:83),
`runtime_profile` is a hash (TurnMeter.cs:872), no seat starts in a cell (AgentSession.cs:702 calls `Start(psi)`; `ContainedProcess.Cell` Containment.cs:212),
OpenCode/custom have no seat plan (SeatProfile.cs:222). runtimes.json replaces a manifest whole (RuntimeManifest.cs:1089).
**Observable result:** a position runs only on a seat whose ESTABLISHED assurance meets the minimum of every capability the launch has — the position's
plus the seat's intrinsic ones (a CLI seat has Code) — read from code, never from runtimes.json; the loop steps over a refused position (its wakes kept),
`Begin` refuses with no row, no reservation and no wake, and AgentSession/ApiConversation refuse once more just before the process or request (a meterless
host). The legacy pair keep their two-role behaviour on unconfined seats; while any non-legacy position is ACTIVE on the home, every unconfined launch —
the legacy pair and the owner's chat included — is refused ("the legacy pair run confined too, or not there", § 2.3) and says why. Every attempt records
its seat class and assurance. The armed-live refusal holds for every AgentSession seat; the harness is unchanged. **THE OWNER'S ANSWER, LITERALLY (seat A at queueing, from the orchestrator's ruling; ORCHESTRATION § 2.3 as amended `92ab862a`):** until this host has a PROVEN executor cell class RECORDED (data the app writes when a cell provider's benchmark passes here — none exists today), NO position other than the legacy pair activates on ANY real-model seat, a harness or broker-only head included; puppet seats are exempt (they run no model); never lifted by a flag or a setting.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.3, 2.5, 2.7, 5, 8; the survey §§ 1-2; PositionActivation.cs; OrgStore.cs:206-306; AppHost.cs:78-110, 240-330, 840-870; TurnMeter.cs:820-900, 1090-1130, 1250-1280; AgentSession.cs:280-300, 420-450, 610-705; ApiConversation.cs:190-215; MissionLoop.cs:255-300, 1900-1930, 2455-2475; `OrgPrincipalsTests.cs:206-300` (the third-position fixture); `CouncilLoopTests.cs:138-200`.
Items, one commit each, one-sentence messages:
1. **The vocabulary as code (Core):** `SeatAssurance {Unconfined, Mediated, Brokered, Confined}`, `Capability {Reads, EvidenceRequests, DecisionVerbs, Code,
   Helpers, Orders}`, `CapabilityPolicy.Minimum` (reads/evidence/decision ≥ Mediated; Code = Confined; Helpers inherit; Orders = `operations` only, ORDER-001
   untouched) and `CapabilitiesOf(standing, seatClass)` (a CLI seat adds Code; a position's today = Reads + EvidenceRequests, O2b's).
2. **The seam's body replaced, signature kept:** `PositionActivation.Seats` (a process-wide register, the `AgentGrants.Shared` pattern) answers the DECLARED
   class of the seat a position would launch on; AppHost fills it in `ComposeTheAi` from `RuntimeObjectFor` (harness ⇒ Brokered; a manifest with a seat plan
   whose class is `cli` or none ⇒ Unconfined; a cell only when a cell launch exists). Unset, null or a throw refuses; the legacy pair pass for existing behaviour.
3. **The launch-time half:** `SeatVerdict.Assurance` (AgentSession's verified seat; `ApiConversation` ⇒ Brokered); `Begin` refuses after `SeatOf` when the
   verified class misses the minimum or differs from the declared one; AgentSession refuses after `VerifyAsync` (:628) and ApiConversation before its request
   (:535) by the same rule; the owner's chat path goes through `Begin` (it does today) and is now covered.
4. **No unconfined neighbour:** `ActivePositionsBeyondLegacy` (one OrgStore read) — while > 0, an Unconfined launch is refused in words naming the active
   positions and the repair; a non-legacy launch is refused while any Unconfined attempt is open (TurnMeter's open set).
5. **The record (rung 39):** `ai_attempt.seat_class` (`cli`, `broker`, `harness`, `cell`) and `assurance`, written in `Begin` beside `RuntimeProfile`;
   `Versions` and the rung's crash-recovery stamp as the existing rungs do; no migration (CLAUDE.md).
6. **No real-model position before a proven cell (the owner's answer):** rung 39 also creates the record the gate reads — the host's PROVEN executor-cell classes (provider, platform, build, profile hash, when), written only by the app (by `U-cell-linux-c`/`-e` later; nothing in this unit writes it); while it holds none, every non-legacy position on a real-model seat (cli, harness, broker) is refused in words naming the missing proof; a `puppet` seat (no model, test host only — `U-puppet-runtime`) is exempt from this refusal and is no unconfined neighbour for item 4; whichever of the two units lands second wires that seam.
   The record says in plain words: item 4 has no production effect until `U-org-verbs` writes positions.
Red-first tests (quote each red at base): (a) `A_position_on_a_cli_seat_is_refused_because_its_shell_is_code_and_code_needs_a_cell`; (b)
`A_head_on_the_harness_is_refused_until_the_host_records_a_proven_executor_cell` and `With_a_proven_cell_recorded_a_head_on_the_harness_passes_and_records_brokered`; (c) `The_launch_time_check_refuses_a_seat_weaker_than_the_one_declared_with_no_row_no_reservation_no_wake`;
(d) `A_refused_position_is_stepped_over_and_the_legacy_pair_keep_turning`; (e) `While_a_position_beyond_the_legacy_pair_is_active_an_unconfined_launch_and_the_owners_chat_are_refused`;
(f) `A_seam_whose_register_is_unset_refuses_every_position_but_the_legacy_pair`; (g) `A_seat_class_is_never_read_from_runtimes_json`; (h) GUARD `The_legacy_pair_alone_run_exactly_as_before` (green at base); (i) GUARD `Armed_live_refuses_every_agent_session_seat_and_not_the_harness`.
**The watched mutant:** `Begin`'s launch-time assurance check removed (the seam kept) ⇒ (c) red. Quote it, restore, green.
**Evidence beyond CI:** the full suite on the tests box if `win-test.sh ready`, else NOT RUN with the reason. No model call; no observed run.
Out of scope: the door (b), the broker-only codex class and its canon (c), org verbs (O5/O6), cells for seats (`U-cell-linux-b..d`), the recorded settle of a refused wake (O3). Conflicts: O4 (`TurnMeter`, `AiAttemptStore`, `AppHost`, rung 38) — rebase, renumber; `U-grant-tree` (`AgentSession` :610-705) — adjacent; O2b none in code.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed, both set sizes printed). `## Report` ≤ 20 lines appended here.
