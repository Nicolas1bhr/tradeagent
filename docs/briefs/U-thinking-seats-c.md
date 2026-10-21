# U-thinking-seats-c — the broker-only codex seat: a seat class with no environment, the app's door as its only tool server, its model pinned and its offered tools verified before every launch
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.7 (a broker-only codex seat, "viable and MEDIATED"; "the offered surface is verified at every launch"), § 2.6
(the canon per seat template, its reach generated), § 5 (the thinking-seat column); R-exec-server DECISION 2; ORGANISATION § 15 row `U-thinking-seats`.
Part c of 3 (a gate → b door → **c**). **A protection — CRED-001 (the seat home's login offered to no model tool), Law 2 (no model-directed code in the
process that holds the login), ORG-001: red-first and ONE watched mutant.** Seat A; opus/high. **No rung** (part a's rung 39 records the class).
**AMENDED AT DISPATCH (seat A, 2026-10-17 ~12:10, main `bb390ed7`; these lines outrank the older ones below where they differ):**
- **Re-grep every file:line below** (taken at `8f261176`). LANDED since: `U-seat-acl`; `U-puppet-runtime`; part a `U-thinking-seats-a` (rung 39 — `SeatClasses`
  in Core/Db/SeatAssurance.cs already names `broker` ⇒ Mediated, `RuntimeManifest.SessionSeatClass` is the one answer, `Begin` writes `seat_class`/`assurance`,
  `CapabilityPolicy`, `PositionActivation`, `cell_proof`; O3's `RefusalKind`); part b `U-thinking-seats-b` (`BrokerDoor`, `AppHost.DoorUrl`, `BrokerDoor.PinnedCodex`
  — the built-in codex pin, never runtimes.json; `VerbGate` = recorded assurance ≥ Minimum(DecisionVerbs) = Mediated); O5a (`mandate_*` door verbs, rung 40);
  `U-cell-linux-b` (Guarded refuses the seat home etc.; codex `environments.toml` is in the seat home's Forbidden — it would replace CODEX_EXEC_SERVER_URL; keep it so).
  Reuse these; never a second class table, pin or policy. "Depends on" and "Conflicts" below are satisfied: all three landed.
- **Item 6 (OWED from U-thinking-seats-b's review R5, a protection):** `PeerImage.HashOf`'s cache (Security/PeerImage.cs ~225) trusts path + length + mtime, so a
  same-length replacement with a restored mtime reuses the approved digest for the pin the door and the pipe check. Key it on (device, inode, ctime, size) — ctime
  cannot be set back by a user — or re-hash; on Windows the file id + change time. Red-first: `A_runtime_replaced_with_equal_length_bytes_and_its_mtime_restored_is_not_the_pin`
  (prime the cache, replace, restore mtime, a new door connection → refused).
- **Canon:** main is `Canon.Version = 4`; this unit takes v5 unless another lands first (renumber at rebase, say so).
- **Q2 (code ceiling):** a broker-only (MEDIATED) seat counts as UNCONFINED for lanes (ORCHESTRATION § 2.5); this unit adds no lane and raises no N (U-org-lanes, seat B, in flight).
- **No real model, no paid run, no observed run.** codex only with a throwaway CODEX_HOME + HOME, the kit's mock and a FAKE grant. The repo is public.
- CI is shared (GitHub Free): one branch run per pass; the watched mutant on the ONE job that proves it, the others cancelled. Scan exclusions by phrase: "Bearer", "token".
  A run red ONLY on macOS VendorLimitQuoteTests (seat B's unit) → re-run that job once and quote both.
**Depends on parts a and b, and on `U-seat-acl`** (in flight on `SeatProfile.cs`). Canon v5 (or the next free version — the second to land renumbers).
**Today (main `8f261176`, READ + RUN):** one seat class, `SeatPlan.CliClass` (RuntimeManifest.cs:83); the seat machinery is per runtime: `Environment`
(:80), `KeyArgs()` reads the CLI class only (:129), `ConfigBytes` (SeatProfile.cs:187) and `SeatArgs` (AgentSession.cs:163) hard-code it, one
`FeatureAllowlist` and an empty `McpAllowlist` (RuntimeManifest.cs:704), `-c mcp_servers={}` (:672); `VerifyAsync` (SeatProfile.cs:219) proves pin,
deny set, config bytes, inventory and the CLI's own reports — never the tools a model is offered. The app pins no model for codex seats.
`RuntimeClass {Cli, Harness}` (AgentReach.cs:11); `Canon.Version = 4` (Canon.cs:38) with its ledger and binding tests (CanonTests.cs:135, :267-303, :447).
RUN (`fleet/tmp/A-org/thinking-seats/probe/runs/`, codex 0.160.1, mock model, throwaway home): with `CODEX_EXEC_SERVER_URL=none`, the host features
off, `agents.enabled=false` and the door as the only MCP server, gpt-5.5 is offered `list_mcp_resources, list_mcp_resource_templates, read_mcp_resource,
request_user_input, tool_search` (e1); gpt-6-astra is offered `exec` (code mode, a V8 isolate inside the codex process) even with `code_mode_host`
disabled (e7); `codex debug prompt-input` and the rollout show no tool list (e6).
**Observable result:** a position the gate assigns a broker-only seat runs codex 0.160.1 with the class's deny set, `CODEX_EXEC_SERVER_URL=none` in its
environment, the door (part b) as `mcp_servers.tradeagent` with `required=true` and the launch grant as bearer, on a PINNED model; before each launch the
app knows the tools that binary, argv template and model offer — from a dry run against its own loopback mock, cached by (binary digest, class argv
template hash, model) — and refuses if they differ from the class's compiled expected set or include `exec`; the attempt records class `broker`,
assurance Mediated; the seat's `AGENTS.md` is a broker canon whose reach is generated from the door's tools; armed live refuses it like every CLI seat.
Read first: `CLAUDE.md` (refuse, never guess; vendor commands are data); ORCHESTRATION §§ 2.6-2.8, 5, 8; R-exec-server; the survey § 3; RuntimeManifest.cs:60-150, 630-710; SeatProfile.cs:170-300, 500-560; AgentSession.cs:150-200, 280-300, 610-705; AgentReach.cs; Canon.cs:30-140, 821ff; CanonTests.cs; SeatHomeTests.cs (the fake probe); the kit `fleet/tmp/A-org/exec-server/` (mock, `tools.py`).
Items, one commit each, one-sentence messages:
1. **Per-class seat data:** `SeatPlan.BrokerClass = "broker"`; `Args`, environment, feature allowlist, MCP allowlist and probe keys keyed by class; ONE
   config.toml stays the CLI class's (turns ignore it; two writers would trip `VerifyAsync`'s same-bytes check :245); `{doorUrl}` substituted at launch;
   the broker class's security fields compiled in — a runtimes.json override that changes them refuses the class, naming both.
2. **The pinned model:** the class carries its model (gpt-5.5 today); no model or another one refuses; the expected offered set per model is compiled data.
3. **The surface verifier:** `ISurfaceProbe` (fake in CI) and the real one — a dry `codex exec` against an app-hosted loopback mock Responses endpoint, a
   throwaway home, the class argv with a stub door — returning the captured `tools`/`additional_tools`; cached per key; any `exec`/`shell`/`apply_patch`
   or a set ≠ expected refuses before admission (inside `CheckSeat`, so `Begin` writes nothing).
4. **Launching it:** `SeatArgs(manifest, class)`; `VerifyAsync` per class; `SeatVerdict.Assurance = Mediated` for a verified broker seat; part a's
   register declares the class for positions it assigns; `ProfileHash` per class.
5. **The canon:** `RuntimeClass.Broker`; `AgentReach.For(role, Broker)` = the door's tools (GrantedWorkerTools), `Os = null`, `RunsCode = false`, `Walled =
   true`, the CLI session length; a broker template rendered to `AGENTS.md`; `Canon.Version` raised and the ledger extended; the binding tests over Broker.
Red-first tests (quote each red at base): (a) `A_broker_seat_starts_with_no_environment_and_the_door_as_its_only_tool_server`; (b)
`A_broker_seat_whose_offered_tools_include_a_host_tool_or_exec_is_refused_before_admission`; (c) `A_broker_seat_without_its_pinned_model_is_refused`;
(d) `A_runtimes_json_override_of_the_broker_class_is_refused_naming_both`; (e) `A_broker_seat_is_refused_while_live_is_armed`; (f)
`Every_verb_tool_and_path_the_canon_names_is_reachable_by_its_role_and_runtime` extended to Broker (red: no such class); (g) GUARD `The_cli_class_runs_as_before`.
**The watched mutant:** the verifier's comparison skipped (the probe still runs) ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** the real verifier on this Mac — codex 0.160.1, a throwaway home, the mock, FAKE grant: quote the captured set for gpt-5.5 (accepted)
and gpt-6-astra (refused, `exec`). No real model, never the owner's `~/.codex`. The tests box if `ready`. NOT claimed: the real provider's model metadata
equals the bundled metadata the dry run sees (UNKNOWN until the first observed run, on the owner's word).
Out of scope: a per-request inspecting proxy for the model API; the first observed run; cells; org verbs; the legacy pair's seats. Conflicts: `U-seat-acl` (`SeatProfile.cs`) — wait; parts a and b — after both; `U-puppet-runtime` (`RuntimeManifest.cs`) — rebase.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed, both set sizes printed). `## Report` ≤ 20 lines appended here.

## Report
**Tip (code)** `d58e82f3` (rebased on main `673a2ca5`, U-org-lanes); commits a8515719→d58e82f3, one per item + one Windows test fix. Builder, opus/high, 2026-10-17. No rung; canon v5 (no other unit took it).
**Gate:** Release `--no-incremental`: "0 Warning(s) 0 Error(s)". BrokerSeatTests+CanonTests+ThinkingSeatsTests 3× "Passed: 49 Failed: 0"; BrokerDoorTests 3× "Passed: 22 Failed: 0" (rebased tree). CI **39029970908** at `d58e82f3`: every job success (attempt 2 — attempt 1 red ONLY on macOS `TurnMeterTests.Two_usage_events_in_one_run_are_added_rather_than_replaced`, NullReferenceException at :212, a no-seat Operations session over a script — the family O5a's report sighted on ubuntu; no seat-plan path of this diff reaches it; the same test green on ubuntu/windows in that run and on macOS in 39028106588; failed job re-run once, no code changed — FOR SEAT A to judge). Pre-rebase run 39028106588 all 12 success; 39026094969 red only on Windows (a) — the .cmd echoes quoted argv; fixed in the test (d58e82f3). Names vs main: "sets: base 2945 tip 2952 / removed: 0 / added: 7". Scan: "Bearer"/"token" by phrase (the brief's).
1 DONE `SeatPlan.BrokerClass`, `DoorSlot`; per-class Args/ClassEnvironment/FeatureAllowlist/McpAllowlist/KeyArgs; one config.toml (CLI's); `BrokerSeat.OverrideRefusal` names field, file's and build's SHA-256 + `Versions.App`. Deviation: allowlists are now dictionaries, so an older runtimes.json with array allowlists is refused whole as unreadable (no shim, CLAUDE.md).
2 DONE `BrokerSeat.Model = gpt-5.5`, `Expected` per model; no pin / no `{model}` flag / another chosen model refuses; the owner's raw choice reaches the session (default model is no choice); pricing at the pin.
3 DONE `ISurfaceProbe` + `CodexSurfaceProbe` (loopback mock Responses + stub door, throwaway home, class argv), cached by (exe SHA, template hash, model), compared every call inside `CheckSeat`.
4 DONE `SessionSeatClassFor(role)` (position beyond the pair on codex ⇒ broker; replaces `SessionSeatClass`), `SeatArgs/For(manifest, class, door)`, `VerifyAsync(SeatLaunch)`, `ProfileHash` per class (CLI hash unchanged), `door` through supervisor→runtime→session, AppHost declares broker. ThinkingSeatsTests launch-time test re-posed (door + fakes so the verified seat is reached; purpose kept).
5 DONE `RuntimeClass.Broker`, reach = door tools + mandate verbs, Os null, walled, CLI session length; v5 ledger (ops cbb98738…, research 04ddafbd…); positions' AGENTS.md is the broker render. Note: a broker seat reads no inbox, so OrgPrincipalsTests (j) no longer exercises a position's inbox path.
6 DONE `PeerImage.HashOf` keyed on dev/inode/ctime/size (stat before+after), Windows uncached.
**RED at base:** (a)(b)(c)(d)(e) "'ISurfaceProbe' could not be found … 'SeatPlan' does not contain a definition for 'DoorSlot'"; (f) "'RuntimeClass' does not contain a definition for 'Broker'"; item 6 "…restored was served as the pin: 200". (g) guard green. **Mutant** (Mac, pure policy): comparison skipped ⇒ only (b) red "a broker seat offered code mode's exec (gpt-6-astra) was not refused"; restored green.
**Mac evidence** (codex 0.160.1 = pin 09fa44fd…, throwaway home, app's mock, FAKE grant; the binary resolves under ~/.codex/packages — only run/hashed, no config read): gpt-5.5 "list_mcp_resources, list_mcp_resource_templates, read_mcp_resource, request_user_input, tool_search[tool_search]" ACCEPTED; gpt-6-astra "functions.exec[custom], wait, request_user_input(_async)" REFUSED 'exec'.
**Review:** fleet/tmp/A-org/u-thinking-seats-c-build/U-thinking-seats-c-review.md (2 P1s, not acted on). Tests box: NOT RUN — the machine does not answer.
**NOT VERIFIED:** real provider metadata = bundled (UNKNOWN until an observed run); the real verifier on Linux/Windows; how codex names door tools to the model vs the canon's `read_file(...)`; no real broker turn end to end.

## Fix pass 1
**Tip (code)** `df1411b0`, on origin/main after the rebase (the rebased 037bf49d is `5a6731ac`). Fixer opus/high, 2026-10-22. Commits: `57a6b91b` R1, `df1411b0` R2. No rung; canon v5.
**Gate:** Release `--no-incremental`: "0 Warning(s) 0 Error(s)". BrokerSeat+ThinkingSeats+SeatHome+Canon+PuppetSeat+SeatAcl+SeatSignIn 3× "Passed: 94, Skipped: 2, Failed: 0". Names vs main: "sets: base 2968 tip 2977 / removed: 0 / added: 9". CI **39686607659** at `df1411b0`: all 12 jobs success (attempt 1). Scan: "Bearer"/"token" excluded by phrase (the builder's code terms, the brief's).
**R1 DONE** `BrokerSeat.Fields` compares the program pin (trimmed, lower-case), first. `A_runtimes_json_that_swaps_the_program_and_its_pin_is_refused_and_the_program_never_runs` (an override swapping executable + digest to a second script, the REAL `CliSeatProbe`): RED at `5a6731ac` "the swapped program ran during the seat check: features list --disable apps …"; green — refused naming "program pin", no admission, the script never runs. Test rig: `SeatHomeTests.Seat` registers its script through the internal `BrokerSeat.PinForTest`, read only while `PuppetSeats.TestHost` (every broker-session class shares the serialized vendor-override collection with the one test that clears that flag); a program not registered compares against the build's real pin.
**R2 DONE** `Fields` compares the home's forbidden paths; same shape, also the workspace's forbidden names and the credential files (CRED-001), as two new rows of (d)'s table. `A_runtimes_json_that_drops_a_forbidden_home_path_is_refused_although_the_surface_passes` (environments.toml dropped, one seeded in the seat home, the fake reports and surface passing): RED at `5a6731ac` "…dropped the seat home's environments.toml from its forbidden paths was admitted"; green — refused before the surface is asked (0 asks), no attempt row, no exec.
**Mutant:** the pin line dropped from the compared set ⇒ only R1's test red "the swapped program ran during the seat check: features list …" (BrokerSeatTests 1 failed / 7 passed); restored, green.
**Re-review:** fleet/tmp/A-org/u-thinking-seats-c-fix/rereview.md (unread). Tests box: left to seat A's landing train.
**NOT VERIFIED:** the fix on a real codex (no codex run in this pass; the Mac evidence above is the builder's); `SeatPlan.Home` and other manifest fields outside the compared set remain as the builder left them.
