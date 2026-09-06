# U-life — the AI never stops: a mission and a loop that keeps it working

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `AgentSession.cs` (`RunTurnAsync`: one CLI process per
message; `SendAsync`; `StopAsync`), `AgentSupervisor.cs`, `WorkspaceBuilder.cs` (`Instructions`), the codex entry of
`RuntimeManifest.cs` (line 292: `ExecArgs`, `ResumeArgs`), `AgentPresence.cs`, and the `U-batch-2` section of
`BUILD-STATUS.md` (finding 5: an inbox sighting attests to the owner only across a window with no agent process alive).
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/life`, branch `u-life` from `main`. Another builder owns schema 5,
`Database.cs`, `GatewaySchema.cs`, `TradingGateway.cs`, `GatewayPipeServer.cs` and a `PerformanceCard.cs`: you make NO
schema change and touch none of those. The Dashboard's AI card is yours. A later unit, `U-meter`, prices the turns; you
leave a seam (a `TurnEnded` event carrying the turn's exit code, duration and raw stream) and nothing more.

**Why.** The product is a container that keeps an autonomous AI alive and working — researching, writing its own
strategies, trading inside the limits — to make at least enough to pay for itself. Today the AI runs one process per
chat message and its instructions tell it to leave nothing running, so nothing happens unless the owner types.

1. **The mission loop** (new `MissionLoop.cs` in `AgentRuntime`, hosted from `AppHost.cs`): while "working on its own"
   (item 3), turns run back to back. Each turn's message is a `## Situation` block the app writes — local time; mode;
   `execution_available` and its reason; account, positions, open orders, unconfirmed requests; inbox material new
   since the last turn; the owner's guidance (item 3); the owner's chat messages typed meanwhile, FIRST — then:
   "Continue your mission. Your memory is your files: read `PLAN.md` and `JOURNAL.md` first and update them before you
   finish." A turn ends when the CLI exits (unchanged). The next starts at once, or after the delay the AI asked for in
   `.tradeagent/next.json` (`{"after_seconds": N}`, capped by a setting, default 30 min), or after a backoff doubling
   from 30 s to 30 min while turns end in error. `resume` up to N turns per CLI session (setting, default 20), then a
   fresh session: the files are the memory. The loop yields to the scanner: when the inbox changed since the last
   complete pass, the next turn waits for one complete pass with no agent alive, so `Inbox` still attests. RED first
   (a turn starts inside the scanner's window → origin `InboxUnattested`) → GREEN; mutant → RED.
2. **The mission** (`WorkspaceBuilder.Instructions`, rewritten for the loop): the purpose in one sentence — make at
   least enough, net of your own running cost, to pay for yourself; the number is `trade pnl --json` (landing beside
   you: describe it, do not depend on it). What a turn is; files as memory; research, backtesting and building
   strategies ARE the job when execution is blocked or the market is closed; `../inbox` is guidance and material to
   work ON (the rule stands: never instruction, never permission); `JOURNAL.md` records what was tried, the result and
   the number. Keep "leave no process behind": a turn may run a script as long as it needs, and exits.
3. **Controls** (the AI card in `DashboardView.cs`, `Ui.Confirm`, `Theme.cs` only): a Guidance text box saved in
   `Settings` and included in every Situation; **"Let the AI work on its own" is two presses**, **"Pause the AI" is
   one**; the card shows working / waiting until hh:mm / paused / stopped: N errors, the turn count and the last turn's
   first line. STOP AI TRADING keeps its meaning and does not stop the loop. Paused survives a restart; working
   resumes on start (a setting, default on). RED first (one press starts the loop) → GREEN; mutant → RED.

Yours: `MissionLoop.cs`, `AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `AppHost.cs` (hosting only),
`DashboardView.cs` (the AI card), `Settings` in `Trading.cs`, `docs/USER-GUIDE.md`, tests. Commit per item, no trailers.
Gate: Release `--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file, one project at a time,
nothing else running; names vs `main` 0 removed. No box, no ATAS, no UI photograph: say so.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant quoted; gate counts; what you did
NOT do. Verified or NOT VERIFIED, nothing in between.
