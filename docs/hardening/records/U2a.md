# U2a — gateway pipe hardening, operator-context hole, connector deadlines, cancel-all ids, replayable request id, emergency fast path

Branch `u2a-pipe-hardening` @ **5c716aa** (base 283d942, 21 commits; full messages in `commits-u2a-pipe-hardening.md`).
Tier 1. Reconstructed 2026-09-03 from the session transcript; the round build records, verify records (1465 lines) and
mutation tables were lost with the scratchpad. Suite at tip: **360 green** (298 at base), App builds.

## Why it exists

- **Codex finding 4 on 283d942, confirmed from source by the manager:** `GatewayPipeServer.cs:141` built
  `new AgentContext(req.Session)` from the client string, `IsOperator => SessionId == "operator"`, and the CLI copied
  `TRADEAGENT_SESSION` into the frame — `TRADEAGENT_SESSION=operator trade buy …` skipped LIVE_CONFIRM parking (G19)
  and the kill switch (G5). Proven over the pipe by the verifier (a `session:"operator"` buy FILLED with STOP pressed).
- The agent pipe was created with 0,0 buffers and no write deadline (the same class as the 2026-09-01 bridge freeze);
  `BridgeServer.Subscribe` had no unsubscribe; the connector's writes to the bridge had no deadline before the RPC
  timeout started; `cancel-all` derived per-order ids `{rid}-{i}` that collided with agent ids and misreported.

## What the branch does (by round; each fix red-first and mutation-proved on its round)

**Round 1 (a0aa1a7).** `AgentContext.IsOperator` is `private init`, only the static `Operator` sets it, the public
one-argument ctor can never yield operator; the pipe refuses the reserved session string with INVALID_REQUEST + an
engineering-log line. Pipe buffer 8192 + write deadline that drops only the stalled peer (`peer_stopped_reading`);
near-1 MiB legal reply still round-trips. `Subscribe` unsubscribes on dispose. Connector send path gets a deadline.
**Round 2 (d7597d5).** Replay contract: the CLI prints `request-id:` BEFORE sending and distinguishes "nothing sent"
from "reply lost — re-run with --request-id"; the drop event carries the id; AGENTS.md states the rule. The write
deadline measures PROGRESS (8 KiB chunks) instead of elapsed time (a 79 KiB/s reader was being dropped at 10.1 s).
Handler tasks are tracked and drained with two tokens (disposal no longer aborts an in-flight order — measured
`DisposeAsync returned in 15 ms` before the fix). Connector: `Sent` / `PeerStalled` (drop) / `Busy` (fails only that
caller); deadline starts after the gate; cancellation mid-write drops instead of wedging the shared StreamWriter. Tests
at shipped defaults (10 s); `cancel-all` behind one stalled write measured **9.76 s** (verifier: 9.81 s) — the round-1
record's "0.90 s" was a test-value figure and was corrected. `AgentContext` became a sealed class (no `with`; the clone
`AgentContext.Operator with { SessionId = "x" }` had kept `IsOperator = true`); seven spellings of the reserved word
refused over the pipe.
**Round 3 (f518251).** Drain bound derived from the connector's worst path (`WorstCaseOrderPath` = 10+10+10 s, drain
35 s; a 28 s order settles FILLED). Minted ids `op-{nonce}-{intent}-{index}` with `[A-Za-z0-9-]` enforced on incoming
ids at the pipe (the previous `#` separator was being minted into a client order id sent to the broker). CLI replay
contract extracted to `CliReplayContract` and tested as functions and by running the real binary. `RefuseCancel`
one-shot fault in `TradeAgent.Connectors.Fake` so the "cancelled counts attempts" mutant bites alone. **Decision
(manager): `EmergencyGateWait` 2 s** for cancel-all/close, then drop + indefinite failure with an owner-readable reason.
**Round 4 (5c716aa).** `IsEmergency` → `IsRiskReducing` keyed on INTENT (`Cancel`/`CancelAll`/`Close` whoever asked;
`Place`/`Modify` never) — the agent's legs were measured at 9707 ms vs the operator's 2006 ms. Connector writes chunked so
progress is observable; emergency gate expiry asks whether the gate-holder moved: progressing → `Busy`, no drop, "the
bridge is busy"; stalled → drop, "not responding" (round 3 had dropped a merely busy bridge: 1500 × 900 KiB RPCs → 2.01 s
drop). Client-order-id budget 64 − `TA-` = 61 enforced at the pipe; W2/W3 caps tested; `CONTRACTS.md` records the
charset/length restriction on `--request-id` (a release-note fact).

## Verification history

| Round | Opus verifier | Codex |
|---|---|---|
| 1 | FAIL 1H/4M/1L — replay id lost on drop (HIGH); throughput-floor deadline; cancellation strands a writer; DisposeAsync tracks pipes not handlers; no test at shipped values | 3H/1M/1L — same four + `with` clone |
| 2 | FAIL 0H/4M — drain 5 s < 20 s worst path; `#` minted into ClientOrderId; CLI half untested; R7 needs a fake knob | — |
| 3 | FAIL 0H/2M — agent-side cancel-all not fast-pathed; emergency expiry drops a busy bridge | — |
| 4 | **NOT RUN** (leg killed while writing probes) | — |

## Open at handoff

- Verify round 4 (targeted): the intent-based classification measured ALONE per caller on its own stalled bridge (the
  builder found that measuring both together let the button's drop free the leg); the progress-aware expiry against the
  1500 × 900 KiB saturation case (connection must be KEPT, UNKNOWN within ~2 s with the busy message); the 61-char
  budget with the CLI's own ids as positive control; suite stability at 2 m 01 s under load.
- Known gap (deliberate, → U2c-2): the agent's `close-all` legs are offsetting `Place`s and get no fast path; fixing it
  means carrying intent through `ITradingConnector` (TradingGateway is another unit's file).
- **NOT VERIFIED on Windows (all rounds):** named-pipe buffer semantics, the handle-dispose killing an accepted write,
  the no-buffer stall (mutant B4 cannot bite on macOS); and two guesses about ATAS on the same open question — does ATAS
  accept the `op-…` id shape, and is 64 at or under its real client-order-id limit. One `close-all` on the box settles
  both. The integration suite runs the shipped-default tests (≈2 min); if that becomes a problem the answer is a
  slow-test category, not shorter deadlines.
- Integrate FIRST among the open units (disjoint files from U14; U2c-1/U2d touch `GatewayTypes.cs` too but different
  regions).
