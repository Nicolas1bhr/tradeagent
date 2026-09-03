# U2a — ADVERSARIAL-VERIFY BRIEF · round 5 (targeted on the bounce) at `0909ada`

Same verifier as round 4 (context intact). Sha under test **`0909ada`** = `d25dbb4` + 10 commits, one per finding
(`briefs/U2a-r5-bounce.md` answered; builder's record in `records/U2a.md` "Round 5"). Builder's claims: 421 green
(75/108/238) on the Mac AND identical on the Windows box (ConnectorSendDeadline 20/20, GatewayPipeBackpressure 12/12,
CliReplay+Operator+Sweep 56/56 there); every finding real, a mutant per finding; **red-first was INVERTED on V2, F11
and F2** (tests written against the fix, RED measured by reverting — a mutation proof, not red-first; the builder said
so). Manager rulings taken: `FrameTimeout` 30 s whole-frame ceiling (worst-case shutdown with an order in flight
35 s → 55 s, `WorstCaseOrderPath` = 10+30+10 asserted from live values) and `EmergencyGateWait` → `EmergencyDeadline`
(bounds gate + write + reply; still 2 s). F5/F6/F8-gateway are OPEN BY DECISION with U2c-1 — confirm they were not
touched, do not re-find them.

Worktree: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r5`, detached at
`0909ada`; first command `git checkout -b u2a-verify-r5-probes`; cherry-pick from `u2a-verify-r4-probes` as useful.
Work ONLY there. Box OFFLINE for you (the builder's Windows figures are claims you read).

## Targets (then stop)

1. **V1/F1 closed both ways:** the 203-char id and the forged `op-…` key now refused with zero broker orders; the CLI's
   own minted ids still pass; `RequestId` present-and-valid still works.
2. **V2 as implemented:** idle stalled bridge, free gate, emergency cancel-all → ≈ 2 s, the owner sentence, UNKNOWN;
   the connection's fate by liveness — dropped when no progress, KEPT when slow-but-answering (both directions; the
   builder read the 5 s heartbeat from source, not the box — check what the liveness rule actually keys on).
3. **F11 through the real gateway:** cancel-all with a stalled write holding the gate ≈ 2 s; a `place` inside the same
   scope still waits its full deadline; single cancel by broker id and the position read before a close.
4. **F2:** the 30 s ceiling and the derived drain bound — a steadily progressing peer no longer runs 100+ s; disposal
   re-awaits and never returns with an unsettled request; Codex's 8 KiB-per-9 s peer with a 64+ KiB order disposed after
   DISPATCHING. Attack the inversion: write YOUR OWN red-first probe for one of V2/F11/F2 against `d25dbb4` and confirm
   it is RED there and GREEN at `0909ada`.
5. **F4 measured fix** (a peer accepting 2048 B at 1 KiB/800 ms was dropped): confirm the progress unit and that the
   round-4 busy/stalled numbers still hold (2002 ms busy kept / 2006 ms stalled dropped).
6. **CLI tri-state:** every `PipeClient` exit path yields `NothingWritten` / `PossiblyWritten` / `ReplyReceived`; the
   SIGABRT-134 case; the ordering test observes ordering. Mutants for the two you consider weakest.
7. **Regression:** 421 green once; your round-4 T1/T4 probes (seven spellings with STOP; U2b re-check M15/M16) still bite.

Record: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a-verify-r5.md` (MAIN
worktree path; no git there), checkpoint per target, `VERDICT: PASS | PASS WITH LOW | FAIL — nH/nM/nL` last. NOT verified
by name (Windows for you; ATAS acceptance of ids). Do not fix; do not push; full suite at most twice.
