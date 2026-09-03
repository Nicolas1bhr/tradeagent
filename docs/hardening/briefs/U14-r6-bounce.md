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

## Verifier round-5 findings (leg [2], Opus, on `6a40fa7`) — VERDICT: FAIL — 1H/2M/2L · record `records/U14-verify-r5.md`

- **R1 (HIGH) = the heartbeat branch of the F20 / PRIOR 9 class.** A peer whose hello was refused as protocol 2 sets
  `_hello` — and with it `ReconciliationProvable=True` — through ONE heartbeat claiming protocol 3 (`AtasConnector.cs:371`);
  that removes the `AUTONOMY_REQUIRES_PROVABLE_STATE` refusal (`TradingGateway.cs:213`) and the "needs a human to look"
  escalation (`:818`) while the row still says "reinstall the add-on". Round 5 guarded the event branch and left the
  heartbeat branch; the connector's own comment names the route. **Rule (one decision, one place):** refusal is decided
  ONCE at the top of `Dispatch` for the whole connection — a refused peer's events, heartbeats and later hellos are all
  dropped; `_hello`/`ReconciliationProvable` can be set only by a compatible hello on an unrefused connection. Tests: v2
  hello then a v3-claiming heartbeat → still refused, `ReconciliationProvable=false`, autonomy still refused, row
  unchanged; fresh v3 connection → accepted. (You may read `TradingGateway.cs` to write the assertion; do not edit it.)
- **R2 (MED) = F21's premise.** The lease is released only via `StopBridge` ← `OnStopping` (`AtasStrategyAdapter.cs:212/393/468`),
  a path no test runs; if ATAS does not fire it, every later order is refused until ATAS restarts. Rule: release on EVERY
  terminal path — `OnStopping`, `Dispose()`, and whatever ATAS calls when a strategy is removed from a chart — and never
  rely on one callback. Which callback ATAS actually fires is NOT verifiable without disturbing the running bridge on the
  box; leave that line under NOT verified for the v0.1.2 bridge redeploy, and say so.
- **R3 (MED).** Safety events are DROPPED under concurrent sidecar appends (4/2/2/6 lost of 160 over four runs) — the
  writers are the ones the lease refused, so they are unserialised by construction. Rule: a refused writer never writes the
  owner's sidecar; it writes its own per-writer file (same directory, same glob, collected by the probe and the support
  package), or appends with a guaranteed-atomic single write — choose, and prove it with a 160-event concurrent test
  that loses none.
- **R4 (LOW).** Unlinking the lock file yields two live owners on macOS (Windows immunity not verified); measured to cost no
  claim (CAS + read-back refuse). Document in the record; no code unless a one-line guard exists.
- **R5 (LOW).** Record wording: the CAS also fires after a legitimate ownership handover, not only for a foreign build.

Closed by the verifier (do not carry forward): the "unproved hello" peer (dropped outright, raises nothing); MF4b
(unreachable, three ways); MV9 (no observable effect). Held: lease both directions on real processes (A alive → B
refused; A SIGKILLed → C acquires); 3 × 240 claims → 80 durable / 0 lost / 0 phantom / 156 lock refusals / 0 CAS; F8
field-precise; F4/F13 anchors; 417 twice.
