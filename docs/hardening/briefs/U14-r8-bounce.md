# U14 — ROUND 8 BOUNCE · Codex delta on rounds 6–7b (`records/codex-U14-r7.txt`): 10/17 priors FIXED (+ verifier r7, below)

**Same builder, same worktree.** Findings are INPUTS; each becomes a test → RED → fix → GREEN → mutant, or is refuted
by running its check. The manager already refuted five (bottom) — do not re-open them, do write them in the record.

## Real — fix

- **PRIOR 17 (Codex counts it HIGH).** `DirectoryNotFoundException` is still classified as ABSENCE (`CoidWitness.cs:1539`);
  the rule permits only `FileNotFoundException` on the committed PATH. A missing bridge directory is the ratified
  fail-closed case ("a machine with no bridge directory refuses every order") — it must read UNREADABLE and refuse, never
  replace. Test: remove the directory under a live witness → `Submitting` false, nothing written.
- **PRIOR 21 (MED).** The stopped guard's check is unsynchronised with lease disposal, so `Identified` can enter after the
  check and reacquire (`AtasStrategyAdapter.cs:2038`). Take the same lock/state the disposal takes; test with an
  interleaving that enters `Identified` between check and dispose.
- **F26 = R2 (MED).** Teardown has no `finally`: an exception from `UntrackSecurities` skips witness disposal and the
  lease survives a terminal path (`AtasStrategyAdapter.cs:501`). `finally`; inject the exception in both stopping and
  disposal; a replacement adapter acquires.
- **F27 + V3 (MED).** Rotation deletes the previous generation BEFORE the unresolved safety state is durably carried
  forward (`CoidWitness.cs:1982`) — a crash in that window loses the last safety line; and V3's "cannot be produced"
  claim is false for a process death between rotation and the next deciding line. Rule: write the restating marker as
  the first line of the new log and flush it BEFORE deleting the old generation; correct the V3 claim and its test
  (drive the crash window: seed `.1` with an unresolved ERROR, rotate, terminate before the deciding append, restart →
  degraded survives).
- **F28 (MED).** An existing but UNREADABLE sidecar (held `FileShare.None`) is treated as "no notes" → a falsely Clean,
  non-provisional zero (`CoidWitness.cs:949`). Unreadable ≠ empty — same predicate family as F17: standing
  unreadable/degraded, zero provisional. Test with the file held open exclusively.
- **F23 (MED, the V1 class again).** A compatible peer that goes silent (or sends a partial frame, or stays open and
  stale) monopolises the only pipe instance indefinitely because heartbeat degradation never terminates the read loop
  (`AtasConnector.cs:175`). Rule: after `HeartbeatTimeout` the peer is DROPPED (markers preserved per the round-7b rules)
  so the instance recycles and a redialling bridge connects. Test: handshake, silence past `HeartbeatTimeout`, second
  bridge dials with a deadline → connects (today: times out).
- **F29 (LOW).** `CoidWitnessReport.cs:68` describes every Noted state as "a rejected candidate beside the witness"; Noted
  now also means a per-writer ownership refusal — say which.

## Refuted by the manager (write each in the record with this reason)

- **PRIOR 5 PARTIAL** — the on-box gate execution evidence exists in `records/U14.md` (main; round 6) — the branch copy of
  the record is the 2026-09-03 snapshot Codex read.
- **PRIOR R4** — accepted LOW residual (documented): the lock is a pathname lock; on macOS an unlink yields a second
  owner, measured to cost no claim (CAS + read-back refuse); Windows holds the handle open with `FileShare.None`, which
  blocks deletion; macOS is not the production platform.
- **PRIOR R5** — record wording; the record lives on main.
- **F24 (LOW)** — `_incompatible` is instance-scoped BY DESIGN: a connector restart re-derives the refusal at the next
  hello within seconds; persisting it buys nothing and would need a store the connector does not own.
- **F25 (MED)** — "degraded" spans the OWNER's canonical sidecar; "noted" spans the whole per-writer set. A refused
  writer's ERROR line records a refusal that cost no order (the adapter refused it), so it makes the zero provisional
  (Noted) but does not degrade the owner's witness — the round-6 verifier examined and accepted this reasoning. The
  decision text in `briefs/U14-r6-bounce.md` was imprecise; correct the sentence in the record.

## Process

As before; `## Round 8 (build record, <date>)` in `records/U14.md` AS YOU GO; targeted gates, then `dotnet build` + FULL
suite once on the Mac; no box unless the manager grants it. Report: tip sha, per finding RED → GREEN → mutant, the
refutations written, suite counts, "What I did NOT do".

## Verifier round-7 findings (appended by the manager when leg [2] reports)

_pending_
