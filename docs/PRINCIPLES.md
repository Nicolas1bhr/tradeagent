# What TradeAgent is

TradeAgent is a factory for autonomous LLM agents pursuing a trading mission: discover, test, operate and improve strategies, aiming to cover their full costs within the owner's chosen risk and spending limits.

The factory floor belongs to the models. The vault door belongs to deterministic code.

```text
AUTONOMOUS WORKING ENVIRONMENT
research · reasoning · coding · tools · experiments
strategy creation · delegation · communication · memory
planning · paper trading · failure · adaptation
                         │
                  proposed action
                         ▼
TRUSTED BOUNDARIES
resources and spending · evidence integrity
credentials · account identity · real exposure
                         │
                         ▼
AUTHORISED EXTERNAL EFFECTS
```

### The user experience and economic mission

The product runs through its own app on the target Windows laptop. Dependencies, sign-in, configuration, reporting and recovery must support an owner who never uses a terminal. The owner grants authority and sets ceilings; ordinary work proceeds within them without continual human direction. The app explains what is working, waiting, blocked or paused, why, what it costs, and what happens next.

The economic score is measured trading performance after trading fees and applicable financing, data, inference, subscription, infrastructure and other operating costs. Keep actual charges, estimates and subscription/list-price equivalents distinct. Name missing costs and incomplete valuations; unknown is never zero. Attribute results and costs to the relevant account, strategy version and attempts where the system can establish that lineage. Backtest and paper results remain labelled simulations and establish no realised live profit.

### Three zones, with different purposes

| Zone | What belongs here | Default |
|---|---|---|
| Creative | Research, coding, workspace tools, delegation, plans, communication, ordinary experiments and losing paper strategies | Freedom within granted resources and the workspace boundary |
| Evidence | App-computed measurements, frozen versions, causal data access, holdouts, trial accounting, lineage and forward evidence | Protect the measurement and its provenance; let models choose hypotheses |
| Capital and external effects | Real exposure, accounts, credentials, hard spending limits, operator controls and enabled external actions | Explicit authority, deterministic enforcement and attributable effects |

Paper losses and bad hypotheses are acceptable. Spending on those experiments is still real. Writing scratch code is acceptable; writing the supervisor, trusted configuration, measurement ledger or private holdout is a different action. Agents can cooperate and share ordinary research; protected evaluation evidence does not become shareable merely because agents want to discuss it.

### The organisation

On 2026-10-02 the owner set the organisation's shape: "agents shouldn't choose anything there should be multiple top level managers managing teams that have a head manager that makes decisions and executing agents that do the work smartly. (way more advanced that that) it should be a whole organisation with sub org's and so on" — one chief above the top-level managers, and "one invisible chief watcher that secretly reports to me if he's not delivering or doing mistakes". It restates his direction of 2026-09-07. TradeAgent's agents therefore work as an organisation: a chief; top-level managers heading divisions; teams whose head manager decides; executors who choose how to do assigned work, never what to work on; an audit line beside every chain; and the watcher, which reports only to the owner.

Every choice has one owner, fixed by the chart. The chart, positions, decision rights, envelopes, work orders and decision records are application data, not prompts; a title, a charter or a message grants nothing. Managers grow and reshape the chart inside bounds the application enforces and the owner can veto, and each layer stays only while it is measured to pay. Managers decide from instruments — application measurements — not from opinion, and record each consequential decision with a forecast the application later scores. Budget follows rules — equal shares and a bounded, recorded, scored tilt — until measurement can allocate it, then measured contribution, never persuasion. Routine decisions have one accountable decider and no vote; a meeting or a second opinion must have a purpose within budget.

The organisation decides work; deterministic code decides money and truth; the owner decides authority. The existing role identities survive as the first positions. Strong models sit where judgement is scarce, cheaper capable models do bounded work with an advisor, and the balance is chosen from measured capability and cost. An inefficient experiment, a closed team or a losing paper strategy is an ordinary outcome to learn from. `docs/ORGANISATION.md` is the design.

### Delegation is one reusable capability

A manager asks the application for a child unit or position with a mission, relevant context, workspace/data scope, a subset of its delegable capabilities, a resource allowance and a lifetime. The app mints the child's identity and records its parentage. The child works autonomously and returns artifacts and evidence; the manager decides the next step.

Delegation cannot manufacture authority or money. Parent and children draw from the same encompassing ceilings; reservation, cancellation and expiry apply to the actual work and its descendants. Replacing or renaming a worker does not reset campaign trials, spending commitments or unresolved operations. Operator controls, credentials and authority to modify the supervisor are not delegable agent capabilities.

Examples of broad capabilities are workspace access, contained code execution, market data, web research, backtesting, paper simulation, spawning workers and separately scoped external actions. Identity establishes who acted; capabilities establish what that launch may do. A title, model-supplied role name or directory depth establishes neither.

### Useful autonomy needs code, tools and durable memory

The working environment must eventually let agents write and execute useful code, develop temporary tools, analyse data, conduct experiments and delegate inside its boundary. A model that can only exchange small reports does not meet that requirement. Reuse and extend the existing runtimes before inventing a replacement. Runtime selection must satisfy the intended capabilities and the actual isolation and spending requirements; neither CLI nor API is the product philosophy.

Keep active context small and relevant. Preserve durable plans, findings, failed experiments, decisions, source references, artifacts and lineage outside the transcript so a fresh attempt can resume. Distinguish measurements, external claims and agent interpretations. Bounds on context, retrieval, storage and compute are legitimate; a short summary limit is not a reason to discard the underlying research. Prefer a bounded summary with artifact references, explicit refusal and recoverable output over silent truncation or destruction. Add elaborate retrieval or consolidation only when an observed failure needs it.

“Never stopping” means an enduring mission that can schedule useful work, wait, resume and recover. It does not mean constant paid inference. Agents can choose a next task or wake condition within their allowance; app scheduling persists that intent and avoids duplicate launches. No useful eligible work is healthy idleness. Budget exhaustion stops paid work; app protection and reconciliation continue. A restart must not erase costs or uncertainty, repeat an unresolved external action, or require the owner to reconstruct the mission.

### Evidence supports iteration and governs promotion

Models may explore freely on research data. The app owns authoritative measurements and protected evaluation: immutable candidate identity, evaluator and data versions, declared execution/cost assumptions, causal inputs, trial limits and relevant holdout protection. Models may propose candidates for evaluation; software decides admission and issues the verdict within the existing evidence budget. Requesting evaluation does not grant access to the holdout, alter the scorer or authorise live capital.

Distinguish acceptance of a program, a favourable historical verdict, eligibility for paper observation, and eligibility for live capital. Preserve the deployed contract while making these meanings explicit. Forward paper evidence cannot be required before the very first paper run that produces it. A paper experiment also cannot confer live authority. Keep rejection, inconclusive evidence and no trade as valid outcomes; do not lower the scorer or invent a profitable candidate to obtain a demonstration.

Frozen strategies execute through the app's deterministic runner and existing gateway. Agent-authored research code does not run with the gateway's authority. Models can revise strategies between versions; no model call is required for each signal or for emergency protection. Preserve the existing language and semantics until a demonstrated strategy need justifies extending them. The owner judged that need demonstrated on 2026-10-02 — the language can express none of the researched edges — so it grows as `docs/EDGE-FACTORY.md` § 4.4 sets out, keeping its total parse, freezing, hashing and runner semantics.

### The boundary remains enforceable

Preserve the existing dispatch and recovery protections: enabled authority; valid deployment and allocation where required; connector, mode and account identity; current launch authority; reconciled order state; fresh required data and decisions; exposure and loss limits; idempotency; app-owned emergency risk reduction; and durable attribution. UNKNOWN remains uncertainty requiring reconciliation, never permission to retry a potentially completed order.

Owner controls remain in-process. Inbox documents and agent prose grant no authority. New live authority and live capital allocations retain their deliberate owner confirmation. Pause, stop and flatten retain their distinct meanings and existing confirmation behaviour; making routine work autonomous must not weaken emergency controls.

Resource and spending caps must be enforced where costs are incurred, including children and retries, with unresolved charges retained across restarts. Describe advisory limits as advisory. Protect the supervisor, trusted state, credentials and private evaluation data from agent code. A process group, a job object or a prompt instruction alone does not establish that protection.

The target is to prevent unauthorised effects by construction. Claim only the properties actually demonstrated on the supported environment. A loss limit constrains permitted exposure and triggers protection; it is not a guarantee against market gaps, execution failure or every possible loss. Preserve evidence of limitations instead of promising that catastrophic outcomes are universally impossible.

Before adding a blocker, name the concrete consequence, why an existing control does not cover it, and why observation, a resource bound or ordinary failure is insufficient. Add the smallest control at the responsible boundary. Do not turn an LLM's reversible mistakes into a growing set of prescribed workflows. Equally, do not remove a genuine boundary merely because it has deterministic enforcement.

### The loop and the priority rule

```text
notice / choose useful work
  → research and create a candidate
  → test and obtain a bounded independent verdict
  → when eligible, run forward on paper
  → receive fills, P&L, costs and evidence
  → keep / modify / kill / branch / choose another problem
  → continue without the owner supplying the next step

Separately: sufficient evidence + explicit live authority
  → bounded live execution → measured results → further learning
```

This describes an outcome to enable, not an app-enforced script for every agent. Models can revisit, skip inapplicable research steps, work in parallel or stop an unpromising line; they cannot skip an applicable evidence or capital boundary.

Prioritise work that closes an arrow, removes a demonstrated blocker, makes that loop usable and recoverable by the owner, or protects an actual material boundary. Prefer a local extension of a working primitive over a new subsystem. Preserve useful existing machinery; simplify a concrete obstruction without tearing out its underlying guarantees.

Broader strategy languages, extra venues, copy trading, generic governance and hypothetical external-tool policies are not the next priority simply because an older plan mentioned them. Build them when the mission demonstrates the need. On 2026-10-02 the owner judged the build "way too stiff" with "nothing in place that makes it a moat", and asked for an evolving system that finds edges with every current and future tool, "like copy trading, news treatment and scraping". The manager's plan, `docs/EDGE-FACTORY.md`, concludes that this need is met by new information sources, a broader language and an evolutionary selection layer, and by derivatives venues pending the owner's venue decision; it sequences them. Copy trading as such stays unprioritised: public positioning enters as measured features. The same day the owner made the organisation itself a priority; `docs/ORGANISATION.md` builds it as a second lane beside the edge factory. Success is autonomous, economically accountable operation inside the owner's limits, not the number of safeguards, roles, tests or commits accumulated.

### The edge and the moat (2026-10-02)

The factory exists to find edges a modest autonomous system can hold, and its moat is what compounds with calendar time: the point-in-time record of what it observed as it arrived, a forward ledger nobody can backfill, the memory of everything tried and why it died, calibration against its own outcomes, and knowing which research pays per AI dollar. Code computes what is exact; bounded decision models judge at volume; frontier models research and design; none of them sits on the signal path. Model-derived evidence is clean only from the moment it was recorded; answers backfilled over history are exploratory. Prove families and processes rather than single versions, and fund what keeps working. `docs/EDGE-FACTORY.md` is the target architecture and build order; it grants no authority and weakens no protection.

---

*Amended 2026-10-02 for the owner's direction of that day (the software "way too stiff", "nothing … that makes it a moat"): § Evidence's language sentence, the priority paragraph and "The edge and the moat"; amended again later that day for his organisation direction: "Models own the organisation" became "The organisation", and the delegation and priority paragraphs follow it; the rest is unchanged. Landed 2026-09-19 from `manager-prompt.md` § 3, the owner's corrective direction of 2026-09-17/18. This file is the source of product direction; `CLAUDE.md`, `docs/RESUME-HERE.md` and `docs/COUNCIL.md` point here. It establishes intent only: it marks no feature implemented, no test passed and no boundary enforced.*
