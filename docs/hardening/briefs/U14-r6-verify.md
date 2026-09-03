# U14 — ADVERSARIAL-VERIFY BRIEF · round 6 (targeted) at `f8a724c`

Same verifier (context intact). Sha under test **`f8a724c`** = `6a40fa7` + 13 commits answering `briefs/U14-r6-bounce.md`
(builder's record: `records/U14.md` "## Round 6"). Builder's claims: Mac **432 green** (75/111/246); the bridge compiled
on the box (0 errors) and the money-path gate (PRIOR 5/16: witness unavailable → `ClosePosition` never called, "nothing
was submitted") PASSED on the box both directions; **every on-box SUITE figure from rounds 5 and 6 is WITHDRAWN** (a
second leg was pushing to the same box repo; the tree was replaced and once wiped). One deviation the manager ACCEPTED:
for a protocol-mismatched peer the read loop keeps `return true` (returning false → `Drop` → clears `_incompatible` by
design and the "reinstall the add-on" sentence vanishes); the peer is left parked on the pipe, read by nobody, and
every behavioural clause of the rule holds. Two of the builder's tests were silently deleted by a later edit and
restored (caught by a mutant that reported no failure) — rule: a SURVIVED mutant is evidence only if the test count moved.

Worktree: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u14-verify-r6`, detached at
`f8a724c`; first command `git checkout -b u14-verify-r6-probes`; cherry-pick from `u14-verify-r5-probes` as useful.
Work ONLY there. **The box is NOT yours** (serialised; the U2a builder holds the grant).

## Targets (then stop)

1. **F17 unreadable ≠ absent:** Codex's injected-opener case (deny load + CAS reads, permit the post-replace read; commit
   A, submit B → refused, A byte-identical); plus your own variants (partial read, `UnauthorizedAccessException` on the
   committed path, a directory at the path). Parse-failure and I/O-failure share one predicate — try to split them.
2. **R1/F20/PRIOR 9 as implemented — a refused peer is refused for the whole connection:** v2 hello → v3-claiming
   heartbeat → still refused, `ReconciliationProvable=false`, autonomy still refused, row unchanged; a later compatible
   hello on the same connection → still refused; fresh v3 connection → accepted. Then the accepted deviation: with the
   peer parked and unread, does the connector still accept a NEW connection from a fixed bridge? Does the parked peer
   hold a slot, a buffer, a thread, or a heartbeat timer forever? A parked-forever peer that blocks the fixed bridge is
   a HIGH.
3. **F18/F19 rotation:** the unresolved-safety state survives rotation (a diagnostic first line in the new log), and
   `Historical` requires RESOLVED after the last safety line. Both directions.
4. **R3 per-writer sidecars:** 160 concurrent events from refused writers → 0 lost (five runs); the probe and the support
   package collect every per-writer file; the owner's degraded state still computed over the whole set.
5. **F21/R2 lease release on every terminal path:** `OnStopping`, `Dispose`, and the strategy-removal path; a stopped
   adapter's handler cannot reacquire; a second writer acquires after each. Which ATAS callback fires stays NOT verified.
6. **Test-count integrity:** reproduce the two silently deleted tests being present at `f8a724c` (names in the record)
   and that the suite count 432 is real (75/111/246 twice).

Record `records/U14-verify-r6.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. NOT verified
by name (Windows: everything; ATAS teardown callback). Do not fix; do not push; full suite at most twice.
