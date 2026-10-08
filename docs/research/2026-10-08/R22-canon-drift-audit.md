# R22 — An outside canon-and-architecture drift audit, verified against `main` and answered (2026-10-08, late evening)

**What it is.** Late on 2026-10-08, after R21 (`b29dcead`) and the planning meeting's stand-down, the owner shared a second outside, read-only audit —
"TradeAgent — Canon & Architecture Drift Audit", reviewing `main` at `b29dcead` — as the output of "an automated vision rectifying agent", "not canonical".
Its thesis: R20 and R21 preserved and improved the vision, but the planning documents now disagree with one another, one sentence of VISION § 6.7 constrains
belief more than the doctrine it imports, and R21 puts the next evidence run behind new architecture. This file records what this session did with it, as
R20 did with the first: every claim checked against `b29dcead` — and against the fleet's own files, which sit outside the repository and the audit could
not see — the primary source of the contested doctrine re-read, each recommendation answered, and the decisions taken on the owner's behalf. Before landing,
a fresh Opus review leg pinned to this record's draft (never pushed) re-checked every claim: 24 findings (2 HIGH, 11 MEDIUM, 11 LOW), each answered in the
text below; it overturned one of the draft's own conclusions (§ 3, `U-runner-features`). The audit's text is not reproduced; its four findings are cited
as "audit A–D" and its other parts as "audit § n".

**Labels** (as in R20): SOURCE = `file:line` at `b29dcead`, read and not run (paths under `src/` unless stated); RECORD = `BUILD-STATUS.md` line; RUN = a
command run on 2026-10-08 late evening; CANON = the AI Concierge canonical product concept, the owner's side project outside this repository (§§ 6–9, the
source R21 paraphrased); FLEET = `fleet/` outside the repository; INFERENCE = this file's reasoning. Nothing was built, run or launched.

## 0. Verdict

1. **Its strongest finding is right and worse than it says** (audit A). The order of work was stated in at least fourteen places across the repository and
   the fleet's files, at least six still giving the pre-R21 order — among them the fleet's orchestrator handoff, the copy a new session executes; three
   landed units still read READY; three READY briefs named the closed seat P; one READY brief depended on units R21 moved after it; one READY brief had the
   scope R21 replaced. Now there is one current order (ORGANISATION § 15), which a seat's manager rewrites when it reorders its own lane; the waves table is
   historical; the briefs, handoffs, charter and board agree with it; and a dispatch must check that they do.
2. **VISION § 6.7's "a hypothesis becomes a belief only on a measurement" is corrected** (audit B). The canon sets no measurement precondition on belief —
   it keeps assumptions and hypotheses distinguishable inside the sources of truth and controls only their promotion to fact — and the sentence contradicted
   § 6.7's own list of believed entries, § 7.4's belief graph and PRINCIPLES' autonomy section (§ 1). The rewrite also keeps `CLAUDE.md`'s
   measurement-versus-claim split, which the old wording blurred: an agent's entry stays its claim, and a measurement stays the app's record, linked to it.
3. **Its "factual error" on operational truth is half right.** R21 used the canon's sense — "learned knowledge about how work actually gets done" — which the
   app does not keep; but the row did not say so, and the app's operational record is extensive and authoritative. Both are now stated, and the research
   ledger stores references to app records, never copies, so it cannot become a second authority.
4. **M0 attempt 4 waited on four units it does not depend on** (audit C): the three layers, and — as this record's draft still kept — `U-runner-features`
   (§ 3). Its prerequisites are `U-quiet-review` and `U-memory-kept`, exactly the two the audit named. When it runs stays the owner's word.
5. **Linux, containment and the public-repository rule were already labelled unproven or policy** (audit D, § 3); the labels now stand where the target, the
   observed world and the rule are stated, and the "4 %" carries its period and method.

## 1. The audit's factual claims, checked

| Claim (audit) | Verdict | Evidence |
|---|---|---|
| The two newest commits change documentation only (intro) | TRUE | `git show --stat b29dcead c317606a`: docs, `CLAUDE.md`, `manager-prompt.md`, `BUILD-STATUS.md`, the queue; no `src/`, `tests/`, `tools/` |
| PRINCIPLES now says "Autonomy is the default inside authority" (intro) | TRUE | `docs/PRINCIPLES.md:39-41` |
| `U-decision-port` depends on `U-org-principals` and `U-org-envelopes`; R21 sequences it before them (A) | TRUE | brief `:4` "lands after `U-org-principals`/`U-org-envelopes`"; R21 § 3.3 and `docs/RESUME-HERE.md:22` put it in seat B ahead of lane B's organisation; R21 and ORGANISATION § 15 (`:448-449`) said "re-checked at dispatch" and left the brief READY |
| `U-quiet-review` and `U-memory-kept` designate seat P, which the resume block closes (A) | TRUE — and a third | `docs/queue/U-quiet-review.md:3`, `U-memory-kept.md:3`, also `U-red-says-why.md:3`; R21 § 3.4 closes P and seat B carries all three |
| Old READY statuses for landed units, `U-org-ledger` among them (A) | TRUE — three | ORGANISATION § 15: `U-fix-resume-on-start` "READY, first" (`:395`; landed `c8d6642a`), `U-price-rows` (`:397`; `492ae794`), `U-org-ledger` (`:398`; `33017817`, schema 28) |
| Competing descriptions of the active plan (A) | TRUE — far more than it saw | In the repository: ORGANISATION's waves table, "the only copy" (`docs/ORGANISATION.md:417-438`), against R21's paragraph under it (`:444-449`); EDGE § 9's switch-on paragraph (`docs/EDGE-FACTORY.md:434-438`), its waves line, also "the only copy" (`:431-432`), and its phase-4 line (`:417-418`, `U-decision-port` READY); R21 § 3; the resume block's R21 paragraph (`docs/RESUME-HERE.md:16-25`), its R20 paragraph (`:37`, the two lights in seat P) and its 15:25 checkpoint, whose NEXT (`:89-94`) is the pre-R21 order and which "Do this first" (`:13-14`) sends a new session to. In FLEET: `handoff/ORCHESTRATOR.md` ("Next session, in order": seats P and A, M0 attempt 4's "prerequisites are on main") under its own R21 paragraph; `handoff/A.md` and `handoff/P.md` (pre-R21 queues); `BOARD.md` (R21's M0 prerequisites over a pre-R21 seat table); `status/M.md` (the waves' W7 and W12); `charters/B-agents.md` (R21's single chain, `U-decision-port` READY). Only `status/A.md` and `status/B.md` followed R21 throughout |
| VISION § 6.7: "a hypothesis becomes a belief only on a measurement" is too restrictive (B) | TRUE — and inconsistent three ways | `docs/VISION.md:311`. The same section lists hypotheses AS believed entries (`:306-308`); § 7.4 makes each belief "a claim the organisation writes" carrying an app-written evidence summary (`:368-371`); PRINCIPLES gives the agent "its intermediate hypotheses" (`:41`). CANON § 6: sources of truth "represent what is currently believed to be true", with "facts, assumptions, hypotheses, inferred patterns, and derived conclusions" kept distinguishable, and promotion to fact controlled — "A worker assumption does not become truth automatically. A research hypothesis does not become a user fact." No measurement precondition on belief |
| Operational truth listed as none kept, though the app persists mission events, attempts, spending, gateway states, operations and trading records (B) | HALF | The app keeps them (SOURCE, tables: `mission_event`, `ai_attempt`, `activity`, `health_event`, `tool_call`, `deployment_op`, `execution_request`, `fill`, `boundary_event`, `strategy_*`, `paper_*`, `org_*`). But CANON § 6 defines operational truth as "learned knowledge about how work actually gets done: reliable channels, failure patterns, provider behaviour, verification needs, workflow effectiveness, and execution heuristics" — R21's sense, kept today only in two capped prose files. R21 § 2 (`:47`) did not say which sense, so a reader takes the audit's |
| The canon keeps truth domains and epistemic status apart; R21 compresses them (B) | TRUE for VISION § 6.7 | CANON § 6 (user, world, work, system, operational) and § 7 (observed, believed, desired) are separate sections. VISION § 6.7 said "Sources of truth — app data, in three worlds" (`:305`) and dropped the domains; R21 § 1 kept both, § 2 mixed layers and domains in one column |
| M0 attempt 3: 36 turns, ≈ 3.84 of 5 USD, versions produced and rejected, no holdout verdict or forward result (C) | EXACT (as R20 § 1 found) | RECORD `:8806-8807`, `:8818` ("no role asked `trade verdict`, so the verdict and paper path went unexercised") |
| R21 puts M0 attempt 4 behind `U-canon`, `U-research-ledger`, `U-reconcile-wakes` and `U-runner-features` (C) | TRUE | R21 § 3.4 (`:75-76`) |
| Avalonia 12.1.1, which supports Linux (D) | TRUE (version); support not re-read here | `src/TradeAgent.App/TradeAgent.App.csproj:12-14` |
| Linux deployment is plausible but unvalidated; other documents may come to treat it as established (D) | TRUE / none found | R21 § 3.1 (survey first) and § 5 (open). No document claims the app runs on Linux; four state the target without the qualifier (`CLAUDE.md`, `docs/RESUME-HERE.md:17`, `docs/EDGE-FACTORY.md:434-436`, BUILD-STATUS's R21 section). Attempt 3, the six unattended hours on record, ran on this Mac (macOS); CI runs the tests, not the app, on ubuntu |
| AppContainer: codex and node do not start bare; a credential inside is readable (§ 2) | TRUE — already held | R20 § 4; ORGANISATION § 15, `U-contain-seats`' first item (`:413`) |
| Unconfined CLI processes can reach protected state; R21's praise of the observed world lacks that qualification (§ 3) | TRUE | ORGANISATION § 14 (`:374-381`: holdout, scorer and ledgers "advisory" against a CLI seat); neither R21 § 0.1 nor VISION § 6.7 said it |
| The public-repository rule is a policy, not a publication barrier (§ 3) | TRUE | `CLAUDE.md` Conventions; the gate's scan (FLEET `bin/scan.sh`) matches credentials, host addresses, keys and attribution trailers — nothing can match "edge-revealing". INFERENCE: the app's home (strategies, tape, forward records) lives outside the repository and CI runs on fixtures, so the leak path is the records the fleet writes |
| "Runs 4 % of the time" needs a period and an accounting method (§ 3) | TRUE | R21 § 0.1 (`:13`) gave neither. RUN: Spotlight finds one `tape.db` outside the test homes, the attempt-3 home's — 6,891 rows (R21's figure reproduced); 6 h 04 min over the ≈ 143.5 h from `U-tape-store`'s landing record (`a912261b`, 2026-10-02 22:18 CEST) to R21 (2026-10-08 21:47) ≈ 4.2 %. Tape coverage on this Mac, not an availability metric |

## 2. The recommendations, answered

| Audit | Recommendation | Answer | Where |
|---|---|---|---|
| A, § 4.1 | One current dependency sequence; the historical waves marked historical; every READY brief's dependencies, seat and status reconciled before dispatch | ADOPTED | ORGANISATION § 15: three rows LANDED, the current order (the only copy; a seat's manager rewrites its own row), the waves HISTORICAL; `U-decision-port` RE-BRIEF (it lands first; the two org briefs carry `perception` forward); `U-org-wakes` RE-SCOPE; seat B on the three lights; EDGE § 9 (its waves line, phase 4 and the switch-on), the resume block, FLEET and R21 point to § 15 instead of restating it; FLEET's orchestrator handoff, seat handoffs A, P and M, seat B's charter, the board and seat M's status reconciled |
| B, § 4.2 | Provisional beliefs with uncertainty and provenance; measurements update them; nothing becomes a measured fact because an agent accepts it | ADOPTED, in the repo's words: an entry stays the agent's claim; a measurement is the app's own record, which the app links to the entries it bears on | VISION § 6.7 |
| B | Keep the canon's truth domains beside observed, believed and desired | ADOPTED — two axes; canon § 7's two rules on unknowns and on representations added | VISION § 6.7 |
| B | The ledger must not duplicate authoritative application state | ADOPTED as a schema rule: the ledger stores references to app records, never copies of their fields; an agent's restatement of a figure stays its claim | VISION § 6.7; ORGANISATION § 15 (`U-research-ledger`) |
| B | Operational truth already exists | NARROWED: the operational record exists; operational truth in the canon's sense does not — both now said | R21 § 2; VISION § 6.7 |
| C, § 4.3 | Keep M0's evidence goal independent of the institutional build; fix the idle reviews and the destroyed memory first; build the doctrine in increments | ADOPTED for the prerequisites: `U-quiet-review` and `U-memory-kept` only (§ 3 on `U-runner-features`). NOT ADOPTED: "let the current factory continue gathering evidence" now — running is the owner's word ("wait a bit before testing"), which the audit itself keeps authoritative | ORGANISATION § 15; R21 § 3.4 |
| D, § 4.4 | Linux and CLI containment pending verification; app-mediated versus contained integrity | ALREADY HELD (R21 § 5, R20 § 4, ORGANISATION § 14); the labels now stand where the claims are made | `CLAUDE.md`; EDGE § 9; the resume block; VISION § 6.7; R21 § 0.1 |
| D | The Windows application and its nontechnical owner's experience stay intact | ALREADY HELD: the no-terminal rule (`CLAUDE.md`); owner controls stay in-process, reached through a remote view of the app's own window, never a new remote channel (R21 § 3.1); `U-linux-host` adds a host and removes none | — |
| § 3 | Publication controls before the first promising private strategy exists | ALREADY HELD as policy (`CLAUDE.md`); its limit now said: no gate detects edge-revealing text, a repository made private later does not unpublish its history, so the move precedes the first record that would need it | `CLAUDE.md` |
| § 3 | The 4 % figure needs a period and a method | ADOPTED | R21 § 0.1 |
| § 4.5 | Documentation as governed state: one plan, one dependency record; owner decisions, orchestrator recommendations, verified work and untested design kept apart; old instructions never silently override new ones | LARGELY HELD (the labels SOURCE/RECORD/RUN/INFERENCE, "decided by the orchestrator on the owner's behalf, his to overrule", NOT VERIFIED); the missing mechanisms ADOPTED: a dispatch checks the brief against § 15's current order and fixes the docs first; a seat's manager writes its own reorder into § 15; a dated block's NEXT yields to § 15; a meeting or planning session arms no heartbeat and opens no seat | `docs/HOW-WE-BUILD.md`; ORGANISATION § 15; the resume block; `docs/FLEET.md` |

## 3. What the audit missed, and what the review leg found

- **The copies most likely to be executed were outside its view.** FLEET's orchestrator handoff listed the pre-R21 order under a paragraph giving R21's;
  seat A's and seat P's handoffs kept their pre-R21 queues; seat B's charter, the board and seat M's status each disagreed with the order in a different
  way. A fresh seat opens from exactly those files. Only the two seat status files of 21:53 agreed with R21.
- **`U-org-wakes`' READY brief describes the scope R21 replaced** (it extends `U-reconcile-wakes` to positions now) — dispatched as written, it builds the
  old design. Marked RE-SCOPE; it is re-briefed as a CARD after `U-reconcile-wakes` lands.
- **§ 6.7's sentence broke the rule R20 had just made.** PRINCIPLES outranks VISION, and a control over an agent's reversible reasoning needs a demonstrated
  failure first (`docs/PRINCIPLES.md:41`); no failure was cited. § 7.4 already had the right model.
- **§ 6.7 also blurred measurement and claim.** It let an agent-written entry carry the mark "measurement" once linked — against `CLAUDE.md`'s rule that
  measurement and claim stay in different tables, § 7.4 ("each belief is a claim") and ORGANISATION § 13.6. Now the entry stays the agent's claim and the
  measurement stays the app's record, linked to it. And it left out two canon § 7 rules that matter more once any hypothesis may be held: unknowns are
  first-class state, and a representation of reality is not the reality. Both are in.
- **`U-runner-features` is not an attempt-4 prerequisite either** — R21 said it was, and this record's draft kept it; the review leg showed why not. The
  runner does end a feature program's forward run before a bar (`Gateway/ForwardRuns.cs:845-853`), but no feature program can reach a verdict in attempt 4:
  a feature counts only from its clean-history start (`docs/CONTRACTS.md:3259-3262`), an absent value decides nothing (`:2653-2655`), the one source whose
  readings reach back before a home existed is GDELT's archive, and its recorder backfills at most seven days (RECORD `:8198`; `docs/CONTRACTS.md:3226-3227`);
  and the INCONCLUSIVE path into paper is `U-referee-v2`'s, not built (`docs/EDGE-FACTORY.md:214-216`). The runner matters once a home has recorded weeks of
  tape — the switch-on, where seat A's order already puts it second.
- **The decoupling helps the canon's own measurement.** If attempt 4 runs before `U-canon` lands, its record is the old canon's run; R21 § 3.3's comparison
  (old canon against new, same budget) can use it or pair two runs — `U-canon`'s CARD decides. INFERENCE.
- **The start procedure armed the fleet in a meeting.** FLEET § The orchestrator, *Starting a session*, began "arm the heartbeat, first" with no condition; on
  2026-10-08 evening a planning meeting armed the heartbeat and opened two plan-only seats, stood down by the owner (~22:20, "we're just having a meeting").
  Nothing was dispatched and nothing remains running but a CI waiter on `b29dcead` (RUN: `git worktree list`, `git cherry`, `ps`, `git for-each-ref`,
  `crontab -l`, the app's session and scheduled-task lists; BUILD-STATUS quotes them). Now conditional.

## 4. Decisions — taken on the owner's behalf (his to overrule)

1. **One current order, in ORGANISATION § 15 only;** every other statement points there; a seat's manager reorders its own lane by rewriting its row before
   the dispatch that depends on it (his grant: managers decide everything inside their lanes), the order across seats is the orchestrator's, and a brief
   that disagrees with § 15 is fixed in the docs before dispatch.
2. **VISION § 6.7 follows the canon he named:** two axes (five domains × three worlds); an agent may hold a provisional belief with its provenance and its own
   statement of how sure it is (a record field, never a gate); an entry stays the agent's claim, and a measurement the app's record linked to it; the ledger
   stores references, never copies; unknowns stay unknown; the observed world's integrity is app-mediated until containment, and says so.
3. **M0 attempt 4 needs `U-quiet-review` and `U-memory-kept`,** and runs when he says testing may start — in a fresh home or with months held after the old
   window, at a GO sha pinned to the newest green `main`, with his two clicks, under seat M's handoff. Whatever else has landed by then, it runs on.
4. **Seat B's row is written in its own two-slot form** — the three layers, with the perception chain in its second slot beside them — as the seat planned
   on 2026-10-08 (its status file, 21:53); R21 listed one chain. The seat's manager may change it (decision 1).
5. **Labels where the claims are made:** Linux unverified until `U-linux-host`'s survey; the public rule a policy whose move precedes its first need; the
   4 % a tape-coverage figure on this Mac, with its period.

Nothing here reinterprets the owner's words or overrules a decision of his: § 6.7 returns to the canon he pointed to ("the ai concierge project
inspirations"); the order and M0's prerequisites were the orchestrator's decisions on his behalf (R21 § 3); "wait a bit before testing" and "public until the
absolute point" stand as he said them. So no question was put to him. The meeting's own questions (R21's proposal, the absolute point's bar, the host,
when testing starts) remain his. Under his decision on the order of work this record changes two things: M0 attempt 4 waits only on the two lights, and
seat B's perception chain runs in its second slot beside the three layers instead of after them.

## 5. What changed

Docs only — no product code, test or build. `docs/VISION.md` (§ 6.7, "Where it sits", the "Folded into units" heading); `docs/ORGANISATION.md` § 15;
`docs/EDGE-FACTORY.md` § 9 (the waves line, phase 4, the switch-on); R21 (an amendment banner and marked corrections); `docs/RESUME-HERE.md`;
`docs/HOW-WE-BUILD.md` (the dispatch check); `docs/FLEET.md` (the order's one copy; the start condition); `CLAUDE.md`; `BUILD-STATUS.md`; the queue:
`U-quiet-review`, `U-memory-kept`, `U-red-says-why` (seat B), `U-decision-port` (RE-BRIEF), `U-org-principals` and `U-org-envelopes` (`perception` carried
forward), `U-org-wakes` (RE-SCOPE). Outside the repository: FLEET `handoff/ORCHESTRATOR.md` (its next-session list rewritten; the earlier one kept in
`tmp/`), `handoff/A.md`, `handoff/P.md`, `handoff/M.md`, `charters/B-agents.md`, `status/A.md`, `status/M.md` and `BOARD.md`.

## 6. Open, and how each closes

- Does the deliberate canon beat the accreted one? → R21 § 3.3's comparison, `U-canon`'s CARD, on the owner's word to test.
- Does the app run unattended on Linux? → `U-linux-host`'s survey, then the switch-on on his word.
- Can a codex seat run confined with its login outside the cell? → `U-contain-seats`' first item, on the ATAS box.
- Where exactly is the absolute point? → R21 § 4's proposed bar, his to confirm; the move to `TradeAgent-Org` precedes the first record it would protect.
- Does the research ledger stay a layer of claims over the app's records? → `U-research-ledger`'s tests: a "measured" mark or a link the agent wrote itself
  is refused, and a confidence it did not state is recorded as unknown, never refused.
