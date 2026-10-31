using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE GOLDEN VECTORS — WHAT THE EVALUATOR OUTPUTS OVER FIXED BARS, PINNED TOGETHER WITH THE TWO NUMBERS
/// THAT DECLARE WHAT IT MEANS (<c>U-evidence-identity</c>).
///
/// <para><b>Why they exist.</b> A promotion's standing is withdrawn when <see cref="Referee.EvaluatorVersion"/>
/// or <see cref="StrategyVersions.Manifest"/> is not the one it was taken under, and on nothing else that a
/// build can change — the app's release number no longer withdraws anything, because it moved on every
/// update whether or not a program's meaning had. That makes the two numbers a DECLARATION, and a
/// declaration nobody checks is a promise. These vectors check it: a change to what a program does over
/// these bars, or to what that is judged to be worth, with neither number moved, fails here and says which
/// to bump. A bump then withdraws every verdict taken under the old semantics, which is the point — the
/// evidence no longer means what it meant.</para>
///
/// <para><b>What is pinned, per vector.</b> The SHA-256 of the trace (<see cref="BacktestTrace.Sha256"/>, the
/// figure every <c>strategy_run</c> row records), the SHA-256 of the metrics and the closed trades as
/// <see cref="MetricsText"/> spells them, and the answers of <see cref="ScoringPolicyV1"/> and
/// <see cref="PaperPolicyV1"/> over that run — the backtest, the metrics and the scoring, the three
/// parts of <c>backtest=3;metrics=1;scoring=1</c>.</para>
///
/// <para><b>What every vector declares.</b> Its program, the series it runs over, and all four numbers of
/// its execution model. Nothing is left to a default — a research default or a venue fee edited elsewhere
/// must not be able to move a pin that is about the evaluator.</para>
///
/// <para><b>The bars are a fixture file and never generated</b>
/// (<c>tests/TradeAgent.UnitTests/Golden/evaluation-bars.json</c>, LF by <c>.gitattributes</c>' <c>*.json</c>
/// rule): a golden series is only a tripwire if its input cannot move either, so the file's own hash is
/// pinned and checked first, with a message of its own.</para>
///
/// <para><b>Culture-free, and proved so</b> (<c>U-invariant-traces</c>). Every trace line is built with
/// the invariant culture, and every fault's and no-trade's words are made through <see cref="TraceText"/>,
/// which spells a decimal as the line does — invariant, trailing zeros gone — and cannot be handed words
/// built in the thread's culture. Three vectors fault, two of them on a decimal with a trailing zero
/// (<c>1026.70</c>, <c>120.50</c>), and
/// <see cref="Every_golden_vector_writes_the_same_bytes_under_nl_BE_and_fr_BE_number_formats"/> runs every
/// vector again under nl-BE's and fr-BE's number formats, so a pin here means the same thing on a machine
/// set to either. Under <c>backtest=1</c> those two faults were written in the machine's culture with the
/// decimal's own scale — <c>1026,70</c> on a Belgian machine, <c>1026.70</c> on CI — and that change in
/// what the backtest outputs is what moved the number to 2. The fifteen vectors that do not fault on a
/// decimal kept every pin they had.</para>
///
/// <para><b>Re-pinned under <c>backtest=3</c></b> (<c>U-backtest-parity</c>): every order now fills on the minute paper
/// fills it (two minutes after the minute it was learnt on), protection rests from two minutes after the entry's fill,
/// relative levels are measured from the fill and the maximum hold is a market close in flight. Thirteen vectors moved —
/// every one whose run reached an order's fill: all but <c>size-rounds-to-nothing</c>, <c>operation-budget-fault</c> and
/// the two <c>-fault</c> vectors, which never fill anything — and <c>history-arithmetic-stop-and-target</c> went from
/// Met / MetOnHistory to NotProfitable on both policies: under 2 its edge was the minute paper never trades on.</para>
/// </summary>
public class EvaluationGoldenVectorTests(ITestOutputHelper log)
{
    /// <summary>
    /// THE EVALUATION SEMANTICS EVERY PIN BELOW WAS COMPUTED UNDER. Moving either constant in the product
    /// without re-pinning fails <see cref="Changed_evaluation_output_without_a_version_bump_fails_the_golden_vectors"/>
    /// on its first check; re-pinning means computing the vectors again under the new numbers and writing
    /// both numbers and every pin here, in the commit that moved them.
    /// </summary>
    const string PinnedEvaluator = "backtest=3;metrics=1;scoring=1";

    /// <inheritdoc cref="PinnedEvaluator"/>
    const string PinnedManifest = "language=1;indicators=1;calendar=1";

    /// <summary>The SHA-256 of the fixture file, its line endings read as LF.</summary>
    const string FixtureSha256 = "62069406c57c8c45e0decb2dfa33634437297f84b3fe56569cf4e3c0af57db9d";

    /// <summary>The dataset id every vector's request names. It reaches the run id only, which is not pinned.</summary>
    const long GoldenDatasetId = 1;

    /// <summary>
    /// ONE VECTOR: a program, the series it is run over, and its execution model's four numbers, every one
    /// declared. <paramref name="Limits"/> is null for the evaluator's own limits — which is every vector
    /// but the one that measures the operation budget.
    /// </summary>
    sealed record Vector(
        string Name, string Series, string Program,
        decimal Fees, decimal Slippage, decimal Increment, decimal Capital,
        EvaluationLimits? Limits = null)
    {
        public ExecutionModel Model =>
            ExecutionModel.Declare(Fees, Slippage, Increment, Capital) is { Model: { } m }
                ? m
                : throw new InvalidOperationException($"vector {Name} declares an execution model that is refused");
    }

    /// <summary>What one vector produced: the two hashes and the two policies' answers.</summary>
    sealed record Pin(string Name, string Trace, string Metrics, string V1, string Paper)
    {
        public string AsCode =>
            $"        new(\"{Name}\", \"{Trace}\",\n            \"{Metrics}\", {Reason(V1)}, {Reason(Paper)}),";

        static string Reason(string r) => r switch
        {
            PromotionReason.Met => "PromotionReason.Met",
            PromotionReason.MetOnHistory => "PromotionReason.MetOnHistory",
            PromotionReason.PrecedesTheFreeze => "PromotionReason.PrecedesTheFreeze",
            PromotionReason.DidNotComplete => "PromotionReason.DidNotComplete",
            PromotionReason.NoTrade => "PromotionReason.NoTrade",
            PromotionReason.NotProfitable => "PromotionReason.NotProfitable",
            _ => $"\"{r}\""
        };
    }

    static readonly Vector[] Vectors =
    [
        // A moving-average crossover with fixed sizing and a percentage stop, its bounds declared.
        new("sma-crossover-percent-stop", "trend-utc", """
            instrument BTCUSDT
            timeframe 1m
            data_freshness 2m
            max_decision_age 60s
            const fast = 5
            const slow = 20
            indicator fastma = sma(close, fast)
            indicator slowma = sma(close, slow)
            size fixed 2
            stop percent 1.5
            exit when crosses_below(fastma, slowma)
            entry when crosses_above(fastma, slowma)
            """, Fees: 0.001m, Slippage: 0.0005m, Increment: 0.001m, Capital: 10_000m),

        // Two exponential averages, a fraction of capital, and a percentage target.
        new("ema-trend-capital-fraction-target", "trend-utc", """
            instrument BTCUSDT
            indicator fast = ema(close, 8)
            indicator slow = ema(close, 21)
            size capital_fraction 0.5
            target percent 2
            exit when fast < slow
            entry when crosses_above(fast, slow)
            """, Fees: 0.0008m, Slippage: 0.0002m, Increment: 0.0001m, Capital: 5_000m),

        // An RSI turning up from oversold, with a history reference and a maximum hold.
        new("rsi-reversion-max-hold", "trend-utc", """
            instrument BTCUSDT
            indicator r = rsi(close, 14)
            size fixed 1
            max_hold_bars 15
            exit when r > 65
            entry when r < 35 and r > r[1]
            """, Fees: 0.001m, Slippage: 0m, Increment: 0.01m, Capital: 10_000m),

        // A channel breakout: highest and lowest one bar back, an ATR stop and equity-risk sizing.
        new("channel-breakout-atr-stop-risk", "trend-utc", """
            instrument BTCUSDT
            indicator hi = highest(high, 20)
            indicator lo = lowest(low, 10)
            size risk_fraction 0.02
            stop atr 2 14
            exit when close < lo[1]
            entry when close > hi[1]
            """, Fees: 0.00075m, Slippage: 0.0003m, Increment: 0.001m, Capital: 20_000m),

        // ATR as a value in a rule, the volume series, not / or / and, and a fixed stop and target.
        new("atr-volume-filter-fixed-levels", "trend-utc", """
            instrument BTCUSDT
            indicator a = atr(10)
            indicator v = sma(volume, 5)
            size fixed 1
            stop fixed 96
            target fixed 110
            exit when close < open and a > 1.2
            entry when not (close < open) and (volume > v or a < 0.8)
            """, Fees: 0.0005m, Slippage: 0.0001m, Increment: 0.01m, Capital: 10_000m),

        // History references twelve bars deep inside arithmetic, a constant, and a stop and a target.
        new("history-arithmetic-stop-and-target", "trend-utc", """
            instrument BTCUSDT
            const k = 0.5
            size fixed 3
            stop percent 3
            target percent 1.5
            exit when (close - close[3]) / close[3] * 100 < -k
            entry when close > close[1] and close[1] > close[2] and (close - close[12]) != 0
            """, Fees: 0.001m, Slippage: 0.0005m, Increment: 0.001m, Capital: 10_000m),

        // A 120-bar warm-up: nothing is asked of the program before it, across a gap.
        new("long-warm-up-max-hold", "trend-utc", """
            instrument BTCUSDT
            indicator longma = sma(close, 120)
            indicator e = ema(close, 30)
            size capital_fraction 0.25
            max_hold_bars 40
            exit when crosses_below(e, longma)
            entry when crosses_above(e, longma) or close > longma * 1.02
            """, Fees: 0.001m, Slippage: 0.0002m, Increment: 0.001m, Capital: 10_000m),

        // Two exits and two entries in declared order; a stop and a target so close to the entry that
        // one bar's range touches both — the stop wins, which is the conservative ordering a bar without
        // intrabar order allows — and a maximum hold.
        new("ordered-rules-stop-beats-target", "trend-utc", """
            instrument BTCUSDT
            indicator s = sma(close, 10)
            indicator h = highest(close, 5)
            size fixed 1
            stop percent 0.3
            target percent 0.3
            max_hold_bars 8
            exit when close < s * 0.99
            exit when crosses_below(close, s)
            entry when close >= h
            entry when crosses_above(close, s)
            """, Fees: 0.0004m, Slippage: 0.0001m, Increment: 0.001m, Capital: 10_000m),

        // A size that rounds down to nothing at a whole-unit increment: every signal is a no-trade.
        new("size-rounds-to-nothing", "trend-utc", """
            instrument BTCUSDT
            indicator f = ema(close, 5)
            size capital_fraction 0.001
            exit when close < f
            entry when crosses_above(close, f)
            """, Fees: 0.001m, Slippage: 0m, Increment: 1m, Capital: 10_000m),

        // A fixed size the declared capital cannot pay for: every fill is a no-trade. Its two entries
        // cross a four-bar mean that the flat stretches make EQUAL to the close the bar before, so both
        // crossings' "at or below" and "at or above" decide a signal here.
        new("capital-cannot-pay", "trend-utc", """
            instrument BTCUSDT
            indicator f = sma(close, 4)
            size fixed 500
            exit when close < f
            entry when crosses_above(close, f)
            entry when crosses_below(close, f)
            """, Fees: 0.001m, Slippage: 0.0005m, Increment: 0.001m, Capital: 10_000m),

        // An event over a tightened operation budget: a defined fault, and the run halts there.
        new("operation-budget-fault", "trend-utc", """
            instrument BTCUSDT
            indicator s = sma(close, 8)
            size fixed 1
            exit when close < s
            entry when close > s and (close - close[1]) * (close - close[2]) * (close - close[3]) > 0
            """, Fees: 0.001m, Slippage: 0m, Increment: 0.001m, Capital: 10_000m,
            Limits: EvaluationLimits.Of(16, StrategyLimits.MaxStateBytes)),

        // An opening-range breakout in New York: the range, an entry window, a session exit, Mondays
        // only, ATR risk sizing and an ATR stop.
        new("new-york-opening-range-breakout", "new-york-two-days", """
            instrument BTCUSDT
            timezone America/New_York
            opening_range 09:30-10:00
            entry_window 10:00-11:30
            session_exit 12:30
            weekdays mon
            indicator orh = opening_range_high()
            indicator orl = opening_range_low()
            size risk_fraction 0.001
            stop atr 1.5 14
            exit when close < orl
            entry when crosses_above(close, orh)
            """, Fees: 0.001m, Slippage: 0.0003m, Increment: 0.001m, Capital: 25_000m),

        // A New York mean reversion on Tuesdays and Wednesdays, in two entry windows, out by 13:30 — so
        // Monday's signals are refused by the weekday filter and Tuesday's are taken.
        new("new-york-two-window-reversion", "new-york-two-days", """
            instrument BTCUSDT
            timezone America/New_York
            weekdays tue, wed
            entry_window 09:45-10:30
            entry_window 11:00-12:00
            session_exit 13:30
            indicator r = rsi(close, 7)
            indicator lo = lowest(close, 30)
            size fixed 1
            target percent 0.8
            exit when r > 60
            entry when r < 35 and close <= lo * 1.002
            """, Fees: 0.0006m, Slippage: 0.0002m, Increment: 0.01m, Capital: 10_000m),

        // A short opening range, re-entered from below, with a maximum hold and a crossing negated.
        new("new-york-range-reentry", "new-york-two-days", """
            instrument BTCUSDT
            timezone America/New_York
            opening_range 09:30-09:45
            entry_window 09:45-12:00
            indicator orh = opening_range_high()
            indicator orl = opening_range_low()
            indicator m = sma(close, 10)
            size capital_fraction 0.3
            max_hold_bars 30
            exit when close > orh
            entry when not crosses_below(close, orl) and close < m and close > orl
            """, Fees: 0.0008m, Slippage: 0.0002m, Increment: 0.001m, Capital: 10_000m),

        // London across the change to summer time: an entry window and a session exit on local clocks.
        // The window is the first half hour AFTER the change — 02:00-02:29 BST is 01:00Z-01:29Z, while
        // a calendar that moved the change, or ignored it, would open it half an hour elsewhere or never
        // — and 03:30 BST is 02:30Z; the exit rule is rare enough that the session exit closes the trade.
        new("london-summer-time-window", "london-dst", """
            instrument BTCUSDT
            timezone Europe/London
            entry_window 02:00-02:30
            session_exit 03:30
            indicator f = sma(close, 3)
            indicator s = sma(close, 9)
            size fixed 1
            exit when f < s * 0.97
            entry when crosses_above(f, s)
            """, Fees: 0.001m, Slippage: 0.0001m, Increment: 0.001m, Capital: 10_000m),

        // A division by zero under a dividend with a trailing zero: `close * 10` is 1026.70 on the first
        // bar, and the fault names it the way the trace spells every number — 1026.7, never 1026.70 or
        // 1026,70 — so this pin is the same on a machine set to any culture.
        new("zero-divisor-under-a-trailing-zero-fault", "trend-utc", """
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close * 10 / (close - close) > 0
            """, Fees: 0.001m, Slippage: 0m, Increment: 0.001m, Capital: 10_000m),

        // Risk sizing under a fixed stop above every close in the series: the first entry has no risk
        // distance, and the fault names the stop and the close as the trace would — 120.5 and 102.67.
        new("risk-sizing-under-a-stop-above-every-close-fault", "trend-utc", """
            instrument BTCUSDT
            size risk_fraction 0.01
            stop fixed 120.50
            exit when close < 1
            entry when close > 0
            """, Fees: 0.001m, Slippage: 0m, Increment: 0.001m, Capital: 10_000m)
    ];

    /// <summary>
    /// THE PINS — computed under <see cref="PinnedEvaluator"/> and <see cref="PinnedManifest"/>. When the
    /// test fails it prints this whole block as it now computes, ready to paste in the commit that bumps.
    /// </summary>
    static readonly Pin[] Pins =
    [
        new("sma-crossover-percent-stop", "3715a818042420b56c760af3918ff1b43c26e4e4199ec46b683e9ec107ea3b42",
            "0f2ab9bd6f428e337efb056f74af10f8fb9b12dd0e02db611e35e93b8d0d5578", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("ema-trend-capital-fraction-target", "63ce8366f78f63f5f0ded2b538ecce44c7359cf2fb106294dbd4f96778eba7f8",
            "1220ded8614f052ffc3ad5b1040c11b4749c3f34e861435a549e8c2e35c690d5", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("rsi-reversion-max-hold", "d3db218fb5ac899bae3f43fad6e0e886076dd095a6c0ef822902dc63d10dab2a",
            "31005084a3d79bd00b2c8f748c76a7332dcad70bf16fd3c07519dfa85757c28e", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("channel-breakout-atr-stop-risk", "e43bf3cd300dd56123ee2461f1a88511664daea6143c28a9ac464152391783b5",
            "f5db6444d9d504b855199d5bd5e9f1fee6e33b067046536e945e3b3706b48bd8", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("atr-volume-filter-fixed-levels", "a9b1fa9cd7eaf000c03acfd75100417fb1829cfa768cba34c73eae59104a8c5d",
            "ec7ab078b23d8737cc74844e5743bb00b973cfab08bcffa6116b0d814ad3bdfe", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("history-arithmetic-stop-and-target", "a60e2d8942f24187e1bc84a291cd64b4666830aaff25b28caed8592ad536709a",
            "d4197ce971802812b7c442cb1c34bdcaf1e0eb5fddfdaece00dce86297272511", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("long-warm-up-max-hold", "353e69380c330db71ac2030df1e3613a2cb6792f48068e466bfe8c4e23854406",
            "746c4e504cb4a1e5e4516e5cdf4a669cc5654865aaff819c9d937c29a9df1f11", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("ordered-rules-stop-beats-target", "0bd5d4047c0b004b668ee06e84274e9919d25b984056805654f9605bd3d88fb8",
            "e545920b0709c95ee4f4860d74016fd50b14fd866ff45d55dc085e82b2d3a39c", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("size-rounds-to-nothing", "711979444155d0154fb6df00028bb6927131a5f169a37a08bc09335fff41814a",
            "643cc95a84ad1d2d8f15c9a3a4d59a063d907dee83ad717cb3ee24228ae3de15", PromotionReason.NoTrade, PromotionReason.NoTrade),
        new("capital-cannot-pay", "a7dd784a80f8ae7068094a5ccc36c63ac9f49d7a634f5b0e9211a51ff9a1e21d",
            "a798d4e28741b030a168d8076edead5b1ab9875f7cbe177e4f233eaa4e029e1c", PromotionReason.NoTrade, PromotionReason.NoTrade),
        new("operation-budget-fault", "39666aed151dce031200c84ea611d56acd95c7c870350f836d27da3d1e03f339",
            "be871dc2260d69987280d0f772796405fa179a731690a62f05508b979a29d319", PromotionReason.DidNotComplete, PromotionReason.DidNotComplete),
        new("new-york-opening-range-breakout", "71438e663c63453b386dc28d1f5a768069e7fa7e3f6d8624bdbc98deee79eb9f",
            "5d9b475df0838f512a47ca9f20d276dc99e6fa13ea50c407682dfd7225444de0", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("new-york-two-window-reversion", "7a1326ecd6bcd4117d21077bd7d2c054ef63aef1ea60ba552696ae7ac002fdd0",
            "5d701bb1474a68cccd45a1bc02fb713cab7fd80c98d3d4dce8b477d3f21dc8a7", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("new-york-range-reentry", "de505b4008947d870928401812b36858e7aac8371910763c0231e81888a5a8e5",
            "89c436833f5f9c582aae4845e7a3711a296f954be55f3f34223e6be154bf1522", PromotionReason.NotProfitable, PromotionReason.NotProfitable),
        new("london-summer-time-window", "a4d76eb05c25647fc2a2de9a897dac36c3ca53444b0df541a3e892a4d522f325",
            "fa32322a832690a0bb022a3b68b9feb93c0e235da385e7d55c04337f0897d82b", PromotionReason.Met, PromotionReason.MetOnHistory),
        new("zero-divisor-under-a-trailing-zero-fault", "d7720e8bfcf708177e9fe67dfd639bdae80575daaf03830543fa71d69369c5a3",
            "f2f987d0bbd47b20982605f3970de962212a0fece1c6c14c4d0ac04f90fb43ef", PromotionReason.DidNotComplete, PromotionReason.DidNotComplete),
        new("risk-sizing-under-a-stop-above-every-close-fault", "7eb8a46a76cb522552ff497716ea452d769cf4d6a0d3ab478396a732f89fd5b8",
            "859012137eabb253e71e7ab9aae8de300e18dd87a6511502855737a10ac45f62", PromotionReason.DidNotComplete, PromotionReason.DidNotComplete),
    ];

    // ---- the fixture ---------------------------------------------------------------------------

    sealed record Series(string Name, DateTimeOffset Start, IReadOnlyList<KlineBar> Bars);

    static string FixturePath() =>
        Path.Combine(DayOnePrograms.RepoRoot(), "tests", "TradeAgent.UnitTests", "Golden", "evaluation-bars.json");

    /// <summary>The fixture's text, read with LF whatever the checkout wrote. See <see cref="DayOnePrograms.Text"/>.</summary>
    static string FixtureText() => File.ReadAllText(FixturePath()).ReplaceLineEndings("\n");

    static IReadOnlyDictionary<string, Series> Load()
    {
        using var doc = JsonDocument.Parse(FixtureText());
        var all = new Dictionary<string, Series>(StringComparer.Ordinal);

        foreach (var s in doc.RootElement.GetProperty("series").EnumerateArray())
        {
            var name = s.GetProperty("name").GetString()!;
            var start = DateTimeOffset.Parse(s.GetProperty("start").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var bars = s.GetProperty("bars").EnumerateArray()
                .Select(b => new KlineBar(start.AddMinutes(b[0].GetInt32()),
                    b[1].GetDecimal(), b[2].GetDecimal(), b[3].GetDecimal(), b[4].GetDecimal(), b[5].GetDecimal()))
                .ToList();
            all.Add(name, new Series(name, start, bars));
        }

        return all;
    }

    // ---- one vector ----------------------------------------------------------------------------

    static StrategyProgram Parsed(Vector v)
    {
        var parse = StrategyParser.Parse(v.Program);
        Assert.True(parse.Ok, $"golden vector {v.Name} no longer parses: {parse.Why}");
        return parse.Program!;
    }

    static BacktestResult Run(Vector v, Series s) =>
        Backtest.Run(Parsed(v), new BacktestRequest(GoldenDatasetId, $"golden:{s.Name}", v.Model, s.Start, null),
            s.Bars, v.Limits);

    /// <summary>
    /// WHEN EVERY VECTOR'S VERSION IS TAKEN AS FROZEN: a day before its series begins, so the
    /// forward-evidence clause is met and the PERFORMANCE clauses decide <see cref="ScoringPolicyV1"/>'s
    /// answer.
    /// </summary>
    static DateTimeOffset FrozenAt(Series s) => s.Start.AddDays(-1);

    /// <summary>
    /// THE METRICS AND THE CLOSED TRADES AS ONE TEXT — every figure <see cref="BacktestMetrics"/> carries,
    /// named, in a fixed order, numbers through the invariant formatter the trace uses, an unknown as the
    /// dash it prints as; then every metric gap's words; then every closed trade. Spelled out field by
    /// field rather than reflected over, so a figure ADDED to the metrics is not, by itself, a change to
    /// the ones already pinned.
    /// </summary>
    static string MetricsText(BacktestResult run)
    {
        var m = run.Metrics;
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"outcome={run.Outcome};fault={run.FaultReason ?? BacktestMetrics.Dash};bars={m.Bars};")
            .Append(CultureInfo.InvariantCulture,
                $"exposure_bars={m.ExposureBars};signals={m.Signals};fills={m.Fills};no_trades={m.NoTrades};")
            .Append(CultureInfo.InvariantCulture, $"trades={m.Trades};wins={m.Wins};win_rate={Show(m.WinRate)};")
            .Append(CultureInfo.InvariantCulture,
                $"gross={Show(m.GrossPnl)};fees={Show(m.Fees)};net={Show(m.NetPnl)};")
            .Append(CultureInfo.InvariantCulture,
                $"max_drawdown={Show(m.MaxDrawdown)};final_equity={Show(m.FinalEquity)};")
            .Append(CultureInfo.InvariantCulture,
                $"missing_minutes={m.MissingMinutes};gaps={m.Gaps};faults={m.Faults};open_at_end={m.PositionOpenAtEnd}\n");

        foreach (var gap in m.Missing)
            text.Append("missing|").Append(gap.Field).Append('|').Append(gap.Why).Append('\n');

        foreach (var t in run.Trades)
            text.Append(CultureInfo.InvariantCulture,
                $"trade|{t.Ordinal}|{Iso(t.EntryBar)}|{Num(t.EntryPrice)}|{Iso(t.ExitBar)}|{Num(t.ExitPrice)}|")
                .Append(CultureInfo.InvariantCulture, $"{Num(t.Quantity)}|{t.Reason}|{Num(t.Fees)}|{Num(t.Pnl)}\n");

        return text.ToString();

        static string Show(decimal? d) => BacktestMetrics.Show(d);
        static string Num(decimal d) => BacktestMetrics.Show(d);
        static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    static Pin Computed(Vector v, Series s, BacktestResult run) => new(
        v.Name, run.Trace.Sha256, Sha256Hex.Of(MetricsText(run)),
        ScoringPolicyV1.Reason(run, FrozenAt(s)), PaperPolicyV1.Reason(run));

    /// <summary>Every vector run, in declared order, with what it computed.</summary>
    (Vector Vector, Series Series, BacktestResult Run, Pin Pin)[] RunAll()
    {
        var series = Load();
        var all = new List<(Vector, Series, BacktestResult, Pin)>();

        foreach (var v in Vectors)
        {
            Assert.True(series.TryGetValue(v.Series, out var s), $"vector {v.Name} names a series the fixture does not have: {v.Series}");
            var run = Run(v, s!);
            var pin = Computed(v, s!, run);
            all.Add((v, s!, run, pin));

            var exits = string.Join(",", run.Trades.GroupBy(t => t.Reason).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}x{g.Count()}"));
            log.WriteLine($"{v.Name,-38} bars {run.Metrics.Bars,3} trades {run.Metrics.Trades,2} [{exits}] "
                          + $"no-trades {run.Metrics.NoTrades} gaps {run.Metrics.Gaps} {run.Outcome} "
                          + $"net {BacktestMetrics.Show(run.Metrics.NetPnl)} v1 {pin.V1} paper {pin.Paper}");
        }

        return [.. all];
    }

    static string Repin((Vector Vector, Series Series, BacktestResult Run, Pin Pin)[] all, string fixtureSha) =>
        $"    const string FixtureSha256 = \"{fixtureSha}\";\n\n    static readonly Pin[] Pins =\n    [\n"
        + string.Join("\n", all.Select(a => a.Pin.AsCode)) + "\n    ];";

    // ---- the tests -----------------------------------------------------------------------------

    /// <summary>
    /// (b) CHANGED EVALUATION OUTPUT WITHOUT A VERSION BUMP FAILS THE GOLDEN VECTORS.
    ///
    /// <para>Three checks, in an order that makes each failure say one thing. The FIXTURE is the one that
    /// was pinned — a moved input is its own mistake. The pins were computed under the semantics this build
    /// DECLARES — a bump that did not re-pin is told to re-pin. And then every vector's output is what was
    /// pinned — a change with neither number moved is told to bump <see cref="Referee.EvaluatorVersion"/> (or
    /// the manifest) and re-pin in the same commit, and is printed the new pins to paste.</para>
    ///
    /// <para><b>RED through mutant (ii)</b>: one pinned sha edited, standing in for an evaluator that changed
    /// its output and nobody bumped.</para>
    /// </summary>
    [Fact]
    public void Changed_evaluation_output_without_a_version_bump_fails_the_golden_vectors()
    {
        var fixtureSha = Sha256Hex.Of(FixtureText());
        var all = RunAll();

        Assert.True(fixtureSha == FixtureSha256,
            $"the golden bar fixture {FixturePath()} hashes {fixtureSha} and was pinned at {FixtureSha256}. A golden "
            + "series is only a tripwire if its input cannot move: put the file back, or — if the change is meant — "
            + "say why in the commit and re-pin every vector over it:\n" + Repin(all, fixtureSha));

        Assert.True(Referee.EvaluatorVersion == PinnedEvaluator && StrategyVersions.Manifest == PinnedManifest,
            $"the evaluation semantics moved from {EvaluationSemantics.Of(PinnedEvaluator, PinnedManifest)} to "
            + $"{EvaluationSemantics.Current}, and the golden vectors are still pinned under the old ones. Set "
            + "PinnedEvaluator and PinnedManifest to the new numbers and re-pin in the same commit:\n"
            + Repin(all, fixtureSha));

        var pins = Pins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        var changed = all.Where(a => !pins.TryGetValue(a.Vector.Name, out var pinned) || pinned != a.Pin)
            .Select(a => a.Vector.Name)
            .ToList();

        Assert.True(changed.Count == 0,
            $"{changed.Count} golden vector(s) produced output other than what was pinned under "
            + $"{EvaluationSemantics.Current} — {string.Join(", ", changed)} — while neither number moved. What "
            + "a program does over these bars, or what it is judged to be worth, has changed: bump "
            + "`Referee.EvaluatorVersion` (or the manifest) and re-pin in the same commit. The bump is what "
            + "withdraws every verdict taken under the old semantics (`Promotions.Standing`), and a change that "
            + "did not bump would leave those verdicts standing on evidence the new build would not reproduce. "
            + "The pins as this build computes them:\n" + Repin(all, fixtureSha));
    }

    /// <summary>
    /// THE VECTORS COVER WHAT THEY CLAIM TO, read off the parsed programs and the runs rather than off their
    /// names — so a vector edited until it no longer exercises its feature fails here, not silently.
    ///
    /// <para>Every indicator kind, both crossings, history references on a series and on an indicator,
    /// all three sizings, every stop and target kind, two non-UTC zones, weekdays, entry windows (one
    /// program with two), an opening range, a session exit, a maximum hold and a warm-up past a hundred
    /// bars. And in the runs: a gap, every exit reason a trade can close on, a no-trade, a fault, and the
    /// policies answering with each performance clause — so a change to any of them moves some pin.</para>
    /// </summary>
    [Fact]
    public void The_golden_vectors_cover_every_indicator_crossing_history_stop_target_session_window_hold_warm_up_and_gap()
    {
        Assert.True(Vectors.Length >= 12, $"{Vectors.Length} golden vectors; the floor is twelve");
        Assert.Equal(Vectors.Length, Vectors.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Vectors.Select(v => v.Name).Order(StringComparer.Ordinal),
            Pins.Select(p => p.Name).Order(StringComparer.Ordinal));

        var programs = Vectors.Select(Parsed).ToList();
        var expressions = programs.SelectMany(p => p.Rules).SelectMany(r => Walk(r.Condition)).ToList();

        foreach (var kind in Enum.GetValues<IndicatorKind>())
            Assert.True(programs.Any(p => p.Indicators.Any(i => i.Kind == kind)), $"no vector declares {kind}");
        foreach (var direction in Enum.GetValues<CrossDirection>())
            Assert.True(expressions.OfType<CrossExpr>().Any(c => c.Direction == direction), $"no vector crosses {direction}");
        Assert.Contains(expressions, e => e is SeriesRef { Back: > 0 });
        Assert.Contains(expressions, e => e is IndicatorRef { Back: > 0 });
        foreach (var sizing in Enum.GetValues<SizingKind>())
            Assert.True(programs.Any(p => p.Sizing.Kind == sizing), $"no vector sizes by {sizing}");
        foreach (var stop in new[] { StopKind.FixedPrice, StopKind.Percent, StopKind.EntryAtr })
            Assert.True(programs.Any(p => p.Stop.Kind == stop), $"no vector stops by {stop}");
        foreach (var target in new[] { TargetKind.FixedPrice, TargetKind.Percent })
            Assert.True(programs.Any(p => p.Target.Kind == target), $"no vector targets by {target}");

        Assert.True(programs.Select(p => p.Time.TimeZone).Where(z => z != "UTC").Distinct().Count() >= 2,
            "the vectors read fewer than two zones other than UTC");
        Assert.Contains(programs, p => p.Time.Days != Weekdays.All);
        Assert.Contains(programs, p => p.Time.EntryWindows.Count >= 2);
        Assert.Contains(programs, p => p.Time.OpeningRange is not null);
        Assert.Contains(programs, p => p.Time.SessionExit is not null);
        Assert.Contains(programs, p => p.MaxHoldBars is not null);
        Assert.Contains(programs, p => p.WarmUpBars >= 100);

        var runs = RunAll();
        Assert.Contains(runs, r => r.Run.Trace.Of(BacktestEventKind.Gap).Any());
        Assert.Contains(runs, r => r.Run.Trace.Of(BacktestEventKind.NoTrade).Any());
        Assert.Contains(runs, r => r.Run.Trace.Of(BacktestEventKind.Fault).Any());
        foreach (var reason in Enum.GetValues<ExitReason>())
            Assert.True(runs.Any(r => r.Run.Trades.Any(t => t.Reason == reason)), $"no vector closes a trade on {reason}");

        foreach (var answer in new[]
                 {
                     PromotionReason.Met, PromotionReason.NotProfitable, PromotionReason.NoTrade,
                     PromotionReason.DidNotComplete
                 })
            Assert.True(runs.Any(r => r.Pin.V1 == answer), $"no vector is answered {answer} by scoring policy v1");
        Assert.Contains(runs, r => r.Pin.Paper == PromotionReason.MetOnHistory);
    }

    /// <summary>
    /// (a) EVERY GOLDEN VECTOR WRITES THE SAME BYTES UNDER nl-BE'S AND fr-BE'S NUMBER FORMATS
    /// (<c>U-invariant-traces</c>).
    ///
    /// <para>The pins above are computed under the invariant culture, which is the only one this build can
    /// construct (<see cref="Cultures"/>). A pin is only a declaration about the EVALUATOR if a machine set
    /// to another culture computes it too, so every vector is run again under each Belgian number format and
    /// must produce the same trace text, the same trace and metrics hashes, the same run id, the same fault
    /// and the same two policy answers as the invariant run — which is the run the pins hold.</para>
    ///
    /// <para><b>RED on the base</b>: the two vectors that fault on a decimal wrote it in the thread's culture —
    /// <c>1026,70</c> and <c>120,50</c> under a comma where the invariant run wrote <c>1026.70</c> and
    /// <c>120.50</c> — so their traces, their metrics and their hashes were the machine's.</para>
    /// </summary>
    [Fact]
    public void Every_golden_vector_writes_the_same_bytes_under_nl_BE_and_fr_BE_number_formats()
    {
        var invariant = Written(RunAll());

        foreach (var (culture, clone) in Cultures.Belgian)
        {
            var under = Cultures.Under(clone, () => Written(RunAll()));
            Assert.Equal(invariant.Length, under.Length);

            for (var i = 0; i < invariant.Length; i++)
            {
                var (vector, trace, fault, runId, pin) = invariant[i];
                var other = under[i];
                var name = $"golden vector {vector} under {culture}";

                Assert.True(trace == other.Trace,
                    $"{name} wrote other trace bytes — first difference: {FirstDifference(trace, other.Trace)}");
                Assert.True(fault == other.Fault,
                    $"{name} faulted with '{other.Fault}' where the invariant run faulted with '{fault}'");
                Assert.True(pin == other.Pin, $"{name} computed {other.Pin} where the invariant run computed {pin}");
                Assert.True(runId == other.RunId, $"{name} has run id {other.RunId}, not {runId}");
            }
        }

        // WRITTEN WHILE THE CULTURE IS STILL SET. A trace's text is computed when it is read, so a text read
        // after the culture was put back would be the invariant culture's text whatever the run did — every
        // figure is taken here, inside the call that runs under the clone.
        static (string Vector, string Trace, string? Fault, string RunId, Pin Pin)[] Written(
            (Vector Vector, Series Series, BacktestResult Run, Pin Pin)[] all) =>
            [.. all.Select(a => (a.Vector.Name, a.Run.Trace.Text, a.Run.FaultReason, a.Run.RunId, a.Pin))];

        static string FirstDifference(string a, string b)
        {
            var left = a.Split('\n');
            var right = b.Split('\n');
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var l = i < left.Length ? left[i] : "(no line)";
                var r = i < right.Length ? right[i] : "(no line)";
                if (l != r) return $"line {i + 1}: '{l}' became '{r}'";
            }
            return "none";
        }
    }


    static IEnumerable<Expr> Walk(Expr e) => e switch
    {
        UnaryExpr u => [e, .. Walk(u.Operand)],
        BinaryExpr b => [e, .. Walk(b.Left), .. Walk(b.Right)],
        CrossExpr c => [e, .. Walk(c.Left), .. Walk(c.Right)],
        _ => [e]
    };
}
