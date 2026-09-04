# U14 — ADVERSARIAL-VERIFY BRIEF · round 8 (targeted) at the sha named in the dispatch

Same verifier (context intact). Sha under test = `4de7c25` + round 8 (`briefs/U14-r8-bounce.md`: PRIOR 17 directory
absence, PRIOR 21 stopped-guard race, F26/R2 `finally`, F27+V3 rotation carry-forward, F28 unreadable sidecar, F23
heartbeat-timeout drop, F29 wording, V4 row precedence, V5 the unbuilt state; five refutations to be written in the
record). Worktree `u14-verify-r8`, detached at the sha; `git checkout -b u14-verify-r8-probes`; cherry-pick from
`u14-verify-r7-probes` as useful. Work ONLY there; the box is not yours.

## Targets (then stop)

1. **Absence predicate:** remove the directory under a live witness → `Submitting` false, nothing written; then every
   read-failure variant from round 6 again (chmod 000, a directory at the path, short read, mid-read failure) — one
   predicate, no split.
2. **Lease on every terminal path, with the race:** `UntrackSecurities` throwing during stop and during dispose → a
   replacement adapter acquires; the `Identified`-between-check-and-dispose interleaving cannot reacquire.
3. **Rotation crash window:** seed `.1` with an unresolved ERROR, oversized diagnostic current log, terminate after the
   rotation write and before any later deciding append, restart → degraded survives; and the corrected V3 claim/test.
4. **Unreadable sidecar** held `FileShare.None` → standing unreadable/degraded, zero provisional.
5. **F23 / the V1 class:** compatible handshake → silence past `HeartbeatTimeout` → a second bridge with a deadline
   connects; markers preserved; a partial-frame peer and a stale-open peer likewise. Does the drop on heartbeat
   timeout interact badly with a legitimately quiet bridge (no orders for minutes)? What keeps it alive — measure.
6. **V4 precedence:** v2 refusal → reinstalled bridge fails authentication → the row says the AUTH sentence; and the
   reverse order; a live good bridge clears both. **V5:** the exact state (canonical diagnostic-only + a refused
   writer's unresolved safety line) built and pinned; MV2c RED; MV2b still inert.
7. **The five refutations** written in the record with the manager's reasons — read them; if a refutation is WRONG,
   that is a finding.
8. Full suite once; your standing harnesses (R3 per-writer, F17 variants, MD1) still bite.

Record `records/U14-verify-r8.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. NOT
verified by name (Windows entirely; ATAS teardown callback). Do not fix; do not push; full suite at most twice.
