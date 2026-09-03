# U2a — BUILD BRIEF · round 4b: one test fails after the rebase onto main

**Tier T1.** You are leg [1] (builder) for a narrow round: the unit's 21 commits were rebased onto `main` (clean
textually) and exactly one test now fails, deterministically. Diagnose the root cause, fix it without weakening either
unit's guarantee, prove the fix bites. Leg [2] (Opus adversarial verifier) and leg [3] (Codex) run after you report, on
your final sha, each in its own worktree. **Round cap: 2.**

**FIRST, in this session, read in full:**
1. `/Users/nicolasbeeckman/Projects/innovision-os/innovision-os/docs/ORCHESTRATION-STANDARD.md` (mandatory read-gate).
2. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/CLAUDE.md`.
3. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a.md` — especially "Round 4"
   (the progress-aware emergency-gate expiry: a busy gate-holder yields `Busy` with "the bridge is busy" and the
   connection is KEPT; a stalled one is dropped with "not responding") and "Round 2" (Sent / PeerStalled / Busy).
4. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2b.md` — what `main` gained
   in `GatewayTypes.cs` / `TradingGateway.cs` / `Stores.cs` / `Errors.cs` (approval re-authorization, one clock).
5. `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/commits-u2a-pipe-hardening.md`.

## Where you work

- Worktree `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-rebase-probe`, branch
  `u2a-rebase-probe` @ **`e91293e`** (= `u2a-pipe-hardening` `5c716aa` rebased onto `main` `9fd5eb7`). Commit your fix
  on THIS branch; the manager moves `u2a-pipe-hardening` to your tip afterwards. Do not touch that branch ref yourself.
- Comparison worktree, read-only: `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-orig`
  detached at `5c716aa` (the pre-rebase tip). Build and run tests there to compare; never edit there.
- Toolchain: `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`. No `timeout` binary; tool calls cap at
  10 min; the full suite takes ≈2 min 10 s — run it with output to a file and read the tail.
- The Windows box is OFFLINE.

## The failure (measured by the manager on `e91293e`, full suite 75 + 108 + 207 green, 1 red)

```
TradeAgent.Tests.Integration.ConnectorSendDeadlineTests.An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it [FAIL]
Assert.ThrowsAny() Failure: No exception was thrown   Expected: typeof(System.Exception)
tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs:line 413 (and 426)
```

Three isolated re-runs (`--filter FullyQualifiedName=...`): red, red, red, each ≈ 0.8 s. So the emergency call behind
a busy bridge returned normally instead of failing with `Busy` — or the fixture no longer makes the bridge busy.

## What to deliver

**0. Bisect the cause class.** Run the same test in `u2a-orig` (`5c716aa`). Record: green there → the rebase onto U2b
changed the behaviour (find WHICH main-side change: diff `e91293e` against `5c716aa` for the files U2b touched and
read the interaction); red there too → the round-4 builder's "360 green" was taken before its last commit, and the
defect is U2a's own. Write the answer and the evidence in the record before fixing anything.

**1. Root cause, then fix.** The fix must keep BOTH guarantees: U2b's re-check of every gate at approval time and one
gateway clock; U2a's busy-vs-stalled distinction (busy → `Busy`, connection kept, "the bridge is busy"; stalled →
dropped, "not responding"; `Place`/`Modify` never on the fast path). If the honest fix is in the TEST (it asserted a
mechanism, not the behaviour), say so and cite the round-4 design sentence that the new assertion pins; if it is in
the product, the failing test is your RED and must go GREEN unchanged. If the fix needs a U2b-owned file
(`TradingGateway.cs`, `GatewayTypes.cs`, `Stores.cs`, `Errors.cs`) beyond a mechanical merge, STOP and report before
editing it.

**2. Prove the tooth.** Mutant: make the busy path drop the bridge (or return `Sent`) → the test must go RED → restore
from a `cp` copy, `touch`, GREEN. Quote both runs.

**3. Gates.** Targeted: the whole `ConnectorSendDeadlineTests` class. Then `dotnet build TradeAgent.sln` and the FULL
suite once; paste the per-project counts. Any RED = not done. Watch for other timing tests that the rebase may have
shifted — if one flakes, name it, do not re-run it into green silently.

## Rules

- Commit on the branch BEFORE any mutant run; restore mutants from a `cp` copy, never `git checkout --`; `touch` after
  restore. No `Co-Authored-By` trailers. One-sentence commit messages saying what changed and why.
- Checkpoint as you go: append `## Round 4b — post-rebase fix (build record, 2026-09-03)` to
  `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a.md` (MAIN worktree path;
  do not run git there — the manager commits it). Update it after step 0, after the fix, after the mutant.
- Honesty contract (§6): "verified by running X → output" or "NOT verified: why". Banned words: should work, looks
  correct, probably, I believe, minor, trivial, static-verified, basically.
- §9.9: answer in the record whether a gate could catch "a rebase changes a measured behaviour and the claim is not
  re-measured" next time. Answer only.
- Do not push, merge or rebase. Do not touch other worktrees.

## Report back

Tip sha; the bisect answer (one line); root cause (two sentences); where the fix landed (test or product, which file);
the mutant bite quoted; suite counts; "What I did NOT do".
