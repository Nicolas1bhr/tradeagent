using System.Globalization;
using System.Text;
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

namespace TradeAgent.Tests.Fault;

/// <summary>
/// THE PAPER RUNNER VALUES A PROGRAM'S FEATURES AS THE BACKTEST DOES (<c>U-runner-features</c>; EDGE § 4.4 v2a; R05 row 10):
/// at each declared close, from what had arrived by it less the spec's latency, through the backtest's own
/// <c>FeatureFeed</c>, under a holdout that reads no window — absent meaning no decision, protection still on the minute,
/// each order naming the values it stood on, and live still refused.
///
/// <para><b>What it replaced.</b> <c>U-language-v2a</c>'s runner computed no feature value, so a run of a program that
/// reads one was ENDED before its first bar, in words, and the deployment sweep started no replacement. A judged program
/// had nowhere to run forward. Now it runs, and the four ways it may not are each ended in words: no tape on this host,
/// reads that would reach a holdout window, a feed that halts, and a replay that no longer reads what an order stood on.</para>
///
/// <para><b>The harness is the money path, in paper mode</b>, built by the calls the product uses: the envelope through the
/// gateway's grant, the allocation and the deployment by the app's own sweeps, the minutes into <c>forward_bar</c> and to
/// the paper connector through the shipped adapter (<see cref="ForwardBarSource"/>), every order through
/// <c>PlaceAsync</c>, and the tape a temporary file written through <see cref="TapeStore"/> (<see cref="TapeReadings"/>,
/// the backtest tests' own helper) and read through a <see cref="TapeReader"/> handed to the runner by the test as the
/// composition root hands it. Nothing reaches a venue and no real money is involved.</para>
/// </summary>
public partial class FeatureProgramRunnerTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The deployment's start, 12:00: the first minute it reads is 12:01, and minute 60·h − 1 is the last of an hour.</summary>
    static readonly DateTimeOffset At = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The minute whose close completes the hour that opened <paramref name="hour"/> hours after the start.</summary>
    static int HourClose(int hour) => 60 * hour - 1;

    /// <summary>The backtest tests' feature: funding, five seconds of latency, a reading of the last two hours.</summary>
    const string Spec = FeatureProgramSpec.Funding;

    /// <summary>
    /// AN HOURLY PROGRAM THAT BUYS WHEN FUNDING IS DEEPLY NEGATIVE AND SELLS WHEN IT TURNS POSITIVE, its three execution
    /// bounds declared — the backtest tests' observable program, with <paramref name="extra"/> lines and the spec given.
    /// </summary>
    static string Program(string extra = "", string spec = Spec) =>
        "instrument BTCUSDT\nbars 1h\ntimeframe 1h\ndata_freshness 2h\nmax_decision_age 1h\nsize fixed 1\n"
        + $"feature funding = {spec}\n{extra}exit when funding > 0\nentry when funding < -0.0003\n";

    /// <summary>A clock this suite owns and moves, a minute at a time.</summary>
    internal sealed class TestClock(DateTimeOffset at) : TimeProvider
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

    /// <summary>
    /// ONE INSTALLATION WITH A FEATURE PROGRAM'S RUN IN IT, and the tape its runner reads — or none, for a host that has
    /// no tape open. <see cref="Runner"/> is settable: a restart is a new runner over the same database and tape.
    /// </summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required PaperConnector Conn { get; init; }
        public required TradingGateway Gw { get; init; }
        public required TestClock Clock { get; init; }
        public required ForwardBarStore Bars { get; init; }
        public required string Version { get; init; }
        public required string TapeFile { get; init; }
        public TapeStore? Tape { get; init; }
        public TapeReader? Reader { get; init; }
        public required ForwardRuns Runner { get; set; }

        /// <summary>The run the app's own policy started first.</summary>
        public StrategyDeploymentRow Run => Gw.Deployments.All().OrderBy(d => d.StartedAt).First();

        /// <summary>A new runner over the same rows and the same tape: what a restart builds.</summary>
        public ForwardRuns Restarted() => new(Gw, Db, () => Clock.At, Reader);

        /// <summary>One premium-index reading on this rig's tape, stamped and received when the test says.</summary>
        public void Reading(DateTimeOffset stamped, string funding, DateTimeOffset? received = null) =>
            TapeReadings.Reading(Tape ?? throw new InvalidOperationException("this rig has no tape"), stamped, funding, received);

        /// <summary>
        /// MINUTES <paramref name="from"/> TO <paramref name="to"/> AFTER THE START, CLOSED, the way the collector stores a
        /// stretch: one fetch, every bar flat at <paramref name="price"/>, the clock moved past the last one's close.
        /// </summary>
        public void Minutes(int from, int to, decimal price = 100m)
        {
            var klines = new List<ForwardBars.Kline>(to - from + 1);
            for (var minute = from; minute <= to; minute++)
            {
                var open = At.AddMinutes(minute);
                klines.Add(new ForwardBars.Kline(open, price, price, price, price, 10m, open + ForwardBars.BarLength));
            }

            var last = klines[^1].CloseTime;
            var append = Bars.Append(new ForwardFetchAttempt
            {
                Source = ForwardBars.Source,
                Symbol = "BTCUSDT",
                Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                RequestedAt = last,
                ReceivedAt = last.AddSeconds(1),
                HttpStatus = 200
            }, klines);
            Assert.Equal(klines.Count, append.Stored);
            Clock.At = last.AddSeconds(1);
        }

        /// <summary>
        /// WHAT THE APP DOES WHEN A STRETCH OF MINUTES HAS CLOSED: the connector asked, which settles its book on them, and
        /// one pass of the runner — every run it advanced.
        /// </summary>
        public async Task<IReadOnlyList<ForwardRunState>> PassAsync()
        {
            await Gw.RefreshHealthAsync();
            return await Runner.AdvanceAsync();
        }

        /// <summary>
        /// MINUTES UP TO THE CLOSE OF THE HOUR THAT OPENED <paramref name="hour"/> HOURS IN, AS THE APP SEES THEM: the first
        /// two and a pass — where an order decided at the close before fills, at the open of the first minute after it was
        /// sent and on that minute, as it does when the minutes arrive one at a time and a pass runs every five seconds —
        /// then the rest and a pass. The state the last pass left.
        /// </summary>
        public async Task<ForwardRunState> ThroughHourAsync(int hour, int fromMinute, decimal price = 100m)
        {
            if (HourClose(hour) - fromMinute > 2)
            {
                Minutes(fromMinute, fromMinute + 1, price);
                var early = Assert.Single(await PassAsync());
                if (early.Ended is not null) return early;
                fromMinute += 2;
            }

            Minutes(fromMinute, HourClose(hour), price);
            return Assert.Single(await PassAsync());
        }

        public IReadOnlyList<DeploymentOpRow> Ops => Gw.Deployments.OpsOf(Run.Id);

        public async Task<IReadOnlyList<OrderInfo>> Wire() =>
            await Conn.GetOrdersAsync(PaperConnector.TheAccount, includeInactive: true, since: null);

        public async Task<decimal> Position() =>
            (await Conn.GetPositionsAsync(PaperConnector.TheAccount)).FirstOrDefault(p => p.Symbol == "BTCUSDT")?.Quantity ?? 0m;

        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Conn.DisposeAsync();
            Tape?.Dispose();
            Db.Dispose();
        }
    }

    /// <summary>
    /// A PAPER ACCOUNT, A GRANT, <paramref name="text"/> JUDGED PAPER-ELIGIBLE, AND THE APP'S OWN ALLOCATION AND DEPLOYMENT
    /// OF IT — each by the call the product uses — with a tape open on this host unless <paramref name="tape"/> is false.
    /// <paramref name="tapeFile"/> is a tape another rig already writes, read here and never written.
    /// </summary>
    internal static async Task<Rig> ReadyAsync(string text, bool tape = true, string? tapeFile = null)
    {
        var db = TestEnv.NewDb();
        var clock = new TestClock(At);
        var bars = new ForwardBarStore(db);
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = new ForwardBarSource(bars),
            Clock = () => clock.At,
            BookFile = Path.Combine(TestEnv.Home, $"paper-features-{Guid.NewGuid():n}.db"),
            Catalogue = Catalogue()
        });

        // The quote gate is held wide: the age these tests are about is the decision's, and QuoteClockTests owns the quote's.
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

        // AFTER the gateway, which syncs the shipped catalogue — whose BTCUSDT row is unverified on purpose.
        new VenueStore(db).Sync(Catalogue());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        // THE TAPE, AS THE COMPOSITION ROOT HANDS IT: the collector's store writes it, a reader reads it — on the runner,
        // and on the gateway, whose sweep asks it (`AppHost.ReportTapeToTheGateway`).
        var file = tapeFile ?? TapeReadings.NewFile();
        var store = tape && tapeFile is null ? new TapeStore(file) : null;
        var reader = tape ? new TapeReader(file) : null;
        gw.Tape = reader;

        var granted = await gw.GrantPaperEnvelopeAsync("BTCUSDT", 5m, 5_000_000m, At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);
        var version = Judged(db, text);
        Assert.Equal(1, gw.AllocatePaperDue(At));
        Assert.Equal(1, gw.StartPaperDeploymentsDue(At));

        return new Rig
        {
            Db = db, Conn = conn, Gw = gw, Clock = clock, Bars = bars, Version = version, TapeFile = file,
            Tape = store, Reader = reader, Runner = new ForwardRuns(gw, db, () => clock.At, reader)
        };
    }

    /// <summary>
    /// A VERSION OF <paramref name="text"/> THAT REALLY CARRIES A PAPER-ELIGIBLE VERDICT IN THIS DATABASE — dataset, holdout,
    /// campaign, version, holdout run and promotion — written by this build. Its dataset holds out June and July, months
    /// before the run, so no read of the run's reaches that window.
    /// </summary>
    static string Judged(Database db, string text)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"feature-program-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT funding", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var parse = StrategyParser.Parse(text);
        Assert.True(parse.Ok, parse.Why);
        var program = parse.Program!;

        var strategies = new StrategyStore(db);
        strategies.RecordVersion(new StrategyVersionRow(
            program.StrategyId, program.Source, program.Canonical, program.Manifest, StrategyStore.InterpreterBuild,
            ParseVerdict.Accepted, program.WarmUpBars, Cutoff.AddDays(-1), null, null)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        var model = ExecutionModel.Declare(0.001m, 0m, 0.0001m, 10_000m).Model!;
        var runId = new BacktestRequest(set.Id, set.NormalisedSha256, model, Cutoff, null).RunIdFor(program.StrategyId);
        strategies.RecordRun(new StrategyRunRow(
            runId, program.StrategyId, set.Id, set.NormalisedSha256, Cutoff, null, model.Canonical,
            BacktestOutcome.COMPLETED.ToString(), null, 500, 4, 3, 4, 4, 100, 0, 0,
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id, CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical, Referee.EvaluatorVersion,
            runId, PromotionVerdict.PaperEligible, PromotionReason.MetOnHistory, At)
        {
            Timeframe = program.Freshness?.Timeframe,
            DataFreshness = program.Freshness?.DataFreshness,
            MaxDecisionAge = program.Freshness?.MaxDecisionAge
        });

        return program.StrategyId;
    }

    /// <summary>The minute after the start an operation was written on.</summary>
    static int MinuteOf(DeploymentOpRow op) => (int)(op.BarOpenTime - At).TotalMinutes;

    /// <summary>The intent an operation wrote down before anything was sent.</summary>
    static PlaceIntent IntentOf(DeploymentOpRow op) => Json.Read<PlaceIntent>(op.IntentJson)!;

    /// <summary>A request or client order id with the deployment's hash taken out and the minute made relative.</summary>
    static string Said(string id)
    {
        var request = id.StartsWith("TA-", StringComparison.Ordinal) ? id[3..] : id;
        var parts = request.Split('-');
        if (parts.Length != 4 || parts[0] != "dp") return id;
        var minute = long.Parse(parts[2], CultureInfo.InvariantCulture) - At.ToUnixTimeSeconds() / 60;
        return FormattableString.Invariant($"+{minute}#{parts[3]}");
    }

    /// <summary>
    /// A RUN'S OPERATIONS, THE INTENTS THEY WROTE AND ITS ORDERS AT THE WIRE, with the deployment's id taken out of every
    /// one — so two installations' runs of one version can be compared line for line.
    /// </summary>
    static async Task<string> Ledger(Rig rig)
    {
        var said = new StringBuilder();
        foreach (var op in rig.Ops)
            said.Append(CultureInfo.InvariantCulture,
                $"op {Said(op.RequestId)} {op.Kind} {op.State} — {op.Answer}\n   {op.IntentJson.Replace(rig.Run.Id, "<run>", StringComparison.Ordinal)}\n");
        foreach (var order in (await rig.Wire()).OrderBy(o => o.ClientOrderId, StringComparer.Ordinal))
            said.Append(CultureInfo.InvariantCulture,
                $"wire {Said(order.ClientOrderId ?? "-")} {order.Side} {order.Type} {order.Quantity} filled={order.FilledQuantity} {order.State}\n");
        said.Append(CultureInfo.InvariantCulture, $"position {await rig.Position()}\n");
        return said.ToString();
    }

    void Show(Rig rig)
    {
        foreach (var op in rig.Ops)
            log.WriteLine($"op {Said(op.RequestId)} {op.Kind} {op.State} — {op.Answer}");
    }

    // ---- (a) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) A PAPER RUN OF A FEATURE PROGRAM DECIDES AT THE CLOSE, ON WHAT HAD ARRIVED. Funding is a mild 0.0001 at 12:10 and
    /// −0.01 at 13:30. At the 13:00 close the newest reading that had arrived is the mild one, so the hour opening 12:00
    /// decides nothing; at the 14:00 close it is −0.01, so the hour opening 13:00 enters — written on that hour's last
    /// minute, decided at 14:00, sent through <c>PlaceAsync</c> and filled by the paper connector at the next open.
    ///
    /// <para><b>RED before this unit</b>: the run is ended before its first bar — "this program requires `feature`, which this
    /// build's paper runner does not implement". <b>The mutant</b> — the runner asking the feed for the NEXT close's value —
    /// goes red here: the evaluator refuses a value stamped with another instant, the step faults and the run ends.</para>
    /// </summary>
    [Fact]
    public async Task A_paper_run_of_a_feature_program_decides_at_the_close_on_what_had_arrived()
    {
        await using var rig = await ReadyAsync(Program());
        rig.Reading(At.AddMinutes(10), "0.00010000");
        rig.Reading(At.AddMinutes(90), "-0.01000000");

        var first = await rig.ThroughHourAsync(1, 1);
        log.WriteLine($"13:00: {first.BarsReplayed} bar(s), ended: {first.Ended ?? "no"}");
        Assert.Null(first.Ended);
        Assert.Equal(1, first.BarsReplayed);
        Assert.Empty(rig.Ops);

        var second = await rig.ThroughHourAsync(2, HourClose(1) + 1);
        Show(rig);
        Assert.Null(second.Ended);
        Assert.Equal(2, second.BarsReplayed);
        var entry = Assert.Single(rig.Ops);
        Assert.Equal(DeploymentOpKind.Entry, entry.Kind);
        Assert.Equal(HourClose(2), MinuteOf(entry));
        Assert.Equal(At.AddHours(2), IntentOf(entry).Decision!.BarClose);

        rig.Minutes(HourClose(2) + 1, HourClose(2) + 3);
        Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
        var order = Assert.Single(await rig.Wire());
        Assert.Equal(TradingGateway.ClientOrderIdFor(entry.RequestId), order.ClientOrderId);
        Assert.Equal(1m, await rig.Position());
    }

    // ---- (b) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (b) A READING THAT ARRIVED AFTER THE CLOSE MOVES NO DECISION AT IT — the backtest's (f), on paper
    /// (<c>FeatureProgramBacktestTests.A_backtest_reads_a_feature_only_as_it_had_arrived</c>). Funding is mild at ten past
    /// 12, 13 and 14; a spike to −0.01 is stamped 14:59:58 — before the 15:00 close — and first seen at 15:00:20, after
    /// it. The tape holds every reading before the run is stepped. At the 15:00 close the spike had not arrived, so the hour
    /// opening 14:00 decides nothing; at the 16:00 close it had, and is the newest, so the hour opening 15:00 enters. A
    /// restart, which reads every close again from the tape, decides nothing more.
    ///
    /// <para><b>RED before this unit</b>: the run is ended before its first bar.</para>
    /// </summary>
    [Fact]
    public async Task A_reading_that_arrived_after_the_close_moves_no_decision_at_it()
    {
        await using var rig = await ReadyAsync(Program());
        for (var h = 0; h < 3; h++) rig.Reading(At.AddHours(h).AddMinutes(10), "0.00010000");
        rig.Reading(At.AddHours(3).AddSeconds(-2), "-0.01000000", received: At.AddHours(3).AddSeconds(20));
        rig.Reading(At.AddHours(4).AddMinutes(30), "0.00010000");

        for (var h = 1; h <= 3; h++)
        {
            var state = await rig.ThroughHourAsync(h, h == 1 ? 1 : HourClose(h - 1) + 1);
            Assert.Null(state.Ended);
            Assert.Empty(rig.Ops);
        }

        var entered = await rig.ThroughHourAsync(4, HourClose(3) + 1);
        Show(rig);
        Assert.Null(entered.Ended);
        var entry = Assert.Single(rig.Ops);
        Assert.Equal(DeploymentOpKind.Entry, entry.Kind);
        Assert.Equal(HourClose(4), MinuteOf(entry));
        Assert.Equal(At.AddHours(4), IntentOf(entry).Decision!.BarClose);

        // A RESTART READS EVERY CLOSE FROM THE TAPE AGAIN — the spike among the rows at the 15:00 close — and still decides
        // nothing there.
        var before = await Ledger(rig);
        rig.Runner = rig.Restarted();
        var replayed = Assert.Single(await rig.PassAsync());
        Assert.Null(replayed.Ended);
        Assert.Equal(4, replayed.BarsReplayed);
        Assert.Equal(before, await Ledger(rig));
    }

    // ---- (c) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (c) AN ABSENT VALUE DECIDES NOTHING, SAYS WHY, AND PROTECTION STILL ACTS. A program with a one-hour max age and a
    /// two-bar maximum hold: funding is −0.01 at 12:10, so the 13:00 close enters; the tape is then silent, so at 14:00,
    /// 15:00, 16:00 and 17:00 the value is ABSENT — no decision, counted — yet the maximum hold, which is protection,
    /// closes the position on the minute that reaches it. The first absence on a live close is said once on the
    /// engineering line <c>forward_run_feature_absent</c> — the run, the close, the feature, the evaluator's words and the
    /// source's last delivery, which is older than twice its cadence plus 30 s — and not again until the value is present.
    /// A reading at 17:10 makes it present at 18:00, where the program enters again; at 19:00 it is absent again, and said
    /// again.
    ///
    /// <para><b>RED before this unit</b>: the run is ended before its first bar.</para>
    /// </summary>
    [Fact]
    public async Task An_absent_value_decides_nothing_says_why_and_protection_still_acts()
    {
        await using var rig = await ReadyAsync(Program("max_hold_bars 2\n",
            Spec.Replace("\"max_age_s\":7200", "\"max_age_s\":3600", StringComparison.Ordinal)));
        rig.Reading(At.AddMinutes(10), "-0.01000000");

        ForwardRunState state = await rig.ThroughHourAsync(1, 1);
        for (var h = 2; h <= 7; h++)
        {
            // THE TAPE IS WRITTEN AS IT ARRIVES: the 17:10 reading only once the 17:00 close is decided, so the source's
            // last delivery said at 14:00 is the one that had arrived by then.
            if (h == 6) rig.Reading(At.AddHours(5).AddMinutes(10), "-0.01000000");
            state = await rig.ThroughHourAsync(h, HourClose(h - 1) + 1);
            Assert.Null(state.Ended);

            // NO DECISION, COUNTED: by 15:00 the two closes with no value are the evaluator's undefined events.
            if (h == 3)
            {
                log.WriteLine($"through 15:00: {state.BarsReplayed} closes, {state.State!.Counters.UndefinedEvents} undefined");
                Assert.Equal(3, state.BarsReplayed);
                Assert.Equal(2, state.State.Counters.UndefinedEvents);
            }
        }
        Show(rig);

        // THE PROGRAM DECIDED TWICE, BOTH ENTRIES, AT 13:00 AND 18:00; THE MAXIMUM HOLD CLOSED THE FIRST POSITION ON THE
        // MINUTE THAT REACHED IT, WHILE THE VALUE WAS ABSENT; AND THE PROGRAM NEVER EXITED ON A VALUE IT DID NOT HAVE.
        Assert.Equal([HourClose(1), HourClose(6)],
            rig.Ops.Where(o => o.Kind == DeploymentOpKind.Entry).Select(MinuteOf));
        // The maximum hold closed it at 16:00 — the minute after is asked again while that close is in flight, and refused
        // before the wire, as on any declared bar — and exactly one close reached the venue.
        var flattens = rig.Ops.Where(o => o.Kind == DeploymentOpKind.Flatten).ToList();
        Assert.Equal(HourClose(4), MinuteOf(flattens[0]));
        Assert.Single(await rig.Wire(), o => o.Side == OrderSide.Sell);
        Assert.DoesNotContain(rig.Ops, o => o.Kind == DeploymentOpKind.Exit);

        // SAID ONCE PER ABSENCE, ON A LIVE CLOSE: at 14:00, and again at 19:00 after it was present at 18:00.
        var said = Absences(rig.Db);
        foreach (var line in said) log.WriteLine(line);
        Assert.Equal(2, said.Count);
        Assert.Contains("\"close\":\"2026-10-03T14:00:00+00:00\"", said[0], StringComparison.Ordinal);
        Assert.Contains("\"close\":\"2026-10-03T19:00:00+00:00\"", said[1], StringComparison.Ordinal);
        Assert.All(said, line =>
        {
            Assert.Contains($"\"deployment\":\"{rig.Run.Id}\"", line, StringComparison.Ordinal);
            Assert.Contains("\"feature\":\"funding\"", line, StringComparison.Ordinal);
            Assert.Contains("the newest is older than max_age_s (3600 s)", line, StringComparison.Ordinal);
            Assert.Contains("\"source\":\"binance-um-premium\"", line, StringComparison.Ordinal);
        });
        Assert.Contains("\"last_delivered\":\"2026-10-03T12:10:02+00:00\"", said[0], StringComparison.Ordinal);
        Assert.Contains("\"last_delivered\":\"2026-10-03T17:10:02+00:00\"", said[1], StringComparison.Ordinal);
    }

    /// <summary>Every <c>forward_run_feature_absent</c> line, oldest first, as its metadata.</summary>
    static List<string> Absences(Database db) => db.Read(_ =>
    {
        using var c = db.Cmd("SELECT metadata FROM engineering_log WHERE component='Gateway' AND event=$e ORDER BY id",
            ("$e", "forward_run_feature_absent"));
        using var r = c.ExecuteReader();
        var lines = new List<string>();
        while (r.Read()) lines.Add(r.GetString(0));
        return lines;
    });

    // ---- (g) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (g) THE RUNNER STEPS A FEATURE PROGRAM, AND LIVE STAYS REFUSED — rewritten from <c>U-language-v2a</c>'s
    /// <c>A_runner_that_does_not_understand_a_required_field_refuses</c>, whose refusal this unit lifts (its name is removed,
    /// a judged exception as <c>U-timeframe-b</c>'s two were). With a tape, a paper-eligible version that reads funding is
    /// stepped: two and a half hours of minutes are the two hours that closed in them, and nothing ends it. Its verdict
    /// still confers NO live authority — every tape source is research-only, and the promotion's live refusal says so. On a
    /// host with no tape open, the same version's run is ended in words before a bar is stepped, nothing is sent,
    /// <c>ForwardRuns.CannotRun</c> says the same sentence, and three sweeps start no replacement.
    ///
    /// <para><b>RED before this unit</b>: the run with a tape is ended before its first bar.</para>
    /// </summary>
    [Fact]
    public async Task The_runner_steps_a_feature_program_and_live_stays_refused()
    {
        var text = Program();
        var program = StrategyParser.Parse(text).Program!;
        Assert.Contains(StrategyDeclarations.Feature, program.Requires);
        Assert.Equal(StrategyDeclarations.All, ForwardRuns.Implements);
        Assert.Null(ForwardRuns.Refuses(program));

        // WITH A TAPE: STEPPED, AND STILL NO CAPITAL.
        await using (var rig = await ReadyAsync(text))
        {
            rig.Reading(At.AddMinutes(10), "0.00010000");
            rig.Minutes(1, 150);
            var state = Assert.Single(await rig.PassAsync());
            log.WriteLine($"with a tape : {state.BarsReplayed} bars stepped, ended: {state.Ended ?? "no"}");
            Assert.Null(state.Ended);
            Assert.Equal(2, state.BarsReplayed);
            Assert.True(rig.Gw.Deployments.ById(rig.Run.Id)!.IsActive);

            var standing = rig.Gw.Promotions.Standing(rig.Version);
            log.WriteLine($"live refusal: {standing.LiveRefusal}");
            Assert.Equal(PromotionState.PaperEligible, standing.State);
            Assert.NotNull(standing.LiveRefusal);
        }

        // WITHOUT ONE: ENDED IN WORDS BEFORE A BAR, NOTHING SENT, AND NO CHURN.
        await using (var rig = await ReadyAsync(text, tape: false))
        {
            rig.Minutes(1, 150);
            var state = Assert.Single(await rig.PassAsync());
            var ended = rig.Gw.Deployments.ById(rig.Run.Id)!;
            log.WriteLine($"no tape     : {ended.State} — {ended.EndReason ?? "-"}");

            const string Words =
                "this program reads 1 feature(s) — `funding` — and this host has no market-context tape open to read them "
                + "from, so the paper runner cannot value them at its closes: a program that reads a feature runs on its values "
                + "as they had arrived, or not at all — never as though every value were absent. TradeAgent opens the tape "
                + "itself when it starts, and the account owner's activity log says why it is not open; a run of this version "
                + "starts again by itself once one is";
            Assert.Equal(DeploymentState.Ended, ended.State);
            Assert.Equal(Words, ended.EndReason);
            Assert.Equal(Words, state.Ended);
            Assert.Equal(0, state.BarsReplayed);
            Assert.Empty(await rig.Wire());
            Assert.DoesNotContain(rig.Ops, o => o.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit);
            Assert.True(rig.Gw.Deployments.IsReconciled(rig.Run.Id));

            // AND THE SWEEP SAYS THE SAME SENTENCE, SO IT STARTS NO REPLACEMENT.
            Assert.Equal(Words, ForwardRuns.CannotRun(rig.Gw.Strategies, rig.Version, rig.Gw.Tape, rig.Gw.Datasets, rig.Clock.At));
            var started = new List<int>();
            for (var k = 1; k <= 3; k++)
            {
                rig.Clock.At = rig.Clock.At.AddMinutes(1);
                started.Add(rig.Gw.StartPaperDeploymentsDue(rig.Clock.At));
                await rig.Runner.AdvanceAsync();
            }
            log.WriteLine($"sweeps after the end started: {string.Join(", ", started)}");
            Assert.Equal([0, 0, 0], started);
            Assert.Single(rig.Gw.Deployments.ForAllocation(rig.Run.AllocationId));
            Assert.Empty(rig.Gw.Deployments.Open());
        }
    }

    // ---- (h) ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (h) A RESTART READS THE SAME VALUES AND DECIDES AS A RUN THAT NEVER STOPPED. Two installations run one version over
    /// one tape and the same minutes, pass for pass: in one the runner never stops, and so reads each close from the tape
    /// once and serves it from what it read ever after; in the other a new runner — a restart, knowing nothing — takes over
    /// half way through an hour while a position is open, and reads every close from the tape again. Funding turns
    /// negative, positive, negative and positive again over seven hours, so the run enters at 13:00, exits at 15:00,
    /// enters at 17:00 and exits at 19:00 — each a close after the one its signal turned on, because the close after a
    /// decision reads it pending. The two must arrive at the same operations under the same ids, the same intents — the
    /// values each order stood on and their digest among them — and the same orders at the wire.
    ///
    /// <para><b>RED before this unit</b>: both runs are ended before their first bar.</para>
    /// </summary>
    [Fact]
    public async Task A_restart_reads_the_same_values_and_decides_as_a_run_that_never_stopped()
    {
        await using var control = await ReadyAsync(Program());
        control.Reading(At.AddMinutes(10), "-0.01000000");
        control.Reading(At.AddHours(1).AddMinutes(50), "0.00020000");
        control.Reading(At.AddHours(3).AddMinutes(20), "-0.02000000");
        control.Reading(At.AddHours(5).AddMinutes(40), "0.00030000");
        await using var restarted = await ReadyAsync(Program(), tapeFile: control.TapeFile);

        foreach (var rig in new[] { control, restarted })
        {
            for (var h = 1; h <= 5; h++) Assert.Null((await rig.ThroughHourAsync(h, h == 1 ? 1 : HourClose(h - 1) + 1)).Ended);
            rig.Minutes(HourClose(5) + 1, HourClose(5) + 1);
            Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
            rig.Minutes(HourClose(5) + 2, HourClose(5) + 30);
            Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
            Assert.Equal(1m, await rig.Position());
        }

        // HALF WAY THROUGH THE HOUR OPENING 17:00, LONG, A NEW PROCESS: a new runner over the same rows and the same tape.
        restarted.Runner = restarted.Restarted();

        foreach (var rig in new[] { control, restarted })
        {
            rig.Minutes(HourClose(5) + 31, HourClose(6));
            Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
            Assert.Null((await rig.ThroughHourAsync(7, HourClose(6) + 1)).Ended);
            rig.Minutes(HourClose(7) + 1, HourClose(7) + 3);
            Assert.Null(Assert.Single(await rig.PassAsync()).Ended);
        }

        var uninterrupted = await Ledger(control);
        var resumed = await Ledger(restarted);
        log.WriteLine(uninterrupted);
        Assert.Equal(uninterrupted, resumed);

        // AND IT TRADED: entered, exited, entered and exited again — so the restart fell inside an open position.
        var decided = control.Ops.Where(o => o.Kind is DeploymentOpKind.Entry or DeploymentOpKind.Exit).ToList();
        Assert.Equal([DeploymentOpKind.Entry, DeploymentOpKind.Exit, DeploymentOpKind.Entry, DeploymentOpKind.Exit],
            decided.Select(o => o.Kind));
        Assert.Equal([HourClose(1), HourClose(3), HourClose(5), HourClose(7)], decided.Select(MinuteOf));
        Assert.Equal(0m, await control.Position());
    }
}
