# U14 — ROUND 9 BOUNCE · Codex delta on round 8 (`records/codex-U14-r8.txt`): 0 HIGH / 3 MED / 0 LOW; 13/16 priors FIXED (+ verifier r8, below)

**Fresh builder** (the round-8 builder's session is gone). Read first: the standard's §6, `CLAUDE.md`, `records/U14.md`
"## Round 8" (skim rounds 5–7 for the design vocabulary: lease, per-writer sidecars, noted vs degraded, refusal markers
with counters, rotation carry-forward), `briefs/U14-r8-bounce.md`, `records/codex-U14-r8.txt`. Worktree `u14-build`,
branch `u14-coid-witness-rewrite`, tip `10fa21f`. Rules as every round: commit per finding, no `Co-Authored-By`, commit
before mutants, `cp` restore + `touch`, diff test-method names after every structural edit, checkpoint `## Round 9
(build record, <date>)` in `records/U14.md` (MAIN worktree, no git there). Build gate `dotnet build TradeAgent.sln
--no-incremental` (0 warnings). No box unless granted.

Three MEDs, three classes already fixed once — close the edges, and this time state in the record WHY no further edge
of each class exists (enumerate the operations of the class and show each is covered):

- **F30 + PRIOR 27 (MED, class: the rotation crash window).** Moving the current generation to an unscanned `.rotating`
  staging file hides its only unresolved marker before the carry-forward exists (`CoidWitness.cs:1497`): oversized
  current log whose latest deciding line is ERROR, `.1` absent or RESOLVED, terminate after `File.Move(log, staging)` and
  before `_writeSidecar` → a new reader scans only `log` and `.1`, reports no open gap, and a later rotation deletes
  `.rotating`. Also: the replacement uses `File.WriteAllText` with no `Flush(true)` before the old generation is
  deleted (`:551`). Rule: at EVERY interleaving a reader sees the unresolved state — either the carry-forward line is
  written and flushed BEFORE the move, or readers scan the staging file too (and a later rotation never deletes a staging
  file that still holds an unresolved marker); the carry-forward write is flushed to disk (`Flush(true)`) before any
  deletion. Enumerate the interleavings (before move / after move before write / after write before flush / after
  flush before delete) in the record with a test for each; the `_writeSidecar` seam from round 8 is the instrument.
- **F31 + PRIOR 28 (MED, class: unreadable ≠ empty, the probe branch).** `File.Exists` and `Directory.GetFiles` failures
  (attributes denied, enumeration denied) return false/empty and leave `_noted` and `_sidecarUnreadable` false → `Trouble`
  null, zero non-provisional (`CoidWitness.cs:1519`, `:1000`). Rule: every probe that can fail is a read; a failure is
  UNREADABLE. Enumerate every filesystem call on the sidecar path (exists, enumerate, open, read, attributes) and show
  each maps to unreadable; tests with each denied (deny via the `_open`/`_replace`/`_writeSidecar` seam family — add a
  probe seam if needed).
- **F32 (MED, class: the row describes the peer that is there NOW).** An older refusal suppresses the derived state of a
  NEWLY connected silent peer (`AtasConnector.cs:134`): refuse protocol 2, `Drop` preserves `_incompatible`, a different
  peer connects and sends nothing → after `AuthGrace` `Unauthenticated.Silent` is suppressed and the row keeps saying
  protocol 2 until the new peer times out. Rule: a derived state for the CURRENT connection is a newer observation than
  any marker from a previous connection — stamp derived states with the same counter at the moment they are derived,
  so the row takes the newest; older markers stay recorded. Test: v2 refused → new silent peer → after `AuthGrace` the
  row says silent/unauthenticated; then v3 hello → clear.
- **PRIOR 29 (LOW).** `CoidWitnessReport.cs:73` describes a successfully recovered rewrite as "refused" — three states,
  three sentences (refused writer / rejected candidate / recovered rewrite).

Gate and report as before: per item RED → GREEN → mutant; the class-closure argument per item in the record; suite
counts; "What I did NOT do".

## Verifier round-8 findings (fresh Opus, on `10fa21f`) — VERDICT: FAIL — 3H/1M/0L · record `records/U14-verify-r8.md`

**CLASS (§9.10, the verifier's words): each round-8 guard was proved in exactly the state its author built, and each has
a neighbouring state one step away it does not reach.** This round's class-closure arguments exist to end that.

- **R8-1 (HIGH) = Codex F30, broadened to the ORDINARY state.** The current log is moved to `<log>.rotating`, a name
  `SidecarGenerations()` does not scan; an unresolved ERROR living in the CURRENT log (the ordinary case — both round-8
  rotation tests seeded `.1` instead) is invisible in the window and permanently lost if the restatement write does not
  land. Measured with a real SIGKILL inside the window: restart reads `Trouble = null`, `io:noted`; `SupportsClientOrderId`
  stays true over a lost write-ahead record. Fix per F30's rule; the test seeds the ERROR in the CURRENT log and kills
  inside the window (the verifier's harness is on `u14-verify-r8-probes`).
- **R8-2 (HIGH).** `AdapterTeardown.Record` guards ONE of FOUR witness write sites (`AtasStrategyAdapter.cs:1409`, `:1562`,
  `:1824`); the other three run on the BridgeServer frame loop, which outlives teardown by construction (`DisposeAsync`
  waits 5 s then gives up; `StopBridge` catches its own timeout). Measured: after `Stop` released the lease, an unguarded
  write re-leased and a replacement adapter was refused "another writer owns this witness" — PRIOR 21's own harm,
  unchanged. Rule: EVERY witness write goes through the guard (one choke point, enumerate the four sites in the record),
  and the frame loop's writes after teardown are refused, not raced. The adapter compiles only on the box — the manager
  grants ONE box run at the end of this round for the bridge compile + `tools/atas-gate` (push, hash-verify, run, re-hash).
- **R8-3 (HIGH).** The F23 drop is consulted only when the idle poll WINS the race (`AtasConnector.cs:276-281`, `:189`): a
  peer emitting any line faster than `IdlePoll` (`{"op":"ping"}` every 200 ms, no heartbeat) is never asked whether it
  has gone quiet — health says DEGRADED and the replacement bridge's `ConnectAsync` still fails. Rule: the heartbeat
  predicate is evaluated on EVERY loop turn regardless of which branch of the race completed; a chatty non-heartbeating
  peer is dropped at `HeartbeatTimeout` like a silent one. The verifier's 200 ms-ping probe is the acceptance.
- **R8-4 (MED).** A half-move of the PRIOR 21 guard (check outside the lock, write inside) survives every
  `AdapterTeardownTests` case; only a 40-round two-thread probe catches it — lift that probe into the suite.

Box (one run, identity-checked): bridge compiled against ATAS, 5 warnings 0 errors, the `AdapterTeardown` call sites bind;
`tools/atas-gate` PASSED both directions. Held: 454 twice; ten carried probes green (V4, V5 closed); R3 harness 160/0
dropped; all five refutations confirmed (R4 re-measured).
