# How we build TradeAgent

**Standing doctrine from 2026-09-04.** It replaces the round-based process in `docs/hardening/`, and it demotes the
sibling projects' orchestration standard to inspiration. The read-gate before any delegated work is this file,
`CLAUDE.md` and `docs/RESUME-HERE.md`. Nothing else.

## Why it changed

Between 2026-09-02 and 2026-09-04 the process ported from the industrial projects produced 82 commits, of which 9
touched product code; 16,000 lines of briefs and records; twelve verification rounds on one unit and ten on another;
eight rate-limit kills; and nothing landed on `main`. A triad per round is priced for a multi-tenant SaaS with fifty
migrations. This is a two-person deliverable whose test box is the deployment target. What follows keeps the hierarchy
and the honesty and deletes the passes.

## What stays

- **Hierarchy.** A manager directs, gates and lands; it writes no product code. Every leg is a fresh Opus agent on its
  own worktree under `~/Projects/ai-trading-software-for-mihael-worktrees/`. At most two heavy legs at once.
- **Honesty.** Every claim is "verified by running X → output" or "NOT VERIFIED". Banned: should work, looks correct,
  probably, I believe, minor, trivial, static-verified. `BUILD-STATUS.md` stays the record and keeps that rule.
- **Safety.** The rules in `CLAUDE.md`. A change on the money path (gateway, connectors, witness, updater, kill switch,
  approvals) ships with a test that was RED before the fix, and the builder watches ONE mutant of the guard go red and
  quotes it. That is the whole proof burden. There is no separate mutant sweep.
- **Mechanics that paid for themselves.** Checkpoint into the repo, never the scratchpad. The Windows box is one leg's
  at a time, by explicit grant, tree proven by hash before a figure counts. Secret-scan as a gate before every commit.
  No `Co-Authored-By` trailers. `--ff-only` into `main`. `dotnet build --no-incremental`, because an incremental build
  once hid a warning. Two findings with one root cause get one structural fix.

## Two passes, then it lands

**Pass 1 — build.** One fresh builder, one brief of at most 40 lines at `docs/briefs/<unit>.md`, one branch. The
builder rebases onto `main` first and resolves any conflict itself, builds the whole unit, writes red-first tests where
the money path is touched, runs the gate (`--no-incremental` build at 0 warnings, full suite to a file), commits per
item with one-sentence messages, and appends a `## Report` of at most 20 lines to its own brief: tip sha, the gate
counts pasted, one line per item, and what it did NOT do. The report is the record. The box is not part of a unit
unless the brief grants it, for code that compiles only there; otherwise the box is used once, at the milestone.

**Pass 2 — land.** The manager, in the same session, runs the landing checklist below on the reported tip, writes a
`BUILD-STATUS.md` section of at most 40 lines from the report, and deletes the brief. `docs/briefs/` holds only work
in flight; empty means nothing is.

There is no verify leg, no Codex leg, no bounce, no combination verify and no scribe between the two passes. A unit is
built once and landed once.

## Ask questions later — the milestone review

Once per milestone, as the last step before a release is cut: one fresh Opus reviewer told to break the money path on
`main` at a named sha, and Codex read-only on the same sha in its own worktree, in parallel. Findings go to
`docs/REVIEW-<date>.md` as one table, one line per finding: severity, file:line, the check that settles it. Each HIGH
becomes a fix unit before the release. MED and LOW together become one batch unit. A fix unit goes through the two
passes like any other; its red-first test is its proof, and the next milestone's review is what catches what it missed.
Fixes are not re-reviewed.

## The fresh-fixer rule

A builder gets one attempt per item. When the gate fails on an item, or the report says NOT FIXED, PARTIAL or
"I could not", the manager does not ask that builder to try again. It stops the leg (the per-item commits are on the
branch) and dispatches a fresh agent with a fixer brief: the item, the failing command and its output, the file and
line, and nothing of the previous builder's reasoning. A builder that has failed carries the wrong model of the problem,
and more context makes that worse, not better. Two fresh fixers failing on the same item means the item is mis-stated
or structural: the manager rewrites it as a class fix. It does not send a third fixer.

A rate-limit or process kill is not a failure. If the process is alive, resume the leg with one message. If not,
re-brief from the file on disk, and read the branch first, because the fixes may already be there.

## What is gone

Round numbers. Bounce briefs, verify briefs and Codex prompts as files. Verify records, unit records with per-round
sections, the manager log, Codex transcripts in the repo. The pre-integration design challenge. Tiers as leg
allocation. The combination verify. The integration scribe. A box run per round. The sibling standard as a read-gate.
`docs/hardening/` is frozen history: its unit table is still the backlog, its process is not ours.

## The manager's landing checklist

1. `git status --porcelain` clean in the builder's worktree; tip equals the reported sha.
2. `git rebase main` if `main` moved; a conflict goes back to a builder, the manager does not resolve it.
3. `dotnet build TradeAgent.sln --no-incremental` → 0 warnings; full suite to a file → 0 failed; counts pasted.
4. Test-name diff against `main` → nothing removed. A deleted test cannot fail; it happened three times.
5. Secret scan of the whole diff against `main`, as a gate, not a neighbouring command.
6. `git merge --ff-only`, push, CI green on all three platforms at the merge sha. Red CI: `git reset --hard` to the
   pre-merge sha, `--force-with-lease`, then a fixer.
7. `BUILD-STATUS.md` section; brief deleted; worktree removed; memory updated.

## Sizes, so that this stays true

Brief ≤ 40 lines. Report ≤ 20 lines. `BUILD-STATUS.md` section ≤ 40 lines. Review table one line per finding. This
file ≤ 100 lines. Anything that needs more is two units.
