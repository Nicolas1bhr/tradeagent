# R24 — The security board: the owner's direction of 2026-10-23, read against `main` and designed (2026-10-23)

**What it is.** On 2026-10-23, resuming the build run halted the evening before for security updates, the owner said where the organisation's safety
should come from — "the security board not programmatic immutable parameters in the ui" — and answered two questions on it, both against the
orchestrator's recommendation, with the option texts in front of him. This file records his words and answers verbatim, the reading, a read-only inventory
of every owner-facing control at `main` `101e0006` (schema 46; the full table is the fleet's `tmp/DIRECTION-2026-10-23/inventory.md`), the design, every
protection it touches, the order of work and the open questions, each with a default that applies until he answers. The design is the orchestrator's
direction, developed by the direction leg and corrected where the code or the docs showed it wrong (§ 6). The decisions are the owner's where § 1 quotes
him and the orchestrator's on his behalf elsewhere — his to overrule. Docs only: no product code, test or build.

**Labels** (as in R20–R23): SOURCE = `file:line` at `101e0006`, read and not run (under `src/`); RUN = a command run on 2026-10-23; RECORD =
`BUILD-STATUS.md`; INFERENCE = this file's reasoning. Everything under § 4 is DESIGN: nothing here marks a feature built, a test passed or a boundary
enforced.

## 0. Verdict

1. **The owner moved the organisation's limit and capital authority from his own presses to a security board** — positions of the organisation outside
   the chief's line, which decides every limit and puts real money to work "on its own judgement, with no mandate from you". Only the kill switch stays
   his. Spending on a paid API key still needs his word.
2. **What changes is who holds the vault door's key, not the door** (PRINCIPLES: "The vault door belongs to deterministic code"). Every gate the gateway,
   the meter and the allocator enforce today stays deterministic and enforces whatever is currently decided. The owner-set boxes go; enforcement does not.
3. **The app is today the inverse of his direction.** The Safety page holds about twenty owner-set limit, allowance and capital controls (inventory § 1),
   and no page shows the organisation the backend already records — the chart, mandates, deliveries, decisions, forecasts. An escalation that reaches the
   root is shown nowhere, although `docs/ORCHESTRATION.md` § 2.9 says his surface shows it (RUN: no `src/TradeAgent.App` file names an escalation).
4. **Four protections a literal reading would drop are kept, because his words did not reach them:** SIGNAL-001 — a board approving each order would be
   model judgement timing it; EVID-001 — the holdout, paper friction, the AI price and the campaigns' evidence budgets are measurements, not limits;
   CHANGE-001 — the board cannot set its own power, composition or budget; and containment — a board seat is a real-model seat, so nothing activates
   before a recorded proven cell, and nothing that arms live runs unconfined.
5. **One protection the owner removed, said plainly:** PRINCIPLES' "New live authority and live capital allocations retain their deliberate owner
   confirmation". What remains is deliberate confirmation inside the board — two independent seats — with the gateway's gates, a raise-pace guard, his
   veto that narrows, the kill switch, and the money he chooses to connect.
6. **Real money cannot move until the frozen runner can trade live, and it cannot today.** A deployment is refused in every live mode by its identity
   (SOURCE `Gateway/TradingGateway.cs:3505-3525`, the check at `:3521`). The only live order path in the code is the legacy `operations` seat's own orders,
   which a board may not switch on (SIGNAL-001). Live waits for the board's activation and for the runner's live path (§ 7).

## 1. The owner's words and answers, verbatim

**2026-10-23 ~00:0x, resuming the run halted 2026-10-22 23:1x:** "okay let's resume with our same momentum i'd like you to continue what was going on and
update everything that should be after the world wode php exploit. i noticed that the app is a not only a bad reflection of the current software but also
currently holds way too much decision making in the settings and such. based on the end vision this software will run a continuously running wild and
zany orchestration of agents. on all sorts of investment related ventures as wide as index fund trading, to copy trading to even crazy and crazier stuff
than memecoin sniping and so on. the "security" should come from the security board not programmatic immutable parameters in the ui. i'll let you decide
how we should address this all, as always you can always ping me if you need my input i won't be available in about an hour"

**His two answers, ~00:1x** — both against the orchestrator's recommendation:
- Asked "You want the security board, not fixed settings, to keep the organisation safe. For real money, how far should the board's authority go?" he chose
  **"The board decides alone"** — "Only the kill switch stays yours. The board can put real money to work on its own judgement, with no mandate from you."
  Not chosen: "One signed mandate (Recommended)" — a bounded, expiring mandate he signs with two presses (VISION § 8's standing authority); "Your presses
  on each step" — today's rule.
- Asked "Who sets how much AI spending the organisation may use per day?" he chose **"The board sets it"** — "The board sets the total too, within your
  ChatGPT plan. Spending on a paid API key would still need your word." Not chosen: "You set one total (Recommended)".

## 2. The reading

- **"a bad reflection of the current software"** — the app shows the trading product of September, not the organisation built since: the app becomes
  organisation-first (§ 4.7), and `U-org-surface` (O7) is re-scoped to it.
- **"way too much decision making in the settings"** — the owner's surface shrinks to what only he can do (§ 4.3); every other decision becomes the board's,
  shown with its history, rationale and score.
- **"wild and zany … index fund trading, … copy trading, … memecoin sniping"** — ventures, representable without limit (VISION § 0's law) and activated
  only when earned; the order stays evidence-led (§ 4.6).
- **"the security should come from the security board"** — the board holds the authority; code keeps enforcing what it decides. A limit stops being a
  number the owner typed and becomes the board's recorded decision, which the same gate enforces.
- **"Only the kill switch stays yours"** answered a question about real money. Read with PRINCIPLES, which keeps "Pause, stop and flatten" as one family
  with distinct meanings, the kill switch is that family. The question did not ask about his credentials, account connections, sign-ins, updates or private
  line; they are CLAUDE.md protections, not limits, and stay his. His second answer keeps his word over a paid key.
- **What his words did not change**, and ruling 3 of the orchestrator's resume header holds: the four `IAtasAdapter` rules; SIGNAL-001, which he made
  permanent on 2026-10-08; ORDER-001; UNKNOWN-001; EVID-001; INBOX-001; CRED-001; containment (his 2026-10-11 answer: nothing runs on real agents before
  executors have confined cells); no terminal; the honesty rule.

## 3. The code, read (inventory, at `101e0006`)

- **Who can change a control today: only an in-process press.** No pipe op reaches a limit, a mode, a cap or an allocation (SOURCE `Core/Protocol.cs:12-251`).
  The mutating ops are order ops and `deployment-stop`, which only removes exposure (`:159-170`).
- **Classes** (the owner's, plus one): OWNER — the kill switch with pause, cancel all and close all; connections, account choice, sign-in, the bridge;
  updates; the real-money AI cap and the keys; the Oversight page and its cap; the answer to an UNKNOWN from his account. BOARD — trading mode, live
  activation, the nine risk limits and the allowlist, the loss-hold release, capital allocation, paper envelopes, the plan's AI day-total, the turn
  allowance, the review interval, lanes, model and runtime choices, the research share, and the settings-file-only wake policy fields. APP — resume-on-start
  (`Core/Trading.cs:340`, no UI), the report's "Write it now", the inbox's file chooser; the code has no language or notification setting today. OBSOLETE — the per-order approval of LIVE_CONFIRM (§ 4.2), the research share
  once O4's envelopes carry it, the fixed review interval as positions take event wakes. **EVIDENCE**, a class the owner's list lacked: the AI price
  override (a lower price loosens every cap), paper friction, the holdout cutoff and the campaigns' trial, verdict and exploration budgets
  (`Core/Trading.cs:628-683`, no UI). `docs/ORCHESTRATION.md` § 2.3 already names "evidence policy and global evidence budgets" never delegable.
- **Facts the design rests on.**
  - Live needs two settings: `Mode` and `LiveActivated` (`Gateway/TradingGateway.cs:3096-3131`). Selecting a live mode is refused while `MaxDailyLoss` is
    0 (`:3112-3113`).
  - The kill switch stops in one press and resumes in two (`App/DashboardView.cs:1719-1727`). Cancel all and close all take two presses each
    (`:1739-1744`).
  - An allocation is accepted only for a version whose promotion stands, in one transaction (`TradingGateway.cs:251`; `Allocations.Record`). The data
    licence gate keeps a live allocation off a research-only dataset (CONTRACTS § Data licences).
  - An unconfined runtime is refused while live is armed (`AgentRuntime/Containment.cs:71-73`).
  - At most one unconfined attempt runs, by a code constant (`AgentRuntime/Lanes.cs:93`).
  - Settings live under `state/`, which an unconfined neighbour can rewrite (ORCHESTRATION § 2.5).

## 4. The design

### 4.1 The security board
- **What it is.** It is a set of positions outside the chief's subtree and outside every `org_*` table, like the audit line's and the watcher's identities:
  no organisation verb charters, hires, retires or funds a board seat. Its ids are minted by the app; it has its own store with one app writer; it
  reports to the owner. It inherits VISION § 5.3's independence and that section's security plane and narrow emergency powers: quarantine a department's
  cells, freeze its envelope, revoke its workload identities. It is the third accountability line beside the audit line and the watcher, not around them
  (§ 6, correction 2).
- **It decides:**
  - every limit in every money currency: paper and live exposure and loss, the risk limits, the instrument allowlist, paper envelopes;
  - the plan's AI day-total within his plan and its division among positions and app principals, the turn allowance, lanes and the wake-policy data;
  - seat choices among the runtimes he connected;
  - autonomy grants — capabilities to positions;
  - venture approval;
  - paper and live mode, live activation and capital allocation;
  - the chief's appointment and replacement, and structural vetoes;
  - lifting a quarantine;
  - what ORGANISATION § 3 gave the owner about `operations` — its seat, occupant and unit — but not the binding of order permission to it, which is
    code (ORDER-001).
- **It never decides:**
  - a trading act: no board verb places, alters, times or sizes an order; it allocates to a version and sets ceilings (ORDER-001, SIGNAL-001);
  - evidence: the holdout, friction, prices, global evidence budgets, verdicts or live-eligibility classes (EVID-001). It may divide trials and verdicts
    already granted, never widen them;
  - its own power: its composition, its budget, the audit line's budget, the watcher or the kernel's invariants (CHANGE-001);
  - the owner's controls (AUTH-001) or credentials (CRED-001);
  - anything through text (INBOX-001).
- **Its seats are real-model seats under the activation gate** (ORCHESTRATION § 2.3): broker-only or confined, never unconfined, and none before the host
  holds a RECORDED proven cell. Once they run, no unconfined seat shares their host (the no-unconfined-neighbour rule), so `state/` — where the board's
  decisions and the settings live — is out of every model's direct reach.
- **Its cost.** A two-seat act costs two strong-model turns, and on Plus the whole organisation shares about six every five hours (ORGANISATION § 10). The
  board therefore wakes only on a proposal, a boundary or a detector, never on a heartbeat, and a narrowing needs one seat.
- **Composition — the founding default.** Three seats, on at least two model classes, and one on a second vendor family as soon as the owner connects one.
  The owner changes it, two presses; the board may propose a change, never make it. Open question 6 covers this.

### 4.2 The board's acts
- **Broker verbs held by board positions only**, named apart from the pipe's ops as the mandate verbs are (ORCHESTRATION § 2.9) — for example
  `board-propose`, `board-confirm`, `board-narrow`, `board-list`, `board-show`; the unit names them. They are grant-checked and lattice-checked (VISION
  § 6.2). No other position, the chief included, holds them, and no packet, note or document reaches them.
- **Each act is a row.** It holds the exact intent: a typed change (field, from, to) or an allocation (version, account, ceilings), bound by its hash —
  VISION § 6.2's "a decision binds to the exact intent it authorised". It also holds:
  - the rationale, as a claim;
  - the evidence ids, checked by the app;
  - the evidence class of anything allocated;
  - forecasts in O5b's closed vocabulary;
  - the seats that acted.
  The audit line and the watcher score it later. The founding policy (§ 4.5) changes only by board acts; the board can supersede a founding value, never
  rewrite the record of it.
- **Narrowing takes one seat; widening and money take two** — the orchestrator's design, the owner's to overrule. Lowering a limit, withdrawing an
  envelope or allocation, deactivating live or quarantining applies on one seat at once. Raising any limit, granting an envelope, allocating capital,
  selecting a live mode, activating live, granting a capability or lifting a quarantine needs a **proposer and a confirmer** — two distinct board seats:
  - The confirmer receives the intent and the approved evidence, never the proposer's rationale before its own decision is sealed (VISION § 5.4's
    shadow rule).
  - The two seats are on different model families where his connected runtimes allow (VISION § 7.6). On his plan alone they are one family, and the
    record says so.
  - The confirmation binds to the intent's hash and expires; a refusal is a record too.
  This is PRINCIPLES' deliberate confirmation, kept inside the board.
- **The raise-pace guard on live** (ORGANISATION § 12's risk-ceiling guard, carried over as an app rule): a live loss or exposure limit rises at most
  once in 30 days and by at most 25 % a step; a lowering applies at once. Open question 11 covers this.
- **Gates the board cannot pass**, unchanged in code:
  - live refused without a daily-loss bound (`TradingGateway.cs:3112-3113`);
  - live eligibility read from the app's standing promotion;
  - the data-licence gate;
  - the armed-live refusal (`Containment.cs:71-73`);
  - reconciliation before anything new;
  - the kill switch.
  The board cannot arm live unless at least two of its seats run on a class that refusal admits — confined in a proven cell, or brokered on the API
  harness. Otherwise arming live would stop the board that must be able to narrow it.
- **No per-order approval.** LIVE_CONFIRM's per-order press (`App/DashboardView.cs:579-581`) cannot become a board act: a board seat approving each order
  would be model judgement timing an order (SIGNAL-001).

### 4.3 What the owner keeps — in-process, unreachable from every pipe op and every verb, the board's included (AUTH-001)
- **The kill switch and its emergency family**: pause the AI, stop AI trading, cancel all, close all. Each keeps its press count: stop in one, resume in
  two; cancel all and close all in two. The board cannot lift a stop.
- **Credentials, sign-ins and account connections.** Connecting an account is what makes its funds reachable; disconnecting removes them. The money he
  connects is the outer bound on what the board can put to work. A new venue, a wallet's funding and the bridge are his, and runs on his own machines
  need his word, as today.
- **A paid API key's spend**: the real-money cap (`AiDailyCostCap`, `Core/Trading.cs:578`) and every key. The board divides inside the cap — the
  perception budget, a harness seat's share — and may lower it, never raise it. Open question 4 covers this.
- **Updates of the app.**
- **His private line**: the watcher (ORGANISATION § 9) and its own cap, invisible to the organisation and now to the board too, watching the board as well.
- **Facts only his account holds**: his answer on an UNKNOWN order or an operator press (UNKNOWN-001; `App/DashboardView.cs:784-790`).
- **The unconfined-seat ceiling**, because an unconfined seat can read his login (CRED-001; ORGANISATION § 10).
- **The board's composition, its budget and the audit line's budget.** The founding defaults apply until he changes them (CHANGE-001; the auditor's
  independence, R13 D8).
- **The veto** — the orchestrator's design, his to overrule; open question 2. He may veto any board act at any time, in two presses. A veto cancels the act
  and restores the state before it, but it never widens a limit or puts money to work — it belongs to the kill switch's family. Nothing waits on him.
- **His words**, as to any position: data that binds nothing (INBOX-001).
- **The evidence settings**: the holdout cutoff, paper friction, the AI price and the campaigns' evidence budgets, which no model may set. Default: they stay
  his presses, as today; open question 8.

### 4.4 What the board cannot widen — its own power (CHANGE-001)
It cannot reach credentials, updates, the kernel's invariants (containment, evidence, the supervisor, the activation gate, the code ceiling of one
unconfined lane), the watcher, the audit line or the kill switch. It may **propose**:
- a change of its composition or budget;
- a new connection, key, runtime or vendor;
- a change to the founding policy's shape;
- a kernel change.
Each proposal is a record shown to the owner; the owner acts on it, or no one does.

### 4.5 Before the board is active — the founding policy
- **Until the board's seats pass the activation gate, an app-shipped founding policy governs paper and AI spend.** It is one versioned record whose
  decider is `app` — never attributed to the owner — read by the gateway, the meter and the paper allocator wherever they read an owner-set field today.
  - Its values are the build's current defaults where one exists (`Core/Trading.cs:26-155, 485-609`: quantity 1, two open positions, six orders a minute,
    24 h loss closure, 7-day strike window, 15-minute valuation exit, the plan day-total at 40 list-price, one lane, a 30-minute review).
  - Where today has no default — a standing paper envelope on the app's own paper venue and its allowlist — `U-board-a` proposes values in its brief and
    the orchestrator decides, conservatively.
- **Real money cannot be put to work at all before the board exists.** No live mode can be selected without a board act, and with no board there is
  none: the live path is closed by construction. The owner's live press leaves with his limit boxes, so from the train that lands `U-board-a` until the
  board and the runner's live path exist, no one — the owner included — can put real money to work. That follows his answer; it is said here so that it
  is not discovered.
- **No backward compatibility** (CLAUDE.md). A home's owner-set values for the fields that move simply go: no migration, no dual reader, no field kept for
  an older build. The founding policy governs until the board decides. The record of his past presses in the activity log and the ledgers stays
  (append-only). A home whose settings hold a live mode or an activation from before the board is **refused**, naming both versions and the repair. It is
  never converted into a board decision and never silently switched.

### 4.6 Ventures
- **Representation is unbounded.** Index funds, copy trading, memecoin sniping and wilder are venture kinds — validated text a later build extends, never
  an enum — that the organisation proposes and the board approves. A venture is a mandate (VISION § 6.1): thesis, venue needed, evidence class, kill
  rules, tranches.
- **Each keeps the evidence protections.** Verdicts and live-eligibility classes are app measurements. The board may act on weak evidence, but only
  visibly:
  - "Weak" means a lower class the app defines, never no record. The allocation carries the class, and the watcher and the audit line score the board on it.
  - Today the only live class is `promoted`. A venture whose thesis is not an edge — a passive index allocation — needs an app-measured class of its own
    before any live allocation; `U-ventures-a` proposes the class, and the referee's rules stay the app's.
- **Each needs a venue the money-path rules allow.** Its connector keeps `IAtasAdapter`'s four rules, or reports the capability false and is refused fully
  automatic trading. A venue account is the owner's connection, so a venture can wait on him only for what needs his connection or his money.
- **On-chain ventures** need wallet keys the app holds and no seat sees (CRED-001). Funding a wallet is his connection. Open question 10 covers this.
- **Legal status stays his**: venue terms, professional status and tax (R16). The data-licence gate stays code.
- **PRINCIPLES' "Copy trading as such stays unprioritised" is amended**: breadth is the end state; the order of building stays evidence-led, and public
  positioning enters first as measured features.

### 4.7 The app
- **The owner's surface:**
  - the kill switch and the emergency family;
  - connections and sign-ins;
  - the paid-key word;
  - updates;
  - preferences;
  - Oversight;
  - his inbox and his veto;
  - the evidence settings (by default).
- **Everything else becomes the board's decisions.** Each is shown as what is in force now — a founding value or a board act — with its history,
  rationale, deciders, evidence class and score.
- **Organisation-first:**
  - the chart: positions, seats and cell classes in plain words;
  - ventures;
  - the board;
  - the work: mandates, deliveries, decisions, forecasts;
  - costs by billing class, per unit;
  - the owner's inbox: escalations at the root, owner words owed;
  - Oversight.
- **The fleet's leave to press in-app permission and allowance controls** (the owner, 2026-10-22 19:3x) covers what is left: once `U-settings-shed` lands,
  those controls are board acts, not presses.

## 5. Protections touched — what stays, what moves, how each is kept

| Id | Stays | Moves to the board | How the property is kept — or what the owner removed and what remains |
|---|---|---|---|
| AUTH-001 (operator authority in-process) | the kill switch and emergency family, connections, sign-ins, paid-key spend, updates, the private line, the veto, the unconfined-seat ceiling, the board's composition and budget | mode, live activation, every limit and allowance, allocation, ventures, instruments | **split** into AUTH-001 (the owner's, in-process, no op or verb reaches it) and **AUTH-002** (the board's, reached only by board positions' broker acts, two seats for a widening). The owner REMOVED his in-process authority over the second list; the gateway's enforcement of it stays |
| PRINCIPLES' deliberate owner confirmation of live authority and allocations | — | the confirmation | REMOVED by the owner. What remains: two independent board seats with a blind confirmer, the raise-pace guard, the gates, his narrowing veto, the kill switch and the money he connects |
| Two-press (CLAUDE.md) | every owner press keeps its count (stop one, resume two) | — | translated: a board act that moves real money or widens a limit takes two seats |
| ORDER-001 | order permission bound to `operations`; never by headship | `operations`' seat, occupant and unit (two seats) | no board verb places, alters, times or sizes an order; no board act names an order |
| ORDER-002…005 | the four rules | — | a venture's venue keeps them or is refused fully automatic trading |
| SIGNAL-001 | permanent | — | the runner trades; no per-order board approval; the legacy discretionary live modes are not the board's to enable (open question 9) |
| UNKNOWN-001 | reconciliation; the owner's answer from his account | — | the board cannot mark an order resolved |
| EVID-001, SIM-001 | the referee, holdout, ledgers, friction, prices, evidence budgets | — | the board writes no class or verdict; every allocation records the app's evidence class; simulated stays simulated |
| INBOX-001 | — | — | a board act is a verb call, never text; the owner's words to the board bind nothing |
| BUDGET-001 | reserve before launch; the day-total gate first; unknown never zero; the real-money cap the owner's | the plan day-total and its divisions | the board's and the audit line's budgets outside the board's reach; the AI price never the board's |
| CRED-001 | no seat holds his credentials | — | the board holds none; wallet keys app-held; the unconfined ceiling stays his |
| ORG-001 | a position grants no system privilege | — | board seats are outside `org_*`; their verbs are their only reach |
| CHANGE-001 | — | — | the board proposes changes to its own power; only the owner makes them |
| Containment (the activation gate, the armed-live refusal) | unchanged | — | board seats wait for a recorded proven cell; live is armed only with board seats that refusal admits |
| COUNCIL rule 1 ("Only in-process owner controls grant authority") | the gates in its second half | the grant of limit and capital authority | changed by the owner. COUNCIL.md is not amended here: `U-board-a`'s landing amends it, or the orchestrator does |
| Watcher invisibility (ORGANISATION § 9) | unchanged | — | extended: no board-reachable total, packet or page names it |
| No terminal; honesty | unchanged | — | the board page and the veto are in the app; founding values are labelled the app's; each board seat's enforcement class is shown |
| No backward compatibility | — | — | owner-set fields go without migration; an older home in live is refused, never converted |

## 6. Where the direction was corrected, and why

1. **The record is R24, not R23.** R23 is `docs/research/2026-10-11/R23-orchestration-first.md`, cited by ORCHESTRATION's header and ORGANISATION § 15.
2. **The board is not VISION § 5.3's council whole.** That council holds assurance and the owner's private line, and a body cannot audit or secretly
   watch itself (audit sits outside the chain it checks, R13 D8). The board takes the council's independence, security plane and emergency powers; the
   audit line and the private line stay beside it and score it. This matches the resume header's "beside the audit line and the watcher".
3. **"Every limit in every currency" excludes evidence and accounting.** A board that could lower the AI price would loosen its own caps; one that could
   lighten friction, move the holdout or widen a campaign's verdicts would buy evidence. It divides evidence currencies and never widens them (§ 4.1).
4. **"The frozen runner trades" is not built for live.** Deployments are refused in every live mode, and the runner's money-path fixes are parked
   (`U-backtest-parity`, `U-runner-fixed-levels`). `U-board-live` builds the board's authority, and the runner's live path is a later unit (§ 7).
5. **"Every OWNER press keeps its two presses" is "keeps its press count".** The kill switch stops in one press by design (`App/DashboardView.cs:1719-1722`).
6. **The two-seat confirmation is one model family on the plan alone.** The confirmer is therefore blinded to the proposer's rationale, and the record
   names the family.
7. **A board that arms live must keep running**, because every seat without a cell is refused while live is armed. Arming needs two board seats on an
   admitted class.
8. **"Weak evidence" means a lower app-defined class, never none,** and a non-edge venture needs its own app-measured class before any live allocation.
9. **The amendment list missed two places that would contradict R24.** PRINCIPLES "The organisation" ends "the owner decides authority", and that sentence
   is amended here. COUNCIL rule 1 is named in § 5 and not amended: it lies outside this leg's list.
10. **"Nothing waits on him" holds for decisions, not for money or reach.** A venture needing a venue, a key or a wallet waits for his connection.

## 7. The order of work (proposed rows for ORGANISATION § 15; one builder pass each)

- **First, both seats:**
  - the landings in flight: `U-execution-environment` and `U-org-audit-a` (A); `U-org-verbs-a` and `U-number-entry-culture` (B);
  - the M4 regression, a P1 (seat A; the orchestrator's survey, the fleet's `tmp/M4-regression/report.md`);
  - the security sweep's light unit (seat A; `tmp/SWEEP-2026-10-23/report.md`);
  - the owed Codex second passes.
- **Seat A — the board's kernel side.**
  1. `U-board-a` — the board as data: positions outside the chief's subtree and every `org_*` table, the founding composition, its store and one writer;
     the founding policy as a versioned `app` record that the gateway, the meter and the paper allocator read instead of the owner-set fields; live
     closed by construction; owner-set values dropped without migration; an older home in live refused. A rung. It lands in ONE TRAIN with
     `U-settings-shed`, so no build shows a box that no longer governs.
  2. `U-board-b` — the board's broker verbs, narrowing on one seat, widening and money on two with a blind confirmer bound to the intent's hash, each act a
     decision record with an evidence class and forecasts, the owner's narrowing veto, the board's own power out of its reach, and whole board lives on
     puppet seats in CI at no AI cost. A rung.
  3. `U-watcher-board` (light) ∥ `U-board-live`. `U-watcher-board` gives the watcher detectors over the board — rubber-stamp rate, same-family
     confirmations, widening after a loss, allocations by evidence class, board spend — and makes it invisible to board seats, by extending the
     enumeration test. `U-board-live` is the money path, opus/xhigh, AUTH-002 in code:
     - mode, activation and allocation as two-seat board acts;
     - `LiveActivated` written only by a confirmed act;
     - the raise-pace guard;
     - arming refused unless two board seats run on an admitted class;
     - the runner's live dispatch kept refused.
  4. `U-ventures-a` — ventures as data: kinds, the mandate shape, the evidence class on an allocation, a non-edge class proposed. No connector. A rung.
  5. Then the old order: `U-org-audit-b` (its budget the owner's, never the board's) → `-c` (the adversarial brief also at board boundaries) → the
     proving ground. **`U-runner-live`** — the runner trading a board allocation in a live mode — joins the parked edge lane after `U-backtest-parity`
     and `U-runner-fixed-levels`. Until it lands no real money moves.
- **Seat B — the surface.**
  1. `U-settings-shed` (in `U-board-a`'s train). Settings and Safety shed every BOARD control and the owner's live press. A Board page shows each decision
     in force with its history, rationale, deciders, evidence class and score. The owner's surface is § 4.3's.
  2. `U-org-surface-a` (O7, re-scoped) — the organisation-first app, read-only: the chart, seats and cell classes, mandates, deliveries, decisions,
     forecasts, costs by billing class per unit.
  3. `U-org-verbs-b` … `-e`, as surveyed, re-checked at dispatch against R24: the unconfined-seat ceiling stays the owner's; a structural veto is a board
     act or the owner's narrowing veto.
  4. `U-org-surface-b` (O7, after `U-board-b`) — the owner's inbox (escalations at the root, owed words), his veto, the board's acts, ventures.
  5. O8 → `U-org-chief`, re-scoped: the chief is appointed by a two-seat board act, under the owner's veto.
  6. **M-org1**, as before. It runs on paper and does not wait for `U-board-live` or `U-ventures-a`; the order across seats stays the orchestrator's.

## 8. What changed

Docs only — no product code, test or build:
- this record;
- `docs/PRINCIPLES.md` — "The user experience", "The organisation", "The boundary remains enforceable", the priority paragraph and the footer;
- `docs/VISION.md` — §§ 5.3, 6.3, 8, 13.1, 13.3 and 15;
- `docs/ORGANISATION.md` § 15 — new rows, O7's and the chief's re-scope, the order and a re-sequence note;
- `CLAUDE.md` — the operator-authority paragraph and the two-press line.

Outside the repository: the fleet's `tmp/DIRECTION-2026-10-23/inventory.md`.

## 9. Open questions — the owner answers, or the default applies

1. **May the owner still put money to work, or set a limit, himself?** Default (from his answer): no. His capital controls are the kill switch and its
   family, the narrowing veto and what he connects.
2. **The veto narrows only?** Default: yes. It cancels a board act and never widens a limit or puts money to work (the orchestrator's design).
3. **Two seats, proposer and confirmer, for every widening and money act?** Default: as designed (the orchestrator's design).
4. **"Spending on a paid API key would still need your word"** read as: his real-money cap bounds every paid-key spend; the board divides inside it and
   may lower it, never raise it. Default: as read.
5. **Should part of his plan be reserved for his own use?** His own Codex and the fleet's reviews share the plan. Default: none reserved ("within your
   ChatGPT plan"); the app shows the window's use, and he may veto.
6. **The board's composition.** Default: as founded — three seats, at least two model classes, a second vendor on one seat once he connects one.
7. **The board's own budget and the audit line's.** Default: founding shares beside the organisation's, changed only by him.
8. **The evidence settings** (holdout, friction, AI price, campaign budgets). Default: they stay his in-process presses, never the board's.
9. **The legacy discretionary live modes** (LIVE_CONFIRM's per-order press and LIVE_AUTONOMOUS for `operations`). Default: closed. Real money moves only
   through the runner on a board allocation (SIGNAL-001), and paper keeps `operations`' own orders.
10. **Wallet keys for on-chain ventures.** Default: decided when the first such venture is proposed — app-held keys, his funding, no on-chain venture
    before.
11. **The raise-pace guard** (at most monthly, at most 25 % a step) on live widenings. Default: adopted as an app rule.

---

*Written 2026-10-23 by the direction leg from the owner's words and answers of that night, the orchestrator's resume header and design direction, and a
read-only inventory of `main` `101e0006`. It establishes direction only: it marks no feature implemented, no test passed and no boundary enforced.*
