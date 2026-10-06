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

    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    static TapeFetch Attempt(string source, string series, DateTimeOffset receivedAt, string? note = null, string? url = null) => new()
    {
        Source = source,
        Series = series,
        Url = url ?? $"http://127.0.0.1:9/{source}/{series}",
        RequestedAt = receivedAt.AddMilliseconds(-300),
        ReceivedAt = receivedAt,
        HttpStatus = note is null ? 200 : 503,
        Note = note
    };

    static TapeItem Oi(DateTimeOffset sourceTime, string symbol = "BTCUSDT") => new(symbol, sourceTime,
        $$"""{"openInterest":"1.000","symbol":"{{symbol}}","time":{{sourceTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}}""");

    /// <summary>
    /// (e) THE STATUS REPORTS THE TAPE RECORDING AND ITS LAST ERROR — off the tape's own rows, at the gateway's clock.
    /// Binance's open interest delivered thirty seconds ago and OKX's last look failed; rows arrived both sides of UTC
    /// midnight; GDELT delivered a file today and then the daily cap stopped the next one. The status says the tape is
    /// recording, counts only today's rows and the last hour's failures, names what is failing, and says the cap stopped
    /// GDELT today. Switched off, a recorder is not recording whatever its rows say; a recorder whose newest delivery is
    /// older than its window is not either; and a gateway with no tape reports none rather than an invented "recording".
    /// </summary>
    [Fact]
    public async Task Status_reports_the_tape_recording_and_its_last_error()
    {
        var now = new DateTimeOffset(2026, 10, 7, 0, 20, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        var (gw, _, db) = await TestEnv.Ready(options: new GatewayOptions { Clock = clock });
        using var _1 = db;
        using var store = new TapeStore(NewFile());

        Assert.Null((await gw.StatusAsync()).Tape);   // no tape: absent, never "recording"

        // WRITTEN IN THE ORDER THEY ARRIVED, as every writer of the tape writes: a failure more than an hour ago (which the
        // hour does not count), OKX delivering, three rows before UTC midnight and two after, OKX failing, GDELT's listing
        // delivered and then a file labelled today refused for the daily cap, a row at 00:19:30, OKX failing again.
        var midnight = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", now.AddMinutes(-61), "the host answered 418 and nothing was read"));
        store.Append(Attempt(TapeSourceCatalog.OkxEeaAnnouncements, "announcements", now.AddMinutes(-50)));
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", midnight.AddSeconds(-50)),
            [Oi(midnight.AddMinutes(-2)), Oi(midnight.AddMinutes(-1)), Oi(midnight.AddMinutes(-1), "ETHUSDT")]);
        store.Append(Attempt(TapeSourceCatalog.OkxEeaAnnouncements, "announcements", now.AddMinutes(-20), "the host answered 503 and nothing was read"));
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", midnight.AddSeconds(10)),
            [Oi(midnight), Oi(midnight, "ETHUSDT")]);
        store.Append(Attempt(GdeltGkg.Source, "lastupdate", now.AddMinutes(-5), url: GdeltGkg.ListingUrl("http://127.0.0.1:9")));
        store.Append(Attempt(GdeltGkg.Source, "gkg-live", now.AddMinutes(-5),
            GdeltGkg.CapNotePrefix + "its kept rows pass the 12 bytes left under the daily cap", GdeltGkg.BatchUrl("http://127.0.0.1:9", midnight)));
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", now.AddSeconds(-30)),
            [Oi(midnight.AddMinutes(19))]);
        store.Append(Attempt(TapeSourceCatalog.OkxEeaAnnouncements, "announcements", now.AddSeconds(-10), "the host did not answer within 10 s"));

        gw.Tape = new TapeReader(store.File);
        var status = await gw.StatusAsync();
        log.WriteLine(Json.Write(status.Tape, pretty: true));

        var tape = Assert.IsType<TapeStatus>(status.Tape);
        Assert.True(tape.Recording);
        Assert.Equal(3, tape.RowsToday);              // the two after midnight and the one at 00:19:30
        Assert.Equal(3, tape.FailuresLastHour);       // OKX's two and GDELT's capped file — not the one 61 minutes ago

        Assert.True(tape.MarketContext.On);
        Assert.True(tape.MarketContext.Recording);
        Assert.Equal(now.AddSeconds(-30), tape.MarketContext.LastReceivedAt);
        Assert.Equal(2, tape.MarketContext.FailuresLastHour);
        Assert.Equal("the host did not answer within 10 s", tape.MarketContext.LastError);
        Assert.Null(tape.MarketContext.DailyCapReachedToday);

        Assert.True(tape.GdeltNews.On);
        Assert.True(tape.GdeltNews.Recording);
        Assert.Equal(now.AddMinutes(-5), tape.GdeltNews.LastReceivedAt);
        Assert.True(tape.GdeltNews.DailyCapReachedToday);
        Assert.StartsWith(GdeltGkg.CapNotePrefix, tape.GdeltNews.LastError);

        var okx = Assert.Single(tape.Sources, s => s.Source == TapeSourceCatalog.OkxEeaAnnouncements);
        Assert.Equal((now.AddMinutes(-50), 2, "the host did not answer within 10 s"), (okx.LastReceivedAt, okx.FailuresLastHour, okx.LastError));
        var oi = Assert.Single(tape.Sources, s => s.Source == TapeSourceCatalog.OpenInterest);
        Assert.Null(oi.LastError);                    // it failed once, and has delivered since: it is not failing
        Assert.Equal(0, oi.FailuresLastHour);         // and that failure was more than an hour ago

        // ON THE WIRE THE FIELDS ARE THE BRIEF'S: recording, sources, last arrival per source, failures, rows today.
        var json = Json.Write(status);
        foreach (var field in new[] { "\"tape\":", "\"recording\":", "\"rows_today\":3", "\"failures_last_hour\":3", "\"last_received_at\":",
                     "\"daily_cap_reached_today\":true", "\"market_context\":", "\"gdelt_news\":", "\"sources\":" })
            Assert.Contains(field, json, StringComparison.Ordinal);

        // SWITCHED OFF IS NOT RECORDING, whatever the rows say; and a delivery older than the window is not recording either.
        gw.Update(s => s.RecordMarketContext = false);
        var off = (await gw.StatusAsync()).Tape!;
        Assert.False(off.MarketContext.On);
        Assert.False(off.MarketContext.Recording);
        Assert.True(off.Recording);   // GDELT's recorder still is

        clock.Now = now.AddHours(2);
        var stale = (await gw.StatusAsync()).Tape!;
        Assert.False(stale.GdeltNews.Recording);
        Assert.False(stale.Recording);
        Assert.Equal(0, stale.FailuresLastHour);
    }

    /// <summary>
    /// THE DAILY REPORT SAYS WHAT THE TAPE RECORDED, IN ONE LINE: the rows that arrived that day, the requests and the
    /// failed ones, the gaps, the newest failure — and GDELT's credit, because some of the rows are GDELT's. With no tape
    /// open the line says so, and a day the tape recorded nothing reads as zero rows from zero requests, never as absent.
    /// </summary>
    [Fact]
    public async Task The_daily_report_says_in_one_line_what_the_tape_recorded()
    {
        var (gw, _, db) = await TestEnv.Ready();
        using var _1 = db;
        using var store = new TapeStore(NewFile());

        var noon = TestEnv.LocalNoon();
        Assert.Contains("market context tape: no tape open", DailyReportText.Render(gw.Reports.Compose(noon)), StringComparison.Ordinal);

        // THREE DELIVERIES OF OPEN INTEREST, THE THIRD FORTY MINUTES AFTER THE SECOND: a gap of a 60 s source.
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(-2)), [Oi(noon.AddMinutes(-3))]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(-1)), [Oi(noon.AddMinutes(-2))]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(39)), [Oi(noon.AddMinutes(38)), Oi(noon.AddMinutes(38), "ETHUSDT")]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(40), "the host answered 503 and nothing was read"));
        store.AppendArchive(
            Attempt(GdeltGkg.Source, "gkg-live", noon, url: GdeltGkg.BatchUrl("http://127.0.0.1:9", GdeltGkg.LabelAtOrBefore(noon))),
            new TapeArchiveBatch
            {
                RecordSeries = GdeltGkg.BatchSeries, RecordSubject = GdeltGkg.BatchSubject, ItemsSeries = GdeltGkg.ItemsSeries,
                Label = GdeltGkg.LabelAtOrBefore(noon), Bytes = 10, PublishedMd5 = new string('1', 32), ComputedMd5 = new string('1', 32),
                Sha256 = new string('2', 64), Rows = 5, Filter = GdeltGkg.Filter
            },
            [new TapeItem($"{GdeltGkg.LabelText(GdeltGkg.LabelAtOrBefore(noon))}-1", GdeltGkg.LabelAtOrBefore(noon), """{"GKGRECORDID":"x"}""")]);
        // AND A ROW ON ANOTHER DAY, which this day's line does not count.
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddDays(-2)), [Oi(noon.AddDays(-2))]);

        gw.Tape = new TapeReader(store.File);
        var text = DailyReportText.Render(gw.Reports.Compose(noon));
        var line = text.Split('\n').Single(l => l.Contains("market context tape", StringComparison.Ordinal));
        log.WriteLine(line);

        Assert.Contains("6 rows recorded from 5 requests, 1 of them recorded a failure", line, StringComparison.Ordinal);
        Assert.Contains("1 gaps", line, StringComparison.Ordinal);
        Assert.Contains("the longest 40 min on " + TapeSourceCatalog.OpenInterest, line, StringComparison.Ordinal);
        Assert.Contains("last failure: the host answered 503 and nothing was read", line, StringComparison.Ordinal);
        Assert.Contains("2 of the rows are GDELT's", line, StringComparison.Ordinal);
        Assert.Contains(TapeSourceCatalog.GdeltCitation, line, StringComparison.Ordinal);
        Assert.Contains("never evaluation evidence", line, StringComparison.Ordinal);
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
