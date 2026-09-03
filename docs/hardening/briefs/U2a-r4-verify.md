# U2a — ADVERSARIAL-VERIFY BRIEF · round 4 (targeted) on the REBASED tip

**Tier T1.** You are leg [2]: an independent Opus verifier. **Your job is to break the change, not confirm it.** Default
to "fails" when uncertain. The previous verifier for this unit is gone (process restart); its records were lost. Leg [3]
(Codex, read-only) runs concurrently on the same sha in a different worktree — you will not see each other.

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` (mandatory; §0, §3, §6, §9).
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md`.
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a.md` — the unit record: what
   each round fixed, the verdict history, and "Open at handoff" (your target list). Reconstructed; the branch is the truth.
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u2a-pipe-hardening.md`.
5. `docs/CONTRACTS.md` and `AGENTS.md` in your worktree (the replay contract and the id restriction are release facts).

## Where you work

- Worktree: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r4`, detached at
  **`e91293e`** = `u2a-pipe-hardening` (21 commits) rebased onto `main` `9fd5eb7` (which carries U2b, approval
  re-authorization, in `GatewayTypes.cs`/`TradingGateway.cs`). The rebase was textually clean and the full suite was run
  on it by the manager (figure in `records/manager-log-2026-09-03b.md`). First command: `git checkout -b u2a-verify-r4-probes`
  so your probe commits stay reachable. Work ONLY there.
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`. No `timeout` binary; tool calls cap at
  10 min; run the full suite at most twice, output to a file, read the tail. Targeted `dotnet test --filter` otherwise.
- The Windows box is OFFLINE. Named-pipe buffer semantics, the handle-dispose kill and the no-buffer stall (mutant B4)
  cannot bite on macOS — list them under NOT verified, do not pretend.

## Targets (round 4 is targeted, §9.5 — these five, then stop)

1. **Intent-based classification, measured ALONE per caller.** `IsRiskReducing` keys on intent: `Cancel`/`CancelAll`/
   `Close` get the 2 s emergency gate whoever asks (agent over the pipe AND operator in-process); `Place`/`Modify` never.
   Measure each caller on its OWN stalled bridge — the builder found that measuring both together let the button's drop
   free the leg. Numbers, not adjectives: expected ≈2.0 s for both callers; round 3 measured the agent at 9707 ms.
2. **Progress-aware expiry under saturation** (1500 × 900 KiB RPCs): the connection must be KEPT, the caller gets
   `Busy`/UNKNOWN within ≈2 s with the "bridge is busy" reason; a truly stalled peer is dropped with "not responding".
   Both directions: a busy bridge is never dropped; a stalled one never survives.
3. **61-char client-order-id budget at the pipe** (64 minus `TA-`): a 62-char id refused, 61 accepted, charset
   `[A-Za-z0-9-]` enforced, and the CLI's OWN minted ids (`op-{nonce}-{intent}-{index}`) pass as the positive control.
4. **The rebase did not undo a round 1–3 fix or break U2b.** Re-run the round-1 exploit over the pipe: `session:"operator"`
   (all seven spellings) with STOP pressed must be refused with INVALID_REQUEST, and the approval re-authorization path
   from U2b must still re-check every gate when a parked order is approved. Both directions: legitimate operator actions
   in-process still work.
5. **Suite stability at shipped defaults**: the full suite once under load (run the saturation probe concurrently) —
   report the wall time and any flake by name.

## Method (non-negotiable)

- Red-first refutation: for each target write the probe/test that PASSES if the defect exists, run it, quote the output.
- Mutants: for every guard behind targets 1–3, apply a mutant in your worktree (commit first; restore from a `cp` copy,
  never `git checkout --`; `touch` after restore) and watch the existing test bite. A guard with no biting test is a
  finding (MED) even when the behaviour is correct today.
- Severity: HIGH = money/authority/loss-of-order-state; MED = a guard without a biting test or a measured miss of the
  stated bound; LOW = wording, docs, tidiness. ≥2 findings with one root cause → name the CLASS (§9.10).

## Record (checkpoint as you go — a result held only in context dies with the connection)

Write `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a-verify-r4.md` (the MAIN
worktree path; do not run git there — the manager commits it). Structure: sha under test · per-target: refutation
executed (command + output tail) · mutants table (mutant · test · bit? · restored sha) · findings ranked HIGH/MED/LOW
with `file:line`, the concrete risk and the exact fix expectation · **NOT verified** list (macOS limits, box-only items)
· "What I did NOT do" · verdict line: `VERDICT: PASS | PASS WITH LOW | FAIL — nH/nM/nL`. Update the file after every
target, not at the end. Banned words: should work, looks correct, probably, I believe, minor, trivial, static-verified.

Do not fix anything. Do not push. Report back the verdict line, the findings in one line each, and the record path.
