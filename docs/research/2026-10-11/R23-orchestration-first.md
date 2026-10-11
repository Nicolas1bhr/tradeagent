# R23 — The in-app orchestration first: two surveys, the architecture, its review, and the owner's decisions (2026-10-11)

**What it is.** On the afternoon of 2026-10-11, after the fourth speed-mode session's close, the owner re-prioritised the build: "we need to have the
orchestration dialed in, as well build out as can be. this is a priority over all the others. the vision should be clear enough but this is still outside
of the box thinking since those processes are not really a genuine part of your training. … so now lets start arching out that genuine in app ai
orchestration and get it right from the get go. it's really important for this one that you ground yourself in the vision and other documentation." This
file records what the orchestrator's session did with it: it read the canon in full (PRINCIPLES, VISION, ORGANISATION, EDGE-FACTORY, COUNCIL, R20–R22,
HOW-WE-BUILD, FLEET; the owner's 2026-09-27 factory plan, his 2026-10-06 interface architecture and the outside review of 2026-10-09 he keeps outside the
repository); ran two read-only surveys of `main`; wrote `docs/ORCHESTRATION.md`; had its draft attacked by Codex `gpt-6.1-sol` and by a fresh Opus
reviewer, read-only, before landing; asked the owner the two questions only he could answer; and re-sequenced `docs/ORGANISATION.md` § 15. Docs only: no
product code, test or build.

**Labels** (as in R20–R22): SOURCE = `file:line` at `main` `fd7174ee`, read and not run (under `src/`); RUN = a command run on 2026-10-11; RECORD =
`BUILD-STATUS.md`; INFERENCE = this file's reasoning.

## 0. Verdict
1. **What runs today is a well-protected two-role loop, not yet an organisation** (ORCHESTRATION § 1): `CouncilRoles.All` is a hard-coded pair
   (`Core/Council.cs:41`); an unknown role folds into the chair's slots and folder (`:53`, `:80`); mission dispatch is serial (`AgentRuntime/MissionLoop.cs:1547-1583`),
   the owner's chat the one overlap; the org ledger is read by nothing; no attempt has a parent; nothing is confined. Its strongest primitives — spend
   reserved before launch, wakes as deduplicated rows consumed in one transaction, a grant per launch, an attested inbox, a generated canon, beliefs as
   claims — are what the kernel generalises.
2. **A protection gap, found by reading and confirmed by a run.** The app never sets `CODEX_HOME` and disables no feature (`AgentRuntime/RuntimeManifest.cs:499-502`,
   `:586-592`, `:649`; `AgentEnvironment.cs:78, 87`), so a codex seat on the default home runs under the owner's personal codex configuration. RUN (16:18
   CEST, this Mac): a `codex exec` (0.160.1) started under that home — the fleet's own review leg — spawned, as its children, the ChatGPT app's computer-use
   MCP server (`node_repl`) and two plugin MCP servers; that home also enables plugins for his browser, GitHub and his hosting provider, a notify hook and a
   global AGENTS.md. Which tools a SEAT's launch offers its model is NOT VERIFIED; that the processes start is. A seat launched with approvals bypassed that
   held them could reach his in-process controls through the screen (AUTH-001), drive any user interface (ORDER-005) and use services that are his
   (CRED-001). → `U-seat-home`, first in seat A, and a prerequisite of any further observed run.
3. **A second, older gap, found by the review and confirmed by reading:** spend is reserved before launch except when the reservation cannot be written —
   `TurnMeter.Begin` turns ANY exception into `AiAdmission.Unrecorded` (`AgentRuntime/TurnMeter.cs:874`), whose `Admitted` is true by design ("a launch the
   app failed to write down is not a launch the app may silently decline to make", `Core/Db/AiAttemptStore.cs:164-181`). With a broken store, launches run
   unmetered past every cap. → `U-admission-fail-closed`, a light protection before O4.
4. **The architecture answers "build out as can be" and VISION's "earn it" together:** build the whole kernel, activate by measurement — an app-checked
   earning test on every structural verb and the proving ground built with the kernel, whose first ladder is the outside review's own question (a
   deterministic search, one agent, an agent with a critic, a headed team, equal budgets).
5. **Containment stops being the organisation's bottleneck:** decision rights are exercised through the broker and never rest on an unconfined shell;
   code runs only in cells, at any level; the owner put the first cells on Linux; the work plane lands in parallel and a per-capability activation gate,
   checked fail-closed before every launch, decides what may run.

## 1. The surveys (2026-10-11, read-only, at `fd7174ee`)
Two Opus legs mapped the agent half of the code: runtimes, hosting and the pipe; scheduling, wakes, memory and the organisation substrate. Their facts
with file:line are ORCHESTRATION § 1's table. What shaped the design most: every live key assumes exactly two roles (`CouncilRoles.All`, `Or`,
`HomeDir`, `IsKnown`, routing by role name, `RoleObjectives`' kinds, the 1/N share, one minted-id slot in the meter, boundary release by every role);
the material pass's quiescence is an evidence protection (`MissionLoop.cs:1350-1382`; COUNCIL rule 7); the Situation has no size cap; the three org
briefs in `docs/queue/` keep valid premises with drifted pointers, `U-org-wakes`' is history; and one INFERENCE, not run: while two roles turn, a same-user
seat could read the chair's grant from its environment and present it through the real `trade` (`Security/PeerImage.cs:101-108` concedes same-user
impersonation of `trade`) — the reason concurrency waits for `U-grant-tree` and cells.

## 2. The review of the draft
**Codex `gpt-6.1-sol`, read-only, high effort: 48 findings — 22 HIGH, 24 MEDIUM, 2 LOW — "not ready to become doctrine".** It was right; the draft was
rewritten before landing. Every finding is ADOPTED unless the row says otherwise.

| Ids | What the review found | Answer | Where now |
|---|---|---|---|
| A01, A05, A06 | "one turn at a time" ignored the owner's chat overlap; "36 resumed turns" (36 turns, 216.6 M of 224 M input tokens cached); a citation that did not say what it was cited for | ADOPTED | § 1; § 0.5 |
| A02, A03 | "every seat runs on the owner's `~/.codex`" overstated it (an inherited `CODEX_HOME` is kept; OpenCode and the harness do not use it); tool exposure was not shown by source | ADOPTED, NARROWED: scoped to codex seats on the default home; the RUN shows the MCP servers start; exposure to a seat's model NOT VERIFIED until `U-seat-home`'s inventory | § 1; § 2.7; § 11 |
| A04 | admission fails open when the reservation cannot be written | ADOPTED — a unit of its own | § 2.4; `U-admission-fail-closed` |
| B01, B02 | the doctrine's limits on `operations` (no instrument, side, size or timing; no order permission when working another's mandate) and the reserved identities were missing | ADOPTED | § 2.2; § 2.9 (one rights matrix) |
| B03 | the forecast resolver dropped ORGANISATION § 5's precommitment, private-backtest and held-figure rules | ADOPTED | § 2.11 |
| B04 | "the whole CLI in a cell with a revocable credential" still exposes the owner's account | ADOPTED — only with a provider-supported, narrower workload credential | § 2.8 |
| B05 | § 8 left capital, pause/stop/flatten, the perception cap, version refusal, the armed-live refusal and ORDER-002…005 unmapped | ADOPTED — § 8 is a full mapping | § 8 |
| C01, C02 | the legacy exception defeated the gate; assurance checked only at activation; today's bare-start fallbacks | ADOPTED — a per-capability gate, fail-closed before model work on every adapter path; no degraded start for assurance-required work; the legacy pair keep only their existing behaviour unconfined | § 2.3 |
| C03, C04, C05 | process binding needs a bootstrap; it does NOT stop a determined same-user process; a Unix session is not a process tree | ADOPTED, the claim narrowed to cross-container replay; Linux binds by per-attempt cgroup, Windows by job object, macOS weaker and said so | § 2.3; § 5 |
| C06, C07 | triage retries had no durable counter, no escalation at half the allocation, and hid failed checks | ADOPTED — retry policy, count and cost on the mandate; every failure kept; a review of the check on repeated identical failures | § 2.2; § 2.5 |
| C08, C09 | dropping the material pass's quiescence weakened inbox attestation; one write lock | ADOPTED — quiescence kept for unconfined writers, outputs sealed before a pass; expensive work outside the lock | § 2.5; § 8 |
| C10, C11 | helpers versus one lease per position; limits are per quota pool, shared with the owner's own use | ADOPTED — per-attempt keys with parent links; quota pools | §§ 2.1, 2.5 |
| C12, C13 | the owner's chat would wait behind agents; owner words fan out also while the chief is held at a vendor limit | ADOPTED — the owner's lane outside N; both triggers | §§ 2.5, 2.10 |
| C14, C15, C16 | a seat home needs an end-to-end contract (every runtime operation, Linux device code, sessions); a clean home is not an allowlist | ADOPTED — the home in the profile and in every operation; the effective surface verified at every launch; integrity ADVISORY while unconfined | § 2.7 |
| C17, C18, C19 | confinement does not bound billed inference; cells with network have side channels; future classes stated as established | ADOPTED — admission and incurred cost classed apart; side channels per tested policy; § 5's future columns are TARGETS | §§ 2.4, 2.8, 5 |
| C20, C21, C22 | boundaries' assessor sets; consumed wakes do not "stay due" after a crash; no authority invalidation | ADOPTED — frozen assessor ids; LOST reconciliation re-raises owed work once; generation fences on every authority change | §§ 2.10, 4.3, 2.3 |
| D01 | "managers need no shell" restricted intellectual work without a demonstrated failure (PRINCIPLES) | ADOPTED — the thesis is now "authority needs no shell; code runs only in cells": a head gets a cell when its mandate needs computation | § 0.3; § 2.8 |
| D02 | "activation is earned" was rhetoric | ADOPTED — the earning test, app-checked at `charter`, `hire` and any new layer | § 2.9 |
| D03 | § 10 restated an order | ADOPTED — § 10 maps components to units only; order and status live in ORGANISATION § 15 | § 10 |
| E01–E08 | missing: sealed deliveries; restructuring transitions; service mandates; the public audit line; currency semantics; delegable evidence allowances; versioned seat records; input lineage | ADOPTED — `U-org-audit` added for the audit line | §§ 2.2–2.4, 2.6, 2.9, 2.13, 3 |

**The Opus reviewer** (fresh, read-only): see § 2b, appended when it reports.

## 3. The outside review of 2026-10-09 — its organisation points
The owner keeps an outside "adversarial review with a path to making it work", dated 2026-10-09, outside the repository; it was never recorded here.
Not verified line by line in this record — only the points this re-prioritisation meets:
- **"The AI organization may multiply expenses faster than intelligence"; compare a single agent, a critic and the full hierarchy at equal budgets;
  hierarchy earned** → ANSWERED by design: the earning test on every structural verb, the proving ground built with the kernel, its first ladder that
  exact comparison with a no-model floor, and activation only when earned (ORCHESTRATION §§ 0.2, 2.9, 2.14).
- **"Stop building organizational breadth for now"** → NOT ADOPTED as an order of building: the owner's 2026-10-11 direction is the opposite, and the
  design meets the concern where it bites — breadth is built, not activated, until measured to pay.
- **OS-enforced isolation before trusting evidence; a self-improving system must not touch its own evaluator** → ALREADY HELD ("unprotected evidence",
  EDGE § 4.5; CHANGE-001; kernel proposals) and advanced: cells first for code, decision rights never on an unconfined shell, the gate fail-closed.
- Its other attacks (economics at small scale, paper realism, broker execution, legal access) are not this record's; owed if the owner wants them answered.

## 4. The owner's decisions, 2026-10-11 (asked with the recommendation first; both recommended answers chosen)
1. **"Parallel, gated":** "Build the org's work machinery alongside containment, but nothing runs on real agents until executors have confined
   workspaces; every claim says what is enforced vs only asked." — this narrows R20 § 5's "containment first" from an order of landing to an order of
   activation.
2. **"Linux host":** the first observed organisation (M-org1) runs on the Linux host; Linux confinement comes first. Deploying there still needs his
   go and the nine host questions of 2026-10-09.

## 5. Decisions taken on the owner's behalf (his to overrule)
Both seats on the organisation (seat A: cells, runtimes, the puppet runtime, the audit line, the watcher, the proving ground; seat B: the spine, the work
plane, the surface, the chief); lane A's edge queue and seat B's perception chain PARKED with their queues kept; `U-seat-home` first and a prerequisite of
M0 attempt 4 and of every further observed run; `U-admission-fail-closed` before O4; `U-grant-tree` before any concurrency; `U-org-lanes` (O9) brought
forward; `U-proving-ground` before M-org2; `U-org-audit` added.

## 6. What changed
Docs only: `docs/ORCHESTRATION.md` (new); `docs/ORGANISATION.md` (a 2026-10-11 header paragraph, § 15's table, current order, re-sequencing note and
M0 prerequisite, M-org1's host, § 16); `docs/VISION.md` ("Where it sits"); `CLAUDE.md` ("Start here"); `docs/RESUME-HERE.md`; `BUILD-STATUS.md`; this file.
Outside the repository: the fleet's board, the orchestrator handoff and two new seat charters (`A-org-cells.md`, `B-org-spine.md`).

## 7. Open, and how each closes
The exec-server split and the broker-only codex seat → `R-exec-server`. The tools a seat is offered today → `U-seat-home`'s inventory. Linux confinement
without root → `U-cell-linux`'s survey. Concurrent turns per quota pool → `U-org-lanes`. Whether a manager layer pays → the proving ground, then M-org2.
The owner's: when testing may start (M-org0); the go to deploy on the Linux host; funding beyond the plan after M-org1.
