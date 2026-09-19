using TradeAgent.ConnectorSdk;
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
/// U-approve-gates — AN APPROVAL RUNS EVERY POSITION GATE A PLACEMENT RUNS, ON ONE READING.
///
/// <para><b>What was wrong before this.</b> <c>PlaceAsync</c> ran four gates on its single position
/// reading inside the dispatch gate — the open-position cap, the unresolved-reducer refusal, the loss
/// budgets, the allocation ceiling — and <c>ApproveAsync</c> re-ran the first and the third. The two
/// it dropped are the capital gate and the reconciliation refusal, two of the five
/// <c>docs/COUNCIL.md</c>:14-15 says EVERY order passes. So a parked order was approved onto a ceiling
/// the owner had since withdrawn, and a parked reduce was approved over an order this gateway cannot
/// account for — the doubling <c>CLOSE_UNRESOLVED</c> exists to refuse. <c>record.AllocationId</c> was
/// never recomputed either, so the sent order was attributed to an allocation that no longer stood.</para>
///
/// <para>The first two tests are REVIEW 2026-09-16's probes <c>C3a</c> and <c>C3b</c> (finding 4),
/// brought over and given assertions: the probes only logged. Each asks what a FRESH order carrying
/// the same intent is answered at that same instant, and requires the approval to be answered the
/// same way — the finding is the difference between those two answers, so that is what is pinned.</para>
///
/// <para>Everything is measured at the WIRE, over <see cref="RecordingConnector"/>: a refusal code
/// says nothing about whether a frame went out. The connector is the simulator; no venue is reached
/// and no real money is involved.</para>
/// </summary>
public class ApprovalPositionGateTests(ITestOutputHelper log)
{
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        DateTimeOffset _now = at;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A gateway in LIVE_CONFIRM with real money on, so an agent's order parks.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Confirming(TestClock? clock = null)
    {
        var db = TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()));
        var gw = new TradingGateway(db, conn, new HealthRegistry(),
            clock is null ? new GatewayOptions() : new GatewayOptions { Clock = clock });
        gw.Update(s =>
        {
            s.Mode = TradingMode.LIVE_CONFIRM;
            s.LiveActivated = true;
            s.SelectedAccountId = conn.Broker.AccountId;
            s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments];
            s.Risk.MaxOrderQuantity = 100m;
            s.Risk.MaxNotionalPerOrder = 0m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 100;
            s.Risk.MaxDailyLoss = 1_000_000m;
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    static PlaceIntent Order(OrderSide side, decimal qty, string? version = null) =>
        new("ES", side, OrderType.Market, qty, null, null, TimeInForce.Day, null) { StrategyVersionId = version };

    /// <summary>The code a placement is answered with now, or SENT because it was not refused.</summary>
    static async Task<string> AnswerTo(Func<Task> call)
    {
        try { await call(); return "SENT"; }
        catch (GatewayDeniedException ex) { return ex.Code.ToString(); }
    }

    /// <summary>
    /// C3a, WITH ASSERTIONS — A PARKED ORDER APPROVED AFTER ITS CAPITAL WAS WITHDRAWN.
    ///
    /// <para>A version really standing promoted in this database, allocated five, an order for five
    /// parked in <c>LIVE_CONFIRM</c>, and then the owner withdraws the capital by recording a fresh
    /// allocation of zero from a later instant — the only way this ledger lowers a ceiling, since it
    /// has no update and no delete.</para>
    /// </summary>
    [Fact]
    public async Task A_parked_order_approved_after_its_capital_was_withdrawn_is_refused_as_a_fresh_one_is()
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await Confirming(clock);

        var version = PromotedVersion(db, clock.GetUtcNow());
        Assert.True(gw.Allocate(version, 5m, null, "the owner allocated five").Ok);
        var intent = Order(OrderSide.Buy, 5m, version);

        var parked = await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "c3a-park", intent));
        log.WriteLine($"parked                    : {parked}");
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(), parked);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(gw.Allocate(version, 0m, null, "the owner withdrew it").Ok);
        Assert.Equal(0m, gw.Allocations.StandingForLive(version, clock.GetUtcNow())!.Allocation.MaxQuantity);

        var fresh = await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "c3a-fresh", intent));
        log.WriteLine($"a FRESH order now         : {fresh}");
        Assert.Equal(ErrorCode.ALLOCATION_EXCEEDED.ToString(), fresh);

        var before = conn.Places;
        var approved = await AnswerTo(() => gw.ApproveAsync("c3a-park"));
        log.WriteLine($"the PARKED order, approved: {approved}");
        log.WriteLine($"orders that reached the wire: {conn.Places - before}");

        // THE FINDING IS THE DIFFERENCE BETWEEN THOSE TWO ANSWERS, so it is the difference that is
        // pinned: the withdrawn ceiling refuses the approval exactly as it refuses the fresh order.
        Assert.Equal(fresh, approved);
        Assert.Equal(before, conn.Places);
        // A refusal leaves the record parked for a person to decline deliberately.
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("c3a-park")!.State);
    }

    /// <summary>
    /// C3b, WITH ASSERTIONS — A PARKED REDUCE APPROVED OVER AN ORDER THIS GATEWAY CANNOT ACCOUNT FOR.
    ///
    /// <para>A long 4, an AI reduce of 2 parked while nothing is unresolved, then the owner's own sell
    /// 1 whose acknowledgement is lost after the broker accepted it, then the owner confirming on the
    /// card that they cannot say what happened to it — which clears the flag, lets trading resume, and
    /// leaves that order still able to fill. The blocker has to be a record that is UNKNOWN and NOT
    /// flagged: a flagged one pauses everything on its own and would refuse the approval for a
    /// different reason, proving nothing about this gate.</para>
    /// </summary>
    [Fact]
    public async Task A_parked_reduce_approved_over_an_unresolved_reducer_is_refused_as_a_fresh_one_is()
    {
        var (gw, conn, db) = await Confirming();
        var requests = new ExecutionRequestStore(db);

        await gw.PlaceAsync(AgentContext.Operator, "c3b-long", Order(OrderSide.Buy, 4m));

        var reduce = Order(OrderSide.Sell, 2m);
        var parked = await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "c3b-park", reduce));
        log.WriteLine($"parked                    : {parked}");
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(), parked);

        conn.Faults.DropAfterBrokerAccept = 1;
        try { await gw.PlaceAsync(AgentContext.Operator, "c3b-lost", Order(OrderSide.Sell, 1m)); }
        catch (Exception ex) { log.WriteLine($"the lost order            : {ex.GetType().Name}"); }
        Assert.Equal(ExecutionState.UNKNOWN, requests.Get("c3b-lost")!.State);

        gw.ForceResolve("c3b-lost", ExecutionState.UNKNOWN, "I checked ATAS and I cannot tell");
        await gw.RefreshHealthAsync();
        var blocker = requests.Get("c3b-lost")!;
        log.WriteLine($"after the owner's card    : state={blocker.State} flagged={blocker.NeedsReconciliation} "
                      + $"unconfirmed_work={gw.HasUnconfirmedWork()}");
        Assert.Equal(ExecutionState.UNKNOWN, blocker.State);
        Assert.False(blocker.NeedsReconciliation);
        Assert.False(gw.HasUnconfirmedWork());

        var fresh = await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "c3b-fresh", reduce));
        log.WriteLine($"a FRESH reduce now        : {fresh}");
        Assert.Equal(ErrorCode.CLOSE_UNRESOLVED.ToString(), fresh);

        var before = conn.Places;
        var held = conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m;
        var approved = await AnswerTo(() => gw.ApproveAsync("c3b-park"));
        var after = conn.Broker.Positions.FirstOrDefault(p => p.Symbol == "ES")?.Quantity ?? 0m;
        log.WriteLine($"the PARKED reduce approved: {approved}");
        log.WriteLine($"orders that reached the wire: {conn.Places - before}");
        log.WriteLine($"position at the broker    : ES {after} (it was ES {held})");

        Assert.Equal(fresh, approved);
        Assert.Equal(before, conn.Places);
        // The position is sized from a reading the lost sell 1 is already moving; nothing may be sent
        // on top of it, so the book is exactly as the approval found it.
        Assert.Equal(held, after);
        Assert.Equal(ExecutionState.AWAITING_APPROVAL, gw.GetRequest("c3b-park")!.State);
    }

    /// <summary>
    /// WHAT THE APPROVED ORDER IS ATTRIBUTED TO: the allocation that authorised it AT THE PRESS, not
    /// the one it parked under.
    ///
    /// <para>The capital is not withdrawn here — it is REPLACED by an allocation of the same size from
    /// a later instant, which is a different <c>effective_from</c> and therefore a different row id.
    /// The order goes out, as it must, and <c>docs/COUNCIL.md</c>:210-211's "which allocation caused an
    /// operation" has to name the row that stood when the frame left, because that is the decision the
    /// money went out under. It also proves the whole gate sequence still LETS a good approval through:
    /// a gate that refuses everything would pass the two tests above and be a different bug.</para>
    /// </summary>
    [Fact]
    public async Task An_approval_is_attributed_to_the_allocation_that_authorised_it_not_the_one_it_parked_under()
    {
        var clock = new TestClock(Noon);
        var (gw, conn, db) = await Confirming(clock);

        var version = PromotedVersion(db, clock.GetUtcNow());
        Assert.True(gw.Allocate(version, 5m, null, "the owner allocated five").Ok);
        var parkedUnder = gw.Allocations.StandingForLive(version, clock.GetUtcNow())!.Allocation.Id;

        var intent = Order(OrderSide.Buy, 5m, version);
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(),
            await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "attrib-park", intent)));
        Assert.Equal(parkedUnder, gw.GetRequest("attrib-park")!.AllocationId);

        // The owner re-states the same ceiling a minute later: a fresh row, a different id, and the
        // one that stands when the press happens.
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(gw.Allocate(version, 5m, null, "the owner restated it").Ok);
        var standing = gw.Allocations.StandingForLive(version, clock.GetUtcNow())!.Allocation.Id;
        log.WriteLine($"parked under              : {parkedUnder}");
        log.WriteLine($"standing at the press     : {standing}");
        Assert.NotEqual(parkedUnder, standing);

        var before = conn.Places;
        Assert.Equal("SENT", await AnswerTo(() => gw.ApproveAsync("attrib-park")));
        Assert.Equal(before + 1, conn.Places);

        var row = new ExecutionRequestStore(db).Get("attrib-park")!;
        log.WriteLine($"the record's allocation_id: {row.AllocationId}");
        Assert.Equal(standing, row.AllocationId);
    }

    /// <summary>
    /// THE CONTROL FOR THE REDUCER REFUSAL: with nothing unresolved, the same parked reduce approves
    /// and reaches the wire. The gate refuses a doubling, not a reduce.
    /// </summary>
    [Fact]
    public async Task A_parked_reduce_with_nothing_unresolved_still_approves_and_reaches_the_wire()
    {
        var (gw, conn, _) = await Confirming();

        await gw.PlaceAsync(AgentContext.Operator, "ctl-long", Order(OrderSide.Buy, 4m));
        Assert.Equal(ErrorCode.APPROVAL_REQUIRED.ToString(),
            await AnswerTo(() => gw.PlaceAsync(new AgentContext("a"), "ctl-park", Order(OrderSide.Sell, 2m))));

        var before = conn.Places;
        Assert.Equal("SENT", await AnswerTo(() => gw.ApproveAsync("ctl-park")));
        log.WriteLine($"orders that reached the wire: {conn.Places - before}");
        Assert.Equal(before + 1, conn.Places);
    }

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// A VERSION THAT REALLY STANDS PROMOTED in this database — <c>AllocationGateTests</c>' fixture,
    /// with the instant passed in so a moved clock and the ledger agree. A faked id would be refused by
    /// the allocation ledger a step earlier and would prove nothing about the gateway.
    /// </summary>
    static string PromotedVersion(Database db, DateTimeOffset at)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"approve-gates-{Guid.NewGuid():n}.csv");
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
}
