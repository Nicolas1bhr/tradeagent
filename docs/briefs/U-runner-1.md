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

## Report

**Gated tip `b5f60c1`** (this report commit is docs-only on top), rebased onto `main` `62639ed`, no conflicts, `main` unmoved since. Six commits: one per item, plus one that turned two test files' literal control bytes back into C# escapes — the fuzz inputs had reached disk as real NUL / 0x01-0x03 / BOM bytes, which made those files grep as BINARY and hid fourteen test names from the name diff; the gate caught it, both files are now `Unicode text, UTF-8 text`, and the diff sees all forty.
**Gate**, all Release, output to files in the worktree, deleted before this commit: `dotnet build TradeAgent.sln -c Release --no-incremental` → **`Build succeeded. / 0 Warning(s) / 0 Error(s)`**, 17 projects. The five touched classes (`--filter FullyQualifiedName~Strategy`) 3× → **`Failed: 0, Passed: 77`** every run. `--no-build -c Release`: Unit **`Failed: 0, Passed: 706`**, Fault **`Failed: 0, Passed: 277`**, Integration **`Failed: 0, Passed: 627, Skipped: 1`**. Nothing went red, so no `Timing` re-run; `ps -eo pid,ppid,pcpu` before the suites found no orphaned test process (only `WindowServer` at 42%). Names vs `main`: **nothing removed**, 40 added, sets **1287 → 1327**.
**1 — the language and its typed AST** (`Core/Strategy/`, `docs/STRATEGY-LANGUAGE.md`, 149 lines). RED `expected a program, got: program: the parser is not implemented` (23 failed / 0 passed). Mutant (`if (!TryIndicator(head, out var kind)) kind = IndicatorKind.Sma;` — an unknown name becomes a node): `expected a refusal, got a program`, 1 failed / 22 passed. COUNCIL's "Never:" line is UNREPRESENTABLE, not filtered — `Expr`'s constructor is `private protected`, so the node kinds are closed to the assembly — and 19 attempts to spell it (an import, a `def`, a `for`, a `while`, `now()`, `random()`, a model call, `train`, a shell call, a file read, an http read, a second instrument in an expression and on the instrument line, `short`, `leverage 3`, `capital_fraction 2.5`, `risk_fraction 1.5`, an indicator over itself) are each refused naming the line.
**2 — refusals that name the line** (`StrategyTypes`; every limit a constant in `StrategyLimits`). RED `sma(close, 0)` → `expected a refusal, got a program` (18 failed / 7 passed). Mutant (`period <= 0` → `period < 0`, COUNCIL's `> 0` read as `>= 0`): 5 failed / 20 passed on the five period cases.
**3 — canonical form and identity.** RED, with the canonical form still the source text: `Assert.Equal() Failure: Strings differ / ↓ (pos 11) / Expected: "instrument BTCUSDT\nconst fast = 20…" / Actual: "instrument   BTCUSDT   \n\n   const fast…"` (3 failed / 2 passed). Mutant (hash the source text): 3 failed / 2 passed, `Expected: b8c939f4…, Actual: bd977b8d…`. Four spellings of one program — spacing, comments, reordered constants, upper case with CRLF and a BOM, `1.50` for `1.5` — reach ONE id; fourteen changes of meaning each reach another.
**4 — warm-up** (`StrategyWarmUp`, one place; written into the canonical form, so the id covers it). RED `Expected: 20 / Actual: 1` on the 20-bar average (12 failed / 2 passed). Mutant (the maximum lookback − 1): 14 failed / 0 passed, `Expected: 15 / Actual: 14`.
**5 — the three day-one programs** as fixtures under `tests/TradeAgent.UnitTests/Strategies/`, byte for byte what the document prints (asserted). RED: the three placeholder ids (4 failed / 6 passed — every hand-written canonical form, warm-up, sizing, stop, target, hold and time filter passed first run). Mutant (the canonical form drops its `instrument` line): 3 failed / 7 passed, all three goldens. **Goldens:** crossover `8873b58693fc00af44a0a630ddcad9a7b6c3b61dcbbbb45f8ea6373e6b14edaf`, opening-range breakout `88f6586a99138dde124993655df33f074ea5711fb1b712525f59e495247386a0`, RSI mean reversion `eeb614304febec859879ee3f747d91a44dc899d525eb294a37454e803a806548` — each recomputed OUTSIDE the build from COUNCIL's formula, `sha256(canonical + "\n" + parameters + "\n" + manifest)`, and equal.
**Where the brief and `COUNCIL.md:126-166` differ, the brief won, and here it is.** COUNCIL's account-state readings (strategy capital, equity, position, average fill price, pending-order state, bars since entry) are NOT in the expression grammar: item 1 lists arithmetic, comparison, Boolean, crossing and bounded history, and those readings are evaluator state rather than parse-time vocabulary. The timezone is an allowlist of six zones in `StrategyLimits` rather than an OS lookup — `InvariantGlobalization` makes `FindSystemTimeZoneById` answer differently per platform, and a promoted id must not depend on the machine that hashed it; the calendar version is in the manifest, and there is no holiday table yet.
**NOT done:** no evaluation, no indicator values, no intents, no bar read, no dataset, no storage, no schema, no trace, no report, no relay, no promotion, no referee (`U-runner-2`, `U-runner-3`); no per-event operation budget and no state-size limit (`U-runner-2` adds those to the same `StrategyLimits`); nothing reads `workspace/strategies/` and no verb or pipe op reaches the parser; no box, no ATAS, no money, no gateway, no orders; `BUILD-STATUS.md` untouched, which is the manager's pass.
