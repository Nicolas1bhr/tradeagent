using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// A SETTINGS ROW THIS BUILD CANNOT READ IS NOT AN EMPTY SETTINGS ROW (REVIEW 2026-09-05, finding 5).
///
/// <c>TradingGateway.LoadSettings</c> caught every deserialization failure and returned
/// <c>new TradeAgentSettings()</c>. Those defaults are not neutral — they are the permissions of a
/// fresh install:
///
///   AiTradingStopped = false          -> the kill switch the owner pressed comes back up
///   LiveActivated    = false          -> the one field that happened to fail safe
///   InstrumentAllowlist = []          -> which InstrumentAllowed read as "everything is allowed"
///   MaxOrderQuantity = 1, MaxOpenPositions = 2, MaxOrdersPerMinute = 6
///
/// So the single event that proves the software cannot read what the owner asked for was also the
/// event that granted the AI more authority than the owner ever gave it, with no log line, no health
/// change and nothing on any screen. The row is not hypothetical: a newer build writes a field this
/// one cannot parse and a rollback reads it; so does a truncated write, a type change, or a bad enum
/// name. They all take the same catch.
///
/// The rule this file holds down: <b>unreadable settings are the most restrictive settings.</b>
/// Nothing is allowed, the AI is stopped, real money is off, and the owner is told in their words.
/// </summary>
public class UnreadableSettingsTests(ITestOutputHelper log)
{
    /// <summary>
    /// The owner's real configuration, written by this build, then damaged the way a rollback
    /// damages it: one enum value this build has never heard of. Nothing about the value is special
    /// — truncation and a type change take the same path — and the test below proves that.
    /// </summary>
    static (Database Db, string Row) OwnersRowMadeUnreadable(string damaged)
    {
        var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker());
        var configured = new TradingGateway(db, conn, new HealthRegistry());
        configured.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.AiTradingStopped = true;
            s.Risk.InstrumentAllowlist = ["MES"];
            s.Risk.MaxOrderQuantity = 1m;
        });
        var saved = db.GetKv("settings")!;
        Assert.Contains("\"ai_trading_stopped\":true", saved);
        configured.DisposeAsync().AsTask().GetAwaiter().GetResult();

        db.SetKv("settings", damaged == "" ? saved : saved.Replace("\"PAPER\"", damaged));
        return (db, saved);
    }

    static async Task<(TradingGateway Gw, RecordingConnector Conn)> RestartOver(Database db)
    {
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn);
    }

    // ── item 1 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FINDING, INVERTED. P1 asserted <c>AiTradingStopped == false</c>, an empty allowlist and
    /// <c>InstrumentAllowed("ES") == true</c>. Every one of those is now the opposite.
    /// </summary>
    [Fact]
    public async Task A_settings_row_that_cannot_be_read_stops_the_AI_and_allows_nothing()
    {
        var (db, _) = OwnersRowMadeUnreadable("\"LIVE_LOCKED\"");
        using var _1 = db;
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"settings row on disk : {db.GetKv("settings")}");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"LiveActivated        : {gw.Settings.LiveActivated}");
        log.WriteLine($"Mode                 : {gw.Settings.Mode}");
        log.WriteLine($"InstrumentAllowlist  : [{string.Join(",", gw.Settings.Risk.InstrumentAllowlist)}]");
        log.WriteLine($"InstrumentAllowed(ES): {gw.Settings.Risk.InstrumentAllowed("ES")}");
        log.WriteLine($"InstrumentAllowed(MES): {gw.Settings.Risk.InstrumentAllowed("MES")}   (the owner's own instrument)");
        log.WriteLine($"MaxOrderQuantity     : {gw.Settings.Risk.MaxOrderQuantity}");
        log.WriteLine($"MaxOpenPositions     : {gw.Settings.Risk.MaxOpenPositions}");
        log.WriteLine($"MaxOrdersPerMinute   : {gw.Settings.Risk.MaxOrdersPerMinute}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);           // the kill switch stays DOWN
        Assert.False(gw.Settings.LiveActivated);             // real money is not switched on
        Assert.Empty(gw.Settings.Risk.InstrumentAllowlist);
        Assert.False(gw.Settings.Risk.InstrumentAllowed("ES"));    // ...and empty now allows NOTHING
        Assert.False(gw.Settings.Risk.InstrumentAllowed("MES"));   // not even the one the owner set
        Assert.Equal(0m, gw.Settings.Risk.MaxOrderQuantity);
        Assert.Equal(0, gw.Settings.Risk.MaxOpenPositions);
        Assert.Equal(0, gw.Settings.Risk.MaxOrdersPerMinute);

        // The gate, not just the fields: an order does not reach the broker.
        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "us-1", TestEnv.Buy()));
        log.WriteLine($"buy                  : {denied.Code} — {denied.Message}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");
        Assert.Empty(conn.Broker.Orders);
        Assert.Equal(0, conn.Places);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// The damage is not the enum. Anything the parser refuses lands in the same catch, so each of
    /// these has to fail closed the same way — otherwise this is a test about one string.
    /// </summary>
    [Theory]
    [InlineData("\"LIVE_LOCKED\"")]          // an enum name a newer build wrote
    [InlineData("[\"PAPER\"]")]              // a type change: a scalar field that became an array
    [InlineData("{\"name\":\"PAPER\"}")]     // ...or an object
    public async Task Every_shape_of_unreadable_row_fails_closed(string damaged)
    {
        var (db, _) = OwnersRowMadeUnreadable(damaged);
        using var _1 = db;
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"damaged mode value   : {damaged}");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"InstrumentAllowed(ES): {gw.Settings.Risk.InstrumentAllowed("ES")}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.False(gw.Settings.Risk.InstrumentAllowed("ES"));
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A truncated row — the write that did not finish. Same catch, same refusal, and it is worth its
    /// own case because it is the one an unclean shutdown actually produces.
    /// </summary>
    [Fact]
    public async Task A_half_written_row_fails_closed_too()
    {
        var (db, saved) = OwnersRowMadeUnreadable("");
        using var _1 = db;
        db.SetKv("settings", saved[..(saved.Length / 2)]);
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"row on disk          : {db.GetKv("settings")}");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN EMPTY ROW IS NOT AN ABSENT ROW, and only one of the two is a fresh install. The kv row is
    /// missing exactly once in a TradeAgent's life — before the first save; a row that exists and
    /// holds nothing is the truncation above with nothing left of it, so it has to refuse the same
    /// way. It did not: the blank check at the top of <c>LoadSettings</c> returned the defaults
    /// before the catch below ever saw the row.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public async Task An_empty_settings_row_is_a_lost_row_and_not_a_fresh_install(string blank)
    {
        var (db, _) = OwnersRowMadeUnreadable("");
        using var _1 = db;
        db.SetKv("settings", blank);
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"row on disk          : \"{db.GetKv("settings")}\"  (present, and empty)");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"MaxOrderQuantity     : {gw.Settings.Risk.MaxOrderQuantity}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(0m, gw.Settings.Risk.MaxOrderQuantity);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// The other side of that boundary, so "empty is unreadable" does not turn every first run into
    /// an alarm: a database with NO settings row is a TradeAgent nobody has configured yet, and it
    /// starts on the shipped defaults with nothing said about it.
    /// </summary>
    [Fact]
    public async Task A_database_with_no_settings_row_at_all_is_a_fresh_install()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var health = new HealthRegistry();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, health);

        log.WriteLine($"row on disk          : {db.GetKv("settings") ?? "(absent)"}");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"health               : {health.Get(Components.ExecutionCapability).Detail}");

        Assert.Null(db.GetKv("settings"));
        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.DoesNotContain("could not be read", health.Get(Components.ExecutionCapability).Detail);
        Assert.DoesNotContain(new LogStore(db).RecentActivity(50).Select(a => a.Text),
            m => m.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE BOUNDARY WITH U-GATES, NAMED RATHER THAN DUPLICATED. A mode written as a NUMBER is not a
    /// parse failure at all — <c>System.Text.Json</c>'s enum converter accepts numbers and casts one
    /// it does not recognise straight onto the enum — so this row IS read, and what refuses it is
    /// <c>TradeAgentSettings.ModeIsRecognised</c>, U-gates' check. Asserting it here keeps the two
    /// failures apart: if this ever started reporting "could not be read", the owner would be told
    /// their settings are damaged when in fact they are intact and one value is from the future.
    /// </summary>
    [Fact]
    public async Task A_numeric_mode_is_the_unknown_mode_check_and_not_this_one()
    {
        var (db, _) = OwnersRowMadeUnreadable("999");
        using var _1 = db;
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}   (the row parsed)");
        log.WriteLine($"ModeIsRecognised     : {gw.Settings.ModeIsRecognised}   (U-gates' check)");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}   (the owner's own value, kept)");
        log.WriteLine($"InstrumentAllowlist  : [{string.Join(",", gw.Settings.Risk.InstrumentAllowlist)}]   (kept)");

        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.False(gw.Settings.ModeIsRecognised);
        // The owner's values survive, which is the whole reason the two are not one check.
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(new[] { "MES" }, gw.Settings.Risk.InstrumentAllowlist);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "us-999", TestEnv.Buy()));
        log.WriteLine($"buy                  : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.MODE_FORBIDS_EXECUTION, denied.Code);
        Assert.Equal(0, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE OWNER IS TOLD. A refusal only an agent ever reads over a pipe is not news the person
    /// at the keyboard receives, and this failure is invisible by construction: every value they set
    /// is still on the screen they set it on, because the screen reads the object, not the row.
    /// </summary>
    [Fact]
    public async Task The_owner_is_told_that_their_settings_could_not_be_read()
    {
        var (db, _) = OwnersRowMadeUnreadable("\"LIVE_LOCKED\"");
        using var _1 = db;
        var health = new HealthRegistry();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, health);

        // Before any refresh: the row is set while the settings are loaded, in the constructor,
        // because a gateway that has not had its health refreshed yet is a gateway an agent can
        // already reach.
        var atConstruction = health.Get(Components.ExecutionCapability);
        log.WriteLine($"health at construction : {atConstruction.State} — {atConstruction.Detail}");

        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        var afterRefresh = health.Get(Components.ExecutionCapability);
        log.WriteLine($"health after refresh   : {afterRefresh.State} — {afterRefresh.Detail}");

        var said = new LogStore(db).RecentActivity(50).Select(a => a.Text).ToList();
        foreach (var line in said) log.WriteLine($"activity : {line}");

        Assert.Equal(HealthState.PAUSED, atConstruction.State);
        Assert.Contains("could not be read", atConstruction.Detail);
        // RefreshHealthAsync recomputes this row from scratch every five seconds. A state set once at
        // startup and overwritten a moment later is a claim that does not hold.
        Assert.Equal(HealthState.PAUSED, afterRefresh.State);
        Assert.Contains("could not be read", afterRefresh.Detail);

        Assert.Contains(said, m => m.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(said, m => m.Contains("Exception", StringComparison.Ordinal));

        // The engineering log carries the technical half, so the owner's line does not have to.
        var eng = new List<string>();
        using (var c = db.Cmd("SELECT event,severity FROM engineering_log WHERE component='Gateway' ORDER BY id"))
        using (var r = c.ExecuteReader())
            while (r.Read()) eng.Add($"{r.GetString(0)}/{r.GetString(1)}");
        log.WriteLine($"engineering : {string.Join(", ", eng)}");
        Assert.Contains("settings_unreadable/error", eng);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE OTHER DIRECTION, so that "fails closed" does not quietly mean "refuses everything". A row
    /// this build CAN read is obeyed exactly as it was written, including the kill switch being up.
    /// </summary>
    [Fact]
    public async Task A_row_this_build_can_read_is_obeyed_exactly()
    {
        var db = TestEnv.NewDb();
        using var _1 = db;
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var first = new TradingGateway(db, conn, new HealthRegistry());
        first.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.AiTradingStopped = false;
            s.Risk.InstrumentAllowlist = ["ES"];
            s.Risk.MaxOrderQuantity = 3m;
            s.Risk.MaxOpenPositions = 4;
            s.Risk.MaxOrdersPerMinute = 20;
        });

        var gw = new TradingGateway(db, conn, new HealthRegistry());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        log.WriteLine($"CouldNotBeRead      : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"allowlist           : [{string.Join(",", gw.Settings.Risk.InstrumentAllowlist)}]");

        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.False(gw.Settings.AiTradingStopped);
        Assert.Equal(3m, gw.Settings.Risk.MaxOrderQuantity);
        Assert.Equal(4, gw.Settings.Risk.MaxOpenPositions);
        Assert.Equal(20, gw.Settings.Risk.MaxOrdersPerMinute);

        var placed = await gw.PlaceAsync(new AgentContext("a"), "us-ok", TestEnv.Buy());
        log.WriteLine($"buy                 : {placed.State}, orders = {conn.Broker.Orders.Count}");
        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Single(conn.Broker.Orders);

        await gw.DisposeAsync();
        await first.DisposeAsync();
    }
}
