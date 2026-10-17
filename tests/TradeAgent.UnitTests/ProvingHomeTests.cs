using Microsoft.Data.Sqlite;
using TradeAgent.Core;
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
}
