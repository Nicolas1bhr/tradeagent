# U14 — ADVERSARIAL-VERIFY BRIEF · round 11 (the structure completed) at the sha named in the dispatch

FRESH verifier unless the dispatch says otherwise. Read first: `records/U14-verify-r10.md` and its probe branch
`u14-verify-r10-probes` (plus r8/r9 probes: SIGKILL rotation, 200 ms-ping peer, two-thread guard, three-process lease,
R3 160-event); the builder's `records/U14.md` "## Round 11" (the corrected class-closure sentence per directive);
`briefs/U14-r11-bounce.md`; `records/codex-U14-r10.txt`. Worktree `u14-verify-r11`, detached at the sha;
`git checkout -b u14-verify-r11-probes`. Work ONLY there; the box is not yours unless the dispatch grants one run.

Verify that each directive's class-closure sentence is now TRUE, by trying to falsify it:

1. **"Unreadable is a value that reaches every consumer."** Inject `Unreadable` at the snapshot and walk EVERY consumer
   (`HasNotes`, `LastDecidingLine`, `Settled()`, `SidecarPaths`, `Trouble`, `Notes`, `Candidates`, the report, the probe,
   the support package, rotation): each must degrade/refuse, none may write RESOLVED, none may reopen a file. grep for
   any null-for-unreadable conversion left.
2. **"The snapshot carries lines; no path leaves it for re-reading."** grep the probe, report and support package for
   file opens on sidecar paths; the tail rendered after a rotation agrees with the headline (one state).
3. **"Candidates are inside the before/after window."** Change a candidate between the listing and the read → the
   snapshot is invalidated (retry → `Unreadable("changing")`), never a mixed-time adoption.
4. **"Rotation is resumable at every act."** Fail each of the four acts once (seam) on macOS; simulate the Windows
   sharing case (a held `.new`); a later append lands or the standing says why; a restart mid-sequence completes it.
5. **"Stop is idempotent and serialised; Started is refused throughout."** Two concurrent `Stop()`s + `Started()` in the
   window; `Stopped` published once; the verifier's earlier MR10-4d question answered with a test or an argument you
   can break.
6. **"A connection always has a stamped status from accept."** Read `StatusDetail` at accept, during `AuthGrace`, at
   expiry, at hello, and after a refusal — each newer than the previous connection's marker.
7. **Bytes vs chars** on the sidecar bound; and the regression set: every rounds 6–10 test green, 0 warnings
   `--no-incremental`, full suite once, test-name diff against `01fcd60`.

Record `records/U14-verify-r11.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
