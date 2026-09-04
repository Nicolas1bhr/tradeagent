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
- **Windows — rewritten 2026-09-04 (Codex PRIOR 12 / PRIOR 14); the sweeping "NOT VERIFIED on Windows (all rounds)"
  that stood here was out of date.** What IS measured, on a tree proven identical to the builder's by SHA-256 before
  and after the run (round 6's section, and again in round 7): the pipe and connector classes and the whole suite pass
  on the box at shipped defaults — backpressure drops, the shutdown drain, the emergency classification, and the
  paced-peer fixture whose premise needs the named-pipe buffer to be far smaller than a 512 KiB frame. The
  handle-dispose kill of an accepted overlapped write is exercised by every test that asserts a drop, since the drop
  depends on it. **What is still NOT verified:** mutant **B4** (the no-buffer stall) has been run by nobody on either
  platform, so the 8 KiB buffer is unproven BY MUTATION even though the tests that need it pass; and **ATAS's real
  client-order-id limit and whether it accepts the `op-…` shape**. The old claim that "one `close-all` settles both" is
  wrong and is withdrawn: a generated sweep id is about 23 characters, so it can demonstrate the CHARSET and nothing
  about the 64-character boundary. Settling that needs a deliberate probe at v0.1.2 — one order at exactly 64 and one
  at 65, read back from ATAS history after a restart.
- The integration suite runs the shipped-default tests; if that becomes a problem the answer is a slow-test category,
  not shorter deadlines.
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

## Round 5 (build record, 2026-09-03)

Bounce on `d25dbb4` from Codex round 4 (5H/6M/3L, `records/codex-U2a-r4.txt`) and the Opus verifier
(FAIL 2H/1M/1L, `records/U2a-verify-r4.md`). Same worktree `u2a-rebase-probe`, same builder (§9.3).
F5, F6 and the gateway half of F8 were split to U2c-1 round 4 by the manager and are NOT touched here;
`TradingGateway.cs`, `Stores.cs` and `GatewayTypes.cs` are not opened.

**Windows box reachable this round** (`tools/win-state.sh` → tailscale up, `DESKTOP-K8VRIT9`,
session Active/console, desktop live, ATAS installed+running, `C:\ta\repo` present; exit 3 = no UI
agent, console work only — which is all a test run needs). On-box results at the end of this section.

| finding | real / refuted | RED | GREEN | mutant | commit |
|---|---|---|---|---|---|
| **F1 / V1** (H) effective id `RequestId ?? Id` unguarded | **real** | 9 failed (6 charset+length, 3 reserved prefix), each with the order on the broker | 23/23 `SweepRequestIdTests` | guard the optional field again → **RED 9** | `b9d2f5a` |
| **F10** (L, mandatory) reserved session accepted on hello | **real** | 8 failed — all 8 spellings answered `ok=true` on the first frame | 22/22 `OperatorContextTests` | hello skips the tripwire again → **RED 8** | `d88ebd0` |
| **F9** (M) 32-bit sweep nonce collides with durable history | **real** | forced collision left order `FB-2` WORKING while the sweep reported it cancelled | 25/25 `SweepRequestIdTests` | drop the collision check → **RED 1**; narrow the nonce to 8 hex → **SURVIVED** (see note) | `1af35c0` |
| **V3** (M) ordinary `SendOutcome` sentences unread; M14 survived | **real** | M14 (sentences swapped) survived the suite in round 4 | 13/13 class | M14 re-run → **RED 1** (`Local_queueing_under_load…`, `Assert.Contains() Failure: Filter not matched`) | `ba66916` |
| **V2** (H, new) emergency bounded only at the gate | **real** | idle stalled bridge, free gate: 10005 ms, "ATAS did not answer", connection UP | 17/17 class | reply wait → ordinary timeout **RED 2**; never drop **RED 1**; always drop **RED 1** | `040add3` |
| **F4** (M) chunk completion mistaken for byte progress | **real, measured** | 1 KiB/800 ms: peer accepted 2048 B during the window and was DROPPED as "not responding" | 17/17 class | chunk back to 8192 → **RED 1** | `60ac33e` |
| **F11** (H) prerequisite reads served the ordinary deadline | **real** | cancel-all through the REAL gateway on a stalled bridge: **9.77 s** | 18/18 class, ≈2 s | scope never opened → **RED 1** (9.77 s); `place` inside an open scope still takes 10 s | `8f983ac` |
| **F2** (H) non-composable deadline accounting | **real** | (i) write ran **102.84 s** on a steadily-progressing peer; (ii) `handlers_did_not_finish` at **error** for a handler that would have settled | 32/32 both classes | no ceiling → **RED 1** (102.84 s); no re-await → **RED 1** | `5eb6f44` |
| **F3 / F7 / F8-CLI** (M) transport result is ad hoc | **real** | `trade buy` against a service that vanished after the handshake exited **134 (SIGABRT)**, unhandled, with the recovery JSON already printed | 9/9 `CliReplayContractTests` | drop the truncated-reply catch → **RED 1**; claim `NothingWritten` for every failure → **RED 2** | `788832a` |
| **F13** (L) the "id before sending" test did not observe ordering | **real** | announcement moved to the failure path → **RED 1** | 9/9 | same mutant is the proof | `0909ada` |
| **V4** (L) `MaxRequestIdChars` literal mutant survives | **noted, no action** | — | — | the verifier's own finding: equivalent today (M13 and M12+M13 both RED), and the brief asks for none | — |

**F7's test found a second crash while it was being written**, and it is the same defect one line
later: `Program.cs` holds the client in an `await using`, so disposal runs AFTER the catch has chosen
an exit code and printed the JSON — and flushing a `StreamWriter` into a pipe whose far end has gone
throws `IOException` from outside every handler. Measured: exit **134**. `PipeClient.DisposeAsync` now
swallows transport failures. Recorded because it was not in either review's list.

**§9.9 for F2 — can a gate catch "a derived bound whose inputs changed" generically?** Partly, and the
useful half is cheap. What CANNOT be caught generically is the actual defect here: `WorstCaseOrderPath`
was arithmetic over the wrong TERM — it counted a per-chunk progress budget as if it bounded a whole
write — and no script can know that `WriteTimeout` is reset by every chunk while `FrameTimeout` is not.
That is a reading of the code, and it took a second model to do it. What CAN be caught, and now is: the
composition itself. `HandlerDrainTimeout > connector.WorstCaseOrderPath` is asserted from the LIVE
values rather than from the numbers in the comment, so changing any input moves the assertion instead
of silently invalidating a document — and the round-4 verifier's M13 (`TA-` → `TA-v2-`) already proved
that shape bites. The generalisable rule, and it is the same one as round 4b's: **a number that a
comment derives must be re-derived by an assertion from the same inputs, in the same expression the
comment describes.** Every constant this unit reasons about now has one (`WriteTimeout`, `FrameTimeout`,
`EmergencyDeadline`, `HandlerDrainTimeout`, `MaxRequestIdChars`).

**F9 note (honest, in the shape of the verifier's own M12 entry).** Widening the nonce and adding the
collision check are not independent: once a sweep asks the store whether the id it is about to mint is
already in history, ANY nonce width is correct, so narrowing it back to 8 hex survives the suite. The
width is now defence-in-depth and the check is the property under test. Recorded because a surviving
mutant is a fact, not because it is a defect.

**F4 was measured before it was believed.** Codex's stated numbers (1 KiB every 400 ms) land on the
SAFE side on macOS, so a drain sweep against the 8 KiB chunk found where the boundary actually sits:

| peer drain | bytes accepted during the 2 s window | verdict at chunk = 8 KiB |
|---|---|---|
| 2.50 KiB/s (1 KiB / 400 ms) | 5120 | busy, connection kept |
| **1.25 KiB/s (1 KiB / 800 ms)** | **2048** | **"not responding", DROPPED** |
| 0.63 KiB/s (1 KiB / 1600 ms) | 1024 | "not responding", DROPPED |

A peer that took two kilobytes off us while we watched, and was still reading when we hung up on it,
was told it had stopped responding — 9e50559's defect one layer down, in the resolution of the signal
that commit added. The chunk is now 1 KiB, which moves the boundary from 4 KiB/s to 512 B/s. It cannot
be removed, only moved, and that is stated in the source rather than left implicit.

**V2's liveness rule, and the two things it is deliberately NOT keyed on.** Not on the write's own
progress: the kernel accepting bytes means the socket buffer had room, not that anything read them —
an 8 KiB buffer swallows a whole emergency frame while the far end is a corpse. Not on heartbeats
specifically: `BridgeServer.HeartbeatInterval` is **5 s** (read from source), so a healthy connection
is routinely silent for longer than an emergency waits and a heartbeat-in-the-window rule would drop
healthy bridges. It is keyed on ANY frame arriving during the caller's window. The trade, stated: a
bridge that is alive but wedged on this one operation is dropped and redialled, which costs a
reconnect — and a reconnect is the remedy for a wedged connection and the only thing that makes the
retry the failure advises worth making.

**`EmergencyGateWait` is renamed `EmergencyDeadline`**, because it is no longer a gate wait. Callers
in the suite and the round-4 text in this record above refer to the old name; the number (2 s) and the
decision behind it are unchanged.

### Commits (branch `u2a-rebase-probe`, on top of `d25dbb4`) — tip **`0909ada`**

| sha | finding | what |
|---|---|---|
| `b9d2f5a` | F1 / V1 | Guard the request id that is used, not the field that may be absent |
| `d88ebd0` | F10 | Refuse the reserved operator session on the hello frame as well |
| `1af35c0` | F9 | Make a repeated sweep nonce harmless instead of merely unlikely |
| `ba66916` | V3 | Read the ordinary transport sentences, and reach the branch that writes them |
| `040add3` | V2 | Bound an emergency end to end, and let the peer decide the connection's fate |
| `60ac33e` | F4 | Make the progress signal finer than the peer we are willing to call dead |
| `8f983ac` | F11 | Carry the emergency deadline through the reads an emergency has to do first |
| `5eb6f44` | F2 | Give one frame a ceiling, and wait for a cancelled handler to write down what it knows |
| `788832a` | F3/F7/F8-CLI | Make the CLI's transport state something the transport reports, not a flag set in advance |
| `0909ada` | F13 | Watch the request id reach stderr while the call is still outstanding |

No `Co-Authored-By` trailers (`git log d25dbb4..HEAD --format=%B | grep -ci co-authored` → `0`).
**No forbidden file opened:** `git diff --name-only d25dbb4..HEAD | grep -E 'TradingGateway.cs|Stores.cs|GatewayTypes.cs'`
→ no matches. `u2a-pipe-hardening` untouched; nothing pushed to a remote, merged or rebased.

Every mutant was applied to a `cp` copy's original, `touch`ed, run, then restored from the `cp` copy
and `touch`ed again — never `git checkout --`. `git status --short` empty after each.

### Gates — Mac (tip `0909ada`)

```
dotnet build TradeAgent.sln
Build succeeded.  0 Warning(s)  0 Error(s)

dotnet test TradeAgent.sln      (exit 0)
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 677 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 238, Skipped: 0, Total: 238, Duration: 2 m 42 s - TradeAgent.IntegrationTests.dll
```
**421 green, 0 red** (391 at `d25dbb4`; this round adds 30 tests).

### Gates — the Windows box (F12) — ⚠ **UNKNOWN STANDING, see the note at the end of Round 6**

> **STANDING WITHDRAWN 2026-09-04.** Everything in this section was measured while another leg (the
> U14 builder) was pushing to and building the same `C:\ta\repo`. `tools/win-push.sh` deletes
> `src`/`tests`/`packaging`/`tools` before unpacking, so the box tree was replaced under both of us
> repeatedly, and I did not verify whose tree was on the box at run time. **Do not rely on any number
> below.** It is kept because it was honestly recorded and because the round-6 re-run (verified, at
> the end of this file) reaches the same conclusion by a checked route. The inference drawn here —
> that the Windows pipe buffer is too small to swallow a 512 KiB frame — is re-established there.

`tools/win-push.sh` (740K, 446 files) then `tools/win-run.sh`, against `DESKTOP-K8VRIT9`, ATAS
installed and running, console session live. The installed app, ATAS and the real home were not
touched: the suite redirects `TRADEAGENT_HOME` to a scratch directory and every pipe name in these
tests is a fresh GUID, so nothing bound the installation's own pipe.

```
dotnet build TradeAgent.sln
Build succeeded.  0 Warning(s)  0 Error(s)   (13.63 s)

--filter FullyQualifiedName~ConnectorSendDeadlineTests
Passed!  - Failed: 0, Passed: 20, Skipped: 0, Total: 20, Duration: 58 s

--filter FullyQualifiedName~GatewayPipeBackpressureTests
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 m 23 s

--filter CliReplayContractTests | OperatorContextTests | SweepRequestIdTests
Passed!  - Failed: 0, Passed: 56, Skipped: 0, Total: 56, Duration: 7 s

dotnet test TradeAgent.sln --no-build      (exit 0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 4 s     - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 238, Skipped: 0, Total: 238, Duration: 2 m 44 s - TradeAgent.IntegrationTests.dll
```
**421 green on Windows, 0 red — the same 421 as macOS, and the same durations.**

**What that does and does not settle.** It settles the class of claim every earlier round had to leave
open: the pipe and connector behaviour runs on real Windows named-pipe semantics at shipped defaults,
including the backpressure drops, the shutdown drain, the emergency classification and the round-4b
paced-peer fixture — whose premise depends on the socket buffer being far smaller than a 512 KiB
frame, and which the verifier flagged as possibly failing on a Windows pipe with a large buffer. It
did not: the premise assertions passed, so the buffer is not large enough to swallow the frame. The
drop paths passing is also the first evidence that disposing the handle really does kill an accepted
overlapped write on Windows, since the tests that assert the drop depend on it.

**It does NOT settle:** ATAS's real client-order-id limit or whether ATAS accepts the `op-…` shape —
both need the app and a live order, and stay with the v0.1.2 step as the brief directs (Codex F14 is
right that one `close-all` cannot settle the 64-character question; the two checks it names are the
plan). I also did NOT run the Windows-only no-buffer mutant (B4) to prove the backpressure tests would
fail without the 8 KiB buffer — the tests pass with it, which is weaker than a mutation proof.

### What I did NOT do, round 5

- **F5, F6 and the gateway half of F8 are untouched**, per the manager's split. `TradingGateway.cs`,
  `Stores.cs` and `GatewayTypes.cs` were never opened — verified by `git diff --name-only`. They stay
  HIGH-open-with-owner at U2c-1 round 4. Two consequences are load-bearing for reading this record:
  an agent `close`/`close-all` still arrives at the connector as `BridgeOps.Place` and still does NOT
  get the emergency deadline for the frame itself (only its prerequisite reads now do, which is my
  half of F11); and a replayed sweep still re-sweeps the current book (F6).
- **The F9 forced-collision consequence is bounded, not eliminated.** A sweep now re-mints rather than
  replaying, so a collision is harmless. What a colliding leg id would have done — replayed an old
  record and counted a stale CANCELLED — is F6's class and is fixed there, not here.
- **Red-first was inverted on V2, F11 and F2.** Their tests were written against the fix and the RED
  was then measured by reverting the product change (V2: reply wait back to the ordinary timeout;
  F11: scope never opened, 9.77 s; F2: no ceiling, 102.84 s; no re-await, error logged). That is a
  mutation proof, not a red-first one, and it is weaker in exactly one way: it cannot show the test
  would have been written differently had the defect been in front of me. F1, F10, F9, F4 and the CLI
  findings were genuinely red first.
- **`EmergencyDeadline`'s liveness rule has a stated cost I did not measure in the field:** a bridge
  that is alive but wedged on one operation is dropped and redialled. I measured that a heartbeating
  peer is kept and a silent one is dropped; I did not measure how often a real ATAS bridge goes
  silent for two seconds while healthy — the 5 s heartbeat interval is read from source, not observed
  on the box.
- **`FrameTimeout` = 30 s and the drain 55 s are judgments, not measurements**, in the same sense the
  2 s emergency was: the arithmetic is derived and asserted, the choice of 30 is mine. The
  product-visible consequence is that a shutdown with an order genuinely in flight may now take up to
  55 s rather than 35. That is a number the manager may want to rule on.
- I did not run the App or the UI, `tools/probe`, `tools/mac-run.sh`, or any ATAS interaction.
- I did not run the round-4 verifier's probe branch (`u2a-verify-r4-probes`) or lift its files; I read
  its record and reproduced the findings with my own fixtures. Its `VerifyR4Probes` /
  `VerifyR4TimingProbes` remain in the verify worktree only.
- I did not re-run the full suite a second time on Mac after the last commit's mutants; the 421-green
  run above is at the tip with a clean tree, and each mutant was restored and re-verified by its
  class before moving on.

## Round 6 (build record, 2026-09-04)

Bounce on `0909ada` from the round-5 Opus verifier (FAIL 1H/2M/1L, `records/U2a-verify-r5.md`; the
Codex delta was pending a quota reset). Same worktree, same builder. **F-A (HIGH) split to U2c-1**
— the operator's own Close All is still on the ordinary deadline (9759 ms vs the agent's 2018 ms)
because `RiskReducingScope` is opened only in the pipe server, and the method U2c-1 is rewriting is
where the scope belongs. `TradingGateway.cs` and `DashboardView.cs` were not opened.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F-B** (M) liveness keyed on frames-in, so a wedged-but-beating bridge is kept | **4 of 12 phases** kept and told "busy" | 12/12 dropped + the answering peer kept | any-frame liveness → **RED 4**; never keep → **RED 1** | `3e241d7` |
| **F-C** (M) mutant W3 survives all 238 | W3 **survived** at `0909ada` | 10/10 `CliReplayContractTests` | W3 → **RED 1** (`Expected: PossiblyWritten / Actual: NothingWritten`) | `e1cb147` |
| **F-D** (L) a read inherits an order's wording | the gateway cancel-all's own message: `'orders' is NOT confirmed … check your positions` | 34/34 class | all ops get order wording → **RED 2**; all ops get read wording → **RED 2** | `0bb3712` |

### F-B — the proposed mechanism does not work, and that is measured

The bounce named the fix: key liveness on WRITE progress (`_lastWriteProgressAt`). I implemented it
and ran it before adopting it, and it fails in both directions:

```
TRIAL: keep iff _lastWriteProgressAt > startedAt
  A_bridge_that_only_heartbeats…(phaseMs: 1200)  [FAIL]   ← wedged peer still KEPT
  A_bridge_that_only_heartbeats…(phaseMs: 1600)  [FAIL]   ← wedged peer still KEPT
  An_emergency_a_live_bridge_does_not_answer…    [FAIL]   ← a peer that WAS reading got dropped
  Failed! - Failed: 3, Passed: 11, Total: 14
```

The reason is arithmetic, not luck: the emergency frame is about a hundred bytes and the socket
buffer is eight kilobytes, so **the kernel accepts the frame whether or not anything ever reads it**
— `_lastWriteProgressAt` moves identically for a wedged peer and a healthy one. (The false DROP is
the other edge: the write and the caller's `startedAt` can land on the same `TickCount64`
millisecond, so `>` is false for a peer that is reading perfectly.) Write progress is the right
signal for GATE expiry, where the holder's bytes really are or are not moving, and it carries no
information at all once our own small frame is already in the buffer.

**What I implemented instead: liveness is an ANSWER.** A heartbeat proves a thread is running —
`BridgeServer.StartHeartbeat` is its own `Task.Run`, independent of the read loop a freeze wedges.
An answer proves the read loop took our frame, matched it and replied, which is exactly the faculty
an emergency needs and the one a freeze removes. It is recorded in one place, where a pending RPC is
completed, and nothing else votes. **This is a deliberate divergence from the bounce's stated
mechanism and the manager should confirm it** — the intent (12/12, phase-independent) is met; the
named mechanism could not meet it.

The keep-direction test had to be rewritten with it. The old one
(`An_emergency_a_live_bridge_does_not_answer_is_unknown_but_not_a_drop`) used a peer that read
everything and heartbeated but answered nothing — which is the wedged shape wearing the busy label,
and it passed for the wrong reason. Its replacement,
`An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop`, uses a peer that answers
every frame except the cancel-all, with ordinary traffic kept flowing across the window so answers
demonstrably arrive while the emergency waits — asserted (`answered > answeredBefore`), not assumed.
**Consequence of the new rule, stated:** a bridge that reads us and answers nothing at all for the
whole window is now dropped and redialled, where before it was kept.

### Two tests were deleted by my own editing, and nothing failed

A text slice taken to replace one test swallowed the two that followed it:
`An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge` (F11's only
through-the-gateway test) and `A_write_that_keeps_making_progress_is_still_bounded_in_total` (F2's
ceiling test). The class stayed green throughout, because a deleted test is a test that cannot fail.
Caught by comparing method names against `0909ada`, not by a run:

```
git show 0909ada:…ConnectorSendDeadlineTests.cs | grep -oE '^    public (async Task|void) [A-Za-z_]+'
  → 17 methods;  working tree → 16
LOST: A_write_that_keeps_making_progress_is_still_bounded_in_total
      An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge
      An_emergency_a_live_bridge_does_not_answer_is_unknown_but_not_a_drop   (this one deliberate)
```

Both were restored from `0909ada` in `0bb3712`, and "verbatim" was accurate for only one of them —
corrected here (Codex/verifier F-F). `A_write_that_keeps_making_progress_is_still_bounded_in_total`
is byte-identical. `An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge` was
restored with its assertions CHANGED: its single `Assert.Contains("NOT confirmed", …)` became five,
because the same commit gave reads their own wording (F-D) and that test's sweep dies on its `orders`
READ. The change strengthens it — it now asserts the read sentence and the absence of both wrong
halves — but it is a strengthening, not a restoration, and this round exists partly because a silent
test deletion survived a green suite. Precision about what went back is the compensating control. **§9.9: a green suite cannot detect a
removed test, and no gate in this program would have.** The cheap generic check is the one used
here — diff the test-method names against the base sha whenever a test file is edited structurally —
and it belongs in the builder's own routine, not in a review round.

### A fixture that crashed the Windows test host — found only because the box was run

`A_bridge_that_only_heartbeats…`'s twelve cases aborted `dotnet test TradeAgent.sln` on the box with
**"Test host process crashed"** — twice, at 234 and at 209 tests. The same command with only those
twelve excluded was green end to end (108 + 75 + **241**, exit 0; 241 = 253 − 12, which also confirms
the tree under that run was mine). Running the integration project alone was green too, so it takes
three test hosts in parallel to show it.

The fault was mine and it was in the FIXTURE, not the product. `BridgePeer` cancelled its token and
disposed its pipe in the same breath, leaving a background writer — the heartbeat, the paced read
pump, the answering loop — mid-`WriteAsync` on a handle being closed underneath it. On macOS that is
a caught exception and nothing more, which is why six rounds of this suite never saw it; on Windows a
named pipe is a kernel object with an overlapped operation in flight, and that is precisely the shape
this record has carried as NOT VERIFIED on Windows since round 1. Every background task is now held,
cancelled and awaited before the pipe is disposed (`ffa1a3d`). **This is the first defect in this
unit that only the box could find.**

### Gates — Mac (tip `ffa1a3d`)

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test TradeAgent.sln             (exit 0)
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 587 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253, Duration: 3 m 6 s - TradeAgent.IntegrationTests.dll
```
**436 green, 0 red** (421 at `0909ada`; +15 — twelve phase cases, F-C, and two F-D cases, with one
test replaced one-for-one).

### Gates — the Windows box (tip `ffa1a3d`), with the tree PROVEN to be mine

The box grant is serialised as of 2026-09-04 and this is the only leg holding it. Pushed, then
verified before running and again after, per the coordinator's rule:

```
LOCAL                                             BOX (C:\ta\repo)
e76f63eee4525ecc  ConnectorSendDeadlineTests.cs   e76f63eee4525ecc   ✓
e76daaa99ecd4c27  CliReplayContractTests.cs       e76daaa99ecd4c27   ✓
d7f23ec6beae50a1  AtasConnector.cs                d7f23ec6beae50a1   ✓
f926e525779c6b70  PipeClient.cs                   f926e525779c6b70   ✓
.cs under src+tests: 88                           88                 ✓   (no foreign file)
```

Build and both runs then happened in ONE ssh session so nothing could intervene:

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)

--filter ConnectorSendDeadlineTests | CliReplayContractTests
Passed!  - Failed: 0, Passed:  44, Skipped: 0, Total:  44, Duration: 1 m 33 s

dotnet test TradeAgent.sln --no-build   (exit 0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 4 s     - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253, Duration: 3 m 13 s - TradeAgent.IntegrationTests.dll
```

Re-verified immediately afterwards — the three hashes and the count of 88 were unchanged, so the tree
was mine for the whole run. **436 green on Windows, 0 red — the same 436 as macOS, test for test.**
That re-establishes, on a checked tree, the inference round 5 drew on an unchecked one: the paced-peer
fixture's premise holds on a real Windows named pipe, so the buffer cannot swallow a 512 KiB frame.

### Commits (round 6, on top of `0909ada`) — tip **`ffa1a3d`**

| sha | finding | what |
|---|---|---|
| `3e241d7` | F-B | Key an emergency's liveness on an answer, not on the bridge still talking |
| `e1cb147` | F-C | Test the one transport transition that would place a second real order |
| `0bb3712` | F-D | Say what a failed READ means, instead of telling the owner to check for an order |
| `ffa1a3d` | (box) | Stop the test peer writing before its pipe is closed underneath it |

No `Co-Authored-By` trailers. `TradingGateway.cs` and `DashboardView.cs` not opened —
`git diff --name-only 0909ada..ffa1a3d | grep -E 'TradingGateway.cs|DashboardView.cs'` → no matches.

### What I did NOT do, round 6

- **F-A is untouched** and stays with U2c-1: the operator's own Close All is still on the ordinary
  deadline (9759 ms vs the agent's 2018 ms). `main` has no fast path for anyone, so integrating U2a
  does not regress the button — but the asymmetry is real until U2c-1 lands, and it is the round-4
  principle with the roles swapped.
- **F-B diverges from the mechanism the bounce named**, and the manager should confirm it. Write
  progress cannot discriminate the case (measured above); liveness is an ANSWER instead. The intent —
  12/12, phase-independent — is met.
- **The new rule changes one behaviour beyond the finding:** a bridge that reads us but answers
  nothing at all for the whole window is now dropped, where before it was kept. That is a deliberate
  consequence, not an oversight.
- I did not establish whether a REAL wedged ATAS keeps heartbeating — `BridgeServer`'s independent
  `Task.Run` is read from source and reproduced with a synthetic peer, not observed on a live bridge.
- I did not re-run the round-5 on-box figures whose standing I withdrew above; the round-6 run
  supersedes them rather than repairing them.
- I did not investigate the box's `CoidWitnessTests` anomaly (a filtered run reported 118 where macOS
  reports 25) beyond establishing it was cross-leg contamination — that file is U14's and untouched
  by rounds 5-6.
- I did not run the App, `tools/probe`, or any ATAS interaction; the ATAS client-order-id questions
  stay with v0.1.2.

## Round 7 (build record, 2026-09-04)

Bounce on `ffa1a3d` from the round-6 Opus verifier (FAIL 0H/1M/1L, `records/U2a-verify-r6.md`) and,
mid-round, the Codex delta review of rounds 5+6 (`records/codex-U2a-r6.txt`: 13 priors FIXED, 4
deferred by decision, 1H/4M/3L new). F-A stays with U2c-1; `TradingGateway.cs` and `DashboardView.cs`
were not opened.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F-E / C2** (M) liveness judged on the caller's 2 s | a bridge answering at 2500/3500 ms was dropped at ~2000 ms as "not responding" (verifier) | 37/37 class; the late answer recorded | any-frame liveness → **RED 4**; never keep → **RED 1** (round 6, still bite) | `3c046a1` |
| **C5** (L) `>` discards a same-tick answer | — (folded into the liveness rework as directed) | same | — | `3c046a1` |
| **C1** (H) two clocks in one emergency | **3.40 s against a 2 s promise** | 2.0 s | budget restarts at the gate → **RED 1** | `923cdb6` |
| **C3** (M) the 55 s drain is a literal | a 100 s worst path drained for 55 s | 13/13 class | drain back to a literal → **RED 1** | `dca6519` |
| **C4** (L) a frame with both ids null | `the trading service closed the connection` | 26/26 class | — (the test is the fix's own proof) | `606890d` |
| **PRIOR 8 CLI half** — the note overpromised | every mutating op promised a replay only Place performs | 20/20 class, pinned per op | — | `6850e83` |
| **PRIOR 4** residual documented | — | — | — | `a974142` |
| **F-F, PRIOR 12/14** record corrections | — | — | — | (this file) |

### C1 — the one that mattered, and how it was made observable

`EmergencyDeadline` was captured before the gate wait and then started against a NEW clock the moment
the gate was acquired, so a call could spend nearly the whole deadline queueing and be handed a fresh
one for its write. Codex's own check is the fixture, and it needed two things no existing peer could
do:

- **a peer that drains at a fixed rate and then stops for good**, so the gate is released at a chosen
  moment INTO A FULL BUFFER — a writer's frame completes when the kernel takes the last of it, not
  when the peer reads it;
- **an oversized emergency frame.** A cancel-all is ~100 bytes, which an 8 KiB buffer swallows whether
  or not anything is reading, so a small frame can only ever measure the gate. Two earlier fixtures
  failed for exactly that reason before this was understood, and both are on the record: the first
  released the gate too late (the emergency expired queueing, "busy"), the second released it into a
  buffer with room (the write succeeded and the reply timed out, also "busy").

With a 64 KiB emergency frame behind a gate released at ~1.5 s: **3.40 s** before, **2.0 s** after.
The test asserts `"still being sent"` as its premise — that is the `FrameIncomplete` branch, so it
proves the call reached the WRITE rather than expiring on the queue, which is the only arrangement in
which C1 is observable at all.

### F-E — what changed, and the cost

Two bounds, two meanings. `EmergencyDeadline` bounds what the CALLER waits and nothing else: two
seconds, NOT confirmed, check ATAS, UNKNOWN — unchanged, and asserted unchanged in all three new
fixtures. Liveness gets the deadline this system already uses for "ATAS did not answer" — `_timeout`,
**no new number** — as its grace, the verdict is deferred to it, the pending request stays registered,
and a late answer is delivered rather than dropped on the floor (`LateAnswers`, `LateAnswerReceived`).
**Whether the gateway settles a request on a late answer is U2c-1's**, which is why these are exposed
rather than consumed.

**The stated cost: a wedged-but-heartbeating bridge is now detected at ≈10 s instead of ≈2 s. The
caller's answer is not delayed by it — only the teardown is.** One wording consequence the manager
should note: on the reply path the caller now always gets the "busy / still up" sentence at two
seconds, because at that instant nothing has been dropped and "not responding … has been dropped"
would be false. The "not responding" wording still reaches the drop itself, as the disconnect reason.

The twelve heartbeat phases are now STRONGER than when they were written: with a 10 s judging window
and a 5 s beat, every phase has at least one heartbeat inside the window, so no case is silent by
luck — all twelve turn on heartbeats being refused as evidence.

### Commits (round 7, on top of `ffa1a3d`) — tip **`a974142`**

| sha | finding | what |
|---|---|---|
| `3c046a1` | F-E / C2 / C5 | Judge the connection on the grace, not on the two seconds the caller waited |
| `923cdb6` | C1 | Spend one budget across the gate and the write, not one each |
| `dca6519` | C3 | Derive the shutdown drain from the connector instead of writing it down |
| `606890d` | C4 | Refuse a frame that names no request instead of hanging up on the agent |
| `6850e83` | PRIOR 8 (CLI) | Stop the CLI promising a replay the gateway does not perform |
| `a974142` | PRIOR 4 | Write the bridge deadlines down where an operator meets them |

No `Co-Authored-By` trailers. `TradingGateway.cs` and `DashboardView.cs` not opened. One mechanical
addition to a U2b test file (`ApprovalReauthorizationTests.ConnectorFacade` forwards the new
interface member) — what compiles, nothing more.

Test-method names diffed against `ffa1a3d` after every structural edit — the control this program
adopted after round 6's silent deletion. The only loss is the deliberate rename
(`…_drops_it` → `…_drops_it_at_the_grace`); three tests added.

### Gates — Mac (tip `a974142`)

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test TradeAgent.sln             (exit 0)
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 565 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 268, Skipped: 0, Total: 268, Duration: 5 m 19 s - TradeAgent.IntegrationTests.dll
```
**451 green, 0 red** (436 at `ffa1a3d`). **The integration suite is now 5 m 19 s, up from 3 m 06 s** —
the twelve heartbeat phases and the idle-stalled case each wait out the 10 s grace, and the two
late-answer cases wait past it. That is the direct, visible price of F-E's decision and it is a number
the manager may want to rule on; the alternative is a shorter grace, which is a new number the
decision explicitly refused.

### Gates — the Windows box (tip `a974142`), tree PROVEN mine, ONE run as granted

```
LOCAL                                              BOX (C:\ta\repo)
e7255869ab8e7abe  ConnectorSendDeadlineTests.cs    e7255869ab8e7abe   ✓
4ea4f892102a2185  GatewayPipeBackpressureTests.cs  4ea4f892102a2185   ✓
4ea8675ca08f1a4a  AtasConnector.cs                 4ea8675ca08f1a4a   ✓
4133a88bd9295743  GatewayPipeServer.cs             4133a88bd9295743   ✓
6cd3f1050600f183  CliReplayContract.cs             6cd3f1050600f183   ✓
.cs under src+tests: 88                            88                 ✓  (no foreign file)
```

Build and both runs in ONE ssh session, and the tree re-verified unchanged afterwards:

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)

--filter ConnectorSendDeadlineTests | GatewayPipeBackpressureTests | CliReplayContractTests
Passed!  - Failed: 0, Passed:  70, Skipped: 0, Total:  70, Duration: 5 m 5 s

dotnet test TradeAgent.sln --no-build   (exit 0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 4 s      - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 268, Skipped: 0, Total: 268, Duration: 5 m 17 s - TradeAgent.IntegrationTests.dll
```

**451 green on Windows, 0 red — the same 451 as macOS, test for test, and the same durations.**

### What I did NOT do, round 7

- **F-A untouched**, still with U2c-1: the operator's Close All remains on the ordinary deadline.
- **F-E's mechanism is the manager's decision, implemented as given** (grace = the existing ordinary
  RPC deadline, no new number). Two consequences are mine to flag rather than to have settled: the
  reply-path caller now always reads the "busy / still up" sentence at two seconds, because nothing
  has been dropped at that instant; and the suite is two minutes longer.
- **`LateAnswerReceived` / `LateAnswers` are exposed and not consumed.** Whether the gateway settles a
  request on a late answer is U2c-1's; nothing in this unit reads them except a test.
- I did not verify that a real ATAS synchronous call exceeds two seconds in practice — F-E's premise
  is read from `BridgeProtocol.cs` and reproduced with a synthetic peer, as the verifier also stated.
- **Mutant B4 (the Windows no-buffer stall) is still not run by anyone**, so the 8 KiB buffer remains
  unproven by mutation on either platform even though every test that depends on it passes on the box.
- ATAS's real client-order-id limit and the `op-…` shape still need the deliberate 64/65-character
  probe at v0.1.2; the old "one `close-all` settles both" claim is withdrawn above.
- I did not run the App, `tools/probe`, or any ATAS interaction, and I used the box grant once.

## Round 8 (build record, 2026-09-04)

Bounce on `a974142` from the Codex delta on round 7 (`records/codex-U2a-r7.txt`: 8 of 11 priors FIXED,
1H/1M/1L new) and the round-7 Opus verifier (`records/U2a-verify-r7.md`, **PASS WITH LOW**, F-G).
F-A stays with U2c-1; `TradingGateway.cs` and `DashboardView.cs` were not opened.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F1** (H) the clock was per `Rpc`, not per operation | Codex: three 1.9 s replies → a sweep took **5.7 s** against a 2 s promise | 28/28 sweep class; 38/38 connector class | shared deadline reverted → **RED, 4.01 s**; legs skipped in silence → **RED** | `cd0165d`, `d5c3cd4` |
| **F2** (M) the drain covered one call, not the handler | a 12 s handler drained for 9 s → the cancel left `DISPATCHING` | 14/14 backpressure class | drain back to one call → **RED 2** | `0d25426` |
| **F3** (L) a late answer lost to a race, `_abandoned` leaks | — (closed with the F1/F-G rework as directed) | `AwaitingLateAnswer` returns to 0 | — | `02f457c` |
| **F-G** (L) the sentence led with connection state | the caller's 2 s sentence began "the bridge is busy; …" | starts-with assertion | connection state first again → **RED 2** | `02f457c` |
| PRIOR 12/14/F-F | **refuted** — the corrections are on `main`; the branch carries the 2026-09-03 snapshot | — | — | `5624cd1` |

### F1 — and the hole the acceptance would have left

`RiskReducingScope` now carries ONE absolute deadline; every RPC inside gets `deadline − now`, nesting
can only bring it forward, and `EmergencyBudget` joins `WorstCaseOperationPath` on `ITradingConnector`
because the component that DECOMPOSES an operation is the one that must start its clock.

The leg loop had three faults that were one fault seen from three sides: it awaited each leg before
starting the next; it had no `try`/`catch`, so **one failing leg abandoned every leg after it in
silence** — the sweep surfaced as a single transport error naming none of the orders left working;
and each leg restarted the budget. Legs are now issued in **bounded waves** (four), each inheriting
the deadline, failures are recorded rather than fatal, and a leg whose turn arrives after the deadline
is reported **not-sent**. The wave bound is what makes not-sent a real outcome rather than a branch
nothing reaches: unbounded fan-out issues every leg in the same microsecond, so the deadline can never
fall between two of them.

**The acceptance as briefed would have passed over a broken connector.** It runs through the gateway
onto `FakeConnector`, which honours the ambient deadline itself — so reverting `AtasConnector`'s half
of the fix left every one of those tests green (measured). `Two_emergency_calls_inside_one_operation_
share_its_deadline` reaches the connector: **2.0 s** shared, **4.01 s** with the mutant. Writing it
found a defect the sweep tests could not see — a leg reached after the deadline queued for its one
remaining millisecond, saw no write progress in that millisecond, and **dropped a bridge that was
reading throughout**. Judging liveness over a millisecond is not a measurement; such a leg now fails
before the gate, connection untouched, because a leg whose turn never came is not evidence about the
peer.

### F2 — the number an operator will feel

The drain is now the longest serial chain one handler issues (a prerequisite read, a target
resolution, the mutation) × the per-call bound + the settle. **At shipped values that is 3 × 50 + 5 =
155 s, and disposal's ceiling is 5 + 155 + 5.** It is paid only while a request is genuinely in
flight, and the alternative is the abandoned DISPATCHING order — but it is a product decision, not an
arithmetic one, and it is the second time this round a correctness fix has bought time with
wall-clock. A risk-reducing operation can no longer reach it (F1 bounds the whole operation at 2 s);
what reaches it is an ordinary multi-call handler such as `modify`.

### Round 8 close — gates, box run, and the test-name diff (2026-09-04)

Closed by a second builder: the round-8 builder was killed by a rate limit after the code was on
disk but before the final runs. Nothing was redesigned; the code verified below is `5624cd1`
exactly as that builder left it (branch `u2a-rebase-probe`, 5 commits on `a974142`, tree clean).

**Mac — `dotnet build TradeAgent.sln` then the FULL suite once** (`export PATH="$HOME/.dotnet:$PATH"
DOTNET_ROOT="$HOME/.dotnet"`):

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 747 ms - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s    - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 272, Skipped: 0, Total: 272, Duration: 5 m 39 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**455 green (75 / 108 / 272), 0 failed, 0 skipped — the previous builder's claim is CONFIRMED**, at
those exact per-project counts.

**Correction to the round-8 build claim: the build emits 1 warning, not 0, and the warning is new
this round.** The first `dotnet build TradeAgent.sln` on the Mac reported `0 Warning(s)`, but that
run was incremental — every project was already up to date, so nothing recompiled and no warning was
re-emitted. Forced (`--no-incremental`) it reports what the box's from-scratch build reports:

```
src/TradeAgent.Gateway/GatewayPipeServer.cs(626,32): warning CS8619: Nullability of reference types
in value of type 'Task<ExecutionRequest>' doesn't match target type 'Task<ExecutionRequest?>'.
    1 Warning(s)
    0 Error(s)
```

Both machines report the identical single warning, so it is not platform-specific. It is new in
round 8: `RunLegs` does not exist at `a974142` (`git grep RunLegs a974142 -- src/` → no match) and is
introduced by the F1 rework. The mechanism, from the declared types: `RunLegs` takes
`Func<string, string, Task<ExecutionRequest?>>` so that one helper serves both sweeps;
`TradingGateway.CloseAsync` returns `Task<ExecutionRequest?>` and matches, while
`TradingGateway.CancelAsync` returns `Task<ExecutionRequest>`, and `Task<T>` is invariant, so the
`cancelall` call site at line 626 warns. **NOT verified: whether this warrants a code change** — it
is 0 errors and the suite is green on both machines, and closing it would be a new edit whose
RED/GREEN/mutant evidence this round does not have. Left for the manager to route; nothing was
changed to hide it.

**Test-name diff `a974142` → `5624cd1` — no test was silently deleted.** Test-method names extracted
at both shas (`git grep -n -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`, reduced to
`path::method`, sorted unique):

| | a974142 | 5624cd1 |
|---|---|---|
| test methods | 358 | 362 |
| `[Fact]` | 320 | 324 |
| `[Theory]` | 27 | 27 |
| `[InlineData]`/`[MemberData]` rows | 122 | 122 |

**REMOVED: 0.** ADDED: 4, all `[Fact]` —

```
tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs::Two_emergency_calls_inside_one_operation_share_its_deadline
tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs::Disposal_covers_a_handler_that_makes_several_connector_calls_in_series
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_five_order_sweep_answers_within_the_budget_and_accounts_for_every_order
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_sweep_pays_the_emergency_budget_once_not_once_per_rpc
```

The arithmetic closes: 451 green at `a974142` + 4 new facts = **455**, which is what ran. Four test
files changed in the diff; the fourth, `ApprovalReauthorizationTests.cs`, is `+1` line and that line
is `public TimeSpan EmergencyBudget => inner.EmergencyBudget;` — a new interface member on a test
double, not a test.

**The box run** (`DESKTOP-K8VRIT9`, one granted run). `tools/win-state.sh` first: tailscale up,
session `Active (id 1, console)`, desktop live. `tools/win-push.sh` from the clean worktree:
`packed 760K` / `unpacked: 155 files` — 156 files locally excluding `.git`/`bin`/`obj`/`artifacts`,
minus the worktree's `.git` pointer file (a file, not a directory, so `tar --exclude='.git'` drops
it) = 155. Then build, the two pipe classes, and the full suite in ONE ssh session
(`tools/win-ps.sh`), with the identity check taken before and re-taken after.

**Identity check — the box tree is the Mac tree.** SHA-256 of seven files, all seven changed by this
round, plus the count of source `.cs` under `src`+`tests`. Box BEFORE, box AFTER and Mac worktree are
byte-identical on all seven:

```
835dc4d3e5f67c2581c9462fc804476326fa18d525bc9e7e0cfa83d0e45dbd73  src/TradeAgent.ConnectorSdk/RiskReducingScope.cs
61b7d86b065f1b29910da8e479b2860cdce17d1732484b0cc8e45c9527dc48a9  src/TradeAgent.ConnectorSdk/Contracts.cs
4e90ccf9b2d8459931431cd83ec72574dcfa35d0f4e2054f1e5460cf6b8e30f0  src/TradeAgent.Connectors.Atas/AtasConnector.cs
56c920bceb7b446cdbfff0afb2f277ff3736f3f7450823ec9a1600a24085dcfd  src/TradeAgent.Gateway/GatewayPipeServer.cs
d1be528e43c10ed44c193916423b024e65095e1a0d23a7d7d1b825d0f5a7b4dd  tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs
f24cc09c0e61a737e9f8cf416d58212d79a6801565616c65e6a5e648f832a79e  tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs
6dad1a36e7b7d6e55a063cb426a8bdd13115fa6041c19e449d27b89ecc22eeb6  tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs
```

`.cs` under `src`+`tests`: box **88** BEFORE → **136** AFTER. Both are correct and both match the Mac:
88 is the source count (`git ls-tree -r --name-only 5624cd1 -- src tests | grep -c '\.cs$'` → 88, and
the Mac excluding `bin`/`obj` → 88); 136 is that plus the `obj/*.cs` the build generates, which is
what the built Mac tree also shows. The seven hashes are unchanged AFTER the three runs, so nothing
the run did altered the tree it measured.

**Box counts:**

```
== BUILD ==            Build succeeded.  1 Warning(s)  0 Error(s)   Time Elapsed 00:00:12.06   (exit 0)

== PIPE CLASSES (ConnectorSendDeadlineTests + GatewayPipeBackpressureTests) ==
Passed!  - Failed: 0, Passed:  52, Skipped: 0, Total:  52, Duration: 5 m 14 s - TradeAgent.IntegrationTests.dll (net10.0)   (exit 0)

== FULL SUITE ==
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 3 s     - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 272, Skipped: 0, Total: 272, Duration: 5 m 41 s - TradeAgent.IntegrationTests.dll (net10.0)
full exit: 0     full duration: 352 s
```

**Windows: 455 green (75 / 108 / 272), 0 failed, 0 skipped — the same 455 as the Mac, on the target
platform.** The 52 in the pipe run is the two classes together and reconciles with the build record's
own figures: `ConnectorSendDeadlineTests` 38 + `GatewayPipeBackpressureTests` 14 = 52.

The suites cannot reach the real installation: `tests/Shared/TestEnv.cs` redirects `TRADEAGENT_HOME`
to a fresh temp directory and `TRADEAGENT_PIPE` to `ta-test-<guid>` from a `[ModuleInitializer]`,
before anything touches `Paths` (read, not assumed — `TestEnv.Init()`).

### What I did NOT do

- **Did not redesign, refactor or fix anything.** No source file was edited. The CS8619 warning above
  is reported, not closed. The only file this session writes is this record.
- **Did not commit, push, merge, rebase or move any branch.** `u2a-rebase-probe` is still at
  `5624cd1`; `u2a-pipe-hardening` was not touched. No git command was run in the main worktree.
- **Did not re-run the round-8 RED/GREEN/mutant evidence.** The build-record table above is the
  previous builder's, carried forward unverified by me. NOT verified by this session: every RED,
  GREEN and mutant figure in it, and the F2 arithmetic (3 × 50 + 5 = 155 s). What I verified is that
  the tree producing those claims builds and is 455 green on both machines.
- **Did not touch the installed app, ATAS, or the real home on the box.** ATAS was running throughout
  (`win-state.sh`: `ATAS running: True`) and was not started, stopped or driven. `win-push.sh`
  rewrites `C:\ta\repo` only; it verified first that no process was running out of that tree
  ("nothing running from C:\ta\repo") so its delete step could not half-remove a running install.
  No UI agent was installed or started, and no screenshot was taken.
- **Did not run `probe atas`, place, modify or cancel any order**, on the box or anywhere.
- **Did not run the Codex leg or any adversarial-verify leg.** This session is the builder.
- Two ssh calls beyond the single granted run, both read-only and neither producing a figure quoted
  above: `win-state.sh` before pushing, and one `Select-String` over `C:\ta\r8-build.log` — the log
  the granted run itself wrote — to recover the text of the one warning.

## Round 9 (build record, 2026-09-04)

Bounce on `5624cd1` from the Codex delta on round 8 (`records/codex-U2a-r8.txt`: 0H/2M/3L, 10 of 11
priors FIXED) plus the round-8 finisher's addendum (F5). **Fresh builder** — the round 4–8 builder's
session is gone; nothing in the round-8 table below was re-measured by me except where this section
says it was. No box access this round: everything here is the Mac. `TradingGateway.cs`,
`DashboardView.cs`, `Stores.cs` and `GatewayTypes.cs` were **read but not modified** (the drain
derivation below is a count of the call chain those handlers issue, which cannot be made without
reading them; `git diff --name-only` at the end of this section proves no edit).

**The build gate changed this round, per the addendum:** `dotnet build TradeAgent.sln
--no-incremental`. An incremental build recompiles nothing and re-emits no warning, so its
"0 Warning(s)" is not a claim about the code.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F5** (gate) `RunLegs` nullable variance warns | `--no-incremental` → `GatewayPipeServer.cs(626,32): warning CS8619` … `1 Warning(s)` | `0 Warning(s)  0 Error(s)`; 28/28 `SweepRequestIdTests` | lambda back to the direct call → **CS8619 at (633,32), 1 Warning(s)** | `456b1cd` |
| **F4** (L) `Left` hands out 1 ms past an absolute deadline | `Expected: 00:00:00 / Actual: 00:00:00.0010000` | 39/39 `ConnectorSendDeadlineTests` | unclamp it → **RED, `Actual: -00:00:05`** | `f6e5ddf` |
| **F3** (L) the simulator maxes two serial latencies | `a 2400 ms call completed inside a 2000 ms operation, in 2410 ms` | 29/29 `SweepRequestIdTests`; 14/14 backpressure | precheck ignores the uncancellable half → **RED, 2407 ms** | `0bbe5fe` |
| **F2** (L) `_abandoned` leaks when the grace ends early | **RED 2** — `1 request(s) still awaiting a late answer after the bridge disconnects` / `… after the connector is disposed` | 41/41 `ConnectorSendDeadlineTests` | drop the removal from the early exit → **RED 2**, same two sentences | `a0b0472` |
| **PRIOR 2** (M) drain assumes 3 serial RPCs; an override can shorten it | chain at 3: `a cold placement issues 5 connector calls in series (account -> positions -> quote -> instruments -> place) against a drain derived from 3` and `1 request(s) left DISPATCHING: the drain came out at 3.20s against a five-call chain that needs 5.00s`; override unclamped: `the drain came out at 0s against a 500s chain` | 17/17 backpressure; 70/70 sweep + deadline classes | chain **4** (one short) → **RED 2**; clamp inverted (`<` for `>`) → **RED 2** | `74aeef6` |
| **F1** (M, class) the leg vocabulary is not 1:1 with the record | **RED 3** — `rejected` and `sent-still-working` absent; both pre-send legs read `Expected: "not-sent" / Actual: "sent-not-confirmed"` | 33/33 `SweepRequestIdTests`; 69/69 with backpressure + slice + CLI | one per mapping arm, five arms → **five REDs** (the fifth arm survived until its test was written) | `4e1396f`, `088c059` |

### F5 — and why its mutant is watched on the build, not on a test

`RunLegs` takes the WIDER contract, `Func<string,string,Task<ExecutionRequest?>>`, because
`close-all` has a leg that legitimately produces no record and `cancel-all` does not. `Task<T>` is
invariant, so handing it `CancelAsync`'s `Task<ExecutionRequest>` is a mismatch. The fix converts the
VALUE rather than the task — `async (legId, target) => await gateway.CancelAsync(…)` — which is the
widening C# does allow. No `!`, no `#pragma`, no `SuppressMessage`, and no signature on
`TradingGateway` was touched.

**Stated plainly: nullability annotations are erased at run time, so no xUnit assertion can observe
this.** The gate that catches the class is the build itself, and the mutant is watched there: reverting
the lambda re-emits the identical CS8619 (at line 633 after the comment this commit adds). **§9.9
candidate for the manager, NOT done here:** `<TreatWarningsAsErrors>` in `Directory.Build.props` would
make every future warning a RED instead of a line somebody has to read. It is a program-wide policy
change outside this unit's brief, so it is proposed, not taken.

### F4 — and the sibling that keeps the millisecond on purpose

There were three copies of "how long until this absolute deadline": the connector's write budget, its
reply budget, and the simulator's precheck — and they had drifted to two different answers for
*expired*. They are now one function, `RiskReducingScope.LeftUntil`, next to the deadline it measures
against, and it returns `TimeSpan.Zero`.

`AtasConnector.Remaining` still returns a millisecond and still says why, because it is a different
thing: a RELATIVE budget handed to a grace that is only just opening. A caller that arrives there with
nothing left has already spent its ordinary timeout, and a moment to collect an answer that has
already landed costs nobody anything. An ABSOLUTE deadline is a promise to a person, and one more
millisecond of it is a promise broken. The reason given for "never zero" — that zero would cancel
before an already-arrived answer could be read — turns out not to hold anyway: `Task.WaitAsync` checks
`IsCompleted` before it looks at the token, so a completed task comes back whatever the token's state.

**Pinned as arithmetic, and that is the honest bar for it.** The wait is milliseconds; no end-to-end
timing can separate one millisecond from zero, so an integration assertion here would be theatre. What
is asserted end-to-end is that the 41 tests of `ConnectorSendDeadlineTests` still hold with the
millisecond gone.

### F3 — the instrument was outrunning the deadline it is used to measure

`FakeConnector.Wire` asks whether the injected latency fits inside the operation deadline and then
awaits `LatencyMs` and `UncancellableLatencyMs` ONE AFTER THE OTHER — but the question was asked about
`Math.Max` of the two. Codex's own check reproduces exactly: both at 1200 ms inside a 2000 ms
operation, and the call **returns successfully at 2410 ms**, four hundred milliseconds after the
operation promised to be over.

`WorstCaseOperationPath` had the same `Math.Max` and it is the worse half, because
`GatewayPipeServer`'s shutdown drain is DERIVED from it: the connector used to prove the drain covers
a handler was telling the drain to be shorter than the connector. Both are sums now. **No existing
test set both latencies** (`grep` over `tests/`: `LatencyMs` in six places, `UncancellableLatencyMs` in
one, never together) — which is why a wrong `Math.Max` survived 455 tests.

The RED is a RETURN-versus-THROW discriminator rather than a stopwatch reading: summed, the call
cannot fit and says so at the deadline; maxed, it succeeds past it. The test asserts both directions —
two latencies that DO fit are still served — so "refuse everything" is not a passing mutant.

### F2 — the two other ways a grace can end

When the caller gives up at two seconds the request is parked in `_abandoned` and the CONNECTION's
verdict is deferred to the grace (round 7's F-E). The waiter removed the entry on the timeout path
only, and both other exits went through one `catch (Exception) { return; }`. `Drop` faults every
pending request, so **a disconnect during the grace and a disposal each returned without removing
anything** — the id stayed for the life of the process.

It is a few dozen bytes per abandoned emergency, which is why it is LOW. What makes it worth closing
is what the number is FOR: `AwaitingLateAnswer` is the only external evidence that the deferred
verdict cleans up after itself, so a counter that can stick at one for a reason nobody intended can no
longer prove anything about the ones that do. Both of Codex's triggers are now a `[Theory]` arm.

### PRIOR 2 — the longest chain, named, and why it is that one

**The longest ORDINARY handler is a cold `buy`/`sell`, and it issues FIVE connector calls in series.**
Not read off a comment — counted over the real pipe by a connector decorator that records which calls
it was asked for, in order:

```
account -> positions -> quote -> instruments -> place
```

1. `TradingGateway.PlaceAsync` → `AccountAsync` (the chosen account);
2. `RiskCheckOrThrow` → `GetPositionsAsync`, for the `MaxOpenPositions` check;
3. → `GetQuoteAsync`, required for EVERY order so a stale price cannot size one;
4. → `GetInstrumentsAsync`, read once and cached — only a cold process pays it;
5. `DispatchPlaceAsync` → `PlaceOrderAsync`.

Round 8 used **three** — "a prerequisite read, a target resolution, the mutation" — which is the shape
of a `modify` and is not the longest.

**"Cold" is the ordinary state of a configured installation, not a corner case.** The first version of
the count test measured four, because `RefreshHealthAsync` reads the instrument list to pick a symbol
to quote and so warms the cache within seconds of startup. It does that **only when nothing is
allowlisted** (`TradingGateway.cs:1017`): with a `Risk.InstrumentAllowlist` set — which is what a
configured install has, and the safe configuration — health never reads instruments, the cache stays
empty, and the first placement pays for it. The measurement above is on that configuration, and the
test says why in its own comment. **Worth flagging for U2c-1:** on a NON-allowlisted install the chain
is four, so the bound over-covers there. Over-covering is the safe direction and is what a bound is.

**Why not seven, which is what a cold `close` issues.** `close` → `RequireAccountId` + a positions read
+ all five of `PlaceAsync` = 7. But `close` is RISK-REDUCING, so six of those seven share ONE
`EmergencyBudget` between them (round 8's one-deadline-per-operation), and only the trailing `Place` is
served the ordinary bound — `Place` is excluded from the emergency deadline on purpose. Counting its
calls at the ordinary rate would over-cover by minutes and would be arithmetic about a thing that
cannot happen. The sweeps are excluded for the same reason and one more: **their legs are issued
concurrently, so their call count is not their serial depth at all.**

So the derivation is two shapes, MAXED rather than added, because one handler is one or the other:

```
drain = max( 5 × WorstCaseOperationPath ,  EmergencyBudget + WorstCaseOperationPath )
      + SettleAfterCancelTimeout
```

The second term is not a rounding allowance and it is not theoretical: **the suite's own
`Disposal_covers_a_handler_that_makes_several_connector_calls_in_series` fixture uses a 30 s emergency
budget over a 4 s connector, which needs 34 s against 20 s from the ordinary term alone.** Round 8's
formula under-covered that fixture and the test passed anyway, because it only asserted `> 12 s`.

**The trailing term is `SettleAfterCancelTimeout` itself, not a second literal five seconds.** The two
numbers always meant the same thing — time for a handler to write down what it knows — and writing it
twice is exactly how a derived number stops being derived, which is the class this unit has now fixed
three times (round 4b, round 5 §9.9, round 7 C3).

### PRIOR 2 — the override, and the invariant that replaces it

`HandlerDrainTimeout`'s getter was `_drain ?? DerivedDrainTimeout`: an explicit value won outright,
which put the whole derivation one constructor argument away from meaningless. **Measured on the
suite's own case: `{ HandlerDrainTimeout = 7 seconds }` against a 100 s worst path gave 7 seconds** —
and the round-8 test asserted that as correct behaviour. It now clamps: an explicit value may only
LENGTHEN. A caller naming a longer value means it and gets it (asserted, so this is a clamp and not an
override quietly ignored); one naming a shorter value is asking for an order to be abandoned at
shutdown, which is not theirs to ask for.

`No_combination_of_settings_makes_the_drain_shorter_than_the_chain` asserts the invariant over every
knob a caller can turn — an undersized drain, a zero settle, both together — against a 60 s-RPC
connector. With the old getter it reports `the drain came out at 0s against a 500s chain`.

**Two suite tests needed the old short drain and now get it honestly.** `Disposal_waits_for_a_cancelled_
handler_to_record_what_it_knows` and `A_handler_that_outlasts_the_drain_is_recorded_as_an_error` both
set a 300 ms drain to reach the cancelled/abandoned paths. They now use a connector that
**under-reports its own worst case** (`FakeConnector.WorstCaseOperationPath` gained an `init`) — which
is the realistic shape of that failure anyway: a vendor SDK call that blocks for longer than the vendor
admits is exactly what `handlers_did_not_finish` exists to report. The drain derives itself correctly
from what it is told; what it is told is wrong.

### PRIOR 2 — "disposal never returns unsettled", and what is still only logged

Codex's CHECK (d) named three mechanisms. Two are causes and are closed above (the undersized
multiplier, the undersized override). The third — `DisposeAsync` returning after merely LOGGING an
unfinished handler — is the symptom, and it is reached now only by a handler that outlasts a bound
derived correctly from what the connector declares. **That remaining exit is deliberate and stays:** a
handler that will not finish must not be able to hold the app open for ever, and the only thing that
still produces one is a call that does not honour its cancellation token. It is logged at `error`
because it is the sole trace that an order may have been left unsettled.

The acceptance is Codex's cold-placement check, and it is the new test
`Disposal_covers_a_cold_placement_and_not_just_the_call_it_is_inside`: five calls of 1 s each, the
latency **uncancellable** on purpose (a merely slow broker unwinds at the cancel and records UNKNOWN,
which hides the harm as "an order that needs reconciling"; a call that ignores the token is what leaves
the row DISPATCHING with nothing coming to change it), disposal called 1.2 s in with three calls still
ahead. At the round-8 count of three: **`1 request(s) left DISPATCHING: the drain came out at 3.20s
against a five-call chain that needs 5.00s`**. At five: FILLED, no `handlers_did_not_finish`, one order
on the broker, all read the instant `DisposeAsync` returns.

**THE PRICE WENT UP AND IT IS A PRODUCT DECISION, NOT AN ARITHMETIC ONE — the manager's to take.** At
shipped ATAS values the drain is now **5 × 50 + 5 = 255 s**, and disposal's ceiling **5 + 255 + 5 =
265 s** (round 8: 155 s / 165 s). It is paid ONLY while a request is genuinely in flight — an idle
handler is freed when its pipe closes, before this wait. The alternative is the order that reached the
broker and is recorded DISPATCHING for ever.

**WHAT THE BOUND STILL DOES NOT COVER, named rather than left to be found.** It is the bound for ONE
handler. `TradingGateway._dispatchGate` is a mutex, so N placements in flight together queue on each
other and cost N chains, while `DisposeAsync` waits for all of them under this one bound. That was true
before this round and this round does not change it; it is now stated in the source next to the number
so the 255 s is not read as covering everything. **NOT verified: what N can be in practice** — it is
bounded by how many agent connections are live, which this unit does not fix.

### F1 — the per-leg vocabulary, and the fifth word the rule turned out to need

There were THREE words for six situations, and two of them were lies. `Collect` decided the word from
the shape of the return rather than from the record:

- `RefuseCancel=1` → the broker definitively refuses, the gateway records **REJECTED**, and the reply
  said `sent-not-confirmed` — which means "UNKNOWN, and the gateway will reconcile it". It sent the
  owner to hunt through ATAS for the state of an order the broker had already answered about, and
  safety rule 3 exists to keep those two apart in the other direction.
- a target resolution that expired before `_requests.TryCreate` → `sent-not-confirmed` with
  `attempted=0` and **no record anywhere**: two claims contradicting each other in the same object,
  and the dangerous one is the word — it says an order may be live at the broker when this process
  never touched the wire.

**The rule that replaces them: `Classify(record)` reads the outcome OFF the record**, and nothing else
may construct one. A word can only be produced by the state that means it, so `sent-not-confirmed` now
really does imply UNKNOWN + reconciliation, which is the guarantee Codex asked for and the only reason
the word is worth having.

| word | record states | what the owner is being told |
|---|---|---|
| `sent-and-confirmed` | CANCELLED, FILLED | this leg's own intent is done |
| `rejected` | REJECTED | a definite refusal; nothing is working from this leg, nothing to reconcile |
| `sent-still-working` | WORKING, ACKNOWLEDGED, PARTIALLY_FILLED, CANCEL_PENDING | sent, answered, and still out there |
| `sent-not-confirmed` | UNKNOWN, DISPATCHING, RECONCILING | it reached the wire, or may have; UNKNOWN + reconciliation |
| `not-sent` | no record, or CREATED / AWAITING_APPROVAL | it never reached the wire |
| `nothing-to-do` | (no record, and the leg returned none) | there was nothing for this leg to act on |

**`sent-still-working` is a FIFTH word where the bounce named four, and the bounce's own rule is what
requires it.** `sent-not-confirmed` is defined as UNKNOWN + reconciliation. A `close-all` leg places an
offsetting order, and an offsetting order that rests rather than filling is WORKING — sent, answered,
definitely not unknown and definitely not done. With four words it fell into `sent-not-confirmed` and
promised a reconciliation that will never happen, which is the same defect Codex named, reached by a
third route. It is reachable, not hypothetical: `A_close_leg_whose_order_rests_reads_still_working_not_unknown`
produces it through the pipe. **Flagged for the manager as the one place this round widened the brief's
vocabulary, with that reason.**

`Describe()` also lost its `_ => "not-sent"` catch-all, which would have reported a new outcome as
"nothing was even attempted" — the most dangerous of the six to be wrong about — silently. A new
member now throws in the first test that reaches it.

**`attempted` is counted from the outcomes** (`Outcome is not NotSent and not NothingToDo`) rather
than from "legs holding a record", so it cannot disagree with the words beside it. **This CHANGES
`close-all`'s number:** a symbol with nothing to close was counted as attempted, while already being
listed by name in `nothing_to_close` — the same over-claim `bdf9a24` removed from `cancelled`, one
field over. `cancel-all`'s number is unchanged at every value the suite exercises (asserted by the two
pre-existing count tests, which still pass unedited).

**One existing assertion had to be relaxed, and it is worth naming.** `A_five_order_sweep…` asserted
that every `not-sent` leg's error contains "not sent". There are now two honest reasons for the word:
a leg whose turn came after the deadline (the pre-issue branch, whose message says exactly that) and a
leg that WAS issued and gave up on its own target resolution before the wire. The second carries the
underlying failure verbatim — from the simulator, `the operation deadline passed before the simulator
answered; it is not known whether it acted`. The test now requires every `not-sent` leg to carry a
reason and at least one to carry the pre-issue reason, so that branch is still proven reachable.
**The design point, stated: the WORD is the claim and the `error` is the cause.** A read that failed is
honestly "not known whether it acted" about the READ, while the leg it belonged to reached nothing —
and on the real connector that sentence is already correct for a read (round 6's F-D:
`'orders' could not be read, so the operation was not started. Nothing was placed or cancelled.`). The
mismatch is in the SIMULATOR's generic wording, which does not know which op it is serving. **NOT
fixed, and not verified beyond reading `FakeConnector.Wire`:** making it op-aware is a fixture change
with no product consequence, and it is left for the manager to decide.

**Five mapping arms, five mutants, all five bit** (`SweepRequestIdTests`, 33 tests):

| arm mutated to `NotConfirmed` | who bit |
|---|---|
| CANCELLED/FILLED → | `A_definite_broker_refusal…` (the cancelled leg starts reading `sent-not-confirmed`) |
| REJECTED → | `A_definite_broker_refusal…` |
| no record (the exception path) → | `A_leg_that_failed_before_the_wire…` |
| WORKING/ACKNOWLEDGED/… → | `A_close_leg_whose_order_rests…` |
| CREATED/AWAITING_APPROVAL → | `A_close_leg_parked_for_approval…` |

**The last one is honest bookkeeping: that arm SURVIVED first.** Mutating it passed all 32 tests,
because nothing in the suite produced a parked leg. `A_close_leg_parked_for_approval_reads_not_sent_and_is_not_counted_as_attempted`
was written for it — a `close-all` in LIVE_CONFIRM parks its offsetting order as AWAITING_APPROVAL and
`PlaceAsync` refuses, so a record exists and nothing reached the broker. Classifying by "the leg threw"
would call that `sent-not-confirmed` and ask the owner to reconcile an order sitting on their own
screen waiting for them to press Approve. With the test, the mutant bites.

### Round 9 close — gates, counts and the test-name diff (2026-09-04)

Tip **`088c059`** (7 commits on `5624cd1`), branch `u2a-rebase-probe`, tree clean.

**Build gate — `dotnet build TradeAgent.sln --no-incremental`** (the addendum's rule; an incremental
build recompiles nothing and re-emits no warning):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.53                                                        (exit 0)
```

**FULL suite, once, on the Mac — `dotnet test TradeAgent.sln`:**

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 977 ms   - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 283, Skipped: 0, Total: 283, Duration: 5 m 53 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**466 green (75 / 108 / 283), 0 failed, 0 skipped** — 455 at `5624cd1` plus 11.

**Test-name diff `5624cd1` → `088c059` — REMOVED: 0.** Method names extracted at both shas
(`git grep -n -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`, reduced to `path::method`, sorted
unique): **362 → 372**, ten new methods. Eleven test CASES, because one of them is a two-row `[Theory]`:

```
tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs::An_absolute_deadline_that_has_passed_leaves_nothing_not_a_millisecond
tests/TradeAgent.IntegrationTests/ConnectorSendDeadlineTests.cs::Nothing_is_left_awaiting_a_late_answer_when_the_grace_ends_early   [Theory ×2]
tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs::A_cold_placement_issues_no_more_connector_calls_than_the_drain_assumes
tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs::Disposal_covers_a_cold_placement_and_not_just_the_call_it_is_inside
tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs::No_combination_of_settings_makes_the_drain_shorter_than_the_chain
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_close_leg_parked_for_approval_reads_not_sent_and_is_not_counted_as_attempted
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_close_leg_whose_order_rests_reads_still_working_not_unknown
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_definite_broker_refusal_reads_rejected_and_needs_no_reconciliation
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::A_leg_that_failed_before_the_wire_reads_not_sent_and_writes_no_record
tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs::The_simulators_two_latencies_add_up_rather_than_competing
```

272 + 11 = 283, which is what ran. The diff was taken after every structural edit, not only at the end.

**Scope.** Seven files changed, and none of them is a forbidden one
(`git diff --name-only 5624cd1..HEAD | grep -E 'TradingGateway.cs|DashboardView.cs|Stores.cs|GatewayTypes.cs'`
→ no match):

```
src/TradeAgent.ConnectorSdk/RiskReducingScope.cs      +29
src/TradeAgent.Connectors.Atas/AtasConnector.cs       +40 −…
src/TradeAgent.Connectors.Fake/FakeConnector.cs       +34 −…
src/TradeAgent.Gateway/GatewayPipeServer.cs          +246 −…
tests/…/ConnectorSendDeadlineTests.cs                 +90
tests/…/GatewayPipeBackpressureTests.cs              +304
tests/…/SweepRequestIdTests.cs                       +271
```

No `Co-Authored-By` trailers (`git log 5624cd1..HEAD --format=%B | grep -ci co-authored` → `0`).
Every mutant was applied to a `cp` copy's original, `touch`ed, run, then restored from the `cp` copy
and `touch`ed again — never `git checkout --`; `git status --short` empty after each.

### What I did NOT do (round 9)

- **Did not run on the Windows box.** No grant this round. **NOT verified on Windows: every figure in
  this section**, including the 466 and the 0-warning build. The round-8 section's box run was at
  `5624cd1`; this round's seven commits have not been on the box.
- **Did not run the Codex leg or any adversarial-verify leg.** This session is the builder, and under
  R1 nothing here is a verdict.
- **Did not re-measure any earlier round's evidence.** Rounds 4b–8 above are carried forward as their
  builders left them. What I verified about them is that the tree they produced still builds clean and
  is green with my changes on top.
- **Did not modify `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs` or `GatewayTypes.cs`.** I
  **read** `TradingGateway.cs` — counting the connector chain a handler issues cannot be done without
  it — and changed nothing there; the `git diff --name-only` above is the proof.
- **Did not change the CS8619 class of gate program-wide.** `<TreatWarningsAsErrors>` is proposed in
  the F5 note and not taken.
- **Did not close the `close-all` fan-out question.** `RunLegs` issues legs in waves of four and each
  close leg's trailing `Place` is served the ordinary bound, so a book with many positions costs more
  than one `RiskReducingHandlerPath`. The drain covers ONE wave's shape. **NOT verified: what that
  costs at a realistic book size** — it needs a fixture with many positions and belongs with whoever
  owns the sweep's concurrency.
- **Did not bound cross-handler queueing.** `TradingGateway._dispatchGate` is a mutex; N concurrent
  placements cost N chains under one drain. Named in the source and above; not fixed, and it is not
  new this round.
- **Did not make the simulator's deadline message op-aware.** Named under F1; a fixture-wording issue
  with no product consequence.
- **Did not touch the installed app, ATAS, the real home, or any branch other than
  `u2a-rebase-probe`.** Nothing pushed, merged, rebased or moved; `u2a-pipe-hardening` untouched; no
  git command run in the main worktree.
- **Did not run `probe atas`, place, modify or cancel any real order.**

## Round 10 (build record, 2026-09-04)

Bounce on `088c059` from `briefs/U2a-r10-bounce.md`: the Codex delta on round 9
(`records/codex-U2a-r9.txt`, 2H/2M/1L) plus the fresh verifier's rounds 8+9 record
(`records/U2a-verify-r9.md`, FAIL 0H/2M/3L). **Fresh builder** — the round-9 builder's session is
gone; nothing in the round-9 table was re-measured by me except where this section says it was.
`TradingGateway.cs`, `DashboardView.cs`, `Stores.cs` and `GatewayTypes.cs` were **read but not
modified** (`git diff --name-only` at the end of this section is the proof).

**PRIOR F5 refuted, and re-run.** Codex's "no round-9 non-incremental build showing zero warnings is
recorded" read a stale snapshot of the branch: round 9's close on `main` quotes it. Re-run at my own
tip this round — the figure is in the round close below.

**F2 (Codex HIGH) is DEFERRED-BY-DECISION to U2c-1, class C1**, per the brief. A `close`'s final
offsetting `Place` is excluded from the emergency scope and takes fresh gate/frame/reply budgets
(`AtasConnector.cs:1018`, Codex's line). That is round 4's rule (`Place`/`Modify` never take the fast
path) meeting the U2c-1 item "carry a `Close` intent through `ITradingConnector` so close legs are not
`Place`s" — once U2c-1 lands, those legs inherit the scope by INTENT rather than by op name. **I did
not give a `Place` the emergency budget**, and the drain table below prices the trailing placement at
the ordinary bound precisely because it still takes one.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F1 + PRIOR 2** (H, class) the drain models a risk-reducing handler as `E + W`; a `close-all` wave is `E + L·W` | **RED 3** — arithmetic at Codex's own values: `the drain came out at 39s against a close-all wave that needs E + 4W + S = 51s`; measured: `'close-all' took 4.53s against a drain of 3.80s`; disposal: one position (`P-YM`) still open when `DisposeAsync` returned | 27/27 `GatewayPipeBackpressureTests` | wave term back to `1 × W` → **RED 3**, the same three | `c88ea48` |

### F1 + PRIOR 2 — the handler table, and why enumerating them is the class fix

Three rounds have now found the drain derived from ONE handler's shape and silently short for
another: round 8 from a single connector call, round 9 from a three-call chain that was really five,
round 10 from a risk-reducing handler with one trailing placement that really has four. The fix is
not a fourth arithmetic correction: `GatewayPipeServer.HandlerPaths` enumerates EVERY handler with
its own serial depth, and the drain is the maximum over that table plus the settle margin. A handler
is covered because it is IN the table.

Terms, all read off the live connector: **W** = `WorstCaseOperationPath` (one ordinary call), **E** =
`EmergencyBudget` (the whole risk-reducing part of one operation), **L** = `MaxLegsInFlight` (4),
**S** = `SettleAfterCancelTimeout`.

| handler | serial depth | why that is the chain |
|---|---|---|
| `status` `accounts` `account` `instruments` `quote` | **2W** | an account resolution, then the read |
| `positions` `position` `orders` `order` `executions` | **2W** | the account, then the read |
| `material-list` `material-note` | — | no connector call at all |
| `buy` `sell` | **5W** | a cold placement: account → positions → quote → instruments → place |
| `modify` | **4W** | the account, the orders read that resolves the target, the account again, the modify |
| `cancel` | **E** | resolve the target, then cancel — every call risk-reducing, so the whole handler is the one budget |
| `cancel-all` | **E** | the orders read and every leg, all inside the one budget |
| `close` | **E + W** | the prefix inside the budget, then ONE ordinary placement |
| `close-all` | **E + L·W** | the prefix inside the budget, then one WAVE of placements, serialised on `_dispatchGate` |

```
drain = max(that table) + S
```

The same table is now in `docs/CONTRACTS.md` — it is a release fact, not an implementation detail.

**At shipped ATAS values the number does not move:** `max(5×50, 2 + 4×50) + 5 = 255 s`, exactly what
round 9 recorded, because the ordinary placement still dominates there. The term that was missing
bites where `E` is large relative to `W`, which is Codex's own fixture and the suite's own disposal
fixture.

**Why ONE wave and not the whole book, stated because it is the load-bearing step.** `RunLegs` checks
the operation deadline before issuing each leg, so once `E` is gone every remaining leg is reported
`not-sent` rather than issued. At the instant the last wave is issued, less than `E` has elapsed; that
wave then costs at most `L·W` more. A book of two hundred positions therefore costs `E + L·W`, not
`E + 50·W`.

**The measured half is a `[Theory]` with one row per handler** —
`Every_handlers_measured_chain_fits_inside_the_drain_derived_for_it` — which drives each handler over
the real pipe at a fake latency and asserts the derived bound still covers what it actually cost. The
fixture's emergency budget sits just above one `close-all` leg's read prefix (5 × W) on purpose: with
a wider budget the legs still run but the wave stops being the longest thing in the table and the row
proves nothing. `close-all` measured **4.53 s** against a round-9 drain of **3.80 s**.

**The disposal case, and the honest limit of it.** `A_close_all_wave_that_disposal_lands_in_leaves_
nothing_unsettled` disposes twice: once with the whole prefix and wave still ahead, and once
MID-WAVE, after a placement of the wave has actually reached the broker. Only the first
discriminates, and the arithmetic says why it must: mid-wave disposal has at most `L·W` left to cover
while the round-9 drain already allowed `E + W + S`, and `E` exceeds the prefix by construction — so
`3W > E + S` is unreachable. The mid-wave landing is asserted because the bounce names it, not
because it can fail on its own.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F4 + F-1** (M, class) the leg word is read off the record, which cannot carry wire certainty | `Expected: "not-sent" / Actual: "sent-not-confirmed"` on a leg the connector refused before the wire | 35/35 `SweepRequestIdTests`; 43/43 `ConnectorSendDeadlineTests` | `NothingWritten → NotConfirmed` → **RED 1 of 35**; the bridge's gate refusal claims `PossiblyWritten` → **RED 1 of 43** | `d931e0c` |
| **F3** (M) the per-leg vocabulary is six words | `Item: Tuple ("sent-and-confirmed", "CANCELLED", …)` not in `["confirmed","rejected","sent-still-working","sent-not-confirmed","not-sent"]` | 37/37 `SweepRequestIdTests` | the word back to `sent-and-confirmed` + `nothing_to_do` hard-coded false → **RED 3 of 37** | `756e7e5` |
| **F-3** (L) `Classify` kept the catch-all `Describe()` had lost | `Assert.Throws() Failure: No exception was thrown` — an unmapped state became `sent-not-confirmed` | 38/38 `SweepRequestIdTests` | the catch-all restored → **RED 1 of 38** | `6181633` |
| **F-2** (M, my half) disposal returns silently on a request nothing will settle | `Expected: "error" / Actual: null` — `handlers_did_not_finish` not logged with a token-honouring connector | 28/28 `GatewayPipeBackpressureTests` | the sentinel counts handler tasks again → **RED 1** | `04aed45` |
| **F-5** (L) the five-order acceptance passes with `attempted = 0` | measured: every leg `not-sent` (`["not-sent" ×5]`), so "which sent, which confirmed" is never exercised | 38/38 `SweepRequestIdTests` | `LoseAfterSend` reports `NothingWritten` → **RED 1 of 38** | `12e2c65` |

### F4 + F-1 — the connector reports where the frame got to, and that is what names the leg

Round 9's rule was *"the record decides the word"*. It is right that a word must be producible only by
the thing that means it, and wrong about the record being able to mean it. **`TradingGateway` maps
EVERY `ConnectorTransportException` to UNKNOWN** (`TradingGateway.cs:660-665`), correctly — from up
there a refusal before the send gate and a half-written frame are the same exception. Down in the
connector they are not, and that difference is the whole of the verifier's F-1.

**The mechanism, and why it is not a signature change.** `TransportOutcome` — the tri-state `trade`
has used since round 2 — moved to `TradeAgent.Core` so it has ONE definition for both users, and a new
`TransportLedger` (ConnectorSdk, `AsyncLocal`, the same shape as `RiskReducingScope`) carries one
`TransportRecord` per sweep leg. `RunLegs` attaches a record before it starts a leg, so the value
flows DOWN into that leg's execution context and the leg's connector calls mutate the object the loop
still holds — a wave of four concurrent legs each has its own and none can see another's. **Only
MUTATIONS are recorded** (`AtasConnector.Mutates(op)`): a leg is a read to resolve its target and then
the thing it came to do, and recording the read would report "a reply was received" for a leg whose
cancel never left the process.

The rule, and it is two sources answering two different questions:

1. A record in a state only a BROKER'S ANSWER can produce — CANCELLED, FILLED, REJECTED, WORKING,
   ACKNOWLEDGED, PARTIALLY_FILLED, CANCEL_PENDING — is itself proof the round trip completed, and it
   says WHICH answer came back. (An idempotent replay arrives here with no transport of its own, which
   is why it does not read `not-sent`.)
2. Everything else — CREATED, AWAITING_APPROVAL, DISPATCHING, UNKNOWN, RECONCILING, or no record —
   is a state the record cannot settle, and the CONNECTOR's transport result decides: nothing
   attempted or `NothingWritten` → `not-sent`; `PossiblyWritten` or `ReplyReceived` →
   `sent-not-confirmed`, which is the fail-closed direction.

**The shipped connector's report is measured, not modelled.** `A_refusal_that_never_took_the_send_gate_
reports_that_nothing_was_written` drives the real `AtasConnector` over a real pipe through all three
ways an emergency fails without the gate: the operation already over, `Busy` behind our own backlog,
and a gate-expiry `PeerStalled` — different facts about the far end, the same fact about the frame.
`An_answered_frame_reports_a_reply_and_an_unanswered_one_reports_it_may_have_landed` supplies the other
two states, so `NothingWritten` is a measurement rather than the only answer the connector can give.

**THE RESIDUAL, AND IT IS NOW MEASURED RATHER THAN ARGUED.** The RECORD is still UNKNOWN with
`NeedsReconciliation` set, because `TradingGateway.SettleUnknown` writes it and this unit may not open
that file. The word is fixed and the leg now carries the connector's own report in a new `transport`
field, so the answer states its evidence instead of leaving two fields to disagree in silence. What
the flag then does was measured this round, by accident, while building the F-5 mix — and it is worse
than the verifier's account:

```
op-…-cancelall-0  not-sent  state=UNKNOWN  transport=NothingWritten
                  error="it was not sent: the operation ran out of time before this leg's turn came"
op-…-cancelall-1  not-sent  error="1 earlier request(s) are unconfirmed"
op-…-cancelall-2  not-sent  error="1 earlier request(s) are unconfirmed"
op-…-cancelall-3  not-sent  error="1 earlier request(s) are unconfirmed"
op-…-cancelall-4  not-sent  error="1 earlier request(s) are unconfirmed"
```

**One leg the connector PROVED it never sent paused the remaining four legs of its own sweep.** The
UNKNOWN row it wrote sets the flag, and `TRADING_PAUSED_UNRECONCILED` refuses everything after it —
so on the one command a person reaches for when they want everything to stop, a leg that did nothing
stops the sweep. **ROUTED TO U2c-1 with this measurement:** `CancelAsync`/`ModifyAsync` must not
settle UNKNOWN for a transport failure the connector reports as `NothingWritten`.

### F3 — five words, and why `nothing-to-do` was a category error

`sent-and-confirmed` led with a claim about the wire when the content of the word is the BROKER'S
answer, which put it in the same shape as the two words that really are about the wire. It is
`confirmed`. And `nothing-to-do` was a fact about the OPERATION wearing a leg's clothes: a leg exists
because there was something for it to act on. It is now `nothing_to_do` on the sweep itself, true when
there were no targets at all; a `close-all` symbol whose position had gone by the time its leg ran is
`not-sent` and is still named in `nothing_to_close`.

`The_per_leg_vocabulary_is_exactly_five_words_over_every_reachable_combination` drives the mapping
over the FULL cross product — every `ExecutionState` (plus "no record") against every
`TransportOutcome` (plus "nothing attempted") — through a seam exported for it, `LegWordFor`. Both
directions: no combination may produce a sixth word, and all five must be produced by some
combination, so a mapping that refused everything would not pass. A membership test over the replies
some fixture happens to produce could only ever cover the arms those fixtures reach, which is exactly
how the round-9 mapping shipped with an arm no test touched.

### F-2 — my half, and the half that is not mine

Round 9 recorded that the only thing which still produces an abandoned handler is "a call that does
not honour its cancellation token". The verifier refuted it with a connector that DOES honour it, and
I reproduced the refutation before changing anything: `handlers_did_not_finish` — `Expected: "error" /
Actual: null` — with the row DISPATCHING, `needs_reconciliation=0`, and `ReconcileAsync` scanning
`NeedingReconciliation()` alone, so nothing would ever settle it.

The sentinel counted the wrong noun. A token-honouring connector unwinds the instant disposal cancels
it, so the HANDLER finishes; `TradingGateway.ModifyAsync` catches only `ConnectorRejectedException`
and `ConnectorTransportException` and lets the cancellation escape, so the REQUEST does not. It now
counts requests still DISPATCHING when `DisposeAsync` returns and **names them** — "something was
abandoned" is not something anybody can act on. Disposal still waits the full derived drain before
cancelling anything, and the test asserts that too, because a shutdown that cancelled early would
produce this row for a handler that had time left.

**Settling that row is DEFERRED-BY-DECISION to U2c-1** with the verifier's measurement and mine:
`CancelAsync`/`ModifyAsync` must catch `OperationCanceledException`/`TimeoutException` the way
`DispatchPlaceAsync` already does at `TradingGateway.cs:481`, so a cancelled mutation is recorded
UNKNOWN and reconciled. `TradingGateway.cs` is not this unit's to edit.

### F-5 — a mixed answer, and the fixture fact that makes it reachable

The acceptance was satisfiable by a sweep that attempted nothing: at a second per leg the orders read
plus one target resolution is the whole two-second budget, every leg comes back `not-sent`, and
`not_sent > 0` is satisfied. The test now runs a SECOND sweep over the same five still-working orders
with the simulator quick and two one-shot faults armed, and asserts `confirmed`, `rejected`,
`not-sent` and `sent-not-confirmed` by name in one answer, with `attempted` equal to the number of
legs whose word is not `not-sent` and reconciliation flagged on exactly the leg that says so.

**Fifty milliseconds of latency is load-bearing in that fixture and the comment says so.** At zero
latency the simulator never awaits, so `issue()` runs each leg to completion before the loop starts
the next and the wave is serial — the first UNKNOWN then refuses the other three and every word in
the answer is the same one. Two harness additions were needed and both model real connector
branches: `FaultProfile.LoseAfterSend` (the frame went out, no answer came back — the reply-timeout
branch, and the only honest way a CANCELLATION becomes UNKNOWN in the simulator), and
`FaultProfile.Take` is now locked, because read-then-decrement across four concurrent legs can hand
one use to two of them and leave a third fault unconsumed.

### The Windows box — the granted run, what it proved and what it FOUND

Manager grant, one run, at the end of the round. Taken at `12e2c65`, before the fixture fix below.

**The tree really was mine, before AND after.** `tools/win-push.sh` (796 K packed, 157 files
unpacked), then SHA-256 of five changed files plus the `.cs` count under `src` + `tests` — identical
to my worktree on both sides of the run:

```
dfb994dd0588f33d88e24bf03b1c0124e610a6a7836f6587115c148b94ba769e  …\src\TradeAgent.Gateway\GatewayPipeServer.cs
7dd7ac2c7c0b8fdd85d6f2389a4f06e6f70115c7b967cd5cf9a1c8d3605c49f2  …\src\TradeAgent.Connectors.Atas\AtasConnector.cs
e11bdc1842b3be5d408928fa13056ccd00ad35c909092f57e97a2d400beb3d50  …\src\TradeAgent.Connectors.Fake\FakeConnector.cs
dcc9e620429668f3eb34831cd14e3a92681dcf1d46b7762d2248e2d8fe313df0  …\src\TradeAgent.ConnectorSdk\TransportLedger.cs
7bc5e2e053989f300afa467438e3d18f89c7a3fbb241719c92194f780448565a  …\tests\TradeAgent.IntegrationTests\SweepRequestIdTests.cs
cs files under src+tests: 90                                      (Mac: 90)
```

One SSH session, `DESKTOP-K8VRIT9`, console session Active, ATAS running, no UI agent (console work
only, which is all this needed):

```
=== BUILD (--no-incremental) ===      0 Warning(s)  0 Error(s)   BUILD EXIT=0
=== PIPE CLASSES ===                  Failed: 1, Passed: 108, Total: 109, 6 m 16 s   PIPE EXIT=1
=== FULL SUITE ===                    UnitTests   Passed: 108 / 108
                                      FaultTests  Passed:  75 /  75
                                      Integration Failed: 2, Passed: 299, Total: 301, 6 m 37 s
                                      SUITE EXIT=1
```

**This is the first time round 9's seven commits have been built or run on Windows at all**, and the
build is clean there. Two failures, and they are different in kind.

**BOX FAILURE 1 — MINE, and the box is right.** `A_five_order_sweep_answers_within_the_budget_and_
accounts_for_every_order` — `Collection: ["not-sent" ×5] / Not found: "confirmed"`, reproduced in BOTH
box runs. The mixed-outcome half I added for F-5 ran as a SECOND sweep on the gateway the first half
had already used, and whether that first sweep leaves a flagged request depends on where its deadline
falls — one flagged request refuses every leg of the next sweep with `1 earlier request(s) are
unconfirmed`. On macOS the first sweep's legs died in their target RESOLUTION, which writes no record;
on Windows one got further. **That is F-1's residual doing exactly what it is routed to U2c-1 for, and
it found me.** Fixed in `c00fa08` by giving the mixed sweep its own gateway and asserting the
precondition (`Assert.Empty(gw.Requests.NeedingReconciliation())`) so a future coupling fails saying
so. **NOT VERIFIED ON THE BOX: the fix.** It is Mac-verified only — the grant was one run and it was
spent; `c00fa08` and the final gate below have never been on Windows.

**BOX FAILURE 2 — NOT MINE, AND NOT DETERMINISTIC.** `An_emergency_a_busy_bridge_has_not_answered_yet_
is_unknown_but_not_a_drop` — *"a bridge that was answering requests throughout was dropped"*
(`ConnectorSendDeadlineTests.cs:846`). It is a round-7-era timing test I did not touch. **It PASSED in
the pipe-class run and FAILED in the full-suite run, on the same box, the same binaries, 2½ minutes
apart** — so it is load-dependent on Windows. **NOT verified: whether it is a pre-existing Windows
flake or something this round made likelier.** I did add `TransportLedger.Record` calls to
`AtasConnector`'s failure paths; outside an attached record each is one null-check on an `AsyncLocal`,
which is not a plausible cause but is not ruled out by argument alone. It needs a repeat box run, and
it is the first Windows figure this test has ever had.

### Round 10 close — gates, counts and the test-name diff (2026-09-04)

Tip **`c00fa08`** (7 commits on `088c059`), branch `u2a-rebase-probe`, tree clean.

**Build gate — `dotnet build TradeAgent.sln --no-incremental`** (and this is PRIOR F5 re-run and
re-quoted, which Codex read as unrecorded off a stale branch snapshot):

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.47                                                        (exit 0)
```

**FULL suite, on the Mac, at the tip — `dotnet test TradeAgent.sln`:**

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 972 ms   - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 302, Skipped: 0, Total: 302, Duration: 6 m 34 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**485 green (75 / 108 / 302), 0 failed, 0 skipped** — 466 at `088c059` plus 19.

**A SIBLING SESSION'S TEST RUN OVERLAPPED MINE ON THIS MAC** (a U14 leg in
`…-worktrees/u14-build`, seen in `ps` mid-run). Each test assembly redirects `TRADEAGENT_HOME` to its
own scratch directory and each `TestEnv.NewDb()` is a fresh file, so there is no shared state — but
there is shared CPU, and the durations above are not comparable with round 9's. The counts are.

**Test-name diff `088c059` → `c00fa08` — REMOVED: 0.** Method names extracted at both shas, reduced
to `path::method`, sorted unique: **372 → 384**, twelve new methods, nineteen new CASES (one is an
eight-row `[Theory]`):

```
tests/…/ConnectorSendDeadlineTests.cs::A_refusal_that_never_took_the_send_gate_reports_that_nothing_was_written
tests/…/ConnectorSendDeadlineTests.cs::An_answered_frame_reports_a_reply_and_an_unanswered_one_reports_it_may_have_landed
tests/…/GatewayPipeBackpressureTests.cs::A_close_all_wave_that_disposal_lands_in_leaves_nothing_unsettled
tests/…/GatewayPipeBackpressureTests.cs::A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error
tests/…/GatewayPipeBackpressureTests.cs::Every_handlers_measured_chain_fits_inside_the_drain_derived_for_it   [Theory ×8]
tests/…/GatewayPipeBackpressureTests.cs::The_drain_covers_a_close_all_wave_and_not_just_one_trailing_place
tests/…/SweepRequestIdTests.cs::A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer
tests/…/SweepRequestIdTests.cs::A_leg_refused_before_the_wire_reads_not_sent_even_though_its_record_is_unknown
tests/…/SweepRequestIdTests.cs::A_sweep_with_no_targets_says_so_as_a_whole_and_not_on_any_leg
tests/…/SweepRequestIdTests.cs::An_execution_state_nothing_maps_throws_rather_than_becoming_a_word
tests/…/SweepRequestIdTests.cs::Every_sent_not_confirmed_leg_carries_an_unknown_record_that_will_be_reconciled
tests/…/SweepRequestIdTests.cs::The_per_leg_vocabulary_is_exactly_five_words_over_every_reachable_combination
```

283 + 19 = 302, which is what ran. The diff was taken after every structural edit, not only at the end.

**Scope.** Eleven files changed, and none of them is a forbidden one
(`git diff --name-only 088c059..HEAD | grep -E 'TradingGateway.cs|DashboardView.cs|Stores.cs|GatewayTypes.cs'`
→ no match):

```
docs/CONTRACTS.md                                     +72
src/TradeAgent.ConnectorSdk/TransportLedger.cs        +98   (new)
src/TradeAgent.Core/TransportOutcome.cs               +33   (new — moved out of TradeCli)
src/TradeAgent.Connectors.Atas/AtasConnector.cs       +50 −…
src/TradeAgent.Connectors.Fake/FakeBroker.cs          +39 −…
src/TradeAgent.Connectors.Fake/FakeConnector.cs       +83 −…
src/TradeAgent.Gateway/GatewayPipeServer.cs          +284 −…
src/TradeAgent.TradeCli/TransportResult.cs            −25   (the enum moved to Core)
tests/…/ConnectorSendDeadlineTests.cs                +128
tests/…/GatewayPipeBackpressureTests.cs              +399
tests/…/SweepRequestIdTests.cs                       +262
```

Seven commits, one per finding. No `Co-Authored-By` trailers
(`git log 088c059..HEAD --format=%B | grep -ci co-authored` → `0`). Every mutant was applied to a `cp`
copy's original, `touch`ed, run, then restored from the `cp` copy and `touch`ed again — never
`git checkout --`; `git status --short` empty after each.

### Routed onward, with measurements attached

- **F2 (Codex HIGH) — U2c-1 class C1.** A `close`'s final offsetting `Place` takes fresh gate, frame
  and reply budgets inside an emergency operation. Deferred by the brief's decision; once a `Close`
  intent travels through `ITradingConnector`, those legs inherit the scope by intent instead of by op
  name. Not given the emergency budget here.
- **F-1's RECORD half — U2c-1.** `TradingGateway.SettleUnknown` writes UNKNOWN + `NeedsReconciliation`
  for a transport failure the connector reports as `NothingWritten`. Measured this round: **one leg
  the connector proved it never sent paused the remaining four legs of its own sweep**, and it then
  reproduced on Windows as a test failure. `CancelAsync`/`ModifyAsync` must not settle UNKNOWN for a
  `NothingWritten` transport result.
- **F-2's SETTLEMENT half — U2c-1.** `CancelAsync`/`ModifyAsync` catch only `ConnectorRejectedException`
  and `ConnectorTransportException`; `DispatchPlaceAsync` at `TradingGateway.cs:481` also catches
  `TimeoutException or OperationCanceledException`. Until they match, a mutation cancelled by disposal
  stays DISPATCHING and unflagged. It is now LOGGED by name at `error`, which is this unit's half.
- **The one-warning-per-future-regression policy** (`<TreatWarningsAsErrors>` in
  `Directory.Build.props`) is still proposed and still not taken — program-wide, outside this brief.

### What I did NOT do (round 10)

- **Did not verify the last two commits on Windows.** The box grant was ONE run and it was spent at
  `12e2c65`. **NOT VERIFIED ON THE BOX:** `c00fa08` (the fixture fix the box itself found) and the
  final Mac gate above. The box needs one more run, and it should be watched for the second failure
  below as well.
- **Did not settle the Windows failure of `An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_
  but_not_a_drop`.** It passed in the targeted run and failed in the full-suite run on the same
  binaries. **NOT verified: whether it is a pre-existing Windows flake or something this round made
  likelier.** I did not touch that test or its assertions.
- **Did not run the Codex leg or any adversarial-verify leg.** This session is the builder, and under
  R1 nothing here is a verdict.
- **Did not modify `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs` or `GatewayTypes.cs`.** I read
  `TradingGateway.cs` and `Core/Db/Stores.cs` — the handler table and the disposal sentinel cannot be
  written without knowing what they do — and changed neither; the `git diff --name-only` above proves
  it.
- **Did not give a `Place` the emergency budget** (F2 is deferred), and did not change
  `RiskReducingScope`'s exclusion of `place`/`modify`.
- **Did not clear or write any execution record from the pipe server.** The `not-sent` leg's UNKNOWN
  row is left exactly as the gateway wrote it; the reply now carries the connector's transport result
  beside it rather than the pipe server editing the row.
- **Did not re-measure any earlier round's evidence.** Rounds 4b–9 are carried forward as their
  builders left them; what I verified about them is that the tree they produced still builds clean and
  is green with my changes on top.
- **Did not bound cross-handler queueing.** `_dispatchGate` is a mutex; N concurrent placements cost N
  chains under one drain. Named in the source and in `docs/CONTRACTS.md`; **NOT verified: what N can
  be in practice.**
- **Did not make the simulator's deadline message op-aware** (carried from round 9), and did not close
  the `close-all` fan-out at realistic book size beyond the one-wave bound derived above.
- **Did not touch the installed app, ATAS, the real home, or any branch other than
  `u2a-rebase-probe`.** Nothing pushed to a git remote, merged, rebased or moved; `u2a-pipe-hardening`
  is still at `088c059`; no git command was run in the main worktree.
- **Did not run `probe atas`, place, modify or cancel any real order.** The only thing that reached the
  Windows machine is the source tree and `dotnet build` / `dotnet test`.

## Round 11 (build record, 2026-09-04)

Bounce on `c00fa08` from `briefs/U2a-r11-bounce.md`: the Codex delta on round 10
(`records/codex-U2a-r10.txt`, 12/15 priors FIXED, 1 HIGH-counted deferral, 2 new MED, 1 LOW).
**Fresh builder** — the round-10 builder's session is gone; nothing in the round-10 table was
re-measured by me except where this section says it was. `TradingGateway.cs`, `DashboardView.cs`,
`Stores.cs` and `GatewayTypes.cs` were **read but not modified** (`git diff --name-only` at the end
of this section is the proof).

**Deferred by the brief's decision, and written here rather than built.** Both are the GATEWAY's
half and both are already routed to U2c-1 class C4 with round 10's measurements:

- **PRIOR 2 — "disposal still returns with DISPATCHING unsettled"** (Codex's line
  `GatewayPipeServer.cs:1360`). U2a's half shipped in round 10: the sentinel counts REQUESTS still
  DISPATCHING when `DisposeAsync` returns and names them at `error`. Settling the row lands on
  `TradingGateway.cs:696-700` — `CancelAsync`/`ModifyAsync` must catch
  `OperationCanceledException`/`TimeoutException` the way `DispatchPlaceAsync` already does at
  `TradingGateway.cs:481`, so a mutation cancelled by disposal is recorded UNKNOWN and reconciled.
- **PRIOR V-F1 — "the never-sent leg still ends UNKNOWN + flagged"**. U2a's half shipped in round 10:
  the wire-side word is `not-sent` and the leg carries the connector's own `transport` beside it.
  The row lands on `TradingGateway.cs:660-665` — every `ConnectorTransportException` maps to
  `SettleUnknown`, and it must not for a transport result the connector reports as `NothingWritten`.
  Round 10's measurement stands: one leg the connector PROVED it never sent paused the remaining
  four legs of its own sweep with `1 earlier request(s) are unconfirmed`.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F2** (M, the DANGEROUS direction) a completed mutation records nothing until a reply, so a caller cancellation leaves the transport result null and a FULLY SENT leg reads `not-sent` | `Assert.Equal() Failure: Values differ / Expected: PossiblyWritten / Actual: null` — Codex's own fixture: the peer reads the whole cancel frame, withholds the reply, the caller cancels | 112/112 over the three pipe classes | drop the attempt fallback → **RED 2**, same `Expected: PossiblyWritten / Actual: null`; make the fallback beat an explicit report → **RED 3**: `Expected: NothingWritten / Actual: PossiblyWritten` and `Expected: ReplyReceived / Actual: PossiblyWritten` | `6fdca39` |
| **F1** (M) cancellation while waiting for the send gate bypasses the `NothingWritten` arm and the outer catch records `PossiblyWritten` | `Expected: NothingWritten / Actual: PossiblyWritten` on a cancel that never took the gate | 45/45 `ConnectorSendDeadlineTests` | drop the gate-cancellation arm → **RED 1**, same two lines; restore the outer catch's blanket `PossiblyWritten` → **RED 1**, same two lines | `805eaa9` |

### F2 — the fix is not another catch arm, it is making an empty record mean something

The hole Codex names is one exit: `SendOutcome.Sent` records nothing, and the reply wait's catch is
filtered `when (!ct.IsCancellationRequested)` precisely so a caller's OWN cancellation passes through
it. Between the write finishing and the reply arriving, the frame is entirely on the far side and the
record is empty — and an empty record means *no mutating call was ever attempted*, which the mapper
reads as `not-sent`. **`not-sent` is the one word in the set that is an ASSURANCE** — no
reconciliation, no pause — and it was being produced by an ABSENCE of information about an order that
may be at the broker.

**The brief's rule was "record `PossiblyWritten` the moment the frame is fully written, before any
reply wait". I did not do that, and the reason is measurable rather than stylistic.**
`TransportRecord.Observe` merges reports with THE MOST UNCERTAIN WINNING, and `PossiblyWritten` beats
`ReplyReceived` — so recording it at `Sent` and `ReplyReceived` at the reply leaves every answered
mutation reporting `PossiblyWritten`. That is not an argument: mutant B below is exactly that
collapse, and it fails round 10's `An_answered_frame_reports_a_reply_and_an_unanswered_one_reports_it_
may_have_landed` with `Expected: ReplyReceived / Actual: PossiblyWritten`. The connector would lose
the ability to say that a round trip completed.

What is implemented instead satisfies the same rule at the source and closes the class rather than
the instance: **`TransportLedger.Attempt()` is called the moment a mutating call STARTS**, and a
record that was attempted and never reported reads `PossiblyWritten`. Then:

- the exit Codex names is `PossiblyWritten` — his check passes, measured above;
- so is every exit nobody has enumerated yet, which is the part an arm on one catch cannot buy;
- `null` becomes PRODUCIBLE ONLY by work that never started a mutation, so the mapper's
  `null → not-sent` stops being a guess and becomes a proof. **This is why I did not make a bare
  `null` map to `sent-not-confirmed` as the brief's parenthesis says:** three reachable legs arrive
  with a genuinely empty record — a target resolution that failed before any mutation
  (`A_leg_that_failed_before_the_wire…`), a `close` leg parked as AWAITING_APPROVAL
  (`A_close_leg_parked_for_approval…`), and a `close-all` symbol with nothing left to close — and
  flagging those is the round-9 defect arrived at from the other side. The brief's *property* — a
  fully sent leg can never read `not-sent` — holds by construction; its *mechanism* would have
  broken three tests that are right.
- an explicit report always wins over the fallback, in BOTH directions (mutant B).

`FakeConnector` marks the attempt too, in `Wire` and in `PlaceOrderAsync`: its
`Task.Delay(LatencyMs, ct)` is the same unenumerated exit, and a cancelled simulated mutation used to
record nothing at all.

### F1 — the fourth way of not getting the gate

Three exits from the gate wait already report `NothingWritten` and can prove it: the operation was
already over, our own backlog was in the way, the peer had stopped reading. Cancellation is a fourth,
and it is as provable as the others — the gate is a semaphore, the frame is not built until the wait
returns true, and not one byte of it can exist. It was the one exit that fell through to `Rpc`'s
outer catch, which writes the fail-closed answer for everything it cannot identify.

**Two edits, and the second is the one that makes the first stick.** The gate wait's
`OperationCanceledException` now records `NothingWritten`; and the outer catch no longer records
`PossiblyWritten` at all, because `PossiblyWritten` beats `NothingWritten` in the merge, so leaving it
there overwrites the proof one line below with a guess. Nothing is lost by removing it: F2's attempt
fallback supplies the identical fail-closed answer for an exit nothing identified, and does it without
overwriting one that was identified. Both halves are watched by their own mutant.

**Observed, NOT fixed, and named for the manager:** the caller-cancellation exit also leaves the
request registered in `AtasConnector._pending` — `_pending.TryRemove(id, out _)` runs on the
reply-timeout branch and not on this one. It is bounded by the connection's lifetime (`Drop` faults
everything pending) and it is the same class as round 9's F2 `_abandoned` leak. Not in this brief and
not measured beyond reading the code.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **PRIOR R9-F4** (PARTIAL) definite-state arms bypass transport | **RED 7**, one per definite state: `CANCELLED + NothingWritten: expected 'not-sent', got 'confirmed'` · `FILLED + …: got 'confirmed'` · `REJECTED + …: got 'rejected'` · `WORKING / ACKNOWLEDGED / PARTIALLY_FILLED / CANCEL_PENDING + …: got 'sent-still-working'` | 41/41 `SweepRequestIdTests` | one mutant per arm, four arms, all four bit — see the table below | `a9b9cdd` |
| **F3** (L) the "exhaustive" drain table omits four handled operations | `the dispatcher handles connectors, material-list, material-note, schema and the drain table has no row for them` — discovered by asking the dispatcher, not by reading the switch | 30/30 `GatewayPipeBackpressureTests` | remove the `schema` row → **RED 2** (`the dispatcher handles schema …` and the measured theory's `schema` row: `Sequence contains no matching element`); add a row for an op nothing handles → **RED 1**: `the drain table has rows for flatten-everything, which the dispatcher does not handle` | `a59c7f0` |

### PRIOR R9-F4 — the one report allowed to overrule the record, and why the definite arms needed it

Round 10 gave the UNRESOLVED states a transport result and left the three definite arms reading the
record alone. That is one rule short of the guarantee the vocabulary is worth having:
`confirmed`, `rejected` and `sent-still-working` are each a claim that THIS LEG's frame reached the
broker, and the connector can prove that it did not. A record can be in a definite state for a reason
that has nothing to do with this leg — the connector's event stream updates request records, so a
sweep leg can find one already settled by something else. *"The record says CANCELLED"* and *"this leg
cancelled it"* are different sentences.

The rule, and it is now one line of code per arm:

| record | `NothingWritten` | `PossiblyWritten` | `ReplyReceived` | nothing attempted |
|---|---|---|---|---|
| CANCELLED · FILLED | `not-sent` | `confirmed` | `confirmed` | `confirmed` |
| REJECTED | `not-sent` | `rejected` | `rejected` | `rejected` |
| WORKING · ACKNOWLEDGED · PARTIALLY_FILLED · CANCEL_PENDING | `not-sent` | `sent-still-working` | `sent-still-working` | `sent-still-working` |
| no record · CREATED · AWAITING_APPROVAL · DISPATCHING · UNKNOWN · RECONCILING | `not-sent` | `sent-not-confirmed` | `sent-not-confirmed` | `not-sent` |

`NothingWritten` is the only report strong enough to overrule the record, and it overrules every arm.
Everything else defers to the record where the record can answer — **including a leg with no transport
of its own**, because an idempotent replay dispatches nothing and arrives with a settled record: it is
`confirmed`, not `not-sent`.

**NO CATCH-ALL WAS ADDED TO BUY THIS.** The transport check could have been a short-circuit in front
of the state switch, and that would have made a NEW `ExecutionState` become `not-sent` in silence
whenever the transport said `NothingWritten` — undoing verifier round-9 F-3. Each arm calls a helper
instead, so both switches stay exhaustive in both dimensions:
`An_execution_state_nothing_maps_throws_rather_than_becoming_a_word` still throws.

**The mutants, one per arm** (`SweepRequestIdTests`, 41 tests):

| arm mutated to ignore the transport | what it reported |
|---|---|
| CANCELLED/FILLED | `FILLED + NothingWritten: expected 'not-sent', got 'confirmed'` (+ CANCELLED) |
| REJECTED | `REJECTED + NothingWritten: expected 'not-sent', got 'rejected'` |
| WORKING/ACKNOWLEDGED/PARTIALLY_FILLED/CANCEL_PENDING | four rows, all `got 'sent-still-working'` |
| the unresolved arm (`NothingWritten` folded into `NotConfirmed`) | six rows, all `got 'sent-not-confirmed'`, **plus** `An_attempted_mutation_that_reported_nothing…` and `A_leg_refused_before_the_wire_reads_not_sent_even_though_its_record_is_unknown` |

**`DISPATCHING` and `RECONCILING` still reach `sent-not-confirmed`, deliberately, and CONTRACTS.md now
says so.** Codex's CHECK (b) is right that the word's stated 1:1 record contract
(`UNKNOWN` + `needs_reconciliation`) did not hold for them — but the honest half is the DOCUMENT, not
the mapping: the word is about the WIRE, and a frame that may have landed is exactly what it means. A
mutation cancelled by disposal stays `DISPATCHING` and unflagged, which is `TradingGateway`'s half and
is routed to U2c-1 (PRIOR 2 above); until then the leg carries the connector's own `transport` beside
the word rather than the pipe server editing a row it does not own.

**NOT VERIFIED end-to-end: a definite record state arriving with `NothingWritten` through the real
pipe.** In one leg the two cannot be produced together — a mutation the connector refuses before the
wire is settled UNKNOWN by the gateway, not CANCELLED — so the combination is asserted through
`LegWordFor`, the seam this vocabulary is exported to be tested at, exactly as round 10's cross-product
test is. What is measured end-to-end is the invariant it protects, in the two directions the fixtures
can reach.

### F3 — the table is checked against the dispatcher, because a hand list cannot find its own omission

Codex's four missing operations are `schema`, `connectors`, `material-list` and `material-note`, and
`schema` makes a connector-backed `StatusAsync` call from outside the derivation. The rows:

| handler | serial depth | why |
|---|---|---|
| `schema` | **2W** | it builds the same status the `status` handler does, and describes it |
| `connectors` `material-list` `material-note` | **0** | no connector call at all |

A zero row contributes nothing to the maximum, which is the correct arithmetic — the point of putting
it in the table is that **a handler which is ABSENT is one nobody notices growing a call**, which is
precisely how `schema` came to have one.

**The coverage test asks the DISPATCHER.** Every operation in `Core.Ops`' vocabulary is read off the
constants by reflection and sent over the real pipe; an op with no arm answers
`unknown operation '…'` and anything else — a refusal for missing arguments, an empty sweep, a real
answer — means an arm ran. Both directions are asserted, so a row for an operation that no longer
exists fails too. `hello` is excluded and the reason is stated in the test: the read loop answers it
before the dispatcher is reached, so it has no chain to bound. **The discriminator's own premise is
asserted** — a made-up op must come back unhandled, or "handled" would mean nothing and every row
would pass for free (the round-4b pattern).

`schema` is also added to the measured `[Theory]`, so the one new connector-backed row has a measured
number and not just a declared one. **The other read rows remain declared, not measured** — they are
`2W` against a maximum of `5W`, so no read can move the drain — and that was true before this round
too.

### The round-7 test that failed on Windows and passed alone — read, rated, and fixed as a TEST defect

`An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop` failed round 10's full
Windows suite on the last assertion — *"a bridge that was answering requests throughout was dropped"*
— and passed in the targeted run on the same binaries two and a half minutes earlier.

**The rating: it is the TEST's premise, not a Windows timing hole in the product.** The assertion is
about what the EMERGENCY did to the connection, and it was read AFTER the test's own teardown, which
can end the connection by itself:

```
var ex = await Assert.ThrowsAnyAsync<Exception>(() => connector.CancelAllOrdersAsync(...));   // the verdict
await chatter.CancelAsync();                    // <-- cancels a chatter RPC that may be mid-WRITE
try { await talking; } catch { }
Assert.True(await connector.IsConnectedAsync(), "a bridge that was answering … was dropped");
```

The chatter issued `GetAccountsAsync(chatter.Token)` in a loop, and `AtasConnector.WriteFrame` ends
the connection when a write is cancelled in flight — deliberately, and the source says why: the frame
is half-written into a `StreamWriter` every caller shares, so releasing the gate would hand the next
caller a wedged writer. **Measured on the Mac with a scratch probe at this round's tip** (written,
run, and removed — it is not in the diff):

```
Zz_probe_cancelling_an_rpc_whose_write_is_in_flight_drops_the_connection [FAIL]
  CONNECTION DROPPED by cancelling an in-flight write (peer had taken 24576 bytes)
```

So the assertion was a race between the emergency's verdict and the test's own cleanup: whether the
chatter happens to be inside a write when the token is cancelled. A quiet Mac wins that race; a
Windows box running the whole suite at once does not — which is exactly the shape of the observation
(fails under the full suite, passes alone).

**Two edits, both in the test.** The verdict is read at the moment it is about, BEFORE the teardown;
and the chatter now stops on a flag, with the token only cancelling the sleep between requests, so
the teardown cannot drop the connection at all. Nothing in the product changed — the drop-on-cancel
behaviour is correct and stays.

**NOT VERIFIED: that this is what the box hit in round 10.** That run's evidence is a pair of counts
in the round-10 record and the failing run itself is gone. What is verified is that the mechanism
exists (the probe above), that it sits in the exact window between the verdict and the assertion, and
that the assertion was measuring the wrong moment either way. **And no mutant can bite this fix on the
Mac** — the defect it removes is a race the Mac wins, so restoring either half leaves the test green
here. The empirical half is the box, below: three solo runs and two full-suite runs.

### The Windows box — the granted run, at this round's tip

Manager grant, ONE run, at the end of the round, at tip `120c739`. Round 10 closed with two commits
that had never been on Windows; this run covers them and everything above them.

**The tree really was mine, before AND after.** `tools/win-push.sh` (804 K packed, 157 files
unpacked), then SHA-256 of all eight changed files plus the `.cs` count under `src` + `tests`. The
identity check is INSIDE the run and it is a GATE: the script compares each hash against the value
computed on the Mac and `exit 9`s before building if any differs.

```
IDENTITY BEFORE (identical AFTER, and identical to the Mac worktree):
763BE8FD9AE88FDEF297BB355C705AFFAC77C2669EB56FA6D8E05D294F34F080  docs\CONTRACTS.md
4FEF6B4578822747E3AD81515F970474C30166B0719D3282AE344000805DDD4F  src\TradeAgent.Connectors.Atas\AtasConnector.cs
1EED82F9012501800BFD72FE69C11D6379543D3CB44DCCE52C8039B22B93AF78  src\TradeAgent.Connectors.Fake\FakeConnector.cs
EA022BD5C4605DFEF7E68679E4D8D72A4B5461C0A4256ED17ECAA9648A3ADBBD  src\TradeAgent.ConnectorSdk\TransportLedger.cs
9AD1666001D7A02D5E262366705D2B14B9DACCB281EE1BD1EF9154E5160DB4F9  src\TradeAgent.Gateway\GatewayPipeServer.cs
1362E67D179210388469B60F78A20AD4AA16CFED1AEF25EAB563CC9069AC782F  tests\...\ConnectorSendDeadlineTests.cs
70408AC37826420A0DB11F6B7B8845337E8077F4D66BA1EA6D962CD331294368  tests\...\GatewayPipeBackpressureTests.cs
C134B672F5752EE707EAEB3ADF1BC5CC5983360F41B6ABCEEC12E2D86DB35636  tests\...\SweepRequestIdTests.cs
cs files under src+tests (excluding bin/obj): 90                  (Mac: 90)
IDENTITY OK: the box tree is the Mac tree
```

One ssh session (`tools/win-ps.sh`, one script), `DESKTOP-K8VRIT9`, console session Active, ATAS
running and untouched, no UI agent (console work only, which is all this needed):

```
=== BUILD (--no-incremental) ===   Build succeeded.  0 Warning(s)  0 Error(s)  Time Elapsed 00:00:12.70   BUILD EXIT=0

=== PIPE CLASSES (ConnectorSendDeadline + GatewayPipeBackpressure + SweepRequestId) ===
Passed!  - Failed: 0, Passed: 116, Skipped: 0, Total: 116, Duration: 6 m 17 s   PIPE EXIT=0

=== FULL SUITE RUN 1 ===
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108 - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 308, Skipped: 0, Total: 308, Duration: 6 m 39 s - TradeAgent.IntegrationTests.dll
SUITE 1 EXIT=0

=== FULL SUITE RUN 2 ===
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108 - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 308, Skipped: 0, Total: 308, Duration: 6 m 38 s - TradeAgent.IntegrationTests.dll
SUITE 2 EXIT=0

=== SOLO An_emergency_a_busy_bridge_has_not_answered_yet ===
RUN 1  Passed!  - Failed: 0, Passed: 1, Total: 1, Duration: 2 s   SOLO 1 EXIT=0
RUN 2  Passed!  - Failed: 0, Passed: 1, Total: 1, Duration: 2 s   SOLO 2 EXIT=0
RUN 3  Passed!  - Failed: 0, Passed: 1, Total: 1, Duration: 2 s   SOLO 3 EXIT=0
```

**Windows: 491 green (75 / 108 / 308), 0 failed, 0 skipped — TWICE, and the same 491 as the Mac.**
The eight hashes and the `.cs` count are unchanged AFTER the run, so nothing the run did altered the
tree it measured.

**THE FLAKE VERDICT, with the box's own evidence.** The round-7 test that failed round 10's full
Windows suite and passed alone now passes **five times on the box at this tip**: in both full suites
(the condition it failed under) and in three solo runs. The rating stands as recorded above — a TEST
premise, read at the wrong moment, and the fix is in the test. **What is NOT proven: that the fix is
WHY it passed.** Five green runs cannot distinguish a removed race from an unlucky one that did not
recur, and the honest bar is that the mechanism was measured (the Mac probe) and the assertion was
demonstrably reading a value the teardown could change. It is worth watching for one more round.

### Round 11 close — gates, counts and the test-name diff (2026-09-04)

Tip **`120c739`** (5 commits on `c00fa08`), branch `u2a-rebase-probe`, tree clean.

**Build gate — `dotnet build TradeAgent.sln --no-incremental`, at the tip, on the Mac:**

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.75                                                        (exit 0)
```

**FULL suite, once, on the Mac, at the tip — `dotnet test TradeAgent.sln`:**

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 856 ms   - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 308, Skipped: 0, Total: 308, Duration: 6 m 35 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**491 green (75 / 108 / 308), 0 failed, 0 skipped** — 485 at `c00fa08` plus 6. No sibling test run
overlapped it (`ps` at the end of the run: no other `dotnet test`, only idle MSBuild node-reuse
workers), unlike round 10's.

**Test-name diff `c00fa08` → `120c739` — REMOVED: 0.** Method names extracted at both shas
(`git grep -n -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`, reduced to `path::method`,
sorted unique): **384 → 389**, five new methods, SIX new cases (one is a new `[InlineData]` row on an
existing `[Theory]`):

```
tests/…/ConnectorSendDeadlineTests.cs::A_cancellation_that_never_got_the_send_gate_reports_that_nothing_was_written
tests/…/ConnectorSendDeadlineTests.cs::A_frame_the_peer_read_whole_is_not_reported_as_never_sent_when_its_caller_gives_up
tests/…/GatewayPipeBackpressureTests.cs::Every_operation_the_dispatcher_handles_has_a_row_in_the_drain_table
tests/…/SweepRequestIdTests.cs::An_attempted_mutation_that_reported_nothing_is_not_confirmed_and_an_unattempted_one_is_not_sent
tests/…/SweepRequestIdTests.cs::Every_arm_of_the_leg_classifier_consults_the_transport_result
  + [InlineData(Ops.Schema)] on Every_handlers_measured_chain_fits_inside_the_drain_derived_for_it
```

302 + 6 = 308, which is what ran on both machines. The diff was taken after every structural edit,
not only at the end. Per class: `ConnectorSendDeadlineTests` 43 → 45, `GatewayPipeBackpressureTests`
28 → 30, `SweepRequestIdTests` 38 → 41 (the three together: 110 → 116, which is the box's pipe-class
figure).

**Scope.** Eight files changed, and none of them is a forbidden one
(`git diff --name-only c00fa08..HEAD | grep -E 'TradingGateway.cs|DashboardView.cs|Stores.cs|GatewayTypes.cs'`
→ no match):

```
docs/CONTRACTS.md                                     +42 −…
src/TradeAgent.ConnectorSdk/TransportLedger.cs        +40 −…
src/TradeAgent.Connectors.Atas/AtasConnector.cs       +33 −…
src/TradeAgent.Connectors.Fake/FakeConnector.cs        +9
src/TradeAgent.Gateway/GatewayPipeServer.cs           +72 −…
tests/…/ConnectorSendDeadlineTests.cs                +157 −…
tests/…/GatewayPipeBackpressureTests.cs               +75
tests/…/SweepRequestIdTests.cs                       +117
```

Five commits, one per finding. No `Co-Authored-By` trailers
(`git log c00fa08..HEAD --format=%B | grep -ci co-authored` → `0`). Every mutant was applied to a
`cp` copy's original, `touch`ed, run, then restored from the `cp` copy and `touch`ed again — never
`git checkout --`; `git status --short` empty after each.

### Routed onward, with measurements attached

- **PRIOR 2 and PRIOR V-F1 — U2c-1 class C4**, as the brief directs. Lines and mechanisms are at the
  top of this section; round 10's measurements are unchanged and unrepeated.
- **`AtasConnector._pending` leaks an entry when the CALLER cancels an emergency.** The reply-timeout
  branch removes it; the caller-cancellation exit does not, because it is filtered out of that catch
  on purpose. Bounded by the connection's lifetime (`Drop` faults everything pending) and the same
  class as round 9's F2. **NOT measured beyond reading the code**, and not in this brief.
- **The one-warning-per-future-regression policy** (`<TreatWarningsAsErrors>` in
  `Directory.Build.props`) is still proposed and still not taken — program-wide, outside this brief.
- **`sent-not-confirmed` for a `DISPATCHING` or `RECONCILING` record** is honest about the wire and
  dishonest about nothing, but it only stops being a two-field disagreement when U2c-1 settles those
  rows. CONTRACTS.md now states the truth rather than the aspiration.

### What I did NOT do (round 11)

- **Did not record `PossiblyWritten` at the moment the frame is fully written**, which is the
  mechanism the brief's F2 names. The reason is measured and quoted under F2: the merge rule makes
  `PossiblyWritten` beat `ReplyReceived`, so that edit makes every answered mutation report
  `PossiblyWritten` and breaks round 10's `An_answered_frame_reports_a_reply…`. The attempt marker
  delivers the property the brief asks for — a fully sent leg can never read `not-sent` — for every
  exit rather than for that one.
- **Did not make a bare null transport result map to `sent-not-confirmed`.** Three reachable legs
  arrive with a genuinely empty record and are honestly `not-sent`; flagging them is the round-9
  defect from the other side. Null is now producible only by work that never started a mutation,
  which is what makes the mapping safe.
- **Did not produce a definite record state together with `NothingWritten` through the real pipe.**
  Asserted through the `LegWordFor` seam; stated under R9-F4.
- **Did not measure the read handlers' chains.** `schema` is measured because it is new and calls the
  connector; `status`, `accounts`, `account`, `instruments`, `quote`, `positions`, `position`,
  `orders`, `order` and `executions` remain declared at `2W` and unmeasured, as they were before this
  round. No read can move a drain whose maximum is `5W`.
- **Did not prove the flake fix is why the box is green.** Five green runs at this tip; the mechanism
  is measured, the causation is not. Stated in full above.
- **Did not run the Codex leg or any adversarial-verify leg.** This session is the builder, and under
  R1 nothing here is a verdict.
- **Did not re-measure any earlier round's evidence.** Rounds 4b–10 are carried forward as their
  builders left them; what I verified is that the tree they produced builds clean and is green with
  my changes on top, on both machines.
- **Did not modify `TradingGateway.cs`, `DashboardView.cs`, `Stores.cs` or `GatewayTypes.cs`.** I
  read `TradingGateway.cs` — the `schema` row's depth is a count of the chain `StatusAsync` issues —
  and changed nothing; the `git diff --name-only` above proves it.
- **Did not touch the installed app, ATAS, the real home, or any branch other than
  `u2a-rebase-probe`.** Nothing pushed to a git remote, merged, rebased or moved, and no git command
  was run in the main worktree. `u2a-pipe-hardening` is at `c00fa08` and **I did not put it there** —
  its reflog reads `branch: Reset to c00fa08`, written at 11:19 today, before this session's first
  commit, so the manager advanced it after round 10 (round 10's own record, written earlier, says
  `088c059`). ATAS was running on the box throughout and was not started, stopped or driven; no UI
  agent, no screenshot.
- **Did not run `probe atas`, place, modify or cancel any real order.** The only things that reached
  the Windows machine are the source tree, `dotnet build` and `dotnet test`.
- Two ssh calls beyond the single granted run, both read-only and neither producing a figure quoted
  above: `tools/win-state.sh` before pushing, and `tools/win-push.sh`'s own refuse-then-unpack step.

## Round 12 (build record, 2026-09-04)

Bounce on `120c739` from `briefs/U2a-r12-bounce.md`: the round-10+11 verifier's FAIL (0H/2M/4L,
`records/U2a-verify-r11.md`) and Codex r11 (`records/codex-U2a-r11.txt`, 0H/1M refuted/1L).
**Fresh builder** — the round-11 builder's session is gone; nothing in any earlier round's table was
re-measured by me except where this section says it was. `TradingGateway.cs`, `DashboardView.cs`,
`Stores.cs` and `GatewayTypes.cs` were **read but not modified** (the `git diff --name-only` at the
end of this section is the proof). This is the last round before integration.

| finding | RED | GREEN | mutant | commit |
|---|---|---|---|---|
| **F-1** (M) disposal's sentinel sits inside `if (handlers.Length > 0)`, so the agent disconnecting first switches off the only trace that a request was left mid-dispatch | `Assert.Equal() Failure: Strings differ / Expected: "error" / Actual: null` — the agent gone, the row DISPATCHING, disposal silent | 31/31 `GatewayPipeBackpressureTests` | put the guard back → **RED 1 of 31**, same two lines | `47bd4a1` |
| **F-2** (M, contract) `not-sent` is an assurance a connector must opt into, and `ITradingConnector` never said so | `a cancel that reached the broker was reported 'not-sent'` — a connector written to the public interface that really cancels and never calls `TransportLedger` | 42/42 `SweepRequestIdTests` | three, one per half — all bit, table below | `059fcee` + `a8488b9` |
| **L-1** `AtasConnector._pending` leaks on caller cancellation and the late answer is counted nowhere | `Assert.Equal() Failure: Expected: 1 / Actual: 0` on `AwaitingLateAnswer` after a cancelled emergency | 46/46 `ConnectorSendDeadlineTests` | drop the release → **RED 1**, same line; make it judge the connection → **RED 1**: `the connection was judged on a cancellation that came from this side` | `1295c78` |
| **L-2** a table row bounds the connector chain, not the handler, and the only margin is settable to zero | `the drain came out at 500.000s against a 500.000s connector chain plus 1.000s of handler — a caller shortened it below the work it has to cover` | 32/32 `GatewayPipeBackpressureTests` | fold the term back into the settle margin → **RED 3 of 32** (the invariant, and the two arithmetic assertions at `00:04:16` and `00:00:52`) | `6c2709b` |
| **L-3** the coverage test's candidate set comes from `Core.Ops`, not from the dispatch switch | see below — the RED is the round-11 check passing over a defect | 1/1, and 32/32 for the class | add a switch arm labelled with a LITERAL → **RED 1**: `the dispatch switch has an arm labelled "flatten-everything", which is not an Ops constant` | `c8da925` |
| **L-4** the simulator's deadline sentence is not op-aware, so a `not-sent` leg says the outcome is unknown | `Assert.DoesNotContain() Failure: Sub-string found / Found: "it is not known whether it acted"` on a leg reported `not-sent` | 43/43 `SweepRequestIdTests` | make the sentence unconditional again → **RED 1**, same line | `87fae86` |
| **Codex r11 LOW** the busy-bridge test captures its verdict before the liveness judge runs | Codex's own check applied: `PeerAnsweredSince` → `false`, and **all 122 pipe-class tests passed except the new one** | 122/122 over the three pipe classes | that same mutation → **RED 1 of 122**: `a bridge that was answering throughout was dropped when the liveness judge ran` | `ec98da5` |

### Refuted — Codex r11 "PRIOR FINDING 2 — NOT FIXED", and why the round-11 mechanism stands

Codex reads `SendOutcome.Sent` recording no `PossiblyWritten` and `null` still mapping to `not-sent`
as the round-10 F2 defect surviving. **The round-11 verifier measured both halves of the deviation
and both come out in round 11's favour**, and I did not re-measure them — this is a reading of
`records/U2a-verify-r11.md` targets 3(a) and 3(b), which is where the numbers are:

- the three legs that arrive with an empty transport record start **zero** mutating connector calls
  — counted at the connector through the real pipe for all three (`nothing to close`: calls
  `positions`; `resolution expires`: `orders,orders,orders`, two orders still working; `parked for
  approval`: `positions,positions,account,positions,quote`, broker orders 1 → 1);
- inside both shipped connectors, null-after-attempt is unreachable: `TransportLedger.Attempt()` is
  the first statement of `Rpc` for every `Mutates(op)`, and `Mutates` covers all six mutating
  `BridgeOps`;
- applying `null → sent-not-confirmed` blindly turns **five true tests RED** (the verifier's M6, and
  my own mutant C below reproduces it: RED 5 of 42, naming the same three legs).

So the mechanism is right and the gap is the one the verifier named instead: the property was true of
the implementations and untrue of the CONTRACT. **F-2 closes that**, and it closes it without the
edit Codex asks for — see below.

### F-1 — the guard was the same defect one level up from the count

Round 10 fixed what the sentinel COUNTS (unfinished handler tasks → unfinished **or** unsettled
requests) and left the `if (handlers.Length > 0)` around it. `_handlers` holds per-CONNECTION tasks
that remove themselves on completion, and it is read AFTER step 2 has disposed every connection — so
`handlers.Length` is a fact about **whether an agent was still attached**, and the promise "disposal
may leave a request unsettled, it may not do it silently" was conditioned on it. In the most ordinary
shutdown there is — the agent CLI exits, the operator then closes the app — it is zero.

The fix is one word long in effect: the DISPATCHING query and the sentinel are now step 6, with no
`if` in front of them. The wait for the unwind keeps its guard, because waiting for nothing is
genuinely a no-op; the REPORT does not, because the report is about requests.

The acceptance is the verifier's agent-disconnected probe, lifted into the suite as
`A_row_left_dispatching_is_named_even_when_the_agent_disconnected_first`. The row is produced with no
fault injected into the pipe server at all: a connector `TimeoutException` — which safety rule 3
REQUIRES to propagate — escapes `TradingGateway.ModifyAsync`'s catch taxonomy, the handler answers the
agent and finishes, and the row stays DISPATCHING and unflagged. The test asserts its own premise
(`server.LiveHandlerCount == 0`, a new read-only counter) so "no handler was alive" is measured rather
than assumed, and the connected control — `A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error`
— is unchanged and still green.

### F-2 — the guarantee moved to where the obligation lives, in three halves

**(a) The pipe server stopped taking a connector's silence for an assurance.** It already knew
something it was not using: `TradingGateway` writes `DISPATCHING` immediately before every mutating
connector call, and `UNKNOWN` and `RECONCILING` are reachable only through it — so a leg holding one
of those three states is the pipe server's OWN proof that a mutating step of that leg was dispatched.
`Classify`'s unresolved arm is now two arms:

| record | meaning | nothing reported |
|---|---|---|
| no record · `CREATED` · `AWAITING_APPROVAL` | nothing of this leg reached the wire | `not-sent` (`BeforeTheWire`) |
| `DISPATCHING` · `UNKNOWN` · `RECONCILING` | a mutating step WAS dispatched | `sent-not-confirmed` (`Dispatched`) |

`Dispatched` is `TheAnswer(NotConfirmed, transport)` — i.e. the pipe server's own proof has the same
standing as a broker's answer and is overruled by the same single report, `NothingWritten`. So
`NothingWritten` still overrules every arm, and the three legs keep their word.

**Two existing tests changed their expectations, and that is the point rather than a casualty.**
`Every_arm_of_the_leg_classifier_consults_the_transport_result`'s table gained the third branch
(RED first: `DISPATCHING + nothing attempted: expected 'not-sent', got 'sent-not-confirmed'`, and the
same for UNKNOWN and RECONCILING), and
`An_attempted_mutation_that_reported_nothing_is_not_confirmed_and_an_unattempted_one_is_not_sent` had
picked `UNKNOWN` as its "nothing was ever attempted" record — incidental to what it is about, which
is the LEDGER. It now uses `AWAITING_APPROVAL` and asserts the three dispatched states as the new
fact, so it got longer rather than weaker. **A side effect worth naming: an idempotent replay of an
`UNKNOWN` record used to read `not-sent`** — an assurance about a row that is flagged for
reconciliation — and now reads `sent-not-confirmed`.

**(b) The obligation is stated where a connector author will find it.** A doc block on
`ITradingConnector` itself, a pointer beside the five mutating methods, and a paragraph in
`docs/CONTRACTS.md`'s connector section. It says what to call and when, that reads must not record,
and — the part that matters for a third party — that ignoring it is **safe and imprecise, never
dangerous**: the gateway will not produce the assurance from silence, so the cost of not opting in is
a reconciliation the connector might have avoided.

**(c) `transport` is emitted as explicit `null`.** `Leg.Describe()` returned an anonymous object and
`Json.Options` has `DefaultIgnoreCondition = WhenWritingNull`, so the EVIDENCE field was dropped from
the answer in exactly the case where the word rests on the pipe server's knowledge rather than the
connector's report. It is now a named `LegAnswer` record with
`[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` on that one property. `state` keeps the old
behaviour deliberately: its absence is a fact with its own meaning (no record was ever written) and
the suite reads it that way.

**The gateway-side marking is the better fix and it is NOT mine — routed to U2c-1.** Marking the
attempt where `TradingGateway` dispatches a mutation (immediately before `Connector.CancelOrderAsync` /
`ModifyOrderAsync` / `PlaceOrderAsync`) would make the fail-closed default hold for any connector
*without* the pipe server inferring anything from a record state, and it would give the precise answer
rather than the conservative one. That is `TradingGateway.cs`, which this unit may not open. **U2c-1:
the two are complementary, not alternatives — this round's arm can be left in place under it, and the
`Dispatched` doc comment says so.**

The mutants, one per half:

| mutant | file | bit? | evidence |
|---|---|---|---|
| **A** — the dispatched arm reads a null transport as an assurance again | `GatewayPipeServer.cs` | **RED 3 of 42** | `DISPATCHING/UNKNOWN/RECONCILING + nothing attempted: expected 'sent-not-confirmed', got 'not-sent'`; `a cancel that reached the broker was reported 'not-sent'` |
| **B** — the evidence field is omitted when null again | `GatewayPipeServer.cs` | **RED 1 of 42** | `the leg carries no 'transport' key at all, so its claim arrived without its evidence` |
| **C** — the OTHER direction: the pre-dispatch arm made fail-closed too (the brief's original parenthesis) | `GatewayPipeServer.cs` | **RED 5 of 42** | `A_leg_that_failed_before_the_wire…`, `A_close_leg_parked_for_approval…`, `A_five_order_sweep_carries_a_mix…` + the two contract tests — the verifier's M6, reproduced |
| **D** — the obligation taken back off `ITradingConnector` AND `CONTRACTS.md` | `Contracts.cs`, `CONTRACTS.md` | **RED 1** | `Assert.Contains() Failure: Not found: "TransportLedger"` |

### L-1 — the exit that was filtered out of the cleanup as well as out of the verdict

The reply wait's catch is filtered `when (!ct.IsCancellationRequested)` on purpose, so a caller's own
cancellation is not read as a reply timeout. The filter also skipped the `_pending.TryRemove` every
other exit performs, and because the id never reached `_abandoned`, an answer arriving for it was
delivered to a `TaskCompletionSource` nobody awaited and counted in NEITHER `LateAnswers` NOR the
late-answer event — the two counters round 9's F2 exists to keep honest.

The exit now goes through the same bounded machinery as every other abandoned request
(`AwaitALateAnswer`), with **one difference, stated in the source rather than inherited**: it passes
NO verdict on the connection. A reply timeout is evidence about the bridge; the app closing or an
operator pressing stop is evidence about us, and tearing down a working bridge on it would be the
round-6 mistake in a new place. `PendingRequests` is exposed for the same reason `AwaitingLateAnswer`
already was: a number that only grows is a leak nothing outside the class can see.

Both directions are in the test: answered late (`LateAnswers` 0 → 1, `PendingRequests` and
`AwaitingLateAnswer` back to 0, connection kept) and never answered (both counters back to 0 at the
grace, connection kept). Two mutants, both bit.

### L-2 — a row is the connector chain, and a handler is more than its calls

Every row in `HandlerPaths` is arithmetic over `W` and `E`, which are the CONNECTOR's deadlines. A
handler also reads and parses a frame, writes its request record, settles it and writes a reply, and
no connector deadline describes any of that. The only thing covering it was
`SettleAfterCancelTimeout` — a different quantity (the post-cancellation write-back window), added
once, and `init`-settable to zero, at which point the drain equalled the longest row exactly.

`HandlerOverhead` is now its own term: `drain = max(table) + H + S`, with `H = 1 s`. **A constant on
purpose, and this is the one place in this unit where that is not the defect**: the work is a pipe
read, a JSON parse and two or three local SQLite writes, so deriving it from a connector deadline
would be the fiction. The measurement behind the judgement: at `W = 300 ms`, `E = 900 ms` the
verifier measured `cancel-all` at 917 ms against its 900 ms row, and my own run of the same shape
measured **909 ms against 900 ms**. One second is three orders of magnitude over that.

**The shipped numbers move: the drain is `5×50 + 1 + 5 = 256 s` and disposal's ceiling `5 + 256 + 5 =
266 s`, up from 255/265.** `CONTRACTS.md` and every doc comment quoting them are updated. **The
manager's 265 s ruling now buys 266 s — flagged rather than assumed.**

Three existing tests moved with the arithmetic and each says why in its own comment: the two that
assert the shipped figures (`00:04:15 → 00:04:16`, `00:00:51 → 00:00:52`), and
`Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows`, whose own PREMISE assertion caught
the change — its fixture needs the drain to expire while the handler is still inside its connector
call, and it compared against a literal "under a second" rather than against the 5 s call it is
really about. It now compares against the fault, so it cannot drift again.

The measured test asserts `row + HandlerOverhead >= elapsed` rather than `drain >= elapsed`, because
the drain is the MAXIMUM over the table and `close-all`'s longer row happens to cover `cancel-all`
today (measured: 909 ms against a drain of 2100 ms). A table whose longest row is the tight one has
no such luck, which is why the bound is asserted per row.

### L-3 — the check and the omission came from the same place, one level up

Round 11's coverage test asks the DISPATCHER about every op — but the set of ops it asks about comes
from `typeof(Ops)`'s literals. Every arm uses an `Ops` constant today, so the test is sound at
`120c739`; a handler added with a literal op string would be invisible to it in exactly the way
`schema` was invisible to the hand list before it.

The candidate set is now also read off the SWITCH'S OWN SOURCE (`DispatchSwitchOps()`, via
`Build.RepoRoot`, which the suite already uses): every arm label must BE an `Ops` constant, and the
switch-derived set and the set the dispatcher answers must be equal, both ways.

**The RED is what the round-11 check does with the defect.** With an arm `"flatten-everything" =>
await CloseAll(...)` added to the dispatcher and the round-11 check in place, the coverage test
**passed** — `Passed! - Failed: 0, Passed: 1`. With the round-12 check it fails at
`DispatchSwitchOps()` before the pipe is even driven:
`the dispatch switch has an arm labelled "flatten-everything", which is not an Ops constant`. The
existing missing-row and stale-row assertions never fire on it, because their candidate set cannot
contain it — which is the finding, demonstrated rather than argued.

### L-4 — the sentence and the word are about the same leg

`FakeConnector.Wire` threw one message for reads and mutations alike, so a leg the gateway correctly
reports `not-sent` carried, in the same object, *"it is not known whether it acted"*. `Wire` now takes
the op and `DeadlineSentence` splits it the way the shipped `AtasConnector.EmergencySentence` has
since round 7: a mutation that was under way may have acted and says where to look; a read that timed
out means the operation was never started, and says so. Both deadline exits use it, not just the one
the verifier measured.

### Codex r11 LOW — the verdict was relied on and never observed

`ConnectorSendDeadlineTests.cs:848` reads `connectedAtTheVerdict` at the CALLER's two seconds and the
test disposes the connector before the grace expires, so the liveness judge — which is what actually
decides keep-or-drop, on `PeerAnsweredSince` — never ran inside any test. Codex's own check, applied:
**`PeerAnsweredSince` forced to `false`, and 121 of the 122 pipe-class tests still passed.**

`A_bridge_that_keeps_answering_survives_the_liveness_verdict_not_just_the_caller` observes it, and it
does so in seconds rather than tens of them: the grace is what is left of the ordinary RPC deadline,
so a three-second connector puts the verdict about a second after the caller's two. Chatter keeps
answers arriving across the window and the count is asserted **before and after** the caller's
deadline, so the judge demonstrably had something to keep the connection for.

**DEVIATION, stated: I did not lift `R7P5` from `u2a-verify-r9-probes` verbatim, which is what the
brief says.** Two reasons, both checkable. Its assertion is refutation-shaped — `Assert.True(survived
> 0 || callerMax > 2600)` PASSES when the wedged bridge is NOT dropped, which is the opposite of the
product rule, so it cannot be a suite test as written. And its twelve phases each wait out a ten-second
grace: about 170 s of wall clock, against a suite that is 6 m 48 s in total. **The 12-phase sweep the
brief asks for is already in the suite** — `A_bridge_that_only_heartbeats_is_dropped_whatever_the_heartbeat_phase`,
twelve `[InlineData]` phases across the shipped 5 s heartbeat interval, which is R7P5's fixture with
the product's assertion. That covers `PeerAnsweredSince` returning FALSE; what was missing, and is now
added, is the direction where it returns TRUE.

### Round 12 close — gates, counts and the test-name diff (2026-09-04)

Tip **`ec98da5`** (8 commits on `120c739`), branch `u2a-rebase-probe`, tree clean.

**Build gate — `dotnet build TradeAgent.sln --no-incremental`, at the tip, on the Mac:**

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:01.53                                                        (exit 0)
```

**FULL suite, once, on the Mac, at the tip — `dotnet test TradeAgent.sln`:**

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 1 s      - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 314, Skipped: 0, Total: 314, Duration: 6 m 48 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**497 green (75 / 108 / 314), 0 failed, 0 skipped** — 491 at `120c739` plus 6.

**Test-name diff `120c739` → `ec98da5` — REMOVED: 0.** Method names extracted at both shas
(`git grep -n -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`, reduced to method names, sorted
unique): **384 → 391**, six new test methods and one non-test (`CancelOrderAsync`, the
`LedgerBlindConnector` fixture's own override, which the regex sees):

```
tests/…/ConnectorSendDeadlineTests.cs::A_caller_that_cancels_an_emergency_releases_its_slot_and_still_counts_a_late_answer
tests/…/ConnectorSendDeadlineTests.cs::A_bridge_that_keeps_answering_survives_the_liveness_verdict_not_just_the_caller
tests/…/GatewayPipeBackpressureTests.cs::A_row_left_dispatching_is_named_even_when_the_agent_disconnected_first
tests/…/GatewayPipeBackpressureTests.cs::The_drain_covers_a_handler_whose_row_is_exactly_its_connector_chain
tests/…/SweepRequestIdTests.cs::A_mutating_step_the_connector_never_marked_is_not_reported_as_never_sent
tests/…/SweepRequestIdTests.cs::The_ledger_obligation_is_stated_on_the_interface_and_in_the_frozen_contract
```

308 + 6 = 314, which is what ran. Per class: `ConnectorSendDeadlineTests` 45 → 47,
`GatewayPipeBackpressureTests` 30 → 32, `SweepRequestIdTests` 41 → 43 (the three together:
116 → 122). The diff was taken after every structural edit, not only at the end.

**Scope.** Eight files changed, and none of them is a forbidden one
(`git diff --name-only 120c739..HEAD | grep -E 'TradingGateway.cs|DashboardView.cs|Stores.cs|GatewayTypes.cs'`
→ no match):

```
docs/CONTRACTS.md                                     +62 −…
src/TradeAgent.ConnectorSdk/Contracts.cs              +30
src/TradeAgent.Connectors.Atas/AtasConnector.cs       +51 −…
src/TradeAgent.Connectors.Fake/FakeConnector.cs       +51 −…
src/TradeAgent.Gateway/GatewayPipeServer.cs          +249 −…
tests/…/ConnectorSendDeadlineTests.cs                +163
tests/…/GatewayPipeBackpressureTests.cs              +329 −…
tests/…/SweepRequestIdTests.cs                       +210 −…
```

Eight commits, one per finding (F-2 has two: the fix and its contract-statement assertion). No
`Co-Authored-By` trailers (`git log 120c739..HEAD --format=%B | grep -ci co-authored` → `0`). Every
mutant was applied to a `cp` copy's original, `touch`ed, rebuilt, run, then restored from the `cp`
copy and `touch`ed again — never `git checkout --`; `git status --short` was empty after each.
