# U2a — ADVERSARIAL-VERIFY record · rounds 10 AND 11 (leg [2], Opus, fresh verifier)

**Sha under test:** `120c739` = `088c059` + round 10 (7 commits) + round 11 (5 commits).
**FRESH verifier** — the rounds 4–9 verifiers' sessions are gone; their records are my baseline, their
verdicts are not.
**Worktree:** `…/ai-trading-software-for-mihael-worktrees/u2a-verify-r11`, branch
`u2a-verify-r11-probes` (twelve probe commits cherry-picked from `u2a-verify-r9-probes`, plus my own
`VerifyR11Probes.cs`). Nothing pushed. No git command run in the main worktree.
**Toolchain:** `PATH=$HOME/.dotnet:$PATH`, `DOTNET_ROOT=$HOME/.dotnet`, macOS Darwin 25.5.0.
**The box is NOT mine.** Round 11's on-box 491 twice, 116/116 pipe classes, the eight hashes and the
five green runs of the round-7 test are claims I read. Everything below is macOS.

**VERDICT: FAIL — 0H/2M/4L** (detail at the end; the two MEDs share one root cause and it is named there).

---

## Pre-checks

```
git log --oneline -1                → 120c739 Read the busy-bridge verdict before the test tears down …
dotnet build TradeAgent.sln --no-incremental      (pristine tree, before any probe existed)
    → Build succeeded.  0 Warning(s)  0 Error(s)   Time Elapsed 00:00:02.03   EXIT=0
git diff --name-only 088c059 120c739     → eleven files, none of them forbidden (below)
```

**Scope, checked myself:**

```
git diff --name-only 088c059 120c739 | grep -E 'TradingGateway.cs|DashboardView.cs|Stores.cs|GatewayTypes.cs'
    → (no match — the four forbidden files are untouched)
git log 088c059..120c739 --format=%B | grep -ci co-authored   → 0
git rev-list --count 088c059..120c739                          → 12
eleven files changed: docs/CONTRACTS.md · ConnectorSdk/TransportLedger.cs · Connectors.Atas/AtasConnector.cs ·
Connectors.Fake/FakeBroker.cs · Connectors.Fake/FakeConnector.cs · Core/TransportOutcome.cs ·
Gateway/GatewayPipeServer.cs · TradeCli/TransportResult.cs · the three pipe test classes
```

---

## Target 1 — the drain table, attacked

### The op set comes from the dispatcher, and it is 21 both ways (`R11P1`)

I enumerated the protocol vocabulary by reflection myself, drove every op over the real pipe, and
compared the answer with `HandlerPaths` in BOTH directions.

```
vocabulary   = 21: account accounts buy cancel cancel-all close close-all connectors executions
                   instruments material-list material-note modify order orders position positions
                   quote schema sell status
handled      = 21: (identical)
table rows   = 21: status schema accounts account instruments quote positions position orders order
                   executions connectors material-list material-note buy sell modify cancel
                   cancel-all close close-all
handled \ rows = ∅        rows \ handled = ∅        rows are distinct
"zz-not-an-op" → NOT handled          (the discriminator's own premise)
hello          → answered ok=True by the read loop  (the premise its exclusion rests on)
```

**The four operations round 11 added are in the table with the depth it claims:** `schema` at `2W`
(measured serial depth **1** connector call — `account` — so `2W` over-covers it), and `connectors`,
`material-list`, `material-note` at `0` (measured **0** connector calls each).

### Every handled op measured, not the nine the builder's theory names (`R11P2`)

A counting decorator over the simulator records each call's start and end, so the SERIAL depth is the
longest chain of non-overlapping calls rather than a wall clock that also contains pipe and database
time. Latency armed only AFTER the book was stocked; one fresh fixture per op.

```
W = 240 ms   E = 20000 ms   L = 4      drain = 20580 ms
op               depth   elapsed       row  row/W  calls
account              1       123       240    2.0  account
accounts             1       123       240    2.0  accounts
buy                  4       503       600    5.0  account,positions,quote,place
cancel               2       254     20000  166.7  orders,cancel
cancel-all           3       368     20000  166.7  orders,orders,cancel
close                5       613     20120  167.7  positions,account,positions,quote,place
close-all            9      1116     20480  170.7  positions×5,account×4,positions×4,quote×4,place×4
connectors           0         0         0    0.0
executions           1       123       240    2.0  executions
instruments          1       122       240    2.0  instruments
material-list        0         0         0    0.0
material-note        0         0         0    0.0     [not ok: 'text' is required — an arm still ran]
modify               2       252       480    4.0  orders,modify
order                1       126       240    2.0  orders
orders               1       122       240    2.0  orders
position             1       122       240    2.0  positions
positions            1       122       240    2.0  positions
quote                1       123       240    2.0  quote
schema               1       122       240    2.0  account
sell                 4       491       600    5.0  account,positions,quote,place
status               1       123       240    2.0  account
```

**No row understates its handler's connector chain.** `buy`/`sell` are declared `5W` and measured 4
calls on a warm process (the fifth, `instruments`, is the cold read the row is written for — the
round-9 probe `R9P4` re-run at this sha still counts `COLD buy : 5 calls`, `WARM buy : 4 calls`).
`modify` is declared `4W` and measured 2 on an installation with an account selected, which is what
the row's own text says.

### The derived drain against every measured chain, at three customised timeout sets (`R11P3`)

All 21 ops driven at each set; the assertion is `HandlerDrainTimeout ≥ measured` for every one.

```
(latency 120 ms, budget 20 s, settle 100 ms)  drain 20580 ms   worst measured: close-all 1107 ms   PASS 21/21
(latency 300 ms, budget 900 ms, settle 50 ms) drain  2150 ms   worst measured: buy      1215 ms   PASS 21/21
(latency  60 ms, budget 400 ms, settle 2000 ms) drain 2640 ms  worst measured: close-all  561 ms  PASS 21/21
```

### A close-all BIGGER than one wave, disposed with a placement genuinely in flight (`R11P12`)

Eight distinct symbols (two waves), uncancellable latency, and disposal is fired only once a leg is
observed `DISPATCHING` — asserted, so the landing is real rather than a guessed millisecond.

```
orders opened = 8, positions = 8
DISPATCHING while disposal starts = 1
disposal took 3661 ms, derived drain 7500 ms
DISPATCHING rows at return = 0 ()
needing reconciliation     = 0
```

**Mutants for this target: M7, M8, M9 — all bit** (table below).

---

## Target 2 — the vocabulary, exactly five per leg

### The whole cross product through the exported seam, counted from the ENUM (`R11P4`)

`Enum.GetValues<ExecutionState>()` (13 members) plus "no record", against `null` plus the three
`TransportOutcome` members — **52 combinations, every one a member of the five, and all five
produced** (so a mapping that refused everything would not pass either).

```
CREATED            (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
AWAITING_APPROVAL  (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
DISPATCHING        (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
ACKNOWLEDGED       NothingWritten -> not-sent             (none)/PossiblyWritten/ReplyReceived -> sent-still-working
WORKING            NothingWritten -> not-sent             … -> sent-still-working
PARTIALLY_FILLED   NothingWritten -> not-sent             … -> sent-still-working
CANCEL_PENDING     NothingWritten -> not-sent             … -> sent-still-working
FILLED             NothingWritten -> not-sent             … -> confirmed
CANCELLED          NothingWritten -> not-sent             … -> confirmed
REJECTED           NothingWritten -> not-sent             … -> rejected
UNKNOWN            (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
RECONCILING        (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
(none)             (none)/NothingWritten -> not-sent      PossiblyWritten/ReplyReceived -> sent-not-confirmed
states = 13, transports = 4, combinations = 52
```

`NothingWritten` overrules **every** arm (asserted for all 13 states + "no record"), and nothing else
overrules a definite record (asserted for CANCELLED / FILLED / REJECTED / WORKING against `null`,
`PossiblyWritten` and `ReplyReceived`). **This matches `docs/CONTRACTS.md`'s table row for row.**

### `nothing-to-do` is on the operation and on no leg

Measured on the real pipe, not read: a `close-all` over a book that had already been closed answers
`{"closed":0,"nothing_to_do":true,"attempted":0,"nothing_to_close":[],"not_closed":[],"not_sent":0,
"outcomes":[],"requests":[]}` — the word is a field of the operation, the leg array is empty, and no
leg carries it. A sweep WITH a leg answers `"nothing_to_do":false` beside per-leg `outcome` values
drawn from the five.

### The classifier is exhaustive in BOTH dimensions (`R11P5`)

```
invented state       (ExecutionState)9999    -> no leg outcome for execution state '9999'
invented transport, unresolved arm            -> no leg outcome for transport result '9999'
invented transport, definite arm              -> no leg outcome for transport result '9999'
```

The second and third are the ones a short-circuit in front of the state switch would have lost.
**Mutants M11 (catch-all restored) and M13 (`confirmed` back to `sent-and-confirmed`) both bit.**

---

## Target 3 — classification by transport result, and the null rule

### Every arm, both directions (mutants M1, M2, M3, M4, M5, M6 — all bit)

The builder's own claims reproduce exactly, on my harness:

- **M1** drop the attempt fallback → `Expected: PossiblyWritten / Actual: null`, RED 2
  (`A_frame_the_peer_read_whole…`, `An_attempted_mutation_that_reported_nothing…`).
- **M2** make the fallback beat an explicit report → RED 4, including
  `Expected: ReplyReceived / Actual: PossiblyWritten` and `Expected: NothingWritten / Actual:
  PossiblyWritten` — the measurement behind the builder's refusal of the brief's stated mechanism.
- **M3** delete the gate-cancellation arm → `Expected: NothingWritten / Actual: PossiblyWritten`.
- **M4** restore the outer catch's blanket `PossiblyWritten` → the same line.
- **M5** definite arms ignore the transport → `Every_arm_of_the_leg_classifier_consults_the_transport_result` RED.
- **M6** `null → sent-not-confirmed` (the brief's parenthesis, applied) → **RED 5**, and the five name
  the three legs the builder says arrive with a genuinely empty record:
  `A_leg_that_failed_before_the_wire_reads_not_sent_and_writes_no_record` (2 of 2 legs,
  `Expected: "not-sent" / Actual: "sent-not-confirmed"`),
  `A_close_leg_parked_for_approval_reads_not_sent_and_is_not_counted_as_attempted`,
  `A_five_order_sweep_carries_a_mix_of_outcomes_in_one_answer`, plus the two ledger tests.

### THE DEVIATION, judged

**(a) Can any of the three legs the builder names have touched the wire? No — all three measured at
the connector, not argued.** A counting decorator over the simulator, driven through the real pipe:

```
(a) nothing to close     {"closed":0,"nothing_to_do":true,"attempted":0,"outcomes":[]}
    connector mutations = 0   (calls: positions)
(b) resolution expires   two legs, both "not-sent", no record at all
    connector mutations = 0   (calls: orders,orders,orders)      orders still working = 2
(c) parked for approval  one leg "not-sent", state AWAITING_APPROVAL
    connector mutations = 0   (calls: positions,positions,account,positions,quote)
                              broker orders 1 -> 1
```

Not one of the three starts a mutating connector call, and (b) and (c) also leave the BROKER
unchanged — two orders still working, the order count unmoved. `not-sent` is the true word for each,
and the brief's `null → sent-not-confirmed` would flag all three (M6 above, RED 5).

**(b) Can any OTHER path yield a null transport after a mutation? YES — but not inside either
shipped connector.** Inside `AtasConnector`, `TransportLedger.Attempt()` is the first statement of
`Rpc` for every `Mutates(op)` — and `Mutates` covers all six mutating `BridgeOps` (`place`, `modify`,
`place-via-async-overload`, `cancel`, `cancel-all`, `close`), checked against the whole `BridgeOps`
class — so after an attempt `TransportRecord.Outcome` cannot be null by construction. Every mutation
the gateway can reach goes through `Rpc` (`PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`,
`CancelAllOrdersAsync` are one-line `Rpc` calls). `FakeConnector` marks the attempt in `Wire` and in
`PlaceOrderAsync`. The `AsyncLocal` flows: the only `Attach` in `src` is `GatewayPipeServer.cs:1080`,
inside `RunLegs`, and nothing on the dispatch path suppresses execution-context flow.

**What is NOT closed is the obligation itself** — see finding **F-2**: the property is opt-in per
connector, `ITradingConnector` does not state it, and a connector that does not opt in reports
`not-sent` for a cancel that reached the broker. Measured in `R11P7`.

---

## Target 4 — disposal never silent. IT IS SILENT, in an ordinary shutdown shape.

The invariant U2a shipped in round 10 is *"disposal MAY leave a request unsettled; it MAY NOT do it
silently"*. Two probes, identical except for one thing: whether the agent is still connected when the
app closes.

**Control — agent connected (`R11P11`): the sentinel works.**

```
handlers_did_not_finish = error
metadata = {"unfinished":0,"of":1,"unsettled":1,"requests":["p11-modify"],
            "drain_timeout_ms":3400,"settle_timeout_ms":200}
```

**The agent goes away first, then the app closes (`R11P10`): the sentinel does not run at all.**

```
modify ok=False err=the modify timed out
row state = DISPATCHING  reconcile = False
disposal returned in 3 ms (derived drain 3400 ms)
DISPATCHING rows at return = 1 (p10-modify)
needing reconciliation      = 0
handlers_did_not_finish     = (not logged)
metadata                    =
```

Both rows are produced the same legitimate way: a connector timeout, which safety rule 3 says must
PROPAGATE, escapes `TradingGateway.ModifyAsync`'s catch taxonomy and leaves the row `DISPATCHING`
while the handler answers the agent and lives on. Detail and the fix expectation are in finding
**F-1**.

The round-9 verifier's own probe re-run at this sha (`R9P9`) shows the half that IS closed:

```
derived drain = 700 ms      state when disposal starts = DISPATCHING
disposal returned after 706 ms          DISPATCHING rows = 1
handlers_did_not_finish = error         op_failed = error
```

— i.e. with a handler alive, the row is named at `error` (round-9 F-2's U2a half), and the row is
still unsettled and unflagged (the U2c-1 half, deferred by decision, unchanged). **The full derived
drain is waited before anything is cancelled** — 706 ms against 700 ms, and 3661 ms of a 7500 ms
drain in `R11P12` — so the deferral to U2c-1 C4 is written with a measurement on both sides.

**Mutant M10** (the sentinel counts handler tasks again) → RED 2:
`A_request_left_unsettled_when_disposal_returns_is_logged_by_name_at_error` and my `R11P11`, both
`Expected: "error" / Actual: null`.

---

## Target 5 — the Windows flake verdict

The builder rated `An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop` a TEST
premise defect and fixed the TEST. I read the test at this sha and the rating is supported by the
code it is about: `AtasConnector.WriteFrame` ends the connection when a write is cancelled in flight,
deliberately and with the reason stated in the source (a half-written frame on a `StreamWriter` every
caller shares), and the old assertion read `IsConnectedAsync` AFTER a teardown that cancelled a
chatter RPC's token. Both edits are in the test: the verdict is now read at the moment it is about
(`connectedAtTheVerdict`, before the teardown), and the chatter stops on a flag so the teardown can
only cancel the sleep between requests.

**The test still enters its branch, and its premise is asserted:**
`Assert.True(answered > answeredBefore, "no request was answered while the emergency waited, so this
is the wedged case and not the busy one")` — it is in the test, and it passes here, so the fixture is
the BUSY case and not the wedged one.

**It still bites a mutant — after two tries, and the first try is worth recording.**

- **M12** — delete the gate-expiry busy short-circuit
  (`if (Volatile.Read(ref _lastWriteProgressAt) > waitedFrom) return SendOutcome.Busy;`) → **GREEN,
  1 passed.** Not a defect: this fixture's peer READS everything, so the emergency never expires on
  the send gate; it expires on the REPLY. My mutant was in a branch this test does not enter.
- **M12b** — make the reply-wait emergency drop the peer instead of deferring the verdict to the
  grace (`JudgeTheConnectionWhenTheGraceRunsOut(…)` → `DropStalledPeer()`) → **RED 1**, which is the
  assertion whose timing was the flake. So the round-11 edit did not neuter it.

**NOT verified by me:** every Windows figure — the five green runs at this tip, the two 491s, the
116/116 and the eight hashes are the builder's, on a box that is not mine. I also cannot confirm the
fix is WHY the box is green, and neither can the builder; the record says so.

---

## Target 6 — regression

The rounds 7 + 9 probes, cherry-picked onto my branch and re-run at `120c739`:

```
R7P4  emergency elapsed = 2005 ms  (peer took 564432 bytes)  FrameIncomplete wording, connected after = False
R7P5  12 of 12 dropped at 10031–10062 ms; every caller answered in 2001–2003 ms; beats=2 in every phase
R7P2  emergency#1 2015 ms · emergency#2 2001 ms · ordinary buy 7410 ms · connected at t+10014 = False
      needing reconciliation = 0
R7P3  elapsed 2014 ms   RECORD = UNKNOWN reconcile=True   needing reconciliation = 1
R9P1  five-order sweep answered in 2004 ms, every order named, needing reconciliation = 0
R9P2  sweep that fits: 522 ms, cancelled = 5, attempted = 5, every leg CANCELLED
R9P3  three 1.9 s calls, IPC cancel-all answered in 2003 ms
R9P4  COLD buy : 5 calls -> account -> positions -> quote -> instruments -> place;  WARM buy : 4
R9P6  idle disposal took 0 ms (against a drain of 505 s)
R9P7  derived 00:05:05 · asked 7 s → 00:05:05 (clamped) · asked 900 s → 00:15:00 · settle 0 → 00:05:00
R9P8  the leg refused before the wire now reads not-sent (state=UNKNOWN, reconcile=True — the routed half)
R9P10 the real AtasConnector still refuses a leg whose turn came late
R9P11 awaiting-a-late-answer 1 → 0 on BOTH exits (peer-disconnects, connector-disposed)
R9P12 / R9P13  the close-all wave against the drain it is bounded by
```

**C1 is 2005 ms** (round 7: 2005; round 9: 2006). **One deadline per operation is 2003–2004 ms**
across three shapes. **The five-order sweep names every order.** **The 12-phase drop still lands at
≈10.04 s.** **The chain count is still five, in order.** **The clamp still works in both directions.**

Two of the r9-branch probes fail for reasons that are not the product:
`R9P2` asserts the word `sent-and-confirmed`, which round 10 renamed to `confirmed` — the rename is
the fix, and the failure is the probe being older than it; `R9P9` asserts the settlement half that is
deferred by decision to U2c-1 (its own output shows `handlers_did_not_finish = error`, i.e. the U2a
half it was written to refute is closed). `R7P1`/`R7P3` end in `BeginTransaction can only be called
when the connection is open` from the PROBE's own teardown (`AtasConnector.DisposeAsync` → `Drop` →
health log, into a database the probe closed first); their measurements are printed before it and are
what target 6 is about. **NOT verified: whether `R7P1`/`R7P3` ended the same way at `088c059`** — the
round-9 record quotes the same measurement lines but does not say.

**Test-name diff, taken myself** (`git grep -h -E 'public (async Task|void) ' <sha> -- 'tests/*.cs'`,
reduced to method names, sorted unique):

| | `088c059` | `c00fa08` | `120c739` |
|---|---|---|---|
| distinct method names | 367 | 379 | 384 |

**REMOVED `088c059` → `120c739`: 0** (`comm -23` empty). **REMOVED `c00fa08` → `120c739`: 0.**
Seventeen names added; they are the twelve of round 10 and the five of round 11, by name.

---

## Mutants

`cp` copy, an anchored python patch applied to the ORIGINAL, `touch`, solution rebuilt, targeted
filter run, restored from the copy and `touch`ed again — **never `git checkout --`**. The harness
prints `git status --short` after every restore and **every line of it was empty**; my own probes
were committed (`395ce39`) before the first mutant so the tree was clean to begin with.

| # | mutant | `file` | bit? | evidence |
|---|---|---|---|---|
| M1 | drop the attempt fallback (`Outcome` → null when nothing was reported) | `TransportLedger.cs` | **RED 2 of 5** | `A_frame_the_peer_read_whole…`, `An_attempted_mutation_that_reported_nothing…` — `Expected: PossiblyWritten / Actual: null` |
| M2 | the attempt fallback BEATS an explicit report | `TransportLedger.cs` | **RED 4 of 5** | `Expected: ReplyReceived / Actual: PossiblyWritten` · `Expected: NothingWritten / Actual: PossiblyWritten` ×3 |
| M3 | delete the gate-cancellation `NothingWritten` arm | `AtasConnector.cs` | **RED 1 of 4** | `A_cancellation_that_never_got_the_send_gate…` — `Expected: NothingWritten / Actual: PossiblyWritten` |
| M4 | restore the outer catch's blanket `PossiblyWritten` | `AtasConnector.cs` | **RED 1 of 4** | the same test, the same two lines |
| M5 | the definite arms ignore the transport (`TheAnswer` returns `answer`) | `GatewayPipeServer.cs` | **RED 1 of 41** | `Every_arm_of_the_leg_classifier_consults_the_transport_result` |
| M6 | **the brief's rule applied**: bare `null` → `sent-not-confirmed` | `GatewayPipeServer.cs` | **RED 5 of 41** | `A_leg_that_failed_before_the_wire…` (2 of 2 legs), `A_close_leg_parked_for_approval…`, `A_five_order_sweep_carries_a_mix…`, `An_attempted_mutation…`, `Every_arm_of_the_leg_classifier…` |
| M7 | remove the `schema` row | `GatewayPipeServer.cs` | **RED 3 of 11** | `the dispatcher handles schema and the drain table has no row for them` · the measured theory's `schema` row (`Sequence contains no matching element`) · my `R11P1` |
| M8 | add a row for `flatten-everything`, which nothing handles | `GatewayPipeServer.cs` | **RED 2 of 11** | `the drain table has rows for flatten-everything, which the dispatcher does not handle` · my `R11P1` |
| M9 | `CloseAllHandlerPath` back to `E + W` | `GatewayPipeServer.cs` | **RED 3 of 12** | `The_drain_covers_a_close_all_wave…` · `A_close_all_wave_that_disposal_lands_in…` · the measured `close-all` row |
| M10 | the disposal sentinel counts handler tasks again | `GatewayPipeServer.cs` | **RED 2 of 2** | `A_request_left_unsettled_when_disposal_returns…` and my `R11P11` — `Expected: "error" / Actual: null` |
| M11 | `Classify`'s catch-all restored | `GatewayPipeServer.cs` | **RED 2 of 2** | `An_execution_state_nothing_maps_throws…` and my `R11P5` — `Assert.Throws() Failure: No exception was thrown` |
| M12 | delete the gate-expiry busy short-circuit | `AtasConnector.cs` | **GREEN** | the round-7 test never enters that branch — see target 5; replaced by M12b |
| M12b | the reply-wait emergency drops the peer instead of deferring the verdict | `AtasConnector.cs` | **RED 1 of 1** | `An_emergency_a_busy_bridge_has_not_answered_yet_is_unknown_but_not_a_drop` |
| M13 | `confirmed` back to `sent-and-confirmed` | `GatewayPipeServer.cs` | **RED 2 of 2** | `The_per_leg_vocabulary_is_exactly_five_words…` and my `R11P4` |

**Thirteen distinct mutants, thirteen bit** (M12 counted as the one that named a branch the test does
not reach; M12b is its replacement and it bit).

---

## Findings

**The class the two MEDs share, named rather than enumerated (§9.10):** *a safety property was made
true of the two implementations in the tree, and left untrue of the contract.* Round 11's own words
for its fix are "closes the CLASS rather than the instance" — and both findings are places where the
class is still open one level up. **F-1:** disposal's report is conditioned on a handler task being
alive rather than on the state it is about, so the invariant holds for the configuration the tests
build and not for the one an operator ends up in. **F-2:** `not-sent` is an assurance that depends on
every `ITradingConnector` calling `TransportLedger.Attempt()`, and the interface that defines what a
connector owes says nothing about it. The structural fix in both cases is to move the guarantee to
where the obligation is stated — the state, and the interface — not to add another arm.

### MED

**F-1 — disposal returns SILENTLY with an unsettled `DISPATCHING` row whenever no connection handler
is alive.** `src/TradeAgent.Gateway/GatewayPipeServer.cs` — the sentinel that reports
`handlers_did_not_finish` lives inside `if (handlers.Length > 0)` (step 5 of `DisposeAsync`), and
`handlers` is `_handlers.Keys.ToArray()`: live CONNECTION handler tasks, each of which REMOVES itself
on completion (`_ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), …)` at `:398-399`), read
AFTER step 2 has already disposed every live connection.

Measured, on the Mac, through the real pipe, with the two probes differing in one thing only:

```
agent still connected   (R11P11)  handlers_did_not_finish = error
                                  metadata = {"unfinished":0,"of":1,"unsettled":1,
                                              "requests":["p11-modify"], …}
agent disconnected first (R11P10) DISPATCHING rows at return = 1 (p10-modify)
                                  needing reconciliation      = 0
                                  handlers_did_not_finish     = (not logged)
```

The row is produced legitimately and without any fault injection into the pipe server: a connector
`TimeoutException` — which safety rule 3 requires to propagate — escapes
`TradingGateway.ModifyAsync`'s `ConnectorRejectedException`/`ConnectorTransportException` catch, the
handler answers the agent and lives on, and the row stays `DISPATCHING` and unflagged. The shutdown
shape is ordinary: the agent CLI exits (or the pipe drops) and the operator then closes the app.
`ReconcileAsync` scans `NeedingReconciliation()` alone, so nothing will settle that row — and with
this guard nothing records that it existed either. **This is verifier round-9 F-2's harm, in a
narrower but entirely reachable configuration, surviving the round-10 fix that changed the COUNT
(`unfinished` → `unfinished || unsettled`) without changing the GUARD around it.**

**Fix expectation:** evaluate `gateway.Requests.Query("execution_state='DISPATCHING'")`
unconditionally — outside the `if (handlers.Length > 0)` block — and log `handlers_did_not_finish`
(with `unfinished`, `of`, `unsettled`, `requests`) whenever that query is non-empty, whatever the
handler count. A test that disconnects the agent before disposing the server, and asserts the row is
named, makes it bite; M10's existing test does not, because it keeps the connection open.

**F-2 — `not-sent` is an assurance that every connector must opt into, and the contract does not say
so.** `src/TradeAgent.ConnectorSdk/Contracts.cs` (`ITradingConnector`, which states
`WorstCaseOperationPath` and `EmergencyBudget` as connector obligations and says nothing about the
ledger) with `src/TradeAgent.Gateway/GatewayPipeServer.cs` (`Unresolved(null) → NotSent`) and
`docs/CONTRACTS.md` ("An empty transport record means no mutating call was ever attempted, and that
is producible only by work that never started one").

That last sentence is true of `AtasConnector` and `FakeConnector` — I checked every mutating path in
both, and `Mutates` covers all six mutating `BridgeOps` — and it is false of the contract. Measured
with a connector that implements the public interface, really cancels the order at the broker, and
does not touch the ledger (`R11P7`):

```
{"cancelled":0,"nothing_to_do":false,"attempted":0,
 "not_cancelled":[{"request_id":"op-…-cancelall-0","state":"UNKNOWN"}],"not_sent":1,
 "outcomes":[{"request_id":"op-…-cancelall-0","order":"FB-1","outcome":"not-sent","state":"UNKNOWN",
              "error":"the acknowledgement was lost after the cancel was sent"}]}
cancels that really reached the broker = 1
```

`not-sent`, `attempted: 0`, for a cancel that reached the broker — the exact report round 11 exists
to make impossible, produced by an absence of information. **And the evidence field is absent
exactly when the claim is the dangerous one:** `Leg.Describe()` emits `transport = Transport?.ToString()`
and the serializer omits null properties, so the leg above carries no `transport` key at all.

**Fix expectation:** (a) state the obligation on `ITradingConnector` where the other connector
obligations are stated, and in `docs/CONTRACTS.md`'s connector contract, so a connector author is
told; (b) better, and this is the structural half: mark the attempt where the GATEWAY dispatches a
mutation — immediately before `Connector.CancelOrderAsync` / `ModifyOrderAsync` / `PlaceOrderAsync`
in `TradingGateway` — so the fail-closed default holds for any connector; that is
`TradingGateway.cs`, so it is a routing, not a U2a edit; (c) serialize `transport` explicitly as
`null` rather than omitting the field, so the answer always carries its evidence.

### LOW

**L-1 — `AtasConnector._pending` leaks an entry when the CALLER cancels an emergency, and the leak is
invisible to the late-answer accounting.** `src/TradeAgent.Connectors.Atas/AtasConnector.cs` — the
reply wait's catch is filtered `when (!ct.IsCancellationRequested)`, so a caller's own cancellation
skips the `_pending.TryRemove(id, out _)` that every other exit performs. The builder flagged it and
did not measure it; measured here (`R11P9`, the real `AtasConnector` against a peer that reads
everything and answers everything but `cancel`):

```
pending at rest                    = 0
pending while in flight            = 1
pending after the caller cancelled = 1      late-answer slots (AwaitingLateAnswer) = 0
```

Bounded by the connection's lifetime (`Drop` faults everything pending), so it is not an unbounded
leak — but within one connection it grows by one per cancelled emergency, and because the id never
reaches `_abandoned`, a late answer for it is delivered to a `TaskCompletionSource` nobody awaits and
is counted in NEITHER `LateAnswers` NOR the late-answer event. Those two counters are what round 9's
F2 exists to keep honest. **Fix expectation:** a `catch (OperationCanceledException) when
(ct.IsCancellationRequested)` (or a `finally`) that removes the id, and — if the frame was written —
registers it in `_abandoned` so a late answer for it is counted like every other.

**L-2 — a table row bounds the CONNECTOR chain, not the handler, and the only margin for the rest is
added once and can be set to zero.** `src/TradeAgent.Gateway/GatewayPipeServer.cs`
(`DerivedDrainTimeout = HandlerPaths.Max(p => p.Path) + SettleAfterCancelTimeout`). Measured at
`W = 300 ms, E = 900 ms, S = 50 ms`: **`cancel-all` cost 917 ms against a row of 900 ms** (`R11P3`).
Harmless today — `close-all`'s row is the maximum and `S` sits on top of it — but `S` is added ONCE
rather than per row, it is `init`-settable to zero, and the invariant test
(`No_combination_of_settings_makes_the_drain_shorter_than_the_chain`) compares against the connector
chain, which is the quantity that is already covered. **Fix expectation:** say in `CONTRACTS.md` that
a row bounds the connector chain and that `S` is what covers the handler's own pipe and database
work, or floor `SettleAfterCancelTimeout` so that margin cannot be configured away.

**L-4 — the built-in simulator's deadline sentence is not op-aware, so a `not-sent` leg carries a
sentence saying the outcome is unknown.** `src/TradeAgent.Connectors.Fake/FakeConnector.cs` (`Wire`)
throws *"the operation deadline passed before the simulator answered; it is not known whether it
acted"* for reads and mutations alike, where the shipped `AtasConnector` distinguishes them
(`EmergencySentence`: *"'orders' could not be read, so the operation was not started. Nothing was
placed or cancelled."* — round 7's F-D fix). Measured (`R11P8b`), the two fields inside ONE leg:

```
{"outcome":"not-sent","error":"the operation deadline passed before the simulator answered;
                               it is not known whether it acted"}
```

The word is right and the sentence contradicts it — the F-1/F4 disagreement class, in the connector
the product ships for paper trading. Round 10's record already carries this as a disclosed gap
("Did not make the simulator's deadline message op-aware (carried from round 9)"); what is new here
is that the round-10 vocabulary makes the disagreement visible inside a single leg object.
**Fix expectation:** give `Wire` the op and the same two sentences `EmergencySentence` uses.

**L-3 — the coverage test's candidate set is `Core.Ops`'s constants, not the dispatcher's arms.**
`tests/…/GatewayPipeBackpressureTests.cs` (`Every_operation_the_dispatcher_handles_has_a_row_in_the_drain_table`)
reads the vocabulary off `typeof(Ops)`'s literals and asks the dispatcher about each. Every arm of
the `Handle` switch does use an `Ops` constant today — I checked all 21 — so the test is sound at
this sha. But a handler added with a literal op string would be invisible to it in exactly the way
`schema` was invisible to the hand list, and the round's own argument is that the omission and the
check must not come from the same place. **Fix expectation:** assert that the dispatch switch's
labels are `Ops` constants (or discover the set from the switch), so the premise is checked rather
than assumed.

---

## Gates

**Build gate, on the pristine tree, before any probe existed:**

```
dotnet build TradeAgent.sln --no-incremental
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.03                                                        (exit 0)
```

**Full suite, once, at `120c739`, on the Mac, with my probes excluded:**

```
dotnet test TradeAgent.sln --no-build --filter "FullyQualifiedName!~VerifyR"
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 1 s      - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 108, Skipped: 0, Total: 108, Duration: 3 s      - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 308, Skipped: 0, Total: 308, Duration: 6 m 35 s - TradeAgent.IntegrationTests.dll (net10.0)
EXIT=0
```

**491 green (75 / 108 / 308), 0 failed, 0 skipped — the builder's Mac count reproduces exactly**,
including the 6 m 35 s integration duration. One full-suite run used; one unspent. No sibling
`dotnet test` was running on this Mac during it.

**One caveat on the build gate, so nobody misreads a later run.** The rounds 7 + 9 probe files I
cherry-picked add two xUnit analyzer warnings to the solution build (`VerifyR7Probes.cs` xUnit2020,
`VerifyR9Probes.cs` xUnit2013 — both inherited from `u2a-verify-r9-probes`). **Zero warnings come
from `src/`**, and the 0-warning figure above was taken on the pristine tree before any probe
existed.

---

## NOT verified — by name

- **Every Windows figure.** The box is not mine. Round 11's 491 twice, 116/116 on the pipe classes,
  the eight SHA-256s, the `.cs` count and the five green runs of the round-7 test are the builder's
  claims, read and not reproduced. Everything in this record is macOS.
- **That the round-7 flake fix is why the box is green.** Neither I nor the builder can distinguish a
  removed race from an unlucky one that did not recur. What I did establish on the Mac is that the
  test still enters the BUSY branch with its premise asserted, and that it still goes RED when the
  emergency drops a bridge that was answering (M12b).
- **Whether `R7P1`/`R7P3` ended in the same probe-teardown exception at `088c059`.** The round-9
  record quotes the same measurement lines but does not say whether the probes passed.
- **`A_leg_refused_before_the_wire_reads_not_sent_even_though_its_record_is_unknown`'s end-to-end
  half** — a definite record state arriving together with `NothingWritten` through the real pipe. The
  builder says the combination cannot be produced in one leg and asserts it through `LegWordFor`; I
  did not find a way to produce it either, and I did not prove it is impossible.
- **What the reconciler does with a flagged CANCEL request** whose client order id never reached a
  broker order. Carried from round 9, still not measured; `ReconcileAsync` is `TradingGateway`'s.
- **The cross-handler queueing bound (N concurrent placements on `_dispatchGate` under one drain).**
  Named in the source and in `CONTRACTS.md` as NOT verified; I did not measure it either.
- **ATAS's real client-order-id limit and charset**, and whether a real ATAS synchronous call exceeds
  two seconds in practice. Both need the box.
- **F-2's harm on a REAL third-party connector.** I produced it with a connector I wrote to the public
  interface; no such connector ships today, and I am not claiming one does.
- **Whether `_pending`'s leak (L-1) can grow large in practice** — I measured one entry from one
  cancelled emergency and read the code path that clears everything on `Drop`. I did not run a
  long-lived connection with many cancellations.
- **265 s as an acceptable product cost.** The manager's ruling, recorded as taken; I re-measured only
  that an idle shutdown is 0 ms and that the number is derived rather than written down.

## What I did NOT do

- **I fixed nothing and pushed nothing.** `git diff 120c739 -- src/ docs/` is empty in my worktree;
  every change is test-only, on `u2a-verify-r11-probes` (twelve cherry-picked probe commits plus my
  own `395ce39`, and one later commit adding the three null-leg measurements). Nothing was merged,
  rebased, or moved; no git command was run in the main worktree.
- I ran the full solution suite **once**; one run is unspent.
- I did not reproduce the builders' RED states at `c00fa08` or `088c059`. I re-established each guard
  by mutation at `120c739` instead, which is the weaker order for the same evidence.
- **I did not use the Windows box**, `tools/win-*.sh`, `tools/probe`, ATAS, or the installed app, and I
  placed, modified or cancelled no real order.
- I did not read leg [3]'s output for this round (there is none yet); I read `codex-U2a-r9.txt` and
  `codex-U2a-r10.txt` as the two bounces' inputs.
- I did not re-measure rounds 4b–7's own evidence beyond the five regression probes in target 6.
- I mutated `src/` files thirteen times, each on a `cp` copy's original with a restore and a `touch`
  afterwards, and the tree was clean after every one. I did **not** mutate `TradingGateway.cs`,
  `DashboardView.cs`, `Stores.cs` or `GatewayTypes.cs`, and I did not edit any file under `src/`.
- I did not attempt to produce a definite record state together with `NothingWritten` end to end, and
  I did not measure the read handlers on the real `AtasConnector` (only on the simulator).
- I did not run the App or take a screenshot.

---

## Verdict

Rounds 10 and 11 do what they say on the surfaces the brief names, and they do it with teeth. The
drain table is genuinely exhaustive against the DISPATCHER — 21 rows, 21 handled ops, no row
missing and none stale, both directions, checked on my own enumeration — and the derived drain
covered every one of the 21 measured chains at three different timeout sets. No row understates its
handler's connector chain, the four operations round 11 added are present with the depth they claim,
and an eight-position `close-all` disposed with a placement genuinely in flight left nothing
unsettled. The vocabulary is exactly five words over all 52 combinations of every `ExecutionState`
and every transport result, all five are producible, `NothingWritten` overrules every arm including
the three definite ones, `nothing_to_do` appears only on the operation, and both switches throw on an
invented member. Thirteen mutants, thirteen bit. The regression figures reproduce: C1 at 2005 ms, one
deadline per operation at 2003–2004 ms across three shapes, five calls in a cold placement in the
order claimed, the 12-phase drop at ≈10.04 s, the clamp both ways, 491 green.

**The deviation is judged in the builder's favour, with measurements on both halves.** None of the
three legs that arrive with an empty transport record starts a mutating connector call — measured at
the connector for all three, and (b) and (c) leave the broker itself unchanged — and applying the
brief's rule instead turns five true tests red. `null → not-sent` is the correct mapping for the
connectors in this tree.

What does not hold is the *contract* around the two guarantees, and both failures are the same shape:
a property made true of the implementations and left untrue of the thing that defines the obligation.
Disposal's promise not to return silently is conditioned on a connection handler happening to be
alive, and in a perfectly ordinary shutdown — the agent exits, the operator then closes the app — it
returns in 3 milliseconds with a `DISPATCHING` row nobody will settle, nothing flagged and nothing
logged. And `not-sent`, the one word in the set that is an assurance, is an obligation on every
`ITradingConnector` that the interface does not state: a connector written to the public contract that
cancels an order at the broker and does not call `TransportLedger` reports `not-sent` with
`attempted: 0`, with the `transport` evidence field omitted from the answer entirely. Neither is a
missing arm; both are a guarantee that has not yet been moved to where the obligation lives.

**VERDICT: FAIL — 0H/2M/4L**
