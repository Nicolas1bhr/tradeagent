using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
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

    // ---- fixtures -----------------------------------------------------------------------------------

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
