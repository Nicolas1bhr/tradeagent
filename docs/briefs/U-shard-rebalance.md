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
