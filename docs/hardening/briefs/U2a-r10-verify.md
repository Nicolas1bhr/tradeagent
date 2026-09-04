# U2a — ADVERSARIAL-VERIFY BRIEF · round 10 (targeted) at the sha named in the dispatch

FRESH verifier unless the dispatch says otherwise. Read first: `records/U2a-verify-r9.md` (the rounds 8+9 verdict) and
its probe branch `u2a-verify-r9-probes`; the builder's `records/U2a.md` "## Round 10" including the per-handler serial
depth table; `briefs/U2a-r10-bounce.md`; `records/codex-U2a-r9.txt`; `docs/CONTRACTS.md` in the worktree (the table
and the five-word vocabulary are release facts there). Worktree `u2a-verify-r10`, detached at the sha;
`git checkout -b u2a-verify-r10-probes`. Work ONLY there; the box is not yours unless the dispatch grants one run.

## Targets (then stop)

1. **The drain table:** for every handler (place, modify, cancel, cancel-all, close, close-all, each read) measure the
   serial chain at fake latency and compare with the table's depth; the derived drain ≥ every measured chain at THREE
   customised timeout sets (shipped; E large/W small; W large/E small); close-all with four positions disposed
   mid-wave leaves nothing unsettled. Attack the table: a handler or a path the builder did not list (a modify that
   resolves its target twice; a close that re-reads positions; a cancel-all whose targets need resolution each).
2. **Vocabulary exactly five per leg** + `nothing-to-do` only at operation level: deserialise every reachable reply
   (sweeps, single ops, replays) and assert membership; each word ↔ one record state, both directions (a WORKING record
   is never reported `confirmed`; a `rejected` leg's record is REJECTED); CONTRACTS.md matches the code.
3. **Classification by wire certainty:** pre-wire `Busy` → `not-sent`; gate-expiry `PeerStalled` → `not-sent`; every
   `sent-not-confirmed` leg has UNKNOWN + `NeedsReconciliation=true`; disposal cancelling a leg before the wire →
   `not-sent`, after the wire → `sent-not-confirmed` with UNKNOWN persisted BEFORE disposal returns; a late definite
   answer after the deadline → still `sent-not-confirmed` at answer time (settlement is U2c-1's). Mutant per arm.
4. **F2 deferral written** in the record with owner U2c-1 C1; the round-4 rule (a `Place` never takes the fast path)
   still holds in code.
5. **Regression:** the rounds 8+9 probes (one deadline per operation ≈2 s with three 1.9 s legs; the five-order sweep;
   the composite-drain cold placement) still hold; full suite once; 0 warnings `--no-incremental`.

Record `records/U2a-verify-r10.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
