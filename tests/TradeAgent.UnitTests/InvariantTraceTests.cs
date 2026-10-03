using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml.Linq;
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
    public static CultureInfo FrBe => Clone(",", "\u202F");

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

    // ---- (d): every id and canonical text ------------------------------------------------------------

    /// <summary>
    /// A PROGRAM THAT PUTS EVERY WHOLE NUMBER THE CANONICAL FORM WRITES INTO ITS ID — wall clocks in an
    /// opening range, two entry windows and a session exit, every indicator's period, an ATR stop's period,
    /// history references on a series and on an indicator, a maximum hold, and the warm-up they add up to.
    /// </summary>
    const string EveryClockAndPeriod = """
        instrument BTCUSDT
        timezone America/New_York
        opening_range 09:30-10:00
        entry_window 10:05-11:30
        entry_window 13:00-15:45
        session_exit 15:55
        weekdays mon, wed, fri
        const k = 1.250
        indicator a = sma(close, 12)
        indicator b = ema(high, 26)
        indicator c = rsi(close, 14)
        indicator d = highest(high, 20)
        indicator e = lowest(low, 20)
        indicator f = atr(14)
        indicator g = opening_range_high()
        size risk_fraction 0.0250
        stop atr 2.50 14
        target percent 1.50
        max_hold_bars 120
        exit when close[3] < a[2] * 0.990 or c > 70 or b < 1
        entry when crosses_above(close, g) and d - e > f * k and volume[1] > 0
        """;

    /// <summary>A program on declared hourly bars with all three execution bounds, which the canonical form writes in seconds.</summary>
    const string DeclaredBarsAndBounds = """
        instrument ETHUSDT
        bars 1h
        timeframe 1h
        data_freshness 90m
        max_decision_age 120s
        indicator m = sma(close, 24)
        size capital_fraction 0.50
        target fixed 2500.00
        exit when close < m
        entry when close > m[1]
        """;

    /// <summary>
    /// A raw month as a source publishes it — open time and close time in milliseconds, five prices — with a
    /// minute missing, for <see cref="KlineNormaliser.Normalise"/> to write as a dataset file.
    /// </summary>
    static string RawMonth()
    {
        var text = new StringBuilder();
        foreach (var minute in (int[])[0, 1, 2, 4, 5])
        {
            var open = 1_767_571_200_000L + minute * 60_000L;   // 2026-01-05T00:00:00Z
            text.Append(CultureInfo.InvariantCulture,
                $"{open},102.67,102.82,102.17,102.6{minute},10.50,{open + 59_999}\n");
        }
        return text.ToString();
    }

    /// <summary>
    /// EVERY ID AND CANONICAL TEXT THIS PATH WRITES, labelled, in a fixed order, computed under whatever
    /// culture the thread is in: three shipped programs and two more, each as its id, its canonical text and
    /// its parameters; the manifest; the execution model and both venue cost texts with their hashes; a run's
    /// window and id; a promotion's id, the interpreter build inside it, and the note the referee publishes
    /// (the publication's id is that note's hash); and a dataset file written by the normaliser, with its sha.
    /// </summary>
    static List<(string What, string Text)> EveryIdAndCanonicalText()
    {
        var all = new List<(string, string)>();

        var programs = DayOnePrograms.Names.Select(n => (n, DayOnePrograms.Text(n)))
            .Append(("every clock and period", EveryClockAndPeriod))
            .Append(("declared bars and bounds", DeclaredBarsAndBounds));
        foreach (var (name, text) in programs)
        {
            var p = Program(text);
            all.Add(($"{name}: version id", p.StrategyId));
            all.Add(($"{name}: canonical text", p.Canonical));
            all.Add(($"{name}: parameters", p.Parameters));
        }

        var version = Program(EveryClockAndPeriod).StrategyId;
        all.Add(("manifest", StrategyVersions.Manifest));

        var model = ExecutionModel.Declare(0.0010m, 0.00050m, 0.0010m, 25_000.00m).Model!;
        all.Add(("execution model", model.Canonical));

        using (var db = TestEnv.NewDb())
        {
            var venues = new VenueStore(db);
            venues.Sync(new VenueCatalogRead(
            [
                new VenueEntry
                {
                    Id = VenueCatalog.BinanceSpot, DisplayName = VenueCatalog.BinanceSpot,
                    CalendarKind = CalendarKind.Continuous, Source = "declared by this test", Verified = true,
                    Instruments =
                    [
                        new VenueInstrumentEntry
                        {
                            Symbol = "BTCUSDT", TickSize = 0.010m, QuantityIncrement = 0.000010m,
                            Source = "declared by this test", Verified = true
                        }
                    ]
                }
            ], null));
            var bars = new DatasetRecord(7, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
                "/not/read/here.csv", "aa11", 1000, Start, Start.AddDays(30), 0, [], false, 0, 0, 0, Start,
                DatasetState.ACCEPTED, null, []) { VenueId = VenueCatalog.BinanceSpot, InstrumentSymbol = "BTCUSDT" };

            var judge = VenueCostModel.For(bars, venues, 25_000.00m);
            Assert.True(judge.Ok, judge.Why);
            all.Add(("venue cost model", judge.Model!.Canonical));
            all.Add(("venue cost model sha256", judge.Model.Sha256));
        }

        var friction = VenueFriction.Of(VenueCatalog.BinanceSpot)!;
        all.Add(("venue friction", friction.Canonical));
        all.Add(("venue friction sha256", friction.Sha256));
        all.Add(("venue friction id", friction.Id));

        var request = new BacktestRequest(1_234_567, "abc123", model, Start, Start.AddDays(90));
        var runId = request.RunIdFor(version);
        all.Add(("run window", request.Window));
        all.Add(("run id", runId));

        all.Add(("interpreter build", StrategyStore.InterpreterBuild));
        var promotionId = PromotionRow.IdOf(version, 1_234_567, "policy-sha", StrategyStore.InterpreterBuild,
            7_654_321, "dataset-sha", model.Canonical, Referee.EvaluatorVersion, runId);
        all.Add(("promotion id", promotionId));
        all.Add(("the referee's note", RefereeFeedback.Text(new PromotionRow(promotionId, version, 1_234_567,
            "policy-sha", StrategyStore.InterpreterBuild, 7_654_321, "dataset-sha", model.Canonical,
            Referee.EvaluatorVersion, runId, PromotionVerdict.Refused, PromotionReason.NotProfitable, Start))));

        var dir = Path.Combine(TestEnv.Home, "invariant-traces", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(dir, "BTCUSDT-1m-2026-01.csv");
        File.WriteAllText(raw, RawMonth());
        var dataset = KlineNormaliser.Normalise([new RawArchiveFile("2026-01", raw)],
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), Path.Combine(dir, "normalised.csv"));
        all.Add(("dataset file", File.ReadAllText(dataset.Path)));
        all.Add(("dataset sha256", dataset.Sha256));

        return all;
    }

    /// <summary>
    /// (d) NO ID OR CANONICAL TEXT MOVES UNDER ANOTHER CULTURE.
    ///
    /// <para>A version's id, a run's, a promotion's and a publication's, and a dataset's sha, are hashes of
    /// texts with whole numbers and dates in them; the ledger and the referee compare them against figures
    /// earlier builds wrote, on other machines. Every one is computed under the invariant culture and again
    /// under nl-BE's and fr-BE's number formats and under a clock that writes <c>00.00.00</c>, and must be
    /// the same text. The integers are named invariant where they are written (<c>U-invariant-traces</c>);
    /// the decimals already went through <c>StrategyParser.Number</c>.</para>
    ///
    /// <para><b>RED on the base</b> at the dataset file: <see cref="KlineNormaliser"/> wrote each bar's time
    /// through a pattern whose <c>:</c> means the culture's time separator, so the dotted clock wrote
    /// <c>2026-01-05T00.00.00Z</c> — a different file, a different sha, a different dataset and therefore a
    /// different run id for every run over it. Every other line held already, by the luck of positive whole
    /// numbers having no separator to disagree about.</para>
    /// </summary>
    [Fact]
    public void No_id_or_canonical_text_moves_under_another_culture()
    {
        var invariant = EveryIdAndCanonicalText();
        Assert.Contains(invariant, x => x.What == "dataset file" && x.Text.Contains("2026-01-05T00:00:00Z,", StringComparison.Ordinal));

        foreach (var (culture, clone) in Cultures.Belgian.Append(("a clock that writes 00.00.00", Cultures.DottedClock)))
        {
            var under = Cultures.Under(clone, EveryIdAndCanonicalText);
            Assert.Equal(invariant.Count, under.Count);

            for (var i = 0; i < invariant.Count; i++)
                Assert.True(invariant[i] == under[i],
                    $"under {culture}, the {invariant[i].What} is\n{under[i].Text}\nand under the invariant culture\n{invariant[i].Text}");
        }
    }

    // ---- (e): the switch that keeps the latent latent ----------------------------------------------

    /// <summary>What has to be true before the switch may go, named for every failure below.</summary>
    const string WhatMustPassFirst =
        "It is what keeps a number some later change writes without naming its culture in the invariant spelling "
        + "on every machine this ships to, and it may be turned off only after (a) EvaluationGoldenVectorTests."
        + nameof(EvaluationGoldenVectorTests.Every_golden_vector_writes_the_same_bytes_under_nl_BE_and_fr_BE_number_formats)
        + ", (b) " + nameof(Every_evaluator_fault_spells_its_numbers_as_the_trace_does)
        + ", (c) " + nameof(A_backtest_over_a_stored_dataset_records_the_same_run_under_nl_BE_and_fr_BE)
        + " and (d) " + nameof(No_id_or_canonical_text_moves_under_another_culture)
        + " pass WITHOUT it, under the real nl-BE and fr-BE cultures it makes unbuildable today — then change this test.";

    /// <summary>
    /// (e) INVARIANT GLOBALIZATION STAYS ON UNLESS THE CULTURE TESTS PASS WITHOUT IT — the orchestrator's order:
    /// what is latent stays latent by construction.
    ///
    /// <para>(a)–(d) prove that every number the evaluation path records names its culture, under the only
    /// cultures this build can construct: clones of the invariant one. They cannot run under the real nl-BE or
    /// fr-BE, because <c>InvariantGlobalization=true</c> is what makes those unbuildable — and the same switch
    /// makes the ambient culture the invariant one on every machine this ships to, so a number a later change
    /// writes without naming its culture still comes out in the invariant spelling there. It is one line in
    /// <c>Directory.Build.props</c>, written for startup cost and not for traces, which is the kind of line a
    /// tidy-up removes. So this reads the switch every shipped project is built with — the props file, and no
    /// project, props, targets, runtime template, packaging script or CI step turning it off — and the switch
    /// the tests and the shipped programs beside them run with, and fails if any is off, naming what must pass
    /// first.</para>
    ///
    /// <para><b>RED</b> with <c>Directory.Build.props</c> set to <c>false</c>, watched once.</para>
    /// </summary>
    [Fact]
    public void Invariant_globalization_stays_on_unless_the_culture_tests_pass_without_it()
    {
        var root = DayOnePrograms.RepoRoot();
        string Relative(string path) => Path.GetRelativePath(root, path);
        static IEnumerable<XElement> Switches(XDocument d) =>
            d.Descendants().Where(e => e.Name.LocalName == "InvariantGlobalization");

        // 1. WHAT EVERY PROJECT INHERITS: the props file at the root turns it on.
        var props = Path.Combine(root, "Directory.Build.props");
        var declared = Switches(XDocument.Load(props)).Select(e => e.Value.Trim()).ToList();
        Assert.True(declared.SequenceEqual(["true"]),
            $"{Relative(props)} sets InvariantGlobalization to [{string.Join(", ", declared)}] rather than true. {WhatMustPassFirst}");

        // 2. NOTHING TURNS IT OFF FOR ONE PROJECT, or for one published build, or on one CI runner.
        var files = new[] { "src", "tests", "packaging", ".github" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
            .Where(f => !Relative(f).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .ToList();

        var msbuild = files.Where(f => f.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                                       || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                                       || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(msbuild, f => Path.GetFileName(f) == "TradeAgent.App.csproj");
        foreach (var file in msbuild)
            foreach (var e in Switches(XDocument.Load(file)))
                Assert.True(e.Value.Trim() == "true",
                    $"{Relative(file)} sets InvariantGlobalization to '{e.Value}' for its project. {WhatMustPassFirst}");

        var turnedOff = new System.Text.RegularExpressions.Regex(
            @"InvariantGlobalization\W*=?\W*false|Globalization\.Invariant\W+false|GLOBALIZATION_INVARIANT\W+(0|false)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var file in files.Where(f => f.EndsWith("runtimeconfig.template.json", StringComparison.OrdinalIgnoreCase)
                                              || f.Contains($"{Path.DirectorySeparatorChar}packaging{Path.DirectorySeparatorChar}")
                                              || f.Contains($"{Path.DirectorySeparatorChar}.github{Path.DirectorySeparatorChar}")))
            Assert.False(turnedOff.IsMatch(File.ReadAllText(file)),
                $"{Relative(file)} turns invariant globalization off. {WhatMustPassFirst}");

        // 3. WHAT THE TESTS RUN WITH — the switch, and its effect: the culture a Belgian machine runs in cannot be built.
        Assert.True(AppContext.TryGetSwitch("System.Globalization.Invariant", out var on) && on,
            $"this test process runs without System.Globalization.Invariant. {WhatMustPassFirst}");
        Assert.Throws<CultureNotFoundException>(() => CultureInfo.GetCultureInfo("nl-BE"));

        // 4. WHAT THE SHIPPED PROGRAMS BESIDE THE TESTS RUN WITH — the app the owner starts is always here.
        var shipped = new[] { "TradeAgent", "trade", "tradeagent-gateway", "TradeAgent.UnitTests" }
            .Select(name => Path.Combine(AppContext.BaseDirectory, $"{name}.runtimeconfig.json"))
            .Where(File.Exists)
            .ToList();
        Assert.Contains(shipped, f => Path.GetFileName(f) == "TradeAgent.runtimeconfig.json");
        foreach (var config in shipped)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
            var invariant = json.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
                .TryGetProperty("System.Globalization.Invariant", out var value) && value.GetBoolean();
            Assert.True(invariant,
                $"{Path.GetFileName(config)} runs without System.Globalization.Invariant. {WhatMustPassFirst}");
        }
    }
}
