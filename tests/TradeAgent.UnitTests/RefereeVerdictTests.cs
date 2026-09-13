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
    /// AND THERE IS NO OP THAT ASKS FOR A VERDICT OR WRITES A PROMOTION. The whole agent-facing
    /// vocabulary is asked, by name, so an op added later has to be justified here rather than pass
    /// quietly: the caller being judged must not be able to spend the owner's evaluation budget or to
    /// mark its own homework.
    /// </summary>
    [Fact]
    public void No_pipe_op_asks_for_a_verdict_or_writes_a_promotion()
    {
        foreach (var op in GatewaySchema.Ops())
        {
            Assert.DoesNotContain("verdict", op.Op, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("promot", op.Op, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("referee", op.Op, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Midday on the owner's local day, so the report's window is unambiguous. See DailyReportTests.</summary>
    static DateTimeOffset Midday()
    {
        var day = DateTimeOffset.Now.ToLocalTime().Date.AddHours(12);
        return new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
    }
}
