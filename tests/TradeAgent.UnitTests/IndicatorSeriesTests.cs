using System.Globalization;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 2 — THE SEVEN INDICATORS, PINNED VALUE BY VALUE OVER A FIXTURE OF BARS.
///
/// <para>`docs/STRATEGY-LANGUAGE.md` specifies each one's value, its INITIALISATION and the bars it
/// needs. Initialisation is the half that goes wrong quietly: an EMA seeded with its first close
/// instead of with the simple mean of the first `p` values is a different indicator wearing the same
/// name, and every backtest over it is evidence about a program nobody wrote. There is nothing in a
/// result that would say so, which is why `StrategyVersions.IndicatorSemanticsVersion` is hashed into
/// every `StrategyId` and why the series below are pinned digit for digit rather than to a tolerance.
/// A pinned series also pins the SCALE: `101.50` and `101.5` are the same number and not the same
/// answer, and a change to how a mean is divided shows up in the trailing zeros first.</para>
///
/// <para><b>Where the figures come from.</b> Every value below was recomputed OUTSIDE this build, by
/// an independent implementation of the documented formulas in Python's arbitrary-precision decimal
/// at sixty digits: all 280 agreed, with a worst relative deviation of 2.274E-27 (the longest Wilder
/// chain, `rsi4` at bar 23) — .NET's `decimal` carries about 29 significant digits, so that is
/// agreement to the last digit either type can hold. Four of the seven are also checked against an
/// exact, hand-checkable anchor in this file, and three are recomputed naively from the raw window in
/// the test itself, so a mistake would have to be made twice in two different ways.</para>
/// </summary>
public class IndicatorSeriesTests
{
    const string Sma5 =
        "null null null null 101.50 101.98 102.55 103.21 103.96 104.40 105.20 106.00 106.64 107.04 107.60 107.91 " +
        "107.69 107.09 106.19 104.99 103.50 102.00 100.50 99.00 98.04 97.10 96.18 95.28 94.98 94.74 95.08 96.00 " +
        "97.50 99.00 100.50 102.00 103.05 104.10 105.15 106.20";

    const string Ema4 =
        "null null null 101.125 101.8750 102.08500 102.691000 103.5346000 104.52076000 104.792456000 " +
        "105.4354736000 106.30128416000 106.980770496000 107.3884622976000 107.63307737856000 107.759846427136000 " +
        "107.2559078562816000 106.35354471376896000 105.212126828261376000 103.9272760969568256000 " +
        "102.55636565817409536000 101.133819394904457216000 99.6802916369426743296000 98.20817498216560459776000 " +
        "97.804904989299362758656000 97.0029429935796176551936000 95.96176579614777059311616000 " +
        "94.77705947768866235586969600 94.66623568661319741352181760 95.19974141196791844811309056 " +
        "96.11984484718075106886785434 97.27190690830845064132071260 98.56314414498507038479242756 " +
        "99.93788648699104223087545654 101.36273189219462533852527392 102.81763913531677520311516435 " +
        "103.39058348119006512186909861 104.33435008871403907312145917 105.50061005322842344387287550 " +
        "106.80036603193705406632372530";

    const string Ema5 =
        "null null null null 101.50 101.80000000000000000000000000 102.40000000000000000000000000 " +
        "103.20000000000000000000000000 104.13333333333333333333333333 104.48888888888888888888888889 " +
        "105.12592592592592592592592593 105.95061728395061728395061729 106.63374485596707818930041153 " +
        "107.08916323731138545953360769 107.39277549154092363968907179 107.57851699436061575979271453 " +
        "107.21901132957374383986180969 106.47934088638249589324120646 105.48622725758833059549413764 " +
        "104.32415150505888706366275843 103.04943433670592470910850562 101.69962289113728313940567041 " +
        "100.29974859409152209293711361 98.86649906272768139529140907 98.31099937515178759686093938 " +
        "97.47399958343452506457395959 96.44933305562301670971597306 95.29955537041534447314398204 " +
        "95.03303691361022964876265469 95.35535794240681976584176979 96.07023862827121317722784653 " +
        "97.04682575218080878481856435 98.19788383478720585654570957 99.46525588985813723769713971 " +
        "100.81017059323875815846475981 102.20678039549250543897650654 102.88785359699500362598433769 " +
        "103.84190239799666908398955846 104.97793493199777938932637231 106.23528995466518625955091487";

    const string Rsi4 =
        "null null null null 100 78.947368421052631578947368421 86.51685393258426966292134832 " +
        "90.88607594936708860759493671 93.63582793164407778432527991 73.834477773762197325623418865 " +
        "81.61169768876310728928558470 86.83077669314254532225792761 88.30592472124915928216584297 " +
        "88.30592472124915928216584297 88.30592472124915928216584297 85.46923723963580421402254198 " +
        "38.120083217816922123587845036 21.608493707803058458836674042 13.69768417383931284477449299 " +
        "9.20463103132125210283536963 6.40387181577440243208617655 4.5556367262429388504177885 " +
        "3.28970502429346946374287713 2.40035099254085217380440273 24.245494860896944229837112388 " +
        "17.98399524042228865743284074 13.37757593735997453822719472 9.97195483668190254827283119 " +
        "33.981604705748850639174791615 51.299060351641630476044193696 63.918551017513826716853453418 " +
        "73.18353622959402997642931751 80.02309049305225433159940478 85.09260863415805331123831032 " +
        "88.86143649061041825144830938 91.66953640101011594627235682 78.479439039463111312161549334 " +
        "84.44708943861535956529644642 88.64529978531225531730598731 91.65038965891694304818823180";

    const string Atr3 =
        "null null null 1.55 1.55 1.50 1.6666666666666666666666666667 1.6444444444444444444444444445 " +
        "1.7629629629629629629629629630 1.7086419753086419753086419753 1.8057613168724279835390946502 " +
        "1.8705075445816186556927297668 1.6470050297210791037951531779 1.3646700198140527358634354519 " +
        "1.1764466798760351572422903013 1.0676311199173567714948602009 1.4617540799449045143299068006 " +
        "1.7411693866299363428866045337 1.9274462577532908952577363558 2.0516308385021939301718242372 " +
        "2.0010872256681292867812161581 2.1007248171120861911874774387 2.1671498780747241274583182925 " +
        "2.2114332520498160849722121950 2.1409555013665440566481414633 2.1606370009110293710987609755 " +
        "2.1737580006073529140658406503 2.1825053337382352760438937669 2.2216702224921568506959291779 " +
        "2.2477801483281045671306194519 2.2651867655520697114204129679 2.2767911770347131409469419786 " +
        "2.2845274513564754272979613191 2.1563516342376502848653075461 2.2042344228251001899102050307 " +
        "2.2361562818834001266068033538 2.0074375212556000844045355692 2.1049583475037333896030237128 " +
        "2.1699722316691555930686824752 2.2133148211127703953791216501";

    const string High5 =
        "null null null null 103.40 103.40 104.00 105.20 106.40 106.40 106.80 108.00 108.40 108.40 108.40 108.40 " +
        "108.40 108.40 108.40 108.40 108.35 106.90 105.40 103.90 101.10 100.90 99.40 97.90 97.60 97.60 97.90 " +
        "99.40 100.90 102.40 103.90 105.40 105.40 106.15 107.65 109.15";

    const string Low5 =
        "null null null null 99.60 99.60 100.35 101.10 101.85 102.00 102.00 104.40 104.40 104.80 104.80 106.00 " +
        "106.10 104.60 103.10 101.60 100.10 98.60 97.10 95.60 95.60 95.40 94.00 92.60 92.60 92.60 92.60 92.60 " +
        "92.60 94.10 95.60 97.10 98.60 100.90 101.60 103.10";
    static IndicatorDecl Decl(IndicatorKind kind, BarSeries source, int period) =>
        new("x", kind, source, period);

    /// <summary>Every value of one indicator over <see cref="SyntheticBars.Forty"/>, `null` included.</summary>
    static string[] Series(IndicatorDecl decl)
    {
        var series = IndicatorSeries.For(decl);
        var values = new List<string>();

        foreach (var bar in SyntheticBars.Forty)
        {
            series.Push(bar, newSession: false, inOpeningRange: false);
            values.Add(series.Value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null");
        }

        return [.. values];
    }

    /// <summary>
    /// THE GOLDEN SERIES. `docs/STRATEGY-LANGUAGE.md`'s table, one indicator per row, and the leading
    /// `null`s are the documented "bars needed" — an indicator is UNDEFINED while it is warming up,
    /// not zero and not a mean of what it has so far.
    /// </summary>
    [Theory]
    [InlineData(IndicatorKind.Sma, BarSeries.Close, 5, Sma5, 5)]
    [InlineData(IndicatorKind.Ema, BarSeries.Close, 4, Ema4, 4)]
    [InlineData(IndicatorKind.Ema, BarSeries.Close, 5, Ema5, 5)]
    [InlineData(IndicatorKind.Rsi, BarSeries.Close, 4, Rsi4, 5)]
    [InlineData(IndicatorKind.Atr, BarSeries.Close, 3, Atr3, 4)]
    [InlineData(IndicatorKind.Highest, BarSeries.High, 5, High5, 5)]
    [InlineData(IndicatorKind.Lowest, BarSeries.Low, 5, Low5, 5)]
    public void The_golden_series_is_what_the_document_specifies(
        IndicatorKind kind, BarSeries source, int period, string golden, int barsNeeded)
    {
        var expected = golden.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var actual = Series(Decl(kind, source, period));

        Assert.Equal(SyntheticBars.Forty.Count, expected.Length);
        Assert.Equal(expected, actual);

        // Undefined during warm-up, and defined on the bar the document says, not one earlier.
        Assert.Equal(barsNeeded - 1, actual.Count(v => v == "null"));
        Assert.Equal("null", actual[barsNeeded - 2]);
        Assert.NotEqual("null", actual[barsNeeded - 1]);
        Assert.Equal(barsNeeded, StrategyWarmUp.Bars(Decl(kind, source, period)));
    }

    /// <summary>
    /// An independent oracle for the three indicators that have one: recomputed naively from the raw
    /// window on every bar, which is the definition in the document and not the incremental sum,
    /// expiring extreme and ring buffer the implementation actually uses.
    /// </summary>
    [Theory]
    [InlineData(IndicatorKind.Sma, BarSeries.Close, 5)]
    [InlineData(IndicatorKind.Sma, BarSeries.Volume, 7)]
    [InlineData(IndicatorKind.Highest, BarSeries.High, 5)]
    [InlineData(IndicatorKind.Highest, BarSeries.Close, 11)]
    [InlineData(IndicatorKind.Lowest, BarSeries.Low, 5)]
    [InlineData(IndicatorKind.Lowest, BarSeries.Open, 3)]
    public void The_window_indicators_agree_with_the_window_recomputed_from_scratch(
        IndicatorKind kind, BarSeries source, int period)
    {
        var series = IndicatorSeries.For(Decl(kind, source, period));
        var seen = new List<decimal>();

        for (var i = 0; i < SyntheticBars.Forty.Count; i++)
        {
            seen.Add(Raw(SyntheticBars.Forty[i], source));
            series.Push(SyntheticBars.Forty[i], false, false);

            if (seen.Count > period) seen.RemoveAt(0);
            if (seen.Count < period)
            {
                Assert.Null(series.Value);
                continue;
            }

            var expected = kind switch
            {
                IndicatorKind.Sma => seen.Sum() / period,
                IndicatorKind.Highest => seen.Max(),
                _ => seen.Min()
            };

            Assert.Equal(expected, series.Value);
        }
    }

    static decimal Raw(KlineBar bar, BarSeries series) => series switch
    {
        BarSeries.Open => bar.Open,
        BarSeries.High => bar.High,
        BarSeries.Low => bar.Low,
        BarSeries.Close => bar.Close,
        _ => bar.Volume
    };

    /// <summary>
    /// THE EXACT ANCHORS — the four values the documented initialisation makes hand-checkable, so that
    /// the long decimals above are not the only thing standing between this build and a plausible
    /// wrong seeding.
    ///
    /// <para>`ema(close, 5)` seeded with the simple mean of the first five closes is exactly their
    /// mean on bar five: (100.00 + 100.75 + 101.50 + 102.25 + 103.00) / 5 = 101.50, and `sma(close, 5)`
    /// says the same thing on the same bar. `rsi(close, 4)` over four rises has a zero average loss
    /// and reads exactly 100. `atr(3)` over three bars whose true range is 1.55 each is exactly 1.55.</para>
    /// </summary>
    [Fact]
    public void The_documented_initialisation_is_exact_where_it_can_be_checked_by_hand()
    {
        var mean = (100.00m + 100.75m + 101.50m + 102.25m + 103.00m) / 5m;
        Assert.Equal(101.50m, mean);

        Assert.Equal("101.50", Series(Decl(IndicatorKind.Ema, BarSeries.Close, 5))[4]);
        Assert.Equal("101.50", Series(Decl(IndicatorKind.Sma, BarSeries.Close, 5))[4]);
        Assert.Equal("101.125", Series(Decl(IndicatorKind.Ema, BarSeries.Close, 4))[3]);
        Assert.Equal("100", Series(Decl(IndicatorKind.Rsi, BarSeries.Close, 4))[4]);
        Assert.Equal("1.55", Series(Decl(IndicatorKind.Atr, BarSeries.Close, 3))[3]);
    }

    /// <summary>
    /// A FLAT SERIES. An EMA of an unchanging price is that price — the weights sum to one, which is
    /// only true if `1-a` is the other half of the same `a` — and an RSI with no gain and no loss
    /// reads 100 by the document's zero-average-loss rule rather than by dividing by zero.
    /// </summary>
    [Fact]
    public void A_price_that_never_moves_leaves_the_smoothed_indicators_exactly_where_they_are()
    {
        var flat = Enumerable.Range(0, 30).Select(i => new KlineBar(
            new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
            250.25m, 250.25m, 250.25m, 250.25m, 1m)).ToList();

        var ema = IndicatorSeries.For(Decl(IndicatorKind.Ema, BarSeries.Close, 7));
        var rsi = IndicatorSeries.For(Decl(IndicatorKind.Rsi, BarSeries.Close, 7));
        var atr = IndicatorSeries.For(Decl(IndicatorKind.Atr, BarSeries.Close, 7));

        foreach (var bar in flat)
        {
            ema.Push(bar, false, false);
            rsi.Push(bar, false, false);
            atr.Push(bar, false, false);
        }

        Assert.Equal(250.25m, ema.Value);
        Assert.Equal(100m, rsi.Value);
        Assert.Equal(0m, atr.Value);
    }

    /// <summary>
    /// A true range is over the high, the low AND the previous close, so a bar that GAPS through the
    /// previous close is wider than its own high-to-low. The fixture gaps up at bar 7 and down at bar
    /// 20 for exactly this; each of the three terms wins somewhere.
    /// </summary>
    [Fact]
    public void A_gap_through_the_previous_close_is_part_of_the_true_range()
    {
        var bars = SyntheticBars.Forty;
        var atr = IndicatorSeries.For(Decl(IndicatorKind.Atr, BarSeries.Close, 1));

        var ranges = new List<decimal?>();
        foreach (var bar in bars)
        {
            atr.Push(bar, false, false);
            ranges.Add(atr.Value);
        }

        // atr(1) is the bar's own true range, so each one can be read off.
        Assert.Equal(1.60m, ranges[7]);                                   // high 105.20 over close 103.60
        Assert.Equal(bars[7].High - bars[6].Close, ranges[7]);
        Assert.Equal(1.90m, ranges[20]);                                  // low 100.10 under close 102.00
        Assert.Equal(bars[19].Close - bars[20].Low, ranges[20]);
        Assert.Equal(1.40m, ranges[5]);                                   // an ordinary bar: its own high to low
        Assert.Equal(bars[5].High - bars[5].Low, ranges[5]);
    }

    /// <summary>
    /// THE OPENING-RANGE ACCUMULATOR IS NOT A LOOKBACK. Its window is a wall-clock interval, it resets
    /// each session, and it is UNDEFINED until that session's interval has had a bar — so a session
    /// whose opening range has no bars at all leaves the program unable to act rather than acting on
    /// yesterday's range. The calendar reading that sets these two flags is the evaluator's
    /// (`StrategyCalendar`); this is the accumulator on its own.
    /// </summary>
    [Fact]
    public void The_opening_range_resets_each_session_and_is_undefined_until_its_first_bar()
    {
        var high = IndicatorSeries.For(Decl(IndicatorKind.OpeningRangeHigh, BarSeries.Close, 0));
        var low = IndicatorSeries.For(Decl(IndicatorKind.OpeningRangeLow, BarSeries.Close, 0));

        void Bar(int i, bool newSession, bool inRange)
        {
            high.Push(SyntheticBars.Forty[i], newSession, inRange);
            low.Push(SyntheticBars.Forty[i], newSession, inRange);
        }

        // Session one opens with two bars inside the interval, then trades outside it.
        Bar(0, newSession: true, inRange: false);
        Assert.Null(high.Value);
        Assert.Null(low.Value);

        Bar(1, false, true);
        Assert.Equal(101.15m, high.Value);
        Assert.Equal(99.60m, low.Value);

        Bar(2, false, true);
        Assert.Equal(101.90m, high.Value);
        Assert.Equal(99.60m, low.Value);

        Bar(8, false, false);
        Assert.Equal(101.90m, high.Value);       // held for the rest of the session, not extended
        Assert.Equal(99.60m, low.Value);

        // Session two: undefined again from its first bar, and nothing carried over.
        Bar(9, newSession: true, inRange: false);
        Assert.Null(high.Value);
        Assert.Null(low.Value);

        Bar(16, false, true);
        Assert.Equal(108.35m, high.Value);
        Assert.Equal(106.10m, low.Value);
    }

    /// <summary>
    /// A ROLLING EXTREME COSTS ONE OPERATION ON AN ORDINARY BAR AND `p` WHEN THE EXTREME EXPIRES —
    /// which is what `StrategyLimits.MaxOperationsPerEvent` is sized against. Every other indicator
    /// is one operation a bar, always.
    /// </summary>
    [Fact]
    public void What_a_bar_costs_is_bounded_by_the_period()
    {
        var highest = IndicatorSeries.For(Decl(IndicatorKind.Highest, BarSeries.High, 5));
        var sma = IndicatorSeries.For(Decl(IndicatorKind.Sma, BarSeries.Close, 5));

        var extremes = SyntheticBars.Forty.Select(b => highest.Push(b, false, false)).ToList();
        var means = SyntheticBars.Forty.Select(b => sma.Push(b, false, false)).ToList();

        Assert.All(means, cost => Assert.Equal(1, cost));
        Assert.All(extremes, cost => Assert.InRange(cost, 1, 5));
        Assert.Contains(5, extremes);
        Assert.Contains(1, extremes);
    }
}
