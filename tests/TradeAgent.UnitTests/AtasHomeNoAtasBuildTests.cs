using TradeAgent.App;
using TradeAgent.Connectors.Atas;
using TradeAgent.Connectors.Fake;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Platform = TradeAgent.Platforms.Connectors;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// A HOME WHOSE SAVED PLATFORM IS ATAS, OPENED BY A BUILD WITHOUT THE ATAS ADAPTER (<c>U-atas-home-no-atas-build</c>): the ATAS connector
/// is never built, the start refuses in words before anything exists, the owner's two presses put this build on TradeAgent's paper venue
/// with his ATAS choice kept, and every order sent to ATAS stays exactly as recorded — UNKNOWN stays UNKNOWN, trading stays paused.
/// </summary>
public class AtasHomeNoAtasBuildTests
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _now += by;
    }

    static ExecutionRequest OnAtas(string id, DateTimeOffset at) => new()
    {
        RequestId = id, ConnectorId = Platform.Atas, AccountId = "ATAS-ACCOUNT-1", Instrument = "BTCUSDT",
        Intent = RequestIntent.PLACE, ParametersJson = "{}", ClientOrderId = $"TA-{id}",
        CreatedAt = at, State = ExecutionState.CREATED, Mode = TradingMode.PAPER
    };

    /// <summary>An order sent to ATAS whose answer was lost on the wire: UNKNOWN, flagged.</summary>
    static void LostOnAtas(ExecutionRequestStore store, string id, DateTimeOffset at)
    {
        store.TryCreate(OnAtas(id, at));
        store.Transition(id, ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition(id, ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, needsReconciliation: true);
    }

    [Fact]
    public async Task A_build_without_the_adapter_never_builds_the_ATAS_connector()
    {
        var refused = Assert.Throws<InvalidOperationException>(() => Platform.Create(Platform.Atas, new ConnectorChoice()));
        Assert.Equal(BuildContents.AtasNotInThisBuild, refused.Message);
        Assert.Throws<InvalidOperationException>(() => Platform.Create(Platform.Atas));

        // And a build WITH the adapter still builds it: the flag is the build's own fact, not a lock on ATAS.
        await using var built = Platform.Create(Platform.Atas, new ConnectorChoice { AtasAdapterInThisBuild = true });
        Assert.IsType<AtasConnector>(built);
    }

    [Fact]
    public void A_home_whose_platform_is_ATAS_is_refused_at_the_start_in_words_and_left_as_it_was()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("connector", Platform.Atas);

        var platform = AtasHome.PlatformAtStart(db, atasInThisBuild: false, out var refused);

        Assert.Null(platform);
        Assert.NotNull(refused);
        Assert.StartsWith("This build has no ATAS; your setup chose ATAS.", refused.Message);
        Assert.Equal(Platform.Atas, db.GetKv("connector"));
        Assert.Null(db.GetKv(AtasHome.StandInKey));

        // The same home in a build WITH the adapter starts ATAS; any other saved platform starts as it always did.
        Assert.Equal(Platform.Atas, AtasHome.PlatformAtStart(db, atasInThisBuild: true, out var none));
        Assert.Null(none);
        db.SetKv("connector", Platform.Paper);
        Assert.Equal(Platform.Paper, AtasHome.PlatformAtStart(db, atasInThisBuild: false, out none));
        Assert.Null(none);
    }

    [Fact]
    public void The_refusal_names_the_unsettled_ATAS_orders_and_leaves_every_one_as_recorded()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("connector", Platform.Atas);
        var store = new ExecutionRequestStore(db);
        var at = DateTimeOffset.UtcNow;
        LostOnAtas(store, "lost-1", at);
        store.TryCreate(OnAtas("working-1", at));
        store.Transition("working-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("working-1", ExecutionState.DISPATCHING, ExecutionState.WORKING);
        store.TryCreate(OnAtas("filled-1", at));
        store.Transition("filled-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("filled-1", ExecutionState.DISPATCHING, ExecutionState.FILLED);
        var before = store.Query().Select(r => (r.RequestId, r.State, r.NeedsReconciliation)).OrderBy(r => r.RequestId).ToList();

        AtasHome.PlatformAtStart(db, atasInThisBuild: false, out var refused);

        Assert.NotNull(refused);
        Assert.Equal(["lost-1", "working-1"], refused.Unsettled.Select(r => r.RequestId).Order());
        Assert.Equal(1, refused.Unconfirmed);
        Assert.Contains("2 orders TradeAgent sent to ATAS are not settled, and 1 of them are not confirmed.", refused.Message);
        Assert.Contains("they stay exactly as recorded until a TradeAgent with ATAS reconciles them", refused.Message);

        AtasHome.ChoosePaper(db);
        Assert.Equal(before, store.Query().Select(r => (r.RequestId, r.State, r.NeedsReconciliation)).OrderBy(r => r.RequestId).ToList());
    }

    [Fact]
    public void Choosing_paper_keeps_the_ATAS_choice_and_a_build_with_ATAS_goes_back_to_it()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("connector", Platform.Atas);

        AtasHome.ChoosePaper(db);

        Assert.Equal(Platform.Atas, db.GetKv("connector"));
        Assert.Equal(Platform.Paper, AtasHome.PlatformAtStart(db, atasInThisBuild: false, out var refused));
        Assert.Null(refused);
        Assert.Equal(Platform.Atas, AtasHome.PlatformAtStart(db, atasInThisBuild: true, out refused));
        Assert.Null(refused);
    }

    /// <summary>
    /// ON THE PAPER VENUE THE OWNER CHOSE, AN ORDER SENT TO ATAS IS NOT RECONCILED AGAINST PAPER'S BOOK: paper's empty history, long past
    /// the absence grace, would otherwise read "never landed" and write it off. It stays UNKNOWN and flagged, and trading stays paused.
    /// </summary>
    [Fact]
    public async Task On_the_paper_venue_an_UNKNOWN_ATAS_order_stays_UNKNOWN_and_trading_stays_paused()
    {
        using var db = TestEnv.NewDb();
        using var scratch = TestEnv.NewScratch("atas-home-paper");
        db.SetKv("connector", Platform.Atas);
        var clock = new TestClock(DateTimeOffset.UtcNow);
        LostOnAtas(new ExecutionRequestStore(db, clock), "lost-1", clock.GetUtcNow());
        AtasHome.ChoosePaper(db);
        Assert.Equal(Platform.Paper, AtasHome.PlatformAtStart(db, atasInThisBuild: false, out _));

        await using var paper = new PaperConnector(new PaperConnectorOptions
        {
            Source = new MemoryBarSource(), BookFile = Path.Combine(scratch.Dir, "paper.db")
        });
        await using var gateway = new TradingGateway(db, paper, options: new GatewayOptions { Clock = clock });
        await paper.ConnectAsync();
        clock.Advance(TimeSpan.FromMinutes(30));

        var result = await gateway.ReconcileAsync();

        var row = gateway.GetRequest("lost-1")!;
        Assert.Equal(ExecutionState.UNKNOWN, row.State);
        Assert.True(row.NeedsReconciliation);
        Assert.False(result.Clean);
        Assert.Contains(result.Details, d => d.Contains("placed on atas; connected to paper"));
        Assert.False(gateway.TryAuthorizeExecution(new AgentContext("a"), out _));
    }

    /// <summary>The app hands the connector factory its own build fact — an ATAS build that forgot would refuse its own ATAS.</summary>
    [Fact]
    public async Task The_app_hands_its_build_fact_to_the_connector_factory()
    {
        using var db = TestEnv.NewDb();
        var host = AppHost.Composed(db, new FakeConnector(new FakeBroker()), new AgentPresence());
        try
        {
            host.AtasInThisBuild = true;
            Assert.True(host.ConnectorChoiceNow().AtasAdapterInThisBuild);
            host.AtasInThisBuild = false;
            Assert.False(host.ConnectorChoiceNow().AtasAdapterInThisBuild);
        }
        finally { await host.Gateway.DisposeAsync(); }
    }

    /// <summary>A gateway on the paper stand-in (a fake venue here), healthy and allowed to trade.</summary>
    static async Task<TradingGateway> OnTheStandIn(Database db, FakeConnector venue)
    {
        var gw = new TradingGateway(db, venue, new HealthRegistry());
        gw.Update(s => s.SelectedAccountId = "SIM-001");
        gw.UpdateRiskUnderTest(r =>
        {
            r.InstrumentAllowlist = [.. TestEnv.Instruments];
            r.MaxOrderQuantity = 10m;
            r.MaxNotionalPerOrder = 10_000_000m;
            r.MaxOpenPositions = 10;
            r.MaxOrdersPerMinute = 100;
        });
        await venue.ConnectAsync();
        await gw.RefreshHealthAsync();
        return gw;
    }

    /// <summary>
    /// AN ORDER SENT TO ATAS IS NEVER CANCELLED OR CHANGED THROUGH THE VENUE THAT STANDS IN FOR IT (Codex review, P1): its broker id means
    /// nothing on paper — sent there, it cancels nothing or, on a collision, one of paper's own orders. Refused in words; nothing is written.
    /// </summary>
    [Fact]
    public async Task An_order_sent_to_ATAS_is_not_cancelled_or_changed_through_the_stand_in()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        store.TryCreate(OnAtas("working-1", DateTimeOffset.UtcNow));
        store.Transition("working-1", ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition("working-1", ExecutionState.DISPATCHING, ExecutionState.WORKING, connectorOrderId: "ATAS-77");
        await using var gw = await OnTheStandIn(db, new FakeConnector(new FakeBroker()));

        var cancel = await Assert.ThrowsAsync<GatewayDeniedException>(() => gw.CancelAsync(new AgentContext("a"), "c-1", "working-1"));
        Assert.Contains("was placed on atas", cancel.Message);
        var modify = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            gw.ModifyAsync(new AgentContext("a"), "m-1", "working-1", 1m, 100m, null));
        Assert.Contains("was placed on atas", modify.Message);

        Assert.Null(gw.GetRequest("c-1"));
        Assert.Null(gw.GetRequest("m-1"));
        Assert.Equal(ExecutionState.WORKING, gw.GetRequest("working-1")!.State);
    }

    /// <summary>
    /// AN EMERGENCY PRESS MADE ON ATAS IS NOT JUDGED BY THE STAND-IN'S BOOK (Codex review, P2): paper's empty account would have told the
    /// Dashboard an unconfirmed ATAS close left the instrument "now flat". The card shows no position, and the press is not complete.
    /// </summary>
    [Fact]
    public async Task An_ATAS_press_is_not_judged_by_the_stand_in_book()
    {
        using var db = TestEnv.NewDb();
        var store = new ExecutionRequestStore(db);
        var row = OnAtas($"{TradingGateway.ClosePress}-n1-BTCUSDT", DateTimeOffset.UtcNow);
        store.TryCreate(row);
        store.Transition(row.RequestId, ExecutionState.CREATED, ExecutionState.DISPATCHING);
        store.Transition(row.RequestId, ExecutionState.DISPATCHING, ExecutionState.UNKNOWN, needsReconciliation: true);
        await using var gw = await OnTheStandIn(db, new FakeConnector(new FakeBroker()));

        var outcome = await gw.PressOutcomeAsync(TradingGateway.ClosePress, "n1");

        Assert.Null(Assert.Single(outcome.Targets).PositionNow);
        Assert.False(outcome.Complete);
        Assert.Contains("sent to atas", outcome.Summary);
    }

    /// <summary>
    /// THE ATAS ACCOUNT IS HELD ASIDE ON THE STAND-IN AND HANDED BACK TO ATAS (Codex review, P2): an ATAS account id names nothing on
    /// paper, so the stand-in starts with none chosen (the owner chooses paper's on the Settings page) and a build with ATAS gets it back.
    /// </summary>
    [Fact]
    public async Task The_ATAS_account_is_held_aside_on_the_stand_in_and_handed_back_to_ATAS()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("connector", Platform.Atas);
        await using (var atas = new TradingGateway(db, new FakeConnector(new FakeBroker())))
            atas.Update(s => s.SelectedAccountId = "ATAS-ACCOUNT-1");
        AtasHome.ChoosePaper(db);

        await using (var paper = new TradingGateway(db, new FakeConnector(new FakeBroker())))
        {
            AtasHome.HoldTheAtasAccount(db, paper, Platform.Paper);
            Assert.Null(paper.Settings.SelectedAccountId);
            paper.Update(s => s.SelectedAccountId = "SIM-001");
            AtasHome.HoldTheAtasAccount(db, paper, Platform.Paper);   // the next start on paper keeps the paper account
            Assert.Equal("SIM-001", paper.Settings.SelectedAccountId);
        }

        await using var back = new TradingGateway(db, new FakeConnector(new FakeBroker()));
        AtasHome.HoldTheAtasAccount(db, back, Platform.Atas);
        Assert.Equal("ATAS-ACCOUNT-1", back.Settings.SelectedAccountId);
    }

    /// <summary>
    /// AN UNFINISHED ATAS SETUP ASKS ITS PLATFORM AGAIN (Codex review, P2): on the stand-in the ATAS steps would pass as "not using ATAS"
    /// and a later build with ATAS would skip them. The press sends the owner back to the platform step instead.
    /// </summary>
    [Fact]
    public void Choosing_paper_on_an_unfinished_ATAS_setup_asks_the_platform_again()
    {
        using var db = TestEnv.NewDb();
        db.SetKv("connector", Platform.Atas);
        var setup = new OnboardingStore(db);
        foreach (var step in Enum.GetValues<OnboardingStep>().TakeWhile(s => s != OnboardingStep.ATAS_INSTALLED)) setup.Complete(step);
        Assert.Equal(OnboardingStep.ATAS_INSTALLED, setup.Current());
        AtasHome.PlatformAtStart(db, atasInThisBuild: false, out var refused);
        Assert.False(refused!.SetupFinished);

        AtasHome.ChoosePaper(db);

        Assert.Equal(OnboardingStep.TRADING_PLATFORM_SELECTED, setup.Current());
        Assert.Equal(Platform.Paper, AtasHome.PlatformAtStart(db, atasInThisBuild: false, out _));
    }
}
