# R29 — An adversarial audit of the fast lane (2026-11-04)

**What it is.** A findings-only audit of the 144 commits `997365c4..2a4a7edc` (29 units, 2026-10-30 to 11-02, R25) against `CLAUDE.md`'s "The safety
rules that outrank making it compile" and `docs/PRINCIPLES.md` "The boundary remains enforceable", with R24 and R25 D1–D13 as what was meant to hold.
Run from a cloud session on branch `claude/r29-fast-lane-audit`, starting at main `92204b30`. No product code changed. Each area was read by a separate audit leg; the findings marked VERIFIED were reproduced in a scratch
worktree (removed, nothing committed) and the output is quoted; READ-ONLY means read and not run. Windows code could only be read on this Linux host.

## 0. Method
- Toolchain: .NET SDK 10.0.114 (distro package; the dotnet-install host was refused by the session's proxy). `dotnet build TradeAgent.sln` at
  `92204b30`: 0 warnings, 0 errors.
- Areas: (A) hires, wakes and budgets (D3, D5, D13, wake-once, seat models); (B) evidence (D6, D7, D11, D12, α-wealth, holdout, inconclusive path,
  backtest parity, lineage); (C) owner authority and data-vs-instruction (D4, D9, material-read, vendor_said, pages, updater, Start fresh, R24);
  (D) containment (Windows cells and proof, D2 egress, Landlock, kit socket, home-on-hire); (E) proving ground, invariant registry, canon, vacuous tests.
- Public-repo scan of `git diff 997365c4 2a4a7edc`: no key, token, e-mail or IP beyond loopback and 8.8.8.8 test constants; home paths are placeholders
  (`/home/o`, `C:\Users\Owner`); `tools/linux-ui.sh` takes host, user and key from an untracked env file.

## 1. Verdict

- **No path was found by which an agent moves money, reaches live mode, widens a limit or its own envelope, or touches the kill switch, credentials,
  updates, the veto or approvals.** Every owner-authority entry point the range added (wake-once, seat model, Start fresh, the veto preview) is
  in-process only; D3 hires stay in the caller's subtree and pass the activation gate; D6 verdicts stay inside the campaign's budget; D9 stops are
  paper-only and subtree-only. Containment and the proving ground came out with no CRITICAL or HIGH finding.
- **The two HIGH findings are about evidence and honesty, not money:** the forward holdout D7 promises is served live through `quote` and paper
  results (F-B1), and the invariant registry calls protections "enforced by code" on test names that can be neutered (F-E1).
- **Main's red is a real integration break, not noise:** D11 (three deployments per pair) landed without D12 (12-month holdout), so the losing
  version in the e2e test is put on paper through the inconclusive quota (F-B2). D12 and D13 are records only; neither is code.
- **Two bounds D5 and R24 state are not where the wakes go** (F-A1 hire/retire cycling, F-A2 the cell's wake file); spend is still bounded by the
  envelopes and day-total.

| # | Sev. | Finding | Introduced | Status |
|---|---|---|---|---|
| F-B1 | HIGH | Forward holdout served live via quote/executions/P&L | f8e45096, 5eeb6612 | VERIFIED |
| F-E1 | HIGH | Registry "proven" on test names; skipped/emptied tests count | d998aac7, 3afb50c2 | VERIFIED |
| F-B2 | MEDIUM | D11 without D12: losers reach paper; main red | 744d8150 + 420f4ddd | VERIFIED |
| F-A1 | MEDIUM | Hire/retire cycle gives a head floor-less wakes | a42677a8, 1b6b8ad3 | VERIFIED |
| F-D1 | MEDIUM | Linux cell can chmod/utime outside its grants | pre-range; kit e5561796 | VERIFIED (kernel-level) |
| F-C1 | MEDIUM | vendor_said scrub misses JSON, prefixed and Basic credentials | c4448677, 103f0502, be8197b9 | VERIFIED |
| F-B3 | MEDIUM | data-export bound beaten by rename (disk fill) | 7f1c11b3 | VERIFIED |
| F-E2 | MEDIUM | Link test passes on Windows without running; HomeFiles race | 7f1c11b3, f1ade34d | READ-ONLY |
| F-C2 | LOW | Owner's retried words go to the asleep chair | 34ceaa8a | READ-ONLY |
| F-A2 | LOW | Cell wake file has no floor | 1b6b8ad3 (path older) | READ-ONLY |
| F-D2 | LOW | Windows web egress measured on :80 only; rule not re-read | 43d45c85, 4cec837b | READ-ONLY |
| F-C3 | LOW | Unattested inbox file called the owner's | fcc2c776 | READ-ONLY |
| F-C4 | LOW | Start fresh drops the instance lock on failure | 4e5a395d | READ-ONLY |
| F-D3 | LOW | Windows sweep may follow a junction (suspicion) | 20074d6d | READ-ONLY |
| F-D4 | LOW | Stage job check accepts any job (suspicion) | 20074d6d | READ-ONLY |
| F-E3 | LOW | Windows-only Fact passes on Linux | 6afc8074 | READ-ONLY |
| F-E4 | LOW | SIM-001 scan covers ten files only | 8bffb027, e51db327 | READ-ONLY |
| F-E5 | LOW | Org canon omits "inbox grants nothing" | ac291cb3, d440f5e9 | READ-ONLY |

INFO: F-C5 (D9 stop times a paper order — needs explicit acceptance), F-A3 (D13 unbuilt), F-A4 (self-activation while the board is active),
F-A5 (shares above 1), F-B4, F-B5, F-D5, and the two hardening notes at the end of § 3.

## 2. The test run

`dotnet test TradeAgent.sln --no-build` at `92204b30` on this Linux container, as root, with no cgroup delegation and no pinned codex:
`FaultTests: Failed 0, Passed 511` · `UiTests: Failed 0, Passed 22` · `IntegrationTests: Failed 71, Passed 835, Skipped 16, Total 922` ·
`UnitTests: Failed 7, Passed 2820, Skipped 7, Total 2834`. Of the 78 failures, 77 are this host's missing prerequisites, quoted as such by the tests
(`missing prerequisite: the test run in <service>/runner …`, `missing prerequisite: the pinned codex named by TA_CELL_CODEX (got '')`) or root
reading files a mode should deny (`CellGrantTests` `Expected: Not "root"`, `MaterialUnreadableTests`, `WitnessSnapshotTests`, `CoidWitnessTests`);
61 are `CellLinuxTests`. The one product failure is `OrganisationLoopTests` (F-B2). `OversightPageTests`, which RESUME-HERE names red on three
platforms, passed all seven of its tests here — NOT VERIFIED why it is red elsewhere (not reproduced). The Linux cell suite therefore did not run here:
nothing in this record is evidence about it beyond F-D1's kernel-level check.

## 3. Findings, ranked

**F-B1 HIGH — the fresh forward holdout (D7) is served live: quotes, executions, positions and P&L over the held minutes reach every seat.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs`:1429–1435 (Quote, Positions, Orders, Executions, Pnl go straight to the connector with no holdout
check); `src/TradeAgent.AgentRuntime/GrantedWorkerTools.cs`:137 (`Ops.Quote`, `Ops.Executions` on every role's trade tool);
`src/TradeAgent.Core/Data/TapeHoldout.cs`:168 (the paper runner exempt from forward windows). A spent campaign's successor is judged on "every bar
from {cutoff} on, which no caller on this channel is served until then" (`src/TradeAgent.Core/Db/CampaignRenewal.cs`:269; also :39, :104). But the
forward window is the present: while it holds `[cutoff, ∞)` any research or executor seat can poll `trade quote BTCUSDT` each minute and rebuild the
very minutes its successor's verdicts are scored on, and a deployed version's author receives fills and P&L over them and can submit a variant judged
on the same months. D7's "data no prior verdict or research could read" is false through the app's own channel, and the sentence above breaks the
honesty rule. (Executors' web egress is a second leak code cannot close; the app should not serve one itself.) The dataset holdouts lie in the past and
are not affected. Introduced f8e45096, refined 5eeb6612. Fix: while any forward window holds, refuse `quote` (and executions/P&L detail) to non-
operations pipe roles or answer a timestamp only, and hold paper fill and P&L notes from research seats until the successor opens; at minimum correct
the words and CONTRACTS to say what is served. VERIFIED (scratch test, `campaign_forward_holdout:999` armed from 01:00Z, calls as Research over the
pipe): `data-bars inside fresh holdout: ok=False code=HOLDOUT_WITHHELD …` · `quote during fresh holdout: ok=True
data={"Symbol":"BTCUSDT","Bid":120.15,"Ask":120.65,"Last":120.4,…}` · `executions during fresh holdout: ok=True`. The dispatch lines were re-read by
the record's author.

**F-E1 HIGH — the invariant registry calls a protection proven on test names alone.** `src/TradeAgent.Core/InvariantRegistry.cs`:70–80 (`Shown`)
counts `Tests.Count`; the conformance test `tests/TradeAgent.UnitTests/InvariantRegistryTests.cs`:24–29, 211–233 checks only that each named
`Class.Method` exists with a Fact/Theory attribute. A named test that returns early, carries `Skip`, or is a platform-skipped `[UnixFact]` still backs
"enforced by code" on the Oversight page. Already true today: CRED-001's cell side rests on `CellPolicyTests.*` (`[UnixFact]`, skipped on Windows).
Introduced d998aac7 / 3afb50c2. Fix: refuse a named method with `Skip` or a platform-skip attribute; better, bind the claim to a recorded test run (CI
or tests-box trx) on that platform and show anything else advisory. VERIFIED (scratch worktree): an early `return` in
`BoardActTests.No_op_verb_or_board_act_reaches_the_owners_veto_composition_or_budget` and `[Fact(Skip=…)]` on
`OrgStructureTests.No_pipe_op_or_door_verb_reaches_veto_or_activation` (both AUTH-001's proofs) left
`Every_test_the_registry_names_is_a_test_in_the_tree_under_that_class` and `A_class_no_named_test_backs_is_shown_advisory_and_says_why` Passed
(`Total tests: 9  Passed: 8  Skipped: 1`).

**F-B2 MEDIUM — main is red because D11 landed without D12: every losing verdict on the default 3-month holdout now reaches paper.**
`src/TradeAgent.Core/Data/EvidenceDefaults.cs`:25 (`HoldoutMonths = 3`; R25 D12's 36-month download holding back 12 is recorded by 420f4ddd, a
docs-only commit, and built nowhere), `src/TradeAgent.Core/Strategy/Inconclusive.cs`:52, 69 (`Slots(3) = 1`; `NotProfitable` under 365 days is
`loss-over-a-short-holdout`), `src/TradeAgent.Gateway/TradingGateway.cs`:942–1000 (`AllocateInconclusiveDue`). With 744d8150 giving each pair three
deployments, the inconclusive quota opens one slot per pair, and on a 91-day holdout every `NotProfitable` refusal qualifies — so the version that
lost on its held-back months is put on paper for 90 days, charged one trial. The contract permits this path, but D11 was amended to open it on the
premise that D12's 12-month holdout would make losses conclusive; without D12 the slot goes to the oldest/highest-ranked loser, and R27's "every
verdict under the 12-and-3 default inconclusive" means the ADDIS test never rejects either. The fast lane's own e2e test says the loser "allocates
nothing". Introduced by the pairing of 744d8150 (D11 built) with 420f4ddd (D12 recorded, unbuilt); found by U-loop-e2e's test after 2a4a7edc.
Fix: build D12 (and say in RESUME-HERE that it is unbuilt), or hold the envelope at fewer than three deployments until it is; then decide in the test
whether a short-holdout loser on paper is the intended outcome and assert that. VERIFIED (full suite on this host, § 2):
`OrganisationLoopTests.The_organisation_loop_runs_end_to_end_through_a_restart_and_a_losing_verdict_allocates_nothing` fails at
`OrganisationLoopTests.cs`:292 `Assert.Single() Failure: The collection contained 2 items`, the second `PolicyVersion =
allocation-policy-inconclusive-v1, Mode = PAPER, … Promotion = PromotionStanding { State = refused … Verdict = refused`. The 12-month span itself has
no off-by-one: `CampaignStore.Close` is the last bar's open plus one bar (:1383–1392).

**F-A1 MEDIUM — a head wakes itself back-to-back, past the board's floor, by hiring and retiring in a cycle (D3).**
`src/TradeAgent.Core/Db/OrgStore.Verbs.cs`:348 (`WakeActivated(id, r.Caller, …)`) and :304 (`if (inUnit.Count == 0) route = "first";`). A head
runs `org-hire` into a vacant child unit; the hire self-activates and raises an immediate `activated` wake to the hire and one to the hirer; the head
runs `org-retire`; the unit is empty again, so the next hire passes the earning test as a "first" hire with no evidence. Every cycle hands the head an
immediate wake that never meets the board's floor (60 min at team level) its own `org-wake` is held to, plus a fresh id and home per cycle. Only the
unit envelopes and the day-total bound the spend — so this widens no money, but it voids the board's rewake policy (R24) and the earning rule. Also,
`SelfActivationRefusal` prices one turn while each activation commits two wakes. Introduced a42677a8; 1b6b8ad3's id keying makes every re-activation
wake again. Fix: no `activated` wake to a hirer who is the verb's caller (the reply already tells it), or hold it to `LastWoken + policy.Floor` as
`RequestWake` does; count ended positions in the "first" route; price the two wakes. VERIFIED (scratch test in `OrgAutonomyTests`, broker seats, one
head launch): `ORG-WAKE 1 min: ok=False … the security board's floor`; `CYCLE 0: hired pos-… status=active; retire ok=True` … `CYCLE 4: … retire
ok=True`; `ACTIVATED WAKES TO THE HEAD: 5, due 23:22:43,23:22:43,…`; `ALL ACTIVATED WAKES: 10`; `Passed: 1`.

**F-D1 MEDIUM — a Linux cell changes modes and times of any file the owner's uid owns, outside its grants; the kit (in range) leans on it.**
`src/TradeAgent.Core/CellNative.cs`:77–80 (the seccomp deny list is io_uring, keyctl, add_key, request_key, bpf, perf_event_open, userfaultfd: no
chmod, fchmod, fchmodat, fchmodat2, utimes, utimensat) and :37 (Landlock's handled rights carry no attribute change; Landlock does not mediate chmod or
utime). A cell running as the owner's uid can `chmod 000` the home's database or the app's files (the supervisor and trusted state denied service),
`chmod 0666` them, loosen `~/.ssh`, or flip a run bit in the kit, whose hash covers run bits — so one cell withdraws the kit from every other cell.
No read or write is gained. Partly known: `tests/TradeAgent.IntegrationTests/CellLinuxTests.Kit.cs`:72 says "chmod is not a right Landlock handles …
said, not asserted"; `RouteA.AssuranceRows` (`src/TradeAgent.AgentRuntime/RouteA.cs`:270–305) names no such GAP. Introduced before the range (the
Linux cell, 1b4d7bdd); e5561796 / b58835cc depend on it. Fix: EPERM from seccomp for the chmod and utime family (fchmod on an owned fd at most), or
at least a GAP assurance row "attribute changes outside grants". VERIFIED at kernel level with an equivalent ruleset (Landlock ABI 7, handled rights
0xFFFF, the kit granted read-only): `chmod OK …/kit/tool 0o100644` · `chmod OK …/outside/s 0o100666` · `read outside DENIED [Errno 13]` · `write kit
DENIED [Errno 13]` — not run through the product's own cell stage (this host's cgroup lacks the delegation CI's Cell step gives, § 2). The deny list
was re-read by the record's author.

**F-C1 MEDIUM — vendor_said's credential scrub lets common shapes through.** `src/TradeAgent.Core/Db/LaunchEvidence.cs`:51 (`Named`), :54
(`Bearer`), :57 (`Key`), called from `src/TradeAgent.AgentRuntime/TurnMeter.cs`:1419. In JSON the key name's closing quote satisfies the separator
alternative `["'`]`, the value class `[^\s"',;]+` then eats only the `:` and stops at the value's opening quote; `\b` fails after `_` or a camelCase
prefix; `Basic` is not withheld; `sk_`/`rk_` with an underscore and short values miss `Key`. A vendor error line echoing a short key lands in the
ledger in clear. Introduced c4448677, 103f0502, be8197b9. Fix: let the naming word end in a closing quote then `\s*[:=]\s*` and an optional opening
quote; allow `[_A-Za-z]*` prefixes (`access_token`, `client_secret`, `sessionToken`); treat `Basic` like `Bearer`; add `sk_`/`rk_`, `pwd`.
VERIFIED (scratch test, 10 inputs, `Failed: 8, Passed: 2`): `{"api_key":"Zq81mP4xLw0"}` → `{"api_key"[withheld]"Zq81mP4xLw0"}`;
`access_token=…`, `client_secret=…`, `sessionToken=…`, `pwd=…`, `sk_live_…` kept verbatim; `Authorization: Basic QUJDREVGR0g` →
`Authorization: [withheld] QUJDREVGR0g`. The regex was also re-read by the record's author.

**F-B3 MEDIUM — data-export's folder bound is beaten by a rename, so a position can fill the disk.** `src/TradeAgent.Gateway/GatewayPipeServer.cs`
:3517 with `Exports()` below it counts only `data/export-*` names; code in a cell renames each export (or moves it into a subfolder) and asks again,
up to 200,000 bars (≈ 12 MB) per file, against `DataExports`' "a position cannot fill the machine's disk by asking". Introduced 7f1c11b3. Fix: bound
the bytes under the position's home (or `data/`), or exports per position per day from the `tool_call` ledger, which the agent cannot write.
VERIFIED (scratch test): `successful exports: 10, files in data folder: 10, MaxFiles=4`.

**F-E2 MEDIUM — a link-following test on a cell-writable folder passes on Windows without running, now that cells run there.**
`tests/TradeAgent.UnitTests/DataExportTests.cs` `A_link_planted_at_the_name_is_never_followed` is a plain `[Fact]` that opens with
`if (OperatingSystem.IsWindows()) return;`. data-export (`src/TradeAgent.Gateway/GatewayPipeServer.cs`:3521) writes through `HomeFiles.Write` into
the position's home; on Windows `HomeFiles` (`src/TradeAgent.Core/HomeFiles.cs`:48, 579–594) checks components for reparse points and opens in a
separate step, excused in its own doc by "Windows — where no cell runs", which f1ade34d/ddb51f42 made false. Suspicion, not established: a junction
swapped in between check and open makes the gateway write its (fixed-name, fixed-content) CSV outside the cell. Introduced 7f1c11b3 (test), f1ade34d
(premise). Fix: `[UnixFact]` plus a Windows junction test; close the race (open by handle without reparse, check the final path) or correct the doc.
READ-ONLY.

**F-C2 LOW–MEDIUM — the owner's words are retried to the asleep chair, not the active chief (D4 delivery).**
`src/TradeAgent.AgentRuntime/MissionLoop.cs`:4632 (`RetryOnce`) raises the retry to `CouncilRoles.Operations`; called from :4595, :4666, :4735. While
a chief is active the gate keeps the legacy pair asleep, so a failed or lost chief turn strands the owner's message his chat said was "Sent to the
chief". Not a permission bypass. Introduced 34ceaa8a (added `OwnerRecipient()` at `MissionEventStore.cs`:793, not used here). Fix: raise the retry
to `events.OwnerRecipient()` or to the original event's role. READ-ONLY (code re-read by the record's author).

**F-A2 LOW — a cell's wake file is held to no floor (D5 on the path cells actually use).** `src/TradeAgent.AgentRuntime/MissionLoop.cs`:4769–4773
(`AskedForDelay`) clamps only to `MaxDelay` (30 min); `{"AfterSeconds":1}` in `.tradeagent/next.json` each turn gives a `self` wake a second later,
past the board's executor floor (15 min). 1b6b8ad3 refuses `org-wake` to cells because "a cell asks with its wake file", so the floor and ceiling D5
states are never applied to cells. Spend still bounded by envelope and day-total. Introduced: the wake file predates the range; 1b6b8ad3 made it the
cells' only path. Fix: clamp to at least `RewakeFloors.Of(policy).Floor(level)` for every org position. READ-ONLY (code re-read by the record's author).

**F-D2 LOW — Windows `web` egress is measured on port 80 only and the firewall rule is not re-read per launch.**
`src/TradeAgent.AgentRuntime/WindowsCellBenchmark.cs`:279–288 (`web-nothing-else` probes github.com:80), `RouteA.cs`:236. `internetClient` opens every
port; a rule blocking 80 but not other TCP ports or UDP still yields a proof naming `web`, and a rule removed after the proof leaves web cells every
port until reboot; nothing in the code creates the rule (D2's helper). Exposure today none: ddb51f42 shows web holding nowhere, cells run at `none`.
Introduced 43d45c85, 4cec837b. Fix: probe another TCP port and a UDP send; re-read the rule by SID and port at admission before choosing `web`.
READ-ONLY (Windows).

**F-C3 LOW — material-read calls an unattested inbox file "THE ACCOUNT OWNER'S MATERIAL".** `src/TradeAgent.Gateway/GatewayPipeServer.cs`:4372–4373
serves `InboxUnattested` rows; the reply's `what_this_is` (~:4427) uses the owner's phrase for both origins, while the app's own words for that origin
are "TradeAgent cannot say who put it there" (`src/TradeAgent.App/InboxView.cs`:292). Still framed as data and granting nothing; attribution only.
Introduced fcc2c776. Fix: the unattested origin's own sentence. READ-ONLY.

**F-C4 LOW — Start fresh drops the single-instance lock before the move and never retakes it on failure.** `src/TradeAgent.App/AppHost.cs`
`StartFreshAsync` disposes `_lock` before `OlderHome.SetAside`; a refused move leaves the refusal screen up with no lock, so a second instance can
open the home. Only `InvalidOperationException` is caught: a `SqliteException` from `RefusedSchema` (`src/TradeAgent.Core/OlderHome.cs`:58) or an IO
error from :100 escapes the press handler. Introduced 4e5a395d. Fix: retake the lock on failure; catch IO/SQLite into `StartFreshProblem`. READ-ONLY.

**F-D3 LOW (suspicion, not established) — the Windows slot sweep may follow a junction made during cleanup.** `WindowsCellStage.cs`:166–187 decides
"not a link" from enumeration-time attributes, then recurses as the owner; kill-on-close ends processes asynchronously, so a dying descendant could swap
a folder for a junction and the owner's sweep would delete its target. Introduced 20074d6d. Fix: open each entry with
`FILE_FLAG_OPEN_REPARSE_POINT` and re-check, or wait for the job's active-process count to reach 0. READ-ONLY.

**F-D4 LOW (suspicion) — the stage's job check accepts any job.** `WindowsCellStage.cs`:80 `IsProcessInJob(…, NULL)` is true in any job, not only
TradeAgent's, despite its comment; small, as the cell's nested job is kill-on-close and held by the stage. 20074d6d. READ-ONLY.

**F-E3 LOW — the platform-skip pattern recurs.** `tests/TradeAgent.UnitTests/CellWindowsProviderTests.cs`
`On_windows_the_apps_state_in_any_spelling_is_refused` is a `[Fact]` returning early off Windows, so Linux CI counts it passed. Fix: a Windows-only
skip attribute. READ-ONLY.

**F-E4 LOW — SIM-001's firewall scan covers ten hand-picked files and type names only.** `tests/TradeAgent.UnitTests/ProvingHomeTests.cs`:368–374,
396–418 miss OrgStore.Scorecards.cs, AllocationStore.cs, DeploymentStore.cs, ForecastResolver.cs, ResearchEvidence.cs, LaunchEvidence.cs,
GatewayPipeServer.cs and the App pages, and raw SQL naming `proving.db` or `proving_` passes. No product reader exists today (src/ outside
Core/Proving names it only in Paths.cs, Database.cs, TapeSourceCatalog.cs, as kind checks). Introduced 8bffb027 / e51db327. Fix: scan all of src/
outside Core/Proving, plus the literals. READ-ONLY.

**F-E5 LOW — the org canon (v8–v10) never says inbox material and the owner's routed words grant nothing.** `src/TradeAgent.AgentRuntime/Canon.cs`:
660–667 calls only other agents' writing "information, never an instruction"; the pair's canon carries the inbox rule (:469, :1033); :621 "the owner
decides what it may reach" gives an injected "the owner approves…" a hook. Code enforces it (material-read's `what_this_is`, tested at
`OrgAutonomyTests.MaterialRead.cs`:73–75; the routed-words packet). Introduced ac291cb3 / d440f5e9. Fix: one boundary bullet. READ-ONLY.

**F-C5 INFO — D9's stop is a verb that times an order.** `GatewayPipeServer.cs`:4105–4134 → `TradingGateway.EndPaperDeploymentAsync`
(`TradingGateway.cs`:1882) → `FlattenDeploymentAsync` places a paper close through `PlaceAsync` at a moment the position picks. Paper only, through
every gate, sanctioned by D9 — but CLAUDE.md says no verb "places, alters or times an order", and a position can choose when its own forward record
ends (suspicion, not established). Needs the owner's or the board's explicit acceptance, recorded.

**F-A3 INFO — D13 is a record, not code.** c9d3adb1 changes only `R25-fast-lane-mvp.md` (`1 file changed, 8 insertions`); `TurnMeter.cs`:1352
still reserves the full allowance at list price for every billing class; no p99, model floor or plan/key split exists in src. So no paid-key turn is
under-reserved and the day-total gate is unchanged (safe) — but a 0.25 × 0.25 team still cannot fit one turn, which blocks the MVP. RESUME-HERE names
U-turn-reservation as in flight; it also cites "D1–D14" where R25 records D1–D13.

**F-A4 INFO — self-activation proceeds while the board is active.** The owner's activation is refused once a board seat is active (activation is
then the board's act, R24); a hire's self-activation (OrgStore.Verbs.cs Hire) is not. R25 D3 says "the board's powers stand", so this is a ruling to
confirm, not a defect established.

**F-A5 INFO — an idle legacy division counting 0 lets declared shares sum above 1.** `src/TradeAgent.Gateway/GatewayPipeServer.Structure.cs`:389
(`Asleep`, ea019732/e5dadccb). Every launch is still admitted along its whole chain to the root's day-total, so no unit draws past its envelope:
overcommitment, not new money.

**F-B4 INFO — the lineage trigger checks presence, not provenance.** `src/TradeAgent.Core/Db/ArtifactLineage.cs`:162–175 accepts any non-blank
attempt string. Not reachable by an agent: author and attempt come only from the launch grant (`GatewayPipeServer.cs`:864, 912, 4494, 4510) and no
write op takes either. A backstop against an in-process writer bug only. e53ab493. Fix: `EXISTS (SELECT 1 FROM ai_attempt WHERE id = NEW.attempt …)`.
READ-ONLY.

**F-B5 INFO — a small backtest/paper gap at exit.** `Backtest.cs` (`restsUntil = Placed(learnt)`, 79c49b62) stops protection resting from X+1
once an exit is decided at X; on the paper venue a stop can still fire during X+1 before its cancel lands. Not look-ahead (fills at the first open
≥ X+2). READ-ONLY.

**F-D5 INFO — the kit's "no link" check does not cover hardlinks.** `Kit.cs`:438–462: a source folder holding a hardlink to a secret installs its
bytes into the kit every cell reads. No power gained (making the hardlink already needs the owner's uid). Fix if wanted: refuse `st_nlink > 1`.
VERIFIED: `hardlink: INSTALLED; kit/tools/support/key = PRIVATE KEY`.

**INFO.** `InternalsVisibleTo("TradeAgent.ProvingHost")` is unsigned in Core, App and AgentRuntime (as the UnitTests entries are); needs in-process
code, not reachable from a seat (e51db327). `ProvingStore` (`src/TradeAgent.Core/Proving/ProvingStore.cs`:80) opens any path without checking the
proving mark; its one caller passes a proving home — hardening only.

## 4. What came out clean (with what was read)

Evidence: D6 chief verdicts (`GatewayPipeServer.cs`:2970–3115) — the root head only, an author in its subtree, measured on the author's own
completed run, charged idempotently per (version, campaign) with budget and shares in one write (`CampaignStore.cs`:1470–1514), the reply the same
fixed record (`VerdictReply`, :3223). data-export's holdout and paths (:3460–3545): `DatasetReader.Read` with the pipe holdout, forward windows
included; pair names `[A-Z0-9]{2,20}`; `HomeFiles.Write` with O_NOFOLLOW per component on Linux; whole-month or UTC-midnight cutoffs. Renewal
(`CampaignRenewal.cs`:110–250): cutoff strictly after now and every close, ≥ 30 fresh days, one transaction. Per-pair breadth: ordinal pairs, an
unknown pair counts as every pair, agents cannot create datasets (`CampaignStore.cs`:548–628, 822–860, 1280–1300). The inconclusive cap is exactly 1
at three deployments (`held.Count >= slots`, `AllocationStore.cs`:640–680) plus one per lineage. One envelope per pair per account inside the insert
(77acbe6d). The veto fails closed when the pair's terms moved or cannot be read (2249434b, `TradingGateway.cs`:675–693). Backtest parity: no
look-ahead, fills only at `OpenTime >= learnt+2`; d3563f8e's re-pins keep or tighten their assertions. No commit in range touches `material` or
`material_note`. No α constant changed.

Kit: zip-slip and links refused, VERIFIED against the real `Kit.Preview` (`../evil`, `tools/../../evil`,
`/tmp/evil`, `..\evil`, a symlink entry, a directory symlink in a folder source; install from inside the home refused, `Kit.cs`:401–408, 465–489);
press two re-stages and requires the first press's hash (`Kit.cs`:241–243, `AppHost.cs`:2067); every grant re-hashes every byte (`Kit.cs`:177);
never granted writable (`Cell.cs`:303–309); kit tools last on PATH (`RouteA.cs`:106). AppContainer job: KILL_ON_JOB_CLOSE without BREAKAWAY_OK or
SILENT_BREAKAWAY_OK, created suspended and assigned before resume, exactly three inherited handles (`AppContainerCell.cs`:188–205, 315–331); grants
recorded before granted, swept, a failed revoke refuses (`WindowsCellStage.cs`:124–163, 220–234); Windows proofs bound to BootId, machine, release,
build and profile, written only when every core row held (`CellProofStore.cs`:72–80, 186–190; `WindowsCellBenchmark.cs`:310–319) — all read only.
Linux: no commit in range changes Cell.cs, CellNative.cs or CellProofStore.cs beyond the kit's read-only grant; self-activation passes `Decide`'s
ProvenCell check (`PositionActivation.cs`:344). Home on hire: app-minted `[a-z0-9-]` ids, 48 random bits, `workspace/org/<id>`, links replaced
(`WorkspaceBuilder.cs`:95–103). Puppet torn-read touches tests only. `tools/linux-ui.sh` commits no host, user, display or path (it presses the
owner's GUI as the app's account; outside the product, policy-limited as its header says). Hire placement (`OrgRights.cs`:200–232): no hire into the root as a member, the root's head and anything under
div-operations are board acts, the caller must head the parent; board seats and the watcher are not org units. Activation gate
(`PositionActivation.cs`:369–377): broker or cell seats only, then `Decide` (proven cell, assurance, ceilings); a seat priced at nothing is refused;
unreadable facts leave the hire dormant. f5f5a509's wake give-back charges nothing because it runs nothing. org-wake (`OrgStore.Autonomy.cs`): own
wake only, floor and ceiling from its last woken turn, one pending, no target argument. Wake-once (`OrgStore.ChiefWake.cs`): second press through
`WhatMoved`, unreachable from Gateway, TradeCli, AgentReach and GrantedWorkerTools. Seat models: only the owner's `ChooseSeatModel` (refused while the
board is active) or SeatChief write seat columns, the hire verb takes no model, `IsCatalogModel` limits to luna and sol. Campaign budgets key by
campaign, so a new hire resets none. D9 scope: live and non-paper runs refused (`PositionsPaperStopRefusal`, :4161), list filtered to the subtree's
PAPER runs (:4080), board, pair and app principals excluded (:1368), subtree from app-recorded `strategy_version.role` (`StrategyStore.cs`:309) and
`Org.SubtreeOf`; the stop re-reads the row inside its per-run gate. D4: owner-kind events raised only by `RecordOwnerMessage` (`AppHost.cs`:842) and
`RetryOnce`; no agent input parses a `MissionEventKind`. material-read writes only a tool_call row, opens the scanner's `RelPath` through
`HomeFiles.ReadAll`, re-hashes, ≤ 4 MiB, cells refused (:4361). Veto preview required (`BoardStore.cs`:780, `TradingGateway.Board.cs`:132). Owner
pages compose names from chart rows; agent text reaches no button or confirmation; the emergency strip stays above the scroll (fd5f9516). Updates:
channel from the installed bridge, the environment override removed (304c58e0); Start fresh is two-press, in-app only, one `Directory.Move`, deletes
nothing. R24 per-pair envelopes: an unnamed pair reads null, so granting one is a widening and takes two seats; foreign-instrument terms refused at
parse (`BoardIntent.cs`). Proving: ProvingHost referenced by no src project nor packaging (`packaging/build.ps1`:114–136); real and proving homes
refuse each other (`Database.cs`:26–97, `ProvingGround.cs`:56–71, 172–205); SIMULATED and `real_dollars = '0'` by CHECK
(`ProvingStore.cs`:100–135). request_user_input off in broker and cell argv (40792b90, asserted by `BrokerSeatTests`). Canon role parts claim no
authority they lack. Fast-lane tests read and not vacuous: door containment (a4610a25), D9 stop, material-read, inconclusive path
(`PaperAllocationGateTests.PerPair.cs`), CellProofTests (e9fa19a3); the golden re-pin d3563f8e came with an evaluator bump to `backtest=3` and a
stated list of moved vectors.

## 5. NOT VERIFIED

- Everything on Windows: the AppContainer stage, the job, the sweep, the egress rule and the proof (F-D2–F-D4 and the Windows half of § 4) were read
  only. F-E2's junction race is a suspicion.
- The Linux cell suite did not run here (§ 2); F-D1 was shown with an equivalent Landlock ruleset, not through the product's cell stage.
- Why `OversightPageTests` is red elsewhere: it passed here.
- Whether any seat can write into `workspace/inbox` (bearing on F-C3); whether D9's stop can be used to choose when a forward record ends (F-C5).
- Nothing was run against a real model, venue or the owner's hosts. The scratch tests are not committed.

*Written 2026-11-04 by a cloud audit session from `git log 997365c4..2a4a7edc` and main `92204b30`. It records findings only and marks nothing
fixed, no protection weakened or strengthened, and no decision taken.*
