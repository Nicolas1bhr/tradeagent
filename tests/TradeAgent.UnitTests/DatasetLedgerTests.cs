using System.Net;
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
    static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Rows for a whole month is too much for a test; three minutes is the same shape. A different
    /// <paramref name="closePrice"/> is the same month with other bytes — what a vendor that re-published it serves.
    /// </summary>
    static string Rows(DateOnly month, string closePrice = "100.50000000")
    {
        var start = new DateTimeOffset(month.Year, month.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var b = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            var open = start.AddMinutes(i).ToUnixTimeMilliseconds() * 1000L;
            var close = start.AddMinutes(i + 1).ToUnixTimeMilliseconds() * 1000L - 1;
            b.Append(open).Append(",100.00000000,101.00000000,99.00000000,").Append(closePrice).Append(",1.00000000,")
             .Append(close).Append(",1000.00000000,10,0.50000000,500.00000000,0\n");
        }
        return b.ToString();
    }

    /// <summary>One collection of twelve months, of a pair of the test's own (<see cref="TestEnv.NewPair"/>).</summary>
    static async Task<(MarketDataService Svc, DataCollection First, Database Db, FakeArchive Archive, string Pair)> Collected()
    {
        var pair = TestEnv.NewPair();
        var archive = new FakeArchive();
        foreach (var m in BinanceArchive.RecentCompleteMonths(Now)) archive.Publish(pair, m, Rows(m));

        var db = TestEnv.NewDb();
        var svc = new MarketDataService(db, new BinanceArchiveClient(archive.BaseUrl));
        return (svc, await svc.CollectAsync(pair, Now), db, archive, pair);
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
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;
        Assert.NotNull(first.Dataset);
        Assert.Equal(DatasetState.ACCEPTED, first.Dataset!.State);

        // One byte, in the raw archive file the ledger measured.
        var raw = first.Dataset.Files[0].Path;
        var bytes = File.ReadAllBytes(raw);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(raw, bytes);

        var reread = svc.Store.Checked(svc.Store.Newest(pair)!);

        Assert.Equal(DatasetState.REJECTED, reread.State);
        Assert.Contains("no longer matches the hash recorded for it", reread.RejectedReason);
        Assert.Equal(DatasetState.REJECTED, svc.Store.Newest(pair)!.State);

        // NEVER RE-NORMALISED: no new version is written from bytes nobody can identify.
        var rebuilt = svc.Rebuild(pair);
        Assert.Contains("was NOT rebuilt", rebuilt.Summary);
        Assert.Equal(DatasetState.REJECTED, rebuilt.Dataset!.State);
        Assert.Equal("v1", svc.Store.Newest(pair)!.Version);
        Assert.Single(svc.Store.All());
    }

    [Fact]
    public async Task The_same_raw_files_rebuild_to_the_same_normalised_hash()
    {
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;

        var again = svc.Rebuild(pair);

        Assert.NotNull(again.Dataset);
        Assert.Equal(first.Dataset!.NormalisedSha256, again.Dataset!.NormalisedSha256);
        Assert.NotEqual(first.Dataset.NormalisedPath, again.Dataset.NormalisedPath);
        Assert.Equal("v1", first.Dataset.Version);
        Assert.Equal("v2", again.Dataset.Version);
    }

    [Fact]
    public async Task Every_raw_file_is_recorded_with_its_url_both_hashes_and_the_unit_it_was_written_in()
    {
        var (_, first, _, archive, _) = await Collected();
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
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;

        File.AppendAllText(first.Dataset!.NormalisedPath,
            "2026-08-01T00:03:00Z,100.00000000,101.00000000,99.00000000,100.50000000,1.00000000\n");

        var reread = svc.Store.Checked(svc.Store.Newest(pair)!);

        Assert.Equal(DatasetState.REJECTED, reread.State);
        Assert.Contains("normalised dataset file no longer matches", reread.RejectedReason);
    }

    // ---- U-dataset-version-once: what a dataset records is never deleted, replaced or reused -------

    /// <summary>Whether the file at <paramref name="path"/> is still exactly <paramref name="bytes"/>.</summary>
    static bool Still(string path, byte[] bytes) => File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);

    /// <summary>Where a period whose vendor name holds other bytes is kept: the first 16 hex of its hash before the extension.</summary>
    static string HashedName(string pair, DateOnly month, byte[] bytes)
    {
        var name = BinanceArchive.FileName(pair, month);
        return Path.Combine(BinanceArchive.RawDir(pair),
            $"{Path.GetFileNameWithoutExtension(name)}.{FakeArchive.Sha256(bytes)[..16]}{Path.GetExtension(name)}");
    }

    /// <summary>
    /// (i) A PRESS THAT REACHES NO VENDOR LEAVES THE DATASET BEFORE IT EXACTLY AS IT WAS.
    ///
    /// <para>RED before this unit (seat P's P4): the fetch deleted each period's raw file at its one path
    /// before it asked the vendor anything, so one press with the vendor unreachable took all twelve files
    /// of the dataset before it, and the next read rejected that dataset for good — "the raw archive file for
    /// 2025-09 is no longer on disk". The press itself recorded nothing, so nothing said why.</para>
    /// </summary>
    [Fact]
    public async Task A_press_that_reaches_no_vendor_leaves_the_dataset_before_it_as_it_was()
    {
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;
        var set = first.Dataset!;
        var raw = set.Files.ToDictionary(f => f.Path, f => File.ReadAllBytes(f.Path));
        Assert.Equal(12, raw.Count);

        archive.AlwaysAnswer = HttpStatusCode.ServiceUnavailable;
        var offline = await svc.CollectAsync(pair, Now);
        Assert.Null(offline.Dataset);
        Assert.All(offline.Months, m => Assert.Equal(MonthOutcome.Unreachable, m.Outcome));

        var reread = svc.Store.Checked(svc.Store.ById(set.Id)!);
        Assert.True(reread.State == DatasetState.ACCEPTED, reread.RejectedReason);
        foreach (var (path, bytes) in raw)
            Assert.True(Still(path, bytes), $"{path} is no longer the file dataset {set.Id} recorded");

        var rebuilt = svc.Rebuild(pair).Dataset!;
        Assert.True(rebuilt.State == DatasetState.ACCEPTED, rebuilt.RejectedReason);
        Assert.Equal("v2", rebuilt.Version);
        Assert.Equal(set.NormalisedSha256, rebuilt.NormalisedSha256);
    }

    /// <summary>
    /// (ii) A PERIOD THE VENDOR RE-PUBLISHED WITH OTHER BYTES IS KEPT BESIDE THE FIRST, NEVER OVER IT: at the
    /// vendor's name with the first 16 hex of its SHA-256 before the extension. The eleven periods whose bytes
    /// did not change are the same files, reused.
    ///
    /// <para>RED before: the second press replaced the period's file at its one path, and the dataset recorded
    /// over the first bytes was rejected for good — "…no longer matches the hash recorded for it".</para>
    /// </summary>
    [Fact]
    public async Task A_period_republished_with_other_bytes_is_kept_beside_the_first_and_never_over_it()
    {
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;
        var set = first.Dataset!;
        var raw = set.Files.ToDictionary(f => f.Path, f => File.ReadAllBytes(f.Path));

        var month = BinanceArchive.RecentCompleteMonths(Now).Last();
        var republished = archive.Publish(pair, month, Rows(month, closePrice: "100.75000000"));
        var second = (await svc.CollectAsync(pair, Now)).Dataset!;

        var reread = svc.Store.Checked(svc.Store.ById(set.Id)!);
        Assert.True(reread.State == DatasetState.ACCEPTED, reread.RejectedReason);
        foreach (var (path, bytes) in raw)
            Assert.True(Still(path, bytes), $"{path} is no longer the file dataset {set.Id} recorded");

        var kept = Assert.Single(second.Files, f => f.Month == BinanceArchive.MonthName(month));
        Assert.Equal(HashedName(pair, month, republished), kept.Path);
        Assert.Equal(FakeArchive.Sha256(republished), kept.ComputedSha256);
        Assert.True(Still(kept.Path, republished), $"{kept.Path} does not hold the bytes the vendor re-published");
        Assert.Equal(11, second.Files.Count(f => raw.ContainsKey(f.Path)));
        Assert.NotEqual(set.NormalisedSha256, second.NormalisedSha256);
        Assert.Equal(DatasetState.ACCEPTED, svc.Store.Checked(svc.Store.ById(second.Id)!).State);
    }

    /// <summary>
    /// OTHER BYTES ON BOTH NAMES REFUSE THE PERIOD, IN A SENTENCE NAMING THE FILE, and both files stay as they
    /// are. Nothing a fetch places is ever deleted or replaced, so a name that holds something else is never
    /// a name these bytes may take.
    /// </summary>
    [Fact]
    public async Task Other_bytes_on_both_names_refuse_the_period_in_words_and_are_left_as_they_are()
    {
        var pair = TestEnv.NewPair();
        var month = BinanceArchive.RecentCompleteMonths(Now).Last();
        using var archive = new FakeArchive();
        var published = archive.Publish(pair, month, Rows(month));

        var raw = BinanceArchive.RawDir(pair);
        Directory.CreateDirectory(raw);
        var vendorName = Path.Combine(raw, BinanceArchive.FileName(pair, month));
        var hashed = HashedName(pair, month, published);
        File.WriteAllText(vendorName, "other bytes at the vendor's name");
        File.WriteAllText(hashed, "other bytes at the hashed name");

        var result = await new BinanceArchiveClient(archive.BaseUrl).FetchMonthAsync(pair, month, raw);

        Assert.NotEqual(MonthOutcome.Collected, result.Outcome);
        Assert.Equal(MonthOutcome.NameTaken, result.Outcome);
        Assert.Null(result.File);
        Assert.Contains(hashed, result.Detail, StringComparison.Ordinal);
        Assert.Equal("other bytes at the vendor's name", File.ReadAllText(vendorName));
        Assert.Equal("other bytes at the hashed name", File.ReadAllText(hashed));
    }

    /// <summary>
    /// (iii) TWO LEDGERS, ONE HOME, ONE PAIR — the shape of the windows-latest red (run 37421444199; seat P's
    /// P1) — AND NEITHER WRITES THE OTHER'S FILES. Ledger B's vendor serves other bytes for every period, so
    /// its raw files take the hashed names; its dataset file skips the <c>v1.csv</c> ledger A recorded.
    ///
    /// <para>RED before: B's press deleted and replaced A's twelve raw files and wrote its own <c>v1.csv</c>
    /// over A's — each ledger naming its own <c>v1</c> — and A's rebuild was rejected for good.</para>
    /// </summary>
    [Fact]
    public async Task Two_ledgers_collecting_one_pair_in_one_home_never_write_each_others_files()
    {
        var pair = TestEnv.NewPair();
        var months = BinanceArchive.RecentCompleteMonths(Now);

        using var vendorA = new FakeArchive();
        foreach (var m in months) vendorA.Publish(pair, m, Rows(m));
        using var dbA = TestEnv.NewDb();
        var a = new MarketDataService(dbA, new BinanceArchiveClient(vendorA.BaseUrl));
        var setA = (await a.CollectAsync(pair, Now)).Dataset!;
        var filesA = setA.Files.Select(f => f.Path).Append(setA.NormalisedPath)
                         .ToDictionary(p => p, p => File.ReadAllBytes(p));

        using var vendorB = new FakeArchive();
        foreach (var m in months) vendorB.Publish(pair, m, Rows(m, closePrice: "100.75000000"));
        using var dbB = TestEnv.NewDb();
        var b = new MarketDataService(dbB, new BinanceArchiveClient(vendorB.BaseUrl));
        var setB = (await b.CollectAsync(pair, Now)).Dataset!;

        foreach (var (path, bytes) in filesA)
            Assert.True(Still(path, bytes), $"ledger B's collection wrote ledger A's file {path}");
        Assert.Empty(setB.Files.Select(f => f.Path).Append(setB.NormalisedPath).Intersect(filesA.Keys));

        var rebuilt = a.Rebuild(pair).Dataset!;
        Assert.True(rebuilt.State == DatasetState.ACCEPTED, rebuilt.RejectedReason);
        Assert.Equal(setA.NormalisedSha256, rebuilt.NormalisedSha256);
        Assert.Equal(DatasetState.ACCEPTED, b.Store.Checked(b.Store.ById(setB.Id)!).State);
    }

    /// <summary>
    /// (iv) AN UNRECORDED <c>v2.csv</c> — what a write that died between its file and its row leaves behind — IS
    /// SKIPPED, never replaced and never deleted, and the label after it is the next one any write takes.
    ///
    /// <para>RED before: the version was the ledger's row count plus one, read before normalising, so the rebuild
    /// named itself <c>v2</c> and wrote over the file that was already there.</para>
    /// </summary>
    [Fact]
    public async Task An_unrecorded_dataset_file_is_skipped_and_never_written_over()
    {
        var (svc, first, _, archive, pair) = await Collected();
        using var _a = archive;
        var dir = Path.GetDirectoryName(first.Dataset!.NormalisedPath)!;
        var orphan = Path.Combine(dir, "v2.csv");
        const string Left = "what a write that died before its row left behind\n";
        File.WriteAllText(orphan, Left);

        var rebuilt = svc.Rebuild(pair).Dataset!;
        Assert.Equal(Left, File.ReadAllText(orphan));
        Assert.Equal("v3", rebuilt.Version);
        Assert.Equal(Path.Combine(dir, "v3.csv"), rebuilt.NormalisedPath);
        Assert.Equal(DatasetState.ACCEPTED, svc.Store.Checked(svc.Store.ById(rebuilt.Id)!).State);

        var next = (await svc.CollectAsync(pair, Now)).Dataset!;
        Assert.Equal("v4", next.Version);
        Assert.Equal(Left, File.ReadAllText(orphan));
    }

    /// <summary>
    /// THE LEDGER REFUSES A SECOND ROW UNDER A (pair, interval, version) IT HOLDS, IN WORDS, AND WRITES NOTHING:
    /// a label names one dataset, and a run, a promotion or a campaign that names it must never be able to
    /// mean two. The same label under another pair or interval is another dataset's.
    /// </summary>
    [Fact]
    public void The_ledger_refuses_a_second_row_under_a_label_it_holds_and_writes_nothing()
    {
        using var db = TestEnv.NewDb();
        var store = new DatasetStore(db);
        var row = new DatasetRecord(
            0, BinanceArchive.Source, "BTCUSDT", BinanceArchive.Interval, "v1", 12, 12, [],
            Path.Combine(TestEnv.Home, $"{Guid.NewGuid():n}.csv"), new string('a', 64), 10,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(9), 0, [], false, 0, 0, 0,
            DateTimeOffset.UnixEpoch, DatasetState.ACCEPTED, null, []);
        var id = store.Record(row);

        var refused = Assert.Throws<InvalidOperationException>(() =>
            store.Record(row with { NormalisedPath = Path.Combine(TestEnv.Home, $"{Guid.NewGuid():n}.csv") }));
        Assert.Contains("BTCUSDT 1m v1", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"dataset {id}", refused.Message, StringComparison.Ordinal);
        Assert.Equal(id, Assert.Single(store.All()).Id);

        store.Record(row with { Interval = "5m" });
        store.Record(row with { Pair = "ETHUSDT" });
        Assert.Equal(3, store.All().Count);
    }
}
