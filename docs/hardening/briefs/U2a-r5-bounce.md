# U2a — ROUND 5 BOUNCE · Codex round-4 review of `d25dbb4`: 5 HIGH / 6 MED / 3 LOW (+ the verifier's list, below)

Raw output: `docs/hardening/records/codex-U2a-r4.txt` (read every block; each carries an exact check). **You stay the
builder (§9.3); work in `u2a-rebase-probe` on branch `u2a-rebase-probe`** (the manager moves `u2a-pipe-hardening` to
your tip). Findings are INPUTS (§4.4): each is either turned into a test → RED → fix → GREEN → mutant, or refuted by
RUNNING its check with the output quoted and a one-line reason. Silent dismissal is forbidden.

## Split decided by the manager

Three findings sit in files another unit is reworking right now (`TradingGateway.cs`, the record store): **F5** (agent
`close`/`close-all` become `Place` and never see the emergency path — single close too), **F6** (a replayed
cancel-all/close-all re-sweeps against NEW account state: the outer `rid` is unused, a fresh nonce is minted each
call) and the gateway half of **F8** (idempotent replay exists only for Place). They move to **U2c-1 round 4** (its brief
now carries them) and are recorded as HIGH-open-with-owner at U2a's integration. **Do not open `TradingGateway.cs`,
`Stores.cs` or `GatewayTypes.cs` beyond what compiles.** Everything else is yours.

## Direction on your findings

- **F1 (HIGH).** Validate the EFFECTIVE broker request id (`req.RequestId ?? req.Id`) with every rule (charset, 61-char
  budget, reserved `op-` shape). Test: `RequestId=null` + a malicious `Id` → `INVALID_REQUEST`, zero broker orders; RED today.
- **F2 (HIGH, class: non-composable deadline accounting).** `WorstCaseOrderPath` counts one 10 s write while `WriteFrame`
  gives every 8 KiB chunk its own 10 s progress budget, so a near-1 MiB order can legally take ~128 × 10 s and the 35 s
  drain abandons it: `DisposeAsync` cancels the handler and never re-awaits it before the gateway and database are
  disposed. Decision: (i) a WHOLE-FRAME ceiling exists alongside the per-chunk progress budget (generous — the point is
  that the total is bounded, not fast) and `WorstCaseOrderPath`/the drain bound derive from it; (ii) disposal RE-AWAITS
  the cancelled handler so the after-the-wire catch-all can record UNKNOWN before the store closes — a disposal that
  returns with a request unsettled is the defect. Codex's check is the acceptance: at shipped defaults, a peer accepting
  one 8 KiB chunk every 9 s, a 64+ KiB order, dispose after DISPATCHING → disposal does not return with the request
  unsettled and never logs `handlers_did_not_finish`.
- **F11 (HIGH, class with F5: intent classified below the layer that transforms operations).** Your half: every
  prerequisite read inside a risk-reducing operation inherits the emergency deadline — the pipe server's `OrdersAsync`
  before an agent cancel-all, the connector's `ResolveConnectorOrderId` Orders RPC inside `CancelAsync`, the position read
  before a close. Then the test the record lacks: cancel-all THROUGH THE REAL `GatewayPipeServer` with a stalled write
  holding the connector gate → completes ≈ 2 s with the emergency wording. The existing "agent leg" test that calls
  `AtasConnector.CancelOrderAsync` directly stays, but is no longer the evidence.
- **F3, F7, CLI half of F8 (MED, class: transport result is ad hoc).** The CLI asserts "transmitted" before the write
  completed, and common lost-reply exceptions bypass `CliReplayContract`. Adopt an explicit tri-state transport result —
  `NothingWritten` / `PossiblyWritten` / `ReplyReceived` — produced by `PipeClient` on every exit path, consumed by the
  contract; a test per exception path.
- **F4 (MED).** "Chunk completion mistaken for byte progress, so a healthy slow reader is dropped as stalled" — probe it at
  the chunk boundary with a paced reader; fix if real, refute with the run if not.
- **F9 (MED).** 32-bit sweep nonce eventually collides with durable history — widen (64-bit or a monotonic component) and
  test the collision check against the store's history.
- **F10 (LOW, but it is the operator hole's cousin — mandatory).** Refuse the reserved operator session on hello frames and
  every other frame kind, with the same seven spellings.
- **F13 (LOW).** Make the "prints request-id BEFORE sending" binary test actually observe ordering.
- **F12 / F14 (records).** The box is REACHABLE now: read `tools/README.md`, push your branch with `tools/win-push.sh`
  and run the pipe/connector integration test classes ON THE BOX (`ConnectorSendDeadlineTests`, the pipe backpressure
  tests, the replay-contract binary test) via `tools/win-run.sh`; paste the Windows output into the record. That
  converts "NOT VERIFIED on Windows" into measured, or into a named Windows defect. Do not touch the installed app,
  ATAS, or the real home. The ATAS 64-char/`op-…` acceptance check stays with the v0.1.2 step (needs the app).

## Process

Commit per finding; no `Co-Authored-By`; commit before mutants, `cp` restore, `touch`. Append `## Round 5 (build record,
<date>)` to `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael/docs/hardening/records/U2a.md` AS YOU GO — a
table `finding · real/refuted · RED · GREEN · mutant · commit`, plus the on-box run. Targeted gates per finding;
`dotnet build TradeAgent.sln` + FULL suite once at the end, counts pasted (Mac) and the Windows class results. §9.9: for
F2, answer whether a test could catch "a derived bound whose inputs changed" generically. Report: tip sha; the table one
line per finding; suite counts (Mac + box); "What I did NOT do".

## Verifier findings (appended by the manager when leg [2] reports)

_pending_
