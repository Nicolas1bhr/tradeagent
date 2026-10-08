# R21 — The three layers of truth, the switch-on, and the owner's decisions of 2026-10-08 (evening)

**What it is.** On the evening of 2026-10-08 the owner asked what would be a real turning point for the program, what to improve now for returns (a deeper
hierarchy, "the three layers of truth", immaculate prompts, canonical strategies like trading courses, copy trading, a Jev prediction engine), and settled
four things: open source until the "absolute point", his machines, AgentPool out of scope, and when testing resumes. "The three layers of truth" are the AI
Concierge canon's doctrine for long-running agents (outside this repository, the owner's side project: its §§ 6–7). This file assesses TradeAgent against
that doctrine at `c317606a` and records the conclusions the orchestrator took on his behalf ("i'll let you assess we need to come to actionable
conclusions"). **Labels:** SOURCE = `file:line` at `c317606a`, read and not run (under `src/` unless stated); RECORD = `BUILD-STATUS.md` line; RUN = a command
run on 2026-10-08; INFERENCE = this file's reasoning. Docs only: nothing was built, run or launched.

## 0. Verdict

1. **The observed world is TradeAgent's strongest part and runs 4 % of the time.** App-measured truth is stronger than the concierge canon asks — evidence
   classes per observation, a first-seen gate, a private holdout, UNKNOWN reconciled and never retried, measurement and claim in separate tables — because
   money forced it. But the app records only while it runs: the only tape on the dev Mac is 6 h 04 min, 2026-10-07 14:38–20:43Z (RUN: 6,891 rows in the
   attempt-3 home), and a market feature counts only from a live reading (`docs/CONTRACTS.md:3003-3005`, `:3259`). Every feature program has ≤ 6 hours of
   clean history, and the paper runner refuses every feature program (`Gateway/ForwardRuns.cs:845-846`).
2. **The believed world is prose.** The agents' hypotheses, findings, kill reasons and next steps live in `PLAN.md` (≤ 60 lines) and `JOURNAL.md` (≤ 200)
   (`AgentRuntime/WorkspaceRevisions.cs:40,47`) and an uncapped, unversioned archive; facts, assumptions and hypotheses are not told apart, and every turn
   rebuilds belief from those files ("read `PLAN.md` and `JOURNAL.md` first", `AgentRuntime/MissionLoop.cs:654-656`; a fresh CLI session every 20 turns,
   `:108`). The canon says the opposite: work from maintained sources of truth, not by reconstructing reality each time.
3. **The desired world is a timer.** A review per role every 30 minutes whatever the role asked (`MissionLoop.cs:1968-1974`, `Core/Trading.cs:476`), beside
   twelve wake kinds (`MissionLoop.cs:2252-2269`); attempt 3 spent 2.03 USD, 53 % of its run, on 16 reviews after the last useful work (RECORD `:8816-8817`).
4. **The canon is accreted and partly false.** `AGENTS.md` is one ~520-line text (`AgentRuntime/WorkspaceBuilder.cs:194-714`) that each unit extends by a
   paragraph (`:319`, the feature line); it tells the harness worker of a shell, the internet and `next.json` it cannot reach (R20 § 1) and that an idle turn
   costs almost nothing (R20 § 4). Nothing binds it to what a runtime is actually granted.
5. **The turning point is the switch-on** (§ 3): the whole loop running unattended, 24/7, on paper, on the owner's always-on machines. Everything EDGE § 3
   calls the moat — the tape, answers recorded at arrival, the forward record, the memory of what was tried — accrues only while the app runs.

## 1. The doctrine (the concierge canon §§ 6–9, paraphrased)
- **Canon** — what the system is and how it works: principles, responsibilities, authority, definitions, invariants. Changes deliberately and rarely.
- **Operational prompts** — temporary instructions for current work; disposable, subordinate to canon, never system truth because they were said.
- **Sources of truth** — what is currently believed: user, world, work, system and operational truth. Facts, assumptions, hypotheses, inferred patterns
  and conclusions stay distinguishable; promotion between layers is controlled (a hypothesis does not become a fact, a strategy does not become canon,
  experience does not silently become policy).
- **Observed, believed, desired** — evidence of what exists; the best reconciled interpretation; the state being sought. The loop: observe → understand →
  reconcile → decide → delegate → act → verify → update. Unknowns are first-class; belief is versioned (what was believed when, why, what changed it).

## 2. TradeAgent against it
| Layer | Today (SOURCE) | Verdict | Unit |
|---|---|---|---|
| Canon | `AGENTS.md` rewritten at each start from one builder (`WorkspaceBuilder.cs:194-714`), extended per unit | durable rules, how-tos and capability claims mixed; true only where a unit remembered | `U-canon` |
| Operational prompt | the Situation (`MissionLoop.cs:658-752`): owner messages, deliveries, wake causes, boundaries, account lines | disposable and subordinate, as it should be; carries no belief and no objective | `U-reconcile-wakes` |
| World truth | tape classes (`docs/CONTRACTS.md:2995-3020`), first-seen gate (`:3224-3234`), referee and holdouts, gateway UNKNOWN, ledgers | strong; intermittent | the switch-on |
| Work truth | campaigns, frozen versions, backtests, verdicts, deployments are app data; the agents' own reasoning is two capped prose files | the believed world is prose; promotion uncontrolled | `U-research-ledger` |
| System truth | tools and verbs stated in the canon's prose | drifts (R20 § 1) | `U-canon` |
| Operational truth | none kept | — | `U-research-ledger` (as claims) |
| Desired world | the mission text and the owner's guidance; a 30-minute review | polls instead of reconciling | `U-quiet-review` → `U-reconcile-wakes` |
VISION § 7.4 (the belief graph, case law, doctrine, sleep) is the mature form of the last three rows; nothing of it is built, and its first version
belongs to the two roles that run today, not to an organisation that does not yet exist.

## 3. Conclusions — decided (the orchestrator, on the owner's behalf; his to overrule)
1. **The switch-on is the target.** Seat A: `U-tape-chain` (resume) → `U-runner-features` → `U-holdout-campaign` → `U-paper-books` → `U-trial-returns` →
   `U-referee-v2` → `U-forward-standing` → `U-incubator`; light `U-linux-host` (survey first): the app unattended on Linux — linux-x64 runtimes, a virtual
   display, restart after a failure or reboot, a durable home; owner controls stay in-process, reached through a remote view of the app's own window, never
   a new remote channel. **Claim when deployed** (on the owner's word): seven days unattended on his always-on host — the tape recording with gaps
   accounted, Jev answering arriving items, both roles researching under the daily cap, several paper strategies with forward records, one retirement —
   M1, M3 and M4's mechanics; not profit.
2. **The three layers, first on the two existing roles** (seat B), designed so lane B extends them to positions: `U-quiet-review` → `U-memory-kept` (READY)
   → `U-canon` (a short deliberate canon — identity, mission, authority, evidence rules, the boundary — with capability facts generated from what the app
   grants that role and runtime, and a test that fails when the canon names a tool, verb or file the runtime cannot reach; changed only by a unit that says
   so) → `U-research-ledger` (hypotheses, experiments, findings and kill reasons as app data, each entry marked claim, assumption or hypothesis — the agent's —
   or measurement, linked by the app to a backtest, verdict or forward record; versioned; `PLAN.md` and `JOURNAL.md` stay the agent's scratch; VISION § 7.4's
   belief graph v0, and `U-experiments-op`'s genome bank builds on it; a main rung) → `U-reconcile-wakes` (explicit objectives per role; a wake when an
   observation touches what the role owns — a verdict, a fill, a subscribed tape event, a deadline, a delivery — not a fixed timer; each turn's prompt a
   packet of what changed, what it believes and what is wanted; `U-org-wakes` and `U-org-packets` re-scoped to extend it).
3. **The owner's list:** prompts = `U-canon` (measured over two observed runs, old canon against new, same budget, when testing resumes); Jev = a reader, not
   an oracle (R02 § 0: strong on short scoped judgements, weak on numbers, dates and finance text; the one public news-to-returns test found no tradeable
   next-close signal) — `U-decision-port` (re-check at dispatch: its brief assumed lane B's principals and envelopes first) → `U-decision-card` →
   `U-annotator`, answers recorded at arrival; canonical strategies = `U-hypothesis-library` after the ledger — documented families (trend, carry, momentum,
   reversal, event drift) and the owner's course notes entered as claims and hypotheses, judged by the referee like any idea, never canon (one course rule,
   never cutting a loss inside a "buy zone", is what the app's loss limits refuse); copy trading = positioning features only (`U-tape-chain`; R03 § 0
   item 5); the hierarchy = lane B after the three layers, its first teams on distinct information (a data world each, Jev's labels).
4. **Seat P closes** until a Windows box answers: live stays closed (licence, venue, broker), so its BEFORE-LIVE items and `TheOwnersAnswerAsync` wait;
   `U-red-says-why` rides as a light. **M0 attempt 4** tests the new loop, on the owner's word, after `U-canon`, `U-research-ledger`, `U-reconcile-wakes` and
   `U-runner-features`.

## 4. The owner's words and decisions, 2026-10-08 evening (verbatim)
- "it is substantially cheaper to develop this as opensource. absolute point meant the moment the moat it proven and this should have become gatekept." →
  public until then; at the absolute point development moves to the private `TradeAgent-Org`; meanwhile nothing that reveals a working edge is committed
  (`CLAUDE.md`). Proposed bar, his to confirm: the first family whose forward record clears EDGE § 8's learning tier.
- "i have four machines at my disposal my macbook, a powerful desktop, a linux server, a large hostinger vps two of the last run 24/7 for the jev related
  stuff i'd suggest we wait a bit before testing though implementation can start whenever." → the always-on Linux machines host the switch-on and the Jev
  reader; nothing is deployed or tested on them until he says; building starts now.
- "we shouldn't act upon AgentPool that's a separate project." → out of TradeAgent's plan.
- "when i said the three layers of truth i was talking about the ai concierge project inspirations we have written in the vision related to the best possible
  way to run long running ai agent's." → VISION § 6.7.

## 5. Open
- The absolute point's bar (§ 4). · Whether the app runs unattended on Linux today → `U-linux-host`'s survey. · Which machine is the ATAS box (the desktop is
  the likely one). · Jev's account and daily budget: the owner's, entered on the Perception card `U-decision-card` builds (EDGE § 10.5).
