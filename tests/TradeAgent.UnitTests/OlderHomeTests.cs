using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// AN OLDER HOME IS REFUSED, AND STARTING FRESH IS THE OWNER'S PRESS (<c>U-windows-release</c>; CLAUDE.md "No backward compatibility
/// while we build"). A home a published v0.1.x release wrote (schema 2 or 4) is refused by the open in words naming both versions and
/// left byte-for-byte as it was found — not migrated, not even switched to WAL. Start fresh moves the WHOLE home aside by one rename,
/// every file kept, and refuses to move a home the current build opens.
/// </summary>
public class OlderHomeTests
{
    /// <summary>A home as v0.1.x left it: an unmarked database stamped at <paramref name="schema"/>, with a record in it, and a workspace file.</summary>
    static string OldHome(string root, int schema)
    {
        var home = Path.Combine(root, "TradeAgent");
        var state = Path.Combine(home, "state");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(Path.Combine(home, "workspace"));
        File.WriteAllText(Path.Combine(home, "workspace", "notes.txt"), "the owner's own file");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = Path.Combine(state, "tradeagent.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
               }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);"
                              + $"INSERT INTO meta VALUES('schema_version','{schema}');"
                              + "CREATE TABLE execution_request(request_id TEXT PRIMARY KEY, intent TEXT NOT NULL);"
                              + "INSERT INTO execution_request VALUES('r-1','BUY 1 ES');";
            cmd.ExecuteNonQuery();
        }
        return home;
    }

    /// <summary>
    /// Every file and its SHA-256 — except SQLite's own <c>-wal</c>/<c>-shm</c> sidecars, which any open of a WAL database (a read-only
    /// probe included) makes and a clean close empties: the database file itself is compared byte for byte.
    /// </summary>
    static Dictionary<string, string> Fingerprint(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("-wal", StringComparison.Ordinal) && !f.EndsWith("-shm", StringComparison.Ordinal)).ToDictionary(
            f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    [Theory]
    [InlineData(4, "0.1.2")]
    [InlineData(2, "0.1.0 or 0.1.1")]
    public void A_home_a_published_release_wrote_is_refused_untouched_naming_both_versions(int schema, string release)
    {
        using var scratch = TestEnv.NewScratch("older-home");
        var home = OldHome(scratch.Dir, schema);
        var before = Fingerprint(home);

        var refused = Assert.Throws<OlderHomeException>(() => new Database(Path.Combine(home, "state", "tradeagent.db")));

        Assert.Contains($"TradeAgent {release}", refused.Message);
        Assert.Contains($"This is TradeAgent {Versions.App}", refused.Message);
        Assert.Contains($"schema {schema}", refused.Message);
        Assert.Contains("None of your records or files in that folder has been changed", refused.Message);
        Assert.Equal(schema, refused.HomeSchema);
        Assert.Equal(Path.GetFullPath(home), refused.Home);
        Assert.Equal(before, Fingerprint(home));
    }

    [Fact]
    public void The_floor_is_the_first_schema_no_release_wrote()
    {
        Assert.Equal(5, Versions.OldestHomeSchema);
        Assert.Null(OlderHome.Refused(0));                       // no stamp: a new file
        Assert.Equal(4, OlderHome.Refused(4));
        Assert.Null(OlderHome.Refused(Versions.OldestHomeSchema));
        Assert.Null(OlderHome.Refused(Versions.DatabaseSchemaVersion));
    }

    [Fact]
    public void Start_fresh_keeps_the_whole_old_home_aside_and_the_new_one_opens_empty()
    {
        using var scratch = TestEnv.NewScratch("older-home");
        var home = OldHome(scratch.Dir, 4);
        var before = Fingerprint(home);

        var aside = OlderHome.SetAside(home, new DateTimeOffset(2026, 10, 31, 10, 15, 0, TimeSpan.Zero));

        Assert.Equal(Path.GetFullPath(home) + "-from-0.1.2-20261031-101500", aside);
        Assert.Equal(before, Fingerprint(aside));                // every file, byte for byte
        Assert.Empty(Directory.GetFileSystemEntries(home));      // a new, empty home in its place

        using var db = new Database(Path.Combine(home, "state", "tradeagent.db"));
        Assert.True(db.CreatedHere);
    }

    [Fact]
    public void Start_fresh_never_moves_a_home_this_build_opens()
    {
        using var scratch = TestEnv.NewScratch("older-home");
        var home = Path.Combine(scratch.Dir, "TradeAgent");
        using (new Database(Path.Combine(home, "state", "tradeagent.db"))) { }
        var before = Fingerprint(home);

        var refused = Assert.Throws<InvalidOperationException>(() => OlderHome.SetAside(home, DateTimeOffset.UtcNow));

        Assert.Contains("is not a home from an older TradeAgent", refused.Message);
        Assert.Equal(before, Fingerprint(home));
        Assert.Empty(Directory.GetDirectories(scratch.Dir, "TradeAgent-from-*"));
    }

    /// <summary>
    /// The app's start reads the refusal BEFORE its lock file and managed folders are made (Codex review): the probe answers an older
    /// home with both versions and writes nothing to it, and answers nothing for a home the build opens.
    /// </summary>
    [Fact]
    public void The_start_reads_the_refusal_before_it_writes_anything_into_the_home()
    {
        using var scratch = TestEnv.NewScratch("older-home");
        var home = OldHome(scratch.Dir, 4);
        var before = Fingerprint(home);
        var dirs = Directory.GetDirectories(home, "*", SearchOption.AllDirectories).Length;

        var refused = TradeAgent.App.AppHost.ProbeOlderHome(home);

        Assert.NotNull(refused);
        Assert.Contains("TradeAgent 0.1.2", refused!.Message);
        Assert.Equal(before, Fingerprint(home));
        Assert.Equal(dirs, Directory.GetDirectories(home, "*", SearchOption.AllDirectories).Length);

        var current = Path.Combine(scratch.Dir, "current");
        using (new Database(Path.Combine(current, "state", "tradeagent.db"))) { }
        Assert.Null(TradeAgent.App.AppHost.ProbeOlderHome(current));
    }
}
