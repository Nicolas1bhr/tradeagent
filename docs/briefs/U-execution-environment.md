**RE-CHECKED 2026-10-22 against main 7550906b** (rung 43, `U-cell-linux-c` landed; Canon.Version 5). Every pointer below is main's; grep again at dispatch if main moved.
**SEAT A RULINGS at dispatch (2026-10-22 ~07:00):** Staff stays `broker` with Heads (the orchestrator's 2026-10-21 decision, BOARD); item 0's test-host-only mock seam ACCEPTED (the `DeclaredSeatClass`/`TestPins` pattern: no product path sets it, a grep in the report); a Member with no proof measuring `web` is refused at launch (`RouteA.Admission`, before any model work — ORCHESTRATION § 2.3 fail-closed), never started on another class. The Codex review runs unless seat A says the observed-run window is open.
The survey's lines L1–L6 are all on main (L1 door `RouteATests.cs:129-130`; L2 `BrokerSeat.Model` "gpt-5.5" `BrokerSeat.cs:32` + the surface verifier; L3 row
`CellLinuxTests.Route.cs:93`; L4 `CellBenchmark.ProvedEgress` = none, web `CellBenchmark.cs:46`, refused at `RouteA.cs:158-160`; L5 the turn's cwd, which the app sets
to `HomeFor(role)` `AppHost.cs:363`; L6 `DeclaredSeatClass`, test host only, `RuntimeManifest.cs:455,465`) — none becomes an item 0 for that reason. Item 0 below exists
because -c's `AgentSession.StartInCell` has never run a turn end to end (-c's NOT VERIFIED): this unit's rows are its first, so they get a seam and a GUARD of their own.

# U-execution-environment — an executor position runs in a cell: a Member on the `cell` class, its own home as its workspace, egress `web` (TCP 443) by the owner's answer, a canon that names only what a cell reaches, and refused — never degraded — where no counted proof measured it
**The owner's answer (2026-10-21, ORGANISATION § 16, verbatim): "Web (443) for every executor"** — an executor's cell takes `web` (TCP 443 + DNS over TCP, any host,
`Cell.cs:386`); its loopback and LAN reach on 443 are GAP rows, stated on the proof (-c) and on every attempt (here). `none` stays a class a launch may name only where
a counted proof measured it; no executor names it in this unit.
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.8 ("a workspace (the position's home and the mandate's scratch)"; "a confined seat keeps its working freedom: shell, code,
packages, permitted network"), § 2.3 (code execution needs a confined cell; never a degraded start), § 5's doing-seat column; ORGANISATION :324 ("what gives an executor
both"), M-org1's "an executor's cell runs a tool it wrote with no admission step" (:537); § 15 row :448, seat A's order :459.
**Protections — CRED-001 (an executor never on `cli`; the login stays in the harness), Law 2 (L2 holds: code mode refused), AUTH-001 (no verb, pipe op, setting or
`runtimes.json` field picks a class or an egress), INBOX-001 / COUNCIL rule 7 (presence stays DEFAULT IN: the attempt's inbox/state are MEDIATED, -c), refuse-never-guess,
honesty (every class measured on its build and host; GAPs said), no terminal: red-first and ONE watched mutant.** Seat A; **opus/high** (the agent boundary).
**No rung:** `cell_assurance` already records `policy_hash` and `egress` per attempt (`Database.cs:2785-2799`, written `AgentSession.cs:1117-1119`), its rows take the GAP
class (`cell_assurance_row`), and `ai_attempt.seat_class`/`assurance` exist (rung 38, `Database.cs:2463-2474`). If the builder finds a column missing: the next free at dispatch.
**Depends (landed):** `U-cell-linux-c` (rung 43), `U-thinking-seats-c`, `-a` (the gate), `U-puppet-runtime`, O2a. Overlaps: B's `U-org-packets-b` (rung 44; `RuntimeManifest.cs`,
`AgentSession.cs`) — whichever lands second rebases; `U-org-lanes` reads the class; `U-org-watcher-b` follows this unit.
**Today (main 7550906b):** `SessionSeatClassFor(role)` (`RuntimeManifest.cs:463-467`) takes the ROLE ONLY — puppet → declared; test-host `DeclaredSeatClass`; beyond the
legacy pair → `broker`; else `cli` — and cannot see a position's kind; its readers are `AgentSession.SeatClass` (:312), `CliAgentRuntime.cs:980`, the app's declared class
(`AppHost.cs:434-440`, fed to the gate by `FactsFor` :400-407) and `WorkspaceBuilder.RuntimesOf` (:121-123: beyond the pair → [Broker, Harness]). The kind is derived
(`OrgPositionKind` `PositionActivation.cs:7-12`; `OrgStore.cs:377-379, :401-403`) and carried on `PositionStanding.Kind` (`PositionActivation.cs:20`). Code comes from a
shell seat (`CapabilitiesOf` `SeatAssurance.cs:138-144`, `RunsShell` :95 = cli|cell); the gate refuses a declared/verified mismatch (`PositionActivation.cs:179-182`), a
missing capability (:202-207) and a host with no proof (:209-213; `HoldsProof` = ANY provider's counted proof, `CellProofStore.cs:210-214`). Egress `none` is written
TWICE: the cell's policy (`RouteA.Policy`, `RouteA.cs:74-82`) and the admission check (`AgentSession.cs:1098`); the assurance's network row says "egress none"
(`RouteA.cs:198`). `RuntimeClass` is `{ Cli, Harness, Broker }` (`AgentReach.cs:14`); `Canon.Version = 5` (`Canon.cs:38`). The puppet cannot take the cell route:
`StartInCell` runs codex's exec-server from the seat's own codex (`RouteA.CodexRoot`, `RouteA.cs:61`); -c's Linux rows drive the pinned codex against
`tests/TradeAgent.IntegrationTests/CellRoute/mock_responses.py` by a hand-built launch (`CellLinuxTests.Route.cs:433-470`), never through `AgentSession`.
**Observable result:** a Member position beyond the legacy pair, on a runtime with a `cell` class and a host whose counted proof measured `web`, launches on `cell`: the
harness outside; every model-directed shell/file/patch action in a cell whose workspace is the position's own home (`workspace/org/<id>`, `Council.cs:90-94`); egress `web`;
its verbs through the door from the harness; a file it wrote in one attempt is there and runs in the next. Heads and Staff stay `broker`, the legacy pair `cli`. With no
counted proof, or one that did not measure `web`, the Member is refused in words and never started on `broker` or `cli`. The attempt records `seat_class` `cell`, a
`cell_assurance` row with egress `web`, and GAP rows for loopback-443 and LAN-443.
Items, one commit each, one-sentence messages:
0. **StartInCell end to end, first** (Linux, `Category=Cell`): a test-host-only seam (`[JsonIgnore] internal`, honoured only under `PuppetSeats.TestHost` — the
   `DeclaredSeatClass`/`TestPins` pattern; nothing in the product sets it) that points a session's codex at -c's mock provider; GUARD row (0)
   `A_cell_class_session_runs_a_turn_through_StartInCell_end_to_end` (a `DeclaredSeatClass = cell` manifest, today's `none`). A defect it exposes in -c's code is fixed here,
   named in the report. Settles: (0).
1. **The class rule, in code:** the kind reaches the one answer — `SessionSeatClassFor(role, kind)` (or the session handed the standing's kind as it is handed its
   workspace), read from the chart where asked: `cell` for a Member beyond the legacy pair on a runtime whose BUILT-IN manifest has the class, `broker` for Head and Staff,
   `cli` for the pair; puppet/test-host declarations unchanged; the gate, the session and `RuntimesOf` read the same answer. Settles: (a), (b).
2. **No degraded start:** a Member whose `cell` launch is refused (no counted proof; a proof not measuring `web`; a profile mismatch; not Linux; no python3 ⇒ no proof, -c)
   is refused with the gate's or `RouteA.Admission`'s own words, its wakes kept — never re-declared `broker`/`cli`. Settles: (c).
3. **The workspace and egress `web`:** ONE constant for an executor cell's egress (`CellEgress.Web`) read by both `RouteA.Policy` and the admission check — never a file,
   setting, verb or op; `RouteA.AssuranceRows` names the policy's egress and, under `web`, adds GAP rows for loopback-443 and LAN-443 (the proof's own row names); the
   workspace stays `HomeFor(position)`, created by the app, never `Paths.Workspace`; the canon the cell can rewrite in its home (`AGENTS.md`) is the app's again before the
   next turn (state what `WorkspaceBuilder` does; fix if it does not). Settles: (d), (e), (k).
4. **The canon for `cell`:** `RuntimeClass.Cell`; `AgentReach.For(role, Cell)` = the door's tools, `RunsCode = true`, `Os = Linux`, `Walled = true`, the network it has
   (TCP 443 and names, any host; nothing else) and that `trade` is not on its path; `Canon.Version` 5 → 6 (renumber at rebase) with `CanonTests`' ledger entry; the
   binding test over `Cell`. Settles: (f).
5. **The executor rows** over item 0's seam (replaces the draft's puppet steps — the puppet cannot be route (a)'s harness): a Member's turn through the real app path.
   Settles: (d), (g)–(j).
Red-first (UnitTests, every OS, OS-absolute paths — never `Category=Cell`, Cell row (f)'s lesson): (a) `A_member_beyond_the_legacy_pair_is_declared_cell`; (b) GUARD
`Heads_and_staff_stay_broker_and_the_legacy_pair_cli`; (c) `A_member_with_no_proof_measuring_web_is_refused_and_never_started_on_another_class`; (e)
`An_executor_cells_egress_is_web_and_nothing_outside_code_changes_it`; (f) `Every_tool_and_path_the_cell_canon_names_is_reachable` (red: no class); (k)
`An_executor_attempts_assurance_names_web_and_its_loopback_and_lan_443_gaps`.
Linux (`CellLinuxTests.Executor.cs`, `Category=Cell`, CI's pinned codex and mock, FAKE key, no login): (0) above; (d) `An_executor_cell_writes_only_its_own_home`; (g)
`A_member_writes_a_tool_in_its_cell_and_runs_it_with_no_admission_beyond_its_turn`; (h) `A_tool_written_in_one_attempt_runs_in_the_next`; (i)
`An_executor_tool_reaches_443_and_no_other_port_and_reads_no_state_seat_home_or_inbox`; (j) GUARD `Under_web_a_route_a_cell_still_cannot_connect_to_the_door`
(the door is an ephemeral loopback port, `BrokerDoor.cs:325`; -c's row (f) proved it under `none` only).
**The watched mutant:** item 1's Member branch answers `cli` ⇒ (a) red AND (g) refused by the gate ("code needs a confined cell"). Quoted red on the job that proves it,
restored, green.
**Evidence beyond CI:** the Cell step's log (rows, the proof id and its egress, the attempt's recorded class, egress and GAP rows); the tests box per the manager (Mihael's
laptop: tests only). **NOT VERIFIED until the owner's host (-e) and a paid run on his word:** a real model in the cell; real `pip`/`npm` over 443; his kernel/LSMs;
python3 present there (else no proof and every executor refused — by design, in words); aarch64.
Read first: `CLAUDE.md`; ORCHESTRATION §§ 2.3, 2.7, 2.8, 5; the survey (`survey.md` §§ 2–6); -c's record (`fleet/records/U-cell-linux-c.md`) and brief
(`git show 7550906b^:docs/briefs/U-cell-linux-c.md`); `PositionActivation.cs`, `SeatAssurance.cs`, `RuntimeManifest.cs`, `RouteA.cs`, `AgentSession.cs` (:1093-1125),
`AgentReach.cs`, `Canon.cs`, `WorkspaceBuilder.cs`, `CellLinuxTests.Route.cs`.
**NOT in scope:** `web-plain`, a host allowlist or proxy, in-cell code calling the broker, the `door` egress class, bulk data inputs (`U-capability-broker-sandbox`); route
(c) (`U-creative-api-worker`); the lattice (O6); lanes (N stays) and presence (Q5); a confined harness (a card); Windows/macOS cells; Staff on `cell` unless seat A rules it.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; branch CI on three platforms with the Cell step's rows quoted;
Codex review — HELD while the orchestrator says attempt 4 is live; names vs `main` 0 removed). `## Report` ≤ 20 lines appended here.
