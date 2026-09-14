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
/// ITEM 3 — A VOLUME-LESS CANDLE IS NEVER TRADE EVIDENCE, AND EVERY SURFACE THAT SERVES ONE SAYS SO.
///
/// <para><c>docs/COUNCIL.md</c>:164-172: a candle WITHOUT volume is midpoint-derived and is flagged as
/// such, "never as trade evidence". Red first: a run over a dataset of midpoint-derived candles came
/// back looking exactly like a run over traded bars — same reply, same note, same metrics shape — so
/// the one thing the caller needed to know in order to read the figure was the one thing the answer
/// did not carry.</para>
///
/// <para>Four surfaces, one sentence (<see cref="BarQuality.Note"/>): <c>data-bars</c>, which also
/// puts the quality on every bar it serves; the backtest reply; section 8 of the owner's report, which
/// <c>trade report</c> hands to the AI; and <see cref="BarFeed"/>, which is where a run gets its bars
/// in process.</para>
/// </summary>
public class MidpointEvidenceTests(ITestOutputHelper log)
{
    static readonly DateTimeOffset At = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    const string Symbol = "BTCUSD";
    const string Interval = "5m";

    /// <summary>How many of the fixture's bars arrived with no volume at all.</summary>
    const int Midpoints = 40;

    const int Bars = 120;

    static string NewPipe() => "ta-mid-" + Guid.NewGuid().ToString("n")[..12];

    /// <summary>The wire by hand: the machine token on every frame, and the launch grant on the hello.</summary>
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

    static async Task<(TradingGateway Gw, Database Db, Raw Client, IAsyncDisposable Server)> Connected()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var grants = new AgentGrants();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe) { Grants = grants };
        server.Start();

        var client = await Raw.ConnectAsync(pipe);
        var hello = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Hello, Token = IpcToken.Ensure(),
            Grant = grants.Issue(CouncilRoles.Operations, "attempt-1").Token
        });
        Assert.True(hello.Ok, Json.Write(hello.Error));

        return (gw, db, client, server);
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in pairs) args[key] = JsonSerializer.SerializeToElement(value);
        return args;
    }

    static string GivenProgram()
    {
        var dir = Path.Combine(Paths.RoleHome(CouncilRoles.Operations), "strategies");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "midpoint.strategy"),
            "instrument BTCUSD\nsize fixed 1\nexit when close < 97\nentry when close > 103\n");
        return "strategies/midpoint.strategy";
    }

    /// <summary>
    /// A DATASET WRITTEN THE WAY THE COLLECTOR WRITES ONE FROM A SOURCE THAT MAY PUBLISH A CANDLE WITH
    /// NO VOLUME: the seven-column header, a <c>quality</c> on every row, and a row that records what
    /// the source declared and what the normaliser counted.
    /// </summary>
    static DatasetRecord GivenMidpointData(Database db)
    {
        var dir = Path.Combine(Paths.Data, CandleSourceCatalog.RevolutXCandles, Symbol, Interval);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        var raw = Path.Combine(dir, "raw", $"{Symbol}-{Guid.NewGuid():n}.csv");
        File.WriteAllText(raw, "a stand-in for the vendor's own answer");

        var csv = Path.Combine(dir, $"{Guid.NewGuid():n}.csv");
        var text = new StringBuilder().Append(KlineNormaliser.HeaderWithQuality).Append('\n');
        for (var i = 0; i < Bars; i++)
        {
            var close = 96m + i % 10;
            var midpoint = i < Midpoints;
            text.Append(CultureInfo.InvariantCulture,
                $"{At.AddMinutes(5 * i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},{close},{close + 1m},{close - 1m},"
                + $"{close},{(midpoint ? 0m : 1m)},{(midpoint ? BarQuality.MidpointDerived : BarQuality.Traded)}\n");
        }
        File.WriteAllText(csv, text.ToString());

        var record = new DatasetRecord(
            0, CandleSourceCatalog.RevolutXCandles, Symbol, Interval, "v1", 1, 1, [],
            csv, DatasetStore.Sha256(csv)!, Bars, At, At.AddMinutes(5 * (Bars - 1)), 0, [], false,
            0, 0, 0, At, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-06-16..2026-09-13", "http://127.0.0.1:0/candles", "",
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, At, KlineTimeUnit.Milliseconds, raw)])
        {
            CoverageTargetDays = 90,
            SourceCarriesVolume = false,
            MidpointBars = Midpoints
        };

        return record with { Id = new DatasetStore(db).Record(record) };
    }

    /// <summary>
    /// <c>data-bars</c> says it in the note, counts it in the window and in the dataset, and puts the
    /// quality on every bar it serves.
    /// </summary>
    [Fact]
    public async Task Data_bars_says_which_of_its_bars_carry_no_traded_volume()
    {
        var (_, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        GivenMidpointData(db);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1", Args = Args(("pair", Symbol))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var data = Data(reply);
        log.WriteLine(data.GetProperty("note").GetString());

        Assert.Equal(Midpoints, data.GetProperty("midpoint_bars_in_window").GetInt32());
        Assert.Equal(Midpoints, data.GetProperty("midpoint_bars_in_dataset").GetInt32());

        var note = data.GetProperty("note").GetString()!;
        Assert.Contains("MIDPOINT-DERIVED", note);
        Assert.Contains("never trade evidence", note);

        var bars = data.GetProperty("bars").EnumerateArray().ToList();
        Assert.Equal(Bars, bars.Count);
        Assert.Equal(Midpoints, bars.Count(b => b.GetProperty("quality").GetString() == BarQuality.MidpointDerived));
        Assert.Equal(Bars - Midpoints, bars.Count(b => b.GetProperty("quality").GetString() == BarQuality.Traded));
    }

    /// <summary>
    /// A BACKTEST OVER SUCH A DATASET DOES NOT REPORT LIKE A RUN OVER TRADED BARS — the same sentence,
    /// in the reply the agent reads.
    /// </summary>
    [Fact]
    public async Task A_backtest_over_midpoint_candles_says_they_are_not_trade_evidence()
    {
        var (_, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenMidpointData(db);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Backtest, Session = "agent-1",
            Args = Args(("strategy", GivenProgram()),
                ("dataset", set.Id.ToString(CultureInfo.InvariantCulture)),
                ("fees", "0.001"), ("slippage", "0.0005"), ("increment", "1"))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var note = Data(reply).GetProperty("note").GetString()!;
        log.WriteLine(note);

        Assert.Contains("MIDPOINT-DERIVED", note);
        Assert.Contains("never trade evidence", note);
        Assert.Contains($"{Midpoints:N0} of these {Bars:N0} bars", note);
    }

    /// <summary>
    /// SECTION 8 OF THE OWNER'S REPORT, which <c>trade report</c> serves to the AI as well, names them
    /// on the dataset's own line.
    /// </summary>
    [Fact]
    public async Task Section_eight_names_the_midpoint_bars_on_the_datasets_line()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        GivenMidpointData(db);

        var line = Assert.Single(gw.Reports.Compose(At.AddHours(12)).Research.AppMetrics,
            l => l.Contains(CandleSourceCatalog.RevolutXCandles, StringComparison.Ordinal));
        log.WriteLine(line);

        Assert.Contains($"{Midpoints} bars with NO traded volume", line);
        Assert.Contains("never trade evidence", line);
        Assert.Contains($"of {90} days deep", line);
    }

    /// <summary>
    /// AND THE FEED A RUN READS IN PROCESS SAYS IT TOO, at the one moment the dataset's verdict is
    /// taken.
    /// </summary>
    [Fact]
    public async Task The_feed_a_run_reads_says_which_of_its_bars_are_midpoint_derived()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = GivenMidpointData(db);

        var open = BarFeed.Open(gw.Datasets, set.Id, BarAudience.Pipe(CouncilRoles.Research), null, null);
        Assert.True(open.Ok, open.Why);

        Assert.Contains("MIDPOINT-DERIVED", open.Feed!.MidpointNote);
        Assert.Equal(Midpoints, open.Feed.Bars().Count(b => b.Quality == BarQuality.MidpointDerived));
    }

    /// <summary>
    /// AND A DATASET WHOSE BARS ARE ALL TRADED SAYS NOTHING, so the sentence above means something
    /// when it appears. A source that CAN publish a volume-less candle still says so.
    /// </summary>
    [Fact]
    public void A_dataset_with_no_midpoint_bars_carries_no_warning()
    {
        Assert.Null(BarQuality.Note(0, 120, sourceCarriesVolume: true));
        Assert.Contains("none of these bars is one", BarQuality.Note(0, 120, sourceCarriesVolume: false));
        Assert.Contains("MIDPOINT-DERIVED", BarQuality.Note(1, 120, sourceCarriesVolume: false));
    }
}
