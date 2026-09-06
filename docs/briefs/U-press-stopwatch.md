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

## Report

Code tip `0c0d53c` (this report is the commit on top of it, this file only), draft PR #8. Every claim below is "verified by running X → output"; the two NOT VERIFIED items are named at the end.

1. **Measured first, on the draft PR, 3 runs × 3 runners (run 33996443013 attempts 1–3 at harness commit `1b6054c`, plus the killed fixer's run 33987436723).** It is the SAME step nearly every time, and the process was RUNNING while it ran: the overrun is carried by ONE post-deadline local SQLite settle at `synchronous=FULL` — `SafelyRecordIndefinite`, seen as `leg-settle-indefinite` or as the same call at `press-settle`. ubuntu 547 / 593 / 358 / 129 / 113 ms in the bad runs and 2–5 ms in the two good ones, off the same code and the same fixture (a 200× spread); windows 63–187 ms (worst press 313 ms over); macos local steps ≤27 ms with the 9–136 ms overrun sitting in the connector call instead, because macos delivered a 1200 ms timer at 1204–1337 ms. Throughout: a 20 ms tick on a dedicated thread AND on the pool kept arriving at 21 ms (ubuntu), 32–47 (windows), 145–181 (macos, its floor); `gcPause=0`; pool queue 0–1. So it is the runner's file IO — not descheduling, not GC, not pool starvation.
2. **(3), not (2).** Nothing in the press is wrongly off the emergency clock: what runs after the deadline is the write-ahead record of what the press learned (`LatchUnconfirmed` + settle + activity + engineering rows — the contract's H, which `HandlerOverhead` bounds), and putting it under the emergency deadline would abandon the record `U-stranded` exists to keep. `git diff main -- src/` is empty: product code untouched.
3. **Moved into `Timing`, assertions byte-identical:** `OperatorPressIsAnEmergencyTests` (class, 5 tests) and `SweepRequestIdTests.A_sweep_pays_the_emergency_budget_once_not_once_per_rpc` + `.A_five_order_sweep_answers_within_the_budget_and_accounts_for_every_order` (one root cause, one fix). The category's meaning is written once in `build.yml` and once in `docs/HOW-WE-BUILD.md` step 6, and **its one second attempt now runs on every runner rather than windows only** — the numbers in (1) are what retires "windows is the flaky one". `RunnerSpeedProbeTests`'s comment stated the old rule and was corrected.
4. **NOT moved, against the brief's candidate list, on measurement.** `PressReachesTheWireOnItsOwnTermsTests` asserts no wall clock at all — it checks what was sent and what was recorded — so it fails the meaning written in (3). `ControlTests.Cancel_all_removes_orders_but_leaves_positions_alone` (52 s, run 33973192760) is NOT shown to be the same shape: I measured its exact shape — a press with no injected latency — 24 times across the three runners, and it never exceeded 63 ms against its 2 s budget. It stays one unexplained occurrence, unbriefed.
5. **Harness out.** `_RunnerMeasurement.cs` deleted and `PressStep`/`Step()` removed; `grep -rn "PressStep\|ZPressStopwatch\|_RunnerMeasurement" src tests .github docs` → no matches.
6. **Gate.** `dotnet clean` then Release `--no-incremental`, all 17 projects → **0 Warning(s), 0 Error(s)**. Moved classes 3× each: `OperatorPressIsAnEmergencyTests` → `Passed: 5, Failed: 0` three times; the two sweep tests → `Passed: 2, Failed: 0` three times. Test names vs `main`, 860 declarations each side → **0 removed, 0 added**. Partition exact: `Category=Timing` 93 + `Category!=Timing` 961 = 1054 listed. Secret scan of the whole diff vs `main` → clean.
7. **Draft PR #8 green TWICE on all three runners, the category step included: run 33997442226 attempt 1** — test (ubuntu-latest) success, test (windows-latest) success, test (macos-latest) success, `package` success — **and attempt 2**, the same four. Per runner per run: 960 non-timing + 93 timing = **1053 passed, 0 failed**, with **0 runtime retry markers** on any runner in either run, so both greens are first-attempt greens and not rescued ones.

**NOT VERIFIED / NOT done.** The local full suite was NOT run: another leg's suite (`two-press-grant`, then `codex-2b`) held 4–9 `testhost` processes on this Mac continuously through the whole window, and one gate at a time is the rule — the full-suite evidence is CI's in (7), 1053 × 3 platforms × 2 runs in Release. The windows 52 s `ControlTests` red and the 24 ms `A_wait_the_simulator_predicted_would_fit_is_still_stopped_by_the_deadline` red on 33974342472 are NOT explained. No product code, no box, no ATAS, no UI; nothing loosened, and the shipped 2 s is untouched. `main` moved to `be13d7d` (U-two-press-grant, U-bridge-2) AFTER the two green runs and the branch was not rebased onto it: `comm -12` over the two file lists shows **zero overlap** with this branch's six files, so the landing rebase is clean and its re-gate is the manager's.
