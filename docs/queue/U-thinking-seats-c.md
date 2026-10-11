# U-thinking-seats-c — the broker-only codex seat: a seat class with no environment, the app's door as its only tool server, its model pinned and its offered tools verified before every launch
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.7 (a broker-only codex seat, "viable and MEDIATED"; "the offered surface is verified at every launch"), § 2.6
(the canon per seat template, its reach generated), § 5 (the thinking-seat column); R-exec-server DECISION 2; ORGANISATION § 15 row `U-thinking-seats`.
Part c of 3 (a gate → b door → **c**). **A protection — CRED-001 (the seat home's login offered to no model tool), Law 2 (no model-directed code in the
process that holds the login), ORG-001: red-first and ONE watched mutant.** Seat A; opus/high. **No rung** (part a's rung 39 records the class).
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
