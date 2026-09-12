# U-runner-1 — the strategy program: parsed, validated, frozen and identified; text in, a typed program or a refusal out
Read `docs/HOW-WE-BUILD.md`, `CLAUDE.md`, `docs/COUNCIL.md:126-166` (rule 8; "The runner and the referee"; "The strategy language,
v1" — Astra's specification, the contract this unit implements), then `Core/Sha256Hex.cs`, `WorkspaceBuilder.cs:275-280` (the
mission already tells the agent to write strategies in `strategies/`), `Data/DatasetReader.cs:6-12` (the bar record). Branch
`u-runner-1`, worktree `~/Projects/ai-trading-software-for-mihael-worktrees/U-runner-1`, rebased onto `main` first. Every item ships
with a test that was RED before it and ONE mutant watched going red, quoted. No box, no ATAS, no money, no gateway. **No schema number.**

**Why.** `grep -rni "backtest\|indicator\|parser" src` finds prose only: no parser, no typed program, no identity for a strategy
exists; the agent is told to write strategies as text, so today a strategy is a file nobody can execute, validate or name. Rule 8:
a promoted, immutable version executes through the app's runner with no inference on the signal path — this is the program half;
`U-runner-2` evaluates, `U-runner-3` backtests and persists. `RunnerSpeedProbeTests.cs` is the TEST runner: namespace `Core.Strategy`.

1. **The language and its typed AST** (new `src/TradeAgent.Core/Strategy/`): typed constants; indicator expressions (SMA, EMA,
   ATR, RSI, rolling high/low, a session opening-range accumulator, each with a period); ORDERED rules, exits before entries, whose
   conditions are expressions (arithmetic, comparison, Boolean, crossing, bounded history such as `close[1]`); one spot instrument,
   long/flat, one position; sizing (fixed quantity, fraction of capital, equity-risk fraction over stop distance); stops (fixed,
   percentage, entry-time ATR distance), optional target, maximum holding bars; time filters (versioned timezone, weekdays, entry
   windows, opening-range interval, scheduled session exit). Everything on COUNCIL's "Never:" line is NOT representable. The
   concrete syntax is yours — line-oriented, one declaration per line, an expression grammar — specified in `docs/STRATEGY-
   LANGUAGE.md` (≤ 150 lines: grammar, indicator semantics and initialisation, the limits, item 5's programs verbatim), written so
   a cheap model can produce it. RED: no `StrategyProgram` type; item 5's programs do not parse. Mutant (an unknown indicator
   name accepted as a node): a misspelt `smaa(20)` parses.
2. **Parse-time refusals, each naming the line:** a period ≤ 0, an undeclared constant, a type mismatch, undefined or ambiguous
   sizing, a lookback, node count, rule count or history depth over the limit (every limit a named constant in ONE place,
   `StrategyLimits`), risk-based sizing with no stop. RED: `sma(0)` parses. Mutant (`period > 0` → `>= 0`): red.
3. **Canonical form and identity:** a canonical serialisation (typed, ordered, whitespace- and comment-free) and `StrategyId =
   Sha256Hex.Of(canonical program + "\n" + parameters + "\n" + manifest)`, the manifest naming the language and indicator-semantics
   versions; the source text retained beside the canonical form. RED: two spellings of one program (whitespace, a comment,
   constant order) get two ids → one. Mutant (hash the source text): a trailing space changes the id.
4. **Warm-up is explicit and computed:** `Program.WarmUpBars` = the maximum declared lookback over indicators and history
   references, nested included, stated on the frozen program; `U-runner-2` refuses to evaluate before it. RED: a 20-bar SMA
   program reports warm-up 1. Mutant (max lookback − 1): one bar short, red.
5. **The three day-one programs** (`docs/COUNCIL.md:164-166`: an MA crossover with fixed sizing and a stop; an opening-range
   breakout with ATR risk sizing and a time stop; an RSI mean reversion with a profit exit and a maximum holding time) as fixtures
   under `tests/TradeAgent.UnitTests/Strategies/`: each parses, round-trips through the canonical form and hashes to a golden
   `StrategyId` pinned in the test. `CONTRACTS.md` gains the program's shape and the identity rule.
Not this unit: evaluation on bars, indicator values, intents, storage, the report, the relay, promotion, the referee.
Gate: `dotnet build TradeAgent.sln -c Release --no-incremental` → 0 warnings; the three test projects in Release to a file → 0
failed; touched classes 3×; a `Timing` red re-run alone 3×, never loosened. Commit per item, one sentence, no trailers.
Append `## Report` (≤20 lines): tip sha, gate counts, one line per item with its RED and mutant, what you did NOT do.
