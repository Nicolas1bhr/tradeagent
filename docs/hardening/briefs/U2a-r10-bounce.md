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

## Verifier rounds 8+9 findings (appended by the manager when leg [2] reports)

_pending_
