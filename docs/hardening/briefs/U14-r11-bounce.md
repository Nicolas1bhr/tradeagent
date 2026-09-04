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

## Verifier round-10 findings (fresh Opus, on `01fcd60`) — VERDICT: FAIL — 1H/2M/3L · record `records/U14-verify-r10.md`

- **R10-1 (HIGH) — a FIFTH crash point, inside act 1.** The carry write opens `log.new` with `FileMode.Create`, which
  EMPTIES an existing `log.new` at the open; a write that fails after the open destroys the only copy of the unresolved
  marker, and it needs no second crash — one transient IO error suffices (`attempts=2`: the retry recomputes the carry
  from the now-empty file and completes the rotation over the hole). The shipped `A_restatement_that_never_lands…`
  cannot see it because its seam throws without opening the file. Rule (directive 3 completed): act 1 writes to a
  UNIQUE temp name (`FileMode.CreateNew`, never `Create` over an existing file); an existing `.new`/temp is never
  truncated — it is either completed (resumable rotation, F41) or read as part of the snapshot; the retry recomputes
  the carry from the SNAPSHOT taken before act 1, never from a file act 1 may have touched. Test: the verifier's
  harness (seam that opens then fails) + a transient-error retry → marker present in every state; mutant: `CreateNew`
  → `Create` RED.
- **R10-2 (MED) = Codex F39 at the consumers.** `Doctor.cs:284-295` (the support package) and `tools/probe/Program.cs:1075-1078`
  still glob and copy the sidecar set themselves under a swallowing catch (one denied generation silently drops itself
  and every file after it; `GetFiles` cannot see a directory at a sidecar's name). → directive 1 at every consumer: the
  support package and the probe take the snapshot's captured lines/bytes; a snapshot `Unreadable` is written INTO the
  zip as a note, never silently absent.
- **R10-3 (MED).** Both declared survivors are load-bearing: MR10-4d (the lock on `Running→Stopping`) goes RED against
  a 300 ms-order test the builder did not write; MR10-3a's redundancy argument is wrong past the first append
  (`_sidecarBytes` is seeded once; every later append enters `Rotate` with a fresh, possibly unreadable snapshot). Both
  get the biting test; the record's "stated survivor" lines are corrected.
- **R10-4 (LOW).** The F25 reversal is now third-party reachable: any process that can write in `Paths.BridgeDir` can
  drop `SupportsClientOrderId` with one unreadable file — fail-closed and same trust domain; say so in the record and
  in CONTRACTS.md (a residual, not a fix).
- **R10-5 (LOW).** Three record claims do not check out: the cited heartbeat cover test is not in the suite; a fourth
  round-9 probe was unlifted and unlisted; the "this is the whole of it" filesystem enumeration misses three
  `FileStream` calls (one is R10-1). Correct the record; the enumeration becomes a grep the verifier can re-run.
- **R10-6 (LOW).** `AppendDurably` is dead since round 10; beside it the one sidecar append is un-flushed while its
  carried restatement is flushed — delete the dead code and state the flush policy in one sentence (which writes are
  flushed and why).

Held (the verifier's runs): 0 warnings; 517 twice; the three-removal test-name diff; all six on-box hashes; MR10-1c,
MR10-2a, MR10-5b RED in the shipped suite (R9-3 closed); 40/40 SIGKILL; 55,927 out-of-process readings against 43 real
rotations with zero clean readings.
