using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Core.Oversight;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE OWNER'S PRIVATE LINE, LAYER 1 (<c>U-org-watcher-a</c>; ORGANISATION § 9). Its own file and ladder, eight code detectors scoped per
/// unit, a code-composed digest whose every sentence cites, and an app sweep that reads the organisation's database read-only.
/// </summary>
public partial class OversightTests(ITestOutputHelper log)
{
    static string NewFile() => Path.Combine(TestEnv.Home, $"oversight-{Guid.NewGuid():n}.db");

    static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- (h) the file and its ladder ----------------------------------------------------------------------------------

    /// <summary>
    /// (h) A FILE WRITTEN BY A NEWER BUILD IS REFUSED, BOTH VERSIONS NAMED, AND LEFT BYTE-FOR-BYTE AS FOUND. RED at base: no
    /// <c>OversightStore</c> existed — the line had no file.
    /// </summary>
    [Fact]
    public void A_newer_oversight_file_is_refused_with_both_versions_named_and_left_untouched()
    {
        var file = NewFile();
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE oversight_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO oversight_meta VALUES('schema', '7');
                CREATE TABLE oversight_finding(x TEXT);
                """;
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(file);

        var refused = Assert.Throws<InvalidOperationException>(() => new OversightStore(file, Now));
        log.WriteLine(refused.Message);
        Assert.Contains("version 7", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"understands {OversightStore.Schema}", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing has been read from it or written to it", refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.False(File.Exists(file + "-wal") && new FileInfo(file + "-wal").Length > 0, "a refused file grew a write-ahead log");
    }

    /// <summary>
    /// THE FILE IS ITS OWN: schema 1 in its own meta table, the root scope seeded as the owner's line, every table append-only by trigger, and
    /// the path the class's constant under <c>state/</c> — not one of <see cref="Paths"/>.
    /// </summary>
    [Fact]
    public void Its_own_file_is_its_own_ladder_and_append_only()
    {
        Assert.Equal(Path.Combine(Paths.State, "oversight.db"), OversightStore.DefaultFile);
        Assert.DoesNotContain(typeof(Paths).GetProperties(), p => p.GetValue(null) is string s && s.EndsWith("oversight.db", StringComparison.Ordinal));

        var file = NewFile();
        using (var store = new OversightStore(file, Now))
        {
            var root = Assert.Single(store.Scopes());
            Assert.Equal((OversightStore.RootScope, OrgIds.Root), (root.Id, root.UnitId));
            var f = Finding("spend-no-accept", subject: "div-research");
            Assert.Single(store.Commit([f], new Dictionary<string, long> { ["tool_call"] = 5 }, Now));
            store.Commit([], new Dictionary<string, long> { ["tool_call"] = 9 }, Now);
            Assert.Equal(9, store.Cursor("tool_call"));
            store.RecordFailure(Now, "the database could not be read");
        }
        // REOPENED: nothing climbs twice, nothing is lost.
        using (var again = new OversightStore(file, Now.AddDays(1)))
        {
            Assert.Single(again.Findings());
            Assert.Equal(9, again.Cursor("tool_call"));
            Assert.Single(again.Failures());
        }

        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        Assert.Equal("1", Scalar(c, "SELECT value FROM oversight_meta WHERE key='schema'"));
        foreach (var (table, edit) in new[]
                 {
                     ("oversight_scope", "UPDATE oversight_scope SET unit_id='x'"), ("oversight_scope", "DELETE FROM oversight_scope"),
                     ("oversight_finding", "UPDATE oversight_finding SET blind='x'"), ("oversight_finding", "DELETE FROM oversight_finding"),
                     ("oversight_cursor", "UPDATE oversight_cursor SET last_id=0"), ("oversight_cursor", "DELETE FROM oversight_cursor"),
                     ("oversight_failure", "UPDATE oversight_failure SET what='x'"), ("oversight_failure", "DELETE FROM oversight_failure"),
                 })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = edit;
            var refused = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            log.WriteLine($"{table}: {refused.Message}");
        }
    }

    /// <summary>A cite outside the closed list, or a finding whose id is not its parts' hash, is refused at the write.</summary>
    [Fact]
    public void A_finding_cites_only_the_closed_list_and_carries_its_own_id()
    {
        using var store = new OversightStore(NewFile(), Now);
        var odd = Finding("refusals", subject: "x") with { Cites = [new OversightCite("publication", "p-1")] };
        Assert.Throws<ArgumentException>(() => store.Commit([odd], new Dictionary<string, long>(), Now));
        var forged = Finding("refusals", subject: "x") with { Id = "wf-forged" };
        Assert.Throws<ArgumentException>(() => store.Commit([forged], new Dictionary<string, long>(), Now));
        Assert.Empty(store.Findings());
    }

    static OversightFinding Finding(string detector, string subject, string scope = OversightStore.RootScope, string window = "w") => new(
        OversightFinding.IdOf(detector, scope, subject, window), detector, OversightSeverity.Info, scope, OrgIds.Research,
        "org/div-research", subject, window, [new OversightCite("attempt", "a-1")], new Dictionary<string, string> { ["spent"] = "1" },
        "blind", Now);

    static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
