# TradeAgent — final direction for the build fleet

Prepared 2026-09-18 from the owner's original corrective draft, Fable's 2026-09-17 revision, and a source inspection of `main` at `bd7d9c2d6b9cfac74161abc4fc951d80a11b4ab8`. This is the handoff to execute; preparing it did not implement or verify the product changes below. Recheck implementation claims against the checkout you actually receive.

## 1. Your mandate and the destination

Build TradeAgent into an autonomous, evolving research and trading organisation for a nontechnical owner. Capable models choose useful work, research, write and execute code, create tools and strategies, delegate, test, learn from results, and repeat. The economic mission is to make at least enough money, after trading and operating costs, to pay for itself. That is an objective to measure honestly, never a promised return or a reason to relax controls.

The owner installs and configures it through the app, provides the required accounts and credentials through supported sign-in, sets budgets and risk authority, and deliberately enables the mission. Routine research, paper deployment, evaluation and iteration then continue without Nicolas supplying the next instruction, choosing every experiment, repairing files, or approving each internal handoff. Real-money activation and live capital grants remain deliberate owner actions. Once an eligible autonomous mode is authorised, execution within that authority must not require approval of every trade; preserve confirmation mode where the owner chooses it or the connector requires it. The owner can inspect, guide, pause and stop it through the app and never needs a terminal.

The architectural direction is **a broad autonomous working environment inside a small, enforceable boundary**. Models organise the work. Software enforces actual resource limits, evidence integrity and external authority. The existing implementation is valuable substrate; this is an incremental correction of direction, not a replacement project.

There are two fleets here: the **build fleet** receiving this prompt, and the **agent organisation inside the product**. `docs/HOW-WE-BUILD.md` governs the former. Its builder roles, worktrees, short briefs and landing procedure are not automatically requirements to hard-code into the latter.

## 2. Standing and the first landing

Read this directive first, then `CLAUDE.md`, `docs/RESUME-HERE.md`, `docs/HOW-WE-BUILD.md`, `docs/COUNCIL.md`, and the relevant current evidence in `BUILD-STATUS.md`. Establish the actual branch, changes and work in flight before acting; dated resume text is not proof of current state.

This directive supersedes conflicting product philosophy, organisational prescriptions and future sequencing in `docs/COUNCIL.md` and old handoffs. It preserves the no-terminal promise, the honesty rule, and the existing money, credential, accounting, evidence and recovery protections. Do not use the phrase “principles outrank other documents” as permission to bypass a protection. Where a policy needs a local change, name the protected property and show how the replacement preserves it.

**First land the direction in the repo, as a documentation-only change:**

1. Save the text between the `PRINCIPLES` markers below as `docs/PRINCIPLES.md`. It is the durable product definition.
2. Point `CLAUDE.md`'s “Start here” and `docs/RESUME-HERE.md`'s “Do this first” to it before the older council doctrine. Make the preserved protections and the build-process distinction explicit.
3. Add a precedence note atop `docs/COUNCIL.md`: its existing boundary protections remain; its fixed organisation, mandatory deliberation patterns and unbuilt machinery are historical design proposals to reassess against the principles, not an automatic backlog. Memory, accountability and delegation remain product needs; their earlier prescribed implementations are not commitments.
4. Correct the active resume block where completed work still appears pending, and replace its old next-work choices with this inspection and the resulting loop-closing queue. Preserve historical evidence. Amend conflicting aspirational instructions locally when needed; avoid a broad documentation rewrite.

Retain this file as the fleet handoff. Make `docs/PRINCIPLES.md` the source of future product direction so two copies do not evolve independently. Follow the repo's scan and landing procedure. Documentation establishes intent; it must not mark a feature implemented, a test passed, or a boundary enforced.

## 3. Canonical principles

<!-- PRINCIPLES BEGIN -->
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

### Models own the organisation

Agents choose what to investigate, how to investigate it, when to seek help, what tools to build, and when to keep, modify, abandon or branch their work. Several workers may attack the same problem differently. An inefficient experiment, an unnecessary worker within budget, a changed plan or a losing paper strategy is an ordinary outcome to observe and learn from.

Supply useful primitives rather than prescribing every sequence of thought and work. A task record may preserve identity, completion, cost and recovery without defining a corporate workflow. A typed tool interface may protect an actual boundary without requiring an organisational approval chain.

Keep the council and role identities already in use for compatibility. Do not grow a fixed list of roles to represent every helper the models invent, and do not make the current number of roles a permanent product limit. Powerful models can coordinate and challenge important decisions; cheaper capable models can do bounded work. Choose that balance from measured capability and cost. A meeting or a second opinion must have a purpose within budget; every ordinary experiment need not purchase a fixed ceremony.

### Delegation is one reusable capability

A parent asks the runtime for a child with a mission, relevant context, workspace/data scope, a subset of its delegable capabilities, a resource allowance and a lifetime. The app mints the child's identity and records its parentage. The child works autonomously and returns artifacts and evidence; the parent chooses the next step.

Delegation cannot manufacture authority or money. Parent and children draw from the same encompassing ceilings; reservation, cancellation and expiry apply to the actual work and its descendants. Replacing or renaming a worker does not reset campaign trials, spending commitments or unresolved operations. Operator controls, credentials and authority to modify the supervisor are not delegable agent capabilities.

Examples of broad capabilities are workspace access, contained code execution, market data, web research, backtesting, paper simulation, spawning workers and separately scoped external actions. Identity establishes who acted; capabilities establish what that launch may do. A title, model-supplied role name or directory depth establishes neither.

### Useful autonomy needs code, tools and durable memory

The working environment must eventually let agents write and execute useful code, develop temporary tools, analyse data, conduct experiments and delegate inside its boundary. A model that can only exchange small reports does not meet that requirement. Reuse and extend the existing runtimes before inventing a replacement. Runtime selection must satisfy the intended capabilities and the actual isolation and spending requirements; neither CLI nor API is the product philosophy.

Keep active context small and relevant. Preserve durable plans, findings, failed experiments, decisions, source references, artifacts and lineage outside the transcript so a fresh attempt can resume. Distinguish measurements, external claims and agent interpretations. Bounds on context, retrieval, storage and compute are legitimate; a short summary limit is not a reason to discard the underlying research. Prefer a bounded summary with artifact references, explicit refusal and recoverable output over silent truncation or destruction. Add elaborate retrieval or consolidation only when an observed failure needs it.

“Never stopping” means an enduring mission that can schedule useful work, wait, resume and recover. It does not mean constant paid inference. Agents can choose a next task or wake condition within their allowance; app scheduling persists that intent and avoids duplicate launches. No useful eligible work is healthy idleness. Budget exhaustion stops paid work; app protection and reconciliation continue. A restart must not erase costs or uncertainty, repeat an unresolved external action, or require the owner to reconstruct the mission.

### Evidence supports iteration and governs promotion

Models may explore freely on research data. The app owns authoritative measurements and protected evaluation: immutable candidate identity, evaluator and data versions, declared execution/cost assumptions, causal inputs, trial limits and relevant holdout protection. Models may propose candidates for evaluation; software decides admission and issues the verdict within the existing evidence budget. Requesting evaluation does not grant access to the holdout, alter the scorer or authorise live capital.

Distinguish acceptance of a program, a favourable historical verdict, eligibility for paper observation, and eligibility for live capital. Preserve the deployed contract while making these meanings explicit. Forward paper evidence cannot be required before the very first paper run that produces it. A paper experiment also cannot confer live authority. Keep rejection, inconclusive evidence and no trade as valid outcomes; do not lower the scorer or invent a profitable candidate to obtain a demonstration.

Frozen strategies execute through the app's deterministic runner and existing gateway. Agent-authored research code does not run with the gateway's authority. Models can revise strategies between versions; no model call is required for each signal or for emergency protection. Preserve the existing language and semantics until a demonstrated strategy need justifies extending them.

### The boundary remains enforceable

Preserve the existing dispatch and recovery protections: enabled authority; valid deployment and allocation where required; connector, mode and account identity; current launch authority; reconciled order state; fresh required data and decisions; exposure and loss limits; idempotency; app-owned emergency risk reduction; and durable attribution. UNKNOWN remains uncertainty requiring reconciliation, never permission to retry a potentially completed order.

Owner controls remain in-process. Inbox documents and agent prose grant no authority. New live authority and live capital allocations retain their deliberate owner confirmation. Pause, stop and flatten retain their distinct meanings and existing confirmation behaviour; making routine work autonomous must not weaken emergency controls.

Resource and spending caps must be enforced where costs are incurred, including children and retries, with unresolved charges retained across restarts. Describe advisory limits as advisory. Protect the supervisor, trusted state, credentials and private evaluation data from agent code. A process group, a job object or a prompt instruction alone does not establish that protection.

The target is to prevent unauthorised effects by construction. Claim only the properties actually demonstrated on the supported environment. A loss limit constrains permitted exposure and triggers protection; it is not a guarantee against market gaps, execution failure or every possible loss. Preserve evidence of limitations instead of promising catastrophic outcomes are universally impossible.

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

Broader strategy languages, extra venues, copy trading, fixed worker hierarchies, generic governance and hypothetical external-tool policies are not the next priority simply because an older plan mentioned them. Build them when the mission demonstrates the need. Success is autonomous, economically accountable operation inside the owner's limits, not the number of safeguards, roles, tests or commits accumulated.
<!-- PRINCIPLES END -->

## 4. Inspect the reachable loop before choosing units

After the documentation landing, dispatch one fresh read-only survey leg. It may write its inspection report; it must not modify product code. Have it read the principles first. Use `docs/INSPECTION-<date>.md`, five compact tables, target at most 100 lines. Cite the inspected SHA and file:line evidence, with separate labels for source observations, executed checks and historical claims. Do not call source inspection runtime verification.

| Table | What it must settle |
|---|---|
| Fits; preserve | Locate the working gateway, grants, recovery, ledgers, budgets, strategy evaluator, referee, allocation and reporting primitives and the properties they protect. |
| Concrete obstructions | Identify a specific action an agent needs, the restriction it encounters, and the smallest correction preserving the real boundary. A missing tool can be a blocker even when no output is discarded. A size limit can be reasonable even when it rejects output. |
| Missing connections | Trace the path actually available from a model turn through data, program creation, backtest, candidate submission, verdict, paper allocation, deployment, fill feedback and another autonomous decision. A public C# method with no production caller is not a connected capability. Include delegation and useful code execution. |
| Hard boundaries and limitations | Identify what is enforced and where, what is merely stated, and the isolation and account conditions needed for the next demonstration. Preserve the difference between a protected evaluation and a mechanics-only run. |
| Smallest next slice | Name the earliest broken dependency, the subsequent forward-paper connection, the acceptance evidence, and any genuine owner-only prerequisite. Order a short queue by those dependencies. |

Starting evidence to recheck, not a prewritten verdict:

- `GrantedWorkerTools.cs` offers six tool names, no code-execution or child-spawn tool, and writes only to `out/` and `trading/`. Trace what a real model can accomplish through the offered schemas and handlers, not just what internal methods exist. Contrast that with the vendor CLI's capabilities and actual isolation.
- `TradingGateway.cs` exposes `Referee` in process and explicitly documents no agent-facing verdict request. The source inspection for this handoff found no production invocation of `Referee.Verdict`. Campaign setup and holdout selection also need tracing. This may break the loop before the missing paper runner. A bounded candidate-submission path is compatible with an app-owned referee; exposing protected data or arbitrary evaluation-budget renewal is not.
- `StrategyEvaluator` emits `StrategyIntent`; the latest build record reports no deployed paper/live runner converting it to an order. The interpreter and backtester already exist. Distinguish them from the missing forward execution host.
- `TradingGateway.Allocate` and the Capital card currently put allocations behind an owner press. Trace how that affects paper deployment; paper experimentation must not require Nicolas to grant capital to every candidate.
- `CouncilRelay.ReportLines`, `WorkspaceRevisions` restore behaviour and `BoundaryBaselines` declaration checks constrain publication. Establish whether each protects cost/evidence, prevents useful work, loses recoverable work, or only prescribes a format. Preserve useful limits and protected publication properties when changing a concrete obstruction.
- Trace `CouncilBoundaries` wakes, deadlines, assessments and dispositions to **all** their consumers, including subsequent agent decisions. No order-path consumer alone does not prove a review is useless. Conversely, paid review with no useful consequence is a candidate for local simplification, not a reason to add another dispatch veto.
- `Containment.Sandbox()` reports `NONE`. Same-user agent code can reach files a tool-level contract says it cannot modify. Do not describe those files as physically protected until isolation proves it.
- The built-in simulator's fixed quotes establish mechanics only. Historical records also leave real provider calls, current UI behaviour and an autonomous strategy loop unverified. Use historical evidence with its date and scope; do not infer that something has never happened solely from an old handoff.

## 5. Execute the smallest connected slices

Use the survey to choose the first unit. **The default destination is the forward-paper loop; an upstream gap takes precedence over wiring a runner to a candidate that no agent can get evaluated.** Reuse the existing evaluator, referee, ledgers, gateway and scheduler.

Settle these design points in the relevant short briefs, not in a new architecture programme:

- **Evaluation access:** a candidate submission or an app-triggered evaluation within existing policy, with app-owned admission, frozen identity, precharged trial limits and bounded feedback. Automatic setup within an already granted research envelope must not become permission to reset holdouts or manufacture a fresh allowance.
- **Paper authority:** grant bounded paper experimentation as part of the owner's mission setup, then permit eligible paper allocations and deployment by app policy or delegated capability without a new owner press per version. Enforce a genuinely non-live execution scope; a model-supplied “paper” label is insufficient. Scope state, budgets and records so switching connector, mode or account cannot carry a paper grant into live authority. Preserve the live allocation path and limits.
- **Forward execution:** use a compatible instrument, market-data source and execution model. Route the frozen version's intents through the existing gateway with explicit app-owned execution identity and lineage back to the deployment and originating research. That identity must not bypass checks. Persist progress and unresolved operations so restart, replay, replacement or cancellation cannot silently duplicate exposure. Ensure required exits and protection run as well as entries.
- **Actual market observation:** prefer the smallest available compatible source of real, advancing prices and simulated execution, including an app paper adapter over live data if that is the smallest fit. Do not assume ATAS's available instrument fits the current strategy semantics, invent a testnet capability, or substitute a small live order for paper. Fixed quotes and historical replay remain useful preliminary mechanics checks with narrower claims.
- **Useful working environment:** extend the existing runtime where the trace demonstrates missing tools or argument support. Add the smallest bounded child and contained code-execution capabilities when needed for the factory's work. The first single-agent paper slice can precede delegation; it does not discharge the end-product requirement for self-directed delegation and tool building.

Containment is dependency-based, not unconditionally “later than the loop.” A paper-only development demonstration can proceed in a disposable, suitably isolated environment with no reachable live authority or unrelated secrets, with its evidence limitations recorded. Protect private evaluation before claiming uncontaminated evidence, and protect real authority before exposing it to agent code. If those conditions do not hold, the smallest necessary isolation work precedes that run. Solve the boundary while retaining useful code execution; neither removing the factory's capabilities nor trusting an unsandboxed process meets the destination.

Do not sweepingly remove existing council machinery, refactor the gateway into a new “kernel,” or add duplicate policy checks. A restriction with a demonstrated cost may be changed locally even if it is working as originally designed. State what is preserved and why the replacement is enough.

## 6. Proof of progress and completion

Treat these as distinct claims. Completing one does not imply the next.

| Milestone | Evidence required |
|---|---|
| Connected mechanics | Repeatable automated checks show the actual production path and its refusals, including restart/replay and paper/live separation where touched. Fixtures are labelled fixtures. |
| Autonomous paper loop | In the running app, a real model authors a candidate, invokes the app's backtest, submits it through the real evaluation path, and receives the verdict. A genuinely eligible version proceeds to forward paper execution on advancing market data; fills and costs reach the app ledger/report; a later agent turn reads them, records a reasoned keep/modify/kill/branch decision and carries out the resulting work or schedules a justified wake. No developer seeds promotion/allocation rows or manually hands artifacts between steps. |
| Sustained autonomous factory | A bounded observation window and budget are stated before the run. More than one decision cycle is observed, along with resume after interruption, useful waiting, budget exhaustion behaviour and result delivery. A model chooses and uses a bounded helper and a tool or code artifact it creates. Another fresh attempt can recover the mission from durable state. Record actual duration, cycles, interventions, spending and remaining gaps. |
| Live readiness and economic performance | Separately establish applicable connector/account capabilities, isolation, protected evidence, forward observations, execution/protection behaviour and explicit owner live authority. Observe eligible operation on the target environment and report net performance with its cost coverage and limitations. Neither a paper demo nor this development directive authorises real orders, and neither proves that the system pays for itself. |

The immediate phase target is **the autonomous paper loop**, followed by sustained operation. A UI screenshot alone, a developer-authored strategy, an internal method exercised only by tests, or an agent saying it traded cannot satisfy it. Record the build, runtime/model, data source, version and account/mode identities, observed actions and outputs, and manual interventions. A rejected candidate that autonomously leads to another experiment is useful partial evidence. Do not force a passing verdict, trade, or favourable return to finish a milestone.

One-time supported setup and the deliberate grant to work unattended are allowed. Manual selection of each strategy, routine campaign maintenance within an already granted allowance, per-candidate paper allocation, file repair, database edits or a fresh prompt to make each cycle continue are gaps to record and close. Exhausting an authorised trial or spending allowance is a legitimate boundary, not a reason to reset it. Ask the owner only for actual missing authority, credentials, paid commitments or a product decision the directive cannot settle; keep independent work moving.

## 7. Build discipline and handoff

Use `docs/HOW-WE-BUILD.md` for unit delivery: fresh builders, at most two heavy legs, short briefs and reports, appropriate red-first proof on the money path, the landing gate, and milestone review. Read its fresh-fixer rule literally; a rate limit or killed process is not automatically a failed design. Follow the procedure within the current session's authorisation and tool permissions; do not expand it into repeated review rounds.

Every brief starts with the loop arrow it closes or the actual boundary it protects, then the observable result that will prove it. Run the real model and app at the first useful integration point within the configured allowance; do not postpone all runtime discovery until after another long sequence of disconnected landings. Use the Windows box at the appropriate milestone and under its existing access rules. External capability, platform-rule and provider assumptions must be verified from current official sources when a unit depends on them.

If taking the existing Astra consult before the paper line, give it the principles and concrete survey findings first, ask the bounded unresolved question, and use the final answer as advice. No consultant's preferred organisation overrides the owner's product direction.

At each landing and session close, record what was actually run and what remains **NOT VERIFIED** under `BUILD-STATUS.md`'s honesty rule. Distinguish the loop connection gained from the protections preserved. Update the resume block with the current principles, inspection, observed milestone, next small units and genuine owner-only dependencies. Preserve concurrent work and the repository's normal landing discipline.

Keep the fleet aimed at the destination: models doing useful autonomous work, learning from measured outcomes, within a boundary the software can actually enforce.
