# U2a — ADVERSARIAL-VERIFY BRIEF · rounds 10 + 11 (targeted) at the sha named in the dispatch

FRESH verifier (the round-10 verifier was killed before writing anything). Read first: `records/U2a-verify-r9.md` (the
rounds 8+9 verdict) and its probe branch `u2a-verify-r9-probes`; the builder's `records/U2a.md` "## Round 10" and
"## Round 11" (the handler depth table, the five words, transport-result classification, the null-transport rule, the
box session with the flake verdict); `briefs/U2a-r10-bounce.md` and `U2a-r11-bounce.md`; `records/codex-U2a-r9.txt` and
`codex-U2a-r10.txt`; `docs/CONTRACTS.md` in the worktree. Worktree `u2a-verify-r11`, detached at the sha;
`git checkout -b u2a-verify-r11-probes`. Work ONLY there; the box is not yours unless the dispatch grants one run.

## Targets (then stop)

1. **The drain table, attacked:** for every handler the dispatcher knows (enumerate from the dispatcher, not the
   table) measure the serial chain at fake latency and compare with the table; the derived drain ≥ every measured chain
   at three customised timeout sets; close-all at four positions disposed mid-wave leaves nothing unsettled; the four
   operations round 11 added are in the table with the right depth.
2. **Vocabulary exactly five per leg** (+ `nothing-to-do` only at operation level): deserialise every reachable reply
   and assert membership; each word ↔ one record state both directions; CONTRACTS.md matches the code.
3. **Classification by transport result, every arm:** pre-wire `Busy` → `not-sent`; gate-expiry `PeerStalled` →
   `not-sent`; cancellation BEFORE gate acquisition → `NothingWritten` → `not-sent`; a fully written frame with the
   reply withheld and the caller cancelled → `PossiblyWritten` → `sent-not-confirmed` with UNKNOWN persisted; **a null
   transport result maps to `sent-not-confirmed`, never `not-sent`** (mutant: null → not-sent must go RED); DISPATCHING /
   RECONCILING with `NothingWritten` → `not-sent`, with `PossiblyWritten` → `sent-not-confirmed`; a definite reply → its
   word. Hunt the state the mapper has no arm for.
4. **Disposal never silent:** with a cancellation-honouring connector, disposal waits the full derived drain and logs
   `handlers_did_not_finish` with the request id at error when a request stays unsettled; the deferral to U2c-1 C4 is
   written with the measurement.
5. **The Windows flake verdict:** read the builder's box session (two full runs + the single test thrice); if the test
   was changed, confirm on the Mac that it still enters its branch (premise-asserted) and bites its mutant; if it was
   rated a product timing hole, that is a finding to carry.
6. **Regression:** the rounds 8+9 probes (one deadline per operation ≈2 s; the five-order sweep with a mixed answer;
   the composite-drain cold placement; the 12-phase liveness drop at ≈10 s; C1 2005 ms) still hold; full suite once;
   0 warnings `--no-incremental`; test-name diff against `088c059` shows 0 removed.

Record `records/U2a-verify-r11.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
