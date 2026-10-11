using System.Globalization;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.AgentRuntime;
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
    ///
    /// <para>It declares the three execution bounds `docs/COUNCIL.md`:96-97 asks a promoted strategy
    /// for, because `U-promote-bounds` refuses to judge a version that declares none of them at all and
    /// every test below is about what happens AFTER the referee agrees to judge. The refusal itself has
    /// its own class, `PromotionBoundsTests`. None of the three changes what the backtest does.</para>
    /// </summary>
    const string ProfitableText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close > 103\nentry when close < 97\n";

    /// <summary>The same program the other way up: it buys the rip and sells the dip, and loses.</summary>
    const string LosingText =
        "instrument BTCUSDT\nsize fixed 1\ntimeframe 1m\ndata_freshness 2m\nmax_decision_age 30s\n"
        + "exit when close < 97\nentry when close > 103\n";

    internal sealed record World(TradingGateway Gw, Database Db, DatasetRecord Set, CampaignRow Campaign, string VersionId);

    /// <summary>
    /// A GATEWAY WITH REAL BARS ON DISK, A REAL CUTOFF, A REAL CAMPAIGN AND ONE ACCEPTED VERSION whose
    /// freeze instant this test chooses.
    ///
    /// <para>The version is written through <c>StrategyStore.RecordVersion</c> — the app's own writer,
    /// exactly as <c>Backtests.Record</c> calls it — because <paramref name="frozenAt"/> is the fact
    /// under test in item 4 and a version recorded by a live backtest is frozen at the wall clock.</para>
    /// </summary>
    internal static async Task<World> Given(DateTimeOffset? frozenAt = null, string program = ProfitableText,
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
            // The increment is DECLARED: this fixture's dataset records no venue, and TradeAgent will
            // not invent one (`VenueIncrementTests`). 1 is what this run used before the catalogue
            // existed, so nothing measured here moves.
            new BacktestAsk("strategies/" + name, w.Set.Id, Bar0, Bar0.AddMinutes(HoldoutAtBar - 1),
                Increment: 1m));
    }

    // ---- the boundary the verdict opens (U-council-concurrent-2, item 1) --------------------------

    /// <summary>
    /// A VERDICT OPENS ONE CONSEQUENTIAL BOUNDARY, WAKES BOTH DIRECTORS ONCE, AND ASKING AGAIN BUYS
    /// NOTHING.
    ///
    /// <para><c>docs/COUNCIL.md</c>:59 lists promotion of a strategy first among the boundaries the
    /// strongest model is spent at, and :64 says repeated proposals must not manufacture that spend. The
    /// referee is the app's only producer of one today: the boundary is opened inside the same
    /// transaction as the promotion, so a crash cannot leave a judgement nobody was asked about.</para>
    ///
    /// <para>The default written on the row is the POLICY's answer and is frozen at open — the fixture
    /// program is profitable over the holdout and was frozen before it, so this one deploys.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_opens_one_boundary_with_the_policys_default_and_two_paid_wakes()
    {
        var w = await Given();
        using var _1 = w.Db;
        var boundaries = new CouncilBoundaries(w.Db);

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Ok, verdict.Why);

        var boundary = Assert.Single(boundaries.All());
        Assert.Equal(BoundaryIds.Of(BoundaryKind.Promotion, w.VersionId, w.Campaign.Id), boundary.Id);
        Assert.Equal(BoundaryDisposition.Deploy, boundary.DefaultDisposition);
        Assert.True(boundary.IsOpen);

        // TWO TURNS, ONE PER DIRECTOR. ":63 — two assessments are two turns."
        var wakes = new MissionEventStore(w.Db).OfKind(MissionEventKind.Boundary);
        Assert.Equal(2, wakes.Count);
        Assert.Equal(CouncilRoles.All.Order(), wakes.Select(e => e.For).Order());

        // THE EVIDENCE CARRIES NO FIGURE FROM THE HELD-BACK MONTHS. It is rendered into both directors'
        // Situations and into a report `trade report` serves to an agent verbatim, so the only digits in
        // it are the campaign id and the two hashes' own characters.
        var run = new StrategyStore(w.Db).RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.DoesNotContain(run.NetPnl!.Value.ToString(CultureInfo.InvariantCulture), boundary.Evidence,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.Bars.ToString(CultureInfo.InvariantCulture), boundary.Evidence,
            StringComparison.Ordinal);

        // ASKED AGAIN: the same promotion, the same boundary, and no second turn for anybody.
        RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.Single(boundaries.All());
        Assert.Equal(2, new MissionEventStore(w.Db).OfKind(MissionEventKind.Boundary).Count);
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
    /// (g) THE REFEREE'S HOLDOUT RUN KEEPS NO STREAM (<c>U-trial-returns</c>). Over the venue fixture — BTC-priced bars on
    /// Binance spot, so a stream has days — a research run of the version keeps its 1x and 2x daily net returns; the
    /// referee's run of the same version over the held-back hour is recorded under its own mark and keeps none, at either
    /// multiple: nothing of the holdout is stored as a series (<c>U-referee-v2</c> computes what it needs at verdict time),
    /// and no stream is not a stream of no days — there is no row at all.
    /// </summary>
    [Fact]
    public async Task The_referees_holdout_run_keeps_no_stream()
    {
        var w = await CostModelPinTests.Given();
        using var _1 = w.Db;
        var (held, campaign) = w.Gw.SetHoldout(w.Set.Id, CostModelPinTests.Cutoff, EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);
        var version = CostModelPinTests.Version(w.Db, CostModelPinTests.BtcProgram, CostModelPinTests.Bar0);

        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Research), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "streamless.strategy"), CostModelPinTests.BtcProgram);
        var research = w.Gw.Backtests.Run(AgentContext.ForAgent("agent", CouncilRoles.Research, "attempt-1"),
            new BacktestAsk("strategies/streamless.strategy", w.Set.Id, CostModelPinTests.Bar0,
                CostModelPinTests.Bar0.AddMinutes(CostModelPinTests.HoldoutAtBar - 1)));
        Assert.Equal(version, research.Result.VersionId);
        var kept = w.Gw.Strategies.StreamsOf(research.Result.RunId);
        Assert.Equal([1, 2], kept.Select(s => s.Multiple));
        Assert.All(kept, s => Assert.NotEmpty(s.Days));

        var verdict = CostModelPinTests.RefereeOf(w).Verdict(version, campaign!.Id);
        Assert.True(verdict.Ok, verdict.Why);
        var holdout = w.Gw.Strategies.RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.Equal(Referee.RunRole, holdout.Role);
        Assert.True(holdout.Bars > 0, "the referee's run evaluated nothing");

        Assert.Empty(w.Gw.Strategies.StreamsOf(holdout.Id));
        Assert.Equal(2L, w.Db.Read(_ =>
        {
            using var c = w.Db.Cmd("SELECT COUNT(*) FROM strategy_stream");
            return (long)c.ExecuteScalar()!;
        }));
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
    /// A VERSION FROZEN AFTER THE HELD-BACK WINDOW BEGINS IS NEVER PROMOTED, WHATEVER ITS FIGURES SAY.
    ///
    /// <para>`docs/COUNCIL.md`:135-136: "because public history may already be known or hard-coded into
    /// a submission, forward evidence collected after the strategy's freeze is required before capital".
    /// The program under test is the profitable one — it closes winning trades over exactly these bars —
    /// and it is kept out of a promotion anyway, which is the whole point: months that predate the
    /// freeze are not weak evidence to be weighed against the rest, they are not evidence FOR CAPITAL
    /// at all.</para>
    ///
    /// <para>This is also the mutant: comparing the version's freeze with when the RUN was made instead
    /// of with the window the run covered. Every run is made now, so that comparison passes for every
    /// version ever submitted and refuses nothing.</para>
    ///
    /// <para><b>`U-paper-verdict` moved what this arm is RECORDED as, and nothing else.</b> A
    /// profitable version on this arm is now written down as <c>paper-eligible</c> rather than
    /// <c>refused</c> — `docs/PRINCIPLES.md` § Evidence keeps "a favourable historical verdict" and
    /// "eligibility for live capital" apart, and a loop that could only ever be refused here can never
    /// produce the first paper run. The property this test was written for is untouched and is asserted
    /// below exactly as before: the clause is applied against the WINDOW, the figures were good, and
    /// the version is not promoted and has no standing that could be given money.
    /// `PaperEligibleVerdictTests` holds the rest of that arm.</para>
    /// </summary>
    [Fact]
    public async Task A_version_frozen_after_the_held_back_window_begins_is_refused_in_words()
    {
        var w = await Given(frozenAt: Bar0.AddMinutes(HoldoutAtBar + 10));
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.False(verdict.Promoted, "a version frozen after the holdout begins promoted on it");
        Assert.NotEqual(PromotionVerdict.Promoted, verdict.Promotion!.Verdict);
        Assert.NotEqual(PromotionReason.Met, verdict.Promotion!.Reason);
        Assert.Equal(PromotionVerdict.PaperEligible, verdict.Promotion!.Verdict);
        Assert.Equal(PromotionReason.MetOnHistory, verdict.Promotion!.Reason);

        var standing = new Promotions(w.Db).Standing(w.VersionId);
        Assert.Equal(PromotionState.PaperEligible, standing.State);
        Assert.False(standing.IsPromoted, "a version frozen after the holdout begins stood promoted");
        Assert.Contains("do not post-date its own freeze",
            PromotionReason.Words(verdict.Promotion!.Reason), StringComparison.Ordinal);

        // The run really was made and really was profitable: it is the DATE that kept it out.
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

    // ---- item 5: the verdict is delivered and told ------------------------------------------------

    /// <summary>
    /// ONE WAKE, KEYED BY THE PROMOTION, AND ONE NOTE THAT CARRIES NO FIGURE FROM THE HELD-BACK MONTHS.
    ///
    /// <para>This is the mutant: the holdout's metrics published to the team. Every figure the app
    /// measured over those months is read back out of the run row and asserted to be absent from the
    /// text that crosses — because a net result quoted in a note tells Research something about months
    /// it was never shown, and `docs/COUNCIL.md`:212 says that cannot be untold.</para>
    ///
    /// <para>The second half is the deduplication `docs/COUNCIL.md`:64 asks for: asking again is one
    /// judgement, one publication and one paid turn, because the wake is keyed by the promotion, whose
    /// id is the hash of the evidence.</para>
    /// </summary>
    [Fact]
    public async Task The_verdict_wakes_research_once_and_the_note_carries_no_holdout_figure()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;
        var referee = RefereeOf(w);

        var verdict = referee.Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Ok, verdict.Why);
        var promotion = verdict.Promotion!;

        // ONE WAKE, FOR RESEARCH, KEYED BY THE PROMOTION.
        var events = new MissionEventStore(w.Db);
        var wake = Assert.Single(events.OfKind(MissionEventKind.Verdict));
        Assert.Equal(MissionEventIds.ForRole(MissionEventIds.Verdict(promotion.Id), CouncilRoles.Research),
            wake.Id);
        Assert.Equal(CouncilRoles.Research, wake.For);
        Assert.False(wake.Consumed);

        // ONE PUBLICATION, FROM THE REFEREE, TO RESEARCH, COMMITTED FOR DELIVERY.
        var publications = new PublicationStore(w.Db);
        var note = Assert.Single(publications.By(Referee.RunRole));
        Assert.Equal(PublicationKind.Verdict, note.Kind);
        Assert.Equal([CouncilRoles.Research], note.RecipientList);
        Assert.Equal(Publication.IdOf(Referee.RunRole, PublicationKind.Verdict, note.Content), note.Id);
        Assert.Equal(note.Id, Json.Read<MissionTask>(wake.Payload ?? "")!.Publication);
        Assert.Single(publications.To(CouncilRoles.Research));

        // WHAT IT SAYS: the verdict and the reason class, and the promotion it can be tied back to.
        Assert.Contains(promotion.Verdict, note.Content, StringComparison.Ordinal);
        Assert.Contains(promotion.Reason, note.Content, StringComparison.Ordinal);
        Assert.Contains(promotion.Id, note.Content, StringComparison.Ordinal);

        // WHAT IT MUST NOT SAY: anything the app measured over the months held back. Asserted as the
        // ABSENCE OF NUMBERS rather than as the absence of particular ones — a figure that happens to be
        // 0 or 6 would slip through a list of strings, and the rule is not "not these figures", it is
        // "no figure at all". With the two hashes taken out, the only digits left in the note are the
        // campaign's id.
        var run = new StrategyStore(w.Db).RunById(promotion.HoldoutRunId)!;
        var stripped = note.Content.Replace(promotion.Id, "", StringComparison.Ordinal)
            .Replace(promotion.VersionId, "", StringComparison.Ordinal);

        Assert.Equal(promotion.CampaignId.ToString(CultureInfo.InvariantCulture),
            new string([.. stripped.Where(char.IsDigit)]));
        Assert.DoesNotContain(run.TraceSha256, note.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(promotion.HoldoutRunId, note.Content, StringComparison.Ordinal);
        Assert.True(run.NetPnl > 0m, "the fixture program is profitable, so a leak would have shown one");

        // ASKING AGAIN IS THE SAME JUDGEMENT: no second note, no second wake, nobody charged twice.
        var again = referee.Verdict(w.VersionId, w.Campaign.Id);
        Assert.Equal(promotion.Id, again.Promotion!.Id);
        Assert.Single(publications.By(Referee.RunRole));
        Assert.Single(events.OfKind(MissionEventKind.Verdict));
        Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
    }

    /// <summary>
    /// THE TURN IS TOLD WHAT IS PROMOTED, and told when what was promoted no longer stands.
    ///
    /// <para><c>docs/COUNCIL.md</c>:74 puts the promoted strategy in the Situation. The line is built
    /// from <c>Promotions.Standing</c>, so an invalidated promotion reads as "none" with the reason
    /// rather than as a strategy an agent could plan around.</para>
    /// </summary>
    [Fact]
    public async Task The_situation_names_the_promoted_version_and_says_when_it_no_longer_stands()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;
        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        var promotions = new Promotions(w.Db);

        var promoted = MissionSituation.PromotedLine(promotions.Standing(w.VersionId));
        Assert.Contains(w.VersionId[..12], promoted, StringComparison.Ordinal);
        Assert.Contains("promoted by TradeAgent's referee", promoted, StringComparison.Ordinal);
        Assert.Contains($"- {promoted}",
            new MissionSituation { Mode = "PAPER", Promoted = promoted }.Text(), StringComparison.Ordinal);

        new DatasetStore(w.Db).Reject(w.Set.Id, "a raw archive file changed under it");

        var withdrawn = MissionSituation.PromotedLine(promotions.Standing(w.VersionId));
        Assert.StartsWith("Promoted strategy: none.", withdrawn, StringComparison.Ordinal);
        Assert.Contains("no longer stands", withdrawn, StringComparison.Ordinal);
        Assert.StartsWith("Promoted strategy: none.", MissionSituation.PromotedLine(null),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// SECTION 8 LISTS THE VERDICT AND WITHHOLDS THE HOLDOUT'S FIGURES — in the document the owner
    /// reads AND the agent is served over <c>trade report</c>.
    ///
    /// <para>The holdout run is named, dated and counted as a run; its net, its drawdown and its trace
    /// are not in the document, because this one is handed to the research process on request. The
    /// verdict beneath it is what was worth knowing, and its reason is the promotion row's closed
    /// vocabulary, which cannot hold a figure.</para>
    /// </summary>
    [Fact]
    public async Task Section_eight_lists_the_verdict_and_prints_no_holdout_figure()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;

        var before = DailyReportText.Render(w.Gw.Reports.Compose(Midday()));
        Assert.Contains("nothing has been backtested or promoted by this build", before, StringComparison.Ordinal);

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Promoted, verdict.Promotion?.Reason ?? verdict.Why);

        var text = DailyReportText.Render(w.Gw.Reports.Compose(Midday()));

        Assert.Contains($"PROMOTED version {w.VersionId[..12]}", text, StringComparison.Ordinal);
        Assert.Contains(PromotionReason.Words(PromotionReason.Met), text, StringComparison.Ordinal);
        Assert.Contains("holdout run", text, StringComparison.Ordinal);
        Assert.Contains("its figures are not printed in this report", text, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing has been backtested or promoted by this build", text,
            StringComparison.Ordinal);

        var run = new StrategyStore(w.Db).RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.DoesNotContain($"net {run.NetPnl?.ToString(CultureInfo.InvariantCulture)}", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.TraceSha256, text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{run.Bars} bars", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE VERDICT REQUEST IS THE ONE OP THAT REACHES ANY OF THIS, AND EVERY OTHER PART OF IT STILL HAS
    /// NONE. The whole agent-facing vocabulary is asked, by name, so an op added later has to be
    /// justified here rather than pass quietly.
    ///
    /// <para><b>What changed, and what did not</b> (`U-verdict-op`). Until that unit this asserted that
    /// no op was even NAMED verdict, and the sentence it stood for was `docs/CONTRACTS.md`'s "no pipe op
    /// and no `trade` verb reaches it". The REQUEST is now reachable — `docs/PRINCIPLES.md` § Evidence:
    /// models propose candidates, software decides admission and issues the verdict — and the property
    /// that mattered is kept by its SHAPE rather than by its absence: the caller names a version and at
    /// most a dataset, and the campaign's own verdict budget is what bounds it. So the assertion is now
    /// that there is exactly ONE such op, that those two are the whole of what it takes, and that
    /// nothing it could pass chooses the months, the scorer or the friction its evidence is judged
    /// under.</para>
    ///
    /// <para>And the rest of the sentence stands unchanged: no op writes a promotion, opens, renews or
    /// re-budgets a campaign, registers a trial, moves a holdout cutoff or writes a disposition. The
    /// caller being judged must not be able to mark its own homework.</para>
    /// </summary>
    [Fact]
    public void No_pipe_op_asks_for_a_verdict_or_writes_a_promotion()
    {
        foreach (var op in GatewaySchema.Ops())
        {
            foreach (var forbidden in new[] { "promot", "referee", "holdout", "campaign", "trial", "disposition" })
                Assert.DoesNotContain(forbidden, op.Op, StringComparison.OrdinalIgnoreCase);

            if (op.Op.Contains("verdict", StringComparison.OrdinalIgnoreCase))
                Assert.Equal(Ops.Verdict, op.Op);
        }

        // EXACTLY ONE, AND IT IS THE REQUEST — not a writer of one.
        var verdict = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Verdict);
        Assert.False(verdict.Mutating);
        Assert.DoesNotContain(Ops.Verdict, Ops.Mutating);

        // AND THE WHOLE OF WHAT A CALLER MAY PASS. The execution model, the window, the policy and the
        // campaign are deliberately absent: a submitter that could pass any of them would be choosing
        // the standard its own evidence is scored against.
        //
        // `entry` (U-research-ledger) is the one addition, and it chooses nothing judged: it names the caller's own
        // research-ledger entry the verdict is asked under, so the app can link the promotion it answers with — the
        // months, the scorer, the friction and the budget are the campaign's exactly as before, and an entry that is not
        // the caller's role's own is refused before anything is charged.
        Assert.Equal(["version", "dataset", "entry"], verdict.Args.Select(a => a.Name).ToArray());
        Assert.True(verdict.Args.Single(a => a.Name == "version").Required);
        Assert.False(verdict.Args.Single(a => a.Name == "dataset").Required);
        Assert.False(verdict.Args.Single(a => a.Name == "entry").Required);
        foreach (var never in new[] { "fees", "slippage", "from", "to", "policy", "campaign", "model" })
            Assert.DoesNotContain(never, verdict.Args.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
    }

    // ---- a verdict the app stops (U-verdict-stopped) ----------------------------------------------

    /// <summary>
    /// (a) A VERDICT THE APP STOPS BEFORE ITS CHARGE IS REFUSED AND CHARGES NOTHING.
    ///
    /// <para>The stop is the app's — it is closing, or the harness turn that asked for the verdict was ended — and it
    /// is asked before the charge, so a stop that has already fired costs the campaign no judgement: nothing charged,
    /// no holdout run, no promotion, no note, no boundary and no wake. And the same ask, not stopped, is judged on one
    /// judgement. RED on the base, where the stop reached only the holdout run: the judgement was charged, the run
    /// halted at its first bar, and the halt was recorded as the version's final verdict — <c>refused</c>,
    /// <c>the-holdout-run-did-not-complete</c> — on a FAULTED run under the referee, blaming the strategy for the
    /// app's own stop.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_the_app_stops_before_its_charge_is_refused_and_charges_nothing()
    {
        var w = await Given();
        using var _1 = w.Db;
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id, stop: stop.Token);

        Assert.True(verdict.Stopped, "a verdict the app stopped before its charge was answered as one: "
            + Answered(w, verdict));
        Assert.False(verdict.Ok);
        Assert.Null(verdict.Promotion);
        Assert.Contains("before its judgement was charged", verdict.Why, StringComparison.Ordinal);

        NothingRecorded(w);
        Assert.Empty(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Equal(0, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));

        // AND THE SAME ASK, NOT STOPPED, IS JUDGED: charged once, and promoted.
        var judged = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(judged.Promoted, judged.Promotion?.Reason ?? judged.Why);
        Assert.False(judged.Stopped);
        Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
    }

    /// <summary>
    /// (b) A VERDICT THE APP STOPS DURING ITS RUN RECORDS NOTHING, AND ITS CHARGE JUDGES IT LATER.
    ///
    /// <para>The stop fires when the charge reads the referee's clock (<c>ChargeVerdict</c>, inside the charge's own
    /// transaction), so the check before the charge has already passed, the judgement is written, and the holdout run
    /// starts with the stop set and halts at its first bar. What is under test is the second check, when the run
    /// returns: nothing of that run is kept — no run row, no promotion, no note, no boundary, no wake — and the charge
    /// is neither undone, voided nor moved: it stands, as after a crash. Asked again, not stopped, the version is
    /// judged ON THAT CHARGE: the budget is ONE and already spent, and the verdict is taken with the count unmoved.</para>
    ///
    /// <para>RED on the base: the halted run was recorded as the version's final <c>refused</c>, and the second ask
    /// answered that refusal again — a re-run over the same bytes has the same run id, so the first rows stand and only
    /// a new version, a second judgement, could go on.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_the_app_stops_during_its_run_records_nothing_and_its_charge_judges_it_later()
    {
        var w = await Given(verdicts: 1);
        using var _1 = w.Db;
        using var stop = new CancellationTokenSource();
        var stopping = new Referee(w.Db, () =>
        {
            stop.Cancel();
            return At;
        });

        var verdict = stopping.Verdict(w.VersionId, w.Campaign.Id, stop: stop.Token);

        Assert.True(verdict.Stopped, "a verdict the app stopped during its holdout run was answered as one: "
            + Answered(w, verdict));
        Assert.False(verdict.Ok);
        Assert.Null(verdict.Promotion);
        Assert.Contains("stays this version's", verdict.Why, StringComparison.Ordinal);

        NothingRecorded(w);

        // THE CHARGE STANDS: one row, for this version, and the budget of one is spent.
        Assert.Equal(w.VersionId, Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id)).VersionId);
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));

        // ASKED AGAIN, NOT STOPPED: judged on that charge, and nothing more is spent.
        var judged = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(judged.Ok, judged.Why);
        Assert.False(judged.Stopped);
        Assert.Equal(PromotionVerdict.Promoted, judged.Promotion!.Verdict);
        Assert.Equal(PromotionReason.Met, judged.Promotion!.Reason);
        Assert.Single(w.Gw.Campaigns.Verdicts(w.Campaign.Id));
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
        Assert.Equal(BacktestOutcome.COMPLETED.ToString(),
            new StrategyStore(w.Db).RunById(judged.Promotion!.HoldoutRunId)!.Outcome);
    }

    /// <summary>What the referee answered and what the campaign was charged, in one line, for a red assertion to quote.</summary>
    static string Answered(World w, RefereeVerdict verdict) =>
        $"ok {verdict.Ok}, {verdict.Promotion?.Verdict ?? "no verdict"} / {verdict.Promotion?.Reason ?? "no reason"}, "
        + $"why \"{verdict.Why}\", judgements spent {w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id)}";

    /// <summary>
    /// NOTHING OF A STOPPED VERDICT IS IN THE LEDGERS: no holdout run under the referee, no promotion, no note to Research
    /// and no wake for it, no boundary and no wake for either director.
    /// </summary>
    static void NothingRecorded(World w)
    {
        Assert.DoesNotContain(new StrategyStore(w.Db).Runs(1000), r => r.Role == Referee.RunRole);
        Assert.Empty(new Promotions(w.Db).All());
        Assert.Empty(new PublicationStore(w.Db).By(Referee.RunRole));
        Assert.Empty(new MissionEventStore(w.Db).OfKind(MissionEventKind.Verdict));
        Assert.Empty(new CouncilBoundaries(w.Db).All());
        Assert.Empty(new MissionEventStore(w.Db).OfKind(MissionEventKind.Boundary));
    }

    // ---- the bounds a verdict was taken under -----------------------------------------------------

    /// <summary>
    /// A PROMOTION'S BOUNDS COME OFF THE FROZEN PROGRAM, NEVER OFF THE VERSION ROW'S OWN COLUMNS.
    ///
    /// <para>The mutant is <c>Referee.Verdict</c> reading <c>version.Timeframe</c> — a restatement
    /// some earlier writer put in the row — instead of <c>program.Freshness</c>, the text it just
    /// parsed and just proved still hashes to the version id. This fixture makes the two disagree on
    /// purpose: <c>Given</c> records the version row with no bounds at all while the source declares
    /// three, which is exactly the shape a row written before schema 19 has. Under the mutant the
    /// promotion restates the row's own answer — null — and the verdict says nothing about the
    /// program it judged.</para>
    /// </summary>
    [Fact]
    public async Task A_promotion_carries_the_bounds_of_the_frozen_program_not_the_version_rows_columns()
    {
        var w = await Given(program: ProfitableText);
        Assert.Null(new StrategyStore(w.Db).VersionById(w.VersionId)!.Freshness);

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(
            new FreshnessBounds(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30)),
            verdict.Promotion!.Freshness);
        await w.Gw.DisposeAsync();
    }

    // ---- the research evidence a verdict measures (U-referee-v2a, item 2) --------------------------

    /// <summary>The first day of the synthetic research streams below.</summary>
    static readonly DateOnly StreamDay0 = new(2026, 3, 1);

    /// <summary>
    /// A RESEARCH RUN OF <paramref name="version"/> WITH A 1x STREAM OF <paramref name="days"/> KNOWN DAYS (none for 0), recorded
    /// through the app's own writer and charged as a trial of the world's campaign. Its window is the pre-cutoff hour unless
    /// <paramref name="windowTo"/> says otherwise — a run over the held-back minutes is (g)'s.
    /// </summary>
    static string StreamedRun(World w, string name, int days, int seed, string? version = null, DateTimeOffset? windowTo = null,
        double mean = 0.001, IEnumerable<double>? returns = null)
    {
        var runId = "run-" + name;
        var of = version ?? w.VersionId;
        var model = "fees=0.001;slippage=0.0002;increment=1;capital=10000";
        IReadOnlyList<StrategyStreamRow> streams = days == 0
            ? []
            : [new StrategyStreamRow(runId, 1, model, "friction-" + name, "trace-" + name, "COMPLETED", DailyReturns.Version,
                null, DeflationTests.Series(StreamDay0, returns ?? DeflationTests.Normal(seed, days, mean, 0.01)))];
        new StrategyStore(w.Db).RecordRun(new StrategyRunRow(
            runId, of, w.Set.Id, w.Set.NormalisedSha256, Bar0, windowTo ?? Bar0.AddMinutes(HoldoutAtBar - 1), model,
            BacktestOutcome.COMPLETED.ToString(), null, 60, 1, 1, 1, 2, 10, 0, 0, 1m, 0m, 1m, 0m, "trace-" + name, At,
            CouncilRoles.Research, "attempt-1"), [], streams);
        var trial = w.Gw.Campaigns.RegisterTrial(w.Campaign.Id, of, runId, EvaluationClass.Research, At);
        Assert.True(trial.Ok, trial.Why);
        return runId;
    }

    /// <summary>A second accepted version, for a trial of another program in the same lineage.</summary>
    static string OtherVersion(World w)
    {
        var parsed = StrategyParser.Parse(LosingText).Program!;
        new StrategyStore(w.Db).RecordVersion(new StrategyVersionRow(
            parsed.StrategyId, parsed.Source, parsed.Canonical, parsed.Manifest,
            StrategyStore.InterpreterBuild, ParseVerdict.Accepted, parsed.WarmUpBars, Bar0, CouncilRoles.Research, "attempt-1"));
        return parsed.StrategyId;
    }

    /// <summary>Every column of every row of <paramref name="table"/>, as the database holds it, one line a row.</summary>
    static string Bytes(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT * FROM {table} ORDER BY 1");
        using var r = c.ExecuteReader();
        var text = new StringBuilder();
        while (r.Read())
        {
            for (var i = 0; i < r.FieldCount; i++)
                text.Append(r.GetName(i)).Append('=').Append(Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)).Append('|');
            text.Append('\n');
        }
        return text.ToString();
    });

    static long Count(Database db, string table) => db.Read(_ =>
    {
        using var c = db.Cmd($"SELECT COUNT(*) FROM {table}");
        return (long)c.ExecuteScalar()!;
    });

    /// <summary>
    /// (e) A VERDICT RECORDS ITS RESEARCH EVIDENCE, AND ITS PROMOTION IS UNCHANGED.
    ///
    /// <para>The version has a 139-day research stream, a second version of the lineage an uncorrelated 139-day one, and a
    /// third trial no stream at all. The verdict records the candidate (its run, friction and trace), blocks k of 8, M 3
    /// with one unstreamed, N_eff 3 (the unstreamed trial clusters alone), a noise ceiling above zero and a DSR — each gate
    /// <c>inconclusive</c> with its power, which at 139 days is far below 0.50. And the promotion row and the note are byte
    /// for byte those of the same verdict over a lineage with no research stream at all, which measured nothing: no clause
    /// read a new figure. RED on the base: the referee measured nothing and had no <c>Research</c> to read.</para>
    /// </summary>
    [Fact]
    public async Task A_verdict_records_its_research_evidence_and_its_promotion_is_unchanged()
    {
        var bare = await Given();
        using var _1 = bare.Db;
        var based = RefereeOf(bare).Verdict(bare.VersionId, bare.Campaign.Id);
        Assert.True(based.Promoted, based.Why);

        var w = await Given();
        using var _2 = w.Db;
        var candidate = StreamedRun(w, "candidate", 139, 11);
        StreamedRun(w, "other", 139, 12, OtherVersion(w));
        StreamedRun(w, "streamless", 0, 0);

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Promoted, verdict.Why);

        Assert.Equal(Bytes(bare.Db, "strategy_promotion"), Bytes(w.Db, "strategy_promotion"));
        Assert.Equal(
            new PublicationStore(bare.Db).By(Referee.RunRole).Select(p => p.Content),
            new PublicationStore(w.Db).By(Referee.RunRole).Select(p => p.Content));
        Assert.Equal(RefereeFeedback.Text(based.Promotion!), RefereeFeedback.Text(verdict.Promotion!));

        // THE BASE'S LINEAGE HAD NO STREAM: recorded so, every statistic unknown.
        var none = Assert.Single(RefereeOf(bare).Research.All());
        Assert.Null(none.CandidateRunId);
        Assert.Equal((GateStatus.NoStream, GateStatus.NoStream), (none.BlocksStatus, none.DsrStatus));
        Assert.Null(none.Dsr);

        var e = RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId);
        Assert.NotNull(e);
        Assert.Equal(ResearchEvidence.Version, e!.Method);
        Assert.Equal((candidate, "friction-candidate", "trace-candidate"), (e.CandidateRunId, e.FrictionSha256, e.TraceSha256));
        Assert.Equal(139, e.Days);
        Assert.Equal(8, e.BlocksKnown);
        Assert.InRange(e.BlocksPositive, 0, 8);
        Assert.Equal((3, 1, 3), (e.TrialsM, e.TrialsUnstreamed, e.NEff));
        Assert.True(e.Ceiling > 0, $"ceiling {e.Ceiling}");
        Assert.NotNull(e.Dsr);
        Assert.NotNull(e.Sr);
        Assert.Equal(GatePower.Blocks(139), e.BlocksPower, 12);
        Assert.Equal(GatePower.Dsr(139, 3), e.DsrPower, 12);
        Assert.True(e.BlocksPower < GatePower.Discriminates && e.DsrPower < GatePower.Discriminates);
        Assert.Equal((GateStatus.Inconclusive, GateStatus.Inconclusive, GateStatus.Inconclusive),
            (e.BlocksStatus, e.DsrStatus, e.FamilyStatus));
        Assert.Equal(At, e.At);
        Assert.Equal([ResearchEvidence.Streamed, ResearchEvidence.Streamed, ResearchEvidence.NoStream],
            e.Trials.Select(t => t.Reading));

        // ASKED AGAIN, THE FIRST MEASUREMENT STANDS: one row, the same one.
        var again = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.Equal(verdict.Promotion!.Id, again.Promotion!.Id);
        Assert.Equal(e with { Trials = [] }, Assert.Single(RefereeOf(w).Research.All()) with { Trials = [] });
    }

    /// <summary>
    /// (f) A STOPPED OR REFUSED VERDICT RECORDS NO RESEARCH EVIDENCE. Stopped before its charge, stopped during its holdout
    /// run — after the measurement was taken in memory — and refused for a campaign whose policy this build does not
    /// implement: none of the three writes a row of either table. The measurement lands in the verdict's own write or not
    /// at all.
    /// </summary>
    [Fact]
    public async Task A_stopped_or_refused_verdict_records_no_research_evidence()
    {
        var w = await Given(verdicts: 1);
        using var _1 = w.Db;
        StreamedRun(w, "candidate", 139, 11);

        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id, stop: before.Token).Stopped);
        }
        Assert.Equal((0L, 0L), (Count(w.Db, "referee_research"), Count(w.Db, "referee_research_trial")));

        using (var during = new CancellationTokenSource())
        {
            var stopping = new Referee(w.Db, () =>
            {
                during.Cancel();
                return At;
            });
            Assert.True(stopping.Verdict(w.VersionId, w.Campaign.Id, stop: during.Token).Stopped);
        }
        Assert.Equal((0L, 0L), (Count(w.Db, "referee_research"), Count(w.Db, "referee_research_trial")));

        var campaigns = new CampaignStore(w.Db);
        campaigns.Close(w.Campaign.Id, At);
        var odd = campaigns.Open("odd policy", w.Set, 5, 2, At, policy: "promote whatever you like");
        Assert.True(odd.Ok, odd.Why);
        Assert.False(RefereeOf(w).Verdict(w.VersionId, odd.Campaign!.Id).Ok);
        Assert.Equal((0L, 0L), (Count(w.Db, "referee_research"), Count(w.Db, "referee_research_trial")));
    }

    /// <summary>
    /// (g) RESEARCH EVIDENCE READS NO HOLDOUT BAR. A research run of the version whose recorded window reaches the
    /// campaign's cutoff — its stream the longer of the two — is not read: it is <c>held-back</c>, counted in M and
    /// clustered alone, and the candidate is the shorter stream over the pre-cutoff hour. The referee's own holdout run is
    /// no trial and keeps no stream. Read as a candidate, the held-back stream's 300 days would have been measured from
    /// bars past the cutoff.
    /// </summary>
    [Fact]
    public async Task Research_evidence_reads_no_holdout_bar()
    {
        var w = await Given();
        using var _1 = w.Db;
        var inside = StreamedRun(w, "inside", 139, 11);
        var over = StreamedRun(w, "over", 300, 13, windowTo: Bar0.AddMinutes(HoldoutAtBar + 30));

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Ok, verdict.Why);

        var e = RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId)!;
        Assert.Equal(inside, e.CandidateRunId);
        Assert.Equal(139, e.Days);
        Assert.Equal((2, 1, 2), (e.TrialsM, e.TrialsUnstreamed, e.NEff));
        Assert.Equal(ResearchEvidence.HeldBack, e.Trials.Single(t => t.RunId == over).Reading);
        Assert.DoesNotContain(e.Trials, t => t.RunId == verdict.Promotion!.HoldoutRunId);
    }

    /// <summary>
    /// (h) THE RESEARCH-EVIDENCE TABLES ARRIVE AT RUNG 33, A CRASH BEFORE ITS STAMP RUNS IT AGAIN OVER WHAT IS THERE, AND
    /// NOTHING BUT THE ONE INSERT WRITES THEM.
    /// </summary>
    [Fact]
    public async Task Research_evidence_arrives_at_its_rung_and_has_one_writer()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 33,
            "the research-evidence tables arrive at rung 33; this build says " + Versions.DatabaseSchemaVersion);
        var w = await Given();
        StreamedRun(w, "candidate", 139, 11);
        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Ok);
        var before = Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial");
        var file = w.Db.Read(c => c.DataSource);
        await w.Gw.DisposeAsync();
        w.Db.Dispose();

        // A CRASH BETWEEN THE RUNG'S STATEMENTS AND ITS STAMP: every table and row in place, the stamp one lower.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = "UPDATE meta SET value='32' WHERE key='schema_version';";
            c.ExecuteNonQuery();
        }

        using (var rerun = new Database(file))
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return (string)c.ExecuteScalar()!;
            }));
            Assert.Equal(before, Bytes(rerun, "referee_research") + Bytes(rerun, "referee_research_trial"));
        }

        var rewrite = new System.Text.RegularExpressions.Regex(
            @"(UPDATE\s+referee_research(_trial)?\b|DELETE\s+FROM\s+referee_research(_trial)?\b|REPLACE\s+INTO\s+referee_research(_trial)?\b"
            + @"|INSERT\s+OR\s+\w+\s+INTO\s+referee_research(_trial)?\b|DROP\s+TABLE\s+(IF\s+EXISTS\s+)?referee_research(_trial)?\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var writers = Directory.GetFiles(Path.Combine(DayOnePrograms.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(l => rewrite.IsMatch(l.line) || l.line.Contains("INSERT INTO referee_research", StringComparison.Ordinal))
            .Select(l => $"{Path.GetFileName(l.f)}: {l.line.Trim()}")
            .Order(StringComparer.Ordinal)
            .ToList();
        // Beside the one writer, rung 36's rebuild (the evaluator joining the key), which copies every row as it was; in
        // ordinal order, because the file system's is not one.
        Assert.Equal(
            ["Database.cs: INSERT INTO referee_research(campaign_id, version_id, method, evaluator_version, candidate_run_id,",
             "Database.cs: INSERT INTO referee_research_trial(campaign_id, version_id, method, evaluator_version, ordinal, run_id,",
             "ResearchEvidence.cs: INSERT INTO referee_research({Cols})",
             "ResearchEvidence.cs: INSERT INTO referee_research_trial(campaign_id, version_id, method, evaluator_version, ordinal, run_id, cluster, reading)"],
            writers);
    }

    /// <summary>
    /// ITEM 3 — THE REPORT SHOWS A VERDICT'S RESEARCH EVIDENCE BESIDE IT, AND STILL NO HOLDOUT FIGURE. The verdict line in
    /// "measured by TradeAgent" carries the DSR, N_eff of M, the noise ceiling and the blocks, each with its status and
    /// power, and the family gate — research data, which agents may read (R04 :309) — and the holdout run's own figures
    /// stay out of the document exactly as before. A verdict whose lineage had no stream says so.
    /// </summary>
    [Fact]
    public async Task The_report_shows_a_verdicts_research_evidence_beside_it()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;
        StreamedRun(w, "candidate", 139, 11);
        StreamedRun(w, "streamless", 0, 0);
        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Promoted, verdict.Why);
        var e = RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId)!;

        var text = DailyReportText.Render(w.Gw.Reports.Compose(Midday()));
        var line = Assert.Single(text.Split('\n'), l => l.Contains($"PROMOTED version {w.VersionId[..12]}", StringComparison.Ordinal));

        Assert.Contains("Research evidence (research streams only, shown and not enforced), over run run-candidat's 139 known days",
            line, StringComparison.Ordinal);
        Assert.Contains($"DSR {e.Dsr!.Value.ToString("0.00", CultureInfo.InvariantCulture)} — inconclusive, power "
            + $"{e.DsrPower.ToString("0.00", CultureInfo.InvariantCulture)} of the 0.50 it needs", line, StringComparison.Ordinal);
        Assert.Contains("M 2 (1 without a readable stream), N_eff 2, noise ceiling "
            + $"{e.Ceiling!.Value.ToString("0.00", CultureInfo.InvariantCulture)} annual Sharpe", line, StringComparison.Ordinal);
        Assert.Contains($"blocks {e.BlocksPositive}/8 positive — inconclusive, power "
            + $"{e.BlocksPower.ToString("0.00", CultureInfo.InvariantCulture)}", line, StringComparison.Ordinal);
        Assert.Contains("family PBO: no family — inconclusive", line, StringComparison.Ordinal);

        var run = new StrategyStore(w.Db).RunById(verdict.Promotion!.HoldoutRunId)!;
        Assert.DoesNotContain(run.TraceSha256, text, StringComparison.Ordinal);
        Assert.DoesNotContain($"net {run.NetPnl?.ToString(CultureInfo.InvariantCulture)}", line, StringComparison.Ordinal);

        var bare = await Given(frozenAt: Bar0);
        using var _2 = bare.Db;
        Assert.True(RefereeOf(bare).Verdict(bare.VersionId, bare.Campaign.Id).Promoted);
        Assert.Contains("Research evidence (research streams only, shown and not enforced): no research stream for this "
            + "version in its lineage; M 0 (0 without a readable stream), N_eff 0",
            DailyReportText.Render(bare.Gw.Reports.Compose(Midday())), StringComparison.Ordinal);
    }

    /// <summary>
    /// (c, <c>U-evidence-evaluator-label</c>) RESEARCH EVIDENCE COUNTS A TRIAL MEASURED UNDER OTHER SEMANTICS, BUT USES NONE
    /// OF ITS RETURNS. Beside the candidate's 139-day stream, two trials of the lineage carry streams their runs' labels say
    /// another evaluator measured — one labelled <c>backtest=1</c>, one recorded before rung 36 (NULL, unknown) — and the
    /// first is the version's own with a 300-day stream, which as this build's would have been the candidate. Both are read <c>no-stream</c>, counted in
    /// M and clustered alone, and every figure the verdict records is the figure of the same lineage whose two trials had
    /// no stream at all: not one of their returns reached the deflation. The record says how many were read so, the report
    /// names them, and a lineage with none says nothing of it. RED on the base: the 300-day stream was the candidate.
    /// </summary>
    [Fact]
    public async Task Research_evidence_counts_a_trial_measured_under_other_semantics_but_uses_none_of_its_returns()
    {
        var bare = await Given(frozenAt: Bar0);
        using var _1 = bare.Db;
        StreamedRun(bare, "candidate", 139, 11);
        StreamedRun(bare, "foreign", 0, 0);
        StreamedRun(bare, "unknown", 0, 0, OtherVersion(bare));
        Assert.True(RefereeOf(bare).Verdict(bare.VersionId, bare.Campaign.Id).Promoted);
        var expected = RefereeOf(bare).Research.Of(bare.Campaign.Id, bare.VersionId)!;
        Assert.Equal(0, expected.TrialsOtherSemantics);

        var w = await Given(frozenAt: Bar0);
        using var _2 = w.Db;
        var candidate = StreamedRun(w, "candidate", 139, 11);
        var foreign = StreamedRun(w, "foreign", 300, 13);
        var unknown = StreamedRun(w, "unknown", 139, 14, OtherVersion(w));
        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("UPDATE strategy_run SET evaluator_version = CASE id WHEN $f THEN 'backtest=1;metrics=1;scoring=1' "
                + "ELSE NULL END WHERE id IN ($f, $u)", ("$f", foreign), ("$u", unknown));
            return c.ExecuteNonQuery();
        });
        Assert.Equal(300, new StrategyStore(w.Db).StreamsOf(foreign).Single().Days.Count);

        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        var e = RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId)!;
        Assert.Equal(candidate, e.CandidateRunId);
        Assert.Equal((3, 2, 3), (e.TrialsM, e.TrialsUnstreamed, e.NEff));
        Assert.Equal(2, e.TrialsOtherSemantics);
        Assert.Equal(ResearchEvidence.NoStream, e.Trials.Single(t => t.RunId == foreign).Reading);
        Assert.Equal(ResearchEvidence.NoStream, e.Trials.Single(t => t.RunId == unknown).Reading);

        // NOT ONE OF THEIR RETURNS: every figure is the lineage's whose two trials had no stream.
        Assert.Equal(expected with { Trials = [], TrialsOtherSemantics = 2 }, e with { Trials = [] });
        Assert.Equal(expected.Trials, e.Trials);

        Assert.Contains("M 3 (2 without a readable stream, of which 2 measured under other evaluation semantics), N_eff 3",
            VerdictLine(w), StringComparison.Ordinal);
        Assert.Contains("M 3 (2 without a readable stream), N_eff 3", VerdictLine(bare), StringComparison.Ordinal);
        Assert.DoesNotContain(ResearchEvidence.OtherSemantics, VerdictLine(bare), StringComparison.Ordinal);

        // A MEASUREMENT RECORDED BEFORE THE RUNG APPLIED NO SUCH CHECK: unknown, and the report claims none.
        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("UPDATE referee_research SET trials_other_semantics = NULL");
            return c.ExecuteNonQuery();
        });
        Assert.Null(RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId)!.TrialsOtherSemantics);
        Assert.Contains("M 3 (2 without a readable stream), N_eff 3", VerdictLine(w), StringComparison.Ordinal);
    }

    /// <summary>
    /// A HOLDOUT SET AFTER A VERDICT WITHHOLDS ITS RECORDED RESEARCH EVIDENCE FROM THE REPORT, AND THE RECORD STANDS. The
    /// owner downloads the same bars again and holds back minutes the candidate's research window covers: the run is held
    /// back now (<c>StrategyStore.HeldBack</c>), so its DSR, blocks and ceiling — figures of those minutes — leave the
    /// agent-readable report, which names the measurement as withheld, the run and why. The same for a trial the measurement
    /// read that is not the candidate. The rows are what they were: the measurement stays true about what ran. RED before
    /// the fix: the report printed the recorded figures without asking today's windows.
    /// </summary>
    [Fact]
    public async Task The_report_withholds_research_evidence_a_holdout_set_later_reaches()
    {
        var w = await Given(frozenAt: Bar0);
        using var _1 = w.Db;
        var candidate = StreamedRun(w, "candidate", 139, 11);
        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        var recorded = Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial");
        Assert.Contains("noise ceiling", VerdictLine(w), StringComparison.Ordinal);

        // ANOTHER DOWNLOAD OF THE SAME BARS, CUT AT MINUTE 10: its window reaches the candidate's research window.
        var again = w.Gw.Datasets.Record(w.Set with { Id = 0, Version = "v2", HoldoutFrom = null });
        var held = w.Gw.Datasets.SetHoldout(again, Bar0.AddMinutes(10), EvaluationClass.Research);
        Assert.True(held.Ok, held.Why);

        var line = VerdictLine(w);
        Assert.Contains($"Research evidence withheld: this verdict's measurement read run {candidate[..12]}, which is held back "
            + "now, so not one of its figures is shown — the trades of run", line, StringComparison.Ordinal);
        foreach (var figure in new[] { "DSR", "N_eff", "noise ceiling", "blocks", "power", "known days" })
            Assert.DoesNotContain(figure, line, StringComparison.Ordinal);
        Assert.Equal(recorded, Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial"));

        // A TRIAL THAT IS NOT THE CANDIDATE: the candidate's window ends at minute 5, the other version's trial runs the
        // whole hour, and a later cut at minute 30 reaches the trial alone.
        var t = await Given(frozenAt: Bar0);
        using var _2 = t.Db;
        var own = StreamedRun(t, "candidate", 139, 11, windowTo: Bar0.AddMinutes(5));
        var trial = StreamedRun(t, "other", 139, 12, OtherVersion(t));
        Assert.True(RefereeOf(t).Verdict(t.VersionId, t.Campaign.Id).Promoted);
        Assert.Equal(own, RefereeOf(t).Research.Of(t.Campaign.Id, t.VersionId)!.CandidateRunId);
        var later = t.Gw.Datasets.Record(t.Set with { Id = 0, Version = "v2", HoldoutFrom = null });
        Assert.True(t.Gw.Datasets.SetHoldout(later, Bar0.AddMinutes(30), EvaluationClass.Research).Ok);

        var trialLine = VerdictLine(t);
        Assert.Contains($"Research evidence withheld: this verdict's measurement read run {trial}, which is held back now",
            trialLine, StringComparison.Ordinal);
        Assert.DoesNotContain("DSR", trialLine, StringComparison.Ordinal);
    }

    /// <summary>The report's line for the world's verdict.</summary>
    static string VerdictLine(World w) => Assert.Single(DailyReportText.Render(w.Gw.Reports.Compose(Midday())).Split('\n'),
        l => l.Contains($"PROMOTED version {w.VersionId[..12]}", StringComparison.Ordinal));

    /// <summary>
    /// A VERDICT OVER NO RESEARCH STREAM, OR OVER A DEGENERATE ONE, RECORDS ITS ROW AND CHANGES NOTHING ELSE. One lineage's
    /// only trial has no stream; another's candidate earns exactly 0.1% every day — no variation, so its Sharpe ratio, skew,
    /// kurtosis and DSR cannot be computed — beside a trial of another version that earns exactly 0 every day. Neither
    /// verdict throws; each records its row with every figure that cannot be computed recorded as unknown (NULL, never NaN
    /// or infinity text, and "unknown" in the report); and each promotion row, holdout note and <c>RefereeFeedback.Text</c>
    /// is byte for byte that of the same verdict over a lineage with no trial at all — the closest honest comparison, since
    /// this build has no verdict without its measurement step.
    /// </summary>
    [Fact]
    public async Task A_verdict_over_no_stream_or_a_degenerate_stream_records_its_row_and_changes_nothing()
    {
        var bare = await Given(frozenAt: Bar0);
        using var _1 = bare.Db;
        var based = RefereeOf(bare).Verdict(bare.VersionId, bare.Campaign.Id);
        Assert.True(based.Promoted, based.Why);

        var none = await Given(frozenAt: Bar0);
        using var _2 = none.Db;
        StreamedRun(none, "streamless", 0, 0);

        var flat = await Given(frozenAt: Bar0);
        using var _3 = flat.Db;
        var candidate = StreamedRun(flat, "flat", 139, 0, returns: Enumerable.Repeat(0.001, 139));
        StreamedRun(flat, "idle", 139, 0, OtherVersion(flat), returns: Enumerable.Repeat(0.0, 139));

        foreach (var w in new[] { none, flat })
        {
            var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
            Assert.True(verdict.Promoted, verdict.Why);
            Assert.Equal(Bytes(bare.Db, "strategy_promotion"), Bytes(w.Db, "strategy_promotion"));
            Assert.Equal(
                new PublicationStore(bare.Db).By(Referee.RunRole).Select(p => p.Content),
                new PublicationStore(w.Db).By(Referee.RunRole).Select(p => p.Content));
            Assert.Equal(RefereeFeedback.Text(based.Promotion!), RefereeFeedback.Text(verdict.Promotion!));
            Assert.Single(RefereeOf(w).Research.All());
            foreach (var word in new[] { "NaN", "Infinity", "∞" })
                Assert.DoesNotContain(word, Bytes(w.Db, "referee_research"), StringComparison.Ordinal);
        }

        var unstreamed = RefereeOf(none).Research.Of(none.Campaign.Id, none.VersionId)!;
        Assert.Null(unstreamed.CandidateRunId);
        Assert.Equal((1, 1, 1), (unstreamed.TrialsM, unstreamed.TrialsUnstreamed, unstreamed.NEff));
        Assert.Equal((GateStatus.NoStream, GateStatus.NoStream), (unstreamed.BlocksStatus, unstreamed.DsrStatus));

        var e = RefereeOf(flat).Research.Of(flat.Campaign.Id, flat.VersionId)!;
        Assert.Equal((candidate, 139, 8, 8), (e.CandidateRunId, e.Days, e.BlocksKnown, e.BlocksPositive));
        Assert.Equal((2, 0, 2), (e.TrialsM, e.TrialsUnstreamed, e.NEff));
        Assert.Equal((null, null, null, null), (e.Sr, e.Skew, e.Kurtosis, e.Dsr));
        Assert.Equal(GateStatus.Inconclusive, e.DsrStatus);
        Assert.Contains("DSR unknown — inconclusive", VerdictLine(flat), StringComparison.Ordinal);
    }

    // ---- the sealed holdout record and the remembered-holdout flag (U-referee-v2c) ------------------------------------

    /// <summary>The instant the fixture's holdout starts: the campaign's cutoff.</summary>
    static DateTimeOffset HoldoutStart => Bar0.AddMinutes(HoldoutAtBar);

    /// <summary>The fixture version's attempt, as the launch ledger would hold it: what TradeAgent asked for and what the runtime named.</summary>
    static void Attempt(World w, string? requested, string? effective) => w.Db.Write(_ =>
    {
        using var c = w.Db.Cmd("""
            INSERT INTO ai_attempt(id, started_at, runtime, requested_model, state, effective_model)
            VALUES('attempt-1', $at, 'claude', $req, 'ENDED', $eff)
            """, ("$at", Sql.T(Bar0)), ("$req", requested), ("$eff", effective));
        return c.ExecuteNonQuery();
    });

    /// <summary>
    /// (c) AN UNKNOWN AUTHOR OR AN UNKNOWN CUTOFF FLAGS THE HOLDOUT AS REMEMBERED — and what TradeAgent ASKED for is never
    /// read as the author.
    ///
    /// <para>Each version is frozen after its holdout starts, so its favourable verdict is paper-eligible and its holdout is
    /// history the author's model may have been trained on. The flag is raised for a version whose attempt the ledger does
    /// not hold, for an attempt whose runtime named no model even though TradeAgent requested one with a published cutoff
    /// before the holdout (the mutant: <c>requested_model</c> read as the author), and for a model with no published cutoff;
    /// it is lowered only for a named model whose published cutoff ends before the holdout starts. Q7 (a): any overlap at
    /// all flags.</para>
    /// </summary>
    [Fact]
    public async Task An_unknown_author_or_cutoff_flags_the_holdout_as_remembered()
    {
        var frozen = HoldoutStart.AddMinutes(30);
        var cases = new (string Name, Action<World>? Ledger, string? Model, bool Remembered)[]
        {
            ("no attempt in the ledger", null, null, true),
            ("the runtime named no model", w => Attempt(w, "claude-opus-4-8", null), null, true),
            ("no published cutoff", w => Attempt(w, null, "claude-opus-unpublished"), "claude-opus-unpublished", true),
            ("a cutoff before the holdout", w => Attempt(w, null, "claude-opus-4-8"), "claude-opus-4-8", false),
        };

        foreach (var (name, ledger, model, remembered) in cases)
        {
            var w = await Given(frozenAt: frozen);
            using var _ = w.Db;
            ledger?.Invoke(w);

            var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
            Assert.True(verdict.PaperEligible, $"{name}: {verdict.Promotion?.Reason ?? verdict.Why}");

            var sealedRow = RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId);
            Assert.NotNull(sealedRow);
            Assert.Equal((name, remembered), (name, sealedRow!.Remembered));
            Assert.Equal((name, model), (name, sealedRow.AuthorModel));
            Assert.Equal("attempt-1", sealedRow.AuthorAttempt);

            var flag = RefereeOf(w).Holdouts.Flag(w.Campaign.Id, w.VersionId)!;
            Assert.Equal((name, remembered), (name, flag.Remembered));
            Assert.Equal(HoldoutStart, flag.HoldoutFrom);
            if (remembered)
                Assert.Contains("holdout may be remembered", VerdictLineOf(w, "PAPER-ELIGIBLE"), StringComparison.Ordinal);
            else
            {
                Assert.Equal(new DateOnly(2026, 1, 31), sealedRow.ModelCutoff);
                Assert.Equal($"{ModelCutoffs.AnthropicModel("opus-4-8")} (read {ModelCutoffs.ReadOn})", sealedRow.CutoffSource);
                Assert.DoesNotContain("remembered", VerdictLineOf(w, "PAPER-ELIGIBLE"), StringComparison.Ordinal);
            }
        }

        // ANY OVERLAP FLAGS (Q7 (a)), TO THE DAY — and a holdout at or after the freeze never does, whatever the model.
        var sol = ModelCutoffs.Of("gpt-6.1-sol")!;
        var freeze = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(ModelCutoffs.Remembered(new DateTimeOffset(2026, 4, 30, 23, 59, 0, TimeSpan.Zero), freeze, sol));
        Assert.False(ModelCutoffs.Remembered(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), freeze, sol));
        Assert.True(ModelCutoffs.Remembered(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), freeze, null));
        Assert.False(ModelCutoffs.Remembered(freeze, freeze, null));
        Assert.Null(ModelCutoffs.Of(null));
        Assert.Null(ModelCutoffs.Of("GPT-6.1-SOL"));
    }

    /// <summary>
    /// (d) A HOLDOUT AFTER THE FREEZE IS NEVER FLAGGED: the author's model was trained before the version was written, so
    /// it cannot reach months after it — not even with no attempt, no model and no cutoff, which flag a holdout before the
    /// freeze. The verdict is promoted and its sealed row is written all the same, the flag down.
    /// </summary>
    [Fact]
    public async Task A_holdout_after_the_freeze_is_never_flagged()
    {
        var w = await Given(frozenAt: Bar0);
        using var _ = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.Promoted, verdict.Promotion?.Reason ?? verdict.Why);

        var sealedRow = RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId)!;
        Assert.False(sealedRow.Remembered);
        Assert.Equal(("attempt-1", null, null), (sealedRow.AuthorAttempt, sealedRow.AuthorModel, sealedRow.ModelCutoff));
        Assert.False(RefereeOf(w).Holdouts.Flag(w.Campaign.Id, w.VersionId)!.Remembered);
        Assert.Equal("", RefereeOf(w).Holdouts.Flag(w.Campaign.Id, w.VersionId)!.Words);
        Assert.DoesNotContain("remembered", VerdictLine(w), StringComparison.Ordinal);
    }

    /// <summary>
    /// (e) NO SEALED FIGURE REACHES THE NOTE, THE REPORT, THE SITUATION OR THE ANSWER.
    ///
    /// <para>A four-day holdout, so every figure is a number: the holdout's Sharpe ratio, skew and kurtosis, z, E_h, p_h,
    /// α_j and the wealth after it. Each is read back from the sealed row and spelt every way a surface could spell it — the
    /// round-trip text and two to six decimals — and none is in the note the research process is sent, the report
    /// <c>trade report</c> serves, the Situation a turn is shown or the verdict the caller gets back. Only the flag is shown,
    /// and it carries two dates and a model id.</para>
    /// </summary>
    [Fact]
    public async Task No_sealed_figure_reaches_the_note_the_report_the_situation_or_the_answer()
    {
        var w = await Given(frozenAt: HoldoutStart.AddMinutes(30), bars: HoldoutAtBar + 4 * 1440);
        using var _ = w.Db;
        StreamedRun(w, "candidate", 139, 11);

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(verdict.PaperEligible, verdict.Promotion?.Reason ?? verdict.Why);
        var row = RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId)!;
        Assert.True(row.HoldoutDays >= 4, $"{row.HoldoutDays} known days");

        double?[] sealedFigures = [row.SrH, row.SkewH, row.KurtosisH, row.Z, row.EH, row.PH, row.AlphaJ, row.WealthAfter];
        Assert.All(sealedFigures, f => Assert.NotNull(f));
        var spellings = sealedFigures
            .SelectMany(f => new[] { "R", "0.00", "0.000", "0.0000", "0.00000", "0.000000" }
                .Select(format => f!.Value.ToString(format, CultureInfo.InvariantCulture)))
            .Where(s => s.TrimStart('-').Length >= 5)
            .Distinct()
            .ToList();
        Assert.NotEmpty(spellings);

        var standing = new Promotions(w.Db).Standing(w.VersionId);
        var surfaces = new Dictionary<string, string>
        {
            ["note"] = RefereeFeedback.Text(verdict.Promotion!)
                       + string.Concat(new PublicationStore(w.Db).By(Referee.RunRole).Select(p => p.Content)),
            ["report"] = DailyReportText.Render(w.Gw.Reports.Compose(Midday())),
            ["situation"] = new MissionSituation { Mode = "PAPER", Promoted = MissionSituation.PromotedLine(standing) }.Text(),
            ["answer"] = System.Text.Json.JsonSerializer.Serialize(verdict) + verdict.Why,
        };

        foreach (var (surface, text) in surfaces)
            foreach (var spelt in spellings)
                Assert.False(text.Contains(spelt, StringComparison.Ordinal), $"the {surface} carries sealed figure {spelt}");

        // THE FLAG IS SHOWN, AND IT IS TWO DATES AND A MODEL: no attempt in this ledger, so no model and no cutoff.
        Assert.True(row.Remembered);
        Assert.Contains($"holdout may be remembered: it starts {HoldoutStart.UtcDateTime:yyyy-MM-dd}",
            surfaces["report"], StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SEALED TABLE ARRIVES AT RUNG 35, AND A CRASH BEFORE ITS STAMP RUNS IT AGAIN OVER WHAT IS THERE: a row written at
    /// this build's stamp survives the rung running a second time, the stamp one lower, and the key holds — one row per
    /// campaign, version and method, first writer wins.
    /// </summary>
    [Fact]
    public async Task The_sealed_holdout_table_arrives_at_rung_35_and_reruns_over_its_rows()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 35, $"this build says {Versions.DatabaseSchemaVersion}");
        var w = await Given(frozenAt: Bar0);
        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        var before = RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId)!;
        var file = w.Db.Read(c => c.DataSource);
        await w.Gw.DisposeAsync();
        w.Db.Dispose();

        // A CRASH BETWEEN THE RUNG'S STATEMENTS AND ITS STAMP: the table and its row in place, the stamp one lower.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = "UPDATE meta SET value='34' WHERE key='schema_version';";
            c.ExecuteNonQuery();
        }

        using (var rerun = new Database(file))
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), rerun.Read(_ =>
            {
                using var c = rerun.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return (string)c.ExecuteScalar()!;
            }));
            var holdouts = new RefereeHoldout(rerun);
            Assert.Equal(before, holdouts.SealedOf(w.Campaign.Id, w.VersionId));

            // FIRST WRITER WINS: a second row for the same campaign, version and method writes nothing.
            holdouts.Record(before with { Remembered = true, AddisStatus = AlphaWealth.Rejected });
            Assert.Equal(before, holdouts.SealedOf(w.Campaign.Id, w.VersionId));
        }

        // AND ONE WRITER: nothing in src updates, deletes, replaces or inserts into the table but the one insert.
        var rewrite = new System.Text.RegularExpressions.Regex(
            @"(UPDATE\s+referee_holdout\b|DELETE\s+FROM\s+referee_holdout\b|REPLACE\s+INTO\s+referee_holdout\b"
            + @"|INSERT\s+(OR\s+\w+\s+)?INTO\s+referee_holdout\b|DROP\s+TABLE\s+(IF\s+EXISTS\s+)?referee_holdout\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var writers = Directory.GetFiles(Path.Combine(DayOnePrograms.RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => File.ReadAllLines(f).Select(line => (f, line)))
            .Where(l => rewrite.IsMatch(l.line))
            .Select(l => $"{Path.GetFileName(l.f)}: {l.line.Trim()}")
            .Order(StringComparer.Ordinal)
            .ToList();
        // Beside the one writer, rung 36's rebuild (the evaluator joining the key), which copies every row as it was; in
        // ordinal order, because the file system's is not one.
        Assert.Equal(["Database.cs: INSERT INTO referee_holdout(campaign_id, version_id, method, evaluator_version, research_row,",
            "RefereeHoldout.cs: INSERT INTO referee_holdout({Cols}, evaluator_version)"], writers);
    }

    /// <summary>
    /// A VERDICT RECORDED WITHOUT A SEALED ROW GAINS NONE WHEN IT IS ASKED AGAIN — the sealed row is written only in the
    /// write that records the verdict's promotion and its research measurement for the first time (finding 1 of the
    /// review). A verdict from before rung 35 holds its v2a row and no sealed row; asking it again re-runs the holdout and
    /// measures the research again, and a row sealed then would set a fidelity read off research measured NOW beside a
    /// <c>research_row</c> naming the older, immutable measurement the verdict was judged beside — a record that is not
    /// true about what ran. The imitation deletes the sealed row of a recorded verdict (the shape a rung-33 or rung-34
    /// verdict has); the re-ask is made twice — under the campaign's pinned model, which finds its promotion recorded, and
    /// under another model, which records a new promotion over the same, older research row — and once more with the v2a
    /// row gone too, the shape of a verdict from before rung 33. None seals. The mutants are the guard removed (the seal
    /// written in every write) and either half of it alone.
    /// </summary>
    [Fact]
    public async Task A_verdict_asked_again_with_no_sealed_row_gains_none()
    {
        var w = await Given(frozenAt: Bar0);
        using var _ = w.Db;
        StreamedRun(w, "candidate", 139, 11);
        var first = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(first.Promoted, first.Promotion?.Reason ?? first.Why);
        Assert.NotNull(RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId));

        // A VERDICT FROM BEFORE THE RUNG: its v2a row in place, no sealed row.
        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("DELETE FROM referee_holdout");
            return c.ExecuteNonQuery();
        });
        Assert.Null(RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId));
        var research = Bytes(w.Db, "referee_research");

        var again = new Referee(w.Db, () => At.AddHours(1)).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(again.Promoted, again.Promotion?.Reason ?? again.Why);
        Assert.Equal(first.Promotion!.Id, again.Promotion!.Id);
        Assert.Null(RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId));

        var other = new Referee(w.Db, () => At.AddHours(2)).Verdict(w.VersionId, w.Campaign.Id,
            ExecutionModel.Declare(increment: 1m, capital: 20_000m).Model!);
        Assert.True(other.Ok, other.Why);
        Assert.NotEqual(first.Promotion.Id, other.Promotion!.Id);
        Assert.Null(RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId));

        Assert.Equal(research, Bytes(w.Db, "referee_research"));

        // AND A VERDICT FROM BEFORE RUNG 33, with no v2a row either: asked again, it records its research now (v2a's own
        // first writer) and still gains no sealed row, because its promotion was recorded before.
        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("DELETE FROM referee_research_trial; DELETE FROM referee_research;");
            return c.ExecuteNonQuery();
        });
        var older = new Referee(w.Db, () => At.AddHours(3)).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(older.Promoted, older.Promotion?.Reason ?? older.Why);
        Assert.Equal(first.Promotion.Id, older.Promotion!.Id);
        Assert.NotNull(RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId));
        Assert.Null(RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId));

        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
    }

    /// <summary>
    /// THE EVALUATOR IS IN THE MEASUREMENT'S IDENTITY (<c>U-evidence-evaluator-label</c>, the review's P1): a verdict asked
    /// again under another evaluator measures its research and seals its holdout under its own, and the earlier
    /// evaluator's rows stand beside them, untouched and unread. The home is one whose verdict, over a streamed research
    /// trial, was recorded by a build under evaluator A — every run, the promotion (its id hashes the evaluator) and every
    /// research and sealed row labelled A, where the table carries a label. Asked again under this build with no new
    /// research run, the trial is A's: counted in M, none of its returns used, so the measurement has no candidate — and
    /// that is what is read back, with a sealed row of this build's beside it. RED before the fix: the measurement under
    /// this build was discarded by the (campaign, version, method) key, <c>Of</c> served A's — a candidate whose returns
    /// another evaluator measured — and the seal guard, finding nothing measured, sealed nothing for this build, ever.
    /// </summary>
    [Fact]
    public async Task A_verdict_asked_again_under_another_evaluator_is_measured_and_sealed_under_its_own()
    {
        var w = await Given(frozenAt: Bar0);
        using var _ = w.Db;
        var trial = StreamedRun(w, "candidate", 139, 11);
        var first = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(first.Promoted, first.Promotion?.Reason ?? first.Why);
        Assert.Equal(trial, RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId)!.CandidateRunId);

        AsIfJudgedUnder(w, "backtest=1;metrics=1;scoring=1");
        var earlier = (Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial") + Bytes(w.Db, "referee_holdout"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var again = new Referee(w.Db, () => At.AddHours(1)).Verdict(w.VersionId, w.Campaign.Id);
        Assert.True(again.Promoted, again.Promotion?.Reason ?? again.Why);
        Assert.Equal(Referee.EvaluatorVersion, again.Promotion!.EvaluatorVersion);
        Assert.Equal(2L, Count(w.Db, "strategy_promotion"));

        var e = RefereeOf(w).Research.Of(w.Campaign.Id, w.VersionId);
        Assert.NotNull(e);
        Assert.Null(e.CandidateRunId);
        Assert.Equal((1, 1, 1), (e.TrialsM, e.TrialsUnstreamed, e.TrialsOtherSemantics));
        Assert.Equal((trial, ResearchEvidence.NoStream), Assert.Single(e.Trials) is var t ? (t.RunId, t.Reading) : default);
        Assert.Equal(GateStatus.NoStream, e.DsrStatus);

        var sealedRow = RefereeOf(w).Holdouts.SealedOf(w.Campaign.Id, w.VersionId);
        Assert.NotNull(sealedRow);
        Assert.Equal(e.Method, sealedRow.ResearchRow);
        Assert.Equal(e.At, sealedRow.At);
        Assert.NotNull(RefereeOf(w).Holdouts.Flag(w.Campaign.Id, w.VersionId));

        // A'S ROWS STAND, BYTE FOR BYTE, BESIDE THIS BUILD'S ONE OF EACH.
        var now = Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial") + Bytes(w.Db, "referee_holdout");
        Assert.All(earlier, row => Assert.Contains(row + "\n", now, StringComparison.Ordinal));
        Assert.Equal((2L, 2L, 2L),
            (Count(w.Db, "referee_research"), Count(w.Db, "referee_research_trial"), Count(w.Db, "referee_holdout")));

        // AND ASKED A THIRD TIME UNDER THIS BUILD: first writer wins, and nothing more is written.
        var settled = Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial") + Bytes(w.Db, "referee_holdout");
        Assert.True(new Referee(w.Db, () => At.AddHours(2)).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        Assert.Equal(settled,
            Bytes(w.Db, "referee_research") + Bytes(w.Db, "referee_research_trial") + Bytes(w.Db, "referee_holdout"));
        Assert.Equal(1, w.Gw.Campaigns.JudgementsSpent(w.Campaign.Id));
    }

    /// <summary>
    /// RUNG 36 REBUILDS THE MEASUREMENT TABLES WITH THE EVALUATOR IN THEIR KEY AND KEEPS EVERY ROW, AND A CRASH BEFORE ITS
    /// STAMP RUNS NONE OF IT AGAIN. A home at rung 35 — <c>referee_research</c>, its trials and <c>referee_holdout</c> keyed
    /// (campaign, version, method), one verdict's rows in each — is opened by this build: every row is there, byte for byte
    /// but for the new columns, which are NULL (unknown, from before the rung), and no reader of this build's evaluator
    /// serves it. Then the stamp is set one lower, as a crash between the rebuild's commit and the stamp leaves it, and the
    /// home opens at this build's stamp with every row as it was.
    /// </summary>
    [Fact]
    public async Task The_measurement_tables_are_rebuilt_at_rung_36_with_every_row_kept_and_a_crash_before_its_stamp_recovers()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= 36, $"this build says {Versions.DatabaseSchemaVersion}");
        var w = await Given(frozenAt: Bar0);
        StreamedRun(w, "candidate", 139, 11);
        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Promoted);
        string Tables(Database db) =>
            Bytes(db, "referee_research") + Bytes(db, "referee_research_trial") + Bytes(db, "referee_holdout");
        var expected = Tables(w.Db)
            .Replace($"evaluator_version={Referee.EvaluatorVersion}|", "evaluator_version=|", StringComparison.Ordinal)
            .Replace("trials_other_semantics=0|", "trials_other_semantics=|", StringComparison.Ordinal);
        Assert.Equal(3, expected.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        var file = w.Db.Read(c => c.DataSource);
        await w.Gw.DisposeAsync();
        w.Db.Dispose();

        // A HOME AT RUNG 35: the three tables as rungs 33 and 35 made them, the same rows, the stamp before this rung.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = """
                PRAGMA foreign_keys=OFF;
                BEGIN;
                ALTER TABLE referee_research_trial RENAME TO t36; ALTER TABLE referee_research RENAME TO r36;
                ALTER TABLE referee_holdout RENAME TO h36;
                CREATE TABLE referee_research(
                  campaign_id INTEGER NOT NULL REFERENCES strategy_campaign(id), version_id TEXT NOT NULL REFERENCES strategy_version(id),
                  method INTEGER NOT NULL, candidate_run_id TEXT REFERENCES strategy_run(id), friction_sha256 TEXT, trace_sha256 TEXT,
                  days INTEGER NOT NULL, blocks_known INTEGER NOT NULL, blocks_positive INTEGER NOT NULL, trials_m INTEGER NOT NULL,
                  trials_unstreamed INTEGER NOT NULL, n_eff INTEGER NOT NULL, sr TEXT, skew TEXT, kurtosis TEXT, sr0 TEXT, dsr TEXT,
                  ceiling TEXT, blocks_power TEXT NOT NULL, dsr_power TEXT NOT NULL, blocks_status TEXT NOT NULL,
                  dsr_status TEXT NOT NULL, at TEXT NOT NULL, PRIMARY KEY(campaign_id, version_id, method));
                CREATE TABLE referee_research_trial(
                  campaign_id INTEGER NOT NULL, version_id TEXT NOT NULL, method INTEGER NOT NULL, ordinal INTEGER NOT NULL,
                  run_id TEXT NOT NULL, cluster INTEGER NOT NULL,
                  reading TEXT NOT NULL CHECK(reading IN ('streamed','no-stream','held-back')),
                  PRIMARY KEY(campaign_id, version_id, method, run_id),
                  FOREIGN KEY(campaign_id, version_id, method) REFERENCES referee_research(campaign_id, version_id, method));
                CREATE TABLE referee_holdout(
                  campaign_id INTEGER NOT NULL REFERENCES strategy_campaign(id), version_id TEXT NOT NULL REFERENCES strategy_version(id),
                  method INTEGER NOT NULL, research_row INTEGER NOT NULL, holdout_days INTEGER NOT NULL, sr_h TEXT, skew_h TEXT,
                  kurtosis_h TEXT, z TEXT, z_status TEXT NOT NULL, e_h TEXT, p_h TEXT, alpha_j TEXT NOT NULL,
                  wealth_before TEXT NOT NULL, wealth_after TEXT NOT NULL, addis_status TEXT NOT NULL, author_attempt TEXT,
                  author_model TEXT, model_cutoff TEXT, cutoff_source TEXT, remembered INTEGER NOT NULL, at TEXT NOT NULL,
                  PRIMARY KEY(campaign_id, version_id, method));
                INSERT INTO referee_research SELECT campaign_id, version_id, method, candidate_run_id, friction_sha256, trace_sha256,
                  days, blocks_known, blocks_positive, trials_m, trials_unstreamed, n_eff, sr, skew, kurtosis, sr0, dsr, ceiling,
                  blocks_power, dsr_power, blocks_status, dsr_status, at FROM r36;
                INSERT INTO referee_research_trial SELECT campaign_id, version_id, method, ordinal, run_id, cluster, reading FROM t36;
                INSERT INTO referee_holdout SELECT campaign_id, version_id, method, research_row, holdout_days, sr_h, skew_h,
                  kurtosis_h, z, z_status, e_h, p_h, alpha_j, wealth_before, wealth_after, addis_status, author_attempt,
                  author_model, model_cutoff, cutoff_source, remembered, at FROM h36;
                DROP TABLE t36; DROP TABLE r36; DROP TABLE h36;
                UPDATE meta SET value='35' WHERE key='schema_version';
                COMMIT;
                """;
            c.ExecuteNonQuery();
        }

        using (var db = new Database(file))
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Stamp(db));
            Assert.Equal(expected, Tables(db));
            var referee = new Referee(db, () => At);
            Assert.Null(referee.Research.Of(w.Campaign.Id, w.VersionId));
            Assert.Null(referee.Holdouts.SealedOf(w.Campaign.Id, w.VersionId));
            Assert.Null(referee.Holdouts.Flag(w.Campaign.Id, w.VersionId));
            Assert.Null(Assert.Single(referee.Research.All()).EvaluatorVersion);
            Assert.Single(Assert.Single(referee.Research.All()).Trials);
            Assert.Empty(db.Read(_ =>
            {
                using var c = db.Cmd("PRAGMA foreign_key_check");
                using var r = c.ExecuteReader();
                var rows = new List<string>();
                while (r.Read()) rows.Add(r.GetString(0));
                return rows;
            }));
        }

        // A CRASH BETWEEN THE REBUILD'S COMMIT AND THE STAMP: the rebuilt tables and every row in place, the stamp one lower.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file};Pooling=False"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            c.CommandText = "UPDATE meta SET value='35' WHERE key='schema_version';";
            c.ExecuteNonQuery();
        }

        using (var rerun = new Database(file))
        {
            Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture), Stamp(rerun));
            Assert.Equal(expected, Tables(rerun));
        }

        static string Stamp(Database db) => db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return (string)c.ExecuteScalar()!;
        });
    }

    /// <summary>
    /// THE HOME AS A BUILD UNDER <paramref name="evaluator"/> WOULD HAVE LEFT IT: every run labelled so, every promotion's
    /// evaluator and its id (which hashes it) re-written to it, and every research and sealed row labelled so where its
    /// table carries the label — the imitation of an earlier build's home, in a test only.
    /// </summary>
    static void AsIfJudgedUnder(World w, string evaluator) => w.Db.Write(_ =>
    {
        using (var defer = w.Db.Cmd("PRAGMA defer_foreign_keys=ON;"))
            defer.ExecuteNonQuery();
        using (var runs = w.Db.Cmd("UPDATE strategy_run SET evaluator_version=$e", ("$e", (object?)evaluator)))
            runs.ExecuteNonQuery();

        var promotions = new Promotions(w.Db);
        var ids = new List<string>();
        using (var c = w.Db.Cmd("SELECT id FROM strategy_promotion"))
        using (var r = c.ExecuteReader())
            while (r.Read()) ids.Add(r.GetString(0));
        foreach (var id in ids)
        {
            var moved = promotions.ById(id)! with { EvaluatorVersion = evaluator };
            using var u = w.Db.Cmd("UPDATE strategy_promotion SET evaluator_version=$e, id=$new WHERE id=$old",
                ("$e", evaluator), ("$new", moved.ComputedId), ("$old", id));
            u.ExecuteNonQuery();
        }

        foreach (var table in new[] { "referee_research", "referee_research_trial", "referee_holdout" })
        {
            using var has = w.Db.Cmd($"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='evaluator_version'");
            if ((long)has.ExecuteScalar()! == 0) continue;
            using var u = w.Db.Cmd($"UPDATE {table} SET evaluator_version=$e", ("$e", evaluator));
            u.ExecuteNonQuery();
        }
        return 0;
    });

    /// <summary>The report's line for the world's verdict, by its verdict word.</summary>
    static string VerdictLineOf(World w, string verdict) => Assert.Single(
        DailyReportText.Render(w.Gw.Reports.Compose(Midday())).Split('\n'),
        l => l.Contains($"{verdict} version {w.VersionId[..12]}", StringComparison.Ordinal));

    /// <summary>Midday on the owner's local day, so the report's window is unambiguous. See DailyReportTests.</summary>
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
