using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE FOUNDING ALLOWLIST READS THE LIST THE VENUE PUBLISHES NOW, AND ASKS FOR IT BOUNDEDLY (PREFLIGHT-2 blocker 1). A symbol missing from the list the
/// gateway holds asks the venue once before it is refused; still missing, it stays refused; a list that could not be read refuses in its own words and
/// allows nothing; and a refused symbol does not reach the venue on every order. <c>PaperPairsAfterSetupTests</c> walks the path a real install takes.
/// </summary>
public class InstrumentListRefreshTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
    }

    static InstrumentInfo Listed(string symbol) => new(symbol, symbol, "fake", 0.25m, 12.5m, 50m);

    sealed record Rig(TradingGateway Gw, RecordingConnector Conn, Database Db, TestClock Clock) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            Db.Dispose();
        }
    }

    /// <summary>The founding policy, on the app's own paper venue (or a venue reporting <paramref name="id"/>), with <paramref name="listed"/> the list it holds.</summary>
    static async Task<Rig> Founded(string? id, params string[] listed)
    {
        var db = TestEnv.NewDb();
        var clock = new TestClock(Start);
        var fake = new FakeConnector(new FakeBroker()) { QuoteClock = clock };
        var conn = new RecordingConnector(fake, id) { InstrumentsAnswer = [.. listed.Select(Listed)] };
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = fake.Broker.AccountId);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        await gw.InstrumentsAsync();
        return new Rig(gw, conn, db, clock);
    }

    int _orders;

    async Task<string> Order(Rig rig, string symbol)
    {
        try
        {
            var placed = await rig.Gw.PlaceAsync(new AgentContext("a"), $"refresh-{++_orders}", TestEnv.Buy(symbol));
            log.WriteLine($"{symbol}: {placed.State}");
            return placed.State.ToString();
        }
        catch (GatewayDeniedException ex)
        {
            log.WriteLine($"{symbol}: {ex.Code}: {ex.Message}");
            return $"{ex.Code}: {ex.Message}";
        }
    }

    /// <summary>
    /// A SYMBOL THE VENUE LISTED AFTER THE GATEWAY'S FIRST LOOK IS ASKED FOR ONCE AND TRADES. RED at the base:
    /// <c>RISK_LIMIT_EXCEEDED: NQ is not on the allowed instrument list</c>, with the venue never asked.
    /// </summary>
    [Fact]
    public async Task A_symbol_the_venue_listed_since_the_first_look_costs_one_read_and_trades()
    {
        await using var rig = await Founded(null, "ES");
        rig.Conn.InstrumentsAnswer = [Listed("ES"), Listed("NQ")];
        var before = rig.Conn.InstrumentReads;

        var answer = await Order(rig, "NQ");

        Assert.DoesNotContain("not on the allowed instrument list", answer, StringComparison.Ordinal);
        Assert.Equal(1, rig.Conn.Places);
        Assert.Equal(before + 1, rig.Conn.InstrumentReads);

        // AND ONCE IT IS LISTED NOTHING ASKS AGAIN.
        await Order(rig, "NQ");
        Assert.Equal(before + 1, rig.Conn.InstrumentReads);
    }

    /// <summary>
    /// STILL MISSING AFTER THE ASK, IT STAYS REFUSED, AND THE ASK IS BOUNDED: a minute's orders, for one symbol or several, ask the venue once.
    /// </summary>
    [Fact]
    public async Task A_symbol_still_missing_stays_refused_and_does_not_ask_the_venue_on_every_order()
    {
        await using var rig = await Founded(null, "ES");
        var before = rig.Conn.InstrumentReads;

        foreach (var symbol in new[] { "XYZ", "XYZ", "XYZ", "ABC", "DEF" })
            Assert.Equal($"RISK_LIMIT_EXCEEDED: {symbol} is not on the allowed instrument list", await Order(rig, symbol));

        Assert.Equal(before + 1, rig.Conn.InstrumentReads);
        Assert.Equal(0, rig.Conn.Places);

        rig.Clock.At += TradingGateway.InstrumentListAskedAtMost;
        Assert.Equal("RISK_LIMIT_EXCEEDED: XYZ is not on the allowed instrument list", await Order(rig, "XYZ"));
        Assert.Equal(before + 2, rig.Conn.InstrumentReads);
    }

    /// <summary>
    /// A LIST THAT COULD NOT BE READ REFUSES, SAYS SO, AND ALLOWS NOTHING NEW — without asking on every order, and without taking away what the list it holds
    /// already allows. The next ask after the bound reads it, and the symbol trades.
    /// </summary>
    [Fact]
    public async Task A_read_that_failed_refuses_in_its_own_words_and_allows_nothing()
    {
        await using var rig = await Founded(null, "ES");
        rig.Conn.InstrumentsThrow = new IOException("the venue did not answer");
        var before = rig.Conn.InstrumentReads;

        for (var i = 0; i < 3; i++)
        {
            var answer = await Order(rig, "NQ");
            Assert.StartsWith($"{ErrorCode.RISK_CHECK_UNAVAILABLE}: ", answer, StringComparison.Ordinal);
            Assert.Contains("the venue did not answer", answer, StringComparison.Ordinal);
            Assert.Contains("NQ", answer, StringComparison.Ordinal);
        }

        Assert.Equal(before + 1, rig.Conn.InstrumentReads);
        Assert.Equal(0, rig.Conn.Places);

        Assert.DoesNotContain("instrument", await Order(rig, "ES"), StringComparison.Ordinal);
        Assert.Equal(1, rig.Conn.Places);

        rig.Conn.InstrumentsThrow = null;
        rig.Conn.InstrumentsAnswer = [Listed("ES"), Listed("NQ")];
        rig.Clock.At += TradingGateway.InstrumentListAskedAtMost;
        Assert.DoesNotContain("instrument", await Order(rig, "NQ"), StringComparison.Ordinal);
        Assert.Equal(2, rig.Conn.Places);
    }

    /// <summary>
    /// ONLY WHERE THE FOUNDING ALLOWLIST'S REACH APPLIES. Off the app's own paper venues the founding list is BTCUSDT alone, and a later policy's list is
    /// that list alone, so neither has a venue's list to ask for: the refusal costs no read, and what the venue lists allows nothing.
    /// </summary>
    [Theory]
    [InlineData("atas", false)]
    [InlineData(null, true)]
    public async Task A_list_that_is_the_policys_own_never_asks_the_venue(string? venue, bool laterPolicy)
    {
        await using var rig = await Founded(venue, "BTCUSDT");
        if (laterPolicy) rig.Gw.UpdateRiskUnderTest(r => r.InstrumentAllowlist = ["ES"]);
        rig.Conn.InstrumentsAnswer = [Listed("ES"), Listed("NQ")];
        var before = rig.Conn.InstrumentReads;

        Assert.Equal("RISK_LIMIT_EXCEEDED: NQ is not on the allowed instrument list", await Order(rig, "NQ"));

        Assert.Equal(before, rig.Conn.InstrumentReads);
        Assert.Equal(0, rig.Conn.Places);
    }
}
