# U-launcher-env — the contained child runs on the runtime the app runs on, and a launcher that fails is not "installed"
**Blocker demonstrated (RUN, 2026-09-20, the observed-run attempt on this Mac, build `34ec647`, dev home fresh):** onboarding stalls at step 5 "Sign in to your
AI account" although `codex login status` answers `Logged in using ChatGPT`, exit 0, in a shell. Cause, reproduced: on Unix `ProcessContainment.Start` relaunches
every child through the app's own `trade` (`Containment.cs:277-320`, `DefaultLauncher` = `Paths.Bin/trade`), a framework-dependent apphost; the child's
environment is the whitelist in `AgentEnvironment.cs:38-62` (PATH, HOME, TMPDIR, …) which drops `DOTNET_ROOT`, so the launcher prints `You must install .NET to
run this application … DOTNET_ROOT = <not set> … Default location: /usr/local/share/dotnet` and exits 131 before `codex` runs: `env -i PATH=… HOME=… trade <flag>
codex login status` → exit 131; the same with `DOTNET_ROOT=$HOME/.dotnet` → the launcher runs. The auth probe (`CliAgentRuntime.cs:562-575`) reads the non-zero
exit as NotAuthenticated, silently, every 2 s. Meanwhile step 4 "Installing the AI assistant" PASSED, because `GetVersionAsync` (`:~580`) returned the launcher's
error text as the version and `DetectAsync` called that installed. **Observable result:** on this Mac, with the app launched by `tools/mac-bundle.sh`, step 5 advances
by itself with the CLI already signed in; a launcher that cannot start reports NOT installed with its first line as the reason. Not the money path. No schema.
Read first: `AgentEnvironment.cs` (whole file, ~100 lines); `Containment.cs:150-330` (`ContainedProcess`, `DefaultLauncher`, `Start`, `Relaunch`); `CliAgentRuntime.cs:
70-110` (`ResolveExecutable`), `:556-600` (`GetAuthenticationStateAsync`, `GetVersionAsync`, `DetectAsync`), `:726-772` (`Run`); `OnboardingView.cs:382-425`;
`ToolDeployer.cs:41-79`; `tests/…/AgentEnvironmentTests*`, `…/ContainmentTests*`, `…/CliAgentRuntimeTests*`. Rebase onto `main` first.
Items, one commit each with a one-sentence message:
1. `AgentEnvironment`: `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_ROOT_ARM64` and `DOTNET_ROOT(x86)` join the pass-through on every platform — passed only when the
   app itself has them (the existing loop skips unset names), never invented. `CONTRACTS.md` (the containment section): the launcher is the app's own
   framework-dependent `trade`, so the child must find the runtime the app found; nothing else is added to the whitelist; no secret is among these names.
2. `GetVersionAsync` returns null when the process exits non-zero, and `DetectAsync` then answers `Installed = false` with the first non-empty line of stderr/stdout
   as `Reason` (the launcher's own message when it is the launcher that failed); the onboarding install step and the runtime health row show that reason instead
   of passing. A version is a version only from an exit-0 run.
Red-first tests: (a) `The_child_environment_carries_the_dotnet_root_the_app_runs_on` — set `DOTNET_ROOT` (and the arch variant) in the test process, `Apply`,
assert both present, and assert an unrelated variable is still dropped (RED: absent); (b) `A_launcher_that_cannot_start_is_not_an_installed_runtime` — a stub
executable that prints the apphost's message and exits 131: `DetectAsync().Installed == false`, `Reason` starts with "You must install .NET" (RED today:
Installed true, the message as the version); (c) `An_exit_zero_run_is_the_version` (a guard checked if GREEN on the base). Mutant to watch red and quote:
`GetVersionAsync` ignoring the exit code → (b) red. Nothing removed or renamed.
Gate and report as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed; classes 3×; names 0 removed; `## Report` ≤ 20 lines
here). Off-limits: the test files `U-runner-reds-3` owns (`SweepRequestIdTests`, `ValuationLossSurfacesTests`, `BridgeRoundTripTests`, `CouncilLoopTests`).
No push, no merge; touch nothing in `docs/briefs/` but this file.
