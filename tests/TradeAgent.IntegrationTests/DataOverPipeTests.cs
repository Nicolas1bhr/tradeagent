using System.Text;
using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Data;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// <c>trade data list</c> and <c>trade data bars</c> as an agent actually reaches them: over the
/// pipe, through the gateway, on a dataset the ledger really recorded.
///
/// The unit tests hold the normaliser's arithmetic and the schema's wording. This holds the three
/// things only the wire settles: that the bar CAP refuses instead of truncating and says what it is,
/// that a dataset whose bytes changed under it serves nothing, and that an UNKNOWN crosses as a null
/// rather than as a missing key.
/// </summary>
public class DataOverPipeTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-data-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, Database Db, PipeClient Client, IAsyncDisposable Server)> Connected()
    {
        var (gw, _, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        return (gw, db, client, server);
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Writes a normalised dataset file with <paramref name="bars"/> minutes in it and records it,
    /// exactly as the app's own collector would — hashes and all, so the ledger's verification has
    /// something true to check.
    /// </summary>
    static DatasetRecord Given(Database db, string pair, int bars, int skipAfter = -1)
    {
        var dir = BinanceArchive.DatasetDir(pair);
        Directory.CreateDirectory(dir);

        // A stand-in for the vendor's zip: the ledger hashes whatever file its row names, so any
        // bytes will do as long as the row records the hash they really have.
        var raw = Path.Combine(dir, "raw", $"{pair}-1m-2026-08.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        File.WriteAllText(raw, "stand-in for the vendor's monthly archive");

        var csv = Path.Combine(dir, "v1.csv");
        var b = new StringBuilder().Append(KlineNormaliser.Header).Append('\n');
        var written = 0;
        for (var i = 0; i < bars; i++)
        {
            if (i == skipAfter + 1) continue;
            b.Append($"{Start.AddMinutes(i).UtcDateTime:yyyy-MM-ddTHH:mm:ssZ},100.00,101.00,99.00,100.50,1.00\n");
            written++;
        }
        File.WriteAllText(csv, b.ToString());

        var store = new DatasetStore(db);
        var record = new DatasetRecord(
            0, BinanceArchive.Source, pair, BinanceArchive.Interval, "v1", 12, 1, ["2025-09"],
            csv, DatasetStore.Sha256(csv)!, written, Start, Start.AddMinutes(bars - 1),
            skipAfter < 0 ? 0 : 1,
            skipAfter < 0 ? [] : [new GapRun(Start.AddMinutes(skipAfter + 1), Start.AddMinutes(skipAfter + 1), 1)],
            false, 0, 2, 0, DateTimeOffset.UtcNow, DatasetState.ACCEPTED, null,
            [new DatasetFile("2026-08", "http://127.0.0.1:0/data/spot/monthly/klines/x.zip", DatasetStore.Sha256(raw)!,
                DatasetStore.Sha256(raw)!, new FileInfo(raw).Length, DateTimeOffset.UtcNow,
                KlineTimeUnit.Microseconds, raw)]);

        return record with { Id = store.Record(record) };
    }

    [Fact]
    public async Task A_window_bigger_than_the_cap_is_refused_and_the_refusal_names_the_cap()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        Given(db, "BTCUSDT", 20_000);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1",
            Args = Args(("pair", "BTCUSDT"))
        });

        log.WriteLine(Json.Write(reply.Error));
        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("10000", reply.Error.Message);
        Assert.Contains("--from", reply.Error.Message);
    }

    [Fact]
    public async Task A_window_inside_the_cap_comes_back_with_its_bars_and_what_they_are_not()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        Given(db, "BTCUSDT", 200, skipAfter: 9);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1",
            Args = Args(("pair", "btcusdt"), ("from", "2026-08-01"), ("to", "2026-08-01T00:04:00Z"))
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        var d = Data(reply);
        log.WriteLine(Json.Write(reply.Data, pretty: true));

        Assert.Equal(5, d.GetProperty("count").GetInt32());
        Assert.Equal(5, d.GetProperty("bars").GetArrayLength());
        Assert.Equal("BTCUSDT", d.GetProperty("pair").GetString());
        Assert.Equal("1m", d.GetProperty("interval").GetString());
        Assert.Equal(1, d.GetProperty("gaps_in_dataset").GetInt32());
        Assert.Equal(2, d.GetProperty("incomplete_excluded").GetInt32());
        Assert.Contains("hypothesis evidence", d.GetProperty("note").GetString());
        Assert.Equal("2026-08-01T00:00:00+00:00", d.GetProperty("bars")[0].GetProperty("open_time").GetString());
    }

    [Fact]
    public async Task An_unreadable_date_is_refused_rather_than_read_as_something_else()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        Given(db, "BTCUSDT", 10);

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1",
            Args = Args(("pair", "BTCUSDT"), ("from", "last tuesday"))
        });

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("not a date this build can read", reply.Error.Message);
    }

    [Fact]
    public async Task A_dataset_whose_raw_file_changed_serves_no_bars_and_lists_as_rejected()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        var set = Given(db, "ETHUSDT", 10);

        File.WriteAllText(set.Files[0].Path, "not what the ledger measured");

        var bars = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1", Args = Args(("pair", "ETHUSDT"))
        });
        Assert.False(bars.Ok);
        Assert.Equal(nameof(ErrorCode.MARKET_DATA_UNAVAILABLE), bars.Error!.Code);
        Assert.Contains("REJECTED", bars.Error.Message);

        var list = await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "agent-1" });
        Assert.True(list.Ok, Json.Write(list.Error));
        var d = Data(list);
        var row = d.GetProperty("datasets")[0];
        Assert.Equal("REJECTED", row.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, row.GetProperty("rejected_reason").ValueKind);
    }

    [Fact]
    public async Task The_listing_carries_the_provenance_and_a_null_arrives_as_null()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;
        Given(db, "BTCUSDT", 10);

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.DataList, Session = "agent-1" });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        log.WriteLine(Json.Write(reply.Data, pretty: true));

        // The absent reason arrives as `null`, not as a missing key — the `pnl` lesson.
        Assert.Contains("\"rejected_reason\":null", Json.Write(reply.Data));

        var row = Data(reply).GetProperty("datasets")[0];
        Assert.Equal("ACCEPTED", row.GetProperty("state").GetString());
        Assert.Equal(12, row.GetProperty("months_attempted").GetInt32());
        Assert.Equal(1, row.GetProperty("months_present").GetInt32());
        Assert.Equal("2025-09", row.GetProperty("months_not_published")[0].GetString());
        var file = row.GetProperty("files")[0];
        Assert.Equal("2026-08", file.GetProperty("month").GetString());
        Assert.Equal(64, file.GetProperty("published_sha256").GetString()!.Length);
        Assert.Equal(64, file.GetProperty("computed_sha256").GetString()!.Length);
        Assert.Equal("Microseconds", file.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task A_pair_with_no_data_is_told_who_collects_it_and_it_is_not_this_channel()
    {
        var (gw, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.DataBars, Session = "agent-1", Args = Args(("pair", "SOLUSDT"))
        });

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.MARKET_DATA_UNAVAILABLE), reply.Error!.Code);
        Assert.Contains("no command here that does it", reply.Error.Message);
    }

    static Dictionary<string, JsonElement> Args(params (string Key, string Value)[] args) =>
        args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value));
}
