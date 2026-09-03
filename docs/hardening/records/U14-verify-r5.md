# U14 — ADVERSARIAL-VERIFY RECORD · round 5 (leg [2], Opus, same verifier as round 4, §9.3)

**Sha under test:** `6a40fa7bf6e99fe4f38c6d6939bcac1a6938a863` = `e22eec6` + 14 commits.
Worktree `…-worktrees/u14-verify-r5`, branch `u14-verify-r5-probes`. Toolchain
`PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS.
**The box is not mine this round** — the builder's Windows run of the same 417 is a claim I read, not one
I repeat. Round-4 probes cherry-picked: `ProtocolThreeVerifyR4Probes.cs` (`b5295ae`), which the branch had
not carried over; the round-4 witness probes are superseded by the builder's own tests and were not.

## Item 0 — the builder's headline figure, reproduced not trusted

`dotnet build TradeAgent.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)` (Time Elapsed 00:00:03.02),
working tree clean.

`dotnet test TradeAgent.sln` (full-suite run 1 of 2, `scratchpad/r5-suite-run1.txt`):

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 989 ms - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111, Duration: 3 s   - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 231, Skipped: 0, Total: 231, Duration: 20 s  - TradeAgent.IntegrationTests.dll (net10.0)
```

**417 green (75 / 111 / 231) on macOS — the builder's Mac figure reproduced exactly.** Every test the
round-5 record names as added was confirmed present by name before anything else was run.

_(targets filled in as executed)_

---

## Target 1 — THE TWO CODEX HIGH CLASSES AS IMPLEMENTED

### 1a. The lifetime lease (F1 = my round-4 V2)

**Both directions, real OS processes**, driven through a standalone console with a `ProjectReference` to
this worktree's `TradeAgent.AtasBridge` (the real `CoidWitness`, the real `File.Move`). The owner
process writes once and then LIVES without ever calling `Dispose`:

```
### 1. A alive, no overlapping call -> B refused (Codex F1's acceptance) ###
OWNER TA-A pid=22671 submitted=True
TRY TA-B pid=22692 submitted=False trouble=another writer owns this witness (…/coid-witness.json.lock): IOException

### 2. A killed as a REAL process (SIGKILL, no Dispose ever runs) -> B acquires ###
killing owner pid=22671 with SIGKILL
TRY TA-C pid=22700 submitted=True trouble=

### committed ids ###
['TA-A', 'TA-C']
```

Codex F1's acceptance is met with the exact sentence, and the both-directions half holds: a SIGKILLed
owner strands nothing, the next process takes the witness, and `TA-B` — the claim that was refused —
is correctly absent. **Nothing lost, nothing phantom.**

**MV2, my round-4 mutant, is now RED.** `Lease()` `FileShare.None` → `FileShare.ReadWrite`:

```
CoidWitnessTests.A_claim_and_an_acknowledgement_are_both_refused_without_the_lock [FAIL]
CoidWitnessTests.Two_writers_do_not_share_a_temp_name [FAIL]
CoidWitnessTests.A_second_live_writer_is_refused_even_when_it_never_overlaps_a_call [FAIL]
CoidWitnessTests.The_lease_is_what_stops_a_claim_reported_durable_from_being_dropped [FAIL]
CoidWitnessVerifyR5Probes.Unlinking_the_lock_file_does_not_hand_the_witness_to_a_second_owner [FAIL]
CoidWitnessVerifyR5Probes.A_lease_not_disposed_refuses_the_next_instance_in_the_same_process_until_it_is [FAIL]
Failed!  - Failed: 6, Passed: 106, Total: 112
```

At `e22eec6` the same mutant left all 80 green. **Round-4 finding F2 is closed**, and the probe I asked
for is on the branch under the builder's own name.

### 1b. The attack the lease does not survive — an unlinked lock file (new finding R1)

The lease is an advisory `flock` on the OPEN FILE, not a claim on the NAME. Unlinking
`coid-witness.json.lock` leaves the owner holding a handle to an inode with no name; the next writer
creates a fresh inode at the same path and takes its own flock. Measured with real processes:

```
OWNER TA-OWNER pid=22743 submitted=True
--- control: rival refused while the owner lives ---
TRY TA-RIVAL-BEFORE  submitted=False trouble=another writer owns this witness (…lock): IOException
--- now DELETE the lock file out from under the living owner ---
TRY TA-RIVAL-AFTER-UNLINK  submitted=True trouble=
### committed ids ###  ['TA-OWNER', 'TA-RIVAL-AFTER-UNLINK', 'TA-THIRD']
```

**Two live owners, which is the state the lifetime lease exists to make impossible.** What it costs is
bounded and was measured, not assumed: the probe
`Unlinking_the_lock_file_does_not_hand_the_witness_to_a_second_owner` drives the exact MV2 interleaving
after the unlink and **passes** — the compare-and-swap and the read-back still refuse, so no claim
reported durable is dropped. The exposure is one extra writer in the window between the unlink and the
next writer re-creating the lock file; after that the new owner excludes everyone again, and the
original owner is refused by the CAS on its next write.

Ranked **LOW**: it degrades the lease to exactly the CAS-only protection round 4 shipped with (measured
there at 0 lost / 0 phantom over 240 concurrent claims), it needs an external process to delete a file
inside `%LOCALAPPDATA%\TradeAgent\bridge`, and on Windows — the platform that trades — a file open
without `FILE_SHARE_DELETE` cannot be unlinked at all, so the attack does not exist there. **That last
clause is NOT verified by me** (the box is not mine this round); it is the same API-contract reasoning
the builder recorded, and it is the reason this is LOW rather than MED.

### 1c. The lease on `StopBridge` — ruled

`AtasStrategyAdapter` holds `readonly CoidWitness _witness = new()` per strategy instance
(`AtasStrategyAdapter.cs:212`) and releases it only from `StopBridge` (`:468`), reached from
`OnStopping` (`:393`). The builder compiled that on the box and states the runtime release is NOT
exercised. Measured what the un-released case costs, in-process:

`A_lease_not_disposed_refuses_the_next_instance_in_the_same_process_until_it_is` — a second
`CoidWitness` over the same path is refused with "another writer owns this witness" while the first
lives, and **acquires the moment `Dispose()` is called**, after which both claims are on the committed
file (`["TA-FIRST", "TA-SECOND"]`). So the handover works; what is unproven is only that ATAS calls
`OnStopping`.

**Ruled: MED (R2), and the severity is about the consequence, not the code.** If `OnStopping` does not
fire — ATAS tearing a strategy down another way, or a second strategy started on a second chart
(trap 24/35's misconfiguration) — the first lease is held for the life of the ATAS PROCESS, and every
subsequent order is refused with "another writer owns this witness" until ATAS itself is restarted.
That is fail-closed and the row names the file, so it is not HIGH; but it converts a recoverable
mis-click into "restart ATAS", and the one code path that prevents it has never been run. Per-call
locking could not produce this state; the lifetime lease can, which is why it arrives with the lease
rather than before it.

---

## Target 4 — THE CLASS FIX: "degraded = unresolved SAFETY lines"

Both directions hold, and my attempt to break the third case FAILED — recorded because a refutation
that misses is evidence too.

- **A quarantine warning no longer degrades.** Verified through the builder's
  `A_quarantined_leftover_is_noted_without_claiming_a_durability_gap` and independently in my rotation
  probe's own output: a directory whose sidecar holds only `ignored …` lines reads
  `token=…,io:noted` with `Trouble` null — flagged, not degraded.
- **A lost claim still degrades.** 300 real refused rewrites → `Assert.NotNull(new CoidWitness(File_).Trouble)`
  passes mid-probe, before anything resolves it.
- **RESOLVED survives the quota** — the builder's `A_clean_commit_says_the_gap_is_closed_even_after_the_warning_quota_is_spent`,
  which is my round-4 F1, with mutant MC2 recorded RED 1/81.

**My refutation attempt: can sidecar ROTATION lose an unresolved gap?** `_degraded` is decided from
`LastDecidingLine()` (`CoidWitness.cs:1264`), which reads only the CURRENT log; `AppendToErrorLog`
rotates that file to `.1` and writes the new line into a fresh one. So a WARNING that trips the size
bound moves every safety line out of the file that decides. I built it: 300 refused rewrites past the
64 KB bound, then a session that quarantines a leftover.

First result looked like a hit — `rotated=True token=…io:noted`, `Trouble` null. **It is not a defect.**
Printing the files showed why:

```
CURRENT LOG:
  … ignored /…/coid-witness.json.tmp-dead-1: …
  … ignored /…/… — moved to coid-witness.json.rejected-1
  … RESOLVED coid-witness committed cleanly after the failures above.
ROTATED (.1) last 2:
  … ERROR coid-witness rewrite did not land. …
  … ERROR coid-witness rewrite did not land. …
```

The session that rotated the file is by construction a WRITING session, and it wrote a deciding line
into the fresh file either way: RESOLVED when its own commit landed, or a fresh `ERROR ` when it did
not. There is no path in which rotation leaves the current log with no deciding line while a gap is
open. The probe is kept, restated as the invariant that actually holds
(`An_unresolved_gap_survives_the_sidecar_rotating`) — **PASSES**. Refuted, no finding.

---

## Target 2 — F8: A TEMP IS NEVER A NEW CLAIM

**The rewritten test at the old line 1800.** `A_writer_leaves_at_most_one_uncommitted_rewrite` (now
`CoidWitnessTests.cs:2461-2478`) asserts the opposite of what Codex named: after the owning run ends,
`Assert.Equal(["TA-SEED"], next.All()…)` and `for (var i = 0; i < 5; i++) Assert.Null(next.PriorSession($"TA-{i}"))`.
One temp still survives and none of the five refused claims comes back from it. The reversal is real
and is stated in place.

**A refused submission's temp after a restart** — the builder's `A_temp_is_never_adopted_as_a_new_claim`
is present and bites: mutant **MF8d** (the transition rule stops requiring the committed identifiers)
→ RED 1/117, `A_candidate_that_swapped_a_committed_claim_for_another_is_ignored [FAIL]`.

**An `Identified` temp still adds its broker id — and NOTHING ELSE.** This is the half the rule's
wording leaves open: `IllegalTransition` only compares identifier SETS, so a candidate that passes it
is free to carry different values for every other field. My probe
`A_temp_cannot_rewrite_any_field_of_a_committed_claim_except_the_broker_id` drops a legal-transition
temp whose every other field is a lie (`account_id=ACC-FORGED`, `symbol=NQ`, `side=Sell`,
`quantity=999`, `price=1`, `written_at=2001`) and asserts the merge is field-precise:

```
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4 - TradeAgent.IntegrationTests.dll (net10.0)
```

`BrokerOrderId` IS recovered (`BRK-1`); `AccountId`, `Symbol`, `Side`, `Quantity`, `Price`, `SessionId`
and `WrittenAt` are all untouched. `AdoptInMemory` (`CoidWitness.cs:1086-1092`) rewrites exactly two
properties with `_records[i] with { BrokerOrderId, IdentifiedAt }`. **The rule holds beyond its
wording.**

**The F8 residual, and the builder's stated direction — CONFIRMED by running it.**
`The_F8_residual_is_a_claim_without_an_order_and_never_becomes_evidence` injects a `_replace` that
really renames and then throws. Result: `Submitting` returns **false** (so `Place` refuses the order)
while `TA-GHOST` IS on the committed file, and a later session answers `PriorSession("TA-GHOST")` →
null with `PriorSessionIds(10)` empty. **A claim with no order, never cross-session evidence** — the
direction the builder names, measured rather than argued, and the opposite of the direction rule 1
exists to prevent.

---

## Target 3 — THE F4/F13 ANCHORS, AND THE TWO SURVIVED MUTANTS ATTACKED

**F4's anchor, Codex's exact check.** `A_null_element_envelope_is_unreadable_and_its_bytes_are_left_alone`:
`records:[null, <valid acknowledged A>]` → `Unreadable` true, `All()` empty, `PriorSession("TA-A")` null,
`Submitting` false, `Trouble` non-null, and the file byte-identical afterwards. **PASSES.**

**F13's anchor, both halves.** `Corrupt_committed_bytes_are_never_an_anchor` as a Theory over
generation 1 and 999, with the temp naming `FNV1a64(corrupt bytes)` as its predecessor: no adoption at
either generation, `Unreadable` true, bytes intact. **PASSES.**

**MF4b — I could not construct the path either; the builder's claim survives inspection.**
Reproduced as SURVIVED (`_loaded = true` moved back above `Take`): `Passed! 112/112`. Attacked it
three ways. `_loaded` is read in exactly one place, its own guard (`CoidWitness.cs:838`), so the
mutant's only observable difference is an exception *inside* `Take`. `Take` (`:944`) iterates
`envelope.Records` and calls `_records.Add`; `Parse` (`:921`) now guarantees a non-null list, no null
elements, no empty identifiers and no duplicates, and catches every exception rather than
`JsonException` alone. Nothing between `Take` and `_loaded = true`. **Unreachable rather than
untested — recorded as the builder recorded it, with no new finding.**

**MV9 — proved closed, not merely unobserved.** Reproduced as SURVIVED (`Trouble`'s `EnsureRecovered()`
removed by line): `Passed! 112/112`. `EnsureRecovered` → `AdoptInMemory` touches only `_records` and
`_adopted`; `Trouble` (`:735-750`) reads `_committedUnreadable`, `_notOwned`, `LastWriteFailure` and
`_degraded`, none of which recovery can change since the class fix decided `_degraded` at LOAD from the
sidecar. `Describe()`'s order (`Guard(SweepWitness)` → `PriorSessionIds` → `EnsureRecovered`, then
`Trouble`) means recovery has already run in the one production caller either way. **No observable
effect anywhere, including through `Describe()`. Refuted; the call is uniform ceremony and no finding.**

---

## Target 5 — F9, RUN AND RULED

**The events half is fixed and bites** — the builder's
`A_bridge_speaking_the_previous_protocol_raises_no_events_into_the_application` is present, and my
cherry-picked wire-level probes still pass (v2 refused with `Incompatible.ReportedProtocolVersion == 2`,
v3 accepted, `witness_failure` → DEGRADED naming the file).

**The disconnect, refuted with a code reason — and the reason checks out, but only for this branch.**
`AtasConnector.cs:152`: `if (!await Dispatch(line)) break; // a peer we have refused gets no second frame`.
The INCOMPATIBLE branch returns `true`, so that peer is kept; the UNPROVED branch returns `false`, so
that one is dropped. The asymmetry is deliberate and defensible: an incompatible peer's identity is the
repairable message on screen ("reinstall the add-on"), an unproved peer is an impostor.

**The adjacent "unproved hello" peer the builder flagged for the manager — REFUTED, no finding.**
`An_unproved_peer_raises_no_events_into_the_application`: a raw pipe client that never answers the
challenge, sending hello + two events in ONE write burst so they are on the wire before this end can
react. Result: **`seen == 0`**, `Bridge` null, `SupportsClientOrderId` false, `ReconciliationProvable`
false. An unproved peer has `_authenticated == false` by construction (the refusal branch IS
`if (!_authenticated)`, `AtasConnector.cs:342`), so the event branch's existing `_authenticated` test
already excludes it — and the connection is dropped on top. The flagged item can be closed as refuted
rather than carried forward.

**But running that check found the hole next door — finding R1 (HIGH).** The connector's own comment at
`AtasConnector.cs:366-370` says *"THE HELLO REFUSAL IS WORTH NOTHING WITHOUT THIS ONE"* — a heartbeat
carries a whole `BridgeHello` and the branch below assigns it to `_hello`. F9 added `_incompatible is null`
to the EVENT branch (`:296`) and **not** to the HEARTBEAT branch (`:371`), which still guards on
`_authenticated` alone. Measured over a real pipe with a real authenticated peer that says protocol 2
in its hello and protocol 3 in its heartbeat:

```
a refused v2 peer set capabilities through a heartbeat:
  Bridge=SET proto=3  Incompatible=reported=2
  SupportsClientOrderId=True  SupportsOrderHistory=True  ReconciliationProvable=True
  IsConnected=False
  StatusDetail="bridge 0.1.1 speaks protocol 2, this build speaks 3 — reinstall the add-on from TradeAgent"
```

The connector says FAILED and "reinstall the add-on" **and** reports `ReconciliationProvable = True` at
the same moment. Consumers, enumerated: `TradingGateway.cs:213` refuses LIVE_AUTONOMOUS with
`AUTONOMY_REQUIRES_PROVABLE_STATE` on exactly this flag — that refusal is removed; `TradingGateway.cs:818`
routes an UNKNOWN order to "needs a human to look" on exactly this flag — that escalation is removed.
Stable across 3/3 isolated runs and 3/3 co-runs.

**Diagnostic mutant MR-HB (a diagnosis, not a fix — restored immediately):** adding
`|| _incompatible is not null` to the heartbeat guard makes the probe pass while every other protocol
and round-trip test stays green.

---

## Target 6 — THE THREE-PROCESS HARNESS ON `6a40fa7`

Rebuilt against this worktree's `TradeAgent.AtasBridge`. Three real processes × 80 claims, released by
one gate file, **all three kept alive to the end** so no lease is released by process exit:

```
  A: pid=25002 submitted=80 refused=0  token=session:83605ae2,records:80,prior:0,io:ok
  B: pid=25003 submitted=0  refused=80 token=session:b4611676,records:32,prior:32,io:failed
  C: pid=25004 submitted=0  refused=80 token=session:56619917,records:32,prior:32,io:failed

  generation=160  records on file=80  acknowledged=80
  claimed TRUE=80  claimed FALSE=160
  LOST=[]  PHANTOM=[]
  writers on file=['A']

  who refused whom:
      156  LOCK — another writer owns this witness
```

**80 durable / 0 lost / 0 phantom / no merge, one owner, and the refusal is now the LOCK: 156 lock
lines and ZERO compare-and-swap lines.** At `e22eec6` the same harness gave 158 CAS / 2 lock. **V6 is
confirmed: the lifetime lease is the mechanism and the CAS no longer fires under live contention.**

**A trap I fell into, recorded because the next reader will too.** With the processes allowed to EXIT
when finished, 10/10 runs showed TWO writers each reporting 6/6 durable and both sets of records on the
file — which reads exactly like the merge round 4 forbade. It is not. The first owner finished, exited,
the OS released its lease, and the next process legitimately became the owner and carried the
predecessor's committed records forward. Holding every process alive settles it: 5/5 runs, **exactly
one writer**, 0 lost, 0 phantom. So a CAS refusal on this sha means an ownership HANDOVER happened
mid-run, not "a writer that is not this build" — which is a narrower claim than the round-5 record's
(**R5, LOW**).

**And the harness found a real one — R3 (MED).** 160 refusals produced only 156 safety lines on disk.
Reproduced: 2, 2 and 6 lines dropped out of 160 over three further runs.

```
  run 1: refusals=160  safety lines on disk=158  DROPPED=2
  run 2: refusals=160  safety lines on disk=158  DROPPED=2
  run 3: refusals=160  safety lines on disk=154  DROPPED=6
```

All 156/158/154 lines are well-formed and timestamped; none is blank or truncated. The lines are lost,
not mangled. `AppendToErrorLog` ends in `File.AppendAllText` with no cross-process coordination, and the
processes writing these lines are precisely the ones the lease REFUSED — so they are unserialised by
construction. The class's contract is that a safety event is never dropped; the quota path now honours
it and the concurrency path does not.

---

## Mutants

Production files restored from `cp` copies every time, `touch`ed, never `git checkout --`; the
production tree confirmed byte-identical after each (`git diff --stat 6a40fa7 -- src tools` empty).
Pristine shas: `CoidWitness.cs 390cce67…`, `AtasConnector.cs 4c401299…`, `Versioning.cs 9f31e56f…`,
`probe/Program.cs 9fb62c13…`.

| # | Mutant | Result |
|---|---|---|
| **MV2** (mine, round 4) | `Lease()` `FileShare.None` → `FileShare.ReadWrite` | **RED 6/112** — `A_claim_and_an_acknowledgement_are_both_refused_without_the_lock`, `Two_writers_do_not_share_a_temp_name`, `A_second_live_writer_is_refused_even_when_it_never_overlaps_a_call`, `The_lease_is_what_stops_a_claim_reported_durable_from_being_dropped`, plus both of my new lease probes. It left 80/80 green at `e22eec6`: **round-4 F2 closed** |
| MF8d (builder's) | the transition rule stops requiring the committed identifiers | RED 1/117 |
| MF4b (builder's, declared unreachable) | `_loaded = true` moved back above `Take` | **SURVIVED 112/112** — attacked and confirmed unreachable (see target 3) |
| MV9 (builder's, declared unobservable) | `Trouble` stops calling `EnsureRecovered()` | **SURVIVED 112/112** — attacked and confirmed to have no observable effect (see target 3) |
| MR-HB (mine, *diagnostic only*) | the heartbeat branch also guards on `_incompatible` | the R1 probe passes; every other protocol/round-trip test stays green — confirms R1's fix expectation |

---

## Findings

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **R1** | **HIGH** | A peer whose hello was refused as protocol-2 can set `_hello` — and with it `SupportsClientOrderId`, `SupportsOrderHistory` and `ReconciliationProvable` — by sending ONE heartbeat whose payload claims protocol 3. The connector simultaneously displays "bridge 0.1.1 speaks protocol 2 — reinstall the add-on" and reports `ReconciliationProvable = True`. That flag is what `TradingGateway.cs:213` consults to refuse LIVE_AUTONOMOUS (`AUTONOMY_REQUIRES_PROVABLE_STATE`) and what `TradingGateway.cs:818` consults to escalate an UNKNOWN order to "needs a human to look" — both are removed. F9 closed this for the EVENT branch and left the heartbeat branch behind; the connector's own comment at `:366-370` names this exact route. Measured over a real pipe, 3/3 isolated and 3/3 co-runs. | `src/TradeAgent.Connectors.Atas/AtasConnector.cs:371` | Guard the heartbeat branch on the refusal state as the event branch now is — `if (!_authenticated \|\| _incompatible is not null) return true;`. Better, and the CLASS (§9.10): F9 treated one branch as the instance; the class is **"a peer this connector has refused is still allowed to speak"** — decide it once at the top of `Dispatch` so a future frame type cannot reopen it. Probe ready at `tests/TradeAgent.IntegrationTests/ProtocolThreeVerifyR4Probes.cs` (`A_refused_bridge_cannot_set_capabilities_through_a_heartbeat`, commit `45507cf`). |
| **R2** | MED | The lifetime lease is released only from `StopBridge` via `OnStopping`, and that path has never been run. If ATAS tears a strategy down another way, or a second strategy is started on a second chart (trap 24/35), the first lease is held for the life of the ATAS PROCESS and every later order is refused "another writer owns this witness" until ATAS itself is restarted. Measured in-process: a second instance is refused while the first lives and acquires the moment `Dispose()` is called. Fail-closed, so not HIGH — but per-call locking could not produce this state and the lifetime lease can. | `src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs:212, 393, 468` | Exercise the ATAS stop/start cycle on the box and record it, or release the lease from a second, more certain hook (the StateChanged fan already calls `StopBridge`) and pin "a witness whose owner was torn down is re-leasable" with a test. Until then it belongs under NOT verified by name, not inside a closed finding. |
| **R3** | MED | The sidecar drops safety events under concurrent appends. `AppendToErrorLog` ends in `File.AppendAllText` with no cross-process coordination, and the processes writing these lines are exactly the ones the lease REFUSED — unserialised by construction. Reproduced across four runs: 4, 2, 2 and 6 lines lost out of 160 refusals. Lines are lost whole, not mangled. The class's stated contract is that a safety event is never dropped; the quota path honours it and the concurrency path does not. | `src/TradeAgent.AtasBridge/CoidWitness.cs` (`AppendToErrorLog`, the `File.AppendAllText` call) | Serialise the append — open with `FileMode.Append` + `FileShare.Read` and retry on a sharing violation, the same shape the replace already uses — so a concurrent appender waits instead of overwriting. Pin it with a two-writer probe counting lines against refusals. |
| **R4** | LOW | The lease is an advisory `flock` on the open file, not a claim on the name: unlinking `coid-witness.json.lock` while the owner lives lets the next writer create a fresh inode and take its own lease. Measured with real processes — two live owners. What it costs was measured too and is nothing: the CAS and the read-back still refuse, so no claim reported durable is dropped (probe passes). It degrades the lease to exactly the CAS-only protection round 4 shipped. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1517` | Either accept it explicitly in the doc block (the lease is a same-platform courtesy backed by the CAS), or hold the lease on a handle whose name cannot be recycled. **NOT verified by me:** that Windows forbids the unlink of a file open without `FILE_SHARE_DELETE` — that is the API-contract reasoning that keeps this LOW. |
| **R5** | LOW | The round-5 record says the CAS "now fires only for a writer that is not this build (an older bridge, a hand edit, a restored backup)". Measured: it also fires for the rivals of a legitimate successor after an ownership HANDOVER — an owner that exits mid-run releases its lease, the next process becomes the owner, and a third process holding stale lineage takes CAS refusals. Under fully live contention the count is 156 lock / 0 CAS; with handovers it is the reverse. | `docs/hardening/records/U14.md`, the "V6 (LOW)" section | Add the handover case to the sentence, so a reader who sees CAS lines does not conclude a foreign writer. |

**1 HIGH / 2 MED / 2 LOW.**

**Refuted, no finding** (recorded because a refutation that misses is evidence): the sidecar-rotation
hypothesis for target 4; MF4b's reachability; MV9's observability; and the builder's own flagged
"unproved hello peer", which is refused harder than an incompatible one and raises nothing.

---

## NOT verified, by name

- **Windows, everything.** The box is not mine this round. The builder's 417-green Windows run, the
  on-box compile of the four adapter hunks (`5 Warning(s) 0 Error(s)`), and the Windows `.tmp`
  assertion are claims I read. I re-ran nothing on the box.
- **F5's behavioural half.** That `ClosePosition` refuses at runtime when the witness is unavailable
  needs ATAS driving a real position. The adapter is `<Compile Remove>`d off Windows, so no test in
  the 417 can reach the branch. Unchanged from the builder's own statement.
- **R2's premise** — that `OnStopping` fires on every ATAS strategy teardown. Not exercised by anyone.
- **R4's Windows half** — that a file open without `FILE_SHARE_DELETE` cannot be unlinked there.
- **`_witness.Dispose()` in the real ATAS stop/start cycle.** No ATAS strategy was started or stopped.
- **R1 by an accidental peer.** The DEPLOYED v2 DLL heartbeats with its own hello carrying
  `BridgeProtocolVersion = 2`, which `BridgeCompatible(2)` rejects — so the real old DLL does not trip
  R1 by accident. R1 needs a peer inconsistent between its hello and its heartbeat, i.e. something
  holding the pipe secret, which the code documents as same-user readable and "not a boundary against
  anything running as you". I did not measure whether any shipped build produces that inconsistency.

## What I did NOT do

- **I fixed nothing.** `git diff 6a40fa7 -- src tools` is empty and the four production shas are
  unchanged. Every mutant was restored from a `cp` copy and `touch`ed, never `git checkout --`.
- **I did not push, merge or rebase.** Three probe commits on `u14-verify-r5-probes` (`2e28dae`,
  `45507cf`, plus the robustness commit), all under `tests/`.
- **I did not re-verify round 5's untouched surfaces** — the Gateway, `AppHost.cs`, the updater and the
  App UI, which the builder's sweep shows the diff does not touch.
- **I did not exercise `Quarantine`'s 64-slot exhaustion**, which the builder lists as still finite and
  never reclaimed.
- **I did not measure R3 against DISTINCT safety lines.** The lines lost in my runs were duplicate
  refusals; that the same race can drop a unique "rewrite did not land" line is reasoned from the same
  code path, not separately measured.
- **I did not chase MF11's hang** (the builder's 500-attempt mutant) or re-run the retry-budget timings.
- **Full suite run twice, no more.** Run 1 (baseline at `6a40fa7`) and run 2 (after every mutant was
  restored, my probe classes filtered out) — both 417 green.

```
$ dotnet test TradeAgent.sln --filter "FullyQualifiedName!~VerifyR4Probes&FullyQualifiedName!~VerifyR5Probes"   # run 2 of 2
EXIT=0
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 231, Skipped: 0, Total: 231 - TradeAgent.IntegrationTests.dll (net10.0)
```

The tree is left exactly as it was found.

VERDICT: FAIL — 1H/2M/2L
