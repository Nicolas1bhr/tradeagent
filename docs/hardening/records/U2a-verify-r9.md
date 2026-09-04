# U2a — ADVERSARIAL-VERIFY record · rounds 8 AND 9 (leg [2], Opus, fresh verifier)

**Sha under test:** `088c059` = `a974142` + round 8 (5 commits) + round 9 (7 commits).
**FRESH verifier** — the round 4–7 verifier's session is gone; its records are my baseline, its verdicts are not.
**Worktree:** `…/ai-trading-software-for-mihael-worktrees/u2a-verify-r9`, branch `u2a-verify-r9-probes`
(nine probe commits cherry-picked from `u2a-verify-r7-probes`, plus my own `52091c7`). Nothing pushed.
**Toolchain:** `PATH=$HOME/.dotnet:$PATH`, `DOTNET_ROOT=$HOME/.dotnet`, macOS Darwin 25.5.0.
**The box is NOT mine.** Round 8's on-box 455 is a claim I read; round 9 has no box figure at all.

**VERDICT: FAIL — 0H/2M/3L** (detail at the end; the two MEDs share one root cause and it is named there).

---

## Pre-checks

```
git log --oneline -1                     → 088c059 Cover the leg parked for a person, …
git diff --stat 5624cd1..088c059         → 7 files, 944 insertions(+), 70 deletions(-)
git diff --stat a974142..088c059 -- src/TradeAgent.Gateway/TradingGateway.cs src/TradeAgent.Core
                                         → EMPTY (the forbidden files really are untouched)
dotnet build TradeAgent.sln --no-incremental
                                         → Build succeeded.  0 Warning(s)  0 Error(s)  EXIT=0
```

---

## Target 9 — the build gate (round 9 F5)

**Verified by running `dotnet build TradeAgent.sln --no-incremental` → `Build succeeded. 0 Warning(s) 0 Error(s)`,
exit 0.** The CS8619 the round-8 finisher found is gone. The mutant is below (M6); a gate nobody can make
RED proves nothing.

---

## Target 1 — one deadline per emergency OPERATION

All three on **my own** fixtures (`VerifyR9Probes`, commit `52091c7`), through the real IPC pipe.

**Codex's own check — three 1.9 s replies (orders read, target resolution, cancel):**

```
R9P3  IPC cancel-all with three 1.9 s calls answered in 2001 ms
      not-sent  state=(none)  err=the operation deadline passed before the simulator answered; …
      needing reconciliation = 0
```

**2001 ms against a two-second operation, not 5.7 s.** The per-RPC clock is gone.

**The five-order sweep at 1 s per leg:**

```
R9P1  sweep answered in 2004 ms
      cancelled = 0   attempted = 0   not_sent = 5
      not-sent  ×4  err=the operation deadline passed before the simulator answered; it is not known whether it acted
      not-sent  ×1  err=the operation ran out of time before this leg was issued; it was not sent
      needing reconciliation = 0
```

Every one of the five orders is NAMED in the answer, four of them because their own target resolution ran
out of time and one because its turn came after the deadline. Nothing is skipped in silence and nothing is
left needing reconciliation. **What this run does NOT show is a leg that reached the wire** — at 1 s per
call the sweep's own `orders` read plus one resolution is already the whole budget, so `attempted = 0`.
The builder's `A_five_order_sweep_answers_within_the_budget_and_accounts_for_every_order` passes on exactly
this shape: it asserts `not_sent > 0` and never asserts that any leg was attempted. (LOW, F-5 below.)

**The other direction — a sweep that fits:**

```
R9P2  sweep answered in 546 ms
      cancelled = 5   attempted = 5   not_sent = 0
      sent-and-confirmed  state=CANCELLED  ×5   needing reconciliation = 0
```

Five legs at 100 ms all confirmed, so "refuse everything" is not what makes R9P1 pass.

**Concurrency and the send gate.** `RunLegs` issues legs in waves of `MaxLegsInFlight = 4` and awaits each
wave in `Collect` before the next is issued (`GatewayPipeServer.cs:849-885`). The gate's backpressure is
therefore not bypassed — the connector's `_sendGate` still serialises frames, and the wave bound caps how
many gateway dispatches queue on it. The ordering hazard the brief names (a cancel leg racing its own
target resolution) cannot arise: each leg resolves its own target inside its own `CancelAsync` before that
leg's cancel frame is built (`TradingGateway.cs:635-649`), and legs address different orders.

---

## Target 7 — the outcome vocabulary, and the SIXTH state

The five words plus `nothing-to-do` are produced only by `Classify(record)` (`GatewayPipeServer.cs:786-801`),
and `Describe()` has no catch-all arm. Both are real improvements. Two things survive.

### F-1 (MED) — a leg the connector refused BEFORE sending still reads `sent-not-confirmed`

`R9P8`, through the real pipe:

```
sweep answered in 2432 ms
  cancelled = 0   attempted = 1   not_sent = 0
  sent-not-confirmed   state=UNKNOWN   reconcile=True
      err='cancel' is NOT confirmed — check your positions and orders in ATAS. It was not sent:
          the operation ran out of time before this leg's turn came.
  needing reconciliation = 1
```

The word and the error inside the SAME leg object contradict each other: `sent-not-confirmed` is documented
as *"It was sent and the outcome is not known"* (`GatewayPipeServer.cs:768`) and the error says *"It was
not sent"*. Detail in the findings section.

### F-2 (MED) — disposal returns with a request DISPATCHING, unflagged and unlogged

`R9P9`, a connector that HONOURS its cancellation token:

```
derived drain = 700 ms
state when disposal starts = DISPATCHING
disposal returned after 722 ms
DISPATCHING rows = 1
handlers_did_not_finish = (not logged)
op_failed               = error
record = DISPATCHING  reconcile=False
needing reconciliation = 0
```

Detail in the findings section.

### What the sweep is NOT exposed to (checked, and it is sound)

A sweep leg cannot be cancelled by disposal's token before its own emergency deadline kills it: the derived
drain is `max(5×WorstCase, EmergencyBudget + WorstCase) + Settle`, which is `≥ EmergencyBudget` by
construction, so the leg always expires first and expires into `ConnectorTransportException` → UNKNOWN.
`Busy` and `PeerStalled` both throw `ConnectorTransportException` too (`AtasConnector.cs:1069-1077`) and so
land on UNKNOWN + reconciliation, which is the truthful mapping for both. A **late definite answer** is
delivered to `LateAnswerReceived` and settles nothing by decision (U2c-1's), so it cannot move a word.

---

## Target 3 — F3/F2, the late-answer grace and `_abandoned`

`R9P11`, my own fixture on the real `AtasConnector` with a 500 ms emergency deadline and a peer that mutes
only `cancel`:

```
peer-disconnects   caller: 'cancel' is NOT confirmed — check your positions and orders in ATAS. …
                   awaiting a late answer right after the caller gave up = 1
                   after the peer goes away: 0
connector-disposed awaiting a late answer right after the caller gave up = 1
                   after disposal: 0
```

Both exits that used to leak the id now clear it. **Verified.**

---

## Target 4 — the sentence leads with the outcome (F-G)

`R7P3` (the round-7 verifier's own probe, re-run at this sha) drives a real `AtasConnector` whose peer
answers every read and mutes only `cancel`:

```
elapsed  = 2019 ms
RECORD   = UNKNOWN  reconcile=True
err='cancel' is NOT confirmed — check your positions and orders in ATAS. The bridge is busy;
    The connection is still up — try again.
needing reconciliation = 1
```

**Outcome first, connection state after** — F-G is closed on the string the owner actually meets, and the
record is still UNKNOWN + `NeedsReconciliation` at two seconds. `R9P10` confirms the same ordering on the
pre-gate refusal. One starts-with assertion exists (`ConnectorSendDeadlineTests.cs:686`); its mutant is M1.

---

## Target 8 — the longest ordinary chain, counted independently

`R9P4`, my own connector decorator over the real pipe (allowlist set, i.e. the configured install):

```
COLD buy   : 5 calls -> account -> positions -> quote -> instruments -> place
WARM buy   : 4 calls -> account -> positions -> quote -> place
modify     : 1 call  -> orders   (my request was malformed; by inspection modify is orders + modify = 2)
SerialConnectorCallsPerHandler = 5
```

**The count reproduces exactly, including the order.** I read every ordinary handler in the pipe's dispatch
table (`GatewayPipeServer.cs:592-618`) and none of the reads issues more than two; `modify` is
`ResolveConnectorOrderId` + `ModifyOrderAsync`; `close`/`close-all`/`cancel`/`cancel-all` are risk-reducing
and take the other shape. Five is the longest ordinary chain.

**The override, both directions** (`R9P7`, a 60 s worst path):

```
derived         = 00:05:05      (5 × 60 + 5)
asked for 7 s   = 00:05:05      clamped — the caller cannot shorten it
asked for 900 s = 00:15:00      a longer value is honoured
settle = 0      = 00:05:00      still ≥ the 300 s chain
```

**The price the manager accepted** (`R9P6`): with a 100 s worst path the drain would be 505 s; an idle
connected client is disposed in **1 ms**. The 255 s is genuinely paid only with a request in flight.

---

---

## Target 5 — regression: the round-7 probes on the new clock model

All three re-run at `088c059` from the cherry-picked branch. They are written so that the FAILURE
carries the measurement (`Assert.True(false, …)`), which is the round-7 verifier's own style.

```
R7P4  emergency elapsed = 2006 ms   (bytes the peer took: 556240)
      msg = 'close' is NOT confirmed — check your positions and orders in ATAS. The bridge is too
            slow; It was still being sent when the deadline passed, so the connection has been
            dropped and will be retried.
      connected after = False
```

**2006 ms** against round 7's 2005 ms, with the `FrameIncomplete` wording that proves the call reached
the WRITE. The per-operation deadline did not reintroduce a per-RPC restart on the write path.

```
R7P5  phase  0: caller  2006 ms  drop at  10044 ms  beats=1  said=busy
      …
      phase 11: caller  2002 ms  drop at  10049 ms  beats=2  said=busy
      survived the grace = 0 of 12;  caller's worst answer = 2006 ms
```

**12 of 12 dropped at ≈10.04 s while every caller answered at 2000–2006 ms**, with one or two
heartbeats inside each judging window. Round 7's figures reproduce exactly.

```
R7P2  emergency#1 cancel  issued at t+    0 ms  took   2018 ms  'orders' could not be read, so the
                          operation was not started. Nothing was placed or cancelled. The bridge is busy…
      emergency#2 cancel  issued at t+ 2502 ms  took   2004 ms  (same)
      ordinary buy        issued at t+ 2601 ms  took   7413 ms
      connected at t+10016 ms = False      needing reconciliation = 0
```

A second emergency during the grace still gets its own two seconds; the ordinary order is released by
the grace's own drop; **nothing is left needing reconciliation**. The read branch still says "could not
be read … Nothing was placed or cancelled" (F-D), so the outcome-first rewrite did not spread the
mutating wording onto a read.

---

## Mutants

`cp` copies, patched with an anchored script, `touch`ed, solution rebuilt, run, restored from the copy
and `touch`ed again — **never `git checkout --`**; `git status --short` was empty after every one
(the harness prints it, and every line of `mut/all.log` reads `git status after restore: ''`).

| # | mutant | `file:line` | bit? | evidence |
|---|---|---|---|---|
| M1 | `EmergencySentence` leads with the connection again | `AtasConnector.cs:189-191` | **RED 2** | `An_emergency_a_bridge_answers_late_keeps_it_and_records_the_answer` (both arms) |
| M2 | the chain is 4, not 5 | `GatewayPipeServer.cs:259` | **RED 1 of 2** | `A_cold_placement_issues_no_more_connector_calls_than_the_drain_assumes`; **`Disposal_covers_a_cold_placement…` PASSED at 4** — that test discriminates 3 from 5, not 4 from 5 |
| M3 | the override clamp inverted (`<` for `>`) | `GatewayPipeServer.cs:157` | **RED** | `No_combination_of_settings_makes_the_drain_shorter_than_the_chain` |
| M4a | REJECTED → `NotConfirmed` | `GatewayPipeServer.cs:790` | **RED 1 of 33** | `A_definite_broker_refusal_reads_rejected_and_needs_no_reconciliation` |
| M4b | CREATED/AWAITING_APPROVAL → `NotConfirmed` | `:794` | **RED 1 of 33** | `A_close_leg_parked_for_approval_reads_not_sent_and_is_not_counted_as_attempted` |
| M4c | WORKING/… → `NotConfirmed` | `:796-797` | **RED 1 of 33** | `A_close_leg_whose_order_rests_reads_still_working_not_unknown` |
| M4d | CANCELLED/FILLED → `NotConfirmed` | `:788` | **RED 1 of 33** | `A_definite_broker_refusal_reads_rejected_and_needs_no_reconciliation` |
| M5 | the pre-issue deadline check deleted (legs issued regardless) | `GatewayPipeServer.cs:861-867` | **RED** | `A_five_order_sweep_answers_within_the_budget_and_accounts_for_every_order` |
| M6 | the CS8619 fix reverted (direct call, no `!`) | `GatewayPipeServer.cs:708` | **RED (build)** | `--no-incremental` → `GatewayPipeServer.cs(708,32): warning CS8619` |
| M7 | `LeftUntil` hands out 1 ms past the deadline | `RiskReducingScope.cs:80` | **RED** | `An_absolute_deadline_that_has_passed_leaves_nothing_not_a_millisecond` |
| M8 | the simulator maxes its two latencies again (both places) | `FakeConnector.cs:31,92` | **RED** | `The_simulators_two_latencies_add_up_rather_than_competing` |
| M9 | every RPC starts its own budget again | `AtasConnector.cs:1030-1033` | **RED** | `Two_emergency_calls_inside_one_operation_share_its_deadline` — *"took 4.01s — each is still starting its own two seconds"* |
| M10 | the early-exit stops clearing `_abandoned` | `AtasConnector.cs:955-957` | **RED 4** | the builder's `[Theory×2]` **and** both arms of my own `R9P11` |

Thirteen mutants, thirteen bit. **Every one of the five mapping arms has exactly one biting test**
(1 failure of 33 each time), which is the builder's claim and it holds.

**Standing probes, still biting:**

```
PROBE4  seven operator spellings with STOP   Failed 7 of 7   (every probe fails == the guard holds)
M15     AuthorizeOrThrow disabled in ApproveAsync   Failed 5, Passed 70 of 75
M16     RiskCheckOrThrow disabled in ApproveAsync   Failed 6, Passed 69 of 75
W3      read-failure path -> NothingWritten         RED — A_reply_whose_read_fails_leaves_the_order_possibly_written
```

M15/M16 name the identical five and six tests the round-7 verifier recorded.

---

## Findings

**The class both MEDs share, named rather than enumerated (§9.10).** Round 9's rule is *"the record
decides the word"*, and it is the right rule — but it was applied without checking the other half:
**that the record is 1:1 with what actually happened.** Both findings below are places where the record
is wrong about reality, and the vocabulary now faithfully repeats the wrong thing. F-1: a leg the
connector proved was never sent gets UNKNOWN, so it reads `sent-not-confirmed`. F-2: a leg nobody will
ever settle stays DISPATCHING, which also reads `sent-not-confirmed`, and this time without even the
reconciliation the word promises. The structural fix is to close the two gateway mappings, not to add
a sixth word.

### MED

**F-1 — a leg the connector refused BEFORE sending reads `sent-not-confirmed`, and pauses trading.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:768` (the word's documented meaning) and `:800`
(`Classify`'s default arm), reached through `src/TradeAgent.Gateway/TradingGateway.cs:660-665`, which
maps *every* `ConnectorTransportException` to `SettleUnknown`.

This is Codex round-8 F1 reaching the same wrong word by a third route. The fix closed the route where
no record exists (resolution expiring before `TryCreate` → `not-sent`). It leaves the route where the
resolution lands just INSIDE the deadline, `TryCreate` runs, and the connector then refuses the send
because the operation is over.

*The refusal is real, not modelled.* `R9P10` drives the shipped `AtasConnector` over a real pipe with an
expired operation deadline:

```
message   = 'cancel' is NOT confirmed — check your positions and orders in ATAS. It was not sent:
            the operation ran out of time before this leg's turn came.
connected = True
```

That branch is `AtasConnector.cs:1043-1050`, and it is the branch round 8 ADDED so that a leg whose turn
never came would not judge the bridge. `R9P8` puts a connector with exactly that branch behind the real
gateway and pipe, and the sweep answers:

```
sent-not-confirmed   state=UNKNOWN   reconcile=True   attempted=1   not_sent=0
   err='cancel' is NOT confirmed — … It was not sent: the operation ran out of time before this leg's turn came.
needing reconciliation = 1
```

**Two harms.** The owner is sent to hunt through ATAS for an order this process proved it never sent —
the exact service failure the F-D/F-G wording work exists to prevent. And `NeedsReconciliation` is set,
which `TradingGateway.cs:243` counts to refuse *all* further execution with
`TRADING_PAUSED_UNRECONCILED` — including the retry the sentence itself advises, and including the next
`cancel-all`. A cancel request's `ClientOrderId` never reaches a broker order, so what the reconciler
can do with such a row is a question this unit does not answer.

**Fix expectation.** A pre-gate refusal is DEFINITE about not-sending and must be distinguishable from an
ambiguous transport failure — a distinct exception (or a flag on `ConnectorTransportException`) that
`CancelAsync`/`ModifyAsync` map to "no dispatch happened", leaving the record in a non-UNKNOWN state
and the word `not-sent`. `TradingGateway.cs` is not this unit's to edit, so the minimum here is: state
it in the record, route the mapping to whoever owns that file, and add `R9P8` as a pinning test so the
gap cannot be mistaken for the guarantee `:768` currently documents. Alternatively the connector's
pre-gate refusal could be raised where the gateway has not yet written a record.

**F-2 — disposal returns with a request DISPATCHING, unflagged, and NOT reported.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:1155-1190` (`DisposeAsync`) with
`src/TradeAgent.Gateway/TradingGateway.cs:696-700` (`ModifyAsync` catches only `ConnectorRejectedException`
and `ConnectorTransportException`) — where `DispatchPlaceAsync` at `:481` also catches
`TimeoutException or OperationCanceledException`.

Round 9 states: *"That remaining exit is deliberate and stays … the only thing that still produces one is
a call that does not honour its cancellation token. It is logged at `error` because it is the sole trace
that an order may have been left unsettled."* **The connector in `R9P9` honours its token** — the fake's
`Task.Delay(LatencyMs, ct)` — and the result is:

```
derived drain = 700 ms      state when disposal starts = DISPATCHING
disposal returned after 722 ms
DISPATCHING rows = 1        record = DISPATCHING  reconcile=False
handlers_did_not_finish = (not logged)      needing reconciliation = 0
```

The handler DID finish — it unwound through the cancellation — so the abandonment sentinel never fires;
the only trace is a generic `op_failed`. `ReconcileAsync` scans `NeedingReconciliation()` alone
(`TradingGateway.cs:783`), and this row is not flagged, so **nothing will ever settle it**. That is the
abandoned-DISPATCHING order the whole 255 s drain was bought to prevent, produced silently by a
well-behaved connector. It needs a connector that under-reports its worst case (`FakeConnector`'s own
`init`, which the builder introduced this round as "a situation an operator can actually be in") — the
drain derives itself correctly from what it is told, and what it is told is wrong. Sweep legs are NOT
exposed (their emergency deadline always expires first); `modify` and a single `cancel` past the drain
are.

**Fix expectation.** Two independent changes: (a) `CancelAsync`/`ModifyAsync` catch
`OperationCanceledException`/`TimeoutException` exactly as `DispatchPlaceAsync` does, so a cancelled
mutation is recorded UNKNOWN and reconciled — that is `TradingGateway.cs`, so route it; and (b) the
disposal sentinel counts what it is actually about: **requests still DISPATCHING when `DisposeAsync`
returns**, not handler tasks still running. A test with a token-honouring connector and an
under-reporting worst case (`R9P9`) makes both bite.

### LOW

**F-3 — `Classify` kept the catch-all that `Describe()` had removed for exactly this reason.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:800`, `_ => LegOutcome.NotConfirmed`. `Describe()` at
`:824` now throws on an unmapped outcome, and the commit message for it is right: a new member must not
be reported as something wrong and dangerous in silence. One switch over, a new `ExecutionState` still
becomes `sent-not-confirmed` — the state that promises UNKNOWN and reconciliation — with no compiler
complaint and no failing test. **Fix expectation:** list DISPATCHING/UNKNOWN/RECONCILING explicitly and
throw on anything else, the same shape as `Describe()`.

**F-4 — the risk-reducing term counts ONE trailing ordinary call; a `close-all` wave owes up to four.**
`src/TradeAgent.Gateway/GatewayPipeServer.cs:220-221` (`RiskReducingHandlerPath`) and `:242-244` (*"Plus
exactly one ordinary call, and that one is not a rounding allowance"*), against `:890`
(`MaxLegsInFlight = 4`) and `TradingGateway.cs:27` (`_dispatchGate = new SemaphoreSlim(1, 1)`, held
across `PlaceOrderAsync`). Four close legs are issued at once and every one ends in `PlaceAsync`, so
their trailing places run ONE AT A TIME inside a single handler.

Measured (`R9P13`): a four-position `close-all` with a one-second connector took **9.06 s**, where
`max(5×1, 6.5+1) = 7.5 s` is the whole derived term. What covers the difference at the default settings
is `SettleAfterCancelTimeout` (5 s) — a margin the source calls a margin and
`No_combination_of_settings_makes_the_drain_shorter_than_the_chain` deliberately allows a caller to
shorten. The gap opens whenever `EmergencyBudget > WorstCaseOperationPath + SettleAfterCancelTimeout`,
which is **the suite's own disposal fixture**: 30 s budget over a 4 s connector needs
`30 + 4×4 = 46 s` against `max(20, 34) + 5 = 39 s`. At shipped ATAS values (2 s over 50 s) the ordinary
term dominates and it is covered. **NOT verified: that this leaves an abandoned DISPATCHING row** —
`R9P12` (settle 200 ms, disposal 200 ms in) did not catch one; I measured the shortfall, not the harm.
**Fix expectation:** `EmergencyBudget + MaxLegsInFlight × WorstCaseOperationPath`, with a test at values
where the emergency budget exceeds one call plus the settle.

**F-5 — the five-order acceptance passes with `attempted = 0`.**
`tests/TradeAgent.IntegrationTests/SweepRequestIdTests.cs:292-345`. At one second per call, the sweep's
own `orders` read plus one target resolution is the entire two-second budget, so every leg comes back
`not-sent` and the assertions (`not_sent > 0`, at least one pre-issue reason, every leg named) are all
satisfied by a sweep that attempted nothing (`R9P1`, measured). The brief's acceptance — *"which sent,
which confirmed, which not sent"* — is never exercised in one answer. **Fix expectation:** one case with
a MIXED reply, some legs `sent-and-confirmed` and some `not-sent` together; a per-leg latency of ~250 ms
over five legs would produce it.

---

## Target 6 — the full suite, and the counts

```
dotnet build TradeAgent.sln --no-incremental   → Build succeeded. 0 Warning(s) 0 Error(s)   (pristine tree)
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR"
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 844 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 283, Skipped: 0, Total: 283, Duration: 5 m 53 s - TradeAgent.IntegrationTests.dll
EXIT=0
```

**466 green (75 / 108 / 283), 0 failed, 0 skipped — the builder's Mac count reproduces exactly**,
including the 5 m 53 s integration duration. One full-suite run used; one unspent.

**The test-name diff, taken myself** (`git grep -h -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`,
reduced to method names, sorted unique):

| | `a974142` | `5624cd1` | `088c059` |
|---|---|---|---|
| distinct method names | 353 | 357 | 367 |
| `[Fact]` | 320 | — | 333 |
| `[Theory]` | 27 | — | 28 |
| `[InlineData]`/`[MemberData]` rows | 122 | — | 124 |

**REMOVED between `a974142` and `088c059`: 0** (`comm -23` of the two sorted lists is empty). +4 then
+10 method names; +13 Facts, +1 Theory, +2 rows. **451 + 4 + 11 = 466**, which is what ran. My absolute
counts differ from the builder's by five because I key on the method name and the builder keys on
`path::method`; the deltas and the zero removals agree exactly.

**One caveat on the build gate, stated so nobody misreads a later run.** My own probe files add two
xUnit analyzer warnings to the solution build (`VerifyR7Probes.cs` xUnit2020, cherry-picked from the
round-7 branch; one of mine, since removed). **Zero of the warnings in any build I ran come from
`src/`** (`grep "warning" build.log | grep "/src/" | wc -l` → `0`), and the 0-warning figure quoted at
the top was taken on the pristine tree before any probe existed.

---

## NOT verified — by name

- **Every Windows figure.** The box is not mine and round 9 has no box run at all: the seven commits of
  round 9 **have never been built or run on the target platform**, by the builder or by me. Round 8's
  455-on-the-box (with its SHA-256 identity check) is a claim I read. Everything in this record is macOS.
- **F-4's harm.** I measured the shortfall (9.06 s handler against a 7.5 s derived term) but did not
  produce an abandoned DISPATCHING row from it; `R9P12` finished inside the drain twice.
- **F-1 on the real connector end-to-end.** `R9P10` proves the shipped `AtasConnector` produces the
  pre-gate refusal, and `R9P8` proves the gateway maps that refusal to UNKNOWN + `sent-not-confirmed`,
  but the two halves were measured on two fixtures rather than one: I did not stage a real bridge peer
  whose `orders` reply lands in the last millisecond of the operation.
- **What the reconciler does with a flagged CANCEL request** whose client order id never reached a
  broker order. Named as a consequence of F-1; not measured. `ReconcileAsync` is `TradingGateway`'s.
- **Mutant B4** (the Windows no-buffer stall) — still run by nobody, including me.
- **ATAS's real client-order-id limit**, and whether a real ATAS synchronous call exceeds two seconds in
  practice. Unchanged from round 7; both need the box.
- **`LateAnswers`/`LateAnswerReceived` are exposed and unconsumed** — I verified the counters return to
  zero, not that anything settles a request on a late answer (by decision, U2c-1's).
- **F-A** (the operator's Close All on the ordinary deadline) — untouched, still with U2c-1.
- **Cross-handler queueing** (N concurrent placements on `_dispatchGate` under one drain) — named by the
  builder, not measured by me either.
- **The 265 s disposal ceiling as an acceptable product cost** — I verified an idle shutdown is 1 ms and
  that the number is derived rather than written down. Whether 265 s is acceptable is the manager's
  ruling, which the record says has been taken.

## What I did NOT do

- **I fixed nothing and pushed nothing.** `git diff 088c059 -- src/ docs/` is empty in my worktree; every
  change is test-only, on `u2a-verify-r9-probes` (three commits of mine on top of nine cherry-picked).
- I ran the full solution suite **once**; one run is unspent.
- I did not reproduce the builder's RED states at `5624cd1`; I re-established each guard by mutation at
  `088c059` instead, which is the weaker order for the same evidence and is the trade the builder
  disclosed for its own inverted cases.
- I did not run the App, `tools/probe`, `tools/win-*.sh`, ATAS, or anything on the Windows box, and I
  placed, modified and cancelled no real order.
- I did not read leg [3]'s output for this round (there is none yet; `codex-U2a-r8.txt` is round 8's and
  I read it as the bounce's input).
- I mutated `TradingGateway.cs` twice (M15/M16) and `PipeClient.cs` once (W3) as read-only regression
  checks, restored each from a `cp` copy and rebuilt afterwards.
- I did not re-measure rounds 4b–7's own evidence beyond the three regression probes named in target 5.
- I did not attempt the sixth-state hunt on `Busy`/`PeerStalled` end-to-end; I read their throw sites and
  established that both produce `ConnectorTransportException`, which is the same mapping the measured
  cases take.

---

## Verdict

Rounds 8 and 9 close real defects and close them with teeth. The one deadline per operation is genuine
and I measured it three ways on my own fixtures: **2001 ms** for Codex's three-1.9 s check, **2004 ms**
for a five-order sweep in which every order is named, and **546 ms** with every leg confirmed when the
work fits. The chain count is **five, in the order the source claims**, counted by my own decorator. The
override clamps in both directions, an idle shutdown costs **1 ms**, `_abandoned` returns to zero on both
exits that used to leak it, the sentence now leads with the order outcome, the build gate is honestly 0
warnings, **466 green reproduces exactly**, and thirteen of thirteen mutants bit — including one per
mapping arm, each with exactly one biting test.

What does not hold is the guarantee the round hangs the vocabulary on. *"The record decides the word"* is
the right rule, and it was adopted without auditing the other half of it: that the record is 1:1 with
what happened. A leg the shipped connector proves it never sent is recorded UNKNOWN and reported
`sent-not-confirmed` — the same word, for the same reason, that Codex raised F1 about — and the flag it
sets pauses the very retry the sentence advises. A `modify` cancelled by disposal is left DISPATCHING
with no flag, no reconciliation and no `handlers_did_not_finish`, by a connector that honours its token,
which is precisely the case round 9 says cannot happen. Both are one-line mappings in a file this unit
may not open, so the action is the manager's: route them, and pin them here with the two tests that
produce them.

**VERDICT: FAIL — 0H/2M/3L**
