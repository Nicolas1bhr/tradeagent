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
