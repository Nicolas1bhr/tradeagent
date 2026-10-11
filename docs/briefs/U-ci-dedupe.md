# U-ci-dedupe — a push to main whose build tree a successful dispatched run already tested skips its jobs and names that run, so a landing no longer re-tests a tree land.sh has already proven green
**Protects:** what CI proves (a tree is skipped ONLY when a completed workflow_dispatch run of this same workflow, every job success, tested a commit whose tree equals this push's outside `fleet/bin/bookkeeping-paths.txt` —
the one list land.sh and `paths-ignore` already share; anything else runs everything); the honesty rule (the skipped run's summary names the run that tested its tree and the filter used; a lookup that fails, times
out or is ambiguous RUNS, never skips); the carry chain (land.sh never carries FROM a deduped run: `land.sh:105` requires every job `success`, and a skipped job is `skipped` — keep it so). Seat B; LIGHT, CI only:
sonnet; **no rung**. Proposed by the orchestrator 07:04 (GitHub's 20 concurrent jobs saturated; each sharded run takes 11 slots; ~⅓ of the load re-tests carried trees).
**Depends on** `U-shard-rebalance` and `U-red-says-why` landing first (both touch `.github/workflows/build.yml`).
**Facts (SOURCE at `main` `eace5030`; re-check by `grep -n` at dispatch).**
- `.github/workflows/build.yml`: `on: push (main, paths-ignore = the 13 bookkeeping lines) | pull_request | workflow_dispatch` (:3-25); `shard-plan` (:33-48) feeds the test matrix; `package`, `package-linux`, `linux-host`.
- Branch runs are `workflow_dispatch` (`fleet/bin/ci-dispatch.sh:12`); land.sh's carry (`fleet/bin/land.sh:74-115`) reads push + dispatch runs, requires every job `success` (:105) and tree equality outside the list
  (`build_tree_equal`, :34-36); the drift guard (:41-71) refuses carry if the list and `paths-ignore` differ.
- Tonight's carried landings, for the proof: e.g. main `b55fc552` (U-runner-features) carried run 38102722770; seat B's train B1 (run 38112158847) and earlier records in `fleet/records/` name theirs.
**Re-checked at dispatch (main `3430404b`, seat B; both dependencies LANDED — U-shard-rebalance, U-red-says-why in train B3):** `build.yml` `on:` :3-26 (paths-ignore :11), jobs `shard-plan` :33,
`test` :56, `package` :190, `package-linux` :243, `linux-host` :286; `land.sh`'s carry: `ci_step` :93, candidates :102, every job `success` :107, `build_tree_equal` :43, drift guard :45-71; `ci-dispatch.sh:12`. The
landed train records name carried runs too: U-red-says-why/U-owner-words-delegated carried 38132029331 (main `80945a60`'s merge tip `93aaa4dd`). Grep before editing.
Must NOT: skip anything on `pull_request` or `workflow_dispatch`; trust a run of another workflow, a `pull_request` run, a run not completed, or one with any job not `success`; read the excluded paths from anywhere but
the one list's mirror in `paths-ignore` (or a checked-in copy the drift guard also compares); grant more than `actions: read` + `contents: read`; change any test, shard, timeout or the package jobs' content.
Items, one commit each, one-sentence messages:
1. **The dedupe gate.** A first job `tested-before` (push to main only): fetch enough history, list recent successful `workflow_dispatch` runs of build.yml (`gh api`, bounded), and for each head sha compare trees outside the
   list (`git diff --quiet <sha> HEAD -- . ':(exclude,glob)…'`), checking every job of the candidate run succeeded; output `skip=true` + `tested_by=<run id>` on the first match, else `skip=false`. Every
   other job `needs:` it and runs `if: needs.tested-before.outputs.skip != 'true'`. The job summary names the run, its sha and the filter. A failed lookup ⇒ `skip=false`.
2. **The script, testable locally.** The comparison lives in `.github/ci/tested-before.sh` (the workflow calls it); run against tonight's landings it finds each carried run; a code push with no equal tree finds none.
   If the shard count changes to 4 in `U-shard-rebalance`, nothing here depends on it.
Proof (quoted in the report): the script locally over ≥ 3 of tonight's main landings (each finds its carried run id) and one code commit (finds none); one branch run green (dispatch: the gate job is skipped
there by its `if`); the first real main push after landing is observed in the record of the NEXT landing (not claimable here).
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; no local test run (CI-only); one branch CI run all green; names vs `main` 0 removed (both set sizes);
`shellcheck` clean on the script if available. `## Report` ≤ 20 lines appended here. No push to `main`, no merge.

## Report
Tip: code `75a54362` (CI run 38133792860, workflow_dispatch, ALL 12 jobs success incl. `tested-before`); the report commit follows it, no code after. Base main 3430404b (main has since moved to af1a7a6f; it did not touch `.github`).
- Gate 1: `dotnet build TradeAgent.sln -c Release --no-incremental` -> "0 Warning(s) 0 Error(s)". shellcheck: not installed here, NOT RUN (`bash -n` ok).
- Gate 3: run 38133792860 green; `tested-before` conclusion success, its steps "Run actions/checkout@v7: skipped" and the gate step "skipped" (by `if: github.event_name == 'push'`).
- Gate 4: `names.sh main u-ci-dedupe` prints "removed: 11" ONLY because main advanced (U-lenses landed, +11 tests); against the branch base `names.sh 3430404b u-ci-dedupe`: "sets: base 2595 tip 2595, removed: 0, added: 0", [Fact]/[Theory] 2536/2536.
- Gate 5: tests box NOT RUN (CI-only unit).
- Item 2 (`.github/ci/tested-before.sh`, 436ac480) DONE. Item 1 (gate job, 75a54362) DONE with ONE DEVIATION: the brief says the gate job is skipped on dispatch by its `if`; but `land.sh:107` (ci_step) requires EVERY job of a carry candidate `success`, so a skipped `tested-before` would end every carry from every dispatch run. The job therefore runs on every event and only its STEPS are `if: github.event_name == 'push'`; on dispatch/PR it is `success`, outputs `skip=false`. A deduped push run has every other job `skipped` => land.sh can never carry from it.
- Design: lookup = newest 50 (TB_RUNS) successful, first-attempt, `workflow_dispatch` runs of build.yml (path checked); per candidate: fetch sha depth 1, `git diff --quiet` vs the push outside `on.push.paths-ignore` read from the push's own build.yml (the mirror land.sh's drift guard compares), then require jobs total_count == length and all `success`. Any failure/no filter/timeout (240 s, job 10 min) => skip=false. Job permissions: `actions: read`, `contents: read` only. Dependents: `needs: tested-before`, `if: !cancelled() && skip != 'true'` (+ `shard-plan`/`package-linux` result success where they feed a job); no test, shard, timeout or package content changed.
- Local proof (`TB_RUNS=100`): 80945a60 -> tested_by=38132029331 (93aaa4dd); 3430404b -> 38132029331; 1970f86a -> 38129113502 (d871f212); 4fa0f0c2 -> 38132029331; b55fc552 -> 38102722770 (e6e2672d); 913374b2 -> 38129113502. Code commit with no equal tree (49cf67e4, and a scratch commit editing Versioning.cs): `skip=false`.
- Independent review: `fleet/tmp/B-scratch/u-ci-dedupe/U-ci-dedupe-review.md` (not acted on): one P2 — `git diff -- .` is relative to the cwd, so a LOCAL run from a subdirectory could report skip=true (CI runs from the repo root, so not reachable there).
- NOT verified: the first real main push (observed in the NEXT landing's record, not claimable here); a real deduped push run's summary and its skipped jobs; the checkout/fetch of a candidate sha inside a runner (only locally).
