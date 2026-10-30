# R26 — The roadmap from the MVP to the end vision (2026-10-31)

**What it is.** The owner asked on 2026-10-30 for the build to go "full speed towards the end vision adn MVP" (R25, verbatim there). R25 defined the
MVP and its gaps; this record lays out what comes after it: where the build stands against `docs/VISION.md` § 11's growth path, the units from the MVP
to stage 4, what the 72-hour run (M-org1 sitting #2) must measure to order stage 2, and what is the owner's. Sources: `docs/PRINCIPLES.md`, VISION §§ 0–15,
`docs/ORGANISATION.md` §§ 6, 8, 15–17, `docs/ORCHESTRATION.md` §§ 2.13–2.16, 6–7, 10–11, `docs/EDGE-FACTORY.md` §§ 4, 9 (only to tell his lane from ours),
R24 (`docs/research/2026-10-23/`), R25 (`docs/research/2026-10-30/`), the fleet's three surveys of main `997365c4` (`tmp/MVP-2026-10-30/`), and
`git log 997365c4..origin/main` at `843a03f5`.

**Labels** (as in R24): RECORD = `BUILD-STATUS.md` or a landing commit; SOURCE = a survey's `file:line` at `997365c4`, read and not run; INFERENCE = this
file's reasoning. A unit line reads: purpose · the protection it touches · size (S < one builder pass, M ≈ one, L = several) · what it waits on. Every unit
below is DESIGN; this file grants no authority, marks nothing built and orders nothing a later measurement contradicts.

**Not ours: the agents' head-start lane.** The owner builds it himself — information sources, perception (lenses, decision models, annotators) and the
agents' tools and skills ("i'll build that myself and hand it to you later", R25). No unit below builds any of it. The kernel builds only what his kit
plugs into: the socket (`U-kit-socket`, in flight), the admission ladder that decides what may feed evidence, and the departments that open around what
his kit brings (§ 3.2).

## 0. Verdict

1. **Stage 1's mechanics are built and green in CI on puppet seats; stage 1's proof is not.** The chart, rights, envelopes, wakes, lanes, mandates,
   decisions and forecasts, packets, verbs, the watcher, the audit line's first part, the surface, the chief, the board as data and acts, scorecards and
   Linux cells all landed (RECORD, ORGANISATION § 15). One real-model turn on an org seat is recorded — the chief, gpt-6-luna, exit 0, 2026-10-30 22:51Z
   (`docs/RESUME-HERE.md`, sitting #1). No head or executor has turned, and verdict → paper → fills has never run on any seat.
2. **MVP-L is code-complete but for four legs in flight** (`U-loop-e2e`, `U-runway-fixes`, `U-seat-models`, `U-kit-socket`); R25's other code gaps closed
   on 2026-10-30 (§ 1). **MVP-W** waits on the Windows cell provider and its proof, and on a release that reaches the laptop.
3. **Stage 2 is further along than its DESIGN label:** the proving ground's parts a and b and the scorecards' parts a and b (forecasts scored against a
   measured base rate; a cut rule raises a review answered only by a record) are landed. Missing: worker replacement, the seat shadow, real-model proving
   configurations, the paired-run machinery, and a forecast cadence that can reach 50 scored forecasts per manager in weeks rather than quarters.
4. **Stage 2's order is the 72-hour run's to decide** (§ 4); § 3.1 is the default if the run shows nothing that reorders it.
5. **Stages 3 and 4 are design almost throughout, and real money waits on the capital track** (§ 3.4) whatever stage the organisation reaches: R24 closes
   live until `U-board-live` and `U-runner-live` land and the owner connects a venue.

## 1. Where the build stands against VISION § 11

- **Stage 0 — LANDED** (VISION § 11).
- **Stage 1 — built in CI, unproven on a real model.** Landed per ORGANISATION § 15 and RECORD: O1–O9, `U-org-packets` a/b, `U-org-verbs` a–e,
  `U-org-watcher` a/b (the owner's private line), `U-org-audit-a`, `U-org-surface` a/b, `U-org-chief`, `U-board` a1/a2/b1/b2, `U-settings-shed`,
  `U-org-scorecards` a/b, `U-cell-linux` a–d, `U-execution-environment`. The fast lane added (R25 § 5, wave 1–2): `U-ci-fast-fixes` `8a6acc49`,
  `U-cell-windows` probe `6e5cd301`, `U-chief-wake-once` `4b128c7f` (rung 52), `U-evidence-campaign` `9836e38b`, `U-org-canon` `5ddadc92` (canon v8),
  `U-org-autonomy` `3cb9abd9` + `d55784e4`, `tools/linux-ui.sh` `6e2df729`. M-org0 is M0 attempt 4 (2026-10-22): it produced nothing ("code-mode host
  is disabled"), the cause was fixed by `U-cli-code-mode`, and one real chat turn passed on 2026-10-25 (backlog survey § Last real-model run). The Linux
  host holds a recorded cell proof (15 PASS, 2 GAP, 2026-10-25), and on it the chief took the first real org-seat turn (one luna turn, 10 door calls,
  none refused; RESUME-HERE). **M-org1 has not run:** no charter, hire, mandate or executor turn by a real model. The puppet tests prove the arrows one by
  one; no test walks them end to end yet (`U-loop-e2e`, in flight).
- **MVP-L / MVP-W** (R25 § 2) are M-org1 observed for 72 hours plus the evidence loop closing; they are not VISION stages but stage 1's proof in product
  form. In flight besides the four legs above: `U-ui-pages`, `U-org-cards-1`, `U-evidence-breadth`, `U-backtest-parity`, `U-board-alive`,
  `U-cell-windows` (provider → proof), `U-windows-release` (a draft release only); then the 72-hour run and the Windows sitting.
- **Stage 2 — partly built.** Landed: `U-proving-ground-a` (the shadow home, SIM-001's store marks) and `-b` (planted edges, the three traps, the
  referee's own power, landed `a3ea9d2f`); `U-org-scorecards-a`/`-b`; position memory as the research ledger, decision log and packets (`U-research-ledger`,
  `U-org-packets-b`). READY: `U-proving-ground-c` (`docs/queue/`). Not built: a replacement flow, `U-seat-shadow` (CARD), `U-proving-ground-d`/`-e`,
  the paired run. The 90-day cut rule cannot fire within M-org2's horizon; the half-envelope review can.
- **Stage 3 — mostly design.** Built: the tape's sources (Binance context, archives, GDELT, chain sources — EDGE phase 1), the decision port
  `7a8f6043` and lenses `af1a7a6f` (nothing runs a lens), the board's store and acts on puppet seats, the private line's per-unit detectors. Not built:
  departments earned around a source, a per-department watcher (VISION § 5.2's second line), `U-org-audit-b`/`-c`, assurance model seats, a board
  that has sat, the invariant registry beyond VISION § 6.3's table.
- **Stage 4 — design.** The referee keeps ADDIS α-wealth per campaign (`U-referee-v2c` `1970f86a`) and scorecards read a unit's yield; none of the
  organisation's economy, metabolism or mind exists.

## 2. The owner's head-start lane, and where it meets the kernel

| His (no unit here) | What the kernel provides or keeps at the edge |
|---|---|
| Information sources the agents use (feeds, scrapers, data services) | `U-kit-socket`: a read-only kit folder in every seat home and cell, named in the canon (R25 D10) |
| Perception: lenses, decision models, annotators, event studies | the decision port and lens schemas (landed); the lens-boundary holdout card (EVID-001), owed before anything runs a lens |
| Agents' tools and skills | `U-admission-ladder` (§ 3.2) for what is offered to other seats or feeds evidence; an agent's own scratch stays free |

## 3. The units

### 3.1 Stage 2 — M-org2: does a manager layer pay on trading research (default order; § 4 may reorder it)

1. `U-lineage-check` — every artifact (version, run, delivery, ledger entry, forecast) records its author position and the inputs its attempt read; a
   conformance test fails on a writer that skips either · lineage (VISION § 7.2: what is not recorded at creation cannot be rebuilt), EVID-001 · S ·
   nothing (start now; it gates § 3.3's contribution ledger and templates).
2. `U-run-measures` — extends O8's scorecards with what the 72-hour run shows is missing for a paired comparison: admissible candidates per AI dollar,
   cycle time, turns per accepted delivery, idle-turn share, false discoveries (versions verdicted then retired), per unit and per arm · measurement and
   claim in separate tables; unknown never zero · M · the 72-hour run.
3. `U-forecast-cadence` — short-horizon predicates in O5b's closed vocabulary that the app resolves from its own records (a delivery accepted by a date,
   a backtest or verdict outcome, a paper run's state on a date), so 50 scored forecasts per manager fit in weeks · forecasts scored only from app
   records (measurement versus claim) · S–M · the run's forecasts-resolved-per-manager figure.
4. `U-proving-ground-c` (READY) — the proving host process, a configuration as data, the no-model floor and one puppet agent at equal budgets · SIM-001,
   BUDGET-001 (equal budgets are enforced envelopes), puppets only in a test host · M · proving ground a, b (landed).
5. `U-proving-ground-d` (CARD; fleet draft) — the rest of the first ladder as puppet configurations (an agent with a critic, a headed team), calibration,
   and the off-path exam · SIM-001 · M · part c.
6. `U-proving-ground-e` — real models in the proving process on confined seats only: flat against headed on synthetic markets, the off-path exam, scored
   in hours — the earning rule's first route-2 experiment (VISION § 3) · SIM-001 (advisory on any unconfined seat, so none), containment, BUDGET-001; a
   paid run on the owner's word (ORCHESTRATION § 2.14) · M · part d, `U-seat-models`.
7. `U-worker-replace` — an app rule (a stall, repeated failed checks, calibration below the base rate, an audit finding) or the owner retires an
   occupant's sessions; a fresh occupant starts from the position's charter, ledger, decision log and open mandates, never the transcript; trials,
   spend and open operations carry over (ORGANISATION § 8) · trial and α keys carry no role (restructuring resets nothing), BUDGET-001 · M · scorecards
   and packets (landed).
8. `U-seat-shadow` (CARD) — a candidate model or occupant works a seat's real mandates under its own minted principal with no effect-bearing verb and its
   own reserved envelope, scored on the same scorecard; the handover is staged, recorded and never silent · ORG-001, AUTH-002 (seat choice is the board's,
   the founding policy's until it sits), BUDGET-001 · L · `U-worker-replace`, `U-seat-models`.
9. `U-paired-run` — the flat-versus-headed run in the real app: two arms with one brief, equal AI envelopes, one window, one data set and one memory
   snapshot; no lateral note or ledger read crosses arms; each arm on its own evidence campaign so neither spends the other's holdout; the report composed
   by code · EVID-001, BUDGET-001, measurement versus claim · L · `U-run-measures`, part e (synthetic first, VISION § 3), `U-evidence-breadth`; paid, his word.
10. **Conditional on the run:** `U-broker-lanes` — if heads queue behind the one unconfined lane (`Lanes.cs:93`; broker seats count as unconfined), let
    MEDIATED broker seats (login outside the cell, shell-free door) count apart from unconfined shells, the number as policy data · CRED-001; the
    unconfined-seat ceiling is the owner's (R24 § 4.3), so his word first · M. `U-evidence-rate` — if verdicts still bind after `U-evidence-breadth`,
    more campaigns (pairs, timeframes), fresh data only (R25 D7) · EVID-001 · M. `U-cut-rules-policy` — if the half-envelope review never fires or a
    threshold is still a constant, move it into founding or board policy data · CHANGE-001, AUTH-002 · S.

**M-org2 itself is a sitting, not a unit** (ORGANISATION § 15): a worker replaced from position memory; a seat shadowed and decided by its scorecard; a
cut-rule review fired and answered; at least 50 scored forecasts per manager; the paired run; the off-path exam. Claim ceiling: whether a layer paid on
this build, plan and window — not profit.

### 3.2 Stage 3 — earned growth (the law in practice)

- `U-invariant-registry` — VISION § 6.3's table as data: id, statement, enforcing component, tests and enforcement class per seat type, shown on the
  organisation's and the owner's pages; a conformance test fails when an id names no test · the honesty rule; it creates no law · M · nothing (may run
  beside stage 2).
- `U-admission-ladder` — a tool, adapter or feature offered to other seats or feeding evidence, the owner's kit's included, moves registered →
  conformance-tested (a seeded look-ahead adapter must fail) → evidence-eligible → quarantined on a health signal (EDGE § 4.7) · EVID-001, INBOX-001, the
  kit read-only · M · `U-kit-socket`.
- `U-earning-rule` — a charter of a department, layer or standing seat cites app-checked evidence ids (a tape series with features, a family's
  candidates, a recurring load, a control finding) or a paired-run result, with a scored forecast, shown on the chart and vetoable; O6's throttles stand
  · measurement versus claim, CHANGE-001 · M · `U-paired-run` (route 2), `U-admission-ladder` (route 1, around the sources his kit brings).
- `U-org-audit-b` (CARD) — the audit line's budget (the owner's), seeded impossible assignments, canaries and catch rates · the accountability line stays
  app-owned, EVID-001 · M · audit-a (landed). Then `U-org-audit-c` (CARD) — the adversarial brief at consequential and board boundaries, audit agents
  dormant, the app-principal model gate · INBOX-001 · M · part b.
- `U-dept-watchers` — VISION § 5.2's second line: one routing identity per department over the per-unit detectors, appointed and funded by the
  accountability line, findings raised as review events the department's parent answers; no AI turn until a detector fires · no organisation verb
  charters, funds or re-seats a watcher; the private line stays invisible · M · `U-org-audit-b`, `U-earning-rule`.
- `U-harness-responses` (CARD) → `U-assurance-seats` — GPT-6 tool calls on the API harness, then investigations and the assurance line's model seats on a
  second vendor family · CRED-001 (keys are his), BUDGET-001 (a paid key's spend is his word) · L · a second vendor's key (§ 5).
- `U-watcher-board` (CARD, light) — the private line's detectors over the board: rubber-stamp rate, same-family confirmations, widening after a loss,
  allocations by evidence class; invisible to board seats · watcher invisibility · S · `U-board-alive`. **The board's first sitting** follows (a
  sitting): a chief's proposal, a widening confirmed by a blind second seat, a narrowing on one seat, the owner's veto used once, on paper · AUTH-002.
- `U-org-circuit-breaker` — on a fabrication finding or runaway spend, code puts the subtree to sleep with its memory intact; waking it is a two-seat board
  act or the owner's (VISION § 7.7 read through R24) · BUDGET-001, AUTH-002 · M · `U-org-audit-b`.
- `U-owner-letter` — the weekly one-page letter and the monthly pack composed by code, every number linked to its record, the chief's claims labelled and
  capped, the decision inbox with an attention budget and defaults (VISION § 8) · no prose crosses an authority boundary · M · `U-run-measures`.
- `U-org-templates` (CARD) — unit templates (charter, staffing, seat policies, playbook) recorded with lineage from the first unit; none cloned before five
  units × 90 forward days (ORGANISATION § 8) · measurement versus claim · S to record · `U-lineage-check`.

### 3.3 Stage 4 — the organs, each when a measured need appears

- `R-alpha-split` — a research check against R04's procedures that splitting α-wealth across units and routing earnings back keeps the global guarantee
  (VISION § 14) · EVID-001 · S · nothing. Then `U-alpha-tree` — α-wealth split down the mandate tree from the campaigns' ADDIS wealth; a discovery
  earns it back; the board divides and never widens · EVID-001, CHANGE-001 · L · `R-alpha-split`, `U-evidence-breadth`.
- `U-shadow-prices` — each binding currency's shadow price in packets and on the pages ("prices, not pleas") · measurement versus claim · S ·
  `U-alpha-tree`.
- `U-contribution-ledger` — a deployment's forward marginal contribution net of cost flows back along recorded lineage by fixed, versioned splits; debits
  for findings and AI dollars · measurement versus claim, simulations labelled · L · `U-forward-standing`, `U-lineage-check`, ≥ 90 forward days.
- `U-tranches` — the root mandate released in tranches by app-checked milestone predicates (EDGE M1–M6), each with a burn ceiling and a deadline; a miss
  shrinks the organisation to its standing minimum (positions dormant, tape and protection running) · BUDGET-001; the parameters are his · M ·
  `U-run-measures`. `U-retention` — base + r × realised live net profit, trailing, losses carried, never sizing exposure · BUDGET-001; risk stays in the
  slow layers; simulations never count · M · the capital track, `U-tranches`.
- `U-belief-graph` — typed beliefs (mechanism, regime, source, feature, family) the organisation writes and evidence summaries the app writes, versioned,
  with decay; doctrine v1 is EDGE §§ 2–3, owned by the chief · measurement versus claim · L · the research ledger (landed). Then `U-case-law` — decisions
  and their resolutions as precedents whose base rate reaches the decider's packet · M · scorecards-a (landed), the graph. Then `U-sleep` — consolidation
  when nothing is due, by code and the local decision port at no AI-dollar cost · BUDGET-001 · M · the graph.

### 3.4 The capital and selection track (crosses stages; nothing here waits on a stage, and no stage opens it)

- Selection plane (EDGE § 4.6, ours, parked since 2026-10-11): `U-backtest-parity` (in flight) → `S-runner-fixed-levels` → `U-runner-fixed-levels` →
  `U-forward-standing` → `U-incubator` · the money path on paper, EVID-001 · L in all · in that order.
- `U-board-alive` (in flight) → `U-board-live` (CARD; opus/xhigh) — mode, activation and allocation as two-seat board acts, the raise-pace guard, the live
  daily-loss gate re-added, arming only with two admitted board seats · AUTH-002, containment · L · the board's first sitting.
- `U-runner-live` (CARD) — the frozen runner trades a board allocation in a live mode · ORDER-002…005, SIGNAL-001, UNKNOWN-001 · L ·
  `U-runner-fixed-levels`, `U-board-live`. `U-ventures-a` (CARD) — ventures as data with a non-edge evidence class proposed · EVID-001 · M · board-b.
- Not planned here: the horizon (a firm of firms, an outcome network — VISION § 11), venues beyond the app's paper venue and ATAS (owner decisions).

## 4. What the 72-hour run must measure to order stage 2

The run (M-org1 sitting #2; fleet brief `tmp/FAST/M-org1-72h-run.md`) cannot answer whether a layer pays — that is M-org2's. It must measure three things,
each from the app's own records:

1. **Where the loop breaks on a real model.** For each arrow — charter, hire, self-activation, mandate, delivery accepted or rejected, backtest, verdict
   asked, paper allocation, fills back to the author, keep / kill / iterate — the first time it was reached and how often, and the first refusal or stall
   with its words. *Decides:* if verdict → paper → fills does not close, its fix comes before every stage-2 unit; a paired run of a broken loop measures
   nothing.
2. **What a turn costs and how fast turns come.** Turns per position per day; list-price cost per turn by seat; the idle-turn share; the wait of broker
   heads behind the one unconfined lane; vendor-limit refusals against the Plus windows (shared with his own use); the day-total consumed. *Decides:*
   whether `U-broker-lanes` and his funding decision come before the paired run, and whether two equal arms fit in the plan at all.
3. **How fast the run produces scorable signal.** Forecasts written and resolved per manager per day (so: days to 50 each); verdicts and trials spent
   against the campaigns' budgets; admissible candidates; findings a fresh session found again from the ledger. *Decides:* `U-forecast-cadence` and
   `U-evidence-rate` before or after the proving ground, the paired run's window length, and whether position memory is ready for `U-worker-replace`.

**Default reading (INFERENCE):** if all three hold, § 3.1's order stands; a broken arrow (1) pre-empts everything; a binding plan (2) brings
`U-broker-lanes` and his funding decision forward; slow signal (3) brings `U-forecast-cadence` and `U-evidence-rate` forward.

## 5. What is the owner's

**Decisions the roadmap surfaces** (defaults in brackets; R25's D1–D10 stay his to overrule):
1. Funding beyond his ChatGPT plan, once the 72-hour run has measured cost and output (VISION § 15 open 3) [the plan only].
2. A second vendor's key — its spend needs his word — which unlocks the assurance seats, model-layer watchers and two-family board confirmations [none:
   code-only watchers, a one-family board, said so in the record].
3. The unconfined-seat and lane ceiling, if the run shows broker heads queued (R24 § 4.3) [one lane, as built].
4. His word on each paid real-model proving-ground or paired run (ORCHESTRATION § 2.14); his standing go of 2026-10-31 covers the server's paper presses,
   not these [asked when `U-proving-ground-e` lands].
5. R24 § 9's open questions, still on their defaults; the roadmap reaches first the board's composition (Q6), the legacy discretionary live modes (Q9,
   closed), the raise-pace guard (Q11) and who appoints the chief (Q12).
6. Live money: whether and when to connect a live venue and fund it; the venue and his professional status (EDGE § 10, R16) [nothing real moves before
   `U-runner-live` in any case].
7. Tranche and retention parameters (VISION § 15 open 5) [none until a milestone ladder is proposed with evidence].

**Physical actions only he can take:** the Windows laptop (which machine, its edition) and its one UAC "Yes" for the 443 rule (R25 D2); publishing a
release (the workflow only drafts one); bringing the ATAS box online for an ATAS-enabled build; his own eyes on the organisation's pages (`U-ui-pages`
renders them headless; the screen check held for him is still owed); keeping his Linux server up and his sign-in valid; connecting a second vendor, a
live venue or a wallet; telling Mihael, once MVP-W holds.

**His kit:** information sources, perception and the agents' tools and skills (§ 2). It installs into `U-kit-socket`'s folder; stage 3's departments
open around what it brings, through `U-earning-rule`; `U-admission-ladder` decides what of it may feed evidence. Nothing in this roadmap waits on it
before stage 3, and no unit here builds it.

---

*Written 2026-10-31 by the R26 leg (read-only on code) from the sources named above, at main `843a03f5`. It establishes direction only: it marks no
feature implemented, no test passed and no boundary enforced, and it grants no authority.*
