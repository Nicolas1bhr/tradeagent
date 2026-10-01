# U-resume-agent — a restart that resumes a working mission also starts the AI it needs, through every check a press goes through
**Arrow and boundary:** the loop's "continue without the owner supplying the next step" across an interruption (`docs/PRINCIPLES.md`: "A restart must not
… require the owner to reconstruct the mission"; `manager-prompt.md` § 6 "Sustained autonomous factory": "resume after interruption"), keeping
`CONTAINMENT_REQUIRED` and every launch check exactly as a press meets them. **Observable result:** in the running app, an app restart while the AI was
working on its own brings the next turn without any press; a paused AI stays paused; an AI the containment rule refuses is refused in the same words.
**The gap, RUN 2026-10-01 in the observed run (`docs/briefs/U-observed-loop.md`, attempt 2, app at `f8a7500`):** relaunched while the mission was working —
the interrupted turn went `LOST` with its reservation charged (correct), the mission flag resumed (`ResumeAiOnStart`, on by default; the card offered "Pause
the AI"), but the card read "stopped — the AI has not been started", Chat "The AI is not running yet", Agent runtime "unknown"; no turn ran until the owner
pressed "Start the AI" (18:01:5xZ), after which the mission took its next turn by itself (18:02:09Z).
**Cause (SOURCE):** `AppHost.StartAsync` → `ResumeMissionIfItWasWorking()` (`src/TradeAgent.App/AppHost.cs:667`, `:1104-1110`) starts the LOOP only; the
runtime is started only by the owner's press — `MainWindow.StartOrStopAgentAsync` (`src/TradeAgent.App/MainWindow.cs:683-693`: `SelectedRuntimeId`,
`RuntimeCatalog.Require`, `Agent.PrepareAsync(manifest, WorkspaceContext())`, `Agent.StartAsync()`) and onboarding's `StartAgent` (`OnboardingView.cs:1068`).
The setting's own summary (`src/TradeAgent.Core/Trading.cs:334-340`) says why it exists: "an autonomous product that stops for good the first time Windows
updates overnight".
Read first: `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the files above, `AgentSupervisor.cs:31-110`, `CliAgentRuntime.cs:683-700` (the `CONTAINMENT_REQUIRED`
refusal), `docs/CONTRACTS.md` on the mission and containment.
Items, one commit each:
(a) **One start path.** The press, onboarding and the resume use ONE method on the host (the runtime chosen in settings, `Require` not `Find`, prepare, start)
so the resume cannot skip a check the press makes; the resume calls it only when `DecideOnStart` says `Resume` and onboarding is complete; a start that throws
(containment, a runtime that will not start, a missing manifest) leaves the AI stopped and says why where the press would — the card and the activity log —
never silently. The page is not switched by a resume (the owner did not press anything).
(b) **Tests, RED on the base:** a host started with the mission working and resuming on start has its runtime running and the next due wake taken without a
press; paused → not started; resuming off → not started and the flag written back (unchanged behaviour); armed-live and uncontained → `CONTAINMENT_REQUIRED`
in the activity log and no runtime. Watch ONE mutant (the resume calling the loop only) turn the first test red and quote it.
(c) `CONTRACTS.md` and `docs/USER-GUIDE.md` say what a restart does, one sentence each.
Not the money path (no order, no gate touched) — the launch path; red-first is the proof. No schema rung. Gate as `docs/HOW-WE-BUILD.md` (`--no-incremental`
Release 0 warnings; the three suites 0 failed on this Mac, counts pasted; touched classes 3×; names 0 removed). Report ≤ 20 lines appended here. No
`Co-Authored-By`; no push to `main`; touch nothing in `docs/briefs/` but this file. The manager verifies the observable result in the running app after landing.

## Report
**Code tip `bb64295`**, 3 commits on `2dab37f` (cut from `2fd0318`, rebased onto `main`: it moved by tests and docs only, no conflict). **Gate on this Mac, Release:** build `--no-incremental` →
19 projects, `0 Warning(s)`, `0 Error(s)`; Unit `Passed! - Failed: 0, Passed: 1198, Skipped: 0, Total: 1198, Duration: 25 s`; Fault `Passed! - Failed: 0, Passed: 399, Skipped: 0, Total: 399,
Duration: 1 m 27 s`; Integration `Passed! - Failed: 0, Passed: 699, Skipped: 1, Total: 700, Duration: 11 m 5 s`. `ResumeOnStartTests` + `MissionControlsTests` 3× → 20/20 each. Names vs `main` (git objects): 1914 → 1920, 0 removed.
- **(a) `2790105`:** `AppHost.StartTheAiAsync` (`SelectedRuntimeId` → `RuntimeCatalog.Require` → `Agent.PrepareAsync` → `Agent.StartAsync`) is the one start: the press (`MainWindow`, which still switches
  to Chat after it) and setup's `StartAgent` call it; `ResumeOnStartAsync`, awaited last in `StartAsync`, calls it only on `Resume` with setup complete, then `Mission.Start()`. A throw puts the press's
  words on the card (`AiNotStarted` → "stopped — …"), a `warn` activity line "The AI was not started: … (CODE)" and an engineering line, then rethrows (the press's strip unchanged; the resume swallows it).
- **(b) `b6dc3d8`:** `ResumeOnStartTests` (6) drive `AppHost.Composed` (the same `ComposeTheAi` that `StartAsync` runs, over a test DB) and its own `ResumeOnStartAsync`, with a real `CliAgentRuntime`
  over a one-line probe. RED on the base (the resume body, the press and setup as on `main`, seam in place): `:62 the restart resumed the loop and left the AI it needs stopped`; `:160 Assert.Single()
  Failure: The collection did not contain any matching items … Collection: []`; `:216 … Not found: "await _host.StartTheAiAsync();"`; paused, resuming-off and setup-unfinished GREEN on the base (guards).
  Mutant, the start removed from the resume (loop only): `:62 the restart resumed the loop and left the AI it needs stopped` (and `:226 Expected: 2 Actual: 1`); restored → 6/6.
- **(c) `bb64295`:** `CONTRACTS.md` (after the leases paragraph) and `docs/USER-GUIDE.md` ("It picks up where it left off") say what a restart does, one sentence each.
- **Deviations:** (1) `AgentSupervisor.StartAsync` now stops and drops a runtime that threw: kept, its conversation let the resumed loop launch turns refused at the launch (each a ledger row holding a
  reservation) and the card could never say "stopped" — so a refused PRESS no longer leaves a dead conversation either. (2) The press gains the card line and the activity line (one path). (3) Seams for (b):
  `ComposeTheAi` extracted verbatim, `AppHost.Composed`, and an optional `AgentPresence` on `AgentSupervisor`/`CliAgentRuntime` (null = the shared one, which is all the product passes) so the test's turn
  never enters the sticky shared register. (4) The start opens the conversation on its own thread: the getter is unsafe to race with the resumed loop's first look. (5) `StartAsync` awaits the start, so
  a resuming app first paints after the CLI's version probe.
- **Not run headless:** the whole `StartAsync` (instance lock, pipe, collectors, background loop, relay reconcile over the shared test home); that it calls the resume is the source assertion in `MissionControlsTests`.
- **NOT done / NOT verified:** the running app's observable result (the manager's, after landing; the observed run untouched); Windows, where the probe runs via `powershell -File`: NOT VERIFIED; CI not
  watched; no box. The credential scan gated every commit; excluded by name, on (a) and on this line: `CancellationToken`, `AiTurnAllowance(Input|Output)Tokens`. No `Co-Authored-By`.
