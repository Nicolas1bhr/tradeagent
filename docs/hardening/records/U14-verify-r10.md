# U14 — ADVERSARIAL-VERIFY RECORD · round 10 (the STRUCTURAL round), leg [2], Opus — **FRESH verifier**

**Sha under test:** `01fcd60` (= `e113c4c` + 6 commits). Worktree
`…-worktrees/u14-verify-r10`, branch `u14-verify-r10-probes` (cut from the detached `01fcd60`).
Toolchain `PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS (Apple silicon).
The rounds 4–9 verifiers' records and their probe branches are my BASELINE, not my verdict.

---

## Target 0 — the headline figures, reproduced (leg [2]'s own run, not the builder's)

```
$ dotnet build TradeAgent.sln --no-incremental
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:02.38
EXIT=0
```
**0 warnings on `--no-incremental`: verified.**

```
$ dotnet test TradeAgent.sln          # full-suite run 1 of 2, before any probe was compiled in
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 988 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 330, Skipped: 0, Total: 330, Duration: 2 m 4 s - TradeAgent.IntegrationTests.dll
EXIT=0
```
**517 green (75 / 112 / 330) — the builder's Mac figure reproduced exactly, from 477 at `e113c4c`.**

The six production SHA-256s I read match the six the builder printed in its on-box identity check,
digit for digit — so the bytes compiled and gated on the box are the bytes I am attacking:

```
64961398888bd5fcd3139310d2590a6493bd07d35411b8f593f567c16c43bfb0  CoidWitness.cs
296340125f9a024b4e9d6e6028928eec1c96b9edb663d9e8620436ba2b796b63  AdapterTeardown.cs
dc3eb64e6924acfea14e69a20b3625f06636b44938e7f1775dd1e533796db92c  CoidWitnessReport.cs
aad3f048d76c503bc6ff820d773a59864239741b91b4b90eba6fb148072cb1d1  AtasConnector.cs
5543935473b0d24af723b63d43d460d98f6b061e59f6ef92a0fb76a12384a7d1  AtasStrategyAdapter.cs
c60ee31c76e9ee50d8a791d32919b1cfb7040feb248c9a3ea3c437fa6b37a0c1  tools/atas-gate/Program.cs
```

```
$ git diff e113c4c -- tests/ | grep -E '^-.*public (async )?(Task|void) '
-    public async Task The_stopped_flag_that_decides_is_the_one_read_under_the_lock()
-    public void A_sidecar_read_that_fails_is_unreadable_unless_it_says_the_file_is_not_there(
-    public void A_sidecar_enumeration_that_fails_flags_the_zero_without_degrading_the_machine(
$ git diff e113c4c -- tests/ | grep -cE '^\+.*public (async )?(Task|void) '
32
```
**Three removals, 32 additions — the builder's test-name diff reproduced; no fourth removal.**

---

## Target 1 — ONE SITE · the one reader holds inside `CoidWitness.cs` · **but not across the unit** · **FINDING R10-2 (MED)**

### What I could not refute

`_readSidecar` and `_listSidecars` are private readonly fields and `grep -n` finds **three** call
sites, all inside the one function or its helper:

```
$ grep -n "_readSidecar\|_listSidecars" src/TradeAgent.AtasBridge/CoidWitness.cs
336:    readonly Func<string, string[]> _readSidecar;
337:    readonly Func<string, string, string[]> _listSidecars;
614:        _readSidecar = readSidecar ?? File.ReadAllLines;
618:        _listSidecars = listSidecars ?? Directory.GetFileSystemEntries;
1714:                foreach (var (path, _, _) in before) lines[path] = _readSidecar(path);
1719:                    var candidates = _listSidecars(dir, …GetFileName(_path) + ".tmp*");
1741:        var names = _listSidecars(dir, ErrorLogName + "*");
```

`SidecarSnapshot` is a **private nested sealed class** and `ReadSidecarSet()` is private, so a
consumer cannot ask for one. The API shape refuses it, and the compiler says so — I tried:

```
$ // added to tests/TradeAgent.IntegrationTests/ReachProbe.cs:  var snap = w.ReadSidecarSet();
$ dotnet build tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj
ReachProbe.cs(10,22): error CS1061: 'CoidWitness' does not contain a definition for 'ReadSidecarSet'
and no accessible extension method 'ReadSidecarSet' accepting a first argument of type 'CoidWitness'
could be found
```
(the file was deleted again immediately; it is not on the probe branch)

**Every exception at every step → `Unreadable` at every consumer: verified, including two steps the
builder's own theory does not drive.** `tests/TradeAgent.IntegrationTests/SnapshotSeamsVerifyR10Probes.cs`
(mine, `42dc5b3` on `u14-verify-r10-probes`):

| step | probe | result |
|---|---|---|
| the STAT (`new FileInfo(name)` inside `Listing`) | `A_name_this_build_cannot_stat_is_unreadable_rather_than_absent` — the listing returns a name with a NUL | PASS: `Trouble` non-null, `io:degraded`, zero provisional |
| a name that VANISHED between the listing and the read | `A_name_that_vanished_between_the_listing_and_the_read_is_unreadable` | PASS |
| the CANDIDATE glob (the second listing call, after the stability check) | `A_denied_candidate_glob_is_unreadable_rather_than_no_stranded_rewrite` | PASS — R9-5 is closed |
| the read seam, 5 exception types | shipped `A_sidecar_read_that_fails_is_unreadable_whatever_the_failure_was` | 5/5 |
| the listing seam, 4 exception types | shipped `A_sidecar_enumeration_that_fails_degrades_rather_than_reading_as_an_empty_directory` | 4/4 |
| **the other direction** | `CONTROL_a_directory_that_enumerated_cleanly_and_held_nothing_is_clean` — `Trouble` null, `Noted` false, `io:ok`, `Clean`, zero NOT provisional | PASS |

### What I did refute — the class is closed inside the class, not across the unit

The round-10 directive is *"Every consumer — `HasNotes`, `LastDecidingLine`, `SidecarPaths`,
`Trouble`/degraded, `Notes`, `CoidWitnessReport`, **the probe, the support package**, and ROTATION —
reads from a snapshot and never touches the filesystem itself."* The builder's enumeration
(`grep -nE "File\.|Directory\." CoidWitness.cs`) is scoped to ONE FILE and is complete for that file.
Grepped across the unit, two consumers still read the sidecar path themselves:

```
$ grep -nE "File\.|Directory\.|FileStream|FileInfo" src/TradeAgent.Diagnostics/Doctor.cs tools/probe/Program.cs | grep -i "errors.log\|SidecarPaths\|ReadTail"
Doctor.cs:291:   foreach (var f in Directory.GetFiles(Paths.BridgeDir, "*.errors.log*"))
Doctor.cs:292:       File.Copy(f, Path.Combine(staging, "bridge-" + Path.GetFileName(f)), true);
probe/Program.cs:1075:  foreach (var file in witness.SidecarPaths)
probe/Program.cs:1077:      foreach (var note in ReadTail(file, 10)) …      // ReadTail = File.ReadAllLines, :2664
```

`Doctor.cs:291-293` sits under `catch (IOException) { } catch (UnauthorizedAccessException) { }`, and
the `foreach` is INSIDE the try — so one unreadable generation drops itself **and every file after it
in the enumeration order**, with nothing in the zip saying so.
`tests/TradeAgent.IntegrationTests/SupportPackageVerifyR10Probes.cs` (mine):

```
$ dotnet test … --filter "FullyQualifiedName~SupportPackageVerifyR10Probes"
  CONTROL_two_readable_sidecars_are_both_collected                                   PASS
  A_denied_sidecar_drops_the_whole_set_from_the_support_package_without_saying_so    [FAIL]
      a sidecar this run could not read is missing from the support package and nothing in it says so.
      sidecars=[bridge-coid-witness.errors.log]
      all=[activity.txt, bridge-coid-witness.errors.log, engineering.log, environment.json]
      readable=coid-witness.errors.log  denied=coid-witness.errors.log-9999-deadbeef
  A_directory_at_a_sidecars_name_is_invisible_to_the_support_package                 [FAIL]
      GetFiles=[]   GetFileSystemEntries=[coid-witness.errors.log.2]
Failed!  - Failed: 2, Passed: 1, Skipped: 0, Total: 3
```

The second failure is the same call's other half: the collector globs with `Directory.GetFiles`,
which is exactly the call round 10 replaced in `CoidWitness`'s own seam default with
`GetFileSystemEntries` because it does not return a DIRECTORY at a sidecar's name.

`tools/probe/Program.cs:1075-1078` re-reads every sidecar off the disk with `File.ReadAllLines`
although the snapshot already holds the lines — F33's shape (a second read of the same file), one
consumer over. It does NOT lie: `ReadTail` prints `(could not be read: <Type>)`. That half is
correct; it is the enumeration's completeness that is not.

**Not refuted, and said as such: no consumer can get a WRONG STATE this way.** `Trouble`, `Notes`,
`Standing`, `Token()` and `SupportsClientOrderId` all come from the snapshot. What the two sites cost
is EVIDENCE — the artefact an operator sends to support — which is the same currency R9-1 was about.

---

## Target 2 — concurrent change · **NOT refuted in process or out of it** · one construction found and rated

### Out of process, real rotations under real readers

`scratchpad/rotkill10v` (a console with a `ProjectReference` to this worktree's real
`TradeAgent.AtasBridge`; on the probe branch). One writer padding the log past its cap and rotating
with a fresh witness each turn — every rewrite failing, so the gap is never legitimately resolved —
against **three separate reader processes** constructing a fresh `CoidWitness` in a tight loop:

```
$ rotkill10v seed $D ; rotkill10v churn $D 10 &  ; rotkill10v read-loop $D 8 ×3
reads=18608 degraded=18608 otherTrouble=0 CLEAN=0
reads=18763 degraded=18763 otherTrouble=0 CLEAN=0
reads=18556 degraded=18556 otherTrouble=0 CLEAN=0
rotations driven = 43
```

**55 927 readings, 43 real rotations, ZERO clean readings and zero non-degraded ones.** No reader
ever reported a clean zero, and none ever fell through to `Unreadable("changing")` either — the one
retry always resolved.

### The construction the brief asks for

`A_same_length_rewrite_that_restores_the_mtime_is_invisible_to_the_stability_check` (mine) **PASSES**,
which means the construction exists: two lines of exactly equal length that say opposite things (an
`ERROR` and the `RESOLVED` marker, padded to match), swapped between the two listings with
`File.SetLastWriteTimeUtc` restoring the stamp. The snapshot is ACCEPTED — `Trouble` is non-null and
does not say "could not be read" — while a second reader taken afterwards answers `Trouble` null.

How much that is worth is measured rather than argued:

```
$ dotnet test … --filter "…MEASURE_the_mtime_resolution_this_filesystem_reports"    PASS
   (asserts 200 distinct stamps over 200 same-length rewrites — the stamp moves on EVERY write)
```

So on this filesystem the only way through the check is to restore the mtime deliberately. Nothing in
this product does that; a backup agent, an `rsync --times` or a restore does. The direction it fails
in is CLOSED (the reader reported the unresolved state while the disk had been resolved), and anyone
who can rewrite a sidecar and forge its mtime can rewrite the witness itself. **Rated LOW-adjacent
and recorded, not raised as a finding.**

---

## Target 3 — rotation crash points · the four renames hold · **the write BEFORE them does not** · **FINDING R10-1 (HIGH)**

### What holds

| claim | how I checked it | result |
|---|---|---|
| no staging file is created | `A_real_rotation_creates_no_staging_file_and_its_temp_is_inside_the_readers_glob` (mine) — the file set is captured from inside the carry write of a REAL rotation | PASS: `.new` present, `.rotating` absent, no `.rotating*` on disk afterwards |
| `.rotating` is read, never written | `grep -n StagingSuffix CoidWitness.cs` → `257` (the const) and `1864` (`Generations`) and nothing else | verified |
| no `File.Delete` in `Rotate` | `sed -n '1623,1660p' … \| grep -E "File\.\|Delete"` → three `File.Move`, no delete | verified |
| a rotation that cannot read refuses and the append still lands | `A_rotation_that_cannot_read_refuses_and_the_append_still_lands` (mine) — both halves: nothing renamed AND the log grew | PASS |
| the four crash-point rows | shipped `A_gap_is_readable_at_every_instant_of_the_rotation` (5), `…_in_the_oldest_generation…` (5), `A_closed_gap_stays_closed…` (5), all green in my own run | 15/15 |
| a real `SIGKILL` at a random instant | my own 40 rounds, real processes, 30–930 ms, fresh reader after each | `SIGKILL rounds: invariant held 40, violated 0` |

### What I refuted — there is a FIFTH crash point, and it is inside act 1

The builder's list is *"`Rotate` is `write · rename · rename · rename`, and rows 1–4 are the four gaps
after them. There is no fifth."* There is: **inside the write.** Production's carry write is
`WriteDurably(path, text, FileMode.Create)` (`CoidWitness.cs:645, :654`), and `FileMode.Create`
EMPTIES an existing `log.new` at the open, before a byte of the replacement is written. Measured, not
read:

```
$ dotnet test … --filter "…PREMISE_FileMode_Create_empties_the_file_at_the_open"    PASS
   (new FileStream(p, FileMode.Create, …) → new FileInfo(p).Length == 0 before any Write)
```

`log.new` holding the only copy of the unresolved line is the state the builder's own crash-point
**row 3** leaves behind whenever the deciding line lived in the generation act 2 overwrote. Drive the
next rotation over that state and let the carry write fail after the open — a disk-full, which is the
canonical cause of the ERROR lines this file exists for:

```
$ dotnet test … --filter "FullyQualifiedName~SnapshotSeamsVerifyR10Probes"
  CONTROL_a_real_rotation_carries_the_only_copy_out_of_the_pending_generation        PASS
  CONTROL_a_carry_write_that_never_opens_the_file_keeps_the_only_copy                PASS
  A_carry_write_that_truncates_and_then_fails_loses_the_only_copy                    [FAIL]
      Trouble=<null>  Token=session:dac29de7,records:2,prior:0,io:noted  Standing=Noted
      files=[coid-witness.errors.log, coid-witness.errors.log.new]   TA-GAP-on-disk=False
  One_failed_write_during_a_rotation_loses_the_marker_and_the_retry_completes_over_it [FAIL]
      attempts=2  Trouble=<null>  Token=session:d866e22f,records:2,prior:0,io:noted  Standing=Noted
      files=[coid-witness.errors.log, coid-witness.errors.log.1]      TA-GAP-on-disk=False
Failed!  - Failed: 2, Passed: 13, Skipped: 0, Total: 15
```

**`Trouble = <null>` / `io:noted` / `Standing: Noted` / the marker gone from every file is R9-1's own
figure, reproduced at round 10 through the rotation's first act.** The second probe is the one that
matters most: `attempts=2` — ONE transient write error, no second crash anywhere. `AppendToErrorLog`
retries, the retry takes a FRESH snapshot in which `log.new` is now the empty file attempt 1 left,
recomputes the carry as "nothing to carry", and completes the rotation normally over the hole. The
file set afterwards (`[log, log.1]`) is indistinguishable from a healthy machine's.

Both controls are the same state one condition different, so this is the write and not the fixture.

**Why the shipped suite cannot see it.** `A_restatement_that_never_lands_leaves_the_gap_where_a_reader
_still_finds_it` — one of the three tests round 10 rewrote — models the failed carry write with a seam
that throws WITHOUT OPENING THE FILE, and then asserts the file set at that instant is exactly
`["coid-witness.errors.log"]`. That is true of the seam and false of `FileMode.Create`: production
leaves an empty `coid-witness.errors.log.new` beside it. The assertion the record calls "stronger than
it was" is stronger about renames and unfaithful about the write.

---

## Target 4 — the state machine · **NOT refuted** · but the survivor is refutable · **FINDING R10-3 (MED)**

### The door, measured on my own copy

```
$ grep -c "_witness" src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs        0
$ grep -c "new CoidWitness" src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs 0
$ grep -c "_teardown\." src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs     17
```
The adapter holds no witness and constructs none. `Record` and `Read<T>` are the only two members
that touch `_witness` and both take `_gate` — read off the source.

### The four interleavings, and the race

```
$ dotnet test … --filter "FullyQualifiedName~AdapterTeardownTests"          13/13 green
$ dotnet test … --filter "FullyQualifiedName~TeardownLockVerifyR10Probes"    3/3 green (mine)
  A_start_on_another_thread_during_the_steps_is_refused                      PASS
  The_two_thread_race_never_lets_a_write_land_after_the_release  (40 rounds)  PASS
  The_teardown_steps_do_not_start_while_a_write_is_still_inside_the_guard     PASS
```

### The survivor, refuted as a survivor — MR10-4d

The builder records: *"the lock on the `Running → Stopping` transition is NOT verified to be
load-bearing … the states that would separate the two are ones where a `Record` is already inside the
lock, and such a write has already passed its check and completes under both."* That is true of the
WRITE and not of the ORDER. With the lock, `Stop` cannot enter `Stopping` — and therefore cannot start
the teardown steps, which call into ATAS to unsubscribe the strategy — until the write in flight has
left the guard. Without it, the steps run over a witness write that is still going. A writer parked
inside `Record`, a stopper arriving behind it, and the order the two complete in:

```
# mutant MR10-4d applied: `lock (_gate) _state = State.Stopping;`  →  `_state = State.Stopping;`
$ dotnet test … --filter "…AdapterTeardownTests|…TeardownLockVerifyR10Probes"
  The_teardown_steps_do_not_start_while_a_write_is_still_inside_the_guard  [FAIL]
      Assert.Equal() Failure: Collections differ  ↓ (pos 0)
Failed!  - Failed: 1, Passed: 15, Skipped: 0, Total: 16
```

**`AdapterTeardownTests` 13/13 still green under the mutant — the builder's own measurement,
reproduced — and RED 1/16 against a deterministic test that takes 300 ms.** So the lock IS
load-bearing and it IS pinnable; what the record says is unverifiable is merely untested.

---

## Target 5 — the row · **NOT refuted** · R9-3 confirmed closed

`PeerRowTests` carries three of the round-9 verifier's four probes verbatim plus two of the builder's,
5/5 green in my run. The mutant that R9-3 asked for:

```
# MR10-5b (= the round-9 verifier's MV9-a): UnauthenticatedNow always returns the explicit refusal,
# whatever the stamps say
$ dotnet test … --filter "…PeerRowTests|…BridgeRoundTripTests|…VerticalSliceTests"
  PeerRowTests.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure  [FAIL]
Failed!  - Failed: 1, Passed: 41, Skipped: 0, Total: 42
```
**RED 1/42 in the SHIPPED suite, where at `e113c4c` it survived all three classes. R9-3 is closed.**

`StatusDetail` read line by line for a state with no reading: `PendingHello` requires `_authenticated
&& _hello is null`, `silent` requires `!_authenticated` — mutually exclusive, so the three cover every
live peer; `Drop` clears `_authenticated`, `_authenticatedAt`, `_peerArrived` and `_peerArrivedAt`
(`:490-498`), so a pipe with nobody on it derives nothing and cannot claim a peer is waiting. A peer
that authenticates and THEN sends an incompatible hello is stamped later than its own
`_authenticatedAt`, so the protocol refusal wins — checked by reading, not by a test.

**Examined and NOT raised:** `NoteIncompatible`/`NoteUnauthenticated` write the peer and its stamp as
two statements, so a reader between them can see the new peer with the cleared stamp. Display only,
transient, and the round-8 verifier already recorded the same non-finding about these fields.

---

## Target 7 — every filesystem call on the sidecar path, and `Quarantine`'s survivor

**The builder's table is complete for the grep it ran and its title over-claims.** `grep -nE
"File\.|Directory\." CoidWitness.cs` cannot see a `FileStream` or a `FileInfo` constructor. Re-grepped
with those included, three more calls exist in the file than the table's "13 lines, and this is the
whole of it":

| line | call | on the sidecar path? |
|---|---|---|
| `:635` | `new FileStream(path, FileMode.Open, Read, ReadWrite\|Delete)` — `DefaultOpen` | no: the committed witness file |
| `:654` | `new FileStream(path, mode, Write, Read)` — `WriteDurably` | **yes**, as `_writeSidecar`'s default — and this is R10-1 |
| `:2199` | `new FileStream(lockPath, OpenOrCreate, ReadWrite, None)` — the lease | no: the `.lock` |
| `:1745` | `new FileInfo(name)` — in `Listing` | yes, and it IS inside the one `try` (named in the table) |

**`Quarantine`'s `File.Exists` (`:1951`) is NOT on the sidecar path and cannot lie into a state.** Both
its probes are on `_path` (`<witness>.tmp*` → `<witness>.rejected-N`), neither name matches the sidecar
glob `coid-witness.errors.log*`, and each wrong answer is caught by the caller:

- `File.Exists(target)` false when the name IS taken → `File.Move(candidate, target)` without
  `overwrite` throws → the outer `catch (Exception) { }` → `return null` → the candidate is LEFT where
  it is. Fail-safe.
- `File.GetLastWriteTimeUtc(candidate)` cannot throw into a state either: .NET answers `1601-01-01`
  for a name it cannot stat, which is older than the grace, so the move is attempted and fails the
  same way.
- The one direction that is not fail-safe is a candidate whose mtime is in the FUTURE (clock skew, a
  restored file): `UtcNow - future` is negative, so it is never quarantined and stays in the `.tmp*`
  glob for ever. That costs a permanent `Noted` and no state lie. **Recorded, not raised.**

**What the sweep did find is two consumers outside `CoidWitness.cs`** — `Doctor.cs:291` and
`tools/probe/Program.cs:1075` — written up under target 1 as finding R10-2.

---

## Target 8 — the reversed theory · **the direction is right, and it is now third-party reachable**

The reversal (`A_sidecar_enumeration_that_fails_flags_the_zero_without_degrading_the_machine` →
`…_degrades_rather_than_reading_as_an_empty_directory`, 4 rows, all four now degrading) is the
fail-CLOSED direction and I did not refute it: with no listing there are no canonical generation names
either, so this run cannot say whether a gap is open, and `SupportsClientOrderId` dropping is exactly
what rule 1 asks for over "do not fake it". Both halves checked:

```
$ dotnet test … --filter "FullyQualifiedName~SnapshotSeamsVerifyR10Probes"
  A_refused_writers_content_still_notes_without_degrading_this_machine     PASS
      (a second writer's own ERROR line: Noted true, Trouble null, no io:degraded — F25 intact)
  A_second_writers_unreadable_file_degrades_this_machine                   PASS
      (the same file at chmod 000: Trouble non-null, io:degraded)
```

**A refused writer's CONTENT still only notes: verified.** What is new, and what the record's F25
paragraph does not say, is that the widened boundary is reachable by somebody else: any process that
can write in `Paths.BridgeDir` can leave one unreadable file matching `coid-witness.errors.log*` and
drop `SupportsClientOrderId` on the canonical bridge for as long as it stands. It is fail-closed
(availability, not safety) and in the same trust domain as the witness file itself — `Fingerprint`'s
own doc makes that argument — so it is **finding R10-4 (LOW)**, a sentence the record owes rather than
a change to the direction.

---

## Target 9 — the three renamed tests · **no assertion weakened, one is unfaithful to production**

| test | change | verdict |
|---|---|---|
| `A_sidecar_read_that_fails_is_unreadable_unless_it_says_the_file_is_not_there` → `…_whatever_the_failure_was` | 5 rows, of which 2 asserted CLEAN, now 5 rows all asserting `Trouble` non-null + `io:degraded` + `Noted` + provisional; a sidecar file added to the fixture because the reader now reads only names the listing returned; the absent case moved to a new `A_sidecar_that_is_not_there_is_not_a_sidecar_that_could_not_be_read` with the opposite assertions | **STRENGTHENED**, both directions kept |
| `A_sidecar_enumeration_that_fails_flags_the_zero_without_degrading_the_machine` → `…_degrades_rather_than_reading_as_an_empty_directory` | reversed; 4 rows; `Assert.Null(Trouble)` + `DoesNotContain("io:degraded")` replaced by `NotNull` + `Contains` + `Empty(SidecarPaths)` + provisional | **STRENGTHENED and reversed** (target 8) |
| `A_restatement_that_never_lands_leaves_the_gap_where_a_reader_still_finds_it` | the file-set assertion moved from `["…log.rotating"]` to `["…log"]` | stronger about RENAMES, **unfaithful about the WRITE** — its seam throws without opening the file, so the asserted set is one production does not produce (R10-1) |
| `The_stopped_flag_that_decides_is_the_one_read_under_the_lock` → `The_state_that_decides_…` | the stopper moved from a third thread to a reentrant `Stop` inside the holder's own `Record`; `Assert.True(teardown.Stopped)` moved from before `letGo` to after; `Assert.True(Submit(Session(), "TA-REPLACEMENT"))` added | the interleaving it pins (a writer parked on the lock before the transition, released after it) is the same, and MR10-4b is still RED 1/13. What it no longer exercises is a CROSS-THREAD stop; that half is now covered by my `A_start_on_another_thread_during_the_steps_is_refused` and `The_two_thread_race…` and by nothing in the shipped suite. **Not weakened; narrowed, and the narrowing is not stated.** |

`git diff e113c4c -- tests/ | grep -E '^-.*public (async )?(Task|void) '` is three lines and I
reproduced it in target 0: no fourth test was removed.

---

## Target 6 — regression + gate

```
$ dotnet test TradeAgent.sln --filter "FullyQualifiedName!~VerifyR10Probes"   # full-suite run 2 of 2
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 693 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 330, Skipped: 0, Total: 330, Duration: 2 m 10 s - TradeAgent.IntegrationTests.dll
EXIT=0
```
**517 both times**, after every mutant was restored from its `cp` copy and `touch`ed.

```
$ dotnet build TradeAgent.sln --no-incremental      # with my probe classes compiled in
Build succeeded.
    0 Warning(s)
    0 Error(s)
$ git diff --stat 01fcd60 -- src tools packaging
(empty)
$ shasum -a 256 …    # all five files I copied, identical to the pre-mutant copies
64961398888bd5fcd3139310d2590a6493bd07d35411b8f593f567c16c43bfb0  CoidWitness.cs
296340125f9a024b4e9d6e6028928eec1c96b9edb663d9e8620436ba2b796b63  AdapterTeardown.cs
dc3eb64e6924acfea14e69a20b3625f06636b44938e7f1775dd1e533796db92c  CoidWitnessReport.cs
aad3f048d76c503bc6ff820d773a59864239741b91b4b90eba6fb148072cb1d1  AtasConnector.cs
acd890543d8b8b307659dcf485d59886a0d983240991acd0717f3f9a5345630f  Doctor.cs
```

**Rounds 6–9 tests: still there and still green.** The three classes that hold them —
`CoidWitnessTests`, `AdapterTeardownTests`, `BridgeRoundTripTests` — are inside the 330 above; the
round-6 F17 variants, the `FileShare.None` test, the directory-at-the-name test, the rotation-window
tests, the four teardown terminal-path tests and the R3 five-writer harness all ran in my own runs.
**Two round-9 verifier probes were NOT carried, and the record names only one of them:** the 130 s
quiet-but-beating probe (named), and `A_peer_that_arrives_long_after_the_last_beat_still_completes_
its_handshake` (not named) — see finding R10-5.

---

## Mutants

Every production file was copied with `cp` before the first mutant, restored from that copy and
`touch`ed after each one — **never `git checkout --`** — and the SHA-256s above are the confirmation.

| # | mutant | builder's result | mine |
|---|---|---|---|
| **MR10-1c** | an unreadable snapshot is noted but NOT degraded (`_diskDegraded = false` in `Derive`'s refusal branch) | RED 13/175 | **RED 13/176** — reproduced |
| **MR10-2a** | the before/after listing is never compared (`if (Same(before, after))` → `if (true)`) | RED 1/175 | **RED 1/176** — `A_sidecar_set_that_keeps_changing_under_the_read_is_refused_not_believed`, reproduced |
| **MR10-3a** | `Rotate` does not refuse an unreadable snapshot | **SURVIVED 176/176**, argued redundant | **SURVIVED the shipped suite (189/192, the 3 being mine) and RED against my `A_rotation_entered_from_the_counter_still_refuses_an_unreadable_snapshot`.** The redundancy argument is wrong past the first append — see R10-3 |
| **MR10-4a/4b/4c** | the three teardown mutants | RED 2/13, 1/13, 2/13 | not re-run individually; `AdapterTeardownTests` 13/13 green in my runs and MR10-4b's test is the one target 9 examines |
| **MR10-4d** | the `Running → Stopping` transition leaves the lock | **SURVIVED 13/13**, recorded "not verified load-bearing" | **SURVIVED `AdapterTeardownTests` 13/13 — reproduced — and RED 1/16 against my `The_teardown_steps_do_not_start_while_a_write_is_still_inside_the_guard`** — see R10-3 |
| **MR10-5b (= MV9-a)** | an explicit credential refusal always outranks the derived reading | RED 1/42 in the shipped suite | **RED 1/42** — reproduced; R9-3 closed |
| **MR10-1a/1b/1d, 3b, 3c, 5a** | — | RED | **NOT re-run by me.** They are the builder's evidence; I read them and did not reproduce them |

---

## Findings

**CLASS (§9.10) — R10-1 and R10-2 share one root cause, and it is the class this round claimed to
close, escaping through the two doors the restructure did not cover: the WRITE side of the sidecar
path, and the consumers outside `CoidWitness.cs`.** The round made "I could not read it" a value no
consumer can conflate — inside the class. It left (a) the one call that DESTROYS a sidecar generation
before replacing it, `_writeSidecar(pending, carry)` with `FileMode.Create`, which is the same
"destroy before the replacement is on the disk" shape F27/F30/R9-1 were about, one act earlier than
anyone looked; and (b) two consumers named in the directive by name — the support package and the
probe — still reading the sidecar path themselves. **The structural fix is the round's own rule
applied to its own temp and to its own consumer list:** nothing on this path is destroyed except by an
atomic rename onto it, and anything that wants sidecar content is handed the snapshot.

**CLASS (§9.10) — R10-3 is two instances of one habit: a surviving mutant was reasoned about instead
of probed one state further out.** Both survivors the record declares (MR10-3a "redundant", MR10-4d
"not load-bearing") are load-bearing, and both are caught by a deterministic test that runs in under a
second. The argument that retired each one stopped at the state the builder had already built.

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **R10-1** | **HIGH** | **The rotation destroys the file that holds the marker before it writes the replacement — inside act 1, the instant the crash-point list says does not exist.** `_writeSidecar(pending, carry)` is `WriteDurably(path, text, FileMode.Create)`, and `FileMode.Create` EMPTIES an existing `log.new` at the open (measured: `PREMISE_FileMode_Create_empties_the_file_at_the_open`). When `log.new` holds the only copy of the unresolved line — the state the builder's own crash-point **row 3** leaves whenever the deciding line lived in the generation act 2 overwrote — a write that fails after the open leaves the marker in no file at all: `Trouble=<null>`, `Token=…io:noted`, `Standing=Noted`, `TA-GAP-on-disk=False`, which is R9-1's signature exactly. **`AtasStrategyAdapter.cs:655` then keeps `SupportsClientOrderId` true over a write-ahead record that is gone.** No second crash is required: `One_failed_write_during_a_rotation_loses_the_marker_and_the_retry_completes_over_it` shows `attempts=2` — one transient IO error, the retry takes a fresh snapshot in which `log.new` is now empty, recomputes the carry as nothing, and finishes the rotation over the hole, leaving `[log, log.1]`, a file set indistinguishable from a healthy machine's. Both controls (a real carry write; a seam that never opens the file) carry the line correctly. The shipped `A_restatement_that_never_lands…` cannot see it because its seam throws WITHOUT OPENING, and it then asserts a file set production does not produce. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1643` (`_writeSidecar(pending, carry)`), `:645` + `:654` (`DefaultWriteSidecar` → `WriteDurably(…, FileMode.Create)`), `tests/TradeAgent.IntegrationTests/CoidWitnessTests.cs:3288-3300` (the unfaithful seam and its file-set assertion) | Apply the round's own rule to its own temp: write the carry to a fresh unique name and `File.Move(…, pending, overwrite: true)` onto `log.new`, so no existing generation is emptied before its replacement is on the disk; or open the pending name with `FileMode.CreateNew` under a name nothing can already hold. AND make the shipped seam open-and-truncate before it throws, so the test asserts the state production leaves. Probes ready on `u14-verify-r10-probes` (`5f0485d`): `SnapshotSeamsVerifyR10Probes.cs` — two REDs, three controls and the premise. |
| **R10-2** | MED | **The one-reader rule holds inside `CoidWitness.cs` and not across the unit, at the two consumers the directive names.** `Doctor.cs:291-293` enumerates the sidecar set with its own `Directory.GetFiles(Paths.BridgeDir, "*.errors.log*")` and copies it under `catch (IOException) { } catch (UnauthorizedAccessException) { }`, with the `foreach` INSIDE the try: one generation this run cannot read drops itself **and every file after it in enumeration order**, and nothing in the zip says a file was skipped — measured, `sidecars=[bridge-coid-witness.errors.log]` where two exist, control green with both readable. The same call globs with `GetFiles`, which does not return a DIRECTORY at a sidecar's name — the exact call round 10 replaced in `CoidWitness`'s own seam default (`GetFiles=[]` vs `GetFileSystemEntries=[coid-witness.errors.log.2]`). `tools/probe/Program.cs:1075-1078` re-reads every sidecar with `File.ReadAllLines` although the snapshot already holds those lines (F33's shape, one consumer over); that one does NOT lie — `ReadTail` prints `(could not be read: <Type>)`. No consumer can obtain a wrong STATE this way; what is lost is the evidence an operator sends to support. | `src/TradeAgent.Diagnostics/Doctor.cs:284-295`, `tools/probe/Program.cs:1075-1078` + `:2664` (`ReadTail`) | Hand both consumers the snapshot — a `CoidWitness` member that returns the tail of each sidecar it already read — so neither globs or opens anything. Minimum: move the try INSIDE the `foreach`, write a `sidecars-not-collected.txt` naming each file and its exception into the staging directory, and switch the glob to `GetFileSystemEntries`. Probes: `SupportPackageVerifyR10Probes.cs` (2 RED + 1 control). |
| **R10-3** | MED | **Both mutants the record declares survivors are load-bearing, and both are caught by a deterministic sub-second test.** *MR10-4d:* the record says the `Running → Stopping` lock is not verified load-bearing because "the states that would separate the two are ones where a `Record` is already inside the lock". That is true of the write and false of the ORDER — with the lock, `Stop` cannot start the teardown steps (which call into ATAS to unsubscribe) until the in-flight write has left the guard; without it they run over it. `AdapterTeardownTests` 13/13 green under the mutant (reproduced) and RED 1/16 against mine. *MR10-3a:* the record says the rotation's refusal is redundant because "`_sidecarBytes` is seeded from `Snapshot().Length(log)` … so `Rotate` is never entered at all". That holds for the FIRST append only — `_sidecarBytes` is seeded once (`:2486`, `if (_sidecarBytes < 0)`), and every append after it decides from the counter while `Rotate` takes a FRESH snapshot (`:1625`), which may be unreadable when the seed was not. A long-lived bridge whose directory stops being readable mid-session is that state, and there the refusal is the only guard: mutant applied → `CoidWitnessTests` + `WitnessSnapshotTests` 189/192 green (the 3 being mine), RED against mine. | `src/TradeAgent.AtasBridge/AdapterTeardown.cs:233`; `src/TradeAgent.AtasBridge/CoidWitness.cs:1631` read against `:1625` and `:2486`; `records/U14.md` "## Round 10", the two survivor paragraphs and "What round 10 did NOT do" | Keep `TeardownLockVerifyR10Probes.The_teardown_steps_do_not_start_while_a_write_is_still_inside_the_guard` and `SnapshotSeamsVerifyR10Probes.A_rotation_entered_from_the_counter_still_refuses_an_unreadable_snapshot` as permanent tests, and correct the two paragraphs: neither guard is redundant. |
| **R10-4** | LOW | **The F25 reversal is now reachable by a third party, and the record does not say so.** Any process that can write in `Paths.BridgeDir` can leave one unreadable file matching `coid-witness.errors.log*` — its own per-writer sidecar at `chmod 000`, or a Windows share-mode hold — and the canonical bridge's whole snapshot becomes `Unreadable`: `Trouble` non-null, `io:degraded`, and `SupportsClientOrderId` false for as long as it stands (`A_second_writers_unreadable_file_degrades_this_machine`, PASS). The direction is right and I did not refute it — it is fail-closed, and it is the same trust domain as the witness file itself. The record's F25 paragraph argues only about a refused writer's CONTENT (which still only notes — verified) and does not state that UNREADABILITY hands a second process an availability lever over live trading. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1794-1806` (`Derive`'s refusal branch), `records/U14.md` "## Round 10", the F25 boundary row | One sentence in the record and in `Derive`'s doc: the widened boundary is deliberate, fail-closed, and reachable by anything that can write in the bridge directory — which is already able to write the witness. If that is not wanted, exclude non-canonical names from the degraded scope while keeping them in the noted one. |
| **R10-5** | LOW | **Three claims in the record do not check out.** (a) *"the shipped suite already covers it at 45 s (`A_quiet_bridge_that_only_beats_is_not_dropped_at_shipped_values`)"* — that test is **not in the suite**: `grep -rn "only_beats" tests/ src/ tools/` is empty, and `git log --all -S` finds it only on `u14-verify-r8-probes` and `u14-verify-r9-probes`. The property IS covered, by `BridgeRoundTripTests.A_peer_that_keeps_beating_is_not_dropped_when_the_window_passes` at a 1.5 s timeout — not "at shipped values". (b) The round-9 verifier's fourth probe, `A_peer_that_arrives_long_after_the_last_beat_still_completes_its_handshake`, was not lifted either and is not in the record's omissions list. (c) *"Every filesystem call left in the file, enumerated … 13 lines, and this is the whole of it"* is grepped with `File\.\|Directory\.`, which cannot see a `FileStream` or `FileInfo` constructor; three more exist (`:635`, `:654`, `:2199`). `:654` is the one R10-1 is about. | `records/U14.md` "## Round 10" — "What round 10 did NOT do" and the filesystem-call table | Correct the citation to the test that exists and its real values; add the second unlifted probe to the omissions list or lift it; re-run the enumeration as `grep -nE "File\.\|Directory\.\|FileStream\|FileInfo\|Path\.Exists"` and add the three rows. |
| **R10-6** | LOW | `AppendDurably` (`CoidWitness.cs:650`) is now dead: its only caller was the old carry append at `e113c4c:1612`, which directive 3 replaced. It does not warn (an unused private static method is not a compiler warning), so "0 warnings" does not cover it. Beside it, the ONE sidecar append (`:2490`) is `File.AppendAllText`, which is not flushed — so the primary record of a durability gap is written non-durably while the carried RESTATEMENT of that same line is flushed. That asymmetry is defensible (the flush exists to order the carry against a destructive rename, per `DefaultWriteSidecar`'s own doc) and is unchanged since round 9, but nothing in the file says so. | `src/TradeAgent.AtasBridge/CoidWitness.cs:648-650`, `:2490` | Delete `AppendDurably`, or use it for the sidecar append and say which. Either way one sentence saying why the append is not durable and the carry is. |

**1 HIGH / 2 MED / 3 LOW.**

**Closed from the round-9 verifier's record, on my own re-runs and mutants:** **R9-2** (`Ready()` =
load → recover → snapshot on every public member; `Every_reading_is_the_same_whichever_is_asked_first`
green in my run); **R9-3** (MR10-5b RED 1/42 in the SHIPPED suite); **R9-4** (the carry comes from
`Generations(log)`, the file set being rotated — read at `:1633`, and
`A_refused_writers_rotation_carries_its_own_unresolved_line_not_the_canonical_one` green);
**R9-5** (`A_denied_candidate_glob_is_unreadable_rather_than_no_stranded_rewrite`, mine, PASS).
**R9-1 is closed for the states it built and reopened one act earlier, as R10-1.**

**Examined and NOT raised:** `Quarantine`'s two probes (target 7 — both fail-safe, both off the
sidecar path; the future-mtime case costs a permanent `Noted` and no state lie); `NoteIncompatible`/
`NoteUnauthenticated` writing peer and stamp as two statements (display-only, transient, the round-8
verifier's own recorded non-finding); `SidecarSnapshot.Length` reconstructing a byte count from UTF-16
char lengths, so a non-ASCII or CRLF sidecar rotates LATE (bigger file, the stated fail direction);
`_sidecarBytes = 0` on a refusal meaning the log grows unbounded while the denial stands (stated by the
builder as the direction to fail in, and it is); the same-length + forged-mtime rewrite that defeats
the stability check (target 2 — fail-closed, and the actor needed can write the witness itself).

---

## NOT verified, by name

- **WINDOWS, ENTIRELY.** The box was not mine; another leg holds the grant. The builder's
  identity-checked bridge compile (5 warnings / 0 errors at the same four line numbers), its
  `_witness in the adapter: 0` on-box measurement, its `tools/atas-gate` GATE PASSED transcript, its
  `win-state` before/after and its re-hash are **claims I read and did not re-run.** The one thing I
  could check independently, and did, is that all six production files in my worktree hash to exactly
  the six digits the builder printed — so the bytes compiled and gated on that machine are the bytes
  every finding above is about. Everything downstream of `dotnet build` on Windows is unverified by me.
- **The ATAS teardown callback.** Which callback ATAS fires on a strategy teardown (`OnStopping` vs
  `OnDispose`) is still two hooks and a compiler. No strategy was loaded on a chart in a running ATAS.
  Unchanged since round 6.
- **THE PLATTER.** I did not refute MR10-3d and I did not confirm it: no in-process observation on
  this machine separates a flushed write from an unflushed one, and a `SIGKILL` does not either
  because the page cache survives it. I did not attempt the one instrument that would say something
  adjacent — timing `fsync` over many writes, which measures the SYSCALL and not the platter — and
  I make no claim either way. What IS measured is the ORDER, and R10-1 is about the order being wrong
  one act earlier than the flush.
- **R10-1 out of process.** My two REDs are in-process, through the `writeSidecar` seam, and the seam
  is faithful to production in the one respect that matters (it opens with `FileMode.Create` before
  it fails — measured separately by `PREMISE_FileMode_Create_empties_the_file_at_the_open`). I did NOT
  reproduce it with a real `SIGKILL`: the window is one `open(O_TRUNC)` away from one `write`, and 40
  randomly-timed kills will not land in it. The join between "production truncates first" and "a
  truncate-then-fail loses the marker" is a code read of `WriteDurably`, named here as such.
- **Everything here is macOS/APFS on Apple silicon.** The denial instrument is `chmod 000` and an
  execute-only directory; the Windows-reachable equivalents are an ACL denying `FILE_READ_DATA` and a
  share-mode hold, neither of which I ran. That `File.Move` and `FileMode.Create` behave the same in
  kind on NTFS is reasoning, not measurement — and R10-1 does not depend on it: `FileMode.Create`
  truncating is a .NET contract, not a filesystem one.
- **The dashboard rendering of the row strings.** I measured `StatusDetail`, `Incompatible` and
  `Unauthenticated` as values; no screen was drawn.
- **MR10-1a, MR10-1b, MR10-1d, MR10-3b, MR10-3c, MR10-5a and MR10-4a/4b/4c** — the builder's own RED
  mutants. I read them and did not reproduce them; the six I did run are in the table above.
- **The F8 residual and `Quarantine`'s 64 slots** — still open, in nobody's brief, untouched by me.
- **Whether the 5 on-box warnings predate this unit** — still needs a second on-box build at an older
  sha, which nobody has run.

## What I did NOT do

- **I fixed nothing.** `git diff --stat 01fcd60 -- src tools packaging` is empty and all five
  production SHA-256s match the `cp` copies taken before the first mutant. Every mutant was restored
  from that copy and `touch`ed — never `git checkout --`.
- **I did not push, merge or rebase.** Six commits on `u14-verify-r10-probes`, all under `tests/` and
  `scratchpad/`: `42dc5b3`, `9bfdea5`, `cd92056`, `9260cc6`, `2deddcf`, `5f0485d`. Nothing was run in
  the main worktree; this record was written there by hand, as briefed.
- **I did not touch the box.** No `win-push`, no ssh, no build, no gate.
- **I did not run the R3 five-writer lease harness** at this sha. The builder ran it three times
  (160/0 each) and the round-9 verifier ran its own three times; the surface it covers — one writer
  per sidecar file — is not what round 10 changed, and my own three-process concurrency harness
  exercises the same lease from the reader side. Named rather than left implicit.
- **I did not exercise `SwitchConnectorAsync`, the UI, `tools/probe` or `probe atas`.** The two probe
  defects in R10-2 are source reads, not executions — `tools/probe` has no test project and cannot run
  without a live bridge, which the file's own comment states.
- **I did not attempt the ACL/share-mode denial instruments**, having no Windows box.
- **Full suite run exactly twice.** Run 1 (target 0) before any probe was compiled in; run 2 (target 6)
  after every mutant was restored, with my probe classes filtered out. 517 both times.

VERDICT: FAIL — 1H/2M/3L
