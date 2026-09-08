# U-api-worker — the app-owned harness: one provider, one role on it, every tool a grant, every boundary counted
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (the no-terminal rule; a pasted key in the app's own window is allowed), `docs/COUNCIL.md`
("Workers run on an app-owned harness", rules 2–5, the round-4 grant fields, the `U-api-worker` line), `docs/RESUME-HERE.md`
step 6, then `IAgentRuntime.cs`, `AgentSession.cs:22-69` (`IAgentConversation`), `AgentSupervisor.cs:44-67`, `AppHost.cs:60-124`,
`TurnMeter.cs` (`Begin`, `Close`, `TurnUsage.Read:80-121`), `ListPrices.cs:125-144`, `GatewaySchema.cs`. Branch `u-api-worker`,
worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-api-worker`, rebased onto `main` first. Money path: every guard
ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money, no real provider call.

**Why.** Workers cannot be capped or tool-restricted on a vendor CLI ("its caps are advisory"). The only runtime is
`CliAgentRuntime` (`AgentSupervisor.cs:49`); `IAgentRuntime`/`IAgentConversation` are the seams; the meter, the prices, the role
folders and the pipe's read ops exist. A key must not sit on disk beside an uncontained CLI, so this slice holds it in memory.

1. **`ApiAgentRuntime` (id `openai-api`)** — manifest data: base URL, the OpenAI models `ListPrices` prices (a third `foreach`
   tagging `openai-api`); `ApiConversation : IAgentConversation` runs the turn: the role's mission and the Situation as input,
   the provider's tool-calling loop executed BY THE APP (each call → item 2) until the model ends the turn or a budget trips;
   `TurnEnded` carries `Usage` summed over every response, the model named on each (exact price, never the estimate). RED:
   `RuntimeCatalog.Require("openai-api")` throws (today); mutant (usage from the last response only) → a two-request turn priced as one.
2. **Tools are grants, default deny.** A fixed set the app implements: `read_file` and `list_files` inside the role's own
   folder and `in/` (bounded bytes per call and per turn); `write_file` inside `out/` and the role's `trading/` only (staged,
   the relay and `U-turn-commit` publish); `trade` → the pipe's READ ops for every role, the mutating ops for Operations
   only, under the app-assigned identity (role + attempt); `data` (`data-list`/`data-bars`), `report`. No shell, no HTTP, no
   packages, no path outside the folder; every call written to a `tool_call` row (attempt, tool, argument summary, bytes,
   served or refused) — the observed-deliveries record. RED: Research's `trade buy` served (today's pipe would); mutant (the
   role check dropped) → served. Path RED: `read_file("../operations/PLAN.md")` served.
3. **Every boundary counted.** `TurnMeter.Begin` reserves before the first request; per request the app counts the input it
   sends and caps output with the provider's max-output parameter; cumulative input, output and retrieval bytes are checked
   against `TurnAllowance` before EACH request; a turn over its allowance ends `CONTEXT_BUDGET_EXCEEDED` (staged files kept,
   the row saying so); usage that never arrives is unresolved, never zero; a never-answering endpoint fails in seconds. RED: a
   fourth request runs past the allowance; mutant (the check after the request) → the same. Lost-usage RED → the reservation.
4. **One role on it.** The owner chooses per role runtime and model on the Safety page (Research defaults to `openai-api`
   once a key is held, Operations stays on codex); `ConversationFor(role)` picks the runtime; the key is pasted on the Safety
   page (`PasswordChar`), held in memory only, never written, cleared on exit, the sentence saying why, and the daily report's
   AI-spending section says "harness key: held / not held". RED: a role on `openai-api` with no key starts anything.
5. **Tests over a loopback `HttpListener`** playing the provider (canned tool calls, usage on each), never the network (the
   vendor-reach scan extended to the provider's host); `CONTRACTS.md` and the guide describe the harness.
Not this unit: memory search, the chair on the harness, a grant TABLE with fencing, a second provider, keys on disk (`U-contain-2`).

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file →
0 failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
`## Report` (≤20 lines): tip sha, gate counts, per item its RED and mutant, what you did NOT do.
