# U-language-in-home — the strategy language and its three worked programs reach the role that must write one
**Arrow closed:** research → create a candidate (`docs/PRINCIPLES.md` § loop). **Blocker demonstrated (read-only survey 2026-09-20, SOURCE):** nothing in `src/`
puts the language's grammar or an example program where a Research turn can read it — `AGENTS.md` names `trade backtest --strategy …` once (`WorkspaceBuilder.cs:286`),
`trade schema` describes arguments only (`GatewaySchema.cs:255-275`), and the three day-one programs are test fixtures (`tests/TradeAgent.UnitTests/Strategies/*.strategy`;
`docs/STRATEGY-LANGUAGE.md:166-233`). A fresh role would invent the syntax from parser refusals. **Observable result:** a fresh Research home holds the language
reference and the three programs, `AGENTS.md` points at them, and a shipped example backtests through the pipe. Not the money path. No schema rung.
Read first: `docs/STRATEGY-LANGUAGE.md` in full; `WorkspaceBuilder.cs:32-82,120-136,250-310` (the home layout, `AGENTS.md` regenerated every start, the mission
text); `ToolDeployer.cs:41-79` (how the app ships a file into a home with its hash); `Backtests.cs:209-260` (`Resolve`: the program must be inside the role's own
home); `tests/TradeAgent.UnitTests/Strategies/`; `MissionInstructionsTests.cs`. Rebase onto `main` first; resolve conflicts yourself.
Items, one commit each with a one-sentence message:
1. Ship the language: `docs/STRATEGY-LANGUAGE.md` becomes a content file of `TradeAgent.AgentRuntime` (copied to the output directory), and `WorkspaceBuilder`
   writes it as `<roleHome>/research/STRATEGY-LANGUAGE.md` on every start, app-owned — regenerated like `AGENTS.md`, a role's edit overwritten, and the file's
   first line says so. The doc stays the single source: no second copy of the grammar anywhere in code.
2. Ship the three programs: the fixtures move to `src/TradeAgent.AgentRuntime/Strategies/*.strategy` as the ONE copy (the unit tests that read them now read the
   shipped files; `docs/STRATEGY-LANGUAGE.md:166` names the new home), written to `<roleHome>/strategies/examples/` on every start, app-owned, byte for byte.
3. `AGENTS.md` (`WorkspaceBuilder.cs:286` region): three lines — where the reference is, where the examples are, and that `trade backtest --strategy
   strategies/examples/ma-crossover.strategy --dataset <id>` is the first thing a role can run; `trade schema`'s `backtest` entry names the reference path.
Red-first tests: (a) `A_fresh_research_home_holds_the_language_reference_and_the_three_programs_byte_for_byte` (RED: absent); (b) `A_roles_edit_of_the_reference_is_
overwritten_at_the_next_start`; (c) `A_shipped_example_backtests_through_the_pipe_under_a_research_grant` (the `BacktestFor` path, the `VerdictOverPipeTests` fixture
shape; RED: the path does not exist in the home); (d) `The_programs_the_parser_tests_read_are_the_shipped_files` (one path, no second copy). Mutant to watch red and
quote: the examples written once and never refreshed → (b) red. Nothing removed or renamed; a fixture MOVED is recorded as moved, with both paths.
Gate and report as `docs/HOW-WE-BUILD.md` (`--no-incremental` Release 0 warnings; three suites 0 failed; classes 3×; names 0 removed; `## Report` ≤ 20 lines here).
No push, no merge; touch nothing in `docs/briefs/` but this file. Off-limits (the envelope leg's): `AllocationStore.cs`, `TradingGateway.cs`, `Database.cs`,
`DashboardView.cs`, `SettingsView.cs`, `MissionLoop.cs`, `DailyReports.cs`.

## Report
Tip `df94d08`, rebased onto `main` at `c17d5c1`, 3 commits replayed, no conflict. Gate there: `dotnet build TradeAgent.sln -c Release --no-incremental` → **0 Warning(s), 0 Error(s)**, 19 projects; Unit **1181/0/0**, Fault **393/0/0**, Integration **687 passed / 0 failed / 1 skipped** (688, 11m08s). Touched classes 3×: 55/55 unit, 11/11 `BacktestOverPipeTests`, identical each run. Test names `main`→tip **1914 → 1919, 0 removed**.
1. DONE — `docs/STRATEGY-LANGUAGE.md` is `Content` of `TradeAgent.AgentRuntime`, present after `dotnet publish -r win-x64 --self-contained` (what the installer stages); `ResearchLibrary.Write`, called from `WorkspaceBuilder.Build`, writes `<roleHome>/research/STRATEGY-LANGUAGE.md`, whose body `diff`s IDENTICAL to the doc. No second copy of the grammar in code.
2. DONE — fixture MOVED (`git mv`, recorded R): `tests/TradeAgent.UnitTests/Strategies/*.strategy` → `src/TradeAgent.AgentRuntime/Strategies/*.strategy`, the one copy; `DayOneStrategyTests` and `DayOneEvaluationTests` read it through `tests/Shared/DayOnePrograms.cs`, whose name list IS `ResearchLibrary.Programs`. `cmp` against a built home: identical, all three. Doc line 166 names the new home.
3. DONE — three lines in `AGENTS.md` (reference, examples, `trade backtest --strategy strategies/examples/ma-crossover.strategy --dataset <id>`), every path interpolated from `ResearchLibrary`; `GatewaySchema`'s `backtest` entry names both.
RED on the base, quoted: (a) and (b) `the Research home has no language reference at …/research/STRATEGY-LANGUAGE.md`; (d) `Expected: ["src/TradeAgent.AgentRuntime/Strategies/ma-crossove"···] Actual: ["tests/TradeAgent.UnitTests/Strategies/ma-crossover"···]`; (c) `the Research home has no worked program at …/workspace/research/strategies/examples/ma-crossover.strategy`.
MUTANT `if (File.Exists(Path.Combine(examples, name))) continue;` → (b) red, `Assert.Equal() Failure: Collections differ` at `ResearchLibraryTests.cs:106`; put back, green.
DEVIATIONS: the three programs carry no app-owned banner — byte for byte wins, so ownership of `strategies/examples/` is stated in the reference's first line, in `AGENTS.md` and in `trade schema` instead. One guard added beyond the brief's four, `The_mission_and_the_schema_name_the_reference_the_examples_and_the_first_backtest` (RED first: `Assert.Contains() Failure: Sub-string not found`), so the path the app writes and the path it names cannot drift.
NOT DONE: no Windows box, no provider, no venue, no order, no push, no merge. NOTED, NOT FIXED: `MaterialScanner` walks `research/` and `strategies/`, so these five app-written files record as `MaterialOrigin.Agent` ("so the agent produced it") — the reading it already applies to the app-owned `in/`; out of scope for this unit.
