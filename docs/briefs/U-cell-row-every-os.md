# U-cell-row-every-os — Cell row (f)'s "every OS" claim is true on every OS, and CI runs it on all three, so the tests box is no longer the only place it can fail

**Arrow closed:** the tests box's catch-up run on `main` @ `7e372c9b` (`BUILD-STATUS.md`, 2026-10-21; run `20261021-151027-7e372c9b`): RED on one row,
`CellLinuxTests.A_cell_launch_without_a_delegated_cgroup_is_refused_naming_delegate_yes` (`CellLinuxTests.Cgroup.cs:335`), every other test green on real
Windows. Judged a TEST defect (read): production never reaches the policy check on Windows, because `CellPlatform.Read()` (`Cell.cs:479`) refuses every
cell there first (`Containment.cs:282`, `Core`). **Light; tests and CI only — no product code.** Seat A; **sonnet/high**. **No rung.** Depends on nothing.

**Today (main `25bae9bc`):**
- Row (f) says "Every OS: the platform is a stand-in" (`CellLinuxTests.Cgroup.cs:330`), accepts a stand-in Linux `CellPlatform` (`:336-337`) and builds its
  policy from `CellPolicy.SystemRoots` (`:340`; `Cell.cs:27` — `/usr`, `/lib`, …). On Windows `/usr` is not `Path.IsPathFullyQualified` (`Cell.cs:157-161`),
  and `ProcessContainment.Start` refuses a refused policy FIRST (`AgentRuntime/Containment.cs:744-746`), so the refusal names the path, not `Delegate=yes`.
  Still `CELL_UNAVAILABLE`, nothing started — the property holds; the row's claim does not.
- macOS, measured 2026-10-21 by the orchestrator at `25bae9bc` (`dotnet test tests/TradeAgent.IntegrationTests -c Release --filter
  "FullyQualifiedName~CellLinuxTests"`): `Total tests: 41 · Passed: 1 · Skipped: 40` — row (f) is the ONLY Cell row that runs off Linux, and it passes there.
- CI excludes `Category=Cell` from every OS's normal steps (`.github/workflows/*.yml:131`) and runs the Cell rows on ubuntu alone under `systemd-run` with
  `Delegate=yes` (`:248`; since `1471d8a9`). So row (f) runs on Windows only on the tests box (`tools/win-test-run.ps1:132` runs `Category!=Timing`).

**Items, one commit each:**
1. **The row holds on every OS.** Its policy's roots are fully qualified on the OS that runs it (the Linux roots where they are absolute; a scratch root of
   the running OS elsewhere), so the refusal it asserts is the cgroup's, naming `Delegate=yes`, on Windows, macOS and Linux alike; the Linux-only second half
   (`:356-362`) unchanged. RED first: the row on Windows at `25bae9bc` is the tests box's quoted failure — reproduce it without Windows by asserting the policy is
   valid on the running OS (`cell.Problem()` null) before the launch, red on a policy that is not; then green. Settling check: the row's assertion text
   unchanged (`Delegate=yes`).
2. **CI runs every every-OS Cell row on every OS.** Today that is row (f) alone; give the every-OS rows their own trait (or an equivalent filter) so the normal
   test steps of all three platforms run them and the ubuntu Cell step still runs the Linux-only rows. Settling check: the CI run's trx shows row (f) passed on
   windows-latest, macos and ubuntu; the ubuntu Cell step's count is unchanged minus the moved rows. The tests box's runner keeps `Category!=Timing` (it stays a
   second Windows run of the same rows; nothing is excluded there).

**Gate:** Release `--no-incremental` 0 warnings; touched classes 3× through `fleet/bin/suite.sh`; names vs main 0 removed; one branch CI (all three platforms).
**Tests box:** when `TA_WIN_BOX=tests tools/win-test.sh ready` answers, one run on the tip — the first Windows proof of the row; otherwise a NOT RUN line.
**Read first:** `CellLinuxTests.Cgroup.cs:325-365`, `CellLinuxTests.cs:30-60` (the class trait), `Cell.cs:20-60, 150-170, 475-510`,
`AgentRuntime/Containment.cs:735-760`, the workflow's test and Cell steps (`:120-260`), `tools/win-test-run.ps1:120-140`.
**NOT in scope:** any product change; any other Cell row; the runner's other filters.
