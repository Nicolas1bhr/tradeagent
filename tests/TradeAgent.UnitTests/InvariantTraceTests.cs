using System.Globalization;
using System.Reflection;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// nl-BE'S AND fr-BE'S NUMBER FORMATS, AS FAR AS THIS BUILD CAN HAVE THEM — clones of the invariant
/// culture, set on ONE test's thread and put back.
///
/// <para><b>Why clones.</b> <c>Directory.Build.props</c> sets <c>InvariantGlobalization=true</c> for every
/// project and every test, so <c>CultureInfo.GetCultureInfo("nl-BE")</c> throws
/// <see cref="CultureNotFoundException"/> here: the culture a Belgian machine runs in cannot be built. What
/// can be built is what that culture does to a number — a clone of the invariant culture whose decimal
/// separator is a comma and whose group separator is a full stop (nl-BE) or U+202F, the narrow no-break
/// space (fr-BE). Each clone is proved to write <c>1.5m</c> as <c>1,5</c> on the thread before anything
/// runs under it, so a test that passes cannot be passing because the culture never took.</para>
///
/// <para><b>Never process-wide.</b> <see cref="CultureInfo.CurrentCulture"/> and
/// <see cref="CultureInfo.CurrentUICulture"/> are set in a try/finally around one piece of work, never
/// <see cref="CultureInfo.DefaultThreadCurrentCulture"/>: xUnit runs this assembly's classes in parallel,
/// and a default set for the process would reach every other class's thread while it ran.</para>
/// </summary>
static class Cultures
{
    /// <summary>nl-BE's number format: <c>1.234,5</c>.</summary>
    public static CultureInfo NlBe => Clone(",", ".");

    /// <summary>fr-BE's number format: <c>1 234,5</c>, the group separator U+202F.</summary>
    public static CultureInfo FrBe => Clone(",", " ");

    /// <summary>
    /// A clock that writes <c>00.00.00</c> — the time separator fi-FI and da-DK use — over nl-BE's number
    /// format. For the one writer on the path that formats a time with a pattern, where <c>:</c> means
    /// "the culture's time separator" rather than a colon.
    /// </summary>
    public static CultureInfo DottedClock
    {
        get
        {
            var culture = Clone(",", ".");
            culture.DateTimeFormat.TimeSeparator = ".";
            return culture;
        }
    }

    /// <summary>The two Belgian number formats, named for a failure message.</summary>
    public static IEnumerable<(string Name, CultureInfo Culture)> Belgian =>
        [("nl-BE's number format", NlBe), ("fr-BE's number format", FrBe)];

    static CultureInfo Clone(string decimals, string groups)
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        var n = culture.NumberFormat;
        n.NumberDecimalSeparator = decimals;
        n.NumberGroupSeparator = groups;
        n.CurrencyDecimalSeparator = decimals;
        n.CurrencyGroupSeparator = groups;
        n.PercentDecimalSeparator = decimals;
        n.PercentGroupSeparator = groups;
        return culture;
    }

    /// <summary>
    /// <paramref name="work"/>, with this thread's culture and UI culture set to <paramref name="culture"/>
    /// and put back afterwards whatever happens. Asserts first that the thread now writes <c>1.5m</c> as
    /// <c>1,5</c>.
    /// </summary>
    public static T Under<T>(CultureInfo culture, Func<T> work)
    {
        var was = CultureInfo.CurrentCulture;
        var wasUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            Assert.Equal("1,5", 1.5m.ToString());     // the thread really is in it
            return work();
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
            CultureInfo.CurrentUICulture = wasUi;
        }
    }
}

/// <summary>
/// A TRACE, ITS HASHES AND ITS FAULT WORDS ARE THE SAME BYTES UNDER ANY CULTURE (<c>U-invariant-traces</c>).
///
/// <para><b>What is at stake.</b> A request "reproduces the same trace byte for byte and the same id"
/// (<c>docs/CONTRACTS.md</c>), on two machines. A fault is the trace's last line and its words are hashed
/// into <c>trace_sha256</c> and stored as <c>fault_reason</c>; a version id, a run id, a promotion id, a
/// publication id and a dataset's sha are hashes of texts with numbers in them. A number written in the
/// machine's culture makes every one of those a fact about the machine: the same program over the same
/// bars on a laptop set to Dutch would produce a different trace from CI's, and an id that does not
/// reproduce is not an identity.</para>
///
/// <para><b>Why nothing was red before.</b> <c>InvariantGlobalization=true</c> makes the ambient culture
/// the invariant one everywhere this ships, so the defect was one property away and no test could reach
/// it. These tests reach it the only way this build allows — <see cref="Cultures"/> — and the golden
/// vectors' own test (<c>EvaluationGoldenVectorTests</c>) runs every vector under the same clones.</para>
/// </summary>
public class InvariantTraceTests
{
    static readonly DateTimeOffset Monday = new(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);

    static StrategyProgram Program(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    /// <summary>One closed bar, its close as given, a high and a low half a unit either side.</summary>
    static KlineBar Bar(decimal close) => new(Monday, close, close + 0.50m, close - 0.50m, close, 1m);

    /// <summary>The fault ONE event comes back with — <see cref="StrategyEvaluator.Step"/>, on a fresh run.</summary>
    static string FaultOf(StrategyProgram program, KlineBar bar, AccountReading account)
    {
        var state = EvaluationState.Start(program);
        var outcome = StrategyEvaluator.Step(state, bar, account);
        Assert.True(outcome.Status == EvaluationStatus.Faulted,
            $"the event was meant to fault and came back {outcome.Status}");
        return outcome.FaultReason!;
    }

    /// <summary>
    /// (b) EVERY EVALUATOR FAULT SPELLS ITS NUMBERS AS THE TRACE DOES — invariant, trailing zeros gone
    /// (<c>StrategyParser.Number</c>), so <c>100.5</c>, never <c>100.50</c> or <c>100,50</c>.
    ///
    /// <para>The four faults that carry a decimal, each on the rig its own test already uses: a division by
    /// zero names its dividend (<c>EvaluatorLimitTests</c>); risk sizing against a stop that is not below the
    /// close names both (<c>EvaluatorRuleTests</c>); an account that reads Long with no positive quantity
    /// names the quantity — <c>-0.5</c> here; and a fraction of capital over a close of <c>0.00</c> names the
    /// close. Each is answered under the invariant culture and under both Belgian number formats, and must
    /// read exactly the same words every time.</para>
    ///
    /// <para><b>RED on the base</b>: the words carried the machine's culture and the decimal's own scale —
    /// <c>100,50</c>, <c>-0,5</c>, <c>0,00</c> under a comma culture, and <c>100.50</c>, <c>0.00</c> even
    /// under the invariant one.</para>
    /// </summary>
    [Fact]
    public void Every_evaluator_fault_spells_its_numbers_as_the_trace_does()
    {
        var division = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close < 1
            entry when close / (close - close) > 0
            """);
        var riskOverAStopAboveTheClose = Program("""
            instrument BTCUSDT
            size risk_fraction 0.01
            stop fixed 250.50
            exit when close < 1
            entry when close > 0
            """);
        var exitAndEntryBothTrue = Program("""
            instrument BTCUSDT
            size fixed 1
            exit when close > 0
            entry when close > 0
            """);
        var capitalFraction = Program("""
            instrument BTCUSDT
            size capital_fraction 0.5
            exit when close > 5
            entry when close < 1
            """);

        var longWithANegativeQuantity =
            new AccountReading(10_000m, 10_000m, PositionSide.Long, -0.5m, 100m, false, 1);

        (string Case, Func<string> Fault, string Words)[] cases =
        [
            ("a division by zero", () => FaultOf(division, Bar(100.50m), AccountReading.Flat(10_000m)),
                "a rule divided 100.5 by zero, which has no value to compare or size against"),
            ("risk sizing against a stop above the close",
                () => FaultOf(riskOverAStopAboveTheClose, Bar(200.10m), AccountReading.Flat(10_000m)),
                "the stop at 250.5 is not below the reference price 200.1, so there is no risk distance to size against"),
            ("an account that reads Long with a quantity of -0.5",
                () => FaultOf(exitAndEntryBothTrue, Bar(100m), longWithANegativeQuantity),
                "the account reads Long with a quantity of -0.5, so there is no position for an exit to close; "
                + "the evaluator does not invent one"),
            ("a fraction of capital over a close of 0.00",
                () => FaultOf(capitalFraction, Bar(0.00m), AccountReading.Flat(10_000m)),
                "the bar's close is 0, so a fraction of capital buys no quantity that can be divided by a price")
        ];

        foreach (var (name, fault, words) in cases)
        {
            var invariant = fault();
            Assert.True(invariant == words,
                $"{name}, under the invariant culture, faulted with '{invariant}' where the trace's spelling is '{words}'");

            foreach (var (culture, clone) in Cultures.Belgian)
            {
                var under = Cultures.Under(clone, fault);
                Assert.True(under == words,
                    $"{name}, under {culture}, faulted with '{under}' where the trace's spelling is '{words}'");
            }
        }
    }

    /// <summary>
    /// A FAULT OR A NO-TRADE IS WORDED THROUGH <see cref="TraceText"/> AND NOTHING ELSE — which is what makes
    /// (b) hold for a fault nobody has written yet.
    ///
    /// <para>(b) proves the four faults that exist. A fifth, written next year as <c>$"… {price} …"</c>, is
    /// spelled correctly only because every place that makes these words takes <see cref="TraceText"/>, to
    /// which a string does not convert — so <c>"… " + price</c> or a string built elsewhere cannot be handed
    /// in. That is a property of five signatures, and a convenience overload taking a string would undo it
    /// without a single test going red. This one would: every overload a caller can reach takes the words as
    /// <see cref="TraceText"/>, and none takes a string.</para>
    /// </summary>
    [Fact]
    public void A_fault_or_a_no_trade_is_worded_through_trace_text_and_nothing_else()
    {
        const BindingFlags every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var core = typeof(EvaluationOutcome).Assembly;

        (string What, MethodBase[] Overloads)[] sinks =
        [
            ("EvaluationOutcome.Faulted", [.. typeof(EvaluationOutcome).GetMethods(every).Where(m => m.Name == "Faulted")]),
            ("the evaluation state's Fault", [.. typeof(EvaluationState).GetMethods(every).Where(m => m.Name == "Fault")]),
            ("the interpreter's EvaluationFault",
                [.. core.GetType("TradeAgent.Core.Strategy.EvaluationFault", throwOnError: true)!.GetConstructors(every)]),
            ("a trace's fault line", [.. typeof(BacktestEvent).GetMethods(every).Where(m => m.Name == "Fault")]),
            ("a trace's no-trade line", [.. typeof(BacktestEvent).GetMethods(every).Where(m => m.Name == "NoTrade")])
        ];

        foreach (var (what, overloads) in sinks)
        {
            var reachable = overloads.Where(m => !m.IsPrivate).ToList();
            Assert.True(reachable.Count > 0, $"{what} has no overload a caller can reach — renamed?");

            foreach (var overload in reachable)
            {
                var parameters = overload.GetParameters().Select(p => p.ParameterType).ToList();
                Assert.True(parameters.Contains(typeof(TraceText)) && !parameters.Contains(typeof(string)),
                    $"{what}({string.Join(", ", parameters.Select(p => p.Name))}) takes its words as something other "
                    + "than TraceText, so a number in them can be written in the machine's culture again");
            }
        }
    }

    // ---- (c): a stored dataset, a recorded run ------------------------------------------------------

    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The caller every run here is for: a pipe caller, which can never read a holdout bar.</summary>
    static readonly BarAudience Research = BarAudience.Pipe(CouncilRoles.Research);

    /// <summary>
    /// A program that faults on its first bar on a decimal with a trailing zero: the first close is 96, and
    /// <c>close * 1.5</c> is <c>144.0</c>.
    /// </summary>
    const string FaultsOnItsFirstBar = """
        instrument BTCUSDT
        size fixed 1
        exit when close < 1
        entry when close * 1.5 / (close - close) > 0
        """;

    /// <summary>
    /// A dataset the ledger really recorded — the rig <c>BacktestDeterminismTests</c> uses: a normalised file
    /// of <paramref name="bars"/> minutes and a stand-in for the vendor's archive, both hashed, so
    /// <see cref="DatasetStore.Checked"/> has something true to check. Unique names, because every class in
    /// this assembly shares one home and xUnit runs them in parallel.
    /// </summary>
    static DatasetRecord Given(Database db, int bars, string pair = "BTCUSDT")
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{pair}-1m-2026-08-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// ONE RUN OVER A STORED DATASET, RECORDED AND READ BACK — the dataset written, the version and the run
    /// recorded and the <c>strategy_run</c> row read from the ledger, all under whatever culture the thread
    /// is in. A fresh ledger each time, so the dataset is id 1 in every one of them and the run ids can be
    /// compared.
    /// </summary>
    static (StrategyRunRow Row, string Trace) RecordedRun()
    {
        using var db = TestEnv.NewDb();
        var set = Given(db, 30);
        var program = Program(FaultsOnItsFirstBar);
        var model = ExecutionModel.Declare(fees: 0.0010m).Model!;

        var run = Backtest.Over(new DatasetStore(db), set.Id, program, model, Research);
        Assert.True(run.Ok, run.Why);
        var result = run.Result!;

        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars, Start, null, null));
        strategies.RecordRun(new StrategyRunRow(
            result.RunId, result.VersionId, set.Id, result.Request.DatasetSha256, null, null,
            result.Request.Model.Canonical, result.Outcome.ToString(), result.FaultReason,
            result.Metrics.Bars, result.Metrics.Trades, result.Metrics.Wins, result.Metrics.Signals,
            result.Metrics.Fills, result.Metrics.ExposureBars, result.Metrics.MissingMinutes,
            result.Metrics.Faults, result.Metrics.GrossPnl, result.Metrics.Fees, result.Metrics.NetPnl,
            result.Metrics.MaxDrawdown, result.Trace.Sha256, Start, null, null), []);

        return (Assert.Single(strategies.RunsOfDataset(set.Id)), result.Trace.Text);
    }

    /// <summary>
    /// (c) A BACKTEST OVER A STORED DATASET RECORDS THE SAME RUN UNDER nl-BE AND fr-BE.
    ///
    /// <para>The whole path a <c>trade backtest</c> takes — the normalised file read and verified, the run,
    /// the trace hashed, the row written and read back — once under the invariant culture and once under
    /// each Belgian number format. The program faults on its first bar on <c>144.0</c>, so the row's
    /// <c>fault_reason</c>, its <c>trace_sha256</c> and its run id are all exposed to the culture: the row
    /// must be the same row, its fault must read <c>144</c>, and the trace must be the same bytes.</para>
    ///
    /// <para><b>RED on the base</b>: the fault read <c>144.0</c> under the invariant culture and
    /// <c>144,0</c> under a comma, so the two rows disagreed on their fault and on their trace's hash.</para>
    /// </summary>
    [Fact]
    public void A_backtest_over_a_stored_dataset_records_the_same_run_under_nl_BE_and_fr_BE()
    {
        var invariant = RecordedRun();
        Assert.Equal(nameof(BacktestOutcome.FAULTED), invariant.Row.Outcome);

        foreach (var (culture, clone) in Cultures.Belgian)
        {
            var under = Cultures.Under(clone, RecordedRun);

            Assert.True(invariant.Row.FaultReason == under.Row.FaultReason,
                $"under {culture} the recorded fault reads '{under.Row.FaultReason}' and under the invariant culture "
                + $"'{invariant.Row.FaultReason}'");
            Assert.True(invariant.Trace == under.Trace, $"under {culture} the trace is other bytes:\n{under.Trace}");
            Assert.Equal(invariant.Row.TraceSha256, under.Row.TraceSha256);
            Assert.Equal(invariant.Row.Id, under.Row.Id);
            Assert.Equal(invariant.Row, under.Row);
        }

        // And the words are the trace's spelling, not merely the same mistake twice.
        Assert.Equal("a rule divided 144 by zero, which has no value to compare or size against",
            invariant.Row.FaultReason);
    }
}
