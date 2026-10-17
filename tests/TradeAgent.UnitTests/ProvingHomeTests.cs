using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Proving;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE SHADOW HOME (<c>U-proving-ground-a</c>; <c>docs/ORCHESTRATION.md</c> § 2.14, § 8 SIM-001): a proving process runs
/// only in a home of its own, marked as a proving home, on a pipe of its own — and every store refuses the other kind.
/// Every home here is a scratch directory of this test process; the default home is a stand-in path, never the real one.
/// </summary>
public class ProvingHomeTests(ITestOutputHelper log)
{
    /// <summary>A home whose database carries <paramref name="word"/> under the mark's key, or none when it is null.</summary>
    static string HomeMarked(string dir, string? word)
    {
        var state = Path.Combine(dir, "state");
        Directory.CreateDirectory(state);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(state, "tradeagent.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
        }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT INTO meta VALUES('schema_version','1');"
                          + (word is null ? "" : $"INSERT INTO meta VALUES('{HomeMark.Key}','{word}');");
        cmd.ExecuteNonQuery();
        return dir;
    }

    /// <summary>
    /// (b) A PROVING PROCESS REFUSES THE DEFAULT HOME, AN UNMARKED HOME AND THE DEFAULT PIPE — and a missing home, a home
    /// marked real and a missing pipe — each in words naming the home and the pipe; a marked proving home on a pipe of its
    /// own is the one it runs in. The same check stands at <c>Paths</c>' first use, before anything is created, and a real
    /// process is not asked it.
    /// </summary>
    [Fact]
    public void A_proving_process_refuses_the_default_home_an_unmarked_home_and_the_default_pipe()
    {
        using var scratch = TestEnv.NewScratch("proving-home");
        var real = Path.Combine(scratch.Dir, "real-default");
        const string Own = "ta-proving-test-pipe";

        var proving = HomeMarked(Path.Combine(scratch.Dir, "proving"), HomeMark.Word(HomeKind.Proving));
        var unmarked = HomeMarked(Path.Combine(scratch.Dir, "unmarked"), null);
        var markedReal = HomeMarked(Path.Combine(scratch.Dir, "marked-real"), HomeMark.Word(HomeKind.Real));
        var empty = Path.Combine(scratch.Dir, "empty");
        Directory.CreateDirectory(empty);

        void Refused(string? home, string? pipe, params string[] words)
        {
            var why = ProvingGround.HomeRefusal(home, real, pipe);
            Assert.NotNull(why);
            log.WriteLine(why);
            Assert.Contains($"home {home ?? "not set"}", why, StringComparison.Ordinal);
            Assert.Contains($"pipe {pipe ?? "not set"}", why, StringComparison.Ordinal);
            Assert.Contains("It did not start.", why, StringComparison.Ordinal);
            foreach (var w in words) Assert.Contains(w, why, StringComparison.Ordinal);
        }

        Refused(null, Own, "TRADEAGENT_HOME is not set", real);
        Refused(real, Own, "is the default home");
        Refused(real.ToUpperInvariant() + Path.DirectorySeparatorChar, Own, "is the default home");
        Refused(unmarked, Own, "carries no home mark, so it is a real home's");
        Refused(markedReal, Own, "is marked 'real'");
        Refused(empty, Own, "it has no database at");
        Refused(proving, null, "TRADEAGENT_PIPE is not set", Paths.DefaultPipeName);
        Refused(proving, Paths.DefaultPipeName, "is the real gateway's");
        Refused(proving, Paths.DefaultPipeName.ToLowerInvariant(), "is the real gateway's");

        Assert.Null(ProvingGround.HomeRefusal(proving, real, Own));

        // AT PATHS' FIRST USE: a proving process is refused before anything is created; a real one is not asked.
        var notYet = Path.Combine(scratch.Dir, "not-created");
        var thrown = Assert.Throws<TradeAgentException>(() => Paths.Resolve(notYet, Own, proving: true, real));
        Assert.Equal(ErrorCode.WORKSPACE_CORRUPT, thrown.Code);
        Assert.Contains("it has no database at", thrown.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(notYet));
        Assert.Throws<TradeAgentException>(() => Paths.Resolve(null, Own, proving: true, real));
        Assert.False(Directory.Exists(real));

        Assert.Equal(proving, Paths.Resolve(proving, Own, proving: true, real));
        Assert.Equal(notYet, Paths.Resolve(notYet, null, proving: false, real));
        Assert.True(Directory.Exists(notYet));

        // AND NOTHING IN THIS ASSEMBLY IS A PROVING PROCESS: the flag is a proving host's alone.
        Assert.False(ProvingGround.ThisProcess);
        Assert.Equal(HomeKind.Real, ProvingGround.ThisKind);
    }
    static string? Scalar(string file, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() is { } o and not DBNull ? Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    static void Exec(string file, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string Hash(string file) => Sha256Hex.OfFile(file)!;

    /// <summary>
    /// (a) A REAL PROCESS REFUSES A PROVING DATABASE AND TAPE, NAMING BOTH KINDS — and the other direction: a proving open
    /// refuses a store marked real and one carrying no mark, a real home's. Each refusal names the file and both kinds,
    /// and leaves the file's bytes as they were. A new store is marked with the kind it was opened for.
    ///
    /// <para><b>The watched mutant</b> — the kind comparison in <see cref="Database"/>'s open skipped — goes red here.</para>
    /// </summary>
    [Fact]
    public void A_real_process_refuses_a_proving_database_and_tape_naming_both_kinds()
    {
        using var scratch = TestEnv.NewScratch("home-kind");
        var provingDb = Path.Combine(scratch.Dir, "proving.db");
        var provingTape = Path.Combine(scratch.Dir, "proving-tape.db");
        var realDb = Path.Combine(scratch.Dir, "real.db");
        var realTape = Path.Combine(scratch.Dir, "real-tape.db");

        using (var db = new Database(provingDb, HomeKind.Proving)) Assert.Equal(HomeKind.Proving, db.Kind);
        using (var tape = new TapeStore(provingTape, kind: HomeKind.Proving)) Assert.Equal(HomeKind.Proving, tape.Kind);
        using (var db = new Database(realDb)) Assert.Equal(HomeKind.Real, db.Kind);
        using (var tape = new TapeStore(realTape)) Assert.Equal(HomeKind.Real, tape.Kind);

        Assert.Equal("proving", Scalar(provingDb, $"SELECT value FROM meta WHERE key='{HomeMark.Key}'"));
        Assert.Equal("proving", Scalar(provingTape, $"SELECT value FROM tape_meta WHERE key='{HomeMark.Key}'"));
        Assert.Equal("real", Scalar(realDb, $"SELECT value FROM meta WHERE key='{HomeMark.Key}'"));
        Assert.Equal("real", Scalar(realTape, $"SELECT value FROM tape_meta WHERE key='{HomeMark.Key}'"));

        void Refuses<T>(string file, Func<object> open, params string[] words) where T : Exception
        {
            var before = Hash(file);
            var refused = Assert.Throws<T>(open);
            log.WriteLine(refused.Message);
            Assert.Contains(file, refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing has been read from it or written to it.", refused.Message, StringComparison.Ordinal);
            foreach (var w in words) Assert.Contains(w, refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, Hash(file));
        }

        // A REAL PROCESS — this one, opening with its own kind — REFUSES A PROVING HOME'S STORES.
        Refuses<TradeAgentException>(provingDb, () => new Database(provingDb),
            "the database", "marked as a proving home's ('proving')", "a real home's ('real')");
        Refuses<InvalidOperationException>(provingTape, () => new TapeStore(provingTape),
            "the market-context tape", "marked as a proving home's ('proving')", "a real home's ('real')");

        // A PROVING OPEN REFUSES A REAL HOME'S, MARKED OR NOT.
        Refuses<TradeAgentException>(realDb, () => new Database(realDb, HomeKind.Proving),
            "marked as a real home's ('real')", "a proving ground's, which reads only a proving home's ('proving')");
        Refuses<InvalidOperationException>(realTape, () => new TapeStore(realTape, kind: HomeKind.Proving),
            "marked as a real home's ('real')", "a proving ground's, which reads only a proving home's ('proving')");

        Exec(realDb, $"DELETE FROM meta WHERE key='{HomeMark.Key}'");
        Exec(realTape, $"DELETE FROM tape_meta WHERE key='{HomeMark.Key}'");
        Refuses<TradeAgentException>(realDb, () => new Database(realDb, HomeKind.Proving),
            "carries no home mark, so it is a real home's ('real')", "('proving')");
        Refuses<InvalidOperationException>(realTape, () => new TapeStore(realTape, kind: HomeKind.Proving),
            "carries no home mark, so it is a real home's ('real')", "('proving')");

        // A MARK THIS BUILD DOES NOT KNOW IS REFUSED BY BOTH KINDS.
        Exec(provingDb, $"UPDATE meta SET value='shadow' WHERE key='{HomeMark.Key}'");
        Refuses<TradeAgentException>(provingDb, () => new Database(provingDb), "marked 'shadow'");
        Refuses<TradeAgentException>(provingDb, () => new Database(provingDb, HomeKind.Proving), "marked 'shadow'");

        // AND EACH KIND OPENS ITS OWN.
        using (new TapeStore(provingTape, kind: HomeKind.Proving)) { }
    }

    /// <summary>
    /// (h) GUARD: A REAL HOME OPENS AND COLLECTS AS BEFORE. A new real database and tape are marked real and reopen; one
    /// stamped before this build — no mark — opens as a real home's and is not rewritten; and a reading recorded on time
    /// from a built-in row's own origin is O-LIVE in both, exactly as it was.
    /// </summary>
    [Fact]
    public void A_real_home_opens_and_collects_as_before()
    {
        using var scratch = TestEnv.NewScratch("real-home");
        var dbFile = Path.Combine(scratch.Dir, "tradeagent.db");
        var tapeFile = Path.Combine(scratch.Dir, "tape.db");
        var at = new DateTimeOffset(2026, 8, 3, 0, 10, 0, TimeSpan.Zero);

        using (var db = new Database(dbFile)) Assert.True(db.CreatedHere);
        using (var tape = new TapeStore(tapeFile)) TapeReadings.Reading(tape, at, "0.00010000");
        Assert.Equal("real", Scalar(dbFile, $"SELECT value FROM meta WHERE key='{HomeMark.Key}'"));

        Exec(dbFile, $"DELETE FROM meta WHERE key='{HomeMark.Key}'");
        Exec(tapeFile, $"DELETE FROM tape_meta WHERE key='{HomeMark.Key}'");

        using (var db = new Database(dbFile))
        {
            Assert.False(db.CreatedHere);
            Assert.Equal(HomeKind.Real, db.Kind);
        }
        Assert.Null(Scalar(dbFile, $"SELECT value FROM meta WHERE key='{HomeMark.Key}'"));
        Assert.Equal(Versions.DatabaseSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Scalar(dbFile, "SELECT value FROM meta WHERE key='schema_version'"));

        using (var tape = new TapeStore(tapeFile))
        {
            TapeReadings.Reading(tape, at.AddHours(1), "0.00020000");
            Assert.Equal([TapeClass.Live, TapeClass.Live],
                tape.Fetches().SelectMany(f => tape.ObservationsOf(f.Id)).Select(o => o.EvidenceClass).ToArray());
        }
        Assert.Null(Scalar(tapeFile, $"SELECT value FROM tape_meta WHERE key='{HomeMark.Key}'"));
    }
}

