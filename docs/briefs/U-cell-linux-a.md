# U-cell-linux-a — the Linux cell launcher: Landlock + no_new_privs + seccomp on one thread, then execve; its file/IPC benchmark rows proven on CI's ubuntu-24.04
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.8 ("Providers: Linux first"; "Acceptance"), the first part of `U-cell-linux` (`U-contain-seats`' Linux
provider, ORGANISATION § 15 lane A). **A protection — CRED-001, the `state/` ledgers and holdout, AUTH-001, no terminal: red-first and ONE watched mutant.**
Seat A; heavy (the agent boundary: opus). **No rung.** Parts b–d (cgroup + grant binding, route a for codex, network) follow; this unit wires NO seat.
**Today:** the Unix launcher `trade --spawn-contained` only `setsid`s and supervises (`src/TradeAgent.Core/Containment.cs:57-129`, `Process.Start` at
:99-101); `Containment.Sandbox()` says NONE (`src/TradeAgent.AgentRuntime/Containment.cs:43-45`); a launcher that will not start falls back to a BARE
start (:363-370, :382-396). **Survey (RUN, local Ubuntu 24.04 VM, kernel 6.8, Landlock ABI 4, `fleet/tmp/A-org/cell-linux/survey.md` § 1, kit `probe/`):**
Landlock + NNP + a seccomp `socket(AF_UNIX)` rule denied `state/`, a dummy owner secret, symlink/hardlink/rename escapes, `setsid`'d descendants,
`$HOME` persistence, `/proc/<pid>/environ`, TCP, the user D-Bus and cgroupfs writes; WITHOUT the seccomp rule the cell ran
`systemd-run --user` and read a planted `state/` ledger (`ledger-secret`). CI's ubuntu-24.04 is kernel 6.17 = ABI 7 (runner-images readme 20261004).
**Observable result:** `ProcessContainment.Start(psi, cell)` starts a program confined by a `CellPolicy` on Linux with Landlock ABI ≥ 6, or refuses in
words naming the kernel, the ABI found and the minimum; never a bare start for a cell launch. Steps in an existing CI job prove the file/IPC rows on every push (amendment and item 6).
Read first: `CLAUDE.md` (no terminal; refuse, never guess); ORCHESTRATION §§ 2.3, 2.8, 5, 8; the survey (whole); `Core/Containment.cs` (whole);
`AgentRuntime/Containment.cs:320-430`; `TradeCli/Program.cs:12-14`; `tests/TradeAgent.IntegrationTests/ContainmentTests.cs:139` (`DeployLauncher`); build.yml:95-137, :285-290.
Items, one commit each, one-sentence messages:
1. **`CellPolicy` and `CellPlatform`** (new `src/TradeAgent.Core/Cell.cs`): policy = workspace (RW), read-only inputs, executable roots (the runtime's
   tools, `/usr`, `/lib*`, `/bin`, `/etc`, `/proc`), an env allowlist with values (no inheritance), TCP ports (empty = none); serialised to an app-owned
   file, hashed (sha256). Platform = kernel release, Landlock ABI (`landlock_create_ruleset(NULL,0,VERSION)`), `/sys/kernel/security/lsm`,
   `kernel.apparmor_restrict_unprivileged_userns`. `CellPlatform.Refusal()` returns words when ABI < 6 or Landlock is absent from the LSM list.
2. **The native layer** (`src/TradeAgent.Core/CellNative.cs`, P/Invoke, Linux-only): Landlock ruleset (handled = every fs right of the ABI + TCP
   bind/connect + scopes ABSTRACT_UNIX_SOCKET|SIGNAL; allow-rules from the policy), `prctl(PR_SET_NO_NEW_PRIVS)`, a seccomp BPF with an arch check
   (x86_64/aarch64; anything else KILL) denying `socket(AF_UNIX)` with EACCES, `io_uring_setup/enter/register`, `keyctl/add_key/request_key`, `bpf`,
   `perf_event_open`, `userfaultfd` (`socketpair` stays allowed — RUN), `close_range(3,~0)`, `execve` with an envp built from the policy only.
3. **The cell stage of the launcher:** `trade --spawn-contained --cell <policy-file> <program> [args]` — the supervisor stays as today (setsid, watch,
   `TreeTeardown`) and starts `trade --cell-exec <policy-file> …`, which on its own thread does NNP → seccomp → Landlock → `execve` (below ABI 8 a
   restriction covers only the calling thread: Landlock doc). Any failed step exits 126 with the step and errno; the policy file must be app-owned
   (under `Paths.Home`, not the workspace) or the stage refuses. Both are flags on `trade`, never verbs, never a pipe op (AUTH-001).
4. **`ProcessContainment.Start(psi, cell)`** (`AgentRuntime/Containment.cs:356`): a cell launch on a platform with a `Refusal()` or without the
   launcher THROWS `TradeAgentException` (new `ErrorCode.CELL_UNAVAILABLE`) — the bare fallbacks at :363-370/:382-396 stay for non-cell launches only.
   `ContainedProcess` gains `Cell` (policy hash + `CellPlatform`) for the Mechanism line. `Sandbox()` stays NONE: no seat uses a cell until -c (honesty).
5. **The benchmark rows** (`tests/TradeAgent.IntegrationTests/CellLinuxTests.cs`, `[Trait("Category","Cell")]`, Linux-only; a missing prerequisite
   FAILS, never skips): one test per row of survey § 3 marked "yes" — workspace, read-only, `state/` (a planted `state/tradeagent.db`), user secrets
   (dummy `~/.codex/auth.json`, `~/.ssh/id_x` under a test HOME), symlink/hardlink/rename/`..` escapes, descendants (sh→sh→python, a setsid double
   fork), persistence (`~/.config/systemd/user`, `~/.bashrc`), host processes (environ, ptrace, kill), IPC (a planted pathname socket at the gateway
   pipe's own name, an abstract socket, `systemd-run --user`), TCP denied, no tty, and the cell stage's `/proc/<child>/status`: `NoNewPrivs: 1`, `Seccomp: 2`.
   `/proc/<pid>/cmdline` readable is ASSERTED as a recorded gap (it turns the test red the day it closes, so the profile is updated); UDP is DENIED (amendment (i)).
6. **The Cell rows on CI, in an EXISTING job (the orchestrator's ruling: no new always-on job — GitHub's 20 concurrent jobs are shared by both seats):**
   add steps to `test (ubuntu-latest)` (build.yml, `if:` that matrix entry only; ~13-14 min today, under `linux-host`'s 20) — or to `linux-host` if the
   rows need its service setup; say which and why. The first step prints `uname -r`, the Landlock ABI and `/sys/kernel/security/lsm` and FAILS if
   Landlock is absent or the ABI < 6; then `sudo loginctl enable-linger runner` and `dotnet test --filter Category=Cell` under `sudo systemd-run --wait
   --pipe -p User=runner -p Delegate=yes` (the host's shape for -b). Every other test filter adds `&Category!=Cell` (build.yml:131 and the timing steps).
Red-first tests (quote each red at base — the flag and types do not exist): (a) `A_cell_cannot_read_or_write_state_or_the_owners_login`;
(b) `A_cell_cannot_escape_by_symlink_hardlink_rename_or_dotdot`; (c) `A_cell_descendant_and_a_setsid_daemon_stay_confined`;
(d) `A_cell_cannot_reach_any_named_or_abstract_socket_or_the_user_manager`; (e) `A_cell_cannot_signal_trace_or_read_the_environment_of_an_outside_process`;
(f) `A_cell_launch_below_landlock_6_is_refused_naming_both_versions` (fake `CellPlatform`, runs on every OS);
(g) `A_cell_launch_never_falls_back_to_a_bare_start` (launcher missing / not executable → throws; today's fallback for non-cell launches unchanged).
**The watched mutant:** remove the `socket(AF_UNIX)` rule from the seccomp filter ⇒ (d) red: the cell connects to the planted gateway-pipe socket and
`systemd-run --user` reads the planted ledger (the survey's escape). Quote it red on CI, restore, green.
**CI evidence:** the Cell steps' log quoted in the report: kernel, ABI, LSM line, and every Cell test passed with its name; no new job: the run stays at 12.
Out of scope: the per-attempt cgroup, `cgroup.kill`, `Delegate=yes` in `packaging/linux/tradeagent.service` and the broker's grant binding (-b); codex
`exec-server` and any seat using a cell, `CellAssurance` on the attempt, `Sandbox()` changes (-c); egress classes, UDP, packages (-d); the host run
(-e, the owner's word); Windows/macOS cells; user/mount/pid/net namespaces (Ubuntu restricts them: survey § 1).
Protections and how kept: CRED-001 and `state/`/holdout — Landlock denies, tested with planted dummies (never a real login); AUTH-001 — flags on
`trade` grant nothing and reach no gateway; no terminal — no console, no sudo at run time (CI's sudo builds the host's unit shape only); refuse-never-
guess — an unknown ABI or LSM list refuses with both versions named; honesty — `Sandbox()` unchanged until a seat runs in a cell.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (speed mode): Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI,
all three platforms with the Cell steps; the Codex review started with the CI wait; names vs `main` 0 removed. `## Report` ≤ 20 lines appended here.
**Seat A's amendments (2026-10-11, the survey's facts + the orchestrator's views, adopted):**
(i) **Sockets default-deny by family:** the seccomp filter ALLOWS `socket()` only for AF_INET/AF_INET6 with type SOCK_STREAM (mask SOCK_NONBLOCK|SOCK_CLOEXEC
in the type argument; Landlock then governs TCP ports) and denies every other family/type with EACCES — AF_UNIX, AF_NETLINK, AF_PACKET, AF_VSOCK, and
SOCK_DGRAM incl. ICMP — so the UDP gap is CLOSED now by seccomp, not recorded open until ABI 10. Rows: `A_cell_cannot_send_udp_or_open_a_netlink_or_packet_socket`
(red at base) and `A_cell_runtime_keeps_a_connected_socketpair` (socketpair(AF_UNIX) still works: runtimes wake themselves with one, and a pair reaches no listener).
A runtime that breaks under this (name it, quote the errno) is reported, not loosened. (ii) `io_uring_setup/enter/register` denied (seccomp cannot see
io_uring's socket/connect ops) — row `A_cell_cannot_use_io_uring`. (iii) **Fail closed below ABI 6:** ABI 6's SCOPE_SIGNAL is what stops a same-uid cell
from killing the app; below it the launcher refuses the cell (test (f)), never runs it weaker. (iv) `/proc/<pid>/environ` of an outside process is a
TEST ROW (Landlock's ptrace scoping), not an assumption — test (e) asserts it. (v) `/proc/<pid>/cmdline` of other same-user processes stays readable
(no hidepid without a mount namespace): keep it ASSERTED as a recorded gap — `U-prompt-stdin` (its own unit, before -c) takes every prompt out of argv.
(vi) The watched mutant is unchanged (drop the AF_UNIX denial ⇒ (d) red); with (i) the mutant is "allow AF_UNIX in the family allowlist".

## Report
**Tip ed408023** (code; build + fix pass on origin/main a09937da; this report commit sits on it). Gates: (1) `dotnet build -c Release --no-incremental` → `0 Warning(s)` `0 Error(s)`. (2) Unit CellPolicy/CellSeccomp/CellLaunch/CellGrant `Passed: 15, Skipped: 3` and Integration CellLinux+Containment `Passed: 2, Skipped: 21` (Linux-only rows skip on macOS), ×3 on ed408023. (3) CI 38170920602 on ed408023: all 12 jobs green (no new job). (4) names vs main: `removed: 0`, 2597→2604. Tests box (ed408023, real Windows): GREEN, Unit 1785 / 8 not run, Integration 692 / 22 not run (21 Cell rows + 1), Fault 498, timing 6+91+18; his files unchanged.
Build pass (c0157758, before rebase): items 1–6 done — Cell.cs, CellNative.Enter (deviation: `close_range(3,~0,CLOSE_RANGE_CLOEXEC)`), `--spawn-contained --cell` → `--cell-exec`, `ProcessContainment.Start(psi, cell)` throwing CELL_UNAVAILABLE, the rows, and the Cell steps in `test (ubuntu-latest)` under `sudo systemd-run -p User= -p Delegate=yes` after linger; `Sandbox()` unchanged; its base red was a compile failure (`CellPlatform` could not be found).
Fix pass (seat A's triage of the Codex review): #1 seccomp allows `socketpair()` for AF_UNIX SOCK_STREAM|SOCK_SEQPACKET only (flags masked), datagram pairs EACCES; from ABI 9 the ruleset also handles RESOLVE_UNIX. #2 each grant is opened first (O_PATH|O_NOFOLLOW|O_CLOEXEC; a final-component symlink such as Ubuntu's /lib is opened through), its real target read from /proc/self/fd and statx'd, judged, and THAT fd granted. #3 the protected set (home and above, state/ — ledgers, holdout, credentials — and cells/) is judged in every spelling, as written and realpath'd. #4 a read-only input inside, equal to or containing the workspace is refused in words, by path in `Problem()` and by descriptor in the ruleset. #5 row (d) split in two. #6 one policy file per launch (`<sha256>.<16 hex>.cell.json`), removed by the supervisor after the cell exits and by `ContainedProcess.End` when it is killed. #7 the stage refuses a policy file not owned by this account (statx uid vs geteuid).
Re-review (`fleet/tmp/A-org/u-cell-linux-a-fix/U-cell-linux-a-rereview.md`), its three points closed in ed408023: a workspace symlink retargeted onto an input (unit row), a killed cell's policy file (Linux row), and the pair-end row accepting EACCES where ABI 9 refuses the lookup before EISCONN.
RED before (8449af5b, the tests ahead of the fixes): local unit run `Socketpair_is_a_unix_stream_or_seqpacket_pair_only` `Expected: 327693 Actual: 2147418112` (x64 and arm64), `A_grant_naming_the_real_state_…` and `A_read_only_input_inside_…` `Sub-string not found`; on CI 38167984274's ubuntu runner `A_grant_retargeted_to_state_after_the_policy_was_checked_is_refused`: `a Landlock ruleset was built granting …/cellgrant-359e62de/input, which by then led to …/state`.
MUTANT (AF_UNIX allowed in the family allowlist, with the Cell steps forced to run past the unit filter test's own red; CI 38170963861, sha d5734a76, reset off the branch; seat A cancelled the jobs that could not speak to it): `Failed …A_cell_cannot_reach_the_gateway_pipe_or_an_abstract_socket` `expected the line 'gateway-pipe-denied errno=13'` / stdout `gateway-pipe-connected`, `abstract-denied errno=1`; `Failed …A_cell_cannot_escape_through_the_user_manager_to_read_the_ledger` `Assert.DoesNotContain() Failure: Sub-string found` `"systemd-run-rc=0"`; `Total tests: 21 Passed: 19 Failed: 2`. (The first mutant run, 38168887435, stopped at the unit test's red before the Cell step.)
CI cell platform (38170920602): `cell platform: kernel 6.17.0-1022-azure (x86_64); Landlock ABI 7; LSM lockdown,capability,landlock,yama,apparmor,ima,evm; apparmor_restrict_unprivileged_userns 1`; `Total tests: 21 Passed: 21`: the 16 rows before plus cannot_make_a_datagram_socketpair, stream_socketpair_still_works, stream_pair_end_cannot_connect_or_sendto_a_pathname_socket, policy_file_is_removed_after_the_cell_exits, and (d) as cannot_reach_the_gateway_pipe_or_an_abstract_socket + cannot_escape_through_the_user_manager_to_read_the_ledger (all `A_cell_…`). The two CellGrant rows ran in the ubuntu unit step (1793 passed, 0 skipped).
NOT verified: the datagram-pair Linux row red at base (CI 38167984274's unit red skipped the Cell step; its red is the BPF interpreter's); aarch64 cells (CI is x86_64; the arm64 filter and its O_NOFOLLOW value are proved only by the interpreter and the header); ABI 9's RESOLVE_UNIX on a real kernel (none on CI). OWED: a policy file left by a crash of both the app and the supervisor is not swept; the Colima VM stays off-limits (the orchestrator's ruling).
