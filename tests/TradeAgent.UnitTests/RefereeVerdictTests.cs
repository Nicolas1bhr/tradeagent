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
/// ITEMS 2 AND 4 — THE HOLDOUT RUN IS THE REFEREE'S OWN, AND THE EVIDENCE HAS TO COME AFTER THE FREEZE.
///
/// <para><b>What was wrong before this.</b> No holdout run could be made at all: every run is refused
/// past the cutoff (`U-referee-1` item 2), and the referee had a charge and a door but nothing that
/// walked through it. And nothing anywhere compared a version's <c>created_at</c> with the window a run
/// covered, so a version frozen today would have promoted on last year's bars —
/// `docs/COUNCIL.md`:135-136 requires the opposite: "because public history may already be known or
/// hard-coded into a submission, forward evidence collected after the strategy's freeze is required
/// before capital".</para>
///
/// <para><b>The two mutants this class exists to catch.</b> The referee's own run charged against the
/// campaign's RESEARCH trial budget, so the evaluation the verdict budget already paid for spends the
/// submitter's allowance a second time. And forward evidence compared against the RUN's
/// <c>created_at</c> instead of the window it covered, which is true of every run ever made and
/// therefore no check at all.</para>
/// </summary>
public class RefereeVerdictTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    /// <summary>
    /// BUYS THE DIP AND SELLS THE RIP, over bars that cycle 96 → 105. Each ten-bar cycle enters at the
    /// next open after a close under 97 and leaves at the next open after a close over 103, so the runs
    /// below close real trades and come out ahead — which is what lets the promoted path be tested at
    /// all, rather than only the refusals.
    /// </summary>
    const string ProfitableText =
        "instrument BTCUSDT\nsize fixed 1\nexit when close > 103\nentry when close < 97\n";

    /// <summary>The same program the other way up: it buys the rip and sells the dip, and loses.</summary>
    const string LosingText =
        "instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > 103\n";

    sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign, string VersionId);

    /// <summary>
    /// A GATEWAY WITH REAL BARS ON DISK, A REAL CUTOFF, A REAL CAMPAIGN AND ONE ACCEPTED VERSION whose
    /// freeze instant this test chooses.
    ///
    /// <para>The version is written through <c>StrategyStore.RecordVersion</c> — the app's own writer,
    /// exactly as <c>Backtests.Record</c> calls it — because <paramref name="frozenAt"/> is the fact
    /// under test in item 4 and a version recorded by a live backtest is frozen at the wall clock.</para>
    /// </summary>
    static async Task<World> Given(DateTimeOffset? frozenAt = null, string program = ProfitableText,
        int bars = 120, int verdicts = 2, string evaluationClass = EvaluationClass.Research)
    {
        var (gw, _, db) = await TestEnv.Ready(s =>
        {
            s.CampaignTrialBudget = 5;
            s.CampaignVerdictBudget = verdicts;
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

        var (held, campaign) = gw.SetHoldout(id, Bar0.AddMinutes(HoldoutAtBar), evaluationClass);
        Assert.True(held.Ok, held.Why);
        Assert.NotNull(campaign);

        var parsed = StrategyParser.Parse(program).Program!;
        new StrategyStore(db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars,
            frozenAt ?? Bar0, CouncilRoles.Research, "attempt-1"));

        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, parsed.StrategyId);
    }

    static Referee RefereeOf(World w) => new(w.Db, () => At);

    /// <summary>A research run inside the window the pipe is allowed to see, to charge a trial with.</summary>
    static BacktestRan Research(World w, string program = ProfitableText, string name = "research.strategy")
    {
        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), program);
        return w.Gw.Backtests.Run(
            AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk("strategies/" + name, w.Set.Id, Bar0, Bar0.AddMinutes(HoldoutAtBar - 1)));
    }

    // ---- item 2: the holdout run is the referee's own ---------------------------------------------

    /// <summary>
    /// THE REFEREE RUNS THE HELD-BACK MONTHS ITSELF, THROUGH THE ONE IN-PROCESS DOOR, and records the
    /// run with its trace and its figures.
    ///
    /// <para>The paired negative is in the same test: the identical window asked for as a caller on the
    /// pipe is refused, so what makes this run possible is the charge and not a hole.</para>
    /// </summary>
    [Fact]
    public async Task The_referee_runs_the_holdout_itself_and_records_the_run()
    {
        var w = await Given();
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        var promotion = verdict.Promotion!;
        Assert.Equal(w.VersionId, promotion.VersionId);

        // THE RUN IS REAL, IT IS OVER THE HELD-BACK WINDOW, AND ITS FIGURES CAME FROM THE APP'S TRACE.
        var run = new StrategyStore(w.Db).RunById(promotion.HoldoutRunId)!;
        Assert.Equal(w.Set.HoldoutFrom, run.WindowFrom);
        Assert.Null(run.WindowTo);
        Assert.Equal(60, run.Bars);
        Assert.Equal(BacktestOutcome.COMPLETED.ToString(), run.Outcome);
        Assert.Equal(Referee.RunRole, run.Role);
        Assert.False(CouncilRoles.IsKnown(run.Role), "the referee is code and is not a council role");
        Assert.Equal(64, run.TraceSha256.Length);
        Assert.True(run.Trades > 0, "the fixture program closes trades on the holdout");

        // THE SAME WINDOW, ASKED FOR BY A CALLER ON THE PIPE: refused, and refused as the HOLDOUT's.
        var asPipe = Backtest.Over(w.Gw.Datasets, w.Set.Id, StrategyParser.Parse(ProfitableText).Program!,
            ExecutionModel.Frictionless, BarAudience.Pipe(CouncilRoles.Research), w.Set.HoldoutFrom, null);
        Assert.False(asPipe.Ok);
        Assert.True(asPipe.IsHoldout);
        Assert.Contains("holds out every bar from", asPipe.Why, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE REFEREE'S OWN RUN IS NOT A RESEARCH TRIAL — this is the mutant.
    ///
    /// <para>A trial is a peek the research process asked for and is charged against the budget that
    /// bounds how often the data may be looked at. The holdout run is the evaluation the VERDICT budget
    /// has already paid for; charging it as research would spend the submitter's allowance on the
    /// referee's own work, and on the last trial of a campaign it would refuse the very run it was
    /// asked to make.</para>
    /// </summary>
    [Fact]
    public async Task The_referees_holdout_run_is_not_charged_as_a_research_trial()
    {
        var w = await Given();
        using var _1 = w.Db;
        Research(w);
        Assert.Equal(1, w.Gw.Campaigns.TrialsCharged(w.Campaign.Id));

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(1, w.Gw.Campaigns.TrialsCharged(w.Campaign.Id));
        Assert.DoesNotContain(w.Gw.Campaigns.Trials(w.Campaign.Id),
            t => t.RunId == verdict.Promotion!.HoldoutRunId);

        // And the verdict budget IS what paid for it: one charge, taken before the run.
        Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
    }

    /// <summary>
    /// A CAMPAIGN WHOSE FIXED POLICY IS NOT THE ONE THIS BUILD IMPLEMENTS IS NOT JUDGED AT ALL.
    ///
    /// <para>The clauses in <see cref="ScoringPolicyV1"/> are the text in <see cref="CampaignPolicy.V1"/>
    /// as code, and the sha on the campaign row is what binds the two. Judging evidence by different
    /// clauses under a sha that promises these ones would hollow out precommitment silently, which is
    /// the one thing `docs/COUNCIL.md`:212 says cannot be repaired afterwards.</para>
    /// </summary>
    [Fact]
    public async Task A_campaign_that_fixed_another_policy_is_not_judged_by_this_builds_clauses()
    {
        var w = await Given();
        using var _1 = w.Db;

        // A second dataset with its own holdout, opened under a policy this build does not implement.
        var other = w.Gw.Datasets.ById(w.Set.Id)!;
        var campaigns = new CampaignStore(w.Db);
        campaigns.Close(w.Campaign.Id, At);
        var odd = campaigns.Open("odd policy", other, 5, 2, At, policy: "promote whatever you like");
        Assert.True(odd.Ok, odd.Why);

        var verdict = RefereeOf(w).Verdict(w.VersionId, odd.Campaign!.Id);

        Assert.False(verdict.Ok);
        Assert.Contains("will not judge evidence by a standard other than", verdict.Why, StringComparison.Ordinal);
        Assert.Empty(new Promotions(w.Db).All());
    }

    // ---- item 4: forward evidence, after the freeze -----------------------------------------------

    /// <summary>
    /// A VERSION FROZEN AFTER THE HELD-BACK WINDOW BEGINS IS REFUSED, WHATEVER ITS FIGURES SAY.
    ///
    /// <para>`docs/COUNCIL.md`:135-136: "because public history may already be known or hard-coded into
    /// a submission, forward evidence collected after the strategy's freeze is required before capital".
    /// The program under test is the profitable one — it closes winning trades over exactly these bars —
    /// and it is refused anyway, which is the whole point: months that predate the freeze are not weak
    /// evidence to be weighed against the rest, they are not evidence at all.</para>
    ///
    /// <para>This is also the mutant: comparing the version's freeze with when the RUN was made instead
    /// of with the window the run covered. Every run is made now, so that comparison passes for every
    /// version ever submitted and refuses nothing.</para>
    /// </summary>
    [Fact]
    public async Task A_version_frozen_after_the_held_back_window_begins_is_refused_in_words()
    {
        var w = await Given(frozenAt: Bar0.AddMinutes(HoldoutAtBar + 10));
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.False(verdict.Promoted, "a version frozen after the holdout begins promoted on it");
        Assert.Equal(PromotionVerdict.Refused, verdict.Promotion!.Verdict);
        Assert.Equal(PromotionReason.PrecedesTheFreeze, verdict.Promotion!.Reason);
        Assert.Equal(PromotionState.Refused, new Promotions(w.Db).Standing(w.VersionId).State);
        Assert.Contains("not evidence collected after the freeze",
            PromotionReason.Words(verdict.Promotion!.Reason), StringComparison.Ordinal);

        // The run really was made and really was profitable: it is the DATE that refused it.
        var run = new StrategyStore(w.Db).RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.True(run.Trades > 0);
        Assert.True(run.NetPnl > 0m, "the fixture program is profitable over these bars");
    }

    /// <summary>
    /// A VERSION FROZEN BEFORE THE WINDOW IS JUDGED ON IT, and this one is promoted — the clauses of
    /// <see cref="ScoringPolicyV1"/> in the order they are applied, with the figures reached at last.
    /// </summary>
    [Fact]
    public async Task A_version_frozen_before_the_window_is_judged_on_it()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.True(verdict.Promoted, verdict.Promotion!.Reason);
        Assert.Equal(PromotionReason.Met, verdict.Promotion!.Reason);
        Assert.Equal(PromotionState.Promoted, new Promotions(w.Db).Standing(w.VersionId).State);
    }

    /// <summary>
    /// THE FIGURES ARE REACHED ONLY AFTER THE DATE IS, and a losing program frozen in good time is
    /// refused on the figures rather than on the calendar. The two refusals are different words.
    /// </summary>
    [Fact]
    public async Task A_version_that_loses_over_the_holdout_is_refused_on_the_figures()
    {
        var w = await Given(frozenAt: Bar0, program: LosingText);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.False(verdict.Promoted);
        Assert.Equal(PromotionReason.NotProfitable, verdict.Promotion!.Reason);
        Assert.True(new StrategyStore(w.Db).RunById(verdict.Promotion!.HoldoutRunId)!.NetPnl <= 0m);
    }
}
