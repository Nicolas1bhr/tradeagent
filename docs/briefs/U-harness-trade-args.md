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

## Report
Code tip `98e7bf9b` (on `main` `f1bdd2d2`). **No rung.** Seat B's two Codex gaps are folded in, one commit each (`5586185e`, `98e7bf9b`).
- Build: `dotnet build TradeAgent.sln -c Release --no-incremental` → `0 Warning(s)` / `0 Error(s)`.
- Touched classes 3× Release: `WorkerToolTests` Passed 28/28 ×3; `HarnessTradeArgsTests` Passed 3/3 ×3. CI run 38114512335 `success`, all 11 jobs (ubuntu, macos, windows 1–5, packages, linux-host). Runs 38113355133 and 38113431116 were cancelled and do not count.
- Names: `removed: 0`, `added: 4`, sets base 2512 / tip 2516.
- Item 1 done: `OpsSchema` builds trade/data/report's parameters from `GatewaySchema.Ops()`: an `op` enum, `request_id` only on a tool with a mutating op, and the union of the ops' args, each described as "For <ops>.". Types: `bool`→`boolean`; `number`→`number` (`Dec` reads a JSON number). It throws on two types for one name, an unknown type or an undescribed op. Offered tools on the wire: **5,139 → 7,280 bytes**.
- Deviation 1: the schema is now a Dictionary. `Json.Write`'s snake_case policy had been sending `additional_properties` to the provider, so `additionalProperties:false` never reached it. Now it does, on all six tools.
- Deviation 2: `sell` declares no args ("Same arguments as buy"; one `ParsePlace` reads both), so it is offered buy's. `request_id` is not added to data/report, which carry no mutating op.
- Item 2 done: every op's complete call validates against the wire schema and is refused for no name or type, on the harness route and the real pipe. The fixture has a real tape and a real ledger entry, and data-tape and ledger ops are now served. Every number/flag arg is probed with an unreadable value and must be refused FOR that arg. Without a tape that check goes red (`data-tape·limit: MARKET_DATA_UNAVAILABLE`). The `'run' is not an argument of 'ledger-add'` refusal holds on both routes. No offered property is undeclared, and every op's every arg is checked per op.
- RED at base: `trade·buy: the schema does not say additionalProperties: false under that name (its keys: type, properties, additional_properties); 'symbol' is not a property the schema offers; …`, plus `trade offers 'op' with no enum of its ops`.
- Mutants, red then restored green: (a) skip `Ops.Backtest` → `trade·backtest: 'strategy' is not a property the schema offers; …`. (b) drop `capital`, declared by backtest alone (LINQ-logged) → `trade·backtest·capital: offered nothing, read as number`. Skipping buy cannot go red, because sell shares its args.
- Canon: `CanonTests`/`AgentReach` read tool names and ops only, never tool text, so canon bytes are unchanged (`CanonTests` 21/21 green).
- Tests box: NOT RUN. `ready` says `NO - the machine does not answer (asleep, off Tailscale, or the share was removed)`.
- Not done: no gateway refusal, `Fields`, parser, op or ArgSpec changed. Nothing is marked required. Not run on a real provider's schema validator.
