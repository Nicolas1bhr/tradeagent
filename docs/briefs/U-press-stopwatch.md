# U-press-stopwatch — the press-budget stopwatch went 1.2 s over on ubuntu: measure which step, then fix the class

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, the `U-press-budget`, `U-win-timing` and
`U-override-lease` sections at the end of `BUILD-STATUS.md`, `.github/workflows/build.yml` (the `Timing` category is
run separately and retried once on EVERY runner), then the test. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. No box; draft PRs are your instrument for the hosted runners. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/press-stopwatch`, branch `u-press-stopwatch` from `main`. Run a full
suite on this Mac only when `pgrep -f testhost` shows none: two builders run suites here.

**What happened.** CI run 33986791747 at `d14a2f0` (the U-override-lease merge), ubuntu, Fault 241/242:
`OperatorPressIsAnEmergencyTests.Cancel_all_gives_up_on_a_stalled_platform_inside_the_emergency_budget` —
`cancel-all returned 1243 ms after the deadline the press itself opened, against 1s of handler overhead; the emergency
budget was not the thing that ran out` (`EmergencyPressTests.cs:338`, `:421`). Windows and macos: see the run. The same
sha passed the whole Fault suite twice on this Mac (the builder's gate and the manager's), and U-override-lease's diff
adds no wait, lock or timeout — it adds a settle arm that applies only to a row a person resolved, which this test never
creates. History of the class on ubuntu: `the press took 3.4s against a 2s emergency budget` at `88617a0` (before
U-press-budget rewrote the assertion onto the press's own clock and measured the press's own cost at 5–7 ms there);
`A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline` over by 24 ms on 33974342472; now 1243 ms.

1. **Measure before you move anything.** Re-create U-press-budget's throwaway harness (`d734e42`, taken back out at
   `a4ff1e8`: per-step timestamps inside `OperatorCancelAllAsync` against the same stalled platform) on a draft PR and
   read all three runners 3× each: WHICH step — write-ahead rows, latch, the cancel leg's cut, the settles, the activity
   line, the composite row — carried the overrun, and whether it is the same step every time. Quote the numbers.
2. **If a product step is off the emergency clock** (a write or a wait that the deadline does not bound), fix it
   red-first with a mutant, like U-press-budget did for the simulator's wait. The shipped 2 s stays.
3. **Otherwise the runner stalled the process, and the fix is the class, not the instance:** the tests that assert the
   product's promise on a wall clock the runner does not keep join the `Timing` category — `OperatorPressIsAnEmergencyTests`,
   `PressReachesTheWireOnItsOwnTermsTests`, `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc`
   and, if its 52 s on 33973192760 was the same shape, `ControlTests.Cancel_all_removes_orders_but_leaves_positions_alone` —
   with the category's meaning written once in the workflow comment and in `docs/HOW-WE-BUILD.md` step 6. Nothing is
   loosened: the assertions stay exactly as they are; a red twice in a row is still a red run.
4. Take the harness back out. Gate: Release `--no-incremental` → 0 warnings; the moved classes 3× each; full suite once
   to a file; names vs `main` 0 removed; the draft PR green on all three runners twice (the category step included).

## Report — append here, commit it, ≤16 lines: tip sha; the per-step numbers per runner; which of (2)/(3) and why; the
tests moved; gate counts; the two PR runs; what you did NOT do. Verified or NOT VERIFIED.
