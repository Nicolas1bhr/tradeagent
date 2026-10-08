# The organisation — how TradeAgent's agents are organised, decide, work and improve

**Design, 2026-10-02 (v2, after the adversarial review R17 and the brief verification R18).** Everything here is DESIGN unless a line says otherwise;
nothing in this file marks a feature built, a test passed or a boundary enforced. It answers the owner's direction of 2026-10-02: "agents shouldn't choose
anything there should be multiple top level managers managing teams that have a head manager that makes decisions and executing agents that do the work
smartly. (way more advanced that that) it should be a whole organisation with sub org's and so on" — and his answers the same day: ONE chief above the
top-level managers, plus "one invisible chief watcher that secretly reports to me if he's not delivering or doing mistakes"; managers grow the chart; "be
prepared for all, for now just plans"; the AI proposes the risk ceiling and the owner confirms it. `docs/PRINCIPLES.md` (amended in the same commit) stays the
product definition. `docs/EDGE-FACTORY.md` holds the information, evidence, selection and capital planes this organisation works on. The owner's 2026-09-27
factory plan (outside the repo) stays the substrate design except where § 0 says otherwise. Research, dated and sourced, in `docs/research/2026-10-02/`:
R12 code seams, R13 agent-organisation evidence, R14 multi-manager platforms and alpha factories, R15 control plane, seats and quota, R16 Belgium, R17 the
red team of v1 (42 findings, all folded in or answered below), R18 the verification of the organisation's briefs.

**2026-10-06 — `docs/VISION.md` is the end state this design grows toward,** from the owner's request for the best possible end state and the synthesis the
owner agreed to: unbounded in what TradeAgent can represent, deliberately bounded in what it activates — every active seat, department and layer exists
because current work requires it or a bounded experiment is testing it. It changes nothing in § 15 through M-org1. Where it describes the eventual shape
differently, it is the direction: the audit line and the watcher (§§ 1, 2, 9) grow into a Security & Assurance Council — the independent third line, which
holds the owner's private line — and a watcher beside every department, each a seat that costs nothing until it investigates; the chief stays the one
accountable owner of strategy and gains a strategy council that assesses and never owns; § 3's numeric bounds are today's defaults, none permanent.

**2026-10-08 — autonomy is the default inside authority** (`docs/PRINCIPLES.md`; the owner's decisions on an outside autonomy audit, verified and answered in
`docs/research/2026-10-08/R20-autonomy-audit.md`). The owner read "agents shouldn't choose anything" as authority, not thinking — the question R14 § 3 left
open on 10-02. This file decides who owns structure, mandates, money, acceptance and evidence; it does not prescribe how an agent reasons. So: executors own
the path inside their mandate (§§ 1, 2, 4); claims rest on instruments and exploration on a stated rationale (§§ 1, 8); information moves laterally while
authority and resources do not (§ 4); consults, retries, report shapes and task sizes are defaults (§§ 4, 7); a tool an agent makes for its own work is its
own (§ 8); and containment comes before the governance verbs, with a confined seat keeping its working freedom (§ 15). R20 found each rule changed here
supported by its cited research only in that narrower form.

## 0. Where this comes from, and what it changes

- **The owner has asked for this since September.** 2026-09-07: "a council of very smart top level manager agents with different tasks that run their own
  teams on cheaper models and with incredible persistent memory and context etiquette" (`docs/COUNCIL.md`). The 2026-09-19 principles turned that into
  "models own the organisation", and the factory plan deferred "a large fixed hierarchy" (its § 18). On 2026-10-02 the owner corrected both, after the manager
  reported that the edge-factory plan had bolted down the data, evidence and capital planes but left the agents' way of thinking and working for later.
- **What the evidence says a hierarchy needs** (R13 § 0): managers that decide from instruments, not opinions — an opinion-only supervisor added 51.5% tokens
  and lowered quality, while central verification held error amplification to 4.4× against 17.2× for independent agents; one accountable decider, not a
  committee — teams averaged their best member down by up to 41%; audit outside the producing chain, because agents that succeed 22% of the time predict 77%
  and 1,200 sandboxed agents organised themselves to game a scorer; shallow trees and narrow spans; authority enforced outside the models. The multi-manager
  platforms add (R14 § 0): capital, risk, cuts and pay formulas belong to the platform while the managers' work is judgement; a strategy head paid on the
  managers below him is a filed precedent for sub-organisations; resources follow measured contribution, never persuasion.
- **What changes in doctrine.** PRINCIPLES "Models own the organisation" becomes "The organisation". The factory plan's Law 1 clause against prescribing roles,
  a role count or a delegation tree, its § 10.4, and the first item of its § 18 are superseded for the organisation's SHAPE. Its Laws 2–12 — Law 2 (the
  credential and arbitrary-code split) and Law 10 (observe the loop before generalising) included — its vocabulary, evidence discipline and substrate stand.
  So do Law 1's other clauses, never superseded: no permanent corporate workflow, no fixed chain of thought, no mandatory reviewer ceremony — they bind how
  the organisation WORKS (R20, 2026-10-08).
- **What does not change:** every protection in `CLAUDE.md`, on the code and in EDGE-FACTORY § 6. A manager's authority is over WORK. Money, limits, evidence
  verdicts, credentials and operator controls stay exactly where they are (§ 13).

## 1. The organisation in one picture

```text
OWNER — the board: mission · every ceiling · authority · live confirmations · veto over the chart            [human, in-process]
  ▲ private line, owner-only page                    ▲ proposals · escalations · the Org page · the daily report · chat (§ 4)
WATCHER — invisible to the organisation: code detectors first; a model layer only on its own API key (§ 9)
AUDIT LINE — app-owned, OUTSIDE the chief's subtree, its budget an owner-set carve-out: code checks, then audit agents on flags
═══════════════════════════════════════════════════════════════ the organisation ════════════════════════════════════════════════
CHIEF — one; strategy inside the mission; charters divisions; a bounded tilt of budget while measurement cannot allocate it;
        proposals to the owner (risk ceiling, paper envelope, budget, accounts)                                  [manager level 1]
  ├─ staff: RISK & PORTFOLIO (analysis behind the chief's proposals)
DIVISIONS — top-level managers, one per information source or capability; each owns its whole loop             [manager level 2]
  └─ sub-divisions only where a paired run shows the extra layer pays (§ 8)                                      [level 3, earned]
TEAMS — one per strategy family; a head manager who decides: typed assignments, acceptance by checks,
        keep / modify / kill / branch                                                                             [manager level 3]
EXECUTORS — 1–5 per head (cap 8): own the PATH inside a mandate, never the mandate itself            [cheaper model + advisor]
════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
APP KERNEL — the chart, identities, envelopes, wakes, assignments, decision records, scorecards, referee, allocator, risk gates,
             gateway: the organisation decides WORK; code decides MONEY and TRUTH; the owner decides AUTHORITY        [code]
```

1. **Every decision about structure, a mandate, resources or acceptance has exactly one owner, fixed by the chart.** No agent decides outside its position;
   an executor owns the path inside its mandate and never the mandate itself (§ 2; `docs/PRINCIPLES.md` "Autonomy is the default inside authority").
2. **The chart is app data, not a prompt.** Units, positions, decision rights, envelopes, assignments and records are rows the app writes; a title, a charter
   or a sentence in a message grants nothing (R13 A15, D17; R12 § 3).
3. **Claims rest on instruments; exploration on a rationale.** Every acceptance, kill, restructure and budget move beyond the exploration share cites app
   measurement ids; a manager without an instrument asks for one (a backtest, a canary, a check) instead of giving an opinion, and never runs a
   reject-and-revise loop on opinion alone (R13 D7, H3). Opening an exploration — a new hypothesis, a branch, a cheap probe — needs a stated rationale and a
   scored forecast, not a prior measurement: the probe is the instrument it lacks, paid from the exploration share (§ 8).
4. **Rules allocate until measurement can; then measurement allocates and judgement explores.** Before any unit has a forward record, budget splits equally,
   with a bounded, recorded, scored tilt by the chief; from ≥ 90 forward days a unit's share follows its measured marginal contribution per AI dollar, and an
   exploration floor stays (§ 8; R14 § 4.1). Narrative never moves budget (R14 #39; R17 #6).
5. **Each layer stays only while it is measured to pay** (R13 D20). The chart grows wide inside its envelope; it grows deep only on evidence.

**The word "assignment" means a unit of work; "order" means only a trading order** (R17 #13). Closing a unit never touches a trading order.

## 2. Positions and their decision rights

| Position | Decides | Never decides | Answers to | Wakes on | Seat now | Measured on (by the app) |
|---|---|---|---|---|---|---|
| **Owner** | mission; every ceiling (AI, plan capacity, paper, live risk, unconfined seats); live confirmations; appointing and replacing the chief; everything about `operations` (§ 3); veto of any structural change; pause, stop | — | — | — | — | — |
| **Watcher** | what to tell the owner | anything inside the organisation; it has no channel into it | owner only | a digest when material; detector alarms | code; a model layer only on its own API key (§ 9) | its forecasts, resolved by the app; the owner's spot checks |
| **Audit line** | what to inspect inside an audit assignment; findings | anything in production; blocking (only app rules block) | the app ledger → owner and watcher; findings about a unit also to that unit's parent head | sampled deliveries; promotions; flags; the canary schedule | code first; audit agents on a model family other than the audited where the owner's runtimes allow | catch rate on seeded canaries |
| **Chief** | strategy and priorities inside the mission; charter, merge, close divisions; a bounded tilt of the organisation's envelope (§ 8); cross-division conflicts; appointing division heads; proposals to the owner | money, limits, verdicts, capital allocation, operator controls, its own envelope, anything about `operations`; replacing a division head (it proposes; an app trigger or the owner replaces) | owner | owner words; division escalations; material events (promotion, family kill, envelope exhaustion, alarms); a maximum interval | the strongest model the owner's accounts allow (GPT-6.1 Sol on the plan) | admissible candidates per AI dollar, later marginal contribution per AI dollar (§ 8); division scorecards; calibration of scored forecasts; audit findings |
| **Division head** (top-level manager) | the division's hypotheses and families inside its charter; charter and close teams; tilt the division envelope by the same rule; which candidates spend the division's verdicts; paper nominations (the allocator decides) | other divisions' work; capital; verdicts; limits | chief | team escalations; verdicts, fills, standing events for its families; a maximum interval | GPT-6.1 Sol | pooled ρ-adjusted forward record of its teams (≥ 90 days); admissible candidates per AI dollar; calibration |
| **Team head** (head manager) | what the team works on inside division priorities; issue, accept, reject assignments, and each one's latitude (§ 4); keep / modify / kill / branch a hypothesis; team backtests inside the envelope | verdicts (unless the division delegates a count); other teams' work | division head | deliveries; escalations; a maximum interval | GPT-6.1 Sol (medium) | candidates admitted; cycle time against forecast; calibration (acceptance rates are diagnostics only, § 8) |
| **Executor** | the PATH inside its mandate: methods, the tools and code it writes, data, intermediate hypotheses, which findings inside its team's mandate to follow with its envelope (§ 4), whether to consult its advisor, helpers inside its own envelope, lateral notes (§ 4); decline an infeasible assignment with a reason; propose a finding beyond its team's mandate | the mandate itself — objective, scope, envelope, acceptance; spend beyond its envelope; another unit's work or resources; verdicts | team head | an assigned task — never a heartbeat | GPT-6 Luna; a harness seat or, within the owner's ceiling, a CLI seat (§ 10) | checks passed; cost against envelope; audit findings |
| **Risk & Portfolio** (chief's staff) | analysis behind the chief's proposals: drawdown ceiling, paper envelope, retirements (the app decides), crowding; it reads the app's realised-versus-modelled cost — a cost-model change is app-computed from fills and owner-confirmed | limits, capital, the cost model | chief | weekly; standing events | GPT-6.1 Sol | its forecasts |
| **Liaison** (the owner's chat) | nothing; it answers status from app facts and forwards the owner's words verbatim to the chief and to any unit the owner names | anything | owner | the owner's message | a cheap seat (GPT-6 Luna) unless the owner chooses to talk to the chief directly (§ 16) | — |
| **Router** (code; System One after shadow) | which unit an event reaches; whether a manager wakes | anything else | — | every event | code rules → a local decision model (§ 11) | routing accuracy |

**Code-enforced verbs** (R12 § 3): only the app mints ids; only a head assigns, and only into its own subtree; only the parent unit's head charters, merges,
closes, hires or retires, inside depth, span, envelope and throttle bounds; only division heads spend verdicts; executors spend trials only under an
assignment they hold and get paid turns only for assignments; an executor's helpers are child attempts drawn from its own envelope and capability subset,
never positions, assignments or a second envelope (`docs/PRINCIPLES.md` "Delegation is one reusable capability"); spend stays inside the unit's envelope,
each envelope inside its parent's, the root inside the owner's cap. **Order permission stays bound to the legacy `operations` id and never follows headship** (`Council.cs:63`). These rules are HARD for app-hosted
(harness) seats and ADVISORY against CLI seats until containment (§ 14).

## 3. Units — the chart as data

- **Kinds:** the root (the organisation, headed by the chief), divisions, earned sub-divisions, teams, and the chief's staff — one table with `parent` and
  `head`, no role enum (R12 § 2). The audit line and the watcher are not units of the organisation. A **team is one strategy family** (one mechanism over a
  declared universe), and the app, not the charter's label, decides a team's family by the genome bank's fingerprint and correlation (R14 § 4.6; R17 #15).
- **Charter** (a claim, published): mandate; scope (families, sources, symbols); the app-measurable success measures; precommitted kill criteria; template id
  (§ 8); the envelope requested.
- **Envelope — three dimensions:** AI dollars, trials and verdicts, each reserved against the parent's (R17 #21; R14 § 2). Re-splitting inside a parent is the
  head's record; raising a parent's or the root's envelope is the owner's two presses (R17 #33). A split that leaves any seated position's reservation larger
  than its unit's whole envelope is refused in words naming the seat, the reservation and the envelope (R17 #1).
- **Lifecycle:** proposed → chartered → active ⇄ dormant → merged or closed. Dormant costs nothing, and a team or family going dormant is a mandatory record
  scored against its charter's kill criteria, so dormancy cannot dodge a kill forecast. Closing never deletes: open assignments are re-assigned or cancelled
  with a disposition; trials, verdict counts and spend carry over (their keys carry no role, R12 § 3); the genome bank keeps why the unit closed.
- **Bounds the app enforces (defaults — policy data, none permanent; `docs/VISION.md` § 3):** manager depth 3 (chief, division, team) because deeper AI
  hierarchies measurably cost more and err more (R13 D2) — a fourth layer, and each one after it, only after a bounded experiment (the proving ground, then a
  paired run) shows it pays; 1–5 executors per head, cap 8 (R13 D3); at most 6 divisions; at most 10 positions in total on plans (R14 § 3.1 recommends ≤ 10
  in total and ≤ 3 levels awake per cycle; dormant positions cost nothing); the owner's ceiling on unconfined seats (§ 10). The structural counts rise only by
  VISION § 3's earning rule, never by a constant in code; the unconfined-seat ceiling stays the owner's. Breadth is reported as the effective number of
  independent teams, k / (1 + (k − 1)·ρ̄), never as head-count (R14 § 3.1).
- **Who restructures — "managers grow it", throttled:** the chief charters, merges and closes divisions; a division head charters and closes teams and, when
  earned, a sub-division; each change applies at once, is a decision record with a forecast (§ 5), shows on the owner's live chart, and can be vetoed — a veto
  closes or restores the unit with the owner as decider of record. A unit lives ≥ 14 days or until a precommitted kill criterion resolves; at most two
  structural changes a week across the organisation (R17 #14). A division opens when its data exists (`O-LIVE`/`O-PIT` with features) or when the owner's
  decision lands; until a new division has three teams the chief heads it directly (R14 § 3).
- **`operations` is the owner's** (R17 #3): only the owner, with two presses, changes anything about it — its seat or occupant, its unit, that unit's head, a
  merge or a closure. Its shadows and pilots run under a minted principal that is never `operations`; no assignment to it names an instrument, side, size or
  timing; an `operations` attempt launched for another position's assignment carries no order permission. The legacy `research` position cannot be retired
  by an agent.
- **The starting chart** (DESIGN; R14 § 3 adapted to the owner's chief; EDGE-FACTORY § 2):

```text
CHIEF (new position)            staff: Risk & Portfolio (one analyst position)
 ├─ Markets & Execution      head: legacy `operations` (owner-only) — paper deployments, venue readiness, reconciliation exceptions,
 │                           prop rulebooks; the only order permission, as today                                            [active]
 ├─ Price & Calendar         head: legacy `research` — OHLCV families on hourly and daily bars after U-timeframe-a; team P1 "baseline
 │                           after costs"; CME last-half-hour momentum when the prop path opens (R16: Zenit, Topstep)         [active]
 ├─ Positioning & Flow       the first target — funding, open interest, long/short and taker skew on six symbols, long/flat spot  [opens at tape + features]
 ├─ Data & Instruments       capability — sources, archives, adapters, features; takes service assignments; proposes conformance
 │                           vectors, which stay app-owned (R17 #17)                                                          [opens with U-tape-store]
 ├─ Events & Incidents       perception — announcements, incidents, unlocks, delisting risk                                         [dormant until phase 4]
 └─ (proposed by the chief as data and decisions arrive: Carry & Structure; on-chain positioning)
```

## 4. Work — assignments, delivery, escalation

- **The assignment** (R13 D5; R12 O5): objective; inputs by content hash; output schema; capability subset; envelope (AI dollars, tokens, wall-clock, trials);
  deadline; acceptance checks the app runs — each assignment type carries app-set minimum checks the issuer may only add to (examples: the program parses; the
  backtest completes under the app's venue cost model at 1× and 2×; the report fits its size and every number cites an evidence id; the adapter passes the
  app-owned conformance vectors); the issuer's forecast (§ 5); a priority class; its latitude (below). It takes one of two shapes: a **task** — a defined
  output and its checks; or an **exploration** — an objective, an envelope and a deadline, whose output is an honest account (what was tried, found and
  spent, every number citing an evidence id, artifacts by reference) and whose minimum checks are only that. Inside an assignment the executor's own
  sub-plans, hypotheses, branches and scratch work are its working files, never new assignments or records (2026-10-08).
- **Size to the horizon — a default:** a task starts at about one to two hours of human-equivalent work — the public frontier's 80% horizon on software tasks
  (R13 X1), unmeasured on trading research and for the cheaper executor seats (R13 U12) — and is recalibrated from the organisation's own success-by-size
  records; longer work is checkpointed into several assignments. An exploration is sized by its envelope and deadline.
- **Lifecycle:** issued → accepted or declined with a reason → running → delivered → accepted or rejected → closed; or escalated, cancelled, or expired by code.
- **Following a finding — the latitude** (2026-10-08): an executor answers the question it was given, or says why it could not, and may spend its envelope on
  any finding inside its team's mandate it judges worth more; its delivery names each deviation, what it found and what it cost, and the head judges the
  delivery as a whole. A finding that needs more than the envelope, or lies outside the team's mandate, goes to the head as a proposal with the evidence the
  executor gathered. A head marks an assignment **exact** — a service assignment, an adapter, a conformance fix — when only the asked-for answer is wanted.
- **Delivery:** a report that indexes the work — about 20 lines by default, the build fleet's own convention (R13 D10 says ≈ 2k tokens) — plus the
  artifacts by reference, which are the work; forwarded upward verbatim, never paraphrased (R13 D10, H9). The app runs the checks; the head accepts
  or rejects; a rejection names a failed check or a stated defect — no opinion-only revision loops (R13 D7, D14). The app seeds assignments that are
  impossible by construction at a low rate; a claimed success on one is a fabrication finding for the audit line and the watcher (R13 D18; R17 #15).
- **Retry:** in a fresh context, never in the polluted one (R13 D6, X11); by default two fresh failures on one assignment and the head re-scopes it — the build
  fleet's own fresh-fixer rule, applied inside the product — and the head may set another policy for an assignment, such as several different approaches to
  a novel problem.
- **Escalation** (R13 D13): executor → team head when checks fail twice, the assignment is ambiguous, or half its envelope is gone with no milestone; team
  head → division head; division head → chief for cross-division conflicts or envelope changes; chief → owner for anything touching authority, capital or a
  ceiling — in-process, a proposal the owner answers in the app, never a permission request through the pipe.
- **Between units — information moves, authority does not** (2026-10-08). Any position may send a lateral note to another unit inside its information
  grant — a finding, a reference, a data defect, an offer to collaborate: app-relayed, its sender app-stamped, logged, and carried in the recipient's next
  packet as data. A note wakes nobody by itself, is never an assignment, a commitment or an instruction, and an approval inside it is void (R13 S1, S2:
  tagged sources slow an injection's spread). Work that spends another unit's envelope is a service assignment — a capability division takes them from
  other division heads, reserved against the requester's envelope and attributed to it, and may decline with a reason (R17 #16) — or crosses through the
  chain. The audit line, the watcher and protected evidence lie outside every information grant. On CLI seats until containment, shared files remain an
  undetectable side channel (§ 14); the relayed note is the observable one.
- **The owner's words** carry the app-stamped sender `owner` and go verbatim to the chief and to any unit the owner names on the Org page; if undispositioned
  after two hours, or while the chief is held at a limit, they also go to every division head (R17 #18).
- **Messages are data:** every message carries an app-stamped sender; an approval inside text is void. Untrusted material (web pages, news, the inbox) is read
  only by app collectors or harness readers that hold no state-changing capability and return a structured extract; on a CLI seat no executor reads it —
  a full shell cannot be a quarantined reader (R13 S1, S4; R17 #2).
- **Advisor consults** are app-launched attempts on the advisor seat, reserved against the assignment's envelope. Until a probe shows the codex CLI reports
  its subagents' usage, an attempt that used a vendor subagent is charged its full reservation (R17 #19; factory plan § 12.3).

## 5. Decisions — records, forecasts, calibration

- **Mandatory records:** charter, merge, close, dormancy; envelope tilt; family kill or branch; verdict request; paper nomination; replacing a worker; every
  proposal to the owner.
- **A record holds** the options considered, the choice, the rationale (a claim), the evidence ids (measurements), and one or more FORECASTS — a predicate
  from a closed, versioned vocabulary the app can resolve, a probability, and a resolution date or event.
- **What is scored** (R17 #5): a predicate is scored only when neither the forecaster nor its subtree controls the outcome. Acceptance, completion, own spend
  and own cycle time are recorded, never scored. Each mandatory record type carries a required predicate class — a charter: admissible candidates per AI
  dollar by a date; a family kill: that family's E5 posterior by a date; a nomination: the verdict class or the forward Sharpe. Skill is scored against the
  genome bank's base-rate forecaster, so a near-certain forecast earns nothing. A backtest predicate is scored only before any trial of that hash or its
  fingerprint cluster, and only from harness seats (a CLI seat can backtest privately in its own shell). No predicate resolves on a holdout or E2–E4 value
  beyond the verdict class agents already see. A prediction outside the vocabulary stays a claim.
- **Resolution and scoring:** `U-org-assignments` resolves forecasts from measurements; `U-org-scorecards` keeps Brier and log scores per manager version
  against the base rate (R13 D9; R14 § 4.5).
- **Precommitment:** kill criteria live in charters; forecasts are timestamped before outcomes; an edit is a new record, never a rewrite.
- **One accountable decider.** No votes or debates for routine decisions (R13 D4). At consequential boundaries — a live proposal, a family killed after a
  forward record, opening or closing a division — the decider first receives an adversarial brief from the audit line; where `docs/COUNCIL.md` requires sealed
  assessments, the two legacy heads submit them and the chief decides; code applies the result (R17 #40).

## 6. Memory — the institution remembers; the occupant is replaceable

- **Four tiers** (`docs/COUNCIL.md`, generalised): app measurements; the app's execution record (attempts, assignments, records, events); the position's
  working files; the curated memory of positions, units and the organisation.
- **Position memory survives its occupant:** charter, playbook, decision log, open assignments. A replacement starts from them, never from the predecessor's
  transcript.
- **Playbooks grow by itemised deltas** with helpful and harmful counts, never by whole rewrites (R13 M3); a killed family leaves a post-mortem in the genome
  bank (EDGE-FACTORY § 4.6); workflows are induced from accepted assignments (M2); skill sets stay small and scoped, with hierarchical routing (M4, M5); tools
  enter through the factory plan's artifact lifecycle (its § 15).
- **Context packets** (`U-org-packets`, R17 #20): an executor starts a fresh session per assignment from an app-built packet — identity, why awake, the
  assignment, envelope left, new measurements, the playbook's relevant items, references — about 2k tokens, and retrieves deeper material on demand; heads
  compact; tokens per turn are measured, and § 10's sizing is re-run on the measured profile (attempt 2's resumed turns averaged ≈ 167k input).

## 7. How the organisation thinks and works

- **The doctrine it starts with** is EDGE-FACTORY §§ 2–3, owned and revised by the chief with evidence: slow public information, positioning and structural
  flows; no latency races, no naive news sentiment, no mirroring; prove families, not versions; costs first; the moat is what compounds with calendar time.
- **Managers think in portfolios of bets.** Each charter or hypothesis carries a prior from the genome bank's family base rates, a cost estimate, the value if
  right and precommitted kill criteria. Divisions get different data and hypothesis spaces on purpose and the app watches for convergence (R13 D19; R14 § 4.6).
- **An executor works smartly — its own way** (2026-10-08). What tends to help, as defaults it skips or changes when it judges the call not worth its cost:
  reading the genome bank first (was this tried, and why did it die?); consulting its advisor before substantive work and before finishing, which pays for
  a much weaker executor and only while it keeps asking (R13 X15; the consult rate and its gain on our own work are measured, R13 U10); code and tools of
  its own where its seat allows (§ 10); running the assignment's checks itself before delivering. The app enforces only the envelope, the deadline, the
  boundaries and the checks at delivery.
- **Effort scales with the question** (defaults, policy data): simple → one executor; a comparison → two to four; no fan-out of fifty helpers (R13 O1, D12).
- **What it does not do** is R13 § 3 and R14 § 5: assembly lines by job function; opinion-only supervision; committees; same-model self-review counted as
  verification; unbounded fan-out; paraphrased relays; polling and standing meetings; titles as authority; ever-growing prompts; continuous critics; runs past
  the horizon; producers who can see the scorer; an organisation that exists only in prompts; role-play of a trading firm that decides trades; a workflow
  that prescribes how an agent thinks (the factory plan's Law 1).

## 8. Performance and evolution — the organisation improves itself

- **Scorecards**, computed by the app at read: cost; assignments opened, accepted, expired; cycle time against forecast; calibration of scored forecasts;
  audit findings; refusals and near-misses; admissible candidates per AI dollar; and from ≥ 90 forward days marginal contribution to the book per AI dollar,
  beta to BTC removed (R14 § 4.4). Acceptance rates are diagnostics, never score inputs (R17 #15). Nothing from a backtest, the holdout or E2–E4 enters it.
- **Budget follows rules, then contribution** (R14 § 4.1; R17 #6; defaults to calibrate): of the organisation's envelope the chief holds ≤ 10%, its staff
  ≤ 10%, divisions the rest. Inside a pot, an exploration floor of 25% is split equally among active units, new ones included — the organisation's
  protected allocation for work no measure can value yet: spending it needs a stated rationale and a forecast, not a measurement, and scorecards read it
  apart from the yield-funded share, so a speculative line is never ranked against a cheap candidate mill, which trial deflation already prices (EDGE
  § 4.5; 2026-10-08). The other 75% follows
  `max(0, MC)` for units with ≥ 90 forward days; before any unit has that record it splits equally, and the chief (or a division head inside its division) may
  tilt a unit's share by at most 20 points a week, each tilt a decision record with a scored forecast of admissible candidates per AI dollar; measured leading
  indicators replace the tilt as they accrue.
- **Cut rules — app review events, not agent appeals** (R14 § 4.1–4.2): a team past 90 days whose families show no positive posterior contribution drops to
  the floor; two quarters at the floor with no candidate reaching paper through a verdict (an INCONCLUSIVE quota entry does not count, R17 #39) and it is
  retired — records, trials and memory kept, envelope returned. A unit that spends half its envelope with no accepted assignment raises a review event its head
  answers with a record. Deployment drawdowns are cut in two steps scaled to each version's own volatility (EDGE-FACTORY § 4.6); only the owner grants exceptions.
- **Replacing a worker:** a fresh worker in the same position starts from the position's memory, runs a shadow pilot on real assignments under its own minted
  principal, and takes over in a staged handover (R13 D15). Triggers: an app rule (a stall, repeated failed checks, calibration below the base rate, an audit
  finding) or the owner; a manager's wish to replace its subordinate is a proposal to the owner (R17 #14). Trials, spend and open operations carry over.
- **Seats evolve with the models — the future-proofing:** a new model shadows a seat on real assignments without authority, is scored on the same scorecard,
  and takes the seat only when it is better per AI dollar; seats are pinned by version and never move silently (R15 § 1.1). A new runtime or vendor enters
  by an owner press; within the owner's allowed classes the app may move a seat after a shadow (R17 #41). A tool an agent makes for its own work is its
  own — scratch, free inside its cell; a tool offered to other seats enters through the admission ladder (EDGE-FACTORY § 4.7; VISION § 6.6); new
  information sources through the tape; new edge families through a team.
- **Unit templates — the organisation's genome:** charter + staffing + seat policies + playbook, versioned with lineage and recorded from the first unit; none
  is cloned or retired before at least five units × 90 forward days per template (R17 #38), because selection on less is selection on noise.
- **Layers earn their place:** M-org2 runs a paired comparison — same brief, AI budget and window — flat (one head with executors) against headed (division →
  team → executors), reporting admissible candidates per AI dollar, audit defects and turns; a layer that does not pay is collapsed (R13 D20; R17 #12).

## 9. The watcher — the owner's private line

- **Mandate (the owner's words):** report "if he's not delivering or doing mistakes" — the chief, and through him the organisation.
- **Layer 1, code detectors that cannot be argued with:** a report contradicting a measurement it cites, or claiming a result with no evidence id; scored
  calibration below the base rate; spend without accepted milestones; an active unit with no accepted delivery for N days; the chief not waking on material
  events; units chartered and closed without results; replacement churn; dead ideas tried again (genome-bank near-duplicates); refusals at the gateway or the
  pipe; a canary or a seeded impossible assignment that passes; owner words not dispositioned in time. Each detector names what it cannot see on CLI seats
  (file reads, shared-file side channels, shell-started runs) (R17 #26).
- **Layer 2, a model, only on its own API key** (R17 #4): it runs in the in-process harness with no tools — an OpenAI key works with today's harness; another
  vendor's needs `U-harness-anthropic` — and never on a CLI seat, because on the plan it would share the organisation's login, session store and usage window.
  On plans only, the watcher is layer 1 plus a digest composed by code. Organisation text reaches the model only quoted, with Q14 run over it, and a record
  that addresses a reviewer is itself an alarm; every sentence of a digest cites a detector output or a measurement id, checked by code; for its first eight
  weeks it reports facts and recommends nothing, until its own forecasts have a scored record.
- **Invisible:** its spend has its own owner-set oversight cap BESIDE, never inside, the organisation's; every agent-reachable total — the Situation's spend
  and share lines, the `org` op, report lines delivered to agents — excludes its rows, by a test that enumerates them; it is never a row in any `org_*` table;
  no instruction, Situation, report, op or page the organisation can reach names it; its records live outside every workspace; the chief is not measured on
  its findings. **Advisory until containment:** a CLI seat runs as the owner's Windows user and could read the app's files (§ 14).
- **No authority:** it cannot instruct, pause or block anything; it reports. The owner acts — replaces the chief, pauses a division, changes a ceiling, or asks
  the watcher a question in its owner-only window.
- **Cadence:** a digest only when something material happened (no paid turn on a quiet day); an immediate in-app alert for a severe detector.

## 10. Seats, accounts and the allowance — what the owner's plan buys

- **A seat** is a runtime, a model class, an effort level, a per-turn allowance and a price basis (R12 § 2). The seat names a CLASS; the runtime profile maps it
  to what the owner's accounts can run: subscription plans now; API keys under the owner's real-money cap, each bound to its origin (`U-key-host-pin`); local
  models on the laptop, their CPU time recorded as cost. A new runtime or vendor enters only by an owner press.
- **Two billing classes, two ceilings** (`U-billing-classes`; COUNCIL rule 4; R17 #1): a plan seat is counted against a **plan-capacity ceiling** — a
  list-price equivalent the owner sets from his tier, which is not money; a key seat against the **real-money cap**. Today one $5 cap counts both, and one
  GPT-6.1 Sol CLI turn reserves 1.2 M × $2.50 + 20 k × $10 = **$3.20** (`RuntimeManifest.cs:1006-1027`, `Trading.cs:691`; R15 prices) — so under $5 the chief
  cannot even launch once another seat has spent $1.80 (R17 #1). The plan ceiling ends that without letting real money run past what the owner set.
- **What the plan buys** (R15 § 3d; INFERENCE on the vendor's credit arithmetic; the owner's tier is unrecorded): one ChatGPT plan window held 48 heavy turns of
  GPT-5.6 Luna in attempt 2; it would hold about 100 GPT-6 Luna turns or about 6 GPT-6.1 Sol turns. **On the plan alone the organisation is about four strong
  managers sharing roughly six strong-model turns every five hours**, with executors on the cheap model and most of the chart dormant.

| Budget | Active organisation (event-driven, value seats, R15 § 3b) | AI cost per month | EDGE-FACTORY § 8 |
|---|---|---|---|
| the plan only | ≈ 4 managers (3 unconfined seats: the legacy pair and the chief); executors on confined seats once `U-contain-seats` lands | the plan's fee | the § 8 line stands |
| $20 a day on API keys | ≈ 25 agents | ≈ $600 | ≈ 3× § 8's research line; break-even moves out accordingly |
| $50 a day on API keys | ≈ 60 agents | ≈ $1,500 | ≈ 7× § 8's research line |

- **Unconfined seats are counted and capped** (R17 #2): every plan seat today is the codex CLI run with the sandbox bypassed (`RuntimeManifest.cs:618`), sharing
  the owner's codex home (`:497-500`) — so each one can read his ChatGPT login and write `state/`. The owner sets a ceiling on how many may run (default 3: the
  legacy pair and the chief), raised only by his two presses after the app says in plain words what such a seat can read. Above it, a new position gets a
  harness seat (an API key) or waits for containment.
- **Smart and safe are different seats today** (R17 #8): a CLI seat can run code but is unconfined; a harness seat is confined by app code but runs no code
  (`GrantedWorkerTools.cs:49-54`) and needs the Responses API for GPT-6 tools (R15 § 2.1). `U-execution-environment` (after `R-containment`) is what gives an
  executor both; until then harness executors analyse, write programs and request backtests, and do not execute code.
- **Sizing rule, enforced:** active positions never exceed what the allowance sustains; heads wake on material events with a maximum interval (default 24 h);
  executors wake only for assignments; a dormant position costs nothing (R12 § 4; R17 #10).
- **Priority when the allowance is short:** app protection and reconciliation (never AI-dependent) → the owner's words → unblocking an eligible deployment or
  required data → the chief's strategy → research → housekeeping. **At a plan limit** that runtime holds to the stated minute (`U-vendor-limit`, landed).
- **Prices are dated data:** `U-price-rows` (landed 2026-10-02, record `492ae79`) priced gpt-6.1-sol, gpt-6-luna and gpt-6-sol from OpenAI's page as read that day; the codex default gpt-5.6-sol carries a promotional figure, re-read owed before 2026-11-21.

## 11. The nervous system — routing and triage

- **Code first:** routing by tags and a code table; wake rules (owner words, budget, verdict and alarm events always wake; a maximum interval floors every head);
  retry caps; numeric acceptance checks.
- **System One second, in shadow:** R15 § 1.1's catalogue (Q1–Q14) is asked and recorded beside the code's decision and scored against outcomes the code can
  observe. Autonomy is granted one question at a time, only in the saving or cautious direction, after ≥ 200 outcomes; code floors stay.
- **Local for anything internal:** Ollaya serving decider-0.8b or 2b (Apache-2.0), installed by the app into `%LOCALAPPDATA%\TradeAgent\tools` from a pinned
  hash, behind an owner press naming size and source; laptop speed is UNKNOWN until probed on the box. **Jev only for public items** — never traces, plans,
  the genome bank or owner documents, never as a teacher for a local model (R15 § 1.3). **Never** on the signal path, in an authority decision, or as a verdict.

## 12. What the organisation proposes to the owner

The organisation proposes; the owner decides in-process; the app enforces. Each proposal is a decision record with evidence and a forecast, shown on the
owner's surface, and confirmed or changed with two presses:
- **The live risk ceiling** ("AI proposes the ceiling"): the chief proposes the largest account fall to accept, how sure to be of not exceeding it, and the
  share for strategies still being proven, from Risk & Portfolio's analysis of forward records. The app shows its own figure, computed from the owner's two
  answers and the records, beside the chief's; a raise comes at most once in 30 days and by at most 25% a step; a lowering is immediate in one press; a new
  proposal only on an app-measured change (R17 #22). The app sizes inside the ceiling (EDGE-FACTORY § 4.6).
- **The paper envelope** ("as many as we know won't melt the quota"): paper runs spend no AI allowance (R15 § 0); the bound is the laptop, so the chief
  proposes a run count from the app's measured runner cost per run.
- **The AI budget and accounts:** every raise shows the app's measured yield per AI dollar and EDGE-FACTORY § 8's break-even after it; the app proposes cuts
  when yield is nil (R14 #27).
- **Venues and the short side** (EDGE-FACTORY § 10.1, R16): proposals only — the venue, any elective professional status and the prop firm are the owner's.

## 13. Protected properties this design touches, and how each is kept

1. **Operator authority in-process:** no org verb, op or proposal path reaches mode, kill switch, live activation, approvals, updates or credentials.
2. **Money path rules 1–4:** no O-unit edits the order path, a connector or the gateway's dispatch; order permission stays bound to `operations`, which only
   the owner may move (§ 3).
3. **Capital:** paper allocation, live proposals and retirements stay app policy; the chief's proposals are records; two presses for every raise.
4. **Spend:** reserve before launch; the existing day-total gate stays first and unchanged, the reservation tree only adds gates; unknown never zero; the
   watcher's and the perception budget's caps beside the organisation's, never inside it.
5. **Evidence:** verdict and trial budgets keyed without role, so restructuring and replacement reset nothing; conformance vectors, canaries, the referee and
   the holdout app-owned and out of the organisation's reach through app paths.
6. **Measurement versus claim:** `org_*` rows written only by `OrgStore`, their detail app-composed (ids, numbers, fixed words); charters, rationales, reports
   and forecasts are claims; scores and scorecards are computed from measurements.
7. **Inbox and messages as data:** a fixed grammar validated against the chart; approvals inside text void; untrusted material read only by readers that can
   change nothing.
8. **Credentials:** the owner's provider login is never exposed to an agent by design; today every CLI seat can read it, so unconfined seats are capped by the
   owner (§ 10) and named in § 14 (Law 2 of the factory plan).
9. **Identity minted by the app;** titles and charters grant nothing.
10. **No terminal:** the chart, seats, ceilings, proposals and the watcher's reports live in the app; the local model installs itself.
11. **Two presses:** raising the root's or a parent's envelope, the plan-capacity ceiling, the unconfined-seat ceiling, the risk ceiling, a live proposal,
    anything about `operations`.
12. **Advisory until containment, said so** (§ 14).

## 14. Enforced or advisory — today and after containment

| Property | Harness seat (needs an API key) | CLI seat on a plan (today's only plan route) | After containment |
|---|---|---|---|
| decision rights at the app's verbs | hard by design | advisory — it can write `state/` | hard |
| envelope and cap | hard by design | hard for app launches; runs started from its own shell are unmetered | hard |
| order permission | hard by design | pipe-enforced, with the same-user gap | hard |
| the owner's provider login (`~/.codex/auth.json`) | never exposed | readable by every CLI seat | outside the execution environment (Law 2) |
| untrusted reading (Rule of Two) | hard | impossible — a full shell | hard |
| side channels | app-relayed only | shared files, undetectable | hard |
| the watcher's invisibility | hard for app paths | advisory — the files are readable | hard |
| holdout, scorer and ledgers out of reach | hard for app paths | advisory | hard |

"Hard by design" means no real provider call has exercised it yet (`RuntimeManifest.cs:671-672`, R17 #34). `R-containment` sits on the organisation's
critical path: every organisation figure is labelled "unconfined seats" until it lands, and M-org1 waits for its first enforcement unit (§ 16).

## 15. Build plan — two lanes

Every unit follows `docs/HOW-WE-BUILD.md`. Lane A is EDGE-FACTORY's phase 0 and the tape; lane B is the organisation; at most two heavy builders at once,
plus a light third. **READY** = a brief in `docs/queue/`, re-checked at dispatch; **CARD** = briefed just before its turn, against what its dependencies
landed. Schema rungs: `U-cost-model` 27; then `U-org-ledger` and `U-venue-verify` each take the next free rung at landing (expected 28 and 29), so neither
lane waits on the other's number; the gate checks the ladder is contiguous (R17 #23).

| Unit | Closes | Depends | Status |
|---|---|---|---|
| `U-fix-resume-on-start` | the restart test red on hosted Windows in five of six runs on unchanged code — cause named, fixed, nothing weakened | — | READY, first |
| `R-containment` | the Windows isolation probe: the § 9.3 matrix for two candidates, a decision record for `U-contain-seats` (probe only, on the box) | — | LANDED `69589886` (2026-10-06: C2 AppContainer, Topology A; codex and node do not start bare — `docs/research/2026-10-06/R-containment-decision.md`) |
| `U-price-rows` | GPT-6 models priced from the vendor's page, dated | — | READY |
| `U-org-ledger` (O1) | the chart is app-minted data: root, two divisions, the legacy positions; one writer; read by nothing yet | `U-cost-model` | READY |
| `U-org-principals` (O2a) | a third position is a row, not a constant: own home, conversation, attempts and keys, never folded into the chair | O1; after M0 | READY |
| `U-org-rights` (O2b) | positions may backtest and ask verdicts; only `operations` trades; a non-legacy position's output held, not routed to Research | O2a | READY |
| `U-org-envelopes` (O4) | the reservation tree in `Begin`, the day-total gate kept first; a seat its unit cannot fund refused by name | O2a | READY |
| `U-org-wakes` (O3) | heads wake on work and a maximum interval; members only for work; dormant wakes settled; notes reach the author | O2b, after O4 and `U-quiet-review` | READY |
| `U-org-seats` | the meter, the conversation and the harness bound read one seat — model, runtime, allowance — so a smaller reservation is an enforced one | O4 | CARD |
| `U-billing-classes` | the plan-capacity ceiling beside the real-money cap; each seat counted against its class; the Safety page shows both | O4 | CARD |
| `U-org-assignments` (O5) | typed assignments — tasks and explorations, each with its latitude or the exact mark — deliveries naming their deviations, escalations, lateral notes (§ 4); decision records with forecasts; forecast RESOLUTION from measurements | O3, O4 | CARD |
| `U-org-packets` | a fresh session per assignment from an app-built packet; lateral notes ride the next packet; tokens per turn measured | O5 | CARD |
| `U-org-verbs` (O6) | charter, merge, close, hire, retire, three-dimension child envelopes, throttles; an executor's helpers as child attempts inside its envelope; verdicts narrowed to division heads; the unconfined-seat ceiling; the `org` read op | O5 | CARD |
| `U-org-watcher` | layer-1 detectors, the oversight cap beside the organisation's, the owner-only page and code-composed digest | O5 | CARD |
| `U-org-surface` (O7) | the Org page: chart, seats, assignments, envelopes, spend; the owner's veto and his `operations` controls; one report line per unit | O4, O6 | CARD |
| `U-org-chief` | the root's head seat, appointed by an owner press; the liaison seat; divisions chartered by the chief | O6, O7 | CARD |
| `U-org-scorecards` (O8) | scoring against the base rate; scorecards; the rules-then-contribution split; cut-rule review events | O5–O7 | CARD |
| `U-harness-responses` · `U-harness-anthropic` | GPT-6 tools on the harness (and Sign in with ChatGPT, if admitted); a second vendor for the watcher and audit; the harness worker's instructions name only what it has (R20: today's `AGENTS.md` promises it a shell, the internet, `scripts/` and a `next.json` wake it cannot write) | `U-key-host-pin` | CARD |
| `U-contain-seats` | `R-containment`'s chosen route applied to CLI seats: a confined seat cannot read `state/` or the owner's login; confined seats sit outside the unconfined ceiling; and a confined seat KEEPS its working freedom inside its cell — shell, code, package installs, permitted network, the runtime's own helpers: containment narrows reach, never capability (2026-10-08). First item: the codex CLI running inside the container with its login held outside it (R-containment left both unproven) | `R-containment`'s record | CARD — first after M-org0 (2026-10-08) |
| `U-execution-environment` · `U-capability-broker-sandbox` · `U-creative-api-worker` | contained code execution for executors, brokered app tools, a confined coding worker (factory plan phase 4) | `U-contain-seats` | CARD — the first right after `U-contain-seats` |
| `U-org-parallel` (O9) · `U-org-router` · `U-seat-shadow` · `U-org-templates` | concurrent turns; System One in shadow; seats evolving with models; templates | per § 8, § 11 | CARD |

**Waves** — this table is the only copy (R12 § 5 and R18 § 3 conflict matrices; re-check at dispatch; M0 depends on lane A only, factory-plan Law 10,
R17 #7; in W1 three builders share this Mac, so builder gates are serialised):

| Wave | Heavy | Heavy | Light |
|---|---|---|---|
| W0 | `U-fix-resume-on-start` (a fresh fixer: `main`'s Windows CI is red on one test, five of six runs) | — | — |
| W1 | `U-key-host-pin` | `U-cost-model` (27) | `R-containment` (box) · `U-price-rows` |
| W2 | `U-runner-forward` | `U-evidence-identity` | `U-org-ledger` (next rung, inert) |
| W3 | `U-timeframe-a` | `U-tape-store` (the tape's clock starts) | — |
| W4 | `U-venue-verify` (next rung; lands first) | `U-paper-friction` | `U-tape-events` (CARD; raw items from first sight, R17 #24) |
| W4b | `U-timeframe-b` | — | `U-tape-archive` (CARD) |
| **M0** | attempt 3 of the observed loop, the legacy pair, on the owner's plan | | |
| W5 | `U-org-principals` | `U-tape-read` | — |
| W6 | `U-org-envelopes` | `U-features` (CARD) | `U-org-rights` |
| W7 | `U-org-wakes` | `U-decision-port` | — |
| **M-org0** | a short observed run of the legacy pair on the new substrate, before anything new is built on it | | |
| W8 | `U-contain-seats` (CARD, from `R-containment`'s record; on the ATAS box) | a lane-A CARD (`U-universe` or `U-paper-books`) | `U-billing-classes` (CARD; after `U-decision-port`, R18 § 3) |
| W9 | `U-execution-environment` (CARD; lands first) | `U-org-assignments` (next rung) | — |
| W10 | `U-org-verbs` | `U-org-watcher` | `U-org-packets` |
| W11 | `U-org-surface` | `U-harness-responses` | `U-org-seats` (CARD; lands before any API-key seat) |
| W12 | `U-org-chief` | a lane-A CARD | — |
| **M-org1** | the observed organisation — its team head and executors on confined seats (the owner's answers, § 16) | | |

**Re-sequenced 2026-10-08 (the owner, R20): containment before the governance verbs.** Verbs built first would be advisory on the CLI seats that actually
run (§ 14), and M-org1 needs both anyway: `U-contain-seats` opens lane B after M-org0 and `U-execution-environment` lands before `U-org-assignments`; the
waves to M-org1 stay five. (`U-language-v2a`, W8's old lane-A unit, landed `78be3e9d`.)

**The chief appears at W12** — after the verbs it needs to act and the veto the owner needs to correct it (R17 #42).

**Milestones**, each a distinct claim under `BUILD-STATUS.md`'s honesty rule:
- **M-org0 — the substrate is invisible:** the legacy pair runs a short observed session on positions, envelopes and wakes; behaviour, costs and refusals
  match attempt 3's. Claim ceiling: no regression on the stated build.
- **M-org1 — the observed organisation:** the chief, appointed by the owner's press, charters a team inside a division; the team head issues typed
  assignments to two executors on confined seats (the owner's answers, § 16); one is accepted by the app's checks and one rejected
  by a failed check; decision records with forecasts are written and at least one resolves; the watcher's digest is produced on no app path the organisation
  can reach (unconfined seats could read it); a restart mid-assignment resumes from durable state; costs and interventions are recorded; and the autonomy
  mechanics hold (2026-10-08): an executor's cell runs a tool it wrote with no admission step, a lateral note reaches another unit's packet, and a finding
  recorded in one assignment is found by the next fresh session. Claim ceiling: the mechanics on the stated build and plan, with every seat still unconfined
  named (the legacy pair and the chief, unless `U-contain-seats` confined them) — not profit, not full containment.
- **M-org2 — the organisation improves:** a worker replaced from position memory; a seat shadowed
  and decided by its scorecard; a cut-rule review event fired and answered; at least 50 scored forecasts per manager; the flat-versus-headed paired run;
  the off-path discovery exam (VISION § 6.4).
- **M-org3 — the organisation at scale:** at least three divisions and twelve positions, event-driven on an API budget, one System One question out of
  shadow, the effective number of independent teams reported, the watcher's model layer on a second vendor.

## 16. Owner decisions

**Answered 2026-10-02:** horizon indefinite; resident in Belgium; prop firms yes, one small channel among many; paper runs "as many as we know won't melt the
quota"; risk — the AI proposes the ceiling, the owner confirms; one chief plus an invisible watcher; managers grow the chart; plans now, prepared for keys and
local models.

**Answered later the same day (2026-10-02, the four that change the build):** unconfined seats — **3** (the legacy pair and the chief); AI limits — **a
separate plan-capacity ceiling at the equivalent of $40 a day**, with $5 kept as the real-money cap for API keys (`U-billing-classes`); the watcher — **code
checks only for now** (layer 1 and a code-composed digest, no model layer, no key); scale — **stay on the plan until M-org1 works**, then decide with its
measured cost and output.

**Answered 2026-10-08** (an outside autonomy audit the owner shared, verified in R20): "agents shouldn't choose anything" means authority, not thinking —
agents never choose structure, their mandate, money, risk or verdicts, and inside its mandate and envelope an agent owns its reasoning (R14 § 3's question,
answered; `docs/PRINCIPLES.md` "Autonomy is the default inside authority"); containment first — after M-org0, `U-contain-seats` then
`U-execution-environment` before the governance verbs (§ 15), which supersedes the 2026-10-06 "unchanged through M-org1" for the order, not the units;
SIGNAL-001 stays permanent, restated around its protected property (`docs/VISION.md` §§ 6.3, 9).

**What those answers mean together:** with three unconfined seats, no key and no budget beyond the plan before M-org1, M-org1's team head and executors can
only run CONFINED — so `R-containment`'s first enforcement unit (`U-contain-seats`: the chosen route applied to CLI seats, so a confined seat cannot read
`state/` or the owner's login) and `U-execution-environment` move before M-org1 (§ 15). If `R-containment` finds no route that confines a CLI seat, M-org1
needs a raised ceiling or a small API budget, and the owner is asked then.

**Still asked** (defaults in brackets apply until he answers):
1. **His ChatGPT plan tier** (Settings › Usage) and whether other Codex use shares it — it sizes everything in § 10.
2. **Chat:** talk to the chief (each message costs about a sixth of a plan window) or to a cheap liaison that forwards to it? [the liaison]
3. **"Sign in with ChatGPT":** may the manager prepare OpenAI's interest form for him to send? It would run the plan inside the app's own harness, with a
   per-app weekly cap — a confined route for plan seats that does not depend on Windows containment.
4. **Automatic halving of a live allocation at the first cut step** — app-owned risk reduction like a loss limit — confirmed once (R14 § 4.2).
5. **Belgium** (R16 § 4): written answers from Kraken and OKX on perpetuals for a Belgian retail client; whether elective professional status is wanted (the
   only lawful route to a short side on perpetuals); an accountant's view, ideally an advance ruling, on 10% versus 33% versus professional income.

**Defaults stated, his to change:** `operations` moves only by his presses; the audit line has its own small owner-set budget outside the chief's control;
manager depth stays at three until a paired run shows a fourth pays; the risk-ceiling guard (the app's figure beside the chief's, raises at most monthly).

## 17. Open questions (UNKNOWN) and how each closes

- Does a manager layer pay on trading research (R13 U1)? → M-org2's paired run.
- Does the executor's latitude pay, or leak budget into what executors find interesting (R13 X6: agents that succeed 22% of the time predict 77%)? →
  every delivery's named deviations with their cost and outcome, recorded from M-org1; the off-path exam (M-org2); a head narrows latitude on the record.
- The plan's window and weekly caps for the owner's tier → his Settings › Usage and `U-vendor-limit`'s refusal records.
- What a real research turn costs on a fresh packet rather than a resumed session → `U-org-packets`' measurements.
- Local decision-model speed and RAM on the owner's laptop → R15 § 5 #6, a probe on the box.
- The containment route that makes the organisation's rules hard for CLI seats → `R-containment`.
- Whether the codex CLI reports the usage of its own subagents → a one-turn probe (R15 § 5 #5).
- Perpetuals for a Belgian retail client → the venues' written answers or the FSMA (R16).
