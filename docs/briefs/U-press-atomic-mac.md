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

## Report — append as you go, commit it, ≤12 lines: tip sha; per item RED → GREEN → mutant; counts; CI per platform;
what you did NOT do. Verified or NOT VERIFIED.

- Mine only: `tests/TradeAgent.FaultTests/PressAtomicityTests.cs` and the fault harness
  (`tests/Shared/RecordingConnector.cs`: a null-by-default `Seam` hook, and `Close` gated like the rest).
  NO product file changed — both mutants reverted, `git status` shows nothing under `src/`.
- **Item 1 RED** (a seam letting A's fill land before B's re-read; old assertion): `Assert.Single() Failure:
  The collection did not contain any matching items` with `press B : ok — Nothing was sent for 1 of them,
  because what is there changed after you pressed: ES was 2 when you pressed and is 0 now` — CI 33958941039
  byte for byte. **GREEN**: the invariants (1 close on the wire, 2 orders, flat, 1 press row, one nonce) and
  B's answer one of the refusals, both named — plus the third the capture read can give ("nothing open to
  close"). 4/4 five times. That RED seam is kept as `The_drift_re_read_refuses_the_second_press_when_the_
  first_fill_landed_first`, so the macos schedule is now proven on every runner, not only a slow one.
- **Item 2** `Only_the_atomic_claim_can_refuse_a_press_whose_drift_re_read_saw_no_change`: the seam holds the
  winning press's close until the other has answered, so the book cannot move under the loser's re-read and
  only the insert can refuse it — GREEN, `press B : EMERGENCY_PRESS_UNRESOLVED — close-all sent at 14:45;
  resolve it first`, and the drift sentence asserted absent. **Mutant** (the `NOT EXISTS` claim clause alone,
  `AND ($claim IS NULL OR 1=1)`) → RED `Assert.Equal() Failure ... Expected: 1 Actual: 2` close calls, 2 press
  rows, both presses "ok". **Item 1's mutant** (that clause AND `live != quantity` bypassed) → the same RED on
  all three double-press tests: 2 closes, 2 rows.
