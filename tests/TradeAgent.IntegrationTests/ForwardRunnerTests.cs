using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE FROZEN VERSION RUN FORWARD ON PAPER — THE MONEY PATH, END TO END, IN PAPER MODE.
///
/// <para>This is the first production producer of a <c>PlaceIntent</c> from a
/// <see cref="StrategyIntent"/>, so nothing here is a fixture: the envelope is granted through the
/// gateway's own two-witness card, the allocation is written by <c>AllocatePaperDue</c>, the
/// deployment by <c>StartPaperDeploymentsDue</c>, the bars go into <c>forward_bar</c> through
/// <see cref="ForwardBarStore.Append"/> and reach the connector through the SHIPPED adapter
/// (<see cref="ForwardBarSource"/>, the binding <c>Connectors.Create</c> makes), and every order
/// goes out through <c>TradingGateway.PlaceAsync</c> under <c>AgentContext.Deployment</c> and every
/// gate it has. No venue is reached: the paper connector's assembly has none.</para>
///
/// <para><b>The two mutants these tests exist to catch.</b> (i) the runner advancing past a bar
/// whose operations have not resolved — which sends a second entry over an order that is already at
/// the wire; and (ii) the maximum hold checked AFTER the evaluator is stepped — which lets the
/// program decide from a position the app's own protection had already said must not survive the
/// bar.</para>
/// </summary>
public class ForwardRunnerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A clock this suite owns and MOVES, one bar at a time.</summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>The owner's venues.json, standing in one line: BTCUSDT on Binance spot, VERIFIED.</summary>
    static VenueCatalogRead Catalogue() => new(
    [
        new VenueEntry
        {
            Id = VenueCatalog.BinanceSpot,
            DisplayName = "Binance spot",
            CalendarKind = CalendarKind.Continuous,
            Source = "declared by this test, standing in for the account owner's venues.json",
            Verified = true,
            Instruments =
            [
                new VenueInstrumentEntry
                {
                    Symbol = "BTCUSDT", TickSize = 0.01m, QuantityIncrement = 0.001m,
                    Source = "declared by this test", Verified = true
                }
            ]
        }
    ], null);

    const string Head =
        "instrument BTCUSDT\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\n"
        + "size fixed 1\n";

    static string ProgramText(string declarations = "", string entry = "entry when close > 100") =>
        Head + declarations + "exit when close < 90\n" + entry + "\n";

    // ---------------------------------------------------------------- the harness

    /// <summary>
    /// ONE INSTALLATION WITH A RUN IN IT: a paper account, a granted envelope, a judged version, the
    /// app's own allocation and the app's own deployment — every one of them written by the call the
    /// product uses, never by a row.
    /// </summary>
    sealed class Rig : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required PaperConnector Conn { get; init; }
        public required TradingGateway Gw { get; init; }
        public required ForwardRuns Runner { get; init; }
        public required TestClock Clock { get; init; }
        public required ForwardBarStore Bars { get; init; }
        public required ForwardBarSource Source { get; init; }
        public required string BookFile { get; init; }
        public required string Symbol { get; init; }

        public StrategyDeploymentRow Deployment =>
            Gw.Deployments.All().FirstOrDefault() ?? throw new InvalidOperationException("no deployment");

        /// <summary>
        /// ONE CLOSED MINUTE, THE WAY THE COLLECTOR STORES ONE: a fetch attempt, the bar, and then the
        /// announcement. The clock moves to that bar's close, because that is when it existed.
        /// </summary>
        public void Bar(int minute, decimal open, decimal high, decimal low, decimal close)
        {
            var openTime = Origin.AddMinutes(minute);
            var closeTime = openTime + ForwardBars.BarLength;
            Clock.At = closeTime;

            var append = Bars.Append(new ForwardFetchAttempt
            {
                Source = ForwardBars.Source,
                Symbol = Symbol,
                Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                RequestedAt = closeTime,
                ReceivedAt = closeTime.AddSeconds(1),
                HttpStatus = 200
            }, [new ForwardBars.Kline(openTime, open, high, low, close, 10m, closeTime)]);

            if (append.Stored != 1)
                throw new InvalidOperationException($"the bar at {openTime:u} was not stored");

            Clock.At = closeTime.AddSeconds(1);
            Source.Announce(Symbol, openTime);
        }

        public DateTimeOffset Origin { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Conn.DisposeAsync();
        }
    }

    static async Task<Rig> ReadyAsync(string program, string? bookFile = null, Database? db = null,
        DateTimeOffset? origin = null)
    {
        db ??= TestEnv.NewDb();

        // The quote the gateway sizes against is the last closed bar's close, and `QuoteInfo.IsStale`
        // measures it against the REAL clock while this suite's bars are placed on an injected one.
        // The age that matters to this unit is the DECISION's, which is measured on the gateway's own
        // clock — see (d), which is entirely about that gate.
        var start = origin ?? new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(start);

        var bars = new ForwardBarStore(db);
        var source = new ForwardBarSource(bars);

        var book = bookFile ?? Path.Combine(TestEnv.Home, $"paper-run-{Guid.NewGuid():n}.db");
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => clock.At,
            BookFile = book,
            Catalogue = Catalogue()
        });

        var gw = new TradingGateway(db, conn, new HealthRegistry(),
            new GatewayOptions { Clock = clock, MaxQuoteAge = TimeSpan.FromDays(3650) });

        gw.Update(s =>
        {
            s.Mode = TradingMode.PAPER;
            s.SelectedAccountId = PaperConnector.TheAccount;
            s.Risk.InstrumentAllowlist = ["BTCUSDT"];
            s.Risk.MaxOrderQuantity = 10m;
            s.Risk.MaxNotionalPerOrder = 10_000_000m;
            s.Risk.MaxOpenPositions = 10;
            s.Risk.MaxOrdersPerMinute = 1000;
        });

        // AFTER the gateway, which syncs the SHIPPED catalogue when it opens — and the shipped
        // BTCUSDT row is unverified on purpose, so a run over it would place nothing at all.
        new VenueStore(db).Sync(Catalogue());

        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        var rig = new Rig
        {
            Db = db, Conn = conn, Gw = gw, Clock = clock, Bars = bars, Source = source,
            BookFile = book, Symbol = "BTCUSDT", Origin = start,
            Runner = new ForwardRuns(gw, db, () => clock.At)
        };

        if (db.GetKv("rig-seeded") is null)
        {
            var granted = await gw.GrantPaperEnvelopeAsync(
                "BTCUSDT", 5m, 5_000_000m, start.AddDays(30), start);
            Assert.True(granted.Ok, granted.Why);

            Judged(db, program, start);
            Assert.Equal(1, gw.AllocatePaperDue(start));
            Assert.Equal(1, gw.StartPaperDeploymentsDue(start));
            db.SetKv("rig-seeded", "yes");
        }

        return rig;
    }

    /// <summary>
    /// A VERSION THAT REALLY CARRIES A VERDICT IN THIS DATABASE — the dataset, the holdout, the
    /// campaign, the version, the holdout run and the promotion row. A faked id would be refused by
    /// the ledger a step earlier and would prove nothing about anything above it.
    /// </summary>
    static string Judged(Database db, string text, DateTimeOffset at)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"forward-runner-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, at, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT 1m forward", set, 10, 3, at);
        Assert.True(campaign.Ok, campaign.Why);

        var parsed = StrategyParser.Parse(text);
        Assert.True(parsed.Ok, parsed.Why);
        var program = parsed.Program!;

        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, program.WarmUpBars,
            Cutoff.AddDays(-1), null, null)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0m, 0m, 0.001m, 1_000_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null)
            .RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", at, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id,
            CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1), StrategyStore.InterpreterBuild,
            set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion, runId,
            PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory, at));

        return program.StrategyId;
    }

    static async Task<IReadOnlyList<OrderInfo>> Wire(Rig rig) =>
        await rig.Conn.GetOrdersAsync(PaperConnector.TheAccount, includeInactive: true, since: null);

    static async Task<decimal> Position(Rig rig) =>
        (await rig.Conn.GetPositionsAsync(PaperConnector.TheAccount))
        .FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m;

    static void Show(ITestOutputHelper log, Rig rig)
    {
        foreach (var op in rig.Gw.Deployments.OpsOf(rig.Deployment.Id))
            log.WriteLine($"op {op.RequestId} {op.Kind} {op.State} bar {op.BarOpenTime:HH:mm} — {op.Answer}");
    }

    // ---------------------------------------------------------------- (a)

    [Fact]
    public async Task A_closed_bar_drives_an_entry_through_PlaceAsync_and_the_paper_connector_fills_it_at_the_next_open()
    {
        await using var rig = await ReadyAsync(ProgramText());

        rig.Bar(1, 99m, 101m, 98m, 101m);      // entry when close > 100 — fires here
        await rig.Runner.AdvanceAsync();

        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        Show(log, rig);
        var entry = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Entry);

        // THE WIRE HOLDS EXACTLY ONE ORDER, and it is the one this operation is joined to by the
        // request id — which is on the broker order as the client order id, safety rule 1.
        var order = Assert.Single(await Wire(rig));
        Assert.Equal(TradingGateway.ClientOrderIdFor(entry.RequestId), order.ClientOrderId);
        Assert.Equal(OrderSide.Buy, order.Side);
        Assert.Equal(OrderType.Market, order.Type);

        // AND THE REQUEST ROW NAMES THE VERSION, THE ALLOCATION AND THE DEPLOYMENT.
        var request = rig.Gw.Requests.Get(entry.RequestId)!;
        log.WriteLine($"{request.RequestId} v={request.StrategyVersionId} a={request.AllocationId} "
                      + $"s={request.AgentSessionId} {request.ParametersJson}");
        Assert.Equal(rig.Deployment.VersionId, request.StrategyVersionId);
        Assert.Equal(rig.Deployment.AllocationId, request.AllocationId);
        Assert.Equal($"deployment:{rig.Deployment.Id}", request.AgentSessionId);
        Assert.Contains($"deployment:{rig.Deployment.Id} rule ", request.ParametersJson);

        // NOTHING FILLED YET: what a market order fills at is the OPEN of the first bar that opens
        // after it was sent — and the minute in progress when the signal was computed had already
        // opened. Bar 2 is that minute; bar 3 is the first open this order could have had.
        Assert.Equal(0m, await Position(rig));

        rig.Bar(2, 102m, 103m, 101m, 102m);
        await rig.Gw.RefreshHealthAsync();
        Assert.Equal(0m, await Position(rig));

        rig.Bar(3, 104m, 105m, 103m, 104m);
        await rig.Gw.RefreshHealthAsync();

        Assert.Equal(1m, await Position(rig));
        var fill = Assert.Single(await rig.Conn.GetExecutionsAsync(PaperConnector.TheAccount, null));
        log.WriteLine($"filled {fill.Quantity} at {fill.Price}");
        Assert.Equal(104m, fill.Price);
        Assert.Single(await Wire(rig));
    }

    // ---------------------------------------------------------------- (b)

    [Fact]
    public async Task The_stop_and_target_rest_after_the_entry_fills_and_the_loser_is_cancelled_when_the_other_fills()
    {
        await using var rig = await ReadyAsync(
            ProgramText("stop percent 5\ntarget percent 1\n", "entry when close > 99"));

        rig.Bar(1, 99m, 100m, 98m, 100m);             // signalled from a close of 100: stop 95, target 101
        await rig.Runner.AdvanceAsync();

        rig.Bar(2, 100m, 100.5m, 99.5m, 100m);        // the minute already in progress: no open to fill at
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        rig.Bar(3, 100m, 100.6m, 99.6m, 100.2m);      // the entry fills at THIS open, 100
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Assert.Equal(1m, await Position(rig));
        await rig.Runner.AdvanceAsync();              // the pass that places the protection

        Show(log, rig);
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        var stop = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Stop);
        var target = Assert.Single(ops, o => o.Kind == DeploymentOpKind.Target);

        var resting = (await Wire(rig))
            .Where(o => o.State is ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED).ToList();
        foreach (var o in resting) log.WriteLine($"resting {o.Type} {o.Side} stop={o.StopPrice} limit={o.LimitPrice}");
        Assert.Equal(2, resting.Count);
        Assert.Contains(resting, o => o.Type == OrderType.Stop && o.StopPrice == 95m);
        Assert.Contains(resting, o => o.Type == OrderType.Limit && o.LimitPrice == 101m);

        // AND A BAR REACHES THE TARGET AND NOT THE STOP. The limit fills at its own level.
        rig.Bar(4, 100.5m, 100.9m, 100.2m, 100.8m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        rig.Bar(5, 101m, 101.5m, 100.8m, 101.2m);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        await rig.Runner.AdvanceAsync();              // the pass that cancels the loser

        Show(log, rig);
        Assert.Equal(0m, await Position(rig));

        var wire = await Wire(rig);
        var targetOrder = Assert.Single(wire,
            o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(target.RequestId));
        var stopOrder = Assert.Single(wire,
            o => o.ClientOrderId == TradingGateway.ClientOrderIdFor(stop.RequestId));
        log.WriteLine($"target {targetOrder.State} / stop {stopOrder.State}");

        Assert.Equal(ExecutionState.FILLED, targetOrder.State);
        Assert.Equal(ExecutionState.CANCELLED, stopOrder.State);
        Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id),
            o => o.Kind == DeploymentOpKind.Cancel);
    }

    // ---------------------------------------------------------------- (c)

    [Fact]
    public async Task The_maximum_hold_closes_at_the_bars_close_before_the_evaluator_is_asked()
    {
        // No stop and no target: the maximum hold is the only thing that can end this position, and
        // the program's entry stays true on every bar, so an evaluator asked from a position that was
        // NOT closed first would read Long and answer with an exit of its own.
        await using var rig = await ReadyAsync(ProgramText("max_hold_bars 2\n"));

        rig.Bar(1, 99m, 101m, 98m, 101m);
        await rig.Runner.AdvanceAsync();

        rig.Bar(2, 102m, 103m, 101m, 102m);           // the minute already in progress
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        rig.Bar(3, 103m, 104m, 102m, 103m);           // the entry fills at this open, 103
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        Assert.Equal(1m, await Position(rig));

        rig.Bar(4, 104m, 105m, 103m, 104m);           // held 1 bar
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        rig.Bar(5, 105m, 106m, 104m, 105m);           // held 2 bars: the limit is reached
        await rig.Gw.RefreshHealthAsync();
        var states = await rig.Runner.AdvanceAsync();
        Show(log, rig);

        // THE APP'S OWN PROTECTION CLOSED IT, AND THE EVALUATOR WAS ASKED AFTERWARDS FROM A FLAT
        // ACCOUNT. The op is a `flatten` — the runner's own — and NOT an `exit`, which is what the
        // evaluator produces when it is asked first and reads a position the limit had reached.
        var ops = rig.Gw.Deployments.OpsOf(rig.Deployment.Id);
        Assert.Single(ops, o => o.Kind == DeploymentOpKind.Flatten);
        Assert.DoesNotContain(ops, o => o.Kind == DeploymentOpKind.Exit);

        var state = Assert.Single(states);
        log.WriteLine($"account at the last bar: {state.Account}");
        log.WriteLine($"counters: {state.State!.Counters}");
        Assert.Equal(PositionSide.Flat, state.Account!.Position);

        // AND THE EVALUATOR EMITTED NOTHING ON THAT BAR. One intent in the whole run — the entry.
        // Asked from the position instead, the program's own `max_hold_bars` exit fires and there are
        // two, which is the mutant: the app's protection and the program's exit for one event.
        Assert.Equal(1, state.State!.Counters.Intents);
    }

    // ---------------------------------------------------------------- (d)

    [Fact]
    public async Task A_stale_bar_is_refused_DECISION_EXPIRED_recorded_and_the_cursor_still_advances_with_nothing_at_the_wire()
    {
        await using var rig = await ReadyAsync(ProgramText());

        rig.Bar(1, 99m, 101m, 98m, 101m);

        // THE BAR CLOSED AND THEN THE APP WAS BUSY FOR TEN MINUTES. The program declares a
        // `max_decision_age` of five, so the signal is past it before anything is sent — and the
        // refusal is DEFINITE: nothing left this process.
        rig.Clock.At = rig.Origin.AddMinutes(12);
        await rig.Runner.AdvanceAsync();
        Show(log, rig);

        var op = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        log.WriteLine($"{op.Kind} {op.State} — {op.Answer}");
        Assert.Equal(DeploymentOpState.Refused, op.State);
        Assert.Contains(ErrorCode.DECISION_EXPIRED.ToString(), op.Answer);

        // NOTHING AT THE WIRE, AND THE CURSOR MOVED OVER THE BAR ANYWAY: a refused operation is an
        // outcome, and a run that stalled on one would never take another bar.
        Assert.Empty(await Wire(rig));
        Assert.Equal(op.BarOpenTime, rig.Gw.Deployments.ById(rig.Deployment.Id)!.CursorOpenTime);
    }

    // ---------------------------------------------------------------- (e)

    [Fact]
    public async Task A_crash_after_the_order_was_accepted_recovers_on_restart_with_exactly_one_order_at_the_wire_and_the_same_position()
    {
        // THE ORDER THIS IS ABOUT IS THE RUNNER'S OWN PROTECTION. An entry and an exit are both
        // guarded a second time by the evaluator, which emits neither while the caller reports an
        // order pending; the maximum hold's close is not, and must not be — protection that a
        // pending order could suppress is not protection. What stops it going twice is the
        // write-ahead operation and the cursor, and this is the test of exactly that.
        var db = TestEnv.NewDb();
        var book = Path.Combine(TestEnv.Home, $"paper-crash-{Guid.NewGuid():n}.db");
        var origin = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
        var program = ProgramText("max_hold_bars 1\n");
        string deploymentId, flattenRequest;

        // ---- the process that died, with a close it had already put on the wire
        {
            await using var first = await ReadyAsync(program, book, db, origin);
            deploymentId = first.Deployment.Id;

            first.Bar(1, 99m, 101m, 98m, 101m);             // the entry signals
            await first.Runner.AdvanceAsync();
            first.Bar(2, 102m, 103m, 101m, 102m);           // the minute already in progress
            await first.Gw.RefreshHealthAsync();
            await first.Runner.AdvanceAsync();
            first.Bar(3, 103m, 104m, 102m, 103m);           // the entry fills at this open
            await first.Gw.RefreshHealthAsync();
            await first.Runner.AdvanceAsync();
            Assert.Equal(1m, await Position(first));

            first.Bar(4, 104m, 105m, 103m, 104m);           // held one bar: the limit is reached
            await first.Gw.RefreshHealthAsync();
            await first.Runner.AdvanceAsync();
            Show(log, first);

            var flatten = Assert.Single(first.Gw.Deployments.OpsOf(deploymentId),
                o => o.Kind == DeploymentOpKind.Flatten);
            flattenRequest = flatten.RequestId;
            log.WriteLine($"before the crash: {flatten.RequestId} {flatten.Kind} {flatten.State}");

            // ACCEPTED AT THE VENUE AND WITH NO TERMINAL ANSWER: the close is working and the
            // position it is closing is still open.
            Assert.Equal(DeploymentOpState.Dispatched, flatten.State);
            Assert.Equal(1m, await Position(first));
        }

        // ---- the same installation, a new process over the same database and the same book
        await using var second = await ReadyAsync(program, book, db, origin);

        // A MINUTE CLOSED WHILE IT WAS COMING UP. The maximum hold is still exceeded and the position
        // is still open, so a runner that walked past the bar its own close is unresolved on would
        // send a second one — and two market sells under a long 1 is a SHORT 1.
        second.Bar(5, 105m, 106m, 104m, 105m);
        var states = await second.Runner.AdvanceAsync();
        Show(log, second);

        var working = (await Wire(second))
            .Where(o => o.State is ExecutionState.WORKING or ExecutionState.ACKNOWLEDGED).ToList();
        foreach (var o in working) log.WriteLine($"working {o.ClientOrderId} {o.Side} {o.Quantity}");

        Assert.Single(working);
        Assert.Equal(TradingGateway.ClientOrderIdFor(flattenRequest), working[0].ClientOrderId);
        Assert.Single(second.Gw.Deployments.OpsOf(deploymentId),
            o => o.Kind == DeploymentOpKind.Flatten);
        Assert.Equal(1m, await Position(second));       // the same position it crashed holding
        Assert.Single(states);

        // AND THE OTHER HALF: the one close fills once, the operation resolves, and the run is flat
        // rather than short.
        second.Bar(6, 106m, 107m, 105m, 106m);
        await second.Gw.RefreshHealthAsync();
        await second.Gw.ReconcilePaperDeploymentsAsync(second.Clock.At);
        await second.Runner.AdvanceAsync();
        Show(log, second);

        Assert.Equal(0m, await Position(second));
        Assert.Equal(DeploymentOpState.Resolved,
            second.Gw.Deployments.OpById(flattenRequest)!.State);
    }

    // ---------------------------------------------------------------- (f)

    [Fact]
    public async Task A_second_runner_over_the_same_rows_reaches_the_same_state_and_the_gateway_refuses_its_duplicate_request_id()
    {
        await using var rig = await ReadyAsync(ProgramText());

        rig.Bar(1, 99m, 101m, 98m, 101m);
        await rig.Runner.AdvanceAsync();                    // the pass that wrote the operation
        var first = Assert.Single(await rig.Runner.AdvanceAsync());

        // A SECOND RUNNER OVER THE SAME ROWS. Nothing is held in the first one that this one lacks:
        // the evaluator's windows are a function of the bars and the position is read back out of the
        // run's own operation ledger.
        var other = new ForwardRuns(rig.Gw, rig.Db, () => rig.Clock.At);
        var second = Assert.Single(await other.AdvanceAsync());

        log.WriteLine($"first: {first.BarsReplayed} bars, last {first.LastBar:u}, {first.Account}");
        log.WriteLine($"second: {second.BarsReplayed} bars, last {second.LastBar:u}, {second.Account}");
        Assert.Equal(first.BarsReplayed, second.BarsReplayed);
        Assert.Equal(first.LastBar, second.LastBar);
        Assert.Equal(first.Account, second.Account);
        Assert.Equal(first.State!.Counters, second.State!.Counters);

        // AND ONE ORDER. The second runner presented the same request id and the gateway answered it
        // with the record that was already there — the idempotent replay, not a second order.
        var op = Assert.Single(rig.Gw.Deployments.OpsOf(rig.Deployment.Id));
        Assert.Single(await Wire(rig));

        var replay = await rig.Gw.PlaceAsync(AgentContext.Deployment(rig.Deployment.Id), op.RequestId,
            new PlaceIntent("BTCUSDT", OrderSide.Buy, OrderType.Market, 1m, null, null, TimeInForce.Day, null));
        log.WriteLine($"replay of {op.RequestId} answered {replay.State} / {replay.ClientOrderId}");
        Assert.Equal(TradingGateway.ClientOrderIdFor(op.RequestId), replay.ClientOrderId);
        Assert.Single(await Wire(rig));
    }
}
