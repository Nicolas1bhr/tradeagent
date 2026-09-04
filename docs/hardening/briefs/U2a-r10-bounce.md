# U2a — ROUND 10 BOUNCE · Codex delta on round 9 (`records/codex-U2a-r9.txt`): 2 HIGH / 2 MED / 1 LOW (+ verifier r8+9, below)

**Fresh builder.** Read first: the standard's §6, `CLAUDE.md`, `records/U2a.md` "## Round 9" (skim 7–8), `briefs/U2a-r9-bounce.md`,
`records/codex-U2a-r9.txt`. Worktree `u2a-rebase-probe`, branch `u2a-rebase-probe`, tip `088c059`. Rules as every round
(commit per finding, no trailers, commit before mutants, `cp` restore + `touch`, test-name diff after every structural
edit, `## Round 10 (build record, <date>)` in `records/U2a.md` on MAIN, no git there, `dotnet build TradeAgent.sln
--no-incremental` = 0 warnings). No box unless granted. `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs`,
`GatewayTypes.cs`: read only.

## Decided split

- **F2 (HIGH) → DEFERRED-BY-DECISION to U2c-1 class C1.** A close's final offsetting `Place` is excluded from the
  emergency scope and gets fresh budgets. That is the round-4 rule ("`Place`/`Modify` never take the fast path") meeting
  the U2c-1 item "carry a `Close` intent through `ITradingConnector` so close legs are not `Place`s" — once U2c-1 lands,
  those legs inherit the scope by intent. Write it in the record as deferred with owner; do not give a `Place` the
  emergency budget here.
- **PRIOR F5 "NOT FIXED — no zero-warning build recorded"** → refuted: the round-9 record on `main` quotes
  `0 Warning(s) 0 Error(s)` from `--no-incremental`; Codex read the branch's stale snapshot. Re-run and quote it again.

## Yours

- **F1 + PRIOR 2 (HIGH, the drain, third time — close the CLASS).** The drain models a risk-reducing handler as
  `E + W`, but one `close-all` wave serialises up to four ordinary placement RPCs through `_dispatchGate`, so the real
  path is `E + 4W` (Codex: `E=30 s, W=4 s, S=5 s`, four positions → the formula gives 39 s, the path needs 51 s, disposal
  returns with unsettled requests). Rule: the drain is derived from EVERY handler's real serial chain — enumerate the
  handlers (place, modify, cancel, cancel-all, close, close-all, the reads) and for each state its serial depth in terms
  of `E`, `W`, the wave size and the settle time; the drain is the max over that table, computed from live values; a test
  per handler asserts the derived bound ≥ its measured chain at fake latency, and the close-all case at four positions
  leaves nothing unsettled after disposal. Put the table in `docs/CONTRACTS.md` — it is a release fact.
- **F3 (MED).** The public per-leg vocabulary drifted to six values (`sent-and-confirmed`, `sent-still-working`,
  `nothing-to-do`, …) and `SweepRequestIdTests.cs:395` approves all six. Decision: the per-leg set is EXACTLY five —
  `confirmed`, `rejected`, `not-sent`, `sent-not-confirmed`, `sent-still-working` — each 1:1 with a record state
  (settled / REJECTED / no record or not reached the wire / UNKNOWN + reconciliation / WORKING); `nothing-to-do` is
  allowed ONLY as the whole-operation result of a sweep with zero targets, never on a leg. Names must match
  `docs/CONTRACTS.md` (write the table there); a test deserialises every reachable reply and asserts membership.
- **F4 (MED, class: classification by record state cannot preserve wire certainty).** Pre-gate `Busy` / gate-expiry
  `PeerStalled` legs are classified UNKNOWN though never sent; disposal cancellation can leave DISPATCHING/RECONCILING
  described as `sent-not-confirmed`. Rule: the leg outcome is derived from the CONNECTOR's transport result — the same
  tri-state the CLI already uses (`NothingWritten` → `not-sent`; `PossiblyWritten` → `sent-not-confirmed` with UNKNOWN +
  `NeedsReconciliation=true`; `ReplyReceived` → by the reply: `confirmed` / `rejected` / `sent-still-working`) — and
  never from the record state alone; a leg cancelled by disposal before the wire is `not-sent`; a leg cancelled after the
  wire is `sent-not-confirmed` and its record MUST be UNKNOWN before disposal returns (the round-5 rule). Tests: pre-wire
  `Busy` → `not-sent`; gate-expiry `PeerStalled` → `not-sent`; every `sent-not-confirmed` leg has UNKNOWN +
  `NeedsReconciliation`; disposal mid-leg both sides of the wire.
- **LOW (see the raw file).** Fix or refute.

Gate and report as before; the handler table quoted in the report.

## Verifier rounds 8+9 findings (fresh Opus, on `088c059`) — VERDICT: FAIL — 0H/2M/3L · record `records/U2a-verify-r9.md`

- **F-1 (MED) = Codex F4, measured through the real pipe.** A sweep leg the SHIPPED connector refused BEFORE sending
  reads `sent-not-confirmed` with UNKNOWN + `NeedsReconciliation` — and the flag pauses all execution, including the
  retry the sentence advises. Route: `GatewayPipeServer.cs:768/:800` via `TradingGateway.cs:660-665`, which maps every
  `ConnectorTransportException` to `SettleUnknown`. Your rule is F4's (classify by the connector's transport result). If
  the transport result cannot reach the pipe server without editing `TradingGateway.cs`, expose it from the CONNECTOR
  (e.g. the exception type/property carrying `NothingWritten`, or a per-request last-transport-result the pipe server
  reads) — and if even that needs the gateway file, STOP that item and report; it becomes a U2c-1 item with your
  measurement attached.
- **F-2 (MED).** `DisposeAsync` returns with a request DISPATCHING, `needs_reconciliation=0` and `handlers_did_not_finish`
  NOT logged, with a connector that honours its cancellation token (`GatewayPipeServer.cs:1155-1190` +
  `TradingGateway.cs:696-700` vs `:481`) — nothing ever reconciles that row; this refutes round 9's "only a call that
  ignores its token produces one". Decision: the settlement of a cancelled handler is the GATEWAY's (U2c-1: its startup
  sweep turns every DISPATCHING row into UNKNOWN + paused at restart, and its cancellation path must settle) — record
  it as deferred with owner U2c-1 with the verifier's measurement. YOUR half: disposal never returns SILENTLY — it waits
  the full derived drain before cancelling, and if a request is still unsettled when it returns it logs
  `handlers_did_not_finish` with the request id at error and the record says so; a test drives the honouring connector
  and asserts the log line.
- **F-3 (LOW).** `Classify` keeps a catch-all `_ => LegOutcome.NotConfirmed` (`:800`) while `Describe()` had its catch-all
  removed for exactly that reason — make the switch exhaustive; a new `ExecutionState` must fail to compile, not map.
- **F-4 (LOW) = Codex F1** (the close-all wave) — covered by the drain table above.
- **F-5 (LOW).** `A_five_order_sweep…` passes with `attempted = 0` (at 1 s/leg every leg is `not-sent`) — the test never
  exercises "which sent, which confirmed" in one answer. Rewrite it so one answer carries a MIX (some confirmed, some
  not-sent, one sent-not-confirmed) and asserts each by name.

Held (the verifier's own fixtures): one deadline per operation 2001 ms; the five-order sweep 2004 ms with every order
named; idle shutdown 1 ms; the override clamps both ways; `_abandoned` → 0 on both grace exits; 466 green; 13/13 mutants
bit; r7 regressions reproduce. NOT verified: round 9's seven commits have never been built or run on the box — the
round-10 builder gets ONE box run at the end of the round (push, hash-verify, pipe classes + full suite, re-hash).
