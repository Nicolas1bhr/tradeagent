using System.Security.Cryptography;
using System.Text;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// REVIEW 2026-09-05b finding 6 and Codex F17 / F19, red first. Three ways the ledger could state a
/// measurement it did not make, all of them about the same table.
///
/// The rule they share is one sentence: <b>the ledger never states a hash for bytes it did not
/// hash</b>, and never attaches a claim to a row nobody named.
/// </summary>
public class MaterialLedgerVersionTests
{
    static (Database Db, string Root) Workspace()
    {
        var root = Path.Combine(TestEnv.Home, $"ver-{Guid.NewGuid():n}");
        Directory.CreateDirectory(Path.Combine(root, MaterialScanner.InboxDir));
        foreach (var d in MaterialScanner.TrackedAgentDirs)
            Directory.CreateDirectory(Path.Combine(root, MaterialScanner.AgentDir, d));
        return (TestEnv.NewDb(), root);
    }

    static MaterialScanner Scanner(Database db, string root) => new(db, root, _ => true);

    static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>
    /// P5b, lifted. The ledger watched the file go. Something of the same length, with the mtime
    /// restored, took its place — both of which the writer chooses. The old row used to come back
    /// carrying the old hash; now the sighting is a new version with its own measurement.
    /// </summary>
    [Fact]
    public void A_row_that_was_removed_never_comes_back_carrying_its_old_hash()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);
        var path = Path.Combine(root, MaterialScanner.InboxDir, "strategy.py");
        var when = new DateTime(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

        File.WriteAllText(path, "print('the reviewed strategy')");
        File.SetLastWriteTimeUtc(path, when);
        Scanner(db, root).Scan();
        var first = store.History("inbox/strategy.py").Single();
        Assert.NotNull(first.Sha256);

        File.Delete(path);
        Scanner(db, root).Scan();
        Assert.NotNull(store.History("inbox/strategy.py").Single().RemovedAt);

        // Same length, same mtime, different content — both are the writer's to choose.
        File.WriteAllText(path, "print('the OTHER strategy')".PadRight((int)first.SizeBytes, '#'));
        File.SetLastWriteTimeUtc(path, when);
        Scanner(db, root).Scan();

        var rows = store.History("inbox/strategy.py");
        Assert.Equal(2, rows.Count);                       // a new version, not a resurrection

        var live = store.Present().Single(m => m.RelPath == "inbox/strategy.py");
        Assert.Equal(Sha(path), live.Sha256);              // measured, not inherited
        Assert.NotEqual(first.Sha256, live.Sha256);

        var gone = rows.Single(m => m.RemovedAt is not null);
        Assert.Equal(first.Sha256, gone.Sha256);           // and the old measurement still stands
        Assert.Equal(first.Id, gone.Id);
    }

    /// <summary>
    /// The other direction: an unchanged file that is simply seen again is still one row, and the
    /// pass that sees it re-reads nothing. Versioning must not turn every scan into a new row.
    /// </summary>
    [Fact]
    public void A_file_that_never_went_away_is_still_one_row()
    {
        var (db, root) = Workspace();
        using var _ = db;
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "steady.txt"), "unchanged");

        Assert.Equal(1, Scanner(db, root).Scan().Added);
        var second = Scanner(db, root).Scan();

        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
        Assert.Equal(0, second.Hashed);
        Assert.Single(new MaterialStore(db).History("inbox/steady.txt"));
    }

    /// <summary>
    /// Codex F19. The hash is filled in after the walk — a big drop is worked through over several
    /// passes — and the file can change in that gap. The row describes a tuple; a hash written onto
    /// it has to be a hash of THAT file, so a row whose file no longer matches is left unhashed for
    /// the next walk to record properly.
    ///
    /// The gap is reproduced exactly: the sighting is recorded, the file changes, and then a pass
    /// runs whose walk sees nothing (its budget is spent, so it also stamps nothing removed) and
    /// whose only work is the hashing.
    /// </summary>
    [Fact]
    public void A_file_that_changed_since_it_was_sighted_is_not_hashed_onto_the_old_row()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);
        var path = Path.Combine(root, MaterialScanner.InboxDir, "big.bin");

        File.WriteAllText(path, "the bytes that were sighted");
        var info = new FileInfo(path);
        store.Observe("inbox/big.bin", MaterialOrigin.Inbox, info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), false, DateTimeOffset.UtcNow);
        Assert.Null(store.Present().Single().Sha256);

        // In the gap, before anything read it.
        File.WriteAllText(path, "entirely different bytes, and a different length as well");

        var pass = new MaterialScanner(db, root, _ => true) { FileLimit = 0 }.Scan();

        Assert.Equal(0, pass.Hashed);
        Assert.Equal(0, pass.Removed);                     // a spent budget invents no deletion
        Assert.Null(store.Present().Single().Sha256);      // and no measurement was invented either
    }

    /// <summary>
    /// Codex F17. The agent names a row by a hash prefix, and every note it writes is attached
    /// through that one call. <c>LIKE</c> made <c>_</c> and <c>%</c> wildcards, so a one-character
    /// prefix matched everything and <c>LIMIT 1</c> picked a row for it.
    /// </summary>
    [Fact]
    public void A_sha_prefix_that_is_not_a_hash_or_names_more_than_one_file_is_refused()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "find-me.txt"), "hello");
        Scanner(db, root).Scan();

        // "hello" — the hash the ledger's own tests have used since the table existed.
        Assert.Equal("inbox/find-me.txt", store.ByShaPrefix("2cf24dba5fb0")?.RelPath);
        Assert.Null(store.ByShaPrefix("ffffffffffff"));

        foreach (var bad in new[] { "_", "%", "____", "%2cf2", "2cf2_", "2cf24dba5fb0z", "zzzz", "", "  ", "2c" })
            Assert.Throws<TradeAgentException>(() => store.ByShaPrefix(bad));

        // The wildcards were the mechanism, and the row they used to reach is still there.
        Assert.Single(store.Present());
    }

    /// <summary>
    /// The ambiguity itself, with two real files whose hashes genuinely share four characters.
    /// Answering with one of them is the defect; the refusal says what to do instead.
    /// </summary>
    [Fact]
    public void Two_files_sharing_a_prefix_are_refused_rather_than_one_of_them_chosen()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);
        var (a, b, shared) = TwoContentsSharingAPrefix();

        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "a.txt"), a);
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "b.txt"), b);
        Scanner(db, root).Scan();

        var ex = Assert.Throws<TradeAgentException>(() => store.ByShaPrefix(shared));
        Assert.Contains("more than one file's hash", ex.Message);

        // And each of them, named in full, still resolves to itself.
        var full = store.Present().Select(m => m.Sha256!).ToList();
        Assert.Equal(2, full.Distinct().Count());
        foreach (var h in full) Assert.Equal(h, store.ByShaPrefix(h)!.Sha256);
    }

    /// <summary>
    /// Rows that share ONE hash are not ambiguous — they are the same bytes at two paths — so a
    /// prefix naming them resolves rather than refusing.
    /// </summary>
    [Fact]
    public void One_hash_at_two_paths_is_not_ambiguous()
    {
        var (db, root) = Workspace();
        using var _ = db;
        var store = new MaterialStore(db);
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "one.txt"), "hello");
        File.WriteAllText(Path.Combine(root, MaterialScanner.InboxDir, "two.txt"), "hello");
        Scanner(db, root).Scan();

        Assert.Equal(2, store.Present().Count);
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            store.ByShaPrefix("2cf24dba")?.Sha256);
    }

    /// <summary>
    /// Two short strings whose SHA-256 hashes begin with the same <see cref="MaterialStore.MinShaPrefix"/>
    /// characters, found by search so the test does not depend on a hard-coded pair. Deterministic:
    /// SHA-256 does not move, so this returns the same two strings on every run and every platform.
    /// </summary>
    static (string A, string B, string Shared) TwoContentsSharingAPrefix()
    {
        var seen = new Dictionary<string, string>();
        for (var i = 0; i < 200_000; i++)
        {
            var text = $"collide-{i}";
            var head = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant()[..MaterialStore.MinShaPrefix];
            if (seen.TryGetValue(head, out var first)) return (first, text, head);
            seen[head] = text;
        }
        throw new InvalidOperationException("no prefix collision found, which cannot happen at this width");
    }
}
