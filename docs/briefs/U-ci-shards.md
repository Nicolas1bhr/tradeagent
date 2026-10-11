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

## Report
**Tip** = this commit (docs/briefs only, a bookkeeping path) on code tip `a082e026`. Money path not touched (CI files only): no RED or mutant owed. Gate 2 N/A: no test class touched.
**Gate:** build `-c Release --no-incremental` → `0 Warning(s)` `0 Error(s)` · `names.sh main u-ci-shards` → `sets: base 2457 tip 2457`, `removed: 0` · tests box NOT RUN: `ready : NO - the machine does not answer`.
**CI run 38097157034** at `a082e026`: all 12 jobs success — shard-plan 0, test ubuntu 12, macos 16, windows shards 1-5: 10/8/8/4/11, package 4, package-linux 2, linux-host 20 min; wall createdAt 00:04:07Z → updatedAt 00:29:34Z = **25.5 min**; every test* job log has `0 Warning(s)`; Timing green on its first attempt on all three; every shard's check step: 0 classes out of place, 0 stale lines.
**1 done** `a844cc9b`: 5 shards, `.github/ci/test-shards.txt` (80 classes on shards 1-4, shard 5 the complement) + `shard-filter.sh` (filters 0.3/3.3/3.4/3.3/10.4 K chars); Timing step changed by its `if:` line only (diffed), shard 1. DEVIATION: the matrix comes from a `shard-plan` job (not test*) that reads the file, so the shard count lives in the file alone — a static `matrix.include` could drop the complement silently. shellcheck: one line in package-linux's lint, green there; NOT VERIFIED locally (not installed).
**1b done** `a082e026` (seat P's amendment): the check step is a red for a class that ran in a shard not listing it (nor its outer class) or a listed class in the complement, naming the matching lines; stale lines stay warnings.
**2 KEPT** `b162c275`: A/B runs 38095346946 (C:) / 38095369638 (D:), windows counts identical (non-Timing F 479 I 675 U 1595, Timing 6/91/18); summed non-Timing s F 2239→1560, I 2415→1510, U 4968→4618 (Timing 695→715; all 10318→8403); homes seen at `D:\a\_temp\tradeagent-tests` in B's trx, `C:\Users\runneradmin\…` in A's.
**3 done** `e831a886`. **4 done** `5f76ecaf`, none dropped: land.sh's own `list_matches_workflow` run on this file → SAME SET (13); greps over src/ tests/ packaging/ tools/ .github/: every hit a comment, prose, a printed string (CandleSourceCatalog.cs:134, tools/probe/Program.cs:299,440,949) or the agent home's `research` folder, none a read (tests read only CONTRACTS, USER-GUIDE, STRATEGY-LANGUAGE, RESEARCH-REQUIRED, MONITORING-PHASE, DEPLOYMENT); GitHub's "Filter pattern cheat sheet": `docs/**/*.md` = "A file with a `.md` suffix anywhere in the `docs` directory", e.g. `docs/README.md`.
**Exactly once** (my `compare.py`, trx testName multisets, non-Timing + Timing first attempt) vs main's push run **38093191853** of `dab2768e` — DEVIATION: c4f611fe's push run 38094202491 was cancelled, and dab2768e..c4f611fe differs only in docs/ORGANISATION.md, docs/briefs, docs/queue (bookkeeping). ubuntu, macos, windows each: Fault 485, Integration 766, Unit 1613 — 0 missing, 0 duplicated (runs A and B also 0/0).
**Script, local:** real VSTest `--list-tests` per shard filter: union == the `Category!=Timing` list, 2749 names, 0 in two shards, complement == the 239 unlisted classes · `check` on B's trx classNames: shards 1-5 exit 0 · Codex's `New.…CandleSourceTests.…MissionLoopTests` to shard 2 → `::error …Matched by: line 35 (shard 2…), line 71 (shard 3…)`, exit 1 · listed MissionLoopTests to shard 5 → `::error … runs twice`, exit 1 · a duplicate line in another case → `listed twice (first on line 31)`, exit 1, 0 bytes out · an unknown class → `::warning … ran no test`, exit 0 · `C+Inner` beside `C` → refused.
**A red not mine, for seat P:** run A's linux-host (38095346946): `FAIL after kill -9 the main process is 0 (was 3248) and NRestarts 0` — my diff touches neither that job, src/ nor packaging/ (package-linux, its input, gained only a lint line); green in 38095369638, 38097157034, 38093191853 and 38094202491.
**Not done / not verified:** ≤ 30 min holds only with runners free (B took 31 min, 4.5 queued before shard-plan); shards not rebalanced after the D: move (shard 5 11 min, shard 4 4); tools/win-test-run.ps1 still runs the suite whole, unchanged; the report commit is not pushed (no code after the run).
