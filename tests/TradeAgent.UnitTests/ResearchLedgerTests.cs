using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using Xunit;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE RESEARCH LEDGER'S STORE AND ITS RUNG — <c>U-research-ledger</c>, item 1.
///
/// <para>Each role's hypotheses, experiments, findings, kills and lessons are its own versioned claims in the app's
/// database: an entry and its revisions are written by the author under the role its launch grant proved, and the only
/// thing that joins a claim to what the app measured is <c>ledger_link</c>, which the app writes. These tests hold the
/// store to that: an entry is revised only by its author and never rewritten, the ledger holds references and never
/// copies, and the rung arrives — and arrives again after a crash before its stamp.</para>
///
/// <para><b>The rung's assertions are raw SQL on purpose</b>, for the reason <c>OrgLedgerTests</c> gives: they are what
/// a database holds after an upgrade, whatever reads it later.</para>
/// </summary>
public class ResearchLedgerTests
{
    /// <summary>
    /// THE LEDGER'S RUNG. Renumbered with the rung if <c>main</c> gains this number first: the ladder stays contiguous,
    /// and the tests below write the rung BEFORE it into <c>meta</c>.
    /// </summary>
    const int LedgerRung = 31;

    static readonly DateTimeOffset At = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

    // ---- (f) an entry is revised only by its author and never rewritten -------------------------------------------

    /// <summary>
    /// (f) AN ENTRY IS REVISED ONLY BY ITS AUTHOR AND NEVER REWRITTEN. The Research Director's entry is refused to the
    /// Operations Director in words naming the way to disagree, with nothing written; its own three revisions are 1, 2
    /// and 3, and the first two read back byte for byte after the third; no entry is written under an app principal or
    /// under nobody; the store's public writes are exactly <c>Add</c> and <c>Revise</c>; and no file in <c>src</c>
    /// updates, deletes, replaces, alters or drops a <c>ledger_</c> table.
    /// </summary>
    [Fact]
    public void An_entry_is_revised_only_by_its_author_and_never_rewritten()
    {
        using var db = TestEnv.NewDb();
        var clock = At;
        var ledger = new ResearchLedger(db, () => clock);

        var entry = ledger.Add(CouncilRoles.Research, "attempt-r1", LedgerKind.Hypothesis,
            "minute momentum survives fees on BTCUSDT", LedgerMark.Hypothesis, 0.4m, null, "my own reading of the run");
        Assert.Equal(CouncilRoles.Research, entry.Author);
        Assert.Equal("attempt-r1", entry.Attempt);
        var first = Assert.Single(ledger.Revisions(entry.Id));
        Assert.Equal((1, LedgerStatus.Open, (string?)null), (first.Revision, first.Status, first.Why));

        // THE OTHER ROLE IS REFUSED, AND NOTHING IS WRITTEN.
        var other = Assert.Throws<TradeAgentException>(() => ledger.Revise(CouncilRoles.Operations, "attempt-o1", entry.Id,
            "it does not", LedgerMark.Claim, "I read it differently", null, null));
        Assert.Equal(ErrorCode.INVALID_REQUEST, other.Code);
        Assert.Contains("is the Research Director's, and an entry is revised only by the role that wrote it", other.Message,
            StringComparison.Ordinal);
        Assert.Contains("add an entry of your own about it", other.Message, StringComparison.Ordinal);
        Assert.Single(ledger.Revisions(entry.Id));

        // ITS AUTHOR REVISES IT TWICE; THE FIRST TWO REVISIONS STAND AS THEY WERE WRITTEN.
        clock = At.AddMinutes(5);
        var second = ledger.Revise(CouncilRoles.Research, "attempt-r2", entry.Id, "only above the 20-bar high",
            LedgerMark.Hypothesis, "the run under it lost below the high", null, LedgerStatus.Held);
        Assert.Equal((2, LedgerStatus.Held, (decimal?)null), (second.Revision, second.Status, second.Confidence));
        var before = Rows(db, "SELECT * FROM ledger_revision WHERE entry_id=$e AND revision <= 2 ORDER BY revision",
            ("$e", entry.Id));

        clock = At.AddMinutes(9);
        var third = ledger.Revise(CouncilRoles.Research, "attempt-r3", entry.Id, "it does not survive fees",
            LedgerMark.Claim, "two more runs lost", 0.8m, LedgerStatus.Dropped);
        Assert.Equal(3, third.Revision);
        Assert.Equal(before, Rows(db, "SELECT * FROM ledger_revision WHERE entry_id=$e AND revision <= 2 ORDER BY revision",
            ("$e", entry.Id)));
        Assert.Equal([3, 2, 1], ledger.Revisions(entry.Id).Select(r => r.Revision));

        // A STATUS GATES NOTHING: a dropped entry is still revised, and an unstated status is the last one.
        var fourth = ledger.Revise(CouncilRoles.Research, "attempt-r3", entry.Id, "still dropped", LedgerMark.Claim,
            "nothing new", null, null);
        Assert.Equal((4, LedgerStatus.Dropped), (fourth.Revision, fourth.Status));

        // NO ENTRY UNDER AN APP PRINCIPAL, UNDER NOBODY, OR UNDER A NAME NO LAUNCH PROVED.
        var entries = Rows(db, "SELECT COUNT(*) FROM ledger_entry");
        foreach (var who in AppPrincipals.All.Append("").Append("owner").Append("Research"))
        {
            var refused = Assert.Throws<TradeAgentException>(() => ledger.Add(who, "attempt-x", LedgerKind.Lesson,
                "a lesson", LedgerMark.Claim, null, null, null));
            Assert.Equal(ErrorCode.INVALID_REQUEST, refused.Code);
            Assert.Contains("is not a council role", refused.Message, StringComparison.Ordinal);
        }
        Assert.Equal(entries, Rows(db, "SELECT COUNT(*) FROM ledger_entry"));
        Assert.Equal(["4"], Rows(db, "SELECT COUNT(*) FROM ledger_revision"));

        // THE PUBLIC WRITES ARE EXACTLY TWO, AND NOTHING ON EITHER CLASS EDITS OR REMOVES.
        string[] reads = ["Ask", "Entry", "Latest", "List", "Revisions", "Show"];
        Assert.Equal(["Add", "Revise"], typeof(ResearchLedger)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Except(reads).Order(StringComparer.Ordinal));
        Assert.Equal(["Link", "Of"], typeof(LedgerLinks)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Order(StringComparer.Ordinal));
        foreach (var type in new[] { typeof(ResearchLedger), typeof(LedgerLinks) })
            Assert.DoesNotContain(type.GetMethods(), m => Regex.IsMatch(m.Name, "Update|Delete|Remove|Edit|Rewrite|Replace|Unlink"));

        var root = RepoRoot();
        var rewrites = new Regex(
            @"\b(UPDATE(\s+OR\s+\w+)?|DELETE\s+FROM|REPLACE\s+INTO|INSERT\s+OR\s+REPLACE\s+INTO|DROP\s+TABLE(\s+IF\s+EXISTS)?|ALTER\s+TABLE)\s+ledger_\w+",
            RegexOptions.IgnoreCase);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => rewrites.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
    }

    // ---- (g) the ledger holds references, never copies ---------------------------------------------------------------

    /// <summary>
    /// (g) THE LEDGER HOLDS REFERENCES, NEVER COPIES. <c>ledger_link</c> is its seven columns — which entry, at which
    /// revision, which record by its kind and id, for which attempt, when — and not one field of the record it names; the
    /// entry and its revisions hold the author's words and nothing of an app record either; and no app table gained a
    /// column or a reference to the ledger, so nothing the app measured was touched to make room for a claim.
    /// </summary>
    [Fact]
    public void The_ledger_holds_references_never_copies()
    {
        using var db = TestEnv.NewDb();

        Assert.Equal(["id", "entry_id", "revision", "record_kind", "record_id", "attempt", "at"], Columns(db, "ledger_link"));
        Assert.Equal(["id", "kind", "author", "about", "source", "created_at", "attempt"], Columns(db, "ledger_entry"));
        Assert.Equal(["entry_id", "revision", "at", "attempt", "text", "mark", "confidence", "status", "why"],
            Columns(db, "ledger_revision"));

        // A LINK REFERENCES A LEDGER ROW, AND NOTHING ELSE IN THE DATABASE REFERENCES THE LEDGER.
        Assert.Equal(["ledger_entry", "ledger_revision"], ForeignTables(db, "ledger_link"));
        foreach (var table in Rows(db, "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'ledger_%' AND name NOT LIKE 'sqlite_%'"))
        {
            Assert.DoesNotContain(ForeignTables(db, table), t => t.StartsWith("ledger_", StringComparison.Ordinal));
            Assert.DoesNotContain(Columns(db, table), c => c.Contains("ledger", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var table in new[] { "strategy_run", "strategy_promotion", "strategy_version", "strategy_deployment" })
            Assert.DoesNotContain(Columns(db, table), c => c.Contains("entry", StringComparison.OrdinalIgnoreCase));
    }

    // ---- (j) the rung ------------------------------------------------------------------------------------------------

    /// <summary>
    /// (j) THE RESEARCH LEDGER ARRIVES AT ITS RUNG — on a fresh database; on one from the rung below, with the three
    /// tables absent; and again after a crash before its stamp, with one table written and the stamp one lower, keeping
    /// every row already there. It arrives with its checks on disk: a mark outside claim, assumption and hypothesis, and
    /// a confidence outside 0 to 1 or not a plain number, are refused by the database itself; an unstated one is NULL.
    /// </summary>
    [Fact]
    public void The_research_ledger_arrives_at_its_rung()
    {
        Assert.True(Versions.DatabaseSchemaVersion >= LedgerRung,
            $"the research ledger needs schema {LedgerRung} or later; this build says {Versions.DatabaseSchemaVersion}");

        var file = NewFile("rung");
        long entry;
        using (var db = new Database(file))
        {
            AssertArrived(db);
            entry = new ResearchLedger(db).Add(CouncilRoles.Operations, "attempt-o1", LedgerKind.Lesson,
                "a fill is the app's record", LedgerMark.Claim, null, null, null).Id;
            AssertChecks(db, entry);
        }

        // BACK TO THE RUNG BELOW: an installation that predates the unit.
        StampBack(file, "DROP TABLE ledger_link; DROP TABLE ledger_revision; DROP TABLE ledger_entry; ");
        using (var upgraded = new Database(file))
        {
            AssertArrived(upgraded);
            Assert.Equal(["0"], Rows(upgraded, "SELECT COUNT(*) FROM ledger_entry"));
            entry = new ResearchLedger(upgraded).Add(CouncilRoles.Research, "attempt-r1", LedgerKind.Finding,
                "the run lost after costs", LedgerMark.Claim, 0.9m, null, null).Id;
        }

        // A CRASH BEFORE THE STAMP: two tables written, the third not, the stamp one lower. The rung runs again.
        StampBack(file, "DROP TABLE ledger_link; ");
        using (var rerun = new Database(file))
        {
            AssertArrived(rerun);
            Assert.Equal([$"{entry}|finding|research"], Rows(rerun, "SELECT id, kind, author FROM ledger_entry"));
            Assert.Equal(["1|claim|0.9|open"], Rows(rerun, "SELECT revision, mark, confidence, status FROM ledger_revision"));
            AssertChecks(rerun, entry);
        }

        // AND THE WHOLE RUNG IN PLACE, THE STAMP ONE LOWER.
        StampBack(file, "");
        using var again = new Database(file);
        AssertArrived(again);
        Assert.Equal(["1"], Rows(again, "SELECT COUNT(*) FROM ledger_entry"));
    }

    static void AssertArrived(Database db)
    {
        Assert.Equal(["ledger_entry", "ledger_link", "ledger_revision"],
            Rows(db, "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'ledger_%' ORDER BY name"));
        Assert.Equal([Versions.DatabaseSchemaVersion.ToString(CultureInfo.InvariantCulture)],
            Rows(db, "SELECT value FROM meta WHERE key='schema_version'"));
    }

    /// <summary>The database's own refusals, by raw SQL past the store: the mark and the confidence.</summary>
    static void AssertChecks(Database db, long entry)
    {
        var next = 100;
        foreach (var (mark, confidence) in new (string, string?)[]
                 {
                     ("measured", null), ("observed", "0.5"), ("claim", "1.7"), ("claim", "-0.1"), ("claim", "high"),
                     ("claim", "0.5.1"), ("claim", "1e-1"), ("claim", " 0.5")
                 })
        {
            var refused = Assert.Throws<SqliteException>(() => Insert(db, entry, next++, mark, confidence));
            Assert.Contains("CHECK constraint failed", refused.Message, StringComparison.Ordinal);
        }

        foreach (var (mark, confidence) in new (string, string?)[]
                 { ("claim", null), ("assumption", "0"), ("hypothesis", "1"), ("claim", "0.3"), ("claim", "1.000") })
            Insert(db, entry, next++, mark, confidence);

        // AND THEY GO AGAIN, so the assertions above do not leave the entry with revisions the store never wrote.
        Exec(db, $"DELETE FROM ledger_revision WHERE entry_id={entry} AND revision >= 100");
    }

    static void Insert(Database db, long entry, int revision, string mark, string? confidence) => db.Write(_ =>
    {
        using var c = db.Cmd("""
            INSERT INTO ledger_revision(entry_id, revision, at, attempt, text, mark, confidence, status, why)
            VALUES($e, $r, '2026-10-10T09:00:00.0000000Z', NULL, 'text', $m, $c, 'open', 'why')
            """, ("$e", entry), ("$r", revision), ("$m", mark), ("$c", confidence));
        return c.ExecuteNonQuery();
    });

    // ---- helpers -------------------------------------------------------------------------------------------------------

    static List<string> Columns(Database db, string table) =>
        Rows(db, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid");

    static List<string> ForeignTables(Database db, string table) =>
        Rows(db, $"SELECT DISTINCT \"table\" FROM pragma_foreign_key_list('{table}') ORDER BY 1");

    static string NewFile(string what) => Path.Combine(TestEnv.Home, $"research-ledger-{what}-{Guid.NewGuid():n}.db");

    /// <summary>
    /// BACK TO THE RUNG BELOW THE LEDGER'S, by raw SQL: <paramref name="undo"/> first — the tables an installation
    /// before the rung lacks, or the ones a crash had not yet written — then the stamp one lower.
    /// </summary>
    static void StampBack(string file, string undo)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = undo + $"UPDATE meta SET value='{LedgerRung - 1}' WHERE key='schema_version';";
        c.ExecuteNonQuery();
    }

    static void Exec(Database db, string sql) => db.Write(_ =>
    {
        using var c = db.Cmd(sql);
        return c.ExecuteNonQuery();
    });

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

    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("could not find the repository root");
    }
}
