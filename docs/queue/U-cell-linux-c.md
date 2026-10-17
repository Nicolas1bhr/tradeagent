# U-cell-linux-c — route (a) for codex seats: the harness in `attempt-<id>/harness`, `codex exec-server --listen stdio` in `attempt-<id>/cell` with its environment built from scratch and an empty read-only home, the host benchmark that writes `cell_proof`, and the cell's assurance profile on the attempt
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.8 (route (a) on its four conditions; Acceptance *the broker*, *credentials*), § 2.3 (no non-legacy position on a real-model seat
until the host offers a PROVEN executor cell class), § 2.5 (lanes and presence read the RECORDED profile), § 5; R-exec-server DECISION 1; ORGANISATION § 15 `U-cell-linux-c` (:445).
**Protections — CRED-001 (the login never in a cell), AUTH-001 (no verb, pipe op or setting lifts the gate), INBOX-001 / COUNCIL rule 7 (presence DEFAULT IN), BUDGET-001,
honesty (every class measured on its build and host), refuse-never-guess, no terminal: red-first and ONE watched mutant.** Seat A; **opus/high**. **A rung (the next free at
dispatch).** **After** `U-thinking-seats-c` (in flight 2026-10-17; `U-cell-linux-b`, `U-thinking-seats-a` rung 39 and `-b` LANDED: `cell_proof`, `SeatClasses.Cell`) — and dispatch takes the NEXT FREE RUNG (41 is O5b's if it lands first), (§ 15's order; reuse its per-class
seat data). Survey: `fleet/tmp/A-org/cell-linux/c/survey.md` (main `e3344445`) — grep every pointer before trusting its number.
**Today:** the exec'd program's envp is the policy's alone (`CellNative.cs:236`), but supervisor and stage inherit their starter's environment (`Core/Containment.cs:127-139`) and the
stage leaves its .NET IPC files in /tmp (`CellLinuxTests.Workspace.cs:50-58`); `Guarded` (`Cell.cs:202-231`) does not refuse `Paths.SeatHome` (the login), `bin/`, `tools/`,
`updates/`, `logs/`; `environments.toml` is not in codex's `Forbidden` (`RuntimeManifest.cs:765-769`) and, read only from `CODEX_HOME`, REPLACES `CODEX_EXEC_SERVER_URL` (vendor
`environment_toml.rs:222-233`); -b's `--cgroup` takes only an `attempt-*` dir (branch `Core/Containment.cs:225`); `cell_proof` is never written (ts-a `CellProofStore.cs:87-94`) and is
bound to `Environment.OSVersion` and `Versions.App` x.y.z (:37, :114); `SeatClasses.Cell => Confined` unconditionally (`SeatAssurance.cs:89`); `Sandbox()` is NONE (`AgentRuntime/Containment.cs:43`).
**Observable result:** a `cell`-class codex seat starts its harness (the login, outside every cell) in `attempt-<id>/harness` with a per-attempt `CODEX_HOME` whose
`environments.toml` names one stdio program, `trade --spawn-contained --cgroup <attempt>/cell --cell <policy> codex exec-server --listen stdio`; every model-directed shell,
file, patch and image action runs in the cell (egress `none`, no harness environment or home); on Linux the app runs the host benchmark at every start and writes `cell_proof`
only when every row holds, bound to machine, kernel, boot and build; each launch records `cell_assurance` and is refused when the platform no longer matches its proof.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.3, 2.5, 2.7, 2.8, 5, 8; R-exec-server; the survey; -a/-b/-d records; ts-a `CellProofStore.cs`, `SeatAssurance.cs`, `PositionActivation.cs`; ts-b `BrokerDoor.cs`; ts-c's brief; the kit `fleet/tmp/A-org/exec-server/` (`run_case.sh`, `es-tee.sh`, `mock/`, cases b1 b3 b4 b8 b9 b10).
Items, one commit each, one-sentence messages:
1. **THE PROBE, which decides** (CI's Cell step; the pinned linux-musl codex; the kit's mock; FAKE key; prints a table; fails only on a missing prerequisite): the exec-server
   in a real cell under (A) an empty READ-ONLY `CODEX_HOME`, (B) an empty writable one — starts? `initialize`, an empty `environmentConfig/read`, `process/start`? — and whether
   `codex exec --ignore-user-config` reads a per-attempt `environments.toml`. (A) holds ⇒ read-only; else writable and never read back by anything — recorded with the errno.
2. **The stage from scratch** (NOTE seat A 2026-10-17: -b LANDED item 5 already — Guarded refuses the seat home, bin/, tools/, updates/, logs/ and environments.toml, which is in Forbidden; re-grep and do not redo it; -c needs an EXECUTE-ONLY grant to run the pinned codex from tools/ in a cell, never write): the supervisor starts the stage with ONLY `TRADEAGENT_HOME`, `DOTNET_EnableDiagnostics=0`, `DOTNET_ROOT` (framework-dependent); `Guarded` refuses
   `Paths.SeatHome`, `bin/`, `tools/` (write), `updates/`, `logs/`; `codex/environments.toml` joins the shared seat home's `Forbidden`.
3. **The leaves:** `AttemptCgroup` gains `harness/` and `cell/`; `--cgroup` accepts an empty `harness`/`cell` leaf of an `attempt-*` dir (beneath the attempt: -b's AND unchanged).
4. **The route-a class** in ts-c's per-class data: the CLI deny set; a per-attempt `CODEX_HOME` (config.toml = the class's verified bytes; `environments.toml` with `default="cell"`,
   `include_local=false`; `auth.json` a symlink to the seat home's — codex writes it in place, `storage.rs` at rust-v0.160.1); no `environment_id="remote"` server; the cell
   workspace = the turn cwd under `AgentHome`; egress `none`; class `cell` recorded ONLY with a counted proof, else refused — never degraded.
5. **The host benchmark and the proof:** `CellBenchmark` runs the survey's Q-a rows through the product path at every Linux start (no press, flag or setting), inside
   `AgentPresence`, including the no-model route-a leg; all pass ⇒ one `cell_proof` row: machine hash, FULL osrelease, boot-id hash, deployed `trade` SHA-256 + `Versions.App`,
   profile hash (fix `HostIdentity`); `Proven()` counts this boot's row only; each launch recomputes the profile hash and refuses on a mismatch; a failed canary appends a revocation.
6. **The rung:** `cell_assurance` + `cell_assurance_row` (survey Q-b: property × side cell/harness × class HARD_PLATFORM | HARD_APP | MEDIATED | ADVISORY | GAP, with the proof id);
   the attempt's class = the weaker side. `Sandbox()` and the armed-live refusal answer per seat from it: a `cell` seat is still refused while live is armed (unconfined harness).
Red-first (`CellLinuxTests.Route.cs`, `public partial class CellLinuxTests`, `Category=Cell`; each red at base quoted): (a) `The_exec_server_in_a_cell_sees_only_its_policys_environment`
(no CODEX_HOME, HOME or bearer of the harness); (b) `Every_model_directed_action_of_route_a_runs_in_the_cell` (kit b1 b3 b4 b8 b9); (c) `A_cell_declared_mcp_server_never_starts` (b10);
(d) `The_cell_stage_leaves_nothing_in_tmp`; (e) GUARD `A_cell_cannot_reach_the_apps_diagnostic_socket_or_debug_pipes`; (f) `A_route_a_cell_cannot_connect_to_the_door`;
(g) `A_policy_granting_the_seat_home_is_refused` (every OS); (h) `An_environments_toml_in_the_seat_home_refuses_the_launch`; (i) `A_proof_is_written_only_when_every_row_holds`,
`…counts_only_on_its_boot_kernel_and_build`; (j) `A_cell_seat_without_a_counted_proof_is_refused`; GAPS asserted: `/proc/*/cmdline` readable, stat outside grants.
**The watched mutant:** the stage's execve passes its inherited `environ` instead of the policy's envp ⇒ (a) red AND the benchmark writes no proof. Quoted red on CI's Cell step, restored, green.
**Evidence beyond CI:** the Cell step's log (kernel, ABI, item 1's table and decision, benchmark rows, the proof row — machine id HASHED); the tests box (Windows unchanged). No real model,
never the owner's `~/.codex`, throwaway homes, FAKE keys. **NOT VERIFIED until the owner's host (-e, his word):** his kernel and LSMs, a real token refresh through the symlink, real model metadata, aarch64.
**Q2:** N not raised; **Q5:** presence NOT flipped — the harness is MEDIATED, so the attempt's `state`/`inbox` class is too; -c records the rows both will read. The lever: a confined
harness (Landlock on the codex harness: seat home RW, `state/` and the inbox denied) — a new card. Out of scope: a `door` egress class (U-execution-environment); route (c); Windows/macOS.
Conflicts: -b first (both `Containment.cs`, `CellCgroup.cs`, `AgentSession.cs`, build.yml, the `partial` line); ts-c first (`RuntimeManifest.cs`, `SeatProfile.cs`, `AgentSession.cs`);
ts-a first (rung 39, `CellProofStore.cs`); `U-org-lanes` reads what this records. Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched
classes 3×; branch CI on three platforms, the Cell step's rows quoted; Codex review with the CI wait; names vs `main` 0 removed; tests box line). `## Report` ≤ 20 lines appended here.
