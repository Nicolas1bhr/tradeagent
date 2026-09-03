# Commits on `u2c1-dispatch-recovery` (tip 1e10660, base 283d942 — generated 2026-09-03 for the handoff)

Each commit message is the builder's own account of what changed and why. The per-round build/verify records and mutation tables were lost with the session scratchpad; these messages, the tests on the branch, and `HANDOFF-2026-09-03.md` are the surviving record.

## 1b64130 — Pin what an approval must re-check, against the code that re-checks nothing

Thirteen tests fail on this commit, on purpose: each parks an order in
LIVE_CONFIRM, changes one thing (kill switch, mode, live activation,
chosen account, an unconfirmed order, a dead connection, a stale quote,
the rate / position / quantity limits), and expects Approve to refuse.
Today Approve dispatches regardless, so every one fails with 'No
exception was thrown'. Four more pin gates the safety map listed as
unpinned (blank id, non-positive quantity, notional x ContractSize,
SupportsModify) and pass today.

## 3173d89 — Re-check every gate at the moment a person approves a parked order

An approval went to the wire on the verdict made when the order was parked. Kill
switch pressed since, mode changed since, account cleared or switched since,
connection dead since, quote stale since, limits consumed since: none of it was
looked at. ApproveAsync now runs the same gates a fresh dispatch faces, in the
same order and under the dispatch gate, so "with whatever has been dispatched in
between" is exact. It authorizes the order as the AI's own session rather than as
the operator, which is what makes the kill switch refuse an approval: re-enabling
and then approving is two deliberate acts. It also refuses when the account now
chosen is not the one the record was parked for, because the dispatch sends to the
account the record names. Any refusal leaves the record awaiting approval for a
person to decline deliberately.

An order older than GatewayOptions.ApprovalTtl (15 minutes) is the exception: it is
declined through the state machine, AWAITING_APPROVAL to CANCELLED with a
last_error saying so, and refused with the new APPROVAL_EXPIRED. A request nobody
can ever dispatch must not sit on the Dashboard looking alive. Age is judged before
the other gates so a dead request is not left parked behind a refusal the person
could lift and then walk straight back into. An agent replaying that request id
gets the cancelled record instead of APPROVAL_REQUIRED.

Adds GatewayOptions.Clock (TimeProvider, default TimeProvider.System) and routes
the gateway's six DateTimeOffset.UtcNow reads through one Now property, so the
time-to-live can be proved on both sides of its boundary without sleeping through
it. The reconcile age mixes that clock with dispatched_at, which the store writes
from its own UtcNow; a comment marks it, since fixing it means changing Stores.cs.

The Dashboard shows the approve-by time beside each waiting order and reports a
refusal with the plain-language message and repair, not just the technical string
the two-step button would otherwise show. CONTRACTS.md and the schema's new
approval line describe what the agent can observe.

## 3aee50f — Refuse an approval whose platform is no longer the one connected

A parked record names a connector and an account, and only the pair says where the
order goes. Approve compared account ids alone. Switching platform in Settings
disposes the gateway and builds a new one over the same database, so a request
parked before the switch is still in the store afterwards, and account ids are
unique only within a platform: a simulator and a broker both handing out SIM-001
is what default ids look like, not a contrived coincidence. The consequence was a
proposal made against the simulator dispatching to the real broker.

Approve now refuses unless Connector.Id equals the record's ConnectorId, checked
before the account because asking the wrong platform to look up an account is a
meaningless question. ACCOUNT_NOT_FOUND is the honest existing code: the account
the order was parked for genuinely is not on the platform now connected, and its
repair, choose your account again in Settings, is exactly what follows a switch.

The test puts two named platforms over one store, parks on A, approves through B,
and proves neither broker received anything and the record is still parked; the
control approves the same record through A and it dispatches. The test file's
connector wrapper now overrides identity as well as capabilities.

## cbd7806 — Give the execution request store the gateway's clock

The gateway ages requests on GatewayOptions.Clock, but ExecutionRequestStore wrote
dispatched_at from its own DateTimeOffset.UtcNow, and the reconciler subtracts one
from the other to decide whether an order has been missing long enough for absence
to mean it never landed. The two were comparable only by the accident of both
being the system clock. Substitute a clock, which the approval time-to-live tests
must, and every order looked as old as the gap between the clocks the instant it
was dispatched, so the absence grace window was skipped entirely.

ExecutionRequestStore now takes a TimeProvider, defaulting to the system clock so
existing callers are unaffected, and TradingGateway hands it the same clock it
reads itself. One clock now governs CreatedAt, DispatchedAt, the approval
time-to-live, the rate-limit window and the reconcile age. The comment at the
reconcile site that described the split is replaced by one that describes the fix.

The test moves the clock three hours from the system clock before anything is
written, loses an order before the broker accepts it, and proves the dispatch
timestamp came from the injected clock, that reconciliation refuses to call the
order absent while it is inside the grace window, and that it resolves once the
window has passed on that same clock. LogStore and OnboardingStore are untouched:
their timestamps record when a line was written, and nothing measures a duration
across them.

## 965c95d — Bound the approval age at both ends, and fail closed at each

The age of a parked request was compared with one strict inequality and no lower
bound, and both ends were wrong.

A record timestamped in the future gives a negative age, which no positive limit
can ever exceed, so such a request stayed approvable forever: exactly the state a
time-to-live exists to make impossible. A clock stepped backwards between parking
and approving, or a restored database, is enough to produce it. An age that cannot
be trusted now expires, and the person is told TradeAgent cannot tell how old the
order is rather than being shown a negative number of minutes.

At the limit the comparison is now >= rather than >. ApprovalTtl is documented as
literal, with no 0 = off, but under > a frozen clock leaves the age exactly zero
and a zero limit let the order through, which is a limit of nothing permitting
everything.

Both refusals stay APPROVAL_EXPIRED and both still decline the request through the
state machine, so nothing else about the path changes.

## db98137 — Pin the allowlist and the notional arithmetic on the approval path

Two of the gates an approval re-runs were invisible to every approval test: the
instrument allowlist, and the multiplication of order value by contract size.
Disabling either left the approval tests green, so the re-authorization was
trusting arithmetic and a list that nothing here checked.

The allowlist test narrows the list while an order sits parked and proves the
approval is refused and stays parked, then restores the list and proves the same
approval dispatches. The notional test sets the cap between price times quantity
and price times quantity times contract size, so only the multiplied value
breaches it, which is the arithmetic that would otherwise pass an ES order worth
fifty times the limit.

## cb2ce2f — Say what the approval path actually does, in the three places that overstated it

Three claims did not match the code.

The ApproveAsync comment said an expired request must not sit on the Dashboard
looking alive, which reads as though something removes it. Nothing sweeps: expiry
is evaluated when a person presses Approve and nowhere else, so an expired request
keeps its awaiting-approval row until it is pressed. That is why the row states an
approve-by time. Expiring them in the background would mean calling a sweep from
the app's periodic loop in AppHost, which is outside this unit's files, so the
comment is corrected rather than the behaviour changed.

The schema and CONTRACTS said age is decided before any other refusal. It is
decided before any of the gates, but a request that is not parked at all is still
refused as invalid first. Both now say that, and both now say expiry is not on a
timer. CONTRACTS also records the two age bounds.

MODE_FORBIDS_EXECUTION told the owner the current mode does not allow the AI to
trade. That is true when the mode is observe-only, and false in the two cases the
approval path added: paper and fully automatic both allow the AI to trade, they
just are not the mode the parked order was proposed under. The message now names
the order rather than the AI's permissions, and the repair says which mode an
already-proposed order can be approved in.

## 628aff1 — Pin what a dispatch must leave behind, against code that leaves nothing

33 of these fail: a DISPATCHING record nothing sweeps, an aged one nothing
counts, a connector answer that becomes ACKNOWLEDGED whatever it said, an
exception outside the taxonomy, and two emergency buttons that touch the wire
with no record.

## 37ae0f4 — Treat a touched wire as unconfirmed until something says otherwise

Sweep DISPATCHING records into UNKNOWN when a gateway opens over the store, and
count a record still dispatching past the connector's own deadline as
unconfirmed work. Map every state a platform can answer with instead of calling
the unlisted ones ACKNOWLEDGED, and check that a modify came back carrying the
change. Route every non-refusal exception on place, cancel and modify to the
same UNKNOWN. Give each operator close and cancel a write-ahead record keyed by
the press.

## 052ba1d — Carry the press through the two emergency buttons, and say all of it on the wire

The cancel-all press gets a record of its own so a retried press is recognisable
after the orders it cancelled have left the book. CONTRACTS.md and the runtime
schema now describe the startup sweep, the dispatch age bound, the answer
mapping and the operator records.

## 21b582c — Make the two diagnostic surfaces agree with the gate, and note the reconciler race

Status and the doctor are what a person and an agent read before deciding
anything; both now report a swept record. The reconciler comment says what
happens if it ever meets a dispatch that outlived three connector deadlines.

## eb1dc74 — Correct the caller list on the dispatching-to-cancelled edge

Three callers now settle a DISPATCHING record as CANCELLED, not one. A comment
that names them is a claim with an expiry date, so it says so.

## bfa9376 — Reconcile a cancel or a modify against the order it named

These requests never transmit their own client order id, so matching the broker
on it always missed and the absence rule wrote CANCELLED over an order that was
still working. Read the target instead: cancelled or gone means the cancel
landed, still working or filled means it did not, and a cancel-all is judged by
what is left on the book.

## 7a2d8a2 — Pause execution in memory before asking the database to remember it

Every durable record of an unconfirmed outcome is a write, and a locked or
read-only store made the write throw before the pause, the logs and the health
row ever ran. Latch the refusal in memory first, then persist; a persistence
failure is reported to the caller, retried into the engineering log off the
order path, and does not lift the pause. Setting health no longer fails its
caller when the log row cannot be written.

## d848902 — Close every position, even after one of them fails

A close that failed on the first symbol abandoned the rest: no order, no record,
position still open. Record the failure, pause on it, and go on to the next
position; a store that refuses the record no longer stops the loop either.

## c63dc76 — Count a position closed only when it is flat, and say what is left

A close order that is merely working has flattened nothing, and the old count
said otherwise under the words You closed all positions. Read the account back
and name what is still open instead.

## 283631e — Hold one press of an emergency control until it is known to have finished

The screen minted a new nonce on every confirmed press, so the natural retry
after an unconfirmed close was a new press and a second close. Keep the press
while the gateway has unconfirmed work, repeat it instead of reissuing it, and
say plainly that the previous one has to be resolved first.

## fbe3e70 — Restore the press object the mutation run reverted

The item 3 production change was still uncommitted when a mutation run reset
src, so the previous commit carried only its tests. Same code, committed.

## 20eed1c — Judge a changed price on the instrument's own tick grid

A platform rounds a request to the tick, so 4242.13 comes back as 4242.25 and
the comparison called a modification that plainly happened unconfirmed. Compare
against the request rounded to the nearest tick, keep a price that did not move
unconfirmed, and say cannot tell rather than definitely not when the grid is
unknown.

## 70ed297 — Ask one question about unconfirmed work everywhere it is asked

The doctor, the dev host loop and the unconfirmed card each counted the raw
flag, so a stranded dispatch left the self-check saying nothing outstanding and
the card empty while the gate refused to trade.

## 2f61785 — Point the background loop and the stale comment at the same question

The loop now reconciles when anything is unconfirmed, including an outcome that
could not be written down; the comment claiming other surfaces still read the
raw flag is no longer true of any of them.

## 2f69813 — Say on the wire what a cancel outcome now means, and what a pause can mean

CONTRACTS and the runtime schema carry the targeted reconciliation rules, the
in-memory pause, and how the emergency controls behave now.

## 2a47120 — Latch unconfirmed work per request, and stop the startup sweep swallowing failures

One latch for the whole gateway meant confirming any record lifted a pause
another outcome was holding; it is now a set keyed by request id, released only
by evidence about that request. The sweep's fallback logged nothing when the
store refused the write and could throw out of a constructor; it now latches
first, says so at error where it can, and never stops the gateway being built.
The store's docstring no longer claims it owns no clock.

## 6e000ea — Judge a cancel or a modify only on definite, stable evidence about its own target

Look the target up against the whole book instead of a window measured from the
cancel's creation, which excluded any order that had rested longer than it and
turned absence into a landed cancellation. Absence now waits out the same grace
a place does; a target that is unknown, dispatching, reconciling or
cancel-pending decides nothing; a working target has to hold still across a
grace window before the cancellation is called failed; a cancel-all is judged on
the orders its own press captured; and a modification is confirmed on the
instrument's grid within a tick, never called a definite failure without a
definite refusal.

## b867b14 — Settle each cancelled order from the platform's answer about that order

A sweep returning without an exception says the call was made, not what became
of any particular order, so a record settled cancelled on that basis asserted
something nobody said. The press-level record now says unknown too when the
answer did not account for everything it captured, and the transition table's
comment names every caller that can write CANCELLED from DISPATCHING.

## 1bc7334 — Give a press its own target set, its own completion, and a memory that survives a restart

A press captures what it saw at the first press and every retry acts only on
that, so an order or a position that arrived afterwards is never swept up by it.
Completion is judged by the press's own records and the position it targeted
rather than by whether anything anywhere is unconfirmed, and a press that sent
nothing now says so instead of reporting that it closed everything. The screen
adopts an unresolved press from the store when it is built.

## 0abcc77 — Write the conservative rule into the contract and the runtime schema

One rule at the top of reconciliation, with every branch derived from it, plus
what a press owns and why a quantity that does not match is not a refusal.

## 1e10660 — Make the platform's own time window observable in the fault harness

The scripted connector applied a test's rewrite after the since filter, so a
mutant that reintroduced the five minute lookup window survived every test. The
window now applies to the timestamp the platform reports, which is what a real
one filters on.

