using TradeAgent.Core;
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
