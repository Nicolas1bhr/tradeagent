using TradeAgent.ConnectorSdk;
using TradeAgent.Core;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HELD SCOPE NEVER READS AS "EARLIEST …", AND A RELEASE IS ITS OWN NEWS (<c>U-reopen-2</c>, item 5).
///
/// <para>Every other thing that holds a closure is fixed by waiting: a flatten finishes, an
/// unreconciled order is answered, a clock is put right. A review hold is not. It was written when
/// the second breach was confirmed and the only thing that lifts it is a person pressing something
/// on a screen the AI cannot reach — so a surface that showed an instant beside it would be telling
/// an owner and an agent to wait for a time nothing is going to honour, which is the exact failure
/// the "earliest" wording was introduced to avoid.</para>
///
/// <para>And both durations are the owner's now, which means the rule an episode is judged under is
/// no longer a constant anyone can assume: the report and the status state the SNAPSHOT, because an
/// agent doing the arithmetic off the live settings would get a different answer from the gateway.</para>
/// </summary>
public class LossHoldSurfacesTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void MoveTo(DateTimeOffset to) => _now = to;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(20);
    static readonly TimeSpan Day = TimeSpan.FromHours(24);
    const string Note = "Read the journal: the ES strategy is being run over by the open";

    static async Task<(TradingGateway Gw, TradeAgent.Connectors.Fake.FakeConnector Conn,
        TradeAgent.Core.Db.Database Db, TestClock Clock)> Ready()
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await TestEnv.Ready(
            s => s.Risk.MaxDailyLoss = 1_000m, new GatewayOptions { Clock = clock });
        return (gw, conn, db, clock);
    }

    static async Task Breach(TradingGateway gw, TradeAgent.Connectors.Fake.FakeConnector conn,
        TestClock clock, string tag, decimal budget, ITestOutputHelper log)
    {
        gw.Update(s => s.Risk.MaxDailyLoss = budget);
        await gw.PlaceAsync(new AgentContext("a"), tag, TestEnv.Buy("ES"));
        conn.Broker.PriceOffset = -20m;
        clock.Advance(Tick);
        await gw.LossWatchAsync();
        clock.Advance(Tick);
        var closing = await gw.LossWatchAsync();
        log.WriteLine($"{tag,-12} closed : [{string.Join(", ", closing.Closed)}]");
        Assert.NotEmpty(closing.Closed);
        conn.Broker.PriceOffset = 0m;
        gw.Update(s => s.Risk.MaxDailyLoss = 100_000m);
    }

    /// <summary>Two episodes, the second of them held. Returns the second breach record.</summary>
    static async Task<LossBreachRecord> Held(TradingGateway gw, TradeAgent.Connectors.Fake.FakeConnector conn,
        TestClock clock, ITestOutputHelper log)
    {
        var account = conn.Broker.AccountId;
        await Breach(gw, conn, clock, "episode-one", 1_000m, log);
        var first = gw.DayClosed(account)!;
        clock.MoveTo(LossReopen.EligibleAt(first.ConfirmedAt, Day) + TimeSpan.FromMinutes(1));
        Assert.Single((await gw.LossWatchAsync()).Reopened);

        clock.MoveTo(new DateTimeOffset(2026, 3, 13, 9, 0, 0, TimeSpan.Zero));
        await Breach(gw, conn, clock, "episode-two", 2_000m, log);
        return gw.DayClosed(account)!;
    }

    /// <summary>
    /// A HELD SCOPE SAYS SO, ON THE STATUS, IN THE SITUATION AND IN THE REPORT — and shows no instant.
    /// </summary>
    [Fact]
    public async Task A_held_scope_says_held_for_review_everywhere_and_names_no_instant()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        var second = await Held(gw, conn, clock, log);

        var status = await gw.StatusAsync();
        var json = Json.Write(status);
        log.WriteLine($"loss_held_for_review  : {status.LossHeldForReview}");
        log.WriteLine($"loss_closure_rule     : {status.LossClosureRule}");
        Assert.Null(status.LossReopensAt);
        Assert.NotNull(status.LossHeldForReview);
        Assert.Null(status.LossReleasedAt);
        Assert.Equal(status.LossHeldForReview, status.LossReopenHeld);
        Assert.Contains("loss_held_for_review", json, StringComparison.Ordinal);
        Assert.Contains("loss_closure_rule", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_released_at", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loss_reopens_at", json, StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"line                  : {line}");
        Assert.Contains("held for review since", line!, StringComparison.Ordinal);
        Assert.DoesNotContain("at the earliest", line!, StringComparison.Ordinal);

        var section = Section(gw, clock);
        log.WriteLine(section);
        Assert.Contains("held for review:", section, StringComparison.Ordinal);
        Assert.Contains("closure rule:", section, StringComparison.Ordinal);
        Assert.DoesNotContain("reopens: by code, at the earliest", section, StringComparison.Ordinal);

        // SECTION 4 LISTS THE EPISODES, WITH THE RULE AND THE OUTCOME OF EACH.
        Assert.Contains("loss closures in the window:", section, StringComparison.Ordinal);
        Assert.Contains("reopened at 2026-03-11 12:01 UTC by code", section, StringComparison.Ordinal);
        Assert.Contains("HELD for you to look at", section, StringComparison.Ordinal);
        Assert.Contains("closed for at least 24 hours", section, StringComparison.Ordinal);
        Assert.Equal(LossBreach.KeyFor(second), LossBreach.KeyFor(gw.DayClosed(conn.Broker.AccountId)!));
    }

    /// <summary>
    /// A RELEASE IS TOLD, WITH THE OWNER'S NOTE QUOTED, and the hold stops being what is in the way.
    /// The note is the durable trace of a person overruling the software's own refusal, so it is
    /// shown rather than summarised — and the report names who released it and when.
    /// </summary>
    [Fact]
    public async Task A_release_is_told_with_the_owners_note_quoted()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;
        await Held(gw, conn, clock, log);

        Assert.True(gw.ReleaseHold(Note).Ok);

        var status = await gw.StatusAsync();
        log.WriteLine($"loss_released_at      : {status.LossReleasedAt?.ToString("O") ?? "absent"}");
        Assert.Null(status.LossHeldForReview);
        Assert.NotNull(status.LossReleasedAt);
        Assert.Contains("loss_released_at", Json.Write(status), StringComparison.Ordinal);

        var line = (await gw.LossTodayAsync()).Line();
        log.WriteLine($"line                  : {line}");
        Assert.Contains(Note, line!, StringComparison.Ordinal);
        Assert.Contains("You released the review hold", line!, StringComparison.Ordinal);

        var section = Section(gw, clock);
        log.WriteLine(section);
        Assert.Contains($"It was released by you at", section, StringComparison.Ordinal);
        Assert.Contains(Note, section, StringComparison.Ordinal);
        Assert.DoesNotContain("held for review:", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RULE ON THE STATUS AND IN THE REPORT IS THE EPISODE'S SNAPSHOT, not the live setting. An
    /// owner who shortens the closure after a breach, and an agent doing the arithmetic off the
    /// settings it can read, would both otherwise get a number the gateway is not going to honour.
    /// </summary>
    [Fact]
    public async Task The_rule_shown_is_the_episodes_snapshot_and_not_the_live_setting()
    {
        var (gw, conn, db, clock) = await Ready();
        using var _1 = db;
        await using var _2 = gw;

        await Breach(gw, conn, clock, "episode", 1_000m, log);
        gw.Update(s => { s.Risk.LossMinClosureHours = 1m; s.Risk.LossStrikeWindowDays = 2; });

        var status = await gw.StatusAsync();
        log.WriteLine($"live setting          : {gw.Settings.Risk.LossMinClosureHours} h / "
                      + $"{gw.Settings.Risk.LossStrikeWindowDays} days");
        log.WriteLine($"loss_closure_rule     : {status.LossClosureRule}");
        Assert.Equal("closed for at least 24 hours; a second breach inside a week is held for you to look at",
            status.LossClosureRule);
        Assert.Equal(Noon.AddSeconds(40) + Day, status.LossReopensAt);

        var section = Section(gw, clock);
        log.WriteLine(section);
        Assert.Contains("closure rule: closed for at least 24 hours", section, StringComparison.Ordinal);
    }

    static string Section(TradingGateway gw, TestClock clock)
    {
        var text = DailyReportText.Render(gw.Reports.Compose(clock.GetUtcNow().AddMinutes(1)));
        var s = text[text.IndexOf("4. Capital and performance", StringComparison.Ordinal)..];
        return s[..s.IndexOf("5. Execution health", StringComparison.Ordinal)];
    }
}
