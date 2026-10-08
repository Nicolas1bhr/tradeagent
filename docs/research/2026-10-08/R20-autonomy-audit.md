# R20 — An outside autonomy audit, verified against `main` and answered (2026-10-08)

**What it is.** On 2026-10-08, after the build's wind-down and before it resumed, the owner shared an outside, read-only audit — "TradeAgent AI Autonomy
Audit", reviewing `main` at `30fe7a32` — with the instruction "do not treat it as canonical nor authoritative, but rather useful audit". Its thesis: TradeAgent
is not yet a deterministic cage for its models, but the organisation design blurs **governing an agent's authority** with **governing an agent's
intelligence**, and should be corrected before the next organisational layer is built. This file records what the orchestrator's session did with it: every
factual claim checked against `main` `30fe7a32` by three read-only legs, the research the contested rules cite re-read, each recommendation answered, and the
owner's three decisions of the same day. The audit's text is not reproduced here; its sections are cited as "audit § n".

**Labels** (as in `docs/research/2026-10-02/README.md`): SOURCE = `file:line` at `30fe7a32`, read and not run (paths under `src/` unless stated); RECORD =
`BUILD-STATUS.md` line; R13 items carry R13's own labels (its rows are paraphrases of primary sources; its design rules D1–D20 are INFERENCE); FP = the owner's
2026-09-27 factory plan (outside the repo, read-only); INFERENCE = this file's reasoning. Nothing was built, run or launched.

## 0. Verdict

1. **The thesis is right, and the repo's own sources support it more strongly than the audit argued.** Each of the eight contested organisation rules is
   supported by the research it cites only in a narrower form (§ 2). R13's own rule was "executors choose the method, never the *mission*" (D5); ORGANISATION
   widened it to "never *what*". FP Law 1 forbids "a permanent corporate workflow, fixed chain of thought … mandatory reviewer ceremony", and ORGANISATION § 0
   superseded only its clauses on the organisation's *shape*. R14 § 3 asked the owner on 2026-10-02 to "confirm that 'choose nothing' means capital, risk and
   verdicts …, not research"; the question was never recorded as answered. The owner answered it on 2026-10-08 (§ 5).
2. **On facts it is mostly right, and it overstates what matters least** (§ 1): the API harness it calls severely limited has never made a real provider call;
   both roles run the codex CLI with a full shell. Its M0 figures are exact, but "three strategies rejected" was three versions of two ideas, retired by the
   agents on their own pre-holdout backtests.
3. **Two defects are worse than it says:** a refused plan or journal is destroyed whole, and the queued fix for the idle-review cost would not have fixed it
   (§ 4). Both are now READY briefs.
4. **It misreads SIGNAL-001** as a choice about latency, cost and trust. The protected property is evidence binding, and model judgement already reaches
   trading as recorded, pinned instrument answers (§ 3, row 6.8). Kept and restated.
5. **Its most useful engineering point is containment** — a confined cell that keeps the CLI's freedom, not a smaller tool list. Containment now precedes
   the governance verbs, and its first unit must keep the seat's working freedom (§ 5).

## 1. The audit's factual claims, checked

| Claim (audit §) | Verdict | Evidence |
|---|---|---|
| The CLI agent may create files, write and run code, install packages, use the shell and the internet (§ 2) | TRUE | `AgentRuntime/WorkspaceBuilder.cs:196-198` "You may create files, write and run code, install packages, use the shell and use the internet." Limits: the inbox read-only (`:201-202`); no local models or heavy services (`:501-503`); the other role's folder "a convention, not a wall" (`:676-679`) |
| Role homes, folders, AGENTS.md, `context.json`; nothing wiped between turns (§ 2) | TRUE, small corrections | `inbox/` once at the root (`:74`), not per role; per-role subfolders `:32-33`, `:85-89`; AGENTS.md and the reference files are rewritten on each start; nothing deletes agent files |
| The API harness has six tools and no shell, HTTP or packages (§ 2) | TRUE — but it binds a path nobody runs | `AgentRuntime/GrantedWorkerTools.cs:49-54`, `:16-17`, `:150-151`. Operations always runs the app-wide CLI (`App/AppHost.cs:202-204`); Research moves to the harness only if an API key is pasted (`:205-206`); the harness has never made a real provider call (RECORD `:5315-5316`; `RuntimeManifest.cs:691-694` "UNVERIFIED"); the owner's runs were codex on the plan for both roles (RECORD `:6802-6804`, `:6818`) |
| The harness's `write_file` reaches only `trading/` and `out/` (§ 2) | TRUE, and missed a consequence | `GrantedWorkerTools.cs:74`, `:229`; `ToolPaths.cs:71-77`. It cannot write `.tradeagent/next.json`, so a harness worker cannot ask to be woken; and its system prompt is the same AGENTS.md (`ApiConversation.cs:469-476`), which promises it a shell, the internet, `scripts/` and `next.json` — an honesty defect, owed by the harness's first real seat (ORGANISATION § 15, `U-harness-responses`) |
| The scheduler's wakes and the Situation (§ 3) | TRUE, list incomplete | twelve wake kinds, `AgentRuntime/MissionLoop.cs:2252-2269`; the Situation `:658-752` |
| Plan 60 / journal 200 lines; an over-cap version is replaced by the last accepted one, with a notice (§ 3) | TRUE — and the loss is total | only the valid branch records content (`AgentRuntime/WorkspaceRevisions.cs:102-118`); the refusal records a line count and a revision number (`:125-140`); after the commit the last accepted revision is written over the file (`:151-160`, from `CouncilRelay.cs:189`); the post-turn scan keeps hash and size only (`Core/Db/Database.cs:223-234`). A refused journal loses that turn's new entries; a first-ever over-cap file stays on disk unrecorded; the caps count lines, not bytes |
| Handoffs 40 / 20 lines, artifacts by reference (§ 3) | TRUE | `CouncilRelay.cs:57`, `:60`, `:626`, `:632-637` (validation counts lines only, so paths may be cited); app ids work across roles (`Gateway/GatewayPipeServer.cs:2471`) |
| No app-level delegation exists (§§ 2, 4) | TRUE | no delegation, child or spawn path in `src/`; one turn at a time per role (`MissionLoop.cs:1714-1729`). The CLI's own helpers, or a second CLI from its shell, are not blocked (no sandbox) and end with the turn's process tree (`U-agent-tree`, RECORD `:9008`) |
| `OrgStore` is read-only: a root, two divisions, the two legacy positions (§ 4) | TRUE | `Core/Db/OrgStore.cs:159-163` "READ — and only read: there is no public writer yet"; seeded at `Database.cs:1904-1908`; nothing in `src/` constructs it yet |
| The CLI runs with sandbox and approvals bypassed (§ 8) | TRUE (codex) | `AgentRuntime/RuntimeManifest.cs:633` `--dangerously-bypass-approvals-and-sandbox`; `:625-632` the stricter mode skipped as untested on Windows; OpenCode `--auto` (`:486-487`) |
| No OS sandbox, and the app says so (§ 8) | TRUE | `AgentRuntime/Containment.cs:43-45`, `:30-31` |
| An unconfined CLI is refused only when live is chosen AND armed (§ 8) | TRUE | `Containment.cs:62-65` refuses when `ModeIsLive && LiveActivated && !Sandbox().Ok`; paper and observe untouched (`:50-57`); the harness path has no such check (it runs no code) |
| M0 attempt 3: 36 turns, 3.84 of 5 USD, three strategies rejected, no verdict, ≈ 2.03 USD (53 %) on 16 review turns after the last useful work (§ 4) | numbers EXACT; three characterisations off | RECORD `:8807` (36 turns, 3.8444 USD), `:8816-8817` (2.0337 USD, 53 %, "No agenda or wake is pending"). Three VERSIONS of two ideas (`9677ccc7` is `ef4969c5` with only the target changed, `:8802-8803`); retired by the agents on their own pre-holdout backtests — "No verdict has been spent" (`:8806`) — not by the referee; the dollars are the app's meter pricing plan tokens at gpt-6-luna list price, not a vendor bill, a distinction `docs/PRINCIPLES.md` keeps |
| The organisation documents prescribe "choose how, never what", instruments for every decision, no lateral channel, mandatory consults, admission for every change (§§ 5–6) | TRUE | ORGANISATION §§ 1–2, 4, 7 and VISION §§ 2, 6.1, 6.6 as they stood at `30fe7a32` |
| The 2026-10-02 design deliberately superseded FP's prohibition on prescribed roles (§ 1) | TRUE, narrower than implied | ORGANISATION § 0 superseded only Law 1's shape clauses (roles, a role count, a delegation tree); "no permanent corporate workflow, fixed chain of thought … mandatory reviewer ceremony" were never superseded |

## 2. The research behind the contested rules

| Rule (ORGANISATION at `30fe7a32`) | Cited | What the source supports | Verdict |
|---|---|---|---|
| Executors choose how, never what | R13 D5; the owner's words | D5 itself: "choose the method, never the mission". No item measures following an unexpected finding; MAST (F1) counts spec disobedience 11.8 % and derailment 7.4 % of failures — frequencies, not a rule's effect | NARROWER: never change the objective or overspend; nothing supports limiting investigation inside the envelope |
| Managers decide from instruments | R13 D7, H3 | H3 (preprint, 86 paired runs, one task type): a reject-and-revise manager on opinion lowered utility (d 0.42) and added 51.5 % tokens. H1's 4.4× vs 17.2× compares architectures, not instruments | NARROWER: no opinion-only revision loops; never tested for opening or branching work |
| Consult the advisor before substantive work and before finishing | R13 X15 | vendor docs: a cheaper executor closed ≥ half the gap "while it kept asking"; near-equal pairs gained nothing; at low effort consults collapsed | NARROWER: a default for a much weaker executor, measured (U10) |
| Assignments of one to two hours | R13 X1 | METR's public-frontier 80 % horizon ≈ 1.5 h on software tasks, about 2× either way (X2); a capability measure, not a sizing experiment; executors run a weaker class; the horizon on trading research is UNKNOWN (U12) | NARROWER: a starting default, recalibrated |
| Fresh retry; two failures and the head re-scopes | R13 D6, X11 | fresh context and re-scoping supported (X11, O7, F3); the count of two and the failure note unmeasured | NARROWER: defaults |
| Delivery ≤ 20 lines, verbatim | R13 D10, H9 | verbatim forwarding helped ≈ 50 % (H9); D10 says ≈ 2k tokens; "20 lines" is the build fleet's own report convention; summaries versus traces contested (U3) | NARROWER: by reference, never paraphrased; the size is a convention |
| No channel outside the chart; everything else through the chief | R17 #16 | #16 is about who PAYS for service work. Side channels (S6: ≈ 1,200 agents, > 70,000 messages through a shared cache, the scorer targeted; O5 collusion) and injection spread (S1 58–90 %; S2: tagged sources slow it) argue for relayed, stamped channels that carry no authority — not for routing all traffic through the chief; H13 and O2 favour lateral flow | NARROWER: relayed, stamped, authority-free |
| One accountable decider, no votes | R13 D4, H5 | H5: teams fell up to 41 % below their best member yet were more robust to adversarial members; H6: majority voting explains most of debate's gain | NARROWER: no committees; one decider who receives adversarial briefs |

**For the audit's thesis:** R14 § 3's unanswered question; R14 § 4.7 "Telling a model not to is not a control"; O5 (organisational prompts changed little
next to model choice); H11 (a well-prompted single agent matched elaborate systems); M7 (self-evolved structure beat hand-designed); X10 (a critic collapsed
one model by 26 points); agent-made tools (O1, M1, M6). FP § 6 gives the model "what hypothesis to investigate", "how to decompose reasoning" and "code/tool
creation"; FP § 15 starts every tool as an ordinary workspace artifact, and Law 6 says "Agents may freely damage their own scratch state". **Against it:** X6
(agents that succeed 22 % of the time predict 77 %), X1 (≥ 16 % of hard-task successes illegitimate), X4 (reward hacking when the scorer is visible), S6, O5's
monoculture, O4 (enforced procedures helped in one shop), O1 (50 subagents for simple queries). INFERENCE: nearly all of the evidence against justifies
controls on scorers, evidence, authority, spend and shared state — which stay — not on reasoning; the few items about how work is done support defaults and
budgets, not prohibitions.

## 3. The recommendations, answered

| Audit § | Recommendation | Answer | Where |
|---|---|---|---|
| 11 | "Autonomy is the default inside authority" as an authoritative principle | ADOPTED, condensed — in PRINCIPLES, which outranks VISION and ORGANISATION, so they are checked against it | PRINCIPLES (new section); "The organisation" |
| 6.1 | Executors own methods, investigative direction and reversible actions; managers own the mandate and resources | ADOPTED as the owner's reading, with one condition the overconfidence evidence (X6) earns: answer the assigned question or say why not, and name every deviation and its cost; a head may mark an assignment exact | ORGANISATION §§ 1, 2, 4; VISION §§ 2, 6.1 |
| 6.2 | Mandates where responsibility, money, an effect or an evaluation crosses; free inside | ADOPTED; leaf mandates are tasks or explorations | VISION § 6.1; ORGANISATION § 4 |
| 6.3 | Evidence to claim ≠ evidence to investigate | ADOPTED, narrowed: "ask for an instrument" already funded the cheap probe; "branch" no longer needs a measurement id, and exploration is paid from the exploration share | ORGANISATION § 1 rule 3, § 8; PRINCIPLES |
| 6.4 | Consults and retries as defaults | ADOPTED | ORGANISATION §§ 4, 7 |
| 6.5 | Information lateral, commitments not | ADOPTED with conditions: app-relayed, sender-stamped, logged, wakes nobody, carries no authority; the audit line, the watcher and protected evidence outside every grant | ORGANISATION § 4 |
| 6.6 | Protect speculative work from short-horizon proxies | LARGELY HELD (a 25 % exploration floor; acceptance rates are diagnostics only; trial deflation prices candidate mills, EDGE § 4.5), clarified: the floor needs a rationale, not a measurement, and is read apart | ORGANISATION § 8 |
| 6.7 | Tiered self-improvement: private tools free, shared ones governed | ADOPTED; already held at the bottom rung (EDGE § 4.7 starts at scratch; FP Law 6, § 15) — VISION § 6.6 had over-reached | VISION § 6.6 (a five-tier table); EDGE § 4.7; ORGANISATION § 8 |
| 6.8 | Decide whether SIGNAL-001 is permanent or a default | KEPT PERMANENT, restated (the owner). The audit's reasons miss the protected property EDGE § 6.1 names: what trades is the frozen, measured computation. Model judgement already enters as recorded, pinned instrument answers (COUNCIL rule 8 read precisely), a frontier model may become one when a version needs it (EDGE § 4.2), and an agent's view of a trade enters as a new version; a model's decisions over history cannot be cleanly backtested (R07 § 5.5) | VISION §§ 6.3, 9 |
| 2, 8 | Close the gap with a capable, confined cell, not a smaller tool list | ADOPTED — already the plan (`U-contain-seats` → `U-execution-environment` → `U-capability-broker-sandbox` → `U-creative-api-worker`); now FIRST in lane B after M-org0 (the owner), and `U-contain-seats` must keep the seat's shell, code, packages, permitted network and runtime helpers | ORGANISATION § 15 |
| 3 | Never lose oversized new work | ADOPTED — a defect against PRINCIPLES' own memory rule | `docs/queue/U-memory-kept.md` |
| 3 | Treat a short handoff as an index to richer work | ADOPTED (wording) | ORGANISATION § 4 "Delivery" |
| 4 | Fix wake scheduling, not cognition | ADOPTED — the queued fix did not fix it (§ 4) | `docs/queue/U-quiet-review.md`; `U-org-wakes` amended |
| 9 | Probabilistic assistance, deterministic authorisation | ALREADY HELD — System One answers in shadow first, autonomy one question at a time in the saving direction, never an authority, verdict or signal (ORGANISATION § 11) | — |
| 10 | Delegation: agents launch bounded helpers within their envelope | ADOPTED (PRINCIPLES' delegation section already said so): child attempts inside the executor's envelope and capability subset, never positions | ORGANISATION § 2 |
| 10 | An autonomy regression test | ADOPTED: a ninth test of the end state; three mechanics in M-org1's claim; the off-path exam in the proving ground for M-org2; the latitude itself measured | VISION §§ 1, 6.4; ORGANISATION §§ 15, 17 |

## 4. What the audit missed

- **The idle-review fix would not have fixed it.** `docs/RESUME-HERE.md` assigned attempt 3's 53 % to `U-org-wakes`, whose observable result is "the
  legacy pair keep their 30-minute review" (brief `:10`, written before attempt 3). The turns come from `MissionLoop.Schedule` raising a review per role every
  `ReviewEvery` (`MissionLoop.cs:1968-1974`; 30 minutes, `Core/Trading.cs:476`) whatever the role asked for: its own `next.json` wake (`:2336-2352`) adds a
  wake (`:2190-2205`) and suspends nothing. INFERENCE: 16 ≈ two roles × about four hours ÷ 30 minutes. → `U-quiet-review`, light, before M0 attempt 4.
- **The over-cap loss is total** (§ 1), against PRINCIPLES: "a short summary limit is not a reason to discard the underlying research … recoverable output
  over silent truncation or destruction". → `U-memory-kept`, light, before M0 attempt 4.
- **The harness worker is told it has tools it lacks** (§ 1) — owed by the harness's first real seat.
- **R14 § 3's question and FP Law 1's surviving clauses** (§ 0) — the repo already held the audit's strongest argument.
- **`R-containment` left the hard part unproven:** codex and node do not start bare in the chosen AppContainer, and a credential placed inside it is
  readable (`docs/research/2026-10-06/R-containment-decision.md`), so a confined codex seat needs its login held outside the cell. That is
  `U-contain-seats`' first item, not a detail. ORGANISATION § 15 and EDGE § 9 still listed `R-containment` as READY; corrected. Source comments still call
  the unit `U-contain-2` (`Containment.cs:34` and four other files) — for the unit that touches them.
- **The governance verbs would land advisory.** ORGANISATION § 2's code-enforced verbs are "ADVISORY against CLI seats until containment", and every plan
  seat is a CLI seat — the argument that settled the sequencing (§ 5).

## 5. The owner's decisions, 2026-10-08 (asked with the recommendation first; all three recommended answers chosen)

1. **"Agents shouldn't choose anything" means authority, not thinking.** Agents never choose the structure, their mandate, money, risk or verdicts —
   managers, code and the owner do. Inside its mandate and envelope an executor owns its reasoning: methods, tools, hypotheses, and which findings to follow;
   it answers the question it was given or says why it could not, and names every deviation and its cost; a head can lock an assignment to its exact
   question. Landed in PRINCIPLES, ORGANISATION, VISION, EDGE § 4.7, `CLAUDE.md` and `manager-prompt.md`.
2. **Containment first.** After M-org0, `U-contain-seats` then `U-execution-environment`, before the assignments, verbs, watcher, surface and chief; a
   confined seat keeps its working freedom. This changes the order the owner kept on 2026-10-06, not the units; the waves to M-org1 stay five.
3. **SIGNAL-001 stays permanent, restated** around its protected property.

## 6. What changed

Docs only — no product code, test or build. `docs/PRINCIPLES.md`; `docs/ORGANISATION.md` (header, §§ 0–2, 4, 7, 8, 15–17); `docs/VISION.md` (header, §§ 0,
1, 2, 5.1, 6.1, 6.3–6.6, 7.5, 9, 11, 12, 15); `docs/EDGE-FACTORY.md` (§§ 4.7, 9); `CLAUDE.md`; `manager-prompt.md`; `docs/RESUME-HERE.md`; `BUILD-STATUS.md`;
the queue: `U-memory-kept` and `U-quiet-review` (new, READY, light), `U-org-wakes` (amended to build on `U-quiet-review`).

## 7. Open, and how each closes

- Does the latitude pay, or leak budget into what executors find interesting? → named deviations with their cost and outcome, recorded per delivery from
  M-org1; the off-path exam (M-org2); a head narrows latitude on the record (ORGANISATION § 17).
- Can a codex seat run confined with its login outside the cell, keeping its freedom? → `U-contain-seats`' first item, on the ATAS box.
- Do lateral notes spread injected text? → every note is stamped and logged; the watcher's message-graph detectors (VISION § 7.7) once they exist.
