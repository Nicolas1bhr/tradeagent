# R29 — An adversarial audit of the fast lane (2026-11-04)

**What it is.** A findings-only audit of the 144 commits `997365c4..2a4a7edc` (29 units, 2026-10-30 to 11-02, R25) against `CLAUDE.md`'s "The safety
rules that outrank making it compile" and `docs/PRINCIPLES.md` "The boundary remains enforceable", with R24 and R25 D1–D13 as what was meant to hold.
Run from a cloud session on branch `claude/r29-fast-lane-audit`, starting at main `92204b30`. No product code changed.

**Status: IN PROGRESS.** Areas C and E are recorded below; A, B and D follow in later commits on this branch.

## 0. Method
- Toolchain: .NET SDK 10.0.114 (distro package; the dotnet-install host was refused by the session's proxy). `dotnet build TradeAgent.sln` at
  `92204b30`: 0 warnings, 0 errors.
- Areas: (A) hires, wakes and budgets (D3, D5, D13, wake-once, seat models); (B) evidence (D6, D7, D11, D12, α-wealth, holdout, inconclusive path,
  backtest parity, lineage); (C) owner authority and data-vs-instruction (D4, D9, material-read, vendor_said, pages, updater, Start fresh, R24);
  (D) containment (Windows cells and proof, D2 egress, Landlock, kit socket, home-on-hire); (E) proving ground, invariant registry, canon, vacuous tests.
- Public-repo scan of `git diff 997365c4 2a4a7edc`: no key, token, e-mail or IP beyond loopback and 8.8.8.8 test constants; home paths are placeholders
  (`/home/o`, `C:\Users\Owner`); `tools/linux-ui.sh` takes host, user and key from an untracked env file.

## 1. Findings so far (interim; ranked in the final commit)

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

**F-C1 MEDIUM — vendor_said's credential scrub lets common shapes through.** `src/TradeAgent.Core/Db/LaunchEvidence.cs`:51 (`Named`), :54
(`Bearer`), :57 (`Key`), called from `src/TradeAgent.AgentRuntime/TurnMeter.cs`:1419. In JSON the key name's closing quote satisfies the separator
alternative `["'`]`, the value class `[^\s"',;]+` then eats only the `:` and stops at the value's opening quote; `\b` fails after `_` or a camelCase
prefix; `Basic` is not withheld; `sk_`/`rk_` with an underscore and short values miss `Key`. A vendor error line echoing a short key lands in the
ledger in clear. Introduced c4448677, 103f0502, be8197b9. Fix: let the naming word end in a closing quote then `\s*[:=]\s*` and an optional opening
quote; allow `[_A-Za-z]*` prefixes (`access_token`, `client_secret`, `sessionToken`); treat `Basic` like `Bearer`; add `sk_`/`rk_`, `pwd`.
VERIFIED (scratch test, 10 inputs, `Failed: 8, Passed: 2`): `{"api_key":"Zq81mP4xLw0"}` → `{"api_key"[withheld]"Zq81mP4xLw0"}`;
`access_token=…`, `client_secret=…`, `sessionToken=…`, `pwd=…`, `sk_live_…` kept verbatim; `Authorization: Basic dXNlcjpodW50ZXIy` →
`Authorization: [withheld] dXNlcjpodW50ZXIy`. The regex was also re-read by the record's author.

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

**F-C3 LOW — material-read calls an unattested inbox file "THE ACCOUNT OWNER'S MATERIAL".** `src/TradeAgent.Gateway/GatewayPipeServer.cs`:4372–4373
serves `InboxUnattested` rows; the reply's `what_this_is` (~:4427) uses the owner's phrase for both origins, while the app's own words for that origin
are "TradeAgent cannot say who put it there" (`src/TradeAgent.App/InboxView.cs`:292). Still framed as data and granting nothing; attribution only.
Introduced fcc2c776. Fix: the unattested origin's own sentence. READ-ONLY.

**F-C4 LOW — Start fresh drops the single-instance lock before the move and never retakes it on failure.** `src/TradeAgent.App/AppHost.cs`
`StartFreshAsync` disposes `_lock` before `OlderHome.SetAside`; a refused move leaves the refusal screen up with no lock, so a second instance can
open the home. Only `InvalidOperationException` is caught: a `SqliteException` from `RefusedSchema` (`src/TradeAgent.Core/OlderHome.cs`:58) or an IO
error from :100 escapes the press handler. Introduced 4e5a395d. Fix: retake the lock on failure; catch IO/SQLite into `StartFreshProblem`. READ-ONLY.

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

**INFO.** `InternalsVisibleTo("TradeAgent.ProvingHost")` is unsigned in Core, App and AgentRuntime (as the UnitTests entries are); needs in-process
code, not reachable from a seat (e51db327). `ProvingStore` (`src/TradeAgent.Core/Proving/ProvingStore.cs`:80) opens any path without checking the
proving mark; its one caller passes a proving home — hardening only.

**Clean so far (with what was read).** D9 scope: live and non-paper runs refused (`PositionsPaperStopRefusal`, :4161), list filtered to the subtree's
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

*Records findings only; marks nothing fixed.*
