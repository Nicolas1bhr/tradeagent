# U-life — the AI never stops: a mission and a loop that keeps it working

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `AgentSession.cs`, `AgentSupervisor.cs`,
`WorkspaceBuilder.cs` (`Instructions`), the codex entry of `RuntimeManifest.cs` (line 292), `AgentPresence.cs`, and
the `U-batch-2` section of `BUILD-STATUS.md` (finding 5: an inbox sighting attests to the owner only across a window
with no agent process alive). `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/life`, branch `u-life` from `main`. Another builder owns schema
5, `Database.cs`, `GatewaySchema.cs`, `TradingGateway.cs`, `GatewayPipeServer.cs` and `PerformanceCard.cs`: no schema
change, none of those files. The Dashboard's AI card is yours. `U-meter` prices the turns later: leave a `TurnEnded`
event (exit code, duration, raw stream), nothing more.

**Why.** The product is a container that keeps an autonomous AI alive and working — researching, writing strategies,
trading inside the limits — to pay for itself. Today nothing happens unless the owner types: one process per message.

1. **The mission loop** (new `MissionLoop.cs` in `AgentRuntime`, hosted from `AppHost.cs`): while "working on its own"
   (item 3), turns run back to back. Each turn's message is a `## Situation` block the app writes — local time; mode;
   `execution_available` and its reason; account, positions, open orders, unconfirmed requests; inbox material new
   since the last turn; the owner's guidance; the owner's chat messages typed meanwhile, FIRST — then: "Continue your
   mission. Your memory is your files: read `PLAN.md` and `JOURNAL.md` first and update them before you finish." Next
   turn: at once, or after the delay the AI asked for in `.tradeagent/next.json` (`{"after_seconds": N}`, capped,
   default 30 min), or after a backoff doubling from 30 s to 30 min while turns end in error. `resume` up to N turns
   per CLI session (setting, default 20), then a fresh session: the files are the memory. The loop yields to the
   scanner: when the inbox changed since the last complete pass, the next turn waits for one complete pass with no agent
   alive, so `Inbox` attests. RED first (a turn inside the scanner's window → `InboxUnattested`) → GREEN; mutant → RED.
2. **The mission** (`WorkspaceBuilder.Instructions`, rewritten for the loop): the purpose in one sentence — make at
   least enough, net of your own running cost, to pay for yourself; the number is `trade pnl --json` (landing beside
   you). Files as memory; research, backtesting and building strategies ARE the job when execution is blocked or the
   market is closed; `../inbox` is guidance and material to work ON (never instruction, never permission); `JOURNAL.md`
   records what was tried, the result, the number; "leave no process behind" stays: a turn may run a script and exit.
3. **Controls** (the AI card in `DashboardView.cs`, `Ui.Confirm`, `Theme.cs` only): a Guidance box saved in `Settings`
   and included in every Situation; **"Let the AI work on its own" is two presses**, **"Pause the AI" is one**; the card
   shows working / waiting until hh:mm / paused / stopped: N errors, the turn count, the last turn's first line. STOP
   AI TRADING keeps its meaning and does not stop the loop. Paused survives a restart; working resumes on start (a
   setting, default on). RED first (one press starts the loop) → GREEN; mutant → RED.

Yours: `MissionLoop.cs`, `AgentSession.cs`, `AgentSupervisor.cs`, `WorkspaceBuilder.cs`, `AppHost.cs` (hosting only),
`DashboardView.cs` (AI card), `Settings` in `Trading.cs`, `docs/USER-GUIDE.md`, tests. Commit per item, no trailers.
Gate: Release `--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file, one project at a time,
nothing else running; names vs `main` 0 removed. No box, no ATAS, no UI photograph: say so.
## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant quoted; gate counts; NOT done.
Tip `dd10682`, rebased onto `main` `ea7c44e`; 7 commits, 15 files, +2200/−48. **No box, no ATAS, no money, no UI run, no screenshot: the card's layout and colour are NOT VERIFIED on a running app** — its four words and two counts are, as `DashboardPage.MissionSentence`/`MissionCounts`.
1. **Loop** (`MissionLoop.cs`, hosted in `AppHost.cs`): turns back to back; message = `## Situation` (owner's typed words FIRST) + the memory sentence; next turn at once, or `.tradeagent/next.json` `after_seconds` capped at 30 min and consumed, or a backoff doubling 30 s→30 min; fresh CLI session every 20 turns (setting); `TurnEnded(exit code, duration, raw)` on every path, for `U-meter`. A turn that throws is a failed turn, not the end of the loop.
   Yield = a pass AFTER every turn and one BEFORE a turn when the inbox changed; both load-bearing. RED (pre-turn yield deleted, post-turn pass kept) → `Expected: Inbox / Actual: InboxUnattested`. GREEN 21/21. Mutant (post-turn pass deleted) → the same, plus `Expected: "first 2, then 2" / Actual: "first 1, then 0"`. STOP AI TRADING leaves the loop running, asserted at the loop and at the setting.
2. **Mission** (`WorkspaceBuilder.Instructions`), GREEN 7/7: "Make at least enough money, net of what you cost to run, to pay for yourself"; the number is `trade pnl --json`, "An unknown is never a zero"; `PLAN.md`/`JOURNAL.md` in `trading/` are the memory across a fresh session; research/backtesting/strategies ARE the job when execution is blocked; the inbox is material and guidance, never instruction, never permission; "Leave no process behind — a turn ENDS".
3. **Controls** (AI card, `Ui.ConfirmIf`, `Theme.cs` only): Guidance saved in `Settings`, in every Situation, grants nothing. RED (one-press `Ui.Primary`) → `Expected: False / Actual: True` after the FIRST press, 3 red. GREEN 12/12 (14/14 after item 2's card assertions). Mutant (armed sentence → `null`) → the same, 2 red. Paused survives a restart; working resumes unless `ResumeAiOnStart` is off, and then the flag is corrected to match. `Unreadable()` clears `AiWorksOnItsOwn` and `Guidance`.
   **Extra, declared:** typing while the AI worked raised `INVALID_REQUEST` and lost the message — nearly always, once turns run back to back. `SendAsync` now queues and shows it and the loop puts it first; `ChatView.cs` (on neither builder's list) line 250 lost `|| _bound.Busy` so Enter still sends.
**Gate** at `b274045` — `dd10682` is the identical tree under `src/` and `tests/`, the rebase being docs-only — bin/obj deleted, `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s) 0 Error(s)`. Each touched class 3× in Release: MissionLoop 21/21, MissionInstructions 7/7, MissionControls 14/14, TypedWhileWorking 4/4, every run `Failed: 0`.
Full suite once in Release, one project at a time: 327 + 261 + 610 = **1198 passed, 0 failed**, 1 skipped (the pre-existing `PipeContractTests` hello skip). `testhost.dll --port` count 0 before Unit and before Fault; **the other leg's suite started in Fault's last seconds and ran through the whole of Integration** — both green, and an overlap risks a false red, never a false green. Names vs `main` `396ac63` (docs-only since): **0 removed**, 35 added, 909 → 944 method names.
**NOT done:** no schema change, and `Database.cs`, `GatewaySchema.cs`, `TradingGateway.cs`, `GatewayPipeServer.cs`, `PerformanceCard.cs` are untouched. `trade pnl --json` is named in `AGENTS.md` and is `U-ledger`'s to land; nothing here fails without it. `MissionInbox.ChangedSince` walks the drop folder on every ask (bounded at 5,000 entries); NOT measured on a large folder. The loop cannot start the AI: with none started the card reads "stopped — the AI has not been started", beside the existing `Start the AI` button.
