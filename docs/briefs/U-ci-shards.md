# U-ci-shards — a CI run takes ≤ 30 min instead of ~66, with every test still run exactly once per platform
**Protects:** CI as every unit's gate (land.sh v2 lands on a green run of the rebased tip, with no local suite): the full suite on three platforms, each test once,
the `Timing` category's one recorded retry, a red stays a red. `.github/workflows/build.yml` + a small shard script only; no `src/`, no test edits; seat P; no rung.
**Facts (run 38046993760, `u-decision-card` `7d8b8a6`, all green; digest of its trx, seat P 2026-10-11):**
- Jobs: test windows 60 min (restore 0.9, build 0.7, non-Timing 48.9, Timing 10.2) · macos 26 (its Timing step 18: one first-attempt red, retried) · ubuntu 13 ·
  package-linux 2 → linux-host 20 · package 4, but it `needs: test` (`build.yml:104-105`), so it starts at minute ~61.
- Windows non-Timing, sum of test durations / wall: Unit 1601 tests 6657 s / 1819 s; Fault 479 tests 2907 / 2908; Integration 673 (+1 skipped) 2928 / 2930.
  Fault and Integration are SERIAL by design (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`, `tests/TradeAgent.FaultTests/FaultTests.cs:9`,
  `tests/TradeAgent.IntegrationTests/Harness.cs:11`); all three assemblies share one 4-vCPU runner today. Ubuntu runs the same tests in 197 / 252 / 229 s (sum).
- Windows' slowest classes (sum s): ForwardRunnerTests 584 I · PaperGatewayTests 573 I (one test 562) · SweepRequestIdTests 331 I · FeatureSeriesTests 301 U ·
  MissionLoopTests 284 U · CampaignLedgerTests 232 U · CoidWitnessTests 207 I · PipeContractTests 191 I · BacktestRequestTests 180 U · RiskGateTests 152 F.
  Full lists (30 slowest tests, 15 slowest classes): `fleet` seat P's scratch `trx/DIGEST.md`, path in your Notes. Build: 0 warnings, 0 errors on all three.
- Test homes live under `Path.GetTempPath()` (`tests/Shared/TestEnv.cs:30`): `C:` on a windows runner, whose workspace and `runner.temp` are on `D:`.
**Must keep:** the Timing step's text and semantics exactly (`build.yml:33-94`: non-Timing first, then Timing, its one retry, the first failure in the summary, the annotation
and its own trx), in exactly ONE job per platform; `fail-fast: false`; `upload-artifact` names unique per job; every test job's name starts with `test` (land.sh v2 reads
those jobs' build logs for `0 Warning(s)`); `workflow_dispatch` and `pull_request` unchanged; ubuntu and macos unsharded unless the 30 min needs them (macOS runners are few).
**Must NOT:** touch `src/` or `tests/`; change `Timing` membership, a filter's category logic, a retry or a timeout; skip, exclude or deduplicate a test away; cache build
output between jobs; disable Defender or change any other runner setting beyond item 2.
Items, one commit each, one-sentence messages:
1. **Windows shards.** The windows test job becomes ≤ 6 jobs (`matrix.include`), each `dotnet test TradeAgent.sln` (the whole solution, so a new project is always
   discovered) with its own filter ANDed with the step's category filter. The partition lives in ONE file, `.github/ci/test-shards.txt` (fully qualified class names per
   shard, balanced from the sums above), read by ONE script (`.github/ci/shard-filter.sh`, shellcheck-clean) that prints a shard's filter, and for the LAST shard prints the
   COMPLEMENT — no listed class — so a new class falls into it by construction. A class listed twice fails the script; a listed class that matches no test is printed as
   a `::warning` annotation. Filters match a class exactly (`FullyQualifiedName~Ns.Class.`, nested `+` types included), never a prefix of another class. The Timing step runs
   in one windows shard, after that shard's non-Timing step. Target: every windows job ≤ 25 min.
2. **Windows test homes on the work disk:** the windows test jobs set `TMP` and `TEMP` to `${{ runner.temp }}`. Kept ONLY if the per-assembly test counts are unchanged and
   the summed durations fall; your report quotes both runs' sums (with and without). Otherwise drop the commit and say so.
3. **`package` no longer `needs: test`**; its comment says why (as `package-linux`'s does, `build.yml:155-156`).
4. **`on.push.paths-ignore`**, exactly these 13 (`fleet/bin/bookkeeping-paths.txt`, which land.sh v2 checks against main's workflow and refuses carries on any drift):
   `BUILD-STATUS.md` `docs/RESUME-HERE.md` `docs/ORGANISATION.md` `docs/FLEET.md` `docs/HOW-WE-BUILD.md` `docs/EDGE-FACTORY.md` `docs/VISION.md` `docs/PRINCIPLES.md`
   `CLAUDE.md` `manager-prompt.md` `docs/briefs/**/*.md` `docs/queue/**/*.md` `docs/research/**/*.md` — markdown only there, because `ResearchLibraryTests` enumerates
   `*.strategy` across the whole repo. Prove each unread with one grep over `src/ tests/ packaging/ tools/ .github/` (hits only in comments or strings, each judged), and
   cite GitHub's filter-pattern docs that `docs/x/**/*.md` matches `docs/x/a.md`. An entry a test or tool reads is DROPPED and named in your report (seat P fixes the list).
**Proof:** ONE branch CI run of the final tip, every job green, wall time `createdAt`→`updatedAt` quoted (target ≤ 30 min). Exactly once: a script (yours, outside the
repo) that compares, per platform × assembly, the multiset of trx `testName`s (non-Timing + the Timing first attempt) of that run with a run of your rebased BASE sha
(main's push run of it; name its id) → 0 missing, 0 duplicated, counts quoted per platform × assembly. The shard script run locally: fed every class of the solution, each lands in exactly one shard (the complement holds exactly the unlisted ones),
a duplicate entry exits non-zero, an unknown class warns — quoted. Gate per `fleet/BUILDER-PROMPT-v2.md` (no test class touched: its step 2 is N/A).
