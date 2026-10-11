# In-app orchestration — the organisation kernel

**Architecture, 2026-10-11.** How the TradeAgent app runs its agent organisation: how it identifies, funds, wakes, feeds, confines, connects,
measures and grows its agents. It is the runtime architecture that `docs/ORGANISATION.md`'s design and `docs/VISION.md`'s kernel (§ 6) are built
as. It is **not** the build fleet: `docs/FLEET.md` and `docs/HOW-WE-BUILD.md` say how we build; this file says what the product runs. Everything
here is DESIGN unless a line says otherwise; nothing in it marks a feature built, a test passed or a boundary enforced. Every enforcement class named
below for a seat that does not exist yet is a TARGET, to be established by the unit that builds it on a stated build and platform (§ 5).

**Where it sits.** `docs/PRINCIPLES.md` is the product definition and outranks it. `docs/VISION.md` is the end state. `docs/ORGANISATION.md` is the
organisational design — who decides what, how work, decisions, memory and evolution flow — and its § 15 is **the only order of work**: this file
decides HOW the kernel is built and which unit builds which component, never their order or status. `docs/EDGE-FACTORY.md` holds the planes the
organisation works on. Every protection in `CLAUDE.md` and on the code stands; § 8 maps each one this design touches to what keeps it.

**The owner, 2026-10-11, verbatim:** "we need to have the orchestration dialed in, as well build out as can be. this is a priority over all the
others. the vision should be clear enough but this is still outside of the box thinking since those processes are not really a genuine part of your
training. … so now lets start arching out that genuine in app ai orchestration and get it right from the get go. it's really important for this one
that you ground yourself in the vision and other documentation." **His two answers the same day:** containment and the organisation's work machinery
are built in PARALLEL, gated — nothing runs on real agents until executors have confined cells, and every claim states what is enforced and what is
only asked (this narrows his 2026-10-08 "containment first" from an order of landing to an order of activation, R20 § 5); and the first observed
organisation (M-org1) runs on the **Linux host**, so Linux confinement is built first. The record of this design, its two surveys and its review is
`docs/research/2026-10-11/R23-orchestration-first.md`.

## 0. The thesis

1. **One kernel; the organisation is data, never prompts — "row, rule, reading".** Every organisational concept exists as a **row** (application
   data with one app writer — never a prompt, a title or a file an agent can edit), is bound by a **rule** at an enforcement point (code, its
   enforcement class stated per seat type), and is judged by a **reading** (an app measurement: a check, a resolved forecast, a cost, a score). A
   concept without its row is role-play, without its rule is advice, without its reading is ceremony. The legacy pair become the first two
   positions and run through the same path as every later one; the kernel never grows a `CouncilRoles.All` with more names in it (the factory
   plan's § 10.4).
2. **Build the whole kernel; activate by measurement.** The owner wants the orchestration built out as far as it can be; VISION's law wants every
   active layer earned (§§ 0, 3), and so does the outside review of 2026-10-09 the owner keeps outside the repository (a single agent against a
   critic against a hierarchy, at equal budgets). Both hold when the kernel can represent and run ANY configuration — flat, critic, headed, deeper —
   as data, when structural growth passes an app-checked earning test (§ 2.9), and when the instrument that compares configurations is part of the
   kernel: the proving ground (§ 2.14) and the paired run are built with it, not after it. Representation is unbounded and the kernel is built whole; activation is earned.
3. **Authority needs no shell; code runs only in cells.** Every decision right — assign, accept, charter, decide — is exercised through the
   app's broker and never depends on a shell. Code, at any level, runs in a confined cell (§ 2.8). A head works broker-only by default and gets a
   cell when its mandate needs computation, as an executor does; nothing restricts how any agent reasons (PRINCIPLES, "Autonomy is the default
   inside authority"). The harness that holds a model login is not where model-directed code runs (the factory plan's Law 2). What blocked the
   organisation — containment — is needed exactly where code runs, and nowhere does an unconfined shell stand behind a decision right. The one
   exception is the legacy pair: they keep their existing two-role behaviour on unconfined seats and so hold no organisational decision right until
   they too are confined, and no unconfined seat runs beside an organisation seat (§ 2.3).
4. **Turns are capital.** On the owner's plan the organisation shares about six strong-model turns every five hours (ORGANISATION § 10). The
   scheduler is the kernel's economy: a turn is spent on a divergence between what is observed and what is wanted; code triages before a paid turn;
   a note wakes nobody; no eligible work is healthy idleness.
5. **Occupants are disposable; the institution is durable.** Every model session can be thrown away: identity, mandates, envelopes, decisions,
   beliefs and memory live in app records, and every turn starts from a packet built from them (VISION § 1, test 1; § 6.7). Attempt 3 of the
   observed run read 224 M input tokens in 36 turns, 216.6 M of them cached (`BUILD-STATUS.md`, 2026-10-07) — the packet is the cost lever as well
   as the memory, and its measure separates cached from uncached input.

## 1. Today, measured (main `fd7174ee`; two read-only surveys and one run, 2026-10-11)

| Area | What exists (SOURCE, under `src/`) | Verdict |
|---|---|---|
| Roles | `CouncilRoles.All = [operations, research]` (`Core/Council.cs:41`); an unknown role folds into the chair (`:53`) and into the chair's folder (`:80`); `MayPlaceOrders` = `operations` (`:63`) | a hard-coded pair; a third role would silently share the chair's home, meter slot, counters and codex session |
| Org ledger | `org_unit`, `org_position`, `org_event` seeded at rung 28 (`Core/Db/Database.cs:1684-1761`, `:2302-2351`); `org_position` has no kind and no seat version; `OrgStore` read-only; nothing in `src/` reads it but the ledger's author key | representable in part, inert |
| Loop | the mission loop dispatches serially — one `LoopAsync` awaits each `TurnAsync` (`AgentRuntime/MissionLoop.cs:1547-1583`); the owner's chat runs the chair beside it, outside the lease, with no Situation and no wake (`AgentSession.cs:324-349`; the meter allows that overlap, `TurnMeter.cs:503-507`) | serial dispatch, one overlap |
| Wakes | deduplicated `mission_event` rows consumed in the launch transaction (`Core/Db/AiAttemptStore.cs:256-281`); per-role objectives in code (`AgentRuntime/RoleObjectives.cs:77-138`); the quiet-review backoff (`MissionLoop.cs:2142-2173`); a LOST launch's owner words re-raised once (`:2544-2568`) | a sound primitive, keyed by role |
| Inbox attestation | a material pass runs only while no role holds its turn lease, and no lease is taken during a pass (`MissionLoop.cs:1350-1382`; COUNCIL rule 7) | an evidence protection any concurrency must keep |
| Prompt | the Situation: fixed-order text, no size cap, no ledger, no objectives (`MissionLoop.cs:690-795`); `U-reconcile-packet` in flight | a packet in progress |
| Runtimes | codex: `codex exec --json --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox -m <model>` (`RuntimeManifest.cs:586-592`, `:649`), resumed each turn, fresh every 20 (`MissionLoop.cs:1744-1748`); OpenCode; the API harness posts to chat-completions and has never made a real provider call | a CLI seat is one process with full host reach |
| Seat configuration | the app never sets `CODEX_HOME`, keeps an inherited one (`RuntimeManifest.cs:499-502`) and passes `HOME`/`USERPROFILE` (`AgentEnvironment.cs:78, 87`); no codex feature is disabled. A codex seat on the default home runs under the owner's personal codex configuration — on this Mac its plugins (computer use, Chrome, browser, GitHub, Hostinger), two MCP servers (`node_repl` and `computer-use`), a `shell_environment_policy` that sets
variables past the app's environment whitelist, a notify hook and a global AGENTS.md. RUN, 16:18 CEST: a `codex exec` (0.160.1) started under that home spawned, as its children, the ChatGPT app's computer-use MCP server (`node_repl`) and two plugin MCP servers. Which of their tools the model is offered in a seat's own launch is NOT VERIFIED (§ 2.7's inventory) | the owner's ambient authority within a seat's reach — a protection gap |
| Identity | a grant per launch through `TRADEAGENT_GRANT` and a check of the calling `trade`'s path and hash (`Gateway/GatewayPipeServer.cs:1068-1088`); the code itself says a same-user process can pass as the deployed `trade` (`Security/PeerImage.cs:101-108`) | sound against a confused agent; advisory against a determined unconfined one |
| Spend | one admission transaction: the day cap, then the role's share, 1/N of two (`AiAttemptStore.cs:256-281`; `Core/Trading.cs:311-314`); LOST rows keep their reservation — but an admission that cannot be written runs anyway: `TurnMeter.Begin` turns any exception into `AiAdmission.Unrecorded` (`TurnMeter.cs:874`), whose `Admitted` is true (`AiAttemptStore.cs:164-181`) | the best primitive in the agent half, with one fail-open path |
| Memory | canon v2 per role × runtime, generated reach, hash ledger (`AgentRuntime/Canon.cs`); PLAN 60 / JOURNAL 200 lines (`WorkspaceRevisions.cs`); the research ledger at rung 31, author → `org_position`, writers limited to the pair (`Core/Db/ResearchLedger.cs:484-488`) | the three layers, built for two roles |
| Relay | reports and agendas routed by role name (`AgentRuntime/CouncilRelay.cs:596-617`); boundary release needs every role in `All` (`Core/Db/BoundaryStore.cs:589-609`) | two-party |
| Containment | `Sandbox()` is NONE everywhere (`AgentRuntime/Containment.cs:43-45`); process trees bounded (a job object per turn on Windows; a `setsid` launcher on Unix); some setup failures fall back to a bare, unheld start (`Containment.cs:345-395`); `R-containment`'s AppContainer probe lives outside the solution | nothing confined |
| Delegation | no parent/child link between attempts | none |

**Verdict.** A well-protected two-role loop, not yet an organisation. Its strongest parts — spend reserved before launch, wakes as deduplicated rows
consumed in one transaction, a grant per launch, an attested inbox, a canon generated from what enforces it, beliefs as claims linked to measurements —
are exactly the primitives the kernel generalises. The work is to replace the role pair as the unit of everything by the position and the attempt,
and to add what an organisation has that a pair does not: mandates, a tree of envelopes, a scheduler with lanes, packets per position, confined cells,
the work verbs, an audit line and the instrument that tells whether any of it pays.

## 2. The kernel — sixteen components

```text
OWNER (in-process) — mission · ceilings · authority · veto · the Org page · the decision inbox
═════════════════════════════════════ ORGANISATION KERNEL (code) ═════════════════════════════════════
 2.1 CHART & IDENTITY   2.2 MANDATES      2.3 AUTHORITY       2.4 ENVELOPES       2.11 DECISIONS
 unit·position·seat·    one record from   grants, lattice,    one admission tx;   records·forecasts·
 occupant·attempt       charter to task   activation, fences  funding lineage     resolution·scores
        └─────────────── 2.5 SCHEDULER: wake → eligibility → priority → lane → admission ───────────────┐
                                  2.6 PACKETS: canon per seat · packet per attempt                       │
          2.7 HARNESS ADAPTERS (runtime profiles, seat homes) ──── 2.8 CELLS (execution environments)    │
                2.9 BROKER: pipe ops + org verbs, one authority implementation, one rights matrix        │
                2.10 RELAY: deliveries · notes · escalations · owner words · sealed boundaries           │
 2.12 TRUTH PER POSITION   2.13 AUDIT LINE & WATCHER (outside the chain)   2.14 PROVING GROUND           │
 2.15 THE OWNER'S SURFACE  2.16 EVOLUTION ◄─────────────────────────────────────────────────────────────┘
═════════════════════════════════ existing trusted boundaries, unchanged ════════════════════════════
 referee · holdout · gateway rules 1–4 · risk and loss · runner · operator controls · credentials · updates
```

### 2.1 Chart and identity — five layers
- **Unit** (`org_unit`): the root, divisions, earned sub-divisions, teams, the chief's staff; parent, head position, status, envelope share; its
  charter is its mandate (§ 2.2). Kinds are validated text a later build extends; depth has no constant (VISION § 3).
- **Position** (`org_position`): the durable identity — home, memory, ledger entries, decision records, scorecard. Its kind (head, member,
  staff) is derived from the chart — it heads a unit, sits on the chief's staff, or is a member — and never stored beside it; it gains a reference to its
  pinned seat version (a rung; no compatibility owed, `CLAUDE.md`). Status active, dormant or ended. An id that is not an
  active position is refused or quarantined with a reason, never folded into the chair.
- **Seat** (a versioned record): runtime profile × model class × effort × playbook version × allowance × cell class. A changed field is a new version;
  a seat moves only by an app rule or the owner (ORGANISATION § 8).
- **Occupant**: the model version actually in the seat, recorded per attempt as requested and effective model.
- **Attempt** (`ai_attempt`): one billable launch, carrying position, mandate, seat version, runtime-profile hash, cell id, funding chain and — for
  helpers — its parent attempt. Every per-launch state in the meter, the grant register and the relay is keyed by the attempt, never by the role.
- **Rules:** only `OrgStore` writes `org_*`; ids are app-minted and never equal a legacy id or an app principal (`referee`, `allocator`,
  `perception`, which are never positions); a position grants no system privilege (ORG-001); order permission is bound to `operations` and never
  follows headship (ORDER-001). The legacy pair are positions `operations` and `research` (homes `agent` and `research` kept); every later position
  lives under `org/<id>`, the id opaque and app-minted — never a name an agent chose, never a reserved device name on Windows — and never read back
  as a path from a row.

### 2.2 Mandates — one record from charter to task
VISION § 6.1's mandate is the kernel's unit of work and of governance: parent, decider (one position), holder, kind (mission, charter, family,
task, exploration, service), objective and thesis (claims), scope, inputs by content hash, output shape (tasks), capability subset, allocations per
currency, tranches, deadline, checks (the type's app-set minimum plus the issuer's additions, run by the app), latitude or the **exact** mark,
forecasts, kill rules, retry policy, status. A transition is an append-only event (`org_mandate_event`) with its actor, attempt, reason and evidence ids.
- A mandate exists only where something crosses between positions — responsibility, resources, an external effect or an evaluation. Inside one, an
  agent's sub-plans, branches, tools and scratch are its own working files (PRINCIPLES, "Autonomy is the default inside authority").
- **Leaf mandates** are tasks or explorations. The executor answers the question or says why not and names every deviation and its cost; a finding
  beyond the team's mandate becomes a proposal to its head (ORGANISATION § 4).
- **`operations` stays the owner's** (ORGANISATION § 3): no mandate to it, or to a position working on its behalf, names an instrument, side, size or
  timing — refused at `assign` by the app's validation; an `operations` attempt launched for another position's mandate carries no order permission
  in its effective grant.
- **A delivery is sealed before it is judged:** the app copies its artifacts into its own content-addressed store and writes an immutable manifest of
  their hashes first (the material scanner walks every position's home); the checks run on that
  revision, inside a bounded environment and never with the app's authority; acceptance binds to the checked revision, and a later edit is a new
  delivery.
- **Failures are remembered:** the mandate carries its retry policy (two fresh failures by default, ORGANISATION § 4), its failure count and its
  accumulated cost; every failed check is kept with its checker and reason; at the threshold — or at half the allocation spent with no accepted
  milestone, or on an ambiguity the executor declares — the app stops automatic retries and raises the head's escalation in one transaction.
  Repeated identical or infrastructure failures raise a review of the check itself, not of the executors.
- **Service mandates** (ORGANISATION § 4, "Between units"): a division may serve another's request. Its funding chain is the requester's, its
  execution chain the server's; the server accepts or declines with a reason; no authority crosses.

### 2.3 Authority — grants, the lattice, activation and fences
- **The grant per attempt** (today's `AgentGrant`, extended) names the position, mandate, seat, effective capabilities, information scope, expiry
  and fencing generation. It is **bound to the attempt's process container**: the broker accepts it only from a peer the app placed in that
  attempt's container — a job object on Windows (named-pipe client pid, `IsProcessInJob` on the retained handle), a per-attempt cgroup on Linux
  (descendants cannot leave it, whatever session they create — MEDIATED until the cell cannot write the cgroup filesystem; systemd user delegation
  and the host's user-namespace policy are prerequisites `U-cell-linux` checks), the launcher's session and process group on macOS (weaker; said so) — after a held
  launch handshake, with process start time against pid reuse and a refusal on any lookup error. API-harness seats are authenticated in-process.
  **What it stops:** a token read from another seat's environment and replayed from outside that container. **What it does not stop:** a
  determined same-user process that passes or duplicates an authenticated connection, or acts through a process inside the other container —
  only containment and process-access restrictions stop those (§ 2.8). Concurrency waits for it (§ 2.5).
- **`operations` never holds order permission beside another unconfined attempt** (until cells): an `operations` attempt that overlaps one — the
  owner's lane included — is admitted without it (it answers, it does not trade, and says so), and no other unconfined launch starts while
  `operations` holds it. The grant binding cannot close that window (it does not stop a determined same-user process); this rule does.
- **Effective capability** = requested ∩ the parent's delegable ∩ product policy ∩ what the seat's runtime and cell support — computed and
  recorded by the app, never supplied by a model (the factory plan's § 13.2).
- **The lattice, v1** (VISION § 6.2): dimensions — verbs and ops, targets (the caller's own subtree for a head, minus the reserved identities of
  § 2.9), information scopes, each currency, time, cell class, delegation depth; each defines contains and intersect; a comparison that cannot be
  decided is "not contained"; grants never combine into a union. **Never delegable:** operator controls, credentials, the holdout, ceilings, live
  activation, updates, evidence policy and global evidence budgets, `operations`' order permission. **Delegable:** bounded trial and verdict
  allowances, carved from the parent's, like AI spend.
- **The activation gate — the owner's 2026-10-11 answer (nothing runs until executors are confined), with its decision-verb minimum decided on his
  behalf, as code.** Each CAPABILITY has a minimum seat assurance, checked when a position is
  activated and again, fail-closed, immediately before model work on every adapter path, against the pinned profile and the boundary actually
  established: decision verbs need a broker-only or a confined seat, never an unconfined one; code execution needs a confined cell; helpers inherit
  their parent's minimum. The legacy pair may keep running unconfined for their existing two-role behaviour only — any capability new to the
  organisation (its verbs, assignments to them, helpers) is refused on an unconfined seat. So until they are confined the legacy pair hold no organisational decision
  right, and the chief — before it exists, the owner — heads their divisions' organisational work. **No unconfined neighbour:** on a host where any
  position other than the legacy pair is active, the legacy pair run confined too, or not there; every class below holds only where no unconfined
  seat shares the host and user (a neighbour's shell can rewrite `state/`, `runtimes.json` and any seat's home, and read any seat's environment). Assurance-required work never falls back to a degraded
  start: today's bare-start fallbacks (§ 1, Containment) refuse instead, for such work. **And literally, as he answered:** until the host offers a
  PROVEN executor cell class, no position other than the legacy pair activates on ANY real-model seat — a broker-only head included, though it
  would pass the capability minimum (the thinking-seats survey, 2026-10-11); puppet seats, which run no model, are exempt (§ 7).
- **Fences.** Every authority-changing transition — a subtree put to sleep, a mandate cancelled, a position retired, a seat replaced, a veto — bumps
  the generation of every grant it touches; the broker checks the generation on every operation; a running attempt under a stale generation is
  ended with its process tree, and its outputs are quarantined at completion, never published. Fences and the circuit breaker act on the
  organisation's AI only: they never touch trading (pause, stop and flatten stay the owner's), and anything about `operations` stays his presses. The
  breaker trips on code detectors — a fabrication finding, spend without milestones, runaway retries (VISION § 7.7). Policy data — lanes, ceilings,
  retry and timescale defaults, the rights matrix — is changed by the owner in the app; the organisation can only propose.

### 2.4 Envelopes and admission
- `AiAttemptStore.Begin` stays the one admission transaction and becomes **fail-closed**: a launch whose admission cannot be written is refused in
  words, never run unrecorded (`U-admission-fail-closed`). Inside it: the day-total gate first and unchanged; then, for the attempt's funding chain,
  every ancestor's subtree spent + unresolved reservations + this reservation ≤ that unit's envelope, read inside the transaction so two siblings
  cannot both take a parent's last room; then the mandate's own allocation. Unknown is never zero; a LOST attempt keeps its reservation.
- **Currencies:** AI spend in two billing classes (plan capacity — a list-price equivalent the owner sets — apart from the real-money cap,
  `U-billing-classes`), trials, verdicts, paper slots, data, owner attention. V1 carries AI spend, trials and verdicts.
- **Four quantities per currency:** an **allocation** (a mandate's earmark from its unit), a **reservation** (an attempt's or a broker call's hold),
  a **debit** (incurred) and a **release**. A launch draws its reservation from its mandate's allocation, so nothing is counted twice; trials and
  verdicts are reserved atomically at the broker call that spends them (`backtest`, `verdict`), not at launch.
- **Funding lineage is immutable:** an attempt is charged to the chain recorded at its admission; a later move, merge or close carries debts and
  history and never re-parents a past charge.
- **Two enforcement classes, never merged:** admission is hard (refused before launch); incurred cost is bounded only where a total-turn bound —
  helpers and the vendor's own subagents included — is demonstrated for that runtime; elsewhere the reservation is the bound, and an overrun is
  recorded and charged, never hidden.
- **Periods and units:** AI spend is counted per local day (the owner's daily cap) and per quota-pool window; trials and verdicts per campaign; a
  mandate's allocation over its life; `envelope_share` is a fraction of the parent's envelope in the same currency and period, never an amount.
- When a currency binds, the app shows its shadow price: a manager weighs a price, never an argument (VISION § 7.2).

### 2.5 The scheduler — the turn allocator
- **Wakes** are `mission_event` rows addressed to a position, with a cause class, a priority class, an entity and a deduplication key; raised only
  by app paths; consumed inside the launch transaction (as today).
- **Eligibility:** the position active; not leased (one conversation per position at a time); its unit not asleep (the circuit breaker, VISION § 7.7);
  envelope room; its quota pool not held; its cell available; the activation gate passes for every capability the launch needs (§ 2.3 — a refused position's due wakes settle with a
  recorded disposition and never retry); the owner's pause and stop obeyed.
- **Priority:** ORGANISATION § 10's order, read from there and never restated; oldest due first within a class; ties by unit depth, then id.
- **Objectives per position kind** (extending `RoleObjectives`): a wake is a divergence between the observed and the desired world. A head owns its
  deliveries to judge, escalations, mandates past their deadline, kill rules triggered and the owner's words addressed to it; an executor owns its
  open mandate and its own requested wakes, never an app timer or heartbeat. **Timescale ceilings** (VISION § 7.5) are policy data — a maximum interval per level, a floor and
  never a heartbeat.
- **Code triages before a paid turn.** A delivery's checks run first, on its sealed manifest. One that fails them and names no deviation goes back
  to a fresh executor attempt under the head's own retry policy, remembered on the mandate (§ 2.2), without waking the head — code applies the head's
  policy; it does not judge. One that names a deviation or an off-path finding always reaches the head. The head wakes on a pass, an escalation, a
  deviation or a review event. A lateral note wakes nobody. The liaison answers status from app facts (ORGANISATION § 2).
- **Lanes.** The loop dispatches up to N eligible attempts — one conversation lease per position; a helper is a child attempt admitted under its
  parent's mandate, not a second lease. Limits are keyed by **quota pool** (provider, account, plan window — shared by every seat on it and by the
  owner's own use outside the app, whose consumption is unknown) and by cell class, as policy data. N stays 1 by default until grants are bound to
  containers (§ 2.3) and cells exist. **Inbox attestation is kept:** a material pass still needs every UNCONFINED seat quiet, so with N > 1 the
  scheduler drains to a quiet window for each pass; a confined seat whose cell cannot write the inbox does not count, and outputs are sealed before
  any pass reads them. Expensive work — checks, hashing, scans — runs outside the store's single write lock on immutable inputs; commits stay short
  and revalidate what they rest on. The app's sweeps (boundary deadlines, paper allocation and deployments) run once per tick, never once per lane.
- **The owner's lane.** The owner's words get an immediate receipt and a visible place in the queue. An idle addressed position answers at once in a
  lane reserved for the owner, outside N, as the chair does today (an `operations` turn there carries no order permission beside another
  unconfined attempt, § 2.3); a busy one takes them first in its next packet, and the owner sees why it waits.
  In-process emergency controls never wait for any queue.
- **Healthy idleness:** no eligible wake, no paid turn. Budget exhaustion stops paid work; app protection and reconciliation never wait for it.

### 2.6 Packets — what every turn starts from
- **The canon per seat template** (position kind × runtime × cell class): identity, the mission, authority generated from the
  verbs the position holds (what it decides, what it never decides — ORGANISATION § 2), the evidence rules, the boundary, and its reach generated
  from what enforces it — `U-canon`'s mechanism, its version ledger and binding test carried to every template.
- **The packet per attempt** (`U-reconcile-packet`'s, extended to positions): the owner's words first when addressed to it; why it is awake; the
  unit's charter, quoted as data; the mandate (objective, scope, checks, latitude or exact, deadline, allocation left per currency); what changed since its cursor; what it believes (its
  open ledger entries, bounded); what is wanted (its open objectives); deliveries and lateral notes as data, by reference, sender-stamped; references
  (artifact ids and how to fetch deeper); state lines. Size budgets per seat class — about 2 k tokens for an executor (ORGANISATION § 6) — measured
  per attempt, cached and uncached input apart.
- **Input lineage:** each attempt records what it was given and what it retrieved — artifact and measurement revisions — so attribution, the
  contribution ledger and paired runs can be reconstructed (VISION § 7.2). A position's cursor advances only with the attempt's committed outcome.
- **Session policy per seat class:** an executor starts fresh per mandate; a head resumes within a rotation bound (turns or tokens), then starts fresh
  from its position memory; nothing an occupant needs lives only in a transcript.

### 2.7 Harness adapters, runtime profiles and seat homes
- **One adapter contract** behind every seat: start or continue a session; send a packet; stream events (usage, tool calls, a final output with an
  optional schema); cancel; report the effective model; declare native delegation. Adapters: codex exec (today's); the API harness (topology A,
  which needs `U-harness-responses` for tools); a **puppet** adapter that plays scripted seats, for tests (§ 7); others by an owner press.
- **Runtime profiles are versioned data, probed** (the factory plan's § 7): binary version, arguments, the seat home, the features allowed
  (everything else disabled), the MCP servers (the app's only), sandbox mode, output-schema support — each field unknown, advertised, observed or
  trusted-for-a-property. A runtime update invalidates the observed fields it can change; a profile this build does not recognise is refused with
  both versions named (`CLAUDE.md`, "Refuse, never guess").
- **A seat never runs under the owner's own agent configuration.** The app keeps ONE app-owned **seat home** (for codex, its own `CODEX_HOME`, its
  credentials pinned to file storage) and ONE sign-in, and applies each seat class's default-deny configuration at launch — no plugins, no MCP server
  but the app's, no notify hook, no memories, computer and browser use off; the runtime binary is pinned by version and hash, never a moving latest.
  The home is part of the profile and every runtime operation uses it: detection, sign-in (in the app; on Linux the device code shown in the app's
  own window, with expiry and retry — `U-linux-host`'s path), status, turns, resume, usage-limit reads, diagnostics and the observed-run audit
  (whose session store moves with it); the move is a deliberate fresh start, never a copy of the owner's login or sessions. A clean home is not yet
  an allowlist: the app **verifies the effective tool and configuration surface at every launch**, fresh and resumed, keeps project-level
  configuration out of every seat's workspace, and labels the configuration's integrity ADVISORY while ANY unconfined seat shares the host and user —
  its own shell or a neighbour's can rewrite the home, `state/` and `runtimes.json` (§ 2.3's no-neighbour rule ends that where the organisation runs).
- **A seat's prompt travels on stdin, never in its argv** (`U-prompt-stdin`): today the whole packet rides in the command line
  (`AgentSession.cs:155, 162`; stdin closed at `:580`), where any process of the host can read it — a confined cell included, since
  `/proc/<pid>/cmdline` is outside Landlock's reach — and where it is capped (128 KiB per argument on Linux, 32,767 characters per command line on
  Windows). A test proves a cell cannot read another attempt's prompt.
- **The broker's shell-free door:** a broker-only seat reaches the broker without a shell — through the app's own MCP server (or the API harness's
  tools), under the attempt's grant, serving the organisation's verbs, artifact reads by id and bounded data queries (`U-thinking-seats`). A **broker-only codex seat** is shown
  viable and MEDIATED (`docs/research/2026-10-11/R-exec-server.md`, the dev Mac, a mock model): with `CODEX_EXEC_SERVER_URL=none`, the host-reaching
  features off, `agents.enabled=false` and the app's tools as a loopback HTTP MCP server, no host-reaching tool is offered and 17 calls to tools not
  offered were refused on both tool modes; flags alone are not enough (with a local environment present, `apply_patch` wrote a host file); the
  offered surface is verified at every launch, since a runtime update or the account's default model changes it. It holds only on a model with
  classic tools: a code-mode-only model (gpt-6-astra) keeps its code-mode `exec` even with the code-mode host disabled, so model-written code
  would run inside the process that holds the login (Law 2, § 2.8) — a thinking seat pins a classic-tools model and refuses any offered surface
  containing `exec`; codex lists no offered tools itself, so the surface is read from a cached dry run against a loopback mock, keyed by binary,
  argument template and model (the thinking-seats survey, 2026-10-11). Thinking seats therefore need neither `U-harness-responses` nor an API
  key. **Seat classes live in code:** a class's pin and deny set are never read from `runtimes.json`, which replaces a manifest whole and stays
  the vendor-commands data file (`CLAUDE.md`).
- **Native delegation** (the factory plan's level A) only where the profile says so, inside one attempt and its envelope, charged to it, with no
  rows; durable delegation is a mandate (§ 2.2).
- **Usage:** what the provider reports; otherwise the reservation stands.

### 2.8 Cells — execution environments
- **A cell** is a workspace (the position's home and the mandate's scratch), a process container, file grants (read-write its own; read-only shared
  inputs; nothing of `state/`, the credentials, the inbox or the holdout), a network and IPC policy, a lifetime and resource ceilings — and an
  **assurance profile** naming which properties were PROVEN on which build and platform, each with its enforcement class (the factory plan's Law 9;
  VISION § 6.3). A confined seat keeps its working freedom: shell, code, packages, permitted network, the runtime's own helpers (R20).
- **Providers:** none (today, ADVISORY, labelled "unconfined"); **Linux first** (the owner's answer): the app's own launcher — no_new_privs, then seccomp
  (`socket()` only for TCP over IPv4 or IPv6, `io_uring` denied, `socketpair()` kept), then Landlock (ABI 6 or newer, refused below), on one
  thread, then `execve` — and a per-attempt cgroup in a subtree the app's systemd service delegates; no namespaces (Ubuntu refuses unprivileged
  user namespaces) — proved on CI's ubuntu runners, then on the Linux host (`docs/queue/U-cell-linux-a.md`; parts a–e in ORGANISATION § 15); Windows (`R-containment`'s AppContainer route, on the
  box); macOS only for development, if cheap.
- **The split** (Law 2): the harness that holds the login stays outside the cell; model-directed shell, file, package and network actions run
  inside. Candidate routes: (a) codex 0.160.1's experimental `exec-server` inside the cell, the harness outside — shown viable on the dev Mac
  against a mock model (`R-exec-server`): every host-reaching model-directed action ran in the exec-server and the login never crossed its
  transport, on four conditions — a scrubbed `env -i` stdio launch (or `--ws-auth` on a WebSocket listener), the workspace at the same path on both
  sides, MCP servers placed in the cell without the harness's `env_vars` and the cell's own `mcp_servers` answer ignored, and the cell's codex home
  empty and unwritable by its model processes; Linux not yet run (`U-cell-linux` re-runs the kit); (b) the whole CLI inside the cell, acceptable ONLY with a provider-supported workload
  credential scoped below the owner's account — a copy of the owner's login inside a cell is exposure, revocable or not; (c) the API harness with an
  app-run executor in the cell.
- **Information flow:** a cell with permitted network or IPC can talk to another outside the relay through any service both may reach; side-channel
  claims are limited to the cell's tested network, IPC and file policy, and inter-cell communication is part of the benchmark.
- **Acceptance:** each provider passes the factory plan's § 9.3 containment benchmark (workspace, read-only, `state/`, user secrets, reparse
  escapes, descendants, persistence, host processes, IPC, the broker, grant expiry, network, packages, cancellation, restart, credentials, no
  terminal), plus session changes inside a container and host-to-cell pid translation for the broker.

### 2.9 The broker, the verbs and the rights matrix
One authority implementation (Law 8): the organisation's verbs join the gateway's pipe ops under the same grant, and domain handlers still validate.
- **Every position:** `org show` (its own view — unit, chain, mandates, envelope; never the watcher or the audit line), its inbox of deliveries and
  notes, the ledger (built), `decide` and `forecast` (§ 2.11).
- **Heads:** `assign` (a leaf mandate into its own subtree), `accept` or `reject` a delivery (a rejection names a failed check or a stated defect),
  `rescope`, `cancel`, `escalate`; service requests to another division's head.
- **Unit heads:** `charter`, `close`, `merge`, `hire`, `retire`, `tilt` — each a decision record with a forecast, inside the throttles (a unit lives
  14 days; two structural changes a week; a tilt moves a share by at most 20 points a week, ORGANISATION § 8), shown on the owner's chart and vetoable (ORGANISATION § 3).
- **Executors:** `deliver` (a sealed manifest, a report, deviations named), `decline` with a reason, `propose`, `note` (lateral, inside its
  information grant); helpers as child attempts inside its own allocation and capability subset.
- **One rights matrix** (actor × target × operation, data the app enforces): reserved identities — anything about `operations` is the owner's two
  presses; the legacy `research` is never retired by an agent; a division head is replaced only by an app trigger or the owner, the chief proposing;
  the chief's own appointment and replacement are the owner's; appointment and replacement are different operations.
- **The earning test** (VISION § 3), app-checked at `charter`, `hire` and any new layer: the record cites evidence of current work — records of the kinds VISION § 3
  names (a tape series with features, a family with candidates, a recurring service load, a control finding), checked by the app for kind, recency
  and bearing on the unit chartered, not only for existence — or a funded, bounded experiment, and states its collapse condition; without one the verb is refused.
- **Restructuring is a transaction:** a close, merge, retire or veto disposes every open mandate (re-assigned or cancelled with a reason), fences the
  grants it touches (§ 2.3), carries reservations, debts and history to the successor, and keeps funding lineage; a veto restores structure, never
  work already done.
- **Every verb:** grant-checked, lattice-checked, throttled where structural, written as an `org_event` with app-composed detail; none reaches
  operator authority (AUTH-001); orders stay `operations`' (ORDER-001).

### 2.10 The relay — how information moves
- A **delivery** goes to the mandate's issuer, chosen by the app, verbatim and by reference (about 20 lines by default — `CouncilRelay`'s caps,
  generalised).
- A **lateral note** is app-relayed into the recipient's next packet, sender-stamped and logged; it wakes nobody and carries no authority.
- An **escalation** goes up the chain (ORGANISATION § 4).
- The **owner's words** go verbatim, stamped `owner`, to the chair until the chief exists, then to the chief or the liaison and any unit he names;
  undispositioned after two hours, or at once while the chief is held at a vendor limit, also to every division head — one record, deduplicated.
- A **boundary** freezes, when it opens, its exact assessor ids, what each may see, its deadline, its default and its decision owner; sealed
  assessments are released when that set is complete; a departed or failed assessor has an explicit disposition; one challenge; code applies.
- Untrusted material is read only by readers that can change nothing (INBOX-001).

### 2.11 Decisions, forecasts and scoring
`org_decision` holds the options, the choice, the rationale (a claim), the evidence ids (checked by the app to exist) and its forecasts;
`org_forecast` holds a predicate from the closed, versioned vocabulary, a probability and a resolution date or event. The resolver's contract carries
ORGANISATION § 5 whole: a predicate is scored only where neither the forecaster nor its subtree controls the outcome; a backtest predicate only
before any trial of that hash or its fingerprint cluster, and only from a seat that cannot backtest privately; no predicate resolves on a holdout or
E2–E4 value beyond the verdict class agents already see. Scores run against the base rate; scorecards are computed at read. The rationale and the
lessons go to the research ledger as the position's own claims, linked.

### 2.12 Truth per position
The three layers (VISION § 6.7), per position: the canon per seat; the packet per attempt; the sources of truth — the research ledger with its author
the position (rung 31 already keys it to `org_position`), position memory (charter, playbook, decision log, open mandates) that outlives every
occupant, and the input lineage of § 2.6. A position reads its own entries and what its information grant reaches.

### 2.13 Accountability — the audit line and the watcher
- **The audit line** (ORGANISATION §§ 2, 5; VISION § 5.3): app-owned, outside the chief's subtree, its own owner-set budget beside the
  organisation's; it reads approved evidence, never hidden reasoning; it samples deliveries, runs the seeded canaries and impossible assignments and
  keeps each catch rate, and writes the adversarial brief a decider receives at a consequential boundary. Its findings go to the app's ledger, the
  owner and, about a unit, that unit's parent head. It can neither instruct nor block; app rules block.
- **The watcher — the owner's private line** (ORGANISATION § 9): outside every `org_*` table and every agent-reachable total, its own tables, layer-1
  detectors scoped per unit from the start (VISION § 5.2), a digest composed by code, an owner-only page, an oversight cap beside the organisation's; a
  model layer only on its own key (the owner, 2026-10-02). Its invisibility is hard on app paths and advisory on unconfined seats, and says so.

### 2.14 The proving ground — the organisation's instrument
Synthetic markets in the tape's formats (pure noise, planted edges of known size inside and beside the doctrine, traps); a shadow home with its own
state (SIM-001: no evidence path, belief or genome-bank row reads it); a configuration — chart template, seats and policies — works a brief under a
budget and is scored in hours: planted edges found, false discoveries on noise, AI dollars, calibration, cost discipline, and the off-path exam
(VISION § 6.4). Paired configurations start from one snapshot of memory. Its first ladder answers the 2026-10-09 outside review's question — does
added intelligence pay: a deterministic search with no model as the floor, one agent, an agent with a critic, a headed team — at equal budgets on the
same markets. It is built with the kernel because it is how a layer earns its place: flat against headed is a pair of configurations, not a debate. It runs as a separate app process with its own home, pipe and registers, never inside the real one; real models run in it only on confined
seats, or SIM-001 is advisory there. Its plumbing is proved with puppet seats at no cost; a run with real models is a paid run and needs the owner's
word, like any other.

### 2.15 The owner's surface
The Org page appears when positions govern — a chart that governs nothing is a sophisticated-looking lie (the owner's interface architecture of
2026-10-06). It shows the chart; each position's seat, occupant and cell class in plain words ("unconfined", "broker-only", "confined on Linux");
current attempts and why they woke; mandates, envelopes and spend; decisions awaiting his veto. His controls stay in-process: appointing and
replacing the chief (two presses), raising an envelope (two presses), vetoing a structural change, putting a unit to sleep, everything about
`operations`. A decision inbox with an attention budget (VISION § 8). The watcher's page is his alone.

### 2.16 Evolution
Seat shadowing under a minted principal, exams in the proving ground, unit templates as the organisation's genome, cut-rule review events, the
collapse test read from decision timestamps, and shared playbooks, tools and templates changed only through VISION § 6.6's governed change
(proving ground, shadow or canary, promotion or rollback) — on the records the earlier components keep from their first day.

## 3. The data, at a glance
Each table lands with its unit, on the next free rung, in landing order; none is designed for an older build (`CLAUDE.md`).
- **Kept:** `org_unit`, `org_event` (rung 28); `ledger_*` (rung 31); `tool_call`.
- **Changed:** `org_position` gains its seat-version reference (its kind is derived, § 2.1); `mission_event` gains the position (exact id, never folded);
  `ai_attempt` gains the position, mandate, seat version, runtime-profile hash, cell id, funding chain and parent attempt, and its input lineage.
- **New:** versioned seat and runtime-profile records; `org_mandate` and `org_mandate_event` (with sealed delivery manifests and failure records);
  `org_decision` and `org_forecast`; relay rows for notes; the audit line's and the watcher's own tables, outside `org_*`; the proving ground's store
  in its own file.
- **Never:** a table an agent writes directly; a copy of an app record inside a claim; a column kept for an older build.

## 4. Flows
1. **A wake becomes a turn:** an app path writes a wake for a position → the scheduler finds it due, the position eligible and a lane free → the
   attempt id is minted → the packet is composed → the lease is taken → admission reserves along the funding chain and consumes the wakes in one
   transaction, or refuses → the cell is established and the activation gate checked against it → the grant is minted, bound to the attempt's
   container → the harness runs → usage is metered → one short commit closes the attempt, publishes deliveries, records revisions and dispositions,
   advances the cursor and raises the recipients' wakes.
2. **An assignment's life:** a head's `assign` → validation (the rights matrix, the lattice, `operations`' limits), the allocation reserved, the
   mandate minted, a wake to the executor → the executor works in its cell, fresh from its packet → `deliver` seals a manifest → the app runs the
   checks on it → a failure: a fresh retry under the remembered policy, or the escalation at its threshold; a pass: the head wakes → `accept` or
   `reject` with a cited check → its forecasts resolve when their measurements arrive.
3. **A restart mid-assignment:** LAUNCHED attempts become LOST with their reservations kept; leases clear; wakes those attempts consumed are NOT due
   again by themselves — a reconciliation pass re-raises each owed item once, idempotently (owed deliveries to judge, the mandate's open work, the
   owner's words as today), reconciles staged outputs against their manifests and keeps every uncertainty about an external effect; the next attempt
   starts fresh from its packet. Nothing recorded as done is redone.
4. **A usage stop:** the quota pool is held to the stated minute; due wakes stay due; app protection and reconciliation continue.
5. **The owner's words:** recorded with receipt → the owner's lane or first in the addressed position's next packet → dispositioned by the turn's
   measured outcome; nothing an agent writes marks them handled (`U-owner-words-delegated`).

## 5. Seat types — what each property's enforcement class is, or is meant to become

| Property | Legacy pair, CLI, unconfined (today → after the named unit) | Thinking seat, API harness (TARGET) | Thinking seat, codex broker-only (TARGET; MEDIATED on a classic-tools model — `R-exec-server`; a code-mode-only model refused) | Doing seat, confined cell (TARGET, per provider) |
|---|---|---|---|---|
| Decision rights at the verbs | refused: no org verb runs on an unconfined seat (§ 2.3) | hard (brokered) | mediated | hard |
| AI admission | hard, once `U-admission-fail-closed` lands | hard | hard | hard |
| AI incurred cost | the reservation; its own shell's runs unmetered | bounded per request; the turn bound shown, not assumed | the reservation | the reservation, unless a total-turn bound is shown |
| Order permission | pipe-enforced; replay from another container refused once `U-grant-tree` lands; same-user tricks advisory | hard | mediated | hard against the cell's processes |
| The owner's provider login | readable | never exposed | held by the codex process, offered to no model tool | outside the cell (route a or c) |
| `state/`, the holdout, the ledgers | advisory | hard on app paths | mediated | hard |
| Side channels | shared files, undetectable | app-relayed only | app-relayed only | as the cell's tested network, IPC and file policy allows |
| The watcher's invisibility | advisory | hard on app paths | mediated | hard |

"Hard" means enforced by app code or the operating system on a stated build and platform; "mediated" means no model-reachable tool crosses it but the
process itself could; "advisory" means asked, not enforced. A TARGET becomes a class only when the unit that builds it shows it on its build, platform
and probe. Every TARGET holds only where no unconfined seat shares the host and user; beside one it is advisory (§ 2.3's no-neighbour rule). Every
organisation figure names its seats' classes until every seat is hard (ORGANISATION § 14).

## 6. Running an organisation on a fixed plan
- On the plan the organisation shares about six strong-model turns every five hours; priority, triage and timescale ceilings decide who gets them,
  and cheaper seats do bounded work (ORGANISATION § 10). A seat names a model CLASS; the runtime profile maps it to what the owner's accounts run.
- The packet is the cost lever: a fresh executor session from a bounded packet against a resumed transcript, measured cached and uncached apart.
- Lanes raise throughput only inside a quota pool's limits, which the owner's own use outside the app shares; their number is policy data, measured.
- Funding beyond the plan is the owner's decision with M-org1's measured cost and output (ORGANISATION § 16).

## 7. Testing the organisation without paying for it
- **Puppet seats:** a harness adapter that plays scripted positions through the real kernel — the whole life of a charter, an assignment, a sealed
  delivery, a failed check and its retry, an escalation, a veto mid-work, a lateral note reaching a packet, a seeded fabrication the audit line
  catches, a crash and restart mid-assignment, envelope exhaustion, a fenced grant, lanes under load with a material pass — in CI, at no AI cost.
- **A conformance suite for the verbs,** as `CanonTests` binds the canon: every cell of the rights matrix, the lattice and every throttle asserted
  for every position kind and seat class.
- **Paid runs** — M-org0, M-org1, the proving ground's real configurations — only on the owner's word, each a milestone claim under
  `BUILD-STATUS.md`'s honesty rule.

## 8. Protected properties this design touches, and what keeps each
| Property | Kept by |
|---|---|
| Operator authority in-process (AUTH-001) | no verb, packet field or note reaches mode, the kill switch, live activation, approvals, updates or credentials; seat homes remove the owner's computer-use and browser plugins, which could press his controls through the screen |
| Orders (ORDER-001) | `MayPlaceOrders` unchanged; never by headship; no mandate names instrument, side, size or timing to `operations`; an `operations` attempt for another position carries no order permission; until cells, `operations` holds no order permission beside another unconfined attempt; grants bound to containers refuse cross-container replay |
| The four adapter rules (ORDER-002…005) | the connectors and the gateway, untouched; seat homes and cells remove a model-native way to drive a user interface |
| UNKNOWN-001 | the gateway's reconciliation, untouched; restart reconciliation keeps every uncertain external effect |
| Capital | live authority, allocations and two presses unchanged; no verb, mandate or currency carries capital (the factory plan's Law 5) |
| Pause, stop, flatten | unchanged and in-process; the organisation's circuit breaker is separate from the trading pause |
| Spend (BUDGET-001) | one admission transaction, fail-closed; the day-total gate first; every ancestor of the funding chain inside it; LOST keeps its reservation; the perception budget, the audit line's and the watcher's caps beside the organisation's |
| Evidence (EVID-001, SIM-001) | referee, holdout and ledgers app-owned; trial and verdict keys carry no role; forecasts never resolve on held figures; the proving ground's store read by no evidence path |
| Inbox attestation (COUNCIL rule 7) | quiescence kept for unconfined writers; outputs sealed before a pass |
| Measurement versus claim | objectives, rationales, reports and forecasts are claims; checks, resolutions, scores and costs are app records; a claim references a record, never copies it (R22) |
| Inbox and messages as data (INBOX-001) | notes, deliveries and owner words are data in a packet; an approval inside text is void |
| Credentials (CRED-001) | no seat runs under the owner's agent configuration; the seat home's own login is held by a codex thinking seat's process and offered to no model tool; cells keep his files and login out; the unconfined ceiling of three stands |
| SIGNAL-001 | no verb, packet or seat on the signal path; what trades is the frozen, measured computation |
| ORG-001, CHANGE-001 | a position grants no system privilege; kernel changes are proposals to the owner, never made by the organisation |
| Armed-live refusal | an unconfined runtime is refused when live is armed (`AgentRuntime/Containment.cs:60-69`) — kept, and applied to every seat class: while live is armed every seat without a cell, broker-only codex managers included, is refused, so the organisation stops unless its managers run on the API harness or in cells — said so on the Safety page |
| Refuse, never guess; recovery | an unrecognised profile, seat version or home is refused with both versions named; an older home is never rewritten — starting fresh is the owner's press (`CLAUDE.md`) |
| No terminal | seat-home sign-in, the Org page, the veto, the watcher's page — all in the app |
| Honesty | every enforcement claim states its class per seat type; targets stay targets until measured; simulated stays labelled |

## 9. What it will not become
A loop with more role names · an organisation that lives in prompts · a seat that inherits the owner's agent configuration · a decision right that
rests on an unconfined shell · concurrency that lets one seat borrow another's grant or blinds the inbox's attestation · a launch nobody recorded · a
chart on the owner's screen that governs nothing · depth because it is cheap · a paid turn on a timer · a workflow engine that prescribes how an agent
thinks · a proving-ground score read as market truth.

## 10. Components and the units that build them (their order and status: ORGANISATION § 15 only)

| Component | Units |
|---|---|
| 2.1 Chart and identity | `U-org-principals` (O2a), `U-org-seats` |
| 2.2 Mandates | `U-org-assignments` (O5) |
| 2.3 Authority | `U-org-rights` (O2b), `U-grant-tree`, `U-thinking-seats` (the activation gate), `U-org-verbs` (O6: the lattice, fences) |
| 2.4 Envelopes | `U-admission-fail-closed`, `U-org-envelopes` (O4), `U-billing-classes` |
| 2.5 Scheduler | `U-org-wakes` (O3), `U-org-lanes` (O9) |
| 2.6 Packets | `U-reconcile-packet`, `U-org-packets` |
| 2.7 Runtimes and seat homes | `U-seat-home`, `R-exec-server`, `U-prompt-stdin`, `U-thinking-seats` (the broker's shell-free door), `U-harness-responses`, `U-puppet-runtime` |
| 2.8 Cells | `U-cell-linux` a–e (`U-contain-seats`' Linux provider), `U-execution-environment`, `U-cell-windows` |
| 2.9 Broker and verbs | `U-org-assignments`, `U-org-verbs` |
| 2.10 Relay | `U-org-assignments` (deliveries), `U-org-chief` (the owner's words), `U-org-verbs` (boundaries' assessor sets) |
| 2.11 Decisions | `U-org-assignments`, `U-org-scorecards` (O8) |
| 2.12 Truth per position | `U-org-principals` (ledger authors), `U-org-packets` |
| 2.13 Audit line and watcher | `U-org-audit`, `U-org-watcher` |
| 2.14 Proving ground | `U-proving-ground` |
| 2.15 Owner's surface | `U-org-surface` (O7), `U-org-chief` |
| 2.16 Evolution | `U-seat-shadow`, `U-org-templates`, `U-org-router` (ORGANISATION § 15) |

## 11. Open, and how each closes
- CLOSED by `R-exec-server` (2026-10-11, the dev Mac, a mock model): the exec-server split carries every host-reaching model-directed action with
  the login outside, and a broker-only codex seat is MEDIATED — thinking seats need no API key. Still open: both on Linux (`U-cell-linux` re-runs
  the kit) and a real model on the broker-only surface (`U-thinking-seats`' first observed run).
- Which tools a seat is actually offered under the owner's home today, fresh and resumed → `U-seat-home`'s inventory.
- Which Linux confinement the host supports: answered for CI's runner and a local VM by `U-cell-linux`'s survey (the stack in § 2.8); the host's
  own answers are the owner's — the nine questions of 2026-10-09 and five for the cells (the kernel, 6.12 or newer for Landlock ABI 6; `landlock` in
  the LSM list; the app as a systemd service under its own non-root user with `Delegate=yes`; no other agent CLI run as that user; an executor
  cell's default network) — then his go to deploy.
- How many concurrent turns a quota pool allows, with the owner's own use beside it → measured under `U-org-lanes`.
- Does a manager layer pay? → the proving ground, then M-org2's paired run (ORGANISATION § 17).
- **The owner's:** when testing may start (M-org0 first); the go to deploy on the Linux host and its nine questions; funding beyond the plan after
  M-org1.

---

*Written 2026-10-11 from the owner's direction of that day and his two answers, on `docs/PRINCIPLES.md`, `docs/VISION.md`, `docs/ORGANISATION.md`,
`docs/EDGE-FACTORY.md`, `docs/COUNCIL.md`, R20–R22, the owner's 2026-09-27 factory plan and 2026-10-06 interface architecture (outside the
repository), the outside review of 2026-10-09 he keeps there, two read-only surveys of `main` at `fd7174ee`, and two adversarial reviews of its draft
(Opus and Codex `gpt-6.1-sol`, read-only; R23 answers each finding). It establishes design only.*
