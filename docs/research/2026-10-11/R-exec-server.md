# R-exec-server — codex's exec-server and the broker-only thinking seat (decision record)

2026-10-11 · seat A probe leg · codex-cli 0.160.1 (macOS arm64 dev Mac) · vendor source openai/codex tag `rust-v0.160.1`
(commit object c3e23d4c, release published 2026-10-05), read 2026-10-11. No product code. Every run: a throwaway CODEX_HOME
and HOME, a scripted local mock model (no login, no real model call). Kit and raw runs: `fleet/tmp/A-org/exec-server/` (`$OUT`).

## Questions
A. What is `codex exec-server`, how is a harness pointed at it? B. Does every model-directed action route through it with the
login kept in the harness? C. Can a codex seat run broker-only (no host-reaching tool, the app's tools only) — and does codex
execute a tool it did not offer? D. Is it in the Linux build?

## Method
Mock Responses API (SSE: `response.created`, one `response.output_item.done`, `response.completed`; shapes from
`core/tests/common/responses.rs:773-1070`), one tool call per step, then a final message. Split on one Mac by two seatbelt
profiles: the exec-server runs as its own process (`EXEC_ENV=cell`, its own empty CODEX_HOME, cwd in the cell tree, denied the
harness tree); the harness (`EXEC_ENV=harness`, the CODEX_HOME holding the config, denied the cell tree) connects by
`CODEX_EXEC_SERVER_URL=ws://127.0.0.1:…` or launches it by `environments.toml` `program=` (stdio, transport tee'd). Where a thing ran
= marker + which private tree it could touch + its parent in `ps`. Models: `gpt-5.5` metadata (classic tools) and `gpt-6-astra`
metadata (`tool_mode: code_mode_only`, multi-agent v2) — the app pins no model, so a seat gets the account default.

## A. What it is
- `codex exec-server` "[EXPERIMENTAL] Run the standalone exec-server service" (`codex --help`, RUN). A JSON-RPC server over
  `--listen ws://IP:PORT` (default) or `stdio` (RUN help), plus `--remote`/`forward` registration with OpenAI's environment
  registry (Noise relay; uses ChatGPT sign-in or `CODEX_API_KEY` — README.md:36-60). Optional WebSocket auth (capability token,
  signed bearer); none on stdio (README.md:28-35). Methods: `process/start|read|write|terminate`, `fs/readFile|writeFile|…`,
  `environmentConfig/read` (README.md:184-450; environment_config.rs). Feature list marks no `exec_server` flag; experimental.
- Pointing a harness at it: `CODEX_EXEC_SERVER_URL=ws://…` makes it the default environment and DROPS the local one
  (`environment_provider.rs:84` `include_local = !disabled && !has_remote`); or `CODEX_HOME/environments.toml` with `url=` or
  `program/args/env/cwd` (stdio; `environment_toml.rs:39-50`, `include_local = false`). `CODEX_EXEC_SERVER_URL=none` = no
  environment at all; "Callers use `default_environment().is_some()` as the signal for model-facing shell/filesystem tool
  availability" (`environment.rs:79-82`).

## B. Routing (ws and stdio, `--dangerously-bypass-approvals-and-sandbox`) — RUN, $OUT/runs/b*
| model-directed action | where it ran | evidence |
|---|---|---|
| shell `exec_command` | cell | `MARK=cell`, `CODEX_HOME=$OUT/sides/cell/codexhome`, `only-in-cell`, harness file "Operation not permitted"; `sleep 3` ppid = exec-server pid (b1-ws, b1-stdio) |
| unified exec PTY (`tty:true`) | cell | `TTY MARK=cell … /dev/ttys001` (b2) |
| `apply_patch` (abs. cell path / harness path / relative) | cell | `A …/cell/priv/patched.txt`; harness path "Failed to write file"; relative file in workspace (b3) |
| `view_image` | cell | cell image returned; harness image "unable to locate image … Operation not permitted" (b4) |
| code-mode `exec` (JS) | harness, in a V8 isolate | `require=undefined process=undefined fetch=undefined`; its nested `exec_command` → `CODE MARK=cell`, nested `apply_patch` wrote in cell (b9) |
| sub-agent (`spawn_agent`) | agent in harness; its tools in cell | sub-agent's `exec_command` → `SUB MARK=cell`, `only-in-cell` (b8) |
| stdio MCP server, default (`environment_id` local) | refused to start | "local stdio MCP server `echo` requires a local environment" (b5-mcp-ws; runtime.rs:893-897) |
| stdio MCP server, `environment_id="remote"` + `cwd` | cell | echo's ppid = exec-server pid (b5-mcpremote-ws; rmcp_client.rs:1236-1245) — but `env_vars` copied the harness's value in |
| web search | provider (hosted) | `web_search_call` only rendered; nothing ran locally (b6) |
| image generation | not offered (no login); by source: decoded bytes written by the HARNESS (`LOCAL_FS`) when a save root is set, else in the environment | `ext/image-generation/src/tool.rs:288-331` |
| js_repl | gone | `features list`: `js_repl removed` (RUN) |
Transport (stdio, logged): harness→cell methods `initialize` (params `{"clientName":"codex-environment"}`), `initialized`,
`fs/getMetadata` ×52 (a `.git` walk up from the cwd), `environmentConfig/read`, `process/start`, `process/terminate`.
`process/start` carries argv, cwd, an env policy (`inherit: all` of the CELL's env) and 13 fixed vars (`CODEX_SESSION_ID`, `LANG`,
`TERM`, …) — no auth: the harness home path, `SENTINEL`, `config.toml`, `token`, `api_key`, `Authorization`, `bearer` each 0 hits
(the one `auth.json` hit is the model's own command text). The exec-server ran with an EMPTY CODEX_HOME and no login.
Found on the way: (1) a stdio `program` inherits the harness's environment (`es-env-received.txt`: CODEX_HOME, HOME, PATH,
EXEC_ENV) — the launcher must `env -i`; (2) the cell answers `environmentConfig/read` with its own `mcp_servers` TOML layer (the
harness reads cell config; codex exec did not start a cell-declared server in two tries — b10); (3) the ws listener is
unauthenticated unless `--ws-auth` is set; (4) the process cwd is the harness's turn cwd, so the cell needs the workspace at the
same path; (5) a fresh home clones the plugin marketplace over the network unless `features.plugins=false`.

## C. Broker-only seat — RUN, $OUT/runs/c*
Config `both`: `CODEX_EXEC_SERVER_URL=none`, `--disable` shell_tool unified_exec view_image apps plugins remote_plugin
multi_agent multi_agent_v2 memories computer_use browser_use browser_use_external in_app_browser image_generation goals
tool_suggest skill_search sleep_tool workspace_dependencies realtime_conversation worktrees hooks, `web_search="disabled"`,
`agents.enabled=false`, the app's tool as `mcp_servers.echo.url="http://127.0.0.1:…/mcp"` with
`default_tools_approval_mode="approve"` (without it, no-bypass runs answer "MCP tool call requires approval, but approval policy
is never"). Offered (from the captured request):
- gpt-5.5: `list_mcp_resources list_mcp_resource_templates read_mcp_resource request_user_input tool_search`; `echo` is deferred
  behind `tool_search` and callable as namespace `mcp__echo`. All catalog/planning tools over the app's own server.
- gpt-6-astra: `functions.exec` (code-mode JS isolate), `functions.wait`, `functions.request_user_input(_async)`; inside `exec`,
  `ALL_TOOLS=["clock__curr_time","list_mcp_resource_templates","list_mcp_resources","mcp__echo__echo","read_mcp_resource"]`.
  Without `agents.enabled=false` the `collaboration.*` tools stay offered despite both multi_agent flags.
- Flags alone are NOT enough: `flags` variant (local environment present) still offered `apply_patch`, and with bypass it wrote
  `NEG-apply_patch_custom` on the host (c2-neg-flags-by1); without bypass the read-only sandbox refused it. `none` alone leaves
  `web_search` and the goal tools. Only `both` is broker-only.
NEGATIVE CASES (17 calls to tools not offered + one `local_shell_call` item; c4-final-*, both models, bypass 1 and 0): every one
answered `unsupported call: <name>` / `unsupported custom tool call: <name>` — exec_command, shell, shell_command, local_shell,
apply_patch (custom and function), view_image, write_stdin, spawn_agent (plain and namespaced), read_file, list_dir, create_goal,
mis-namespaced echo; code-mode `exec` ran but `tools.exec_command is not a function`; the `local_shell_call` item produced no call
(router.rs:248-298 builds calls only from function/custom/tool_search items; registry.rs:553-570 refuses any unregistered name).
`NEG files created:` empty in all four runs; `echo` answered every time. Same result with and without the app's bypass argv.

## D. Linux
Release `rust-v0.160.1` ships `codex-x86_64-unknown-linux-musl` and `codex-aarch64-unknown-linux-musl` (GitHub release assets,
read 2026-10-11); `exec-server` is a subcommand of that one binary, not cfg-gated (`cli/src/main.rs:235-236`); the only
platform-specific code in `exec_server_command.rs` is two macOS-only symlinked-home blocks (:235, :284); Linux adds
`--linux-sandbox-pid-namespace` (RUN help) and Linux-only tests (`cli/tests/exec_server.rs:1`). NOT RUN on Linux (no remote run
allowed; Docker is not running on the dev Mac).

## DECISION
1. Route (a) — harness outside, `codex exec-server` inside the cell — is VIABLE for `U-cell-linux`: every host-reaching
   model-directed action observed (shell, PTY, apply_patch, view_image, code-mode's nested tools, sub-agents' tools, an MCP server
   placed `environment_id="remote"`) ran in the cell; the login and the harness's home never crossed the transport; the
   exec-server needs no login. Conditions the unit must carry: stdio launch through a scrubbed `env -i` wrapper (or ws with
   `--ws-auth`); workspace at the same path in the cell; `features.image_generation=false` (or no save root); MCP servers placed
   `remote` with no `env_vars` from the harness, the app's own broker MCP over loopback HTTP; the cell's CODEX_HOME empty and
   not writable by the cell's model processes (it feeds `environmentConfig/read`); every model call still costs the harness.
   The model-written JS of code mode runs in the harness's V8 isolate — no fs/network/process there (RUN), so an isolate escape
   is the residual. Class: hard for the cell's side (as the cell provider proves), the harness itself unconfined.
2. A broker-only codex thinking seat is VIABLE for `U-thinking-seats` with the `both` profile above, enforcement class
   MEDIATED: no model-reachable tool crosses (17 negative calls + `local_shell_call` refused on both tool modes, with and
   without bypass) but the codex process holds the login and could. The seat must verify the offered surface at every launch
   (the request's tool list = the expected set) — a runtime update or account model change alters it (astra vs 5.5 differ).
   `U-harness-responses` + an API key is therefore not a precondition for thinking seats.

## UNKNOWN, and how each closes
- Linux behaviour of all of B and C → `U-cell-linux` re-runs this kit on CI's ubuntu runner (cell = namespaces/Landlock).
- Whether app-server mode (not `codex exec`) acts on a cell-declared `mcp_servers` layer → a b10 case under app-server in `U-cell-linux`.
- Real-model behaviour of a code-mode-only model when its only nested tools are the app's → `U-thinking-seats`' first observed run.
- Image generation's save root under the owner's login (not offered without login here) → `U-seat-home`'s inventory, then disable.
- A per-environment working directory distinct from the harness cwd for `codex exec` (app-server has `TurnEnvironmentParams`) → `U-cell-linux`.
- Bearer auth on the app's HTTP MCP door (`bearer_token_env_var`) not exercised → `U-thinking-seats`.
