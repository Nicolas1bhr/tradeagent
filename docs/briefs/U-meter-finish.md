# U-meter-finish — carry the built meter over the landed ledger, gate it, and write its report

Fresh fixer on Opus. Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/briefs/U-meter.md`, and the `U-life` and `U-ledger`
sections at the end of `BUILD-STATUS.md`. `export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`; no
`timeout`. The worktree `~/Projects/ai-trading-software-for-mihael-worktrees/meter` exists on branch `u-meter` with
FOUR commits from a builder that stopped before its gate, plus ONE uncommitted edit to `TurnMeter.cs`. **Read the
branch first: the work is there.** You write no new behaviour beyond what finishing it needs.

The builder's own findings, to carry into the report as its claims (verify what you can, mark the rest as the
builder's):
- Usage is parsed from the runtime's stream onto `AgentTurnEnded.Usage` (`c05cc48`); per-turn lines in
  `state/agent-turns.jsonl` plus `kv` totals, priced from `costs.json` or visibly unpriced (`adda9e4`); the cap pauses
  the loop until local midnight (`9c29015`); the card, the grant's second press, the Safety page ceiling, the Situation
  line and `trade status` carry the cost (`0d2c66c`).
- The codex `--json` usage event, measured on this Mac with `codex-cli 0.153.4` and `codex exec -s read-only
  --skip-git-repo-check --json "say hi"`, fourth and last stdout line, verbatim: `{"type":"turn.completed","usage":
  {"input_tokens":17232,"cached_input_tokens":12928,"cache_write_input_tokens":0,"output_tokens":6,
  "reasoning_output_tokens":0}}`. No model name in the stream, so `costs.json` names the model per runtime.
- RED/GREEN/mutant: item 1 RED `Assert.NotNull() Failure: Value is null` → GREEN 9/9 → mutant (parser dropped) RED
  2/9; item 3 RED `Assert.Empty() Failure: Collection was not empty` (a Situation composed past the cap) → GREEN 6/6 →
  mutant (`Spent >= Cap` inverted) RED 5/6, both polarities.
- A cross-test collision fixed on the way: a real child started in the agent's role moved the sticky
  `AgentPresence.Shared` and turned three `MaterialLedgerTests` red while each class passed alone; the probe now passes
  its own register, as `MaterialScanner` already does.

1. **Judge the uncommitted `TurnMeter.cs` edit on its merits** — it deletes `LastUnpricedReason` and `ReadTail` (27
   lines that read the turns file's tail for the last unpriced reason) so `WhyNoPrice` falls back to `probe.Unpriced`
   only: if that is the finished shape and the tests agree, commit it with a one-sentence message; if not, drop it.
2. **Rebase** `u-meter` onto `main` (which now carries `U-ledger`: a `fill` table, `Pnl.cs`, `PerformanceCard.cs`, and
   changes in `TradingGateway.cs`, `GatewaySchema.cs`, `DashboardView.cs`, `USER-GUIDE.md`). Resolve conflicts keeping
   BOTH sides' behaviour, inside the rebase so the builder's commits keep their messages.
3. **Gate** at the rebased tip: Release `--no-incremental` → 0 warnings; the meter's classes (`TurnMeterTests`,
   `MissionCapTests`, `MissionCostSurfacesTests`) and the life and ledger classes 3× each; the full suite once in
   Release to a file, one project at a time, no other test host (`pgrep -fl 'testhost.dll' | grep -v pgrep`); names
   vs `main` 0 removed. A red is reported, not patched away — except a conflict you introduced in step 2.
4. **Append the `## Report`** to `docs/briefs/U-meter.md` (≤20 lines): the builder's claims above marked as such, the
   usage event verbatim, what you committed or dropped, the two-sided conflict resolutions, the gate counts, NOT done.
   Commit it, no trailers. Do NOT push, merge, or remove the worktree.

Your final message to the manager is the report and the tip sha, nothing else.
