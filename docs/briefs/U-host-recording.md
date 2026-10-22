# U-host-recording — a recording-only start on the Linux host is proved on CI, the kit carries the read path the fleet needs, and `tools/linux-host.sh` reads, deploys and stops it on the owner's word

**Arrow closed:** the owner's word of 2026-10-21 — a recording-only deploy on his always-on Linux server ("Yes, the Linux server"): the app with its AI
off records the tape around the clock; `docs/PRINCIPLES.md` "The edge and the moat" (the moat accrues only while the app runs; about 6 h recorded by
2026-10-08, R22); R21 § 3 (the switch-on). Survey S-tape-host: `fleet/tmp/PREP-2026-10-21/tape-host/` (`runbook.md`, `gaps.md`; main `25bae9bc`).
**Light — CI and tooling; no product code, no protection touched, no rung.** Seat A; **sonnet/high**. **Depends (landed):** `U-linux-host` (2026-10-09).

**Today (main `25bae9bc`, by reading):** a fresh home records from its first start whatever its setup state — `AppHost.StartAsync` opens the database
(`AppHost.cs:789`) and the tape (`:847`) and starts it (`:861`) before the window picks setup or shell (`MainWindow.cs:165,187`); a fresh home's defaults
are `RecordMarketContext` true (`Trading.cs:396`), `AiWorksOnItsOwn` false (`:332`), the practice simulator (`AppHost.cs:805`), and with no runtime chosen
no AI process is built (`SeatSignIn.cs:124,221`). But nothing PROVES it: CI's `linux-host` job (`packaging/linux/ci/prove.sh`) stops the fresh home at once
(`:109-121`) and asserts the tape only after a seed that writes sixteen onboarding rows, a false AI_AUTHENTICATED among them (`:125-138`). The kit installs
no `sqlite3` (`install.sh:129-141`), which the read path needs (`prove.sh:49-52, 98`). `tools/` holds `win-*.sh` only.

**Items, one commit each:**
1. **CI proves the recording-only start** (gap G1): a phase in `prove.sh` between the fresh start and the seed — `tape-sources.json` installed (`:138`)
   WITHOUT the SQL seed; start; a loopback tape row within 120 s; `ai_attempt` count 0; no `codex|opencode|node` process under the account; RSS and CPU
   sampled for 10 minutes on the setup screen (its poll looped once before — the 2026-10-09 record's `269b02c2`). Seen red once (the source file withheld ⇒
   no row), quoted; green on CI's `linux-host` job.
2. **The kit carries its read path** (G2): `packaging/linux/tape-health` — `prove.sh:98`'s per-source counts, the newest delivery's age per source,
   `systemctl show -p NRestarts`, the daily report's tape line (`DailyReports.cs:869`) — installed beside `display-prepare`; `sqlite3` in the kit's
   package list; linted where the workflow lints the kit (`build.yml` ~`:345`). Settling check: item 1's phase reads its numbers through `tape-health`.
3. **`tools/linux-host.sh`** (G3), modelled on `tools/win-env.sh:1-48`: host, account and key from `~/.tradeagent/linux.env` (outside the repo; key auth,
   BatchMode). Verbs: `recon` (`uname -mr`, `/etc/os-release`, `/sys/kernel/security/lsm`, `systemctl --version`, `stat -fc %T /sys/fs/cgroup`,
   `timedatectl` — the tape assumes no clock step over 10 min, `TapeReader.cs:407-410` — `free -m`, `df -h /var`, `nproc`, `ss -ltn`,
   `getent passwd tradeagent`); `install <tarball> <the owner's dated word>` (stop → back up `state/tape.db*` and `tradeagent.db*` to a dated folder outside
   the home → the kit → `health`; refuses without the dated word); `health` (runs `tape-health`); `stop`. Host names and the private network's name come from
   the env file only (the scan). `tools/README.md` gains its section. Settling check: shellcheck clean, and `recon` + `health` run against CI's own
   linux-host job host — or say which part could not run there.

**The deploy it serves is the orchestrator's, not this unit's** (rules, 2026-10-21): never seed the host's home (it becomes M-org1's home; a seeded
AI_AUTHENTICATED is a false record); each deploy on the owner's word, with a backup; no build that refuses older homes before `U-older-layout-refusal`
lands (D1); the host's tape is a new clock — the Mac's hours are neither moved nor merged (D5). The first deploy may run by hand from the survey's runbook
before this unit lands: the run on the host is its own evidence.
**Gate:** the workflow's lint and the `linux-host` job green; Release build 0 warnings; names vs main 0 removed (no C# expected). Tests box: not applicable.
**Read first:** the survey's `runbook.md` and `gaps.md`; `packaging/linux/` whole; `.github/workflows/build.yml:330-390`; `tools/win-env.sh`,
`tools/README.md`; `BUILD-STATUS.md`'s 2026-10-09 `U-linux-host` section.
**NOT in scope:** `U-tape-run-record` (G4: the tape telling "app down" from "switched off" — a `tape.db` rung, EVIDENCE; needed before "24/7" is a claim;
CARD); an arm64 leg (only if `recon` says ARM — D4); any product change.

## Report
Tip: a260d014 (code), rebased on origin/main b3d0a777; run 39711410266 (first run 39686471711 was red on a root-owned evidence dir in the tool step; fixed in a260d014). Review: fleet/tmp/A-org/u-host-recording-build/U-host-recording-review.md (3 findings, base b3d0a777 not 199a0074 after the rebase; untriaged by me).
- Build Release --no-incremental: 0 Warning(s), 0 Error(s). shellcheck 0.11 clean on tape-health, install.sh, prove.sh, tools/linux-host.sh; actionlint clean; CI's package-linux "Lint the Linux scripts" green (shellcheck 0.9.0).
- Run 39711410266: all jobs green but `test (ubuntu-latest)`: CellLinuxTests.A_cell_installs_a_python_package_into_its_workspace_from_a_loopback_index failed "outside the workspace, /tmp changed: /tmp/systemd-private-...-phpsessionclean.service-..." (the runner's own systemd timer made a /tmp dir during the test). My diff has no C#; same test green on ubuntu in run 39686471711 (same branch, earlier). Goes to the manager, not called a flake.
- linux-host: success (33 min), summary.txt PASS lines quoted: "tape-sources.json withheld: the app recorded 73 tape rows from its built-in sources and no loopback row in 90 s"; "recording-only: a loopback tape row 1 s after the start of a home nobody onboarded"; "40 samples 15 s apart over 10 minutes; peak RSS 277168 kB; mean CPU after the first two minutes 0.4% of one core; 11 loopback rows"; "ai_attempt 0, no codex/opencode/node process, no AI step in the home"; later phases (kill -9, cgroup, VNC, stop) still PASS.
- names.sh main u-host-recording: sets base 3001 tip 3001, removed: 0. Tests box: not applicable (Linux kit and tooling). No C# test class touched.
1. DONE. prove.sh phase 0b between the first start and the seed (home never seeded before its asserts). Red-once: done ON CI as a guarded negative sub-phase (source file withheld => no loopback row in 90 s, asserted) rather than a red run. Deviations: loopback server and tape-sources.json install moved into 0b; phase 2 now baselines the row id; job timeout-minutes 45 -> 65 (the phase adds ~12 min); CPU bound (mean <50% of a core after 2 min), RSS <1 GiB and >=5 loopback rows are my own guards, not measured limits.
2. DONE. packaging/linux/tape-health installed beside display-prepare, sqlite3 in install.sh's packages, linted in package-linux; the phase reads its numbers through it. The daily report's tape line is read from the newest state/reports/*.md (none exists on CI: that branch is exercised only by a local fake-home run).
3. DONE. tools/linux-host.sh (recon, health, install, stop) + tools/README.md section. Deviation: `stop` also needs the dated word (runbook says so). CI ran all four verbs over a real ssh to its own runner with a throwaway key and env file (recon, health, refusal without the word, install with 6 files backed up to /var/backups, health, stop). Never pointed at any owner machine; no linux.env created.
Not verified: a first install (no home, VNC password generation path), the report-line branch on a real report, arm64, behaviour on a non-apt host.
