# Frozen contracts

The interfaces that let the layers be built and changed independently. Small on purpose. When one
proves wrong, change it and repair the consumers — do not add a second way to do the same thing.

## `ITradingConnector` — `src/TradeAgent.ConnectorSdk/Contracts.cs`

Semantic trading operations, no platform detail. Reads, mutations, and an event surface.

Two things carry the safety of the whole system:

`OrderInfo.Quantity` is deliberately **not** specified as the order's total or its remaining size:
connectors differ, and nothing in TradeAgent depends on the distinction. What depends on it — deciding
whether a modification was applied — treats a quantity that does not match the request as *unknown*
rather than as a refusal, so the ambiguity cannot become a false outcome.

**`ConnectorCapabilities`** — what a backend can actually promise. `ReconciliationProvable` is
`SupportsClientOrderId && SupportsOrderHistory`. The gateway reads it to decide how much autonomy is
safe, and refuses `LIVE_AUTONOMOUS` when it is false. A connector must report this truthfully;
overstating it is the most dangerous lie a connector can tell.

**The residual under `SupportsClientOrderId`, and it is a write permission rather than a bug.** The
capability is `ClientOrderIdProof.ProvesRoundTrip() && AdapterTeardown.Trouble is null`, and `Trouble`
is non-null whenever this run cannot READ the sidecar set beside `coid-witness.json` — deliberately,
because a run that cannot tell whether a durability gap is open must not claim rule 1 is proven. The
consequence is that **any process that can write in `Paths.BridgeDir` can drop the capability with one
unreadable file**: a `coid-witness.errors.log*` name it holds open, or one whose ACL denies this
process, and the bridge falls back from `LIVE_AUTONOMOUS` to asking. It fails in the safe direction and
it is not fixable by classifying harder — the alternative is reading an unreadable file as an empty one,
which is the conflation U14 exists to end. `Paths.BridgeDir` is under `%LOCALAPPDATA%\TradeAgent`, so
the party that can do this is already the logged-in user or something running as them; it is recorded
here as the price of the fail-closed reading, not as a defence.

**The exception distinction** —

| Throw | Meaning | Gateway's response |
|---|---|---|
| `ConnectorRejectedException` | The broker definitively refused | `REJECTED`, final, nothing to reconcile |
| `ConnectorTransportException` | We do not know what happened | `UNKNOWN`, trading pauses, reconcile, **never resubmit** |
| anything else | We do not know what happened | identical to the row above |

Any other exception is treated as indefinite — literally, on all three dispatch paths (place, cancel,
modify): after the write-ahead, `ConnectorRejectedException` is the only exception that may settle a
record without flagging it, and every other one settles `UNKNOWN`, pauses execution and writes an
engineering row naming the exception type. Getting these backwards is the one mistake that can
produce a live position nobody asked for.

**What the connector answers is mapped, not guessed at.** The state on the returned `OrderInfo` is
recorded as itself for `FILLED`, `PARTIALLY_FILLED`, `WORKING`, `ACKNOWLEDGED`, `REJECTED` and
`CANCELLED`. Every other value — `UNKNOWN` first among them, and `CANCEL_PENDING`, which the state
table will not let a dispatch claim — is recorded as `UNKNOWN` and reconciled. A modify is only
recorded as applied when the returned order actually carries every value the modification asked for
and is still in a state where a working modification means anything; otherwise it is `UNKNOWN` too.
A connector that answers with a state outside this list is not wrong to do so, but it will pause
trading, which is the safe direction.

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
**`CancelAllOrdersAsync` is no longer sent by anything in TradeAgent.** The gateway's emergency
cancel-all is per-order cancels of the set it captured (see below), and the agent's `cancel-all` was
already per-order legs. **Reviewed 2026-09-05 and it stays**: it is the only call through which the
ATAS bridge's send-gate, whole-frame and reply deadlines are measured (17 tests), and those
measurements are about the transport rather than about cancelling everything — removing the method
would delete the harness, not the risk. Nothing calls it on the money path, nothing should start, and
the rule that would make a caller safe if one ever did is the same as for every other mutation: the
dispatcher marks the attempt.

**Why a placement carries an INTENT, and it is the same shape of obligation.** `PlaceOrderCommand`
has an `Intent` — `Open` or `Close` — and a connector that gives risk-reducing work a shorter
deadline must read it. A close is implemented as an offsetting placement, so the operation the
connector is about to send is a `place` like any other: classifying urgency by the op alone kept
every close on the ordinary bound, and an agent `close` ran its whole read prefix inside the
two-second emergency budget and was then served ten seconds for the order it was hurrying to make.
The side and the quantity cannot supply the answer — `Sell 2 ES` flattens a long and opens a short,
and the difference is a position the connector is not told about — so the intent travels with the
command from where the operation was decomposed. `Open` is the default and is what an unmarked
placement gets, which is where every placement was before; **a connector that ignores the field is
safe and slow**, in the same way one that ignores the ledger is safe and imprecise. `place` and
`modify` are still excluded from an ambient `RiskReducingScope`: an order that can OPEN exposure has
no claim on an emergency deadline whatever it is nested inside, and only the command may say
otherwise.

**A connector that ignores this is safe and imprecise, never dangerous**: the gateway does not take
silence for an assurance — a leg whose own record proves a mutating step was dispatched is
`sent-not-confirmed` whatever the ledger says. Marking the attempt is what buys the precision back,
and `NothingWritten` — which only a connector can prove — is the one report allowed to overrule the
record.

**And the gateway marks the attempt itself, at every one of its own dispatch sites**
(`TransportLedger.MarkDispatch`, called immediately before each mutating connector call in
`TradingGateway`). The obligation above is a contract, and a contract a third party can get wrong is
not a guarantee: an empty record is what produces `not-sent`, so a connector that mutates and never
marks turned an absence of information into an assurance. A dispatched mutation can now never leave
an empty record, whatever the connector does — an unreported exit is `PossiblyWritten`, and the leg
carries that as its evidence instead of a null. The marker reuses the record a sweep leg already
carries rather than attaching a second, which would hide the connector's own reports from the leg
holding it; where there is no leg — a single `cancel`, a `buy`, an operator's press — it attaches
one, and that is what lets an ordinary dispatch read a proven `NothingWritten` back at all. **The
three legs that legitimately answer `not-sent` are unaffected**, because none of them reaches a
dispatch site: a target resolution that failed before its record existed, a leg parked for approval,
and a `close-all` symbol with nothing left to close.

## `IAgentRuntime` — `src/TradeAgent.AgentRuntime/IAgentRuntime.cs`

Detect · Install · Update · GetVersion · BeginAuthentication · GetAuthenticationState ·
CreateEnvironment · Start · Stop · Restart · ExecuteTask · GetHealth · Capabilities.

`CliAgentRuntime` implements all of it from a `RuntimeManifest`, so OpenCode, Codex and anything later
are the same code with different data. Runtime-specific awkwardness stays inside the manifest.

## Gateway IPC — `src/TradeAgent.Core/Protocol.cs`

Newline-delimited JSON over a named pipe, one object per line, 1 MiB cap **counted in bytes on the
wire**. A frame past it is not answered: the peer is dropped, the way a peer that stops reading is,
because finding the end of an unbounded frame in order to reply to it is the thing the cap forbids.
The count was `StringBuilder.Length` — UTF-16 chars after decoding — until 2026-09-05, so a legal
frame of CJK text was accepted at 2.6x the stated cap and held whole in the server: measured at
2,700,096 bytes. It is the only bound on what a peer can make this server hold, and the read runs
BEFORE the `hello` check, so the peer that spends it need not have authenticated (finding 10).

```jsonc
// request
{"v":1,"id":"...","op":"buy","token":"...","session":"agent-...","request_id":"...","args":{...}}
// response
{"v":1,"id":"...","ok":true,"data":{...}}
{"v":1,"id":"...","ok":false,"error":{"code":"...","message":"...","user_message":"...","repair":"...","auto_repairable":false}}
```

- The first frame **must** be `hello` carrying the token. Anything else closes the connection.
- **`v` is settled before the token is read.** A `hello` whose `v` is not `Versions.ProtocolVersion`
  is refused with `INCOMPATIBLE_PROTOCOL`, no session comes of it, and one `protocol_rejected`
  engineering line records it; the connection stays open so the peer may say hello again at the right
  version. Before the credential deliberately: a token is a value read out of a frame whose shape both
  ends have agreed, and answering a version mismatch with `IPC_UNAUTHENTICATED` sends whoever owns the
  peer hunting a permission problem that does not exist. Until 2026-09-05 the mismatch was only
  *reported* — the reply carried `compatible: false` and the connection authenticated anyway — so a
  peer built against another protocol traded over this channel on the strength of a field nothing was
  obliged to read (Codex F8; measured: `a hello naming protocol 2 was accepted`).
- **`v` is REQUIRED on every frame, and an omitted one is refused rather than assumed.** `IpcRequest.V`
  is `[JsonRequired]`, so a frame that never says which protocol it speaks is answered
  `INCOMPATIBLE_PROTOCOL` — at the hello, and equally on an already-authenticated session — and one
  `protocol_rejected` line records it with `said: "absent"`. The initializer on that property remains
  the OUTGOING default, which is why no client changed. Until 2026-09-06 the default answered the
  question on the peer's behalf: a versionless `hello` deserialized onto `ProtocolVersion`, so
  `req.V != ProtocolVersion` compared the current version against itself and the one check that must
  never be optional was (Codex F13; measured: a hello carrying no `v` was answered
  `{"ok":true,...,"compatible":true}`, and a versionless `buy` on a live session FILLED).
- **Every enumerated field accepts exactly its named values, and an unrecognised one is refused.** The
  closed vocabularies the frame carries are `tif` (`buy`/`sell`: `Day`, `GoodTillCancel`,
  `ImmediateOrCancel`, `FillOrKill` — case-insensitive, **default `Day` when absent**), `all`
  (`orders`: `true`/`false`, **default `false`**), `origin` (`material-list`: `inbox`, `agent`, `all`,
  **default `all`**) and `kind` (`material-note`: `ran`, `used`, `derived`, `note`, **default
  `note`**). A field that is present and unrecognised is `INVALID_REQUEST` naming the field and the
  accepted values, with zero connector calls; only an ABSENT field takes a default. `tif` used to be
  `Enum.TryParse` with a `Day` fallback, which failed open twice over: a misspelling became a resting
  Day order, and `tif: "999"` PARSED — TryParse takes the underlying integer — and reached the
  connector as `(TimeInForce)999` (Codex F8; both measured over the pipe). **Present-and-EMPTY is
  present**: `tif: ""`, `tif: null` and `all: ""` are refused, where they used to fall through the
  refusal onto the default because the reader asked whether the value was empty rather than whether
  the key was there (Codex F6; measured: `tif='' -> ok=True · connector saw: Day`).
- **A price that is present and unreadable is refused, never read as an absent one.** `quantity`,
  `limit` and `stop` (on `buy`/`sell`/`modify`) accept a JSON number, or a string of digits with an
  optional leading sign and at most one `.` — **no thousands separators, no exponent, no surrounding
  whitespace, parsed invariant**. Anything else is `INVALID_REQUEST` naming the field, with zero
  connector calls. It matters because the ORDER TYPE is derived from which prices are present, so a
  dropped price is a different order: `decimal.TryParse(...) ? d : null` made `limit: "bad"` a MARKET
  order, and the framework's default `NumberStyles.Number` in the ambient culture made `limit: "1,5"`
  a limit of **15** (Codex F6; both measured over the pipe: `limit='bad' -> ok=True · connector saw:
  Market limit=none` and `limit='1,5' -> ... Limit limit=15`). The culture is NAMED at the call site
  rather than inherited: `InvariantGlobalization=true` in `Directory.Build.props` already makes the
  ambient culture invariant everywhere this ships, and a price is not a number whose meaning a build
  property may decide. On `modify` the same collapse read an unreadable price as "leave that price
  where it is". A field named with JSON `null` in it is PRESENT and is refused on all four of
  `limit`, `stop`, `quantity` and `tif`.
- **`side` and `type` are not fields of this protocol and a frame naming one is refused.** The side is
  the op and the type is read off which prices are present (neither = Market, `limit` = Limit, `stop`
  = Stop, both = StopLimit). They were accepted and discarded, so `{"op":"buy","side":"sell"}` bought
  — the same failure as an unrecognised enum value, by the other door.
- Reads and order operations only. Operator authority is not on this channel — **and neither is the
  operator's record of using it.** `order <id>` resolves a request record only when the caller wrote
  it: a row whose session is `operator` never resolves here, and a gateway-minted `op-` id resolves
  only for the session whose own sweep minted it (the agent is handed those ids in its own
  `cancel-all` / `close-all` replies, so it must be able to ask about them). It is answered as an id
  nobody ever minted is answered, identically, because a refusal that looks different from a miss is
  an existence oracle. Until 2026-09-05 `trade order op-close-<nonce>-ES` returned the operator's
  press record whole — session, parameters, state, broker order id and the sentence written for the
  owner's screen (UNVERIFIED 6). The BROKER's book is unrestricted and stays so: `orders --all`
  already shows every order on the account, and it carries the platform's view rather than
  TradeAgent's record of who asked for it.
- **`pnl` answers in money, and a null there is an UNKNOWN rather than a zero.** It is computed from
  TradeAgent's own `fill` table (schema 5) — one row per execution, keyed `(account_id,
  execution_id)`, written by the gateway from BOTH the connector's execution stream and a pull of
  `GetExecutionsAsync` at every (re)connect and every five minutes, so a fill both sources report is
  one row. Realized P&L is average cost, signed by side, sized by `InstrumentInfo.ContractSize` or by
  the value of a point (`TickValue / TickSize`) where the platform reported those instead, and by
  quantity × price where it reported neither. `net` is `realized - fees` and is **null whenever any
  fill in the period carries no fee**, because a net computed as though an unreported cost were zero
  is wrong in the owner's favour on exactly the fills nobody checked; `fees` is the sum of the fees
  the platform did report and `fees_unknown_fills` counts the rest. `unrealized` values the
  platform's own positions at the last price this gateway saw, and is null when a held symbol has no
  price. `max_drawdown` is the worst peak-to-trough drop of that curve. `incomplete` names every gap
  in words, including a `GetExecutionsAsync` that FAILED — the difference between "nothing traded"
  and "nothing was asked" — and how far back the ledger reaches at all: coverage starts when
  TradeAgent first read the platform's executions, and **on ATAS that is when the bridge started**,
  because `AtasStrategyAdapter.GetExecutions` reads the strategy's in-session `MyTrades` whatever the
  bridge says about order history. `by_day` buckets are UTC calendar days and the default window is
  the UTC day; `--since` takes an ISO-8601 date or instant and, present and unreadable, is refused
  rather than read as today; `--all` is everything the ledger holds and cannot be combined with it.
- **`data-list` and `data-bars` are READS, and there is no write beside them.** They serve the
  `dataset` and `dataset_file` tables (schema 8) and the normalised file those rows describe. Nothing
  on this channel collects, normalises, rebuilds, rejects or deletes a dataset: the account owner
  presses that in TradeAgent, on the Settings page under Market data. The asymmetry is the same one
  `material` makes and is there for the same reason — an agent that could edit the provenance of the
  data its strategies are judged on could report a clean twelve months over three with the holes
  filled in. `data-list` carries, per dataset: source, pair, interval, version, `months_attempted`
  against `months_present` (the coverage TARGET against what was actually got) and the months not
  published by name; the normalised file's SHA-256, the bar count, the first and last bar, `gaps`
  (minutes with no bar INSIDE the covered period, with `gap_runs` bounded at 50 and
  `gap_runs_truncated` saying when the list was cut), `duplicates` dropped and `incomplete` (bars
  excluded because their close time was after the archive was read); and per RAW archive file the
  URL, the SHA-256 the vendor published, the SHA-256 TradeAgent computed, the byte count, the
  download time and the timestamp `unit` the file turned out to be written in. Every dataset is
  re-verified as it is listed — every raw file and the normalised file re-hashed against the row — and
  a disagreement moves it to `state: REJECTED` permanently, with `rejected_reason` in words; a
  REJECTED dataset serves no bars and is never re-normalised from the changed bytes.
  **What the bars are NOT**: hypothesis evidence. They establish no fill, no queue position and no
  intrabar ordering, so a result computed over them is a reason to test something and never a record
  of a trade, and one venue's bars are not another venue's execution evidence (`docs/COUNCIL.md`,
  "Data"). Nothing is filled in — a missing minute is missing and is counted. The timestamp unit is
  decided per raw file **by the magnitude of the number**, never from the month it is named after:
  Binance moved this column from milliseconds to microseconds in January 2025 and a twelve-month
  collection straddles that, so a build that read the date would place a millisecond file dated after
  the change in 1970. `data-bars` takes `pair` (required, upper-case letters and digits) and the
  inclusive `from`/`to`, which take an ISO-8601 date or instant and, present and unreadable, are
  refused rather than read as something else — the `pnl --since` rule. **At most 10 000 bars in one
  call, and a bigger window is REFUSED naming that limit rather than truncated**, because an answer
  quietly cut short is a different window from the one that was asked for and nothing in the reply
  would say so. A pair with no dataset, and a REJECTED dataset, are both `MARKET_DATA_UNAVAILABLE`.
  Both handlers are in the deadline table at **0** — no connector call at all.
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
- **`status` carries what the AI's own work has cost, and it is a READ.** `ai_state` (`stopped`,
  `working`, `waiting`, `paused`), `ai_turns_today` since local midnight, `ai_cost_today` — **ABSENT,
  never zero, when TradeAgent cannot price the turns** — `ai_cost_estimated`, the sentence saying the
  figure is an upper bound rather than a bill, and `ai_model`, the model TradeAgent asked the AI tool
  for (absent where it asked for none: the tool takes no model flag, or nothing has named one).
  `ai_model` is the app's choice and is composed by the app; there is no verb and no op that changes
  it, starts or pauses the loop, or moves the daily ceiling — operator authority is deliberately
  absent from this channel, so what the agent gains is the ability to see its own bill and never to
  edit it. The model is on the wire because the mission is to cover what the work costs and the model
  is most of that arithmetic: the same turn on `gpt-6-astra` and on `gpt-5.6-luna` differs by a factor
  of fifty. Measured on codex-cli 0.153.4 (2026-09-06 and again 2026-09-07): the CLI's own event
  stream names NO model, even when `-m` was on the command line, so `ai_model` is what the app asked
  for and `ai_cost_estimated` says as much.

## Bridge protocol — `src/TradeAgent.Connectors.Atas/BridgeProtocol.cs`

Compiled into both halves so the shapes cannot drift. TradeAgent hosts; the bridge dials in, sends
`hello` with its capabilities, then heartbeats. One payload field, `data`, in both directions. A
`bridge_protocol_version` mismatch is refused outright rather than half-trusted. `rejected: true` on a
failure frame is what marks a refusal definite. **The version is 3** (`Versions.BridgeProtocolVersion`):
U14 raised it from 2 because the write-ahead promise changed — a version-2 bridge places the order
whether or not the `coid-witness.json` rewrite reached the disk, and omits `witness_failure` from its
hello, so its silence cannot be read as "no trouble"; the mismatch routes it to `IncompatibleBridge`,
which names the version and the repair.

**A capability proof does not outlive the bridge's ability to attest it.** Every heartbeat carries the
current `Describe()`, and `BridgeServer` degrades to a BARE PULSE whenever that read throws — which is
what happens when ATAS's own `Portfolio` and `Connector` properties stop answering. A heartbeat whose
payload is absent, unreadable or at an incompatible bridge version therefore clears `_hello`:
`ConnectorCapabilities` reports nothing supported, `ReconciliationProvable` goes false and the gateway
refuses `LIVE_AUTONOMOUS` with `AUTONOMY_REQUIRES_PROVABLE_STATE`. **It is not a refusal and not a
disconnect** — the peer is not accused, liveness is still refreshed by the pulse, nothing is decided by
a clock, and the next heartbeat carrying a whole compatible answer restores the proof on the same
connection. The bridge row says which state it is, in its own sentence: *"connected and has stopped
saying what it can do"*, distinct from *"has not said hello yet"*, because a bridge that introduced
itself correctly must not be reported as the wrong strategy on the chart. Until 2026-09-06 the pulse
refreshed liveness while the latched answer stayed true, so autonomous eligibility survived the
evidence for it (Codex F7; measured over a real pipe with every other gate deliberately open — mode
`LIVE_AUTONOMOUS`, live activated, all four health rows READY — and every `Describe()` after the
handshake throwing: `after 10.0s of pulses that cannot describe the bridge: connected=True coid=True
history=True provable=True` and `autonomous dispatch: authorized=True`. The same probe now reads
`provable=False` and `authorized=False code=AUTONOMY_REQUIRES_PROVABLE_STATE`).

**And what a reader sees while that clear is happening: the last attested description, or none at
all, never a torn read.** `_hello` is volatile and every reader takes one snapshot of it and decides
on that, so `Capabilities`, `Bridge` and the whole bridge status row are each derived from a single
instant. Which of the two answers a given read gets is genuinely a race and neither is wrong; a
`NullReferenceException` out of the getter the gateway consults before it permits anything is.

## What ATAS's own objects mean — the readings, not the guesses

**`Position.Volume` is NEGATIVE for a short.** Measured 2026-09-05 on ATAS 8.0.14.397, simulated
account `CRYPTO5EB41`, instrument BTCUSDT: a position opened by ATAS's own chart trader — its
confirmation dialog read `BTCUSDT Perpetual · Sell/Short · Market · 1 Lots` — came back through
`AtasStrategyAdapter.ToPosition` as `quantity: -1, average_price: 79913.8`. The operator Close All
that followed recorded `Buy 1 BTCUSDT at market`, ATAS filled a Buy of 1 at 79913.9, and the position
went to `0` — flattened, never doubled.

It is load-bearing, and the adapter used to say it was decorative. `TradingGateway.CloseAsync` and
`TradingGateway.OperatorCloseAllAsync` both size and SIDE a market order with
`quantity > 0 ? Sell : Buy` off exactly this number, so an inverted convention would have doubled a
position rather than closed it. The adapter's comment said "reported here for display only", which
was true of that file and false of the product (review 2026-09-05b finding 4 / Codex F4).

**What the reading licenses:** one platform, one instrument, one simulated account. The SIGN is a
fact about ATAS's data model and travels; nothing about latency or fills does. The adapter uses it
only to RECOGNISE the order ATAS built for a close (`ClosingDirection`), never to send one — a wrong
reading there costs a refusal to identify the close, which is the safe direction.

**`Order.Comment` on a close is ATAS's, not ours.** `ITradingManager.ClosePosition` builds the order
itself and writes `Close position` into the comment, so a close carries nothing of ours at submission
time; `ClosePosition` identifies it afterwards by (account, instrument, closing direction, size) over
the orders **and the fills** that appeared during the call, and refuses to name one unless exactly one
matches. Measured 2026-09-05: the press minted `TA-op-close-31d4779568274e53-0` and ATAS's own order
read back `client_order_id: "Close position"`.

**A filled close is in NO order collection — it is on the fill.** Measured 2026-09-06 (ATAS
8.0.14.397, simulated `CRYPTO5EB41`, BTCUSDT): a short of 1 closed by the operator's Close All filled
at 79720.2, and the diff over `ITradingManager.Orders`, `ChartStrategy.Orders` and `Connector.Orders`
saw **zero** new orders — the bridge answered, inside its budget, `0 of the 0 order(s) ATAS added
match Buy 1 BTCUSDT on CRYPTO5EB41`. A market close on this platform is Done before the adapter can
look, and a Done order is in none of the three. The same fill is in `MyTrades` inside the window and
carries `MyTrade.Order` — the order object itself, which is where `client_order_id: "Close position"`
in `trade executions` is read from. So the causal window is over both collections; the four terms that
must match are unchanged. With the fill included, the same press read `the platform filled it`,
`TradeAgent thinks: filled`, `Broker reference: 12063688` — the id of the execution ATAS recorded.

**`Order.Comment` carries at least 65 characters.** `TA-COID64-…` and `TA-COID65-…` were both accepted
verbatim and read back byte-identical (2026-09-05, ATAS's own collection, no broker attached). The
64-character ceiling on a client order id is ours, not ATAS's.

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
- **Inside the bridge, every ATAS money call has a deadline too**, and a bridge that stops waiting for
  one says so: `BridgeHello.TradingSurface` carries `calls=ok` or `calls=stalled(<op>@<budget>)` from
  the expiry onwards. Without it a wedged call took the frame loop with it — including the operator's
  cancel-all — while the heartbeat went on reporting READY.
- **`BridgeBudgets.Emergency` (2 s) — the CALLER's total** for `cancel`, `cancel-all` and `close`,
  covering the send gate, the write and the reply together. On expiry the caller is told the
  operation is NOT confirmed and to check ATAS, and the record is UNKNOWN. `place` and `modify` never
  get it. `AtasConnector.EmergencyDeadline` defaults from it — and so does the BRIDGE, which is the
  half that was missing. The bridge answered a close on its own clock (`WaitFor(AckTimeout)`, three
  seconds), which cannot fit inside two: measured 2026-09-05, a Close All whose close FILLED in
  341 ms was recorded `'close' is NOT confirmed … The bridge is busy`, because the caller gave up at
  2.0 s. The bridge now spends `Emergency` minus a 200 ms wire allowance (measured app→bridge:
  8.2 ms), split one-third to the ATAS call and two-thirds to the acknowledgement wait. Both ends
  read the one constant, out of a file compiled into both.
- **Whether the CONNECTION is dropped is a different question on a different clock.** The bridge is
  dropped only when it has answered nothing within the ordinary RPC deadline (10 s) — not when one
  emergency went unanswered for two. A bridge handles frames one at a time, so silence while it works
  on our own frame is what a busy bridge looks like as well as a dead one. An answer that arrives
  after its caller gave up is delivered rather than discarded.

`WorstCaseOrderPath` is `WriteTimeout + FrameTimeout + rpcTimeout` and the shutdown drain is derived
from it, so a connector built with different deadlines moves the drain with it.

**A `place` gets the emergency deadline when — and only when — the command says it is closing.**
`PlaceOrderCommand.Intent` is how a close, which is an offsetting placement, is told apart from an
order that opens exposure; the connector cannot derive it from the op or from the side. The last two
rows of the handler table keep an ordinary placement's worth of headroom anyway, because the intent
is an obligation a third-party connector may ignore and a drain that is too short abandons an order.

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
| **E** | `ITradingConnector.EmergencyBudget` — the WHOLE risk-reducing part of one operation, however many calls it decomposes into. `2 s` at shipped values (`BridgeBudgets.Emergency`). |
| **L** | `GatewayPipeServer.MaxLegsInFlight` — how many legs of a sweep are in the air at once. `4`. |
| **S** | `GatewayPipeServer.SettleAfterCancelTimeout` — the write-back margin, added ONCE on top of the maximum. `5 s`. |
| **H** | `GatewayPipeServer.HandlerOverhead` — what a HANDLER costs beyond its connector calls, added ONCE. `1 s`. |

| handler | serial depth | why that is the chain |
|---|---|---|
| `status` `schema` `accounts` `account` `instruments` `quote` | **2W** | an account resolution, then the read — `schema` builds the same status `status` does |
| `positions` `position` `orders` `order` `executions` | **2W** | the account, then the read |
| `connectors` `material-list` `material-note` `data-list` `data-bars` | **0** | no connector call at all — in the table anyway, because a handler that is ABSENT is one nobody notices growing a call |
| `pnl` | **3W** | the account, the positions, then the instruments. The fills cost nothing: they come out of the ledger. Both reads may FAIL without failing the handler — the report names what it could not include |
| `buy` `sell` | **5W** | a cold placement: account → positions → quote → instruments → place |
| `modify` | **6W** | one orders read that both resolves the target and takes it as it stands, then everything a cold placement does — account, positions, quote, instruments — and the modify. It is risk-checked on its resulting size, so it pays a placement's chain |
| `cancel` | **E** | resolve the target, then cancel — every call risk-reducing, so the whole handler is the one budget |
| `cancel-all` | **E** | the orders read and every leg, all inside the one budget |
| `close` | **E + W** | all of it inside the budget on a connector that reads the close intent, plus ONE ordinary placement for one that does not |
| `close-all` | **E + L·W** | the same, plus one WAVE of placements — every leg ends in a `Place` and `TradingGateway._dispatchGate` is a mutex, so a wave's placements queue rather than overlap |

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
missing from it — `schema`, `connectors`, `material-list`, `material-note`, `data-list` and `data-bars` — and `schema` makes a
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
`DISPATCHING` and `RECONCILING` are in that row because the word is about the WIRE, and the leg
carries the connector's own `transport` beside the word rather than the pipe server editing a row it
does not own. A mutation cancelled by disposal no longer stays `DISPATCHING` and unflagged: every
dispatch path catches the cancellation and settles UNKNOWN while the store is still open. What still
reaches `DISPATCHING` is a handler that outlasts the drain and never unwinds — the
`handlers_did_not_finish` case.

**And the gateway now agrees with the word rather than contradicting it.** A
`ConnectorTransportException` whose transport result is `NothingWritten` is settled `CANCELLED` with
no flag and no pause: the connector PROVED nothing left the process, so there is nothing at the
broker to reconcile, and flagging it refused every further order — including the retry the message
advises. Only `PossiblyWritten`, `ReplyReceived` and silence stay indefinite. `CANCELLED` rather than
`REJECTED`, which is reserved for a definite refusal by a broker that was never asked, and rather
than an unflagged `UNKNOWN`, which nothing would ever move. `cancel-all`'s `cancelled` count and its
`not_cancelled` list read the per-leg WORD, so a terminal row that was never sent is not counted as a
cancellation that landed.

**`close-all` answers in the same shape, and the count is where the two sweeps legitimately differ.**
`closed` and `not_closed` read the word too, and `not_closed` carries it in the same `outcome` field
`not_cancelled` has: they read the RECORD alone until 2026-09-05, so a leg the connector proved it
never sent came back as `state: CANCELLED` and nothing else — which reads as the platform having
cancelled the closing order rather than as a position still open that nothing was sent about. But
`closed` is **not** the word alone: `confirmed` is "this leg's own intent is done" and is read off a
`CANCELLED` *or* `FILLED` record, which for a cancel leg both mean the order is not working any more,
while a CLOSE leg's intent is a FILLED offsetting order and a closing order that was itself cancelled
has flattened nothing. So `closed` requires the word **and** a `FILLED` record — the transport has to
agree and the position has to have actually closed — and the reading cannot over-claim from either
side. The one over-claim reachable today is the record-only one; the other is a shape no shipped
connector produces, and it is closed here rather than left to be found.

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

**`DISPATCHING` means the wire may have been touched, and it expires.** The row is written before the
connector is called, so a record still in `DISPATCHING` is one where nobody wrote down what the
broker did. Two things follow, and both are the gateway's own behaviour rather than advice:

- **At startup** — in the constructor, before any caller can read the store or place an order — every
  `DISPATCHING` record becomes `UNKNOWN`, flagged for reconciliation, with execution capability
  `PAUSED`. A record left mid-flight by a crash, a Windows restart or an update therefore pauses
  trading on the next start instead of being silently trodden on by the next order.
- **While running**, a record still `DISPATCHING` longer than `TradingGateway.DispatchStrandedAfter`
  counts as unconfirmed work for the gate, the status fields and the health row, without waiting for
  a restart to notice. Under that bound an order in flight is ordinary and pauses nothing.

**The stranded bound is DERIVED from the connector, exactly as the shutdown drain is**, and it is
`Connector.WorstCaseOperationPath + GatewayOptions.DispatchSettleSlack` — 50 + 20 = **70 s** at
shipped ATAS values. An explicit `GatewayOptions.DispatchStrandedAfter` may only LENGTHEN it. It was
the constant 30 s until 2026-09-05, justified as "the connector's 10 s RPC deadline plus 20 s of
slack" while one ordinary order path is 50 s — the send gate, the whole frame and the reply, which is
what the drain has been derived from since the 2026-09-03 correction and what the stranded bound
never got. A placement legitimately in flight for 30–50 s was therefore "stranded", was already past
`AbsenceGrace` when the reconciler could first see it, and was settled `CANCELLED` / "never reached
the broker" / unflagged with trading resumed — and then filled (REVIEW 2026-09-05 finding 1, probe
P6b). The second term is not a second deadline: the connector's own bounds cover the CALL, and a
dispatch also has to be rescheduled and write the outcome down after it returns.

**A DISPATCHING row on disk cannot say whether anyone is still flying it, so the gateway remembers.**
`TradingGateway` holds an in-memory lease over every request from immediately before a mutating
connector call until the dispatcher has settled it. While the lease is live the reconciler will not
move that row: it is counted, it keeps trading paused, and its reconcile line names the wire —
*still on the wire for N s of a possible M s*, against the connector's own worst case. The lease is
deliberately not durable, because a claim that outlived the process holding it could never be
released: a genuinely abandoned record — crash, restart, update — has no lease at the next start and
reconciles at the bound like any other.

**The owner's override obeys the same lease, in the same words.** `ForceResolve` refuses a request
this process is still inside the connector call for, and the Dashboard's unconfirmed card asks
`TradingGateway.StillOnTheWire` on every tick and shows that sentence where the two override buttons
would be. The reason is not deference to the machine: while the order can still reach the broker
there is nothing in ATAS for the owner to have seen, so the book at that instant is not evidence
about this request. **The row is not hidden** — `Unreconciled()` still lists it, it is still
unconfirmed work, and it still keeps trading paused; taking it out of that list would let the gate
authorize a new order over a live dispatch. What it loses is the pair of buttons. A row whose
dispatcher is dead — an entry this process watched end, or no entry at all — is unaffected, which is
the ordinary case the override exists for. It was reachable through the button until 2026-09-05:
the card wrote CANCELLED / "resolved by user: no such order exists" onto a live `DISPATCHING` row,
cleared the flag and the latch, and trading resumed while the placement was on the wire (REVIEW
2026-09-05b finding 1, probe P3).

**And a `Settle` that arrives after some other party moved the row WINS when it carries a definite
broker answer** (logged `late_definite_settle`): `already_settled` is the right word for a race with
the event stream and was the wrong word for a race with the reconciler, which had moved the row
precisely because no answer had been written down yet. `UNKNOWN` and `RECONCILING` are the only two
states it may overrule, and it still cannot resurrect a terminal row — the state table refuses to
leave one. **A terminal row a PERSON asserted is the third case, and it is re-flagged rather than
overruled**: the dispatch lease stops one process overriding its own live dispatch, but two gateways
over one store (the app and `tradeagent-gateway.exe`) and a restart both still reach it. The record
keeps the state the owner put there — overwriting it would erase the only account of what they saw —
and gains the platform's own answer and reference beside the claim, the flag, and a paused gate
(logged `late_definite_over_an_override` at error). Only when the two genuinely differ; an answer
that agrees is agreement. The stream disagreeing with the dispatch about a row no person touched is
not adjudicated here, exactly as `ForceResolve` refuses to adjudicate it.

Unconfirmed work is therefore "flagged, **or** dispatching for too long, **or** an outcome TradeAgent
could not write down"; `trade status`'s `unreconciled_requests` counts the first two, and every
surface that reports or acts on unconfirmed work — the gate, the health row, the doctor, the
unconfirmed card, the background reconciler — asks the same question rather than the raw flag.

**The pause does not depend on the database.** Recording an outcome is a write, and a write can fail
(locked file, full disk, read-only store). For an INDEFINITE outcome execution is paused in memory
BEFORE the write is attempted; for a DEFINITE one — the broker answered, and the answer is what
cannot be stored — the pause is taken the moment the write fails. **The test is whether the wire has
been touched, not what came back:** an answer nobody could write down is an unconfirmed outcome, and
a record still `DISPATCHING` looks like an ordinary order in flight until the stranded bound expires.
Either way the failure is reported to the caller as `STATE_DATABASE_CORRUPT`, written to the
engineering log at error (`record_indefinite_failed`, `settle_failed`) off the failing thread, and
does not lift the pause. For the same reason the settle comes before the activity line on every
dispatch path: both are writes, and the outcome is the one that must land. A reconcile pass that
finds nothing pending while that pause is held reports itself unfinished instead of clearing it.

**A request leaves the unconfirmed set only on positive, definite, stable evidence about its own
target. Anything else is inconclusive and keeps trading paused.** Every branch of reconciliation is
derived from that one rule:

- **A cancel or a modify is reconciled against the order it named**, not against a client order id it
  never transmitted, and the target is looked up by id against the whole book — never a time window,
  because an order that has rested longer than the window is not absent, it is old.
- **Absence is evidence only after the grace window** (`GatewayOptions.AbsenceGrace`), measured from
  **the later of the operation's own dispatch and the stranded bound**, on a connector that can prove
  its own history. Then a cancel whose target does not exist is `CANCELLED`. The later of the two,
  because "the broker has never heard of this" says nothing while the order can still be on its way
  there — and measured from the dispatch alone the window had always already expired on any stranded
  record the reconciler could see, the bound being longer than the grace. Where this process watched
  the dispatch END, the wire went quiet then and the dispatch instant is the honest reference; where
  it did not — a crash, a restart, a second process over the same store — the bound is all there is.
- **A target that is `UNKNOWN`, `DISPATCHING`, `RECONCILING` or `CANCEL_PENDING` decides nothing.**
- **"The cancel did not take effect" needs a TERMINAL target, a definite refusal, or the owner's
  card.** A target that is merely working is not proof, and it does not become proof by holding
  still: an order that has not moved is an order the platform has said nothing about, and the
  platform's acknowledgement can arrive after TradeAgent's own RPC gave up. Only a definite end
  makes the request `REJECTED`, after which the agent may ask again under a new request id;
  otherwise it stays unconfirmed and trading stays paused.
- **A modify is `ACKNOWLEDGED` only if the ORDER THAT WAS NAMED carries what was asked for**, and is
  never recorded as a definite failure without a definite refusal. The answer has to be about that
  order: the returned order id must be the target's, and its symbol and account must be the target's
  too — an answer about a replacement minted under a new id, or about somebody else's order, settles
  nothing. Prices are judged on the instrument's tick grid, and a request is carried by exactly two
  prices: the grid point below it and the one above (not a band of one tick, which accepts a
  neighbouring grid point when the request is already on the grid). **A returned price that is the
  price the order already had is not evidence of a change** when the request differed from it — that
  is a platform ignoring a sub-tick change, and the dispatcher records the target's prior prices so
  the reconciler can tell the two apart. `OrderInfo.Quantity` **is** defined, in
  `TradeAgent.ConnectorSdk.Contracts`, as the order's TOTAL and never the remainder, so a quantity
  that does not match is a change that is not there — still inconclusive rather than a refusal.
**An emergency press IS its records: one shot, then a pause, then a person.** "Cancel all working
orders" and "Close all positions" bypass authorization on purpose — they must work while trading is
paused and while the kill switch is down — and each thing a press does gets a write-ahead
`execution_request` **written already flagged** before the wire is touched, attributed to the
operator, keyed by the press. From that moment trading is paused, *whatever the platform answered*: a
close the platform accepted and left WORKING has flattened nothing, and a cancel-all that succeeded
outright is still something the owner has not read. **Only the owner's card clears a press record.**
The reconciler is not allowed near them — it would release a press whose position is still open, and
drag a row the platform answered plainly through `UNKNOWN` on the way.

- **A second press of the same control is refused while the last one is unresolved**, with the time
  it was sent: *"close-all sent at 14:32; resolve it first"*. Per control, not globally — an
  unresolved cancel-all must never be able to stop somebody flattening a position. **The refusal and
  the first durable row are ONE STATEMENT** — the press's first `execution_request` is inserted
  flagged, guarded by `NOT EXISTS(a flagged row of this control)` — so it holds between the two
  processes that reach these controls (the app and `tradeagent-gateway.exe`, over one database) and
  not merely within one. It was a store read followed by a store write with a connector round trip
  between them: two presses released together both passed, both captured the same position, both
  passed the drift re-read because neither fill had landed, and a long 2 became **short 2** with both
  presses answering "ok" (REVIEW 2026-09-05 finding 2, probe P10; Codex F6).
- **A press names its own legs**: `op-close-{nonce}-{index}` and `op-cancel-{nonce}-{index}`, the
  same shape as the agent path's `op-{nonce}-{intent}-{index}` and for the same reason — the id is
  carried onto the order as `TA-{id}` and safety rule 1 requires the broker to hand it back
  unchanged. It used to end in the target: an instrument name for a close, a broker order id for a
  cancel. `TA-op-close-…-ES 12-25 [CME Globex Futures]` is 58 characters with `' []'` outside
  `[A-Za-z0-9-]`, and the MES contract's name makes 65 against the 64-character ceiling (REVIEW
  2026-09-05 finding 4, probe P2). The target itself is on the record — the instrument in
  `instrument`, the order id in `parameters` — which is where the card already read it from. Every id
  the gateway mints is checked against that charset and budget before it can become a client order id.
- **There is no retry and no press object.** A press's nonce is minted once, inside the gateway, and
  never handed back; nothing in the UI holds it and nothing is reconstructed at startup. A restart
  reads the same flagged rows and refuses to trade over them, which is what "the durable records ARE
  the press" means. The previous design — a nonce held by the screen, reused by the next click —
  is what made a definitely failed close impossible to press past.
- **Cancel-all is per-order cancels of the set it captured.** No account-wide sweep is sent:
  "cancel whatever is there" acts on orders the person never saw and can be reconciled against
  nothing. Each record is settled from the platform's answer **about its own order**, and one leg
  failing neither stops the press nor decides another leg.
- **Close-all re-reads the position immediately before each wire call and sends nothing for an
  instrument that changed.** The press captured a size and turned it into a market order for that
  size; if a fill landed in between, that order opens exposure rather than closing it. A changed
  position is a different decision, so it is refused and named, and the owner presses again.
- **And it sends nothing for an instrument this gateway still has an order ON THE WIRE for.** The
  re-read above compares POSITIONS, so it sees a fill that has landed and is blind to the one that
  has not: an agent's own `close` is an ordinary `execution_request`, and while it sat inside the
  connector call the re-read still showed ES 2, so the press sized a market sell 2 beside the agent's
  sell 2 and a long 2 became **short 2** — after the owner pressed the control whose whole purpose is
  to flatten (REVIEW 2026-09-05b finding 2, probe P6). **The check and the wire are one statement**:
  the leg's write-ahead insert itself refuses to run while a request that can move a position on that
  instrument is `DISPATCHING`, so there is no window between asking and sending, and it holds between
  the two processes that reach these controls. The answer says so — *"1 leg waited on an order still
  on the wire, so nothing was sent for it: ES is waited on by <request>, still DISPATCHING"* — and it
  is **per leg**: the other instruments of the same press are still closed. `DISPATCHING` and not
  also `UNKNOWN`, deliberately: an UNKNOWN record is a dispatch that is over with no answer and is
  the ordinary state of the emergency somebody is pressing the button about, so refusing on it would
  re-impose exactly the pause these controls bypass on purpose.
- **An `UNKNOWN` order on the instrument is SETTLED before the leg sends, and is never sent over.**
  That reasoning was right about refusing and was never an argument for sending. An order the broker
  accepted with the acknowledgement lost is RESTING at the platform: the press reads the position as
  still open because nothing has filled, sizes its market sell against it, and both fill — long 2 to
  **short 2** again, by a different route. So for each leg, and inside the press's own deadline, every
  record on that instrument that is `UNKNOWN` and would offset the same way the leg would is read back
  by its client order id (`GetOrdersAsync`, then the fills). **Terminal at the platform** → the record
  is settled the two steps `ReconcileAsync` settles it in (`UNKNOWN → RECONCILING →` the platform's
  own state) and the leg proceeds on the re-read position. **`ACKNOWLEDGED` or `WORKING`** → it is
  cancelled, and the leg proceeds only on a cancel that returned. **Anything else** — a timeout, a
  disconnect, a refused cancel, an order the platform does not list, an answer that is itself
  `UNKNOWN`, `CANCEL_PENDING` or partially filled, a deadline already gone — is not an answer: the
  record is left exactly as it was and **that leg is refused** with `CLOSE_UNRESOLVED_ON_INSTRUMENT`.
  Absence is not read as "it never landed" here; the reconciler's absence rule needs a grace window a
  press does not have. A refused leg still **writes its row**, flagged, in `CREATED` with the reason
  in it, so the card names the instrument beside the position that may still be open; every other leg
  goes out; and the press stays the owner's to resolve. A press's own row is never settled this way —
  only the person who made it may resolve one. Every call here is charged to the press's deadline
  exactly as its close is.
- **And the agent's own `close` or reduce is refused while such a record exists** —
  `CLOSE_UNRESOLVED`, naming the record. It is the same doubling reached from the agent's side:
  `RefuseAStaleCloseOrThrow` compares this close's size to the LIVE position, and while the earlier
  close is still resting at the broker those two agree exactly. A flagged `UNKNOWN` record already
  pauses trading; what does not is one the owner has confirmed on the card WITHOUT being able to say
  what happened to it, and that record is still an order that can fill. The refusal lifts when the
  record gets an outcome — the owner confirming it as filled, cancelled or rejected, or an emergency
  press settling it by the rule above.
- **The two controls are not symmetrical here, and only close-all needs the guard.** A close leg
  computes a side and a size from a reading and sends a market order for them, so a reading that is
  stale by one in-flight fill makes the press itself add exposure. A cancel leg computes nothing: it
  names an order the press captured and asks the platform to stop it. An agent's modify or cancel of
  that same order held inside the connector call leaves no order working, sends nothing the owner did
  not ask for, and ends in a record that does not claim its own change took effect (the modify
  UNKNOWN and flagged, the cancel REJECTED).
- **Completion and outcome read the ACCOUNT stored on the records**, never whichever account is
  selected now — the owner can change that between the press and the card.

**Every mutating operation is idempotent by request id, including the multi-target ones.** A `buy`,
`cancel`, `modify` or `close` keys one `execution_request` on the caller's id and a repeat dispatches
nothing. `cancel-all` and `close-all` decompose into legs, so they key a `composite_request` instead:
the outer request id, the plan it captured, and the nonce its legs are named after, **all written
before any effect**; the answer is written after. A repeat of a known id returns that stored answer
and touches nothing — and a repeat of one whose first run died mid-flight re-runs against the STORED
plan and nonce, so the legs that already have records dispatch nothing and only the unfinished ones
go. Before this, a sweep minted its nonce per CALL: an agent that lost the reply and re-sent the same
request id got a second sweep over whatever was on the book by then, including orders it had placed
since.

**A request id names one operation, from one session, and a replay is looked up before anything is
read.** The `composite_request` row has always carried `op` and `agent_session_id`; nothing read them
back, so a `close-all` id reused for a `cancel-all` RESUMED the close-all's captured plan under the
wrong verb — and a *completed* one answered the second caller with the first operation's reply, which
tells an agent its cancellation is already done while every order is still working. Both mismatches
are now refused with `INVALID_REQUEST`, naming the operation the id already has; the answer to one is
a new request id, never a guess about which operation was meant. And the lookup is the FIRST step,
before the book or position read that builds the plan (`TradingGateway.BeginCompositeAsync` takes the
capture as a delegate and does not invoke it on a replay), so the case a request id exists for — a
lost reply, re-sent — is answerable with the platform exactly as unreachable as it was when the reply
went missing (REVIEW 2026-09-05, Codex F7). **A replayed sweep therefore performs NO read at all**:
zero connector calls, the stored answer, and the verb/session binding as the one gate it passes. The
gateway alone was not enough — `cancel-all` and `close-all` over the pipe still read the book and the
positions ahead of the call, so a replay with the connector unreachable answered
`TRADING_CONNECTION_MISSING` after one connector call rather than returning the reply already in the
database; both now hand the read over as the delegate.

**And a second caller on a composite that is still RUNNING waits for it, rather than resuming it.**
A `composite_request` row with a null result means two different things — the run died mid-flight, or
it has not finished — and the store cannot tell them apart. Read as the first, a duplicate re-ran the
stored plan while the owner was inside a connector call, saw each leg in the write-ahead `DISPATCHING`
state it was in at that instant, and wrote THAT down as the answer; `Complete` is first-write-wins, so
the transient reading became permanent and the owner's real answer was dropped. The agent that asked
what its `cancel-all` did was then told, for ever, that nothing was cancelled — about a sweep that
cancelled everything (REVIEW 2026-09-05b, Codex F18). The gateway therefore keeps an **owner lease**
for as long as one caller in this process is running an id: a second `BeginCompositeAsync` on it
checks the verb/session binding first (a mistake is refused immediately rather than parked behind
somebody else's sweep), then waits on the caller's own token and returns the answer the owner stored.
**The lease is in memory, deliberately** — a claim that outlived the process holding it would be a
claim nothing could release, and the row a crash leaves behind must still resume, which is exactly
what "no lease" means after a restart. The owner releases it when it writes the answer and, whatever
else happens, when it disposes the lease the plan handed it. The emergency press does not take one:
its ids carry a freshly minted nonce inside the `op-` namespace the pipe refuses outright, so no
second caller can name one.

**A trading mode this build does not have allows nothing.** `TradingMode` is `OBSERVE`, `PAPER`,
`LIVE_CONFIRM`, `LIVE_AUTONOMOUS`; it is persisted as a name, and the JSON enum converter also reads
NUMBERS and casts one it does not recognise straight onto the enum. A settings row saying
`"mode": 999` therefore produced a mode of 999, and every gate is a comparison against the named
values: 999 is not `OBSERVE` so it executed, not `LIVE_CONFIRM` or `LIVE_AUTONOMOUS` so the
real-money activation switch was never consulted, and not `PAPER` so a real-money account was not
refused either — a mode nobody chose, trading real money with the safety off (REVIEW 2026-09-05,
Codex F3). It is not hypothetical: a newer build writes a mode this one has never heard of, and a
rollback reads it. So `TradeAgentSettings.ModeIsRecognised` gates execution, the health row is
`PAUSED` with that reason on every refresh, and the owner is told in the app's own words that the
saved mode is not one this version knows. **The value is not rewritten** — substituting a control the
owner never chose is its own defect class, and a mode a newer build wrote should survive intact until
they upgrade again.

**Every mutating verb passes the same gates, and a modification is checked on the order as it will
stand.** `place`, `modify` and `cancel` all run the authorization chain; `place` and `modify` also run
every risk limit and both park in `LIVE_CONFIRM`. A `modify` used to run the authorization and
nothing else, so the quantity cap, the notional cap, the open-position limit, the instrument
allowlist and the rate limit did not apply to it and no person saw it in `LIVE_CONFIRM` — a working
quantity-1 order was grown to 1000 against a cap of 1, over the authenticated pipe (REVIEW
2026-09-05, Codex F2). A working order is a live claim on the account, so raising its quantity is the
same act as placing an order of the new size by another name. The limits are therefore applied to the
**resulting** order: the values the change names, and for every field it does not name, the value the
target already has. **It fails closed when the target cannot be read** — `RISK_CHECK_UNAVAILABLE`,
nothing sent — because the resulting size of a price-only change is the size the order already has and
the instrument each limit is applied per is the one the order is on, and guessing either makes a cap
decorative. A `cancel` reduces risk and is never refused for a limit.

**Every gate is evaluated at the moment of dispatch, after the awaited reads.** Authorizing at the
top of a mutating method is a verdict about a moment that has passed: `PlaceAsync` authorized once
and then made four connector reads before touching the wire, so Stop AI trading pressed inside that
window did not stop the order it was pressed to stop — measured, with the switch down and the order
`FILLED` (REVIEW 2026-09-05 finding 6, probe P3; Codex F4). The window is bounded by four
`WorstCaseOperationPath`s, 200 s at shipped ATAS values, and it swallows the kill switch, the
real-money activation switch and an install that started while the reads were in flight. So the whole
chain runs again immediately before the wire, on the place, modify, cancel and approval paths alike —
and **the mode is checked against the record rather than against a list**: a placement authorized in
`PAPER` with the mode moved to `LIVE_CONFIRM` while it read is a record already built as `CREATED`,
past the question of whether a person should see it, and only the mode a record was decided under may
send it.

**Nothing awaited may come between the last gate and the wire — and what is left after it is the
connector's own send, which is a WINDOW that is stated rather than closed.** The re-check used to sit
above the close's stale-position re-read, so a `close` re-authorized and then made ONE MORE awaited
connector round trip before touching the wire: a kill switch pressed inside that read arrived after
the last gate had been passed and the order went out with the switch down, over a window one
`WorstCaseOperationPath` wide — 50 s at shipped ATAS values (REVIEW 2026-09-05b, Codex F5). It is now
the last thing before the wire on all three dispatch paths, with only synchronous work after it.
**What remains cannot be closed, and is not claimed to be.** Once the command is inside
`PlaceOrderAsync` the frame is on its way, and the two levers that could reach into it are both worse
than the window. The gateway ALREADY holds `_dispatchGate` across the whole wire call, so making
`SetMode`, `ActivateLive`, `StopAiTrading` or `Update` take that gate would not stop the order — it
would make the owner's press *wait* for it, up to a full `WorstCaseOperationPath`, which is the wrong
thing to do with an emergency control; the emergency controls are outside that gate deliberately.
Cancelling the send instead manufactures exactly the ambiguity safety rule 3 exists to avoid: a
cancelled write cannot say whether the broker saw the order, so the record settles `UNKNOWN`, trading
pauses on unconfirmed work, and one authorized order becomes an unresolved position. So the bound is:
**an order already handed to the connector when authority is revoked may still reach the broker, for
up to one `WorstCaseOperationPath`; every order that has not been is refused.** A test pins that
answer as an answer, so making the send cancellable cannot happen quietly.

**And a `close` is SIZED at the moment of dispatch too, for the same reason and one more.** A close
is the only placement whose side and quantity are a claim about something that moves: `close` reads
the position, turns it into "sell 2 ES", and then makes those same four awaited reads. A fill landing
in that window turns the close into a new position — closing 2 of a position that is now 1 opens a
short, and closing a long that has already flipped doubles it (Codex F3; the same class as the
emergency press's own drift re-read, reached from the agent's side). So the position is read again
inside the dispatch gate and a close that no longer offsets it is refused with `POSITION_MOVED`:
nothing is sent, the record never leaves `CREATED` — which is what makes a `close-all` leg read
`not-sent` rather than `sent-not-confirmed` — and the caller asks again under a new `request_id`
against what is actually there. **Refused rather than recomputed**, in the words the press has used
since `U-press-atomic`: a different position is a different decision, and recomputing would send a
size no risk check ever saw, because the position can have grown. Only closes pay the extra read; an
opening order asserts nothing about a position.

**The rate limit is an atomic reservation, not a count that is read and spent later.** The place is
taken under one lock immediately before the write-ahead and given back if nothing is sent; committing
it is the last step before the wire. It used to be a count READ in the risk check and an unrelated
add two awaited reads later in the dispatcher, so N callers arriving together all read the same free
count and all took it: `MaxOrdersPerMinute = 1` admitted as many orders as there were callers. The
check in the risk pass remains, as an early refusal that costs nothing — it is advisory, and the
reservation is what bounds the minute.

**The notional cap's multiplier is REQUIRED, and where it comes from has never been measured.**
`MaxNotionalPerOrder` is compared against quantity × price × **contract size**, and on a futures
account that last term is the whole of the number: one ES at 109 is $5,450 of exposure, not $109. The
contract size arrives from `InstrumentInfo.ContractSize`, which the ATAS adapter maps straight from
the SDK's `Security.LotSize` (`AtasStrategyAdapter.ToInstrument`, zero mapped to null). **That
mapping is NOT VERIFIED**: no live futures account has been attached to this build, so nobody has
confirmed that ATAS's `LotSize` is the contract multiplier rather than a minimum order increment or a
board-lot size. It is the only field in the dump that could be it, and it is a guess until a broker is
attached. What the code no longer does is guess the VALUE: the cap used to swallow a failed
instrument read, a symbol the read did not carry and a null or zero `ContractSize` alike, and
substitute `1` — the owner's limit off by the contract size, in the permissive direction, invisibly
(REVIEW 2026-09-05b, Codex F2). When a value cap is set and the multiplier cannot be established, the
order is refused with `RISK_CHECK_UNAVAILABLE` and nothing is sent; the sentence says whether the
platform would not answer or answered without the number. A zero is refused too, because it does not
understate the exposure, it erases it. The multiplier is asked for **only when a cap is set** —
`MaxNotionalPerOrder` is zero by default and that means not enforced, so an installation that set no
value cap is not stopped by metadata nothing is going to multiply.

**The loss budgets are the only limits about what HAPPENED, and they refuse new risk without closing
anything.** `MaxLossPerTrade` and `MaxDailyLoss` are decimals in the ACCOUNT's currency, and **zero on
either means not enforced** — the reading `MaxNotionalPerOrder` has, and the opposite of
`AiDailyCostCap`'s. The day is `TradingGateway.StartOfDay` (UTC), the day `trade pnl` means by
`today`: realized from the fill ledger by the same average-cost book `Pnl` computes, less the fees the
platform DID report, plus unrealized on every open position — the platform's own
`PositionInfo.UnrealizedPnl` where it reports one, otherwise the last price this gateway saw times
the instrument's multiplier. Both budgets are evaluated inside the dispatch gate, off the SAME position read the
open-position cap uses and before `TryCreate`, so a refusal places nothing and writes no request row;
the approval path re-evaluates them, because a proposal can park across a morning the account has since
lost. Only an order that could INCREASE exposure is refused: a declared close never is, nor is an
order against a position and no larger than it — an order LARGER than the position it is against
flips it and is checked, because the surplus is new exposure. Reaching a budget is
`LOSS_BUDGET_REACHED` and not `RISK_LIMIT_EXCEEDED`, because the two have different answers: a
breached order limit is answered by asking for a smaller order and a reached budget by not asking
again today. **Nothing is flattened** — that is `U-flatten`. **An unknown refuses rather than
counting as zero**: a symbol traded today whose multiplier the platform will not state, an open
position with neither a mark nor a price, or one with a price and no multiplier, all refuse with
`RISK_CHECK_UNAVAILABLE` and a sentence naming what was missing — `trade pnl` reports the same gaps in
`incomplete` and prints a figure anyway, which is right for a report and wrong for a gate. **Fills the
platform reported no fee for count at their gross**, so the loss is understated by exactly those fees;
every sentence that shows the figure says how many there were. With both budgets at zero the ledger is
not read and the instrument list is not asked for at all. `status` carries `loss_today`,
`loss_budget_day` and `loss_budget_trade`, each **absent rather than zero** on the `ai_cost_today`
convention: an absent budget is not enforced, and an absent `loss_today` means the figure could not be
worked out. Finally, **a real-money mode cannot be SELECTED while `MaxDailyLoss` is zero**
(`TradingGateway.SetMode`, `INVALID_REQUEST`, the sentence names the field): there is no human in the
loop for real money, so an unattended agent with no bound on the day must not be one button away.

**The open-position cap counts what is on its way to being a position, and is decided inside the
dispatch gate.** `MaxOpenPositions` used to count the positions the platform had already FILLED, read
before the gate. Both halves were permissive in the same direction: two placements arriving together
each read the same empty account, each passed a cap of one and each sent — the cap admitted as many
orders as there were callers — and a resting opening order read as a free account for exactly as long
as it sat on the book (REVIEW 2026-09-05b, Codex F1). The position read therefore moved into the gate
together with the decision, where the dispatch before it has finished, and what counts as one open
instrument is a non-zero position **or** an opening request the store still calls open (`DISPATCHING`,
`ACKNOWLEDGED`, `WORKING`, `PARTIALLY_FILLED`, `CANCEL_PENDING`, `UNKNOWN`, `RECONCILING`). A CLOSING
request is excluded — it reduces exposure and is aimed at an instrument already counted, and counting
it would turn the cap into a trap an account at its limit could not be flattened out of — but a
request whose parameters cannot be read is counted, because an unreadable intent is not evidence that
it was a close. A MODIFICATION is not asked: the cap counts instruments, and an order that already
exists is already in one.

**An approval is a dispatch decision, authorized at the moment it is made.** In `LIVE_CONFIRM` an
agent's order — or its modification — is parked as `AWAITING_APPROVAL` after passing every gate and
refused to the agent with `APPROVAL_REQUIRED`. When a person presses Approve, the gateway makes the
decision again from the start, in this order:

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
   for an order without its own price, and order value multiplied by contract size. A parked
   MODIFICATION re-reads its target from the book at the moment of the press and is judged on the
   order as it will stand **now** — it may have moved, filled or been cancelled while it waited, and
   a target that cannot be read refuses the approval rather than sending on a stale reading.

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

## The council's relay — `src/TradeAgent.Core/Db/PublicationStore.cs`

Two roles (`operations`, the chair; `research`) run one at a time by one app instance. A role writes
a file into its own `out/`; nothing an agent can do publishes, delivers or creates a task. Two tables,
written by the app only — the rule `material`, `ai_attempt` and `mission_event` already keep:

`publication` — `id · role · attempt · revision · kind · recipients · classification · created_at ·
source · content`. `id` is SHA-256 over the role, the kind and the text. That is the whole of what
makes the handoff safe to crash inside: republishing the same content is republishing the same row,
which the primary key refuses. `content` is the app's own copy, because the role's `out/` file may be
rewritten or deleted before the delivery is made. `kind` is `report` (Research → the chair, at most
20 lines) or `brief` (the chair → Research, at most 40); the app rejects a longer one and the last
valid publication stands. `recipients` is the app's decision, never the publishing role's.

`delivery` — `publication_id · recipient · state · created_at · delivered_at`, primary key
`(publication_id, recipient)`. `committed` the moment the transaction lands; `delivered` only after
the file is in the recipient's `in/<publication-id>.md`. There is no state in which a delivery is
claimed and no file exists.

**One `Database.Write`** commits the publication, its deliveries and ONE `mission_event`
`task:<publication-id>` (`MissionEventIds.Task`, suffixed per recipient by `ForRole`) of kind `report`
or `brief` for the recipient. The copy onto disk comes after. `CouncilRelay.Run` reconciles disk
against the tables on start and after every turn: an unpublished file is published (idempotent by
hash), a committed delivery with no file is re-copied from `content`. **The property:** a crash after
the file, after the transaction or after the copy recovers to exactly one task and one file — never
zero, never two.

`role` on `ai_attempt` and on `mission_event` is nullable and additive; a row that names none is the
chair's (`CouncilRoles.Or`), because the single agent the council replaces was Operations.

## A turn's staged output, its revisions and its one commit — `src/TradeAgent.AgentRuntime/CouncilRelay.cs`

**A file is attributed by the attempt id in its NAME.** The `## Situation` names the launch id and the
agent writes `out/report-<attempt>.md` or `out/agenda-<attempt>.md`; the relay reads the id back out.
A file whose id is not in `ai_attempt`, or names a launch made for another role, or names one still
`LAUNCHED` that is not the pass's own turn, is MOVED to `out/quarantine/` with an activity line and
never published. `ENDED` and `LOST` both publish: a killed turn's report is the work of THAT turn, and
attribution by whichever launch happened to run the pass named the wrong one.

**`publication` also holds each role's own memory, versioned.** `kind` `plan` (`trading/PLAN.md`, at
most 60 non-empty lines) and `journal` (`trading/JOURNAL.md`, at most 200; older entries go to
`trading/archive/`), `recipients` = the role itself, `classification` `private`. No delivery and no
`mission_event` come with them — `PublicationStore.Record`, not `Commit` — because a role woken to read
its own plan is the owner paying for the app to hand an agent its own memory back. An unchanged file
raises no revision (the id is the content hash). An over-cap or unreadable file is REJECTED, the last
valid revision is written back over it, and the role's next `## Situation` says so. `revision` is
assigned inside the transaction that inserts the row. `ix_publication_kind (role, kind, revision)` is
what the restore reads; it is the whole of schema 11.

**One `Database.Write` per turn** (`CouncilRelay.CommitTurn`, `IMissionHost.CommitTurn`): the launch
record closed (`AiAttemptStore.End`), what the turn published, its two revisions, and the dispositions
of the wakes it consumed. `Database.Write` is re-entrant — a nested call joins the transaction already
open rather than starting a second. The disk work is after the commit: the copy into `in/`, and the
restore of a refused plan. **The property:** a crash either side of it leaves exactly one revision per
file and the attempt in one identifiable state — `LOST` (nothing of the transition landed; the next
meter to open the database turns every open row `LOST` and the reconcile pass publishes that attempt's
files under it) or `ENDED` (all of it landed) — never an `ENDED` attempt whose work is nowhere, and
never a wake answered by a turn the ledger did not close.

**The material pass walks every role's home** (`MaterialScanner.RolePaths`): each role's tracked
folders plus `in/` and `out/`, all under one `agent` origin so `MarkMissing` never invents a deletion.
What a role was handed and what it published are measured facts in `material`, which the agent cannot
edit, beside the relay's own record of the same artifacts.

## The owner's daily report — `src/TradeAgent.Gateway/DailyReports.cs`

One file per LOCAL calendar day at `state/reports/<yyyy-MM-dd>.md`, LF, at most 120 lines, written by
the app and by nothing else — the rule `material`, `fill`, `ai_attempt`, `mission_event` and
`publication` already keep. `trade report [--day yyyy-MM-dd]` serves it; there is no op and no verb
that writes, rewrites or deletes one, because it is the record the AI's own work is judged by. The
account owner presses **Write it now** on the Daily report page; the background loop writes any day
that has passed with no file, so a laptop asleep at midnight still gets one.

**Composed from ONE snapshot, and nothing in it is inferred.** Ten sections
(`docs/COUNCIL.md` rule 10): identity · mission state · trading readiness · capital and performance ·
execution health · AI spending · other operating costs · research evidence · decisions and changes ·
recovery and next work. Every field is nullable-and-labelled and **never defaulted**: a figure the app
could not work out is `null` in the reply, a `—` in the document, and a named line in `missing`. The
sharpest case is `net`, which is withheld whenever any fill that day carried no fee — a net computed
as though an unreported cost were zero is wrong in the owner's favour every time.

**No paid turn and no connector call.** No `mission_event` is raised by writing a report: every row in
that queue is a reason to spend the owner's money, and a document the app already finished writing is
not one. That is also why `report` sits in the handler table at `TimeSpan.Zero`. The price is stated
rather than hidden: `exposure`, `unrealized` and the remaining loss allowance are absent, with a
`missing` line saying the report asks the platform nothing — `pnl` is the op that does.

**An over-long draft is refused by the app itself.** A rendering past 120 lines is not trimmed: the
file becomes a refusal naming the line count, and `rejected` carries it. The same rule the relay
applies to a role's publication, applied to the app's own output.

**The Operations Director's note is beside the facts, never inside them.** A `publication` of kind
`note` by `operations`, looked up BY DAY AND BY KIND; a day with none says so. Nothing in this build
produces one — it is the one paid thing near this page and exists only when an app predicate changed.

**`mission_event.disposition` and `disposition_detail` (schema 10).** `answered` and `failed` are what
a TURN made of a wake. The other three are outcomes the app reaches WITHOUT one, and
`MissionEventStore.SettleWithoutTurn` refuses the first two for exactly that reason: `delegated` (the
launch that consumed the owner's message published a brief — the detail is that publication's id, a
measured co-occurrence and never a reading of either text; `Settle` will not overwrite it, because the
relay runs before the loop settles), `blocked` (nothing could take it and the detail is why; the row
stays UNCONSUMED, so the message is still owed a turn) and `superseded` (the same words arrived again
before anybody looked, and the detail is the later event's id — EXACT text only, because "the same
subject" is a judgment this software is not entitled to make).

**The deadline is a line, never a wake.** `OwnerReplyDeadlineHours` (default 24) from the moment a
message was received; the report lists the day's messages plus any older one still unsettled and past
due, marked `OVERDUE`. It buys no turn: priority a task claims for itself is what rule 7 forbids, and
a backlog that manufactured wakes would spend tomorrow's allowance at midnight.
