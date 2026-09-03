# U14 — ADVERSARIAL-VERIFY RECORD · round 6 (leg [2], Opus, same verifier as rounds 4–5, §9.3)

**Sha under test:** `f8a724cb27680a642d17e50eeac6d951db8d82e5` = `6a40fa7` + 13 commits.
Worktree `…-worktrees/u14-verify-r6`, branch `u14-verify-r6-probes`. Toolchain
`PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"`, .NET 10, macOS/APFS.
**The box is NOT mine this round** (serialised; the U2a builder holds the grant), and **every on-box
suite figure from rounds 5 and 6 is withdrawn** — so Windows is treated as entirely NOT verified,
including the builder's compile and the `tools/atas-gate` run, which I read and did not repeat.

## Target 6 — TEST-COUNT INTEGRITY (done first, because the rest rests on it)

**The two silently deleted-and-restored tests are present at `f8a724c`**, confirmed by name before
anything else was run:

| test | restored by | present in |
|---|---|---|
| `An_acknowledgement_for_an_identifier_this_witness_does_not_have_takes_no_lease` | `e7f75f7` | `CoidWitnessTests.cs` |
| `No_safety_event_is_lost_when_several_writers_produce_one_at_once` | `f8a724c` | `CoidWitnessTests.cs` |

`git show f8a724c --stat` → one file, +53 insertions, and the added method is the R3 test. `git show
e7f75f7` adds the F21 test. Neither is a stub: both carry bodies with assertions.

**432 green, reproduced** (full-suite run 1 of 2, `scratchpad/r6-suite-run1.txt`):

```
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75, Duration: 872 ms  - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111, Duration: 3 s     - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 246, Skipped: 0, Total: 246, Duration: 1 m 55 s - TradeAgent.IntegrationTests.dll (net10.0)
```

`dotnet build TradeAgent.sln` → `Build succeeded. 0 Warning(s) 0 Error(s)`, tree clean.
The second run is at the end of this record. **The builder's Mac figure is honest.** The integration
project now takes 1 m 55 s rather than round 5's 20 s — the concurrency tests are real work, not
skipped.

---

## Target 2 — THE REFUSED-PEER CLASS, AND THE ACCEPTED DEVIATION

### 2a. The class as implemented — holds

All five tests the builder names are present in `BridgeRoundTripTests.cs`
(`A_refused_bridge_cannot_set_capabilities_through_a_heartbeat`,
`A_refused_bridge_cannot_clear_its_refusal_with_a_later_hello`,
`An_authenticated_peer_raises_no_events_before_a_compatible_hello`,
`An_authenticated_peer_sets_no_capabilities_by_heartbeat_before_any_hello`,
`A_fresh_connection_from_a_compatible_bridge_is_still_accepted`) and green in the 246.
`_refused` is tested at the top of `Dispatch` (`AtasConnector.cs:300`) above every frame type, and
`_compatible` gates both the event branch (`:339`) and the heartbeat branch (`:437`).
**My round-5 R1 is closed** — the route I measured is refused at the connection level, not per branch.

### 2b. The accepted deviation — a parked refused peer blocks the fixed bridge. **FINDING V1 (HIGH).**

The pipe is created with **`maxNumberOfServerInstances = 1`** on both paths
(`AtasConnector.cs:220`, `:223`), and `AcceptLoop` creates the next instance only after the inner read
loop ends (`:152-184`). Keeping `return true` for a mismatched peer means that loop never ends while
the peer holds the connection open — so the single instance stays occupied by a peer read by nobody.

**Refutation executed** — `A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe`: a real
authenticated peer sends a v2 hello and then goes silent without disconnecting; the operator then does
exactly what the row instructs and a current `StubBridge` dials in. **3/3 in isolation:**

```
a parked refused peer kept the fixed bridge off the pipe: clientConnected=False helloAccepted=False
  Bridge=null Incompatible=reported=2
  StatusDetail="bridge 0.1.1 speaks protocol 2, this build speaks 3 — reinstall the add-on from TradeAgent"
```

`clientConnected=False` — the fixed bridge's `ConnectAsync(10_000)` **times out entirely**. The row
tells the operator to reinstall the add-on, and doing so does not work while the old peer is parked.

**The control passes, so the probe is sound**:
`A_fixed_bridge_is_accepted_once_the_refused_peer_disconnects` → `Passed! 1/1, Duration: 146 ms`. With
the refused peer gone the fixed bridge is accepted immediately.

**And the alternative the manager weighed is not a fix either — measured, not assumed.** Diagnostic
mutant **MD1** (the mismatch branch `return true` → `return false`, restored immediately):

```
ParkedPeerVerifyR6Probes.A_parked_refused_peer_does_not_keep_a_fixed_bridge_off_the_pipe [FAIL]
ParkedPeerVerifyR6Probes.A_fixed_bridge_is_accepted_once_the_refused_peer_disconnects [FAIL]
BridgeRoundTripTests.When_an_incompatible_bridge_disconnects_the_status_row_is_told [FAIL]
BridgeRoundTripTests.A_refused_bridge_cannot_clear_its_refusal_with_a_later_hello [FAIL]
BridgeRoundTripTests.A_refused_bridge_cannot_set_capabilities_through_a_heartbeat [FAIL]
BridgeRoundTripTests.A_bridge_speaking_the_previous_protocol_raises_no_events_into_the_application [FAIL]
BridgeRoundTripTests.An_incompatible_bridge_names_its_version_and_gains_nothing_by_it [FAIL]
Failed!  - Failed: 7, Passed: 23, Total: 30
```

The builder's "five RED" is confirmed exactly, **and my own probes fail under it too** — because `Drop`
clears `_incompatible` (`:252`), so the version and the repair vanish before the probe can even read
them. So `return false` trades a blocked pipe for a blank row. **Neither branch of the choice as posed
is correct, which is why this is a finding against the code rather than against the decision.**

---

## Target 1 — F17: UNREADABLE IS NOT ABSENT

**One predicate, confirmed by reading it and then by attacking it.** `EnsureLoaded` sets
`_committedUnreadable = unreadable` (`CoidWitness.cs:996`) where `unreadable` covers BOTH the I/O
failure and the parse failure (`:960`), and `Save`'s compare-and-swap read now carries the same rule
(`:1571-1572`) instead of discarding the flag. Absent is exactly `FileNotFoundException` /
`DirectoryNotFoundException` in `ReadTolerantly` (`:1522-1523`).

**Four variants beyond Codex's injected opener — three of them REAL failures, no seam:**

```
$ dotnet test … --filter "FullyQualifiedName~UnreadableVerifyR6Probes"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 221 ms
```

| variant | how it is made | result |
|---|---|---|
| `UnauthorizedAccessException` on the committed path | real `chmod 000`, and the probe fails itself if the platform lets the owner read anyway | Unreadable, write refused, `Trouble` says "could not be read", **bytes byte-identical** |
| a DIRECTORY at the witness path | real `Directory.CreateDirectory(File_)` with a marker inside | Unreadable, write refused, the directory and its marker untouched |
| a read that ends SHORT without throwing | a stream over half the bytes — the I/O layer reports success, only the parse can catch it | Unreadable, write refused, bytes intact |
| a read that throws PART WAY through | a stream that delivers a third of the file and then fails like a bad disk | Unreadable, write refused, bytes intact |

**The split attempt — three mutants, none of them gets past.**

| # | mutant | result |
|---|---|---|
| MF17a | the load predicate split again (parse failure only) | RED 1/123 — `A_committed_file_that_cannot_be_read_is_not_treated_as_absent(from: 0, to: 4, "the load's four reads")` |
| MF17b | the compare-and-swap discards the failure flag again | RED 1/123 — the same Theory's row `(1, 5, "the compare-and-swap's four reads")` |
| **MVA (mine)** | the most tempting wrong simplification: a DENIED read counted as absent (`catch (UnauthorizedAccessException) { failed = false; }`) | RED 4/123 — two of the builder's (`A_real_unopenable_candidate_reaches_the_same_answer`, `A_candidate_that_cannot_be_opened_is_a_failed_read_not_an_empty_one`) and two of mine |

Each of the two guards is separately load-bearing — the builder's Theory rows distinguish WHICH one
fired, which my probes do not, and that is the sharper instrument. **Target 1 holds; I could not split
the predicate.**

---

## Target 3 — F18/F19 ROTATION

**Both directions hold, and both guards bite.**

| # | mutant | result |
|---|---|---|
| MF18 | the deciding line read from the current log only (`SidecarGenerations().Take(1)`) | RED — `An_unresolved_gap_is_not_lost_when_the_sidecar_rotates` **and** `A_gap_closed_before_the_rotation_stays_closed_after_it` |
| MF19 | standing decided by `Noted` again — i.e. by the file existing | RED — `A_diagnostic_only_sidecar_is_noted_and_not_historical` |

`LastLineWhere` iterates `SidecarGenerations()` (current log, then `.1`) and the last deciding line
wins wherever it lives; `Historical` is `GapClosed` — a RESOLVED marker after the last safety line —
never "no safety lines in this file".

**A scope note, and it is the builder's own (V3, LOW).** I tried to drive a REAL rotation into the F18
shape and could not: `ReportAndQuarantine` is reached only after `Lease()` in both `Submitting`
(`:575-580`) and `Identified` (`:659-...`), and both then run `Save`, which writes either a RESOLVED
marker or an `ERROR ` line into the new log. **No session in this build can write a diagnostic without
also writing a deciding line**, so the state F18 defends against is producible only by a foreign
writer or an older build. The builder constructs it by hand and says so in the test's own doc comment.
The guard is right; nothing pins that a real rotation reaches it, and nothing in this build can.

---

## Target 5 — F21/R2, THE LEASE ON TERMINAL PATHS

The testable half holds. `Dispose_hands_the_witness_over_and_a_stopped_instance_takes_no_lease_to_read`:
a second instance is refused while the first lives, acquires the moment `Dispose()` is called, and —
the F21 half — the stopped instance's order handler firing for a FOREIGN identifier does not take the
lease back; the live writer keeps writing and the committed file reads `["TA-1", "TA-2", "TA-3"]`.
`Identified` now finds the record BEFORE leasing (`:14-17` of the method, lease at `:26`), which is the
fix. Mutant MF21 is the builder's and is recorded RED 1/116.

**NOT verified, by name:** which callback ATAS actually fires on a strategy teardown. `OnStopping` and
`OnDispose` both route to the same idempotent teardown and the adapter compiled on the box — but the
adapter is `<Compile Remove>`d off Windows, the box is not mine this round, and no test in the 432 can
reach `StopBridge`. Two hooks and a compiler, exactly as the builder states.

---

## Target 4 — R3, PER-WRITER SIDECARS

**My round-5 R3 is closed, measured the way I found it.** The `h6` harness (a `ProjectReference`
console driving the real `CoidWitness` in real OS processes), five writers × 40 claims, all held alive
so no lease is released by exit — **exactly 160 refused-writer safety events**:

```
  refusals=160  files=4  lines=160  naming a claim=160  DROPPED=0
    coid-witness.errors.log-33125-601b2076: 40 lines
    coid-witness.errors.log-33126-30843412: 40 lines
    coid-witness.errors.log-33127-fd7ac748: 40 lines
    coid-witness.errors.log-33129-851a4b1c: 40 lines
```

And four writers × 40, five consecutive runs: `DROPPED=0` every time (120 refusals → 120 lines).
**Nothing lost, and every line names its claim** — the second half of R3, which round 5 flagged as
unmeasured, is closed too.

**The support package collects them:** `Doctor.cs:291` globs `*.errors.log*`, which matches
`coid-witness.errors.log-<pid>-<session8>`.

**The degraded state is NOT computed over the whole set — deliberately, and correctly.**
`SidecarGenerations()` (canonical + `.1`) decides `_degraded`/`_gapClosed`; `SidecarSet()` (every
per-writer file) answers only `Noted`. The brief's target-4 wording asks for the whole set; the
implementation's reason is sound and stated at `:1400-1404` — a refused second bridge cost no order,
because the refusal is what stops the order being sent, so it must not mark the machine degraded for
ever. **Verified divergence from the brief's wording, not a finding.**

**But the probe does NOT collect them, and that is finding V2 (MED).** `_noted` is computed from
`SidecarSet()` — which does include the per-writer files — but the whole block is gated on
`if (SidecarGenerations().Any(File.Exists))` (`CoidWitness.cs:936`), i.e. on the CANONICAL sidecar
existing. On a machine where the owner never failed and only REFUSED writers wrote — which is the
normal shape of the misconfiguration R3's fix was built for — the canonical file does not exist, the
guard is false, and `_noted` stays false.

Refutation executed — `A_zero_is_flagged_when_the_only_account_of_a_refusal_is_a_per_writer_sidecar`:

```
records=0 but the report calls the directory clean: Noted=False Standing=Clean ZeroIsProvisional=False
  perWriterFiles=[coid-witness.errors.log-33529-582d5b92] linesInThem=5
```

Five refusals are written down in a file sitting beside the witness, and
`CoidWitnessReport.Standing` returns **`Clean`** with **`ZeroIsProvisional = false`** — so
`tools/probe` prints "WITNESS FAILURES: none recorded" and then reads `records:0` as a confident zero.
That is the "flagged zero" the F12/V4 class exists to prevent, reopened by R3's own fix.

Bounded, and the bound is why this is MED not HIGH: `_degraded` and `Trouble` sit inside the same
guard, so `SupportsClientOrderId` is unaffected and no order can reach the wire unrecorded; and the
zero is not a false statement about submission — the refused orders genuinely were never sent. What is
lost is the operator's only signal that a second bridge is fighting for the witness.

---

## Mutants

Production files restored from `cp` copies every time, `touch`ed, never `git checkout --`; the
production tree confirmed byte-identical after each (`git diff --stat f8a724c -- src tools` empty).
Pristine shas: `CoidWitness.cs 617a0902…`, `CoidWitnessReport.cs bc2d0f25…`,
`AtasConnector.cs 8e6d1637…`, `Doctor.cs ec02b718…`, `probe/Program.cs 8c8417e2…`.

| # | Mutant | Result |
|---|---|---|
| MF17a (builder's) | the load predicate split again — parse failure only | RED 1/123, Theory row `(0, 4, "the load's four reads")` |
| MF17b (builder's) | the compare-and-swap discards the failure flag again | RED 1/123, Theory row `(1, 5, "the compare-and-swap's four reads")` |
| **MVA (mine)** | a DENIED read counted as absent | RED 4/123 — two of the builder's, two of mine |
| MF18 (builder's) | the deciding line read from the current log only | RED — both rotation tests |
| MF19 (builder's) | standing decided by `Noted` again | RED — `A_diagnostic_only_sidecar_is_noted_and_not_historical` |
| **MD1 (mine, *diagnostic only*)** | the mismatch branch `return true` → `return false` | RED 7/30 — the builder's five, **plus both of my parked-peer probes**, because `Drop` clears `_incompatible` before either can read it. Confirms the trade-off exactly as the builder described it, and shows the alternative is not a fix either |

---

## Findings

| # | Sev | Finding | `file:line` | Exact fix expectation |
|---|---|---|---|---|
| **V1** | **HIGH** | A refused peer parked on the pipe keeps a fixed bridge off it. The pipe is created with `maxNumberOfServerInstances = 1` and `AcceptLoop` creates the next instance only after the inner read loop ends, so a mismatched peer that holds the connection open and says nothing occupies the only slot for ever. Measured 3/3 in isolation: the fixed bridge's `ConnectAsync(10_000)` **times out** (`clientConnected=False`) while the row instructs the operator to "reinstall the add-on from TradeAgent". The control — the same run with the refused peer disconnected — passes in 146 ms. Any same-user process can hold the trading path shut this way; an ATAS-hosted v2 DLL self-resolves only because reinstalling it restarts ATAS. | `src/TradeAgent.Connectors.Atas/AtasConnector.cs:220`, `:223` (instance count), `:152-184` (accept loop), `:378` (the accepted `return true`) | Drop the peer AND keep the identity across the drop: make `Drop` preserve `_incompatible` when the disconnection was caused by our own refusal — the code already makes exactly this argument two paragraphs below, at `:257-266`, for `_refused` and `Unauthenticated` ("the refusal is what CAUSES the disconnection — so clearing it here erased the reason"). A mismatch we ourselves refused is the same case. Then `return false` is safe; MD1 shows it is not safe before that. Probe ready at `tests/TradeAgent.IntegrationTests/ParkedPeerVerifyR6Probes.cs` (commit `2f7f2d7`). |
| **V2** | MED | A zero is not flagged when the only account of a refusal is in per-writer sidecars. `_noted` is computed from `SidecarSet()` (which includes them) but the whole block is gated on `SidecarGenerations().Any(File.Exists)` — the CANONICAL log or its `.1`. On the machine R3's fix was built for (owner healthy, only refused writers wrote) the canonical file does not exist, so `Noted=False`, `Standing=Clean`, `ZeroIsProvisional=False`, and the probe prints "none recorded" then reads `records:0` as a confident zero. Measured with five refusals sitting in a per-writer file. Introduced by R3's own fix; it reopens the F12/V4 flagged-zero class. Bounded: `_degraded`/`Trouble` are inside the same guard, so `SupportsClientOrderId` is unaffected and no order reaches the wire unrecorded. | `src/TradeAgent.AtasBridge/CoidWitness.cs:936` | Gate on `SidecarSet().Any(File.Exists)`, or compute `_noted` from `SidecarSet()` unconditionally and keep only the deciding-line work inside the canonical-generations guard. One line. Probe ready (commit `324b6f9`). |
| **V3** | LOW | The state F18 defends against cannot be produced by this build. `ReportAndQuarantine` is reached only after `Lease()` in both `Submitting` and `Identified`, and both then run `Save`, which writes a RESOLVED marker or an `ERROR ` line into the new log — so no session can rotate the sidecar leaving only a diagnostic behind. The builder's test constructs the rotated state by hand and its doc comment says so. The guard is correct and worth keeping (a foreign writer or an older build can still produce it); what is not pinned is that any real rotation reaches it. | `tests/…/CoidWitnessTests.cs` (`An_unresolved_gap_is_not_lost_when_the_sidecar_rotates`), `CoidWitness.cs:575-580`, `:659-…` | Say in the test's doc that the shape is reachable only from outside this build, so a later reader does not treat it as a live path — or drive it from a second process writing the sidecar directly. |

**1 HIGH / 1 MED / 1 LOW.**

**Closed this round:** my round-5 **R1** (the heartbeat route — refusal is now decided once for the
connection, and MR1a/MF20/MR1b are the builder's biting mutants), my round-5 **R3** (0 dropped over
six measured runs, and every line names its claim), and my round-5 **R2**'s testable half (the lease is
released on `Dispose` and a stopped instance's handler cannot take it back).

**Refuted / verified-as-divergence, no finding:** the degraded state deliberately NOT being computed
over the per-writer set (the brief's target-4 wording versus the implementation's stated and sound
reason); and the F17 predicate, which three mutants including my own could not split.

---

## NOT verified, by name

- **Windows: everything.** The box is not mine this round and **every on-box suite figure from rounds
  5 and 6 is withdrawn** by the builder. I did not repeat, and cannot repeat, the bridge compile
  against real ATAS, the `tools/atas-gate` money-path run (PRIOR 5/16), or any Windows suite count.
  The `GATE PASSED` output and the `5 Warning(s) 0 Error(s)` compile are claims I read.
- **V1 on Windows.** Measured on macOS, where .NET emulates named pipes over Unix domain sockets. The
  mechanism — one server instance, occupied — is the same in kind on Windows (a busy instance returns
  `ERROR_PIPE_BUSY`), but I did not measure it there.
- **Which ATAS callback fires on a strategy teardown** (`OnStopping` vs `OnDispose`). Two hooks and a
  compiler; no test in the 432 reaches `StopBridge`, and the adapter is `<Compile Remove>`d off Windows.
- **R4's Windows half** (a file open without `FILE_SHARE_DELETE` cannot be unlinked) — unchanged,
  still reasoned from the API contract by both of us.
- **The F8 residual** (a rename that throws after the replace landed) — still open, still not closable
  from this side of the rename.

## What I did NOT do

- **I fixed nothing.** `git diff f8a724c -- src tools` is empty and the five production shas are
  unchanged. Every mutant was restored from a `cp` copy and `touch`ed, never `git checkout --`.
- **I did not push, merge or rebase.** Two probe commits on `u14-verify-r6-probes` (`2f7f2d7`,
  `324b6f9`), both under `tests/`.
- **I did not touch the box, or ask for it.** Access is serialised and the U2a builder holds the grant.
- **I did not re-run round 5's closed findings beyond the checks above**, nor re-verify the Gateway,
  `AppHost.cs`, the updater or the App UI, which the builder's sweep shows the diff does not touch.
- **I did not measure V1's blast radius under a real ATAS-hosted v2 DLL**, only with a synthetic peer.
- **I did not pursue `Quarantine`'s 64 slots** (still finite, still never reclaimed) or the
  `DirectoryNotFoundException`-as-absent case, which I read and judged correct without a probe.
- **Full suite run twice, no more.** Run 1 (baseline at `f8a724c`) and run 2 (after every mutant was
  restored, my probe classes filtered out) — both **432 green (75 / 111 / 246)**.

```
$ dotnet test TradeAgent.sln --filter "FullyQualifiedName!~VerifyR6Probes"    # run 2 of 2
EXIT=0
Passed!  - Failed: 0, Passed:  75, Skipped: 0, Total:  75 - TradeAgent.FaultTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 111, Skipped: 0, Total: 111 - TradeAgent.UnitTests.dll (net10.0)
Passed!  - Failed: 0, Passed: 246, Skipped: 0, Total: 246 - TradeAgent.IntegrationTests.dll (net10.0)
```

The tree is left exactly as it was found.

VERDICT: FAIL — 1H/1M/1L
