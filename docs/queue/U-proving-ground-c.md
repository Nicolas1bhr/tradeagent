# U-proving-ground-c — the proving host process, a configuration as data, the no-model floor and one puppet agent at equal budgets, scored into the shadow home's own store
**AMENDED 2026-10-21 (the orchestrator's preparation session; pointer re-check `fleet/tmp/PREP-2026-10-21/recheck/recheck.md` § 4, main `25bae9bc`; re-check
again at dispatch):** "`OrgStore` has no public writer" is FALSE now — `OrgStore.Decisions.cs` exposes `Decide` (`:231`), `Forecast` (`:275`), `RaiseEscalation`
(`:465`), `SendLateral` (`:606`), and `OrgStore.Mandates.cs` `Assign` (`:326`), `Cancel` (`:367`), `LaunchRefused` (`:383`): a puppet life writes through them, never
seeded SQL. The tests' seed SQL is `PuppetLivesTests.cs:165-168`; `class OrgStore` is at `OrgStore.cs:321`; the app's composition is `AppHost.Composed`
(`AppHost.cs:1081`) — `:950-970` is the inbox copy pass and `:1711-1830` the owner's failure sentences. "VISION § 3 route 2" means § 3's earning rule's second
route (`VISION.md:122`). `U-org-lanes` is landed (`MissionLoop.Sweep()` `:2142`). Order: after `U-org-audit` and parts a, b (ORGANISATION § 15).
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.14 ("a configuration — chart template, seats and policies — works a brief under a budget and is scored in
hours"; "a deterministic search with no model as the floor, one agent … at equal budgets on the same markets"; "a separate app process"; "plumbing
proved with puppet seats at no cost"), § 7; VISION § 3 route 2. Part c of 5. **A protection — puppets run only in a test host (the puppet runtime's
rule), SIM-001 (the score store read by no evidence path), BUDGET-001 (equal budgets are enforced envelopes, not settings read back): red-first and
ONE watched mutant.** Seat A; opus/high. **No rung** in the real schema: `proving.db` is the proving home's own file with its own ladder (the tape.db
pattern, `TapeStore.cs:218-340`). Depends on parts a and b landed.
**Today (after a, b):** a proving home and a market can be written by a test in-process; no process runs a configuration in one. `PuppetLife`
(`tests/TradeAgent.UnitTests/PuppetLife.cs:144-182`) composes `AppHost.Composed` + `GatewayPipeServer` + puppet manifest inside the test assembly over
the ASSEMBLY's `Paths`; `OrgStore` has no public writer (tests seed SQL, `PuppetLivesTests.cs:157-160`); no search code exists; `Backtests.Run(caller,
ask)` (`Gateway/Backtests.cs:97`) is the one entry the pipe's `backtest` op uses.
**Observable result:** a test writes a configuration file and starts `TradeAgent.ProvingHost` as a CHILD PROCESS with a fresh proving home and pipe;
the host seeds the market (a, b), copies the seeded home once (`VACUUM INTO`, hash recorded) per configuration, applies each configuration — the floor
and one agent — at the same trial, verdict and AI budgets and the same deadline, runs it, scores it into `proving.db`, and exits; the test reads the
scores: the floor finds the planted edges the grammar can express and passes nothing on noise; the scripted agent's score equals what its script did
(found 1, false 0, a pre-fee trap's verdict counted), its AI spend metered from `usage` steps against the envelope and refused past it; the real
home and the test assembly's home are untouched.
Read first: parts a, b; ORCHESTRATION §§ 2.4, 2.14, 7; the survey Q3-Q4; `PuppetLife.cs`, `PuppetLivesTests.cs`, `tests/TradeAgent.Puppet/Program.cs`;
`AppHost.cs:950-970, 1711-1830`; `Gateway/Backtests.cs:76-200`; `Referee.cs:150-200`; `OrgStore.cs:14-200`; O4's envelope code (grep `OrgEnvelopes`,
`ai_attempt_funding`); `App.csproj:17` and AgentRuntime's InternalsVisibleTo; the packaging scripts (grep `TradeAgent.Puppet` in `tools/`, `.github/`).
Items, one commit each, one-sentence messages:
1. **The host** (`tests/TradeAgent.ProvingHost`, a console program in the solution, never packaged — its own Directory.Build.props like the puppet's):
   sets `PuppetSeats.TestHost` and `ProvingGround.ThisProcess` first, refuses per part a's `HomeRefusal`, composes as `PuppetLife` does over its own
   home (no collector, no network), deploys `trade` into its own `bin/`, runs, exits with a code; InternalsVisibleTo widened to it alone; a test proves the
   packaging scripts' output contains no `ProvingHost` or `Puppet` file.
2. **A configuration as data** (`Core/Proving/ProvingConfiguration`, JSON, refused-never-guessed with a version): kind (`floor` | `seats`), chart
   template (units, positions, a puppet script per position), the root unit's AI envelope, trial and verdict budgets, deadline; applied by a writer
   that refuses any home not marked proving; positions written as O1/O2a write them (no new columns).
3. **The floor:** a closed grammar of v2a programs over the market's proving series (`latest|change` × fixed percentile thresholds × ± × two holds,
   `bars 1h`), each run through `Backtests.Run` under a proving-only app principal so trials count as a seat's; verdicts for the best research results
   until the verdict budget is spent; deterministic order, no model.
4. **The score store** (`proving.db`: `proving_run`, `proving_score`; its own ladder): per configuration — planted found, false discoveries on noise,
   traps fallen for, trials and verdicts spent, AI dollars metered (and real dollars: 0), envelope refusals, first-PASS time; each figure's source rows
   named; the manifest read only here. Labelled SIMULATED in every row and sentence; no evidence-path type reads it (part a's scan extended).
5. **One life of the ladder** (UnitTests, one test, all three platforms, outside the Timing category, small markets): floor and one-agent at equal
   budgets; the report prints both score rows per platform.
Red-first tests (quote each red at base): (a) `A_puppet_configuration_runs_only_in_the_proving_host_and_never_in_the_test_assemblys_home`; (b)
`A_configuration_is_refused_by_a_home_not_marked_proving`; (c) `The_floor_finds_the_expressible_planted_edges_and_passes_nothing_on_noise`; (d)
`The_scripted_agents_score_equals_what_its_script_did`; (e) `Two_configurations_start_from_byte_identical_homes_at_equal_budgets`; (f)
`An_agent_past_its_envelope_is_refused_and_scored_so`; (g) `The_score_store_is_read_by_no_evidence_path`; (h) GUARD `Every_puppet_life_runs_as_before`.
**The watched mutant:** the configuration writer's proving-mark check removed ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** both score rows per CI platform (identical by seed); the tests box if `ready`. No model, codex or network. NOT claimed: intelligence.
Out of scope: critic and headed configurations, calibration, the off-path exam (d); real models (e). Conflicts: O9 `U-org-lanes` (`MissionLoop` — read
only; rebase if it lands first); `U-thinking-seats-a` (not needed: the agent is the legacy research position). Gate and report per
`fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.
