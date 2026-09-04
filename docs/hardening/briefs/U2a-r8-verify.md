# U2a — ADVERSARIAL-VERIFY BRIEF · round 8 (targeted) at the sha named in the dispatch

FRESH verifier (the round 4–7 verifier's session is gone). Read its records first — `records/U2a-verify-r4.md` … `-r7.md` — and rebuild what you need from its probe branches (`u2a-verify-r7-probes` holds the latest harnesses: the 12-phase liveness probe, the C1 one-clock fixture, the seven-spellings exploit, M15/M16, W3). Its findings are your baseline; its verdicts are not yours to trust. Sha under test = `a974142` + round 8 (`briefs/U2a-r8-bounce.md`: Codex F1 one absolute
deadline per emergency OPERATION with per-leg outcomes reported and nothing skipped silently; F2 the drain derived from
the handler's worst-case COMPOSITE and disposal never unsettled; F3 the late-answer race; F-G outcome-first wording).
Worktree `u2a-verify-r8`, detached at the sha; `git checkout -b u2a-verify-r8-probes`; cherry-pick from
`u2a-verify-r7-probes` as useful. Work ONLY there; the box is not yours.

## Targets (then stop)

1. **One deadline per operation:** Codex's check (orders reply, target-resolution reply and cancel reply each delayed
   1.9 s → IPC `cancel-all` ≈2 s, not 5.7 s); then a five-order sweep on a 1 s-per-leg connector → answer at ≈2 s with
   per-leg outcomes listed (which sent, which confirmed, which not sent) and UNKNOWN recorded for the unconfirmed;
   nothing skipped silently — a leg that was never sent is NAMED in the answer. Then the both-directions half: a sweep
   that fits (five legs at 100 ms) completes fully with every leg confirmed. If legs are sent concurrently, look for the
   ordering hazard (a cancel leg racing its own target resolution) and for the gate: does concurrency bypass the send
   gate's backpressure?
2. **Composite drain:** `LatencyMs=4000`, one working order, IPC `cancel-all`, dispose after its first read → the active
   cancel does not stay `DISPATCHING`; no literal drain left; the derived bound stated from live values in the record.
3. **F3:** a reply arriving between the caller's timeout and `_abandoned` registration is counted as a late answer and
   `_abandoned` does not leak (probe the interleaving, count the dictionary).
4. **F-G:** every emergency sentence at 2 s starts with the outcome; the connection detail follows; the starts-with
   assertion exists and bites under a reorder mutant.
5. **Regression:** your r7 probes (C1 2005 ms; the 12-phase F-E drop at ≈10 s; the second-emergency-during-grace case)
   still hold on the new clock model — the per-operation deadline must not have reintroduced a per-RPC restart anywhere.
6. Full suite once; standing probes (seven spellings, M15/M16, W3) still bite.

Record `records/U2a-verify-r8.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
