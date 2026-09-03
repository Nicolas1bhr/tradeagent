# U2a — ROUND 6 BOUNCE · verifier round 5 on `0909ada`: FAIL 1H/2M/1L (Codex delta pending the quota reset)

Record: `records/U2a-verify-r5.md`. **Same builder, same worktree (`u2a-rebase-probe`).** Findings are INPUTS; each
becomes a test → RED → fix → GREEN → mutant, or is refuted by running its check.

## Decided split

- **F-A (HIGH) → U2c-1 round 4, class C.** The operator's Close All (`DashboardView.cs:544` →
  `TradingGateway.OperatorCloseAllAsync:734` → `GetPositionsAsync`) stays on the ordinary deadline: same stalled bridge,
  same held gate, agent `close-all` **2018 ms** with the owner sentence vs operator Close All **9759 ms** with "could not
  reach the ATAS bridge". The method is the one U2c-1's press redesign rewrites, so the scope is opened there at the
  gateway level (every caller inherits it). Recorded as HIGH-open-with-owner at U2a's integration; `main` today has no
  fast path for anyone, so integrating U2a does not regress the button. **Do not touch `TradingGateway.cs` or
  `DashboardView.cs`.**

## Yours

- **F-B (MED).** The reply-timeout liveness rule keys on frames-in only (`PeerMovedSince`, `AtasConnector.cs:831`); a
  peer that accepted ZERO bytes of our frame but still heartbeats (the heartbeat runs on its own `Task.Run`,
  `BridgeServer.cs:251`, independent of the read loop a freeze wedges) is KEPT and told "busy" — kept 6 of 12 runs at
  the shipped 5 s interval. Rule: liveness for a pending emergency frame keys on WRITE progress
  (`_lastWriteProgressAt` already holds it): no byte accepted within the window → stalled → dropped, heartbeats
  notwithstanding; bytes accepted → busy → kept. Both directions; 12/12, not 6/12.
- **F-C (MED).** Mutant W3 (`PipeClient.cs:71` read-failure path `PossiblyWritten` → `NothingWritten`) survives all 238
  integration tests; consequence: `RecoveryLine` null, `reply_lost:false`, a frame that provably left the process
  becomes a fresh proposal with a new id — a second real order. Write the test that makes W3 bite (the read-failure
  path yields `PossiblyWritten` and the recovery line names the id).
- **F-D (LOW).** A prerequisite READ inside the scope inherits an order's wording ("'accounts' is NOT confirmed … check
  your positions and orders in ATAS"): give reads their own sentence (nothing was placed; the bridge did not answer).

## Process

As before: commit per finding, no trailers, commit before mutants, `cp` restore + `touch`; append `## Round 6 (build
record, <date>)` to `records/U2a.md` in the main worktree AS YOU GO; targeted `ConnectorSendDeadlineTests` +
`CliReplayContractTests`, then `dotnet build TradeAgent.sln` + FULL suite once (Mac); run the two classes on the box.
Report: tip sha, per finding RED → GREEN → mutant, suite counts, "What I did NOT do".
