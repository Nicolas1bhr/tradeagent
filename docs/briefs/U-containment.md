# U-containment — the agent process is held in a job, given a clean environment, known by role on the pipe, and refused in live
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (operator authority in-process only; the four rules), `docs/COUNCIL.md` (rule 2, the
"Containment" lines of round 4, the `U-containment` line), `docs/RESUME-HERE.md` step 5 (b) and 6 (f), then `AgentSession.cs:413-438`,
`WorkspaceBuilder.EnvironmentFor` (`:121-133`), `GatewayPipeServer.cs:519-534,602-619,827-837`, `AtasConnector.cs:1915-1932`
(the bridge's peer-image rule), `SecretStore.cs`. Branch `u-containment`, worktree `~/Projects/ai-trading-software-for-mihael-
worktrees/U-containment`, rebased onto `main` first. Money path: every guard ships with a test that was RED before it and ONE
mutant watched going red, quoted. No box, no ATAS, no money; Windows-only code behind `OperatingSystem.IsWindows()`, proved on
the hosted windows runner through a draft PR (quote the run id), the Mac proving the rest.

**Why.** The turn process is started as the same OS user with `Process.Start` and nothing else: it inherits the app's whole
environment; `Kill(entireProcessTree)` is a walk a detached grandchild escapes; the pipe authenticates the MACHINE ACCOUNT (a
token the child can read) and cannot tell one role from another (one `TRADEAGENT_SESSION` for all, `AppHost.cs:121`); `trade.exe`
sits first on the child's PATH in a folder it can write; no Job Object, AppContainer or sandbox exists anywhere in `src/`.

1. **A job that dies with the app.** On Windows every agent process is placed in a Job Object with `KILL_ON_JOB_CLOSE`
   (P/Invoke, `CreateJobObject`/`SetInformationJobObject`/`AssignProcessToJobObject`, breakaway forbidden); on macOS the
   child is its own process group and the kill is the group's. RED (windows runner): a grandchild that detaches survives
   `CancelAsync` (today); after: gone with the job. Mutant (breakaway allowed) → red. Mac RED: a detached grandchild survives.
2. **A clean environment.** The child gets a WHITELIST, not the app's environment: `PATH` (app bin first), the platform's home,
   temp and locale variables, the vendor CLI's own home variable, and TradeAgent's `TRADEAGENT_SESSION`/`TRADEAGENT_WORKSPACE`/
   `TRADEAGENT_HOME`/`TRADEAGENT_PIPE`; everything else the app inherited is dropped. RED: a secret exported in the app's
   environment reaches the child's `psi.Environment` (today; `CoreTests.cs:260-270` tests the three entries, not the merge);
   mutant (the whitelist replaced by the inherited copy) → red.
3. **The pipe knows who is calling.** Each launch mints a per-attempt token (role, attempt id, expiry at the turn's end plus
   a grace) handed ONLY through the environment and required in `hello` beside the machine token; the server stamps role and
   attempt on `AgentContext`; a token of one role cannot act as another; an expired one is refused. The peer image is checked
   as the bridge does (`GetNamedPipeClientProcessId` → image path): the app's own `trade` under `Paths.Bin`, with the hash the
   app deployed, never under the vendor tools folder or a workspace. RED: a client with the machine token alone is served as
   the chair (today); mutant (the role check dropped) → Research places an order. Image RED: a copied `trade.exe` served.
4. **The protected configuration.** `Doctor` gains a `Containment` component (the job, the clean environment, the token, the
   image check, and "OS sandbox: NONE" said plainly); while `ModeIsLive && LiveActivated` the vendor CLI is REFUSED to start
   until an OS sandbox reports OK (none can today), the sentence on the card and in the guide; paper and observe unchanged.
   RED: `LIVE_AUTONOMOUS` activated starts codex (today); mutant (`ModeIsLive` alone) → `LIVE_CONFIRM` unactivated refused too.

Not this unit (`U-contain-2`, after the box proves it): an AppContainer with brokered file access and the macOS sandboxed
helper. `CONTRACTS.md` and `docs/COUNCIL.md` say what this unit closes and what stays open (same-user reads of `state/`).

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened; the draft PR's three runners green. Commit per
item, one sentence, no trailers. `## Report` (≤20 lines): tip sha, gate counts, the run id, per item RED and mutant, NOT done.


## Report

Tip `3735a17`, five commits over `main` `5fcccdd`, rebased through `U-budget-reserve` (its per-role open attempt is why the grant's attempt id is read per role now, `TurnMeter.OpenAttemptIdFor`). NO schema number taken: the grant register is in memory and the deployed-CLI hash is a file in `state/`.
Gate, Release: `--no-incremental` → `0 Warning(s) 0 Error(s)`. Touched classes 3x, identical each pass — unit (AgentEnvironment/PeerImageRule/LaunchGrantRegister/ProtectedConfiguration/JobObjectFlags) 30/30, integration (Containment/LaunchGrant/PipeContract) 61 passed 1 skipped, GatewayPipeBackpressure 34/34. Suites once: Unit 736/736, Fault 277/277, Integration 635 passed 1 skipped 0 failed. Test names: main 1402, branch 1441, NOTHING removed.
CI `34720169201` at `3735a17` and `34721448060` at the report commit: ubuntu and macos green in both; windows red on main's OWN known CRLF fixture red only (`DayOneStrategyTests`, recorded at `5fcccdd`, fixer `U-crlf-strategy-win`) — this unit's windows figures there are Fault 272/272 and Integration 547 passed 1 skipped 0 failed.
1. **Job/session.** Mac RED (product stashed to main's): "the turn's detached grandchild (pid 40448) was still running after CancelAsync". Windows RED, run `34718510974` (job discarded after creation): same test, "Probe: ticks=10147, middle.done=True". Mutant `SILENT_BREAKAWAY_OK`, run `34719666628`: that test red again plus "SILENT_BREAKAWAY_OK lets EVERY child start outside the job, asked for or not". Assigned immediately after `Process.Start` rather than suspended; the comment says why.
2. **Clean environment.** RED (`Apply` as the pre-unit merge): "TA_TEST_EXPORTED_CREDENTIAL … reached the agent process; the child inherits the app's environment unless the whitelist replaces it". Mutant (`Clear()` deleted): the same line.
3. **The pipe knows who is calling.** RED (role gate removed): "a client holding nothing but the machine token … was served an order as though it were the Operations Director". Mutant (`MayPlaceOrders => true`): "the Research Director's launch placed an order". Image mutant (workspace clause dropped): the copy inside the agent's own tree was then refused only by the path rule, so the verdict stopped naming the workspace.
4. **Protected configuration.** RED: `Assert.NotNull() Failure: Value is null`, and at the launch "the vendor CLI ran in the armed live configuration". Mutant (`ModeIsLive` alone): `LIVE_CONFIRM` unactivated refused too.
NOT done: no OS sandbox — `Containment.Sandbox()` says NONE, so same-user reads AND WRITES of `state/` stand and a Unix session is escapable by a process that calls `setsid` itself (`U-contain-2`); the peer-image kernel call is Windows-only and the rule is unenforced elsewhere, which the Doctor row says; nothing was run on the box.
