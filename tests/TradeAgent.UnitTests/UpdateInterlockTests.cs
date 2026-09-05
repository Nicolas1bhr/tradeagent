using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Diagnostics;
using TradeAgent.Gateway;
using TradeAgent.Provisioning;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// The interlock's OWN question, rather than the updater's file handling (<see cref="UpdateTrustTests"/>).
///
/// Milestone review 2026-09-05 finding 3 / Codex F5, executed as probes P4 and P5: the provider was
/// <c>gateway.Requests.NeedingReconciliation()</c> with no argument — the raw <c>needs_reconciliation</c>
/// flag and nothing else. Three kinds of record the wire may be holding were therefore invisible to it:
/// a row still DISPATCHING (the broker has the frame, nobody has written down the answer), a row the
/// gateway itself is refusing to trade over because the dispatch is stranded, and the in-memory latch
/// that U2c-1 raises when an outcome arrives and the store will not take it. Each one ends the same
/// way: Setup runs, and the process holding the owner's open orders is replaced mid-order.
///
/// The stop is also attached to ONE gateway. Switching trading platforms builds a new one
/// (<c>AppHost.SwitchConnectorAsync</c>), and the updater kept answering from the disposed one.
/// </summary>
public class UpdateInterlockTests(ITestOutputHelper log)
{
    const string Asset = "TradeAgent-Setup-x64.exe";
    const string Hash = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";

    static string Release() =>
        $$"""
        {"tag_name":"v0.2.0","draft":false,"prerelease":false,
         "html_url":"https://github.com/owner/repo/releases/tag/v0.2.0","body":"notes",
         "assets":[{"name":"{{Asset}}","size":90000000,"browser_download_url":"https://example.invalid/{{Asset}}"},
                   {"name":"SHA256SUMS.txt","size":100,"browser_download_url":"https://example.invalid/SHA256SUMS.txt"}]}
        """;

    /// <summary>GitHub, the network and the installer, replaced by a record of what was attempted.</summary>
    sealed class Fake
    {
        public int Launches;
        public string? Launched;
        public bool DownloadStarted;

        public UpdateSources Sources() => new(
            _ => Task.FromResult<string?>(Release()),
            (_, _) => Task.FromResult<string?>($"{Hash}  artifacts/{Asset}\r\n"),
            (_, _, _, _) => { DownloadStarted = true; return Task.FromResult("C:/updates/0.2.0/" + Asset); },
            path => { Launched = path; Launches++; },
            (_, _) => Task.FromResult(Hash));
    }

    static UpdateService Offered(Fake f)
    {
        var updates = new UpdateService("0.1.0", "owner/repo", UpdateService.DefaultAssetPattern, f.Sources());
        return updates;
    }

    /// <summary>A gateway on its own store, healthy and allowed to trade, with a connector of our choosing.</summary>
    static async Task<TradingGateway> Ready(Database db, ITradingConnector conn, string accountId,
        GatewayOptions? options = null)
    {
        var gw = new TradingGateway(db, conn, new HealthRegistry(), options);
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = accountId;
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return gw;
    }

    // ---- 1. everything wire-touched counts ---------------------------------------------------------

    /// <summary>
    /// P5, lifted from <c>review-probes</c> and turned the right way up. No clock trick and no SQL:
    /// the connector is simply still inside <c>PlaceOrderAsync</c>, so the record is DISPATCHING
    /// because the gateway wrote it there microseconds ago, and the broker may already have the order.
    ///
    /// This is the sharpest case because nothing is wrong yet. It is an ordinary placement, in flight,
    /// and replacing the program now is how it becomes an order nobody will ever reconcile.
    /// </summary>
    [Fact]
    public async Task An_order_on_the_wire_right_now_stops_the_install()
    {
        using var db = TestEnv.NewDb();
        var inner = new FakeConnector(new FakeBroker());
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        var conn = new AtTheWire(inner) { Park = release.Task, Reached = reached };
        var gw = await Ready(db, conn, inner.Broker.AccountId);

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "onthewire-1", TestEnv.Buy());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var state = gw.GetRequest("onthewire-1")!.State;
        var counted = updates.UnconfirmedWork!();
        var installed = await updates.InstallAsync();

        log.WriteLine($"record state while installing : {state}");
        log.WriteLine($"updater UnconfirmedWork()     : {counted}");
        log.WriteLine($"InstallAsync returned         : {installed}");
        log.WriteLine($"Setup launched                : {f.Launches} time(s)");

        Assert.Equal(ExecutionState.DISPATCHING, state);
        Assert.Equal(1, counted);
        Assert.False(installed);
        Assert.False(f.DownloadStarted);
        Assert.Equal(0, f.Launches);

        release.SetResult();
        await inFlight;
        await gw.DisposeAsync();
    }

    /// <summary>
    /// P4, lifted: a record that reached DISPATCHING and never came back, now well past the stranded
    /// bound. The gateway calls this unconfirmed work and refuses to TRADE over it; the updater's own
    /// view of the same store said zero and replaced the program. Both must now agree.
    /// </summary>
    [Fact]
    public async Task An_order_stranded_in_dispatching_stops_the_install()
    {
        using var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker());
        var clock = new Movable(DateTimeOffset.UtcNow);
        var gw = await Ready(db, conn, conn.Broker.AccountId, new GatewayOptions { Clock = clock });

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        await gw.PlaceAsync(new AgentContext("a"), "inflight-1", TestEnv.Buy());
        db.Exec("UPDATE execution_request SET execution_state='DISPATCHING', needs_reconciliation=0 " +
                "WHERE request_id='inflight-1'");
        clock.Now += TimeSpan.FromMinutes(10);   // well past any derived DispatchStrandedAfter

        var gatewaySays = gw.Unreconciled().Count;
        var willTrade = gw.TryAuthorizeExecution(new AgentContext("a"), out var why, out var code);
        var counted = updates.UnconfirmedWork!();
        var installed = await updates.InstallAsync();

        log.WriteLine($"gateway Unreconciled()    : {gatewaySays}");
        log.WriteLine($"gateway will trade        : {willTrade}  ({code}: {why})");
        log.WriteLine($"updater UnconfirmedWork() : {counted}");
        log.WriteLine($"InstallAsync returned     : {installed}  (Setup launched {f.Launches} time(s))");

        Assert.Equal(1, gatewaySays);
        Assert.False(willTrade);
        Assert.Equal(1, counted);        // the two views of one store agree
        Assert.False(installed);
        Assert.Equal(0, f.Launches);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// The third arm, and the one no store query can reach: the broker answered, the store would not
    /// take the answer, and U2c-1 latched it in memory. The row is unflagged and — after the store
    /// starts taking writes again and something else moves it on — need not even be DISPATCHING. The
    /// gateway holds trading shut on the latch alone; the updater has to read the same latch.
    /// </summary>
    [Fact]
    public async Task An_outcome_the_store_could_not_take_stops_the_install()
    {
        using var db = TestEnv.NewDb();
        var inner = new FakeConnector(new FakeBroker());
        var conn = new AtTheWire(inner) { AfterPlace = () => db.Exec("PRAGMA query_only = ON") };
        var gw = await Ready(db, conn, inner.Broker.AccountId);

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        var boom = await Assert.ThrowsAnyAsync<Exception>(
            () => gw.PlaceAsync(new AgentContext("a"), "latched-1", TestEnv.Buy()));
        db.Exec("PRAGMA query_only = OFF");                       // whatever held the store lets go

        var row = gw.GetRequest("latched-1")!;
        log.WriteLine($"place threw               : {(boom as TradeAgentException)?.Code.ToString() ?? boom.GetType().Name}");
        log.WriteLine($"row state / flag          : {row.State} / needs_reconciliation={row.NeedsReconciliation}");
        log.WriteLine($"gateway HasUnconfirmedWork: {gw.HasUnconfirmedWork()}");
        log.WriteLine($"updater UnconfirmedWork() : {updates.UnconfirmedWork!()}");

        Assert.False(row.NeedsReconciliation);                    // nothing on disk says anything
        Assert.True(gw.HasUnconfirmedWork());                     // the latch is holding trading shut
        Assert.Equal(1, updates.UnconfirmedWork!());
        Assert.False(await updates.InstallAsync());
        Assert.Equal(0, f.Launches);

        // And with the row moved on to a state no store query counts, the latch is the ONLY thing
        // left — which is exactly the case the raw flag could never see.
        db.Exec("UPDATE execution_request SET execution_state='WORKING', needs_reconciliation=0 " +
                "WHERE request_id='latched-1'");
        log.WriteLine($"latch alone, row WORKING  : {updates.UnconfirmedWork!()}");
        Assert.Equal(1, updates.UnconfirmedWork!());
        Assert.False(await updates.InstallAsync());
        Assert.Equal(0, f.Launches);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// The updater's question may never be SMALLER than the gate's. It is deliberately wider — a
    /// placement two seconds old is safe to trade around and is never safe to update over — so this
    /// pins the direction of the inequality rather than an equality that is not true.
    /// </summary>
    [Fact]
    public async Task What_the_updater_counts_is_never_less_than_what_the_gate_refuses_over()
    {
        using var db = TestEnv.NewDb();
        var inner = new FakeConnector(new FakeBroker());
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        var conn = new AtTheWire(inner) { Park = release.Task, Reached = reached };
        var gw = await Ready(db, conn, inner.Broker.AccountId);

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        Assert.Equal(gw.Unreconciled().Count, updates.UnconfirmedWork!());   // nothing anywhere: equal

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "wider-1", TestEnv.Buy());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        log.WriteLine($"gate Unreconciled()       : {gw.Unreconciled().Count}");
        log.WriteLine($"updater UnconfirmedWork() : {updates.UnconfirmedWork!()}");
        Assert.True(updates.UnconfirmedWork!() >= gw.Unreconciled().Count);
        Assert.Equal(1, updates.UnconfirmedWork!());

        release.SetResult();
        await inFlight;
        await gw.DisposeAsync();
    }

    // ---- 3. both directions ------------------------------------------------------------------------

    /// <summary>
    /// The interlock is a stop, not a wall. With the wire quiet the install runs, all the way to
    /// Setup — the same gateway, the same seam, the same question, answered zero.
    /// </summary>
    [Fact]
    public async Task With_nothing_on_the_wire_the_update_still_installs()
    {
        using var db = TestEnv.NewDb();
        var conn = new FakeConnector(new FakeBroker());
        var gw = await Ready(db, conn, conn.Broker.AccountId);

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        // A settled order is not wire-touched work: it was placed, filled and written down.
        var placed = await gw.PlaceAsync(new AgentContext("a"), "settled-1", TestEnv.Buy());
        log.WriteLine($"settled order state       : {placed.State}");
        log.WriteLine($"updater UnconfirmedWork() : {updates.UnconfirmedWork!()}");

        Assert.Equal(0, updates.UnconfirmedWork!());
        Assert.True(await updates.InstallAsync());
        Assert.Equal(1, f.Launches);
        Assert.False(updates.Refused);

        await gw.DisposeAsync();
    }

    /// <summary>
    /// U2d's refusal path, reached by the wider count: the refusal names how many orders it is about,
    /// is marked as the one refusal that expires on its own, and is written where the owner can find
    /// it afterwards. Those three are what the strip and the Settings card render.
    /// </summary>
    [Fact]
    public async Task The_refusal_names_the_count_and_reaches_the_owners_surfaces()
    {
        using var db = TestEnv.NewDb();
        var inner = new FakeConnector(new FakeBroker());
        var release = new TaskCompletionSource();
        var reached = new TaskCompletionSource();
        var conn = new AtTheWire(inner) { Park = release.Task, Reached = reached };
        var gw = await Ready(db, conn, inner.Broker.AccountId);

        var f = new Fake();
        var updates = Offered(f);
        UpdateTradingInterlock.Attach(gw, updates);
        await updates.CheckAsync();

        var inFlight = gw.PlaceAsync(new AgentContext("a"), "surfaced-1", TestEnv.Buy());
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(await updates.InstallAsync());

        log.WriteLine($"Message : {updates.Message}");
        log.WriteLine($"Refused : {updates.Refused} / pending work: {updates.RefusedPendingWork}");

        Assert.True(updates.Refused);                             // the strip renders refusals only
        Assert.True(updates.RefusedPendingWork);                  // and expires this one
        Assert.Equal(UpdateStage.Failed, updates.Stage);
        Assert.Contains("an order's outcome is", updates.Message);
        Assert.Contains("still unconfirmed", updates.Message);

        var written = gw.Log.RecentActivity().Where(r => r.Text.Contains("still unconfirmed")).ToList();
        Assert.Single(written);

        release.SetResult();
        await inFlight;
        await gw.DisposeAsync();
    }

    // ---- harness -----------------------------------------------------------------------------------

    sealed class Movable(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>
    /// Everything the simulator does, plus a place that can park ON the wire (<see cref="Park"/>) or
    /// break the world the moment the broker has taken the order (<see cref="AfterPlace"/>). Lifted
    /// from the reviewer's P5 probe and given the second hook, because the two halves of finding 3 are
    /// the same instant seen from either side.
    /// </summary>
    sealed class AtTheWire(FakeConnector inner) : ITradingConnector
    {
        public Task? Park;
        public TaskCompletionSource? Reached;
        public Action? AfterPlace;

        public async Task<OrderInfo> PlaceOrderAsync(PlaceOrderCommand cmd, CancellationToken ct = default)
        {
            Reached?.TrySetResult();
            if (Park is not null) await Park;
            var order = await inner.PlaceOrderAsync(cmd, ct);
            AfterPlace?.Invoke();
            return order;
        }

        public string Id => inner.Id;
        public string DisplayName => inner.DisplayName;
        public ConnectorCapabilities Capabilities => inner.Capabilities;
        public TimeSpan WorstCaseOperationPath => inner.WorstCaseOperationPath;
        public TimeSpan EmergencyBudget => inner.EmergencyBudget;
        public Task ConnectAsync(CancellationToken ct = default) => inner.ConnectAsync(ct);
        public Task<HealthState> GetHealthAsync(CancellationToken ct = default) => inner.GetHealthAsync(ct);
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => inner.IsConnectedAsync(ct);
        public Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default) => inner.GetAccountsAsync(ct);
        public Task<AccountInfo?> GetAccountAsync(string a, CancellationToken ct = default) => inner.GetAccountAsync(a, ct);
        public Task<IReadOnlyList<InstrumentInfo>> GetInstrumentsAsync(CancellationToken ct = default) => inner.GetInstrumentsAsync(ct);
        public Task<QuoteInfo?> GetQuoteAsync(string s, CancellationToken ct = default) => inner.GetQuoteAsync(s, ct);
        public Task<IReadOnlyList<PositionInfo>> GetPositionsAsync(string a, CancellationToken ct = default) => inner.GetPositionsAsync(a, ct);
        public Task<IReadOnlyList<OrderInfo>> GetOrdersAsync(string a, bool inactive, DateTimeOffset? since, CancellationToken ct = default) => inner.GetOrdersAsync(a, inactive, since, ct);
        public Task<IReadOnlyList<ExecutionInfo>> GetExecutionsAsync(string a, DateTimeOffset? since, CancellationToken ct = default) => inner.GetExecutionsAsync(a, since, ct);
        public Task<OrderInfo> ModifyOrderAsync(ModifyOrderCommand c, CancellationToken ct = default) => inner.ModifyOrderAsync(c, ct);
        public Task CancelOrderAsync(string id, CancellationToken ct = default) => inner.CancelOrderAsync(id, ct);
        public Task<IReadOnlyList<string>> CancelAllOrdersAsync(string a, CancellationToken ct = default) => inner.CancelAllOrdersAsync(a, ct);
        public Task<OrderInfo?> ClosePositionAsync(string a, string s, string coid, CancellationToken ct = default) => inner.ClosePositionAsync(a, s, coid, ct);

        public event Action<HealthState>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
        public event Action<QuoteInfo>? QuoteChanged { add => inner.QuoteChanged += value; remove => inner.QuoteChanged -= value; }
        public event Action<OrderInfo>? OrderChanged { add => inner.OrderChanged += value; remove => inner.OrderChanged -= value; }
        public event Action<ExecutionInfo>? ExecutionReceived { add => inner.ExecutionReceived += value; remove => inner.ExecutionReceived -= value; }
        public event Action<PositionInfo>? PositionChanged { add => inner.PositionChanged += value; remove => inner.PositionChanged -= value; }
        public event Action<AccountInfo>? AccountChanged { add => inner.AccountChanged += value; remove => inner.AccountChanged -= value; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
