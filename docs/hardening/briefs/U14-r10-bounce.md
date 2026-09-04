# U14 — ROUND 10 · STRUCTURAL ROUND (Codex delta on round 9: 4 HIGH / 5 MED / 0 LOW, 3 priors still open) (+ verifier r9, below)

**Fresh builder.** Read first: the standard's §6 and §9.10, `CLAUDE.md`, `records/U14.md` "## Round 9" (and the
round-8/9 class-closure arguments — they are what failed), `briefs/U14-r9-bounce.md`, `records/codex-U14-r9.txt`.
Worktree `u14-build`, branch `u14-coid-witness-rewrite`, tip `e113c4c` (477 green, 0 warnings). Rules as every round
(commit per step, no trailers, commit before mutants, `cp` restore + `touch`, test-name diff after every structural edit,
`## Round 10 (build record, <date>)` in `records/U14.md` on MAIN, no git there, `--no-incremental` = 0 warnings). One
box run at the END (the adapter changes; bridge compile + `tools/atas-gate`, hash-verified) — the manager grants it here.

## Why this round is different

Rounds 6–9 fixed the same three classes instance by instance: unreadable ≠ empty (F17, F28, F31, PRIOR 28, F33, F36,
F37), the rotation crash window (F18, F27, F30, R8-1, F34, PRIOR 27), the teardown door (F21, R2, PRIOR 21, R8-2, R8-4,
F35). Each fix was proved in the state its author built, and the next reviewer found the neighbouring state. §9.10: when
findings share a root cause, fix the CLASS with a structural change. This round changes the structure so the class has
one site and one failure mode; the class-closure argument then becomes a sentence, not an enumeration.

## Structural directives (decided — implement; report where one does not survive the code)

1. **One function touches the sidecar filesystem.** Introduce `SidecarSnapshot ReadSidecarSet()` in `CoidWitness`: it
   enumerates the directory, reads EVERY sidecar generation, staging and per-writer file in full, and returns either a
   complete in-memory snapshot (file → lines) or `Unreadable(reason)`. It has exactly ONE `try/catch` around the whole
   operation: any exception of any type at any step (enumerate, exists, attributes, open, read, `DirectoryNotFound`,
   `UnauthorizedAccess`, sharing violation, a file that vanished mid-read) → `Unreadable`. Every consumer — `HasNotes`,
   `LastDecidingLine`, `SidecarPaths`, `Trouble`/degraded, `Notes`, `CoidWitnessReport`, the probe, the support package,
   and ROTATION — reads from a snapshot and never touches the filesystem itself. Consumers must not be able to reach the
   filesystem by construction: give them the snapshot type, and make the raw read private. `Unreadable` at every
   consumer means: standing unreadable/degraded, zero provisional, `Trouble` non-null, report says "could not read".
   Both directions: an absent directory-with-witness-present and an absent sidecar read as clean-empty ONLY when the
   enumeration itself succeeded and found nothing.
2. **Concurrent change is detected, not raced.** The snapshot records the directory listing (names + lengths + mtimes)
   before and after reading; if they differ, read again; if they differ twice, the snapshot is `Unreadable("changing")`
   (provisional, degraded). That closes "a marker moved into an already-scanned file during rotation" without a lock on
   readers; rotation stays owner-only under the lease.
3. **Rotation is atomic renames over a snapshot, and the carry is written first.** Rotation (owner only, under the lease):
   (a) take the snapshot; if `Unreadable` → do not rotate (append to the current log instead; a safety event is never
   lost to a rotation that cannot read what it rotates); (b) compute the carry (the unresolved safety state) from the
   whole snapshot; (c) write the NEW current log to a temp file, carry line FIRST, `Flush(true)`; (d) rename the oldest
   generation out only AFTER (c): rename `.1`→`.2` (deleting a prior `.2` last), rename current→`.1`, rename temp→current.
   No `.rotating` staging file exists any more. A crash at ANY point leaves the marker in at least one file the reader
   reads (the reader reads every name matching the sidecar glob, temps included). Enumerate the crash points in the
   record as a list of renames — the argument is "every intermediate state is a subset of files the snapshot reads".
4. **Teardown is a locked three-state machine.** `AdapterTeardown` holds `Running → Stopping → Stopped` under ONE lock;
   `Started()` is legal only from `Stopped` (never during `Stopping`) and takes the lock; `Stop()` moves to `Stopping`
   under the lock, runs the steps, releases the lease, then moves to `Stopped`; every witness operation checks the state
   UNDER THE SAME LOCK and is refused unless `Running`. The witness is reachable only through the teardown object (round
   9's "only door" stands: `_witness in the adapter: 0`). Tests: the interleavings `Started()` during `Stopping`, a write
   during `Stopping`, a write after `Stopped`, a restart after `Stopped` — each under the lock, plus the verifier's
   two-thread probe at 40 rounds.
5. **The row always has a current status.** A current connection ALWAYS yields a derived status newer than any marker:
   authenticated-without-hello → "connected, waiting for the add-on's hello"; silent past `AuthGrace` → silent; refused →
   its refusal. "No status" is not a state. F38's test: v2 refused → new peer authenticates, sends no hello → the row says
   waiting-for-hello, not protocol 2.

## What this closes (write the mapping in the record; Codex and the verifier will check it)

PRIOR 27, PRIOR 28, PRIOR 31, F33, F34, F36, F37 → directives 1–3. F35 → directive 4. F38 → directive 5. Every prior test
from rounds 6–9 in these areas stays green (they are the regression set); a test that only passes because of the old
structure is rewritten against the snapshot, and the record says which.

## Gate and report

Targeted classes; `dotnet build TradeAgent.sln --no-incremental` (0 warnings) + FULL suite once; the box run at the end
(bridge compile + `tools/atas-gate`, hash-verified before and after, ATAS untouched). Report: tip sha, per directive the
RED that motivated it → GREEN → the mutant that bites, the crash-point list, the mapping table, suite counts, the box
result, "What I did NOT do".

## Verifier round-9 findings (fresh Opus, on `e113c4c`) — VERDICT: FAIL — 1H/2M/2L · record `records/U14-verify-r9.md`

Every one maps to a directive above; the mapping table must show it.

- **R9-1 (HIGH) = Codex F34, measured.** `Rotate` destroys a generation it could not READ: `LastDecidingLine()` answers
  null both for "nothing unresolved" and for "every generation threw", so `carry is null` deletes the `.rotating`/`.1`
  that held the only gap — real `chmod 000` (`TA-GAP` gone from every file) and a real SIGKILL between rotation and
  save (`Trouble = null`, `io:noted`). → directives 1 + 3: unreadability is a VALUE the caller must handle; a rotation
  that cannot read does not rotate.
- **R9-5 (LOW), same class.** `Candidates()` returns an empty list for a refused enumeration (the recovery path). →
  directive 1 (the snapshot is the only reader; `Unreadable` propagates).
- **R9-2 (MED).** `_noted` is written in `EnsureRecovered()`, which `Noted` does not run: a fresh instance answers
  `Noted=false` while `Token()` answers `io:noted`; `Standing` is right only by C#'s argument-evaluation order. →
  directive 1: ONE derived state from ONE snapshot, computed once, read by every property; no property may depend on
  another having run first (test: every public property on a fresh instance in every order).
- **R9-3 (MED).** The "explicit credential refusal vs derived silence, across two connections" cell is untested (MV9-a
  survives everything but the verifier's probe). → directive 5: the precedence table gets a test per cell; lift the
  verifier's probe (`u14-verify-r9-probes`).
- **R9-4 (LOW).** `Rotate(SidecarPath)` decides its carry from `ErrorLogPath`, so a refused writer's own oversized sidecar
  rotates on the canonical machine's deciding line and deletes its own `.1`. → directive 3: rotation computes the carry
  from the snapshot OF THE FILE SET BEING ROTATED.

Closed by the verifier: R8-2 (`_witness` in the adapter = 0; MD-R9-2 RED), R8-3 (dribbler dropped; MR9-3 RED), R8-4;
the quiet-but-heartbeating bridge held 130 s; R3 harness 160/0 three times; 477 twice.
