# U2a — ROUND 12 BOUNCE · verifier rounds 10+11 on `120c739`: FAIL 0H/2M/4L; Codex r11: 0H/1M(refuted)/1L

**Fresh builder.** Read first: the standard's §6, `CLAUDE.md`, `records/U2a.md` "## Round 11" (skim 9–10),
`records/U2a-verify-r11.md` (its probes on `u2a-verify-r11-probes` at `93f6ec0` — reuse them), `records/codex-U2a-r11.txt`,
`docs/CONTRACTS.md`. Worktree `u2a-rebase-probe`, branch `u2a-rebase-probe`, tip `120c739` (491 green Mac + box).
Rules as every round (commit per finding, no trailers, commit before mutants, `cp` restore + `touch`, test-name diff
after every structural edit, `## Round 12 (build record, <date>)` in `records/U2a.md` on MAIN, no git there,
`dotnet build TradeAgent.sln --no-incremental` = 0 warnings). `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs`,
`GatewayTypes.cs`: read only. **This is the last round before integration; the box run at the end is yours (ONE
session: identity by hash, build, pipe classes, full suite once).**

## Refuted (write in the record with the reason)

- **Codex r11 "PRIOR F2 NOT FIXED"** — the verifier measured both halves: the three empty-transport legs
  (nothing-to-close, resolution-expires, parked-for-approval) start ZERO mutating connector calls, and inside both
  shipped connectors null-after-attempt is unreachable (`Attempt()` is the first statement of `Rpc` for all six mutating
  `BridgeOps`); applying `null → sent-not-confirmed` blindly turns five true tests RED. The attempt-marker mechanism
  satisfies the rule's intent for the shipped connectors. F-2 below closes the third-party gap.

## Yours

- **F-1 (MED).** `GatewayPipeServer.DisposeAsync`: the `handlers_did_not_finish` sentinel sits inside
  `if (handlers.Length > 0)`, and `_handlers` holds live CONNECTION handler tasks that self-remove on completion,
  read after step 2 disposes the connections — agent disconnects, then the app closes → disposal returns in 3 ms
  with a DISPATCHING row and nothing logged. Rule: the DISPATCHING query and the sentinel run UNCONDITIONALLY at the
  end of disposal; the verifier's agent-disconnected probe is the acceptance (log at error with the request id; the
  control with the agent connected unchanged). Mutant: put the guard back → RED.
- **F-2 (MED, contract).** `not-sent` is an assurance a connector must opt into (the attempt marker) and the
  `ITradingConnector` interface never states it; a third-party connector written to the public contract that really
  cancels at the broker and never calls `TransportLedger` reports `not-sent` / `attempted:0` — and the `transport`
  field is omitted from the JSON exactly then. Rule, U2a's half: (a) the PIPE SERVER knows which of a leg's own steps
  are mutating — a mutating step that was DISPATCHED and comes back with a null transport result is classified
  `sent-not-confirmed` (PossiblyWritten by the pipe server's own knowledge), while a leg that never dispatched a
  mutating step stays `not-sent` (the three legs keep their word); (b) the obligation is written on `ITradingConnector`
  (doc comment) and in `docs/CONTRACTS.md`; (c) `transport` is emitted as explicit `null`, never omitted. Test: a fake
  connector that performs a mutating call WITHOUT marking an attempt → `sent-not-confirmed`; the three legs still
  `not-sent`; mutant per half. The gateway-side marking (the better fix) is routed to U2c-1 — write it there.
- **L-1.** `AtasConnector._pending` leaks on caller cancellation (0 → 1 → 1, `AwaitingLateAnswer = 0`, a late answer
  counted nowhere): clear on cancellation and count the late answer; the verifier's measurement is the test.
- **L-2.** A table row bounds the connector chain, not the handler, and `S` is added once and settable to zero
  (`cancel-all` 917 ms vs a 900 ms row at W=300/E=900/S=50): bound the HANDLER (add the pipe-server overhead term, or
  a floor for `S`), and state it in CONTRACTS.md.
- **L-3.** The coverage test's candidate set comes from `Core.Ops` literals, not the dispatch switch — derive it from
  the switch (or assert the two sets are equal).
- **L-4.** The simulator's deadline sentence is not op-aware (a `not-sent` leg carries "it is not known whether it
  acted"): make the fake's sentence agree with the leg's word.
- **Codex r11 LOW** (`ConnectorSendDeadlineTests.cs:848` captures its verdict before the liveness judge runs): lift one
  12-phase liveness probe from `u2a-verify-r9-probes` into the suite so a `PeerAnsweredSince` regression is observed.

## Gate and report

Targeted classes; `dotnet build TradeAgent.sln --no-incremental` (0 warnings) + FULL suite once on the Mac; the box
session (identity by hash before/after, build, pipe classes, full suite once). Report: tip sha, per item RED → GREEN →
mutant, suite counts Mac + box, "What I did NOT do".
