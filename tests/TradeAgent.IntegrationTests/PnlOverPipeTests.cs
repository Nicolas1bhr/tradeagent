using System.Text.Json;
using TradeAgent.Core;
using TradeAgent.Core.Db;
using TradeAgent.Gateway;
using TradeAgent.Security;
using TradeAgent.TradeCli;
using Xunit;
using Xunit.Abstractions;

namespace TradeAgent.Tests.Integration;

/// <summary>
/// <c>trade pnl --json</c> as an agent actually reaches it: over the pipe, through the gateway, with
/// a real handshake, on fills the simulator really produced.
///
/// The unit tests hold the arithmetic. This holds the two things only the wire can settle: that the
/// answer is a JSON shape an agent can branch on, and that an UNKNOWN survives the trip as a null
/// with a sentence beside it rather than arriving as a zero.
/// </summary>
public class PnlOverPipeTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-pnl-" + Guid.NewGuid().ToString("n")[..12];

    static async Task<(TradingGateway Gw, Connectors.Fake.FakeConnector Conn, Database Db, PipeClient Client, IAsyncDisposable Server)> Connected()
    {
        var (gw, conn, db) = await TestEnv.Ready();
        var pipe = NewPipe();
        var server = new GatewayPipeServer(gw, IpcToken.Ensure(), pipe);
        server.Start();
        var client = new PipeClient();
        await client.ConnectAsync(10_000, pipe);
        return (gw, conn, db, client, server);
    }

    static JsonElement Data(IpcResponse r) => JsonSerializer.SerializeToElement(r.Data, Json.Options);

    [Fact]
    public async Task An_unreported_fee_crosses_the_pipe_as_a_null_net_and_a_sentence()
    {
        var (gw, conn, db, client, server) = await Connected();
        using var _ = db;
        await using var _2 = server;
        await using var _3 = client;

        // Bought and sold at a profit, and the platform reported a fee on neither.
        await gw.PlaceAsync(new AgentContext("agent-1"), "pnl-buy", TestEnv.Buy());
        conn.Broker.PriceOffset = 2m;
        await gw.PlaceAsync(new AgentContext("agent-1"), "pnl-sell",
            TestEnv.Buy() with { Side = ConnectorSdk.OrderSide.Sell });

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Pnl, Session = "agent-1" });
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var d = Data(reply);
        log.WriteLine(Json.Write(reply.Data, pretty: true));

        // AND IT ARRIVES AS `null`, NOT AS A MISSING KEY. The serializer drops nulls by default, so
        // an absent `net` was the first thing this test found: a reader asking for it got "no such
        // field" rather than "TradeAgent cannot compute this".
        Assert.Contains("\"net\":null", Json.Write(reply.Data));
        Assert.Equal(JsonValueKind.Null, d.GetProperty("net").ValueKind);
        Assert.Equal(JsonValueKind.Null, d.GetProperty("fees").ValueKind);
        Assert.Equal(2, d.GetProperty("fees_unknown_fills").GetInt32());
        Assert.Equal(2, d.GetProperty("fills").GetInt32());
        Assert.True(d.GetProperty("realized").GetDecimal() > 0m);
        var incomplete = d.GetProperty("incomplete").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Contains(incomplete, s => s.Contains("did not report a fee"));
        Assert.Equal("today", d.GetProperty("window").GetString());
    }

    [Fact]
    public async Task Fees_the_platform_does_report_produce_a_net_and_no_word_about_fees()
    {
        var (gw, conn, db, client, server) = await Connected();
        using var _ = db;
        await using var _2 = server;
        await using var _3 = client;

        conn.Broker.FeePerContract = 1.50m;
        await gw.PlaceAsync(new AgentContext("agent-1"), "pnl-fee-buy", TestEnv.Buy());
        conn.Broker.PriceOffset = 2m;
        await gw.PlaceAsync(new AgentContext("agent-1"), "pnl-fee-sell",
            TestEnv.Buy() with { Side = ConnectorSdk.OrderSide.Sell });

        var d = Data(await client.SendAsync(new IpcRequest { Op = Ops.Pnl, Session = "agent-1" }));
        log.WriteLine(Json.Write(JsonSerializer.Deserialize<object>(d.GetRawText()), pretty: true));

        Assert.Equal(3.00m, d.GetProperty("fees").GetDecimal());
        Assert.Equal(0, d.GetProperty("fees_unknown_fills").GetInt32());
        var net = d.GetProperty("net").GetDecimal();
        Assert.Equal(d.GetProperty("realized").GetDecimal() - 3.00m, net);

        // Nothing is said about fees, because none is missing. The one line that IS there is the
        // ledger's own age: this installation started watching part-way through the day the window
        // covers, and saying so is the point of `incomplete` rather than an exception to it.
        var incomplete = d.GetProperty("incomplete").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.DoesNotContain(incomplete, s => s.Contains("fee"));
        Assert.Contains(incomplete, s => s.Contains("has been recording your fills since"));
    }

    [Fact]
    public async Task A_since_this_build_cannot_read_is_refused_rather_than_read_as_today()
    {
        var (_, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var bad = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Pnl, Session = "agent-1",
            Args = GatewayThroughPipeTests.Args(("since", "last Tuesday"))
        });

        Assert.False(bad.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), bad.Error!.Code);
        log.WriteLine($"since='last Tuesday' : {bad.Error.Message}");

        var good = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Pnl, Session = "agent-1",
            Args = GatewayThroughPipeTests.Args(("since", "2026-09-01"))
        });
        Assert.True(good.Ok, Json.Write(good.Error));
        Assert.Equal("since", Data(good).GetProperty("window").GetString());
    }

    /// <summary>
    /// The op is in the schema an agent discovers at runtime, and in the handler table the shutdown
    /// drain is derived from — a handler that is absent from that table is one nobody notices growing
    /// a connector call.
    /// </summary>
    [Fact]
    public async Task Pnl_is_described_in_the_schema_and_covered_by_the_drain()
    {
        var (_, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var schema = Json.Write((await client.SendAsync(new IpcRequest { Op = Ops.Schema, Session = "agent-1" })).Data);
        Assert.Contains("\"op\":\"pnl\"", schema);
        Assert.Contains("never a zero", schema);
        // And the ledger it is computed from describes itself, including what it does not cover.
        Assert.Contains("one row per fill", schema);
        Assert.Contains("coverage begins when TradeAgent first read your platform's executions", schema);

        var paths = ((GatewayPipeServer)server).HandlerPaths;
        Assert.Contains(paths, p => p.Handler == Ops.Pnl);
        Assert.All(GatewaySchema.Ops(), o => Assert.Contains(paths, p => p.Handler == o.Op));
    }
}
