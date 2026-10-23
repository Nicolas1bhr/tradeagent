# U-cli-code-mode — the CLI seat class gets its tools back on a code-mode-only model; broker and cell keep code mode closed; a seat whose tools cannot run is refused in words
**P1 (the orchestrator, 2026-10-23 ~00:5x; seat A; opus/high — the agent boundary, credentials, a vendor pin). No rung.** It blocks attempt 5 (the M-org0 re-run).
Source: the orchestrator's read-only survey `fleet/tmp/M4-regression/report.md`. Read its §§ 1, 2, 4, 5 and 6; § 7 is this brief's origin. Pointers below were re-checked on main
`dd16ccbf` by seat A. The builder re-checks each at its rebase.
**Cause (≈95 %):** the app's own deny set disables codex's code-mode host for the CLI class: `"--disable", "code_mode_host"` in `CodexCliDeny` (`RuntimeManifest.cs:770`, the pair
at `:781`), and `features.code_mode_host = false` in the seat's config (`SeatProfile.ConfigBytes`, `:651`). It has been there since ca235dcc (U-seat-home, 2026-10-11). gpt-6-luna,
like every model on the owner's account, is `code_mode_only`, so every `exec` call failed closed with "code-mode host is disabled". Attempt 3 had no deny set, so the host was on.
**Goal:** the legacy pair and the Chat (class `cli`) can run tools on a code-mode-only model. Broker and cell seats keep the host disabled and keep their classic-tools model pin.
A seat whose tools cannot run is refused in words before admission. It is never passed off as turns that exit 0.

Items, one commit each, one-sentence messages:
1. **Move the flag.** Delete `"--disable", "code_mode_host"` from `CodexCliDeny`. Add it back EXPLICITLY to `CodexBrokerDeny` (`:796`) and `CodexCellDeny` (`:822`), which derive from
   `CodexCliDeny` today. Add `"code_mode_host"` to `CodexCliFeatures` (`:844`). Add it to the `Except` lists of the broker and cell feature allowlists (`:1021-1026`), which derive
   from the CLI list and must refuse a broker or cell seat that reports the host on. The seat's `config.toml` loses its line through `ConfigBytes`, so the profile hash changes.
   No compatibility is owed (CLAUDE.md).
2. **Pin the second vendor program.** With the host on, the CLI class runs `bin/codex-code-mode-host` (`.exe` on Windows). The vendor's layout is in `scripts/codex_package/layout.py:55-56,138`,
   and `install-context/src/lib.rs:176-206` finds the program. Add its sha256 per platform beside `ExecutableSha256`, measured from the vendor's official `rust-v0.160.1` release archives.
   Those are the archives the codex pin already comes from: hash them in your scratch directory and run nothing from them on this Mac, or measure them in CI.
   `VerifyAsync` refuses a CLI seat in words when the host program is missing or differs, naming both hashes, as the codex pin does. This matters on macOS, where the app installs
   nothing and pins only codex.
3. **Refuse a dead tool set.**
   (a) Give the CLI class a one-call dry run. It joins the loopback dry run, cached by binary, argument template and model like the broker's (`SeatProfile.cs:794`: `if (!broker)`
   returns before it today). When `exec` is offered, the mock answers request 1 with an exec call `text("probe")`, and request 2 must carry `probe` as the output. When `exec_command`
   is offered, the same with `echo probe`. Anything else is refused in words before admission.
   (b) The turn reader stops counting codex's `error` items as commands (`TurnMeter.IsToolItem`, `TurnMeter.cs:364`, counts any item that is not an agent_message or reasoning as a
   command and drops its text). The message goes to the engineering log and an Activity line. The vendor sentence "Code Mode is unavailable because" becomes manifest data, like the
   usage-limit sentence. On a match the loop stops launching, names the reason, and releases the wakes as it does for a gate refusal.
4. **Correct the doctrine.** Fix `BrokerSeat.cs:13-14` and `docs/ORCHESTRATION.md:275`. At 0.160.1, a code-mode-only model under `--disable code_mode_host` is offered `exec`, but every
   call fails closed; attempt 4's transcript is the evidence. With the host on, the JavaScript runs in a `codex-code-mode-host` child process of the codex that holds the login.
   The broker and cell pin to a classic-tools model stays.

Red-first (quote each red at base):
- (A) A unit test on any OS. The CLI class's args must not contain the adjacent pair `--disable code_mode_host`; `ConfigBytes` must not contain `features.code_mode_host = false`;
  `code_mode_host` is on the CLI allowlist. Broker and cell args carry the pair, and their allowlists exclude `code_mode_host`.
- (B) An integration test on CI with the pinned linux-musl codex the Cell step already unpacks (`TA_CELL_CODEX`) and the `CellRoute/mock_responses.py` mock, which already reads
  `custom_tool_call_output`. Use the CLI class's real args and config bytes, `-m gpt-6-luna` (bundled code_mode_only), and a throwaway home with no login. The mock answers request 1
  with an exec call `const r = await tools.exec_command({cmd:"echo ok"}); text(r.output)`, and request 2's `custom_tool_call_output` must contain `ok`. It is RED at base with
  "code-mode host is disabled". If the musl archive lacks the host program, say so and pin what the archive holds.
- (C) The turn reader. A captured stream holding the vendor's `error` item is refused in words and counts as no command. It is RED today, counted as 1.
- (D) GUARD: every codex seat's verify refuses a missing or different `codex-code-mode-host` in words, on the CLI class.

MUTANTS (each watched red, then restored):
- the pair put back on the CLI class ⇒ (A) and (B) red;
- the pair dropped from the broker ⇒ `BrokerSeatTests.cs:84` red;
- the pair dropped from the cell ⇒ (A) red.

Protections, and how each is kept:
- **The agent boundary.** Nothing changes for operator authority or the pipe. The CLI class is already `unconfined` with `--dangerously-bypass-approvals-and-sandbox`. Code mode's
  JavaScript runs in a V8 isolate with no filesystem, network or process access (R-exec-server b9). Its nested tools are the same `exec_command` and `apply_patch` the class already
  has, so it reaches nothing new. The activation gate (`PositionActivation.cs`) is untouched.
- **Law 2 and the cells.** Broker and cell keep the host disabled, keep the gpt-5.5 pin, and keep refusing any tool set that contains `exec`.
- **Credentials (CRED-001).** No new process reads the sign-in: the host is a child of the codex that holds it, and containment ends the tree at turn end. Run codex only under a
  throwaway `CODEX_HOME` and `HOME` with the mock and FAKE keys. Never the owner's `~/.codex`.
- **The pin.** A second vendor program now runs, so it is pinned per platform and refused in words.
- **Refuse, never guess.**
- **Evidence.** Attempt 4's turns stay as they ran.

The record (the manager's) labels attempt 4's turns: "the seat had no working tool: code-mode host disabled by the app (ca235dcc)". They are never counted as the model's behaviour.
Conflicts: `RuntimeManifest.cs`, `SeatProfile.cs` and `BrokerSeat.cs` are lane A's. `TurnMeter.cs` is shared with seat B's units (U-billing-classes is landed; check U-org-verbs-* at your rebase).
NOT in scope: the broker and cell seats' gpt-5.5 metadata, which differs between the dry run (bundled) and the live account (fallback), report § 5. That is a sibling CARD due before M-org1.
After landing (NOT this unit; on the owner's word only): one real research turn on gpt-6-luna in a fresh home reads PLAN.md and makes one served `trade` call (a tool_call row). Then
`ps` shows no codex-code-mode-host left after the turn, and codex's warning is absent.
Gate and report per `fleet/BUILDER-PROMPT-v2.md`. `## Report` ≤ 20 lines appended here.

## Report
Code tip 558c7a13 (on main 9baef74b). The report commit follows it. CI 39878214796: 12/12 green on windows ×5, macos, ubuntu (Cell step included), linux-host and package. Run 39871022284 had been red on HomeFilesGuardTests, which is now fixed by 558c7a13. Its linux-host red, two loopback listeners in the app, did not recur, and its cause is NOT VERIFIED.
Gate: `dotnet build -c Release --no-incremental` gives 0 Warning(s), 0 Error(s). The six touched unit classes ran 3× in Release and passed 58/58 each time. CliCodeModeHostTests ran 3× on this Mac and passed 2/2, against the pinned codex under a throwaway home with the mock and no key. `names.sh`: removed: 0 (sets base 3201, tip 3214). Tests box: NOT RUN — a run is in progress (20261023-004830-f03b91a0); wait for it.
1 done: the `--disable code_mode_host` pair moved off the CLI class onto the broker and cell classes explicitly. `code_mode_host` is now on the CLI allowlist and excluded from the broker and cell allowlists. The CLI-class copies in BrokerSeatTests were updated, and the commit says why.
2 done: the `codex-code-mode-host` pin is a data field (`InstallPlan.Companions`), looked up in `../codex-resources` and then beside the real path. VerifyAsync refuses a missing host or a different one in words, naming both hashes. The CI codex fetch checks the host too. The guard's reviewed list gained 4 reviewed calls on the program's own folder, each with its reason.
3a done: the CLI class gets a one-call loopback dry run (`CodexToolProbe`, through `ISeatProbe.ToolsAsync`, which has no default). A pass is cached by binary, template and model. Deviation: the dry run's output is the token `text("probe-"+"<nonce>")` / `echo probe-<nonce>` rather than `probe`, so the output must come from a call that ran.
3b done: an `error` item no longer counts as a command. Vendor warnings go to `engineering_log` and an Activity line, through the meter. The `ToolsUnavailablePattern` data match refuses that seat in words until its profile or model changes, and gives the turn's wakes back. 4 done: the doctrine is corrected in BrokerSeat.cs and ORCHESTRATION.md.
RED at base, all quoted from the runs: (A) "the CLI class still disables codex's code-mode host". (B) on this Mac, pinned codex 0.160.1: "the code-mode exec did not run: its output was code-mode host is disabled; exit 0". (C) TurnContext.Read CommandItems "Expected: 0 Actual: 1", plus the session's sentence "Filter not matched". (D) "a CLI seat with no codex-code-mode-host beside codex was admitted".
Mutants, each watched red and then restored: pair put back on the CLI class makes (A) and both (B) rows red, the latter with "answered "code-mode host is disabled"". Pair dropped from the broker makes BrokerSeatTests.A_broker_seat_starts… red with "argv lacks '--disable code_mode_host'". Pair dropped from the cell makes (A) red with "the cell class lost --disable code_mode_host". The wake give-back line removed makes A_turn_whose_tools_could_not_run_gives_back_its_wakes red.
Host hashes, sha256 of bin/codex-code-mode-host from the vendor's rust-v0.160.1 codex-package archives. Each archive's digest equals GitHub's, and each bin/codex equals its pin. They were hashed in scratch and nothing was run. win-x64 b905b516…ba78d (also equal to GitHub's digest of the standalone .exe asset), linux-x64 b33e8a52…14e1, linux-arm64 fbccde22…fd8, mac-arm64 6ac432b1…236b2f, mac-x64 345b92c2…00d2b2.
Proven on Linux by CI 39878214796 (ubuntu, Cell step): (B) and the product dry run both passed. On macOS they passed on this Mac.
NOT VERIFIED: Windows companion resolution against a real codex.exe install. The dry run's exec_command branch against a real classic-tools model. An end-to-end loop look refused after a dead turn (the CheckSeat refusal is only proven in a unit test). The real-model turn after landing is the owner's word, not mine.
Also NOT VERIFIED: macOS x64 and Linux arm64 hashes were measured but never run. To find the Mac's pinned codex, I listed and hashed the two binaries in the owner's ~/.codex/packages release folder (equal to the pins). No config or credential there was opened.
Codex review: fleet/tmp/A-org/u-cli-code-mode/U-cli-code-mode-review.md, written 02:30. Run 1's review is in -review-run1.md. Neither was acted on.
