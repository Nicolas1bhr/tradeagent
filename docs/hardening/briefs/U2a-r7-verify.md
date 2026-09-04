# U2a — ADVERSARIAL-VERIFY BRIEF · round 7 (targeted) at the sha named in the dispatch

Same verifier (context intact). Sha under test = `ffa1a3d` + round 7 (`briefs/U2a-r7-bounce.md` + its addendum:
F-E liveness grace, F-F record, Codex C1 one clock, C3 derived drain, C4 both-ids-null, C5 ms-equality, PRIOR 8 CLI
wording, PRIOR 4 residual stated, PRIOR 12/14 record rewrite). Manager's decisions in force: `EmergencyDeadline` (2 s)
bounds ONLY the caller's wait, on ONE clock across gate + write + reply; liveness uses the ordinary RPC deadline (10 s)
as grace — a peer that answers nothing within it is dropped, a late answer (2 s < t ≤ 10 s) is kept and recorded on
the pending RPC; a wedged heartbeating peer is now detected at ≈10 s (stated cost).

Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r7`, detached at the
sha; first command `git checkout -b u2a-verify-r7-probes`; cherry-pick from `u2a-verify-r6-probes` as useful. Work
ONLY there; the box is not yours (the builder's verified-tree box run is a claim you read).

## Targets (then stop)

1. **C1 one clock:** hold `_sendGate` just under 2 s, release into insufficient pipe-buffer capacity → the emergency
   returns ≈2 s, not ≈4 s; the caller's sentence and UNKNOWN unchanged; place/modify still on their own budget.
2. **F-E as implemented:** answers at 2.5 s / 3.5 s → kept, "busy", late answer recorded on the pending RPC (find where
   it lands and whether anything reads it); answers nothing for 10 s → dropped; the wedged heartbeating peer dropped at
   ≈10 s, 12/12 phases; the caller's 2 s answer measured unchanged in all three. Then the question that matters: with a
   10 s grace, can a dead bridge hold the connection long enough that a SECOND emergency (or an ordinary order) queues
   behind it past its own deadline — what does the owner see, and is anything unsettled?
3. **C3:** change the connector deadlines in a test and assert the drain follows; no literal 55 s left anywhere.
4. **C4 / C5:** both ids null → `INVALID_REQUEST`, connection kept; ms-equality — an answer at the same millisecond as
   the deadline check is not discarded.
5. **PRIOR 8 CLI wording:** the CLI's replay promise matches what the gateway does today (Place only), pinned by a test;
   the recovery line still names the id for every op.
6. **Records:** `records/U2a.md` NOT-VERIFIED list matches reality (pipe classes measured on the verified-tree box run;
   B4 and the ATAS 64/65-char probe still open); PRIOR 4's threshold stated in CONTRACTS.md and the record.
7. **Full suite once** with your standing probes (seven spellings, M15/M16, W3) still biting.

Record `records/U2a-verify-r7.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
