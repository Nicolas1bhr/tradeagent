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
/// U-data-licence (b) — A LIVE ALLOCATION WHOSE EVIDENCE IS RESEARCH-ONLY OPENS NOTHING, AND STILL CLOSES.
///
/// <para><b>Why.</b> Binance's archive is under the Binance Vision Dataset Terms v1.0, whose § 4.2 forbids
/// live proprietary trading execution on it, and the orchestrator's COMPLY decision of 2026-10-04 is that no
/// live eligibility rests on it. An allocation of capital written before the evidence under it was read as
/// research-only must stop authorising at once — the standing is computed when the order arrives, never
/// copied onto the allocation — and the live arm of <c>AllocationStanding.Authorises</c> is where that is
/// asked: <c>IsPromoted</c> and a null <c>LiveRefusal</c>, read in a live mode and in practice mode off an
/// envelope. The refusal is <c>ALLOCATION_NONE</c> with the sentence, nothing is sent, and the allocation row
/// stays on the table. A close and a reduce ALWAYS pass, for the loss budget's reason: a gate that stopped a
/// position being flattened would be a trap.</para>
///
/// <para>Measured at the WIRE, over <see cref="RecordingConnector"/> and the simulator, in practice mode —
/// the arm <c>AllocationGateTests</c> measures the capital ceiling on. Nothing reaches a venue.</para>
///
/// <para>RED before the gate: the order after the narrowing came back <c>ok — FILLED</c> and the connector
/// saw a second place call. The mutant this watches is the live arm back to <c>IsPromoted</c> alone.</para>
/// </summary>
public class LiveLicenceGateTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready()
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
        });
        await conn.ConnectAsync();
        await gw.RefreshHealthAsync();
        return (gw, conn, db);
    }

    /// <summary>
    /// A VERSION THAT REALLY STANDS PROMOTED over FIRST-PARTY evidence under a source no reading names
    /// (<see cref="TestEnv.FirstParty"/>), so its capital is allocated and authorises — until the row says
    /// otherwise.
    /// </summary>
    static (string Version, long DatasetId) PromotedVersion(Database db)
    {
        var at = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"live-licence-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, TestEnv.FirstPartySource, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, at, DatasetState.ACCEPTED, null, []) { Licence = TestEnv.FirstParty });
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

        return (program.StrategyId, id);
    }

    static PlaceIntent By(string version, decimal qty = 1m,
        ConnectorSdk.OrderSide side = ConnectorSdk.OrderSide.Buy,
        ConnectorSdk.OrderIntent intent = ConnectorSdk.OrderIntent.Open) =>
        new("ES", side, ConnectorSdk.OrderType.Market, qty, null, null, ConnectorSdk.TimeInForce.Day, null)
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

    /// <summary>
    /// (b) THE ALLOCATION OPENS ON FIRST-PARTY EVIDENCE; THE ROW IS NARROWED TO RESEARCH-ONLY BY RAW SQL; THE
    /// NEXT OPENING ORDER IS REFUSED IN WORDS AND NOTHING IS SENT; A CLOSE STILL GOES OUT.
    /// </summary>
    [Fact]
    public async Task A_live_allocation_on_research_only_evidence_opens_nothing_and_still_closes()
    {
        var (gw, conn, db) = await Ready();
        using var _1 = db;

        var (version, datasetId) = PromotedVersion(db);
        var allocated = gw.Allocate(version, 5m, null, "test");
        Assert.True(allocated.Ok, allocated.Why);

        // THE CONTROL: on first-party evidence the allocation authorises an opening order.
        var opened = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "licence-open", By(version)));
        Assert.StartsWith("ok", opened, StringComparison.Ordinal);
        Assert.Equal(1, conn.Places);

        // THE ROW NARROWED AFTERWARDS: its bars now read as having come under the archive's terms.
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE dataset SET licence_class=$class, terms_url=$url, terms_version=$version, terms_read_on=$read
                 WHERE id=$id
                """, ("$class", DataLicence.ResearchOnly), ("$url", DataLicence.ArchiveTermsUrl),
                ("$version", DataLicence.ArchiveTermsVersion), ("$read", DataLicence.ArchiveTermsReadOn),
                ("$id", datasetId));
            return c.ExecuteNonQuery();
        });

        var refused = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "licence-refused", By(version)));

        log.WriteLine($"opening order after  : {refused}");
        log.WriteLine($"allocation row still : {gw.Allocations.For(version).Count}");
        log.WriteLine($"connector place calls: {conn.Places}");

        Assert.StartsWith(ErrorCode.ALLOCATION_NONE.ToString(), refused, StringComparison.Ordinal);
        Assert.Contains("REFUSED FOR LIVE", refused, StringComparison.Ordinal);
        Assert.Contains($"dataset {datasetId} ({TestEnv.FirstPartySource} BTCUSDT 1m v1)", refused,
            StringComparison.Ordinal);
        Assert.Contains(DataLicence.ResearchOnly, refused, StringComparison.Ordinal);
        Assert.Contains(DataLicence.ArchiveTermsUrl, refused, StringComparison.Ordinal);
        Assert.Contains("backtests and paper go on", refused, StringComparison.Ordinal);
        Assert.Equal(1, conn.Places);
        Assert.Null(gw.GetRequest("licence-refused"));
        Assert.Single(gw.Allocations.For(version));

        // A CLOSE STILL GOES OUT: nothing about evidence may stop a position being flattened.
        var closed = await SwallowAsync(gw.PlaceAsync(new AgentContext("a"), "licence-close",
            By(version, side: ConnectorSdk.OrderSide.Sell, intent: ConnectorSdk.OrderIntent.Close)));

        log.WriteLine($"close                : {closed}");
        log.WriteLine($"connector place calls: {conn.Places}");

        Assert.StartsWith("ok", closed, StringComparison.Ordinal);
        Assert.Equal(2, conn.Places);
        await gw.DisposeAsync();
    }
}
