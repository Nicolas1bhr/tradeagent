# U14 — ROUND 7 BOUNCE · verifier round 6 on `f8a724c`: FAIL 1H/1M/1L (Codex delta for r6+r7 follows the quota reset)

Record: `records/U14-verify-r6.md`. **Same builder, same worktree.** The manager's round-6 ruling ("park the refused peer,
keep `return true`") is WITHDRAWN — the verifier showed it blocks the fixed bridge.

- **V1 (HIGH).** The pipe has `maxNumberOfServerInstances = 1` and the accept loop creates the next instance only after
  the read loop ends (`AtasConnector.cs:220/223/152-184/378`), so a refused peer that parks and stays silent holds the
  only slot: a FIXED bridge's `ConnectAsync(10_000)` times out (3/3) while the row says "reinstall the add-on". Rule:
  the refused peer is DROPPED (`return false`) and `Drop` PRESERVES `_incompatible` when our own refusal caused the
  disconnect — the code already argues exactly this for `_refused` at `:257-266`; make the two cases one. Acceptance:
  v2 hello → dropped within the read-loop turn; the row keeps the version and "reinstall the add-on"; a v3 bridge
  connecting next succeeds (the verifier's control: 146 ms); a v2 reconnecting is refused again; the verifier's MD1
  mutant (Drop wipes the version) RED.
- **V2 (MED).** `_noted` is computed over the per-writer sidecar set but gated on the CANONICAL sidecar existing
  (`CoidWitness.cs:936`): with five refusals on disk and no canonical file the probe prints "none recorded" and reads
  `records:0` as a confident zero — R3's fix reopened the flagged-zero class. Gate on the whole set; test: per-writer
  files only → `Noted=True`, zero provisional, probe prints the refusals.
- **V3 (LOW).** The F18 shape (a rotation leaving only a diagnostic line) cannot be produced by this build
  (`ReportAndQuarantine` always precedes `Save`); the guard is right, the test builds the state by hand. Either pin that a
  real rotation cannot reach it (a test that drives a real rotation and asserts the deciding line is present) or say in
  the record that the guard is defensive and why.

Process as before; append `## Round 7 (build record, <date>)` to `records/U14.md` in the main worktree AS YOU GO;
targeted gates, then `dotnet build TradeAgent.sln` + FULL suite once on the Mac. **No box access this round** (the
grant is not yours; nothing here needs Windows). Report: tip sha, per finding RED → GREEN → mutant, suite counts,
"What I did NOT do".
