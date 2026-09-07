using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Provisioning;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// ITEM 3 — provenance the AI cannot edit, and what makes it a measurement rather than a claim.
///
/// The dataset row carries the SHA-256 of every raw archive file it was built from. Red first: with
/// nothing re-reading those hashes, a raw file that had been edited on disk still read ACCEPTED and
/// its bars were still served — the ledger asserting the reproducibility of a file that had ceased
/// to exist in the form it recorded.
/// </summary>
public class DatasetLedgerTests
{
    const string Pair = "BTCUSDT";
    static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Rows for a whole month is too much for a test; three minutes is the same shape.</summary>
    static string Rows(DateOnly month)
    {
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var open = start.AddMinutes(i).ToUnixTimeMilliseconds() * 1000L;
            var close = start.AddMinutes(i + 1).ToUnixTimeMilliseconds() * 1000L - 1;
            b.Append(open).Append(",100.00000000,101.00000000,99.00000000,100.50000000,1.00000000,")
             .Append(close).Append(",1000.00000000,10,0.50000000,500.00000000,0\n");
        }
        return b.ToString();
    }

    static async Task<(BinanceDataService Svc, DataCollection First, Database Db, FakeArchive Archive)> Collected()
    {
        var archive = new FakeArchive();
        foreach (var m in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(Pair, m, Rows(m));

        var db = TestEnv.NewDb();
        var svc = new BinanceDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        return (svc, await svc.CollectAsync(Pair, Now), db, archive);
    }

    /// <summary>
    /// The migration. <c>dataset</c> and <c>dataset_file</c> arrived at schema 8 — 7 was
    /// <c>U-wakes</c>'s <c>mission_event</c> — and 8 is this class's FLOOR, because below it there
    /// are no dataset tables at all. The exact number belongs to whichever migration last moved it,
    /// which is now the council's at 9
    /// (<c>CouncilRoleTests.The_role_columns_arrive_at_schema_nine_and_an_unnamed_row_is_the_chairs</c>);
    /// pinning it here as well is the trap <c>AiAttemptLedgerTests</c> and <c>MissionEventTests</c>
    /// were both cured of, where an ADDITIVE migration that touches neither table fails the test of
    /// the table it did not touch. An older database gains both tables empty, which reads correctly
    /// as "this installation has collected no data yet".
    /// </summary>
    [Fact]
    public void The_schema_carries_the_dataset_tables_at_version_eight()
    {
        using var db = TestEnv.NewDb();

        Assert.True(Versions.DatabaseSchemaVersion >= 8,
            $"the dataset ledger needs schema 8 or later; this build says {Versions.DatabaseSchemaVersion}");
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(), db.Read(_ =>
        {
            using var c = db.Cmd("SELECT value FROM meta WHERE key='schema_version'");
            return c.ExecuteScalar() as string;
        }));
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM dataset");
            return Convert.ToInt64(c.ExecuteScalar());
        }));
        Assert.Equal(0L, db.Read(_ =>
        {
            using var c = db.Cmd("SELECT COUNT(*) FROM dataset_file");
            return Convert.ToInt64(c.ExecuteScalar());
        }));
    }

    [Fact]
    public async Task An_altered_raw_file_makes_the_dataset_rejected_and_it_is_never_renormalised()
    {
        var (svc, first, _, archive) = await Collected();
        using var _a = archive;
        Assert.NotNull(first.Dataset);
        Assert.Equal(DatasetState.ACCEPTED, first.Dataset!.State);

        // One byte, in the raw archive file the ledger measured.
        var raw = first.Dataset.Files[0].Path;
        var bytes = File.ReadAllBytes(raw);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(raw, bytes);

        var reread = svc.Store.Checked(svc.Store.Newest(Pair)!);

        Assert.Equal(DatasetState.REJECTED, reread.State);
        Assert.Contains("no longer matches the hash recorded for it", reread.RejectedReason);
        Assert.Equal(DatasetState.REJECTED, svc.Store.Newest(Pair)!.State);

        // NEVER RE-NORMALISED: no new version is written from bytes nobody can identify.
        var rebuilt = svc.Rebuild(Pair);
        Assert.Contains("was NOT rebuilt", rebuilt.Summary);
        Assert.Equal(DatasetState.REJECTED, rebuilt.Dataset!.State);
        Assert.Equal("v1", svc.Store.Newest(Pair)!.Version);
        Assert.Single(svc.Store.All());
    }

    [Fact]
    public async Task The_same_raw_files_rebuild_to_the_same_normalised_hash()
    {
        var (svc, first, _, archive) = await Collected();
        using var _a = archive;

        var again = svc.Rebuild(Pair);

        Assert.NotNull(again.Dataset);
        Assert.Equal(first.Dataset!.NormalisedSha256, again.Dataset!.NormalisedSha256);
        Assert.NotEqual(first.Dataset.NormalisedPath, again.Dataset.NormalisedPath);
        Assert.Equal("v1", first.Dataset.Version);
        Assert.Equal("v2", again.Dataset.Version);
    }

    [Fact]
    public async Task Every_raw_file_is_recorded_with_its_url_both_hashes_and_the_unit_it_was_written_in()
    {
        var (_, first, _, archive) = await Collected();
        using var _a = archive;

        var set = first.Dataset!;
        Assert.Equal(BinanceArchive.Source, set.Source);
        Assert.Equal(12, set.MonthsAttempted);
        Assert.Equal(12, set.MonthsPresent);
        Assert.Equal(12, set.Files.Count);
        Assert.All(set.Files, f =>
        {
            Assert.StartsWith("http://127.0.0.1:", f.Url);
            Assert.Equal(64, f.PublishedSha256.Length);
            Assert.Equal(f.PublishedSha256, f.ComputedSha256);
            Assert.True(f.Bytes > 0);
            Assert.Equal(KlineTimeUnit.Microseconds, f.Unit);
        });
        Assert.Equal(36, set.Bars);
    }

    [Fact]
    public async Task A_dataset_whose_normalised_file_was_edited_is_rejected_too()
    {
        var (svc, first, _, archive) = await Collected();
        using var _a = archive;

        File.AppendAllText(first.Dataset!.NormalisedPath,
            "2026-08-01T00:03:00Z,100.00000000,101.00000000,99.00000000,100.50000000,1.00000000\n");

        var reread = svc.Store.Checked(svc.Store.Newest(Pair)!);

        Assert.Equal(DatasetState.REJECTED, reread.State);
        Assert.Contains("normalised dataset file no longer matches", reread.RejectedReason);
    }
}
