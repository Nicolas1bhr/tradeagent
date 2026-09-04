# SECOND-UNIT REBASE BRIEF — whichever of U2a / U14 integrates second crosses the other's `AtasConnector.cs`

Measured 2026-09-04 (dry-run, probe removed): `u14-coid-witness-rewrite` (`01fcd60`) rebased onto `main` is CLEAN (85
commits), and onto the U2a tip (`120c739`) CONFLICTS in `src/TradeAgent.Connectors.Atas/AtasConnector.cs` at U14's
round-8 commit `6884cdf` ("Let the peer that is there now describe the row, and keep the older refusal recorded"). Both
branches also touch `docs/CONTRACTS.md` (merges cleanly). The handoff's "disjoint files" was true on 2026-09-03 and
stopped being true in U14 round 5 (protocol-3 refusal) and U2a round 5 (deadlines and liveness) — both units rewrote
the connector's read loop and status logic.

## Rule

The first unit to pass its final verify + Codex integrates per `integration-checklist.md`. The second unit does NOT
merge until a builder leg has rebased it over the new `main`, resolved the connector conflict keeping BOTH units'
behaviour, and a targeted verify of the COMBINATION has passed.

## Builder leg (Opus, fresh): "rebase <second unit> over main"

1. Worktree: the unit's build worktree. `git rebase main` (U14) or `git rebase main` on the probe branch (U2a). At the
   conflict in `AtasConnector.cs`: resolve so that EVERY guard of both units survives — U2a's: the risk-reducing scope
   and one absolute deadline per operation, the transport tri-state and attempt marker, liveness = an answer within the
   ordinary deadline (10 s grace), the whole-frame ceiling, the chunk-progress rule; U14's: refusal decided once at the
   top of `Dispatch` for the whole connection, `_incompatible`/`_unauthenticated` markers with counters, the row always
   deriving a status newer than any marker, the heartbeat predicate on every read-loop turn, drop on `HeartbeatTimeout`,
   the frame-loop writes refused after teardown. Never drop a guard to make it compile; if two guards genuinely
   contradict (e.g. U2a's liveness grace keeping a peer that U14's heartbeat rule would drop), STOP and report the
   contradiction with both records' sentences — the manager decides.
2. `dotnet build TradeAgent.sln --no-incremental` (0 warnings) + FULL suite; every test of both units green — a red
   test is a real interaction, not a rebase artefact: diagnose in the record.
3. Record: `records/<unit>.md` "## Rebase over <other unit>" — the conflict hunks, how each was resolved, which guard
   each line serves, the suite counts. Commit per resolution step; no trailers.

## Verifier leg (Opus, fresh): "combination verify"

Targets: the connector's behaviour with BOTH units live — (a) a v2 peer refused while an emergency is pending; (b) a
heartbeating-but-mute peer under U2a's 10 s grace vs U14's `HeartbeatTimeout` — which rule fires first, and is the
result correct under both records; (c) a refused peer parked/dropped while a sweep is mid-flight — per-leg outcomes
still 1:1; (d) the row's derived status during a liveness drop; (e) both units' full probe sets from their last verify
rounds (`u2a-verify-r12-probes`, `u14-verify-r10-probes`) green on the combined sha; (f) full suite once, 0 warnings.
Codex delta on the combined sha with both units' final prompts as priors (range: the other unit's merge sha..HEAD).
Verdict PASS / PASS WITH LOW → the manager merges; else a bounce to the rebase builder.
