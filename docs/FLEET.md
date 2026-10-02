# The build fleet — how the whole build is run

**Opened 2026-10-02 (evening) on the owner's instruction:** "the app is still excessively naive and immature to produce that long term advantage and
moat over the markets. this isn't just a catchy trading agent. it's an ai orchestration that uses everything that exists and will exist as it's only goal
making the best possible investment decisions. for this time as orchesrator you can now summon not only sub agents but also what we called previously top
level managers as you are now the orchestrator through the whole build and exquisite grounding and context preservation is key. all shall be opus agents".
It adds one layer to `docs/HOW-WE-BUILD.md` and changes none of its honesty, safety or landing rules; where this file differs, it says so and why. The
build it runs is `docs/ORGANISATION.md` § 15 (the only waves table) over `docs/EDGE-FACTORY.md` § 9. This is the BUILD fleet, not the product's organisation.

## Seats

| Seat | Decides | Never |
|---|---|---|
| **Orchestrator** (the main session) | which seats exist and their units; the waves, the builder allotment and the schema rungs; cross-seat conflicts; every question to the owner; memory, this file and the `docs/RESUME-HERE.md` "Do this first" block | product code; running a builder; landing a unit |
| **Top-level manager** (one per seat, a fresh Opus agent) | inside its units: re-check the brief, dispatch, brief builders, judge reports, fresh fixers, land, record, read CI per sha; CARD briefs just before their turn, from a survey leg | product code; another seat's units or worktrees; asking the owner anything |
| **Builder · fixer · survey leg** (fresh Opus, spawned by a manager) | how to build its one unit: one brief, one worktree, one pass | anything outside its brief; `main` |

Every agent is Opus (`model: "opus"`), by the owner's choice. A manager runs its builders itself (the Agent tool works one level down; two builders in
one message run concurrently) and talks to the orchestrator by `SendMessage` to `main`. **A sub-agent is not woken by its own background work** (probed
2026-10-02), so no manager or builder ends its turn while work is pending: it waits in foreground slices of at most nine minutes (`fleet/bin/wait-for.sh`,
`ci-wait.sh … 9`). Live seats, agent ids and allotments: `fleet/BOARD.md`.

**The `fleet/` directory** is `~/Projects/ai-trading-software-for-mihael-worktrees/fleet/` — outside the repo and outside `/tmp`, which the OS empties after
about three days: `bin/` the tooling below, `status/<seat>.md`, `handoff/<seat>.md`, `gates/<label>/`, `locks/`, `ci-ledger.md`, `BOARD.md`.

## Grounding — read before acting, verify before trusting

- **A manager:** `CLAUDE.md`; `docs/HOW-WE-BUILD.md`; this file; `docs/RESUME-HERE.md` "Do this first"; `docs/PRINCIPLES.md`; its lane's plan (EDGE-FACTORY
  §§ 0, 4, 6, 9 · ORGANISATION §§ 1–5, 13–15); its briefs; its status or handoff file if one exists. Then the state from git, worktrees and locks — dated text
  is not proof of state. A brief is re-checked against `main` (file:line pointers, rung, dependencies landed) before dispatch; an amendment is committed by
  `dispatch.sh` with the move.
- **A builder:** its brief, `CLAUDE.md`, `docs/HOW-WE-BUILD.md` and the files the brief names; the plan only where the brief points to it.
- External facts (vendor APIs, prices, rules) are read from the official source on the day and dated; the session's web-search budget is shared by every leg.

## This Mac (M3 Pro, 11 cores, 18 GB RAM, ~50 GB free disk)

- **At most four builders at once, fleet-wide** — the orchestrator allots them per seat on the board; a seat never exceeds its allotment.
- **Locks** (`fleet/bin/lock.sh`): `suite` — any full local test suite (`gate.sh` takes it itself); `land` — one landing in flight, prep to record; `main` —
  any commit in the main checkout, held for seconds (dispatch, merge, record); `box` — the Windows machine, one leg at a time by grant.
- Worktrees at `~/Projects/ai-trading-software-for-mihael-worktrees/<branch>`, branch = the unit's name in lower case; `git -C`, never `cd` into one inside
  a compound command. Each agent keeps scratch files in its own `<scratchpad>/<seat-or-unit>/` subfolder; nothing durable lives in a scratchpad.

## The builder pass — HOW-WE-BUILD pass 1, with two changes

1. **The full suite runs on CI, on the builder's own branch.** The builder rebases on `main`; builds `-c Release --no-incremental` at 0 warnings; runs Unit
   and Fault locally and its touched classes 3×; then `fleet/bin/ci-dispatch.sh <worktree>` (scan-gated push of ITS branch + the workflow on all three
   platforms) and `ci-wait.sh --run <id> 9` in foreground slices until it stops answering TIMEOUT (windows-latest takes 40–50 min). The report quotes the run id and every job's
   verdict. *Why:* one full suite at a time is this Mac's bottleneck, and CI adds Windows — the target, which the Mac cannot prove. `gate.sh` stays available
   to a builder that needs a local full run, under the suite lock.
2. **A builder may push its own branch, and only through `ci-dispatch.sh`.** Never `main`, never a merge.

Unchanged: red-first tests and one watched mutant on the money path; one commit per item with a one-sentence message; a `## Report` ≤ 20 lines appended to
its brief and committed on its branch (tip sha, gate counts, CI run and verdicts, one line per item, what it did NOT do); the fresh-fixer rule, literally.

## Landing — the manager, HOW-WE-BUILD's checklist scripted in `fleet/bin/land.sh`

`lock.sh acquire land <seat>:<unit>` → `land.sh prep` (clean tree, tip = report, rebase on `main`; a conflict goes back to a builder) → the local gate
(Release, full, detached; or "GATE CARRIES" when only `docs/`/`*.md` moved since this unit's last gate) → `land.sh check` (PASS) → the branch's CI read:
green, or red ONLY on `ResumeOnStartTests.A_restart_with_the_ai_working_…` until W0 lands, named in the record → `land.sh merge` (ff-only) → `land.sh
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
  traps met) and reports; the orchestrator opens a fresh seat from it. A killed leg is resumed by `SendMessage` naming its branch state, or re-briefed
  fresh from its brief and branch — the branch is the handoff.
- **The repo is the checkpoint:** a `BUILD-STATUS.md` record per landing; the resume block at each wave's end and at every stop (the orchestrator); the
  memory files (the orchestrator).
- Keep every context lean: the scripts print summaries; never print a whole log or a whole long file; read by range.

## Escalation

Builder → its manager (the final report, a blocker named in it). Manager → orchestrator, `SendMessage` to `main`, first line self-contained, for: a landing,
a blocker, a decision outside the seat, an owner question, a budget or machine problem — routine progress goes in the status file. **Only the orchestrator
asks the owner,** and only for what he alone can give: real-money authority, credentials, a paid commitment, a release, a product decision the docs leave open.

## Rules every seat carries

`CLAUDE.md`'s four money rules, operator authority in-process, the inbox as data, and the no-terminal rule. No `Co-Authored-By` or "Generated with" line in
this repository's commits — the owner's rule (commit `16d4862`), which overrides any tool default. The secret scan is a gate before every commit and push
(`scan.sh`); a judged false positive is excluded by name in that one call, never by loosening the pattern. Simulated orders on the box's sim accounts are
allowed and the book is left clean; real-money orders never. The app's own AI runs on the owner's ChatGPT plan — only the milestone seat runs it, inside the
bound stated before the run.
