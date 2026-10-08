using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A RISK-SIZED ENTRY MAY DECLARE THE MOST OF ITS CAPITAL IT SPENDS (<c>U-size-cap</c>):
/// <c>size risk_fraction &lt;f&gt; max_capital_fraction &lt;c&gt;</c>, so a tight stop sizes to the cap instead of to no trade.
///
/// <para><b>Why the clause exists.</b> A risk size is <c>equity * f / (close - stop)</c>. With <c>stop percent p</c> that is
/// the constant <c>100f/p</c> of equity — a capital fraction in disguise — but with <c>stop atr</c> or <c>stop fixed</c> it is
/// <c>f * close / distance</c> of it, without bound as the stop nears the price: no <c>f</c> keeps a tight-stop signal funded
/// and no declared capital does either, because the size scales with the capital. A backtest drops such an entry whole
/// ("the declared capital cannot pay for this fill") and the gateway refuses it whole on paper (ALLOCATION_EXCEEDED). The
/// cap makes the size the smaller of the risk size and <c>capital * c / close</c> — what <c>capital_fraction</c> reads — so
/// a cap only ever makes a size smaller, and every gate downstream is untouched.</para>
///
/// <para><b>And nothing else moves.</b> A program without the clause parses, hashes, sizes and traces byte for byte as
/// before: the canonical form writes the cap only when one is declared, the manifest and the evaluator's version do not
/// move, and the golden vectors run unedited.</para>
/// </summary>
public class SizeCapTests(ITestOutputHelper log)
{
    static StrategyProgram Parsed(string text)
    {
        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        return parse.Program!;
    }

    static readonly DateTimeOffset Monday = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <paramref name="count"/> one-minute bars from <see cref="Monday"/>, each opening and closing at <paramref name="price"/>
    /// and reaching <paramref name="half"/> either side — so every true range is <c>2 * half</c>, and so is every ATR.
    /// </summary>
    static IReadOnlyList<KlineBar> Flat(int count, decimal price, decimal half) =>
        [.. Enumerable.Range(0, count).Select(i => new KlineBar(Monday.AddMinutes(i), price, price + half, price - half, price, 1m))];

    /// <summary>Bars from explicit OHLC rows, one minute apart from <see cref="Monday"/>, so nothing about a fixture is implicit.</summary>
    static IReadOnlyList<KlineBar> Rows(params decimal[][] rows) =>
        [.. rows.Select((r, i) => new KlineBar(Monday.AddMinutes(i), r[0], r[1], r[2], r[3], 1m))];

    /// <summary>A program whose size line is <paramref name="size"/>, on line 4 — under two constants it may read.</summary>
    static string WithSize(string size) => $"""
        instrument BTCUSDT
        const cap = 0.5
        const flag = true
        {size}
        stop percent 2
        exit when close < 1
        entry when close > 1
        """;

    // ---------------------------------------------------------------- item 1: the clause

    /// <summary>
    /// (d) A CAP WHERE IT MEANS NOTHING IS REFUSED, IN WORDS, NAMING THE SIZE LINE: on a fixed quantity (a constant) and on
    /// a capital fraction (already its own cap), at or below 0, above one whole (the leverage words), with no value or a
    /// value that is not a number, and any other tail on the size line.
    /// </summary>
    [Theory]
    [InlineData("size fixed 1 max_capital_fraction 0.5", "`size fixed` is a constant quantity with nothing to cap")]
    [InlineData("size fixed 1 max_capital_fraction", "`size fixed` is a constant quantity with nothing to cap")]
    [InlineData("size capital_fraction 0.5 max_capital_fraction 0.5", "already is a fraction of capital — it is its own cap")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction 0", "`max_capital_fraction` must be above 0, and this one is 0")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction -0.5", "`max_capital_fraction` must be above 0, and this one is -0.5")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction 1.5", "1.5 is more than everything there is, which is leverage")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction", "and nothing after it")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction 0.5 0.6", "and nothing after it")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction flag", "`flag` is a true/false constant, so it is not a number")]
    [InlineData("size risk_fraction 0.01 max_capital_fraction nope", "`nope` is not a number and is not a declared constant")]
    [InlineData("size risk_fraction 1.5 max_capital_fraction 0.5", "1.5 is more than everything there is, which is leverage")]
    [InlineData("size risk_fraction 0.01 0.95", "`size risk_fraction <fraction> max_capital_fraction <fraction>`")]
    [InlineData("size risk_fraction 0.01 cap 0.95", "`size risk_fraction <fraction> max_capital_fraction <fraction>`")]
    [InlineData("size half 0.01 max_capital_fraction 0.5", "`half` is not a way to size")]
    [InlineData("size max_capital_fraction 0.5", "`max_capital_fraction` is not a way to size")]
    public void A_cap_where_it_means_nothing_is_refused_naming_the_size_line(string size, string words)
    {
        var parse = StrategyParser.Parse(WithSize(size));
        log.WriteLine($"{size,-56} → {parse.Why}");

        Assert.False(parse.Ok, $"`{size}` parsed, and it should have been refused");
        Assert.Equal(4, parse.Refusal!.Line);
        Assert.StartsWith("line 4: ", parse.Why, StringComparison.Ordinal);
        Assert.Contains(words, parse.Refusal.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CLAUSE READS A NUMBER OR A DECLARED NUMBER CONSTANT, in any letter case, and the typed program carries it — while
    /// a program without it carries none.
    /// </summary>
    [Fact]
    public void A_cap_is_a_number_or_a_declared_constant_and_the_program_carries_it()
    {
        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m, 0.95m),
            Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction 0.95")).Sizing);
        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m, 0.5m),
            Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction cap")).Sizing);
        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m, 1m),
            Parsed(WithSize("SIZE Risk_Fraction 0.01\tMAX_CAPITAL_FRACTION 1")).Sizing);
        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m),
            Parsed(WithSize("size risk_fraction 0.01")).Sizing);
        Assert.Null(Parsed(WithSize("size risk_fraction 0.01")).Sizing.MaxCapitalFraction);

        // RISK SIZING STILL NEEDS A STOP, capped or not, and the refusal still names the size line.
        var noStop = StrategyParser.Parse(WithSize("size risk_fraction 0.01 max_capital_fraction 0.95").Replace("stop percent 2\n", ""));
        Assert.False(noStop.Ok);
        Assert.Equal(4, noStop.Refusal!.Line);
        Assert.Contains("declares no stop", noStop.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE CANONICAL SIZE FORM GAINS <c> max_capital_fraction:&lt;c&gt;</c> ONLY WHEN ONE IS DECLARED. Without the clause it is
    /// the line it always was; with it, one meaning has one text whatever the spelling, the id is the documented formula's
    /// answer, and a capped program never shares an id with its uncapped twin — nor with one capped at another fraction.
    ///
    /// <para><b>The mutant</b> — the canonical form writing a cap when none is declared — goes red here on the first line,
    /// and in <c>DeclaredBarsGuardTests</c> and <c>FeatureProgramGrammarTests</c> (a) on every v1 id.</para>
    /// </summary>
    [Fact]
    public void The_cap_is_in_the_canonical_form_only_when_declared()
    {
        var uncapped = Parsed(WithSize("size risk_fraction 0.01"));
        var capped = Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction 0.95"));
        log.WriteLine(capped.Canonical);

        Assert.Contains("\nsize risk_fraction:0.01\n", uncapped.Canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("max_capital_fraction", uncapped.Canonical, StringComparison.Ordinal);
        Assert.Contains("\nsize risk_fraction:0.01 max_capital_fraction:0.95\n", capped.Canonical, StringComparison.Ordinal);
        Assert.Equal(uncapped.Canonical.Replace("risk_fraction:0.01\n", "risk_fraction:0.01 max_capital_fraction:0.95\n",
            StringComparison.Ordinal), capped.Canonical);

        foreach (var program in new[] { uncapped, capped })
            Assert.Equal(Sha256Hex.Of($"{program.Canonical}\n{program.Parameters}\n{StrategyVersions.Manifest}"), program.StrategyId);
        Assert.Equal("language=1;indicators=1;calendar=1", StrategyVersions.Manifest);

        // ONE MEANING, ONE TEXT: letter case, spacing and trailing zeros are not part of it.
        var respelled = Parsed(WithSize("size   RISK_FRACTION 0.010   Max_Capital_Fraction\t0.950"));
        Assert.Equal(capped.Canonical, respelled.Canonical);
        Assert.Equal(capped.StrategyId, respelled.StrategyId);

        // A CONSTANT IS RESOLVED INTO THE FORM, and is a parameter as every constant is.
        var byConstant = Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction cap"));
        Assert.Contains("\nsize risk_fraction:0.01 max_capital_fraction:0.5\n", byConstant.Canonical, StringComparison.Ordinal);
        Assert.Equal("cap=number:0.5\nflag=boolean:true", byConstant.Parameters);

        // THREE PROGRAMS, THREE IDS: uncapped, capped at 0.95 and capped at the whole of the capital.
        var whole = Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction 1"));
        Assert.Equal(3, new HashSet<string> { uncapped.StrategyId, capped.StrategyId, whole.StrategyId }.Count);
    }

    /// <summary>
    /// A CAPPED PROGRAM REQUIRES THE CAP, AND BOTH READERS IMPLEMENT IT. <c>max_capital_fraction</c> is a declaration kind of
    /// its own, named right after the size it caps; a program without it does not require it; the backtest and the paper
    /// runner both size through the evaluator and so both implement it; and a reader that did not would refuse the program
    /// in words rather than send an entry larger than the program says one may ever be.
    /// </summary>
    [Fact]
    public void A_capped_program_requires_the_cap_and_both_readers_implement_it()
    {
        var capped = Parsed(WithSize("size risk_fraction 0.01 max_capital_fraction 0.95"));
        var uncapped = Parsed(WithSize("size risk_fraction 0.01"));

        Assert.Equal(
            [
                StrategyDeclarations.Instrument, StrategyDeclarations.Const, StrategyDeclarations.Size,
                StrategyDeclarations.MaxCapitalFraction, StrategyDeclarations.Stop, StrategyDeclarations.Exit,
                StrategyDeclarations.Entry
            ],
            capped.Requires);
        Assert.DoesNotContain(StrategyDeclarations.MaxCapitalFraction, uncapped.Requires);

        Assert.Contains(StrategyDeclarations.MaxCapitalFraction, Backtest.Implements);
        Assert.Contains(StrategyDeclarations.MaxCapitalFraction, ForwardRuns.Implements);
        Assert.Null(StrategyDeclarations.Refusal(capped, Backtest.Implements, "the backtest"));
        Assert.Null(ForwardRuns.Refuses(capped));

        var without = StrategyDeclarations.All.Where(k => k != StrategyDeclarations.MaxCapitalFraction).ToList();
        var refused = StrategyDeclarations.Refusal(capped, without, "a reader that sizes without the cap");
        log.WriteLine(refused);
        Assert.StartsWith("this program requires `max_capital_fraction`, which a reader that sizes without the cap does not implement",
            refused, StringComparison.Ordinal);
        Assert.Null(StrategyDeclarations.Refusal(uncapped, without, "a reader that sizes without the cap"));
    }

    // ---------------------------------------------------------------- item 2: the size

    /// <summary>
    /// (a) A CAPPED RISK SIZE IS THE SMALLER OF THE TWO: the risk size, <c>equity * f / (close - stop)</c>, and
    /// <c>capital * c / close</c> — what <c>capital_fraction</c> reads, so the backtest and the paper runner agree. The account
    /// reads a capital of 8,000 and an equity of 10,000, as a paper run's does when its ceiling is 8,000 and it is up 2,000:
    /// the risk is measured on the equity and the cap on the capital.
    ///
    /// <para>A SMALL ATR — bars a tenth of a unit wide at 100, so atr(3) is 0.1 and the stop 0.2 below the close — asks
    /// <c>10,000 * 0.01 / 0.2 = 500</c>, fifty thousand of notional, and is capped at <c>8,000 * 0.95 / 100 = 76</c>. A LARGE
    /// one — four units wide, atr(3) 4, the stop 8 below — asks 12.5, and 12.5 it is. Without the clause the small ATR
    /// sizes to 500, exactly as it did before this unit.</para>
    ///
    /// <para><b>RED before item 2</b>: the capped program parsed and sized 500. <b>The mutant</b> — the <c>min</c> dropped,
    /// so the risk size is answered whatever the cap — goes red here, in (b) and in (f).</para>
    /// </summary>
    [Fact]
    public void A_capped_risk_size_is_the_smaller_of_the_two()
    {
        const string Text = """
            instrument BTCUSDT
            size risk_fraction 0.01 max_capital_fraction 0.95
            stop atr 2 3
            exit when close < 1
            entry when close > 0
            """;
        var capped = Parsed(Text);
        var uncapped = Parsed(Text.Replace(" max_capital_fraction 0.95", "", StringComparison.Ordinal));
        var account = new AccountReading(8_000m, 10_000m, PositionSide.Flat, 0m, 0m, false, 0);

        StrategyIntent Entry(StrategyProgram program, IReadOnlyList<KlineBar> bars)
        {
            var run = StrategyEvaluator.Run(program, bars, (_, _) => account);
            Assert.False(run.Faulted, run.FaultReason);
            var intent = Assert.Single(run.Intents);
            log.WriteLine($"{(program.Sizing.MaxCapitalFraction is null ? "uncapped" : "capped  ")} stop {intent.StopPrice} → {intent.Quantity}");
            return intent;
        }

        // A SMALL ATR: the risk size is fifty thousand of notional, and the cap is what is asked.
        var small = Entry(capped, Flat(4, 100m, 0.05m));
        Assert.Equal(100m - 2m * 0.1m, small.StopPrice);
        Assert.Equal(8_000m * 0.95m / 100m, small.Quantity);
        Assert.Equal(76m, small.Quantity);
        Assert.Equal(new Sizing(SizingKind.EquityRiskFraction, 0.01m, 0.95m), small.Sizing);

        // A LARGE ATR: the risk size is the smaller, and the cap changes nothing.
        var large = Entry(capped, Flat(4, 100m, 2m));
        Assert.Equal(100m - 2m * 4m, large.StopPrice);
        Assert.Equal(10_000m * 0.01m / 8m, large.Quantity);
        Assert.Equal(12.5m, large.Quantity);

        // WITHOUT THE CLAUSE, the small ATR sizes as it always did.
        Assert.Equal(10_000m * 0.01m / 0.2m, Entry(uncapped, Flat(4, 100m, 0.05m)).Quantity);
        Assert.Equal(12.5m, Entry(uncapped, Flat(4, 100m, 2m)).Quantity);
    }

    /// <summary>
    /// (b) A TIGHT STOP FILLS CAPPED AND IS NO TRADE UNCAPPED — one backtest over a fixture whose ATR shrinks.
    ///
    /// <para>Four bars four units wide put atr(3) at 4; the fifth rises and signals with a stop 4 below, a risk size of
    /// <c>10,000 * 0.01 / 4 = 25</c> — a quarter of the capital — and both programs fill 25 at the next open: the cap
    /// (<c>10,000 * 0.5 / 100.1</c>, about 49.95) is the larger and changes nothing. The maximum hold closes it flat, and
    /// nine bars a tenth of a unit wide shrink the ATR to about 0.2 before the next rise signals: a risk size of about
    /// 496, near fifty thousand of notional. Without the clause the declared capital cannot pay for it; with it the entry
    /// sizes to <c>10,000 * 0.5 / 100.15</c>, the trace's Signal line carries that, and it fills rounded DOWN to 49.925.</para>
    /// </summary>
    [Fact]
    public void A_tight_stop_fills_capped_and_is_no_trade_uncapped()
    {
        const string Text = """
            instrument BTCUSDT
            size risk_fraction 0.01 max_capital_fraction 0.5
            stop atr 1 3
            max_hold_bars 1
            exit when close < 1
            entry when close > close[1]
            """;

        decimal[] wide = [100m, 102m, 98m, 100m], narrow = [100.1m, 100.15m, 100.05m, 100.1m];
        var bars = Rows(
            wide, wide, wide, wide,                     // 0-3: atr(3) 4, nothing rises
            [100m, 102.1m, 98.1m, 100.1m],              // 4: rises with atr(3) still 4 — the first entry signals
            narrow,                                     // 5: both fill 25 at 100.1
            narrow,                                     // 6: held one bar — the maximum hold closes it at 100.1, flat
            narrow, narrow, narrow, narrow, narrow, narrow,   // 7-12: the ATR shrinks toward 0.25
            [100.1m, 100.2m, 100.1m, 100.15m],          // 13: rises with atr(3) about 0.2 — the second entry signals
            [100.15m, 100.2m, 100.1m, 100.15m]);        // 14: the capped one fills here; the uncapped one cannot pay

        var model = ExecutionModel.Declare(0m, 0m, 0.001m, 10_000m).Model!;
        BacktestResult Run(string text)
        {
            var result = Backtest.Run(Parsed(text), new BacktestRequest(7, "sha-of-the-normalised-file", model), bars);
            log.WriteLine(result.Trace.Text);
            Assert.Equal(BacktestOutcome.COMPLETED, result.Outcome);
            return result;
        }

        var capped = Run(Text);
        var uncapped = Run(Text.Replace(" max_capital_fraction 0.5", "", StringComparison.Ordinal));

        // THE WIDE STOP: one signal, one fill, one trade, the same in both — the cap is the larger of the two.
        foreach (var run in new[] { capped, uncapped })
        {
            var first = run.Trace.Of(BacktestEventKind.Signal).ToList()[0];
            Assert.Equal(5L, first.Ordinal);
            Assert.Equal(25m, first.Quantity);
            var trade = run.Trades[0];
            Assert.Equal(25m, trade.Quantity);
            Assert.Equal(100.1m, trade.EntryPrice);
            Assert.Equal(ExitReason.MaxHoldBars, trade.Reason);
        }

        // THE TIGHT STOP, UNCAPPED: the risk size, which the declared capital cannot pay for — no trade, with the reason.
        var asked = uncapped.Trace.Of(BacktestEventKind.Signal).ToList()[1];
        Assert.Equal(14L, asked.Ordinal);
        Assert.True(asked.Quantity * 100.15m > 10_000m, $"the uncapped risk size {asked.Quantity} is payable");
        var refused = Assert.Single(uncapped.Trace.Of(BacktestEventKind.NoTrade));
        Assert.Contains("the declared capital cannot pay for this fill", refused.Reason, StringComparison.Ordinal);
        Assert.Single(uncapped.Trace.Of(BacktestEventKind.Fill));

        // THE TIGHT STOP, CAPPED: half the capital at the signal's close, in the trace's Signal line, filled rounded DOWN.
        var cappedAsk = capped.Trace.Of(BacktestEventKind.Signal).ToList()[1];
        Assert.Equal(14L, cappedAsk.Ordinal);
        Assert.Equal(10_000m * 0.5m / 100.15m, cappedAsk.Quantity);
        Assert.Empty(capped.Trace.Of(BacktestEventKind.NoTrade));
        var fills = capped.Trace.Of(BacktestEventKind.Fill).ToList();
        Assert.Equal(2, fills.Count);
        Assert.Equal(49.925m, fills[1].Quantity);
        Assert.Equal(100.15m, fills[1].Price);
        Assert.Equal(15L, fills[1].Ordinal);
    }

    // ---------------------------------------------------------------- item 3: the words

    /// <summary>
    /// THE ROLE THAT WRITES PROGRAMS IS TOLD, where it reads: the backtest's <c>capital</c> argument in the schema says more
    /// capital does not fund a risk-sized entry and names the clause, and the reference copied into its folder has the
    /// clause in its grammar, the arithmetic, and that a limit downstream refuses an entry whole.
    /// </summary>
    [Fact]
    public void The_role_is_told_more_capital_does_not_fund_a_risk_size_and_how_to_cap_it()
    {
        var backtest = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Backtest);
        var capital = Assert.Single(backtest.Args, a => a.Name == "capital").Description;
        log.WriteLine(capital);
        Assert.Contains("More capital does not fund a 'size risk_fraction' entry", capital, StringComparison.Ordinal);
        Assert.Contains("'size risk_fraction 0.01 max_capital_fraction 0.95'", capital, StringComparison.Ordinal);

        var reference = File.ReadAllText(Path.Combine(DayOnePrograms.RepoRoot(), "docs", "STRATEGY-LANGUAGE.md"))
            .ReplaceLineEndings("\n");
        Assert.Contains("| \"size\" \"risk_fraction\" value (\"max_capital_fraction\" value)?", reference, StringComparison.Ordinal);
        Assert.Contains("**A risk size can ask for more than everything, and `max_capital_fraction` caps it.**", reference,
            StringComparison.Ordinal);
        Assert.Contains("REFUSES an entry WHOLE: nothing downstream makes an order smaller to fit", reference,
            StringComparison.Ordinal);
        Assert.DoesNotContain("the gateway's limits happen downstream", reference, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CAP APPLIES AT THE SIGNAL'S CLOSE, SO FEES, SLIPPAGE AND THE NEXT OPEN COME ON TOP — the reference's arithmetic,
    /// run. Under the venue cost model's fee (0.1 %) and slippage (<see cref="VenueCostModel.SlippageRate"/>), a stop half a
    /// unit under a close of 100 asks for 200 units, twice the 10,000 of capital, so the cap decides the size:
    /// <c>max_capital_fraction 1</c> is 100 units, which cost 10,012 at an open equal to the close — the declared capital
    /// cannot pay — and fill only at an open about 0.12 % below it; <c>0.95</c> is 95 units, which fill at an open equal to
    /// the close and at one 5 % above it.
    /// </summary>
    [Theory]
    [InlineData("1", "100", false)]
    [InlineData("1", "99.88", true)]
    [InlineData("0.95", "100", true)]
    [InlineData("0.95", "105", true)]
    public void A_cap_applies_at_the_close_so_costs_and_the_next_open_come_on_top(string cap, string open, bool fills)
    {
        var c = decimal.Parse(cap, System.Globalization.CultureInfo.InvariantCulture);
        var o = decimal.Parse(open, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0.0002m, VenueCostModel.SlippageRate);

        var program = Parsed($"""
            instrument BTCUSDT
            size risk_fraction 0.01 max_capital_fraction {cap}
            stop fixed 99.5
            exit when close < 1
            entry when close > 99.95
            """);
        var bars = Rows([100m, 100.05m, 99.95m, 100m], [o, o + 0.05m, o - 0.05m, o]);
        var model = ExecutionModel.Declare(0.001m, VenueCostModel.SlippageRate, 0.001m, 10_000m).Model!;

        var run = Backtest.Run(program, new BacktestRequest(7, "sha-of-the-normalised-file", model), bars);
        log.WriteLine(run.Trace.Text);

        var signal = run.Trace.Of(BacktestEventKind.Signal).First();
        Assert.Equal(10_000m * c / 100m, signal.Quantity);
        Assert.True(10_000m * 0.01m / 0.5m > signal.Quantity, "the cap did not decide this size");

        if (fills)
        {
            var fill = Assert.Single(run.Trace.Of(BacktestEventKind.Fill));
            Assert.Equal(model.RoundDown(10_000m * c / 100m), fill.Quantity);
            Assert.Equal(model.Buy(o), fill.Price);
        }
        else
        {
            Assert.Empty(run.Trace.Of(BacktestEventKind.Fill));
            Assert.Contains("the declared capital cannot pay for this fill",
                run.Trace.Of(BacktestEventKind.NoTrade).First().Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// GUARD — A STORED PROGRAM WITH A CONSTANT NAMED <c>max_capital_fraction</c> STILL PARSES, TO THE TEXT IT ALWAYS HAD. The
    /// language had no such word before this unit, so a stored program may use it as a name; the clause is read only as the
    /// size line's third word, and reserving it would turn a recorded version into a refusal. Green on the base, as a guard
    /// is; the canonical text is pinned whole, so a change that kept the parse and moved the id is caught too.
    /// </summary>
    [Fact]
    public void A_stored_program_with_a_const_named_max_capital_fraction_still_parses()
    {
        var stored = Parsed("""
            instrument BTCUSDT
            const max_capital_fraction = 0.5
            size capital_fraction max_capital_fraction
            stop percent 2
            exit when close < 1
            entry when close > max_capital_fraction
            """);

        Assert.Equal(new Sizing(SizingKind.CapitalFraction, 0.5m), stored.Sizing);
        Assert.Equal("""
            program/1
            instrument BTCUSDT
            zone UTC
            timeframe none
            freshness none
            decisionage none
            days all
            size capital_fraction:0.5
            stop percent:2
            target none
            hold none
            warmup 1
            exit (< close 1)
            entry (> close 0.5)

            """.ReplaceLineEndings("\n"), stored.Canonical);
        Assert.Equal("max_capital_fraction=number:0.5", stored.Parameters);
    }
}
