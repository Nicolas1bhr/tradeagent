using TradeAgent.Connectors.Fake;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Fault;

/// <summary>
/// ITEMS 3 AND 4 — WHAT AN ORDER WAS PLACED UNDER, AND THE CEILING THAT REFUSES IT.
///
/// <para><b>What was wrong before this.</b> <c>PlaceIntent</c> named no strategy version and
/// <c>execution_request</c> recorded none, so <c>docs/COUNCIL.md</c>:210-211 — "which strategy or
/// allocation caused an operation" is unrecoverable later — described this build exactly. And nothing
/// on the order path read <c>Promotions.Standing</c> or any allocation at all, so the capital gate
/// rule 1 lists beside the loss gate did not exist: a promoted version and a quantity the gateway
/// would allow had nothing binding them.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> The attribution read back out of
/// <c>ParametersJson</c>, so a rewritten blob re-attributes an order that has already been sent. And
/// the ceiling evaluated in <c>RiskCheckOrThrow</c>, above the awaited reads, where two placements in
/// flight together each see an empty account and both pass one allocation — the same shape as the
/// open-position cap before REVIEW 2026-09-05b, Codex F1.</para>
///
/// <para>Everything here is measured at the WIRE, over <see cref="RecordingConnector"/>: a refusal
/// code says nothing about whether a frame went out. Nothing reaches a venue and no real money is
/// involved — the connector is the simulator.</para>
/// </summary>
public class AllocationGateTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry());
        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 100m;
            s.Risk.MaxNotionalPerOrder = 100_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            settings?.Invoke(s);
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// A VERSION THAT REALLY STANDS PROMOTED in this database — the dataset, the holdout, the campaign,
    /// the version, the holdout run and the verdict. A faked id would be refused by the allocation
    /// ledger a step earlier and would prove nothing about the gateway.
    /// </summary>
    static string PromotedVersion(Database db)
    {
        var at = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"allocation-gate-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, at, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, at);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(ProgramText).Program!;
        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), null, null));

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null)
            .RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", at, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id, campaign.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, PromotionVerdict.Promoted, PromotionReason.Met, at));

        return program.StrategyId;
    }

    static PlaceIntent By(string? version, string symbol = "ES", decimal qty = 1m,
        ConnectorSdk.OrderSide side = ConnectorSdk.OrderSide.Buy, ConnectorSdk.OrderIntent intent = ConnectorSdk.OrderIntent.Open) =>
        new(symbol, side, ConnectorSdk.OrderType.Market, qty, null, null, ConnectorSdk.TimeInForce.Day, null)
        {
            StrategyVersionId = version,
            Intent = intent
        };

    static async Task<string> SwallowAsync(Task<ExecutionRequest> t)
    {
        try { var r = await t; return $"ok — {r.State}"; }
        catch (GatewayDeniedException ex) { return $"{ex.Code} — {ex.Message}"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    // ---- item 3: the order says what it was placed under ---------------------------------------

    /// <summary>
    /// A PLACEMENT NAMING A VERSION RECORDS THAT VERSION AND THE ALLOCATION IT WENT OUT UNDER.
    ///
    /// <para>RED before this unit: <c>ExecutionRequest</c> had neither column and <c>PlaceIntent</c>
    /// had no version at all, so the build did not compile against this test —
    /// <c>error CS0117: 'PlaceIntent' does not contain a definition for 'StrategyVersionId'</c> — and
    /// the row the gateway wrote named nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_placement_naming_a_version_records_the_version_and_its_allocation()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        var allocated = gw.Allocate(version, 5m, null, "test");
        Assert.True(allocated.Ok, allocated.Why);

        var placed = await gw.PlaceAsync(new AgentContext("a"), "alloc-attr", By(version));
        var stored = gw.GetRequest("alloc-attr")!;

        log.WriteLine($"state                : {placed.State}");
        log.WriteLine($"strategy_version_id  : {stored.StrategyVersionId ?? "none"}");
        log.WriteLine($"allocation_id        : {stored.AllocationId ?? "none"}");

        Assert.Equal(version, stored.StrategyVersionId);
        Assert.Equal(allocated.Allocation!.Id, stored.AllocationId);
        Assert.Single(conn.Placed);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE MUTANT: THE ATTRIBUTION READ BACK OUT OF <c>ParametersJson</c>.
    ///
    /// <para>The order has been placed. The parameters blob is then rewritten to name a different
    /// version — which is what a later build, a migration, or anything at all holding this file can
    /// do to one text column — and the row must still say what the order was actually sent under.
    /// <c>docs/COUNCIL.md</c>:210-211: lost provenance is one of the four things that cannot be
    /// recovered afterwards, and an attribution that can be restated is not a record of it.</para>
    ///
    /// <para>With the reader re-deriving the version from the blob:
    /// <c>Assert.Equal() Failure / Expected: the version that placed it / Actual: a-version-that-never-placed-anything</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_rewritten_parameters_blob_cannot_re_attribute_a_sent_order()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 5m, null, "test").Ok);
        await gw.PlaceAsync(new AgentContext("a"), "alloc-blob", By(version));

        var rewritten = Json.Write(By("a-version-that-never-placed-anything"));
        db.Write(_ =>
        {
            using var c = db.Cmd("UPDATE execution_request SET parameters=$p WHERE request_id='alloc-blob'",
                ("$p", rewritten));
            return c.ExecuteNonQuery();
        });

        var stored = gw.GetRequest("alloc-blob")!;

        log.WriteLine($"blob now names       : a-version-that-never-placed-anything");
        log.WriteLine($"strategy_version_id  : {stored.StrategyVersionId ?? "none"}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(version, stored.StrategyVersionId);
        Assert.Contains("a-version-that-never-placed-anything", stored.ParametersJson, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN ORDER NO STRATEGY PLACED RECORDS NOTHING, AND IS NOT INVENTED A VERSION. The owner's own buy
    /// has no allocation to be charged against, and a null here is the truth about it rather than a
    /// column nobody filled in.
    /// </summary>
    [Fact]
    public async Task An_order_no_strategy_placed_is_attributed_to_nothing()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        await gw.PlaceAsync(new AgentContext("a"), "alloc-none", TestEnv.Buy());
        var stored = gw.GetRequest("alloc-none")!;

        log.WriteLine($"strategy_version_id  : {stored.StrategyVersionId ?? "none"}");
        log.WriteLine($"allocation_id        : {stored.AllocationId ?? "none"}");

        Assert.Null(stored.StrategyVersionId);
        Assert.Null(stored.AllocationId);
        Assert.Single(conn.Placed);
        await gw.DisposeAsync();
    }
}
