# U14 — ADVERSARIAL-VERIFY BRIEF · round 9 (targeted) at the sha named in the dispatch

FRESH verifier unless the dispatch says otherwise. Read first: the round-8 verify record (`records/U14-verify-r8.md`)
and its probe branch `u14-verify-r8-probes`; the builder's `records/U14.md` "## Round 9" and the class-closure arguments
it must contain; `briefs/U14-r9-bounce.md`; `records/codex-U14-r8.txt`. Worktree `u14-verify-r9`, detached at the sha;
`git checkout -b u14-verify-r9-probes`. Work ONLY there; the box is not yours unless the dispatch grants one run.

## Targets (then stop)

1. **Rotation, every interleaving:** terminate before the move / after the move before the carry-forward write / after
   the write before the flush / after the flush before the deletion — a fresh reader sees the unresolved state in ALL
   four; a later rotation never deletes a staging file holding an unresolved marker; `Flush(true)` precedes deletion
   (prove with the seam: make the flush the observation point). Then attack the builder's class-closure argument: list
   the operations yourself and find one it did not enumerate.
2. **Unreadable ≠ empty, every probe:** exists / enumerate / open / read / attributes each denied → UNREADABLE, zero
   provisional, `Trouble` non-null; and the both-directions half — a genuinely absent sidecar reads as clean-empty.
   Attack the enumeration: any filesystem call on the sidecar path the builder did not list.
3. **Row precedence for a new connection:** v2 refused → new silent peer → after `AuthGrace` the row says
   silent/unauthenticated (newer observation wins) → v3 hello clears; then the permutations (auth failure after a
   refusal; refusal after an auth failure; a silent peer that later speaks v2); the older markers stay recorded.
4. **PRIOR 29 wording:** three states, three sentences, pinned.
5. **Regression:** the round-8 harnesses (F23 idle-poll drop, V4 counters, `AdapterTeardown` on every terminal path,
   MF27b's seam) still bite at this sha; full suite once, 0 warnings on `--no-incremental`.

Record `records/U14-verify-r9.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
