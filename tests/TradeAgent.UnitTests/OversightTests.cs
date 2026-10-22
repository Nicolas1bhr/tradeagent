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
    /// THE FILE IS ITS OWN: its schema (<see cref="OversightStore.Schema"/>) in its own meta table, the root scope seeded as the owner's line, every table append-only by trigger, and
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
        Assert.Equal(OversightStore.Schema.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Scalar(c, "SELECT value FROM oversight_meta WHERE key='schema'"));
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

    // ---- (h) rung 2: the owner's cap and his acknowledgements --------------------------------------------------------------

    /// <summary>
    /// (h) A CRASH BETWEEN RUNG 2'S STATEMENTS AND ITS STAMP REOPENS, AND A RUNG-1 FILE CLIMBS. A file the rung-1 ladder wrote (part a's
    /// layout, a finding and a cursor in it) is opened by a build whose rung 2 fails after its statements and before its stamp: the open
    /// throws, and the file is still rung 1 — no cap or acknowledgement table, its finding kept. Reopened, it climbs to rung 2 with nothing
    /// lost. RED at base: rung 2 did not exist (no ladder seam, no cap, no acknowledgement).
    /// </summary>
    [Fact]
    public void A_crash_between_rung_two_and_its_stamp_reopens()
    {
        var file = NewFile();
        using (var rung1 = new OversightStore(file, Now, climbTo: 1, beforeStamp: null))
            rung1.Commit([Finding("refusals", subject: "x")], new Dictionary<string, long> { ["tool_call"] = 3 }, Now);
        Assert.Equal("1", Meta(file));

        var crash = Assert.Throws<IOException>(() => new OversightStore(file, Now, climbTo: OversightStore.Schema,
            beforeStamp: rung => { if (rung == 2) throw new IOException("the machine lost power between rung 2 and its stamp"); }));
        log.WriteLine(crash.Message);
        Assert.Equal("1", Meta(file));
        Assert.False(HasTable(file, "oversight_cap"), "a rung that never stamped left its table behind");

        using var climbed = new OversightStore(file, Now);
        Assert.Equal("2", Meta(file));
        Assert.Single(climbed.Findings());
        Assert.Equal(3, climbed.Cursor("tool_call"));
        Assert.Null(climbed.Cap());
        Assert.Empty(climbed.Acknowledged());
    }

    /// <summary>
    /// RUNG 2 IS APPEND-ONLY: the cap is its latest row (none is zero, said as null), an acknowledgement is a row naming a finding the file
    /// holds, and neither table takes an UPDATE or a DELETE. A negative cap, or an acknowledgement of a finding the file never held, is refused.
    /// </summary>
    [Fact]
    public void The_cap_and_the_acknowledgements_are_rows_never_edits()
    {
        var file = NewFile();
        var f = Finding("refusals", subject: "y");
        using (var store = new OversightStore(file, Now))
        {
            store.Commit([f], new Dictionary<string, long>(), Now);
            Assert.Null(store.Cap());
            store.SetCap(2.5m, "USD", Now);
            store.SetCap(0m, "USD", Now.AddMinutes(1));
            Assert.Equal((0m, "USD"), (store.Cap()!.Amount, store.Cap()!.Currency));
            Assert.Throws<ArgumentException>(() => store.SetCap(-1m, "USD", Now));
            store.Acknowledge(f.Id, Now.AddMinutes(2));
            store.Acknowledge(f.Id, Now.AddMinutes(3));
            Assert.Equal(Now.AddMinutes(2), store.Acknowledged()[f.Id]);
            Assert.ThrowsAny<Exception>(() => store.Acknowledge("wf-never-written", Now));
            Assert.Single(store.Findings());
        }

        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        Assert.Equal("2", Scalar(c, "SELECT value FROM oversight_meta WHERE key='schema'"));
        Assert.Equal("2", Scalar(c, "SELECT COUNT(*) FROM oversight_cap"));
        foreach (var edit in new[]
                 {
                     "UPDATE oversight_cap SET amount='99'", "DELETE FROM oversight_cap",
                     "UPDATE oversight_ack SET at='x'", "DELETE FROM oversight_ack",
                 })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = edit;
            log.WriteLine(Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery()).Message);
        }
    }

    static string? Meta(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        return Scalar(c, "SELECT value FROM oversight_meta WHERE key='schema'");
    }

    static bool HasTable(string file, string table)
    {
        using var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        return Scalar(c, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'") == "1";
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
