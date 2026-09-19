# SURVEY — inspect the reachable loop before the first paper-line unit (read-only)
**Arrow:** none closed; this leg settles WHICH arrow of the loop in `docs/PRINCIPLES.md` is broken earliest. **Result that proves it:**
`docs/INSPECTION-2026-09-19.md` — five tables, ≤ 100 lines, every row anchored to `file:line` at the inspected sha.
You are a fresh general-purpose leg on worktree `~/Projects/ai-trading-software-for-mihael-worktrees/survey`, branch `survey`. You change
NOTHING under `src/`, `tests/` or `docs/` except the one file you write, and you commit only that file (`git add docs/INSPECTION-2026-09-19.md`,
one-sentence message, no trailer). No product code, no test, no other doc, no push.

Read, in order, and nothing else as a gate: `docs/PRINCIPLES.md` in full; `manager-prompt.md` § 4–6; `CLAUDE.md`. Open source as the questions
need it. `git rev-parse HEAD` is the inspected sha; cite it at the top of the report.

Label every cell by its kind: **SOURCE** (read in the code, `file:line`), **RUN** (a command you executed, its output quoted in ≤ 1 line),
**HIST** (a claim from `BUILD-STATUS.md` or `docs/RESUME-HERE.md`, with its date). Source inspection is never runtime verification; a public
method with no production caller is not a connected capability; a test-only caller is not a production caller.

The five tables, headed exactly so:
1. **Fits; preserve** — the working gateway, grants, recovery, ledgers, budgets, strategy evaluator, referee, allocation and reporting
   primitives, and the property each protects.
2. **Concrete obstructions** — a specific action an agent needs, the restriction it hits, the smallest correction that keeps the real boundary.
   A missing tool can be a blocker with nothing discarded; a size limit can be reasonable even when it rejects output.
3. **Missing connections** — the path ACTUALLY available from a model turn through data, program creation, backtest, candidate submission,
   verdict, paper allocation, deployment, fill feedback and another autonomous decision; include delegation and code execution. One row per
   arrow: connected / test-only / absent, with the evidence.
4. **Hard boundaries and limitations** — what is enforced and where, what is merely stated, the isolation and account conditions the next
   demonstration needs; keep a protected evaluation distinct from a mechanics-only run.
5. **Smallest next slice** — the earliest broken dependency, the subsequent forward-paper connection, the acceptance evidence, any genuine
   owner-only prerequisite; a short queue ordered by dependency (unit name, arrow closed, ≤ 1 line each).

Recheck on THIS checkout, never copy (`manager-prompt.md` § 4, anchored at `90af8e4`): `GrantedWorkerTools.cs` six tools, no exec/spawn,
`Writable`; `TradingGateway.Referee` with no pipe op or verb, `Referee.Verdict` with no production caller, campaign setup and holdout selection;
`StrategyEvaluator` reached only by `Backtest.cs`/`StrategyInterpreter.cs`, `IntentDecision.From` test-only, nothing turning a `StrategyIntent`
into a `PlaceIntent`; `TradingGateway.Allocate` behind the owner's press only; `CouncilRelay.ReportLines`, `WorkspaceRevisions` restore,
`BoundaryBaselines`; `CouncilBoundaries` wakes/deadlines/assessments/dispositions traced to ALL consumers and the agent decisions they feed;
`Containment.Sandbox()` NONE; `FakeBroker.BasePrice` fixed quotes. Trace what a real model can DO through the offered tool schemas and handlers
(`GrantedWorkerTools`, the pipe ops, the `trade` verbs, the mission loop's turn) and contrast it with the vendor CLI runtime's reach and isolation.

Allowed RUN checks: `git grep`; `dotnet build TradeAgent.sln -c Release` (`export PATH="$HOME/.dotnet:$PATH"`); an existing test class by
`--filter`; `trade --help` and read-only verbs against a scratch home under your scratchpad directory (`TRADEAGENT_HOME`), never the real one.
Forbidden: seeding rows, the Windows box, any provider or venue call, any order, any change to the tree beyond the report.
Finish with a ≤ 10-line message: the tip sha, the report's `wc -l`, the earliest broken dependency in one sentence, and what you did NOT trace.
