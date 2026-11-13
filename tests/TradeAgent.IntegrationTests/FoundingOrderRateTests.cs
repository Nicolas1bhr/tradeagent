using TradeAgent.ConnectorSdk;
using TradeAgent.Connectors.Paper;
using TradeAgent.Core;
using TradeAgent.Core.Board;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Platforms;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE FOUNDING ORDER RATE FITS THE RUNS THE FOUNDING ENVELOPES ALLOW, IN PAPER ONLY (PREFLIGHT-2 blocker 2; D20). Under the founding policy as it ships,
/// with the real quote age and the runner choosing every order: runs that signal together get their entries, and every entry its stop and target. The rate
/// every paper test set to 100 or more was the one thing that hid six a minute stripping two runs of both. Nothing reaches a venue; no real money is involved.
/// </summary>
public class FoundingOrderRateTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The 2026 levels R28 sized against, in USDT.</summary>
    static readonly (string Pair, decimal Price)[] Pairs =
        [("BTCUSDT", 100_000m), ("ETHUSDT", 4_000m), ("SOLUSDT", 200m), ("BNBUSDT", 1_000m), ("XRPUSDT", 2m)];

    sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    static VenueCatalogRead Catalogue() => new(
    [
        new VenueEntry
        {
            Id = VenueCatalog.BinanceSpot, DisplayName = "Binance spot", CalendarKind = CalendarKind.Continuous,
            Source = "declared by this test", Verified = true,
            Instruments =
            [
                .. Pairs.Select(p => new VenueInstrumentEntry
                {
                    Symbol = p.Pair, TickSize = 0.0001m, QuantityIncrement = 0.00001m, Source = "declared by this test", Verified = true
                })
            ]
        }
    ], null);

    /// <summary>A program on <paramref name="pair"/> that buys on its first bar, its stop <paramref name="stop"/> per cent under the fill: each stop its own version.</summary>
    static string Program(string pair, int stop) =>
        $"instrument {pair}\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize capital_fraction 1\n"
        + $"stop percent {stop}\ntarget percent 5\nexit when close < 0.5\nentry when close > 0.5\n";

    sealed class Rig : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required PaperConnector Conn { get; init; }
        public required TradingGateway Gw { get; init; }
        public required ForwardRuns Runner { get; init; }
        public required Clock Clock { get; init; }
        public required ForwardBarStore Bars { get; init; }
        public required ForwardBarSource Source { get; init; }
        public Dictionary<string, decimal> Prices { get; } = Pairs.ToDictionary(p => p.Pair, p => p.Price, StringComparer.Ordinal);

        /// <summary>
        /// One closed minute on every pair at its price, the health pass the 5-s tick makes, and the runner's pass three seconds after the close;
        /// <paramref name="beforeTheRunner"/> is whatever else happens at that instant, before the runner's pass.
        /// </summary>
        public async Task Minute(int minute, Func<Task>? beforeTheRunner = null)
        {
            var open = Start.AddMinutes(minute);
            var close = open + ForwardBars.BarLength;
            Clock.At = close;
            foreach (var (pair, _) in Pairs)
            {
                var p = Prices[pair];
                Assert.Equal(1, Bars.Append(new ForwardFetchAttempt
                {
                    Source = ForwardBars.Source, Symbol = pair, Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                    RequestedAt = close, ReceivedAt = close.AddSeconds(1), HttpStatus = 200
                }, [new ForwardBars.Kline(open, p, p, p, p, 10m, close)]).Stored);
            }

            Clock.At = close.AddSeconds(3);
            foreach (var (pair, _) in Pairs) Source.Announce(pair, open);
            await Gw.RefreshHealthAsync();
            if (beforeTheRunner is not null) await beforeTheRunner();
            await Runner.AdvanceAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Conn.DisposeAsync();
        }
    }

    /// <summary>The founding policy as it ships with <paramref name="runsPerPair"/> judged versions of each pair on paper, every one started by the app's own sweeps.</summary>
    static async Task<Rig> Founded(int runsPerPair = 1, Action<Database>? board = null)
    {
        var db = TestEnv.NewDb();
        board?.Invoke(db);
        var clock = new Clock(Start);
        var bars = new ForwardBarStore(db);
        var source = new ForwardBarSource(bars);
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source, Clock = () => clock.At, BookFile = Path.Combine(TestEnv.Home, $"paper-rate-{Guid.NewGuid():n}.db"), Catalogue = Catalogue()
        });

        // THE REAL QUOTE AGE, the default thirty seconds: this class is about what a real minute's orders meet.
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock });
        gw.Update(s => s.SelectedAccountId = PaperConnector.TheAccount);
        new VenueStore(db).Sync(Catalogue());
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        foreach (var (pair, _) in Pairs)
            for (var run = 0; run < runsPerPair; run++) ForwardRunnerTests.Judged(db, Program(pair, 2 + run), Start, pair);
        Assert.Equal(Pairs.Length * runsPerPair, gw.AllocatePaperDue(Start));
        Assert.Equal(Pairs.Length * runsPerPair, gw.StartPaperDeploymentsDue(Start));

        return new Rig
        {
            Db = db, Conn = conn, Gw = gw, Clock = clock, Bars = bars, Source = source,
            Runner = new ForwardRuns(gw, db, () => clock.At)
        };
    }

    void Show(Rig rig, string when)
    {
        log.WriteLine($"--- {when}");
        foreach (var run in rig.Gw.Deployments.All().OrderBy(d => d.Symbol, StringComparer.Ordinal))
        {
            log.WriteLine($"{run.Symbol} {(run.IsActive ? "active" : "ended: " + run.EndReason)}");
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id)) log.WriteLine($"  {op.BarOpenTime:HH:mm} {op.Kind} {op.State}: {op.Answer}");
        }
    }

    /// <summary>Every run holds, and its stop and its target both rest at the venue.</summary>
    void EveryRunIsProtected(Rig rig, int runs)
    {
        var all = rig.Gw.Deployments.All();
        Assert.Equal(runs, all.Count);
        foreach (var run in all)
        {
            var ops = rig.Gw.Deployments.OpsOf(run.Id);
            foreach (var kind in new[] { DeploymentOpKind.Stop, DeploymentOpKind.Target })
            {
                var op = Assert.Single(ops, o => o.Kind == kind);
                Assert.True(op.Answer?.Contains("resting at the venue", StringComparison.Ordinal) == true, $"{run.Symbol}'s {kind}: {op.State}: {op.Answer}");
            }
        }
    }

    /// <summary>
    /// FIVE RUNS THAT SIGNAL TOGETHER ALL KEEP THEIR PROTECTION, and it does what it is for: a 10 % fall stops every one of them out. RED at the base:
    /// <c>ETHUSDT's stop: refused: nothing was sent: RISK_LIMIT_EXCEEDED — 6 orders per minute is the limit</c>; after the fall ETH and XRP still held from entry.
    /// </summary>
    [Fact]
    public async Task Five_runs_entering_together_all_keep_their_stop_and_target_under_the_founding_policy()
    {
        await using var rig = await Founded();
        for (var m = 1; m <= 4; m++) await rig.Minute(m);
        Show(rig, "after four minutes");
        EveryRunIsProtected(rig, 5);

        foreach (var (pair, price) in Pairs) rig.Prices[pair] = price * 0.9m;
        for (var m = 5; m <= 12; m++) await rig.Minute(m);
        Show(rig, "after a 10% fall, eight minutes on");

        // EVERY RUN'S FIRST STOP FILLED. The program is free to buy again afterwards, and does; what the test is about is that nothing was left holding from entry.
        foreach (var run in rig.Gw.Deployments.All())
        {
            var stop = rig.Gw.Deployments.OpsOf(run.Id).First(o => o.Kind == DeploymentOpKind.Stop);
            Assert.Equal(ExecutionState.FILLED, rig.Gw.Requests.Get(stop.RequestId)!.State);
        }
    }

    /// <summary>
    /// THE WORST MINUTE THE FOUNDING ENVELOPES ALLOW: fifteen runs, five pairs by three, signalling together. Fifteen entries and thirty protections inside the
    /// sliding minute is what the figure was set to, and every one goes out.
    /// </summary>
    [Fact]
    public async Task Fifteen_runs_entering_together_all_keep_their_stop_and_target_under_the_founding_policy()
    {
        await using var rig = await Founded(runsPerPair: 3);
        for (var m = 1; m <= 4; m++) await rig.Minute(m);
        Show(rig, "after four minutes");
        EveryRunIsProtected(rig, 15);
    }

    /// <summary>A figure the board decided, written as its act writes one (the precondition <c>FoundingOrderLimitTests</c> writes the same way).</summary>
    static void BoardDecides(Database db, int rate)
    {
        var inForce = new BoardReader(db).InForce();
        var risk = inForce.Policy.Risk;
        risk.MaxOrdersPerMinute = rate;
        risk.InstrumentAllowlist = [.. Pairs.Select(p => p.Pair)];
        var body = BoardPolicyBody.Canonical(inForce.Policy with { Risk = risk });
        var act = $"bact-seed{inForce.Version + 1}";
        var at = Sql.T(Start);
        db.Write(_ =>
        {
            using (var c = db.Cmd("INSERT INTO board_act(id, kind, seat, attempt_id, intent, intent_sha256, policy_version, rationale, refs, evidence_class, expires_at, at) "
                                  + "VALUES($id, 'narrow', 'board-3', NULL, '[]', $sha, $v, 'the test seeds this version', '[]', NULL, NULL, $at)",
                       ("$id", act), ("$sha", Sha256Hex.Of("[]")), ("$v", inForce.Version), ("$at", at)))
                c.ExecuteNonQuery();
            using var d = db.Cmd("INSERT INTO board_policy(version, decider, basis, act_id, written_at, mode, live_activated, body, body_sha256, supersedes) "
                                 + "VALUES($next, 'board', 'act', $act, $at, 'PAPER', 0, $body, $sha, $v)",
                ("$next", inForce.Version + 1), ("$act", act), ("$at", at), ("$body", body), ("$sha", Sha256Hex.Of(body)), ("$v", inForce.Version));
            return d.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// THE FIGURE IS THE RUNS' AND NOBODY ELSE'S (D20): the row stays six and shared across modes, and every caller and every mode but a paper run's own order in
    /// PAPER reads it. The policy has no field that holds forty-five, so no act, verb or seat has anything to widen.
    /// </summary>
    [Fact]
    public async Task Every_other_caller_and_every_other_mode_stay_at_six()
    {
        await using var rig = await Founded();
        var run = rig.Gw.Deployments.All()[0];

        Assert.Equal(6, BoardFounding.V1.Risk.MaxOrdersPerMinute);
        Assert.Equal(6, rig.Gw.Policy.Risk.MaxOrdersPerMinute);
        Assert.Equal(BoardFounding.FoundingOpenPositions * 3, BoardFounding.PaperRunOrdersPerMinute);
        Assert.Equal(45, rig.Gw.MaxOrdersPerMinuteFor(run.Id));

        // NO OTHER CALLER: nobody at all, and an id that is not a run's.
        Assert.Equal(6, rig.Gw.MaxOrdersPerMinuteFor(null));
        Assert.Equal(6, rig.Gw.MaxOrdersPerMinuteFor(""));
        Assert.Equal(6, rig.Gw.MaxOrdersPerMinuteFor("no-such-run"));

        // NO OTHER MODE.
        foreach (var mode in Enum.GetValues<TradingMode>().Where(m => m != TradingMode.PAPER))
        {
            rig.Gw.SetModeUnderTest(mode);
            Assert.Equal(6, rig.Gw.MaxOrdersPerMinuteFor(run.Id));
        }
        rig.Gw.SetModeUnderTest(TradingMode.PAPER);

        // NOTHING ON THE RECORD TO REACH: the founding body says six and no more.
        var fields = new BoardReader(rig.Db).InForce().Fields;
        Assert.Equal("6", Assert.Single(fields, f => f.Name == "risk.max_orders_per_minute").Value);
        Assert.DoesNotContain(fields, f => f.Value == "45");
        Assert.DoesNotContain("45", BoardPolicyBody.Canonical(BoardFounding.V1).Replace("\"max_notional_per_order\"", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>
    /// The owner's own buy of 0.001 BTC, the caller that is not a run which this account takes (an agent's unattributed order is refused here by the envelope's
    /// reservation, whatever the minute holds), and what it was answered: null when it was taken.
    /// </summary>
    static async Task<string?> NotARun(Rig rig, int n)
    {
        try
        {
            await rig.Gw.PlaceAsync(AgentContext.Operator, $"not-a-run-{n}", TestEnv.Buy("BTCUSDT", 0.001m));
            return null;
        }
        catch (GatewayDeniedException e)
        {
            return $"{e.Code}: {e.Message}";
        }
    }

    /// <summary>
    /// A BURST OF RUNS DOES NOT TAKE ANOTHER CALLER'S SIX (U-rate-limits; D20's "every other caller stays at 6"). Under the founding rate a run's own order and every
    /// other caller's are two counts in the one sliding minute: five runs' entries and protections inside it, and the next caller still has its six, and is refused
    /// at the seventh in the same words. RED at the base, one count for both: the first order refused, <c>RISK_LIMIT_EXCEEDED: 6 orders per minute is the limit</c>,
    /// for up to a minute after the runs signalled.
    ///
    /// <para><b>Changed in the open.</b> This was <c>A_caller_that_is_not_a_run_is_still_refused_at_six_with_the_runs_orders_in_the_minute</c>, which held the one
    /// shared count: that count is the defect U-paper-t0 carded, and it is gone.</para>
    ///
    /// <para><b>Its caller changed in the open too</b> (<c>U-test-meaning</c>, A8): the old test's caller was an agent (<c>new AgentContext("a")</c>); the six
    /// here are the owner's (<see cref="NotARun"/>), because on this account an agent's unattributed order is refused by the envelope's reservation, so an
    /// agent cannot place six to count. Both callers take the same branch (the count keys on the run id alone). The agent's half is kept as its own row: with
    /// the runs' minute full, an agent's order passes the rate and is refused by the reservation, never by the minute — and that refusal takes none of the
    /// owner's six.</para>
    /// </summary>
    [Fact]
    public async Task A_caller_that_is_not_a_run_keeps_its_six_with_the_runs_orders_in_the_minute()
    {
        await using var rig = await Founded();
        for (var m = 1; m <= 3; m++) await rig.Minute(m);
        Assert.Equal(5, rig.Gw.Deployments.All().Sum(d => rig.Gw.Deployments.OpsOf(d.Id).Count(o => o.Kind == DeploymentOpKind.Entry)));
        EveryRunIsProtected(rig, 5);

        var agent = await Assert.ThrowsAsync<GatewayDeniedException>(() =>
            rig.Gw.PlaceAsync(new AgentContext("a"), "an-agents-not-a-run", TestEnv.Buy("BTCUSDT", 0.001m)));
        log.WriteLine($"the agent: {agent.Code}: {agent.Message}");
        Assert.Equal(ErrorCode.ENVELOPE_ACCOUNT_RESERVED, agent.Code);

        var said = new List<string?>();
        for (var n = 1; n <= 7; n++) said.Add(await NotARun(rig, n));
        for (var n = 0; n < said.Count; n++) log.WriteLine($"order {n + 1}: {said[n] ?? "taken"}");

        Assert.All(said.Take(6), s => Assert.Null(s));
        Assert.Equal("RISK_LIMIT_EXCEEDED: 6 orders per minute is the limit", said[6]);
    }

    /// <summary>
    /// One order of <paramref name="run"/>'s own, a buy of the pair's smallest step, sent the way the runner sends one at the clock's instant, and what it was
    /// answered.
    /// </summary>
    static async Task<string> ARunsOrder(Rig rig, StrategyDeploymentRow run, int n)
    {
        var bar = new DateTimeOffset(rig.Clock.At.UtcDateTime.Date.AddHours(rig.Clock.At.Hour).AddMinutes(rig.Clock.At.Minute), TimeSpan.Zero);
        var id = Deployments.RequestIdFor(run.Id, bar, 100 + n);
        await rig.Gw.RunDeploymentIntentAsync(run, id, DeploymentOpKind.Entry, bar,
            new PlaceIntent(run.Symbol, OrderSide.Buy, OrderType.Market, 0.00001m, null, null, TimeInForce.Day, $"deployment:{run.Id} test")
            {
                StrategyVersionId = run.VersionId
            });
        var op = rig.Gw.Deployments.OpsOf(run.Id).Single(o => o.RequestId == id);
        return op.Answer ?? $"sent, {op.State}";
    }

    /// <summary>
    /// AND THE OTHER WAY ROUND: another caller's six do not come out of the runs' forty-five. Six orders of a caller that is not a run, then forty-five of the
    /// fifteen runs' own at the same instant, three each, and every one goes out; the forty-sixth is refused at the runs' figure, so the forty-five still
    /// binds the runs. RED at the base, one count for both: the fortieth run order refused, <c>45 orders per minute is the limit</c>, with thirty-nine sent.
    /// </summary>
    [Fact]
    public async Task Another_callers_six_do_not_come_out_of_the_runs_minute()
    {
        await using var rig = await Founded(runsPerPair: 3);
        var runs = rig.Gw.Deployments.All();
        Assert.Equal(15, runs.Count);
        var said = new List<string>();
        string? over = null;

        // ALL OF IT AT ONE INSTANT, the minute's close as the tick sees it, before the runner's own pass.
        await rig.Minute(1, beforeTheRunner: async () =>
        {
            for (var n = 1; n <= 6; n++) Assert.Null(await NotARun(rig, n));
            for (var round = 0; round < 3; round++)
                foreach (var run in runs) said.Add(await ARunsOrder(rig, run, said.Count));
            over = await ARunsOrder(rig, runs[0], said.Count);
        });
        for (var n = 0; n < said.Count; n++) log.WriteLine($"run order {n + 1}: {said[n]}");
        log.WriteLine($"run order 46: {over}");

        Assert.Equal(45, said.Count);
        Assert.All(said, s => Assert.StartsWith("sent, ", s, StringComparison.Ordinal));
        Assert.Contains("45 orders per minute is the limit", over, StringComparison.Ordinal);
    }

    /// <summary>
    /// A FIGURE THE BOARD DECIDES IS ONE COUNT FOR EVERY CALLER, AS IT ALWAYS WAS: the two counts are the founding rate's and nothing else's. Under a decided ten, the
    /// runs' orders in the minute are the other callers' too, and the next one is refused at ten.
    /// </summary>
    [Fact]
    public async Task Under_a_figure_the_board_decided_the_minute_is_one_count_for_every_caller()
    {
        await using var rig = await Founded(board: db => BoardDecides(db, 10));
        for (var m = 1; m <= 3; m++) await rig.Minute(m);
        Show(rig, "after three minutes");

        var said = await NotARun(rig, 1);
        log.WriteLine($"the next caller: {said}");
        Assert.Equal("RISK_LIMIT_EXCEEDED: 10 orders per minute is the limit", said);
    }

    /// <summary>
    /// A FIGURE THE BOARD DECIDES BINDS A RUN LIKE EVERY OTHER CALLER. "Founding" is who set the field: a board that went to ten and narrowed to six decided six, and
    /// reading that as the founding value would turn its narrowing into a raise to forty-five; a narrowing to three is three.
    /// </summary>
    [Theory]
    [InlineData(10, 6)]
    [InlineData(3, 3)]
    [InlineData(60, 60)]
    public async Task A_figure_the_board_decided_binds_a_run_like_every_other_caller(int first, int decided)
    {
        await using var rig = await Founded(board: db =>
        {
            BoardDecides(db, first);
            if (first != decided) BoardDecides(db, decided);
        });
        var run = rig.Gw.Deployments.All()[0];
        Assert.Equal(decided, rig.Gw.Policy.Risk.MaxOrdersPerMinute);
        Assert.Equal(decided, rig.Gw.MaxOrdersPerMinuteFor(run.Id));
    }
}
