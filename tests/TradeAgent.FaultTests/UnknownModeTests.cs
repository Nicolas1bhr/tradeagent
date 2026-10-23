using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A MODE AN EARLIER BUILD SAVED IN THE SETTINGS ROW IS NEVER THE MODE IN FORCE (rewritten by <c>U-board-a1</c>; R24 § 4.5).
///
/// <para>The class was written for REVIEW 2026-09-05's Codex F3: <c>"mode": 999</c> in the settings row produced a mode of 999 that
/// executed, consulted no real-money switch and refused no real account. Since the security board the mode is not a setting at all —
/// it is the board's policy in force, whose store cannot hold a live mode — so the finding cannot recur where it was found. What a
/// pre-board row's <c>mode</c> key does now is held here: a value that is plainly not live (OBSERVE, PAPER) is ignored with no
/// refusal and the founding policy governs; a live value, or one this build cannot name — 999 included — refuses every order under
/// <c>HOME_ARMED_BEFORE_THE_BOARD</c>, the owner is told in words, and nothing reaches the broker.</para>
/// </summary>
public class UnknownModeTests(ITestOutputHelper log)
{
    /// <summary>The owner's row, exactly as the app writes it, with one value replaced by a number.</summary>
    const string SeedWithMode999 =
        """
        {"mode":999,"live_activated":false,"ai_trading_stopped":false,"selected_account_id":"REAL-001",
         "risk":{"max_order_quantity":10,"max_notional_per_order":0,"max_open_positions":10,
         "max_orders_per_minute":100,"instrument_allowlist":["ES"]}}
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
    /// THE FINDING, RUN AGAINST THE BOARD. Restart over a settings row with an undefined numeric mode, then submit a buy: nothing
    /// reaches the broker, and the refusal is the earlier-home one — this build will not guess what 999 meant.
    /// </summary>
    [Fact]
    public async Task An_undefined_numeric_mode_executes_nothing()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999);
        using var _1 = db;

        log.WriteLine($"mode in force         : {gw.Policy.Mode} (the board's policy, never the row's)");
        log.WriteLine($"earlier setting       : {gw.EarlierLiveSetting?.Mode}");
        log.WriteLine($"account               : {gw.Settings.SelectedAccountId} simulated={conn.Broker.IsSimulated}");

        var authorized = gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out var code);
        log.WriteLine($"TryAuthorizeExecution : {authorized} — {code} {why}");

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "um-1", TestEnv.Buy()));

        log.WriteLine($"buy                   : {denied.Code} — {denied.Message}");
        log.WriteLine($"orders at the broker  : {conn.Broker.Orders.Count}");

        Assert.Equal(TradingMode.PAPER, gw.Policy.Mode);
        Assert.False(authorized);
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, code);
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, denied.Code);
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(0, conn.Places);
    }

    /// <summary>
    /// AND THE OWNER IS TOLD, in the app's own words, on the screen they already read: the activity line names the mode the row
    /// held, both versions, and the press that sets it aside.
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_that_the_saved_mode_is_not_one_this_build_knows()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999);
        using var _1 = db;

        var said = new LogStore(db).RecentActivity(50).Select(a => a.Text).ToList();
        foreach (var line in said) log.WriteLine($"activity : {line}");

        Assert.Contains(said, m => m.Contains("mode", StringComparison.OrdinalIgnoreCase)
                                   && m.Contains("999", StringComparison.Ordinal)
                                   && m.Contains(Labels.SetAsideEarlierLiveSetting, StringComparison.Ordinal));
        Assert.DoesNotContain(said, m => m.Contains("Exception", StringComparison.Ordinal));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, so that "fails closed" does not quietly mean "refuses everything": an earlier row's PAPER is ignored and
    /// the founding policy trades on paper exactly as before; its LIVE_CONFIRM or LIVE_AUTONOMOUS with real money switched on is
    /// never honoured — it is refused as an earlier armed home and nothing reaches the broker.
    /// </summary>
    [Theory]
    [InlineData("PAPER")]
    [InlineData("LIVE_CONFIRM")]
    [InlineData("LIVE_AUTONOMOUS")]
    public async Task A_mode_an_earlier_build_saved_is_never_the_mode_in_force(string mode)
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        db.SetKv("settings", SeedWithMode999
            .Replace("999", $"\"{mode}\"")
            .Replace("\"live_activated\":false", mode == "PAPER" ? "\"live_activated\":false" : "\"live_activated\":true")
            .Replace("REAL-001", "SIM-001"));
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        Assert.Equal(TradingMode.PAPER, gw.Policy.Mode);
        if (mode == "PAPER")
        {
            Assert.Null(gw.EarlierLiveSetting);
            var placed = await gw.PlaceAsync(new AgentContext("a"), $"um-{mode}", TestEnv.Buy());
            log.WriteLine($"{mode} : {placed.State}, orders at the broker = {conn.Broker.Orders.Count}");
            Assert.Equal(ExecutionState.FILLED, placed.State);
            Assert.Single(conn.Broker.Orders);
        }
        else
        {
            Assert.Equal(mode, gw.EarlierLiveSetting?.Mode);
            var refused = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                gw.PlaceAsync(new AgentContext("a"), $"um-{mode}", TestEnv.Buy()));
            log.WriteLine($"{mode} : {refused.Code}, orders = {conn.Broker.Orders.Count}");
            Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, refused.Code);
            Assert.Empty(conn.Broker.Orders);
        }
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EARLIER ROW'S OBSERVE IS IGNORED TOO — it is not live, so it is no armed home and refuses nothing of its own; the founding
    /// policy governs, and on this real-money account Practice itself refuses the order (the paper-on-real guard), in its own words.
    /// </summary>
    [Fact]
    public async Task An_earlier_watch_only_mode_is_ignored_and_the_founding_policy_governs()
    {
        var (gw, conn, db) = await RestartOver(SeedWithMode999.Replace("999", "\"OBSERVE\""));
        using var _1 = db;

        Assert.Null(gw.EarlierLiveSetting);
        Assert.Equal(TradingMode.PAPER, gw.Policy.Mode);
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "um-observe", TestEnv.Buy()));
        log.WriteLine($"OBSERVE : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.MODE_ACCOUNT_MISMATCH, denied.Code);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }
}
