# U14 — ADVERSARIAL-VERIFY RECORD · round 8, leg [2], Opus — **FRESH verifier** (rounds 4–7's session is gone)

**Sha under test:** `10fa21f` (= `4de7c25` + 10 commits). Worktree
`…-worktrees/u14-verify-r8`, branch `u14-verify-r8-probes` (cut from the detached `10fa21f`).
Toolchain `PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS.
Cherry-picked from `u14-verify-r7-probes`: `2c00070` (`PeerRefusalVerifyR7Probes`) and `c39e7fa`
(`SidecarBoundaryVerifyR7Probes`, `UnreadableVerifyR6Probes` — the four F17 variants, the lease
handover, the flagged-zero probe).

The round 4–7 verifier's four records are my BASELINE, not my verdict: I re-ran its harnesses rather
than reading its results.

---

## Target 0 — the headline figures, reproduced

```
$ dotnet build TradeAgent.sln --no-incremental
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.41
```
17 project outputs; `grep -c warning` over the full log → `0`. **0 warnings non-incremental: verified.**

```
$ dotnet test TradeAgent.sln          # full-suite run 1 of 2, before any probe was compiled in
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 678 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 267, Skipped: 0, Total: 267, Duration: 1 m 59 s - TradeAgent.IntegrationTests.dll
EXIT=0
```
**454 green (75 / 112 / 267) — the builder's Mac figure reproduced exactly.**

---

## Target 3 — THE ROTATION CRASH WINDOW: the fix protects the state its tests build, and not the ordinary one · **FINDING R8-1 (HIGH)**

Both round-8 rotation tests seed the unresolved `ERROR` into **`.1`** — one generation BACK — and then
rotate (`CoidWitnessTests.cs:2966` and `:3024`). In that arrangement `.1` is untouched until the last
two statements of `Rotate`, so the gap is readable throughout the window by construction.

The ordinary state is the other one: **the unresolved `ERROR` is in the CURRENT log**, which is the log
being rotated. `Rotate` (`CoidWitness.cs:1497`) now moves it to `<log>.rotating` — a name **no reader
scans**: `SidecarGenerations()` (`:1543`) yields the log and `log + ".1"` and nothing else — and the
gap is invisible from that instant until the restatement lands. The pre-round-8 rotation
(`File.Delete(rolled); File.Move(log, rolled)`) had no such window: the current log kept its content
until the single `Move`. **The round-8 fix introduced this window.**

Reachability of the state needs no contrivance: safety events are unrationed
(`AppendToErrorLog`, `:2107`), the cap is 64 KiB (`MaxErrorLogBytes`, `:235`), and nothing moves an
`ERROR` out of the current log except a rotation.

### Refutation 1 — in-process, at the builder's own seam, control beside probe

`tests/TradeAgent.IntegrationTests/RotationWindowVerifyR8Probes.cs` (my probe, commit on
`u14-verify-r8-probes`). Same seam, same reading, one difference: where the `ERROR` sits.

```
$ dotnet test tests/TradeAgent.IntegrationTests/… --filter "FullyQualifiedName~RotationWindowVerifyR8Probes"
  CONTROL_a_gap_one_generation_back_is_readable_at_the_window                     PASS
  A_gap_in_the_current_log_is_gone_at_the_instant_the_restatement_has_not_landed  [FAIL]
      Assert.NotNull() Failure: Value is null            ← Trouble at the window
  A_restatement_that_does_not_land_leaves_the_only_copy_of_the_gap_unscanned      [FAIL]
      files=[coid-witness.errors.log, coid-witness.errors.log.rotating]
      scanned generations contain ERROR: False
      rotating file exists: True
      rotating contains ERROR: True
      Trouble now = <null>
Failed!  - Failed: 2, Passed: 1, Skipped: 0, Total: 3, Duration: 45 ms
```

(The third test is written as an always-`Assert.Fail` reporter — its body is the measurement, not a
pass/fail claim.)

### Refutation 2 — a REAL process kill, which is what the brief asked for

Out-of-process harness (`scratchpad/rotkill`, a `ProjectReference` console driving the real
`CoidWitness`): build the state, then `Process.GetCurrentProcess().Kill()` **inside** the restatement
write — the rotation has happened, the restatement has not landed, no later deciding line is ever
written. `scratchpad/rotread` is a second process that reports what a restart sees.

```
############ CASE A — the gap ONE GENERATION BACK (the builder's arrangement)
about to rotate; the machine dies inside the restatement write
  killed, exit=137                                   ← SIGKILL, no unwind, no flush
  files      : coid-witness.errors.log.1, coid-witness.errors.log.rotating
  Trouble    : an earlier run could not write the write-ahead record; the account of it is in …
  Token      : session:8bba58d5,records:1,prior:0,io:degraded
  Noted      : True   GapClosed : False   Standing : Unresolved

############ CASE B — the gap in the CURRENT log (the ordinary state)
about to rotate; the machine dies inside the restatement write
  killed, exit=137
  files      : coid-witness.errors.log.rotating
  Trouble    : <null>
  Token      : session:8337a580,records:1,prior:0,io:noted
  Noted      : True   GapClosed : False   Standing : Noted
```

**CASE B is the harm F27 was raised to fix, still present after the F27 fix.** `Trouble` is null, so
`AtasStrategyAdapter.cs:645` (`SupportsClientOrderId = proof.ProvesRoundTrip() && _witness.Trouble is
null`) stays **true** and the gateway goes on trading fully automatically over a lost write-ahead
record. In CASE A the same line goes false. The only thing left standing in CASE B is `io:noted`, and
that is an accident rather than the guard: `SidecarSet()` (`:1562`) globs `ErrorLogName + "*"`, which
happens to match `.rotating`; the deciding-line scan does not use `SidecarSet()`.

And the last copy is scheduled for deletion: the next rotation opens with
`if (File.Exists(staging)) File.Delete(staging)` (`:1482`) under the comment "Its content is already
restated in the current log by step 3, so it is a duplicate rather than evidence" — which is false in
exactly this case, because step 3 is the step that did not happen.

**Fix expectation:** `SidecarGenerations()` must yield the staging name too (`log + ".rotating"`), so
the window has no observation point that hides the gap — or `Rotate` must restate BEFORE it moves the
current log aside. A test in the CASE-B arrangement (gap in the current log) must be added beside the
two that seed `.1`; today no test builds the state at all.

---

## Target 10 (dispatch item 10) — the AdapterTeardown extraction: the CLASS is not closed · **FINDING R8-2 (HIGH)**

The round-8 record names the class as "nothing made 'this strategy is down' and 'this strategy no
longer owns the witness' the same fact", and enumerates **three** consumers of `AdapterTeardown`
(`Started()` in `StartBridge`, `Stop(...)` in `StopBridge`, `Record(...)` in `OnOrderPayload`).

`grep -n "_witness" src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs` finds **four** write sites:

| line | call | through `Record`? |
|---|---|---|
| `:2055` | `_teardown.Record(() => _witness.Identified(o.Comment, o.Id))` — the order-event fan | **yes** |
| `:1409` | `_witness.Submitting(...)` — `Place`'s write-ahead record | **no** |
| `:1562` | `_witness.Identified(cmd.ClientOrderId, order.Id)` — `Place`'s identification | **no** |
| `:1824` | `_witness.Submitting(...)` — `ClosePosition`'s write-ahead record | **no** |

The three unguarded ones run on the **BridgeServer frame loop**, and that loop can outlive the
teardown by construction — this is not inferred, it is what the two files say about themselves:

* `BridgeServer.DisposeAsync` waits **5 s** for the loop and then gives up:
  `catch (Exception) { /* cancelled, faulted, or would not let go: either way we are done */ }`
  (`BridgeServer.cs:450-451`).
* `StopBridge` wraps that wait in `StopTimeout` (`CallTimeout + AckTimeout + 2 s`,
  `AtasStrategyAdapter.cs:151`) and **catches the timeout** (`:499-502`), and its own doc says the
  abandoned loop "still holds its pipe client until whatever wedged it returns" (`:487-488`).
* `AdapterTeardown.Stop` then releases the lease in its `finally`.

So a `Place` still in flight on the abandoned loop reaches `:1562` **after** the release, and
`CoidWitness.Identified` leases whenever there is something of this session's to write (`:691-692`,
the "look before leasing" narrowing — and a record this same `Place` wrote at `:1409` is exactly that).

### Refutation — executed, against a real `CoidWitness` and a real lease

`tests/TradeAgent.IntegrationTests/TeardownReachVerifyR8Probes.cs`:

```
$ dotnet test … --filter "FullyQualifiedName~TeardownReachVerifyR8Probes"
  CONTROL_the_guarded_write_site_cannot_take_the_lease_back                              PASS
  A_restart_racing_a_teardown_has_its_witness_released_by_the_old_teardown               PASS
  An_unguarded_write_site_takes_the_lease_back_after_the_teardown_released_it            [FAIL]
     a strategy ATAS has already stopped is refusing the witness to the live one:
     another writer owns this witness (…/coid-witness.json.lock): IOException
  An_unguarded_write_ahead_record_also_takes_the_lease_back_after_the_teardown           [FAIL]
     a strategy ATAS has already stopped is refusing the witness to the live one:
     another writer owns this witness (…/coid-witness.json.lock): IOException
Failed!  - Failed: 2, Passed: 2, Skipped: 0, Total: 4, Duration: 127 ms
```

This is **PRIOR 21's own harm, unchanged**, through a door the fix does not cover: a strategy ATAS has
already taken down leases the witness again and holds it for the life of the ATAS process, refusing
every order the live bridge then tries to record. `AdapterTeardownTests` cannot see it because the
extracted class is only ever driven through `Record`.

**Fix expectation:** every witness WRITE in the adapter goes through `_teardown.Record` — `:1409`,
`:1562` and `:1824` as well as `:2055` — with `Place`/`ClosePosition` treating a `false` return as the
same refusal a failed `Submitting` already is (`AtasRejectedException`, "nothing was submitted"). A
test in `AdapterTeardownTests` must drive `Record` returning false for a write-ahead record, not only
for an identification.

**Also examined, and NOT a finding:** `Started()` sets `_stopped = false` outside the lock
(`AdapterTeardown.cs:28`), so a restart that races a teardown still inside its steps has its lease
released by the old teardown's `finally`
(`A_restart_racing_a_teardown_has_its_witness_released_by_the_old_teardown`, PASS = the rival acquires).
The restarted session's next write re-leases (`Lease()` runs per write, `CoidWitness.cs:1850`), so what
this costs is a window in which a second ATAS process could take the name — the same exposure the
"lease at first write" design already carries. Recorded, not raised.

---

## Target 9 (dispatch item 9) — the F23 idle poll, both directions · **FINDING R8-3 (HIGH)** + two answers

`PeerHasGoneQuiet()` (`AtasConnector.cs:189`) reads `_lastHeartbeat`, written in exactly two places —
`:557` (an accepted hello) and `:574` (a heartbeat frame) — and by nothing else. But it is CONSULTED
only when the idle poll WINS the race against the pending read (`:276-281`). Those are two different
questions, and the gap between them is the hole.

`tests/TradeAgent.IntegrationTests/PipeLivenessVerifyR8Probes.cs`:

```
$ dotnet test … --filter "FullyQualifiedName~PipeLivenessVerifyR8Probes"
  CONTROL_a_silent_peer_is_dropped_and_a_second_bridge_gets_in                    PASS
  A_quiet_bridge_that_only_beats_is_not_dropped_at_shipped_values                 PASS
  The_idle_poll_neither_loses_nor_duplicates_a_frame_across_its_wakeups           PASS
  A_peer_that_dribbles_any_frame_keeps_the_only_pipe_instance                     [FAIL]
      a dribbling peer with a frozen heartbeat held the single pipe instance:
      health=DEGRADED, connected=False, bridge=<none>, detail=<none>
Failed!  - Failed: 1, Passed: 3, Skipped: 0, Total: 4, Duration: 1 m 12 s
```

**The finding.** The dribbler authenticates, says a compatible hello, then writes one meaningless
`{"op":"ping"}` line every **200 ms** against a **333 ms** poll and a **1 s** heartbeat timeout, and
never sends a heartbeat. Five heartbeat windows later the connector's own health says **DEGRADED** — it
knows perfectly well the peer is stale — and the peer is still holding the only server instance there
is: the replacement bridge's single `ConnectAsync` **failed** and `connector.Bridge` is null. That is
exactly the harm F23 was raised to fix ("a peer nobody can hear must not be able to hold the trading
path shut"), reachable by any process running as this user that writes a newline now and then. My
CONTROL, the same peer saying nothing, is dropped and the replacement gets in — so the drop works only
against total silence.

**Fix expectation:** decide staleness on the same schedule regardless of who wins the race — check
`PeerHasGoneQuiet()` after a frame is dispatched as well as when the poll wakes empty (a frame that is
not a hello or a heartbeat is not evidence of liveness under this connector's own definition), or give
the loop a deadline task that is not restarted per iteration. A test with a peer that emits a
non-heartbeat frame faster than `IdlePoll` must go RED first.

**Target 9's two questions, answered by measurement rather than by reasoning:**

1. **Can it drop a legitimately quiet but healthy bridge? No, and what keeps it alive is the heartbeat
   alone.** `AtasConnector.HeartbeatTimeout` ships at **15 s** (`:204`); `BridgeServer.HeartbeatInterval`
   ships at **5 s** (`BridgeServer.cs:40`) — a 3x margin, and three beats must be missed. Measured at
   those shipped values: a bridge with **no** orders, quotes or events for **45 s** (three whole
   windows), beating only, stays `READY` with `Bridge.BridgeVersion` intact. Order silence is
   irrelevant to the drop; only the beat matters.
2. **Does the poll race lose or duplicate a frame? Not in the ordinary path.** Twelve order events at
   800 ms against a 666 ms poll — an empty poll inside every gap — arrive **12 of 12, all distinct**.
   The carried `pending` task is correct: the read is never restarted and never cancelled.
   **NOT verified:** the narrow interleaving where the read completes between `Task.WhenAny` returning
   the delay and `PeerHasGoneQuiet()` being evaluated (`:276-278`), which would discard a completed
   line. I could not make it reproducible, so I make no claim about it either way.

---

## Target 1 — the absence predicate, and three variants the builder's test does not build

The builder's `A_missing_bridge_directory_is_unreadable_rather_than_absent` (`CoidWitnessTests.cs:1091`)
RENAMES the directory (`Directory.Move`). Three more shapes, all in
`tests/TradeAgent.IntegrationTests/AbsenceAndRowVerifyR8Probes.cs`, all **PASS**:

| probe | what it drives | result |
|---|---|---|
| `A_deleted_bridge_directory_is_unreadable_and_nothing_is_written` | real `Directory.Delete(recursive)` under a LIVE witness holding its lease | `Submitting` false, `Trouble` says "could not be read", never "changed underneath", nothing recreated |
| `A_machine_with_no_bridge_directory_refuses_every_order` | a witness whose directory never existed — the ratified fail-closed case from a cold start | `Submitting` false, `Trouble` non-null, and **it does not create its own directory** (`grep -n CreateDirectory CoidWitness.cs` → no match) |
| `An_unreadable_bridge_directory_is_the_same_predicate_as_an_unreadable_file` | `chmod 000` on the FOLDER, a different syscall path from a chmod on the file | refused, diagnosed "could not be read" — one predicate, no split |

**The predicate is genuinely one.** `ReadTolerantly` (`CoidWitness.cs:1661`) is the only committed-file
read; `FileNotFoundException` alone is absence (`:1674`), `DirectoryNotFoundException` is `failed = true`
(`:1684`), and every other exception falls to `failed = true` after three retries (`:1686`). The
sidecar readers (`HasNotes` `:1514`, `LastLineWhere` `:1578`, `SidecarSet` `:1562`) answer "nothing"
for a missing directory rather than "unreadable" — but that is unobservable, because `Trouble` returns
`UnreadableDetail()` from the committed read first (`:850`), which the three probes above confirm.

**All four round-6 F17 variants (real `chmod 000`, a directory at the path, a short read, a mid-read
failure) plus the lease/dispose handover and the flagged-zero probe still hold at this sha** — the
carried `UnreadableVerifyR6Probes` / `SidecarBoundaryVerifyR7Probes` / `PeerRefusalVerifyR7Probes`:

```
$ dotnet test … --filter "FullyQualifiedName~VerifyR6Probes|FullyQualifiedName~VerifyR7Probes"
Passed!  - Failed: 0, Passed: 10, Skipped: 0, Total: 10, Duration: 935 ms
```

That also closes the round-7 verifier's **V4** and **V5** from my side: its own `A_live_refusal_is_not_masked_by_a_stale_one` and
`A_refused_writers_safety_line_flags_the_zero_without_degrading_the_machine` were RED at `4de7c25` and are green here.

---

## Target 2 — the lease on every terminal path, with the race

`A_stop_that_lands_mid_write_never_leaves_the_lease_held` (mine): **40 rounds**, two real threads, the
stop landing mid-write with `UntrackSecurities` throwing (`steps: () => throw new
InvalidOperationException(...)`), a fresh directory and a real lease each round. A replacement witness
acquires **every** round. PASS.

Combined with the builder's `AdapterTeardownTests` (6/6 green here), the lease does not survive a
terminal path **for a write that goes through `Record`**. For the three that do not, see finding R8-2.

---

## Target 4 — the unreadable sidecar

`A_sidecar_that_cannot_be_read_is_not_a_sidecar_with_nothing_in_it` (`CoidWitnessTests.cs:2932`) holds
the canonical sidecar `FileShare.None` and asserts `Noted`, `Trouble` "could not be read",
`io:degraded`, `ZeroIsProvisional` true, `GapClosed` false — green at this sha, and RED under my
independent re-run of MF28a (below). The scoping is as the F25 boundary says and I read it in code:
`_noted` over `SidecarSet()` (`:1000`, every per-writer file), `_sidecarUnreadable` over
`SidecarGenerations()` (`:1005`, canonical only).

**Examined, not raised:** `EnsureLoaded` is `if (_loaded) return` (`:980`), so `_sidecarUnreadable`
is computed once per instance — a scanner that lets go a second later leaves the instance degraded for
its whole life. That is the fail-closed direction and the witness lives for the ATAS process, so it is
an accepted cost rather than a defect; recorded so it is not rediscovered as one.

---

## Target 5 — the other F23 peers

`A_partial_frame_peer_is_dropped_so_the_pipe_can_be_taken_again` (mine): a peer that authenticates and
then writes `{"v":3,"op":"hel` with **no newline, ever**. `ReadLineAsync` never completes, the poll
wins every time, the peer is dropped, and a second bridge dialled ONCE gets in. PASS.
`CONTROL_a_silent_peer_is_dropped_and_a_second_bridge_gets_in` (the stale-open peer): PASS.
The **dribbling** peer is finding R8-3 above.

---

## Target 6 — V4's precedence and V5's state

| probe | result |
|---|---|
| `PeerRefusalVerifyR7Probes.A_live_refusal_is_not_masked_by_a_stale_one` (v2 refusal → reinstalled bridge fails AUTH → the row says the auth sentence) | PASS — the round-7 finding is closed |
| `The_reverse_order_also_puts_the_newer_refusal_on_the_row` (mine: auth refusal FIRST, then a protocol-2 peer) | PASS — `StatusDetail` says "speaks protocol 2", `Incompatible.ReportedProtocolVersion == 2` |
| `A_live_good_bridge_clears_both_refusals` (mine) | PASS — `Incompatible`, `Unauthenticated` and `StatusDetail` all null |
| `SidecarBoundaryVerifyR7Probes.A_refused_writers_safety_line_flags_the_zero_without_degrading_the_machine` (V5's state) | PASS, and MV2c-shaped edits are pinned by it |

**One correction to my own first reading, recorded because it was a candidate finding and is not one.**
I expected the older AUTH marker to stay recorded after a newer protocol refusal. It does not, and that
is right: the protocol-2 peer in that sequence **proved it holds the secret**, so `NoteUnauthenticated(null)`
at `:641` REPAIRED the auth refusal — the precedence rule is not what cleared it. My probe now asserts
the correct thing.

**Examined, not raised (LOW, no finding):** `_incompatibleAt` / `_unauthenticatedAt` are plain `long`
fields (`:86-87`) written next to a reference field and read from another thread in `StatusDetail`
without a barrier. A reader can transiently pair a new marker with a stale stamp and render the wrong
one of two sentences until the stores are visible. Display only, self-correcting, and I could not make
it reproduce.

---

## Target 7 — the manager's five refutations, each read and one of them re-measured

| # | verdict |
|---|---|
| **PRIOR 5 PARTIAL** | **Correct, and now moot.** The round-6 `tools/atas-gate` transcript is in `records/U14.md` on main (the `GATE PASSED` block). More to the point, I re-ran the gate at THIS sha — see the box section below — so the evidence no longer depends on which copy of the record anyone read. |
| **PRIOR R4** | **Correct, and re-measured rather than read.** `RefutationSpotCheckR8.R4_an_unlinked_lock_yields_a_second_owner_and_costs_the_first_every_later_claim` → PASS: the unlink DOES yield a second owner (the premise), A's every later claim is **refused** (`Submitting` false, `Trouble` set), and the committed file holds B's claim and none of A's. Fail-closed, and it costs no claim silently. |
| **PRIOR R5** | Record wording, T3; nothing executable to attack. Accepted. |
| **F24** | **Correct.** `_incompatible` is re-derived at the next hello; the only clearer of a refused row besides an accepted hello is constructing a new `AtasConnector` (`AppHost.cs:124`, `:184`, `GatewayHost/Program.cs:29`) — an explicit operator action. Persisting it across process lifetimes would need a store the connector does not own. |
| **F25** | **Correct, and the code says it in one place rather than two.** `_noted` over `SidecarSet()` (`:1000`), `_degraded`/`_gapClosed` over `SidecarGenerations()` (`:1005`, `:1012`). The V5 test pins the boundary in the one state that distinguishes them, and it is green here. |

**None of the five is wrong.**

---

## Target 8 — my standing harnesses still bite

**R3, per-writer sidecars, real OS processes at this sha.** Five writers x 40 claims against one
witness, every process held alive for 25 s so no lease is released by exit and every refusal is
genuine contention (`scratchpad/r3writer`, a `ProjectReference` console driving the real `CoidWitness`):

```
  run1: refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
  run2: refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
  run3: refusals=160 files=4 lines=160 naming a claim=160 DROPPED=0
    coid-witness.errors.log-57262-43d19e4d … -57265-006c1338   (four losers, one file each)
```

**MD1** (the round-6 verifier's mutant — `Drop` wipes `_incompatible` again) → **RED 15/40**, up from
the 12 it was recorded at, because round 8 added tests over the same guard. See the mutants table.

---

## Mutants

Every production file restored from a `cp` copy taken before the first mutant and `touch`ed —
**never `git checkout --`**. Pristine SHA-256, taken before and confirmed after the last mutant:

```
222f09b63d8b198e2533866c5aebb8f49a5b70c1b629969df0059e512b150a10  CoidWitness.cs
d277680bd5836c40433b39ad6c93670d5439399aeee8abcdc699eef53080a3bb  AdapterTeardown.cs
cf3900c6ea75117e6d06f56433954258ac4e8f6365da513a659f3a899459bb3e  AtasConnector.cs
$ git diff --stat 10fa21f -- src tools packaging      # after every mutant, and at the end
                                                       (empty)
```

| # | Mutant | Result |
|---|---|---|
| **MP17** | `DirectoryNotFoundException` classified as absence again | **RED 2/139** — the builder's `A_missing_bridge_directory_is_unreadable_rather_than_absent` **and** my `A_deleted_bridge_directory_is_unreadable_and_nothing_is_written` |
| **MF28a** | an unreadable sidecar counts as empty again (`HasNotes` catch → `false`) | **RED 1/133** |
| **MF27b** | `File.Delete(rolled)` moved above the restatement | **RED 4/131** — the builder's `The_restatement_lands_before…` and my `CONTROL_a_gap_one_generation_back_is_readable_at_the_window`. The `_writeSidecar` seam is genuinely load-bearing; the round-8 residual is correctly closed |
| **MP21'** | the builder's shape — check AND write both leave the lock (the pre-fix code) | **RED 4/10**, including `A_write_that_began_before_the_stop_cannot_take_the_lease_back_after_it` |
| **MP21-half** (mine) | only the CHECK leaves the lock; the write stays inside it | **the builder's tests all SURVIVE it.** Only my `A_stop_that_lands_mid_write_never_leaves_the_lease_held` (40 rounds) goes RED → **finding R8-4** |
| **MP21b** | `_stopped = true` moved down beside the release | **RED 4/10**, including `A_write_that_arrives_while_the_teardown_is_running_does_not_run` — the builder's own residual kill holds |
| **MF26** | the release goes back to a plain statement after the steps | **RED 4/10**, including `An_exception_in_teardown_does_not_keep_the_witness` |
| **MV4a** | `StatusDetail` returns the incompatible marker whenever it is set (the old precedence) | **RED 3/40** — both copies of `A_live_refusal_is_not_masked_by_a_stale_one` and my reverse-order probe |
| **MD1** (round 6's) | `Drop` wipes `_incompatible` again | **RED 15/40** |
| **MF23a** | never drop (`PeerHasGoneQuiet` → false) | **RED 4/7** — silent peer, partial-frame peer, my CONTROL |
| **MF23b** | drop at every idle poll (`PeerHasGoneQuiet` → true) | **RED 4/7** — the builder's beating test, my quiet-at-shipped-values test, my no-loss test |
| **MR8-8** (mine, *diagnostic only*) | `SidecarGenerations()` also yields `<log>.rotating` | the fix shape for **R8-1**: my window probe passes, the real-kill CASE B reads `io:degraded` / `Standing: Unresolved` / `Trouble` non-null, and **135/136** stay green (the one failure is my always-`Assert.Fail` reporter). One line, no existing test disturbed |
| **MR8-9** (mine, *diagnostic only*) | `if (PeerHasGoneQuiet()) break;` added after a dispatched frame | the fix shape for **R8-3**: my dribbler probe passes and all 4 liveness probes go green; the wider 55-test pipe batch stayed at 54 with only a parallel-pipe-contention flake, refuted by two clean re-runs (class-level 4/4, single-test 1/1) |

Both diagnostic mutants were restored immediately; neither is a proposed patch, they are evidence that
the fix expectations in the findings table are one line each and disturb nothing.

---

## THE BOX — the one run, for the two items the builder could not compile

**Identity check FIRST, before anything ran.** `tools/win-push.sh` → `packed 720K`, `unpacked: 160 files`.
Then SHA-256 on the box against my worktree:

```
LOCAL (worktree)                                                        BOX (C:\ta\repo)
0a2256d4eb051071d95e33d638310e8bc1ae47f5bf46dd22466891b63896003c        0a2256…003c  AtasStrategyAdapter.cs
d277680bd5836c40433b39ad6c93670d5439399aeee8abcdc699eef53080a3bb        d27768…a3bb  AdapterTeardown.cs
222f09b63d8b198e2533866c5aebb8f49a5b70c1b629969df0059e512b150a10        222f09…2a10  CoidWitness.cs
cf3900c6ea75117e6d06f56433954258ac4e8f6365da513a659f3a899459bb3e        cf3900…bb3e  AtasConnector.cs
c60ee31c76e9ee50d8a791d32919b1cfb7040feb248c9a3ea3c437fa6b37a0c1        c60ee3…a0c1  tools/atas-gate/Program.cs
cs-count(src+tests): 89                                                 cs-count(src+tests): 89
_teardown markers in the adapter: 5      ← the round-6 grep marker is now `_teardown`, as stated
AdapterTeardown.cs mtime: 09/04/2026 09:34:18
```

**All five hashes and the file count match. The tree on the box is mine.** My worktree's production
files are byte-identical to `10fa21f` (`git diff --stat 10fa21f -- src tools packaging` empty), so
this is `10fa21f`'s bridge that was compiled and gated.

### 1. `AtasStrategyAdapter.cs` COMPILED against the real ATAS assemblies — the builder's load-bearing gap, closed

```
$ dotnet build src\TradeAgent.AtasBridge\TradeAgent.AtasBridge.csproj -c Release --no-incremental \
      -p:AtasBridgeBuild=true -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"
  TradeAgent.AtasBridge -> C:\ta\repo\src\TradeAgent.AtasBridge\bin\Release\net10.0-windows\TradeAgent.AtasBridge.dll
Build succeeded.
    5 Warning(s)
    0 Error(s)
BRIDGE BUILD EXIT: 0
```

**The adapter's three call sites into `AdapterTeardown` — `Started()` in `StartBridge` (`:447`),
`Stop(...)` in `StopBridge` (`:492`), `Record(...)` in `OnOrderPayload` (`:2055`) — BIND.** Round 8's
"unproven until the bridge is compiled on the box" is discharged: PRIOR 21 and F26 compile, and the
gate below executes the file.

The 5 warnings are `MSB3277` (a `WindowsBase` 4.0 / 10.0 conflict introduced by ATAS's own
`ATAS.Indicators.dll` / `ATAS.Indicators.Technical.dll`) and four `CS0618` obsolete-API warnings at
`AtasStrategyAdapter.cs:1532/1616/1677/1841` (`OpenOrder`, `ModifyOrder`, `CancelOrder`,
`ClosePosition`). None names `AdapterTeardown` or a round-8 line, and none contradicts the Mac's
`0 Warning(s)` — that build has the adapter `<Compile Remove>`d. **NOT verified: that these 5 predate
round 8**, which would need a second on-box build at `4de7c25` and I had one run.

### 2. `tools/atas-gate` — the money path, both directions, re-run at this sha

```
$ cd C:\ta\repo\tools\atas-gate && dotnet run -c Release -p:AtasBridgeBuild=true \
      -p:AtasInstallDir="C:\Program Files (x86)\ATAS Platform"
TRADEAGENT_HOME = C:\Users\Nicolas\AppData\Local\Temp\ta-atas-gate-1f3e35c5
bridge dir      = C:\Users\Nicolas\AppData\Local\Temp\ta-atas-gate-1f3e35c5\bridge
  [PASS] ITradingManager.ClosePosition was never called — calls = 0
  [PASS] the refusal says nothing was submitted — the write-ahead record for TA-CLOSE-REFUSED could not
         be written to …\coid-witness.json; nothing was submitted. ERROR claim=TA-CLOSE-REFUSED another
         writer owns this witness (…\coid-witness.json.lock): IOException
  [PASS] the refusal names the witness file
  [PASS] ITradingManager.ClosePosition WAS called once the witness could be written — calls = 1
  [PASS] the refused close left no write-ahead record — records = [TA-CLOSE-ALLOWED]
  [PASS] the permitted close left one — records = [TA-CLOSE-ALLOWED]
GATE PASSED
ATAS-GATE EXIT: 0
```

**Re-hash after the run — identical, and the count unchanged:**

```
0a2256…003c AtasStrategyAdapter.cs · d27768…a3bb AdapterTeardown.cs · 222f09…2a10 CoidWitness.cs
cf3900…bb3e AtasConnector.cs · c60ee3…a0c1 Program.cs · cs-count(src+tests): 89
```

`tools/win-state.sh` afterwards: ATAS `installed True`, `running True`, 14 strategy files — unchanged
from before the run. The installed app, ATAS and the real home were not touched; nothing but
`C:\ta\repo` and a `%TEMP%\ta-atas-gate-*` directory the gate makes for itself was written.

---

## Findings

**CLASS (§9.10) — three of the four share one root cause: each round-8 guard was proved in exactly the
state its own author built, and each has a neighbouring state one step away that the fix does not
reach.** R8-1: a gap in `.1` (built) versus a gap in the current log (never built). R8-2: the fan's
write site (guarded) versus the other three witness writers (never enumerated). R8-3: a silent peer
(built) versus a peer that writes any line at all (never built). The structural fix is the same one in
each case: enumerate the SET of states or callers that reach the guard and pin the boundary, instead
of pinning the single instance that motivated the fix.

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **R8-1** | **HIGH** | The F27 rotation fix moves the current log to `<log>.rotating` before restating, and **no reader scans that name** (`SidecarGenerations` yields the log and `.1` only). So an unresolved `ERROR` living in the CURRENT log — the ordinary state, and the one neither round-8 rotation test builds; both seed `.1` — is invisible from the `File.Move` until the restatement lands, and is **permanently lost** if the restatement does not land (the exact failure the `_writeSidecar` seam's own doc exists for: a full disk, a read-only directory, a scanner). Measured with a real `SIGKILL` inside the window: the restart reads `Trouble = <null>`, `io:noted`, `Standing: Noted`, versus `io:degraded` / `Unresolved` for the builder's arrangement — so `SupportsClientOrderId` (`AtasStrategyAdapter.cs:645`) stays **true** and the gateway trades fully automatically over a lost write-ahead record. The pre-round-8 rotation had no such window; this one introduced it. The last copy is then deleted by the next rotation (`:1482`) under a comment asserting it is a duplicate, which in this case it is not. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1497` (the move), `:1543` (`SidecarGenerations`), `:1482` (the staging delete) | `SidecarGenerations()` yields `log + ".rotating"` between the log and `.1`, so the window has no state that hides the gap — or `Rotate` restates before it moves the current log aside. Verified as a one-liner by diagnostic mutant MR8-8: my window probe passes, the real-kill CASE B reads `io:degraded`, **135/136** green. A test in the CASE-B arrangement must be added: `RotationWindowVerifyR8Probes.cs` is ready on `u14-verify-r8-probes` (`a86e3a5`). |
| **R8-2** | **HIGH** | `AdapterTeardown.Record` guards **one of four** witness write sites. `Place`'s write-ahead record, `Place`'s identification and `ClosePosition`'s write-ahead record all reach `_witness` with no flag and no lock, and all three run on the **BridgeServer frame loop, which outlives the teardown by construction**: `DisposeAsync` waits 5 s and gives up (`BridgeServer.cs:450`), `StopBridge` catches its own `StopTimeout` (`AtasStrategyAdapter.cs:499-502`) and its doc says the abandoned loop "still holds its pipe client until whatever wedged it returns". Measured: after `Stop` released the lease, an unguarded write re-leases and a replacement adapter is refused — *"another writer owns this witness … IOException"*. This is **PRIOR 21's own harm, unchanged**, on a path the fix does not cover, and `AdapterTeardownTests` cannot see it because the extracted class is only ever driven through `Record`. | `src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs:1409`, `:1562`, `:1824` (guarded: `:2055` only) | Route all four writes through `_teardown.Record`; `Place`/`ClosePosition` treat a `false` return as the refusal a failed `Submitting` already is (`AtasRejectedException`, "nothing was submitted"). `AdapterTeardownTests` gains a case driving `Record` false for a WRITE-AHEAD record, not only for an identification. Probe ready: `TeardownReachVerifyR8Probes.cs` (`a86e3a5`). |
| **R8-3** | **HIGH** | The F23 drop is decided by `PeerHasGoneQuiet()` but **consulted only when the idle poll wins the race against the pending read**. A peer that completes any line more often than `IdlePoll` therefore never lets the poll win and is never asked. Measured: a peer that handshakes and then writes one `{"op":"ping"}` every 200 ms against a 333 ms poll and a 1 s timeout, sending no heartbeat, still holds the only server instance five windows later — the connector's own health says **DEGRADED** while the replacement bridge's single `ConnectAsync` **fails** and `Bridge` is null. My control, the same peer silent, is dropped and the replacement gets in. This is F23's stated harm ("a peer nobody can hear must not be able to hold the trading path shut") reachable by any process running as this user that writes a newline now and then. | `src/TradeAgent.Connectors.Atas/AtasConnector.cs:276-281` (the race decides whether the guard runs), `:189` (`PeerHasGoneQuiet`) | Ask staleness on the same schedule whichever side of the race wins — `if (PeerHasGoneQuiet()) break;` after a dispatched frame as well as on an empty poll (a frame that is neither a hello nor a heartbeat is not evidence of liveness under this connector's own definition, `:557`/`:574`), or give the loop a deadline task that is not restarted per iteration. Verified as a one-liner by diagnostic mutant MR8-9. A RED-first test with a peer emitting a non-heartbeat frame faster than `IdlePoll`: `PipeLivenessVerifyR8Probes.cs` (`a86e3a5`). |
| **R8-4** | MED | PRIOR 21's stated rule is "the check and the write are ONE act, under the lock the release takes" — but no test distinguishes that from "the write is under the lock". **MP21-half** (only the CHECK leaves the lock) survives every `AdapterTeardownTests` case, including `A_write_that_began_before_the_stop_cannot_take_the_lease_back_after_it` and `A_write_that_arrives_while_the_teardown_is_running_does_not_run`, and is a real weakening: the fan reads the flag, the stop completes and releases, and the fan then takes the lock and re-leases. Only my 40-round two-thread probe catches it. A guard on a T1 surface with no biting test for the shape its own doc names. | `src/TradeAgent.AtasBridge/AdapterTeardown.cs:44-52` | Keep `A_stop_that_lands_mid_write_never_leaves_the_lease_held` (`AbsenceAndRowVerifyR8Probes.cs`, `a86e3a5`) as a permanent test, or add a deterministic equivalent that enters `Record` past the check and lets the whole `Stop` complete before the write. |

**3 HIGH / 1 MED / 0 LOW.**

**Closed from the round-7 verifier's record, on my own re-runs:** its **V4** (a stale protocol refusal
masking a live authentication one) — its own probe is green here and MV4a is RED 3/40; its **V5**
(MV2c unpinned) — its own probe is green here and pins the state.

**Examined and NOT raised** (each written above where it was found): the `Started()`-racing-a-teardown
window; `EnsureLoaded`'s once-per-instance sidecar state; the unsynchronised `_incompatibleAt` /
`_unauthenticatedAt` stamps; and the auth marker being cleared by a peer that proves itself, which is
correct rather than a precedence failure.

---

## NOT verified, by name

- **Which callback ATAS fires on a strategy teardown** (`OnStopping` vs `OnDispose`). The adapter now
  COMPILES on the box and `tools/atas-gate` executes it, but no strategy was loaded on a chart in a
  running ATAS, so this remains two hooks and a compiler. Unchanged since round 6.
- **Windows beyond my one run.** I built the bridge and ran `tools/atas-gate`, and nothing else: **no
  on-box test suite, no UI, no `probe atas`, no second build**. Every other Windows claim stands where
  round 6 left it.
- **Whether the 5 warnings from the on-box bridge build predate round 8.** That needs a second on-box
  build at `4de7c25` and the grant was one run. None of the five names `AdapterTeardown` or a round-8
  line.
- **The narrow interleaving at `AtasConnector.cs:276-278`** where a read completes between
  `Task.WhenAny` returning the delay and `PeerHasGoneQuiet()` being evaluated, which would discard a
  completed line. I could not make it reproducible and make no claim about it either way.
- **The dashboard rendering of the new `BridgeRow` strings.** I measured `StatusDetail`,
  `Incompatible` and `Unauthenticated` as values; no screen was drawn. Same gap the round-7 verifier
  declared.
- **R8-1 and R8-3 on Windows.** Both are measured on macOS/APFS and on .NET's named pipes on macOS.
  The rotation mechanism is `File.Move`/`File.WriteAllText`, and the pipe mechanism is
  `maxNumberOfServerInstances = 1`; the same in kind on Windows is reasoning, not measurement.
- **The F8 residual** and **`Quarantine`'s 64 slots** — both still open, neither touched this round.

## What I did NOT do

- **I fixed nothing.** `git diff --stat 10fa21f -- src tools packaging` is empty and the three
  production SHA-256s match the copies taken before the first mutant. Every mutant was restored from a
  `cp` copy and `touch`ed, never `git checkout --`. Both diagnostic mutants (MR8-8, MR8-9) were
  restored immediately.
- **I did not push to any git remote, merge or rebase.** Four commits on `u14-verify-r8-probes`, all
  under `tests/`: `2c00070` and `c39e7fa` cherry-picked from `u14-verify-r7-probes`, `a86e3a5` and
  `977d562` mine.
- **What I did to the box's tree, plainly.** `tools/win-push.sh` deletes `C:\ta\repo\src`, `tests`,
  `packaging` and `tools` and unpacks my worktree over them, which is what the grant instructed. The
  box's `C:\ta\repo` now holds `10fa21f` plus my four probe test files. I did not touch the installed
  app, ATAS, `%LOCALAPPDATA%\TradeAgent` or the UI agent; ATAS was running before and after.
- **I did not run the full suite on the box** (not asked for, and not needed for these two items).
- **I did not re-run the builder's MV2b, MF28b, MF29a/b, MV4b/c or MP21b-as-recorded** beyond what is
  in the mutants table — those are the builder's evidence and I read them; MP21b I did re-run.
- **I did not exercise `SwitchConnectorAsync` end to end**; I read its call sites.
- **I did not build the `Place`/`ClosePosition` half of R8-2 through the real adapter.** It cannot be
  driven off a chart, so the finding rests on the extracted class plus what `BridgeServer.cs:450` and
  `AtasStrategyAdapter.cs:487-502` say about their own lifetimes. The re-lease itself is measured.
- **Full suite run twice, no more.** Run 1 (baseline, before any probe was compiled in) and run 2
  (after every mutant was restored, my probe classes filtered out):

```
$ dotnet test TradeAgent.sln --filter "…!~VerifyR6Probes&…!~VerifyR7Probes&…!~VerifyR8Probes&…!~RefutationSpotCheckR8"
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 585 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 267, Skipped: 0, Total: 267, Duration: 2 m 4 s - TradeAgent.IntegrationTests.dll
EXIT=0
```

**454 both times.** The tree is left exactly as it was found.

VERDICT: FAIL — 3H/1M/0L
