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

## Round 4b — post-rebase fix (build record, 2026-09-03)

Worktree `ai-trading-software-for-mihael-worktrees/u2a-rebase-probe`, branch `u2a-rebase-probe`, from `e91293e`
(= `5c716aa` rebased onto `main` `9fd5eb7`). One test red, deterministically:
`ConnectorSendDeadlineTests.An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it` —
`Assert.ThrowsAny() Failure: No exception was thrown`.

### Step 0 — bisect: the rebase is NOT the cause

Verified by running the same single test in the read-only worktree `u2a-orig` (detached at the PRE-rebase tip
`5c716aa`):

```
dotnet test tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj \
  --filter FullyQualifiedName=...An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it
→ Failed  ...An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it [1 s]
  Error Message: Assert.ThrowsAny() Failure: No exception was thrown  Expected: typeof(System.Exception)
  ConnectorSendDeadlineTests.cs:line 413
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
```

**RED at `5c716aa` too — identical assertion, identical line.** So the defect is U2a's own and the round-4 "360 green"
did not cover this test as it stands. Corroborating evidence that the rebase could not have caused it: `git diff
5c716aa e91293e` touches no file on this path — the src changes are `TradingGateway.cs`, `GatewayTypes.cs`,
`Stores.cs`, `Errors.cs`, `GatewaySchema.cs`, `DashboardView.cs`, `AtasHealth.cs` (an `IAtasProbe` seam) and
`Directory.Build.props` (`Version 0.1.0 → 0.1.1`); the test exercises only `AtasConnector`, `BridgeServer` and
`LoopbackAtasAdapter`, and `ConnectorSendDeadlineTests.cs` itself is byte-identical across the rebase.

### Root cause: the fixture's premise was a throughput race, bounded in the wrong direction

The test drove contention by firing 400 concurrent 512 KiB quote RPCs at a real `BridgeServer` and assumed the send
gate would still be held two seconds later, when `EmergencyGateWait` expires. That needs the MACHINE to be slow
enough. Measured on this box with an instrumented copy of the fixture (temporary probe, deleted before the commit):

```
connected=True after 43ms
backlog created in 10ms
t=312ms after 300ms delay: done=73  faulted=0  ok=73
cancel-all: 0.710s  ex=<none>
after cancel: done=400 faulted=0 ok=400
connected=True
```

The whole 400-frame backlog drains in ≈1.02 s, so the emergency acquired the gate at **0.710 s** — inside the 2 s
`EmergencyGateWait` — and was SENT. `Rpc` therefore never reached the gate-expiry branch at all, and the test failed
on `ThrowsAny` because nothing had gone wrong. **No product code is involved:** the Busy/PeerStalled classification
was never executed by this fixture on this machine.

Two sentences: *the test asserted a mechanism (that a given volume of traffic would still be draining after two
seconds) instead of the behaviour, and that mechanism is a wall-clock race whose bound runs the wrong way — it holds
only while the box is slow. The box is faster than the box the number was taken on, so the gate was free when the
emergency asked for it and the expiry branch the test exists to pin was never entered.*

The round-4 design sentence the new assertion pins, unchanged (record above, Round 4): *"emergency gate expiry asks
whether the gate-holder moved: progressing → `Busy`, no drop, 'the bridge is busy'; stalled → drop, 'not responding'."*

### The fix — in the TEST, `tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs` only

No product file changed. `git diff --stat` against `e91293e` is one file.

1. The nested peer double `StalledBridgePeer` becomes `BridgePeer` with two factories that differ in exactly one
   thing — whether bytes are accepted: `BridgePeer.Stalled(...)` (the old behaviour, all seven existing call sites
   mechanically updated, no behaviour change) and `BridgePeer.ReadingSlowly(...)`, which after the same real
   handshake pumps the pipe at **at most 8 KiB every 200 ms** and exposes `BytesRead`.
2. The busy test now runs at shipped deadlines against `ReadingSlowly`, holding the gate with one 512 KiB order.
   40 KiB/s is a **wall-clock ceiling**: no machine can make a 512 KiB frame finish inside ~12 s, so the gate is
   still held — and still progressing — across the whole 2 s wait, on any box under any load. The bound now runs the
   right way.
3. The test asserts **its own premise** before it reads the verdict — `stuck.IsCompleted` is false (the gate really
   was held) and `peer.BytesRead` grew during the wait (the peer really was reading, i.e. this is the busy case and
   not the stalled one). This is what the old fixture lacked: it could not tell "the busy path is broken" from "the
   gate was never contended", so it reported the second as the first.
4. It also pins `EmergencyGateWait == 2 s`, as its stalled sibling already did.

Both guarantees are intact and untouched: U2b's approval re-check and one gateway clock are not on this path (no
U2b-owned file — `TradingGateway.cs`, `GatewayTypes.cs`, `Stores.cs`, `Errors.cs` — was opened); U2a's busy-vs-stalled
distinction is now pinned by a matched PAIR of fixtures that differ in one variable —
`An_emergency_cancel_all_behind_a_stalled_write_fails_fast_and_says_why` (`BridgePeer.Stalled` → dropped, "not
responding") and this one (`BridgePeer.ReadingSlowly` → `Busy`, connection kept, "the bridge is busy").
`Place`/`Modify` off the fast path is untouched.

Verified by running the whole class: `--filter FullyQualifiedName~ConnectorSendDeadlineTests` →
`Passed! - Failed: 0, Passed: 13, Skipped: 0, Total: 13, Duration: 32 s`.
Verified deterministic in isolation, three consecutive runs of the single test: `Passed! - Failed: 0, Passed: 1`,
each ≈2 s (the 2 s the emergency is designed to wait).

### §9.9 — could a gate catch this class next time?

Yes, and it is the class fix applied above rather than a new script: **a timing/contention test must assert the
condition it created, not assume it.** The sibling `Local_queueing_under_load_does_not_disconnect_a_healthy_bridge`
already did exactly this (`Assert.Contains(calls, c => c.IsFaulted)` — "the contention has to be REAL for the rest of
this to mean anything"); the busy test did not, and that is the whole difference between a fixture that fails loudly
as a fixture and one that libels the product. A cheap mechanical gate on top: a review/grep rule that any test
constructing load or a deadline race carries at least one assertion on the fixture state (not only on the outcome).
The second, orthogonal gate is the one this round paid for directly — **re-measure a suite claim after a rebase
instead of carrying it forward**, which is what turned a pre-existing red into a "the rebase broke it" hypothesis.

### Commits (branch `u2a-rebase-probe`, on top of `e91293e`) — tip **`d25dbb4`**

| sha | what |
|---|---|
| `027eb42` | Hold the send gate with a paced reader instead of hoping the box is slow |
| `1a28522` | Check the fixture's premise on the clock, not on the write it left in flight |
| `d25dbb4` | Quote the measured hold time rather than one derived from an unmeasured buffer |

No `Co-Authored-By` trailers (`git log -3 --format=%B \| grep -ci co-authored` → `0`). `u2a-pipe-hardening`
untouched. Nothing pushed, merged or rebased.

`1a28522` exists because the FIRST premise assertion was wrong in an instructive way: it asserted that the write
holding the gate had not finished, which mutant A (below) also makes false — dropping the bridge kills that write —
so a genuine product defect was reported as *"the emergency was never queued behind anything"*. The premise is now
the clock (the emergency must have waited out `EmergencyGateWait`) and the peer's byte count, neither of which a drop
can confound, and the call is made in `try`/`catch` rather than `ThrowsAny` so the premise is read BEFORE the verdict
— under `ThrowsAny` the fixture failure threw first and the premise assertions never ran at all, which is the very
case they exist for.

### Mutation — the tooth, three mutants

Each applied to a `cp` copy's original, `touch`ed, run, then restored from the `cp` copy and `touch`ed again (never
`git checkout --`); pristine `AtasConnector.cs` sha1 `ed1617fc…`, restored and re-verified identical after each.

| # | mutation | result |
|---|---|---|
| A | `AtasConnector.WriteFrame`: the busy branch always DROPS (`if (false) return SendOutcome.Busy;` → falls through to `DropStalledPeer()` + `PeerStalled`) | **RED** |
| B | the busy branch claims the frame went out (`return SendOutcome.Sent;`) | **RED** |
| C | fixture mutant — the ORIGINAL 400 × 512 KiB / real-`BridgeServer` fixture restored under the NEW assertions | **RED** |

Mutant A, quoted:

```
Error Message:
 Assert.Contains() Failure: Sub-string not found
 String:    "the bridge is not responding; 'cancel-all"···
 Not found: "busy"
 ConnectorSendDeadlineTests.cs:line 457
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 2 s
```

Mutant B, quoted — the emergency claimed Sent and then sat out the whole 10 s RPC timeout:

```
Error Message:
 the emergency took 12.01s
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 12 s
```

Mutant C, quoted — this is the §9.9 payoff: the old fixture now names ITSELF instead of saying "No exception was
thrown", which is exactly what round 4b was spent diagnosing:

```
Error Message:
 the emergency came back in 0.83s, short of the 2s gate wait — it was never queued behind
 anything, so this run measured nothing about gate EXPIRY
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s
```

Restored after each: `cp <scratchpad copy> <file>` + `touch`, `git status --short` empty, single test
`Passed! - Failed: 0, Passed: 1` and the full class `Passed! - Failed: 0, Passed: 13, Total: 13`.

### The one number the fixture rests on, measured rather than derived

The doc comment first claimed "no machine can make a 512 KiB frame finish inside twelve seconds", which was arithmetic
over a socket buffer nobody had measured. Measured with a throwaway probe at the shipped pace (deleted; `git status`
clean): **the order's last byte was accepted at 12.95 s** (524,497 bytes), against the **2 s** the emergency waits on
the gate — a 6.5× margin, and bounded from below by the peer's own `Task.Delay`, so a faster machine only reaches the
next sleep sooner. `d25dbb4` puts that measured figure in the comment.

### Gates (all at tip `d25dbb4`)

Targeted, the whole class:
```
dotnet test tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj \
  --filter FullyQualifiedName~ConnectorSendDeadlineTests
Passed!  - Failed: 0, Passed: 13, Skipped: 0, Total: 13, Duration: 31 s
```
The single previously-red test, three consecutive isolated runs: `Passed! - Failed: 0, Passed: 1` each, ≈2 s each
(the 2 s the emergency is designed to wait).

```
dotnet build TradeAgent.sln
Build succeeded.  0 Warning(s)  0 Error(s)
```

Full suite, once, `dotnet test TradeAgent.sln` (exit 0):
```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 809 ms - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s    - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 208, Skipped: 0, Total: 208, Duration: 2 m    - TradeAgent.IntegrationTests.dll
```
**391 green, 0 red.** The integration count reconciles exactly with the manager's baseline: 207 passed + 1 failed = 208
tests, the same 208, now all passing. No test was added or removed. No other timing test shifted — the class's other
twelve, including the two ten-second shipped-default tests, ran green in the same runs.

**NOT verified / disclosed:**
- The full-suite run overlapped, for its first few seconds, a shell poll loop of mine that spun without a sleep. It
  cannot have produced a false GREEN here — the new fixture's bound is the peer's wall-clock `Task.Delay`, not CPU —
  and the same test is green in three isolated no-load runs, but the overlap is on the record.
- Windows: unchanged and still NOT VERIFIED, as for every earlier round. The paced peer is a Unix-socket measurement;
  named-pipe buffer semantics on Windows remain unmeasured. The box is offline.
- No U2b-owned file was opened (`TradingGateway.cs`, `GatewayTypes.cs`, `Stores.cs`, `Errors.cs`) — so nothing to STOP
  and report. `git diff e91293e..d25dbb4 --stat` is one file:
  `tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs`. **No product code changed in round 4b.**
- The rebase itself is therefore still unverified as a whole by this round: it is proven not to have caused THIS
  failure, and the full suite is green at `d25dbb4`, but that is the extent of the claim.
