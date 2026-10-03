# U-invariant-traces — a trace, its hashes and its fault words are the same bytes under any culture, because every number the evaluation path writes or reads names the invariant one
**Arrow closed:** U-evidence-identity's NOT-done line, fault texts "in the machine's culture", a must-fix before M0 (`BUILD-STATUS.md`:7360-7362; `CONTRACTS.md`:2286-2288). **Protects:** a request "reproduces the
same trace byte for byte and the same id" (`CONTRACTS.md`:2269-2270), on two machines (`BacktestTrace.cs`:44-47); no money-path file is touched. **After `U-timeframe-a` (dispatched `298bb36`; its brief is silent on
culture), before `U-timeframe-b`:** rebase onto `main` with it in, re-run the audit there, re-pin every vector then present. The bump withdraws nothing today: no install the fleet controls holds a standing verdict
(the orchestrator's statement, not a measurement). **Today (SOURCE at `923fb28`, unchanged at `298bb36`, read by the survey leg; NOT runtime-verified but for the measurements quoted):** the audit (`Strategy/`'s 21
files: 301 interpolation holes, every `ToString(`/`Parse(`/`TryParse(`/`Convert.To`/`Append(`/`Join(`/`+`, then what they hash into) finds culture-default DECIMALS on five lines: `StrategyInterpreter.cs:194`
`{a.Number}` (division by zero), `StrategyEvaluator.cs:535` `{account.Quantity}` (exit with nothing open), `:612` `{reference}` (close ≤ 0, capital fraction; no test found), `:626` `{level}`, `{reference}` (stop
not below the close), all fault texts, and `StrategyParser.cs:541`, a refusal printing `1000000` anyway. A fault is the trace's `Reason` (`Backtest.cs:470`), hashed into `trace_sha256` (`BacktestTrace.cs:152-215`),
stored as `fault_reason` (`Referee.cs:392`). **No culture-less decimal reaches a canonical or id text:** trace numbers, `ExecutionModel.Canonical`, `StrategyCanonical`, `VenueCostModel`, `PromotionRow.IdOf` use
`StrategyParser.Number` (`:1149-1150`, invariant, trailing zeros gone) or name the culture, as do `Sql.D/Dec/T` and every parse on the path (e.g. `StrategyParser.cs:675,695-696,1145`, `VenueCostModel.cs:310`,
`DatasetReader.cs:112-119`, `KlineNormaliser.cs:173-174,277`); dates are `O`, `u` or explicit; no `double`. Culture-less INTEGERS reach the version id (`StrategyCanonical.cs:57-60,68,73,115-120,145,165-166`,
`StrategyAst.cs:155,161`, `StrategyVersions.cs:45`), the run id (`Backtest.cs:276`) and a publication id (`Referee.cs:712-722`), and `KlineNormaliser.cs:350-351` writes bar times in the culture's time separator and
calendar: the same bytes under nl-BE and fr-BE (minus U+002D, `:`, Gregorian; measured); the times differ under some cultures. **Why laptop and CI agree today:** `InvariantGlobalization=true` for every project and
the tests (`Directory.Build.props:7`, `tests/Directory.Build.props:2`); the test runtimeconfig says `System.Globalization.Invariant: true`. Measured (`LANG=nl_BE.UTF-8 DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet
fsi`): culture and UI culture `''`; `GetCultureInfo("nl-BE")` → `CultureNotFoundException`; an invariant clone with `NumberDecimalSeparator = ","` makes `202.50m.ToString()` print `202,50`. The defect is latent,
one property away (`CoreTests.cs:345-349`), and no golden vector reaches it (`EvaluationGoldenVectorTests.cs:41-44`): today's fifteen pass under a comma culture; the red needs new vectors. **Observable result:**
under nl-BE's and fr-BE's number formats every golden vector (two new faulting ones too) and a stored-dataset backtest give the invariant run's trace, shas, run id, fault and trades; a fault spells numbers as the
trace does (`1026.7`, never `1026.70` or `1026,70`); `Referee.EvaluatorVersion` = `backtest=2;metrics=1;scoring=1`, re-pinned in that commit (required: `Number` drops trailing zeros, so old faulting traces change);
no id, manifest or cost-model sha moves. **No schema change.**
Read first: `CLAUDE.md`; `EvaluationGoldenVectorTests.cs`; `StrategyEvaluator.cs:529-641`; `BacktestTrace.cs:147-215`; `CoreTests.cs:340-382`; `CONTRACTS.md:342-353,2266-2288`.
Items, one commit each, one-sentence messages:
1. Fault texts are rendered invariantly where they are made (`EvaluationFault`, `EvaluationOutcome.Faulted`, `EvaluationState.Fault`, `Backtest.Run`'s own), so a later one cannot slip (a helper or `string.Create`;
   report which), decimals through `Number` (`StrategyParser.cs:541` too); two golden vectors, a zero divisor under a trailing-zero dividend (`close * 10 / (close - close)` → `1026.70`) and risk sizing under a
   fixed stop above every close; `Referee.cs:109` bumped and `PinnedEvaluator`, the pins and the class summary (`:13-45`) rewritten in this commit.
2. Every integer and date in hashed or canonical text (the list above) names `CultureInfo.InvariantCulture`; no byte moves, so the id pins (`DayOneStrategyTests.cs:53-55`, `BacktestDeterminismTests.cs:173-180`,
   `VenueCostModelTests.cs:104`) and the fifteen old vector pins stay untouched.
3. `Directory.Build.props`'s `InvariantGlobalization` is PINNED by test (e) (the orchestrator's order: the latent stays latent by construction), and `CONTRACTS.md`:2286-2288's limit becomes the rule: every number
   the evaluation path writes or reads names its culture explicitly (as `:350-353` for prices); `:2277`'s count too.
Red-first tests (Unit; each culture an invariant clone with `,` decimals and `.` (nl-BE) or U+202F (fr-BE) groups, the real ones being unbuildable here; `CurrentCulture` and `CurrentUICulture` set per test in
try/finally (`CoreTests.cs:368-381`), never `DefaultThreadCurrentCulture` (process-wide; Unit runs classes in parallel); each first asserts its clone writes `1.5m` as `1,5`): (a)
`Every_golden_vector_writes_the_same_bytes_under_nl_BE_and_fr_BE_number_formats` (`RunAll`/`Computed`: trace text, shas, run id, fault, policies; red on base); (b)
`Every_evaluator_fault_spells_its_numbers_as_the_trace_does` (`Step` on the rigs `EvaluatorLimitTests.cs:188`, `EvaluatorRuleTests.cs:399-430`, plus a `0.00` close under `capital_fraction` and a Long `-0.5`; red on
base: `0,00`, `-0,5`); (c) `A_backtest_over_a_stored_dataset_records_the_same_run_under_nl_BE_and_fr_BE` (rig `BacktestDeterminismTests.cs:67-96`, faulting on `close * 1.5 / (close - close)`, the `strategy_run` row
read back; red on base); (d) `No_id_or_canonical_text_moves_under_another_culture` (version ids, manifest, both models, run and promotion ids, `RefereeFeedback.Text`: green on base; `KlineNormaliser.Normalise` with
a `.` time separator: red on base, `00.00.00Z` — fixed in this unit); (e) `Invariant_globalization_stays_on_unless_the_culture_tests_pass_without_it` (reads the switch every shipped project and test runs with;
fails if it is off, naming (a)–(d) as what must pass first). Traced by reading, NOT run: the main checkout's Release build predates `f8b237d`. Mutants to watch red and quote: (i) `StrategyInterpreter.cs:194` back
to `{a.Number}` ⇒ (a)–(c) red; (ii) `StrategyParser.cs:1150` without its culture ⇒ (a)–(d) red, nothing else.
Seen, not in this unit: `ForwardRuns.cs:402,509-510` put decimals into the paper runner's no-trade words in the ambient culture (not hashed), for `U-timeframe-b`, which rewrites that file.
Gate and report per `docs/HOW-WE-BUILD.md`: rebase on `main` first; `--no-incremental` Release build 0 warnings; three suites 0 failed; touched classes 3×;
names vs `main` 0 removed (both set sizes printed); `## Report` ≤ 20 lines appended here. No push, no merge; touch nothing in `docs/briefs/` but this file.
