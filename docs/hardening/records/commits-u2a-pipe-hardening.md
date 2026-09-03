# Commits on `u2a-pipe-hardening` (tip 5c716aa, base 283d942 — generated 2026-09-03 for the handoff)

Each commit message is the builder's own account of what changed and why. The per-round build/verify records and mutation tables were lost with the session scratchpad; these messages, the tests on the branch, and `HANDOFF-2026-09-03.md` are the surviving record.

## ff9034d — Measure the agent pipe against a peer that stops reading

Two tests and a stub, red before anything is fixed. GatewayPipeServer creates its
pipe with no buffer and sends replies through a WriteLineAsync with no deadline,
so a peer that authenticates, asks for something big and then stops reading parks
the handler in a write nothing can recall. BridgeServer subscribes six lambdas to
the adapter and keeps no reference to them, so nothing can ever unsubscribe.

The reply the backpressure tests provoke is deliberately large. On macOS a named
pipe is a Unix socket with a 16 KiB kernel buffer, so a small reply lands and the
stall is invisible; on Windows the no-buffer pipe stalls on any reply at all. A
material-list carrying four planted notes is ~960 KB, under the 1 MiB frame cap,
and shows the defect on both.

Red: 5 failed, 3 passed. The peer is never dropped (960,527 bytes still owed after
nine seconds, connection still open), shutdown walks away from the stalled handler
instead of closing it, and a disposed bridge is still handed all six adapter events.
The three that pass are the other direction, present so the fix cannot regress them.

WriteTimeout is declared but read by nothing yet, so the tests compile against the
defect rather than against the fix.

## 690492e — Stop the agent promoting itself to operator by saying so

IsOperator was SessionId == "operator", and SessionId came off the wire.
GatewayPipeServer built the context straight from req.Session, and `trade` copies
TRADEAGENT_SESSION into that field verbatim, so TRADEAGENT_SESSION=operator trade
buy asked for operator authority in the one place nothing checked. Measured over
the real pipe before the fix, in LIVE_CONFIRM with live armed:

  a frame with session='operator' placed a LIVE order with nobody approving it:
  {"agent_session_id":"operator", ... "state":"FILLED","connector_order_id":"FB-1",
   "filled_quantity":1, ... "mode":"LIVE_CONFIRM"}

and with the kill switch pressed, the same frame filled anyway. The existing test
that covers this boundary enumerates op names; it never sends a value, so a channel
with no operator OPS still had an operator SESSION.

Authority no longer rides on the string. AgentContext.Operator is the only operator
context there is, built by a constructor nothing outside the type can reach; the
public constructor, ForAgent, a with expression and JSON all yield IsOperator false
whatever the session is called, and IsOperator is private init so the record's own
copy semantics cannot promote one either. The pipe additionally refuses the reserved
word by name and logs it at warn, but that is a tripwire on an agent probing for an
escalation, not the defence.

The constructor stays public deliberately: making it private is the stronger gate,
but AgentContext is constructed by name in ~55 places across two test files this
unit does not own. The type is the gate instead, and a test asserts that property
directly over every public route rather than grepping the source.

## b3b6e68 — Stop one agent that stops reading from holding a connection open

The mirror of bbcd36e on the agent-facing pipe, which that commit left standing on
purpose. GatewayPipeServer created its pipe with no buffer and replied through a
WriteLineAsync with no deadline, so an authenticated peer that asked for something
large and then stopped reading parked the handler in a write nothing could recall.

Measured on macOS before the fix: a peer read one byte of a 960 KB material-list and
stopped, and nine seconds later the handler still owed it 960,527 bytes with the
connection open. Shutdown did not hang on that — it did something quieter. It
returned in 21 ms and walked away, because DisposeAsync awaited the ACCEPT loop and
the per-connection handlers are untracked Task.Runs nobody held. The connection
outlived the server that owned it.

Three parts. An 8 KiB buffer on both branches, the same value AtasConnector was
given, so an ordinary reply no longer waits for a reader at all. A WriteTimeout that
ends the one stalled connection by closing its handle, since cancellation cannot
recall a write the kernel has taken, and records peer_stopped_reading at warn with
the op that caused it. And a register of live connections, closed BEFORE the bounded
wait on the loop, so shutdown neither waits on a stalled writer nor abandons one.

There is no lock on the send path on purpose: a deadline implemented with a lock
shared across connections turns one stalled peer into an outage for everyone. The
test that a second agent is served throughout passed before this change and passes
after; it is here to keep that true.

BridgeServer.Subscribe attached six lambdas and kept none, so nothing could ever
unsubscribe and a disposed bridge was still handed all six events. The handlers are
now fields, removed in a finally outside the reconnect loop so reconnects keep their
subscription, and guarded so that disposal racing the loop's start ends at nothing
subscribed either way.

## a0aa1a7 — Put a deadline on the connector's writes to the bridge

bbcd36e gave BridgeServer.SendRaw a write deadline after the bridge froze on
Windows. This is the same defect facing the other way and it was left standing:
AtasConnector writes an RPC through WriteLineAsync with no deadline, and the RPC
timeout it does have only starts once that write returns, so the timeout meant to
bound an order could not bound the part of it that hangs.

Worse than one stuck order. The write is taken under _sendGate, so one peer that
stops reading parks the first caller in the write and every caller after it on the
semaphore — the cancel and the cancel-all included. The frames a person reaches for
when something has gone wrong are the ones queued behind the frame that is stuck.

Measured on macOS against a peer that completed the real handshake, said a
compatible hello and then read nothing: 1872 of 2000 calls never finished within
20s, against a 1s RPC timeout. After: 4/4 in 2s.

Both the gate wait and the write now carry WriteTimeout, and a frame that does not
land closes the handle so the pending write dies and the bridge redials. Both
deadlines are needed and the tests say so separately: with several callers, the
second one timing out on the gate is enough to free the first, so a single order
larger than the socket buffer — a legal order with a long comment, nothing queued
behind it — is what holds the deadline on the write itself.

A frame the kernel took but the bridge never read may or may not have reached ATAS,
so every one of these paths surfaces as ConnectorTransportException and never as a
rejection. Safety rule 3: only a definite broker refusal may read as definite.

## 7c93181 — Keep the request id when the reply is the thing that gets lost

A drop after a dispatched buy lost the reply AND the id it belonged to. The CLI
minted the id inside the request initialiser and printed it only as part of a reply
that never came, and the server logged peer_stopped_reading with request_id NULL.
So in the one case where the agent must reuse the id, neither end still had it, and
its only recovery was a fresh id — a second real order for a position it asked for
once.

Three ends, all of them:

The CLI mints the id before the frame goes out and prints it on stderr as
"request-id: <id>" straight away, with request_id also in the --json object so an
agent reading only stdout still has it. Verified by running the real binary with
nothing listening: the id prints, then the failure.

On a transport failure it now distinguishes "nothing was sent" from "the reply is
lost", and only the second says so: "reply lost — re-run with --request-id <id> or
check `trade orders` first", plus reply_lost and recovery in --json. A frame that
went out and was not answered is an UNKNOWN order, not a failed one.

The server's drop event carries the request id, in its own column and in the
metadata. That log line is the only surviving link between a lost reply and the
order it acknowledged.

The AGENTS.md the workspace builder writes says the rule in the agent's own words:
never retry a lost reply with a new id, because that is not a retry.

Test: an order with a 64 KiB comment, so its own reply cannot fit the socket buffer,
is dispatched and its reply dropped; the drop record still names the id, the broker
has exactly one order, and replaying that id on a fresh connection returns the stored
FILLED record without placing a second.

## de627e3 — Make the write deadline measure progress instead of elapsed time

The deadline bounded the whole write, which is not a stalled-peer detector. It is a
throughput floor of (reply size / timeout): a ~1 MiB reply against the shipped 10s
default demanded ~96 KiB/s of the agent forever. Review of a0aa1a7 measured a peer
reading steadily at 79 KiB/s being dropped at 10.1s and recorded as having stopped
reading — a healthy agent on a busy machine, disconnected mid-order and then libelled
in the log.

The reply is now written to the pipe in 8 KiB chunks with the deadline on each one,
so bytes accepted resets it. The floor becomes one chunk per timeout, about 819 B/s
at the shipped default, and a peer that is moving at all survives a reply of any
size — while a peer that has genuinely stopped still fails the first chunk that does
not fit the buffer, at the same deadline as before.

Written straight to the pipe rather than through a StreamWriter, because a
StreamWriter hands the runtime the whole frame and gives back one task to wait on,
which is exactly the total-duration bound being removed.

The drop record now also carries bytes_sent and bytes_total. That is the difference
between "this peer is gone" and "this peer is slow" — the distinction the old bound
could not make and got wrong.

Test: a reader paced at ~260 KiB/s, a quarter of the old floor at this deadline,
receives the whole ~960 KB reply and takes several times the deadline to do it. Both
assertions cannot hold at once under a total-duration bound.

## cc7006e — Wait for a handler that is inside the gateway, not just one blocked on a pipe

Registering the pipes fixed the abandoned-connection half and missed this one. A
handler parked in the middle of a place — through to the broker, waiting on it — is
doing no I/O, so closing its pipe does not reach it and disposal walked past. It
outlived the server, the gateway and the database, and the settle that moves the
order out of DISPATCHING ran against a closed connection or never ran. An order that
really reached the broker was left DISPATCHING for ever.

Handler tasks are now tracked and awaited on disposal, bounded at 5s, with anything
still running logged as handlers_did_not_finish rather than waited on.

Tracking alone would have been useless, and finding out why is the substance of this
commit. Disposal cancelled one shared token first, and that token reaches
TradingGateway.PlaceAsync and the connector's wait on the broker — so it ABORTED the
in-flight order rather than letting it settle, which is itself a way to produce the
DISPATCHING-for-ever state this drain exists to prevent. Measured: the in-flight
place unwound in 15ms and disposal "succeeded" having waited for nothing.

So there are two tokens now. _accept closes the door; _cts is what handlers hold and
is cancelled only after they have had their chance. Disposal runs: stop accepting,
close the connections so a stalled writer is freed, drain the handlers with their
token still live, then cancel it.

AppHost disposes server, then gateway, then database, so a settle that finishes in
that drain finishes while both are still open.

Test: a broker that takes 1.5s, an order in flight, the server disposed underneath
it. Disposal waits, and the state read with no polling afterwards is FILLED rather
than DISPATCHING.

## 667b9a2 — Separate a stalled bridge from this process's own send queue

Two faults in the same method, both found by review of a0aa1a7.

The deadline started BEFORE the send gate was acquired, so it measured our own
backlog as well as the peer's reading. Enough concurrent RPCs and a perfectly
healthy, actively-reading bridge was declared stalled and disconnected because OUR
queue ran out ITS clock. The deadline now starts after the gate. The gate wait stays
bounded, because a caller must not queue for ever, but expiring there is Busy: that
one caller fails with an indefinite transport error and the connection is untouched.

And a caller that cancelled during the write released the gate with its frame still
going into a StreamWriter every caller shares. The next caller interleaved with a
half-written frame and the connector sat wedged — Connected still true, no reconnect,
every later frame failing for ever. Cancellation cannot cancel the write, so the
write state is unknown, so the connection now ends the way a timeout ends it. Latent
today: only shutdown and connector-swap tokens reach that path.

Three outcomes instead of a bool, because two of them were being confused. Sent;
PeerStalled, which is a fact about the bridge and drops it; Busy, which is a fact
about this process under load and drops nothing. Both failures are
ConnectorTransportException and stay indefinite — safety rule 3 — but they are
different sentences, because one says the platform stopped listening and the other
says this process is saturated.

Tests: a caller cancelling mid-write leaves the connector disconnected rather than
wedged; and 400 concurrent RPCs against a real BridgeServer with a 50ms deadline
leave the connection up and answering, where before the contention alone killed it.

## d0ffb5e — Test at the deadlines the product actually ships

Every test in this unit set a short deadline so it could run in seconds, nothing
checked that the shipped default was still what the reasoning assumed, and the build
record quoted suite durations taken at 1s as if they described the product. Review of
a0aa1a7 measured a cancel-all behind one stalled write at 9.81s against defaults.

Reproduced independently here: 9.76s. The record's Round 1 figures are corrected
rather than left to be re-derived.

Three deadlines are now pinned by name — GatewayPipeServer, AtasConnector and
BridgeServer all 10s — so changing a default breaks a test instead of silently
invalidating a number in a document. Two more tests run at the real default with
nothing shortened: an emergency cancel-all queued behind a stalled write, and a
stalled peer dropped on the agent pipe. They take about ten seconds each, which is
the point of them.

Worth an operator's attention rather than a silent change: at shipped defaults an
emergency cancel-all queued behind one stalled write waits the better part of ten
seconds. That is the deadline doing what it is set to do, not a defect, but the
number is a product decision and it is now measured instead of assumed.

## bdf9a24 — Stop cancel-all colliding with the agent's own ids and overstating what it did

Carried over from the first adversarial review of 283d942. Both sweeps derived their
per-order request ids as {rid}-{i}, which is a shape an agent can type. An agent that
placed an order with --request-id X-0 and later swept with --request-id X handed the
first cancellation the id X-0, already in the idempotency store as a PLACE, so the
store replayed that record instead of cancelling and the sweep counted it anyway:
cancelled=1, order still WORKING.

Derived ids are now {rid}#intent#index, and a request id containing '#' is refused at
the pipe with INVALID_REQUEST. That is what makes the collision impossible by
construction rather than unlikely: the agent cannot type the shape, because the shape
is rejected on the way in.

And the count was of attempts. cancelled = results.Count reported every order it had
tried, so a sweep that left one WORKING or came back UNKNOWN still said it had
cancelled it. On the one command a person reaches for when they want everything to
stop, that is the worst possible thing to be wrong about. cancelled now counts orders
that actually reached CANCELLED, attempted is reported separately, and not_cancelled
names each one still out there with its state.

close-all had both faults identically and is fixed the same way, plus its null result
— the gateway finding nothing to close for a symbol — is now nothing_to_close rather
than being counted as a closure.

Tests: an order placed under the id the old scheme would derive, then a sweep, with
the claim checked against what the broker actually shows cancelled; the reserved
separator refused; and the count reconciled against reality in both directions.

## 27b6881 — Close the last route to a forged operator, and test every spelling of the word

AgentContext was a record, so it kept its own copy constructor and
AgentContext.Operator with { SessionId = "x" } produced a new operator context with
someone else's name on it — while the comment directly above claimed no public route
could. The code was wrong or the comment was; both are fixed by making it a sealed
class, which has no `with`. Nothing depended on record semantics: no equality
comparison, no deconstruction, and the ~55 constructor call sites in test files this
unit does not own still compile untouched.

Equality is deliberately not reimplemented. Nothing compares these for authority, and
an accidental value-equality check on a security type is a trap rather than a
convenience.

The reserved-session tripwire is now tested over the real pipe in seven spellings —
case variants and leading, trailing and tab whitespace. The type refuses operator
authority whatever the string says, so none of these could escalate; the tripwire is
what is on trial, and it exists so a probe is VISIBLE. One that catches only the exact
lowercase spelling catches only an agent that was not trying. Without the variants,
OrdinalIgnoreCase could be narrowed to Ordinal and the Trim() deleted with nothing
failing.

Each variant asserts the refusal, that no order reached the broker, and that the
attempt reached the engineering log — a probe silently downgraded rather than refused
leaves the operator with no way to know it happened.

## d7597d5 — Make the sweep tests actually sweep something

The fake broker fills every order on arrival by default, so the orders these tests
placed were terminal before the sweep ran, the working list was empty and every
assertion passed vacuously. Found by mutation: reverting the derived-id scheme AND
the count together left the collision test green, which it could not have been if it
were measuring anything. They now place resting orders and assert there is something
to cancel before claiming anything about cancelling it.

## 02aad9a — Make the shutdown drain outlast the order path it is draining

Five seconds was picked, not derived, and it was shorter than the path it had to
cover: at shipped values a shutdown during an order still abandoned it, measured as
DisposeAsync returned after 5.01s, unfinished:1, state=DISPATCHING.

The connector's worst case for one order is three bounded waits in series — the send
gate, the write, then the reply to come back — which at shipped values is 10 + 10 +
10 = 30s. The drain is now 35s: that, plus five for the settle and its write-back.
The arithmetic is in the comment, and AtasConnector.WorstCaseOrderPath computes the
first three from the live values so a test can assert the drain still covers them.
Change a connector deadline and a test fails, instead of an order being abandoned at
shutdown six months later.

The trade is deliberate and worth stating: the app may take up to 35s to close, but
only while an order is actually in flight. An idle handler is freed when its pipe is
closed, which happens before this wait. Waiting is the right side of that trade,
because the alternative is an order that reached the broker and is recorded
DISPATCHING for ever.

handlers_did_not_finish is pinned at error by a test. It is the only trace that an
order may have been left unsettled, so it has to reach whatever an operator reads;
nothing was stopping it being quietly downgraded to info.

Tests arm the broker latency AFTER warming the gateway's instrument and account
lookups, because the fault applies to every connector call and would otherwise land
in the pre-flight checks instead of the dispatch this is about.

## ea1f47d — Mint broker-safe ids instead of ones with a reserved separator in them

Round 2 solved sweep id collisions with a reserved '#' and created a worse problem.
The request id is carried onto the broker order as ClientOrderId "TA-{id}", safety
rule 1 requires that field to round-trip, and the gateway was minting
TA-...#close-all#0 into it. Whether ATAS accepts '#' in a client order id is not
knowable from here — only on the box — so this was a guess in the one field the rule
says must not be guessed at.

Minted ids are now op-{nonce}-{intent}-{index}: letters, digits and hyphens only. The
agent's own id is no longer embedded, which is what keeps the minted one inside the
charset whatever the agent called its sweep; the nonce and the reserved prefix are
what keep it from colliding. The legs come back in the reply, so the agent can still
tie them to its request.

Collision-freedom moves from a reserved separator to a reserved PREFIX: a request id
starting with "op-" is refused. And the charset is now enforced on the way in as
well — letters, digits, '-', up to 64 — because whatever the agent chooses ends up on
a broker order too, and nothing was checking that at all.

Every request id already in the suite conforms, so this narrows what is accepted
without changing what anything does today.

Tests assert every minted id matches [A-Za-z0-9-] and so does the client order id
derived from it; the reserved prefix is refused in three spellings including
uppercase; and five shapes that would not survive the trip to a broker are refused
before an order can carry them.

## 7ded629 — Give the CLI's replay contract a test seam, and test it

The id `trade` mints is the only thing that makes a retry safe, and none of it had a
test: top-level statements in an exe are not reachable from one, so Round 2 verified
the behaviour by running the binary once by hand. Two mutants proved the gap — "stop
printing the id" and "never say reply lost" both left the whole suite green.

The contract moves to CliReplayContract: which calls get an id, when it is announced,
and what to say when a call does not come back. Program.cs keeps the flow and calls
into it.

Tested at both levels, because a tested function that Program.cs has stopped calling
is worth nothing. The functions: orders get an id and reads do not, an explicit id
always wins, two calls get two ids, the announcement goes to stderr and only stderr,
and "nothing sent" is a different sentence from "reply lost" on every path including
the --json object.

And the real binary, twice. With nothing listening it announces the id and then must
NOT claim a lost reply, because nothing was sent. With a service that completes the
handshake, takes the order frame and hangs up without answering, it must say the
reply is lost and name the id to re-run with. That second one is the branch Round 2
recorded as untested — the dangerous case, where the order may be at the broker and
only the acknowledgement is gone.

## 2a71d60 — Let the fake refuse a cancellation, so the sweep count can be tested

Mutant R7 — cancelled counts attempts instead of successes — survived the whole
suite in Round 2, and the reason was the harness, not the tests. The fake broker
cancelled everything it was asked to, so attempts and successes were always the same
number and no assertion could tell the two implementations apart.

RefuseCancel is a one-shot fault in the same shape as the others: the broker refuses
the next cancellation definitively. That is a real thing brokers do — the order filled
a moment ago, or the venue will not take a cancel now.

The test places two resting orders, refuses exactly one cancellation, and asserts
attempted=2, cancelled=1, one entry in not_cancelled naming the stranded order and
its state, and that the claim matches what the gateway actually shows cancelled
rather than merely being internally consistent.

## f518251 — Let an emergency stop waiting after two seconds

Manager's decision, implemented. At shipped deadlines an emergency cancel-all queued
behind one stalled write took 9.76s to come back, and for all of it the owner had a
screen that said nothing while trying to stop. Ten seconds is a long time to be told
nothing.

cancel-all and close-position now wait two seconds for the send gate — a judgment,
not a measurement — and then take the connection down and fail as indefinite with a
sentence written for the owner rather than for a log: the bridge is not responding,
the operation is NOT confirmed, the connection has been dropped and will be retried,
check your positions and orders in ATAS. Worse information than a confirmed
cancellation, far better than silence, and it is the sentence that sends a person to
their platform to look.

Dropping the connection is the point rather than collateral damage: the stalled
writer holding the gate is what made the emergency wait, so closing the handle frees
it and starts the reconnect.

Close is treated as an emergency alongside CancelAll because close-all is built out
of per-position closes — the gateway loops them, so a close that queues for ten
seconds is a close-all that queues for ten seconds per position.

Ordinary agent traffic keeps the full deadline. A quote arriving late costs nothing,
and a caller that is merely queued has no business tearing down a connection.

Both halves tested at shipped values: the emergency comes back in under six seconds
with a readable reason and the connection gone, and an ordinary read still takes the
full deadline and gets the ordinary message.

## 17aa280 — Give a cancellation the fast path whoever asked for it

The fast path keyed on the bridge op, which meant it keyed on the operator's own
button. The agent's cancel-all is not one bridge op: the gateway sweeps it into
per-order Cancel legs, and those fell through to the full deadline. Measured at
9707ms per agent leg against 2006ms for the button, and the legs run in sequence, so
an agent cancelling N orders through a stalled bridge waited about 10N seconds to be
told nothing. Same act, same urgency, ten times the wait, because of where it
entered.

Classified by what the frame does instead. Cancelling an order or closing a position
can only reduce exposure and is worth interrupting a stalled write for, whoever sent
it. Place and Modify can increase exposure and never get the short wait — an order
that opens risk has no claim on an emergency path, and would be the obvious way to
abuse one.

Known gap, written into the source rather than left to be found: the gateway
implements close as a PLACE of an offsetting order, so an agent close-all arrives
here as BridgeOps.Place and is indistinguishable from an order that opens a position.
It does not get the fast path. Closing that means carrying intent down through
ITradingConnector, which is not this unit's to change.

Tests: the sweep leg and the operator button fired at the same moment against one
stalled bridge, both back in under six seconds; and the ordinary case is now a theory
over a read AND a place, so the exclusion that matters is held by a test rather than
by a comment.

## 9e50559 — Tell a busy bridge apart from a dead one before saying which

Emergency gate expiry dropped the connection unconditionally and told the owner the
bridge was not responding. That was false whenever the bridge was reading everything
and the queue was ours: 1500 concurrent 900 KiB RPCs and one cancel-all came back in
2.01s having disconnected a bridge that was perfectly healthy. It also collapsed the
Busy/PeerStalled split from round 2 back into one outcome.

The write now goes out in 8 KiB chunks and records when bytes were last accepted, so
an emergency that gave up on the gate can ask the writer holding it whether it got
anywhere during those two seconds. Bytes accepted means the far end is reading and we
are the queue: the caller fails as Busy, UNKNOWN, connection untouched, and is told
the bridge is busy and to try again. Nothing accepted means it has stopped: drop, and
the existing not-responding message.

Chunking earns its place twice. It is what makes progress observable at all — a
single WriteLineAsync gives back one task that either finishes or does not — and it
also makes WriteTimeout a stalled-peer detector on this pipe rather than a throughput
floor of frame size over timeout, which is the same correction the gateway pipe got
in round 2.

The cost that remains is in the source comment: an emergency behind a genuinely busy
bridge still waits two seconds and returns UNKNOWN. Its frame was never sent, so its
outcome is honestly unknown and no amount of classification changes that. What it
gets is a truthful answer in two seconds instead of a wrong one in ten, and a
connection left up so the retry it advises has somewhere to go.

Test: a real BridgeServer reading everything, 400 concurrent 512 KiB frames holding
the gate, one cancel-all — which must say busy, must not say not responding, and must
leave the bridge connected.

## 3417c5e — Test each cancellation caller alone, so the classification is what is measured

Firing the agent's sweep leg and the operator's button at the same moment let the
button's two-second expiry drop the connection and free the leg, so the leg looked
fast while still being classified as ordinary traffic. Reverting the classification
failed nothing. Each now runs alone on its own stalled bridge. Found by mutation.

## 5c716aa — Bound the id that actually reaches the broker, and test the two caps

Three small things the review found had no teeth.

The length cap was on the request id, not on the string built from it. TA- is
prefixed on the way to the broker, so a 64-character request id left this process as
a 67-character client order id, and safety rule 1 needs that field to come back
unchanged. The bound is now derived: 64 for the client order id, minus whatever
ClientOrderIdFor prefixes, which is 61 today. Read off the real function so a change
there moves this instead of silently breaking it. The test asserts the CLIENT ORDER
ID length, because that is the string the rule is about — a mutant that loosens the
cap fails on it.

The 64 is a conservative guess and is labelled one in the source and in CONTRACTS.md.
ATAS's real limit is NOT VERIFIED and cannot be settled from this machine.

A constant sweep nonce broke nothing, so two sweeps now have to mint different ids —
and both have to really cancel, rather than the second replaying the first's records,
which is the original collision one layer in.

CONTRACTS.md records the restriction as the release-note fact it is: request_id is
letters, digits and '-', 1 to 61 characters, may not begin with the reserved op-
prefix, and is refused rather than truncated; plus what `trade` prints and when to
re-run with the same id.

And the round 3 record said eleven mutants over a ten-row table. Corrected.

