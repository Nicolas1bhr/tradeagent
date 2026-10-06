using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TAPE'S READER AND THE OP THAT SERVES IT, BELOW THE WIRE (<c>U-tape-read</c>). The wire's own claims are
/// <c>TapeOverPipeTests</c>; these are the ones a table or a type settles: the op is a read in the drain's table, the
/// reader refuses a file it was not built for, and it has no way to write.
/// </summary>
public class TapeReadTests(ITestOutputHelper log)
{
    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    /// <summary>
    /// (d) THE DRAIN TABLE NAMES <c>data-tape</c> — the backpressure guard's own premise: a handler is covered because it
    /// is in the table, and this one makes no connector call at all, so its row is zero. It is a read: not in
    /// <see cref="Ops.Mutating"/>, so it is never stopped at the role gate.
    /// </summary>
    [Fact]
    public async Task The_drain_table_names_data_tape()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        await using var server = new GatewayPipeServer(gw, IpcToken.Ensure(), "ta-tape-" + Guid.NewGuid().ToString("n")[..12]);

        var row = server.HandlerPaths.SingleOrDefault(h => h.Handler == Ops.DataTape);
        Assert.Equal(Ops.DataTape, row.Handler);
        Assert.Equal(TimeSpan.Zero, row.Path);
        log.WriteLine($"{row.Handler}: {row.Path} — {row.Why}");

        Assert.False(Ops.IsMutating(Ops.DataTape));
        Assert.DoesNotContain(Ops.DataTape, Ops.Mutating);
    }

    /// <summary>
    /// REFUSE, NEVER GUESS: a tape whose layout version is not this build's is refused when a reader is made, naming both
    /// versions, and nothing is read; so is a file that is not there.
    /// </summary>
    [Fact]
    public void The_reader_refuses_a_tape_of_another_layout_and_one_that_is_not_there()
    {
        var file = NewFile();
        using (new TapeStore(file)) { }

        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE tape_meta SET value='2' WHERE key='schema'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        var newer = Assert.Throws<InvalidOperationException>(() => new TapeReader(file));
        log.WriteLine(newer.Message);
        Assert.Contains("layout version 2", newer.Message, StringComparison.Ordinal);
        Assert.Contains($"version {TapeStore.Schema}", newer.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<InvalidOperationException>(() => new TapeReader(NewFile()));
        Assert.Contains("no market-context tape", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE READER HAS NO WAY TO WRITE: no public method that could, and a read leaves every row and attempt exactly as
    /// it found them. The connection it reads through is opened read-only — SQLite refuses a write on it — which is
    /// what makes this a property of the type rather than a promise of its callers.
    /// </summary>
    [Fact]
    public void The_reader_has_no_way_to_write_the_tape()
    {
        var verbs = typeof(TapeReader).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => new[] { "Append", "Write", "Insert", "Update", "Delete", "Set", "Add", "Remove", "Migrate" }
                .Any(v => n.StartsWith(v, StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(verbs);

        var file = NewFile();
        using var store = new TapeStore(file);
        var at = new DateTimeOffset(2026, 10, 2, 12, 0, 2, TimeSpan.Zero);
        store.Append(new TapeFetch
        {
            Source = TapeSourceCatalog.OpenInterest,
            Series = "open-interest",
            Url = "http://127.0.0.1:9/fapi/v1/openInterest?symbol=BTCUSDT",
            RequestedAt = at.AddSeconds(-1),
            ReceivedAt = at,
            HttpStatus = 200
        }, [new TapeItem("BTCUSDT", at.AddSeconds(-2), """{"openInterest":"1.000","symbol":"BTCUSDT","time":1}""")]);

        var before = Counts(file);
        var reader = new TapeReader(file);
        var window = reader.Window(BarAudience.Pipe(CouncilRoles.Research),
            new TapeQuery { Source = TapeSourceCatalog.OpenInterest, Series = "open-interest" });
        Assert.Single(window.Rows);
        Assert.Equal([TapeSourceCatalog.OpenInterest], reader.Sources().Where(s => s == TapeSourceCatalog.OpenInterest));
        Assert.Equal(before, Counts(file));
    }

    static string Counts(string file)
    {
        using var c = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM tape_fetch) || '/' || (SELECT COUNT(*) FROM tape_obs) || '/' || "
                          + "(SELECT COALESCE(MAX(id), 0) FROM tape_obs)";
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }
}
