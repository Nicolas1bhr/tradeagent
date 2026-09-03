# U14 — ROUND 6 BOUNCE · Codex delta review of `6a40fa7`: 13/16 priors FIXED, 3 PARTIAL, 6 new (1 HIGH / 4 MED / 1 LOW)

Raw: `docs/hardening/records/codex-U14-r5.txt`. **You stay the builder; same worktree `u14-build`.** Findings are INPUTS
(§4.4): each becomes a test → RED → fix → GREEN → mutant, or is refuted by RUNNING its check with the output quoted.

## Direction

- **F17 (HIGH, class: unreadable ≠ absent).** An opener that denies the load and CAS reads but permits the post-replace
  read makes the committed file look ABSENT: `_committedHash=null`, `_committedUnreadable=false`, A replaced by B, and
  `Submitting` can return true. Rule: **absent means `FileNotFound` on the committed path, nothing else**; every other
  read failure is UNREADABLE → every write refused, bytes preserved. Codex's injected-opener check is the acceptance
  (commit A; deny the four load reads and four CAS reads; submit B → refused, A byte-identical). This is the I/O sibling
  of F4/F13 (parse failures) — make the three share one predicate so the next variant cannot slip past.
- **F18 + F19 (MED, class: the degraded state must survive rotation and be computed over the whole sidecar set).**
  Rotation can hide an unresolved safety event when the first entry of the new log is diagnostic; a diagnostic-only
  sidecar is labelled historical and makes a zero falsely non-provisional. Rule: the unresolved-safety state is carried
  into the new log (a restating marker as its first line, or computed across the rotated files); "historical" means a
  RESOLVED marker AFTER the last safety line, never "no safety lines in this file". Both directions.
- **F20 + PRIOR 9 (MED, class: protocol compatibility is connection-level).** Authentication alone lets trusted events
  through before the hello is checked, a mismatch still returns true, and a later compatible hello in the same session
  clears the refusal. Rule: no trusted event is accepted until a compatible hello has been seen on THIS connection; a
  mismatched hello poisons the connection — nothing clears it but a reconnect; `mismatch` returns a refusal, not true.
  Tests: v2 hello then events → dropped and the connection refused; v2 then v3 on the same connection → still refused;
  v3 fresh connection → accepted.
- **F21 (MED).** A stopped adapter can reacquire the lease through its still-subscribed order handler. Rule: `StopBridge`
  releases the lease AND unsubscribes (or the handler ignores events after stop); test: stop → event arrives → no lease
  reacquired; a second writer acquires after stop. This also closes the runtime release the round-5 record listed as
  not done.
- **F22 (LOW).** A viable candidate contributing no acknowledgement is reported recovered and left for repeated
  processing — fix or refute.
- **PRIOR 5 + PRIOR 16 (PARTIAL → close them on the box).** The adapter's refusal of operator close-all when the witness
  is unavailable has no executable gate. You have the box tooling working: write the test against the ATAS stub on the
  box (make the witness path or lock unavailable; invoke close-all; assert `ITradingManager.ClosePosition` is never
  called and the result is the definite "nothing was submitted" refusal), run it there, paste the output. If the stub
  cannot drive a position, say exactly what is missing.

## Process

As round 5: commit per finding, no trailers, commit before mutants, `cp` restore + `touch`; append `## Round 6 (build
record, <date>)` to `records/U14.md` in the main worktree AS YOU GO with the finding table; targeted gates per finding,
`dotnet build TradeAgent.sln` + FULL suite once at the end on the Mac, and the on-box run for the adapter gate. Report:
tip sha, the table, suite counts (Mac + box), "What I did NOT do".

## Verifier round-5 findings (appended by the manager when leg [2] reports)

_pending_
