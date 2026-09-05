# U-press-atomic-mac — the double-press test asserts WHICH guard refused; either refusal is the product being right

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the U-press-atomic section at the end of
`BUILD-STATUS.md`, then `tests/TradeAgent.FaultTests/PressAtomicityTests.cs`. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; the fault suite ≈ 1 min in Release. No box; a draft PR is your only run
on the hosted runners. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-press-atomic-mac`, new
branch `u-press-atomic-mac` from `main`.

**What happened.** CI run 33958941039 at `9cc3fb4`, macos-latest, FaultTests 217/218:
`PressAtomicityTests.Two_close_all_presses_released_together_send_one_close_and_refuse_the_other` —
`Assert.Single() Failure: The collection did not contain any matching items`. The test's own log shows the product
right: `both inside the capture read: True`, `press A: ok — 1 of 1 record(s) from this press are still waiting for
you`, `close calls on the wire: 1`, `position after: flat`, `press rows: 1`. Press B was refused — by the DRIFT re-read
("Nothing was sent for 1 of them, because what is there changed after you pressed: ES was 2 when you pressed and is 0
now"), not by the atomic press guard ("close-all sent at HH:MM; resolve it first"), because on the slower runner
press A's close FILLED before B re-read the position. The test asserted the second sentence. Green 3× on this Mac,
in the manager's gate, and on ubuntu and windows.

1. **The invariant is what the test asserts.** Exactly one close on the wire, the position flat, one press row, and
   the other press refused with nothing sent — by whichever guard fires first. Assert the invariants and that press B's
   answer is one of the two refusals, naming both. RED first is the run above (reproduce by letting A's fill land
   before B's re-read — a connector latency of 0 for the fill, or a seam); GREEN; mutant (the atomic guard's
   `NOT EXISTS` clause neutralised AND the drift guard bypassed) → RED with two closes.
2. **The atomic guard still gets its own deterministic proof.** A second test holds A's fill until both presses have
   passed the drift re-read (a seam or a barrier the connector honours), so only the atomic guard can refuse B; assert
   the "resolve it first" sentence there. Mutant (the `NOT EXISTS` clause alone) → RED.

Yours: `PressAtomicityTests.cs` and the fault-suite harness it uses. Not yours: any product file — if you believe the
product is wrong, say so and stop. Commit per item, no trailers, no other worktree. Gate: Release `--no-incremental` →
0 warnings; the fault suite 3×; full suite once; push, draft PR, three job conclusions.

## Report — the gate and CI ran at `dab3b0e`; this report is the commit on top of it

- Mine only: `PressAtomicityTests.cs` + the fault harness (`RecordingConnector`: a null-by-default `Seam`, `Close`
  gated). NO product file — both mutants reverted, `git status` clean under `src/`.
- **Item 1 RED** (a seam landing A's fill before B's re-read; old assertion) is CI 33958941039 byte for byte:
  `Assert.Single() Failure: The collection did not contain any matching items` / `press B : ok — … ES was 2 when you
  pressed and is 0 now`. **GREEN**: invariants (1 close, 2 orders, flat, 1 press row, one nonce) + B's answer one of
  the refusals, both named. **Mutant** (claim clause AND drift guard) → RED `Expected: 1 Actual: 2`, 2 press rows.
- **Item 2** `Only_the_atomic_claim_can_refuse_a_press_whose_drift_re_read_saw_no_change` holds the winner's close until
  the other press has ANSWERED, so nothing else can refuse it: **GREEN** `EMERGENCY_PRESS_UNRESOLVED — close-all sent at
  14:45; resolve it first`, drift sentence absent; **mutant** (`NOT EXISTS` alone) → the same RED. Item 1's RED seam is
  kept as a third test, so that schedule now runs on every runner; class 4/4 17×, 12 of them under load.
- **Gate** at `dab3b0e`, Release: `--no-incremental` → 0 warnings; fault 220 3×; 218 + 220 + 582 = 1020, 0 failed; names
  0 removed, 2 added; scan clean. **CI** 33967839971 at `dab3b0e`, PR #5 (draft, NOT merged): ubuntu, windows and
  macos all SUCCESS — 218 + 220 + 496 + 86 = 1020 on each — and `package` SUCCESS.
- **NOT done:** no product file, no `BUILD-STATUS.md`, no box, no ATAS, no UI, nothing outside this worktree.
