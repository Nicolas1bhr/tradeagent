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
