# INTEGRATION SCRIBE BRIEF (light leg, docs only) — run once per integrated unit, after the manager's merge is pushed

**Tier T3 (docs) with the honesty contract in force:** every sentence you write is traced to a record line or a run the
record quotes; NOT VERIFIED where the record says so; banned words apply (should work, looks correct, probably, I
believe, minor, trivial, static-verified, basically). You write docs; you do not touch code or tests.

Inputs (the dispatch names the unit `<U>`, the merge sha, the CI run id and the suite counts at the merge):

1. `docs/hardening/records/<U>.md` — the whole unit record (every round, the verify verdicts, the Codex deltas, the
   box runs, the deferred items and their owners).
2. `docs/hardening/records/<U>-verify-r*.md` and `codex-<U>-r*.txt` — for the final verdicts and the exact numbers.
3. `docs/hardening/briefs/integration-checklist.md` — what integration means here.
4. `BUILD-STATUS.md`, `docs/RESUME-HERE.md`, `docs/USER-GUIDE.md`, `docs/CONTRACTS.md`, `AGENTS.md` — the docs you
   update, in the MAIN worktree `~/Projects/ai-trading-software-for-mihael` on a branch the manager names (never on
   `main` directly).

Deliverables:

- **`records/<U>.md` gets a final `## Integrated` section**: merge sha, date, the suite counts at the merge (Mac; box if
  a verified-tree run exists), the CI run id and its per-platform conclusion, the final verify verdict and the final
  Codex summary with their record paths, and the DEFERRED items each with its owner unit and the measurement that
  justifies it (copy the sentence from the round that deferred it). Then a "Claims that expire" list: every number in
  the record that depends on constants (deadlines, drain, chunk sizes) with the constant it depends on.
- **`BUILD-STATUS.md` gets the unit's section**, written from the record: what the unit changed (by round, one sentence
  each), the proof (the final suite counts, the mutants table summary — "N mutants, N bit, K stated equivalent"), the
  on-box figure with its identity check or NOT VERIFIED, the deferred items with owners, and the "Claims that expire".
  Nothing in it is a claim without a quoted run.
- **`docs/RESUME-HERE.md`**: "Do this first" and "Verifying what you inherited" updated where the unit changed a reading
  (U14: `proto=3`, the bridge DLL must be redeployed; U2a: the 61-char id budget, the `EmergencyDeadline` 2 s and the
  10 s liveness grace, the five per-leg words, the 265 s worst-case shutdown with an order in flight).
- **`docs/USER-GUIDE.md`**: only the sentences the unit makes true or false for the OWNER (no command lines, the app's
  own words): for U2a the emergency answer wording ("NOT confirmed — check your positions and orders in ATAS") and the
  shutdown wait; for U14 nothing owner-visible except the "reinstall the add-on" sentence and its trigger.
- **`AGENTS.md` / `docs/CONTRACTS.md`**: confirm the unit's release facts are stated once (U2a: the replay contract per
  op, the id charset/length, the handler depth table, the five words; U14: protocol 3, the witness-refusal sentence);
  fix duplicates or contradictions by pointing one at the other.

Process: work on the named branch; commit per document, one plain sentence, NO `Co-Authored-By` trailers; do not push
(the manager merges the docs branch); write `records/<U>-integration-docs.md` AS YOU GO with the source map
(`sentence → record §/line`) and "What I did NOT do". Report: branch tip, files changed with line counts, the number of
sentences sourced vs marked NOT VERIFIED, anything in the existing docs you found contradicted by the record.
