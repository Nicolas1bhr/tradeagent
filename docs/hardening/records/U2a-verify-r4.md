# U2a — ADVERSARIAL-VERIFY record · round 4 (leg [2], Opus, targeted)

**Sha under test:** `d25dbb4` (`u2a-pipe-hardening` 21 commits rebased onto `main` `9fd5eb7` + 3 round-4b test-only commits).
**Verify worktree:** `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r4`, branch
`u2a-verify-r4-probes` (created from detached `d25dbb4`). Nothing pushed. No product fix applied.
**Toolchain:** `PATH="$HOME/.dotnet:$PATH"`, dotnet 10.0.400, macOS 25.5.0 (Darwin), Apple silicon.

**VERDICT: FAIL — 2H/1M/1L** (detail at the end)

| target | result |
|---|---|
| 1 · intent classification, each caller ALONE | **holds** — 2002 / 2002 / 2002 ms for `Cancel`/`CancelAll`/`Close`, 9605 / 9602 / 9602 ms for `Place`/`Modify`/read |
| 2 · progress-aware expiry under saturation | **holds** both ways — busy KEPT at 2002 ms, stalled DROPPED at 2006 ms |
| 3 · 61-char budget + `[A-Za-z0-9-]` at the pipe | **REFUTED — F1 (HIGH)**: correct on `request_id`, bypassed via the frame `id` |
| 4 · rebase undid nothing; U2b intact | **holds** — 7/7 spellings refused, FaultTests 75/75, OperatorContextTests 14/14, M15/M16 bite |
| 5 · suite stability under load | **holds** — 391 green, 0 flakes, 2 m 04 s under concurrent saturation |
| 6 · the round-4b rewrite is a tooth | **holds** — 3 builder mutants reproduced; branch confirmed by instrumentation; no false-GREEN state found |
| (outside the six) | **F2 (HIGH)**: an emergency on an idle stalled bridge takes 10005 ms, wrong sentence, no drop |

---

## Target 3 — the 61-char client-order-id budget and the `[A-Za-z0-9-]` charset at the pipe

### REFUTED. The guard is on the wrong field.

`src/TradeAgent.Gateway/GatewayPipeServer.cs:369` runs `IsConservativeId` on `req.RequestId`, and
`src/TradeAgent.Gateway/GatewayPipeServer.cs:361` runs the reserved-prefix check on `req.RequestId` —
but `src/TradeAgent.Gateway/GatewayPipeServer.cs:372` then computes

```csharp
var rid = req.RequestId ?? req.Id;
```

`IpcRequest.Id` (`src/TradeAgent.Core/Protocol.cs:37`) is an ordinary wire field with a GUID default.
It is never validated. An agent that simply **omits `request_id`** puts an arbitrary string into
`rid`, and `TradingGateway.ClientOrderIdFor(rid)` (`src/TradeAgent.Gateway/TradingGateway.cs:381`)
carries it to the broker as `TA-{rid}`.

Probe (`tests/TradeAgent.IntegrationTests/VerifyR4Probes.cs:PROBE1`, commit `355d948`) — a 200-char
frame id containing `#`, `/`, space and `_`, no `request_id`:

```
dotnet test tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj --no-build \
  --filter "FullyQualifiedName~VerifyR4Probes" -l "console;verbosity=detailed"

Passed  VerifyR4Probes.PROBE1_frame_id_bypasses_the_client_order_id_budget_and_charset [495 ms]
 reply.Ok            = True
 frame id length     = 200
 broker orders       = 1
 broker ClientOrderId= [TA-x#y/z w_qqqq…qqq] len=203
```

**203 characters, with `#`, `/` and a space in it, on a broker order.** The five shapes
`SweepRequestIdTests.A_request_id_outside_the_conservative_charset_is_refused` refuses in the
`request_id` field all pass through the `id` field.

`PROBE3` shows the same root cause defeats the reserved-prefix guard, which
`GatewayPipeServer.cs:436` claims makes a minted sweep id uncollidable *by construction*:

```
Passed  VerifyR4Probes.PROBE3_frame_id_can_take_the_reserved_minted_prefix [29 ms]
 ok=True err=
   broker coid=[TA-op-deadbeef-cancelall-0]
 replay ok=True broker order count=1 (1 => the id is a live idempotency key)
```

An agent can plant `op-{nonce}-cancelall-0` in the idempotency store as a PLACE. That is exactly the
`bdf9a24` collision — a sweep leg replaying an agent's PLACE record and being counted as cancelled —
one field over, with the reserved-prefix defence bypassed rather than the reserved separator.

### The stated bound itself, measured (PROBE2 — positive control both directions)

```
 len= 60  ok=True   coid_len=63  err=
 len= 61  ok=True   coid_len=64  err=
 len= 62  ok=False  coid_len=65  err=INVALID_REQUEST
 len= 63  ok=False  coid_len=66  err=INVALID_REQUEST
 len= 64  ok=False  coid_len=67  err=INVALID_REQUEST
 minted [op-10afaede-cancelall-0] len=23 coid_len=26 charset_ok=True
 minted [op-10afaede-cancelall-1] len=23 coid_len=26 charset_ok=True
 broker orders now: 2
   [TA-aaaa…aaa] len=63
   [TA-aaaa…aaa] len=64
```

61 accepted / 62 refused holds **on the `request_id` path**, and the CLI's own minted
`op-{nonce}-{intent}-{index}` ids pass the charset as the positive control. The budget is correct
where it is applied; it is simply not applied to the value the gateway actually uses.

---

## Target 4 (first half) — the round-1 operator exploit on the rebased tip

### NOT refuted. All seven spellings refused, with STOP pressed, in `LIVE_CONFIRM` with live armed.

`VerifyR4Probes.PROBE4` is written to PASS if the exploit works. All seven cases **failed** — i.e.
the guard held:

```
Failed  PROBE4_forged_operator_session_with_stop_pressed(spelling: "operator")   — guard held
Failed  … ("Operator") / ("OPERATOR") / ("oPeRaToR") / (" operator") / ("operator ") / ("\toperator")
 spelling=[<each>] ok=False code=INVALID_REQUEST
   msg='operator' is a reserved session name and is not available on this channel
 broker orders=0
```

Seven for seven: `INVALID_REQUEST`, zero broker orders. The rebase did not undo `690492e` /
`27b6881`.

---

## Target 1 — intent-based classification, measured ALONE per caller on its own stalled bridge

### NOT refuted. Six callers, six separate stalled bridges, shipped deadlines, nothing shortened.

`tests/TradeAgent.IntegrationTests/VerifyR4TimingProbes.cs:PROBE5` (commit `9885603`). Each case
builds its own `AtasConnector` at shipped values (asserted: `EmergencyGateWait == 2 s`,
`WriteTimeout == 10 s`), its own pipe, its own stalled peer, and ONE 128 KiB write holding the gate.
Nothing else is in flight, so no other caller's drop can free the one being measured. The probe is
written to PASS if the classification is wrong; all six FAILED.

```
dotnet test tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj --no-build \
  --filter "FullyQualifiedName~VerifyR4TimingProbes.PROBE5" -l "console;verbosity=detailed"

 caller=cancel-leg  elapsed=  2002 ms  ex=ConnectorTransportException
   msg=the bridge is not responding; 'cancel' is NOT confirmed. …
 caller=cancel-all  elapsed=  2002 ms  msg=the bridge is not responding; 'cancel-all' is NOT confirmed. …
 caller=close       elapsed=  2002 ms  msg=the bridge is not responding; 'close' is NOT confirmed. …
 caller=place       elapsed=  9605 ms  msg=could not reach the ATAS bridge
 caller=modify      elapsed=  9602 ms  msg=could not reach the ATAS bridge
 caller=read        elapsed=  9602 ms  msg=could not reach the ATAS bridge
Total tests: 6   Failed: 6      (every probe failed == every classification held)
```

**2002 / 2002 / 2002 ms** for `Cancel` (the agent's sweep leg), `CancelAll` (the operator's button)
and `Close` — against round 3's **9707 ms** for the agent leg. `Place`, `Modify` and a read keep the
full ~10 s. `IsRiskReducing` (`src/TradeAgent.Connectors.Atas/AtasConnector.cs:105`) keys on intent,
and it is intent that was measured: no second caller existed to drop the connection out from under
the one on trial.

One observation carried to the findings: the ordinary callers' message is
`"could not reach the ATAS bridge"`, not the `SendOutcome.Busy` sentence
(`AtasConnector.cs:724`) the ordinary path is written to produce. See finding F3.

---

## Target 2 — progress-aware expiry under saturation, both directions

### NOT refuted. Both directions measured at shipped deadlines.

**A busy bridge is KEPT** (`PROBE6`) — a real `BridgeServer` over `LoopbackAtasAdapter` reading
everything, **1500 concurrent 900 KiB RPCs**, one `cancel-all`:

```
 emergency elapsed = 2002 ms
 exception         = ConnectorTransportException: the bridge is busy; 'cancel-all' is NOT confirmed.
                     The connection is still up — try again, and check your positions and orders in ATAS.
 connected after   = True
 backlog done=780 faulted=0 of 1500
```

Round 3's failure was 2.01 s **with the healthy bridge disconnected**. Here the connection survives,
the sentence is "busy", and the backlog was demonstrably still ours — 780 of 1500 done, 0 faulted.

**A stalled bridge is DROPPED** (`PROBE7`) — same shipped deadlines, peer that handshakes and then
reads nothing:

```
 emergency elapsed = 2006 ms
 exception         = ConnectorTransportException: the bridge is not responding; 'cancel-all' is NOT
                     confirmed. The connection has been dropped and will be retried — …
 connected after   = False
```

2002 ms / kept / "busy" versus 2006 ms / dropped / "not responding". The single variable is whether
`_lastWriteProgressAt` (`AtasConnector.cs:113`) moved during the wait.

---

## Target 4 (second half) — U2b's approval re-check, and legitimate operator actions

### NOT refuted, and both directions run.

```
dotnet test tests/TradeAgent.FaultTests/TradeAgent.FaultTests.csproj --no-build
Passed!  - Failed: 0, Passed: 75, Skipped: 0, Total: 75, Duration: 901 ms

dotnet test tests/TradeAgent.IntegrationTests/… --filter "FullyQualifiedName~OperatorContextTests"
Passed!  - Failed: 0, Passed: 14, Skipped: 0, Total: 14, Duration: 133 ms
```

The legitimate direction is a named test that ran green:
`OperatorContextTests.The_in_process_operator_still_places_without_approval`
(`tests/TradeAgent.IntegrationTests/OperatorContextTests.cs:190`), alongside
`An_ordinary_agent_session_still_parks_for_approval` (`:164`).

The re-check is not merely present, it bites — two mutants on
`TradingGateway.ApproveAsync` (`src/TradeAgent.Gateway/TradingGateway.cs:558`, `:585`):

| mutant | change | result |
|---|---|---|
| M15 | `AuthorizeOrThrow(proposer)` deleted from `ApproveAsync` | **RED — 5 failed** (kill switch, live-off, account cleared, connection died, unconfirmed work) |
| M16 | `await RiskCheckOrThrow(intent, account, ct)` deleted from `ApproveAsync` | **RED — 6 failed** (rate limit, tightened limit, position limit, notional × contract size, allowlist, stale quote) |

---

## Target 6 — is the round-4b rewrite a tooth, and is it reaching the product branch?

### 6a. The builder's three mutants reproduced.

`MC` is the fixture mutant: the ORIGINAL 400 × 512 KiB / real-`BridgeServer` fixture restored under
the NEW assertions.

```
  Failed  An_emergency_behind_a_busy_but_healthy_bridge_says_busy_and_does_not_drop_it [823 ms]
  Error Message:
   the emergency came back in 0.48s, short of the 2s gate wait — it was never queued behind
   anything, so this run measured nothing about gate EXPIRY
```

The builder measured 0.83 s for the same mutant; I measure **0.48 s**. Same diagnosis, and the
fixture names ITSELF rather than reporting "no exception was thrown". `M5` (busy branch always
drops) and `M6` (busy branch returns `Sent`) reproduce the builder's mutants A and B — see the
mutants table.

### 6b. The branch the test reaches, read off the product, not off the assertion's name.

I instrumented `AtasConnector.WriteFrame` in the worktree (a `VR4()` sink appending to the file named
by `$VR4_TRACE`, at each return point), rebuilt, ran the two product tests, restored the file
(`shasum ed1617fc47946185dfdf5918d0e9b9d66fc86c08`, `git status` clean) and read the traces:

```
busy test    : RPC op=cancel-all emergency=True gateWait=2s
               BRANCH gate-expiry EMERGENCY-BUSY    progressAt=22162613 waitedFrom=22160808
stalled test : RPC op=cancel-all emergency=True gateWait=2s
               BRANCH gate-expiry EMERGENCY-STALLED progressAt=22163475 waitedFrom=22163731
```

In the busy run the last accepted-bytes timestamp is **+1805 ms after** the wait began; in the
stalled run it is **−256 ms before** it. The matched pair really does turn on
`_lastWriteProgressAt > waitedFrom` (`src/TradeAgent.Connectors.Atas/AtasConnector.cs:605`), and
each test enters the branch its name claims.

Branch histogram over the whole class (13 tests, one instrumented run):

```
1015 BRANCH gate-expiry ORDINARY-BUSY
   4 BRANCH write-timeout PEER-STALLED
   3 BRANCH gate-expiry EMERGENCY-STALLED
   1 BRANCH gate-expiry EMERGENCY-BUSY
```

Two things fall out of that and are carried to the findings: the emergency-busy branch is reached by
exactly one test in the unit, and the **ordinary** `An_ordinary_op_behind_a_stalled_write_still_gets_the_full_deadline`
test never reaches a gate-expiry branch at all — its trace is `RPC op=place emergency=False
gateWait=10s` followed by the gate-HOLDER's `BRANCH write-timeout PEER-STALLED`. The ordinary caller
is freed by the holder's drop, not by its own gate expiry (finding F3).

### 6c. Is there a machine speed or scheduler state that lets the emergency through before the gate wait?

**No state produces a false GREEN. Every such state produces a loud RED.** Three routes checked:

1. *The gate is free when the emergency asks.* Then the emergency returns fast and the premise
   assertion fires by name — measured above at 0.48 s under mutant `MC`. It cannot pass.
2. *The gate is released between 1.9 s and 2.0 s* (inside the premise's tolerance) — the only window
   in which a `Sent` outcome could pass the clock premise. It cannot open: the holder's 512 KiB
   frame is written in 8 KiB chunks and the peer sleeps 200 ms between reads, and the kernel will
   hold only what the socket buffer holds. Measured on this box rather than assumed:
   `sysctl net.local.stream.sendspace = 8192`, `net.local.stream.recvspace = 8192` — ~16 KiB in
   flight, so ≥ 63 pace cycles ≈ 12.6 s, against the builder's measured 12.95 s. A faster machine
   reaches each `Task.Delay(200 ms)` sooner and cannot shorten it. Even then, a `Sent` emergency
   would sit out the 10 s RPC timeout and fail `timer.Elapsed < 6 s`.
3. *A stall inside the emergency's own wait* (GC pause, oversubscribed scheduler). The check is
   "any progress since `waitedFrom`", not "recent progress", and the peer supplies ~10 chunks in the
   2 s window; the measured margin was 1805 ms. A stall long enough to zero that would classify the
   run as `EMERGENCY-STALLED` and fail `Assert.Contains("busy")` — RED, not a pass.

The one machine on which the premise assertion could fire spuriously is one whose local-socket
buffer is ≥ 512 KiB (`sendspace` tuned very high, or a Windows pipe with a large buffer): the whole
frame would be accepted at once, the gate released immediately, and the test would fail as a
**fixture** failure with its own sentence. That is the safe direction, and it is on the NOT-verified
list for Windows.

---

## Target 5 — suite stability at shipped defaults, under load

The full suite ran once **while the 1500 × 900 KiB saturation probe ran in a separate `dotnet test`
process on a loop** (8 iterations). My own probe files were excluded by filter so the count is
comparable with the manager's baseline.

```
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR4"
exit=0  wall=124s          (load average 2.49 before → 4.90 during)

Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 1 s   - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s   - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 208, Skipped: 0, Total: 208, Duration: 2 m 1 s - TradeAgent.IntegrationTests.dll
```

**391 green, 0 red, 2 m 04 s wall. No flake, by name or otherwise** — the failure list is empty.
The concurrent load ran 8 for 8 with the same verdict each time ("the healthy saturated bridge was
kept (connected=True) and told it was busy"), so the saturation classification is stable at ~2 s
while the machine is carrying a full suite.

---

## Mutants

Every mutant was applied to a `cp` copy's original, `touch`ed, built, run, then restored from the
`cp` copy and `touch`ed again — never `git checkout --`. Pristine sha1s:
`AtasConnector.cs ed1617fc47946185dfdf5918d0e9b9d66fc86c08` (identical to the builder's),
`GatewayPipeServer.cs f43da192a6e163cc3efcbb15b231ee6b28e7ddd5`,
`TradingGateway.cs ec6e9fb7fad6535e8cf31c58d1a48f049c5487a9`,
`ConnectorSendDeadlineTests.cs 47b07fac6ad9437b1cf28e6d45080da7a07084f2`.
`git status --short` was empty after every restore.

| # | mutant | `file:line` | bit? | evidence |
|---|---|---|---|---|
| M1 | `IsRiskReducing` drops `BridgeOps.Cancel` (round-4 regression) | `AtasConnector.cs:106` | **RED** | `A_cancellation_fails_fast…(leg)`: "the 'leg' cancellation took 9.76s behind a stalled write" |
| M2 | `IsRiskReducing` adds `BridgeOps.Place` | `AtasConnector.cs:106` | **RED** | `An_ordinary_op…(place)`: "gave up after 2.00s — it took the emergency path it is not entitled to" |
| M3 | gate wait never shortened for an emergency | `AtasConnector.cs:702` | **RED** (4) | "the emergency took 10.00s"; "the emergency cancel-all took 9.75s" |
| M4 | `EmergencyGateWait` 2 s → 5 s | `AtasConnector.cs:81` | **RED** (2) | `Assert.Equal()` on both emergency tests |
| M5 (= builder A) | busy branch always drops | `AtasConnector.cs:605` | **RED** | `Assert.Contains()` — "busy" not found |
| M6 (= builder B) | busy branch returns `Sent` | `AtasConnector.cs:605` | **RED** | "the emergency took 12.00s" |
| M7 | progress no longer recorded after a chunk | `AtasConnector.cs:635` | **RED** | busy test: "busy" not found |
| M8 | chunking removed — one `WriteAsync` per frame | `AtasConnector.cs:616` | **RED** | busy test: "busy" not found |
| M9 | `IsConservativeId` bound → `MaxClientOrderIdChars` (64) | `GatewayPipeServer.cs:467` | **RED** | `The_longest_accepted_request_id_still_fits_the_client_order_id_budget` |
| M10 | charset predicate → `c => true` | `GatewayPipeServer.cs:467` | **RED** (5) | all five `A_request_id_outside_the_conservative_charset_is_refused` cases |
| M11 | reserved `op-` prefix no longer refused | `GatewayPipeServer.cs:359` | **RED** (3) | all three `A_request_id_using_the_reserved_minted_prefix_is_refused` cases |
| M12 | `MaxRequestIdChars` derivation → literal `61` | `GatewayPipeServer.cs:457` | **SURVIVED** | 14/14 green — equivalent today; see F4 |
| M13 | `ClientOrderIdFor` prefix `TA-` → `TA-v2-` | `TradingGateway.cs:381` | **RED** | cap moved to 58: "a 61-character id was refused … up to 58 characters" |
| M12+M13 | literal bound AND a longer prefix | both | **RED** | `Assert.Equal()` `Expected: 64  Actual: 67` |
| M14 | the two ORDINARY `SendOutcome` sentences swapped | `AtasConnector.cs:723`/`:725` | **SURVIVED** | class 13/13 green; whole integration assembly green apart from my own always-failing probes; see F2 |
| M15 | `AuthorizeOrThrow(proposer)` deleted from `ApproveAsync` | `TradingGateway.cs:577` | **RED** (5) | kill switch, live-off, account cleared, connection died, unconfirmed work |
| M16 | `RiskCheckOrThrow` deleted from `ApproveAsync` | `TradingGateway.cs:598` | **RED** (6) | rate limit, tightened limit, position limit, notional × contract size, allowlist, stale quote |
| MC (= builder C) | old 400 × 512 KiB fixture under the new assertions | `ConnectorSendDeadlineTests.cs:418` | **RED** | "came back in 0.48s, short of the 2s gate wait … measured nothing about gate EXPIRY" |

---

## Findings

### HIGH

**F1 — the id restriction is enforced on `request_id`, but the id that reaches the broker is
`req.RequestId ?? req.Id`. Omitting `request_id` bypasses all three guards.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:359` (reserved prefix), `:364`+`:467` (charset and
61-char budget), `:372` (`var rid = req.RequestId ?? req.Id;`), `src/TradeAgent.Core/Protocol.cs:37`
(`IpcRequest.Id`, an unvalidated wire field).

This is a **CLASS with two instances** (§9.10) — one root cause, one structural fix:

- *Instance 1 — the budget and the charset.* Measured: a 200-character frame id containing `#`, `/`,
  a space and `_`, with no `request_id`, was accepted and reached the broker as the 203-character
  `ClientOrderId` `TA-x#y/z w_qqq…`. Every shape
  `SweepRequestIdTests.A_request_id_outside_the_conservative_charset_is_refused` refuses in the
  `request_id` field passes through the `id` field.
- *Instance 2 — the reserved `op-` prefix.* Measured: frame id `op-deadbeef-cancelall-0` was
  accepted, reached the broker as `TA-op-deadbeef-cancelall-0`, and became a live idempotency key (a
  second frame with the same id replayed the PLACE record; broker order count stayed 1). The comment
  at `GatewayPipeServer.cs:436` — the collision is impossible *by construction* because "the agent
  cannot type the shape" — is false. An agent can plant a PLACE under a minted sweep-leg id, which is
  the `bdf9a24` fault (`cancelled=1`, order still WORKING) restored by a different route.

**Risk.** Safety rule 1 requires `ClientOrderId` to round-trip; a 203-character id with `#` and a
space in it is precisely the field the rule says must not be guessed at, and the whole reason
`ea1f47d` and `5c716aa` exist. A broker that truncates or refuses it takes reconciliation with it
(rule 2). `docs/CONTRACTS.md` also states "Every mutating op takes `request_id`" — the code does not
enforce that, and the omitted-`request_id` path is the one that is unguarded.

**Fix expectation.** Validate the value that is USED, not the field that may be absent: compute
`rid` first and apply the `MintedIdPrefix` refusal and `IsConservativeId` to `rid` (or refuse a
mutating op that arrives without `request_id`, which `CONTRACTS.md` already claims). Two new tests
must go RED before the fix: a mutating frame carrying a 62-character / out-of-charset `id` and no
`request_id`, and one carrying `op-…` in `id`, both asserting `INVALID_REQUEST` and
`Assert.Empty(conn.Broker.Orders)`. `docs/CONTRACTS.md` needs re-reading afterwards, since it
describes the restriction as a release-note fact.

**F2 — an emergency on a stalled bridge with a FREE send gate waits 10 s, gets a message with no
owner instruction in it, and the dead connection is left up.**
`src/TradeAgent.Connectors.Atas/AtasConnector.cs:81` (`EmergencyGateWait` bounds only the gate),
`:702`, `:759-763`.

Measured (`VerifyR4TimingProbes.PROBE8`, shipped deadlines, peer handshakes then stops reading,
**nothing else in flight**):

```
 idle-stalled emergency elapsed = 10005 ms
 exception = ConnectorTransportException: ATAS did not answer 'cancel-all' within 10s
 connected after = True
 owner-readable sentence present: NOT confirmed=False
```

`EmergencyGateWait` bounds the queue wait only. With the gate free the emergency becomes the writer,
its ~100-byte frame lands in the 8 KiB socket buffer, `WriteFrame` returns `Sent`, and the caller
then serves the full 10 s RPC reply timeout. The result is **10005 ms**, the generic
"ATAS did not answer" sentence instead of "the bridge is not responding; 'cancel-all' is NOT
confirmed … check your positions and orders in ATAS", and **no drop** — so the reconnect that would
restore service never starts. `GetHealthAsync` only reports DEGRADED after `HeartbeatTimeout` (15 s)
and does not drop either.

This is the shape `f518251` was written for in its most likely real form: ATAS frozen, owner presses
stop, nothing else in flight. It is strictly worse than the contended case the tests do cover — 5×
the wait, the wrong sentence, and no reconnect. **No test in the unit exercises it**: every emergency
test places a 128 KiB `stuck` write first.

**Fix expectation.** Bound the emergency end-to-end, not just at the gate: an emergency's reply wait
should use `EmergencyGateWait` (or a stated emergency RPC bound) rather than the ordinary `_timeout`,
and a risk-reducing op that times out waiting for its reply should produce the same owner-readable
sentence and the same drop-and-reconnect as the gate-expiry path. A red-first test:
`An_emergency_on_an_idle_stalled_bridge_still_answers_in_two_seconds_and_says_why` — the fixture is
`PROBE8` with the assertion inverted. If the manager instead rules this OUT of U2a's scope, the
10 s / no-drop behaviour needs writing into `records/U2a.md` as a known gap in the owner-facing
sentence, next to the `close-all`-is-a-`Place` gap.

### MED

**F3 — the ORDINARY half of `SendOutcome` is pinned only by wall-clock duration; neither its branch
nor its sentence is asserted anywhere.** One class, two instances:

- *Instance 1 — the sentences.* `AtasConnector.cs:723` and `:725`. Mutant **M14 survived**: swapping
  them (a merely busy bridge is told "the ATAS bridge did not read 'cancel' within 10s", a genuinely
  stalled one "the bridge connection is still up") leaves the 13-test class green and the whole
  integration assembly green. `grep` confirms no test in the repo asserts either string. This is the
  identical class to the defect round 4 fixed on the emergency path (`9e50559`: a healthy bridge
  libelled as dead) — one branch over, unguarded.
- *Instance 2 — the branch.* Instrumentation shows
  `An_ordinary_op_behind_a_stalled_write_still_gets_the_full_deadline` never reaches a gate-expiry
  branch at all: its trace is `RPC op=place emergency=False gateWait=10s` and then the gate
  HOLDER's `BRANCH write-timeout PEER-STALLED`. The ordinary caller is freed by the holder's drop at
  10 s and receives the generic `"could not reach the ATAS bridge"` wrapper (`AtasConnector.cs:730`),
  so `Assert.DoesNotContain("NOT confirmed", ex.Message)` examines a message the classification never
  produced. The test still bites M2 (`place` at 2.00 s), so the exclusion it exists for is held — but
  it holds it by duration alone. Meanwhile the `ORDINARY-BUSY` branch is entered **1015 times** per
  class run (by `Local_queueing_under_load_does_not_disconnect_a_healthy_bridge`) with nothing
  reading what it says.

**Fix expectation.** Assert the two ordinary sentences where the branches are already reached — the
Busy one in `Local_queueing_under_load_does_not_disconnect_a_healthy_bridge` (1015 hits), the
PeerStalled one in `Rpcs_to_a_bridge_that_stopped_reading_end_rather_than_hang`; and give
`An_ordinary_op_behind_a_stalled_write_still_gets_the_full_deadline` a gate holder that outlives the
ordinary caller's own deadline (a `BridgePeer.ReadingSlowly` holder, as the busy test now uses), so
the ordinary caller expires on ITS OWN gate wait and the sentence under test is the one it reads.
M14 must go RED after the fix.

### LOW

**F4 — the derivation at `GatewayPipeServer.cs:457` is not itself pinned; the property it defends
is.** Mutant M12 (replace `MaxClientOrderIdChars - TradingGateway.ClientOrderIdFor("").Length` with
the literal `61`) survived 14/14. It is an equivalent mutant today. I checked whether the source's
claim — "reading the prefix off the real function means a change THERE moves this instead of
silently breaking it" — is load-bearing, and it is: M13 alone (prefix `TA-` → `TA-v2-`) moved the cap
to 58 and went RED, and M12+M13 together still went RED on
`Assert.Equal(64, ClientOrderIdFor(longest).Length)` — `Expected: 64  Actual: 67`. So the budget
cannot silently drift either way. Recorded because a surviving mutant is a fact, not because it is a
defect. No fix required; if one is wanted, an assertion that
`MaxRequestIdChars + ClientOrderIdFor("").Length == 64` would pin the derivation itself.

---

## NOT verified

- **Everything Windows.** The box is offline. Named-pipe buffer semantics; the handle-dispose kill
  of an accepted overlapped write (`DropStalledPeer`, `AtasConnector.cs:657`); the no-buffer stall
  (the builder's mutant B4). None of these can bite on macOS, where a named pipe is a Unix socket.
  Measured here instead, and stated as a macOS fact only: `net.local.stream.sendspace = 8192`,
  `net.local.stream.recvspace = 8192`.
- **Whether the round-4b fixture's premise holds on Windows.** `BridgePeer.ReadingSlowly`'s guarantee
  ("no machine can make a 512 KiB frame finish inside ~12 s") rests on the ~16 KiB of Unix-socket
  buffer measured above. A Windows named pipe with a large buffer could accept the whole frame at
  once, release the gate immediately, and make the test fail as a FIXTURE failure. That is the safe
  direction — it cannot produce a false GREEN — but it is unmeasured.
- **ATAS's real client-order-id limit and charset.** 64 is a conservative guess, labelled as one in
  `GatewayPipeServer.cs:441` and `docs/CONTRACTS.md`. Whether ATAS accepts the `op-…` shape is the
  same open question. F1 makes this worse than the record states, since ids far outside the guess
  reach the connector today — but the guess itself is still unsettled and settleable only on the box.
- **Whether the broker actually round-trips the 203-character id from F1.** I measured it reaching
  `FakeBroker`, not a real ATAS. What is proven is that nothing in TradeAgent stops it leaving.
- **The rebase as a whole.** I re-ran the round-1 exploit, U2b's approval re-check and the full suite;
  I did not diff `9fd5eb7..d25dbb4` file by file for other semantic drift.
- **`GetHealthAsync` degradation timing under F2.** I read `HeartbeatTimeout = 15 s` and the
  `DEGRADED`-not-drop behaviour from the source; I did not run a 15-second heartbeat-staleness probe.

## What I did NOT do

- I did not fix anything, and I did not push. My probe commits (`355d948`, `9885603`, `77abc37`) sit
  on `u2a-verify-r4-probes` in the verify worktree only; `u2a-pipe-hardening` and `d25dbb4` are
  untouched. Every mutant was restored from a `cp` copy and `git status --short` was empty after each.
- I ran the full solution suite **once** (under load, for target 5), not twice. The second run is
  unspent.
- I did not run the App / UI, `tools/probe`, or `tools/mac-run.sh`.
- I did not review the U2b diff itself beyond re-running its tests and mutating two of its gates; leg
  [3] (Codex) covers the same sha independently and I have not seen its output.
- I did not exercise `close-all` over the pipe as an agent (the known `Place`-classification gap,
  deliberately deferred to U2c-2) — F2 is about a different shape and does not overlap it.
- I did not attempt any Windows-only mutant, and I did not simulate a large socket buffer by tuning
  `sysctl` (that changes machine state outside the worktree).
- I did not test `material-list` / `material-note`, onboarding, or update paths — outside the six
  targets.
- **I did not read leg [3]'s output.** The scratchpad this session uses is shared with other legs of
  the program and already contained `codex-U2a-r4.raw.log`, `codex-U14-r4.raw.log` and mutation logs
  from another unit (test counts 79-81, not this unit's 13/14), timestamped before my own pristine
  snapshot. I deliberately did not open the Codex logs — ORCHESTRATION-STANDARD §4.2a trap 4 forbids
  reading a raw codex transcript into an orchestrating context, and the brief says the two legs must
  not see each other. Nothing in that directory influenced any measurement above; my own artifacts
  are the `mut-M1…M16`, `mut-MC`, `trace-*`, `load.log` and `fullsuite-underload.txt` files written
  from 21:53 onward, and my worktree was clean (`git status`: "nothing to commit, working tree
  clean") before the first of them.
- I did not verify the restores by re-reading each file; I verified them by sha1 against the `cp`
  copies, by an empty `git status --short` after each, and by the 391-green full suite run at the
  restored tree AFTER every `src/` mutant.

## Verdict

Targets 1, 2, 4, 5 and 6 all survived refutation, with numbers. Target 3's stated bound is correct
where it is applied and is bypassed entirely by omitting one optional field — an unbounded,
out-of-charset string reaches the broker as `ClientOrderId`, and the reserved-prefix collision
guarantee is false. F2 is a second measured miss of the round-4 emergency promise in its most likely
real shape. Both are on the money path, so the unit does not pass as it stands.

**VERDICT: FAIL — 2H/1M/1L**
