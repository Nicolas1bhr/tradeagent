using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE BAR AN INTENT WAS COMPUTED FROM, OUT OF THE EVALUATOR AND ONTO THE ORDER.
///
/// <para>Before this unit nothing that reached the gateway named a time a gate could read. The
/// evaluator already stamped the bar's open (<c>StrategyIntent.Bar</c>) and its close
/// (<c>NotBefore</c>) — what was missing was any way to carry either past that assembly, and the
/// bounds to judge them against.</para>
///
/// <para><b>The mutant this class exists to catch</b> is the bar's OPEN time carried as the decision
/// instant. The signal was computed from the bar's CLOSE; reading the open instead makes a
/// one-minute intent read one whole bar older than it is, and a program whose
/// <c>max_decision_age</c> is shorter than its timeframe would then refuse every order at the
/// instant it was made.</para>
/// </summary>
public class IntentDecisionTests
{
    static readonly DateTimeOffset Bar0 = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    const string Bounded =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "stop percent 1\nentry when close > 100\n";

    const string Unbounded =
        "instrument BTCUSDT\nsize fixed 1\nstop percent 1\nentry when close > 100\n";

    /// <summary>One entry intent, out of a real evaluation over real closed bars.</summary>
    static StrategyIntent Signalled(string text)
    {
        var program = StrategyParser.Parse(text).Program!;
        var state = EvaluationState.Start(program, barInterval: TimeSpan.FromMinutes(1));
        StrategyIntent? intent = null;

        for (var i = 0; i < 3 && intent is null; i++)
        {
            var bar = new KlineBar(Bar0.AddMinutes(i), 101m, 102m, 100m, 101m, 1m);
            var outcome = StrategyEvaluator.Step(state, bar, AccountReading.Flat(10_000m));
            Assert.True(outcome.Ok, outcome.FaultReason);
            intent = outcome.Intent;
        }

        Assert.NotNull(intent);
        return intent!;
    }

    /// <summary>The evaluator stamps the program's bounds onto what it emits.</summary>
    [Fact]
    public void An_intent_carries_the_bounds_of_the_program_that_produced_it()
    {
        var intent = Signalled(Bounded);

        Assert.Equal(
            new FreshnessBounds(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)),
            intent.Freshness);
    }

    /// <summary>
    /// THE MUTANT'S TEST: the decision instant carried onto the order is the bar's CLOSE, and the
    /// bar's open is carried beside it rather than instead of it.
    /// </summary>
    [Fact]
    public void The_decision_carried_onto_an_order_is_the_bars_close_and_its_open()
    {
        var intent = Signalled(Bounded);
        var decision = IntentDecision.From(intent);

        Assert.NotNull(decision);
        Assert.Equal(intent.Bar, decision!.BarOpen);
        Assert.Equal(intent.Bar.AddMinutes(1), decision.BarClose);
        Assert.Equal(intent.NotBefore, decision.BarClose);
        Assert.Equal(TimeSpan.FromMinutes(2), decision.DataFreshness);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.MaxDecisionAge);

        // And the age it is judged on is measured from that close.
        Assert.Equal(TimeSpan.FromSeconds(10), decision.AgeAt(decision.BarClose.AddSeconds(10)));
    }

    /// <summary>A program with no bounds produces no block, rather than a block with generous ones.</summary>
    [Fact]
    public void An_intent_from_a_program_with_no_bounds_carries_no_decision_block()
    {
        var intent = Signalled(Unbounded);

        Assert.Null(intent.Freshness);
        Assert.Null(IntentDecision.From(intent));
    }

    /// <summary>
    /// IT SURVIVES THE ROUND TRIP THROUGH <c>ParametersJson</c>, which is what an approval reads back:
    /// an order parked for a person must still be measured against the bar it was decided on.
    /// </summary>
    [Fact]
    public void The_decision_round_trips_through_the_persisted_parameters()
    {
        var decision = IntentDecision.From(Signalled(Bounded))!;
        var intent = new PlaceIntent("BTCUSDT", ConnectorSdk.OrderSide.Buy, ConnectorSdk.OrderType.Market,
            1m, null, null, ConnectorSdk.TimeInForce.Day, null) { Decision = decision };

        var read = Json.Read<PlaceIntent>(Json.Write(intent))!;

        Assert.Equal(decision, read.Decision);
    }

    /// <summary>An order nobody's strategy decided carries none, and that is not an omission.</summary>
    [Fact]
    public void An_order_with_no_strategy_behind_it_carries_no_decision()
    {
        var read = Json.Read<PlaceIntent>(Json.Write(TestEnv.Buy()))!;

        Assert.Null(read.Decision);
    }
}
