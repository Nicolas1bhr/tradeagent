using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// THE TAPE'S STORE, ON ITS OWN FILE (<c>U-tape-store</c>). No network here at all: every fetch is a
/// record built by the test, because what is under test is what the ONLY WRITER of
/// <c>state/tape.db</c> does with what it is handed — first readings that stand, revisions that are
/// rows, "as of" that means as of, and a file this build did not write left as it was found.
/// </summary>
public class TapeStoreTests(ITestOutputHelper log)
{
    const string Source = "binance-um-oi-5m";
    const string Series = "open-interest-5m";
    const string Symbol = "BTCUSDT";

    /// <summary>A loopback URL: nothing here is ever asked, it is only the address the fetch records.</summary>
    const string LoopbackUrl = "http://127.0.0.1:9/futures/data/openInterestHist?symbol=BTCUSDT&period=5m&limit=3";

    static readonly DateTimeOffset Noon = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    static TapeFetch Fetch(DateTimeOffset receivedAt, string url = LoopbackUrl, string source = Source,
        string series = Series) => new()
    {
        Source = source,
        Series = series,
        Url = url,
        RequestedAt = receivedAt.AddMilliseconds(-300),
        ReceivedAt = receivedAt,
        HttpStatus = 200,
        BodySha256 = new string('b', 64)
    };

    /// <summary>One open-interest point in the vendor's own shape, its decimal kept as the string Binance serves.</summary>
    static TapeItem Point(DateTimeOffset sourceTime, string openInterest, string symbol = Symbol) => new(symbol,
        sourceTime,
        $$"""{"symbol":"{{symbol}}","sumOpenInterest":"{{openInterest}}","timestamp":{{Ms(sourceTime)}}}""");

    static string Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    static SqliteConnection Raw(string file)
    {
        var c = new SqliteConnection($"Data Source={file};Pooling=False");
        c.Open();
        return c;
    }

    static string? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static List<string> Tables(string file)
    {
        using var c = Raw(file);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }

    /// <summary>
    /// THE FIRST READING STANDS AND A DIFFERENT ONE IS A NEW ROW. The vendor publishes a point, is
    /// re-read unchanged, changes its mind, is re-read unchanged again, and changes back. Three revisions
    /// result, each with the arrival instant and the fetch it really came in, and neither unchanged
    /// re-reading moved anything — which is what <c>INSERT OR REPLACE</c> would do, and what this test
    /// is here to see go red.
    /// </summary>
    [Fact]
    public void A_rereading_with_a_different_payload_is_a_new_revision_not_an_overwrite()
    {
        using var store = new TapeStore(NewFile());

        var first = store.Append(Fetch(Noon.AddSeconds(2)), [Point(Noon, "97045.28100000")]);
        var same = store.Append(Fetch(Noon.AddMinutes(5).AddSeconds(2)), [Point(Noon, "97045.28100000")]);
        var revised = store.Append(Fetch(Noon.AddMinutes(10).AddSeconds(2)), [Point(Noon, "97050.00000000")]);
        var again = store.Append(Fetch(Noon.AddMinutes(15).AddSeconds(2)), [Point(Noon, "97050.00000000")]);
        var back = store.Append(Fetch(Noon.AddMinutes(20).AddSeconds(2)), [Point(Noon, "97045.28100000")]);

        log.WriteLine($"first={first} same={same} revised={revised} again={again} back={back}");

        // WHAT EACH APPEND SAYS IT DID: a new row, nothing, a revision, nothing, a revision.
        Assert.Equal((1, 1, 0, 0), (first.Items, first.Stored, first.Revised, first.Unchanged));
        Assert.Equal((1, 0, 0, 1), (same.Items, same.Stored, same.Revised, same.Unchanged));
        Assert.Equal((1, 1, 1, 0), (revised.Items, revised.Stored, revised.Revised, revised.Unchanged));
        Assert.Equal((1, 0, 0, 1), (again.Items, again.Stored, again.Revised, again.Unchanged));
        Assert.Equal((1, 1, 1, 0), (back.Items, back.Stored, back.Revised, back.Unchanged));

        // AND WHAT IS ACTUALLY THERE: three revisions under one key, each carrying ITS OWN fetch and ITS
        // OWN arrival. An unchanged re-reading that overwrote would show up right here as revision 1
        // arriving at 12:05 or revision 2 at 12:15, carrying the re-reading's fetch.
        var revisions = store.Revisions(Source, Series, TapeStore.NaturalKey(Symbol, Noon));
        foreach (var r in revisions) log.WriteLine($"rev {r.Revision} id={r.Id} fetch={r.FetchId} at {r.ReceivedAt:O} {r.Payload}");

        Assert.Equal([1, 2, 3], revisions.Select(r => r.Revision));
        Assert.Equal([first.FetchId, revised.FetchId, back.FetchId], revisions.Select(r => r.FetchId));
        Assert.Equal(
            [Noon.AddSeconds(2), Noon.AddMinutes(10).AddSeconds(2), Noon.AddMinutes(20).AddSeconds(2)],
            revisions.Select(r => r.ReceivedAt));
        Assert.True(revisions[0].Id < revisions[1].Id && revisions[1].Id < revisions[2].Id,
            "the revisions are not in the order they were first written");

        // THE VENDOR'S DECIMAL IS KEPT AS IT WAS SERVED, trailing zeros and all, and the flip back to
        // the first value is the first value again — same payload, same hash, a later revision.
        Assert.Contains("\"97045.28100000\"", revisions[0].Payload, StringComparison.Ordinal);
        Assert.Contains("\"97050.00000000\"", revisions[1].Payload, StringComparison.Ordinal);
        Assert.Equal(revisions[0].PayloadSha256, revisions[2].PayloadSha256);
        Assert.NotEqual(revisions[0].PayloadSha256, revisions[1].PayloadSha256);
        Assert.All(revisions, r => Assert.Equal(Noon, r.SourceTime));

        // EVERY ATTEMPT IS A ROW, the unchanged ones included, and only the new readings name one.
        Assert.Equal(5, store.Fetches(Source, Series).Count);
        Assert.Equal([1, 0, 1, 0, 1],
            new[] { first, same, revised, again, back }.Select(a => store.ObservationsOf(a.FetchId).Count));
    }

    /// <summary>
    /// "AS OF" MEANS WHAT HAD ARRIVED BY THEN. A point arrives, is revised five minutes later, and a newer
    /// point arrives five minutes after that; each instant between is answered with what TradeAgent had
    /// in hand at it — never the revision before it arrived, never the newer point before it existed
    /// here. The audience is required and checked inside the reader.
    /// </summary>
    [Fact]
    public void As_of_returns_what_had_arrived_by_then_for_the_named_audience()
    {
        using var store = new TapeStore(NewFile());
        var research = BarAudience.Pipe(CouncilRoles.Research);
        var nobody = BarAudience.Pipe(null);

        var p1 = Noon.AddMinutes(5);
        var p2 = Noon.AddMinutes(10);
        store.Append(Fetch(p1.AddSeconds(2)), [Point(p1, "100.000")]);
        store.Append(Fetch(p2.AddSeconds(2)), [Point(p1, "101.000")]);
        store.Append(Fetch(p2.AddMinutes(5).AddSeconds(2)), [Point(p2, "102.000")]);
        store.Append(Fetch(p2.AddMinutes(5).AddSeconds(2)), [Point(p2, "999.000", "ETHUSDT")]);

        string? At(DateTimeOffset t, BarAudience? who = null) =>
            store.AsOf(who ?? research, Source, Series, Symbol, t) is { } o
                ? $"{o.SourceTime:HH:mm} r{o.Revision} {o.Payload.Split('"')[3]}"
                : null;

        foreach (var t in new[] { p1.AddSeconds(1), p1.AddSeconds(2), p1.AddMinutes(4), p2.AddSeconds(2), p2.AddMinutes(6) })
            log.WriteLine($"as of {t:HH:mm:ss}: {At(t) ?? "nothing"}");

        // BEFORE ANYTHING ARRIVED, NOTHING — and the instant of arrival is included.
        Assert.Null(At(p1.AddSeconds(1)));
        Assert.Equal("12:05 r1 100.000", At(p1.AddSeconds(2)));

        // BETWEEN THE FIRST READING AND THE REVISION, THE FIRST READING. A reader that ignored the
        // arrival instant would answer the 12:10 point, which did not reach this machine until 12:15.
        Assert.Equal("12:05 r1 100.000", At(p1.AddMinutes(4)));

        // ONCE THE REVISION ARRIVED, THE REVISION; ONCE THE NEWER POINT ARRIVED, THE NEWER POINT.
        Assert.Equal("12:05 r2 101.000", At(p2.AddSeconds(2)));
        Assert.Equal("12:10 r1 102.000", At(p2.AddMinutes(6)));

        // ONE SUBJECT'S SERIES, never another's.
        Assert.Equal("12:10 r1 999.000",
            store.AsOf(research, Source, Series, "ETHUSDT", p2.AddMinutes(6)) is { } eth
                ? $"{eth.SourceTime:HH:mm} r{eth.Revision} {eth.Payload.Split('"')[3]}" : null);

        // THE AUDIENCE IS REQUIRED. No tape holdout exists yet, so every audience reads the same.
        Assert.Throws<ArgumentNullException>(() => store.AsOf(null!, Source, Series, Symbol, p2));
        Assert.Equal(At(p2.AddSeconds(2), research), At(p2.AddSeconds(2), nobody));
    }

    /// <summary>
    /// THE CLASS IS THE STORE'S, FROM WHAT IT RECORDED. Live only for a built-in row, fetched from that
    /// row's built-in origin, arriving within its cadence plus thirty seconds of its source time; archive
    /// for a late reading, for the same reading from another origin, and for a row a file added even at
    /// the built-in address. A later revision is never above the one before it.
    ///
    /// <para>The built-in address below is never asked anything: the store makes no request, and a
    /// fetch here is only the record of one.</para>
    /// </summary>
    [Fact]
    public void A_late_row_or_an_override_origin_is_arch_and_an_on_time_built_in_row_is_live()
    {
        using var store = new TapeStore(NewFile());

        var premium = TapeSourceCatalog.BuiltIn().Single(r => r.Id == TapeSourceCatalog.Premium);
        Assert.Equal(60, premium.CadenceSeconds);
        var series = premium.Series[0].Id;
        var builtInUrl = premium.Series[0].UrlShape.Replace("{base}", premium.BaseUrl, StringComparison.Ordinal);
        const string elsewhereUrl = "http://127.0.0.1:9/fapi/v1/premiumIndex";

        TapeFetch At(DateTimeOffset receivedAt, string url, string source = TapeSourceCatalog.Premium) =>
            Fetch(receivedAt, url, source, series);

        static TapeItem Mark(string symbol, DateTimeOffset time, string price) => new(symbol, time,
            $$"""{"markPrice":"{{price}}","symbol":"{{symbol}}","time":{{Ms(time)}}}""");

        var received = Noon.AddSeconds(2);
        var builtIn = store.Append(At(received, builtInUrl),
        [
            Mark("BTCUSDT", Noon, "85495.82186232"),                // two seconds old
            Mark("ETHUSDT", received.AddSeconds(-90), "4000.00"),   // exactly cadence + 30 s old: the bound is inclusive
            Mark("SOLUSDT", received.AddSeconds(-91), "200.00"),    // one second past it
            Mark("BNBUSDT", received.AddSeconds(1), "600.00")       // the vendor's clock a second ahead of this machine's
        ]);
        var elsewhere = store.Append(At(received, elsewhereUrl), [Mark("XRPUSDT", Noon, "0.50")]);
        var added = store.Append(At(received, builtInUrl, source: "my-premium"), [Mark("DOGEUSDT", Noon, "0.10")]);

        string Class(long fetchId, string symbol) =>
            store.ObservationsOf(fetchId).Single(o => o.Subject == symbol).EvidenceClass;

        foreach (var (fetch, symbol) in new[] { (builtIn, "BTCUSDT"), (builtIn, "ETHUSDT"), (builtIn, "SOLUSDT"),
                     (builtIn, "BNBUSDT"), (elsewhere, "XRPUSDT"), (added, "DOGEUSDT") })
            log.WriteLine($"{symbol}: {Class(fetch.FetchId, symbol)}");

        Assert.Equal(TapeClass.Live, Class(builtIn.FetchId, "BTCUSDT"));
        Assert.Equal(TapeClass.Live, Class(builtIn.FetchId, "ETHUSDT"));
        Assert.Equal(TapeClass.Arch, Class(builtIn.FetchId, "SOLUSDT"));
        Assert.Equal(TapeClass.Live, Class(builtIn.FetchId, "BNBUSDT"));

        // ON TIME, BUILT-IN SOURCE, ANOTHER ORIGIN: ARCHIVE. This is the line that would read live if the
        // class ignored where a reading came from.
        Assert.Equal(TapeClass.Arch, Class(elsewhere.FetchId, "XRPUSDT"));

        // A ROW A FILE ADDED IS ARCHIVE AT ANY ADDRESS — even the built-in one, on time.
        Assert.Equal(TapeClass.Arch, Class(added.FetchId, "DOGEUSDT"));

        // THE ORIGIN ON EACH FETCH ROW IS THE ONE ITS CLASS WAS DECIDED BY, read off its URL.
        var fetches = store.Fetches(TapeSourceCatalog.Premium);
        Assert.Equal(UrlOrigin.Of(TapeSourceCatalog.BinanceUmBaseUrl), fetches.Single(f => f.Id == builtIn.FetchId).Origin);
        Assert.Equal("http://127.0.0.1:9", fetches.Single(f => f.Id == elsewhere.FetchId).Origin);

        // A LATER REVISION NEVER UPGRADES: XRPUSDT's point re-published from the built-in origin twenty
        // seconds later — on time on its own — stays archive, because its first reading was.
        store.Append(At(Noon.AddSeconds(20), builtInUrl), [Mark("XRPUSDT", Noon, "0.51")]);
        Assert.Equal([TapeClass.Arch, TapeClass.Arch],
            store.Revisions(TapeSourceCatalog.Premium, series, TapeStore.NaturalKey("XRPUSDT", Noon)).Select(o => o.EvidenceClass));

        // AND A LIVE DATUM'S LATE REVISION IS ARCHIVE.
        store.Append(At(Noon.AddMinutes(5), builtInUrl), [Mark("BTCUSDT", Noon, "85500.00000000")]);
        Assert.Equal([TapeClass.Live, TapeClass.Arch],
            store.Revisions(TapeSourceCatalog.Premium, series, TapeStore.NaturalKey("BTCUSDT", Noon)).Select(o => o.EvidenceClass));

        // AND NOTHING A CALLER HANDS THE STORE CAN CARRY A CLASS: there is no member to put one in.
        Assert.DoesNotContain(typeof(TapeItem).GetProperties(), p => p.Name.Contains("Class", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(TapeFetch).GetProperties(), p => p.Name.Contains("Class", StringComparison.Ordinal)
                                                                     || p.Name.Contains("Origin", StringComparison.Ordinal));
    }

    /// <summary>
    /// ITS OWN FILE AND ITS OWN LADDER, AND A NEWER FILE IS LEFT AS IT WAS FOUND. The version is read
    /// BEFORE anything is migrated — a newer tape gets no table of this build's added to it, and keeps
    /// its journal mode — and the app is told in the activity log, in words.
    /// </summary>
    [Fact]
    public void The_tape_has_its_own_file_and_ladder_and_a_newer_file_is_refused_with_an_activity_line()
    {
        // THE APP'S TAPE IS ITS OWN FILE BESIDE THE DATABASE, NOT A RUNG OF IT.
        Assert.Equal(Path.Combine(Paths.State, "tape.db"), Paths.TapeFile);
        Assert.NotEqual(Paths.DatabaseFile, Paths.TapeFile);

        var file = NewFile();
        using (var store = new TapeStore(file))
        {
            Assert.Equal(TapeStore.Schema, store.Version);
            Assert.Equal(1, store.Version);
            store.Append(Fetch(Noon.AddSeconds(2)), [Point(Noon, "1.0")]);
        }

        Assert.Equal(["tape_fetch", "tape_meta", "tape_obs"], Tables(file));
        using (var raw = Raw(file))
        {
            Assert.Equal("wal", Scalar(raw, "PRAGMA journal_mode"));
            Assert.Equal("1", Scalar(raw, "SELECT value FROM tape_meta WHERE key='schema'"));
        }

        // REOPENING IS NOT A MIGRATION: what was written is still there and the version did not move.
        using (var again = new TapeStore(file))
        {
            Assert.Equal(1, again.Version);
            Assert.Single(again.Fetches());
        }

        // AND THE APP'S OWN DATABASE GAINED NOTHING: no tape table lives there.
        using (var db = TestEnv.NewDb())
        {
            var tapeTables = db.Read(_ =>
            {
                using var c = db.Cmd("SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'tape%'");
                return Convert.ToInt32(c.ExecuteScalar(), CultureInfo.InvariantCulture);
            });
            Assert.Equal(0, tapeTables);
        }

        // A TAPE WRITTEN BY A NEWER BUILD: version 2, a table this build has never heard of, and the
        // default rollback journal.
        var newer = NewFile();
        using (var raw = Raw(newer))
        using (var cmd = raw.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE tape_meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO tape_meta(key, value) VALUES('schema', '2');
                CREATE TABLE tape_rung_two(x INTEGER);
                """;
            cmd.ExecuteNonQuery();
        }

        var refused = Assert.Throws<InvalidOperationException>(() => new TapeStore(newer));
        log.WriteLine(refused.Message);
        Assert.Contains("newer TradeAgent", refused.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", refused.Message, StringComparison.Ordinal);

        // NOTHING WAS MIGRATED BEFORE THE REFUSAL: no table of this build's was added, the version row
        // still says 2, and the journal mode was never switched.
        Assert.Equal(["tape_meta", "tape_rung_two"], Tables(newer));
        using (var raw = Raw(newer))
        {
            Assert.Equal("2", Scalar(raw, "SELECT value FROM tape_meta WHERE key='schema'"));
            Assert.Equal("delete", Scalar(raw, "PRAGMA journal_mode"));
        }

        // AND THE OWNER IS TOLD, as an activity line, while nothing about the app's start depends on it.
        using var appDb = TestEnv.NewDb();
        var activity = new LogStore(appDb);
        Assert.Null(TapeStore.TryOpen(newer, activity));

        var line = Assert.Single(activity.RecentActivity(),
            a => a.Text.StartsWith("TradeAgent is not recording market context:", StringComparison.Ordinal));
        log.WriteLine($"{line.Level}: {line.Text}");
        Assert.Equal("warn", line.Level);
        Assert.Contains("newer TradeAgent", line.Text, StringComparison.Ordinal);
        Assert.Equal(["tape_meta", "tape_rung_two"], Tables(newer));
    }

    /// <summary>
    /// ONE SPELLING PER PAYLOAD. Keys in ordinal order, no whitespace, every value exactly as served —
    /// so the same item read twice with its fields in another order is the same payload and writes
    /// nothing, and a decimal string is never re-formatted into a number the vendor did not publish.
    /// What the store refuses, it refuses before anything — the attempt included — is written.
    /// </summary>
    [Fact]
    public void A_payload_is_stored_canonical_and_keeps_the_vendors_decimal_strings()
    {
        using var store = new TapeStore(NewFile());

        var served = $$"""
            { "timestamp": {{Ms(Noon)}},
              "sumOpenInterest" : "97045.28100000", "symbol":"BTCUSDT", "ratio": 1.50 }
            """;
        var first = store.Append(Fetch(Noon.AddSeconds(2)), [new TapeItem(Symbol, Noon, served)]);

        var obs = Assert.Single(store.ObservationsOf(first.FetchId));
        Assert.Equal(
            $$"""{"ratio":1.50,"sumOpenInterest":"97045.28100000","symbol":"BTCUSDT","timestamp":{{Ms(Noon)}}}""",
            obs.Payload);
        Assert.Equal(TapeJson.Sha256(obs.Payload), obs.PayloadSha256);
        Assert.Equal(64, obs.PayloadSha256.Length);
        Assert.Equal($"{Symbol}|{Ms(Noon)}", obs.NaturalKey);

        // THE SAME ITEM, ITS FIELDS IN ANOTHER ORDER AND ANOTHER SPACING: the same payload.
        var reordered = $$"""{"symbol":"BTCUSDT","ratio":1.50,"timestamp":{{Ms(Noon)}},"sumOpenInterest":"97045.28100000"}""";
        var reread = store.Append(Fetch(Noon.AddMinutes(5)), [new TapeItem(Symbol, Noon, reordered)]);
        Assert.Equal((0, 1), (reread.Stored, reread.Unchanged));

        // REFUSED BEFORE ANYTHING IS WRITTEN — not even the attempt — because a refusal that left an
        // attempt with half its items would be a fetch row claiming a delivery that did not happen.
        var before = store.Fetches().Count;
        var huge = $$"""{"symbol":"BTCUSDT","blob":"{{new string('x', TapeStore.MaxPayloadBytes)}}"}""";
        Assert.Throws<ArgumentException>(() => store.Append(Fetch(Noon.AddMinutes(6)), [new TapeItem(Symbol, Noon, huge)]));
        Assert.Throws<ArgumentException>(() => store.Append(Fetch(Noon.AddMinutes(6)),
            [new TapeItem(Symbol, Noon, """{"symbol":"BTCUSDT","symbol":"ETHUSDT"}""")]));
        Assert.Throws<ArgumentException>(() => store.Append(Fetch(Noon.AddMinutes(6)), [new TapeItem("BTC|USDT", Noon, "{}")]));
        Assert.Throws<ArgumentException>(() => store.Append(Fetch(Noon.AddMinutes(6)), [new TapeItem(Symbol, Noon, "not json")]));
        Assert.Equal(before, store.Fetches().Count);
    }
}
