# U14 — ADVERSARIAL-VERIFY BRIEF · round 10 (the STRUCTURAL round) at the sha named in the dispatch

FRESH verifier unless the dispatch says otherwise. Read first: `records/U14-verify-r9.md` and its probe branch
`u14-verify-r9-probes` (plus `u14-verify-r8-probes`: the SIGKILL rotation harness, the 200 ms-ping peer, the two-thread
guard probe, the three-process lease harness, the R3 160-event test); the builder's `records/U14.md` "## Round 10" (the
mapping table, the crash-point list, the box run); `briefs/U14-r10-bounce.md` (the five structural directives);
`records/codex-U14-r9.txt`. Worktree `u14-verify-r10`, detached at the sha; `git checkout -b u14-verify-r10-probes`.
Work ONLY there; the box is not yours unless the dispatch grants one run.

This round claims STRUCTURE, so verify structure, not instances:

1. **One site.** `grep` the witness and its report for every filesystem call (`File.`, `Directory.`, `FileStream`,
   `FileInfo`, `Path.Exists`, enumeration): all sidecar access must be inside `ReadSidecarSet()` (rotation and the
   witness file itself excepted and named); no consumer can reach the filesystem — try to add one in a test and show the
   type system or the API shape refuses it. Then the one failure mode: inject EVERY exception type at EVERY step via the
   seam (enumerate, listing, open, read, mid-read vanish, `DirectoryNotFound`, `UnauthorizedAccess`, sharing violation)
   → `Unreadable` at every consumer (degraded, provisional, `Trouble` non-null, report "could not read"); both directions
   (a clean-empty directory reads clean-empty only after a successful enumeration).
2. **Concurrent change.** Rotate while a reader snapshots (real threads, then real processes): the reader either sees a
   consistent snapshot containing the marker or reports `Unreadable("changing")` — never a clean zero. Try to construct
   a listing that is byte-identical before and after while content changed (same names, lengths, mtimes within the
   clock's granularity) and rate what you find.
3. **Rotation crash points.** Enumerate the rename sequence yourself; SIGKILL a real process at each point (the r8
   harness); after each, a fresh reader sees the unresolved marker; no staging file exists; the temp is read by the glob;
   a rotation that cannot read refuses to rotate and appends instead.
4. **The state machine.** Under one lock: `Started()` during `Stopping` refused; a write during `Stopping` refused; a
   write after `Stopped` refused; restart after `Stopped` allowed; the two-thread probe at 40 rounds; `_witness in the
   adapter: 0` still true (read the adapter source). Mutant: remove the lock from one transition — a test must bite.
5. **The row.** Authenticated-without-hello → "waiting for the add-on's hello", newer than any marker; the r7–r9
   precedence permutations still hold.
6. **Regression + gate:** every rounds 6–9 test still green or rewritten with the reason in the record; 0 warnings
   `--no-incremental`; full suite once; test-name diff against `e113c4c`.

Record `records/U14-verify-r10.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
