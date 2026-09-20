using System.Globalization;
using System.Text;
using TradeAgent.AgentRuntime;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Gateway;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-paper-verdict — A FAVOURABLE VERDICT OVER NEVER-SERVED *HISTORICAL* MONTHS MAKES A VERSION
/// ELIGIBLE FOR PAPER, AND NEVER FOR LIVE CAPITAL.
///
/// <para><b>What was wrong before this.</b> <c>ScoringPolicyV1</c>'s forward-evidence clause is asked
/// first and answers <c>evidence-precedes-the-freeze</c> for every version frozen after the holdout
/// cutoff — so the figures were never reached at all and the only answer such a version could get was
/// <c>refused</c>. <c>docs/PRINCIPLES.md</c>:67 asks for four distinct meanings ("acceptance of a
/// program, a favourable historical verdict, eligibility for paper observation, and eligibility for
/// live capital") and adds the clause that made this unit necessary: "forward paper evidence cannot be
/// required before the very first paper run that produces it". A loop that can only ever be told "your
/// evidence predates the freeze" can never produce that first paper run.</para>
///
/// <para><b>The deployed contract is preserved and this class holds it.</b> <c>promoted</c> still needs
/// a window that post-dates the freeze;
/// <see cref="A_version_frozen_before_the_cutoff_is_still_promoted_under_V1"/> is that guard, checked
/// here rather than assumed. <c>IsPromoted</c> is FALSE for a paper-eligible standing and the live
/// allocation path refuses one categorically, in words.</para>
///
/// <para><b>The two mutants this class exists to catch.</b> <c>IsPromoted</c> answered true for
/// <c>paper_eligible</c>, which puts the owner's capital behind evidence collected over months the
/// submission may have been written around. And the paper policy's performance clauses SKIPPED — the
/// freeze refusal mapped straight to paper-eligible — which would make a program that loses money over
/// the held-back months eligible for paper observation on the strength of its date alone.</para>
/// </summary>
public class PaperEligibleVerdictTests
{
    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Bar0 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Minutes into the fixture dataset at which the owner's holdout begins.</summary>
    const int HoldoutAtBar = 60;

    /// <summary>
    /// The freeze instant of the PAPER arm: after the held-back window opens, so
    /// <c>ScoringPolicyV1</c>'s first clause refuses it whatever the figures say. That is the whole
    /// point — this is the version whose only possible answer used to be <c>refused</c>.
    /// </summary>
    static DateTimeOffset AfterTheCutoff => Bar0.AddMinutes(HoldoutAtBar + 10);

    /// <summary>
    /// Buys the dip and sells the rip over bars that cycle 96 → 105, so it closes real trades and comes
    /// out ahead. It declares the three execution bounds because the referee refuses to JUDGE a version
    /// that declares none of them (`PromotionBoundsTests`), and none of the three changes the backtest.
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
    /// freeze instant this test chooses. The shape `RefereeVerdictTests` and `VerdictOverPipeTests`
    /// both use, in process, because what is under test here is the referee's own arithmetic.
    /// </summary>
    static async Task<World> Given(DateTimeOffset frozenAt, string program = ProfitableText, int bars = 120)
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
            frozenAt, CouncilRoles.Research, "attempt-1"));

        return new World(gw, db, gw.Datasets.ById(id)!, campaign!, parsed.StrategyId);
    }

    static Referee RefereeOf(World w) => new(w.Db, () => At);

    // ---- item 2: the verdict ----------------------------------------------------------------------

    /// <summary>
    /// A PROFITABLE VERSION FROZEN AFTER THE CUTOFF IS PAPER-ELIGIBLE, AND IT IS NOT PROMOTED.
    ///
    /// <para>The order is the whole design: <c>ScoringPolicyV1</c> is asked first and unchanged, it
    /// answers <c>evidence-precedes-the-freeze</c>, and only then are the paper policy's performance
    /// clauses evaluated ON THE SAME RUN. One holdout run, two standards, and the weaker one cannot
    /// reach a promotion — its answer is a word of its own.</para>
    /// </summary>
    [Fact]
    public async Task A_profitable_version_frozen_after_the_cutoff_is_paper_eligible_and_not_promoted()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal("paper-eligible", verdict.Promotion!.Verdict);
        Assert.Equal("meets-the-paper-policy-on-historical-evidence", verdict.Promotion.Reason);

        // THE WORDS THEMSELVES ARE THE CONTRACT — they are written into rows that outlive this build
        // and read back by later ones — so the constants are pinned to their spellings here rather
        // than used as their own definition everywhere.
        Assert.Equal("paper-eligible", PromotionVerdict.PaperEligible);
        Assert.Equal("paper_eligible", PromotionState.PaperEligible);
        Assert.Equal("meets-the-paper-policy-on-historical-evidence", PromotionReason.MetOnHistory);
        Assert.True(PromotionVerdict.IsKnown(PromotionVerdict.PaperEligible));
        Assert.True(PromotionReason.IsKnown(PromotionReason.MetOnHistory));
        Assert.True(verdict.PaperEligible);
        Assert.True(verdict.Promotion.IsPaperEligible);

        // AND IT IS NOT A PROMOTION, on either of the two objects the money path reads.
        Assert.False(verdict.Promoted, "a paper-eligible verdict read as a promotion");
        Assert.False(verdict.Promotion.IsPromoted, "the row read as a promotion");
        var standing = new Promotions(w.Db).Standing(w.VersionId);
        Assert.Equal("paper_eligible", standing.State);
        Assert.False(standing.IsPromoted, "a paper-eligible standing answered IsPromoted");
        Assert.True(standing.IsPaperEligible);
        Assert.Contains("NOT promoted", standing.Why, StringComparison.Ordinal);

        // AND THE LINE THE TURN READS SAYS BOTH HALVES OF IT: judged favourably, and no capital.
        var line = MissionSituation.PromotedLine(standing);
        Assert.StartsWith("Promoted strategy: none.", line, StringComparison.Ordinal);
        Assert.Contains("PAPER-ELIGIBLE", line, StringComparison.Ordinal);
        Assert.Contains("can be given no capital at all", line, StringComparison.Ordinal);

        // THE HELD-BACK MONTHS WERE READ ONCE. Two runs would be the verdict budget's whole point
        // spent twice over, and the second one is a peek nobody was charged for.
        Assert.Single(w.Gw.Strategies.Runs(50), r => r.Role == Referee.RunRole);

        // AND THE RUN REALLY WAS PROFITABLE: it is the DATE that kept it out of a promotion.
        var run = new StrategyStore(w.Db).RunById(verdict.Promotion.HoldoutRunId)!;
        Assert.True(run.Trades > 0);
        Assert.True(run.NetPnl > 0m, "the fixture program is profitable over these bars");

        // THE ROW IS SCORED BY THE POLICY THAT PRODUCED THE ANSWER, and the campaign holds that sha.
        Assert.Equal(CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1), verdict.Promotion.ScoringPolicySha256);
        Assert.Equal(w.Campaign.PaperPolicySha256, verdict.Promotion.ScoringPolicySha256);
        Assert.NotEqual(w.Campaign.ScoringPolicySha256, verdict.Promotion.ScoringPolicySha256);
        Assert.Equal(CampaignPolicy.PaperV1, w.Campaign.PaperPolicy);
    }

    /// <summary>
    /// AND THE CAMPAIGN MUST HOLD THE PAPER STANDARD THIS BUILD IMPLEMENTS, or nothing is judged.
    ///
    /// <para>The same precommitment the scoring policy has (<c>docs/COUNCIL.md</c>:212): a rewrite of
    /// the words is a rewrite of the standard and cannot be applied to a campaign that fixed a
    /// different one. No writer in this build moves the column — a campaign copies it at open — so the
    /// test writes past the store to put the ledger in the state a later build's edited constant would
    /// leave it in, which is what <c>PromotionLedgerTests</c> does to prove the sha check is the
    /// arithmetic rather than the absence of a caller.</para>
    /// </summary>
    [Fact]
    public async Task A_campaign_that_fixed_another_paper_policy_is_not_judged_by_this_builds_clauses()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;

        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("UPDATE strategy_campaign SET paper_policy_sha256=$sha WHERE id=$id",
                ("$sha", "another-builds-paper-policy"), ("$id", w.Campaign.Id));
            return c.ExecuteNonQuery();
        });

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.False(verdict.Ok, "a campaign that fixed another paper policy was judged by this one");
        Assert.Null(verdict.Promotion);
        Assert.Contains("fixed paper policy another-builds-paper-policy at open", verdict.Why,
            StringComparison.Ordinal);
        Assert.Contains("no verdict was recorded", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(PromotionState.Unjudged, new Promotions(w.Db).Standing(w.VersionId).State);
    }

    /// <summary>
    /// THE RUNG'S ONE BACKFILL: every campaign that predates schema 23 is pinned to this build's own
    /// <see cref="CampaignPolicy.PaperV1"/>.
    ///
    /// <para>No campaign written before the rung fixed a second standard, because none existed for any
    /// of them to fix — so the build's own is the truth about those rows, and an empty column would
    /// refuse a paper verdict on every campaign of every installation that upgrades. Verified by taking
    /// a real database BACK to 22 and reopening it, which is the only way the rung runs over a row that
    /// predates it: a test that asserted the columns exist would be a test of the ALTER and not of the
    /// UPDATE.</para>
    /// </summary>
    [Fact]
    public void The_rung_pins_this_builds_paper_policy_onto_every_campaign_that_predates_it()
    {
        var file = Path.Combine(TestEnv.Home, $"paper-backfill-{Guid.NewGuid():n}.db");
        long id;

        using (var db = new Database(file))
        {
            var datasets = new DatasetStore(db);
            var csv = Path.Combine(Paths.Data, $"paper-{Guid.NewGuid():n}.csv");
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(csv, KlineNormaliser.Header + "\n");

            var setId = datasets.Record(new DatasetRecord(
                0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
                csv, DatasetStore.Sha256(csv)!, 1000, Bar0.AddDays(-300), Bar0.AddDays(60), 0, [],
                false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []));

            Assert.True(datasets.SetHoldout(setId, Bar0, EvaluationClass.Research).Ok);
            var opened = new CampaignStore(db).Open("BTCUSDT 1m v1", datasets.ById(setId)!, 10, 3, At);
            Assert.True(opened.Ok, opened.Why);
            id = opened.Campaign!.Id;
        }

        // BACK TO 22: the two columns gone and the stamp lowered, which is what a campaign opened
        // before this unit landed genuinely looks like.
        using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            raw.Open();
            using var c = raw.CreateCommand();
            // EVERY RUNG ABOVE 22 HAS TO BE UNDONE, not only 23's — a reopen runs all of them, and a
            // half-rolled-back database is one no installation has ever had. `VenueCatalogTests` states
            // the same rule against schema 16.
            c.CommandText = """
                ALTER TABLE strategy_campaign DROP COLUMN paper_policy;
                ALTER TABLE strategy_campaign DROP COLUMN paper_policy_sha256;
                ALTER TABLE strategy_allocation DROP COLUMN scope;
                ALTER TABLE strategy_allocation DROP COLUMN connector_id;
                ALTER TABLE strategy_allocation DROP COLUMN mode;
                ALTER TABLE strategy_allocation DROP COLUMN account_id;
                ALTER TABLE strategy_allocation DROP COLUMN envelope_id;
                DROP TABLE deployment_op;
                DROP TABLE strategy_deployment;
                DROP TABLE paper_envelope;
                DROP TABLE forward_bar;
                DROP TABLE forward_gap;
                DROP TABLE forward_fetch;
                UPDATE meta SET value='22' WHERE key='schema_version';
                """;
            c.ExecuteNonQuery();
        }

        using var reopened = new Database(file);
        var campaign = new CampaignStore(reopened).ById(id)!;

        Assert.Equal(CampaignPolicy.PaperV1, campaign.PaperPolicy);
        Assert.Equal(CampaignPolicy.Sha256Of(CampaignPolicy.PaperV1), campaign.PaperPolicySha256);

        // AND THE STANDARD IT PRECOMMITTED TO IS UNTOUCHED. A backfill that moved `scoring_policy`
        // would be a migration rewriting what a campaign's evidence was judged against.
        Assert.Equal(CampaignPolicy.V1, campaign.ScoringPolicy);
        Assert.Equal(CampaignPolicy.Sha256Of(CampaignPolicy.V1), campaign.ScoringPolicySha256);
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture),
            reopened.Read(_ =>
            {
                using var c = reopened.Cmd("SELECT value FROM meta WHERE key='schema_version'");
                return c.ExecuteScalar()!.ToString();
            }));
    }

    /// <summary>
    /// AND A RENEWAL CARRIES THE PARENT'S PAPER STANDARD, exactly as it carries the parent's scoring
    /// policy: a renewal buys attempts and never an easier standard, and there are now two standards
    /// for that sentence to be true of.
    /// </summary>
    [Fact]
    public async Task A_renewal_carries_the_parents_paper_policy()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;

        var child = w.Gw.Campaigns.Renew(w.Campaign.Id, 5, 2, At);

        Assert.True(child.Ok, child.Why);
        Assert.Equal(w.Campaign.PaperPolicy, child.Campaign!.PaperPolicy);
        Assert.Equal(w.Campaign.PaperPolicySha256, child.Campaign.PaperPolicySha256);
        Assert.Equal(CampaignPolicy.PaperV1, child.Campaign.PaperPolicy);
    }

    /// <summary>
    /// AND THE PERFORMANCE CLAUSES ARE REALLY EVALUATED — this is the second mutant.
    ///
    /// <para>The mutant maps the freeze refusal straight to paper-eligible. It passes every test whose
    /// fixture program makes money, and it makes a program that LOSES money over the held-back months
    /// eligible for paper observation on the strength of its date alone. The refusal here names the
    /// clause that actually failed, which is the informative one; the freeze is never the answer for a
    /// version the paper policy could have judged.</para>
    /// </summary>
    [Fact]
    public async Task An_unprofitable_version_frozen_after_the_cutoff_is_refused_for_the_performance_clause_not_the_freeze()
    {
        var w = await Given(AfterTheCutoff, program: LosingText);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.Equal(PromotionVerdict.Refused, verdict.Promotion!.Verdict);
        Assert.Equal(PromotionReason.NotProfitable, verdict.Promotion.Reason);
        Assert.NotEqual(PromotionReason.PrecedesTheFreeze, verdict.Promotion.Reason);
        Assert.Equal(PromotionState.Refused, new Promotions(w.Db).Standing(w.VersionId).State);
        Assert.True(new StrategyStore(w.Db).RunById(verdict.Promotion.HoldoutRunId)!.NetPnl <= 0m);
    }

    /// <summary>
    /// THE DEPLOYED CONTRACT, CHECKED RATHER THAN ASSUMED: <c>promoted</c> still requires a window that
    /// post-dates the freeze, and a version frozen before the cutoff is judged by V1 exactly as it was.
    /// This is a guard, and it was green before this unit as well as after it.
    /// </summary>
    [Fact]
    public async Task A_version_frozen_before_the_cutoff_is_still_promoted_under_V1()
    {
        var w = await Given(Bar0);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);

        Assert.True(verdict.Ok, verdict.Why);
        Assert.True(verdict.Promoted, verdict.Promotion!.Reason);
        Assert.Equal(PromotionVerdict.Promoted, verdict.Promotion.Verdict);
        Assert.Equal(PromotionReason.Met, verdict.Promotion.Reason);
        Assert.Equal(PromotionState.Promoted, new Promotions(w.Db).Standing(w.VersionId).State);

        // AND IT IS STILL SCORED BY THE CAMPAIGN'S OWN PRECOMMITTED POLICY, not by the paper one.
        Assert.Equal(w.Campaign.ScoringPolicySha256, verdict.Promotion.ScoringPolicySha256);
    }

    // ---- item 3: the money path -------------------------------------------------------------------

    /// <summary>
    /// THE LIVE ALLOCATION PATH REFUSES A PAPER-ELIGIBLE VERSION, IN WORDS — the first mutant.
    ///
    /// <para><c>docs/PRINCIPLES.md</c>:67: "a paper experiment also cannot confer live authority", and
    /// the historical verdict beneath it is weaker still — it is a result over months the submission may
    /// already have been written around. <c>IsPromoted</c> answered true for <c>paper_eligible</c> is
    /// the mutant, and it puts the owner's capital behind exactly that evidence.</para>
    /// </summary>
    [Fact]
    public async Task The_live_allocation_path_refuses_a_paper_eligible_version()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;
        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.Equal("paper-eligible", verdict.Promotion!.Verdict);

        var refused = new Allocations(w.Db).Record(new AllocationRow(
            "", w.VersionId, verdict.Promotion.Id, AllocationPolicy.V1, 1m, null, "USD",
            At, null, "the owner allocated capital to this version", At));

        Assert.False(refused.Ok, "capital was allocated to a paper-eligible version");
        Assert.Null(refused.Allocation);
        Assert.Contains("historical evidence; paper only", refused.Why, StringComparison.Ordinal);
        Assert.Empty(new Allocations(w.Db).For(w.VersionId));
    }

    /// <summary>
    /// A PAPER-ELIGIBLE VERDICT IS INVALIDATED EXACTLY AS A PROMOTION IS. The dataset re-collected under
    /// another sha is the same withdrawal of the same evidence, and a fifth state that escaped
    /// invalidation would be a standing whose truth nothing rechecks.
    /// </summary>
    [Fact]
    public async Task Standing_invalidates_a_paper_eligible_verdict_like_a_promotion()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;
        var promotions = new Promotions(w.Db);

        Assert.True(RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id).Ok);
        Assert.Equal("paper_eligible", promotions.Standing(w.VersionId).State);

        w.Db.Write(_ =>
        {
            using var c = w.Db.Cmd("UPDATE dataset SET normalised_sha256=$sha WHERE id=$id",
                ("$sha", "collected-again-0000"), ("$id", w.Set.Id));
            return c.ExecuteNonQuery();
        });

        var standing = promotions.Standing(w.VersionId);
        Assert.Equal(PromotionState.Invalidated, standing.State);
        Assert.Contains("the months have been collected again", standing.Why, StringComparison.Ordinal);
        Assert.False(standing.IsPromoted);
    }

    // ---- item 4: delivered, and no boundary -------------------------------------------------------

    /// <summary>
    /// A PAPER-ELIGIBLE VERDICT IS DELIVERED TO RESEARCH AND OPENS NO CONSEQUENTIAL BOUNDARY.
    ///
    /// <para><c>docs/PRINCIPLES.md</c>: "every ordinary experiment need not purchase a fixed ceremony".
    /// Paper observation is an ordinary experiment by app policy, so the directors' 24 h review stays
    /// the ceremony of a <c>promoted</c> verdict. The wake and the sanitised note happen as they always
    /// did — the research process is told, because being told is what lets it iterate.</para>
    ///
    /// <para>And the note carries NO FIGURE. Every figure the app measured over the held-back months is
    /// read back out of the run row and asserted absent, and the remaining digits in the note are
    /// counted rather than searched for: the rule is "no figure at all", and a net that happened to be
    /// 6 would slip through a list of particular strings.</para>
    /// </summary>
    [Fact]
    public async Task A_paper_eligible_verdict_opens_no_boundary_and_still_wakes_research()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        var promotion = verdict.Promotion!;
        Assert.Equal("paper-eligible", promotion.Verdict);

        // NO BOUNDARY, AND NO DIRECTOR IS WOKEN TO REVIEW IT.
        Assert.Empty(new CouncilBoundaries(w.Db).All());
        Assert.Empty(new MissionEventStore(w.Db).OfKind(MissionEventKind.Boundary));

        // RESEARCH IS STILL WOKEN ONCE, KEYED BY THE PROMOTION, AND STILL TOLD.
        var wake = Assert.Single(new MissionEventStore(w.Db).OfKind(MissionEventKind.Verdict));
        Assert.Equal(CouncilRoles.Research, wake.For);
        Assert.Equal(MissionEventIds.ForRole(MissionEventIds.Verdict(promotion.Id), CouncilRoles.Research),
            wake.Id);

        var note = Assert.Single(new PublicationStore(w.Db).By(Referee.RunRole));
        Assert.Equal(PublicationKind.Verdict, note.Kind);
        Assert.Equal([CouncilRoles.Research], note.RecipientList);
        Assert.Contains("PAPER-ELIGIBLE", note.Content, StringComparison.Ordinal);
        Assert.Contains("not promoted", note.Content, StringComparison.Ordinal);
        Assert.Contains(promotion.Reason, note.Content, StringComparison.Ordinal);

        // AND NOT ONE FIGURE FROM THE MONTHS IT WAS NEVER SHOWN. The two hashes come out first: they
        // are the note's subject, they are hex, and any two-digit metric will occur inside one of them
        // sooner or later — a `DoesNotContain` over the raw text would be a test of luck.
        var run = new StrategyStore(w.Db).RunById(promotion.HoldoutRunId)!;
        var stripped = note.Content.Replace(promotion.Id, "", StringComparison.Ordinal)
            .Replace(promotion.VersionId, "", StringComparison.Ordinal);

        Assert.True(run.NetPnl > 0m, "the fixture program is profitable, so a leak would have shown one");
        Assert.DoesNotContain(run.NetPnl!.Value.ToString(CultureInfo.InvariantCulture), stripped,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.Bars.ToString(CultureInfo.InvariantCulture), stripped,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.Trades.ToString(CultureInfo.InvariantCulture), stripped,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.TraceSha256, note.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(promotion.HoldoutRunId, note.Content, StringComparison.Ordinal);

        // AND THE WHOLE OF IT AT ONCE: with the two hashes out, the only digits left in the note are
        // the campaign's own id. "No figure at all" rather than "not these figures" — a net that
        // happened to be 0 or 6 would slip through a list of particular strings.
        Assert.Equal(promotion.CampaignId.ToString(CultureInfo.InvariantCulture),
            new string([.. stripped.Where(char.IsDigit)]));
    }

    /// <summary>
    /// SECTION 8 OF THE OWNER'S REPORT NAMES THE PAPER-ELIGIBLE VERDICT AND PRINTS NO HOLDOUT FIGURE.
    ///
    /// <para>It needed no new code, and that is the point of the design: the section prints the verdict
    /// WORD and the reason CLASS, both of which are closed vocabularies, so a fifth verdict says itself
    /// and a reason column that cannot hold a figure still cannot hold one. The document matters
    /// because <c>trade report</c> serves it to the research process verbatim.</para>
    /// </summary>
    [Fact]
    public async Task Section_eight_names_the_paper_eligible_verdict_and_prints_no_holdout_figure()
    {
        var w = await Given(AfterTheCutoff);
        using var _1 = w.Db;

        var verdict = RefereeOf(w).Verdict(w.VersionId, w.Campaign.Id);
        Assert.Equal("paper-eligible", verdict.Promotion!.Verdict);

        var text = DailyReportText.Render(w.Gw.Reports.Compose(
            new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero)));

        Assert.Contains($"PAPER-ELIGIBLE version {w.VersionId[..12]}", text, StringComparison.Ordinal);
        Assert.Contains(PromotionReason.Words(PromotionReason.MetOnHistory), text, StringComparison.Ordinal);
        Assert.Contains("is not promoted and gets no capital", text, StringComparison.Ordinal);
        Assert.Contains("its figures are not printed in this report", text, StringComparison.Ordinal);

        var run = new StrategyStore(w.Db).RunById(verdict.Promotion.HoldoutRunId)!;
        Assert.DoesNotContain($"net {run.NetPnl?.ToString(CultureInfo.InvariantCulture)}", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(run.TraceSha256, text, StringComparison.Ordinal);
        Assert.DoesNotContain($"{run.Bars} bars", text, StringComparison.Ordinal);
    }
}
