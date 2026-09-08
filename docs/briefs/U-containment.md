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
