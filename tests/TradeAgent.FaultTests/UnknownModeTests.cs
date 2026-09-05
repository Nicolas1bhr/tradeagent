using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A MODE THIS BUILD DOES NOT KNOW IS NOT A MODE, AND IT MUST NOT TRADE (REVIEW 2026-09-05, Codex F3).
///
/// <c>TradingMode</c> is written to the settings row as a name, and <c>System.Text.Json</c>'s enum
/// converter reads NUMBERS as well as names — and casts a number it does not recognise straight onto
/// the enum without complaint. So a row saying <c>"mode": 999</c> produced a settings object whose
/// mode was 999, and every gate in <c>TryAuthorizeExecution</c> is written as a comparison against
/// the named modes:
///
///   ModeAllowsExecution  =  Mode != OBSERVE           -> 999 is allowed to execute
///   ModeIsLive           =  LIVE_CONFIRM or LIVE_AUTONOMOUS -> 999 is not live, so the live
///                                                       activation switch is never consulted
///   the PAPER guard      =  Mode == PAPER             -> 999 is not paper, so a real-money account
///                                                       is not refused either
///
/// which is a mode that trades real money with the safety switch off and nobody asked. It is not a
/// hypothetical row: a newer build writes a mode this one has never heard of, and a rollback reads
/// it. Neither is 999 special — anything outside the four named values takes the same path.
///
/// The fix is that the classification itself fails closed: a value that is not one of the named
/// modes allows nothing, and the owner is told in the app's own words rather than by an order that
/// quietly went out.
/// </summary>
public class UnknownModeTests(ITestOutputHelper log)
{
    /// <summary>The owner's row, exactly as the app writes it, with one value replaced by a number.</summary>
    const string SeedWithMode999 =
        """
        {"mode":999,"live_activated":false,"ai_trading_stopped":false,"selected_account_id":"REAL-001",
         "risk":{"max_order_quantity":10,"max_notional_per_order":0,"max_open_positions":10,
         "max_orders_per_minute":100,"instrument_allowlist":[]}}
        """;

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> RestartOver(string settingsRow)
    {
        var db = TestEnv.NewDb();
        db.SetKv("settings", settingsRow);

        // A REAL-MONEY account, chosen, which is the condition the finding names: the mode is the
        // only thing standing between the agent and it.
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = "REAL-001", IsSimulated = false }));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// THE FINDING, RUN. Restart over a settings row with an undefined numeric mode, then submit a
    /// buy. Nothing may reach the broker.
    /// </summary>
    [Fact]
    public async Task An_undefined_numeric_mode_executes_nothing()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999);
        using var _1 = db;

        log.WriteLine($"mode read back        : {(int)gw.Settings.Mode} ({gw.Settings.Mode})");
        log.WriteLine($"ModeAllowsExecution   : {gw.Settings.ModeAllowsExecution}");
        log.WriteLine($"ModeIsLive            : {gw.Settings.ModeIsLive}   (live_activated is false)");
        log.WriteLine($"account               : {gw.Settings.SelectedAccountId} simulated={conn.Broker.IsSimulated}");

        var authorized = gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out var code);
        log.WriteLine($"TryAuthorizeExecution : {authorized} — {code} {why}");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "um-1", TestEnv.Buy()));

        log.WriteLine($"buy                   : {denied.Code} — {denied.Message}");
        log.WriteLine($"orders at the broker  : {conn.Broker.Orders.Count}");

        Assert.False(authorized);
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, code);
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(0, conn.Places);
    }

    /// <summary>
    /// AND THE OWNER IS TOLD, in the app's own words, on the screen they already read. A refusal an
    /// agent receives over a pipe is not news the person at the keyboard ever sees; the whole failure
    /// here is a setting they believe they set.
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_that_the_saved_mode_is_not_one_this_build_knows()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999);
        using var _1 = db;

        var said = new LogStore(db).RecentActivity(50).Select(a => a.Text).ToList();
        foreach (var line in said) log.WriteLine($"activity : {line}");

        Assert.Contains(said, m => m.Contains("mode", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("999", StringComparison.Ordinal));
        Assert.DoesNotContain(said, m => m.Contains("Exception", StringComparison.Ordinal));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, so that "fails closed" does not quietly mean "refuses everything". Every
    /// mode the app actually offers still parses and still trades exactly as it did.
    /// </summary>
    [Theory]
    [InlineData("PAPER")]
    [InlineData("LIVE_CONFIRM")]
    [InlineData("LIVE_AUTONOMOUS")]
    public async Task A_mode_this_build_does_know_is_read_and_honoured(string mode)
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        db.SetKv("settings", SeedWithMode999
            .Replace("999", $"\"{mode}\"")
            .Replace("\"live_activated\":false", "\"live_activated\":true")
            .Replace("REAL-001", "SIM-001"));
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        Assert.Equal(mode, gw.Settings.Mode.ToString());
        Assert.True(gw.Settings.ModeAllowsExecution);
        Assert.True(gw.TryAuthorizeExecution(new AgentContext("a"), out var why), why);

        // LIVE_CONFIRM parks and says so by refusing; the other two send. Both are "the mode was
        // honoured", which is the whole point of this direction.
        if (mode == "LIVE_CONFIRM")
        {
            var parked = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                gw.PlaceAsync(new AgentContext("a"), $"um-{mode}", TestEnv.Buy()));
            log.WriteLine($"{mode} : {parked.Code}, record = {gw.GetRequest($"um-{mode}")!.State}, orders = {conn.Broker.Orders.Count}");
            Assert.Equal(ErrorCode.APPROVAL_REQUIRED, parked.Code);
            Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest($"um-{mode}")!.State);
            Assert.Empty(conn.Broker.Orders);
        }
        else
        {
            var placed = await gw.PlaceAsync(new AgentContext("a"), $"um-{mode}", TestEnv.Buy());
            log.WriteLine($"{mode} : {placed.State}, orders at the broker = {conn.Broker.Orders.Count}");
            Assert.Equal(ExecutionState.FILLED, placed.State);
            Assert.Single(conn.Broker.Orders);
        }
        await gw.DisposeAsync();
    }

    /// <summary>
    /// OBSERVE is the mode that already refused, and it has to keep refusing with its own words —
    /// otherwise the test above proves only that SOMETHING refuses.
    /// </summary>
    [Fact]
    public async Task Observe_still_refuses_with_the_mode_it_actually_is()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999.Replace("999", "\"OBSERVE\""));
        using var _1 = db;

        Assert.Equal(TradingMode.OBSERVE, gw.Settings.Mode);
        Assert.False(gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out var code));
        log.WriteLine($"OBSERVE : {code} — {why}");
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, code);
        Assert.Contains("OBSERVE", why);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }
}
