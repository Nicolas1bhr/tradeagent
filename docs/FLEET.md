# The build fleet — how the whole build is run

**Opened 2026-10-02 (evening) on the owner's instruction:** "the app is still excessively naive and immature to produce that long term advantage and
moat over the markets. this isn't just a catchy trading agent. it's an ai orchestration that uses everything that exists and will exist as it's only goal
making the best possible investment decisions. for this time as orchesrator you can now summon not only sub agents but also what we called previously top
level managers as you are now the orchestrator through the whole build and exquisite grounding and context preservation is key. all shall be opus agents".
It adds one layer to `docs/HOW-WE-BUILD.md` and changes none of its honesty, safety or landing rules; where this file differs, it says so and why. The
build it runs is `docs/ORGANISATION.md` § 15's current order — the only copy of the order (R22) — over `docs/EDGE-FACTORY.md`'s units. This is the BUILD fleet, not the product's organisation.

## Seats

| Seat | Decides | Never |
|---|---|---|
| **Orchestrator** (the main session, holding the owner's own seat over the build — § below) | what sits ABOVE the seats, on the owner's behalf: which seats exist (at most two at a time) and their lanes; the session's priorities across seats, the builder allotment and the schema rungs; cross-seat conflicts; the channel to the owner; the heartbeat; memory, this file and the `docs/RESUME-HERE.md` "Do this first" block | product code; running a builder; landing a unit; deciding inside a manager's lane |
| **Top-level manager** (one per seat, a fresh Opus agent) — a top-level manager in the full sense of the older standard | everything inside its lane: which units to brief, run and land and in what order within the session's priorities, and the units it judges owed; how each is briefed and built; every report and every deviation judged; surveys, fixers and fresh fixers; landing, the record, CI per sha | product code (its builders write it); another seat's lane |
| **Builder · fixer · survey leg** (fresh Opus, spawned by a manager) | how to build its one unit: one brief, one worktree, one pass | anything outside its brief; `main` |

Every agent is Opus (`model: "opus"`), by the owner's choice. A manager runs its builders itself (the Agent tool works one level down; two builders in
one message run concurrently) and talks to the orchestrator by `SendMessage` to `main`. **A sub-agent is never woken by a background child agent finishing**
(that notice goes to the orchestrator), **and a background Bash wakes it only late** (probed 2026-10-02: 5 m 40 s after a 50 s command). So a BUILDER never ends its turn while its
pass is pending: it waits in foreground slices of at most nine minutes (`fleet/bin/wait-for.sh`, `ci-wait.sh … 9`). A MANAGER, since 2026-10-04 (budget,
below), does not poll while its builders work: it ends its turn, and the orchestrator — who receives every builder's completion notice — wakes it by
`SendMessage` with the report; it waits in slices only for short work of its own (its landing gate, a lock). Live seats and allotments: `fleet/BOARD.md`.

**The `fleet/` directory** is `~/Projects/ai-trading-software-for-mihael-worktrees/fleet/` — outside the repo and outside `/tmp`, which the OS empties after
about three days: `bin/` the tooling below, `status/<seat>.md`, `handoff/<seat>.md`, `gates/<label>/`, `locks/`, `ci-ledger.md`, `BOARD.md`.

## The orchestrator — the owner's own seat over the build (the standard since 2026-10-06)

**The owner, 2026-10-06 00:50 CEST, verbatim:** "for tonight i'm leaving you the highest possible position. mine, the orchestrator. you can now manage up
to two top level managers and use orchestration standard just as inspiration and help as to how you should guide them. tonight i'd like you to continue
the construction of the software. i really like your performance over the last few days as orchestrator especially with the hearbeat you programmed to
wake you back up each time my rate limit resets. if tomorrow morning i see that you delivered again when it comes to progress and sheer build quality i'd
like to make this permanent. make sure this is written down somewhere so we can upgrade the orchestration documents to incorporate all of this. if you
need anything from me you can always ping me." **01:12:** "No need to budget the weekly rate limit. If you use all that's no issue." **The morning after,
making it the standard:** "when you decide your session was fruitful enough i'd suggest wrapping up slowly and then reworking the orchestration standards
so this orchestration position becomes the standard."

- **What it is.** The main session holds the owner's own seat over the BUILD. It runs at most two top-level manager seats at a time (a third, such as seat
  M for a milestone, opens when one closes); it decides sequencing, allotments and cross-seat questions on his behalf (the 2026-10-04 delegation:
  conservative, compliant, reversible, each decision on `fleet/BOARD.md` with its reason, his to overrule); it is the channel to him. Its managers are top-level managers in the full sense of the older standard: the orchestrator
  adds a layer above them and takes nothing from their authority, freedom or judgement inside their lanes. It does not hold his
  authority over money, credentials, legal status, paid commitments or anything sent in his name — each still needs his explicit yes — and it changes no
  protection in `CLAUDE.md`. The sibling projects' orchestration standard is inspiration only; `docs/HOW-WE-BUILD.md` and this file govern.
- **Starting a session** (the owner says "you are the orchestrator" in a build session he has opened — in a meeting or a planning session, which his words
  decide, the orchestrator lands its conclusions as docs and arms no heartbeat, opens no seat and dispatches no builder; "implementation can start whenever"
  means allowed later, not now — 2026-10-08, R22): (1) arm the heartbeat, first, since crons die with the session that set them;
  (2) the Mac — `pmset -g batt`, `ioreg -r -k AppleClamshellState -d 4`, `df -h /` (a closed lid on battery sleeps the fleet); (3) `get_usage` — the
  5-hour window and its reset (the weekly figure is reported, not rationed); (4) the state — `fleet/BOARD.md`, `fleet/status/`, `fleet/handoff/`,
  `git log origin/main`, `git worktree list`, `fleet/bin/lock.sh status`, `gh run list`, the resume block, `tools/win-state.sh` and
  `TA_WIN_BOX=tests tools/win-test.sh ready`; (5) open FRESH manager seats (agent ids die with their session) whose prompts name the charter, handoff and
  status files, the session's rules and its priorities across seats — each manager plans its lane from them on its own judgement and says so when it
  sees a better order; write the board.
- **Each wake.** A builder's completion notice reaches the orchestrator, not its manager: the orchestrator wakes the manager with the report's facts
  (tips, gate counts, CI per platform, deviations, NOT VERIFIED) and, where it helps, its view. The manager judges the report itself — the whole report
  is on the branch — and decides everything inside its lane; the orchestrator rules only on what sits above a seat (priorities across seats, a
  protection's urgency, a product question the owner's documents leave open) and says which is which. A message that changes nothing for a running agent
  waits for its next wake instead of costing one. A red no diff can reach is a FIRST SIGHTING, recorded with its run id and a one-line reading — never "a
  flake" by assertion (the fleet's Windows-only reds have twice been real defects); one that threatens a protection (money, evidence) is surveyed at
  once. The orchestrator reads reports, the board and status files, never code: on 2026-10-06 its context stood at 35 % of 1M after eleven hours.
  **Waiting costs a whole context per call, so every seat and builder waits DETACHED** (Bash `run_in_background`, which re-invokes the agent when the
  command exits, or `nohup` for durable ledgers), never in foreground slices; a builder ends a waiting turn with one line `WAITING: …`, which is not a
  report and wakes no one. An OBSERVING seat (a milestone run) ends its turn behind a detached zero-token logger and the orchestrator's cron looks in
  (2026-10-07: ~100 foreground watch calls, flagged by the owner). A seat holding `land` whose transcript shows no progress for 15 min is STALLED:
  stop it and resume it by `SendMessage` with its state (2026-10-08: a nested `zsh -c` with mixed quoting sat on stdin for 38 min — run commands
  plainly, `< /dev/null`, never nested quoting). Board times come from `date`; seats' self-reported clocks ran 4–25 min fast.
- **The heartbeat — the practice he named.** Session crons (`CronCreate`) carry the fleet across usage stops: a ONE-SHOT wake at each 5-hour window's
  reset + 4 min (from `get_usage`'s `resetsAt`, re-armed at every wake; deleted when the orchestrator resumes the fleet by hand first) and a RECURRING
  2-hourly backstop. A cron fires only while the session is idle, which a usage stop leaves it; on the wake the orchestrator reads usage, the Mac and the
  fleet's state and resumes every seat and builder that died at the stop by one `SendMessage` naming its branch state and the CI runs that finished
  meanwhile — nothing is lost, because the branch is the handoff. The window, not the week, binds: spend each window on the highest-value units first, and
  let CI and detached gates run through the stop. Crons are session-only (a recurring one expires after 7 days).
- **The morning report and the wrap-up.** The report: what landed (record shas, gate counts, CI per platform), what is building, what only the owner can
  give, the budget spent, and every NOT VERIFIED — `BUILD-STATUS.md`'s honesty rule; a push only when he can act on it. The wrap-up, when he asks or the
  orchestrator judges the session fruitful: seats land what is in flight and start nothing new but a protection's fix; each writes its status and handoff;
  the orchestrator checkpoints the resume block, the board, `fleet/handoff/ORCHESTRATOR.md` and memory, removes its crons, and reports.
- **The reference run, 2026-10-06** (00:50 to 18:30): two seats, four Mac builders and one ATAS-box leg; three usage stops and a clamshell sleep bridged
  with nothing lost; twelve units landed (the resume block's checkpoint lists them).
- **The second, 2026-10-06 21:33 → 2026-10-08 15:25:** seats P and A (each ROTATED once, fresh from its handoff) plus seat M for M0 attempt 3;
  four Mac builders; every usage stop bridged by the heartbeat (the weekly ran out once and the owner reset it); twenty-one units landed and M0 recorded.
  Read-only surveys found protection defects nobody had briefed — the agent's process tree outliving Pause, two holdout leaks, two credential files
  written readable — and each was fixed and landed within the session (the resume block's checkpoint lists them).

## Grounding — read before acting, verify before trusting

- **A manager:** `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; this file; `docs/RESUME-HERE.md` "Do this first"; `docs/PRINCIPLES.md`; its lane's plan (EDGE-FACTORY
  §§ 0, 4, 6, 9 · ORGANISATION §§ 1–5, 13–15); its briefs; its status or handoff file if one exists. Then the state from git, worktrees and locks — dated text
  is not proof of state. A brief is re-checked against `main` (file:line pointers, rung, dependencies landed) before dispatch; an amendment is committed by
  `dispatch.sh` with the move.
- **A builder:** its brief, `CLAUDE.md`, `docs/HOW-WE-BUILD.md` and the files the brief names; the plan only where the brief points to it.
- External facts (vendor APIs, prices, rules) are read from the official source on the day and dated; the session's web-search budget is shared by every leg.

## This Mac (M3 Pro, 11 cores, 18 GB RAM, ~50 GB free disk)

- **At most four builders at once, fleet-wide, by machine.** The 2026-10-04 "two by budget" throttle is WITHDRAWN — the owner, 2026-10-06 01:12: "No need
  to budget the weekly rate limit. If you use all that's no issue." What still binds is the 5-hour usage window (two managers, four builders and the
  orchestrator empty it in ~75–110 min; `get_usage`): spend each window on the highest-value units first, and let CI run through the stop. The orchestrator
  allots builders per seat on the board; a seat never exceeds its allotment. Calls cost in proportion to context: a seat past ~60 % context is replaced.
- **Local suites are slow and leak:** since 2026-10-03 the local Unit suite runs ~13× slower than on CI (swap-bound; environmental), so a red local gate in
  a timing-sensitive test gets one fresh re-gate; every test process leaves a home in `$TMPDIR/tradeagent-tests`, purged by `fleet/bin/purge-test-homes.sh`
  inside `gate.sh` and `suite.sh`.
- **Locks** (`fleet/bin/lock.sh`): `suite` — any full local test suite (`gate.sh` takes it itself); `land` — one landing in flight, prep to record; `main` —
  any commit in the main checkout, held for seconds (dispatch, merge, record); `box` — the ATAS Windows machine, one leg at a time by grant.
- **The tests box** (`TA_WIN_BOX=tests`, since 2026-10-05; `tools/README.md`) runs the CI test job on real Windows in ~22 min against windows-latest's
  40–50. **When it is available, it is used — the owner's rule:** `tools/win-test.sh ready` (0 yes · 1 unreachable · 4 a run in progress · 5 his own
  apps open), then `start --src <worktree>` and `wait` (detached, like every wait). No `lock.sh` lock: the box refuses a second run itself (exit 4 — wait up to
  30 min, then write NOT RUN). It is a lent laptop with its owner's own TradeAgent and ATAS installed: nothing of ours starts or touches them, a run is a
  gate or a diagnosis, never a loop, and experiments (`R-containment`, the bridge, the app) go to the ATAS box, never there.
- Worktrees at `~/Projects/ai-trading-software-for-mihael-worktrees/<branch>`, branch = the unit's name in lower case; `git -C`, never `cd` into one inside
  a compound command. Each agent keeps scratch files in its own `<scratchpad>/<seat-or-unit>/` subfolder; nothing durable lives in a scratchpad.

## The builder pass — HOW-WE-BUILD pass 1, with two changes

1. **The full suite runs on CI, on the builder's own branch.** The builder rebases on `main`; builds `-c Release --no-incremental` at 0 warnings; runs Unit
   and Fault locally and its touched classes 3×; then `fleet/bin/ci-dispatch.sh <worktree>` (scan-gated push of ITS branch + the workflow on all three
   platforms) and `ci-wait.sh --run <id>` DETACHED (Bash `run_in_background`), ending its turn with `WAITING: …` until the verdict re-invokes it (windows-latest
   takes 40–60 min). The report quotes the run id and every job's
   verdict. *Why:* one full suite at a time is this Mac's bottleneck, and CI adds Windows — the target, which the Mac cannot prove. `gate.sh` stays available
   to a builder that needs a local full run, under the suite lock. **And on the tests box when `ready` says yes** (HOW-WE-BUILD pass 1): the same tip, the
   run id, verdict and counts in the report — or "tests box: NOT RUN — <ready's answer>". Real Windows hardware, the target, in half CI's time.
2. **A builder may push its own branch, and only through `ci-dispatch.sh`.** Never `main`, never a merge. It never uses the app's built-in browser pane
   (a site-permission prompt only the owner can answer hung a builder for 80 min on 2026-10-04): web sources are read with curl or WebFetch.

Unchanged: red-first tests and one watched mutant on the money path; one commit per item with a one-sentence message; a `## Report` ≤ 20 lines appended to
its brief and committed on its branch (tip sha, gate counts, CI run and verdicts, the tests-box run or its NOT RUN line, one line per item, what it did
NOT do); the fresh-fixer rule, literally.

## Landing — the manager, HOW-WE-BUILD's checklist scripted in `fleet/bin/land.sh`

`lock.sh acquire land <seat>:<unit>` (turns: the seat that released `land` yields 120 s before re-taking it) → `land.sh prep` (clean tree, tip = report, rebase on `main`; a conflict goes back to a builder) → the local gate
(Release, full, detached; or "GATE CARRIES" when only `docs/`/`*.md` moved since this unit's last gate) → `land.sh check` (PASS) → the branch's CI read:
green on all three platforms (the W0 exception for `ResumeOnStartTests` ended when `U-fix-resume-on-start` landed at `c8d6642`: such a red is now a red);
the run id named in the record → the tests-box run of the landed tree read (re-run after a rebase that moved code), or its NOT RUN line → `land.sh merge` (ff-only) → `land.sh
record` (≤ 40 lines measured; the brief retired; pushed with the merge) → release `land` → a detached `nohup ci-wait.sh <sha> 100 &`, whose verdict lands
in `fleet/ci-ledger.md` and the seat's next record → `land.sh cleanup`. Red CI on `main` in the product: tell the orchestrator, then HOW-WE-BUILD step 6 (reset, force-with-lease,
a fixer on the branch). A hosted-runner red is seat P's fixer. **Schema rungs** are assigned in landing order on the board; a collision at rebase is a conflict.

The record is written from the builder's report and the gate: what was run with its output quoted, what is NOT VERIFIED, the rung, the CI line. The landing
manager keeps `BUILD-STATUS.md`'s honesty rule exactly — no *should*, *probably*, *looks correct*; a judged exception (a disclosed test rename, a carried
gate, a known red) is written into the record as a judgement.

## Context preservation

- **`fleet/status/<seat>.md`** — rewritten, not appended, at every state change: units and their state, branch tips, live agent ids, what is in flight, what
  is owed, the last CI verdicts. ≤ 40 lines. With git, it is enough for a fresh manager to resume the seat.
- **Hand-off:** a manager whose context passes about 60 %, or whose scope ends, writes `fleet/handoff/<seat>.md` (≤ 40 lines: state, open judgements,
  traps met) and reports; the orchestrator opens a fresh seat from it. In practice the orchestrator ROTATES a seat at ~400–550k, when each wake costs
  more than a fresh seat's grounding (2026-10-08: A at 02:59, P at 08:51 → a fresh P at 12:12), the outgoing seat writing its handoff before a usage
  stop; the fresh seat's prompt names the charter, handoff and status files, the verified state and the orchestrator's priorities. Survey outputs live in
  the session scratchpad, which the OS empties: the orchestrator copies what a seat will need into `fleet/tmp/`. A killed leg is resumed by `SendMessage` naming its branch state, or re-briefed
  fresh from its brief and branch — the branch is the handoff.
- **A new orchestrator session** starts with § "The orchestrator" (*Starting a session*), then `fleet/handoff/ORCHESTRATOR.md` for the state. Agent ids die with the session that spawned them: it opens FRESH seats from
  `fleet/charters/`, `fleet/handoff/` and `fleet/status/`, and a paused builder's work is continued by a fresh builder from its branch and worktree.
- **The repo is the checkpoint:** a `BUILD-STATUS.md` record per landing; the resume block at each wave's end and at every stop (the orchestrator); the
  memory files (the orchestrator).
- Keep every context lean: the scripts print summaries; never print a whole log or a whole long file; read by range.

## Escalation

Builder → its manager (the final report, a blocker named in it). Manager → orchestrator, `SendMessage` to `main`, first line self-contained, for: a landing,
a blocker, a decision outside the seat, an owner question, a budget or machine problem — routine progress goes in the status file. **A question for the
owner travels through the orchestrator,** which carries it to him whole — one voice to him, nothing dropped — for what he alone can give: real-money
authority, credentials, a paid commitment, a release, a product decision the docs leave open.

## Rules every seat carries

`CLAUDE.md`'s four money rules, operator authority in-process, the inbox as data, and the no-terminal rule. No `Co-Authored-By` or "Generated with" line in
this repository's commits — the owner's rule (commit `16d4862`), which overrides any tool default. The secret scan is a gate before every commit and push
(`scan.sh`); a judged false positive is excluded by name in that one call, never by loosening the pattern. Simulated orders on the box's sim accounts are
allowed and the book is left clean; real-money orders never. The app's own AI runs on the owner's ChatGPT plan — only the milestone seat runs it, inside the
bound stated before the run.
