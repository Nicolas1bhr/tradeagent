# U-puppet-isolation — a puppet life leaves nothing process-wide behind: AiAttemptLedgerTests' restart-and-midnight test passes beside PuppetLivesTests
**Arrow closed:** "never a flake" (docs/FLEET.md § Speed mode): a red the diff cannot reach is a defect once it repeats. Found by U-thinking-seats-a's adoption pass
(2026-10-17): `AiAttemptLedgerTests.A_turn_killed_before_its_usage_stays_charged_across_a_restart_and_midnight` (tests/TradeAgent.UnitTests/AiAttemptLedgerTests.cs:60)
fails at :84 (`session.Busy`) when run in one `dotnet test` with `PuppetLivesTests` (tests/TradeAgent.UnitTests/PuppetLivesTests.cs, `[Collection(VendorOverrideFiles.Name)]`);
reproduced twice on a clean main worktree; green alone. Seat A (U-puppet-runtime is its unit); **sonnet/high** (tests/rig; opus/high instead if the cause is product
code — then it is a protection unit: red-first + one watched mutant). **No rung.**
**Observable result:** both classes green in one run, in either order, 3× — and the shared state named: what a puppet life leaves behind (a static register, a
process-wide flag such as `PositionActivation.TestHost` or a gate register, a pipe or meter singleton, an environment variable, a file in a shared folder, a
background task outliving its host's dispose) and why `session.Busy` reads true in the other class.
Items: 1. **Reproduce and name the cause** (a filter running both classes; quote the failure and the state that crosses). 2. **Fix at the cause:** the puppet fixture
(or the product singleton it touches) restores or scopes that state per host — never a sleep, retry, order attribute or a collection that merely serialises them;
if the cause is PRODUCT state that two hosts in one process share, fix the product and say so (two app hosts in one process is also what the proving ground needs).
3. **A guard:** a test that runs a puppet life, disposes it, then asserts the shared state is back to its default (red before the fix, quoted).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; both classes + the guard 3× in one filter; branch CI on three platforms; the
Codex review with the CI wait; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.

## Report
Tip: see the commit this report is in (code tip 9c5a129cb4 = CI run 38994595285). No rung; test-side fix, product unchanged.
CAUSE (item 1): the shared state is the launcher at Paths.Bin (`trade`) which TestEnv.EnsureLauncher puts in the assembly's home. `PuppetLife.DisposeAsync` deleted it (and the hash record); the next class to launch a held process on macOS/Linux was refused by `ProcessContainment.CannotHold` ("TradeAgent's own trade command ... is not at .../bin/trade"), so `session.Busy` never went true (turn ended at once with that System line). Reproduced on 63b295f1 with a filter of the two classes, 12 tests, 1 failed; DIAG dump of the session history quoted the refusal. Also leaked: DOTNET_ROOT (set when empty, never restored) and the side-car files the deployer copies.
1. Reproduce and name: DONE.  2. Fix at cause: DONE — `ProcessWide.Note/PutBack` in tests/TradeAgent.UnitTests/PuppetLife.cs restores launcher bytes+mode, hash record, DOTNET_ROOT and removes added bin files; no sleep/retry/order/collection.  3. Guard: DONE — PuppetLivesTests.A_puppet_life_puts_back_every_process_wide_thing_it_took. RED before: failed on DOTNET_ROOT ("Expected null, Actual /Users/.../.dotnet"). MUTANT (restore deletes the launcher as the old code did): guard AND AiAttemptLedgerTests restart test both red; restored, green.
Gate 1: Release --no-incremental: 0 Warning(s), 0 Error(s).  Gate 2: AiAttemptLedgerTests+PuppetLivesTests+PuppetSeatTests 3x: "Passed! 17/17" x3.
Gate 3: CI 38994595285 (9c5a129): every job green EXCEPT the 3 unit-test jobs' one failure, CouncilLoopTests.A_runnable_head_gets_one_ceiling_wake_from_its_last_turn (CouncilLoopOrgWakeTests.cs:305) — NOT MINE: `Noon()` is `DateTime.Today.AddHours(12)` local, so before local noon the seeded turn is in the real clock's future and `LastWoken` returns it; it fails here at 04:21 local on my branch and on the first run (38992707891) at ~02:00 UTC; it goes green after 12:00 on the runner's clock. A time-of-day defect in main's test; owner of the fix: U-org-lanes/O3 lane. My first run's other red (my guard on Windows, no launcher there) was mine and fixed in 9c5a129.
Gate 3b: Codex review at fleet/tmp/A-org/u-puppet-isolation-build/U-puppet-isolation-review.md (3 P2: sidecar contents not restored, no rollback if Begin throws mid-startup, one failed delete skips launcher restore) — not acted on, for triage.
Gate 4: names.sh main u-puppet-isolation: sets base 2838 tip 2840, removed: 0, added 2; [Fact]/[Theory] base 2716 tip 2717.
Gate 5: tests box: NOT RUN — the machine does not answer
NOT done/verified: the guard run alone on Windows locally (CI Windows green); no proof of order-reversed run (fix is order-independent).
