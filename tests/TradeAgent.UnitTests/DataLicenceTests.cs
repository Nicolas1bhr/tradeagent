using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
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

    // ---- fixtures -----------------------------------------------------------------------------------

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
