# U2a — ROUND 11 BOUNCE · Codex delta on round 10 (`records/codex-U2a-r10.txt`): 12/15 priors FIXED; 1 HIGH-counted deferral, 2 new MED, 1 LOW

**Fresh builder.** Read first: the standard's §6, `CLAUDE.md`, `records/U2a.md` "## Round 10" (skim 8–9), `briefs/U2a-r10-bounce.md`,
`records/codex-U2a-r10.txt`, `docs/CONTRACTS.md`. Worktree `u2a-rebase-probe`, branch `u2a-rebase-probe`, tip `c00fa08`.
Rules as every round (commit per finding, no trailers, commit before mutants, `cp` restore + `touch`, test-name diff
after every structural edit, `## Round 11 (build record, <date>)` in `records/U2a.md` on MAIN, no git there,
`dotnet build TradeAgent.sln --no-incremental` = 0 warnings). `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs`,
`GatewayTypes.cs`: read only.

## Deferred by decision (write in the record, do not build)

- **PRIOR 2 "disposal still returns with DISPATCHING unsettled"** and **PRIOR V-F1 "the never-sent leg still ends UNKNOWN
  + flagged and pauses execution"** — both are the GATEWAY's half (`TradingGateway.cs:696-700` cancellation path;
  `:660-665` every `ConnectorTransportException` → `SettleUnknown`), already routed to U2c-1 class C4 with the round-10
  measurements. U2a's halves are done (not-silent disposal; `not-sent` on the wire side). Codex counts them; the record
  states the owner and the exact line each fix lands on.

## Yours

- **F2 (MED — the DANGEROUS direction).** A completed mutation write records no transport state until a reply
  (`AtasConnector.cs:827`), so caller cancellation during the reply wait leaves the transport result null and the mapper
  can classify a FULLY SENT leg as `not-sent` — the owner is told nothing was sent when an order may exist. Rule: the
  transport state is recorded at the moment the frame is fully written (`PossiblyWritten`), before any reply wait; a
  null transport result NEVER maps to `not-sent` — null is `sent-not-confirmed` (fail toward reconciliation). Codex's
  check (peer reads the whole cancel frame, withholds the reply, caller cancels → `PossiblyWritten`) plus the
  null-maps-to-not-confirmed test; mutants both ways.
- **F1 (MED).** Cancellation while waiting for the send gate bypasses the `NothingWritten` arm and the outer catch
  records `PossiblyWritten` (`:739` → the catch at `:1121`), forcing reconciliation for a frame that never left. Rule:
  cancellation BEFORE gate acquisition is `NothingWritten`; Codex's check (hold the gate with an oversized call, start
  `CancelOrderAsync`, cancel before acquisition → `NothingWritten`).
- **PRIOR R9-F4 PARTIAL.** Definite-state arms still bypass transport, and DISPATCHING/RECONCILING remain eligible for
  `sent-not-confirmed` (`GatewayPipeServer.cs:906`). Rule: every arm consults the transport result first; a DISPATCHING
  or RECONCILING record with `NothingWritten` is `not-sent`; with `PossiblyWritten` it is `sent-not-confirmed`; with a
  definite reply it is the reply's word. Table in the record; a mutant per arm.
- **F3 (LOW).** The "exhaustive" drain table omits four handled operations (`GatewayPipeServer.cs:211`) — add them
  (depth stated), and make the coverage test enumerate the handler set from the dispatcher, not from a hand list.

## Box grant — at the END of the round, ONE ssh session (you hold the only access)

Round 10's tip `c00fa08` never ran on the box, and a round-7 test
(`An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop`) failed in the full Windows suite and passed
alone. Push (`tools/win-push.sh`), VERIFY the tree by hash (five changed files + the `.cs` count) before running, build
`--no-incremental`, run the pipe classes, the FULL suite TWICE, and that single test three times alone; re-hash after;
paste everything. If the test flakes: read it and rate it — a premise not asserted under load (the round-4b pattern) or
a real Windows timing hole — with the exact fix; fix it in this round if it is the test's premise. Do not touch the
installed app, ATAS or the real home.

Gate and report as before; suite counts Mac + box (both runs); the flake verdict; "What I did NOT do".
