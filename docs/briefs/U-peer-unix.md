# U-peer-unix — the agent pipe identifies its caller on macOS and Linux as it does on Windows, so a CLI turn's `trade` works there
**Arrow and boundary:** the model → `trade` arrow of every CLI turn (`docs/PRINCIPLES.md` loop: research and create a candidate) on macOS and Linux, keeping
the strong claim of `docs/CONTRACTS.md` "The pipe knows which launch is calling": only TradeAgent's own `trade` under `Paths.Bin`, with the hash `ToolDeployer`
recorded, may present a launch grant. **Observable result:** on this Mac the Research Director's `trade data list` answers instead of being refused; a
program that is not the deployed `trade` is still refused, naming its path; Windows unchanged.
**The refusal, RUN 2026-10-01 in the observed run (`docs/briefs/U-observed-loop.md`, attempt 2, app at `faef463`):** every `trade` call of both roles'
codex turns → `IPC_UNAUTHENTICATED` "this connection presented a launch grant, and the operating system would not say which program is holding this
connection, so the caller could not be identified at all" (Research's report, quoted; `engineering_log` `Ipc grant_rejected` ×11 from 15:49:07Z).
**Cause (SOURCE):** `PeerImage.ClientPath` (`src/TradeAgent.Security/PeerImage.cs:83-91`) returns null off Windows; `AppHost.cs:575` and `:759` configure
`Peer = Containment.PeerRuleNow()` on every platform since `faf892f` (2026-09-12), against the intent written at `GatewayPipeServer.cs:29-38` and `:1052-1055`
("a rule applied everywhere would refuse every caller on two of the three platforms"). No CLI turn on macOS or Linux has reached `trade` with its grant since.
Read first: `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/CONTRACTS.md` lines 195-260, `PeerImage.cs`, `GatewayPipeServer.cs:700-830` and `:1040-1100`,
`Containment.cs:15-30`, `tests/TradeAgent.IntegrationTests/LaunchGrantTests.cs`, `tests/TradeAgent.UnitTests/PeerImageRuleTests.cs`.
Items, one commit each:
(a) **The kernel answers on Unix.** `ClientPath` returns the connecting process's image path on macOS (the accepted socket's `getsockopt(SOL_LOCAL,
LOCAL_PEERPID)` → `proc_pidpath`) and Linux (`getsockopt(SOL_SOCKET, SO_PEERCRED)` → `/proc/<pid>/exe`), Windows untouched; any failure stays null, which
stays a refusal (fail closed). Verify every constant and signature from the official source (xnu `sys/un.h`/`libproc.h`, Linux `unix(7)`/`socket(7)`) and
cite it at the code. Paths: the kernel reports the resolved form (macOS `/var` → `/private/var`); compare so a home under `$TMPDIR` is not refused for a
symlink — and a copy anywhere else still is. Nothing else in `PeerImage.Verdict` changes; the two folder clauses still come first.
(b) **Tests, RED on the base where the platform is Unix:** a connection from this process is identified as `Environment.ProcessPath` (resolved); the real
`trade` command, deployed as `ToolDeployer` deploys it, presenting a valid grant under the rule `AppHost` configures, is ADMITTED (on the base: the quoted
refusal); a different real program presenting the same grant is REFUSED with its path in the reason. Watch ONE mutant (`ClientPath` back to null off
Windows) turn the admission test red and quote it.
(c) **Say it where it is said:** the stale comments above, `PeerImage.cs:29` and `:80-81`, and `CONTRACTS.md` — the rule is answered on all three
platforms; where the kernel will not say, the caller is refused. No Doctor or UI change unless a surface states the old reading.
Gate as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; the three suites 0 failed on this Mac, counts pasted; touched classes 3×; names 0
removed). The CI matrix runs ubuntu, macos and windows — the new tests must pass on all three; do not mark one platform-skipped to get green.
Report ≤ 20 lines appended here. No `Co-Authored-By`; no push to `main`; touch nothing in `docs/briefs/` but this file. MONEY PATH (the gateway's pipe):
red-first and the mutant are the proof. The manager verifies the observable result in the running app after landing.

## Report
**Code tip `22b424d`** (on `main` `516376c`). Gate, this Mac, Release: build `--no-incremental` → `0 Warning(s)` `0 Error(s)`; Unit `Passed!  - Failed: 0,
Passed: 1189, Skipped: 0, Total: 1189`; Fault `Passed!  - Failed: 0, Passed: 399, Skipped: 0, Total: 399`; Integration `Passed!  - Failed: 0, Passed: 699, Skipped: 1,
Total: 700, Duration: 11 m 6 s`; LaunchGrantTests 3× `Passed: 10`, PeerImageRuleTests 3× `Passed: 8`; `[Fact]`/`[Theory]` methods vs `main` 1906 → 1910, 0 removed.
(a) `538fa86` off Windows `ClientPath` asks the accepted socket: macOS `getsockopt(SOL_LOCAL 0, LOCAL_PEERPID 2)` → `proc_pidpath`; Linux `SO_PEERCRED` (SOL_SOCKET
1, 17) → `/proc/<pid>/exe`, generic-number architectures only; any failure → null → refused; Windows unchanged; recorded paths also compared via `realpath(3)`.
(b) `9ddd2f7` LaunchGrantTests +4 on the real kernel and the real `trade`: identity; ToolDeployer's `trade` with a Research grant under `PeerRuleNow()` served; a
byte-identical copy elsewhere refused by name; a copy in the workspace refused by that clause. (c) `22b424d` stale comments, the Doctor sentence, CONTRACTS.md.
RED on base (macOS) `Failed: 4, Passed: 6`: admission `(exit 1): IPC_UNAUTHENTICATED: this connection presented a launch grant, and the operating system would not
say which program is holding this connection, so the caller could not be identified at all`; identity `Expected: "/Users/nicolasbeeckman/.dotnet/dotnet" Actual: null`;
copy `Not found: "ta-4794c0c2c63b/trade"`; workspace `Not found: "inside the agent's own workspace"`. MUTANT `if (!OperatingSystem.IsWindows()) return null;` →
`Failed …LaunchGrantTests.The_trade_command_this_app_deployed_presents_its_grant_and_is_served …(exit 1): IPC_UNAUTHENTICATED: …could not be identified at all`.
Sources: MacOSX15.5.sdk `sys/un.h:85,89`, `sys/proc_info.h:743-744`, `libproc.h:102`, `sys/socket.h:716`; xnu `uipc_usrreq.c` (far socket's `last_pid`), `libproc.c`;
man7 `unix(7)`, `socket(7)`, `proc_pid_exe(5)`, `realpath(3)`; linux `uapi/asm-generic/socket.h` (1, 17), `powerpc/…/socket.h` (21), `linux/socket.h` (ucred).
CI, draft PR #24 (closed unmerged): run 36891262990 on `22b424d` attempt 1 ubuntu red `CouncilLoopTests.A_file_dropped_between_turns…` (`Actual: InboxUnattested`,
not this unit's code), attempt 2 success ×3; run 36890851311 (`74f937f`) windows red `ValuationLossSurfacesTests` (U-runner-reds-3's). New tests Passed in all 6 trx.
Deviations: a 4th test (the folder resolution is new guard logic); the "different program" is a byte-identical copy, matched by its unique folder; rebased once.
Secret-scan exclusions by name: `IpcToken`, `research\.Token`, `CancellationTokenSource`, `cts\.Token`, `machine token`, `the token, the turn must not be over`.
NOT done: the running-app observable (the manager's); the same-user gap is stated, not closed (`U-contain-2`); the Doctor's no-recorded-hash sentence still says
"not checked" though such a caller is refused (not the old reading, so untouched).
