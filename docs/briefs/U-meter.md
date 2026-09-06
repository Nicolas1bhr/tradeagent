# U-meter — what the AI costs, per turn and per day, with a cap that pauses it

Fresh builder on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md` (vendor commands are data, not code), `MissionLoop.cs`
and `AgentSession.cs` as `U-life` left them (the `TurnEnded` seam), `RuntimeManifest.cs` and `VendorFile.cs` (how
`runtimes.json` is read), and the `U-life` section of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`; no `timeout`. Codex CLI 0.153.4 is installed and signed in on this Mac
(`~/.local/bin/codex`): the stream can be verified for real with `codex exec -s read-only --json "say hi"`. Worktree
`~/Projects/ai-trading-software-for-mihael-worktrees/meter`, branch `u-meter` from `main`. No schema change: today's
totals live in `kv`, per-turn detail in a file.

**Why.** The AI's purpose is to make at least enough to pay for itself, and half of that sentence is its own bill.
Nothing meters it, and an AI that works non-stop on the owner's account with no cap is a bill with no ceiling.

1. **Usage from the stream**: run codex 0.153.4 here with `--json` and QUOTE the event that carries token usage; parse
   it in the turn (input, output, cached, model). If the CLI emits none, meter turns and wall-clock and the card says
   "cost unknown" — never a guessed number. RED first (a turn with usage in the stream → nothing recorded) → GREEN;
   mutant (the parser dropped) → RED.
2. **The record**: one line per turn in `state/agent-turns.jsonl` in the app's state dir — NOT the agent's home, which
   the agent can edit — with started, ended, session, exit code, tokens, model, price; today's total and turn count in
   `kv`, reset at local midnight. Priced by `costs.json` beside `runtimes.json`, read through `VendorFile` (per-model
   prices per million tokens; absent or unparseable → "unpriced", visibly, as `U-batch-2b` did for the other files).
3. **The cap**: a daily cost cap in `Settings` (currency, default 5). Reaching it pauses the mission loop until local
   midnight and writes an activity line in the owner's words; the second press of "Let the AI work on its own" names
   the cap; raising the cap asks twice, lowering saves at once (the Safety page's rule for a widened limit). RED first
   (cap reached → the loop keeps turning) → GREEN; mutant (the comparison inverted) → RED.
4. **Surfaces**: the AI card shows today's cost against the cap; `trade status --json` gains `ai_state`,
   `ai_turns_today`, `ai_cost_today` (the composer that emits `ai_trading_stopped`); the Situation block (`U-life`)
   gets the cost line it left a placeholder for.

Yours: a new `TurnMeter.cs` in `AgentRuntime`, `MissionLoop.cs` (the pause only), `AgentSession.cs` (the parse only),
`Settings` in `Trading.cs`, `RuntimeManifest.cs`/`VendorFile.cs` (`costs.json`), the Safety page's cap control, the
AI card's cost line, the status composer, `docs/USER-GUIDE.md`, tests. Commit per item, no trailers. Gate: Release
`--no-incremental` → 0 warnings; each touched class 3×; full suite once to a file, one project at a time, nothing else
running; names vs `main` 0 removed. No box, no UI photograph: say so.

## Report — append here, commit it, ≤20 lines: tip sha; per item RED→GREEN→mutant quoted; the codex `--json` usage
event verbatim; gate counts; what you did NOT do. Verified or NOT VERIFIED, nothing in between.

Code tip `14164c9`: the builder's four commits plus one of mine, rebased onto `main` `c1a8ee2`; this report on top.
**The uncommitted `TurnMeter.cs` edit: COMMITTED, as dead code.** Every `CostCatalog.Price` branch that returns a null `Cost` returns
a sentence with it, so `probe.Unpriced` is never null where `WhyNoPrice` reads it: `LastUnpricedReason`/`ReadTail` were unreachable,
and dropping them makes `TurnRecord`'s "nothing reads it back" true.
**Verified by running, mine** — `codex exec -s read-only --skip-git-repo-check --json "say hi"`, `codex-cli 0.153.4`, this Mac: four
stdout lines, no model named in any of them, and the fourth identical to the builder's recording, verbatim:
`{"type":"turn.completed","usage":{"input_tokens":17232,"cached_input_tokens":12928,"cache_write_input_tokens":0,"output_tokens":6,"reasoning_output_tokens":0}}`
**The builder's, carried as its claims and NOT re-verified — I re-ran no mutant:** usage onto `AgentTurnEnded.Usage` (`c05cc48`); a line
per turn in `state/agent-turns.jsonl` with `kv` totals, priced from `costs.json` or visibly unpriced (`adda9e4`); the cap pausing the
loop until local midnight (`9c29015`); card, second press, Safety ceiling, Situation and `trade status` carrying it (`0d2c66c`); item 1
RED `Assert.NotNull() Failure: Value is null` → GREEN 9/9 → mutant (parser dropped) RED 2/9; item 3 RED `Assert.Empty() Failure:
Collection was not empty` → GREEN 6/6 → mutant (`Spent >= Cap` inverted) RED 5/6, both ways. Its cross-test fix is in the tree.
**Rebase: NO conflict**, against the brief — git merged the four shared files textually, twice (`main` moved mid-leg). Both sides are
checked by name: `DashboardView.cs` keeps `_missionCost`, `MissionCost()` and `BuildSaveDailyCap` AND `_performance`, its `Root` in the
left column and its `Update`; `GatewaySchema.cs` the new `status` text AND `fill_ledger` and the `pnl` op; `TradingGateway.cs`
`Ai`/`AiState` AND `LedgerPnl`; `USER-GUIDE.md` both sections. The five commits keep their messages.
**Gate** at `14164c9`, bin/obj deleted (17 → 0), Release `--no-incremental`: 0 warnings, 0 errors, 17 projects; 11 classes 3× → 33 runs,
0 failed (meter 9, 6, 13 · life 21, 7, 14, 4 · ledger 7, 11, 3, 5); the suite to a file, one project at a time, no other test host:
390 + 615 + 261 = 1266 passed, 0 failed, 1 skipped; names vs `main` 0 removed, 42 added (1225 → 1267).
**NOT done:** no box, no ATAS, no real money, no UI run or photograph — card, armed sentence and cap control proved by words alone; not pushed, not merged, worktree kept.
