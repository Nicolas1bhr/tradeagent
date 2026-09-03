# U14 — ADVERSARIAL-VERIFY RECORD · round 4 (leg [2], Opus, independent)

**Sha under test:** `e22eec6241a053a4fa5877c1dadc2e06fa80dc13` (branch `u14-coid-witness-rewrite`, 25 + 5 commits on
main `3f1d8f2`). Verifier worktree `…-worktrees/u14-verify-r4`, branch `u14-verify-r4-probes` off that sha.
Toolchain `PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS. Windows box OFFLINE.

Leg [3] (Codex) ran concurrently on the same sha in another worktree.

## Item 0 — the builder's headline figure, reproduced not trusted

Verified by running `dotnet build TradeAgent.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)` (Time Elapsed 00:00:03.01).

Verified by running `dotnet test TradeAgent.sln` (full-suite run 1 of 2, output
`scratchpad/suite-run1-e22eec6.txt`):

```
Passed!  - Failed:     0, Passed:    75, Skipped:     0, Total:    75, Duration: 1 s - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed:     0, Passed:   111, Skipped:     0, Total:   111, Duration: 3 s - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed:     0, Passed:   201, Skipped:     0, Total:   201, Duration: 14 s - TradeAgent.IntegrationTests.dll (net10.0)
```

**387 green (75 / 111 / 201), 0 failed, 0 skipped — the builder's figure reproduced exactly.**

_(targets below are filled in as they are executed)_

---

## Target 1 — ONE OWNER PER WITNESS (item 1)

**Harness rebuilt** (the round-3 one was lost with the scratchpad): a standalone .NET 10 console
`harness.csproj` with a `ProjectReference` to the worktree's `TradeAgent.AtasBridge.csproj`, so it drives
the REAL `CoidWitness` with the REAL `_replace` (`File.Move`) — nothing injected, nothing stubbed.
Three genuinely separate OS processes, each `new CoidWitness(path)` over ONE bridge directory, each
spinning on a gate file so the three actually overlap, 80 claims each (`Submitting` then, on true,
`Identified`). 240 claims total.

**Refutation executed** — the probe that PASSES if the defect exists is: any id whose `Submitting`
returned **true** missing from the committed file (LOST), or any id on the committed file whose
`Submitting` never returned true (PHANTOM), or ids from more than one writer interleaved on the file
(MERGED).

```
$ "$EXE" "$W" A 80 "$RUN/GO" "$RUN/A.json" &   # ... B and C likewise
$ sleep 2; touch "$RUN/GO"; wait
A: submitted=0  refused=80
B: submitted=80 refused=0
C: submitted=0  refused=80

A: pid=12117 submitted=0  refused=80 token=session:da327ca2,records:57,prior:56,io:failed
B: pid=12118 submitted=80 refused=0  token=session:4d364897,records:80,prior:0,io:ok
C: pid=12119 submitted=0  refused=80 token=session:ad8fd1ae,records:52,prior:52,io:failed

generation      : 160
records on file : 80
acknowledged    : 80
claimed TRUE    : 80
claimed FALSE   : 160
LOST  (true but not on file): []
PHANTOM (on file but never claimed true): []
MERGED? distinct tag prefixes on file: ['B']
```

**Exactly one owner wrote. 80 durable / 0 lost / 0 phantom / no merge, across 240 concurrent claims
from three real processes.** All 80 of the owner's claims are also acknowledged (`broker_order_id`
present on every record), so `Identified` was not silently dropped for the owner either. No `.tmp`
file was left behind and no rival's identifier appears on the file.

**How the two rivals were refused — and this differs from the brief's expected wording.** The
refusal that actually fires 158 times out of 160 is the **compare-and-swap miss**, not the lock:

```
=== distinct sidecar message shapes ===
 158 ERROR the witness file changed underneath this writer, so something else is writing it.
   2 ERROR another writer owns this witness (…/coid-witness.json.lock): IOException
```

The lock (`Own`, `CoidWitness.cs:1256`) is taken and released **per call**, not held for the life of
the process, so a rival does usually get the lock — it is then refused by the CAS in `Save`
(`CoidWitness.cs:1186-1195`). The brief's expected `Trouble` string ("another writer owns this
witness") is therefore the *minority* refusal path in a genuine three-process race; the majority path
is `"the witness file changed underneath this writer"`. Both are refusals, both return `Submitting`
false, both are safety events in the sidecar. **The acceptance is met; the wording in the record and
in the brief describes only one of the two mechanisms that deliver it.** Recorded as LOW (F6).

**Refusal is permanent for the rival's process, and that is measured here rather than assumed.**
`_committedHash` is set once in `EnsureLoaded` and thereafter only by a replace that landed
(`Committed`, `CoidWitness.cs:1350-1351`). A rival that misses the CAS once never re-syncs, so it is
refused for every subsequent order for the life of the process — A and C were refused 80/80 each,
not 1 each. That is fail-closed and is the consequence the manager ratified, but see F2: the
`Committed` doc block still claims a re-sync that the code does not perform.

---

## Target 2 — PROTOCOL 3 (item 2)

The suite's only wire-level version test uses `Versions.BridgeProtocolVersion + 1` — a NEWER peer.
Nothing exercised the literal **2** that the DLL deployed in `%APPDATA%\ATAS\Strategies` actually
answers, which is the case the bump exists for. Probe added:
`tests/TradeAgent.IntegrationTests/ProtocolThreeVerifyR4Probes.cs` (commit `b5295ae` on
`u14-verify-r4-probes`) — a REAL named pipe, the REAL authenticating `StubBridge`, three tests.

**Refutation executed** (the probes PASS only if the refusal/acceptance/plumbing all hold):

```
$ dotnet test tests/TradeAgent.IntegrationTests/TradeAgent.IntegrationTests.csproj \
      --filter "FullyQualifiedName~ProtocolThreeVerifyR4Probes"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 50 ms - TradeAgent.IntegrationTests.dll (net10.0)
```

- **v2 refused.** `connector.Incompatible.ReportedProtocolVersion == 2`,
  `ExpectedProtocolVersion == 3`, `connector.Bridge` null, and all four capabilities it asserted
  (`SupportsClientOrderId`, `SupportsOrderHistory`, `ReconciliationProvable`, `IsConnectedAsync`)
  come back false. The established mechanism (`AtasConnector.cs:298-306`) is what refuses it.
- **v3 accepted.** `Versions.BridgeProtocolVersion == 3`, `Incompatible` null,
  `connector.Bridge.BridgeProtocolVersion == 3`, capabilities through.
- **`witness_failure` reaches DEGRADED naming the file** — taken off the hello the connector actually
  received rather than a hand-built one: `AtasHealth.BridgeRow(...) == (DEGRADED, "connected, but
  orders are being refused: …coid-witness.json…")`.

**Mutant MV8** — `Versions.BridgeCompatible` changed from `== BridgeProtocolVersion` to `>= 2`:

```
TradeAgent.Tests.Unit.AtasHealthTests.A_bridge_speaking_the_previous_protocol_is_refused_rather_than_believed [FAIL]
Failed!  - Failed: 1, Passed: 110, Total: 111 - TradeAgent.UnitTests.dll
TradeAgent.Tests.Integration.ProtocolThreeVerifyR4Probes.A_version_two_bridge_is_refused_and_nothing_it_claims_gets_through [FAIL]
```

The gate bites at both levels. **Target 2 holds.**

One test-fixture inconsistency, LOW (F7): `AtasHealthTests.cs:125`
(`A_bridge_that_cannot_write_its_write_ahead_record_is_not_reported_as_ready`) builds its hello with
`BridgeProtocolVersion = 2` — a hello that, per its own sibling test three cases down, can never
reach `BridgeRow` at all, because `_hello` stays null for a v2 peer. `BridgeRow` is pure so the
assertion is still valid; the fixture contradicts the rule the unit just established.

---

## Target 5 — THE PROBE TREATS RESOLVED AS HISTORICAL (item 5)

**The probe's witness block cannot be reached on this machine, and has no test at all.** Verified by
running the real probe binary against a prepared `TRADEAGENT_HOME`:

```
$ TRADEAGENT_HOME=$H dotnet ./tools/probe/bin/Release/net10.0/probe.dll atas --coid-restart-check --wait-anyway --wait 1
exit=1
-- THE BRIDGE HANDSHAKE --------------------------------------------------------
BRIDGE PIPE           : NO ANSWER within 1s
$ grep -c "WITNESS FAILURES" probe-out.txt
0
```

The block that carries the whole of item 5 (`tools/probe/Program.cs:1056-1085`) sits behind a live
bridge-pipe connection, so it never executes off Windows. `grep -rn "historical|SHOULD NOT EXIST|
WITNESS FAILURES" tests/` → **no hits**; no test project references `tools/probe`. Mutant **MV7**
(`var unresolved = witness.Trouble is not null;` → `var unresolved = true;`) leaves the whole
81-test class green — there is nothing to bite. Recorded as F4 (MED) and under NOT verified.

**The half that IS reachable — the decision the probe delegates — was measured, both directions**,
with a real separate reader process (`inspect`, a `ProjectReference` build against the worktree's
`TradeAgent.AtasBridge`):

| sidecar tail | `Trouble` | `Token()` | what the probe would print |
|---|---|---|---|
| last line = `RESOLVED coid-witness committed cleanly after the failures above.` | `<null>` | `records:1,prior:1,io:ok` | "— historical." |
| last line = an `ERROR …` line | "an earlier run could not write the write-ahead record; the account of it is in …errors.log" | `…,io:degraded` | "— UNRESOLVED." |
| `RESOLVED` **followed by** a later `ERROR` | non-null | `io:degraded` | "— UNRESOLVED." |

The third row is the one worth having: the test is the LAST line, not the mere presence of a
`RESOLVED` anywhere, so a gap that reopened after a resolution is not masked by the old marker.
**The decision is correct; the rendering of it is unverified.**

---

## Target 3 — THE SIDECAR NEVER DROPS A SAFETY EVENT (item 3)

**Direction 1, safety events unrationed — held, at process scale.** The three-process run of target 1
produced 160 refusals; the sidecar holds **160 lines**, five times the 32-line quota:

```
$ wc -l run-t1/bridge/coid-witness.errors.log
     160
 158 ERROR the witness file changed underneath this writer, so something else is writing it.
   2 ERROR another writer owns this witness (…/coid-witness.json.lock): IOException
```

**Direction 1 under rotation — held.** Probe `A_safety_event_is_still_written_after_the_sidecar_has_rotated`
drives 400 genuinely refused rewrites through the real `AppendToErrorLog`, past `MaxErrorLogBytes`:
`coid-witness.errors.log.1` exists and is non-empty, the sidecar still contains the LAST claim's
failure (`TA-FAIL-0399`), and more than 32 `did not land` lines survive the roll. **PASSES.**

**Direction 2, warnings rationed — held.** 40 stale foreign temps in one session produce exactly
**32** `ignored …` lines. `Assert.Equal(32, lines.Count(l => l.Contains("ignored ")))` passes.

**But the RESOLVED marker is rationed with the warnings, and the marker is what ENDS a degradation.**
`Settled()` writes it with `safety: false` (`src/TradeAgent.AtasBridge/CoidWitness.cs:1398`), so a
session that has already spent its 32 non-safety lines on quarantine warnings cannot write it.
`_degraded` is cleared in memory while the file's last line still reads as an open gap.

Refutation executed — `A_clean_commit_marks_the_sidecar_resolved_even_after_the_quota_is_spent`:

```
Assert.Contains() Failure: Sub-string not found
String:    "2026-09-03T19:57:16.6784410+00:00 ignored"···
Not found: "RESOLVED"
```

**How long it lasts, measured** (`A_witness_that_commits_cleanly_does_not_make_the_next_start_report_a_gap`,
a fresh `CoidWitness` per row = the next bridge start; every claim commits):

```
leftovers=40
  session 1: committed=True nextStartTrouble=DEGRADED token=…,io:degraded
  session 2: committed=True nextStartTrouble=none     token=…,io:ok
  session 3: committed=True nextStartTrouble=none     token=…,io:ok
  session 4: committed=True nextStartTrouble=none     token=…,io:ok
leftovers=100
  session 1: committed=True nextStartTrouble=DEGRADED token=…,io:degraded
  session 2: committed=True nextStartTrouble=DEGRADED token=…,io:degraded
  session 3: committed=True nextStartTrouble=DEGRADED token=…,io:degraded
  session 4: committed=True nextStartTrouble=DEGRADED token=…,io:degraded
```

At 40 leftovers the 64 `.rejected-n` quarantine slots suffice and the mess clears after one wrong
session. At 100 they do not: the surplus is re-rejected every session, the quota is spent every
session, and the marker is never written again — **permanently DEGRADED while every order commits.**
That is the permanent-degradation loop commit `5e5b011` closed, re-entered through the quota. The
`.rejected-n` slots are consumed cumulatively over a machine's whole life and never reclaimed (they
are "kept, not deleted" by design), so the trigger is *64 rejections ever, plus ≥32 candidates that
cannot be quarantined in one session* — not "100 temps at once".

**Consequence, named:** `Trouble` non-null → `AtasStrategyAdapter.cs:570` puts it on the hello →
`AtasHealth.BridgeRow` reads DEGRADED "orders are being refused" while every order goes through, and
`AtasStrategyAdapter.cs:589` computes `SupportsClientOrderId = proof.ProvesRoundTrip() &&
_witness.Trouble is null` → **false**, which is what the gateway consults before permitting
LIVE_AUTONOMOUS. Fail-closed, so no order reaches the wire unrecorded — but it is the row crying
wolf, which `Settled()`'s own comment names as the reason the clearing exists.

**Diagnostic mutant MV9 (a diagnosis, not a fix — restored immediately after).** Writing the marker
with `safety: true` and re-running the same probes:

```
leftovers=40 : sessions 1-4 nextStartTrouble=none
leftovers=100: sessions 1-4 nextStartTrouble=none
A_clean_commit_marks_the_sidecar_resolved_even_after_the_quota_is_spent — PASSES
```

Eight of eight measured sessions flip. **Finding F1 (MED)**, fix expectation below.

---

## Target 4 — READERS NEVER WRITE (item 4)

Measured with **real separate processes** — an `inspect` build (`ProjectReference` to the worktree's
`TradeAgent.AtasBridge`) that reports `Trouble`/`Token`/`All`/`PriorSessionIds` and diffs the
directory before and after, and a `holder` build that takes the witness lock exactly the way `Own()`
does and holds it.

**Direction 1 — a reader WHILE THE OWNER HOLDS THE LOCK changes nothing. HELD.**

```
$ holder …/coid-witness.json.lock 6 &      # the owner
$ inspect …/coid-witness.json              # the concurrent reader
Trouble        : <null>
Token          : session:bd914b8a,records:1,prior:1,io:ok
All().Count    : 1
PriorSessionIds: [TA-PRIOR-1]
DISK added     : []
DISK removed   : []
--- sidecar? --- (no sidecar — the reader wrote nothing)
```

The stale candidate is still `coid-witness.json.tmp-999-deadbeef-1` afterwards: not adopted, not
quarantined, no sidecar line, and the reader still answered correctly. This is the property that
matters and it holds.

**Direction 2 — a reader that runs when NOBODY holds the lock adopts, quarantines and writes.**

```
--- before ---           coid-witness.json, coid-witness.json.tmp-999-deadbeef-1
=== A READ-ONLY PROCESS over that directory (no writer running) ===
DISK added     : [coid-witness.errors.log, coid-witness.json.lock, coid-witness.json.rejected-1]
DISK removed   : [coid-witness.json.tmp-999-deadbeef-1]
--- sidecar written by the READER? ---
… ignored …coid-witness.json.tmp-999-deadbeef-1: it does not descend from the committed file
   (temp generation=3 predecessor=0000000000000000; committed generation=7 fingerprint=b17b70dd7cbfd8fd)
   — moved to coid-witness.json.rejected-1
```

That is by the code's design (`EnsureRecovered`, `CoidWitness.cs:930-936`: take the lock, and if you
got it you ARE the owner), and the commit message says so. It is **not** what the brief's target
sentence says ("a concurrent reader cannot adopt, quarantine or mark a good rewrite unresolved") —
the qualifier "concurrent" is load-bearing and the record does not carry it.

**The consequence was measured rather than argued.** After the diagnostic reader ran once, a fresh
process over the same directory reports:

```
Trouble : an earlier run could not write the write-ahead record; the account of it is in …coid-witness.errors.log
Token   : session:99d87f47,records:1,prior:1,io:degraded
```

So running `tools/probe` — the diagnostic the operator is told to run when something looks wrong, on
a machine where the bridge is NOT running, which is exactly when they would run it — can leave the
app reporting DEGRADED and `SupportsClientOrderId = false` until the next successful bridge write.
And the sentence it produces misdescribes the event: nothing "could not write the write-ahead
record"; a foreign leftover was quarantined. **Finding F3 (MED).**

**Mutants.** MV5 (`EnsureRecovered` acts without owning) → RED,
`A_reader_that_does_not_own_the_witness_changes_nothing_on_disk [FAIL]`, 1 failed / 80 passed.
MV6 (writer trusts its flag instead of re-reading the sidecar before RESOLVED) → RED,
`A_resolved_marker_is_not_appended_over_one_that_is_already_there [FAIL]`, 1 failed / 80 passed.
Both target-4 guards bite.

---

## Target 6 — ITEM 6, ALL FOUR HALVES (reproduced, not trusted)

**6a — superset by MEMBERSHIP.** M1/M1b/M1c reproduced below; the swap-one-under-the-cap case is
refused and the legitimate at-cap trim is still adopted. Both directions bite.
**6b — the `Identified` asymmetry.** M2 reproduced: 79 of 80 pass under it and the one failure is
the new pin, so M2 survives every pre-existing test in the class — which is what the pin was for.
**6c — "refused without the lock" on both paths.** M3a/M3b/M3c reproduced. M3b fails ONLY the new
test (1 failed / 79 passed): the acknowledgement-lock path had no coverage before item 3. See F2 for
the half of this guard that still has no biting test.
**6d — an anchorless candidate reads as a flagged zero.** M4a/M4b reproduced, and verified
independently with a real reader process over a directory holding NO committed file and one temp
carrying another machine's acknowledged history:

```
Trouble        : <null>
Token          : session:345a4548,records:0,prior:0,io:degraded
Unreadable     : False
All().Count    : 0
PriorSessionIds: []
DISK removed   : [coid-witness.json.tmp-777-cafebabe-1]   (quarantined, not adopted)
```

`records:0` AND `io:degraded` AND `Unreadable == false` together — the zero is a fact about the disk
and the reader is told something was refused. `TA-IMPORTED` reached neither `All()` nor
`PriorSessionIds()`. The import route is closed.

---

## The extra target — the missing-prefix allowance reads THIS instance's `_cap`

**The record names the wrong direction.** `U14.md` says *"a temp written by a build with a LARGER cap
would now be refused where `a8b3fb0` adopted it."* Measured, both directions, with a legitimate
at-cap rewrite (`An_at_cap_rewrite_is_adopted_whatever_cap_the_reading_build_has`):

| writer's cap | reader's cap | outcome |
|---|---|---|
| 5 | 3 | **adopted** (test passes) |
| 3 | 5 | **REFUSED** — `the legitimate at-cap rewrite was NOT adopted; reader sees [TA-1, TA-2, TA-3]` |

The guard is `if (i > 0 && candidate.Records.Count < _cap)` (`CoidWitness.cs:975`). The candidate
holds the WRITER's cap; the comparison is against the READER's. So it is a temp from a
**smaller**-capped build that is refused — i.e. the affected upgrade is a cap **RAISE** (512 → 1024),
not a lower. The record's "safe direction" argument was made about a case that cannot occur.

**No test pins it.** Every cap-using test sets writer and reader to the same cap
(`CoidWitnessTests.cs:716/732`, `865/879`, `1826/1833`). No production caller passes a cap at all —
`AtasStrategyAdapter` uses `new CoidWitness()` and `tools/probe/Program.cs:1043` passes only a path,
so both are `DefaultCap`. The cross-cap case can therefore arise ONLY across builds, which is exactly
the upgrade scenario.

**Is the direction safe in every restart/downgrade scenario?** Walked through:

- *Same build restart* (the overwhelming majority): caps equal, unaffected.
- *Cap LOWERED on upgrade*: adopted, then `Trim` drops the surplus on the next commit — the cap doing
  its documented job, not a new loss.
- *Cap RAISED on upgrade*: the one legitimate uncommitted rewrite the old build left is refused,
  once, on the first start of the new build. What it holds is either a claim whose `Submitting`
  returned false — so `Place` refused the order and nothing was sent, nothing is lost — or an
  `Identified` broker id for an order that IS live, which item 2's asymmetry deliberately leaves in
  the temp for a later session. In that second case the broker id is not recovered, so `PriorSession`
  answers null and the read-back **refuses a proof rather than inventing one**.
- *Rollback to the old build*: reader's cap is smaller, adopted.

**Verdict on the direction: it is the safe one** — fail-closed, same bound `Trim` already documents
("a very old identifier stops being provable"). Two things are NOT costless and are not in the
record: a cap raise costs one un-recovered acknowledgement per stranded temp, and the refusal also
calls `Note` → `_degraded` → `Trouble` non-null → `SupportsClientOrderId = false` for that start.
**Finding F5 (MED)**: unpinned, and recorded backwards.

---

## Mutants

Source restored from a `cp` copy every time (sha `0ad2cd11f89841ae24c5bd5b16895a49e3a99778` —
identical to the builder's copy), then `touch`ed, never `git checkout --`; `git status --porcelain`
confirmed empty after each. Filter `FullyQualifiedName~CoidWitnessTests` (80 tests) for M1–M4b,
widened to include my probes for MV*.

### The builder's nine, reproduced

| # | Mutant | Result | Matches the builder's table |
|---|---|---|---|
| M1 | membership body → `return null` (count check alone) | RED 1/79 — `A_candidate_that_swapped_a_committed_claim_for_another_is_ignored` | yes |
| M1b | cap condition made unreachable (the `a8b3fb0` prefix rule) | RED 1/79 — same test | yes |
| M1c | trim allowance removed (`if (i > 0)`) — too strict | RED 1/79 — `A_rewrite_that_trimmed_the_oldest_at_the_cap_is_still_adopted` | yes |
| M2 | `Identified` given `Submitting`'s snapshot/restore | RED 1/79 — `A_refused_acknowledgement_is_kept_where_a_refused_claim_is_taken_back` | yes |
| M3a | `Submitting`'s lock refusal deleted | RED 2/78 — `…refused_without_the_lock`, `A_second_writer_is_refused_rather_than_merged_with` | yes |
| M3b | the same deletion in `Identified` | RED **1/79 — only the new test** | yes |
| M3c | compare-and-swap disabled | RED 2/78 — `A_witness_file_changed_by_something_else_is_refused_not_merged`, `…changed_underneath` | yes |
| M4a | `DescendsFrom` anchorless → the round-2 shape test | RED 2/78 — `With_nothing_committed_only_a_first_rewrite_is_adopted`, `A_sidecar_left_by_an_earlier_run_makes_the_token_say_so` | yes |
| M4b | `DescendsFrom` anchorless → `return true` | RED 2/78 — same two | yes |

**All nine reproduce exactly** — same failing test names, same counts (class size 80 rather than the
builder's 77/78 at the time). The builder's table is honest.

### Mine, on the guards the builder's table does not cover

| # | Mutant | Result |
|---|---|---|
| MV1 | the post-rename read-back accepts anybody's bytes | RED 1/80 — `Submitting_is_false_when_the_claim_is_not_in_what_got_committed` |
| **MV2** | **`Own()` `FileShare.None` → `FileShare.ReadWrite` — the lock stops excluding anybody** | **SURVIVED: 80/80 green.** See F2 |
| MV3 | the safety bypass removed — the quota rations failures again | RED 2/79 — `A_write_failure_is_never_dropped_by_the_sidecar_quota`, `A_safety_event_after_the_quota_and_after_a_resolved_marker_is_still_recorded` |
| MV4 | rotation replaced by deletion (pre-round-4) | RED 1/80 — `An_oversized_sidecar_is_rotated_rather_than_thrown_away` |
| MV5 | `EnsureRecovered` acts without owning the witness | RED 1/80 — `A_reader_that_does_not_own_the_witness_changes_nothing_on_disk` |
| MV6 | writer trusts its flag instead of re-reading before RESOLVED | RED 1/80 — `A_resolved_marker_is_not_appended_over_one_that_is_already_there` |
| **MV7** | **`tools/probe`'s `unresolved = witness.Trouble is not null` → `true`** | **SURVIVED: 81/81 green.** See F4 |
| MV8 | `Versions.BridgeCompatible` `==` → `>= 2` | RED — `A_bridge_speaking_the_previous_protocol_is_refused_rather_than_believed` (unit) AND my `A_version_two_bridge_is_refused…` (wire) |
| MV9 | *diagnostic, not a fix*: RESOLVED marker written with `safety: true` | flips 8/8 measured sessions from DEGRADED to none; confirms F1's cause exactly |

**MV2 is the one that matters.** Under it the whole 80-test class stays green while a real
three-process run still shows 0 lost / 0 phantom — the outcome is unchanged by luck, not by design.
The deterministic interleaving probe shows the removal IS record-losing:

```
TradeAgent.Tests.Integration.CoidWitnessVerifyR4Probes.The_lock_is_what_stops_a_claim_reported_durable_from_being_dropped [FAIL]
   Assert.Contains() Failure: Item not found in collection
Collection: ["TA-SEED", "TA-B"]
Failed!  - Failed: 1, Passed: 80, Skipped: 0, Total: 81
```

`TA-A`'s `Submitting` returned **true** — so `Place` would have sent that order — and the committed
file does not contain it.

---

## The CLASS (§9.10)

**F1 and F3 share one root cause, and F4 sits on top of it: the sidecar conflates "a line was written
here" with "a durability gap is open".** `Note()` (`CoidWitness.cs:1416-1420`) sets `_degraded = true`
for EVERY line it writes — a quarantine warning, a rival-candidate warning and a write-ahead failure
alike — and `_degraded` is the sole input to `Trouble`'s third branch (`CoidWitness.cs:673-676`),
which is what drops `SupportsClientOrderId` to false. Downstream, a warning and a lost claim are
indistinguishable. Every consequence follows from that one conflation:

- a **reader** that quarantines a foreign leftover opens a "durability gap" (F3);
- the **marker that closes it** is classed as a warning and can be rationed away (F1);
- and the **probe** has to ask `Trouble` rather than read the file, because the file cannot be read
  for this (F4) — which is correct, and is why the probe's own rendering went untested.

**Structural fix:** make the degraded state a function of unresolved **safety** lines rather than of
any line — tag safety lines on the wire (e.g. the existing `ERROR ` prefix is already there and the
quarantine notes already lack it), have `EnsureLoaded` test the last SAFETY line against the marker,
and `Note(safety: false)` stop setting `_degraded`. That closes F1 and F3 together and makes F4's
rendering testable as a pure function of the sidecar tail. Fixing the three instances separately
would leave the conflation in place.

---

## Findings

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **F1** | MED | The RESOLVED marker is written as a non-safety line, so the warning quota can ration away the very line that ends a degradation. A witness that committed cleanly does not say so; the next process reports DEGRADED and `SupportsClientOrderId = false` over a healthy witness. One session when the 64 `.rejected-n` slots suffice; **permanent** once ≥32 candidates cannot be quarantined (slots are cumulative over a machine's life and never reclaimed). Measured 4 sessions × 2 scenarios. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1398` | Write the marker as a safety event (`safety: true`), or adopt the CLASS fix. It is a state transition, at most once per session, and already guarded against duplication by the `LastSidecarLine()` re-read at `:1396`, so it cannot flood. Verified: 8/8 measured sessions flip to `none`. |
| **F2** | MED | The lock's OWN exclusion has no biting test. Both lock tests hold the lock file *from the test* with `FileShare.None`, so they pin "refused when it cannot open the lock", never "its own open excludes a second witness". MV2 leaves all 80 green — and the removal is record-losing: a claim `Submitting` reported durable is absent from the committed file. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1262`; tests `CoidWitnessTests.cs:1604`, `:1636` | Add the interleaving assertion to `CoidWitnessTests`: two witnesses over one path, the second's whole `Submitting` driven from inside the first's `_replace`, asserting every claim reported durable is on the committed file. Ready to lift from `tests/TradeAgent.IntegrationTests/CoidWitnessVerifyR4Probes.cs` (commit `f65e2dc`). |
| **F3** | MED | A read-only process that runs while no writer holds the lock adopts, quarantines and writes the sidecar — measured: it created `coid-witness.errors.log`, created the lock file, and renamed a foreign temp to `.rejected-1`. A fresh process then reports `Trouble` non-null and `io:degraded`. `tools/probe` is the diagnostic the operator runs when the bridge is NOT running, which is precisely when it becomes the owner. The sentence produced ("an earlier run could not write the write-ahead record") misdescribes a quarantine. | `src/TradeAgent.AtasBridge/CoidWitness.cs:930-936`, `:673-676` | Adopt the CLASS fix, or give the read paths a classify-only mode and let `Trouble` distinguish a quarantine warning from a write failure. Also: the record's target sentence needs the word "concurrent" — the code's contract is "acted on only under the lock", which DOES hold. |
| **F4** | MED | The whole of item 5's rendering has no test and cannot execute off Windows — it sits behind a live bridge-pipe connection (measured: exit=1, the block never printed). MV7 leaves 81/81 green. No test project references `tools/probe`. | `tools/probe/Program.cs:1056-1085` | Extract the decision + the two wordings into a pure function in a tested assembly and assert both renderings, or add a test that builds a `CoidWitness` over a prepared directory and asserts the exact line the probe would print. |
| **F5** | MED | The cross-cap adoption direction is unpinned, and `U14.md` records it backwards. Measured: writerCap 3 / readerCap 5 → **REFUSED**; writerCap 5 / readerCap 3 → adopted. So a temp from a **smaller**-capped build is refused — the affected upgrade is a cap RAISE, not a lower. Every cap-using test sets both caps equal. The direction is safe (refuses a proof, never invents one), but a raise costs one un-recovered `Identified` per stranded temp and one DEGRADED session. | `src/TradeAgent.AtasBridge/CoidWitness.cs:975`; `U14.md` "What I did NOT do" | Correct the direction in the record, and add the two-row theory (writer cap ≠ reader cap, both ways). Ready to lift from `CoidWitnessVerifyR4Probes.cs` (commit `681eb55`). |
| **F6** | LOW | The refusal wording. In a real three-process race the CAS refusal fires 158/160 and the lock refusal 2/160, because `Own()` is taken and released per call rather than held for the process. The record's and the brief's expected `Trouble` ("another writer owns this witness") is the minority path. | `docs/hardening/records/U14.md`, round-4 item 1 paragraph | Name both refusal sentences in the record; a CAS miss is the ordinary way a rival is turned away. |
| **F7** | LOW | `Committed()`'s doc block still says the "SOMEBODY ELSE'S" branch re-syncs the lineage "so the next rewrite descends from THAT". The code does not re-sync — it calls `NotOurs` and returns false, leaving `_committedHash` stale, which is why a CAS-refused rival stays refused for the life of the process (measured: 80/80 refusals each for two rivals). The comment is round 3's; round 4 changed the behaviour. | `src/TradeAgent.AtasBridge/CoidWitness.cs:1310-1313` vs `:1341-1348` | Replace the re-sync sentence with what round 4 actually does: refuse, do not negotiate, and stay refused. |
| **F8** | LOW | `A_bridge_that_cannot_write_its_write_ahead_record_is_not_reported_as_ready` builds its hello with `BridgeProtocolVersion = 2` — a hello that, per its own sibling test, can never reach `BridgeRow`, because `_hello` stays null for a v2 peer. `BridgeRow` is pure so the assertion is valid; the fixture contradicts the rule the unit establishes. | `tests/TradeAgent.UnitTests/AtasHealthTests.cs:125` | Use `Versions.BridgeProtocolVersion`. |
| **F9** | LOW | `Trouble` runs `EnsureLoaded()` but not `EnsureRecovered()`, while `Token()`/`All()`/`PriorSessionIds()` do, so within one instance they can disagree — measured `Trouble: <null>` beside `Token: …io:degraded` in one process. The one production caller is safe by ordering: `Describe()` runs `Guard(SweepWitness)` → `PriorSessionIds` before reading `Trouble`. The class does not enforce it. | `src/TradeAgent.AtasBridge/CoidWitness.cs:660-680`; `AtasStrategyAdapter.cs:551, 570` | Either have `Trouble` run `EnsureRecovered()` like its siblings, or say in its doc that it reports the load-time state and depends on a read path having run. |

**0 HIGH / 5 MED / 4 LOW.**

No HIGH: across every probe run — three real processes × 240 concurrent claims, 400 refused rewrites,
the deterministic two-writer interleaving, the anchorless import, the cross-cap cases — **no order
could reach the wire without a durable witness record, and no record was lost or phantomed** on the
sha as it stands. Every failure mode found is fail-closed.

---

## NOT verified

- **Windows sharing violations.** Every replace failure in this leg is injected at the `_replace`
  seam. `rename(2)` on APFS does not consult open handles, so the real `MoveFileEx` refusal cannot be
  provoked here. The box is offline.
- **The two `AtasStrategyAdapter.cs` hunks** (`Place`'s refusal above `lock (_gate)`;
  `Describe()`'s `WitnessFailure` / `SupportsClientOrderId`). `<Compile Remove>`d off Windows. I read
  them and traced the values they publish, and I drove `witness_failure` end-to-end through the REAL
  pipe with a stand-in bridge — but **that file was not compiled by anything in this leg**. The next
  release build on the box is still its first compiler.
- **The churn test's load-dependent `.tmp` assertion** on Windows. Not exercisable here.
- **`tools/probe`'s witness/sidecar rendering** (F4) — measured to be unreachable off Windows, not
  merely unexercised. What the operator actually SEES for a resolved gap is unverified by anything.
- **`proto=3` as a reading taken from the box.** The deployed DLL still answers `proto=2` as far as
  anything here knows; `docs/RESUME-HERE.md`'s claim is about what this tree expects.
- **The cross-process lock on anything but APFS.** `FileShare.None` is enforced by .NET's own
  locking; its Windows behaviour is a different mechanism and was not exercised.
- **F1's permanent case in production numbers.** I measured it at 100 leftovers with a cap of 64
  quarantine slots. I did NOT measure how long a real machine takes to accumulate 64 rejections.

## What I did NOT do

- **I fixed nothing.** `git diff e22eec6 -- src tools` is empty; `CoidWitness.cs` hashes
  `0ad2cd11f89841ae24c5bd5b16895a49e3a99778`, identical to the builder's `cp` copy. Every mutant was
  restored from a `cp` copy and `touch`ed, never with `git checkout --`, and `git status --porcelain`
  was confirmed empty after each.
- **I did not push, merge or rebase anything.** Four probe commits sit on `u14-verify-r4-probes`
  (`f65e2dc`, `b5295ae`, `3424132`, `681eb55`), all in `tests/`, none touching `src/` or `tools/`.
  Four of the probe assertions are RED by design: they ARE findings F1, F2 (under MV2) and F5.
- **I did not re-run the round-3 durability numbers** (238 durable / 0 lost / 0 phantom). My harness
  is a rebuild from the record's description, not the original; my figures are 80 durable / 0 lost /
  0 phantom out of 240 claims, which is the round-4 refusal semantics rather than round 3's merge.
- **I did not test the gateway, the updater, the App UI or the agent pipe.** Out of scope for a
  targeted round; the builder's sweep shows the diff does not touch them and I confirmed that
  (`git diff --name-only a8b3fb0..e22eec6` → 4 files, none of them those).
- **I did not stress the MV2 window beyond one deterministic interleaving plus one 240-claim race.**
  The probe proves the loss is reachable; I did not measure how often it happens under real timing.
- **I did not exercise `Quarantine`'s 64-slot exhaustion against a real long-lived machine**, only
  against a synthetic directory.
- **Full suite run twice, no more.** Run 1 (baseline at `e22eec6`) and run 2 (after every mutant was
  restored, probes filtered out) — both 387 green (75/111/201), 0 failed, 0 skipped. Everything else
  was a targeted filter.

```
$ dotnet test TradeAgent.sln --filter "FullyQualifiedName!~VerifyR4Probes"     # run 2 of 2
EXIT=0
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 201, Skipped: 0, Total: 201 - TradeAgent.IntegrationTests.dll (net10.0)
```

The tree is left exactly as it was found.

VERDICT: FAIL — 0H/5M/4L
