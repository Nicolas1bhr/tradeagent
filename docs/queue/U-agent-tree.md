# U-agent-tree — Pause, Stop, a turn's end and the app's quit or death end the turn's WHOLE process tree on macOS and Linux, as the job does on Windows
**Protects** (operator authority, `CLAUDE.md`; the owner's spend, `TurnMeter`/`AiAttemptStore`): (1) **Pause means the AI is stopped** — after Pause, Stop, quit or
the app's death no process of the turn runs; (2) **the spend ledger measures every paid process or refuses the next turn** — the turn's `ai_attempt` row closes
and its presence ends only once its tree is dead, and a teardown that cannot prove that holds the next turn's admission, in words naming the pids; (3) **nothing a
turn spawned reaches the pipe after its turn** — the grant ends after the tree (`AgentGrants.cs:177-193`), and a failed teardown revokes it without the 60 s
grace (:94). The teardown kills ONLY what it proves is the turn's. Fresh builder, seat P; no rung, no schema, wire or verb change. Survey `s-agent-tree` (seat A).
**Facts (SOURCE at `8319b390`, src unchanged since the survey's `901586a7`; MEASURED = the survey on this Mac, never committed).**
- `AgentSession.RunTurnAsync` (`AgentSession.cs:468-559`) starts the turn through `ProcessContainment.Start` (:529; `AgentRuntime/Containment.cs:278-326`):
  Windows, a KILL_ON_JOB_CLOSE job assigned after `Process.Start` (:354-388; gap admitted :364-377); macOS/Linux, `bin/trade --spawn-contained` (setsid + execv,
  `Core/Containment.cs:49-87`), else a bare start "not held" (:285-292, :304-318). `ContainedProcess.Kill` (:194-200) kills the GROUP, then walks parent links (a
  child that left the group is orphaned first); `Dispose` (:216-224) kills only `if (!Process.HasExited && CurrentGroup() > 0)`. `alive` (:538) is disposed before `contained`.
- `MissionLoop.PauseAsync` (:1410-1425) cancels the token first (:1416): the turn unwinds through `Dispose`, `_current` is nulled (`AgentSession.cs:450`) before
  `CancelAsync` (:1417, the chair's conversation only) and `AgentSupervisor.StopAsync` (:135-145 → `CliAgentRuntime` :751-765 → :898-906) could kill. Quit:
  `AppHost.DisposeAsync` (:1917-1940), run from an `async void` (`TradeAgentApp.cs:32-35`), pauses no loop and stops no AI. `CliAgentRuntime.Run`'s timeout (:836)
  and the key sign-in (:610) kill with `entireProcessTree: true`. `ContainmentTests.cs:32-70` covers `CancelAsync` only.
- MEASURED (a `/bin/sh` turn writing its pids; 5 probes, all red): no launcher — all six alive after Pause + Stop, `presence.Live=0`, the row ENDED; launcher —
  a `setpgrp` and a `setsid` child live through Pause, Stop and the host's exit; an exit-0 turn's group child lives on; `DisposeAsync` mid-turn: all six alive. No
  usage on `TurnEnded` (`AgentSession.cs:433-465`): `AiAttemptStore.End` (:312-341) closes the row at its reservation while survivors run. codex 0.160.1 imports `setsid`.
Read first: `CLAUDE.md`; both `Containment.cs`; `AgentSession.cs:410-559`, :891-915; `MissionLoop.cs:1406-1425`, :1759-1793; `AppHost.cs:1917-1940`; `ContainmentTests.cs`;
the survey's notes and probe, read only: `~/Projects/ai-trading-software-for-mihael-worktrees/fleet/tmp/s-agent-tree-survey/`.
Must NOT: kill by name or without proof (a pid is the turn's when it descends from the turn's leader or sits in its own session, re-proved by pid + start time
AFTER it is frozen; one that fails is resumed and left) — never the app's own group or session (`Posix.KillGroup`'s refusal stays), never a process outside the
tree, the owner's own Codex Desktop included; give the launcher a grant or a pipe (`Core/Containment.cs:34-40`); claim an OS sandbox; touch Windows' job flags or
breakaway refusal (`JobObjectFlagsTests`); change the owner's resume choice; weaken an assert. Items, one commit each, one-sentence messages:
1. The tests below over the OLD code, each red on this Mac where the survey measured it, quoted.
2. `ContainedProcess.Dispose` IS the teardown, leader exited or not: the session id (the leader's pid once `Held` latched) is remembered, never re-read from a gone
   pid; it freezes (SIGSTOP) and collects the parent tree FIRST, then every pid of the turn's session, proves each, SIGKILLs them and repeats until none is left
   (bounded; said in words when not, and (2)-(3) then hold the next turn and revoke the grant); `Run` (:836) and the key sign-in (:610) end through it.
3. `MissionLoop.PauseAsync` kills every role's turn (`ConversationFor`, not the chair's alone) before it cancels the token and returns once the teardown has;
   `RunTurnAsync` ends `alive` after it; the row closes and presence ends after the teardown.
4. Quit: `AppHost.DisposeAsync` pauses the loop (`Mission.PauseAsync`, NOT `PauseTheAiAsync`: the owner's resume choice stays) and stops the AI before anything
   else; the `ShutdownRequested` handler holds the quit until it has, bounded; whether the update path's `Shutdown()` (`MainWindow.cs:642-646`) raises it is reported.
5. App death (crash, force quit, the dead-man): `--spawn-contained` stays resident as the turn's supervisor — setsid, start the command with stdio inherited
   (`Process.Start`, never a managed `fork`), forward its exit code; on its exit or the app's death (`getppid()` no longer the app) it sweeps the session as item 2.
Tests (UnitTests over `AppHost.Composed` as `ResumeOnStartTests` drives it; the launcher as `ContainmentTests.DeployLauncher`; Unix `/bin/sh`, Windows `cmd`; every pid
dead within 5 s): (a) a paused turn, with and without the launcher, a Research role's too; (b) a stopped turn; (c) a finished turn's leftover; (d) children that left the
group or the session; (e) the app's dispose mid-turn; (f) the app's death (Unix: the supervisor's parent killed −9; Windows in CI where cheap, else named to
`U-contain-seats`, as is Windows' start gap); (g) the row ENDED and `presence.Live` 0 only after (a)'s tree is dead; (h) guard: a process outside the tree,
with the turn's command, survives. Mutant, quoted red: `Dispose` back to `if (!Process.HasExited && …)` ⇒ (c).
Gate and report per `docs/FLEET.md` "The builder pass": rebase; Release 0 warnings; Unit, Fault 0 failed; touched classes 3×; CI on all three; tests box or NOT RUN; names 0 removed; `## Report` ≤ 20 lines.
