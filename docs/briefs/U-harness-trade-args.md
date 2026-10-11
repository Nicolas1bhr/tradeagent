# U-harness-trade-args — the API harness offers the trade tool every argument its ops take, each with the type the pipe reads, so a model that honours the offered schema can pass them, pinned by a test that sends a complete call of every op through the offered schema to the gateway
**Protects:** the agent boundary and operator authority (`CLAUDE.md`: no op, verb or argument grants permission — the gateway still refuses, per op, an argument the op does not take; nothing here adds an op, an
argument or a reach); honesty (the tool contract a provider is handed says what the surface really takes — today it says "no arguments" and the description says "run 'schema'"); the switch-on (an
unattended run on the API harness cannot place, cancel, backtest or write the ledger through a schema-honouring provider). Seat B; heavy (the agent boundary: opus); **no rung**.
Owed (pre-existing on `main`, found by seat B 2026-10-11 02:16; fail-closed — less capability, never more). MUST land before any unattended run on the API harness.
**Facts (SOURCE at `main` `604a3133`, the named files unchanged at `eace5030`; re-check by `grep -n`).**
- `src/TradeAgent.AgentRuntime/GrantedWorkerTools.cs`: the `trade` spec (:449-459) declares only `op` and `request_id`; `Schema` (:494-500) sets `additionalProperties = false`. `data` (:461-478) and
  `report` (:480-484) list their args by hand. `Fields` (:363-373) forwards every property but `op`/`request_id` to the gateway, whose own parsers read them; `TradeOps` (:124-131), `OpsOf` (:148-154).
- `src/TradeAgent.Gateway/GatewaySchema.cs`: `ArgSpec(Name, Type, Required, Description)` (:12), `Ops()` (:88-) — every op's args; types in use are `string`, `number`, `bool` (not JSON-schema `boolean`).
  `GatewayPipeServer.cs:3981` — an argument an op does not take is refused by the gateway.
- `ToolSpec(Name, Description, Parameters)` (`WorkerTools.cs:8`); `Granted` is also read by `AgentReach.cs:157` and `Canon.cs:945`. Tests: `WorkerToolTests`, `HarnessLoopTests`, `ResearchLedgerOverPipeTests`
  (a real pipe).
Must NOT: add, rename or remove an op or an `ArgSpec`; change the gateway's argument refusal, `Fields`, a parser, or what any op does; make anything REQUIRED in the offered schema (the doc on `Schema`
says why); touch the canon template, `Canon.cs`'s rendering or `WorkspaceBuilder` (the canon's ≤ 16 KiB budget, heaviest pair 16,211 B); add a pipe op, verb or setting.
Items, one commit each, one-sentence messages:
1. **The offered schema from the one description.** `trade`'s parameters (and `data`'s and `report`'s, replacing the hand-written lists) are built from `GatewaySchema.Ops()` for exactly the ops
   `OpsOf(tool)` carries: `op` (an `enum` of those ops), `request_id`, and the union of their `ArgSpec`s, each typed as the pipe reads it (`bool` ⇒ `boolean`; verify `number` against the pipe's strict
   reader — if it reads a JSON string, offer what it reads), its description short (name the ops that take it; the full text stays in 'schema'). An argument name declared with two types across one
   tool's ops is a build-time refusal (a thrown exception in `Specs()` caught by a test), never a guess. `additionalProperties = false` stays. Measure the offered tools' JSON bytes before/after and
   report both (they ride every API turn).
2. **The test that a provider's call goes through.** For every tool with ops and every op it carries: a call carrying EVERY one of that op's args, each a value of the offered type, validates against the
   tool's `Parameters` (a small validator in the test: `type`, `properties`, `enum`, `additionalProperties`), and — through a real pipe to a test gateway as `ResearchLedgerOverPipeTests` does — is
   refused by the gateway for no argument's name or type (a mutating op may be refused for its role or mode; that refusal is fine and named). Plus: no offered property is one `GatewaySchema` does not
   declare for some op of that tool (no invented reach).
RED-first: test 2's validation quoted red at base (`trade` rejects every op's args). Mutant, red and quoted: one op's args dropped from the union (e.g. skip `Ops.Buy`) ⇒ red.
Gate: SPEED MODE (`fleet/SPEED-MODE.md` § 4) — rebase on `main` first; Release `--no-incremental` 0 warnings; touched classes 3×; the full suite on branch CI, all three platforms; tests box `ready` once or
NOT RUN; names vs `main` 0 removed (both set sizes). The report names "no rung" and the offered bytes before/after. `## Report` ≤ 20 lines appended here. No push to `main`, no merge.
