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

## Report
**Code tip `57afb55`; this report is the commit above it.** Rebase onto `main` was a no-op — `main` had not moved from `52072f6`, no conflicts.
Gate, Release `--no-incremental`: **0 warnings, 0 errors**. Unit **1189 / 0 / 0**, Fault **399 / 0 / 0**, Integration **695 / 0 / 1** (passed/failed/skipped;
the one skip is pre-existing). Test names vs `main`: 1939 → 1942, **0 removed**, 3 added. `AgentEnvironmentTests` + `RuntimeDetectionTests` 3×: 6/6 each run.
1. **DONE** `8ac0a7b` — `DOTNET_ROOT`, `DOTNET_ROOT_X64`, `DOTNET_ROOT_ARM64` and `DOTNET_ROOT(x86)` join `PassThrough` on every platform. `Apply` is untouched,
   so each crosses only when the app itself holds it and the whitelist is still a list of names; `CONTRACTS.md`'s containment section names the four, says no
   secret is among them, and quotes the measurement.
2. **DONE** `57afb55` — a version comes only from an exit-0 run; a program that will not start is `Installed = false` carrying the first line it printed as
   `RuntimeDetection.Reason`, which the onboarding install step, the Doctor row and the `AgentRuntime` health row show. Such a program is not downloaded again:
   re-fetching the same file fails the same way, and what the owner needs is the sentence the program printed.
RED on the base, quoted: (a) "DOTNET_ROOT was set in TradeAgent's own environment and did not reach the child; the child is relaunched through TradeAgent's own
framework-dependent `trade`, which cannot start without the runtime location the app itself was given"; (b) "a program that exits 131 without running was
reported as an installed runtime; setup then advances past the install step and the owner is never told what the program said". (c)
`An_exit_zero_run_is_the_version` was GREEN on the base — recorded as a guard checked, not a RED. Mutant, the exit code ignored in the version probe → (b) red
on that same line; put back, 6/6 green.
Also measured through the real launcher, not only the stub: `env -i PATH=… HOME=… trade --spawn-contained /bin/echo` → "You must install .NET to run this
application.", exit 131; the same with `DOTNET_ROOT=$HOME/.dotnet` → "the vendor CLI ran", exit 0.
Deviations: `RuntimeDetection.Reason` was added inert (always null) before the red run so (b) could compile against base behaviour; `CONTRACTS.md` also gained
an `IAgentRuntime` paragraph for item 2, which the brief did not ask for. **NOT DONE:** the observed run itself — `tools/mac-bundle.sh` would have killed the
manager's parked app and dev home, so "step 5 advances by itself" is NOT VERIFIED here. No box, no vendor call, no order, no push, no merge.
