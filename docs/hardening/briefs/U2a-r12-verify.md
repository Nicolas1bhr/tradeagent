# U2a — ADVERSARIAL-VERIFY BRIEF · round 12 (targeted, the last before integration) at the sha named in the dispatch

FRESH verifier unless the dispatch says otherwise. Read first: `records/U2a-verify-r11.md` (the rounds 10+11 verdict;
probes on `u2a-verify-r11-probes` at `93f6ec0` — reuse them); the builder's `records/U2a.md` "## Round 12";
`briefs/U2a-r12-bounce.md`; `records/codex-U2a-r11.txt`; `docs/CONTRACTS.md`. Worktree `u2a-verify-r12`, detached at
the sha; `git checkout -b u2a-verify-r12-probes`. Work ONLY there; the box is not yours (the builder's identity-checked
session at the tip is a claim you read).

## Targets (then stop)

1. **F-1:** agent disconnects → app closes with a DISPATCHING row → `handlers_did_not_finish` logged at error with the
   request id, unconditionally; the connected control unchanged; mutant (guard restored) RED. Then the both-directions
   half: an idle shutdown logs nothing and returns in milliseconds.
2. **F-2:** a fake connector that performs a mutating call WITHOUT marking an attempt → the leg reads
   `sent-not-confirmed` (the pipe server's own knowledge); the three never-dispatched legs (nothing-to-close,
   resolution-expires, parked-for-approval) still `not-sent`; `transport` present as explicit `null` in the JSON; the
   obligation stated on `ITradingConnector` and in CONTRACTS.md; the gateway-side marking recorded as routed to U2c-1.
   Hunt: a mutating step the pipe server does not know is mutating.
3. **LOW batch:** `_pending` returns to 0 after a cancelled emergency and the late answer is counted; the handler bound
   covers the pipe-server overhead at the verifier's W=300/E=900/S=50 case (and with S=0); the coverage set derives from
   the dispatch switch; the simulator's sentence agrees with the leg's word; the lifted 12-phase liveness probe bites a
   `PeerAnsweredSince` regression (mutant).
4. **Integration readiness (§8):** full suite once, 0 warnings `--no-incremental`; test-name diff against `120c739` shows
   0 removed; the standing probes (seven spellings, M15/M16, W3, C1 2005 ms, one deadline per op, 12-phase drop) still
   bite; every DEFERRED item in the record names its owner (U2c-1 C1–C5) with its measurement; no banned words in the
   round-12 record section.

Record `records/U2a-verify-r12.md` (MAIN worktree path; no git there), checkpoint per target, `VERDICT:` last. Do not
fix; do not push; full suite at most twice.
