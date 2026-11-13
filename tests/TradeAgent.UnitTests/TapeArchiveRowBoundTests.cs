using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// HOW LARGE A ROW OF AN ARCHIVE FILE THE TAPE HOLDS, AND THE RUNG THAT RAISED IT (<c>U-tape-gdelt</c>). On the owner's server
/// on 2026-11-12, 102 GDELT files were not read because one kept row of each passed the 65,536 bytes the tape held a row — the
/// largest 437,260 as the tape keeps it. Rung 4 raises the file's own bound on a row to 8 MiB, which the worst GKG row the
/// reader accepts fits (<c>GdeltGkgTests</c>), and changes no stored byte. What a poller writes stays at 64 KB, and so does
/// an archive file's own record.
/// </summary>
public class TapeArchiveRowBoundTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset Label = new(2026, 11, 12, 19, 0, 0, TimeSpan.Zero);

    const int Raised = 8 * 1024 * 1024;

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static TapeFetch Fetch(DateTimeOffset label) => new()
    {
        Source = GdeltGkg.Source,
        Series = "gkg-backfill",
        Url = GdeltGkg.BatchUrl(GdeltGkg.BaseUrl, label),
        RequestedAt = label.AddHours(1),
        ReceivedAt = label.AddHours(1).AddSeconds(2),
        HttpStatus = 200,
        BodySha256 = new string('5', 64)
    };

    static TapeArchiveBatch Batch(DateTimeOffset label) => new()
    {
        RecordSeries = GdeltGkg.BatchSeries,
        RecordSubject = GdeltGkg.BatchSubject,
        ItemsSeries = GdeltGkg.ItemsSeries,
        Label = label,
        Bytes = 4_126_527,
        PublishedMd5 = "2b4bc982748899a2ce73887198e0d9a0",
        ComputedMd5 = "2b4bc982748899a2ce73887198e0d9a0",
        Sha256 = new string('5', 64),
        LastModified = label.AddMinutes(-12),
        Rows = 900,
        Filter = GdeltGkg.Filter
    };

    /// <summary>A kept row whose canonical payload is exactly <paramref name="bytes"/> long.</summary>
    static TapeItem Row(DateTimeOffset label, int serial, int bytes)
    {
        var id = $"{GdeltGkg.LabelText(label)}-{serial}";
        var shell = $$"""{"GKGRECORDID":"{{id}}","V2.1DATE":"{{GdeltGkg.LabelText(label)}}","V2GCAM":""}""";
        var payload = shell.Replace("\"V2GCAM\":\"\"", $"\"V2GCAM\":\"{new string('c', bytes - shell.Length)}\"", StringComparison.Ordinal);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(TapeJson.Canonical(payload)));
        return new(id, label, payload);
    }

    static SqliteConnection Raw(string file)
    {
        var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        return c;
    }

    static string Scalar(string file, string sql)
    {
        using var c = Raw(file);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    static void Exec(string file, string sql)
    {
        using var c = Raw(file);
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row of <c>tape_obs</c>, every column, in id order: what "no stored byte changed" is checked against.</summary>
    static string Rows(string file)
    {
        using var c = Raw(file);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {TapeStore.ObsCols} FROM tape_obs ORDER BY id";
        using var r = cmd.ExecuteReader();
        var all = new StringBuilder();
        while (r.Read())
            for (var i = 0; i < r.FieldCount; i++) all.Append(r.GetValue(i)).Append('\u001f');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())));
    }

    static string FileSha(string file)
    {
        SqliteConnection.ClearAllPools();
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
    }

    /// <summary>
    /// THE TABLE AS RUNG 1 WROTE IT, its bound on a row <paramref name="bound"/>, rows and ids kept — SQLite's own way of
    /// changing a table, run by the test to stand for a tape a build before rung 4 left, then stamped at layout 3.
    /// </summary>
    internal static void AsAtLayoutThree(string file, string bound = "65536")
    {
        Exec(file, $"""
            PRAGMA foreign_keys=OFF;
            BEGIN;
            CREATE TABLE tape_obs_then(
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              source         TEXT NOT NULL,
              series         TEXT NOT NULL,
              subject        TEXT NOT NULL,
              source_time    TEXT NOT NULL,
              received_at    TEXT NOT NULL,
              fetch_id       INTEGER NOT NULL REFERENCES tape_fetch(id),
              natural_key    TEXT NOT NULL,
              revision       INTEGER NOT NULL CHECK (revision >= 1),
              payload_sha256 TEXT NOT NULL,
              payload        TEXT NOT NULL CHECK (length(CAST(payload AS BLOB)) <= {bound}),
              evidence_class TEXT NOT NULL CHECK (evidence_class IN ('O-LIVE','O-PIT','O-ARCH','O-HIND')),
              UNIQUE (source, series, natural_key, revision));
            INSERT INTO tape_obs_then SELECT * FROM tape_obs;
            DROP TABLE tape_obs;
            ALTER TABLE tape_obs_then RENAME TO tape_obs;
            CREATE INDEX ix_tape_obs_asof ON tape_obs(source, series, subject, source_time);
            CREATE INDEX ix_tape_obs_fetch ON tape_obs(fetch_id);
            UPDATE tape_meta SET value='3' WHERE key='schema';
            COMMIT;
            """);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// A TAPE LEFT AT LAYOUT 3 OPENS AT 4 WITH EVERY ROW AS IT WAS, AND THEN HOLDS A LARGE KEPT ROW. The rows, their ids and
    /// the id sequence are unchanged and SQLite's own check passes; the table's bound on a row now reads 8 MiB; and the
    /// file the owner's server refused — a kept row of 437,260 bytes — is written whole. RED at 5ca22a31: the tape opened at 3
    /// and the kept row was refused ("the tape holds at most 65536").
    /// </summary>
    [Fact]
    public void A_tape_at_layout_three_opens_at_four_with_every_row_as_it_was_and_holds_a_large_kept_row()
    {
        var file = NewFile();
        using (var store = new TapeStore(file))
            store.AppendArchive(Fetch(Label), Batch(Label), [Row(Label, 3, 2_000), Row(Label, 17, 60_000)]);
        AsAtLayoutThree(file);
        var rows = Rows(file);
        var sequence = Scalar(file, "SELECT seq FROM sqlite_sequence WHERE name='tape_obs'");
        Assert.Equal("3", Scalar(file, "SELECT value FROM tape_meta WHERE key='schema'"));

        using (var store = new TapeStore(file))
        {
            Assert.Equal(4, store.Version);
            Assert.Equal(rows, Rows(file));
            Assert.Equal(sequence, Scalar(file, "SELECT seq FROM sqlite_sequence WHERE name='tape_obs'"));
            Assert.Equal("ok", Scalar(file, "PRAGMA integrity_check"));

            var table = Scalar(file, "SELECT sql FROM sqlite_master WHERE type='table' AND name='tape_obs'");
            log.WriteLine(table);
            Assert.Contains($"length(CAST(payload AS BLOB)) <= {Raised})", table, StringComparison.Ordinal);
            Assert.DoesNotContain("65536", table, StringComparison.Ordinal);

            var next = Label.AddMinutes(15);
            var large = Row(next, 1055, 437_260);
            var appended = store.AppendArchive(Fetch(next), Batch(next), [large, Row(next, 1056, 900)]);
            Assert.Equal(3, appended.Stored);
            var held = store.ObservationsOf(appended.FetchId).Single(o => o.Subject == large.Subject);
            Assert.Equal(TapeJson.Canonical(large.Payload), held.Payload);
            Assert.Equal(437_260, Encoding.UTF8.GetByteCount(held.Payload!));
        }
    }

    /// <summary>
    /// THE BOUND IS THE FILE'S OWN, AND ONLY AN ARCHIVE FILE'S ROWS REACH IT. Written straight into the file, a row of exactly
    /// 8 MiB is held and one byte more is refused by the table itself. Through the store, an archive row one byte past 8 MiB is
    /// refused in words naming the bound; a poller's item one byte past 64 KB still is, as it always was.
    /// RED at 5ca22a31: the file refused the 8 MiB row (its bound was 65,536).
    /// </summary>
    [Fact]
    public void The_file_holds_an_archive_row_up_to_eight_MiB_and_a_poller_item_still_up_to_64_KB()
    {
        var file = NewFile();
        using var store = new TapeStore(file);
        store.AppendArchive(Fetch(Label), Batch(Label), [Row(Label, 3, 2_000)]);

        string Insert(int bytes, string subject)
        {
            try
            {
                Exec(file, $"""
                    INSERT INTO tape_obs(source, series, subject, source_time, received_at, fetch_id, natural_key, revision,
                                         payload_sha256, payload, evidence_class)
                    SELECT source, series, '{subject}', source_time, received_at, fetch_id, '{subject}|1', 1, payload_sha256,
                           '"' || replace(hex(zeroblob({(bytes - 2) / 2})), '0', 'c') || '"', evidence_class
                    FROM tape_obs WHERE series='{GdeltGkg.ItemsSeries}' LIMIT 1
                    """);
                return "held";
            }
            catch (SqliteException ex) { return ex.Message; }
        }

        Assert.Equal("held", Insert(Raised, "exactly"));
        var past = Insert(Raised + 2, "past");
        log.WriteLine("one past: " + past);
        Assert.Contains("CHECK constraint failed", past, StringComparison.Ordinal);

        var archive = Assert.Throws<ArgumentException>(() =>
            store.AppendArchive(Fetch(Label.AddMinutes(15)), Batch(Label.AddMinutes(15)), [Row(Label.AddMinutes(15), 1, Raised + 1)]));
        log.WriteLine(archive.Message);
        Assert.StartsWith($"an archive file's row is {Raised + 1} bytes and the tape holds at most {Raised}", archive.Message,
            StringComparison.Ordinal);

        var poller = Assert.Throws<ArgumentException>(() => store.Append(new TapeFetch
        {
            Source = TapeSourceCatalog.OpenInterest, Series = "oi", Url = "http://127.0.0.1:9/oi", RequestedAt = Label,
            ReceivedAt = Label, HttpStatus = 200
        }, [new TapeItem("BTCUSDT", Label, $"\"{new string('c', TapeStore.MaxPayloadBytes - 1)}\"")]));
        Assert.StartsWith($"an observation's payload is {TapeStore.MaxPayloadBytes + 1} bytes and the tape holds at most {TapeStore.MaxPayloadBytes}",
            poller.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE RUNG RERUNS AFTER A CRASH BEFORE ITS STAMP, AND REFUSES A TABLE IT DOES NOT KNOW. A tape at 4 whose stamp says 3
    /// opens at 4 with the bound written once. A tape stamped 3 whose table carries neither bound — one this build did not
    /// write — is refused in words naming the file and the table, and the file is left byte for byte as it was found.
    /// RED at 5ca22a31: the unknown table opened, and the tape stayed at 3.
    /// </summary>
    [Fact]
    public void The_rung_reruns_after_a_crash_before_its_stamp_and_refuses_a_table_it_does_not_know()
    {
        var crashed = NewFile();
        using (new TapeStore(crashed)) { }
        Exec(crashed, "UPDATE tape_meta SET value='3' WHERE key='schema'");
        SqliteConnection.ClearAllPools();
        using (var reopened = new TapeStore(crashed)) Assert.Equal(4, reopened.Version);
        var table = Scalar(crashed, "SELECT sql FROM sqlite_master WHERE type='table' AND name='tape_obs'");
        Assert.Equal(2, table.Split($"<= {Raised})").Length);

        var unknown = NewFile();
        using (var store = new TapeStore(unknown))
            store.AppendArchive(Fetch(Label), Batch(Label), [Row(Label, 3, 2_000)]);
        AsAtLayoutThree(unknown, bound: "65535");
        var before = FileSha(unknown);

        var refused = Assert.Throws<InvalidOperationException>(() => new TapeStore(unknown));
        log.WriteLine(refused.Message);
        Assert.Contains(unknown, refused.Message, StringComparison.Ordinal);
        Assert.Contains("tape_obs", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, FileSha(unknown));
        Assert.Equal("3", Scalar(unknown, "SELECT value FROM tape_meta WHERE key='schema'"));
    }
}
