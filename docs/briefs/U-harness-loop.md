# U-harness-loop — the mission loop proven over a harness turn end to end, and the backtest granted to a harness worker
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` ("Workers run on an app-owned harness"; rules 3–6), the `## 2026-09-13 —
U-api-worker landed` section of `BUILD-STATUS.md` (its NOT VERIFIED line is this unit), then `AgentRuntime/ApiConversation.cs`,
`GrantedWorkerTools.cs` (the closed `trade` op list), `AppHost.ConversationFor(role)`, `MissionLoop.TurnAsync` (admission, the committed
transition, the relay), `CouncilRelay.cs`, `tests/TradeAgent.UnitTests/ApiWorkerTests.cs` and `HarnessRoleTests.cs` (the `FakeProvider`
fixture: canned tool calls, usage on each response), `CouncilLoopTests.cs`/`MissionLoopTests.cs` (the loop fixtures), `Gateway/Backtests.cs`.
Branch `u-harness-loop`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-harness-loop`, rebased onto `main` first. Every
item ships with a test RED before it and ONE mutant, quoted. No box, no ATAS, no money, no network, no real provider call. **No schema number.**

**Why.** `U-api-worker` proved each piece of the harness by driving the conversation directly; nothing drives `MissionLoop` over it, so the
loop's admission (`TurnMeter.Begin` inside its transaction), the one committed transition at turn end (`U-turn-commit`), the relay of a
harness turn's `out/report-<attempt>.md` and the Situation it is given are unproven end to end for the runtime Research will actually run
on. And the harness's closed `trade` op list does not carry `backtest`, so a Research worker cannot ask for the one measurement the runner
exists to give it — a decision `U-api-worker` left to a brief rather than inheriting.

1. **A loop turn on the harness, end to end.** A `MissionLoop` fixture with Research on `openai-api` against `FakeProvider`: the loop admits
   the turn (one `ai_attempt` row, reserved), the provider's canned calls `read_file` the Situation's named file, `write_file` a report
   into `out/report-<attempt>.md` and end the turn with usage; the turn ends ENDED with the exact price of the model on each response,
   the `tool_call` rows carry the attempt id, the committed transition publishes the report under that attempt and the Operations
   `task:` event is raised — all in the loop's own path, nothing called directly. RED: no such test exists. Mutant (`ConversationFor`
   returning the CLI conversation for a role on the harness): the fake provider sees no request and the assertion on `tool_call` fails.
2. **A turn that trips the allowance mid-loop is committed like any other:** `CONTEXT_BUDGET_EXCEEDED` ends the attempt with the reason,
   the staged files are kept and published under that attempt on the SAME commit, the next Situation says the turn was cut and why.
   RED: the trip path bypasses the committed transition (an ENDED row with no publication) — or, if it does not, say so with the run and
   keep the test as the pin. Mutant (the trip returning before `CommitTurn`): an ENDED attempt with no publication.
3. **`backtest` granted to a harness worker:** the closed `trade` op list gains `backtest` for every role (it is read-only for the gateway
   and runs under the launch's own identity, `U-runner-3`); one run at a time per role holds; `CONTRACTS.md` and the guide say a worker may
   ask for a backtest of a program in its own home. RED: a Research worker's `trade backtest …` refused as an unknown op. Mutant (`backtest`
   added to `Ops.Mutating` instead): Research refused by the role check.

Not this unit: the chair on the harness, a real provider call, a second provider, memory search, a grant table with fencing, keys on disk.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0 failed;
touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers. Append `## Report` (≤20
lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
