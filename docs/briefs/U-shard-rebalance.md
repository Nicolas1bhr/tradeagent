# U-shard-rebalance — the five windows test shards are rebalanced from measured per-class minutes on the work disk, so the longest shard, not the sum, sets the run's wall time, with every test still run exactly once
**Protects:** what CI proves (every test of the solution runs exactly once per platform; the complement shard still catches a new or renamed class; the exactness check stays red on a misplacement); the
builder's and landing's only full-suite gate (SPEED-MODE § 4-5); no test is skipped, re-categorised, renamed or made faster by changing what it does. Seat B (inherited from seat P); LIGHT, CI only: sonnet;
**no rung**. Plan: `fleet/handoff/P.md` § Next.
**Facts (SOURCE at `main` `eace5030`; re-check by `grep -n`).**
- `.github/ci/test-shards.txt` (106 lines): `shards 5`; lines `<shard> <class>` for shards 1-4; shard 5 = the complement (every class listed nowhere). Its header (:10-14) says it was balanced on run 38046993760's
  summed durations with test homes on C: — before U-ci-shards moved test homes to D: (the work disk), which changed the per-class figures. `.github/ci/shard-filter.sh` is its one reader (refuses a duplicate or a
  nested name); `.github/workflows/build.yml` `shard-plan` (:33-48) makes the matrix; shard 1 also runs the Timing category (~10 min).
- Measured on D: (seat P, run 38097157034): shards 1-5 ≈ 10 / 8 / 8 / 4 / 11 min including ~3 min setup. Fault and Integration run their tests one at a time (DisableTestParallelization), so a shard lasts about as
  long as the larger of its Fault and Integration sums; Unit runs classes in parallel. The method for per-class durations and the exactly-once proof: `fleet/tmp/P-briefs/trx-digest-38046993760.md` and the
  U-ci-shards report (`fleet/records/U-ci-shards.md`).
Must NOT: change a test, a category, `DisableTestParallelization`, the Timing step, `shard-filter.sh`'s refusals, the exactness check, the paths-ignore list, any job but the windows test shards' partition;
raise any timeout; drop the complement shard; change the shard count unless the measurement shows 5 cannot reach the target (then 6, named with the minutes that justify it — CI concurrency is shared by three seats).
Items, one commit each, one-sentence messages:
1. **Measure.** Per-class durations on D: from the trx of a recent green main or branch run (e.g. 38112158847 or 38098873084; `gh run download` its test artifacts, or the run's logs) — Fault, Integration and
   Unit summed per class per shard; write the table to your scratch (not the repo).
2. **Rebalance.** Move lines in `test-shards.txt` so each shard's larger serial sum (shard 1 counting Timing) is as even as the classes allow; target: the longest windows shard ≤ 8 min wall. Update the header's
   "balanced on" sentence to the run and disk you measured.
Proof (the report quotes it): one branch run — per-shard wall minutes before (a base run) and after; every test once per platform, by the U-ci-shards method (per-test diff of trx names vs a base run: 0 missing,
0 duplicated); the exactness check green; no `::warning` for a stale line.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings (no code changes, so only that); no local test run needed (CI is the measurement); the branch CI run
all green; names vs `main` 0 removed (both set sizes). `## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
Tip: see `git log` (report commit on top of 577a1761, the sha CI ran; the report commit is docs only). Base main 5c3f9b34 plus docs; rebased on main f1bdd2d2.
- Gate 1: `dotnet build -c Release --no-incremental` -> "0 Warning(s) 0 Error(s)". Gate 2: no local test run. Gate 5: tests box NOT RUN (CI-only unit).
- Gate 3: CI run 38114107185 (577a1761) success, all 11 jobs green; no `##[warning]` line in the log; every shard's exactness check green (shard 2: 42 listed/42 ran/0 ran none; 3: 38/38; 4: 35/35; 1: 2/2; 5: 0 lines ran).
- Gate 4: names.sh main u-shard-rebalance -> "removed: 0", sets base 2512 tip 2512 (Fact/Theory 2461/2461).
- Item 1 (measure, scratch only, no commit): per-class F/I/U sums from the trx of 6 windows runs on D: (38098873084, 38111240442, 38112135298, 38112158847, 38112348577, 38112363314).
- Item 2 (rebalance): commit 42f7e500 balanced on run 38098873084 alone; its branch run 38112363314 showed that was too noisy (a class moved 2 s -> 323 s between runs), so 577a1761 re-balances on the MEAN of all six runs (deviation: two commits; the header now names the six runs). Predicted serial sums F/I per shard 2..5: 341/381, 367/379, 345/380, 378/379 s (old partition, same means: 303/328, 301/301, 302/348, 524/542 -> shard 5 was the outlier).
- Exactly once (U-ci-shards method, trx (class,testName) multiset incl. Timing first attempt, windows shards 1-5 union vs main run 38112348577): 2919 vs 2919 tests, 0 missing, 0 duplicated, 0 extra; outcomes 2918 Passed + 1 NotExecuted in both.
- Wall minutes (job, windows shards 1..5): before (main 38112348577) 10.5/5.1/4.1/7.8/6.2; other old-partition runs: 10.4/8.8/4.4/8.4/13.2, 10.4/5.0/8.7/7.1/6.0, 10.4/7.7/7.6/9.4/14.3, base 38098873084 10.4/8.5/7.5/6.7/11.3. After (38114107185): 10.4/9.1/9.3/4.3/5.0 (first attempt, 38112363314: 10.7/9.0/10.0/8.6/12.4).
- The proof is inconclusive on wall time and I will not call it better: the SAME classes ran 2.5x faster on some runners than others (shard 4 test sums 409 s U vs 961 s on an equal-load shard in the same run), so one run's minutes are dominated by runner speed. What the data supports: old shard 5 was the outlier by mean (524/542 s vs ~300-350), the new means are within 4% of each other.
- Target "longest windows shard <= 8 min" NOT MET and not reachable by partitioning: shard 1 is 10.4 min in every run, because the Timing category alone is ~8.5 min (Timing step untouched per Must NOT). Shards 2-5 mean ~6.7 min test step expected; observed max 9.3 min on a slow runner.
- Not done: no 6th shard (not justified: shard 1/Timing sets the max); no Timing split; no tests-box run; nothing pushed after the report commit; no main push or merge.
