using System.Reflection;
using TradeAgent.App;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE READERS OF THE SECURITY BOARD'S POLICY (<c>U-board-a2</c>; R24 § 4.5): the gateway's limits, the meter's plan day-total and the
/// paper allocator's standing envelope come from the policy in force — decided by <c>app</c> at the founding until the board decides —
/// and from no owner-set box. A number an earlier build left in the settings row governs nothing; the owner's real-money cap and
/// perception's budget inside it stay his settings (AUTH-001); the founding envelope is the app's, on the app's own paper venue only.
/// </summary>
public class BoardReadersTests(ITestOutputHelper log)
{
    /// <summary>
    /// A LIMIT LEFT IN AN EARLIER SETTINGS ROW GOVERNS NOTHING: a row holding a largest order of 50 and a plan capacity of 400 —
    /// keys this build no longer has — reads (it is not "could not be read"), and the founding policy governs: an order of 2 is
    /// refused against the policy's 1, and the meter admits plan turns against the policy's 40. RED before item 8 and 9: the row's 50
    /// let the order past the quantity check, and its 400 was the plan's ceiling.
    /// </summary>
    [Fact]
    public async Task A_limit_left_in_an_earlier_settings_row_governs_nothing()
    {
        using var db = TestEnv.NewDb();
        var broker = new FakeBroker();
        db.SetKv("settings", $"{{\"selected_account_id\":\"{broker.AccountId}\",\"ai_plan_daily_capacity\":400,"
                             + "\"risk\":{\"max_order_quantity\":50,\"max_open_positions\":10,\"max_orders_per_minute\":100,"
                             + "\"instrument_allowlist\":[\"BTCUSDT\"]}}");
        var conn = new FakeConnector(broker);
        var host = AppHost.Composed(db, conn, new AgentPresence());
        try
        {
            await conn.ConnectAsync();
            await host.Gateway.RefreshHealthAsync();
            log.WriteLine($"row read: {!host.Gateway.Settings.CouldNotBeRead}; largest order in force: {host.Gateway.Policy.Risk.MaxOrderQuantity}");

            Assert.False(host.Gateway.Settings.CouldNotBeRead);
            var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                host.Gateway.PlaceAsync(new AgentContext("a"), "left-in-the-row", TestEnv.Buy("BTCUSDT", 2m)));
            log.WriteLine($"{denied.Code}: {denied.Message}");
            Assert.Equal(ErrorCode.RISK_LIMIT_EXCEEDED, denied.Code);
            Assert.Equal("quantity 2 exceeds the limit of 1", denied.Message);
            Assert.Empty(broker.Orders);

            var plan = host.Meter!.TodayIn(BillingClasses.Plan).Cap;
            log.WriteLine($"plan admission ceiling: {plan}");
            Assert.Equal(40m, plan);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>
    /// THE OWNER'S REAL-MONEY CAP IS NOT THE POLICY'S (AUTH-001; R24 § 4.3): the policy has no member that could set the paid-key cap
    /// or perception's budget inside it, the settings still hold both and the app reads both there — while every value that moved to
    /// the policy has left the settings, so no owner-set box can hold one. RED before items 8 and 9: the settings still held them.
    /// </summary>
    [Fact]
    public async Task The_owners_real_money_cap_is_not_the_policys()
    {
        var policy = typeof(BoardPolicy).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToList();
        log.WriteLine($"policy members: {string.Join(", ", policy)}");
        foreach (var word in new[] { "Cost", "Perception", "Price", "Money", "Key" })
            Assert.DoesNotContain(policy, name => name.Contains(word, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(typeof(TradeAgentSettings).GetProperty(nameof(TradeAgentSettings.AiDailyCostCap)));
        Assert.NotNull(typeof(TradeAgentSettings).GetProperty(nameof(TradeAgentSettings.PerceptionDailyBudget)));
        string[] moved =
        [
            "Risk", "AiPlanDailyCapacity", "RoleShare", "AiTurnAllowanceInputTokens", "AiTurnAllowanceOutputTokens", "AiLanes",
            "AiLanesPerPool", "AiLanesPerCellClass", "MissionReviewMinutes", "HeadCeilingHours", "RewakeFloorMinutes"
        ];
        var stillThere = moved.Where(name => typeof(TradeAgentSettings).GetProperty(name) is not null).ToList();
        log.WriteLine($"moved values still in the settings: {string.Join(", ", stillThere)}");
        Assert.Empty(stillThere);

        // AND THE APP READS BOTH OF HIS THERE: the meter's money ceiling moves with his setting, and perception's budget is his row's.
        var app = File.ReadAllText(Path.Combine(Repo(), "src", "TradeAgent.App", "AppHost.cs"));
        Assert.Contains("Gateway.Settings.AiDailyCostCap", app, StringComparison.Ordinal);
        Assert.Contains("Gateway.Settings.PerceptionDailyBudget", app, StringComparison.Ordinal);

        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            host.Gateway.Update(s => s.AiDailyCostCap = 7m);
            Assert.Equal(7m, host.Meter!.TodayIn(BillingClasses.Key).Cap);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
