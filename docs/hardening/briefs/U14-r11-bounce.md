# U14 — ROUND 11 · completing the structure (Codex delta on round 10: 2 HIGH / 4 MED / 1 LOW; 12/14 priors FIXED) (+ verifier r10, below)

**Fresh builder.** Read first: the standard's §6 and §9.10, `CLAUDE.md`, `records/U14.md` "## Round 10" (the five
directives as built, the mapping table, the crash-point list, the two stated survivors), `briefs/U14-r10-bounce.md`,
`records/codex-U14-r10.txt`. Worktree `u14-build`, branch `u14-coid-witness-rewrite`, tip `01fcd60` (517 green, 0
warnings). Rules as every round (commit per item, no trailers, commit before mutants, `cp` restore + `touch`, test-name
diff after every structural edit, `## Round 11 (build record, <date>)` in `records/U14.md` on MAIN, no git there,
`--no-incremental` = 0 warnings). One box run at the END if the adapter or teardown changes (bridge compile +
`tools/atas-gate`, hash-verified) — the manager grants it here, ONE session; if your hash check fails, wait ten
minutes, re-push, re-verify, and say so.

The structure is right; these are the places where the old shape leaked back in. Each item names the directive it
completes; the class-closure sentence for each directive must be TRUE after this round, not approximately true.

- **PRIOR 33 (HIGH) — directive 1 leaked at `LastDecidingLine()`** (`CoidWitness.cs:2346`): it converts an `Unreadable`
  snapshot to null, so `Settled()` appends RESOLVED without having seen the unresolved marker. Rule: `Unreadable` is a
  VALUE that propagates to every consumer — `LastDecidingLine` returns a tri-state (line / none / unreadable), and
  `Settled()` refuses to write RESOLVED over an unreadable snapshot (it appends nothing and the standing stays degraded).
  Test: snapshot unreadable → `Settled()` writes nothing; readable → as before; mutant: collapse the tri-state → RED.
- **F39 (MED) — directive 1 leaked at the snapshot type**: the snapshot exposes PATHS but not captured LINES, so the
  probe and the support collector reopen files and combine one snapshot's standing with another filesystem state.
  Rule: the snapshot carries the lines (file → lines, captured at read time); the probe, the report and the support
  package render from the snapshot they were handed; no path leaves the snapshot for re-reading. Test: compute
  standing, rotate/resolve, render the tail → headline and lines agree (from one state).
- **F40 (MED) — directive 2 leaked for candidates**: candidate (temp/rewrite) contents are read AFTER the before/after
  listing comparison (`:1300` vs `:1719`), so recovery classification and adoption use a mixed-time view. Rule: candidates
  are part of the snapshot (read inside the before/after window); adoption decides from the snapshot; a candidate that
  changed after the snapshot invalidates it (the listing differs → retry → `Unreadable("changing")`).
- **F41 (MED) — directive 3 is not resumable on Windows**: after `current→.1` succeeds, if `.new→current` throws (a
  reader holding `.new` without `FileShare.Delete`), every retry starts with a missing current and later notes never
  append until restart. Rule: rotation is RESUMABLE — at every append and at every rotation start, if `.new` exists and
  current does not, complete the last act (`.new→current`) first; if it cannot, append to `.new`'s successor state is
  refused and the standing is degraded with the reason (never silent). Test: simulate the throw at the last act (seam),
  then append → the note lands and the standing says what happened; plus the mid-sequence restart.
- **PRIOR 35 (HIGH) — directive 4 leaked at re-entrant `Stop()`** (`AdapterTeardown.cs:233`): overlapping `Stop()` calls let
  one finalizer publish `Stopped` while another still executes, making `Started()` legal during that teardown. Rule:
  `Stop()` is idempotent and serialised under the state lock — a second `Stop()` during `Stopping` waits for (or joins)
  the first and returns after it; `Stopped` is published exactly once, after the last step of the ONE teardown; `Started()`
  stays refused throughout. Test: two concurrent `Stop()` + `Started()` in the window → refused; mutant → RED. Then the
  verifier's MR10-4d question (is the lock on `Running→Stopping` load-bearing?): construct the interleaving that needs it
  or state, with the test, why the transition cannot race.
- **F42 (MED) — directive 5 leaked during `AuthGrace`** (`AtasConnector.cs:171`): a newly arrived peer has no stamped
  status until the grace expires, so the previous connection's refusal stays the displayed winner. Rule: a new
  connection stamps "connecting — waiting for the add-on to authenticate" at ACCEPT time (counter-stamped, newer than
  any marker); the grace expiry replaces it with silent/unauthenticated; the hello replaces it with the live row.
- **F43 (LOW).** `_sidecarBytes` counts UTF-16 chars while `MaxErrorLogBytes` is a byte limit — count encoded bytes.

## Gate and report

Targeted classes; `dotnet build TradeAgent.sln --no-incremental` (0 warnings) + FULL suite once; the box run if the
adapter/teardown changed. Report: tip sha, per item RED → GREEN → mutant, the corrected class-closure sentence per
directive, suite counts, the box result, "What I did NOT do".

## Verifier round-10 findings (appended by the manager when leg [2] reports)

_pending_
