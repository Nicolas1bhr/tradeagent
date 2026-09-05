# U-press-budget — the operator press must give up inside its two-second budget on a slow runner too

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` (the emergency budget; the five
per-leg words), the U2c1b, U2c1c and U-press-atomic sections of `BUILD-STATUS.md`, then the test named below.
`export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`; full suite 8–12 min in Release. No box;
CI on a draft PR is your only Linux run. Fresh worktree `~/Projects/ai-trading-software-for-mihael-worktrees/u-press-budget`,
new branch `u-press-budget` from `main` — after U-press-atomic has landed (same region of `TradingGateway.cs`).

**What happened.** CI run 33952871991 at `88617a0`, ubuntu-latest, FaultTests 194/195:
`Fault.OperatorPressIsAnEmergencyTests.Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget` —
"the press took 3.4s against a 2s emergency budget". Windows and macos green on the same sha; the test had not failed
in the five main runs before, and the runs after are recorded in `BUILD-STATUS.md` (read them: if it repeated, treat it
as deterministic on Linux). The test is in the fault suite, outside the `Timing` category that U-win-timing retries
once on windows-latest only. The runner-speed probe (`RunnerSpeedProbeTests`) says ubuntu's timers and CPU are under
1.6× this Mac, so a 1.4 s overshoot of a 2 s timer is NOT explained by runner speed alone.

**Establish first, then fix.** (1) Is the overshoot the product's? Read the press's path against a stalled platform
under `RiskReducingScope` (U2c1b item 5, U2c1c's intent): is there a read or a write inside the press that is not on
the emergency clock — the position re-read, the write-ahead row, the activity line, a connector call whose deadline is
the ordinary one? Reproduce locally by slowing that step (a seam, not a sleep in the product) → RED with the CI
message; fix so every step of the press is under the one 2 s budget; mutant → RED. (2) If every step is on the clock
and the overshoot is the runner's, say so with the measurement (the step that took the time, from the test's own
timing), and make the fixture assert the promise — the press RETURNED with "NOT confirmed" and sent nothing after the
budget — with the budget measured from the press's own clock, not the test's; never widen the shipped 2 s.

**Proof.** The test 5× locally in Release; the class once; the full suite once; push, draft PR, three job conclusions.
Commit per step, no trailers, no other worktree, not merged.

## Report — append as you go, commit it, ≤20 lines: tip sha; which of (1)/(2) it was and the measurement; RED → GREEN
→ mutant; local counts; CI per platform; what you did NOT do. Verified or NOT VERIFIED.

## Report — tip `e1cb46a`, `u-press-budget` rebased onto `main` `8e821fe`; draft PR #6, NOT merged
**IT IS (2), measured on all three hosted runners.** A throwaway harness pushed to CI (`d734e42`, run 33969167809, taken back out in `e800eb9`) timed `OperatorCancelAllAsync` against the same stalled platform, per call. ubuntu: press = **2005–2012 ms** [`orders 1→1200` full length inside the budget, `cancel 1202→2000` cut exactly at the deadline, `positions 2004→2004` refused with zero left]; macos 2013–2135; windows 2030–2038. The press's OWN cost with the latency knob at 0 — write-ahead rows, latch, composite rows, settles, activity line, every SQLite write at `synchronous=FULL` — is **5–7 ms ubuntu, 6–11 ms macos, 34–40 ms windows**. Nothing in the press is off the emergency clock and no step of it can be made faster.
**The one thing a runner's lateness could still reach was the INSTRUMENT.** `FakeConnector` PREDICTED — "1.2 s fits in the 2.0 s left, so run it" — then slept its full nominal latency unclipped, so a late timer made the prediction wrong and nothing re-checked it; shipped `AtasConnector` clips (`Left(deadlineAt)` on the write, `CancelAfter` on the reply) and never predicted. `TheCancellableWait` makes the simulator clip too, onto the same branch, sentence and `PossiblyWritten`; `UncancellableLatencyMs` and opening placements untouched. **RED** (new test; seam `FaultProfile.Wait`, 1.2 s declared delivered 2.2 s late): "a press whose simulator ran late returned **1409 ms** after the deadline the press itself opened" — a 3.4 s press, CI's own number. **GREEN** at ~10 ms. **Mutant** `CancelAfter(InfiniteTimeSpan)` → RED 1407 ms; **second mutant** `RiskReducingScope.Begin()` (no budget, both presses) → **4 RED**, both reshaped tests among them.
**The two timing tests assert the promise now, not a stopwatch:** the deadline the press itself opened, read back out of the scope; overrun < `GatewayPipeServer.HandlerOverhead` (the contract's H, which is exactly these local writes); the press RETURNED "not confirmed — check ATAS"; cancel-all's cut leg never reached the book. The 3 s and 4 s wall clocks are GONE, not raised, and the shipped 2 s is untouched.
**Gate**, Release at the tip: build `--no-incremental` → 0 warnings, 0 errors; the named test 5× → 5 passed; its class → 5 passed; suite → Unit 218 + Fault 221 + Integration 582 = **1021, 0 failed**; names vs `main` 0 removed, 1 added; scan clean.
**CI** run 33970406089 at `e1cb46a`: **ubuntu SUCCESS** (218 + 221 + 496 + 86). windows and macos FAILURE on the first attempt, both outside this change: windows `SweepRequestIdTests.A_five_order_sweep…`, the identical red BUILD-STATUS already records on `main` at docs-only `572c2be` (run 33966990967 — same `Not found: "confirmed"`, same multiset), and this clip can only ever report `PossiblyWritten`, never the `not-sent` those legs carry; macos the `Timing` test `Shutdown_waits_for_a_handler…placing_an_order`, a 5 s `WaitFor` on an OPENING `buy`, where this whole diff is `Task.Delay(int)` → `Task.Delay(TimeSpan)`. Both re-run at the same sha → **SUCCESS**; `package` SUCCESS.
**NOT done:** `TradingGateway.cs` untouched; no box, no ATAS, no UI; not merged. **NOT VERIFIED:** which runner-side stall produced the 1.4 s at `88617a0` — a late timer or a slow fsync. It did not recur, and that day's message carried only the total; it cannot be that again, because the new one names the step and measures the press's own clock.
