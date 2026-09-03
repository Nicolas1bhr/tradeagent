# U2a — ADVERSARIAL-VERIFY BRIEF · round 6 (targeted) at `ffa1a3d`

Same verifier (context intact). Sha under test **`ffa1a3d`** = `0909ada` + 4 commits answering `briefs/U2a-r6-bounce.md`
(builder's record: `records/U2a.md` "## Round 6"). Builder's claims: Mac **436 green** (75/108/253); box **436 green,
identical test for test**, with the tree identity verified by SHA-256 of four changed files + the `.cs` count before and
after a single-session run (quoted in the record — the round-5 box figures are WITHDRAWN); F-B implemented NOT as the
bounce's `_lastWriteProgressAt` rule (tried: kept the wedged peer 2/12 AND dropped a healthy reader 3/14 — the
emergency frame is ~100 bytes into an 8 KiB socket buffer, so the kernel accepts it whether or not anyone reads) but as
**liveness = an ANSWER to a pending RPC** — the manager RATIFIED this (it is the one signal a frozen read loop cannot
produce; consequence: a bridge that reads but answers nothing for the whole window is now dropped); F-C W3 bites; F-D
reads have their own wording; two tests (F11's only through-the-gateway test and F2's ceiling test) were silently
deleted by a text slice and restored verbatim; a Windows-only fixture race (cancel + dispose racing an overlapped
write) crashed the Windows test host and was fixed. F-A stays with U2c-1.

Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r6`, detached at
`ffa1a3d`; first command `git checkout -b u2a-verify-r6-probes`; cherry-pick from `u2a-verify-r5-probes` as useful.
Work ONLY there; the box is not yours.

## Targets (then stop)

1. **Liveness-as-answer, both directions, at shipped values:** (a) a wedged peer that heartbeats on its own task and
   answers nothing → dropped 12/12 phases with "not responding"; (b) a saturated-but-answering bridge (1500 × 900 KiB) →
   kept, "busy"; (c) a slow bridge that answers ONE pending RPC late in the window → kept; (d) the new consequence: a
   bridge that reads everything and answers nothing for the whole window → dropped — decide whether any legitimate ATAS
   state looks like (d) (e.g. a long synchronous ATAS call holding the command loop while the bridge reads) and rate it.
2. **F-C:** W3 RED; the recovery line names the id on the read-failure path; W2/W4 still RED.
3. **F-D:** read wording vs order wording, both mutants RED; no owner sentence sends someone hunting an order a read
   never placed.
4. **Restored tests:** F11's through-the-gateway test and F2's ceiling test are present and bite (re-run one mutant each).
5. **The Windows fixture fix:** read it; does it change what the tests prove on macOS (a fixture that no longer races may
   also no longer exercise the drop path)? Confirm the drop-path tests still enter the branch.
6. **436 green once** (75/108/253) and your standing probes (seven spellings, M15/M16) still bite.

Record `records/U2a-verify-r6.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. NOT verified
by name. Do not fix; do not push; full suite at most twice.
