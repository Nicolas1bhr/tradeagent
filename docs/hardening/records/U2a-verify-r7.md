# U2a — ADVERSARIAL-VERIFY record · round 7 (leg [2], Opus, targeted)

**Sha under test:** `a974142` = `ffa1a3d` + 6 commits. **Same verifier** as rounds 4-6
(r4 FAIL 2H/1M/1L · r5 FAIL 1H/2M/1L · r6 FAIL 0H/1M/1L).
**Worktree:** `…/u2a-verify-r7`, branch `u2a-verify-r7-probes` from detached `a974142`. Nothing pushed.
**Toolchain:** dotnet 10.0.400, macOS Darwin 25.5.0, 11 cores. **The box is not mine** — the builder's
verified-tree 451 is a claim I read.

**VERDICT: PASS WITH LOW — 0H/0M/1L** (detail at the end) — first round with no HIGH and no MED.

| target | result |
|---|---|
| 1 · C1 one clock | **2005 ms**, not ≈4 s, on **my own** drain-then-stop fixture with a 64 KiB emergency; `FrameIncomplete` proves it reached the write |
| 2 · F-E as implemented | caller 2000-2004 ms in all 12 phases; **drop at ≈10.05 s, 12/12**; late answers kept and recorded; grace cost measured (below) |
| 3 · C3 derived drain | no `FromSeconds(55)` anywhere in `src/`; mutant back-to-literal RED |
| 4 · C4 / C5 | both ids null → `INVALID_REQUEST`, channel survives; liveness compare is now `>=` |
| 5 · PRIOR 8 | replay promised only for Buy/Sell, pinned per op; recovery line still names the id for every op |
| 6 · records | NOT-VERIFIED list matches reality (B4 open, ATAS limit open); CONTRACTS.md states the 1 KiB threshold |
| 7 · regression | **451 green** (75/108/268) exit 0, 321 s; 7/7 spellings, W3, M15/M16 all still biting |
| manager's ruling | record **UNKNOWN + NeedsReconciliation at 2040 ms** ✓, guidance present ✓, **lead-ordering not done → F-G (LOW)** |

## Pre-checks

```
git diff --name-only ffa1a3d..a974142                                   → 13 files
  … | grep -E 'TradingGateway.cs|Stores.cs|GatewayTypes.cs|DashboardView.cs'   → no matches
git log ffa1a3d..a974142 --format=%B | grep -ci co-authored             → 0
git status --short                                                      → clean
dotnet build TradeAgent.sln                                             → Build succeeded. 0 Warning(s) 0 Error(s)
```

F-A confirmed untouched, still with U2c-1. The one U2b-file edit the builder declares
(`ApprovalReauthorizationTests.ConnectorFacade` forwarding a new interface member) is in the diff and
is one line in a test file.

---

## Target 1 — C1, one clock across the gate and the write

**My own fixture, not the builder's** (`VerifyR7Probes.R7P4`, commit `dd16bff`): a peer that drains
at ~400 KiB/s and then **stops reading for good at 1.5 s**, a 512 KiB ordinary place taking the gate,
and a **64 KiB emergency** — oversized on purpose, because a ~100-byte cancel-all disappears into the
8 KiB buffer and can only ever measure the gate. Written to PASS if the emergency exceeds its
deadline; it FAILED:

```
 emergency elapsed = 2005 ms   (bytes the peer took: 556240)
   msg = the bridge is too slow; 'close' is NOT confirmed. It was still being sent when the deadline
         passed, so the connection has been dropped and will be retried — check your positions and
         orders in ATAS.
   connected after = False
```

**2005 ms, not ≈4 s.** The `FrameIncomplete` wording is the premise: the call reached the WRITE
rather than expiring on the queue, which is the only arrangement in which two clocks would show. The
peer really drained (556,240 bytes) and really stopped. `Place`/`Modify` keep their own budget — the
gate holder ran on `WriteTimeout`/`FrameTimeout` throughout and the class's exclusion tests are green.

---

## Target 2 — F-E as implemented, and the grace's cost

**The caller's bound and the drop, measured on twelve phases** (`VerifyR7Probes.R7P5`, my own probe,
written to PASS if any phase survives or the caller's answer moves — it FAILED):

```
   phase  0: caller  2004 ms  drop at  10057 ms  beats=1  said=busy
   phase  1: caller  2002 ms  drop at  10057 ms  beats=2  said=busy
   …
   phase 11: caller  2001 ms  drop at  10055 ms  beats=2  said=busy
 survived the grace = 0 of 12;  caller's worst answer = 2004 ms
```

**12 of 12 dropped at ≈10.05 s; the caller's answer is 2000–2004 ms in every phase**, with one or two
heartbeats inside the judging window each time — so every phase turns on heartbeats being refused as
evidence, as the builder claims. The two bounds really are separate.

The builder's own new cases all pass (16 targeted tests, 34 s): late answers at 2.5 s / 3.5 s kept and
recorded, the grace drop, C1's one-budget test, C4, C3's drain-follows, and PRIOR 8's two theories.

### The question the brief asks: what queues behind a dead bridge during the 10 s grace

`VerifyR7Probes.R7P2` — a peer that answers normally until the gateway is healthy, then **freezes**
(stops reading and answering). Three calls over the real pipe, at t+0, t+2.5 s and t+2.6 s:

```
 emergency#1 cancel   issued at t+    0 ms  took   2017 ms   the bridge is busy; 'orders' could not be read,
                                                             so the operation was not started. Nothing was
                                                             placed or cancelled. The connection is still up — try again.
 emergency#2 cancel   issued at t+ 2503 ms  took   2004 ms   (same)
 ordinary buy         issued at t+ 2602 ms  took   7414 ms   the ATAS bridge answered nothing within 10s;
                                                             'orders' is not confirmed and the bridge is not responding
 connected at t+10017 ms = False        connected at t+13020 ms = False
 RECORD r7q-e1 = (none)   r7q-e2 = (none)   r7q-buy = (none)
 needing reconciliation = 0
```

**Answers to the brief's three questions.** (1) A second emergency does NOT queue behind the first —
it gets its own two seconds (2004 ms) because the gate is free once each small frame is buffered.
(2) An ordinary order issued inside the grace pays **7414 ms** — it is released by the grace's own
drop at t+10 s rather than by its 10 s deadline. Before this round the connection was gone at ~2 s and
that call would have failed immediately with "not connected", so the grace converts a fast failure
into a slow one; it does not exceed the caller's own deadline, and the sentence it produces is
truthful. (3) **Nothing is left unsettled**: `needing reconciliation = 0`, and the connection is
dropped at t+10017 ms so the system recovers on its own. Rated LOW-and-accepted, not a finding — the
alternative is failing a caller on a connection that has not yet been judged.

---

## The manager's ruling — the sentence and the record at two seconds

The three calls above all died on a prerequisite READ, so no record existed to be UNKNOWN. To ask the
ruling's question at all the cancel frame itself has to be the thing unanswered, so `R7P3` uses a peer
that answers every read and mutes only `cancel`:

```
 elapsed  = 2040 ms
 RECORD   = UNKNOWN  reconcile=True
 err      = the bridge is busy; 'cancel' is NOT confirmed. The connection is still up — try again —
            check your positions and orders in ATAS.
 needing reconciliation = 1
   starts with the ORDER outcome?      False
   starts with the CONNECTION state?   True
   contains 'NOT confirmed'            True
   sends the owner to ATAS             True
```

- **The record IS UNKNOWN at two seconds**, with `NeedsReconciliation = true` — the ruling's record
  requirement is met, and neither MED trigger fires.
- **The outcome guidance IS present** — `'cancel' is NOT confirmed … check your positions and orders
  in ATAS`.
- **The sentence does not LEAD with the order outcome.** It leads with `the bridge is busy;` and the
  order outcome follows. That is finding F-G (LOW).

---

## Targets 3-6

**C3 — the drain is derived.** `GatewayPipeServer.cs:161`:
`DerivedDrainTimeout => gateway.Connector.WorstCaseOperationPath + TimeSpan.FromSeconds(5)`, behind
`HandlerDrainTimeout { get => _drain ?? DerivedDrainTimeout; init => _drain = value; }` (`:136-139`).
`grep -rn "FromSeconds(55)" src/` → **no hits**; the only remaining "55 s" strings are comments
describing the shipped-value arithmetic. `The_shutdown_drain_follows_the_connectors_deadlines_when_they_change`
constructs an `AtasConnector` with a 60 s RPC timeout, asserts `WorstCaseOrderPath == 100 s` and that
the drain exceeds it, and separately that an explicit `HandlerDrainTimeout = 7 s` still wins.

**C4 — a frame naming no request.** `GatewayPipeServer.cs` (commit `606890d`) refuses
`string.IsNullOrEmpty(rid)` with `INVALID_REQUEST` **before** the two id checks that used to
dereference it, and outside the handler's boundary so it does not become `UNKNOWN_ERROR`.
`A_frame_with_both_ids_explicitly_null_is_refused_and_the_channel_survives` passes.

**C5 — millisecond equality.** `AtasConnector.cs:960` is now
`Volatile.Read(ref _lastAnswerAt) >= since` (was `>`), so an answer landing on the same tick as the
check is kept rather than discarded.

**PRIOR 8 — the CLI's replay promise, per op.** `CliReplayContract.cs:71-77`: `Buy`/`Sell` get
*"re-running with the same --request-id returns this same result; it will not place a second order"*;
every other mutating op gets *"Re-running it is NOT a replay for this command yet — it acts again on
the book as it is then, so check `trade orders` or `trade positions` first"*; a read gets `null`.
Pinned by `The_success_note_promises_a_replay_only_where_the_gateway_performs_one` and
`A_read_gets_no_note`, both green. `RecoveryLine(outcome, requestId)` takes no op and is unchanged,
so **the recovery line still names the id for every op** — that is the same function W3 pins.

**Records.** `records/U2a.md`'s NOT-VERIFIED block is rewritten and matches what I can check from
here: it scopes the Windows claims to the SHA-verified box runs, names **B4 as run by nobody on
either platform** (true — I have not run it either), keeps ATAS's real client-order-id limit open, and
explicitly withdraws the old "one `close-all` settles both" claim on the correct ground that a ~23-character
generated sweep id cannot probe a 64-character boundary. `docs/CONTRACTS.md:85-98` states PRIOR 4's
residual by number: the progress budget is spent per **1 KiB** chunk, progress counts only when a
whole chunk is accepted, and a peer slower than one chunk per window is reported as stalled — with
the boundary named as movable, not removable.

---

## Target 7 — regression

```
dotnet build TradeAgent.sln            → Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR"
exit=0  wall=321s
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 842 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 268, Skipped: 0, Total: 268, Duration: 5 m 19 s - TradeAgent.IntegrationTests.dll
```

**451 green (75/108/268), 0 red — the builder's Mac count and its 5 m 19 s integration duration both
reproduce exactly.** One full-suite run used; one unspent. Standing probes:

```
PROBE4  seven operator spellings with STOP   Failed 7 of 7  (every probe fails == the guard holds)
W3      read-failure path -> NothingWritten  RED — A_reply_whose_read_fails_leaves_the_order_possibly_written
M15     AuthorizeOrThrow deleted             Failed 5, Passed 70 of 75
M16     RiskCheckOrThrow deleted             Failed 6, Passed 69 of 75
after restore + REBUILD                      Passed! 75 of 75
```

---

## Mutants

`cp` copies, `touch`ed, built, run, restored, `touch`ed again — never `git checkout --`. Pristine
sha1s `AtasConnector.cs ed5d470959149899…`, `GatewayPipeServer.cs 97a8b61d26445824…`,
`PipeClient.cs aff0bedbb6e3feb6…`, `TradingGateway.cs ec6e9fb7fad6535e…`; `git status --short` empty
after each, and the solution rebuilt before any `--no-build` run that followed a restore.

| # | mutant | `file:line` | bit? | evidence |
|---|---|---|---|---|
| C1 | the write starts a NEW clock (`writeDeadlineAt` → null) | `AtasConnector.cs:983` | **RED** | `An_emergency_spends_one_budget_across_the_gate_and_the_write` |
| FE | the grace is the caller's 2 s again (round-6 rule) | `AtasConnector.cs:910` | **RED (15)** | all twelve heartbeat phases + the idle-stalled grace test + both late-answer cases |
| C3 | the drain is a literal 55 s again | `GatewayPipeServer.cs:161` | **RED** | "the drain is 55s against a 100s worst path — it was written down rather than derived" |
| W3 | read-failure path → `NothingWritten` | `PipeClient.cs` | **RED** | `A_reply_whose_read_fails_leaves_the_order_possibly_written` |
| M15 | `AuthorizeOrThrow` deleted from `ApproveAsync` | `TradingGateway.cs:577` | **RED (5)** | U2b's re-check still bites |
| M16 | `RiskCheckOrThrow` deleted from `ApproveAsync` | `TradingGateway.cs:598` | **RED (6)** | U2b's re-check still bites |

Six mutants, six bit. Nothing survived.

## Findings

### LOW

**F-G — the caller's sentence does not LEAD with the order outcome, as the ruling directs.**
`src/TradeAgent.Connectors.Atas/AtasConnector.cs:183-188` (`EmergencySentence`), reached at `:1074`.
Measured through the real gateway (`R7P3`), the string the owner meets on the record is:

```
the bridge is busy; 'cancel' is NOT confirmed. The connection is still up — try again —
check your positions and orders in ATAS.
```

Both MED triggers named in the ruling are clear: the outcome guidance is present, and **the record IS
`UNKNOWN` with `NeedsReconciliation = true` at 2040 ms** (`needing reconciliation = 1`). What is not
done is the ordering the ruling asks for — the sentence leads with the connection state and the order
outcome follows, where the ruling wants *"'cancel-all' is NOT confirmed — check your orders in ATAS"*
first and the connection state as detail. It matters more now than it did in round 6: after the grace
change this "busy / still up" sentence is what EVERY emergency reads at two seconds, including one
against a bridge that is in fact dead and will be dropped eight seconds later.

**Fix expectation.** Reorder `EmergencySentence` so the mutating branch reads outcome-first — e.g.
`$"'{op}' is NOT confirmed — check your positions and orders in ATAS. The bridge is {condition}; {consequence}."`
— leaving the read branch (F-D) as it is, since a read has no outcome to lead with. The existing D1/D2
wording mutants and `An_emergency_says_confirm_only_when_something_could_have_been_changed` must stay
RED; add one assertion that the mutating sentence *starts with* the quoted op.

## NOT verified — by name

- **Every Windows figure is a claim I read.** The box is not mine. Unverified by me: the 451 on-box
  green, the 70-test class run, the five SHA-256 file hashes and the `.cs` count of 88 before and
  after, and "the same 451, test for test, and the same durations". I read how the identity check is
  constructed and it answers the objection round 5 raised; I did not repeat it, as the brief directs.
- **Mutant B4 (the Windows no-buffer stall) — still run by nobody, including me.** The 8 KiB buffer
  is unproven by mutation on either platform.
- **ATAS's real client-order-id limit and the `op-…` shape** — needs the deliberate 64/65-character
  probe at v0.1.2.
- **Whether a real ATAS synchronous call exceeds two seconds in practice** — F-E's premise is read
  from `BridgeProtocol.cs` and reproduced with synthetic peers by both the builder and me.
- **`LateAnswers` / `LateAnswerReceived` are exposed and unconsumed.** I confirmed the late-answer
  tests pass; I did not verify that anything settles a request on a late answer, because by decision
  nothing in this unit does — that is U2c-1's.
- **F-A** (the operator's Close All on the ordinary deadline) — untouched, still with U2c-1, not
  re-measured.
- **The 5 m 19 s suite duration as an accepted cost** — I reproduced the number; whether it is
  acceptable is the manager's ruling, not mine.

## What I did NOT do

- I did not fix anything and did not push. Probes are on `u2a-verify-r7-probes` (seven cherry-picked
  from rounds 4-6, plus `dd16bff` and `06510d2`). `git diff a974142 -- src/ docs/` is empty.
- I ran the full solution suite **once**; one run is unspent.
- I did not reproduce the builder's RED states for C1, C3, C4 or PRIOR 8 at `ffa1a3d` — I re-established
  each by mutation at `a974142` instead, which is a weaker order for the same evidence and is the
  same trade the builder disclosed for its own inverted cases.
- I did not run the App, `tools/probe`, `tools/win-*.sh`, or ATAS.
- I did not read leg [3]'s output.
- I mutated `TradingGateway.cs` twice (M15/M16) as a read-only regression check, restored it from a
  `cp` copy and rebuilt afterwards.
- My round-6 probe `R6P1` now reports "12 of 12 kept" because it samples the connection 150 ms after
  the caller returns, which the grace decision deliberately changed; I replaced it with `R7P5`, which
  polls for the drop, rather than reporting the stale assertion as a regression.

## Verdict

Everything the bounce and the Codex addendum asked for is closed, and closed with teeth. C1 — the one
HIGH — is fixed on one clock and I proved it with **my own** fixture rather than the builder's: a peer
that drains and then stops, an oversized emergency, **2005 ms with the `FrameIncomplete` wording that
shows the call reached the write**. F-E's two bounds are genuinely separate and I measured both
independently: **12 of 12 phases dropped at ≈10.05 s while every caller answered at 2000-2004 ms**,
with one or two heartbeats inside each judging window, so no phase passes by silence. C3's drain is
derived with no literal left, C4 refuses a frame naming no request without taking the channel down,
C5 is `>=`, and the CLI no longer promises a replay the gateway does not perform. 451 green reproduces
exactly, including the 5 m 19 s the builder flagged, and six of six mutants bit.

The brief's hard question has a clean answer: during the grace a second emergency still gets its own
two seconds, an ordinary order pays up to the remaining grace instead of failing fast, **nothing is
left unsettled** (`needing reconciliation = 0`) and the connection drops on its own at 10 s.

One point remains, and it is wording rather than mechanism. The manager ruled that the caller's
sentence must lead with the order outcome; it still leads with the connection state. Neither MED
trigger the ruling names is met — the guidance is there and the record is `UNKNOWN` with
`NeedsReconciliation` set at 2040 ms — so this is a LOW, but it is now the sentence every emergency
reads at two seconds, including against a bridge that is already dead.

**VERDICT: PASS WITH LOW — 0H/0M/1L**
