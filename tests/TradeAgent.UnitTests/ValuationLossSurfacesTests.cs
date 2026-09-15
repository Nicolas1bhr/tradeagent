using TradeAgent.AgentRuntime;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// WHAT THE THRESHOLDS DO, AND WHAT THEY DO NOT PROMISE — said in the daily report, in words, on
/// every day and whatever the budgets are set to.
///
/// <para>"Most it may lose in one day" reads as a bound on the loss. It is not one. It is the figure
/// at which TradeAgent intervenes, and what the intervention is left holding is whatever a market
/// order fills at after the market has gapped, across whatever spread it has to cross, less fees the
/// platform may never have reported. A report that printed the budget beside the day's figure and
/// said nothing else would be handing the owner a guarantee this product cannot give, and an owner
/// who believes a loss is bounded takes risks they would not otherwise take.</para>
///
/// <para>The sampling delay is the other half of it, and it is MEASURED rather than declared: the
/// gateway times its own reading of the open book on a real wall clock and the report prints what it
/// took. A constant compiled into this build would be a claim about somebody else's computer.</para>
/// </summary>
public class ValuationLossSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);

    /// <summary>A feed that has stopped: every price the platform serves is a memory.</summary>
    static readonly TimeSpan Silent = TimeSpan.FromMinutes(10);

    static async Task<(TradingGateway Gw, FakeConnector Conn, TradeAgent.Core.Db.Database Db, TestClock Clock)>
        Ready(decimal dailyBudget = 1_000m, decimal exitAfterMinutes = 15m)
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(s =>
        {
            s.Risk.MaxDailyLoss = dailyBudget;
            s.Risk.ValuationLossExitMinutes = exitAfterMinutes;
        }, new GatewayOptions { Clock = clock });
        return (gw, conn, db, clock);
    }

    /// <summary>
    /// THE REPORT SAYS THE BUDGET IS A THRESHOLD AND NOT A MAXIMUM LOSS (item 3).
    ///
    /// <para>All four thresholds are named with the numbers actually in force — the tick the book is
    /// valued on, the window a second reading has to agree inside, the age at which a price is
    /// refused, and the bound past which a position nobody can value is closed — together with what
    /// they cannot bound: the gap between two readings, the spread the closing order crosses, the
    /// slippage on the way and the fees the platform did not report.</para>
    ///
    /// <para><b>The mutant this watches</b> is printing the line only for an installation whose
    /// budget is zero. That is the one reader with nothing to be misled about, and it withholds the
    /// sentence from every owner who has set a budget and is relying on it: a live budget then reads
    /// as a guarantee, which is exactly the defect.</para>
    /// </summary>
    [Fact]
    public async Task The_report_states_what_the_thresholds_do_and_that_they_are_not_a_maximum_loss()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        clock.Advance(Tick);
        await gw.LossWatchAsync();

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        log.WriteLine($"MEASURED sampling: one reading of the open book took {gw.LastLossWatchTook?.TotalMilliseconds:0.###} ms "
                      + $"on this machine; the gap between two readings is up to {TimeSpan.FromSeconds(15)}");

        // A BUDGET IS SET, so the mutant's "only when it is zero" never prints and this fails first.
        Assert.Contains("what these limits are and are not", text, StringComparison.Ordinal);
        Assert.Contains("none of them is a maximum loss", text, StringComparison.Ordinal);
        log.WriteLine(text.Split('\n').First(l => l.Contains("what these limits are and are not", StringComparison.Ordinal)));

        // THE FOUR THRESHOLDS, with the numbers in force.
        Assert.Contains("values your open book every 15 seconds", text, StringComparison.Ordinal);
        Assert.Contains("agreeing reading within 1 minute", text, StringComparison.Ordinal);
        Assert.Contains("older than 30 seconds", text, StringComparison.Ordinal);
        Assert.Contains("unable to value at all for 15 minutes", text, StringComparison.Ordinal);

        // AND WHAT THEY CANNOT BOUND.
        Assert.Contains("gap straight through the figure", text, StringComparison.Ordinal);
        Assert.Contains("spread it crosses", text, StringComparison.Ordinal);
        Assert.Contains("slippage", text, StringComparison.Ordinal);
        Assert.Contains("fees your platform has not reported", text, StringComparison.Ordinal);
        Assert.Contains("can be larger than the budget", text, StringComparison.Ordinal);

        // AND THE SAMPLING DELAY, MEASURED ON THE MACHINE THIS IS RUNNING ON.
        Assert.NotNull(gw.LastLossWatchTook);
        Assert.Contains("last reading of your open book took", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND IT IS PRINTED ON AN INSTALLATION WITH NO BUDGET TOO — the other side of the mutant.
    ///
    /// <para>Printing it here as well is what makes the sentence a statement about the product rather
    /// than a footnote attached to one configuration. An owner about to set their first budget reads
    /// this report before they set it.</para>
    /// </summary>
    [Fact]
    public async Task The_disclosure_is_printed_whether_a_budget_is_set_or_not()
    {
        var (gw, _, db, clock) = await Ready(dailyBudget: 0m);
        using var _1 = db;
        await using var _2 = gw;

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        Assert.Contains("no daily loss budget is set", text, StringComparison.Ordinal);
        Assert.Contains("none of them is a maximum loss", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ZERO EXIT BOUND IS DISCLOSED AS THE WIDEST VALUE IT HAS, not omitted.
    ///
    /// <para>Switching the data-loss exit off is a decision with a consequence — a position nobody
    /// can value stays open and unbounded — and the report that lists the thresholds is the document
    /// that has to say which of them is not in force.</para>
    /// </summary>
    [Fact]
    public async Task A_switched_off_data_loss_exit_is_disclosed_rather_than_left_out()
    {
        var (gw, _, db, clock) = await Ready(exitAfterMinutes: 0m);
        using var _1 = db;
        await using var _2 = gw;

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        Assert.Contains("will NOT close a position it cannot value at all", text, StringComparison.Ordinal);
        Assert.DoesNotContain("unable to value at all for", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A POSITION NOBODY CAN VALUE IS ON THE STATUS AND IN SECTION FOUR, WITH ITS CLOCK.
    ///
    /// <para>An agent that reads <c>RISK_CHECK_UNAVAILABLE</c> has been told that its order was
    /// refused, not that a POSITION has been unmeasurable for minutes and is on its way to being
    /// closed by the app. The owner reading a report is in the same position: the money section's
    /// figure is silently missing whatever nobody could value.</para>
    /// </summary>
    [Fact]
    public async Task The_status_and_the_report_name_the_position_nobody_can_value_and_since_when()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        conn.Faults.QuoteAge = Silent;
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(Tick);
            await gw.LossWatchAsync();
        }

        var json = Json.Write(await gw.StatusAsync());
        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        log.WriteLine(json);
        log.WriteLine(text.Split('\n').First(l => l.Contains("cannot value", StringComparison.Ordinal)));

        Assert.Contains("loss_valuation_lost", json, StringComparison.Ordinal);
        Assert.Contains("cannot work out what your open 2 ES is worth", json, StringComparison.Ordinal);
        Assert.Contains("positions TradeAgent cannot value", text, StringComparison.Ordinal);
        Assert.Contains("It has now been 40 seconds", text, StringComparison.Ordinal);

        // AND IT IS NOT A CLOSURE. Nothing here may read as a budget having been reached.
        Assert.DoesNotContain("loss_day_closed_at", json, StringComparison.Ordinal);
        Assert.Contains("no — the day was open to new positions", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// AND AN EXIT IS ITS OWN LINE, NEVER FOLDED INTO "positions closed for you".
    ///
    /// <para>That line is about a budget that was REACHED. A reader who found a data-loss exit under
    /// it would be told their money went when their prices did — the mutant item 2's fixture watches,
    /// arriving at the surface where the owner would actually read it.</para>
    /// </summary>
    [Fact]
    public async Task An_exit_is_reported_under_its_own_line_and_says_no_budget_was_reached()
    {
        var (gw, conn, db, clock) = await Ready(exitAfterMinutes: 1m);
        using var _1 = db;
        await using var _2 = gw;

        await gw.PlaceAsync(new AgentContext("a"), "open-es", TestEnv.Buy("ES", 2m));
        clock.Advance(Tick);
        await gw.RefreshHealthAsync();

        conn.Faults.QuoteAge = Silent;
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(Tick);
            await gw.RefreshHealthAsync();
        }

        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow()));
        var line = text.Split('\n').First(l => l.Contains("closed because nothing could value it", StringComparison.Ordinal));
        log.WriteLine(line);

        Assert.Contains("closed because nothing could value it", text, StringComparison.Ordinal);
        Assert.Contains("VALUATION_LOST", text, StringComparison.Ordinal);
        Assert.Contains("Your loss budget was NOT reached", text, StringComparison.Ordinal);
        Assert.DoesNotContain("positions closed for you", text, StringComparison.Ordinal);
        Assert.Contains("no — the day was open to new positions", text, StringComparison.Ordinal);
    }
}
