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

### Gates — the Windows box (F12, and it closes the oldest NOT-VERIFIED line in this record)

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
