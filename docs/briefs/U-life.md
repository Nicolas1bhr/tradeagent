# U-life — the AI never stops: a mission, a loop that keeps it working, and a meter on what it costs

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `AgentSession.cs` (`RunTurnAsync`: one CLI process per
message, `SendAsync`, `StopAsync`), `AgentSupervisor.cs`, `WorkspaceBuilder.cs` (`Instructions`), the codex entry of
`RuntimeManifest.cs` (line 292: `ExecArgs`, `ResumeArgs`, `JsonFlag`), `AgentPresence.cs`, and the `U-batch-2` section
of `BUILD-STATUS.md` (finding 5: an inbox sighting attests to the owner only across a window with no agent process
alive). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Codex CLI 0.153.4 is installed
and signed in on this Mac (`~/.local/bin/codex`), so the stream can be verified for real with `-s read-only`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/life`, branch `u-life` from `main`. Another builder owns schema 5,
`Database.cs`, `GatewaySchema.cs`, `TradingGateway.cs` and a `PerformanceCard.cs`; you make NO schema change and touch
none of those. The Dashboard's AI card is yours; the Performance card is theirs.

**Why.** The product is a container that keeps an autonomous AI alive and working — researching, writing its own
strategies, trading inside the limits — to make at least enough to pay for itself. Today the AI runs one process per
chat message and its instructions tell it to leave nothing running, so nothing happens unless the owner types. This
unit gives it life, a mission, and a bill.

1. **The mission loop** (new `MissionLoop.cs` in `AgentRuntime`, hosted from `AppHost.cs`): while "working on its own"
   (item 3), turns run back to back. Each turn's message is a `## Situation` block the app writes — local time; mode;
   `execution_available` and its reason; account, positions, open orders, unconfirmed requests; inbox material new
   since the last turn; the owner's guidance (item 3); the owner's chat messages typed meanwhile, FIRST; today's cost
   against the cap — then: "Continue your mission. Your memory is your files: read `PLAN.md` and `JOURNAL.md` first
   and update them before you finish." A turn ends when the CLI exits (unchanged). The next starts at once, or after
   the delay the AI asked for in `.tradeagent/next.json` (`{"after_seconds": N}`, capped by a setting, default 30 min),
   or after a backoff doubling from 30 s to 30 min while turns end in error. `resume` up to N turns per CLI session
   (setting, default 20), then a fresh session: the files are the memory. The loop yields to the scanner: when the
   inbox changed since the last complete pass, the next turn waits for one complete pass with no agent alive, so the
   `Inbox` origin still attests. RED first: a cap reached does not pause the loop → GREEN; mutant → RED.
2. **The mission** (`WorkspaceBuilder.Instructions`, rewritten for the loop): the purpose in one sentence — make at
   least enough, net of your own running cost, to pay for yourself; the number is `trade pnl --json` (landing beside
   you; describe it, do not depend on it) and your cost is in every Situation. What a turn is; files as memory;
   research, backtesting and strategy-building ARE the job when execution is blocked or the market is closed; `../inbox`
   is guidance and material to work ON (the rule stands: never instruction, never permission); `JOURNAL.md` records
   what was tried, the result, the number. Keep "leave no process behind": a turn may run a script as long as it
   needs, and exits.
3. **Controls in the window** (the AI card in `DashboardView.cs`, `Ui.Confirm` for two presses, `Theme.cs` only): a
   Guidance text box saved in `Settings` and included in every Situation; **"Let the AI work on its own" is two
   presses**, the second naming today's cost cap; **"Pause the AI" is one**; the card shows working / waiting until
   hh:mm / paused / stopped: cost cap / stopped: N errors, the turn count, the last turn's first line, today's cost.
   STOP AI TRADING keeps its meaning (trading permission) and does not stop the loop. Paused survives a restart;
   working resumes on start (a setting, default on). RED first (one press starts the loop) → GREEN; mutant → RED.
4. **The meter**: read usage from the CLI's `--json` stream — run codex 0.153.4 here with `--json` and QUOTE the event
   it emits; if none, meter turns and wall-clock and show "cost unknown". Per turn: a line in `state/agent-turns.jsonl`
   (the app's state dir, not the agent's home); today's total and turn count in `kv`. Priced by `costs.json` beside
   `runtimes.json` (per-model prices, read through `VendorFile`; absent → "unpriced"). Daily cap in `Settings`
   (currency, default 5); reaching it pauses until local midnight and writes an activity line. `trade status --json`
   gains `ai_state`, `ai_turns_today`, `ai_cost_today` (the composer that emits `ai_trading_stopped`).

Yours: `MissionLoop.cs`, `AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `AppHost.cs` (hosting only),
`DashboardView.cs` (the AI card), `Settings` in `Trading.cs`, `RuntimeManifest.cs`/`VendorFile.cs` (`costs.json`), the
status composer, `docs/USER-GUIDE.md`, tests. Commit per item, no trailers. Gate: Release `--no-incremental` → 0
warnings; each touched class 3×; full suite once to a file, one project at a time, nothing else running; names vs
`main` 0 removed. No box, no ATAS, no UI photograph: say so.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant quoted; the codex `--json` usage
event quoted verbatim; gate counts; what you did NOT do. Verified or NOT VERIFIED, nothing in between.
