using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// THE FORWARD BARS AS AN AGENT ACTUALLY REACHES THEM: over the pipe, through the gateway, on rows
/// the forward ledger really holds.
///
/// <para>The unit tests hold the store's keys and the collector's arithmetic. This holds the two
/// things only the wire settles. First, that <c>data-bars --source forward</c> serves a bounded
/// window to a caller whatever role it proved and WITHOUT a holdout refusal — no holdout applies to
/// a bar that post-dates every freeze on the installation. Second, and this is the one that matters,
/// that the archive's cutoff is untouched by any of it: the SAME database, the SAME window and the
/// SAME caller is still refused the held-back months through <c>--source archive</c>, so the new
/// door is a door onto different evidence and not a way round the old one.</para>
/// </summary>
public class ForwardBarsOverPipeTests(ITestOutputHelper log)
{
    const string Pair = "BTCUSDT";

    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Where the archive dataset's holdout is drawn. Half way, so both halves exist.</summary>
    const int HoldoutAtBar = 60;

    static string NewPipe() => "ta-fwd-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, Database Db, Raw Client, IAsyncDisposable Server)> Connected(
        string? role = CouncilRoles.Research)
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Raw.ConnectAsync(pipe);
        var grant = role is null ? null : grants.Issue(role, "attempt-fwd").Token;
        var hello = await client.SendAsync(new IpcRequest { Op = Ops.Hello, Token = IpcToken.Ensure(), Grant = grant });
        Assert.True(hello.Ok, Json.Write(hello.Error));

        return (gw, db, client, server);
    }

    /// <summary>The wire by hand: the machine token on every frame, and the grant on the hello.</summary>
    sealed class Raw : IAsyncDisposable
    {
        NamedPipeClientStream _pipe = null!;
        StreamReader _r = null!;
        StreamWriter _w = null!;

        public static async Task<Raw> ConnectAsync(string pipeName)
        {
            var c = new Raw { _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous) };
            await c._pipe.ConnectAsync(5000);
            c._r = new StreamReader(c._pipe, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            c._w = new StreamWriter(c._pipe, new UTF8Encoding(false), 8192, leaveOpen: true) { AutoFlush = true };
            return c;
        }

        public async Task<IpcResponse> SendAsync(IpcRequest req)
        {
            req.Token ??= IpcToken.Ensure();
            await _w.WriteLineAsync(Json.Write(req));
            var line = await _r.ReadLineAsync() ?? throw new IOException("the gateway closed the connection");
            return Json.Read<IpcResponse>(line) ?? throw new IOException("unreadable reply");
        }

        public ValueTask DisposeAsync()
        {
            _r.Dispose();
            _w.Dispose();
            return _pipe.DisposeAsync();
        }
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    /// <summary>
    /// FORWARD BARS AS THE APP'S OWN COLLECTOR WOULD HAVE WRITTEN THEM — through the store, in one
    /// transaction, with the closure rule applied. <paramref name="skipAfter"/> leaves a one-minute
    /// hole so the reply's gap counts are about a real absence.
    /// </summary>
    static void GivenForward(Database db, int bars, int skipAfter = -1)
    {
        var store = new ForwardBarStore(db);
        var readAt = Start.AddMinutes(bars + 1);

        var klines = new List<ForwardBars.Kline>();
        for (var i = 0; i < bars; i++)
        {
            if (i == skipAfter + 1) continue;
            var close = 96m + i % 10;
            klines.Add(new ForwardBars.Kline(Start.AddMinutes(i), close, close + 1m, close - 1m, close,
                1.5m, Start.AddMinutes(i + 1).AddMilliseconds(-1)));
        }

        store.Append(new ForwardFetchAttempt
        {
            Source = ForwardBars.Source,
            Symbol = Pair,
            Url = $"http://127.0.0.1:0/api/v3/klines?symbol={Pair}&interval=1m",
            RequestedAt = readAt.AddSeconds(-1),
            ReceivedAt = readAt,
            HttpStatus = 200,
            BodySha256 = new string('c', 64)
        }, klines);
    }

    /// <summary>
    /// AN ARCHIVE DATASET WITH A REAL HOLDOUT OVER THE SAME MINUTES, so the two doors can be asked
    /// the same question and only one of them may answer.
    /// </summary>
    static void GivenArchiveWithHoldout(Database db, int bars = 120)
    {
        var dir = BinanceArchive.DatasetDir(Pair);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{Pair}-{Guid.NewGuid():n}.zip");
        File.WriteAllText(raw, "a stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        for (var i = 0; i < bars; i++)
        {
            var close = 96m + i % 10;
            text.Append(CultureInfo.InvariantCulture,
                $"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},{close},1.00\n");
        }
        File.WriteAllText(csv, text.ToString());

        var store = new DatasetStore(db);
        var id = store.Record(new DatasetRecord(
            0, BinanceArchive.Source, Pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, bars, Start, Start.AddMinutes(bars - 1), 0, [], false,
            0, 0, 0, Start, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "https://127.0.0.1/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, Start, KlineTimeUnit.Microseconds, raw)]));

        var done = store.SetHoldout(id, Start.AddMinutes(HoldoutAtBar), EvaluationClass.Research);
        Assert.True(done.Ok, done.Why);
    }

    /// <summary>
    /// THE HEADLINE. The same caller, the same window, the same database: the FORWARD bars come back
    /// and the ARCHIVE's held-back months do not.
    /// </summary>
    [Theory]
    [InlineData(CouncilRoles.Research)]
    [InlineData(CouncilRoles.Operations)]
    [InlineData(null)]
    public async Task Data_bars_serves_forward_bars_to_a_role_bounded_and_without_a_holdout_refusal(string? role)
    {
        var (gw, db, client, server) = await Connected(role);
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        GivenForward(db, 120, skipAfter: 9);
        GivenArchiveWithHoldout(db);

        // THE WINDOW REACHES PAST THE ARCHIVE'S CUTOFF ON PURPOSE. Through the archive it is the
        // refusal; through the forward door it is simply the bars.
        var window = Args(("pair", Pair), ("from", Iso(Start)), ("to", Iso(Start.AddMinutes(119))));

        var forward = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-fwd",
            Args = new Dictionary<string, JsonElement>(window) { ["source"] = JsonSerializer.SerializeToElement("forward") }
        });

        Assert.True(forward.Ok, Json.Write(forward.Error));
        var reply = Data(forward);
        log.WriteLine(reply.ToString());

        Assert.Equal(119, reply.GetProperty("count").GetInt32());
        Assert.Equal(ForwardBars.Source, reply.GetProperty("source").GetString());

        // WHAT THESE BARS ARE NOT, IN THE ANSWER AND NOT ONLY IN A SCHEMA.
        var note = reply.GetProperty("note").GetString()!;
        Assert.Contains("collected by TradeAgent", note, StringComparison.Ordinal);
        Assert.Contains("no vendor checksum", note, StringComparison.Ordinal);
        Assert.Contains("not evaluation evidence", note, StringComparison.Ordinal);

        // THE HOLE IS REPORTED AND IS NOT FILLED IN.
        Assert.Equal(1, reply.GetProperty("gaps_in_series").GetInt32());
        Assert.Equal(1, reply.GetProperty("bars_missing").GetInt32());

        // AND EVERY BAR CARRIES THE INSTANT IT WAS RECEIVED, WHICH IS AFTER ITS OWN CLOSE — the
        // closure claim, checkable by the caller rather than promised to it.
        foreach (var bar in reply.GetProperty("bars").EnumerateArray())
            Assert.True(bar.GetProperty("close_time").GetDateTimeOffset()
                        < bar.GetProperty("received_at").GetDateTimeOffset());

        // THE ARCHIVE'S CUTOFF IS UNTOUCHED, for this very caller, on this very window.
        var archive = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-fwd", Args = window
        });

        log.WriteLine(Json.Write(archive.Error));
        Assert.False(archive.Ok);
        Assert.Equal(nameof(ErrorCode.HOLDOUT_WITHHELD), archive.Error!.Code);
    }

    /// <summary>
    /// BOUNDED, AND THE REFUSAL NAMES THE CAP. An answer quietly cut short is a different window from
    /// the one that was asked for, and nothing in the reply would say so.
    /// </summary>
    [Fact]
    public async Task A_forward_window_bigger_than_the_cap_is_refused_and_the_refusal_names_the_cap()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        GivenForward(db, DatasetReader.MaxBars + 50);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-fwd",
            Args = Args(("pair", Pair), ("source", "forward"))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("10000", reply.Error.Message, StringComparison.Ordinal);
        Assert.Contains("--from", reply.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>data-list</c> NAMES THE FORWARD SERIES AND KEEPS IT APART FROM THE DATASETS. A caller that
    /// could not tell the two lists apart would be one bar count away from asking for a verdict over
    /// minutes nobody checksummed.
    /// </summary>
    [Fact]
    public async Task Data_list_names_the_forward_series_apart_from_the_frozen_datasets()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        GivenForward(db, 30, skipAfter: 4);
        GivenArchiveWithHoldout(db, 30);

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "agent-fwd" });
        Assert.True(reply.Ok, Json.Write(reply.Error));

        var data = Data(reply);
        log.WriteLine(data.ToString());

        // THE FROZEN DATASET IS WHERE IT ALWAYS WAS, with its holdout.
        var dataset = Assert.Single(data.GetProperty("datasets").EnumerateArray().ToList());
        Assert.Equal(BinanceArchive.Source, dataset.GetProperty("source").GetString());
        Assert.NotEqual(JsonValueKind.Null, dataset.GetProperty("holdout_from").ValueKind);

        // AND THE FORWARD SERIES IS IN ITS OWN LIST, with no holdout field to be misread.
        var series = Assert.Single(data.GetProperty("forward").EnumerateArray().ToList());
        Assert.Equal(ForwardBars.Source, series.GetProperty("source").GetString());
        Assert.Equal(Pair, series.GetProperty("symbol").GetString());
        Assert.Equal(29, series.GetProperty("bars").GetInt32());
        Assert.Equal(1, series.GetProperty("gaps").GetInt32());
        Assert.Equal(1, series.GetProperty("bars_missing").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, series.GetProperty("last_received_at").ValueKind);
        Assert.Contains("not evaluation evidence", series.GetProperty("note").GetString()!,
            StringComparison.Ordinal);
        Assert.False(series.TryGetProperty("holdout_from", out _),
            "a forward series must not carry a holdout field at all");

        // AND THE LIST'S OWN NOTE SAYS WHICH IS WHICH.
        Assert.Contains("DIFFERENT KIND OF THING", data.GetProperty("note").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A SOURCE WORD THIS BUILD DOES NOT SERVE IS REFUSED, never defaulted: a misspelling that
    /// quietly handed back the archive would answer a question nobody asked, and the caller would
    /// have no way to tell which evidence it was reasoning over.
    /// </summary>
    [Fact]
    public async Task An_unknown_source_word_is_refused_and_names_the_two_there_are()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        GivenArchiveWithHoldout(db, 30);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-fwd",
            Args = Args(("pair", Pair), ("source", "forwards"))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("archive", reply.Error.Message, StringComparison.Ordinal);
        Assert.Contains("forward", reply.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE OWNER'S REPORT SAYS SO IN SECTION 7, counted off the rows rather than off "the collector
    /// is running" — and it is a COUNT and never a price: TradeAgent still does not know what this
    /// installation's bandwidth costs, and the section's gap still says so.
    /// </summary>
    [Fact]
    public async Task The_owners_report_counts_the_live_feed_in_section_seven()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        // Nothing collected: the line is present and says so rather than being absent.
        var before = new DailyReports(gw, db).Compose(DateTimeOffset.UtcNow, new DailyReportInputs());
        Assert.Null(before.OtherCosts.ForwardData);
        Assert.Contains("- live market data: not collected", DailyReportText.Render(before),
            StringComparison.Ordinal);

        GivenForward(db, 30, skipAfter: 4);

        var after = new DailyReports(gw, db).Compose(DateTimeOffset.UtcNow, new DailyReportInputs());
        var text = DailyReportText.Render(after);
        log.WriteLine(text[text.IndexOf("## 7.", StringComparison.Ordinal)..]);

        var line = after.OtherCosts.ForwardData!;
        Assert.Contains(ForwardBars.Source, line, StringComparison.Ordinal);
        Assert.Contains("29 bars held", line, StringComparison.Ordinal);
        Assert.Contains("1 gap runs / 1 minutes missing and NOTHING filled in", line, StringComparison.Ordinal);
        Assert.Contains(ForwardBars.Evidence, line, StringComparison.Ordinal);
        Assert.Contains("No price", line, StringComparison.Ordinal);

        // THE GAP THAT SAYS NOTHING HERE IS PRICED IS STILL THERE. Counting one activity is not the
        // same as measuring what running this costs, and the report must not start reading as though
        // it were.
        Assert.Contains(after.OtherCosts.Missing,
            g => g.Field.Contains("market data subscriptions", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE STATUS CARRIES THE FRESHNESS, because it is the fact the agent's next piece of work is
    /// about to be bounded by — and ABSENT never means fresh.
    /// </summary>
    [Fact]
    public async Task Status_carries_the_forward_series_freshness_and_absent_never_means_fresh()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        // Nothing collected at all: the whole record is absent rather than a zero age.
        var before = await client.SendAsync(new IpcRequest { Op = Ops.Status, Session = "agent-fwd" });
        Assert.True(before.Ok, Json.Write(before.Error));
        Assert.False(Data(before).TryGetProperty("forward_data", out _),
            "an installation collecting nothing must not report a forward_data at all");

        GivenForward(db, 30);

        var after = await client.SendAsync(new IpcRequest { Op = Ops.Status, Session = "agent-fwd" });
        Assert.True(after.Ok, Json.Write(after.Error));

        var forward = Data(after).GetProperty("forward_data");
        log.WriteLine(forward.ToString());

        Assert.Equal(Pair, forward.GetProperty("symbol").GetString());
        Assert.Equal(Start.AddMinutes(29), forward.GetProperty("last_bar").GetDateTimeOffset());
        Assert.True(forward.GetProperty("age_seconds").GetInt64() > 0);
        Assert.Equal(0, forward.GetProperty("gaps_today").GetInt32());
        Assert.True(forward.GetProperty("collecting").GetBoolean());
    }
}
