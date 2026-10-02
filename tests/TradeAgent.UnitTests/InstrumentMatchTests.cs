using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;
using static TradeAgent.Tests.Unit.CostModelPinTests;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-cost-model item 3 — A PROGRAM MUST TRADE THE DATASET'S INSTRUMENT, OR NOTHING IS CHARGED FOR IT.
///
/// <para><b>What was wrong before this.</b> A program names its instrument on its first line, and the
/// quantity step a run rounds to is looked up by the DATASET's instrument, not by that line
/// (<c>Backtests.Increment</c>, rightly: the line is something the agent types). So a program saying
/// <c>instrument ES</c> ran over BTCUSDT bars at BTCUSDT's step, was recorded as a trial, and could be
/// judged — evidence about one instrument filed under a program about another. With a venue cost model
/// the mismatch would also price the judge's fee and step off an instrument the program never named.</para>
///
/// <para><b>The mutant this class exists to catch.</b> The instrument check removed: the ES program is
/// measured over BTCUSDT bars and charged a trial again.</para>
/// </summary>
public class InstrumentMatchTests(ITestOutputHelper log)
{
    const string EsProgram =
        "instrument ES\nsize capital_fraction 0.5\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 85750\nentry when close < 84500\n";

    static string GivenProgram(string role, string text)
    {
        var dir = Path.Combine(Paths.RoleHome(role), "strategies");
        Directory.CreateDirectory(dir);
        var file = $"instrument-{Guid.NewGuid():n}.strategy";
        File.WriteAllText(Path.Combine(dir, file), text);
        return Path.Combine("strategies", file);
    }

    /// <summary>
    /// A PROGRAM ABOUT ONE INSTRUMENT IS REFUSED OVER ANOTHER'S BARS — by the research runner before the
    /// run and its trial, and by the referee before the verdict — and both refusals name the two.
    ///
    /// <para>Red first: the research run went ahead and charged a trial, and the referee charged a
    /// judgement. A dataset that records no instrument is not refused for this; there is nothing on it
    /// to disagree with (<c>RefereeVerdictTests</c>, whose fixtures record none, all stand).</para>
    /// </summary>
    [Fact]
    public async Task An_instrument_dataset_mismatch_is_refused_before_a_trial_or_a_verdict_is_charged()
    {
        var w = await Given();
        using var _1 = w.Db;
        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, Cutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        // THE RESEARCH RUN: a program that says ES, over BTCUSDT bars, inside the window it may see.
        var refused = Assert.Throws<GatewayDeniedException>(() => w.Gw.Backtests.Run(
            AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk(GivenProgram(CouncilRoles.Research, EsProgram), w.Set.Id, Bar0,
                Bar0.AddMinutes(HoldoutAtBar - 1))));
        log.WriteLine(refused.Message);

        Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Info.Code);
        Assert.Contains("`instrument ES`", refused.Message, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, w.Gw.Campaigns.TrialsCharged(campaign.Id));
        Assert.Empty(w.Gw.Strategies.Runs(50));

        // THE REFEREE: the same program, as a version this installation holds.
        var version = Version(w.Db, EsProgram, Bar0);
        var verdict = RefereeOf(w).Verdict(version, campaign.Id);
        log.WriteLine(verdict.Why);

        Assert.False(verdict.Ok, "a program about ES was judged on BTCUSDT's held-back months");
        Assert.Null(verdict.Promotion);
        Assert.Contains("`instrument ES`", verdict.Why, StringComparison.Ordinal);
        Assert.Contains("BTCUSDT", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(0, w.Gw.Campaigns.VerdictsInLineage(campaign.Id));
        Assert.Empty(w.Gw.Strategies.Runs(50));
    }
}
