# TradeAgent — First Trustable Deployment & Monitoring Phase (program plan)

Started 2026-09-02. Process modelled on `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md`
(a mandatory read-gate before any delegated build/verify work) and venture-agent's unattended-operation doctrine; the
twelve ported patterns are in `sibling-process-survey.md`. Current state and the pick-up point: `HANDOFF-2026-09-04.md` (supersedes `HANDOFF-2026-09-03.md`).

## Definition of "finished" for this phase

TradeAgent is deployable and observable: a release that installs without a terminal, whose order path has survived
independent adversarial + cross-model review with no HIGH/MED open, whose failure modes fail closed, whose update channel
is proven, and whose behaviour during the first weeks can be read back by Nicolas from the live system with a written
protocol for what to check, how often, and when to stop it.

| # | Done-criterion | Status 2026-09-03 |
|---|---|---|
| D1 | Order path, operator authority, pipe auth, updater trust chain reviewed by a Claude adversarial leg and Codex on a pinned sha; every finding triaged on the record; no HIGH/MED open; each real finding has a red-first test watched to bite | Reviews done (283d942: Codex 15 findings, adversarial 16); fixes in U2a/U2b/U2c-1/U2d/U14; U2b integrated; the rest in verification rounds |
| D2 | Update path proven end to end on Windows | **DONE 2026-09-02** (v0.1.0 → v0.1.1 self-installed over the running app) |
| D3 | Seen on Windows: setup journey, bridge-refusal sentence, reconciliation card via a genuine ambiguous order | **DONE 2026-09-03** (16 UX defects → U6) |
| D4 | Fail-closed audit of runtime inputs; UNKNOWN/consecutive-failure behaviour pauses trading; kill-switch semantics incl. unwritable DB | Findings in hand (settings parse → kill switch off; G9 flag-only; kill switch throws under DB lock); fixes = U2c-2 |
| D5 | Observability on-machine (append-only where it must be, rotated where it must be) and an off-machine way for Nicolas to read health + activity | Not started; needs decision 4 |
| D6 | `docs/DEPLOYMENT.md` + `docs/MONITORING-PHASE.md` exist and were walked once | Not started |
| D7 | Code signing executed or explicitly deferred with the SmartScreen consequence in the user guide | Needs decision 2 |
| D8 | RESUME-HERE / BUILD-STATUS / USER-GUIDE match reality | Backlog in the handoff §7 |

Out of scope for "finished" and gated on Nicolas: the staged live trial (needs a broker), the sync→async ATAS call-site
flip (wants a real broker's latency), `LIVE_AUTONOMOUS` (refused by design while `SupportsOrderHistory` is false).

## Units and tiers

T1 = order path / authority / money (full triad, rounds until no HIGH/MED). T2 = runs unattended or touches the wire.
T3 = mechanical/docs.

| Unit | Tier | Scope | Status |
|---|---|---|---|
| U0 | T3 | Restore the box after the 2026-09-02 reboot | done |
| U1 | T1 | Adversarial + Codex review of 283d942 | done (`records/reviews-283d942.md`) |
| U2a | T1 | Gateway pipe backpressure; operator-context hole; connector send deadline; cancel-all ids; replayable request id; emergency fast path | r4 built, verify pending |
| U2b | T1 | Approval re-authorization + 15-min TTL + one clock | integrated |
| U2c-1 | T1 | Dispatch recovery: startup sweep, aged DISPATCHING, exhaustive state mapping, catch-all after the wire, emergency-control records, target-based reconciliation | r3 built; round 4 decided, not briefed |
| U2c-2 | T1 | Authority robustness (see handoff §3) | not started |
| U2d | T2/T1 | Updater fail-closed: no checksum → refuse; single asset; re-hash before launch; hard stop in `InstallAsync`; install latch; capped manifest fetch; testable coupling seam | r3 built; round 4 pending |
| U2e | T2 | Bridge health = command-loop responsiveness (Codex F9) | not started (box) |
| U2g | T1 | Connector-side peer image check (the image check is one-directional today) | not started (box) |
| U3 | T2 | Update path proof | done |
| U4 | T2 | Windows eyes | done |
| U6 | T2 | 16 UX defects + support-zip redaction by content + `ErrorCatalogueTests` weakness + NOT_APPLICABLE health state | not started |
| U7 | T2 | Off-machine observability per decision 4 | not started |
| U8 | T3 | DEPLOYMENT.md + MONITORING-PHASE.md | not started |
| U9 | T3 | Signing in `build.ps1`; updater pins publisher once a cert exists | needs decision 2 |
| U10 | T3 | winagent must survive its console being closed (`-Hidden` task) | not started |
| U11 | T3 | `docs/hardening/` in-repo (this) | this commit |
| U12 | T1 | Agent containment design (same-user boundary) | design challenged UNSOUND; revision needed |
| U13 | T2 | Job Object (KILL_ON_JOB_CLOSE) around the CLI and descendants | scaffold lost; re-brief |
| U14 | T1 | CoidWitness rewrite durability (rule-1 write-ahead) | r4 items 1–5 built; item 6 + verify pending |

## Record conventions

`docs/hardening/records/<unit>.md` (design note, RED quotes, GREEN, mutants table, adjacent sweep, gaps ✅ only when
closed by running something, "what I did NOT do"), verify records alongside, Codex raw output kept as
`codex-<unit>-r<n>.txt`. Findings: real → bounce; false → recorded with the one-line reason. Every real finding: "can a
test/gate catch this class next time?" Banned words: should work, looks correct, probably, I believe, minor, trivial,
static-verified. **Records are committed to the repo as they are written — never only to a temp directory.**
