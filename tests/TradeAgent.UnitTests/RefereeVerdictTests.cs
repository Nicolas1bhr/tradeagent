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
        double mean = 0.001)
    {
        var runId = "run-" + name;
        var of = version ?? w.VersionId;
        var model = "fees=0.001;slippage=0.0002;increment=1;capital=10000";
        IReadOnlyList<StrategyStreamRow> streams = days == 0
            ? []
            : [new StrategyStreamRow(runId, 1, model, "friction-" + name, "trace-" + name, "COMPLETED", DailyReturns.Version,
                null, DeflationTests.Series(StreamDay0, DeflationTests.Normal(seed, days, mean, 0.01)))];
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
        Assert.Equal(33, Versions.DatabaseSchemaVersion);
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
            Assert.Equal("33", rerun.Read(_ =>
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
            .ToList();
        Assert.Equal(
            ["ResearchEvidence.cs: INSERT INTO referee_research({Cols})",
             "ResearchEvidence.cs: INSERT INTO referee_research_trial(campaign_id, version_id, method, ordinal, run_id, cluster, reading)"],
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

    /// <summary>Midday on the owner's local day, so the report's window is unambiguous. See DailyReportTests.</summary>
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
