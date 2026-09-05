using System.Runtime.CompilerServices;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;

namespace TradeAgent.Tests;

/// <summary>
/// Every test assembly redirects TRADEAGENT_HOME into a scratch directory before anything touches
/// <see cref="Paths"/>, so tests can never read or write the real installation.
/// </summary>
public static class TestEnv
{
    public static string Home { get; private set; } = "";

    [ModuleInitializer]
    public static void Init()
    {
        Home = Path.Combine(Path.GetTempPath(), "tradeagent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Home);
        Environment.SetEnvironmentVariable("TRADEAGENT_HOME", Home);
        Environment.SetEnvironmentVariable("TRADEAGENT_PIPE", "ta-test-" + Guid.NewGuid().ToString("n")[..12]);
    }

    public static Database NewDb() => new(Path.Combine(Home, $"db-{Guid.NewGuid():n}.db"));

    /// <summary>
    /// THE INSTRUMENTS THIS SUITE TRADES. An empty allowlist used to mean "everything" and now means
    /// nothing (REVIEW 2026-09-05 finding 5), so a gateway a test has configured to trade has to
    /// name what it may trade — exactly as a configured installation does. Spelled once so that a
    /// test that starts using a new symbol adds it here rather than quietly widening one setup.
    /// </summary>
    public static readonly string[] Instruments = ["ES", "NQ", "MES", "YM", "XYZ"];

    /// <summary>A gateway wired to a fresh simulator, already healthy and allowed to trade.</summary>
    public static async Task<(TradingGateway Gw, FakeConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null, GatewayOptions? options = null, FaultProfile? faults = null)
    {
        var db = NewDb();
        var conn = new FakeConnector(new FakeBroker(), faults);
        var gw = new TradingGateway(db, conn, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            // "ALLOWED TO TRADE" NOW HAS TO NAME WHAT IT MAY TRADE. An empty allowlist used to mean
            // "everything", which is the defect REVIEW 2026-09-05 finding 5 turned into a re-armed
            // AI; it now allows nothing, so a configured installation — which is what this helper
            // stands in for — lists its instruments.
            s.Risk.InstrumentAllowlist = [.. Instruments];
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    public static PlaceIntent Buy(string symbol = "ES", decimal qty = 1m) =>
        new(symbol, ConnectorSdk.OrderSide.Buy, ConnectorSdk.OrderType.Market, qty, null, null, ConnectorSdk.TimeInForce.Day, null);
}
