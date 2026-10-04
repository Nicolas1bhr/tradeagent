using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Strategy;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// U-data-licence — EVERY DATASET RECORDS THE TERMS ITS BARS CAME UNDER, AND NOTHING LIVE RESTS ON
/// RESEARCH-ONLY EVIDENCE.
///
/// <para><b>Why.</b> Binance's public archive is now under the Binance Vision Dataset Terms v1.0
/// (CC BY-NC-SA 4.0): § 4.1 allows backtesting for personal non-production research, § 4.2 forbids live
/// proprietary trading execution. The orchestrator's COMPLY decision of 2026-10-04 and
/// <c>docs/research/2026-10-04/R19-historical-data-licences.md</c> follow from it: archive datasets serve
/// research — backtests, paper, M0 — and no live eligibility may rest on them.</para>
///
/// <para><b>The rung's assertions are raw SQL on purpose</b>, for the reason <c>OrgLedgerTests</c> gives:
/// they are what a database holds after an upgrade, whatever reads it later, and they compile against a
/// build that has no reader at all — which is how they were watched failing before the rung existed.</para>
/// </summary>
public class DataLicenceTests
{
    /// <summary>
    /// THE LICENCE RUNG. Renumbered with the rung if <c>main</c> gains this number first: the ladder stays
    /// contiguous, and the tests below write the rung BEFORE it into <c>meta</c>.
    /// </summary>
    const int LicenceRung = 30;

    // ---- (f) the rung -------------------------------------------------------------------------------

    /// <summary>
    /// (f) THE RUNG READS EVERY ARCHIVE DATASET RESEARCH-ONLY, AND SEEDS ITS TWO READINGS ONCE.
    ///
    /// <para>A database collected on before this unit, with the table and the four columns absent and the
    /// stamp one lower, reopened by this build: the archive row takes the archive reading — the ONE
    /// backfill — and a row of any other source stays unrecorded, because nothing knowable says what its
    /// bars came under. Then the crash the rung has to survive: everything written and the stamp one lower,
    /// so the rung runs again and must neither seed a second copy nor restate a row.</para>
    ///
    /// <para><b>The forward ledger's reading is <c>unverified</c> and confers nothing</b> — on the
    /// orchestrator's order of 2026-10-04, a deviation from the brief's <c>first-party</c>: whether the
    /// archive terms' "associated endpoints" reach the forward host is R19 § 6 Q2, and it is open.</para>
    /// </summary>
    [Fact]
    public void The_rung_reads_every_archive_dataset_research_only_and_seeds_once()
    {
        var file = NewFile("rung");
        long archive, other;
        using (var db = new Database(file))
        {
            var store = new DatasetStore(db);
            archive = store.Record(Row(BinanceArchive.Source));
            other = store.Record(Row("some-other-candle-source"));
        }

        // BACK TO THE RUNG BELOW: an installation that predates the unit.
        StampBack(file, dropRung: true);
        using (var upgraded = new Database(file))
            AssertRead(upgraded, archive, other);

        // AND A CRASH BETWEEN THE SEED AND THE STAMP: everything in place, the stamp one lower.
        StampBack(file, dropRung: false);
        using (var rerun = new Database(file))
            AssertRead(rerun, archive, other);
    }

    static void AssertRead(Database db, long archive, long other)
    {
        Assert.Equal(
            [
                $"{archive}|{DataLicence.ResearchOnly}|{DataLicence.ArchiveTermsUrl}|{DataLicence.ArchiveTermsVersion}|{DataLicence.ArchiveTermsReadOn}",
                $"{other}|NULL|NULL|NULL|NULL"
            ],
            Rows(db, "SELECT id, licence_class, terms_url, terms_version, terms_read_on FROM dataset ORDER BY id"));

        Assert.Equal(
            [
                $"{BinanceArchive.Source}|{DataLicence.ResearchOnly}|{DataLicence.ArchiveTermsUrl}|{DataLicence.ArchiveTermsVersion}|{DataLicence.ArchiveTermsReadOn}|{DataLicence.ArchiveNote}",
                $"{ForwardBars.Source}|{DataLicence.Unverified}|{DataLicence.ForwardTermsUrl}|{DataLicence.ForwardTermsVersion}|{DataLicence.ForwardTermsReadOn}|{DataLicence.ForwardNote}"
            ],
            Rows(db, "SELECT source, licence_class, terms_url, terms_version, terms_read_on, note FROM data_licence ORDER BY id"));
        Assert.Empty(Rows(db, "SELECT id FROM data_licence WHERE recorded_at IS NULL OR recorded_at = ''"));

        // THE FORWARD LEDGER'S OWN ASSERTION: it reads `unverified`, and `unverified` confers nothing.
        var forward = Assert.Single(Rows(db, "SELECT licence_class FROM data_licence WHERE source=$s",
            ("$s", ForwardBars.Source)));
        Assert.Equal(DataLicence.Unverified, forward);
        Assert.False(DataLicence.Confers(forward), "the forward ledger's unverified reading conferred live eligibility");
        Assert.Contains("R19 § 6 Q2 open", DataLicence.ForwardNote, StringComparison.Ordinal);

        Assert.Equal([Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture)],
            Rows(db, "SELECT value FROM meta WHERE key='schema_version'"));
    }

    // ---- (g) the collector --------------------------------------------------------------------------

    /// <summary>
    /// (g) A COLLECTION TAKES ITS READING ONLY FROM THE BUILT-IN ORIGIN, AND A REBUILD CARRIES THE ROW'S OWN.
    ///
    /// <para>The archive HAS a reading, and a collection of the archive's id served from loopback does not take
    /// it: bars fetched from anywhere but this build's own row for that id — a <c>sources.json</c> row that
    /// replaced it, which an unconfined agent can write, or a test's listener — are not the bars the reading
    /// was taken about. Every vendor URL here is built from <see cref="BinanceArchive.BaseUrl"/> and is never
    /// fetched: the stamp is a pure function of the URLs, the source and the reading.</para>
    ///
    /// <para>A REBUILD re-derives the same bars from the same raw files, so it carries the row's own licence —
    /// not the source's newest reading, which here is narrower, and not a re-stamp from the origin.</para>
    /// </summary>
    [Fact]
    public async Task A_collection_takes_its_reading_only_from_the_built_in_origin()
    {
        using var archive = new FakeArchive();
        foreach (var m in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(Pair, m, Minutes(m));
        using var db = TestEnv.NewDb();
        var svc = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));

        var newest = new DataLicences(db).Newest(BinanceArchive.Source);
        Assert.NotNull(newest);
        Assert.Equal(DataLicence.ResearchOnly, newest.Class);

        // LOOPBACK: the archive's id, not the archive's origin — nothing stamped, on the record and on the row.
        var collected = (await svc.CollectAsync(Pair, Now)).Dataset!;
        Assert.Equal(DatasetLicence.Unrecorded, collected.Licence);
        Assert.Equal(DatasetLicence.Unrecorded, svc.Store.ById(collected.Id)!.Licence);
        Assert.False(svc.Store.ById(collected.Id)!.Licence.Confers);

        // THE BUILT-IN ORIGIN, AND ONLY IT, TAKES THE READING.
        var months = BinanceArchive.RecentCompleteMonths(Now).ToList();
        var vendor = months.Select(m => BinanceArchive.MonthUrl(BinanceArchive.BaseUrl, Pair, m)).ToList();
        Assert.Equal(new DatasetLicence(DataLicence.ResearchOnly, DataLicence.ArchiveTermsUrl,
                DataLicence.ArchiveTermsVersion, DataLicence.ArchiveTermsReadOn),
            DataLicence.Stamp(BinanceArchive.Source, vendor, newest));

        // ONE PERIOD FROM ANYWHERE ELSE and none of it is stamped; nothing fetched, nothing stamped.
        Assert.Equal(DatasetLicence.Unrecorded, DataLicence.Stamp(BinanceArchive.Source,
            [.. vendor.Skip(1), BinanceArchive.MonthUrl(archive.BaseUrl, Pair, months[0])], newest));
        Assert.Equal(DatasetLicence.Unrecorded, DataLicence.Stamp(BinanceArchive.Source, [], newest));

        // A SOURCE WITH NO BUILT-IN ROW, or a built-in row with no endpoint, takes nothing whatever it names;
        // and a reading of one source is never stamped onto another.
        Assert.Equal(DatasetLicence.Unrecorded, DataLicence.Stamp("a-row-sources-json-added", vendor,
            newest with { Source = "a-row-sources-json-added" }));
        Assert.Equal(DatasetLicence.Unrecorded, DataLicence.Stamp(CandleSourceCatalog.RevolutXCandles, vendor,
            newest with { Source = CandleSourceCatalog.RevolutXCandles }));
        Assert.Equal(DatasetLicence.Unrecorded, DataLicence.Stamp(CandleSourceCatalog.RevolutXCandles, vendor,
            newest));

        // REBUILD CARRIES THE ROW'S OWN. The row is given a conferring class by raw SQL, as a collection under a
        // conferring reading would have written it; the source's newest reading stays research-only.
        db.Write(_ =>
        {
            using var c = db.Cmd("""
                UPDATE dataset SET licence_class=$class, terms_url=$url, terms_version=$version, terms_read_on=$read
                 WHERE id=$id
                """, ("$class", DataLicence.FirstParty), ("$url", "terms of the row's own"), ("$version", "v9"),
                ("$read", "2026-10-01"), ("$id", collected.Id));
            return c.ExecuteNonQuery();
        });

        var rebuilt = svc.Rebuild(Pair).Dataset!;
        var carried = new DatasetLicence(DataLicence.FirstParty, "terms of the row's own", "v9", "2026-10-01");
        Assert.Equal("v2", rebuilt.Version);
        Assert.Equal(carried, rebuilt.Licence);
        Assert.Equal(carried, svc.Store.ById(rebuilt.Id)!.Licence);
    }

    // ---- (a) the capital ledger ---------------------------------------------------------------------

    /// <summary>
    /// (a) A VERSION PROMOTED ON RESEARCH-ONLY EVIDENCE IS REFUSED CAPITAL, IN WORDS, AND NOTHING IS WRITTEN.
    ///
    /// <para>The verdict stands — the referee said yes and nothing here re-judges it — and the capital ledger
    /// refuses anyway, after its <c>IsPromoted</c> and promotion-id checks: the bars the version was judged on
    /// came under terms that forbid live trading on them. The sentence names the dataset (id, source, pair,
    /// interval, version), its class and terms (address, version, read date), and says that backtests and
    /// paper go on — on the store's own write and on the owner's capital card, which is that write.</para>
    ///
    /// <para>RED before the gate: <c>Assert.False() Failure / capital was allocated to a version promoted on
    /// research-only evidence</c> — the row was written. The mutant this watches is that refusal removed.</para>
    /// </summary>
    [Fact]
    public async Task A_version_promoted_on_research_only_evidence_is_refused_capital_in_words()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        var archive = new DataLicences(db).Newest(BinanceArchive.Source)!;
        var j = Promote(db, BinanceArchive.Source, archive.Licence);
        var allocations = new Allocations(db);

        Assert.True(new Promotions(db).Standing(j.VersionId).IsPromoted, "the fixture's verdict does not stand");

        var refused = allocations.Record(RowFor(j));
        Assert.False(refused.Ok, "capital was allocated to a version promoted on research-only evidence");
        Assert.Null(refused.Allocation);
        Assert.Empty(allocations.For(j.VersionId));
        AssertNamesTheEvidence(refused.Why, j.Set, archive.Licence);

        // THE CAPITAL CARD IS THAT WRITE, and says the same words.
        var card = gw.Allocate(j.VersionId, 1m, null, "allocated by the account owner");
        Assert.False(card.Ok, "the capital card allocated capital on research-only evidence");
        Assert.Empty(gw.Allocations.For(j.VersionId));
        AssertNamesTheEvidence(card.Why, j.Set, archive.Licence);

        // AND THE VERDICT IS UNTOUCHED: refusing capital is not a re-judging.
        Assert.True(new Promotions(db).Standing(j.VersionId).IsPromoted);
        await gw.DisposeAsync();
    }

    // ---- (c) the newest reading ---------------------------------------------------------------------

    /// <summary>
    /// (c) A NEWER READING FOR ITS SOURCE REFUSES WHAT THE DATASET RECORDED — AND A WIDER ONE NEVER RE-OPENS.
    ///
    /// <para>Both must confer: the row's own class, which is what its bars came under, and its source's
    /// newest reading, which is what TradeAgent knows now. A reading appended later that is NARROWER stops an
    /// allocation already written from authorising anything and refuses a fresh one; one that is WIDER does
    /// not reach back and widen bars collected under narrower terms. A reading is written as a rung would
    /// write it, by raw SQL, because nothing else in this build writes one.</para>
    ///
    /// <para>RED before the gate: the narrowed allocation went on authorising. The mutant this watches is the
    /// newest reading ignored.</para>
    /// </summary>
    [Fact]
    public void A_newer_reading_for_its_source_refuses_what_the_dataset_recorded()
    {
        using var db = TestEnv.NewDb();
        var allocations = new Allocations(db);
        const string narrowed = "a-source-later-read-narrower";
        const string widened = "a-source-later-read-wider";

        // A DATASET RECORDED FIRST-PARTY under a source whose one reading says first-party: it stands.
        AppendReading(db, narrowed, DataLicence.FirstParty, "the terms as first read");
        var stood = Promote(db, narrowed, new DatasetLicence(DataLicence.FirstParty, "terms-a", "v1", "2026-10-01"));
        var allocated = allocations.Record(RowFor(stood));
        Assert.True(allocated.Ok, allocated.Why);
        Assert.True(allocations.StandingForLive(stood.VersionId, At)!.Authorises);

        // A NEWER READING NARROWS IT: the allocation stops authorising, and a fresh one is refused in words.
        AppendReading(db, narrowed, DataLicence.ResearchOnly, "the terms as read again");
        Assert.True(new Promotions(db).Standing(stood.VersionId).IsPromoted, "a reading re-judged the verdict");
        Assert.False(allocations.StandingForLive(stood.VersionId, At)!.Authorises,
            "an allocation went on authorising after its source's newest reading stopped conferring");

        var again = allocations.Record(RowFor(stood, quantity: 2m));
        Assert.False(again.Ok, "a fresh allocation was written over a narrowed source");
        Assert.Contains($"the newest reading of {narrowed}'s terms is {DataLicence.ResearchOnly}", again.Why,
            StringComparison.Ordinal);
        Assert.Single(allocations.For(stood.VersionId));

        // A WIDER NEWER READING NEVER RE-OPENS a dataset whose bars came under research-only terms.
        var narrow = Promote(db, widened,
            new DatasetLicence(DataLicence.ResearchOnly, "terms-b", "v1", "2026-10-01"), threshold: 107);
        AppendReading(db, widened, DataLicence.CommercialOk, "a written licence, read later");

        var reopened = allocations.Record(RowFor(narrow));
        Assert.False(reopened.Ok, "a wider reading re-opened bars collected under research-only terms");
        Assert.Contains(DataLicence.ResearchOnly, reopened.Why, StringComparison.Ordinal);
        Assert.Empty(allocations.For(narrow.VersionId));
    }

    // ---- (d) paper -----------------------------------------------------------------------------------

    /// <summary>
    /// (d) PAPER ALLOCATION AND DEPLOYMENT STAND ON RESEARCH-ONLY EVIDENCE — for a promoted version and for a
    /// paper-eligible one.
    ///
    /// <para>The Binance Vision Dataset Terms allow personal non-production research (§ 4.1) and paper is
    /// exactly that, so the paper arm asks nothing of a licence: the app's own policy puts the version on
    /// paper inside the owner's envelope and starts a deployment, on bars that confer no live eligibility. A
    /// paper experiment confers no live authority either (<c>docs/PRINCIPLES.md</c> § Evidence), which is why
    /// the promoted version's capital card is refused in the same breath.</para>
    ///
    /// <para>RED before the gate: the promoted version's capital card allocated capital.</para>
    /// </summary>
    [Fact]
    public async Task Paper_allocation_and_deployment_stand_on_research_only_evidence()
    {
        foreach (var verdict in new[] { PromotionVerdict.Promoted, PromotionVerdict.PaperEligible })
        {
            var (gw, conn, db) = await TestEnv.Ready(s => s.Risk.InstrumentAllowlist = [.. TestEnv.Instruments, Pair]);
            using var _1 = db;
            var archive = new DataLicences(db).Newest(BinanceArchive.Source)!;
            var j = Promote(db, BinanceArchive.Source, archive.Licence, verdict);

            var granted = await gw.GrantPaperEnvelopeAsync(Pair, 5m, null, At.AddDays(30), At);
            Assert.True(granted.Ok, granted.Why);

            Assert.Equal(1, gw.AllocatePaperDue(At));
            var paper = gw.Allocations.StandingForPaper(j.VersionId, conn.Id, conn.Broker.AccountId, At);
            Assert.NotNull(paper);
            Assert.True(paper.Authorises, $"a {verdict} version's paper allocation does not authorise");

            Assert.Equal(1, gw.StartPaperDeploymentsDue(At));
            var run = Assert.Single(gw.Deployments.ForEnvelope(granted.Envelope!.Id));
            Assert.Equal(j.VersionId, run.VersionId);
            Assert.True(run.IsActive);

            // AND NO CAPITAL: the live door stays shut on the same evidence.
            var card = gw.Allocate(j.VersionId, 1m, null, "allocated by the account owner");
            Assert.False(card.Ok, $"a {verdict} version on research-only evidence was given capital");
            Assert.DoesNotContain(gw.Allocations.For(j.VersionId), a => !a.IsPaper);
            await gw.DisposeAsync();
        }
    }

    // ---- (e) unknown is never zero ----------------------------------------------------------------------

    /// <summary>
    /// (e) NO LICENCE, OR A CLASS THIS BUILD DOES NOT KNOW, IS RESEARCH-ONLY.
    ///
    /// <para>Only <c>first-party</c> and <c>commercial-ok</c> confer. A row nothing stamped (NULL) and a word one
    /// character from a conferring one (<c>commercial_ok</c>) are refused capital in words, under sources no
    /// reading names, so the row's own class is the whole of the check — and the store keeps the unknown word
    /// as written, because the class is text and never a CHECK. The control is the same shape with
    /// <c>first-party</c>, which stands: the refusals are about the class and about nothing else.</para>
    ///
    /// <para>RED before the gate: capital was allocated over the unrecorded row. The mutant this watches is
    /// conferring on every class but <c>research-only</c>.</para>
    /// </summary>
    [Fact]
    public void No_licence_or_an_unknown_class_is_research_only()
    {
        using var db = TestEnv.NewDb();
        var allocations = new Allocations(db);

        var unrecorded = Promote(db, "a-source-nothing-has-read", DatasetLicence.Unrecorded);
        var misspelt = Promote(db, "another-source-nothing-has-read",
            new DatasetLicence("commercial_ok", "terms-c", "v1", "2026-10-01"), threshold: 107);
        Assert.Equal("commercial_ok", new DatasetStore(db).ById(misspelt.Set.Id)!.Licence.Class);

        foreach (var (j, words) in new[] { (unrecorded, "no licence recorded"), (misspelt, "commercial_ok") })
        {
            var refused = allocations.Record(RowFor(j));
            Assert.False(refused.Ok, $"capital was allocated over a dataset whose licence reads '{words}'");
            Assert.Contains(words, refused.Why, StringComparison.Ordinal);
            Assert.Contains("backtests and paper go on", refused.Why, StringComparison.Ordinal);
            Assert.Empty(allocations.For(j.VersionId));
        }

        var conferring = Promote(db, "a-third-source-nothing-has-read",
            new DatasetLicence(DataLicence.FirstParty, "terms-d", "v1", "2026-10-01"), threshold: 111);
        var control = allocations.Record(RowFor(conferring));
        Assert.True(control.Ok, control.Why);

        Assert.False(DataLicence.Confers(null));
        Assert.False(DataLicence.Confers("commercial_ok"));
        Assert.False(DataLicence.Confers(DataLicence.Unverified));
        Assert.False(DataLicence.Confers(DataLicence.ResearchOnly));
        Assert.True(DataLicence.Confers(DataLicence.FirstParty));
        Assert.True(DataLicence.Confers(DataLicence.CommercialOk));
    }

    // ---- fixtures -----------------------------------------------------------------------------------

    static readonly DateTimeOffset At = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset Cutoff = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    static string ProgramText(int threshold) =>
        $"instrument BTCUSDT\nsize fixed 1\nexit when close < 97\nentry when close > {threshold}\n";

    sealed record Judged(DatasetRecord Set, string VersionId, PromotionRow Promotion);

    /// <summary>
    /// A VERSION THAT REALLY CARRIES A VERDICT IN THIS DATABASE over a dataset of <paramref name="source"/>
    /// recorded under <paramref name="licence"/> — the dataset, the holdout, the campaign, the version, the
    /// holdout run and the promotion row. A faked id would be refused a step earlier and prove nothing.
    /// </summary>
    static Judged Promote(Database db, string source, DatasetLicence licence,
        string verdict = PromotionVerdict.Promoted, int threshold = 103)
    {
        var datasets = new DatasetStore(db);
        var file = Path.Combine(Paths.Data, $"licence-{Guid.NewGuid():n}.csv");
        Directory.CreateDirectory(Paths.Data);
        File.WriteAllText(file, KlineNormaliser.Header + "\n");

        var id = datasets.Record(new DatasetRecord(
            0, source, Pair, BinanceArchive.Interval, "v1", 12, 12, [],
            file, DatasetStore.Sha256(file)!, 1000, Cutoff.AddDays(-300), Cutoff.AddDays(60), 0, [],
            false, 0, 0, 0, At, DatasetState.ACCEPTED, null, []) { Licence = licence });
        Assert.True(datasets.SetHoldout(id, Cutoff, EvaluationClass.Research).Ok);
        var set = datasets.ById(id)!;

        var campaign = new CampaignStore(db).Open($"BTCUSDT 1m licence {threshold}", set, 10, 3, At);
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

        var paper = verdict == PromotionVerdict.PaperEligible;
        var promotion = new Promotions(db).Record(new PromotionRow(
            "", program.StrategyId, campaign.Campaign!.Id,
            CampaignPolicy.Sha256Of(paper ? CampaignPolicy.PaperV1 : CampaignPolicy.V1),
            StrategyStore.InterpreterBuild, set.Id, set.NormalisedSha256, model.Canonical,
            Referee.EvaluatorVersion, runId, verdict, paper ? PromotionReason.MetOnHistory : PromotionReason.Met, At));

        return new Judged(set, program.StrategyId, promotion);
    }

    /// <summary>The seven facts of one live allocation, as the owner's card would have declared them.</summary>
    static AllocationRow RowFor(Judged j, decimal quantity = 1m) =>
        new("", j.VersionId, j.Promotion.Id, AllocationPolicy.V1, quantity, null, "USD", At, null,
            "the owner allocated capital to this version", At);

    /// <summary>A reading of <paramref name="source"/>'s terms, appended the way a later rung would append one.</summary>
    static void AppendReading(Database db, string source, string licenceClass, string note) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO data_licence(source, licence_class, terms_url, terms_version, terms_read_on, note, recorded_at)
            VALUES($src, $class, 'terms read by this test', 'test', '2026-10-04', $note, $at)
            """, ("$src", source), ("$class", licenceClass), ("$note", note),
            ("$at", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        return c.ExecuteNonQuery();
    });

    /// <summary>The refusal names the dataset, its class and its terms, and says what goes on.</summary>
    static void AssertNamesTheEvidence(string why, DatasetRecord set, DatasetLicence licence)
    {
        Assert.Contains("REFUSED FOR LIVE", why, StringComparison.Ordinal);
        Assert.Contains($"dataset {set.Id} ({set.Source} {set.Pair} {set.Interval} {set.Version})", why,
            StringComparison.Ordinal);
        Assert.Contains(licence.Class!, why, StringComparison.Ordinal);
        Assert.Contains(licence.TermsUrl!, why, StringComparison.Ordinal);
        Assert.Contains(licence.TermsVersion!, why, StringComparison.Ordinal);
        Assert.Contains($"read {licence.TermsReadOn}", why, StringComparison.Ordinal);
        Assert.Contains("backtests and paper go on", why, StringComparison.Ordinal);
    }

    const string Pair = "BTCUSDT";

    static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Three closed minutes of one month, in the archive's own column order.</summary>
    static string Minutes(DateOnly month)
    {
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var open = start.AddMinutes(i).ToUnixTimeMilliseconds() * 1000L;
            var close = start.AddMinutes(i + 1).ToUnixTimeMilliseconds() * 1000L - 1;
            b.Append(open).Append(",100.00000000,101.00000000,99.00000000,100.50000000,1.00000000,")
             .Append(close).Append(",1000.00000000,10,0.50000000,500.00000000,0\n");
        }
        return b.ToString();
    }

    /// <summary>A dataset row with the provenance a collector writes, and nothing on disk to hash.</summary>
    static DatasetRecord Row(string source) => new(
        0, source, "BTCUSDT", "1m", "v1", 12, 12, [],
        Path.Combine(TestEnv.Home, $"{Guid.NewGuid():n}.csv"), new string('a', 64), 10,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(9), 0, [], false, 0, 0, 0,
        DateTimeOffset.UnixEpoch, DatasetState.ACCEPTED, null, []);

    static string NewFile(string what) => Path.Combine(TestEnv.Home, $"licence-{what}-{Guid.NewGuid():n}.db");

    /// <summary>
    /// BACK TO THE RUNG BELOW THE LICENCE RUNG, by raw SQL. With <paramref name="dropRung"/> the table and the
    /// four columns go too — an installation that predates the rung; without it they stay — a crash between the
    /// seed and the stamp. A rung landing above this one undoes itself here first, in landing order.
    /// </summary>
    static void StampBack(string file, bool dropRung)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = (dropRung
                            ? "ALTER TABLE dataset DROP COLUMN licence_class; "
                              + "ALTER TABLE dataset DROP COLUMN terms_url; "
                              + "ALTER TABLE dataset DROP COLUMN terms_version; "
                              + "ALTER TABLE dataset DROP COLUMN terms_read_on; "
                              + "DROP TABLE data_licence; "
                            : "")
                        + $"UPDATE meta SET value='{LicenceRung - 1}' WHERE key='schema_version';";
        c.ExecuteNonQuery();
    }

    /// <summary>Every row of a query, its columns joined by <c>|</c> and a NULL spelled <c>NULL</c>.</summary>
    static List<string> Rows(Database db, string sql, params (string, object?)[] ps) => db.Read(_ =>
    {
        using var c = db.Cmd(sql, ps);
        using var r = c.ExecuteReader();
        var rows = new List<string>();
        while (r.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, r.FieldCount).Select(i =>
                r.IsDBNull(i) ? "NULL" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))));
        return rows;
    });
}
