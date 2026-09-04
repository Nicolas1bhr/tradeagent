# Frozen contracts

The interfaces that let the layers be built and changed independently. Small on purpose. When one
proves wrong, change it and repair the consumers — do not add a second way to do the same thing.

## `ITradingConnector` — `src/TradeAgent.ConnectorSdk/Contracts.cs`

Semantic trading operations, no platform detail. Reads, mutations, and an event surface.

Two things carry the safety of the whole system:

**`ConnectorCapabilities`** — what a backend can actually promise. `ReconciliationProvable` is
`SupportsClientOrderId && SupportsOrderHistory`. The gateway reads it to decide how much autonomy is
safe, and refuses `LIVE_AUTONOMOUS` when it is false. A connector must report this truthfully;
overstating it is the most dangerous lie a connector can tell.

**The exception distinction** —

| Throw | Meaning | Gateway's response |
|---|---|---|
| `ConnectorRejectedException` | The broker definitively refused | `REJECTED`, final, nothing to reconcile |
| `ConnectorTransportException` | We do not know what happened | `UNKNOWN`, trading pauses, reconcile, **never resubmit** |

Any other exception is treated as indefinite. Getting these backwards is the one mistake that can
produce a live position nobody asked for.

**The transport ledger — an obligation on every mutating call, and it is not a method on the
interface.** `PlaceOrderAsync`, `ModifyOrderAsync`, `CancelOrderAsync`, `CancelAllOrdersAsync` and
`ClosePositionAsync` must each call `TransportLedger.Attempt()` the moment they START — before
anything can go wrong — and `TransportLedger.Record(...)` at every site that KNOWS where the frame
got to. Both are no-ops outside a leg, so a connector may call them unconditionally. **Reads must
not record**: a leg is a read to find its target and then the thing it came to do, and recording the
read would report a reply for a mutation that never left.

Why it carries safety: one of the five per-leg words, `not-sent`, is an ASSURANCE, and an empty
transport record is what produces it. A connector that mutates and never marks the attempt turns
"nothing was recorded" from *no mutation was started* into *nobody wrote it down* — measured on a
connector written to this interface that really cancelled at the broker: `not-sent`, `attempted: 0`.
**A connector that ignores this is safe and imprecise, never dangerous**: the gateway does not take
silence for an assurance — a leg whose own record proves a mutating step was dispatched is
`sent-not-confirmed` whatever the ledger says. Marking the attempt is what buys the precision back,
and `NothingWritten` — which only a connector can prove — is the one report allowed to overrule the
record.

## `IAgentRuntime` — `src/TradeAgent.AgentRuntime/IAgentRuntime.cs`

Detect · Install · Update · GetVersion · BeginAuthentication · GetAuthenticationState ·
CreateEnvironment · Start · Stop · Restart · ExecuteTask · GetHealth · Capabilities.

`CliAgentRuntime` implements all of it from a `RuntimeManifest`, so OpenCode, Codex and anything later
are the same code with different data. Runtime-specific awkwardness stays inside the manifest.

## Gateway IPC — `src/TradeAgent.Core/Protocol.cs`

Newline-delimited JSON over a named pipe, one object per line, 1 MiB cap.

```jsonc
// request
{"v":1,"id":"...","op":"buy","token":"...","session":"agent-...","request_id":"...","args":{...}}
// response
{"v":1,"id":"...","ok":true,"data":{...}}
{"v":1,"id":"...","ok":false,"error":{"code":"...","message":"...","user_message":"...","repair":"...","auto_repairable":false}}
```

- The first frame **must** be `hello` carrying the token. Anything else closes the connection.
- Reads and order operations only. Operator authority is not on this channel.
- `material-list` and `material-note` carry the workspace ledger. `material-note` is the only write on
  this channel that is not an order, and it writes to a table of **claims** — it cannot alter what the
  scanner observed, so it is not a route to editing the record of the agent's own work. A note whose
  hash matches nothing in the ledger is refused rather than stored.
- Every mutating op takes `request_id`. Reusing one returns the original outcome and dispatches nothing.
  **If it is omitted, the frame's own `id` is used in its place** — so the restriction below is
  enforced on whichever of the two is in effect, not on the optional field. (Until 2026-09-03 it was
  enforced on `request_id` alone, and omitting that field put an unrestricted wire string on a broker
  order: measured at 203 characters, with `#`, `/` and a space in it.)
- **The effective request id is restricted** (release-note fact, narrowed 2026-09-03): letters,
  digits and `-` only, 1-61 characters, and it may not begin with `op-`. It is carried onto the
  broker order as the client order id `TA-{request_id}`, which must therefore fit 64 characters;
  safety rule 1 needs that field to round-trip, so the shape is kept to what a broker is least
  likely to refuse. `op-` is reserved for the ids the gateway mints itself for `cancel-all` and
  `close-all` legs (`op-{nonce}-{intent}-{n}`), so an agent's id can never collide with one — a
  guarantee that holds only because the frame `id` is checked too. An effective id outside this is
  refused with `INVALID_REQUEST` rather than truncated.
  **The 64-character ceiling is a conservative guess: ATAS's real client-order-id limit is NOT
  VERIFIED and can only be settled on the Windows box.**
- `trade` prints `request-id: <id>` on stderr *before* sending, and includes `request_id` in `--json`.
  If a command dies without a reply, re-run it with the same `--request-id`; never with a new one.
- `trade schema --json` serves this contract at runtime, so an agent discovers capabilities instead of
  relying on a prompt that drifts.

## Bridge protocol — `src/TradeAgent.Connectors.Atas/BridgeProtocol.cs`

Compiled into both halves so the shapes cannot drift. TradeAgent hosts; the bridge dials in, sends
`hello` with its capabilities, then heartbeats. One payload field, `data`, in both directions. A
`bridge_protocol_version` mismatch is refused outright rather than half-trusted. `rejected: true` on a
failure frame is what marks a refusal definite.

## Bridge deadlines, and what a slow bridge is told

Four bounds, and they answer different questions. Changing any of them changes a number a test
asserts, rather than silently invalidating this section.

- **`AtasConnector.WriteTimeout` (10 s) — a PROGRESS budget, not a total.** It is spent per 1 KiB
  chunk and reset by every chunk the peer accepts, so a slow-but-moving bridge is never dropped for
  being slow.
- **The progress threshold is the chunk size, and this is the residual it leaves.** Progress is
  recognised only when a WHOLE 1 KiB chunk has been accepted, so a peer moving slower than one chunk
  per emergency window — below roughly **512 bytes/second** — is indistinguishable from one that has
  stopped, and is reported as stalled. The boundary cannot be removed, only moved: at the previous
  8 KiB chunk it sat at 4 KiB/s, where an ordinary struggling reader could fall on the wrong side of
  it (measured: a peer taking 2 KiB during the window was called "not responding"). It is documented
  rather than fixed because a peer that slow is, for a two-second emergency, not usefully different
  from a dead one.
- **`AtasConnector.FrameTimeout` (30 s) — the whole-frame ceiling**, so one frame is bounded in total
  and not merely per chunk. Against the 1 MiB frame cap it is a floor of about 34 KiB/s.
- **`AtasConnector.EmergencyDeadline` (2 s) — the CALLER's total** for `cancel`, `cancel-all` and
  `close`, covering the send gate, the write and the reply together. On expiry the caller is told the
  operation is NOT confirmed and to check ATAS, and the record is UNKNOWN. `place` and `modify` never
  get it.
- **Whether the CONNECTION is dropped is a different question on a different clock.** The bridge is
  dropped only when it has answered nothing within the ordinary RPC deadline (10 s) — not when one
  emergency went unanswered for two. A bridge handles frames one at a time, so silence while it works
  on our own frame is what a busy bridge looks like as well as a dead one. An answer that arrives
  after its caller gave up is delivered rather than discarded.

`WorstCaseOrderPath` is `WriteTimeout + FrameTimeout + rpcTimeout` and the shutdown drain is derived
from it, so a connector built with different deadlines moves the drain with it.

## The shutdown drain, and the handler table it is the maximum over

`GatewayPipeServer.DisposeAsync` closes every connection, then WAITS for the handlers already
running, because a handler cut off mid-dispatch leaves an order that may have reached the broker
recorded `DISPATCHING` for ever. How long it waits is **derived**, never written down — and it is
derived from **every** handler rather than from one, because three separate rounds of this unit
found a drain that was correct for the handler somebody had in mind and short for another.

Three terms, all read off the live connector (`GatewayPipeServer.HandlerPaths`):

| term | what it is |
|---|---|
| **W** | `ITradingConnector.WorstCaseOperationPath` — ONE ordinary call, every bounded wait in it added up. `50 s` at shipped ATAS values (`10 + 30 + 10`). |
| **E** | `ITradingConnector.EmergencyBudget` — the WHOLE risk-reducing part of one operation, however many calls it decomposes into. `2 s` at shipped values. |
| **L** | `GatewayPipeServer.MaxLegsInFlight` — how many legs of a sweep are in the air at once. `4`. |
| **S** | `GatewayPipeServer.SettleAfterCancelTimeout` — the write-back margin, added ONCE on top of the maximum. `5 s`. |
| **H** | `GatewayPipeServer.HandlerOverhead` — what a HANDLER costs beyond its connector calls, added ONCE. `1 s`. |

| handler | serial depth | why that is the chain |
|---|---|---|
| `status` `schema` `accounts` `account` `instruments` `quote` | **2W** | an account resolution, then the read — `schema` builds the same status `status` does |
| `positions` `position` `orders` `order` `executions` | **2W** | the account, then the read |
| `connectors` `material-list` `material-note` | **0** | no connector call at all — in the table anyway, because a handler that is ABSENT is one nobody notices growing a call |
| `buy` `sell` | **5W** | a cold placement: account → positions → quote → instruments → place |
| `modify` | **4W** | the account, the orders read that resolves the target, the account again, the modify |
| `cancel` | **E** | resolve the target, then cancel — every call risk-reducing, so the whole handler is the one budget |
| `cancel-all` | **E** | the orders read and every leg, all inside the one budget |
| `close` | **E + W** | the prefix inside the budget, then ONE ordinary placement — `Place` is excluded from the emergency deadline on purpose |
| `close-all` | **E + L·W** | the prefix inside the budget, then one WAVE of placements — every leg ends in a `Place` and `TradingGateway._dispatchGate` is a mutex, so a wave's placements queue rather than overlap |

```
drain = max(that table) + H + S
```

**A ROW BOUNDS THE CONNECTOR CHAIN, NOT THE HANDLER**, and the two extra terms are different
quantities that must not be confused. Every row above is arithmetic over `W` and `E`, which are the
connector's own deadlines — so the table covers the CALLS. A handler also reads a frame off the pipe
and parses it, writes its request record, settles it and writes a reply, and no connector deadline
describes any of that: `H` is that work, and it is a constant because it is a pipe read, a JSON
parse and two or three local SQLite writes — it does not scale with anything a connector reports.
`S` is a different promise: how long a handler gets AFTER its token is cancelled to write down what
it already knows. `S` was the only thing standing in for `H`, and it is settable to zero, at which
point the drain equalled the longest row exactly and every millisecond of handler was outside it —
measured at `W = 300 ms`, `E = 900 ms`: `cancel-all` cost 917 ms against its 900 ms row. They are
separate now, so configuring the write-back window cannot configure away the bound.

**The table is checked against the DISPATCHER, not against a list.** Four handled operations were
missing from it — `schema`, `connectors`, `material-list` and `material-note` — and `schema` makes a
connector-backed call, so a hand-written check could not have found them: the omission and the check
came from the same memory. Every operation in the protocol vocabulary is now driven over the real
pipe, and an op the dispatcher has no arm for answers `unknown operation '…'`; everything else must
have a row, and every row must name an operation the dispatcher handles.

At shipped ATAS values that is `max(5×50, 2 + 4×50) + 1 + 5 = 256 s`, and disposal's ceiling is
`5 + 256 + 5 = 266 s` — paid ONLY while a request is genuinely in flight, since an idle handler is
freed when its pipe closes, before this wait. **The reason `close-all` costs one wave and not the
whole book:** `RunLegs` checks the operation deadline before issuing each leg, so once `E` is gone
every remaining leg is reported `not-sent` instead of being issued — at the instant the last wave is
issued, less than `E` has elapsed, and that wave costs at most `L·W` more.

**An explicit `HandlerDrainTimeout` may only LENGTHEN this.** A caller naming a longer value means it
and gets it; one naming a shorter value is asking for an order to be abandoned at shutdown, which is
not theirs to ask for.

**What the bound does NOT cover, stated rather than left to be found:** it is the bound for ONE
handler. `_dispatchGate` is a mutex, so N placements in flight together queue on each other and cost
N chains while disposal waits for all of them under this one bound. **NOT verified: what N can be in
practice.**

## The per-leg vocabulary of a sweep

`cancel-all` and `close-all` answer with one `outcomes` entry per order. **The set is exactly five
words**, each 1:1 with what is known about that leg, and the word is derived from the CONNECTOR's
transport result — what is known about where the frame got to — never from the record state alone.

| word | what happened | record | transport |
|---|---|---|---|
| `confirmed` | the broker said this leg's own intent is done | `CANCELLED` / `FILLED` | anything but `NothingWritten` |
| `rejected` | a DEFINITE refusal. Nothing is working from this leg and there is nothing to reconcile | `REJECTED` | anything but `NothingWritten` |
| `sent-still-working` | sent, answered, and the order is still out there | `WORKING` / `ACKNOWLEDGED` / `PARTIALLY_FILLED` / `CANCEL_PENDING` | anything but `NothingWritten` |
| `sent-not-confirmed` | it reached the wire, or may have, and the outcome is not known | `UNKNOWN` + `needs_reconciliation`, or still `DISPATCHING` / `RECONCILING` | `PossiblyWritten` / `ReplyReceived`, **or nothing reported at all** |
| `not-sent` | it never reached the wire — nothing is at the broker from this leg | no record, or `CREATED` / `AWAITING_APPROVAL` — **or any record at all** when the transport proves it | `NothingWritten`, or nothing reported on a record that never reached the wire |

**Every arm consults the transport, including the three that read a definite answer.**
`NothingWritten` is a PROOF that this leg's frame never left the process, and a record can be in a
definite state for a reason that has nothing to do with this leg — the connector's event stream
updates request records, so a sweep leg can find one already settled by something else. It is
therefore the one report allowed to overrule the record. Everything else defers to the record where
the record can answer, including a leg with no transport of its own: an idempotent replay dispatches
nothing and is `confirmed`, not `not-sent`.

**An empty transport record is not by itself an assurance, and the RECORD's own state is what says
whether it may be read as one.** A connector that marks its attempts makes an empty record mean "no
mutating call was ever started" — that is the obligation stated above, both shipped connectors keep
it, and a mutation that STARTED and reported nothing is `PossiblyWritten`, so an unenumerated exit
cannot become an assurance. **A connector that does not is not allowed to produce one either.**
`TradingGateway` writes `DISPATCHING` immediately before a mutating connector call and `UNKNOWN` and
`RECONCILING` are reachable only through it, so a leg holding one of those three states is the pipe
server's OWN proof that a mutating step of this leg was dispatched: with nothing reported it is
`sent-not-confirmed`, whatever the connector did or did not write down. `not-sent` from an empty
record therefore needs a record that never got to the wire — no record at all, `CREATED`, or
`AWAITING_APPROVAL` — which is what the three legs that legitimately produce it have: a target
resolution that failed before its record existed, a leg parked for approval, and a `close-all` symbol
with nothing left to close.

**The `transport` field is always present, and it is `null` when the connector reported nothing.**
It is the EVIDENCE for the word beside it, and it used to be omitted by the serializer in exactly the
case where the word rests on the pipe server's own knowledge rather than on a connector's report.

`nothing-to-do` is **not** a per-leg word. It is a whole-operation result: `nothing_to_do` is true on
a sweep that found zero targets. A `close-all` leg whose symbol turns out to have nothing to close is
`not-sent` and is named in `nothing_to_close`.

The distinction the words exist for is `not-sent` versus `sent-not-confirmed`. `sent-not-confirmed`
sets `needs_reconciliation`, which **pauses all further execution** (`TRADING_PAUSED_UNRECONCILED`) —
including the retry the message itself advises. Claiming it for a leg the connector PROVED it never
sent is therefore not a wording problem, and it is why the word comes from the transport result.
`DISPATCHING` and `RECONCILING` are in that row because the word is about the WIRE: a mutation
cancelled by disposal stays `DISPATCHING` and unflagged (`TradingGateway`'s half, routed to U2c-1),
and the leg carries the connector's own `transport` beside the word rather than the pipe server
editing a row it does not own.

## Order state machine — `src/TradeAgent.Core/OrderStateMachine.cs`

```
CREATED ──► AWAITING_APPROVAL ──► DISPATCHING ──► ACKNOWLEDGED ──► WORKING ──► PARTIALLY_FILLED ──► FILLED
                                       │                                              │
                                       ├──► REJECTED (terminal)                        └──► CANCEL_PENDING ──► CANCELLED
                                       │
                                       └──► UNKNOWN ──► RECONCILING ──► (whatever the broker actually says)
```

The rule the file exists to enforce: **nothing re-enters `DISPATCHING` from `UNKNOWN`.** That is the
transition a naive retry makes, and it is how a live account gets double-filled. `UNKNOWN` leaves only
through `RECONCILING`.

Ownership: whoever holds a request writes its outcome. The dispatcher owns `CREATED`,
`AWAITING_APPROVAL` and `DISPATCHING`; the reconciler owns `RECONCILING`; the connector's event stream
may only update requests neither of them currently holds. Both races this rule prevents were real
bugs found during the build.

**An approval is a dispatch decision, authorized at the moment it is made.** In `LIVE_CONFIRM` an
agent order is parked as `AWAITING_APPROVAL` after passing every gate and refused to the agent with
`APPROVAL_REQUIRED`. When a person presses Approve, the gateway makes the decision again from the
start, in this order:

1. the request must exist and still be `AWAITING_APPROVAL` — else `INVALID_REQUEST`;
2. its age must be inside the approval time-to-live — else `APPROVAL_EXPIRED`, below;
3. the mode must still be `LIVE_CONFIRM`, the mode it was proposed under — else
   `MODE_FORBIDS_EXECUTION`. `PAPER` and `LIVE_AUTONOMOUS` do allow the AI to trade; they are simply
   not the mode this order was parked under, and neither auto-dispatches it;
4. the authorization chain — kill switch, live activation, autonomy-needs-provable-state, an account
   chosen, no unreconciled work, a trustable health chain — run with **the AI's session, never the
   operator's**. The person is pressing the button but the ORDER is the AI's proposal, which is what
   makes the kill switch refuse an approval with `AI_TRADING_STOPPED`: re-enable, then approve, two
   deliberate acts;
5. the platform must still be the connector the record was parked on — else `ACCOUNT_NOT_FOUND`. An
   account id is unique only *within* a platform, and switching platforms builds a new gateway over
   the same database, so a parked request outlives the platform it was proposed for; comparing
   account ids alone would send a simulator proposal to a real broker exposing the same id;
6. the chosen account must be the one the record names — else `ACCOUNT_NOT_FOUND`, since the dispatch
   goes to the account on the record;
7. every risk limit: allowlist, quantity, paper-vs-real, rate limit, open positions, quote freshness
   for an order without its own price, and order value multiplied by contract size.

Only then does it dispatch. A refusal at any step leaves the record `AWAITING_APPROVAL` for a person
to decline deliberately — except step 2. A request as old as or older than the approval time-to-live
(`GatewayOptions.ApprovalTtl`, 15 minutes by default) is refused with `APPROVAL_EXPIRED` and declined
through the state machine: `AWAITING_APPROVAL → CANCELLED`, `last_error` saying so. The bound is
inclusive, so `ApprovalTtl = 0` expires everything; and an age that cannot be trusted — a record
timestamped in the future, after a clock step or a restore — expires on the same rule rather than
staying approvable forever. Age is judged before every gate that follows it, so a request that is
both expired and refusable for some other reason is declined rather than left parked behind a refusal
the person could lift and then walk straight back into. **Nothing sweeps:** expiry is evaluated only
when a person presses Approve, so a request can be past the limit and still listed as awaiting
approval — which is why the Dashboard row states the time it stops being approvable. An agent
replaying that request id gets whatever the record now says, and proposes again with a new id if it
comes back `CANCELLED` and it still wants the order.

## Health and errors

`HealthState`: `UNKNOWN · STARTING · READY · DEGRADED · FAILED · PAUSED`, per component.
`HealthRegistry.ExecutionTrustable` requires Gateway, Trading connection, Account and Execution
capability all `READY`; anything else revokes trading rather than guessing.

`ErrorCode` → `ErrorInfo` gives every failure a technical detail, a plain-language explanation, a
suggested repair, and whether TradeAgent can fix it itself. A unit test asserts every code has all
four, so a raw exception can never become the user's primary guidance.

## Onboarding

`OnboardingStep` in order, progress in the database. `Current()` is the first unfinished step, which is
what makes setup resumable after a crash, an ATAS restart or a Windows restart. Steps the software can
verify never ask the user to confirm they did them.
