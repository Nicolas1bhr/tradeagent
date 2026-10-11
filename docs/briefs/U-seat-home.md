# U-seat-home — every codex seat runs in ONE app-owned home under a default-deny profile the app verifies at every launch, never under the owner's `~/.codex`
**Arrow closed:** the seat-configuration gap of `docs/ORCHESTRATION.md` § 1 ("Seat configuration") and § 2.7 ("A seat never runs under the owner's own
agent configuration"); `docs/ORGANISATION.md` § 15 row `U-seat-home`. **A protection — AUTH-001, ORDER-005, CRED-001: red-first and ONE watched mutant.**
Seat A; heavy (the agent boundary and the money-adjacent attempt row: opus). **Rung 37** (one column). Part 1 of 2: the in-app "sign in again"
after onboarding is `U-seat-signin` (below), landed before M0 attempt 4; this unit's item 6 refuses in words so nothing fails silently between them.
**Today (RUN 2026-10-11, codex-cli 0.160.1 on this Mac, a local mock provider, `fleet/tmp/A-org/seat-home/survey.md` §§ 1-2):** the app never sets
`CODEX_HOME` (`RuntimeManifest.cs:499-502` inherits it) and passes `HOME`/`USERPROFILE` (`AgentEnvironment.cs:78, 87`); argv is
`exec --json --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox -m <model>` (`:649`, `AgentSession.cs:118-166`). Under a copy of the owner's
home the model was offered 14 tools incl. `mcp__node_repl{js,…}` and `mcp__cua_repl{js,…}`, the ChatGPT app's REPLs ran as children and the notify hook
received every prompt; even an EMPTY home offers 12 (multi-agent, goals, web_search, MCP resources), injects 5 bundled skills, loads a workspace
`.codex/config.toml` (its MCP server spawned) and `$HOME/.agents/skills`, and leaves a `git fetch` of the plugin repo running after codex exits.
Under the deny set below over a HOSTILE home: 4 tools (`exec_command, write_stdin, request_user_input, view_image`), no child process, canon kept.
**Observable result:** every codex process the app starts — turn, resume, `--version`, `login status`, sign-in, API key — carries
`CODEX_HOME=<Paths.Home>/seat-home/codex` and `HOME=<…>/seat-home/home`, never an inherited value; every turn's argv carries the deny set fresh and resumed;
a launch whose binary is not the pin, whose seat home or workspace holds a file the profile does not allow, whose probe shows a feature or MCP server
outside the allowlist, or whose home holds no credential is refused in words naming the file/feature/hash and the repair; every `ai_attempt` row
records the runtime-profile hash it launched under. The owner's `~/.codex` is never read or written by the app.
Read first: `CLAUDE.md` (no terminal; no backward compatibility; refuse, never guess); `docs/ORCHESTRATION.md` §§ 1, 2.7; the survey above (whole);
`RuntimeManifest.cs:494-660, 794-852`; `AgentEnvironment.cs`; `AgentSession.cs:118-166, 330-360, 510-561`; `CliAgentRuntime.cs:60-130, 220-300, 380-480, 600-720`.
Items, one commit each, one-sentence messages:
1. **The seat home and its environment.** `Paths.SeatHome` (`Paths.cs`, beside `Tools` :10) with `codex/` and `home/`, created by the app. A manifest field
   `SeatEnvironment` (data, `{"CODEX_HOME":"{seatHome}/codex","HOME":"{seatHome}/home"}`) applied by `AgentEnvironment.Apply` (:110-126) AFTER the
   whitelist, so `HOME`/`USERPROFILE` and an inherited `CODEX_HOME` never cross for a runtime that names one; codex's `KeepEnvironment` becomes `[]`
   (no compatibility branch). Every call site already funnels through `Apply` (`AgentSession.cs:557`; `CliAgentRuntime.cs:474, 623, 799, 876`) — check
   `Run(...)` (`:281` version, `:711` status) does too, and route it if not. Windows: set `USERPROFILE` too only if the box shows codex reads it (survey Risks).
2. **The deny set, as data, on fresh AND resume.** `RuntimeManifest.SeatArgs` keyed by seat class (one class today, `"cli"`), inserted by `AgentArgs.Build`
   (`AgentSession.cs:140`) for `ExecArgs` and `ResumeArgs` alike; codex's value is `fleet/tmp/A-org/seat-home/mock/seat-deny.args` verbatim (proved: exit 0
   under `--strict-config`, 4 tools). The app also WRITES `seat-home/codex/config.toml` (same keys as TOML, atomic) before every codex start — `login`,
   `features`, `mcp` take no `--ignore-user-config`, and a seat-written `chatgpt_base_url` there would steer a sign-in. Never `--ephemeral` (resume needs the rollout).
3. **The pin.** `InstallPlan` gains `Version` and `ExecutableSha256` per platform; `CodexInstall` (`RuntimeManifest.cs:794-817`) downloads
   `releases/download/rust-v0.160.1/…` (not `latest`) with the archive `Sha256` set (`Integrity.PinnedOr`, `CliAgentRuntime.cs:400`, then has a pin).
   `ResolveExecutable` (:78) for a SEAT accepts a file only when its sha256 is the pin — the PATH copy too (this Mac: `0.160.1-aarch64-apple-darwin`,
   `09fa44fd…82be`); otherwise refuse naming both hashes and both versions. Take each release asset's digest from the GitHub release, quoted in the report.
4. **Verify at every launch, fresh and resumed.** A `SeatProfile.Verify` called before admission: argv carries the class's deny set; `codex/config.toml`
   equals the app's bytes; no `AGENTS.md`, `plugins/`, `skills/`, `rules/`, `hooks.json` under `codex/`; no `.agents`/`.codex` under `home/`; no `.codex/` or
   `.agents/` in the seat's workspace (`Canon.SeenByCli`'s folder); and a probe — `codex features list` and `codex mcp list --json` with the seat args under the
   seat home (0.04 s / 0.03 s RUN) — shows every feature outside the allowlist false and no MCP server but the app's (none today). Behind an `ISeatProbe`
   so CI fakes it. Any miss ⇒ refused in words naming what and where; integrity labelled ADVISORY while seats are unconfined (§ 2.7).
5. **The profile hash on the attempt — rung 37.** `ALTER TABLE ai_attempt ADD COLUMN runtime_profile TEXT` (`Database.cs`, `Versioning.cs:380` → 37), written
   only in `AiAttemptStore.Begin`'s insert (`:256-277`, via `TurnMeter.Begin` `:820-853` and `AgentSession.cs:337`'s `Admit`), never updated; NULL before the rung.
   Value: sha256 over {executable sha256, version, seat argv minus prompt and model, config.toml bytes, the probe's two outputs}. A resumed session whose
   thread was opened under a different hash starts fresh (the loop's existing fresh-session path, `MissionLoop.cs:1744-1748`).
6. **Fresh start, refused in words.** A seat launch while `login status` under the seat home exits ≠ 0 is refused: "TradeAgent now keeps its own AI
   sign-in, separate from your own Codex. Sign in again." — no copy of `~/.codex/auth.json`, no migration (CLAUDE.md). Credentials stay
   `cli_auth_credentials_store="file"` in `seat-home/codex/auth.json`.
Red-first tests (CI has no codex: fake executables, fake `ISeatProbe`, the suite's existing fake-CLI pattern; quote each red at base):
(a) `A_codex_seat_runs_under_the_app_seat_home_never_the_owners` (red: an inherited `CODEX_HOME` and the owner's `HOME` cross, `:502`, `:87`);
(b) `A_codex_seat_argv_carries_the_deny_set_fresh_and_resumed` (red: base argv has no `--ignore-user-config`);
(c) `A_foreign_file_in_the_seat_home_or_workspace_refuses_the_launch_in_words` (`AGENTS.md`, workspace `.codex/config.toml`, `home/.agents`);
(d) `A_binary_that_is_not_the_pin_is_refused_naming_both_hashes`;
(e) `Every_attempt_records_the_runtime_profile_it_launched_under` and `The_profile_hash_moves_when_one_deny_flag_moves`;
(f) `A_seat_home_without_a_credential_refuses_with_sign_in_in_words`;
(g) `Sign_in_status_and_api_key_all_run_in_the_seat_home` (the three `Apply` sites).
**The watched mutant:** drop `--ignore-user-config` from codex's `SeatArgs` ⇒ (b) red — the one flag that keeps a seat-written `config.toml`'s MCP servers
out (`-c mcp_servers={}` merges, does not clear: survey § 1). Quote it red, restore, green.
**Evidence beyond CI:** dump the unit's real argv from a test and re-run `fleet/tmp/A-org/seat-home/mock/run-case.sh` with it under a throwaway home
holding the owner-config copy (survey case (d) recipe) — quote the 4 tools and the empty `ps`. The box (`TA_WIN_BOX=tests`) runs the suite; a Windows
mock run is NOT RUN unless the box has codex.
Out of scope: `U-seat-signin` (part 2: a dashboard Sign-in card when the seat home has no credential, reusing `BeginAuthenticationAsync` /
`SignInWithApiKeyAsync`; the Linux device code in the app window with expiry and retry per `U-linux-host`; a Doctor row "AI seat home"; the fleet's
observed-run audit moving from `~/.codex/sessions` to `<seat home>/codex/sessions`, `fleet/charters/M-milestones.md:36`); a broker-only seat class
(`U-thinking-seats`); OS confinement (`U-cell-linux`); `request_user_input` (no stable feature removes it); OpenCode's home.
Conflicts (re-check at dispatch): `U-admission-fail-closed` (in `docs/briefs/`) edits `AgentSession.cs:337` and `TurnMeter.Begin` — this unit adds one
argument there: rebase, keep its refusal. `R-exec-server` shares only the mock. Neither brief in flight takes a rung; re-check 37 is free.
**Seat A's amendments at dispatch (2026-10-11):** (i) rung 37 is ASSIGNED to this unit by the orchestrator (O4 takes 38). (ii) `U-admission-fail-closed`
(seat B) touches the same admission code: build on `main`; whichever lands first, the other rebases onto it and keeps its refusal. (iii) `request_user_input`:
run ONE mock case (mock emits a `request_user_input` call under `exec`, stdin closed) and quote what codex does; if any `--disable`/`-c` key removes the tool
and `--strict-config` accepts it, add it to the deny set; otherwise name the behaviour in the report and leave it. (iv) Windows: codex may take its home
directory from the known-folder API rather than `HOME`/`USERPROFILE` (so the real profile's `.agents/skills` could leak) — UNKNOWN here; if the tests box
is reachable and has codex, prove it; otherwise the report says NOT VERIFIED and it is an owed card for `U-cell-windows`, not this unit.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (speed mode: Release `--no-incremental` 0 warnings; touched classes 3× via `suite.sh`; the full suite on
branch CI, all three platforms; the Codex review started with the CI wait; names vs `main` 0 removed). The report names the rung, the pinned version and
every digest, the mock run quoted, and the RED-before and mutant lines. `## Report` ≤ 20 lines appended here.

## Report
- **Tip** `5077711c` (code; this Report commits on top) on main 06ebf6e7 (2 rebases: U-admission-fail-closed, U-reconcile-packet); **rung 37** (main 36). Build Release --no-incremental `0 Warning(s) 0 Error(s)`;
  touched classes 3× `Passed: 74` (SeatHome, AgentModel, LinuxHostRuntime, VendorLimit, TurnMeter, BudgetReservation, HarnessBudget); names: removed 0 (2575 → 2589).
  CI 38166217557 on 5077711c: every job success (dedupe-gate, shard-plan, package, package-linux, macos, ubuntu, windows 1-5, linux-host). Tests box run 20261011-185626-583276c5 GREEN (1754 unit · 692+1 not run integration · 498 fault · timing green; his files unchanged) — before the owner fixes; later NOT RUN (box busy).
- Items 1-6 (pass 1): seat env at the one Apply seam; deny set as data fresh+resumed and config.toml written each start; pin per platform; verify every launch; profile hash at insert; signed-out refused in words.
- Rebase: kept admission's refusal (LedgerRefused, NoLedger, null Admit refuses; the seat check sits beside StillRunning in TurnMeter.Begin); U-seat-home's ExecuteTaskAsync changes dropped (method deleted on main → review P2 closed).
- Fix 1 (P1 :272): a non-pin is never run, `--version` included (one choke point in ProbeVersionAsync + RequirePin; ISeatProbe.VersionAsync deleted); refusal names its sha256, the pin's, the pinned version, "version unknown — not run". RED: `Assert.Contains() Failure: Sub-string not found` (2 tests).
- Fix 2 (P1 :241): cache dropped, hashed from bytes at every check (seat A's ruling: no metadata is unforgeable; codex 241,556,032 B hashes in 0.61 s); binary integrity labelled ADVISORY in code. RED: `a same-length swap with its time restored was taken for the pin`.
- Fix 3 (P1 :35): lstat-style link/junction/reparse refusal on every component root→folder→config, folders made one level at a time, config read with O_NOFOLLOW (Unix), never written through a link. RED: `seat-home/codex -> owner-home/.codex: detected as installed`; fixture byte-identical after.
- Re-review fixes: foreign-owned folder refused (Unix: chmod 0700 only an owner may do; Windows: owner SID) — RED `UnauthorizedAccessException: Access to the path '/usr/codex' is denied`; Windows folders get a protected owner-only DACL; a folder that cannot be made private (macOS CI: `Read-only file system: '/usr'`) is refused in words.
- Mutant (drop `--ignore-user-config`), rebased tree: (b) `Item not found in collection`, (c) `Filter not matched in collection` → restored, green.
- Pin codex 0.160.1; archive digests = GitHub's; program digests win 9e7c59c0…f55d · linux x64 f34a4d23…1e35 · linux arm64 fbbaec80…eb3e · mac arm64 09fa44fd…82be (= PATH copy) · mac x64 8757e2a2…eafc.
- Mock (pass 1, unit's real argv, hostile throwaway home): 4 tools fresh + resumed, `ps` empty, notify hook never called; real probe on a clean seat home: 26 allowlisted features, `mcp list` `[]`, `login status` rc 1.
- request_user_input under exec: codex answers "unavailable in Default mode", turn exits 0 — left as is (no key removes it).
- Deviations (pass 1): `forced_login_method` dropped (blocks a pasted key); settings on `RuntimeManifest.Seat`; profile change → fresh session in AgentSession; meter checks the seat via the role's attached conversation. Fixer: test (a) now pins its script (a non-pin is no longer run).
- NOT VERIFIED: Windows USERPROFILE / known-folder home (→ U-cell-windows); `$CODEX_HOME/AGENTS.md` refused only by the inventory check, ADVISORY while unconfined; root on Unix (chmod proves no ownership — documented, product never elevates); Windows red for the DACL test (green on CI only).
- OWED (re-review 3, beyond brief): macOS ACL entries survive 0700 (clear ACLs on seat folders); Windows explicit grants on an existing `codex/auth.json` survive the folder DACL (reset file DACLs).
