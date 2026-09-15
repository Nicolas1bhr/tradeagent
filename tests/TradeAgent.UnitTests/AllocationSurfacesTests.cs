using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 5 — THE OWNER AND THE ROLES ARE TOLD WHAT IS ALLOCATED.
///
/// <para><b>What was wrong before this.</b> Section 4 of the owner's report is the capital section and
/// said nothing about capital allocated to a strategy, because none could be; the Situation's promoted
/// line told a turn a version was promoted and nothing about whether it could execute anything at all.
/// A turn planning a deployment the gateway would refuse with <c>ALLOCATION_NONE</c> is a turn spent
/// on nothing.</para>
///
/// <para><b>The mutant this class exists to catch.</b> A standing allocation whose PROMOTION has since
/// been withdrawn, printed as though it were live. The row is still on the table and the version may
/// trade nothing, so an unmarked line tells the owner their capital is working when it is not — and
/// tells the agent it may plan against it.</para>
/// </summary>
public class AllocationSurfacesTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    const string ProgramText = "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    /// <summary>
    /// THE SNAPSHOT INSTANT, WHICH IS NOW AND NOT A FIXED HOUR. An allocation takes effect at the
    /// instant the owner pressed, so a report composed at a midday that has already passed would ask
    /// the ledger what stood BEFORE the press — and be told, correctly, nothing.
    /// </summary>
    static DateTimeOffset Snapshot() => DateTimeOffset.Now;

    /// <summary>A version that really stands promoted in this database, with everything it points at.</summary>
    static (string Version, long DatasetId) PromotedVersion(Database db)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"allocation-surface-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open("BTCUSDT 1m v1", set, 10, 3, At);
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
            12m, 2m, 10m, 3m, "trace-sha", At, Referee.RunRole, null), []);

        new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id, campaign.Campaign.ScoringPolicySha256,
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, PromotionVerdict.Promoted, PromotionReason.Met, At));

        return (program.StrategyId, set.Id);
    }

    /// <summary>
    /// SECTION 4 NAMES EVERY STANDING ALLOCATION — the version, the ceiling, the currency, the instant
    /// it took effect and the policy that was applied.
    ///
    /// <para>RED before this unit: an allocation stood and the report was silent —
    /// <c>Assert.Contains() Failure: Sub-string not found / Not found: "allocated:"</c>.</para>
    /// </summary>
    [Fact]
    public async Task Section_four_names_a_standing_allocation_with_its_ceiling_and_policy()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var (version, _) = PromotedVersion(db);
        var allocated = gw.Allocate(version, 3m, 250_000m, "allocated by the account owner");
        Assert.True(allocated.Ok, allocated.Why);

        var text = DailyReportText.Render(gw.Reports.Compose(Snapshot()));

        Assert.Contains("- allocated: " + version[..12], text, StringComparison.Ordinal);
        Assert.Contains("up to 3", text, StringComparison.Ordinal);
        Assert.Contains("policy " + AllocationPolicy.V1, text, StringComparison.Ordinal);
        Assert.Contains(gw.AccountCurrency, text, StringComparison.Ordinal);
        Assert.DoesNotContain("WITHDRAWN", text, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND AN INSTALLATION THAT HAS ALLOCATED NOTHING SAYS SO. "none" rather than an omitted line: a
    /// silent section reads as "no allocations exist" and as "this build does not know" identically.
    /// </summary>
    [Fact]
    public async Task Section_four_says_none_when_no_capital_is_allocated()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var text = DailyReportText.Render(gw.Reports.Compose(Snapshot()));

        Assert.Contains("- allocated: none", text, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE MUTANT: A WITHDRAWN ALLOCATION PRINTED AS A LIVE ONE.
    ///
    /// <para>The capital was allocated while the promotion stood; the holdout dataset is then rejected,
    /// so <c>Promotions.Standing</c> reads <c>invalidated</c> and the gateway refuses every order that
    /// version places. The row is still on the table, so the report must list it AND mark it. Printed
    /// without the mark, the line reads exactly like the live one above — the owner is told their
    /// capital is working when the version can trade nothing.</para>
    /// </summary>
    [Fact]
    public async Task An_allocation_whose_promotion_was_withdrawn_is_listed_and_marked()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var (version, datasetId) = PromotedVersion(db);
        Assert.True(gw.Allocate(version, 3m, null, "allocated by the account owner").Ok);
        new DatasetStore(db).Reject(datasetId, "a raw archive file changed under it");

        var text = DailyReportText.Render(gw.Reports.Compose(Snapshot()));

        Assert.Contains("- allocated: " + version[..12], text, StringComparison.Ordinal);
        Assert.Contains("WITHDRAWN: its promotion no longer stands, so it may trade nothing",
            text, StringComparison.Ordinal);
        Assert.Contains("REJECTED", text, StringComparison.Ordinal);
        Assert.Single(gw.Allocations.For(version));
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE SITUATION'S PROMOTED LINE SAYS WHAT IS ALLOCATED, AND CARRIES NO HOLDOUT FIGURE. A turn told
    /// only "promoted" would plan a deployment the gateway refuses with <c>ALLOCATION_NONE</c>.
    /// </summary>
    [Fact]
    public async Task The_promoted_line_says_what_is_allocated_and_no_holdout_figure()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var (version, _) = PromotedVersion(db);
        var allocated = gw.Allocate(version, 3m, null, "allocated by the account owner");
        Assert.True(allocated.Ok, allocated.Why);

        var standing = gw.Promotions.Standing(version);
        var line = MissionSituation.PromotedLine(standing, allocated.Allocation);

        Assert.Contains("allocated it up to 3 at a time", line, StringComparison.Ordinal);
        Assert.Contains("enforced when an order arrives", line, StringComparison.Ordinal);
        // Nothing from the held-back months crosses: the figures behind a verdict stay the owner's.
        Assert.DoesNotContain("12", line, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND SAYS SO WHEN NOTHING IS ALLOCATED, with the repair that is true: the owner allocates capital
    /// in the app and there is no command that asks for it.
    /// </summary>
    [Fact]
    public async Task The_promoted_line_says_when_no_capital_is_allocated()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var (version, _) = PromotedVersion(db);
        var line = MissionSituation.PromotedLine(gw.Promotions.Standing(version), null);

        Assert.Contains("No capital is allocated to it, so it can place nothing", line, StringComparison.Ordinal);
        Assert.Contains("no command that asks for it", line, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE FROZEN CONTRACT STATES THE THREE CHOICES, because a choice only the code knows is not a
    /// choice anybody made. <c>docs/COUNCIL.md</c> never names what capital is allocated TO; this unit
    /// answered "a promoted strategy version", declared the ceiling to be the owner's own number rather
    /// than a fraction of a balance TradeAgent does not persist, and counts a position it cannot
    /// attribute against the version anyway. <c>AllocationCeilingOrThrow</c> cites
    /// <c>docs/CONTRACTS.md</c> for the third of those by name, so the file has to carry it.
    ///
    /// <para>The guard is the one <c>LossBudgetSurfacesTests</c> already puts on its own contract: a
    /// paragraph nothing reads drifts from the code in one release.</para>
    /// </summary>
    [Fact]
    public void The_contract_states_what_capital_is_allocated_to_and_who_declares_the_ceiling()
    {
        var contracts = File.ReadAllText(Path.Combine(Repo(), "docs", "CONTRACTS.md"));

        Assert.Contains("What capital is allocated TO is a promoted strategy version — a choice, stated.",
            contracts, StringComparison.Ordinal);
        Assert.Contains(
            "The ceiling is the owner's own declared number, not a fraction of a balance — a choice, stated.",
            contracts, StringComparison.Ordinal);
        Assert.Contains(
            "A position is not attributable to a version, and the ceiling counts it anyway — a choice, stated.",
            contracts, StringComparison.Ordinal);
        Assert.Contains("ALLOCATION_NONE", contracts, StringComparison.Ordinal);
        Assert.Contains("ALLOCATION_EXCEEDED", contracts, StringComparison.Ordinal);
        // The honest limit belongs in the contract too: what is frozen here is the gate, not a
        // deployment, and a reader must not take the section for a claim that something trades.
        Assert.Contains("No runner emits a live or paper intent yet", contracts, StringComparison.Ordinal);
    }

    /// <summary>The repository root, found from the test assembly rather than assumed.</summary>
    static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
