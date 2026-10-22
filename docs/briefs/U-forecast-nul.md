# U-forecast-nul — a forecast's probability with a NUL inside it is refused by the schema, and a malformed forecast row is refused by name instead of stopping every resolution
**Arrow closed:** O5b's owed re-review item 4 (judged OWED, `fleet/handoff/B.md` § 3): the rung-41 CHECK on `org_forecast.probability`
(`Database.cs:2568-2571`) accepts `'0.5' || char(0) || 'garbage'`, which the app's writer refuses (`OrgStore.Decisions.cs:115-119`); ORCHESTRATION § 2.11, § 8 (Evidence).
**Protections — evidence (a forecast is a precommitted claim the resolver scores; the schema states its one shape), refuse-never-guess: red-first and ONE
watched mutant.** Seat B; **opus/high** (an evidence path and a rung; small). **A rung (the next free at dispatch).** Light; course step (0).
**Depends (landed):** O5b `U-org-decisions` (rung 41, `e94ee00d`). Survey: `fleet/tmp/B-scratch/org-packets/survey.md` § 7.
**Today:** SQLite's `length`, `GLOB` and `substr` stop at the first NUL, so the CHECK sees `0.5` (macOS `sqlite3` 3.51.0: accepted, `length` 3, blob length 11;
the app's bundled SQLite NOT VERIFIED). `ReadForecast` parses every row with `decimal.Parse` (`OrgStore.Decisions.cs:445-448`), which throws
`FormatException` on `"0.5\0garbage"` (checked with `dotnet fsi`), so one such row would throw `OpenForecasts()` (:416), `ForecastResolver.Sweep`
(`ForecastResolver.cs:42`) and, swallowed at `MissionLoop.cs:3412`, stop every resolution (INFERENCE: the provider returning the NUL tail is NOT VERIFIED).
Only a writer bypassing `OrgStore` can make one (an unconfined seat can write `state/`, ORCHESTRATION § 2.5): defence in depth.
**Observable result:** an INSERT whose probability's byte length differs from its character length is aborted by the schema with a sentence; a
forecast row the reader cannot parse is listed by the resolver as refused ("left unresolved, never guessed", as the vocabulary refusal does,
`ForecastResolver.cs:44-48`) and every other forecast still resolves.
Items, one commit each, one-sentence messages:
1. **The rung:** `CREATE TRIGGER org_forecast_probability_bytes BEFORE INSERT ON org_forecast WHEN length(CAST(NEW.probability AS BLOB)) <>
   length(NEW.probability) BEGIN SELECT RAISE(ABORT, …) END` (no table rebuild; append-only triggers untouched); the `Versions` note names it. Settles (a), (b).
2. **The reader:** `OpenForecasts` reads the probability as text and yields a malformed row as a refusal the sweep lists, never a throw. Settles (c).
Red-first (each red at base quoted): (a) `A_probability_with_a_nul_inside_is_refused_by_the_schema`; (b) GUARD `Every_probability_the_writer_accepts_the_schema_accepts`
(0, 1, 0.5, 0.0000…, 1.000); (c) `A_malformed_forecast_row_is_refused_by_name_and_the_others_still_resolve` (seeded by raw SQL with the trigger dropped
in the test's own home). **The watched mutant:** the trigger's WHEN inverted to `=` ⇒ (a) red and (b) red. Quote it, restore, green.
**NOT in scope:** rebuilding `org_forecast` with a new CHECK; any other column's NUL (a card if the board wants a sweep of text CHECKs).
Gate and report per `fleet/BUILDER-PROMPT-v2.md` (the rung's crash-before-stamp reopen; three platforms; Codex review). `## Report` ≤ 20 lines appended here.

## Report
Code tip `28bf77b9` (base f7b2ab02 after rebase; rung 42, main at 41). Items: 1 done `8093c5fc` (trigger `org_forecast_probability_bytes`, Versions note); 2 done `28bf77b9`.
Deviation (item 2): `OpenForecasts` now returns `OpenForecastList(Rows, Refused)`; the refusal names the row, never its text. `ForecastsOn` still parses strictly (throws), out of scope.
Gate 1: `dotnet build -c Release --no-incremental` → `0 Warning(s)` / `0 Error(s)`.
Gate 2: OrgDecisionTests 3× → `Passed!  - Failed: 0, Passed: 26, Skipped: 0, Total: 26` each run (OrgLedgerTests also green once, 33/33 with it).
Gate 3: CI run 39686367905 → `completed success`, 12/12 jobs green (all three platforms) on 28bf77b9.
Gate 4: names.sh → `removed: 0`, added 4, `[Fact]/[Theory] base: 2828  tip: 2832`.
RED at base: (a) `Assert.Throws() Failure: No exception was thrown` — the bundled SQLite (app connection) gives length 3, blob length 11 and STORES the NUL row;
(c) `System.FormatException : The input string '0.5 garbage' was not in a correct format.` at ReadForecast ← OpenForecasts ← ForecastResolver.Sweep;
rung test: `the trigger arrives at rung 42; this build says 41`. (b) is a guard: green at base by design.
Mutant (WHEN `=`): (a) `Assert.Throws() Failure: No exception was thrown`; (b) `SqliteException : SQLite Error 19: 'a forecast's probability is refused: its bytes and its characters differ …'`. Restored, green.
Crash-before-stamp: `The_probability_trigger_arrives_at_its_rung_and_a_crash_before_its_stamp_runs_it_again` — stamp set to 41 with the trigger present, and again with it dropped; each reopen stamps 42, the trigger present and refusing, the forecast in place. Green.
Tests box: NOT RUN — a run from another branch was in progress (ready exit 4).
Review: Codex review relaunched detached by seat B → fleet/tmp/B-scratch/u-forecast-nul/U-forecast-nul-review.md (not read, not acted on).
NOT done/verified: no other column's NUL; ForecastsOn unchanged; the provider returning a NUL tail stays NOT VERIFIED; ready ran 3× (once to read its reason), not once.
