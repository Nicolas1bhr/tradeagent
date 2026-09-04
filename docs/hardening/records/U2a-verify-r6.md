# U2a — ADVERSARIAL-VERIFY record · round 6 (leg [2], Opus, targeted)

**Sha under test:** `ffa1a3d` = `0909ada` + 4 commits. **Same verifier** as rounds 4 and 5
(`records/U2a-verify-r4.md` FAIL 2H/1M/1L, `records/U2a-verify-r5.md` FAIL 1H/2M/1L).
**Worktree:** `…/u2a-verify-r6`, branch `u2a-verify-r6-probes` from detached `ffa1a3d`. Nothing pushed.
**Toolchain:** dotnet 10.0.400, macOS Darwin 25.5.0, 11 cores. **The box is not mine.**

**VERDICT: FAIL — 0H/1M/1L** (detail at the end) — the first round with no HIGH.

| target | result |
|---|---|
| 1 · liveness-as-answer, four directions | wedged **0 of 12 kept** (was 6/12); answering bridge kept "busy"; reads-but-mute dropped; **new: a bridge answering at 2.5 s on a quiet connection is dropped → F-E (MED)** |
| 2 · F-C | **W3 RED**; the recovery line names `--request-id`; W2/W4 still RED |
| 3 · F-D | both wording mutants RED (D1 → 2, D2 → 7); no read sentence sends anyone hunting an order |
| 4 · the two restored tests | present and biting (N2 9.77 s, N4 102.66 s); method-name diff shows no third test lost; one restored **not** verbatim → F-F (LOW) |
| 5 · the Windows fixture fix | teardown-only; branch trace shows **every** drop/keep branch still entered on macOS |
| 6 · regression | **436 green** (75/108/253) exit 0; 7/7 spellings refused; M15/M16 still RED |

## Pre-checks

```
git diff --name-only 0909ada..ffa1a3d          → 3 files: AtasConnector.cs,
                                                 CliReplayContractTests.cs, ConnectorSendDeadlineTests.cs
  … | grep -E 'TradingGateway.cs|Stores.cs|GatewayTypes.cs|DashboardView.cs'   → no matches
git log 0909ada..ffa1a3d --format=%B | grep -ci co-authored                    → 0
git status --short                                                             → clean
dotnet build TradeAgent.sln                    → Build succeeded. 0 Warning(s) 0 Error(s)
```

F-A confirmed still untouched and still with U2c-1; I do not re-measure it.

---

## Target 1 — liveness-as-answer, all four directions at shipped values

`VerifyR6Probes` (commit `27db262`). Every probe is written to PASS if the defect it names exists.
`EmergencyDeadline == 2 s` asserted in the fixture.

**(a) The wedged shape — reads nothing, heartbeats on its own task. R6P1 FAILED, i.e. the guard holds.**
Twelve randomised phases against the shipped 5 s beat:

```
   phase  0:  2000 ms  connected=False beats=0 bytes_read=0  verdict=not-responding-dropped
   phase  3:  2002 ms  connected=False beats=1 bytes_read=0  verdict=not-responding-dropped
   phase  4:  2000 ms  connected=False beats=1 bytes_read=0  verdict=not-responding-dropped
   phase 11:  2003 ms  connected=False beats=1 bytes_read=0  verdict=not-responding-dropped
   … (all twelve dropped)
 KEPT = 0 of 12  (round 5 measured 6 of 12 kept)
```

**0 of 12**, including the three phases where a heartbeat did land inside the window. F-B is closed
and the rule is phase-independent — round 5's coin flip is gone.

**(b)/(c) The keep direction — a bridge answering other RPCs while the emergency waits. R6P4 FAILED,
i.e. the guard holds.**

```
 elapsed=2003 ms  answers during the window=13
    connected_after=True
    msg=the bridge is busy; 'cancel-all' is NOT confirmed. The connection is still up — try again —
        check your positions and orders in ATAS.
```

The fixture asserts its own premise (`answeredDuring > 0`) before the verdict, so this is not a
vacuous keep. The saturated-but-answering direction is target 5's re-run below.

**(d) The stated new consequence — reads everything, answers nothing, no heartbeat. R6P3 FAILED,
i.e. the consequence holds.**

```
 elapsed=2001 ms  bytes_read=96  connected_after=False
    msg=the bridge is not responding; 'cancel-all' is NOT confirmed. …dropped and will be retried…
```

**(e) THE EXTENT OF THAT CONSEQUENCE — R6P2 PASSED, both cases. This is finding F-E.**
A bridge that READ our frame and answers it a little after the window, on a quiet connection:

```
 answer_after=2500 ms  elapsed=2010 ms  frames read by peer=1  answers sent=0  connected_after=False
 answer_after=3500 ms  elapsed=2004 ms  frames read by peer=1  answers sent=0  connected_after=False
    msg=the bridge is not responding; 'cancel-all' is NOT confirmed. The connection has been dropped…
```

Both cases dropped at ~2.0 s. `frames read by peer=1` — the peer had our frame in hand and was
working on it. **This is the rating the brief asks for (target 1d): yes, a legitimate ATAS state
looks exactly like this**, and it is documented in this repo's own source rather than hypothesised:

1. `src/TradeAgent.AtasBridge/BridgeServer.cs:130-131` handles frames **strictly sequentially** —
   `while (… await reader.ReadLineAsync(ct)) await HandleFrame(line, ct);`. While the bridge works
   on our frame it cannot read, match or answer any other. So `PeerAnsweredSince(startedAt)` has
   nothing it *can* observe during exactly the window in which the bridge is busy with us.
2. `src/TradeAgent.Connectors.Atas/BridgeProtocol.cs` (the `PlaceViaAsyncOverload` doc) states the
   cause in the product's own words: the four obsolete synchronous ATAS call sites *"cannot be given
   a deadline, so a block inside one wedges the bridge's frame loop."* A synchronous ATAS call
   running longer than two seconds is a state this unit already knows about and has written down.

**So the keep branch is unreachable by construction whenever the bridge is merely slow on the
emergency itself.** It is reachable only when frames queued BEFORE the emergency are still being
answered inside the window — genuine saturation, which is R6P4 above and target 5's re-run. Rated as
finding F-E (MED).

---

## Target 2 — F-C

`W3` now bites, and the test asserts the whole consequence chain rather than the enum:

```
=== MUTANT W3 (read-failure path PossiblyWritten -> NothingWritten) ===
  Failed  CliReplayContractTests.A_reply_whose_read_fails_leaves_the_order_possibly_written [412 ms]
  Error Message:  Assert.Equal() Failure: Values differ
Failed!  - Failed: 1, Passed: 9, Total: 10
```

`CliReplayContractTests.cs:141-155` asserts `TransportOutcome.PossiblyWritten`, then
`RecoveryLine(...)` is non-null and `Contains("--request-id cli-w3-1")` — **the recovery line names
the id** — then `reply_lost == true` and `transport == "PossiblyWritten"` on the `--json` object,
with the failure message *"a frame that reached the service was reported as never sent, so the agent
would propose again with a NEW id"*. Both siblings still bite:

```
W2 (line is null -> NothingWritten)        RED — "the service took the order and hung up, and the CLI did not say the reply was lost"
W4 (truncated-reply catch removed)         RED — The_real_cli_reports_a_half_written_reply_as_an_unknown_order
```

**F-C closed.**

---

## Target 3 — F-D wording, both mutants

`Mutates(op)` (`AtasConnector.cs:162-164`) selects the sentence in `EmergencySentence`
(`AtasConnector.cs:184-190`). The op set is complete against `BridgeOps`: `Place`, `Modify`,
`PlaceViaAsyncOverload`, `Cancel`, `CancelAll`, `Close` — every mutating op, and no read.

```
D1  Mutates(op) -> true   (every op gets ORDER wording)   RED 2
    An_emergency_says_confirm_only_when_something_could_have_been_changed(kind:"read", mutating:False)
    An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge
D2  Mutates(op) -> false  (every op gets READ wording)     RED 7
    A_cancellation_fails_fast…("button"), ("leg"), …("cancel", mutating:True),
    An_emergency_a_busy_bridge_has_not_answered_yet…, An_emergency_on_an_idle_stalled_bridge…,
    An_emergency_behind_a_busy_but_healthy_bridge…, An_emergency_cancel_all_behind_a_stalled_write…
```

Both directions bite (the builder claimed RED 2 each; D2 bites seven). The measured sentences:

```
read : the bridge is not responding; 'orders' could not be read, so the operation was not started.
       Nothing was placed or cancelled. The connection has been dropped and will be retried.
order: the bridge is not responding; 'cancel-all' is NOT confirmed. The connection has been dropped
       and will be retried — check your positions and orders in ATAS.
```

**No owner sentence sends anyone hunting an order a read never placed. F-D closed.**

---

## Target 4 — the two silently-deleted tests

Both are present, and I checked for others rather than taking the two on trust:

```
grep -cE '^    public (async Task|void) [A-Za-z_]+'
  0909ada: 17     ffa1a3d: 19
names at 0909ada and missing now:
  An_emergency_a_live_bridge_does_not_answer_is_unknown_but_not_a_drop     ← the deliberate replacement
```

Only the one the record says was deliberately replaced is gone. **No third test vanished.**

Restoration fidelity, each test body diffed against `0909ada`:

| test | restored |
|---|---|
| `A_write_that_keeps_making_progress_is_still_bounded_in_total` | **byte-identical** to `0909ada` (32 lines) |
| `An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge` | **not verbatim** — see F-F |

The second differs by one assertion replaced with five: `Assert.Contains("NOT confirmed", …)` became
`Contains("not responding")` + `Contains("'orders'")` + `Contains("Nothing was placed or cancelled")`
+ `DoesNotContain("NOT confirmed")` + `DoesNotContain("check your positions")`. That is a consequential
strengthening for F-D, not a weakening — but the record calls both restorations verbatim (F-F, LOW).

Both bite, one designed mutant each:

```
N2  the pipe server never opens RiskReducingScope
    RED — "cancel-all through the real gateway took 9.77s — the prerequisite orders read is still
           on the ordinary deadline"
N4  the whole-frame ceiling removed
    RED — "the write ran for 102.66s — the per-chunk budget was reset forever and nothing bounded
           the total"          (builder 102.84 s; my round-5 run 102.92 s)
```

---

## Target 5 — the Windows fixture fix, and whether it still proves anything on macOS

The change (`ffa1a3d`) is **teardown-only**: four `_ = Task.Run(…)` become `peer.Track(Task.Run(…))`,
plus a `_background` list that is cancelled and awaited with a bound before `_p.DisposeAsync()`. No
peer's in-test behaviour is touched — the pacing, the heartbeat interval and the answering rules are
byte-identical. So it cannot change what a test proves in principle.

Confirmed in practice rather than argued. I instrumented the four branch points in
`AtasConnector` (a `VR6()` sink appending to `$VR6_TRACE`), rebuilt, ran the whole class, restored
the file (`shasum 0828da80b254b274…`, `git status` clean) and read the histogram:

```
dotnet test --filter FullyQualifiedName~ConnectorSendDeadlineTests
Passed!  - Failed: 0, Passed: 34, Skipped: 0, Total: 34, Duration: 1 m 26 s

  13 REPLY-TIMEOUT DROP op=cancel-all
   6 GATE-EXPIRY DROP
   2 GATE-EXPIRY BUSY op(unknown)
   1 REPLY-TIMEOUT KEEP-BUSY op=cancel-all
```

**Every branch is still entered on macOS** — the new reply-timeout drop 13 times (the twelve phases
plus the idle-stalled test), its keep sibling once, and round 4's two gate-expiry branches 6 and 2
times. The de-raced fixture exercises strictly more than before, not less.

---

## Target 6 — 436 green, and the standing probes

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR"
exit=0  wall=193s
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 1 s     - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 253, Skipped: 0, Total: 253, Duration: 3 m 11 s - TradeAgent.IntegrationTests.dll
```

**436 green (75/108/253), 0 red — the builder's Mac count confirmed independently.** My probe files
were excluded by filter so the count is comparable. One full-suite run used; one unspent.

Standing probes from rounds 4 and 5, all still biting:

```
PROBE4  seven operator spellings with STOP pressed   Failed 7 of 7  (every probe fails == the guard holds;
                                                     each: INVALID_REQUEST, 0 broker orders)
M15     AuthorizeOrThrow deleted from ApproveAsync   Failed 5, Passed 70 of 75
M16     RiskCheckOrThrow deleted from ApproveAsync   Failed 6, Passed 69 of 75
after restore + REBUILD                              Passed! 75 of 75
```

*(The rebuild after restoring M15/M16 is explicit this round — round 5's first full-suite run was
invalidated by exactly that omission.)*

---

## Mutants

Applied to a `cp` copy's original, `touch`ed, built, run, restored from the copy and `touch`ed
again — never `git checkout --`. Pristine sha1s: `AtasConnector.cs 0828da80b254b274…`,
`GatewayPipeServer.cs 4489b5e3511de647…`, `PipeClient.cs 1c1a591bc6ec52d9…`,
`TradingGateway.cs ec6e9fb7fad6535e…`. `git status --short` empty after each, and the solution was
rebuilt before any `--no-build` run that followed a restore.

| # | mutant | `file:line` | bit? | evidence |
|---|---|---|---|---|
| B1 | liveness back to ANY frame (record `PeerAnswered()` for every frame in `Dispatch`) | `AtasConnector.cs:583` | **RED (5)** | five of the twelve `A_bridge_that_only_heartbeats_is_dropped_whatever_the_heartbeat_phase` cases (phases 400/800/1200/1600/2000 ms) — the builder reported 4 |
| B2 | `PeerAnsweredSince` → `false` (never keep) | `AtasConnector.cs:871` | **RED** | `An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop` |
| W2 | `line is null` → `NothingWritten` | `PipeClient.cs:77` | **RED** | "the service took the order and hung up, and the CLI did not say the reply was lost" |
| W3 | read-failure path → `NothingWritten` (round 5's survivor) | `PipeClient.cs:71` | **RED** | `A_reply_whose_read_fails_leaves_the_order_possibly_written` — **survived 238/238 at `0909ada`** |
| W4 | truncated-reply catch removed | `PipeClient.cs:87` | **RED** | `The_real_cli_reports_a_half_written_reply_as_an_unknown_order` |
| D1 | `Mutates(op)` → `true` (order wording for reads) | `AtasConnector.cs:163` | **RED (2)** | the read case of `An_emergency_says_confirm_only_…` + the restored gateway test |
| D2 | `Mutates(op)` → `false` (read wording for orders) | `AtasConnector.cs:163` | **RED (7)** | seven emergency tests across the class |
| N2 | `RiskReducingScope` never opened | `GatewayPipeServer.cs:451` | **RED** | restored F11 test, 9.77 s |
| N4 | whole-frame ceiling removed | `AtasConnector.cs:708` | **RED** | restored F2 test, 102.66 s |
| M15 | `AuthorizeOrThrow(proposer)` deleted from `ApproveAsync` | `TradingGateway.cs:577` | **RED (5)** | U2b's re-check still bites |
| M16 | `RiskCheckOrThrow` deleted from `ApproveAsync` | `TradingGateway.cs:598` | **RED (6)** | U2b's re-check still bites |

Eleven mutants, eleven bit. Nothing survived this round — round 5's one survivor (W3) is closed.

---

## Findings

### MED

**F-E — the new liveness rule drops a healthy bridge that is merely slower than two seconds, and on a
quiet connection the "busy, kept" branch cannot be reached at all.**
`src/TradeAgent.Connectors.Atas/AtasConnector.cs:871` (`PeerAnsweredSince`), `:959` (the keep test),
`:970` (the drop), against `src/TradeAgent.AtasBridge/BridgeServer.cs:130-131`.

Measured (`VerifyR6Probes.R6P2`, shipped values, a peer that reads the frame and answers it after the
window, nothing else in flight):

```
 answer_after=2500 ms  elapsed=2010 ms  frames read by peer=1  connected_after=False
 answer_after=3500 ms  elapsed=2004 ms  frames read by peer=1  connected_after=False
    msg=the bridge is not responding; 'cancel-all' is NOT confirmed. The connection has been dropped…
```

**Why it is structural, not a corner case.** `BridgeServer` handles frames strictly sequentially, so
while it works on our emergency frame it cannot answer anything else — the exact signal
`PeerAnsweredSince(startedAt)` requires is the one the bridge is unable to emit precisely because it
is busy with us. The keep branch is therefore reachable only from a backlog of frames sent *before*
the emergency (R6P4: 13 answers inside the window → kept, "busy"). And the cause is documented in
this repo: `BridgeProtocol.cs` records that the obsolete synchronous ATAS call sites *"cannot be
given a deadline, so a block inside one wedges the bridge's frame loop"* — a >2 s synchronous ATAS
call is a state the unit already expects, and it now costs a connection teardown.

**Risk.** The emergency's own outcome is UNKNOWN either way, so the drop does not change what the
owner is told about the cancel. What it adds is a forced disconnect at the worst possible moment on a
healthy platform, and every other RPC in flight on that connection loses its reply — including a
`place` already at the broker, which becomes UNKNOWN and goes to reconciliation. The manager ratified
"a bridge that reads but answers nothing for the whole window is dropped"; the measured rule fires
for "a bridge that answers later than two seconds", which is a materially commoner state, and the
brief asked for this to be rated rather than assumed.

**Fix expectation — and the trade is real, so this is a decision, not a repair.** Two options, and I
do not think one is obviously right:
(i) *Widen the signal in time*: keep when the peer answered anything within a grace window wider than
`EmergencyDeadline` — `Volatile.Read(ref _lastAnswerAt) > startedAt - StallGrace`, `StallGrace` ≥ the
bridge's 5 s `HeartbeatInterval`. The twelve wedged-phase cases stay dropped (that peer never answers
anything, so `_lastAnswerAt` is never set), and a bridge that was serving a second ago is kept. The
cost is stated plainly: a bridge that wedges immediately after answering is kept for up to
`StallGrace`, so detection moves from the first emergency to the second.
(ii) *Keep the rule and record its extent*: amend `records/U2a.md` and the `EmergencyDeadline` doc
comment to say "answers later than the deadline" rather than "answers nothing", and put the
reconnect-on-a-slow-bridge consequence in `docs/CONTRACTS.md` where an operator will meet it.
Either way the red-first test is `R6P2`'s fixture — reads the frame, answers at 2.5 s, quiet
connection — asserting kept + "busy" for (i), and it must be RED today.

### LOW

**F-F — "restored verbatim" is accurate for one of the two recovered tests, not both.**
`records/U2a.md` "Round 6" says *"Both were restored verbatim from `0909ada` in `0bb3712`."*
`A_write_that_keeps_making_progress_is_still_bounded_in_total` is byte-identical (32 lines).
`An_agent_cancel_all_through_the_real_gateway_fails_fast_on_a_stalled_bridge` is not: its single
`Assert.Contains("NOT confirmed", …)` became five assertions in both directions for F-D. The change
strengthens the test and is correct — the record's description of it is what is wrong.
**Fix expectation:** one sentence in the round-6 record saying which was verbatim and that the other
was restored with its assertion updated for F-D. This round exists partly because a silent test
deletion survived a green suite; the compensating control is precision about what was put back, and a
reader diffing the two files would find the record and the tree disagreeing.

## NOT verified — by name

- **Every Windows figure is a claim I read.** The box is not mine. Unverified by me: the 436 on-box
  green, the 44-test class run, the SHA-256 tree-identity check and the `.cs` count of 88 before and
  after, the "identical test for test" claim, and the conclusion drawn from it that the paced-peer
  fixture's premise holds on a real Windows named pipe. I read the identity check's construction and
  it answers the objection round 5 raised (an unchecked tree); I did not repeat it, as the brief
  directs. The Windows-only no-buffer mutant (B4) is still not run by anyone.
- **Whether a real wedged ATAS keeps heartbeating.** The builder says the same. `BridgeServer`'s
  independent `Task.Run` is read from source and reproduced with a synthetic peer on both sides.
- **Whether a real ATAS synchronous call exceeds two seconds in practice.** F-E's mechanism is proven
  from this repo's source and measured against a synthetic peer; I did not observe a live ATAS call.
- **F-A** (the operator's Close All at 9759 ms). Confirmed untouched and with U2c-1; not re-measured.
- **ATAS's client-order-id limit and the `op-…` shape.** Unchanged, needs the app.
- **The box's `CoidWitnessTests` anomaly** the builder mentions. Outside my targets; not looked at.

## What I did NOT do

- I did not fix anything and did not push. Probes are on `u2a-verify-r6-probes` (`4b1804e`…`08d69da`
  cherry-picked from rounds 4-5, plus `27db262`). `git diff ffa1a3d -- src/` is empty.
- I ran the full solution suite **once**; one run is unspent.
- I did not re-run the builder's F-B trial (the `_lastWriteProgressAt` rule that failed both ways). I
  read the quoted output and the arithmetic behind it — a ~100-byte frame into an 8 KiB socket buffer
  — and my own R6P2/R6P3 measurements are consistent with it, but I did not reproduce the trial.
- I did not re-verify F-C's, F-D's or F-B's *red* states at `0909ada`; W3's survival there is my own
  round-5 measurement, and the other two RED states are the builder's, quoted not reproduced.
- I did not exercise the App or UI, `tools/probe`, `tools/win-*.sh`, or ATAS.
- I did not read leg [3]'s output.
- I mutated `TradingGateway.cs` twice (M15/M16) as a read-only regression check and restored it from a
  `cp` copy, rebuilding afterwards.

## Verdict

This is the strongest state the unit has been in. Round 5's HIGH went to U2c-1 by decision and its two
MEDs are closed with teeth: **W3, which survived all 238 tests at `0909ada`, is RED**, and the test
behind it asserts the whole consequence chain — the recovery line naming `--request-id`, `reply_lost`
and the transport state — rather than the enum. F-B is fixed and, unlike round 5's 6-of-12 coin flip,
**phase-independent: 0 of 12 kept**, with both directions pinned by separate mutants. F-D gives reads
their own sentence and both wording mutants bite. The two silently deleted tests are back and each
bites its own mutant, and I confirmed by method-name diff that no third test went with them. The
de-raced Windows fixture still enters every branch on macOS — 13 reply-timeout drops, 1 keep, 6 and 2
gate-expiry — so it proves more than before, not less. 436 green reproduces exactly, and eleven of
eleven mutants bit.

It does not pass, on one point, and it is the point the brief asked me to rate. The new rule's extent
is wider than the sentence that ratified it: because `BridgeServer` answers frames one at a time, a
bridge that is merely slower than two seconds on the emergency **cannot** produce the signal that
would keep it, so on a quiet connection every slow-but-healthy bridge is disconnected — and this
repo's own source names the legitimate cause, a synchronous ATAS call blocking the frame loop. That
is a decision for the manager rather than a broken guard, but it is a measured consequence nobody has
written down, and the record's "restored verbatim" is inaccurate for one of the two tests.

**VERDICT: FAIL — 0H/1M/1L**
