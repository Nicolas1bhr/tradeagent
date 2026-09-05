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
