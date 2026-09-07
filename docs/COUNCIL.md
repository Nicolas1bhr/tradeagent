# The council — how the never-stopping mission is run

**Doctrine from 2026-09-07.** The owner's direction that day: the mission "should NOT be a single agent, it should be a
council of very smart top level manager agents with different tasks that run their own teams on cheaper models and with
incredible persistent memory and context etiquette." Debated in three written rounds between the manager (Claude) and
GPT-6 Astra on the day the single loop (`U-life`) first ran on a screen — four turns, 5.07 USD, no order, the AI spending
its first turn discovering what the simulator was. Only Astra's final answers were used, never its reasoning stream. This
file extends `docs/HOW-WE-BUILD.md` from the build to the product and changes nothing in `CLAUDE.md`: operator authority
stays in-process, the inbox stays data, the gateway's limits stay code. Astra's last word: no blocker to adopting this as
the build plan; it is not evidence that containment, accounting or unattended live execution already satisfy it.

## The ten rules — each enforceable by code or by a gate, in priority order

1. Only in-process owner controls grant authority, and every order passes the code-enforced capability, freshness,
   reconciliation, capital and loss gates regardless of any agent's opinion or of the AI budget that remains.
2. Agents and submitted strategies cannot reach credentials, the active controls, another role's writable state or the
   private evaluation evidence; inbox material never grants authority.
3. Every inference launch has a durable task, attempt, input revision, execution identity and a role-and-global spending
   commitment made BEFORE launch; usage the app never received stays unresolved, never zero, until reconciled, and an
   unresolved commitment survives a restart and midnight.
4. Hard-cap execution admits only requests whose cost has an enforceable bound and counts every retry; runtime, billing
   basis and enforcement are recorded apart — subscription charges, API charges and list-price equivalents are three
   figures, never one — so the owner never reads an advisory number as a cap.
5. A worker starts fresh from a bounded brief and capability snapshot the app generated, uses only the tools it was
   given, and publishes memory and reports only inside validated size and evidence limits.
6. The app accepts a worker's output through staged validation, immutable publication and one committed transition; a
   superseded attempt cannot publish; an uncertain external operation is reconciled before any retry or handoff; and an
   app-owned operation record carries a request's originating-session ownership across a replacement, because
   reconciliation alone never lets a fresh worker reuse another session's request id.
7. Persisted, deduplicated events schedule one decision owner per task with a deadline and bounded retries; justified
   idleness launches no inference; the inbox scanner attests only across proven quiescence of every managed agent.
8. A promoted, immutable strategy version executes through the app's runner with no inference on the signal path; an
   expired opportunity takes the policy's safe outcome, never a late trade.
9. Promotion needs app-computed evidence bound to code, parameters, dependencies, data, evaluator, execution-and-cost
   model and scoring-policy versions — a changed assumption invalidates the evidence that rested on it — with causal data
   access and campaign-wide trial limits that survive a team's replacement.
10. The app generates the daily factual report itself; the two directors are invoked only under the fixed review policy
    and its spending limits; every decision is recorded with its evidence, its owner and its deadline.

## The shape

**Roles survive agents.** A role is a workspace folder with an app-managed mission file, `trading/PLAN.md` and
`trading/JOURNAL.md`, a curated memory with an index, a model and a daily budget. An agent is one bounded attempt at one
task for one role; a killed attempt recovers to an identifiable state from the files and the task record — "loses nothing"
is a promise nobody can keep, uncommitted computation is lost. The roles:
- **Research Director** — hypotheses, experimental design, priorities, the Strategist teams.
- **Operations Director, who chairs** — scheduling, allocation of the budget across roles, the Engineer teams,
  execution readiness, the note on the owner's report.
- **Strategist teams** — data, hypotheses, backtests, strategy programs, on cheaper models with small briefs.
- **Trader** — supervises the app's strategy runner within its existing authority: exceptions, reviews, reconciliation.
  A trade contingent on the Trader's inference would put inference on the signal path, so there is none.
- **Engineer** — data pipelines, tooling, the cost of running. It may submit artifacts; it can never update its own
  supervisor, the trusted configuration or the evaluator's storage.

**What is code and never a role:** the risk limits (`U-loss`, later the prop-firm rulebooks), the referee, the capital
allocator, the budgets and their reservations, the task ledger, the scanner, the daily report. An LLM may investigate a
risk assumption; its opinion grants nothing.

**Two seniors, at boundaries the app fixes.** The strongest model is spent only at consequential boundaries — promotion of
a strategy, a change of allocation, the launch of a research campaign, the retirement of a team, the post-mortem after a
loss-budget event — and there both directors submit an assessment before either sees the other's; one bounded challenge,
one disposition, a deadline with a predetermined default, and code applies the promotion and allocation policy so neither
director can veto an eligible deployment forever. Count invocations, not meetings: two assessments are two turns.
Deduplicate boundary events by entity and revision, so repeated proposals cannot manufacture senior spend; the whole of it
sits inside the owner's ceiling. No paid standup when nothing material changed.

**Context etiquette is the build doctrine applied to the swarm.** A brief of at most 40 lines AND a validated size limit
in, a report of at most 20 lines out; files as the handoff; typed results, evidence references, causal ids and lineage
crossing; inherited transcripts and authority-bearing prose never. A stalled worker is replaced by a fresh one holding the
failing evidence and none of the predecessor's reasoning; two fresh fixers failing on the same item mean the item is
mis-stated and is rewritten as a structural fix, never sent to a third; a rate-limit or process kill is not such a
failure. The `## Situation` is a timestamped snapshot, not current truth: snapshot id and freshness; role, task, attempt,
deadline and the event that caused the wake; what changed since the role's last committed cursor; capability and schema
versions, fixture versus market data, execution block reasons; the promoted strategy and unresolved request ids; the
budget remaining after reservations; the owner's in-app words, separated from untrusted material; the required output
schema and the exact memory paths. The gateway revalidates the state when an action arrives, whatever the snapshot said.

**Memory in four tiers.** (i) Measured facts — the app's tables (fills, costs, materials), read-only to agents. (ii) The
app's execution record — tasks, events, attempts, leases, reservations, commits. (iii) The role's working files, versioned
in the workspace. (iv) A curated per-role memory: decisions, findings and runbooks as small notes, each marking app
measurement versus external source versus agent interpretation, with an index (id, title, tags, status, evidence, as-of,
review date, supersedes) loaded every turn and a weekly consolidation that merges, expires and archives while preserving
evidence and superseded revisions — and costs no paid turn in a week where nothing changed. Size budgets on every file,
enforced by the app at publication: an invalid publication is rejected and the last valid plan and index stand. A vector
store only when measured retrieval failures show one is needed, and then as a discovery aid, never the authority.
"Incredible persistent memory" means a fresh worker knows what is decided, why, on what evidence, what is stale and what
happens next, without reconstructing history.

**Never-stopping is a scheduler, not a loop.** Persisted events — closed bar or strategy boundary, fill or rejection or
disconnect or reconciliation, validated data arrival, experiment completion or promotion verdict or health breach, budget
renewal, scheduled review, the owner's message, new inbox material — coalesced where the task permits, never for fills and
order transitions. Priority: app-side protection and reconciliation outside the AI queue; time-sensitive execution
exceptions and owner controls; work that unblocks eligible deployment or required data; research and engineering;
housekeeping. Weighted fair shares with priority aging; no task manufactures priority by calling itself urgent. Calendars
are per connector and per instrument — a continuous venue inherits no other venue's weekend; a closed venue means bounded
research, data repair and maintenance if tasks justify them, or nothing. A promoted strategy declares its timeframe, its
required data freshness and its maximum decision age, and the runner checks them again when the intent reaches execution.

**Budgets are reserved, not checked.** Caps are per role AND global, reserved atomically before launch and reconciled
against reported usage; the Operations Director reallocates only unreserved allowance and can never raise the owner's
ceiling; when AI spending stops, app-side protection and reconciliation continue and never depend on an inference
allowance. Astra's evidence, kept: the 5.0683 USD against a 5 USD cap on 2026-09-07 shows a check of completed spending
is not a cap. Four quantities, bounded separately: per-request input, cumulative task input, output (visible plus
provider-accounted reasoning) and retrieval (what the app's tools return, the one the app controls outright).

**Workers run on an app-owned harness; seniors may keep the vendor CLI.** Cheap teams with enforceable tools and a
bounded context need control at every model-and-tool boundary, which the codex CLI's documented contract does not
provide: its sandbox restricts writes, not what enters its context, so its caps are advisory. The harness calls a
provider API directly with a key the owner pastes in the app's own window (the no-terminal rule holds), reserves before
each call `input upper bound × the uncached rate + maximum output × the output rate + every supported additional charge`
with reasoning inside the output limit and every retry counted, bounds retrieval before it enters context, denies tools
rather than asking, and can run the cheapest capable model per task. It persists the requested model, the effective
identity when the provider reports one, the usage and a versioned pricing basis — calculated token charges are a
calculation, not an invoice. Its first tool surface: bounded file reads inside the role's folder, memory search, dataset
metadata, a `trade` tool that calls the existing gateway ops under an app-assigned identity (Research gets no order
permission), a typed report; no shell, no arbitrary HTTP, no package installs, no model-selected executable. The vendor
CLI remains for the two directors and the owner's chat, under the same task ledger, a bounded invocation count and
elapsed time, and a separate billing class; an owner who wants one hard ceiling over everything runs the directors
through the harness too. Until the harness lands, the CLI interim rule is: a turn over its budget fails publication, its
outputs are quarantined for diagnosis, its usage is charged in full, a retry draws on a separate remaining allowance, and
usage the app never received cannot pass the gate; repeated overruns split the task or change runtime, they do not
repeat "read less". Keys live in the OS credential store, are never exposed to a worker, and are not retained beside an
unsandboxed CLI process until containment lands: containment protects the supervisor's binaries, the trusted
configuration and the evaluator's storage from every agent process, the seniors included.

**The runner and the referee.** A promoted strategy is an immutable version whose frozen semantics and event interface are
the same in backtest, paper and live — the observations and the execution adapters differ, so fills and results need
not match — receiving only observations available at simulated time t and returning bounded intents. The runner bounds
program size, parse and validation work, lookback, state, per-event computation and output; an interpreter fault or a
timeout is a defined outcome (no new exposure, the app's protection policy) while app protection continues. The referee
protects a protocol, not a truth: holdout data the research process cannot reach; registered submissions charged against
a campaign-wide trial budget that survives team replacement, with campaign renewal authorised by code so no new campaign
resets holdout access; lineage by hash; metrics computed by the app from its own trace; promotion binding every version;
fixture runs establishing plumbing only; the scoring policy fixed before a campaign; final evaluation scarce because
every verdict leaks; and because public history may already be known or hard-coded into a submission, forward evidence
collected after the strategy's freeze is required before capital. Astra's round-2 sandbox was a bundled Wasmtime helper
running capability-free Wasm modules (no WASI, no host calls, fuel and deadlines); round 3 decided **language first**:
one app-interpreted strategy language, Wasm deferred until a demonstrated strategy family needs it. The language exposes
no ambient capability; that still needs a correct interpreter, bounded resource use and causal host inputs, and a later
Wasm adapter obeys the same causal interface and evaluation limits. Cheap-model reliability at writing rule sets is a
hypothesis to measure, not an established advantage.

**The strategy language, v1 (Astra's specification, round 3).** Programs declare typed constants, indicator expressions
and ordered rules; no general statements, no user functions. One spot instrument, long/flat, one position, no pyramiding;
closed 1-minute OHLCV bars in UTC with instrument increments and quality flags; only bars whose close has arrived,
bounded lookbacks, explicit warm-up, missing data reported and never filled. Indicators: SMA, EMA, ATR, RSI, rolling
high/low, a session opening-range accumulator — periods, initialisation and numeric semantics versioned. Account state:
available strategy capital, equity, position, average fill price, pending-order state, bars since entry. Expressions:
arithmetic, comparison, Boolean, crossing, bounded history; invalid periods and undefined sizing rejected at parse.
Entry and exit emit bounded market intents; exit precedes entry; no same-event reversal, no duplicate entry while one is
pending. Sizing: fixed quantity, fraction of capital, or equity-risk fraction over stop distance, rounded down to the
increment, then the gateway's limits. Stops: fixed price or percentage or entry-time ATR distance, optional target,
maximum holding bars; protection is app-owned between evaluations. Time filters with a versioned timezone and calendar,
weekdays, entry windows, opening-range interval, scheduled session exit, DST and missing sessions defined. A signal from a
completed bar executes only afterwards; a backtest declares fees, slippage, gaps and conservative stop-versus-target
ordering. Explicit node, rule, lookback, state and per-event operation limits. Never: shell, files, network, imports,
clocks, randomness, recursion, unbounded loops, model calls, training, multi-instrument, shorting, leverage. Bounded
state and processed-event identity persisted so a restart cannot enter twice on one accepted intent. SHA-256 over the
canonical typed program, parameters and semantic-version manifest, source retained; promotion separately binds the
interpreter build, dataset, execution model and evaluation policy. Day one must express: a moving-average crossover with
fixed sizing and stop; an opening-range breakout with ATR risk sizing and a time stop; an RSI mean reversion with a
profit exit and a maximum holding time.

**Data.** Historical experiments need history; hypothesis specification, capability assessment and experimental design
can precede it. Binance's public archives first (one liquid, region-eligible spot pair, closed 1-minute bars, a
twelve-month coverage TARGET with the actual coverage and gaps persisted and reported, archive checksums, source URLs,
download time and normalised hashes recorded; timestamps changed to microseconds in January 2025); Revolut X public
candles (five minutes, a ninety-day target, actual depth recorded; a candle WITHOUT volume is midpoint-derived and is
flagged as such, never as trade evidence); futures through `U-bars` on ATAS with provider identity, and Databento as the
reproducible fallback for depth (usage-priced, quoted before download). Bars support explicitly limited fill
simulations; they establish no actual fill, queue position or intrabar ordering, and Binance results are hypothesis
evidence for Revolut X, not its execution evidence.

**The owner's report, every day, from the app, even when no agent ran.** Identity and snapshot; mission state (working,
healthy idle, blocked, paused) with its coded reason and the next eligible wake; trading readiness; capital and
performance with valuation time and the remaining loss allowance; execution health including UNKNOWN operations by age;
AI spending by role, runtime and model with reservations and unresolved commitments, API charges apart from subscription
and list-equivalent figures; other operating costs, with every missing trading or operating cost named, so an incomplete
net figure can never read as a complete profitability claim; research evidence with app metrics apart from agent
claims; decisions and changes; recovery and next work, including a review that is pending because it was not funded.
The Operations Director adds a paid note of at most 20 lines only when an app predicate changed: what changed, why it
matters, the decision within existing authority, the alternative rejected, the next task with owner, budget and
deadline, the evidence, the uncertainty left. No material change: the app says so and pays nobody.

## The unit order

The money path keeps its safety dependencies and its live-release gates: `U-loss` (in flight) → the UNKNOWN close fixed or
disabled before any live order and before `U-flatten` → containment, venue capabilities, the applicable prop rules and
paper evidence before unattended real money. Round 2 did not require weeks of paper to finish before the council
increments start; the two lines run side by side, and nothing here loosens a live gate. The council substrate, in the
order settled in round 3, each a 40-line brief a fresh builder builds in one pass, each with the property its red-first
test proves:

- `U-model` — the model named and passed, the requested model, effective identity when reported, usage and versioned
  pricing basis persisted per attempt; one bounded fresh session per task; the observable components of a turn's context
  measured from the event stream with the remainder labelled unattributed (`TurnMeter.cs` sees aggregate usage only); the
  attempt-and-spending record written before launch, with admission against unresolved usage. **Proves:** every launched
  attempt has a durable identity and spending commitment, and killing it before its usage arrives, then restarting across
  midnight, cannot make that allowance available again.
- `U-wakes` — persisted, deduplicated events with identity replace the immediate re-turn; the idle language of the mission
  fixed (justified idleness is healthy, not a failure to look hard enough); the scanner's quiescence barrier. **Proves:**
  with no eligible unconsumed event, ticks, a restart and a replay of completed events launch zero paid processes.
- `U-data-binance` — the first real dataset with provenance and quality checks. **Proves:** an accepted normalised dataset
  is reproducible from its recorded raw hashes, with timestamp units, gaps, duplicates and incomplete bars classified
  correctly rather than silently misrepresented.
- `U-council-thin` — two role workspaces run SERIALLY by one app instance (the previous process tree proven terminated,
  accepted publication app-only, the owner's chat serialised with them, so leases can wait); the report-to-agenda relay
  as an immutable report publication followed by one transaction committing its reference, the source event's
  consumption and one uniquely keyed Operations task; per-role model and budget; the factual daily report. **Proves:** a
  crash at each handoff boundary recovers to exactly one committed report-to-Operations task, neither lost nor doubled.
- then `U-turn-commit` (staged output, immutable publication, committed transitions, fenced replacement, generalised
  from the thin slice) → `U-budget-reserve` (the generalised, concurrent reservation machinery; the durable unresolved
  spending itself is `U-model`'s) → `U-containment` → `U-api-worker` (the harness, one provider, one worker task) →
  `U-runner` (the language runner) → `U-referee` (its referee; Wasm deferred) → `U-council-concurrent` → the remaining
  venue and data units → `U-allocator` (evolution).

## The debate's record, from the answer files only

Round 1: the manager proposed one Chair on the smart model, three memory tiers, an event scheduler, files as the only
handoff. Astra: two senior roles at consequential boundaries; a fourth tier — the app's execution record — and a real
durable-execution protocol (tasks, attempts, leases, staged publication, commitment before launch); the Trader
supervising an app-run executor; "un-foolable" unattainable, protocol protection achievable; execution identity across
replacement as the missing boundary; cached input is not the enemy, unmeasured context is.
Round 2: the manager conceded all of it with the boundaries fixed in code; Astra conceded wakes-first and a serial
council before the substrate, under conditions, keeping two crash cases (a handoff repeated, spending forgotten) that
need the minimal record even in serial execution; the harness verdict (app-owned workers, CLI seniors optional); a Wasm
sandbox as its first proposal; Binance data first; the report; the ten rules.
Round 3: Astra's 32 corrections to the first draft of this file, all applied above; language first for strategies, Wasm
deferred; the four red-first properties; and no blocker to adopting the corrected doctrine as the build plan.
