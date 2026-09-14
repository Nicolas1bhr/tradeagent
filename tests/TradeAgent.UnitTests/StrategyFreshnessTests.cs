using TradeAgent.Core.Strategy;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE THREE DECLARATIONS A PROMOTED STRATEGY MAKES ABOUT TIME, AND THE IDENTITY THEY ARE PART OF.
///
/// <para><c>docs/COUNCIL.md</c>:96-97: "A promoted strategy declares its timeframe, its required data
/// freshness and its maximum decision age, and the runner checks them again when the intent reaches
/// execution". Before this unit the language had no way to spell any of the three, so rule 1's
/// freshness gate and :33's "never a late trade" had nothing to read.</para>
///
/// <para><b>They are in the canonical form, so they are part of the id.</b> Two programs that differ
/// only in how stale a decision may be are two programs: one of them may send an order the other
/// refuses, and results recorded against a shared id would be results about both.</para>
/// </summary>
public class StrategyFreshnessTests
{
    const string Body = """
        instrument BTCUSDT
        size fixed 1
        stop percent 1
        entry when close > 100
        """;

    static string With(string timeframe, string freshness, string decision) =>
        $"{Body}\ntimeframe {timeframe}\ndata_freshness {freshness}\nmax_decision_age {decision}";

    /// <summary>RED before this unit: `timeframe` was not a declaration the language had.</summary>
    [Fact]
    public void A_program_declaring_a_timeframe_a_freshness_and_a_decision_age_parses()
    {
        var parse = StrategyParser.Parse(With("1m", "5m", "90s"));

        Assert.True(parse.Ok, parse.Why);
        Assert.Equal(TimeSpan.FromMinutes(1), parse.Program!.Freshness!.Timeframe);
        Assert.Equal(TimeSpan.FromMinutes(5), parse.Program.Freshness.DataFreshness);
        Assert.Equal(TimeSpan.FromSeconds(90), parse.Program.Freshness.MaxDecisionAge);
    }

    /// <summary>
    /// THE MUTANT'S TEST. Leave the three out of <c>StrategyCanonical.Of</c> and two programs whose
    /// bounds differ share one id — the same failure a hash over the source's spacing would cause,
    /// pointed at the one declaration that decides whether an order is sent.
    /// </summary>
    [Fact]
    public void Two_programs_differing_only_in_their_bounds_are_two_ids()
    {
        var tight = StrategyParser.Parse(With("1m", "5m", "30s")).Program!;
        var loose = StrategyParser.Parse(With("1m", "5m", "10m")).Program!;

        Assert.NotEqual(tight.StrategyId, loose.StrategyId);
        Assert.NotEqual(tight.Canonical, loose.Canonical);
    }

    /// <summary>A program that declares none of them still parses, and says so rather than defaulting.</summary>
    [Fact]
    public void A_program_that_declares_none_of_them_has_no_bounds_at_all()
    {
        var parse = StrategyParser.Parse(Body);

        Assert.True(parse.Ok, parse.Why);
        Assert.Null(parse.Program!.Freshness);
        Assert.Contains("timeframe none", parse.Program.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// ALL THREE OR NONE. A timeframe with no freshness bound beside it reads like a gate and is not
    /// one, and the intent's own block is all-or-nothing for the same reason: half a bound is a
    /// refusal the dispatcher cannot make.
    /// </summary>
    [Theory]
    [InlineData("timeframe 1m", "data_freshness", "max_decision_age")]
    [InlineData("data_freshness 5m", "timeframe", "max_decision_age")]
    [InlineData("timeframe 1m\nmax_decision_age 30s", "data_freshness", "data_freshness")]
    public void Declaring_some_of_the_three_is_refused_and_names_the_missing_ones(
        string declared, string first, string second)
    {
        var parse = StrategyParser.Parse($"{Body}\n{declared}");

        Assert.False(parse.Ok);
        Assert.Contains(first, parse.Why, StringComparison.Ordinal);
        Assert.Contains(second, parse.Why, StringComparison.Ordinal);
    }

    /// <summary>A duration is a whole number and a unit, and nothing else is one.</summary>
    [Theory]
    [InlineData("1m", 60)]
    [InlineData("30s", 30)]
    [InlineData("2h", 7200)]
    [InlineData("1d", 86400)]
    public void A_duration_is_a_whole_number_and_a_unit(string text, int seconds)
    {
        var parse = StrategyParser.Parse(With(text, "7d", "7d"));

        Assert.True(parse.Ok, parse.Why);
        Assert.Equal(TimeSpan.FromSeconds(seconds), parse.Program!.Freshness!.Timeframe);
    }

    [Theory]
    [InlineData("1")]          // no unit
    [InlineData("1.5m")]       // not whole
    [InlineData("0s")]         // a bound of nothing is satisfiable by nothing
    [InlineData("-1m")]
    [InlineData("8d")]         // past the week a bound may be
    [InlineData("1w")]
    [InlineData("01:00")]      // a wall clock is not a duration
    public void A_bound_that_is_not_a_whole_positive_duration_is_refused(string text)
    {
        var parse = StrategyParser.Parse(With(text, "5m", "30s"));

        Assert.False(parse.Ok);
        Assert.Contains("line", parse.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SAME BOUND, SPELLED TWO WAYS, IS ONE PROGRAM. `60s` and `1m` are the same duration, and the
    /// canonical form writes both as seconds — the rule every other number in this language follows.
    /// </summary>
    [Fact]
    public void One_duration_spelled_two_ways_is_one_id()
    {
        var minutes = StrategyParser.Parse(With("1m", "5m", "2m")).Program!;
        var seconds = StrategyParser.Parse(With("60s", "300s", "120s")).Program!;

        Assert.Equal(minutes.Canonical, seconds.Canonical);
        Assert.Equal(minutes.StrategyId, seconds.StrategyId);
    }

    /// <summary>Declared twice is refused, as every other once-only declaration is.</summary>
    [Fact]
    public void A_bound_declared_twice_is_refused()
    {
        var parse = StrategyParser.Parse($"{With("1m", "5m", "30s")}\ntimeframe 5m");

        Assert.False(parse.Ok);
        Assert.Contains("timeframe", parse.Why, StringComparison.Ordinal);
    }
}
