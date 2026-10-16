# U-cell-linux-b — the per-attempt cgroup under `tradeagent.service`'s `Delegate=yes`: every Linux launch born in `attempt-<id>/`, `cgroup.kill` teardown, the restart sweep and ceilings, and cgroup membership ANDed onto `U-grant-tree`'s grant predicate
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.3 ("a per-attempt cgroup on Linux (descendants cannot leave it, whatever session they create …)") and § 2.8 ("a per-attempt
cgroup in a subtree the app's systemd service delegates"); ORGANISATION § 15 row `U-cell-linux-b` (:443). **Protections — AUTH-001, ORDER-001 (cross-container replay),
UNKNOWN-001 (untouched), no terminal, refuse-never-guess, recovery: red-first and ONE watched mutant.** Seat A; **opus/high**. **No rung** (cgroups are live facts; grants
stay in memory). **Dispatch only after `U-grant-tree` lands** (seat B): this unit extends its seams and never replaces them.
**Today (main `bd151496`, READ; `U-grant-tree` = `origin/u-grant-tree` `ea9f799b`, READ — re-checked by seat A at dispatch 2026-10-16 22:4x: U-grant-tree LANDED `eb943501` (record `cc17d93e`) after its fix pass 2 (a Windows teardown whose snapshot fails is never read as leader-only; owner text kept on a claim refusal), and O4 + U-seat-signin landed: `AgentSession.cs`, both `Containment.cs` moved — grep every pointer below before trusting its number):** no cgroup code; `packaging/linux/tradeagent.service:19-34`:
`User=` (:21), `KillMode=mixed` (:33), no `Delegate=`. The launcher `setsid`s (`Core/Containment.cs:110`), starts the program (:142), ends the session (:165; app side
`ContainedProcess.End`, `AgentRuntime/Containment.cs:234-255`) — a double-forked `setsid` daemon reparented to init escapes that proof. -a's record OWES the sweep of a policy
file left when app AND supervisor crash (BUILD-STATUS.md:10434ff). The Cell step runs as `sudo systemd-run --wait --pipe -p User=… -p Delegate=yes` (build.yml:222-236).
**Seams extended (`ea9f799b`):** `AttemptContainer.Contains(PeerFacts) → MembershipVerdict{Member|Outside|Unknown}` (`Security/AttemptContainer.cs:9-59`); `PeerFacts(Pid, Start,
ReadAt){Source}` from `PeerImage.Facts(int)` via `SO_PEERPIDFD` (`PeerImage.cs:408-432`, pidfd closed before return); `ContainedProcess.Container` = `SessionContainer` on Unix
(`AgentRuntime/Containment.cs:185-199`, :356) over `TreeTeardown.Holds`/`IsMember` (`Core/Containment.cs:869`, :882); `GatewayPipeServer.ContainerRefusal` (:1132-1142, after
`GrantRefusal`'s image rule, :1096); `AgentGrants.Bind` (:277) at `AgentSession.cs:845` after `ProcessContainment.Start(psi, hold)` (:816; overloads :560, :582, :651).
**Observable result:** under `Delegate=yes` the app moves itself into `<its cgroup>/supervisor/`, enables `+pids +memory` below its root, kills and removes every leftover
`attempt-*` and cell policy file, and starts each Linux launch (cell or not) with its supervisor already in `attempt-<attemptId>/` — every descendant is born there, whatever
session it makes. Teardown = `cgroup.kill`, `populated 0`, THEN today's session teardown, then `rmdir`; survivors named. A grant is served only when the session proof says
Member **AND** the peer's cgroup, read through the held pidfd, is that attempt's directory or beneath it (RULING 1). A CELL launch without a delegated subtree or its
controllers is refused `CELL_UNAVAILABLE` naming `Delegate=yes`; a non-cell launch without one keeps the session binding, its Mechanism line saying "session only — no
delegated cgroup". Q2: N is not raised (the cgroup is one part of a PROVEN cell class). Q5: a cell process still enters `AgentPresence` — untouched.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.3, 2.5, 2.8, 5, 8; the survey §§ 1, 3, 4; `U-grant-tree`'s brief + reports; both `Containment.cs`; the seams above; `prove.sh:194-252`.
Items, one commit each, one-sentence messages:
1. **`AttemptCgroups`** (new `src/TradeAgent.Core/CellCgroup.cs`, Linux-only): `ForThisProcess()` reads `/proc/self/cgroup` (exactly one `0::` line, else a refusal), proves it
   delegated (owned by this uid, `cgroup.procs` writable), moves the app into `supervisor/`, writes `+pids +memory` to the root's `subtree_control`; `Open(root)` is the test
   seam (never read from the environment). `Create(attemptId)` = `attempt-<id>/` with `pids.max` 2048 and `memory.max` half of MemTotal (named constants, on the Mechanism
   line); `Kill(dir)`; `Sweep()` = every `attempt-*` killed and removed, every file in `CellPolicy.Directory` from before this start `CellStage.Discard`ed (closes -a's OWED).
2. **The launcher joins before it starts anything:** `trade --spawn-contained --cgroup <attempt-dir> [--cell <policy>] <program>` — the supervisor checks the directory
   (canonical, under `/sys/fs/cgroup`, named `attempt-*`, owned by this uid, `cgroup.procs` empty) and writes its own pid there BEFORE `setsid` and `Process.Start`; a failure
   exits 126 naming the step, nothing runs. A flag on `trade`, never a verb or pipe op (AUTH-001). 3. **`ContainedProcess` holds it:** every `ProcessContainment.Start`
   overload passes `--cgroup` when the app holds a root; `End()` = `cgroup.kill` → `populated 0` within `TreeTeardown.Bound` → `TreeTeardown.End` → `rmdir`; `AppHost.cs:672` runs `ForThisProcess` + `Sweep`.
4. **The AND (RULING 1):** `PeerFacts` gains `Cgroup` (init), read from `/proc/<pid>/cgroup` between the two `PidOf(pidfd)` reads; `CgroupContainer : AttemptContainer` is
   Member only for `Source == "SO_PEERPIDFD"` and a path at or beneath the attempt's directory (whole components); Unknown for a null/non-`0::` line, a removed directory or an
   `NSpid` of more than one field (pid translation); Outside otherwise. `Container` = `AllOf(SessionContainer, CgroupContainer)`, first non-Member wins. `GatewayPipeServer`
   and `AgentGrants` unchanged. 5. **`tradeagent.service`:** `Delegate=yes` — one root-owned line at install; the app never uses sudo.
6. **CI, existing jobs only (RULING 6):** the Cell step's `systemd-run` runs `bash -c` that moves its shell into `<svc>/runner` before `exec dotnet test` (a cgroup holding
   processes cannot hand controllers down); each row `Open`s its own `<svc>/rows-<guid>`. `linux-host` already installs the REAL unit: `prove.sh` asserts after phase 2 and
   after phase 4's `kill -9` that the service cgroup is the account's, the main pid in `supervisor/`, `pids memory` in `subtree_control`, no `attempt-*` left. Still 12 jobs.
Red-first tests (`CellLinuxTests.Cgroup.cs`, `public partial class CellLinuxTests`, `Category=Cell`; each red at base quoted): (a) `Cancelling_an_attempt_ends_a_setsid_daemon_that_left_its_session`;
(b) `Every_process_an_attempt_starts_is_born_in_its_cgroup` (sh→sh→python, a `setsid` double fork); (c) `A_cell_cannot_move_itself_out_of_its_attempts_cgroup` (the outside control
moves); (d) `A_restart_kills_and_removes_every_leftover_attempt_and_policy_file`; (e) `An_attempt_cannot_fork_past_its_pids_ceiling`; (f) `A_cell_launch_without_a_delegated_cgroup_is_refused_naming_delegate_yes`
(fake platform + root, every OS); (h) `A_grant_from_a_process_in_its_session_but_moved_to_another_attempts_cgroup_is_refused` — a descendant of attempt A writes itself into attempt B's
`cgroup.procs` and presents A's grant (base: served); GUARD (g) `U-grant-tree`'s `A_process_the_attempt_started_is_accepted` stays green on Linux inside a cgroup.
**The watched mutant:** `Container` = the session proof alone (the AND dropped) ⇒ (h) red, quoted on CI, restored, green ((h) also catches a prefix match on the root).
**Evidence beyond CI:** the Cell step's log (kernel, ABI, service cgroup, `subtree_control`, rows by name); `prove.sh`'s lines on the REAL unit; tests box (Windows job unchanged). **`Delegate=yes` without a user service (RULING 4):** none is needed — the Cell step's `sudo systemd-run -p User= -p Delegate=yes` is a transient SYSTEM service of the host's
exact shape (survey RUN: `--scope --uid` left the cgroup root-owned; a service with `User=` + `Delegate=yes` gave it to the user), and `linux-host` runs the real unit file;
sudo only builds the unit, as `install.sh` does. **NOT VERIFIED until the owner's host (-e, his word):** his kernel, systemd and controllers, `memory.max` under real load,
systemd's cleanup of a delegated subtree across his restarts, a real seat's grant from a cgroup (no seat before -c).
Out of scope: route (a), `attempt-<id>/{harness,cell}` leaves, any seat in a cell (-c); egress (-d); owner-editable ceilings; Windows/macOS (unchanged); raising N. Conflicts:
`U-grant-tree` first (rebase on it); -d (`Cell.cs`, `CellNative.cs`, its own partial test file) — disjoint but the identical `partial` line; `U-thinking-seats-b` — other regions.
Gate and report per `fleet/BUILDER-PROMPT-v2.md`: Release `--no-incremental` 0 warnings; touched classes 3×; full suite on branch CI, three platforms, Cell and `linux-host`
lines quoted; Codex review with the CI wait; names vs `main` 0 removed; tests box line. `## Report` ≤ 20 lines appended here.
