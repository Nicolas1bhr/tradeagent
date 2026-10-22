# U-launcher-race — ubuntu's Unit step stops failing on a shared launcher or fake runtime that parallel test classes write and execute at once

**Arrow closed:** a red that blocks landings, now SEEN TWICE on ubuntu's `test (ubuntu-latest)` Unit step (the orchestrator, 2026-10-22: a second sighting
makes it a unit): run 39685897934 attempt 1 (u-cell-row-every-os; 7 `PromptStdinTests` + 3 `LinuxHostRuntimeTests`, "the turn ended 154" = 128+26, ETXTBSY)
and run 39692232483 (u-thinking-seats-c fix 2; the same `PromptStdinTests` rows "Actual: 154", AND `PuppetLivesTests.A_puppet_life_puts_back_every_process_wide_thing_it_took`
at `PuppetLivesTests.cs:290`: the launcher's bytes expected `#!/bi…` (a script), actual `0x7F 'E' 'L' 'F'…` — another class wrote a binary over the
launcher in `Paths.Bin` while the life ran). Neither diff touches those classes; both green in the neighbouring runs. **Light — tests/rig; seat A; sonnet/high; no rung.**
**Reading (verify first; it is not established):** a process-wide path — the launcher/`ToolDeployer.DeployedHashFile` in `Paths.Bin`, or a fake runtime
— is written by one test class while another executes or snapshots it (ETXTBSY on exec of a file still open for writing; a byte compare racing a redeploy).
**Items, one commit each:** (1) find the writers and executors of the shared path(s) by reading (`grep -n` `Paths.Bin`, `ToolDeployer`, `launcher`, the fake-runtime
writers in PromptStdinTests/LinuxHostRuntimeTests/PuppetLives) and REPRODUCE the red locally on this Mac's Unit project by running the involved classes
together in parallel (quote it; macOS gives ETXTBSY too) — if it will not reproduce, say so and fix on the reading. (2) Fix in the RIG: classes sharing
the process-wide path join one non-parallel xUnit collection, and/or a fake runtime is written to a temp name, closed, then renamed into place (atomic;
never exec a file still open for writing). No retry-on-ETXTBSY, no timeout raised, no test weakened. If the cause is PRODUCT code (e.g. ToolDeployer
writes the launcher in place while something runs it), STOP and report — that is a product unit.
**Gate:** Release `--no-incremental` 0 warnings; the involved classes together 5× via `fleet/bin/suite.sh` (quote each); names vs main 0 removed; one branch CI.
**NOT in scope:** VendorLimitQuoteTests/TurnMeterTests on macOS (U-turn-tail-drain's).

## Report
Tip: e930f3d2 (code; this report commit follows it, docs only). Base a65feeb6, not rebased (seat A rebases at landing; main is now b3d0a777).
- Item 1 REPRODUCED (no code commit): PromptStdinTests+LinuxHostRuntimeTests+PuppetLivesTests together, xUnit parallelism as CI, Release, via suite.sh: 4 reds in 16 runs, all LinuxHostRuntimeTests.The_device_auth_prompt_yields_its_link_and_code ("Expected https://auth.openai.com/codex/device, Actual https://aka.ms/dotnet-cli-telemetry" x3; "Actual: null" x1). Cause: LinuxHostRuntimeTests had NO collection, so it ran beside the VendorOverrideFiles classes; PuppetLife.DeployTrade deletes then copies the real apphost over the launcher at Paths.Bin, and the sign-in stub was started through it. The macOS ETXTBSY itself did not appear in 16 runs: it is the fork race (a thread forks while another holds a script's write handle, then the script is exec'd), which only serialization removes; a rename does not.
- Cause of the 2nd sighting's PuppetLivesTests.cs:290: ProcessWide.PutBack rewrote the launcher in place with its IOException swallowed; opening a running program for write is ETXTBSY, so the real command stayed where the script was.
- Item 2 DONE (e930f3d2): TestEnv.WriteExecutable (temp name, close, mode, rename over) used by TestEnv.EnsureLauncher, AgentTreeTests.DeployLauncher and ProcessWide.PutBack; LinuxHostRuntimeTests joins [Collection(VendorOverrideFiles.Name)]. No retry, no timeout, no test weakened. Product code untouched (ToolDeployer.EnsureTradeCli copies in place, but only at app start; not a product cause here).
- Not changed, said so: IntegrationTests ContainmentTests.DeployLauncher and ProtectedConfigurationTests.Installed still write in place (other project / only when absent); any non-collection class that forks while a collection class writes a script can still meet the fork race, none seen. Third sighting (seat A): run 39699108133, ubuntu PromptStdinTests "Actual: FAILED" - not re-examined here.
- Gate: Release --no-incremental 0 warnings, 0 errors. Same 3 classes 16x after the fix: 16/16 Passed 20/20. Those 3 + AgentTreeTests + ToolDeployerTests + ProtectedConfigurationTests 5x via suite.sh: 5/5 "Passed! 54/54".
- names.sh main u-launcher-race: vs the base, removed 0, 2841/2841. Against today's main it lists removed 20 (sets 3001 vs 2981): those are tests main gained after my base; this branch touches 4 test files and deletes none.
- CI run 39696526489 on e930f3d2: all jobs success. ubuntu Unit step "Passed! 2011, Failed 0, Skipped 6, Total 2017" (the three classes are inside it); macos the same 2011/0/6; the second ubuntu pass 20/20 (16 s). windows shards green.
- Codex review: first launch died with the turn; relaunched detached, review running, detached: fleet/tmp/A-org/u-launcher-race/U-launcher-race-review.md (prompt beside it).
- Tests box: left to seat A's landing train. Not done: no full local suite, no observed run.
