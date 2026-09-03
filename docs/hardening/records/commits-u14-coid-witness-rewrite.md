# Commits on `u14-coid-witness-rewrite` (tip a8b3fb0, base 3f1d8f2 — generated 2026-09-03 for the handoff)

Each commit message is the builder's own account of what changed and why. The per-round build/verify records and mutation tables were lost with the session scratchpad; these messages, the tests on the branch, and `HANDOFF-2026-09-03.md` are the surviving record.

## cce344b — Make the witness file's rename step replaceable

The failure that has to be survived is a Windows sharing violation on
MoveFileEx, and rename(2) on macOS does not consult open handles at all,
so it cannot be provoked where the code is written. Production passes
nothing and gets File.Move as before; a test can pass a delegate that
throws the way Windows would.

## 72e2acf — Ask the churn test the question it was not asking, and pin the rename failures

The Windows CI failure on 3931c10 said a rename was refused; the test
only checked that no temp was left behind. Three of the new tests are
red: a claim whose rename never lands is not readable after a restart,
a fourth refusal in a row escapes the retry budget, and an access error
is not retried at all.

## a159bf4 — Stop a witness rewrite that will not land from losing the claim

The replace is retried five times over 200ms and now covers access
errors as well as sharing violations. When it still will not land the
claim stays in memory, the temp keeps the newer state, and a reader
prefers a newer temp that parses - so a failure at the end of a session
no longer erases the record. Submitting returns whether the claim
reached the disk, and the failure is appended to coid-witness.errors.log
beside the file it is about.

## dfcd234 — Decide the witness recovery by lineage instead of by timestamp

An envelope parses with an empty record list, so a newer {} shadowed a
good committed file and the next save committed the void; and a
preserved older envelope with a later mtime resurrected identifiers the
cap had trimmed, which reach the cross-session reading. A rewrite now
carries the generation after the committed one and a fingerprint of the
committed bytes it was derived from, and is adopted only if both match
and it has records. Timestamps only order candidates now, so a tie or a
backwards clock no longer refuses a real recovery.

## 7c8fdc4 — Give every writer its own temp name and check what actually got committed

Two bridges sharing one coid-witness.json.tmp interleave inside a
rewrite, so one renames the other's content onto the file and still
reports its own claim durable; and a temp consumed by the other writer
made the replace fail with FileNotFound and burn the whole 200ms retry
budget. Temps are now named per process and session, a vanished temp
ends the attempt instead of being waited for, and Submitting confirms
the committed content is its own before returning true. Each writer
keeps at most one uncommitted rewrite, written before the previous one
is removed.

## 62e399b — Stop an unreadable witness reporting a confident zero

An interrupted rewrite with no committed file beside it read as
records:0, io:ok, which for this file means the product never submitted
the identifier being asked about. The read failure now propagates when
nothing readable was found, Token says records:err, and the probe
reports an unreadable witness instead of no experiment has been set up.
A garbage temp beside an intact committed file is still not a failed
read.

## 9e98c3f — Put the witness failure log somewhere it will actually be seen

The sidecar was written and then surfaced nowhere: the probe ignored it,
the support package collected only Paths.Logs, and a restarted process
began with LastWriteFailure null, so a durability gap that ended with
the process was invisible. Token reports io:degraded while the sidecar
exists, the probe prints its tail before any verdict, the support
package carries bridge/*.errors.log, and a line written to it is forced
to be one line so an OS message cannot forge extra records.

## 5e87350 — Refuse an order whose write-ahead record did not reach the disk, and say so on screen

Place now asks the witness whether the claim landed and declines the
order when it did not, above the lock so no side table describes an
order that was never sent. Because a permanent local failure at the
witness path would then refuse every order forever with nothing on any
screen, the failure rides the hello into the ATAS bridge health row,
which reports DEGRADED and names the file instead of saying connected.
AtasStrategyAdapter compiles only against a real ATAS install, so that
half is NOT verified by any compiler here.

## 51f3c85 — Name the right claim in the sidecar, bound it, and measure the retry budget

The failure line read the newest record rather than the one being
written, so an Identified that failed named an unrelated order; and it
said the temp held the newer state even when writing the temp was what
failed. Adds the assertions the surviving mutants slipped through: a
temp whose generation is not the next one, the backoff actually being
taken, the sidecar's per-session line cap and its size cap. The churn
test now reads its durability out of the committed file rather than
through a session, which could be satisfied by the temp it recovers.

## 1107712 — Close the import route the mutation sweep found

With no committed file there is no fingerprint to match, and the branch
accepted any temp that parsed and had records - so a fragment of another
witness's history dropped in the bridge directory would become this
machine's record of what it submitted, with acknowledged ids reaching
the cross-session reading. Replacing that whole condition with true had
left every other test passing.

## 94d3c4a — Never adopt a rewrite with no committed file to descend from

Generation 1 with no predecessor is a shape every first rewrite of every
witness has, not a lineage test, so a fragment of another machine's
history satisfied it and walked its acknowledged ids into the
cross-session reading. The branch has no legitimate case left either:
Place now refuses any order whose write-ahead record did not land, so a
first rewrite that never landed is protecting no order. With nothing
committed a candidate is never adopted, and the sidecar says why.

## 12a34c5 — Take a refused claim back out so no other order can complete it

Place refuses the order when the write-ahead record does not land, but
the claim stayed in memory and the order-event fan calls Identified for
every order in the book carrying a comment - so an unrelated order
bearing that identifier completed the abandoned claim with its own
broker id and became full prior-session evidence for an order this
product never submitted. Submitting now restores the pre-attempt
snapshot on failure. Identified deliberately does not: there the order
is live and the broker id is real. Five tests asserted the sequence the
adapter contract forbids and now assert the one that can happen.

## a863475 — Serialise two writers, and stop calling an unread rewrite durable

A stale second writer replaced the first writer's fresh commit with its
own rewrite of the same generation, deleting a claim after its owner's
read-back had already reported it durable. A save now compares the
committed fingerprint against its own lineage immediately before
replacing, rebases onto the other writer's commit when they differ, and
takes a lock file so two bridges take turns; a miss costs one read and
short-circuits before the retry budget. The read-back asks whether THIS
claim is in what got committed rather than whose bytes won, and an
absent or unreadable destination is no longer reported as durable.

## 758efbc — Refuse a rewrite that shrinks the record set, and refuse to choose between rivals

Lineage authenticates the parent, not the content: a candidate that
descends correctly can still hold fewer records than the committed file,
and adopting one drops committed claims and can put a trimmed identifier
back in their place. Every viable candidate carries the same generation
by construction, so two of them are indistinguishable and mtime was
silently picking; one writer cannot produce that, so it means two
writers or a copy. Both cases now keep the committed file and say so.

## e955c73 — Let a resolved write failure stop being reported

LastWriteFailure was permanent for the life of the process, so a
contended replace that succeeded on the next order left the ATAS bridge
row saying orders are being refused while every order went through. A
commit that carries the same records supersedes the failure, so it
clears; the sidecar keeps the history and whether it is still a live
problem is a separate question.

## 5e5b011 — Stop one crash degrading the witness for ever

The rejected leftover stayed where it was, every later session rejected
it again, every rejection wrote another sidecar line, and the sidecar
merely existing was what made the witness look degraded - so the probe
shouted about a file that harmed nothing, permanently. A rejected
candidate is now reported once and renamed out of the candidate glob
(kept, not deleted), a recovered rewrite is deleted once its records are
committed, and degraded asks whether a failure is unresolved rather than
whether one ever happened. A candidate younger than two seconds is left
alone: it may be another process between its write and its rename.

## 4c0294d — Carry a gap left by an earlier run onto the wire and out of the capability

A failure in a previous session leaves nothing in memory, so the hello
carried null and the ATAS bridge row said READY over a witness with an
unresolved durability gap. Trouble reports all three states - this
session's failure, an earlier run's unresolved one, and a witness with
nowhere to live - and while it is set Describe reports
SupportsClientOrderId false, because a run that cannot vouch for its own
history cannot claim rule 1 is proven. The per-order refusal in Place is
the precise test and is unaffected, so LIVE_CONFIRM dispatch still
works.

## dc197df — Measure the fingerprint's discrimination, and drop an ordering that decides nothing

The whole of FNV-1a's discrimination is in the multiply: with the prime
at 1 it collapses to an xor fold into the low byte and every lineage
test still passes, because each compares a fingerprint against itself.
The fingerprint is part of the file format, so it is public and measured
directly. Candidate ordering is removed rather than tested: a candidate
qualifies on lineage alone and two that qualify are declined, so sorting
by mtime was untested code that looked load-bearing. Adds the two
remaining branch tests - a failed write must not have swept the earlier
rewrite away, and a candidate that cannot be opened is a failed read.

## aac1fe8 — Make the two-writer test actually create the stale writer

It used PriorSessionIds(0) to force a load, which returns early without
touching the file, so the second writer loaded after the first had
already committed and the test passed with no compare-and-swap at all.
Found by the compare-and-swap mutant surviving.

## c167bbf — Keep the destination non-null across the save split, and re-measure the budget

Splitting Attempt out of Save left the replace taking a nullable path.
The retry budget re-measures at 218ms for one fully-refused rewrite and
216ms each over ten, so neither the compare-and-swap nor the lock file
is measurable in the uncontended case.

## 496e2f5 — One owner per witness: refuse a second writer instead of merging with it

Multi-writer was never a requirement - a second bridge is a
misconfiguration - and every interleaving a lock-optional design has to
survive is an interleaving of a scenario the product does not support.
The lock is now mandatory: no lock, no write, and Trouble names the lock
file. A compare-and-swap miss is a refusal rather than a rebase, because
a file that changed under a writer holding the lock is being written by
something whose semantics this build does not know. The read-back
accepts nothing but the bytes just written. Rebase is deleted.

## 049224e — Bump the bridge protocol to 3 because the write-ahead promise changed

A version-2 bridge writes the witness, ignores whether the rewrite
reached the disk, and sends the order anyway, and it omits
witness_failure from its hello so this build cannot see it doing so.
Reading that null as no trouble is the wrong inference - it cannot
report trouble it does not look for - and a current app was accepting
the old DLL and trusting it. The bump routes it to IncompatibleBridge,
which names the version and the repair.

## 4d24d93 — Never ration a safety event out of the sidecar

The per-session quota stopped writing after 32 lines, so a later
write-ahead or acknowledgement failure went unrecorded - and for an
order that is live at the broker this file is the only cross-process
record that the gap happened. The quota now applies to warnings and
markers; failures always go in, and the size bound keeps that finite by
rotating one generation back instead of deleting the file that holds
them.

## fc91771 — Keep readers off the disk: recovery belongs to the owner, under the lock

The candidate scan runs on every read path, including PriorSession on
ATAS's event thread and the probe in another process. A reader that
adopted, quarantined and wrote the sidecar could do all three in the
middle of the owner's rewrite: the candidate it recovers is the rewrite
in flight, and the rewrite then commits cleanly, leaving an unresolved
failure recorded about nothing. The scan is now pure classification; the
findings are acted on only while the lock is held, and a process that
cannot take the lock reads and answers and does nothing else. The owner
re-reads the sidecar tail before marking a gap resolved.

## a8b3fb0 — Stop the probe shouting about a witness gap that already closed

Any sidecar read as THIS FILE SHOULD NOT EXIST, including one whose last
line says the witness committed cleanly afterwards. The witness already
decides that - Trouble is null once the gap is closed - so the probe
asks instead of inferring from the file existing, prints the history,
and says nothing below it is provisional.

