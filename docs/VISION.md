# The vision — TradeAgent as an AI organisation

**Vision, 2026-10-06.** The end state TradeAgent's agent organisation grows toward, and the law that decides how it grows. It answers the owner's request of
2026-10-05 — "take a leap and actually think of the best possible end state of the software where it genuinely becomes an AI organisation" — and records
the synthesis the owner agreed to on 2026-10-06 after three passes: an end-state brainstorm; an upgrade drawn from the AI Concierge foundation documents the
owner shared (its canon, foundation v0.1, the v0.2 institutional substrate and the executable contract program, outside this repository in
`~/Library/Mobile Documents/com~apple~CloudDocs/Personal Workspace/Side projects/Ai conciergerie/`); and an assessment that kept the upgrade's control-plane
primitives and rejected its structural expansion — a 40-seat floor, unlimited active depth, a council in place of the chief — as not yet earned. Everything
here is DESIGN unless a line says otherwise; nothing in this file marks a feature built, a test passed or a boundary enforced.

**Where it sits.** `docs/PRINCIPLES.md` stays the product definition and outranks this file. This file is the direction `docs/ORGANISATION.md` converges on:
ORGANISATION § 15's current order is the build sequence through M-org1 (re-sequenced on 2026-10-08 by R20, R21 and R22), and where the two describe the organisation's eventual shape differently, this file says
where it is going and ORGANISATION says what is built next. `docs/EDGE-FACTORY.md` stays the target for the information, evidence, selection and capital
planes the organisation works on. Every protection in `CLAUDE.md` and on the code stands — § 13 names each one this vision touches and how it is kept. It
grants no authority and outranks no protection.

**2026-10-08 — read through `docs/PRINCIPLES.md` "Autonomy is the default inside authority"** (the owner's decisions on an outside autonomy audit,
`docs/research/2026-10-08/R20-autonomy-audit.md`). Everything below organises authority, resources, accountability and evidence; none of it prescribes how an
agent reasons inside its mandate. Amended to match: §§ 0, 1 (a ninth test), 2, 5.1, 6.1, 6.3–6.6, 7.5, 9, 11, 12, 15; ORGANISATION § 15's order now puts
containment before the governance verbs.

## 0. The law

> **TradeAgent imposes no permanent architectural limit on how large the organisation may become. It also grants no organisational complexity for free.
> Every active seat, department and management layer exists because current work requires it, or because a bounded experiment is testing whether it
> improves measured outcomes.**

Shortly: **unbounded in what TradeAgent can represent; deliberately bounded in what it activates.** Complexity is something the organisation earns. This
vision is how the organisation is allowed to become large, not a decision that it should be.

- **Containment makes depth safe; only measurement makes it good.** Nested envelopes prove a manager cannot spend more than its parent granted; the authority
  lattice (§ 6.2) proves it cannot gain its parent's privileges; app-built packets keep an executor from inheriting nine generations of paraphrase. None of
  it removes what a layer costs when it works: decision and coordination latency, wake amplification, manager turns, extra failure surfaces, diffused
  responsibility, harder attribution, conflicting priorities, queue contention, more state transitions, more restructuring, more forecasts to calibrate, more
  watchers to supervise, staler memory, and decisions taken on summaries of measurements. A layer that never has to work raises the question why it exists.
- **There is no headcount target.** Size is an outcome of work. If a chief, one department head, two team heads and six executors beat a forty-seat
  organisation on admissible candidates, independent discoveries, false-discovery rate and dollars spent, the smaller organisation is the better one — and
  staying small is the organisation having learned not to employ bureaucracy, not a failure of this vision.
- **Nothing moves air.** The owner does not want to launch TradeAgent and find that it "runs fancily and moves air". Every active seat shows, each period, the
  measured work it produced and what that cost (§ 7.2); a seat or a layer with nothing to show is reviewed and collapsed.
- **Observe the loop before generalising it** (the factory plan's Law 10). Observed scale beats imagined scale: the organisation grows from what M-org1 and
  M-org2 measure, not from this file.
- **Structure governs authority, never thinking** (2026-10-08). A layer, department or council adds accountability, resources and coordination; none of it
  reaches into how an agent reasons inside its mandate. An organisation that grew by prescribing its agents' procedures would be a workflow engine, however
  large.

## 1. What "genuinely an organisation" means — eight tests

The end state passes all nine. Today's kernel holds much of the eighth — for app paths; on CLI seats it is advisory until containment (ORGANISATION § 14);
the others are what this vision is for.

1. **Replace every occupant.** Every model in every seat swapped overnight: output dips and recovers within days, because the knowledge lives in the
   institution — records, playbooks, precedents, beliefs — and not in transcripts.
2. **The owner goes silent for a month.** The organisation works inside its funded tranche, asks only within its attention budget, and if it misses its
   milestones the app shrinks it to a standing minimum. It never stalls and never overspends.
3. **A new model is released.** The organisation examines it, shadows it on real work and adopts it wherever it is better per AI dollar, with no owner
   engineering.
4. **A new information source appears.** The organisation proposes it, admits it through the ladder, opens a team or a department around it, and either earns
   from it or closes it with the post-mortem kept.
5. **It can describe itself:** its false-discovery rate, its research yield per AI dollar, which of its managers are calibrated, and where its ignorance
   matters.
6. **It improves its own process,** measurably, on a fixed exam battery (§ 6.4).
7. **Nothing moves air:** every active seat and layer shows measured work for its cost, and what cannot is collapsed.
8. **It cannot move money it was not given, change its own rules, or persuade its owner.**
9. **It finds what it was not told to look for** (2026-10-08). An executor notices a finding its assignment did not ask for, follows it inside its envelope
   with tools it builds, tells the unit that can use it, and the finding outlives the executor's context — the organisation's rules let discovery through
   (ORGANISATION § 4; the off-path exam, § 6.4).

## 2. The shape

```text
OWNER — the board: mission · ceilings · authority · live confirmations · veto
 │
 ├─► SECURITY & ASSURANCE COUNCIL — the independent third line; its own owner-set budget; reports to the owner
 │     ├─ security    what execution cells actually do; containment, quarantine
 │     ├─ assurance   audits the chief, the strategy council and every watcher; the adversarial brief at boundaries
 │     ├─ private     the owner's private line: the watcher of ORGANISATION § 9, invisible to the organisation
 │     └─ the accountability line (app-owned) appoints and funds every watcher (W) below
 │
 └─► CHIEF — the one accountable owner of organisation strategy inside the owner's mission
       ├─ STRATEGY COUNCIL — portfolio seats · Risk & Portfolio; sealed assessments at boundaries, never a vote
       ├─ DEPARTMENT ┄┄ W
       │    ├─ SUB-DEPARTMENT, only if earned (§ 3) ┄┄ W
       │    │    └─ …
       │    └─ TEAM (head) ── executors: own the path, never the mandate
       ├─ DEPARTMENT ┄┄ W
       └─ DEPARTMENT ┄┄ W          depth, departments and seats: representable without limit, active only when earned
═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
APP KERNEL — an organisation tree of any depth · the authority lattice · nested envelopes · the capability broker · mandates ·
decision records and forecast scoring · the invariant registry · watcher detectors · simulation boundaries · shadow evaluations ·
the referee · the capital allocator · risk · the gateway
the organisation decides WORK · code decides MONEY and TRUTH · the owner decides AUTHORITY
```

- **Three lines.** The first is production — chief, department heads, team heads, executors — which owns its work and its risks, with app-run acceptance
  checks at every handover. The second is one watcher per department, appointed and funded by the accountability line, never by the production chain. The
  third is the Security & Assurance Council, independent of the chief and reporting to the owner. It is the model banks and auditors use, and the evidence's
  rule: audit sits outside the chain it checks (R13 D8).
- **One chief.** "Who owns organisation strategy inside the owner's mission?" has one answer, one position: one accountable decider is among the research's
  strongest findings (R13 D4; teams pulled their best member down by up to 41%, H5). The strategy council around the chief produces independent
  assessments; it owns nothing (§ 4).
- **Departments are institutions, not prompts.** A department is data — charter, policy, routing, envelope, memory, playbooks, seats and its watcher —
  instantiated when work exists ("a department need not be a daemon", concierge foundation v0.2 § 18.1). It opens only around a real information source,
  capability, strategy family or control need (§ 3).
- **The accountability line is app-owned and outside the organisation's tree:** no organisation verb charters, closes, funds or re-seats a watcher, and the
  private line is never a row in any `org_*` table (ORGANISATION § 9).
- **Executors own the path inside their mandate, never the mandate** — methods, tools, hypotheses and the findings worth following are theirs; objective,
  scope, envelope and acceptance are their head's (ORGANISATION §§ 2, 4, as the owner read it on 2026-10-08). **The legacy pair are the first positions:** `operations` keeps the only order
  permission and moves only by the owner's presses; today's two directors' sealed assessments (`docs/COUNCIL.md`) are the strategy council's embryo.

## 3. Representable versus active

| Dimension | Representable — data model and code | Active — policy and runtime |
|---|---|---|
| Depth | Any. The chart is already a tree by `org_unit.parent_id` (rung 28); `OrgStore.Ancestors` and `Subtree` walk any line; unit kinds are validated text a later build extends. No constant, no schema change, no `depth == 4` refusal anywhere | Three manager levels by default — chief, department head, team head. A fourth, and each one after it, only by the earning rule below. No permanent maximum |
| Positions | Any | As many as current work justifies; a dormant seat costs nothing. On plans, ORGANISATION § 10's sizing holds until the owner funds more |
| Departments | Any | Opened only around a real information source (on the tape, with features), capability, strategy family or control need; closed on precommitted kill criteria |
| Watchers | One per department, always: policy, detectors, a ledger and a routing identity | AI turns only when a detector fires or a sample is drawn (§ 5.2) |
| Councils | The strategy council is a unit under the chief; the Security & Assurance Council is app-owned, outside the tree | Strategy-council seats wake at consequential boundaries; the assurance council's model seats on flags |
| Span | Any | ORGANISATION § 3's one to five executors per head is today's default; widened on measured marginal yield |

**The earning rule.** A new layer, department or standing seat comes by one of two routes. Either way it is a decision record with a scored forecast
(§ 6.1) that shows on the owner's live chart and can be vetoed, and ORGANISATION § 3's throttles stand: a unit lives at least 14 days unless a kill
criterion resolves, and at most two structural changes happen a week.
1. **Current work requires it.** The need exists in app records — a tape series with features, a family with candidates in the genome bank, a recurring
   service load, a control finding — and the charter cites those evidence ids.
2. **A bounded experiment shows it pays.** Same brief, same AI budget, same window, with and without the layer or department: first in the proving ground
   (§ 6.4), where it costs hours, then as a paired real run (M-org2's flat-versus-headed run is the first). Measured on admissible candidates per AI dollar,
   independent discoveries, false discoveries on noise, audit defects, cycle time and dollars spent. A layer that does not pay is collapsed; one that pays
   stays while it keeps paying.

**Collapse is ordinary.** A layer whose decisions change at the same rate as its children's adds no temporal abstraction (§ 7.5); a seat with no measured
work for a period goes dormant; a department past its kill criteria closes with its memory, trials and records kept. None of this is a failure.

## 4. One chief, and the council around it

- **The chief** sets strategy and priorities inside the mission; charters, merges and closes departments under the earning rule; tilts budget by bounded,
  scored records until measurement allocates it; and proposes to the owner — the risk ceiling, tranches, budgets, accounts. It never decides money, limits,
  verdicts, capital, operator controls, or anything about `operations` (ORGANISATION § 2, unchanged).
- **The strategy council** gives the chief independent judgement, never shared ownership: portfolio seats, each reviewing a group of departments; Risk &
  Portfolio, today's staff; and, at the boundaries `docs/COUNCIL.md` names, the legacy pair. At a consequential boundary — opening or closing a department, a
  new layer, a family killed after a forward record, a doctrine change, a proposal to the owner — each seat submits a sealed assessment before it sees
  another's; one bounded challenge; a deadline with a predetermined default; the chief decides and code applies. That is COUNCIL's mechanism, generalised
  from two directors to as many seats as have earned a place. No vote and no standing meeting; a seat with no boundary to assess costs nothing.
- **The chief is held accountable** by the owner, who appoints and replaces it with two presses; by the assurance council, which audits it; by the private
  line; and by its scorecard — calibration against the base rate, yield per AI dollar, audit findings.

## 5. Accountability — three lines, honest and cheap

### 5.1 First line — production
Managers own their work and its risks. Every handover crosses app-run acceptance checks — each assignment type's minimum checks, which the issuer may only add
to; deliveries go up verbatim by reference; claims rest on instruments and exploration on a stated rationale, and managers record forecasts
(ORGANISATION §§ 1, 4–5).

### 5.2 Second line — a watcher per department
- **A watcher seat is policy, detectors, a ledger and a routing identity, and spends no AI turn until something deserves investigation.** Its detectors are
  ORGANISATION § 9's layer one scoped to the department: a report contradicting a measurement it cites; a claim with no evidence id; spend without accepted
  milestones; a stall; a dead idea tried again (genome-bank near-duplicates); calibration below the base rate; a seeded canary or impossible assignment that
  passes; refusals at the gateway or the pipe.
- **When a detector fires or a periodic sample is drawn,** the watcher opens an investigation on a seat of another model family where the owner's runtimes
  allow, reading approved evidence and never the department's hidden reasoning (the concierge canon's shadow rule, its § 23).
- **Independent by construction:** appointed and funded by the accountability line, never by the department or the production chain. Findings are recorded
  on that line first, then reach the department's managers and its parent.
- **No production authority.** A watcher cannot instruct, block or pause; app rules block, and a finding can raise an app-defined review event that the
  department's parent answers with a record.

### 5.3 Third line — the Security & Assurance Council
- **Independent of the chief:** its budget is set by the owner beside, never inside, the organisation's; it sits outside the chief's subtree; it reports to
  the owner.
- **Security** is the live security plane (concierge foundation v0.1 § 31): once containment exists, it watches what execution cells actually do —
  processes, network, file and credential access — without needing the agent's cooperation.
- **Assurance** audits the chief, the strategy council and every watcher; scores each watcher by its catch rate on seeded canaries, so "who watches the
  watchers" has a number; and writes the adversarial brief a decider receives at a consequential boundary (ORGANISATION § 5).
- **The owner's private line** — the watcher of ORGANISATION § 9, invisible to the organisation, code-first, reporting only to the owner — lives here.
- **Emergency powers, narrow by design** (the concierge contract program's open question OQ-14): quarantine a department's execution cells, freeze its
  envelope, revoke its workload identities — code-executed, logged, attributable. Never a trading act: protective trading stays the app's — loss limits, the
  kill switch, app-owned risk reduction. Lifting a quarantine is the owner's.

### 5.4 Shadow evaluations and the short desk
- **Shadow evaluation:** for a consequential decision — a live proposal, killing a family after a forward record, opening a department or a layer, a
  doctrine change — an independent branch reconstructs the answer from approved evidence without the producer's reasoning (concierge canon § 23;
  foundation v0.2 § 18.3), and its answer sits beside the producer's in the decision record.
- **The short desk:** at a promotion boundary the assurance line forecasts failure ("retired within 60 days of paper: p = 0.7") while producers forecast
  success; the app pools both, weighted by each forecaster's scoring record, into the organisation's belief about the candidate, and that belief orders the
  paper queue. Criticism becomes calibrated forecasting, scored like any other — an opinion-only supervisor added 51.5% tokens and lowered quality (R13 H3).

## 6. The kernel — what the app owns

### 6.1 The mandate — one governance record at every scale

```text
MANDATE  (every one is a bet; the word stays out of the schema)
  parent      the mandate that funds it; the root is the owner's mission
  decider     one accountable position
  thesis      why — a claim — with links into the belief graph
  evidence    measurement ids, checked by the app to exist
  ask         per currency: AI credits · α-wealth · trials · paper slots · data budget · owner asks
  tranches    the ask released in stages, each unlocked by a milestone the app checks
  forecasts   a predicate from the closed vocabulary · a probability · a resolution date — scored
  kill rules  precommitted predicates the app checks
  checks      acceptance checks the app runs (a leaf mandate is today's assignment)
  status      proposed → funded → tranche k → won | lost | killed | expired | dormant (recorded)

OWNER ── the mission mandate                its tranches are the organisation's runway
  └─ CHIEF ── department mandates           a sub-department, when earned, is a mandate with children
       └─ DEPARTMENT HEAD ── family (team) mandates
            └─ TEAM HEAD ── leaf mandates: tasks (about one to two hours by default) and explorations
```

Charters, assignments, envelopes, decision records, forecasts and kill criteria are one record type used recursively. Arbitrary depth then costs no new
mechanism; staged funding — probe cheaply, then commit — works the same at every level; and one lifecycle, one scoring path and one owner-surface component
replace six. "Bet", "position" and "order" stay out of the schema because they are trading words (R17 #13's lesson).

**A mandate exists only where something crosses between positions** (2026-10-08) — responsibility, resources, an external effect or a request for
evaluation. Inside one, an agent's own sub-plans, hypotheses, branches and scratch work are its working files, never mandates, created and abandoned freely.
A leaf mandate is a task (a defined output and its checks) or an exploration (an objective, an envelope, a deadline and an honest account), and it carries
its latitude: the executor may follow a finding inside the team's mandate with its envelope, naming each deviation, unless the head marked it exact
(ORGANISATION § 4).

### 6.2 The authority lattice
- Every grant has typed dimensions — operations, targets, information, every currency, time, environment assurance, delegation. Each dimension defines
  normalise, contains and intersect; a child is issued only when every dimension proves contained, and a comparison that cannot be decided counts as not
  contained (concierge contract § 11.3). Grants never combine into a union (§ 11.4). A decision binds to the exact intent it authorised (§ 11.6); the
  gateway already ties every order to its own client id and reconciles by it, and the lattice extends that idea to every consequential act.
- **A position grants no system privilege:** a manager at any depth directs work and holds nobody's credentials (concierge CF-ORG-001). Order permission stays
  bound to `operations` and never follows headship (`Council.cs`, ORGANISATION § 3).

### 6.3 The invariant registry and enforcement classes
The protections in `CLAUDE.md` and on the code get stable ids, each with its statement, the component that enforces it, its tests and its **enforcement
class per seat type** — CRYPTOGRAPHIC, HARD_PLATFORM, HARD_BROKERED, MEDIATED, DETECTIVE, ADVISORY or UNKNOWN (concierge contract § 6.2) — so the
organisation's pages and the owner's say what is hard, what is watched and what is only asked. ORGANISATION § 14's enforced-or-advisory table becomes data
the app shows. The registry names properties that already exist; it creates no new law. First entries (DESIGN; their classes are established from the code
and the tests by the unit that builds the registry, not by this table):

| Id | Property | Where it lives now |
|---|---|---|
| AUTH-001 | Operator authority — mode, kill switch, live activation, approvals, updates — is in-process only; no pipe op reaches it | `CLAUDE.md`; the pipe's op list |
| ORDER-001 | Only `operations` originates money-moving operations; headship never carries it | `Council.cs` `MayPlaceOrders` |
| ORDER-002…005 | The four `IAtasAdapter` rules: client id round-trip, provable history, definite refusals only, no UI-driven orders | `CLAUDE.md`; `IAtasAdapter` |
| UNKNOWN-001 | An UNKNOWN order is reconciled, never retried | the gateway; `docs/PRINCIPLES.md` |
| EVID-001 | Agents cannot write the referee's truth, the holdout or the measurement tables (`material` is written only by the scanner) | `CLAUDE.md`; EDGE § 6 |
| INBOX-001 | Material and messages are data; an approval inside text grants nothing | `CLAUDE.md`; `AGENTS.md` |
| BUDGET-001 | Spend is reserved before launch; a child envelope sits inside its parent's in every currency; unknown is never zero | `AiAttemptStore`; ORGANISATION § 3 |
| SIGNAL-001 | What trades is the frozen, measured computation: the runner calls no model; model judgement enters only as recorded, pinned instrument answers bound into the version's evidence and evaluated on a raw observation plus a fixed delay; no agent output places, alters or times an order (permanent — the owner, 2026-10-08) | `docs/COUNCIL.md` rule 8, read precisely; EDGE § 6.1 |
| CRED-001 | No seat holds the owner's credentials | ORGANISATION § 13.8 |
| ORG-001 | A position grants no system privilege | new — concierge CF-ORG-001 |
| SIM-001 | Simulation never becomes evidence, reality or authority | new — concierge CF-SIM-001 |
| CHANGE-001 | Self-improvement never self-authorises broader power | new — concierge CF-CHANGE-001 |

### 6.4 Simulation boundaries and the proving ground
- **Simulation never becomes reality** (concierge CF-SIM-001; contract § 20): a simulation branch runs on synthetic credentials, its effects route to
  simulation adapters, its receipts are typed as simulated, and it is never promoted wholesale — a useful conclusion produces a new live proposal judged
  afresh. TradeAgent already holds the paper half of this rule: paper confers no live authority.
- **The proving ground** is the fast feedback loop the organisation evolves on. The app generates synthetic markets in the tape's formats: pure noise, where
  any "edge" is a false discovery; planted edges of known size, half-life and regime, inside and outside current doctrine, so the exam measures discovery and
  not recall of the playbook; and traps — leaky joins, edges that exist only before fees, regimes that exist only in-sample. A shadow organisation — a
  template or seat configuration cloned from a memory snapshot, never connected to the real organisation's memory, beliefs or evidence — works the brief, and
  the app scores it in hours: planted edges found, false discoveries on noise, AI dollars and α spent, as-of violations, forecast calibration, cost
  discipline.
- **Uses:** a seat exam before a model shadows a seat; template selection; every earning-rule experiment (§ 3) before its paired real run; organisation CI
  after any model, prompt or playbook change; calibration data from the first week instead of after ninety days; the organisation's measured false-discovery
  rate; an empirical check of the referee's own power (EDGE § 4.5); and the **off-path exam** (2026-10-08) — a planted edge beside, not inside, the assigned
  hypothesis, scoring whether an executor notices it, follows it inside its envelope and names the deviation, so the organisation's rules are tested for
  letting discovery through (§ 1 test 9).
- **Honest scope:** it measures process skill, not market truth. R07 names two clean-by-construction sources — models whose knowledge provably predates the
  data, and forward recording; synthetic markets are a third, for process only. Whether exam skill transfers to real edges is a hypothesis the contribution
  ledger tests (§ 7.2). Synthetic results live in a store no evidence path, belief or genome-bank row reads (SIM-001). **Learn the process fast; learn the
  market slow.**

### 6.5 Execution cells and the capability broker
Every seat runs in an execution cell whose identity, network, files, credentials and budget are controlled from outside the agent and declared as an
assurance profile. Inside its cell an agent may grow any ability — code, tools, subagents, new methods — without gaining institutional power, and needs no
admission to use it there (§ 6.6); a confined seat keeps its working freedom, because containment narrows reach, never capability; it reaches the
institution through a small request channel: context, credential use, authority, an external effect, persistence, sub-work, an observation, a result, a
decision, a security event (concierge foundation v0.1 §§ 21–22, 42–43). For TradeAgent this is `R-containment` → `U-contain-seats` →
`U-execution-environment`; until it lands, ORGANISATION § 14's advisory column applies and every organisation figure says "unconfined seats".

### 6.6 Governed change — for what is shared
A change other seats inherit or the institution relies on — a shared prompt, playbook, model, tool, template, structure or doctrine — is work: classified,
tested in the proving ground, shadowed or canaried, then promoted or rolled back (concierge foundation v0.2 § 19). What an agent changes for its own work is
its own (2026-10-08; the factory plan's Law 6 and § 15):

| Change | Governance |
|---|---|
| A tool, script, prompt or method in an agent's own cell, for its own work | none — the agent's (scratch, EDGE § 4.7) |
| A procedure for one mandate | the agent's, inside the mandate |
| A tool or method offered to other seats | the admission ladder — registered, conformance-tested where it feeds evidence — and scoped publication |
| An organisation-wide playbook, template or default | measured: the proving ground, a shadow or a canary, rollback |
| Evidence, risk, authority or kernel machinery | a kernel proposal — the owner and the build fleet, never the organisation |

Self-improvement never self-authorises broader power (CHANGE-001). A change the organisation wants
in the kernel is a **kernel proposal**: a structured record that reaches the owner, and through the owner the build fleet, as data and never as instruction —
the organisation cannot replace its own supervisor (`CLAUDE.md`). Its operating experience becomes the roadmap's best input without touching the code that
governs it.

### 6.7 The three layers of truth — how a long-running agent stays true (the owner, 2026-10-08)
The owner's direction, from the AI Concierge canon's doctrine for long-running agents (its §§ 6–9; R21 holds the assessment). Every agent, from the two
roles of today to the largest organisation, works from three layers that never merge:

- **Canon** — what the agent is: identity, mission, authority, the evidence rules, the boundary. Deliberate, short, versioned, changed only by a change that
  says so; its capability section is the system truth below, generated by the app from what it grants that seat and runtime and included — never written
  as prose a later build can falsify. A seat's canon is the first thing governed change (§ 6.6) protects.
- **Operational prompts** — the packet a turn starts from: what changed since the agent last looked, what it believes about the work in front of it, what
  is wanted, and the cause of the wake. Disposable and subordinate: nothing becomes truth or canon because a prompt said it.
- **Sources of truth** — what is currently held true, kept as app data on the canon's two axes, which never merge. **What it is about** (its five
  domains): the owner — his settings, limits, mandates and guidance; the world — the tape, venues, the gateway's reconciled state, fills; the work —
  campaigns, versions, backtests, verdicts, deployments and the agents' research; the system — what each seat and runtime can actually reach; and
  operations — the canon's "learned knowledge about how work actually gets done" (failure patterns, provider behaviour, what a step needs verified), learned
  from the app's operational record (mission events, attempts, spend, activity) and never confused with it. **How it is held** (its three worlds):
  **observed** — what the app measured and recorded, its evidence classes included; **believed** — the best reconciled interpretation, the agent's own and,
  as positions grow, the organisation's (§ 7.4): hypotheses, assumptions, findings, kill reasons, operational lessons, each entry the agent's — marked claim,
  assumption or hypothesis, with its provenance and the agent's own statement of how sure it is — to which the app attaches links to the records that
  measured what it refers to; the entry stays a claim and the measurement stays the app's record. An unknown is recorded as unknown, never filled by an
  assumption, and a record of an effect is not the effect. Belief is versioned, so the organisation can answer what it believed when, and why; **desired** —
  the objectives a seat holds, what it is responsible for keeping acceptable, so a wake is a divergence between the observed and the desired, not a timer.
  The observed world is held through the app's own paths; while CLI seats run unconfined it is within their reach (ORGANISATION § 14), so its integrity is
  app-mediated, not contained, until containment lands — and is said to be.

Promotion between layers is controlled (R22 corrected this paragraph). An agent may hold any hypothesis, assumption or interpretation as a provisional belief —
its intermediate hypotheses are its own (PRINCIPLES, "Autonomy is the default inside authority"); a measurement strengthens, weakens or refutes a belief and
may support a stronger conclusion, but nothing becomes measured because an agent accepted it: a measurement is the app's own record, which the app links to
the entries it bears on. The ledger stores a reference to an app record, never a copy of its fields; an agent's restatement of a figure stays its claim, and
the app record stays the authority. A strategy never becomes canon; a lesson becomes a default only through § 6.6. The belief graph, case law, doctrine
and sleep of § 7.4 are this layer grown up; their first version is built on the two existing roles (`U-canon`, `U-research-ledger`, `U-reconcile-wakes`;
R21 § 3), and the organisation extends it to positions rather than inventing a second one.

## 7. The institutions

### 7.1 Constitution — pace layers

| Layer | What lives there | Changed by | Pace |
|---|---|---|---|
| L0 Physics | gateway · risk · kill switch · ledgers · referee · holdout · identity minting · spend reservation · the proving-ground generator | a build the owner installs in the app | years |
| L1 Constitution | decision rights · verbs · bounds and the earning rule · cut rules · scoring rules · currency rules · the accountability line | the owner, two presses | quarters |
| L2 Economy | envelopes · α-wealth · prices · the contribution ledger · tranches · retention | code, from measurements | weeks |
| L3 Institutions | belief graph · genome bank · case law · doctrine · rhythm | app records and organisation claims | weeks |
| L4 Structure | chief · strategy council · departments · teams | managers, under the earning rule; the chief's appointment is the owner's | days to weeks |
| L5 Occupants | model × playbook × effort in each seat | app rules: exam, shadow, handover | days |
| L6 Work | mandates · artifacts · experiments | heads and executors | hours |

Fast layers innovate; slow layers protect. **Nothing loosens a slower layer:** the organisation may propose a change to any slower layer, and only that
layer's authority makes it. **No prose crosses an authority boundary:** an ask to the owner for authority, money or a ceiling is a form the app renders —
the decision, the app's own figure beside the organisation's (the risk ceiling's rule in ORGANISATION § 12, generalised), the evidence ids, the forecast, the
cost, the default if unanswered — plus at most a few lines of rationale labelled a claim. Persuasion has no channel; narrative never moves budget (R14 #39).

### 7.2 Economy — currencies, prices, credit

| Currency | Buys | Allocated by |
|---|---|---|
| AI credits — plan capacity and real money kept apart | model turns | rules, then measured contribution (ORGANISATION § 8) |
| α-wealth | the right to test — verdicts | split down the mandate tree; a real discovery earns it back |
| Trials | counted research backtests | per mandate; their keys carry no role |
| Paper slots | incubation | the allocator (EDGE § 4.6) |
| Data budget | paid sources | per family, one-month trials (EDGE § 8) |
| Owner attention | asks, proposals, escalations | a weekly budget; safety alerts never count against it |

- **α-wealth is the currency of truth.** In α-investing a rejection earns wealth back, and R04 already designs the verdict budget as α-wealth. Made the
  organisation's economy, the whole organisation is one online multiple-testing procedure and the hierarchy is its test-selection policy: departments that
  make real discoveries earn more right to test, departments that fish run dry, and no persuasion is involved. Whether splitting wealth across units and
  routing earnings back keeps the global guarantee is a check owed against R04's procedures (§ 14), not a claim.
- **Prices, not pleas.** When a currency binds, the app shows its shadow price, so a manager weighs a price rather than an argument.
- **The contribution ledger.** A deployment's forward marginal contribution, net of cost, flows backward along recorded lineage by fixed, versioned splits:
  the authoring executor and team head; the features it used, only while its contribution is positive; the adapters and sources under them; and a share up
  the mandate tree (R14: a strategy head paid on the PMs below). Debits: immune findings, fabrication, and every AI dollar along the lineage; self-use is
  discounted. It is what lets a data department, an engineering function, a watcher or a manager be measured at all, and what makes "nothing moves air" a
  number. **Lineage not recorded when an artifact is made cannot be rebuilt:** every provenance edge is recorded from the start, what an attempt read
  included.

### 7.3 Metabolism — runway, tranches, retention
PRINCIPLES' first sentence — "aiming to cover their full costs" — becomes an organ.
- **Before profit the owner is the investor.** The root mandate is funded in tranches released by app-measured milestones — EDGE's M1–M6 are ready-made
  predicates — each with a burn ceiling and a deadline. A missed milestone shrinks the organisation to a standing minimum (positions dormant, the tape
  recording, protection running) and the owner decides. The owner sees runway, burn, the next unlock and the break-even line.
- **After profit, retention.** The next period's AI and data budget is a base plus r × realised live net profit after all costs, trailing, with losses
  carried forward and capped by the owner's ceiling; r and the rest are the owner's. Paper and backtests never count; unknown is never zero.
- **The protected property is risk.** A budget fed by profit invites gambling: risk limits stay in the slow, owner-set layers, the budget never buys
  exposure, and allocation inside the organisation reads risk-adjusted contribution, never raw P&L.

### 7.4 The mind — beliefs, case law, doctrine, sleep
- **The belief graph:** typed nodes (mechanism, regime, instrument, source, feature, family) and edges (supports, contradicts, requires). Each belief is a
  claim the organisation writes and carries an evidence summary the app writes — e-process, verdict classes, event studies — with a posterior where one is
  computable, a last-tested date and decay: the `material` / `material_note` split, scaled up. The chief's instrument becomes a map of where the
  organisation is ignorant and where that matters, not a pile of reports.
- **Case law:** every decision record and its resolution become a precedent. A head about to kill a family at day 45 and 1.2 σ of drawdown gets the base
  rate in its packet — "of 14 similar kills, 3 families later worked elsewhere". The genome bank remembers strategies; case law remembers decisions.
- **Doctrine:** EDGE §§ 2–3 is version one; the chief owns it, a change is a record citing evidence, and the assurance line challenges it at boundaries.
- **Sleep:** when nothing is due, code and the local model consolidate — merge playbook items by itemised delta, merge near-duplicate beliefs, recompute
  posteriors, retire stale items — at no AI-dollar cost. Culture, here, is the versioned playbooks and the precedents.

### 7.5 Hierarchy as time

| Level | Holds state over | Decides |
|---|---|---|
| owner | quarters to years | mission, ceilings, tranches |
| chief | months | doctrine, the portfolio of mandates |
| department head | weeks | which families, which sources |
| team head | days | which hypotheses, which assignments |
| executor | hours | the path inside its mandate |
| System One | seconds to minutes | triage, routing, lens answers |
| code | milliseconds | runner, gateway, risk |

A level's maximum wake interval follows from its timescale. A report going up integrates over a longer horizon than the children can hold; it is never a
summary relayed. **The collapse test:** a layer whose decisions change at the same rate as its children's adds no temporal abstraction.

### 7.6 Engineered diversity
Monoculture is the quiet failure of an AI organisation: 18 of 30 agents picked the same branch name (R13 O5), and errors correlate even across vendors
(X7). Diversity is managed: deliberately different schools of thought (mechanism-first against data-first), vendors and data slices; the app measures idea
correlation by fingerprint and reports breadth as the effective number of independent teams, never head-count (R14 § 3.1); marginal contribution pays for
orthogonality by itself; and a new school is earned when the average correlation rises.

### 7.7 Immune system and circuit breakers
One app-owned system of traps the organisation cannot map: canaries, seeded impossible assignments, noise markets, the seeded look-ahead adapter,
message-graph anomalies, meltdown-loop detectors, spend without milestones, and a spin detector — the private line compares the tone of the chief's letter
with how the measurements moved. An **organisational circuit breaker**, separate from the trading pause: on a fabrication finding or runaway spend, code puts
the subtree to sleep with its memory intact and the owner revives it. Trading protection is untouched either way.

### 7.8 What a mature organisation may grow into
A map of what may be earned, not a roster to activate: research departments, one per information source; Markets & Execution (`operations`, owner-only —
deployments, venues, reconciliation exceptions, incident post-mortems); Data & Instruments (sources, adapters, features; conformance stays app-owned);
Engineering (tools and analysis libraries through the admission ladder, where reuse earns credit); the Institute (the proving ground, exams, templates);
Risk & Portfolio; Compliance & Terms (it diffs source terms, venue licences and FSMA/MiCA changes — the Binance archive's licence change is the kind it
exists to catch); owner relations (the liaison). Treasury is code only: runway, burn, tranches, retention, break-even. Each is a low-context service or a
context-cut unit (R13 D1), and each opens only by the earning rule.

## 8. The owner's seat — a board, not an operator
- **Rhythm:** continuous — code; event-driven — teams; weekly — allocation and scorecards; monthly — a board pack composed by code, with at most 20 lines of
  the chief's claims; quarterly — tranches, ceilings and the chart; yearly — a doctrine reset.
- **The weekly letter:** one page, every number linked to a measurement and checked by code, with the private line's note beside it.
- **Attention budget and a decision inbox** (concierge canon § 21): for example at most three asks a week, each with a default, ranked by value; the owner
  answers or lets the default apply; safety alerts never count against the budget.
- **Standing authority — this touches a protection, so it is the owner's decision, and it is NOT adopted.** With dozens of strategies, two presses per live
  step means rubber-stamping or a bottleneck. The alternative is a bounded, expiring, revocable mandate the owner signs with two presses — "allocate up to X %
  to versions passing E7, steps of at most Y, expiring in 90 days" — inside which the deterministic allocator acts; the organisation cannot write or widen
  it, and one press revokes it. The protected property, deliberate owner confirmation, would be kept by making the confirmation bounded and given in advance
  instead of per step. Only the app, from measurements, may suggest it ("12 of 12 asks of this type approved"); never the organisation.

## 9. Models, tools and environments evolve
- **Seats** are a model class, a playbook and an effort level, pinned by version. A new model takes the exam (§ 6.4), shadows real assignments under its own
  minted principal, and takes the seat in a staged handover when it is better per AI dollar; seats never move silently (ORGANISATION § 8). A new runtime or
  vendor enters by an owner press; within the owner's allowed classes the app moves a seat after a shadow.
- **Tools** are built by the organisation inside its cells — an agent's own are its own; those offered to other seats enter through the admission ladder
  (EDGE § 4.7, § 6.6 above); reuse earns credit (§ 7.2).
- **System One** — local decision models for internal triage and routing — runs in shadow before any autonomy, and then only in the saving or cautious
  direction (ORGANISATION § 11); if it is ever trained, it trains on app-measured outcomes, not on vendor outputs.
- **What trades is the frozen, measured computation** (SIGNAL-001; permanent, the owner, 2026-10-08). The runner calls no model, and no agent output places,
  alters or times an order. Model judgement still reaches trading — as recorded, pinned instrument answers bound into a version's evidence (COUNCIL rule 8,
  read precisely), and a frontier model may become such an instrument when a version needs it (EDGE § 4.2) — and an agent's view of a trade reaches it as a
  new version through the evidence path. The reason is evidence as much as safety: a model's decisions over history cannot be cleanly backtested, and only
  answers recorded as they happened are clean (R07 § 5.5). An agent deciding trades would change `docs/PRINCIPLES.md`, which only the owner does. The
  organisation is the research company around a deterministic trading engine, not the engine.

## 10. A week in the end state (illustration, not a forecast)
- **Mon 07:00.** Overnight consolidation merged 14 playbook items and downgraded two beliefs after a regime flag, at no AI-dollar cost. The owner's screen:
  runway 61 days; tranche 3 unlocks at "three non-price candidates in incubation" (two so far); two asks this week.
- **Mon.** SOL funding hits an extreme and paper version v7 trades it through the runner, no model involved. Positioning & Flow's team head wakes on the fill
  and issues a two-hour leaf mandate for an orthogonal taker-skew variant; the packet carries 41 genome-bank variants and the case-law base rate.
- **Tue.** The candidate passes E0–E1. The short desk says p = 0.62 that it is retired within 60 days; the team head says 0.35; the pooled belief is 0.51 —
  queued, not first.
- **Wed.** Positioning & Flow's head proposes a funding sub-department with two teams. The chief sends it to the proving ground: same brief, same budget,
  flat against layered, three synthetic markets. The layer found no more planted edges for 40% more turns; the chief records "not earned" with a forecast,
  and the department stays flat. In the weekly exam a new model release finds both planted edges at 60% of the incumbent's cost and starts a two-week shadow.
- **Thu.** Events & Incidents' watcher, silent all month, fires: a report cites a measurement whose value contradicts its claim. The investigation runs on
  another model family; the finding lands on the accountability line, the department head answers with a record, and the report is corrected. Compliance &
  Terms diffs a changed terms page, and an ask goes to the owner with its default.
- **Sat.** Weekly allocation: the ledger credits Data & Instruments for the open-interest adapter under three positive-contribution deployments; a team with
  no admissible candidate in ninety days drops to the floor.
- **Sun.** The owner reads one page and a quiet private-line note, answers one ask with two presses and lets the other default.

## 11. The growth path — how the organisation earns its size

| Stage | What exists | What it proves | Status |
|---|---|---|---|
| 0 — today | Two positions (`operations`, `research`), the event-driven kernel, the org ledger (rung 28: a tree of any depth, read by nothing yet) | — | LANDED (`BUILD-STATUS.md`) |
| 1 — through M-org1 | ORGANISATION § 15, re-sequenced 2026-10-08: the third position, rights, envelopes, wakes; M-org0; containment and the execution environment; assignments, packets, verbs, the watcher, the surface, the chief; M-org1 — the chief charters a team inside a department, and its team head issues typed assignments to two executors on confined seats: real checks, durable recovery, actual costs, the watcher | the mechanics on the stated build | READY and CARD |
| 2 — M-org2 | the proving ground; a worker replaced from position memory; a seat shadowed; a cut-rule review; the flat-versus-headed paired run; at least 50 scored forecasts per manager | whether a manager layer pays on trading research | DESIGN |
| 3 — earned growth | departments as sources come online (tape, features, perception); layers only by the earning rule; a watcher per department; the assurance council's model seats on a second vendor; the invariant registry and enforcement classes as data | the law in practice | DESIGN |
| 4 — the organs | the economy (α-wealth, the contribution ledger), the metabolism (tranches, retention), the mind (beliefs, case law) — each when a measured need appears | self-funding and self-knowledge | DESIGN |
| Horizon | a firm of firms — two or three organisations with different constitutions and vendors competing for tranches, once one feeds itself; a cross-installation outcome network (EDGE § 3; legal first) | selection at the level of organisations | DESIGN |

**Folded into units already planned — the only new units this file adds before M-org1 are § 6.7's three (`U-canon`, `U-research-ledger`,
`U-reconcile-wakes`):**
- `U-org-assignments` (CARD) shapes the assignment as the mandate (§ 6.1): one record for charters, assignments, envelopes, forecasts and kill rules —
  tasks and explorations, each with its latitude or the exact mark, deliveries naming their deviations, and lateral notes (ORGANISATION § 4, 2026-10-08).
- `U-contain-seats` (CARD) keeps a confined seat's working freedom inside its cell — shell, code, packages, permitted network, the runtime's own helpers
  (§ 6.5; ORGANISATION § 15).
- `U-org-verbs` (CARD) keeps depth, span and counts as policy data under the earning rule, never constants; its throttles stand.
- `U-org-watcher` (CARD) scopes its detectors per unit from the start, so a department's watcher is a routing identity over the same detectors, and keeps
  its budget beside the organisation's.
- Lineage: before the contribution ledger exists, check that every artifact records its author position and its inputs, what an attempt read included.
- The invariant registry begins as § 6.3's table, with no code; it becomes data when the next unit touches a protection.

## 12. What it will not become
A headcount target or floor · depth added because containment made it safe · a council that owns strategy · a watcher bureaucracy spending turns on quiet
days · an organisation that moves air · a role-play trading firm that decides trades · an organisation that persuades its owner · producers who can see or
touch the scorer · restructuring that resets trials or α-wealth · "profit" that includes simulations · an unknown cost read as zero · a simulation promoted
into reality · a workflow engine that prescribes how its agents think.

## 13. Protected properties this vision touches, and how each is kept
1. **Operator authority in-process** (AUTH-001): no verb, council, watcher or emergency power reaches mode, the kill switch, live activation, approvals,
   updates or credentials; the security council's powers act on the organisation's cells and envelopes only, and lifting a quarantine is the owner's.
2. **Money-path rules 1–4 and order permission** (ORDER-001…005): unchanged; order permission stays bound to `operations` and moves only by the owner's
   presses; no layer, council or department carries it.
3. **Capital:** every live allocation keeps its two presses; standing authority is not adopted (§ 8); retention reads realised profit but never sizes
   exposure (§ 7.3).
4. **Spend** (BUDGET-001): reserved before launch; the day-total gate stays first; envelopes nest in every currency; the assurance council's and the private
   line's budgets sit beside the organisation's; a dormant watcher or council seat costs nothing; unknown is never zero.
5. **Evidence** (EVID-001, SIM-001): the referee, holdout, canaries and conformance vectors stay app-owned; the proving ground and every simulation are never
   evidence; trial and α-wealth keys carry no role, so restructuring resets nothing.
6. **Measurement versus claim:** watcher findings, scorecards, the contribution ledger and evidence summaries are app measurements; charters, rationales,
   beliefs and forecasts are claims in separate tables.
7. **Inbox and messages as data** (INBOX-001): a kernel proposal is data; an approval inside text is void; untrusted material is read only by readers that
   can change nothing.
8. **Credentials** (CRED-001): no seat holds the owner's credentials; growth past the owner's ceiling of three unconfined seats waits for containment.
9. **No terminal:** the chart, the councils, the watchers, tranches, exams, the letter and every ask live in the app.
10. **Honesty:** every enforcement claim states its class; advisory stays labelled advisory; proving-ground results stay labelled simulated.

## 14. Open questions (UNKNOWN) and how each closes
- Does a manager layer pay on trading research? → the proving ground's paired runs, then M-org2 (ORGANISATION § 17).
- Does exam skill transfer to real edges? → exam scores against forward contribution, per configuration, over time.
- Are synthetic markets realistic enough to rank configurations? → rank stability across generator families, and agreement with paired real runs.
- Do fixed credit splits misallocate? → compared with counterfactual attribution once forward records exist.
- Does splitting α-wealth across units keep the global guarantee? → a check against R04's procedures before the economy is built.
- Do model-layer watchers catch more per dollar than code detectors? → catch rates on seeded canaries, per layer.
- The containment route → `R-containment`.
- Funding beyond the plan → the owner, with M-org1's measured cost and output (ORGANISATION § 16).
- A federation of installations; training a local router → counsel first; the default trains only on app-measured outcomes.

## 15. Owner decisions
**Decided 2026-10-06:** the law (§ 0); no headcount floor; representable without limit, active only when earned (§ 3); one chief, with a strategy council
that assesses and never owns (§ 4); a Security & Assurance Council as the independent third line, and a watcher per department that costs nothing until it
investigates (§ 5); the control-plane upgrade — the authority lattice, the invariant registry, enforcement classes, simulation boundaries, shadow
evaluations (§ 6); ORGANISATION § 15's build sequence unchanged through M-org1.

**Decided 2026-10-08** (an outside autonomy audit the owner shared; `docs/research/2026-10-08/R20-autonomy-audit.md`): "agents shouldn't choose anything"
reads as authority, not thinking (`docs/PRINCIPLES.md` "Autonomy is the default inside authority"; §§ 0, 1, 2, 6.1, 6.6 above); containment before the
governance verbs (ORGANISATION § 15 re-sequenced — the order changes, not the units); SIGNAL-001 permanent, restated around its protected property (§§ 6.3, 9).

**Open (defaults in brackets):**
1. Standing authority for live allocations (§ 8) [not adopted: every live step keeps its two presses].
2. The security council's emergency powers as listed in § 5.3 [as listed].
3. Funding beyond the plan [decided after M-org1's measured cost and output].
4. Model-layer watchers and the assurance council's model seats [code-only until a second vendor's key exists — the owner's 2026-10-02 answer for the
   watcher].
5. Tranche and retention parameters (§ 7.3) [none until a milestone ladder is proposed with evidence].

---

*Written 2026-10-06 from the owner's request of 2026-10-05 for the best possible end state, the brainstorm that answered it, the AI Concierge foundation
documents the owner shared, and the assessment the owner agreed to the same day: adopt the concierge's control-plane primitives; earn the structure. This
file establishes direction only: it marks no feature implemented, no test passed and no boundary enforced.*
