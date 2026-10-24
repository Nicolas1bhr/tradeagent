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

    /// <summary>
    /// THE FOUNDING ENVELOPE IS THE APP'S AND ONLY ON THE PAPER VENUE (item 10): on <c>paper</c> / <c>PAPER-1</c>, one standing
    /// envelope with the founding terms — BTCUSDT, at most 0.05 and 5,000 of notional, one deployment, no end date — whose reason
    /// and Activity line name the founding policy and never the owner; written once, and not again after it is withdrawn. On the
    /// simulator, on another venue holding an account of the same name, and on a "paper" venue whose account is not provably a
    /// simulation, none. RED before item 10: no envelope was granted by anyone but the owner.
    /// </summary>
    [Fact]
    public async Task The_founding_envelope_is_the_apps_and_only_on_the_paper_venue()
    {
        var terms = BoardFounding.V1.PaperEnvelope!;

        // ON THE APP'S OWN PAPER VENUE AND ACCOUNT: one, with the founding terms, the app's.
        var (gw, db) = await Gateway(BoardFounding.PaperVenue, BoardFounding.PaperAccount, simulated: true);
        await using (gw)
        using (db)
        {
            var standing = gw.Envelopes.All().Where(e => e.StandsAt(DateTimeOffset.UtcNow)).ToList();
            foreach (var e in standing) log.WriteLine($"standing: {e.ConnectorId}/{e.AccountId} {e.Symbol} {e.MaxQuantity} {e.MaxNotional} ×{e.MaxDeployments} until {e.ExpiresAt:O} — {e.Reason}");
            var envelope = Assert.Single(standing);
            Assert.Equal((terms.Connector, terms.Account, terms.Instrument, terms.MaxQuantity, (decimal?)terms.MaxNotional, terms.Deployments),
                (envelope.ConnectorId, envelope.AccountId, envelope.Symbol, envelope.MaxQuantity, envelope.MaxNotional, envelope.MaxDeployments));
            Assert.Equal(DateTimeOffset.MaxValue.UtcDateTime, envelope.ExpiresAt.UtcDateTime);
            Assert.Equal("granted by the app under the security board's founding policy, version 1", envelope.Reason);
            Assert.DoesNotContain("owner", envelope.Reason, StringComparison.OrdinalIgnoreCase);
            var said = Assert.Single(new LogStore(db).RecentActivity(200).Select(a => a.Text), t => t.Contains("founding policy", StringComparison.Ordinal));
            log.WriteLine($"activity: {said}");
            Assert.DoesNotContain("You ", said, StringComparison.Ordinal);
            Assert.DoesNotContain("you granted", said, StringComparison.OrdinalIgnoreCase);

            // ONCE: the next pass grants nothing more, and the owner's withdrawal holds — the next pass does not undo it.
            await gw.RefreshHealthAsync();
            Assert.Single(gw.Envelopes.All());
            Assert.True(gw.WithdrawPaperEnvelope(envelope.Id).Ok);
            await gw.RefreshHealthAsync();
            await gw.RefreshHealthAsync();
            Assert.Single(gw.Envelopes.All());
            Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(DateTimeOffset.UtcNow));
        }

        // ON THE SIMULATOR, ON ANOTHER VENUE WITH AN ACCOUNT OF THE SAME NAME, AND ON A PAPER VENUE THAT CANNOT PROVE ITS ACCOUNT: none.
        foreach (var (venue, account, simulated) in new[]
                 {
                     (FakeConnector.ConnectorId, "SIM-001", true),
                     ("atas", BoardFounding.PaperAccount, true),
                     (BoardFounding.PaperVenue, BoardFounding.PaperAccount, false)
                 })
        {
            var (other, otherDb) = await Gateway(venue, account, simulated);
            await using (other)
            using (otherDb)
            {
                log.WriteLine($"{venue}/{account} (simulated {simulated}): {other.Envelopes.All().Count} envelope(s)");
                Assert.Empty(other.Envelopes.All());
            }
        }
    }

    /// <summary>
    /// TWO HEALTH PASSES AT ONCE GRANT THE FOUNDING ENVELOPE ONCE (fix pass 1 of <c>U-board-a2</c>, the review's P2 on item 10): two
    /// passes on the paper venue, held together just after the read that found no envelope, on a clock that moves between them —
    /// so two grants would hash to two ids — leave exactly one envelope, and the owner's one withdrawal leaves none standing. RED
    /// before the fix: the read and the write were two transactions, both passes found none, and two envelopes were granted.
    /// </summary>
    [Fact]
    public async Task Two_health_passes_at_once_grant_the_founding_envelope_once()
    {
        using var db = TestEnv.NewDb();
        var clock = new Ticking(DateTimeOffset.UtcNow);
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = BoardFounding.PaperAccount, IsSimulated = true }),
            BoardFounding.PaperVenue);
        await using var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = BoardFounding.PaperAccount);
        await conn.ConnectAsync();
        var account = await conn.GetAccountAsync(BoardFounding.PaperAccount);
        Assert.NotNull(account);
        Assert.Empty(gw.Envelopes.All());

        // HELD TOGETHER AFTER THE EMPTY READ: each pass that found none waits there for the other, up to two seconds.
        using var both = new Barrier(2);
        var metThere = 0;
        gw.FoundingEnvelopeFoundNone = () => { if (both.SignalAndWait(TimeSpan.FromSeconds(2))) Interlocked.Increment(ref metThere); };
        await Task.WhenAll(Task.Run(() => gw.GrantPolicyEnvelopeIfDue(account!)), Task.Run(() => gw.GrantPolicyEnvelopeIfDue(account!)));

        var all = gw.Envelopes.All();
        foreach (var e in all) log.WriteLine($"envelope {e.Id[..12]} granted {e.GrantedAt:O} — {e.Reason}");
        log.WriteLine($"passes that met after the empty read: {metThere}");
        var envelope = Assert.Single(all);
        Assert.True(gw.WithdrawPaperEnvelope(envelope.Id).Ok);
        Assert.DoesNotContain(gw.Envelopes.All(), e => e.StandsAt(clock.GetUtcNow()));
    }

    /// <summary>
    /// THE FOUNDING ALLOWLIST COVERS THE APP'S OWN PAPER VENUES' INSTRUMENTS (the orchestrator's ruling, fix pass 1 of <c>U-board-a2</c>):
    /// the practice simulator lists ES, NQ, MES and YM and not BTCUSDT, so its health pass quotes an instrument it lists, and an
    /// order there in ES reaches the per-order limits instead of the allowlist; an instrument it does not list is still refused,
    /// and on any other venue — here one reporting <c>atas</c>, paper or not — the allowlist stays BTCUSDT alone. RED before the
    /// ruling: the simulator's health pass quoted BTCUSDT, which it does not list, and ES was "not on the allowed instrument list".
    /// </summary>
    [Fact]
    public async Task The_founding_allowlist_covers_the_apps_own_paper_venues_instruments_and_nothing_else()
    {
        string AllowlistRefusal(string symbol) => $"{symbol} is not on the allowed instrument list";
        Assert.Equal([FakeConnector.ConnectorId, BoardFounding.PaperVenue], BoardFounding.AppPaperVenues);

        // THE PRACTICE SIMULATOR: its health pass quotes what it lists, and its listed instruments reach the per-order limits.
        using (var db = TestEnv.NewDb())
        {
            var broker = new FakeBroker();
            var conn = new FakeConnector(broker);
            var quoted = new List<string>();
            conn.QuoteChanged += q => { lock (quoted) quoted.Add(q.Symbol); };
            var host = AppHost.Composed(db, conn, new AgentPresence());
            try
            {
                host.Gateway.Update(s => s.SelectedAccountId = broker.AccountId);
                await conn.ConnectAsync();
                await host.Gateway.RefreshHealthAsync();
                log.WriteLine($"simulator health pass quoted: {string.Join(", ", quoted)}; market data {host.Gateway.Health.Get(Components.MarketData).State}");
                Assert.NotEmpty(quoted);
                Assert.All(quoted, s => Assert.Contains(FakeBroker.Instruments, i => i.Symbol == s));

                var es = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                    host.Gateway.PlaceAsync(new AgentContext("a"), "es-on-the-simulator", TestEnv.Buy("ES", 2m)));
                log.WriteLine($"ES ×2 on the simulator: {es.Code}: {es.Message}");
                Assert.Equal("quantity 2 exceeds the limit of 1", es.Message);

                var xyz = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                    host.Gateway.PlaceAsync(new AgentContext("a"), "xyz-on-the-simulator", TestEnv.Buy("XYZ", 2m)));
                log.WriteLine($"XYZ ×2 on the simulator: {xyz.Code}: {xyz.Message}");
                Assert.Equal(AllowlistRefusal("XYZ"), xyz.Message);
                Assert.Empty(broker.Orders);
            }
            finally { await host.Gateway.DisposeAsync(); }
        }

        // ANY OTHER VENUE: BTCUSDT alone, even where the platform lists ES and calls itself a practice platform.
        using (var db = TestEnv.NewDb())
        {
            var broker = new FakeBroker();
            var conn = new RecordingConnector(new FakeConnector(broker), "atas");
            var host = AppHost.Composed(db, conn, new AgentPresence());
            try
            {
                host.Gateway.Update(s => s.SelectedAccountId = broker.AccountId);
                await conn.ConnectAsync();
                await host.Gateway.RefreshHealthAsync();
                var es = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
                    host.Gateway.PlaceAsync(new AgentContext("a"), "es-elsewhere", TestEnv.Buy("ES", 2m)));
                log.WriteLine($"ES ×2 on a venue reporting atas: {es.Code}: {es.Message}");
                Assert.Equal(AllowlistRefusal("ES"), es.Message);
                Assert.Empty(broker.Orders);
            }
            finally { await host.Gateway.DisposeAsync(); }
        }
    }

    /// <summary>A clock that moves a millisecond every time it is read, so two grants never share an instant.</summary>
    sealed class Ticking(DateTimeOffset start) : TimeProvider
    {
        long _ticks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    /// <summary>A gateway on a connector reporting <paramref name="venue"/> as its id, over one account, after one health pass.</summary>
    static async Task<(TradingGateway Gw, Database Db)> Gateway(string venue, string account, bool simulated)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker { AccountId = account, IsSimulated = simulated }), venue);
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s => s.SelectedAccountId = account);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, db);
    }

    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
