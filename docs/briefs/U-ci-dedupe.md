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
