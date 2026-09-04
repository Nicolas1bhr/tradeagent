# Integration checklist (manager, mechanical — §8 of the standard applied to this repo)

A unit branch is integrated only when the LAST verify round is PASS or PASS WITH LOW, the LAST Codex delta reports no
NEW HIGH/MED and every NOT FIXED is refuted on the record, and every deferred item names its owner unit.

Steps, in order, one unit at a time, from the main worktree `~/Projects/ai-trading-software-for-mihael`:

1. `git status --porcelain` on main is empty except records the manager is about to commit; commit them first.
2. Worktree sweep: `git worktree list --porcelain` + `git -C <each> status --porcelain` — the unit's build worktree is
   clean and its branch tip equals the verified sha.
3. Rebase: in the unit's build worktree, `git rebase main` (U14) or `git rebase main` on the probe branch (U2a); expected
   clean because `main` moved by docs only since the branch's base — if it conflicts, STOP: the merged combination is
   unverified and needs a targeted verify round. Record the new tip.
4. Gate on the rebased tip, in that worktree: `dotnet build TradeAgent.sln` (0 errors) + `dotnet test TradeAgent.sln`
   (full, output to a file; paste per-project counts) — this is the "full once more at Integrate" (§9.5).
5. `git branch -f <unit-branch> <rebased tip>` if the work happened on a probe branch; then on main:
   `git merge --ff-only <unit-branch>`; `git log --oneline -1`.
6. Push: `git push origin main`; then `gh run list --limit 3` and wait for the CI run on the merge sha; paste the
   conclusion (ubuntu/macos/windows + package). A red CI = revert the merge (`git revert -m 1` is NOT needed for an
   ff-merge — `git reset --hard <pre-merge sha>` + force-with-lease is the honest undo before anyone builds on it) and
   open a round.
7. Records: `records/<unit>.md` gets an "## Integrated" line (sha, date, suite counts, CI run id, the deferred items and
   their owners); `BUILD-STATUS.md` gets the unit's section written from the record (claims with the run quoted; NOT
   VERIFIED where so); `docs/RESUME-HERE.md` "Verifying what you inherited" updated where the unit changed a reading
   (U14: `proto=3`).
8. Cleanup: remove the unit's verify/codex worktrees (`git worktree remove --force`), keep the probe branches; note the
   pre-rebase tip sha in the record for archaeology.
9. Memory + manager log entry; the next unit's rebase (U2c-1 expects a `GatewayTypes.cs` conflict after U2a).

Deferred-with-owner items at U2a's integration (must appear in its "## Integrated" line): Codex r4 F5 (agent close →
`Place`, no fast path), F6 + F8-gateway (sweep replay repeats effects; idempotency only for Place), verifier r5 F-A
(operator Close All on the ordinary deadline) → U2c-1 round 4 class C; mutant B4 (Windows no-buffer stall) → run by
nobody, named; ATAS 64/65-char probe → v0.1.2; `LateAnswers` unconsumed → U2c-1.
Deferred at U14's integration: the ATAS teardown callback (which of `OnStopping`/`OnDispose` fires) → v0.1.2 bridge
redeploy; the F8 residual (a claim without an order after a rename that threw post-replace) → named; `Quarantine`'s 64
slots → named; R4's Windows half → named; the bridge DLL at protocol 3 → MUST be redeployed at v0.1.2.
