# U-puppet-runtime — scripted seats run whole organisational lives through the real kernel in CI, at no AI cost, and can never run outside a test host
**Arrow closed:** `docs/ORCHESTRATION.md` § 7 ("puppet seats: a harness adapter that plays scripted positions through the real kernel … in CI, at no AI
cost"), § 2.7 (the puppet adapter), § 2.14 (the proving ground's plumbing); ORGANISATION § 15 row `U-puppet-runtime` (Depends O2a — landed `7be8bec7`).
**A protection — a scripted seat holds a real launch grant (AUTH-001, ORDER-001, CRED-001): the puppet never starts outside a test host — red-first and ONE
watched mutant.** Seat A; opus/high. **No rung.** Builds on `main`; ∥ `U-thinking-seats-a`. **Seam with part a (seat A at queueing, the orchestrator's ruling 1):** a `puppet` seat runs no model, so it is EXEMPT from part a's refusal of real-model positions before a proven cell and is no unconfined neighbour; it still meets every other check (rights, admission, the grant); whichever of the two lands second wires it.
**Today (main `0cd79011`, READ — re-checked by seat A 2026-10-16: since `8f261176` only O2b `U-org-rights` moved code; its shifts: MissionLoop.cs past 846 +18 (:1900-2100 → :1918-2118), MissionEventStore.cs past 314 +23 (:740-840 → :763-863); the other cited files unchanged where cited — grep before trusting a number):** fake CLIs exist but none calls `trade` or the pipe: `TurnMeterTests.cs:47-110` `SessionOverStream` (a `.cmd`/`.sh`
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
`final "<text>"`, `exit n`, `hang`, `crash`. The puppet seat records the seat class its TEST MANIFEST declares — never read from runtimes.json and unreachable from production configuration (seat classes live in code) — so later units' puppet tests can reach the verbs a declared class allows (seat B's O5 survey, 2026-10-16). A puppet manifest is refused everywhere but a test host: its flag is `[JsonIgnore] internal`, its id is reserved
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

## Report
Tip of code: `0782939b` (base `06471ec7`, no rebase needed: main unmoved; part a and U-org-lanes not on main, so the `puppet` class stays nameable as `SeatPlan.PuppetClass` for part a to exempt).
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → "Build succeeded. 0 Warning(s) 0 Error(s)". Touched classes (PuppetSeatTests, PuppetLivesTests, ToolDeployerTests) 3× Release via suite lock: "Passed! - Failed: 0, Passed: 14" ×3. Neighbours once (TurnMeter, QuiescenceBarrier, ResumeOnStart, MaterialPassExclusion, OrgPrincipals, SeatHome, VendorOverrideFile, AgentEnvironment, MissionOwnerMessage): "Passed: 69".
CI run 38943617130 (0782939): all 7 test jobs + package + package-linux green; linux-host red — fleet-wide image break (U-ci-host-pins). The 10 puppet tests "Passed" on windows, ubuntu and macos (trx).
Lives' rows per platform (trx output, identical on Windows 10.0.26100, Ubuntu 24.04.5, macOS 26.6.2): (c) ai_attempt=2 mission_event=4 ledger_entry=2; (d) ai_attempt=2 mission_event=4; (e) ai_attempt=3 mission_event=6.
Names: "removed: 0", sets base 2707 tip 2720 (added 13). Codex review: `fleet/tmp/A-org/u-puppet-runtime-build/U-puppet-runtime-review.md` (not acted on).
1. Done — `tests/TradeAgent.Puppet` (own Directory.Build.props: not a test project; in .sln/.slnx; never packaged); one C# program for all three OSes; steps trade/write/usage/final/signal/await/exit/hang/crash.
2. Done — `RuntimeManifest.Puppet`/`PuppetSeatClass` `[JsonIgnore] internal`; `PuppetId` reserved (any casing) refuses `RuntimeCatalog.Read` in words (`Labels.RuntimesNameAReservedId`); `PuppetSeats.TestHost` set by UnitTests' module initializer; refused in `AgentSession.RunTurnAsync` before any grant, also in `CheckSeat` (no row) and `CliAgentRuntime.ResolveExecutable` (no version probe). Declared class recorded as `seat_class` in `ai_attempt.context` (no rung; null for every non-puppet).
3. Done — `PuppetLife` in UnitTests (it references App, which carries TradeCli; deviation: not a new project). Crash = `VACUUM INTO` image opened by a recomposed host. Note for later units in its summary.
4. Done — lives (c)(d)(e), material pass, owner's words to a busy chair, plus declared-class test. Deviations: lives wake with `self` wakes (the loop withdraws payload-less review looks for a role owning nothing open — measured: chair's review withdrawn); ToolDeployerTests joined the VendorOverrideFiles collection (it clears the shared bin the lives deploy trade into); (f) is behavioural (existing fake CLIs run identically with TestHost on/off), no existing fake-CLI test edited.
RED: (a) at base: "runtimes.json naming 'puppet' was read: 5 runtimes". (b) flag present, refusal short-circuited: "a puppet outside a test host was issued 1 launch grant(s)". (c)(d)(e) with no puppet built: "the puppet seat was not built: no …/TradeAgent.Puppet.dll" (not a base-commit run: the tests need the item-2 API to compile).
MUTANT: AgentSession's 2-line `PuppetSeats.Refusal` check deleted → (b) "Failed … issued 1 launch grant(s)"; restored → "Passed 4/4".
tests box: NOT RUN — the machine does not answer
Not done/verified: no real model, no codex run; (f) has no base run (needs the flag); lives run outside the Timing category with 3-min patience (CI took seconds).
