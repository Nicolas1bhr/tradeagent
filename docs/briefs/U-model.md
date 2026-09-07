# U-model — the app names the model, commits the spend before launch, and measures the turn's context
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md` (rules 3, 4 and its `U-model` line), `docs/RESUME-HERE.md` step 6.
Branch `u-model`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-model`, rebased onto `main` first. Every
guard ships with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money.

**Why.** A turn's argv is `codex exec --json --skip-git-repo-check … "<prompt>"` (`AgentSession.cs:312-350`), so the model
comes from `~/.codex/config.toml` — the loop ran `gpt-6-astra` at 1.5 USD a turn unchosen. Nothing is written before
`Process.Start`; a killed turn costs 0 (`TurnMeter.AddToToday`); the day's totals are a kv day-string (`TurnMeter.cs:337-356`);
the cap is checked on completed spend only (`MissionLoop.cs:452`): 5.07 USD against 5 on 2026-09-07. Rules 3 and 4, made true.

1. **The model is the app's choice.** `RuntimeManifest` gains `ModelArgs` (codex: `["-m", "{model}"]`; empty where a runtime
   has no flag) and `DefaultModel` (codex: `gpt-5.6-sol`), both data; `AgentArgs.Build` inserts them before the prompt on
   exec AND resume; `TradeAgentSettings.SelectedModelId` (null = the default) is chosen on the Safety page beside the price
   boxes from the runtime's `ListPrices` models, each priced per million; one press. Measure on this Mac (codex 0.153.4)
   that `exec -m` and `exec resume --last -m` accept the flag; quote both. RED: the codex argv has no model. Mutant: resume.
2. **An attempt row before launch** — schema 6, table `ai_attempt`: id, started_at, runtime, requested_model, pricing_basis
   (`ListPrices.ReadOn` + source, or `owner`), reserved_cost, state (`LAUNCHED`/`ENDED`/`LOST`), ended_at, exit_code, the five
   token counts, effective_model (the stream's, else null), cost, unpriced_reason, context, policy_version, input_hash
   (SHA-256 of the prompt the app wrote; `docs/COUNCIL.md` round 4). Written in `MissionLoop`'s launch
   sequence BEFORE `Process.Start`, updated on `TurnEnded`; an attempt still `LAUNCHED` when a new meter opens the database
   becomes `LOST` and keeps its reservation as its cost. Day totals become SUMS over this table by local start day (the kv
   keys go); `agent-turns.jsonl` stays as the diagnostic mirror. RED (the property in `docs/COUNCIL.md`): a probe turn
   (`AgentRuntimeProbe.SessionOverStream`, a sleeping script) killed before its usage, then a fresh `TurnMeter` on the same
   database → spent includes the reservation; the next local day (injected `now`) → still charged, today 0. Mutant: `LOST` at 0.
3. **The reservation is the admission gate.** `reserved_cost` = `TurnAllowance` (a setting: default 1,200,000 input tokens at
   the requested model's uncached rate + 20,000 output at its output rate; owner override rates win); a turn is admitted only
   if `spent today + unresolved reservations + this reservation ≤ cap`, else the loop waits for midnight as now and the
   Situation says the cap was reached BEFORE the turn. RED: cap 5, three priced turns of 1.2, the fourth runs today
   (`MissionCapTests` pattern) → refused. Mutant: reservations left out of the sum.
4. **Context by component**, in `context` on the row, from the stream the app keeps (`AgentTurnEnded.Raw`): prompt chars the
   app wrote, the count of command/tool items, bytes of tool output where an item carries it (measure one real
   `codex exec --json` turn that runs `ls`; quote the item's fields), cached vs uncached input, `unattributed` = input tokens
   minus what the components explain. Nothing estimates what the stream does not show. GREEN on `TurnMeterTests.cs:80-86`.
5. **Pricing and surfaces:** a turn whose stream names no model is priced by the REQUESTED model's list price, the row saying
   the runtime did not name it; the "highest list price" estimate stays only for a runtime with no `ModelArgs`. Words only,
   `Theme.cs` only: the AI card's cost line names the model; `trade status` gains `ai_model` (schema, `CONTRACTS.md`); the guide.

Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file
→ 0 failed; touched classes 3×. Commit per item, one sentence each, no trailers. Append `## Report` (≤20 lines): tip sha,
the gate counts pasted, one line per item with its RED and mutant output, the two codex measurements, what you did NOT do.
