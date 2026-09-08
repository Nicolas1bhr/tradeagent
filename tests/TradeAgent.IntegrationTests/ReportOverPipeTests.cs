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
/// U-report item 4. <c>trade report --json</c> as an agent actually reaches it: over the pipe,
/// through the gateway, with a real handshake.
///
/// <para>The unit tests hold the document. This holds what only the wire can settle: that a figure
/// the app WITHHELD arrives as <c>null</c> rather than as a missing key, that the op is a read the
/// handler table covers, and that there is no way from this channel to write one.</para>
/// </summary>
public class ReportOverPipeTests(ITestOutputHelper log)
{
    static string NewPipe() => "ta-report-" + Guid.NewGuid().ToString("n")[..12];

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
    public async Task The_report_crosses_the_pipe_with_every_unknown_as_a_null_and_a_named_gap()
    {
        var (gw, conn, db, client, server) = await Connected();
        using var _ = db;
        await using var _2 = server;
        await using var _3 = client;

        // Bought and sold at a profit, and the platform reported a fee on neither.
        await gw.PlaceAsync(new AgentContext("agent-1"), "rep-buy", TestEnv.Buy());
        conn.Broker.PriceOffset = 2m;
        await gw.PlaceAsync(new AgentContext("agent-1"), "rep-sell",
            TestEnv.Buy() with { Side = ConnectorSdk.OrderSide.Sell });

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Report, Session = "agent-1" });
        Assert.True(reply.Ok, Json.Write(reply.Error));
        log.WriteLine(Json.Write(reply.Data, pretty: true));

        var d = Data(reply);
        var p = d.GetProperty("performance");

        // AS `null`, NOT AS A MISSING KEY. The serializer drops nulls by default, so without the
        // declared reply type a withheld net reads as a field this build does not have.
        Assert.Contains("\"net\":null", Json.Write(reply.Data));
        Assert.Equal(JsonValueKind.Null, p.GetProperty("net").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("unrealized").ValueKind);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("exposure").ValueKind);
        Assert.Equal(2, p.GetProperty("fees_unknown_fills").GetInt32());

        // And every one of those is NAMED, in the owner's own words, in the same answer.
        var missing = d.GetProperty("missing").EnumerateArray()
            .Select(x => x.GetProperty("why").GetString()!).ToList();
        Assert.Contains(missing, s => s.Contains("did not report a fee"));
        Assert.Contains(missing, s => s.Contains("what is still OPEN is not valued"));

        // The document the owner reads is in the same answer, verbatim.
        Assert.Contains("net after costs: —", d.GetProperty("text").GetString());
        Assert.Contains("no AI turn was spent producing it", d.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, d.GetProperty("rejected").ValueKind);
    }

    [Fact]
    public async Task A_day_this_build_cannot_read_is_refused_and_never_answered_as_today()
    {
        var (_, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Report,
            Session = "agent-1",
            Args = new Dictionary<string, JsonElement>
            {
                ["day"] = JsonSerializer.SerializeToElement("last tuesday")
            }
        });

        Assert.False(reply.Ok);
        Assert.Equal(nameof(ErrorCode.INVALID_REQUEST), reply.Error!.Code);
        Assert.Contains("yyyy-MM-dd", reply.Error!.Message);
    }

    [Fact]
    public async Task A_named_day_answers_that_day_and_not_today()
    {
        var (_, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var day = DateTimeOffset.Now.AddDays(-3).ToLocalTime().ToString("yyyy-MM-dd");
        var reply = await client.SendAsync(new IpcRequest
        {
            Op = Ops.Report,
            Session = "agent-1",
            Args = new Dictionary<string, JsonElement>
            {
                ["day"] = JsonSerializer.SerializeToElement(day)
            }
        });

        Assert.True(reply.Ok, Json.Write(reply.Error));
        Assert.Equal(day, Data(reply).GetProperty("day").GetString());
    }

    [Fact]
    public void The_report_is_a_read_this_channel_cannot_write_and_the_drain_covers_it()
    {
        // NOT MUTATING: nothing on this channel writes, rewrites or deletes a report.
        Assert.False(Ops.IsMutating(Ops.Report));
        Assert.DoesNotContain(Ops.Report, Ops.Mutating);

        // AND IT IS IN THE HANDLER TABLE. A handler that is absent is one nobody notices growing a
        // connector call; this one is in it at zero, which is the claim that it makes none.
        Assert.Contains(Ops.Report, GatewaySchema.Ops().Select(o => o.Op));
    }

    [Fact]
    public async Task Every_handler_including_the_report_is_in_the_deadline_table()
    {
        var (gw, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var row = Assert.Single(((GatewayPipeServer)server).HandlerPaths, p => p.Handler == Ops.Report);
        Assert.Equal(TimeSpan.Zero, row.Path);
        Assert.Contains("in process", row.Why);
        _ = gw;
    }

    [Fact]
    public async Task The_surface_the_agent_discovers_says_the_report_cannot_be_written_from_here()
    {
        var (_, _, db, client, server) = await Connected();
        using var _1 = db;
        await using var _2 = server;
        await using var _3 = client;

        var reply = await client.SendAsync(new IpcRequest { Op = Ops.Schema, Session = "agent-1" });
        Assert.True(reply.Ok, Json.Write(reply.Error));
        var text = Json.Write(reply.Data);

        Assert.Contains("no operation that writes, rewrites or deletes a report", text);
        Assert.Contains("Nothing in it is inferred and no AI turn produces it", text);
    }
}
