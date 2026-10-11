# U-thinking-seats-b — the broker's shell-free door: the app's own MCP server on loopback, the launch's grant as its bearer, bound to the attempt's container, serving the harness's default-deny tools
**Arrow closed:** `docs/ORCHESTRATION.md` § 2.7 ("the broker's shell-free door … the app's own MCP server … under the attempt's grant, serving the
organisation's verbs, artifact reads by id and bounded data queries"), § 2.3 (a grant bound to the attempt's container), § 2.9 (one authority
implementation); ORGANISATION § 15 row `U-thinking-seats`. Part b of 3 (a gate → **b** → c class). **A protection — AUTH-001 (no operator op on the door),
ORDER-001 (no mutating op unless `operations`' grant), the grant binding: red-first and ONE watched mutant.** Seat A; opus/high. **No rung.**
**Depends on `U-grant-tree`** (`AttemptContainer`, PENDING-until-bound grants — `docs/briefs/U-grant-tree.md` items 1-2); ∥ part a.
**Today (main `8f261176`, READ + RUN):** no MCP server or HTTP listener in `src`. The harness's surface is `GrantedWorkerTools` (AgentRuntime/GrantedWorkerTools.cs:41;
six tools, default deny, a `tool_call` row per call, `InvokeAsync` :175, ops `TradeOps` :130 / `DataOps` :145 / `ReportOps` :148, writes `Writable` :75),
dispatching through `IGatewayCalls` = `GatewayPipeServer.CallAsync` (:1123, the pipe's own handler; orders refused :1188), composed per role at AppHost.cs:211.
The pipe authenticates `TRADEAGENT_GRANT` (Security/AgentGrants.cs:71; issued AgentSession.cs:682, env :689; `GrantRefusal` GatewayPipeServer.cs:1068).
codex 0.160.1 speaks MCP over stdio or streamable HTTP only (vendor `codex-mcp/src/runtime.rs:894-899`) and, with `CODEX_EXEC_SERVER_URL=none`, HTTP only
(:893-899). RUN (`fleet/tmp/A-org/thinking-seats/probe/runs/`, mock model, FAKE token): `bearer_token_env_var` sends `Authorization: Bearer` on every request
(e1); `required=true` + an absent token or a 401 fails the session before any model request (e2, e3: rc 1, 0 model requests); HTTP/1.1 keep-alive carries
initialize/tools/list on one connection (e5); the TCP peer of that connection is the codex process (macOS `lsof`, e1/e5).
**Observable result:** the app runs ONE loopback MCP door (127.0.0.1, an ephemeral port chosen at start, streamable HTTP, JSON responses) that accepts a
request only with `Authorization: Bearer <a live launch grant>`, only on a connection whose TCP peer is a member of that grant's attempt container and runs
the pinned codex image (start before accept; any lookup error refuses), re-verifying the grant per request and closing the grant's connections when it ends.
It serves exactly `GrantedWorkerTools.Granted` for the grant's role — the same object the harness uses, so the same refusals, rows and write roots — as MCP
`tools/list`/`tools/call`; results bounded (a byte cap, "truncated — fetch by id"); no op the pipe lacks; no operator op; mutating ops refused as on the pipe.
Read first: `CLAUDE.md` (operator authority in-process; inbox is data); ORCHESTRATION §§ 2.3, 2.7, 2.9, 8; R-exec-server; the survey § 4; GrantedWorkerTools.cs;
GatewayPipeServer.cs:700-830, 1050-1200; AgentGrants.cs; Security/PeerImage.cs; U-grant-tree's `AttemptContainer` as landed; `ApiAgentRuntime.cs` (in-process calls).
Items, one commit each, one-sentence messages:
1. **`BrokerDoor`** (a `TcpListener` on 127.0.0.1:0 with a minimal HTTP/1.1 + JSON-RPC MCP subset: `initialize`, `notifications/initialized`, `tools/list`,
   `tools/call`; GET 405; no SSE) composed in AppHost beside the pipe server, its URL exposed for part c; started and stopped with the gateway.
2. **Authentication:** the bearer is the launch grant (`Grants.Verify` per request; Absent/Unknown/Expired/Pending ⇒ 401 in words); `AgentGrants.Ended` closes the
   grant's door connections as it closes its pipes.
3. **The binding:** `TcpPeer.OwnerOf(local, remote) → PeerFacts` per platform — Linux `/proc/net/tcp{,6}` inode → `/proc/<pid>/fd`; macOS libproc
   `PROC_PIDFDSOCKETINFO`; Windows `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_CONNECTIONS)` — fed to `AttemptContainer.Contains` AND the `PeerImage` rule with the
   pinned codex digest, once per accepted connection; injectable for tests.
4. **The surface:** `tools/list` = `GrantedWorkerTools.Granted` as MCP tool schemas; `tools/call` → `InvokeAsync` under the grant's role and attempt; a byte cap
   per result; every call a `tool_call` row (as the harness's).
Red-first tests (quote each red at base): (a) `A_door_request_without_a_live_launch_grant_is_refused`; (b) `A_grant_replayed_to_the_door_from_outside_its_attempt_container_is_refused`
(the test process presents a live attempt's token, the image rule pointed at the test's own image so only membership decides); (c) `A_peer_whose_image_is_not_the_pinned_codex_is_refused`; (d) `A_peer_lookup_that_fails_refuses`;
(e) `A_research_grant_asking_the_door_to_buy_is_refused_as_on_the_pipe_and_recorded`; (f) `An_ended_grant_closes_its_door_connection`;
(g) GUARD `The_door_lists_exactly_the_harness_tools_and_answers_a_read` (a real HTTP client on all three runners); (h) GUARD `The_pipe_and_the_harness_behave_as_before`.
**The watched mutant:** the container-membership conjunct removed from the door's binding ⇒ (b) red. Quote it, restore, green.
**Evidence beyond CI:** codex 0.160.1 against the door on this Mac with a throwaway CODEX_HOME+HOME, the mock provider and a FAKE grant minted by a test host
(no model): the door's log shows initialize/tools/list accepted, and a second codex outside the attempt refused — quote both. The tests box if `ready`.
Out of scope: the codex seat class and its argv (part c); org verbs (O5/O6) and artifact reads beyond today's `*-show` ops; cells reaching the door
(`CellPolicy.TcpPorts`, `U-cell-linux-c`); a per-request inspecting proxy for the model API.
Conflicts: `U-grant-tree` (wait for it); part a (`AppHost` composition — adjacent); `GatewayPipeServer` untouched beyond reuse.
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three
platforms; the Codex review with the CI wait; names vs `main` 0 removed, both set sizes printed). `## Report` ≤ 20 lines appended here.
