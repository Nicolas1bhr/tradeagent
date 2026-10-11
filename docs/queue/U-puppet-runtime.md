# U-puppet-runtime — scripted seats run whole organisational lives through the real kernel in CI, at no AI cost, and can never run outside a test host
**Arrow closed:** `docs/ORCHESTRATION.md` § 7 ("puppet seats: a harness adapter that plays scripted positions through the real kernel … in CI, at no AI
cost"), § 2.7 (the puppet adapter), § 2.14 (the proving ground's plumbing); ORGANISATION § 15 row `U-puppet-runtime` (Depends O2a — landed `7be8bec7`).
**A protection — a scripted seat holds a real launch grant (AUTH-001, ORDER-001, CRED-001): the puppet never starts outside a test host — red-first and ONE
watched mutant.** Seat A; opus/high. **No rung.** Builds on `main`; ∥ `U-thinking-seats-a`. **Seam with part a (seat A at queueing, the orchestrator's ruling 1):** a `puppet` seat runs no model, so it is EXEMPT from part a's refusal of real-model positions before a proven cell and is no unconfined neighbour; it still meets every other check (rights, admission, the grant); whichever of the two lands second wires it.
**Today (main `8f261176`, READ):** fake CLIs exist but none calls `trade` or the pipe: `TurnMeterTests.cs:47-110` `SessionOverStream` (a `.cmd`/`.sh`
replaying `CodexStream` :127, usage included, through the real `AgentSession`); `QuiescenceBarrierTests.cs:54-90`; `ResumeOnStartTests.cs:276-350` (a
probe runtime registered with `RuntimeCatalog.SaveOverrides`, run through the real `AppHost.Composed`); `ContainmentTests.cs:151-154` (a `trade` shim).
The pipe with a real grant is tested alone (`LaunchGrantTests.cs:38-39` starts a `GatewayPipeServer`; deploy :351-356; env :370-390). `AppHost.Composed`
(AppHost.cs:937) leaves the pipe server out by design. LOST is always simulated (`AiAttemptStore.LoseOpen`, AiAttemptStore.cs:431). UnitTests references
`TradeAgent.App` (InternalsVisibleTo, App.csproj:17) but not `TradeCli`; IntegrationTests references `TradeCli` (csproj:8) but not `App`. A runtimes.json
entry can name any program (`custom`, RuntimeManifest.cs:943; whole-entry override :1089). § 2.7's adapter contract is today's
`IAgentRuntime`/`IAgentConversation` (IAgentRuntime.cs:23-98, AgentSession.cs:24-53) — no new abstraction is needed.
**Observable result:** a test writes per-position scripts and runs the REAL chain — `MissionLoop` → admission (`TurnMeter.Begin`) → the activation seam →
`AgentSession` (prompt on stdin) → `ProcessContainment` → a real puppet process → the real `trade` with the launch's real grant → `GatewayPipeServer` →
the stores — on all three CI platforms, at no AI cost. The puppet is a test-only program (`tests/TradeAgent.Puppet`, never in `src`, never packaged): on
a wake it runs its script — `trade <op…>`, `write <out/…>`, `usage {input, cached, output}` (codex `--json` events, metered by AgentSession's own parser),
`final "<text>"`, `exit n`, `hang`, `crash`. A puppet manifest is refused everywhere but a test host: its flag is `[JsonIgnore] internal`, its id is reserved
(a runtimes.json entry using it refuses the file's read, in words), and AgentSession refuses to start it unless an internal test-host flag is set.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.5, 2.7, 7, 8; the survey § 6; AgentSession.cs (all, U-prompt-stdin's stdin path); TurnMeter.cs:820-900;
MissionLoop.cs:1900-2100; RuntimeManifest.cs:300-450, 1075-1100; MissionEventStore.cs:740-840 (LOST and the owner's words re-raised once); the tests named above;
`OrgPrincipalsTests.cs:206-300` (a third position seeded and stepped over); `MaterialPassExclusionTests.cs`; `MissionOwnerMessageTests.cs`.
Items, one commit each, one-sentence messages:
1. **The puppet program** (`tests/TradeAgent.Puppet`, a console app in the solution): reads its prompt from stdin, finds its script by position in its
   home, plays the steps, finds `trade` on `PATH` exactly as a seat does; one implementation for Windows, macOS and Linux (no `.cmd`/`.sh` pair).
2. **The refusal (the protection):** `RuntimeManifest.Puppet` (`[JsonIgnore] internal`), a reserved id refused in `RuntimeCatalog.Read`, `TestHost`
   (internal, set by the test assembly's module initializer) checked in AgentSession before any grant is issued; the refusal's words name the manifest.
3. **The `PuppetLife` fixture:** a home, the deployed `trade` (the LaunchGrantTests pattern), a `GatewayPipeServer` over the composed host's gateway, the
   puppet manifest handed in-process, scripts, `RunUntil(condition)`, store readers; in the test project that can reference both `App` and `TradeCli`.
4. **The first lives** (one test each, all three platforms): a legacy-pair turn with a pipe op and metered usage; a restart mid-turn (`hang`, the host
   disposed and recomposed) → LOST, its reservation kept, the owner's words re-raised once; a non-legacy position refused by the seam and stepped over while
   the legacy pair turn; the material pass waiting for a puppet turn's quiescence; the owner's words reaching a busy chair's next turn. A short note in the
   fixture says how later units add theirs (O3 settles, O4 envelope exhaustion, O5 assignments, the verbs' conformance suite, part c's broker class).
Red-first tests (quote each red at base): (a) `A_puppet_manifest_named_in_runtimes_json_is_refused_and_nothing_starts`; (b)
`A_puppet_outside_a_test_host_is_refused_before_any_grant_is_issued`; (c) `A_legacy_turn_by_a_puppet_reaches_the_pipe_with_its_own_grant_and_its_usage_is_metered`
(red: no puppet); (d) `A_restart_mid_turn_loses_the_attempt_keeps_its_reservation_and_re_raises_the_owners_words_once`; (e)
`A_position_beyond_the_legacy_pair_is_stepped_over_and_the_legacy_puppets_keep_turning`; (f) GUARD `Every_existing_fake_cli_test_is_unchanged`.
**The watched mutant:** the `TestHost` check in AgentSession removed ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** the lives on all three CI runners (quote one life's row counts per platform); the tests box if `ready`. No model, no codex.
Out of scope: org verbs and their conformance suite (they arrive with O5/O6 and ride this fixture); the proving ground; a puppet in the shipped app.
Conflicts: `U-thinking-seats-a` (`TurnMeter`, `AgentSession`) — adjacent, rebase; part c (`RuntimeManifest.cs`); O4 (envelope life after it lands).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed, both set sizes printed). `## Report` ≤ 20 lines appended here.
