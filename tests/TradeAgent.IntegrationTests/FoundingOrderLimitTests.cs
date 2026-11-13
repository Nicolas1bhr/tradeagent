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
/// THE FOUNDING QUANTITY LIMIT FITS THE ENVELOPES (R30 D18; R28 P2). Under the founding policy as it ships, on the app's own paper venue with the real
/// runner: a full-size entry on SOL, BNB and XRP fills, where the flat 1 refused every one; an order over the run's share is still refused; the 5,000 USDT
/// per pair still binds; and nothing outside a paper run's own order moved. Nothing reaches a venue and no real money is involved.
/// </summary>
public class FoundingOrderLimitTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Start = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The 2026 levels R28 sized against, in USDT.</summary>
    static readonly (string Pair, decimal Price)[] Pairs = [("SOLUSDT", 200m), ("BNBUSDT", 1_000m), ("XRPUSDT", 2m)];

    sealed class Clock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset At { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => At;
        public override long GetTimestamp() => At.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>The catalogue as it stands when <paramref name="verified"/> are the instruments the app has checked.</summary>
    static VenueCatalogRead Catalogue(params string[] verified) => new(
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
                .. verified.Select(p => new VenueInstrumentEntry
                {
                    Symbol = p, TickSize = 0.0001m, QuantityIncrement = 0.001m, Source = "declared by this test", Verified = true
                })
            ]
        }
    ], null);

    static string Program(string pair) =>
        $"instrument {pair}\ntimezone UTC\ntimeframe 1m\ndata_freshness 5m\nmax_decision_age 5m\nsize capital_fraction 1\n"
        + "exit when close < 0.5\nentry when close > 0.5\n";

    sealed class Rig : IAsyncDisposable
    {
        public required Database Db { get; init; }
        public required PaperConnector Conn { get; init; }
        public required TradingGateway Gw { get; init; }
        public required ForwardRuns Runner { get; init; }
        public required Clock Clock { get; init; }
        public required ForwardBarStore Bars { get; init; }
        public required ForwardBarSource Source { get; init; }

        /// <summary>One closed minute on every pair, at its price, the way the collector stores one.</summary>
        public void Minute(int minute)
        {
            var open = Start.AddMinutes(minute);
            var close = open + ForwardBars.BarLength;
            Clock.At = close;
            foreach (var (pair, price) in Pairs)
            {
                var append = Bars.Append(new ForwardFetchAttempt
                {
                    Source = ForwardBars.Source, Symbol = pair,
                    Url = "https://example.invalid/klines (this test wrote the rows; nothing was fetched)",
                    RequestedAt = close, ReceivedAt = close.AddSeconds(1), HttpStatus = 200
                }, [new ForwardBars.Kline(open, price, price, price, price, 10m, close)]);
                Assert.Equal(1, append.Stored);
            }

            Clock.At = close.AddSeconds(1);
            foreach (var (pair, _) in Pairs) Source.Announce(pair, open);
        }

        public StrategyDeploymentRow RunOn(string pair) => Gw.Deployments.All().Single(d => d.Symbol == pair);

        public async ValueTask DisposeAsync()
        {
            await Gw.DisposeAsync();
            await Conn.DisposeAsync();
        }
    }

    /// <summary>
    /// A VERSION WRITTEN AS A BOARD ACT'S, with raw SQL: the precondition the board tests write the same way. The allowlist names the pairs, because
    /// the founding allowlist's reach over the venue's own list ends with the founding record.
    /// </summary>
    static void BoardDecides(Database db, decimal orderLimit)
    {
        var inForce = new BoardReader(db).InForce();
        var risk = inForce.Policy.Risk;
        risk.MaxOrderQuantity = orderLimit;
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

    /// <summary>A home as the founding policy leaves it: its envelopes granted by the first health pass, each pair's one judged version on paper.</summary>
    static async Task<Rig> Founded(Action<Database>? board = null)
    {
        var db = TestEnv.NewDb();
        board?.Invoke(db);
        var clock = new Clock(Start);
        var bars = new ForwardBarStore(db);
        var source = new ForwardBarSource(bars);

        // THE CATALOGUE GROWS AFTER THE GATEWAY'S FIRST LOOK, AS A REAL INSTALL'S DOES (PREFLIGHT-2 blocker 1): BTCUSDT is verified at onboarding and the other
        // pairs by setup's press. A catalogue handed over whole here is how this class passed over four pairs that could not trade until a restart;
        // PaperPairsAfterSetupTests walks that path through the app's own press.
        var catalogue = Catalogue("BTCUSDT");
        var conn = new PaperConnector(new PaperConnectorOptions
        {
            Source = source,
            Clock = () => clock.At,
            BookFile = Path.Combine(TestEnv.Home, $"paper-run-{Guid.NewGuid():n}.db"),
            CatalogueNow = () => catalogue
        });
        var gw = new TradingGateway(db, conn, new HealthRegistry(), new GatewayOptions { Clock = clock, MaxQuoteAge = TimeSpan.FromDays(3650) });
        gw.Update(s => s.SelectedAccountId = PaperConnector.TheAccount);
        new VenueStore(db).Sync(catalogue);
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();

        catalogue = Catalogue([.. Pairs.Select(p => p.Pair)]);
        new VenueStore(db).Sync(catalogue);

        foreach (var (pair, _) in Pairs) ForwardRunnerTests.Judged(db, Program(pair), Start, pair);
        Assert.Equal(Pairs.Length, gw.AllocatePaperDue(Start));
        Assert.Equal(Pairs.Length, gw.StartPaperDeploymentsDue(Start));

        return new Rig
        {
            Db = db, Conn = conn, Gw = gw, Clock = clock, Bars = bars, Source = source,
            Runner = new ForwardRuns(gw, db, () => clock.At)
        };
    }

    int _asked;

    /// <summary>What the gateway answered an order of the run's for <paramref name="quantity"/>, written down under the run's own id.</summary>
    async Task<string> Asked(Rig rig, string pair, decimal quantity)
    {
        var run = rig.RunOn(pair);
        var bar = Start.AddMinutes(1);
        var id = Deployments.RequestIdFor(run.Id, bar, ++_asked);
        await rig.Gw.RunDeploymentIntentAsync(run, id, DeploymentOpKind.Entry, bar,
            new PlaceIntent(pair, OrderSide.Buy, OrderType.Market, quantity, null, null, TimeInForce.Day, $"deployment:{run.Id} test")
            {
                StrategyVersionId = run.VersionId
            });
        var said = rig.Gw.Deployments.OpsOf(run.Id).Single(o => o.RequestId == id).Answer ?? "(no answer)";
        log.WriteLine($"{pair} {quantity}: {said}");
        return said;
    }

    /// <summary>
    /// A NORMAL ENTRY ON SOL, BNB AND XRP PASSES (D18): a full-size entry, its whole capital of 1,666.66 USDT at the 2026 levels, reaches the ledger as a
    /// fill on each pair, through every gate an order meets, with the runner choosing the size.
    ///
    /// <para>RED at the base: <c>quantity 8.333 exceeds the limit of 1</c>, and no run on these three pairs ever filled.</para>
    /// </summary>
    [Fact]
    public async Task Normal_sized_entries_on_sol_bnb_and_xrp_fill_under_the_founding_policy()
    {
        await using var rig = await Founded();

        rig.Minute(1);
        await rig.Runner.AdvanceAsync();
        rig.Minute(2);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Minute(3);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        foreach (var (pair, price) in Pairs)
        {
            var run = rig.RunOn(pair);
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id)) log.WriteLine($"{pair} {op.Kind} {op.State}: {op.Answer}");

            var fill = Assert.Single(rig.Gw.Deployments.FillsOf(run));
            log.WriteLine($"{pair}: {fill.Quantity} at {fill.Price}");
            Assert.InRange(fill.Quantity, 0.99m * 1_666.66666666m / price, 1_666.66666666m / price);
            Assert.True(fill.Quantity > BoardFounding.V1.Risk.MaxOrderQuantity);
        }
    }

    /// <summary>
    /// AN ORDER OVER THE RUN'S SHARE IS STILL REFUSED, AND THE VALUE STILL BINDS BELOW IT (D18): quantity ceilings stay ceilings. SOL's share of 50 is 16.66…
    /// and an order of 17 is refused by the per-order limit, in its words, on every pair. Under that share, 10 SOL at 200 is worth 2,000 USDT, over the run's
    /// 1,666.66 of the pair's 5,000, and is refused by the capital gate.
    /// </summary>
    [Fact]
    public async Task An_order_over_the_share_is_refused_and_the_notional_still_binds()
    {
        await using var rig = await Founded();
        rig.Minute(1);

        foreach (var (pair, quantity, limit) in new[]
                 {
                     ("SOLUSDT", 17m, 16.66666666m), ("BNBUSDT", 3.4m, 3.33333333m), ("XRPUSDT", 1_700m, 1_666.66666666m)
                 })
            Assert.Contains($"exceeds the limit of {limit}", await Asked(rig, pair, quantity), StringComparison.Ordinal);

        var overValue = await Asked(rig, "SOLUSDT", 10m);
        Assert.Contains("the capital allocated to it is", overValue, StringComparison.Ordinal);
        Assert.Empty(rig.Gw.Deployments.FillsOf(rig.RunOn("SOLUSDT")));
    }

    /// <summary>
    /// A FIGURE THE BOARD DECIDES IS READ PER PAIR, IN THE FOUNDING FIGURE'S OWN UNIT (U-rate-limits; D18): the founding 1 is one run's share of its pair, so a
    /// board's figure is that many shares, and never more than the one its allocation holds. A raise to 2 for every other caller is no narrowing of any run:
    /// a full-size entry on SOL, BNB and XRP still fills. RED at the base, the one figure for every pair: <c>quantity 8.333 exceeds the limit of 2</c>, no fill.
    /// </summary>
    [Fact]
    public async Task A_figure_the_board_raised_does_not_cut_a_normal_entry_on_any_pair()
    {
        await using var rig = await Founded(db => BoardDecides(db, 2m));
        Assert.Equal(2m, rig.Gw.Policy.Risk.MaxOrderQuantity);

        rig.Minute(1);
        await rig.Runner.AdvanceAsync();
        rig.Minute(2);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();
        rig.Minute(3);
        await rig.Gw.RefreshHealthAsync();
        await rig.Runner.AdvanceAsync();

        foreach (var (pair, price) in Pairs)
        {
            var run = rig.RunOn(pair);
            foreach (var op in rig.Gw.Deployments.OpsOf(run.Id)) log.WriteLine($"{pair} {op.Kind} {op.State}: {op.Answer}");
            var fill = Assert.Single(rig.Gw.Deployments.FillsOf(run));
            Assert.InRange(fill.Quantity, 0.99m * 1_666.66666666m / price, 1_666.66666666m / price);
        }
    }

    /// <summary>
    /// AND A NARROWING NARROWS EVERY RUN BY THE SAME PART OF ITS OWN SHARE: a board's 0.5 is half of each run's share, 8.33333333 SOL, 1.66666666 BNB and
    /// 833.33333333 XRP, rounded down, where the one figure gave 0.5 of each. Over it is refused in the limit's words; under it the order goes on to the capital
    /// gate. Every other caller's limit is the figure itself, as before. RED at the base: <c>quantity 8 exceeds the limit of 0.5</c>.
    /// </summary>
    [Fact]
    public async Task A_figure_the_board_narrowed_narrows_each_runs_share_by_the_same_part()
    {
        await using var rig = await Founded(db => BoardDecides(db, 0.5m));
        rig.Minute(1);

        foreach (var (pair, over, limit) in new[]
                 {
                     ("SOLUSDT", 8.4m, 8.33333333m), ("BNBUSDT", 1.7m, 1.66666666m), ("XRPUSDT", 834m, 833.33333333m)
                 })
            Assert.Contains($"exceeds the limit of {limit}", await Asked(rig, pair, over), StringComparison.Ordinal);

        Assert.DoesNotContain("exceeds the limit", await Asked(rig, "SOLUSDT", 8m), StringComparison.Ordinal);

        var owner = await Assert.ThrowsAsync<GatewayDeniedException>(() => rig.Gw.PlaceAsync(AgentContext.Operator, "owner-order",
            new PlaceIntent("SOLUSDT", OrderSide.Buy, OrderType.Market, 0.6m, null, null, TimeInForce.Day, null)));
        Assert.Contains("exceeds the limit of 0.5", owner.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A BOARD THAT SETTLES BACK ON 1 GIVES A RUN ITS SHARE AND NO MORE (the Codex review's P1, still held): 1 decided after 2 is no raise over what 2 gave, and
    /// an order over the share is refused at the share. With a figure read past the share (the raise not capped), 17 SOL would pass this limit.
    ///
    /// <para><b>Changed in the open.</b> This was <c>A_figure_the_board_decided_binds_a_runs_opener_even_when_it_equals_the_founding_one</c>, which held a run's
    /// opener to the board's 1 itself, 2 SOL refused: under one figure for every pair that refused every normal entry on SOL, BNB and XRP (U-paper-closure's
    /// card). The figure is now read per pair (D18's unit: the founding 1 is one run's share), so the 1 is one share; what P1 protects, that settling on a
    /// figure is never a raise over the one before it, holds because the reading only grows with the figure and stops at the share.</para>
    /// </summary>
    [Fact]
    public async Task A_board_that_settles_back_on_1_gives_a_run_its_share_and_no_more()
    {
        await using var rig = await Founded(db =>
        {
            BoardDecides(db, 2m);
            BoardDecides(db, BoardFounding.V1.Risk.MaxOrderQuantity);
        });
        rig.Minute(1);

        Assert.Equal(BoardFounding.V1.Risk.MaxOrderQuantity, rig.Gw.Policy.Risk.MaxOrderQuantity);
        Assert.Contains("exceeds the limit of 16.66666666", await Asked(rig, "SOLUSDT", 17m), StringComparison.Ordinal);
        Assert.DoesNotContain("exceeds the limit", await Asked(rig, "SOLUSDT", 2m), StringComparison.Ordinal);
    }

    /// <summary>
    /// UNDER A RAISE THE SHARE STILL BINDS: a board's 2 is two shares for nobody; an order over one run's share is refused at the share.
    /// </summary>
    [Fact]
    public async Task Under_a_figure_the_board_raised_an_order_over_the_share_is_refused_at_the_share()
    {
        await using var rig = await Founded(db => BoardDecides(db, 2m));
        rig.Minute(1);
        Assert.Contains("exceeds the limit of 16.66666666", await Asked(rig, "SOLUSDT", 17m), StringComparison.Ordinal);
        Assert.Contains("exceeds the limit of 1666.66666666", await Asked(rig, "XRPUSDT", 1_700m), StringComparison.Ordinal);
    }

    /// <summary>
    /// A RUN CAN ALWAYS CLOSE WHAT IT HOLDS (the Codex review's P2): a run long 8 SOL when the limit is narrowed to 0.5, then stopped, sends its one close of
    /// 8 and it fills. RED with the close held to the narrowed figure: refused before the wire, every minute, and the slot never freed.
    /// </summary>
    [Fact]
    public async Task A_run_closes_what_it_holds_after_the_limit_is_narrowed_below_it()
    {
        await using var rig = await Founded();
        for (var minute = 1; minute <= 3; minute++)
        {
            rig.Minute(minute);
            await rig.Gw.RefreshHealthAsync();
            await rig.Runner.AdvanceAsync();
        }

        var run = rig.RunOn("SOLUSDT");
        var held = Assert.Single(rig.Gw.Deployments.FillsOf(run)).Quantity;

        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrderQuantity = 0.5m);
        await rig.Gw.StopPaperDeploymentAsync(run.Id, "the account owner");

        for (var minute = 4; minute <= 5; minute++)
        {
            rig.Minute(minute);
            await rig.Gw.RefreshHealthAsync();
        }

        var close = rig.Gw.Deployments.OpsOf(run.Id).Last(o => o.Kind == DeploymentOpKind.Flatten);
        log.WriteLine($"the close of {held}: {close.State} {rig.Gw.GetRequest(close.RequestId)?.State} {close.Answer}");
        Assert.Equal(ExecutionState.FILLED, rig.Gw.GetRequest(close.RequestId)!.State);
        Assert.Equal(0m, rig.Gw.Deployments.BookOf(run).Held);
    }

    /// <summary>
    /// NOTHING ELSE MOVED: another caller's order on the same pair is bound by the flat 1, the owner's included, and a figure other than the founding one
    /// binds a run too, by the same part of its share. The per-pair limit is a paper run's own, and it is a way toward nothing.
    ///
    /// <para><b>Changed in the open.</b> A figure moved to 0.5 held the run's opener to 0.5 itself (<c>exceeds the limit of 0.5</c> for 2 SOL); it is now half
    /// the run's share, 8.33333333 SOL (U-rate-limits, the board's figure read per pair, D18's unit), so the order over it is 9.</para>
    /// </summary>
    [Fact]
    public async Task The_flat_limit_stays_on_every_other_caller_and_a_decided_figure_binds_a_run()
    {
        await using var rig = await Founded();
        rig.Minute(1);

        var owner = await Assert.ThrowsAsync<GatewayDeniedException>(() => rig.Gw.PlaceAsync(AgentContext.Operator, "owner-order",
            new PlaceIntent("SOLUSDT", OrderSide.Buy, OrderType.Market, 2m, null, null, TimeInForce.Day, null)));
        log.WriteLine($"the owner's 2 SOL: {owner.Message}");
        Assert.Contains("exceeds the limit of 1", owner.Message, StringComparison.Ordinal);

        rig.Gw.UpdateRiskUnderTest(r => r.MaxOrderQuantity = 0.5m);
        Assert.Contains("exceeds the limit of 8.33333333", await Asked(rig, "SOLUSDT", 9m), StringComparison.Ordinal);
    }
}
