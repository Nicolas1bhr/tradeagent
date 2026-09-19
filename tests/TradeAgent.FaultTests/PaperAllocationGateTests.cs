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
/// THE PAPER SCOPE OF AN ALLOCATION, AND THE FOUR THINGS IT CANNOT DO.
///
/// <para><b>What this unit added.</b> A version the referee calls <c>paper_eligible</c> can now be
/// allocated to PAPER by app policy, inside an envelope the owner granted once — no press per version,
/// which is the arrow <c>manager-prompt.md</c> § 5 asks for. What must stay exactly as it was is
/// everything about LIVE capital: <c>TradingGateway.Allocate</c> is two presses and still asks
/// <c>Promotions.Standing().IsPromoted</c> and nothing else, allocation rows written before this unit
/// carry no scope and are read as LIVE because each of them is an owner's own press, and a paper row
/// authorises NOTHING in <c>LIVE_CONFIRM</c> or <c>LIVE_AUTONOMOUS</c>, categorically, whatever
/// connector or account it names.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> <c>AllocationFor</c> reading LIVE rows in
/// PAPER mode — which makes a paper dispatch spend the owner's capital ceiling — and the scope facts
/// dropped from the paper id hash, which collapses two envelopes' allocations of one version onto one
/// row and silently leaves the second account unallocated.</para>
///
/// <para>Everything is measured over <see cref="RecordingConnector"/> and the built-in simulator.
/// Nothing here reaches a venue and no real money is involved.</para>
/// </summary>
public class PaperAllocationGateTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset At = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A DIFFERENT THRESHOLD IS A DIFFERENT PROGRAM AND THEREFORE A DIFFERENT VERSION ID. A comment
    /// would NOT be: the id is the hash of the CANONICAL form, which is the whole point of it.
    /// </summary>
    static string ProgramText(int threshold) =>
        $"instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > {threshold}\n";

    /// <summary>A clock this suite owns, so the gateway's own <c>Now</c> is the instant the rows say.</summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
        public override long GetTimestamp() => at.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    /// <summary>A gateway on a simulator both witnesses call a simulation, in practice mode.</summary>
    static async Task<(TradingGateway Gw, RecordingConnector Conn, Database Db)> Ready(
        Action<TradeAgentSettings>? settings = null, string? connectorId = null, Database? db = null)
    {
        db ??= TestEnv.NewDb();
        var conn = new RecordingConnector(new FakeConnector(new FakeBroker()), connectorId);
        var gw = new TradingGateway(db, conn, new HealthRegistry(),
            new GatewayOptions { Clock = new TestClock(At) });
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
    /// A VERSION THAT REALLY CARRIES A VERDICT IN THIS DATABASE — the dataset, the holdout, the
    /// campaign, the version, the holdout run and the promotion row. A faked id would be refused by the
    /// ledger a step earlier and would prove nothing about anything above it.
    /// </summary>
    static string Judged(Database db, string verdict, int threshold = 103)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"paper-envelope-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open($"BTCUSDT 1m v{threshold}", set, 10, 3, At);
        Assert.True(campaign.Ok, campaign.Why);

        var program = StrategyParser.Parse(ProgramText(threshold)).Program!;
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
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        // THE POLICY SHA IS THE ONE THAT PRODUCED THE ANSWER, which is what `Promotions.Standing`
        // re-checks: a paper-eligible row carries PaperV1's, a promotion carries V1's, and a row
        // carrying the other one is invalidated the instant it is read.
        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id,
            verdict == PromotionVerdict.PaperEligible
                ? CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1)
                : CampaignPolicy.Sha256Of(CampaignPolicy.V1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, verdict,
            verdict == PromotionVerdict.PaperEligible ? PromotionReason.MetOnHistory : PromotionReason.Met, At));

        return program.StrategyId;
    }

    static async Task<PaperEnvelopeRow> Envelope(TradingGateway gw, decimal quantity = 5m,
        decimal? notional = null, DateTimeOffset? until = null)
    {
        var granted = await gw.GrantPaperEnvelopeAsync(
            "BTCUSDT", quantity, notional, until ?? At.AddDays(30), At);
        Assert.True(granted.Ok, granted.Why);
        return granted.Envelope!;
    }

    static AllocationRow PaperRowFor(TradingGateway gw, string version, PaperEnvelopeRow envelope,
        decimal? quantity = null, DateTimeOffset? from = null) =>
        new("", version, gw.Promotions.Standing(version).Promotion!.Id, AllocationPolicy.V1,
            quantity ?? envelope.MaxQuantity, envelope.MaxNotional, envelope.Currency,
            from ?? At, null, "app policy: test", At)
        {
            Scope = AllocationScope.Paper,
            ConnectorId = envelope.ConnectorId,
            Mode = TradingMode.PAPER.ToString(),
            AccountId = envelope.AccountId,
            EnvelopeId = envelope.Id
        };

    // ---- item 2: what a paper allocation may be written against --------------------------------

    /// <summary>
    /// (d) THE LIVE PRESS STILL REFUSES A PAPER-ELIGIBLE VERSION, and the whole of this unit is around
    /// it rather than through it.
    ///
    /// <para>This is a GUARD CHECKED rather than a red: <c>Allocations.Record</c> has asked
    /// <c>Standing.IsPromoted</c> and nothing else since <c>U-paper-verdict</c>, and it goes on doing
    /// so. It is here because a unit that teaches this ledger the word "paper" is exactly the unit
    /// during which that question could quietly grow a second arm.</para>
    /// </summary>
    [Fact]
    public async Task The_live_press_still_refuses_a_paper_eligible_version()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var version = Judged(db, PromotionVerdict.PaperEligible);
        var allocated = gw.Allocate(version, 5m, null, "by the account owner");

        log.WriteLine($"live allocation ok   : {allocated.Ok}");
        log.WriteLine($"why                  : {allocated.Why}");

        Assert.False(allocated.Ok);
        Assert.Contains("paper only", allocated.Why, StringComparison.Ordinal);
        Assert.Empty(gw.Allocations.For(version));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// A PAPER ALLOCATION IS REFUSED UNLESS THE VERDICT STANDS, THE ENVELOPE STANDS, AND THE CEILING
    /// IS INSIDE THE ENVELOPE'S. Four refusals and one write, all through the one writer.
    /// </summary>
    [Fact]
    public async Task A_paper_allocation_is_refused_outside_the_verdict_and_the_envelope()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = await Envelope(gw, quantity: 5m);
        var eligible = Judged(db, PromotionVerdict.PaperEligible);
        var refused = Judged(db, PromotionVerdict.Refused, 104);

        var noVerdict = gw.Allocations.RecordPaper(PaperRowFor(gw, refused, envelope), At);
        var overCeiling = gw.Allocations.RecordPaper(
            PaperRowFor(gw, eligible, envelope, quantity: 6m), At);
        var afterExpiry = gw.Allocations.RecordPaper(
            PaperRowFor(gw, eligible, envelope, from: At.AddDays(40)), At.AddDays(40));
        var written = gw.Allocations.RecordPaper(PaperRowFor(gw, eligible, envelope), At);

        log.WriteLine($"refused version      : {noVerdict.Why}");
        log.WriteLine($"over the ceiling     : {overCeiling.Why}");
        log.WriteLine($"after expiry         : {afterExpiry.Why}");
        log.WriteLine($"written              : {written.Ok} — {written.Why}");

        Assert.False(noVerdict.Ok);
        Assert.False(overCeiling.Ok);
        Assert.False(afterExpiry.Ok);
        Assert.True(written.Ok, written.Why);
        Assert.Single(gw.Allocations.For(eligible));
        Assert.Empty(gw.Allocations.For(refused));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// (f) A SECOND VERSION IS REFUSED WHILE THE ENVELOPE'S DEPLOYMENTS ARE FULL, and the version
    /// already in it is not.
    ///
    /// <para><c>max_deployments</c> is 1 for now, so an envelope carries one experiment at a time. The
    /// refusal is about OTHER versions only: re-recording the one that is already in the envelope — a
    /// restart, a second sweep — writes the row that is already there and is not a second deployment.</para>
    /// </summary>
    [Fact]
    public async Task A_second_version_is_refused_while_max_deployments_is_full()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = await Envelope(gw);
        var first = Judged(db, PromotionVerdict.PaperEligible);
        var second = Judged(db, PromotionVerdict.PaperEligible, 105);

        Assert.True(gw.Allocations.RecordPaper(PaperRowFor(gw, first, envelope), At).Ok);
        var again = gw.Allocations.RecordPaper(PaperRowFor(gw, first, envelope), At);
        var crowded = gw.Allocations.RecordPaper(PaperRowFor(gw, second, envelope), At);

        log.WriteLine($"same version again   : {again.Ok} — {again.Why}");
        log.WriteLine($"second version       : {crowded.Ok} — {crowded.Why}");

        Assert.True(again.Ok, again.Why);
        Assert.False(crowded.Ok);
        Assert.Empty(gw.Allocations.For(second));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE LIVE WRITER REFUSES A PAPER SCOPE, AND A ROW WITH NO SCOPE AT ALL IS LIVE.
    ///
    /// <para>Two halves of one sentence. <c>Allocations.Record</c> is the owner's two presses and
    /// writes live capital only — a caller handing it a paper scope is refused rather than quietly
    /// granted — and every allocation row written before this unit carries NULL in all five new
    /// columns and is read as LIVE, because each of them is a press the owner made on the Safety page
    /// and a press is never a paper grant.</para>
    /// </summary>
    [Fact]
    public async Task The_live_writer_refuses_a_paper_scope_and_a_row_with_no_scope_is_live()
    {
        var (gw, _, db) = await Ready();
        using var _1 = db;

        var envelope = await Envelope(gw);
        var promoted = Judged(db, PromotionVerdict.Promoted);

        var refused = gw.Allocations.Record(PaperRowFor(gw, promoted, envelope));
        Assert.False(refused.Ok);
        log.WriteLine($"live writer          : {refused.Why}");

        var live = gw.Allocate(promoted, 3m, null, "by the account owner");
        Assert.True(live.Ok, live.Why);

        // WHAT AN OLD ROW LOOKS LIKE: the five columns emptied, which is what every allocation written
        // before this rung genuinely carries.
        db.Write(_ =>
        {
            using var c = db.Cmd(
                "UPDATE strategy_allocation SET scope=NULL, connector_id=NULL, mode=NULL, "
                + "account_id=NULL, envelope_id=NULL WHERE id=$id", ("$id", live.Allocation!.Id));
            return c.ExecuteNonQuery();
        });

        var standing = gw.Allocations.StandingForLive(promoted, At);
        log.WriteLine($"scope of the old row : {standing!.Allocation.Scope ?? "null"}");
        log.WriteLine($"read as              : {standing.Allocation.EffectiveScope}");

        Assert.False(standing.Allocation.IsPaper);
        Assert.Equal(AllocationScope.Live, standing.Allocation.EffectiveScope);
        Assert.Null(gw.Allocations.StandingForPaper(promoted, gw.Connector.Id, envelope.AccountId, At));
        await gw.DisposeAsync();
    }
}
