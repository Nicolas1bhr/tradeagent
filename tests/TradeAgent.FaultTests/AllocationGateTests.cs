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

    /// <summary>
    /// Lets nobody past <paramref name="on"/> until <paramref name="n"/> callers are standing at it —
    /// <c>RiskGateTests</c>' barrier, and the only honest way to state "these two placements were in
    /// flight together". It is at the QUOTE because that is the last call on the placement path both
    /// callers make OUTSIDE the dispatch gate: hold them anywhere inside it and the second never
    /// arrives, because the first is holding the gate (<see cref="RecordingConnector.Quote"/>).
    /// </summary>
    static Func<RecordingConnector.HeldCall, Task> Barrier(int n, RecordingConnector.HeldCall on)
    {
        var arrived = 0;
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return async kind =>
        {
            if (kind != on) return;
            if (Interlocked.Increment(ref arrived) >= n) open.TrySetResult();
            await open.Task;
        };
    }

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

    // ---- item 4: the ceiling, inside the dispatch gate -----------------------------------------

    /// <summary>
    /// AN ORDER TEN TIMES THE ALLOCATION IS REFUSED AND NOTHING IS SENT.
    ///
    /// <para>RED before this unit: <c>ok — FILLED</c>, one order at the broker, one open position of
    /// ten against an allocation of one. Every per-order limit on this gateway passed it — the
    /// quantity cap is 100 here and the value cap is a hundred million — because no limit anywhere
    /// knew what a promoted version had been given.</para>
    ///
    /// <para>The refusal is DEFINITE: nothing left the process, so the record stays
    /// <see cref="ExecutionState.CREATED"/> and is not flagged for reconciliation. Actually it stays
    /// unwritten — the gate refuses BEFORE <c>TryCreate</c>, so there is no row at all, which is the
    /// shape the three gates beside it have.</para>
    /// </summary>
    [Fact]
    public async Task An_order_ten_times_its_allocation_is_refused_and_nothing_is_sent()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 1m, null, "test").Ok);

        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-over", By(version, qty: 10m)));

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"record               : {gw.GetRequest("alloc-over")?.State.ToString() ?? "none"}");
        log.WriteLine($"connector place calls: {conn.Places}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.StartsWith(ErrorCode.ALLOCATION_EXCEEDED.ToString(), outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Placed);
        Assert.Empty(conn.Broker.Orders);
        Assert.Null(gw.GetRequest("alloc-over"));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE MUTANT: THE CEILING EVALUATED ABOVE THE AWAITED READS.
    ///
    /// <para>Two placements of one contract each, against an allocation of one, barriered at the quote
    /// so that neither is past the risk check while the other is still before it. The ceiling belongs
    /// inside the dispatch gate with the position reading, where the second caller sees what the first
    /// one did; decided out there, each sees the same empty account, each passes a ceiling of one and
    /// both go out — <c>connector place calls: 2</c>, two orders at the broker, a version holding
    /// twice what the owner gave it. It is the shape of REVIEW 2026-09-05b, Codex F1.</para>
    /// </summary>
    [Fact]
    public async Task Two_placements_in_flight_together_cannot_both_pass_one_allocation()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 1m, null, "test").Ok);
        conn.Seam = Barrier(2, RecordingConnector.HeldCall.Quote);

        var first = gw.PlaceAsync(new AgentContext("a"), "alloc-race-1", By(version));
        var second = gw.PlaceAsync(new AgentContext("a"), "alloc-race-2", By(version));
        var outcomes = await Task.WhenAll(SwallowAsync(first), SwallowAsync(second));

        log.WriteLine($"allocation           : 1");
        log.WriteLine($"first                : {outcomes[0]}");
        log.WriteLine($"second               : {outcomes[1]}");
        log.WriteLine($"connector place calls: {conn.Places}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.Equal(1, conn.Places);
        Assert.Single(conn.Broker.Orders);
        Assert.Contains(outcomes, o =>
            o.StartsWith(ErrorCode.ALLOCATION_EXCEEDED.ToString(), StringComparison.Ordinal));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A VERSION WITH NO CAPITAL BEHIND IT PLACES NOTHING. It is promoted — the referee said yes — and
    /// that is not permission to trade the owner's money: <c>docs/COUNCIL.md</c>:135-136 puts forward
    /// evidence before capital, and the allocation is the owner's separate act.
    /// </summary>
    [Fact]
    public async Task An_order_naming_a_version_with_no_allocation_is_refused()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-nil", By(version)));

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.StartsWith(ErrorCode.ALLOCATION_NONE.ToString(), outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Placed);
        Assert.Null(gw.GetRequest("alloc-nil"));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AN ALLOCATION WHOSE PROMOTION HAS BEEN WITHDRAWN AUTHORISES NOTHING, AND THE ROW IS STILL THERE.
    ///
    /// <para>The standing is computed at the moment the order arrives, never copied onto the allocation
    /// when it was written: the dataset the verdict rested on is rejected after the capital was
    /// allocated, and the very next order is refused. <c>docs/COUNCIL.md</c>:35 — a changed assumption
    /// invalidates the evidence that rested on it.</para>
    /// </summary>
    [Fact]
    public async Task An_allocation_whose_promotion_no_longer_stands_authorises_nothing()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 5m, null, "test").Ok);
        await gw.PlaceAsync(new AgentContext("a"), "alloc-live", By(version));
        Assert.Single(conn.Placed);

        new DatasetStore(db).Reject(1, "a raw archive file changed under it");
        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-dead", By(version)));

        log.WriteLine($"outcome after reject : {outcome}");
        log.WriteLine($"allocation row still : {gw.Allocations.For(version).Count}");
        log.WriteLine($"connector place calls: {conn.Places}");

        Assert.StartsWith(ErrorCode.ALLOCATION_NONE.ToString(), outcome, StringComparison.Ordinal);
        Assert.Single(gw.Allocations.For(version));
        Assert.Equal(1, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A CLOSE ALWAYS PASSES THE CEILING, and is still attributed to the version that placed it. A
    /// ceiling that stopped a position being flattened would be a trap, and the day it fired is the day
    /// the owner most needs out — the loss budget's reason, applied to capital.
    /// </summary>
    [Fact]
    public async Task A_close_passes_the_ceiling_and_is_still_attributed()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        var allocated = gw.Allocate(version, 1m, null, "test");
        Assert.True(allocated.Ok, allocated.Why);
        await gw.PlaceAsync(new AgentContext("a"), "alloc-open", By(version));

        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-close",
            By(version, qty: 1m, side: ConnectorSdk.OrderSide.Sell, intent: ConnectorSdk.OrderIntent.Close)));
        var stored = gw.GetRequest("alloc-close")!;

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"allocation_id        : {stored.AllocationId ?? "none"}");
        log.WriteLine($"connector place calls: {conn.Places}");

        Assert.StartsWith("ok", outcome, StringComparison.Ordinal);
        Assert.Equal(allocated.Allocation!.Id, stored.AllocationId);
        Assert.Equal(2, conn.Places);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE VALUE CEILING, WHERE THE OWNER SET ONE, against the same reference price the notional cap
    /// multiplies. A ceiling of one unit of the account's currency refuses an order of any size at any
    /// price this simulator quotes, which is what makes the assertion independent of the quote.
    /// </summary>
    [Fact]
    public async Task An_order_over_the_allocations_value_ceiling_is_refused()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 5m, 1m, "test").Ok);

        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-value", By(version)));

        log.WriteLine($"outcome              : {outcome}");
        log.WriteLine($"orders at the broker : {conn.Broker.Orders.Count}");

        Assert.StartsWith(ErrorCode.ALLOCATION_EXCEEDED.ToString(), outcome, StringComparison.Ordinal);
        Assert.Empty(conn.Placed);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND A GENEROUS VALUE CEILING LETS THE SAME ORDER THROUGH, which is what makes the one above a
    /// ceiling rather than a refusal of everything.
    /// </summary>
    [Fact]
    public async Task An_order_inside_both_ceilings_is_sent()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var version = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 5m, 50_000_000m, "test").Ok);

        var outcome = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "alloc-inside", By(version)));

        log.WriteLine($"outcome              : {outcome}");

        Assert.StartsWith("ok", outcome, StringComparison.Ordinal);
        Assert.Single(conn.Placed);
        await gw.DisposeAsync();
    }
}
