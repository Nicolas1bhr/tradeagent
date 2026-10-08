using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Core.Features;
using TradeAgent.Provisioning;
using TradeAgent.Tests;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Unit;

/// <summary>
/// HYPERLIQUID'S PUBLIC PERPETUAL CONTEXTS ON THE TAPE (<c>U-tape-chain</c>), against a loopback listener and never against
/// the vendor. The bodies are the vendor's own shape as measured on 2026-10-08 (<c>docs/RESEARCH-REQUIRED.md</c>, C5f): a
/// two-element list, the universe beside the contexts, zipped by index, with no time anywhere in it — the coins' entries
/// and values below are its 22:13Z answer's, a delisted coin and a seventh one among the six.
/// </summary>
public class TapeHyperliquidTests(ITestOutputHelper log) : IDisposable
{
    const string Series = "asset-ctxs";

    const string Hl = TapeSourceCatalog.HyperliquidAssetCtxs;

    /// <summary>
    /// THE RESEARCH DIRECTOR'S HOLDOUT OVER A LEDGER HOLDING NO CUTOFF (<c>U-tape-holdout</c>), for the feature read in (h):
    /// no window, so the read is served as it would be on a tape with no holdout. Its own database, disposed with the test.
    /// </summary>
    readonly Database _ledger = TestEnv.NewDb();

    public void Dispose() => _ledger.Dispose();

    static string NewFile() => Path.Combine(TestEnv.Home, $"tape-{Guid.NewGuid():n}.db");

    /// <summary>The positioning row as this build ships it, a fresh copy.</summary>
    static TapeSourceEntry Row() => TapeSourceCatalog.Positioning()[0];

    /// <summary>Binance's premium index row as this build ships it: a market row asked by GET, beside which the POST is compared.</summary>
    static TapeSourceEntry Premium() => TapeSourceCatalog.BuiltIn().Single(r => r.Id == TapeSourceCatalog.Premium);

    /// <summary>Where a row's first series is asked on any host: its own shape, its base left out.</summary>
    static string PathOf(TapeSourceEntry row) => TapeCollector.Url(row.Series[0], "", null);

    static TapeCollector Collector(TapeStore store, FakeArchive host, Func<DateTimeOffset> now, params TapeSourceEntry[] rows) =>
        new(store,
            baseUrl: host.BaseUrl,
            requestTimeout: TimeSpan.FromSeconds(5),
            now: now,
            catalog: new TapeSourceCatalogRead(rows.Length == 0 ? TapeSourceCatalog.Positioning() : [.. rows], null, []));

    // ---- the vendor's shape, as measured ---------------------------------------------------------------------------

    /// <summary>
    /// ONE COIN AS HYPERLIQUID SERVES IT: its universe entry and — at the same index of the other list — its context, which
    /// names nothing and carries no time.
    /// </summary>
    sealed record Coin(string Name, string Universe, string Context)
    {
        /// <summary>The two as the tape must keep them: every property of both, as served, in the canonical spelling.</summary>
        public string Merged => TapeJson.Canonical("{" + Universe[1..^1] + "," + Context[1..^1] + "}");
    }

    static readonly Coin Btc = new("BTC", """{"szDecimals":5,"name":"BTC","maxLeverage":40,"marginTableId":56}""",
        """{"funding":"0.0000105483","openInterest":"39939.21482","prevDayPx":"83203.0","dayNtlVlm":"3593774151.3242697716","premium":"-0.0004033539","oraclePx":"81814.0","markPx":"81779.1","midPx":"81780.5","impactPxs":["81772.4","81781.0"],"dayBaseVlm":"43842.8963099999"}""");

    static readonly Coin Eth = new("ETH", """{"szDecimals":4,"name":"ETH","maxLeverage":25,"marginTableId":55}""",
        """{"funding":"0.0000110996","openInterest":"1120661.5976000011","prevDayPx":"2567.0","dayNtlVlm":"1948871120.6519711018","premium":"-0.0003630057","oraclePx":"2479.3","markPx":"2478.3","midPx":"2478.35","impactPxs":["2478.3","2478.4"],"dayBaseVlm":"784147.0384999997"}""");

    /// <summary>A seventh coin, listed and trading: not this installation's to record.</summary>
    static readonly Coin Atom = new("ATOM", """{"szDecimals":2,"name":"ATOM","maxLeverage":5,"marginTableId":5}""",
        """{"funding":"0.0000121621","openInterest":"1449266.1200000003","prevDayPx":"1.7025","dayNtlVlm":"1699527.7706189996","premium":"-0.0007373795","oraclePx":"1.763","markPx":"1.7613","midPx":"1.7602","impactPxs":["1.7592","1.7617"],"dayBaseVlm":"969837.2500000001"}""");

    /// <summary>A delisted coin, as the vendor still lists it: <c>isDelisted</c>, nulls where a live book would have numbers.</summary>
    static readonly Coin Matic = new("MATIC", """{"szDecimals":1,"name":"MATIC","maxLeverage":20,"marginTableId":20,"isDelisted":true}""",
        """{"funding":"0.0","openInterest":"0.0","prevDayPx":"0.37621","dayNtlVlm":"0.0","premium":null,"oraclePx":"0.3754","markPx":"0.37621","midPx":null,"impactPxs":null,"dayBaseVlm":"0.0"}""");

    static readonly Coin Sol = new("SOL", """{"szDecimals":2,"name":"SOL","maxLeverage":20,"marginTableId":54}""",
        """{"funding":"-0.0000133865","openInterest":"5734236.3800000008","prevDayPx":"115.72","dayNtlVlm":"436168628.3827002048","premium":"-0.0005878363","oraclePx":"110.575","markPx":"110.5044","midPx":"110.505","impactPxs":["110.5","110.51"],"dayBaseVlm":"3929586.3499999978"}""");

    static readonly Coin Bnb = new("BNB", """{"szDecimals":3,"name":"BNB","maxLeverage":10,"marginTableId":51}""",
        """{"funding":"-0.000018616","openInterest":"61766.222","prevDayPx":"769.26","dayNtlVlm":"14305062.413060002","premium":"-0.0006228327","oraclePx":"735.35","markPx":"734.81","midPx":"734.795","impactPxs":["734.79","734.892"],"dayBaseVlm":"19147.547"}""");

    static readonly Coin Doge = new("DOGE", """{"szDecimals":0,"name":"DOGE","maxLeverage":10,"marginTableId":52}""",
        """{"funding":"0.0000062697","openInterest":"1106447516.0","prevDayPx":"0.08867","dayNtlVlm":"38081351.4503669888","premium":"-0.0005089059","oraclePx":"0.084495","markPx":"0.08446","midPx":"0.084451","impactPxs":["0.084442","0.084452"],"dayBaseVlm":"445255855.0"}""");

    static readonly Coin Xrp = new("XRP", """{"szDecimals":0,"name":"XRP","maxLeverage":20,"marginTableId":53}""",
        """{"funding":"0.0000125","openInterest":"201342418.0","prevDayPx":"1.4159","dayNtlVlm":"154337154.9221000373","premium":"-0.0004345937","oraclePx":"1.3806","markPx":"1.3801","midPx":"1.37995","impactPxs":["1.3799","1.38"],"dayBaseVlm":"111784593.0"}""");

    /// <summary>The answer as served: the six among others, the delisted MATIC and the seventh coin, ATOM, between them.</summary>
    static readonly Coin[] Measured = [Btc, Eth, Atom, Matic, Sol, Bnb, Doge, Xrp];

    static readonly Coin[] Six = [Btc, Eth, Sol, Bnb, Xrp, Doge];

    /// <summary>The rest of the answer's first element, as served: the margin tables (the first of seven) and which asset is the collateral.</summary>
    const string Meta = "\"marginTables\":[[50,{\"description\":\"\",\"marginTiers\":[{\"lowerBound\":\"0.0\",\"maxLeverage\":50}]}]],\"collateralToken\":0";

    /// <summary>The vendor's answer: <c>[{universe, marginTables, collateralToken}, contexts]</c>, the two lists as given.</summary>
    static string Answer(IEnumerable<string> universe, IEnumerable<string> contexts) =>
        "[{\"universe\":[" + string.Join(",", universe) + "]," + Meta + "},[" + string.Join(",", contexts) + "]]";

    static string Answer(params Coin[] coins) => Answer(coins.Select(c => c.Universe), coins.Select(c => c.Context));

    /// <summary>The answer with one coin's context replaced.</summary>
    static string With(Coin coin, string context) => Answer(Measured.Select(c => c == coin ? c with { Context = context } : c).ToArray());

    /// <summary>A premium-index answer in Binance's shape, for the market row asked beside it.</summary>
    static string PremiumAnswer(DateTimeOffset at) => "[" + string.Join(",", TapeSourceCatalog.Universe.Select(s =>
        $$"""{"symbol":"{{s}}","markPrice":"85495.82186232","indexPrice":"85534.78173913","lastFundingRate":"0.00001937","time":{{at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}}}""")) + "]";

    /// <summary>A body made exactly <paramref name="bytes"/> long with trailing spaces, which JSON reads past.</summary>
    static string Padded(string body, int bytes) => body + new string(' ', bytes - Encoding.UTF8.GetByteCount(body));

    /// <summary>
    /// THE LISTENER'S OWN <c>Date</c>, AS THE ANSWER CARRIED IT: a whole second — the header names no less — no earlier than
    /// two seconds before <paramref name="before"/>'s (the header is cut to the second, and http.sys may hand out the same
    /// second's text for a moment) and no later than <paramref name="after"/>. It is this machine's clock as the LISTENER read
    /// it while answering — the collector's own clock, injected, is somewhere else entirely in (b).
    /// </summary>
    static void AssertTheAnswersDate(DateTimeOffset stamp, DateTimeOffset before, DateTimeOffset after)
    {
        Assert.Equal(TimeSpan.Zero, stamp.Offset);
        Assert.True(stamp.UtcTicks % TimeSpan.TicksPerSecond == 0, $"source time {stamp:O} is not a whole second, as a Date header is");
        Assert.True(stamp >= before.AddSeconds(-2) && stamp <= after,
            $"source time {stamp:O} is not the Date of an answer sent between {before:O} and {after:O}");
    }

    static long Count(string file, string sql)
    {
        using var raw = new SqliteConnection($"Data Source={file};Pooling=False");
        raw.Open();
        using var c = raw.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// (a) ONE ANSWER BECOMES ONE ROW PER KEPT COIN, STAMPED WITH ITS <c>Date</c>. The answer names eight coins — the six, a
    /// delisted MATIC and a seventh, ATOM — and the tape keeps six rows by Hyperliquid's own names, each the coin's universe
    /// entry and its context merged whole, decimals as served; all six carry the one source time the answer's <c>Date</c>
    /// said, and arrive with their fetch; from a loopback listener they are <c>O-ARCH</c>. <c>data-list</c>'s reader names
    /// the series under the owner's "Record market context" switch with its six coins.
    /// </summary>
    [Fact]
    public async Task A_hyperliquid_answer_becomes_one_row_per_coin_stamped_with_its_Date()
    {
        using var host = new FakeArchive();
        host.PublishAt(PathOf(Row()), Answer(Measured));

        var file = NewFile();
        using var store = new TapeStore(file);
        await using var collector = Collector(store, host, () => DateTimeOffset.UtcNow);

        var before = DateTimeOffset.UtcNow;
        var tick = await collector.CollectOnceAsync(Assert.Single(collector.Rows));
        var after = DateTimeOffset.UtcNow;
        log.WriteLine($"{tick} between {before:O} and {after:O}");
        Assert.Equal((1, 1, 6, true), (tick.Attempts, tick.Delivered, tick.Stored, tick.Worked));

        var fetch = Assert.Single(store.Fetches(Hl));
        Assert.Equal((Series, host.BaseUrl + "/info", 200, 6, (string?)null), (fetch.Series, fetch.Url, fetch.HttpStatus, fetch.Items, fetch.Note));
        Assert.Equal(64, fetch.BodySha256!.Length);

        var rows = store.ObservationsOf(fetch.Id);
        foreach (var r in rows) log.WriteLine($"{r.Subject,-4} {r.SourceTime:O} {r.NaturalKey} {r.EvidenceClass} {r.Payload}");

        // SIX ROWS, BY HYPERLIQUID'S OWN NAMES: the delisted coin and the seventh are not this installation's to record.
        Assert.Equal(TapeSourceCatalog.HyperliquidCoins.Order(StringComparer.Ordinal), rows.Select(r => r.Subject).Order(StringComparer.Ordinal));
        Assert.Equal(6, Count(file, "SELECT COUNT(*) FROM tape_obs"));

        // EACH THE COIN'S UNIVERSE ENTRY AND ITS CONTEXT, MERGED WHOLE, every decimal string as served.
        foreach (var coin in Six)
            Assert.Equal(coin.Merged, rows.Single(r => r.Subject == coin.Name).Payload);
        Assert.Contains("\"openInterest\":\"39939.21482\"", rows.Single(r => r.Subject == "BTC").Payload, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"XRP\"", rows.Single(r => r.Subject == "XRP").Payload, StringComparison.Ordinal);

        // ONE SOURCE TIME FOR ALL SIX, THE ANSWER'S Date — and the natural key the coin and that second.
        var stamp = Assert.Single(rows.Select(r => r.SourceTime).Distinct());
        AssertTheAnswersDate(stamp, before, after);
        Assert.All(rows, r =>
        {
            Assert.Equal(TapeStore.NaturalKey(r.Subject, stamp), r.NaturalKey);
            Assert.Equal(fetch.ReceivedAt, r.ReceivedAt);
            Assert.Equal((1, TapeClass.Arch), (r.Revision, r.EvidenceClass));
        });

        // DATA-LIST NAMES THE SERIES WITH ITS COINS, under the switch that records it.
        var listed = new TapeReader(file).Series().Single(s => s.Source == Hl && s.Series == Series);
        log.WriteLine($"data-list: {listed}");
        Assert.Equal((6L, TapeReader.MarketContextSwitch, (string?)null), (listed.Rows, listed.Switch, listed.LastError));
        Assert.Equal(TapeSourceCatalog.HyperliquidCoins.Order(StringComparer.Ordinal), listed.Subjects!);
    }

    /// <summary>
    /// (b) THE SOURCE TIME IS THE ANSWER'S <c>Date</c> AND NEVER THIS MACHINE'S CLOCK. The collector's clock is injected
    /// years away; the rows' source time is still the listener's own <c>Date</c>, within seconds of real time, while the
    /// arrival is that clock's — the one thing it says. Recorded from a loopback listener, every row is <c>O-ARCH</c>.
    /// </summary>
    [Fact]
    public async Task The_source_time_is_the_answers_Date_and_never_this_machines_clock()
    {
        using var host = new FakeArchive();
        host.PublishAt(PathOf(Row()), Answer(Measured));

        var clock = new DateTimeOffset(2019, 3, 4, 5, 6, 7, TimeSpan.Zero);
        using var store = new TapeStore(NewFile());
        await using var collector = Collector(store, host, () => clock);

        var before = DateTimeOffset.UtcNow;
        await collector.CollectOnceAsync(Assert.Single(collector.Rows));
        var after = DateTimeOffset.UtcNow;

        var fetch = Assert.Single(store.Fetches(Hl));
        Assert.Equal((clock, clock), (fetch.RequestedAt, fetch.ReceivedAt));

        var rows = store.ObservationsOf(fetch.Id);
        foreach (var r in rows) log.WriteLine($"{r.Subject,-4} source {r.SourceTime:O} received {r.ReceivedAt:O} {r.EvidenceClass}");
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r =>
        {
            AssertTheAnswersDate(r.SourceTime, before, after);
            Assert.Equal(clock, r.ReceivedAt);
            Assert.Equal(TapeClass.Arch, r.EvidenceClass);
        });
    }

    /// <summary>
    /// (c) AN ANSWER THAT DOES NOT ZIP IS REFUSED WHOLE, IN WORDS, AND STORES NOTHING BUT ITS ATTEMPT — at the parser, every
    /// refusal it has, and over the wire every one a listener can serve: an attempt with the status the host sent, this
    /// build's hash of what it read, the reason, and no row. A coin's context filed under the wrong name is the hazard: an
    /// answer whose two lists may be off by one is not read for the coins that happen to line up.
    /// </summary>
    [Fact]
    public async Task An_answer_that_does_not_zip_is_refused_whole()
    {
        var row = Row();
        var date = new DateTimeOffset(2026, 10, 8, 22, 13, 12, TimeSpan.Zero);

        // ONE COIN AT THE BOUND: 2 KB once merged and canonical is kept; one byte more refuses the whole answer.
        string Grown(int bytes) => Btc.Context[..^1] + ",\"note\":\"" + new string('x', bytes - Encoding.UTF8.GetByteCount(Btc.Merged) - 10) + "\"}";
        Assert.Equal(TapeParse.MaxAssetContextBytes, Encoding.UTF8.GetByteCount((Btc with { Context = Grown(2048) }).Merged));
        Assert.True(TapeParse.TryReadAssetContexts(With(Btc, Grown(2048)), date, row, row.Series[0], out var atBound, out var none), none);
        Assert.Equal(6, atBound.Count);

        var named = Btc.Universe;
        (string? Body, string Says)[] refusals =
        [
            (null, "the body was empty"),
            ("   ", "the body was empty"),
            ("<html><body>an afternoon</body></html>", "the body is not JSON"),
            ("""{"universe":[]}""", "the answer is not the two-element list of a universe and its contexts"),
            ("[{\"universe\":[" + named + "]}]", "the answer is not the two-element list of a universe and its contexts"),
            (Answer(Measured)[..^1] + ",[]]", "the answer is not the two-element list of a universe and its contexts"),
            ("[{" + Meta + "},[]]", "the answer holds no 'universe' list"),
            ("[[],[]]", "the answer holds no 'universe' list"),
            ("[{\"universe\":{}},[]]", "the answer holds no 'universe' list"),
            ("[{\"universe\":[" + named + "]},{\"0\":" + Btc.Context + "}]", "the answer holds no list of contexts beside its universe"),
            // BTC's entry gone from the universe and not from the contexts: zipped regardless, BTC's open interest would be ETH's.
            (Answer(Measured.Skip(1).Select(c => c.Universe), Measured.Select(c => c.Context)),
                "the answer's universe names 7 assets and holds 8 contexts, so the two lists do not zip"),
            (Answer(Measured.Select(c => c.Universe), Measured.Skip(1).Select(c => c.Context)),
                "the answer's universe names 8 assets and holds 7 contexts, so the two lists do not zip"),
            (Answer(Measured.Select(c => c.Universe).Append(Atom.Universe), Measured.Select(c => c.Context)),
                "the answer's universe names 9 assets and holds 8 contexts, so the two lists do not zip"),
            (Answer(Measured.Select(c => c == Matic ? "\"MATIC\"" : c.Universe), Measured.Select(c => c.Context)),
                "entry 3 of the universe or of the contexts is not an object, so the two lists do not zip"),
            (Answer(Measured.Select(c => c.Universe), Measured.Select(c => c == Eth ? "null" : c.Context)),
                "entry 1 of the universe or of the contexts is not an object, so the two lists do not zip"),
            (Answer(Measured.Select(c => c == Atom ? """{"szDecimals":2,"maxLeverage":5}""" : c.Universe), Measured.Select(c => c.Context)),
                "universe entry 2 has no 'name' naming its asset, so the two lists cannot be zipped by name"),
            (Answer(Measured.Select(c => c == Atom ? """{"szDecimals":2,"name":7}""" : c.Universe), Measured.Select(c => c.Context)),
                "universe entry 2 has no 'name' naming its asset, so the two lists cannot be zipped by name"),
            (With(Btc, Btc.Context[..^1] + ",\"name\":\"ETH\"}"),
                "BTC's universe entry and its context both name 'name', so the two cannot be kept as one item"),
            (With(Sol, Sol.Context[..^1] + ",\"maxLeverage\":20}"),
                "SOL's universe entry and its context both name 'maxLeverage', so the two cannot be kept as one item"),
            (Answer([Btc, Eth, Sol, Btc]), "the answer names BTC twice, and the tape cannot say which of the two contexts is BTC's"),
            (With(Doge, Doge.Context[..^1] + ",\"funding\":\"0.0\"}"), "DOGE's context cannot be kept as one payload"),
            (With(Btc, Grown(2049)), "BTC's context is 2049 bytes, and one coin's context here is at most 2048"),
            (Answer(Atom, Matic), "the answer names none of BTC, ETH, SOL, BNB, XRP, DOGE"),
            (Answer(), "the answer names none of BTC, ETH, SOL, BNB, XRP, DOGE")
        ];

        // AT THE PARSER: each refused whole, no item; and a good answer with no Date is refused the same way.
        foreach (var (body, says) in refusals)
        {
            var read = TapeParse.TryReadAssetContexts(body, date, row, row.Series[0], out var items, out var why);
            log.WriteLine($"{says,-70} <- {why}");
            Assert.False(read, says);
            Assert.Empty(items);
            Assert.Contains(says, why!, StringComparison.Ordinal);
        }

        Assert.False(TapeParse.TryReadAssetContexts(Answer(Measured), null, row, row.Series[0], out var undated, out var noDate));
        Assert.Empty(undated);
        Assert.Equal("the answer carried no Date header, and its contexts carry no time of their own", noDate);

        // OVER THE WIRE: every refusal a listener can serve is an attempt with its status, its hash and its reason — and no row.
        using var host = new FakeArchive();
        var file = NewFile();
        using var store = new TapeStore(file);
        await using var collector = Collector(store, host, () => date);

        foreach (var (body, says) in refusals.Where(r => !string.IsNullOrWhiteSpace(r.Body)))
        {
            host.PublishAt(PathOf(row), body!);
            var tick = await collector.CollectOnceAsync(row);
            var fetch = store.Fetches(Hl)[0];
            log.WriteLine($"{fetch.HttpStatus} {fetch.Note}");
            Assert.Equal((1, 0, 0, false), (tick.Attempts, tick.Delivered, tick.Stored, tick.Worked));
            Assert.Equal((200, 0), (fetch.HttpStatus ?? 0, fetch.Items));
            Assert.Equal(64, fetch.BodySha256!.Length);
            Assert.StartsWith("the answer could not be read: ", fetch.Note!, StringComparison.Ordinal);
            Assert.Contains(says, fetch.Note!, StringComparison.Ordinal);
            Assert.Contains(says, collector.LastError(Hl)!, StringComparison.Ordinal);
            Assert.Empty(store.ObservationsOf(fetch.Id));
        }

        Assert.Equal(refusals.Count(r => !string.IsNullOrWhiteSpace(r.Body)), store.Fetches(Hl, limit: 1000).Count);
        Assert.Equal(0, Count(file, "SELECT COUNT(*) FROM tape_obs"));
    }

    /// <summary>
    /// (d) THE CONTEXTS ARE ASKED BY POST WITH THE ROW'S OWN BODY AND NO KEY. The one request is a POST of
    /// <c>{"type":"metaAndAssetCtxs"}</c> as <c>application/json</c>, and nothing else is added: its headers are exactly the
    /// headers of the GET this collector sends Binance's premium index, plus the two a body needs — no key, no account, no
    /// header of this build's own. The GET stays a GET with nothing in it.
    /// </summary>
    [Fact]
    public async Task The_contexts_are_asked_by_post_with_the_rows_body_and_no_key()
    {
        var row = Row();
        var premium = Premium();
        using var host = new FakeArchive();
        host.PublishAt(PathOf(row), Answer(Measured));
        host.PublishAt(PathOf(premium), PremiumAnswer(DateTimeOffset.UtcNow));

        using var store = new TapeStore(NewFile());
        await using var collector = Collector(store, host, () => DateTimeOffset.UtcNow, row, premium);
        foreach (var r in collector.Rows) log.WriteLine($"{r.Id}: {await collector.CollectOnceAsync(r)}");

        var asked = host.Requests;
        foreach (var r in asked) log.WriteLine($"{r.Method} {r.PathAndQuery} [{r.ContentType}] '{r.Body}' headers: {string.Join(", ", r.Headers)}");
        Assert.Equal(2, asked.Count);
        var post = Assert.Single(asked, r => r.PathAndQuery == PathOf(row));
        var get = Assert.Single(asked, r => r.PathAndQuery == PathOf(premium));

        // ONE POST OF THE ROW'S OWN BODY, AS application/json.
        Assert.Equal(("POST", "/info", "application/json", """{"type":"metaAndAssetCtxs"}"""), (post.Method, post.PathAndQuery, post.ContentType, post.Body));
        Assert.Equal(row.Series[0].Body, post.Body);

        // AND NOTHING ELSE ADDED: the GET's headers and the two a body needs, no more — none of them a key.
        static IEnumerable<string> Names(IEnumerable<string> names) => names.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal);
        Assert.Equal(Names(get.Headers.Concat(new[] { "Content-Type", "Content-Length" })), Names(post.Headers));
        Assert.All(post.Headers, h =>
        {
            foreach (var credential in new[] { "auth", "key", "cookie" })
                Assert.DoesNotContain(credential, h, StringComparison.OrdinalIgnoreCase);
        });

        // THE GET STAYS A GET, with no body and no content type.
        Assert.Equal(("GET", (string?)null, ""), (get.Method, get.ContentType, get.Body));

        // AND BOTH DELIVERED.
        Assert.All(store.Fetches(limit: 10), f => Assert.Null(f.Note));
        Assert.Equal(2, store.Fetches(limit: 10).Count);
    }

    /// <summary>
    /// (e) A FILE ROW CAN NEITHER POST NOR NAME THE FAMILY. In <c>tape-sources.json</c>, a row naming the asset-context parser
    /// is refused in words; a row whose series names a <c>body</c> (and whose row names <c>subjects</c>) is read as a market row
    /// like any other with neither — and over the wire it is asked by GET, with no body and no content type, and keeps the
    /// universe's symbols and nothing it named.
    /// </summary>
    [Fact]
    public async Task A_file_row_can_neither_post_nor_name_the_family()
    {
        var path = Path.Combine(TestEnv.Home, $"tape-sources-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, """
            [
              { "id": "my-contexts", "base_url": "http://127.0.0.1:9", "cadence_seconds": 300, "parser": "hyperliquid-ctx-json",
                "series": [ { "id": "asset-ctxs", "url_shape": "{base}/info", "time_field": "time", "symbol_field": "name" } ] },
              { "id": "my-posted", "base_url": "http://127.0.0.1:9", "cadence_seconds": 300, "subjects": ["BTC"],
                "series": [ { "id": "s", "url_shape": "{base}/info", "time_field": "time", "symbol_field": "name",
                              "body": "{\"type\":\"metaAndAssetCtxs\"}" } ] }
            ]
            """);

        var read = TapeSourceCatalog.Read(path);
        var refused = Assert.Single(read.Refused);
        log.WriteLine("refused: " + refused);
        Assert.StartsWith("'my-contexts' names the parser 'hyperliquid-ctx-json', which only TradeAgent's built-in Hyperliquid row uses",
            refused, StringComparison.Ordinal);
        Assert.Contains("may neither send a body nor take its rows' time from a host's header", refused, StringComparison.Ordinal);
        Assert.DoesNotContain(read.Sources, r => r.Id == "my-contexts");

        var added = read.Sources.Single(r => r.Id == "my-posted");
        Assert.Equal((TapeSourceCatalog.JsonParser, "", 0), (added.Parser, added.Series[0].Body, added.Subjects.Count));
        Assert.Null(TapeSourceCatalog.BuiltInLiveRule(added.Id));

        // OVER THE WIRE: a GET with nothing in it, kept to the universe — BTCUSDT, not the BTC it named.
        using var host = new FakeArchive();
        var at = new DateTimeOffset(2026, 10, 8, 22, 13, 0, TimeSpan.Zero);
        var ms = at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        host.PublishAt("/info", $$"""[{"name":"BTCUSDT","openInterest":"1.5","time":{{ms}}},{"name":"BTC","openInterest":"2.5","time":{{ms}}}]""");

        using var store = new TapeStore(NewFile());
        await using var collector = Collector(store, host, () => at.AddSeconds(2), added);
        var tick = await collector.CollectOnceAsync(added);
        log.WriteLine($"{tick}");

        var asked = Assert.Single(host.Requests);
        log.WriteLine($"{asked.Method} {asked.PathAndQuery} [{asked.ContentType}] '{asked.Body}' headers: {string.Join(", ", asked.Headers)}");
        Assert.Equal(("GET", "/info", (string?)null, ""), (asked.Method, asked.PathAndQuery, asked.ContentType, asked.Body));
        Assert.DoesNotContain(asked.Headers, h => h.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));

        var fetch = Assert.Single(store.Fetches("my-posted"));
        var kept = Assert.Single(store.ObservationsOf(fetch.Id));
        Assert.Equal(("BTCUSDT", at, TapeClass.Arch), (kept.Subject, kept.SourceTime, kept.EvidenceClass));
    }

    /// <summary>
    /// (f) A HYPERLIQUID READING ON TIME FROM ITS OWN ORIGIN IS LIVE, by the store's one rule and this build's row: received
    /// within the 300 s cadence plus 30 s of its source time — the answer's <c>Date</c> — from Hyperliquid's own API
    /// origin is <c>O-LIVE</c>; one second more, or the same reading from any other origin, is <c>O-ARCH</c>. Early by
    /// the cadence plus 30 s is still live, a clock a little behind the vendor's being no reason to call a reading late.
    /// </summary>
    [Fact]
    public void A_hyperliquid_reading_on_time_from_its_own_origin_is_live()
    {
        using var store = new TapeStore(NewFile());
        var date = new DateTimeOffset(2026, 10, 8, 10, 38, 39, TimeSpan.Zero);
        var own = TapeSourceCatalog.HyperliquidBaseUrl + "/info";

        string ClassOf(string url, int seconds, string coin)
        {
            var at = date.AddSeconds(seconds);
            var append = store.Append(new TapeFetch
            {
                Source = Hl, Series = Series, Url = url,
                RequestedAt = at, ReceivedAt = at, HttpStatus = 200
            }, [new TapeItem(coin, date, $$"""{"name":"{{coin}}","openInterest":"40203.71654"}""")]);
            var row = Assert.Single(store.ObservationsOf(append.FetchId));
            log.WriteLine($"{coin} {url} {seconds,5} s -> {row.EvidenceClass}");
            return row.EvidenceClass;
        }

        Assert.Equal(TapeClass.Live, ClassOf(own, 1, "BTC"));
        Assert.Equal(TapeClass.Live, ClassOf(own, 330, "ETH"));
        Assert.Equal(TapeClass.Arch, ClassOf(own, 331, "SOL"));
        Assert.Equal(TapeClass.Live, ClassOf(own, -330, "BNB"));
        Assert.Equal(TapeClass.Arch, ClassOf(own, -331, "XRP"));
        Assert.Equal(TapeClass.Arch, ClassOf("http://127.0.0.1:9/info", 1, "DOGE"));
    }

    /// <summary>
    /// (g) AN OVERSIZE ANSWER IS REFUSED UNREAD. Hyperliquid's answer is bounded on its own at 512 KB, about seven times
    /// what it measured: one byte past, declared, is an attempt with the status, the reason and NO hash — nothing was read to
    /// hash — and no row; at the bound exactly it is read. The bound is this family's own: Binance's premium index answering
    /// the same oversize body is read under the market rows' 4 MB.
    /// </summary>
    [Fact]
    public async Task An_oversize_answer_is_refused_unread()
    {
        Assert.Equal(512 * 1024, TapeCollector.MaxAssetContextAnswerBytes);
        const int past = TapeCollector.MaxAssetContextAnswerBytes + 1;

        var row = Row();
        var premium = Premium();
        using var host = new FakeArchive();
        using var store = new TapeStore(NewFile());
        await using var collector = Collector(store, host, () => DateTimeOffset.UtcNow, row, premium);

        // ONE BYTE PAST THE BOUND, DECLARED: refused unread.
        host.PublishAt(PathOf(row), Padded(Answer(Measured), past));
        var tick = await collector.CollectOnceAsync(row);
        var refused = store.Fetches(Hl)[0];
        log.WriteLine($"{tick}: {refused.HttpStatus} {refused.Note}");
        Assert.Equal((1, 0, false), (tick.Attempts, tick.Delivered, tick.Worked));
        Assert.Equal((200, 0, (string?)null), (refused.HttpStatus ?? 0, refused.Items, refused.BodySha256));
        Assert.Equal($"the answer declared {past} bytes, past the {TapeCollector.MaxAssetContextAnswerBytes} this source's answer may be", refused.Note);
        Assert.Empty(store.ObservationsOf(refused.Id));

        // AT THE BOUND EXACTLY: read whole, six rows.
        host.PublishAt(PathOf(row), Padded(Answer(Measured), TapeCollector.MaxAssetContextAnswerBytes));
        var atBound = await collector.CollectOnceAsync(row);
        var read = store.Fetches(Hl)[0];
        log.WriteLine($"{atBound}: {read.HttpStatus} {read.Note}");
        Assert.Equal((1, 1, 6), (atBound.Attempts, atBound.Delivered, atBound.Stored));
        Assert.Null(read.Note);

        // THE BOUND IS THIS FAMILY'S OWN: the same oversize length from Binance's premium index is read, under its 4 MB.
        host.PublishAt(PathOf(premium), Padded(PremiumAnswer(DateTimeOffset.UtcNow), past));
        var binance = await collector.CollectOnceAsync(premium);
        log.WriteLine($"{binance}: {store.Fetches(TapeSourceCatalog.Premium)[0].Note}");
        Assert.Equal((1, 1, 6), (binance.Attempts, binance.Delivered, binance.Stored));
        Assert.True(TapeCollector.MaxBodyBytes > past);
    }

    /// <summary>
    /// (h) A FEATURE READS HYPERLIQUID'S OPEN INTEREST AND REFUSES BTCUSDT THERE. A spec over the row's own coin parses; one
    /// naming Binance's symbol on Hyperliquid's row — or Hyperliquid's coin on Binance's — is refused naming what that row
    /// records; the coin's name is its symbol and not a value. Over a real tape — the answer through this build's parser,
    /// written by the store as from Hyperliquid's own address a second after its <c>Date</c> — the feature reads BTC's open
    /// interest as served, <c>O-LIVE</c>, and carries the row's terms; nothing calls these rows text.
    /// </summary>
    [Fact]
    public void A_feature_reads_hyperliquid_open_interest_and_refuses_BTCUSDT_there()
    {
        static string Spec(string source, string series, string subject, string field) =>
            $$"""{"kind":"latest","input":{"source":"{{source}}","series":"{{series}}","subject":"{{subject}}","field":"{{field}}"},"latency_s":5,"max_age_s":600}""";

        var parse = FeatureSpec.Parse(Spec(Hl, Series, "BTC", "openInterest"));
        Assert.True(parse.Ok, parse.Why);

        var wrong = FeatureSpec.Parse(Spec(Hl, Series, "BTCUSDT", "openInterest"));
        var crossed = FeatureSpec.Parse(Spec(TapeSourceCatalog.Premium, "premium-index", "BTC", "markPrice"));
        var symbol = FeatureSpec.Parse(Spec(Hl, Series, "BTC", "name"));
        foreach (var no in new[] { wrong, crossed, symbol }) log.WriteLine(no.Why);
        Assert.Equal((false, false, false), (wrong.Ok, crossed.Ok, symbol.Ok));
        Assert.Contains($"the tape records {Hl} for BTC, ETH, SOL, BNB, XRP, DOGE, and 'BTCUSDT' is not one of them", wrong.Why, StringComparison.Ordinal);
        Assert.Contains("for BTCUSDT, ETHUSDT, SOLUSDT, BNBUSDT, XRPUSDT, DOGEUSDT, and 'BTC' is not one of them", crossed.Why, StringComparison.Ordinal);
        Assert.Contains("'name' is the symbol of asset-ctxs, and not a value a feature reads", symbol.Why, StringComparison.Ordinal);
        Assert.DoesNotContain("text", wrong.Why + crossed.Why, StringComparison.Ordinal);

        // OVER A REAL TAPE: the answer through this build's parser, written as from Hyperliquid's own address on time.
        var row = Row();
        var date = new DateTimeOffset(2026, 10, 8, 22, 13, 12, TimeSpan.Zero);
        Assert.True(TapeParse.TryReadAssetContexts(Answer(Measured), date, row, row.Series[0], out var items, out var why), why);

        var file = NewFile();
        using (var store = new TapeStore(file))
            store.Append(new TapeFetch
            {
                Source = Hl, Series = Series, Url = TapeSourceCatalog.HyperliquidBaseUrl + "/info",
                RequestedAt = date.AddMilliseconds(-500), ReceivedAt = date.AddSeconds(1), HttpStatus = 200
            }, items);

        var at = date.AddSeconds(30);
        var holdout = TapeHoldout.Pipe(CouncilRoles.Research, new DatasetStore(_ledger));
        var series = FeatureSeries.Read(new TapeReader(file), holdout, parse.Spec!, at, at, TimeSpan.FromMinutes(1));
        Assert.Null(series.Refusal);
        var point = Assert.Single(series.Points);
        log.WriteLine($"{point}");
        Assert.Equal((39939.21482m, TapeClass.Live, 1), (point.Value, point.Class, point.Rows));

        var terms = Assert.Single(series.Terms);
        Assert.Equal((Hl, Series, "BTC", TapeSourceCatalog.HyperliquidTermsUrl, ""), (terms.Source, terms.Series, terms.Subject, terms.TermsUrl, terms.Citation));
        Assert.Contains("§ 3.1.8", terms.Terms, StringComparison.Ordinal);
    }
}
