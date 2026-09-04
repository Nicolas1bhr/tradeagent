# U14 — ADVERSARIAL-VERIFY RECORD · round 9, leg [2], Opus — **FRESH verifier** (rounds 4–8's sessions are gone)

**Sha under test:** `e113c4c` (= `10fa21f` + 8 commits). Worktree
`…-worktrees/u14-verify-r9`, branch `u14-verify-r9-probes` (cut from the detached `e113c4c`).
Toolchain `PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS.
The round 4–8 verifiers' records and their probe branches are my BASELINE, not my verdict.

---

## Target 0 — the headline figures, reproduced (leg [2]'s own run, not the builder's)

```
$ dotnet build TradeAgent.sln --no-incremental
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.68
EXIT=0
```
**0 warnings on `--no-incremental`: verified.**

```
$ dotnet test TradeAgent.sln          # full-suite run 1 of 2, before any probe was compiled in
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 980 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 290, Skipped: 0, Total: 290, Duration: 2 m 8 s - TradeAgent.IntegrationTests.dll
EXIT=0
```
**477 green (75 / 112 / 290) — the builder's Mac figure reproduced exactly.**

*(record in progress — checkpointed as each target completes)*

---

## Target 1 — the rotation, every interleaving · the class-closure argument REFUTED at row 8 · **FINDING R9-1 (HIGH)**

The builder's row 8 is: *"the plain path (`carry is null`) — nothing unresolved exists to lose — the
deciding line was read off all three files first and is absent or `RESOLVED`."*

**`LastDecidingLine()` also answers null when every generation that could have answered THREW.**
`LastLineWhere` (`CoidWitness.cs:1729-1745`) catches per generation — *"the next generation may still
answer"* — and returns null when none of them did. `Rotate` reads that null as "there is nothing
unresolved to protect" and takes the plain path, whose two `File.Delete` calls
(`CoidWitness.cs:1600-1601`) destroy a file this run never read. F31 closed exactly this conflation
on every READ probe this round; the write path was excluded from the enumeration on the ground that
*"a wrong answer costs at worst a rotation or a quarantine attempt"*. What it costs is the evidence.

### Refutation 1 — in-suite, real `chmod 000`, no seam, control beside probe

`tests/TradeAgent.IntegrationTests/RotationDestroysVerifyR9Probes.cs` (mine, `5f2251c` on
`u14-verify-r9-probes`). The measurement is the CONTENT of the sidecar set, not the file names —
the plain path re-creates `.1` by moving the current log onto it, so "the file exists" is true and
empty of the thing that mattered.

```
$ dotnet test … --filter "FullyQualifiedName~RotationDestroysVerifyR9Probes"
  CONTROL_a_readable_leftover_staging_file_is_carried_across_the_next_rotation   PASS
  A_rotation_destroys_a_staging_file_it_could_not_read                           [FAIL]
      Assert.Contains() Failure: Sub-string not found   Not found: "TA-GAP"
  A_rotation_destroys_a_rolled_generation_it_could_not_read                      [FAIL]
      Assert.Contains() Failure: Sub-string not found   Not found: "TA-GAP"
  A_carrying_rotation_destroys_the_staging_file_it_could_not_read                [FAIL]
      Assert.Contains() Failure: Sub-string not found   Not found: "TA-UNREADABLE-GAP"
Failed!  - Failed: 3, Passed: 1, Skipped: 0, Total: 4, Duration: 61 ms
```

Both directions: the control is the builder's own state one permission bit different, and round 9's
carry-before-delete carries it correctly. The fourth case shows it is the branch's RULE and not the
plain path's accident — a carrying rotation restates the line it could read and then deletes the one
it could not.

### Refutation 2 — a REAL process kill, in the window round 8 named

`scratchpad/rotkill9` (a console with a `ProjectReference` to the worktree's real
`TradeAgent.AtasBridge`). The state is the ordinary one: the ONLY unresolved marker in a staging file
left by a crashed rotation, an oversized current log holding nothing that decides anything, no `.1`.
`ReportAndQuarantine()` — and therefore `Rotate` — runs at `CoidWitness.cs:658`, *before*
`Save(clientOrderId)` at `:676`, so killing inside the `replace` seam is precisely *"a machine that
dies between the rotation and the save"*, which is round 8's own correction quoted in the builder's
test doc.

```
############ CASE kill-readable  (the control — the leftover is readable)
about to rotate; the machine dies between the rotation and the save
  killed, exit=137
  files      : coid-witness.errors.log, coid-witness.errors.log.1
  Trouble    : an earlier run could not write the write-ahead record; the account of it is in …
  Token      : session:0d375c83,records:1,prior:0,io:degraded
  Noted      : True   GapClosed : False   Standing : Unresolved
  TA-GAP still on disk anywhere: True

############ CASE kill-denied   (the same leftover, chmod 000)
about to rotate; the machine dies between the rotation and the save
  killed, exit=137
  files      : coid-witness.errors.log, coid-witness.errors.log.1
  Trouble    : <null>
  Token      : session:adcbfed2,records:1,prior:0,io:noted
  Noted      : True   GapClosed : False   Standing : Noted
  TA-GAP still on disk anywhere: False
```

**`Trouble = <null>` is R8-1's figure, reproduced at `e113c4c` through a different door.**
`AtasStrategyAdapter.cs:655` reads `SupportsClientOrderId = proof.ProvesRoundTrip() &&
_teardown.Trouble is null`, so the gateway goes on trading fully automatically over a write-ahead
record that never reached the disk — and the last copy of the evidence is gone, not merely hidden.

### The rest of target 1 — what I could NOT refute

- **The four interleavings the brief names, in the ordinary state**: reproduced green. The builder's
  `A_gap_in_the_current_log_is_readable_at_every_instant_of_the_rotation` takes four readings inside
  the window and my re-run of the whole `CoidWitnessTests` class is 145/145 (below). The `.rotating`
  name IS scanned (`SidecarGenerations`, `:1673-1685`), between the log and `.1`, which is where it
  sits in age.
- **A later rotation never deletes a staging file holding an unresolved marker** — true whenever the
  marker can be READ (my control, and the builder's own test), false when it cannot (R9-1).
- **`Flush(true)` precedes deletion.** The order is `AppendDurably`/`_writeSidecar` → `File.Delete`,
  read straight off `:1610-1622`. **NOT verified: that `Flush(flushToDisk: true)` reaches the
  platter** — same limit the builder recorded; a SIGKILL leaves the page cache intact, so no
  observation on this machine separates a flushed write from an unflushed one. I did not refute the
  builder's own honest statement of this.
- **`Rotate` rotates `SidecarPath` but decides from `ErrorLogPath`.** For the lease OWNER they are
  the same file. For a REFUSED writer (`SidecarPath`, `:504-512`) they are not: a refused writer's
  own sidecar passing 64 KiB rotates on the canonical file's deciding line, restating the canonical
  gap into its own file and deleting its own `.1`. No canonical evidence is destroyed and the
  refused writer's generations are outside the degraded scope by the ratified F25 boundary, so this
  is **recorded, not raised** — see finding R9-4 (LOW).

---

## Target 2 — unreadable ≠ empty, every probe · **NOT refuted on the read path** (one uncovered probe, LOW)

`tests/TradeAgent.IntegrationTests/ProbeDenialsVerifyR9Probes.cs` (mine). **The builder's stated
reason for the two new seams — that an ACL denying attributes or refusing an enumeration "cannot be
provoked on this machine without also breaking the committed read in the same directory" — is not
true on a POSIX filesystem**: a directory with the EXECUTE bit and not the READ bit serves every
open by name and refuses `readdir`. So I drove the enumeration denial against the REAL
`Directory.GetFiles`, with the committed read still working, and used no seam anywhere in this file.

```
$ dotnet test … --filter "FullyQualifiedName~ProbeDenialsVerifyR9Probes"
  PREMISE_an_execute_only_directory_refuses_readdir_and_still_serves_opens        PASS
  An_enumeration_this_run_cannot_perform_flags_the_zero_and_does_not_read_as_empty PASS
  A_canonical_generation_that_cannot_be_read_is_not_one_with_nothing_in_it        PASS
  A_rolled_generation_that_cannot_be_read_is_noted_by_name                        PASS
  A_staging_generation_that_cannot_be_read_is_noted_by_name                       PASS
  A_directory_at_the_sidecars_name_is_unreadable_rather_than_absent               PASS
  A_genuinely_absent_sidecar_reads_as_clean_empty                                 PASS
  A_denied_candidate_enumeration_is_covered_only_by_its_sibling_globs_flag        PASS
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 31 ms
```

Read denied, enumerate denied, a directory at the name, the `.1` name denied, the `.rotating` name
denied — every one classifies UNREADABLE, `Noted` true, and the degraded/noted split falls where the
F25 boundary says. **Both directions: a genuinely absent sidecar still reads `Trouble` null, `Noted`
false, `io:ok`, `Standing.Clean`, and the zero is NOT flagged provisional.** I did not refute this
target.

**The enumeration I attacked, and the one call it misses.** The builder's table is "every filesystem
call on the sidecar path". It is complete for that path (`grep -n "File.Exists" CoidWitness.cs`
returns the four write-path calls it names and nothing else, re-run by me). It does not cover
`Candidates()` (`CoidWitness.cs:1519-1533`) — `Directory.GetFiles(dir, "<witness>.tmp*")` under
`catch (Exception) { return []; }`, the same conflation `SidecarSet` was fixed for, one glob over, in
the SAME directory, on the recovery path: a denial there means "there is no stranded rewrite to
recover". It is currently unobservable because both globs run against the same directory, so
`SidecarSet`'s new flag fires whenever this one would — which is a coincidence of the two globs
sharing a parent, not a guard. **Finding R9-5 (LOW).**

**And the write path is where the class is NOT closed — see R9-1.** The builder's dismissal ("a wrong
answer costs at worst a rotation or a quarantine attempt") is the sentence R9-1 refutes.

---

## Target 3 — row precedence for a new connection · **NOT refuted** (two argued cells now have tests)

`tests/TradeAgent.IntegrationTests/PeerRowVerifyR9Probes.cs` (mine). The builder's class-closure table
has six cells; two of them are argued and cite no test — **"explicit credential vs derived silence,
ACROSS two connections"** (which is the second half of Codex's own F32 CHECK: *"An older explicit
`_unauthenticated` marker behaves similarly"*), and the bounce brief's third permutation, **"a silent
peer that later speaks v2"**, which nothing builds. I built both, against real named pipes.

```
$ dotnet test … --filter "FullyQualifiedName~PeerRowVerifyR9Probes&…!~kept_for_minutes"
  A_silent_peer_that_later_speaks_v2_reports_the_protocol_refusal                     PASS
  A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure         PASS
  A_silent_peer_that_later_fails_the_challenge_reports_the_credential_refusal          PASS
  A_peer_that_arrives_long_after_the_last_beat_still_completes_its_handshake           PASS
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 4 s
```

Plus the carried round-7 probes, re-run at this sha and green (`PeerRefusalVerifyR7Probes`: a live
credential refusal not masked by a stale protocol one; the reverse order; a live good bridge clearing
both). **The precedence is correct in every permutation I could build.** The two argued cells are
correct today and were not pinned by a test at `e113c4c` — I have written them, and whether the
builder's own suite catches a mutant on that cell is measured in the mutants table (MV9-a).

**Examined and NOT raised.** Round 9 made `Unauthenticated` no longer blind to `_incompatible`, which
is a behaviour change in a public property. `grep -rn "Unauthenticated" src/` finds **no production
consumer outside `AtasConnector` itself** — the row goes through `StatusDetail`
(`ConnectorSdk/Contracts.cs:75` → `TradingGateway.cs:72`, `AtasHealth.cs:169`), which orders by the
stamps. So the change cannot surface a "not authenticated" sentence for a peer that merely speaks the
wrong protocol: such a peer has `_authenticated` true, and `silent` requires `!_authenticated`.

---

## Target 4 — PRIOR 29's wording · pinned · but round 9's own new `Noted` cause is order-dependent · **FINDING R9-2 (MED)**

The wording split holds and is pinned by a mutant (MR9-6a below). Three sentences, three causes, none
of them calling a recovery a refusal; more than one live cause names none of them; and this round's
new file name is not mistaken for a second writer:

```
$ dotnet test … --filter "FullyQualifiedName~NotedCausesVerifyR9Probes"
  The_three_noted_sentences_are_distinct_and_name_their_own_cause                 PASS
  A_staging_generation_is_not_attributed_to_a_refused_writer                      PASS
  CONTROL_the_recovered_rewrite_is_noted_when_the_recovery_has_been_asked_for     PASS
  The_recovered_rewrite_is_noted_however_the_reading_is_ordered                   [FAIL]
      Noted answered false on a fresh instance while Token answered
      'session:75d1cfe7,records:1,prior:1,io:noted' on another
  The_headline_says_the_same_thing_whichever_order_its_inputs_were_read_in        [FAIL]
      Assert.Equal() Failure: Values differ    Expected: Noted    Actual: Clean
Failed!  - Failed: 2, Passed: 3, Skipped: 0, Total: 5, Duration: 42 ms
```

**What the class-closure argument did not enumerate is its own new WRITE.** The round-9 adoption sets
`_noted = true` (`CoidWitness.cs:1376`) — the builder's own "one behaviour change came out of writing
the test" — and the adoption runs inside `EnsureRecovered()`. **`Noted` (`:959`) runs `EnsureLoaded()`
and NOT `EnsureRecovered()`**, so for a machine whose ONLY cause is a recovered rewrite the answer
depends on what was asked first: `Token()` says `io:noted`, a fresh instance's `Noted` says false, and
`Standing` assembled from values read in the order (`Noted`, then `Trouble`) says **Clean** where
`Standing(witness)` says **Noted**.

This is the hazard `Trouble`'s own doc names, in the builder's own words: *"Without it two readings
from ONE instance could disagree … and the only production caller was safe by ordering rather than by
rule."* `Trouble` was fixed by running the recovery; `Noted` and `GapClosed` were not, and round 9
gave `Noted` a cause only the recovery can discover.

**Why MED and not HIGH:** `grep -rn "\.Noted" src/ tools/` finds exactly one production reader —
`CoidWitnessReport.Standing(CoidWitness)` (`CoidWitnessReport.cs:89`) — and C#'s left-to-right
argument evaluation puts `Trouble` (which does run the recovery) before `Noted`, so today's operator
sentence is right. It is right by argument order, not by rule, on a T1 surface, and the same
`Standing(bool,bool,bool)` overload is public for callers that hold the three values already.

---

## Target 6 (R8-2) — is `AdapterTeardown` really the only door? · **NOT refuted**

I cannot compile `AtasStrategyAdapter.cs` on this machine (`<Compile Remove>`d off the box), so this
is a source-level enumeration plus the mutants that pin the door. **Every claim below is a grep or a
read, named as such.**

```
$ grep -n "_witness" src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs | wc -l
0
$ grep -n "new CoidWitness\|new AdapterTeardown" src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs
246:    readonly AdapterTeardown _teardown = new();
$ grep -c "_teardown\." src/TradeAgent.AtasBridge/AtasStrategyAdapter.cs
17
```

The adapter holds no `CoidWitness` reference and constructs none; the only handle it has is one
`AdapterTeardown` field. The seventeen uses, classified by reading each:

| kind | sites | through the guard? |
|---|---|---|
| WRITES | `:1424` `Submitting` (`Place`), `:1578` `Identified` (`Place`), `:1840` `Submitting` (`ClosePosition`), `:2072` `Identified` (the order-event fan) | **all four** — `Submitting`/`Identified` are the only members that reach `CoidWitness.Submitting`/`Identified`, and both go through `Record`, which holds the lock the release takes |
| READS | `:636` `:655` `Trouble`, `:1429` `:1845` `Path`, `:1430` `:1846` `LastWriteFailure`, `:1431` `:1847` `Stopped`, `:691` `PriorSessionIds`, `:747` `Token()`, `:3203` `PriorSession` | unguarded, and **that takes no lease**: `Lease()` is called from `CoidWitness.cs:653` and `:739` only, both inside `Submitting`/`Identified`; `EnsureRecovered` (`:1439`) is `AdoptInMemory()` and nothing else — no filesystem write, no lock |
| LIFECYCLE | `:457` `Started()`, `:502` `Stop(...)` | — |

**Can the frame loop still write after teardown through any path?** The three routes the round-8
finding named are all writes and all four write sites are now `_teardown` methods: the order-event fan
(`:2072`), `Place`'s two (`:1424`, `:1578`) and `ClosePosition`'s (`:1840`). The health-probe path —
`Describe()`'s `PriorSessionIds` (`:691`), `Token()` (`:747`), `Trouble` (`:636`, `:655`) — is reads
only, and reads never lease. A fifth write site cannot be added by forgetting a wrapper because there
is no witness in the file to call.

`Place`/`ClosePosition` treat `false` as `AtasRejectedException` "nothing was submitted"
(`:1426-1432`, `:1842-1848`), and `Guard` (`:3322`) swallows exceptions leaving `recorded` false —
the same refusal. Fail-closed in both directions.

**Mutants pin it** (below): MD-R9-2 (both doors bypass `Record`) → **RED 8/15**; MP21-half (only the
CHECK leaves the lock) → **RED 1/15** — so the round-8 MED R8-4 is closed by the builder's staged
test, independently confirmed; MF26 (the release back to a plain statement) → **RED 3/15**.

**NOT verified by me: that the adapter compiles and binds against the real ATAS assemblies.** The box
is not mine this round. What I CAN state is that the file the builder hashed on the box is the file I
read: my own `shasum -a 256` of the four production files at `e113c4c` matches its identity check
digit for digit —
`CoidWitness.cs 9787a3a2a9…6a93`, `AdapterTeardown.cs 95bd516e00…816a`,
`CoidWitnessReport.cs c7891ae9aa…dc6e`, `AtasConnector.cs c1a1134718…e28f`.
The bridge compile, the 5 on-box warnings and the `tools/atas-gate` transcript remain the builder's
claims, read and not re-run.

---

## Target 7 (R8-3) — the heartbeat predicate on every turn · **NOT refuted**, all four cases

```
$ dotnet test … --filter "…kept_for_minutes|…PipeLivenessVerifyR8Probes|…dribbles_frames_without_beating|…silent_peer_is_not_masked_by_the_previous_peers_refusal"
Passed!  - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 3 m 15 s
```

| case | probe | result |
|---|---|---|
| a chatty non-heartbeating peer dropped at `HeartbeatTimeout` | the round-8 verifier's own `A_peer_that_dribbles_any_frame_keeps_the_only_pipe_instance` — **RED at `10fa21f`, GREEN here** — and the builder's `A_peer_that_dribbles_frames_without_beating_is_dropped_like_a_silent_one` | PASS |
| a silent one too | `CONTROL_a_silent_peer_is_dropped_and_a_second_bridge_gets_in` | PASS |
| a healthy heartbeating one kept | `A_quiet_bridge_that_only_beats_is_not_dropped_at_shipped_values` (45 s) | PASS |
| **a legitimately quiet-but-beating bridge kept for MINUTES** | mine: `A_quiet_bridge_that_only_beats_is_kept_for_minutes_at_shipped_values` — shipped 15 s timeout, 5 s beats, **26 beats = 130 s**, more than eight whole windows, no other traffic; `Bridge` asserted non-null on every beat, then `READY` and `StatusDetail` null | PASS |
| no frame lost or duplicated across the poll | `The_idle_poll_neither_loses_nor_duplicates_a_frame_across_its_wakeups` | PASS |

**The regression I looked for and did not find.** The predicate is now asked after EVERY dispatched
frame, including the auth frame — which does not write `_lastHeartbeat` (`:557` hello and `:574`
heartbeat are its only writers). A peer arriving after a long idle period could therefore have been
dropped between its challenge and its hello. It is not: `PeerHasGoneQuiet()` (`:216-221`) measures
from the LATER of `_lastHeartbeat` and `_peerArrived`, so every new connection gets a full window from
arrival. Pinned by mine:
`A_peer_that_arrives_long_after_the_last_beat_still_completes_its_handshake` (3 s of dead air against
a 1 s timeout, then a full handshake) — PASS.

**Mutant MR9-3** (round 8's shape — the predicate asked only when the poll wins) → **RED 2/48**.

**MR9-3b, the builder's survivor, I did not resolve either.** The builder recorded that asking the
question after the dispatch rather than before it is unverified, that it wrote a test for it, watched
it fail against the fixed code for scheduling-jitter reasons, and removed it. I made no attempt to
build that state and make no claim about it. **NOT verified, by both of us.**

---

## Target 5 — regression: the round-8 harnesses still bite at this sha

Cherry-picked from `u14-verify-r8-probes` onto `u14-verify-r9-probes` and re-run. Two files needed
the round-9 API (`AdapterTeardown` now owns the witness; `Stop` lost `releaseWitness`); the two
`TeardownReach` probes that asserted the round-8 HARM were rewritten to assert the round-9 ACCEPTANCE
through the only door there is, and renamed `R9_the_former_unguarded_*`. No probe was deleted.

```
$ dotnet test … --filter "…VerifyR6Probes|…VerifyR7Probes|…TeardownReachVerifyR8Probes|…RotationWindowVerifyR8Probes|…AbsenceAndRowVerifyR8Probes"
Failed!  - Failed: 1, Passed: 23, Skipped: 0, Total: 24, Duration: 5 s
```

The one failure is the round-8 verifier's **always-`Assert.Fail` reporter**
(`A_restatement_that_does_not_land_leaves_the_only_copy_of_the_gap_unscanned`) — its body is a
measurement, not a pass/fail claim, and it fails by construction at every sha.

**I chased its numbers rather than reading them, because they look like a live R8-1.** It reports
`Trouble now = <null>` with the gap sitting in `.rotating`. That reading is **stale in meaning, not a
defect**: the probe reads AFTER the session completed, and I dumped the current log to see why —

```
R9 CURRENT LOG CONTENT >>> …:35.5074000 ignored …tmp-dead: it does not descend from the committed file
                         | …:35.5152610 RESOLVED coid-witness committed cleanly after the failures above.
```

The rotating session went on to commit successfully and wrote a RESOLVED newer than the ERROR, so the
gap is closed by rule and `Trouble` null is right. Its `scanned generations contain ERROR: False` line
is computed off the probe's own round-8 pair `{log, log.1}`, not off `SidecarGenerations()`. **Not a
finding.**

**The other carried harnesses:** F23 idle-poll drop → green (target 7); V4 counters and the reverse
order (`PeerRefusalVerifyR7Probes`) → green (target 3); `AdapterTeardown` on every terminal path →
green, and MF26 still RED 3/15; MF27b's seam → still RED 3/147; the four round-6 F17 variants, the
lease/dispose handover and the flagged-zero probe → green.

**R3, per-writer sidecars, real OS processes at this sha.** `scratchpad/r3writer9` (a console with a
`ProjectReference` to the worktree's real `TradeAgent.AtasBridge`): five writers × 40 claims against
one witness, every process held alive for 25 s so no lease is released by exit and every refusal is
genuine contention.

```
  run1: files=4 lines=160 naming-a-claim=160 committed=40      (winner b: ok=40 refused=0)
  run2: files=4 lines=160 naming-a-claim=160 committed=40      (winner a)
  run3: files=4 lines=160 naming-a-claim=160 committed=40      (winner d)
```

**160 refusals, four losers with one file each, every line naming a claim, DROPPED=0, and the
committed file holding exactly the winner's forty** — the round-8 figure reproduced three times.

---

## Mutants

Every production file restored from a `cp` copy taken before the first mutant and `touch`ed —
**never `git checkout --`**. SHA-256 taken before the first mutant and confirmed identical after the
last, and `git diff --stat e113c4c -- src tools packaging` empty:

```
9787a3a2a97cc7e22e48f82038e634610b283fd7e548bdca9177e645b8146a93  CoidWitness.cs
95bd516e0015204cf5974c71846816a3c8ca42d2d220f8683c4aa66be7de816a  AdapterTeardown.cs
c7891ae9aa95d307cbf5ded15d0c81c874f4855c1bf81247e46d61b6d53edc6e  CoidWitnessReport.cs
c1a113471e8f4be5393638ab4118174e17a29722d9b54f3453b53eea383e28f0  AtasConnector.cs
```

(These are also the four hashes the builder's on-box identity check reports, matched independently.)

| # | Mutant | Result |
|---|---|---|
| **MR9-1a** | `SidecarGenerations()` stops yielding the staging name (round 8's reader) | **RED 4/152** — 3 of the builder's + my `A_staging_generation_that_cannot_be_read_is_noted_by_name` |
| **MR9-4a** | `File.Exists` back in front of the sidecar read | **RED 5/152** — the builder's 5-row theory and directory-at-the-name test, plus mine |
| **MR9-6a** | round 8's single `Noted` sentence for all three causes | **RED 5 net /149** — the builder's four plus my wording probe |
| **MF27b** | round 8's own — `File.Delete(rolled)` moved above the restatement | **RED 3/147**, still bites at this tip |
| **MP21-half** | only the CHECK leaves the lock (the round-8 verifier's R8-4) | **RED 1/15** — `The_stopped_flag_that_decides_is_the_one_read_under_the_lock`. **R8-4 is closed**, and by the deterministic staged test rather than by the race |
| **MF26** | the release goes back to a plain statement after the steps | **RED 3/15**, including both copies of the 40-round race |
| **MD-R9-2** (mine) | both `AdapterTeardown` doors bypass `Record` — round 8's committed adapter shape moved into the class | **RED 8/15**, including my two rewritten `TeardownReach` probes |
| **MR9-3** | the predicate asked only when the poll wins — round 8's shape | **RED 2/48** — the builder's dribbler and the round-8 verifier's |
| **MV9-a** (mine) | an explicit CREDENTIAL refusal always wins over the derived silence, whatever the stamps say — round 8's precedence for that pair | **RED 1/44, and the only test that catches it is MINE.** `BridgeRoundTripTests` (37), `PeerRefusalVerifyR7Probes` and `VerticalSliceTests` all SURVIVE it → **finding R9-3** |
| **MD-R9-1** (mine, *diagnostic only*) | the fix shape for R9-1: `Rotate` returns without rotating when any scanned generation is unreadable | **all four of my RotationDestroys probes go GREEN and nothing else moves — 158 passed / 159, the single failure being the round-8 always-`Assert.Fail` reporter.** One line, no existing test disturbed |

`MD-R9-1` and `MD-R9-2` are diagnostics, restored immediately; neither is a proposed patch.

---

## Findings

**CLASS (§9.10) — R9-1 and R9-5 share one root cause: "I could not read it" is still returned as
"there is nothing there" everywhere outside the six call sites round 9 enumerated.** F31 closed the
conflation on the sidecar READ path and made the answer a flag the caller must carry
(`HasNotes(out unreadable)`, `SidecarSet(out unreadable)`). Two probes were left answering with a
plain value: `LastDecidingLine()` returns **null** both for "nothing unresolved anywhere" and for
"every generation that could have answered threw" (`LastLineWhere`, `CoidWitness.cs:1729-1745`), and
`Candidates()` returns an **empty list** for both "no candidates" and "the enumeration was refused"
(`:1519-1533`). The read path was hardened because a wrong answer there is a wrong REPORT; the same
wrong answer inside `Rotate` is a `File.Delete`. **The structural fix is the one round 9 already
applied twice: make unreadability a value the caller has to handle**, so that no consumer of these two
can treat "I could not look" as an answer — and then `Rotate`'s `carry is null` branch cannot run over
a file it never read.

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **R9-1** | **HIGH** | `Rotate` destroys a generation it could not read. `LastDecidingLine()` answers null when every generation threw; `carry is null` then takes the plain path and its two `File.Delete` calls remove the staging file and `.1` — and the following `File.Move(log, rolled)` puts the current log at the deleted name, so the file set looks intact. Measured on the real filesystem with `chmod 000` and no seam: with the only unresolved marker in a leftover `.rotating`, `TA-GAP` is **gone from every file** after one rotation, while the identical readable case carries it forward. Measured out of process with a real `SIGKILL` in the window round 8 named (between the rotation and the save): `Trouble = <null>`, `io:noted`, `Standing: Noted`, `TA-GAP still on disk anywhere: False` — against `Trouble` non-null / `io:degraded` / `Unresolved` for the readable control. **`AtasStrategyAdapter.cs:655` then keeps `SupportsClientOrderId` true over a lost write-ahead record — R8-1's harm, at R8-1's own fix.** The carry path does it too (`A_carrying_rotation_destroys_the_staging_file_it_could_not_read`), so it is the branch's rule and not the plain path's accident. The builder's class-closure row 8 — *"the plain path … nothing unresolved exists to lose … absent or RESOLVED"* — is the sentence this refutes, and the enumeration excused it with *"a wrong answer costs at worst a rotation"*. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1596-1602` (the plain path's two deletes), `:1610-1613` (the carry path's), `:1729-1745` (`LastLineWhere`'s per-generation catch), `:1590` (`LastDecidingLine()` in `Rotate`) | `LastDecidingLine()`/`LastLineWhere` report unreadability alongside the line, and `Rotate` treats "a generation could not be read" as evidence to protect: do not delete it, or do not rotate at all until it can be read. Fail-closed is enough — the log grows, which `AppendToErrorLog`'s catch already tolerates. **Verified as one line by diagnostic mutant MD-R9-1: my four probes go green, 158/159 stay green, nothing else moves.** Probes ready on `u14-verify-r9-probes` (`5f2251c`): `RotationDestroysVerifyR9Probes.cs`, plus `scratchpad/rotkill9` for the out-of-process reading. |
| **R9-2** | MED | `Noted` answers differently depending on what was asked first. Round 9's adoption sets `_noted = true` (`:1376`) and the adoption runs under `EnsureRecovered()`; `Noted` (`:959`) runs `EnsureLoaded()` only. Measured: for a machine whose only cause is a recovered rewrite, `new CoidWitness(p).Noted` is **false** while `new CoidWitness(p).Token()` is `io:noted`, and `Standing` assembled as (`Noted`, then `Trouble`) is **Clean** where `Standing(witness)` is **Noted**. Today's operator sentence is right only because C# evaluates `Standing(witness)`'s arguments left to right and `Trouble` runs the recovery — which is exactly the hazard `Trouble`'s own doc records as *"safe by ordering rather than by rule"*, re-introduced one property over by this round's own behaviour change. | `src/TradeAgent.AtasBridge/CoidWitness.cs:955-963` (`Noted`), `:1376` (the new write), `CoidWitnessReport.cs:88-89` | `Noted` runs `EnsureRecovered()` as `Trouble`, `Token()`, `All()`, `PriorSession` and `Notes` already do — one line — and a test asks `Noted` FIRST on a fresh instance. `GapClosed` (`:997-1003`) wants the same treatment or a stated reason why the recovery cannot reach it. Probes ready (`a652b0e`): `NotedCausesVerifyR9Probes.cs`. |
| **R9-3** | MED | The class-closure table's cell *"explicit credential vs derived silence, ACROSS two connections"* is argued and not tested, and it is a real weakening: **mutant MV9-a** (an explicit `_unauthenticated` always outranks the derived silence, whatever the stamps say) **survives `BridgeRoundTripTests` 37/37, `PeerRefusalVerifyR7Probes` and `VerticalSliceTests`** — the whole shipped suite — and is caught only by my probe. Codex's F32 CHECK named this state in as many words (*"An older explicit `_unauthenticated` marker behaves similarly"*); the builder's new test uses a PROTOCOL marker. Correct today, unpinned. | `src/TradeAgent.Connectors.Atas/AtasConnector.cs:152-165` (`UnauthenticatedNow`) | Keep `PeerRowVerifyR9Probes.A_newly_arrived_silent_peer_is_not_masked_by_the_previous_peers_auth_failure` (`7a8ff2d`) as a permanent test beside the builder's protocol-marker one, so MV9-a goes RED in the shipped suite. |
| **R9-4** | LOW | `Rotate(log)` is called with `SidecarPath` — which for a lease-REFUSED writer is `<canonical>-<pid>-<session>` (`:504-512`) — but computes `carry` from `LastDecidingLine()`, which scans the CANONICAL generations only. A refused writer whose own sidecar passes 64 KiB therefore rotates on somebody else's deciding line: it restates the canonical machine's gap into its own file and deletes its own `.1`. No canonical evidence is destroyed and refused-writer files are outside the degraded scope by the ratified F25 boundary, so what is lost is support-package content. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1590` read against `:2272-2275` (`Rotate(log)` where `log = SidecarPath`) | Either scan the generations OF THE FILE BEING ROTATED, or state in `Rotate`'s doc that it is canonical-only by design and that a refused writer's sidecar is bounded rather than carried. |
| **R9-5** | LOW | `Candidates()` returns an empty list for a refused enumeration — the same conflation `SidecarSet` was fixed for this round, one glob over, in the same directory, on the RECOVERY path: "I could not list the directory" reads as "there is no stranded rewrite". Not currently observable, because both globs run against the same parent and `SidecarSet`'s new flag fires whenever this one would — a coincidence of the two sharing a directory, not a guard. Not on the builder's enumeration, whose title is "every filesystem call on the sidecar path". | `src/TradeAgent.AtasBridge/CoidWitness.cs:1519-1533` | Give `Candidates()` the `out bool unreadable` treatment its sibling got, and feed it to `_noted` the same way; or record in the enumeration why the recovery path is out of scope. Probe: `ProbeDenialsVerifyR9Probes.A_denied_candidate_enumeration_is_covered_only_by_its_sibling_globs_flag`. |

**1 HIGH / 2 MED / 2 LOW.**

**Closed from the round-8 verifier's record, on my own re-runs and mutants:** its **R8-2** (four write
sites, one guard) — `_witness` in the adapter is 0, all four writes are `_teardown` methods, MD-R9-2
RED 8/15; its **R8-3** (the dribbler) — its own probe is green here and MR9-3 is RED 2/48; its
**R8-4** (MP21-half) — RED 1/15 against the builder's staged test. Its **R8-1** is closed for the
state it built and reopened one probe out, as R9-1.

**Examined and NOT raised** (each written above where it was found): the round-8 reporter's stale
`Trouble = <null>` figure; `Unauthenticated` no longer being blind to `_incompatible` (no production
consumer outside the class); `Guard` swallowing a `_teardown.Submitting` exception (leaves `recorded`
false → the same `AtasRejectedException`, fail-closed); `Task.Delay(IdlePoll, ct)` allocated per loop
turn and never cancelled when the read wins (unchanged from round 8, not a round-9 regression);
`_incompatibleAt`/`_unauthenticatedAt`/`_peerArrivedAt` written without a barrier (the round-8
verifier's own recorded non-finding, unchanged).

---

## NOT verified, by name

- **WINDOWS, ENTIRELY.** The box was not mine this round; another leg holds the grant. The builder's
  identity-checked bridge compile (5 warnings / 0 errors), its `_witness in the adapter: 0` on-box
  measurement, its `tools/atas-gate` GATE PASSED transcript and its re-hash are **claims I read**. The
  one thing I could check independently, and did, is that the four production files at `e113c4c` in my
  worktree hash to exactly the six-of-six the builder printed — so the bytes it compiled are the bytes
  I read. Everything downstream of `dotnet build` on that machine is unverified by me.
- **The ATAS teardown callback.** Which callback ATAS fires on a strategy teardown (`OnStopping` vs
  `OnDispose`) is still two hooks and a compiler. No strategy was loaded on a chart in a running ATAS.
  Unchanged since round 6.
- **That `Flush(flushToDisk: true)` reaches the platter.** I reproduced the builder's own limitation
  rather than refuting it: a `SIGKILL` leaves the page cache intact, so nothing on this machine
  separates a flushed write from an unflushed one. What I did measure is the ORDER.
- **MR9-3b** — whether asking the heartbeat predicate AFTER the dispatch rather than before it is
  load-bearing. I made no attempt to build the state; the builder's own account of removing its flaky
  test stands unchallenged either way.
- **R9-1 and the rotation generally on Windows.** Everything here is macOS/APFS. The denial instrument
  is `chmod 000`; the Windows-reachable equivalent is an ACL denying `FILE_READ_DATA` on the file while
  the parent still grants delete, which I did not run. A share-mode denial (a scanner holding the name
  without `FileShare.Delete`) would make the `File.Delete` fail on Windows and the file survive — so
  **on Windows the ACL variant is the reachable one and the sharing variant is self-limiting**, and
  that sentence is reasoning, not measurement.
- **The dashboard rendering of the `BridgeRow` strings.** I measured `StatusDetail`, `Incompatible`
  and `Unauthenticated` as values; no screen was drawn.
- **The F8 residual** and **`Quarantine`'s 64 slots** — still open, neither in this round's brief nor
  touched by me.
- **Whether the 5 on-box warnings predate this unit** — needs a second on-box build at an older sha.

## What I did NOT do

- **I fixed nothing.** `git diff --stat e113c4c -- src tools packaging` is empty and the four
  production SHA-256s match the copies taken before the first mutant. Every mutant was restored from a
  `cp` copy and `touch`ed, never `git checkout --`. Both diagnostics (MD-R9-1, MD-R9-2) were restored
  immediately.
- **I did not push, merge or rebase.** Four commits on `u14-verify-r9-probes`, all under `tests/`:
  `5f2251c`, `080de23`, `7a8ff2d`, `a652b0e`. Nothing was run in the main worktree; this record was
  written there by hand, as briefed.
- **I did not touch the box.** No `win-push`, no ssh, no build, no gate.
- **I adapted two carried round-8 probe files rather than deleting them.** `AbsenceAndRowVerifyR8Probes`
  and `TeardownReachVerifyR8Probes` used `Stop(steps, releaseWitness)` and a separate witness, which
  round 9 removed; the two probes that asserted the round-8 HARM now assert the round-9 acceptance
  through the single door and are renamed `R9_the_former_unguarded_*`. No probe was removed, and I say
  so here rather than letting a reader assume the round-8 assertions still stand as written.
- **I did not re-run the builder's MR9-1b/1c, MR9-2, MP21, MP21b, MR9-4b/4c, MR9-5a/5b or MR9-6b/6c**
  beyond what is in my mutants table — those are its evidence and I read them. MP21-half, MF26, MF27b,
  MR9-1a, MR9-3, MR9-4a and MR9-6a I re-ran myself.
- **I did not exercise `SwitchConnectorAsync`, the UI, `tools/probe` or `probe atas`.**
- **Full suite run exactly twice.** Run 1 (baseline, before any probe was compiled in) is under
  target 0. Run 2, after every mutant was restored and with my probe classes filtered out:

```
$ dotnet test TradeAgent.sln --filter "…!~VerifyR6Probes&…!~VerifyR7Probes&…!~VerifyR8Probes&…!~VerifyR9Probes"
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 695 ms  - TradeAgent.FaultTests.dll
Passed!  - Failed: 0, Passed: 112, Skipped: 0, Total: 112, Duration: 3 s     - TradeAgent.UnitTests.dll
Passed!  - Failed: 0, Passed: 290, Skipped: 0, Total: 290, Duration: 2 m 8 s - TradeAgent.IntegrationTests.dll
EXIT=0
```

**477 both times.** The tree is left exactly as it was found.

VERDICT: FAIL — 1H/2M/2L
