using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-PROMOTE-BOUNDS — THE REFEREE WILL NOT JUDGE A VERSION THAT DECLARES NONE OF THE THREE BOUNDS.
///
/// <para><b>What was wrong before this.</b> `U-freshness` landed the three declarations
/// (<c>timeframe</c>, <c>data_freshness</c>, <c>max_decision_age</c>), the gate that refuses a stale
/// decision at dispatch, and the columns that carry the bounds onto a promotion — but the language
/// leaves the three OPTIONAL and all-or-none, and nothing forced a PROMOTED version to declare them.
/// A program declaring none emits an intent with no <c>IntentDecision</c> on it, so
/// <c>RefuseAStaleDecisionOrThrow</c> has nothing to refuse on: every order such a version decided
/// would be sent however old the bars behind it were. `docs/COUNCIL.md`:96-97 states the opposite as
/// one sentence — "a promoted strategy declares its timeframe, its required data freshness and its
/// maximum decision age" — and PROMOTION is the word in it, so promotion is where it is enforced.</para>
///
/// <para><b>The mutant this class exists to catch.</b> The check applied where the standing is READ
/// (<c>Promotions.Standing</c>) instead of at the verdict. The refusal then reads the same from a
/// surface — the version does not stand promoted — while the promotion row has been written, the
/// campaign's scarcest budget has been spent on it, the boundary has been opened and Research has been
/// woken; and a row is the one thing in this ledger nothing can take back
/// (<c>docs/COUNCIL.md</c>:212). <c>Assert.Null(verdict.Promotion)</c> is what separates the two.</para>
///
/// <para><b>What this rule does NOT reach, deliberately.</b> Versions already promoted without the
/// three keep standing — see <see cref="A_promotion_already_recorded_without_bounds_still_stands"/>.
/// Invalidation is what `docs/COUNCIL.md`:35 reserves for a CHANGED ASSUMPTION, and this build's
/// judgement about what may be judged is not one of the assumptions those verdicts rested on;
/// withdrawing them by code would also rewrite a record after the outcome was known, which is the one
/// move this ledger is built to make impossible. Section 4 of the owner's report names them instead.</para>
/// </summary>
public class PromotionBoundsTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    /// <summary>
    /// A PROFITABLE PROGRAM THAT DECLARES NONE OF THE THREE — the shape of every program text this
    /// installation accepted before schema 19, and the one this unit refuses to judge.
    /// </summary>
    const string BareText =
        "instrument BTCUSDT\nsize fixed 1\nexit when close > 103\nentry when close < 97\n";

    /// <summary>The same program with the three `docs/COUNCIL.md`:96-97 asks a promoted strategy for.</summary>
    const string BoundedText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign, string VersionId);

    /// <summary>
    /// A GATEWAY WITH REAL BARS ON DISK, A REAL CUTOFF, A REAL CAMPAIGN AND ONE ACCEPTED VERSION.
    /// The same world `RefereeVerdictTests` judges in, so a refusal here is a refusal on the path that
    /// otherwise promotes.
    /// </summary>
    static async Task<World> Given(string program, int bars = 120)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 5;
            s.CampaignVerdictBudget = 2;
        });

        var dir = BinanceArchive.DatasetDir("BTCUSDT");
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"BTCUSDT-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Bar0.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var id = gw.Datasets.Record(new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Bar0, Bar0.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Bar0, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Bar0, KlineTimeUnit.Microseconds, raw)]));

        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        var parsed = StrategyParser.Parse(program).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            Bar0, CouncilRoles.Research, "attempt-1"));

        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, parsed.StrategyId);
    }

    /// <summary>
    /// THE ITEM: A VERSION THAT DECLARES NONE OF THE THREE IS NOT PROMOTABLE, AND NOTHING IS WRITTEN.
    ///
    /// <para>RED before this unit — the bound-less program is profitable over the held-back months and
    /// was frozen before them, so it met every clause of the scoring policy and promoted:
    /// <c>Assert.False() Failure / Expected: False / Actual: True</c> on <c>verdict.Ok</c>, with a
    /// <c>promoted</c> row in <c>strategy_promotion</c> behind it.</para>
    ///
    /// <para>The refusal NAMES the three, because the sentence has to be actionable: the fix is three
    /// lines of the program's own language, and a refusal that said only "bounds" would send its reader
    /// to the source of this method to find out which.</para>
    /// </summary>
    [Fact]
    public async Task A_version_that_declares_none_of_the_three_is_not_promotable_and_no_row_is_written()
    {
        var w = await Given(BareText);
        using var _1 = w.Db;

        var verdict = new Referee(w.Db, () => At).Verdict(w.VersionId, w.Campaign.Id);

        Assert.False(verdict.Ok, "a version declaring none of the three execution bounds was judged");
        Assert.False(verdict.Promoted);

        // NO ROW. Not a recorded refusal either: a refusal is a VERDICT, and TradeAgent has not judged
        // this program's evidence at all — it has refused to judge a program it may never dispatch.
        Assert.Null(verdict.Promotion);
        Assert.Empty(new Promotions(w.Db).All());
        Assert.Equal(PromotionState.Unjudged, new Promotions(w.Db).Standing(w.VersionId).State);

        // THE SENTENCE NAMES THE THREE DECLARATIONS, in the spelling the program has to use.
        Assert.Contains("`timeframe`", verdict.Why, StringComparison.Ordinal);
        Assert.Contains("`data_freshness`", verdict.Why, StringComparison.Ordinal);
        Assert.Contains("`max_decision_age`", verdict.Why, StringComparison.Ordinal);

        // AND NOTHING ELSE HAPPENED EITHER: the scarcest budget in the product is not spent on an
        // answer that is in the recorded text, the held-back months are not read, and no director is
        // woken to consider a promotion that cannot exist.
        Assert.Empty(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Empty(w.Gw.Strategies.Runs());
        Assert.Empty(new CouncilBoundaries(w.Db).All());
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// AND THE SAME PROGRAM WITH THE THREE ON IT IS JUDGED AND PROMOTED, with the bounds on its row.
    ///
    /// <para>The paired positive, in the same fixture and over the same bars: what refused the version
    /// above is the missing declaration and not the world it was judged in. Without this, a guard that
    /// refused everything would read exactly as well.</para>
    /// </summary>
    [Fact]
    public async Task A_version_that_declares_the_three_is_judged_and_carries_them_onto_its_promotion()
    {
        var w = await Given(BoundedText);
        using var _1 = w.Db;

        var verdict = new Referee(w.Db, () => At).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.True(verdict.Promoted, verdict.Promotion?.Reason ?? verdict.Why);
        Assert.Equal(
            new FreshnessBounds(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)),
            verdict.Promotion!.Freshness);
        Assert.Equal(PromotionState.Promoted, new Promotions(w.Db).Standing(w.VersionId).State);
        await w.Gw.DisposeAsync();
    }

    /// <summary>
    /// NOTHING ALREADY PROMOTED IS INVALIDATED BY THIS UNIT, and that is a choice rather than an
    /// oversight — `docs/CONTRACTS.md` states it under `U-promote-bounds`.
    ///
    /// <para>`docs/COUNCIL.md`:35 spends invalidation on a CHANGED ASSUMPTION — the dataset, the
    /// interpreter, the scoring policy — and this build's new opinion about what may be JUDGED is not
    /// one of the assumptions an existing verdict rested on. Withdrawing those rows by code would also
    /// be the app restating a record after the outcome was known, which is the move
    /// <c>Promotions</c> has no method for on purpose.</para>
    /// </summary>
    [Fact]
    public async Task A_promotion_already_recorded_without_bounds_still_stands()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var version = PromotedWithoutBounds(db);
        var standing = new Promotions(db).Standing(version);

        Assert.Equal(PromotionState.Promoted, standing.State);
        Assert.True(standing.IsPromoted);
        Assert.Null(standing.Promotion!.Freshness);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// SECTION 4 NAMES ONE — the capital section, because that is where a promotion without bounds
    /// costs something: an allocation is standing behind a version whose decisions the dispatch gate
    /// cannot judge the age of, and the owner reading "allocated" would otherwise read it as working.
    ///
    /// <para>RED before this unit: <c>Assert.Contains() Failure: Sub-string not found / Not found:
    /// "the dispatch gate cannot judge"</c> — the allocation line said version, ceiling, currency,
    /// instant and policy, and nothing at all about the bounds behind it.</para>
    ///
    /// <para>Marked only where the allocation still AUTHORISES: a withdrawn one may trade nothing, so
    /// the gate it would be judged at is never reached and a second mark on that line would be noise.</para>
    /// </summary>
    [Fact]
    public async Task Section_four_names_a_standing_promotion_without_bounds_as_one_the_gate_cannot_judge()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;

        var version = PromotedWithoutBounds(db);
        Assert.True(gw.Allocate(version, 3m, 250_000m, "allocated by the account owner").Ok);

        var text = DailyReportText.Render(gw.Reports.Compose(DateTimeOffset.Now));

        Assert.Contains("- allocated: " + version[..12], text, StringComparison.Ordinal);
        Assert.Contains("NO EXECUTION BOUNDS", text, StringComparison.Ordinal);
        Assert.Contains("the dispatch gate cannot judge", text, StringComparison.Ordinal);
        Assert.Contains("`max_decision_age`", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WITHDRAWN", text, StringComparison.Ordinal);
        await gw.DisposeAsync();
    }

    /// <summary>
    /// THE CONTRACT STATES WHAT THIS RULE REACHES AND WHAT IT DOES NOT, because both halves are
    /// choices somebody made and a reader a release later has no other way to know which.
    /// </summary>
    [Fact]
    public void The_contract_states_the_rule_and_what_it_does_not_reach()
    {
        var contracts = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "CONTRACTS.md"));

        Assert.Contains("## U-promote-bounds", contracts, StringComparison.Ordinal);
        Assert.Contains("Nothing already promoted is invalidated by this unit.", contracts,
            StringComparison.Ordinal);
        Assert.Contains("No promotion row is written at all", contracts, StringComparison.Ordinal);
    }

    /// <summary>
    /// A VERSION THAT REALLY STANDS PROMOTED IN THIS DATABASE WITH NO BOUNDS ON ITS ROW — the shape of
    /// every verdict this installation recorded before schema 19, written through the app's own writer.
    /// </summary>
    static string PromotedWithoutBounds(Database db)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"promote-bounds-{Guid.NewGuid():n}.csv");
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

        var program = StrategyParser.Parse(BareText).Program!;
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

        return program.StrategyId;
    }

    /// <summary>The repository root, found by walking up to the solution file. See other contract tests.</summary>
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradeAgent.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
