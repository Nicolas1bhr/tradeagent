# U2a — ADVERSARIAL-VERIFY record · round 5 (leg [2], Opus, targeted on the bounce)

**Sha under test:** `0909ada` = `d25dbb4` + 10 commits, one per finding.
**Verify worktree:** `/Users/nicolasbeeckman/Projects/ai-trading-software-for-mihael-worktrees/u2a-verify-r5`,
branch `u2a-verify-r5-probes` (from detached `0909ada`). Nothing pushed. No product fix applied.
**Same verifier as round 4** (record `records/U2a-verify-r4.md`, FAIL 2H/1M/1L).
**Toolchain:** dotnet 10.0.400, macOS 25.5.0 Darwin, Apple silicon, 11 cores.
**The Windows box is not mine this round** — the builder's on-box figures are claims I read, listed under NOT verified.

**VERDICT: FAIL — 1H/2M/1L** (detail at the end)

| target | result |
|---|---|
| 1 · V1/F1 both ways | **closed** — 203-char id and forged `op-…` refused, 0 broker orders; valid/minted/GUID ids still pass |
| 2 · V2 as implemented | **bound holds** (2003-2007 ms, owner sentence, dropped); liveness keys on frames-in only → **F-B (MED)** |
| 3 · F11 through the real gateway | **holds both ways** — 2006/2018 ms with the read named; place/modify excluded at 9754/9762 ms |
| 4 · F2 ceiling + drain, and the inversion attacked | **holds** — 50 s derived, N4 = 102.92 s, N5 RED; **my own red-first is RED at `d25dbb4`, GREEN at `0909ada`** |
| 5 · F4 unit + round-4 numbers | **holds** — chunk 1024 pinned by N9; 2002 ms busy-kept / 2004 ms stalled-dropped |
| 6 · CLI tri-state | every exit path typed; W2/W4 bite, **W3 survives 238/238 → F-C (MED)** |
| 7 · regression | **421 green** (75/108/238), exit 0, 163 s; 7/7 spellings refused; M15/M16 still RED |
| (outside the seven) | **F-A (HIGH)**: the operator's own Close All is 9759 ms vs the agent's 2018 ms, and loses the owner sentence |

## Pre-checks (the manager's rulings, and the split)

```
git diff --name-only d25dbb4..0909ada | grep -E 'TradingGateway.cs|Stores.cs|GatewayTypes.cs'
  → no matches
```

**CONFIRMED: F5 / F6 / F8-gateway untouched.** The 15 changed files are `docs/CONTRACTS.md`,
`RiskReducingScope.cs` (new), `AtasConnector.cs`, `FakeBroker.cs`, `FakeConnector.cs`,
`GatewayPipeServer.cs`, `CliReplayContract.cs`, `PipeClient.cs`, `Program.cs`, `TransportResult.cs`
(new) and five test files. `git log d25dbb4..0909ada --format=%B | grep -ci co-authored` → `0`.
Tree clean, `dotnet build TradeAgent.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`.
I do not re-find F5/F6/F8-gateway and I do not re-litigate the two manager rulings; I check they hold.

---

## Target 1 — V1/F1 closed, both ways

**The attack side.** My round-4 exploits, cherry-picked unchanged onto `u2a-verify-r5-probes`
(`c24aed1`); each PASSES only if the defect survives. Both now FAIL:

```
PROBE1_frame_id_bypasses_the_client_order_id_budget_and_charset   [FAIL]
   reply.Ok = False        frame id length = 200        broker orders = 0
PROBE3_frame_id_can_take_the_reserved_minted_prefix               [FAIL]
   ok=False err=INVALID_REQUEST
   replay ok=False broker order count=0
```

At `d25dbb4` these were `Ok=True` with a **203-character** `ClientOrderId` on the broker, and
`op-deadbeef-cancelall-0` was a live idempotency key. Now: refused, **zero broker orders**, both
instances of the class.

**The legitimate side** (`VerifyR5Probes.R5P1`, written to fail if the guard over-refuses):

```
 explicit request_id      ok=True
 omitted, default GUID id ok=True          ← the CLI's own frame-id shape still passes
 61 chars ok=True   62 chars ok=False err=INVALID_REQUEST
   minted [op-09238b1e0ebd47d5ad7176fe22540a1f-cancelall-0] len=47 coid=[TA-op-…-0] len=50
   minted [op-…-cancelall-1] len=47  coid len=50
   minted [op-…-cancelall-2] len=47  coid len=50
 sweep attempted=3 cancelled=3
   broker coid=[TA-vr5-valid-1] len=14
   broker coid=[TA-6a261e3d24084ea79bf8b1dcf7d2a464] len=35
   broker coid=[TA-aaa…aaa] len=64
```

The 61/62 boundary is intact, the widened F9 nonce leaves the minted id at 47 chars → client order
id 50, inside the 64 budget, and the sweep still cancels what it claims. **Target 1 holds.**

---

## Target 3 — F11 through the real gateway, and the exclusion (taken before target 2 because it sets up F-A)

`VerifyR5Probes.R5P3`. One stalled bridge each, one 128 KiB write holding the connector gate,
shipped deadlines. Agent ops go over the real `PipeClient` → `GatewayPipeServer` → `TradingGateway`
→ `AtasConnector`; the scope cases open `RiskReducingScope` explicitly and call the connector.

```
 what=agent-close-all             elapsed=  2018 ms
    msg=the bridge is not responding; 'positions' is NOT confirmed. …check your positions and orders in ATAS.
 what=agent-cancel-all            elapsed=  2006 ms
    msg=the bridge is not responding; 'orders' is NOT confirmed. …
 what=cancel-one-inside-an-open-scope  elapsed= 2004 ms   msg=…'cancel' is NOT confirmed. …
 what=read-inside-an-open-scope        elapsed= 2005 ms   msg=…'accounts' is NOT confirmed. …
 what=place-inside-an-open-scope       elapsed= 9754 ms   msg=could not reach the ATAS bridge
 what=modify-inside-an-open-scope      elapsed= 9762 ms   msg=could not reach the ATAS bridge
```

The prerequisite reads inherit both the deadline **and the owner sentence** — the failing op is
named as `'positions'` / `'orders'` / `'accounts'`, which is the read that actually ran out of time.
`Place` and `Modify` are excluded at the far end even with a scope open: 9754 / 9762 ms. **F11 holds
in both directions.**

---

## Target 2 — V2 as implemented, and what the liveness rule actually keys on

### The end-to-end bound holds.

Round-4's `PROBE8` measured **10005 ms** at `d25dbb4` with the generic "ATAS did not answer" sentence
and the dead connection left up. At `0909ada` the same fixture (`VerifyR4TimingProbes.PROBE8`,
carried forward at `1f25eb3`, now inverted) and `R5P4` measure the fixed behaviour: **2006 ms**, the
owner sentence, connection dropped. See target 4 for the red-first re-proof at `d25dbb4`.

### What the rule keys on — measured, not read.

`PeerIsAlive()` is written from exactly one place, `AtasConnector.cs:430` in `Dispatch`, on **every
frame read**, deliberately never from the write path (`AtasConnector.cs:740` says so). I confirmed
that behaviourally with three peers differing in one variable each (`VerifyR5Probes.R5P4`):

```
 peer=silent                  elapsed=2006 ms  connected_after=False  frames=0 bytes_read=0
    msg=the bridge is not responding; 'cancel-all' is NOT confirmed. …dropped and will be retried…
 peer=draining-but-mute       elapsed=2003 ms  connected_after=False  frames=0 bytes_read=103
    msg=the bridge is not responding; …
 peer=chatty-but-not-reading  elapsed=2007 ms  connected_after=True   frames=8 bytes_read=0
    msg=the bridge is busy; 'cancel-all' is NOT confirmed. The connection is still up — try again…
```

**Frames in decide; bytes out are ignored.** `draining-but-mute` took 103 bytes off us and was
dropped; `chatty-but-not-reading` took **zero** and was kept and called busy. The first is the
builder's stated trade. The second is finding F-B below.

---

## Target 4 — F2, and the verifier's own red-first (the inversion attacked)

### The rulings hold, asserted from live values.

`tests/TradeAgent.IntegrationTests/GatewayPipeBackpressureTests.cs:411-414`:
`Assert.Equal(TimeSpan.FromSeconds(50), connector.WorstCaseOrderPath)` and
`Assert.True(server.HandlerDrainTimeout > connector.WorstCaseOrderPath, …)`.
`AtasConnector.cs:208` — `WorstCaseOrderPath => WriteTimeout + FrameTimeout + _timeout` = 10 + 30 + 10 = 50 s;
`GatewayPipeServer.cs:135` — `HandlerDrainTimeout = 55 s`. The composition is re-derived by an
assertion from the same inputs the comment describes, which is the §9.9 rule the builder states.

### RED-FIRST, done in the un-inverted order, on V2.

The brief asks for my own red-first on one of V2 / F11 / F2. I wrote
`tests/TradeAgent.IntegrationTests/VerifyR5RedFirst.cs` — the acceptance stated as a test, naming no
property the rename touched and no type this round introduced, so **the same file compiles and runs
at both shas**. Verified byte-identical before each run:

```
shasum …/u2a-verify-r5/…/VerifyR5RedFirst.cs  083da8aec9ce60991399433b32982d8cc10003f8
shasum …/u2a-verify-r4/…/VerifyR5RedFirst.cs  083da8aec9ce60991399433b32982d8cc10003f8
```

**At `d25dbb4` — RED:**
```
  Error Message:
   the emergency took 10006 ms with a FREE gate — the two-second promise bounds only the queue wait,
   not what the caller waits
 elapsed         = 10006 ms
 exception       = ConnectorTransportException: ATAS did not answer 'cancel-all' within 10s
 connected after = True
```

**At `0909ada` — GREEN:**
```
 elapsed         = 2007 ms
 exception       = ConnectorTransportException: the bridge is not responding; 'cancel-all' is NOT
                   confirmed. The connection has been dropped and will be retried — check your
                   positions and orders in ATAS.
 connected after = False
```

So V2's fix is established red-first independently of the builder's inversion. (The r4 worktree was
returned to a clean `git status` afterwards.)

### The F2 measurements reproduce.

`N4` (whole-frame ceiling removed) → `A_write_that_keeps_making_progress_is_still_bounded_in_total`
RED at **102.92 s**, against the builder's reported 102.84 s. `N5` (the re-await after cancel
removed) → `Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` RED. Both halves of F2
have a biting test.

---

## Target 5 — F4's progress unit, and the round-4 numbers

`WriteChunkBytes` is now **1024** (`AtasConnector.cs:774`); `N9` reverting it to 8192 turns
`A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped` RED. The round-4 figures,
re-measured at `0909ada` with my own round-4 probes carried forward:

```
 saturation 1500 x 900 KiB : 2002 ms  "the bridge is busy"  connected_after=True
                             backlog done=646 faulted=0 of 1500
 stalled peer              : 2004 ms  "the bridge is not responding"  connected_after=False
 idle stalled, free gate   : 2003 ms  owner sentence present  connected_after=False   (was 10005 ms)
```

Round 4 measured 2002 ms busy-kept / 2006 ms stalled-dropped. **Both still hold**, and the third line
is V2's fix.

---

## Target 6 — the CLI tri-state

`TransportOutcome` is produced by the transport on every `PipeClient.TrySendAsync` exit path
(`PipeClient.cs:52-92`): no pipe → `NothingWritten`; `!_pipe.IsConnected` before the write →
`NothingWritten` (the only provable case, and it is provable because it is checked BEFORE any byte);
write threw → `PossiblyWritten`; read threw → `PossiblyWritten`; `line is null` → `PossiblyWritten`;
unparseable/truncated → `PossiblyWritten`; parsed → `ReplyReceived`. `DisposeAsync` (`:119-121`)
swallows transport failures, which is the SIGABRT-134 fix. Consumed at `CliReplayContract.cs:50`
and `:63`.

Mutants on the three I judged weakest — see the table. **`W3` survived** and is finding F-C.

---

## Target 7 — regression

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR"
exit=0  wall=163s
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 535 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 238, Skipped: 0, Total: 238, Duration: 2 m 41 s - TradeAgent.IntegrationTests.dll
```

**421 green, 0 red — the builder's Mac count (75/108/238) confirmed independently.** My own probe
files were excluded by filter so the count is comparable.

*Disclosure: this is my second full-suite run. The first (also 421 tests) reported 6 FaultTests
failures and was **invalid — my own error**: my M15/M16 loop restored `TradingGateway.cs` but did not
rebuild, so a `--no-build` run executed a binary still carrying M16. The source was clean throughout
(`git status --short` empty, `git diff 0909ada -- src/` empty); the run above is after an explicit
rebuild. Both runs are on the record.*

My round-4 probes still bite:

```
PROBE4 (seven operator spellings, STOP pressed) : 7/7 refused INVALID_REQUEST, 0 broker orders
M15 (AuthorizeOrThrow deleted from ApproveAsync): Failed 5, Passed 70 of 75
M16 (RiskCheckOrThrow deleted from ApproveAsync): Failed 6, Passed 69 of 75
```

---

## Mutants

Applied to a `cp` copy's original, `touch`ed, built, run, restored from the copy and `touch`ed
again — never `git checkout --`. Pristine sha1s: `AtasConnector.cs 0f41115cfc7d2d9d…`,
`GatewayPipeServer.cs 4489b5e3511de647…`, `PipeClient.cs 1c1a591bc6ec52d9…`,
`TradingGateway.cs` restored from a copy taken before M15. `git status --short` empty after each.

| # | mutant | `file:line` | bit? | evidence |
|---|---|---|---|---|
| N1 | the id guard back on the optional field (F1) | `GatewayPipeServer.cs:424`,`:429` | **RED (9)** | "frame id 'has space' was accepted with no request_id, and it reaches the broker as TA-has space" — 6 charset/length + 3 reserved prefix, matching the builder's claimed 9 |
| N2 | `RiskReducingScope` never opened (F11) | `GatewayPipeServer.cs:451` | **RED** | "cancel-all through the real gateway took 9.77s — the prerequisite orders read is still on the ordinary deadline" |
| N3 | `OpensExposure` drops `Place` | `AtasConnector.cs:159` | **RED** | "an ordinary 'place-in-scope' gave up after 2.00s — it took the emergency path it is not entitled to" |
| N4 | whole-frame ceiling removed (F2) | `AtasConnector.cs:708` | **RED** | "the write ran for **102.92s** — the per-chunk budget was reset forever and nothing bounded the total" (builder: 102.84 s) |
| N5 | disposal no longer re-awaits after cancel (F2) | `GatewayPipeServer.cs:834` | **RED** | `Disposal_waits_for_a_cancelled_handler_to_record_what_it_knows` |
| N6 | emergency reply wait back to the ordinary timeout (V2) | `AtasConnector.cs:894` | **RED (2)** | "the emergency took 10.00s with a FREE gate — the deadline is still only on the queue" |
| N7 | `PeerMovedSince` always true — never drop | `AtasConnector.cs:831` | **RED** | `An_emergency_on_an_idle_stalled_bridge_answers_in_two_seconds_and_drops_it` |
| N8 | `PeerMovedSince` always false — always drop | `AtasConnector.cs:831` | **RED** | `An_emergency_a_live_bridge_does_not_answer_is_unknown_but_not_a_drop` |
| N9 | `WriteChunkBytes` 1024 → 8192 (the F4 regression) | `AtasConnector.cs:774` | **RED** | `A_peer_reading_below_one_chunk_per_window_is_busy_and_not_dropped` |
| W2 | `line is null` → `NothingWritten` | `PipeClient.cs:77` | **RED** | "the service took the order and hung up, and the CLI did not say the reply was lost" |
| W4 | the truncated-reply catch removed | `PipeClient.cs:87` | **RED** | `The_real_cli_reports_a_half_written_reply_as_an_unknown_order` |
| **W3** | **read-failure path → `NothingWritten`** | **`PipeClient.cs:71`** | **SURVIVED** | 9/9 `CliReplayContractTests` green, and **238/238 integration green** — finding F-C |
| M15 | `AuthorizeOrThrow(proposer)` deleted (round-4 regression check) | `TradingGateway.cs:577` | **RED (5)** | U2b's re-check still bites |
| M16 | `RiskCheckOrThrow` deleted (round-4 regression check) | `TradingGateway.cs:598` | **RED (6)** | U2b's re-check still bites |

Both liveness directions are pinned by a different test each (N7 / N8), which is what makes the
busy-vs-dropped split a rule rather than a coincidence.

---

## Findings

### HIGH

**F-A — the F11 fix gives the AI's emergency the fast path and leaves the OPERATOR's own button on
the ordinary deadline. The round-4 principle, inverted.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:451` is the only place `RiskReducingScope.Begin()` is
called (`grep -rn "RiskReducingScope.Begin" src/` → one hit). The operator's emergency controls do
not go through the pipe: `src/TradeAgent.App/DashboardView.cs:541`/`:544` call
`TradingGateway.OperatorCancelAllAsync` (`TradingGateway.cs:726`) and `OperatorCloseAllAsync`
(`TradingGateway.cs:734`) in process, and `OperatorCloseAllAsync` unconditionally does
`await Connector.GetPositionsAsync(accountId, ct)` — **the exact "position read before a close" the
bounce named** — with no scope open.

Measured, same stalled bridge, same 128 KiB write holding the connector gate, shipped deadlines
(`VerifyR5Probes.R5P2` and `R5P3`):

| caller | elapsed | message |
|---|---|---|
| agent `close-all`, over the pipe | **2018 ms** | `the bridge is not responding; 'positions' is NOT confirmed. …check your positions and orders in ATAS.` |
| operator **Close All** button, in process | **9759 ms** | `could not reach the ATAS bridge` |
| agent `cancel-all`, over the pipe | 2006 ms | `…'orders' is NOT confirmed. …` |
| operator Cancel All button, in process | 2011 ms | `…'cancel-all' is NOT confirmed. …` |

`scope active during the call? False` on both operator runs. The Cancel All button is fast only
because `RequireAccountId` (`TradingGateway.cs:169`) short-circuits when an account is selected; with
no account selected it falls to `GetAccountsAsync` and takes the same ordinary deadline.

**Risk.** 4.8× the promised bound on the human's own stop control, on the button the source itself
marks as *"this one does move money"* — and the owner loses the sentence the whole decision exists to
produce: `f518251`'s "it is the sentence that sends a person to their platform to look" is replaced
by `could not reach the ATAS bridge`. Round 4's `17aa280` fixed exactly this shape in the other
direction ("Same act, same urgency, ten times the wait, because of where it entered"); round 5
re-created it with the roles swapped. Before this round both callers were equally slow, so this is an
asymmetry the round introduced.

**Fix expectation.** Open the scope where the intent is known on the in-process path too:
`RiskReducingScope.Begin()` around the two operator handlers. **`DashboardView.cs` is not a forbidden
file** — the split covers only `TradingGateway.cs`, `Stores.cs` and `GatewayTypes.cs` — so this does
not need U2c-1. Test: `R5P2`'s fixture with the assertion inverted, plus an assertion that the
operator and agent paths for the same act land within the same bound. If the manager rules it out of
scope, it belongs in `records/U2a.md` as a named open gap next to the `close`-arrives-as-`Place` one,
because the record currently reads as though F11 closed the class.

### MED

**F-B — the reply-timeout liveness rule keys on frames only, so a bridge that has stopped reading but
still heartbeats is KEPT and told "busy — try again", in half of all emergencies.**
`AtasConnector.cs:831` (`PeerMovedSince`) and `:430` (the only `PeerIsAlive()` writer, in `Dispatch`).

Measured with three peers differing in one variable (`VerifyR5Probes.R5P4`):

```
 silent (no reads, no frames)      2006 ms  dropped  "not responding"   frames=0 bytes_read=0
 draining-but-mute (reads, mute)   2003 ms  dropped  "not responding"   frames=0 bytes_read=103
 chatty-but-not-reading            2007 ms  KEPT     "the bridge is busy … try again"  frames=8 bytes_read=0
```

`chatty-but-not-reading` accepted **zero bytes** and was called busy. That is not a corner case: a
wedged ATAS is exactly this shape, because `BridgeServer.StartHeartbeat` runs on **its own
`Task.Run` (`src/TradeAgent.AtasBridge/BridgeServer.cs:251`), independent of the frame read loop at
`:130`** — the loop a freeze wedges. At the shipped `HeartbeatInterval = 5 s`
(`BridgeServer.cs:50`) against the 2 s window, with a randomized phase (`VerifyR5Probes.R5P5`,
12 runs):

```
 KEPT (told 'busy, try again') = 6 of 12;  DROPPED (correct) = 6 of 12
```

**The emergency's verdict against a wedged bridge is a coin flip on heartbeat phase**, and on the
"kept" half nothing drops, nothing redials, and each retry costs another 2 s forever. The evidence
that settles it is already recorded — `_lastWriteProgressAt` (`AtasConnector.cs:170`) knows zero
bytes were accepted for the whole window — and is not consulted on this branch. The source's own
statement of the rule, *"Total silence in BOTH directions for the whole window … is what 'not
responding' means"*, is not what the code implements: a peer talking while accepting nothing is not
silent in the direction that matters.

**Fix expectation.** On the reply-timeout branch, require liveness in the direction the frame had to
travel: keep the connection only if a frame arrived **and** the peer has taken bytes since
`startedAt` (or, equivalently, drop when `_lastWriteProgressAt <= startedAt` regardless of frames).
Red-first test: `R5P4`'s `chatty-but-not-reading` peer asserting dropped + "not responding"; it must
be RED today. The existing pair N7/N8 must stay RED.

**F-C — the one `TransportOutcome` transition that would cause a duplicate live order has no biting
test.** `src/TradeAgent.TradeCli/PipeClient.cs:70-74`. Mutant **W3** turns the read-failure path from
`PossiblyWritten` into `NothingWritten` and **238/238 integration tests stay green** (its two siblings
W2 and W4 both go RED, so this is a hole rather than a pattern).

The code's own comment three lines above (`PipeClient.cs:68`) is *"From here the frame is out of this
process. Nothing below may report NothingWritten."* Under W3 it does, and the consequence is exact:
`CliReplayContract.RecoveryLine` (`:50`) returns **null** and `UnansweredJson` (`:63`) reports
`reply_lost: false`, so the agent is never told to re-run with the same id — and `AGENTS.md`'s rule
("never retry a lost reply with a new id, because that is not a retry") is not triggered. A frame
that reached the service and whose reply was lost becomes a fresh proposal with a new id: a second
real order. That is `7c93181`'s original defect, reachable by a one-word edit nothing catches.

**Fix expectation.** A test that lets the service take the frame and then kills the connection while
the reply is being read, asserting `transport == "PossiblyWritten"`, `reply_lost == true` and a
non-null `recovery`. W3 must go RED.

### LOW

**F-D — a prerequisite READ that inherits the scope also inherits an owner sentence written for an
order, and tells the owner to go and check something that was never sent.** Measured
(`VerifyR5Probes.R5P3`):

```
 what=read-inside-an-open-scope   elapsed= 2005 ms
    msg=the bridge is not responding; 'accounts' is NOT confirmed. The connection has been dropped
        and will be retried — check your positions and orders in ATAS.
```

The deadline inheritance is right and is F11's point; the wording is not. Nothing about an `accounts`
or `positions` read needs *confirming*, and "check your positions and orders in ATAS" sends the owner
hunting for an order the read never placed. `AtasConnector.cs:869-876` composes the sentence from the
bridge op without asking whether that op mutates anything.
**Fix expectation:** for a non-mutating op reaching the emergency branch, keep the deadline and the
drop, and say what happened — the bridge is not responding and the operation could not be started —
without "NOT confirmed" or the go-look-at-ATAS instruction. Assert the wording per op-kind.

---

## NOT verified — by name

- **Every Windows figure in the builder's record is a claim I read, not one I checked.** The box is
  not mine this round. Specifically unverified by me: the 421-green on-box run, `ConnectorSendDeadline
  20/20`, `GatewayPipeBackpressure 12/12`, `CliReplay+Operator+Sweep 56/56`, the "same durations"
  claim, and the inference drawn from them that the Windows pipe buffer is too small to swallow a
  512 KiB frame (which retires my own round-4 NOT-verified item). I have no independent evidence for
  any of it. The builder also states they did NOT run the Windows-only no-buffer mutant (B4), so the
  8 KiB buffer remains unproven by mutation on either platform.
- **ATAS's real client-order-id limit and whether it accepts the `op-…` shape.** Unchanged, needs the
  app and a live order, stays with the v0.1.2 step.
- **Whether a real ATAS bridge goes silent for 2 s while healthy.** The builder says the same. My F-B
  measurement uses a synthetic peer at the shipped 5 s interval; I did not observe a real bridge.
- **F5 / F6 / F8-gateway.** Confirmed untouched (`git diff --name-only` → no matches on the three
  files) and deliberately not re-found. I did not check that U2c-1's brief actually carries them.
- **`FrameTimeout = 30 s` and the 55 s drain as product decisions.** I verified the arithmetic is
  derived and asserted from live values (50 s < 55 s) and that removing the ceiling costs 102.92 s.
  Whether 30 s is the right number, and whether a 55 s shutdown is acceptable, are the manager's
  rulings and I did not re-litigate them.
- **The `AsyncLocal` scope leaking into a fire-and-forget continuation.** The stated bound is that a
  stray scope can only make a read give up in 2 s. I read the mechanism and measured the intended
  paths; I did not construct a leak.
- **`SettleAfterCancelTimeout = 5 s` as a sufficient unwind window.** N5 proves the re-await exists
  and bites; I did not probe a handler that needs longer than 5 s to write its UNKNOWN.

## What I did NOT do

- I did not fix anything and did not push. Probes are on `u2a-verify-r5-probes` in the verify
  worktree only (`1f25eb3`, `e5fd7d3`, `6c721a6`, plus the three cherry-picked round-4 commits).
  `git diff 0909ada -- src/` is empty — no product file differs from the sha under test.
- **I ran the full solution suite twice**, which is the cap. The first run was invalidated by my own
  stale binary (M16 left in the build output after a source-only restore); both are disclosed above.
- I did not re-run the builder's own mutants for F9, F10, F13 or V3 — I checked those findings' tests
  exist and are green in the 421, and spent my mutation budget on the guards behind targets 1-3 and
  the CLI tri-state, as the brief directs.
- I did not exercise the App or the UI, `tools/probe`, `tools/mac-run.sh`, `tools/win-*.sh`, or any
  ATAS interaction. F-A is measured through `TradingGateway`'s operator methods directly, not by
  clicking the Dashboard.
- I did not read leg [3]'s output for this round (§4.2a trap 4, and the legs must not see each other).
- I did not test `material-list` / `material-note`, onboarding, update, or reconciliation paths —
  outside the seven targets.
- I mutated `TradingGateway.cs` twice (M15/M16) to re-check U2b's guards; it is a forbidden file for
  the BUILDER, not for a read-only verification mutant, and it was restored from a `cp` copy with
  `git status` clean afterwards.

## Verdict

Six of the seven targets survived refutation with numbers, and the two that mattered most are
closed convincingly: V1/F1 refuses both round-4 exploits with zero broker orders while still passing
every legitimate id, and V2's end-to-end bound is proven **red-first in the un-inverted order** by a
byte-identical probe that is RED at `d25dbb4` (10006 ms, wrong sentence, connection up) and GREEN at
`0909ada` (2007 ms, owner sentence, dropped). F11's scope holds in both directions, F2's ceiling and
re-await each have a biting test, 421 green reproduces exactly, and both liveness directions are
pinned by separate mutants.

It does not pass as it stands. F11's fix reached the AI's path and not the operator's, so the human's
own Close All button now waits 4.8× longer than the AI's identical request and loses the
owner-readable sentence — the round-4 principle re-created with the roles swapped, in a file the
split does not forbid. F-B and F-C are each a guard whose failure mode is one the unit has already
paid for once: a wedged bridge kept alive on a coin flip, and the one transport transition that
turns a lost reply into a second real order, untested.

**VERDICT: FAIL — 1H/2M/1L**
