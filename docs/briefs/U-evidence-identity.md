# U-evidence-identity — evidence is bound to what the evaluator MEANS, not to the app's release number, so an update withdraws only what it changed
**Protects:** rule 9 of `docs/COUNCIL.md` ("a changed assumption invalidates the evidence that rested on it") while removing a false positive.
**Depends on `U-cost-model`** (same files). **Today (SOURCE at `0f47db7`, R09/R11-checked):** versions and promotions record `interpreter_build = "app=
{Versions.App};language={LanguageVersion}"` (`Core/Db/StrategyStore.cs:212-214`; on the promotion at `Referee.cs:307`, also a hashed fact of its id
`PromotionStore.cs:148`); the ONE comparison that withdraws standing is `PromotionStore.cs:445` (in `Invalidation`, `:383-465`); a deployment whose verdict no
longer stands is ENDED (`TradingGateway.cs:784-799` via `:737-741`); `trade verdict` answers a withdrawn row as if it stood (`GatewayPipeServer.cs:2484`).
The app updates itself (`Provisioning/UpdateService.cs`), so EVERY release ends every paper run. Every promotion row already holds `evaluator_version =
"backtest=1;metrics=1;scoring=1"` (`Referee.cs:94`; NOT NULL since rung 15, `Database.cs:890`; one writer `Referee.cs:309`; unchanged since `43ac196`), and
the language/indicator/calendar manifest is recorded on every version row (`StrategyStore.cs:199`) and inside every program id (`StrategyProgram.cs:49-50`).
Today the app version accidentally ALSO covers manifest bumps; replacing it must not lose that. **Observable result:** a release that changes no evaluation
semantics leaves verdicts standing and paper runs running; a change to `EvaluatorVersion` or to `StrategyVersions.Manifest` withdraws exactly the evidence
it touches, ends affected paper runs, and `trade verdict` says why. **No schema change.**
Read first: `CLAUDE.md`; `docs/COUNCIL.md` rule 9 and `:174`; `CampaignStore.cs:37-39,77-79` (the V1 / PaperV1 texts promise "the interpreter build … a change
invalidates" — DO NOT edit them; their sha is checked at `Referee.cs:196-206`); `Referee.cs:80-120,180-340`; `StrategyStore.cs:190-260`; `PromotionStore.cs:140-150,
240-270,383-465`; `ForwardRuns.cs:520-535` (`Frozen` re-parses without checking the id); `TradingGateway.cs:730-800`; `GatewayPipeServer.cs:2440-2520`;
`PromotionLedgerTests.cs:350-369`; `.gitattributes`.
Items, one commit each, one-sentence messages:
1. `Invalidation` withdraws standing when the promotion's `evaluator_version` ≠ `Referee.EvaluatorVersion` OR the version row's `manifest` ≠
   `StrategyVersions.Manifest`, and no longer compares `interpreter_build`, which stays as provenance and as a hashed fact. `ForwardRuns.Frozen` ends a run
   whose re-parsed program id no longer equals `deployment.VersionId`. `PromotionLedgerTests.A_promotion_from_another_interpreter_or_another_policy_does_not
   _stand` is REWRITTEN IN PLACE, name kept: the "other interpreter" becomes a different evaluator version and, separately, a different manifest.
2. CONTRACTS defines "the interpreter build" in the V1 / PaperV1 texts as the evaluation semantics — `EvaluatorVersion` plus the language manifest — names
   the protected property (`CLAUDE.md`: a replaced enforcement point names what it keeps) and shows that item 1 keeps it.
3. GOLDEN VECTORS: ≥ 12 programs (every indicator, crossings, history references, stops/targets, sessions, entry windows, max-hold, warm-up, gaps), each with
   an EXPLICIT execution model, over fixed bar fixtures; trace and metrics shas pinned in one test together with `EvaluatorVersion` and the manifest. Changed
   output with both unchanged FAILS, saying "bump `Referee.EvaluatorVersion` (or the manifest) and re-pin in the same commit". Fixtures are JSON, or CSV with
   `*.csv text eol=lf` added for the fixture path in `.gitattributes`.
4. `trade verdict` on a withdrawn standing answers in words — withdrawn on <date> because the evaluation semantics changed from X to Y; re-judging is not
   available yet (`U-rejudge`) — and a standing verdict is answered as it stands. `CONTRACTS.md`, `USER-GUIDE.md` (updates no longer end paper runs unless
   evaluation changed).
Red-first tests: (a) `A_release_with_unchanged_evaluation_semantics_keeps_the_verdict_standing_and_the_deployment_running` (red on base: a promotion with a
different `app=`); (b) `Changed_evaluation_output_without_a_version_bump_fails_the_golden_vectors` (red through mutant ii); (c) `An_evaluator_version_bump
_withdraws_standing_and_the_verdict_reply_says_why` (red on base at `:2484`); (d) `A_manifest_bump_withdraws_standing_and_ends_the_paper_run`; (e) `A_standing
_verdict_is_answered_as_it_stands` (guard). Mutants to watch red and quote: (i) `interpreter_build` back in the compare ⇒ (a) red; (ii) one golden sha edited
without a bump ⇒ (b) red; (iii) the manifest compare removed ⇒ (d) red.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.

## Report
Tip `64f2876` (last code commit; rebased onto `main` 8a51a16 with `U-runner-forward` in, no conflict). Build `-c Release --no-incremental`: 0 Warning(s), 0 Error(s). No schema change.
Unit: Failed 0, Passed 1249, Total 1249. Fault: Failed 0, Passed 405, Total 405 (both through `suite.sh`). 3× each, all green: `PromotionLedgerTests` 9/9, `EvaluationGoldenVectorTests` 2/2, `EvaluationSemanticsTests` 2/2, `EvidenceIdentityTests` 3/3, `VerdictOverPipeTests` 9/9.
CI run 37055031758 on 64f2876: success — macos-latest success 14 min, windows-latest success 31 min, ubuntu-latest success 12 min, package success 3 min. Names vs `main`: sets base 2006 tip 2016, removed 0, added 10.
Item 1 done (`e005b65`): `Invalidation` compares the row's `evaluator_version` and the version row's `manifest`, never `interpreter_build` (kept as provenance and hashed fact); `ForwardRuns.Frozen` ends a run whose re-parsed id ≠ `deployment.VersionId`. `A_promotion_from_another_interpreter_or_another_policy_does_not_stand` rewritten in place, name kept: evaluator, then manifest, then policy.
Deviation (item 1): the runner's end message at `ForwardRuns.cs:123-125`, outside `Frozen`, now also names the re-identified case — "no longer parses" was false for it; `U-runner-forward`'s hunks never touched those lines and the rebase was clean.
Item 2 done (`bd43e5d`): CONTRACTS' campaign section defines "the interpreter build" in V1/PaperV1 as `EvaluatorVersion` + manifest, names the protected property and shows how item 1 keeps it; verdict, runner and U-promote-bounds wording follow.
Item 3 done (`8bcb26f`): 15 vectors, each with all four model numbers, over `tests/TradeAgent.UnitTests/Golden/evaluation-bars.json` (JSON is already `eol=lf`, so `.gitattributes` is unchanged); trace sha, metrics-and-trades sha and both policies' answers pinned beside `PinnedEvaluator`/`PinnedManifest` and the fixture's own sha; a coverage guard reads the parsed programs and the runs.
Beyond the brief (item 3): 17 real evaluator changes tried (stop/target order, gap-through stop, max hold, EMA seed, RSI, ATR, opening-range reset, warm-up, EU change hour, weekdays, session exit, window end, both crossing boundaries, drawdown, fee rounding, scoring order) each move ≥ 1 pin; four were missed at first (stop/target order, EU hour, weekdays, crossings) and the vectors were reshaped until caught.
Item 4 done (`64f2876`): a withdrawn standing keeps verdict/reason/text and `why` says "WITHDRAWN on <date> because the evaluation semantics changed from X to Y: …", that nothing may trade on it and re-judging is not available yet; a standing one is answered as it stands. CONTRACTS, USER-GUIDE, op schema updated.
Deviation (item 4): no withdrawal date existed (standing is read-time), so the gateway records each semantics' first instant write-once in `kv` at construction (guarded); the date is the first other semantics recorded after the verdict, else "a date this installation did not record". `Promotions.Standing` gained an optional campaign.
RED before: (a), (d), the runner test and both changed `PromotionLedgerTests` red with base `Invalidation`/`Frozen` restored ("Expected: paper_eligible Actual: invalidated"; "Expected: 0 Actual: 3" bars stepped); (c) red with the base reply ("Assert.NotNull() Failure: Value is null").
Mutants: (i) `interpreter_build` compare restored → (a) red, "Expected: \"paper_eligible\" Actual: \"invalidated\""; (ii) one golden sha edited → (b) red, "1 golden vector(s) produced output other than what was pinned … bump `Referee.EvaluatorVersion` (or the manifest) and re-pin in the same commit"; (iii) manifest compare removed → (d) red, "Expected: \"invalidated\" Actual: \"paper_eligible\"".
Not done or verified: Integration run locally only for the touched class (plus Holdout/PipeContract), in full on CI only; no box run; re-judging (`U-rejudge`) not built; a few evaluator fault texts format a decimal in the machine's culture (division by zero, sizing) so such a trace can differ by machine — seen, documented, not fixed; the date exists only from this build on.
