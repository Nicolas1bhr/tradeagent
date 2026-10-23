using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Board;
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
            s.SelectedAccountId = conn.Broker.AccountId;
            s.AiTradingStopped = true;
        });
        var saved = db.GetKv("settings")!;
        Assert.Contains("\"ai_trading_stopped\":true", saved);
        configured.DisposeAsync().AsTask().GetAwaiter().GetResult();

        // DAMAGED IN A FIELD THIS BUILD STILL READS (U-board-a1): the row holds no mode since the board, so the damage is the kill
        // switch's own value — a type change the parser refuses, which is the catch every shape below takes.
        db.SetKv("settings", damaged == "" ? saved : saved.Replace("\"ai_trading_stopped\":true", $"\"ai_trading_stopped\":{damaged}"));
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
        log.WriteLine($"LiveActivated        : {gw.Policy.LiveActivated}");
        log.WriteLine($"Mode                 : {gw.Policy.Mode}");
        log.WriteLine($"InstrumentAllowlist  : [{string.Join(",", gw.Policy.Risk.InstrumentAllowlist)}]");
        log.WriteLine($"InstrumentAllowed(ES): {gw.Policy.Risk.InstrumentAllowed("ES")}");
        log.WriteLine($"MaxOrderQuantity     : {gw.Policy.Risk.MaxOrderQuantity}");
        log.WriteLine($"MaxOpenPositions     : {gw.Policy.Risk.MaxOpenPositions}");
        log.WriteLine($"MaxOrdersPerMinute   : {gw.Policy.Risk.MaxOrdersPerMinute}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);           // the kill switch stays DOWN
        Assert.False(gw.Policy.LiveActivated);             // real money is not switched on
        // THE LIMITS ARE NOT THE ROW'S (U-board-a2 item 8): they are the security board's founding policy, which an unreadable
        // settings row neither widens nor reaches — and the gate below refuses on the unreadable row before any limit is read.
        Assert.Empty(RiskPolicy.Changes(BoardFounding.V1.Risk, gw.Policy.Risk));
        Assert.False(gw.Policy.Risk.InstrumentAllowed("ES"));

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
    [InlineData("\"LIVE_LOCKED\"")]          // a word where a newer build wrote another type
    [InlineData("[\"PAPER\"]")]              // a type change: a scalar field that became an array
    [InlineData("{\"name\":\"PAPER\"}")]     // ...or an object
    public async Task Every_shape_of_unreadable_row_fails_closed(string damaged)
    {
        var (db, _) = OwnersRowMadeUnreadable(damaged);
        using var _1 = db;
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"damaged value        : {damaged}");
        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}");
        log.WriteLine($"InstrumentAllowed(ES): {gw.Policy.Risk.InstrumentAllowed("ES")}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.False(gw.Policy.Risk.InstrumentAllowed("ES"));
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
        log.WriteLine($"MaxOrderQuantity     : {gw.Policy.Risk.MaxOrderQuantity}");

        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.True(gw.Settings.AiTradingStopped);
        Assert.Equal(BoardFounding.V1.Risk.MaxOrderQuantity, gw.Policy.Risk.MaxOrderQuantity);   // the policy's, untouched (U-board-a2)
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
    /// THE BOUNDARY WITH AN EARLIER BUILD'S MODE, NAMED RATHER THAN DUPLICATED (rewritten by <c>U-board-a1</c>: the mode is the security
    /// board's policy and this type no longer holds one). A row an earlier build saved with a mode written as a NUMBER parses — the
    /// key is not this type's at all — so it is NOT "could not be read": the owner's values are kept, and what refuses it is the
    /// earlier-home check, which will not guess what a mode it cannot name meant (<c>HOME_ARMED_BEFORE_THE_BOARD</c>). If this ever
    /// started reporting "could not be read", the owner would be told their settings are damaged when they are intact.
    /// </summary>
    [Fact]
    public async Task A_numeric_mode_is_the_unknown_mode_check_and_not_this_one()
    {
        var (db, saved) = OwnersRowMadeUnreadable("");
        using var _1 = db;
        db.SetKv("settings", "{\"mode\":999," + saved[1..]);
        var (gw, conn) = await RestartOver(db);

        log.WriteLine($"CouldNotBeRead       : {gw.Settings.CouldNotBeRead}   (the row parsed)");
        log.WriteLine($"earlier setting      : {gw.EarlierLiveSetting?.Mode}   (the earlier-home check)");
        log.WriteLine($"AiTradingStopped     : {gw.Settings.AiTradingStopped}   (the owner's own value, kept)");

        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.Equal("999", gw.EarlierLiveSetting?.Mode);
        // The owner's values survive, which is the whole reason the two are not one check.
        Assert.True(gw.Settings.AiTradingStopped);

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(AgentContext.Operator, "us-999", TestEnv.Buy("MES")));
        log.WriteLine($"buy                  : {denied.Code} — {denied.Message}");
        Assert.Equal(ErrorCode.HOME_ARMED_BEFORE_THE_BOARD, denied.Code);
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
            s.SelectedAccountId = conn.Broker.AccountId;
            s.AiTradingStopped = false;
        });

        var gw = new TradingGateway(db, conn, new HealthRegistry());
        // THE LIMITS ARE THE POLICY'S (U-board-a2 item 8), not the row's: this gateway starts on the founding ones, and the
        // fixture's are set on its in-memory copy, after the restart, through the test host's seam.
        Assert.Empty(RiskPolicy.Changes(BoardFounding.V1.Risk, gw.Policy.Risk));
        gw.UpdateRiskUnderTest(r =>
        {
            r.InstrumentAllowlist = ["ES"];
            r.MaxOrderQuantity = 3m;
            r.MaxOpenPositions = 4;
            r.MaxOrdersPerMinute = 20;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        log.WriteLine($"CouldNotBeRead      : {gw.Settings.CouldNotBeRead}");
        log.WriteLine($"allowlist           : [{string.Join(",", gw.Policy.Risk.InstrumentAllowlist)}]");

        Assert.False(gw.Settings.CouldNotBeRead);
        Assert.False(gw.Settings.AiTradingStopped);
        Assert.Equal(3m, gw.Policy.Risk.MaxOrderQuantity);
        Assert.Equal(4, gw.Policy.Risk.MaxOpenPositions);
        Assert.Equal(20, gw.Policy.Risk.MaxOrdersPerMinute);

        var placed = await gw.PlaceAsync(new AgentContext("a"), "us-ok", TestEnv.Buy());
        log.WriteLine($"buy                 : {placed.State}, orders = {conn.Broker.Orders.Count}");
        Assert.Equal(ExecutionState.FILLED, placed.State);
        Assert.Single(conn.Broker.Orders);

        await gw.DisposeAsync();
        await first.DisposeAsync();
    }

    // ── item 3 ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE RECOVERY, END TO END, AS THE OWNER'S PAGE PERFORMS IT. Since <c>U-board-a2</c> the limits are the security board's
    /// policy and Save limits is gone with them, so the write is the one the owner's own page still makes — the daily cap's
    /// save, one <c>Gateway.Update</c> of a field this row still holds: the owner reads the banner, checks his values and
    /// presses it. Afterwards the row on disk
    /// is one this build wrote — so the warning has to stop ON THIS PASS, not after a restart, or the
    /// owner presses the only button on the page and nothing they can see changes — and a restart
    /// over the same database has to agree.
    /// </summary>
    [Fact]
    public async Task Saving_the_limits_rewrites_the_row_and_stops_the_warning()
    {
        var (db, _) = OwnersRowMadeUnreadable("\"LIVE_LOCKED\"");
        using var _1 = db;
        var health = new HealthRegistry();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, health);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var before = health.Get(Components.ExecutionCapability);
        log.WriteLine($"before save   : {before.State} — {before.Detail}");
        Assert.True(gw.Settings.CouldNotBeRead);
        Assert.Contains("could not be read", before.Detail);

        gw.Update(s => s.AiDailyCostCap = 2m);            // exactly what the daily cap's save writes

        Assert.False(gw.Settings.CouldNotBeRead);
        await gw.RefreshHealthAsync();
        var after = health.Get(Components.ExecutionCapability);
        log.WriteLine($"after save    : {after.State} — {after.Detail}");
        Assert.DoesNotContain("could not be read", after.Detail);
        await gw.DisposeAsync();

        var (again, _2) = await RestartOver(db);
        log.WriteLine($"after restart : CouldNotBeRead {again.Settings.CouldNotBeRead}, "
                      + $"daily cap {again.Settings.AiDailyCostCap}");
        Assert.False(again.Settings.CouldNotBeRead);
        Assert.Equal(2m, again.Settings.AiDailyCostCap);

        // The damaged row is still kept. It is the only evidence of what the owner had asked for,
        // and the save above overwrote the row it was taken from.
        log.WriteLine($"kept row      : {db.GetKv("settings_unreadable")}");
        Assert.Contains("LIVE_LOCKED", db.GetKv("settings_unreadable")!);
        await again.DisposeAsync();
    }

    /// <summary>
    /// NO FILE, NO FOLDER, NO PATH, NO TERMINAL, NO DATABASE — in any sentence this failure puts in
    /// front of the owner. This is the product rule, not a style note: the person who bought this
    /// never opens a shell, so a sentence that names a path, a file or a command is one they cannot
    /// act on, and the repair it describes does not exist for them. Every string here is one they
    /// can actually reach — the health row on the Dashboard, the activity log, the banner and the
    /// note on the Safety page, and the refusal a denied order carries back.
    /// </summary>
    [Fact]
    public async Task No_sentence_the_owner_reads_names_a_file_a_path_or_a_terminal()
    {
        var (db, _) = OwnersRowMadeUnreadable("\"LIVE_LOCKED\"");
        using var _1 = db;
        var health = new HealthRegistry();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, health);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var denied = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.PlaceAsync(new AgentContext("a"), "us-say", TestEnv.Buy()));

        var sentences = new List<string>
        {
            Labels.SettingsCouldNotBeRead,
            Labels.SettingsCouldNotBeReadTitle,
            Labels.SettingsCouldNotBeReadBanner,
            Labels.SettingsCouldNotBeReadNext,
            Labels.NoInstrumentAllowed,
            health.Get(Components.ExecutionCapability).Detail,
            denied.Info.UserMessage,
            denied.Info.Repair
        };
        sentences.AddRange(new LogStore(db).RecentActivity(50).Select(a => a.Text));

        // "cmd" and "sh" are substrings of ordinary words; the rest are not.
        string[] banned =
        [
            "\\", "/", ":\\", ".json", ".db", ".exe", "sqlite", "database", "terminal", "console",
            "command prompt", "powershell", "appdata", "folder", "file", "directory", "path",
            "json", "npm", "reinstall", "restart the computer"
        ];

        foreach (var s in sentences) log.WriteLine($"owner reads : {s}");
        foreach (var s in sentences)
            foreach (var b in banned)
                Assert.False(s.Contains(b, StringComparison.OrdinalIgnoreCase),
                    $"the owner is shown \"{b}\" in: {s}");

        // And the sentences point at controls that exist: the page in the nav rail, and the button
        // on it. Both come from the same constants the screens are built from.
        Assert.Contains(Labels.OwnerPage, Labels.SettingsCouldNotBeRead);
        Assert.Contains(Labels.SaveDailyCap, Labels.SettingsCouldNotBeReadBanner);

        await gw.DisposeAsync();
    }
}
