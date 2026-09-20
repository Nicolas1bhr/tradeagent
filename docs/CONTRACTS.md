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

## The paper connector — `src/TradeAgent.Connectors.Paper/`

**A paper fill is a declared simulation and never evidence.** The `paper` connector takes closed bars
from an `IPaperBarSource` and settles orders against them in the backtest's order: a MARKET order
placed at `t` fills at the OPEN of the first closed bar whose `open_time` is after `t`, plus adverse
slippage; a STOP fills at that open when the bar gapped through its level and at the level otherwise;
a LIMIT fills at its level; a bar that would trigger both a resting stop and a resting limit on one
instrument counts as the **stop**, because bars carry no intrabar ordering and the other reading
invents a winning trade. What that establishes is that the price existed at the open of a bar. It
establishes **no actual fill, no queue position and nothing about whether an order of that size could
have been traded there** — the same sentence `docs/COUNCIL.md` puts on a backtest, for the same reason,
and it does not become execution evidence because the bars were recent.

**Friction is DECLARED and zero says so.** `TradeAgentSettings.PaperFeeFraction` and
`PaperSlippageFraction` are fractions (`0.001` is ten basis points) and both default to 0. Declared
neither, every fill the connector writes carries the word **FRICTIONLESS** and the sentence that a
cost of nothing was modelled rather than measured, and the connector's status line repeats it. Slippage
is adverse and applies only to a market order's fill at the open; protection fills where protection
fires. Sizes are rounded **DOWN** to the instrument's quantity increment and a size that rounds to
nothing is refused. Neither number is a risk limit: nothing is refused by them and no cap moves with
them, so they ask once.

**It trades the venue catalogue's VERIFIED rows and nothing else.** The instruments come from
`VenueCatalog` — every venue but TradeAgent's own simulator — filtered to `verified = true`, which is
`Backtests.Increment`'s judgement applied to the same number: a size is rounded down to the increment,
so an increment nobody confirmed against the venue's own definition would make every simulated position
one that could not have been taken. Out of the box that list is **empty**, because Binance spot's
BTCUSDT ships unverified; the account owner records the row in `venues.json` and it trades.

**The book is its own SQLite file, at `state/paper-<account>.db`, versioned inside the file.** It is
not a rung of the app's schema and must not become one: simulated fills one join away from real ones
is the shape that makes a paper experiment a migration. Orders are keyed by **client order id**, fills
are keyed `UNIQUE (client_order_id, bar_open_time)`, and the positions are average-cost. Every SDK read
— orders, executions since a timestamp, positions, and the account whose equity is starting + realised
− fees with an unrealised figure off the last closed bar or NULL — comes off that file, so a second
instance over it answers the same book. **Re-processing a bar writes no second fill, and that unique
key is the whole of the guarantee**: settlement replays whatever the source hands it and takes the
constraint's answer, because a watermark is right until the process holding it restarts and a restart
is precisely what this has to survive.

**What it cannot do, by construction.** The assembly references `TradeAgent.ConnectorSdk` and
`TradeAgent.Core` and nothing else — no HTTP client, no socket, no vendor SDK — and a test reads the
reference list back and holds it there. `SupportsClientOrderId` and `SupportsOrderHistory` are true
because the id is the book's primary key and nothing prunes either table, not because paper fills are
harmless. `SupportsStreaming` is **false**: a price exists here only when a bar closes, and a stream
repeating the last close would be inventing ticks. A `ConnectorRejectedException` is a definite refusal
only — an instrument the catalogue does not hold verified, a size below the increment, a cancellation
of an order that has already filled — and an I/O error on the book or a bar source that throws
propagates, so the gateway records UNKNOWN and reconciles rather than reading a broken read as a no.

**Which connector an id names is decided in one place**, `Connectors.Create` in
`src/TradeAgent.Platforms/`, for the desktop app and the gateway host alike; an id neither recognises
still falls back to the practice simulator, which is the one platform where being wrong costs nothing.

## `IAgentRuntime` — `src/TradeAgent.AgentRuntime/IAgentRuntime.cs`

Detect · Install · Update · GetVersion · BeginAuthentication · GetAuthenticationState ·
CreateEnvironment · Start · Stop · Restart · ExecuteTask · GetHealth · Capabilities.

`CliAgentRuntime` implements all of it from a `RuntimeManifest`, so OpenCode, Codex and anything later
are the same code with different data. Runtime-specific awkwardness stays inside the manifest.

## Containment — `src/TradeAgent.AgentRuntime/Containment.cs`, `src/TradeAgent.Security/AgentGrants.cs`

`U-containment` (2026-09-12). Four properties, and what each does NOT cover:

- **The agent process is held.** Windows: a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, one
  per turn, the child assigned immediately after `Process.Start` (not started suspended — that needs
  raw `CreateProcess`, and re-implementing redirection and `CreateNoWindow` to close a one-syscall race
  is the wrong trade). Breakaway is **not** permitted: neither `BREAKAWAY_OK` nor
  `SILENT_BREAKAWAY_OK` is set. macOS/Linux: the child is started through TradeAgent's own `trade
  --spawn-contained`, which calls `setsid()` then `execv()`s the real command, so the pid the app holds
  is the session and group leader and the cancel is `kill(-pid)`. Measured: `Process.Start` alone leaves
  the child in TradeAgent's OWN group, and `setpgid` from the parent after exec fails as POSIX says.
- **The environment is a whitelist**, `AgentEnvironment.PassThrough` plus the runtime manifest's
  `KeepEnvironment` plus TradeAgent's own — `psi.Environment.Clear()` first, because that dictionary
  starts as a copy of the app's.
- **The pipe knows which launch is calling.** One grant per launch (role, attempt id, expiry), minted
  in `AgentSession` and handed over in the child's environment ONLY, presented in `hello` beside the
  machine token, and stamped on `AgentContext.Role` / `AttemptId`. A grantless caller is authenticated
  and **roleless**: it may read and `Ops.IsMutating` is refused `ROLE_MAY_NOT_TRADE`. Research's grant
  is refused the same way. A grant that is unknown or expired is refused at `hello`, one chance per
  connection. Where a `PeerRule` is configured, a caller PRESENTING a grant must also be the app's own
  `trade` under `Paths.Bin` with the hash `ToolDeployer` recorded, and never under `Paths.Tools` or
  `Paths.Workspace` — the two folder clauses come FIRST, ahead of the recorded path, so a runtime that
  rewrote the record cannot satisfy it.
- **The protected configuration.** While `ModeIsLive && LiveActivated` and no OS sandbox reports OK
  (none can: `Containment.Sandbox()` returns `NONE` on every platform), no AI runtime is started at
  all — `CONTAINMENT_REQUIRED`, said on the Doctor's `AI containment` row and in `docs/USER-GUIDE.md`.
  `ModeIsLive` alone does not refuse: a mode chosen and not armed sends no order anywhere.

**What stays open, and it is `U-contain-2`:** the agent runs as the same OS user, so `state/` —
the database, the datasets, the reports, the recorded CLI hash — is readable AND writable by it. A job
object is not a sandbox and the product says so in the owner's words rather than implying otherwise. The
candidates are an AppContainer with brokered file access on Windows and a sandboxed helper on macOS,
neither verified for a vendor CLI; the honest alternative is harness-only execution for every role
(`docs/COUNCIL.md`, round 4). Also open: the Unix session can be escaped by a process that calls
`setsid` itself, which any scripting language on the machine can do in one line.

## U-grant-liveness — a grant is worth what it is worth NOW — `src/TradeAgent.Gateway/GatewayPipeServer.cs`

REVIEW 2026-09-16 finding 5. The grant above was verified once, inside the `hello` arm, and the role it
proved was carried into every later frame on that connection. So an EXPIRED, REVOKED or turn-ended
grant kept placing orders for as long as the caller held the socket, while a NEW connection presenting
the same grant was refused `IPC_UNAUTHENTICATED` (probe `C1`: all three, one order each at the wire).
Revocation that does not reach a live connection is not revocation.

**Every frame re-verifies the grant the connection PROVED**, against the same register, with the same
code and the same words a fresh connection presenting that token gets. Against the token proved at
`hello` and never against `req.Grant`: a token read off a later frame is one the caller asserts, and
re-reading it there would let a peer swap in another live grant per frame and be whoever it liked.

**An ending reaches the socket.** `AgentGrants.Ended` (`src/TradeAgent.Security/AgentGrants.cs`) is raised for every way a grant stops being live
— revoked, turn ended, or expired and pruned — and the pipe server closes the connections holding that
token. It is raised outside the register's lock, may be raised twice for one token (the turn ending,
then the expiry a minute later), and a subscriber that throws does not stop the next one. The server
subscribes in `Start` (`Grants` is an `init` property and is unset in the constructor) and unsubscribes
in `DisposeAsync`, because `AgentGrants.Shared` outlives every server.

**The 60 s `Grace` is for a call already in flight and for a `trade` launched a moment before the turn
ended — nothing else.** A connection inside a call is marked rather than cut, answers that frame, and
closes on the way out: cutting it would report a failure for an order that may already be at the
broker. A FRESH connection is still served inside the grace, and `GrantLivenessTests` asserts that in
the same breath as the closure, so the fix cannot quietly delete the grace.

Four choices this unit made where the brief was silent:

- **A frame on an ended grant is refused outright, not downgraded to roleless.** A fresh connection
  presenting that grant is refused before it can read; a socket that happens to be open already must
  not mean more. So reads go too, and the connection closes after the refusal — one chance, exactly as
  `hello` gives.
- **The peer-image rule is not re-run per frame.** The process on the other end of an accepted pipe
  cannot change, so its answer cannot either, and re-running it would hash the `trade` image off disk
  in the path of every order.
- **Natural expiry is enforced on the frame, not by a timer.** Nothing runs at the moment an expiry
  passes; `Ended` fires for an expired grant whenever the register is next pruned. A socket holding an
  expired grant is answered `IPC_UNAUTHENTICATED` on its next frame either way, so a timer would buy
  the closing of an idle socket that can no longer do anything.
- **`Revoke` raises `Ended` whether or not this register held the token**, so an owner revoking twice,
  or revoking against the wrong register, still reaches every connection holding it.

**Not this unit, and unchanged:** the in-process harness worker (`CallAsync`) presents no grant and has
none to end — its identity is the one the composition root assigns it, and its turn ends with the
conversation. No new permission reaches the agent-facing pipe: the only thing added to it is a refusal.

## Gateway IPC — `src/TradeAgent.Core/Protocol.cs`

Newline-delimited JSON over a named pipe, one object per line, 1 MiB cap **counted in bytes on the
wire**. A frame past it is not answered: the peer is dropped, the way a peer that stops reading is,
because finding the end of an unbounded frame in order to reply to it is the thing the cap forbids.
The count was `StringBuilder.Length` — UTF-16 chars after decoding — until 2026-09-05, so a legal
frame of CJK text was accepted at 2.6x the stated cap and held whole in the server: measured at
2,700,096 bytes. It is the only bound on what a peer can make this server hold, and the read runs
BEFORE the `hello` check, so the peer that spends it need not have authenticated (finding 10).

```jsonc
// request  — `grant` is the per-launch grant (U-containment); absent means a roleless caller
{"v":1,"id":"...","op":"buy","token":"...","grant":"...","session":"agent-...","request_id":"...","args":{...}}
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
  (`orders`: `true`/`false`, **default `false`**), `origin` (`material-list`: `inbox`,
  `inbox-unattested`, `agent`, `app`, `all`, **default `all`**) and `kind` (`material-note`: `ran`, `used`, `derived`, `note`, **default
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

**The heading was over-stated until `U-review-med`, and the OWNER'S OWN NUMBERS were what it missed.**
What ran again at the wire was the authorization chain, the record's mode and the four gates that
need the position reading; the instrument allowlist, `MaxOrderQuantity`, `MaxNotionalPerOrder` and
the quote-age rule were decided in `RiskCheckOrThrow` above the gate, so an owner narrowing a limit
on the Safety page while an order sat in the gate's position read was answered after the order had
gone — measured, `ES Buy 5` at the broker off an allowlist it had just been taken off (REVIEW
2026-09-16 finding 7, probe P2). Those four are now re-decided inside the gate, on the place and the
approval paths, by `PerOrderLimitsAtDispatchOrThrow`: see `U-review-med` for what it does not re-ask
and why.

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
again today. **Nothing is flattened** — that is `U-flatten-2`. **An unknown refuses rather than
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

**A confirmed breach is a durable record, and the day it closes is closed until the next UTC day**
(`U-flatten-1`). Both budgets used to be evaluated only when an order arrived, so a breach refused
that one order and remembered nothing: a loser closed at a smaller realised loss reopened the day, a
restart reopened it, a widened budget reopened it, and a book bleeding while the AI sent nothing was
never measured at all. Now `TradingGateway.LossWatchAsync` measures both budgets on a tick —
`GatewayOptions.LossWatchInterval`, **15 seconds**, riding the health pass, with an arriving
`QuoteChanged` scheduling one coalesced evaluation sooner — pulling a FRESH quote per open-position
symbol outside the dispatch gate and evaluating and writing under it, so a breach and an opening
order cannot race. A mark is the **executable** side (bid for a long, ask for a short), younger than
`MaxQuoteAge`, from the current connection epoch; an unavailable valuation denies new risk with
`RISK_CHECK_UNAVAILABLE`, as it already did, and records NOTHING. A breach is recorded only when a
SECOND, DISTINCT pull inside `GatewayOptions.LossBreachConfirmWithin` (**60 seconds**) agrees — the
admission gate still refuses on first sight, with no tolerance, and records nothing by itself,
because refusing one order is cheap and reversible and closing a day is neither. The record is
`loss_breach:{account}:{utcDay}` in the app-owned `kv` table (and `loss_breach:{account}:{symbol}:
{utcDay}` for the per-position budget), written once with its evidence — both limits, a hash of the
risk policy as the settings revision, the ledger figure and its missing-fee count, every open
position with the mark used and where it came from, the connection epoch, both pull numbers and both
instants — and **never updated**. `LossBudgetOrThrow` and the approval path refuse `LOSS_BUDGET_REACHED`
off that record **before reading anything**, and ahead of the zero check, because a budget set to
zero after a breach is the widest widening there is. A closure also opens ONE `BoundaryKind.LossBudget`
boundary per `{account}:{utcDay}` through `CouncilBoundaries.Open` — both directors woken once, default
disposition `hold` — so repeated refusals cannot manufacture senior spend. **Nothing is sent**: no
close, no cancel, no order leaves the gateway because of a breach, and every surface says NOTHING WAS
CLOSED FOR YOU. `status` carries `loss_day_closed_at` and `loss_symbols_closed`, absent rather than
zero. **Three choices here are the account owner's to overrule, and are recorded as choices**: the day
reopens AUTOMATICALLY at the next UTC midnight (the alternative is "closed until the owner has read
it", which needs a press this build does not have); the record lives in `kv` rather than a table of
its own until `U-protect` gives the protections one; and the two intervals above are judgments rather
than measurements. There is no verb, no pipe op and no setting that reopens a day.

**A confirmed breach CLOSES WHAT IS OPEN, by code, and the app is the only thing that may do it**
(`U-flatten-2`). `U-flatten-1` closed the day and sent nothing, and every surface in this product
promised exactly that; a budget about what has ALREADY been lost that leaves the losing position open
goes on losing, so the promise is withdrawn and the sentences that carried it are rewritten. The
flatten runs from `TradingGateway.FlattenForBreachAsync`, called by the watch immediately after the
record is written and OUTSIDE the dispatch gate, and there is no verb, no pipe op, no setting and no
button that reaches it. **Operator authority is unchanged**: the app is not asking for permission, it
is doing the one thing the owner already bounded when they set the budget.

**It is a REDUCTION-ONLY exception to two-press, and the exception is enforced in code and not in
this paragraph.** Two new app-owned press kinds, `op-budget-cancel-` and `op-budget-close-`, join
`op-close-` and `op-cancel-` in `IsPressRecord`/`PressKindOf`. They keep the whole of the press
machinery — the flagged write-ahead row before the wire, the per-kind claim, the settle-before-send,
the drift re-read, `RiskReducingScope` for one absolute deadline, the composite plan — and they add
one thing: `TradingGateway.ReductionOnlyOrThrow`, checked against a FRESH read of the position taken
after the write-ahead row and immediately before the call. An order that does not oppose the position,
is larger than it, or meets a flat book **throws and nothing is sent**; the row is left REJECTED with
the sentence, and the leg is reported. Only the app's legs take that last look: the owner's press is
byte for byte what it was, because a person pressing a button has looked at the account and can press
again, and an app leg has nobody. The two kinds are SEPARATE from the owner's so that an unresolved
app flatten can never refuse a person pressing Close all positions; what it does refuse — in words,
by the existing rule — is the owner's leg on the one instrument it is still holding an order for.

**Openers first, and the cancels must have SETTLED before a single close goes out.** A flatten that
closes the position and leaves a working buy on the book has flattened nothing: the opener fills a
second later into a day that is now closed to everything that could hedge it. So every working order
that could increase exposure is cancelled first — `CouldIncreaseExposure`, which is
`CanIncreaseExposure`'s question asked of an order on the book — **and so is every working order on an
instrument the flatten is about to close, including a reducing one**: a protective sell is a reducer
only while the long exists, and the moment the flatten removes it that same order is an opener. That
second set is this unit's choice and is the owner's to overrule. The working book is then read again
and every target has to be gone from it; if any is still working, or the read fails, **no close is
sent at all**, the press row is left flagged and the record says which order stopped it. A per-symbol
breach touches that symbol's orders and that symbol's position and nothing else; **the daily scope
subsumes a symbol's**, so a day already flattened writes no second set of closes, and neither closure
is cleared early by the other.

**Resolved by MACHINE, or flagged and pausing, and terminal is not flatness.** A leg is resolved when
its record is FILLED **and** a fresh read of the position says flat — both halves, on a read-back
that is the only statement about the platform's book this app ever makes. A REJECTED or CANCELLED
close is terminal and closed nothing; a composite answering ok is this app agreeing with itself.
A resolved leg has its flag cleared by `ClearTheFlatteningFlag` — which writes no state, only lifts
the flag the app itself wrote behind evidence the app itself checked — and when every leg resolves,
the app's own press rows are cleared too and `ExecutionCapability` goes back to READY on
`ReconcileAsync`'s own two lines. Anything else keeps the flag, pauses all order flow exactly as an
owner's press does, and waits for a person. **`LOSS_BUDGET_REACHED` is still the caller's answer**,
still refused off the record and never off the figure.

**The outcome is its own write-once record and the breach row is NEVER updated.** `loss_flatten:
{connector}:{account}:{utcDay}` in `kv` (and `…:{connector}:{account}:{symbol}:{utcDay}`), carrying
both press nonces, the cancelled orders, the openers that would not settle, every leg with its state,
fill and read-back position, the residual, `Flat`, and one sentence. The breach row is the
MEASUREMENT and this is the CLAIM about what was then done: a record the app appends to is a record
the app can rewrite, which is the rule the material ledger already follows. **The key carries the
connector** — an account id is unique only within a platform and switching platforms builds a new
gateway over the same database, so a PAPER account's flatten must never be able to answer for a LIVE
closure; that is a deviation from the brief's `loss_flatten:{account}:{utcDay}` and is deliberate. The
account is read off the BREACH record and the flatten refuses outright unless the account this
gateway is actually operating is that account: none is ever the "currently selected" one implicitly.

**A closure with no outcome beside it is a flatten that was killed, and it re-runs** —
`ReFlattenClosuresWithNoOutcomeAsync`, on the health pass every host already runs immediately after
connecting, so that IS the startup path. It is keyed on the ABSENCE of the outcome record and never
on the composite: a composite is written BEFORE the first close, so a run killed among its legs
leaves one behind and keying on it would decide that a flatten which never finished had. It **never
runs while anything is unreconciled** (the startup sweep's rule): the rows a killed flatten leaves are
flagged and UNKNOWN, and closing over one is how a long 2 becomes a short 2. It is on the health pass
rather than on the watch's own interval because every reason the watch has to do nothing — an
unvaluable book, both budgets since set to zero, a failed pull — is not an answer about a closure
already on disk.

**Told, on every surface, in two words.** `status.loss_flatten` is `flat` or `unresolved`, ABSENT
while nothing has been flattened, on the `ai_cost_today` convention; the Situation, the Safety page's
own row under the closure, section 4 of the daily report (`positions closed for you`), `AGENTS.md` and
the guide all carry the flatten record's own sentence rather than a paraphrase. `flat` is the only
claim this product makes about the platform's book and it is only made behind a read-back;
`unresolved` means go and look. **Owner-overrulable, and recorded as choices**: cancelling the
reducing orders on an instrument about to be closed; the connector in the key; the app clearing its
own flags behind a read-back (the alternative is a person confirming every correct flatten, which
makes a breach a manual outage); and the flatten running on the watch's thread rather than on a queue
of its own.

**A CLOSURE IS A STATE, NOT A KEY, AND IT ENDS ON A RECEIPT TRADEAGENT WRITES AFTER LOOKING**
(`U-reopen-1`). `U-flatten-1` filed a breach under `loss_breach:{account}:{utcDay}` and every reader
asked for TODAY's key, so a closure ended when the key went out of scope: a breach confirmed at
23:58Z was two minutes of pause on an account that had just been flattened, with nothing checking
that the flatten had resolved, that the book was flat, that the clock had moved forward honestly, or
that any time had passed at all — and nothing recorded that a reopen had happened, because nothing
had. Now `DayClosed`, `SymbolClosed` and `ClosureToday` SCAN `loss_breach:{account}:` across every
day, and a scope is closed from the record's `ConfirmedAt` until a RECEIPT exists for that record.
The breach key is unchanged. **While a scope is closed the watch writes no further breach record for
it** — one episode, whatever day the tick is on, and one `BoundaryKind.LossBudget` boundary with it;
a second breach on the next UTC day is the same event still going, and the flatten's own fills after
midnight are not a new one. Every flatten key is now taken off the BREACH's day rather than the
clock's (`LossFlatten.KeyFor`), so a flatten that runs, is swept or is reported on after a midnight
still names the row it belongs to.

**Eligibility is computed from the immutable record; the reopen is a positive act.** `EligibleAt` is
the LATER of the first UTC midnight after `ConfirmedAt` and `ConfirmedAt + GatewayOptions.LossMinClosure`
— **24 hours**, fixed in this build and **the account owner's to overrule** (`U-reopen-2` makes it a
setting with a floor; both terms are kept because a shorter one must still not end a closure inside
the UTC day whose ledger figure closed it). The receipt `loss_reopen:{connector}:{account}:{utcDay}`
(and `…:{symbol}:{utcDay}`, both carrying the BREACH's day and symbol) is written ONLY by the watch
tick, under the dispatch gate, **write-once at the SQL layer** — `Database.AddKvOnce`, `INSERT … ON
CONFLICT DO NOTHING`, answering whether it inserted — with its evidence: the eligibility instant and
the closure length it was computed with, the instant it was written, the clock high-water mark, the
connection epoch, the positions read for the scope and what the flatten said. **The admission gate
never writes it**: the gate is the one piece of code an agent can reach, so it only ever READS, and a
restart after eligibility but before the tick admits nothing. The key carries the CONNECTOR for
`U-flatten-2`'s reason — a PAPER reopen must never answer for a LIVE closure.

**Flatness is fresh evidence, never a terminal state.** The tick writes a receipt only when, on that
tick: the position read is from the CURRENT connection epoch and shows nothing open on the scope; the
flatten record for that breach reads `Flat` with no opener left unsettled, where one exists (a breach
confirmed with nothing open needs none); no `op-budget-close-`/`op-budget-cancel-` press is
unresolved and `HasUnconfirmedWork` is false; and — inherited, because the reopen runs inside the
watch's measured branch — the book could be valued at all, so `RISK_CHECK_UNAVAILABLE` stays
independently blocking while a budget is enforced. A daily closure and a symbol closure compose by
AND: each has its own receipt and its own scope, and the account-wide press conditions apply to both.
**A clock that MOVED never reopens anything, whichever way it went**: every tick that runs while
something is closed raises `loss_clock_high_water:{connector}:{account}` (seeded with the breach),
eligibility also requires `Now` at or above it, and a reading below it writes
`loss_clock_suspect:{connector}:{account}` ONCE, refuses, and says so on every surface. A step
FORWARD is answered by the same row and the same refusal — see `U-review-med`, which is where the
mark's second reading, the restart penalty and what the direction is called are stated.

**A parked proposal that predates the breach dies with it**, with its own code
(`APPROVAL_PREDATES_LOSS_BREACH`) and its own sentence, never `LOSS_BUDGET_REACHED` — and the request
is DECLINED rather than left on the Dashboard. It is compared to the latest breach's `ConfirmedAt`
for the account scope or the intent's symbol, checked above the mode and the budgets, and it holds
AFTER the reopen too: the receipt says the account may trade again, not that the flatten un-happened.
A proposal written DURING a closure is untouched — it was written by an AI that could already see the
closure, against the book as the flatten left it.

**Told, and the old sentences withdrawn.** `status` carries `loss_reopens_at` (the EARLIEST instant,
ABSENT when something other than time is holding it), `loss_reopen_held` (what, and none of them are
fixed by waiting) and `loss_reopened_at` (ABSENT whenever anything is still closed, so it can never
read as permission). The Situation line, the Safety row and section 4 of the daily report carry the
same three, the report adding a `reopens` line under the closure and a `reopened` line in the
receipt's own words. Every sentence that said a closure lasts "until the next UTC day" or "until
midnight UTC" is rewritten — the breach record's own sentence, the flatten's, the schema the agent
reads, `AGENTS.md`, the guide and `LOSS_BUDGET_REACHED`'s next step — because that promise was the
defect. The reopened day's loss figure is its own UTC date's ledger, flatten fills included: the
FIGURE still starts again at midnight and the CLOSURE does not, and they are deliberately different.

**Owner-overrulable, and recorded as choices** (`U-reopen-1`): the 24 hours; keeping the names
`ClosureToday` / `SymbolsClosedToday` / `FlattenToday` although they now answer across days; the
clock mark being kept only while something is closed rather than on every tick; the surfaces judging
"held" only from rows this app wrote, never from a platform read, so `loss_reopens_at` is the
earliest and never a promise; `LatestBreach` SKIPPING a breach row it cannot parse (so one rotted row
from an episode that ended cannot make an account unable to approve anything ever again) while
`OpenClosures` still THROWS on an unreadable STANDING closure; and the watch reading the platform on
a tick when both budgets are zero but a closure stands, which is the only way an owner who zeroed the
budgets after a breach is not closed for ever. A breach row is still KEYED by the account and not
the platform, so on a switch to a platform carrying the same account id that account's closures and
the approval rule above still apply — refusing new risk is the safe direction on any platform. What
the record now also CARRIES is the platform and the mode it was measured on, and that is what the
flatten is bound by: see `U-scope-identity`.

**A SCOPE THAT REACHES THE BUDGET TWICE IN THE WINDOW IS NOT REOPENED BY CODE AT ALL**
(`U-reopen-2`). `U-reopen-1` reopens every closure once it has earned it, which is the right answer
to ONE bad day and the wrong answer to a run of them: a strategy or a market going through the
owner's budget repeatedly would be handed one daily budget a day, for ever, with nobody ever asked
whether that is what should be happening. So at CONFIRMATION — inside `Close`, under the dispatch
gate, in the same pass that writes the breach — the scope's earlier breach records inside the strike
window are counted, and a second one writes `loss_hold:{connector}:{account}[:{symbol}]:{utcDay}`
through `AddKvOnce`, naming every episode it counted and the window it counted them in. A held scope
has NO eligibility instant: `HeldBy` answers the hold above the arithmetic, the tick writes no
receipt, and `status.loss_held_for_review` says so.

**The count is taken once and written down, and that is the guard.** A window evaluated when the
closure is asked to lift is a window the closure itself moves — the scope sits shut, the dates go by,
and the earlier breach drops out of the count that was about it, so the worse the run the sooner the
software stops noticing. Everything after confirmation reads the ROW. Each scope counts once per
incident: the account's own closures and one instrument's are different keys, so a day on which both
budgets go through is one incident for each and neither is the other's second strike; a restart or a
duplicate pull adds none, because a breach key is written once and the watch records nothing further
for a scope that is already closed. A breach row that cannot be PARSED is still counted, through its
key — dropping it would let a rotted row buy an extra strike — and an unreadable HOLD row answers
held, because a scope whose history cannot be read is a scope a person has to look at.

**The way out is the owner's, in process, two-press, with a required note.**
`TradingGateway.ReleaseHold` writes `loss_release:{connector}:{account}[:{symbol}]:{utcDay}` once,
with the note and the episodes acknowledged, and it is called by a *Reopen after review* card on the
Safety page and by NOTHING else: no `trade` verb, no pipe op, no setting — `CLAUDE.md`'s rule that
operator authority is in-process only, applied to the one permission this unit adds. The note is
required because the row is the durable trace of a person overruling the software's own refusal, and
a blank one is refused in words having written nothing. **The release lifts the HOLD only**: the
receipt still needs the eligibility instant, the fresh flat read, the settled flatten and the honest
clock. It takes no dispatch gate — it admits nothing by itself and `AddKvOnce` is atomic, so the
worst a race with a running tick can do is leave the release to be noticed fifteen seconds later.

**BOTH DURATIONS ARE THE OWNER'S NUMBERS, NARROWING IS TWO-PRESS, AND EVERY RECORD SNAPSHOTS WHAT
APPLIED TO IT.** `RiskPolicy.LossMinClosureHours` (24) and `RiskPolicy.LossStrikeWindowDays` (7) are
on the Safety page under the two budgets. They are the only fields on that page where the SMALLER
number is the grant — a shorter pause is more days the AI may lose a budget in, a shorter window is
fewer episodes that ever add up to a hold — so `RiskPolicy.Widenings` names a shortened one and
`BuildSaveLimits` arms the second press, and lengthening either saves in one. Zero is the widest
value each has: no closure beyond the UTC day, and no strike rule at all. **Eligibility comes from
the record's own snapshot and never from the live setting**: `LossBreachRecord.MinClosure` and
`.StrikeWindowDays` are written at `Compose`, `LossReopenRecord` carries both, and a closure narrowed
after the event cannot bring its reopen forward while one widened after it cannot delay a receipt
already written. **A record written before this unit carries no snapshot and is judged by the FIXED
defaults** (`GatewayOptions.LossMinClosure` 24 h, `GatewayOptions.LossStrikeWindow` 7 dates) — the
rule that was in force when it was written. Both fields are NULLABLE for that reason: a zero from an
absent JSON field is indistinguishable from an owner who switched the rule off.

**THE DIRECTORS' HOLD IS BOUNDED, OR IT IS NOTHING.** A closure opens a `BoundaryKind.LossBudget`
boundary whose default disposition is `hold`, and `CouncilBoundaries.ApplyDue` writes that default
onto the row when the deadline passes; nothing in the protocol ever revises it. Reading that `hold`
as holding the closure would be a closure with no end at all — an account shut for good because two
directors said nothing — so **no line of `ExtensionFor` looks at `BoundaryRow.Disposition`**. The
only thing that can move an episode's eligibility instant is a
`loss_extend:{connector}:{account}[:{symbol}]:{utcDay}` row, and four bounds are checked where the
instant is used rather than assumed of whoever wrote it: no `Until` extends nothing; a row naming a
boundary other than this episode's (`loss_budget:{account}:{yyyyMMdd}`) is not about this episode;
the instant is clamped to at most ONE closure length past eligibility; and it is one row per episode
by key, so it is applied ONCE rather than re-applied per tick, which would outrun the clock. It is
never applied past a release, and it is stated with its end wherever the instant is shown. An
unreadable row extends nothing.

**What is NOT there, said rather than implied: nothing in this build WRITES an extension.** A
director's assessment is free markdown (`PublicationKind.Assessment`, an `assessment-*.md` file
capped in lines) and `CouncilBoundaries` deliberately exposes no method that takes a disposition from
a director at all, so there is no structured way for one to ask for more time. Scraping a phrase out
of a director's prose would be an agent moving a money-path state by writing words in a document,
which is what `AGENTS.md` forbids of everything handed to an agent. The BOUND is implemented and the
request channel is not.

**Told.** `status` gains `loss_held_for_review` (the hold's own sentence, separate from
`loss_reopen_held` because it is the only entry there that waiting does not fix), `loss_released_at`
and `loss_closure_rule` (the SNAPSHOT, so an agent doing the arithmetic off the live settings cannot
get a different answer from the gateway). The Situation and the Safety page carry the hold, the
extension with its end, the rule and the release with the owner's note quoted; section 4 of the daily
report adds `held for review`, `closure rule`, `held open until` and a `loss closures in the window`
list — one line per episode with the scope, the instant, the rule THAT episode was judged by and how
it ended (reopened by code, released by you with the note, held, or still running). The listing
window is the LIVE setting, because it is how much history is shown and not a decision; the count
that decides a hold is on the hold's own row. `AGENTS.md` and `USER-GUIDE.md` say the same.

**Choices in this unit, and the owner's to overrule.** The connector is in all three new keys
(`U-flatten-2`'s reason; the breach key stays `U-flatten-1`'s shape, and `U-scope-identity` is what
made every part of it safe to build out of a platform's own names). One press releases
EVERY hold standing on the account, each getting its own row carrying the same note, because the
owner reviews an account rather than a key. A hold that cannot be WRITTEN is said out loud in the
engineering log and does not fail the closure (`OpenLossBoundary`'s precedent) — a worse day than a
held one, and not an open account. There is no press that re-imposes a hold, because the count that
wrote it was taken at a confirmation that has passed. A release is refused when nothing is held,
rather than written as a no-op. And the report's listing window follows the live setting while every
decision follows a snapshot, which is deliberate and is the only place the two differ.

**Not this unit, and said here rather than implied.** There is no ALLOCATION LADDER: a hold is per
scope — the account, or one instrument — and nothing in this build enforces or attributes risk across
an aggregate of scopes, so a strategy losing on three instruments at once is three independent
counts and not one. Aggregate enforcement and attribution do not exist yet. The data-loss exit is
`U-flatten-3`.

## U-scope-identity — a loss-line scope is `(connector, account, symbol)`, carried on the row

**THE SCOPE IS ON THE ROW, AND A KEY IS ONLY AN ADDRESS.** `LossBreach.ScopeOf` used to take a
closure apart with `key.Split(':')` and refuse anything that was not exactly three or four parts.
The names in a key are the PLATFORM's strings and never ones TradeAgent chooses, so a
venue-qualified instrument (`ES:H6`, `BINANCE:BTCUSDT`) or an account id a prop firm qualifies made
five parts, and `OpenClosures`, `LatestBreach`, `ClosureHistory`, `PriorBreaches`,
`SymbolsClosedToday` and the strike count all answered "nothing is closed" about a closure that was
written, on disk, flattened and never reopened — so the closed scope went on trading, and was closed
and flattened again on every pair of agreeing pulls (REVIEW 2026-09-16, findings 1 and 3, and
UNVERIFIED 2). Every one of those readers now takes `Account`, `Symbol` and `Day` off the RECORD,
which always carried them, after a prefix scan; `ScopeOf` survives only as the ADDRESS decoder for a
row this build cannot parse, which is the one thing a row cannot answer about itself.

**The delimiter choice, in one sentence: the parts are ESCAPED at minting, not refused.** The review
offered refusing a name that carries the delimiter instead, and that would delete the loss budget on
exactly the venues whose own convention is `VENUE:SYMBOL` — a protection removed rather than a defect
fixed — so `LossBreach.Part` maps `%` to `%25` and `:` to `%3A` before a name goes into any
`loss_breach:`, `loss_flatten:`, `loss_reopen:`, `loss_hold:`, `loss_release:`, `loss_extend:`,
`valuation_unavailable:` or `loss_valuation_exit:` key. It is total and reversible, so no name is
unaddressable and **no two scopes ever share a row** — the account `ACC:ES` losing its day and the
account `ACC` losing ES were the same key, which is a second breach silently never written. A name
carrying NEITHER character maps to itself, so every key and every receipt already on disk is byte for
byte the key it was: nothing is migrated and nothing is orphaned.

**A BREACH IS BOUND TO THE PLATFORM AND THE MODE IT WAS MEASURED ON.** `LossBreachRecord` carries
`Connector` and `Mode` at `Compose` — the fields every other record on this line already had — and
`FlattenForBreachAsync` refuses when either differs from the gateway it is running on, with the
sentence the account check already uses, before it reads anything. Without them a PAPER breach on a
simulator's `SIM-001` cancelled the owner's resting orders and closed their real positions on a
broker whose account carries the same id, on an installation that had set no loss budget at all
(finding 3); the mode is the second half because the same broker and the same connector id are a
different undertaking in LIVE than in PAPER. The killed-flatten sweep filters the same two before it
announces work. **A record written before this unit names neither: it still CLOSES, because refusing
new risk is the safe direction on any platform, and it is never flattened, because sending closes is
not.** That asymmetry is deliberate and is the owner's to overrule.

**THE FILL LEDGER IS SCOPED, AND A ROW THAT NAMES NO PLATFORM BELONGS TO NO PAIR.** `fill` gains
`connector` at **schema 22** — nullable, NOT backfilled, because this build cannot know which
connector wrote a row it did not stamp and a backfill to the attached one would be the defect with a
migration in front of it. `LedgerPnl` and `PnlAsync` read `FillStore.Scoped(connector, account)`, and
`Pnl.Compute` keys its average-cost book by `(account, symbol)` rather than by the symbol alone: one
account's fills entering another's book do not make a smaller answer, they make a wrong one. Until
this, every money figure in the product — including the one the daily loss budget closes a day on —
was a sum over every row in the table, so one account's loss closed an account that had never traded
and one account's PROFIT netted off a real loss until the budget never fired (finding 2). **Rows with
no connector are counted and reported as `unattributed fills` beside the figure and are in none of
it**; `trade pnl` carries `connector`, `account` and `unattributed_fills` and says so in its note, the
Performance card names the pair in its caption, and section 4 prints an `account` line above every
figure and an `unattributed fills` line when there are any. **With no account selected there is no
pair and the ledger reads empty**, which is the honest answer rather than everybody's.

**Not this unit.** The sweep's second attempt after an exit throws (the review's UNVERIFIED 1); the
bridge; a per-symbol valuation denial; and any all-accounts report — `Pnl.Compute` will now keep two
accounts' books apart if it is ever handed both, but nothing hands it both.

## U-review-med — a closure held against two clocks, the owner's limits at the wire, a sighting keyed by scope

**A CLOSURE IS HELD AGAINST A MONOTONE READING AS WELL AS AGAINST THE WALL CLOCK.** The high-water
mark refused `at < mark` and nothing else, so ONE forward step of the machine clock past the
eligibility instant ended a closure on the very next tick with no time having passed at all: the mark
rose, no suspect row was written, and `HeldBy`'s remaining test — `at < eligible` — had just been
satisfied by the jump (finding 6, probe C4). `LossClockMark` now carries a second reading of the same
tick, `Monotone`, taken from the same `TimeProvider` — `Stopwatch`'s counter in production, which
nothing in the machine's settings can move — and the RUN that took it. Within one run the two have to
move together: a wall step ahead of the monotone elapse by more than `LossReopen.ForwardSlack` —
**four watch intervals, 60 s at the shipped 15 s** — writes the same `loss_clock_suspect` row with
`Direction` `forward`, the step and the elapse on it as evidence, refuses, and is said in `status`,
on the Situation line and in section 4 through the row's own sentence. Nothing an agent can reach
moves a clock, which is why this was MED.

**A RESTART IS NOT A MOVED CLOCK, AND IT IS NOT SERVED TIME EITHER.** Two monotone readings taken by
two different runs have different origins and cannot be subtracted, so a mark whose `Run` is not this
gateway's writes no suspect row — and a new gateway over the same database, a platform switch
included, is a new run by design. What it does instead is decline to CREDIT the gap: the wall step
across the downtime is added to `LossClockMark.Unverified`, and every standing closure has that much
added to its eligibility instant, on the tick and on every surface alike. The baseline each closure's
share is measured from is `LossBreachRecord.ClockUnverified`, snapshot at `Compose` for the reason
`MinClosure` is; **NULL means a record written before this unit and is measured from zero**, which
counts the whole of the account's accumulated gap against it. The receipt carries
`ClockUnverified` as the evidence of what was added. A machine that is off overnight therefore serves
a longer closure than one that is not, and that asymmetry is deliberate: TradeAgent was not watching,
refusing new risk is the safe direction, and the alternative is trusting a wall clock across the one
window nothing can check. **A bounded extension may only push the instant later**, never undo the
penalty.

**THE OWNER'S PER-ORDER LIMITS ARE ASKED AGAIN AT THE MOMENT OF DISPATCH.** The allowlist,
`MaxOrderQuantity`, `MaxNotionalPerOrder` and the quote-age rule are re-decided inside the dispatch
gate by `PerOrderLimitsAtDispatchOrThrow` — in `PlaceAsync` after the position read and the loss
budgets and ABOVE the allocation ceiling, so the ceiling multiplies a reference whose freshness has
just been re-asked; in `ApproveAsync` after the same reads, on the resulting order of a modification
as well as on a placement. It **awaits nothing**: the reference price, the QUOTE it came from and the
contract size are carried out of `RiskCheckOrThrow` on `OrderPricing`, because a second quote read
inside the gate would be a second answer to one question and a round trip in the one place that must
stay short. The first pass stays where it is — it is the one that turns an agent away before its
order costs a quote and a position read.

**What it deliberately does NOT re-ask**, and each is a choice: the RATE LIMIT, because it is
advisory in the risk check by its own comment and what bounds the minute is the reservation taken at
the wire, so charging it twice would refuse callers the owner's number allows; the PAPER-vs-real
account check, which is `ReauthorizeAtDispatchOrThrow`'s mode-against-the-record question and is
already at the wire; and `quantity <= 0`, which is a shape error the agent cannot have fixed by
waiting. A reference the intent NAMED does not age and its quote-age rule is skipped — only a price
taken from a quote can go stale. And a value cap switched ON inside the window **fails closed**: the
contract size is carried only when a cap was enforced above, the cached instrument list answers where
it can, and otherwise `RISK_CHECK_UNAVAILABLE` rather than an awaited read inside the gate.

**A SIGHTING IS ABOUT A SCOPE, AND THE RECORD'S DAY IS THE CONFIRMING PULL'S.** `Confirmed` filed the
first of the two agreeing pulls under the BREACH KEY, which carries the UTC day — so a pair
straddling midnight never agreed: the pull at 23:59:50Z filed itself under yesterday's key, the pull
at 00:00:10Z looked under today's, found nothing, and filed itself as a first sighting of its own. At
`LossWatchInterval` 15 s that is the last tick of EVERY UTC day, and the day that went through the
owner's budget wrote no `loss_breach` row at all: nothing flattened, no `BoundaryKind.LossBudget`
boundary, no strike counted, and the only thing still refusing was the admission gate's live figure,
which stops the moment the figure recovers (finding 8, probe P4). The sighting is now keyed by
`(connector, account, symbol)`, escaped through `LossBreach.Part` for `U-scope-identity`'s reason, and
a scope does not change at midnight. The `Day` on the record is `Compose`'s `at` — the CONFIRMING
pull's instant, never the first sighting's — which is the instant the closure exists from and the
instant the key it is written under is built from: a record whose `Day` disagreed with its own
address would send every reader that takes the day off the record to a flatten key and a reopen
receipt nobody is going to write.

**Not this unit.** A fifth position gate; the review's UNVERIFIED list; anything on the bridge; and
any defence of the closure against a clock moved while the process is DOWN beyond not crediting the
gap — there is no trustworthy elapsed-time source across a process boundary, and this says so rather
than implying one.

## U-flatten-3 — a book nobody can value, its clock, and what the thresholds do not promise

**AN UNVALUABLE OPEN POSITION IS A STATE WITH A CLOCK, AND THE CLOCK ONLY STOPS WHEN A PRICE COMES
BACK.** `U-flatten-1` records nothing on a suspect print and `-2` flattens nothing from one — right,
and it leaves the other failure: a feed that goes silent, a platform that stops marking, a disconnect
that outlives the day. The gate already refuses new risk on the unknown (`RISK_CHECK_UNAVAILABLE`),
which is the safe half; the position already there went on being exposed with nothing measuring it,
which is the half nothing answered. Now every tick of `LossWatchAsync`, under the dispatch gate,
writes one episode row per open instrument: `valuation_unavailable:{connector}:{account}:{symbol}`,
carrying `Since` — the FIRST tick that could not value it — which is CARRIED FORWARD unchanged for as
long as the episode is open. The only thing that ends an episode is a tick that actually valued the
position, and the only thing that starts a new one is the next silence; a position that has GONE ends
its episode too, because there is nothing left to value. `TradingGateway.CanBeValued` asks exactly
what `LossBudget.Read` needs and nothing else — the platform's own mark, else a FRESH, in-epoch,
executable quote, else the multiplier — so the clock and the figure can never disagree. The
staleness and the epoch are the guard: a cached price always exists once one has arrived, so a check
that asked only whether a price had ever been seen would end the episode on every tick and
unavailability would never age.

**NO DAY IN EITHER KEY, AND THE EXIT'S KEY IS THE EPISODE'S — a deviation from the brief's
`{utcDay}`, and deliberate.** A breach is a fact about a UTC day because the ledger figure that
closed the scope is a day's figure. A lost valuation is a fact about a STRETCH of time: keyed by the
day, the episode row would start a fresh clock at every midnight, and the exit row of a second
episode on the same instrument on the same date would collide with the first — the write-once insert
would answer false, this code would read that as "already exited", and a position would be left open
that nothing would ever close again. So the episode row carries no day at all and the exit is
`loss_valuation_exit:{connector}:{account}:{symbol}:{yyyyMMddTHHmmssZ}`, the stamp being the
episode's own start. The day is ON the record, for a reader. Both keys carry the CONNECTOR for
`U-flatten-2`'s reason.

**THE EXIT IS `-2`'s MECHANICS AND NOT `-2`'s EVENT, AND IT IS NEVER `LOSS_BUDGET_REACHED`.** After
`RiskPolicy.ValuationLossExitMinutes` of CONTINUOUS unavailability, with the connection UP, the
position is closed through the same cancel-then-settle-then-close order, the same write-ahead rows,
the same `ReductionOnlyOrThrow` at the wire and the same resolution by machine behind a flat
read-back — under this unit's own press kinds `op-valuation-cancel-` / `op-valuation-close-`, its own
reason `VALUATION_LOST`, its own write-once (`AddKvOnce`) record and its own
`BoundaryKind.ValuationLoss` boundary (`valuation_loss:{account}:{yyyyMMdd}`, default disposition
`hold`, holding nothing). **Separate kinds** because `PressName`, the order's note and the drift
sentence all date themselves by the kind, and every one of them would otherwise tell an owner a
budget was reached when none was — and because a stuck valuation exit under the budget's kind would
REFUSE the next real budget flatten, which is the more urgent event. **A separate boundary kind**
because a shared id would let a valuation exit open the very boundary a loss-budget episode's
extension names (`LossBoundaryIdFor`), moving a closure's clock with an event that was never about
the closure. **WITH THE CONNECTION DOWN NOTHING IS SENT AT ALL** (`CLAUDE.md` rule 3): the clock goes
on running, the pause the gate is already enforcing holds, and every surface says so on every day it
lasts.

**IT IS NOT A BREACH, AND THIS IS THE CHOICE THE BRIEF LEFT OPEN.** A `VALUATION_LOST` exit does NOT
close the day, does NOT close the instrument, writes NO `loss_breach` row and counts towards NO
strike. A budget breach is a fact about the owner's money — something was lost, and the closure, the
strike and the post-mortem are all about that. A lost valuation is a fact about the owner's DATA:
nobody has measured a loss at all, which is the problem. Counting it as a strike would let a flaky
feed hold an account for a person to review; closing the day would turn a data outage into a trading
ban the owner never asked for. New risk on that instrument stays denied only while the valuation is
unavailable — which today is account-wide, because one unvaluable position makes the whole reading
unknown — and not one tick longer. The exit is reported, with its reason, on `status.loss_valuation_exit`
and on its own line in section 4, never folded into `positions closed for you`.

**ZERO IS OFF AND IS THE WIDEST VALUE; LENGTHENING IS THE GRANT.** `RiskPolicy.ValuationLossExitMinutes`
is on the Safety page, **15 out of the box**. Zero switches the exit off — the reading every other
zero in that class already has (the notional cap, both loss budgets, the strike window) — rather than
"exit on the first unvaluable tick", which would be a foot-gun reached by typing the digit that
switches every other limit off; the episode is still recorded, the openers are still cancelled, and
the sentence says in words that TradeAgent will not close it. LENGTHENING is the risky direction, so
`RiskPolicy.Widenings` names it and the save arms the second press — it is the only box on that page
whose risky direction is neither of the two above it. Fifteen minutes is a judgment and not a
measurement: sixty ticks of the watch and thirty times `MaxQuoteAge`. `GatewayOptions.ValuationLossExitAfter`
(15 minutes) is the FALLBACK used when the settings row could not be read at all — a row nobody could
read is not an owner who chose zero.

**THE THRESHOLDS ARE NOT A MAXIMUM LOSS, AND SECTION 4 SAYS SO ON EVERY DAY WHATEVER THE BUDGETS
ARE.** The book is valued every `LossWatchInterval` (**15 s**, sooner when a price arrives); a
closure needs a second agreeing reading inside `LossBreachConfirmWithin` (**60 s**); a price older
than `MaxQuoteAge` (**30 s**) is refused as a basis for the figure; a position nobody can value at
all is closed after `ValuationLossExitMinutes` (**15 minutes**). Each is a threshold at which
TradeAgent INTERVENES. None bounds the realised loss: between two readings the market can gap
straight through the figure, and what the closing MARKET order fills at — the spread it crosses, the
slippage, and the fees the platform has not reported (already counted at their gross) — is not
TradeAgent's to choose, so the loss can be larger than the budget and on a gap very much larger. The
report prints the SAMPLING delay it measured on its own last pass, on a real wall clock rather than
`GatewayOptions.Clock`, because the figure is a property of the machine and the platform and a
constant compiled into a build would be a claim about somebody else's computer. **Measured by the
builder on the dev Mac** (macOS 26.5.1, arm64, Release, simulator connector, one open position):
**4.849 / 4.934 / 4.862 / 4.899 ms** per reading — the app's own arithmetic, NOT a platform round
trip, which on a real venue is what dominates. The sampling gap an owner is exposed to is that plus
the interval, so up to about 15 s. It is printed WHATEVER the budgets are: an installation with no
budget has nothing to be misled about, and the owner relying on one is the reader who needs it.

**Choices, and the owner's to overrule.** (a) The episode row is an UPSERT the watch refreshes and
not an `AddKvOnce` — the watch is its only writer and writes it under the dispatch gate, and the one
field that decides anything (`Since`) is carried forward rather than recomputed; the EXIT, which is
what stops a second close going out, is write-once at the SQL layer. (b) The precautionary cancel
takes only orders that could INCREASE exposure and leaves a resting protective order alone: nothing
is being closed, and removing the one thing bounding a position nobody can measure would be the
software cancelling the owner's stop because it had stopped being able to see. The budget flatten
still takes those too, and is entitled to — it is removing the position they protect. (c) That cancel
runs ONCE per episode, retried only while a cancel did not settle, because no new opener can arrive
while the gate refuses on the unknown; a press row every fifteen seconds for a book that cannot
change is a log, not a fact. (d) A cancel whose targets are all gone on the read-back CLEARS the flags
it wrote (writing no state — `ClearTheFlatteningFlag`'s rule) and restores `ExecutionCapability` on
`ReconcileAsync`'s own two lines: leaving them flagged would pause every order in the product for as
long as a data outage lasted, a manual outage imposed by the one event that is already refusing new
risk. A cancel that could NOT confirm one keeps every flag and pauses. (e) ONE exit attempt per
episode: the record is written whatever the outcome, and an exit that could not confirm leaves its
rows flagged and the account paused for a person, which is the product's answer everywhere else.
There is no separate sweep for it — the tick re-derives the episode from its own row.

**Not this unit, said rather than implied.** The denial while a valuation is missing is ACCOUNT-WIDE
and not per symbol, because one unvaluable position makes `LossBudget.Read` answer unknown for the
whole account; a per-symbol denial would need the figure to be decomposable and it is not. Nothing
here re-opens, re-tries or reconciles an exit that failed. Stops and targets are `U-protect`. Not
verified anywhere but the dev Mac: no box, no ATAS, no real money, and every order in every test went
to the simulator through `RecordingConnector`.

**One thing the simulator cannot prove, named rather than implied.** `FakeConnector.ClosePositionAsync`
re-reads the position and sizes the order itself, so a reversal cannot be produced through it however
wrong the composed size is; what the flow test can state is that no close reached the connector and
that the row says why. The arithmetic itself is held directly (`ReductionOnlyTests`). The other named
limit is inherited: the shared close path catches a definite `ConnectorRejectedException` in the same
arm as a timeout and records UNKNOWN, so a broker's flat refusal of a close reads as "we do not know".
That is the safe direction and it is not this unit's to change.

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
7. every risk limit: allowlist, quantity, paper-vs-real, rate limit, quote freshness for an order
   without its own price, and order value multiplied by contract size. A parked MODIFICATION re-reads
   its target from the book at the moment of the press and is judged on the order as it will stand
   **now** — it may have moved, filled or been cancelled while it waited, and a target that cannot be
   read refuses the approval rather than sending on a stale reading;
8. then, for a placement only, **every gate a placement runs on the position, in the same order and
   off one reading of it** — the open-position cap, the unresolved-reducer refusal, the loss budgets
   and the allocation ceiling, which is `U-approve-gates` below. The allocation that gate answers with
   is recorded on the request before anything is dispatched.

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
`LAUNCHED` that is not the pass's own turn AND is not a launch this process is still flying, is MOVED
to `out/quarantine/` with an activity line and never published. `ENDED` and `LOST` both publish: a
killed turn's report is the work of THAT turn, and attribution by whichever launch happened to run the
pass named the wrong one. A file whose launch IS still flying here is LEFT where it is — that is the
other role, mid-turn, and its own commit publishes it; quarantining it would take a live turn's report
away from it on the strength of when a pass happened to run.

**A pass belongs to the role whose turn it follows.** `CouncilRelay.Run(role, attempt)` touches only
that role's `out/`; `Reconcile()` is the only pass over every role and is the app's start-up pass, when
nothing is flying. The staged files are READ before the turn's `Database.Write` opens — there is one
lock over the whole store, so reading them inside the commit held it across every byte the agent wrote
and the other role could not open its own launch record until the read finished. What is still read
inside the transaction is `WorkspaceRevisions.Snapshot`'s two size-capped memory files.

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
folders plus `in/` and `out/`, all in ONE origin group — `Agent` and `App`, the two words that walk
can produce — so `MarkMissing` never invents a deletion and never leaves a deleted app file standing.
What a role was handed and what it published are measured facts in `material`, which the agent cannot
edit, beside the relay's own record of the same artifacts.

**A file in that walk is the app's when, and only when, the app wrote down that it wrote it.**
`AppFileManifest` (`state/app-files.tsv` — outside the workspace, beside `data/` and `reports/`, with
no verb and no pipe op that writes there) carries one entry per file the app puts in a role's home:
the path relative to that home, and the sha256 of the bytes. It is written by `WorkspaceBuilder`,
`ResearchLibrary` and `CouncilRelay.Deliver` at the moment of the write, and by nothing else — the
quarantine move is deliberately not recorded, because those are the role's own bytes and the app only
moved them aside. The scanner records `App` only when the path AND the hash both match: a manifested
path holding other bytes is `Agent` (the role changed it), and a path the manifest never named cannot
become the app's whatever it holds — a role may copy an app file anywhere inside its home and the copy
is its own. Both halves are load-bearing, and dropping either is a mutant the tests kill. Paths are
relative because `TRADEAGENT_HOME` can move an install. The origin is decided once, as the row is
written (`MaterialStore.Observe(Func<MaterialOrigin>, …)`), so the bytes are read only for a path the
app has written down and a re-sighting of an unchanged file still costs a directory listing and
nothing else. **No schema rung**: `material.origin` is text with no CHECK, so the new word is a new
value in it. **Everything here fails closed** — an entry that could not be written, a manifest that
could not be read, a file swapped between the walk and the read: each measures as the agent's, never
as the app's. The inbox attestation is untouched; `Inbox` and `InboxUnattested` still come from agent
process liveness and nothing else.

**And every surface says so.** `material-list` takes `origin: app`, prints the word `app` and reads it
out in its note as *written by TradeAgent … never counted as your work*; `trade schema` says the same
in the op's own description; `AGENTS.md` names the three words in one line and says the role cannot
set them; and the owner's Inbox page prints "written by TradeAgent" instead of falling through to "the
AI made this", which is what it did for every app file before this. An `app` row is not in
`Present(MaterialOrigin.Agent)`, so nothing that counts the agent's work counts it.

**Two roles' turns may overlap, and three leases are what keeps that honest.** A turn lease per role in
`MissionLoop` (a second `TurnAsync` for a role already turning is refused in words, never queued, and a
role that is turning is stepped over when the next turn's role is chosen); one open attempt, one staged
close and one reservation per role in `TurnMeter`; and a register of the launches THIS process is
flying (`LiveAttempts`), which is what a second meter skips when it turns every other open row `LOST`.
**All three are in memory, deliberately** — the same choice the gateway's dispatch lease and the
owner's composite lease are written down above for: a claim that outlived the process holding it could
never be released. The durable witness that a turn was in flight is its `LAUNCHED` `ai_attempt` row,
and a restart holds no leases, loses every open row at its reservation and reconciles from disk. There
is no schema for any of it. Quiescence stays whole-council: a material pass runs only when NO role is
turning, and no role may launch while one runs (`docs/COUNCIL.md` rule 7 — the scanner attests only
across proven quiescence of every managed agent). `_sessionTurns`, the run of consecutive errors and
the cap notice are per role, because each is a fact about one conversation. What is NOT concurrent is
the money path: only the Operations Director places orders and the gateway's dispatch gate is a mutex.

## The AI's spending — `src/TradeAgent.Core/Db/AiAttemptStore.cs`

One `ai_attempt` row per launch of the agent CLI, written by the app only — the rule `material`,
`fill`, `mission_event` and `publication` keep — BEFORE the process starts. `LAUNCHED` with a
reservation; `ENDED` when the app sees it end; `LOST` the next time a meter opens the database with
it still open. Nothing releases a reservation.

**The reservation formula** (`CostCatalog.Reserve`), stated here, on the Safety page and in the user
guide: `allowance input tokens × the DEARER of the plain and cache-write rates + allowance output
tokens × the output rate`, reasoning inside output. The dearer of the two because a bound that
assumed the cheaper of two rates the same tokens can be billed at is not a bound — on this build's
own list `gpt-5.6-sol` writes cache at 5.00 against 4.00 plain — and a maximum rather than a sum
because those tokens are charged at one of the two, never both. The owner's own two numbers have no
cache-write rate, so for them it is that one input rate. The allowance is
`AiTurnAllowanceInputTokens` / `…OutputTokens`, two boxes on the Safety page, one press, zero reading
as the shipped default (1,200,000 / 20,000).

**Admission is inside the transaction that writes the reservation.** `AiAttemptStore.Begin(attempt,
consuming, admit)` reads the day's totals and the role's, compares
`AiSpendToday.AdmitsAnotherTurn` — `spent + reserved + this turn ≤ cap`, globally AND for the role's
share — and INSERTS NOTHING when either has no room, returning the refusal (which ceiling, by how
much, `ResumesAt`). The loop's own look before it is a cheap first filter and never the gate: it is
taken sixty lines earlier, so two launches could both be told there was room and both take it. A
refused launch consumes no `mission_event` and starts no process. **Every** launch passes it — the
owner's typed chat turn through `AgentSession.Admit`, which shows the refusal as a System line and
keeps their words, exactly as a message typed while the AI is working is kept.

**A turn that ends without reporting what it used is charged its reservation** (`unpriced_reason` =
`AiAttemptStore.UnreportedReason`), on the ordinary path as well as after a crash; a reservation of
zero is not a charge and such a row stays unpriced. `AiSpendToday.UnreportedTurns` counts them, so
`Spent` is never read as a bill: an unpriced turn means the real figure is HIGHER, one of these means
that part of it is the most it could have been.

`TurnMeter` holds one open attempt PER ROLE — one conversation per role, and the owner can type into
the chair's while another role's turn is in flight — and `Close` writes the row it opened, by id. The
close of a turn the MISSION LOOP opened is held for that turn's one committed transition
(`CommitStaged`, above); the owner's typed turn publishes nothing and consumes no wake, so its close
is written the moment the turn ends rather than held in a slot the next mission turn also writes to.

**STATED LIMITATION: there is no provider-side ceiling.** The reservation bounds what TradeAgent
COMMITS, not what the vendor will bill. codex 0.153.4 takes no per-request token or cost limit on its
command line and its sandbox restricts writes rather than what enters its context
(`docs/COUNCIL.md`, "workers run on an app-owned harness"), so a turn whose input runs past
`AiTurnAllowanceInputTokens` is still run and still billed, and this app's only enforcement is
refusing the NEXT launch. That is why over-reserving is the safe direction and why the reservation is
priced at the dearest rate the same tokens can carry. A per-REQUEST bound that the provider itself
enforces arrives with the harness (`U-api-worker`); until then the allowance is an app-side
commitment and the daily ceiling is enforced between turns, never inside one. **That limitation now has
an exception, and only one:** a role on the app-owned harness is bounded inside its turn — see below.

## The app-owned harness — `src/TradeAgent.AgentRuntime/ApiAgentRuntime.cs`, `ApiConversation.cs`

One `IAgentRuntime` (`openai-api`) whose turns are HTTP requests this app composes rather than a vendor
process it starts and watches. `docs/COUNCIL.md`, "Workers run on an app-owned harness": a cheap team
with enforceable tools and a bounded context needs control at every model-and-tool boundary, "which the
codex CLI's documented contract does not provide: its sandbox restricts writes, not what enters its
context, so its caps are advisory". Here the app IS the boundary.

**It is manifest data**, like every vendor command (`CLAUDE.md`): `BaseUrl`, `CompletionsPath`,
`MaxOutputParam` (the provider renamed `max_tokens` to `max_completion_tokens` once already) and
`DefaultModel` = `gpt-5.6-luna`, the cheapest current entry. `RuntimeCatalog.Harnesses()`/`IsHarness` is
derived from a non-empty `BaseUrl`, not a flag. `ListPrices` prices the whole current generation for it.
**`Verified = false` and no run of this repository has ever called the provider**: the request and
response shapes are the vendor's published chat-completions contract, and every test drives
`FakeProvider`, a loopback `HttpListener`. `SuiteReachesNoVendorTests` is what keeps that a property of
the suite — it refuses the provider's host in any test source and refuses a test file that uses
`ApiAgentRuntime` without setting `BaseUrl`, matched on the TYPE because a target-typed `new(...)` has
the constructor's name nowhere in the source.

**A turn is one `SendMissionAsync`, however many requests it takes.** `TurnEnded` is raised once, with
the usage SUMMED over every response — last-wins would price a three-request turn as one, and the
direction of a wrong bill is the whole argument (`TurnUsage.Plus`). Usage no response reported stays
NULL, never zeroed, so `AiAttemptStore.End` charges the reservation. `TurnUsage.Read` now also looks one
level into `prompt_tokens_details` / `completion_tokens_details` for the three SUBSET figures, which is
where this provider nests cached input; the flat names still win.

**Four boundaries, each checked BEFORE the request that would pass it** (`ApiConversation.Exceeded`) —
after it, the provider has already done the work the check exists to prevent. Reaching a bound EXACTLY
is already too late. (1) `TurnMeter.Begin` reserves before the first request, through
`IAdmittedConversation` — an interface rather than a type test on `AgentSession`, or a harness turn would
be metered after the fact and never admitted. (2) Cumulative input and (3) output tokens against
`TurnAllowance`, the provider's own reported counts. (4) Retrieval in BYTES against
`MaxRetrievalBytesPerTurn` (512 KiB, 64 KiB per call) — its own number rather than a share of a token
allowance, because "bytes are not tokens and are never converted to them" (`TurnContext`). Plus
`MaxRequestsPerTurn` = 24, which stops a turn that is looping cheaply rather than spending. Every request
carries the provider's max-output parameter, which is the one bound the PROVIDER enforces. Over a bound,
the turn ends `CONTEXT_BUDGET_EXCEEDED` with exit code 1, **its staged files kept**, and
`ai_attempt.context` carrying `"ended"` — an exit code cannot tell a bounded turn from a broken one, and
rule 4 keeps enforcement apart from billing. A never-answering endpoint fails on an injectable timeout
(120 s shipped, seconds in tests) reported as a timeout rather than as a cancellation.

**A cut turn ends like any other, and the next one is told.** `MissionLoop`'s committed transition is in
its `finally`, so a turn stopped on a bound closes ENDED and publishes what it staged in the SAME
`Database.Write` as a turn that finished — `U-harness-loop` item 2, asserted at both boundaries. The
third half is a message, not a row: `AgentTurnEnded.Cut` carries the bound's own sentence out of the
turn, the loop holds it PER ROLE until that role's next turn, and `MissionSituation.Cut` renders it
above `Restored`. Both directions on every turn, so the notice describes the turn immediately before and
never a turn from an hour ago; it does not survive a restart, which by then has turned that launch LOST
and reconciled its files, and `ai_attempt.context` is the durable copy. Without it a role opens a
half-written report with two readings available to it — the app lost the work, or it never wrote it —
and spends the turn the owner is paying for on whichever one it picked.

**Tools are grants, default deny** (`GrantedWorkerTools`). Six, and no seventh: `read_file` /
`list_files` anywhere inside the role's own home (its `in/` included), `write_file` into `out/` and
`trading/` ONLY — staged, the relay publishes — `trade`, `data` (`data-list`/`data-bars`) and `report`.
No shell, no HTTP, no packages, no model-selected executable. `ToolPaths.Resolve` refuses an absolute
path, a `..` SEGMENT (for what it says, not only for where it lands), anything resolving outside the
root, and a symbolic link inside it — links are checked only BELOW the root, because on macOS the root
itself sits under one. **The role check is not in that class**: `trade` goes through
`GatewayPipeServer.CallAsync` (`IGatewayCalls`), which is the pipe's OWN handler under
`AgentContext.ForAgent(session, role, attempt)`, so a Research worker's `buy` is refused by the same line
that refuses a Research launch on the pipe, with the same code and sentence. `trade`'s op list is closed
and carries no operator authority; a mutating op with no `request_id` is sent under one the app minted.
**`backtest` is on that list for every role** (`U-harness-loop` item 3, the decision `U-api-worker`
deferred): it is read-only for the gateway, it is deliberately NOT in `Ops.Mutating`, and it runs under
the caller's own launch identity — so a worker may ask for a backtest of a program in **its own home**,
the run is recorded under the role that asked, and one run at a time per role still holds. Adding it to
`Ops.Mutating` instead would make the pipe's role check refuse the one role that needs it.
**Every call is a `tool_call` row at schema 13** — attempt, role, tool, an argument SUMMARY (never the
payload), bytes returned, served or refused — app-written only, like `material`, `fill`, `ai_attempt`,
`mission_event` and `publication`. It is round 4's "observed deliveries", the thing that sentence said
could not be had from an unrestricted CLI.

**One role on it, by the owner's choice.** `TradeAgentSettings.RoleRuntime` per role;
`AppHost.RuntimeForRole` resolves the owner's choice, then Research onto the harness IFF a key is held,
then the app-wide runtime. The chair stays on the vendor CLI in this slice — its conversation IS the Chat
page's, and the chair on the harness is a later unit. `TurnMeter` takes `roleRuntime` beside `roleModel`,
because a price is looked up by (runtime, model) and a Research turn on `openai-api` priced against the
chair's `codex` catalogue is a commitment against a rate nobody is charged. **The key is
`HarnessKey`: in memory, written nowhere, cleared in `AppHost.DisposeAsync`**, pasted into a masked box
on the Safety page with the sentence saying why — round 4's "keys… are not retained beside an
unsandboxed CLI process until containment lands", and `Containment.Sandbox()` still answers `NONE`.
The owner pastes it again after a restart; the daily report says `harness key: held` / `not held`.

**What the harness is NOT contained by.** It runs IN this process, so `Containment.RefusalToLaunch` —
which refuses to START an uncontained child while real money is armed — has no child to refuse and is
not applied to it. That is not a gap being papered over: a worker with no process of its own is the
"harness-only execution for every role" that round 4 named as the honest alternative to an AppContainer.
What it does not fix is `U-contain-2`: this process reads and writes `state/` as the same OS user, and so
does the vendor CLI beside it.

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

## The strategy program — `src/TradeAgent.Core/Strategy/`

`StrategyParser.Parse(text)` is **total**: it answers a `StrategyProgram` or a `StrategyRefusal`, never
both, never neither, and never an exception. The text comes from an agent writing into
`workspace/strategies/`, so a parser that threw would be a crash the agent can cause with a file. A
refusal reads `line 7: <reason>`, or `program: <reason>` when the fault is the ABSENCE of a declaration.
`docs/STRATEGY-LANGUAGE.md` is the syntax, the indicator semantics and the limits.

`StrategyProgram` is immutable, built only by the parser: `Instrument` (one spot symbol), `Constants`
(sorted, typed), `Indicators` (sorted), `Rules` (declared order, **every exit before every entry**),
`Sizing`, `Stop`, `Target`, `MaxHoldBars`, `Time`, `WarmUpBars`, `NodeCount`, and `Source` — the text
verbatim, kept beside the canonical form so that what is read is what was written.

**Everything on COUNCIL's "Never:" line is unrepresentable, not filtered.** `Expr`'s constructor is
`private protected`, so the node kinds are closed to `TradeAgent.Core`: there is no node for a
statement, a loop, a user function, a clock, a random draw, a file, a socket, a model call or a second
instrument. A program is long or flat — no rule takes a side, and a sizing fraction above 1 (which is
leverage) is refused as a number. Adding any of them would be a new node kind in a reviewed diff, never
a keyword slipping past a blocklist.

**Identity.** `StrategyId = Sha256Hex.Of(Canonical + "\n" + Parameters + "\n" + Manifest)`, which is
`docs/COUNCIL.md`'s rule made literal. `Canonical` (`StrategyCanonical`) is the typed, ordered form:
one fixed line order, constants resolved into it, indicators sorted, conditions in prefix form where
precedence cannot be misread, numbers with trailing zeros gone, and the computed `warmup`. `Parameters`
is the constant table, sorted, because a parameter sweep is one structure with different numbers.
`Manifest` is `StrategyVersions` — language, indicator semantics, calendar — so a build that changes
what a program MEANS re-identifies every program rather than reusing an id over new semantics. Comments,
spacing, letter case and declaration order are not part of it: one strategy saved twice by a model that
reformatted its own output is ONE submission, one trial budget, one lineage. Rule order IS part of it.
The three day-one programs are pinned to their ids in `tests/TradeAgent.UnitTests/Strategies/`.

**Warm-up is explicit** (`StrategyWarmUp`, one place): the deepest lookback over every DECLARED
indicator, every history reference, every crossing (one bar more than its deeper side) and the stop's
own ATR period, at least 1, refused when it exceeds `StrategyLimits.MaxLookbackBars`. An indicator
evaluated one bar early is a different indicator wearing the same name, and every trade it produces is
one the promoted program would not have taken.

**Every limit is a named constant in `StrategyLimits`** — source bytes, lines, line length, names,
constants, indicators, rules, expression nodes, nesting depth, history depth, lookback, entry windows,
holding bars, quantity, fraction, percent — and each is cited by the refusal that enforces it. The
zones a program may name are data there too, never an OS lookup, so a program means the same thing on
every machine that hashes it.

## Bars by dataset id, and the evaluator — `src/TradeAgent.Core/Data/BarFeed.cs`, `Strategy/`

`DatasetStore.ById(id)` finds one dataset by its ledger id, and `BarFeed.Open(store, id)` answers a feed
or a **refusal that says why**. Opening is where `DatasetStore.Checked` is called — **once, at the start
of a run** — because it re-hashes every raw archive file and the normalised file on every call, and
because a run whose dataset changed state half way through has no one dataset its result is about. A
`REJECTED` dataset serves nothing. `BarFeed.Chunks(from, to)` then streams the window ascending with **no
cap**; `DatasetReader.Read`, which the `data-bars` pipe op uses, keeps its 10,000-bar cap and still
REFUSES rather than truncating. Both read one row parser, `DatasetReader.TryBar`.

**What a dataset does not carry.** There are no per-bar quality flags and no instrument increment
anywhere in `dataset` or `dataset_file` — the rows are counts, hashes, coverage and gap runs. So the
evaluator serves the five prices the vendor published and states an intent's quantity **without an
increment applied**: rounding down to the increment and the gateway's own risk limits happen downstream,
where the instrument is known. An intent is not an order.

`StrategyEvaluator.Step(state, bar, account)` is the whole event interface: one closed bar, plus the
account state the CALLER supplies (capital, equity, position, average fill price, pending-order state,
bars since entry). The evaluator **never invents** any of it, reads no clock, draws no random number and
touches no file — the same bars, program and readings produce the same intents on every machine. It
EMITS intents and places nothing: each one names the bar it came from and `NotBefore`, that bar's close,
the earliest instant it may be acted on. `docs/STRATEGY-LANGUAGE.md`'s "Evaluation" section is the
semantics: warm-up refused, exits before entries, an exit that fires ends the event, no duplicate signal
while one is pending, at most one intent, undefined is not false, and time filters read from the bar's
open time against `StrategyCalendar` — a versioned rule table for the six allowed zones, not an OS
lookup, so a program means the same thing on every machine.

**A fault is a value.** `EvaluationOutcome.Faulted` with a reason, the run halted, no intent: a bar out
of order or off the interval grid, a division by zero, arithmetic that overflowed, an event over
`StrategyLimits.MaxOperationsPerEvent`, state over `StrategyLimits.MaxStateBytes`, or a run the caller
stopped — which is how a TIMEOUT is defined without a clock in the evaluator. The budget is counted per
EVENT and not per rule, and it is set above what any program the parser accepts can cost, so the two
limits cannot contradict each other. Nothing throws out of the evaluator: the program was written by a
cheap model, and a text it can produce that crashes the app would be the app's defect.

## The backtest — `src/TradeAgent.Core/Strategy/Backtest.cs`, `src/TradeAgent.Gateway/Backtests.cs`

**A backtest places no order and grants no authority.** Nothing on this path touches a connector, reads
the mode, checks the kill switch or could change one. `trade backtest --strategy <file in your own role
folder> --dataset <id> [--from] [--to] [--fees] [--slippage] [--increment] [--capital]` is the only way
in, it is READ-ONLY for the gateway, and it is not in `Ops.Mutating` — that word on this channel means
"sends something to a broker". What it DOES write is the app's own measurement: `strategy_version`,
`strategy_run` and `strategy_trade` at schema 12, app-owned like `dataset` and `fill`. **There is no op
that writes, edits or deletes one of those rows**, because a record of how a strategy performed is the
evidence its author is judged on.

**The execution model is DECLARED per run and is part of the run's identity.** Fees and slippage are
FRACTIONS (`0.001` is ten basis points), the quantity increment is what a size is rounded DOWN to, and
the capital is what the run starts with. Declared no fee and no slippage, a run is frictionless and the
answer says so in those words rather than letting a zero fee read as a measurement; declared no capital,
it starts with 10,000.

**The increment is the one number a run need not declare, and TradeAgent will not invent it.** A request
that omits `--increment` gets the one recorded for the dataset's own `venue_id`/`instrument_symbol` in
`venue_instrument`, and the answer's `increment_source` and the run row say which row it came from. A
DECLARED increment always wins — the catalogue is a default for a caller that gave no number, never an
override of one that did. An instrument the catalogue does not hold, one whose row is not `verified`, and
a dataset that records no instrument at all are each **REFUSED in words** naming both routes out (declare
`--increment` yourself, or have the account owner record the instrument in `venues.json`); there is no
fallback to 1, because a size is rounded down to the increment and a number nobody recorded makes every
figure in the result a measurement of a position that could not have been taken. **Where the increment
came from is NOT hashed.** The number is, exactly as a declared one is; its provenance is recorded beside
the run in `strategy_run.increment_source`, so adding it moved no run id this installation had written.

**Signals fill at the next bar's open. Protection fills where protection fires.** A signal is computed
from a bar's CLOSE, so the earliest price it can be acted on is the next bar's open plus adverse
slippage, with a fee on every fill. A stop or a target is not a decision taken at a close — it is an
order already resting at the venue — so it fires intrabar at its own price; a bar that OPENED through
the stop fills at that open, and a bar that touched BOTH the stop and the target counts as the **stop**,
because bars carry no intrabar ordering and the other reading invents a winning trade. The maximum
holding time is protection too, taken at the close of the bar that reaches the limit and checked BEFORE
the evaluator is asked anything on that bar — so the account it reads is already flat and the evaluator
emits nothing for it. A size that rounds down to nothing, an entry the declared capital cannot pay for
and a signal the window ended before are each **no trade with the reason**, on the record.

**Metrics come from the trace and from nothing else.** `BacktestMetrics.Of(trace)` takes one argument on
purpose: a figure read off the program's text would be a claim about the program rather than a
measurement of the run. Every unknown is a labelled dash — a null is an UNKNOWN, never a zero — and
`Missing` names the figure and the reason. `net_pnl` covers CLOSED trades only; a position still open at
the last bar is in the **equity** the drawdown is measured on, which is where a drawdown that ignored it
would read 0 for a run 40% under water.

**Deterministic and refusable.** The run id is `sha256(version id \n dataset id \n the dataset's
normalised sha256 \n window \n execution model)` and no clock is read inside a run, so the same request
reproduces the same trace byte for byte and the same id; a changed fee is a different run. The dataset's
verdict is taken ONCE, at the start (`BarFeed.Open`), and a `REJECTED` dataset serves nothing; the sha
the run actually fed on is copied onto the run row, so a rejection discovered later is traceable to every
run it fed (`StrategyStore.RunsOfDataset`). One run at a time per role, refused rather than queued, and
a window beyond `Backtest.MaxTracedBars` HALTS with the reason rather than being truncated.

**It runs under the caller's own launch identity, and reads only that launch's folder.** The role and
the attempt on the version and the run come from `AgentContext` — the launch grant the app minted for
that process (`U-containment`) — and never from the folder a file happened to be in, which is something
an agent can arrange. A caller that proved no launch is refused the op: a run is recorded under a role,
and reading a missing role as the chair is right for a row written before the council existed and wrong
for a live caller. The in-process operator is refused for the same reason — the owner at the keyboard is
not a council role. The program must be inside `Paths.RoleHome(ctx.Role)`: the path is normalised and
compared lexically first, so `../../state/tradeagent.db` is refused before anything is read, and then
against the real path with every symlink on the way down resolved, so neither a linked file nor a linked
directory inside the folder is a door out of it. The other role's folder is outside yours.
**What a run cannot prove:** it is computed over bars, which
establish no actual fill, no queue position and no intrabar ordering (`docs/COUNCIL.md`, "Data"). It is a
reason to test something and never a record of a trade, and a result on one venue's bars is not
execution evidence for another venue.

## The venue catalogue — `src/TradeAgent.Core/Data/VenueCatalog.cs`, `Db/VenueStore.cs`

**An instrument is a recorded fact with a source, not a number an agent typed.** `venue` (id, display
name, calendar kind) and `venue_instrument` (symbol, `tick_size`, `quantity_increment`) arrive at schema
17, and every row in both carries `source`, `recorded_at` and `verified`. `trade venue list` reads them;
**no op writes them** — not add, not edit, not verify, not delete — because an increment is what a size
is rounded down to, and an agent that could write its own would be choosing how much it trades and
having the record agree with it. `venue-list` is deliberately NOT in `Ops.Mutating`: that word here means
"sends something to a broker", and the mutating list is also the role gate, so an op on it would be
refused to the Research Director, whose job is to propose the sizes.

**The rows are shipped and corrected in one line.** `VenueCatalog.BuiltIn()` ships them and a
`venues.json` in TradeAgent's own folder overrides them — the `runtimes.json` pattern of
`docs/DECISIONS.md`:73-78, entry by venue id, replacing what it names and appending what it invents. An
**unreadable** `venues.json` yields NO catalogue at all and says why: nothing shipped stands in for a
file the owner wrote to correct a number, which is `RuntimeManifest.Read`'s judgement applied to the
number a size is computed from. An absent file is a different fact and means the built-ins.
`VenueStore.Sync` writes the table from that at gateway construction; the migration seeds nothing.

**What ships, and what it admits.** Binance spot carries BTCUSDT at tick 0.01 and increment 0.00001 with
`verified = false`, because nothing in this build has read Binance's own instrument definition — so out
of the box a backtest over collected Binance bars that declares no `--increment` is REFUSED until the
account owner records the row. TradeAgent's own simulator carries ES, NQ, MES and YM at increment 1 with
`verified = true`, and that is not a double standard: the venue is this application's own and the rows
are the same four `FakeBroker.Instruments` serves, which a test holds them to. Both venues are
`continuous`.

**What the catalogue does NOT hold: no fee and no minimum notional.** `docs/COUNCIL.md` is silent on a
fee table and :155 keeps fees DECLARED per backtest and part of that run's identity, so a fee read out of
a table nobody measured would read as a measurement. It holds no session table either: `calendar_kind`
is `continuous` for a venue that never closes and `sessioned` means "this one closes and TradeAgent
cannot tell you when", which is a refusal to guess rather than a calendar.

**A dataset names its venue and its instrument.** `dataset.venue_id` and `dataset.instrument_symbol` are
copied onto the row by the collector and never joined to the catalogue at read time — a `venues.json`
edited next month must not restate what last month's evidence was collected from — with no foreign key,
so removing a venue cannot dangle a dataset. Rows written before schema 17 are backfilled to
`binance-spot` and the pair, which is what every one of them was. `data-list` and section 8 of the
owner's daily report carry both, null included. A run's increment is looked up by THAT pair and never by
the instrument named in the program, which is a line an agent types.

## Candle sources — `src/TradeAgent.Core/Data/CandleSource.cs`, `CandleSourceCatalog.cs`, `Provisioning/CandleSourceClient.cs`

**The collector is an interface, and every declaration on it is recorded on the dataset row.**
`ICandleSource` states the id, the venue, the interval, the coverage target in UTC days, the URL shape,
whether the vendor publishes a checksum and whether its candles always carry a traded volume;
`MarketDataService` records the first four on the row at schema 18 and reads no constant. Before this,
`BinanceArchive.Interval` was the only interval a dataset could be of and `MonthsWanted` the only depth
a collection could target, so a source declaring five minutes over ninety days would have produced a row
saying `1m` over twelve months. `coverage_target_days` is what was ASKED for; the actual depth is the
span between `first_bar` and `last_bar` and is computed, never stored twice. Binance is one
implementation and its normalised bytes did not move: a test holds the file to the SHA-256 measured on
the build before this unit.

**The sources are DATA.** `CandleSourceCatalog.BuiltIn()` ships them and a `sources.json` in TradeAgent's
own folder overrides them, entry by id — the `runtimes.json` / `venues.json` pattern of
`docs/DECISIONS.md`:73-78, with the same rule for an unreadable file: NO sources at all, never the
shipped ones standing in for a file the owner wrote to correct an endpoint.

**THE REVOLUT X ENDPOINT IS NOT RECORDED IN THIS REPOSITORY, AND THE FIRST REAL FETCH IS THE OWNER'S TO
AUTHORISE.** `docs/RESEARCH-REQUIRED.md`:170 holds only the SIGNED trading base for Revolut X; there is
no public-candles path in it and no sandbox. So the shipped row for `revolut-x-public-candles` carries an
**empty base URL** and an **unverified** URL shape — this build's guess at the shape such an endpoint
would take — its response format (kline column order, an empty volume column for a candle with none) is
equally unverified, and asking it for a period is a refusal in words rather than a request. Everything
this build knows about that source was proved against a loopback `HttpListener` and nothing else: no run
of this repository has ever sent Revolut X a request. Putting the real endpoint in `sources.json` is what
authorises the first one, and it is the account owner's press.

**A source that publishes no checksum is recorded as one and is never recorded as verified.** Where the
vendor publishes a sidecar it is fetched FIRST and pinned, and a period whose sidecar cannot be read is
simply not collected. Where the vendor publishes none the bytes are taken under `Integrity.Unverified` —
which writes the decision into the owner's activity log before the file is used — and the provenance row
carries an **empty** `published_sha256`. The app's own computed hash is recorded beside it and never in
it: that hash proves the file has not changed SINCE, which is a different claim from proving it is what
the vendor meant to publish, and merging the two would make an unchecked source indistinguishable from a
checked one.

**A candle with no volume is midpoint-derived, and four surfaces say so.** `docs/COUNCIL.md`:164-172. The
normalised file's header is versioned — six columns for a source whose candles always carry volume,
seven with a per-bar `quality` for one that may not — so a file written before schema 18 still reads and
every bar in it reads as `traded`, which is what those bars are. The count is on the row
(`midpoint_bars`), the flag is on every bar `data-bars` serves, and one sentence
(`BarQuality.Note`) reaches `data-list`, `data-bars`, the backtest reply and section 8 of the owner's
daily report. A run over such bars must not report like a run over traded ones.

## Forward bars — `src/TradeAgent.Core/Data/ForwardBars.cs`, `Db/ForwardBarStore.cs`, `Provisioning/ForwardBarCollector.cs`

**Two kinds of market data now live in this product and they are never merged.** A DATASET is months of
a vendor's archive, downloaded once by the owner's press, checksummed where the vendor publishes one,
normalised to a file whose SHA-256 is recorded, and frozen. A FORWARD BAR is one closed minute this
installation asked for shortly after it closed, while the app was running. They are separate tables,
separate lists in `data-list`, separate replies from `data-bars`, and separately worded. `U-forward-bars`
is the first thing in this build that watches a market that is still moving: `docs/PRINCIPLES.md`:65
requires forward paper observation and a paper run over the archive is a re-run of last year.

**WHAT A FORWARD BAR CLAIMS: exactly the candle the vendor served for that minute, at the instant it was
received.** `received_at` is on the bar itself and not only on the fetch, so a reader can check the
closure claim rather than trust it — every stored row's `close_time` precedes its own `received_at`.

**WHAT IT DOES NOT CLAIM, and all four are said in words on every surface that serves one.** (1) *A vendor
checksum.* There is none and there could be none: the minute did not exist when a sidecar for it would
have been signed, so the row carries only the hash THIS BUILD computed of the body it received — which
proves the answer has not changed since, and is a different claim from proving it is what the vendor
meant to publish. That is the same separation `dataset_file.published_sha256` has kept since schema 17.
(2) *Executability.* They are bars: no fill, no queue position, no intrabar ordering. (3) *Evaluation
evidence.* No verdict is ever taken over them. A verdict rests on frozen, checksummed, holdout-protected
months, and a program judged on minutes that arrived while it was being judged is a program judged on
nothing. (4) *A guarantee of coverage.* A forward series is as deep as this app has been running and no
deeper; its catalogue row declares a coverage target of 0 for that reason.

**A BAR IS STORED ONLY WHEN IT HAS CLOSED.** `close_time < received_at`, strictly. The vendor's last row
is usually the minute in progress, whose high, low, close and volume are all still moving; stored, it
would be a row that changes after it was written.

**THE FIRST READING STANDS, AND SQLITE ENFORCES IT.** `forward_bar` is keyed `(source, symbol, open_time)`
and the store inserts `ON CONFLICT DO NOTHING`. A re-fetch of a minute already held cannot overwrite it;
a re-fetch that DISAGREES is counted on the later attempt's own note, never on the bar. The insert is
what refuses — not a branch above it — and that is a measured distinction rather than a stylistic one:
an earlier draft read the row and skipped, which left `INSERT OR REPLACE` passing every test because the
replacing statement was never reached. `Database.AddKvOnce` had already had to take the same reading.

**GAPS ARE RECORDED AND NEVER FILLED.** A run of minutes with no bar is one `forward_gap` row keyed on
its first missing minute, so a hole seen twice is one hole. Nothing interpolates, carries a price
forward or invents a volume, and a window with a hole in it is served with the hole.

**EVERY ATTEMPT IS A ROW, SUCCEEDED OR FAILED.** `forward_fetch` holds the URL asked for, both instants,
the status and the reason in words. A body that does not parse is a recorded failure and never a bar —
not "the rows it could read", because an error page served with a 200 would otherwise be reported as a
healthy minute. A host that says nothing at all has a NULL status: "the vendor said 503" and "the vendor
said nothing" are different facts and the ledger keeps them apart. A failing host backs the look off to
five minutes and is a status line, never a crash.

**NO HOLDOUT APPLIES TO FORWARD BARS, AND THAT IS A FACT ABOUT WHAT THEY ARE RATHER THAN A RELAXATION.**
A holdout is a time cutoff the owner drew across a frozen dataset. Every forward bar post-dates every
freeze on this installation, because it did not exist when the freeze was taken — so there is nothing
here to hold back, and `data-bars --source forward` serves any role and a caller that proved none. The
archive reader's cutoff is untouched by this: the same caller, the same window and the same database is
still refused the held-back months through `--source archive`, which `ForwardBarsOverPipeTests` asserts
side by side in one test. The protection that matters for forward bars is on the EVIDENCE side, and it
is that no verdict is taken over them at all.

**STALENESS IS ANSWERED HERE AND ENFORCED WHERE IT ALWAYS WAS.** `ForwardBarStore.Freshness` is the age
of the newest closed bar, computed off the ROWS and never off "when the collector last ran" — a collector
running happily against a vendor publishing nothing is exactly what a liveness figure must not report as
fresh. It reaches `status.forward_data`, `data-bars --source forward` and section 7 of the owner's report.
It does NOT add a gate: a program's `data_freshness` is checked at dispatch against the bar its decision
was computed from (`RefuseAStaleDecisionOrThrow`), exactly as before, and nothing here satisfies that
bound on a caller's behalf.

**THE HOST IS DATA.** `binance-spot-forward-klines` is a `CandleSourceEntry` like the others, overridable
in `sources.json`, carrying the endpoint shape and the one measurement that verified it. It is Binance's
market-data-only host — it accepts no authenticated or trading request at all — and that is why the
collector holds no credential and can place nothing. Asking that row for a PERIOD is refused in words:
it is not an archive, it has no periods, and driving it as one would fetch a live window and hand it to
the normaliser, producing a frozen dataset claiming a freeze it never had.

**IT IS APP-OWNED, LIKE THE DATASET LEDGER.** The collector runs in the app's own process, started with
the app and stopped with it, independent of the mission loop — a paused AI, an exhausted spending ceiling
and a pressed kill switch all leave it collecting, because evidence is not a paid turn. There is no verb
and no pipe op that starts it, stops it, points it somewhere else or writes a row; the owner's one-press
toggle on the Settings page is the only control, and it is in-process.

## The holdout — `src/TradeAgent.Core/Data/Holdout.cs`, `Db/DatasetStore.cs`

**A holdout is a TIME CUTOFF on a dataset, not a second dataset, and that is a CHOICE this build made
where `docs/COUNCIL.md` is silent.** COUNCIL:131 asks the referee to protect "holdout data the research
process cannot reach" and does not say how. `dataset.holdout_from` is a UTC instant and it is
**inclusive** — the bar whose open time equals it is already private — and `dataset.evaluation_class` is
`research` (real collected history, whose runs are charged) or `fixture` (bars that prove the plumbing
works, charged nothing, never evidence). A second dataset row would have given the research process an
id it could ask for and a second set of provenance that could drift from the first; a cutoff cannot be
asked for, because asking is what is refused. **The class and the cutoff are orthogonal:** the cutoff
decides which bars are served, the class decides whether a run over them is charged.

**Only the owner's own window sets it, and it can never move EARLIER.** `DatasetStore.SetHoldout` is the
one writer of both columns — deliberately not part of `DatasetStore.Record`, so collecting months cannot
declare a holdout as a side effect — and it is reached from the two-press card on the Data page and from
nowhere else. There is no pipe op, no `trade` verb and no request field: `HoldoutOverPipeTests` asks
**every op this build has**, in both directions, and the row does not move. Moving the cutoff back is
refused in words because the bars in between have already been served, and COUNCIL:212 is literal about
it — "a leaked holdout cannot become unseen". Moving it LATER is allowed: it withholds bars nothing has
read. There is no clear, and nothing lowers the class back either.

**The refusal is the DEFAULT path, in the readers themselves.** `DatasetReader.Read` and `BarFeed.Open`
both **require** a `BarAudience` and both take the holdout decision inside, before the file is opened:
`BarAudience.Pipe(role)` is every caller on the agent channel — both directors and a connection that
proved no role, refused identically — and the only audience that may read past a cutoff is
`BarAudience.Referee`, which is `internal` to `TradeAgent.Core`. So the gateway, the pipe server and the
CLI **cannot mint one at all**, and `HoldoutLedgerTests` holds the list of public doors that produce a
`BarAudience` to exactly one entry by name. A window is served only when its END is **proved** to be
before the cutoff: an unbounded `to` asks for every bar there is and is refused, because reading "no
end" as "up to the cutoff" is clipping with extra steps. A refused window is `HOLDOUT_WITHHELD` — its
own code, because the frame was well formed and the data is there, so the repair is an earlier window
rather than a fixed frame or more months — and it is **never truncated**: an answer quietly cut short is
a different window from the one that was asked for, with nothing in the reply to say so. A forgotten
`Refusal` yields an EMPTY window, never a held-back bar. `data-list` **names** the cutoff and the class,
because the boundary is not the secret — the bars are — and an agent that had to find it one refusal at
a time would spend the owner's money doing so.

## The campaign, the trials and the verdict budget — `src/TradeAgent.Core/Db/CampaignStore.cs`, `Strategy/Referee.cs`

**The referee is code and never a role** (`docs/COUNCIL.md`:55-57). There is no LLM behind any of this, no
role called referee, no workspace folder for it. **The verdict REQUEST is `Ops.Verdict`** (`U-verdict-op`,
`GatewayPipeServer.VerdictFor`, `trade verdict --version <hash>`): models propose candidates and software
decides admission and issues the verdict, `docs/PRINCIPLES.md` § Evidence. **The holdout, the campaign,
the trial and the disposition still have no pipe op and no `trade` verb** — a caller that could open a
campaign would have given itself an unlimited supply of attempts.

**What bounds the one that is reachable, so that it is not "searching the holdout by asking".** The
frame carries a version and at most a dataset: the months are the campaign's own, the scorer is
`ScoringPolicyV1` bound to the sha fixed at open, and the execution model is the judge's default — none
of the three is a parameter. The supply is the **verdict budget counted across the whole renewal
lineage** (three by default), charged by `RequestVerdict` before anything reads a bar. And what crosses
back is `RefereeFeedback.Text`, which reads the promotion row alone: the verdict and a reason class from
a closed vocabulary, no metric, no trace hash and no bar. The gateway adds two bounds of its own: a role
must already have COMPLETED a registered trial of that exact version over that dataset, so a version
nobody measured buys nothing; and a promotion already recorded for (version, campaign) is answered **as
it stands with nothing re-run**, so asking again once the dataset has grown buys no second peek.

**A campaign is opened by the app when the owner sets a holdout, one per holdout dataset.**
`TradingGateway.SetHoldout` writes the cutoff and opens the campaign in ONE transaction, so months held
back always have something counting the attempts made against them. The scoring policy **text and its
SHA-256 are copied onto the row at open** and never updated — precommitment is the point, and a policy
read from a constant at judging time could change between the hypothesis and the verdict. The budgets are
copied too, off `TradeAgentSettings.CampaignTrialBudget` (**200**) and `CampaignVerdictBudget` (**3**),
both choices, both stated: what matters is that they are finite, because an unlimited number of peeks at
one holdout is how a process finds a rule that fits the noise. Zero means no attempts — the reading
`AiDailyCostCap` has, and what an unreadable settings row falls to. **One OPEN campaign per dataset is a
partial unique index**, not a C# check, so two presses racing cannot make two. **Renewal is the only
second campaign**: `CampaignStore.Renew` closes the parent and opens a child carrying `renewed_from`, the
parent's holdout **and the parent's policy text and sha** — so a renewal buys attempts and never an easier
standard.

**A campaign fixes TWO standards at open, and both are copied and never updated.** `scoring_policy` is
`CampaignPolicy.V1`; `paper_policy` is `CampaignPolicy.PaperV1` (schema **23**), which is V1's three
performance clauses without its forward-evidence clause and can confer eligibility for paper observation
and nothing else. Both texts and both SHA-256s are on the row, both are carried unchanged by `Renew`, and
neither is a setting: the paper standard is the app's own constant even where `Open`'s `policy` parameter
overrides the scoring one, because a caller that could choose it could choose how little a paper verdict
has to prove. **The rung-23 migration pins this build's `PaperV1` onto every existing campaign row —
that is the one backfill this rung makes**, and it is the 17 and 21 reading rather than the 22 one: no
campaign written before the rung fixed a second standard because none existed for any of them to fix, so
the build's own is the truth about those rows, while an empty column would refuse a paper verdict on
every campaign of every installation that upgrades.

**A trial is one registered research run, keyed `(campaign, version, run)` and by nothing an agent
chooses.** There is deliberately **no role and no attempt column** — `HoldoutLedgerTests` asserts the
column list — because a trial keyed by the attempt would make a restart a fresh budget and one keyed by
the role would let a replacement team start again (`docs/COUNCIL.md`:131, "survives team replacement"). All
three parts are content hashes or the app's own id, so the same program over the same bytes under the same
execution model is ONE trial however often it is asked for, and a different window or fee is a different
trial because it is a different peek. `kind` is the dataset's `evaluation_class` **as it stood at
registration**, copied rather than joined; a `fixture` run is charged nothing and is never evidence. `CampaignStore.TrialRefusal` is asked **before** the run
(`Backtests.Run`, `CAMPAIGN_BUDGET_REACHED`) and is a LOOK, not the gate: it reads the count in its own
transaction and the run takes minutes, so two roles asking for the last trial both pass it honestly. **The
gate is `RegisterTrial`, which reads the count and writes the row in ONE `Database.Write`**, in the same
transaction as the run row — a run without its trial is a peek nobody was charged for, and a budget two
concurrent callers can exceed by one is not the campaign-wide limit `docs/COUNCIL.md`:36 asks for. A
refusal there **rolls the run back and its figures are never served**: the compute is spent either way,
and the alternative is a run over the held-back data standing in the ledger charged to nobody. A trial
already registered answers Ok even over a full budget — the same question asked again is the row that is
already there, and refusing it would make a restart look like an overrun. A dataset with no holdout has no
campaign, so runs over it are charged nothing at all.

**`Referee.RequestVerdict(version, campaign)` charges before anything runs, and the charge is what opens
the door.** The row is written by the request, not by the answer: a budget checked after the holdout run
refuses nothing that matters, because the bars have been read. Verdicts are counted across the whole
**renewal lineage**, which is what makes `renewed_from` load-bearing — trials renew, holdout access does
not. The version must already be in `strategy_version`. Asking twice for one version is one verdict and
one charge, and it stays obtainable after the budget is full, so a crash between the charge and the
computation does not leave a verdict paid for and unreachable. `Referee.HoldoutFeed(charge)` is the only
door past a cutoff that any assembly outside `TradeAgent.Core` can reach, it opens **the campaign's own**
holdout dataset and no other, and a refused charge answers a refusal rather than a feed. **The verdict
itself is `U-referee-2`** — the holdout run, the promotion record, invalidation, forward evidence and the
delivery — and `strategy_verdict` carries no outcome column: this is the budget and the precommitment,
which are the half that cannot be added after a holdout has been read.

## The consequential boundary — `src/TradeAgent.Core/Db/BoundaryStore.cs`

**`docs/COUNCIL.md`:59-65 in code, and every clause of it is the app's.** A boundary is opened by the app
(today: `Referee.Verdict`, in the same transaction as the promotion), keyed
`kind:entity:revision` — for a promotion, the VERSION and the CAMPAIGN, so re-asking one question buys
nothing and the same version under a renewed campaign is a new question worth the spend. Opening one
inserts two `mission_event` rows, one per director: **two assessments are two turns** (:63), and the ids
are `MissionEventIds.ForRole(Boundary(id), role)`, so a restart, a retry or a re-proposal raises ids the
table already holds. The UNIQUE index on `(kind, entity, revision)` states the key beside the primary key.

**The seal is enforced by the app.** An `assessment` publication is committed at once — hashed,
attributed, unrevisable — and its delivery is written `withheld`, a STATE rather than the absence of a
row, because "not delivered yet" is what `committed` already means and a restart cannot tell a delivery
the app is holding from one that merely failed. `CouncilBoundaries.Release`, run by `CouncilRelay.Deliver`,
flips **both together** once both exist. A second assessment from one director over one boundary is
refused in words, and `PRIMARY KEY(boundary_id, role, kind)` is the same refusal in SQL.

**One challenge per boundary, from either director, after both assessments are delivered** — capped at
`CouncilRelay.ReportLines`, delivered normally because it is worth the peer one turn, and a partial UNIQUE
index refuses the second. **Which boundary a submission answers is the APP's choice** (the oldest open one
this role has not answered), for the reason `CouncilRelay.KindFor` decides what a role's output IS: a
director that chose could answer the easy boundary and let the deadline run out on the other.

**The disposition is written by code and by nothing else.** `default_disposition` is frozen AT OPEN — for
a promotion, `Promotions.Standing(version).IsPromoted ? deploy : hold` — because a default computed when
the clock expires is a default chosen once the outcome is known. `CouncilBoundaries.ApplyDue`, called at
the top of every `MissionLoop.TurnAsync` and launching nothing, writes it when the deadline passes or when
the challenge window closes (both assessments delivered and the one challenge made); `MissionLoop.Idle`
sleeps until the earliest deadline, so it happens on the app's clock rather than whenever an agent
next runs. `disposed_by` can only ever read `policy`: **there is no method that takes a disposition from a
caller**, no pipe op and no `trade` verb anywhere near any of it. Section 9 of the owner's report lists
every boundary with its evidence, its owner and its deadline (rule 10), and an open one past its deadline
reads OVERDUE. **The window is 24 hours and is NOT a setting** — a deadline the owner could shorten under
evidence being gathered, or lengthen to hold an eligible deployment, is the veto :62 forbids.

## The verdict — `src/TradeAgent.Core/Db/PromotionStore.cs`, `Strategy/Referee.cs`

**A promotion is one immutable row whose id is the SHA-256 of the nine facts it binds**: version id,
campaign id, scoring-policy sha, interpreter build, holdout dataset id and its normalised sha **at run
time**, declared execution model, evaluator version and the holdout run id (`PromotionRow.IdOf`, and the
order is part of the contract). That list is `docs/COUNCIL.md` rule 9 spelled out. `verdict` and `reason`
are NOT in the hash — they are a function of the nine that are — and the write is `ON CONFLICT DO
NOTHING` with **no update and no delete method at all**, so the same evidence judged twice is one record
and the first answer stands. Schema 15; the table has **no `invalidated` column**, deliberately.

**`Referee.Verdict(version, campaign)` is the whole of a judgement and every step of it is the app's.**
The charge comes first (`RequestVerdict`, which is what produces the audience), the program is re-parsed
from the recorded source and required to hash back to the version's id, the bars are the campaign's own
holdout read in process, the figures come from the app's own trace, and the clauses are
`ScoringPolicyV1` — code, refused outright when the campaign's fixed policy sha is not the one this build
implements. **The holdout run is recorded under `Referee.RunRole` (`referee`, which `CouncilRoles.IsKnown`
rejects) and is NOT charged as a research trial**: it is the evaluation the verdict budget already paid
for, and charging it would spend the submitter's allowance on the referee's own work. The **execution
model is the judge's**, a parameter of the call defaulting to `ExecutionModel.Frictionless`, hashed into
the promotion so a verdict under one declaration cannot read as a verdict under another — a choice, and
one the row states rather than hides. A *refusal* is a verdict and is recorded; a referee that could not
judge at all writes nothing.

**The clauses, in the order they are applied.** Forward evidence first: the holdout window must begin
**after** the version's `created_at` (`docs/COUNCIL.md`:135-136), compared against the WINDOW and never
against when the run was made, so months that predate the freeze are refused whatever the figures say.
Then: the run must have COMPLETED, it must have closed at least one trade, and its net after its declared
costs must be above zero. Every clause answers a **reason class from a closed vocabulary**
(`PromotionReason`), which is why the row's `reason` column can never hold a figure.

**And where the FIRST clause is the one that failed, the same run is scored a second time — by
`CampaignPolicy.PaperV1`, which can confer eligibility for PAPER and never capital.** `docs/PRINCIPLES.md`
§ Evidence asks that "acceptance of a program, a favourable historical verdict, eligibility for paper
observation, and eligibility for live capital" be four distinct meanings, and adds the clause that makes
a paper loop reachable at all: "forward paper evidence cannot be required before the very first paper run
that produces it". Under `ScoringPolicyV1` alone a version frozen after the cutoff can be told nothing
but `evidence-precedes-the-freeze`. So the holdout is run **once**, V1 is asked **first and unchanged**,
and only on that one answer are the paper policy's clauses — V1's three performance clauses, without the
forward-evidence one — evaluated on the same run. All met is `paper-eligible` with a reason class of its
own (`meets-the-paper-policy-on-historical-evidence`); anything else is `refused` naming the PERFORMANCE
clause that failed, because the freeze is a fact about this arm's existence and tells the submitter
nothing it can act on. **The clauses are really evaluated** — mapping the freeze refusal straight to
paper-eligible is the mutant, and it makes a program that LOSES money eligible on the strength of its
date. A paper-eligible row carries **PaperV1's sha** in `scoring_policy_sha256` and the campaign is
required to hold it, refused in words otherwise; a refusal keeps the campaign's own, because the three
clauses it failed are V1's word for word and the two arms produce the same reason classes. The paper
standard is a **separate text with a separate hash** and never a relaxation applied under the sha the
campaign precommitted to.

**A paper-eligible verdict is DELIVERED and opens NO consequential boundary.** The wake and the
sanitised note happen exactly as for any verdict — `RefereeFeedback.Text` says PAPER-ELIGIBLE in words,
says it is not promoted, and carries no figure, the same disclosure boundary with one more sentence in
it. But paper observation is an ordinary experiment by app policy (`docs/PRINCIPLES.md`, "every ordinary
experiment need not purchase a fixed ceremony"): it risks no capital and the version may be given no
money whatever the directors say, so the 24 h review and the two paid assessments stay the ceremony of a
`promoted` verdict. **And the live path refuses it, categorically and by name.** `Allocations.Record`
asks `Standing.IsPromoted` and **nothing else** — one question, so an `IsPromoted` that started answering
true for `paper_eligible` cannot be hidden by a second check in front of it — and a paper-eligible
version's refusal names "historical evidence; paper only". The dispatch gate is unchanged: no allocation
is still `ALLOCATION_NONE`.

**Invalidation is computed at READ time, never written** (`Promotions.Standing(versionId)`, the one
reader, answering `promoted` / `paper_eligible` / `refused` / `invalidated` / `unjudged`). A
paper-eligible standing is invalidated by exactly what invalidates a promotion, and the policy sha it is
re-checked against is the sha of the policy that PRODUCED it — comparing every row with V1's would
withdraw every paper verdict the instant it was written. It compares the hashes ON THE ROW
with the facts as they are now: the holdout dataset gone, REJECTED, re-collected under a different sha or
reclassified as a fixture; the interpreter build or the scoring-policy sha no longer this build's. A
column would make a version's truth depend on a sweep having run. The dataset's **state** is read off the
ledger rather than re-hashed here — every reader that opens the bars re-hashes them, and this answer is
read on the money path — which is a choice, stated. The newest judgement is the one that answers, and a
refusal is invalidated too: "refused on evidence that no longer exists" is a different statement.

**The verdict is delivered as one wake and one sanitised note.** `PublicationKind.Verdict`, published by
`referee` to Research through `PublicationStore.Commit` — one transaction for the artifact, its delivery
and the single paid turn — with the wake keyed by the **promotion** (`MissionEventIds.Verdict`,
`docs/COUNCIL.md`:64, deduplicate by entity), so re-delivering one judgement buys nobody a second turn.
`RefereeFeedback.Text` is the only thing that decides what crosses and it reads the promotion row alone:
**the verdict and the reason class, and no figure from the held-back months** (:196-197). The Situation
names what is promoted (`MissionSituation.PromotedLine`, `docs/COUNCIL.md`:74) and says so as "none" with
the reason when a promotion no longer stands. Section 8 of the owner's report lists every promotion,
refusal and invalidation under "measured by TradeAgent", and its "no evaluation evidence" gap goes only
when a run or a verdict exists. **The holdout run is listed in that report without its figures**, because
`trade report` serves the same document to the agent — naming it is the record, valuing it would hand
back through the report exactly what `data-bars` and `backtest` refuse.

## The capital allocation — `src/TradeAgent.Core/Db/AllocationStore.cs`, `Gateway/TradingGateway.cs`

**An allocation is one immutable row whose id is the SHA-256 of the seven facts it binds**: version id,
promotion id, policy version, `max_quantity`, `max_notional`, currency and `effective_from`
(`AllocationRow.IdOf`, and the order is part of the contract). `effective_to`, `reason` and `at` are NOT
in the hash — they are the account of the decision and not the decision — and the write is one `INSERT …
ON CONFLICT DO NOTHING` with **no update and no delete method at all**. Schema 20. A ceiling is lowered
or withdrawn by recording a FRESH allocation from a later instant, and `Allocations.StandingFor` answers
with the newest row in force: a limit its subject could edit is not a limit, and a capital decision that
can be moved after the outcome is known is not a record (`docs/COUNCIL.md`:210-212).

**What capital is allocated TO is a promoted strategy version — a choice, stated.** `docs/COUNCIL.md`
never names the subject of an allocation; rule 8 makes a promoted version the only executable thing, so
it is the only thing a ceiling can be attached to. `Allocations.Record` refuses unless
`Promotions.Standing` reads `promoted` **at that instant** — not "a promotion row exists" — so evidence
TradeAgent has since withdrawn allocates nothing; and the foreign keys to `strategy_version` and
`strategy_promotion` make an allocation of capital to nothing unwritable rather than merely unwritten.

**The ceiling is the owner's own declared number, not a fraction of a balance — a choice, stated.**
TradeAgent does not persist an account balance or an equity curve, and a ceiling derived from a figure
this app re-reads from a platform would move without anyone deciding that it should. So `max_quantity`
(and `max_notional` where the owner sets one) are typed on the Safety page in the account's currency and
mean exactly what they say. Zero is a real allocation and means the version may open nothing.

**The ceiling bounds EXPOSURE, not one order**, and it is evaluated **inside the dispatch gate on the
same position reading** as the open-position cap, the unresolved-reducer refusal and the loss budgets.
The arithmetic is what the version would be HOLDING — the account's position on the instrument, plus
every opening placement of that version the store still calls open, plus this order — against
`max_quantity`, and that figure multiplied against `max_notional`. Decided above the awaited reads
instead, two placements in flight together each read the same empty account and both pass one
allocation. Refusals are `ALLOCATION_NONE` (nothing stands behind this version) and `ALLOCATION_EXCEEDED`
(something does and this order would pass it); **nothing is sent and no request row is written**. A
version's exposure that cannot be worked out at all answers `RISK_CHECK_UNAVAILABLE` rather than an
allocation code — no ceiling was breached, TradeAgent could not tell whether one would be.

**A close and a reduce always pass**, the loss budgets' reason: a ceiling that stopped an account being
flattened would be a trap. They are still attributed. **An order naming no version is not gated at all**
— the owner's own buy and the emergency press have nothing to be charged against.

**A position is not attributable to a version, and the ceiling counts it anyway — a choice, stated.** The
platform reports one number per instrument and says nothing about who opened it, so a position the owner
opened by hand counts against a strategy trading the same instrument. It is conservative in the one
direction that is safe: it can only refuse.

**What an order was placed under is two columns, never the parameters blob.**
`execution_request.strategy_version_id` and `allocation_id` are written by the INSERT that creates the
row — both of them, by `TryCreate` and by `TryCreateFlagged` — and by no later update, and they are read
back from their own columns. `docs/COUNCIL.md`:210-211: which strategy or allocation caused an operation
cannot be recovered later, and a text column anything can rewrite re-attributes a sent order. Both are
nullable and NOT backfilled.

**Allocating is two presses, in process, and the agent cannot reach it.** `TradingGateway.Allocate` has
no `trade` verb and no pipe op, like every other operator authority. Widening is two presses for the
reason `Widens()` is; so is narrowing, because a lower ceiling is a new permanent record and no press
anywhere takes one back.

**The owner and the roles are told.** Section 4 of the daily report lists one line per standing
allocation — version, ceiling, currency, `effective_from`, policy version — printed as `none` rather
than omitted when nothing is allocated, and an allocation whose promotion no longer stands is **listed
and marked WITHDRAWN** rather than filtered out: the row is still on the table and the version may trade
nothing. A ledger that could not be read becomes a named gap instead of an empty list.
`MissionSituation.PromotedLine` says what is allocated and **carries no holdout figure** (:196-197), and
says so plainly when nothing is, because a turn told only "promoted" would plan a deployment the gateway
refuses with `ALLOCATION_NONE`.

**The honest limit, stated.** No runner emits a live or paper intent yet, so the deployment this gate
ceilings does not exist. What is contracted here is the gate, end to end, and it is what will be there
when one does.

### U-paper-envelope — an allocation now has a SCOPE, and the owner grants paper experimentation once

**An allocation is live or it is paper, and schema 25 is where that becomes a fact rather than a
convention.** `strategy_allocation` gains `scope`, `connector_id`, `mode`, `account_id` and
`envelope_id`, all nullable and none backfilled: **a row with no scope is LIVE**, because every
allocation any installation holds was written by `TradingGateway.Allocate`, which is the owner pressing
twice on the Capital card, and an owner's press is never a paper grant. A paper row's id hashes the
seven facts `AllocationRow.IdOf` already spelled **and then the five that make it a paper allocation**
(`PaperIdOf`) — live ids are unchanged and `IdOf`'s order is untouched. The scope facts are in the hash
because two envelopes on two accounts carrying one version at one instant are TWO allocations; hashing
the seven alone collapses them onto one id and leaves the second account reading as allocated under a
row that names the first.

**The grant is `paper_envelope`, one immutable row, written by a two-press card and by nothing else.**
Its id is the SHA-256 of nine facts — platform, account, instrument, currency, the two ceilings,
`max_deployments`, and the two instants it runs between — with `reason` and `withdrawn_at` outside the
hash for the reason `effective_to` is outside an allocation's. **It is refused unless the account is
provably simulated by BOTH witnesses** (`AccountInfo.IsSimulated` AND `Capabilities.IsPaper`) and the
mode is PAPER at the press. That is deliberately stricter than the `||` the dispatch gate's own
`MODE_ACCOUNT_MISMATCH` check uses, and the difference is stated: that check refuses ONE order and
either witness settles it, while this is a standing authority the app will spend without the owner in
the room, and an installation whose two witnesses disagree is one where nobody can say which is wrong.
**Withdrawal is the one UPDATE in the product's ledgers that is not a fresh row**, and it is written
under `WHERE withdrawn_at IS NULL`, so it is write-once by the statement: an allocation is superseded
because a ceiling is a number, whereas withdrawing an envelope must stop EVERY row beneath it at once,
which a new row cannot say. It only ever removes authority, so it is one press plus a confirm.

**`Allocations.StandingFor` became two readers and the money path splits on the mode.**
`StandingForLive` sees live rows only. `StandingForPaper` sees paper rows only, and matches the
platform, the account and the word PAPER on the row, then asks the envelope ledger **at this instant**
— never a copy stored beside the allocation, which is why one press on the withdrawal card stops every
experiment under the grant. `AllocationStanding.Authorises` keeps its LIVE arm as ONE question
(`IsPromoted`), so an `IsPromoted` that started answering true for `paper_eligible` is still catchable
there; its PAPER arm asks the grant and the verdict. In `LIVE_CONFIRM` and `LIVE_AUTONOMOUS` the paper
ledger **is not read at all** — a paper allocation is not refused there, it is never looked at.

**In PAPER mode the split is by ACCOUNT, and that is a choice, stated.** On an account under a standing
envelope, only paper rows authorise: that account is the one the owner handed over, and what may trade
on it is what the grant allows. Anywhere else in PAPER mode the gate reads LIVE rows exactly as it did
before this unit — the owner's declared ceiling has bounded a practice order since schema 20
(`AllocationGateTests`), that is a guard rather than an oversight, and removing it would leave a
promoted version placing practice orders of any size the per-order limits allow.

**On an envelope's account, an AGENT order naming no version is refused `ENVELOPE_ACCOUNT_RESERVED`.**
An unattributed order is ungated everywhere else — the owner's own buy and the emergency press have
nothing to be charged against — and on this one account that reading has a hole in it: an agent placing
there would be trading inside the grant while standing outside every bound the grant has. **The owner's
own press is never refused by it**, for the loss budgets' reason, and a ledger that cannot be read
answers "not reserved" rather than locking the account.

**`TradingGateway.AllocatePaperDue` is the app's own policy, run on a clock and not on a press.** Every
version whose verdict stands as `promoted` or `paper_eligible`, with no paper allocation yet and whose
frozen program trades the envelope's instrument, is written into the grant at the **envelope's own
ceiling** — nothing here is derived from a balance or from a verdict's figures — with a reason of
`app policy: <verdict>`. `Allocations.RecordPaper` re-asks every bound inside the write: the verdict at
that instant, the grant standing, both ceilings, and `max_deployments`, which counts OTHER versions so
that re-recording the one already in the envelope is not a second deployment. It is called from
`MissionLoop`'s `ApplyBoundaryDeadlines` seam — `docs/COUNCIL.md`:62, a policy on an agent's clock is a
policy an idle agent can hold — and again straight after a verdict is delivered, where it returns
nothing to the caller. **One wake and one sanitised note to Research, keyed by the ALLOCATION**
(`MissionEventIds.PaperAllocation`), so a sweep that runs every tick buys nobody a second turn; the
note carries the version, the account and the owner's own ceiling and **no figure from the held-back
months**, and says in words that it is not capital and not live authority. Section 4 of the owner's
report lists paper allocations in their **own list**, every line marked `PAPER — no live authority`,
because "your money is behind this" and "this is being watched on a practice account" are the two facts
this product most needs never to blur. `MissionSituation.PromotedLine` says the same thing in the same
words, and its last arm now says how a verdict is asked for — `trade verdict --version <hash>`, bounded
by the campaign's budget of final judgements, answering with the verdict and the reason class and never
a figure.

**The honest limit, stated, again.** Nothing runs a paper allocation. No runner emits a forward intent
yet, so what this unit closes is the arrow from a verdict to an allocation the gateway will honour, and
the experiment the allocation is for does not exist until something dispatches one. **`U-deployment`
closed the next link and not that one:** there is now a RUN with an identity, a cursor and a write-ahead
operation ledger, and the thing that still does not exist is a runner emitting an entry.

## The paper deployment — `src/TradeAgent.Core/Db/DeploymentStore.cs`, `Gateway/TradingGateway.cs`

**A deployment is one immutable row whose id is the SHA-256 of the seven facts it binds**: version id,
allocation id, envelope id, connector id, account id, symbol and `started_at` (`StrategyDeploymentRow.IdOf`,
and the order is part of the contract). The state, the cursor, the two reasons and the two later instants
are NOT in the hash — they are what happened to the run and not what it is, exactly as `effective_to` is
outside an allocation's id. Schema 26. **The seven identity columns are immutable and no statement in
`DeploymentStore` updates one**: state moves only through `Deployments.Start`, `Suspend`, `Resume` and
`End`, four methods with one guarded `UPDATE` each whose `WHERE` names the state it comes FROM, so a
transition is write-once by the statement and holds between the two processes that open this database.

**A platform, a mode or an account that moves SUSPENDS the run and never retargets it.** All three are
compared, and none of them is a default: an account id is unique only within a platform, and the same
platform and account are a different undertaking in LIVE than in PAPER — the reading
`FlattenForBreachAsync` already takes of a loss closure. A suspended run dispatches nothing and resumes
by itself when the three match again. **Reading the mode alone is the mutant**, and it leaves a run
active while the gateway is operating a platform and an account it was never started on.

**The execution identity is in-process only.** `AgentContext.Deployment(id)` gives a session of
`deployment:<id>` on every `execution_request` row, beside the version and the allocation columns that
were already there. `AgentContext.ForAgent` — the only factory the pipe server uses — cannot build one,
which is the defence; the reserved name refused at the pipe and the pipe's refusal to serve a
deployment's rows are tripwires. `MayPlaceOrders` is true and the kill switch still stops it, because it
is not an operator. **In `LIVE_CONFIRM` and `LIVE_AUTONOMOUS` it is refused outright**
(`MODE_FORBIDS_EXECUTION`), above the allocation gate and re-asked at the moment of dispatch: a paper
experiment cannot become a live one because a mode changed while its reads were in flight. Under it
`PlaceAsync` passes EVERY existing gate unchanged — freshness, the open-position cap, the
unresolved-reducer refusal, the loss budgets, the owner's per-order limits, the allocation ceiling, the
kill switch. Nothing is skipped for being the app's own caller. The flatten names the deployment's own
VERSION, because the account is under a standing envelope and an order naming none is refused
`ENVELOPE_ACCOUNT_RESERVED` there — that refusal is right and this is what satisfies it.

**An operation is written before it is dispatched, and that is the whole of the guarantee.**
`deployment_op` is keyed by the gateway's own `request_id` — `dp-<12 of the deployment>-<the bar's open
in whole minutes>-<sequence>`, sendable and far inside the 64 characters a client order id may run to —
so the operation and the order it became are one join and not a guess, and a re-plan after a restart
presents the same id for `ExecutionRequestStore.TryCreate` to collapse. It is `planned`, then
`dispatched` immediately before the call, then `resolved` only on a TERMINAL answer or `refused` only
when nothing was sent. **An UNKNOWN answer stays unresolved, holds the cursor and is never re-sent.**
The cursor is the last bar every one of whose operations settled — not the newest settled bar, because
an unresolved operation on an earlier bar is an order that may be live. An end whose flatten left NO
record is the case the write-ahead row buys: with the row there a restart leaves the close alone; without
one, "sent and lost" and "never sent" are the same picture.

**The app's own policy starts one, on a clock and not on a press.** `TradingGateway.StartPaperDeploymentsDue`
runs on `MissionLoop`'s periodic seam straight after `AllocatePaperDue` and writes at most one run per
standing paper allocation, while the envelope has room — and what occupies a slot is every run that is
not over PLUS every ended run whose last operation has no answer, because **a replacement waits for a
flat, reconciled end**. `ReconcilePaperDeploymentsAsync` runs in the app's own background loop and in the
gateway host's, at start-up and on every pass: it settles operations from their order rows, suspends,
resumes, ends and dispatches. `EndPaperDeploymentAsync` cancels the run's working orders first — an end
that closes the position and leaves an opener on the book has flattened nothing — then closes through
`CloseAsync`, then records the reason. **Ending leaves the allocation and the grant exactly as they
were**: a run is not a decision, and ending it must not quietly withdraw what the owner granted.

**Who may ask.** Starting, suspending and resuming are the app's alone: no `trade` verb, no pipe op, and
an agent that wanted a deployment has nowhere to ask — the rule `Allocations` and `Envelopes` keep.
`trade deployment list` is a READ for every role and carries no request ids. `trade deployment stop --id`
is in `Ops.Mutating` and ENDS one, which is reachable from that channel because it only ever removes
exposure — the same reduction-only exception `close` and `cancel` already have. The owner's own press,
**Stop paper deployment** on the Safety page, is one press plus a confirm for the reason Withdraw is:
nothing takes it back. `status.deployments` lists what is not over plus every ended run with an
unresolved operation, and section 4 of the owner's report prints one line per deployment under
*running forward on paper*, every line marked PAPER and no live authority.

## U-promote-bounds — no execution bounds, no promotion — `src/TradeAgent.Core/Strategy/Referee.cs`

`docs/COUNCIL.md`:96-97 says a **promoted** strategy declares its timeframe, its required data
freshness and its maximum decision age. `U-freshness` built the three declarations, the columns that
carry them onto a verdict and the gate that refuses a stale decision at dispatch — but left them
OPTIONAL in the language (all three or none), so a promoted version could declare none, emit an intent
with no `IntentDecision` on it, and be dispatched however old the bars behind it were. This unit is the
word *promoted* in that sentence, enforced.

**`Referee.Verdict` refuses a version whose FROZEN PROGRAM declares none of the three, and the refusal
names all three.** The bounds are read off the recorded source re-parsed, never off `strategy_version`'s
own columns — a restatement some earlier writer put there. A source that no longer parses, or that
parses to a different program, falls through to the refusals `Verdict` already had.

**No promotion row is written at all — not even a `refused` one.** A recorded refusal is a verdict about
EVIDENCE: the budget was spent, the held-back months were read, and the version lost on the figures.
This refusal is about the submission. Writing it as a verdict would put a permanent row in the ledger
saying a program's evidence was judged and found wanting when it never was.

**It is asked BEFORE the verdict charge — a choice, stated.** The answer is in the text this
installation already holds and no holdout bar can change it, so charging the scarcest budget in the
product for it would spend the submitter's allowance on a fact three lines of its own program settle.
`RequestVerdict` and `HoldoutFeed` are deliberately NOT changed: the rule is about promotion, not about
who may look at evidence, and a bound-less version can still be charged and backtested like any other.

**Nothing already promoted is invalidated by this unit.** `PromotionState.Invalidated` is what
`docs/COUNCIL.md`:35 reserves for a changed ASSUMPTION — the dataset, the interpreter build, the scoring
policy — and this build's judgement about what may be judged is not one of the assumptions those
verdicts rested on. Withdrawing them by code would also be the app restating a record after the outcome
was known, which is the move `Promotions` has no method for on purpose. `Promotions.Standing` is
untouched, and a promotion recorded without bounds goes on reading `promoted`.

**Section 4 of the owner's report names them instead.** A standing allocation whose promotion carries no
bounds is printed **and marked NO EXECUTION BOUNDS**, naming the three and saying the dispatch gate
cannot judge how stale anything that version decides is. It is the opposite of the WITHDRAWN mark and
reads as its opposite: the allocation is live, and what the owner cannot rely on is the staleness
refusal. A withdrawn allocation is not marked twice — the gate it would be judged at is never reached.

**The language is unchanged.** The three declarations stay optional and all-or-none, because requiring
them in the parser would refuse the program texts this installation has already accepted. Promotion is
the narrower place, and it is the one the sentence names.

## U-allocator-2 — evolution: parentage, comparable trials, an exploration reserve, retirement

**Schema 21.** `docs/COUNCIL.md`:201 ("evolution adds versioned parentage, comparable trials,
exploration and bounded replacement; directors are evaluated too") and :218-226 in code, except the
parts named at the end of this section as waiting on an entity this build does not have.

**A version's parent is DECLARED by its submitter and never inferred, and it is outside the version's
hash.** `strategy_version.parent_version_id`, self-referencing so a claim about a version this
installation does not hold is unwritable; the `backtest` op's `parent` field is the only route and it
grants nothing. Parentage outside `StrategyProgram.StrategyId` is the point: the same twelve lines
must be ONE version whatever their submitter says about where they came from, or every run, trial and
verdict already charged against the first is evidence about a row nobody can find. `RecordVersion` is
`ON CONFLICT DO NOTHING`, so the FIRST declaration stands and a resubmission cannot restate its own
ancestry. The app guesses no parent from the role's last version, the clock or any similarity — two
unrelated programs from one role would read as parent and child, and every count taken over an
ancestry would be a count over an invention. A version with none is its own root, which is a statement
and not a gap.

**A variant is charged to its ancestry's campaign lineage — `strategy_trial.charged_to`, beside
`campaign_id`.** `campaign_id` is the PEEK (whose held-back months this run read up to);
`charged_to` is the COST (which family paid). A campaign is opened per holdout dataset, so before this
a family that had spent its attempts on one holdout could tweak a number, get a fresh id, run against
a second held-back dataset and be charged against a budget nothing had touched. `ChargedCampaignFor`
walks the declared ancestry to the ELDEST ancestor already charged somewhere and then forward through
that campaign's renewals to the one open now — renewal buys the family trials, and a closed campaign
is not where a new charge belongs. **It can only tighten:** `TrialsCharged` counts a row under either
number and `RegisterTrial` refuses when EITHER budget is full, so a declared parent never buys a trial
the run's own campaign would have refused. Backfilled to `campaign_id`, which is what every trial
before this rung genuinely was.

**The trial budget is TWO POTS that sum to it: `exploration_budget` and the rest.** An exploration
trial is one registered by a version with no declared parent, or one whose parent's
`Promotions.Standing` does not read `promoted`; `strategy_trial.exploration` records which pot, copied
at registration for the reason `kind` is copied — a parent promoted next month must not reclassify
what a trial cost last month, because a count that moves under a promotion is a budget the process
being measured can move. Two pots rather than two independent caps is what makes the submitter's own
parent declaration safe to take: declaring one moves a trial between pots and **creates no allowance**.
The honest limit, stated: the app cannot verify that a program is really derived from the parent it
names, and a submitter whose exploration pot is full can reach the other pot by claiming a promoted
ancestry — what it cannot do is widen the campaign. `CampaignExplorationBudget` is **50 of 200**, a
choice, copied onto the campaign at open like the other two budgets, clamped to `[0, trial_budget]`;
a campaign opened with none declared records its whole trial budget, which is what every campaign
before schema 21 had. **`TrialRefusal` and `RegisterTrial` now ask ONE rule** (`TrialAdmission`, the
shape `AiAdmissionRule` has) and the COUNTS are read only inside `RegisterTrial`'s own
`Database.Write` — a reservation taken outside it is the look `U-referee-1` named, two roles both
honestly told there is room and both taking it.

**Retirement is the boundary machine `U-council-concurrent-2` built, not a second one.**
`CouncilBoundaries.OpenRetirement` opens `kind=retirement` over a version, with the policy's default
frozen at open: **bounded replacement** — retired when a successor that declared it as parent has been
promoted and that promotion STANDS, kept otherwise. `ApplyDue` applies it. `Retirements` computes the
answer at read time from the newest disposed retirement boundary over that entity; there is no
`retired` column and no retirement table, for the reason `strategy_promotion` has no `invalidated`
one, and a later `keep` reinstates a candidate without erasing that it was put up. **The disposition
writes no DELETE and no UPDATE to any history row**: trials, runs, verdicts, promotions and the
standing capital allocation are exactly what they were, no reconciliation is cancelled, and the
dispatch gate goes on enforcing the ceiling — a deployed strategy has its own lifecycle (:223). What it
does is FENCE what comes next: a NEW trial and a NEW verdict are refused in words that say what was
not taken away. A trial already on the table and a verdict already charged still answer Ok, because
neither is a new assignment and refusing them would make a restart look like something retirement had
removed. **It is not a new permission** (:225): no `trade` verb, no pipe op, in-process only.

**The candidate is a strategy VERSION — a choice, stated** — because the doctrine's subject at
:219-222 is a candidate AGENT and no such entity exists in this build. A version is what this product
can select over: declared parentage, a comparable trial history, a promotion record.

**The directors are evaluated too.** An assessment must declare, each on a line of its own,
`RECOMMENDATION:` from `BoundaryDisposition` and — where the app can measure one —`BASELINE:` from
`PromotionState`; an assessment declaring neither is refused in words, published nothing and bought
nobody a turn, because a recommendation the app inferred from prose would be the app's reading of a
director rather than the director's word. Both are sealed with the assessment onto
`boundary_submission`. `boundary_event.review_baseline` is the APP's measurement of the same subject at
the registered review time, written by `ApplyDue` in the same UPDATE as the disposition. **Two
instants, both frozen:** a baseline read at review would be compared with itself and every forecast
would read correct. `CouncilBoundaries.Records` compares the four, one line per director per settled
boundary **including the director that submitted nothing** — silence is a timeliness record, and a
list of only the assessments that arrived would be a record of the diligent. Section 8 of the owner's
report prints them, under "measured by TradeAgent" because the app froze every part of them. A
loss-budget boundary has no measurable baseline in this build: `BoundaryBaselines.Measurable` says so,
no baseline is required of an assessment there, and the record says "no baseline" rather than
inventing one.

**NOT built, and waiting on an entity this build does not have.** Everything at `docs/COUNCIL.md`
:219-222 whose subject is a TEAM or a CANDIDATE AGENT: the heritable candidate definition (model,
mission, tools, memory seed varying while permission ceilings, scorer and supervisor never do),
diversity budgets, bounded BIRTHS and turnover, probation and rollback, and a fixed selection protocol
with registered review times across candidates. There is no team, no candidate agent and no team
incarnation in this product, so each of those would need a record of something that does not exist.
Also not built: any automatic trigger that decides WHEN a candidate is put up for retirement — that
policy is about a population, which is the same missing entity. `OpenRetirement` is in-process and
this build calls it from nowhere; what is contracted here is the event, the disposition, the fence and
the record.

## U-approve-gates — one gate sequence, two callers, and the allocation the press went out under

**No schema.** REVIEW 2026-09-16 finding 4 in code. `PlaceAsync` ran four gates on its single position
reading inside the dispatch gate and `ApproveAsync` re-ran two of them; the two it dropped were the
capital gate and the reconciliation refusal, two of the five `docs/COUNCIL.md`:14-15 says EVERY order
passes. Two arms of one cause, so one structural fix.

**The four position gates are ONE method — `TradingGateway.PositionGatesOrThrow` — and both callers
call it.** `PlaceAsync` and `ApproveAsync` each take their own single `GetPositionsAsync` reading
inside `_dispatchGate` and hand it in; the sequence is the open-position cap, then the
unresolved-reducer refusal, then the loss budgets, then the allocation ceiling, and the order is part
of the contract. Four gates asking the platform the same question one after another could be told four
different answers and refuse on the oldest of them, which is why the reading is taken once and handed
in; and the reading is the CALLER's because the caller decides when — before the record exists on the
placement path, after the mode, platform and account re-checks on the approval path. A caller that
wants these gates gets all four or none. So a parked order whose capital the owner has withdrawn is
answered `ALLOCATION_EXCEEDED` (or `ALLOCATION_NONE`) exactly as a fresh one is, and a parked reduce
over an order this gateway cannot account for is answered `CLOSE_UNRESOLVED` exactly as a fresh one
is. The loss budgets keep their place below the reducer refusal, and they and the ceiling each start
at `CanIncreaseExposure`, so **a close is never refused by either** — the trap a closed day would
otherwise be. `LOSS_BUDGET_REACHED` and `APPROVAL_PREDATES_LOSS_BREACH` are unchanged, and the second
is still judged above the mode, before this sequence and before the budgets that could mask it.

**The sequence answers with the allocation, and the approval path records it before dispatching.**
`ExecutionRequestStore.Attribute` is the one update that names `allocation_id`, and it writes only
while the record is still `AWAITING_APPROVAL`: a record the wire has seen can never be re-attributed,
which is the property `ExecutionRequest.AllocationId` exists to have. The row that authorised the
frame about to leave is the one standing at the PRESS, not the one standing when the proposal parked —
the allocation ledger has no update, so a ceiling is changed by superseding it and superseding gives a
new id, and recording the parked one would answer `docs/COUNCIL.md`:210-211 with an allocation that
did not cause the operation. **Null is a value here, not a no-op:** a proposal whose allocation has
lapsed goes out attributed to nothing, because nothing is what authorised it. `strategy_version_id`
has no update at all — it is the caller's claim, carried verbatim, and it does not change while a
proposal waits.

**A MODIFICATION runs none of this**, for the reasons already stated: a change to an order that exists
cannot raise a count of instruments, and the sequence is written against a `PlaceIntent`. What a
modification re-runs is step 7 above, against the target as the book holds it at the press.

**Not in this unit:** the per-order limits above the gate (MED 7), the approval TTL and the mode
re-check (unchanged), and any sweep — expiry is still evaluated only when a person presses Approve.
