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

        // AND AN AGENT DISCOVERS IT, as a read with its bounds, and finds the tape on the status it reads.
        var spec = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.DataTape);
        Assert.False(spec.Mutating);
        Assert.StartsWith("trade data tape --source S", spec.Cli, StringComparison.Ordinal);
        Assert.Contains(spec.Args, a => a.Name == "source" && a.Required);
        Assert.All(spec.Args.Where(a => a.Name != "source"), a => Assert.False(a.Required));
        Assert.Equal(["as_of", "before", "from", "limit", "series", "source", "subject", "to"],
            spec.Args.Select(a => a.Name).Order(StringComparer.Ordinal));
        Assert.Contains("5000", spec.Description, StringComparison.Ordinal);
        Assert.Contains("O-LIVE rows only are first-hand", spec.Description, StringComparison.Ordinal);
        Assert.Contains("citation", spec.Description, StringComparison.Ordinal);
        var status = Assert.Single(GatewaySchema.Ops(), o => o.Op == Ops.Status).Description;
        Assert.Contains("tape says whether the market-context tape is recording", status, StringComparison.Ordinal);
        Assert.Contains("daily_cap_reached_today", status, StringComparison.Ordinal);
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

        var next = (TapeStore.Schema + 1).ToString(CultureInfo.InvariantCulture);
        using (var c = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"UPDATE tape_meta SET value='{next}' WHERE key='schema'";
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }

        var newer = Assert.Throws<InvalidOperationException>(() => new TapeReader(file));
        log.WriteLine(newer.Message);
        Assert.Contains($"layout version {next}", newer.Message, StringComparison.Ordinal);
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
        using var db = TestEnv.NewDb();
        var window = reader.Window(TapeHoldout.Pipe(CouncilRoles.Research, new DatasetStore(db)),
            new TapeQuery { Source = TapeSourceCatalog.OpenInterest, Series = "open-interest" });
        Assert.Single(window.Rows);
        Assert.Equal([TapeSourceCatalog.OpenInterest], reader.Sources().Where(s => s == TapeSourceCatalog.OpenInterest));
        Assert.Equal(before, Counts(file));
    }

    /// <summary>
    /// A clock a test sets. <see cref="TimeProvider.GetUtcNow"/> answers in UTC whatever offset the instant was given in:
    /// <see cref="TimeProvider.GetLocalNow"/> adds the local offset to the ticks it is handed, so a local instant handed
    /// back as it is would move the gateway's "now" by that offset.
    /// </summary>
    sealed class TestClock(DateTimeOffset at) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = at;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
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
    /// (g) THE DAILY REPORT SAYS WHAT THE TAPE RECORDED, IN ONE LINE: the rows that arrived that day, the requests and the
    /// failed ones, every source's share of the day with its gaps, the longest named with its bounds, the newest failure —
    /// and GDELT's credit, because some of the rows are GDELT's. With no tape open the line says so.
    ///
    /// <para>REWRITTEN TO <c>U-tape-gaps</c>' RULE: the open-interest row two days earlier, which the line used to leave out,
    /// now opens the day's first gap — a source's gap runs from its last delivery before to its first after, across
    /// midnights — and the day is still open at the report's instant, so its tails end there. Written in the order the
    /// rows arrived, as every writer of the tape writes them.</para>
    /// </summary>
    [Fact]
    public async Task The_daily_report_says_in_one_line_what_the_tape_recorded()
    {
        var noon = TestEnv.LocalNoon();
        var (from, to) = DailyReports.LocalDay(noon);
        var instant = noon.AddMinutes(50);
        using var store = new TapeStore(NewFile());

        // A ROW TWO DAYS EARLIER, THEN THREE DELIVERIES OF OPEN INTEREST, THE THIRD FORTY MINUTES AFTER THE SECOND, AND A
        // FAILURE: three gaps of a 60 s source — from two days ago to 11:58, 11:59 to 12:39, and 12:39 to the report's instant.
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddDays(-2)), [Oi(noon.AddDays(-2))]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(-2)), [Oi(noon.AddMinutes(-3))]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(-1)), [Oi(noon.AddMinutes(-2))]);
        store.AppendArchive(
            Attempt(GdeltGkg.Source, "gkg-live", noon, url: GdeltGkg.BatchUrl("http://127.0.0.1:9", GdeltGkg.LabelAtOrBefore(noon))),
            new TapeArchiveBatch
            {
                RecordSeries = GdeltGkg.BatchSeries, RecordSubject = GdeltGkg.BatchSubject, ItemsSeries = GdeltGkg.ItemsSeries,
                Label = GdeltGkg.LabelAtOrBefore(noon), Bytes = 10, PublishedMd5 = new string('1', 32), ComputedMd5 = new string('1', 32),
                Sha256 = new string('2', 64), Rows = 5, Filter = GdeltGkg.Filter
            },
            [new TapeItem($"{GdeltGkg.LabelText(GdeltGkg.LabelAtOrBefore(noon))}-1", GdeltGkg.LabelAtOrBefore(noon), """{"GKGRECORDID":"x"}""")]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(39)), [Oi(noon.AddMinutes(38)), Oi(noon.AddMinutes(38), "ETHUSDT")]);
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", noon.AddMinutes(40), "the host answered 503 and nothing was read"));

        // THE TAPE'S OWN READING: the day's counts, open interest's three gaps with what is inside each, GDELT's recording
        // beginning at noon with nothing before it a gap, and the longest gap of the day.
        var day = new TapeReader(store.File).Day(from, to, instant);
        Assert.Equal((6L, 5, 1, "the host answered 503 and nothing was read", 2L), (day.Rows, day.Requests, day.Failed, day.LastError, day.GdeltRows));
        Assert.Equal((instant, true, 4), (day.Until, day.Open, day.Gaps));
        var oi = day.Sources.Single(s => s.Source == TapeSourceCatalog.OpenInterest);
        Assert.Equal(
            [(noon.AddDays(-2), noon.AddMinutes(-2), from, 0), (noon.AddMinutes(-1), noon.AddMinutes(39), noon.AddMinutes(-1), 0),
             (noon.AddMinutes(39), (DateTimeOffset?)null, noon.AddMinutes(39), 1)],
            oi.Gaps.Select(g => (g.From, g.To, g.ClippedFrom, g.Attempts)));
        Assert.Equal(TimeSpan.FromMinutes(1), oi.Recorded);
        var gdelt = day.Sources.Single(s => s.Source == GdeltGkg.Source);
        Assert.Equal(noon, gdelt.Began);
        Assert.Equal((noon, null, noon, instant, 0), Shape(Assert.Single(gdelt.Gaps)));
        Assert.Equal((TapeSourceCatalog.OpenInterest, from), day.Longest is { } longest ? (longest.Source, longest.Gap.ClippedFrom) : default);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, _, db) = await TestEnv.Ready(options: new GatewayOptions { Clock = new TestClock(instant) });
        using var _1 = db;
        Assert.Contains("market context tape: no tape open", DailyReportText.Render(gw.Reports.Compose(noon)), StringComparison.Ordinal);
        gw.Tape = new TapeReader(store.File);
        var line = TapeLineOf(gw, noon);

        Assert.Contains("6 rows recorded from 5 requests, 1 of them recorded a failure; 4 gaps", line, StringComparison.Ordinal);
        Assert.Contains($"the longest on {TapeSourceCatalog.OpenInterest}: 00:00–11:58 (", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 1 min of {Span(instant - from)} so far, 3 gaps: "
            + $"00:00–11:58 ({Span(noon.AddMinutes(-2) - from)}, since {Stamp(noon.AddDays(-2))}; nothing asked), "
            + "11:59–12:39 (40 min; nothing asked), "
            + "12:39–12:50 (11 min, still open; asked once, which failed: the host answered 503 and nothing was read)",
            line, StringComparison.Ordinal);
        Assert.Contains(
            $"{GdeltGkg.Source} recorded 0 min of {Span(instant - from)} so far, its recording began at 12:00, 1 gap: "
            + "12:00–12:50 (50 min, still open; nothing asked)", line, StringComparison.Ordinal);
        Assert.Contains($"{TapeSourceCatalog.Funding} recorded nothing — its recording has not begun, nothing asked", line, StringComparison.Ordinal);
        Assert.Contains("\"nothing asked\" means the tape holds no attempt in that stretch — TradeAgent was not running or the switch was off, "
                        + "and the tape cannot tell which", line, StringComparison.Ordinal);
        Assert.Contains("last failure: the host answered 503 and nothing was read", line, StringComparison.Ordinal);
        Assert.Contains("2 of the rows are GDELT's", line, StringComparison.Ordinal);
        Assert.Contains(TapeSourceCatalog.GdeltCitation, line, StringComparison.Ordinal);
        Assert.Contains("never evaluation evidence", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ U-tape-gaps: what the tape did not record

    /// <summary>The deliveries of one series every <paramref name="every"/> from <paramref name="first"/> to <paramref name="last"/>, both included.</summary>
    static void Deliver(TapeStore store, string source, string series, DateTimeOffset first, DateTimeOffset last, TimeSpan every)
    {
        for (var at = first; at <= last; at += every) store.Append(Attempt(source, series, at));
    }

    static readonly TimeSpan TwoMinutes = TimeSpan.FromMinutes(2);

    /// <summary>One source's day as the reader reads it.</summary>
    static TapeSourceDay Of(TapeDay day, string source) => day.Sources.Single(s => s.Source == source);

    /// <summary>A gap's whole bounds, where it enters and leaves the day, and the attempts inside that part.</summary>
    static (DateTimeOffset? From, DateTimeOffset? To, DateTimeOffset ClippedFrom, DateTimeOffset ClippedTo, int Attempts) Shape(TapeGap g) =>
        (g.From, g.To, g.ClippedFrom, g.ClippedTo, g.Attempts);

    /// <summary>An instant as the line names one inside its day: the owner's local hour and minute.</summary>
    static string Hm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>An instant as the line names one outside its day: the owner's local date, hour and minute.</summary>
    static string Stamp(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>A length as the line words one: whole minutes, hours when there are sixty of them.</summary>
    static string Span(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0 min";
        if (t < TimeSpan.FromMinutes(1)) return $"{(long)t.TotalSeconds} s";
        var minutes = (long)t.TotalMinutes;
        return minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
    }

    /// <summary>The report's tape line for the day <paramref name="at"/> falls in, composed at the gateway's own clock.</summary>
    string TapeLineOf(TradingGateway gw, DateTimeOffset at)
    {
        var line = DailyReportText.Render(gw.Reports.Compose(at)).Split('\n')
            .Single(l => l.StartsWith("- market context tape:", StringComparison.Ordinal));
        log.WriteLine(line);
        return line;
    }

    /// <summary>A gateway whose clock — the report's instant — is <paramref name="instant"/>, reading the tape at <paramref name="file"/>.</summary>
    static async Task<(TradingGateway Gw, Database Db)> ReportingAt(DateTimeOffset instant, string file)
    {
        var (gw, _, db) = await TestEnv.Ready(options: new GatewayOptions { Clock = new TestClock(instant) });
        gw.Tape = new TapeReader(file);
        return (gw, db);
    }

    /// <summary>
    /// (a) A NIGHT THE APP WAS DOWN IS A GAP ON BOTH DAYS IT TOUCHES. Open interest delivered from 21:00 to 22:00 yesterday —
    /// its first delivery ever — and from 07:00 to 08:00 today, and nothing between: one gap, from 22:00 yesterday to 07:00
    /// today, which yesterday's line counts from 22:00 to its midnight and today's from midnight to 07:00, each naming
    /// where the gap began or ended on the other day. Before this unit both lines counted nothing for it; and today's finds
    /// the delivery at 22:00 only by looking back past its own band of rows.
    /// </summary>
    [Fact]
    public async Task A_night_the_app_was_down_is_a_gap_on_both_days_it_touches()
    {
        var noon = TestEnv.LocalNoon();
        var (from, to) = DailyReports.LocalDay(noon);
        var (yFrom, yTo) = DailyReports.LocalDay(noon.AddDays(-1));
        using var store = new TapeStore(NewFile());
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", yTo.AddHours(-3), yTo.AddHours(-2), TwoMinutes);
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", from.AddHours(7), from.AddHours(8), TwoMinutes);
        var instant = from.AddHours(8).AddMinutes(1);

        // THE TAPE'S OWN READING: one gap, the same on both days, each day holding its own part of it.
        var reader = new TapeReader(store.File);
        var yesterday = Of(reader.Day(yFrom, yTo, instant), TapeSourceCatalog.OpenInterest);
        var today = Of(reader.Day(from, to, instant), TapeSourceCatalog.OpenInterest);
        Assert.Equal((yTo.AddHours(-2), from.AddHours(7), yTo.AddHours(-2), yTo, 0), Shape(Assert.Single(yesterday.Gaps)));
        Assert.Equal((yTo.AddHours(-2), from.AddHours(7), from, from.AddHours(7), 0), Shape(Assert.Single(today.Gaps)));
        Assert.Equal((TimeSpan.FromHours(1), TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1)), (yesterday.Recorded, today.Recorded));
        Assert.Equal(yTo.AddHours(-3), yesterday.Began);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 1 h of {Span(yTo - yFrom)}, its recording began at {Hm(yTo.AddHours(-3))}, 1 gap: "
            + $"{Hm(yTo.AddHours(-2))}–24:00 (2 h, until {Stamp(from.AddHours(7))}; nothing asked)", TapeLineOf(gw, noon.AddDays(-1)),
            StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 1 h 1 min of {Span(instant - from)} so far, 1 gap: "
            + $"00:00–{Hm(from.AddHours(7))} (7 h, since {Stamp(yTo.AddHours(-2))}; nothing asked)", TapeLineOf(gw, noon),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (b) A DAY WITH NO DELIVERY READS AS ONE GAP, NEVER AS ZERO. Open interest delivered just before yesterday began and
    /// again half an hour into today, and not once yesterday: yesterday's line counts one gap of the whole day, named by
    /// the deliveries either side of it — not "0 gaps", which is what an unrecorded day read as before this unit. Settled
    /// funding delivered once, nine days before yesterday, and never since: its gap is the whole day too, and began before
    /// the look back stopped.
    /// </summary>
    [Fact]
    public async Task A_day_with_no_delivery_reads_as_one_gap_never_as_zero()
    {
        var noon = TestEnv.LocalNoon();
        var (from, _) = DailyReports.LocalDay(noon);
        var (yFrom, yTo) = DailyReports.LocalDay(noon.AddDays(-1));
        using var store = new TapeStore(NewFile());
        store.Append(Attempt(TapeSourceCatalog.Funding, "funding-rate", yFrom.AddDays(-9)));
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", yFrom.AddMinutes(-10), yFrom.AddMinutes(-6), TwoMinutes);
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", from.AddMinutes(30), from.AddMinutes(34), TwoMinutes);
        var instant = from.AddMinutes(35);

        // THE TAPE'S OWN READING.
        var day = new TapeReader(store.File).Day(yFrom, yTo, instant);
        var oi = Of(day, TapeSourceCatalog.OpenInterest);
        Assert.Equal((yFrom.AddMinutes(-6), from.AddMinutes(30), yFrom, yTo, 0), Shape(Assert.Single(oi.Gaps)));
        Assert.Equal(TimeSpan.Zero, oi.Recorded);
        Assert.Equal((null, null, yFrom, yTo, 0), Shape(Assert.Single(Of(day, TapeSourceCatalog.Funding).Gaps)));
        Assert.Equal(2, day.Gaps);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        var line = TapeLineOf(gw, noon.AddDays(-1));
        Assert.Contains("0 rows recorded from 0 requests, 0 of them recorded a failure; 2 gaps — ", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 0 min of {Span(yTo - yFrom)}, 1 gap: 00:00–24:00 ({Span(yTo - yFrom)}, "
            + $"since {Stamp(yFrom.AddMinutes(-6))}, until {Stamp(from.AddMinutes(30))}; nothing asked)", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.Funding} recorded 0 min of {Span(yTo - yFrom)}, 1 gap: 00:00–24:00 ({Span(yTo - yFrom)}, "
            + $"no delivery since before {(yFrom - TapeReader.GapLookBack).ToLocalTime():yyyy-MM-dd}, still open; nothing asked)",
            line, StringComparison.Ordinal);
        Assert.DoesNotContain("0 gaps", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// (c) A RESTART ACROSS MIDNIGHT IS COUNTED ONCE IN EACH DAY. Open interest delivered until 23:44 yesterday and again
    /// from 00:16 today: one gap of 32 minutes, sixteen of them on each day, and each day's line counts its own part once.
    /// The delivery at 23:44 is more than <see cref="TapeReader.ArrivalSlack"/> before today began, so today's line finds it
    /// by looking back, not in the band of rows its day read.
    /// </summary>
    [Fact]
    public async Task A_restart_across_midnight_is_counted_once_in_each_day()
    {
        var noon = TestEnv.LocalNoon();
        var (from, to) = DailyReports.LocalDay(noon);
        var (yFrom, yTo) = DailyReports.LocalDay(noon.AddDays(-1));
        using var store = new TapeStore(NewFile());
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", yTo.AddHours(-1), yTo.AddMinutes(-16), TwoMinutes);
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", from.AddMinutes(16), from.AddMinutes(30), TwoMinutes);
        var instant = from.AddMinutes(31);

        // THE TAPE'S OWN READING: the one gap, its two parts adding up to it, and nothing else on either day.
        var reader = new TapeReader(store.File);
        var yesterday = Assert.Single(Of(reader.Day(yFrom, yTo, instant), TapeSourceCatalog.OpenInterest).Gaps);
        var today = Assert.Single(Of(reader.Day(from, to, instant), TapeSourceCatalog.OpenInterest).Gaps);
        Assert.Equal((yTo.AddMinutes(-16), from.AddMinutes(16), yTo.AddMinutes(-16), yTo, 0), Shape(yesterday));
        Assert.Equal((yTo.AddMinutes(-16), from.AddMinutes(16), from, from.AddMinutes(16), 0), Shape(today));
        Assert.Equal(TimeSpan.FromMinutes(32), yesterday.Length + today.Length);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 44 min of {Span(yTo - yFrom)}, its recording began at {Hm(yTo.AddHours(-1))}, 1 gap: "
            + $"{Hm(yTo.AddMinutes(-16))}–24:00 (16 min, until {Stamp(from.AddMinutes(16))}; nothing asked)", TapeLineOf(gw, noon.AddDays(-1)),
            StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 15 min of 31 min so far, 1 gap: "
            + $"00:00–{Hm(from.AddMinutes(16))} (16 min, since {Stamp(yTo.AddMinutes(-16))}; nothing asked)", TapeLineOf(gw, noon),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// (d) A GAP WITH FAILED ATTEMPTS IS NAMED FAILING, AND ONE WITH NONE "NOTHING ASKED". Open interest delivered from
    /// 08:00, failed ten times from 08:32 to 08:50 — the newest because the host did not answer — delivered again from
    /// 08:52 to 09:20, was not asked at all until 11:00, and delivered to 11:10: two gaps, each named by what the tape holds
    /// inside it. And the premium index, first delivered at 09:00, went quiet five times that hour — for 5, 10, 3, 20 and 4
    /// minutes — so its line lists its three longest, in the order they happened, and counts the other two.
    /// </summary>
    [Fact]
    public async Task A_gap_with_failed_attempts_is_named_failing_and_one_with_none_not_asked()
    {
        var noon = TestEnv.LocalNoon();
        var (from, to) = DailyReports.LocalDay(noon);
        DateTimeOffset At(int hour, int minute) => from.AddHours(hour).AddMinutes(minute);

        // EVERY ATTEMPT IN THE ORDER IT ARRIVED, as the tape's writers write them.
        var attempts = new List<(DateTimeOffset At, string Source, string Series, string? Note)>();
        void Every(string source, string series, DateTimeOffset first, DateTimeOffset last, string? note = null)
        {
            for (var at = first; at <= last; at += TwoMinutes) attempts.Add((at, source, series, note));
        }
        Every(TapeSourceCatalog.OpenInterest, "open-interest", At(8, 0), At(8, 30));
        Every(TapeSourceCatalog.OpenInterest, "open-interest", At(8, 32), At(8, 48), "the host answered 503 and nothing was read");
        attempts.Add((At(8, 50), TapeSourceCatalog.OpenInterest, "open-interest", "the host did not answer within 10 s"));
        Every(TapeSourceCatalog.OpenInterest, "open-interest", At(8, 52), At(9, 20));
        Every(TapeSourceCatalog.OpenInterest, "open-interest", At(11, 0), At(11, 10));
        foreach (var minute in new[] { 0, 5, 6, 16, 17, 20, 21, 41, 42, 46 })
            attempts.Add((At(9, minute), TapeSourceCatalog.Premium, "premium-index", null));
        Every(TapeSourceCatalog.Premium, "premium-index", At(9, 48), At(11, 10));

        using var store = new TapeStore(NewFile());
        foreach (var (at, source, series, note) in attempts.OrderBy(a => a.At)) store.Append(Attempt(source, series, at, note));
        var instant = At(11, 11);

        // THE TAPE'S OWN READING: what is inside each gap.
        var day = new TapeReader(store.File).Day(from, to, instant);
        var oi = Of(day, TapeSourceCatalog.OpenInterest);
        Assert.Equal(
            [(At(8, 30), 10, "the host did not answer within 10 s"), (At(9, 20), 0, null)],
            oi.Gaps.Select(g => (g.ClippedFrom, g.Attempts, g.NewestFailure)));
        Assert.Equal((47, 10), (oi.Attempts, oi.Failed));
        var premium = Of(day, TapeSourceCatalog.Premium);
        Assert.Equal([5, 10, 3, 20, 4], premium.Gaps.Select(g => (int)g.Length.TotalMinutes));
        Assert.Equal(TimeSpan.FromMinutes(131 - 42), premium.Recorded);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        var line = TapeLineOf(gw, noon);
        Assert.Contains(
            $", 2 gaps: {Hm(At(8, 30))}–{Hm(At(8, 52))} (22 min; asked 10 times, all failed: the host did not answer within 10 s), "
            + $"{Hm(At(9, 20))}–{Hm(At(11, 0))} (1 h 40 min; nothing asked)", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.Premium} recorded 1 h 29 min of {Span(instant - from)} so far, its recording began at {Hm(At(9, 0))}, 5 gaps: "
            + $"{Hm(At(9, 0))}–{Hm(At(9, 5))} (5 min; nothing asked), {Hm(At(9, 6))}–{Hm(At(9, 16))} (10 min; nothing asked), "
            + $"{Hm(At(9, 21))}–{Hm(At(9, 41))} (20 min; nothing asked) and 2 more of 7 min in all", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// (e) A DAY STILL OPEN COUNTS ITS TAIL TO THE REPORT'S INSTANT. Open interest delivered from 09:00 to 10:00 and the
    /// premium index to 10:28; the report is written at 10:30. Open interest's half hour since its last delivery is a gap
    /// still open, ending at the report's instant; the premium index's two minutes are inside its allowance and are not.
    /// Each share is of the ten and a half hours the day has had.
    /// </summary>
    [Fact]
    public async Task A_day_still_open_counts_its_tail_to_the_reports_instant()
    {
        var noon = TestEnv.LocalNoon();
        var (from, to) = DailyReports.LocalDay(noon);
        using var store = new TapeStore(NewFile());
        for (var at = from.AddHours(9); at <= from.AddHours(10).AddMinutes(28); at += TwoMinutes)
        {
            store.Append(Attempt(TapeSourceCatalog.Premium, "premium-index", at));
            if (at <= from.AddHours(10)) store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", at));
        }
        var instant = from.AddHours(10).AddMinutes(30);

        // THE TAPE'S OWN READING.
        var day = new TapeReader(store.File).Day(from, to, instant);
        Assert.Equal((instant, true), (day.Until, day.Open));
        var oi = Of(day, TapeSourceCatalog.OpenInterest);
        Assert.Equal((from.AddHours(10), null, from.AddHours(10), instant, 0), Shape(Assert.Single(oi.Gaps)));
        Assert.Equal(TimeSpan.FromHours(1), oi.Recorded);
        var premium = Of(day, TapeSourceCatalog.Premium);
        Assert.Empty(premium.Gaps);
        Assert.Equal(TimeSpan.FromMinutes(90), premium.Recorded);

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        var line = TapeLineOf(gw, noon);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 1 h of {Span(instant - from)} so far, its recording began at {Hm(from.AddHours(9))}, "
            + $"1 gap: {Hm(from.AddHours(10))}–{Hm(instant)} (30 min, still open; nothing asked)", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.Premium} recorded 1 h 30 min of {Span(instant - from)} so far, its recording began at {Hm(from.AddHours(9))}, no gap",
            line, StringComparison.Ordinal);
    }

    /// <summary>
    /// (f) BEFORE A SOURCE'S FIRST DELIVERY, NOTHING IS A GAP. Yesterday open interest failed once at 09:00 and delivered for
    /// the first time at 10:00, to 10:20; the premium index only ever failed; the funding series was never asked; OKX first
    /// delivered just after midnight today. Open interest's morning is no gap and its line says when its recording began;
    /// the others recorded nothing and say why, and none of them counts a gap.
    /// </summary>
    [Fact]
    public async Task Before_a_sources_first_delivery_nothing_is_a_gap()
    {
        var noon = TestEnv.LocalNoon();
        var (from, _) = DailyReports.LocalDay(noon);
        var (yFrom, yTo) = DailyReports.LocalDay(noon.AddDays(-1));
        using var store = new TapeStore(NewFile());
        store.Append(Attempt(TapeSourceCatalog.OpenInterest, "open-interest", yFrom.AddHours(9), "the host could not be reached: refused"));
        for (var n = 0; n < 3; n++)
            store.Append(Attempt(TapeSourceCatalog.Premium, "premium-index", yFrom.AddHours(9).AddMinutes(5 * n + 1),
                n == 2 ? "the host answered 451 and nothing was read" : "the host answered 403 and nothing was read"));
        Deliver(store, TapeSourceCatalog.OpenInterest, "open-interest", yFrom.AddHours(10), yFrom.AddHours(10).AddMinutes(20), TwoMinutes);
        store.Append(Attempt(TapeSourceCatalog.OkxEeaAnnouncements, "announcements", from.AddMinutes(10)));
        var instant = from.AddMinutes(30);

        // THE TAPE'S OWN READING.
        var day = new TapeReader(store.File).Day(yFrom, yTo, instant);
        Assert.Equal(1, day.Gaps);
        var oi = Of(day, TapeSourceCatalog.OpenInterest);
        Assert.Equal((true, yFrom.AddHours(10), TimeSpan.FromMinutes(20)), (oi.Begun, oi.Began, oi.Recorded));
        Assert.Equal((yFrom.AddHours(10).AddMinutes(20), null, yFrom.AddHours(10).AddMinutes(20), yTo, 0), Shape(Assert.Single(oi.Gaps)));
        var premium = Of(day, TapeSourceCatalog.Premium);
        Assert.Equal((false, null, 3, 3, "the host answered 451 and nothing was read"),
            (premium.Begun, premium.Began, premium.Attempts, premium.Failed, premium.NewestFailure));
        Assert.Empty(premium.Gaps);
        Assert.Equal((false, null, 0), (Of(day, TapeSourceCatalog.Funding).Begun, Of(day, TapeSourceCatalog.Funding).Began, Of(day, TapeSourceCatalog.Funding).Attempts));
        Assert.Equal((false, from.AddMinutes(10)), (Of(day, TapeSourceCatalog.OkxEeaAnnouncements).Begun, Of(day, TapeSourceCatalog.OkxEeaAnnouncements).Began));

        // AND THE REPORT SAYS SO IN WORDS.
        var (gw, db) = await ReportingAt(instant, store.File);
        using var _1 = db;
        var line = TapeLineOf(gw, noon.AddDays(-1));
        Assert.Contains("; 1 gap — ", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OpenInterest} recorded 20 min of {Span(yTo - yFrom)}, its recording began at {Hm(yFrom.AddHours(10))}, 1 gap: "
            + $"{Hm(yFrom.AddHours(10).AddMinutes(20))}–24:00 ({Span(yTo - yFrom.AddHours(10).AddMinutes(20))}, still open; nothing asked)",
            line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.Premium} recorded nothing — its recording has not begun; asked 3 times this day, all failed: "
            + "the host answered 451 and nothing was read", line, StringComparison.Ordinal);
        Assert.Contains($"{TapeSourceCatalog.Funding} recorded nothing — its recording has not begun, nothing asked", line, StringComparison.Ordinal);
        Assert.Contains(
            $"{TapeSourceCatalog.OkxEeaAnnouncements} recorded nothing — its recording began after this day, at {Stamp(from.AddMinutes(10))}, nothing asked",
            line, StringComparison.Ordinal);
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
