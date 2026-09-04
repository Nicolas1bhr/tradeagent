# U2a — ROUND 9 BOUNCE · Codex delta on round 8 (`records/codex-U2a-r8.txt`): 0 HIGH / 2 MED / 3 LOW; 10/11 priors FIXED

**Fresh builder** (the round 4–8 builder's session is gone). Read first: the standard's §6, `CLAUDE.md`, `records/U2a.md`
"## Round 8" (and skim rounds 5–7 for the unit's conventions), `briefs/U2a-r8-bounce.md`, `records/codex-U2a-r8.txt`.
Worktree `u2a-rebase-probe`, branch `u2a-rebase-probe`, tip `5624cd1` (+ anything the round-8 finisher committed — check
`git log`). Rules as every round: commit per finding, no `Co-Authored-By`, commit before mutants, `cp` restore + `touch`,
diff test-method names after every structural edit, checkpoint `## Round 9 (build record, <date>)` in `records/U2a.md`
(MAIN worktree, no git there). No box unless granted. Do not open `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs`,
`GatewayTypes.cs` beyond what compiles.

## Direction

- **PRIOR 2 (MED, still open).** `GatewayPipeServer.cs:161` derives the drain from THREE serial RPCs, but a cold placement
  issues FIVE; and an explicit drain override can still permit disposal with an unsettled request. Rule: the chain
  length comes from the handler's real maximum (count it from the code paths, state which path is longest and why in the
  record; a test that asserts the derived bound ≥ the longest measured chain at fake latency); the override cannot
  shorten the bound below the composite — either it is removed or it only lengthens. Disposal never returns unsettled
  (round-5 rule) — Codex's cold-placement check is the acceptance.
- **F1 (MED, class: the per-leg outcome vocabulary is not 1:1 with the record).** `GatewayPipeServer.cs:748` maps a
  definitive rejection AND a pre-send failure to `sent-not-confirmed`: `RefuseCancel=1` → "sent-not-confirmed" while the
  record is REJECTED; target resolution expiring before `TryCreate` → "sent-not-confirmed" with `attempted=0` and no
  record. Rule: four outcomes, each mapping to exactly one record state — `confirmed` (settled), `rejected` (definite
  broker refusal, REJECTED), `not-sent` (never reached the wire, no UNKNOWN needed, the owner told which orders are
  still working), `sent-not-confirmed` (UNKNOWN + reconciliation). Both of Codex's checks become tests; a mutant per
  mapping arm.
- **F2 (LOW).** A disconnect or disposal during the late-answer grace leaves the request permanently in `_abandoned`
  (`AtasConnector.cs:946`) — clear on drop/dispose; test the dictionary count.
- **F3 (LOW).** `FakeConnector.cs:69` takes the MAX of two latency faults that lines 78–79 execute serially, so the
  simulator can exceed its own emergency deadline and reported worst case — sum them; the deadline tests still pass.
- **F4 (LOW).** `Left` (`AtasConnector.cs:1158`) returns a fresh 1 ms budget after an absolute deadline has expired —
  return zero/expired; pin.

## Gate and report

Targeted classes, then `dotnet build TradeAgent.sln` + FULL suite once on the Mac (≈5–6 min; output to a file). Report:
tip sha, per finding RED → GREEN → mutant, the longest chain named, suite counts, "What I did NOT do".
