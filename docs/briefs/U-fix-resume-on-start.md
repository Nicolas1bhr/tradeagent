# U-fix-resume-on-start — the restart-resumes-the-AI test is red on hosted Windows in five of six runs on unchanged code: find why, fix it, prove nothing weakened
**Protects:** `U-resume-agent`'s arrow — a restart with the AI working starts its runtime and the due wake is taken without a press (`BUILD-STATUS.md`
2026-10-01; the test's own summary). **A FRESH FIXER, dispatched first — before W1's first gate** (`docs/ORGANISATION.md` § 15): while `main`'s Windows job is
red on this test, every landing's CI reads red and a new Windows failure hides behind it.
**Evidence (RUN `gh run view`, 2026-10-02):** windows-latest RED at `c56540e` (36917510177), `dbc7bf2` (36917533373), `1275aff` (36956107010), `a98f6f1`
(37004472348), `c80f422` (37004803635); GREEN at `0f47db7` (36917611936); `git diff c56540e..c80f422 -- src tests` is empty, so all six ran the same code;
ubuntu-latest and macos-latest green on every one. Each red: `ResumeOnStartTests.A_restart_with_the_ai_working_starts_its_runtime_and_the_next_due_wake_is_taken
_without_a_press` failed [24–25 s] at `tests/TradeAgent.UnitTests/ResumeOnStartTests.cs:62` with "the restart resumed the loop and left the AI it needs
stopped"; Unit 1 failed / 1210 passed; Integration 609 passed / 1 skipped; Fault 394 passed.
**Facts to start from (SOURCE at `c80f422`; facts, not a diagnosis):** the assert at `:62` is the first line after `await host.ResumeOnStartAsync()` (`:60`), so
the 24–25 s were spent inside that call (INFERENCE from the test's text); `AppHost.ResumeOnStartAsync` (`src/TradeAgent.App/AppHost.cs:1247-1260`) calls
`StartTheAiAsync` and SWALLOWS its exception ("said on the card and in the activity log"), so the test sees only `Agent.Running == false`; on Windows the probe
runtime is `powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File probe.ps1` (`ResumeOnStartTests.cs:262-290`) and on Unix a
`/bin/sh` script; the class runs in `[Collection(VendorOverrideFiles.Name)]` (`:33`); `docs/RESUME-HERE.md` item 6 records that `U-resume-agent` kept a
deviation — the first paint waits for the CLI's version probe on a resuming start.
**Reproduce on CI, not on this Mac** (green here proves nothing about Windows): a branch, `gh workflow run build.yml --ref <branch>` (`.github/workflows/build.yml`
accepts `workflow_dispatch`), read the windows-latest job. Temporary diagnostics are allowed ON THE BRANCH ONLY — e.g. print what `StartTheAiAsync` said on the
card and the activity log's tail at the failure — and are removed before the report. The Windows box is NOT granted to this leg.
Read first: `CLAUDE.md`; `docs/HOW-WE-BUILD.md` (the fresh-fixer rule); `ResumeOnStartTests.cs` (whole); `AppHost.cs:580-720, 1240-1262` and `StartTheAiAsync`;
`CliAgentRuntime` start and version-probe paths; `build.yml` (its Timing-category rule); `BUILD-STATUS.md`'s `U-resume-agent` record.
Must NOT: add a retry; raise a timeout until it passes; move the test into the Timing category unless it truly asserts a deadline (`build.yml`'s own rule);
skip it on Windows; weaken any assert. The mutant the class watches — the resume calling the loop only — must still turn it red.
Items, one commit each, one-sentence messages:
1. Name the cause with evidence from a Windows run: quote what `StartTheAiAsync` said (card and activity log) and where the 24–25 s went.
2. Fix it where it is wrong: in the PRODUCT (with a test that was red on windows-latest before the fix) if a real Windows start can fail this way — a real
   owner's restart would hit it too; in the test's HARNESS only if the harness is what differs from a real start, saying in the report why the property
   "a restart with the AI working starts its runtime" is still proven.
Done: three consecutive green windows-latest runs on the branch (run ids quoted) and green ubuntu/macos; the watched mutant quoted red; the local gate.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push to `main`, no merge; touch nothing in `docs/briefs/` but this file.

## Report
**Code tip `0d5b157`**, 2 commits on `73cfaca` (rebased first, no conflict; `main` moved since by U-price-rows, no common file, `git merge-tree` clean). **Gate, this Mac, Release:** build `--no-incremental` → 19
projects, `0 Warning(s)`, `0 Error(s)`; Unit `Passed! - Failed: 0, Passed: 1214, Skipped: 0, Total: 1214, Duration: 26 s`; Fault `Passed! - Failed: 0, Passed: 399, Skipped: 0, Total: 399, Duration: 1 m 27 s`;
`ResumeOnStartTests` + `RuntimeDetectionTests` 3× → 10/10 each. Names vs `main`: sets 1969 → 1971, removed 0, added 2.
**CI on `0d5b157`, workflow unmodified:** 37021009822, 37021014270, 37021021599 — each: ubuntu, windows, macos and package success; windows Unit 1213/1213, Integration 609 + 1 skipped, Fault 394, timing green first
time; all 7 `ResumeOnStartTests` green on windows each time, by name `A_restart_in_the_armed_live_configuration_…` (seat P's second red) 4.5 / 3.1 / 5.7 s, `A_restart_with_the_ai_working_…` 21.2 / 15.0 / 41.6 s.
- **Item 1 (`7be7ec2`), done — the cause, from windows-latest:** diagnostic run 37017805967, job 110872924285: `restarted=4853ms resume=20077ms`; card "Signing in took too long and was cancelled. Press Sign in
  again."; activity `warn` "The AI was not started: Signing in took too long and was cancelled. Press Sign in again. (AI_AUTH_TIMEOUT)"; engineering "TradeAgentException: powershell.exe did not finish within 20s"
  at `CliAgentRuntime.Run` :810 ← `ProbeVersionAsync` :254 ← `DetectAsync` :232 ← `AgentSupervisor.PrepareAsync` :67 ← `StartTheAiAsync` :1111. The 24–25 s: 4.9 s composing the restart, 20 s the version probe's
  deadline on PowerShell's first launch beside the full suite (re-measured at once: 6.5 s, then 0.2 s; the class alone passed, `resume=2218ms`). Seat P's armed-live red (run 37019128845): the same — its one
  activity line is that sentence, thrown before the containment check. Two tests name it, RED before the fix on this Mac: `ResumeOnStartTests.cs:106 a version answer slower than its deadline kept the AI stopped on
  a restart`; `RuntimeDetectionTests.cs:83 TradeAgent.Core.TradeAgentException : slow-to-answer.sh did not finish within 1s` (seam: internal `VersionDeadline`, 1 s there; product 20 s).
- **Item 2 (`0d5b157`), done — in the PRODUCT:** `ProbeVersionAsync` answers its deadline as a failed probe (not installed, found at its path, reason "<exe> did not answer within N seconds when asked for its
  version"), so the start goes on as after any failed probe (armed live: `CONTAINMENT_REQUIRED`, same words); a caller's own cancellation still throws. Product, not harness: the deadline and its sign-in words are
  the product's for every runtime, a restart is a first launch of the AI's program, and the Doctor and setup's install step call the same `DetectAsync` (RED line above). Watched mutant (the resume calls the loop
  only): `:62 the restart resumed the loop and left the AI it needs stopped` (also `:106`, `:198 Assert.Single() Failure`, `:264 … Actual: 1`); restored → 7/7.
- **Temporary, reverted:** `7743fe1` (test prints; `build.yml` narrowed to two windows legs, the full suite and the class alone, timing and package off), run 37017805967 only, dropped by reset before the proving
  runs (`git diff 73cfaca..0d5b157 -- .github` empty). Scan gated every commit and push; `cts.Token` excluded by name on `7743fe1` and on this line. No `Co-Authored-By`.
- **NOT done / NOT verified:** whether a proving run reached the 20 s deadline (no prints; 41.6 s in run 3): NOT VERIFIED; a real CLI's first launch on an owner's machine: NOT measured; the turn's PowerShell cold
  start still runs on the test's own 60 s patience, unchanged; Integration only on CI; no box; the running app not observed; the first paint still waits for the probe (RESUME-HERE item 6).
