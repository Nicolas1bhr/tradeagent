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

## Report
**Tip `81ed13ce`** on main `7e29fc65`; this report is the commit on top. **Build** `-c Release --no-incremental`: 0 Warning(s), 0 Error(s). **Local Release** at `8127585f` (src identical to the tip; after it only the Timing trait, then rerun: AgentTreeTests + VendorOverrideFileTests 25/25, `Category=Timing` 18/18): Unit 1502 passed 0 failed, Fault 471 passed 0 failed, AgentTreeTests 3× 17/17. **Names** vs main: removed 0, added 17 (2327 → 2344).
**CI 37717264247** at `81ed13ce`: macos ✓, ubuntu ✓, windows ✓, package ✓; the Timing step's first attempt passed on all three (Unit 18/18). Earlier: 37694897915 (`22c10087`) red only on the arms of items 3–5, not built yet; 37707921636 (`cd17c836`) ubuntu red on `BridgeRoundTripTests.A_live_refusal_is_not_masked_by_a_stale_one` (TimeoutException :813; AtasConnector over the stub bridge's pipe, which this diff cannot reach; green on main 37706652101 and on every later run here, it did not repeat), and windows red on the new PowerShell arms — stopped and guard "the probe never wrote its pids", finished turn "never committed"; 37712519465 (`8127585f`) windows red on research, stopped and guard, "the probe never wrote its pids: leader.pid, child.pid".
**RED before** (6bd92855 + the item-1 tests, this Mac): 10 of 10 red — "5 s after Mission.PauseAsync, the launcher deployed, the turn's tree still ran: ownpgrp (pid 68379), ownsess (pid 68380)"; no launcher, all five; Stop, Research and left-group/session the same; dispose all six; SIGKILLed app all seven; finished turn "leftover (pid 69341)"; guard green, then the turn's own tree red; (g) "presence.Live read 0 while leader (pid 68448), ownpgrp (pid 68452), ownsess (pid 68453) still ran; the turn's ai_attempt row read ENDED while ownpgrp (pid 68452), ownsess (pid 68453) still ran". Item 3's Pause test at item 2: "Pause did not return: … and that turn was still running".
**Mutant** `Dispose` → `if (!Process.HasExited) End();`: at item 2 the turn-level (c) "5 s after the turn's own end (exit 0), the turn's tree still ran: leftover (pid 69508)"; at item 5 the Dispose-level (c) "5 s after ContainedProcess.Dispose after the session's leader had exited, the turn's tree still ran: leftover (pid 74365)" (the turn-level (c) stays green there: the resident launcher sweeps first). Reverted.
1. done — `AgentTreeTests`, 17 tests, (a)–(h) plus the failed-teardown path. Windows job arms in PowerShell, not cmd: cmd parses the Situation (ResumeOnStartTests' reason). The no-launcher (a) has no pre-detached orphan: only parent links prove such a turn's processes.
2. done — `TreeTeardown`/`ProcessTable` (Core; libproc on macOS, /proc on Linux, no `ps`). It freezes from the leader down parent links and across the session, re-proves pid + start after SIGSTOP (else SIGCONT, and the process is left), SIGKILLs, and repeats for at most 3 s. Never the app's session or group. On failure the grant is revoked at once and `TurnMeter` refuses every launch, naming the pids, until they end. Deviation: the session id is remembered at the start (= the leader's pid by construction), not at Held's latch, so a leader that exits unobserved still leaves its session findable.
3. done — Pause cancels the chair's conversation and every conversation the loop took a turn on before cancelling the loop itself. The loop keeps them as ConversationFor gave them, because asking the host at Pause would race its lazy dictionary. Presence ends after the teardown, never on the leader's exit; Run and the key sign-in use the same order.
4. done — `DisposeAsync` pauses (`Mission.PauseAsync`; the resume choice is asserted kept) and stops the AI first, once. `Quit` holds ShutdownRequested for at most 15 s. Reported, from the 12.1.1 IL: Avalonia's `Shutdown()` passes force and raises NO ShutdownRequested, so the update path never ran the old handler; `Exit` now runs the same stop, and MainWindow's comment is corrected.
5. done — `--spawn-contained` stays resident: setsid, then Process.Start with stdio inherited, and the exit code forwarded. It sweeps its session when the command exits or when getppid() changes; (f), the parent SIGKILLed, leaves every process dead within 5 s on macOS and ubuntu.
Extra commits. `2d35f6d9` (product) ends the tree the moment the leader exits: 37707921636 measured a finished turn whose leftover held stderr never committing, and Windows has been green since. `fd78a786` adds the Windows arms. `81ed13ce` moves the class into Timing, because its verdict is a 5 s ceiling over real processes. Numbers measured on 37712519465 under load: an in-memory test took 9.4 s (0.1 s here), three PowerShell turns were not up within 60 s at 01:49–01:52Z, and the same probe came up in 7 s from 01:53Z. No timeout was raised and no assertion changed. Test seams: `TreeTeardown.End(kill:)` and the internal `AgentSession.EndTree`.
**tests box: NOT RUN — ready: NO - his own OFT.Platform is open; nothing of ours runs beside it** (exit 5 at 02:28 and 03:21).
**Not done / not verified:**
- (f) on Windows and Windows' start gap go to U-contain-seats.
- A process that leaves the session AND loses its parent before a teardown cannot be proved the turn's and is left.
- On Unix with no launcher, a leftover holding the turn's pipes still holds the turn open.
- No app launched and no real codex turn run: the quit hold and the supervisor are proven in tests only.
